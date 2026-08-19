using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

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

        // Reserved root / header pages (page index < InitialReservedPageCount: meta pair, occupancy root + its twin, occupancy growth reserves) — unless free.
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

        // CK-05 (C2) directory twins — each shadows a directory page (the primary it pairs with); classify it the same so
        // no allocated page reads as Unknown. The primary has already been classified above (a root via its segment, or a
        // reserved page via the root range), so mirror it. Runs last: the twin always reflects its primary's final type.
        foreach (var (primary, twin) in MMF.DirectoryPairs)
        {
            if ((uint)twin < (uint)pageCount && (uint)primary < (uint)pageCount && pages[twin] != StoragePageType.Free)
            {
                pages[twin] = pages[primary];
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
    /// Resolves the per-component decode layout of a cluster segment for the Database File Map's L5 entity-content level (file-map §10 Q4 override): one entry
    /// per component slot, in cluster slot order (slot <c>c</c> = bit <c>c</c> of <c>EnabledBits</c> / the decoder's enabled mask), carrying the component's
    /// registered name, its inline SoA <c>Offset</c> / <c>Size</c> within the cluster chunk, whether the slot is <c>Transient</c> (its data lives in the in-memory
    /// transient store — <b>not</b> in this persisted chunk, so the decoder must skip it), and the <see cref="DBComponentDefinition"/> the field decoder walks.
    /// SingleVersion and Versioned slots are decoded inline (the Versioned inline copy is the current committed value — a full struct, not a pointer, since
    /// <c>ComponentSize</c> is <c>sizeof(T)</c>); only Transient slots are absent. Returns <see langword="false"/> when no live cluster archetype owns that segment.
    /// Read-only; walks only in-memory archetype state (O(components), no page I/O).
    /// </summary>
    internal bool TryGetClusterEntityLayout(int clusterSegmentRootPage, out int clusterSize, out int entityIdsOffset, out (string Name, int Offset, int Size,
        bool Transient, DBComponentDefinition Definition)[] components)
    {
        clusterSize = 0;
        entityIdsOffset = 0;
        components = [];

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
            entityIdsOffset = layout.EntityIdsOffset;

            // Slot order = the cluster's EnabledBits / the decoder's enabledMask bit order. Resolved from THIS engine's slot→ComponentTable map (not the shared
            // static ArchetypeRegistry, which a colliding archetype id could serve wrong metadata from — see TryGetClusterComponentNames).
            var tables = state.SlotToComponentTable;
            var count = layout.ComponentCount;
            components = new (string, int, int, bool, DBComponentDefinition)[count];
            for (var c = 0; c < count; c++)
            {
                var transient = (layout.TransientSlotMask & (1 << c)) != 0;
                var def = c < tables.Length ? tables[c]?.Definition : null;
                components[c] = (def?.Name ?? "", layout.ComponentOffset(c), layout.ComponentSize(c), transient, def);
            }
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
    /// Spatial-bucketing context for the cluster segment whose root page is <paramref name="clusterSegmentRootPage"/> — the Database File Map / Inspector uses
    /// it to explain why a spatial cluster archetype's slot occupancy is low (each cluster is a per-grid-cell bucket, so entities spread thinly across cells leave
    /// most slots free — not waste). Returns <see langword="true"/> for any live cluster archetype; <paramref name="isSpatial"/> distinguishes a spatial archetype
    /// (clusters bucketed by grid cell — low occupancy is expected) from a non-spatial one (clusters fill linearly — low occupancy means fragmentation). The grid
    /// fields (<paramref name="cellSize"/> / dimensions / <paramref name="spatialMode"/>) are populated only when spatial AND a grid is configured. Read-only,
    /// O(archetypes), no page I/O.
    /// </summary>
    internal bool TryGetClusterSpatialInfo(int clusterSegmentRootPage, out bool isSpatial, out float cellSize, out int gridWidth, out int gridHeight,
        out string spatialMode)
    {
        isSpatial = false;
        cellSize = 0;
        gridWidth = 0;
        gridHeight = 0;
        spatialMode = "";

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

            isSpatial = cluster.SpatialSlot.HasSpatialIndex;
            if (isSpatial && _spatialGrid != null)
            {
                ref readonly var cfg = ref _spatialGrid.Config;
                cellSize = cfg.CellSize;
                gridWidth = cfg.GridWidth;
                gridHeight = cfg.GridHeight;
                spatialMode = cluster.SpatialSlot.FieldInfo.Mode.ToString();
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Per-cluster spatial-cell context for the cluster chunk <paramref name="clusterChunkId"/> of the segment whose root page is
    /// <paramref name="clusterSegmentRootPage"/> — the grid cell the cluster is attached to (key + 2D coords), the cell's live entity / cluster counts (global sums
    /// across every cluster-spatial archetype sharing the grid), and the cluster's tight 2D AABB. This is the per-chunk "why is this cluster mostly empty" answer:
    /// it is the only cluster in a cell that holds just a handful of entities. Returns <see langword="false"/> when no grid is configured, the owning archetype is
    /// non-spatial, the chunk is unmapped (not attached to a cell), or the id is out of range. Read-only, O(archetypes), no page I/O — all reads hit in-memory
    /// transient spatial state.
    /// </summary>
    internal bool TryGetClusterChunkSpatialInfo(int clusterSegmentRootPage, int clusterChunkId, out int cellKey, out int cellX, out int cellY,
        out int entitiesInCell, out int clustersInCell, out float aabbMinX, out float aabbMinY, out float aabbMaxX, out float aabbMaxY)
    {
        cellKey = -1;
        cellX = 0;
        cellY = 0;
        entitiesInCell = 0;
        clustersInCell = 0;
        aabbMinX = 0;
        aabbMinY = 0;
        aabbMaxX = 0;
        aabbMaxY = 0;

        var grid = _spatialGrid;
        if (grid == null)
        {
            return false;
        }

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

            if (!cluster.SpatialSlot.HasSpatialIndex)
            {
                return false;
            }

            var cellMap = cluster.ClusterCellMap;
            if (cellMap == null || (uint)clusterChunkId >= (uint)cellMap.Length)
            {
                return false;
            }
            cellKey = cellMap[clusterChunkId];
            if (cellKey < 0)
            {
                return false; // cluster not attached to a cell (unmapped)
            }

            (cellX, cellY) = grid.CellKeyToCoords(cellKey);
            ref var cell = ref grid.GetCell(cellKey);
            entitiesInCell = cell.EntityCount;
            clustersInCell = cell.ClusterCount;

            var aabbs = cluster.ClusterAabbs;
            if (aabbs != null && (uint)clusterChunkId < (uint)aabbs.Length)
            {
                ref var box = ref aabbs[clusterChunkId];
                aabbMinX = box.MinX;
                aabbMinY = box.MinY;
                aabbMaxX = box.MaxX;
                aabbMaxY = box.MaxY;
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
    /// Resolves the display name of the archetype that owns the cluster, entity-map, or cluster-index segment whose root page is <paramref name="segmentRootPage"/>.
    /// These segment kinds are archetype-owned (a cluster-eligible archetype's SoA row store, its entity-id → cluster-slot linear hash, and its SoA field index)
    /// rather than component-owned, so the component-table resolver can't name them — this walks the per-archetype state to find the owner, then formats the name
    /// with the SAME precedence the schema endpoint uses (<c>Alias</c> → CLR <c>FullName</c> → CLR <c>Name</c>) so the Workbench's short-name labeller shortens it
    /// identically to everywhere else. Returns <see langword="false"/> when no live archetype owns that segment (or <paramref name="kind"/> is not Cluster,
    /// EntityMap, or Index). Read-only; walks only in-memory archetype state (O(archetypes), no page I/O).
    /// </summary>
    internal bool TryGetSegmentOwnerArchetypeName(int segmentRootPage, StorageSegmentKind kind, out string archetypeName)
    {
        archetypeName = "";

        var states = _archetypeStates;
        if (states == null)
        {
            return false;
        }

        for (var archetypeId = 0; archetypeId < states.Length; archetypeId++)
        {
            var state = states[archetypeId];
            if (state == null)
            {
                continue;
            }

            var owns = kind switch
            {
                StorageSegmentKind.Cluster => state.ClusterState?.ClusterSegment != null
                    && state.ClusterState.ClusterSegment.RootPageIndex == segmentRootPage,
                StorageSegmentKind.EntityMap => state.EntityMap?.Segment != null
                    && state.EntityMap.Segment.RootPageIndex == segmentRootPage,
                // The per-archetype cluster index (the SoA index over a cluster-eligible archetype's indexed SV fields).
                // It is archetype-owned, unlike the per-component-table indexes (Default/String64/Tail), so the
                // component-table resolver can't name it — without this case it falls back to a bare "Index #id" label.
                StorageSegmentKind.Index => state.ClusterState?.IndexSegment != null
                    && state.ClusterState.IndexSegment.RootPageIndex == segmentRootPage,
                _ => false,
            };
            if (!owns)
            {
                continue;
            }

            var meta = ArchetypeRegistry.GetMetadata((ushort)archetypeId);
            archetypeName = meta?.Alias ?? meta?.ArchetypeType?.FullName ?? meta?.ArchetypeType?.Name ?? archetypeId.ToString();
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
    internal bool TryGetIndexLayout(int indexSegmentRootPage, out int directoryChunkCount,
        out (short StableId, short Slot, int RootChunkId, int EntryCount)[] trees)
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
    /// Returns <see langword="null"/> when
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

        // The per-ComponentTable index segments this used to scan after the cluster ones no longer exist (#629); every index page belongs to an archetype.

        return null;
    }

    /// <summary>
    /// Reads the B-tree directory (chunk 0, overflowing into chunks 1-3) into one named tuple per registered tree. Mirrors
    /// <see cref="BTree{TKey,TStore}"/>'s <c>ComputeEntryLocation</c>: chunk 0 holds <c>(stride − headerSize) / entrySize</c> entries after the 2-byte header, the rest tile across
    /// chunks 1-3. Cost is O(registered trees) (see <c>BTree.MaxDirectoryEntriesFor</c> — 84 at the 256-byte stride), no node walk.
    /// </summary>
    private static unsafe (short StableId, short Slot, int RootChunkId, int EntryCount)[] ReadIndexDirectory(ref ChunkAccessor<PersistentStore> accessor, 
        int stride)
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

        var result = new (short StableId, short Slot, int RootChunkId, int EntryCount)[count];
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
            result[i] = (entry.StableId, entry.Slot, entry.RootChunkId, entry.Count);
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
    /// stores. With the directory-only root (v4) the root page holds ONLY the segment's page directory — it stores no bitmap words, so it governs zero file
    /// pages; every subsequent page stores the full <see cref="PagedMMF.PageRawDataSize"/> of L0 words. Used by the Database File Map (Module 15, A6) to render
    /// an occupancy page as a mini allocation-map of the region it governs; the bits themselves come from <see cref="ManagedPagedMMF.ReadOccupancyBits"/>.
    /// </summary>
    internal (long FirstGovernedPage, int GovernedCount) GetOccupancyPageGovernedRange(int occupancyPageOrdinal)
    {
        // One allocation bit governs one file page. The directory-only root stores no bitmap words (rootGoverned == 0); each data page stores the full
        // PageRawDataSize. ×8 converts bytes → bits → governed pages. Data-page ordinal k (k >= 1) governs file pages [(k-1)·otherGoverned, k·otherGoverned).
        const int rootGoverned = (PagedMMF.PageRawDataSize - LogicalSegment<PersistentStore>.RootHeaderIndexSectionLength) * 8; // == 0 under the v4 layout
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

    /// <summary>
    /// Builds the authoritative "owned" page bitmap — one bit per file page, set iff some structure claims that page: every registered segment's <c>Pages</c>,
    /// their directory-map extension pages, the reserved root range (0..<see cref="ManagedPagedMMF.InitialReservedPageCount"/>), the occupancy reserves, and
    /// the CK-05 directory twins. This is the SAME reconstruction the popcount canary in <see cref="RunStorageIntegrityCheck"/> validates against the persisted
    /// bitmap, and the crash-path occupancy re-derive (<see cref="ManagedPagedMMF.RederiveOccupancy"/>) adopts it wholesale — occupancy is a DERIVED structure,
    /// never trusted post-crash (the FPI replacement for the bitmap). The returned array is sized to cover <c>max(occupancy capacity, file page count)</c>,
    /// matching the occupancy bitmap's own word count.
    /// </summary>
    /// <param name="segClaimedPages">Count of segment-claimed pages within the file (diagnostic, surfaced by the integrity report).</param>
    /// <param name="unresolvedPersistedSpis">
    /// Number of persisted archetype segment pointers this reconstruction could not read. Non-zero means the returned bitmap is a PARTIAL view of page
    /// ownership, which is safe to compare against but must never be adopted wholesale — see <see cref="RederiveOccupancyOnCrash"/> (#771).
    /// </param>
    internal long[] BuildOwnedPageBitmap(out int segClaimedPages, out int unresolvedPersistedSpis)
    {
        var pageCount = MMF.StorageFilePageCount;
        var capacity = MMF.OccupancyCapacityPages;
        var wordCount = (Math.Max(capacity, pageCount) + 63) / 64;
        var owned = new long[wordCount];

        // Claim limit for pages whose ownership is recorded in durable metadata rather than discovered by walking written pages: the occupancy reserves and the
        // CK-05 twins. Those are allocated — and bit-set in the persisted occupancy bitmap — the moment they are handed out, but nothing writes to them until
        // they are first used, and `pageCount` is the high-water of what has been WRITTEN. Clipping them to the file therefore drops pages that are genuinely
        // owned, and this bitmap is adopted WHOLESALE by the crash-path re-derive: a dropped page is written free while the metadata still names it, and the
        // next allocation hands it to a second owner (CK-09 on_violation).
        //
        // It stayed invisible because those pages were usually written by accident. Leaked dirty marks kept unrelated pages permanently "dirty", so every
        // checkpoint rewrote them and the file grew past the reserves and twins long before anyone looked. Conserving the marks removed the accident.
        //
        // Both twin paths — the registered one below and ClaimDirectoryTwin on the persisted walk — use THIS limit, not pageCount. They have to agree: the two
        // reconstructions are asserted bit-identical (OwnedBitmapIsIdenticalWithAndWithoutSchema), and lifting the bound on one alone makes ownership depend on
        // whether the caller happened to register the schema, which is the very thing CK-09 forbids.
        var ownedLimit = (long)wordCount * 64;
        var segments = MMF.RegisteredSegments;

        var claimed = 0;
        foreach (var seg in segments)
        {
            foreach (var page in seg.Pages)
            {
                if ((uint)page < (uint)pageCount)
                {
                    owned[page >> 6] |= 1L << (page & 0x3F);
                    claimed++;
                }
            }
        }

        // Directory-map extension pages — outside Pages but bit-set; reachable via LogicalSegmentNextMapPBID.
        using (var dirMapGuard = EpochGuard.Enter(EpochManager))
        {
            var extBuf = new List<int>();
            foreach (var seg in segments)
            {
                extBuf.Clear();
                seg.CollectDirectoryMapExtensionPages(dirMapGuard.Epoch, extBuf);
                foreach (var p in extBuf)
                {
                    if ((uint)p < (uint)pageCount)
                    {
                        owned[p >> 6] |= 1L << (p & 0x3F);
                    }
                }
            }
        }

        // Persisted archetypes this session did NOT materialize (#771). InitializeArchetypes iterates ArchetypeRegistry.GetAllArchetypes() — the CLR types the
        // CALLER registered — so an archetype whose type is absent is never visited and its four segments never reach RegisteredSegments. That is correct for
        // the session (nothing can spawn or read an archetype with no accessor), but this bitmap is a claim about the FILE, and it is adopted WHOLESALE by the
        // crash-path re-derive. Omitting those pages marks live data free, and the next allocation hands one of them to a second owner — CK-09's on_violation,
        // exactly. Opening with a subset of the schema is supported (a repair or forensic tool has no schema assembly at all), so ownership must not depend on
        // what happened to be registered.
        //
        // The persisted record is consulted ONLY where the session has no live view, and that restriction is load-bearing in the other direction: for a
        // materialized archetype the registry is NEWER. A migrating open abandons the old segments, fresh-allocates, and frees the old pages, while ArchetypeR1
        // still names the old roots until the next checkpoint persists the new SPIs — claiming those would invent phantoms over genuinely free pages, which is
        // the mirror image of the bug being fixed here.
        unresolvedPersistedSpis = 0;
        if (_persistedArchetypes != null && _persistedArchetypes.Count > 0)
        {
            var materializedArchetypes = MaterializedArchetypeNames();
            using var spiGuard = EpochGuard.Enter(EpochManager);
            foreach (var kv in _persistedArchetypes)
            {
                if (materializedArchetypes.Contains(kv.Key))
                {
                    continue;
                }

                var arch = kv.Value.Arch;
                ClaimPersistedSegment(arch.EntityMapSPI, spiGuard.Epoch, owned, pageCount, ownedLimit, ref claimed, ref unresolvedPersistedSpis);
                ClaimPersistedSegment(arch.ClusterSegmentSPI, spiGuard.Epoch, owned, pageCount, ownedLimit, ref claimed, ref unresolvedPersistedSpis);
                ClaimPersistedSegment(arch.ClusterIndexSPI, spiGuard.Epoch, owned, pageCount, ownedLimit, ref claimed, ref unresolvedPersistedSpis);
                ClaimPersistedSegment(arch.ClusterString64IndexSPI, spiGuard.Epoch, owned, pageCount, ownedLimit, ref claimed, ref unresolvedPersistedSpis);
            }
        }

        // The same argument for component-owned segments. A ComponentTable is built by RegisterComponentFromAccessor, so an unregistered component's data and
        // revision segments are as absent from the registry as its archetype's are, and just as owned.
        if (_persistedComponents != null && _persistedComponents.Count > 0)
        {
            var materializedComponents = MaterializedComponentNames();
            using var compGuard = EpochGuard.Enter(EpochManager);
            foreach (var kv in _persistedComponents)
            {
                if (materializedComponents.Contains(kv.Key))
                {
                    continue;
                }

                var comp = kv.Value.Comp;
                ClaimPersistedSegment(comp.ComponentSPI, compGuard.Epoch, owned, pageCount, ownedLimit, ref claimed, ref unresolvedPersistedSpis);
                ClaimPersistedSegment(comp.VersionSPI, compGuard.Epoch, owned, pageCount, ownedLimit, ref claimed, ref unresolvedPersistedSpis);
            }
        }

        // Reserved roots — pages 0..InitialReservedPageCount-1 are part of the file header layout, always allocated.
        var rootEnd = Math.Min(ManagedPagedMMF.InitialReservedPageCount, pageCount);
        for (var p = 0; p < rootEnd; p++)
        {
            owned[p >> 6] |= 1L << (p & 0x3F);
        }

        // Occupancy reserves — the pages held outside any segment for occupancy-machinery growth (data, map-extension, and the map-extension twin).
        //
        // Bounded by the BITMAP's extent, not by the file page count as every other claim here is. A reserve is recorded in the root header and allocated in the
        // occupancy bitmap the moment it is handed out, but nothing writes to it until it is consumed — and the file's page count is the high-water of what has
        // been WRITTEN. So a freshly reserved page legitimately sits one past the end of the file, and clipping it here drops a page that is genuinely owned.
        //
        // The consequence was not cosmetic: this bitmap is adopted WHOLESALE by the crash-path re-derive, so a dropped reserve is cleared to free while the root
        // header still names it — and the next allocation hands the same page to a second owner. It went unnoticed because the reserve was usually written by
        // accident: leaked dirty marks kept unrelated pages permanently "dirty", every checkpoint rewrote them, and the file grew past the reserve. Fixing the
        // leak removed the accident and left the real bug exposed, which is the only reason it is visible now.
        var (dataReserve, mapReserve, mapTwinReserve) = MMF.ReservedOccupancyPages;
        if (dataReserve > 0 && dataReserve < ownedLimit)
        {
            owned[dataReserve >> 6] |= 1L << (dataReserve & 0x3F);
        }
        if (mapReserve > 0 && mapReserve < ownedLimit)
        {
            owned[mapReserve >> 6] |= 1L << (mapReserve & 0x3F);
        }
        if (mapTwinReserve > 0 && mapTwinReserve < ownedLimit)
        {
            owned[mapTwinReserve >> 6] |= 1L << (mapTwinReserve & 0x3F);
        }

        // CK-05 (C2) directory twins — each is bit-set in the occupancy bitmap but lives in no segment's page list; the pair state owns them.
        foreach (var (_, twin) in MMF.DirectoryPairs)
        {
            if (twin > 0 && twin < ownedLimit)
            {
                owned[twin >> 6] |= 1L << (twin & 0x3F);
            }
        }

        segClaimedPages = claimed;
        return owned;
    }

    /// <summary>
    /// Claims the pages of one persisted segment pointer by walking its page directory <b>in place</b>, without constructing or registering a
    /// <see cref="LogicalSegment{TStore}"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ManagedPagedMMF.TryLoadChunkBasedSegment"/> is deliberately NOT used here: it inserts into the segment registry, which would make
    /// <see cref="BuildOwnedPageBitmap"/> — documented and relied upon as read-only, and called from <see cref="RunStorageIntegrityCheck"/> on a live engine —
    /// mutate the engine it is describing. It would also need a stride this caller has no schema to compute.
    /// </para>
    /// <para>
    /// A pointer of <c>0</c> means the archetype has no such segment, which is not a failure. A root page that is already claimed was reached through
    /// <see cref="ManagedPagedMMF.RegisteredSegments"/> and needs no second walk. Anything else that cannot be read increments
    /// <c>unresolved</c> so the caller can refuse to adopt an incomplete picture.
    /// </para>
    /// </remarks>
    private void ClaimPersistedSegment(int rootPageIndex, long epoch, long[] owned, int pageCount, long ownedLimit, ref int claimed, ref int unresolved)
    {
        if (rootPageIndex <= 0 || (uint)rootPageIndex >= (uint)pageCount)
        {
            // 0 is "this archetype has no such segment". A pointer outside the file is a different statement — the directory names a page that does not
            // exist — and it is exactly the case that must not be silently treated as "nothing to claim".
            if (rootPageIndex != 0)
            {
                unresolved++;
            }

            return;
        }

        if (((owned[rootPageIndex >> 6] >> (rootPageIndex & 0x3F)) & 1) != 0)
        {
            // Already claimed — two persisted records naming the same root, or a root that is also another segment's twin. Walking it twice is harmless but
            // pointless; the caller has already skipped the materialized archetypes, so this is not the "session loaded it" case.
            return;
        }

        try
        {
            var store = new PersistentStore(MMF);
            store.RequestPageEpoch(rootPageIndex, epoch, out var memPageIndex);
            var page = store.GetPage(memPageIndex);

            owned[rootPageIndex >> 6] |= 1L << (rootPageIndex & 0x3F);
            claimed++;
            ClaimDirectoryTwin(page, owned, ownedLimit);

            // Same directory traversal as LogicalSegment.Load: the root's index section, then each map-extension page in the LogicalSegmentNextMapPBID chain.
            // The extension pages are themselves owned (they are bit-set but appear in no segment's Pages list), so they are claimed as they are walked.
            var rd = page.RawDataReadOnly<int>(0, LogicalSegment<PersistentStore>.RootHeaderIndexSectionCount);
            var maxIndicesForPage = LogicalSegment<PersistentStore>.RootHeaderIndexSectionCount;
            var i = 0;
            var walkedExtensions = 0;

            while (rd[i] != 0)
            {
                var dataPage = rd[i];
                if ((uint)dataPage < (uint)pageCount && ((owned[dataPage >> 6] >> (dataPage & 0x3F)) & 1) == 0)
                {
                    owned[dataPage >> 6] |= 1L << (dataPage & 0x3F);
                    claimed++;
                }

                if (++i != maxIndicesForPage)
                {
                    continue;
                }

                var next = page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).LogicalSegmentNextMapPBID;
                if (next == 0 || (uint)next >= (uint)pageCount || ++walkedExtensions > MaxDirectoryMapExtensionWalk)
                {
                    break;
                }

                // A map-extension page is bit-set but in no segment's Pages list, so it is claimed without advancing the counter — same
                // treatment the registered path's CollectDirectoryMapExtensionPages block gives it.
                owned[next >> 6] |= 1L << (next & 0x3F);

                store.RequestPageEpoch(next, epoch, out memPageIndex);
                page = store.GetPage(memPageIndex);
                ClaimDirectoryTwin(page, owned, ownedLimit);
                rd = page.RawDataReadOnly<int>(0, LogicalSegment<PersistentStore>.NextHeadersIndexSectionCount);
                maxIndicesForPage = LogicalSegment<PersistentStore>.NextHeadersIndexSectionCount;
                i = 0;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            // A torn or unreadable directory. Recording it is the point: the caller must not overwrite the persisted bitmap with a reconstruction that is
            // missing a segment it knows exists.
            unresolved++;
        }
    }

    /// <summary>
    /// Claims the CK-05 (C2) twin of one directory page, read from that page's own <c>TwinPageIndex</c>.
    /// </summary>
    /// <remarks>
    /// The registered path gets its twins from <see cref="ManagedPagedMMF.DirectoryPairs"/>, which is populated by
    /// <c>ResolveDirectoryPairsForLoad</c> — and that runs only inside a segment <i>load</i>. A segment claimed without loading therefore contributes no pair,
    /// so its twin has to come from where the pair state itself reads it: the primary's header. A twin is bit-set in the occupancy bitmap while belonging to no
    /// segment's page list, so it is exactly the kind of page a reconstruction drops silently.
    /// </remarks>
    /// <remarks>
    /// Sets the ownership bit but does NOT advance <c>segClaimedPages</c>: that counter means "pages listed in some segment's
    /// <c>Pages</c>", and a twin belongs to no segment's page list — which is precisely why it needs claiming separately.
    /// The registered path's twin and map-extension blocks do not advance it either, so counting here would make the same
    /// database report a different total depending on whether its schema was registered.
    /// </remarks>
    private static void ClaimDirectoryTwin(PageAccessor page, long[] owned, long ownedLimit)
    {
        var twin = page.StructAt<LogicalSegmentHeader>(LogicalSegmentHeader.Offset).TwinPageIndex;
        if (twin <= 0 || twin >= ownedLimit)
        {
            return;
        }

        owned[twin >> 6] |= 1L << (twin & 0x3F);
    }

    /// <summary>Names of the archetypes this session actually materialized, whose live segments are already in the registry.</summary>
    private HashSet<string> MaterializedArchetypeNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var byRouting = _metaByRouting;
        if (byRouting == null)
        {
            return names;
        }

        for (var i = 0; i < byRouting.Length; i++)
        {
            var meta = byRouting[i];
            if (meta != null)
            {
                names.Add(meta.Name);
            }
        }

        return names;
    }

    /// <summary>Schema names of the components this session actually registered, whose live segments are already in the registry.</summary>
    private HashSet<string> MaterializedComponentNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in GetAllComponentTables())
        {
            names.Add(table.Definition.Name);
        }

        return names;
    }

    /// <summary>Cycle guard for the directory-map extension chain — a segment cannot legitimately exceed the file's page count in extensions.</summary>
    private const int MaxDirectoryMapExtensionWalk = 4096;

    /// <summary>
    /// Audits storage-level invariants and returns every violation found. Pure read-only — touches the occupancy bitmap, the segment registry, and each
    /// segment's forward header chain; no data-page mutation, no allocation beyond a small issue list. Safe to call at any time on a live engine.
    /// </summary>
    /// <remarks>
    /// <para>Two classes of check, each independent:</para>
    /// <list type="bullet">
    /// <item><b>Popcount canary</b> — the count of set bits in the occupancy bitmap must equal the sum of every registered segment's <c>Pages.Length</c>,
    /// plus each segment's directory-map extension pages (the pages outside the root that hold the page-index list when the segment owns more than 500 data
    /// pages — they are bit-set but not part of <c>Pages</c>), plus each directory page's CK-05 twin (the alternate slot, bit-set but in no segment list),
    /// plus the reserved root pages (0..InitialReservedPageCount-1), plus the occupancy-reserve pages (data, map-extension, and the map-extension twin) held by
    /// the page-allocator machinery. Any orphan (bit set, no claimant) or phantom (claimant, bit clear) is reported as a hard durability/structural bug.</item>
    /// <item><b>Chunk-segment capacity</b> — for every <see cref="ChunkBasedSegment{TStore}"/>, <c>AllocatedChunkCount + FreeChunkCount</c> must equal
    /// <c>ChunkCapacity</c>. Desync indicates the segment's chunk free-list drifted from its on-page chunk bitmaps.</item>
    /// <item><b>Cluster MVCC visibility summary</b> — every cluster's <c>ClusterMaxBornTsn</c> / <c>ClusterMaxDiedTsn</c> pair, recomputed from the archetype's
    /// EntityMap and compared against the maintained one. A summary that claims more visibility than its entities justify makes the SoA scan skip its
    /// per-entity probe and emit a phantom; see <see cref="StorageIntegrityIssueKind.ClusterVisibilitySummaryUnsound"/>.</item>
    /// </list>
    /// </remarks>
    public StorageIntegrityReport RunStorageIntegrityCheck()
    {
        var issues = new List<StorageIntegrityIssue>();
        var pageCount = MMF.StorageFilePageCount;
        var segments = MMF.RegisteredSegments;

        // ─── Popcount canary ─
        // Pass 1: build the bitmap into a long[] mirroring ClassifyAllPages' shape.
        var capacity = MMF.OccupancyCapacityPages;
        var wordCount = (Math.Max(capacity, pageCount) + 63) / 64;
        var words = new long[wordCount];
        MMF.ReadOccupancyBits(words);

        // Pass 2: the authoritative "owned" bitmap (every claimant). Shared with the crash-path occupancy re-derive so the canary and the heal agree by construction.
        // The canary only COMPARES, so an incomplete reconstruction costs it accuracy (a phantom it cannot explain), never data — the opposite of the
        // re-derive, which writes it. Hence the count is read and reported rather than fatal here.
        var owned = BuildOwnedPageBitmap(out var segClaimedTotal, out _);

        // Compare word-by-word — orphans = bits set in `words` but not in `owned`, phantoms = vice versa.
        var bitsSet = 0;
        var orphanCount = 0;
        var phantomCount = 0;
        var orphanRanges = new List<(int start, int count)>();
        var phantomRanges = new List<(int start, int count)>();
        var orphanRunStart = -1; var orphanRunLen = 0;
        var phantomRunStart = -1; var phantomRunLen = 0;
        for (var p = 0; p < pageCount; p++)
        {
            var setBit = (words[p >> 6] >> (p & 0x3F)) & 1;
            var ownBit = (owned[p >> 6] >> (p & 0x3F)) & 1;
            bitsSet += (int)setBit;

            if (setBit == 1 && ownBit == 0)
            {
                // Orphan — bit set, no owner.
                orphanCount++;
                if (orphanRunStart < 0) { orphanRunStart = p; orphanRunLen = 1; } else { orphanRunLen++; }
            }
            else if (orphanRunStart >= 0)
            {
                orphanRanges.Add((orphanRunStart, orphanRunLen));
                orphanRunStart = -1;
            }

            if (setBit == 0 && ownBit == 1)
            {
                phantomCount++;
                if (phantomRunStart < 0) { phantomRunStart = p; phantomRunLen = 1; } else { phantomRunLen++; }
            }
            else if (phantomRunStart >= 0)
            {
                phantomRanges.Add((phantomRunStart, phantomRunLen));
                phantomRunStart = -1;
            }
        }
        if (orphanRunStart >= 0) orphanRanges.Add((orphanRunStart, orphanRunLen));
        if (phantomRunStart >= 0) phantomRanges.Add((phantomRunStart, phantomRunLen));

        foreach (var (start, count) in orphanRanges)
        {
            issues.Add(new StorageIntegrityIssue(
                StorageIntegrityIssueKind.PopcountOrphan, 0, start, count,
                $"orphan range [{start}..{start + count - 1}] — {count} page(s) set in bitmap but not in any segment / reserve / root"));
        }
        foreach (var (start, count) in phantomRanges)
        {
            issues.Add(new StorageIntegrityIssue(
                StorageIntegrityIssueKind.PopcountPhantom, 0, start, count,
                $"phantom range [{start}..{start + count - 1}] — {count} page(s) claimed by a segment but bitmap bit clear"));
        }

        // ─── In-memory chain ↔ directory cross-check (LIVE engine, no disk roundtrip) ─
        using (var chainGuard = EpochGuard.Enter(EpochManager))
        {
            foreach (var seg in segments)
            {
                if (seg.Pages.Length == 0) continue;
                LogicalSegment<PersistentStore> ls = null;
                if (MMF.RegisteredSegments is { } coll)
                {
                    foreach (var s in coll)
                    {
                        if (s.RootPageIndex == seg.RootPageIndex)
                        {
                            ls = s;
                            break;
                        }
                    }
                }
                if (ls == null) continue;
                var chainCount = ls.WalkForwardChainPageCount(chainGuard.Epoch);
                var dirCount = ls.VerifyDirectoryAgainst(chainGuard.Epoch, seg.Pages);
                if (chainCount != seg.Pages.Length || dirCount != seg.Pages.Length)
                {
                    issues.Add(new StorageIntegrityIssue(
                        StorageIntegrityIssueKind.ChainDirectoryMismatch, seg.RootPageIndex, -1, 0,
                        $"IN-MEMORY mismatch: root={seg.RootPageIndex} kind={seg.Kind} _pages.Length={seg.Pages.Length} chain={chainCount} dir={dirCount}"));
                }
            }
        }

        // ─── Chunk-segment internal capacity ─
        foreach (var seg in segments)
        {
            if (seg is not ChunkBasedSegment<PersistentStore> cbs)
            {
                continue;
            }
            var sum = cbs.AllocatedChunkCount + cbs.FreeChunkCount;
            if (sum != cbs.ChunkCapacity)
            {
                issues.Add(new StorageIntegrityIssue(
                    StorageIntegrityIssueKind.ChunkSegmentCapacity, cbs.Pages[0], -1, 0,
                    $"segment root={cbs.Pages[0]} kind={cbs.Kind} alloc={cbs.AllocatedChunkCount} free={cbs.FreeChunkCount} " +
                    $"sum={sum} capacity={cbs.ChunkCapacity}"));
            }
        }

        // ─── Cluster MVCC visibility summary ─
        CheckClusterVisibilitySummaries(issues, out var visibilityClustersChecked);

        return new StorageIntegrityReport
        {
            Issues = issues,
            OrphanPageCount = orphanCount,
            PhantomPageCount = phantomCount,
            OccupancyBitsSet = bitsSet,
            SegmentClaimedPages = segClaimedTotal,
            VisibilitySummaryClustersChecked = visibilityClustersChecked,
        };
    }

    /// <summary>Sentinel for "no EntityMap record named this cluster" in the recomputed born-TSN array; a real <c>BornTSN</c> is never negative.</summary>
    private const long NoEntityInCluster = -1;

    /// <summary>
    /// Recomputes every cluster archetype's MVCC visibility summary from its EntityMap and reports each cluster whose maintained summary claims MORE
    /// visibility than the entities present justify. O(entities); read-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> The summary (<c>ClusterMaxBornTsn</c> / <c>ClusterMaxDiedTsn</c> on <see cref="ArchetypeClusterState"/>) lets the SoA scan skip its
    /// per-entity EntityMap probe for a whole cluster. It is maintained by five sites — spawn commit, WAL replay, both reopen rebuilds, and spatial cluster
    /// migration — and a sixth site added later that forgets to fold produces a phantom rather than a failure. Enumerating the sites is the weaker move;
    /// checking the invariant is the stronger one.
    /// </para>
    /// <para>
    /// <b>One-directional by design.</b> The summary is a conservative approximation: it may say "probe" when probing was unnecessary (slower, still correct)
    /// and must never say "clean" when an entity could be invisible. Only the unsound direction is reported — a pessimistic summary is a legal state, not a
    /// defect, so asserting equality would fail on healthy engines.
    /// </para>
    /// <para>
    /// <b>Safe on a live engine, and no false positives.</b> Both maintenance sites publish the summary BEFORE the EntityMap entry the recompute reads it
    /// from (<c>NoteClusterBorn</c> precedes <c>EntityMap.InsertNew</c> on the spawn path; <c>NoteClusterDied</c> precedes the tombstone <c>Upsert</c> on the
    /// destroy path). Pairing that with reading the summary AFTER the walk makes the compared summary at least as new as every record it is compared against,
    /// so a concurrent spawn cannot be reported as a phantom. A false positive here would be worse than no check at all — it is how an invariant check gets
    /// switched off.
    /// </para>
    /// <para>
    /// <b>What it does not cover.</b> The oracle is the EntityMap — the same structure the gate's per-entity probe reads — so an entity occupying a cluster
    /// slot whose EntityMap record names a DIFFERENT cluster is attributed to the record's cluster, not the slot's. That divergence is a location-consistency
    /// defect of its own class, outside the summary invariant checked here.
    /// </para>
    /// </remarks>
    private void CheckClusterVisibilitySummaries(List<StorageIntegrityIssue> issues, out int clustersChecked)
    {
        clustersChecked = 0;
        var states = _archetypeStates;
        if (states == null)
        {
            return;
        }

        using var guard = EpochGuard.Enter(EpochManager);
        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (meta.ArchetypeId >= states.Length)
            {
                continue;
            }

            var state = states[meta.ArchetypeId];
            var clusterState = state?.ClusterState;
            if (clusterState == null || state.EntityMap == null)
            {
                continue;
            }

            var sizing = Volatile.Read(ref clusterState.ClusterMaxBornTsn);
            if (sizing == null)
            {
                continue;   // no site has established anything for this archetype; every cluster takes the per-entity probe
            }

            var clusterCount = sizing.Length;
            var recompute = new ClusterVisibilityRecomputeAction
            {
                ClusterCount = clusterCount,
                MaxBorn = new long[clusterCount],
                MaxDied = new long[clusterCount],
            };
            Array.Fill(recompute.MaxBorn, NoEntityInCluster);

            var accessor = state.EntityMap.Segment.CreateChunkAccessor();
            state.EntityMap.ForEachEntry(ref accessor, ref recompute);
            accessor.Dispose();

            // Re-read the summary AFTER the walk, and in IsClusterFullyVisibleAt's order (maxBorn, then died — never the reverse, or a new born can pair with
            // a stale short died array). Because every site folds BEFORE publishing the EntityMap entry, a summary read after the walk is at least as new as
            // every record the walk compared against it. Reading it before instead would race with a concurrent spawn that grows and replaces the array, and
            // report a phantom that was never there — a false positive is how an invariant check gets disabled.
            var maxBorn = Volatile.Read(ref clusterState.ClusterMaxBornTsn);
            var died = Volatile.Read(ref clusterState.ClusterMaxDiedTsn);

            var rootPage = clusterState.ClusterSegment?.RootPageIndex ?? 0;
            for (var c = 0; c < clusterCount && c < maxBorn!.Length; c++)
            {
                var actualBorn = recompute.MaxBorn[c];
                if (actualBorn == NoEntityInCluster)
                {
                    continue;   // no EntityMap record names this cluster — nothing the gate could be asked about
                }

                clustersChecked++;

                // VisibilityUnknown needs no special case: it is long.MaxValue, so it can never compare below a real BornTSN — an unestablished summary is
                // maximally pessimistic and the gate rejects it outright.
                var claimedBorn = maxBorn[c];
                if (claimedBorn < actualBorn)
                {
                    issues.Add(new StorageIntegrityIssue(
                        StorageIntegrityIssueKind.ClusterVisibilitySummaryUnsound, rootPage, -1, 0,
                        $"archetype '{meta.Name}' cluster {c}: ClusterMaxBornTsn={claimedBorn} but an entity in it was born at {actualBorn} — a reader at " +
                        $"any snapshot in [{claimedBorn}..{actualBorn - 1}] passes the cluster gate, skips the per-entity probe and emits an unborn entity"));
                }

                // A died array that is absent or too short reads as "cannot tell" at the gate, which is conservative — only an UNDER-recorded watermark inside
                // a sized array is unsound. This is strictly stronger than the flag it replaced: a boolean could only catch a death recorded nowhere, whereas
                // a maximum also catches one recorded with too small a TSN, which admits exactly the readers in between (#722).
                var actualDied = recompute.MaxDied[c];
                if (actualDied != 0 && died != null && (uint)c < (uint)died.Length && died[c] < actualDied)
                {
                    issues.Add(new StorageIntegrityIssue(
                        StorageIntegrityIssueKind.ClusterVisibilitySummaryUnsound, rootPage, -1, 0,
                        $"archetype '{meta.Name}' cluster {c}: ClusterMaxDiedTsn={died[c]} but an entity in it died at {actualDied} — a reader at any " +
                        $"snapshot in [{died[c]}..{actualDied - 1}] passes the cluster gate, skips the per-entity probe and misses a tombstone it must "
                        + "still see"));
                }
            }
        }
    }

    /// <summary>
    /// EntityMap walk that recomputes one archetype's per-cluster visibility summary from the very records the gate's per-entity probe would have read.
    /// </summary>
    private struct ClusterVisibilityRecomputeAction : RawValuePagedHashMap<long, PersistentStore>.IEntryAction<long>
    {
        /// <summary>Length of the maintained summary; records naming a cluster at or beyond it are ignored (the gate range-checks and probes there).</summary>
        public int ClusterCount;

        /// <summary>Recomputed maximum <c>BornTSN</c> per cluster, <see cref="NoEntityInCluster"/> where no record named the cluster.</summary>
        public long[] MaxBorn;

        /// <summary>Recomputed maximum <c>DiedTSN</c> per cluster, 0 where no record in it carries one.</summary>
        public long[] MaxDied;

        public unsafe bool Process(long key, byte* value)
        {
            var chunkId = ClusterEntityRecordAccessor.GetClusterChunkId(value);
            if ((uint)chunkId >= (uint)ClusterCount)
            {
                return true;
            }

            ref readonly var header = ref ClusterEntityRecordAccessor.GetHeader(value);
            var born = header.BornTSN;
            if (born > MaxBorn[chunkId])
            {
                MaxBorn[chunkId] = born;
            }

            if (header.DiedTSN > MaxDied[chunkId])
            {
                MaxDied[chunkId] = header.DiedTSN;
            }

            return true;
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
