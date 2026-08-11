using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>How a page came to be considered reachable.</summary>
internal enum PageRole : byte
{
    /// <summary>No structure claims this page.</summary>
    Unclaimed = 0,

    /// <summary>One of the fixed pages reserved at genesis (meta pair, occupancy root/twin/reserves).</summary>
    Reserved,

    /// <summary>A segment's directory root page.</summary>
    SegmentRoot,

    /// <summary>A directory map-extension page.</summary>
    MapExtension,

    /// <summary>A data page listed in a segment's directory.</summary>
    SegmentData,

    /// <summary>The shadow slot of an A/B protected directory page. Occupancy-set, but in no segment's page list.</summary>
    DirectoryTwin
}

/// <summary>One logical segment as recovered from raw bytes.</summary>
internal sealed class SegmentView
{
    /// <summary>File-page index of the segment's directory root.</summary>
    public int RootPageIndex { get; init; }

    /// <summary>Kind recorded in the root page's header.</summary>
    public StorageSegmentKind Kind { get; init; }

    /// <summary>The file pages the directory enumerates, in directory order. <c>[0]</c> is the root.</summary>
    public IReadOnlyList<int> Pages { get; init; } = [];

    /// <summary>Directory map-extension pages walked to read the full directory (the root is excluded).</summary>
    public IReadOnlyList<int> MapExtensionPages { get; init; } = [];

    /// <summary>Twin (shadow) pages of this segment's directory pages.</summary>
    public IReadOnlyList<int> TwinPages { get; init; } = [];

    /// <summary>Pages counted by following the forward <c>NextRawDataPBID</c> chain, for the chain-vs-directory cross-check.</summary>
    public int ForwardChainCount { get; init; }

    /// <summary>Whether the directory walk terminated cleanly rather than hitting a cycle, a bad pointer or the page limit.</summary>
    public bool DirectoryComplete { get; init; }

    /// <summary>Whether the forward-chain walk terminated cleanly.</summary>
    public bool ChainComplete { get; init; }

    /// <summary>Problems encountered while walking this segment, for the check layer to turn into findings.</summary>
    public IReadOnlyList<string> WalkDiagnostics { get; init; } = [];
}

/// <summary>
/// Discovers and walks every logical segment in a data file from raw bytes.
/// </summary>
/// <remarks>
/// <para>
/// Segment discovery is <b>physical</b>, not bootstrap-driven: every segment root carries
/// <see cref="PageBlockFlags.IsLogicalSegmentRoot"/> in its own header, so a full page sweep finds every segment without
/// trusting a dictionary that may itself be damaged. The bootstrap's segment pointers are then treated as a <i>claim to
/// verify</i> rather than the source of truth — which is the same primary-over-derived discipline the rest of the
/// catalogue applies.
/// </para>
/// <para>
/// Every pointer is range-checked against the file before it is followed, and every walk is bounded by a step limit. This
/// is not defensive style, it is a hard requirement: a checker that dereferences a torn directory into a crash is worse
/// than useless on exactly the databases it exists to diagnose.
/// </para>
/// </remarks>
internal sealed class SegmentWalker
{
    private readonly IPageSource _source;
    private readonly byte[] _scratchA = new byte[IntegrityConstants.PageSize];
    private readonly byte[] _scratchB = new byte[IntegrityConstants.PageSize];

    /// <summary>Number of int entries a directory page holds — the whole raw-data area.</summary>
    internal const int DirectoryEntriesPerPage = IntegrityConstants.PageRawDataSize / sizeof(int);

    /// <summary>Creates a walker over a page source.</summary>
    /// <param name="source">The page source to read through.</param>
    public SegmentWalker(IPageSource source) => _source = source;

