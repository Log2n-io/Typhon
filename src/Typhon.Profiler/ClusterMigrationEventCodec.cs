using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded form of a <see cref="TraceEventKind.ClusterMigration"/> event.</summary>
public readonly struct ClusterMigrationEventData
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

    /// <summary>Required — archetype ID whose entities are being migrated between spatial cells.</summary>
    public ushort ArchetypeId { get; }

    /// <summary>Required — number of entities migrated this call.</summary>
    public int MigrationCount { get; }

    /// <summary>
    /// Total component instances moved (= <see cref="MigrationCount"/> × the archetype's per-entity component slot count).
    /// Reflects the actual amount of component data shuffled across clusters. Zero on records produced by older engines
    /// that pre-date this field — the wire layout is additive and the decoder tolerates the smaller payload size.
    /// </summary>
    public int ComponentCount { get; }

    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public ClusterMigrationEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, ushort archetypeId, int migrationCount, int componentCount)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        SourceLocationId = sourceLocationId;
        ArchetypeId = archetypeId;
        MigrationCount = migrationCount;
        ComponentCount = componentCount;
    }
}

/// <summary>
/// Wire codec for <see cref="TraceEventKind.ClusterMigration"/>. Payload: <c>u16 archetypeId</c>, <c>i32 migrationCount</c>, <c>i32 componentCount</c>.
/// The trailing <c>componentCount</c> field is wire-additive — readers tolerate records that omit it (treated as zero) so traces produced by older engines
/// remain decodable. The record's size in the common header tells the loader how many payload bytes are present.
/// </summary>
public static class ClusterMigrationEventCodec
{
    private const int ArchetypeIdSize = 2;
    private const int MigrationCountSize = 4;
    private const int ComponentCountSize = 4;
    private const int PayloadSize = ArchetypeIdSize + MigrationCountSize + ComponentCountSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext)
        => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    public static ClusterMigrationEventData Decode(ReadOnlySpan<byte> source)
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
        var archetypeId = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var migrationCount = BinaryPrimitives.ReadInt32LittleEndian(payload[ArchetypeIdSize..]);
        // Wire-additive: older traces don't carry componentCount. Decode 0 in that case.
        var componentCount = payload.Length >= PayloadSize
            ? BinaryPrimitives.ReadInt32LittleEndian(payload[(ArchetypeIdSize + MigrationCountSize)..])
            : 0;

        return new ClusterMigrationEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            archetypeId, migrationCount, componentCount);
    }
}

