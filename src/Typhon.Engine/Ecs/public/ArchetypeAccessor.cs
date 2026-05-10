using System;
using System.Runtime.CompilerServices;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Fast-path entity accessor pre-bound to a specific archetype.
/// Bypasses epoch checks, archetype lookup, and null guards that are redundant for PTA workers.
/// <para>Created via <see cref="EntityAccessor.For{TArch}"/>. Must be disposed after use.</para>
/// </summary>
/// <remarks>
/// <para><b>What it skips per-entity vs <see cref="EntityAccessor.ResolveEntity"/>:</b></para>
/// <list type="bullet">
///   <item>EpochThreadRegistry.IsCurrentThreadInScope check (ThreadStatic access)</item>
///   <item>ArchetypeRegistry.GetMetadata lookup</item>
///   <item>Null guards on archetype state and entity map</item>
///   <item>EntityMap ChunkAccessor cache check (always same archetype)</item>
/// </list>
/// <para>Versioned components are supported — revision chain walk is performed only for Versioned slots.
/// SV/Transient slots skip the chain walk entirely (the common fast path for game systems).</para>
/// <para>Cluster storage: when the archetype uses cluster storage, Resolve reads ClusterEntityRecord from the EntityMap and populates EntityRef's cluster
/// fields for direct SoA access.</para>
/// </remarks>
[PublicAPI]
public unsafe ref struct ArchetypeAccessor<TArch> where TArch : class
{
    private readonly ArchetypeMetadata _archetype;
    private readonly ArchetypeEngineState _engineState;
    private readonly EntityAccessor _accessor;
    private readonly EnabledBitsOverrides _enabledBitsOverrides;
    private readonly long _tsn;
    private readonly int _recordSize;
    private readonly bool _hasVersionedSlots;
    private bool _mutationPrepared;
    private ChunkAccessor<PersistentStore> _entityMapAccessor;

    // ── Cluster storage fields ──────────────────────────────────────────
    private readonly bool _hasClusterStorage;
    private readonly ArchetypeClusterState _clusterState;
    private ChunkAccessor<PersistentStore> _clusterAccessor;
    private ChunkAccessor<TransientStore> _transientClusterAccessor;
    private readonly bool _hasTransientCluster;

    internal ArchetypeAccessor(ArchetypeMetadata archetype, ArchetypeEngineState engineState, EntityAccessor accessor, DatabaseEngine dbe)
    {
        _archetype = archetype;
        _engineState = engineState;
        _accessor = accessor;
        _enabledBitsOverrides = dbe.EnabledBitsOverrides;
        _tsn = accessor.TSN;
        _recordSize = archetype._entityRecordSize;
        _entityMapAccessor = engineState.EntityMap.Segment.CreateChunkAccessor();

        // Detect if any component uses Versioned storage (needs revision chain walk)
        _hasVersionedSlots = false;
        for (int slot = 0; slot < archetype.ComponentCount; slot++)
        {
            if (engineState.SlotToComponentTable[slot].StorageMode == StorageMode.Versioned)
            {
                _hasVersionedSlots = true;
            }

            // Pre-warm ComponentInfo cache — ensures EntityRef.Read/Write hits the fast array path
            accessor.EnsureComponentInfoCached(archetype._slotToComponentType[slot]);
        }

        // Cluster storage setup
        _hasClusterStorage = archetype.IsClusterEligible && engineState.ClusterState != null;
        _clusterState = engineState.ClusterState;
        _clusterAccessor = _hasClusterStorage && _clusterState.ClusterSegment != null ? _clusterState.ClusterSegment.CreateChunkAccessor() : default;
        _hasTransientCluster = _hasClusterStorage && _clusterState.TransientSegment != null;
        _transientClusterAccessor = _hasTransientCluster ? _clusterState.TransientSegment.CreateChunkAccessor() : default;
    }

    /// <summary>Open an entity for read-only access.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityRef Open(EntityId id) => Resolve(id, false);

    /// <summary>Open an entity for read-write access.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityRef OpenMut(EntityId id)
    {
        if (!_mutationPrepared)
        {
            _accessor.PrepareForMutation();
            _mutationPrepared = true;
        }
        return Resolve(id, true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EntityRef Resolve(EntityId id, bool writable)
    {
        byte* readBuf = stackalloc byte[_recordSize];
        if (!_engineState.EntityMap.TryGet(id.EntityKey, readBuf, ref _entityMapAccessor))
        {
            return default;
        }

        ref var header = ref EntityRecordAccessor.GetHeader(readBuf);
        ushort enabledBits = _enabledBitsOverrides.ResolveEnabledBits(id.EntityKey, header.EnabledBits, _tsn);

        var result = new EntityRef(id, _archetype, _engineState, _accessor, enabledBits, writable);

        if (_hasClusterStorage)
        {
            // Cluster path: read ClusterEntityRecord → resolve cluster base + slot
            int clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(readBuf);
            byte slotIndex = ClusterEntityRecordAccessor.GetSlotIndex(readBuf);

            // Primary base: PersistentStore for mixed/SV, TransientStore for pure-Transient
            result._clusterBase = _clusterState.ClusterSegment != null ? 
                _clusterAccessor.GetChunkAddress(clusterChunkId, writable) : _transientClusterAccessor.GetChunkAddress(clusterChunkId, writable);

            // Mixed archetype: also set TransientStore base for Transient component reads
            if (_hasTransientCluster && _clusterState.ClusterSegment != null)
            {
                result._transientClusterBase = _transientClusterAccessor.GetChunkAddress(clusterChunkId, writable);
            }

            result._clusterSlotIndex = slotIndex;
            result._clusterChunkId = clusterChunkId;
            result._clusterLayout = _clusterState.Layout;

            // For Versioned slots, walk chain and populate _locations for MVCC reads
            if (_hasVersionedSlots)
            {
                ResolveClusterVersionedSlots(readBuf, id, ref result);
            }
        }
        else
        {
            // Legacy path: copy per-component locations
            result.CopyLocationsFrom(readBuf, _archetype.ComponentCount);

            // Versioned components: walk revision chain to find visible content chunk.
            // SV/Transient: location from EntityRecord is the direct content chunk — no walk needed.
            if (_hasVersionedSlots)
            {
                ResolveVersionedSlots(ref result);
            }
        }

        return result;
    }

    /// <summary>
    /// Resolve Versioned component slots for cluster entities.
    /// Walks the revision chain for each Versioned slot and stores the visible content chunkId in _locations.
    /// This enables EntityRef.Read to route Versioned reads through the content chunk (MVCC-correct) while SV reads go through the cluster slot (fast path).
    /// </summary>
    private void ResolveClusterVersionedSlots(byte* record, EntityId id, ref EntityRef result)
    {
        var layout = _archetype.ClusterLayout;
        if (layout.SlotToVersionedIndex == null)
        {
            return;
        }

        long pk = (long)id.RawValue;

        for (int slot = 0; slot < _archetype.ComponentCount; slot++)
        {
            int vi = layout.SlotToVersionedIndex[slot];
            if (vi < 0)
            {
                continue;
            }

            int compRevFirstChunkId = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(record, vi);
            if (compRevFirstChunkId == 0)
            {
                continue;
            }

            var compTypeId = _archetype._componentTypeIds[slot];
            var info = _accessor.GetComponentInfoInternal(compTypeId, _archetype._slotToComponentType[slot]);

            // Check cache first (prior Open or Write in this transaction)
            if (!info.IsMultiple && info.SingleCache.TryGetValue(pk, out var cached))
            {
                result.SetLocation(slot, cached.CurCompContentChunkId);
                continue;
            }

            var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, _tsn, true);
            if (chainResult.IsFailure)
            {
                continue;
            }

            // Cache CompRevInfo for conflict detection and COW (EcsVersionedCopyOnWrite reads from this cache)
            var compRevInfo = chainResult.Value;
            compRevInfo.Operations = ComponentInfo.OperationType.Read;
            info.AddNew(pk, compRevInfo);
            result.SetLocation(slot, compRevInfo.CurCompContentChunkId);
        }
    }

    private void ResolveVersionedSlots(ref EntityRef result)
    {
        long pk = (long)result._id.RawValue;

        for (int slot = 0; slot < _archetype.ComponentCount; slot++)
        {
            var table = _engineState.SlotToComponentTable[slot];
            if (table.StorageMode != StorageMode.Versioned)
            {
                continue;
            }

            int compRevFirstChunkId = result.GetLocation(slot);
            if (compRevFirstChunkId == 0)
            {
                continue;
            }

            var compTypeId = _archetype._componentTypeIds[slot];
            var info = _accessor.GetComponentInfoInternal(compTypeId, _archetype._slotToComponentType[slot]);

            // Check cache first (prior Open or Write in this transaction)
            if (!info.IsMultiple && info.SingleCache.TryGetValue(pk, out var cached))
            {
                result.SetLocation(slot, cached.CurCompContentChunkId);
                continue;
            }

            var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, _tsn, true);
            if (chainResult.IsFailure)
            {
                continue;
            }

            // Cache CompRevInfo for conflict detection and COW
            var compRevInfo = chainResult.Value;
            compRevInfo.Operations = ComponentInfo.OperationType.Read;
            info.AddNew(pk, compRevInfo);
            result.SetLocation(slot, compRevInfo.CurCompContentChunkId);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Cluster iteration API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>True if this archetype uses cluster storage.</summary>
    public bool HasClusterStorage => _hasClusterStorage;

    /// <summary>Number of active clusters (clusters with at least one live entity).</summary>
    public int ClusterCount => _hasClusterStorage ? _clusterState.ActiveClusterCount : 0;

    /// <summary>
    /// Get an enumerator over active clusters for direct SoA iteration.
    /// The enumerator owns its own ChunkAccessor and must be disposed.
    /// </summary>
    public ClusterEnumerator<TArch> GetClusterEnumerator()
    {
        if (!_hasClusterStorage)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.Create(_clusterState, _archetype, _clusterState.ClusterSegment, _clusterState.TransientSegment);
    }

    /// <summary>
    /// Get a scoped enumerator over a range of active clusters for parallel dispatch.
    /// Each worker gets a non-overlapping range [startIndex, endIndex) into <see cref="ArchetypeClusterState.ActiveClusterIds"/>.
    /// Use <see cref="TickContext.StartClusterIndex"/>/<see cref="TickContext.EndClusterIndex"/> for the range.
    /// </summary>
    public ClusterEnumerator<TArch> GetClusterEnumerator(int startIndex, int endIndex)
    {
        if (!_hasClusterStorage)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.CreateScoped(_clusterState, _archetype, _clusterState.ClusterSegment, _clusterState.TransientSegment, startIndex, endIndex);
    }

    /// <summary>
    /// Get a scoped enumerator over an explicit cluster-id source array (issue #231). Typical usage from a tier-filtered QuerySystem:
    /// <code>
    /// foreach (var cluster in ctx.Accessor.GetClusterEnumerator&lt;Ant&gt;(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)) { ... }
    /// </code>
    /// When <paramref name="clusterIds"/> is the archetype's <c>ActiveClusterIds</c>, this overload is semantically equivalent
    /// to <see cref="GetClusterEnumerator(int, int)"/>. When it is a per-tier cluster list, the enumerator iterates only the tier's clusters.
    /// </summary>
    public ClusterEnumerator<TArch> GetClusterEnumerator(int[] clusterIds, int startIndex, int endIndex)
    {
        if (!_hasClusterStorage)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.CreateScoped(
            _clusterState, _archetype, _clusterState.ClusterSegment, _clusterState.TransientSegment, clusterIds, startIndex, endIndex);
    }

    /// <summary>Release the cached EntityMap and cluster ChunkAccessors.</summary>
    public void Dispose()
    {
        _entityMapAccessor.Dispose();
        if (_hasClusterStorage && _clusterState.ClusterSegment != null)
        {
            _clusterAccessor.Dispose();
        }
        if (_hasTransientCluster)
        {
            _transientClusterAccessor.Dispose();
        }
    }
}
