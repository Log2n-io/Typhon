using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of a <see cref="TraceEventKind.SchedulerChunk"/> event — one chunk of a parallel system executing on a worker. The span's
/// duration covers the chunk's execution bracket.
/// </summary>
public readonly struct SchedulerChunkEventData
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

    /// <summary>Required — DAG system index.</summary>
    public ushort SystemIndex { get; }

    /// <summary>Required — chunk index within this system's parallel dispatch (0 for sequential systems).</summary>
    public ushort ChunkIndex { get; }

    /// <summary>Required — total chunks for this system's dispatch.</summary>
    public ushort TotalChunks { get; }

    /// <summary>Required — number of entities processed by this chunk.</summary>
    public int EntitiesProcessed { get; }

    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public SchedulerChunkEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, ushort systemIndex, ushort chunkIndex, ushort totalChunks, int entitiesProcessed)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        SourceLocationId = sourceLocationId;
        SystemIndex = systemIndex;
        ChunkIndex = chunkIndex;
        TotalChunks = totalChunks;
        EntitiesProcessed = entitiesProcessed;
    }
}

/// <summary>Wire codec for <see cref="TraceEventKind.SchedulerChunk"/>. Payload: <c>u16 systemIdx</c>, <c>u16 chunkIdx</c>, <c>u16 totalChunks</c>, <c>i32 entitiesProcessed</c>.</summary>
public static class SchedulerChunkEventCodec
{
    private const int PayloadSize = 2 + 2 + 2 + 4;  // 10 B

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext)
        => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    public static SchedulerChunkEventData Decode(ReadOnlySpan<byte> source)
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
        var systemIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        var totalChunks = BinaryPrimitives.ReadUInt16LittleEndian(payload[4..]);
        var entitiesProcessed = BinaryPrimitives.ReadInt32LittleEndian(payload[6..]);

        return new SchedulerChunkEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            systemIndex, chunkIndex, totalChunks, entitiesProcessed);
    }
}

