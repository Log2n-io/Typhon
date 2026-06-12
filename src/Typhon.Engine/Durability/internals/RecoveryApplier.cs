using System;
using System.Collections.Generic;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

// Applies committed WAL v2 records back into engine state during crash recovery, reusing the engine's own write primitives
// (the design's "one write path"). P1.2: rebuilds a committed spawned entity — the EntityRecord plus, per spawn-init Slot, a
// committed revision-chain root holding the component value — by BUILDING the full record then InsertNew once, mirroring the
// live FinalizeSpawns (approach B; the live engine has no in-place location update — a Versioned location is written once at
// spawn and stays fixed, revisions append within the chain). Flat (non-cluster) path. Destroy / SetEnabledBits / component-
// update Slots / cluster path / collections follow in later increments. Runs single-threaded under one epoch scope with a
// dedicated ChangeSet (so applied page mutations are captured by the sealing checkpoint). See 03-recovery.md §3.

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

    // Allocates a content chunk holding the payload and a committed single-element revision chain pointing at it — exactly the
    // spawn→commit end-state the live ComponentRevisionManager produces (AllocCompRevStorage creates the isolated element, then
    // the live ElementRevisionHandle.Commit clears the isolation flag). Returns the chain-root chunk id (the slot's location).
    private int CreateVersionedChainRoot(ComponentTable table, long pk, long tsn, byte[] payload)
    {
        var info = GetRecoveryInfo(table);

        var contentChunkId = table.ComponentSegment.AllocateChunk(false, _changeSet);
        var dst = info.CompContentAccessor.GetChunkAsSpan(contentChunkId, true);
        payload.AsSpan().CopyTo(dst); // the emitter captured chunk[0..storageSize] from offset 0 → write it back at offset 0

        var compRevChunkId = ComponentRevisionManager.AllocCompRevStorage(info, tsn, 0, contentChunkId, pk);
        var handle = ComponentRevisionManager.GetRevisionElement(ref info.CompRevTableAccessor, compRevChunkId, 0);
        handle.Commit(tsn); // element TSN already == tsn; this clears IsolationFlag → the revision is committed/visible
        return compRevChunkId;
    }

    private int CreateSingleVersionContent(ComponentTable table, byte[] payload)
    {
        var info = GetRecoveryInfo(table);
        var contentChunkId = table.ComponentSegment.AllocateChunk(false, _changeSet);
        var dst = info.CompContentAccessor.GetChunkAsSpan(contentChunkId, true);
        payload.AsSpan().CopyTo(dst);
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

        _engineState = _dbe._archetypeStates[archId];
        _metadata = ArchetypeRegistry.GetMetadata(archId);
        _componentCount = _metadata.ComponentCount;
        _mapAccessor = _engineState.EntityMap.Segment.CreateChunkAccessor(_changeSet);
        _hasAccessor = true;
        _lastArchId = archId;
    }

    public void Dispose()
    {
        if (_hasAccessor)
        {
            _mapAccessor.CommitChanges();
            _mapAccessor.Dispose();
            _hasAccessor = false;
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
