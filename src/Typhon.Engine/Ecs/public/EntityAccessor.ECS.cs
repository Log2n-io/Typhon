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
        // Commit-discipline read-your-own-writes: return this tx's staged value if it has staged this (component, entity). The chunk's inline
        // entityPK (offset 0 for SV/Transient) keys the staging map.
        if (_discipline == DurabilityDiscipline.Commit && table.StorageMode == StorageMode.SingleVersion
            && info.CommitStaged != null && info.CommitStaged.TryGetValue(*(long*)ptr, out var slot))
        {
            return ref Unsafe.AsRef<T>(_commitStagingBuffer + slot.Offset);
        }
        return ref Unsafe.AsRef<T>(ptr + info.ComponentOverhead);
    }

    /// <summary>
    /// Non-generic counterpart to <see cref="ReadEcsComponentData{T}"/>: resolves the raw storage pointer for a component instance without a compile-time type
    /// parameter. Returns a pointer to the component's field data (already past <see cref="ComponentInfo.ComponentOverhead"/>); the caller reads
    /// <c>ComponentStorageSize</c> bytes and decodes fields by offset. Backs <see cref="EntityRef.ReadRaw"/> for runtime tooling (the Workbench Data Browser).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* ReadEcsComponentDataRaw(ComponentTable table, int componentTypeId, Type componentType, int chunkId)
    {
        var info = GetComponentInfoByTypeId(componentTypeId, componentType);
        byte* ptr = table.StorageMode == StorageMode.Transient ? info.TransientCompContentAccessor.GetChunkAddress(chunkId) : info.CompContentAccessor.GetChunkAddress(chunkId);
        return ptr + info.ComponentOverhead;
    }

    /// <summary>Write component data via the existing ComponentInfo accessor cache. Returns mutable ref.
    /// For SingleVersion: atomically marks chunkId in DirtyBitmap for tick fence serialization.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T WriteEcsComponentData<T>(ComponentTable table, int chunkId) where T : unmanaged
    {
        var info = GetComponentInfo(typeof(T));

        // Commit discipline (SingleVersion, Variant A): stage the write — leave the chunk HEAD untouched and unmarked (CM-01). The HEAD still holds the
        // pre-write value, so seed the staging slot from it for partial-write correctness. CM-02 escalation first (so DefaultDiscipline=Commit applies).
        if (table.StorageMode == StorageMode.SingleVersion)
        {
            if (table.Discipline == DurabilityDiscipline.Commit)
            {
                ResolveCommitDiscipline(table);
            }
            if (_discipline == DurabilityDiscipline.Commit)
            {
                byte* head = info.CompContentAccessor.GetChunkAddress(chunkId);
                // Flat location is the content chunkId (captured for the no-re-lookup publish).
                return ref StageCommitWriteCore<T>(info, *(long*)head, chunkId, head + info.ComponentOverhead);
            }
        }

        byte* ptr;
        if (table.StorageMode == StorageMode.Transient)
        {
            ptr = info.TransientCompContentAccessor.GetChunkAddress(chunkId, true);
        }
        else
        {
            ptr = info.CompContentAccessor.GetChunkAddress(chunkId, true);
            _didInPlaceSvWrite = true;   // CM-02: a TickFence in-place SingleVersion write happened — blocks late escalation to Commit
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
    // Committed durability discipline — Variant A staging (issue #392)
    // ═══════════════════════════════════════════════════════════════════════

    private const int InitialCommitStagingCapacity = 4096;

    /// <summary>
    /// CM-02 discipline resolution, invoked from the write path for a <see cref="DurabilityDiscipline.Commit"/>-defaulted
    /// <see cref="StorageMode.SingleVersion"/> component. Escalates this accessor's whole transaction to Commit on first touch (so
    /// every subsequent write is commit-durable), and rejects escalation if a TickFence in-place write has already happened (we cannot
    /// retroactively make an applied write atomic). Idempotent and cheap once escalated. Callers gate on
    /// <c>table.Discipline == Commit</c> so the TickFence hot path never reaches here for a non-Commit component.
    /// </summary>
    internal void ResolveCommitDiscipline(ComponentTable table)
    {
        if (_discipline == DurabilityDiscipline.Commit)
        {
            return;
        }

        if (_didInPlaceSvWrite)
        {
            throw new InvalidOperationException(
                $"Component '{table.Name}' is declared DefaultDiscipline=Commit, but this transaction has already performed a TickFence " +
                "in-place write. Create the transaction with discipline: DurabilityDiscipline.Commit before writing any component so the " +
                "whole transaction is commit-durable (CM-02 uniformity).");
        }

        _discipline = DurabilityDiscipline.Commit;
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
        }
        return ref Unsafe.AsRef<T>(_commitStagingBuffer + slot.Offset);
    }

    /// <summary>
    /// Records that a TickFence in-place SingleVersion write has happened (called from the cluster write path, the counterpart of the flag set inline by
    /// <see cref="WriteEcsComponentData{T}"/> for the non-cluster path). Blocks a later CM-02 auto-escalation to Commit, which could no longer make the
    /// already-applied write atomic. One bool store — negligible beside the cluster <c>SetDirty</c> on the same path.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void NoteSvInPlaceWrite() => _didInPlaceSvWrite = true;

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

    /// <summary>Copy-on-write for Versioned components. Not supported in base EntityAccessor — throws.</summary>
    internal virtual (int chunkId, nint ptr) EcsVersionedCopyOnWrite(Type compType, EntityId entityId, ComponentTable table)
        => throw new InvalidOperationException(
            "EntityAccessor does not support Versioned component writes. Use a full Transaction for systems that modify Versioned components.");

    /// <summary>Stage an EnabledBits change for commit. Not supported in base EntityAccessor — throws.</summary>
    internal virtual void StageEnableDisable(EntityId id, ushort newEnabledBits)
        => throw new InvalidOperationException(
            "EntityAccessor does not support Enable/Disable operations. Use a full Transaction for structural component changes.");
}
