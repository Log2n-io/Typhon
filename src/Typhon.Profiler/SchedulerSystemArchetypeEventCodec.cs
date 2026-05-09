using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of a <see cref="TraceEventKind.SchedulerSystemArchetype"/> event — one (system, archetype) entity-touch rollup emitted at parallel-query
/// completion. Captures the cross-dimension that <see cref="SchedulerChunkEventData"/> (per-system) and the EcsQuery* events (per-archetype) leave separate.
/// Feeds the Workbench Data Flow module's track families.
/// </summary>
public readonly struct SchedulerSystemArchetypeEventData
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

    /// <summary>Required — archetype id (declared by <c>[Archetype(N)]</c>; 0–4095 per the ushort-12-bit allocation).</summary>
    public ushort ArchetypeId { get; }

    /// <summary>Required — entities the system processed for this archetype during this tick.</summary>
    public int EntityCount { get; }

    /// <summary>Required — chunks dispatched for the parallel-query bracket (sum across workers).</summary>
    public int ChunkCount { get; }

    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public SchedulerSystemArchetypeEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, ushort systemIndex, ushort archetypeId, int entityCount, int chunkCount)
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
        ArchetypeId = archetypeId;
        EntityCount = entityCount;
        ChunkCount = chunkCount;
    }
}

/// <summary>Wire codec for <see cref="TraceEventKind.SchedulerSystemArchetype"/>. Payload: <c>u16 systemIdx</c>, <c>u16 archetypeId</c>, <c>i32 entityCount</c>, <c>i32 chunkCount</c>.</summary>
public static class SchedulerSystemArchetypeEventCodec
{
    private const int PayloadSize = 2 + 2 + 4 + 4;  // 12 B

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext)
        => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    public static SchedulerSystemArchetypeEventData Decode(ReadOnlySpan<byte> source)
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
        var archetypeId = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
        var entityCount = BinaryPrimitives.ReadInt32LittleEndian(payload[4..]);
        var chunkCount = BinaryPrimitives.ReadInt32LittleEndian(payload[8..]);

        return new SchedulerSystemArchetypeEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            systemIndex, archetypeId, entityCount, chunkCount);
    }
}
