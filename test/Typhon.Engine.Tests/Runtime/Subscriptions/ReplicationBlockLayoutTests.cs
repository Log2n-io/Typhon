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
