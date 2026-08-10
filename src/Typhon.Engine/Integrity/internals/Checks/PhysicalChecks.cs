using System;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>PHY</c> — per-page physical checks. Everything here is decidable from one page's own bytes, so it needs no
/// cross-structure state and runs during the single sweep that reads the file.
/// </summary>
internal static class PhysicalChecks
{
    /// <summary>Check code: stored checksum matches page content.</summary>
    public const string Checksum = "CHK-PHY-01";

    /// <summary>Check code: the zero-checksum sentinel means "never written", not "live".</summary>
    public const string ChecksumSentinel = "CHK-PHY-02";

    /// <summary>Check code: flags and type hold defined values.</summary>
    public const string HeaderFields = "CHK-PHY-03";

    /// <summary>Check code: the seqlock modification counter is even on disk.</summary>
    public const string SeqlockParity = "CHK-PHY-04";

    /// <summary>Check code: flag combinations are legal.</summary>
    public const string FlagCombination = "CHK-PHY-05";

    /// <summary>Check code: the page's format revision is one this build knows.</summary>
    public const string FormatRevision = "CHK-PHY-06";

    /// <summary>Check code: the page's stamped index matches where it was found.</summary>
    public const string MisdirectedWrite = "CHK-PHY-07";

    /// <summary>
    /// Runs every per-page check against one page image.
    /// </summary>
    /// <param name="ctx">The scan context.</param>
    /// <param name="filePageIndex">Index the page was read from.</param>
    /// <param name="page">The page image.</param>
    /// <param name="allocated">Whether the allocation bitmap marks this page as in use.</param>
    public static void RunForPage(ScanContext ctx, int filePageIndex, ReadOnlySpan<byte> page, bool allocated)
    {
        CheckHeaderFields(ctx, filePageIndex, page);
        CheckSeqlockParity(ctx, filePageIndex, page);
        CheckMisdirectedWrite(ctx, filePageIndex, page);

        if (ctx.AtLeast(ScanDepth.Standard))
        {
            CheckChecksum(ctx, filePageIndex, page, allocated);
        }
    }

    private static void CheckHeaderFields(ScanContext ctx, int filePageIndex, ReadOnlySpan<byte> page)
    {
        var flags = PageImage.Flags(page);
        var type = PageImage.Type(page);

        if (!PageImage.IsTypeDefined(type))
        {
            ctx.Report(HeaderFields, IntegritySeverity.Divergence, "", ctx.LocusForPage(filePageIndex),
                $"Page {filePageIndex} carries an undefined block type ({(byte)type}).",
                "The type byte is written once at page initialisation and never changes, so an undefined value means the "
                + "header zone was overwritten by something that was not the page initialiser.");
        }

        if (!PageImage.AreFlagsWellFormed(flags))
        {
            ctx.Report(FlagCombination, IntegritySeverity.Divergence, "", ctx.LocusForPage(filePageIndex),
                $"Page {filePageIndex} carries an illegal flag combination (0x{(byte)flags:X2}).",
                "Legal combinations are: free; segment member; segment member and root. A root that is not a member, a page "
                + "that is both free and a member, or any reserved bit set, cannot be produced by the page initialiser.");
        }

        var rev = PageImage.FormatRevision(page);
        if (rev is < 0 or > 1)
        {
            ctx.Report(FormatRevision, IntegritySeverity.Advisory, "", ctx.LocusForPage(filePageIndex),
                $"Page {filePageIndex} declares format revision {rev}, which this build does not know.",
                "The page's internal layout may differ from what the decoders assume, so findings about its contents are less "
                + "trustworthy than findings about its header.");
        }
    }

