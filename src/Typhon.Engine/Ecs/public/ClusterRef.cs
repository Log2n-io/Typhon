using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;

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
    private readonly ClusterSpatialAabb[] _clusterAabbs; // null when archetype has no spatial index

    internal ClusterRef(byte* basePtr, byte* transientBasePtr, ArchetypeClusterInfo layout, ArchetypeMetadata meta, int chunkId,
        ClusterSpatialAabb[] clusterAabbs)
    {
        _base = basePtr;
        _transientBase = transientBasePtr;
        _layout = layout;
        _meta = meta;
        _chunkId = chunkId;
        _clusterAabbs = clusterAabbs;
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
        byte slot = _meta.GetSlot(comp._componentTypeId);
        Debug.Assert((_meta.VersionedSlotMask & (1 << slot)) == 0,
            $"GetSpan on Versioned component bypasses revision chain. Use GetReadOnlySpan for reads, OpenMut+Write for writes.");
        return new Span<T>(ResolveBase(slot) + _layout.ComponentOffset(slot), _layout.ClusterSize);
    }

    /// <summary>Get a read-only span of component data for all N slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> GetReadOnlySpan<T>(Comp<T> comp) where T : unmanaged
    {
        byte slot = _meta.GetSlot(comp._componentTypeId);
        return new ReadOnlySpan<T>(ResolveBase(slot) + _layout.ComponentOffset(slot), _layout.ClusterSize);
    }

    /// <summary>Get a mutable reference to a single component value at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>(Comp<T> comp, int slotIndex) where T : unmanaged
    {
        byte slot = _meta.GetSlot(comp._componentTypeId);
        Debug.Assert((_meta.VersionedSlotMask & (1 << slot)) == 0, "Get on Versioned component bypasses revision chain. Use OpenMut+Write for writes.");
        return ref Unsafe.Add(ref Unsafe.AsRef<T>(ResolveBase(slot) + _layout.ComponentOffset(slot)), slotIndex);
    }

    /// <summary>Get a read-only reference to a single component value at the given slot index.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T GetReadOnly<T>(Comp<T> comp, int slotIndex) where T : unmanaged
    {
        byte slot = _meta.GetSlot(comp._componentTypeId);
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
        get => ref (_clusterAabbs != null ? ref _clusterAabbs[_chunkId] : ref ClusterSpatialAabb.s_empty);
    }
}

/// <summary>
/// Iterates active clusters for an archetype. Owns a <see cref="ChunkAccessor{TStore}"/> — must be disposed.
/// </summary>
/// <remarks>
/// <para>Supports <c>foreach</c> via <see cref="GetEnumerator"/>.</para>
/// <para>Usage:</para>
/// <code>
/// foreach (var cluster in ants.GetClusterEnumerator())
/// {
///     ulong bits = cluster.OccupancyBits;
///     var positions = cluster.GetSpan&lt;Position&gt;(Ant.Position);
///     while (bits != 0) { ... }
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
        int chunkId = _clusterIds[_index];
        byte* basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
        ulong occupancy = *(ulong*)basePtr;
        while (occupancy != 0)
        {
            int slot = BitOperations.TrailingZeroCount(occupancy);
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

    /// <summary>Advance to the next active cluster in the range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext() => ++_index < _endIndex;

    /// <summary>Get the current cluster ref.</summary>
    public ClusterRef<TArch> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int chunkId = _clusterIds[_index];
            // Primary base: PersistentStore for mixed/SV, TransientStore for pure-Transient
            byte* basePtr = _hasPersistentAccessor ? _accessor.GetChunkAddress(chunkId) : _transientAccessor.GetChunkAddress(chunkId);
            // TransientStore base for mixed archetypes (null for pure-SV/V and pure-Transient)
            byte* transientPtr = (_hasTransientAccessor && _hasPersistentAccessor) ? _transientAccessor.GetChunkAddress(chunkId) : null;
            return new ClusterRef<TArch>(basePtr, transientPtr, _state.Layout, _meta, chunkId, _state.ClusterAabbs);
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
