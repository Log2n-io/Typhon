using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Dependency Ready instant. Payload: <c>fromSysIdx u16, toSysIdx u16, fanOut u16, predRemain u16</c> (8 B).</summary>
[PublicAPI]
public readonly struct SchedulerDependencyReadyData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort FromSysIdx { get; }
    public ushort ToSysIdx { get; }
    public ushort FanOut { get; }
    public ushort PredRemain { get; }
    public SchedulerDependencyReadyData(byte threadSlot, long timestamp, ushort fromSysIdx, ushort toSysIdx, ushort fanOut, ushort predRemain)
    { ThreadSlot = threadSlot; Timestamp = timestamp; FromSysIdx = fromSysIdx; ToSysIdx = toSysIdx; FanOut = fanOut; PredRemain = predRemain; }
}

/// <summary>Decoded Dependency FanOut span. Payload: <c>completingSysIdx u16, succCount u16, skippedCount u16</c> (6 B).</summary>
[PublicAPI]
public readonly struct SchedulerDependencyFanOutData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort CompletingSysIdx { get; }
    public ushort SuccCount { get; }
    public ushort SkippedCount { get; }
    public SchedulerDependencyFanOutData(byte threadSlot, long startTimestamp, long durationTicks, ushort completingSysIdx, ushort succCount, ushort skippedCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; CompletingSysIdx = completingSysIdx; SuccCount = succCount; SkippedCount = skippedCount; }
}

/// <summary>Wire codec for Scheduler:Dependency events (kinds 154-155).</summary>
public static class SchedulerDependencyEventCodec
{
    public const int ReadySize = TraceRecordHeader.CommonHeaderSize + 2 + 2 + 2 + 2;  // 20
    private const int FanOutPayload = 2 + 2 + 2;                                       // 6

    public static int ComputeSizeFanOut(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + FanOutPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteReady(Span<byte> destination, byte threadSlot, long timestamp, ushort fromSysIdx, ushort toSysIdx, ushort fanOut, ushort predRemain)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ReadySize, TraceEventKind.SchedulerDependencyReady, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, fromSysIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], toSysIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], fanOut);
        BinaryPrimitives.WriteUInt16LittleEndian(p[6..], predRemain);
    }

    public static SchedulerDependencyReadyData DecodeReady(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new SchedulerDependencyReadyData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[6..]));
    }

    public static SchedulerDependencyFanOutData DecodeFanOut(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SchedulerDependencyFanOutData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]));
    }
}
