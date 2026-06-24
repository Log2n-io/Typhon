using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Engine.Internals;
using Typhon.Workbench.Dtos.Storage;
using Typhon.Workbench.Storage.Decoders;

namespace Typhon.Workbench.Storage;

// The Database File Map detail tier (Module 15, Track A — A2). Unlike the coarse tier (StorageMapService.cs),
// these methods fault page bodies in through the engine's no-clock-sweep read path — but always viewport-scoped:
// a detail-tile request reads only its quadtree node's pages, never the whole file (AC3).
public sealed partial class StorageMapService
{
    /// <summary>
    /// Pages per detail tile — a quadtree node at the detail LOD (<c>4^5</c>). The client requests the tiles
    /// intersecting the viewport; one request reads at most this many page bodies.
    /// </summary>
    public const int DetailTileSize = 1024;

    /// <summary>
    /// Builds the per-cell detail SoA for one detail tile — <c>GET /dbmap/region?node=&amp;lod=detail</c>. The tile
    /// is in cell space (matching the coarse arrays); on a down-sampled map (§5.5) each cell's detail is sampled
    /// from one representative page (<c>cell × factor</c>) — the response is then flagged <c>Approximate</c>.
    /// </summary>
    public StorageRegionDetailDto GetRegionDetail(DatabaseEngine engine, string databaseName, int node)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var map = BuildMap(engine, databaseName);
        var first = (long)node * DetailTileSize;
        if (node < 0 || first >= map.CellCount)
        {
            return new StorageRegionDetailDto(node, 0, 0, "", "", "", "", "", "", 0, "", "", false, 1);
        }

        var factor = map.DownSampleFactor;
        var start = (int)first;
        var count = Math.Min(DetailTileSize, map.CellCount - start);

        var fill = new byte[count];
        var changeRev = new int[count];
        var crc = new byte[count];
        var residency = new byte[count];
        var chunkUsed = new ushort[count];
        var chunkTotal = new ushort[count];
        var entropy = new byte[count];
        var byteClass = new byte[count];
        var body = new byte[engine.MMF.StoragePageSize];
        var maxRev = 0;

        for (var i = 0; i < count; i++)
        {
            var cell = start + i;
            // When down-sampled there is no per-page array — sample one representative page per cell (§5.5).
            var page = cell * factor;

            // Residency must be probed before the body read — the read itself faults the page in.
            engine.GetPageResidency(page, out var resident, out var dirty);

            if (map.PageType[cell] == StoragePageType.Free)
            {
                residency[i] = (byte)(resident ? (dirty ? StorageResidency.ResidentDirty : StorageResidency.ResidentClean) : StorageResidency.OnDiskOnly);
                crc[i] = (byte)StorageCrcStatus.Unverified;
                continue;
            }

            if (!engine.TryReadPageBody(page, body))
            {
                continue;
            }

            var header = MemoryMarshal.Read<PageBaseHeader>(body);
            changeRev[i] = header.ChangeRevision;
            if (header.ChangeRevision > maxRev)
            {
                maxRev = header.ChangeRevision;
            }

            crc[i] = (byte)ClassifyCrc(body, header.PageChecksum);
            residency[i] = (byte)(resident ? (dirty ? StorageResidency.ResidentDirty : StorageResidency.ResidentClean) : StorageResidency.OnDiskOnly);

            // Decode-free characterization (design §4.2): both reuse the page body already in hand — no extra I/O.
            entropy[i] = ShannonEntropy(body);
            byteClass[i] = (byte)L4Decoder.DominantByteClass(body);

            var seg = OwningChunkSegment(map, cell, page);
            if (seg.HasValue)
            {
                var total = seg.Value.IsRoot ? seg.Value.Info.ChunkCountRootPage : seg.Value.Info.ChunkCountPerPage;
                var used = CountOccupiedChunks(body, total);
                chunkTotal[i] = (ushort)Math.Min(total, ushort.MaxValue);
                chunkUsed[i] = (ushort)Math.Min(used, ushort.MaxValue);
                fill[i] = total > 0 ? (byte)Math.Clamp(used * 255 / total, 0, 255) : (byte)0;
            }
        }

