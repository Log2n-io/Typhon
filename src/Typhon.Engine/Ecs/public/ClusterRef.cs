using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Per-cluster accessor providing typed, zero-copy access to component SoA arrays.
/// Created by <see cref="ClusterEnumerator{TArch}"/>. Must not outlive the enumerator.
/// </summary>
/// <remarks>
/// <para>Component data is laid out in Structure-of-Arrays format within the cluster:
/// <c>Component₀[N], Component₁[N], ...</c> where N is the cluster size (8..64).</para>
/// <para>Iteration pattern using OccupancyBits TZCNT loop:</para>
/// <code>
/// ulong bits = cluster.OccupancyBits;
/// while (bits != 0)
/// {
///     int idx = BitOperations.TrailingZeroCount(bits);
///     bits &amp;= bits - 1;
///     ref var pos = ref cluster.Get(Ant.Position, idx);
///     // ...
/// }
/// </code>
/// </remarks>
[PublicAPI]
public unsafe ref struct ClusterRef<TArch> where TArch : class
{
    private readonly byte* _base;
    private readonly byte* _transientBase;  // TransientStore cluster base; null for pure-SV/V or pure-Transient (where _base IS TS)
    private readonly ArchetypeClusterInfo _layout;
    private readonly ArchetypeMetadata _meta;
    private readonly int _chunkId;
    private readonly ArchetypeClusterState _state; // null only on synthetic test refs; carries spatial bookkeeping + grid

    internal ClusterRef(byte* basePtr, byte* transientBasePtr, ArchetypeClusterInfo layout, ArchetypeMetadata meta, int chunkId, ArchetypeClusterState state)
    {
        _base = basePtr;
        _transientBase = transientBasePtr;
        _layout = layout;
        _meta = meta;
        _chunkId = chunkId;
        _state = state;
    }

    /// <summary>Bitmask of occupied slots. Bit i = 1 means slot i contains a live entity.</summary>
    public ulong OccupancyBits
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => *(ulong*)_base;
    }

    /// <summary>Bitmask of entities with component at <paramref name="slot"/> enabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong EnabledBits(int slot) => *(ulong*)(_base + _layout.EnabledBitsOffset(slot));

    /// <summary>Combined mask: alive AND component at slot enabled.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ActiveBits(int slot) => OccupancyBits & EnabledBits(slot);

    /// <summary>Number of live entities in this cluster (PopCount of OccupancyBits).</summary>
    public int LiveCount
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitOperations.PopCount(OccupancyBits);
    }

    /// <summary>True when all slots are occupied.</summary>
    public bool IsFull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => OccupancyBits == _layout.FullMask;
    }

    /// <summary>Cluster size N (number of slots, 8..64).</summary>
    public int ClusterSize
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _layout.ClusterSize;
    }

    /// <summary>Full mask with lower N bits set.</summary>
    public ulong FullMask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _layout.FullMask;
    }

    /// <summary>Get a mutable span of component data for all N slots (SoA array).
    /// For Versioned components, use <see cref="GetReadOnlySpan{T}"/> — writing directly to the cluster slot
    /// bypasses the revision chain and breaks MVCC snapshot isolation.</summary>
    /// <summary>Resolve the correct base pointer for a component slot (Transient → _transientBase, else → _base).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* ResolveBase(byte slot) => (_transientBase != null && (_meta.TransientSlotMask & (1 << slot)) != 0) ? _transientBase : _base;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> GetSpan<T>(Comp<T> comp) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        Debug.Assert((_meta.VersionedSlotMask & (1 << slot)) == 0,
            $"GetSpan on Versioned component bypasses revision chain. Use GetReadOnlySpan for reads, OpenMut+Write for writes.");
        return new Span<T>(ResolveBase(slot) + _layout.ComponentOffset(slot), _layout.ClusterSize);
    }

    /// <summary>Get a read-only span of component data for all N slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> GetReadOnlySpan<T>(Comp<T> comp) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        return new ReadOnlySpan<T>(ResolveBase(slot) + _layout.ComponentOffset(slot), _layout.ClusterSize);
    }

    /// <summary>Get a mutable reference to a single component value at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>(Comp<T> comp, int slotIndex) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        Debug.Assert((_meta.VersionedSlotMask & (1 << slot)) == 0, "Get on Versioned component bypasses revision chain. Use OpenMut+Write for writes.");
        return ref Unsafe.Add(ref Unsafe.AsRef<T>(ResolveBase(slot) + _layout.ComponentOffset(slot)), slotIndex);
    }

    /// <summary>Get a read-only reference to a single component value at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T GetReadOnly<T>(Comp<T> comp, int slotIndex) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        return ref Unsafe.Add(ref Unsafe.AsRef<T>(ResolveBase(slot) + _layout.ComponentOffset(slot)), slotIndex);
    }

    /// <summary>Entity keys for all N slots. Use with slot index to reconstruct EntityId.</summary>
    public ReadOnlySpan<long> EntityIds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_base + _layout.EntityIdsOffset, _layout.ClusterSize);
    }

    /// <summary>Read EntityId for the entity at the given slot (stored as full packed EntityId).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityId GetEntityId(int slotIndex) =>
        EntityId.FromRaw(*(long*)(_base + _layout.EntityIdsOffset + slotIndex * 8));

    /// <summary>The chunk ID of this cluster within the archetype's segment.</summary>
    public int ChunkId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _chunkId;
    }

    /// <summary>
    /// Tight AABB of all entities in this cluster. Returns the empty sentinel (min = +inf, max = -inf) when the archetype has no spatial index.
    /// For 2D archetypes, MinZ/MaxZ are ±infinity sentinels — use MinX/MinY/MaxX/MaxY only.
    /// </summary>
    public ref readonly ClusterSpatialAabb SpatialBounds
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref (_state?.ClusterAabbs != null ? ref _state.ClusterAabbs[_chunkId] : ref ClusterSpatialAabb.s_empty);
    }

    /// <summary>
    /// Write-barrier API for spatial components — the canonical replacement for <c>cluster.GetSpan&lt;T&gt;()[slotIndex] = ...</c> when <c>T</c> contains the
    /// archetype's <see cref="SpatialIndexAttribute"/>-marked field. Performs (in order):
    /// <list type="number">
    /// <item>Reads the OLD spatial-field bytes at <paramref name="slotIndex"/></item>
    /// <item>Writes <paramref name="newValue"/> to the slot</item>
    /// <item>Updates <see cref="ArchetypeClusterState.ClusterAabbs"/> inline on AABB grow (O(1) CAS per axis)</item>
    /// <item>Flags <see cref="ArchetypeClusterState.ClusterShrinkPendingAxes"/> for axes where this slot was at an extreme and moved inward — fence rescans
    ///       only this cluster on those axes</item>
    /// <item>Flags <see cref="ArchetypeClusterState.ClusterMigrationPendingSlots"/> when the new position crosses the cell+hysteresis boundary — fence drains
    ///       the migration without any full scan</item>
    /// <item>Sets the cluster's bit in <see cref="ArchetypeClusterState.ClusterProcessBitmap"/> so the fence loop visits this cluster</item>
    /// </list>
    /// <para>
    /// V1 supports <see cref="SpatialFieldType.AABB2F"/> only (AntHill's <c>WorldBounds</c>). Other field types throw <see cref="NotSupportedException"/>.
    /// </para>
    /// <para>
    /// <b>WriteSpatial does NOT mark the slot dirty</b> (via <see cref="ArchetypeClusterState.SetDirty"/>).
    /// The dirty bitmap drives WAL serialization and change-filtered dispatch — for high-frequency  simulation state (e.g., AntHill's ant positions), marking
    /// every slot dirty floods the WAL writer with one frame per entity per tick → backpressure that stalls TickDriver. The fence-time spatial maintenance does
    /// not need the dirty bit; it consumes <see cref="ArchetypeClusterState.ClusterMigrationPendingSlots"/> /
    /// <see cref="ArchetypeClusterState.ClusterProcessBitmap"/> directly. If your workload genuinely needs WAL persistence of the spatial field (e.g.,
    /// resumable autosave), either write through the MVCC <c>Transaction.OpenMut + Write</c> path (which marks dirty), or
    /// call <see cref="ArchetypeClusterState.SetDirty"/> explicitly after <c>WriteSpatial</c>.
    /// </para>
    /// <para>
    /// Thread safety: safe to call concurrently from multiple workers operating on different slots of any cluster (including the same cluster). All
    /// bookkeeping writes use <see cref="Interlocked"/> primitives.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSpatial<T>(Comp<T> comp, int slotIndex, in T newValue) where T : unmanaged
    {
        var slot = _meta.GetSlot(comp._componentTypeId);
        Debug.Assert((_meta.VersionedSlotMask & (1 << slot)) == 0, $"WriteSpatial on Versioned component bypasses revision chain.");
        Debug.Assert(_state != null && _state.SpatialSlot.HasSpatialIndex && _state.SpatialSlot.Slot == slot,
            "WriteSpatial requires the archetype's spatial-indexed component (the one marked [SpatialIndex]). " +
            "For non-spatial fields, use GetSpan or Get.");

        var spatialSlot = _state.SpatialSlot;
        var slotBytes = ResolveBase(slot) + _layout.ComponentOffset(slot) + slotIndex * sizeof(T);
        var fieldPtr = slotBytes + spatialSlot.FieldOffset;

        var fieldType = spatialSlot.FieldInfo.FieldType;
        if (fieldType == SpatialFieldType.AABB2F)
        {
            WriteSpatialAabb2F(slotIndex, slotBytes, fieldPtr, in newValue);
        }
        else
        {
            // TODO: specialize AABB3F / BSphere2F / BSphere3F / double variants.
            throw new NotSupportedException($"WriteSpatial: spatial field type {fieldType} not yet supported. V1 supports AABB2F only.");
        }
    }

    /// <summary>AABB2F specialization of <see cref="WriteSpatial{T}"/>. Inlined into the barrier on the AntHill hot path (WorldBounds.Bounds is AABB2F,
    /// point-form-encoded with MinX==MaxX, MinY==MaxY).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSpatialAabb2F<T>(int slotIndex, byte* slotBytes, byte* fieldPtr, in T newValue) where T : unmanaged
    {
        // Read old AABB before overwriting (fieldPtr points at the AABB2F inside the component).
        ref var oldAabb = ref *(AABB2F*)fieldPtr;
        var oldMinX = oldAabb.MinX;
        var oldMinY = oldAabb.MinY;
        var oldMaxX = oldAabb.MaxX;
        var oldMaxY = oldAabb.MaxY;

        // Write the new value (full T struct, may include non-spatial fields).
        *(T*)slotBytes = newValue;

        // Re-read the AABB2F from the freshly-written value (handles offset within T).
        ref var newAabb = ref *(AABB2F*)fieldPtr;
        var newMinX = newAabb.MinX;
        var newMinY = newAabb.MinY;
        var newMaxX = newAabb.MaxX;
        var newMaxY = newAabb.MaxY;

        // NOTE: WriteSpatial deliberately does NOT call _state.SetDirty for the spatial slot. The dirty bitmap drives WAL serialization and change-filtered
        // dispatch; for cluster archetypes where the spatial component is high-frequency simulation state (e.g., AntHill's WorldBounds), marking every slot
        // dirty floods the WAL with 100k frames/tick → backpressure. The fence-time spatial maintenance does NOT need the dirty bit — it consumes
        // ClusterMigrationPendingSlots / ClusterProcessBitmap directly. Callers that genuinely need WAL persistence of the spatial field should mutate it via
        // the MVCC Transaction path (which marks dirty), or call _state.SetDirty explicitly after WriteSpatial. See claude/design/spatial/write-time-spatial.md.

        // Step 4 + 5: AABB grow inline (CAS) and shrink flag.
        ref var stored = ref _state.ClusterAabbs[_chunkId];
        var aabbChanged = MaybeGrowAndFlagShrink(ref stored, oldMinX, oldMinY, oldMaxX, oldMaxY, newMinX, newMinY, newMaxX, newMaxY);

        // Step 6: migration check.
        var migrationFlagged = MaybeFlagMigration(slotIndex, newMinX, newMinY, newMaxX, newMaxY);

        // Step 6b: bump the fence work-planner's migration cost hint. Non-atomic: an order-of-magnitude approximation is enough for chunk bucketing; lost
        // increments under contention are tolerable.
        if (migrationFlagged)
        {
            _state.MigrationHint++;
        }

        // Step 7: visibility for the fence loop.
        if (aabbChanged || migrationFlagged)
        {
            SetClusterProcessBit();
        }
    }

    /// <summary>
    /// Inline AABB-grow (CAS per axis) + shrink-pending-axes flag. Returns true when either axis-extreme moved (in which case the cluster needs a
    /// fence-time <c>PerCellIndex.UpdateAt</c> with the fresh AABB).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaybeGrowAndFlagShrink(ref ClusterSpatialAabb stored, float oldMinX, float oldMinY, float oldMaxX, float oldMaxY, float newMinX, float newMinY, 
        float newMaxX, float newMaxY)
    {
        var changed = false;

        // GROW path (CAS loop per axis). Note: AABB2F has min/max as separate fields, so we CAS each independently.
        // The 2D AABB is stored on ClusterSpatialAabb (which has 3D fields; we touch only X/Y here).
        if (newMinX < stored.MinX) { CasMin(ref stored.MinX, newMinX); changed = true; }
        if (newMinY < stored.MinY) { CasMin(ref stored.MinY, newMinY); changed = true; }
        if (newMaxX > stored.MaxX) { CasMax(ref stored.MaxX, newMaxX); changed = true; }
        if (newMaxY > stored.MaxY) { CasMax(ref stored.MaxY, newMaxY); changed = true; }

        // SHRINK flag (only set when this slot WAS at an extreme AND moved inward). Bit layout:
        // 0x01=MinX, 0x02=MaxX, 0x04=MinY, 0x08=MaxY (matches ClusterShrinkPendingAxes doc).
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

        if (shrinkMask != 0)
        {
            // byte[] doesn't support Interlocked.Or directly; widen to int[] view at the chunk index. Cluster count is at most a few thousand → bool array
            // would also work, but byte[] keeps the mask compact. We use Interlocked.Or on int slice — see ClusterShrinkPendingAxesOr below.
            InterlockedOrByteArrayElement(_state.ClusterShrinkPendingAxes, _chunkId, shrinkMask);
            changed = true;
        }

        return changed;
    }

    /// <summary>CAS-loop float min update: write <paramref name="candidate"/> if it's still less than the stored value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CasMin(ref float storedRef, float candidate)
    {
        while (true)
        {
            var current = storedRef;
            if (candidate >= current)
            {
                return;
            }

            var currentBits = BitConverter.SingleToInt32Bits(current);
            var candidateBits = BitConverter.SingleToInt32Bits(candidate);
            ref var storedAsInt = ref Unsafe.As<float, int>(ref storedRef);
            if (Interlocked.CompareExchange(ref storedAsInt, candidateBits, currentBits) == currentBits)
            {
                return;
            }
            // Another thread updated; retry the comparison against the new value.
        }
    }

    /// <summary>CAS-loop float max update: write <paramref name="candidate"/> if it's still greater than the stored value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CasMax(ref float storedRef, float candidate)
    {
        while (true)
        {
            var current = storedRef;
            if (candidate <= current)
            {
                return;
            }

            var currentBits = BitConverter.SingleToInt32Bits(current);
            var candidateBits = BitConverter.SingleToInt32Bits(candidate);
            ref var storedAsInt = ref Unsafe.As<float, int>(ref storedRef);
            if (Interlocked.CompareExchange(ref storedAsInt, candidateBits, currentBits) == currentBits)
            {
                return;
            }
        }
    }

    /// <summary>Atomic OR of a small mask into a single byte of a byte[]. Implemented via CAS on the byte's aligned int word slice; safe across writers
    /// targeting different bytes within the same word.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void InterlockedOrByteArrayElement(byte[] array, int index, byte mask)
    {
        // CAS loop on the byte directly: read, OR, CompareExchange byte's int-aligned word. Since `byte` is 1-byte and Interlocked operates on int+, we widen
        // to a per-element approach: each cluster index gets its own array slot, so within-byte word collisions only happen across nearby cluster indices.
        // A simple CAS loop on the single byte slot suffices.
        while (true)
        {
            var current = array[index];
            var updated = (byte)(current | mask);
            if (current == updated)
            {
                return; // mask already set
            }

            // Use Interlocked.CompareExchange on the byte directly via Unsafe.As<byte, int>. Since the byte is part of a larger int chunk, we operate on
            // a 1-byte CAS via a small helper. .NET 7+ has Interlocked.CompareExchange(ref byte, byte, byte) — use it.
            if (Interlocked.CompareExchange(ref array[index], updated, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Migration cell-boundary check. Returns true when a migration was flagged.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaybeFlagMigration(int slotIndex, float newMinX, float newMinY, float newMaxX, float newMaxY)
    {
        var grid = _state.Grid;
        if (grid == null)
        {
            return false;
        }

        var clusterCellMap = _state.ClusterCellMap;
        if (clusterCellMap == null)
        {
            return false;
        }

        var currentCellKey = clusterCellMap[_chunkId];
        if (currentCellKey < 0)
        {
            return false;
        }

        // Use AABB center for the migration check. AntHill point-form encodes pos as (MinX==MaxX, MinY==MaxY), so center == pos. For non-degenerate AABB2F
        // (future spatial systems), the center is the canonical cell-bucketing point (matches WorldToCellKeyFromSpatialField's behavior for AABB2F).
        var centerX = 0.5f * (newMinX + newMaxX);
        var centerY = 0.5f * (newMinY + newMaxY);

        ref readonly var cfg = ref grid.Config;
        var cellSize = cfg.CellSize;
        var hyster = cellSize * cfg.MigrationHysteresisRatio;
        var (cx, cy) = grid.CellKeyToCoords(currentCellKey);
        var cellMinX = cfg.WorldMin.X + cx * cellSize;
        var cellMinY = cfg.WorldMin.Y + cy * cellSize;
        var cellMaxX = cellMinX + cellSize;
        var cellMaxY = cellMinY + cellSize;

        var exited = centerX < cellMinX - hyster || centerX > cellMaxX + hyster || centerY < cellMinY - hyster || centerY > cellMaxY + hyster;
        if (!exited)
        {
            return false;
        }

        var newCellKey = grid.WorldToCellKey(centerX, centerY);
        if (newCellKey == currentCellKey)
        {
            return false;
        }

        // Set bit in per-cluster migration bitmap (atomic OR), and stomp dest cell key. By cluster-coherence invariant, two simultaneous writers to the same
        // cluster end up with the same dest key (modulo racing reads of WorldToCellKey on truly racing positions — fence re-reads positions when draining,
        // so stale dest keys self-correct).
        var slotBit = 1UL << slotIndex;
        Interlocked.Or(ref _state.ClusterMigrationPendingSlots[_chunkId], slotBit);
        _state.ClusterMigrationDestCellKeys[_chunkId] = newCellKey;
        return true;
    }

    /// <summary>Atomically set this cluster's bit in <see cref="ArchetypeClusterState.ClusterProcessBitmap"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetClusterProcessBit()
    {
        var wordIdx = _chunkId >> 6;
        var bit = 1L << (_chunkId & 63);
        Interlocked.Or(ref _state.ClusterProcessBitmap[wordIdx], bit);
    }
}

/// <summary>
/// Iterates active clusters for an archetype. Owns a <see cref="ChunkAccessor{TStore}"/> — must be disposed.
/// </summary>
/// <remarks>
/// <para>Supports <c>foreach</c> via <see cref="GetEnumerator"/>.</para>
/// <para>
/// <b>Non-empty guarantee:</b> the enumerator always yields clusters with <c>OccupancyBits != 0</c> (i.e. <c>LiveCount &gt;= 1</c>). Empty clusters can exist
/// in <c>ActiveClusterIds</c> during the fence's deferred-drain window — between Migrate (last slot released) and Finalize (chunk freed) — but
/// <see cref="MoveNext"/> filters them out so callers never observe a drained cluster.
/// </para>
/// <para>Usage:</para>
/// <code>
/// foreach (var cluster in ants.GetClusterEnumerator())
/// {
///     var positions = cluster.GetSpan&lt;Position&gt;(Ant.Position);
///     ulong bits = cluster.OccupancyBits; // guaranteed non-zero
///     while (bits != 0)
///     {
///         int slot = BitOperations.TrailingZeroCount(bits);
///         bits &amp;= bits - 1;
///         // ...
///     }
/// }
/// </code>
/// </remarks>
[PublicAPI]
public unsafe ref struct ClusterEnumerator<TArch> where TArch : class
{
    private ArchetypeClusterState _state;
    private ArchetypeMetadata _meta;
    private ChunkAccessor<PersistentStore> _accessor;
    private ChunkAccessor<TransientStore> _transientAccessor;
    private bool _hasTransientAccessor;
    private bool _hasPersistentAccessor;
    // Issue #231: source array for cluster chunk ids. Defaults to state.ActiveClusterIds but can point at a tier-filtered partition supplied
    // by TickContext.ClusterIds when a system declares a tier filter.
    private int[] _clusterIds;
    private int _index;
    private int _endIndex;

    [AllowCopy]
    internal static ClusterEnumerator<TArch> Create(ArchetypeClusterState state, ArchetypeMetadata meta,
        ChunkBasedSegment<PersistentStore> segment, ChunkBasedSegment<TransientStore> transientSegment = null)
    {
        var result = new ClusterEnumerator<TArch> { _state = state, _meta = meta };
        if (segment != null)
        {
            result._accessor = segment.CreateChunkAccessor();
            result._hasPersistentAccessor = true;
        }
        if (transientSegment != null)
        {
            result._transientAccessor = transientSegment.CreateChunkAccessor();
            result._hasTransientAccessor = true;
        }
        result._clusterIds = state.ActiveClusterIds;
        result._index = -1;
        result._endIndex = state.ActiveClusterCount;
        return result;
    }

    /// <summary>
    /// Create a scoped enumerator that iterates a range of <see cref="ArchetypeClusterState.ActiveClusterIds"/>.
    /// Used by non-tier-filtered parallel dispatch to partition cluster work across workers.
    /// </summary>
    [AllowCopy]
    internal static ClusterEnumerator<TArch> CreateScoped(ArchetypeClusterState state, ArchetypeMetadata meta,
        ChunkBasedSegment<PersistentStore> segment, ChunkBasedSegment<TransientStore> transientSegment,
        int startIndex, int endIndex)
    {
        var result = new ClusterEnumerator<TArch> { _state = state, _meta = meta };
        if (segment != null)
        {
            result._accessor = segment.CreateChunkAccessor();
            result._hasPersistentAccessor = true;
        }
        if (transientSegment != null)
        {
            result._transientAccessor = transientSegment.CreateChunkAccessor();
            result._hasTransientAccessor = true;
        }
        result._clusterIds = state.ActiveClusterIds;
        result._index = startIndex - 1;
        result._endIndex = endIndex;
        return result;
    }

    /// <summary>
    /// Create a scoped enumerator over an explicit cluster-id source array (issue #231). The source is typically a per-tier cluster list returned
    /// by <see cref="TierClusterIndex.GetClusters"/>. The range <c>[startIndex, endIndex)</c> indexes into <paramref name="clusterIds"/>, not
    /// into <see cref="ArchetypeClusterState.ActiveClusterIds"/>.
    /// </summary>
    [AllowCopy]
    internal static ClusterEnumerator<TArch> CreateScoped(ArchetypeClusterState state, ArchetypeMetadata meta, ChunkBasedSegment<PersistentStore> segment, 
        ChunkBasedSegment<TransientStore> transientSegment, int[] clusterIds, int startIndex, int endIndex)
    {
        ArgumentNullException.ThrowIfNull(clusterIds);
        var result = new ClusterEnumerator<TArch> { _state = state, _meta = meta };
        if (segment != null)
        {
            result._accessor = segment.CreateChunkAccessor();
            result._hasPersistentAccessor = true;
        }
        if (transientSegment != null)
        {
            result._transientAccessor = transientSegment.CreateChunkAccessor();
            result._hasTransientAccessor = true;
        }
        result._clusterIds = clusterIds;
        result._index = startIndex - 1;
        result._endIndex = endIndex;
        return result;
    }

    /// <summary>The chunk ID of the current cluster. Available after <see cref="MoveNext"/> returns true.</summary>
    public int CurrentChunkId
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _clusterIds[_index];
    }

    /// <summary>
    /// Mark all occupied slots in the current cluster as dirty. Call this after writing to component data via
    /// <see cref="ClusterRef{TArch}.GetSpan{T}"/> — the direct cluster path does not set dirty bits automatically.
    /// Without this call, <c>DetectClusterMigrations</c> and the WAL tick fence will not see the changes.
    /// </summary>
    public void MarkCurrentDirty()
    {
        var chunkId = _clusterIds[_index];
        var basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
        var occupancy = *(ulong*)basePtr;
        while (occupancy != 0)
        {
            var slot = BitOperations.TrailingZeroCount(occupancy);
            occupancy &= occupancy - 1;
            _state.SetDirty(chunkId, slot);
        }
    }

    /// <summary>
    /// Mark a single slot in the current cluster as dirty. More precise than <see cref="MarkCurrentDirty"/> —
    /// use when only specific entities changed (e.g., after a cell-boundary crossing check). The slot index
    /// is the bit position from the <see cref="ClusterRef{TArch}.OccupancyBits"/> TZCNT loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MarkSlotDirty(int slotIndex) => _state.SetDirty(_clusterIds[_index], slotIndex);

    /// <summary>
    /// Advance to the next active cluster in the range, skipping drained clusters (<see cref="ClusterRef{TArch}.OccupancyBits"/> == 0) left in
    /// <c>ActiveClusterIds</c> by the fence's deferred-drain window. Guarantees <c>Current.OccupancyBits != 0</c> when returning true.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext()
    {
        while (++_index < _endIndex)
        {
            var chunkId = _clusterIds[_index];
            var basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
            if (*(ulong*)basePtr != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Get the current cluster ref.</summary>
    public ClusterRef<TArch> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var chunkId = _clusterIds[_index];
            // Primary base: PersistentStore for mixed/SV, TransientStore for pure-Transient
            var basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
            // TransientStore base for mixed archetypes (null for pure-SV/V and pure-Transient)
            var transientPtr = (_hasTransientAccessor && _hasPersistentAccessor) ? _transientAccessor.GetChunkAddress(chunkId) : null;
            return new ClusterRef<TArch>(basePtr, transientPtr, _state.Layout, _meta, chunkId, _state);
        }
    }

    /// <summary>Release the ChunkAccessors.</summary>
    public void Dispose()
    {
        if (_hasPersistentAccessor)
        {
            _accessor.Dispose();
        }
        if (_hasTransientAccessor)
        {
            _transientAccessor.Dispose();
        }
    }

    /// <summary>Enable foreach.</summary>
    public ClusterEnumerator<TArch> GetEnumerator() => this;
}
