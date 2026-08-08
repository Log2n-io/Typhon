using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// ═══════════════════════════════════════════════════════════════════════════════
// Issue #631 — the per-(system, archetype) entity-touch rollup that feeds the Workbench Data Flow module never emitted on a real capture, and every system
// on every tick reported EntitiesProcessed == 0. The issue's hypothesis was that the emission's two gates are structurally antagonistic: gate 1 selects
// exactly the cluster-native parallel systems, and cluster-RANGE dispatch is precisely the path that never materializes the per-entity id list gate 2
// counts. These tests are the discriminating measurement it asked for, and they own the answer independently of the Workbench.
// ═══════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Touch.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TouchPos
{
    [Field]
    public int X;
}

/// <summary>Cluster-native (pure SingleVersion), which is what makes a parallel system over it take cluster-range dispatch.</summary>
[Archetype]
partial class TouchArch : Archetype<TouchArch>
{
    public static readonly Comp<TouchPos> Pos = Register<TouchPos>();
}

[TestFixture]
[NonParallelizable]
class SystemArchetypeTouchTests : TestBase<SystemArchetypeTouchTests>
{
    private const int EntityCount = 300;

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();

        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount; i++)
            {
                var v = new TouchPos { X = i };
                tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return dbe;
    }

    /// <summary>
    /// Gate 1 of the rollup: the system must be bound to exactly one archetype id. Guards the fixture — if this ever stops holding, the gate-2 assertion
    /// below would be measuring a system the emission never reaches, and would pass for the wrong reason.
    /// </summary>
    [TestCase(1)]
    [VerifiesRule("BIND-01")]
    [TestCase(4)]
    public void ParallelClusterNativeSystem_IsBoundToItsOwnArchetype(int workerCount)
    {
        using var dbe = SetupEngine();
        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();

        var ticksSeen = 0;
        ushort boundArchetypeId = ushort.MaxValue;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Walk", _ => { }, input: () => view, parallel: true, after: "Tick");
        }, new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = 1000 }))
        {
            runtime.Start();
            SpinWait.SpinUntil(() => ticksSeen >= 2, TimeSpan.FromSeconds(5));
            boundArchetypeId = runtime.SystemArchetypeIdOf(IndexOfSystem(runtime, "Walk"));
            runtime.Shutdown();
        }

        Assert.That(boundArchetypeId, Is.EqualTo(Archetype<TouchArch>.Metadata.ArchetypeId),
            "a parallel query system over a populated cluster-native archetype must bind to that archetype — gate 1 of the touch rollup");

        view.Dispose();
    }

    /// <summary>
    /// Gate 2 of the rollup, and the whole question in #631: does a cluster-range-dispatched parallel system report the entities it processed?
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sampled from a follower <c>CallbackSystem</c> rather than from inside the parallel body: the metrics array is reset at TickStart and read by
    /// <c>EmitSchedulerSystemArchetypeIfActive</c> at system end, so an <c>after:</c>-ordered reader observes the same value the emission gate would.
    /// </para>
    /// <para>
    /// Both worker counts matter. <c>WorkerCount = 1</c> takes <c>DagScheduler</c>'s single-threaded branch, whose system-end hook is a different call site
    /// from the multi-worker one — and that hook used to fire only for callback systems, which is one of the two ways this rollup could produce nothing.
    /// The assertion is on the EXACT count, not merely non-zero: an over-reporting gate would keep the Data Flow panel populated with wrong numbers, which
    /// is worse than empty.
    /// </para>
    /// </remarks>
    [TestCase(1)]
    [VerifiesRule("BIND-03")]
    [TestCase(4)]
    public void ParallelClusterNativeSystem_ReportsTheEntitiesItProcessed(int workerCount)
    {
        using var dbe = SetupEngine();
        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();

        // Keyed by tick: the parallel body runs once per CHUNK, so a per-chunk list would not be comparable against a per-tick metric.
        var visitedPerTick = new ConcurrentDictionary<long, int>();
        var reportedPerTick = new ConcurrentDictionary<long, int>();
        var ticksSeen = 0;
        TyphonRuntime captured = null;
        var walkIdx = -1;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Walk", ctx => visitedPerTick.AddOrUpdate(ctx.TickNumber, CountVisited(ctx), (_, prev) => prev + CountVisited(ctx)),
                input: () => view, parallel: true, after: "Tick");

            // Runs after "Walk" completes and before the next TickStart resets the metrics array, so it reads exactly what
            // EmitSchedulerSystemArchetypeIfActive read a moment earlier at Walk's system end.
            dag.CallbackSystem("Observe", ctx =>
            {
                if (walkIdx < 0)
                {
                    walkIdx = IndexOfSystem(captured, "Walk");
                }

                reportedPerTick[ctx.TickNumber] = captured.Scheduler.GetCurrentSystemMetrics(walkIdx).EntitiesProcessed;
            }, after: "Walk");
        }, new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = 1000 }))
        {
            captured = runtime;
            runtime.Start();
            SpinWait.SpinUntil(() => ticksSeen >= 3, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }

        Assert.That(visitedPerTick, Is.Not.Empty, "the system must have run at least once");
        Assert.That(reportedPerTick, Is.Not.Empty, "the follower must have observed at least one tick's metrics");

        var compared = 0;
        foreach (var (tick, visited) in visitedPerTick)
        {
            if (!reportedPerTick.TryGetValue(tick, out var reported))
            {
                continue;   // Shutdown can cut a tick between the two systems; only fully-observed ticks are comparable.
            }

            compared++;
            Assert.That(visited, Is.EqualTo(EntityCount), $"tick {tick}: the walk must visit every entity exactly once");
            Assert.That(reported, Is.EqualTo(visited),
                $"tick {tick}: EntitiesProcessed is gate 2 of the touch rollup — zero means it can never emit for a cluster-native parallel system, "
                + "and any other value means the Data Flow panel would show a number the walk did not do");
        }

        Assert.That(compared, Is.GreaterThan(0), "no tick was observed by both systems — the comparison above never ran");

        view.Dispose();
    }

    /// <summary>
    /// The binding is resolved ONCE, in the runtime constructor, and used to be gated on <c>ActiveClusterCount &gt; 0</c>. This ticks a runtime built while
    /// the archetype was still empty and populates it afterwards — the order any application that creates its runtime before loading data produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stake is larger than telemetry. The same resolution sets <c>_systemClusterStates</c>, which is what switches a parallel system to cluster-RANGE
    /// dispatch; left null, the system silently falls back to materializing a per-entity id list from the view for the rest of the session.
    /// </para>
    /// <para>
    /// Asserts the BINDING only, deliberately. The obvious follow-on — that the system then receives cluster ids and walks them — cannot be asserted in
    /// this ordering, because the input view was necessarily built before the spawns and an unfiltered pull view is frozen at construction (#718). That is
    /// a separate defect with its own fixture; conflating the two here would make this test fail for a reason it does not own.
    /// </para>
    /// </remarks>
    [TestCase(1)]
    [VerifiesRule("BIND-01")]
    [TestCase(4)]
    public void ParallelSystem_OnAnArchetypePopulatedAfterConstruction_StillBindsToIt(int workerCount)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TouchPos>();
        dbe.InitializeArchetypes();

        using var _ = dbe;
        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();

        var ticksSeen = 0;
        var boundArchetypeId = ushort.MaxValue;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Walk", _ => { }, input: () => view, parallel: true, after: "Tick");
        }, new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = 1000 }))
        {
            // Populate only NOW — after the runtime, and its cluster-state binding, have been constructed.
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < EntityCount; i++)
                {
                    var v = new TouchPos { X = i };
                    tx.Spawn<TouchArch>(TouchArch.Pos.Set(in v));
                }

                tx.Commit();
            }

            dbe.WriteTickFence(1);

            runtime.Start();
            SpinWait.SpinUntil(() => ticksSeen >= 3, TimeSpan.FromSeconds(5));
            boundArchetypeId = runtime.SystemArchetypeIdOf(IndexOfSystem(runtime, "Walk"));
            runtime.Shutdown();
        }

        Assert.That(boundArchetypeId, Is.EqualTo(Archetype<TouchArch>.Metadata.ArchetypeId),
            "an archetype that was empty when the runtime was built must still bind once it has clusters — otherwise the touch rollup is permanently dead "
            + "and the system never takes cluster-range dispatch");

        view.Dispose();
    }

    private static int CountVisited(TickContext ctx)
    {
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<TouchArch>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<TouchArch>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        var seen = 0;
        foreach (var cluster in clusters)
        {
            seen += System.Numerics.BitOperations.PopCount(cluster.OccupancyBits);
        }

        return seen;
    }

    private static int IndexOfSystem(TyphonRuntime runtime, string name)
    {
        var systems = runtime.Scheduler.Systems;
        for (var i = 0; i < systems.Length; i++)
        {
            if (systems[i] != null && systems[i].Name == name)
            {
                return i;
            }
        }

        return -1;
    }
}
