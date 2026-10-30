// EntityAccessor.ECS — entity resolution and component data access methods.
// These are the methods EntityRef delegates to for Read/Write operations.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

public unsafe partial class EntityAccessor
{
    // ═══════════════════════════════════════════════════════════════════════
    // Public entity access API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a fast-path <see cref="ArchetypeAccessor{TArch}"/> pre-bound to a specific archetype.
    /// Bypasses the epoch checks and archetype lookup of every Open/OpenMut call; MVCC visibility is still checked, and the transaction's pending destroys
    /// are misses. Its own spawns are not seen — they are not in the EntityMap until commit. Intended for PTA workers in parallel QuerySystems.
    /// </summary>
    public ArchetypeAccessor<TArch> For<TArch>() where TArch : class
    {
        RequireLeaseIfPointInTime();
        var meta = ArchetypeRegistry.GetMetadata<TArch>();
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        return new ArchetypeAccessor<TArch>(meta, es, this, _dbe);
    }

    /// <summary>
    /// Get a scoped cluster enumerator for parallel iteration, bypassing <see cref="ArchetypeAccessor{TArch}"/>.
    /// Eliminates EntityMap accessor creation, duplicate cluster ChunkAccessors, and ComponentInfo pre-warming that are unnecessary for pure cluster-iteration
    /// systems (systems that only use GetSpan/GetReadOnlySpan, never Open/OpenMut).
    /// </summary>
    /// <param name="startIndex">Inclusive start into <see cref="ArchetypeClusterState.ActiveClusterIds"/>. Use <see cref="TickContext.StartClusterIndex"/>.</param>
    /// <param name="endIndex">Exclusive end index. Use <see cref="TickContext.EndClusterIndex"/>.</param>
    public ClusterEnumerator<TArch> GetClusterEnumerator<TArch>(int startIndex, int endIndex) where TArch : class
    {
        RequireLeaseIfPointInTime();
        var meta = ArchetypeRegistry.GetMetadata<TArch>();
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (!meta.IsClusterEligible || es?.ClusterState == null)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.CreateScoped(es.ClusterState, meta,
            es.ClusterState.ClusterSegment, es.ClusterState.TransientSegment,
            startIndex, endIndex, this);
    }

    /// <summary>
    /// Get a full cluster enumerator over all active clusters, bypassing <see cref="ArchetypeAccessor{TArch}"/>.
    /// See <see cref="GetClusterEnumerator{TArch}(int,int)"/> for details.
    /// </summary>
    public ClusterEnumerator<TArch> GetClusterEnumerator<TArch>() where TArch : class
    {
        RequireLeaseIfPointInTime();
        var meta = ArchetypeRegistry.GetMetadata<TArch>();
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (!meta.IsClusterEligible || es?.ClusterState == null)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.Create(es.ClusterState, meta,
            es.ClusterState.ClusterSegment, es.ClusterState.TransientSegment, this);
    }

    /// <summary>
    /// Get a scoped cluster enumerator over an explicit cluster-id source array (issue #231). Used by tier-filtered
    /// QuerySystems that read <see cref="TickContext.ClusterIds"/> at dispatch time:
    /// <code>
    /// foreach (var cluster in ctx.Accessor.GetClusterEnumerator&lt;Ant&gt;(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)) { ... }
    /// </code>
    /// When <paramref name="clusterIds"/> is the archetype's <c>ActiveClusterIds</c>, this is semantically equivalent to
    /// <see cref="GetClusterEnumerator{TArch}(int,int)"/>. When it is a per-tier cluster list, the enumerator iterates only
    /// the tier's clusters.
    /// </summary>
    public ClusterEnumerator<TArch> GetClusterEnumerator<TArch>(int[] clusterIds, int startIndex, int endIndex) where TArch : class
    {
        RequireLeaseIfPointInTime();
        var meta = ArchetypeRegistry.GetMetadata<TArch>();
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (!meta.IsClusterEligible || es?.ClusterState == null)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.CreateScoped(es.ClusterState, meta, es.ClusterState.ClusterSegment, es.ClusterState.TransientSegment, clusterIds, 
            startIndex, endIndex, this);
    }

    /// <summary>
    /// An enumerator over the clusters of <typeparamref name="TArch"/> in realm <paramref name="realm"/> only (Realms), bypassing
    /// <see cref="ArchetypeAccessor{TArch}"/>: O(clusters in that realm). An archetype without a <c>[RealmKey]</c> lives in realm 0.
    /// </summary>
    /// <exception cref="InvalidOperationException">The archetype does not use cluster storage, or <paramref name="realm"/> is not registered.</exception>
    public ClusterEnumerator<TArch> GetClusterEnumerator<TArch>(RealmId realm) where TArch : class
    {
        RequireLeaseIfPointInTime();
        var meta = ArchetypeRegistry.GetMetadata<TArch>();
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (!meta.IsClusterEligible || es?.ClusterState == null)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }

