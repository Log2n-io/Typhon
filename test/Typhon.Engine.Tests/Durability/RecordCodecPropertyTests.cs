using CsCheck;
using NUnit.Framework;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests;

/// <summary>
/// Property tests for the WAL v2 <see cref="RecordCodec"/> (08 A1.3, 02 §6). Proves the codec is the faithful, lossless,
/// torn-tolerant owner of record bytes: round-trip, chunk-splitting, measure-exactness, truncation tolerance, unknown-kind
/// skipping, and collection-handle zeroing. Pure — no engine, no recovery.
/// </summary>
[TestFixture]
[VerifiesRule("LOG-02")]
[VerifiesRule("LOG-06")]
[VerifiesRule("LOG-07")]
internal sealed class RecordCodecPropertyTests
{
    private enum OpKind
    {
        Spawn, Destroy, EnabledBits, Slot, CollectionDelta, BulkManifest,
    }

    private sealed record ModelOp(
        OpKind Kind, long EntityId, ushort CompTypeId, ushort FieldId, ushort ArchetypeId, ushort EnabledBits,
        byte CollOp, int Index, byte[] Payload, long BulkSession, long BulkBegin, long EntityCount, long CompCount);

    private sealed class Decoded
    {
        public RecordKind Kind;
        public RecordFlags Flags;
        public long Lsn;
        public long Tsn;
        public ushort UowEpoch;
        public long EntityId;
        public ushort CompTypeId;
        public ushort FieldId;
        public ushort ArchetypeId;
        public ushort EnabledBits;
        public byte Op;
        public int Index;
        public long BulkSession;
        public long BulkBegin;
        public long EntityCount;
        public long CompCount;
        public byte[] Payload = [];
    }

    // ── Generators ────────────────────────────────────────────────────────────

    private static readonly Gen<ushort> GenU16 = Gen.Int[0, ushort.MaxValue].Select(i => (ushort)i);
    private static readonly Gen<long> GenEntityId = Gen.Long[1, long.MaxValue];

    private static Gen<byte[]> GenBytes(int max) =>
        from n in Gen.Int[0, max]
        from a in Gen.Byte.Array[n]
        select a;

    private static Gen<ModelOp> GenOp(int maxPayload)
    {
        var spawn =
            from id in GenEntityId
            from arch in GenU16
            from bits in GenU16
            select new ModelOp(OpKind.Spawn, id, 0, 0, arch, bits, 0, 0, [], 0, 0, 0, 0);

        var destroy =
            from id in GenEntityId
            select new ModelOp(OpKind.Destroy, id, 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0);

        var enabled =
            from id in GenEntityId
            from bits in GenU16
            select new ModelOp(OpKind.EnabledBits, id, 0, 0, 0, bits, 0, 0, [], 0, 0, 0, 0);

        var slot =
            from id in GenEntityId
            from cid in GenU16
            from p in GenBytes(maxPayload)
            select new ModelOp(OpKind.Slot, id, cid, 0, 0, 0, 0, 0, p, 0, 0, 0, 0);

        var coll =
            from id in GenEntityId
            from cid in GenU16
            from fid in GenU16
            from op in Gen.Int[1, 5]
            from idx in Gen.Int[0, 4096]
            from e in GenBytes(maxPayload)
            select new ModelOp(OpKind.CollectionDelta, id, cid, fid, 0, 0, (byte)op, idx, e, 0, 0, 0, 0);

        var bulk =
            from s in Gen.Long[1, long.MaxValue]
            from b in Gen.Long[0, long.MaxValue]
            from ec in Gen.Long[0, long.MaxValue]
            from cc in Gen.Long[0, long.MaxValue]
            select new ModelOp(OpKind.BulkManifest, 0, 0, 0, 0, 0, 0, 0, [], s, b, ec, cc);

        return Gen.OneOf(spawn, destroy, enabled, slot, coll, bulk);
    }

