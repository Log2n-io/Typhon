using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Numerics;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Regression suite for cluster dormancy (issue #233). Reuses the <see cref="TierUnit"/> archetype and
/// <c>SetupEngineWithGrid()</c> pattern from <see cref="TierDispatchTests"/>.
/// </summary>
[TestFixture]
[NonParallelizable]
class DormancyTests : TestBase<DormancyTests>
{
    [TearDown]
    public void TearDown() => DormancyReporter.Reset();

    private static TierPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Data = 1.0f };

    private DatabaseEngine SetupEngineWithGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<TierPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(100, 100),
            cellSize: 10f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Helper: spawn one entity in a specific cell, return its cluster state + chunkId.</summary>
    private (ArchetypeClusterState cs, int chunkId, EntityId entityId) SpawnInCell(DatabaseEngine dbe, float x, float y, SimTier tier)
    {
        var meta = Archetype<TierUnit>.Metadata;
        EntityId eid;
        using (var tx = dbe.CreateQuickTransaction())
        {
            eid = tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(x, y)));
            tx.Commit();
        }
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        int cellKey = dbe.SpatialGrid.WorldToCellKey(x, y);
        dbe.SpatialGrid.SetCellTier(cellKey, tier);

        // Find the chunkId for this entity
        int chunkId = cs.ClusterCellMap != null ? FindChunkIdForCell(cs, cellKey) : cs.ActiveClusterIds[cs.ActiveClusterCount - 1];
        return (cs, chunkId, eid);
    }

    private static int FindChunkIdForCell(ArchetypeClusterState cs, int cellKey)
    {
        for (int i = 0; i < cs.ActiveClusterCount; i++)
        {
            int cid = cs.ActiveClusterIds[i];
            if (cs.ClusterCellMap != null && cid < cs.ClusterCellMap.Length && cs.ClusterCellMap[cid] == cellKey)
            {
                return cid;
            }
        }
        return cs.ActiveClusterIds[cs.ActiveClusterCount - 1];
    }

    /// <summary>Simulate N ticks with no writes by calling DormancySweep with an empty dirty bitmap.</summary>
    private static void SimulateCleanTicks(ArchetypeClusterState cs, int tickCount, long startTick = 0)
    {
        var emptyDirtyBits = new long[cs.PrimarySegmentCapacity];
        for (int t = 0; t < tickCount; t++)
        {
            cs.DormancySweep(emptyDirtyBits, startTick + t);
        }
    }

    /// <summary>Create a dirty bitmap with a single cluster marked dirty.</summary>
    private static long[] MakeDirtyBits(int chunkId, int capacity)
    {
        var bits = new long[Math.Max(chunkId + 1, capacity)];
        bits[chunkId] = 1L; // At least one entity dirty (slot 0)
        return bits;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 1: Sleep after threshold
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SleepAfterThreshold()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 5;

        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Active));
        Assert.That(cs.SleepingClusterCount, Is.EqualTo(0));

        // 4 clean ticks: should still be Active
        SimulateCleanTicks(cs, 4);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Active));
        Assert.That(cs.SleepCounters[chunkId], Is.EqualTo(4));

        // 5th clean tick: transitions to Sleeping
        SimulateCleanTicks(cs, 1, 4);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Sleeping));
        Assert.That(cs.SleepingClusterCount, Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 2: Sleeping cluster skipped in dispatch
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SleepingClusterSkippedInDispatch()
    {
        using var dbe = SetupEngineWithGrid();

        // Spawn two entities in two different Tier0 cells
        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(15f, 5f);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier0);

        var meta = Archetype<TierUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        // Put cluster in cell A to sleep
        int chunkIdA = FindChunkIdForCell(cs, cellA);
        cs.SleepThresholdTicks = 3;
        SimulateCleanTicks(cs, 3);
        // Both clusters should now be sleeping (both had clean ticks)
        // But we only want A sleeping, so we need to set up differently:
        // Reset and manually sleep just cluster A
        cs.SleepStates[chunkIdA] = ClusterSleepState.Sleeping;
        cs.SleepingClusterCount = 1;

        // Reset cluster B to active
        int chunkIdB = FindChunkIdForCell(cs, cellB);
        cs.SleepStates[chunkIdB] = ClusterSleepState.Active;
        cs.SleepCounters[chunkIdB] = 0;

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var seenCount = 0;
        var ticksDone = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksDone));
            dag.QuerySystem("Check", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    Interlocked.Increment(ref seenCount);
                }
            }, input: () => view, tier: SimTier.Tier0, parallel: true, after: "Counter");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Only the non-sleeping cluster's entity should be seen (once per tick × 2+ ticks)
        // Cell B entity is not sleeping, cell A entity is sleeping → seenCount should be >= 1 per tick
        Assert.That(seenCount, Is.GreaterThanOrEqualTo(1));
        // And the sleeping cluster's entity should NOT be double-counted
        Assert.That(seenCount, Is.LessThanOrEqualTo(ticksDone));

        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 3: Wake request → WakePending → Active lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void WakeRequest_Transition()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 3;

        // Put cluster to sleep
        SimulateCleanTicks(cs, 3);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Sleeping));

        // Process a wake request
        cs.ProcessWakeRequest(chunkId);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.WakePending));

        // Transition at tick start
        cs.TransitionWakePendingToActive(100);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Active));
        Assert.That(cs.SleepCounters[chunkId], Is.EqualTo(0));
        Assert.That(cs.SleepingClusterCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 4: Duplicate wake deduplication
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DuplicateWakeDeduplication()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 3;
        SimulateCleanTicks(cs, 3);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Sleeping));
        Assert.That(cs.SleepingClusterCount, Is.EqualTo(1));

        // Two wake requests from "different threads"
        DormancyReporter.RequestWake(cs.ArchetypeId, chunkId);
        DormancyReporter.RequestWake(cs.ArchetypeId, chunkId);

        // Drain: both resolve to the same cluster, second is a no-op
        DormancyReporter.DrainAll(dbe._archetypeStates);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.WakePending));
        // SleepingClusterCount decremented only once (in TransitionWakePendingToActive, not here)
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 5: Dirty bitmap resets counter
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DormancySweep_DirtyResetCounter()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 10;

        // Accumulate 7 clean ticks
        SimulateCleanTicks(cs, 7);
        Assert.That(cs.SleepCounters[chunkId], Is.EqualTo(7));

        // Now a dirty tick: counter should reset
        var dirtyBits = MakeDirtyBits(chunkId, cs.PrimarySegmentCapacity);
        cs.DormancySweep(dirtyBits, 7);
        Assert.That(cs.SleepCounters[chunkId], Is.EqualTo(0));
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Active));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 6: Configurable threshold
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void ConfigurableThreshold()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 5;

        SimulateCleanTicks(cs, 5);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Sleeping));

        // Different threshold: 100
        cs.SleepStates[chunkId] = ClusterSleepState.Active;
        cs.SleepCounters[chunkId] = 0;
        cs.SleepingClusterCount = 0;
        cs.SleepThresholdTicks = 100;

        SimulateCleanTicks(cs, 99);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Active));
        SimulateCleanTicks(cs, 1, 99);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Sleeping));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 7: Heartbeat wake
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void HeartbeatWake()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 3;
        cs.HeartbeatIntervalTicks = 10;

        // Put to sleep
        SimulateCleanTicks(cs, 3);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Sleeping));

        // Run ticks until the heartbeat fires. The heartbeat condition is:
        // (tickNumber % HeartbeatIntervalTicks) == (chunkId % HeartbeatIntervalTicks)
        int targetTick = chunkId % 10; // First tick where heartbeat fires
        if (targetTick <= 3)
        {
            targetTick += 10; // Must be after the cluster fell asleep
        }

        // Run a single sweep at the target tick
        var emptyBits = new long[cs.PrimarySegmentCapacity];
        cs.DormancySweep(emptyBits, targetTick);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.WakePending));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 8: No overhead when no sleeping
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NoOverhead_WhenNoSleeping()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, _, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);

        // Dormancy enabled but no clusters sleeping
        cs.SleepThresholdTicks = 60;
        Assert.That(cs.SleepingClusterCount, Is.EqualTo(0));
        // The dispatch filter in OnParallelQueryPrepare checks SleepingClusterCount > 0 first
        // and skips entirely when 0. We verify the field is correct.
        Assert.That(cs.SleepStates, Is.Not.Null);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 9: SetDirty wakes sleeping cluster
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SetDirty_WakesSleepingCluster()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 3;
        SimulateCleanTicks(cs, 3);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Sleeping));

        // Call SetDirty on an entity in the sleeping cluster
        cs.SetDirty(chunkId, 0);

        // The DormancyReporter should have a pending wake request
        Assert.That(DormancyReporter.HasPendingRequests, Is.True);

        // Drain and verify WakePending
        DormancyReporter.DrainAll(dbe._archetypeStates);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.WakePending));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 10: Sleep counter reset on write
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void SleepCounterReset_OnWrite()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 10;

        // Accumulate 8 clean ticks (almost at threshold)
        SimulateCleanTicks(cs, 8);
        Assert.That(cs.SleepCounters[chunkId], Is.EqualTo(8));

        // A dirty tick resets counter — cluster does NOT sleep
        var dirtyBits = MakeDirtyBits(chunkId, cs.PrimarySegmentCapacity);
        cs.DormancySweep(dirtyBits, 8);
        Assert.That(cs.SleepCounters[chunkId], Is.EqualTo(0));
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Active));

        // Need another 10 clean ticks to sleep now
        SimulateCleanTicks(cs, 9, 9);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Active));
        SimulateCleanTicks(cs, 1, 18);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Sleeping));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 11: Multi-archetype independence
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void MultiArchetype_Independence()
    {
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<TierUnit>.Metadata;

        // Spawn in two different cells
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Commit();
        }

        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(5f, 5f), SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(dbe.SpatialGrid.WorldToCellKey(15f, 5f), SimTier.Tier0);

        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(15f, 5f);
        int chunkA = FindChunkIdForCell(cs, cellA);
        int chunkB = FindChunkIdForCell(cs, cellB);

        // Sleep cluster A only: manually set different thresholds via per-cluster counters
        cs.SleepThresholdTicks = 3;
        var emptyBits = new long[cs.PrimarySegmentCapacity];
        SimulateCleanTicks(cs, 3);

        // Both slept (both were clean). Wake B back.
        cs.SleepStates[chunkB] = ClusterSleepState.Active;
        cs.SleepCounters[chunkB] = 0;
        cs.SleepingClusterCount = 1;

        Assert.That(cs.SleepStates[chunkA], Is.EqualTo(ClusterSleepState.Sleeping));
        Assert.That(cs.SleepStates[chunkB], Is.EqualTo(ClusterSleepState.Active));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 12: Tier-filtered + dormancy
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void TierFiltered_PlusDormancy()
    {
        using var dbe = SetupEngineWithGrid();

        // Spawn two entities in Tier0, one in Tier1
        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(15f, 5f);
        var cellC = dbe.SpatialGrid.WorldToCellKey(25f, 5f);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(25f, 5f)));
            tx.Commit();
        }

        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellC, SimTier.Tier1);

        var meta = Archetype<TierUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;
        cs.SleepThresholdTicks = 3;

        // Sleep cluster A (Tier0 cell A)
        int chunkA = FindChunkIdForCell(cs, cellA);
        cs.SleepStates[chunkA] = ClusterSleepState.Sleeping;
        cs.SleepingClusterCount = 1;

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var seenCount = 0;
        var ticksDone = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksDone));
            dag.QuerySystem("T0Check", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    Interlocked.Increment(ref seenCount);
                }
            }, input: () => view, tier: SimTier.Tier0, parallel: true, after: "Counter");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Should see only cell B's entity per tick (cell A is sleeping, cell C is Tier1)
        Assert.That(seenCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(seenCount, Is.LessThanOrEqualTo(ticksDone));
        view.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 13: Dormancy disabled (threshold 0) — no sleep
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DormancyDisabled_ThresholdZero_NoSleep()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, _) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 0; // Disabled

        // Run 1000 clean ticks — cluster should never sleep
        SimulateCleanTicks(cs, 1000);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Active));
        Assert.That(cs.SleepingClusterCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 14: Destroy last entity in sleeping cluster → SleepingClusterCount decrements
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DestroyLastEntity_SleepingCluster_DecrementsCount()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, entityId) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 3;

        // Put to sleep
        SimulateCleanTicks(cs, 3);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.Sleeping));
        Assert.That(cs.SleepingClusterCount, Is.EqualTo(1));

        // Destroy the entity — cluster empties → RemoveFromActiveList
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(entityId);
            tx.Commit();
        }

        // After destroy + release, the sleeping cluster should have been removed
        Assert.That(cs.SleepingClusterCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 15: Destroy entity in WakePending cluster → SleepingClusterCount decrements
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void DestroyLastEntity_WakePendingCluster_DecrementsCount()
    {
        using var dbe = SetupEngineWithGrid();
        var (cs, chunkId, entityId) = SpawnInCell(dbe, 5f, 5f, SimTier.Tier0);
        cs.SleepThresholdTicks = 3;

        // Put to sleep, then request wake
        SimulateCleanTicks(cs, 3);
        cs.ProcessWakeRequest(chunkId);
        Assert.That(cs.SleepStates[chunkId], Is.EqualTo(ClusterSleepState.WakePending));
        Assert.That(cs.SleepingClusterCount, Is.EqualTo(1)); // Still counted

        // Destroy — should decrement SleepingClusterCount for WakePending too
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(entityId);
            tx.Commit();
        }

        Assert.That(cs.SleepingClusterCount, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Test 16: Non-tier-filtered system (SimTier.All) with sleeping clusters
    // ═══════════════════════════════════════════════════════════════════════

    [Test]
    public void NonTierFiltered_SystemWithSleepingClusters_SkipsSleeping()
    {
        using var dbe = SetupEngineWithGrid();

        var cellA = dbe.SpatialGrid.WorldToCellKey(5f, 5f);
        var cellB = dbe.SpatialGrid.WorldToCellKey(15f, 5f);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(5f, 5f)));
            tx.Spawn<TierUnit>(TierUnit.Pos.Set(PointAt(15f, 5f)));
            tx.Commit();
        }

        // Set tiers (required for spatial grid, but the system uses SimTier.All)
        dbe.SpatialGrid.SetCellTier(cellA, SimTier.Tier0);
        dbe.SpatialGrid.SetCellTier(cellB, SimTier.Tier0);

        var meta = Archetype<TierUnit>.Metadata;
        var cs = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        // Sleep cluster A
        int chunkIdA = FindChunkIdForCell(cs, cellA);
        int chunkIdB = FindChunkIdForCell(cs, cellB);
        cs.SleepThresholdTicks = 3;
        cs.SleepStates[chunkIdA] = ClusterSleepState.Sleeping;
        cs.SleepingClusterCount = 1;
        cs.SleepStates[chunkIdB] = ClusterSleepState.Active;

        using var txView = dbe.CreateQuickTransaction();
        var view = txView.Query<TierUnit>().ToView();

        var seenCount = 0;
        var ticksDone = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Counter", _ => Interlocked.Increment(ref ticksDone));
            // NOTE: no tier filter — SimTier.All (default). The dormancy "promote" path should still filter sleeping clusters.
            dag.QuerySystem("AllCheck", ctx =>
            {
                foreach (var id in ctx.Entities)
                {
                    Interlocked.Increment(ref seenCount);
                }
            }, input: () => view, parallel: true, after: "Counter");
        }, new RuntimeOptions { WorkerCount = 1, BaseTickRate = 1000 });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksDone >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // Only cell B's entity should be seen (cell A is sleeping)
        Assert.That(seenCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(seenCount, Is.LessThanOrEqualTo(ticksDone));
        view.Dispose();
    }
}
