using JetBrains.Annotations;
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

[PublicAPI]
public readonly struct EcsQueryConstructData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public ushort TargetArchId { get; }
    public byte Polymorphic { get; }
    public byte MaskSize { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public EcsQueryConstructData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, ushort tai, byte poly, byte mask)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; TargetArchId = tai; Polymorphic = poly; MaskSize = mask; }
}

[PublicAPI]
public readonly struct EcsQueryMaskAndData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort BitsBefore { get; }
    public ushort BitsAfter { get; }
    public byte OpType { get; }
    public EcsQueryMaskAndData(byte ts, long t, ushort bb, ushort ba, byte op)
    { ThreadSlot = ts; Timestamp = t; BitsBefore = bb; BitsAfter = ba; OpType = op; }
}

[PublicAPI]
public readonly struct EcsQuerySubtreeExpandData
{
    public byte ThreadSlot { get; }
    public long StartTimestamp { get; }
    public long DurationTicks { get; }
    public ulong SpanId { get; }
    public ulong ParentSpanId { get; }
    public ulong TraceIdHi { get; }
    public ulong TraceIdLo { get; }
    public ushort SubtreeCount { get; }
    public ushort RootId { get; }
    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;
    public EcsQuerySubtreeExpandData(byte ts, long sts, long dur, ulong sid, ulong psid, ulong thi, ulong tlo, ushort sc, ushort ri)
    { ThreadSlot = ts; StartTimestamp = sts; DurationTicks = dur; SpanId = sid; ParentSpanId = psid;
      TraceIdHi = thi; TraceIdLo = tlo; SubtreeCount = sc; RootId = ri; }
}

[PublicAPI]
public readonly struct EcsQueryConstraintEnabledData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public ushort TypeId { get; }
    public byte EnableBit { get; }
    public EcsQueryConstraintEnabledData(byte ts, long t, ushort ti, byte eb)
    { ThreadSlot = ts; Timestamp = t; TypeId = ti; EnableBit = eb; }
}

[PublicAPI]
public readonly struct EcsQuerySpatialAttachData
{
    public byte ThreadSlot { get; }
    public long Timestamp { get; }
    public byte SpatialType { get; }
    public float QueryBoxX1 { get; }
    public float QueryBoxY1 { get; }
    public float QueryBoxX2 { get; }
    public float QueryBoxY2 { get; }
    public EcsQuerySpatialAttachData(byte ts, long t, byte st, float x1, float y1, float x2, float y2)
    { ThreadSlot = ts; Timestamp = t; SpatialType = st; QueryBoxX1 = x1; QueryBoxY1 = y1; QueryBoxX2 = x2; QueryBoxY2 = y2; }
}

public static class EcsQueryDepthEventCodec
{
    public const int MaskAndSize           = TraceRecordHeader.CommonHeaderSize + 2 + 2 + 1;       // 17
    public const int ConstraintEnabledSize = TraceRecordHeader.CommonHeaderSize + 2 + 1;           // 15
    public const int SpatialAttachSize     = TraceRecordHeader.CommonHeaderSize + 1 + 4 * 4;       // 29

    private const int ConstructPayload     = 2 + 1 + 1;     // 4
    private const int SubtreeExpandPayload = 2 + 2;          // 4
    public static int ComputeSizeConstruct(bool hasTC)     => TraceRecordHeader.SpanHeaderSize(hasTC) + ConstructPayload;
    public static int ComputeSizeSubtreeExpand(bool hasTC) => TraceRecordHeader.SpanHeaderSize(hasTC) + SubtreeExpandPayload;

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

    // ── Construct (span) ──
    public static EcsQueryConstructData DecodeConstruct(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new EcsQueryConstructData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt16LittleEndian(p), p[2], p[3]);
    }

    // ── MaskAnd (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteMaskAnd(Span<byte> destination, byte threadSlot, long timestamp, ushort bitsBefore, ushort bitsAfter, byte opType)
    {
        TraceRecordHeader.WriteCommonHeader(destination, MaskAndSize, TraceEventKind.EcsQueryMaskAnd, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, bitsBefore);
        BinaryPrimitives.WriteUInt16LittleEndian(p[2..], bitsAfter);
        p[4] = opType;
    }

    public static EcsQueryMaskAndData DecodeMaskAnd(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new EcsQueryMaskAndData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]),
            p[4]);
    }

    // ── SubtreeExpand (span) ──
    public static EcsQuerySubtreeExpandData DecodeSubtreeExpand(ReadOnlySpan<byte> source)
    {
        var p = ReadSpanPreamble(source, out var ts, out var sts, out var dur, out var sid, out var psid, out var thi, out var tlo);
        return new EcsQuerySubtreeExpandData(ts, sts, dur, sid, psid, thi, tlo,
            BinaryPrimitives.ReadUInt16LittleEndian(p),
            BinaryPrimitives.ReadUInt16LittleEndian(p[2..]));
    }

    // ── ConstraintEnabled (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteConstraintEnabled(Span<byte> destination, byte threadSlot, long timestamp, ushort typeId, byte enableBit)
    {
        TraceRecordHeader.WriteCommonHeader(destination, ConstraintEnabledSize, TraceEventKind.EcsQueryConstraintEnabled, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        BinaryPrimitives.WriteUInt16LittleEndian(p, typeId);
        p[2] = enableBit;
    }

    public static EcsQueryConstraintEnabledData DecodeConstraintEnabled(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new EcsQueryConstraintEnabledData(threadSlot, timestamp,
            BinaryPrimitives.ReadUInt16LittleEndian(p), p[2]);
    }

    // ── SpatialAttach (instant) ──
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteSpatialAttach(Span<byte> destination, byte threadSlot, long timestamp,
        byte spatialType, float qbX1, float qbY1, float qbX2, float qbY2)
    {
        TraceRecordHeader.WriteCommonHeader(destination, SpatialAttachSize, TraceEventKind.EcsQuerySpatialAttach, threadSlot, timestamp);
        var p = destination[TraceRecordHeader.CommonHeaderSize..];
        p[0] = spatialType;
        BinaryPrimitives.WriteSingleLittleEndian(p[1..], qbX1);
        BinaryPrimitives.WriteSingleLittleEndian(p[5..], qbY1);
        BinaryPrimitives.WriteSingleLittleEndian(p[9..], qbX2);
        BinaryPrimitives.WriteSingleLittleEndian(p[13..], qbY2);
    }

    public static EcsQuerySpatialAttachData DecodeSpatialAttach(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var timestamp);
        var p = source[TraceRecordHeader.CommonHeaderSize..];
        return new EcsQuerySpatialAttachData(threadSlot, timestamp, p[0],
            BinaryPrimitives.ReadSingleLittleEndian(p[1..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[5..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[9..]),
            BinaryPrimitives.ReadSingleLittleEndian(p[13..]));
    }
}
