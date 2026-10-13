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
/// Issue #582 face 1 — a destroy that drains a cluster removes it from <c>ActiveClusterIds</c> inside <c>Commit()</c>, by swap-with-last, with no
/// protocol against a walker holding the list and no lock against another remover (CLUSTERWALK-01).
/// </summary>
[TestFixture]
[NonParallelizable]
class ClusterWalkDestroyTests : TestBase<ClusterWalkDestroyTests>
{
    /// <summary>
    /// The first chunk of a parallel QuerySystem empties the first cluster of the second chunk's range and commits — through a side transaction on the
    /// accessor path, through the chunk's own transaction on the per-chunk Transaction path. One worker runs the chunks in order, so that commit lands
    /// between Prepare (which captured the list) and the second chunk's walk: the interleaving a second worker produces by luck. The swap-with-last puts
    /// the list's tail into the hole and leaves the old copy where it was, so the second chunk, walking the range Prepare gave it with the documented
    /// <c>GetClusterEnumerator(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)</c> idiom, meets the tail cluster twice.
    /// </summary>
    /// <remarks>Quarantined against #582 face 1: red until a drained cluster's removal is deferred to the fence. Acceptance criterion for that fix.</remarks>
    [TestCase(false)]
    [TestCase(true)]
    [Category("Quarantine")]
    public void ADestroyCommittedDuringADispatch_DoesNotShowTheWalkAClusterTwice(bool writesVersioned)
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
        Assert.That(state.ActiveClusterCount, Is.EqualTo(clusters - 1), "precondition: the destroy did not drain its cluster");

        var walk = visits.Where(v => v.Tick == destroyTick).Select(v => v.ChunkId).ToList();
        var twice = walk.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.That(twice, Is.Empty,
            $"CLUSTERWALK-01: tick {destroyTick}'s walk met cluster(s) [{string.Join(",", twice)}] twice after a destroy drained cluster {drainedChunk}");
    }

    /// <summary>
    /// Two threads, each committing its own transactions, every destroy draining a cluster — the concurrency <c>EcsConcurrencyTests</c> already treats as
    /// supported. Thread A drains the tail clusters last-first, so each of its targets is the list's tail; thread B drains from the head. When B's
    /// swap-with-last moves A's target below the index A's scan has reached, A's scan ends without finding it, A frees the chunk anyway, and the freed id
    /// stays listed. No walker is involved: the two removers corrupt the list between them, because <c>RemoveFromActiveList</c> takes no lock.
    /// </summary>
    [Test]
    [CancelAfter(15000)]
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

        Assert.That(state.ActiveClusterCount, Is.EqualTo(order.Count), "precondition: thinning drained a cluster");

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

        var expected = order.Except(head).Except(tail).OrderBy(c => c).ToArray();
        var ids = state.ReadActiveClusterList(out var count);
        var listed = ids.AsSpan(0, count).ToArray();
        Array.Sort(listed);
        Assert.That(listed, Is.EqualTo(expected),
            $"CLUSTERWALK-01: after {2 * share} concurrent draining destroys the active list holds {count} entries, expected {expected.Length}; "
            + $"stale or duplicated: [{string.Join(",", listed.Except(expected).Concat(listed.GroupBy(c => c).Where(g => g.Count() > 1).Select(g => g.Key)))}]"
            + $", missing: [{string.Join(",", expected.Except(listed))}]");
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
