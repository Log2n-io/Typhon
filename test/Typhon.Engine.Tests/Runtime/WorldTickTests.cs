using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

[Component("Typhon.Test.WorldTick.V", 1, StorageMode = StorageMode.Versioned)]
[StructLayout(LayoutKind.Sequential)]
struct WorldTickV
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Archetype]
partial class WorldTickVUnit : Archetype<WorldTickVUnit>
{
    public static readonly Comp<WorldTickV> Data = Register<WorldTickV>();
}

[Component("Typhon.Test.WorldTick.Sv", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct WorldTickSv
{
    [Field]
    public int Value;

    [Field]
    public int Pad;
}

[Archetype]
partial class WorldTickSvUnit : Archetype<WorldTickSvUnit>
{
    public static readonly Comp<WorldTickSv> Data = Register<WorldTickSv>();
}

/// <summary>
/// #1495: a session's tick numbers start at 0, a world's ticks do not. <see cref="DatabaseEngine.WorldTickBase"/> is one past the last tick an earlier
/// session completed — exact across a clean close, and after a crash the clock of the last durable write — and <see cref="TickContext.WorldTick"/> is the
/// session tick plus it.
/// </summary>
[TestFixture]
[NonParallelizable]
internal sealed class WorldTickTests : TestBase<WorldTickTests>
{
    // A real WAL: the crash case reopens through recovery.
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    // No periodic checkpoint: the crash case's own is the only one, so what the reopen finds is exactly what it persisted.
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    private DatabaseEngine Open(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<WorldTickV>();
        dbe.RegisterComponentFromAccessor<WorldTickSv>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    // Session ticks [from, from + count), each committing one durable Versioned spawn during the tick, then its fence.
    private static void TickWritingVersioned(DatabaseEngine dbe, int from, int count)
    {
        for (var t = from; t < from + count; t++)
        {
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                tx.Spawn<WorldTickVUnit>(WorldTickVUnit.Data.Set(new WorldTickV { Value = t }));
                Assert.That(tx.Commit(), Is.True);
            }

            dbe.WriteTickFence(t);
        }
    }

    private static void FlushWal(DatabaseEngine dbe)
    {
        var wal = dbe.WalManager;
        wal.RequestFlush();
        var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(10));
        wal.WaitForDurable(wal.LastPublishedLsn, ref ctx);
    }

    private static void Tick(DatabaseEngine dbe, int from, int count)
    {
        for (var t = from; t < from + count; t++)
        {
            dbe.WriteTickFence(t);
        }
    }

    /// <summary>A fresh database starts at world tick 0; each clean reopen starts one past the last tick the session before completed.</summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void ACleanReopenContinuesTheWorldClock()
    {
        long first, second, third;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            first = dbe.WorldTickBase;
            Tick(dbe, 0, 10);
            Assert.That(dbe.NextWorldTick, Is.EqualTo(10));
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            second = dbe.WorldTickBase;
            Tick(dbe, 0, 5);
            Assert.That(dbe.NextWorldTick, Is.EqualTo(15), "session ticks 0..4 are world ticks 10..14");
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            third = dbe.WorldTickBase;
        }

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Zero, "a database no session ever ticked starts at 0");
            Assert.That(second, Is.EqualTo(10));
            Assert.That(third, Is.EqualTo(15));
        });
    }

    /// <summary>A session that never ticks leaves the clock where it found it: the persisted value is never lowered.</summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void ASessionThatNeverTicksDoesNotMoveTheClock()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Tick(dbe, 0, 7);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Assert.That(dbe.WorldTickBase, Is.EqualTo(7));
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Assert.That(dbe.WorldTickBase, Is.EqualTo(7), "an idle session wrote back what it opened with");
        }
    }

    /// <summary>
    /// After a crash the clock resumes at the clock of the last durable write, read back from the WAL past the checkpoint: a commit made in session tick 19
    /// carried world tick 19, which the reopened session repeats — every deadline the crash kept is judged against the clock it was stamped by.
    /// </summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void ACrashResumesAtTheClockOfTheLastDurableWrite()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            TickWritingVersioned(dbe, 0, 10);
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the checkpoint ran");
            TickWritingVersioned(dbe, 10, 10);
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Assert.That(dbe.LastWalV2RecoveryResult.MaxWorldTick, Is.EqualTo(19L), "precondition: the replayed WAL dates its last write");
            Assert.That(dbe.WorldTickBase, Is.EqualTo(19L), "the clock of the last durable write, not the checkpoint's");
        }
    }

    /// <summary>
    /// The clock a crash resumes at is never ahead of the data it kept: ten ticks that wrote nothing after the last write, then a checkpoint, then a crash —
    /// the reopened clock is the last write's, not the twenty ticks the session ran. The checkpoint persists the highest clock an append carried.
    /// </summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void TheClockACrashResumesAtIsNeverAheadOfTheDataItKept()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            TickWritingVersioned(dbe, 0, 10);
            Tick(dbe, 10, 10);
            Assert.That(dbe.NextWorldTick, Is.EqualTo(20L), "precondition: the session ran twenty ticks");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the checkpoint ran");
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Assert.That(dbe.WorldTickBase, Is.EqualTo(9L), "the last write was in world tick 9; the idle ticks after it carried nothing");
        }
    }

    /// <summary>
    /// A SingleVersion write is durable through its tick's fence record, which carries the NEXT tick's clock (the tick has ended): a crash after five such
    /// ticks resumes at world tick 5, one past the last tick whose fence reached the log.
    /// </summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void AFenceRecordDatesItsTickForRecovery()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            EntityId id;
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
            {
                id = tx.Spawn<WorldTickSvUnit>(WorldTickSvUnit.Data.Set(new WorldTickSv { Value = -1 }));
                Assert.That(tx.Commit(), Is.True);
            }

            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the spawn is checkpointed");
            for (var t = 0; t < 5; t++)
            {
                using (var tx = dbe.CreateQuickTransaction())
                {
                    tx.OpenMut(id).Set(WorldTickSvUnit.Data, new WorldTickSv { Value = t });
                    Assert.That(tx.Commit(), Is.True);
                }

                dbe.WriteTickFence(t);
            }

            FlushWal(dbe);
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Assert.That(dbe.WorldTickBase, Is.EqualTo(5L), "the last fence record (tick 4, ended) carried world tick 5");
        }
    }

    /// <summary>
    /// A crash-open's recovered clock survives a second crash with no write in between: the recovery seal's checkpoint recycles the WAL that held it, so
    /// it must persist the recovered clock itself, not only what the new session appended (nothing).
    /// </summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void ARecoveredClockSurvivesASecondCrashWithNoWriteBetween()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            TickWritingVersioned(dbe, 0, 10);
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Assert.That(dbe.WorldTickBase, Is.EqualTo(9L), "precondition: recovered from the WAL");
            Assert.That(dbe.CheckpointManager.ForceCheckpointAndWait(TimeSpan.FromSeconds(10)), Is.True, "precondition: a checkpoint ran past the WAL");
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Assert.That(dbe.WorldTickBase, Is.EqualTo(9L), "the recovered clock, persisted by the checkpoint that recycled its WAL");
        }
    }

    /// <summary>
    /// The schema-migration arm of the checkpoint's flip: a migrating crash-open replays the WAL and commits its new catalog in a staged checkpoint flip,
    /// which must carry the recovered clock too — a second crash finds it there, the WAL it came from consumed by that very flip.
    /// </summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void AMigratingCrashOpensStagedFlipPersistsTheRecoveredClock()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV1>();
            dbe.InitializeArchetypes();
            for (var t = 0; t < 10; t++)
            {
                using (var tx = dbe.CreateQuickTransaction(DurabilityMode.Immediate))
                {
                    tx.Spawn<WalMigrationArch>(WalMigrationArch.Value.Set(new WalMigrationV1 { A = t, B = -t }));
                    Assert.That(tx.Commit(), Is.True);
                }

                dbe.WriteTickFence(t);
            }

            dbe.SimulateHardCrash();
        }

        long staged = -1;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            dbe.SchemaCommitProbe = stage =>
            {
                if (stage == SchemaCommitStage.Staged)
                {
                    staged = 1;
                }
            };
            dbe.InitializeArchetypes();
            Assert.That(dbe.WorldTickBase, Is.EqualTo(9L), "precondition: recovered from the WAL");
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            dbe.RegisterComponentFromAccessor<WalMigrationV2>();
            dbe.InitializeArchetypes();
            Assert.That(staged, Is.EqualTo(1L), "precondition: the open migrated through a staged commit");
            Assert.That(dbe.WorldTickBase, Is.EqualTo(9L), "the staged flip persisted the recovered clock");
        }
    }

    /// <summary>
    /// The clean close's own write: with the shutdown checkpoint held back by the coverage gate (it cannot advance CheckpointLSN, so its flip persists
    /// nothing), the clock still survives the close — the clean-close flip writes it.
    /// </summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void TheCleanCloseKeepsTheClockWhenItsCheckpointCannotAdvance()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Tick(dbe, 0, 12);
            dbe.CheckpointManager.GateCheckpointForTest = true;
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Assert.That(dbe.WorldTickBase, Is.EqualTo(12L), "the clean close persisted the exact clock");
        }
    }

    /// <summary>
    /// The checkpoint never persists a clock no durable record carries: the clock it sampled stands only when its barrier covers the allocation frontier read
    /// after the sample; an append claimed past the barrier (unpublished, or behind the CK-13 in-flight floor) leaves the persisted clock where it was.
    /// </summary>
    /// <remarks>
    /// A unit test of the decision, not a staged race: a cycle cannot finish while a commit is held between its claim and its publish, so the window cannot
    /// be parked for an integration test. The ordering that makes the decision sound — the append raises the max after its claim with a full fence, the
    /// cycle samples the max before the frontier — is stated at the sample site.
    /// </remarks>
    [Test]
    [VerifiesRule("TICK-01")]
    public void TheCheckpointPersistsTheSampledClockOnlyWhenItsBarrierCoversTheFrontier()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CheckpointManager.WorldTickToPersist(sampled: 10, frontier: 500, barrierLsn: 500), Is.EqualTo(10L), "barrier at the frontier");
            Assert.That(CheckpointManager.WorldTickToPersist(sampled: 10, frontier: 500, barrierLsn: 640), Is.EqualTo(10L), "barrier past the frontier");
            Assert.That(CheckpointManager.WorldTickToPersist(sampled: 10, frontier: 500, barrierLsn: 499), Is.Zero,
                "a claim past the barrier: nothing raised, the last persisted clock stands");
        });
    }

    /// <summary>
    /// A system's commit is dated by the world tick its TickContext showed: after a runtime ran systems that each spawn, a crash resumes at the highest
    /// TickContext.WorldTick a system saw — the clock that stamped its writes — not past it.
    /// </summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void ARuntimeSystemsCommitIsDatedByItsTickContext()
    {
        long highestSeen = -1;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            using var ticked = new CountdownEvent(5);
            using (var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Spawn", ctx =>
                {
                    ctx.Transaction.Spawn<WorldTickVUnit>(WorldTickVUnit.Data.Set(new WorldTickV { Value = (int)ctx.WorldTick }));
                    InterlockedMax(ref highestSeen, ctx.WorldTick);
                    if (ticked.CurrentCount > 0)
                    {
                        ticked.Signal();
                    }
                });
            }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 200 }))
            {
                runtime.Start();
                Assert.That(ticked.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the runtime did not tick");
                runtime.Shutdown();
            }

            Assert.That(dbe.NextWorldTick, Is.GreaterThan(Interlocked.Read(ref highestSeen)), "precondition: the clock moved past the last system's tick");
            dbe.SimulateHardCrash();
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Assert.That(dbe.WorldTickBase, Is.EqualTo(Interlocked.Read(ref highestSeen)), "the clock of the last system's durable commit");
        }
    }

    private static void InterlockedMax(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }

    /// <summary>
    /// A WAL segment of another format version fails the open loudly instead of being skipped as an invalid header — after a crash, skipping it would drop
    /// committed data with no error.
    /// </summary>
    [Test]
    public void AWalSegmentOfAnotherFormatFailsTheOpen()
    {
        string walDir;
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            walDir = dbe.WalDirectory;
            TickWritingVersioned(dbe, 0, 3);
            dbe.SimulateHardCrash();
        }

        var patched = 0;
        foreach (var file in System.IO.Directory.GetFiles(walDir, "*.wal"))
        {
            using var fs = new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite);
            if (fs.Length < WalSegmentHeader.SizeInBytes)
            {
                continue;
            }

            fs.Position = 4;   // WalSegmentHeader.Version, after Magic
            fs.Write(BitConverter.GetBytes(2u));
            patched++;
        }

        Assert.That(patched, Is.GreaterThan(0), "precondition: the crash left WAL segments");
        Exception failure = null;
        try
        {
            using var scope = ServiceProvider.CreateScope();
            using var dbe = Open(scope);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // The WAL directory outlives this test's database: the patched segments must not reach the next test's open.
            foreach (var file in System.IO.Directory.GetFiles(walDir, "*.wal"))
            {
                System.IO.File.Delete(file);
            }
        }

        var durability = failure;
        while (durability != null && durability is not DurabilityException)
        {
            durability = durability.InnerException;
        }

        Assert.That(durability, Is.Not.Null, $"the open must refuse an older WAL format, not skip it (got: {failure})");
        Assert.That(durability.Message, Does.Contain("format 2"));
    }

    /// <summary>
    /// A second runtime on the same session — its scheduler numbering from 0 again — continues the world ticks the first one issued instead of repeating
    /// them, and its ticks keep advancing the clock the close persists.
    /// </summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void ASecondRuntimeOnOneSessionContinuesTheWorldTicks()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = Open(scope);
        Tick(dbe, 0, 5);   // a host drove the fence first: world ticks 0..4

        long RunOnce(out long firstSeen)
        {
            using var ticked = new CountdownEvent(3);
            long first = -1;
            using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", ctx =>
                {
                    Interlocked.CompareExchange(ref first, ctx.WorldTick, -1);
                    if (ticked.CurrentCount > 0)
                    {
                        ticked.Signal();
                    }
                });
            }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 200 });
            runtime.Start();
            Assert.That(ticked.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the runtime did not tick");
            runtime.Shutdown();
            firstSeen = Interlocked.Read(ref first);
            return dbe.NextWorldTick;
        }

        var afterFirst = RunOnce(out var firstOfFirst);
        var afterSecond = RunOnce(out var firstOfSecond);

        Assert.Multiple(() =>
        {
            Assert.That(firstOfFirst, Is.EqualTo(5L), "the first runtime starts where the host's fences left the clock");
            Assert.That(firstOfSecond, Is.EqualTo(afterFirst), "the second starts one past the first's last tick, not at the session's base");
            Assert.That(afterSecond, Is.GreaterThan(afterFirst), "the second's ticks advance the clock");
        });
    }

    /// <summary>The runtime hands each system the world tick: its session tick plus the base the database opened with.</summary>
    [Test]
    [VerifiesRule("TICK-01")]
    public void TheRuntimeHandsSystemsTheWorldTick()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            Tick(dbe, 0, 25);
        }

        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = Open(scope);
            using var ticked = new CountdownEvent(3);
            long mismatches = 0;
            long sampled = -1;
            using (var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", ctx =>
                {
                    if (ctx.WorldTick != 25 + ctx.TickNumber)
                    {
                        Interlocked.Increment(ref mismatches);
                    }

                    Interlocked.Exchange(ref sampled, ctx.WorldTick);
                    if (ticked.CurrentCount > 0)
                    {
                        ticked.Signal();
                    }
                });
            }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 200 }))
            {
                runtime.Start();
                Assert.That(ticked.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the runtime did not tick");
                runtime.Shutdown();
                Assert.That(runtime.Scheduler.CurrentWorldTick, Is.EqualTo(25 + runtime.CurrentTickNumber));
            }

            Assert.Multiple(() =>
            {
                Assert.That(Interlocked.Read(ref mismatches), Is.Zero, "every TickContext.WorldTick is the session tick plus the base");
                Assert.That(Interlocked.Read(ref sampled), Is.Not.EqualTo(-1L), "precondition: a system ran");
                Assert.That(dbe.NextWorldTick, Is.GreaterThan(25L), "the runtime's completed ticks advance the clock the close persists");
            });
        }
    }
}
