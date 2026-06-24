using NUnit.Framework;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="WalSegmentReader"/> — the sequential WAL segment reader used during crash recovery.
/// Builds raw segment binary data using the chunk envelope format and verifies the reader parses chunks
/// correctly, detects CRC breaks, and handles truncation.
/// </summary>
[TestFixture]
public class WalSegmentReaderTests : AllocatorTestBase
{
    private InMemoryWalFileIO _fileIO;
    private const uint TestSegmentSize = 64 * 1024; // 64KB for tests
    private const string TestSegmentPath = "/tmp/test_segment_001.wal";

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
    }

    public override void TearDown()
    {
        _fileIO?.Dispose();
        base.TearDown();
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper: Build WAL segment binary data (chunk envelope format)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a complete WAL segment with header, a single frame, and chunk-wrapped records.
    /// Each record is wrapped: [WalChunkHeader 8B] [WalRecordHeader 32B] [payload] [WalChunkFooter 4B].
    /// CRC chain: each chunk's WalChunkHeader.PrevCRC = previous chunk's footer CRC.
    /// </summary>
    private static unsafe byte[] BuildSegment(long segmentId, long firstLSN, params RecordDef[] records)
    {
        var data = new byte[TestSegmentSize];

        // Write segment header
        fixed (byte* p = data)
        {
            ref var header = ref *(WalSegmentHeader*)p;
            header.Initialize(segmentId, firstLSN, prevSegmentLsn: 0, TestSegmentSize);
            header.ComputeAndSetCrc();
        }

        if (records.Length == 0)
        {
            return data;
        }

        // Calculate total chunk bytes: for each record, ChunkHeader + RecordHeader + payload + ChunkFooter
        var totalChunkBytes = 0;
        foreach (var rec in records)
        {
            var payloadLen = rec.Payload?.Length ?? 0;
            var chunkSize = WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payloadLen + WalChunkFooter.SizeInBytes;
            totalChunkBytes += chunkSize;
        }

        var frameLength = WalFrameHeader.SizeInBytes + totalChunkBytes;

        // Write frame header
        var offset = WalSegmentHeader.SizeInBytes;
        ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref data[offset]);
        frameHeader.FrameLength = frameLength;
        frameHeader.RecordCount = records.Length;

        // Write chunk-wrapped records
        var chunkOffset = offset + WalFrameHeader.SizeInBytes;
        var lsn = firstLSN;
        uint lastFooterCrc = 0;

        for (int i = 0; i < records.Length; i++)
        {
            var rec = records[i];
            var payloadLen = rec.Payload?.Length ?? 0;
            var chunkSize = (ushort)(WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payloadLen + WalChunkFooter.SizeInBytes);

            // Write WalChunkHeader (8 bytes)
            ref var chunkHeader = ref Unsafe.As<byte, WalChunkHeader>(ref data[chunkOffset]);
            chunkHeader.ChunkType = (ushort)WalChunkType.Transaction;
            chunkHeader.ChunkSize = chunkSize;
            chunkHeader.PrevCRC = lastFooterCrc;

            // Write WalRecordHeader (32 bytes) as the body start
            var bodyOffset = chunkOffset + WalChunkHeader.SizeInBytes;
            ref var recHeader = ref Unsafe.As<byte, WalRecordHeader>(ref data[bodyOffset]);
            recHeader.LSN = lsn++;
            recHeader.TransactionTSN = rec.TSN;
            recHeader.UowEpoch = rec.UowId;
            recHeader.ComponentTypeId = rec.ComponentTypeId;
            recHeader.EntityId = rec.EntityId;
            recHeader.PayloadLength = (ushort)payloadLen;
            recHeader.OperationType = rec.OperationType;
            recHeader.Flags = rec.Flags;

            // Write payload after record header
            if (payloadLen > 0)
            {
                rec.Payload.AsSpan().CopyTo(data.AsSpan(bodyOffset + WalRecordHeader.SizeInBytes));
            }

            // Compute footer CRC over [chunkOffset, chunkOffset + chunkSize - 4)
            var crcSpan = data.AsSpan(chunkOffset, chunkSize - WalChunkFooter.SizeInBytes);
            var footerCrc = Crc32CUtil.Compute(crcSpan);

            // Write WalChunkFooter (4 bytes) at end of chunk
            var footerOffset = chunkOffset + chunkSize - WalChunkFooter.SizeInBytes;
            Unsafe.As<byte, uint>(ref data[footerOffset]) = footerCrc;

            lastFooterCrc = footerCrc;
            chunkOffset += chunkSize;
        }

        return data;
    }

    /// <summary>Loads segment data into InMemoryWalFileIO so WalSegmentReader can read it.</summary>
    private void LoadSegmentIntoFileIO(string path, byte[] segmentData)
    {
        using var handle = _fileIO.OpenSegment(path, withFUA: false);
        _fileIO.WriteAligned(handle, 0, segmentData);
    }

    /// <summary>Defines a WAL record for test segment building.</summary>
    private record struct RecordDef(
        ushort UowId,
        byte Flags,
        byte OperationType = (byte)WalOperationType.Create,
        ushort ComponentTypeId = 1,
        long EntityId = 100,
        long TSN = 1,
        byte[] Payload = null);

    // ═══════════════════════════════════════════════════════════════
    // Segment Header Validation
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void OpenSegment_ValidHeader_ReturnsTrue()
    {
        var segmentData = BuildSegment(segmentId: 1, firstLSN: 1);
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);

        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);
        Assert.That(reader.SegmentHeader.SegmentId, Is.EqualTo(1));
        Assert.That(reader.SegmentHeader.FirstLSN, Is.EqualTo(1));
    }

    [Test]
    public void OpenSegment_InvalidMagic_ReturnsFalse()
    {
        var segmentData = BuildSegment(segmentId: 1, firstLSN: 1);
        // Corrupt the magic number (first 4 bytes)
        segmentData[0] = 0xFF;
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);

        Assert.That(reader.OpenSegment(TestSegmentPath), Is.False);
    }

    [Test]
    public void OpenSegment_NonExistentPath_ReturnsFalse()
    {
        using var reader = new WalSegmentReader(_fileIO);

        Assert.That(reader.OpenSegment("/tmp/nonexistent.wal"), Is.False);
    }

    // ═══════════════════════════════════════════════════════════════
    // Record Reading
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void TryReadNext_SingleRecord_ReadsCorrectly()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 5, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit),
                OperationType: (byte)WalOperationType.Create, EntityId: 42, TSN: 100, Payload: payload));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out var chunkHeader, out var body), Is.True);

        // Parse WalRecordHeader from the body
        var recHeader = MemoryMarshal.Read<WalRecordHeader>(body);
        var readPayload = body.Slice(WalRecordHeader.SizeInBytes);

        Assert.That(chunkHeader.ChunkType, Is.EqualTo((ushort)WalChunkType.Transaction));
        Assert.That(recHeader.LSN, Is.EqualTo(1));
        Assert.That(recHeader.UowEpoch, Is.EqualTo(5));
        Assert.That(recHeader.EntityId, Is.EqualTo(42));
        Assert.That(recHeader.TransactionTSN, Is.EqualTo(100));
        Assert.That(recHeader.PayloadLength, Is.EqualTo(4));
        Assert.That(recHeader.OperationType, Is.EqualTo((byte)WalOperationType.Create));
        Assert.That(recHeader.Flags, Is.EqualTo((byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit)));
        Assert.That(readPayload.ToArray(), Is.EqualTo(payload));

        // No more records
        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.False);
        Assert.That(reader.LastValidLSN, Is.EqualTo(1));
        Assert.That(reader.RecordsRead, Is.EqualTo(1));
    }

    [Test]
    public void TryReadNext_MultipleRecords_ReadsAll()
    {
        var payload1 = new byte[] { 0x01, 0x02 };
        var payload2 = new byte[] { 0x03, 0x04, 0x05 };
        var payload3 = new byte[] { 0x06 };

        var segmentData = BuildSegment(1, 10,
            new RecordDef(UowId: 1, Flags: (byte)WalRecordFlags.UowBegin, Payload: payload1),
            new RecordDef(UowId: 1, Flags: 0, Payload: payload2),
            new RecordDef(UowId: 1, Flags: (byte)WalRecordFlags.UowCommit, Payload: payload3));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        // Record 1
        Assert.That(reader.TryReadNext(out _, out var body1), Is.True);
        var h1 = MemoryMarshal.Read<WalRecordHeader>(body1);
        var p1 = body1.Slice(WalRecordHeader.SizeInBytes);
        Assert.That(h1.LSN, Is.EqualTo(10));
        Assert.That(h1.Flags & (byte)WalRecordFlags.UowBegin, Is.Not.EqualTo(0));
        Assert.That(p1.ToArray(), Is.EqualTo(payload1));

        // Record 2
        Assert.That(reader.TryReadNext(out _, out var body2), Is.True);
        var h2 = MemoryMarshal.Read<WalRecordHeader>(body2);
        var p2 = body2.Slice(WalRecordHeader.SizeInBytes);
        Assert.That(h2.LSN, Is.EqualTo(11));
        Assert.That(p2.ToArray(), Is.EqualTo(payload2));

        // Record 3
        Assert.That(reader.TryReadNext(out _, out var body3), Is.True);
        var h3 = MemoryMarshal.Read<WalRecordHeader>(body3);
        var p3 = body3.Slice(WalRecordHeader.SizeInBytes);
        Assert.That(h3.LSN, Is.EqualTo(12));
        Assert.That(h3.Flags & (byte)WalRecordFlags.UowCommit, Is.Not.EqualTo(0));
        Assert.That(p3.ToArray(), Is.EqualTo(payload3));

        // End of data
        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.RecordsRead, Is.EqualTo(3));
        Assert.That(reader.LastValidLSN, Is.EqualTo(12));
    }

    [Test]
    public void TryReadNext_RecordWithNoPayload_ReadsCorrectly()
    {
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit),
                OperationType: (byte)WalOperationType.Delete, EntityId: 99, Payload: null));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out _, out var body), Is.True);
        var recHeader = MemoryMarshal.Read<WalRecordHeader>(body);
        var payload = body.Slice(WalRecordHeader.SizeInBytes);
        Assert.That(recHeader.OperationType, Is.EqualTo((byte)WalOperationType.Delete));
        Assert.That(recHeader.PayloadLength, Is.EqualTo(0));
        Assert.That(payload.Length, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Empty Segment
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void TryReadNext_EmptySegment_ReturnsFalse()
    {
        // Segment with valid header but no records
        var segmentData = BuildSegment(1, 1);
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.False);
        Assert.That(reader.RecordsRead, Is.EqualTo(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // CRC Corruption Detection
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void TryReadNext_CorruptedFooterCRC_SetsTruncatedFlag()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        // Corrupt the chunk footer CRC (last 4 bytes of the chunk)
        var chunkStart = WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes;
        var chunkSize = WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payload.Length + WalChunkFooter.SizeInBytes;
        var footerCrcOffset = chunkStart + chunkSize - WalChunkFooter.SizeInBytes;
        segmentData[footerCrcOffset] ^= 0xFF;

        // Reload with corrupted data
        _fileIO.Dispose();
        _fileIO = new InMemoryWalFileIO();
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.True, "Footer CRC corruption should set WasTruncated");
    }

    [Test]
    public void TryReadNext_CorruptedPayload_SetsTruncatedFlag()
    {
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        // Corrupt a byte in the payload area (footer CRC won't match)
        var payloadOffset = WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes
                            + WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes;
        segmentData[payloadOffset] ^= 0xFF;

        _fileIO.Dispose();
        _fileIO = new InMemoryWalFileIO();
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.True);
    }

    [Test]
    public void TryReadNext_CrcChainBreak_SecondRecord_SetsTruncated()
    {
        var payload1 = new byte[] { 0x01 };
        var payload2 = new byte[] { 0x02 };

        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)WalRecordFlags.UowBegin, Payload: payload1),
            new RecordDef(UowId: 1, Flags: (byte)WalRecordFlags.UowCommit, Payload: payload2));
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        // Locate second chunk: after segment header + frame header + first chunk
        var chunk1Size = WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payload1.Length + WalChunkFooter.SizeInBytes;
        var chunk2Start = WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes + chunk1Size;

        // Corrupt WalChunkHeader.PrevCRC of the second chunk (at offset 4 within the chunk header)
        var prevCrcOffset = chunk2Start + 4; // PrevCRC is at offset 4 in WalChunkHeader (after ChunkType + ChunkSize)
        ref var prevCrc = ref Unsafe.As<byte, uint>(ref segmentData[prevCrcOffset]);
        prevCrc ^= 0xDEADBEEF;

        // Recompute the second chunk's footer CRC so the footer CRC itself is valid
        // (only the chain link is broken)
        var chunk2Size = WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payload2.Length + WalChunkFooter.SizeInBytes;
        var crcSpan = segmentData.AsSpan(chunk2Start, chunk2Size - WalChunkFooter.SizeInBytes);
        var newFooterCrc = Crc32CUtil.Compute(crcSpan);
        var chunk2FooterOffset = chunk2Start + chunk2Size - WalChunkFooter.SizeInBytes;
        Unsafe.As<byte, uint>(ref segmentData[chunk2FooterOffset]) = newFooterCrc;

        _fileIO.Dispose();
        _fileIO = new InMemoryWalFileIO();
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        // First record should read fine
        Assert.That(reader.TryReadNext(out _, out _), Is.True);

        // Second record — PrevCRC doesn't match first chunk's footer CRC → chain break
        Assert.That(reader.TryReadNext(out _, out _), Is.False);
        Assert.That(reader.WasTruncated, Is.True);
        Assert.That(reader.RecordsRead, Is.EqualTo(1));
    }

    // ═══════════════════════════════════════════════════════════════
    // Multiple Segments
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void OpenSegment_MultipleCalls_ResetsState()
    {
        var payload = new byte[] { 0xAA };

        var seg1 = BuildSegment(1, 1,
            new RecordDef(UowId: 1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));
        var seg2 = BuildSegment(2, 100,
            new RecordDef(UowId: 2, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));

        LoadSegmentIntoFileIO("/tmp/seg1.wal", seg1);
        LoadSegmentIntoFileIO("/tmp/seg2.wal", seg2);

        using var reader = new WalSegmentReader(_fileIO);

        // Read segment 1
        Assert.That(reader.OpenSegment("/tmp/seg1.wal"), Is.True);
        Assert.That(reader.TryReadNext(out _, out var body1), Is.True);
        var h1 = MemoryMarshal.Read<WalRecordHeader>(body1);
        Assert.That(h1.LSN, Is.EqualTo(1));
        Assert.That(h1.UowEpoch, Is.EqualTo(1));

        // Read segment 2 — state should be fully reset
        Assert.That(reader.OpenSegment("/tmp/seg2.wal"), Is.True);
        Assert.That(reader.WasTruncated, Is.False);
        Assert.That(reader.RecordsRead, Is.EqualTo(0));
        Assert.That(reader.TryReadNext(out _, out var body2), Is.True);
        var h2 = MemoryMarshal.Read<WalRecordHeader>(body2);
        Assert.That(h2.LSN, Is.EqualTo(100));
        Assert.That(h2.UowEpoch, Is.EqualTo(2));
    }

    // ═══════════════════════════════════════════════════════════════
    // Page-aligned drain blocks (WR-02 — padding-gap traversal)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a segment that mirrors the real <see cref="WalWriter"/> on-disk layout: each record is written as its own O_DIRECT
    /// drain block — a <see cref="WalFrameHeader"/> + one chunk — starting at a 4096-aligned segment offset and zero-padded to the
    /// next page boundary. Consecutive blocks are therefore separated by a zero tail, because the writer pads each
    /// <c>WriteAligned</c> up to a page multiple (<c>bytesToWrite = AlignUp(data.Length, PageSize)</c>). The CRC chain
    /// (<see cref="WalChunkHeader.PrevCRC"/>) runs continuously across blocks, exactly as the writer's single-threaded chain state
    /// does. This is the shape a multi-commit Immediate workload leaves on disk; the reader must jump each padding tail to the next
    /// page boundary to reach every frame (WR-02).
    /// </summary>
    private static unsafe byte[] BuildPageAlignedBlocks(long segmentId, long firstLSN, int blockCount)
    {
        const int pageSize = 4096;
        var data = new byte[TestSegmentSize];

        fixed (byte* p = data)
        {
            ref var header = ref *(WalSegmentHeader*)p;
            header.Initialize(segmentId, firstLSN, prevSegmentLsn: 0, TestSegmentSize);
            header.ComputeAndSetCrc();
        }

        var payload = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var chunkSize = (ushort)(WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payload.Length + WalChunkFooter.SizeInBytes);
        uint lastFooterCrc = 0;

        for (int i = 0; i < blockCount; i++)
        {
            // Block i starts at a page-aligned segment offset: the segment header is exactly one page, then one page per block.
            var blockOffset = WalSegmentHeader.SizeInBytes + i * pageSize;
            var lsn = firstLSN + i;

            ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref data[blockOffset]);
            frameHeader.FrameLength = WalFrameHeader.SizeInBytes + chunkSize;
            frameHeader.RecordCount = 1;
            frameHeader.LastLsn = lsn;

            var chunkOffset = blockOffset + WalFrameHeader.SizeInBytes;
            ref var chunkHeader = ref Unsafe.As<byte, WalChunkHeader>(ref data[chunkOffset]);
            chunkHeader.ChunkType = (ushort)WalChunkType.Transaction;
            chunkHeader.ChunkSize = chunkSize;
            chunkHeader.PrevCRC = lastFooterCrc; // continuous chain across blocks

            // Body: WalRecordHeader (LSN at offset 0 — the reader's LastValidLSN convention) + payload.
            var bodyOffset = chunkOffset + WalChunkHeader.SizeInBytes;
            ref var recHeader = ref Unsafe.As<byte, WalRecordHeader>(ref data[bodyOffset]);
            recHeader.LSN = lsn;
            recHeader.UowEpoch = 1;
            recHeader.PayloadLength = (ushort)payload.Length;
            payload.AsSpan().CopyTo(data.AsSpan(bodyOffset + WalRecordHeader.SizeInBytes));

            var crcSpan = data.AsSpan(chunkOffset, chunkSize - WalChunkFooter.SizeInBytes);
            var footerCrc = Crc32CUtil.Compute(crcSpan);
            Unsafe.As<byte, uint>(ref data[chunkOffset + chunkSize - WalChunkFooter.SizeInBytes]) = footerCrc;

            lastFooterCrc = footerCrc;
            // The rest of this page stays zero — the inter-block padding tail the reader must traverse.
        }

        return data;
    }

    [Test]
    [VerifiesRule("WR-02")]
    public void TryReadNext_FramesAcrossPageAlignedDrainBlocks_ReadsAll()
    {
        const int blocks = 5;
        var segmentData = BuildPageAlignedBlocks(segmentId: 1, firstLSN: 10, blockCount: blocks);
        LoadSegmentIntoFileIO(TestSegmentPath, segmentData);

        using var reader = new WalSegmentReader(_fileIO);
        Assert.That(reader.OpenSegment(TestSegmentPath), Is.True);

        // Each frame lives in its own page-aligned block with a zero-padded tail. Without padding-gap traversal the reader stops
        // after block 0 (the first zero FrameLength reads as end-of-data); with WR-02 it jumps each tail and reads all five frames,
        // validating the CRC chain end-to-end.
        for (int i = 0; i < blocks; i++)
        {
            Assert.That(reader.TryReadNext(out var ch, out var body), Is.True, $"frame {i} must be read across the padding gap");
            Assert.That(ch.ChunkType, Is.EqualTo((ushort)WalChunkType.Transaction));
            var recHeader = MemoryMarshal.Read<WalRecordHeader>(body);
            Assert.That(recHeader.LSN, Is.EqualTo(10 + i));
        }

        Assert.That(reader.TryReadNext(out _, out _), Is.False, "trailing zero pages are genuine end-of-data");
        Assert.That(reader.WasTruncated, Is.False, "padding tails are not truncation");
        Assert.That(reader.RecordsRead, Is.EqualTo(blocks));
        Assert.That(reader.LastValidLSN, Is.EqualTo(10 + blocks - 1));
    }
}