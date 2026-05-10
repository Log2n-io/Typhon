// EntityAccessor.ECS — entity resolution and component data access methods.
// These are the methods EntityRef delegates to for Read/Write operations.

using System;
using System.Runtime.CompilerServices;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

public unsafe partial class EntityAccessor
{
    // ═══════════════════════════════════════════════════════════════════════
    // Public entity access API
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Create a fast-path <see cref="ArchetypeAccessor{TArch}"/> pre-bound to a specific archetype.
    /// Bypasses epoch checks, archetype lookup, and MVCC visibility on every Open/OpenMut call.
    /// Intended for PTA workers in parallel QuerySystems where these checks are redundant.
    /// </summary>
    public ArchetypeAccessor<TArch> For<TArch>() where TArch : class
    {
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
        var meta = ArchetypeRegistry.GetMetadata<TArch>();
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (!meta.IsClusterEligible || es?.ClusterState == null)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.CreateScoped(es.ClusterState, meta,
            es.ClusterState.ClusterSegment, es.ClusterState.TransientSegment,
            startIndex, endIndex);
    }

    /// <summary>
    /// Get a full cluster enumerator over all active clusters, bypassing <see cref="ArchetypeAccessor{TArch}"/>.
    /// See <see cref="GetClusterEnumerator{TArch}(int,int)"/> for details.
    /// </summary>
    public ClusterEnumerator<TArch> GetClusterEnumerator<TArch>() where TArch : class
    {
        var meta = ArchetypeRegistry.GetMetadata<TArch>();
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (!meta.IsClusterEligible || es?.ClusterState == null)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.Create(es.ClusterState, meta,
            es.ClusterState.ClusterSegment, es.ClusterState.TransientSegment);
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
        var meta = ArchetypeRegistry.GetMetadata<TArch>();
        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (!meta.IsClusterEligible || es?.ClusterState == null)
        {
            throw new InvalidOperationException($"Archetype {typeof(TArch).Name} does not use cluster storage");
        }
        return ClusterEnumerator<TArch>.CreateScoped(es.ClusterState, meta, es.ClusterState.ClusterSegment, es.ClusterState.TransientSegment, clusterIds, 
            startIndex, endIndex);
    }

    /// <summary>Pre-warm the ComponentInfo cache for a given component type. Called by ArchetypeAccessor during construction.</summary>
    internal void EnsureComponentInfoCached(Type componentType) => GetComponentInfo(componentType);

    /// <summary>Get cached ComponentInfo by type ID. For ArchetypeAccessor's Versioned chain walk.</summary>
    internal ComponentInfo GetComponentInfoInternal(int componentTypeId, Type componentType) =>
        GetComponentInfoByTypeId(componentTypeId, componentType);

    /// <summary>Open an entity for reading. Throws if not found or not visible.</summary>
    public EntityRef Open(EntityId id)
    {
        var entity = ResolveEntity(id, false);
        if (!entity.IsValid)
        {
            throw new InvalidOperationException($"Entity {id} not found or not visible at TSN {TSN}");
        }
        return entity;
    }

    /// <summary>Open an entity for reading and writing (SV/Transient only).
    /// Override in Transaction to add EnsureMutable + state transition.</summary>
    public virtual EntityRef OpenMut(EntityId id)
    {
        var entity = ResolveEntity(id, true);
        if (!entity.IsValid)
        {
            throw new InvalidOperationException($"Entity {id} not found or not visible at TSN {TSN}");
        }
        return entity;
    }

