using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

// The single owner of WAL v2 record bytes (LOG-02). Writes a CommitBatchBuilder into a sequence of RecordBatch
// chunks (a record never spans a chunk, 02 §1), and reads records back torn-tolerantly (02 §4). The chunk envelope
// (WalChunkHeader/Footer) + CRC chain stay the transport's job — the writer thread patches PrevCRC/CRC at drain.
// See claude/design/Durability/MinimalWal/02-wal-format.md §5–§6.

/// <summary>Stateless codec for WAL v2 records. The only code permitted to read/write record bytes (LOG-02, grep-gated 08 §7).</summary>
[PublicAPI]
internal static class RecordCodec
{
    /// <summary>Max chunk size in bytes — 8-aligned and below <see cref="ushort.MaxValue"/> (the <c>ChunkSize</c> field width).</summary>
    internal const int DefaultMaxChunkSize = 65528;

    private const int ChunkEnvelope = WalChunkHeader.SizeInBytes + WalChunkFooter.SizeInBytes; // 12

    /// <summary>Largest single record (header + body) the codec can emit; larger components/elements are rejected at registration (02 §1).</summary>
    internal static int MaxRecordWireSize(int maxChunkSize = DefaultMaxChunkSize) => maxChunkSize - ChunkEnvelope;

    // ── Sizing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Exact wire size of the batch (Σ chunk envelopes + Σ records, with the same greedy packing <see cref="Write"/> uses).
    /// <paramref name="recordCount"/> = total records (drives LSN assignment); <paramref name="chunkCount"/> = chunks produced.
    /// </summary>
    internal static int Measure(in CommitBatchBuilder batch, out int recordCount, out int chunkCount, int maxChunkSize = DefaultMaxChunkSize)
    {
        var entries = batch.Arena.Entries;
        recordCount = entries.Count;
        chunkCount = 0;

        var maxBody = maxChunkSize - ChunkEnvelope;
        var total = 0;
        var curBody = 0;

        for (var bucket = 0; bucket <= 3; bucket++)
        {
            foreach (var e in entries)
            {
                if (e.Bucket != bucket)
                {
                    continue;
                }

                var recWire = RecordHeader.SizeInBytes + BodyLength(e);
                if (recWire > maxBody)
                {
                    ThrowHelper.ThrowInvalidOp($"WAL record of {recWire} bytes exceeds the maximum of {maxBody} (must be rejected at component registration).");
                }

                if (curBody > 0 && curBody + recWire > maxBody)
                {
                    total += ChunkEnvelope + curBody;
                    chunkCount++;
                    curBody = 0;
                }

                curBody += recWire;
            }
        }

        if (curBody > 0)
        {
            total += ChunkEnvelope + curBody;
            chunkCount++;
        }

        return total;
    }

    // ── Writing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the batch into <paramref name="dest"/> as RecordBatch chunks in LOG-07 order, assigning ascending LSNs from
    /// <paramref name="firstLsn"/>. Markers: TxBegin on the first record, TxCommit on the last (fence batches carry FenceRecord
    /// instead). PrevCRC/footer-CRC are left zero — the WAL writer patches them at drain. Returns bytes written (== <see cref="Measure"/>).
    /// </summary>
    internal static int Write(Span<byte> dest, in CommitBatchBuilder batch, long firstLsn, int maxChunkSize = DefaultMaxChunkSize)
    {
        var arena = batch.Arena;
        var entries = arena.Entries;
        var recordCount = entries.Count;
        var maxBody = maxChunkSize - ChunkEnvelope;

        var baseFlags = batch.FenceMode ? RecordFlags.FenceRecord : RecordFlags.None;
        if (batch.CommittedDiscipline)
        {
            baseFlags |= RecordFlags.Committed;
        }

        var writeOffset = 0;       // cursor into dest
        var chunkStart = -1;       // offset of the open chunk's header (-1 = no open chunk)
        var chunkBodyLen = 0;      // bytes written into the open chunk's body so far
        var globalIndex = 0;       // emission index, for markers + LSN

        for (var bucket = 0; bucket <= 3; bucket++)
        {
            foreach (var e in entries)
            {
                if (e.Bucket != bucket)
                {
                    continue;
                }

                var recWire = RecordHeader.SizeInBytes + BodyLength(e);

                // Close the open chunk if this record would overflow it; a record never spans chunks (02 §1).
                if (chunkStart >= 0 && chunkBodyLen + recWire > maxBody)
                {
                    CloseChunk(dest, chunkStart, chunkBodyLen, ref writeOffset);
                    chunkStart = -1;
                    chunkBodyLen = 0;
                }

                if (chunkStart < 0)
                {
                    chunkStart = writeOffset;
                    writeOffset += WalChunkHeader.SizeInBytes; // reserve header; patched at CloseChunk
                    chunkBodyLen = 0;
                }

                var flags = baseFlags;
                if (!batch.FenceMode)
                {
                    if (globalIndex == 0)
                    {
                        flags |= RecordFlags.TxBegin;
                    }

                    if (globalIndex == recordCount - 1)
                    {
                        flags |= RecordFlags.TxCommit;
                    }
                }

                var written = WriteRecord(dest[writeOffset..], in e, arena, firstLsn + globalIndex, batch.Tsn, batch.UowEpoch, flags);
                writeOffset += written;
                chunkBodyLen += written;
                globalIndex++;
            }
        }

        if (chunkStart >= 0)
        {
            CloseChunk(dest, chunkStart, chunkBodyLen, ref writeOffset);
        }

        return writeOffset;
    }

