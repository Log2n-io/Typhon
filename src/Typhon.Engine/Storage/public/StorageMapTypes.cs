using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Semantic classification of a single file page, used by the Workbench Database File Map (Module 15).
/// Unlike <see cref="PageBlockType"/> — which only distinguishes <c>None</c> / <c>OccupancyMap</c> — this enum captures the role a page plays in the engine's
/// storage graph.
/// </summary>
[PublicAPI]
public enum StoragePageType : byte
{
    /// <summary>Page is allocated but not classifiable by Track-A introspection (e.g. cluster pages).</summary>
    Unknown = 0,

    /// <summary>Page is not allocated — its occupancy bit is clear.</summary>
    Free,

    /// <summary>One of the reserved root / header pages (page index &lt; 4).</summary>
    Root,

    /// <summary>A page of the occupancy-bitmap segment.</summary>
    Occupancy,

    /// <summary>A component-data page of a <c>ComponentTable</c>.</summary>
    Component,

    /// <summary>A component-revision (MVCC history) page.</summary>
    Revision,

    /// <summary>An index page (default / String64 / tail index).</summary>
    Index,

    /// <summary>An archetype cluster page.</summary>
    Cluster,

    /// <summary>A variable-sized-buffer segment page.</summary>
    Vsbs,

    /// <summary>A string-table segment page.</summary>
    StringTable,

    /// <summary>A spatial-index (R-Tree node / back-pointer / occupancy-hashmap) page.</summary>
    Spatial,

    /// <summary>An archetype entity-map (entity-id → cluster slot linear-hash) page.</summary>
    EntityMap,

    /// <summary>An engine-internal system page (UoW registry and other non-user structures).</summary>
    System,
}

/// <summary>Runtime role of a logical segment, as reported by <see cref="DatabaseEngine.EnumerateStorageSegments"/>.</summary>
[PublicAPI]
public enum StorageSegmentKind : byte
{
    /// <summary>Segment role not otherwise classified.</summary>
    Other = 0,

    /// <summary>Holds component instances.</summary>
    Component,

    /// <summary>Holds component-revision (MVCC history) records.</summary>
    Revision,

    /// <summary>Holds index entries (default / String64 / tail index).</summary>
    Index,

    /// <summary>Holds archetype cluster rows.</summary>
    Cluster,

    /// <summary>A variable-sized-buffer segment.</summary>
    Vsbs,

    /// <summary>A string-table segment.</summary>
    StringTable,

    /// <summary>The occupancy-bitmap segment.</summary>
    Occupancy,

    /// <summary>A spatial-index segment (R-Tree static/dynamic node, back-pointer, or occupancy hashmap).</summary>
    Spatial,

    /// <summary>An archetype entity-map segment (entity-id → cluster slot linear hash).</summary>
    EntityMap,

    /// <summary>A component-collection (per-stride <see cref="System.Collections.Generic.List{T}"/> backing) variable-sized-buffer segment.</summary>
    ComponentCollection,

    /// <summary>An engine-internal system segment (UoW registry and other non-user structures).</summary>
    System,
}

