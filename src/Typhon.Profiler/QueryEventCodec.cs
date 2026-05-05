using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

// ── Query span data structs ─────────────────────────────────────────────────

[PublicAPI]
public readonly struct QueryParseData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public ushort PredicateCount { get; }
    public byte BranchCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryParseData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, ushort pc, byte bc)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; PredicateCount = pc; BranchCount = bc; }
}

[PublicAPI]
public readonly struct QueryParseDnfData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public ushort InBranches { get; }
    public ushort OutBranches { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryParseDnfData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, ushort ib, ushort ob)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; InBranches = ib; OutBranches = ob; }
}

[PublicAPI]
public readonly struct QueryPlanData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public byte EvaluatorCount { get; }
    public ushort IndexFieldIdx { get; }
    public long RangeMin { get; }
    public long RangeMax { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryPlanData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, byte ec, ushort ifi, long rmin, long rmax)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; EvaluatorCount = ec; IndexFieldIdx = ifi; RangeMin = rmin; RangeMax = rmax; }
}

[PublicAPI]
public readonly struct QueryEstimateData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public ushort FieldIdx { get; }
    public long Cardinality { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryEstimateData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, ushort fi, long card)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; FieldIdx = fi; Cardinality = card; }
}

[PublicAPI]
public readonly struct QueryPlanPrimarySelectData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte Candidates { get; }
    public byte WinnerIdx { get; }
    public byte Reason { get; }
    public QueryPlanPrimarySelectData(byte ts, long t, byte c, byte w, byte r)
    { ThreadSlot = ts; Timestamp = t; Candidates = c; WinnerIdx = w; Reason = r; }
}

[PublicAPI]
public readonly struct QueryPlanSortData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public byte EvaluatorCount { get; }
    public uint SortNs { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryPlanSortData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, byte ec, uint ns)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; EvaluatorCount = ec; SortNs = ns; }
}

[PublicAPI]
public readonly struct QueryExecuteIndexScanData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public ushort PrimaryFieldIdx { get; }
    public byte Mode { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryExecuteIndexScanData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, ushort pfi, byte m)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; PrimaryFieldIdx = pfi; Mode = m; }
}

[PublicAPI]
public readonly struct QueryExecuteIterateData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int ChunkCount { get; }
    public int EntryCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryExecuteIterateData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int cc, int ec)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ChunkCount = cc; EntryCount = ec; }
}

[PublicAPI]
public readonly struct QueryExecuteFilterData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public byte FilterCount { get; }
    public int RejectedCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryExecuteFilterData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, byte fc, int rc)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; FilterCount = fc; RejectedCount = rc; }
}

[PublicAPI]
public readonly struct QueryExecutePaginationData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int Skip { get; }
    public int Take { get; }
    public byte EarlyTerm { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryExecutePaginationData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int sk, int tk, byte et)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; Skip = sk; Take = tk; EarlyTerm = et; }
}

[PublicAPI]
public readonly struct QueryExecuteStorageModeData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte Mode { get; }
    public QueryExecuteStorageModeData(byte ts, long t, byte m)
    { ThreadSlot = ts; Timestamp = t; Mode = m; }
}

[PublicAPI]
public readonly struct QueryCountData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int ResultCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public QueryCountData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int rc)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; ResultCount = rc; }
}

// ── Codec ───────────────────────────────────────────────────────────────────

public static class QueryEventCodec
{
    public const int PrimarySelectSize    = TraceRecordHeader.CommonHeaderSize + 1 + 1 + 1;       // 15
    public const int StorageModeSize      = TraceRecordHeader.CommonHeaderSize + 1;               // 13

    private const int ParsePayload         = 2 + 1;                  // 3
    private const int ParseDnfPayload      = 2 + 2;                  // 4
    private const int PlanPayload          = 1 + 2 + 8 + 8;          // 19
    private const int EstimatePayload      = 2 + 8;                  // 10
    private const int PlanSortPayload      = 1 + 4;                  // 5
    private const int IndexScanPayload     = 2 + 1;                  // 3
    private const int IteratePayload       = 4 + 4;                  // 8
    private const int FilterPayload        = 1 + 4;                  // 5
    private const int PaginationPayload    = 4 + 4 + 1;              // 9
    private const int CountPayload         = 4;                      // 4

