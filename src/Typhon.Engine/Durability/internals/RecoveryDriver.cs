using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Typhon.Engine.Internals;

// WAL v2 crash recovery (replaces WalRecovery's never-wired replay). Scans the retained WAL segments, determines commit fate
// from TxCommit markers (LOG-04), and applies committed records in strict LSN order through RecoveryApplier. Runs at open AFTER
// archetypes + EntityMap + page cache are online. P1.2 increment 1: scan/fate + Spawn apply → One True Crash Test green.
// See claude/design/Durability/MinimalWal/03-recovery.md. D4: recovery time is not a design driver — straightforward List/HashSet.

internal sealed class RecoveryDriver
{
    internal struct Result
    {
        public int SegmentsScanned;
        public int RecordsScanned;
        public int RecordsApplied;
        public int TxCommitted;
        public long MaxTsn;
        public long MaxLsn; // highest LSN seen in the recovery window — the frontier the post-recovery seal consolidates to
    }

    // Materialized record. Copied during the scan because the reader's body span is invalidated by the next TryReadNext;
    // Slot payloads are copied too (D4: recovery memory/time is not a design driver — a straight copy is fine).
    private sealed class Rec
    {
        public long Lsn;
        public long Tsn;
        public RecordKind Kind;
        public byte Op;
        public long EntityId;
        public ushort ArchetypeId;
        public ushort ComponentTypeId;
        public ushort EnabledBits;
        public bool IsFence;
        public byte[] Payload;
    }

    // Accumulates one committed entity's records across the window (records of a transaction are NOT grouped by entity on the
    // wire — LOG-07 emits all Spawns, then all Slots — so we key by EntityId and assemble per entity before the single insert).
    private sealed class EntityAgg
    {
        public bool HasSpawn;
        public bool HasDestroy;
        public bool HasEnabledChange;
        public ushort ArchetypeId;
        public ushort EnabledBits;
        public long Tsn;        // spawn (born) TSN when HasSpawn
        public long DestroyTsn; // the destroying transaction's TSN — DiedTSN for a base-entity tombstone

        // ComponentTypeId → latest committed value. A component can be written more than once in the window (spawn-init then a
        // post-spawn update); records arrive in LSN order, so overwriting collapses each component's history to its final value
        // (and avoids allocating an orphaned chain per superseded revision).
        public readonly Dictionary<ushort, RecoveryApplier.SlotData> Slots = [];
    }

