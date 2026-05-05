using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Data:Index:BTree:Search instant. Payload: <c>retryReason u8, restartCount u8</c> (2 B).</summary>
[PublicAPI]
public readonly struct DataIndexBTreeSearchData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte RetryReason { get; }
    public byte RestartCount { get; }
    public DataIndexBTreeSearchData(byte threadSlot, long timestamp, byte retryReason, byte restartCount)
    { ThreadSlot = threadSlot; Timestamp = timestamp; RetryReason = retryReason; RestartCount = restartCount; }
}

/// <summary>Decoded Data:Index:BTree:RangeScan span. Payload: <c>resultCount i32, restartCount u8</c> (5 B).</summary>
[PublicAPI]
public readonly struct DataIndexBTreeRangeScanData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    /// <summary>#302 source-location id (0 = no attribution). Resolved through the trace manifest to file:line.</summary>
    public ushort SourceLocationId { get; }
    public int ResultCount { get; }
    public byte RestartCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataIndexBTreeRangeScanData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, int resultCount, byte restartCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; SourceLocationId = sourceLocationId; ResultCount = resultCount; RestartCount = restartCount; }
}

/// <summary>Decoded Data:Index:BTree:RangeScan:Revalidate instant. Payload: <c>restartCount u8</c> (1 B).</summary>
[PublicAPI]
public readonly struct DataIndexBTreeRangeScanRevalidateData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte RestartCount { get; }
    public DataIndexBTreeRangeScanRevalidateData(byte threadSlot, long timestamp, byte restartCount)
    { ThreadSlot = threadSlot; Timestamp = timestamp; RestartCount = restartCount; }
}

/// <summary>Decoded Data:Index:BTree:RebalanceFallback instant. Payload: <c>reason u8</c> (1 B).</summary>
[PublicAPI]
public readonly struct DataIndexBTreeRebalanceFallbackData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte Reason { get; }
    public DataIndexBTreeRebalanceFallbackData(byte threadSlot, long timestamp, byte reason)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Reason = reason; }
}

/// <summary>Decoded Data:Index:BTree:BulkInsert span. Payload: <c>bufferId i32, entryCount i32</c> (8 B).</summary>
[PublicAPI]
public readonly struct DataIndexBTreeBulkInsertData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    /// <summary>#302 source-location id (0 = no attribution). Resolved through the trace manifest to file:line.</summary>
    public ushort SourceLocationId { get; }
    public int BufferId { get; }
    public int EntryCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataIndexBTreeBulkInsertData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, int bufferId, int entryCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; SourceLocationId = sourceLocationId; BufferId = bufferId; EntryCount = entryCount; }
}

/// <summary>Decoded Data:Index:BTree:Root instant (op variant). Payload: <c>op u8, rootChunkId i32, height u8</c> (6 B).</summary>
[PublicAPI]
public readonly struct DataIndexBTreeRootData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    /// <summary>0 = Init, 1 = Split.</summary>
    public byte Op { get; }
    public int RootChunkId { get; }
    public byte Height { get; }
    public DataIndexBTreeRootData(byte threadSlot, long timestamp, byte op, int rootChunkId, byte height)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Op = op; RootChunkId = rootChunkId; Height = height; }
}

/// <summary>Decoded Data:Index:BTree:NodeCow instant. Payload: <c>srcChunkId i32, dstChunkId i32</c> (8 B).</summary>
[PublicAPI]
public readonly struct DataIndexBTreeNodeCowData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int SrcChunkId { get; }
    public int DstChunkId { get; }
    public DataIndexBTreeNodeCowData(byte threadSlot, long timestamp, int srcChunkId, int dstChunkId)
    { ThreadSlot = threadSlot; Timestamp = timestamp; SrcChunkId = srcChunkId; DstChunkId = dstChunkId; }
}

