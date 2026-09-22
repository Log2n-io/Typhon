using System.Collections.Generic;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

[TestFixture]
internal class SpatialNodeDescriptorTests
{
    private static IEnumerable<TestCaseData> AllVariants()
    {
        yield return new TestCaseData(SpatialNodeDescriptor.R2Df32).SetName("R2Df32");
        yield return new TestCaseData(SpatialNodeDescriptor.R3Df32).SetName("R3Df32");
        yield return new TestCaseData(SpatialNodeDescriptor.R2Df64).SetName("R2Df64");
        yield return new TestCaseData(SpatialNodeDescriptor.R3Df64).SetName("R3Df64");
    }

    /// <summary>
    /// The four variants' leaf and internal fan-outs, stated as literals so a layout change has to be acknowledged here.
    /// </summary>
    /// <remarks>
    /// <para><b>Leaf entry layout: coords + payloadId(8B) + CategoryMask(4B).</b> A 4-byte <c>ComponentChunkId</c> sat between the last two until #872 step
    /// 13 removed the entity-level tree that was its only consumer — the cluster trees passed zero. <c>AC-13.5</c> is the arithmetic below.</para>
    /// <para><b>Only the f32 variants moved, and that is not an accident of rounding.</b> <c>LeafCapacity</c> is <c>EntryArea / entrySize</c> floored, so
    /// four fewer bytes per entry buys a slot only when the division crosses an integer. R3Df64's 704-byte entry area gives <c>704/64 = 11</c> before and
    /// <c>704/60 = 11</c> after — the freed bytes are not yet a whole entry. That is why AC-13.5 asks for the f32 numbers and states R3Df64 as unchanged
    /// rather than quietly omitting it.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("SH-02")]
    public void KnownCapacities_MatchDesignDoc()
    {
        Assert.Multiple(() =>
        {
            // 480 entry bytes / 28 = 17 (was 480 / 32 = 15).
            Assert.That(SpatialNodeDescriptor.R2Df32.LeafCapacity, Is.EqualTo(17), "R2Df32 LeafCap");
            Assert.That(SpatialNodeDescriptor.R2Df32.InternalCapacity, Is.EqualTo(24), "R2Df32 InternalCap");
            Assert.That(SpatialNodeDescriptor.R2Df32.MinFill, Is.EqualTo(7), "R2Df32 MinFill");

            // 472 / 36 = 13 (was 472 / 40 = 11).
            Assert.That(SpatialNodeDescriptor.R3Df32.LeafCapacity, Is.EqualTo(13), "R3Df32 LeafCap");
            Assert.That(SpatialNodeDescriptor.R3Df32.InternalCapacity, Is.EqualTo(16), "R3Df32 InternalCap");
            Assert.That(SpatialNodeDescriptor.R3Df32.MinFill, Is.EqualTo(6), "R3Df32 MinFill");

            // 464 / 44 = 10 (was 464 / 48 = 9).
            Assert.That(SpatialNodeDescriptor.R2Df64.LeafCapacity, Is.EqualTo(10), "R2Df64 LeafCap");
            Assert.That(SpatialNodeDescriptor.R2Df64.InternalCapacity, Is.EqualTo(12), "R2Df64 InternalCap");
            Assert.That(SpatialNodeDescriptor.R2Df64.MinFill, Is.EqualTo(4), "R2Df64 MinFill");

            // 704 / 60 = 11, unchanged from 704 / 64 = 11 — see the remarks.
            Assert.That(SpatialNodeDescriptor.R3Df64.LeafCapacity, Is.EqualTo(11), "R3Df64 LeafCap");
            Assert.That(SpatialNodeDescriptor.R3Df64.InternalCapacity, Is.EqualTo(13), "R3Df64 InternalCap");
            Assert.That(SpatialNodeDescriptor.R3Df64.MinFill, Is.EqualTo(5), "R3Df64 MinFill");
        });
    }

    /// <summary>
    /// <c>AC-13.5</c> — the leaf entry is exactly the coordinates, an 8-byte payload id and a 4-byte category mask, with nothing between them.
    /// </summary>
    /// <remarks>
    /// The capacities above are a CONSEQUENCE of the entry size; this asserts the entry size itself, so a future change that removes one field and adds
    /// another of the same width cannot leave the fan-out numbers looking right. It is stated as offsets rather than as a size because that is what a reader
    /// of the SOA arrays actually depends on: the category-mask array must begin exactly one payload-id array after the ids.
    /// </remarks>
    [Test]
    [VerifiesRule("SH-02")]
    [TestCaseSource(nameof(AllVariants))]
    public void LeafSoaLayout_HasNoGapBetweenPayloadIdsAndCategoryMasks(SpatialNodeDescriptor desc)
    {
        Assert.Multiple(() =>
        {
            Assert.That(desc.LeafIdOffset, Is.EqualTo(desc.LeafCoordOffsets + (desc.CoordCount * desc.LeafCoordStride)),
                "the payload-id array must start immediately after the coordinate arrays");
            Assert.That(desc.LeafCategoryMaskOffset, Is.EqualTo(desc.LeafIdOffset + (desc.LeafCapacity * desc.LeafIdSize)),
                "the category-mask array must start immediately after the payload-id array — a gap here is a column that was removed from the arithmetic "
                + "but not from the layout");
            Assert.That(desc.LeafCategoryMaskOffset + (desc.LeafCapacity * desc.LeafCategoryMaskSize), Is.LessThanOrEqualTo(desc.Stride),
                "the leaf entry area overruns the node stride");
        });
    }

