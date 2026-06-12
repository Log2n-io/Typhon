using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Typhon.Engine;

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
    }

    // Materialized record (scalar fields only — payloads are not needed for the lifecycle apply in increment 1). Copied during
    // the scan because the reader's body span is invalidated by the next TryReadNext.
    private sealed class Rec
    {
        public long Lsn;
        public long Tsn;
        public RecordKind Kind;
        public byte Op;
        public long EntityId;
        public ushort ArchetypeId;
        public ushort EnabledBits;
        public bool IsFence;
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
                    // Only RecordBatch chunks carry v2 records. FPI / other v1 chunk types (still emitted until P1.3 deletes FPI)
                    // are orthogonal — skip them so they aren't misparsed as records.
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

                        if (view.IsTxCommit)
                        {
                            committed.Add(view.Tsn);
                        }

                        records.Add(new Rec
                        {
                            Lsn = view.Lsn, Tsn = view.Tsn, Kind = view.Kind, Op = view.Op,
                            EntityId = view.EntityId, ArchetypeId = view.ArchetypeId,
                            EnabledBits = view.EnabledBits, IsFence = view.IsFence,
                        });
                    }
                }
            }
        }

        // Phase 3: apply committed (or fence) records in strict ascending LSN order (AP-11).
        records.Sort(static (a, b) => a.Lsn.CompareTo(b.Lsn));

        using var guard = EpochGuard.Enter(dbe.EpochManager);
        using var applier = new RecoveryApplier(dbe);
        foreach (var r in records)
        {
            if (!r.IsFence && !committed.Contains(r.Tsn))
            {
                continue;
            }

            // Increment 1: entity lifecycle Spawn. Destroy / SetEnabledBits / Slot.Upsert / CollectionDelta land in increment 2.
            if (r.Kind == RecordKind.Lifecycle && r.Op == (byte)LifecycleOp.Spawn)
            {
                applier.ApplySpawn(r.EntityId, r.ArchetypeId, r.EnabledBits, r.Tsn);
                result.RecordsApplied++;
            }
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
}