    private static void CloseChunk(Span<byte> dest, int chunkStart, int chunkBodyLen, ref int writeOffset)
    {
        var chunkSize = WalChunkHeader.SizeInBytes + chunkBodyLen + WalChunkFooter.SizeInBytes;
        var header = new WalChunkHeader { ChunkType = (ushort)WalChunkType.Transaction, ChunkSize = (ushort)chunkSize, PrevCRC = 0 };
        MemoryMarshal.Write(dest[chunkStart..], in header);

        var footer = new WalChunkFooter { CRC = 0 };
        MemoryMarshal.Write(dest[writeOffset..], in footer);
        writeOffset += WalChunkFooter.SizeInBytes;
    }

    private static int WriteRecord(Span<byte> dest, in BatchEntry e, CommitBatchArena arena, long lsn, long tsn, ushort uowEpoch, RecordFlags flags)
    {
        var bodyLen = BodyLength(e);

        // RecordHeader (24 B)
        BinaryPrimitives.WriteInt64LittleEndian(dest, lsn);
        BinaryPrimitives.WriteInt64LittleEndian(dest[8..], tsn);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[16..], uowEpoch);
        dest[18] = (byte)e.Kind;
        dest[19] = (byte)flags;
        BinaryPrimitives.WriteUInt32LittleEndian(dest[20..], (uint)bodyLen);

        var body = dest[RecordHeader.SizeInBytes..];
        switch (e.Kind)
        {
            case RecordKind.Slot:
                BinaryPrimitives.WriteInt64LittleEndian(body[SlotRecordBody.EntityIdOffset..], e.EntityId);
                BinaryPrimitives.WriteUInt16LittleEndian(body[SlotRecordBody.ComponentTypeIdOffset..], e.ComponentTypeId);
                body[SlotRecordBody.OpOffset] = e.Op;
                body[SlotRecordBody.ReservedOffset] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(body[SlotRecordBody.PayloadLengthOffset..], (ushort)e.PayloadLength);
                if (e.PayloadLength > 0)
                {
                    var payloadDst = body.Slice(SlotRecordBody.FixedSize, e.PayloadLength);
                    arena.Payload(e.PayloadOffset, e.PayloadLength).CopyTo(payloadDst);
                    // Zero collection-handle byte ranges in-place — bufferIds never reach the log (LOG-06, 02 §3.1).
                    ZeroHandleRanges(payloadDst, arena.HandleRanges(e.HandleRangeOffset, e.HandleRangeCount));
                }

                break;

            case RecordKind.Lifecycle:
                BinaryPrimitives.WriteInt64LittleEndian(body[LifecycleRecordBody.EntityIdOffset..], e.EntityId);
                body[LifecycleRecordBody.OpOffset] = e.Op;
                body[LifecycleRecordBody.ReservedOffset] = 0;
                BinaryPrimitives.WriteUInt16LittleEndian(body[LifecycleRecordBody.ArchetypeIdOffset..], e.ArchetypeId);
                BinaryPrimitives.WriteUInt16LittleEndian(body[LifecycleRecordBody.EnabledBitsOffset..], e.EnabledBits);
                break;

            case RecordKind.CollectionDelta:
                BinaryPrimitives.WriteInt64LittleEndian(body[CollectionDeltaRecordBody.EntityIdOffset..], e.EntityId);
                BinaryPrimitives.WriteUInt16LittleEndian(body[CollectionDeltaRecordBody.ComponentTypeIdOffset..], e.ComponentTypeId);
                BinaryPrimitives.WriteUInt16LittleEndian(body[CollectionDeltaRecordBody.FieldIdOffset..], e.FieldId);
                body[CollectionDeltaRecordBody.OpOffset] = e.Op;
                body[CollectionDeltaRecordBody.ReservedOffset] = 0;
                BinaryPrimitives.WriteInt32LittleEndian(body[CollectionDeltaRecordBody.IndexOffset..], e.Index);
                BinaryPrimitives.WriteUInt16LittleEndian(body[CollectionDeltaRecordBody.ElementLengthOffset..], (ushort)e.PayloadLength);
                if (e.PayloadLength > 0)
                {
                    arena.Payload(e.PayloadOffset, e.PayloadLength).CopyTo(body.Slice(CollectionDeltaRecordBody.FixedSize, e.PayloadLength));
                }

                break;

            case RecordKind.BulkManifest:
                BinaryPrimitives.WriteInt64LittleEndian(body[BulkManifestRecordBody.BulkSessionIdOffset..], e.BulkSessionId);
                BinaryPrimitives.WriteInt64LittleEndian(body[BulkManifestRecordBody.BulkBeginLsnOffset..], e.BulkBeginLsn);
                BinaryPrimitives.WriteInt64LittleEndian(body[BulkManifestRecordBody.EntityCountOffset..], e.EntityCount);
                BinaryPrimitives.WriteInt64LittleEndian(body[BulkManifestRecordBody.ComponentCountOffset..], e.ComponentCount);
                break;

            default:
                ThrowHelper.ThrowInvalidOp($"Unknown record kind {e.Kind} in batch builder.");
                break;
        }

