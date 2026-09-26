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
    private readonly Transaction _transaction;   // _accessor as a Transaction (null for a PTA worker) — for its pending destroys
    private readonly EnabledBitsOverrides _enabledBitsOverrides;
    private readonly long _tsn;
    private readonly ushort _routingId;
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
        _transaction = accessor as Transaction;
        _enabledBitsOverrides = dbe.EnabledBitsOverrides;
        _tsn = accessor.TSN;
        _routingId = dbe.RoutingIdOf(archetype);
        _recordSize = archetype._entityRecordSize;
        _entityMapAccessor = engineState.EntityMap.Segment.CreateChunkAccessor();

        // Detect if any component uses Versioned storage (needs revision chain walk). No ComponentInfo is created here: every reader and writer reaches
        // one through GetComponentInfoByTypeId, which creates it on first touch, so warming every slot up front only cost an entry per component the
        // caller never used — per transaction, since a pooled one starts each lease empty.
        _hasVersionedSlots = false;
        for (int slot = 0; slot < archetype.ComponentCount; slot++)
        {
            if (engineState.SlotToComponentTable[slot].StorageMode == StorageMode.Versioned)
            {
                _hasVersionedSlots = true;
                break;
            }
        }

        // Cluster storage setup
        _hasClusterStorage = archetype.IsClusterEligible && engineState.ClusterState != null;
        _clusterState = engineState.ClusterState;
        _clusterAccessor = _hasClusterStorage && _clusterState.ClusterSegment != null ? _clusterState.ClusterSegment.CreateChunkAccessor() : default;
        _hasTransientCluster = _hasClusterStorage && _clusterState.TransientSegment != null;
        _transientClusterAccessor = _hasTransientCluster ? _clusterState.TransientSegment.CreateChunkAccessor() : default;
    }

    /// <summary>Open an entity for reading. Throws if it is not an entity of this archetype visible at the accessor's TSN.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityRef Open(EntityId id) => Resolve(id, false, throwOnMiss: true);

    /// <summary>Open an entity for reading and writing. Throws if it is not an entity of this archetype visible at the accessor's TSN.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EntityRefMut OpenMut(EntityId id)
    {
        PrepareMutation();
        return Unsafe.BitCast<EntityRef, EntityRefMut>(Resolve(id, true, throwOnMiss: true));
    }

    /// <summary>Try to open an entity for reading. Returns false if it is not an entity of this archetype visible at the accessor's TSN.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryOpen(EntityId id, out EntityRef entity)
    {
        entity = Resolve(id, false, throwOnMiss: false);
        return entity.IsValid;
    }

    /// <summary>
    /// Try to open an entity for reading and writing, in one resolve. Returns false if it is not an entity of this archetype visible at the accessor's TSN.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryOpenMut(EntityId id, out EntityRefMut entity)
    {
        PrepareMutation();
        entity = Unsafe.BitCast<EntityRef, EntityRefMut>(Resolve(id, true, throwOnMiss: false));
        return entity.IsValid;
    }

    /// <summary>
    /// Check whether <paramref name="id"/> is an entity of this archetype visible at the accessor's TSN — the predicate every open above uses.
    /// </summary>
    public bool IsAlive(EntityId id)
    {
        if (id.ArchetypeId != _routingId || IsPendingDestroy(id))
        {
            return false;
        }
        byte* readBuf = stackalloc byte[_recordSize];
        return _engineState.EntityMap.TryGetWithHint(id.EntityKey, readBuf, ref _entityMapAccessor)
            && EntityRecordAccessor.GetHeader(readBuf).IsVisibleAt(_tsn);
    }

    /// <summary>
    /// True when the owning transaction has destroyed <paramref name="id"/> but not committed yet. Two field loads and a count test when nothing is
    /// pending — the common case — and a hash lookup only when destroys are.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private readonly bool IsPendingDestroy(EntityId id)
    {
        var pending = _transaction?.PendingDestroys;
        return pending != null && pending.Count != 0 && pending.Contains(id);
    }

    /// <summary>
    /// Mutation prep before every writable open (ACCESS-01) — not once per accessor: an accessor taken before its transaction commits and used after must
    /// still be refused. The first open runs it inside the profiling span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PrepareMutation()
    {
        if (_mutationPrepared)
        {
            _accessor.PrepareOpenMut();
            return;
        }
        _accessor.PrepareForMutation();
        _mutationPrepared = true;
    }

    private readonly EntityRef Miss(EntityId id, bool throwOnMiss)
    {
        if (throwOnMiss)
        {
            ThrowEntityNotFound(id, _tsn);
        }
        return default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowEntityNotFound(EntityId id, long tsn) =>
        throw new InvalidOperationException($"Entity {id} is not an entity of archetype {typeof(TArch).Name} visible at TSN {tsn}");

    /// <summary>
    /// Resolves <paramref name="id"/> against this archetype's EntityMap. A miss — the id is null or routes to another archetype, is absent, or is not
    /// visible at the accessor's TSN — throws when <paramref name="throwOnMiss"/> is set and returns <c>default</c> (<see cref="EntityRef.IsValid"/>
    /// false) otherwise. An entity the owning transaction has destroyed but not committed is a miss too. Its own spawns are not seen: they are not in
    /// the EntityMap until commit.
    /// </summary>
    /// <remarks>
    /// Built so that no open copies the ~200-byte handle (#997). The throw lives here, not in <see cref="Open"/> / <see cref="OpenMut"/>, so those stay
    /// one forwarding call that hands their caller's return buffer straight to this method: testing <see cref="EntityRef.IsValid"/> in the caller needs
    /// a local, and the JIT copies out of it (measured +8 ns on a ~50 ns <see cref="Open"/>). The writable opens reinterpret the result with
    /// <c>Unsafe.BitCast</c> instead of wrapping it — see the field comment on <c>EntityRefMut._ref</c>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EntityRef Resolve(EntityId id, bool writable, bool throwOnMiss)
    {
        // Also rejects EntityId.Null: routing id 0 is reserved, so no archetype has it.
        if (id.ArchetypeId != _routingId || IsPendingDestroy(id))
        {
            return Miss(id, throwOnMiss);
        }

        byte* readBuf = stackalloc byte[_recordSize];
        // Hinted lookup — see the note in EntityAccessor.ResolveEntity. Same map, same key shape, same reason.
        if (!_engineState.EntityMap.TryGetWithHint(id.EntityKey, readBuf, ref _entityMapAccessor))
        {
            return Miss(id, throwOnMiss);
        }

        ref var header = ref EntityRecordAccessor.GetHeader(readBuf);
        if (!header.IsVisibleAt(_tsn))
        {
            return Miss(id, throwOnMiss);
        }
        ushort enabledBits = _enabledBitsOverrides.ResolveEnabledBits(id.EntityKey, header.EnabledBits, _tsn);

        var result = new EntityRef(id, _archetype, _engineState, _accessor, enabledBits);

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
            if (info.SingleCache.TryGetValue(pk, out var cached))
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
            if (info.SingleCache.TryGetValue(pk, out var cached))
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

    /// <summary>
    /// An enumerator over the clusters of this archetype in realm <paramref name="realm"/> only (Realms): O(clusters in that realm), not a filter over
    /// every realm's. An archetype without a <c>[RealmKey]</c> lives in realm 0 — all its clusters there, none elsewhere. The enumerator owns its own
    /// ChunkAccessor and must be disposed.
    /// </summary>
    /// <remarks>Like <see cref="GetClusterEnumerator()"/>, the list is live: walk it from a system, or outside the tick fence.</remarks>
    /// <exception cref="InvalidOperationException">The archetype does not use cluster storage, or <paramref name="realm"/> is not registered.</exception>
    public ClusterEnumerator<TArch> GetClusterEnumerator(RealmId realm)
    {
        if (!_hasClusterStorage)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }

        _accessor.DBE.CheckRealmRegistered(realm);
        var ids = _clusterState.ReadRealmClusterList(realm.Value, out var count);
        return ClusterEnumerator<TArch>.CreateScoped(_archetype, _clusterState, ids, count);
    }

    /// <summary>How many clusters of this archetype are in realm <paramref name="realm"/>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="realm"/> is not registered.</exception>
    public int ClusterCountIn(RealmId realm)
    {
        _accessor.DBE.CheckRealmRegistered(realm);
        if (!_hasClusterStorage)
        {
            return 0;
        }

        _clusterState.ReadRealmClusterList(realm.Value, out var count);
        return count;
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
