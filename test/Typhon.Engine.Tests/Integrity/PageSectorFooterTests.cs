using NUnit.Framework;
using System;
using System.Linq;

namespace Typhon.Engine.Tests;

/// <summary>
/// Unit tests for the per-sector page verification footer — the format change that takes provable salvage after a torn
/// write from 0 % of a page's rows to most of them.
/// </summary>
/// <remarks>
/// The tests that matter most here are the two that pin down <i>currency</i> rather than integrity. A torn write leaves
/// one region holding a valid image of the previous generation: its checksum passes, and only the generation stamp can
/// tell that it is stale. And the rule for reading those stamps has to be the maximum over all of them, because comparing
/// each against the page header is unsafe in the one direction that matters.
/// </remarks>
[TestFixture]
internal sealed class PageSectorFooterTests
{
    private static byte[] MakePage(int reservedMetadataBytes, int changeRevision, byte fill = 0x5A)
    {
        var page = new byte[PagedMMF.PageSize];
        new Random(1234).NextBytes(page);
        page.AsSpan(PagedMMF.PageHeaderSize).Fill(fill);
        PageSectorFooter.DeclareGeometry(page, reservedMetadataBytes);
        BitConverter.GetBytes(changeRevision).CopyTo(page.AsSpan(4));
        return page;
    }

    private static int Verify(byte[] page, out bool footerIntact)
    {
        var n = PageSectorFooter.ReadSectorCount(page);
        Span<bool> ok = stackalloc bool[PageSectorFooter.MaxSectorCount];
        footerIntact = PageSectorFooter.Verify(page, n, ok, out var failed);
        return failed;
    }

    // ── Geometry ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void Geometry_ShrinksAsTheChunkBitmapGrows()
    {
        // The footer and the chunk-occupancy bitmap share the metadata region, so the achievable granularity falls as the
        // segment's stride shrinks (a smaller stride means more chunks per page, means more bitmap words).
        Assert.Multiple(() =>
        {
            Assert.That(PageSectorFooter.ChooseSectorCount(0), Is.EqualTo(16), "a page with no chunk bitmap gets the finest granularity");
            Assert.That(PageSectorFooter.ChooseSectorCount(8), Is.EqualTo(16), "a cluster page (1 bitmap word) still gets 16");
            Assert.That(PageSectorFooter.ChooseSectorCount(32), Is.EqualTo(16));
            Assert.That(PageSectorFooter.ChooseSectorCount(40), Is.EqualTo(8), "5 bitmap words leaves room for 8");
            Assert.That(PageSectorFooter.ChooseSectorCount(112), Is.EqualTo(2));
            Assert.That(PageSectorFooter.ChooseSectorCount(128), Is.Zero, "a stride-8 segment fills the region; no footer fits");
        });
    }

    [Test]
    public void Geometry_NeverOverlapsTheChunkBitmap()
    {
        for (var reserved = 0; reserved <= 128; reserved += 8)
        {
            var n = PageSectorFooter.ChooseSectorCount(reserved);
            if (n == 0)
            {
                continue;
            }

            Assert.That(PageSectorFooter.FooterBase(n), Is.GreaterThanOrEqualTo(PageSectorFooter.MetadataOffset + reserved),
                $"footer for {n} sectors must start after {reserved} reserved bitmap bytes");
        }
    }

    [Test]
    public void ReadSectorCount_RejectsAnInconsistentDeclaration()
    {
        var page = MakePage(0, 1);
        Assert.That(PageSectorFooter.ReadSectorCount(page), Is.EqualTo(16), "precondition");

        page[PageSectorFooter.SectorLogSizeOffset] = 7;   // inconsistent with a count of 16
        Assert.That(PageSectorFooter.ReadSectorCount(page), Is.Zero, "a self-inconsistent declaration must be treated as unstamped");

        page = MakePage(0, 1);
        page[PageSectorFooter.SectorCountOffset] = 7;     // not a legal count
        Assert.That(PageSectorFooter.ReadSectorCount(page), Is.Zero);
    }

    // ── Integrity ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void FreshlyStampedPage_VerifiesCompletely()
    {
        var page = MakePage(0, 42);
        PageSectorFooter.Stamp(page, 16);

        var failed = Verify(page, out var footerIntact);

        Assert.That(footerIntact, Is.True);
        Assert.That(failed, Is.Zero);
    }

