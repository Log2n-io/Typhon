using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

// Applies committed WAL v2 records back into engine state during crash recovery, reusing the engine's own write primitives (the design's "one write path").
// P1.2: rebuilds a committed spawned entity — the EntityRecord plus, per spawn-init Slot, a committed revision-chain root holding the component value — by
// BUILDING the full record then InsertNew once, mirroring the live FinalizeSpawns (approach B; the live engine has no in-place location update — a Versioned
// location is written once at spawn and stays fixed, revisions append within the chain). Both the flat (non-cluster) path and the cluster path are wired:
// a cluster-eligible archetype reconstructs the entity into a CLUSTER slot (ClaimSlot + SoA value write + ClusterEntityRecord), mirroring the live
// FinalizeSpawns cluster branch — this is what makes Commit-discipline SingleVersion values WAL-recoverable (#392 AC-2/AC-7). Destroy / SetEnabledBits /
// collections follow in later increments. Runs single-threaded under one epoch scope with a dedicated ChangeSet (so applied page mutations are captured by the
// sealing checkpoint). See 03-recovery.md §3.

internal sealed unsafe class RecoveryApplier : IDisposable
{
    /// <summary>One committed component value to restore (logical-truth payload + the TSN of the transaction that committed it).</summary>
    internal struct SlotData
    {
        public ushort SlotIndex; // per-archetype component slot (the WAL wire identity, LOG-06)
        public byte[] Payload;
        public long Tsn;
    }

    private readonly DatabaseEngine _dbe;
    private readonly ChangeSet _changeSet;
    private long _maxTsn;

    // Cache the current archetype's map accessor + metadata — recovery applies entities usually clustered by archetype.
    private ushort _lastArchId = ushort.MaxValue;
    private bool _hasAccessor;
    private ArchetypeEngineState _engineState;
    private ArchetypeMetadata _metadata;
    private int _componentCount;
    private ChunkAccessor<PersistentStore> _mapAccessor;

    // Per-archetype cluster SoA accessor (set only when the current archetype is cluster-eligible with a PersistentStore ClusterSegment).
    private bool _hasClusterAccessor;
    private ChunkAccessor<PersistentStore> _clusterAccessor;

    // Per-ComponentTable recovery ComponentInfo (content + revision-table accessors bound to the recovery ChangeSet). Mirrors
    // EntityAccessor.GetComponentInfo's Versioned/SingleVersion setup, but threaded through THIS ChangeSet. Flushed at Dispose.
    private readonly Dictionary<ComponentTable, ComponentInfo> _infoByTable = new();

    // routingId → highest entity key this recovery applied for that archetype (#697). Keyed by the EntityId's per-DB ROUTING id, so the driver resolves it
    // through DatabaseEngine._stateByRouting — NOT _archetypeStates, which is indexed by the per-process catalog id and is a different space entirely.
    private readonly Dictionary<ushort, long> _maxEntityKeyByArchetype = new();

    // ── Per-entity collection state (#389), valid only between an apply and the ApplyCollectionFolds that follows it ──

    /// <summary>
    /// slot → the content chunk this apply wrote the component's value into.
    /// </summary>
    /// <remarks>
    /// Recorded rather than re-derived: for a Versioned slot the EntityMap location is the chain ROOT, and the value lives in the head revision's content
    /// chunk, which only the code that just created or appended that revision knows without walking the chain. The fold flush has to write the new buffer
    /// handle into exactly those bytes.
    /// </remarks>
    private readonly Dictionary<ushort, int> _appliedContentChunkBySlot = new();

    /// <summary>
    /// (slot, fieldId) → the collection handle the row held BEFORE this apply overwrote it, for storage that is overwritten in place.
    /// </summary>
    /// <remarks>
    /// Only SingleVersion storage populates this, and the asymmetry is the point. An SV value is rewritten in place, so the buffer the row used to own becomes
    /// unreachable the instant the payload is copied — the fold flush must release it or it leaks on every recovery. A Versioned apply instead appends a NEW
    /// revision, leaving the previous one (and its buffer) referenced until Phase-4 SCRUB frees the content chunk, which releases the buffer with it. Releasing
    /// it here as well would double-decrement.
    /// </remarks>
    private readonly Dictionary<(ushort Slot, ushort FieldId), int> _preApplyHandles = new();

    public RecoveryApplier(DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        _dbe = dbe;
        _changeSet = new ChangeSet(dbe.MMF);
    }

    /// <summary>Highest TSN applied — recovery restores NextFreeTSN above this (RB-05).</summary>
    public long MaxTsn => _maxTsn;

