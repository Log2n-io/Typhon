using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Batch-order proofs (08 A1.17, LOG-07). The builder buckets entries by category as they are added, so no matter what order a
/// caller invokes Add*, the emitted stream is always Spawn → Slot/CollectionDelta → Destroy/SetEnabledBits → BulkManifest — a
/// mis-ordered batch is unconstructible by API shape. And the reader rejects a hand-built malformed stream rather than parsing
/// a partial record (the "recovery fails loudly" half is wired in P1.2; here the reader simply refuses the bad bytes).
/// </summary>
[TestFixture]
[VerifiesRule("LOG-07")]
internal sealed class BatchOrderTests
{
    [Test]
    public void Builder_EmitsLog07Order_RegardlessOfCallOrder()
    {
        var arena = new CommitBatchArena();
        var b = new CommitBatchBuilder(arena, tsn: 42, uowEpoch: 1);

        // Add in a deliberately scrambled order.
        b.AddBulkManifest(sessionId: 1, bulkBeginLsn: 0, entityCount: 2, componentCount: 3);
        b.AddDestroy(100);
        b.AddSlot(200, slotIndex: 7, payload: [1, 2, 3]);
        b.AddSpawn(300, archetypeId: 4, enabledBits: 0);
        b.AddEnabledBits(400, absoluteBits: 9);
        b.AddCollectionDelta(500, slotIndex: 8, fieldId: 0, CollectionOp.Append, index: 0, element: [5]);

        var size = RecordCodec.Measure(b, out var recordCount, out _);
        Assert.That(recordCount, Is.EqualTo(6));
        var buf = new byte[size];
        RecordCodec.Write(buf, b, firstLsn: 1000);

        var records = ReadAll(buf);
        Assert.That(records.Count, Is.EqualTo(6));

        // Expected LOG-07 order, with entity ids proving the bucketing.
        AssertRecord(records[0], RecordKind.Lifecycle, (byte)LifecycleOp.Spawn, 300);
        AssertRecord(records[1], RecordKind.Slot, (byte)SlotOp.Upsert, 200);
        AssertRecord(records[2], RecordKind.CollectionDelta, (byte)CollectionOp.Append, 500);
        AssertRecord(records[3], RecordKind.Lifecycle, (byte)LifecycleOp.Destroy, 100);
        AssertRecord(records[4], RecordKind.Lifecycle, (byte)LifecycleOp.SetEnabledBits, 400);
        Assert.That(records[5].Kind, Is.EqualTo(RecordKind.BulkManifest));

        // Markers ride emission order: only the first carries TxBegin, only the last TxCommit.
        Assert.That((records[0].Flags & RecordFlags.TxBegin) != 0, Is.True);
        Assert.That((records[5].Flags & RecordFlags.TxCommit) != 0, Is.True);
        for (var i = 1; i < records.Count; i++)
        {
            Assert.That((records[i].Flags & RecordFlags.TxBegin) != 0, Is.False, $"TxBegin must not appear on record {i}");
        }

        // LSNs are contiguous ascending from firstLsn.
        for (var i = 0; i < records.Count; i++)
        {
            Assert.That(records[i].Lsn, Is.EqualTo(1000 + i));
        }
    }

    [Test]
    public void Reader_RejectsSlotWithMismatchedPayloadLength_WithoutThrowing()
    {
        // A Slot whose body declares 5 payload bytes but whose internal PayloadLength field says 10: the reader must refuse it.
        var body = new byte[SlotRecordBody.FixedSize + 5];
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(SlotRecordBody.EntityIdOffset), 1234);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(SlotRecordBody.SlotIndexOffset), 1);
        body[SlotRecordBody.OpOffset] = (byte)SlotOp.Upsert;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(SlotRecordBody.PayloadLengthOffset), 10); // lies — only 5 present

        var chunk = WrapChunk(BuildRawRecord((byte)RecordKind.Slot, body));

        var count = 0;
        Assert.DoesNotThrow(() =>
        {
            var reader = new RecordCodec.RecordBatchReader(chunk);
            while (reader.TryRead(out _))
            {
                count++;
            }
        });
        Assert.That(count, Is.EqualTo(0), "a Slot with an internally inconsistent PayloadLength must be rejected, not partially parsed");
    }

    [Test]
    public void Reader_RejectsRecordWhoseBodyLengthOverrunsTheChunk_WithoutThrowing()
    {
        // A record header claiming a 200-byte body inside a chunk that only holds a few bytes → reader stops cleanly.
        var rec = new byte[RecordHeader.SizeInBytes + 4];
        BinaryPrimitives.WriteInt64LittleEndian(rec, 1);          // LSN
        BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(8), 1); // TSN
        rec[18] = (byte)RecordKind.Lifecycle;
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(20), 200); // BodyLength lies

        var chunk = WrapChunk(rec);

        var count = 0;
        Assert.DoesNotThrow(() =>
        {
            var reader = new RecordCodec.RecordBatchReader(chunk);
            while (reader.TryRead(out _))
            {
                count++;
            }
        });
        Assert.That(count, Is.EqualTo(0));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void AssertRecord(in Captured r, RecordKind kind, byte op, long entityId)
    {
        Assert.That(r.Kind, Is.EqualTo(kind));
        Assert.That(r.Op, Is.EqualTo(op));
        Assert.That(r.EntityId, Is.EqualTo(entityId));
    }

    private readonly record struct Captured(RecordKind Kind, RecordFlags Flags, long Lsn, byte Op, long EntityId);

    private static List<Captured> ReadAll(ReadOnlySpan<byte> data)
    {
        var list = new List<Captured>();
        var reader = new RecordCodec.RecordBatchReader(data);
        while (reader.TryRead(out var v))
        {
            list.Add(new Captured(v.Kind, v.Flags, v.Lsn, v.Op, v.EntityId));
        }

        return list;
    }

    private static byte[] BuildRawRecord(byte kind, byte[] body)
    {
        var rec = new byte[RecordHeader.SizeInBytes + body.Length];
        rec[18] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(20), (uint)body.Length);
        body.CopyTo(rec.AsSpan(RecordHeader.SizeInBytes));
        return rec;
    }

    private static byte[] WrapChunk(byte[] chunkBody)
    {
        var chunkSize = WalChunkHeader.SizeInBytes + chunkBody.Length + WalChunkFooter.SizeInBytes;
        var buf = new byte[chunkSize];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)WalChunkType.Transaction);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)chunkSize);
        chunkBody.CopyTo(buf.AsSpan(WalChunkHeader.SizeInBytes));
        return buf;
    }
}
