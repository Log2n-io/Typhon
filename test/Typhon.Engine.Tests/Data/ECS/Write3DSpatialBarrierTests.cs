using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.W3D.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct W3DPos
{
    [Field]
    [SpatialIndex]
    public AABB3F Bounds;
}

[Archetype]
partial class W3DUnit : Archetype<W3DUnit>
{
    public static readonly Comp<W3DPos> Pos = Register<W3DPos>();
}

[Component("Typhon.Test.W3D.Sphere", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct W3DSphere
{
    [Field]
    [SpatialIndex]
    public BSphere3F Ball;
}

[Archetype]
partial class W3DSphereUnit : Archetype<W3DSphereUnit>
{
    public static readonly Comp<W3DSphere> Ball = Register<W3DSphere>();
}

/// <summary>
/// The write-time spatial barrier for 3D archetypes (#914): <c>ClusterRef.WriteSpatial</c> beyond <c>AABB2F</c>, and the two traps the deferral left behind.
/// </summary>
/// <remarks>
/// <para><b>Why this is a bug fixture and not a feature one.</b> Every structure below the write path was already three-dimensional — the grid, the cell
/// state, <c>ClusterSpatialAabb</c>, <c>RecomputeClusterAabb</c>, and the Z half of the outlier guard. Only the one hot path that makes any of it affordable
/// was 2D, so a 3D archetype silently fell back to MVCC and paid one WAL frame per entity per tick. These tests pin the axis that was missing, and each one
/// is written so that deleting the Z handling reddens it.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class Write3DSpatialBarrierTests : TestBase<Write3DSpatialBarrierTests>
{
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private DatabaseEngine SetupEngine(IServiceScope scope = null)
    {
        var dbe = (scope?.ServiceProvider ?? ServiceProvider).GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<W3DPos>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            new Vector3D(0, 0, 0), new Vector3D(WorldMax, WorldMax, WorldMax), CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static W3DPos PointAt(float x, float y, float z) =>
        new() { Bounds = new AABB3F { MinX = x, MinY = y, MinZ = z, MaxX = x, MaxY = y, MaxZ = z } };

    private static ArchetypeClusterState StateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<W3DUnit>.Metadata.ArchetypeId].ClusterState;

    private static EntityId Spawn(DatabaseEngine dbe, float x, float y, float z)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<W3DUnit>(W3DUnit.Pos.Set(PointAt(x, y, z)));
        tx.Commit();
        return id;
    }

    private static unsafe (int ChunkId, int Slot) LocateSlot(DatabaseEngine dbe, EntityId id)
    {
        var cs = StateOf(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var cid = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(cid);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8) == (long)id.RawValue)
                    {
                        return (cid, slot);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (-1, -1);
    }

    /// <summary>Move an entity through the BARRIER — the API a barrier-only archetype promises to use.</summary>
    private static void WriteSpatialTo(DatabaseEngine dbe, EntityId id, float x, float y, float z)
    {
        var (chunkId, slot) = LocateSlot(dbe, id);
        Assert.That(chunkId, Is.GreaterThanOrEqualTo(0), "entity must be resident in a cluster before a barrier write");

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<W3DUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId == chunkId)
                {
                    cluster.WriteSpatial(W3DUnit.Pos, slot, PointAt(x, y, z));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        tx.Commit();
    }

    /// <summary>Move an entity through the MVCC path — the fallback a 3D archetype was forced onto before #914.</summary>
    private static void MvccMoveTo(DatabaseEngine dbe, EntityId id, float x, float y, float z)
    {
        using var tx = dbe.CreateQuickTransaction();
        var eref = tx.OpenMut(id);
        ref var pos = ref eref.Write(W3DUnit.Pos);
        pos.Bounds = PointAt(x, y, z).Bounds;
        tx.Commit();
    }

    /// <summary>The cell key the entity's cluster currently belongs to, or <c>-1</c> when it is resident nowhere.</summary>
    private static int CellOf(DatabaseEngine dbe, EntityId id)
    {
        var (chunkId, _) = LocateSlot(dbe, id);
        return chunkId < 0 ? -1 : StateOf(dbe).ClusterCellMap[chunkId];
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-1 — the tiers that used to throw
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>A 3D barrier write no longer throws, and it MOVES the entity.</summary>
    /// <remarks>
    /// The second half matters as much as the first. A specialisation that wrote the component and did none of the bookkeeping would also stop throwing,
    /// and every test that only asserted "no exception" would pass while the cluster bound and the cell placement silently rotted.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void Aabb3F_BarrierWrite_MovesTheEntityAndKeepsTheBoundContaining()
    {
        using var dbe = SetupEngine();
        dbe.SetSpatialBarrierOnly<W3DUnit>();

        var id = Spawn(dbe, 50f, 50f, 50f);
        dbe.WriteTickFence(1);

        Assert.DoesNotThrow(() => WriteSpatialTo(dbe, id, 55f, 60f, 70f), "AABB3F was the tier #914 exists for — it must not throw");
        dbe.WriteTickFence(2);

        AssertEveryClusterBoundContainsItsEntities(dbe, "after a 3D barrier write");
    }

    /// <summary>The bounding-sphere tiers write through the barrier too — the same omission, one shape along.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void BSphere3F_BarrierWrite_DoesNotThrow()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<W3DSphere>();
        dbe.ConfigureSpatialGrid(new SpatialGridConfig(
            new Vector3D(0, 0, 0), new Vector3D(WorldMax, WorldMax, WorldMax), CellSize));
        dbe.InitializeArchetypes();
        using var _ = dbe;

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<W3DSphereUnit>(W3DSphereUnit.Ball.Set(
                new W3DSphere { Ball = new BSphere3F { CenterX = 50f, CenterY = 50f, CenterZ = 50f, Radius = 2f } }));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        Assert.DoesNotThrow(() =>
        {
            using var tx = dbe.CreateQuickTransaction();
            var accessor = tx.For<W3DSphereUnit>();
            try
            {
                foreach (var cluster in accessor.GetClusterEnumerator())
                {
                    cluster.WriteSpatial(W3DSphereUnit.Ball, 0,
                        new W3DSphere { Ball = new BSphere3F { CenterX = 60f, CenterY = 60f, CenterZ = 60f, Radius = 2f } });
                }
            }
            finally
            {
                accessor.Dispose();
            }
            tx.Commit();
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-2 — trap 1: the Z centre
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-2</c>: the same 3D entity driven through the barrier and through MVCC lands in the SAME cell — including when only Z moved.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the test that fails if the Z centre is regressed to <c>0f</c>.</b> <c>MaybeFlagMigration</c> was called with a hard-coded
    /// <c>centerZ = 0f</c>, with a comment saying so; the write-time check would then have placed every 3D entity in the z = 0 plane while the fence-time
    /// check placed it in its true one. Nothing raises when those disagree — the entity is simply in a cell no query for its real position will look in, and
    /// both detectors report success.</para>
    /// <para>The move is <b>Z-only</b> on purpose. A move that also crossed on X or Y would migrate for the X reason whatever Z did, and the assertion would
    /// hold with the Z term deleted.</para>
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    public void ZOnlyCrossing_BarrierAndMvccAgreeOnTheDestinationCell()
    {
        // A FRESH DATABASE per arm, not merely a fresh scope: both engines open the same file under this fixture's database name, so the second would
        // load the first's population and the comparison would be between two different worlds.
        int barrierCell = RunOneArm(barrier: true, out var barrierCoords);
        int mvccCell = RunOneArm(barrier: false, out var mvccCoords);

        Assert.Multiple(() =>
        {
            Assert.That(barrierCell, Is.GreaterThanOrEqualTo(0), "the barrier-written entity must still be resident somewhere");
            Assert.That(barrierCoords.z, Is.EqualTo(2),
                "PRECONDITION: the move must actually cross two cells on Z, or the comparison below holds for a move that never crossed anything");
            Assert.That(barrierCoords, Is.EqualTo(mvccCoords),
                "a Z-only crossing placed the entity in a different cell depending on which write path moved it. That is #914's trap 1: the write-time "
                + "migration check was given centerZ = 0 while the fence-time check used the real centre, so the two agree with themselves and disagree "
                + "with each other, silently.");
            Assert.That(barrierCell, Is.EqualTo(mvccCell), "the two paths must resolve to the same cell KEY as well as the same coordinates");
        });
    }

    /// <summary>Spawn one entity, move it on Z alone by one path or the other, and report where it ended up.</summary>
    private int RunOneArm(bool barrier, out (int x, int y, int z) coords)
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = SetupEngine(scope);

        if (barrier)
        {
            dbe.SetSpatialBarrierOnly<W3DUnit>();
        }

        var id = Spawn(dbe, 50f, 50f, 50f);
        dbe.WriteTickFence(1);

        // Z only: from the middle of cell z=0 to the middle of cell z=2. X and Y do not move at all, so a migration can only be detected on Z.
        if (barrier)
        {
            WriteSpatialTo(dbe, id, 50f, 50f, 250f);
        }
        else
        {
            MvccMoveTo(dbe, id, 50f, 50f, 250f);
        }

        dbe.WriteTickFence(2);

        int cellKey = CellOf(dbe, id);
        coords = cellKey < 0 ? (-1, -1, -1) : dbe.SpatialGrid.CellKeyToCoords(cellKey);
        return cellKey;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-3 — the Z shrink bits
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-3</c>: a cluster whose Z extreme moves inward re-tightens on Z.
    /// </summary>
    /// <remarks>
    /// The shrink mask documented <c>0x10 MinZ</c> / <c>0x20 MaxZ</c> from the start and nothing ever set them, because a 2D union leaves Z at the sentinel.
    /// Without them the grow-only CAS leaves the Z bound at its high-water mark for ever: the cluster claims a Z extent no entity in it still occupies, and
    /// every query that box overlaps pays a narrowphase pass that can only reject.
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void ClusterBoundReTightensOnZ_WhenTheExtremeMovesInward()
    {
        using var dbe = SetupEngine();
        dbe.SetSpatialBarrierOnly<W3DUnit>();

        // Two entities in one cell, one of them the Z extreme.
        var anchor = Spawn(dbe, 20f, 20f, 20f);
        var mover = Spawn(dbe, 30f, 30f, 90f);
        dbe.WriteTickFence(1);

        var cs = StateOf(dbe);
        var (chunkId, _) = LocateSlot(dbe, mover);
        float stretchedMaxZ = cs.ClusterAabbs[chunkId].MaxZ;

        // Pull the extreme inward on Z only.
        WriteSpatialTo(dbe, mover, 30f, 30f, 40f);
        dbe.WriteTickFence(2);

        float tightenedMaxZ = cs.ClusterAabbs[chunkId].MaxZ;

        Assert.Multiple(() =>
        {
            Assert.That(anchor.RawValue, Is.Not.EqualTo(0UL));
            Assert.That(tightenedMaxZ, Is.LessThan(stretchedMaxZ),
                "the cluster's Z bound stayed at its high-water mark after the Z extreme moved inward — the MinZ/MaxZ shrink bits are not being set, so the "
                + "refresh never re-derives the axis");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-4 — trap 2: the outlier guard's Z axis
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>AC-4</c>: an entity that escapes its cell purely on Z is caught by the outlier guard.
    /// </summary>
    /// <remarks>
    /// <para><b>Until #914 no test could exist for this.</b> The guard tests all three axes, but its Z pair was documented UNREACHABLE: no cluster AABB could
    /// grow on Z at write time, and a 2D union leaves the sentinel. The 3D write tiers make it live.</para>
    /// <para>The entity is moved <b>only on Z</b> and past the raw cell boundary, so the X and Y comparisons in
    /// <c>FlagOutliersForMigration</c> are both false — delete the Z pair and this entity is never nominated, which is exactly the silent stranding the guard
    /// exists to prevent.</para>
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    public void EntityEscapingOnZAlone_IsMigratedOutOfItsCell()
    {
        using var dbe = SetupEngine();
        dbe.SetSpatialBarrierOnly<W3DUnit>();

        var id = Spawn(dbe, 50f, 50f, 50f);
        dbe.WriteTickFence(1);
        int before = CellOf(dbe, id);

        // Four cells away on Z; X and Y unchanged, so only the Z comparison can notice.
        WriteSpatialTo(dbe, id, 50f, 50f, 450f);
        dbe.WriteTickFence(2);
        int after = CellOf(dbe, id);

        Assert.Multiple(() =>
        {
            Assert.That(before, Is.GreaterThanOrEqualTo(0));
            Assert.That(after, Is.Not.EqualTo(before),
                "an entity that left its cell on Z alone stayed in its original cell. Both the write-time check and the outlier guard test Z; if neither "
                + "moved it, the Z axis is not being evaluated and the entity is unreachable from any query for where it actually is.");
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-5 / AC-6 — the barrier path, and CA-01 on three axes
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary><c>AC-5</c>: a 3D archetype can be barrier-only, which is the whole point — it is what skips Prep's legacy per-entity scan.</summary>
    [Test]
    [CancelAfter(15_000)]
    public void ThreeDArchetype_IsBarrierOnly_AndSkipsTheLegacyScan()
    {
        using var dbe = SetupEngine();
        dbe.SetSpatialBarrierOnly<W3DUnit>();

        var ids = new List<EntityId>();
        for (int i = 0; i < 40; i++)
        {
            ids.Add(Spawn(dbe, 10f + i, 20f, 30f + i));
        }

        dbe.WriteTickFence(1);

        var cs = StateOf(dbe);
        Assert.That(cs.SpatialBarrierOnly, Is.True, "a 3D archetype must be able to take the barrier path — before #914 it silently could not");

        foreach (var id in ids)
        {
            WriteSpatialTo(dbe, id, 12f, 22f, 32f);
        }

        long scannedBefore = cs.LastTickSlotsScanned;
        dbe.WriteTickFence(2);

        // The barrier path's promise: work proportional to FLAGGED slots, not to every dirty entity. The legacy scan would touch every one.
        Assert.That(cs.LastTickSlotsScanned, Is.LessThan(ids.Count * 4L),
            $"the fence scanned {cs.LastTickSlotsScanned} slots for {ids.Count} barrier-written entities (was {scannedBefore} before the tick). The "
            + "barrier-only path exists to make this proportional to flagged slots rather than to the population.");
    }

    /// <summary><c>AC-6</c>: <c>CA-01</c> holds on all three axes after a tick of barrier writes.</summary>
    [Test]
    [CancelAfter(30_000)]
    public void CA01_HoldsOnAllThreeAxes_AfterBarrierWrites()
    {
        using var dbe = SetupEngine();
        dbe.SetSpatialBarrierOnly<W3DUnit>();

        var rng = new Random(20260908);
        var ids = new List<EntityId>();
        for (int i = 0; i < 120; i++)
        {
            ids.Add(Spawn(dbe,
                (float)(rng.NextDouble() * 400), (float)(rng.NextDouble() * 400), (float)(rng.NextDouble() * 400)));
        }

        dbe.WriteTickFence(1);

        for (int tick = 2; tick <= 5; tick++)
        {
            foreach (var id in ids)
            {
                WriteSpatialTo(dbe, id,
                    (float)(rng.NextDouble() * 400), (float)(rng.NextDouble() * 400), (float)(rng.NextDouble() * 400));
            }

            dbe.WriteTickFence(tick);
            AssertEveryClusterBoundContainsItsEntities(dbe, $"after tick {tick}");
        }
    }

    /// <summary>
    /// <c>CA-01</c> on three axes: every cluster's stored bound contains every entity it holds.
    /// </summary>
    /// <remarks>
    /// Bounds are <c>C15</c> cell-relative, so the entity's world position is rebased through the same origin before the comparison — comparing a world
    /// coordinate against a cell-relative bound would fail everywhere except in the cell at the world origin.
    /// </remarks>
    private static unsafe void AssertEveryClusterBoundContainsItsEntities(DatabaseEngine dbe, string what)
    {
        var cs = StateOf(dbe);
        var grid = dbe.SpatialGrid;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var chunkId = cs.ActiveClusterIds[i];
                var cellKey = cs.ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }

                grid.CellOrigin(cellKey, out double ox, out double oy, out double oz);
                ref readonly var box = ref cs.ClusterAabbs[chunkId];

                var clusterBase = accessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;
                var ss = cs.SpatialSlot;
                var compOffset = cs.Layout.ComponentOffset(ss.Slot);
                var compStride = cs.Layout.ComponentSize(ss.Slot);

                while (occupancy != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    var fieldPtr = clusterBase + compOffset + slot * compStride + ss.FieldOffset;
                    ref var b = ref *(AABB3F*)fieldPtr;

                    float relMinX = ClusterSpatialAabb.ToCellRelativeMin(b.MinX, ox);
                    float relMinY = ClusterSpatialAabb.ToCellRelativeMin(b.MinY, oy);
                    float relMinZ = ClusterSpatialAabb.ToCellRelativeMin(b.MinZ, oz);
                    float relMaxX = ClusterSpatialAabb.ToCellRelativeMax(b.MaxX, ox);
                    float relMaxY = ClusterSpatialAabb.ToCellRelativeMax(b.MaxY, oy);
                    float relMaxZ = ClusterSpatialAabb.ToCellRelativeMax(b.MaxZ, oz);

                    Assert.That(box.MinX, Is.LessThanOrEqualTo(relMinX), $"CA-01 X-min violated in cluster {chunkId} slot {slot} {what}");
                    Assert.That(box.MinY, Is.LessThanOrEqualTo(relMinY), $"CA-01 Y-min violated in cluster {chunkId} slot {slot} {what}");
                    Assert.That(box.MinZ, Is.LessThanOrEqualTo(relMinZ),
                        $"CA-01 Z-min violated in cluster {chunkId} slot {slot} {what} — the Z axis is the one #914 added to the write path");
                    Assert.That(box.MaxX, Is.GreaterThanOrEqualTo(relMaxX), $"CA-01 X-max violated in cluster {chunkId} slot {slot} {what}");
                    Assert.That(box.MaxY, Is.GreaterThanOrEqualTo(relMaxY), $"CA-01 Y-max violated in cluster {chunkId} slot {slot} {what}");
                    Assert.That(box.MaxZ, Is.GreaterThanOrEqualTo(relMaxZ),
                        $"CA-01 Z-max violated in cluster {chunkId} slot {slot} {what} — the Z axis is the one #914 added to the write path");
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }
}