        return new StorageRegionDetailDto(
            node, start, count,
            Convert.ToBase64String(fill),
            Convert.ToBase64String(MemoryMarshal.AsBytes<int>(changeRev)),
            Convert.ToBase64String(crc),
            Convert.ToBase64String(residency),
            Convert.ToBase64String(MemoryMarshal.AsBytes<ushort>(chunkUsed)),
            Convert.ToBase64String(MemoryMarshal.AsBytes<ushort>(chunkTotal)),
            maxRev,
            Convert.ToBase64String(entropy),
            Convert.ToBase64String(byteClass),
            factor > 1,
            factor);
    }

    /// <summary>
    /// Shannon entropy of a page body, scaled to 0..255 (decode-free, design §4.2). A 256-bin byte histogram
    /// gives <c>H = -Σ p·log2 p</c> in bits; the theoretical max is 8 bits, so the scaled value is <c>H·255/8</c>.
    /// A zeroed page reads 0; uniformly-random bytes read near 255.
    /// </summary>
    internal static byte ShannonEntropy(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty)
        {
            return 0;
        }
        Span<int> counts = stackalloc int[256];
        foreach (var b in body)
        {
            counts[b]++;
        }
        var len = (double)body.Length;
        var h = 0.0;
        for (var i = 0; i < 256; i++)
        {
            if (counts[i] == 0)
            {
                continue;
            }
            var p = counts[i] / len;
            h -= p * Math.Log2(p);
        }
        return (byte)Math.Clamp(h * 255.0 / 8.0, 0.0, 255.0);
    }

    /// <summary>Builds one page's full decode — <c>GET /dbmap/page/{idx}</c>. Returns <c>null</c> for an out-of-range index.</summary>
    public StoragePageDetailDto GetPageDetail(DatabaseEngine engine, string databaseName, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var map = BuildMap(engine, databaseName);
        if (pageIndex < 0 || pageIndex >= map.DataFilePageCount)
        {
            return null;
        }

        // The coarse arrays are cell-indexed (§5.5) — map the real page to its descriptor cell. The exact chunk
        // layout below is still resolved per real page from the segment directory, so down-sampling never blurs it.
        var cell = pageIndex / map.DownSampleFactor;

        engine.GetPageResidency(pageIndex, out var resident, out var dirty);

        var body = new byte[engine.MMF.StoragePageSize];
        var read = engine.TryReadPageBody(pageIndex, body);
        var header = read ? MemoryMarshal.Read<PageBaseHeader>(body) : default;
        var crcStatus = read ? ClassifyCrc(body, header.PageChecksum) : StorageCrcStatus.Unverified;
        var liveCrc = read ? Crc32CUtil.ComputeSkipping(body, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize) : 0u;

        var ownerId = map.OwnerSegmentId[cell];

        // Resolve the owning segment once — it yields the kind, the directory (when this page is the segment
        // root), the chunk layout, and the global id of this page's first chunk (FirstChunkId + i = an L4 ref).
        var ownerKind = "";
        var chunkUsed = 0;
        var chunkTotal = 0;
        var firstChunkId = 0;
        var occupancy = "";
        var chunkFill = "";
        var chunkClass = "";
        var occupancyMap = "";
        var occupancyFirst = 0L;
        var occupancyGoverned = 0;
        var occupancyCols = 0;
        var headerBytes = 0;
        var directoryBytes = 0;
        var paddingBytes = 0;
        StorageContentCellDto[] directory = [];

        if (ownerId != StructuralMap.NoSegment)
        {
            var segments = engine.EnumerateStorageSegments();
            var descriptor = segments[ownerId];
            ownerKind = descriptor.Kind.ToString();

            if (descriptor.RootPageIndex == pageIndex)
            {
                directory = L4Decoder.DecodeDirectory(descriptor.Pages.Span);
            }

            if (descriptor.IsChunkBased)
            {
                var dataOffset = descriptor.OtherDataOffset;
                // Decompose the bytes before chunk 0 into the three distinct overhead parts so the renderer maps the
                // page's memory honestly (A6): the fixed per-page header, the root-page segment directory (page-index
                // table), and the stride-alignment padding the engine inserts so chunks start stride-aligned. With the
                // directory-only root (v4) the root's directory fills the entire 8000-byte body, so a root page renders as
                // all-directory with zero chunks; only non-root data pages carry chunks. The padding grows with the stride
                // — that's why a large-stride segment (e.g. cluster) shows a wider overhead band on a non-root page.
                headerBytes = PagedMMF.PageHeaderSize;
                var pages = descriptor.Pages.Span;
                for (var k = 0; k < pages.Length; k++)
                {
                    if (pages[k] == pageIndex)
                    {
                        var isRoot = k == 0;
                        chunkTotal = isRoot ? descriptor.ChunkCountRootPage : descriptor.ChunkCountPerPage;
                        firstChunkId = isRoot ? 0 : descriptor.ChunkCountRootPage + (k - 1) * descriptor.ChunkCountPerPage;
                        dataOffset = isRoot ? descriptor.RootDataOffset : descriptor.OtherDataOffset;
                        directoryBytes = isRoot ? LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength : 0;
                        paddingBytes = dataOffset - headerBytes - directoryBytes;
                        break;
                    }
                }

                if (read && chunkTotal > 0)
                {
                    chunkUsed = CountOccupiedChunks(body, chunkTotal);
                    occupancy = Convert.ToBase64String(OccupancyBytes(body, chunkTotal));

                    // A Cluster chunk is a sub-allocator of N entity slots; OccupancyBits @chunk+0 popcounted over N
                    // is its intra-chunk fill (design §10.1). ContainerFill chunks colour by this ratio instead of
                    // binary occupancy. Every read is one cache line of the body already faulted in — no extra I/O.
                    if (descriptor.Kind == StorageSegmentKind.Cluster
                        && engine.TryGetClusterLayout(descriptor.RootPageIndex, out var clusterN, out _, out _, out _) && clusterN > 0)
                    {
                        (chunkFill, chunkClass) = BuildClusterChunkFill(body, chunkTotal, dataOffset, descriptor.Stride, clusterN);
                    }
                    else if ((descriptor.Kind == StorageSegmentKind.Vsbs || descriptor.Kind == StorageSegmentKind.ComponentCollection)
                        && engine.TryGetVsbsLayout(descriptor.RootPageIndex, out var vsbsElementSize, out _, out _) && vsbsElementSize > 0)
                    {
                        // A VSBS chunk packs ElementCount elements (header @chunk+4) of vsbsElementSize bytes; fill = used / capacity.
                        (chunkFill, chunkClass) = BuildVsbsChunkFill(body, chunkTotal, dataOffset, descriptor.Stride, vsbsElementSize);
                    }
                    else if (descriptor.Kind == StorageSegmentKind.StringTable)
                    {
                        // A string chunk holds min(SizeLeft, blockSize) UTF-8 bytes (SizeLeft @chunk+0 = bytes from this chunk on).
                        (chunkFill, chunkClass) = BuildStringChunkFill(body, chunkTotal, dataOffset, descriptor.Stride);
                    }
                    else if (descriptor.Kind == StorageSegmentKind.EntityMap
                        && engine.TryGetHashMapLayout(descriptor.RootPageIndex, out _, out _, out var hmBucketCapacity, out var hmNonData) && hmBucketCapacity > 0)
                    {
                        // A linear-hash bucket chunk holds EntryCount entries (header @chunk+4); fill = used / capacity. Structural
                        // chunks (meta / directory) are hatched, and an overflowing bucket is flagged (design §10.1).
                        (chunkFill, chunkClass) = BuildHashMapChunkFill(body, chunkTotal, dataOffset, descriptor.Stride, firstChunkId, hmBucketCapacity, hmNonData);
                    }
                    else if (descriptor.Kind == StorageSegmentKind.Index
                        && engine.TryGetIndexLayout(descriptor.RootPageIndex, out var idxDirectoryChunks, out _))
                    {
                        // A B-tree node is coloured leaf vs internal (self-describing from its control word); the shared directory chunks (0..n) are hatched as
                        // non-data. No intra-node fill-heat — B+tree nodes stay in a narrow [50%,100%] band, so the fill carries little signal (design §13 A6).
                        (chunkFill, chunkClass) = BuildIndexChunkClass(body, chunkTotal, dataOffset, descriptor.Stride, firstChunkId, idxDirectoryChunks);
                    }
                }
            }
            else if (descriptor.Kind == StorageSegmentKind.Occupancy)
            {
                // An occupancy page is not chunk-based but is the densest page in the file: it governs the
                // allocation state of a contiguous file-page range (design §10.2). Render it as a region-map.
                (occupancyMap, occupancyFirst, occupancyGoverned, occupancyCols) = BuildOccupancyRegionMap(engine, descriptor, pageIndex);
            }
        }

        return new StoragePageDetailDto(
            pageIndex,
            (long)pageIndex * engine.MMF.StoragePageSize,
            map.PageType[cell].ToString(),
            ownerId != StructuralMap.NoSegment ? ownerId : -1,
            ownerKind,
            header.ChangeRevision,
            header.FormatRevision,
            header.ModificationCounter,
            header.PageChecksum,
            liveCrc,
            crcStatus.ToString(),
            (resident ? (dirty ? StorageResidency.ResidentDirty : StorageResidency.ResidentClean) : StorageResidency.OnDiskOnly).ToString(),
            dirty ? 1 : 0,
            chunkUsed,
            chunkTotal,
            chunkTotal > 0 ? (double)chunkUsed / chunkTotal : 0.0,
            firstChunkId,
            occupancy,
            directory,
            chunkFill,
            chunkClass,
            occupancyMap,
            occupancyFirst,
            occupancyGoverned,
            occupancyCols,
            headerBytes,
            directoryBytes,
            paddingBytes);
    }

    /// <summary>Builds one segment's directory — <c>GET /dbmap/segment/{id}</c>. Returns <c>null</c> for an unknown id.</summary>
    public StorageSegmentDetailDto GetSegmentDetail(DatabaseEngine engine, int segmentId)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var segments = engine.EnumerateStorageSegments();
        if (segmentId < 0 || segmentId >= segments.Count)
        {
            return null;
        }

        var seg = segments[segmentId];
        var pages = seg.Pages.ToArray();
        var capacity = seg.IsChunkBased
            ? seg.ChunkCountRootPage + Math.Max(0, pages.Length - 1) * seg.ChunkCountPerPage
            : 0;

        return new StorageSegmentDetailDto(
            segmentId, seg.RootPageIndex, seg.Kind.ToString(), pages.Length,
            seg.Stride, seg.ChunkCountPerPage, capacity, pages);
    }

    /// <summary>
    /// Builds one segment's harvest summary — <c>GET /dbmap/segment/{id}/summary</c> (Module 15, A6). Returns <c>null</c> for an unknown id. Universal chunk
    /// counts come straight off the descriptor; cluster and entity-map segments add kind-specific stats. The entity-map stats walk every bucket + overflow chain,
    /// so this endpoint is fetched lazily by the client (only when the card opens), never on the tile path.
    /// </summary>
    public StorageSegmentSummaryDto GetSegmentSummary(DatabaseEngine engine, int segmentId)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var segments = engine.EnumerateStorageSegments();
        if (segmentId < 0 || segmentId >= segments.Count)
        {
            return null;
        }

        var seg = segments[segmentId];
        var pageCount = seg.Pages.Length;

        long entityCount = 0;
        int activeClusterCount = 0;
        int clusterSize = 0;
        var clusterSpatial = false;
        float clusterCellSize = 0;
        var clusterGridWidth = 0;
        var clusterGridHeight = 0;
        var clusterSpatialMode = "";
        if (seg.Kind == StorageSegmentKind.Cluster)
        {
            engine.TryGetClusterStats(seg.RootPageIndex, out entityCount, out activeClusterCount, out clusterSize);
            // Spatial context — explains low slot occupancy (per-cell bucketing) vs flags it as fragmentation (non-spatial). Cheap, in-memory only.
            engine.TryGetClusterSpatialInfo(seg.RootPageIndex, out clusterSpatial, out clusterCellSize, out clusterGridWidth, out clusterGridHeight,
                out clusterSpatialMode);
        }

        EntityMapStatsDto entityMap = null;
        if (seg.Kind == StorageSegmentKind.EntityMap && engine.TryGetEntityMapStats(seg.RootPageIndex, out var stats))
        {
            entityMap = new EntityMapStatsDto(stats.BucketCount, stats.EntryCount, stats.OverflowBucketCount, stats.MaxChainLength, stats.LoadFactor,
                stats.FillEmpty, stats.FillQuarter, stats.FillHalf, stats.FillThreeQuarter, stats.FillFull);
        }

        return new StorageSegmentSummaryDto(
            segmentId, seg.RootPageIndex, seg.Kind.ToString(), pageCount, seg.Stride,
            seg.AllocatedChunkCount, seg.FreeChunkCount, seg.ChunkCapacity,
            entityCount, activeClusterCount, clusterSize, entityMap,
            clusterSpatial, clusterCellSize, clusterGridWidth, clusterGridHeight, clusterSpatialMode);
    }

    /// <summary>Decodes one chunk's L4 content — <c>GET /dbmap/chunk/{segId}/{chunkId}</c>. Returns <c>null</c> for an unknown id.</summary>
    public StorageChunkDto GetChunk(DatabaseEngine engine, int segmentId, int chunkId)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var segments = engine.EnumerateStorageSegments();
        if (segmentId < 0 || segmentId >= segments.Count)
        {
            return null;
        }

        var seg = segments[segmentId];
        if (!seg.IsChunkBased || chunkId < 0)
        {
            return new StorageChunkDto(segmentId, chunkId, L4Decoder.UnknownDecoder, false, 0, 0, "", []);
        }

        // Resolve the chunk's page and in-page slot — chunk 0..rootCount live on the root page, the rest tile
        // across the non-root pages. This mirrors ChunkBasedSegment.GetChunkLocation in plain arithmetic.
        int segmentPageIndex;
        int chunkInPage;
        if (chunkId < seg.ChunkCountRootPage)
        {
            segmentPageIndex = 0;
            chunkInPage = chunkId;
        }
        else
        {
            var adjusted = chunkId - seg.ChunkCountRootPage;
            segmentPageIndex = adjusted / seg.ChunkCountPerPage + 1;
            chunkInPage = adjusted % seg.ChunkCountPerPage;
        }

        var pages = seg.Pages.Span;
        if (segmentPageIndex >= pages.Length)
        {
            return null;
        }

        var filePage = pages[segmentPageIndex];
        var dataOffset = segmentPageIndex == 0 ? seg.RootDataOffset : seg.OtherDataOffset;
        var chunkOffset = dataOffset + chunkInPage * seg.Stride;

        var body = new byte[engine.MMF.StoragePageSize];
        if (!engine.TryReadPageBody(filePage, body) || chunkOffset + seg.Stride > body.Length)
        {
            return null;
        }

        var occupied = ChunkBit(body, chunkInPage);
        var chunkBytes = body.AsSpan(chunkOffset, seg.Stride);
        var fileOffset = (long)filePage * engine.MMF.StoragePageSize + chunkOffset;

        var decoder = L4Decoder.UnknownDecoder;
        var componentType = "";
        StorageContentCellDto[] cells = [];
        string[] clusterComponents = [];
        StorageClusterCellDto clusterCell = null;

        if (seg.Kind == StorageSegmentKind.Component)
        {
            var def = ResolveComponentDefinition(engine, seg.RootPageIndex);
            if (def != null)
            {
                decoder = "component";
                componentType = def.Name;
                cells = L4Decoder.DecodeComponent(def, chunkBytes);
            }
        }
        else if (seg.Kind == StorageSegmentKind.Cluster
            && engine.TryGetClusterLayout(seg.RootPageIndex, out var clusterN, out _, out var clusterComponentCount, out var clusterEntityIdsOffset))
        {
            decoder = "cluster";
            cells = L4Decoder.DecodeCluster(chunkBytes, clusterN, clusterComponentCount, clusterEntityIdsOffset);
            // Slot-ordered component names label the client's per-component overlay picker (bit c of each cell's enabledMask ↔ clusterComponents[c]).
            engine.TryGetClusterComponentNames(seg.RootPageIndex, out clusterComponents);
            // Spatial-cell context (spatial archetypes only) — the per-cluster "why mostly empty" answer: the cell this cluster buckets into + that cell's totals.
            if (engine.TryGetClusterChunkSpatialInfo(seg.RootPageIndex, chunkId, out var cellKey, out var cellX, out var cellY,
                    out var entitiesInCell, out var clustersInCell, out var aabbMinX, out var aabbMinY, out var aabbMaxX, out var aabbMaxY))
            {
                clusterCell = new StorageClusterCellDto(cellKey, cellX, cellY, entitiesInCell, clustersInCell, aabbMinX, aabbMinY, aabbMaxX, aabbMaxY);
            }
        }
        else if ((seg.Kind == StorageSegmentKind.Vsbs || seg.Kind == StorageSegmentKind.ComponentCollection)
            && engine.TryGetVsbsLayout(seg.RootPageIndex, out var vsbsElementSize, out _, out _) && vsbsElementSize > 0)
        {
            decoder = "vsbs";
            cells = L4Decoder.DecodeVsbs(chunkBytes, vsbsElementSize, seg.Stride);
        }
        else if (seg.Kind == StorageSegmentKind.StringTable)
        {
            decoder = "string";
            cells = L4Decoder.DecodeString(chunkBytes, seg.Stride);
        }
        else if (seg.Kind == StorageSegmentKind.EntityMap
            && engine.TryGetHashMapLayout(seg.RootPageIndex, out _, out _, out var hmBucketCapacity, out var hmNonData))
        {
            decoder = "hash-bucket";
            // The meta chunk is always chunk 0; directory / overflow-dir-index chunks come from the engine's non-data set. Everything else is a bucket / overflow.
            var isMeta = chunkId == 0;
            var isDirectory = !isMeta && Array.IndexOf(hmNonData, chunkId) >= 0;
            cells = L4Decoder.DecodeHashMap(chunkBytes, isMeta, isDirectory, hmBucketCapacity);
        }
        else if (seg.Kind == StorageSegmentKind.Index
            && engine.TryGetIndexLayout(seg.RootPageIndex, out var idxDirectoryChunks, out var idxTrees))
        {
            decoder = "index";
            // Chunks 0..n are the shared B-tree directory; everything else is a node decoded leaf / internal from its own header.
            cells = L4Decoder.DecodeIndex(chunkBytes, chunkId, idxDirectoryChunks, idxTrees);
        }

        if (decoder == L4Decoder.UnknownDecoder)
        {
            // A chunk-based segment with no typed decoder (index / VSBS / string table) still characterizes via
            // the byte-class fallback — never blank (design §10).
            decoder = "generic";
            cells = L4Decoder.DecodeGeneric(chunkBytes);
        }

        return new StorageChunkDto(segmentId, chunkId, decoder, occupied, fileOffset, seg.Stride, componentType, cells, clusterComponents, clusterCell);
    }

    /// <summary>
    /// Decodes one cluster entity's full content — the L5 level beneath the L4 slot sub-grid (<c>GET /dbmap/chunk/{segId}/{chunkId}/entity/{slotIndex}</c>,
    /// file-map §10 Q4 override). Returns the entity at <paramref name="slotIndex"/> as component-grouped field cells (see <see cref="L4Decoder.DecodeClusterEntity"/>):
    /// a leading <c>entityPk</c> cell then, per component, a header + one field cell each. <see cref="StorageChunkDto.Occupied"/> reflects the slot's live state;
    /// a free slot yields no cells. Returns <c>null</c> for an unknown segment / non-cluster segment / out-of-range chunk.
    /// </summary>
    public StorageChunkDto GetClusterEntity(DatabaseEngine engine, int segmentId, int chunkId, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var segments = engine.EnumerateStorageSegments();
        if (segmentId < 0 || segmentId >= segments.Count)
        {
            return null;
        }

        var seg = segments[segmentId];
        if (!seg.IsChunkBased || seg.Kind != StorageSegmentKind.Cluster || chunkId < 0
            || !engine.TryGetClusterEntityLayout(seg.RootPageIndex, out var clusterSize, out var entityIdsOffset, out var components))
        {
            return null;
        }

        // Resolve the chunk's page and in-page slot — same arithmetic as GetChunk / ChunkBasedSegment.GetChunkLocation.
        int segmentPageIndex;
        int chunkInPage;
        if (chunkId < seg.ChunkCountRootPage)
        {
            segmentPageIndex = 0;
            chunkInPage = chunkId;
        }
        else
        {
            var adjusted = chunkId - seg.ChunkCountRootPage;
            segmentPageIndex = adjusted / seg.ChunkCountPerPage + 1;
            chunkInPage = adjusted % seg.ChunkCountPerPage;
        }

        var pages = seg.Pages.Span;
        if (segmentPageIndex >= pages.Length)
        {
            return null;
        }

        var filePage = pages[segmentPageIndex];
        var dataOffset = segmentPageIndex == 0 ? seg.RootDataOffset : seg.OtherDataOffset;
        var chunkOffset = dataOffset + chunkInPage * seg.Stride;

        var body = new byte[engine.MMF.StoragePageSize];
        if (!engine.TryReadPageBody(filePage, body) || chunkOffset + seg.Stride > body.Length)
        {
            return null;
        }

        var fileOffset = (long)filePage * engine.MMF.StoragePageSize + chunkOffset;

        // A free chunk holds no cluster — no entity at any slot.
        if (!ChunkBit(body, chunkInPage))
        {
            return new StorageChunkDto(segmentId, chunkId, "clusterEntity", false, fileOffset, seg.Stride, "", [], []);
        }

        var chunkBytes = body.AsSpan(chunkOffset, seg.Stride);
        var cells = L4Decoder.DecodeClusterEntity(chunkBytes, slotIndex, clusterSize, entityIdsOffset, components);
        var componentNames = new string[components.Length];
        for (var c = 0; c < components.Length; c++)
        {
            componentNames[c] = components[c].Name;
        }

        return new StorageChunkDto(segmentId, chunkId, "clusterEntity", cells.Length > 0, fileOffset, seg.Stride, "", cells, componentNames);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Classifies a page body's live CRC32C against its stored checksum (the stored field is skipped).</summary>
    private static StorageCrcStatus ClassifyCrc(ReadOnlySpan<byte> body, uint storedChecksum)
    {
        if (storedChecksum == 0)
        {
            return StorageCrcStatus.Unverified;
        }
        var live = Crc32CUtil.ComputeSkipping(body, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
        return live == storedChecksum ? StorageCrcStatus.Verified : StorageCrcStatus.Failed;
    }

    /// <summary>
    /// The chunk-based segment owning the descriptor <paramref name="cell"/>, plus whether <paramref name="realPage"/>
    /// (the sampled representative page) is that segment's root. On an exact map the cell index is the page index.
    /// </summary>
    private static (StorageSegmentInfo Info, bool IsRoot)? OwningChunkSegment(StructuralMap map, int cell, int realPage)
    {
        var ownerId = map.OwnerSegmentId[cell];
        if (ownerId == StructuralMap.NoSegment)
        {
            return null;
        }
        var seg = map.Segments[ownerId];
        return seg.IsChunkBased ? (seg, realPage == seg.RootPageIndex) : null;
    }

    /// <summary>Counts allocated chunks in a page's metadata occupancy bitmap, limited to its real chunk count.</summary>
    private static int CountOccupiedChunks(ReadOnlySpan<byte> body, int chunkTotal)
    {
        if (chunkTotal <= 0)
        {
            return 0;
        }
        var bitmap = MemoryMarshal.Cast<byte, long>(body.Slice(PagedMMF.PageBaseHeaderSize, PagedMMF.PageMetadataSize));
        var used = 0;
        for (var b = 0; b < chunkTotal; b++)
        {
            if ((bitmap[b >> 6] & (1L << (b & 63))) != 0)
            {
                used++;
            }
        }
        return used;
    }

    /// <summary>One byte per chunk (1 = allocated) from the metadata occupancy bitmap — drives the L3 chunk grid.</summary>
    private static byte[] OccupancyBytes(ReadOnlySpan<byte> body, int chunkTotal)
    {
        if (chunkTotal <= 0)
        {
            return [];
        }
        var bitmap = MemoryMarshal.Cast<byte, long>(body.Slice(PagedMMF.PageBaseHeaderSize, PagedMMF.PageMetadataSize));
        var bytes = new byte[chunkTotal];
        for (var b = 0; b < chunkTotal; b++)
        {
            bytes[b] = (byte)((bitmap[b >> 6] >> (b & 63)) & 1);
        }
        return bytes;
    }

    /// <summary>
    /// Per-chunk intra-chunk fill (0..255) + class arrays for a Cluster page (Module 15, A6, design §10.1). Each
    /// occupied chunk is a cluster of <paramref name="clusterSize"/> entity slots; OccupancyBits @chunk+0 popcounted
    /// over N is the live-entity ratio. Free chunks stay 0 / <see cref="StorageChunkClass.Slot"/>. Both arrays are
    /// base64 SoA, mirroring <see cref="OccupancyBytes"/>. No extra page I/O — the body is already in hand.
    /// </summary>
    private static (string Fill, string Class) BuildClusterChunkFill(ReadOnlySpan<byte> body, int chunkTotal, int dataOffset, int stride, int clusterSize)
    {
        var fill = new byte[chunkTotal];
        var cls = new byte[chunkTotal];
        var fullMask = clusterSize >= 64 ? ulong.MaxValue : (1UL << clusterSize) - 1;

        for (var i = 0; i < chunkTotal; i++)
        {
            if (!ChunkBit(body, i))
            {
                continue; // free chunk → fill 0, class Slot (0)
            }
            var off = dataOffset + i * stride;
            if (off + 8 > body.Length)
            {
                continue;
            }
            var live = System.Numerics.BitOperations.PopCount(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(off)) & fullMask);
            fill[i] = (byte)Math.Clamp(live * 255 / clusterSize, 0, 255);
            cls[i] = (byte)StorageChunkClass.ContainerFill;
        }

        return (Convert.ToBase64String(fill), Convert.ToBase64String(cls));
    }

    /// <summary>
    /// Per-chunk element fill (0..255) + class arrays for a VSBS / component-collection page (Module 15, A6, design §10.1).
    /// Each occupied chunk packs <c>ElementCount</c> (header @chunk+4) elements of <paramref name="elementSize"/> bytes; the
    /// fill is that over the chunk capacity <c>(stride − 8) / elementSize</c>. The root chunk's larger header makes it hold a
    /// few fewer (its fill is thus slightly over-reported) — which root is can't be told from the chunk, so the per-chunk
    /// capacity is used uniformly. Free chunks stay 0. base64 SoA, no extra I/O.
    /// </summary>
    private static (string Fill, string Class) BuildVsbsChunkFill(ReadOnlySpan<byte> body, int chunkTotal, int dataOffset, int stride, int elementSize)
    {
        var fill = new byte[chunkTotal];
        var cls = new byte[chunkTotal];
        var capacity = elementSize > 0 ? (stride - 8) / elementSize : 0; // 8 = VariableSizedBufferChunkHeader { NextChunkId; ElementCount }
        if (capacity <= 0)
        {
            return (Convert.ToBase64String(fill), Convert.ToBase64String(cls));
        }

        for (var i = 0; i < chunkTotal; i++)
        {
            if (!ChunkBit(body, i))
            {
                continue;
            }
            var off = dataOffset + i * stride;
            if (off + 8 > body.Length)
            {
                continue;
            }
            var elementCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body.Slice(off + 4));
            fill[i] = (byte)Math.Clamp(elementCount * 255 / capacity, 0, 255);
            cls[i] = (byte)StorageChunkClass.ContainerFill;
        }

        return (Convert.ToBase64String(fill), Convert.ToBase64String(cls));
    }

    /// <summary>
    /// Per-chunk payload fill (0..255) + class arrays for a string-table page (Module 15, A6, design §10.1). Each occupied
    /// chunk holds <c>min(SizeLeft, blockSize)</c> UTF-8 bytes (<c>SizeLeft</c> @chunk+0 is the bytes remaining from this
    /// chunk onward, so it is valid on every chunk of a chain — full chunks read 100%, the chain's tail reads partial);
    /// <c>blockSize = stride − 8</c>. Free chunks stay 0. base64 SoA, no extra I/O.
    /// </summary>
    private static (string Fill, string Class) BuildStringChunkFill(ReadOnlySpan<byte> body, int chunkTotal, int dataOffset, int stride)
    {
        var fill = new byte[chunkTotal];
        var cls = new byte[chunkTotal];
        var blockSize = stride - 8; // 8 = string ChunkHeader { SizeLeft; NextChunkId }
        if (blockSize <= 0)
        {
            return (Convert.ToBase64String(fill), Convert.ToBase64String(cls));
        }

        for (var i = 0; i < chunkTotal; i++)
        {
            if (!ChunkBit(body, i))
            {
                continue;
            }
            var off = dataOffset + i * stride;
            if (off + 8 > body.Length)
            {
                continue;
            }
            var sizeLeft = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body.Slice(off));
            var used = Math.Clamp(sizeLeft, 0, blockSize);
            fill[i] = (byte)(used * 255 / blockSize);
            cls[i] = (byte)StorageChunkClass.ContainerFill;
        }

        return (Convert.ToBase64String(fill), Convert.ToBase64String(cls));
    }

    /// <summary>
    /// Per-chunk fill (0..255) + class arrays for a linear-hash (entity-map) page (Module 15, A6, design §10.1). Every <i>data</i> chunk is a bucket or an
    /// overflow chunk whose <c>EntryCount</c> (header @chunk+4) over <paramref name="bucketCapacity"/> is its fill; an overflow chunk (<c>OlcVersion == 0</c>
    /// @chunk+0) or a primary bucket that chains (<c>OverflowChunkId != −1</c> @chunk+8) is flagged <see cref="StorageChunkClass.Overflow"/>, a lone primary
    /// <see cref="StorageChunkClass.ContainerFill"/>. The structural chunks — the meta chunk and every directory / overflow-dir-index chunk, supplied as global
    /// ids in <paramref name="nonDataChunkIds"/> — are headerless so their bytes can't be read as a bucket; they are marked <see cref="StorageChunkClass.NonData"/>
    /// (no fill). Free chunks stay 0. base64 SoA, no extra I/O.
    /// </summary>
    private static (string Fill, string Class) BuildHashMapChunkFill(ReadOnlySpan<byte> body, int chunkTotal, int dataOffset, int stride, int firstChunkId,
        int bucketCapacity, int[] nonDataChunkIds)
    {
        var fill = new byte[chunkTotal];
        var cls = new byte[chunkTotal];
        var nonData = nonDataChunkIds is { Length: > 0 } ? new HashSet<int>(nonDataChunkIds) : null;

        for (var i = 0; i < chunkTotal; i++)
        {
            if (!ChunkBit(body, i))
            {
                continue; // free chunk → fill 0, class Slot (0)
            }
            if (nonData != null && nonData.Contains(firstChunkId + i))
            {
                cls[i] = (byte)StorageChunkClass.NonData; // meta / directory — structural, no fill
                continue;
            }
            var off = dataOffset + i * stride;
            if (off + 12 > body.Length)
            {
                continue;
            }
            var olcVersion = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body.Slice(off));
            var entryCount = body[off + 4];
            var overflowChunkId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body.Slice(off + 8));
            fill[i] = bucketCapacity > 0 ? (byte)Math.Clamp(entryCount * 255 / bucketCapacity, 0, 255) : (byte)0;
            cls[i] = olcVersion == 0 || overflowChunkId != -1 ? (byte)StorageChunkClass.Overflow : (byte)StorageChunkClass.ContainerFill;
        }

        return (Convert.ToBase64String(fill), Convert.ToBase64String(cls));
    }

    /// <summary>
    /// Per-chunk class array for a B-tree index page (Module 15, A6, design §13 A6). Each occupied node is classed leaf vs internal from its control word
    /// (<c>NodeStates.IsLeaf</c> = bit 1 @chunk+0) — self-describing regardless of key width; the shared directory chunks (global ids <c>[0, directoryChunkCount)</c>)
    /// are <see cref="StorageChunkClass.NonData"/>. No fill array is emitted (per-node fill needs a tree walk and carries little signal for a self-balancing B+tree).
    /// base64 SoA for the class; empty fill. No extra page I/O.
    /// </summary>
    private static (string Fill, string Class) BuildIndexChunkClass(ReadOnlySpan<byte> body, int chunkTotal, int dataOffset, int stride, int firstChunkId,
        int directoryChunkCount)
    {
        var cls = new byte[chunkTotal];
        for (var i = 0; i < chunkTotal; i++)
        {
            if (!ChunkBit(body, i))
            {
                continue; // free chunk → class Slot (0)
            }
            if (firstChunkId + i < directoryChunkCount)
            {
                cls[i] = (byte)StorageChunkClass.NonData; // shared B-tree directory chunk
                continue;
            }
            var off = dataOffset + i * stride;
            if (off + 4 > body.Length)
            {
                continue;
            }
            var control = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(body.Slice(off));
            cls[i] = (byte)((control & 0x02) != 0 ? StorageChunkClass.Leaf : StorageChunkClass.Internal); // NodeStates.IsLeaf = bit 1
        }
        return ("", Convert.ToBase64String(cls));
    }

    /// <summary>
    /// Occupancy region-map for an occupancy page (Module 15, A6, design §10.2). The page governs a contiguous file-page
    /// range (the root governs fewer — see <see cref="DatabaseEngine.GetOccupancyPageGovernedRange"/>); the bits come from
    /// the resident occupancy segment via <c>ReadOccupancyBits</c> (zero data-page I/O). The range is down-sampled into a
    /// near-square grid of ≤ 256 cells, each the allocated fraction (0..255) of its sub-range — a map-within-the-map.
    /// </summary>
    private static (string Map, long First, int Governed, int Cols) BuildOccupancyRegionMap(DatabaseEngine engine, StorageSegmentDescriptor descriptor,
        int pageIndex)
    {
        var pages = descriptor.Pages.Span;
        var ordinal = -1;
        for (var k = 0; k < pages.Length; k++)
        {
            if (pages[k] == pageIndex)
            {
                ordinal = k;
                break;
            }
        }
        if (ordinal < 0)
        {
            return ("", 0L, 0, 0);
        }

        var (first, governed) = engine.GetOccupancyPageGovernedRange(ordinal);
        var filePages = engine.MMF.StorageFilePageCount;
        var effective = (int)Math.Min(governed, Math.Max(0L, filePages - first));
        if (effective <= 0)
        {
            return ("", first, 0, 0);
        }

        var capacity = engine.MMF.OccupancyCapacityPages;
        var words = new long[(Math.Max(capacity, filePages) + 63) / 64];
        engine.MMF.ReadOccupancyBits(words);

        const int cap = 256;
        var cells = Math.Min(effective, cap);
        var map = new byte[cells];
        for (var c = 0; c < cells; c++)
        {
            var lo = first + (long)c * effective / cells;
            var hi = first + (long)(c + 1) * effective / cells;
            if (hi <= lo)
            {
                hi = lo + 1;
            }
            var alloc = 0;
            for (var p = lo; p < hi; p++)
            {
                if ((words[(int)(p >> 6)] & (1L << (int)(p & 63))) != 0)
                {
                    alloc++;
                }
            }
            map[c] = (byte)Math.Clamp(alloc * 255 / (int)(hi - lo), 0, 255);
        }

        return (Convert.ToBase64String(map), first, effective, (int)Math.Ceiling(Math.Sqrt(cells)));
    }

    private static bool ChunkBit(ReadOnlySpan<byte> body, int chunkInPage)
    {
        var bitmap = MemoryMarshal.Cast<byte, long>(body.Slice(PagedMMF.PageBaseHeaderSize, PagedMMF.PageMetadataSize));
        var word = chunkInPage >> 6;
        return word < bitmap.Length && (bitmap[word] & (1L << (chunkInPage & 63))) != 0;
    }

    private static DBComponentDefinition ResolveComponentDefinition(DatabaseEngine engine, int componentSegmentRootPage)
    {
        foreach (var table in engine.GetAllComponentTables())
        {
            var segment = table.ComponentSegment;
            if (segment != null && segment.RootPageIndex == componentSegmentRootPage)
            {
                return table.Definition;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves a friendly owner name for an attributable storage segment — not just the <see cref="StorageSegmentKind.Component"/> data segment. The
    /// component-owned data/history/index kinds (Component / Revision / Index) all hang off one <c>ComponentTable</c>, so a single root-page scan over the table's
    /// segment family names them with the owning component's registered name. Archetype-owned kinds (<see cref="StorageSegmentKind.Cluster"/> /
    /// <see cref="StorageSegmentKind.EntityMap"/>) are named via the engine's archetype-owner lookup. The returned name is the full registered/CLR name — the
    /// Workbench client shortens it through the same labeller the Query Console uses, so a segment shows the same short name as its type does everywhere else.
    /// Returns <c>""</c> for owner-less engine singletons (Occupancy / Root / System / Free) and for the indirectly-owned auxiliary kinds (VSBS / Spatial /
    /// ComponentCollection), which keep their bare <c>kind #id</c> label — their owner chain isn't a flat <c>ComponentTable</c> field, and they're rarely the
    /// segment a user is hunting for.
    /// </summary>
    private static string ResolveSegmentOwnerName(DatabaseEngine engine, int rootPage, StorageSegmentKind kind)
    {
        switch (kind)
        {
            case StorageSegmentKind.Cluster:
            case StorageSegmentKind.EntityMap:
                return engine.TryGetSegmentOwnerArchetypeName(rootPage, kind, out var archetypeName) ? archetypeName : "";

            case StorageSegmentKind.Component:
            case StorageSegmentKind.Revision:
                foreach (var table in engine.GetAllComponentTables())
                {
                    if (OwnsSegment(table, rootPage))
                    {
                        return table.Definition.Name;
                    }
                }
                return "";

            case StorageSegmentKind.Index:
                // An Index segment is either a per-component-table index (Default / String64 / Tail — component-owned) or the
                // per-archetype cluster index (ClusterState.IndexSegment — archetype-owned, not a flat ComponentTable field).
                // The two owners are disjoint: try the component-table path first, then fall back to the archetype resolver.
                foreach (var table in engine.GetAllComponentTables())
                {
                    if (OwnsSegment(table, rootPage))
                    {
                        return table.Definition.Name;
                    }
                }
                return engine.TryGetSegmentOwnerArchetypeName(rootPage, StorageSegmentKind.Index, out var idxArchetypeName) ? idxArchetypeName : "";

            default:
                // Occupancy / Root / System / Free / VSBS / Spatial / ComponentCollection — no flat owner path; keep the bare kind label.
                return "";
        }
    }

    /// <summary>True when <paramref name="rootPage"/> is the root of one of <paramref name="table"/>'s component-data / MVCC-revision / index segments
    /// (default / String64 / tail). Covers the kinds the File Map labels per-component; the auxiliary VSBS / spatial chains are intentionally excluded
    /// (see <see cref="ResolveSegmentOwnerName"/>).</summary>
    private static bool OwnsSegment(ComponentTable table, int rootPage)
    {
        if (table.ComponentSegment?.RootPageIndex == rootPage) return true;
        if (table.CompRevTableSegment?.RootPageIndex == rootPage) return true;
        if (table.DefaultIndexSegment?.RootPageIndex == rootPage) return true;
        if (table.String64IndexSegment?.RootPageIndex == rootPage) return true;
        if (table.TailIndexSegment?.RootPageIndex == rootPage) return true;
        return false;
    }
}
