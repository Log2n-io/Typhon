using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The AABB refresh must do work proportional to what MOVED, not to what EXISTS.
/// </summary>
/// <remarks>
/// <para><b>Why this fixture exists.</b> The non-barrier arm of <c>RecomputeDirtyClusterAabbsSlice</c> iterates
/// <c>ActiveClusterIds</c>, which has no dirty filter of its own. Before the fence system landed the method walked the
/// <c>dirtyBits</c> array it is still named after and skipped <c>dirtyBits[chunkId] == 0</c>; the rewrite replaced that walk
/// with <c>ActiveClusterCount</c> and reduced the parameter to <c>_ = dirtyBits;</c>. Nothing failed, because the two sets
/// are the same set on a fully-moving world — 2 007 dirty clusters of 2 020 active at 100 % moving — and every campaign ran
/// at that point. At 1 % moving it was 531 of 1 969: the fence walked ~46 700 entity slots per tick to prove nothing in them
/// had changed.</para>
/// <para><b>Nothing in the suite could see it.</b> The result was correct at every moving fraction — a recompute of an
/// untouched cluster returns the bound it already had — so only a COST assertion can catch this class of defect, and
/// <c>ClustersScanned</c> cannot make it: that counter is incremented after the <c>boundsMoved</c> skip, so it counts
/// clusters that had something to say rather than clusters the pass opened and read. <c>SlotsScanned</c> was added with the
/// fix for exactly this reason and is what these tests assert on.</para>
/// <para><b>The destroy case is the trap, and it is why the gate is three signals rather than one.</b> A destroy sets no
/// dirty bit (<c>ReleaseSlot</c> never calls <c>SetDirty</c>, and Prep step 2 ANDs the dirty word with occupancy, so a freed
/// slot's bit is masked away regardless) and no process bit (that is <c>WriteSpatial</c>'s). Gating on dirty bits alone
/// would therefore leave a dead entity's position inside its cluster's bound indefinitely — a silent selectivity loss, not
/// a crash. <c>ReleaseSlot</c> now calls <c>FlagClusterShrinkAxesOnly</c>, and
/// <see cref="ADestroyedEntitysPositionDoesNotStayInsideTheClusterBound"/> is the test that fails without it.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterAabbRefreshDirtyGateTests : TestBase<ClusterAabbRefreshDirtyGateTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    /// <summary>
    /// Repair is pinned off for the same reason the drift fixtures pin it: a re-pack changes the cluster shapes these tests
    /// assert on, and the mechanism under test here is the refresh gate, not the planner.
    /// </summary>
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize,
            reclusterBudgetMs: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<ClMigUnit>.Metadata.ArchetypeId].ClusterState;

    private static int SlotsScanned(DatabaseEngine dbe) =>
        dbe.GetSpatialTelemetry(Archetype<ClMigUnit>.Metadata.ArchetypeId).SlotsScanned;

    /// <summary>
    /// Spawns <paramref name="perCell"/> entities into each of <paramref name="cells"/> cells, one cell per column of the
    /// grid, and settles them with one fence so every bound is exact before the tick under test.
    /// </summary>
    private static EntityId[] SpawnAcrossCells(DatabaseEngine dbe, int cells, int perCell)
    {
        var ids = new EntityId[cells * perCell];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var c = 0; c < cells; c++)
            {
                // Inside one cell, and inside the drift target region (a quarter of the cell) so the scan's second level
                // never opens a cluster for drift and SlotsScanned measures the refresh alone.
                var baseX = c * CellSize + 10f;
                for (var i = 0; i < perCell; i++)
                {
                    var idx = c * perCell + i;
                    ids[idx] = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(baseX + i * 0.25f, 10f + i * 0.25f, idx)));
                }
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        return ids;
    }

    [Test]
    public void ATickWithNoWritesWalksNoSlotsAtAll()
    {
        using var dbe = SetupEngine();
        SpawnAcrossCells(dbe, cells: 8, perCell: 16);
        var cs = ClusterStateOf(dbe);
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(1), "the fixture must build several clusters, or there is nothing to skip");

        // Tick 2 writes nothing. Every stored bound is already exact, so the pass has nothing to re-derive.
        dbe.WriteTickFence(2);

        Assert.That(SlotsScanned(dbe), Is.Zero,
            "a settled world re-read entity positions it had no reason to believe had changed — the refresh has lost its dirty gate");
    }

    [Test]
    public void OnlyTheClusterThatWasWrittenIsWalked()
    {
        using var dbe = SetupEngine();
        var ids = SpawnAcrossCells(dbe, cells: 8, perCell: 16);
        var cs = ClusterStateOf(dbe);
        var population = ids.Length;

        // One entity, moved through OpenMut — the writer that sets a dirty bit and deliberately no process bit.
        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(ids[0]);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 12f, MinY = 12f, MaxX = 12f, MaxY = 12f };
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        var scanned = SlotsScanned(dbe);
        Assert.That(scanned, Is.GreaterThan(0), "the cluster holding the moved entity must be re-derived");
        Assert.That(scanned, Is.LessThanOrEqualTo(64), "at most one cluster's worth of slots — a cluster holds 64");
        Assert.That(scanned, Is.LessThan(population),
            $"one write walked {scanned} of {population} entity slots; the pass is costing what the world costs, not what the write costs");
    }

    [Test]
    public void AMovedEntityStillTightensItsClusterBound()
    {
        // The gate must not buy its saving by skipping a cluster that DID change. This is the same assertion
        // ClusterSpatialAabbRecomputeTests makes, restated here so a regression in the gate names the gate.
        using var dbe = SetupEngine();

        EntityId near, far;
        using (var tx = dbe.CreateQuickTransaction())
        {
            near = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(10f, 10f, 0)));
            far = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(30f, 30f, 1)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        var chunkId = cs.ActiveClusterIds[0];
        Assert.That(cs.ClusterAabbs[chunkId].MaxX, Is.EqualTo(30f), "precondition: the bound spans both spawns");

        using (var tx = dbe.CreateQuickTransaction())
        {
            var eref = tx.OpenMut(far);
            ref var pos = ref eref.Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = 14f, MinY = 14f, MaxX = 14f, MaxY = 14f };
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        Assert.That(cs.ClusterAabbs[chunkId].MaxX, Is.EqualTo(14f), "the bound must follow the entity that moved");
        Assert.That(cs.ClusterAabbs[chunkId].MinX, Is.EqualTo(10f), "the entity that did not move must still be contained");
        _ = near;
    }

    [Test]
    public void ADestroyedEntitysPositionDoesNotStayInsideTheClusterBound()
    {
        // THE test for the gate's second signal. A destroy sets neither a dirty bit nor a process bit, so with the gate in
        // place and no shrink flag from ReleaseSlot this cluster is never revisited and keeps a bound sized to an entity
        // that no longer exists. Remove the FlagClusterShrinkAxesOnly call in ReleaseSlot and this reddens; nothing else in
        // the suite does.
        using var dbe = SetupEngine();

        EntityId stay, doomed;
        using (var tx = dbe.CreateQuickTransaction())
        {
            stay = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(10f, 10f, 0)));
            doomed = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(40f, 40f, 1)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        var chunkId = cs.ActiveClusterIds[0];
        Assert.That(cs.ClusterAabbs[chunkId].MaxX, Is.EqualTo(40f), "precondition: the bound reaches the entity about to be destroyed");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(doomed);
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        Assert.That(cs.ClusterAabbs[chunkId].MaxX, Is.EqualTo(10f),
            "the bound still reaches a destroyed entity's position — every query overlapping that region opens this cluster for nothing");
        Assert.That(cs.ClusterAabbs[chunkId].MaxY, Is.EqualTo(10f), "same on Y — the shrink must re-derive every axis, not the one that was checked");
        _ = stay;
    }

    [Test]
    public void ADestroyIsTheOnlyClusterWalkedWhenNothingElseMoved()
    {
        // The shrink signal must be as narrow as the dirty bit: flagging it must not re-arm a full-archetype walk.
        using var dbe = SetupEngine();
        var ids = SpawnAcrossCells(dbe, cells: 8, perCell: 16);

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(ids[0]);
            tx.Commit();
        }

        dbe.WriteTickFence(2);

        var scanned = SlotsScanned(dbe);
        Assert.That(scanned, Is.GreaterThan(0), "the cluster that lost a slot must be re-derived");
        Assert.That(scanned, Is.LessThanOrEqualTo(64), "one destroy must walk one cluster, not the archetype");
    }


}
