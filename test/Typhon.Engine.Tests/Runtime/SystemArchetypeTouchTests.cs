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

[Component("Typhon.Test.Touch.Other", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct TouchOther
{
    [Field]
    public int Y;
}

/// <summary>A second archetype, so a system whose input is <see cref="TouchArch"/> can declare a component no entity in that input holds (#908 case 2).</summary>
[Archetype]
partial class TouchOtherArch : Archetype<TouchOtherArch>
{
    public static readonly Comp<TouchOther> Other = Register<TouchOther>();
}

/// <summary>#908 case 1: a CallbackSystem that declares component access. The claim under test is that its body still runs.</summary>
sealed class DeclaringCallbackSystem : CallbackSystem
{
    public int Runs;

    protected override void Configure(SystemBuilder b) => b
        .Name("DeclaredCallback")
        .Reads<TouchPos>()
        .Writes<TouchOther>();

    protected override void Execute(TickContext ctx) => Interlocked.Increment(ref Runs);
}

/// <summary>#908 case 2: a QuerySystem declaring a component of an archetype other than its input View's. Same claim.</summary>
sealed class DeclaringQuerySystem : QuerySystem
{
    public Func<ViewBase> InputFactory;
    public int Runs;
    public int EntitiesSeen;

    protected override void Configure(SystemBuilder b) => b
        .Name("DeclaredQuery")
        .Input(InputFactory)
        .Reads<TouchPos>()
        // ReadsFresh, not Reads: DeclaredCallback writes TouchOther in the same phase, and a bare Reads is the conflict the deriver rejects. The point of
        // the test is unchanged — TouchOther is a component no entity in this system's input View holds.
        .ReadsFresh<TouchOther>()
        .After("DeclaredCallback");

    protected override void Execute(TickContext ctx)
    {
        Volatile.Write(ref EntitiesSeen, ctx.Entities.Count);
        Interlocked.Increment(ref Runs);
    }
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
        dbe.RegisterComponentFromAccessor<TouchOther>();
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

    /// <summary>
    /// #908 case 3: the SAME cluster-walking body must process the same entities whether or not the system is <c>.Parallel()</c>. It is
    /// <see cref="CountVisited"/> — the helper the parallel tests above use — that runs here, unchanged, on a single-invocation QuerySystem.
    /// </summary>
    /// <remarks>
    /// Before the fix, a non-parallel system was never bound to its input archetype's cluster state, so its TickContext carried <c>ClusterIds == null</c>,
    /// the range <c>(0,0)</c> that no dispatch had filled, and <c>Accessor == null</c>. This body then NullReferenceException'd on <c>ctx.Accessor</c>; a body
    /// that reached for <c>ctx.Transaction</c> instead walked the empty <c>(0,0)</c> range and silently processed nothing. Both fail by doing nothing, which
    /// is why the issue took a bisect to find.
    /// </remarks>
    [Test]
    [VerifiesRule("CD-03")]
    public void NonParallelQuery_OwnsItsWholeClusterPartition_AndRunsTheParallelBodyUnchanged()
    {
        using var dbe = SetupEngine();
        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();

        var observed = 0;
        var accessorWasNull = 0;
        var clusterIdsWereNull = 0;
        var start = -1;
        var end = -1;
        var visited = -1;
        var fullVisited = -1;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.QuerySystem("Walk", ctx =>
            {
                if (Volatile.Read(ref observed) != 0)
                {
                    return;
                }

                var seen = CountVisited(ctx);

                var fullCount = 0;
                using (var full = ctx.Transaction.GetClusterEnumerator<TouchArch>())
                {
                    foreach (var cluster in full)
                    {
                        fullCount += System.Numerics.BitOperations.PopCount(cluster.OccupancyBits);
                    }
                }

                Volatile.Write(ref accessorWasNull, ctx.Accessor == null ? 1 : 0);
                Volatile.Write(ref clusterIdsWereNull, ctx.ClusterIds == null ? 1 : 0);
                Volatile.Write(ref start, ctx.StartClusterIndex);
                Volatile.Write(ref end, ctx.EndClusterIndex);
                Volatile.Write(ref visited, seen);
                Volatile.Write(ref fullVisited, fullCount);
                Volatile.Write(ref observed, 1);
            }, input: () => view);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            var completed = SpinWait.SpinUntil(() => Volatile.Read(ref observed) != 0, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
            Assert.That(completed, Is.True, "the non-parallel QuerySystem did not execute");
        }

        Assert.That(accessorWasNull, Is.Zero, "every dispatch path hands the body a usable ctx.Accessor — the Transaction is one (#908)");
        Assert.That(clusterIdsWereNull, Is.Zero, "a non-parallel QuerySystem is bound to its input archetype's cluster list like a parallel one");
        Assert.That(start, Is.Zero);
        Assert.That(end, Is.GreaterThan(0), "a single-invocation system owns the whole partition: [0, clusterCount), never the unfilled (0,0)");
        Assert.That(visited, Is.EqualTo(EntityCount), "the parallel cluster-walk body, unchanged, must visit every entity on the non-parallel path");
        Assert.That(fullVisited, Is.EqualTo(visited), "owning the whole partition means the scoped walk and the full-archetype walk agree");

        view.Dispose();
    }

    /// <summary>
    /// #908 cases 1 and 2, measured where the issue measured them — in a running tick, not at Build. Declared component access is scheduling metadata: it
    /// derives edges and validates conflicts. It never decides whether a registered system's BODY runs, and a QuerySystem naming a component its input
    /// archetype does not hold still iterates its whole View.
    /// </summary>
    /// <remarks>
    /// A DAG-membership assertion is not enough on its own: the issue's symptom was a body that never executed, and a system can sit in
    /// <c>UserSystems</c> and still be skipped at dispatch (change filter, ShouldRun, a failed predecessor). These assertions are on the run counts.
    /// </remarks>
    [Test]
    public void DeclaredComponentAccess_DoesNotStopTheBodyFromRunning()
    {
        using var dbe = SetupEngine();
        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TouchArch>().ToView();

        var callback = new DeclaringCallbackSystem();
        var query = new DeclaringQuerySystem { InputFactory = () => view };

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.PublicTrack.DeclareDag("Test")
                    .Add(callback)
                    .Add(query);
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            var ran = SpinWait.SpinUntil(() => Volatile.Read(ref callback.Runs) > 0 && Volatile.Read(ref query.Runs) > 0, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
            Assert.That(ran, Is.True,
                $"both bodies must execute — CallbackSystem ran {Volatile.Read(ref callback.Runs)}x, QuerySystem ran {Volatile.Read(ref query.Runs)}x (#908)");
        }

        Assert.That(Volatile.Read(ref query.EntitiesSeen), Is.EqualTo(EntityCount),
            "declaring a component the input archetype does not hold must not narrow the entity set either");

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
