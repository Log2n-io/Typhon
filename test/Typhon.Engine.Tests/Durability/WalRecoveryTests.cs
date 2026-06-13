using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Typhon.Engine.Tests;

/// <summary>
/// Tests for <see cref="WalRecovery"/> — the crash recovery orchestrator.
/// Builds raw WAL segment files using the chunk envelope format (v2),
/// sets up UowRegistry state, and verifies that recovery correctly
/// promotes committed UoWs and voids pending ones.
/// </summary>
[TestFixture]
class WalRecoveryTests : TestBase<WalRecoveryTests>
{
    private InMemoryWalFileIO _fileIO;
    private string _walDir;
    private const uint TestSegmentSize = 64 * 1024; // 64KB for tests

    public override void Setup()
    {
        base.Setup();
        _fileIO = new InMemoryWalFileIO();
        _walDir = Path.Combine(Path.GetTempPath(), $"typhon_wal_recovery_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_walDir);
    }

    public override void TearDown()
    {
        _fileIO?.Dispose();
        if (Directory.Exists(_walDir))
        {
            Directory.Delete(_walDir, true);
        }

        base.TearDown();
    }

    // ═══════════════════════════════════════════════════════════════
    // Helper: Build WAL segment binary data (chunk envelope v2)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Defines a WAL record for test segment building.</summary>
    private record struct RecordDef(
        ushort UowId,
        byte Flags,
        byte OperationType = (byte)WalOperationType.Create,
        ushort ComponentTypeId = 1,
        long EntityId = 100,
        long TSN = 1,
        byte[] Payload = null);

    /// <summary>
    /// Builds a complete WAL segment with header, a single frame, and chunk-wrapped transaction records.
    /// Each record is wrapped: [WalChunkHeader 8B] [WalRecordHeader 32B] [payload] [WalChunkFooter 4B].
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

        // Calculate total frame bytes (sum of all chunk sizes)
        var totalChunkBytes = 0;
        foreach (var rec in records)
        {
            var payloadLen = rec.Payload?.Length ?? 0;
            var bodyLen = WalRecordHeader.SizeInBytes + payloadLen;
            var chunkSize = WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;
            totalChunkBytes += chunkSize;
        }

        var frameLength = WalFrameHeader.SizeInBytes + totalChunkBytes;

        // Write frame header
        var offset = WalSegmentHeader.SizeInBytes;
        ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref data[offset]);
        frameHeader.FrameLength = frameLength;
        frameHeader.RecordCount = records.Length;

        // Write chunk-wrapped records
        var recordOffset = offset + WalFrameHeader.SizeInBytes;
        var lsn = firstLSN;
        uint prevFooterCrc = 0;

        for (int i = 0; i < records.Length; i++)
        {
            var rec = records[i];
            var payloadLen = rec.Payload?.Length ?? 0;
            var bodyLen = WalRecordHeader.SizeInBytes + payloadLen;
            var chunkSize = WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;

            // Write WalChunkHeader
            ref var ch = ref Unsafe.As<byte, WalChunkHeader>(ref data[recordOffset]);
            ch.ChunkType = (ushort)WalChunkType.Transaction;
            ch.ChunkSize = (ushort)chunkSize;
            ch.PrevCRC = prevFooterCrc;

            // Write WalRecordHeader (32B body)
            ref var rh = ref Unsafe.As<byte, WalRecordHeader>(ref data[recordOffset + WalChunkHeader.SizeInBytes]);
            rh.LSN = lsn++;
            rh.TransactionTSN = rec.TSN;
            rh.UowEpoch = rec.UowId;
            rh.ComponentTypeId = rec.ComponentTypeId;
            rh.EntityId = rec.EntityId;
            rh.PayloadLength = (ushort)payloadLen;
            rh.OperationType = rec.OperationType;
            rh.Flags = rec.Flags;

            // Write payload after record header
            if (payloadLen > 0)
            {
                rec.Payload.AsSpan().CopyTo(data.AsSpan(recordOffset + WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes));
            }

            // Compute CRC over [0, ChunkSize - 4) and write footer
            var crcSpan = data.AsSpan(recordOffset, chunkSize - WalChunkFooter.SizeInBytes);
            var crc = WalCrc.Compute(crcSpan);
            Unsafe.As<byte, uint>(ref data[recordOffset + chunkSize - WalChunkFooter.SizeInBytes]) = crc;
            prevFooterCrc = crc;

            recordOffset += chunkSize;
        }