    /// <summary>
    /// Scans the WAL segments in <paramref name="walDir"/>, applies every committed record with LSN &gt; <paramref name="checkpointLsn"/>
    /// (the recovery window — records at/below it are already in the data file), and restores NextFreeTSN (RB-05).
    /// </summary>
    internal Result Run(IWalFileIO walIO, string walDir, DatabaseEngine dbe, long checkpointLsn)
    {
        var result = default(Result);
        var records = new List<Rec>();
        var committed = new HashSet<long>();

        var paths = Directory.GetFiles(walDir, "*.wal").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        using (var reader = new WalSegmentReader(walIO))
        {
            foreach (var path in paths)
            {
                if (!reader.OpenSegment(path))
                {
                    continue;
                }

                result.SegmentsScanned++;

                // Phase 1+2: scan CRC-valid chunk bodies → records; collect committed-tx TSNs from TxCommit markers (LOG-04).
                while (reader.TryReadNext(out var ch, out var body))
                {
                    // Only RecordBatch chunks carry v2 records. Other chunk types (TickFence / Bulk*, or a stale FullPageImage in an old segment — FPI is
                    // retired, increment D) are orthogonal — skip them so they aren't misparsed as records.
                    if (ch.ChunkType != (ushort)WalChunkType.Transaction)
                    {
                        continue;
                    }

                    var offset = 0;
                    while (RecordCodec.TryReadRecord(body, offset, out var consumed, out var view))
                    {
                        offset += consumed;
                        result.RecordsScanned++;

                        if (view.IsUnknownKind || view.Lsn <= checkpointLsn)
                        {
                            continue;
                        }

                        if (view.Lsn > result.MaxLsn)
                        {
                            result.MaxLsn = view.Lsn;
                        }

                        if (view.IsTxCommit)
                        {
                            committed.Add(view.Tsn);
                        }

                        records.Add(new Rec
                        {
                            Lsn = view.Lsn, Tsn = view.Tsn, Kind = view.Kind, Op = view.Op,
                            EntityId = view.EntityId, ArchetypeId = view.ArchetypeId,
                            ComponentTypeId = view.ComponentTypeId, EnabledBits = view.EnabledBits, IsFence = view.IsFence,
                            Payload = view.Kind == RecordKind.Slot && view.Payload.Length > 0 ? view.Payload.ToArray() : null,
                        });
                    }
                }
            }
        }

        // Phase 3: assemble each committed entity from its records, then build-and-insert (approach B). Records are processed in
        // ascending LSN order (AP-11) but assembled into per-entity aggregates keyed by EntityId — a transaction emits all its
        // Spawns before all its Slots (LOG-07), so an entity's Spawn and Slots are not contiguous on the wire.
        records.Sort(static (a, b) => a.Lsn.CompareTo(b.Lsn));

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        using var applier = new RecoveryApplier(dbe);

        var entities = new Dictionary<long, EntityAgg>();
        foreach (var r in records)
        {
            if (!r.IsFence && !committed.Contains(r.Tsn))
            {
                continue;
            }

            applier.Track(r.Tsn); // RB-05 watermark over every applicable record

            switch (r.Kind)
            {
                case RecordKind.Lifecycle when r.Op == (byte)LifecycleOp.Spawn:
                    var spawnAgg = GetAgg(entities, r.EntityId);
                    spawnAgg.HasSpawn = true;
                    spawnAgg.ArchetypeId = r.ArchetypeId;
                    spawnAgg.EnabledBits = r.EnabledBits;
                    spawnAgg.Tsn = r.Tsn;
                    break;

                case RecordKind.Slot when r.Op == (byte)SlotOp.Upsert:
                    GetAgg(entities, r.EntityId).Slots[r.ComponentTypeId] = new RecoveryApplier.SlotData
                    {
                        ComponentTypeId = r.ComponentTypeId,
                        Payload = r.Payload ?? [],
                        Tsn = r.Tsn,
                    };
                    break;

                case RecordKind.Lifecycle when r.Op == (byte)LifecycleOp.Destroy:
                    var destroyAgg = GetAgg(entities, r.EntityId);
                    destroyAgg.HasDestroy = true;
                    destroyAgg.DestroyTsn = r.Tsn;
                    break;

                case RecordKind.Lifecycle when r.Op == (byte)LifecycleOp.SetEnabledBits:
                    // Absolute set; records arrive in LSN order so the last write (incl. the Spawn's own bits) wins.
                    var enableAgg = GetAgg(entities, r.EntityId);
                    enableAgg.EnabledBits = r.EnabledBits;
                    enableAgg.HasEnabledChange = true;
                    break;

                // CollectionDelta / BulkManifest are applied in later increments (TSN still tracked above).
            }
        }

        foreach (var (entityIdRaw, agg) in entities)
        {
            // No Spawn in the window → the record targets a pre-existing (checkpointed) entity already loaded into the EntityMap.
            if (!agg.HasSpawn)
            {
                // Base-entity Destroy wins over any enabled change (net not-alive); otherwise apply a base-entity enabled-bits
                // change in place. A base-entity value update (AddCompRev to the existing chain) is a later increment.
                if (agg.HasDestroy)
                {
                    applier.ApplyDestroyToExisting(entityIdRaw, agg.DestroyTsn);
                    result.RecordsApplied++;
                }
                else if (agg.HasEnabledChange)
                {
                    applier.ApplySetEnabledBitsToExisting(entityIdRaw, agg.EnabledBits);
                    result.RecordsApplied++;
                }

                continue;
            }

            // Spawned AND destroyed within the window → net not-alive: don't insert (and don't create its revision chains), exactly
            // as the live FinalizeSpawns skips a spawn+destroy entity. Post-recovery reads happen at a TSN past the window, so the
            // historical alive-then-dead transition is not observable — absence == dead for IsAlive.
            if (agg.HasDestroy)
            {
                continue;
            }

            applier.ApplySpawnedEntity(entityIdRaw, agg.ArchetypeId, agg.EnabledBits, agg.Tsn, agg.Slots.Values);
            result.RecordsApplied++;
        }

        result.TxCommitted = committed.Count;
        result.MaxTsn = applier.MaxTsn;

        // RB-05: NextFreeTSN must exceed every applied TSN, or post-recovery reads would not see the recovered entities.
        if (applier.MaxTsn >= dbe.TransactionChain.NextFreeId)
        {
            dbe.TransactionChain.SetNextFreeId(applier.MaxTsn + 1);
        }

        return result;
    }

    private static EntityAgg GetAgg(Dictionary<long, EntityAgg> entities, long entityId)
    {
        if (!entities.TryGetValue(entityId, out var agg))
        {
            agg = new EntityAgg();
            entities[entityId] = agg;
        }

        return agg;
    }
}
