using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

// ── Span data ───────────────────────────────────────────────────────────────

[PublicAPI]
public readonly struct DurabilityWalQueueDrainData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int BytesAligned { get; }
    public int FrameCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityWalQueueDrainData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int ba, int fc)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; BytesAligned = ba; FrameCount = fc; }
}

[PublicAPI]
public readonly struct DurabilityWalOsWriteData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int BytesAligned { get; }
    public int FrameCount { get; }
    public long HighLsn { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityWalOsWriteData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int ba, int fc, long hl)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; BytesAligned = ba; FrameCount = fc; HighLsn = hl; }
}

[PublicAPI]
public readonly struct DurabilityWalSignalData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public long HighLsn { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityWalSignalData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, long hl)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; HighLsn = hl; }
}

[PublicAPI]
public readonly struct DurabilityWalGroupCommitData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort TriggerMs { get; }
    public int ProducerThread { get; }
    public DurabilityWalGroupCommitData(byte ts, long t, ushort tm, int pt)
    { ThreadSlot = ts; Timestamp = t; TriggerMs = tm; ProducerThread = pt; }
}

[PublicAPI]
public readonly struct DurabilityWalQueueData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte DrainAttempt { get; }
    public int DataLen { get; }
    public byte WaitReason { get; }
    public DurabilityWalQueueData(byte ts, long t, byte da, int dl, byte wr)
    { ThreadSlot = ts; Timestamp = t; DrainAttempt = da; DataLen = dl; WaitReason = wr; }
}

[PublicAPI]
public readonly struct DurabilityWalBufferData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int BytesAligned { get; }
    public int Pad { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityWalBufferData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int ba, int p)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; BytesAligned = ba; Pad = p; }
}

[PublicAPI]
public readonly struct DurabilityWalFrameData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort FrameCount { get; }
    public uint CrcStart { get; }
    public DurabilityWalFrameData(byte ts, long t, ushort fc, uint cs)
    { ThreadSlot = ts; Timestamp = t; FrameCount = fc; CrcStart = cs; }
}

[PublicAPI]
public readonly struct DurabilityWalBackpressureData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public uint WaitUs { get; }
    public int ProducerThread { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityWalBackpressureData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, uint wu, int pt)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; WaitUs = wu; ProducerThread = pt; }
}

public static class DurabilityWalEventCodec
{
    public const int GroupCommitSize = TraceRecordHeader.CommonHeaderSize + 2 + 4;       // 18
    public const int QueueSize       = TraceRecordHeader.CommonHeaderSize + 1 + 4 + 1;   // 18
    public const int FrameSize       = TraceRecordHeader.CommonHeaderSize + 2 + 4;       // 18

    private const int QueueDrainPayload   = 4 + 4;          // 8
    private const int OsWritePayload      = 4 + 4 + 8;      // 16
    private const int SignalPayload       = 8;              // 8
    private const int BufferPayload       = 4 + 4;          // 8
    private const int BackpressurePayload = 4 + 4;          // 8

    public static int ComputeSizeQueueDrain(bool hasTC)   => TraceRecordHeader.SpanHeaderSize(hasTC) + QueueDrainPayload;
    public static int ComputeSizeOsWrite(bool hasTC)      => TraceRecordHeader.SpanHeaderSize(hasTC) + OsWritePayload;
    public static int ComputeSizeSignal(bool hasTC)       => TraceRecordHeader.SpanHeaderSize(hasTC) + SignalPayload;
    public static int ComputeSizeBuffer(bool hasTC)       => TraceRecordHeader.SpanHeaderSize(hasTC) + BufferPayload;
    public static int ComputeSizeBackpressure(bool hasTC) => TraceRecordHeader.SpanHeaderSize(hasTC) + BackpressurePayload;

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> ReadSpanPreamble(ReadOnlySpan<byte> source,
        out byte threadSlot, out long startTs, out long dur, out ulong spanId, out ulong parentSpanId,
        out ulong traceIdHi, out ulong traceIdLo)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out threadSlot, out startTs);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out dur, out spanId, out parentSpanId, out var spanFlags);
        traceIdHi = 0; traceIdLo = 0;
        var hasTC = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSL = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        if (hasTC)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        // Source-location bytes (#302) are skipped here — Phase 4 will surface siteId in this codec.
        return source[TraceRecordHeader.SpanHeaderSize(hasTC, hasSL)..];
    }

    // ── QueueDrain ──
    public static DurabilityWalQueueDrainData DecodeQueueDrain(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityWalQueueDrainData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }

    // ── OsWrite ──
    public static DurabilityWalOsWriteData DecodeOsWrite(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityWalOsWriteData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(p[8..]));
    }

    // ── Signal ──
    public static DurabilityWalSignalData DecodeSignal(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityWalSignalData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt64LittleEndian(p));
    }

    // ── GroupCommit (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteGroupCommit(Span<byte> destination, byte threadSlot, long timestamp, ushort triggerMs, int producerThread)
    {
        TraceRecordHeader.WriteCommonHeader(destination, GroupCommitSize, TraceEventKind.DurabilityWalGroupCommit, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, triggerMs);
        BinaryPrimitives.WriteInt32LittleEndian(p[2..], producerThread);
    }

    public static DurabilityWalGroupCommitData DecodeGroupCommit(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DurabilityWalGroupCommitData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[2..]));
    }

    // ── Queue (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteQueue(Span<byte> destination, byte threadSlot, long timestamp, byte drainAttempt, int dataLen, byte waitReason)
    {
        TraceRecordHeader.WriteCommonHeader(destination, QueueSize, TraceEventKind.DurabilityWalQueue, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = drainAttempt;
        BinaryPrimitives.WriteInt32LittleEndian(p[1..], dataLen);
        p[5] = waitReason;
    }

    public static DurabilityWalQueueData DecodeQueue(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DurabilityWalQueueData(threadSlot, timestamp, p[0],
            BinaryPrimitives.ReadInt32LittleEndian(p[1..]), p[5]);
    }

    // ── Buffer ──
    public static DurabilityWalBufferData DecodeBuffer(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityWalBufferData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }

    // ── Frame (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteFrame(Span<byte> destination, byte threadSlot, long timestamp, ushort frameCount, uint crcStart)
    {
        TraceRecordHeader.WriteCommonHeader(destination, FrameSize, TraceEventKind.DurabilityWalFrame, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(p[2..], crcStart);
    }

    public static DurabilityWalFrameData DecodeFrame(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DurabilityWalFrameData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt32LittleEndian(p[2..]));
    }

    // ── Backpressure ──
    public static DurabilityWalBackpressureData DecodeBackpressure(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityWalBackpressureData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]));
    }
}