    /// <summary>Try to open an entity. Returns false if the entity doesn't exist or isn't visible.</summary>
    public bool TryOpen(EntityId id, out EntityRef entity)
    {
        entity = ResolveEntity(id, false);
        return entity.IsValid;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Entity resolution — simplified (no spawn/destroy/CompRevInfo caching)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve an entity from the EntityMap with MVCC visibility at this accessor's TSN.
    /// Base implementation: committed entities only (no spawn/destroy checks, no CompRevInfo caching).
    /// Transaction overrides with full spawn/destroy/caching logic.
    /// </summary>
    private protected virtual EntityRef ResolveEntity(EntityId id, bool writable)
    {
        AssertThreadAffinity();

        if (id.IsNull)
        {
            return default;
        }

        var meta = ArchetypeRegistry.GetMetadata(id.ArchetypeId);
        if (meta == null)
        {
            return default;
        }

        var es = _dbe._archetypeStates[meta.ArchetypeId];
        if (es?.EntityMap == null)
        {
            return default;
        }

        // Read from EntityMap — cache the ChunkAccessor for same-archetype repeated lookups
        int recordSize = meta._entityRecordSize;
        byte* readBuf = stackalloc byte[recordSize];

        // Skip EpochGuard if we're already in an epoch scope (PTA workers enter once in InitLightweight).
        // This eliminates per-entity PinCurrentThread/UnpinCurrentThread overhead.
        var needsGuard = !_epochManager.IsCurrentThreadInScope;
        var guard = needsGuard ? EpochGuard.Enter(_epochManager) : default;

        // Reuse cached EntityMap accessor for same archetype (avoids creating a fresh ChunkAccessor per entity).
        // Transaction uses this pattern in ResolveEntityMapSlotChunkId — extending it to the base class.
        if (!_hasEntityMapCache || _entityMapCacheArchId != id.ArchetypeId)
        {
            if (_hasEntityMapCache)
            {
                _entityMapCacheAccessor.Dispose();
            }

            _entityMapCacheAccessor = es.EntityMap.Segment.CreateChunkAccessor();
            _entityMapCacheArchId = id.ArchetypeId;
            _hasEntityMapCache = true;
        }

        bool found = es.EntityMap.TryGet(id.EntityKey, readBuf, ref _entityMapCacheAccessor);

        if (needsGuard)
        {
            guard.Dispose();
        }

        if (!found)
        {
            return default;
        }

        ref var header = ref EntityRecordAccessor.GetHeader(readBuf);

        // MVCC visibility check
        if (!header.IsVisibleAt(TSN))
        {
            return default;
        }

        // Resolve EnabledBits with MVCC overrides
        ushort enabledBits = _dbe.EnabledBitsOverrides.ResolveEnabledBits(id.EntityKey, header.EnabledBits, TSN);

        var result = new EntityRef(id, meta, es, this, enabledBits, writable);

        if (meta.IsClusterEligible && es.ClusterState != null)
        {
            // Cluster path: read ClusterEntityRecord → resolve cluster base + slot
            int clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(readBuf);
            byte slotIndex = ClusterEntityRecordAccessor.GetSlotIndex(readBuf);

            // Cache cluster accessor for same-archetype repeated lookups
            if (!_hasClusterCache || _clusterCacheArchId != id.ArchetypeId)
            {
                if (_hasClusterCache)
                {
                    _clusterCacheAccessor.Dispose();
                }
                if (_hasTransientClusterCache)
                {
                    _transientClusterCacheAccessor.Dispose();
                    _hasTransientClusterCache = false;
                }

                if (es.ClusterState.ClusterSegment != null)
                {
                    _clusterCacheAccessor = es.ClusterState.ClusterSegment.CreateChunkAccessor();
                }
                if (es.ClusterState.TransientSegment != null)
                {
                    _transientClusterCacheAccessor = es.ClusterState.TransientSegment.CreateChunkAccessor();
                    _hasTransientClusterCache = true;
                }
                _clusterCacheArchId = id.ArchetypeId;
                _hasClusterCache = true;
            }

            // Primary base: PersistentStore for mixed/SV, TransientStore for pure-Transient
            if (es.ClusterState.ClusterSegment != null)
            {
                result._clusterBase = _clusterCacheAccessor.GetChunkAddress(clusterChunkId, writable);
            }
            else
            {
                result._clusterBase = _transientClusterCacheAccessor.GetChunkAddress(clusterChunkId, writable);
            }

            // Mixed archetype: also set TransientStore base
            if (_hasTransientClusterCache && es.ClusterState.ClusterSegment != null)
            {
                result._transientClusterBase = _transientClusterCacheAccessor.GetChunkAddress(clusterChunkId, writable);
            }

            result._clusterSlotIndex = slotIndex;
            result._clusterChunkId = clusterChunkId;
            result._clusterLayout = es.ClusterState.Layout;

            // For Versioned slots, walk chain and store resolved content chunkId in _locations.
            // Versioned reads via EntityRef.Read use _locations (not cluster slot) for MVCC correctness.
            if (meta.VersionedSlotMask != 0)
            {
                var layout = es.ClusterState.Layout;
                if (layout.SlotToVersionedIndex == null)
                {
                    goto skipVersionedWalk;
                }
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

                    var compTypeId = meta._componentTypeIds[slot];
                    var info = GetComponentInfoInternal(compTypeId, meta._slotToComponentType[slot]);

                    var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, TSN, true);
                    if (chainResult.IsSuccess)
                    {
                        result.SetLocation(slot, chainResult.Value.CurCompContentChunkId);
                    }
                }
                skipVersionedWalk:;
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

                var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, TSN, true);
                if (chainResult.IsFailure)
                {
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

    /// <summary>Read component data via the existing ComponentInfo accessor cache. Zero-copy — returns a ref into the page.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T ReadEcsComponentData<T>(ComponentTable table, int chunkId) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));
        byte* ptr = table.StorageMode == StorageMode.Transient ? info.TransientCompContentAccessor.GetChunkAddress(chunkId) : info.CompContentAccessor.GetChunkAddress(chunkId);
        return ref Unsafe.AsRef<T>(ptr + info.ComponentOverhead);
    }

    /// <summary>Write component data via the existing ComponentInfo accessor cache. Returns mutable ref.
    /// For SingleVersion: atomically marks chunkId in DirtyBitmap for tick fence serialization.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T WriteEcsComponentData<T>(ComponentTable table, int chunkId) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));
        byte* ptr;
        if (table.StorageMode == StorageMode.Transient)
        {
            ptr = info.TransientCompContentAccessor.GetChunkAddress(chunkId, true);
        }
        else
        {
            ptr = info.CompContentAccessor.GetChunkAddress(chunkId, true);
        }
        table.DirtyBitmap?.Set(chunkId);
        return ref Unsafe.AsRef<T>(ptr + info.ComponentOverhead);
    }

