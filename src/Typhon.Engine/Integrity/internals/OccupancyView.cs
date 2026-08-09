using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>
/// The page-allocation bitmap, read from the occupancy segment's data pages.
/// </summary>
/// <remarks>
/// The bitmap is <b>derived</b> state (<c>CK-09</c> re-derives it on the crash path), so it is never the authority on what
/// is allocated. It is read so it can be <i>compared against</i> the reachability walk — a disagreement is reported as
/// "occupancy is wrong", never as "these pages are wrong", and the repair is a re-derive rather than a page edit.
/// </remarks>
internal sealed class OccupancyView
{
    /// <summary>Number of L0 bitmap words one segment data page holds.</summary>
    internal const int WordsPerPage = IntegrityConstants.PageRawDataSize / sizeof(long);

    private readonly ulong[] _words;

    private OccupancyView(ulong[] words, int capacity, bool complete, IReadOnlyList<string> diagnostics)
    {
        _words = words;
        Capacity = capacity;
        IsComplete = complete;
        Diagnostics = diagnostics;
    }

    /// <summary>Highest page index the bitmap can describe.</summary>
    public int Capacity { get; }

    /// <summary>Whether every word of the bitmap was read successfully.</summary>
    public bool IsComplete { get; }

    /// <summary>Problems encountered while reading the bitmap.</summary>
    public IReadOnlyList<string> Diagnostics { get; }

    /// <summary>Total set bits — the number of pages the bitmap claims are allocated.</summary>
    public int PopCount
    {
        get
        {
            var total = 0;
            for (var i = 0; i < _words.Length; i++)
            {
                total += BitOperations.PopCount(_words[i]);
            }

            return total;
        }
    }

    /// <summary>Whether the bitmap marks a file page as allocated. Out-of-range indices read as <c>false</c>.</summary>
    /// <param name="filePageIndex">The file page to test.</param>
    public bool IsAllocated(int filePageIndex)
    {
        if (filePageIndex < 0 || filePageIndex >= Capacity)
        {
            return false;
        }

        var word = filePageIndex >> 6;
        return word < _words.Length && (_words[word] & (1UL << (filePageIndex & 63))) != 0;
    }

    /// <summary>Reads the bitmap out of the occupancy segment's data pages.</summary>
    /// <param name="source">The page source.</param>
    /// <param name="occupancySegment">The walked occupancy segment.</param>
    public static OccupancyView Read(IPageSource source, SegmentView occupancySegment)
    {
        var diagnostics = new List<string>();
        if (occupancySegment == null || occupancySegment.Pages.Count < 2)
        {
            diagnostics.Add("The occupancy segment has no data pages; page-allocation state is unavailable.");
            return new OccupancyView([], 0, false, diagnostics);
        }

        // Data pages are the segment's pages beyond the directory-only root. Word i lives on data page (i / WordsPerPage).
        var dataPageCount = occupancySegment.Pages.Count - 1;
        var wordCount = dataPageCount * WordsPerPage;
        var words = new ulong[wordCount];
        var complete = true;

        Span<byte> buf = new byte[IntegrityConstants.PageSize];
        for (var d = 0; d < dataPageCount; d++)
        {
            var filePage = occupancySegment.Pages[d + 1];
            if (!source.TryReadPage(filePage, buf))
            {
                diagnostics.Add($"Occupancy data page {filePage} (segment page {d + 1}) could not be read; {WordsPerPage} bitmap words are unknown.");
                complete = false;
                continue;
            }

            var src = MemoryMarshal.Cast<byte, ulong>(PageImage.RawData(buf))[..WordsPerPage];
            src.CopyTo(words.AsSpan(d * WordsPerPage, WordsPerPage));
        }

        return new OccupancyView(words, wordCount * 64, complete, diagnostics);
    }
}