    /// <summary>
    /// Highest entity key applied, per archetype id — recovery restores each archetype's <c>NextEntityKey</c> above this (RB-05, entity-key half; #697).
    /// </summary>
    /// <remarks>
    /// The rebuild paths already raise <c>NextEntityKey</c> from the persisted base (<c>DatabaseEngine.RebuildEntityMaps…</c>), but they run BEFORE the WAL
    /// window is applied. Entities inserted afterwards by <see cref="ApplySpawnedEntity"/> carry higher keys and bumped nothing, so a crash with no checkpoint
    /// left the counter at 0 and the first post-recovery spawn re-issued key 1 over a live recovered entity. This is the window's half of the same watermark.
    /// </remarks>
    /// <value>Keyed by per-DB <b>routing</b> id (what an <see cref="EntityId"/> carries), not the per-process catalog id.</value>
    public IReadOnlyDictionary<ushort, long> MaxEntityKeyByArchetype => _maxEntityKeyByArchetype;

    /// <summary>Records a committed record's TSN toward the RB-05 watermark (called for every applicable record, applied or not).</summary>
    public void Track(long tsn)
    {
        if (tsn > _maxTsn)
        {
            _maxTsn = tsn;
        }
    }

    /// <summary>Records an applied entity's key toward its archetype's allocation watermark (#697).</summary>
    private void TrackEntityKey(long entityIdRaw)
    {
        var id = EntityId.FromRaw(entityIdRaw);
        var archetypeId = id.ArchetypeId;
        if (!_maxEntityKeyByArchetype.TryGetValue(archetypeId, out var current) || id.EntityKey > current)
        {
            _maxEntityKeyByArchetype[archetypeId] = id.EntityKey;
        }
    }

    /// <summary>
    /// Rebuilds a committed spawned entity: the EntityRecord (BornTSN, EnabledBits) plus, for each spawn-init Slot, a committed
    /// revision-chain root holding the component value, then inserts it into the archetype's EntityMap. Mirrors the live
    /// FinalizeSpawns build-then-insert so recovery produces the same persisted shape through the same insert primitive. Flat
    /// (non-cluster) Versioned/SingleVersion path; the entity becomes alive AND its spawn-init component values resolve.
    /// </summary>
    public void ApplySpawnedEntity(long entityIdRaw, ushort archetypeId, ushort enabledBits, long bornTsn, IReadOnlyCollection<SlotData> slots)
    {
        Track(bornTsn);
        TrackEntityKey(entityIdRaw);
        EnsureArchetype(archetypeId);
        BeginEntity();  // a spawn builds fresh storage, so there is no prior handle to release — only the content chunks to remember

        if (_hasClusterAccessor)
        {
            ApplySpawnedEntityToCluster(entityIdRaw, enabledBits, bornTsn, slots);
            return;
        }

        var key = EntityId.FromRaw(entityIdRaw).EntityKey;

        byte* recordPtr = stackalloc byte[EntityRecordAccessor.MaxRecordSize];

        // Idempotent spawn (AP-12): re-running recovery — e.g. after a crash mid-seal that persisted this entity to the data file
        // but did not advance CheckpointLSN, so its records are replayed again — must NOT double-insert (EntityMap.InsertNew skips
        // the duplicate check, assuming a fresh key). Spawn-if-absent: probe the loaded map first, reusing recordPtr as the buffer.
        if (_engineState.EntityMap.TryGet(key, recordPtr, ref _mapAccessor))
        {
            return;
        }

        EntityRecordAccessor.InitializeRecord(recordPtr, _componentCount); // zeroes header (DiedTSN=0=alive) + all locations
        ref var header = ref EntityRecordAccessor.GetHeader(recordPtr);
        header.BornTSN = bornTsn;
        header.EnabledBits = enabledBits;

        var locations = (int*)(recordPtr + EntityRecordAccessor.HeaderSize);

        if (slots != null)
        {
            foreach (var slot in slots)
            {
                // The caller has already collapsed a component's history to its latest committed value (last write wins), so each slot here is the final value
                // and carries the TSN of the transaction that committed it (which may be later than the spawn's — a post-spawn Write). The chain element
                // records that TSN; BornTSN stays the spawn's. The wire carries the per-archetype slot directly (LOG-06) — no ComponentTypeId→slot lookup
                // needed. Guard against a malformed/foreign record whose slot exceeds this archetype's component count.
                var slotIndex = slot.SlotIndex;
                if (slotIndex >= _metadata.ComponentCount)
                {
                    continue;
                }

                var table = _engineState.SlotToComponentTable[slotIndex];
                switch (table.StorageMode)
                {
                    case StorageMode.Versioned:
                        locations[slotIndex] = CreateVersionedChainRoot(table, entityIdRaw, slot.Tsn, slot.Payload, out var versionedContent);
                        _appliedContentChunkBySlot[slotIndex] = versionedContent;
                        break;
                    case StorageMode.SingleVersion:
                        var svContent = CreateSingleVersionContent(table, slot.Payload);
                        locations[slotIndex] = svContent;
                        _appliedContentChunkBySlot[slotIndex] = svContent;
                        break;
                    default:
                        locations[slotIndex] = 0;   // Transient values are never logged
                        break;
                }
            }
        }

        _engineState.EntityMap.InsertNew(key, recordPtr, ref _mapAccessor, _changeSet);
    }

