using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Worker Idle span. Payload: <c>workerId u8, spinCount u16, idleUs u32</c> (7 B).</summary>
[PublicAPI]
public readonly struct SchedulerWorkerIdleData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public byte WorkerId { get; }
    public ushort SpinCount { get; }
    public uint IdleUs { get; }
    public SchedulerWorkerIdleData(byte threadSlot, long startTimestamp, long durationTicks, byte workerId, ushort spinCount, uint idleUs)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; WorkerId = workerId; SpinCount = spinCount; IdleUs = idleUs; }
}

/// <summary>Decoded Worker Wake instant. Payload: <c>workerId u8, delayUs u32</c> (5 B).</summary>
[PublicAPI]
public readonly struct SchedulerWorkerWakeData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte WorkerId { get; }
    public uint DelayUs { get; }
    public SchedulerWorkerWakeData(byte threadSlot, long timestamp, byte workerId, uint delayUs)
    { ThreadSlot = threadSlot; Timestamp = timestamp; WorkerId = workerId; DelayUs = delayUs; }
}

/// <summary>Decoded Worker BetweenTick span. Payload: <c>workerId u8, waitUs u32, wakeReason u8</c> (6 B).</summary>
[PublicAPI]
public readonly struct SchedulerWorkerBetweenTickData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public byte WorkerId { get; }
    public uint WaitUs { get; }
    public byte WakeReason { get; }
    public SchedulerWorkerBetweenTickData(byte threadSlot, long startTimestamp, long durationTicks, byte workerId, uint waitUs, byte wakeReason)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; WorkerId = workerId; WaitUs = waitUs; WakeReason = wakeReason; }
}

/// <summary>Wire codec for Scheduler:Worker events (kinds 150-152).</summary>
public static class SchedulerWorkerEventCodec
{
    private const int IdlePayload = 1 + 2 + 4;                                    // 7
    public const int WakeSize = TraceRecordHeader.CommonHeaderSize + 1 + 4;       // 17
    private const int BetweenTickPayload = 1 + 4 + 1;                             // 6

    public static int ComputeSizeIdle(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + IdlePayload;
    public static int ComputeSizeBetweenTick(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + BetweenTickPayload;

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

    public static SchedulerWorkerIdleData DecodeIdle(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SchedulerWorkerIdleData(threadSlot, startTimestamp, durationTicks,
            p[0], BinaryPrimitives.ReadUInt16LittleEndian(p[1..]), BinaryPrimitives.ReadUInt32LittleEndian(p[3..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteWake(Span<byte> destination, byte threadSlot, long timestamp, byte workerId, uint delayUs)
    {
        TraceRecordHeader.WriteCommonHeader(destination, WakeSize, TraceEventKind.SchedulerWorkerWake, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = workerId;
        BinaryPrimitives.WriteUInt32LittleEndian(p[1..], delayUs);
    }

    public static SchedulerWorkerWakeData DecodeWake(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerWorkerWakeData(threadSlot, timestamp, p[0], BinaryPrimitives.ReadUInt32LittleEndian(p[1..]));
    }

    public static SchedulerWorkerBetweenTickData DecodeBetweenTick(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SchedulerWorkerBetweenTickData(threadSlot, startTimestamp, durationTicks,
            p[0], BinaryPrimitives.ReadUInt32LittleEndian(p[1..]), p[5]);
    }
}
