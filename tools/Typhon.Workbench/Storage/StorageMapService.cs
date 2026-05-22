using System;
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
            Convert.ToBase64String(typeBytes), Convert.ToBase64String(ownerBytes));
    }

    /// <summary>Builds the top pyramid levels for <c>GET /dbmap/overview</c>.</summary>
    public StorageOverviewDto GetOverview(DatabaseEngine engine, string databaseName)
    {
        var map = BuildMap(engine, databaseName);
        return StorageMapPyramid.BuildOverview(map.PageType, map.HilbertOrder, OverviewMaxLevels);
    }

    /// <summary>
    /// Introspects the engine into a coarse <see cref="StructuralMap"/>. Reads only in-memory structures — the
    /// occupancy bitmap and the segment registry — so the whole-file map costs no page-body disk I/O.
    /// </summary>
    internal static StructuralMap BuildMap(DatabaseEngine engine, string databaseName)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var mmf = engine.MMF;
        var pageCount = mmf.StorageFilePageCount;

        var pageType = new StoragePageType[pageCount];
        engine.ClassifyAllPages(pageType);

        var segments = engine.EnumerateStorageSegments();
        var ownerSegmentId = new ushort[pageCount];
        ownerSegmentId.AsSpan().Fill(StructuralMap.NoSegment);

        var segInfos = new StorageSegmentInfo[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var id = (ushort)i;
            segInfos[i] = new StorageSegmentInfo(id, seg.RootPageIndex, seg.Kind, seg.Pages.Length,
                seg.Stride, seg.ChunkCountRootPage, seg.ChunkCountPerPage, seg.RootDataOffset, seg.OtherDataOffset);
            // Mirror the engine classifier (DatabaseEngine.StorageIntrospection.cs): the occupancy bitmap is
            // authoritative — a page that ClassifyAllPages marked Free stays unowned even if a segment's
            // page list still references it. Keeps `pageType` and `ownerSegmentId` consistent.
            foreach (var page in seg.Pages.Span)
            {
                if ((uint)page < (uint)pageCount && pageType[page] != StoragePageType.Free)
                {
                    ownerSegmentId[page] = id;
                }
            }
        }

        // §5.5 — past the cell budget the coarse arrays are down-sampled (one descriptor per `factor` pages) so a
        // multi-GB database stays bounded in browser memory. The arrays are then in cell space; DataFilePageCount
        // stays the real page count for byte math, and DownSampleFactor bridges the two.
        var factor = DownSampleFactorFor(pageCount, MaxCoarseCells);
        var cellType = pageType;
        var cellOwner = ownerSegmentId;
        if (factor > 1)
        {
            DownSampleArrays(pageType, ownerSegmentId, factor, out cellType, out cellOwner);
        }

        return new StructuralMap
        {
            DatabaseName = string.IsNullOrEmpty(databaseName) ? "database" : databaseName,
            DataFileBytes = mmf.FileSize,
            DataFilePageCount = pageCount,
            WalBytes = engine.GetWalTotalBytes(),
            HilbertOrder = HilbertOrderFor(cellType.Length),
            CheckpointLsn = engine.CheckpointManager?.CheckpointLsn ?? 0L,
            DownSampleFactor = factor,
            PageType = cellType,
            OwnerSegmentId = cellOwner,
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
    internal static void DownSampleArrays(StoragePageType[] pageType, ushort[] ownerSegmentId, int factor,
        out StoragePageType[] cellType, out ushort[] cellOwner)
    {
        var cellCount = CellCountFor(pageType.Length, factor);
        cellType = new StoragePageType[cellCount];
        cellOwner = new ushort[cellCount];
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
