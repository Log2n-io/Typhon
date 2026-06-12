using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System.IO;

namespace Typhon.Engine.Tests;

/// <summary>
/// The "One True Crash Test" (P0.3 / AC-3) — the program's north star. An entity is committed with <see cref="DurabilityMode.Immediate"/> (its records fsynced to
/// the WAL), then the engine is hard-crashed via <see cref="DatabaseEngine.SimulateHardCrash"/> (a power cut: the managed page cache is discarded with no checkpoint
/// and no <c>PersistEngineState</c>, so the committed data exists ONLY in the WAL). On reopen the entity must be recovered via WAL replay.
/// </summary>
/// <remarks>
/// This test is <b>expected RED until #395 (P1.2 RecoveryDriver)</b>. Production reopen currently calls <c>WalRecovery.Recover(..., dbe: null)</c>, which skips the
/// replay phase entirely (TXW-1) — so Immediate-committed data is silently lost across a crash. It is quarantined under <c>KnownIssue-395</c> so it runs and stays
/// visible without failing the gating suite, exactly per design 08 §2 (A0.3): "exists, runs, and is red."
/// </remarks>
[TestFixture]
[Category("KnownIssue-395")]
internal sealed class TrueCrashE2ETests
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
            const string prefix = "Tct_";
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
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(TrueCrashE2ETests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b =>
            {
                b.AddSimpleConsole();
                b.SetMinimumLevel(LogLevel.Warning);
            })
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

        var testRoot = Directory.GetParent(_dbDir)?.FullName; // the per-test "<root>/Tct_<name>" dir (parent of /db and /wal)
        try
        {
            if (testRoot != null && Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void ImmediateCommit_SurvivesHardCrash()
    {
        const int count = 10;
        var entityIds = new EntityId[count];

        // Phase 1: commit entities with Immediate durability (each fsynced to the WAL), then hard-crash without persisting the data file.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using (var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate))
            {
                for (int i = 0; i < count; i++)
                {
                    using var tx = uow.CreateTransaction();
                    var comp = new CompA(i + 1, i, i);
                    entityIds[i] = tx.Spawn<CompAArch>(CompAArch.A.Set(in comp));
                    tx.Commit();
                }

                uow.Flush();
            }

            // Power cut: discard the managed page cache (uncheckpointed dirty pages) with no PersistEngineState / clean-shutdown marker. The committed entities now
            // live ONLY in the fsynced WAL — survival depends entirely on WAL replay at reopen.
            dbe.SimulateHardCrash();
        }

        // Phase 2: reopen the same directory and require every committed entity to be recovered.
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CompA>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            for (int i = 0; i < count; i++)
            {
                Assert.That(tx.IsAlive(entityIds[i]), Is.True,
                    $"Immediate-committed entity {i} must survive a hard crash via WAL replay (RED until #395/P1.2 RecoveryDriver wires recovery — TXW-1)");
            }
        }
    }
}
