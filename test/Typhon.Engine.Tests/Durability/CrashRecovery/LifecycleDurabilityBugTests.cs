using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// Minimal repros for two recovery/durability bugs the crash sweep surfaced via <c>LifecycleChurn</c>:
/// Bug 1 — a disabled Versioned component reads back ENABLED after reopen (durability hole for enabled-bits);
/// Bug 2 — a post-spawn Versioned value UPDATE reverts to the spawn value after a hard crash (crash-rebuild chain-head resolution).
/// Each repro toggles exactly one variable (checkpoint? crash?) to localize the fault.
/// #847 — a replayed enabled-state change reaches the EntityMap record but not the cluster SoA copy.
/// </summary>
[TestFixture]
internal sealed class LifecycleDurabilityBugTests
{
    private string _dbDir;
    private string _walDir;
    private ServiceProvider _serviceProvider;

    private static string CurrentDatabaseName
    {
        get
        {
            var name = TestContext.CurrentContext.Test.Name;
            foreach (var c in new[] { '(', ')', ',', ' ', '"' })
            {
                name = name.Replace(c, '_');
            }

            const int max = 63;
            const string prefix = "Bug_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }

            return prefix + name;
        }
    }

    [SetUp]
    public void Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(LifecycleDurabilityBugTests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning))
            .AddResourceRegistry()
            .AddMemoryAllocator()
            .AddEpochManager()
            .AddHighResolutionSharedTimer()
            .AddDeadlineWatchdog()
            .AddScopedManagedPagedMemoryMappedFile(opts =>
            {
                opts.DatabaseName = CurrentDatabaseName;
                opts.DatabaseDirectory = _dbDir;
                opts.DatabaseCacheSize = (ulong)PagedMMF.MinimumCacheSize * 4;
            })
            .AddScopedDatabaseEngine(opts =>
            {
                opts.Wal = new WalWriterOptions
                {
                    WalDirectory = _walDir,
                    GroupCommitIntervalMs = 5,
                    UseFUA = false,
                    SegmentSize = 4 * 1024 * 1024,
                    PreAllocateSegments = 1,
                };
            });

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        var testRoot = Directory.GetParent(_dbDir)?.FullName;
        try
        {
            if (testRoot != null && Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static void Register(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<CompA>();
        dbe.RegisterComponentFromAccessor<CompB>();
    }

    // ── Bug 1: enabled-bits durability ──────────────────────────────────────

    [Test]
    [CancelAfter(15_000)]
    public void Bug1_Disable_CleanReopen_NoCheckpoint([Values] bool checkpoint)
    {
        EntityId id;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using (var tx = uow.CreateTransaction())
                {
                    id = tx.Spawn<CompABArch>(CompABArch.A.Set(new CompA(1, 1, 1)), CompABArch.B.Set(new CompB(2, 2)));
                    tx.Commit();
                }

                using (var tx = uow.CreateTransaction())
                {
                    tx.OpenMut(id).Disable(CompABArch.B);
                    tx.Commit();
                }

                uow.Flush();
            }

            if (checkpoint)
            {
                Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint must cover what was written");
            }

            // clean dispose (no crash)
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            var enabled = tx.Open(id).IsEnabled(1);
            Assert.That(enabled, Is.False, "CompB was disabled before a clean shutdown — it must read back disabled after reopen");
        }
    }

    // ── Bug 2: hard-crash chain-head revert ─────────────────────────────────

    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("RB-05")]
    public void Bug2_Update_Reopen_WithCheckpoint([Values] bool hardCrash)
    {
        EntityId id;
        var updated = new CompA(777, 7.5f, 7.25);

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using (var tx = uow.CreateTransaction())
                {
                    id = tx.Spawn<CompABArch>(CompABArch.A.Set(new CompA(1, 1, 1)), CompABArch.B.Set(new CompB(2, 2)));
                    tx.Commit();
                }

                using (var tx = uow.CreateTransaction())
                {
                    ref var w = ref tx.OpenMut(id).Write(CompABArch.A);
                    w = updated;
                    tx.Commit();
                }

                uow.Flush();
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint must cover what was written");

            if (hardCrash)
            {
                dbe.SimulateHardCrash();
            }
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            dbe.InitializeArchetypes();
            using var tx = dbe.CreateQuickTransaction();
            var a = tx.Open(id).Read(CompABArch.A).A;
            Assert.That(a, Is.EqualTo(777), "the post-spawn CompA update must survive reopen");
        }
    }

    // ── #847: SetEnabledBits replay reaches the cluster SoA ─────────────────

    /// <summary>
    /// An enabled-state change to a checkpointed entity, recovered from the WAL after a hard crash, reaches both copies of the state (#847).
    /// </summary>
    /// <remarks>
    /// The checkpoint puts the spawn below the replay window, so the change replays through <c>ApplySetEnabledBitsToExisting</c> instead of being folded
    /// into a spawn. <paramref name="supplyMidLife"/> covers both directions: supplying a Versioned component the spawn omitted — the #845 path, and the
    /// only way such a component becomes visible after a crash — must set the SoA bit, and disabling a spawned one must clear it. A point read passed
    /// before the fix; the SoA copy is what bulk iteration and the next crash rebuild read.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void Issue847_EnabledChange_RecoveredFromWal_ReachesTheClusterSoA([Values] bool supplyMidLife)
    {
        EntityId id;
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            dbe.InitializeArchetypes();
            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using (var tx = uow.CreateTransaction())
                {
                    id = supplyMidLife
                        ? tx.Spawn<CompABArch>(CompABArch.A.Set(new CompA(1, 1, 1)))
                        : tx.Spawn<CompABArch>(CompABArch.A.Set(new CompA(1, 1, 1)), CompABArch.B.Set(new CompB(2, 2)));
                    tx.Commit();
                }

                uow.Flush();
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the spawn must be below the replay window");

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                using (var tx = uow.CreateTransaction())
                {
                    var entity = tx.OpenMut(id);
                    if (supplyMidLife)
                    {
                        entity.Enable(CompABArch.B, new CompB(3, 3));
                    }
                    else
                    {
                        entity.Disable(CompABArch.B);
                    }

                    tx.Commit();
                }

                uow.Flush();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Register(dbe);
            dbe.InitializeArchetypes();

            var meta = Archetype<CompABArch>.Metadata;
            var aSlot = meta.GetSlot(CompABArch.A._componentTypeId);
            var bSlot = meta.GetSlot(CompABArch.B._componentTypeId);
            using (var tx = dbe.CreateQuickTransaction())
            {
                Assert.That(tx.Open(id).IsEnabled(CompABArch.B), Is.EqualTo(supplyMidLife), "the EntityMap record must hold the recovered state");
            }

            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, id, bSlot), Is.EqualTo(supplyMidLife),
                "the cluster SoA must agree with the record — bulk iteration and the crash rebuild read this copy, not the record");
            Assert.That(ClusterSoAProbe.IsEnabled(dbe, meta.ArchetypeId, id, aSlot), Is.True,
                "a component the change did not touch must stay enabled in the SoA");
        }
    }
}
