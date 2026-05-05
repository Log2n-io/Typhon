using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded form of a <see cref="TraceEventKind.StatisticsRebuild"/> event.</summary>
public readonly struct StatisticsRebuildEventData
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

    /// <summary>Required — total entity count in the component table at rebuild time.</summary>
    public int EntityCount { get; }

    /// <summary>Required — number of mutations since last statistics rebuild.</summary>
    public int MutationCount { get; }

    /// <summary>Required — sampling interval used for the rebuild (e.g. every N-th entity).</summary>
    public int SamplingInterval { get; }

    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public StatisticsRebuildEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, int entityCount, int mutationCount, int samplingInterval)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        SourceLocationId = sourceLocationId;
        EntityCount = entityCount;
        MutationCount = mutationCount;
        SamplingInterval = samplingInterval;
    }
}

/// <summary>Wire codec for <see cref="TraceEventKind.StatisticsRebuild"/>. Payload: <c>i32 entityCount</c>, <c>i32 mutationCount</c>, <c>i32 samplingInterval</c>.</summary>
public static class StatisticsRebuildEventCodec
{
    private const int EntityCountSize = 4;
    private const int MutationCountSize = 4;
    private const int SamplingIntervalSize = 4;
    private const int PayloadSize = EntityCountSize + MutationCountSize + SamplingIntervalSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext)
        => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    public static StatisticsRebuildEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = source[headerSize..];
        var entityCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var mutationCount = BinaryPrimitives.ReadInt32LittleEndian(payload[EntityCountSize..]);
        var samplingInterval = BinaryPrimitives.ReadInt32LittleEndian(payload[(EntityCountSize + MutationCountSize)..]);

        return new StatisticsRebuildEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            entityCount, mutationCount, samplingInterval);
    }
}

