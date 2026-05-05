using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Data:MVCC:ChainWalk instant. Payload: <c>tsn i64, chainLen u8, visibility u8</c> (10 B).</summary>
[PublicAPI]
public readonly struct DataMvccChainWalkData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Tsn { get; }
    public byte ChainLen { get; }
    public byte Visibility { get; }
    public DataMvccChainWalkData(byte threadSlot, long timestamp, long tsn, byte chainLen, byte visibility)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Tsn = tsn; ChainLen = chainLen; Visibility = visibility; }
}

/// <summary>Decoded Data:MVCC:VersionCleanup span. Payload: <c>pk i64, entriesFreed u16</c> (10 B).</summary>
[PublicAPI]
public readonly struct DataMvccVersionCleanupData
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
    public long Pk { get; }
    public ushort EntriesFreed { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataMvccVersionCleanupData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, long pk, ushort entriesFreed)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; SourceLocationId = sourceLocationId; Pk = pk; EntriesFreed = entriesFreed; }
}

/// <summary>Wire codec for Data:MVCC events (kinds 178-179).</summary>
public static class DataMvccEventCodec
{
    public const int ChainWalkSize = TraceRecordHeader.CommonHeaderSize + 8 + 1 + 1;  // 22

    private const int VersionCleanupPayload = 8 + 2;
    public static int ComputeSizeVersionCleanup(bool hasTC) => TraceRecordHeader.SpanHeaderSize(hasTC) + VersionCleanupPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteChainWalk(Span<byte> destination, byte threadSlot, long timestamp, long tsn, byte chainLen, byte visibility)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ChainWalkSize, TraceEventKind.DataMvccChainWalk, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tsn);
        p[8] = chainLen;
        p[9] = visibility;
    }

    public static DataMvccChainWalkData DecodeChainWalk(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DataMvccChainWalkData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p), p[8], p[9]);
    }

    public static DataMvccVersionCleanupData DecodeVersionCleanup(ReadOnlySpan<byte> source)
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
        return new DataMvccVersionCleanupData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]));
    }
}
