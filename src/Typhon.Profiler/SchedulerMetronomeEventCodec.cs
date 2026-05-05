using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded Scheduler Metronome Wait span. Payload:
/// <c>scheduledTimestamp i64, multiplier u8, intentClass u8, phaseFlags u8</c> (11 B).
/// </summary>
/// <remarks>
/// <para><b>intentClass</b>: 0 = CatchUp (target was already past at wait start — wait phases
/// short-circuited and the next tick fired immediately), 1 = Throttled
/// (<see cref="OverloadDetector.TickMultiplier"/> &gt; 1 — engine is voluntarily slowing
/// itself in response to recent overruns), 2 = Headroom (multiplier == 1, normal between-tick
/// idle waiting for the next 60Hz boundary).</para>
/// <para><b>phaseFlags</b> bits: 0x1 = Sleep entered (Thread.Sleep called at least once),
/// 0x2 = Yield entered (Thread.Yield called at least once), 0x4 = Spin entered.</para>
/// </remarks>
[PublicAPI]
public readonly struct SchedulerMetronomeWaitData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public long ScheduledTimestamp { get; }
    public byte Multiplier { get; }
    public byte IntentClass { get; }
    public byte PhaseFlags { get; }

    public SchedulerMetronomeWaitData(byte threadSlot, long startTimestamp, long durationTicks,
        long scheduledTimestamp, byte multiplier, byte intentClass, byte phaseFlags)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        ScheduledTimestamp = scheduledTimestamp;
        Multiplier = multiplier;
        IntentClass = intentClass;
        PhaseFlags = phaseFlags;
    }
}

/// <summary>
/// Decoded Scheduler OverloadDetector instant snapshot (per-tick gauge sample). Payload:
/// <c>tick i64, overrunRatio f32, consecutiveOverrun u16, consecutiveUnderrun u16,
/// consecutiveQueueGrowth u16, queueDepth i32, level u8, multiplier u8</c> (24 B).
/// </summary>
[PublicAPI]
public readonly struct SchedulerOverloadDetectorData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Tick { get; }
    public float OverrunRatio { get; }
    public ushort ConsecutiveOverrun { get; }
    public ushort ConsecutiveUnderrun { get; }
    public ushort ConsecutiveQueueGrowth { get; }
    public int QueueDepth { get; }
    public byte Level { get; }
    public byte Multiplier { get; }

    public SchedulerOverloadDetectorData(byte threadSlot, long timestamp, long tick, float overrunRatio,
        ushort consecutiveOverrun, ushort consecutiveUnderrun, ushort consecutiveQueueGrowth,
        int queueDepth, byte level, byte multiplier)
    {
        ThreadSlot = threadSlot;
        Timestamp = timestamp;
        Tick = tick;
        OverrunRatio = overrunRatio;
        ConsecutiveOverrun = consecutiveOverrun;
        ConsecutiveUnderrun = consecutiveUnderrun;
        ConsecutiveQueueGrowth = consecutiveQueueGrowth;
        QueueDepth = queueDepth;
        Level = level;
        Multiplier = multiplier;
    }
}

/// <summary>
/// Wire codec for Scheduler:Metronome (kind 241) and Scheduler:Overload:Detector (kind 242).
/// Issue #289 follow-up — surfaces the previously invisible inter-tick wait + per-tick detector
/// state so the profiler can answer "why did the engine wait for nothing".
/// </summary>
public static class SchedulerMetronomeEventCodec
{
    private const int WaitPayload = 8 + 1 + 1 + 1;                                       // 11
    public const int DetectorSize = TraceRecordHeader.CommonHeaderSize + 8 + 4 + 2 + 2 + 2 + 4 + 1 + 1; // 36

    public static int ComputeSizeWait(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + WaitPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSpanPreamble(Span<byte> destination, ushort size, byte threadSlot, long startTimestamp,
        long durationTicks, ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, bool hasTraceContext)
    {
        TraceRecordHeader.WriteCommonHeader(destination, size, TraceEventKind.SchedulerMetronomeWait, threadSlot, startTimestamp);
        var spanFlags = hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks, spanId, parentSpanId, spanFlags);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
    }

    /// <summary>
    /// Encode a SchedulerMetronomeWait span record. The wait happens on the timer thread between
    /// ticks. We accept a caller-supplied <paramref name="spanId"/> so each emitted span is uniquely
    /// addressable by trace-tree features (filters, parent-child grouping); <paramref name="parentSpanId"/>
    /// is typically 0 because the wait has no lexically enclosing span. No distributed-trace context
    /// (the wait is a self-contained internal event).
    /// </summary>
    public static void EncodeWait(Span<byte> destination, byte threadSlot, long startTimestamp, long endTimestamp,
        ulong spanId, ulong parentSpanId,
        long scheduledTimestamp, byte multiplier, byte intentClass, byte phaseFlags, out int bytesWritten)
    {
        const bool hasTC = false;
        var size = ComputeSizeWait(hasTC);
        WriteSpanPreamble(destination, (ushort)size, threadSlot, startTimestamp,
            endTimestamp - startTimestamp, spanId, parentSpanId,
            traceIdHi: 0, traceIdLo: 0, hasTC);
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteInt64LittleEndian(p, scheduledTimestamp);
        p[8] = multiplier;
        p[9] = intentClass;
        p[10] = phaseFlags;
        bytesWritten = size;
    }

    public static SchedulerMetronomeWaitData DecodeWait(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SchedulerMetronomeWaitData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            p[8], p[9], p[10]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDetector(Span<byte> destination, byte threadSlot, long timestamp,
        long tick, float overrunRatio, ushort consecutiveOverrun, ushort consecutiveUnderrun,
        ushort consecutiveQueueGrowth, int queueDepth, byte level, byte multiplier)
    {
        TraceRecordHeader.WriteCommonHeader(destination, DetectorSize, TraceEventKind.SchedulerOverloadDetector, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tick);
        BinaryPrimitives.WriteSingleLittleEndian(p[8..], overrunRatio);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], consecutiveOverrun);
        BinaryPrimitives.WriteUInt16LittleEndian(p[14..], consecutiveUnderrun);
        BinaryPrimitives.WriteUInt16LittleEndian(p[16..], consecutiveQueueGrowth);
        BinaryPrimitives.WriteInt32LittleEndian(p[18..], queueDepth);
        p[22] = level;
        p[23] = multiplier;
    }

    public static SchedulerOverloadDetectorData DecodeDetector(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerOverloadDetectorData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadSingleLittleEndian(p[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[14..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[18..]),
            p[22], p[23]);
    }
}
