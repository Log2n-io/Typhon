using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

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

        /// <summary>
        /// Per-(entity, slot) records expanded out of columnar FenceBlock records (#559). Surfaced so a test can tell that a recovered value actually
        /// travelled the fence-block path rather than arriving as a per-entity Slot record — the two are indistinguishable downstream by design, which is
        /// exactly what makes "the FenceBlock path feeds recovery correctly" (#569) otherwise unfalsifiable.
        /// </summary>
        public int FenceBlockRecordsExpanded;

        /// <summary>
        /// True when the scan stopped at a corruption boundary (LOG-03 / REC-01) rather than running out of segments.
        /// </summary>
        /// <remarks>
        /// Diagnostic only — the stop itself is unconditional. Surfaced so an operator can tell "the log ended" from "the log was
        /// cut short", which are the same shape in every other field of this struct.
        /// </remarks>
        public bool StoppedAtCorruption;
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
        public ushort SlotIndex; // Slot record: per-archetype component slot (LOG-06), resolved via EntityId's routing id
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

        // slot → latest committed value. A component can be written more than once in the window (spawn-init then a post-spawn update); records arrive in LSN
        // order, so overwriting collapses each component's history to its final value (and avoids allocating an orphaned chain per superseded revision).
        // Keyed by per-archetype slot (the wire identity).
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

                        // A columnar fence block (#559) carries one cluster's entity-key column plus each durable component's
                        // column. Expand it here into the same per-(entity, slot) Rec shape the rest of the pipeline consumes,
                        // so recovery semantics are identical to the per-entity Slot records this format replaced. Only the
                        // entities the emitting tick marked dirty are expanded — clean entities inside the emitted range rode
                        // along for the bulk copy and carry no new information.
                        if (view.Kind == RecordKind.FenceBlock)
                        {
                            if (!RecordCodec.TryReadFenceBlock(view.Payload, out var block))
                            {
                                continue;
                            }

                            for (var i = 0; i < block.SlotSpan; i++)
                            {
                                if (!block.IsDirtyAt(i))
                                {
                                    continue;
                                }

                                var entityKey = block.EntityKeyAt(i);
                                if (entityKey == 0)
                                {
                                    continue;   // unoccupied cluster slot — nothing to restore
                                }

                                for (var c = 0; c < block.ColumnCount; c++)
                                {
                                    result.FenceBlockRecordsExpanded++;
                                    records.Add(new Rec
                                    {
                                        Lsn = view.Lsn, Tsn = view.Tsn, Kind = RecordKind.Slot, Op = (byte)SlotOp.Upsert,
                                        EntityId = entityKey, ArchetypeId = block.ArchetypeId,
                                        SlotIndex = block.SlotIndexOf(c), EnabledBits = 0, IsFence = view.IsFence,
                                        Payload = block.ValueAt(c, i).ToArray(),
                                    });
                                }
                            }

                            continue;
                        }

                        records.Add(new Rec
                        {
                            Lsn = view.Lsn, Tsn = view.Tsn, Kind = view.Kind, Op = view.Op,
                            EntityId = view.EntityId, ArchetypeId = view.ArchetypeId,
                            SlotIndex = view.SlotIndex, EnabledBits = view.EnabledBits, IsFence = view.IsFence,
                            Payload = view.Kind == RecordKind.Slot && view.Payload.Length > 0 ? view.Payload.ToArray() : null,
                        });
                    }
                }

                // LOG-03 / REC-01: stop at the FIRST corruption boundary, and stop for good. WalSegmentReader raises WasTruncated for a mid-log CRC break in a
                // sealed segment exactly as it does for a torn tail on the last one, and the rule is deliberately unconditional about both — records past the
                // boundary have no CRC-chain guarantee, so they may be partially flushed, from a transaction that never committed, or stale bytes left in a
                // recycled segment. Applying them is the atomicity violation REC-01 exists to prevent.
                //
                // This must be tested HERE, per segment: OpenSegment resets WasTruncated (WalSegmentReader), so the next iteration erases the evidence before
                // anything downstream could act on it. v1 has always done this (WalRecovery); v2 never read the flag at all, which meant the two paths computed
                // disagreeing frontiers at the same open — v1 stopped at the boundary while v2 kept applying past it (#587).
                if (reader.WasTruncated)
                {
                    result.StoppedAtCorruption = true;
                    break;
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
                    GetAgg(entities, r.EntityId).Slots[r.SlotIndex] = new RecoveryApplier.SlotData
                    {
                        SlotIndex = r.SlotIndex,
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
                // Base-entity Destroy wins over everything (net not-alive): applying values to an entity this window kills would write into storage the
                // tombstone makes unreachable.
                if (agg.HasDestroy)
                {
                    applier.ApplyDestroyToExisting(entityIdRaw, agg.DestroyTsn);
                    result.RecordsApplied++;
                    continue;
                }

                if (agg.HasEnabledChange)
                {
                    applier.ApplySetEnabledBitsToExisting(entityIdRaw, agg.EnabledBits);
                    result.RecordsApplied++;
                }

                // #569: the aggregated Slot payloads used to be DROPPED here, with a comment calling the base-entity value update "a later increment". The
                // aggregation at :53 has always produced exactly the right value — per (entity, slot), latest committed wins, which is the CM-03 last-writer
                // rule the Commit discipline needs against an interleaved TickFence write — so the only thing missing was somewhere to put it. Until this
                // landed, every entity that existed at the last checkpoint lost every value update in the window: for a steady-state workload (spawn once,
                // mutate forever) that is effectively all of them, and the ≤1-tick promise in ADR-057 was really the checkpoint interval.
                if (agg.Slots.Count > 0)
                {
                    applier.ApplySlotToExisting(entityIdRaw, agg.Slots.Values);
                    result.RecordsApplied += agg.Slots.Count;
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

        // #697, the entity-key half of the same watermark discipline: NextEntityKey must be at least the highest key this window applied, or the first
        // post-recovery Spawn re-issues a live recovered entity's id and silently overwrites it. The rebuild paths raise this counter from the persisted base,
        // but they run BEFORE the window is applied — so with no checkpoint at all the counter stayed 0 while recovery inserted keys 1..N. NextEntityKey holds
        // the LAST issued key (Transaction.ECS.cs increments before use), so the max applied key is the correct floor, not max+1.
        // NB: _stateByRouting, NOT _archetypeStates. An EntityId carries the per-DB ROUTING id; _archetypeStates is indexed by the per-process CATALOG id.
        // The two spaces coincide often enough (single archetype, fresh process) that mixing them up silently no-ops instead of throwing.
        foreach (var (routingId, maxKey) in applier.MaxEntityKeyByArchetype)
        {
            var state = routingId < dbe._stateByRouting.Length ? dbe._stateByRouting[routingId] : null;
            if (state != null && maxKey > Interlocked.Read(ref state.NextEntityKey))
            {
                Interlocked.Exchange(ref state.NextEntityKey, maxKey);
            }
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
