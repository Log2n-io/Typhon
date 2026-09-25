using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// The spatial cell layer after a crash recovery (#1054) and the rebuild's per-slot cell check (Realms P0.2, README §9.3-2).
/// </summary>
/// <remarks>
/// A hard crash before the first checkpoint leaves the cluster segment empty at open: InitializeArchetypes' spatial rebuild sees nothing, and the WAL
/// replay then claims every spawn back into clusters with the cell-agnostic <c>ClaimSlot</c>. Before the fix nothing rebuilt the cell layer after that
/// replay, so the reopened database answered every spatial query with nothing; and a cluster claimed that way mixes cells, which the rebuild used to file
/// under its first slot's cell with no further check (CC-02 broken silently until the entities were next written).
/// </remarks>
[TestFixture]
[NonParallelizable]
class RecoverySpatialRebuildTests : TestBase<RecoverySpatialRebuildTests>
{
    /// <summary>Reopen needs WAL segments that outlive an engine dispose; the base class defaults to an in-memory backend that does not.</summary>
    protected override IWalFileIO CreateWalFileIO() => new WalFileIO();

    /// <summary>No periodic checkpoint: one landing between the commit and the crash would empty the replay window, and the tests would stop testing it.</summary>
    protected override void ConfigureEngineOptions(DatabaseEngineOptions o)
    {
        base.ConfigureEngineOptions(o);
        o.Resources.CheckpointIntervalMs = int.MaxValue;
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ClMigPos PointAt(float x, float y, int tag) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private static void Configure(DatabaseEngine dbe)
    {
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize, migrationHysteresisRatio: 0f));
        dbe.InitializeArchetypes();
    }

    /// <summary>Spawns every position under Commit discipline (SV values survive a hard crash only through their own WAL record), then crashes.</summary>
    private void SpawnThenCrash(IReadOnlyList<(float x, float y)> positions)
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(dbe);
        using (var uow = dbe.CreateUnitOfWork())
        {
            using (var tx = uow.CreateTransaction(CommitDiscipline.Commit))
            {
                for (var i = 0; i < positions.Count; i++)
                {
                    tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(positions[i].x, positions[i].y, i)));
                }

                Assert.That(tx.Commit(), Is.True);
            }

            uow.Flush();
        }

        // Power cut before any checkpoint: the cluster pages are lost, the WAL holds the spawns.
        dbe.SimulateHardCrash();
    }

    private static HashSet<int> QueryTags(DatabaseEngine dbe, float minX, float minY, float maxX, float maxY)
    {
        var cs = dbe._archetypeStates[ArchetypeId].ClusterState;
        var entities = new HashSet<EntityId>();
        using (EpochGuard.Enter(dbe.EpochManager))
        {
            foreach (var r in cs.QueryAabb(dbe.SpatialGrid, minX, minY, double.NegativeInfinity, maxX, maxY, double.PositiveInfinity))
            {
                entities.Add(r.Entity);
            }
        }

        var tags = new HashSet<int>();
        using var tx = dbe.CreateQuickTransaction();
        foreach (var e in entities)
        {
            tags.Add(tx.Open(e).Read(ClMigUnit.Pos).Tag);
        }

        return tags;
    }

    /// <summary>Asserts CC-02 in its exact form (the fixture's band is 0): every occupied slot's centre lies in the cell its cluster is mapped to.</summary>
    private static unsafe int CountSlotsOutsideTheirClusterCell(DatabaseEngine dbe)
    {
        var cs = dbe._archetypeStates[ArchetypeId].ClusterState;
        var grid = dbe.SpatialGrid;
        ref readonly var ss = ref cs.SpatialSlot;
        var outside = 0;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var chunkId = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(chunkId);
                var bits = *(ulong*)clusterBase;
                while (bits != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var field = clusterBase + cs.Layout.ComponentOffset(ss.Slot) + slot * cs.Layout.ComponentSize(ss.Slot) + ss.FieldOffset;
                    SpatialGrid.ReadSpatialCenter3D(field, ss.FieldInfo.FieldType, out var x, out var y, out var z);
                    if (grid.WorldToCellKey(x, y, z) != cs.ClusterCellMap[chunkId])
                    {
                        outside++;
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return outside;
    }

    [Test]
    [VerifiesRule("RB-01")]
    [CancelAfter(30_000)]
    public void HardCrashBeforeCheckpoint_SpatialQueriesSeeEveryRecoveredEntity()
    {
        const int population = 500;
        var positions = new List<(float, float)>();
        for (var i = 0; i < population; i++)
        {
            positions.Add((5f + (i * 37) % 990, 5f + (i * 61) % 990));
        }

        SpawnThenCrash(positions);

        using var scope = ServiceProvider.CreateScope();
        using var reopened = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(reopened);

        var cs = reopened._archetypeStates[ArchetypeId].ClusterState;
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(0), "recovery restored no cluster: the fixture did not reach the replay path");
        Assert.That(QueryTags(reopened, 0, 0, WorldMax, WorldMax), Has.Count.EqualTo(population), "the whole-world query must see every recovered entity");

        var cellEntities = 0;
        for (var key = 0; key < reopened.SpatialGrid.CellCount; key++)
        {
            cellEntities += reopened.SpatialGrid.GetCell(key).EntityCount;
        }

        Assert.That(cellEntities, Is.EqualTo(population), "the rebuilt cell counters disagree with cluster storage");
    }

    [Test]
    [VerifiesRule("CC-02")]
    [CancelAfter(30_000)]
    public void MixedCellClusterFromRecoveryClaim_FiledAtRebuild_FixedAtFirstFence()
    {
        // Four far-apart cells, a few entities each, spawned interleaved: the replay's cell-agnostic claim packs them into shared clusters.
        (float x, float y)[] cells = [(50f, 50f), (950f, 50f), (50f, 950f), (950f, 950f)];
        var positions = new List<(float, float)>();
        for (var i = 0; i < 24; i++)
        {
            var (cx, cy) = cells[i % cells.Length];
            positions.Add((cx + i % 7, cy + i % 5));
        }

        SpawnThenCrash(positions);

        using var scope = ServiceProvider.CreateScope();
        using var reopened = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(reopened);

        var cs = reopened._archetypeStates[ArchetypeId].ClusterState;
        Assert.That(cs.LastRebuildForeignCellSlots, Is.GreaterThan(0),
            "the replay did not mix cells in a cluster, so this fixture would pass with the per-slot check deleted");
        Assert.That(CountSlotsOutsideTheirClusterCell(reopened), Is.EqualTo(cs.LastRebuildForeignCellSlots),
            "the rebuild must file exactly the slots outside their cluster's cell");

        // Before the fence the strays are still findable: their cluster's AABB covers them (CA-01).
        for (var c = 0; c < cells.Length; c++)
        {
            var (cx, cy) = cells[c];
            Assert.That(QueryTags(reopened, cx - 20, cy - 20, cx + 20, cy + 20), Has.Count.EqualTo(positions.Count / cells.Length),
                $"cell {c} before the fence");
        }

        reopened.WriteTickFence(1);

        Assert.That(CountSlotsOutsideTheirClusterCell(reopened), Is.Zero, "no mixed-cell cluster may survive the first fence after open");
        for (var c = 0; c < cells.Length; c++)
        {
            var (cx, cy) = cells[c];
            Assert.That(QueryTags(reopened, cx - 20, cy - 20, cx + 20, cy + 20), Has.Count.EqualTo(positions.Count / cells.Length),
                $"cell {c} after the fence");
        }
    }

    [Test]
    [VerifiesRule("CC-02")]
    [CancelAfter(30_000)]
    public void StrayMovedBeforeTheFirstFence_IsMigratedOnce()
    {
        (float x, float y)[] cells = [(50f, 50f), (950f, 950f)];
        var positions = new List<(float, float)>();
        for (var i = 0; i < 8; i++)
        {
            var (cx, cy) = cells[i % cells.Length];
            positions.Add((cx + i, cy + i));
        }

        SpawnThenCrash(positions);

        using var scope = ServiceProvider.CreateScope();
        using var reopened = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(reopened);
        var cs = reopened._archetypeStates[ArchetypeId].ClusterState;
        Assert.That(cs.LastRebuildForeignCellSlots, Is.GreaterThan(0), "precondition: the replay mixed cells");

        // Move every entity a little, inside its own cell, before the first fence: the rebuild already filed the strays, and the detector sees the
        // writes too. One request per slot (CR-05; Debug asserts it at Prep), and every stray still lands home.
        var moved = new List<EntityId>();
        using (EpochGuard.Enter(reopened.EpochManager))
        {
            foreach (var r in cs.QueryAabb(reopened.SpatialGrid, 0, 0, double.NegativeInfinity, WorldMax, WorldMax, double.PositiveInfinity))
            {
                moved.Add(r.Entity);
            }
        }

        using (var tx = reopened.CreateQuickTransaction())
        {
            foreach (var e in moved)
            {
                ref var pos = ref tx.OpenMut(e).Write(ClMigUnit.Pos);
                pos.Bounds = new AABB2F { MinX = pos.Bounds.MinX + 0.5f, MinY = pos.Bounds.MinY, MaxX = pos.Bounds.MaxX + 0.5f, MaxY = pos.Bounds.MaxY };
            }

            tx.Commit();
        }

        reopened.WriteTickFence(1);

        Assert.That(moved, Has.Count.EqualTo(positions.Count));
        Assert.That(CountSlotsOutsideTheirClusterCell(reopened), Is.Zero);
        Assert.That(QueryTags(reopened, 0, 0, WorldMax, WorldMax), Has.Count.EqualTo(positions.Count), "no entity lost or duplicated by the migration");
    }

    [Test]
    [CancelAfter(30_000)]
    public void CleanReopen_FilesNothing()
    {
        using (var scope = ServiceProvider.CreateScope())
        {
            using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
            Configure(dbe);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < 64; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(5f + i * 15 % 990, 5f + i * 37 % 990, i)));
            }

            tx.Commit();
        }

        using var scope2 = ServiceProvider.CreateScope();
        using var reopened = scope2.ServiceProvider.GetRequiredService<DatabaseEngine>();
        Configure(reopened);
        Assert.That(reopened._archetypeStates[ArchetypeId].ClusterState.LastRebuildForeignCellSlots, Is.Zero,
            "clusters claimed by cell hold no stray: the check must not file anything on a clean reopen");
        Assert.That(QueryTags(reopened, 0, 0, WorldMax, WorldMax), Has.Count.EqualTo(64));
    }
}
