using System;
using System.Collections.Generic;
using System.Linq;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Reads the records a live engine actually emitted, straight out of the on-disk WAL segments.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of what <c>rules/durability.md</c> says about LOG-06: the rule was retired to
/// <c>verified: NOT COVERED</c> on 2026-08-07 (#703) precisely because both fixtures claiming it — <c>RecordCodecPropertyTests</c> and
/// <c>FenceBlockCodecTests</c> — build their own wire bytes and round-trip them. They exercise the codec's arithmetic and are
/// structurally incapable of observing the <b>emitter</b> putting a physical identifier on the wire, which is the only thing LOG-06
/// constrains. Both were green in the same build as a red production probe. A verifier for an emitter rule has to read what the
/// emitter wrote, so this scanner reads the real segments through the engine's own reader and codec.
/// </para>
/// <para>
/// FenceBlock records (#559) are expanded into the same per-(entity, slot) shape as <see cref="RecordKind.Slot"/>, mirroring
/// <c>RecoveryDriver</c>. That is deliberate: the columnar path copies component columns straight out of the cluster page, so it
/// carries a collection handle exactly as a per-entity Slot payload does, and a LOG-06 assertion that only looked at Slot records
/// would miss half the emitters.
/// </para>
/// </remarks>
internal static class WalScanner
{
    /// <summary>One record as the engine wrote it, with the variable-length tail copied out of the reader's transient span.</summary>
    internal sealed class Record
    {
        public long Lsn;
        public long Tsn;
        public RecordKind Kind;
        public byte Op;
        public long EntityId;
        public ushort ArchetypeId;
        public ushort SlotIndex;
        public ushort FieldId;
        public int Index;
        public bool IsFence;

        /// <summary>Component value (Slot / expanded FenceBlock) or collection element (CollectionDelta); empty otherwise.</summary>
        public byte[] Payload = [];

        /// <summary>True when this record was expanded out of a columnar FenceBlock rather than read as a standalone Slot.</summary>
        public bool FromFenceBlock;

        public override string ToString() =>
            $"LSN {Lsn} {Kind}/{Op} entity 0x{EntityId:X} slot {SlotIndex} field {FieldId} idx {Index} payload {Payload.Length}B"
            + (FromFenceBlock ? " (fence block)" : string.Empty);
    }

    /// <summary>Scans every WAL segment in <paramref name="walDir"/> in LSN order, expanding FenceBlock records.</summary>
    public static List<Record> ScanAll(string walDir)
    {
        var records = new List<Record>();
        var walIO = new WalFileIO();

        using (var reader = new WalSegmentReader(walIO))
        {
            foreach (var path in walIO.EnumerateSegmentPaths(walDir).OrderBy(p => p, StringComparer.Ordinal))
            {
                if (!reader.OpenSegment(path))
                {
                    continue;
                }

                while (reader.TryReadNext(out var ch, out var body))
                {
                    if (ch.ChunkType != (ushort)WalChunkType.Transaction)
                    {
                        continue;
                    }

                    var offset = 0;
                    while (RecordCodec.TryReadRecord(body, offset, out var consumed, out var view))
                    {
                        offset += consumed;
                        if (view.IsUnknownKind)
                        {
                            continue;
                        }

                        if (view.Kind == RecordKind.FenceBlock)
                        {
                            ExpandFenceBlock(in view, records);
                            continue;
                        }

                        records.Add(new Record
                        {
                            Lsn = view.Lsn, Tsn = view.Tsn, Kind = view.Kind, Op = view.Op,
                            EntityId = view.EntityId, ArchetypeId = view.ArchetypeId, SlotIndex = view.SlotIndex,
                            FieldId = view.FieldId, Index = view.Index, IsFence = view.IsFence,
                            Payload = view.Payload.Length > 0 ? view.Payload.ToArray() : [],
                        });
                    }
                }
            }
        }

        records.Sort(static (a, b) => a.Lsn.CompareTo(b.Lsn));
        return records;
    }

    private static void ExpandFenceBlock(in RecordView view, List<Record> into)
    {
        if (!RecordCodec.TryReadFenceBlock(view.Payload, out var block))
        {
            return;
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
                continue;
            }

            for (var c = 0; c < block.ColumnCount; c++)
            {
                into.Add(new Record
                {
                    Lsn = view.Lsn, Tsn = view.Tsn, Kind = RecordKind.Slot, Op = (byte)SlotOp.Upsert,
                    EntityId = entityKey, ArchetypeId = block.ArchetypeId, SlotIndex = block.SlotIndexOf(c),
                    IsFence = view.IsFence, FromFenceBlock = true, Payload = block.ValueAt(c, i).ToArray(),
                });
            }
        }
    }
}
