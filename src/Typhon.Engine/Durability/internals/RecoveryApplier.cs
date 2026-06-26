using System;
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
        public ushort ComponentTypeId;
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

    public RecoveryApplier(DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        _dbe = dbe;
        _changeSet = new ChangeSet(dbe.MMF);
    }

    /// <summary>Highest TSN applied — recovery restores NextFreeTSN above this (RB-05).</summary>
    public long MaxTsn => _maxTsn;

    /// <summary>Records a committed record's TSN toward the RB-05 watermark (called for every applicable record, applied or not).</summary>
    public void Track(long tsn)
    {
        if (tsn > _maxTsn)
        {
            _maxTsn = tsn;
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
        EnsureArchetype(archetypeId);

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
                // The caller has already collapsed a component's history to its latest committed value (last write wins), so each
                // slot here is the final value and carries the TSN of the transaction that committed it (which may be later than
                // the spawn's — a post-spawn Write). The chain element records that TSN; BornTSN stays the spawn's.
                if (!_metadata.TryGetSlot(slot.ComponentTypeId, out var slotIndex))
                {
                    continue; // component is not part of this archetype — tolerate (malformed/foreign record)
                }

                var table = _engineState.SlotToComponentTable[slotIndex];
                locations[slotIndex] = table.StorageMode switch
                {
                    StorageMode.Versioned => CreateVersionedChainRoot(table, entityIdRaw, slot.Tsn, slot.Payload),
                    StorageMode.SingleVersion => CreateSingleVersionContent(table, slot.Payload),
                    _ => 0, // Transient values are never logged
                };
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
        ClusterEntityRecordAccessor.SetClusterChunkId(recordPtr, clusterChunkId);
        ClusterEntityRecordAccessor.SetSlotIndex(recordPtr, (byte)slotIdx);

        if (slots != null)
        {
            foreach (var slot in slots)
            {
                if (!_metadata.TryGetSlot(slot.ComponentTypeId, out var slotIndex))
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
                        var chainRoot = CreateVersionedChainRoot(table, entityIdRaw, slot.Tsn, slot.Payload);
                        ClusterEntityRecordAccessor.SetCompRevFirstChunkId(recordPtr, vi, chainRoot);
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
        _engineState.EntityMap.Upsert(key, readBuf, ref _mapAccessor, _changeSet);
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
    private int CreateVersionedChainRoot(ComponentTable table, long pk, long tsn, byte[] payload)
    {
        var info = GetRecoveryInfo(table);

        var contentChunkId = table.ComponentSegment.AllocateChunk(false, _changeSet);
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

        _engineState = _dbe._archetypeStates[archId];
        _metadata = ArchetypeRegistry.GetMetadata(archId);
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