    /// <summary>
    /// Cluster counterpart of <see cref="ApplySpawnedEntity"/>: reconstructs a committed spawned entity into a CLUSTER slot — claims a slot, writes each
    /// committed component value into the cluster SoA (the HEAD; for Versioned it also builds the revision-chain root and records its chunkId), writes the
    /// EntityId + per-slot EnabledBits into the cluster, and inserts the ClusterEntityRecord. Mirrors the live FinalizeSpawns cluster branch. Spatial
    /// cell-routing and AABBs are rebuilt wholesale on reopen, so a plain (non-spatial) ClaimSlot is used. RB-01: secondary indexes are NOT populated here
    /// — they are rebuilt from final HEAD data at open. Idempotent (AP-12): a re-applied entity already in the loaded map is skipped.
    /// </summary>
    private void ApplySpawnedEntityToCluster(long entityIdRaw, ushort enabledBits, long bornTsn, IReadOnlyCollection<SlotData> slots)
    {
        var key = EntityId.FromRaw(entityIdRaw).EntityKey;
        byte* recordPtr = stackalloc byte[EntityRecordAccessor.MaxRecordSize];

        if (_engineState.EntityMap.TryGet(key, recordPtr, ref _mapAccessor))
        {
            return; // idempotent re-apply
        }

        var clusterState = _engineState.ClusterState;
        var layout = clusterState.Layout;

        var (clusterChunkId, slotIdx) = clusterState.ClaimSlot(ref _clusterAccessor, _changeSet);
        byte* clusterBase = _clusterAccessor.GetChunkAddress(clusterChunkId, true);

        // Build the ClusterEntityRecord (19 bytes base + 4 bytes per Versioned slot).
        ClusterEntityRecordAccessor.InitializeRecord(recordPtr, _metadata.VersionedSlotCount);
        ref var header = ref ClusterEntityRecordAccessor.GetHeader(recordPtr);
        header.BornTSN = bornTsn;
        header.EnabledBits = enabledBits;
        clusterState.NoteClusterBorn(clusterChunkId, bornTsn);   // H1: replay must bound the cluster too, or a recovered entity is invisible to the summary
        ClusterEntityRecordAccessor.SetClusterChunkId(recordPtr, clusterChunkId);
        ClusterEntityRecordAccessor.SetSlotIndex(recordPtr, (byte)slotIdx);

        if (slots != null)
        {
            foreach (var slot in slots)
            {
                var slotIndex = slot.SlotIndex; // per-archetype slot from the wire (LOG-06)
                if (slotIndex >= _metadata.ComponentCount)
                {
                    continue; // foreign / malformed record — tolerate
                }

                var table = _engineState.SlotToComponentTable[slotIndex];
                if (table.StorageMode == StorageMode.Transient)
                {
                    continue; // Transient values are never logged
                }

                // Write the committed value into the cluster SoA HEAD slot (payload is value-only; its length is the component storage size == ComponentSize).
                int compSize = layout.ComponentSize(slotIndex);
                byte* dst = clusterBase + layout.ComponentOffset(slotIndex) + slotIdx * compSize;
                slot.Payload.AsSpan().CopyTo(new Span<byte>(dst, compSize));

                // Versioned: also rebuild the revision-chain root and record its chunkId (the cluster slot is the HEAD cache over the chain).
                if (table.StorageMode == StorageMode.Versioned)
                {
                    int vi = layout.SlotToVersionedIndex[slotIndex];
                    if (vi >= 0)
                    {
                        var chainRoot = CreateVersionedChainRoot(table, entityIdRaw, slot.Tsn, slot.Payload, out var contentChunkId);
                        ClusterEntityRecordAccessor.SetCompRevFirstChunkId(recordPtr, vi, chainRoot);
                        _appliedContentChunkBySlot[slotIndex] = contentChunkId;
                    }
                }
            }
        }

        // Write the full EntityId and per-slot EnabledBits into the cluster SoA (occupancy bit was set by ClaimSlot).
        *(long*)(clusterBase + layout.EntityIdsOffset + slotIdx * 8) = entityIdRaw;
        for (int slot = 0; slot < _componentCount; slot++)
        {
            if ((enabledBits & (1 << slot)) != 0)
            {
                *(ulong*)(clusterBase + layout.EnabledBitsOffset(slot)) |= 1UL << slotIdx;
            }
        }

        if (slots != null)
        {
            foreach (var sd in slots)
            {
                var t = sd.SlotIndex < _metadata.ComponentCount ? _engineState.SlotToComponentTable[sd.SlotIndex] : null;
            }
        }
        _engineState.EntityMap.InsertNew(key, recordPtr, ref _mapAccessor, _changeSet);
    }

