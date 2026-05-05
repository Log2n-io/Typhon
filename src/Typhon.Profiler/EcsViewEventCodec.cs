using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

[PublicAPI]
public readonly struct EcsViewRefreshPullData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public uint QueryNs { get; }
    public ushort ArchetypeMaskBits { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public EcsViewRefreshPullData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, uint qn, ushort amb)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; QueryNs = qn; ArchetypeMaskBits = amb; }
}

[PublicAPI]
public readonly struct EcsViewIncrementalDrainData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int DeltaCount { get; }
    public byte Overflow { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public EcsViewIncrementalDrainData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int dc, byte ovf)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; DeltaCount = dc; Overflow = ovf; }
}

[PublicAPI]
public readonly struct EcsViewDeltaBufferOverflowData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long CurrentTsn { get; }
    public long TailTsn { get; }
    public ushort MarginPagesLost { get; }
    public EcsViewDeltaBufferOverflowData(byte ts, long t, long cur, long tail, ushort mpl)
    { ThreadSlot = ts; Timestamp = t; CurrentTsn = cur; TailTsn = tail; MarginPagesLost = mpl; }
}

[PublicAPI]
public readonly struct EcsViewProcessEntryData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Pk { get; }
    public ushort FieldIdx { get; }
    public byte Pass { get; }
    public EcsViewProcessEntryData(byte ts, long t, long pk, ushort fi, byte pass)
    { ThreadSlot = ts; Timestamp = t; Pk = pk; FieldIdx = fi; Pass = pass; }
}

[PublicAPI]
public readonly struct EcsViewProcessEntryOrData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Pk { get; }
    public byte BranchCount { get; }
    public uint BitmapDelta { get; }
    public EcsViewProcessEntryOrData(byte ts, long t, long pk, byte bc, uint bd)
    { ThreadSlot = ts; Timestamp = t; Pk = pk; BranchCount = bc; BitmapDelta = bd; }
}

[PublicAPI]
public readonly struct EcsViewRefreshFullData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int OldCount { get; }
    public int NewCount { get; }
    public uint RequeryNs { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public EcsViewRefreshFullData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int oc, int nc, uint rn)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; OldCount = oc; NewCount = nc; RequeryNs = rn; }
}

[PublicAPI]
public readonly struct EcsViewRefreshFullOrData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public int OldCount { get; }
    public int NewCount { get; }
    public byte BranchCount { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public EcsViewRefreshFullOrData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, int oc, int nc, byte bc)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; OldCount = oc; NewCount = nc; BranchCount = bc; }
}

[PublicAPI]
public readonly struct EcsViewRegistryData
{
    public TraceEventKind Kind { get; }
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort ViewId { get; }
    public ushort FieldIdx { get; }
    public ushort RegCount { get; }
    public EcsViewRegistryData(TraceEventKind k, byte ts, long t, ushort vi, ushort fi, ushort rc)
    { Kind = k; ThreadSlot = ts; Timestamp = t; ViewId = vi; FieldIdx = fi; RegCount = rc; }
}

[PublicAPI]
public readonly struct EcsViewDeltaCacheMissData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public long Pk { get; }
    public byte Reason { get; }
    public EcsViewDeltaCacheMissData(byte ts, long t, long pk, byte r)
    { ThreadSlot = ts; Timestamp = t; Pk = pk; Reason = r; }
}

public static class EcsViewEventCodec
{
    public const int DeltaBufferOverflowSize = TraceRecordHeader.CommonHeaderSize + 8 + 8 + 2;       // 30
    public const int ProcessEntrySize        = TraceRecordHeader.CommonHeaderSize + 8 + 2 + 1;       // 23
    public const int ProcessEntryOrSize      = TraceRecordHeader.CommonHeaderSize + 8 + 1 + 4;       // 25
    public const int RegistrySize            = TraceRecordHeader.CommonHeaderSize + 2 + 2 + 2;       // 18
    public const int DeltaCacheMissSize      = TraceRecordHeader.CommonHeaderSize + 8 + 1;           // 21

    private const int RefreshPullPayload      = 4 + 2;            // 6
    private const int IncrementalDrainPayload = 4 + 1;            // 5
    private const int RefreshFullPayload      = 4 + 4 + 4;        // 12
    private const int RefreshFullOrPayload    = 4 + 4 + 1;        // 9

