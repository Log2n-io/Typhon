using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

[PublicAPI]
public readonly struct DurabilityRecoveryStartData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long CheckpointLsn { get; }
    public byte Reason { get; }
    public DurabilityRecoveryStartData(byte ts, long t, long lsn, byte r)
    { ThreadSlot = ts; Timestamp = t; CheckpointLsn = lsn; Reason = r; }
}

[PublicAPI]
public readonly struct DurabilityRecoveryDiscoverData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int SegCount { get; }
    public long TotalBytes { get; }
    public int FirstSegId { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityRecoveryDiscoverData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int sc, long tb, int fsi)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; SegCount = sc; TotalBytes = tb; FirstSegId = fsi; }
}

[PublicAPI]
public readonly struct DurabilityRecoverySegmentData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int SegId { get; }
    public int RecCount { get; }
    public long Bytes { get; }
    public byte Truncated { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityRecoverySegmentData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int si, int rc, long b, byte tr)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; SegId = si; RecCount = rc; Bytes = b; Truncated = tr; }
}

[PublicAPI]
public readonly struct DurabilityRecoveryRecordData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte ChunkType { get; }
    public long Lsn { get; }
    public int Size { get; }
    public DurabilityRecoveryRecordData(byte ts, long t, byte ct, long lsn, int sz)
    { ThreadSlot = ts; Timestamp = t; ChunkType = ct; Lsn = lsn; Size = sz; }
}

[PublicAPI]
public readonly struct DurabilityRecoveryFpiData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int FpiCount { get; }
    public int RepairedCount { get; }
    public int Mismatches { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityRecoveryFpiData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int fc, int rc, int mm)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; FpiCount = fc; RepairedCount = rc; Mismatches = mm; }
}

[PublicAPI]
public readonly struct DurabilityRecoveryRedoData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int RecordsReplayed { get; }
    public int UowsReplayed { get; }
    public uint DurUs { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityRecoveryRedoData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int rr, int ur, uint du)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; RecordsReplayed = rr; UowsReplayed = ur; DurUs = du; }
}

[PublicAPI]
public readonly struct DurabilityRecoveryUndoData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int VoidedUowCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityRecoveryUndoData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int vuc)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; VoidedUowCount = vuc; }
}

[PublicAPI]
public readonly struct DurabilityRecoveryTickFenceData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int TickFenceCount { get; }
    public int Entries { get; }
    public long TickNumber { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public DurabilityRecoveryTickFenceData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int tfc, int e, long tn)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; TickFenceCount = tfc; Entries = e; TickNumber = tn; }
}

public static class DurabilityRecoveryEventCodec
{
    public const int StartSize  = TraceRecordHeader.CommonHeaderSize + 8 + 1;     // 21
    public const int RecordSize = TraceRecordHeader.CommonHeaderSize + 1 + 8 + 4; // 25

    private const int DiscoverPayload   = 4 + 8 + 4;       // 16
    private const int SegmentPayload    = 4 + 4 + 8 + 1;   // 17
    private const int FpiPayload        = 4 + 4 + 4;       // 12
    private const int RedoPayload       = 4 + 4 + 4;       // 12
    private const int UndoPayload       = 4;               // 4
    private const int TickFencePayload  = 4 + 4 + 8;       // 16

    public static int ComputeSizeDiscover(bool hasTC)  => TraceRecordHeader.SpanHeaderSize(hasTC) + DiscoverPayload;
    public static int ComputeSizeSegment(bool hasTC)   => TraceRecordHeader.SpanHeaderSize(hasTC) + SegmentPayload;
    public static int ComputeSizeFpi(bool hasTC)       => TraceRecordHeader.SpanHeaderSize(hasTC) + FpiPayload;
    public static int ComputeSizeRedo(bool hasTC)      => TraceRecordHeader.SpanHeaderSize(hasTC) + RedoPayload;
    public static int ComputeSizeUndo(bool hasTC)      => TraceRecordHeader.SpanHeaderSize(hasTC) + UndoPayload;
    public static int ComputeSizeTickFence(bool hasTC) => TraceRecordHeader.SpanHeaderSize(hasTC) + TickFencePayload;

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

    // ── Start (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteStart(Span<byte> destination, byte threadSlot, long timestamp, long checkpointLsn, byte reason)
    {
        TraceRecordHeader.WriteCommonHeader(destination, StartSize, TraceEventKind.DurabilityRecoveryStart, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, checkpointLsn);
        p[8] = reason;
    }

    public static DurabilityRecoveryStartData DecodeStart(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DurabilityRecoveryStartData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p), p[8]);
    }

    // ── Discover (span) ──
    public static DurabilityRecoveryDiscoverData DecodeDiscover(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityRecoveryDiscoverData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt64LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[12..]));
    }

    // ── Segment (span) ──
    public static DurabilityRecoverySegmentData DecodeSegment(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityRecoverySegmentData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(p[8..]),
            p[16]);
    }

    // ── Record (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRecord(Span<byte> destination, byte threadSlot, long timestamp, byte chunkType, long lsn, int size)
    {
        TraceRecordHeader.WriteCommonHeader(destination, RecordSize, TraceEventKind.DurabilityRecoveryRecord, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = chunkType;
        BinaryPrimitives.WriteInt64LittleEndian(p[1..], lsn);
        BinaryPrimitives.WriteInt32LittleEndian(p[9..], size);
    }

    public static DurabilityRecoveryRecordData DecodeRecord(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new DurabilityRecoveryRecordData(threadSlot, timestamp, p[0],
            BinaryPrimitives.ReadInt64LittleEndian(p[1..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[9..]));
    }

    // ── FPI (span) ──
    public static DurabilityRecoveryFpiData DecodeFpi(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityRecoveryFpiData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(p[8..]));
    }

    // ── Redo (span) ──
    public static DurabilityRecoveryRedoData DecodeRedo(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityRecoveryRedoData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[8..]));
    }

    // ── Undo (span) ──
    public static DurabilityRecoveryUndoData DecodeUndo(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityRecoveryUndoData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p));
    }

    // ── TickFence (span) ──
    public static DurabilityRecoveryTickFenceData DecodeTickFence(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new DurabilityRecoveryTickFenceData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(p[8..]));
    }
}
