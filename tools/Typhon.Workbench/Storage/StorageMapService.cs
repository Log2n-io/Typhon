using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Storage;

namespace Typhon.Workbench.Storage;

/// <summary>
/// Produces the Database File Map (Module 15, Track A) by introspecting a live <see cref="DatabaseEngine"/> —
/// the live-provider pattern, mirroring <c>LiveSchemaProvider</c>. Stateless: every method rebuilds a coarse
/// <see cref="StructuralMap"/> from in-memory engine structures, with no page-body disk I/O.
/// </summary>
public sealed partial class StorageMapService
{
    /// <summary>Number of pyramid levels (0-based) returned by <see cref="GetOverview"/>.</summary>
    private const int OverviewMaxLevels = 5;

    /// <summary>
    /// Memoized coarse map per live engine. <see cref="BuildMap"/>'s whole-file <c>ClassifyAllPages</c> plus the
    /// O(pageCount) descriptor arrays are rebuilt only when the structure changes. A viewport pan fires many
    /// <see cref="GetRegionDetail"/> / <see cref="GetPageDetail"/> tile requests that all reuse the same map; before
    /// this each tile rebuilt it (a multi-MB allocation + a full classify per tile). Keyed by engine (a
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> entry is collected when the session's engine is) and a cheap
    /// structural signature so a mutation rebuilds and a quiet pan hits.
    /// </summary>
    private readonly ConditionalWeakTable<DatabaseEngine, CachedMap> _mapCache = new();

    private sealed class CachedMap
    {
        public long Signature;
        public StructuralMap Map;
    }

    /// <summary>Builds the region headers + segment table for <c>GET /dbmap/regions</c>.</summary>
    public StorageRegionsDto GetRegions(DatabaseEngine engine, string databaseName)
    {
        var map = BuildMap(engine, databaseName);
        var segments = new StorageSegmentDto[map.Segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var s = map.Segments[i];
            // Resolve the user component type name for component segments — it drives the map's search box.
            // In-memory only (walks the component-table registry, no page I/O), so the coarse tier stays free.
            var typeName = s.Kind == StorageSegmentKind.Component
                ? ResolveComponentDefinition(engine, s.RootPageIndex)?.Name ?? ""
                : "";
            segments[i] = new StorageSegmentDto(s.Id, s.RootPageIndex, s.Kind.ToString(), s.PageCount, typeName);
        }
        return new StorageRegionsDto(map.DatabaseName, map.DataFileBytes, map.DataFilePageCount, map.WalBytes,
            map.HilbertOrder, map.CheckpointLsn, map.DownSampleFactor, DetailTileSize, segments);
    }

    /// <summary>
    /// Builds the aggregate health rollup for <c>GET /dbmap/health</c> (GAP-16) — the whole-DB summary plus a
    /// per-segment table. Reuses the page classifier for used/free counts and the segment registry for chunk
    /// allocation, enumerating segments once (vs the client calling segment-summary per segment). In-memory only.
    /// </summary>
    public StorageHealthDto GetHealth(DatabaseEngine engine, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var mmf = engine.MMF;
        var pageCount = mmf.StorageFilePageCount;
        var pageType = new StoragePageType[pageCount];
        engine.ClassifyAllPages(pageType);

        var usedPages = 0;
        for (var i = 0; i < pageCount; i++)
        {
            if (pageType[i] != StoragePageType.Free)
            {
                usedPages++;
            }
        }
        var freePages = pageCount - usedPages;

        var segments = engine.EnumerateStorageSegments();
        var rows = new StorageHealthSegmentDto[segments.Count];
        long totalReclaimable = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var typeName = seg.Kind == StorageSegmentKind.Component
                ? ResolveComponentDefinition(engine, seg.RootPageIndex)?.Name ?? ""
                : "";
            var capacity = seg.ChunkCapacity;
            var reclaimable = (long)seg.FreeChunkCount * seg.Stride;
            totalReclaimable += reclaimable;
            var chunkFillPct = capacity > 0 ? (double)seg.AllocatedChunkCount / capacity * 100.0 : 0.0;

            long entityCount = 0;
            var occupancyPct = chunkFillPct;
            if (seg.Kind == StorageSegmentKind.Cluster &&
                engine.TryGetClusterStats(seg.RootPageIndex, out entityCount, out var activeClusterCount, out var clusterSize))
            {
                var slots = (long)activeClusterCount * clusterSize;
                occupancyPct = slots > 0 ? (double)entityCount / slots * 100.0 : 0.0;
            }

            rows[i] = new StorageHealthSegmentDto(i, seg.Kind.ToString(), typeName, seg.Pages.Length,
                seg.AllocatedChunkCount, capacity, chunkFillPct, reclaimable, entityCount, occupancyPct);
        }

