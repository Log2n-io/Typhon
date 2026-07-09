using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Runtime UoWCreate instant. Payload: <c>tick i64</c> (8 B).</summary>
[PublicAPI]
public readonly struct RuntimePhaseUoWCreateData
{
    /// <summary>Producer thread slot (0-255, from the thread-slot registry) the record was emitted on.</summary>
    public byte ThreadSlot { get; }
    /// <summary>Emit timestamp in <c>Stopwatch.GetTimestamp()</c> ticks.</summary>
    public long Timestamp { get; }
    /// <summary>Engine tick number when the Unit-of-Work was created.</summary>
    public long Tick { get; }
    /// <summary>Constructs the decoded UoWCreate record from its wire fields.</summary>
    public RuntimePhaseUoWCreateData(byte threadSlot, long timestamp, long tick)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Tick = tick; }
}

/// <summary>Decoded Runtime UoWFlush instant. Payload: <c>tick i64, changeCount i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct RuntimePhaseUoWFlushData
{
    /// <summary>Producer thread slot (0-255, from the thread-slot registry) the record was emitted on.</summary>
    public byte ThreadSlot { get; }
    /// <summary>Emit timestamp in <c>Stopwatch.GetTimestamp()</c> ticks.</summary>
    public long Timestamp { get; }
    /// <summary>Engine tick number when the Unit-of-Work was flushed.</summary>
    public long Tick { get; }
    /// <summary>Number of buffered component changes the Unit-of-Work flushed.</summary>
    public int ChangeCount { get; }
    /// <summary>Constructs the decoded UoWFlush record from its wire fields.</summary>
    public RuntimePhaseUoWFlushData(byte threadSlot, long timestamp, long tick, int changeCount)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Tick = tick; ChangeCount = changeCount; }
}

/// <summary>Decoded Runtime Transaction Lifecycle span. Payload: <c>sysIdx u16, txDurUs u32, success u8</c> (7 B).</summary>
[PublicAPI]
public readonly struct RuntimeTransactionLifecycleData
{
    /// <summary>Producer thread slot (0-255, from the thread-slot registry) the span was emitted on.</summary>
    public byte ThreadSlot { get; }
    /// <summary>Span start timestamp in <c>Stopwatch.GetTimestamp()</c> ticks.</summary>
    public long StartTimestamp { get; }
    /// <summary>Span duration in <c>Stopwatch.GetTimestamp()</c> ticks.</summary>
    public long DurationTicks { get; }
    /// <summary>System index (DAG slot) that owned the transaction.</summary>
    public ushort SysIdx { get; }
    /// <summary>Transaction duration in microseconds, measured by the engine (distinct from the span's <see cref="DurationTicks"/>).</summary>
    public uint TxDurUs { get; }
    /// <summary>Commit outcome — nonzero when committed, <c>0</c> when rolled back.</summary>
    public byte Success { get; }
    /// <summary>Constructs the decoded transaction-lifecycle record from its wire fields.</summary>
    public RuntimeTransactionLifecycleData(byte threadSlot, long startTimestamp, long durationTicks, ushort sysIdx, uint txDurUs, byte success)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SysIdx = sysIdx; TxDurUs = txDurUs; Success = success; }
}

/// <summary>Decoded Runtime Subscription Output Execute span. Payload: <c>tick i64, level u8, clientCount u16, viewsRefreshed u16, deltasPushed u32, overflowCount u16</c> (17 B).</summary>
[PublicAPI]
public readonly struct RuntimeSubscriptionOutputExecuteData
{
    /// <summary>Producer thread slot (0-255, from the thread-slot registry) the span was emitted on.</summary>
    public byte ThreadSlot { get; }
    /// <summary>Span start timestamp in <c>Stopwatch.GetTimestamp()</c> ticks.</summary>
    public long StartTimestamp { get; }
    /// <summary>Span duration in <c>Stopwatch.GetTimestamp()</c> ticks.</summary>
    public long DurationTicks { get; }
    /// <summary>Engine tick number when the subscription output phase ran.</summary>
    public long Tick { get; }
    /// <summary>Output-phase level byte (subscription tier).</summary>
    public byte Level { get; }
    /// <summary>Number of connected subscription clients served this output phase.</summary>
    public ushort ClientCount { get; }
    /// <summary>Number of published Views refreshed this output phase.</summary>
    public ushort ViewsRefreshed { get; }
    /// <summary>Number of delta records pushed to clients.</summary>
    public uint DeltasPushed { get; }
    /// <summary>Number of clients whose delta buffer overflowed during the push.</summary>
    public ushort OverflowCount { get; }
    /// <summary>Constructs the decoded subscription-output-execute record from its wire fields.</summary>
    public RuntimeSubscriptionOutputExecuteData(byte threadSlot, long startTimestamp, long durationTicks, long tick, byte level,
        ushort clientCount, ushort viewsRefreshed, uint deltasPushed, ushort overflowCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; Tick = tick; Level = level;
      ClientCount = clientCount; ViewsRefreshed = viewsRefreshed; DeltasPushed = deltasPushed; OverflowCount = overflowCount; }
}

