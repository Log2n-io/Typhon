using System.Runtime.CompilerServices;
using System.Threading;

namespace Typhon.Engine.Internals;

// The write-time spatial bookkeeping (CA-01, CA-02, CC-02): shared by ClusterRef.WriteSpatial, which caches the cluster's cell frame across a cluster's
// entities, and EntityRefMut.Set on a barrier-only archetype, which resolves it per write.
internal sealed unsafe partial class ArchetypeClusterState
{
    /// <summary>
    /// The cell frame of cluster <paramref name="chunkId"/>: its cell key, its realm's grid and the world-space origin its <c>C15</c> cell-relative bounds
    /// are measured from. <see langword="false"/> when the archetype has no grid or the cluster has no cell yet: there is then no frame to express a bound
    /// in, and the caller leaves <c>ClusterAabbs</c> alone.
    /// </summary>
    internal bool TryResolveCellFrame(int chunkId, out int cellKey, out SpatialGrid grid, out double originX, out double originY, out double originZ)
    {
        cellKey = -1;
        originX = 0d;
        originY = 0d;
        originZ = 0d;
        grid = SpatialOfCluster(chunkId).Grid;
        var clusterCellMap = ClusterCellMap;
        if (grid != null && clusterCellMap != null && (uint)chunkId < (uint)clusterCellMap.Length)
        {
            cellKey = clusterCellMap[chunkId];
            if (cellKey >= 0)
            {
                grid.CellOrigin(cellKey, out originX, out originY, out originZ);
            }
        }

        return cellKey >= 0;
    }

