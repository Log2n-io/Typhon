using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Typhon.Engine.Tests;

/// <summary>
/// #954 AC-5 — the replication state block's layout budget, asserted on the compiled types rather than on the design's prose.
/// </summary>
/// <remarks>
/// AC-5 is a per-watched-entity memory bound: at most 64 B hot and 32 B cold. It is worth asserting on <c>Unsafe.SizeOf</c> because the failure mode is
/// silent — adding a field pushes the hot entry to two cache lines, the per-session passes quietly double their line traffic, and nothing else in the suite
/// would notice. The block-size case pins the design's own worked example so the SoA carve cannot drift from the document that specifies it.
/// </remarks>
[TestFixture]
class ReplicationBlockLayoutTests
{
    [Test]
    public void HotEntryIsExactlyOneCacheLine()
    {
        Assert.That(Unsafe.SizeOf<ReplicationHotEntry>(), Is.EqualTo(64),
            "AC-5 bounds the hot entry at one cache line; the per-hit passes read it on every hit");
    }

    [Test]
    public void ColdEntryIsExactlyHalfACacheLine()
    {
        Assert.That(Unsafe.SizeOf<ReplicationColdEntry>(), Is.EqualTo(32),
            "AC-5 bounds the cold entry at 32 B; only the projection pass reads it");
    }

    [Test]
    public void BlockHeaderIsExactlyOneCacheLine()
    {
        Assert.That(Unsafe.SizeOf<ReplicationBlockHeader>(), Is.EqualTo(ReplicationBlockLayout.HeaderSize),
            "the header is padded to a line so hot[0] starts line-aligned");
    }

    /// <summary>
    /// The design's worked example: SWG <c>Creature</c> at N = 21 is 64 + 21 × 96 = 2 080 B. Pinning it here means a change to any of the three sizes has
    /// to be made deliberately, in the document and the code together.
    /// </summary>
    [Test]
    public void BlockSizeMatchesTheDesignsWorkedExample()
    {
        var layout = new ReplicationBlockLayout(slotCount: 21);

        Assert.Multiple(() =>
        {
            Assert.That(layout.BlockSize, Is.EqualTo(2080), "64 + 21 x 96");
            Assert.That(layout.HotOffset, Is.EqualTo(64));
            Assert.That(layout.ColdOffset, Is.EqualTo(64 + (21 * 64)));
            Assert.That(layout.OwnerOffset, Is.EqualTo(layout.BlockSize), "no owner fields declared, so the owner region is empty");
        });
    }

    /// <summary>
    /// The hot region must start and stay cache-line aligned for every slot count, since a hot entry is read per hit and a straddling entry costs two lines.
    /// </summary>
    [Test]
    public void HotAndColdRegionsNeverOverlapAndHotStaysLineAligned()
    {
        for (var n = 1; n <= 64; n++)
        {
            var layout = new ReplicationBlockLayout(slotCount: n);

            Assert.That(layout.HotOffset % 64, Is.Zero, $"hot[0] must be line-aligned at N={n}");
            Assert.That(layout.ColdOffset, Is.GreaterThanOrEqualTo(layout.HotOffset + (n * ReplicationBlockLayout.HotEntrySize)),
                $"cold must start after the whole hot region at N={n}");
            Assert.That(layout.BlockSize, Is.GreaterThanOrEqualTo(layout.ColdOffset + (n * ReplicationBlockLayout.ColdEntrySize)),
                $"the block must contain the whole cold region at N={n}");
        }
    }

    /// <summary>
    /// Blocks are packed in a slab at <c>BlockStride</c>, so the stride — not the raw size — is what has to be a whole number of cache lines. At N = 21 the
    /// raw size is 2 080, which is only 32 B-aligned: packing at that pitch would misalign every block after the first.
    /// </summary>
    [Test]
    public void BlockStrideIsCacheLineAlignedForEverySlotCount()
    {
        for (var n = 1; n <= 64; n++)
        {
            var layout = new ReplicationBlockLayout(slotCount: n);

            Assert.That(layout.BlockStride % 64, Is.Zero, $"a slab packs blocks at the stride, so it must be a whole number of lines at N={n}");
            Assert.That(layout.BlockStride, Is.GreaterThanOrEqualTo(layout.BlockSize), $"the stride must contain the block at N={n}");
            Assert.That(layout.BlockStride - layout.BlockSize, Is.LessThan(64), $"the stride must not waste a whole line at N={n}");
        }
    }