    /// <summary>
    /// The seqlock counter is even while a page is quiescent and odd while a write is in progress, and the checkpoint's
    /// page snapshot only <i>begins</i> on an even counter. An odd counter on disk is therefore evidence that a page was
    /// persisted by some path that did not honour the seqlock — the cheapest independent detector in the catalogue, one
    /// byte-test per page at the shallowest depth.
    /// </summary>
    /// <remarks>
    /// The meta pair is excluded and must be: it is written in place while latched by
    /// <c>ManagedPagedMMF.PersistMetaNow</c>, so its counter is legitimately odd on disk. Encoding that exclusion is not
    /// optional — without it the check fires on every healthy database.
    /// </remarks>
    private static void CheckSeqlockParity(ScanContext ctx, int filePageIndex, ReadOnlySpan<byte> page)
    {
        if (filePageIndex <= 1)
        {
            return;
        }

        var counter = PageImage.ModificationCounter(page);
        if ((counter & 1) == 0)
        {
            return;
        }

        ctx.Report(SeqlockParity, IntegritySeverity.Divergence, "SL-01", ctx.LocusForPage(filePageIndex),
            $"Page {filePageIndex} was captured mid-modification.",
            $"Its seqlock counter is {counter}, which is odd. The counter is even when a page is quiescent and odd only while "
            + "a writer holds it, and the checkpoint's snapshot refuses to start on an odd counter — so this image reached "
            + "disk through a path that did not honour the seqlock. The page's contents may be internally inconsistent even "
            + "though its checksum verifies.");
    }

    /// <summary>
    /// A page carries its own logical index, so a page written to the wrong file offset is caught with certainty rather
    /// than probabilistically. Without this a misdirected write is <i>completely</i> undetectable: the page's checksum is
    /// perfectly valid, it is simply the wrong page.
    /// </summary>
    private static void CheckMisdirectedWrite(ScanContext ctx, int filePageIndex, ReadOnlySpan<byte> page)
    {
        var stamped = PageSectorFooter.ReadFilePageIndex(page);
        if (stamped == 0 || stamped == filePageIndex)
        {
            return;   // 0 means the page predates the stamp
        }

        // A protected page legitimately lives in either of two physical slots, and the stamp records the LOGICAL index, so
        // finding a primary's content at its twin offset is the mechanism working, not a misdirect. Test the twin SET
        // rather than the page's role: a twin can also be one of the genesis-reserved pages (the occupancy root's twin is),
        // and the role array keeps the stronger classification.
        if (ctx.TwinSlots.Contains(filePageIndex))
        {
            return;
        }

        ctx.Report(MisdirectedWrite, IntegritySeverity.DataLoss, "", ctx.LocusForPage(filePageIndex),
            $"Page {filePageIndex} holds the contents of page {stamped}.",
            $"The page stamps its own logical index, and this image says {stamped}. A write landed at the wrong file offset, "
            + $"which destroys whatever page {filePageIndex} held and leaves two offsets claiming to be page {stamped}. The "
            + "checksum verifies, because the bytes themselves are intact — they are simply in the wrong place.",
            Repairability.NotRepairable,
            new LossEstimate
            {
                Kind = LossKind.Unknown,
                EntityCount = -1,
                BoundedMin = 1,
                BoundedMax = 475,
                Explanation = $"Everything page {filePageIndex} held before the misdirected write. Its contents are gone and "
                    + "nothing in the database records what they were."
            });
    }