        var fragmentationPct = pageCount > 0 ? (double)freePages / pageCount * 100.0 : 0.0;
        return new StorageHealthDto(
            string.IsNullOrEmpty(databaseName) ? "database" : databaseName,
            mmf.FileSize, pageCount, usedPages, freePages,
            engine.GetWalTotalBytes(), engine.CheckpointManager?.CheckpointLsn ?? 0L,
            segments.Count, totalReclaimable, fragmentationPct, rows);
    }

    /// <summary>
    /// Builds the coarse per-page descriptors for <c>GET /dbmap/region</c>. In A1 the whole coarse map is
    /// returned in one call; <paramref name="node"/> / <paramref name="lod"/> are reserved for A2 tiling.
    /// </summary>
    public StorageRegionDto GetRegion(DatabaseEngine engine, string databaseName, int node, string lod)
    {
        var map = BuildMap(engine, databaseName);
        var typeBytes = MemoryMarshal.AsBytes<StoragePageType>(map.PageType);
        var ownerBytes = MemoryMarshal.AsBytes<ushort>(map.OwnerSegmentId);
        // PageCount is the descriptor-array length — the cell count, which equals the page count when exact.
        return new StorageRegionDto(node, string.IsNullOrEmpty(lod) ? "leaf" : lod, map.CellCount,
            Convert.ToBase64String(typeBytes), Convert.ToBase64String(ownerBytes), Convert.ToBase64String(map.PageRank));
    }

    /// <summary>Builds the top pyramid levels for <c>GET /dbmap/overview</c>.</summary>
    public StorageOverviewDto GetOverview(DatabaseEngine engine, string databaseName)
    {
        var map = BuildMap(engine, databaseName);
        return StorageMapPyramid.BuildOverview(map.PageType, map.HilbertOrder, OverviewMaxLevels);
    }

    /// <summary>
    /// Introspects the engine into a coarse <see cref="StructuralMap"/>. Reads only in-memory structures — the
    /// occupancy bitmap and the segment registry — so the whole-file map costs no page-body disk I/O. Memoized per
    /// engine (see <see cref="_mapCache"/>): the cheap structural signature is recomputed each call, but the
    /// O(pageCount) classify + arrays are skipped while it is unchanged.
    /// </summary>
    internal StructuralMap BuildMap(DatabaseEngine engine, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var segments = engine.EnumerateStorageSegments();
        var pageCount = engine.MMF.StorageFilePageCount;
        var walBytes = engine.GetWalTotalBytes();
        var checkpointLsn = engine.CheckpointManager?.CheckpointLsn ?? 0L;
        var signature = ComputeSignature(pageCount, walBytes, checkpointLsn, segments);

        var entry = _mapCache.GetOrCreateValue(engine);
        lock (entry)
        {
            if (entry.Map != null && entry.Signature == signature)
            {
                return entry.Map;
            }
            var rebuilt = BuildMapCore(engine, databaseName, segments, pageCount, walBytes, checkpointLsn);
            entry.Signature = signature;
            entry.Map = rebuilt;
            return rebuilt;
        }
    }

    /// <summary>
    /// FNV-1a fold over the structural inputs of <see cref="BuildMapCore"/>. Every segment add/remove, per-segment
    /// page growth, file growth, WAL append, or checkpoint moves the value — so the cached map is rebuilt exactly
    /// when one of its fields could have changed. WAL bytes + checkpoint LSN cover occupancy-only flips (a page going
    /// <c>used→Free</c> without a segment page-list change), since every structural mutation in an ACID engine is journaled.
    /// </summary>
    private static long ComputeSignature(int pageCount, long walBytes, long checkpointLsn, IReadOnlyList<StorageSegmentDescriptor> segments)
    {
        unchecked
        {
            var hash = 1469598103934665603L; // FNV-1a 64-bit offset basis
            hash = (hash ^ pageCount) * 1099511628211L;
            hash = (hash ^ walBytes) * 1099511628211L;
            hash = (hash ^ checkpointLsn) * 1099511628211L;
            hash = (hash ^ segments.Count) * 1099511628211L;
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                hash = (hash ^ seg.RootPageIndex) * 1099511628211L;
                hash = (hash ^ seg.Pages.Length) * 1099511628211L;
                hash = (hash ^ (byte)seg.Kind) * 1099511628211L;
            }
            return hash;
        }
    }

    /// <summary>
    /// The uncached build. Takes the already-fetched <paramref name="segments"/> and volatile counters so a cache
    /// miss never re-enumerates or re-reads them.
    /// </summary>
    private static StructuralMap BuildMapCore(DatabaseEngine engine, string databaseName, IReadOnlyList<StorageSegmentDescriptor> segments,
        int pageCount, long walBytes, long checkpointLsn)
    {
        var mmf = engine.MMF;

        var pageType = new StoragePageType[pageCount];
        engine.ClassifyAllPages(pageType);

        var ownerSegmentId = new ushort[pageCount];
        ownerSegmentId.AsSpan().Fill(StructuralMap.NoSegment);
        var pageRank = new byte[pageCount]; // normalized directory-order position within the owning segment (0 = first page, 255 = last)

        var segInfos = new StorageSegmentInfo[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var id = (ushort)i;
            segInfos[i] = new StorageSegmentInfo(id, seg.RootPageIndex, seg.Kind, seg.Pages.Length,
                seg.Stride, seg.ChunkCountRootPage, seg.ChunkCountPerPage, seg.RootDataOffset, seg.OtherDataOffset);
            // Mirror the engine classifier (DatabaseEngine.StorageIntrospection.cs): the occupancy bitmap is
            // authoritative — a page that ClassifyAllPages marked Free stays unowned even if a segment's
            // page list still references it. Keeps `pageType` and `ownerSegmentId` consistent. The page list is in
            // directory (logical) order, so the loop index is the page's rank within the segment.
            var segPages = seg.Pages.Span;
            var rankDenom = Math.Max(1, segPages.Length - 1);
            for (var k = 0; k < segPages.Length; k++)
            {
                var page = segPages[k];
                if ((uint)page < (uint)pageCount && pageType[page] != StoragePageType.Free)
                {
                    ownerSegmentId[page] = id;
                    pageRank[page] = (byte)(k * 255 / rankDenom);
                }
            }
        }

        // §5.5 — past the cell budget the coarse arrays are down-sampled (one descriptor per `factor` pages) so a
        // multi-GB database stays bounded in browser memory. The arrays are then in cell space; DataFilePageCount
        // stays the real page count for byte math, and DownSampleFactor bridges the two.
        var factor = DownSampleFactorFor(pageCount, MaxCoarseCells);
        var cellType = pageType;
        var cellOwner = ownerSegmentId;
        var cellRank = pageRank;
        if (factor > 1)
        {
            DownSampleArrays(pageType, ownerSegmentId, pageRank, factor, out cellType, out cellOwner, out cellRank);
        }

        return new StructuralMap
        {
            DatabaseName = string.IsNullOrEmpty(databaseName) ? "database" : databaseName,
            DataFileBytes = mmf.FileSize,
            DataFilePageCount = pageCount,
            WalBytes = walBytes,
            HilbertOrder = HilbertOrderFor(cellType.Length),
            CheckpointLsn = checkpointLsn,
            DownSampleFactor = factor,
            PageType = cellType,
            OwnerSegmentId = cellOwner,
            PageRank = cellRank,
            Segments = segInfos,
        };
    }

    /// <summary>Smallest Hilbert order <c>n</c> such that <c>4^n ≥ cellCount</c>.</summary>
    internal static int HilbertOrderFor(int cellCount)
    {
        var n = 0;
        long cells = 1;
        while (cells < cellCount)
        {
            cells <<= 2;
            n++;
        }
        return n;
    }

    /// <summary>
    /// Coarse-map cell budget (§5.5). Past this the map is down-sampled — one descriptor per <c>factor</c> pages,
    /// <c>factor</c> a power of 4 — so a multi-GB database stays bounded in browser memory. Mutable so tests can
    /// lower it and exercise down-sampling without a multi-GB fixture.
    /// </summary>
    internal static int MaxCoarseCells = 1 << 20;

    /// <summary>Descriptor cells for <paramref name="pageCount"/> pages at down-sample <paramref name="factor"/>.</summary>
    internal static int CellCountFor(int pageCount, int factor) => (pageCount + factor - 1) / factor;

    /// <summary>Smallest power-of-4 factor such that the down-sampled cell count fits within <paramref name="maxCells"/>.</summary>
    internal static int DownSampleFactorFor(int pageCount, int maxCells)
    {
        var factor = 1;
        while (CellCountFor(pageCount, factor) > maxCells)
        {
            factor <<= 2;
        }
        return factor;
    }

    /// <summary>
    /// Aggregates the per-page coarse arrays into one descriptor per <paramref name="factor"/> pages — the dominant
    /// non-free type, and the dominant owning segment (<see cref="StructuralMap.NoSegment"/> when unowned wins).
    /// </summary>
    internal static void DownSampleArrays(StoragePageType[] pageType, ushort[] ownerSegmentId, byte[] pageRank, int factor,
        out StoragePageType[] cellType, out ushort[] cellOwner, out byte[] cellRank)
    {
        var cellCount = CellCountFor(pageType.Length, factor);
        cellType = new StoragePageType[cellCount];
        cellOwner = new ushort[cellCount];
        cellRank = new byte[cellCount];
        Span<int> tally = stackalloc int[StorageMapPyramid.PageTypeCount];

        for (var c = 0; c < cellCount; c++)
        {
            var start = c * factor;
            var end = Math.Min(start + factor, pageType.Length);
            tally.Clear();
            for (var p = start; p < end; p++)
            {
                tally[(int)pageType[p]]++;
            }
            cellType[c] = StorageMapPyramid.DominantType(tally);
            cellOwner[c] = DominantOwner(ownerSegmentId, start, end);
            // The block's rank ≈ its first page's rank — within a down-sample block (a contiguous file-page run) the
            // directory-order rank is monotonic, so the leading page is a faithful representative.
            cellRank[c] = pageRank[start];
        }
    }

    /// <summary>The owning-segment id covering the most pages in <c>[start, end)</c> — ties keep the first seen.</summary>
    private static ushort DominantOwner(ushort[] ownerSegmentId, int start, int end)
    {
        var first = ownerSegmentId[start];
        var homogeneous = true;
        for (var i = start + 1; i < end; i++)
        {
            if (ownerSegmentId[i] != first)
            {
                homogeneous = false;
                break;
            }
        }
        if (homogeneous)
        {
            return first;
        }

        var bestOwner = first;
        var bestCount = 0;
        for (var i = start; i < end; i++)
        {
            var owner = ownerSegmentId[i];
            var count = 0;
            for (var j = start; j < end; j++)
            {
                if (ownerSegmentId[j] == owner)
                {
                    count++;
                }
            }
            if (count > bestCount)
            {
                bestCount = count;
                bestOwner = owner;
            }
        }
        return bestOwner;
    }
}
