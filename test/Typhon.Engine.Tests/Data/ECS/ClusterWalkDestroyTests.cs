using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.ClusterWalk.Data", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct ClusterWalkData
{
    [Field]
    public int Value;
}

[Archetype]
partial class ClusterWalkUnit : Archetype<ClusterWalkUnit>
{
    public static readonly Comp<ClusterWalkData> Data = Register<ClusterWalkData>();
}

/// <summary>
/// Issue #582 face 1 — a destroy that drained a cluster used to remove it from <c>ActiveClusterIds</c> inside <c>Commit()</c>, by swap-with-last, under a
/// walker holding the list. Drained clusters are now reclaimed by the tick fence alone (CLUSTERWALK-01, CLUSTERWALK-03).
/// </summary>
[TestFixture]
[NonParallelizable]
class ClusterWalkDestroyTests : TestBase<ClusterWalkDestroyTests>
{
    private const string WalkMarker = "CLUSTERWALK-01";

    /// <summary>
    /// The first chunk of a parallel QuerySystem empties the first cluster of the second chunk's range and commits — through a side transaction on the
    /// accessor path, through the chunk's own transaction on the per-chunk Transaction path. One worker runs the chunks in order, so that commit lands
    /// between Prepare (which captured the list) and the second chunk's walk: the interleaving a second worker produces by luck. The drained cluster must
    /// stay listed until the fence, so the walk, using the documented <c>GetClusterEnumerator(ctx.ClusterIds, ctx.StartClusterIndex,
    /// ctx.EndClusterIndex)</c> idiom, meets every live cluster exactly once and skips the empty one. Inline, the swap-with-last showed it the list's tail
    /// twice.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("CLUSTERWALK-01")]
    [VerifiesRule("CLUSTERWALK-03")]
    public void ADestroyCommittedDuringADispatch_DoesNotShowTheWalkAClusterTwice(bool writesVersioned)
    {
        var run = RunWalkScenario(writesVersioned, reclaimInTick: false);
        AssertTheWalkMetEachLiveClusterOnce(run);
    }

    /// <summary>
    /// The mutant: the drained cluster reclaimed in the middle of the dispatch, right after the destroy's commit — what the inline finalize did. The walk
    /// must then fail the verifier.
    /// </summary>
    [Test]
    [RuleMutant("CLUSTERWALK-01")]
    public void Mutant_ADrainReclaimedInTheTick_ShowsTheWalkAClusterTwice()
    {
        var run = RunWalkScenario(writesVersioned: false, reclaimInTick: true);
        RuleMutants.AssertDetects("CLUSTERWALK-01", WalkMarker, () => AssertTheWalkMetEachLiveClusterOnce(run));
    }

    private sealed record WalkRun(int Clusters, long DestroyTick, int DrainedChunk, List<(long Tick, int ChunkId)> Visits);