    private static void CheckChecksum(ScanContext ctx, int filePageIndex, ReadOnlySpan<byte> page, bool allocated)
    {
        // The A/B protected pages are verified as PAIRS, not individually, and reporting them here would be wrong in both
        // directions. The meta pair's first write goes to slot 1, so physical page 0 legitimately holds a never-CRC'd image
        // on a fresh database — the engine's own load path skips page 0 for exactly this reason. A directory twin holds a
        // complete image of an OLDER generation, which is not damage: it is the fallback working as designed.
        if (filePageIndex <= 1)
        {
            return;   // owned by CHK-BOO-03
        }

        if (ctx.TwinSlots.Contains(filePageIndex))
        {
            CheckTwinSlot(ctx, filePageIndex, page);
            return;
        }

        var sectorCount = PageSectorFooter.ReadSectorCount(page);
        if (sectorCount > 0)
        {
            CheckSectoredPage(ctx, filePageIndex, page, sectorCount);
            return;
        }

        var stored = PageImage.StoredChecksum(page);
        if (stored == 0)
        {
            // The zero sentinel means "never checkpointed". A page that is allocated and carries content but no checksum is
            // a different thing: either it was never flushed, or its header was zeroed by damage.
            if (allocated && !IsBlank(page))
            {
                ctx.Report(ChecksumSentinel, IntegritySeverity.Divergence, "", ctx.LocusForPage(filePageIndex),
                    $"Page {filePageIndex} is allocated and holds data but carries no checksum.",
                    "A zero checksum is the sentinel for a page that was never written to disk. This page is marked allocated "
                    + "and is not blank, so either the write never completed or the header was zeroed after the fact. Its "
                    + "contents cannot be verified either way.");
            }

            return;
        }

        if (PageImage.VerifyWholePageChecksum(page, out var computed))
        {
            return;
        }

        ctx.ChecksumFailures++;
        ReportChecksumFailure(ctx, filePageIndex, allocated, stored, computed);
    }

    /// <summary>
    /// A twin slot holds the alternate copy of a protected directory page. It is <i>expected</i> to be an older generation,
    /// so only an unreadable twin is a finding — and then only as a degradation, because the pair is still serving from its
    /// good slot. What it costs is the fallback: the next write to that directory page has nothing to fall back to.
    /// </summary>
    private static void CheckTwinSlot(ScanContext ctx, int filePageIndex, ReadOnlySpan<byte> page)
    {
        if (PageImage.VerifyWholePageChecksum(page, out var computed))
        {
            return;
        }

        // A twin that was allocated but never written through the alternation path is blank, which is normal on a young
        // database rather than damage.
        if (PageImage.PairGeneration(page) == 0 && IsBlank(page))
        {
            return;
        }

        ctx.ChecksumFailures++;
        ctx.Report(Checksum, IntegritySeverity.Divergence, "CK-05", ctx.LocusForPage(filePageIndex),
            $"Directory twin slot on page {filePageIndex} is unreadable; its segment is running without a fallback copy.",
            $"Stored 0x{PageImage.StoredChecksum(page):X8}, computed 0x{computed:X8}. The primary slot is serving correctly, "
            + "so nothing is lost right now — but the pair exists so that a torn write to the directory can never destroy "
            + "the segment, and that protection is currently absent. It restores itself on the next write to this directory "
            + "page, which alternates back into the bad slot.",
            Repairability.Lossless);
    }

    private static void CheckSectoredPage(ScanContext ctx, int filePageIndex, ReadOnlySpan<byte> page, int sectorCount)
    {
        ctx.PagesWithSectorFooters++;

        Span<bool> sectorOk = stackalloc bool[PageSectorFooter.MaxSectorCount];
        var footerIntact = PageSectorFooter.Verify(page, sectorCount, sectorOk, out var failed);

        if (failed == 0)
        {
            return;
        }

        ctx.ChecksumFailures++;
        ctx.SectorFailures += failed;

        var locus = ctx.LocusForPage(filePageIndex);
        var sectorBytes = IntegrityConstants.PageSize / sectorCount;

        if (!footerIntact)
        {
            ctx.Report(Checksum, IntegritySeverity.DataLoss, "ADR-015", locus,
                $"Page {filePageIndex} failed verification and its per-sector footer is unreadable.",
                "The footer that records each sector's checksum did not itself verify, so no part of this page can be trusted "
                + "and salvage falls back to whole-page granularity. Everything the page held is affected.",
                Repairability.Lossy, DescribeWholePageLoss(ctx, filePageIndex));
            return;
        }

        ctx.Report(Checksum, IntegritySeverity.DataLoss, "ADR-015", locus,
            $"Page {filePageIndex} failed verification in {failed} of its {sectorCount} sectors.",
            $"Each sector covers {sectorBytes} bytes and is verified independently, so the {sectorCount - failed} intact "
            + $"sectors are provably current: {DescribeSectors(sectorOk, sectorCount)}. Only rows that touch a failed sector "
            + "are affected — the rest of the page is salvageable.",
            Repairability.Lossy,
            new LossEstimate
            {
                Kind = LossKind.Unknown,
                EntityCount = -1,
                BoundedMin = 1,
                BoundedMax = 475,
                Explanation = $"Rows stored in the {failed} damaged sector(s) of page {filePageIndex}. Naming them exactly "
                    + "requires the archetype layout, which an offline scan of this depth does not decode."
            });
    }

