using NUnit.Framework;
using System;
using System.Buffers.Binary;

namespace Typhon.Engine.Tests;

/// <summary>
/// Codec tests for the columnar tick-fence record (<see cref="RecordKind.FenceBlock"/>, #559). Proves the write/read pair is
/// lossless for the entity-key column and every component column, that a partially-dirty range survives round-trip, and that a
/// malformed or truncated block is rejected rather than mis-parsed. Pure — no engine, no recovery.
/// </summary>
[TestFixture]
[VerifiesRule("LOG-02")]
[VerifiesRule("LOG-06")]
internal sealed class FenceBlockCodecTests
{
    private const long Lsn = 4242;
    private const long Tsn = 77;

    /// <summary>Writes a block whose columns are filled with a deterministic pattern; returns the wire bytes.</summary>
    private static byte[] WriteBlock(
        ushort archetypeId, int clusterChunkId, byte firstSlot, byte slotSpan, ulong dirtyMask,
        int[] slotIndices, int[] componentSizes, out long[] keys, out byte[][] columns)
    {
        var totalCompSize = 0;
        foreach (var s in componentSizes)
        {
            totalCompSize += s;
        }

        var wire = new byte[RecordCodec.FenceBlockWireSize(slotIndices.Length, slotSpan, totalCompSize)];
        var written = RecordCodec.WriteFenceBlockPrefix(
            wire, Lsn, Tsn, uowEpoch: 0, RecordFlags.FenceRecord, archetypeId, clusterChunkId,
            firstSlot, slotSpan, dirtyMask, slotIndices, componentSizes, totalCompSize);

        // Entity-key column — the emitter copies this straight out of the cluster's EntityKeys array.
        keys = new long[slotSpan];
        for (var i = 0; i < slotSpan; i++)
        {
            keys[i] = ((long)clusterChunkId << 32) | (uint)(firstSlot + i);
            BinaryPrimitives.WriteInt64LittleEndian(wire.AsSpan(written + (i * 8)), keys[i]);
        }

        written += slotSpan * 8;

        // Component columns, in descriptor order.
        columns = new byte[slotIndices.Length][];
        for (var c = 0; c < slotIndices.Length; c++)
        {
            var col = new byte[componentSizes[c] * slotSpan];
            for (var b = 0; b < col.Length; b++)
            {
                col[b] = (byte)((c * 31) + b + 1);
            }

            col.CopyTo(wire.AsSpan(written));
            written += col.Length;
            columns[c] = col;
        }

        Assert.That(written, Is.EqualTo(wire.Length), "prefix + columns must exactly fill the measured wire size");
        return wire;
    }

    private static RecordCodec.FenceBlockView Read(byte[] wire)
    {
        Assert.That(RecordCodec.TryReadRecord(wire, 0, out var consumed, out var view), Is.True, "record must parse");
        Assert.That(consumed, Is.EqualTo(wire.Length));
        Assert.That(view.Kind, Is.EqualTo(RecordKind.FenceBlock));
        Assert.That(view.Lsn, Is.EqualTo(Lsn));
        Assert.That(view.Tsn, Is.EqualTo(Tsn));
        Assert.That(view.IsUnknownKind, Is.False, "FenceBlock is a known kind — it must not fall through to the skip path");
        Assert.That(RecordCodec.TryReadFenceBlock(view.Payload, out var block), Is.True);
        return block;
    }

    [Test]
    public void FullCluster_RoundTrips_KeysAndEveryColumn()
    {
        // Guide-sample shape after the Versioned slot is excluded: 4 durable columns over a 46-entity cluster.
        int[] slots = [0, 1, 2, 3];
        int[] sizes = [16, 16, 24, 4];
        var wire = WriteBlock(7, 1234, firstSlot: 0, slotSpan: 46, dirtyMask: (1UL << 46) - 1, slots, sizes, out var keys, out var columns);

        var block = Read(wire);

        // NOTE: no Assert.Multiple here — FenceBlockView is a ref struct and cannot be captured by a lambda (CS8175).
        Assert.That(block.ArchetypeId, Is.EqualTo(7));
        Assert.That(block.ClusterChunkId, Is.EqualTo(1234));
        Assert.That(block.FirstSlot, Is.EqualTo(0));
        Assert.That(block.SlotSpan, Is.EqualTo(46));
        Assert.That(block.ColumnCount, Is.EqualTo(4));
        Assert.That(block.DirtyMask, Is.EqualTo((1UL << 46) - 1));

        for (var i = 0; i < 46; i++)
        {
            Assert.That(block.EntityKeyAt(i), Is.EqualTo(keys[i]), $"entity key at range index {i}");
            Assert.That(block.IsDirtyAt(i), Is.True);
        }

        for (var c = 0; c < slots.Length; c++)
        {
            Assert.That(block.SlotIndexOf(c), Is.EqualTo(slots[c]));
            Assert.That(block.ComponentSizeOf(c), Is.EqualTo(sizes[c]));
            Assert.That(block.Column(c).ToArray(), Is.EqualTo(columns[c]), $"column {c} bytes");
        }
    }