        return data;
    }

    /// <summary>
    /// Creates a WAL segment file on disk AND populates InMemoryWalFileIO for reading.
    /// WalRecovery.DiscoverSegments uses the filesystem, WalSegmentReader uses IWalFileIO.
    /// </summary>
    private void CreateWalSegmentFile(long segmentId, byte[] segmentData)
    {
        var fileName = $"{segmentId:D16}.wal";
        var filePath = Path.Combine(_walDir, fileName);

        // Create real file for discovery
        File.WriteAllBytes(filePath, segmentData);

        // Populate InMemoryWalFileIO for reading
        using var handle = _fileIO.OpenSegment(filePath, withFUA: false);
        _fileIO.WriteAligned(handle, 0, segmentData);
    }

    // ═══════════════════════════════════════════════════════════════
    // Empty WAL Directory
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_EmptyWalDirectory_VoidsAllPending()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate some UoW IDs (Pending state)
        var id1 = registry.AllocateUowId();
        var id2 = registry.AllocateUowId();

        // LoadFromDiskRaw preserves Pending entries
        registry.LoadFromDiskRaw();

        // Recovery with empty WAL directory — no segments to scan
        // Use a different empty directory
        var emptyDir = Path.Combine(Path.GetTempPath(), $"typhon_empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);

        try
        {
            using var recovery = new WalRecovery(_fileIO, emptyDir);
            var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

            Assert.That(result.SegmentsScanned, Is.EqualTo(0));
            Assert.That(result.RecordsScanned, Is.EqualTo(0));
            Assert.That(result.UowsPromoted, Is.EqualTo(0));
            Assert.That(result.UowsVoided, Is.GreaterThan(0), "Pending entries should be voided");
        }
        finally
        {
            Directory.Delete(emptyDir, true);
            registry.Release(id1);
            registry.Release(id2);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Happy Path: Committed UoW
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_CommittedUoW_PromotesCorrectly()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        // Allocate UoW ID 1 (Pending state)
        var id1 = registry.AllocateUowId();

        // LoadFromDiskRaw preserves Pending
        registry.LoadFromDiskRaw();

        // Build WAL segment with begin + commit for UoW id1
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: id1, Flags: (byte)WalRecordFlags.UowBegin,
                OperationType: (byte)WalOperationType.Create, EntityId: 1, TSN: 10, Payload: payload),
            new RecordDef(UowId: id1, Flags: (byte)WalRecordFlags.UowCommit,
                OperationType: (byte)WalOperationType.Create, EntityId: 2, TSN: 10, Payload: payload));

        CreateWalSegmentFile(1, segmentData);

        // Run recovery
        using var recovery = new WalRecovery(_fileIO, _walDir);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.SegmentsScanned, Is.EqualTo(1));
        Assert.That(result.RecordsScanned, Is.EqualTo(2));
        Assert.That(result.UowsPromoted, Is.EqualTo(1));
        Assert.That(result.UowsVoided, Is.EqualTo(0));
        Assert.That(result.LastValidLSN, Is.EqualTo(2));

        // id1 should now be in the committed bitmap
        Assert.That(registry.IsCommitted(id1), Is.True, "UoW with commit marker should be promoted");

        registry.Release(id1);
    }

    // ═══════════════════════════════════════════════════════════════
    // Partial UoW (no commit marker)
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_PendingUoWWithNoCommitMarker_VoidsIt()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId();
        registry.LoadFromDiskRaw();

        // Build WAL segment with begin but NO commit for UoW id1 (simulates crash before commit)
        var payload = new byte[] { 0x01, 0x02 };
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: id1, Flags: (byte)WalRecordFlags.UowBegin,
                OperationType: (byte)WalOperationType.Create, EntityId: 1, TSN: 10, Payload: payload));

        CreateWalSegmentFile(1, segmentData);

        using var recovery = new WalRecovery(_fileIO, _walDir);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.SegmentsScanned, Is.EqualTo(1));
        Assert.That(result.RecordsScanned, Is.EqualTo(1));
        Assert.That(result.UowsPromoted, Is.EqualTo(0), "UoW without commit marker should not be promoted");
        Assert.That(result.UowsVoided, Is.EqualTo(1), "Pending UoW without commit should be voided");

        Assert.That(registry.IsCommitted(id1), Is.False, "Voided UoW should not be in committed bitmap");

        registry.Release(id1);
    }

    // ═══════════════════════════════════════════════════════════════
    // Mixed UoWs: Some committed, some pending
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_MixedUoWs_CorrectPromotionAndVoiding()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId(); // Will have commit marker in WAL
        var id2 = registry.AllocateUowId(); // Will have begin but no commit (crashed)
        var id3 = registry.AllocateUowId(); // Will have commit marker in WAL

        registry.LoadFromDiskRaw();

        var payload = new byte[] { 0xAA };

        // Build segment with mixed UoWs:
        // id1: begin + commit (committed)
        // id2: begin only (crashed)
        // id3: begin + commit (committed)
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: id1, Flags: (byte)WalRecordFlags.UowBegin, Payload: payload),
            new RecordDef(UowId: id2, Flags: (byte)WalRecordFlags.UowBegin, Payload: payload),
            new RecordDef(UowId: id3, Flags: (byte)WalRecordFlags.UowBegin, Payload: payload),
            new RecordDef(UowId: id1, Flags: (byte)WalRecordFlags.UowCommit, Payload: payload),
            new RecordDef(UowId: id3, Flags: (byte)WalRecordFlags.UowCommit, Payload: payload));

        CreateWalSegmentFile(1, segmentData);

        using var recovery = new WalRecovery(_fileIO, _walDir);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.UowsPromoted, Is.EqualTo(2), "id1 and id3 should be promoted");
        Assert.That(result.UowsVoided, Is.EqualTo(1), "id2 should be voided");

        Assert.That(registry.IsCommitted(id1), Is.True, "id1 had commit marker -> promoted");
        Assert.That(registry.IsCommitted(id2), Is.False, "id2 had no commit marker -> voided");
        Assert.That(registry.IsCommitted(id3), Is.True, "id3 had commit marker -> promoted");

        registry.Release(id1);
        registry.Release(id2);
        registry.Release(id3);
    }

    // ═══════════════════════════════════════════════════════════════
    // CRC Chain Break
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_CrcChainBreak_StopsCleanly()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId();
        var id2 = registry.AllocateUowId();
        registry.LoadFromDiskRaw();

        var payload = new byte[] { 0xBB };

        // Build segment with records — we'll corrupt the second chunk's footer CRC
        var segmentData = BuildSegment(1, 1,
            // id1 committed (begin+commit in single record)
            new RecordDef(UowId: id1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload),
            // id2 committed — but we'll corrupt its footer CRC
            new RecordDef(UowId: id2, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));

        // Find the second chunk's footer CRC and corrupt it
        var chunk1Size = WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes + payload.Length + WalChunkFooter.SizeInBytes;
        var chunk2Start = WalSegmentHeader.SizeInBytes + WalFrameHeader.SizeInBytes + chunk1Size;
        var chunk2Header = Unsafe.As<byte, WalChunkHeader>(ref segmentData[chunk2Start]);
        var chunk2FooterOffset = chunk2Start + chunk2Header.ChunkSize - WalChunkFooter.SizeInBytes;
        segmentData[chunk2FooterOffset] ^= 0xFF;

        CreateWalSegmentFile(1, segmentData);

        using var recovery = new WalRecovery(_fileIO, _walDir);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        // Only the first record should have been scanned (before CRC break)
        Assert.That(result.RecordsScanned, Is.EqualTo(1));
        Assert.That(result.UowsPromoted, Is.EqualTo(1), "id1 should be promoted (before corruption)");
        Assert.That(result.UowsVoided, Is.EqualTo(1), "id2 should be voided (corrupted record ignored)");

        Assert.That(registry.IsCommitted(id1), Is.True);
        Assert.That(registry.IsCommitted(id2), Is.False);

        registry.Release(id1);
        registry.Release(id2);
    }

    // ═══════════════════════════════════════════════════════════════
    // Multi-Segment Recovery
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_MultipleSegments_ScansAllInOrder()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId();
        var id2 = registry.AllocateUowId();
        registry.LoadFromDiskRaw();

        var payload = new byte[] { 0xCC };

        // Segment 1: id1 begin
        var seg1 = BuildSegment(1, 1,
            new RecordDef(UowId: id1, Flags: (byte)WalRecordFlags.UowBegin, Payload: payload));

        // Segment 2: id1 commit + id2 begin+commit
        var seg2 = BuildSegment(2, 10,
            new RecordDef(UowId: id1, Flags: (byte)WalRecordFlags.UowCommit, Payload: payload),
            new RecordDef(UowId: id2, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));

        CreateWalSegmentFile(1, seg1);
        CreateWalSegmentFile(2, seg2);

        using var recovery = new WalRecovery(_fileIO, _walDir);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.SegmentsScanned, Is.EqualTo(2));
        Assert.That(result.RecordsScanned, Is.EqualTo(3));
        Assert.That(result.UowsPromoted, Is.EqualTo(2), "Both UoWs should be promoted");
        Assert.That(result.UowsVoided, Is.EqualTo(0));
        Assert.That(result.LastValidLSN, Is.EqualTo(11));

        Assert.That(registry.IsCommitted(id1), Is.True);
        Assert.That(registry.IsCommitted(id2), Is.True);

        registry.Release(id1);
        registry.Release(id2);
    }

    // ═══════════════════════════════════════════════════════════════
    // CheckpointLSN Filtering
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_WithCheckpointLSN_SkipsOlderRecords()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId();
        registry.LoadFromDiskRaw();

        var payload = new byte[] { 0xDD };

        // Build segment with LSNs 1-3
        // Set checkpointLSN = 2, so records with LSN <= 2 should be skipped
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: id1, Flags: (byte)WalRecordFlags.UowBegin, Payload: payload, EntityId: 1),
            new RecordDef(UowId: id1, Flags: 0, Payload: payload, EntityId: 2),
            new RecordDef(UowId: id1, Flags: (byte)WalRecordFlags.UowCommit, Payload: payload, EntityId: 3));

        CreateWalSegmentFile(1, segmentData);

        using var recovery = new WalRecovery(_fileIO, _walDir);
        var result = recovery.Recover(registry, checkpointLSN: 2, dbe: null);

        // All 3 records were scanned (reader sees them all), but only the one with LSN > 2 is processed
        Assert.That(result.RecordsScanned, Is.EqualTo(3));

        // Only LSN 3 (with UowCommit flag) is past the checkpoint -> UoW should still be promoted
        // because the commit marker at LSN 3 is after checkpoint
        Assert.That(result.UowsPromoted, Is.EqualTo(1));

        registry.Release(id1);
    }

    // ═══════════════════════════════════════════════════════════════
    // Recovery Statistics
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_ReturnsValidStatistics()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId();
        registry.LoadFromDiskRaw();

        var payload = new byte[] { 0xEE, 0xFF };
        var segmentData = BuildSegment(1, 1,
            new RecordDef(UowId: id1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit), Payload: payload));

        CreateWalSegmentFile(1, segmentData);

        using var recovery = new WalRecovery(_fileIO, _walDir);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.ElapsedMicroseconds, Is.GreaterThanOrEqualTo(0));
        Assert.That(result.SegmentsScanned, Is.EqualTo(1));
        Assert.That(result.RecordsScanned, Is.EqualTo(1));
        Assert.That(result.LastValidLSN, Is.EqualTo(1));
        Assert.That(result.FpiRecordsApplied, Is.EqualTo(0), "No torn pages in this test — FPI repair should not trigger");

        registry.Release(id1);
    }

    // ═══════════════════════════════════════════════════════════════
    // Non-Existent WAL Directory
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_NonExistentDirectory_VoidsAllPending()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;

        var id1 = registry.AllocateUowId();
        registry.LoadFromDiskRaw();

        using var recovery = new WalRecovery(_fileIO, "/tmp/nonexistent_wal_dir_12345");
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.SegmentsScanned, Is.EqualTo(0));
        Assert.That(result.UowsVoided, Is.GreaterThan(0));

        registry.Release(id1);
    }

    // ═══════════════════════════════════════════════════════════════
    // FPI Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a page buffer with a valid CRC32C checksum in the PageBaseHeader.
    /// The data region is filled with a recognizable pattern.
    /// </summary>
    private static byte[] BuildPageWithCrc(int filePageIndex, byte fillByte = 0xAA)
    {
        var page = new byte[PagedMMF.PageSize];

        // Write a minimal PageBaseHeader
        ref var header = ref Unsafe.As<byte, PageBaseHeader>(ref page[0]);
        header.Flags = PageBlockFlags.None;
        header.Type = PageBlockType.None;
        header.FormatRevision = 1;
        header.ChangeRevision = 1;
        header.ModificationCounter = 0;
        header.PageChecksum = 0; // Will be computed below

        // Fill data region with pattern
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            page[i] = fillByte;
        }

        // Compute and stamp CRC
        var crc = WalCrc.ComputeSkipping(page, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        Unsafe.As<byte, uint>(ref page[PageBaseHeader.PageChecksumOffset]) = crc;

        return page;
    }

    /// <summary>
    /// Builds a WAL segment containing FPI chunks (WalChunkType.FullPageImage).
    /// FPI chunk body = [LSN 8B] [FpiMetadata 16B] [page data].
    /// </summary>
    private static unsafe byte[] BuildSegmentWithFpi(long segmentId, long firstLSN, params (int FilePageIndex, byte[] PageData)[] fpiEntries)
    {
        var data = new byte[TestSegmentSize];

        // Write segment header
        fixed (byte* p = data)
        {
            ref var header = ref *(WalSegmentHeader*)p;
            header.Initialize(segmentId, firstLSN, prevSegmentLsn: 0, TestSegmentSize);
            header.ComputeAndSetCrc();
        }

        if (fpiEntries.Length == 0)
        {
            return data;
        }

        // Calculate total chunk bytes
        var totalChunkBytes = 0;
        foreach (var entry in fpiEntries)
        {
            var bodyLen = sizeof(long) + FpiMetadata.SizeInBytes + PagedMMF.PageSize;
            var chunkSize = WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;
            totalChunkBytes += chunkSize;
        }

        var frameLength = WalFrameHeader.SizeInBytes + totalChunkBytes;

        // Write frame header
        var offset = WalSegmentHeader.SizeInBytes;
        ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref data[offset]);
        frameHeader.FrameLength = frameLength;
        frameHeader.RecordCount = fpiEntries.Length;

        // Write FPI chunks
        var recordOffset = offset + WalFrameHeader.SizeInBytes;
        var lsn = firstLSN;
        uint prevFooterCrc = 0;

        for (int i = 0; i < fpiEntries.Length; i++)
        {
            var entry = fpiEntries[i];
            var bodyLen = sizeof(long) + FpiMetadata.SizeInBytes + PagedMMF.PageSize;
            var chunkSize = WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;

            // Write chunk header
            ref var ch = ref Unsafe.As<byte, WalChunkHeader>(ref data[recordOffset]);
            ch.ChunkType = (ushort)WalChunkType.FullPageImage;
            ch.ChunkSize = (ushort)chunkSize;
            ch.PrevCRC = prevFooterCrc;

            // Write LSN at body offset 0
            Unsafe.As<byte, long>(ref data[recordOffset + WalChunkHeader.SizeInBytes]) = lsn++;

            // Write FpiMetadata at body offset 8
            ref var meta = ref Unsafe.As<byte, FpiMetadata>(ref data[recordOffset + WalChunkHeader.SizeInBytes + sizeof(long)]);
            meta.FilePageIndex = entry.FilePageIndex;
            meta.SegmentId = 0;
            meta.ChangeRevision = 1;
            meta.UncompressedSize = (ushort)PagedMMF.PageSize;
            meta.CompressionAlgo = 0;
            meta.Reserved = 0;

            // Write page data at body offset 24
            entry.PageData.AsSpan().CopyTo(
                data.AsSpan(recordOffset + WalChunkHeader.SizeInBytes + sizeof(long) + FpiMetadata.SizeInBytes));

            // Compute CRC over [0, ChunkSize - 4) and write footer
            var crcSpan = data.AsSpan(recordOffset, chunkSize - WalChunkFooter.SizeInBytes);
            var crc = WalCrc.Compute(crcSpan);
            Unsafe.As<byte, uint>(ref data[recordOffset + chunkSize - WalChunkFooter.SizeInBytes]) = crc;
            prevFooterCrc = crc;

            recordOffset += chunkSize;
        }

        return data;
    }

    // ═══════════════════════════════════════════════════════════════
    // FPI Torn Page Repair Tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Recover_TornPage_RepairedFromFpi()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;
        var mmf = dbe.MMF;

        registry.LoadFromDiskRaw();

        const int targetPage = 5;

        // Build a valid page with CRC
        var goodPageData = BuildPageWithCrc(targetPage, 0xAA);

        // Write the good page to disk
        mmf.WritePageDirect(targetPage, goodPageData);

        // Build WAL segment with FPI record for the good page
        var segmentData = BuildSegmentWithFpi(1, 1, (targetPage, goodPageData));
        CreateWalSegmentFile(1, segmentData);

        // Corrupt the page on disk (flip bytes in data area, leaving CRC as-is -> mismatch)
        var corruptedPage = (byte[])goodPageData.Clone();
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            corruptedPage[i] = 0xFF; // Overwrite data with different pattern
        }
        mmf.WritePageDirect(targetPage, corruptedPage);

        // Run recovery — Phase 4 should detect CRC mismatch and repair from FPI
        using var recovery = new WalRecovery(_fileIO, _walDir, mmf);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.FpiRecordsApplied, Is.EqualTo(1), "Torn page should be repaired from FPI");
    }

    [Test]
    public void Recover_ConsistentPage_NotRepaired()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;
        var mmf = dbe.MMF;

        registry.LoadFromDiskRaw();

        const int targetPage = 5;

        // Build a valid page with CRC
        var goodPageData = BuildPageWithCrc(targetPage, 0xBB);

        // Write the good page to disk — CRC is valid
        mmf.WritePageDirect(targetPage, goodPageData);

        // Build WAL segment with FPI record for this page (FPI exists but page is fine)
        var segmentData = BuildSegmentWithFpi(1, 1, (targetPage, goodPageData));
        CreateWalSegmentFile(1, segmentData);

        // Run recovery — CRC matches, so FPI should NOT be applied
        using var recovery = new WalRecovery(_fileIO, _walDir, mmf);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.FpiRecordsApplied, Is.EqualTo(0), "Consistent page should not be repaired");
    }

    [Test]
    public void Recover_MultipleFpi_UsesHighestLSN()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;
        var mmf = dbe.MMF;

        registry.LoadFromDiskRaw();

        const int targetPage = 5;

        // Build two versions of the page — the second (higher LSN) should win
        var oldPageData = BuildPageWithCrc(targetPage, 0x11);
        var newPageData = BuildPageWithCrc(targetPage, 0x22);

        // Build WAL segment with two FPI records for the same page (LSN 1 and 2)
        var segmentData = BuildSegmentWithFpi(1, 1, (targetPage, oldPageData), (targetPage, newPageData));
        CreateWalSegmentFile(1, segmentData);

        // Corrupt the page on disk
        var corruptedPage = BuildPageWithCrc(targetPage, 0xFF);
        // Manually corrupt it: change data but keep the old CRC from newPageData
        Array.Copy(newPageData, 0, corruptedPage, 0, PageBaseHeader.PageChecksumOffset);
        Array.Copy(newPageData, PageBaseHeader.PageChecksumOffset, corruptedPage, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        Array.Copy(newPageData, PageBaseHeader.PageChecksumOffset + PageBaseHeader.PageChecksumSize, corruptedPage,
            PageBaseHeader.PageChecksumOffset + PageBaseHeader.PageChecksumSize,
            PagedMMF.PageSize - PageBaseHeader.PageChecksumOffset - PageBaseHeader.PageChecksumSize);
        // Now flip data bytes to create corruption
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            corruptedPage[i] ^= 0xFF;
        }
        mmf.WritePageDirect(targetPage, corruptedPage);

        // Run recovery — should use highest-LSN FPI (newPageData, LSN 2)
        using var recovery = new WalRecovery(_fileIO, _walDir, mmf);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.FpiRecordsApplied, Is.EqualTo(1));

        // Verify the repaired page matches newPageData (0x22 pattern)
        var repairedPage = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(targetPage, repairedPage);
        Assert.That(repairedPage[PagedMMF.PageHeaderSize], Is.EqualTo(0x22),
            "Repaired page should contain data from highest-LSN FPI");
    }

    [Test]
    public void Recover_ZeroChecksum_SkipsRepair()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;
        var mmf = dbe.MMF;

        registry.LoadFromDiskRaw();

        const int targetPage = 5;

        // Build a page with CRC = 0 (never checkpointed)
        var page = new byte[PagedMMF.PageSize];
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            page[i] = 0xCC;
        }
        // PageChecksum is already 0 (default)

        mmf.WritePageDirect(targetPage, page);

        // Build WAL segment with FPI record
        var fpiPageData = BuildPageWithCrc(targetPage, 0xDD);
        var segmentData = BuildSegmentWithFpi(1, 1, (targetPage, fpiPageData));
        CreateWalSegmentFile(1, segmentData);

        // Run recovery — CRC == 0 means "never checkpointed", should be skipped
        using var recovery = new WalRecovery(_fileIO, _walDir, mmf);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        Assert.That(result.FpiRecordsApplied, Is.EqualTo(0), "Zero-checksum page should be skipped");
    }

    // ═══════════════════════════════════════════════════════════════
    // FPI Repair + WAL Replay Ordering (Phase 4 -> Phase 5)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a WAL segment containing both FPI chunks and transaction chunks in a single frame.
    /// FPI chunks come first (lower LSNs), then transaction chunks, matching the typical WAL write order.
    /// All chunks share the same prevFooterCrc chain.
    /// </summary>
    private static unsafe byte[] BuildSegmentWithFpiAndUow(
        long segmentId, long firstLSN,
        (int FilePageIndex, byte[] PageData)[] fpiEntries,
        RecordDef[] uowRecords)
    {
        var data = new byte[TestSegmentSize];

        // Write segment header
        fixed (byte* p = data)
        {
            ref var header = ref *(WalSegmentHeader*)p;
            header.Initialize(segmentId, firstLSN, prevSegmentLsn: 0, TestSegmentSize);
            header.ComputeAndSetCrc();
        }

        var totalRecords = fpiEntries.Length + uowRecords.Length;
        if (totalRecords == 0)
        {
            return data;
        }

        // Calculate total chunk bytes for all records
        var totalChunkBytes = 0;
        foreach (var entry in fpiEntries)
        {
            var bodyLen = sizeof(long) + FpiMetadata.SizeInBytes + PagedMMF.PageSize;
            var chunkSize = WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;
            totalChunkBytes += chunkSize;
        }
        foreach (var rec in uowRecords)
        {
            var payloadLen = rec.Payload?.Length ?? 0;
            var bodyLen = WalRecordHeader.SizeInBytes + payloadLen;
            var chunkSize = WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;
            totalChunkBytes += chunkSize;
        }

        var frameLength = WalFrameHeader.SizeInBytes + totalChunkBytes;

        // Write frame header
        var offset = WalSegmentHeader.SizeInBytes;
        ref var frameHeader = ref Unsafe.As<byte, WalFrameHeader>(ref data[offset]);
        frameHeader.FrameLength = frameLength;
        frameHeader.RecordCount = totalRecords;

        var recordOffset = offset + WalFrameHeader.SizeInBytes;
        var lsn = firstLSN;
        uint prevFooterCrc = 0;

        // Write FPI chunks first
        for (int i = 0; i < fpiEntries.Length; i++)
        {
            var entry = fpiEntries[i];
            var bodyLen = sizeof(long) + FpiMetadata.SizeInBytes + PagedMMF.PageSize;
            var chunkSize = WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;

            // Write chunk header
            ref var ch = ref Unsafe.As<byte, WalChunkHeader>(ref data[recordOffset]);
            ch.ChunkType = (ushort)WalChunkType.FullPageImage;
            ch.ChunkSize = (ushort)chunkSize;
            ch.PrevCRC = prevFooterCrc;

            // Write LSN at body offset 0
            Unsafe.As<byte, long>(ref data[recordOffset + WalChunkHeader.SizeInBytes]) = lsn++;

            // Write FpiMetadata at body offset 8
            ref var meta = ref Unsafe.As<byte, FpiMetadata>(
                ref data[recordOffset + WalChunkHeader.SizeInBytes + sizeof(long)]);
            meta.FilePageIndex = entry.FilePageIndex;
            meta.SegmentId = 0;
            meta.ChangeRevision = 1;
            meta.UncompressedSize = (ushort)PagedMMF.PageSize;
            meta.CompressionAlgo = 0;
            meta.Reserved = 0;

            // Write page data at body offset 24
            entry.PageData.AsSpan().CopyTo(
                data.AsSpan(recordOffset + WalChunkHeader.SizeInBytes + sizeof(long) + FpiMetadata.SizeInBytes));

            // Compute CRC over [0, ChunkSize - 4) and write footer
            var crcSpan = data.AsSpan(recordOffset, chunkSize - WalChunkFooter.SizeInBytes);
            var crc = WalCrc.Compute(crcSpan);
            Unsafe.As<byte, uint>(ref data[recordOffset + chunkSize - WalChunkFooter.SizeInBytes]) = crc;
            prevFooterCrc = crc;

            recordOffset += chunkSize;
        }

        // Write transaction chunks
        for (int i = 0; i < uowRecords.Length; i++)
        {
            var rec = uowRecords[i];
            var payloadLen = rec.Payload?.Length ?? 0;
            var bodyLen = WalRecordHeader.SizeInBytes + payloadLen;
            var chunkSize = WalChunkHeader.SizeInBytes + bodyLen + WalChunkFooter.SizeInBytes;

            // Write chunk header
            ref var ch = ref Unsafe.As<byte, WalChunkHeader>(ref data[recordOffset]);
            ch.ChunkType = (ushort)WalChunkType.Transaction;
            ch.ChunkSize = (ushort)chunkSize;
            ch.PrevCRC = prevFooterCrc;

            // Write WalRecordHeader (32B body)
            ref var rh = ref Unsafe.As<byte, WalRecordHeader>(ref data[recordOffset + WalChunkHeader.SizeInBytes]);
            rh.LSN = lsn++;
            rh.TransactionTSN = rec.TSN;
            rh.UowEpoch = rec.UowId;
            rh.ComponentTypeId = rec.ComponentTypeId;
            rh.EntityId = rec.EntityId;
            rh.PayloadLength = (ushort)payloadLen;
            rh.OperationType = rec.OperationType;
            rh.Flags = rec.Flags;

            // Write payload
            if (payloadLen > 0)
            {
                rec.Payload.AsSpan().CopyTo(
                    data.AsSpan(recordOffset + WalChunkHeader.SizeInBytes + WalRecordHeader.SizeInBytes));
            }

            // Compute CRC over [0, ChunkSize - 4) and write footer
            var crcSpan = data.AsSpan(recordOffset, chunkSize - WalChunkFooter.SizeInBytes);
            var crc = WalCrc.Compute(crcSpan);
            Unsafe.As<byte, uint>(ref data[recordOffset + chunkSize - WalChunkFooter.SizeInBytes]) = crc;
            prevFooterCrc = crc;

            recordOffset += WalRecordHeader.SizeInBytes + payloadLen;
        }

        return data;
    }

    [Test]
    [CancelAfter(5000)]
    public void Recover_FpiRepairThenWalReplay_ProducesCorrectFinalState()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;
        var mmf = dbe.MMF;

        var id1 = registry.AllocateUowId();
        registry.LoadFromDiskRaw();

        // v2 (CK-05): pages 0–4 are reserved (meta pair + occupancy) and the engine owns low pages from 5 up; allocate a
        // genuinely-free page so the FPI repair targets a page nothing else writes.
        var targetPage = mmf.AllocatePage();

        // Build a valid page filled with 0xAA + valid CRC
        var goodPageData = BuildPageWithCrc(targetPage, 0xAA);
        mmf.WritePageDirect(targetPage, goodPageData);

        // Build WAL segment with: FPI chunk (before-image) + committed UoW chunk
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var segmentData = BuildSegmentWithFpiAndUow(1, 1,
            [(targetPage, goodPageData)],
            [
                new RecordDef(UowId: id1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit),
                    OperationType: (byte)WalOperationType.Create, EntityId: 1, TSN: 10, Payload: payload)
            ]);
        CreateWalSegmentFile(1, segmentData);

        // Corrupt the page on disk (change data but keep old CRC -> CRC mismatch)
        var corruptedPage = (byte[])goodPageData.Clone();
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            corruptedPage[i] = 0xFF;
        }
        mmf.WritePageDirect(targetPage, corruptedPage);

        // Run recovery — Phase 4 (FPI repair) should run BEFORE Phase 5 (WAL replay)
        using var recovery = new WalRecovery(_fileIO, _walDir, mmf);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        // Verify Phase 4 ran: FPI repaired the torn page
        Assert.That(result.FpiRecordsApplied, Is.EqualTo(1), "Phase 4 should repair the torn page from FPI");

        // Verify Phase 3 ran: UoW was promoted
        Assert.That(result.UowsPromoted, Is.EqualTo(1), "Phase 3 should promote the committed UoW");

        // Verify page on disk is restored to the before-image (0xAA)
        var repairedPage = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(targetPage, repairedPage);
        Assert.That(repairedPage[PagedMMF.PageHeaderSize], Is.EqualTo(0xAA),
            "Repaired page should contain the FPI before-image data");

        registry.Release(id1);
    }

    [Test]
    [CancelAfter(5000)]
    public void Recover_CompressedFpiRepairThenWalReplay_ProducesCorrectFinalState()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        var registry = dbe.UowRegistry;
        var mmf = dbe.MMF;

        var id1 = registry.AllocateUowId();
        registry.LoadFromDiskRaw();

        // v2 (CK-05): pages 0–4 are reserved (meta pair + occupancy) and the engine owns low pages from 5 up; allocate a
        // genuinely-free page so the FPI repair targets a page nothing else writes.
        var targetPage = mmf.AllocatePage();

        // Build a valid page with 0xAA pattern + CRC (highly compressible)
        var goodPageData = FpiCompressionTests.BuildPageWithCrc(targetPage, 0xAA);
        mmf.WritePageDirect(targetPage, goodPageData);

        // Build WAL segment with compressed FPI + committed UoW
        // The compressed FPI segment and the UoW segment are separate files
        var compressedFpiSegment = FpiCompressionTests.BuildSegmentWithCompressedFpi(1, 1, targetPage, goodPageData);

        // Build a second segment for the UoW record (LSN continues from segment 1)
        var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var uowSegment = BuildSegment(2, 10,
            new RecordDef(UowId: id1, Flags: (byte)(WalRecordFlags.UowBegin | WalRecordFlags.UowCommit),
                OperationType: (byte)WalOperationType.Create, EntityId: 1, TSN: 10, Payload: payload));

        CreateWalSegmentFile(1, compressedFpiSegment);
        CreateWalSegmentFile(2, uowSegment);

        // Corrupt the page on disk
        var corruptedPage = (byte[])goodPageData.Clone();
        for (int i = PagedMMF.PageHeaderSize; i < PagedMMF.PageSize; i++)
        {
            corruptedPage[i] = 0xFF;
        }
        mmf.WritePageDirect(targetPage, corruptedPage);

        // Run recovery — compressed FPI repair (Phase 4) + UoW promotion (Phase 3) + replay (Phase 5)
        using var recovery = new WalRecovery(_fileIO, _walDir, mmf);
        var result = recovery.Recover(registry, checkpointLSN: 0, dbe: null);

        // Verify Phase 4: compressed FPI decompressed and applied
        Assert.That(result.FpiRecordsApplied, Is.EqualTo(1), "Compressed FPI should decompress and repair the page");

        // Verify Phase 3: UoW promoted
        Assert.That(result.UowsPromoted, Is.EqualTo(1), "Committed UoW should be promoted");

        // Verify page restored to before-image
        var repairedPage = new byte[PagedMMF.PageSize];
        mmf.ReadPageDirect(targetPage, repairedPage);
        Assert.That(repairedPage[PagedMMF.PageHeaderSize], Is.EqualTo(0xAA),
            "Repaired page should match the compressed FPI before-image");

        registry.Release(id1);
    }
}
