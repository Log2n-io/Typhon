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
            dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f + 10f * i, 5f, 0f), tier);
        }

        return ids;
    }

    private static ArchetypeClusterState ClusterState(DatabaseEngine dbe) => dbe._archetypeStates[Archetype<TierUnit>.Metadata.ArchetypeId].ClusterState;

    private static int ChunkOf(DatabaseEngine dbe, float x, float y)
    {
        var cs = ClusterState(dbe);
        var cellKey = dbe.SpatialGrid.WorldToCellKey(x, y, 0f);
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

        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(55f, 55f, 0f), SimTier.Tier3);

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
}
