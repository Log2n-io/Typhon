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

    /// <summary>
    /// Audits storage-level invariants and returns every violation found. Pure read-only — touches the occupancy bitmap, the segment registry, and each
    /// segment's forward header chain; no data-page mutation, no allocation beyond a small issue list. Safe to call at any time on a live engine.
    /// </summary>
    /// <remarks>
    /// <para>Two classes of check, each independent:</para>
    /// <list type="bullet">
    /// <item><b>Popcount canary</b> — the count of set bits in the occupancy bitmap must equal the sum of every registered segment's <c>Pages.Length</c>,
    /// plus each segment's directory-map extension pages (the pages outside the root that hold the page-index list when the segment owns more than 500 data
    /// pages — they are bit-set but not part of <c>Pages</c>), plus the four reserved root pages (0..3), plus the two occupancy-reserve pages held by the
    /// page-allocator machinery. Any orphan (bit set, no claimant) or phantom (claimant, bit clear) is reported as a hard durability/structural bug.</item>
    /// <item><b>Chunk-segment capacity</b> — for every <see cref="ChunkBasedSegment{TStore}"/>, <c>AllocatedChunkCount + FreeChunkCount</c> must equal
    /// <c>ChunkCapacity</c>. Desync indicates the segment's chunk free-list drifted from its on-page chunk bitmaps.</item>
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

        // Pass 2: build an "owned" bitmap by ORing in every claimant — segments + their dir-map ext pages + reserves + reserved-root range.
        var owned = new long[wordCount];
        var segClaimedTotal = 0;
        foreach (var seg in segments)
        {
            foreach (var page in seg.Pages)
            {
                if ((uint)page < (uint)pageCount)
                {
                    owned[page >> 6] |= 1L << (page & 0x3F);
                    segClaimedTotal++;
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
        // Reserved roots — pages 0..InitialReservedPageCount-1 are part of the file header layout, always allocated.
        var rootEnd = Math.Min(ManagedPagedMMF.InitialReservedPageCount, pageCount);
        for (var p = 0; p < rootEnd; p++)
        {
            owned[p >> 6] |= 1L << (p & 0x3F);
        }
        // Occupancy reserves — the two pages held outside any segment for occupancy-machinery growth.
        var (dataReserve, mapReserve) = MMF.ReservedOccupancyPages;
        if ((uint)dataReserve < (uint)pageCount)
        {
            owned[dataReserve >> 6] |= 1L << (dataReserve & 0x3F);
        }
        if ((uint)mapReserve < (uint)pageCount)
        {
            owned[mapReserve >> 6] |= 1L << (mapReserve & 0x3F);
        }

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
                if (MMF.RegisteredSegments is ICollection<LogicalSegment<PersistentStore>> coll)
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

        return new StorageIntegrityReport
        {
            Issues = issues,
            OrphanPageCount = orphanCount,
            PhantomPageCount = phantomCount,
            OccupancyBitsSet = bitsSet,
            SegmentClaimedPages = segClaimedTotal,
        };
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
