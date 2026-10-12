using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Typhon.Schema.Definition;

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

        /// <summary>The highest commit stamp on any TxCommit record scanned (VIS-03), at or below the checkpoint too: the TSN counter resumes above it (RB-05).</summary>
        public long MaxCommitStamp;

        /// <summary>
        /// Identity TSN → commit stamp, for every transaction the window commits (VIS-03; design 05 §3.7). A batch that drew no stamp maps to its TSN.
        /// Commit fate stays keyed on the identity (LOG-04/08).
        /// </summary>
        public Dictionary<long, long> CommitStamps;

        public long MaxLsn; // highest LSN seen in the recovery window — the frontier the post-recovery seal consolidates to

        /// <summary>
        /// Per-(entity, slot) records expanded out of columnar FenceBlock records (#559). Surfaced so a test can tell that a recovered value actually
        /// travelled the fence-block path rather than arriving as a per-entity Slot record — the two are indistinguishable downstream by design, which is
        /// exactly what makes "the FenceBlock path feeds recovery correctly" (#569) otherwise unfalsifiable.
        /// </summary>
        public int FenceBlockRecordsExpanded;

        /// <summary>
        /// Collection fields whose folded content was written back (#389). Surfaced for the same reason as
        /// <see cref="FenceBlockRecordsExpanded"/>: a recovered collection is indistinguishable from one that was never touched, so without a counter
        /// "the fold ran" is unfalsifiable — which is exactly how this defect stayed invisible while a green oracle reported no differences.
        /// </summary>
        public int CollectionFoldsFlushed;

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
        public ushort FieldId;   // CollectionDelta: which collection field of that slot's component (02 §3.3)
        public ushort EnabledBits;
        public int Index;        // CollectionDelta: element index (RemoveAt/UpdateAt) or new count (SetCount)
        public bool IsFence;
        public bool IsCommitDiscipline;
        public byte[] Payload;
    }

    /// <summary>
    /// One collection's replayed state, folded across the window per (EntityId, slot, FieldId) — 03-recovery.md §5.
    /// </summary>
    /// <remarks>
    /// <c>EnsureBase</c> is absent, and that is not an omission. The design has the fold read a collection's pre-window content whenever an op needs it, but
    /// under Option B the emitter always logs the FULL content behind a <c>Clear</c>, so <c>baseDiscarded</c> is set before any element op is folded and the
    /// base is never consulted. That matters because <c>EnsureBase</c> cannot be implemented as specified: once LOG-06 zeroes the handle there is no route
    /// from a record back to the buffer it describes — a collection is reachable only through its row's inline <c>_bufferId</c>, there is no reverse index,
    /// and the buffer root carries no owner back-pointer. The non-Clear ops are still folded because the record format defines them and the fold is where
    /// they are cheap; nothing emits them today.
    /// </remarks>
    internal sealed class CollectionFold
    {
        public readonly List<byte[]> Elements = [];

        /// <summary>True once a <see cref="CollectionOp.Clear"/> has been folded — under Option B that is always, before any element op.</summary>
        public bool BaseDiscarded;

        /// <summary>
        /// The commit stamp of the deltas folded, for a Versioned component's collection: only the highest stamp's are kept (VIS-05), as for the slot's
        /// value — every Versioned commit logs the full content behind a Clear, so a higher stamp starts the fold over and a lower one is ignored.
        /// </summary>
        public long Stamp;

        /// <summary>
        /// Folds one delta. <paramref name="where"/> is a caller-supplied location string used only in failure messages.
        /// </summary>
        /// <remarks>
        /// Takes the op's fields rather than the driver's record type so the fold can be driven directly by a test. The out-of-range branches are otherwise
        /// unreachable — nothing emits <c>RemoveAt</c> / <c>UpdateAt</c> today — and an unreachable loud-failure path that has never been seen to fire is
        /// indistinguishable from one that silently clamps.
        /// </remarks>
        public void Apply(CollectionOp op, int index, byte[] element, string where)
        {
            switch (op)
            {
                case CollectionOp.Clear:
                    BaseDiscarded = true;
                    Elements.Clear();
                    break;

                case CollectionOp.Append:
                    Elements.Add(element ?? []);
                    break;

                case CollectionOp.RemoveAt:
                    RequireInRange(index, Elements.Count, "RemoveAt", where);
                    Elements.RemoveAt(index);
                    break;

                case CollectionOp.UpdateAt:
                    RequireInRange(index, Elements.Count, "UpdateAt", where);
                    Elements[index] = element ?? [];
                    break;

                case CollectionOp.SetCount:
                    if (index < 0)
                    {
                        ThrowHelper.ThrowInvalidOp($"Recovery fold: SetCount with a negative count {index} at {where}.");
                    }

                    while (Elements.Count > index)
                    {
                        Elements.RemoveAt(Elements.Count - 1);
                    }

                    while (Elements.Count < index)
                    {
                        Elements.Add([]);   // extend-with-zero; the applier widens each to the segment's element size
                    }

                    break;

                default:
                    ThrowHelper.ThrowInvalidOp($"Recovery fold: unknown collection op {(byte)op} at {where}.");
                    break;
            }
        }

        // 03-recovery.md §9.e: an out-of-range index is a structural impossibility — the producer and this reader disagree about the collection's shape.
        // Never best-effort: clamping would silently write a collection that never existed, which is the failure mode this whole issue is about.
        private static void RequireInRange(int index, int count, string op, string where)
        {
            if ((uint)index >= (uint)count)
            {
                ThrowHelper.ThrowInvalidOp(
                    $"Recovery fold: {op} index {index} is out of range for a {count}-element collection at {where}. This is producer corruption, not crash "
                    + "damage — recovery fails loudly rather than guessing.");
            }
        }
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

        /// <summary>Folded collection content per (slot, FieldId), flushed after this entity's Slot apply (#389).</summary>
        public Dictionary<(ushort Slot, ushort FieldId), CollectionFold> Collections;

        public CollectionFold FoldFor(ushort slot, ushort fieldId)
        {
            Collections ??= [];
            if (!Collections.TryGetValue((slot, fieldId), out var fold))
            {
                Collections[(slot, fieldId)] = fold = new CollectionFold();
            }

            return fold;
        }
    }

    /// <summary>
    /// Scans the WAL segments in <paramref name="walDir"/>, applies every committed record with LSN &gt; <paramref name="checkpointLsn"/>
    /// (the recovery window — records at/below it are already in the data file), and restores NextFreeTSN (RB-05).
    /// </summary>
    internal Result Run(IWalFileIO walIO, string walDir, DatabaseEngine dbe, long checkpointLsn)
    {
        var result = default(Result);
        var records = new List<Rec>();
        var committed = new Dictionary<long, long>();

        // #688: through the backend, so an injected WAL IO is discoverable. Same reason as WalRecovery.DiscoverSegments.
        var paths = walIO.EnumerateSegmentPaths(walDir).OrderBy(p => p, StringComparer.Ordinal).ToArray();
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

                        // RB-05 over every stamp seen, consolidated or not: a stamp is drawn from the TSN counter, which must resume above it.
                        if (view.IsTxCommit && view.CommitStamp > result.MaxCommitStamp)
                        {
                            result.MaxCommitStamp = view.CommitStamp;
                        }

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
                            committed[view.Tsn] = view.CommitStamp != 0 ? view.CommitStamp : view.Tsn;
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
                            SlotIndex = view.SlotIndex, FieldId = view.FieldId, EnabledBits = view.EnabledBits,
                            Index = view.Index, IsFence = view.IsFence, IsCommitDiscipline = view.IsCommittedDiscipline,
                            // CollectionDelta element bytes are copied too (#389). They used to be discarded here, one line before the switch that was
                            // documented as deferring the apply — so a CollectionDelta was gutted at SCAN, and adding an apply case alone could never have
                            // worked.
                            Payload = (view.Kind == RecordKind.Slot || view.Kind == RecordKind.CollectionDelta) && view.Payload.Length > 0
                                ? view.Payload.ToArray()
                                : null,
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
            if (!r.IsFence)
            {
                if (!committed.TryGetValue(r.Tsn, out var stamp))
                {
                    continue;
                }

                // A transaction's effects replay with its commit stamp (VIS-03, design 05 §3.7): the stamp is what the live engine published — revisions,
                // born, died — and the version order (VIS-05). Fate stayed keyed on the identity above. Fence records keep their fence-cycle TSN.
                r.Tsn = stamp;
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
                    var slotAgg = GetAgg(entities, r.EntityId);

                    // A Versioned value's newest is the highest stamp (VIS-05), not the highest LSN: the stamp is drawn after the claim, so two commits can
                    // log in one order and stamp in the other. Other components (fence records, SingleVersion under the Commit discipline) keep CM-03's LSN
                    // order. Versioned by the slot's table, not by the record's discipline flag: a Commit-discipline batch flags its Versioned records too.
                    // The base HEAD's stamp is weighed by the scrub (the chain) and by the applier (the cluster HEAD copy).
                    if (!r.IsFence && IsVersionedSlot(dbe, r.EntityId, r.SlotIndex) && slotAgg.Slots.TryGetValue(r.SlotIndex, out var held) && held.Tsn > r.Tsn)
                    {
                        break;
                    }

                    slotAgg.Slots[r.SlotIndex] = new RecoveryApplier.SlotData
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

                case RecordKind.CollectionDelta:
                    // Folded per (EntityId, slot, FieldId) in LSN order, flushed after the entity's Slot apply (03-recovery.md §5, #389). A Versioned
                    // component's keeps only the highest stamp's deltas, as its value does: the collection must be the one of the revision that wins.
                    var fold = GetAgg(entities, r.EntityId).FoldFor(r.SlotIndex, r.FieldId);
                    if (!r.IsFence && IsVersionedSlot(dbe, r.EntityId, r.SlotIndex))
                    {
                        if (r.Tsn < fold.Stamp)
                        {
                            break;
                        }

                        if (r.Tsn > fold.Stamp)
                        {
                            fold.Elements.Clear();
                            fold.BaseDiscarded = false;
                            fold.Stamp = r.Tsn;
                        }
                    }

                    fold.Apply((CollectionOp)r.Op, r.Index, r.Payload, $"LSN {r.Lsn} (entity 0x{r.EntityId:X}, slot {r.SlotIndex}, field {r.FieldId})");
                    break;

                // BulkManifest is orphan-detection only and is applied in a later increment (TSN still tracked above).
            }
        }

        var sinceBatchPoint = 0;
        foreach (var (entityIdRaw, agg) in entities)
        {
            // Between entities, every RecoveryBatchSize of them: the replay's pages written so far saved and unpinned, so a window larger than the cache
            // replays (#1183, CK-08).
            if (++sinceBatchPoint > DatabaseEngine.RecoveryBatchSize)
            {
                applier.BatchPoint();
                sinceBatchPoint = 1;
            }

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

                result.CollectionFoldsFlushed += FlushCollections(applier, entityIdRaw, agg);
                continue;
            }

            // Spawned AND destroyed within the window → net not-alive: don't insert (and don't create its revision chains), exactly
            // as the live FinalizeSpawns skips a spawn+destroy entity. Post-recovery reads happen at a TSN past the window, so the
            // historical alive-then-dead transition is not observable — absence == dead for IsAlive.
            //
            // Not "nothing to do": the data file may hold the entity — a checkpoint that copied its cluster page between the spawn and the destroy — and
            // the crash path's map, rebuilt from the clusters, then has it alive. The destroy goes through as for a base entity; it is a no-op when the
            // entity is absent (#1251).
            if (agg.HasDestroy)
            {
                applier.ApplyDestroyToExisting(entityIdRaw, agg.DestroyTsn);
                continue;
            }

            applier.ApplySpawnedEntity(entityIdRaw, agg.ArchetypeId, agg.EnabledBits, agg.Tsn, agg.Slots.Values);
            result.RecordsApplied++;
            result.CollectionFoldsFlushed += FlushCollections(applier, entityIdRaw, agg);
        }

        applier.Complete();
        result.TxCommitted = committed.Count;
        result.CommitStamps = dbe.RetainRecoveryCommitStampsForTest ? committed : null;
        result.MaxTsn = applier.MaxTsn;

        // RB-05: NextFreeTSN must exceed every applied TSN, or post-recovery reads would not see the recovered entities — and every commit stamp, drawn
        // from the same counter (VIS-03): a TSN at or below one would share a number with a commit point.
        var floor = Math.Max(applier.MaxTsn, result.MaxCommitStamp);
        if (floor >= dbe.TransactionChain.NextFreeId)
        {
            dbe.TransactionChain.SetNextFreeId(floor + 1);
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

    /// <summary>
    /// Flushes one entity's folded collections, AFTER its Slot apply. Returns the number of collection fields written.
    /// </summary>
    /// <remarks>
    /// The ordering is load-bearing and the design never states it. A Slot apply overwrites the entire component value, so a flush that ran first would be
    /// clobbered; and since LOG-06 zeroes the handle in every Slot payload, the apply leaves the row pointing at nothing, so the flush is also what gives the
    /// collection back its buffer. Both call sites are immediately after their apply for that reason — hence one helper rather than two inline calls that
    /// could drift apart.
    /// </remarks>
    private static int FlushCollections(RecoveryApplier applier, long entityIdRaw, EntityAgg agg)
    {
        if (agg.Collections == null || agg.Collections.Count == 0)
        {
            return 0;
        }

        var folded = new Dictionary<(ushort Slot, ushort FieldId), List<byte[]>>(agg.Collections.Count);
        foreach (var (key, fold) in agg.Collections)
        {
            folded[key] = fold.Elements;
        }

        applier.ApplyCollectionFolds(entityIdRaw, folded);
        return folded.Count;
    }

    /// <summary>Whether <paramref name="slotIndex"/> of <paramref name="entityIdRaw"/>'s archetype is a Versioned component, by its table.</summary>
    private static bool IsVersionedSlot(DatabaseEngine dbe, long entityIdRaw, ushort slotIndex)
    {
        var routing = EntityId.FromRaw(entityIdRaw).ArchetypeId;
        var states = dbe._stateByRouting;
        if (routing >= states.Length || states[routing] is not { } state || slotIndex >= state.SlotToComponentTable.Length)
        {
            return false;
        }

        return state.SlotToComponentTable[slotIndex]?.StorageMode == StorageMode.Versioned;
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