    /// <summary>
    /// Capture old indexed field values before the first SV in-place mutation per entity per tick.
    /// Called from <see cref="EntityRef.Write{T}(Comp{T})"/> for SingleVersion components with indexed fields.
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
        long pk = (long)entityId.RawValue;

        for (int i = 0; i < fields.Length; i++)
        {
            ref var ifi = ref fields[i];
            var oldKey = KeyBytes8.FromPointer(ptr + ifi.OffsetToField, ifi.Size);
            buffers[i].Append(chunkId, pk, oldKey);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Virtual methods — overridden by Transaction
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Copy-on-write for Versioned components. Not supported in base EntityAccessor — throws.</summary>
    internal virtual (int chunkId, nint ptr) EcsVersionedCopyOnWrite(Type compType, EntityId entityId, ComponentTable table)
        => throw new InvalidOperationException(
            "EntityAccessor does not support Versioned component writes. Use a full Transaction for systems that modify Versioned components.");

    /// <summary>Stage an EnabledBits change for commit. Not supported in base EntityAccessor — throws.</summary>
    internal virtual void StageEnableDisable(EntityId id, ushort newEnabledBits)
        => throw new InvalidOperationException(
            "EntityAccessor does not support Enable/Disable operations. Use a full Transaction for structural component changes.");
}
