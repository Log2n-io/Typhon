using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// RT-1 (Realms): every QuerySystem path dispatches the one cluster selection — tier ∩ cellAmortize bucket ∩ awake clusters (DSEL-01), the bucket keyed
/// on the system's run count (DSEL-02). Before it, only the parallel non-Versioned path applied dormancy and amortization.
/// </summary>
[TestFixture]
[NonParallelizable]
class DispatchSelectionTests : TestBase<DispatchSelectionTests>
{
    private static TierPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Data = 1.0f };

    private DatabaseEngine SetupEngineWithGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(100, 100), cellSize: 10f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>One entity per cell along y = 5, each cell set to <paramref name="tier"/> — one cluster each.</summary>
    private static EntityId[] SpawnRow(DatabaseEngine dbe, int n, SimTier tier)
    {
        var ids = new EntityId[n];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < n; i++)
            {
                ids[i] = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f + 10f * i, 5f)));
            }

            tx.Commit();
        }

        for (var i = 0; i < n; i++)
        {
            dbe.Realm0Grid.SetCellTier(dbe.Realm0Grid.WorldToCellKey(5f + 10f * i, 5f, 0f), tier);
        }

        return ids;
    }

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<TierUnit>.Metadata.ArchetypeId].ClusterState;

    private static int ChunkOf(DatabaseEngine dbe, float x, float y)
    {
        var cs = ClusterState(dbe);
        var cellKey = dbe.Realm0Grid.WorldToCellKey(x, y, 0f);
        for (var i = 0; i < cs.ActiveClusterCount; i++)
        {
            var cid = cs.ActiveClusterIds[i];
            if (cs.ClusterCellMap[cid] == cellKey)
            {
                return cid;
            }
        }

        throw new InvalidOperationException("no cluster in that cell");
    }

    /// <summary>Puts one cluster to sleep for good: the threshold stays 0, so the fence's sweep never runs and never wakes it (no heartbeat either).</summary>
    private static void Sleep(ArchetypeClusterState cs, int chunkId)
    {
        cs.SleepThresholdTicks = 0;
        cs.SleepStates[chunkId] = ClusterSleepState.Sleeping;
        cs.SleepingClusterCount++;
    }

    /// <summary>Tight-spin wait (SpinWait.SpinUntil sleeps up to a timer quantum between probes).</summary>
    private static void WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
            {
                Assert.Fail("timed out waiting for the runtime");
            }

            Thread.Yield();
        }
    }

    /// <summary>
    /// Runs the declared systems until <paramref name="runs"/> distinct ticks have been recorded, and returns, per tick in order, the entities seen.
    /// </summary>
    private static List<HashSet<EntityId>> RunAndRecord(DatabaseEngine dbe, Action<Dag, Action<TickContext>> declare, int runs)
    {
        var seen = new ConcurrentQueue<(long Tick, EntityId Entity)>();
        var ticks = new ConcurrentDictionary<long, byte>();
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   var dag = schedule.PublicTrack.DeclareDag("Test");
                   declare(dag, ctx =>
                   {
                       ticks.TryAdd(ctx.TickNumber, 0);
                       foreach (var id in ctx.Entities)
                       {
                           seen.Enqueue((ctx.TickNumber, id));
                       }
                   });
               }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            WaitFor(() => ticks.Count > runs);
            runtime.Shutdown();
        }

        // The last tick may have been cut by the shutdown: keep only the first `runs` complete ones.
        var order = ticks.Keys.OrderBy(t => t).Take(runs).ToArray();
        var byTick = order.ToDictionary(t => t, _ => new HashSet<EntityId>());
        foreach (var (tick, entity) in seen)
        {
            if (byTick.TryGetValue(tick, out var set))
            {
                Assert.That(set.Add(entity), Is.True, $"entity {entity} dispatched twice in tick {tick}");
            }
        }

        return order.Select(t => byTick[t]).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Non-parallel path
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("DSEL-01")]
    public void NonParallelTier_OnlyTheTiersEntities()
    {
        using var dbe = SetupEngineWithGrid();
        var near = SpawnRow(dbe, 2, SimTier.Tier0);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(55f, 55f)));
            tx.Commit();
        }

        dbe.Realm0Grid.SetCellTier(dbe.Realm0Grid.WorldToCellKey(55f, 55f, 0f), SimTier.Tier3);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var runs = RunAndRecord(dbe, (dag, body) => dag.QuerySystem("NonParallelTier", body, input: () => view, tier: SimTier.Tier0), runs: 4);

        var expected = near.ToHashSet();
        foreach (var run in runs)
        {
            Assert.That(run, Is.EquivalentTo(expected), "a non-parallel Tier0 system must see the Tier0 entities only, every tick");
        }
    }

    [Test]
    [VerifiesRule("DSEL-01")]
    [VerifiesRule("DSEL-02")]
    public void NonParallelTier_CellAmortize4_OneBucketPerRun_EachEntityOncePerCycle()
    {
        using var dbe = SetupEngineWithGrid();
        var ids = SpawnRow(dbe, 4, SimTier.Tier2);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var runs = RunAndRecord(dbe,
            (dag, body) => dag.QuerySystem("Amortized", body, input: () => view, tier: SimTier.Tier2, cellAmortize: 4), runs: 8);

        foreach (var run in runs)
        {
            Assert.That(run, Has.Count.EqualTo(1), "cellAmortize 4 over 4 one-entity clusters: one entity per run");
        }

        // Any 4 consecutive runs cover all 4 entities exactly once.
        for (var start = 0; start + 4 <= runs.Count; start++)
        {
            var cycle = runs.Skip(start).Take(4).SelectMany(r => r).ToList();
            Assert.That(cycle, Is.EquivalentTo(ids), $"runs {start}..{start + 3}");
        }
    }

    [Test]
    [VerifiesRule("DSEL-01")]
    public void NonParallel_SleepingCluster_NotDispatched()
    {
        using var dbe = SetupEngineWithGrid();
        var ids = SpawnRow(dbe, 2, SimTier.Tier0);
        Sleep(ClusterState(dbe), ChunkOf(dbe, 5f, 5f));

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var runs = RunAndRecord(dbe, (dag, body) => dag.QuerySystem("NonParallel", body, input: () => view), runs: 4);

        foreach (var run in runs)
        {
            Assert.That(run, Is.EquivalentTo((EntityId[])[ids[1]]), "the sleeping cluster's entity must not be dispatched");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Versioned parallel path
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("DSEL-01")]
    public void VersionedParallel_SleepingCluster_NotDispatched([Values] bool tiered)
    {
        using var dbe = SetupEngineWithGrid();
        var ids = SpawnRow(dbe, 2, SimTier.Tier0);
        Sleep(ClusterState(dbe), ChunkOf(dbe, 5f, 5f));

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var runs = RunAndRecord(dbe,
            (dag, body) => dag.QuerySystem("Versioned", body, input: () => view, parallel: true, writesVersioned: true,
                tier: tiered ? SimTier.Tier0 : SimTier.All),
            runs: 4);

        foreach (var run in runs)
        {
            Assert.That(run, Is.EquivalentTo((EntityId[])[ids[1]]), "the sleeping cluster's entity must not be dispatched");
        }
    }

    [Test]
    [VerifiesRule("DSEL-01")]
    public void VersionedParallel_CellAmortize_OneBucketPerRun()
    {
        using var dbe = SetupEngineWithGrid();
        SpawnRow(dbe, 4, SimTier.Tier2);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var runs = RunAndRecord(dbe,
            (dag, body) => dag.QuerySystem("Versioned", body, input: () => view, parallel: true, writesVersioned: true, tier: SimTier.Tier2,
                cellAmortize: 4),
            runs: 4);

        Assert.That(runs.Select(r => r.Count), Is.All.EqualTo(1), "the Versioned path materializes this run's bucket, not the whole tier");
        Assert.That(runs.SelectMany(r => r).Distinct().Count(), Is.EqualTo(4));
    }

    [Test]
    [VerifiesRule("DSEL-01")]
    public void VersionedCheckerboard_EachEntityOncePerTick()
    {
        using var dbe = SetupEngineWithGrid();
        // Two adjacent cells: one Red, one Black.
        var ids = SpawnRow(dbe, 2, SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        // RunAndRecord asserts no entity is dispatched twice in a tick; before RT-1 both phases materialized the whole tier.
        var runs = RunAndRecord(dbe,
            (dag, body) => dag.QuerySystem("Checkerboard", body, input: () => view, parallel: true, writesVersioned: true, tier: SimTier.Tier0,
                checkerboard: true),
            runs: 4);

        foreach (var run in runs)
        {
            Assert.That(run, Is.EquivalentTo(ids));
        }
    }

    [Test]
    [VerifiesRule("DSEL-01")]
    public void ViewTier_NarrowsEveryPath([Values] bool parallel, [Values] bool writesVersioned)
    {
        using var dbe = SetupEngineWithGrid();
        var near = SpawnRow(dbe, 2, SimTier.Tier0);
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(55f, 55f)));
            tx.Commit();
        }

        dbe.Realm0Grid.SetCellTier(dbe.Realm0Grid.WorldToCellKey(55f, 55f, 0f), SimTier.Tier1);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        view.WithTier(SimTier.Tier0);
        // The system declares no tier: the view's own tier is the only narrowing, and every path must honour it (the Versioned path once ignored it).
        var runs = RunAndRecord(dbe,
            (dag, body) => dag.QuerySystem("ViewTier", body, input: () => view, parallel: parallel, writesVersioned: parallel && writesVersioned),
            runs: 3);

        foreach (var run in runs)
        {
            Assert.That(run, Is.EquivalentTo(near));
        }
    }

    [Test]
    [VerifiesRule("DSEL-01")]
    public void Checkerboard_WithChangeFilter_Refused()
    {
        using var dbe = SetupEngineWithGrid();
        using var tx = dbe.CreateQuickTransaction();
        using var view = tx.Query<TierUnit>().ToView();
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                var dag = schedule.PublicTrack.DeclareDag("Test");
                dag.QuerySystem("Bad", _ => { }, input: () => view, changeFilter: [typeof(TierPos)], parallel: true, checkerboard: true);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });
        Assert.That(ex?.Message, Does.Contain("change filter"));
    }

    [Test]
    [VerifiesRule("DSEL-01")]
    public void DisjointSystemAndViewTiers_RefusedAtConstruction()
    {
        using var dbe = SetupEngineWithGrid();
        using var tx = dbe.CreateQuickTransaction();
        using var view = tx.Query<TierUnit>().ToView();
        view.WithTier(SimTier.Tier1);
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                var dag = schedule.PublicTrack.DeclareDag("Test");
                dag.QuerySystem("Disjoint", _ => { }, input: () => view, parallel: true, tier: SimTier.Tier0);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });
        Assert.That(ex?.Message, Does.Contain("no overlap"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Run-count bucket (DSEL-02)
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("DSEL-02")]
    public void TickDivisor2_CellAmortize2_EveryClusterVisited()
    {
        using var dbe = SetupEngineWithGrid();
        var ids = SpawnRow(dbe, 4, SimTier.Tier2);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        // The system runs on even ticks only. Keyed on the tick, its bucket was always 0: entities 1 and 3 never ran.
        var runs = RunAndRecord(dbe,
            (dag, body) => dag.QuerySystem("Divided", body, input: () => view, parallel: true, tier: SimTier.Tier2, cellAmortize: 2, tickDivisor: 2),
            runs: 4);

        Assert.That(runs.SelectMany(r => r).ToHashSet(), Is.EquivalentTo(ids));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Fast path + validation
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("DSEL-01")]
    public void NoFilter_DispatchArrayIsActiveClusterIds()
    {
        using var dbe = SetupEngineWithGrid();
        SpawnRow(dbe, 3, SimTier.Tier0);
        var cs = ClusterState(dbe);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        int[] observed = null;
        var ticks = 0;
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   var dag = schedule.PublicTrack.DeclareDag("Test");
                   dag.QuerySystem("Plain", ctx =>
                   {
                       observed = ctx.ClusterIds;
                       Interlocked.Increment(ref ticks);
                   }, input: () => view, parallel: true);
               }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            WaitFor(() => Volatile.Read(ref ticks) >= 2);
            runtime.Shutdown();
        }

        Assert.That(observed, Is.SameAs(cs.ActiveClusterIds), "nothing narrows the system: it walks the archetype's own list, no copy");
    }

    [Test]
    [VerifiesRule("DSEL-01")]
    public void CellAmortize_WithChangeFilter_Refused()
    {
        using var dbe = SetupEngineWithGrid();
        using var tx = dbe.CreateQuickTransaction();
        using var view = tx.Query<TierUnit>().ToView();
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            TyphonRuntime.Create(dbe, schedule =>
            {
                var dag = schedule.PublicTrack.DeclareDag("Test");
                dag.QuerySystem("Bad", _ => { }, input: () => view, changeFilter: [typeof(TierPos)], tier: SimTier.Tier2, cellAmortize: 2);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });
        Assert.That(ex?.Message, Does.Contain("change filter"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DSEL-03 — a system's integration step is its own revisit interval
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The three QuerySystem dispatch shapes, because each builds its own <see cref="TickContext"/> in its own method — and a formula applied on three of
    /// four paths is exactly the bug class DSEL-01 exists for.
    /// </summary>
    public enum QueryShape
    {
        Serial,
        Parallel,
        ParallelVersioned,
    }

    /// <summary>Records the <c>(DeltaTime, AmortizedDeltaTime)</c> pair each run was handed. Both come from the same tick, so their RATIO is exact.</summary>
    private sealed class DtProbe
    {
        public readonly ConcurrentQueue<(long Tick, float Dt, float Amortized)> Samples = new();

        public void Record(TickContext ctx) => Samples.Enqueue((ctx.TickNumber, ctx.DeltaTime, ctx.AmortizedDeltaTime));

        public int Count => Samples.Count;

        /// <summary>The samples that saw real elapsed time — tick 0's DeltaTime is 0 by contract and carries no ratio.</summary>
        public (long Tick, float Dt, float Amortized)[] Moving() => Samples.Where(s => s.Dt > 0f).ToArray();
    }

    /// <summary>The marker <see cref="AssertStepsBy"/> fails with, so the mutant can prove it was THIS assertion that rejected.</summary>
    private const string StepMarker = "the integration step must be";

    /// <summary>
    /// DSEL-03's verifier, factored out so a <c>[RuleMutant]</c> can drive it with samples that violate the rule. Asserts on the RATIO rather than an
    /// absolute dt, which is what makes it exact rather than timing-dependent: both figures come from one <c>_currentDeltaTime</c> inside one tick, so
    /// jitter in the tick interval cancels.
    /// </summary>
    private static void AssertStepsBy((long Tick, float Dt, float Amortized)[] samples, int expectedStride, string shape)
    {
        Assert.That(samples, Is.Not.Empty, "no run observed a non-zero DeltaTime — the probe never saw a real tick");
        foreach (var (tick, dt, amortized) in samples)
        {
            Assert.That(amortized, Is.EqualTo(dt * expectedStride).Within(1e-5f),
                $"{shape}, tick {tick}: {StepMarker} {expectedStride} tick periods, not {amortized / dt:0.##}");
        }
    }

    /// <summary>
    /// The assertion is on the RATIO rather than on an absolute dt, which is what makes it exact rather than timing-dependent: both figures are computed
    /// from one <c>_currentDeltaTime</c> inside one tick, so jitter in the tick interval cancels.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-03")]
    public void TickDivisor_ScalesAmortizedDeltaTime([Values] QueryShape shape)
    {
        const int divisor = 4;
        using var dbe = SetupEngineWithGrid();
        SpawnRow(dbe, 3, SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var probe = new DtProbe();
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").QuerySystem("Divided", probe.Record, input: () => view, tickDivisor: divisor,
                       parallel: shape != QueryShape.Serial, writesVersioned: shape == QueryShape.ParallelVersioned);
               }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            WaitFor(() => probe.Count >= 4);
            runtime.Shutdown();
        }

        AssertStepsBy(probe.Moving(), divisor, $"{shape} at tickDivisor {divisor}");
    }

    /// <summary>The chunked-callback path builds a bare TickContext of its own, so it gets its own case.</summary>
    [Test]
    [VerifiesRule("DSEL-03")]
    public void TickDivisor_ScalesAmortizedDeltaTime_ChunkedCallback()
    {
        using var dbe = SetupEngineWithGrid();
        var system = new DividedChunkedCallback();
        using (var runtime = TyphonRuntime.Create(dbe, schedule => schedule.PublicTrack.DeclareDag("Test").Add(system),
                   new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 }))
        {
            runtime.Start();
            WaitFor(() => system.Probe.Count >= 4);
            runtime.Shutdown();
        }

        AssertStepsBy(system.Probe.Moving(), DividedChunkedCallback.Divisor, "chunked callback");
    }

    private sealed class DividedChunkedCallback : CallbackSystem
    {
        internal const int Divisor = 3;

        internal readonly DtProbe Probe = new();

        protected override void Configure(SystemBuilder b) => b
            .Name("DividedChunked")
            .ChunkedParallel(2)
            .TickDivisor(Divisor);

        protected override void Execute(TickContext ctx) => Probe.Record(ctx);
    }

    /// <summary>
    /// <c>DeltaTime</c> answers "how long since the last tick", which has one answer for every system whatever its rate — a timeout or a rate limit in a
    /// divided system must not see a stretched clock. Compared on a tick BOTH systems ran, so the two figures are from the same tick.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-03")]
    public void TickDivisor_DoesNotScaleDeltaTime()
    {
        using var dbe = SetupEngineWithGrid();
        SpawnRow(dbe, 3, SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var every = new DtProbe();
        var divided = new DtProbe();
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   var dag = schedule.PublicTrack.DeclareDag("Test");
                   dag.QuerySystem("EveryTick", every.Record, input: () => view);
                   dag.QuerySystem("Divided", divided.Record, input: () => view, tickDivisor: 4);
               }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            WaitFor(() => divided.Count >= 3);
            runtime.Shutdown();
        }

        var undividedByTick = every.Moving().ToDictionary(s => s.Tick, s => s.Dt);
        var shared = divided.Moving().Where(s => undividedByTick.ContainsKey(s.Tick)).ToArray();
        Assert.That(shared, Is.Not.Empty, "the two systems never ran in the same tick — nothing to compare");
        foreach (var (tick, dt, _) in shared)
        {
            Assert.That(dt, Is.EqualTo(undividedByTick[tick]).Within(1e-6f),
                $"tick {tick}: DeltaTime is the tick's own interval and a divisor must not stretch it");
        }
    }

    /// <summary>
    /// The two strides compose rather than compete: the divisor skips runs, <c>cellAmortize</c> skips clusters within a run, so a cluster here is revisited
    /// once every six ticks. Taking the larger of the two instead of the product would under-step by 3x and pass any "it moved" assertion.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-03")]
    public void TickDivisorAndCellAmortize_StridesMultiply()
    {
        using var dbe = SetupEngineWithGrid();
        SpawnRow(dbe, 6, SimTier.Tier2);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var probe = new DtProbe();
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").QuerySystem("Both", probe.Record, input: () => view, parallel: true,
                       tier: SimTier.Tier2, cellAmortize: 3, tickDivisor: 2);
               }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            WaitFor(() => probe.Count >= 4);
            runtime.Shutdown();
        }

        AssertStepsBy(probe.Moving(), 6, "divisor 2 x cellAmortize 3 revisits a cluster every 6 ticks");
    }

    /// <summary>
    /// A divided system runs on its divisor's ticks and no others — the counterpart to the dt scaling, since the two are only correct together.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-03")]
    public void TickDivisor_RunsOnlyOnItsOwnTicks()
    {
        const int divisor = 5;
        using var dbe = SetupEngineWithGrid();
        SpawnRow(dbe, 2, SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var probe = new DtProbe();
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").QuerySystem("Divided", probe.Record, input: () => view, tickDivisor: divisor);
               }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            runtime.Start();
            WaitFor(() => probe.Count >= 4);
            runtime.Shutdown();
        }

        var ticks = probe.Samples.Select(s => s.Tick).Distinct().OrderBy(t => t).ToArray();
        Assert.That(ticks.Length, Is.GreaterThanOrEqualTo(4));
        foreach (var tick in ticks)
        {
            Assert.That(tick % divisor, Is.Zero, $"a system at tickDivisor {divisor} ran on tick {tick}");
        }
    }

    /// <summary>
    /// A change filter's dirty set is published once by the fence and consumed by the next tick's dispatch, so a system that skips ticks does not DEFER
    /// those changes — it never sees them. Refused for the same reason, and in the same place, as cellAmortize and checkerboard.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-03")]
    public void TickDivisorWithChangeFilter_IsRefusedAtBuild()
    {
        using var dbe = SetupEngineWithGrid();
        using var tx = dbe.CreateQuickTransaction();
        using var view = tx.Query<TierUnit>().ToView();
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").QuerySystem("Bad", _ => { }, input: () => view, changeFilter: [typeof(TierPos)], tickDivisor: 4);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });

        Assert.That(ex?.Message, Does.Contain("tickDivisor 4").And.Contain("change filter"));
        Assert.That(ex?.Message, Does.Contain("cellAmortize"), "the message must name the alternative, or the author has nowhere to go");
    }

    /// <summary>
    /// The throttled gate strides too, and it was the half #1293's first cut forgot: a system at <c>throttledTickDivisor 3</c> integrated at a third of real
    /// time from the moment overload engaged. A run must pass BOTH gates, so the spacing is their LCM — 6 here, not 2 and not 3.
    /// </summary>
    [Test]
    [VerifiesRule("DSEL-03")]
    public void ThrottledTickDivisor_ScalesAmortizedDeltaTimeUnderOverload()
    {
        using var dbe = SetupEngineWithGrid();
        SpawnRow(dbe, 3, SimTier.Tier0);

        using var txView = dbe.CreateQuickTransaction();
        using var view = txView.Query<TierUnit>().ToView();
        var probe = new DtProbe();
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").QuerySystem("Throttled", probe.Record, input: () => view, tickDivisor: 2, throttledTickDivisor: 3);
               }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
        {
            // Forced rather than provoked: making ticks genuinely overrun means a slow system body, which is a timing-dependent precondition for a test
            // whose subject is arithmetic. STAGE the regime, then observe it.
            runtime.Scheduler.ForceOverloadLevelForTests(OverloadLevel.SystemThrottling);
            runtime.Start();
            WaitFor(() => probe.Count >= 3);
            runtime.Shutdown();
        }

        var ticks = probe.Samples.Select(s => s.Tick).Distinct().ToArray();
        Assert.That(ticks, Is.All.Matches<long>(t => t % 6 == 0), "both gates admit only the ticks divisible by 2 and by 3");
        AssertStepsBy(probe.Moving(), 6, "tickDivisor 2 x throttledTickDivisor 3 under overload");
    }

    /// <summary>
    /// Declaring <c>cellAmortize</c> is not the same as striding. The bucketing lives on the tier-scoped path and needs a tier index to bucket, so a system
    /// over a NON-spatial archetype covers every cluster every run however it was declared — and the step must then be the rate divisor alone. Multiplying by
    /// the declared amortization regardless would over-step 3x, which is the same class of silent proportional error #1293 is about.
    /// </summary>
    /// <remarks>
    /// It takes BOTH a non-spatial archetype and a grid-less engine, and the first attempt at this test had neither right. With a grid configured, a
    /// cluster-eligible archetype gets a tier index whatever its fields, so the stride applied and the system stepped by 6 — correctly. And a
    /// <see cref="TierUnit"/> cannot be used at all, because it declares a <c>[SpatialIndex]</c> field and <c>InitializeArchetypes</c> refuses one without a
    /// grid. <c>EcsUnit</c> plus no grid is the combination that actually reaches the <c>else</c>.
    /// </remarks>
    [Test]
    [VerifiesRule("DSEL-03")]
    public void CellAmortizeWithoutATierIndex_DoesNotClaimAStrideItDidNotTake()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        // No TierPos and no ConfigureSpatialGrid: EcsUnit declares no [SpatialIndex], so it is the one archetype this engine can hold without a grid — and
        // without a grid EffectiveTier collapses to All, which is the condition under test.
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        using (dbe)
        {
            using (var txSpawn = dbe.CreateQuickTransaction())
            {
                txSpawn.Spawn<EcsUnit>();
                txSpawn.Commit();
            }

            using var txView = dbe.CreateQuickTransaction();
            using var view = txView.Query<EcsUnit>().ToView();
            var probe = new DtProbe();
            using (var runtime = TyphonRuntime.Create(dbe, schedule =>
                   {
                       schedule.PublicTrack.DeclareDag("Test").QuerySystem("Declared", probe.Record, input: () => view,
                           tier: SimTier.Tier2, cellAmortize: 3, tickDivisor: 2);
                   }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 }))
            {
                runtime.Start();
                WaitFor(() => probe.Count >= 3);
                runtime.Shutdown();
            }

            AssertStepsBy(probe.Moving(), 2, "cellAmortize declared but never applied: the step is the divisor alone");
        }
    }

    /// <summary>
    /// The pre-fix behaviour, fed straight to the verifier: <c>AmortizedDeltaTime</c> equal to <c>DeltaTime</c> for a system at divisor 4. That is what every
    /// path did before #1293, and it has to be rejected — a verifier that accepts it would have been green against the bug.
    /// </summary>
    [Test]
    [RuleMutant("DSEL-03")]
    public void AnUnscaledStep_IsCaughtByTheVerifier() =>
        RuleMutants.AssertDetects("DSEL-03", StepMarker, () =>
            AssertStepsBy([(10L, 0.001f, 0.001f), (14L, 0.001f, 0.001f)], 4, "mutant: the step the divisor was never applied to"));

    [Test]
    public void TickDivisorBelowOne_IsRefusedAtBuild()
    {
        using var dbe = SetupEngineWithGrid();
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Bad", _ => { }, tickDivisor: 0);
            }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });
        });

        Assert.That(ex?.Message, Does.Contain("tickDivisor"));
    }
}