/// <summary>Wire codec for Runtime events (kinds 161-164).</summary>
public static class RuntimeEventCodec
{
    /// <summary>Fixed wire size in bytes of a UoWCreate record (common header + 8-byte tick).</summary>
    public const int UoWCreateSize = TraceRecordHeader.CommonHeaderSize + 8;          // 20
    /// <summary>Fixed wire size in bytes of a UoWFlush record (common header + tick + changeCount).</summary>
    public const int UoWFlushSize  = TraceRecordHeader.CommonHeaderSize + 8 + 4;       // 24
    private const int LifecyclePayload = 2 + 4 + 1;                                    // 7
    private const int OutputExecutePayload = 8 + 1 + 2 + 2 + 4 + 2;                    // 19

    /// <summary>Wire size in bytes of a Transaction-Lifecycle span, including the 16-byte trace context when <paramref name="hasTraceContext"/> is <c>true</c>.</summary>
    public static int ComputeSizeLifecycle(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + LifecyclePayload;
    /// <summary>Wire size in bytes of a Subscription-Output-Execute span, including the trace context when <paramref name="hasTraceContext"/> is <c>true</c>.</summary>
    public static int ComputeSizeOutputExecute(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + OutputExecutePayload;

    /// <summary>Encode a UoWCreate instant into <paramref name="destination"/> (must be at least <see cref="UoWCreateSize"/> bytes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUoWCreate(Span<byte> destination, byte threadSlot, long timestamp, long tick)
    {
        TraceRecordHeader.WriteCommonHeader(destination, UoWCreateSize, TraceEventKind.RuntimePhaseUoWCreate, threadSlot, timestamp);
        BinaryPrimitives.WriteInt64LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], tick);
    }

    /// <summary>Decode a UoWCreate instant from <paramref name="source"/>.</summary>
    /// <returns>The decoded <see cref="RuntimePhaseUoWCreateData"/>.</returns>
    public static RuntimePhaseUoWCreateData DecodeUoWCreate(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        return new RuntimePhaseUoWCreateData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(source[TraceRecordHeader.CommonHeaderSize..]));
    }

    /// <summary>Encode a UoWFlush instant into <paramref name="destination"/> (must be at least <see cref="UoWFlushSize"/> bytes).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUoWFlush(Span<byte> destination, byte threadSlot, long timestamp, long tick, int changeCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, UoWFlushSize, TraceEventKind.RuntimePhaseUoWFlush, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tick);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], changeCount);
    }

    /// <summary>Decode a UoWFlush instant from <paramref name="source"/>.</summary>
    /// <returns>The decoded <see cref="RuntimePhaseUoWFlushData"/>.</returns>
    public static RuntimePhaseUoWFlushData DecodeUoWFlush(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new RuntimePhaseUoWFlushData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }

    /// <summary>
    /// Encode a Transaction-Lifecycle span into <paramref name="destination"/>. Emits the optional 16-byte trace context when
    /// <paramref name="traceIdHi"/> or <paramref name="traceIdLo"/> is nonzero; <paramref name="bytesWritten"/> returns the total record size.
    /// </summary>
    public static void EncodeLifecycle(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ushort sysIdx, uint txDurUs, byte success, out int bytesWritten)
    {
        var hasTC = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSizeLifecycle(hasTC);
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.RuntimeTransactionLifecycle, threadSlot, startTimestamp);
        var spanFlags = hasTC ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);
        if (hasTC)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }
        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTC)..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, sysIdx);
        BinaryPrimitives.WriteUInt32LittleEndian(p[2..], txDurUs);
        p[6] = success;
        bytesWritten = size;
    }

    /// <summary>Decode a Transaction-Lifecycle span from <paramref name="source"/>.</summary>
    /// <returns>The decoded <see cref="RuntimeTransactionLifecycleData"/>.</returns>
    public static RuntimeTransactionLifecycleData DecodeLifecycle(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new RuntimeTransactionLifecycleData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt32LittleEndian(p[2..]),
            p[6]);
    }

    /// <summary>Decode a Subscription-Output-Execute span from <paramref name="source"/>.</summary>
    /// <returns>The decoded <see cref="RuntimeSubscriptionOutputExecuteData"/>.</returns>
    public static RuntimeSubscriptionOutputExecuteData DecodeOutputExecute(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out _, out _, out var spanFlags);
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        var p = source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
        return new RuntimeSubscriptionOutputExecuteData(threadSlot, startTimestamp, durationTicks,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            p[8],
            BinaryPrimitives.ReadUInt16LittleEndian(p[9..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[11..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[13..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[17..]));
    }
}
