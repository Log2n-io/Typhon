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
}
