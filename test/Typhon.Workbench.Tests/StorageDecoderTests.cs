using System;
using NUnit.Framework;
using Typhon.Schema.Definition;
using Typhon.Workbench.Storage.Decoders;

namespace Typhon.Workbench.Tests;

// Unit tests for the Database File Map's L4 decoders (Module 15 Track A, A2): the field-value formatter and the
// generic / page-directory decoders. The component decoder is exercised end-to-end in StorageMapDetailTests
// (it needs a live DBComponentDefinition).
[TestFixture]
public sealed class StorageDecoderTests
{
    [Test]
    public void FieldFormatter_DecodesScalarsExactly()
    {
        Assert.That(StorageFieldFormatter.Format(FieldType.Int, BitConverter.GetBytes(-42)), Is.EqualTo("-42"));
        Assert.That(StorageFieldFormatter.Format(FieldType.UInt, BitConverter.GetBytes(7u)), Is.EqualTo("7"));
        Assert.That(StorageFieldFormatter.Format(FieldType.Long, BitConverter.GetBytes(9000000000L)), Is.EqualTo("9000000000"));
        Assert.That(StorageFieldFormatter.Format(FieldType.Float, BitConverter.GetBytes(1.5f)), Is.EqualTo("1.5"));
        Assert.That(StorageFieldFormatter.Format(FieldType.Boolean, new byte[] { 1 }), Is.EqualTo("true"));
        Assert.That(StorageFieldFormatter.Format(FieldType.Boolean, new byte[] { 0 }), Is.EqualTo("false"));
    }

    [Test]
    public void FieldFormatter_DecodesInlineStringTrimmedAtNul()
    {
        var bytes = new byte[64];
        "hello"u8.CopyTo(bytes);
        Assert.That(StorageFieldFormatter.Format(FieldType.String64, bytes), Is.EqualTo("\"hello\""));
    }

    [Test]
    public void FieldFormatter_DecodesPoint3F()
    {
        var bytes = new byte[12];
        BitConverter.GetBytes(1f).CopyTo(bytes, 0);
        BitConverter.GetBytes(2f).CopyTo(bytes, 4);
        BitConverter.GetBytes(3f).CopyTo(bytes, 8);
        Assert.That(StorageFieldFormatter.Format(FieldType.Point3F, bytes), Is.EqualTo("(1, 2, 3)"));
    }

    [Test]
    public void FieldFormatter_FallsBackToHexForUnknownTypes()
    {
        var value = StorageFieldFormatter.Format(FieldType.AABB3F, new byte[] { 0xDE, 0xAD });
        Assert.That(value, Does.StartWith("0x"));
    }

    [Test]
    public void GenericDecoder_ProducesByteRunCells()
    {
        var cells = L4Decoder.DecodeGeneric(new byte[256]);

        Assert.That(cells, Is.Not.Empty);
        Assert.That(cells, Has.All.Property("Kind").EqualTo("byteRun"));
        // An all-zero buffer classifies as 'zero'.
        Assert.That(cells[0].Value, Is.EqualTo("zero"));
    }

    [Test]
    public void GenericDecoder_EmptyChunkProducesNoCells()
    {
        Assert.That(L4Decoder.DecodeGeneric(ReadOnlySpan<byte>.Empty), Is.Empty);
    }