    public static int ComputeSizeParse(bool hasTC)         => TraceRecordHeader.SpanHeaderSize(hasTC) + ParsePayload;
    public static int ComputeSizeParseDnf(bool hasTC)      => TraceRecordHeader.SpanHeaderSize(hasTC) + ParseDnfPayload;
    public static int ComputeSizePlan(bool hasTC)          => TraceRecordHeader.SpanHeaderSize(hasTC) + PlanPayload;
    public static int ComputeSizeEstimate(bool hasTC)      => TraceRecordHeader.SpanHeaderSize(hasTC) + EstimatePayload;
    public static int ComputeSizePlanSort(bool hasTC)      => TraceRecordHeader.SpanHeaderSize(hasTC) + PlanSortPayload;
    public static int ComputeSizeIndexScan(bool hasTC)     => TraceRecordHeader.SpanHeaderSize(hasTC) + IndexScanPayload;
    public static int ComputeSizeIterate(bool hasTC)       => TraceRecordHeader.SpanHeaderSize(hasTC) + IteratePayload;
    public static int ComputeSizeFilter(bool hasTC)        => TraceRecordHeader.SpanHeaderSize(hasTC) + FilterPayload;
    public static int ComputeSizePagination(bool hasTC)    => TraceRecordHeader.SpanHeaderSize(hasTC) + PaginationPayload;
    public static int ComputeSizeCount(bool hasTC)         => TraceRecordHeader.SpanHeaderSize(hasTC) + CountPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSpanPreamble(Span<byte> destination, TraceEventKind kind, ushort size, byte threadSlot, long startTimestamp,
        long durationTicks, ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, bool hasTC)
    {
        TraceRecordHeader.WriteCommonHeader(destination, size, kind, threadSlot, startTimestamp);
        var spanFlags = hasTC ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks, spanId, parentSpanId, spanFlags);
        if (hasTC)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ReadSpanPreamble(ReadOnlySpan<byte> source,
        out byte threadSlot, out long startTs, out long dur, out ulong spanId, out ulong parentSpanId,
        out ulong traceIdHi, out ulong traceIdLo)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out threadSlot, out startTs);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out dur, out spanId, out parentSpanId, out var spanFlags);
        traceIdHi = 0; traceIdLo = 0;
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        if (hasTC)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        // Source-location bytes (#302) are skipped here — Phase 4 will surface siteId in this codec.
        return source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
    }

    // ── Parse ──
    public static QueryParseData DecodeParse(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryParseData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt16LittleEndian(p), p[2]);
    }

    // ── Parse:DNF ──
    public static QueryParseDnfData DecodeParseDnf(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryParseDnfData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]));
    }

    // ── Plan ──
    public static QueryPlanData DecodePlan(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryPlanData(ts, sts, dur, sid, psid, thi, tlo,
            p[0],
            BinaryPrimitives.ReadUInt16LittleEndian(p[1..]),
            BinaryPrimitives.ReadInt64LittleEndian(p[3..]),
            BinaryPrimitives.ReadInt64LittleEndian(p[11..]));
    }

    // ── Estimate ──
    public static QueryEstimateData DecodeEstimate(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryEstimateData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt64LittleEndian(p[2..]));
    }

    // ── PrimarySelect (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WritePrimarySelect(Span<byte> destination, byte threadSlot, long timestamp, byte candidates, byte winnerIdx, byte reason)
    {
        TraceRecordHeader.WriteCommonHeader(destination, PrimarySelectSize, TraceEventKind.QueryPlanPrimarySelect, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = candidates;
        p[1] = winnerIdx;
        p[2] = reason;
    }

    public static QueryPlanPrimarySelectData DecodePrimarySelect(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new QueryPlanPrimarySelectData(threadSlot, timestamp, p[0], p[1], p[2]);
    }

    // ── PlanSort ──
    public static QueryPlanSortData DecodePlanSort(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryPlanSortData(ts, sts, dur, sid, psid, thi, tlo,
            p[0], BinaryPrimitives.ReadUInt32LittleEndian(p[1..]));
    }

    // ── IndexScan ──
    public static QueryExecuteIndexScanData DecodeIndexScan(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryExecuteIndexScanData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt16LittleEndian(p), p[2]);
    }

    // ── Iterate ──
    public static QueryExecuteIterateData DecodeIterate(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryExecuteIterateData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }

    // ── Filter ──
    public static QueryExecuteFilterData DecodeFilter(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryExecuteFilterData(ts, sts, dur, sid, psid, thi, tlo,
            p[0], BinaryPrimitives.ReadInt32LittleEndian(p[1..]));
    }

    // ── Pagination ──
    public static QueryExecutePaginationData DecodePagination(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryExecutePaginationData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            p[8]);
    }

    // ── StorageMode (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteStorageMode(Span<byte> destination, byte threadSlot, long timestamp, byte mode)
    {
        TraceRecordHeader.WriteCommonHeader(destination, StorageModeSize, TraceEventKind.QueryExecuteStorageMode, threadSlot, timestamp);
        destination[TraceRecordHeader.CommonHeaderSize] = mode;
    }

    public static QueryExecuteStorageModeData DecodeStorageMode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        return new QueryExecuteStorageModeData(threadSlot, timestamp, source[TraceRecordHeader.CommonHeaderSize]);
    }

    // ── Count ──
    public static QueryCountData DecodeCount(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new QueryCountData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p));
    }
}