    /// <summary>
    /// The bookkeeping of a spatial write once its old and new boxes are decoded — every <c>ClusterRef.WriteSpatial</c> tier's, and an
    /// <c>EntityRefMut.Set</c>'s on a barrier-only archetype: grow the cluster bound, flag shrink, test for a cell crossing, and publish the cluster to the
    /// fence. <paramref name="haveOrigin"/>, <paramref name="cellKey"/>, <paramref name="grid"/> and the origin are the cluster's cell frame
    /// (<see cref="TryResolveCellFrame"/>); without one the bound is left alone and no crossing is tested.
    /// </summary>
    /// <remarks>
    /// <para><b><paramref name="is3D"/> is a constant at every call site</b>, so the JIT folds the Z arms away for a 2D tier after inlining — the 2D path
    /// costs exactly what it did before the 3D tiers existed. It is not a runtime mode.</para>
    /// <para><b>The bounds arrive as f64 world coordinates whatever the tier's storage width</b> (#914 phase C). Widening an f32 tier's bound is exact, so
    /// AntHill's <c>AABB2F</c> path means bit-for-bit what it meant; and both things this method then does with them narrow immediately — the cluster AABB
    /// through <see cref="ClusterSpatialAabb.ToCellRelativeMin"/>, the migration test through the same cell-relative subtraction — so nothing downstream
    /// carries the wider value. Taking f32 here instead would have forced the f64 tiers to throw away their magnitude at the door, which is the whole thing
    /// they exist for.</para>
    /// <para><b>Z is skipped entirely for a 2D tier, and that is not an optimisation.</b> A 2D archetype's stored <c>ClusterSpatialAabb</c> leaves
    /// <c>MinZ</c>/<c>MaxZ</c> at the ±Infinity sentinel; growing them to 0 would turn the sentinel into a real bound, and the outlier guard's extent test
    /// (<c>MaxZ - MinZ</c>) would go from <c>-Infinity</c> — its "no Z bound" reading — to 0, which reads as a perfectly tight axis.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ApplySpatialWrite(int chunkId, int slotIndex, bool haveOrigin, int cellKey, SpatialGrid grid, double originX, double originY,
        double originZ,
        double oldMinX, double oldMinY, double oldMinZ, double oldMaxX, double oldMaxY, double oldMaxZ,
        double newMinX, double newMinY, double newMinZ, double newMaxX, double newMaxY, double newMaxZ, bool is3D)
    {
        // See ClusterRef.WriteSpatialAabb2F for why no SetDirty happens here.
        var aabbChanged = false;
        if (haveOrigin)
        {
            var relOldMinX = ClusterSpatialAabb.ToCellRelativeMin(oldMinX, originX);
            var relOldMinY = ClusterSpatialAabb.ToCellRelativeMin(oldMinY, originY);
            var relOldMinZ = ClusterSpatialAabb.ToCellRelativeMin(oldMinZ, originZ);
            var relOldMaxX = ClusterSpatialAabb.ToCellRelativeMax(oldMaxX, originX);
            var relOldMaxY = ClusterSpatialAabb.ToCellRelativeMax(oldMaxY, originY);
            var relOldMaxZ = ClusterSpatialAabb.ToCellRelativeMax(oldMaxZ, originZ);
            var relNewMinX = ClusterSpatialAabb.ToCellRelativeMin(newMinX, originX);
            var relNewMinY = ClusterSpatialAabb.ToCellRelativeMin(newMinY, originY);
            var relNewMinZ = ClusterSpatialAabb.ToCellRelativeMin(newMinZ, originZ);
            var relNewMaxX = ClusterSpatialAabb.ToCellRelativeMax(newMaxX, originX);
            var relNewMaxY = ClusterSpatialAabb.ToCellRelativeMax(newMaxY, originY);
            var relNewMaxZ = ClusterSpatialAabb.ToCellRelativeMax(newMaxZ, originZ);

            // Stamped: a spawn on another thread can grow ClusterAabbs, and a CAS into the array it copied after the copy read this entry would be lost —
            // see ArchetypeClusterState._clusterAabbsGrowth. The grow and the shrink flag are both idempotent, so the redo just repeats them in the copy.
            int stamp;
            do
            {
                stamp = BeginClusterAabbsWrite();
                aabbChanged |= MaybeGrowAndFlagShrink(chunkId, ref Volatile.Read(ref ClusterAabbs)[chunkId],
                    relOldMinX, relOldMinY, relOldMinZ, relOldMaxX, relOldMaxY, relOldMaxZ,
                    relNewMinX, relNewMinY, relNewMinZ, relNewMaxX, relNewMaxY, relNewMaxZ,
                    is3D);
            }
            while (!ClusterAabbsWriteLanded(stamp));
        }

        // The REAL Z centre, which is what trap 1 of #914 was about: this used to be a hard-coded 0f, so a 3D entity would have been placed in the z = 0
        // plane at write time and in its true plane at fence time. Both detectors would have agreed with themselves and disagreed with each other, every
        // counter would have balanced, and the entity would simply have been in the wrong cell.
        var centerX = 0.5d * (newMinX + newMaxX);
        var centerY = 0.5d * (newMinY + newMaxY);
        var centerZ = is3D ? 0.5d * (newMinZ + newMaxZ) : 0d;

        var migrationFlagged = haveOrigin && MaybeFlagMigration(chunkId, grid, slotIndex, cellKey, originX, originY, originZ, centerX, centerY, centerZ);

        if (migrationFlagged)
        {
            MigrationHint++;
        }

        if (aabbChanged || migrationFlagged)
        {
            SetClusterProcessBit(chunkId);
        }
    }

    /// <summary>
    /// Inline AABB-grow (CAS per axis) + shrink-pending-axes flag. Returns true when either axis-extreme moved (in which case the cluster needs a
    /// fence-time <c>PerCellIndex.UpdateAt</c> with the fresh AABB).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaybeGrowAndFlagShrink(int chunkId, ref ClusterSpatialAabb stored,
        float oldMinX, float oldMinY, float oldMinZ, float oldMaxX, float oldMaxY, float oldMaxZ,
        float newMinX, float newMinY, float newMinZ, float newMaxX, float newMaxY, float newMaxZ, bool is3D)
    {
        var changed = false;

        // GROW path (CAS loop per axis). Note: AABB2F has min/max as separate fields, so we CAS each independently.
        if (newMinX < stored.MinX) { ClusterSpatialAabb.CasMin(ref stored.MinX, newMinX); changed = true; }
        if (newMinY < stored.MinY) { ClusterSpatialAabb.CasMin(ref stored.MinY, newMinY); changed = true; }
        if (newMaxX > stored.MaxX) { ClusterSpatialAabb.CasMax(ref stored.MaxX, newMaxX); changed = true; }
        if (newMaxY > stored.MaxY) { ClusterSpatialAabb.CasMax(ref stored.MaxY, newMaxY); changed = true; }

        // SHRINK flag (only set when this slot WAS at an extreme AND moved inward). Bit layout:
        // 0x01=MinX, 0x02=MaxX, 0x04=MinY, 0x08=MaxY, 0x10=MinZ, 0x20=MaxZ (matches ClusterShrinkPendingAxes doc).
        byte shrinkMask = 0;
        if (oldMinX == stored.MinX && newMinX > oldMinX)
        {
            shrinkMask |= 0x01;
        }

        if (oldMaxX == stored.MaxX && newMaxX < oldMaxX)
        {
            shrinkMask |= 0x02;
        }

        if (oldMinY == stored.MinY && newMinY > oldMinY)
        {
            shrinkMask |= 0x04;
        }

        if (oldMaxY == stored.MaxY && newMaxY < oldMaxY)
        {
            shrinkMask |= 0x08;
        }

        // The Z axis, live since #914. It is gated on is3D rather than run unconditionally because a 2D archetype's stored MinZ/MaxZ are the +/-Infinity
        // sentinel: growing them to a real 0 would make the outlier guard's MaxZ - MinZ read 0 (a perfectly tight axis) where it currently reads -Infinity
        // (no Z bound at all), and the guard would stop distinguishing the two.
        if (is3D)
        {
            if (newMinZ < stored.MinZ) { ClusterSpatialAabb.CasMin(ref stored.MinZ, newMinZ); changed = true; }
            if (newMaxZ > stored.MaxZ) { ClusterSpatialAabb.CasMax(ref stored.MaxZ, newMaxZ); changed = true; }

            if (oldMinZ == stored.MinZ && newMinZ > oldMinZ)
            {
                shrinkMask |= 0x10;
            }

            if (oldMaxZ == stored.MaxZ && newMaxZ < oldMaxZ)
            {
                shrinkMask |= 0x20;
            }
        }

        if (shrinkMask != 0)
        {
            FlagShrinkAxes(chunkId, shrinkMask);
            changed = true;
        }

        return changed;
    }

    /// <summary>Migration cell-boundary check. Returns true when a migration was flagged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaybeFlagMigration(int chunkId, SpatialGrid grid, int slotIndex, int currentCellKey, double cellMinX, double cellMinY, double cellMinZ,
        double centerX, double centerY, double centerZ)
    {
        // The cell key and origin are PASSED IN rather than re-derived. The caller resolved both to convert the entity's bounds into the cluster's frame,
        // and this method used to repeat all of it — two array loads, a bounds check, CellKeyToCoords (itself two dependent loads into the cell's CellState)
        // and three multiplies — per entity per tick, inlined into the AntHill simulation barrier.
        ref readonly var cfg = ref grid.Config;

        // ── The test is CELL-RELATIVE, and that is what keeps it f32 (#914) ──────────────────────────────────────
        //
        // The cell origin is f64 since phase A, because a frame at 10^9 needs the mantissa. Comparing the entity centre against it directly would drag six
        // comparisons per entity per tick into double on the hottest write path in the engine. Subtracting first costs three conversions and gives back an
        // offset that is at most one cell wide — which f32 holds exactly, wherever in the world the cell sits. That is the floating-origin bargain applied
        // to the migration test rather than only to stored bounds, and it is the same reason C15 can keep ClusterSpatialAabb in f32.
        var cellSize = (float)cfg.CellSize;
        var hyster = cellSize * cfg.MigrationHysteresisRatio;
        var relX = (float)(centerX - cellMinX);
        var relY = (float)(centerY - cellMinY);
        var relZ = (float)(centerZ - cellMinZ);

        var exited = relX < -hyster || relX > cellSize + hyster
                     || relY < -hyster || relY > cellSize + hyster
                     || relZ < -hyster || relZ > cellSize + hyster;
        if (!exited)
        {
            // Count the crossings the margin swallowed (#872). Without this the SpatialBarrierOnly path reports zero absorbed crossings forever:
            // DetectClusterMigrations only increments inside its legacy dirty-bits scan, and the barrier-only branch returns before reaching it — so the
            // ratio that tunes MigrationHysteresisRatio was structurally 0/N on the path both demos use. The decision is made here, so the count belongs here.
            //
            // The extra test is the SAME comparison without the margin: "left the cell" minus "left the cell plus margin" is exactly the absorbed set.
            //
            // Ungated on purpose, matching MigrationHint below and EntityMap's split counter: a static readonly profiler gate resolves to false by default, so
            // gating it would leave the number a structural zero in exactly the tool built to read it — trading a defect nobody can see for four float
            // compares in a method that already dereferences the grid, loads two arrays and calls CellKeyToCoords.
            //
            // Interlocked, unlike MigrationHint's plain ++ below: this value is PUBLISHED as an exact count through
            // SpatialMigrationTelemetry, where MigrationHint is documented as an order-of-magnitude work estimate. The atomic is affordable precisely because
            // it is rare — it fires only for a write that lands inside the margin band, not on every spatial write.
            var rawExited = relX < 0f || relX > cellSize
                            || relY < 0f || relY > cellSize
                            || relZ < 0f || relZ > cellSize;
            if (rawExited)
            {
                Interlocked.Increment(ref HysteresisAbsorbedLive);
            }

            return false;
        }

        var newCellKey = grid.WorldToCellKey(centerX, centerY, centerZ);
        if (newCellKey == currentCellKey)
        {
            return false;
        }

        // Set bit in per-cluster migration bitmap (atomic OR), and stomp dest cell key. The key is a HINT: the drain (DrainPreFlaggedMigrations) re-reads
        // the slot's position and decides from that, so two slots of one cluster written to two cells, an entity written out and back within the tick,
        // and a write that reached a spawn's slot before its data landed, all resolve to where the entity actually is (CC-02).
        var slotBit = 1UL << slotIndex;
        FlagMigration(chunkId, slotBit, newCellKey);
        return true;
    }
}
