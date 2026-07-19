using System;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Static helpers for maintaining spatial R-Tree entries in response to ECS operations (spawn, update, destroy).
/// All logic is storage-mode-agnostic — only the trigger differs (tick fence for SV, commit for Versioned).
/// </summary>
internal static unsafe partial class SpatialMaintainer
{
    // ── LoggerMessage partials ───────────────────────────────────────────────

    [LoggerMessage(Level = LogLevel.Warning, Message = "Degenerate spatial AABB for entity {EntityPK} in {ComponentName}, skipping spatial {Operation}")]
    private static partial void LogDegenerateAABB(ILogger logger, long entityPK, string componentName, string operation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Spatial escape rate {EscapeRate:P1} ({EscapeCount}/{DirtyCount}) for {ComponentName} exceeds 10% — consider increasing margin")]
    internal static partial void LogHighEscapeRate(ILogger logger, string componentName, double escapeRate, int escapeCount, int dirtyCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cluster migration storm: {MigrationCount} migrations in a single tick for archetype id {ArchetypeId} ({DurationMs:F3} ms) — possible viewport warp, teleport event, or unphysical speed")]
    internal static partial void LogHighMigrationRate(ILogger logger, int migrationCount, ushort archetypeId, double durationMs);

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Insert a newly spawned entity into the spatial R-Tree. Called at FinalizeSpawns (SV) or commit (Versioned).
    /// </summary>
    internal static void InsertSpatial(long entityPK, int componentChunkId, ComponentTable table, ref ChunkAccessor<PersistentStore> compAccessor, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;
        var fi = state.FieldInfo;
        var tree = state.ActiveTree;
        var desc = state.Descriptor;

        using var maintainSpan = TyphonEvent.BeginSpatialMaintainInsert(entityPK, table.WalTypeId);

        // Read tight bounds from component data
        byte* compPtr = compAccessor.GetChunkAddress(componentChunkId);
        Span<double> coords = stackalloc double[desc.CoordCount];

        if (!ReadAndValidateBounds(compPtr, fi, coords, entityPK, table, "insert"))
        {
            TyphonEvent.EmitSpatialMaintainAabbValidate(entityPK, table.WalTypeId, 0);
            return;
        }

        // Enlarge to fat AABB
        EnlargeCoords(coords, fi.Margin, desc);

        // Insert into tree
        var treeAccessor = tree.Segment.CreateChunkAccessor(changeSet);
        try
        {
            var (leafChunkId, slotIndex) = tree.Insert(entityPK, componentChunkId, coords, ref treeAccessor, changeSet);

            // Write back-pointer
            var bpAccessor = state.BackPointerSegment.CreateChunkAccessor(changeSet);
            try
            {
                SpatialBackPointerHelper.Write(ref bpAccessor, componentChunkId, leafChunkId, (short)slotIndex, (byte)state.FieldInfo.Mode);
                TyphonEvent.EmitSpatialMaintainBackPointerWrite(componentChunkId, leafChunkId, (ushort)slotIndex);
            }
            finally
            {
                bpAccessor.Dispose();
            }
        }
        finally
        {
            treeAccessor.Dispose();
        }

        // Layer 1: increment occupancy counter for this entity's coarse cell
        IncrementOccupancy(state, coords, changeSet);
    }

    /// <summary>
    /// Update an existing entity's spatial position. Fast path if tight AABB is still within fat AABB (~25ns).
    /// Slow path removes and reinserts (~500–700ns). Called at tick fence (SV) or commit (Versioned).
    /// </summary>
    /// <returns>True if the entity escaped the fat AABB and was reinserted (slow path). False for fast path or skip.</returns>
    internal static bool UpdateSpatial(long entityPK, int componentChunkId, ComponentTable table, ref ChunkAccessor<PersistentStore> compAccessor, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;
        var bpAccessor = state.BackPointerSegment.CreateChunkAccessor(changeSet);
        var treeAccessor = state.ActiveTree.Segment.CreateChunkAccessor(changeSet);
        try
        {
            return UpdateSpatialCore(entityPK, componentChunkId, table, ref compAccessor, ref treeAccessor, ref bpAccessor, changeSet);
        }
        finally
        {
            treeAccessor.Dispose();
            bpAccessor.Dispose();
        }
    }

    /// <summary>
    /// Batch-optimized overload: callers pre-create and reuse tree/bp accessors across many entities.
    /// Used by <see cref="DatabaseEngine.ProcessSpatialEntries"/> at tick fence (same pattern as B+Tree batch index maintenance).
    /// </summary>
    internal static bool UpdateSpatialBatch(long entityPK, int componentChunkId, ComponentTable table,
        ref ChunkAccessor<PersistentStore> compAccessor, ref ChunkAccessor<PersistentStore> treeAccessor,
        ref ChunkAccessor<PersistentStore> bpAccessor, ChangeSet changeSet)
        => UpdateSpatialCore(entityPK, componentChunkId, table, ref compAccessor, ref treeAccessor, ref bpAccessor, changeSet);

    private static bool UpdateSpatialCore(long entityPK, int componentChunkId, ComponentTable table,
        ref ChunkAccessor<PersistentStore> compAccessor, ref ChunkAccessor<PersistentStore> treeAccessor,
        ref ChunkAccessor<PersistentStore> bpAccessor, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;
        var fi = state.FieldInfo;
        var tree = state.ActiveTree;
        var desc = state.Descriptor;

        // Read current tight bounds
        byte* compPtr = compAccessor.GetChunkAddress(componentChunkId);
        Span<double> tightCoords = stackalloc double[desc.CoordCount];

        if (!ReadAndValidateBounds(compPtr, fi, tightCoords, entityPK, table, "update"))
        {
            TyphonEvent.EmitSpatialMaintainAabbValidate(entityPK, table.WalTypeId, 1);
            return false;
        }

        var bp = SpatialBackPointerHelper.Read(ref bpAccessor, componentChunkId);
        if (bp.LeafChunkId == 0)
        {
            // No back-pointer — entity was never inserted (degenerate at spawn). Try inserting now.
            InsertSpatial(entityPK, componentChunkId, table, ref compAccessor, changeSet);
            return false;
        }

        Span<double> fatCoords = stackalloc double[desc.CoordCount];
        tree.ReadLeafCoords(bp.LeafChunkId, bp.SlotIndex, fatCoords, ref treeAccessor);

        // Fast path: containment check
        if (CoordsContained(fatCoords, tightCoords, desc.CoordCount))
        {
            return false;
        }

        // Slow path: remove + reinsert
        // Compute escape distance squared (sum of axis-overflow magnitudes) for diagnostic payload.
        float escapeDistSq = 0f;
        if (TelemetryConfig.SpatialMaintainUpdateSlowPathActive)
        {
            int half = desc.CoordCount / 2;
            for (int i = 0; i < half; i++)
            {
                double underflow = fatCoords[i] - tightCoords[i];
                double overflow = tightCoords[i + half] - fatCoords[i + half];
                if (underflow > 0) escapeDistSq += (float)(underflow * underflow);
                if (overflow > 0) escapeDistSq += (float)(overflow * overflow);
            }
        }
        using var slowPathSpan = TyphonEvent.BeginSpatialMaintainUpdateSlowPath(entityPK, table.WalTypeId, escapeDistSq);

        long swappedEntityId = tree.Remove(bp.LeafChunkId, bp.SlotIndex, ref treeAccessor);

        if (swappedEntityId != 0 && swappedEntityId != entityPK)
        {
            UpdateSwappedBackPointer(swappedEntityId, bp.LeafChunkId, bp.SlotIndex, table, ref bpAccessor);
        }

        EnlargeCoords(tightCoords, fi.Margin, desc);
        var (newLeaf, newSlot) = tree.Insert(entityPK, componentChunkId, tightCoords, ref treeAccessor, changeSet);
        SpatialBackPointerHelper.Write(ref bpAccessor, componentChunkId, newLeaf, (short)newSlot, bp.TreeSelector);
        TyphonEvent.EmitSpatialMaintainBackPointerWrite(componentChunkId, newLeaf, (ushort)newSlot);

        return true; // Escaped fat AABB → reinserted
    }

    /// <summary>
    /// Remove a destroyed entity from the spatial R-Tree. Called at destroy (SV tick fence or Versioned commit).
    /// </summary>
    internal static void RemoveFromSpatial(long entityPK, int componentChunkId, ComponentTable table, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;

        var bpAccessor = state.BackPointerSegment.CreateChunkAccessor(changeSet);
        try
        {
            var bp = SpatialBackPointerHelper.Read(ref bpAccessor, componentChunkId);
            if (bp.LeafChunkId == 0)
            {
                TyphonEvent.EmitSpatialMaintainAabbValidate(entityPK, table.WalTypeId, 2);
                return; // Never inserted (degenerate bounds)
            }

            var tree = state.GetTree(bp.TreeSelector);
            var treeAccessor = tree.Segment.CreateChunkAccessor(changeSet);
            try
            {
                // Read fat AABB coords before removing (needed for Layer 1 decrement)
                Span<double> removedCoords = stackalloc double[state.Descriptor.CoordCount];
                tree.ReadLeafCoords(bp.LeafChunkId, bp.SlotIndex, removedCoords, ref treeAccessor);

                long swappedEntityId = tree.Remove(bp.LeafChunkId, bp.SlotIndex, ref treeAccessor);

                if (swappedEntityId != 0 && swappedEntityId != entityPK)
                {
                    UpdateSwappedBackPointer(swappedEntityId, bp.LeafChunkId, bp.SlotIndex, table, ref bpAccessor);
                }

                // Layer 1: decrement occupancy for the removed entity's cell
                DecrementOccupancy(state, removedCoords, changeSet);
            }
            finally
            {
                treeAccessor.Dispose();
            }

            SpatialBackPointerHelper.Clear(ref bpAccessor, componentChunkId);
        }
        finally
        {
            bpAccessor.Dispose();
        }
    }

    /// <summary>
    /// Update the category mask of an entity's spatial leaf entry in-place. Called via back-pointer for runtime
    /// category changes (e.g., entity dies → clear Alive bit, entity switches team → change Team bits).
    /// Refits the union mask up the ancestor chain.
    /// </summary>
    internal static void SetSpatialCategory(int componentChunkId, ComponentTable table, uint newCategoryMask, ChangeSet changeSet)
    {
        var state = table.SpatialIndex;

        var bpAccessor = state.BackPointerSegment.CreateChunkAccessor(changeSet);
        try
        {
            var bp = SpatialBackPointerHelper.Read(ref bpAccessor, componentChunkId);
            if (bp.LeafChunkId == 0)
            {
                return; // Never inserted (degenerate bounds)
            }

            var tree = state.GetTree(bp.TreeSelector);
            var treeAccessor = tree.Segment.CreateChunkAccessor(changeSet);
            try
            {
                tree.SetEntryCategoryMask(bp.LeafChunkId, bp.SlotIndex, newCategoryMask, ref treeAccessor);
            }
            finally
            {
                treeAccessor.Dispose();
            }
        }
        finally
        {
            bpAccessor.Dispose();
        }
    }

    // Note: InsertSpatialCluster, UpdateSpatialBatchCluster, and RemoveFromSpatialCluster were removed in issue #230 Phase 3 Option B. The legacy per-entity
    // cluster R-Tree is gone; spawn/destroy/migration hooks now only maintain the per-cell cluster index via ArchetypeClusterState.AddClusterToPerCellIndex
    // / RemoveClusterFromPerCellIndex.

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Read spatial bounds from a raw field pointer, convert BSphere to AABB if needed.
    /// Used by cluster path where fieldPtr points directly into cluster SoA data.
    /// Returns false if bounds are degenerate (NaN/Inf/Min>Max).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool ReadAndValidateBoundsFromPtr(byte* fieldPtr, SpatialFieldInfo fi, Span<double> coords, SpatialNodeDescriptor desc)
    {
        switch (fi.FieldType)
        {
            case SpatialFieldType.AABB2F:
            {
                var aabb = *(AABB2F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3F:
            {
                var aabb = *(AABB3F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2F:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere2F*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3F:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere3F*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.AABB2D:
            {
                var aabb = *(AABB2D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3D:
            {
                var aabb = *(AABB3D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2D:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere2D*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3D:
            {
                var aabb = SpatialGeometry.Enclosing(*(BSphere3D*)fieldPtr);
                if (SpatialGeometry.IsDegenerate(aabb)) { return false; }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            default:
                return false;
        }

        return true;
    }

    /// <summary>
    /// Read spatial bounds from component data, convert BSphere to AABB if needed, write to coords array.
    /// Returns false if bounds are degenerate (NaN/Inf/Min>Max).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ReadAndValidateBounds(byte* compPtr, SpatialFieldInfo fi, Span<double> coords, long entityPK, ComponentTable table, string operation)
    {
        byte* fieldPtr = compPtr + fi.FieldOffset;

        switch (fi.FieldType)
        {
            case SpatialFieldType.AABB2F:
            {
                var aabb = *(AABB2F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3F:
            {
                var aabb = *(AABB3F*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2F:
            {
                var s = *(BSphere2F*)fieldPtr;
                var aabb = SpatialGeometry.Enclosing(s);
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3F:
            {
                var s = *(BSphere3F*)fieldPtr;
                var aabb = SpatialGeometry.Enclosing(s);
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.AABB2D:
            {
                var aabb = *(AABB2D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.AABB3D:
            {
                var aabb = *(AABB3D*)fieldPtr;
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
            case SpatialFieldType.BSphere2D:
            {
                var s = *(BSphere2D*)fieldPtr;
                var aabb = SpatialGeometry.Enclosing(s);
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY;
                coords[2] = aabb.MaxX; coords[3] = aabb.MaxY;
                break;
            }
            case SpatialFieldType.BSphere3D:
            {
                var s = *(BSphere3D*)fieldPtr;
                var aabb = SpatialGeometry.Enclosing(s);
                if (SpatialGeometry.IsDegenerate(aabb))
                {
                    LogDegenerateAABB(table.DBE.Logger, entityPK, table.Definition.Name, operation);
                    return false;
                }
                coords[0] = aabb.MinX; coords[1] = aabb.MinY; coords[2] = aabb.MinZ;
                coords[3] = aabb.MaxX; coords[4] = aabb.MaxY; coords[5] = aabb.MaxZ;
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// Enlarge coords in-place by margin. Coords are [min0, min1, ..., max0, max1, ...].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnlargeCoords(Span<double> coords, float margin, SpatialNodeDescriptor desc)
    {
        int half = desc.CoordCount / 2;
        for (int i = 0; i < half; i++)
        {
            coords[i] -= margin;
        }
        for (int i = half; i < desc.CoordCount; i++)
        {
            coords[i] += margin;
        }
    }

    /// <summary>
    /// Check if tight AABB is fully contained within fat AABB. Coords ordered [min0..., max0...].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CoordsContained(ReadOnlySpan<double> fat, ReadOnlySpan<double> tight, int coordCount)
    {
        int half = coordCount / 2;
        for (int i = 0; i < half; i++)
        {
            if (fat[i] > tight[i])
            {
                return false; // fat min > tight min → not contained
            }
        }
        for (int i = half; i < coordCount; i++)
        {
            if (fat[i] < tight[i])
            {
                return false; // fat max < tight max → not contained
            }
        }
        return true;
    }

    /// <summary>
    /// After a swap-with-last in Remove, update the swapped entity's back-pointer to the vacated slot.
    /// Resolves entityId → componentChunkId using EntityMap (via DatabaseEngine).
    /// </summary>
    private static void UpdateSwappedBackPointer(long swappedEntityId, int leafChunkId, int slotIndex,
        ComponentTable table, ref ChunkAccessor<PersistentStore> bpAccessor)
    {
        // The swapped entity now occupies (leafChunkId, slotIndex). We need its componentChunkId to update the back-pointer.
        // The entity's componentChunkId can be resolved from the EntityMap via EntityId → EntityRecord → Location[slot].
        // For now, we search the back-pointer segment: the entity's old back-pointer has the old leaf position.
        // Since we know the swappedEntityId, we can find its componentChunkId by looking it up in the ArchetypeState's EntityMap.

        var dbe = table.DBE;
        var entityId = EntityId.FromRaw(swappedEntityId);
        if (entityId.ArchetypeId >= dbe._stateByRouting.Length)
        {
            return;
        }
        var archState = dbe._stateByRouting[entityId.ArchetypeId];
        if (archState?.EntityMap == null)
        {
            return;
        }

        // Find the component slot for this table in the archetype
        int compSlot = -1;
        for (int s = 0; s < archState.SlotToComponentTable.Length; s++)
        {
            if (archState.SlotToComponentTable[s] == table)
            {
                compSlot = s;
                break;
            }
        }
        if (compSlot < 0)
        {
            return;
        }

        // Read the entity record to get the component chunkId
        byte* recordBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];
        var emAccessor = archState.EntityMap.Segment.CreateChunkAccessor();
        try
        {
            if (archState.EntityMap.TryGet(entityId.EntityKey, recordBuf, ref emAccessor))
            {
                int swappedCompChunkId = EntityRecordAccessor.GetLocation(recordBuf, compSlot);
                SpatialBackPointerHelper.Write(ref bpAccessor, swappedCompChunkId, leafChunkId, (short)slotIndex, (byte)table.SpatialIndex.FieldInfo.Mode);
            }
        }
        finally
        {
            emAccessor.Dispose();
        }
    }

    // ── Layer 1 occupancy helpers ────────────────────────────────────────

    /// <summary>Compute coarse cell key from AABB center. 2D uses lossless packing, 3D uses XOR-multiply hash.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ComputeCellKey(ReadOnlySpan<double> coords, int coordCount, float inverseCellSize)
    {
        int half = coordCount / 2;
        int cellX = (int)Math.Floor((coords[0] + coords[half]) * 0.5 * inverseCellSize);
        int cellY = (int)Math.Floor((coords[1] + coords[half + 1]) * 0.5 * inverseCellSize);
        if (half == 2)
        {
            // 2D: lossless packing — XOR with MinValue to handle negative coords correctly
            return ((long)(cellX ^ int.MinValue) << 32) | (uint)(cellY ^ int.MinValue);
        }
        // 3D: XOR-multiply with Teschner primes (handles negative coords naturally)
        int cellZ = (int)Math.Floor((coords[2] + coords[half + 2]) * 0.5 * inverseCellSize);
        return (cellX * 73856093) ^ (cellY * 19349663) ^ (cellZ * 83492791);
    }

    /// <summary>Increment occupancy count for the cell containing the given coords.</summary>
    private static void IncrementOccupancy(SpatialIndexState state, ReadOnlySpan<double> coords, ChangeSet changeSet)
    {
        var map = state.OccupancyMap;
        if (map == null)
        {
            return;
        }

        long cellKey = ComputeCellKey(coords, state.Descriptor.CoordCount, state.FieldInfo.InverseCellSize);
        var accessor = map.Segment.CreateChunkAccessor(changeSet);
        try
        {
            if (map.TryGet(cellKey, out int count, ref accessor))
            {
                map.Upsert(cellKey, count + 1, ref accessor, changeSet);
                TyphonEvent.EmitSpatialGridOccupancyChange((int)cellKey, 1, (ushort)count, (ushort)(count + 1));
            }
            else
            {
                map.Insert(cellKey, 1, ref accessor, changeSet);
                TyphonEvent.EmitSpatialGridOccupancyChange((int)cellKey, 1, 0, 1);
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>Decrement occupancy count. Removes the cell entry when count reaches zero.</summary>
    private static void DecrementOccupancy(SpatialIndexState state, ReadOnlySpan<double> coords, ChangeSet changeSet)
    {
        var map = state.OccupancyMap;
        if (map == null)
        {
            return;
        }

        long cellKey = ComputeCellKey(coords, state.Descriptor.CoordCount, state.FieldInfo.InverseCellSize);
        var accessor = map.Segment.CreateChunkAccessor(changeSet);
        try
        {
            if (map.TryGet(cellKey, out int count, ref accessor))
            {
                if (count <= 1)
                {
                    map.Remove(cellKey, out _, ref accessor, changeSet);
                    TyphonEvent.EmitSpatialGridOccupancyChange((int)cellKey, -1, (ushort)count, 0);
                }
                else
                {
                    map.Upsert(cellKey, count - 1, ref accessor, changeSet);
                    TyphonEvent.EmitSpatialGridOccupancyChange((int)cellKey, -1, (ushort)count, (ushort)(count - 1));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    /// <summary>
    /// Check if all coarse cells overlapping the query AABB are empty. Returns true if the query can be skipped entirely.
    /// </summary>
    internal static bool Layer1FastReject(SpatialIndexState state, ReadOnlySpan<double> queryCoords)
    {
        var map = state.OccupancyMap;
        if (map == null)
        {
            return false; // No hashmap → can't reject, proceed to tree
        }

        var fi = state.FieldInfo;
        int coordCount = state.Descriptor.CoordCount;
        int half = coordCount / 2;

        // Compute min/max cell coordinates
        int minCellX = (int)Math.Floor(queryCoords[0] * fi.InverseCellSize);
        int minCellY = (int)Math.Floor(queryCoords[1] * fi.InverseCellSize);
        int maxCellX = (int)Math.Floor(queryCoords[half] * fi.InverseCellSize);
        int maxCellY = (int)Math.Floor(queryCoords[half + 1] * fi.InverseCellSize);

        // Cap the number of cells to probe — if the query is too large relative to cell size,
        // abandon fast-reject and proceed to tree (which handles large queries efficiently)
        const int MaxCellsToProbe = 64;
        long totalCells = (long)(maxCellX - minCellX + 1) * (maxCellY - minCellY + 1);
        if (half == 3)
        {
            int minCellZ = (int)Math.Floor(queryCoords[2] * fi.InverseCellSize);
            int maxCellZ = (int)Math.Floor(queryCoords[half + 2] * fi.InverseCellSize);
            totalCells *= (maxCellZ - minCellZ + 1);
        }
        if (totalCells > MaxCellsToProbe)
        {
            return false; // Too many cells — skip fast-reject, proceed to tree
        }

        var accessor = map.Segment.CreateChunkAccessor();
        try
        {
            if (half == 2)
            {
                // 2D: iterate all cells in the query box
                for (int cx = minCellX; cx <= maxCellX; cx++)
                {
                    for (int cy = minCellY; cy <= maxCellY; cy++)
                    {
                        long cellKey = ((long)(cx ^ int.MinValue) << 32) | (uint)(cy ^ int.MinValue);
                        if (map.TryGet(cellKey, out int count, ref accessor) && count > 0)
                        {
                            return false; // At least one populated cell → can't reject
                        }
                    }
                }
            }
            else
            {
                // 3D: iterate all cells
                int minCellZ = (int)Math.Floor(queryCoords[2] * fi.InverseCellSize);
                int maxCellZ = (int)Math.Floor(queryCoords[half + 2] * fi.InverseCellSize);
                for (int cx = minCellX; cx <= maxCellX; cx++)
                {
                    for (int cy = minCellY; cy <= maxCellY; cy++)
                    {
                        for (int cz = minCellZ; cz <= maxCellZ; cz++)
                        {
                            long cellKey = (cx * 73856093) ^ (cy * 19349663) ^ (cz * 83492791);
                            if (map.TryGet(cellKey, out int count, ref accessor) && count > 0)
                            {
                                return false;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return true; // All cells empty → reject query
    }
}