    public static int ComputeSizeRefreshPull(bool hasTC)      => TraceRecordHeader.SpanHeaderSize(hasTC) + RefreshPullPayload;
    public static int ComputeSizeIncrementalDrain(bool hasTC) => TraceRecordHeader.SpanHeaderSize(hasTC) + IncrementalDrainPayload;
    public static int ComputeSizeRefreshFull(bool hasTC)      => TraceRecordHeader.SpanHeaderSize(hasTC) + RefreshFullPayload;
    public static int ComputeSizeRefreshFullOr(bool hasTC)    => TraceRecordHeader.SpanHeaderSize(hasTC) + RefreshFullOrPayload;

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

    // ── RefreshPull (span) ──
    public static EcsViewRefreshPullData DecodeRefreshPull(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new EcsViewRefreshPullData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt32LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]));
    }

    // ── IncrementalDrain (span) ──
    public static EcsViewIncrementalDrainData DecodeIncrementalDrain(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new EcsViewIncrementalDrainData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p), p[4]);
    }

    // ── DeltaBufferOverflow (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDeltaBufferOverflow(Span<byte> destination, byte threadSlot, long timestamp, long currentTsn, long tailTsn, ushort marginPagesLost)
    {
        TraceRecordHeader.WriteCommonHeader(destination, DeltaBufferOverflowSize, TraceEventKind.EcsViewDeltaBufferOverflow, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, currentTsn);
        BinaryPrimitives.WriteInt64LittleEndian(p[8..], tailTsn);
        BinaryPrimitives.WriteUInt16LittleEndian(p[16..], marginPagesLost);
    }

    public static EcsViewDeltaBufferOverflowData DecodeDeltaBufferOverflow(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new EcsViewDeltaBufferOverflowData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadInt64LittleEndian(p[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[16..]));
    }

    // ── ProcessEntry (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteProcessEntry(Span<byte> destination, byte threadSlot, long timestamp, long pk, ushort fieldIdx, byte pass)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ProcessEntrySize, TraceEventKind.EcsViewProcessEntry, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, pk);
        BinaryPrimitives.WriteUInt16LittleEndian(p[8..], fieldIdx);
        p[10] = pass;
    }

    public static EcsViewProcessEntryData DecodeProcessEntry(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new EcsViewProcessEntryData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[8..]),
            p[10]);
    }

    // ── ProcessEntryOr (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteProcessEntryOr(Span<byte> destination, byte threadSlot, long timestamp, long pk, byte branchCount, uint bitmapDelta)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ProcessEntryOrSize, TraceEventKind.EcsViewProcessEntryOr, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, pk);
        p[8] = branchCount;
        BinaryPrimitives.WriteUInt32LittleEndian(p[9..], bitmapDelta);
    }

    public static EcsViewProcessEntryOrData DecodeProcessEntryOr(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new EcsViewProcessEntryOrData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p),
            p[8],
            BinaryPrimitives.ReadUInt32LittleEndian(p[9..]));
    }

    // ── RefreshFull (span) ──
    public static EcsViewRefreshFullData DecodeRefreshFull(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new EcsViewRefreshFullData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(p[8..]));
    }

    // ── RefreshFullOr (span) ──
    public static EcsViewRefreshFullOrData DecodeRefreshFullOr(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new EcsViewRefreshFullOrData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadInt32LittleEndian(p),
            BinaryPrimitives.ReadInt32LittleEndian(p[4..]),
            p[8]);
    }

    // ── Registry Register/Deregister (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRegistryRegister(Span<byte> destination, byte threadSlot, long timestamp, ushort viewId, ushort fieldIdx, ushort regCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, RegistrySize, TraceEventKind.EcsViewRegistryRegister, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, viewId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], fieldIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], regCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteRegistryDeregister(Span<byte> destination, byte threadSlot, long timestamp, ushort viewId, ushort fieldIdx, ushort regCount)
    {
        TraceRecordHeader.WriteCommonHeader(destination, RegistrySize, TraceEventKind.EcsViewRegistryDeregister, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, viewId);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], fieldIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(p[4..], regCount);
    }

    public static EcsViewRegistryData DecodeRegistry(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out var kind, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new EcsViewRegistryData(kind, threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[4..]));
    }

    // ── DeltaCacheMiss (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteDeltaCacheMiss(Span<byte> destination, byte threadSlot, long timestamp, long pk, byte reason)
    {
        TraceRecordHeader.WriteCommonHeader(destination, DeltaCacheMissSize, TraceEventKind.EcsViewDeltaCacheMiss, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteInt64LittleEndian(p, pk);
        p[8] = reason;
    }

    public static EcsViewDeltaCacheMissData DecodeDeltaCacheMiss(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new EcsViewDeltaCacheMissData(threadSlot, timestamp,
            BinaryPrimitives.ReadInt64LittleEndian(p), p[8]);
    }
}