    /// <summary>
    /// Resolves the current slot of an A/B protected directory page. Returns the physical page index whose content is
    /// current, reading both slots and preferring the higher valid generation.
    /// </summary>
    /// <param name="primaryPage">The primary (directory-referenced) page index.</param>
    /// <param name="image">Receives the selected slot's page image.</param>
    /// <param name="resolution">Receives a description of what was found in each slot.</param>
    /// <returns><c>true</c> when at least one slot was valid.</returns>
    public bool TryResolveDirectoryPage(int primaryPage, Span<byte> image, out DirectoryPairResolution resolution)
    {
        resolution = default;
        if (!_source.TryReadPage(primaryPage, _scratchA))
        {
            return false;
        }

        var flags = PageImage.Flags(_scratchA);
        var twin = PageImage.TwinPage(_scratchA);
        var primaryValid = IsPairSlotValid(_scratchA, out var genPrimary);

        // No twin recorded: not a paired directory page. Its own image is all there is.
        if ((flags & PageBlockFlags.IsLogicalSegment) == 0 || twin == 0)
        {
            _scratchA.CopyTo(image);
            resolution = new DirectoryPairResolution(primaryPage, 0, primaryPage, genPrimary, primaryValid, false, false);
            return true;
        }

        var twinValid = false;
        ulong genTwin = 0;
        var twinInRange = twin > 0 && twin < _source.PageCount;
        if (twinInRange && _source.TryReadPage(twin, _scratchB))
        {
            twinValid = IsPairSlotValid(_scratchB, out genTwin);
        }

        if (!primaryValid && !twinValid)
        {
            resolution = new DirectoryPairResolution(primaryPage, twin, -1, 0, false, false, twinInRange);
            return false;
        }

        if (twinValid && (!primaryValid || genTwin > genPrimary))
        {
            _scratchB.CopyTo(image);
            resolution = new DirectoryPairResolution(primaryPage, twin, twin, genTwin, primaryValid, true, twinInRange);
            return true;
        }

        _scratchA.CopyTo(image);
        resolution = new DirectoryPairResolution(primaryPage, twin, primaryPage, genPrimary, true, twinValid, twinInRange);
        return true;
    }

    /// <summary>
    /// Walks one segment: resolves its directory pages through their A/B pairs, reads the page directory, and follows the
    /// forward data-page chain for the cross-check.
    /// </summary>
    /// <param name="rootPageIndex">The segment's root page index.</param>
    public SegmentView WalkSegment(int rootPageIndex)
    {
        var diagnostics = new List<string>();
        var pages = new List<int>();
        var mapExtensions = new List<int>();
        var twins = new List<int>();
        Span<byte> image = new byte[IntegrityConstants.PageSize];

        if (!TryResolveDirectoryPage(rootPageIndex, image, out var rootRes))
        {
            diagnostics.Add(rootRes.TwinPage != 0
                ? $"Both slots of root page {rootPageIndex} (twin {rootRes.TwinPage}) are invalid; the segment cannot be read."
                : $"Root page {rootPageIndex} is invalid and has no twin; the segment cannot be read.");
            return new SegmentView { RootPageIndex = rootPageIndex, WalkDiagnostics = diagnostics, DirectoryComplete = false, ChainComplete = false };
        }

        var kind = PageImage.SegmentKind(image);
        if (rootRes.TwinPage != 0)
        {
            twins.Add(rootRes.TwinPage);
        }

        // Directory walk: root page's raw data holds int page indices, terminated by 0, continued on map-extension pages.
        var currentDirectoryPage = rootPageIndex;
        var complete = false;
        var visitedDirectoryPages = new HashSet<int> { rootPageIndex };
        var maxDirectoryPages = Math.Max(2, (_source.PageCount / DirectoryEntriesPerPage) + 2);

        for (var step = 0; step < maxDirectoryPages; step++)
        {
            var entries = MemoryMarshal.Cast<byte, int>(PageImage.RawData(image))[..DirectoryEntriesPerPage];
            var hitTerminator = false;
            for (var i = 0; i < entries.Length; i++)
            {
                var p = entries[i];
                if (p == 0)
                {
                    hitTerminator = true;
                    break;
                }

                if (p < 0 || p >= _source.PageCount)
                {
                    diagnostics.Add($"Directory entry {pages.Count} on page {currentDirectoryPage} is out of range ({p}); the walk stopped there.");
                    hitTerminator = true;
                    break;
                }

                pages.Add(p);
            }

            if (hitTerminator)
            {
                complete = true;
                break;
            }

            var nextMap = PageImage.NextMapPage(image);
            if (nextMap == 0)
            {
                complete = true;
                break;
            }

            if (nextMap < 0 || nextMap >= _source.PageCount)
            {
                diagnostics.Add($"Map-extension pointer on page {currentDirectoryPage} is out of range ({nextMap}).");
                break;
            }

            if (!visitedDirectoryPages.Add(nextMap))
            {
                diagnostics.Add($"Directory map chain cycles back to page {nextMap}.");
                break;
            }

            if (!TryResolveDirectoryPage(nextMap, image, out var mapRes))
            {
                diagnostics.Add($"Both slots of map-extension page {nextMap} (twin {mapRes.TwinPage}) are invalid; the directory is truncated.");
                break;
            }

            mapExtensions.Add(nextMap);
            if (mapRes.TwinPage != 0)
            {
                twins.Add(mapRes.TwinPage);
            }

            currentDirectoryPage = nextMap;
        }

        var chainCount = WalkForwardChain(rootPageIndex, pages.Count, out var chainComplete, diagnostics);

        return new SegmentView
        {
            RootPageIndex = rootPageIndex,
            Kind = kind,
            Pages = pages,
            MapExtensionPages = mapExtensions,
            TwinPages = twins,
            ForwardChainCount = chainCount,
            DirectoryComplete = complete,
            ChainComplete = chainComplete,
            WalkDiagnostics = diagnostics
        };
    }

