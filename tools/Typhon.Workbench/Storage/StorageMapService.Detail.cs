using System;
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
        var liveCrc = read ? WalCrc.ComputeSkipping(body, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize) : 0u;

        var ownerId = map.OwnerSegmentId[cell];

        // Resolve the owning segment once — it yields the kind, the directory (when this page is the segment
        // root), the chunk layout, and the global id of this page's first chunk (FirstChunkId + i = an L4 ref).
        var ownerKind = "";
        var chunkUsed = 0;
        var chunkTotal = 0;
        var firstChunkId = 0;
        var occupancy = "";
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
                var pages = descriptor.Pages.Span;
                for (var k = 0; k < pages.Length; k++)
                {
                    if (pages[k] == pageIndex)
                    {
                        var isRoot = k == 0;
                        chunkTotal = isRoot ? descriptor.ChunkCountRootPage : descriptor.ChunkCountPerPage;
                        firstChunkId = isRoot ? 0 : descriptor.ChunkCountRootPage + (k - 1) * descriptor.ChunkCountPerPage;
                        break;
                    }
                }

                if (read && chunkTotal > 0)
                {
                    chunkUsed = CountOccupiedChunks(body, chunkTotal);
                    occupancy = Convert.ToBase64String(OccupancyBytes(body, chunkTotal));
                }
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
            directory);
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

        if (decoder == L4Decoder.UnknownDecoder)
        {
            // A chunk-based segment with no typed decoder (index / VSBS / string table) still characterizes via
            // the byte-class fallback — never blank (design §10).
            decoder = "generic";
            cells = L4Decoder.DecodeGeneric(chunkBytes);
        }

        return new StorageChunkDto(segmentId, chunkId, decoder, occupied, fileOffset, seg.Stride, componentType, cells);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Classifies a page body's live CRC32C against its stored checksum (the stored field is skipped).</summary>
    private static StorageCrcStatus ClassifyCrc(ReadOnlySpan<byte> body, uint storedChecksum)
    {
        if (storedChecksum == 0)
        {
            return StorageCrcStatus.Unverified;
        }
        var live = WalCrc.ComputeSkipping(body, PageBaseHeader.PageChecksumOffset, PageBaseHeader.PageChecksumSize);
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
}
