using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// Recomputes the page-allocation bitmap from the segments that actually exist.
/// </summary>
/// <remarks>
/// <para>
/// The bitmap is <b>derived</b> state — the crash path already re-derives it — so it is never the authority on what is
/// allocated. A disagreement with the reachability walk therefore means the bitmap is wrong, never that the pages are, and
/// the repair is a recompute rather than a page edit. That asymmetry is the whole reason this repair is safe: it writes
/// only to the structure that was already defined as reconstructible.
/// </para>
/// <para>
/// Of the two directions of disagreement, the dangerous one is a <i>phantom</i> free bit — a page a segment owns that the
/// bitmap says is available. Left alone, the allocator eventually hands that page to a second owner and the two structures
/// write over each other. Leaked bits (marked but unreachable) merely waste space. Recomputing fixes both, and fixing the
/// phantom direction before any further allocation is what prevents the double-allocation.
/// </para>
/// </remarks>
internal static class OccupancyRepair
{
    /// <summary>
    /// Rebuilds the bitmap in place from a fresh structural walk.
    /// </summary>
    /// <param name="bundlePath">The bundle directory.</param>
    /// <returns>A description of what changed, for the repair receipt.</returns>
    /// <exception cref="InvalidOperationException">The occupancy segment could not be located or is unusable.</exception>
    public static string Rederive(string bundlePath)
    {
        using var source = new OfflineBundlePageSource(bundlePath);

        var reachable = WalkReachablePages(source, out var occupancySegment);
        if (occupancySegment == null)
        {
            throw new InvalidOperationException(
                "No occupancy segment could be found, so the allocation bitmap cannot be rebuilt. The database's structural "
                + "spine is damaged beyond what this repair can address.");
        }

        var dataPageCount = occupancySegment.Pages.Count - 1;
        if (dataPageCount <= 0)
        {
            throw new InvalidOperationException("The occupancy segment has no data pages; there is no bitmap to rebuild.");
        }

        var wordCount = dataPageCount * OccupancyView.WordsPerPage;
        var words = new ulong[wordCount];
        var capacity = wordCount * 64;

        var set = 0;
        foreach (var page in reachable)
        {
            if (page < 0 || page >= capacity)
            {
                continue;
            }

            words[page >> 6] |= 1UL << (page & 63);
            set++;
        }

        var before = 0;
        var existing = OccupancyView.Read(source, occupancySegment);
        if (existing.IsComplete)
        {
            before = existing.PopCount;
        }

        source.Dispose();
        WriteBitmap(bundlePath, occupancySegment, words);

        var delta = set - before;
        return $"Rebuilt the allocation bitmap from {reachable.Count:N0} reachable pages "
            + $"({before:N0} bits set before, {set:N0} after, {delta:+#;-#;0}). "
            + (delta < 0
                ? $"{-delta:N0} leaked page(s) reclaimed — {(long)(-delta) * IntegrityConstants.PageSize / 1024:N0} KiB."
                : delta > 0
                    ? $"{delta:N0} phantom free bit(s) cleared; those pages can no longer be handed to a second owner."
                    : "The bitmap already agreed with the walk.");
    }

    /// <summary>
    /// Enumerates every page any structure claims, plus the genesis-reserved ones. This is the authority the bitmap is
    /// rebuilt against.
    /// </summary>
    private static HashSet<int> WalkReachablePages(OfflineBundlePageSource source, out SegmentView occupancySegment)
    {
        occupancySegment = null;
        var reachable = new HashSet<int>();
        Span<byte> page = new byte[IntegrityConstants.PageSize];

        for (var p = 0; p < Math.Min(ManagedPagedMMF.InitialReservedPageCount, source.PageCount); p++)
        {
            reachable.Add(p);
        }

        var walker = new SegmentWalker(source);
        for (var p = 0; p < source.PageCount; p++)
        {
            if (!source.TryReadPage(p, page))
            {
                continue;
            }

            if ((PageImage.Flags(page) & PageBlockFlags.IsLogicalSegmentRoot) == 0)
            {
                continue;
            }

            // Roots are identified by self-reference: a twin carries a byte copy of the primary's directory, which still
            // names the primary. Without this test every paired segment is walked twice.
            if (MemoryMarshal.Read<int>(PageImage.RawData(page)) != p)
            {
                continue;
            }

            var segment = walker.WalkSegment(p);
            if (segment.Kind == StorageSegmentKind.Occupancy)
            {
                occupancySegment = segment;
            }

            for (var i = 0; i < segment.Pages.Count; i++)
            {
                reachable.Add(segment.Pages[i]);
            }

            for (var i = 0; i < segment.MapExtensionPages.Count; i++)
            {
                reachable.Add(segment.MapExtensionPages[i]);
            }

            for (var i = 0; i < segment.TwinPages.Count; i++)
            {
                reachable.Add(segment.TwinPages[i]);
            }
        }

        return reachable;
    }

    /// <summary>Writes the recomputed words back into the occupancy segment's data pages, restamping each page.</summary>
    private static void WriteBitmap(string bundlePath, SegmentView occupancySegment, ulong[] words)
    {
        var dataPath = Path.Combine(bundlePath, IntegrityConstants.DataFileName);
        using var handle = File.OpenHandle(dataPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var buffer = new byte[IntegrityConstants.PageSize];
        var dataPageCount = occupancySegment.Pages.Count - 1;

        for (var d = 0; d < dataPageCount; d++)
        {
            var filePage = occupancySegment.Pages[d + 1];
            var offset = filePage * (long)IntegrityConstants.PageSize;
            RandomAccess.Read(handle, buffer, offset);

            var target = MemoryMarshal.Cast<byte, ulong>(buffer.AsSpan(PageImage.RawDataOffset))[..OccupancyView.WordsPerPage];
            words.AsSpan(d * OccupancyView.WordsPerPage, OccupancyView.WordsPerPage).CopyTo(target);

            // Advance the change revision before restamping: the per-sector currency stamp is derived from it, and leaving
            // it unchanged would make the rewritten sectors indistinguishable from the ones they replaced.
            var revision = MemoryMarshal.Read<int>(buffer.AsSpan(PageImage.ChangeRevisionOffset)) + 1;
            MemoryMarshal.Write(buffer.AsSpan(PageImage.ChangeRevisionOffset), in revision);
            PagedMMF.StampPageForWrite(buffer, filePage);

            RandomAccess.Write(handle, buffer, offset);
        }

        RandomAccess.FlushToDisk(handle);
    }
}