    /// <summary>
    /// Applies a committed Destroy to an entity that already exists in the loaded EntityMap (its Spawn is below the checkpoint
    /// frontier, so it is not in the recovery window — only the Destroy is). Sets DiedTSN on the existing record and writes it
    /// back dirty-marked, mirroring the live FlushPendingDestroys archetype-level tombstone. Idempotent: a missing entity is a
    /// no-op. Component-chain / index cleanup is consolidation (orphan sweep, a later increment) — DiedTSN alone makes IsAlive false.
    /// </summary>
    public void ApplyDestroyToExisting(long entityIdRaw, long tsn)
    {
        Track(tsn);
        var eid = EntityId.FromRaw(entityIdRaw);
        EnsureArchetype(eid.ArchetypeId);

        var key = eid.EntityKey;
        byte* readBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];
        if (!_engineState.EntityMap.TryGet(key, readBuf, ref _mapAccessor))
        {
            return; // not in the base map (already gone / never persisted) — nothing to tombstone
        }

        EntityRecordAccessor.GetHeader(readBuf).DiedTSN = tsn;
        // H1: same reasoning as the commit-path tombstone — the replayed death has to take its cluster off the visibility fast path.
        _engineState.ClusterState?.NoteClusterDied(ClusterEntityRecordAccessor.GetClusterChunkId(readBuf));
        _engineState.EntityMap.Upsert(key, readBuf, ref _mapAccessor, _changeSet);
    }

    /// <summary>
    /// Applies committed component VALUES to an entity that already exists in the loaded EntityMap — its Spawn is below the checkpoint frontier, so the
    /// recovery window carries only the update (#569).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base-entity counterpart of the spawn-init slots folded by <see cref="ApplySpawnedEntity"/>, and the piece whose absence made
    /// <see cref="CommitDiscipline.TickFence"/>'s documented ≤1-tick loss window untrue: the driver aggregated these payloads correctly and then dropped
    /// them, so the durability actually delivered for a steady-state workload — spawn once, mutate forever — was the CHECKPOINT interval (30 s by default),
    /// not one tick. Note this is not SingleVersion-specific despite #569's title: the branch is keyed on "no Spawn in this window", so flat Versioned
    /// archetypes lost their updates identically.
    /// </para>
    /// <para>
    /// Each storage home is updated through the same primitive the LIVE write path uses, so recovery produces the shape a normal write would have:
    /// <see cref="ComponentRevisionManager.AddCompRev"/> appends to the existing chain for Versioned (the chain ROOT is unchanged, so the EntityMap record
    /// needs no rewrite), SingleVersion content is overwritten in place, and a cluster entity's SoA HEAD is written at the slot its ClusterEntityRecord
    /// already names. Appending rather than re-rooting matters: Phase-4 SCRUB collapses each chain to its highest-TSN committed element and frees the rest, so
    /// an appended revision is reclaimed correctly, whereas a fresh root would orphan the old chain's chunks where nothing walks them.
    /// </para>
    /// <para>Idempotent (AP-12): an entity missing from the base map is a no-op — its Spawn was not below the frontier, so the window's own Spawn handling
    /// owns it.</para>
    /// </remarks>
    public void ApplySlotToExisting(long entityIdRaw, IReadOnlyCollection<SlotData> slots)
    {
        if (slots == null || slots.Count == 0)
        {
            return;
        }

        var eid = EntityId.FromRaw(entityIdRaw);
        EnsureArchetype(eid.ArchetypeId);
        BeginEntity();

        var key = eid.EntityKey;
        byte* readBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];
        if (!_engineState.EntityMap.TryGet(key, readBuf, ref _mapAccessor))
        {
            return;
        }

        if (_hasClusterAccessor)
        {
            ApplySlotToExistingCluster(entityIdRaw, readBuf, slots);
            return;
        }

        var locations = (int*)(readBuf + EntityRecordAccessor.HeaderSize);
        var rewritten = false;

        foreach (var slot in slots)
        {
            var slotIndex = slot.SlotIndex;
            if (slotIndex >= _metadata.ComponentCount)
            {
                continue; // foreign / malformed record — tolerate, as the spawn path does
            }

            var table = _engineState.SlotToComponentTable[slotIndex];
            switch (table.StorageMode)
            {
                case StorageMode.Versioned:
                    var root = locations[slotIndex];
                    if (root == 0)
                    {
                        // The base record has no chain for this slot (the component was never written before the checkpoint). Build one, exactly as a spawn
                        // would, and repoint the location — there is no prior chain to append to and none to orphan.
                        locations[slotIndex] = CreateVersionedChainRoot(table, entityIdRaw, slot.Tsn, slot.Payload, out var newChainContent);
                        _appliedContentChunkBySlot[slotIndex] = newChainContent;
                        rewritten = true;
                    }
                    else
                    {
                        AppendVersionedRevision(table, root, slot.Tsn, slot.Payload, out var appendedContent);
                        _appliedContentChunkBySlot[slotIndex] = appendedContent;
                    }

                    break;

                case StorageMode.SingleVersion:
                    var content = locations[slotIndex];
                    if (content == 0)
                    {
                        var freshContent = CreateSingleVersionContent(table, slot.Payload);
                        locations[slotIndex] = freshContent;
                        _appliedContentChunkBySlot[slotIndex] = freshContent;
                        rewritten = true;
                    }
                    else
                    {
                        var info = GetRecoveryInfo(table);
                        var dst = info.CompContentAccessor.GetChunkAsSpan(content, true);
                        CapturePreApplyHandles(table, slotIndex, dst[info.ComponentOverhead..]);
                        slot.Payload.AsSpan().CopyTo(dst[info.ComponentOverhead..]);
                        _appliedContentChunkBySlot[slotIndex] = content;
                    }

                    break;

                default:
                    break; // Transient values are never logged
            }
        }

        // Only the two "there was no prior storage" branches change the record itself; an append or an in-place overwrite leaves the locations untouched, and
        // rewriting the record anyway would dirty an EntityMap page for nothing.
        if (rewritten)
        {
            _engineState.EntityMap.Upsert(key, readBuf, ref _mapAccessor, _changeSet);
        }
    }

    /// <summary>Cluster counterpart of <see cref="ApplySlotToExisting"/>: writes the committed values into the SoA slot the entity already occupies.</summary>
    private void ApplySlotToExistingCluster(long entityIdRaw, byte* recordPtr, IReadOnlyCollection<SlotData> slots)
    {
        var clusterState = _engineState.ClusterState;
        var layout = clusterState.Layout;
        var clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(recordPtr);
        var slotIdx = ClusterEntityRecordAccessor.GetSlotIndex(recordPtr);
        byte* clusterBase = _clusterAccessor.GetChunkAddress(clusterChunkId, true);

        foreach (var slot in slots)
        {
            var slotIndex = slot.SlotIndex;
            if (slotIndex >= _metadata.ComponentCount)
            {
                continue;
            }

            var table = _engineState.SlotToComponentTable[slotIndex];
            if (table.StorageMode == StorageMode.Transient)
            {
                continue;
            }

            var compSize = layout.ComponentSize(slotIndex);
            byte* dst = clusterBase + layout.ComponentOffset(slotIndex) + slotIdx * compSize;

            // A cluster slot is overwritten in place, so this is the last moment the previous collection handle exists (SingleVersion only — a Versioned slot's
            // prior buffer stays referenced by the prior revision until SCRUB frees it).
            if (table.StorageMode == StorageMode.SingleVersion)
            {
                CapturePreApplyHandles(table, slotIndex, new ReadOnlySpan<byte>(dst, compSize));
            }

            slot.Payload.AsSpan().CopyTo(new Span<byte>(dst, compSize));

            // Versioned in a cluster: the SoA slot is the HEAD cache over the chain, so the chain has to carry the value too or the scrub would collapse the
            // HEAD back to a stale revision.
            if (table.StorageMode == StorageMode.Versioned)
            {
                var vi = layout.SlotToVersionedIndex[slotIndex];
                if (vi >= 0)
                {
                    var root = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(recordPtr, vi);
                    if (root != 0)
                    {
                        AppendVersionedRevision(table, root, slot.Tsn, slot.Payload, out var appendedContent);
                        _appliedContentChunkBySlot[slotIndex] = appendedContent;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Appends one committed revision carrying <paramref name="payload"/> to the chain rooted at <paramref name="chainRootChunkId"/>.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>CreateVersionedChainRoot</c>'s end state — content written at <c>ComponentOverhead</c>, element committed so its isolation flag is
    /// clear — but through <see cref="ComponentRevisionManager.AddCompRev"/>, which allocates the content chunk and grows the chain when the current one is
    /// full. <c>lockAlreadyHeld</c> stays false: recovery is single-threaded, and taking the chain lock costs nothing here while keeping this path identical
    /// to the live one rather than a second implementation that has to be kept in step.
    /// </remarks>
    private void AppendVersionedRevision(ComponentTable table, int chainRootChunkId, long tsn, byte[] payload) =>
        AppendVersionedRevision(table, chainRootChunkId, tsn, payload, out _);

    private void AppendVersionedRevision(ComponentTable table, int chainRootChunkId, long tsn, byte[] payload, out int contentChunkId)
    {
        var info = GetRecoveryInfo(table);
        var compRevInfo = new ComponentInfo.CompRevInfo
        {
            CompRevTableFirstChunkId = chainRootChunkId,
            PrevRevisionIndex = -1,
            CurRevisionIndex = -1,
        };

        ComponentRevisionManager.AddCompRev(info, ref compRevInfo, tsn, 0, false);
        contentChunkId = compRevInfo.CurCompContentChunkId;

        byte* contentBase = info.CompContentAccessor.GetChunkAddress(compRevInfo.CurCompContentChunkId, true);
        payload.AsSpan().CopyTo(new Span<byte>(contentBase + info.ComponentOverhead, payload.Length));

        var handle = ComponentRevisionManager.GetRevisionElement(ref info.CompRevTableAccessor, chainRootChunkId, compRevInfo.CurRevisionIndex);
        handle.Commit(tsn);
    }

    /// <summary>Resets the per-entity scratch that <see cref="ApplyCollectionFolds"/> consumes. Called at the top of every entity apply.</summary>
    private void BeginEntity()
    {
        _appliedContentChunkBySlot.Clear();
        _preApplyHandles.Clear();
    }

    /// <summary>Records the collection handles a component's value holds right now, before an in-place overwrite destroys them.</summary>
    private void CapturePreApplyHandles(ComponentTable table, ushort slotIndex, ReadOnlySpan<byte> currentValue)
    {
        if (!table.HasCollections)
        {
            return;
        }

        foreach (var f in table.CollectionFields)
        {
            if (currentValue.Length >= f.OffsetInComponentStorage + f.HandleSize)
            {
                _preApplyHandles[(slotIndex, f.FieldId)] = BinaryPrimitives.ReadInt32LittleEndian(currentValue[f.OffsetInComponentStorage..]);
            }
        }
    }

    /// <summary>
    /// Writes the folded collection content back into the entity — the <c>FoldFlush</c> of 03-recovery.md §5, and the step that makes a
    /// <c>ComponentCollection</c> crash-durable at all (#389).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This MUST run after the entity's Slot apply, never before.</b> The design does not state the ordering, and it is load-bearing in two directions. A
    /// Slot apply overwrites the whole component value, so a flush that ran first would be clobbered outright. And because LOG-06 zeroes the handle in every
    /// Slot payload, the apply leaves the row pointing at no buffer — this flush is what gives it one again. Recovery aggregates all of an entity's Slot
    /// records into a single apply before calling here, so "after the apply" is one well-defined point rather than a race between records.
    /// </para>
    /// <para>
    /// <b>The handle is written to every home the value has.</b> A flat entity has one (the content chunk); a cluster entity has the SoA slot, and if the
    /// component is Versioned, the head revision's content chunk as well — the SoA is a cache over the chain, and SCRUB collapses the chain to its highest-TSN
    /// element, so a handle written only to the cache would be replaced by the chain's stale one.
    /// </para>
    /// <para>
    /// Idempotent (AP-12): re-applying the same window recomputes the same element list and calls <see cref="VariableSizedBufferSegmentBase{TStore}.SetElementsRaw"/>
    /// again, which releases the buffer the previous pass created and allocates an equivalent one. Content and refcount converge; only the buffer id differs,
    /// which AP-13 tolerates by design.
    /// </para>
    /// </remarks>
    public void ApplyCollectionFolds(long entityIdRaw, IReadOnlyDictionary<(ushort Slot, ushort FieldId), List<byte[]>> folds)
    {
        if (folds == null || folds.Count == 0)
        {
            BeginEntity();
            return;
        }

        var eid = EntityId.FromRaw(entityIdRaw);
        EnsureArchetype(eid.ArchetypeId);

        byte* recordPtr = stackalloc byte[EntityRecordAccessor.MaxRecordSize];
        if (!_engineState.EntityMap.TryGet(eid.EntityKey, recordPtr, ref _mapAccessor))
        {
            BeginEntity();
            return;     // the entity is not alive after this window (spawned-and-destroyed, or never recovered) — its collections go with it
        }

        foreach (var ((slotIndex, fieldId), elements) in folds)
        {
            if (slotIndex >= _metadata.ComponentCount)
            {
                continue;   // foreign / malformed record — tolerated exactly as the slot paths tolerate it
            }

            var table = _engineState.SlotToComponentTable[slotIndex];
            if (!TryGetCollectionField(table, fieldId, out var field))
            {
                continue;   // the schema no longer has this collection field (a migration dropped it) — nothing to restore it into
            }

            var content = Flatten(elements, field.Vsbs.ElementSize);
            var oldBufferId = _preApplyHandles.TryGetValue((slotIndex, fieldId), out var captured) ? captured : 0;

            var vsbsAccessor = field.Vsbs.Segment.CreateChunkAccessor(_changeSet);
            int newBufferId;
            try
            {
                newBufferId = field.Vsbs.SetElementsRaw(oldBufferId, content, ref vsbsAccessor);
            }
            finally
            {
                vsbsAccessor.CommitChanges();
                vsbsAccessor.Dispose();
            }

            WriteCollectionHandle(recordPtr, table, slotIndex, field, newBufferId);
        }

        BeginEntity();
    }

    /// <summary>Concatenates a fold's per-element byte arrays into one contiguous span, validating each against the segment's element width.</summary>
    private static ReadOnlySpan<byte> Flatten(List<byte[]> elements, int elementSize)
    {
        var buffer = new byte[elements.Count * elementSize];
        for (var i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            if (e.Length != elementSize)
            {
                // §9.e: a structural impossibility, not crash damage. An element whose width disagrees with the schema means the producer and this reader
                // disagree about the format, and writing it anyway would silently shift every following element.
                ThrowHelper.ThrowInvalidOp(
                    $"Recovery collection fold: element {i} is {e.Length} bytes, but the segment stores {elementSize}-byte elements. The log and the schema "
                    + "disagree about this field's element type — recovery fails loudly rather than writing a misaligned buffer.");
            }

            e.CopyTo(buffer.AsSpan(i * elementSize));
        }

        return buffer;
    }

    private static bool TryGetCollectionField(ComponentTable table, ushort fieldId, out ComponentTable.CollectionFieldInfo field)
    {
        foreach (var f in table.CollectionFields)
        {
            if (f.FieldId == fieldId)
            {
                field = f;
                return true;
            }
        }

        field = default;
        return false;
    }

    /// <summary>Writes a freshly-set buffer handle into every storage home the component's value occupies.</summary>
    private void WriteCollectionHandle(byte* recordPtr, ComponentTable table, ushort slotIndex, in ComponentTable.CollectionFieldInfo field, int bufferId)
    {
        if (_hasClusterAccessor)
        {
            var layout = _engineState.ClusterState.Layout;
            var clusterChunkId = ClusterEntityRecordAccessor.GetClusterChunkId(recordPtr);
            var slotIdx = ClusterEntityRecordAccessor.GetSlotIndex(recordPtr);
            byte* clusterBase = _clusterAccessor.GetChunkAddress(clusterChunkId, true);
            var compSize = layout.ComponentSize(slotIndex);

            // A cluster slot carries no component overhead, so the field's value-relative offset is already slot-relative.
            *(int*)(clusterBase + layout.ComponentOffset(slotIndex) + (slotIdx * compSize) + field.OffsetInComponentStorage) = bufferId;
        }

        if (_appliedContentChunkBySlot.TryGetValue(slotIndex, out var contentChunkId) && contentChunkId != 0)
        {
            var info = GetRecoveryInfo(table);
            byte* contentBase = info.CompContentAccessor.GetChunkAddress(contentChunkId, true);
            *(int*)(contentBase + info.ComponentOverhead + field.OffsetInComponentStorage) = bufferId;
        }
    }

    /// <summary>
    /// Applies a committed absolute enabled-bits change to a pre-existing (checkpointed) entity — the base-entity counterpart of
    /// the spawn-time bits folded by <see cref="ApplySpawnedEntity"/>. Sets the record's EnabledBits in place (flat path) and
    /// writes it back dirty-marked. Idempotent: an absolute set re-applies cleanly; a missing entity is a no-op.
    /// </summary>
    public void ApplySetEnabledBitsToExisting(long entityIdRaw, ushort enabledBits)
    {
        var eid = EntityId.FromRaw(entityIdRaw);
        EnsureArchetype(eid.ArchetypeId);

        var key = eid.EntityKey;
        byte* readBuf = stackalloc byte[EntityRecordAccessor.MaxRecordSize];
        if (!_engineState.EntityMap.TryGet(key, readBuf, ref _mapAccessor))
        {
            return;
        }

        EntityRecordAccessor.GetHeader(readBuf).EnabledBits = enabledBits;
        _engineState.EntityMap.Upsert(key, readBuf, ref _mapAccessor, _changeSet);
    }

    // Allocates a content chunk holding the payload and a committed single-element revision chain pointing at it — exactly the
    // spawn→commit end-state the live ComponentRevisionManager produces (AllocCompRevStorage creates the isolated element, then
    // the live ElementRevisionHandle.Commit clears the isolation flag). Returns the chain-root chunk id (the slot's location).
    private int CreateVersionedChainRoot(ComponentTable table, long pk, long tsn, byte[] payload) =>
        CreateVersionedChainRoot(table, pk, tsn, payload, out _);

    private int CreateVersionedChainRoot(ComponentTable table, long pk, long tsn, byte[] payload, out int contentChunkIdOut)
    {
        var info = GetRecoveryInfo(table);

        var contentChunkId = table.ComponentSegment.AllocateChunk(false, _changeSet);
        contentChunkIdOut = contentChunkId;
        byte* contentBase = info.CompContentAccessor.GetChunkAddress(contentChunkId, true);
        // Value lives at offset ComponentOverhead (the read/write paths skip the overhead) — symmetric with the slot emit.
        payload.AsSpan().CopyTo(new Span<byte>(contentBase + info.ComponentOverhead, payload.Length));

        var compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, tsn, 0, contentChunkId, pk);
        var handle = ComponentRevisionManager.GetRevisionElement(ref info.CompRevTableAccessor, compRevChunkId, 0);
        handle.Commit(tsn); // element TSN already == tsn; this clears IsolationFlag → the revision is committed/visible

        // RB-01: recovery never trusts persisted secondary indexes. Apply writes ONLY primary data (content + chain); the secondary indexes are cleared at open
        // on the crash path and rebuilt wholesale from final HEAD data in Phase-5 (DatabaseEngine.RebuildSecondaryIndexes), so populating them here would
        // double-insert against that rebuild. contentBase is still used above (payload copy).
        return compRevChunkId;
    }

    private int CreateSingleVersionContent(ComponentTable table, byte[] payload)
    {
        var info = GetRecoveryInfo(table);
        var contentChunkId = table.ComponentSegment.AllocateChunk(false, _changeSet);
        var dst = info.CompContentAccessor.GetChunkAsSpan(contentChunkId, true);
        payload.AsSpan().CopyTo(dst[info.ComponentOverhead..]); // value lives at offset ComponentOverhead — symmetric with the slot emit
        return contentChunkId;
    }

    private ComponentInfo GetRecoveryInfo(ComponentTable table)
    {
        if (_infoByTable.TryGetValue(table, out var info))
        {
            return info;
        }

        info = new ComponentInfo
        {
            ComponentTable = table,
            ComponentOverhead = table.ComponentOverhead,
            SingleCache = new Dictionary<long, ComponentInfo.CompRevInfo>(),
            CompContentSegment = table.ComponentSegment,
            CompContentAccessor = table.ComponentSegment.CreateChunkAccessor(_changeSet),
        };

        if (table.StorageMode == StorageMode.Versioned)
        {
            info.CompRevTableSegment = table.CompRevTableSegment;
            info.CompRevTableAccessor = table.CompRevTableSegment.CreateChunkAccessor(_changeSet);
        }

        _infoByTable[table] = info;
        return info;
    }

    private void EnsureArchetype(ushort archId)
    {
        if (_hasAccessor && archId == _lastArchId)
        {
            return;
        }

        if (_hasAccessor)
        {
            _mapAccessor.CommitChanges();
            _mapAccessor.Dispose();
        }
        if (_hasClusterAccessor)
        {
            _clusterAccessor.CommitChanges();
            _clusterAccessor.Dispose();
            _hasClusterAccessor = false;
        }

        _metadata = _dbe.GetMetaByRouting(archId);
        _engineState = _dbe._stateByRouting[archId];
        _componentCount = _metadata.ComponentCount;
        _mapAccessor = _engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
        _hasAccessor = true;
        _lastArchId = archId;

        // Cluster-eligible archetypes reconstruct into the cluster SoA (ClusterSegment is the PersistentStore primary for SV/Versioned/mixed; a
        // pure-Transient cluster has no ClusterSegment and is never durable, so it stays on the flat no-op path).
        var clusterState = _engineState.ClusterState;
        if (_metadata.IsClusterEligible && clusterState?.ClusterSegment != null)
        {
            _clusterAccessor = clusterState.ClusterSegment.CreateChunkAccessor(_changeSet);
            _hasClusterAccessor = true;
        }
    }

    public void Dispose()
    {
        if (_hasAccessor)
        {
            _mapAccessor.CommitChanges();
            _mapAccessor.Dispose();
            _hasAccessor = false;
        }
        if (_hasClusterAccessor)
        {
            _clusterAccessor.CommitChanges();
            _clusterAccessor.Dispose();
            _hasClusterAccessor = false;
        }

        foreach (var info in _infoByTable.Values)
        {
            info.CompContentAccessor.CommitChanges();
            info.CompContentAccessor.Dispose();
            if (info.ComponentTable.StorageMode == StorageMode.Versioned)
            {
                info.CompRevTableAccessor.CommitChanges();
                info.CompRevTableAccessor.Dispose();
            }
        }

        _infoByTable.Clear();
    }
}
