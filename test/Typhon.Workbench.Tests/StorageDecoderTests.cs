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
