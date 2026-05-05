using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded Runtime UoWCreate instant. Payload: <c>tick i64</c> (8 B).</summary>
[PublicAPI]
public readonly struct RuntimePhaseUoWCreateData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Tick { get; }
    public RuntimePhaseUoWCreateData(byte threadSlot, long timestamp, long tick)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Tick = tick; }
}

/// <summary>Decoded Runtime UoWFlush instant. Payload: <c>tick i64, changeCount i32</c> (12 B).</summary>
[PublicAPI]
public readonly struct RuntimePhaseUoWFlushData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Tick { get; }
    public int ChangeCount { get; }
    public RuntimePhaseUoWFlushData(byte threadSlot, long timestamp, long tick, int changeCount)
    { ThreadSlot = threadSlot; Timestamp = timestamp; Tick = tick; ChangeCount = changeCount; }
}

/// <summary>Decoded Runtime Transaction Lifecycle span. Payload: <c>sysIdx u16, txDurUs u32, success u8</c> (7 B).</summary>
[PublicAPI]
public readonly struct RuntimeTransactionLifecycleData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ushort SysIdx { get; }
    public uint TxDurUs { get; }
    public byte Success { get; }
    public RuntimeTransactionLifecycleData(byte threadSlot, long startTimestamp, long durationTicks, ushort sysIdx, uint txDurUs, byte success)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; SysIdx = sysIdx; TxDurUs = txDurUs; Success = success; }
}

/// <summary>Decoded Runtime Subscription Output Execute span. Payload: <c>tick i64, level u8, clientCount u16, viewsRefreshed u16, deltasPushed u32, overflowCount u16</c> (17 B).</summary>
[PublicAPI]
public readonly struct RuntimeSubscriptionOutputExecuteData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public long Tick { get; }
    public byte Level { get; }
    public ushort ClientCount { get; }
    public ushort ViewsRefreshed { get; }
    public uint DeltasPushed { get; }
    public ushort OverflowCount { get; }
    public RuntimeSubscriptionOutputExecuteData(byte threadSlot, long startTimestamp, long durationTicks, long tick, byte level,
        ushort clientCount, ushort viewsRefreshed, uint deltasPushed, ushort overflowCount)
    { ThreadSlot = threadSlot; StartTimestamp = startTimestamp; DurationTicks = durationTicks; Tick = tick; Level = level;
      ClientCount = clientCount; ViewsRefreshed = viewsRefreshed; DeltasPushed = deltasPushed; OverflowCount = overflowCount; }
}

/// <summary>Wire codec for Runtime events (kinds 161-164).</summary>
public static class RuntimeEventCodec
{
    public const int UoWCreateSize = TraceRecordHeader.CommonHeaderSize + 8;          // 20
    public const int UoWFlushSize  = TraceRecordHeader.CommonHeaderSize + 8 + 4;       // 24
    private const int LifecyclePayload = 2 + 4 + 1;                                    // 7
    private const int OutputExecutePayload = 8 + 1 + 2 + 2 + 4 + 2;                    // 19

    public static int ComputeSizeLifecycle(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + LifecyclePayload;
    public static int ComputeSizeOutputExecute(bool hasTraceContext) => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + OutputExecutePayload;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUoWCreate(Span<byte> destination, byte threadSlot, long timestamp, long tick)
    {
        TraceRecordHeader.WriteCommonHeader(destination, UoWCreateSize, TraceEventKind.RuntimePhaseUoWCreate, threadSlot, timestamp);
        BinaryPrimitives.WriteInt64LittleEndian(destination[TraceRecordHeader.CommonHeaderSize..], tick);
    }

    public static RuntimePhaseUoWCreateData DecodeUoWCreate(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        return new RuntimePhaseUoWCreateData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(source[TraceRecordHeader.CommonHeaderSize..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUoWFlush(Span<byte> destination, byte threadSlot, long timestamp, long tick, int changeCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, UoWFlushSize, TraceEventKind.RuntimePhaseUoWFlush, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, tick);
        BinaryPrimitives.WriteInt32LittleEndian(p[8..], changeCount);
    }

    public static RuntimePhaseUoWFlushData DecodeUoWFlush(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new RuntimePhaseUoWFlushData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }

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
