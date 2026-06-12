using System;
using Typhon.Engine;

namespace Typhon.Engine.Internals;

// Applies committed WAL v2 records back into engine state during crash recovery, reusing the engine's own write primitives
// (the design's "one write path"). P1.2 increment 1 implements the lifecycle Spawn that makes the One True Crash Test green;
// Destroy / SetEnabledBits / Slot.Upsert / CollectionDelta follow in increment 2. Runs single-threaded under one epoch scope
// with a dedicated ChangeSet (so applied page mutations are tracked for the sealing checkpoint). See 03-recovery.md §3.

internal sealed unsafe class RecoveryApplier : IDisposable
{
    private readonly DatabaseEngine _dbe;
    private readonly ChangeSet _changeSet;
    private long _maxTsn;

    // Cache the current archetype's map accessor — recovery applies records in LSN order, usually clustered by archetype.
    private ushort _lastArchId = ushort.MaxValue;
    private bool _hasAccessor;
    private ArchetypeEngineState _engineState;
    private int _componentCount;
    private ChunkAccessor<PersistentStore> _mapAccessor;

    public RecoveryApplier(DatabaseEngine dbe)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        _dbe = dbe;
        _changeSet = new ChangeSet(dbe.MMF);
    }

    /// <summary>Highest TSN applied — recovery restores NextFreeTSN above this (RB-05).</summary>
    public long MaxTsn => _maxTsn;

    /// <summary>
    /// Applies a committed Spawn: inserts the entity into its archetype's EntityMap with its ORIGINAL EntityId + TSN +
    /// EnabledBits (the live <c>tx.Spawn</c> allocates fresh ids, so recovery uses the low-level insert directly — flat/legacy
    /// path; component locations stay 0/unclaimed until Slot records arrive). Makes <c>IsAlive</c> resolve true post-recovery.
    /// </summary>
    public void ApplySpawn(long entityIdRaw, ushort archetypeId, ushort enabledBits, long tsn)
    {
        if (tsn > _maxTsn)
        {
            _maxTsn = tsn;
        }

        EnsureArchetype(archetypeId);

        var key = EntityId.FromRaw(entityIdRaw).EntityKey;

        byte* recordPtr = stackalloc byte[EntityRecordAccessor.MaxRecordSize];
        EntityRecordAccessor.InitializeRecord(recordPtr, _componentCount); // zeroes header (DiedTSN=0=alive) + locations
        ref var header = ref EntityRecordAccessor.GetHeader(recordPtr);
        header.BornTSN = tsn;
        header.EnabledBits = enabledBits;

        _engineState.EntityMap.InsertNew(key, recordPtr, ref _mapAccessor, _changeSet);
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
        _componentCount = ArchetypeRegistry.GetMetadata(archId).ComponentCount;
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
    }
}