    /// <summary>
    /// The fixed head the baseline struct declares is the one the layout charges every archetype: 8 + 4 + 2 + 2 + 16 before either region starts.
    /// </summary>
    /// <remarks>
    /// The two numbers have to agree or the layout's offsets address the wrong bytes of the struct it describes. Asserting the sum against
    /// <c>Unsafe.SizeOf</c> is what ties the declaration to the arithmetic: adding a field to the head without moving <c>HotFixedBytes</c> fails here rather
    /// than by writing a segment over a group tick.
    /// </remarks>
    [Test]
    public void BaselineRegionsAddUpToTheDeclaredEntrySizes()
    {
        var baseline = new ReplicationBlockLayout(slotCount: 1);

        Assert.Multiple(() =>
        {
            Assert.That(ReplicationBlockLayout.HotFixedBytes + ReplicationBlockLayout.BaselineSegmentBytes + ReplicationBlockLayout.BaselinePackedStateBytes,
                Is.LessThanOrEqualTo(Unsafe.SizeOf<ReplicationHotEntry>()), "the baseline regions must fit the struct that declares them");
            Assert.That(
                ReplicationBlockLayout.ColdFixedBytes + ReplicationBlockLayout.BaselinePrevPositionBytes + ReplicationBlockLayout.BaselineRunStartBytes,
                Is.LessThanOrEqualTo(Unsafe.SizeOf<ReplicationColdEntry>()));
            Assert.That(baseline.HotStride, Is.EqualTo(ReplicationBlockLayout.HotEntrySize), "the baseline layout IS the struct's shape");
            Assert.That(baseline.ColdStride, Is.EqualTo(ReplicationBlockLayout.ColdEntrySize));
            Assert.That(baseline.SegmentOffsetInHotEntry, Is.EqualTo(ReplicationBlockLayout.HotFixedBytes));
            Assert.That(baseline.PackedStateOffsetInHotEntry, Is.EqualTo(ReplicationBlockLayout.HotFixedBytes + ReplicationBlockLayout.BaselineSegmentBytes));
            Assert.That(baseline.LastEventTickOffsetInColdEntry,
                Is.EqualTo(ReplicationBlockLayout.BaselinePrevPositionBytes + ReplicationBlockLayout.BaselineRunStartBytes));
        });
    }

    /// <summary>
    /// An SWG-shaped 2D archetype still lands on AC-5's 64 B hot stride, and a 3D one with four <c>varu</c> state fields lands on 128 B.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the two sides of the design change that made the entries plan-sized. The 2D case is the budget AC-5 states: a 13 B segment (u24 x 2, i16 x 2,
    /// t0, epoch) and a 4 B state body sit inside one line with the 32 B head. The 3D case is what the fixed shape used to overrun silently — 18 B of segment
    /// and 20 B of state is 70 B, so the archetype takes two lines, deliberately and visibly, rather than writing its state over the next entry.
    /// </para>
    /// <para>
    /// The cold entry absorbs both: 3D costs it 9 + 16 + 4 = 29 B, still inside 32.
    /// </para>
    /// </remarks>
    [Test]
    public void HotStrideIsSizedByTheArchetypeAndRoundedToWholeLines()
    {
        var twoD = ReplicationBlockLayout.ForArchetype(slotCount: 21, segmentBytes: 13, packedStateBytes: 4, prevPositionBytes: 6, runStartBytes: 12,
            ownerEntrySize: 0);
        var threeD = ReplicationBlockLayout.ForArchetype(slotCount: 21, segmentBytes: 18, packedStateBytes: 20, prevPositionBytes: 9, runStartBytes: 16,
            ownerEntrySize: 0);

        Assert.Multiple(() =>
        {
            Assert.That(twoD.HotStride, Is.EqualTo(64), "32 + 13 + 4 = 49 B fits one cache line, which is AC-5's bound");
            Assert.That(twoD.ColdStride, Is.EqualTo(32), "4 + 6 + 12 = 22 B fits half a line");
            Assert.That(threeD.HotStride, Is.EqualTo(128), "32 + 18 + 20 = 70 B does not fit one line, so the archetype takes two");
            Assert.That(threeD.ColdStride, Is.EqualTo(32), "4 + 9 + 16 = 29 B still fits half a line");
            Assert.That(threeD.PackedStateOffsetInHotEntry, Is.EqualTo(50), "the state body starts after the head and the segment, wherever they end");
            Assert.That(threeD.ColdOffset, Is.EqualTo(64 + (21 * 128)), "the cold region starts after the whole hot region, at the archetype's stride");
            Assert.That(threeD.BlockSize, Is.EqualTo(64 + (21 * 128) + (21 * 32)));
        });
    }

    /// <summary>The layout sizes and refuses nothing: a segment and a state body far past one line produce a bigger stride, never a throw.</summary>
    [Test]
    public void OversizedRegionsGrowTheStrideRatherThanFailing()
    {
        var wide = ReplicationBlockLayout.ForArchetype(slotCount: 4, segmentBytes: 21, packedStateBytes: 200, prevPositionBytes: 9, runStartBytes: 16,
            ownerEntrySize: 0);

        Assert.Multiple(() =>
        {
            Assert.That(wide.HotStride, Is.EqualTo(256), "32 + 21 + 200 = 253 rounds to four lines");
            Assert.That(wide.HotStride % ReplicationBlockLayout.HotEntrySize, Is.Zero);
            Assert.That(wide.ColdStride % ReplicationBlockLayout.ColdEntrySize, Is.Zero);
            Assert.That(wide.BlockStride % 64, Is.Zero);
        });
    }

    /// <summary>An archetype declaring owner fields grows the block by exactly one owner entry per slot, and only at the end.</summary>
    [Test]
    public void OwnerEntriesExtendTheBlockWithoutMovingHotOrCold()
    {
        var without = new ReplicationBlockLayout(slotCount: 21);
        var with = new ReplicationBlockLayout(slotCount: 21, ownerEntrySize: 8);

        Assert.Multiple(() =>
        {
            Assert.That(with.HotOffset, Is.EqualTo(without.HotOffset));
            Assert.That(with.ColdOffset, Is.EqualTo(without.ColdOffset));
            Assert.That(with.OwnerOffset, Is.EqualTo(without.OwnerOffset));
            Assert.That(with.BlockSize, Is.EqualTo(without.BlockSize + (21 * 8)));
        });
    }
}