    private static void ReportChecksumFailure(ScanContext ctx, int filePageIndex, bool allocated, uint stored, uint computed)
    {
        var locus = ctx.LocusForPage(filePageIndex);
        var kind = locus.Kind;
        var derived = IsDerivedKind(kind);

        if (!allocated)
        {
            ctx.Report(Checksum, IntegritySeverity.Advisory, "ADR-015", locus,
                $"Free page {filePageIndex} failed its checksum.",
                $"Stored 0x{stored:X8}, computed 0x{computed:X8}. The page is not allocated, so nothing reads it and nothing "
                + "is lost; the stale contents are simply not what its header claims.");
            return;
        }

        if (derived)
        {
            ctx.Report(Checksum, IntegritySeverity.Divergence, "RB-01", locus,
                $"Page {filePageIndex} of the {kind} segment failed its checksum.",
                $"Stored 0x{stored:X8}, computed 0x{computed:X8}. {kind} is derived state, so this page is regenerated from "
                + "primary data rather than repaired — nothing is lost.",
                Repairability.Lossless);
            return;
        }

        ctx.Report(Checksum, IntegritySeverity.DataLoss, "RB-04", locus,
            $"Page {filePageIndex} failed its checksum and holds primary data.",
            $"Stored 0x{stored:X8}, computed 0x{computed:X8}. This page carries {(kind == StorageSegmentKind.Other ? "unattributed" : kind.ToString())} "
            + "content, which no other structure can regenerate. It carries no per-sector footer, so the whole page is "
            + "condemned rather than the damaged part of it.",
            Repairability.Lossy, DescribeWholePageLoss(ctx, filePageIndex));
    }

    private static LossEstimate DescribeWholePageLoss(ScanContext ctx, int filePageIndex)
    {
        var locus = ctx.LocusForPage(filePageIndex);
        return new LossEstimate
        {
            Kind = LossKind.Unknown,
            EntityCount = -1,
            BoundedMin = 1,
            BoundedMax = 475,
            Archetype = locus.ArchetypeName,
            Explanation = $"Everything stored on page {filePageIndex}. The upper bound is the most rows a page of any shape "
                + "can hold; the exact figure needs the owning archetype's layout."
        };
    }

    /// <summary>
    /// Whether a segment kind holds state that is a pure function of primary data, and can therefore be discarded and
    /// rebuilt with zero loss. Getting this partition right is the difference between a repair tool and a data-destroying
    /// one.
    /// </summary>
    /// <param name="kind">The segment kind.</param>
    public static bool IsDerivedKind(StorageSegmentKind kind) => kind switch
    {
        StorageSegmentKind.Index => true,
        StorageSegmentKind.EntityMap => true,
        StorageSegmentKind.Spatial => true,
        StorageSegmentKind.Occupancy => true,
        _ => false
    };

    private static string DescribeSectors(ReadOnlySpan<bool> sectorOk, int sectorCount)
    {
        Span<char> map = stackalloc char[sectorCount];
        for (var i = 0; i < sectorCount; i++)
        {
            map[i] = sectorOk[i] ? '.' : 'X';
        }

        return new string(map);
    }

    private static bool IsBlank(ReadOnlySpan<byte> page)
    {
        for (var i = 0; i < page.Length; i++)
        {
            if (page[i] != 0)
            {
                return false;
            }
        }

        return true;
    }
}