    [Test]
    public void PartialRange_PreservesFirstSlotAndDirtyMask()
    {
        // A sparse tick: only cluster slots 5..12 changed, so the emitter sends that range with a mask over it.
        int[] slots = [2];
        int[] sizes = [8];
        const ulong mask = 0b1010_1101;   // 5 of the 8 entities in the range were dirty
        var wire = WriteBlock(3, 99, firstSlot: 5, slotSpan: 8, mask, slots, sizes, out var keys, out var columns);

        var block = Read(wire);

        Assert.That(block.FirstSlot, Is.EqualTo(5));
        Assert.That(block.SlotSpan, Is.EqualTo(8));
        Assert.That(block.DirtyMask, Is.EqualTo(mask));
        Assert.That(block.Column(0).ToArray(), Is.EqualTo(columns[0]));

        for (var i = 0; i < 8; i++)
        {
            Assert.That(block.EntityKeyAt(i), Is.EqualTo(keys[i]));
            Assert.That(block.IsDirtyAt(i), Is.EqualTo((mask & (1UL << i)) != 0), $"dirty bit {i}");
        }
    }

    [Test]
    public void SingleEntity_SingleColumn_IsTheDegenerateCase()
    {
        int[] slots = [0];
        int[] sizes = [16];
        var wire = WriteBlock(1, 0, firstSlot: 63, slotSpan: 1, dirtyMask: 1, slots, sizes, out var keys, out var columns);

        var block = Read(wire);

        Assert.That(block.FirstSlot, Is.EqualTo(63));
        Assert.That(block.SlotSpan, Is.EqualTo(1));
        Assert.That(block.EntityKeyAt(0), Is.EqualTo(keys[0]));
        Assert.That(block.ValueAt(0, 0).ToArray(), Is.EqualTo(columns[0]));
    }

    [Test]
    public void ValueAt_MatchesTheColumnSlice()
    {
        int[] slots = [0, 1];
        int[] sizes = [4, 12];
        var wire = WriteBlock(2, 8, firstSlot: 0, slotSpan: 10, dirtyMask: ulong.MaxValue, slots, sizes, out _, out var columns);

        var block = Read(wire);

        for (var c = 0; c < slots.Length; c++)
        {
            for (var i = 0; i < 10; i++)
            {
                var expected = new byte[sizes[c]];
                Array.Copy(columns[c], i * sizes[c], expected, 0, sizes[c]);
                Assert.That(block.ValueAt(c, i).ToArray(), Is.EqualTo(expected), $"column {c}, entity {i}");
            }
        }
    }

    [Test]
    public void MeasuredSize_IsExact()
    {
        Assert.Multiple(() =>
        {
            // 18 fixed + 4*columns descriptors + span*(8 key + payload)
            Assert.That(RecordCodec.FenceBlockBodyLength(4, 46, 60), Is.EqualTo(18 + 16 + (46 * 68)));
            Assert.That(RecordCodec.FenceBlockWireSize(4, 46, 60), Is.EqualTo(24 + 18 + 16 + (46 * 68)));
            Assert.That(RecordCodec.FenceBlockBodyLength(0, 1, 0), Is.EqualTo(18 + 8));
        });
    }

    [Test]
    public void TruncatedBody_IsRejected_NotMisparsed()
    {
        int[] slots = [0, 1];
        int[] sizes = [16, 8];
        var wire = WriteBlock(5, 11, firstSlot: 0, slotSpan: 20, dirtyMask: ulong.MaxValue, slots, sizes, out _, out _);

        // Chop the last column short but leave the header's BodyLength claiming the full size: the record must not parse.
        var truncated = wire.AsSpan(0, wire.Length - 8).ToArray();
        Assert.That(RecordCodec.TryReadRecord(truncated, 0, out _, out _), Is.False, "a body shorter than BodyLength is torn");

        // Now make BodyLength agree with the shortened buffer — the block's own descriptor arithmetic must still reject it.
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(20), (uint)(truncated.Length - RecordHeader.SizeInBytes));
        Assert.That(RecordCodec.TryReadRecord(truncated, 0, out _, out _), Is.False, "descriptor sizes must not agree with a short body");
    }

    [Test]
    public void ZeroOrOversizedSlotSpan_IsRejected()
    {
        int[] slots = [0];
        int[] sizes = [4];
        var wire = WriteBlock(1, 1, firstSlot: 0, slotSpan: 4, dirtyMask: 0xF, slots, sizes, out _, out _);

        var zeroSpan = (byte[])wire.Clone();
        zeroSpan[RecordHeader.SizeInBytes + FenceBlockRecordBody.SlotSpanOffset] = 0;
        Assert.That(RecordCodec.TryReadRecord(zeroSpan, 0, out _, out _), Is.False, "slotSpan 0 is malformed");

        var hugeSpan = (byte[])wire.Clone();
        hugeSpan[RecordHeader.SizeInBytes + FenceBlockRecordBody.SlotSpanOffset] = 65;
        Assert.That(RecordCodec.TryReadRecord(hugeSpan, 0, out _, out _), Is.False, "a cluster holds at most 64 entity slots");
    }
}