    [Test]
    public void ByteFlipInOneSector_CondemnsOnlyThatSector()
    {
        var page = MakePage(0, 42);
        PageSectorFooter.Stamp(page, 16);

        const int sectorSize = PagedMMF.PageSize / 16;
        page[(7 * sectorSize) + 100] ^= 0xFF;

        var n = PageSectorFooter.ReadSectorCount(page);
        Span<bool> ok = stackalloc bool[PageSectorFooter.MaxSectorCount];
        PageSectorFooter.Verify(page, n, ok, out var failed);

        Assert.That(failed, Is.EqualTo(1), "exactly one sector must be condemned");
        Assert.That(ok[7], Is.False, "and it must be the one that was damaged");
        for (var s = 0; s < 16; s++)
        {
            if (s != 7)
            {
                Assert.That(ok[s], Is.True, $"sector {s} was untouched and must still verify — this is the salvage the format exists for");
            }
        }
    }

    // ── Currency: the part a checksum alone cannot do ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A torn write leaves part of the page holding a valid image of the previous generation. Every byte of it is
    /// intact and its checksum passes — it is simply <i>old</i>. Without a currency stamp the page would verify clean
    /// while silently serving stale rows, which is strictly worse than reporting damage.
    /// </summary>
    [Test]
    public void StaleSectorFromAPreviousGeneration_IsCaughtEvenThoughItsChecksumPasses()
    {
        var generationOld = MakePage(0, 100);
        PageSectorFooter.Stamp(generationOld, 16);

        // Generation 101 rewrites the page's contents...
        var generationNew = (byte[])generationOld.Clone();
        generationNew.AsSpan(PagedMMF.PageHeaderSize).Fill(0xC3);
        BitConverter.GetBytes(101).CopyTo(generationNew.AsSpan(4));
        PageSectorFooter.Stamp(generationNew, 16);

        // ...but sectors 9-15 never landed, so they still hold generation 100's bytes AND generation 100's footer entries.
        const int sectorSize = PagedMMF.PageSize / 16;
        var torn = (byte[])generationNew.Clone();
        for (var s = 9; s < 16; s++)
        {
            generationOld.AsSpan(s * sectorSize, sectorSize).CopyTo(torn.AsSpan(s * sectorSize));
            // The footer entries for those sectors are in sector 0, which DID land — so restore them individually to model
            // a tear that also missed the footer bytes for the affected sectors.
            var crcOffset = PageSectorFooter.FooterBase(16) + (s * 4);
            var genOffset = PageSectorFooter.FooterBase(16) + (16 * 4) + (s * 2);
            generationOld.AsSpan(crcOffset, 4).CopyTo(torn.AsSpan(crcOffset));
            generationOld.AsSpan(genOffset, 2).CopyTo(torn.AsSpan(genOffset));
        }

        // Re-seal the footer array so the footer's own CRC matches, which is what a real torn write would leave if the
        // tear boundary fell after sector 0.
        var footerBase = PageSectorFooter.FooterBase(16);
        var footerCrc = Crc32CUtil.Compute(torn.AsSpan(footerBase, PageSectorFooter.FooterEndOffset - footerBase));
        BitConverter.GetBytes(footerCrc).CopyTo(torn.AsSpan(PageBaseHeader.PageChecksumOffset));

        Span<bool> ok = stackalloc bool[PageSectorFooter.MaxSectorCount];
        var footerIntact = PageSectorFooter.Verify(torn, 16, ok, out var failed);

        Assert.That(footerIntact, Is.True, "the footer itself survived; the sectors are what is stale");
        Assert.That(failed, Is.EqualTo(7), "all seven stale sectors must be condemned despite their checksums passing");
        for (var s = 0; s < 9; s++)
        {
            Assert.That(ok[s], Is.True, $"sector {s} is current and must survive");
        }
    }

