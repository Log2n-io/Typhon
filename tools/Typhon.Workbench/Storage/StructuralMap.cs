using Typhon.Engine;

namespace Typhon.Workbench.Storage;

/// <summary>
/// The origin-agnostic coarse storage map (Module 15, §5.4) — region headers, the coarse descriptors (type +
/// owning segment), and the segment table. In Track A the sole producer is live-engine introspection
/// (<see cref="StorageMapService"/>). The descriptor arrays are in <em>cell</em> space — one entry per
/// <see cref="DownSampleFactor"/> pages (§5.5); the factor is 1 (one cell == one page) below the cell budget.
/// </summary>
internal sealed class StructuralMap
{
    public required string DatabaseName { get; init; }

    public required long DataFileBytes { get; init; }

    public required int DataFilePageCount { get; init; }

    public required long WalBytes { get; init; }

    /// <summary>Hilbert grid order <c>n</c> — the cell grid is <c>2^n × 2^n</c> with <c>4^n ≥ cell count</c>.</summary>
    public required int HilbertOrder { get; init; }

    public required long CheckpointLsn { get; init; }

    /// <summary>
    /// Coarse down-sample factor (§5.5) — pages per descriptor cell, a power of 4. 1 when the map is exact;
    /// larger once <see cref="DataFilePageCount"/> exceeds the cell budget, keeping the coarse arrays bounded.
    /// </summary>
    public required int DownSampleFactor { get; init; }

    /// <summary>Semantic page type per <em>cell</em> (the dominant non-free type when down-sampled).</summary>
    public required StoragePageType[] PageType { get; init; }

    /// <summary>Dense 16-bit owning-segment id per <em>cell</em> (<see cref="NoSegment"/> when unowned).</summary>
    public required ushort[] OwnerSegmentId { get; init; }

    public required StorageSegmentInfo[] Segments { get; init; }

    /// <summary>Number of descriptor cells — <c>ceil(DataFilePageCount / DownSampleFactor)</c>.</summary>
    public int CellCount => PageType.Length;

    /// <summary>Sentinel <see cref="OwnerSegmentId"/> value for a cell owned by no enumerated segment.</summary>
    public const ushort NoSegment = 0xFFFF;
}

/// <summary>
/// One logical segment's footprint in the <see cref="StructuralMap"/> — coarse identity plus, for chunk-based
/// segments, the layout constants the A2 detail tier and L4 decoders need (0 for non-chunk-based segments).
/// </summary>
internal readonly record struct StorageSegmentInfo(
    int Id,
    int RootPageIndex,
    StorageSegmentKind Kind,
    int PageCount,
    int Stride,
    int ChunkCountRootPage,
    int ChunkCountPerPage,
    int RootDataOffset,
    int OtherDataOffset)
{
    /// <summary>Whether this segment stores fixed-size chunks (has an L3 chunk grid).</summary>
    public bool IsChunkBased => Stride > 0;

    /// <summary>Chunk capacity of the given segment-relative page index.</summary>
    public int ChunkCountOfPage(int segmentPageIndex) => segmentPageIndex == 0 ? ChunkCountRootPage : ChunkCountPerPage;

    /// <summary>Byte offset within the given segment-relative page where chunk 0 begins.</summary>
    public int DataOffsetOfPage(int segmentPageIndex) => segmentPageIndex == 0 ? RootDataOffset : OtherDataOffset;
}
