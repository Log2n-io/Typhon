using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded System StartExecution instant. Payload: <c>sysIdx u16</c> (2 B).</summary>
[PublicAPI]
public readonly struct SchedulerSystemStartExecutionData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort SysIdx { get; }
    public SchedulerSystemStartExecutionData(byte threadSlot, long timestamp, ushort sysIdx)
    { ThreadSlot = threadSlot; Timestamp = timestamp; SysIdx = sysIdx; }
}

/// <summary>Decoded System Completion instant. Payload: <c>sysIdx u16, reason u8, durationUs u32</c> (7 B).</summary>
[PublicAPI]
public readonly struct SchedulerSystemCompletionData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort SysIdx { get; }
    public byte Reason { get; }
    public uint DurationUs { get; }
    public SchedulerSystemCompletionData(byte threadSlot, long timestamp, ushort sysIdx, byte reason, uint durationUs)
    { ThreadSlot = threadSlot; Timestamp = timestamp; SysIdx = sysIdx; Reason = reason; DurationUs = durationUs; }
}

/// <summary>Decoded System QueueWait instant. Payload: <c>sysIdx u16, queueWaitUs u32</c> (6 B).</summary>
[PublicAPI]
public readonly struct SchedulerSystemQueueWaitData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort SysIdx { get; }
    public uint QueueWaitUs { get; }
    public SchedulerSystemQueueWaitData(byte threadSlot, long timestamp, ushort sysIdx, uint queueWaitUs)
    { ThreadSlot = threadSlot; Timestamp = timestamp; SysIdx = sysIdx; QueueWaitUs = queueWaitUs; }
}

/// <summary>Decoded System SingleThreaded span. Payload: <c>sysIdx u16, isParallelQuery u8, chunkCount u16</c> (5 B).</summary>
[PublicAPI]
public readonly struct SchedulerSystemSingleThreadedData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort SysIdx { get; }
    public byte IsParallelQuery { get; }
    public ushort ChunkCount { get; }
    public SchedulerSystemSingleThreadedData(byte threadSlot, long startTimestamp, long durationTicks, ushort sysIdx, byte isParallelQuery, ushort chunkCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SysIdx = sysIdx; IsParallelQuery = isParallelQuery; ChunkCount = chunkCount; }
}

/// <summary>Wire codec for Scheduler:System events (kinds 146-149).</summary>
public static class SchedulerSystemEventCodec
{
    public const int StartExecutionSize = TraceRecordHeader.CommonHeaderSize + 2;             // 14
    public const int CompletionSize     = TraceRecordHeader.CommonHeaderSize + 2 + 1 + 4;     // 19
    public const int QueueWaitSize      = TraceRecordHeader.CommonHeaderSize + 2 + 4;         // 18
    private const int SingleThreadedPayload = 2 + 1 + 2;                                      // 5

    public static int ComputeSizeSingleThreaded(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + SingleThreadedPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteStartExecution(Span<byte> destination, byte threadSlot, long timestamp, ushort sysIdx)
    {
        TraceRecordHeader.WriteCommonHeader(destination, StartExecutionSize, TraceEventKind.SchedulerSystemStartExecution, threadSlot, timestamp);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], sysIdx);
    }

    public static SchedulerSystemStartExecutionData DecodeStartExecution(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        return new SchedulerSystemStartExecutionData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(source[TraceRecordHeader.CommonHeaderSize..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteCompletion(Span<byte> destination, byte threadSlot, long timestamp, ushort sysIdx, byte reason, uint durationUs)
    {
        TraceRecordHeader.WriteCommonHeader(destination, CompletionSize, TraceEventKind.SchedulerSystemCompletion, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, sysIdx);
        p[2] = reason;
        BinaryPrimitives.WriteUInt32LittleEndian(p[3..], durationUs);
    }

    public static SchedulerSystemCompletionData DecodeCompletion(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerSystemCompletionData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p), p[2], BinaryPrimitives.ReadUInt32LittleEndian(p[3..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteQueueWait(Span<byte> destination, byte threadSlot, long timestamp, ushort sysIdx, uint queueWaitUs)
    {
        TraceRecordHeader.WriteCommonHeader(destination, QueueWaitSize, TraceEventKind.SchedulerSystemQueueWait, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, sysIdx);
        BinaryPrimitives.WriteUInt32LittleEndian(p[2..], queueWaitUs);
    }

    public static SchedulerSystemQueueWaitData DecodeQueueWait(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerSystemQueueWaitData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p), BinaryPrimitives.ReadUInt32LittleEndian(p[2..]));
    }

    public static SchedulerSystemSingleThreadedData DecodeSingleThreaded(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SchedulerSystemSingleThreadedData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(p), p[2], BinaryPrimitives.ReadUInt16LittleEndian(p[3..]));
    }
}