    private static Gen<ModelOp[]> GenOps(int maxCount, int maxPayload) =>
        from n in Gen.Int[0, maxCount]
        from a in GenOp(maxPayload).Array[n]
        select a;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] Build(ModelOp[] ops, long firstLsn, long tsn, ushort uow, int maxChunk, out int chunkCount)
    {
        var arena = new CommitBatchArena();
        var b = new CommitBatchBuilder(arena, tsn, uow);
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case OpKind.Spawn: b.AddSpawn(op.EntityId, op.ArchetypeId, op.EnabledBits); break;
                case OpKind.Destroy: b.AddDestroy(op.EntityId); break;
                case OpKind.EnabledBits: b.AddEnabledBits(op.EntityId, op.EnabledBits); break;
                case OpKind.Slot: b.AddSlot(op.EntityId, op.CompTypeId, op.Payload); break;
                case OpKind.CollectionDelta: b.AddCollectionDelta(op.EntityId, op.CompTypeId, op.FieldId, (CollectionOp)op.CollOp, op.Index, op.Payload); break;
                case OpKind.BulkManifest: b.AddBulkManifest(op.BulkSession, op.BulkBegin, op.EntityCount, op.CompCount); break;
            }
        }

        if (b.IsEmpty)
        {
            chunkCount = 0;
            return [];
        }

        var size = RecordCodec.Measure(b, out var recordCount, out chunkCount, maxChunk);
        Assert.That(recordCount, Is.EqualTo(ops.Length), "Measure recordCount must equal the number of staged ops");

        var buf = new byte[size];
        var written = RecordCodec.Write(buf, b, firstLsn, maxChunk);
        Assert.That(written, Is.EqualTo(size), "Write must consume exactly Measure bytes (property 5)");
        return buf;
    }

    private static List<Decoded> ReadAll(ReadOnlySpan<byte> data)
    {
        var list = new List<Decoded>();
        var reader = new RecordCodec.RecordBatchReader(data);
        while (reader.TryRead(out var v))
        {
            list.Add(Capture(in v));
        }

        return list;
    }

    private static Decoded Capture(scoped in RecordView v) => new()
    {
        Kind = v.Kind, Flags = v.Flags, Lsn = v.Lsn, Tsn = v.Tsn, UowEpoch = v.UowEpoch,
        EntityId = v.EntityId, CompTypeId = v.ComponentTypeId, FieldId = v.FieldId,
        ArchetypeId = v.ArchetypeId, EnabledBits = v.EnabledBits, Op = v.Op, Index = v.Index,
        BulkSession = v.BulkSessionId, BulkBegin = v.BulkBeginLsn, EntityCount = v.EntityCount,
        CompCount = v.ComponentCount, Payload = v.Payload.ToArray(),
    };

    private static List<ModelOp> ExpectedOrder(ModelOp[] ops)
    {
        var r = new List<ModelOp>(ops.Length);
        r.AddRange(ops.Where(o => o.Kind == OpKind.Spawn));
        r.AddRange(ops.Where(o => o.Kind is OpKind.Slot or OpKind.CollectionDelta));
        r.AddRange(ops.Where(o => o.Kind is OpKind.Destroy or OpKind.EnabledBits));
        r.AddRange(ops.Where(o => o.Kind == OpKind.BulkManifest));
        return r;
    }

    private static void AssertMatches(ModelOp exp, Decoded dec, long expLsn, long tsn, ushort uow)
    {
        Assert.That(dec.Lsn, Is.EqualTo(expLsn), "LSN must be firstLsn + index");
        Assert.That(dec.Tsn, Is.EqualTo(tsn));
        Assert.That(dec.UowEpoch, Is.EqualTo(uow));

        switch (exp.Kind)
        {
            case OpKind.Spawn:
                Assert.That(dec.Kind, Is.EqualTo(RecordKind.Lifecycle));
                Assert.That(dec.Op, Is.EqualTo((byte)LifecycleOp.Spawn));
                Assert.That(dec.EntityId, Is.EqualTo(exp.EntityId));
                Assert.That(dec.ArchetypeId, Is.EqualTo(exp.ArchetypeId));
                Assert.That(dec.EnabledBits, Is.EqualTo(exp.EnabledBits));
                break;
            case OpKind.Destroy:
                Assert.That(dec.Kind, Is.EqualTo(RecordKind.Lifecycle));
                Assert.That(dec.Op, Is.EqualTo((byte)LifecycleOp.Destroy));
                Assert.That(dec.EntityId, Is.EqualTo(exp.EntityId));
                break;
            case OpKind.EnabledBits:
                Assert.That(dec.Kind, Is.EqualTo(RecordKind.Lifecycle));
                Assert.That(dec.Op, Is.EqualTo((byte)LifecycleOp.SetEnabledBits));
                Assert.That(dec.EntityId, Is.EqualTo(exp.EntityId));
                Assert.That(dec.EnabledBits, Is.EqualTo(exp.EnabledBits));
                break;
            case OpKind.Slot:
                Assert.That(dec.Kind, Is.EqualTo(RecordKind.Slot));
                Assert.That(dec.Op, Is.EqualTo((byte)SlotOp.Upsert));
                Assert.That(dec.EntityId, Is.EqualTo(exp.EntityId));
                Assert.That(dec.CompTypeId, Is.EqualTo(exp.CompTypeId));
                Assert.That(dec.Payload, Is.EqualTo(exp.Payload).AsCollection);
                break;
            case OpKind.CollectionDelta:
                Assert.That(dec.Kind, Is.EqualTo(RecordKind.CollectionDelta));
                Assert.That(dec.Op, Is.EqualTo(exp.CollOp));
                Assert.That(dec.EntityId, Is.EqualTo(exp.EntityId));
                Assert.That(dec.CompTypeId, Is.EqualTo(exp.CompTypeId));
                Assert.That(dec.FieldId, Is.EqualTo(exp.FieldId));
                Assert.That(dec.Index, Is.EqualTo(exp.Index));
                Assert.That(dec.Payload, Is.EqualTo(exp.Payload).AsCollection);
                break;
            case OpKind.BulkManifest:
                Assert.That(dec.Kind, Is.EqualTo(RecordKind.BulkManifest));
                Assert.That(dec.BulkSession, Is.EqualTo(exp.BulkSession));
                Assert.That(dec.BulkBegin, Is.EqualTo(exp.BulkBegin));
                Assert.That(dec.EntityCount, Is.EqualTo(exp.EntityCount));
                Assert.That(dec.CompCount, Is.EqualTo(exp.CompCount));
                break;
        }
    }

    private static void AssertMarkers(List<Decoded> decoded)
    {
        for (var i = 0; i < decoded.Count; i++)
        {
            var isFirst = i == 0;
            var isLast = i == decoded.Count - 1;
            Assert.That((decoded[i].Flags & RecordFlags.TxBegin) != 0, Is.EqualTo(isFirst), $"TxBegin only on the first record (idx {i})");
            Assert.That((decoded[i].Flags & RecordFlags.TxCommit) != 0, Is.EqualTo(isLast), $"TxCommit only on the last record (idx {i})");
        }
    }

    // ── Properties ──────────────────────────────────────────────────────────────

    [Test]
    public void RoundTrip_PreservesAllRecords_InLog07Order_WithMarkers()
    {
        (from ops in GenOps(24, 96)
         from firstLsn in Gen.Long[1, 1_000_000]
         from tsn in Gen.Long[1, long.MaxValue]
         from uow in GenU16
         select (ops, firstLsn, tsn, uow))
            .Sample(t =>
            {
                var (ops, firstLsn, tsn, uow) = t;
                var buf = Build(ops, firstLsn, tsn, uow, RecordCodec.DefaultMaxChunkSize, out _);
                var decoded = ReadAll(buf);
                var expected = ExpectedOrder(ops);

                Assert.That(decoded.Count, Is.EqualTo(expected.Count));
                for (var i = 0; i < expected.Count; i++)
                {
                    AssertMatches(expected[i], decoded[i], firstLsn + i, tsn, uow);
                }

                AssertMarkers(decoded);
            });
    }

    [Test]
    public void Splitting_AcrossManyChunks_PreservesOrderLsnAndMarkers()
    {
        // Tiny max chunk forces a batch to straddle 1..n chunk boundaries (property 2).
        const int tinyChunk = 96;
        (from ops in GenOps(40, 24)
         from firstLsn in Gen.Long[1, 1_000_000]
         from tsn in Gen.Long[1, long.MaxValue]
         from uow in GenU16
         select (ops, firstLsn, tsn, uow))
            .Sample(t =>
            {
                var (ops, firstLsn, tsn, uow) = t;
                var buf = Build(ops, firstLsn, tsn, uow, tinyChunk, out var chunkCount);
                var decoded = ReadAll(buf);
                var expected = ExpectedOrder(ops);

                Assert.That(decoded.Count, Is.EqualTo(expected.Count));
                for (var i = 0; i < expected.Count; i++)
                {
                    AssertMatches(expected[i], decoded[i], firstLsn + i, tsn, uow);
                }

                AssertMarkers(decoded);

                // With many records under a tiny chunk cap, splitting must actually occur.
                if (expected.Count >= 6)
                {
                    Assert.That(chunkCount, Is.GreaterThan(1), "a large batch under a tiny chunk cap must span multiple chunks");
                }
            });
    }

    [Test]
    public void Truncation_AtAnyLength_NeverThrows_AndYieldsACompletePrefix()
    {
        (from ops in GenOps(16, 64)
         from firstLsn in Gen.Long[1, 1_000_000]
         from tsn in Gen.Long[1, long.MaxValue]
         from uow in GenU16
         select (ops, firstLsn, tsn, uow))
            .Sample(t =>
            {
                var (ops, firstLsn, tsn, uow) = t;
                var buf = Build(ops, firstLsn, tsn, uow, RecordCodec.DefaultMaxChunkSize, out _);
                var full = ReadAll(buf);

                // Sweep a sample of truncation points; the reader must never throw and must yield a prefix of the full decode.
                var step = Math.Max(1, buf.Length / 24);
                for (var k = 0; k <= buf.Length; k += step)
                {
                    var prefix = ReadAll(buf.AsSpan(0, k));
                    Assert.That(prefix.Count, Is.LessThanOrEqualTo(full.Count));
                    for (var i = 0; i < prefix.Count; i++)
                    {
                        Assert.That(prefix[i].Lsn, Is.EqualTo(full[i].Lsn), $"truncated read at {k} must be a prefix of the full decode");
                        Assert.That(prefix[i].Kind, Is.EqualTo(full[i].Kind));
                        Assert.That(prefix[i].EntityId, Is.EqualTo(full[i].EntityId));
                    }
                }
            });
    }

    [Test]
    public void HandleRanges_AreZeroed_RestOfPayloadPreserved()
    {
        (from len in Gen.Int[1, 64]
         from payload in Gen.Byte.Array[len]
         from rangeCount in Gen.Int[0, 4]
         from ranges in GenRanges(len).Array[rangeCount]
         from id in GenEntityId
         from cid in GenU16
         select (payload, ranges, id, cid))
            .Sample(t =>
            {
                var (payload, ranges, id, cid) = t;

                var packed = ranges.Select(r => ((uint)r.offset << 16) | (uint)r.length).ToArray();

                var arena = new CommitBatchArena();
                var b = new CommitBatchBuilder(arena, tsn: 7, uowEpoch: 3);
                b.AddSlot(id, cid, payload, packed);
                var size = RecordCodec.Measure(b, out _, out _);
                var buf = new byte[size];
                RecordCodec.Write(buf, b, firstLsn: 1);

                var decoded = ReadAll(buf);
                Assert.That(decoded.Count, Is.EqualTo(1));

                // Expected payload: a copy with the handle ranges zeroed.
                var expected = (byte[])payload.Clone();
                foreach (var (offset, length) in ranges)
                {
                    expected.AsSpan(offset, length).Clear();
                }

                Assert.That(decoded[0].Payload, Is.EqualTo(expected).AsCollection, "codec must zero exactly the handle ranges and preserve the rest (LOG-06)");
            });
    }

    private static Gen<(int offset, int length)> GenRanges(int payloadLen) =>
        from offset in Gen.Int[0, payloadLen - 1]
        from length in Gen.Int[0, payloadLen - offset]
        select (offset, length);

    [Test]
    public void UnknownKind_IsSkipped_AndSurroundingRecordsParse()
    {
        // Hand-build a chunk body: [known Slot] [unknown kind=200] [known Slot]. The reader must skip the unknown by
        // BodyLength and still parse the records on either side (02 §4 / property 4).
        var slotA = BuildRawSlot(lsn: 10, tsn: 100, entityId: 0xAABB, compTypeId: 5, payload: [1, 2, 3]);
        var unknown = BuildRawUnknown(lsn: 11, tsn: 100, kind: 200, body: [9, 9, 9, 9, 9]);
        var slotC = BuildRawSlot(lsn: 12, tsn: 100, entityId: 0xCCDD, compTypeId: 6, payload: [7]);

        var body = new List<byte>();
        body.AddRange(slotA);
        body.AddRange(unknown);
        body.AddRange(slotC);

        var chunk = WrapChunk(body.ToArray());

        var reader = new RecordCodec.RecordBatchReader(chunk);
        var results = new List<(bool unknown, long lsn, long entity, RecordKind kind)>();
        while (reader.TryRead(out var v))
        {
            results.Add((v.IsUnknownKind, v.Lsn, v.EntityId, v.Kind));
        }

        Assert.That(results.Count, Is.EqualTo(3), "all three records must be visited");
        Assert.That(results[0].unknown, Is.False);
        Assert.That(results[0].entity, Is.EqualTo(0xAABB));
        Assert.That(results[0].kind, Is.EqualTo(RecordKind.Slot));
        Assert.That(results[1].unknown, Is.True, "the kind=200 record must be flagged unknown and skipped");
        Assert.That(results[1].lsn, Is.EqualTo(11));
        Assert.That(results[2].unknown, Is.False);
        Assert.That(results[2].entity, Is.EqualTo(0xCCDD));
        Assert.That(results[2].kind, Is.EqualTo(RecordKind.Slot));
    }

    // ── Raw byte builders for the unknown-kind test ─────────────────────────────

    private static byte[] BuildRawSlot(long lsn, long tsn, long entityId, ushort compTypeId, byte[] payload)
    {
        var body = new byte[SlotRecordBody.FixedSize + payload.Length];
        BinaryPrimitives.WriteInt64LittleEndian(body.AsSpan(SlotRecordBody.EntityIdOffset), entityId);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(SlotRecordBody.ComponentTypeIdOffset), compTypeId);
        body[SlotRecordBody.OpOffset] = (byte)SlotOp.Upsert;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(SlotRecordBody.PayloadLengthOffset), (ushort)payload.Length);
        payload.CopyTo(body.AsSpan(SlotRecordBody.FixedSize));
        return BuildRawRecord(lsn, tsn, (byte)RecordKind.Slot, (byte)(RecordFlags.TxBegin | RecordFlags.TxCommit), body);
    }

    private static byte[] BuildRawUnknown(long lsn, long tsn, byte kind, byte[] body) =>
        BuildRawRecord(lsn, tsn, kind, (byte)RecordFlags.None, body);

    private static byte[] BuildRawRecord(long lsn, long tsn, byte kind, byte flags, byte[] body)
    {
        var rec = new byte[RecordHeader.SizeInBytes + body.Length];
        BinaryPrimitives.WriteInt64LittleEndian(rec, lsn);
        BinaryPrimitives.WriteInt64LittleEndian(rec.AsSpan(8), tsn);
        BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(16), 0);
        rec[18] = kind;
        rec[19] = flags;
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
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), 0); // PrevCRC
        chunkBody.CopyTo(buf.AsSpan(WalChunkHeader.SizeInBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(chunkSize - WalChunkFooter.SizeInBytes), 0); // footer CRC
        return buf;
    }
}