    /// <summary>
    /// The validity rule must take the <b>maximum</b> generation across the header and every sector stamp. The obvious
    /// alternative — compare each sector against the page header — inverts when it is sector 0 that failed to persist:
    /// the header then reads the OLD generation, so every stale sector agrees with it and the one sector that actually
    /// landed is the one condemned. That is the worst possible answer, because it selects for stale data.
    /// </summary>
    [Test]
    public void WhenSectorZeroIsTheStaleOne_TheRuleDoesNotInvert()
    {
        var page = MakePage(0, 100);
        PageSectorFooter.Stamp(page, 16);

        // Sector 5 persisted at generation 101; everything else, including the header in sector 0, is still at 100.
        const int sectorSize = PagedMMF.PageSize / 16;
        page.AsSpan(5 * sectorSize, sectorSize).Fill(0xE1);
        var newCrc = PageSectorFooter.ComputeSectorCrc(page, 16, 5);
        BitConverter.GetBytes(newCrc).CopyTo(page.AsSpan(PageSectorFooter.FooterBase(16) + (5 * 4)));
        BitConverter.GetBytes((ushort)101).CopyTo(page.AsSpan(PageSectorFooter.FooterBase(16) + (16 * 4) + (5 * 2)));

        var footerBase = PageSectorFooter.FooterBase(16);
        var footerCrc = Crc32CUtil.Compute(page.AsSpan(footerBase, PageSectorFooter.FooterEndOffset - footerBase));
        BitConverter.GetBytes(footerCrc).CopyTo(page.AsSpan(PageBaseHeader.PageChecksumOffset));

        Span<bool> ok = stackalloc bool[PageSectorFooter.MaxSectorCount];
        PageSectorFooter.Verify(page, 16, ok, out var failed);

        Assert.That(ok[5], Is.True, "the sector that DID persist at the newest generation must be kept");
        Assert.That(failed, Is.EqualTo(15), "and every sector still at the older generation must be condemned");
    }

    [Test]
    public void GenerationComparison_HandlesWraparound()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PageSectorFooter.GenerationIsNewer(5, 4), Is.True);
            Assert.That(PageSectorFooter.GenerationIsNewer(4, 5), Is.False);
            Assert.That(PageSectorFooter.GenerationIsNewer(0, ushort.MaxValue), Is.True, "0 follows 65535");
            Assert.That(PageSectorFooter.GenerationIsNewer(ushort.MaxValue, 0), Is.False);
            Assert.That(PageSectorFooter.GenerationIsNewer(7, 7), Is.False, "equal is not newer");
        });
    }

    // ── Round-trip through the engine's own stamp/verify pair ────────────────────────────────────────────────────────

    [Test]
    public void StampPageForWrite_AndVerifyPageImage_AgreeForBothForms()
    {
        var sectored = MakePage(0, 7);
        PagedMMF.StampPageForWrite(sectored, 41208);
        Assert.That(PagedMMF.VerifyPageImage(sectored, out _), Is.True, "a page declaring sectors must verify through the sector path");
        Assert.That(PageSectorFooter.ReadFilePageIndex(sectored), Is.EqualTo(41208), "and must carry its own index");

        var wholePage = MakePage(128, 7);   // no room for a footer
        Assert.That(PageSectorFooter.ReadSectorCount(wholePage), Is.Zero, "precondition: this page declares no footer");
        PagedMMF.StampPageForWrite(wholePage, 99);
        Assert.That(PagedMMF.VerifyPageImage(wholePage, out _), Is.True, "a page with no footer must verify through the whole-page path");
    }

    [Test]
    public void StampPageForWrite_DetectsATamperedPageInBothForms()
    {
        foreach (var reserved in new[] { 0, 128 })
        {
            var page = MakePage(reserved, 7);
            PagedMMF.StampPageForWrite(page, 12);
            page[PagedMMF.PageHeaderSize + 500] ^= 0x01;
            Assert.That(PagedMMF.VerifyPageImage(page, out _), Is.False, $"a flipped bit must be caught (reserved={reserved})");
        }
    }

    /// <summary>
    /// Every legal geometry must round-trip, not just the 16-sector one — a small-stride segment gets fewer sectors and
    /// its pages still have to verify.
    /// </summary>
    [Test]
    public void EveryLegalGeometry_RoundTrips()
    {
        foreach (var reserved in new[] { 0, 8, 40, 80, 112 })
        {
            var n = PageSectorFooter.ChooseSectorCount(reserved);
            var page = MakePage(reserved, 3);
            Assert.That(PageSectorFooter.ReadSectorCount(page), Is.EqualTo(n), $"declared geometry must read back (reserved={reserved})");

            PageSectorFooter.Stamp(page, n);
            var failed = Verify(page, out var footerIntact);
            Assert.That(footerIntact, Is.True, $"footer must verify for {n} sectors");
            Assert.That(failed, Is.Zero, $"all {n} sectors must verify");

            // And the bitmap region must be untouched by stamping.
            var bitmap = page.AsSpan(PageSectorFooter.MetadataOffset, reserved).ToArray();
            PageSectorFooter.Stamp(page, n);
            Assert.That(page.AsSpan(PageSectorFooter.MetadataOffset, reserved).ToArray().SequenceEqual(bitmap), Is.True,
                $"stamping must never write into the chunk bitmap (reserved={reserved})");
        }
    }
}