/// <summary>
/// Read-only description of one logical segment's on-disk footprint — its kind, the file pages it owns, and (for chunk-based segments) the chunk-layout
/// constants the Database File Map's L3/L4 decoders need.
/// Produced by <see cref="DatabaseEngine.EnumerateStorageSegments"/> for the Database File Map (Module 15).
/// </summary>
[PublicAPI]
public readonly struct StorageSegmentDescriptor
{
    /// <summary>Creates a descriptor for a segment. Chunk-layout and chunk-count fields are 0 for non-chunk-based segments.</summary>
    public StorageSegmentDescriptor(int rootPageIndex, StorageSegmentKind kind, ReadOnlyMemory<int> pages, int stride = 0, int chunkCountRootPage = 0, 
        int chunkCountPerPage = 0, int rootDataOffset = 0, int otherDataOffset = 0, int allocatedChunkCount = 0, int freeChunkCount = 0, int chunkCapacity = 0)
    {
        RootPageIndex = rootPageIndex;
        Kind = kind;
        Pages = pages;
        Stride = stride;
        ChunkCountRootPage = chunkCountRootPage;
        ChunkCountPerPage = chunkCountPerPage;
        RootDataOffset = rootDataOffset;
        OtherDataOffset = otherDataOffset;
        AllocatedChunkCount = allocatedChunkCount;
        FreeChunkCount = freeChunkCount;
        ChunkCapacity = chunkCapacity;
    }

    /// <summary>The segment's root file-page index — stable and unique per segment.</summary>
    public int RootPageIndex { get; }

    /// <summary>The segment's runtime role.</summary>
    public StorageSegmentKind Kind { get; }

    /// <summary>The file-page indices this segment owns, in directory order. Not necessarily contiguous on disk.</summary>
    public ReadOnlyMemory<int> Pages { get; }

    /// <summary>Chunk stride in bytes for a chunk-based segment; 0 when the segment is not chunk-based.</summary>
    public int Stride { get; }

    /// <summary>Chunk capacity of the segment's root page (the root page also holds the directory section).</summary>
    public int ChunkCountRootPage { get; }

    /// <summary>Chunk capacity of each non-root page.</summary>
    public int ChunkCountPerPage { get; }

    /// <summary>Byte offset within the root page where chunk 0 begins.</summary>
    public int RootDataOffset { get; }

    /// <summary>Byte offset within a non-root page where chunk 0 begins.</summary>
    public int OtherDataOffset { get; }

    /// <summary>Live count of currently-allocated chunks in a chunk-based segment; 0 when the segment is not chunk-based.</summary>
    public int AllocatedChunkCount { get; }

    /// <summary>Live count of free chunks in a chunk-based segment (<see cref="ChunkCapacity"/> − <see cref="AllocatedChunkCount"/>); 0 when not chunk-based.</summary>
    public int FreeChunkCount { get; }

    /// <summary>Total chunk capacity currently provisioned across the segment's pages; 0 when the segment is not chunk-based.</summary>
    public int ChunkCapacity { get; }

    /// <summary>Whether this segment stores fixed-size chunks (component / revision / index / VSBS / string-table).</summary>
    public bool IsChunkBased => Stride > 0;
}

/// <summary>
/// Diagnostic statistics for an archetype's entity-map (the entity-id → cluster-slot linear hash). Surfaced by the Workbench Database File Map's per-segment
/// harvest summary (Module 15, A6). Public projection of the engine-internal hash-map stats, computed by walking every bucket + overflow chain under an epoch
/// guard — a deliberately lazy, on-demand cost (never on the coarse / detail tile path). Best-effort: the walk can race with concurrent mutation, so a count may
/// be torn, but it never crashes (the epoch guard keeps freed chunks mapped for the duration).
/// </summary>
[PublicAPI]
internal readonly struct EntityMapStats
{
    /// <summary>Creates an entity-map stats snapshot.</summary>
    public EntityMapStats(int bucketCount, long entryCount, int overflowBucketCount, int maxChainLength, double loadFactor, int fillEmpty, int fillQuarter, 
        int fillHalf, int fillThreeQuarter, int fillFull)
    {
        BucketCount = bucketCount;
        EntryCount = entryCount;
        OverflowBucketCount = overflowBucketCount;
        MaxChainLength = maxChainLength;
        LoadFactor = loadFactor;
        FillEmpty = fillEmpty;
        FillQuarter = fillQuarter;
        FillHalf = fillHalf;
        FillThreeQuarter = fillThreeQuarter;
        FillFull = fillFull;
    }

    /// <summary>Number of primary buckets.</summary>
    public int BucketCount { get; }

    /// <summary>Total live entries across all buckets and overflow chains.</summary>
    public long EntryCount { get; }

    /// <summary>Primary buckets that have at least one overflow chunk (a hash-skew signal).</summary>
    public int OverflowBucketCount { get; }

    /// <summary>Longest bucket chain (1 = primary only, 2+ = has overflow).</summary>
    public int MaxChainLength { get; }

    /// <summary>Entries / (bucketCount × bucketCapacity) — the map's load factor.</summary>
    public double LoadFactor { get; }

    /// <summary>Primary buckets that are empty.</summary>
    public int FillEmpty { get; }

    /// <summary>Primary buckets 1–25% full.</summary>
    public int FillQuarter { get; }

    /// <summary>Primary buckets 26–50% full.</summary>
    public int FillHalf { get; }

    /// <summary>Primary buckets 51–75% full.</summary>
    public int FillThreeQuarter { get; }

    /// <summary>Primary buckets 76–100% full.</summary>
    public int FillFull { get; }
}
