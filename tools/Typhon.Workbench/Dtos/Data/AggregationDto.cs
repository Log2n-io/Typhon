namespace Typhon.Workbench.Dtos.Data;

/// <summary>
/// One aggregation query. Tier 2 ops (#312) supply extra params via the optional fields:
/// <list type="bullet">
///   <item><c>histogram</c>: <see cref="Buckets"/> = number of equal-width buckets (must be &gt; 0).</item>
///   <item><c>topk</c>: <see cref="N"/> = number of top values to return.</item>
///   <item><c>cdf</c>: <see cref="Samples"/> = number of evenly-spaced quantile sample points.</item>
///   <item><c>criticalPathParticipationRate</c>: ignored — uses Range only; the field is "system/&lt;name&gt;" implied
///       by the trackId.</item>
/// </list>
/// </summary>
public record AggregationQueryDto(
    string TrackId,
    string Field,
    string Op,
    uint[] Range,         // [t0, t1] inclusive tick numbers
    int? Buckets = null,  // histogram
    int? N = null,        // topk
    int? Samples = null); // cdf

public record AggregationRequestDto(AggregationQueryDto[] Queries);

/// <summary>
/// Per-query result — only one of the typed payload fields is populated per result, matching the operator that produced it.
/// Scalar ops (mean / min / max / sum / count / stddev / variance / percentiles / criticalPathParticipationRate) populate
/// <see cref="Value"/>. Tier 2 shaped ops populate the matching payload (<see cref="Histogram"/> / <see cref="TopK"/> /
/// <see cref="Cdf"/>). All shape fields default to null so existing v1 consumers can keep reading <see cref="Value"/> only.
/// </summary>
public record AggregationResultDto(
    double? Value = null,                   // scalar ops
    HistogramBucketDto[] Histogram = null,  // histogram op
    TopKEntryDto[] TopK = null,             // topk op
    CdfSampleDto[] Cdf = null);             // cdf op

public record AggregationResponseDto(AggregationResultDto[] Results);

/// <summary>One bucket in a histogram result. Range is half-open [<see cref="BucketStart"/>, <see cref="BucketEnd"/>).</summary>
public record HistogramBucketDto(double BucketStart, double BucketEnd, int Count);

/// <summary>One entry in a topk result. <see cref="TickNumber"/> identifies the tick that produced the value.</summary>
public record TopKEntryDto(uint TickNumber, double Value);

/// <summary>One sample in a CDF result. <see cref="Quantile"/> ∈ [0,1]; <see cref="Value"/> is the corresponding observed value.</summary>
public record CdfSampleDto(double Quantile, double Value);
