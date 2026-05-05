using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Storage:PageCache:DirtyWalk span. Payload: <c>rangeStart i32, rangeLen i32, dirtyMs i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct StoragePageCacheDirtyWalkData
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
    public int RangeStart { get; }
    public int RangeLen { get; }
    public int DirtyMs { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public StoragePageCacheDirtyWalkData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, int rangeStart, int rangeLen, int dirtyMs)
    {
        ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks;
        SpanId = spanId; ParentSpanId = parentSpanId; TraceIdHi = traceIdHi; TraceIdLo = traceIdLo;
        SourceLocationId = sourceLocationId;
        RangeStart = rangeStart; RangeLen = rangeLen; DirtyMs = dirtyMs;
    }
}

/// <summary>Decoded Storage:ChunkSegment:Grow instant. Payload: <c>stride i32, oldCap i32, newCap i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct StorageChunkSegmentGrowData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int Stride { get; }
    public int OldCap { get; }
    public int NewCap { get; }
    public StorageChunkSegmentGrowData(byte threadSlot, long timestamp, int stride, int oldCap, int newCap)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Stride = stride; OldCap = oldCap; NewCap = newCap; }
}

/// <summary>Decoded Storage:FileHandle Open/Close instant. Payload: <c>op u8, filePathId i32, modeOrReason u8</c> (6 B).</summary>
[PublicAPI]
public readonly struct StorageFileHandleData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    /// <summary>0 = open, 1 = close.</summary>
    public byte Op { get; }
    public int FilePathId { get; }
    public byte ModeOrReason { get; }
    public StorageFileHandleData(byte threadSlot, long timestamp, byte op, int filePathId, byte modeOrReason)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Op = op; FilePathId = filePathId; ModeOrReason = modeOrReason; }
}

/// <summary>Decoded Storage:OccupancyMap:Grow instant. Payload: <c>oldCap i32, newCap i32</c> (8 B).</summary>
[PublicAPI]
public readonly struct StorageOccupancyMapGrowData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public int OldCap { get; }
    public int NewCap { get; }
    public StorageOccupancyMapGrowData(byte threadSlot, long timestamp, int oldCap, int newCap)
    { ThreadSlot = threadSlot; Timestamp = timestamp; OldCap = oldCap; NewCap = newCap; }
}

/// <summary>Wire codec for Storage:* miscellaneous events (kinds 165/169/170/171).</summary>
public static class StorageMiscEventCodec
{
    private const int DirtyWalkPayload = 4 + 4 + 4;         // 12
    public const int ChunkSegmentGrowSize = TraceRecordHeader.CommonHeaderSize + 4 + 4 + 4; // 24
    public const int FileHandleSize      = TraceRecordHeader.CommonHeaderSize + 1 + 4 + 1;  // 18
    public const int OccupancyMapGrowSize = TraceRecordHeader.CommonHeaderSize + 4 + 4;     // 20

    public static int ComputeSizeDirtyWalk(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + DirtyWalkPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StoragePageCacheDirtyWalkData DecodeDirtyWalk(ReadOnlySpan<byte> source)
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
        return new StoragePageCacheDirtyWalkData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId,
            traceIdHi, traceIdLo, sourceLocationId,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteChunkSegmentGrow(Span<byte> destination, byte threadSlot, long timestamp, int stride, int oldCap, int newCap)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ChunkSegmentGrowSize, TraceEventKind.StorageChunkSegmentGrow, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, stride);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], oldCap);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], newCap);
    }

    public static StorageChunkSegmentGrowData DecodeChunkSegmentGrow(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new StorageChunkSegmentGrowData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFileHandle(Span<byte> destination, byte threadSlot, long timestamp, byte op, int filePathId, byte modeOrReason)
    {
        TraceRecordHeader.WriteCommonHeader(destination, FileHandleSize, TraceEventKind.StorageFileHandle, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = op;
        BinaryPrimitives.WriteInt32LittleEndian(p[1..], filePathId);
        p[5] = modeOrReason;
    }

    public static StorageFileHandleData DecodeFileHandle(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new StorageFileHandleData(threadSlot, timestamp,
            p[0],
            BinaryPrimitives.ReadInt32LittleEndian(p[1..]),
            p[5]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteOccupancyMapGrow(Span<byte> destination, byte threadSlot, long timestamp, int oldCap, int newCap)
    {
        TraceRecordHeader.WriteCommonHeader(destination, OccupancyMapGrowSize, TraceEventKind.StorageOccupancyMapGrow, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(p, oldCap);
        BinaryPrimitives.WriteInt32LittleEndian(p[4..], newCap);
    }

    public static StorageOccupancyMapGrowData DecodeOccupancyMapGrow(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new StorageOccupancyMapGrowData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }
}
