using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

// ═══════════════════════════════════════════════════════════════════════════════════════
// Decoded data types for Spatial query spans (kinds 117-122)
// ═══════════════════════════════════════════════════════════════════════════════════════

/// <summary>Decoded form of a <see cref="TraceEventKind.SpatialQueryAabb"/> record. Payload: <c>nodesVisited u16, leavesEntered u16, resultCount u16, restartCount u8, categoryMask u32</c> (11 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryAabbData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ushort NodesVisited { get; }
    public ushort LeavesEntered { get; }
    public ushort ResultCount { get; }
    public byte RestartCount { get; }
    public uint CategoryMask { get; }

    public SpatialQueryAabbData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ushort nodesVisited, ushort leavesEntered, ushort resultCount, byte restartCount, uint categoryMask)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        NodesVisited = nodesVisited;
        LeavesEntered = leavesEntered;
        ResultCount = resultCount;
        RestartCount = restartCount;
        CategoryMask = categoryMask;
    }
}

/// <summary>Decoded form of a Radius query record. Payload: <c>nodesVisited u16, resultCount u16, radius f32, restartCount u8</c> (9 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryRadiusData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort NodesVisited { get; }
    public ushort ResultCount { get; }
    public float Radius { get; }
    public byte RestartCount { get; }

    public SpatialQueryRadiusData(byte threadSlot, long startTimestamp, long durationTicks, ushort nodesVisited, ushort resultCount, float radius, byte restartCount)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        NodesVisited = nodesVisited;
        ResultCount = resultCount;
        Radius = radius;
        RestartCount = restartCount;
    }
}

/// <summary>Decoded form of a Ray query record. Payload: <c>nodesVisited u16, resultCount u16, maxDist f32, restartCount u8</c> (9 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryRayData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort NodesVisited { get; }
    public ushort ResultCount { get; }
    public float MaxDist { get; }
    public byte RestartCount { get; }

    public SpatialQueryRayData(byte threadSlot, long startTimestamp, long durationTicks, ushort nodesVisited, ushort resultCount, float maxDist, byte restartCount)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        NodesVisited = nodesVisited;
        ResultCount = resultCount;
        MaxDist = maxDist;
        RestartCount = restartCount;
    }
}

/// <summary>Decoded form of a Frustum query record. Payload: <c>nodesVisited u16, resultCount u16, planeCount u8, restartCount u8</c> (6 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryFrustumData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort NodesVisited { get; }
    public ushort ResultCount { get; }
    public byte PlaneCount { get; }
    public byte RestartCount { get; }

    public SpatialQueryFrustumData(byte threadSlot, long startTimestamp, long durationTicks, ushort nodesVisited, ushort resultCount, byte planeCount, byte restartCount)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        NodesVisited = nodesVisited;
        ResultCount = resultCount;
        PlaneCount = planeCount;
        RestartCount = restartCount;
    }
}

/// <summary>Decoded form of a KNN query record. Payload: <c>k u16, iterCount u8, finalRadius f32, resultCount u16</c> (9 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryKnnData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort K { get; }
    public byte IterCount { get; }
    public float FinalRadius { get; }
    public ushort ResultCount { get; }

    public SpatialQueryKnnData(byte threadSlot, long startTimestamp, long durationTicks, ushort k, byte iterCount, float finalRadius, ushort resultCount)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        K = k;
        IterCount = iterCount;
        FinalRadius = finalRadius;
        ResultCount = resultCount;
    }
}

/// <summary>Decoded form of a Count query record. Payload: <c>variant u8 (0=AABB,1=Radius), nodesVisited u16, resultCount i32</c> (7 B).</summary>
[PublicAPI]
public readonly struct SpatialQueryCountData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public byte Variant { get; }
    public ushort NodesVisited { get; }
    public int ResultCount { get; }

    public SpatialQueryCountData(byte threadSlot, long startTimestamp, long durationTicks, byte variant, ushort nodesVisited, int resultCount)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        Variant = variant;
        NodesVisited = nodesVisited;
        ResultCount = resultCount;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════════════
// Wire codec
// ═══════════════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Wire codec for Spatial query span records (kinds 117-122). All spans: 12-byte common header + 25-byte span extension + payload.
/// </summary>
public static class SpatialQueryEventCodec
{
    private const int AabbPayloadSize = 2 + 2 + 2 + 1 + 4;     // 11
    private const int RadiusPayloadSize = 2 + 2 + 4 + 1;       // 9
    private const int RayPayloadSize = 2 + 2 + 4 + 1;          // 9
    private const int FrustumPayloadSize = 2 + 2 + 1 + 1;      // 6
    private const int KnnPayloadSize = 2 + 1 + 4 + 2;          // 9
    private const int CountPayloadSize = 1 + 2 + 4;            // 7

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeAabb(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + AabbPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeRadius(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + RadiusPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeRay(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + RayPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeFrustum(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + FrustumPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeKnn(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + KnnPayloadSize;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSizeCount(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + CountPayloadSize;

    public static SpatialQueryAabbData DecodeAabb(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryAabbData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]),
            payload[6],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[7..]));
    }

    public static SpatialQueryRadiusData DecodeRadius(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryRadiusData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[4..]),
            payload[8]);
    }

    public static SpatialQueryRayData DecodeRay(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryRayData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            BinaryPrimitives.ReadSingleLittleEndian(payload[4..]),
            payload[8]);
    }

    public static SpatialQueryFrustumData DecodeFrustum(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryFrustumData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]),
            payload[4], payload[5]);
    }

    public static SpatialQueryKnnData DecodeKnn(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryKnnData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(payload),
            payload[2],
            BinaryPrimitives.ReadSingleLittleEndian(payload[3..]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[7..]));
    }

    public static SpatialQueryCountData DecodeCount(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var payload = source[TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation)..];
        return new SpatialQueryCountData(threadSlot, startTimestamp, durationTicks,
            payload[0],
            BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]),
            BinaryPrimitives.ReadInt32LittleEndian(payload[3..]));
    }
}
