using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded RTree Insert span. Payload: <c>entityId i64, depth u8, didSplit u8, restartCount u8</c> (11 B).</summary>
[PublicAPI]
public readonly struct SpatialRTreeInsertData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public long EntityId { get; }
    public byte Depth { get; }
    public byte DidSplit { get; }
    public byte RestartCount { get; }

    public SpatialRTreeInsertData(byte threadSlot, long startTimestamp, long durationTicks, long entityId, byte depth, byte didSplit, byte restartCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; EntityId = entityId; Depth = depth; DidSplit = didSplit; RestartCount = restartCount; }
}

/// <summary>Decoded RTree Remove span. Payload: <c>entityId i64, leafCollapse u8</c> (9 B).</summary>
[PublicAPI]
public readonly struct SpatialRTreeRemoveData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public long EntityId { get; }
    public byte LeafCollapse { get; }

    public SpatialRTreeRemoveData(byte threadSlot, long startTimestamp, long durationTicks, long entityId, byte leafCollapse)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; EntityId = entityId; LeafCollapse = leafCollapse; }
}

/// <summary>Decoded RTree NodeSplit span. Payload: <c>depth u8, splitAxis u8, leftCount u8, rightCount u8</c> (4 B).</summary>
[PublicAPI]
public readonly struct SpatialRTreeNodeSplitData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public byte Depth { get; }
    public byte SplitAxis { get; }
    public byte LeftCount { get; }
    public byte RightCount { get; }

    public SpatialRTreeNodeSplitData(byte threadSlot, long startTimestamp, long durationTicks, byte depth, byte splitAxis, byte leftCount, byte rightCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; Depth = depth; SplitAxis = splitAxis; LeftCount = leftCount; RightCount = rightCount; }
}

/// <summary>Decoded RTree BulkLoad span. Payload: <c>entityCount i32, leafCount i32</c> (8 B).</summary>
[PublicAPI]
public readonly struct SpatialRTreeBulkLoadData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public int EntityCount { get; }
    public int LeafCount { get; }

    public SpatialRTreeBulkLoadData(byte threadSlot, long startTimestamp, long durationTicks, int entityCount, int leafCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; EntityCount = entityCount; LeafCount = leafCount; }
}

/// <summary>
/// Wire codec for RTree structural span records (kinds 123-126). Spans: 12-byte common header + 25-byte span extension + payload.
/// </summary>
public static class SpatialRTreeEventCodec
{
    private const int InsertPayload = 8 + 1 + 1 + 1;   // 11
    private const int RemovePayload = 8 + 1;            // 9
    private const int NodeSplitPayload = 4;             // 4
    private const int BulkLoadPayload = 4 + 4;          // 8

    public static int ComputeSizeInsert(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + InsertPayload;
    public static int ComputeSizeRemove(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + RemovePayload;
    public static int ComputeSizeNodeSplit(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + NodeSplitPayload;
    public static int ComputeSizeBulkLoad(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + BulkLoadPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSpanPreamble(Span<byte> destination, TraceEventKind kind, ushort size, byte threadSlot, long startTimestamp,
        long durationTicks, ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, bool hasTraceContext)
    {
        TraceRecordHeader.WriteCommonHeader(destination, size, kind, threadSlot, startTimestamp);
        var spanFlags = hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks, spanId, parentSpanId, spanFlags);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
    }

    public static SpatialRTreeInsertData DecodeInsert(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SpatialRTreeInsertData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadInt64LittleEndian(payload),
            payload[8], payload[9], payload[10]);
    }

    public static SpatialRTreeRemoveData DecodeRemove(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SpatialRTreeRemoveData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadInt64LittleEndian(payload), payload[8]);
    }

    public static SpatialRTreeNodeSplitData DecodeNodeSplit(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SpatialRTreeNodeSplitData(threadSlot, startTimestamp, durationTicks, payload[0], payload[1], payload[2], payload[3]);
    }

    public static SpatialRTreeBulkLoadData DecodeBulkLoad(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SpatialRTreeBulkLoadData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadInt32LittleEndian(payload),
            BinaryPrimitives.ReadInt32LittleEndian(payload[4..]));
    }
}
