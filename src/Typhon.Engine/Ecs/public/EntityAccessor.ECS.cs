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
        var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, chainRoot, TSN, true);
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
    /// Scoped to <see cref="PointInTimeAccessor"/> worker accessors via <c>_ownsPersistentEpochScope</c>. A <c>Transaction</c> registers in the chain, so
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
        if (!_ownsPersistentEpochScope || _dbe == null)
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

        var meta = _dbe.GetMetaByRouting(id.ArchetypeId);
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
        var needsGuard = !_ownsPersistentEpochScope && !_epochManager.IsCurrentThreadInScope;
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

        // Hinted lookup: EntityKey is dense and monotonic, so the low-bit hint slot resolves in one bucket-chunk visit — skipping the hash, the bucket
        // directory read and the chain scan that dominate the plain TryGet. Identical semantics (a stale or colliding hint falls back to the full lookup,
        // which refreshes it), and the hint array self-allocates on first miss. Transaction.ResolveEntity already took this path; the parallel/PTA path
        // did not, despite being the one that performs millions of resolves per tick.
        bool found = es.EntityMap.TryGetWithHint(id.EntityKey, readBuf, ref _entityMapCacheAccessor);

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

                var chainResult = RevisionChainReader.WalkChain(ref info.CompRevTableAccessor, compRevFirstChunkId, TSN, true);
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
        byte* ptr = ResolveSpawnAwarePayload(table, location, isOwnSpawn, info.TransientCompContentAccessor, info.CompContentAccessor);
        // Commit-discipline read-your-own-writes: return this tx's staged value if it has staged this (component, entity). The staging map is keyed by
        // entity PK, which the CALLER supplies — reading it back out of the chunk header instead was #713: a spawn-staging chunk has no PK written yet
        // (FinalizeSpawns stamps it at publish), so every own-spawn lookup keyed on 0.
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
    /// <c>ComponentStorageSize</c> bytes and decodes fields by offset. Backs <see cref="EntityRef.ReadRaw"/> for runtime tooling (the Workbench Data Browser).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* ReadEcsComponentDataRaw(ComponentTable table, int componentTypeId, Type componentType, int location, bool isOwnSpawn)
    {
        var info = GetComponentInfoByTypeId(componentTypeId, componentType);
        byte* ptr = ResolveSpawnAwarePayload(table, location, isOwnSpawn, info.TransientCompContentAccessor, info.CompContentAccessor);
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
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* ResolveSpawnAwarePayload(ComponentTable table, int location, bool isOwnSpawn, ChunkAccessor<TransientStore> transientAccessor,
        ChunkAccessor<PersistentStore> persistentAccessor)
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
            // FinalizeSpawns publishes it, so writing in place IS the atomic behaviour CM-01 asks for, and it is what TickFence already does. Staging it
            // was wrong three ways: StagedSlot.Location would carry a content chunk id where PublishStagedEntry expects a cluster location, the publish
            // would run against a HEAD that does not exist yet, and FinalizeSpawns would then overwrite it with the spawn value. Skipping the staging
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

        // DIRTY-01 (rules/ecs.md): a spawn sets no dirty bit. These bitmaps track write mutations to entities that are already PUBLISHED, and FinalizeSpawns
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
    /// Backs <see cref="EntityRef.Enable{T}(Comp{T}, in T)"/> for the one case the no-value overload refuses: a component the spawn never supplied, which has
    /// no chain and therefore nothing to enable. Spawn used to be the only producer of a first revision, because design decision #14 guaranteed every slot
    /// existed from spawn; once an unsupplied slot is genuinely absent (#845) that guarantee is gone and a component can begin mid-life.
    /// </remarks>
    internal virtual int CreateVersionedContentAndWrite<T>(EntityId id, byte slot, in T value) where T : unmanaged
        => throw new InvalidOperationException(
            "EntityAccessor does not support supplying a component value. Use a full Transaction for structural component changes.");
}
