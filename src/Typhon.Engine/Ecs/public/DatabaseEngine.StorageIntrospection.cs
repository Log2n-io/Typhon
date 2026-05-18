using System;
using System.Collections.Generic;

namespace Typhon.Engine;

// Read-only storage-introspection surface consumed by the Workbench Database File Map (Module 15, Track A).
// Every method here derives its result from in-memory engine structures — the component-table registry, the// segment page lists, and the occupancy bitmap —
// with no data-page I/O.
public partial class DatabaseEngine
{
    /// <summary>
    /// Enumerates every live logical segment's on-disk footprint — the per-<c>ComponentTable</c> segments plus the occupancy-bitmap segment.
    /// Authoritative: walks the component-table registry rather than the page cache's lazy segment cache. Read-only; consumes only in-memory structures.
    /// </summary>
    public IReadOnlyList<StorageSegmentDescriptor> EnumerateStorageSegments()
    {
        var result = new List<StorageSegmentDescriptor>();

        foreach (var table in GetAllComponentTables())
        {
            AddSegment(result, table.ComponentSegment, StorageSegmentKind.Component);
            AddSegment(result, table.CompRevTableSegment, StorageSegmentKind.Revision);
            AddSegment(result, table.DefaultIndexSegment, StorageSegmentKind.Index);
            AddSegment(result, table.String64IndexSegment, StorageSegmentKind.Index);
            AddSegment(result, table.TailIndexSegment, StorageSegmentKind.Index);
            AddSegment(result, table.TailVSBS?.Segment, StorageSegmentKind.Vsbs);
        }

        AddSegment(result, MMF.OccupancySegment, StorageSegmentKind.Occupancy);
        return result;
    }

    /// <summary>
    /// Classifies every file page by semantic type into <paramref name="dest"/> (length ≥ file page count).
    /// Built entirely from in-memory structures — the occupancy bitmap and the segment registry — with no data-page I/O. A page owned by no enumerated segment
    /// and not a reserved root page resolves to
    /// <see cref="StoragePageType.Unknown"/>.
    /// </summary>
    public void ClassifyAllPages(Span<StoragePageType> dest)
    {
        var pageCount = MMF.StorageFilePageCount;
        if (dest.Length < pageCount)
        {
            throw new ArgumentException($"Destination span too small: need {pageCount} entries, got {dest.Length}.", nameof(dest));
        }
        var pages = dest[..pageCount];
        pages.Clear();

        // Free pages — occupancy bit clear. The occupancy capacity always covers the file page range.
        var capacity = MMF.OccupancyCapacityPages;
        var words = new long[(Math.Max(capacity, pageCount) + 63) / 64];
        MMF.ReadOccupancyBits(words);
        for (var p = 0; p < pageCount; p++)
        {
            if ((words[p >> 6] & (1L << (p & 0x3F))) == 0)
            {
                pages[p] = StoragePageType.Free;
            }
        }

        // Reserved root / header pages (page index < 4) — unless free.
        var rootEnd = Math.Min(ManagedPagedMMF.InitialReservedPageCount, pageCount);
        for (var p = 0; p < rootEnd; p++)
        {
            if (pages[p] != StoragePageType.Free)
            {
                pages[p] = StoragePageType.Root;
            }
        }

        // Segment pages override — the occupancy-segment root (page 1) correctly resolves to Occupancy.
        foreach (var seg in EnumerateStorageSegments())
        {
            var type = ToPageType(seg.Kind);
            foreach (var page in seg.Pages.Span)
            {
                if ((uint)page < (uint)pageCount)
                {
                    pages[page] = type;
                }
            }
        }
    }

    /// <summary>Total byte size of the write-ahead log across all segment files (0 when no WAL is active).</summary>
    public long GetWalTotalBytes() => WalManager?.SegmentManager?.TotalWalBytes ?? 0L;

    private static void AddSegment(List<StorageSegmentDescriptor> sink, LogicalSegment<PersistentStore> segment, StorageSegmentKind kind)
    {
        if (segment == null || segment.Length == 0)
        {
            return;
        }

        // Chunk-based segments also carry the layout constants (stride, per-page chunk counts, chunk-0 byte
        // offsets) that the Database File Map's L3/L4 decoders need to slice chunks out of a page body.
        if (segment is ChunkBasedSegment<PersistentStore> chunked)
        {
            sink.Add(new StorageSegmentDescriptor(segment.RootPageIndex, kind, segment.Pages.ToArray(), chunked.Stride, chunked.ChunkCountRootPage, 
                chunked.ChunkCountPerPage, chunked.RootDataOffset, chunked.OtherDataOffset));
        }
        else
        {
            sink.Add(new StorageSegmentDescriptor(segment.RootPageIndex, kind, segment.Pages.ToArray()));
        }
    }

    private static StoragePageType ToPageType(StorageSegmentKind kind) => kind switch
    {
        StorageSegmentKind.Component => StoragePageType.Component,
        StorageSegmentKind.Revision => StoragePageType.Revision,
        StorageSegmentKind.Index => StoragePageType.Index,
        StorageSegmentKind.Cluster => StoragePageType.Cluster,
        StorageSegmentKind.Vsbs => StoragePageType.Vsbs,
        StorageSegmentKind.StringTable => StoragePageType.StringTable,
        StorageSegmentKind.Occupancy => StoragePageType.Occupancy,
        _ => StoragePageType.Unknown,
    };
}
