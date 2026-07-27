using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// ═══════════════════════════════════════════════════════════════════════════════
// Regression: parallel cluster dispatch must bind a system to ITS OWN input view's archetype.
//
// TyphonRuntime.ResolveChangeFilters used to resolve each parallel QuerySystem's ArchetypeClusterState by scanning the GLOBAL ArchetypeRegistry and taking
// the first cluster-eligible archetype it found, never consulting the system's input view. That state feeds ctx.ClusterIds / ctx.StartClusterIndex /
// ctx.EndClusterIndex, so with more than one cluster archetype every system received archetype 0's cluster ids:
//   * archetype 0 has MORE clusters  -> chunk ids past the system's own segment -> "Computed page index >= segment length"
//   * archetype 0 has FEWER clusters -> the system's own tail clusters are silently never visited
//
// It was correct by accident with exactly one cluster archetype, which is why the whole suite passed: no existing fixture registers two. These tests
// deliberately register two with DIFFERENT cluster counts, which is the configuration that exposes it.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.MultiArch.Wide", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MultiArchWideData
{
    [Field]
    public int Value;
}

[Component("Typhon.Test.MultiArch.Narrow", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct MultiArchNarrowData
{
    [Field]
    public int Value;
}

/// <summary>Registered first, so it takes the lower ArchetypeId — this is the archetype the old global scan would have handed to everyone.</summary>
[Archetype]
partial class MultiArchWide : Archetype<MultiArchWide>
{
    public static readonly Comp<MultiArchWideData> Data = Register<MultiArchWideData>();
}

/// <summary>Registered second and deliberately given far fewer entities, so its cluster count differs from <see cref="MultiArchWide"/>'s.</summary>
[Archetype]
partial class MultiArchNarrow : Archetype<MultiArchNarrow>
{
    public static readonly Comp<MultiArchNarrowData> Data = Register<MultiArchNarrowData>();
}

[TestFixture]
[NonParallelizable]
class MultiArchetypeClusterDispatchTests : TestBase<MultiArchetypeClusterDispatchTests>
{
    private const int WideEntities = 600;   // many clusters
    private const int NarrowEntities = 40;  // few clusters — the asymmetry is the point

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<MultiArchWideData>();
        dbe.RegisterComponentFromAccessor<MultiArchNarrowData>();
        dbe.InitializeArchetypes();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < WideEntities; i++)
            {
                var v = new MultiArchWideData { Value = i };
                tx.Spawn<MultiArchWide>(MultiArchWide.Data.Set(in v));
            }

            for (var i = 0; i < NarrowEntities; i++)
            {
                var v = new MultiArchNarrowData { Value = i };
                tx.Spawn<MultiArchNarrow>(MultiArchNarrow.Data.Set(in v));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }

    [Test]
    public void TwoClusterArchetypes_HaveDifferentClusterCounts()
    {
        // Guards the guard: if both archetypes ever ended up with the same ActiveClusterCount the tests below would pass even with the bug reinstated.
        using var dbe = SetupEngine();

        var wide = dbe._archetypeStates[Archetype<MultiArchWide>.Metadata.ArchetypeId].ClusterState;
        var narrow = dbe._archetypeStates[Archetype<MultiArchNarrow>.Metadata.ArchetypeId].ClusterState;

        Assert.That(wide, Is.Not.Null);
        Assert.That(narrow, Is.Not.Null);
        Assert.That(wide.ActiveClusterCount, Is.GreaterThan(narrow.ActiveClusterCount),
            "the fixture must produce asymmetric cluster counts — that asymmetry is what exposes a cross-archetype binding");
    }

    [Test]
    public void EachParallelSystem_ReceivesItsOwnArchetypesClusterIds()
    {
        // Deliberately runs a system on BOTH archetypes. The old global scan handed every system the SAME (first cluster-eligible) archetype's cluster
        // list, so whichever archetype it picked, the other system is provably wrong. That makes this assertion independent of ArchetypeId ordering —
        // a single-system version passes or fails depending on which id happens to sort first, which is not a property worth depending on.
        using var dbe = SetupEngine();

        var wideIds = ActiveClusterIdsOf(dbe, Archetype<MultiArchWide>.Metadata.ArchetypeId);
        var narrowIds = ActiveClusterIdsOf(dbe, Archetype<MultiArchNarrow>.Metadata.ArchetypeId);

        using var txWide = dbe.CreateQuickTransaction();
        using var txNarrow = dbe.CreateQuickTransaction();
        var wideView = txWide.Query<MultiArchWide>().ToView();
        var narrowView = txNarrow.Query<MultiArchNarrow>().ToView();

        var wideVisited = new ConcurrentBag<int>();
        var narrowVisited = new ConcurrentBag<int>();
        var ticksSeen = 0;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("WideCluster", ctx => CollectClusterIds(ctx, wideVisited), input: () => wideView, parallel: true, after: "Tick");
            dag.QuerySystem("NarrowCluster", ctx => CollectClusterIds(ctx, narrowVisited), input: () => narrowView, parallel: true, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }

        // Assert COVERAGE, not membership. Each archetype owns its own ChunkBasedSegment and both number clusters from 1, so a foreign cluster id is
        // frequently also a valid local one — an "is this id mine?" check silently passes when the wrong (smaller) archetype is bound. The distinct set
        // actually visited is what distinguishes them: bound to the 1-cluster archetype, the 11-cluster system covers {1} instead of {1..11}.
        Assert.That(new HashSet<int>(wideVisited), Is.EquivalentTo(wideIds),
            "the Wide system must cover exactly MultiArchWide's clusters — a short set means it was bound to another archetype's cluster list");
        Assert.That(new HashSet<int>(narrowVisited), Is.EquivalentTo(narrowIds),
            "the Narrow system must cover exactly MultiArchNarrow's clusters");

        wideView.Dispose();
        narrowView.Dispose();
    }

    private static HashSet<int> ActiveClusterIdsOf(DatabaseEngine dbe, ushort archetypeId)
    {
        var state = dbe._archetypeStates[archetypeId].ClusterState;
        var ids = new HashSet<int>();
        for (var i = 0; i < state.ActiveClusterCount; i++)
        {
            ids.Add(state.ActiveClusterIds[i]);
        }

        return ids;
    }

    private static void CollectClusterIds(TickContext ctx, ConcurrentBag<int> sink)
    {
        if (ctx.ClusterIds == null)
        {
            return;
        }

        for (var i = ctx.StartClusterIndex; i < ctx.EndClusterIndex; i++)
        {
            sink.Add(ctx.ClusterIds[i]);
        }
    }

    [Test]
    public void ParallelClusterNativeSystem_OnSecondArchetype_VisitsEveryOwnEntityExactlyOncePerTick()
    {
        // The end-to-end shape the guide sample uses: GetClusterEnumerator(ctx.ClusterIds, start, end) + GetSpan. With the bug this either threw
        // "Computed page index >= segment length" or silently walked the wrong archetype's clusters.
        using var dbe = SetupEngine();

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<MultiArchNarrow>().ToView();

        var visitsPerTick = new ConcurrentBag<int>();
        var failures = new ConcurrentBag<string>();
        var ticksSeen = 0;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("NarrowWalk", ctx =>
            {
                try
                {
                    using var clusters = ctx.ClusterIds != null
                        ? ctx.Accessor.GetClusterEnumerator<MultiArchNarrow>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
                        : ctx.Accessor.GetClusterEnumerator<MultiArchNarrow>(ctx.StartClusterIndex, ctx.EndClusterIndex);

                    var seen = 0;
                    foreach (var cluster in clusters)
                    {
                        var bits = cluster.OccupancyBits;
                        var values = cluster.GetReadOnlySpan(MultiArchNarrow.Data);
                        while (bits != 0)
                        {
                            var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                            bits &= bits - 1;
                            if (values[slot].Value is < 0 or >= NarrowEntities)
                            {
                                failures.Add($"slot {slot} decoded Value={values[slot].Value}, outside [0,{NarrowEntities}) — wrong archetype's memory");
                            }

                            seen++;
                        }
                    }

                    visitsPerTick.Add(seen);
                }
                catch (Exception ex)
                {
                    failures.Add(ex.Message);
                }
            }, input: () => view, parallel: true, after: "Tick");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            SpinWait.SpinUntil(() => ticksSeen >= 3, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }

        Assert.That(failures, Is.Empty, string.Join(" | ", failures));
        Assert.That(visitsPerTick, Is.Not.Empty, "the system must have run at least once");
        foreach (var seen in visitsPerTick)
        {
            Assert.That(seen, Is.EqualTo(NarrowEntities),
                "each tick must visit MultiArchNarrow's entities exactly once — a wrong binding under-visits or double-visits");
        }

        view.Dispose();
    }
}
