using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

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
        // Walk the authoritative segment registry (ManagedPagedMMF._segments) rather than hand-listing per-table fields. The registry contains every
        // persistent segment — component / revision / index / VSBS plus spatial indexes, entity maps, cluster storage, cluster indexes, component
        // collections and the UoW registry — so no allocated page is left unattributed (which previously rendered as Unknown). Each segment self-reports
        // its kind from its persisted root header (LogicalSegment.Kind).
        var result = new List<StorageSegmentDescriptor>();
        foreach (var seg in MMF.RegisteredSegments)
        {
            AddSegment(result, seg);
        }
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
        // The occupancy bitmap is authoritative for Free; a page whose bit is 0 must stay Free even if it still appears in a segment's Pages list (a stale
        // reference here would otherwise relabel a free page as Component/Index/etc., which the Map then renders against a garbage page body).
        foreach (var seg in EnumerateStorageSegments())
        {
            var type = ToPageType(seg.Kind);
            foreach (var page in seg.Pages.Span)
            {
                if ((uint)page < (uint)pageCount && pages[page] != StoragePageType.Free)
                {
                    pages[page] = type;
                }
            }
        }
    }

    /// <summary>Total byte size of the write-ahead log across all segment files (0 when no WAL is active).</summary>
    public long GetWalTotalBytes() => WalManager?.SegmentManager?.TotalWalBytes ?? 0L;

    /// <summary>
    /// The schema-assembly manifest persisted in this database: the identity of every .NET assembly that declares a stored component or archetype. Read from the
    /// <see cref="AssemblyR1"/> catalog, which is loaded on every open (including schemaless), so this is available without any user schema DLL. The core engine
    /// assembly is intentionally excluded — it is always loaded. Consumed by tooling (the Workbench) to locate and load the schema assemblies a file depends on.
    /// </summary>
    public IReadOnlyList<AssemblyName> GetRequiredAssemblies()
    {
        var result = new List<AssemblyName>();
        var persisted = _persistedAssemblies;
        if (persisted == null)
        {
            return result;
        }
        foreach (var kvp in persisted)
        {
            var a = kvp.Value.Asm;
            var an = new AssemblyName(a.SimpleName.AsString)
            {
                Version = new Version(a.VerMajor, a.VerMinor, a.VerBuild, a.VerRevision),
            };
            var token = ULongToToken(a.PublicKeyToken);
            if (token.Length == 8)
            {
                an.SetPublicKeyToken(token);
            }
            result.Add(an);
        }
        return result;
    }

    /// <summary>
    /// Resolves the cluster memory layout for the cluster segment whose root page is <paramref name="clusterSegmentRootPage"/>. Used by the Database File Map
    /// to decode cluster chunks: per-cluster <c>OccupancyBits</c> (u64) live at chunk offset 0, the per-component <c>EnabledBits</c> words at
    /// <c>8 + componentSlot * 8</c>, and the packed entity-id array at <paramref name="entityIdsOffset"/>. Returns <see langword="false"/> when no live cluster
    /// archetype owns that segment (e.g. a non-cluster segment, or a pure-Transient archetype). Read-only; walks only in-memory archetype state.
    /// </summary>
    internal bool TryGetClusterLayout(int clusterSegmentRootPage, out int clusterSize, out int headerSize, out int componentCount, out int entityIdsOffset)
    {
        clusterSize = 0;
        headerSize = 0;
        componentCount = 0;
        entityIdsOffset = 0;

        var states = _archetypeStates;
        if (states == null)
        {
            return false;
        }

        foreach (var state in states)
        {
            var cluster = state?.ClusterState;
            if (cluster?.ClusterSegment == null || cluster.ClusterSegment.RootPageIndex != clusterSegmentRootPage)
            {
                continue;
            }

            var layout = cluster.Layout;
            clusterSize = layout.ClusterSize;
            headerSize = layout.HeaderSize;
            componentCount = layout.ComponentCount;
            entityIdsOffset = layout.EntityIdsOffset;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Live entity-fill counts for the cluster segment whose root page is <paramref name="clusterSegmentRootPage"/>. Used by the Database File Map
    /// harvest summary to show the intra-cluster fragmentation signal: <paramref name="entityCount"/> live entities packed into
    /// <paramref name="activeClusterCount"/> active clusters of <paramref name="clusterSize"/> slots each — slot occupancy is
    /// <c>entityCount / (activeClusterCount * clusterSize)</c>. Returns <see langword="false"/> when no live cluster archetype owns that segment. Read-only;
    /// walks only in-memory archetype state (O(archetypes), no page I/O).
    /// </summary>
    internal bool TryGetClusterStats(int clusterSegmentRootPage, out long entityCount, out int activeClusterCount, out int clusterSize)
    {
        entityCount = 0;
        activeClusterCount = 0;
        clusterSize = 0;

        var states = _archetypeStates;
        if (states == null)
        {
            return false;
        }

        for (var archetypeId = 0; archetypeId < states.Length; archetypeId++)
        {
            var state = states[archetypeId];
            var cluster = state?.ClusterState;
            if (cluster?.ClusterSegment == null || cluster.ClusterSegment.RootPageIndex != clusterSegmentRootPage)
            {
                continue;
            }

            entityCount = GetArchetypeEntityCount((ushort)archetypeId);
            activeClusterCount = cluster.ActiveClusterCount;
            clusterSize = cluster.Layout.ClusterSize;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Slot-ordered component names for the cluster segment whose root page is <paramref name="clusterSegmentRootPage"/>. Slot <c>c</c> corresponds to bit
    /// <c>c</c> of the per-slot <c>enabledMask</c> the cluster L4 decoder emits, so the Database File Map can label its per-component overlay picker without a
    /// second decode. Returns <see langword="false"/> when no live cluster archetype owns that segment. Read-only; walks only in-memory archetype state
    /// (O(archetypes), no page I/O).
    /// </summary>
    internal bool TryGetClusterComponentNames(int clusterSegmentRootPage, out string[] componentNames)
    {
        componentNames = [];

        var states = _archetypeStates;
        if (states == null)
        {
            return false;
        }

        foreach (var state in states)
        {
            var cluster = state?.ClusterState;
            if (cluster?.ClusterSegment == null || cluster.ClusterSegment.RootPageIndex != clusterSegmentRootPage)
            {
                continue;
            }

            // Resolve names from THIS engine's slot→ComponentTable map (slot order = the cluster's EnabledBits / the
            // decoder's enabledMask bit order). Deliberately NOT via the global static ArchetypeRegistry: it is shared
            // across engines, so a colliding archetype id can serve the wrong metadata there.
            var tables = state.SlotToComponentTable;
            componentNames = new string[tables.Length];
            for (var i = 0; i < tables.Length; i++)
            {
                componentNames[i] = tables[i]?.Definition?.Name ?? "";
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Diagnostic statistics for the entity-map (entity-id → cluster-slot linear hash) whose backing segment's root page is
    /// <paramref name="entityMapSegmentRootPage"/>. Used by the Database File Map harvest summary. Unlike the other introspection accessors this one is
    /// <b>not</b> O(1): it walks every bucket and overflow chain under an epoch guard, so it must be fetched lazily (on the per-segment summary card only),
    /// never on the coarse / detail tile path. Returns <see langword="false"/> when no live archetype owns that entity-map segment. Best-effort under concurrent
    /// mutation — a count may be torn, but the epoch guard keeps freed chunks mapped so the walk never faults.
    /// </summary>
    internal bool TryGetEntityMapStats(int entityMapSegmentRootPage, out EntityMapStats stats)
    {
        stats = default;

        var states = _archetypeStates;
        if (states == null)
        {
            return false;
        }

        foreach (var state in states)
        {
            var map = state?.EntityMap;
            if (map?.Segment == null || map.Segment.RootPageIndex != entityMapSegmentRootPage)
            {
                continue;
            }

            using var guard = EpochGuard.Enter(EpochManager);
            var accessor = map.Segment.CreateChunkAccessor();
            var s = map.GetStats(ref accessor);
            stats = new EntityMapStats(s.BucketCount, s.EntryCount, s.OverflowBucketCount, s.MaxChainLength, s.LoadFactor,
                s.FillEmpty, s.FillQuarter, s.FillHalf, s.FillThreeQuarter, s.FillFull);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Resolves the linear-hash (entity-map) layout for the segment whose root page is <paramref name="entityMapSegmentRootPage"/>: the key / value widths, the
    /// per-bucket capacity (<c>(stride − 12) / (keyWidth + valueWidth)</c>), and the set of <b>non-data</b> chunk ids (the meta chunk plus every directory and
    /// overflow-dir-index chunk). Used by the Database File Map (Module 15, A6) to colour bucket / overflow chunks by their fill and to hatch the structural
    /// (meta / directory) chunks rather than mis-reading their headerless bytes as a bucket. Every <i>data</i> chunk (a bucket or its overflow) self-identifies
    /// from its own header — a primary bucket carries a non-zero <c>OlcVersion</c>, an overflow chunk carries <c>OlcVersion == 0</c> — so only the small
    /// meta / directory set needs a walk here (O(directory chunks), no bucket-chain traversal). Returns <see langword="false"/> when no live archetype owns that
    /// segment. Read-only; the chunk walk reads the resident page cache under an epoch guard (zero data-page I/O).
    /// </summary>
    internal bool TryGetHashMapLayout(int entityMapSegmentRootPage, out int keyWidth, out int valueWidth, out int bucketCapacity, out int[] nonDataChunkIds)
    {
        keyWidth = 0;
        valueWidth = 0;
        bucketCapacity = 0;
        nonDataChunkIds = [];

        var states = _archetypeStates;
        if (states == null)
        {
            return false;
        }

        foreach (var state in states)
        {
            var map = state?.EntityMap;
            if (map?.Segment == null || map.Segment.RootPageIndex != entityMapSegmentRootPage)
            {
                continue;
            }

            keyWidth = sizeof(long); // EntityKey is a long.
            valueWidth = map.ValueSize;
            bucketCapacity = map.BucketCapacity;

            using var guard = EpochGuard.Enter(EpochManager);
            var accessor = map.Segment.CreateChunkAccessor();
            try
            {
                nonDataChunkIds = CollectHashMapNonDataChunks(ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Collects the structural (non-data) chunk ids of a linear-hash segment: the meta chunk (0), every directory chunk (the first
    /// <see cref="PagedHashMapMeta.MaxInlineDirectoryChunks"/> inline in the meta, the rest reached through the overflow dir-index chain), and the overflow
    /// dir-index chunks themselves. Mirrors <c>PagedHashMapBase.GetDirectoryChunkId</c>'s addressing. Cost is O(directory chunks) — no bucket-chain walk.
    /// </summary>
    private static unsafe int[] CollectHashMapNonDataChunks(ref ChunkAccessor<PersistentStore> accessor)
    {
        ref readonly var meta = ref accessor.GetChunkReadOnly<PagedHashMapMeta>(0);
        var dirCount = meta.DirectoryChunkCount;
        var overflowHead = meta.OverflowDirIndexChunkId;

        var ids = new List<int>(1 + dirCount + 4) { 0 }; // chunk 0 is always the meta chunk.

        var inline = Math.Min((int)dirCount, PagedHashMapMeta.MaxInlineDirectoryChunks);
        for (var i = 0; i < inline; i++)
        {
            ids.Add(meta.DirectoryChunkIds[i]);
        }

        if (dirCount > PagedHashMapMeta.MaxInlineDirectoryChunks)
        {
            var remaining = dirCount - PagedHashMapMeta.MaxInlineDirectoryChunks;
            var ovId = overflowHead;
            while (ovId != -1 && remaining > 0)
            {
                ids.Add(ovId); // the overflow dir-index chunk is itself structural.
                ref readonly var ov = ref accessor.GetChunkReadOnly<OverflowDirIndex>(ovId);
                var take = Math.Min(remaining, OverflowDirIndex.EntriesPerChunk);
                for (var j = 0; j < take; j++)
                {
                    ids.Add(ov.DirectoryChunkIds[j]);
                }
                remaining -= take;
                ovId = ov.NextOverflowChunkId;
            }
        }

        return ids.ToArray();
    }

    /// <summary>Number of chunks an index segment reserves for its B-tree directory (chunks 0..3); mirrors <c>BTree.DirectoryChunkCount</c>.</summary>
    private const int BTreeDirectoryChunkCount = 4;

    /// <summary>
    /// Resolves the B-tree index layout for the segment whose root page is <paramref name="indexSegmentRootPage"/>. An index segment hosts one or more B-trees
    /// (the primary key plus one per secondary-indexed field) sharing a chunk-0 directory; this returns <paramref name="directoryChunkCount"/> (chunks
    /// <c>[0, directoryChunkCount)</c> are the structural directory, never nodes) and one named tuple per registered tree (stable id, root chunk,
    /// entry count) parsed from that directory. A node's leaf / internal role is read directly from its own header (bit 1 of the control word), so the Database
    /// File Map (Module 15, A6) needs nothing more here — per-node fill capacity is deliberately not exposed (it would require a full tree walk; see §13 A6).
    /// Returns <see langword="false"/> when no live component table owns that index segment. Read-only; reads the resident directory chunks under an epoch guard
    /// (zero data-page I/O).
    /// </summary>
    internal bool TryGetIndexLayout(int indexSegmentRootPage, out int directoryChunkCount, out (short StableId, int RootChunkId, int EntryCount)[] trees)
    {
        directoryChunkCount = 0;
        trees = [];

        var seg = FindIndexSegment(indexSegmentRootPage);
        if (seg == null)
        {
            return false;
        }

        directoryChunkCount = BTreeDirectoryChunkCount;
        using var guard = EpochGuard.Enter(EpochManager);
        var accessor = seg.CreateChunkAccessor();
        try
        {
            trees = ReadIndexDirectory(ref accessor, seg.Stride);
        }
        finally
        {
            accessor.Dispose();
        }
        return true;
    }

    /// <summary>
    /// Locates a B-tree index segment by its root page across both storage paths: the per-archetype cluster-storage indexes
    /// (<c>ClusterState.IndexSegment</c>, for cluster-eligible SingleVersion archetypes) and the component-table indexes
    /// (<c>DefaultIndexSegment</c> / <c>String64IndexSegment</c> / <c>TailIndexSegment</c>, for the Versioned / legacy path). Returns <see langword="null"/> when
    /// no live segment matches.
    /// </summary>
    private ChunkBasedSegment<PersistentStore> FindIndexSegment(int rootPage)
    {
        var states = _archetypeStates;
        if (states != null)
        {
            foreach (var state in states)
            {
                var clusterIndex = state?.ClusterState?.IndexSegment;
                if (clusterIndex != null && clusterIndex.RootPageIndex == rootPage)
                {
                    return clusterIndex;
                }
            }
        }

        foreach (var table in GetAllComponentTables())
        {
            if (table.DefaultIndexSegment?.RootPageIndex == rootPage)
            {
                return table.DefaultIndexSegment;
            }
            if (table.String64IndexSegment?.RootPageIndex == rootPage)
            {
                return table.String64IndexSegment;
            }
            if (table.TailIndexSegment?.RootPageIndex == rootPage)
            {
                return table.TailIndexSegment;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the B-tree directory (chunk 0, overflowing into chunks 1-3) into one named tuple per registered tree. Mirrors
    /// <see cref="BTree{TKey,TStore}"/>'s <c>ComputeEntryLocation</c>: chunk 0 holds <c>(stride − headerSize) / entrySize</c> entries after the 2-byte header, the rest tile across
    /// chunks 1-3. Cost is O(registered trees) (≤ 20), no node walk.
    /// </summary>
    private static unsafe (short StableId, int RootChunkId, int EntryCount)[] ReadIndexDirectory(ref ChunkAccessor<PersistentStore> accessor, int stride)
    {
        ref readonly var header = ref accessor.GetChunkReadOnly<BTreeDirectoryHeader>(0);
        var count = header.EntryCount;
        if (count == 0)
        {
            return [];
        }

        var headerSize = BTreeDirectoryHeader.Size;
        var entrySize = BTreeDirectoryEntry.Size;
        var entriesInChunk0 = (stride - headerSize) / entrySize;
        var entriesPerChunk = stride / entrySize;

        var result = new (short StableId, int RootChunkId, int EntryCount)[count];
        for (var i = 0; i < count; i++)
        {
            int chunkId, offset;
            if (i < entriesInChunk0)
            {
                chunkId = 0;
                offset = headerSize + i * entrySize;
            }
            else
            {
                var adjusted = i - entriesInChunk0;
                chunkId = 1 + adjusted / entriesPerChunk;
                offset = adjusted % entriesPerChunk * entrySize;
            }

            var addr = accessor.GetChunkAddress(chunkId);
            ref readonly var entry = ref Unsafe.AsRef<BTreeDirectoryEntry>(addr + offset);
            result[i] = (entry.StableId, entry.RootChunkId, entry.Count);
        }

        return result;
    }

    /// <summary>
    /// Resolves the variable-sized-buffer (VSBS / component-collection) layout for the segment whose root page is <paramref name="vsbsSegmentRootPage"/>: the
    /// fixed element size, the per-chunk header size, and the larger root-chunk header size. Used by the Database File Map (Module 15, A6) to compute per-chunk
    /// element fill (<c>ElementCount / ((stride − headerSize) / elementSize)</c>) and decode VSBS chunks. The element size is the segment's generic <c>T</c>, not
    /// stored on disk, so it is recovered from the live component-collection registry. Returns <see langword="false"/> when no live VSBS owns that segment.
    /// Read-only; walks only in-memory state. NOTE: VSBS segments are pooled by stride, so if two element types of the same stride share one segment this returns
    /// the first match's element size — fill is then approximate for the other (a rare edge; single-type segments are exact).
    /// </summary>
    internal bool TryGetVsbsLayout(int vsbsSegmentRootPage, out int elementSize, out int chunkHeaderSize, out int rootHeaderSize)
    {
        elementSize = 0;
        chunkHeaderSize = 8; // VariableSizedBufferChunkHeader = { int NextChunkId; int ElementCount; }
        rootHeaderSize = 0;

        var vsbsByType = _componentCollectionVSBSByType;
        if (vsbsByType == null)
        {
            return false;
        }

        foreach (var vsbs in vsbsByType.Values)
        {
            if (vsbs?.Segment == null || vsbs.Segment.RootPageIndex != vsbsSegmentRootPage)
            {
                continue;
            }

            elementSize = vsbs.ElementSize;
            rootHeaderSize = vsbs.RootHeaderTotalSize;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Maps an occupancy-segment page (by its ordinal within the occupancy segment, 0 = root) to the contiguous range of file pages whose allocation bits it
    /// stores. The root page's data area is shorter than every subsequent page's (it shares the page with the segment's fixed index section), so it governs
    /// fewer pages — a uniform per-page assumption would misalign every range after the root. Used by the Database File Map (Module 15, A6) to render an
    /// occupancy page as a mini allocation-map of the region it governs; the bits themselves come from <see cref="ManagedPagedMMF.ReadOccupancyBits"/>.
    /// </summary>
    internal (long FirstGovernedPage, int GovernedCount) GetOccupancyPageGovernedRange(int occupancyPageOrdinal)
    {
        // One allocation bit governs one file page. The root page stores (PageRawDataSize - index section) bytes of bitmap; subsequent pages store the full
        // PageRawDataSize. ×8 converts bytes → bits → governed pages.
        const int rootGoverned = (PagedMMF.PageRawDataSize - LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength) * 8;
        const int otherGoverned = PagedMMF.PageRawDataSize * 8;

        if (occupancyPageOrdinal <= 0)
        {
            return (0L, rootGoverned);
        }

        return (rootGoverned + (long)(occupancyPageOrdinal - 1) * otherGoverned, otherGoverned);
    }

    private static void AddSegment(List<StorageSegmentDescriptor> sink, LogicalSegment<PersistentStore> segment)
    {
        if (segment == null || segment.Length == 0)
        {
            return;
        }

        // The kind is read from the segment's own persisted root header — self-describing, no context needed. Chunk-based segments also carry the layout
        // constants (stride, per-page chunk counts, chunk-0 byte offsets) that the Database File Map's L3/L4 decoders need to slice chunks out of a page body.
        if (segment is ChunkBasedSegment<PersistentStore> chunked)
        {
            sink.Add(new StorageSegmentDescriptor(segment.RootPageIndex, segment.Kind, segment.Pages.ToArray(), chunked.Stride, chunked.ChunkCountRootPage,
                chunked.ChunkCountPerPage, chunked.RootDataOffset, chunked.OtherDataOffset,
                chunked.AllocatedChunkCount, chunked.FreeChunkCount, chunked.ChunkCapacity));
        }
        else
        {
            sink.Add(new StorageSegmentDescriptor(segment.RootPageIndex, segment.Kind, segment.Pages.ToArray()));
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
        StorageSegmentKind.Spatial => StoragePageType.Spatial,
        StorageSegmentKind.EntityMap => StoragePageType.EntityMap,
        StorageSegmentKind.ComponentCollection => StoragePageType.Vsbs,
        StorageSegmentKind.System => StoragePageType.System,
        _ => StoragePageType.Unknown,
    };
}