    /// <summary>
    /// Counts pages on the forward <c>NextRawDataPBID</c> chain. The directory and the chain are written by independent
    /// code paths, so a mismatch localises a lost write precisely.
    /// </summary>
    private int WalkForwardChain(int rootPageIndex, int directoryCount, out bool complete, List<string> diagnostics)
    {
        complete = false;
        Span<byte> buf = new byte[IntegrityConstants.PageSize];
        if (!_source.TryReadPage(rootPageIndex, buf))
        {
            return 0;
        }

        var count = 1;
        var maxWalk = (directoryCount * 2) + 16;
        var visited = new HashSet<int> { rootPageIndex };

        while (count < maxWalk)
        {
            var next = PageImage.NextRawDataPage(buf);
            if (next == 0)
            {
                complete = true;
                return count;
            }

            if (next < 0 || next >= _source.PageCount)
            {
                diagnostics.Add($"Forward-chain pointer out of range ({next}) after {count} pages.");
                return count;
            }

            if (!visited.Add(next))
            {
                diagnostics.Add($"Forward data-page chain cycles back to page {next}.");
                return count;
            }

            if (!_source.TryReadPage(next, buf))
            {
                diagnostics.Add($"Forward-chain page {next} could not be read.");
                return count;
            }

            count++;
        }

        diagnostics.Add($"Forward data-page chain exceeded its {maxWalk}-page bound; it is cyclic or wildly longer than the directory.");
        return count;
    }

    /// <summary>
    /// A protected-pair slot is valid exactly when its whole-page checksum matches and its pair generation is non-zero,
    /// mirroring the engine's own selection predicate.
    /// </summary>
    private static bool IsPairSlotValid(ReadOnlySpan<byte> slot, out ulong generation)
    {
        generation = 0;
        if (!PageImage.VerifyWholePageChecksum(slot, out _))
        {
            return false;
        }

        generation = PageImage.PairGeneration(slot);
        return generation > 0;
    }
}

/// <summary>What the walker found when resolving an A/B protected directory page.</summary>
/// <param name="PrimaryPage">The directory-referenced page index.</param>
/// <param name="TwinPage">The shadow slot index, or <c>0</c> when the page is not paired.</param>
/// <param name="SelectedPage">The slot whose content was used, or <c>-1</c> when neither was valid.</param>
/// <param name="SelectedGeneration">Pair generation of the selected slot.</param>
/// <param name="PrimaryValid">Whether the primary slot verified.</param>
/// <param name="TwinValid">Whether the twin slot verified.</param>
/// <param name="TwinInRange">Whether the recorded twin index addressed a page inside the file.</param>
internal readonly record struct DirectoryPairResolution(int PrimaryPage, int TwinPage, int SelectedPage, ulong SelectedGeneration,
    bool PrimaryValid, bool TwinValid, bool TwinInRange);
