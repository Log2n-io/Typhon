using JetBrains.Annotations;
using System;
using System.Collections.Generic;

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
/// Class of consistency violation surfaced by <see cref="DatabaseEngine.RunStorageIntegrityCheck"/>. Every value names an invariant that must hold across a
/// healthy engine — a non-zero issue list is a hard durability / structural bug, not a warning.
/// </summary>
[PublicAPI]
public enum StorageIntegrityIssueKind : byte
{
    /// <summary>
    /// Occupancy bitmap has a bit set for a file page that no segment claims and is not a reserved root/reserve slot. Sign of a lost write — pages allocated
    /// to a segment but the segment's persisted page-list (root-page Page Directory + extension map pages) didn't capture them durably. The on-disk bitmap
    /// survived; the segment's directory append did not.
    /// </summary>
    PopcountOrphan,

    /// <summary>
    /// A segment's <c>Pages</c> list references a file page whose occupancy bit is clear — the segment believes it owns a page that is actually free.
    /// Catastrophic; means a free could double-allocate that page.
    /// </summary>
    PopcountPhantom,

    /// <summary>
    /// <see cref="LogicalSegment{TStore}"/>'s forward header chain (via <c>LogicalSegmentNextRawDataPBID</c>) reaches a different page count than the
    /// persisted Page Directory enumerates. Forward-chain canon is on each page header; Page Directory canon is on the root page's raw-data section. If they
    /// disagree, one of the two writes lost durability during a Grow.
    /// </summary>
    ChainDirectoryMismatch,

    /// <summary>
    /// <see cref="ChunkBasedSegment{TStore}"/>'s computed capacity (<c>ChunkCapacity</c>) ≠ <c>AllocatedChunkCount + FreeChunkCount</c>. The segment's chunk
    /// free-list desynced from the chunk-occupancy bitmaps on its pages.
    /// </summary>
    ChunkSegmentCapacity,
}

/// <summary>
/// One concrete consistency violation found by <see cref="DatabaseEngine.RunStorageIntegrityCheck"/>. The combination (<see cref="Kind"/>,
/// <see cref="SegmentRootPageIndex"/>) localises the bug; <see cref="Detail"/> carries the human-readable summary; the integer fields give the exact counts
/// so the caller can produce structured assertions.
/// </summary>
/// <param name="Kind">Class of violation.</param>
/// <param name="SegmentRootPageIndex">
/// Root page of the implicated segment, or <c>0</c> for whole-DB issues (e.g. popcount mismatch with no specific owner).
/// </param>
/// <param name="FirstPageIndex">First file page of the implicated range, or <c>-1</c> when not applicable.</param>
/// <param name="PageCount">Number of contiguous pages in the range, or <c>0</c> when not applicable.</param>
/// <param name="Detail">Free-form forensic detail. Safe to log.</param>
[PublicAPI]
public readonly record struct StorageIntegrityIssue(StorageIntegrityIssueKind Kind, int SegmentRootPageIndex, int FirstPageIndex, int PageCount, string Detail);

/// <summary>
/// Whole-engine integrity audit produced by <see cref="DatabaseEngine.RunStorageIntegrityCheck"/>. <see cref="IsHealthy"/> is the only assertion callers
/// should care about — every individual issue is reported with enough context to localise the cause without re-running the audit.
/// </summary>
/// <remarks>
/// The audit reads only in-memory structures (occupancy bitmap, segment registry, page headers via the page cache). It is safe to call at any time and
/// incurs no data-page I/O beyond touching the segment's chain headers.
/// </remarks>
[PublicAPI]
public sealed class StorageIntegrityReport
{
    /// <summary>Every issue found in this audit pass, in discovery order. Empty when the engine is healthy.</summary>
    public IReadOnlyList<StorageIntegrityIssue> Issues { get; init; } = Array.Empty<StorageIntegrityIssue>();

    /// <summary>Count of file pages whose occupancy bit is set but no segment claims them. <c>0</c> in a healthy engine.</summary>
    public int OrphanPageCount { get; init; }

    /// <summary>Count of file pages a segment claims to own but whose occupancy bit is clear. <c>0</c> in a healthy engine.</summary>
    public int PhantomPageCount { get; init; }

    /// <summary>Total set bits in the occupancy bitmap when the audit ran. Useful for sizing diagnostics.</summary>
    public int OccupancyBitsSet { get; init; }

    /// <summary>Sum of every segment's <c>Pages.Length</c> over the registered-segments registry.</summary>
    public int SegmentClaimedPages { get; init; }

    /// <summary><c>true</c> when <see cref="Issues"/> is empty.</summary>
    public bool IsHealthy => Issues.Count == 0;
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