        return RecordHeader.SizeInBytes + bodyLen;
    }

    private static void ZeroHandleRanges(Span<byte> payload, ReadOnlySpan<uint> packedRanges)
    {
        foreach (var packed in packedRanges)
        {
            var offset = (int)(packed >> 16);
            var length = (int)(packed & 0xFFFF);
            if (length > 0 && offset + length <= payload.Length)
            {
                payload.Slice(offset, length).Clear();
            }
        }
    }

    private static int BodyLength(in BatchEntry e) => e.Kind switch
    {
        RecordKind.Slot => SlotRecordBody.FixedSize + e.PayloadLength,
        RecordKind.Lifecycle => LifecycleRecordBody.Size,
        RecordKind.CollectionDelta => CollectionDeltaRecordBody.FixedSize + e.PayloadLength,
        RecordKind.BulkManifest => BulkManifestRecordBody.Size,
        _ => 0,
    };

    // ── Reading ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads one record from a chunk body at <paramref name="offset"/> (02 §4). Returns false on exhaustion or truncation
    /// (never throws past a torn tail). Unknown kinds are skipped by BodyLength with <see cref="RecordView.IsUnknownKind"/> set
    /// (the caller counts + continues). On success, <paramref name="bytesConsumed"/> is the record's wire size.
    /// </summary>
    internal static bool TryReadRecord(ReadOnlySpan<byte> chunkBody, int offset, out int bytesConsumed, out RecordView view)
    {
        view = default;
        bytesConsumed = 0;

        var remaining = chunkBody.Length - offset;
        if (remaining < RecordHeader.SizeInBytes)
        {
            return false;
        }

        var hdr = chunkBody.Slice(offset, RecordHeader.SizeInBytes);
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(hdr[20..]);
        if (bodyLength > (uint)(remaining - RecordHeader.SizeInBytes))
        {
            return false; // torn — the body is not fully present
        }

        view.Lsn = BinaryPrimitives.ReadInt64LittleEndian(hdr);
        view.Tsn = BinaryPrimitives.ReadInt64LittleEndian(hdr[8..]);
        view.UowEpoch = BinaryPrimitives.ReadUInt16LittleEndian(hdr[16..]);
        var kind = (RecordKind)hdr[18];
        view.Kind = kind;
        view.Flags = (RecordFlags)hdr[19];
        view.BodyLength = bodyLength;

        var body = chunkBody.Slice(offset + RecordHeader.SizeInBytes, (int)bodyLength);

        switch (kind)
        {
            case RecordKind.Slot:
                if (body.Length < SlotRecordBody.FixedSize)
                {
                    return false;
                }

                view.EntityId = BinaryPrimitives.ReadInt64LittleEndian(body[SlotRecordBody.EntityIdOffset..]);
                view.ComponentTypeId = BinaryPrimitives.ReadUInt16LittleEndian(body[SlotRecordBody.ComponentTypeIdOffset..]);
                view.Op = body[SlotRecordBody.OpOffset];
                var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(body[SlotRecordBody.PayloadLengthOffset..]);
                if (SlotRecordBody.FixedSize + payloadLength != body.Length)
                {
                    return false;
                }

                view.Payload = body.Slice(SlotRecordBody.FixedSize, payloadLength);
                break;

            case RecordKind.Lifecycle:
                if (body.Length != LifecycleRecordBody.Size)
                {
                    return false;
                }

                view.EntityId = BinaryPrimitives.ReadInt64LittleEndian(body[LifecycleRecordBody.EntityIdOffset..]);
                view.Op = body[LifecycleRecordBody.OpOffset];
                view.ArchetypeId = BinaryPrimitives.ReadUInt16LittleEndian(body[LifecycleRecordBody.ArchetypeIdOffset..]);
                view.EnabledBits = BinaryPrimitives.ReadUInt16LittleEndian(body[LifecycleRecordBody.EnabledBitsOffset..]);
                break;

            case RecordKind.CollectionDelta:
                if (body.Length < CollectionDeltaRecordBody.FixedSize)
                {
                    return false;
                }

                view.EntityId = BinaryPrimitives.ReadInt64LittleEndian(body[CollectionDeltaRecordBody.EntityIdOffset..]);
                view.ComponentTypeId = BinaryPrimitives.ReadUInt16LittleEndian(body[CollectionDeltaRecordBody.ComponentTypeIdOffset..]);
                view.FieldId = BinaryPrimitives.ReadUInt16LittleEndian(body[CollectionDeltaRecordBody.FieldIdOffset..]);
                view.Op = body[CollectionDeltaRecordBody.OpOffset];
                view.Index = BinaryPrimitives.ReadInt32LittleEndian(body[CollectionDeltaRecordBody.IndexOffset..]);
                var elementLength = BinaryPrimitives.ReadUInt16LittleEndian(body[CollectionDeltaRecordBody.ElementLengthOffset..]);
                if (CollectionDeltaRecordBody.FixedSize + elementLength != body.Length)
                {
                    return false;
                }

                view.Payload = body.Slice(CollectionDeltaRecordBody.FixedSize, elementLength);
                break;

            case RecordKind.BulkManifest:
                if (body.Length != BulkManifestRecordBody.Size)
                {
                    return false;
                }

                view.BulkSessionId = BinaryPrimitives.ReadInt64LittleEndian(body[BulkManifestRecordBody.BulkSessionIdOffset..]);
                view.BulkBeginLsn = BinaryPrimitives.ReadInt64LittleEndian(body[BulkManifestRecordBody.BulkBeginLsnOffset..]);
                view.EntityCount = BinaryPrimitives.ReadInt64LittleEndian(body[BulkManifestRecordBody.EntityCountOffset..]);
                view.ComponentCount = BinaryPrimitives.ReadInt64LittleEndian(body[BulkManifestRecordBody.ComponentCountOffset..]);
                break;

            default:
                // Forward compatibility: skip the unknown record by BodyLength; the caller counts it (02 §4).
                view.IsUnknownKind = true;
                break;
        }

        bytesConsumed = RecordHeader.SizeInBytes + (int)bodyLength;
        return true;
    }

    /// <summary>
    /// Walks a contiguous region of RecordBatch chunks (as produced by <see cref="Write"/>) and yields records across chunk
    /// boundaries. Torn-tolerant: stops at the first chunk whose declared size overruns the buffer or whose body is exhausted.
    /// CRC validation is the transport's responsibility (the writer patches it at drain); the reader is layout-only here.
    /// </summary>
    internal ref struct RecordBatchReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _chunkOffset;
        private int _recordOffset;   // within the current chunk body
        private int _chunkBodyEnd;   // absolute end of the current chunk body
        private bool _chunkOpen;

        public RecordBatchReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _chunkOffset = 0;
            _recordOffset = 0;
            _chunkBodyEnd = 0;
            _chunkOpen = false;
        }

        /// <summary>True when a non-RecordBatch chunk type was encountered (a v2 violation — recovery fails the open, 02 §1).</summary>
        public bool SawUnknownChunkType { get; private set; }

        public bool TryRead(out RecordView view)
        {
            while (true)
            {
                if (!_chunkOpen && !OpenNextChunk())
                {
                    view = default;
                    return false;
                }

                if (TryReadRecord(_data[.._chunkBodyEnd], _recordOffset, out var consumed, out view))
                {
                    _recordOffset += consumed;
                    return true;
                }

                // Chunk body exhausted (or its tail torn) — advance to the next chunk.
                _chunkOpen = false;
            }
        }

        private bool OpenNextChunk()
        {
            if (_chunkOffset + WalChunkHeader.SizeInBytes > _data.Length)
            {
                return false;
            }

            var header = MemoryMarshal.Read<WalChunkHeader>(_data[_chunkOffset..]);
            var chunkSize = header.ChunkSize;
            if (chunkSize < WalChunkHeader.SizeInBytes + WalChunkFooter.SizeInBytes || _chunkOffset + chunkSize > _data.Length)
            {
                return false; // padding / torn / invalid chunk — stop
            }

            if (header.ChunkType != (ushort)WalChunkType.Transaction)
            {
                SawUnknownChunkType = true;
                return false;
            }

            _recordOffset = _chunkOffset + WalChunkHeader.SizeInBytes;
            _chunkBodyEnd = _chunkOffset + chunkSize - WalChunkFooter.SizeInBytes;
            _chunkOffset += chunkSize;
            _chunkOpen = true;
            return true;
        }
    }
}
