using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Data:Transaction:Init span. Payload: <c>tsn i64, uowId u16</c> (10 B).</summary>
[PublicAPI]
public readonly struct DataTransactionInitData
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
    public long Tsn { get; }
    public ushort UowId { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataTransactionInitData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, long tsn, ushort uowId)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; SourceLocationId = sourceLocationId; Tsn = tsn; UowId = uowId; }
}

/// <summary>Decoded Data:Transaction:Prepare span. Payload: <c>tsn i64</c> (8 B).</summary>
[PublicAPI]
public readonly struct DataTransactionPrepareData
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
    public long Tsn { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataTransactionPrepareData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, long tsn)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; SourceLocationId = sourceLocationId; Tsn = tsn; }
}

/// <summary>Decoded Data:Transaction:Validate span. Payload: <c>tsn i64, entryCount i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct DataTransactionValidateData
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
    public long Tsn { get; }
    public int EntryCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataTransactionValidateData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, long tsn, int entryCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; SourceLocationId = sourceLocationId; Tsn = tsn; EntryCount = entryCount; }
}

/// <summary>Decoded Data:Transaction:Conflict instant. Payload: <c>tsn i64, pk i64, componentTypeId i32, conflictType u8</c> (21 B).</summary>
[PublicAPI]
public readonly struct DataTransactionConflictData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Tsn { get; }
    public long Pk { get; }
    public int ComponentTypeId { get; }
    public byte ConflictType { get; }
    public DataTransactionConflictData(byte threadSlot, long timestamp, long tsn, long pk, int componentTypeId, byte conflictType)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Tsn = tsn; Pk = pk; ComponentTypeId = componentTypeId; ConflictType = conflictType; }
}

/// <summary>Decoded Data:Transaction:Cleanup span. Payload: <c>tsn i64, entityCount i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct DataTransactionCleanupData
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
    public long Tsn { get; }
    public int EntityCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DataTransactionCleanupData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, long tsn, int entityCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SpanId = spanId; ParentSpanId = parentSpanId;
      TraceIdHi = traceIdHi; TraceIdLo = traceIdLo; SourceLocationId = sourceLocationId; Tsn = tsn; EntityCount = entityCount; }
}

/// <summary>Wire codec for Data:Transaction events (kinds 173-177).</summary>
public static class DataTransactionEventCodec
{
    public const int ConflictSize = TraceRecordHeader.CommonHeaderSize + 8 + 8 + 4 + 1;  // 33

    private const int InitPayload     = 8 + 2;
    private const int PreparePayload  = 8;
    private const int ValidatePayload = 8 + 4;
    private const int CleanupPayload  = 8 + 4;

    public static int ComputeSizeInit(bool hasTC)     => TraceRecordHeader.SpanHeaderSize(hasTC) + InitPayload;
    public static int ComputeSizePrepare(bool hasTC)  => TraceRecordHeader.SpanHeaderSize(hasTC) + PreparePayload;
    public static int ComputeSizeValidate(bool hasTC) => TraceRecordHeader.SpanHeaderSize(hasTC) + ValidatePayload;
    public static int ComputeSizeCleanup(bool hasTC)  => TraceRecordHeader.SpanHeaderSize(hasTC) + CleanupPayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteSpanPreamble(Span<byte> destination, TraceEventKind kind, ushort size, byte threadSlot, long startTimestamp,
        long durationTicks, ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo, bool hasTC)
    {
        TraceRecordHeader.WriteCommonHeader(destination, size, kind, threadSlot, startTimestamp);
        var spanFlags = hasTC ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            durationTicks, spanId, parentSpanId, spanFlags);
        if (hasTC)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
    }

    /// <summary>
    /// Read the optional <c>SourceLocationId</c> from the span header. Returns 0 when the
    /// <c>SpanFlagsHasSourceLocation</c> bit is unset. Centralized so the four <c>Decode*</c>
    /// methods don't repeat the read-and-conditional-skip dance.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReadSourceLocationIdIfPresent(ReadOnlySpan<byte> source, byte spanFlags, bool hasTC)
    {
        if ((spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) == 0) return 0;
        return TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTC)..]);
    }

    public static DataTransactionInitData DecodeInit(ReadOnlySpan<byte> source)
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
        var sourceLocationId = ReadSourceLocationIdIfPresent(source, spanFlags, hasTC);
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new DataTransactionInitData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]));
    }

    public static DataTransactionPrepareData DecodePrepare(ReadOnlySpan<byte> source)
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
        var sourceLocationId = ReadSourceLocationIdIfPresent(source, spanFlags, hasTC);
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new DataTransactionPrepareData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            BinaryPrimitives.ReadInt64LittleEndian(p));
    }

    public static DataTransactionValidateData DecodeValidate(ReadOnlySpan<byte> source)
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
        var sourceLocationId = ReadSourceLocationIdIfPresent(source, spanFlags, hasTC);
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new DataTransactionValidateData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteConflict(Span<byte> destination, byte threadSlot, long timestamp, long tsn, long pk, int componentTypeId, byte conflictType)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ConflictSize, TraceEventKind.DataTransactionConflict, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tsn);
        BinaryPrimitives.WriteInt64LittleEndian(p[8..], pk);
        BinaryPrimitives.WriteInt32LittleEndian(p[16..], componentTypeId);
        p[20] = conflictType;
    }

    public static DataTransactionConflictData DecodeConflict(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DataTransactionConflictData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt64LittleEndian(p[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[16..]),
            p[20]);
    }

    public static DataTransactionCleanupData DecodeCleanup(ReadOnlySpan<byte> source)
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
        var sourceLocationId = ReadSourceLocationIdIfPresent(source, spanFlags, hasTC);
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new DataTransactionCleanupData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }
}
