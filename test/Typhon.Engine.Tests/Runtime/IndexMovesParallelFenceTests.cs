using System;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// #1346: the parallel fence's IndexMoves phase applies the shadow drain's index moves on the workers, one leaf-disjoint key range per work item, and the Prep
/// tail finishes each batch. Driven through <see cref="TyphonRuntime"/> on a world small enough for a test, with the slicing and range thresholds brought
/// down so the phase really cuts each batch into several ranges; the index must agree with the data afterwards, and no AllowMultiple move may miss its
/// element on a later tick (an element id written back wrong would).
/// </summary>
[TestFixture]
[NonParallelizable]   // drives the static slicing and range thresholds, and the range probe
class IndexMovesParallelFenceTests : TestBase<IndexMovesParallelFenceTests>
{
    private const int Population = 8_192;
    private const int Ticks = 8;
    private const string Marker = "#1346 injected IndexMoves range failure";

    private sealed class Run
    {
        public int MaxChunks;
        public Exception Unhandled;
        public long LastTick;
    }

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<QfData>();
        dbe.RegisterComponentFromAccessor<QfOther>();
        dbe.RegisterComponentFromAccessor<QfVData>();
        dbe.InitializeArchetypes();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                var d = new QfData(i % 100, i * 2);
                var o = new QfOther();
                tx.Spawn<QfUnit>(QfUnit.Data.Set(in d), QfUnit.Other.Set(in o));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }

    /// <summary>Odd ticks move Group (AllowMultiple) by +1 / -1, even ticks swap neighbouring Keys (unique): legal on the final state (#1342).</summary>
    private static void WriteTick(Transaction tx, int n)
    {
        var flip = (n / 2 & 1) == 0 ? 1 : -1;
        foreach (var cluster in tx.For<QfUnit>().GetClusterEnumerator())
        {
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                var v = cluster.GetReadOnly(QfUnit.Data, slot);
                if ((n & 1) == 1)
                {
                    v.Group += flip;
                }
                else
                {
                    v.Key ^= 2;   // 4j <-> 4j + 2: each pair of keys swaps holders
                }

                cluster.Set(QfUnit.Data, slot, v);
            }
        }
    }

    /// <summary>Runs <see cref="Ticks"/> writing ticks on four workers, with every batch cut into ranges; stops early once the fence has failed.</summary>
    private static Run RunParallel(DatabaseEngine dbe)
    {
        var run = new Run();
        var savedMinClusters = DatabaseEngine.PrepSliceMinClusters;
        var savedSliceWords = FenceWorkPlan.PrepSliceWords;
        var savedMinEvents = IndexMoveBatch.MinEventsPerRange;
        DatabaseEngine.PrepSliceMinClusters = 2;
        FenceWorkPlan.PrepSliceWords = 1;
        IndexMoveBatch.MinEventsPerRange = 256;
        var ticks = 0;
        TyphonRuntime runtime = null;
        try
        {
            using var done = new ManualResetEventSlim();
            runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Write", ctx =>
                {
                    var n = Interlocked.Increment(ref ticks);
                    var chunks = runtime?.LastIndexMovesStats.Chunks ?? 0;   // the previous tick's fence
                    if (chunks > run.MaxChunks)
                    {
                        run.MaxChunks = chunks;
                    }

                    if (n > Ticks)
                    {
                        done.Set();
                        return;
                    }

                    WriteTick(ctx.Transaction, n);
                });
            }, new RuntimeOptions { WorkerCount = 4, BaseTickRate = 50, EnableParallelFence = true });

            using (runtime)
            {
                runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) =>
                {
                    Interlocked.CompareExchange(ref run.Unhandled, ex, null);
                    done.Set();
                };
                runtime.Start();
                Assert.That(done.Wait(TimeSpan.FromSeconds(45)), Is.True, "the run reached its tick count");
                runtime.Shutdown();
                run.LastTick = runtime.CurrentTickNumber;
            }
        }
        finally
        {
            DatabaseEngine.PrepSliceMinClusters = savedMinClusters;
            FenceWorkPlan.PrepSliceWords = savedSliceWords;
            IndexMoveBatch.MinEventsPerRange = savedMinEvents;
        }

        return run;
    }

    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("IX-08")]
    public void MovesAppliedInRangesOnTheWorkers_KeepTheIndexInAgreementWithTheData()
    {
        var dbe = SetupEngine();
        var missesBefore = Volatile.Read(ref BTreeMoveValueMisses.Count);
        var finishedBefore = Volatile.Read(ref IndexMoveBatch.RangesAppliedAtFinish);

        var run = RunParallel(dbe);

        Assert.That(run.Unhandled, Is.Null, $"the parallel fence threw: {run.Unhandled}");
        Assert.That(run.MaxChunks, Is.GreaterThan(1), "the IndexMoves phase cut a batch into several work items");
        Assert.That(Volatile.Read(ref IndexMoveBatch.RangesAppliedAtFinish) - finishedBefore, Is.Zero,
            "every range was applied on a worker, none fell back to the tail's serial finish");
        Assert.That(Volatile.Read(ref BTreeMoveValueMisses.Count) - missesBefore, Is.Zero, "no move missed its element: every element id was written back");
        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after moves applied in ranges on the workers");
    }

    /// <summary>
    /// Review 4 and 5 (#1346): a range whose work item throws without faulting its tree is applied by the tail, which runs every archetype's tail and
    /// raises the failure once. The batch is finished and the buffers and shadow bits reset: the next ticks' key changes are recorded and moved. Skipping
    /// the failed archetype's tail kept its shadow bits over emptied buffers, so the next tick's writes recorded nothing and their moves were lost.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    [VerifiesRule("IX-08")]
    public void ARangeThatFailsOnAWorker_IsFinishedByTheTail_AndTheNextTicksMovesAreKept()
    {
        var dbe = SetupEngine();
        var thrown = 0;
        var finishedBefore = Volatile.Read(ref IndexMoveBatch.RangesAppliedAtFinish);
        DatabaseEngine.IndexMoveRangeProbe = () =>
        {
            if (Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new InvalidOperationException(Marker);
            }
        };

        Run run;
        try
        {
            run = RunParallel(dbe);
        }
        finally
        {
            DatabaseEngine.IndexMoveRangeProbe = null;
        }

        Assert.That(run.Unhandled?.ToString(), Does.Contain(Marker), "the range's failure is raised, not swallowed");
        Assert.That(Volatile.Read(ref IndexMoveBatch.RangesAppliedAtFinish) - finishedBefore, Is.EqualTo(1), "the tail applied the failed range");
        IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, "after the tail finished the batch");

        // The next ticks, serially: their key changes must be recorded (shadow bits cleared) and moved.
        var tick = run.LastTick + 1;
        for (var n = 1; n <= 2; n++)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                WriteTick(tx, n);
                tx.Commit();
            }

            dbe.WriteTickFence(tick++);
            IndexDataOracle.AssertIndexAgreesWithData<QfUnit>(dbe, $"serial tick {n} after the failure");
        }
    }
}
