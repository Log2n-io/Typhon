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
        return new StorageRegionDto(node, string.IsNullOrEmpty(lod) ? "leaf" : lod, map.DataFilePageCount,
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
            foreach (var page in seg.Pages.Span)
            {
                if ((uint)page < (uint)pageCount)
                {
                    ownerSegmentId[page] = id;
                }
            }
        }

        return new StructuralMap
        {
            DatabaseName = string.IsNullOrEmpty(databaseName) ? "database" : databaseName,
            DataFileBytes = mmf.FileSize,
            DataFilePageCount = pageCount,
            WalBytes = engine.GetWalTotalBytes(),
            HilbertOrder = HilbertOrderFor(pageCount),
            CheckpointLsn = engine.CheckpointManager?.CheckpointLsn ?? 0L,
            DownSampleFactor = 1,
            PageType = pageType,
            OwnerSegmentId = ownerSegmentId,
            Segments = segInfos,
        };
    }

    /// <summary>Smallest Hilbert order <c>n</c> such that <c>4^n ≥ pageCount</c>.</summary>
    internal static int HilbertOrderFor(int pageCount)
    {
        var n = 0;
        long cells = 1;
        while (cells < pageCount)
        {
            cells <<= 2;
            n++;
        }
        return n;
    }
}