    [Test]
    public void ClusterDecoder_EmitsOneCellPerSlotWithOccupancyAndEnabledMask()
    {
        // N=4 slots, 2 components → headerSize = 8 + 8*2 = 24, entityIdsOffset = 24, chunk = 24 + 4*8 = 56 bytes.
        const int n = 4;
        const int componentCount = 2;
        const int entityIdsOffset = 24;
        var chunk = new byte[entityIdsOffset + n * 8];

        // OccupancyBits @0: slots 0 and 2 occupied (0b0101).
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(0), 0b0101);
        // EnabledBits[0] @8: component 0 enabled for slots 0 and 2.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(8), 0b0101);
        // EnabledBits[1] @16: component 1 enabled for slot 0 only.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(chunk.AsSpan(16), 0b0001);
        // EntityIds @24: slot 0 = 1001, slot 2 = 2002 (free slots 1, 3 left zero).
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(entityIdsOffset + 0 * 8), 1001);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(entityIdsOffset + 2 * 8), 2002);

        var cells = L4Decoder.DecodeCluster(chunk, n, componentCount, entityIdsOffset);

        Assert.That(cells.Length, Is.EqualTo(n));
        Assert.That(cells, Has.All.Property("Kind").EqualTo("entitySlot"));

        // Slot 0 — occupied, both components enabled (mask 0b11), entity id 1001.
        Assert.That(cells[0].ColorKey, Is.EqualTo(1));
        Assert.That(cells[0].Value, Is.EqualTo("1001"));
        Assert.That(cells[0].EnabledMask, Is.EqualTo(0b11L));

        // Slot 1 — free.
        Assert.That(cells[1].ColorKey, Is.EqualTo(0));
        Assert.That(cells[1].Value, Is.EqualTo("—"));
        Assert.That(cells[1].EnabledMask, Is.EqualTo(0L));

        // Slot 2 — occupied, only component 0 enabled (mask 0b01), entity id 2002.
        Assert.That(cells[2].ColorKey, Is.EqualTo(1));
        Assert.That(cells[2].Value, Is.EqualTo("2002"));
        Assert.That(cells[2].EnabledMask, Is.EqualTo(0b01L));

        // Slot 3 — free.
        Assert.That(cells[3].ColorKey, Is.EqualTo(0));
        Assert.That(cells[3].EnabledMask, Is.EqualTo(0L));
    }

    [Test]
    public void ClusterDecoder_EmptyChunkProducesNoCells()
    {
        Assert.That(L4Decoder.DecodeCluster(ReadOnlySpan<byte>.Empty, 4, 2, 24), Is.Empty);
    }

    [Test]
    public void VsbsDecoder_ReportsElementCountCapacityAndChainLink()
    {
        // stride 64, elementSize 4 → capacity = (64 − 8) / 4 = 14. Header { NextChunkId @0, ElementCount @4 }.
        const int stride = 64;
        const int elementSize = 4;
        var chunk = new byte[stride];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0), 42); // NextChunkId
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), 10); // ElementCount

        var cells = L4Decoder.DecodeVsbs(chunk, elementSize, stride);

        Assert.That(cells.Length, Is.EqualTo(4));
        Assert.That(Array.Find(cells, c => c.Label == "Elements")!.Value, Is.EqualTo("10"));
        Assert.That(Array.Find(cells, c => c.Label == "Capacity / chunk")!.Value, Is.EqualTo("14"));
        Assert.That(Array.Find(cells, c => c.Label == "Next chunk")!.Value, Is.EqualTo("#42"));
    }

    [Test]
    public void VsbsDecoder_EndOfChainShownAsEnd()
    {
        var chunk = new byte[64]; // NextChunkId 0 = chain end
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), 3);
        var cells = L4Decoder.DecodeVsbs(chunk, 4, 64);
        Assert.That(Array.Find(cells, c => c.Label == "Next chunk")!.Value, Does.Contain("chain end"));
    }

    [Test]
    public void StringDecoder_DecodesUtf8PreviewAndChainLink()
    {
        // stride 32 → blockSize 24. Header { SizeLeft @0, NextChunkId @4 }, payload @8.
        const int stride = 32;
        var chunk = new byte[stride];
        var payload = "héllo"u8.ToArray(); // 6 UTF-8 bytes
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0), payload.Length); // SizeLeft (this chunk only)
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), 0);              // NextChunkId — chain end
        payload.CopyTo(chunk.AsSpan(8));

        var cells = L4Decoder.DecodeString(chunk, stride);

        Assert.That(cells.Length, Is.EqualTo(4));
        Assert.That(Array.Find(cells, c => c.Label == "Preview")!.Value, Is.EqualTo("héllo"));
        Assert.That(Array.Find(cells, c => c.Label == "Bytes from here")!.Value, Is.EqualTo($"{payload.Length} B"));
        Assert.That(Array.Find(cells, c => c.Label == "This chunk")!.Value, Is.EqualTo($"{payload.Length} / 24 B"));
        Assert.That(Array.Find(cells, c => c.Label == "Next chunk")!.Value, Does.Contain("chain end"));
    }

    [Test]
    public void StringDecoder_MidChainChunkPreviewsOnlyItsBlock()
    {
        // SizeLeft (24) exceeds blockSize (24-8... here blockSize = 16): this chunk holds a full block, more follows.
        const int stride = 24; // blockSize = 16
        var chunk = new byte[stride];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0), 40);  // 40 bytes remain from here → this chunk is full
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), 7);   // continues at #7
        "ABCDEFGHIJKLMNOP"u8.ToArray().CopyTo(chunk.AsSpan(8)); // 16 bytes fill the block

        var cells = L4Decoder.DecodeString(chunk, stride);

        Assert.That(Array.Find(cells, c => c.Label == "This chunk")!.Value, Is.EqualTo("16 / 16 B"));
        Assert.That(Array.Find(cells, c => c.Label == "Preview")!.Value, Is.EqualTo("ABCDEFGHIJKLMNOP"));
        Assert.That(Array.Find(cells, c => c.Label == "Next chunk")!.Value, Is.EqualTo("#7"));
    }

    [Test]
    public void VsbsAndStringDecoders_ShortChunkProduceNoCells()
    {
        Assert.That(L4Decoder.DecodeVsbs(new byte[4], 4, 64), Is.Empty);
        Assert.That(L4Decoder.DecodeString(new byte[4], 32), Is.Empty);
    }

    [Test]
    public void HashMapDecoder_MetaChunk_UnpacksLevelNextBucketCountAndEntries()
    {
        // Meta chunk: PackedMeta @+8 = Level(1)|Next(2)|BucketCount(8), EntryCount @+16 = 50, DirectoryChunkCount @+24 = 1.
        var chunk = new byte[64];
        var packed = (1L << 56) | (2L << 32) | 8L;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(8), packed);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(chunk.AsSpan(16), 50);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(24), 1);

        var cells = L4Decoder.DecodeHashMap(chunk, isMeta: true, isDirectory: false, bucketCapacity: 9);

        Assert.That(Array.Find(cells, c => c.Label == "Role")!.Value, Is.EqualTo("Meta"));
        Assert.That(Array.Find(cells, c => c.Label == "Buckets")!.Value, Is.EqualTo("8"));
        Assert.That(Array.Find(cells, c => c.Label == "Total entries")!.Value, Is.EqualTo("50"));
        Assert.That(Array.Find(cells, c => c.Label == "Level")!.Value, Is.EqualTo("1"));
        Assert.That(Array.Find(cells, c => c.Label == "Split pointer")!.Value, Is.EqualTo("2"));
        Assert.That(Array.Find(cells, c => c.Label == "Directory chunks")!.Value, Is.EqualTo("1"));
    }

    [Test]
    public void HashMapDecoder_PrimaryBucket_ReportsEntriesOverCapacityAndNoOverflow()
    {
        // Bucket header: OlcVersion @+0 = 4 (primary), EntryCount @+4 = 5, OverflowChunkId @+8 = -1 (no chain).
        var chunk = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0), 4);
        chunk[4] = 5;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), -1);

        var cells = L4Decoder.DecodeHashMap(chunk, isMeta: false, isDirectory: false, bucketCapacity: 9);

        Assert.That(Array.Find(cells, c => c.Label == "Role")!.Value, Is.EqualTo("Bucket"));
        Assert.That(Array.Find(cells, c => c.Label == "Entries")!.Value, Is.EqualTo("5 / 9"));
        Assert.That(Array.Find(cells, c => c.Label == "Overflow")!.Value, Does.Contain("none"));
    }

    [Test]
    public void HashMapDecoder_OverflowChunk_IdentifiedByZeroOlcVersion()
    {
        // An overflow chunk carries OlcVersion == 0 (not independently latched).
        var chunk = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0), 0);
        chunk[4] = 3;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), -1);

        var cells = L4Decoder.DecodeHashMap(chunk, isMeta: false, isDirectory: false, bucketCapacity: 9);

        Assert.That(Array.Find(cells, c => c.Label == "Role")!.Value, Is.EqualTo("Overflow"));
        Assert.That(Array.Find(cells, c => c.Label == "Entries")!.Value, Is.EqualTo("3 / 9"));
    }

    [Test]
    public void HashMapDecoder_PrimaryWithOverflow_ShowsChainLink()
    {
        var chunk = new byte[64];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0), 4);
        chunk[4] = 9;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), 42); // chains to #42

        var cells = L4Decoder.DecodeHashMap(chunk, isMeta: false, isDirectory: false, bucketCapacity: 9);

        Assert.That(Array.Find(cells, c => c.Label == "Role")!.Value, Is.EqualTo("Bucket"));
        Assert.That(Array.Find(cells, c => c.Label == "Overflow")!.Value, Is.EqualTo("#42"));
    }

    [Test]
    public void HashMapDecoder_DirectoryChunk_ReportsDirectoryRole()
    {
        var cells = L4Decoder.DecodeHashMap(new byte[64], isMeta: false, isDirectory: true, bucketCapacity: 9);
        Assert.That(Array.Find(cells, c => c.Label == "Role")!.Value, Is.EqualTo("Directory"));
    }

    [Test]
    public void HashMapDecoder_ShortChunkProducesNoCells()
    {
        Assert.That(L4Decoder.DecodeHashMap(new byte[4], isMeta: false, isDirectory: false, bucketCapacity: 9), Is.Empty);
    }

    [Test]
    public void IndexDecoder_DirectoryChunk_ListsTrees()
    {
        var trees = new (short StableId, int RootChunkId, int EntryCount)[]
        {
            ((short)-1, 10, 5000),  // PK
            ((short)42, 15, 2000),  // secondary on field 42
        };

        var cells = L4Decoder.DecodeIndex(new byte[256], chunkId: 0, directoryChunkCount: 4, trees);

        Assert.That(Array.Find(cells, c => c.Label == "B-trees")!.Value, Is.EqualTo("2"));
        Assert.That(Array.Find(cells, c => c.Label == "Primary key")!.Value, Does.Contain("root #10"));
        Assert.That(Array.Find(cells, c => c.Label == "Primary key")!.Value, Does.Contain("5000"));
        Assert.That(Array.Find(cells, c => c.Label == "Field #42")!.Value, Does.Contain("root #15"));
    }

    [Test]
    public void IndexDecoder_LeafNode_ReportsLeafCountAndSiblings()
    {
        // Control word: Count = byte 3, IsLeaf = bit 1.
        var chunk = new byte[256];
        var control = (12 << 24) | 0x02;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0), control);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(8), 7);  // prev sibling
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(12), 9); // next sibling

        var cells = L4Decoder.DecodeIndex(chunk, chunkId: 5, directoryChunkCount: 4, []);

        Assert.That(Array.Find(cells, c => c.Label == "Role")!.Value, Is.EqualTo("Leaf"));
        Assert.That(Array.Find(cells, c => c.Label == "Entries")!.Value, Is.EqualTo("12"));
        Assert.That(Array.Find(cells, c => c.Label == "Prev sibling")!.Value, Is.EqualTo("#7"));
        Assert.That(Array.Find(cells, c => c.Label == "Next sibling")!.Value, Is.EqualTo("#9"));
        Assert.That(Array.Exists(cells, c => c.Label == "Leftmost child"), Is.False, "a leaf has no child link");
    }

    [Test]
    public void IndexDecoder_InternalNode_ShowsLeftmostChild()
    {
        var chunk = new byte[256];
        var control = 5 << 24; // Count = 5, IsLeaf clear ⇒ internal
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(0), control);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(16), 21); // leftmost child

        var cells = L4Decoder.DecodeIndex(chunk, chunkId: 5, directoryChunkCount: 4, []);

        Assert.That(Array.Find(cells, c => c.Label == "Role")!.Value, Is.EqualTo("Internal"));
        Assert.That(Array.Find(cells, c => c.Label == "Leftmost child")!.Value, Is.EqualTo("#21"));
    }

    [Test]
    public void IndexDecoder_ShortNodeChunkProducesNoCells()
    {
        Assert.That(L4Decoder.DecodeIndex(new byte[8], chunkId: 5, directoryChunkCount: 4, []), Is.Empty);
    }

    [Test]
    public void DirectoryDecoder_MapsLogicalToPhysicalPages()
    {
        var cells = L4Decoder.DecodeDirectory(new[] { 5, 9, 12 });

        Assert.That(cells.Length, Is.EqualTo(3));
        Assert.That(cells[0].Kind, Is.EqualTo("dirEntry"));
        Assert.That(cells[0].Label, Is.EqualTo("logical 0"));
        Assert.That(cells[0].Value, Is.EqualTo("page 5"));
        Assert.That(cells[2].Value, Is.EqualTo("page 12"));
        Assert.That(cells[1].ColorKey, Is.EqualTo(9), "the colour key is the physical page");
    }
}