    [Test]
    public void HeaderSizes_MatchDesignDoc()
    {
        // Header: OlcVersion(4) + Control(4) + ParentChunkId(4) + NodeMBR(CoordCount*CoordSize) + UnionCategoryMask(4)
        Assert.That(SpatialNodeDescriptor.R2Df32.HeaderSize, Is.EqualTo(32), "R2Df32 header");  // 12+16+4=32
        Assert.That(SpatialNodeDescriptor.R3Df32.HeaderSize, Is.EqualTo(40), "R3Df32 header");  // 12+24+4=40
        Assert.That(SpatialNodeDescriptor.R2Df64.HeaderSize, Is.EqualTo(48), "R2Df64 header");  // 12+32+4=48
        Assert.That(SpatialNodeDescriptor.R3Df64.HeaderSize, Is.EqualTo(64), "R3Df64 header");  // 12+48+4=64
    }

    [Test]
    public void CoordParameters_MatchVariant()
    {
        Assert.That(SpatialNodeDescriptor.R2Df32.CoordCount, Is.EqualTo(4), "R2Df32 coords");
        Assert.That(SpatialNodeDescriptor.R2Df32.CoordSize, Is.EqualTo(4), "R2Df32 coord size");

        Assert.That(SpatialNodeDescriptor.R3Df32.CoordCount, Is.EqualTo(6), "R3Df32 coords");
        Assert.That(SpatialNodeDescriptor.R3Df32.CoordSize, Is.EqualTo(4), "R3Df32 coord size");

        Assert.That(SpatialNodeDescriptor.R2Df64.CoordCount, Is.EqualTo(4), "R2Df64 coords");
        Assert.That(SpatialNodeDescriptor.R2Df64.CoordSize, Is.EqualTo(8), "R2Df64 coord size");

        Assert.That(SpatialNodeDescriptor.R3Df64.CoordCount, Is.EqualTo(6), "R3Df64 coords");
        Assert.That(SpatialNodeDescriptor.R3Df64.CoordSize, Is.EqualTo(8), "R3Df64 coord size");
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void LeafLayout_FitsWithinStride(SpatialNodeDescriptor desc)
    {
        int leafEnd = desc.LeafCategoryMaskOffset + desc.LeafCapacity * desc.LeafCategoryMaskSize;
        Assert.That(leafEnd, Is.LessThanOrEqualTo(desc.Stride),
            $"Leaf data overflows stride: ends at {leafEnd}, stride is {desc.Stride}");
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void InternalLayout_FitsWithinStride(SpatialNodeDescriptor desc)
    {
        int internalEnd = desc.InternalIdOffset + desc.InternalCapacity * desc.InternalIdSize;
        Assert.That(internalEnd, Is.LessThanOrEqualTo(desc.Stride),
            $"Internal data overflows stride: ends at {internalEnd}, stride is {desc.Stride}");
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void Leaf_SOA_NoOverlap(SpatialNodeDescriptor desc)
    {
        // Each coord array: [LeafCoordOffsets + i * LeafCoordStride, + LeafCoordStride)
        // EntityId array: [LeafIdOffset, + LeafCapacity * 8)
        for (int i = 0; i < desc.CoordCount; i++)
        {
            int coordStart = desc.LeafCoordOffsets + i * desc.LeafCoordStride;
            int coordEnd = coordStart + desc.LeafCoordStride;

            // Coord arrays must not overlap with each other
            for (int j = i + 1; j < desc.CoordCount; j++)
            {
                int otherStart = desc.LeafCoordOffsets + j * desc.LeafCoordStride;
                Assert.That(coordEnd, Is.LessThanOrEqualTo(otherStart),
                    $"Leaf coord arrays {i} and {j} overlap");
            }

            // Coord arrays must not overlap with EntityId array
            Assert.That(coordEnd, Is.LessThanOrEqualTo(desc.LeafIdOffset),
                $"Leaf coord array {i} overlaps with EntityId array");
        }
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void Internal_SOA_NoOverlap(SpatialNodeDescriptor desc)
    {
        for (int i = 0; i < desc.CoordCount; i++)
        {
            int coordStart = desc.HeaderSize + i * desc.InternalCoordStride;
            int coordEnd = coordStart + desc.InternalCoordStride;

            for (int j = i + 1; j < desc.CoordCount; j++)
            {
                int otherStart = desc.HeaderSize + j * desc.InternalCoordStride;
                Assert.That(coordEnd, Is.LessThanOrEqualTo(otherStart),
                    $"Internal coord arrays {i} and {j} overlap");
            }

            Assert.That(coordEnd, Is.LessThanOrEqualTo(desc.InternalIdOffset),
                $"Internal coord array {i} overlaps with ChunkId array");
        }
    }

    [Test]
    [TestCaseSource(nameof(AllVariants))]
    public void EntryAreaSize_IsStride_MinusHeader(SpatialNodeDescriptor desc)
    {
        Assert.That(desc.EntryAreaSize, Is.EqualTo(desc.Stride - desc.HeaderSize));
    }

    [Test]
    public void ForVariant_ReturnsSameAsPrebuilt()
    {
        Assert.That(SpatialNodeDescriptor.ForVariant(SpatialVariant.R2Df32).LeafCapacity,
            Is.EqualTo(SpatialNodeDescriptor.R2Df32.LeafCapacity));
        Assert.That(SpatialNodeDescriptor.ForVariant(SpatialVariant.R3Df64).LeafCapacity,
            Is.EqualTo(SpatialNodeDescriptor.R3Df64.LeafCapacity));
    }
}