    private WalkRun RunWalkScenario(bool writesVersioned, bool reclaimInTick)
    {
        using var dbe = SetupEngine(2000);
        var state = StateOf(dbe);
        var clusters = state.ActiveClusterCount;
        Assert.That(clusters, Is.GreaterThanOrEqualTo(16), "precondition: too few clusters to split");

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<ClusterWalkUnit>().ToView();
        var visits = new List<(long Tick, int ChunkId)>();
        var destroyTick = -1L;
        var drainedChunk = -1;
        using var ticked = new CountdownEvent(4);
        var ticksSeen = 0;
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ =>
            {
                if (Interlocked.Increment(ref ticksSeen) <= 4)
                {
                    ticked.Signal();
                }
            });
            dag.QuerySystem("Walk", ctx =>
            {
                EntityAccessor accessor = writesVersioned ? ctx.Transaction : ctx.Accessor;
                foreach (var cluster in accessor.GetClusterEnumerator<ClusterWalkUnit>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex))
                {
                    lock (visits)
                    {
                        visits.Add((ctx.TickNumber, cluster.ChunkId));
                    }
                }

                // Once: the victim is the first cluster of the next chunk's range, which is not the list's tail while that range holds two or more.
                if (ctx.ChunkIndex != 0 || ctx.ChunkCount < 2 || destroyTick >= 0 || ctx.ClusterIds == null)
                {
                    return;
                }

                var victims = new List<EntityId>();
                foreach (var cluster in accessor.GetClusterEnumerator<ClusterWalkUnit>(ctx.ClusterIds, ctx.EndClusterIndex, ctx.EndClusterIndex + 1))
                {
                    drainedChunk = cluster.ChunkId;
                    var bits = cluster.OccupancyBits;
                    while (bits != 0)
                    {
                        victims.Add(cluster.GetEntityId(BitOperations.TrailingZeroCount(bits)));
                        bits &= bits - 1;
                    }
                }

                destroyTick = ctx.TickNumber;
                if (writesVersioned)
                {
                    // Committed by the runtime when this chunk returns, before the second chunk starts.
                    foreach (var id in victims)
                    {
                        ctx.Transaction.Destroy(id);
                    }
                }
                else
                {
                    using var side = ctx.CreateSideTransaction(DurabilityMode.Deferred);
                    foreach (var id in victims)
                    {
                        side.Destroy(id);
                    }

                    side.Commit();
                    if (reclaimInTick)
                    {
                        state.DrainPendingClusterFinalizations();   // the mutant: what the inline finalize did, mid-dispatch
                    }
                }
            }, input: () => view, parallel: true, writesVersioned: writesVersioned, chunksPerWorker: 2f, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 100, CostBasedChunking = false }))
        {
            runtime.Start();
            Assert.That(ticked.Wait(TimeSpan.FromSeconds(10)), Is.True, "precondition: the runtime did not run four ticks");
            runtime.Shutdown();
        }

        view.Dispose();
        Assert.That(destroyTick, Is.GreaterThanOrEqualTo(0), "precondition: no dispatch ran two chunks, so the destroy never met a split");
        Assert.That(state.ActiveClusterCount, Is.EqualTo(clusters - 1), "the drained cluster was not reclaimed by a later fence");
        Assert.That(state.ClusterSegment.IsChunkLive(drainedChunk), Is.False, "the drained cluster's chunk was not freed by a later fence");
        return new WalkRun(clusters, destroyTick, drainedChunk, visits);
    }

    // Plain asserts: the mutant runs this through RuleMutants.AssertDetects, which recognises the verifier's own failure by its marker.
    private static void AssertTheWalkMetEachLiveClusterOnce(WalkRun run)
    {
        var walk = run.Visits.Where(v => v.Tick == run.DestroyTick).Select(v => v.ChunkId).ToList();
        var twice = walk.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.That(twice, Is.Empty,
            $"{WalkMarker}: tick {run.DestroyTick}'s walk met cluster(s) [{string.Join(",", twice)}] twice after a destroy drained cluster {run.DrainedChunk}");
        Assert.That(walk, Does.Not.Contain(run.DrainedChunk), $"{WalkMarker}: the walk yielded the drained, empty cluster {run.DrainedChunk}");
        Assert.That(walk, Has.Count.EqualTo(run.Clusters - 1), $"{WalkMarker}: the walk did not meet every live cluster once");
    }

    /// <summary>
    /// Two threads, each committing its own transactions, every destroy draining a cluster — the concurrency <c>EcsConcurrencyTests</c> already treats as
    /// supported. Thread A drains the tail clusters last-first, so each of its targets is the list's tail; thread B drains from the head. Inline, two
    /// removers' swap-with-last corrupted the list between them. Now a commit only records its drain: the list is untouched until the fence, every drain is
    /// recorded exactly once (no record lost to a concurrent grow of the list), and the fence leaves exactly the survivors, freeing each chunk once.
    /// </summary>
    [Test]
    [CancelAfter(15000)]
    [VerifiesRule("CLUSTERWALK-03")]
    public void ConcurrentDestroysThatDrainClusters_LeaveTheActiveListExact()
    {
        using var dbe = SetupEngine(StressEntities);
        var state = StateOf(dbe);
        var (order, members) = MapClusters(dbe);
        Assert.That(order.Count, Is.GreaterThanOrEqualTo(256), "precondition: too few clusters for A's scans to be long");

        // Thin every cluster to one survivor, single-threaded, so that each destroy below drains a cluster.
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var chunk in order)
            {
                for (var i = 1; i < members[chunk].Count; i++)
                {
                    tx.Destroy(members[chunk][i]);
                }
            }

            tx.Commit();
        }

        Assert.That(state.ActiveClusterCount, Is.EqualTo(order.Count), "precondition: thinning must drain no cluster");
        Assert.That(state.HasPendingClusterFinalizations, Is.False, "precondition: thinning must record no drain");

        var share = order.Count / 4;
        var head = order.Take(share).ToArray();
        var tail = order.Skip(order.Count - share).Reverse().ToArray();
        var barrier = new Barrier(2);
        Exception failure = null;

        void DrainEach(int[] chunks)
        {
            try
            {
                barrier.SignalAndWait();
                foreach (var chunk in chunks)
                {
                    using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred);
                    tx.Destroy(members[chunk][0]);
                    tx.Commit();
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, e, null);
            }
        }

        var threadA = new Thread(() => DrainEach(tail));
        var threadB = new Thread(() => DrainEach(head));
        threadA.Start();
        threadB.Start();
        threadA.Join();
        threadB.Join();
        Assert.That(failure, Is.Null, $"a destroy thread threw: {failure}");

        Assert.That(SortedList(state), Is.EqualTo(order.OrderBy(c => c).ToArray()), "a commit changed the active list: drained clusters wait for the fence");
        Assert.That(state.PendingClusterFinalizationCount, Is.EqualTo(2 * share), "every drain is recorded exactly once");

        var doubleFreesBefore = state.ClusterSegment.DoubleFreeCount;
        dbe.WriteTickFence(2);

        var expected = order.Except(head).Except(tail).OrderBy(c => c).ToArray();
        var listed = SortedList(state);
        Assert.That(listed, Is.EqualTo(expected),
            $"{WalkMarker}: after {2 * share} concurrent draining destroys and a fence the active list holds {listed.Length} entries, expected "
            + $"{expected.Length}; stale or duplicated: [{string.Join(",", listed.Except(expected).Concat(listed.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key)))}]"
            + $", missing: [{string.Join(",", expected.Except(listed))}]");
        Assert.That(head.Concat(tail).Count(state.ClusterSegment.IsChunkLive), Is.Zero, "a drained cluster's chunk is still live after the fence");
        Assert.That(state.ClusterSegment.DoubleFreeCount, Is.EqualTo(doubleFreesBefore), "the fence freed a chunk twice");
        Assert.That(state.HasPendingClusterFinalizations, Is.False);
    }

    private static int[] SortedList(ArchetypeClusterState state)
    {
        var ids = state.ReadActiveClusterList(out var count);
        var listed = ids.AsSpan(0, count).ToArray();
        Array.Sort(listed);
        return listed;
    }

    /// <summary>
    /// #626: four threads, each committing its own spawn transactions into one non-spatial archetype, so that cluster allocation — and the append to
    /// <c>ActiveClusterIds</c> it makes — runs on several threads at once. Unlatched, two appenders write one index and publish one increment, leaving a
    /// live cluster absent from the list; the list must hold exactly the clusters an enumeration of the entities finds.
    /// </summary>
    [Test]
    [CancelAfter(15000)]
    public void ConcurrentSpawnsThatAllocateClusters_LeaveTheActiveListExact()
    {
        using var dbe = SetupEngine(0);
        var state = StateOf(dbe);
        const int threads = 4;
        const int perThread = 4096;
        var barrier = new Barrier(threads);
        Exception failure = null;

        void SpawnEach(int seed)
        {
            try
            {
                barrier.SignalAndWait();
                for (var i = 0; i < perThread; i++)
                {
                    using var tx = dbe.CreateQuickTransaction(DurabilityMode.Deferred);
                    var v = new ClusterWalkData { Value = seed * perThread + i };
                    tx.Spawn<ClusterWalkUnit>(ClusterWalkUnit.Data.Set(in v));
                    tx.Commit();
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, e, null);
            }
        }

        var workers = new Thread[threads];
        for (var t = 0; t < threads; t++)
        {
            var seed = t;
            workers[t] = new Thread(() => SpawnEach(seed));
            workers[t].Start();
        }

        foreach (var w in workers)
        {
            w.Join();
        }

        Assert.That(failure, Is.Null, $"a spawn thread threw: {failure}");

        var ids = state.ReadActiveClusterList(out var count);
        var listed = ids.AsSpan(0, count).ToArray();
        Assert.That(listed.Distinct().Count(), Is.EqualTo(count),
            $"the active list holds duplicates: [{string.Join(",", listed.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key))}]");

        // The walk goes through the active list, so a cluster dropped from it takes its entities out of the count.
        var (_, members) = MapClusters(dbe);
        Assert.That(members.Values.Sum(m => m.Count), Is.EqualTo(threads * perThread), "an active-list walk does not reach every spawned entity");
    }

    private const int StressEntities = 32 * 1024;

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClusterWalkUnit>.Metadata.ArchetypeId].ClusterState;

    /// <summary>Chunk ids in active-list order, and each cluster's entities.</summary>
    private static (List<int> Order, Dictionary<int, List<EntityId>> Members) MapClusters(DatabaseEngine dbe)
    {
        var order = new List<int>();
        var members = new Dictionary<int, List<EntityId>>();
        using var tx = dbe.CreateQuickTransaction();
        foreach (var cluster in tx.GetClusterEnumerator<ClusterWalkUnit>())
        {
            var ids = new List<EntityId>();
            var bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                ids.Add(cluster.GetEntityId(BitOperations.TrailingZeroCount(bits)));
                bits &= bits - 1;
            }

            order.Add(cluster.ChunkId);
            members[cluster.ChunkId] = ids;
        }

        return (order, members);
    }

    private DatabaseEngine SetupEngine(int entities)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClusterWalkData>();
        dbe.InitializeArchetypes();
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < entities; i++)
            {
                var v = new ClusterWalkData { Value = i };
                tx.Spawn<ClusterWalkUnit>(ClusterWalkUnit.Data.Set(in v));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }
}
