using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// Crash-recovery proof for the Committed discipline (issue #392, AC-2 / AC-7). A SingleVersion-layout component written under
/// <see cref="CommitDiscipline.Commit"/> with <see cref="DurabilityMode.Immediate"/> is fsynced to the WAL as an ordinary Slot record (Committed
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

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
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

    /// <summary>What the Commit-discipline commit held between its append and its publish does.</summary>
    public enum PausedOp { Write, Destroy }

    /// <summary>Where the commit is held: right after its append, or just before its staged writes are published (a write's last page
    /// effect).</summary>
    public enum HoldPoint { AfterAppend, BeforeStagedPublish }

    /// <summary>Distinctive substring of the CK-13 verifier's rejection messages, which its mutant must trip.</summary>
    private const string Ck13Marker = "CK-13 violated";

    /// <summary>
    /// NEW-CK-1 (2026-07-06 assessment), for the Commit discipline: its staged writes reach page memory only at publish (CM-01), so a commit held
    /// between its append and its publish has not written them yet. A checkpoint that runs then must still keep CheckpointLSN below the commit's
    /// record, or recovery skips the record and the change is lost after a crash. Holding the commit just before its staged writes are published
    /// also pins the other end of the window: the commit must not withdraw its floor before its last page effect.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [VerifiesRule("CK-13")]
    public void CommitDiscipline_ACheckpointDuringAPublish_KeepsTheCommitInTheRecoveryWindow([Values] PausedOp op, [Values] HoldPoint at) =>
        PausedCommitScenario(op, at, withoutFloor: false);

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: with the checkpoint's in-flight floor unwired, the cycle passes the held commit's record,
    /// which the verifier must reject.
    /// </summary>
    [Test]
    [CancelAfter(30_000)]
    [RuleMutant("CK-13")]
    public void CommitDiscipline_ACheckpointThatIgnoresTheFloor_IsRejected([Values] PausedOp op) =>
        RuleMutants.AssertDetects("CK-13", Ck13Marker, () => PausedCommitScenario(op, HoldPoint.AfterAppend, withoutFloor: true));

    private void PausedCommitScenario(PausedOp op, HoldPoint at, bool withoutFloor)
    {
        EntityId id;
        EntityId sibling;
        string cycleReport;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmWallet>();
            dbe.InitializeArchetypes();
            if (withoutFloor)
            {
                dbe.CheckpointManager.InFlightCommitFloor = null;
            }

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                id = tx.Spawn<CmEntity>(CmEntity.Position.Set(new CmPosition(1, 1)), CmEntity.Wallet.Set(new CmWallet(50)));
                sibling = tx.Spawn<CmEntity>(CmEntity.Position.Set(new CmPosition(5, 5)), CmEntity.Wallet.Set(new CmWallet(7)));
                tx.Commit();
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(5)), Is.True, "the base entities must be checkpointed first");

            // Hold the commit inside its publish window. Only the committer's thread is held; nothing else commits here, so the guard is a defence.
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var committerThread = 0;
            Action hold = () =>
            {
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref committerThread))
                {
                    held.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            };
            if (at == HoldPoint.AfterAppend)
            {
                dbe.CommitAfterAppendProbe = hold;
            }
            else
            {
                dbe.CommitBeforeStagedPublishProbe = hold;
            }

            var committer = Task.Run(() =>
            {
                Volatile.Write(ref committerThread, Environment.CurrentManagedThreadId);
                using var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit);
                if (op == PausedOp.Write)
                {
                    tx.OpenMut(id).Write(CmEntity.Position) = new CmPosition(99, 88);
                }
                else
                {
                    tx.Destroy(id);
                }
                tx.Commit();
            });
            bool committed;
            try
            {
                Assert.That(held.Wait(TimeSpan.FromSeconds(5)), Is.True, "the commit never reached its hold point");
                var opLsn = dbe.DurabilityLog.LastAppendedLsn;

                // The held commit's staged write is not in a page yet (CM-01), so the cycle covers everything it collects and the gate cannot hold
                // the watermark back: only the floor can.
                var cm = dbe.CheckpointManager;
                var before = cm.TotalCheckpoints;
                var covered = cm.ForceCheckpointAndWait(TimeSpan.FromSeconds(2));
                cycleReport = $"held {at}: covered={covered}, cycles {before}->{cm.TotalCheckpoints}, gated={cm.ConsecutiveGatedCycles}, "
                    + $"CheckpointLSN={cm.CheckpointLsn}, the commit's record={opLsn}";
                Assert.That(covered, Is.True, $"the cycle must cover what it collected, so that the floor is what gets tested ({cycleReport})");
                Assert.That(cm.CheckpointLsn, Is.LessThan(opLsn),
                    $"{Ck13Marker}: CheckpointLSN passed the record of a commit that has not finished publishing ({cycleReport})");
            }
            finally
            {
                dbe.CommitAfterAppendProbe = null;
                dbe.CommitBeforeStagedPublishProbe = null;
                release.Set();
                committed = committer.Wait(TimeSpan.FromSeconds(10));
            }

            // Tearing the engine down under a live commit would free memory it is still using.
            Assert.That(committed, Is.True, "the held commit must finish before the crash");
            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmWallet>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            if (op == PausedOp.Write)
            {
                Assert.That(tx.Open(id).Read(CmEntity.Position).X, Is.EqualTo(99f), $"{Ck13Marker}: the Commit-discipline write was lost ({cycleReport})");
            }
            else
            {
                Assert.That(tx.IsAlive(id), Is.False, $"{Ck13Marker}: the destroyed entity came back ({cycleReport})");
            }

            Assert.That(tx.IsAlive(sibling) && tx.Open(sibling).Read(CmEntity.Position).X == 5f, Is.True,
                "the untouched entity must come through the crash unchanged");
        }
    }

    /// <summary>
    /// CK-13: a commit whose append fails after claiming its LSNs stored its floor before the frame's publish, and withdraws it when the commit
    /// throws. While the failed transaction is still alive the checkpoint must read no floor; one left behind would hold the watermark below the
    /// abandoned claim until the transaction is disposed. A checkpoint cannot show that: while the failed transaction is alive every cycle skips a page
    /// (measured: all 20 cycles of a 5 s wait gated on one page), so the test asserts the floor the checkpoint reads. The last step is not CK-13: it
    /// checks that CheckpointLSN passes the abandoned claim once a later commit's frame has drained.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    [VerifiesRule("CK-13")]
    public void CommitDiscipline_AnAppendThatFailsAfterItsClaim_WithdrawsItsFloor()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CmPosition>();
        dbe.RegisterComponentFromAccessor<CmWallet>();
        dbe.InitializeArchetypes();

        EntityId id;
        using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
        {
            id = tx.Spawn<CmEntity>(CmEntity.Position.Set(new CmPosition(1, 1)), CmEntity.Wallet.Set(new CmWallet(50)));
            tx.Commit();
        }

        var log = (DurabilityLog)dbe.DurabilityLog;
        var floorAtFailure = 0L;
        var failed = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit);
        try
        {
            failed.OpenMut(id).Write(CmEntity.Position) = new CmPosition(99, 88);
            // The probe sits between the floor's store and the frame's publish. Only this thread's append fails; nothing else appends here, so the
            // guard is a defence.
            var committerThread = Environment.CurrentManagedThreadId;
            log.AfterFloorProbe = () =>
            {
                if (Environment.CurrentManagedThreadId != committerThread)
                {
                    return;
                }
                floorAtFailure = failed.InFlightLsnFloor;
                throw new InvalidOperationException("injected: the append fails after its claim");
            };
            Exception thrown = null;
            try
            {
                failed.Commit();
            }
            catch (Exception e)
            {
                thrown = e;
            }
            finally
            {
                log.AfterFloorProbe = null;
            }

            var floorAfterFailure = failed.InFlightLsnFloor;
            var floorTheCheckpointReads = dbe.CheckpointManager.InFlightCommitFloor();

            // Before any assertion can fail, and before the dispose: a later commit takes LSNs past the abandoned claim. The UoW flush in Dispose and
            // the checkpoint's barrier both wait for LastAppendedLsn, which no drained frame covers until one past the abandoned claim is published.
            using (var next = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                next.Spawn<CmEntity>(CmEntity.Position.Set(new CmPosition(3, 3)), CmEntity.Wallet.Set(new CmWallet(1)));
                next.Commit();
            }

            Assert.That(thrown, Is.TypeOf<InvalidOperationException>(), "the injected append failure must reach the caller");
            Assert.That(floorAtFailure, Is.GreaterThan(0), $"{Ck13Marker}: the append had not stored its floor when its frame was about to be published");
            Assert.That(floorAfterFailure, Is.Zero, $"{Ck13Marker}: the commit threw and kept its floor");
            Assert.That(floorTheCheckpointReads, Is.EqualTo(long.MaxValue), $"{Ck13Marker}: the checkpoint still reads a floor from the failed transaction");
        }
        finally
        {
            failed.Dispose();
        }

        // Not CK-13: the abandoned claim must not keep CheckpointLSN from the later commit's records.
        var target = dbe.DurabilityLog.LastAppendedLsn;
        var cm = dbe.CheckpointManager;
        var cyclesBefore = cm.TotalCheckpoints;
        var reached = cm.ForceCheckpointAndWait(TimeSpan.FromSeconds(5), target);
        var report = $"CheckpointLSN={cm.CheckpointLsn}, abandoned claim at {floorAtFailure}, target {target}, DurableLsn={dbe.DurabilityLog.DurableLsn}, "
            + $"lowest floor={cm.InFlightCommitFloor()}, cycles {cyclesBefore}->{cm.TotalCheckpoints}, gated={cm.ConsecutiveGatedCycles}, "
            + $"skipped pages={cm.LastSkippedPages.Length}, health={cm.Health}, fatal={cm.HasFatalError}";
        Assert.That(reached, Is.True, $"the checkpoint stayed below the commit that follows the failed append ({report})");
    }

    /// <summary>
    /// #713: spawn an entity and write it in the SAME Commit-discipline transaction, then hard-crash with no checkpoint. The write no longer goes through
    /// the staging arena — an own spawn has no HEAD to protect, so it is written in place and rides the spawn's own SV Slot record (CM-06 / #395 D5). This
    /// test is what makes that claim testable: if the in-place write did not reach the record BuildCommitBatch emits, recovery hands back the spawn value.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public void CommitDiscipline_SpawnThenWrite_SameTransaction_SurvivesHardCrash()
    {
        EntityId id;

        using (var scope1 = _serviceProvider.CreateScope())
        {
            var dbe = scope1.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmWallet>();
            dbe.InitializeArchetypes();

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
            {
                id = tx.Spawn<CmEntity>(CmEntity.Position.Set(new CmPosition(1, 1)), CmEntity.Wallet.Set(new CmWallet(50)));
                tx.OpenMut(id).Write(CmEntity.Position) = new CmPosition(99, 88);   // same transaction as the Spawn
                tx.Commit();
            }

            dbe.SimulateHardCrash();
        }

        using (var scope2 = _serviceProvider.CreateScope())
        {
            var dbe = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<CmPosition>();
            dbe.RegisterComponentFromAccessor<CmWallet>();
            dbe.InitializeArchetypes();

            using var tx = dbe.CreateQuickTransaction();
            Assert.That(tx.IsAlive(id), Is.True, "a Commit-discipline spawn must survive a hard crash via WAL replay (CM-06)");

            ref readonly var pos = ref tx.Open(id).Read(CmEntity.Position);
            Assert.That(pos.X, Is.EqualTo(99f), "recovery restored the spawn value, not the same-transaction write");
            Assert.That(pos.Y, Is.EqualTo(88f), "recovery restored the spawn value, not the same-transaction write");
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
            using (var cm = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
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
            using (var cm = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
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

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate, CommitDiscipline.Commit))
            {
                id1 = tx.Spawn<CmIdxEntity>(CmIdxEntity.Position.Set(new CmPosition(1, 1)), CmIdxEntity.Team.Set(new CmTeam { TeamId = 7, Rank = 1 }));
                id2 = tx.Spawn<CmIdxEntity>(CmIdxEntity.Position.Set(new CmPosition(2, 2)), CmIdxEntity.Team.Set(new CmTeam { TeamId = 9, Rank = 2 }));
                tx.Commit();
            }

            // Consolidate the Commit-discipline spawns into the data file (CheckpointLSN advances past their LSNs), then hard-crash with an empty WAL window —
            // recovery must restore the cluster SV state + rebuild the index from the persisted base alone.
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "the checkpoint must cover what was written");
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
