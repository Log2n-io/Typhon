using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// <c>SEG</c> — segment, directory and page-allocation checks. The family that decides whether the file's structural graph
/// is a graph at all.
/// </summary>
internal static class SegmentChecks
{
    /// <summary>Check code: every page is claimed by at most one segment.</summary>
    public const string SingleOwner = "CHK-SEG-01";

    /// <summary>Check code: the allocation bitmap agrees with the reachability walk.</summary>
    public const string OccupancyAgreement = "CHK-SEG-02";

    /// <summary>Check code: the page directory and the forward chain agree.</summary>
    public const string DirectoryChain = "CHK-SEG-05";

    /// <summary>Check code: directory walks terminate.</summary>
    public const string DirectoryTraversal = "CHK-SEG-06";

    /// <summary>Check code: a segment root declares a defined kind.</summary>
    public const string SegmentKind = "CHK-SEG-07";

    /// <summary>Runs the segment family over the walked structure.</summary>
    /// <param name="ctx">The scan context, with segments already walked.</param>
    public static void Run(ScanContext ctx)
    {
        CheckWalkDiagnostics(ctx);
        CheckSegmentKinds(ctx);
        CheckDirectoryVersusChain(ctx);

        if (ctx.AtLeast(ScanDepth.Deep))
        {
            CheckOccupancyAgreement(ctx);
        }
        else
        {
            ctx.Findings.NoteSkipped(OccupancyAgreement, "needs Deep depth");
        }
    }

    private static void CheckWalkDiagnostics(ScanContext ctx)
    {
        foreach (var seg in ctx.Segments.Values)
        {
            for (var i = 0; i < seg.WalkDiagnostics.Count; i++)
            {
                var text = seg.WalkDiagnostics[i];
                var fatal = text.Contains("cycle", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("cannot be read", StringComparison.OrdinalIgnoreCase);

                ctx.Report(DirectoryTraversal, fatal ? IntegritySeverity.Fatal : IntegritySeverity.Divergence, "",
                    new Locus(seg.RootPageIndex, seg.RootPageIndex, seg.Kind),
                    $"The {seg.Kind} segment rooted at page {seg.RootPageIndex} could not be walked cleanly.",
                    text + (fatal
                        ? " An engine walking this structure would hang or read outside the file, so the segment is unusable "
                          + "as it stands."
                        : " The walk recovered what it could and stopped; pages past that point are unaccounted for."));
            }
        }
    }

    private static void CheckSegmentKinds(ScanContext ctx)
    {
        foreach (var seg in ctx.Segments.Values)
        {
            if (PageImage.IsKindDefined(seg.Kind))
            {
                continue;
            }

            ctx.Report(SegmentKind, IntegritySeverity.Divergence, "", new Locus(seg.RootPageIndex, seg.RootPageIndex),
                $"The segment rooted at page {seg.RootPageIndex} declares an undefined kind ({(byte)seg.Kind}).",
                "The kind is written once at segment creation and read back at load. An undefined value means the root "
                + "page's header zone was overwritten, and the engine would not know how to interpret the segment's pages.");
        }
    }

    /// <summary>
    /// The page directory and the forward data-page chain are written by independent code paths, so a disagreement
    /// localises a lost write precisely: one of the two writes did not reach disk before the previous close.
    /// </summary>
    private static void CheckDirectoryVersusChain(ScanContext ctx)
    {
        foreach (var seg in ctx.Segments.Values)
        {
            if (!seg.DirectoryComplete || !seg.ChainComplete)
            {
                continue;   // already reported as a traversal problem; the counts would be meaningless
            }

            if (seg.ForwardChainCount == seg.Pages.Count)
            {
                continue;
            }

            var diff = seg.ForwardChainCount - seg.Pages.Count;
            ctx.Report(DirectoryChain, IntegritySeverity.Divergence, "",
                new Locus(seg.RootPageIndex, seg.RootPageIndex, seg.Kind),
                $"The {seg.Kind} segment rooted at page {seg.RootPageIndex} disagrees with itself about how many pages it owns.",
                $"Its page directory enumerates {seg.Pages.Count:N0} pages; its forward page chain reaches "
                + $"{seg.ForwardChainCount:N0} ({diff:+0;-#}). The two are written by separate code paths during a grow, so "
                + (diff > 0
                    ? "the directory append did not persist — the extra pages are allocated and linked but unaddressable."
                    : "the chain pointer did not persist — the directory names pages the chain cannot reach."),
                Repairability.Lossless);
        }
    }

    /// <summary>
    /// The allocation bitmap is derived state, so a disagreement with the reachability walk is reported as "the bitmap is
    /// wrong", never as "these pages are wrong" — and the repair is a re-derive, not a page edit.
    /// </summary>
    private static void CheckOccupancyAgreement(ScanContext ctx)
    {
        var occ = ctx.Occupancy;
        if (occ == null || !occ.IsComplete)
        {
            ctx.Findings.NoteSkipped(OccupancyAgreement, "the allocation bitmap could not be read in full");
            return;
        }

        var leaked = new List<int>();
        var phantom = new List<int>();
        var pageCount = Math.Min(ctx.Source.PageCount, ctx.Roles.Length);

        for (var p = 0; p < pageCount; p++)
        {
            var reachable = ctx.Roles[p] != PageRole.Unclaimed;
            var allocated = occ.IsAllocated(p);

            if (allocated && !reachable)
            {
                leaked.Add(p);
            }
            else if (!allocated && reachable)
            {
                phantom.Add(p);
            }
        }

        if (leaked.Count > 0)
        {
            ctx.Report(OccupancyAgreement, IntegritySeverity.Leak, "CK-09",
                new Locus(leaked[0]),
                $"{leaked.Count:N0} pages are marked allocated but no segment claims them.",
                $"{FormatPageList(leaked)} The bitmap survived a grow whose directory append did not, so the space is held "
                + $"but unreachable — {(long)leaked.Count * IntegrityConstants.PageSize / 1024:N0} KiB. Nothing is lost; the "
                + "space is reclaimed by re-deriving the bitmap from the reachability walk.",
                Repairability.Lossless);
        }

        if (phantom.Count > 0)
        {
            ctx.Report(OccupancyAgreement, IntegritySeverity.Divergence, "CK-09",
                new Locus(phantom[0]),
                $"{phantom.Count:N0} pages are claimed by a segment but the allocation bitmap says they are free.",
                $"{FormatPageList(phantom)} This is the dangerous direction: the allocator believes these pages are "
                + "available and could hand one out to a second owner, at which point two structures would write over each "
                + "other. Re-deriving the bitmap from the reachability walk fixes it, and doing so before any further "
                + "allocation is what prevents the double-allocation.",
                Repairability.Lossless);
        }
    }

    private static string FormatPageList(List<int> pages)
    {
        const int show = 8;
        var count = Math.Min(show, pages.Count);
        var text = string.Join(", ", pages.GetRange(0, count));
        return pages.Count > show ? $"Pages {text}, … (+{pages.Count - show:N0} more)." : $"Pages {text}.";
    }
}