        _dbe.CheckRealmRegistered(realm);
        var ids = es.ClusterState.ReadRealmClusterList(realm.Value, out var count);
        return ClusterEnumerator<TArch>.CreateScoped(meta, es.ClusterState, ids, count, this);
    }

    /// <summary>Get cached ComponentInfo by type ID. For ArchetypeAccessor's Versioned chain walk.</summary>
    internal ComponentInfo GetComponentInfoInternal(int componentTypeId, Type componentType) =>
        GetComponentInfoByTypeId(componentTypeId, componentType);

    /// <summary>
    /// Copies the value of <paramref name="slot"/>'s Versioned component committed at this snapshot into <paramref name="destination"/> (VIS-12): the newest
    /// revision stamped at or below it, never this transaction's own unpublished write — a field query does not see those (QENABLE-01). False when the
    /// snapshot holds no value for it.
    /// </summary>
    internal unsafe bool TryCopyCommittedAtSnapshot(ArchetypeMetadata meta, int slot, int chainRoot, byte* destination, int size)
    {
        var chunk = ResolveVersionedContentChunk(meta, slot, chainRoot);
        if (chunk == 0)
        {
            return false;
        }

        var info = GetComponentInfoByTypeId(meta._componentTypeIds[slot], meta._slotToComponentType[slot]);
        System.Runtime.CompilerServices.Unsafe.CopyBlock(destination, info.CompContentAccessor.GetChunkAddress(chunk) + info.ComponentTable.ComponentOverhead,
            (uint)size);
        return true;
    }

    /// <summary>
    /// Walk <paramref name="slot"/>'s revision chain from <paramref name="chainRoot"/> and return the content chunk visible at this accessor's
    /// <see cref="TSN"/>, or 0 when the chain yields nothing visible.
    /// <para>
    /// Called lazily by <see cref="EntityRef"/> on the first read of a Versioned slot. <c>ResolveEntity</c> only stashes the root, so opening an entity
    /// costs nothing for Versioned components the caller never touches.
    /// </para>
    /// </summary>
    internal int ResolveVersionedContentChunk(ArchetypeMetadata meta, int slot, int chainRoot)
    {
        if (chainRoot == 0)
        {
            return 0;
        }

        var info = GetComponentInfoByTypeId(meta._componentTypeIds[slot], meta._slotToComponentType[slot]);
        var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, chainRoot, TSN, CommitCards, true);
        if (chainResult.IsFailure)
        {
            ThrowIfSnapshotExpired();
            return 0;
        }

        return chainResult.Value.CurCompContentChunkId;
    }

    /// <summary>
    /// Turns a revision-chain walk that found nothing into a <see cref="SnapshotExpiredException"/> when the reason is that this snapshot was trimmed (#672).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only from the FAILURE branch of a chain walk, so a successful read pays nothing at all — not even the watermark load. That is cheaper than
    /// checking the watermark up front on every read, and strictly more precise: a walk that fails for a legitimate reason (the entity's component genuinely
    /// has no revision visible at this TSN) is left alone unless the snapshot is also demonstrably below the retention floor.
    /// </para>
    /// <para>
    /// Scoped to <see cref="PointInTimeAccessor"/> worker accessors via <c>_isPointInTime</c>. A <c>Transaction</c> registers in the chain, so
    /// <c>ComputeNextMinTSN</c> can see it and its snapshot is genuinely retained; a walk failure there means something else and must not be reported as an
    /// expiry.
    /// </para>
    /// <para>
    /// Measured before it was written: across the full 5044-test suite there are exactly TWO chain-walk failures, both of them PTA reads below the
    /// watermark. So this branch is not a hot path being taxed — it is a path that essentially only fires when the defect fires.
    /// </para>
    /// </remarks>
    private void ThrowIfSnapshotExpired()
    {
        if (!_isPointInTime || _dbe == null)
        {
            return;
        }

        var retained = _dbe.TransactionChain.RetainedMinTSN;
        if (TSN < retained)
        {
            throw new SnapshotExpiredException(TSN, retained);
        }
    }

    /// <summary>Open an entity for reading. Throws if not found or not visible.</summary>
    public EntityRef Open(EntityId id) => ResolveEntity(id, false, true);

    /// <summary>Open an entity for reading and writing. Throws if not found or not visible.</summary>
    /// <remarks>Runs the accessor's mutation prep first (<see cref="PrepareOpenMut"/>), then resolves the entity's cluster page dirty.</remarks>
    public EntityRefMut OpenMut(EntityId id)
    {
        PrepareOpenMut();
        return Unsafe.BitCast<EntityRef, EntityRefMut>(ResolveEntity(id, true, true));   // not `new EntityRefMut(…)`: see EntityRefMut._ref
    }

    /// <summary>
    /// Moves entity <paramref name="id"/> into realm <paramref name="realm"/>, at <paramref name="value"/>'s position — a realm change (Realms C4). The
    /// realm is validated here, before anything is written: an unregistered realm, or one that cannot hold the archetype, throws in the caller's code.
    /// </summary>
    /// <remarks>
    /// <para>Writes the whole spatial component — <paramref name="value"/> with its <c>[RealmKey]</c> set to <paramref name="realm"/>, whatever it held —
    /// and the next tick fence moves the entity into the new realm's cell. Until then it answers neither realm's spatial queries: the narrowphase realm
    /// filter hides it from the old one, and it is not yet in the new one.</para>
    /// <para>A write through the cluster barrier (<c>ClusterRef.WriteSpatial</c>) with a new key is the same operation; this is its entity-level form.</para>
    /// </remarks>
    /// <typeparam name="T">The archetype's spatial component, the one carrying <c>[RealmKey]</c>.</typeparam>
    /// <exception cref="InvalidOperationException">The archetype is not realm-keyed through <paramref name="spatial"/>, or the realm is invalid for
    /// it.</exception>
    public void Teleport<T>(EntityId id, Comp<T> spatial, RealmId realm, in T value) where T : unmanaged
    {
        var mut = OpenMut(id);
        var state = mut._ref._engineState?.ClusterState;
        if (state == null || !state.SpatialSlot.HasRealmKey || state.SpatialSlot.Slot != mut._ref._archetype.GetSlot(spatial._componentTypeId))
        {
            throw new InvalidOperationException(
                $"Teleport needs a realm-keyed archetype's spatial component: {typeof(T).Name} is not the [SpatialIndex] component of an archetype with a "
                + "[RealmKey].");
        }

        state.ValidateRealmEntry(realm.Value);
        var copy = value;   // on the stack: the centre is read through a pointer, which may never address managed memory
        SpatialGrid.ReadSpatialCenter3D((byte*)&copy + state.SpatialSlot.FieldOffset, state.SpatialSlot.FieldInfo.FieldType, out var x, out var y,
            out var z);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
        {
            throw new InvalidOperationException($"Teleport to a non-finite position ({x}, {y}, {z}) cannot be placed in any grid.");
        }

        ref var stored = ref mut.WriteRef(spatial);
        mut.KeepCollectionHandles(mut._ref._archetype.GetSlot(spatial._componentTypeId), (byte*)&copy, ref stored, false);   // as a Set (#1362)
        stored = copy;
        if (state.SpatialSlot.RealmKeyInSpatialComponent)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref Unsafe.As<T, byte>(ref stored), state.SpatialSlot.RealmKeyOffset), realm.Value);
        }
        else
        {
            // The key's own component, written through the same mutation path as any component (dirty bit, commit staging, shadows).
            state.RealmKeyWriter(ref mut, realm.Value);
        }

        // An entity spawned in this transaction has no cluster yet: its placement reads the staged key at commit, and there is nothing to flag.
        if (mut._ref._isOwnSpawn)
        {
            return;
        }

        // The dirty scan finds this write; a barrier-only archetype's fence runs none, so the slot is flagged as the barrier would flag it. On a
        // Commit-discipline write the value lands at commit, and a fence in between consumes this flag against the old key — so the commit's publish
        // flags the slot again (Transaction.PublishStagedEntry, CC-02).
        state.FlagOutOfBarrierSpatialWrite(mut._ref._clusterChunkId, mut._ref._clusterSlotIndex);
    }

    /// <summary>Try to open an entity for reading. Returns false if the entity doesn't exist or isn't visible.</summary>
    public bool TryOpen(EntityId id, out EntityRef entity)
    {
        entity = ResolveEntity(id, false, false);
        return entity.IsValid;
    }

    /// <summary>
    /// Try to open an entity for reading and writing. Returns false if the entity doesn't exist or isn't visible. One resolve — the writable counterpart of
    /// <see cref="TryOpen"/>, for a target that may be stale (a stored id, a link) and is written when present.
    /// </summary>
    /// <remarks>Same preconditions as <see cref="OpenMut"/>: the mutation prep runs whether or not the entity is found, so a read-only transaction throws
    /// here even for a missing id.</remarks>
    public bool TryOpenMut(EntityId id, out EntityRefMut entity)
    {
        PrepareOpenMut();
        entity = Unsafe.BitCast<EntityRef, EntityRefMut>(ResolveEntity(id, true, false));
        return entity.IsValid;
    }

    /// <summary>
    /// Check whether an entity is alive — exists and is visible at this accessor's TSN. The existence probe: one EntityMap lookup, without the component
    /// location, cluster page and enabled-bits resolution an open performs.
    /// </summary>
    public virtual bool IsAlive(EntityId id)
    {
        AssertThreadAffinity();
        return IsCommittedAlive(id, NeedsEpochGuard());
    }

    /// <summary>
    /// Whether a destroy has removed <paramref name="id"/> for this accessor's snapshot: gone from its EntityMap, or its died stamp — published, or pending
    /// with its owner's card — at or below <see cref="TSN"/>. A cluster walk asks it for the occupied slots of a cluster with a destroy in flight
    /// (CLUSTERWALK-04, #1496): the occupancy bit outlives the commit point until the publish releases the slot, and point access already answers "gone".
    /// </summary>
    /// <param name="id">The id a cluster slot holds; never null (the walk hides a null slot itself).</param>
    /// <param name="meta">The walked archetype.</param>
    internal bool IsDestroyedAtSnapshot(EntityId id, ArchetypeMetadata meta)
    {
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (es?.EntityMap == null)
        {
            return false;
        }

        byte* readBuf = stackalloc byte[meta._entityRecordSize];
        // As IsAlive: a walk normally runs inside the accessor's epoch scope already, and a default guard must not be disposed.
        var needsGuard = NeedsEpochGuard();
        var guard = needsGuard ? EpochGuard.Enter(_epochManager) : default;
        try
        {
            ref var entityMap = ref EntityMapAccessor(es, id.ArchetypeId);

            if (!es.EntityMap.TryGetWithHint(id.EntityKey, readBuf, ref entityMap))
            {
                return true;
            }

            var dead = EntityRecordAccessor.GetHeader(readBuf).IsDeadAt(TSN, CommitCards, out var reread);
            if (!reread)
            {
                return dead;
            }

            // Published since the copy, or its commit aborted: the live record decides, and a value still pending then is not committed.
            return !es.EntityMap.TryGet(id.EntityKey, readBuf, ref entityMap)
                   || EntityRecordAccessor.GetHeader(readBuf).IsDeadAt(TSN, CommitCards, out _);
        }
        finally
        {
            if (needsGuard)
            {
                guard.Dispose();
            }
        }
    }

    /// <summary>
    /// The committed half of <see cref="IsAlive"/>: <paramref name="id"/> is in its archetype's EntityMap and visible at this accessor's TSN. One cached,
    /// hinted lookup — the same accessor cache <c>ResolveEntity</c> uses. <paramref name="needsGuard"/> enters an epoch scope around it; a caller that
    /// already holds one passes false.
    /// </summary>
    private protected bool IsCommittedAlive(EntityId id, bool needsGuard) => IsCommittedAliveAt(id, TSN, needsGuard);

    /// <summary>
    /// Above every commit stamp and every TSN (both stay below <see cref="EntityRecordHeader.PendingBit"/>): a record read at it is the HEAD's — a committed
    /// born or died counts whatever its stamp, a pending one only once its owner is past its commit point.
    /// </summary>
    private protected const long HeadTsn = EntityRecordHeader.PendingBit - 1;

    /// <summary><see cref="IsCommittedAlive"/> at <paramref name="tsn"/>; <see cref="HeadTsn"/> asks whether the entity is alive at HEAD.</summary>
    private protected bool IsCommittedAliveAt(EntityId id, long tsn, bool needsGuard)
    {
        if (id.IsNull)
        {
            return false;
        }

        var meta = _dbe.GetMetaByRouting(id.ArchetypeId);
        if (meta == null)
        {
            return false;
        }
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (es?.EntityMap == null)
        {
            return false;
        }

        byte* readBuf = stackalloc byte[meta._entityRecordSize];
        var guard = needsGuard ? EpochGuard.Enter(_epochManager) : default;
        // Same cached accessor and hinted lookup as ResolveEntity.
        ref var entityMap = ref EntityMapAccessor(es, id.ArchetypeId);
        bool found = es.EntityMap.TryGetWithHint(id.EntityKey, readBuf, ref entityMap);
        if (needsGuard)
        {
            guard.Dispose();
        }

        return found && EntityVisibility.IsVisible(readBuf, tsn, CommitCards, es.EntityMap, id.EntityKey, ref entityMap, _epochManager);
    }

    /// <summary>
    /// The cluster slot <paramref name="id"/>'s committed EntityMap record names, with no visibility test — the caller confirms the slot by the id it holds.
    /// The same cached, hinted lookup as <see cref="IsCommittedAlive"/>; the caller holds an epoch scope. False for an id not in the map (an own spawn).
    /// </summary>
    private protected bool TryLocateCommitted(EntityId id, ArchetypeMetadata meta, ArchetypeEngineState es, out int clusterChunkId, out byte slotIndex)
    {
        byte* readBuf = stackalloc byte[meta._entityRecordSize];
        ref var entityMap = ref EntityMapAccessor(es, id.ArchetypeId);

        if (!es.EntityMap.TryGetWithHint(id.EntityKey, readBuf, ref entityMap))
        {
            clusterChunkId = 0;
            slotIndex = 0;
            return false;
        }

        clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(readBuf);
        slotIndex = ClusterEntityRecordAccessor.GetSlotIndex(readBuf);
        return true;
    }

    /// <summary>Check whether an entity link target is alive.</summary>
    public bool IsAlive<T>(EntityLink<T> link) where T : class => IsAlive(link.Id);

    /// <summary>
    /// Mutation prep run before EVERY writable resolve — <see cref="OpenMut"/>, <see cref="TryOpenMut"/> and <see cref="ArchetypeAccessor{TArch}"/>'s
    /// writable opens (ACCESS-01). No-op here; <see cref="Transaction"/> overrides it with <c>EnsureMutable</c> + the <c>InProgress</c> transition.
    /// </summary>
    internal virtual void PrepareOpenMut() { }

    /// <summary>
    /// A resolver's miss: throws for <c>Open</c> / <c>OpenMut</c>, returns <c>default</c> for the try forms. The throw lives inside the resolver so
    /// <c>Open</c> / <c>OpenMut</c> stay one forwarding call; testing <see cref="EntityRef.IsValid"/> in them needs a local the JIT copies the
    /// ~200-byte handle out of (#997).
    /// </summary>
    private protected EntityRef Miss(EntityId id, bool throwOnMiss)
    {
        if (throwOnMiss)
        {
            ThrowEntityNotFound(id);
        }
        return default;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private protected void ThrowEntityNotFound(EntityId id) => throw new InvalidOperationException($"Entity {id} not found or not visible at TSN {TSN}");

    // ═══════════════════════════════════════════════════════════════════════
    // Archetype cache — the EntityMap and cluster accessors of every resolve through this accessor: both resolvers, a handle's re-resolve (#1199) and
    // every ArchetypeAccessor built on it, which owns none of its own
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>The accessors of one archetype in <see cref="_archetypeCache"/>: its EntityMap's, and its clusters' when it has cluster storage.</summary>
    [NoCopy(Reason = "holds a ChunkAccessor by value ('EntityMap'): a copy duplicates its slots and pins")]
    internal struct ArchetypeCacheEntry
    {
        // The hit test's fields first: on the line the entry starts on, not ~1.3 KB further behind three accessors.
        public ushort ArchetypeId;
        public bool Live;
        public bool HasPersistent;
        public bool HasTransient;
        public long LastUse;
        public ChunkAccessor<PersistentStore> EntityMap;
        public ChunkAccessor<PersistentStore> Persistent;
        public ChunkAccessor<TransientStore> Transient;
    }

    internal const int ArchetypeCacheWays = 4;

    [InlineArray(ArchetypeCacheWays)]
    private struct ArchetypeCache
    {
        private ArchetypeCacheEntry _entry;
    }

    /// <summary>
    /// The cache entry of <paramref name="archetypeId"/> (a routing id), created on a miss in place of the least recently used one. Only the persistent
    /// cluster accessor reports what it lets go of to <see cref="ClusterGeneration"/>: Transient pages live in pinned native blocks and are never evicted,
    /// so a pointer into one outlives its accessor's slot; and no handle keeps a pointer into an EntityMap page.
    /// </summary>
    /// <remarks>
    /// A returned reference is valid until the next miss, which may hand its way to another archetype: take it again rather than keep it across a resolve
    /// of another archetype.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref ArchetypeCacheEntry EnsureArchetypeCache(ArchetypeEngineState es, ushort archetypeId)
    {
        ref var mru = ref _archetypeCache[_archetypeCacheMru];
        if (mru.Live && mru.ArchetypeId == archetypeId)
        {
            return ref mru;
        }
        return ref EnsureArchetypeCacheMiss(es, archetypeId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ref ArchetypeCacheEntry EnsureArchetypeCacheMiss(ArchetypeEngineState es, ushort archetypeId)
    {
        int victim = 0;
        long oldest = long.MaxValue;
        for (int i = 0; i < ArchetypeCacheWays; i++)
        {
            ref var entry = ref _archetypeCache[i];
            if (!entry.Live)
            {
                // A free way beats any live one.
                if (oldest != long.MinValue)
                {
                    victim = i;
                    oldest = long.MinValue;
                }
                continue;
            }
            if (entry.ArchetypeId == archetypeId)
            {
                entry.LastUse = ++_archetypeCacheClock;
                _archetypeCacheMru = (byte)i;
                return ref entry;
            }
            if (entry.LastUse < oldest)
            {
                victim = i;
                oldest = entry.LastUse;
            }
        }

        ref var e = ref _archetypeCache[victim];
        if (e.Live)
        {
            DisposeArchetypeCacheEntry(ref e);
        }
        e.EntityMap = es.EntityMap.Segment.CreateChunkAccessor();
        var persistentSegment = es.ClusterState?.ClusterSegment;
        e.HasPersistent = persistentSegment != null;
        if (persistentSegment != null)
        {
            e.Persistent = persistentSegment.CreateChunkAccessor();
            e.Persistent.TrackGeneration(ClusterGeneration);
        }
        var transientSegment = es.ClusterState?.TransientSegment;
        e.HasTransient = transientSegment != null;
        if (transientSegment != null)
        {
            e.Transient = transientSegment.CreateChunkAccessor();
        }
        e.ArchetypeId = archetypeId;
        e.LastUse = ++_archetypeCacheClock;
        e.Live = true;
        _archetypeCacheMru = (byte)victim;
        return ref e;
    }

    /// <summary>
    /// The EntityMap accessor of <paramref name="archetypeId"/> (a routing id) in the archetype cache — valid until the next miss (see
    /// <see cref="EnsureArchetypeCache"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref ChunkAccessor<PersistentStore> EntityMapAccessor(ArchetypeEngineState es, ushort archetypeId) =>
        ref EnsureArchetypeCache(es, archetypeId).EntityMap;

    private static void DisposeArchetypeCacheEntry(ref ArchetypeCacheEntry entry)
    {
        entry.EntityMap.Dispose();
        if (entry.HasPersistent)
        {
            entry.Persistent.Dispose();
        }
        if (entry.HasTransient)
        {
            entry.Transient.Dispose();
        }
        entry.Live = false;
        entry.HasPersistent = false;
        entry.HasTransient = false;
    }

    private protected void DisposeArchetypeCache()
    {
        for (int i = 0; i < ArchetypeCacheWays; i++)
        {
            ref var entry = ref _archetypeCache[i];
            if (entry.Live)
            {
                DisposeArchetypeCacheEntry(ref entry);
            }
        }
        _archetypeCacheClock = 0;
        _archetypeCacheMru = 0;
    }

    /// <summary>
    /// Resolve <paramref name="result"/>'s cluster bases: the primary one (PersistentStore for mixed/SV, TransientStore for pure-Transient) and, for a
    /// mixed archetype, the Transient one. Stamps the handle with the generation its pointers are valid under.
    /// </summary>
    internal void ResolveClusterBases(ArchetypeEngineState es, ushort archetypeId, int clusterChunkId, bool writable, ref EntityRef result) =>
        ResolveClusterBases(ref EnsureArchetypeCache(es, archetypeId), clusterChunkId, writable, ref result);

    /// <summary>The same, from the cache entry the caller already holds — the one its EntityMap lookup took.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ResolveClusterBases(ref ArchetypeCacheEntry cache, int clusterChunkId, bool writable, ref EntityRef result)
    {
        result._clusterBase = cache.HasPersistent
            ? cache.Persistent.GetChunkAddress(clusterChunkId, writable)
            : cache.Transient.GetChunkAddress(clusterChunkId, writable);
        if (cache.HasTransient && cache.HasPersistent)
        {
            result._transientClusterBase = cache.Transient.GetChunkAddress(clusterChunkId, writable);
        }

        // Read after both resolves: a slot evicted to make room for them bumped the generation, and these pointers are valid under the new value.
        result._resolveGen = ClusterGeneration.Value;
        result._resolvedWritable = writable;
    }

    /// <summary>
    /// The primary cluster base of a handle whose cached one may be gone: its accessor let go of a page since it resolved (#1199). Resolved again
    /// through this accessor's cluster cache, from the cluster id the handle kept, and valid until <see cref="ClusterGeneration"/> moves.
    /// </summary>
    internal byte* ReresolveClusterBase(ArchetypeEngineState es, ushort archetypeId, int clusterChunkId, bool writable)
    {
        ref var cache = ref EnsureArchetypeCache(es, archetypeId);
        return cache.HasPersistent
            ? cache.Persistent.GetChunkAddress(clusterChunkId, writable)
            : cache.Transient.GetChunkAddress(clusterChunkId, writable);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Entity resolution — simplified (no spawn/destroy/CompRevInfo caching)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve an entity from the EntityMap with MVCC visibility at this accessor's TSN.
    /// Base implementation: committed entities only (no spawn/destroy checks, no CompRevInfo caching).
    /// Transaction overrides with full spawn/destroy/caching logic.
    /// </summary>
    private protected virtual EntityRef ResolveEntity(EntityId id, bool writable, bool throwOnMiss)
    {
        AssertThreadAffinity();

        if (id.IsNull)
        {
            return Miss(id, throwOnMiss);
        }

        var meta = _dbe.GetMetaByRouting(id.ArchetypeId);
        if (meta == null)
        {
            return Miss(id, throwOnMiss);
        }

        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (es?.EntityMap == null)
        {
            return Miss(id, throwOnMiss);
        }

        // Read from EntityMap — cache the ChunkAccessor for same-archetype repeated lookups
        int recordSize = meta._entityRecordSize;
        byte* readBuf = stackalloc byte[recordSize];

        // Skip EpochGuard if we are already in an epoch scope (a PTA worker under its lease, or a transaction).
        // This eliminates per-entity PinCurrentThread/UnpinCurrentThread overhead.
        // No NoteEntityOpen here: only PTA workers resolve through this base, and their pin lasts one lease (see NoteEntityOpen).
        var needsGuard = NeedsEpochGuard();
        var guard = needsGuard ? EpochGuard.Enter(_epochManager) : default;

        // Reuse cached EntityMap accessor for same archetype (avoids creating a fresh ChunkAccessor per entity).
        // Transaction uses this pattern in ResolveEntityMapSlotChunkId — extending it to the base class.
        ref var entityMap = ref EntityMapAccessor(es, id.ArchetypeId);

        // Hinted lookup: EntityKey is dense and monotonic, so the low-bit hint slot resolves in one bucket-chunk visit — skipping the hash, the bucket
        // directory read and the chain scan that dominate the plain TryGet. Identical semantics (a stale or colliding hint falls back to the full lookup,
        // which refreshes it), and the hint array self-allocates on first miss. Transaction.ResolveEntity already took this path; the parallel/PTA path
        // did not, despite being the one that performs millions of resolves per tick.
        bool found = es.EntityMap.TryGetWithHint(id.EntityKey, readBuf, ref entityMap);

        if (needsGuard)
        {
            guard.Dispose();
        }

        if (!found)
        {
            return Miss(id, throwOnMiss);
        }

        // MVCC visibility check — a pending born through its owner's card (VIS-08)
        if (!EntityVisibility.IsVisible(readBuf, TSN, CommitCards, es.EntityMap, id.EntityKey, ref entityMap, _epochManager))
        {
            return Miss(id, throwOnMiss);
        }

        ref var header = ref EntityRecordAccessor.GetHeader(readBuf);

        // Resolve EnabledBits with MVCC overrides
        ushort enabledBits = _dbe.EnabledBitsOverrides.ResolveEnabledBits(id.EntityKey, header.EnabledBits, TSN, CommitCards);

        var result = new EntityRef(id, meta, es, this, enabledBits);

        if (meta.IsClusterEligible && es.ClusterState != null)
        {
            // Cluster path: read ClusterEntityRecord → resolve cluster base + slot
            int clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(readBuf);
            byte slotIndex = ClusterEntityRecordAccessor.GetSlotIndex(readBuf);

            ResolveClusterBases(es, id.ArchetypeId, clusterChunkId, writable, ref result);
            result._clusterSlotIndex = slotIndex;
            result._clusterChunkId = clusterChunkId;
            result._clusterLayout = es.ClusterState.Layout;

            // Versioned slots: stash the revision-chain ROOT and mark the slot pending — do NOT walk the chain here. The walk that turns a root into the
            // MVCC-visible content chunk is deferred to the first read of that slot (EntityRef.EnsureVersionedResolved), so a caller that touches only
            // SV/Transient components never pays for it. Measured on the SWG sample: MoveSystem writes Transform alone yet walked Wallet's chain
            // 4,000,200 times over 200 ticks. Deferring is MVCC-neutral — TSN is fixed for this accessor, so walking later yields the same revision.
            if (meta.VersionedSlotMask != 0)
            {
                var layout = es.ClusterState.Layout;
                if (layout.SlotToVersionedIndex != null)
                {
                    for (int slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        int vi = layout.SlotToVersionedIndex[slot];
                        if (vi < 0)
                        {
                            continue;
                        }

                        int compRevFirstChunkId = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(readBuf, vi);
                        if (compRevFirstChunkId == 0)
                        {
                            continue;
                        }

                        result.SetChainRoot(slot, compRevFirstChunkId);
                        result.MarkVersionedPending(slot);
                    }
                }
            }
        }
        else
        {
            // Legacy path: per-component locations + Versioned chain walk
            result.CopyLocationsFrom(readBuf, meta.ComponentCount);

            // For Versioned components: walk revision chain to find visible version.
            // skipTimeout: base EntityAccessor is used by PTA — no concurrent writers, chain lock is uncontended.
            // This avoids Stopwatch.GetTimestamp() overhead per entity (~25ns).
            for (int slot = 0; slot < meta.ComponentCount; slot++)
            {
                var table = es.SlotToComponentTable[slot];
                if (table.StorageMode != StorageMode.Versioned)
                {
                    continue;
                }

                int compRevFirstChunkId = result.GetLocation(slot);
                if (compRevFirstChunkId == 0)
                {
                    continue;
                }

                // Use componentTypeId directly from archetype metadata — avoids Dictionary<Type, int> lookup in GetComponentInfo
                var compTypeId = meta._componentTypeIds[slot];
                var info = GetComponentInfoByTypeId(compTypeId, meta._slotToComponentType[slot]);

                var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, TSN, CommitCards, true);
                if (chainResult.IsFailure)
                {
                    ThrowIfSnapshotExpired();

                    // #672, second half. `CopyLocationsFrom` above seeded this slot with the chain ROOT, and `continue` used to leave it there — so a walk
                    // that found nothing handed the reader a CompRev chunk id to dereference as a CONTENT chunk id. It reads whatever happens to live at
                    // that id in the content segment, which is a silent wrong VALUE rather than a zeroed one, and is exactly why three PTA tests passed
                    // before the #629 eligibility flip: chunk id 1 in the CompRev segment happened to name chunk id 1 in the content segment, which still
                    // held the pre-update value. Zeroing is not a good answer either, but it is an honest one, and it is what the cluster branch already does.
                    result.SetLocation(slot, 0);
                    continue;
                }

                result.SetLocation(slot, chainResult.Value.CurCompContentChunkId);
            }
        }

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Component data access (delegated from EntityRef) — non-virtual hot path
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Read component data via the existing ComponentInfo accessor cache. Zero-copy — returns a ref into the page.
    /// <paramref name="pk"/> is the entity's raw <see cref="EntityId"/>, the key of the Commit-discipline staging map.
    /// <paramref name="isOwnSpawn"/> marks an entity this transaction spawned and has not published, whose non-Versioned payload lives in the spawn arena
    /// rather than in a content chunk (#839) — so <paramref name="location"/> is an arena handle, not a chunk id.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T ReadEcsComponentData<T>(ComponentTable table, int location, long pk, bool isOwnSpawn) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));
        byte* ptr = ResolveSpawnAwarePayload(table, location, isOwnSpawn, ref info.TransientCompContentAccessor, ref info.CompContentAccessor);
        // Commit-discipline read-your-own-writes: return this tx's staged value if it has staged this (component, entity). The staging map is keyed by
        // entity PK, which the CALLER supplies — reading it back out of the chunk header instead was #713: a spawn-staging chunk has no PK written yet
        // (PublishSpawns stamps it), so every own-spawn lookup keyed on 0.
        if (_discipline == CommitDiscipline.Commit && table.StorageMode == StorageMode.SingleVersion
            && info.CommitStaged != null && info.CommitStaged.TryGetValue(pk, out var slot))
        {
            return ref Unsafe.AsRef<T>(_commitStagingBuffer + slot.Offset);
        }
        return ref Unsafe.AsRef<T>(ptr + info.ComponentOverhead);
    }

    /// <summary>
    /// Non-generic counterpart to <see cref="ReadEcsComponentData{T}"/>: resolves the raw storage pointer for a component instance without a compile-time type
    /// parameter. Returns a pointer to the component's field data (already past <see cref="ComponentInfo.ComponentOverhead"/>); the caller reads
    /// <c>ComponentStorageSize</c> bytes and decodes fields by offset. Backs <see cref="EntityRef.ReadRaw(int, Span{byte})"/> for runtime tooling
    /// (the Workbench Data Browser).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* ReadEcsComponentDataRaw(ComponentTable table, int componentTypeId, Type componentType, int location, bool isOwnSpawn)
    {
        var info = GetComponentInfoByTypeId(componentTypeId, componentType);
        byte* ptr = ResolveSpawnAwarePayload(table, location, isOwnSpawn, ref info.TransientCompContentAccessor, ref info.CompContentAccessor);
        return ptr + info.ComponentOverhead;
    }

    /// <summary>
    /// Resolves a per-slot payload address, which since #839 depends on whether the entity is published.
    /// </summary>
    /// <remarks>
    /// A spawned-but-unpublished entity has no cluster slot and, for a SingleVersion or Transient component, no content chunk either — its bytes sit in the
    /// transaction's <see cref="SpawnStagingArena"/> and <paramref name="location"/> is a handle into it. A Versioned slot keeps a real content chunk even
    /// while unpublished, because that chunk IS the first revision's payload. Everything published resolves through the component accessors as before.
    /// The single choke point exists so the "is this an arena handle or a chunk id?" question is answered in one place rather than at every read site.
    /// <para>The accessors come by reference. A copy loads the page into a slot of its own and takes the slot reference that keeps the page in the cache;
    /// the copy is then dropped, so nothing ever releases it: one page pinned for good per read, which filled the cache of a database larger than it.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* ResolveSpawnAwarePayload(ComponentTable table, int location, bool isOwnSpawn, ref ChunkAccessor<TransientStore> transientAccessor,
        ref ChunkAccessor<PersistentStore> persistentAccessor)
    {
        if (isOwnSpawn && table.StorageMode != StorageMode.Versioned)
        {
            return SpawnArenaOrNull.Resolve(location);
        }

        return table.StorageMode == StorageMode.Transient ? transientAccessor.GetChunkAddress(location) : persistentAccessor.GetChunkAddress(location);
    }

    /// <summary>Write component data via the existing ComponentInfo accessor cache. Returns mutable ref.
    /// For SingleVersion: atomically marks chunkId in DirtyBitmap for tick fence serialization.
    /// <paramref name="pk"/> is the entity's raw <see cref="EntityId"/>; <paramref name="isOwnSpawn"/> marks an entity this transaction spawned and has
    /// not published yet (see the Commit-discipline branch below).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T WriteEcsComponentData<T>(ComponentTable table, int chunkId, long pk, bool isOwnSpawn) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));

        // Commit discipline (SingleVersion, Variant A): stage the write — leave the chunk HEAD untouched and unmarked (CM-01). The HEAD still holds the
        // pre-write value, so seed the staging slot from it for partial-write correctness. CM-02 escalation first (so DefaultDiscipline=Commit applies).
        if (table.StorageMode == StorageMode.SingleVersion)
        {
            if (table.Discipline == CommitDiscipline.Commit)
            {
                ResolveCommitDiscipline(table);
            }

            // #713: an entity this transaction spawned has no HEAD to protect — it lives in a spawn-staging chunk that no other transaction can see until
            // PublishSpawns publishes it, so writing in place IS the atomic behaviour CM-01 asks for, and it is what TickFence already does. Staging it
            // was wrong three ways: StagedSlot.Location would carry a content chunk id where PublishStagedEntry expects a cluster location, the publish
            // would run against a HEAD that does not exist yet, and PlaceSpawns would then overwrite it with the spawn value. Skipping the staging
            // leaves the spawn's own SV Slot record (BuildCommitBatch, #395 D5 / CM-06) carrying the final value — one record, still atomic.
            if (_discipline == CommitDiscipline.Commit && !isOwnSpawn)
            {
                byte* head = info.CompContentAccessor.GetChunkAddress(chunkId);
                // Flat location is the content chunkId (captured for the no-re-lookup publish).
                return ref StageCommitWriteCore<T>(info, pk, chunkId, head + info.ComponentOverhead);
            }
        }

        byte* ptr;
        if (isOwnSpawn)
        {
            // #839: an unpublished spawn's non-Versioned payload is in the transaction arena, so `chunkId` is an arena handle. A Versioned slot never reaches
            // here — EntityRef takes the copy-on-write branch for it first.
            ptr = SpawnArenaOrNull.Resolve(chunkId);
            if (table.StorageMode != StorageMode.Transient)
            {
                _didInPlaceSvWrite = true;   // CM-02: still an in-place TickFence write, wherever the bytes happen to live
            }
        }
        else if (table.StorageMode == StorageMode.Transient)
        {
            ptr = info.TransientCompContentAccessor.GetChunkAddress(chunkId, true);
        }
        else
        {
            ptr = info.CompContentAccessor.GetChunkAddress(chunkId, true);
            _didInPlaceSvWrite = true;   // CM-02: a TickFence in-place SingleVersion write happened — blocks late escalation to Commit
        }

        // DIRTY-01 (rules/ecs.md): a spawn sets no dirty bit. These bitmaps track write mutations to entities that are already PUBLISHED, and PlaceSpawns
        // deliberately marks neither this one nor ClusterDirtyBitmap for a spawn. For an own-spawn `chunkId` names the spawn-staging chunk, which has no
        // published identity to report: the entity PK is stamped into that chunk's overhead only for TRANSIENT slots, so the fence reads PK 0 and dies on
        // GetMetaByRouting(0) — silently, in Release (#837). A Transient own-spawn does carry its PK and so reached the change-filtered dispatch scan instead,
        // which calls table.ComponentSegment.CreateChunkAccessor() — null on a Transient table, which builds only its transient segments. Both are the same
        // mistake: a staging chunk id where a published one is expected. Nothing is lost by withholding the bit; see DIRTY-01's rationale for the
        // per-discipline durability argument.
        if (!isOwnSpawn)
        {
            table.DirtyBitmap?.Set(chunkId);
        }

        return ref Unsafe.AsRef<T>(ptr + info.ComponentOverhead);
    }

    /// <summary>
    /// Capture old indexed field values before the first SV in-place mutation per entity per tick.
    /// Called from <see cref="EntityRefMut.Set{T}(Comp{T}, in T)"/> for SingleVersion components with indexed fields.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ShadowIndexedFields<T>(ComponentTable table, int chunkId, EntityId entityId) where T : unmanaged
    {
        if (table.ShadowBitmap.TestAndSet(chunkId))
        {
            return; // Already shadowed this tick
        }

        var info = GetComponentInfo(typeof(T));
        byte* ptr = table.StorageMode == StorageMode.Transient ? info.TransientCompContentAccessor.GetChunkAddress(chunkId) : info.CompContentAccessor.GetChunkAddress(chunkId);

        var fields = table.IndexedFieldInfos;
        var buffers = table.FieldShadowBuffers;

        for (int i = 0; i < fields.Length; i++)
        {
            ref var ifi = ref fields[i];
            var oldKey = KeyBytes8.FromPointer(ptr + ifi.OffsetToField, ifi.Size);
            buffers[i].Append(chunkId, entityId, oldKey);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Committed discipline — Variant A staging (issue #392)
    // ═══════════════════════════════════════════════════════════════════════

    private const int InitialCommitStagingCapacity = 4096;

    /// <summary>
    /// CM-02 discipline resolution, invoked from the write path for a <see cref="CommitDiscipline.Commit"/>-defaulted
    /// <see cref="StorageMode.SingleVersion"/> component. Escalates this accessor's whole transaction to Commit on first touch (so
    /// every subsequent write is commit-durable), and rejects escalation if a TickFence in-place write has already happened (we cannot
    /// retroactively make an applied write atomic). Idempotent and cheap once escalated. Callers gate on
    /// <c>table.Discipline == Commit</c> so the TickFence hot path never reaches here for a non-Commit component.
    /// </summary>
    internal void ResolveCommitDiscipline(ComponentTable table)
    {
        if (_discipline == CommitDiscipline.Commit)
        {
            return;
        }

        if (_didInPlaceSvWrite)
        {
            throw new InvalidOperationException(
                $"Component '{table.Name}' is declared DefaultDiscipline=Commit, but this transaction has already performed a TickFence " +
                "in-place write. Create the transaction with discipline: CommitDiscipline.Commit before writing any component so the " +
                "whole transaction is commit-durable (CM-02 uniformity).");
        }

        _discipline = CommitDiscipline.Commit;
        _dbe?.LogDisciplineEscalated(TSN, table.Name);
    }

    /// <summary>
    /// Native staging buffer for Commit-discipline SingleVersion writes (Variant A). Lazily allocated on the first staged write and
    /// freed by <see cref="FreeCommitStaging"/> on transaction reset. Native (not a managed <c>byte[]</c>) so a staged write can return a
    /// stable <c>ref T</c> into it. A staged ref is invalidated by the next staging allocation that grows the buffer
    /// (the same contract as a <c>ref</c> into a <c>List&lt;T&gt;</c> via CollectionsMarshal); the common write-then-commit idiom is always safe.
    /// </summary>
    private protected byte* _commitStagingBuffer;
    private protected int _commitStagingCapacity;
    private protected int _commitStagingUsed;

    /// <summary>
    /// Payload store for entities this transaction has spawned but not yet published (#839). Lazily created on the first spawn, reset with the transaction.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the <see cref="_commitStagingBuffer"/> above, despite the similar job: that one reallocates, and its contract says so — a staged ref
    /// dies at the next growing allocation. A spawn's payload cannot accept that, because <c>ref</c>s into it outlive later spawns in the same transaction
    /// (<c>SpawnBatch</c> is precisely spawn-spawn-write). <see cref="SpawnStagingArena"/> appends fixed blocks and never moves one.
    /// </remarks>
    /// <summary>
    /// The owning transaction's spawn arena, or <see langword="null"/> on an accessor that cannot spawn.
    /// </summary>
    /// <remarks>
    /// Storage lives on <see cref="Transaction"/>, not here, even though the two resolve sites above are on this class. Spawning is a transaction-only
    /// operation, but <see cref="EntityAccessor"/> is also the base of the READ path: <see cref="PointInTimeAccessor"/> holds one instance PER PARALLEL WORKER
    /// for lock-free entity access at a frozen TSN, and those can never spawn. A field here would put native staging memory on every worker accessor, kept
    /// harmless only by the laziness of its initialiser — harmless by accident rather than by construction. The resolve sites are shared because reading an
    /// own-spawn payload is shared; the ownership is not.
    /// </remarks>
    private protected virtual SpawnStagingArena SpawnArenaOrNull => null;

    /// <summary>Reserve <paramref name="size"/> bytes in the native staging buffer; returns the 0-based offset.</summary>
    private int StageAlloc(int size)
    {
        var off = _commitStagingUsed;
        var need = off + size;
        if (need > _commitStagingCapacity)
        {
            var newCap = _commitStagingCapacity == 0 ? InitialCommitStagingCapacity : _commitStagingCapacity;
            while (newCap < need)
            {
                newCap *= 2;
            }
            // native-alloc: doubling growth buffer: Realloc grows in place, where a resource-tree block would be disposed and re-parented on every doubling
            _commitStagingBuffer = (byte*)NativeMemory.Realloc(_commitStagingBuffer, (nuint)newCap);
            _commitStagingCapacity = newCap;
        }
        _commitStagingUsed = need;
        return off;
    }

    /// <summary>Free the native staging buffer (idempotent) and reset its bump pointer. Called from the transaction reset path.</summary>
    private protected void FreeCommitStaging()
    {
        if (_commitStagingBuffer != null)
        {
            NativeMemory.Free(_commitStagingBuffer);
            _commitStagingBuffer = null;
        }
        _commitStagingCapacity = 0;
        _commitStagingUsed = 0;
    }

    /// <summary>
    /// Variant-A staging core: on the first Commit-discipline write to (component, entity) this transaction, reserve a staging slot and seed it
    /// with the current HEAD value (so partial writes are correct), then return a mutable ref into the staging buffer. The cluster/chunk HEAD is
    /// NOT touched until commit publish (CM-01).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T StageCommitWriteCore<T>(ComponentInfo info, long pk, int location, byte* headDataPtr) where T : unmanaged
    {
        var size = info.ComponentTable.ComponentStorageSize;
        Debug.Assert(sizeof(T) == size, "Commit-discipline staging assumes the component IS T (SingleVersion layout)");
        info.CommitStaged ??= new Dictionary<long, ComponentInfo.StagedSlot>();
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(info.CommitStaged, pk, out var exists);
        if (!exists)
        {
            slot.Offset = StageAlloc(size);
            slot.Location = location;                           // captured at stage time — publish uses it, no EntityMap re-lookup
            Unsafe.CopyBlockUnaligned(_commitStagingBuffer + slot.Offset, headDataPtr, (uint)size);   // seed from HEAD (partial-write correctness)
            if (info.ComponentTable.HasCollections)
            {
                ShareStagedCollections(info.ComponentTable, _commitStagingBuffer + slot.Offset);
            }
        }
        return ref Unsafe.AsRef<T>(_commitStagingBuffer + slot.Offset);
    }

    /// <summary>
    /// A staged value seeded from HEAD names HEAD's collection buffers: it takes a reference on each, as a Versioned copy-on-write does for a new revision
    /// (#1362). The staged value then owns its handles whatever they are — HEAD's shared one, or a buffer an accessor moved it to — so the publish hands
    /// them to HEAD and gives back HEAD's previous ones, and a rollback gives back the staged value's, without either reading the other's state.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ShareStagedCollections(ComponentTable table, byte* staged)
    {
        foreach (var f in table.CollectionFields)
        {
            var bufferId = Unsafe.ReadUnaligned<int>(staged + f.OffsetInComponentStorage);
            if (bufferId != 0)
            {
                var accessor = f.Vsbs.Segment.CreateChunkAccessor(_changeSet);
                f.Vsbs.BufferAddRef(bufferId, ref accessor);
                accessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Records that a TickFence in-place SingleVersion write has happened (called from the cluster write path, the counterpart of the flag set inline by
    /// <see cref="WriteEcsComponentData{T}"/> for the non-cluster path). Blocks a later CM-02 auto-escalation to Commit, which could no longer make the
    /// already-applied write atomic. One bool store — negligible beside the cluster <c>SetDirty</c> on the same path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void NoteSvInPlaceWrite() => _didInPlaceSvWrite = true;

    /// <summary>
    /// Records the cache page a SingleVersion value is written into in place, through the internal ref <c>EntityRefMut.WriteRef</c> returns (PS-10,
    /// #1172). The resolve mapped the page dirty through a chunk accessor without a ChangeSet, which records nothing: unrecorded, an eviction before the
    /// next fence reloads the old value. By address, with no page lookup, since it runs once per written component. A cluster without a persistent segment
    /// lives in the transient store and needs nothing.
    /// <para>The caller stores through the ref after this returns. That is safe while the resolving accessor still maps the page dirty, which keeps a
    /// checkpoint from copying it; a store after the accessor released the page is #1176.</para>
    /// </summary>
    internal void NoteSvInPlacePageWrite(ArchetypeClusterState clusterState, byte* address)
    {
        if (clusterState.ClusterSegment == null)
        {
            return;
        }

        var mmf = _dbe.MMF;
        mmf.MarkPageModified(mmf.MemPageIndexOf(address));
    }

    /// <summary>
    /// Cluster-path entry point for Commit-discipline staging — resolves the ComponentInfo, then stages (see <see cref="StageCommitWriteCore{T}"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T StageClusterCommitWrite<T>(ComponentTable table, int componentTypeId, long pk, int clusterLocation, byte* clusterHeadPtr) where T : unmanaged
    {
        var info = GetComponentInfoByTypeId(componentTypeId, typeof(T));
        return ref StageCommitWriteCore<T>(info, pk, clusterLocation, clusterHeadPtr);
    }

    /// <summary>
    /// Read-your-own-writes: returns a pointer to this transaction's staged value for (component, entity), or null if not staged. Consulted only
    /// when <see cref="Discipline"/> is Commit (a per-tx constant), and never creates a ComponentInfo.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* TryGetStagedPtr(Type componentType, long pk)
    {
        if (_commitStagingBuffer == null || !_componentInfos.TryGetValue(componentType, out var info) || info.CommitStaged == null)
        {
            return null;
        }
        if (!info.CommitStaged.TryGetValue(pk, out var slot))
        {
            return null;
        }
        return _commitStagingBuffer + slot.Offset;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Virtual methods — overridden by Transaction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Copy-on-write for Versioned components. Not supported in base EntityAccessor — throws.
    /// <paramref name="chainRootChunkId"/> is the revision-chain root captured at resolve time (0 = unknown → PK-index fallback).</summary>
    internal virtual (int chunkId, nint ptr) EcsVersionedCopyOnWrite(Type compType, EntityId entityId, ComponentTable table, int chainRootChunkId = 0)
        => throw new InvalidOperationException(
            "EntityAccessor does not support Versioned component writes. Use a full Transaction for systems that modify Versioned components.");

    /// <summary>Stage an EnabledBits change for commit. Not supported in base EntityAccessor — throws.</summary>
    internal virtual void StageEnableDisable(EntityId id, ushort newEnabledBits)
        => throw new InvalidOperationException(
            "EntityAccessor does not support Enable/Disable operations. Use a full Transaction for structural component changes.");

    /// <summary>
    /// Creates a Versioned slot's content chunk and first revision for a LIVE entity, and writes <paramref name="value"/> into it.
    /// </summary>
    /// <remarks>
    /// Backs <see cref="EntityRefMut.Enable{T}(Comp{T}, in T)"/> for the one case the no-value overload refuses: a component the spawn never supplied, which
    /// has no chain and therefore nothing to enable. Spawn used to be the only producer of a first revision, because design decision #14 guaranteed every slot
    /// existed from spawn; once an unsupplied slot is genuinely absent (#845) that guarantee is gone and a component can begin mid-life.
    /// </remarks>
    internal virtual int CreateVersionedContentAndWrite<T>(EntityId id, byte slot, in T value) where T : unmanaged
        => throw new InvalidOperationException(
            "EntityAccessor does not support supplying a component value. Use a full Transaction for structural component changes.");
}
