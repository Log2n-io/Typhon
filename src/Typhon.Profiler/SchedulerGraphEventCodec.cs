using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Graph Build span. Payload: <c>sysCount u16, edgeCount u16, topoLen u16</c> (6 B).</summary>
[PublicAPI]
public readonly struct SchedulerGraphBuildData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort SysCount { get; }
    public ushort EdgeCount { get; }
    public ushort TopoLen { get; }
    public SchedulerGraphBuildData(byte threadSlot, long startTimestamp, long durationTicks, ushort sysCount, ushort edgeCount, ushort topoLen)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SysCount = sysCount; EdgeCount = edgeCount; TopoLen = topoLen; }
}

/// <summary>Decoded Graph Rebuild span (design stub — no producer in Phase 4). Payload: <c>oldSysCount u16, newSysCount u16, reason u8</c> (5 B).</summary>
[PublicAPI]
public readonly struct SchedulerGraphRebuildData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort OldSysCount { get; }
    public ushort NewSysCount { get; }
    public byte Reason { get; }
    public SchedulerGraphRebuildData(byte threadSlot, long startTimestamp, long durationTicks, ushort oldSysCount, ushort newSysCount, byte reason)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; OldSysCount = oldSysCount; NewSysCount = newSysCount; Reason = reason; }
}

/// <summary>Wire codec for Scheduler:Graph events (kinds 159-160).</summary>
public static class SchedulerGraphEventCodec
{
    private const int BuildPayload = 2 + 2 + 2;        // 6
    private const int RebuildPayload = 2 + 2 + 1;      // 5

    public static int ComputeSizeBuild(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + BuildPayload;
    public static int ComputeSizeRebuild(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + RebuildPayload;

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

    public static SchedulerGraphBuildData DecodeBuild(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SchedulerGraphBuildData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]));
    }

    public static SchedulerGraphRebuildData DecodeRebuild(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new SchedulerGraphRebuildData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            p[4]);
    }
}
