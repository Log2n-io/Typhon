using Typhon.Engine;

namespace Typhon.Workbench.Storage;

/// <summary>
/// The origin-agnostic coarse storage map (Module 15, §5.4) — region headers, the per-page coarse descriptors
/// (type + owning segment), and the segment table. In Track A the sole producer is live-engine introspection
/// (<see cref="StorageMapService"/>); the down-sample factor is always 1.
/// </summary>
internal sealed class StructuralMap
{
    public required string DatabaseName { get; init; }

    public required long DataFileBytes { get; init; }

    public required int DataFilePageCount { get; init; }

    public required long WalBytes { get; init; }

    /// <summary>Hilbert grid order <c>n</c> — the page grid is <c>2^n × 2^n</c> with <c>4^n ≥ page count</c>.</summary>
    public required int HilbertOrder { get; init; }

    public required long CheckpointLsn { get; init; }

    /// <summary>Coarse down-sample factor (§5.5). Always 1 in A1 — present so the wire shape is stable.</summary>
    public required int DownSampleFactor { get; init; }

    /// <summary>Semantic page type per file page, in page-index order.</summary>
    public required StoragePageType[] PageType { get; init; }

    /// <summary>Dense 16-bit owning-segment id per file page (<see cref="NoSegment"/> when unowned).</summary>
    public required ushort[] OwnerSegmentId { get; init; }

    public required StorageSegmentInfo[] Segments { get; init; }

    /// <summary>Sentinel <see cref="OwnerSegmentId"/> value for a page owned by no enumerated segment.</summary>
    public const ushort NoSegment = 0xFFFF;
}

/// <summary>One logical segment's coarse footprint in the <see cref="StructuralMap"/>.</summary>
internal readonly record struct StorageSegmentInfo(int Id, int RootPageIndex, StorageSegmentKind Kind, int PageCount);
