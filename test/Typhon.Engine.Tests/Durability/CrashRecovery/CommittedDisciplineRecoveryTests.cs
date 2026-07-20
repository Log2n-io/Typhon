using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Crash-recovery proof for the Committed durability discipline (issue #392, AC-2 / AC-7). A SingleVersion-layout component written under
/// <see cref="DurabilityDiscipline.Commit"/> with <see cref="DurabilityMode.Immediate"/> is fsynced to the WAL as an ordinary Slot record (Committed
/// flag = telemetry only). After a hard crash (managed page cache discarded, no checkpoint), reopen must replay the record through the same
/// <c>RecoveryDriver</c> path tick-fence and Versioned records use — last-writer-wins by LSN — and restore the exact committed value (AC-7: zero
/// Committed-specific recovery code). Uses the <see cref="CmEntity"/> cluster archetype from <c>CommittedDisciplineTests</c>.
/// </summary>
[TestFixture]
internal sealed class CommittedDisciplineRecoveryTests
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
            const string prefix = "Cdr_";
            if (prefix.Length + name.Length > max)
            {
                name = name[^(max - prefix.Length)..];
            }
            return prefix + name;
        }
    }

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    [SetUp]
    public void Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "Typhon.Tests", nameof(CommittedDisciplineRecoveryTests));
        _dbDir = Path.Combine(root, CurrentDatabaseName, "db");
        _walDir = Path.Combine(root, CurrentDatabaseName, "wal");
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_walDir);

        var services = new ServiceCollection();
        services
            .AddLogging(b => b.SetMinimumLevel(LogLevel.Warning))
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
            // best-effort cleanup
        }
    }

    [Test]
    [CancelAfter(15_000)]
    public void CommitDiscipline_Write_SurvivesHardCrash()
    {
        EntityId id;

        // Phase 1: spawn (Immediate), then overwrite Position under Commit discipline (Immediate ⇒ fsynced), then hard-crash with no checkpoint.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmWallet>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                id = tx.Spawn<CmEntity>(CmEntity.Position.Set(new CmPosition(1, 1)), CmEntity.Wallet.Set(new CmWallet(50)));
                tx.Commit();
            }

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit))
            {
                tx.OpenMut(id).Write(CmEntity.Position) = new CmPosition(99, 88);
                tx.Commit();
            }

            // Power cut: managed page cache discarded, no checkpoint / clean-shutdown marker. The committed value lives ONLY in the fsynced WAL.
            dbe.SimulateHardCrash();
        }

        // Phase 2: reopen the same directory — WAL replay must restore the Commit-discipline value (last-writer-wins over the spawn value).
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmWallet>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.IsAlive(id), Is.True, "Commit-discipline entity must survive a hard crash via WAL replay");

            var e = tx.Open(id);
            ref readonly var pos = ref e.Read(CmEntity.Position);
            // Position was written under Commit discipline (Immediate) ⇒ a fsynced WAL Slot record ⇒ recovered exactly (AC-2).
            Assert.That(pos.X, Is.EqualTo(99f), "Commit-discipline write must be recovered (AC-2) — got the pre-write value, durability lost");
            Assert.That(pos.Y, Is.EqualTo(88f), "Commit-discipline write Y must be recovered");
            // NB: the spawn-init Wallet value (SingleVersion, TickFence) is NOT asserted — without a checkpoint/tick fence it is not WAL-durable
            // (≤1-tick-loss by design). Only the Commit-discipline write carries the zero-loss guarantee under test here.
        }
    }

    /// <summary>
    /// MixedDiscipline crash sweep (issue #392, AC-10 workload from 08 §T-6): a single session interleaves TickFence (default) and Commit-discipline
    /// transactions across distinct entities, then hard-crashes with no checkpoint. The contract under test is that the Commit-discipline writes are
    /// recovered EXACTLY (zero-loss, last-writer-wins by LSN) regardless of the interleaved TickFence churn — i.e. mixing disciplines never weakens the
    /// Commit guarantee. The TickFence-only entity is intentionally NOT asserted: without a checkpoint/fence its writes are ≤1-tick-loss by design.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void MixedDiscipline_CommitWritesSurviveCrash_AmidTickFenceChurn()
    {
        EntityId e1, e2;

        // Phase 1: spawn two entities, then alternate TickFence (on e2) and Commit (on e1) transactions, ending on a Commit. Hard-crash, no checkpoint.
        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmWallet>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                e1 = tx.Spawn<CmEntity>(CmEntity.Position.Set(new CmPosition(1, 1)), CmEntity.Wallet.Set(new CmWallet(10)));
                e2 = tx.Spawn<CmEntity>(CmEntity.Position.Set(new CmPosition(2, 2)), CmEntity.Wallet.Set(new CmWallet(20)));
                tx.Commit();
            }

            // TickFence churn on e2 (interleaved "noise" — may be lost across the crash).
            using (var tf = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tf.OpenMut(e2).Write(CmEntity.Position) = new CmPosition(222, 222);
                tf.Commit();
            }

            // Commit-discipline write on e1 (zero-loss).
            using (var cm = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit))
            {
                cm.OpenMut(e1).Write(CmEntity.Position) = new CmPosition(11, 11);
                cm.Commit();
            }

            // More TickFence churn on e2.
            using (var tf = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tf.OpenMut(e2).Write(CmEntity.Position) = new CmPosition(444, 444);
                tf.Commit();
            }

            // Final Commit-discipline transaction on e1 — overwrites Position (last writer) and sets Wallet (CmWallet is DefaultDiscipline=Commit).
            using (var cm = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit))
            {
                var e = cm.OpenMut(e1);
                e.Write(CmEntity.Position) = new CmPosition(33, 33);
                e.Write(CmEntity.Wallet) = new CmWallet(777);
                cm.Commit();
            }

            dbe.SimulateHardCrash();
        }

        // Phase 2: reopen — the Commit-discipline writes on e1 must be recovered exactly; the TickFence-only e2 is not asserted.
        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmWallet>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.IsAlive(e1), Is.True, "e1 must survive — its Commit-discipline writes were fsynced (mixed-discipline zero-loss)");

            var e = tx.Open(e1);
            // Last Commit-discipline write to e1 wins on recovery (LSN last-writer-wins), despite the interleaved TickFence churn on e2.
            Assert.That(
                e.Read(CmEntity.Position).X,
                Is.EqualTo(33f),
                "last Commit write to e1.Position lost across the crash (mixed-discipline zero-loss violated)");
            Assert.That(e.Read(CmEntity.Position).Y, Is.EqualTo(33f));
            Assert.That(e.Read(CmEntity.Wallet).Gold, Is.EqualTo(777L), "Commit-discipline Wallet write lost across the crash");
        }
    }

    /// <summary>
    /// #395 capstone — an INDEXED cluster archetype spawned under Commit discipline survives a CONSOLIDATING checkpoint + hard crash, with both its
    /// values and its secondary index intact. This is the cell that exercises all three durability fixes composing at once: CK-10 (the checkpoint
    /// persists the cluster/EntityMap segment SPIs so the consolidated base is reachable on reopen), Face B / CM-06 (the Commit-discipline spawn
    /// WAL-logs its SV values), and RB-01 (the secondary B+Tree is never trusted post-crash — rebuilt from the recovered cluster data). The earlier
    /// sweep cells covered non-indexed cluster (MixedDiscipline) and the no-checkpoint indexed path (CmIdxEntity unit tests); this closes the indexed ×
    /// consolidation × crash gap.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void CommitDiscipline_IndexedClusterSpawn_SurvivesConsolidatingCheckpointCrash()
    {
        EntityId id1, id2;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmTeam>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, DurabilityDiscipline.Commit))
            {
                id1 = tx.Spawn<CmIdxEntity>(CmIdxEntity.Position.Set(new CmPosition(1, 1)), CmIdxEntity.Team.Set(new CmTeam { TeamId = 7, Rank = 1 }));
                id2 = tx.Spawn<CmIdxEntity>(CmIdxEntity.Position.Set(new CmPosition(2, 2)), CmIdxEntity.Team.Set(new CmTeam { TeamId = 9, Rank = 2 }));
                tx.Commit();
            }

            // Consolidate the Commit-discipline spawns into the data file (CheckpointLSN advances past their LSNs), then hard-crash with an empty WAL window —
            // recovery must restore the cluster SV state + rebuild the index from the persisted base alone.
            dbe.ForceCheckpoint();
            dbe.CheckpointManager.WaitForCheckpoint(TimeSpan.FromSeconds(10));
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmTeam>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();

            // Values: the Commit-discipline spawn values survive consolidation + crash (Face B / CM-06 + CK-10).
            Assert.That(tx.IsAlive(id1), Is.True, "indexed cluster entity lost through a consolidating checkpoint + crash (CK-10)");
            Assert.That(tx.Open(id1).Read(CmIdxEntity.Team).TeamId, Is.EqualTo(7), "Commit-discipline spawn value lost through consolidation (Face B)");
            Assert.That(tx.Open(id2).Read(CmIdxEntity.Team).TeamId, Is.EqualTo(9));

            // Index: the secondary B+Tree, never trusted post-crash, is rebuilt from the recovered cluster data and is exact (RB-01).
            Assert.That(
                tx.Query<CmIdxEntity>().WhereField<CmTeam>(t => t.TeamId == 7).Count(),
                Is.EqualTo(1),
                "secondary index not rebuilt/exact after recovery (RB-01)");
            Assert.That(tx.Query<CmIdxEntity>().WhereField<CmTeam>(t => t.TeamId == 9).Count(), Is.EqualTo(1));
            Assert.That(tx.Query<CmIdxEntity>().WhereField<CmTeam>(t => t.TeamId == 1).Count(), Is.EqualTo(0), "phantom index entry after recovery");
        }
    }
}