/// <summary>Wire codec for Data:Index:BTree events (kinds 180-186).</summary>
public static class DataIndexBTreeEventCodec
{
    public const int SearchSize             = TraceRecordHeader.CommonHeaderSize + 1 + 1;  // 14
    public const int RevalidateSize         = TraceRecordHeader.CommonHeaderSize + 1;       // 13
    public const int RebalanceFallbackSize  = TraceRecordHeader.CommonHeaderSize + 1;       // 13
    public const int RootSize               = TraceRecordHeader.CommonHeaderSize + 1 + 4 + 1;  // 18
    public const int NodeCowSize            = TraceRecordHeader.CommonHeaderSize + 4 + 4;  // 20

    private const int RangeScanPayload  = 4 + 1;
    private const int BulkInsertPayload = 4 + 4;
    public static int ComputeSizeRangeScan(bool hasTC)  => TraceRecordHeader.SpanHeaderSize(hasTC) + RangeScanPayload;
    public static int ComputeSizeBulkInsert(bool hasTC) => TraceRecordHeader.SpanHeaderSize(hasTC) + BulkInsertPayload;

    // ── Instants ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSearch(Span<byte> destination, byte threadSlot, long timestamp, byte retryReason, byte restartCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, SearchSize, TraceEventKind.DataIndexBTreeSearch, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = retryReason;
        p[1] = restartCount;
    }

    public static DataIndexBTreeSearchData DecodeSearch(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DataIndexBTreeSearchData(threadSlot, timestamp, p[0], p[1]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRevalidate(Span<byte> destination, byte threadSlot, long timestamp, byte restartCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, RevalidateSize, TraceEventKind.DataIndexBTreeRangeScanRevalidate, threadSlot, timestamp);
        destination[TraceRecordHeader.CommonHeaderSize] = restartCount;
    }

    public static DataIndexBTreeRangeScanRevalidateData DecodeRevalidate(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        return new DataIndexBTreeRangeScanRevalidateData(threadSlot, timestamp, source[TraceRecordHeader.CommonHeaderSize]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRebalanceFallback(Span<byte> destination, byte threadSlot, long timestamp, byte reason)
    {
        TraceRecordHeader.WriteCommonHeader(destination, RebalanceFallbackSize, TraceEventKind.DataIndexBTreeRebalanceFallback, threadSlot, timestamp);
        destination[TraceRecordHeader.CommonHeaderSize] = reason;
    }

    public static DataIndexBTreeRebalanceFallbackData DecodeRebalanceFallback(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        return new DataIndexBTreeRebalanceFallbackData(threadSlot, timestamp, source[TraceRecordHeader.CommonHeaderSize]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRoot(Span<byte> destination, byte threadSlot, long timestamp, byte op, int rootChunkId, byte height)
    {
        TraceRecordHeader.WriteCommonHeader(destination, RootSize, TraceEventKind.DataIndexBTreeRoot, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = op;
        BinaryPrimitives.WriteInt32LittleEndian(p[1..], rootChunkId);
        p[5] = height;
    }

    public static DataIndexBTreeRootData DecodeRoot(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DataIndexBTreeRootData(threadSlot, timestamp, p[0],
            BinaryPrimitives.ReadInt32LittleEndian(p[1..]),
            p[5]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteNodeCow(Span<byte> destination, byte threadSlot, long timestamp, int srcChunkId, int dstChunkId)
    {
        TraceRecordHeader.WriteCommonHeader(destination, NodeCowSize, TraceEventKind.DataIndexBTreeNodeCow, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, srcChunkId);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], dstChunkId);
    }

    public static DataIndexBTreeNodeCowData DecodeNodeCow(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DataIndexBTreeNodeCowData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }

    // ── Spans ──

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

    public static DataIndexBTreeRangeScanData DecodeRangeScan(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        ulong traceIdHi = 0, traceIdLo = 0;
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        if (hasTC)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        ushort sourceLocationId = 0;
        if (hasSL)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTC)..]);
        }
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new DataIndexBTreeRangeScanData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            BinaryPrimitives.ReadInt32LittleEndian(p), p[4]);
    }

    public static DataIndexBTreeBulkInsertData DecodeBulkInsert(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        ulong traceIdHi = 0, traceIdLo = 0;
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        if (hasTC)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        ushort sourceLocationId = 0;
        if (hasSL)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTC)..]);
        }
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new DataIndexBTreeBulkInsertData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }
}
