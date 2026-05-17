namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// CPU-sample frame-symbol manifest for a profiler session (#351 Phase 4). Exposed once per session — like the #302
/// source-location manifest — so the Call Tree panel can resolve a tree node's <c>frameId</c> to a method name and a
/// <c>file:line</c> for go-to-source. <see cref="Categories"/> is the id↔name table for the per-scope category breakdown.
/// </summary>
public sealed record CpuFrameManifestDto(CpuFrameSymbolDto[] Frames, CpuCategoryDto[] Categories)
{
    /// <summary>Returned for traces that carry no CPU-sample section.</summary>
    public static CpuFrameManifestDto Empty { get; } = new([], []);
}

/// <summary>One resolved CPU-sample frame symbol. <c>Line</c> is 0 when the frame has no source (BCL / native).</summary>
public sealed record CpuFrameSymbolDto(int FrameId, string Method, string File, int Line, int CategoryId);

/// <summary>One engine/host category in the §8.6 "Subsystems" breakdown — e.g. <c>Ecs</c>, <c>Storage</c>, <c>BCL</c>.</summary>
public sealed record CpuCategoryDto(int Id, string Name);

/// <summary>
/// Scope for a <c>POST .../profiler/calltree</c> request (#351 Phase 4 + Phase 5). The scope resolves server-side to a
/// set of time-windows; exactly one scope axis applies, in this precedence: <see cref="SpanKind"/> ▸ <see cref="SystemIndex"/>
/// ▸ <see cref="Phase"/> ▸ the manual <see cref="StartUs"/>/<see cref="EndUs"/> range ▸ whole session (all null). A clicked
/// span instance is just the range scope (a single window). <see cref="ViewMode"/> is <c>on-cpu</c> (Managed samples only)
/// or <c>wall-clock</c> (all samples). <see cref="FrameRoot"/>, when set, re-roots the folded tree at that frame
/// (drill-down — §8.2) and composes with any scope.
/// </summary>
public sealed record CallTreeRequestDto(
    double? StartUs,
    double? EndUs,
    int? FrameRoot,
    string ViewMode,
    int? SystemIndex = null,
    string Phase = null,
    int? SpanKind = null);

/// <summary>
/// One node of the folded call tree. <see cref="Children"/> holds <i>indices</i> into the flat
/// <see cref="CallTreeResponseDto.Nodes"/> array — the tree is not nested on the wire. A real CPU call stack can be
/// hundreds of frames deep; a nested-object tree of that depth blows past System.Text.Json's <c>MaxDepth</c>, so the
/// depth lives in index links instead. The synthetic root carries <c>FrameId == -1</c>.
/// </summary>
public sealed record CallTreeNodeDto(int FrameId, long SelfSamples, long TotalSamples, int[] Children);

/// <summary>Self-time sample count attributed to one category in the per-scope breakdown.</summary>
public sealed record CategorySliceDto(int CategoryId, long SelfSamples);

/// <summary>
/// Folded call tree for one scope (#351 Phase 4), in a flat depth-independent form. <see cref="Nodes"/> is the node
/// array — <c>Nodes[0]</c> is always the synthetic root; every node's <c>Children</c> are indices into this array.
/// <see cref="TotalSamples"/> is the sample count in scope; <see cref="ManagedSamples"/> / <see cref="ExternalSamples"/>
/// split it by <c>SampleType</c>; <see cref="CategoryBreakdown"/> is the §8.6 self-time-per-category aggregation.
/// </summary>
public sealed record CallTreeResponseDto(
    CallTreeNodeDto[] Nodes,
    long TotalSamples,
    long ManagedSamples,
    long ExternalSamples,
    CategorySliceDto[] CategoryBreakdown)
{
    /// <summary>Returned when the trace carries no CPU samples (or none fall in scope) — a lone synthetic root.</summary>
    public static CallTreeResponseDto Empty { get; } = new([new CallTreeNodeDto(-1, 0, 0, [])], 0, 0, 0, []);
}

/// <summary>
/// Body of a <c>POST .../profiler/sample-density</c> request (#351 Phase 5, §8.2). <see cref="Scope"/> is the same composite
/// scope a <c>calltree</c> request carries — its <see cref="CallTreeRequestDto.FrameRoot"/> selects the root frame whose
/// sample density is binned, and its <see cref="CallTreeRequestDto.ViewMode"/> filters the sample set. <see cref="BinCount"/>
/// is the number of time-bins (null ⇒ default 64).
/// </summary>
public sealed record SampleDensityRequestDto(CallTreeRequestDto Scope, int? BinCount);

/// <summary>One time-bin of the sample-density sparkline — <see cref="Count"/> in-scope samples starting at <see cref="StartUs"/>.</summary>
public sealed record SampleDensityBinDto(double StartUs, long Count);

/// <summary>
/// Sample-density-over-time for a scope (#351 Phase 5, §8.2) — the non-stationarity sparkline. In-scope samples are binned
/// linearly across <see cref="BinCount"/> equal-width bins spanning the in-scope sample span. A flat profile means the scope
/// is stationary; spikes mean behavioral blending (warm-up vs steady-state averaged together).
/// </summary>
public sealed record SampleDensityDto(double StartUs, double BinWidthUs, SampleDensityBinDto[] Bins)
{
    /// <summary>Returned when the trace carries no CPU samples, or no sample falls in scope.</summary>
    public static SampleDensityDto Empty { get; } = new(0, 0, []);
}
