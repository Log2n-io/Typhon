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

    /// <summary>Whether the forward chain was counted at all: never at Spine depth (#1143).</summary>
    public bool ChainWalked { get; init; }

    /// <summary>
    /// Whether every link of the forward chain names the directory's next entry, the last one 0. Only established when the chain was counted from
    /// recorded pointers; <c>true</c> otherwise, so a count comparison alone decides.
    /// </summary>
    public bool ChainMatchesDirectory { get; init; } = true;

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
    /// Walks one segment: resolves its directory pages through their A/B pairs and reads the page directory. Counts the forward data-page chain for
    /// the cross-check only from <paramref name="recordedNextPointers"/>; without them the chain is not looked at (<see cref="SegmentView.ChainWalked"/>).
    /// </summary>
    /// <remarks>
    /// Reading the chain from the file would read every data page of the segment, which is what made the Spine tier — on every open — O(database)
    /// (#1143). The only pass that wants the chain is the Quick discovery sweep, which reads every page anyway and records each one's pointer.
    /// </remarks>
    /// <param name="rootPageIndex">The segment's root page index.</param>
    /// <param name="recordedNextPointers">
    /// Every page's forward pointer, recorded by a pass that already read every page: the chain is then counted in memory, reading nothing, and
    /// compared with the directory position by position. <c>null</c> to read the directory only.
    /// </param>
    /// <param name="unreadPages">With <paramref name="recordedNextPointers"/>: the pages that pass could not read.</param>
    public SegmentView WalkSegment(int rootPageIndex, int[] recordedNextPointers = null, bool[] unreadPages = null)
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

        var chainCount = 0;
        var chainComplete = false;
        var chainMatches = true;
        var walkChain = recordedNextPointers != null;
        if (walkChain)
        {
            chainCount = CountRecordedChain(rootPageIndex, pages, recordedNextPointers, unreadPages, out chainComplete, out chainMatches, diagnostics);
        }

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
            ChainWalked = walkChain,
            ChainMatchesDirectory = chainMatches,
            WalkDiagnostics = diagnostics
        };
    }

    /// <summary>
    /// Counts the forward <c>NextRawDataPBID</c> chain over pointers already recorded for every page: no page is read. The directory and the chain
    /// are written by independent code paths, so a mismatch localises a lost write precisely. Also compares the chain with the
    /// directory position by position — page <c>i</c> must link to directory entry <c>i+1</c>, the last to 0 — the check the engine's crash-path
    /// load makes (<c>ChunkBasedSegment.ScanForAllocatorState</c>), so a chain of the right length in the wrong order is caught too.
    /// </summary>
    /// <remarks>
    /// No visited set: a chain that outruns its bound is classified afterwards by a constant-memory cycle test, which keeps a segment of millions of
    /// pages from costing a hash set of millions of entries.
    /// </remarks>
    private int CountRecordedChain(int rootPageIndex, List<int> directory, int[] next, bool[] unread, out bool complete, out bool matchesDirectory,
        List<string> diagnostics)
    {
        complete = false;
        matchesDirectory = false;
        if (unread != null && unread[rootPageIndex])
        {
            diagnostics.Add($"Forward-chain root page {rootPageIndex} could not be read.");
            return 0;
        }

        var matches = true;
        var count = 1;
        var maxWalk = (directory.Count * 2) + 16;
        var current = rootPageIndex;

        while (count < maxWalk)
        {
            var nextPage = next[current];
            if (nextPage != (count < directory.Count ? directory[count] : 0))
            {
                matches = false;
            }

            if (nextPage == 0)
            {
                complete = true;
                matchesDirectory = matches;
                return count;
            }

            if (nextPage < 0 || nextPage >= _source.PageCount)
            {
                diagnostics.Add($"Forward-chain pointer out of range ({nextPage}) after {count} pages.");
                return count;
            }

            if (unread != null && unread[nextPage])
            {
                diagnostics.Add($"Forward-chain page {nextPage} could not be read.");
                return count;
            }

            current = nextPage;
            count++;
        }

        diagnostics.Add(HasCycle(rootPageIndex, next, unread)
            ? $"Forward data-page chain cycles: it never ends, and passed its {maxWalk}-page bound."
            : $"Forward data-page chain exceeded its {maxWalk}-page bound; it is wildly longer than the directory.");
        return count;
    }

    /// <summary>Floyd's test over recorded pointers: whether the chain from <paramref name="root"/> loops. Constant memory.</summary>
    private static bool HasCycle(int root, int[] next, bool[] unread)
    {
        static int Step(int page, int[] next, bool[] unread) =>
            page <= 0 || page >= next.Length || (unread != null && unread[page]) ? 0 : next[page];

        var slow = root;
        var fast = root;
        while (true)
        {
            fast = Step(Step(fast, next, unread), next, unread);
            slow = Step(slow, next, unread);
            if (fast <= 0 || slow <= 0)
            {
                return false;
            }

            if (slow == fast)
            {
                return true;
            }
        }
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
