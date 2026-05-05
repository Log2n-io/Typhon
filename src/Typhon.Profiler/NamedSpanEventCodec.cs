using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Typhon.Profiler;

/// <summary>
/// Decoded form of a <see cref="TraceEventKind.NamedSpan"/> — the fallback shape for call sites that need a dynamic/runtime string name instead
/// of a compile-time typed event. The only payload is a length-prefixed UTF-8 byte sequence.
/// </summary>
/// <remarks>
/// Used sparingly — typed events are cheaper because they avoid the string-encoding cost. Intended for test code, <c>MonitoringDemo</c>'s
/// interpolated span names, and genuine user-code ad-hoc markers.
/// </remarks>
public readonly struct NamedSpanEventData
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

    /// <summary>UTF-8-encoded name bytes, sliced from the original record. Caller converts to string on demand.</summary>
    public ReadOnlyMemory<byte> NameUtf8 { get; }

    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public NamedSpanEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, ReadOnlyMemory<byte> nameUtf8)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        SourceLocationId = sourceLocationId;
        NameUtf8 = nameUtf8;
    }

    /// <summary>Decode <see cref="NameUtf8"/> as a <see cref="string"/>. Allocates.</summary>
    public string GetName() => Encoding.UTF8.GetString(NameUtf8.Span);
}

/// <summary>
/// Wire codec for <see cref="TraceEventKind.NamedSpan"/>. Payload: <c>u16 nameByteCount</c>, <c>byte[nameByteCount] nameUtf8</c>.
/// </summary>
/// <remarks>
/// <b>No ref struct producer for this kind</b> — callers that need a dynamic-name span pass a <c>string</c> to
/// <c>TyphonEvent.BeginNamedSpan</c> (Phase 2 addition) which encodes the name at span begin time and stores the byte count in the
/// returned scope. Encoding UTF-8 at begin time avoids having to hold the string managed reference in a ref struct, which is fine for this
/// rarely-hit path.
/// </remarks>
public static class NamedSpanEventCodec
{
    private const int NameLengthSize = 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext, int nameByteCount)
        => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + NameLengthSize + nameByteCount;

    /// <summary>
    /// Encode a named-span record. Caller has pre-computed <paramref name="nameUtf8"/> (via <c>Encoding.UTF8.GetBytes</c>).
    /// </summary>
    public static void Encode(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        ReadOnlySpan<byte> nameUtf8, out int bytesWritten)
    {
        if (nameUtf8.Length > ushort.MaxValue)
        {
            throw new ArgumentException($"NamedSpan name too long: {nameUtf8.Length} bytes (max {ushort.MaxValue})", nameof(nameUtf8));
        }

        var hasTraceContext = traceIdHi != 0 || traceIdLo != 0;
        var size = ComputeSize(hasTraceContext, nameUtf8.Length);

        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.NamedSpan, threadSlot, startTimestamp);
        var spanFlags = hasTraceContext ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0;
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..],
            endTimestamp - startTimestamp, spanId, parentSpanId, spanFlags);

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext);
        if (hasTraceContext)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }

        var payload = destination[headerSize..];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload, (ushort)nameUtf8.Length);
        nameUtf8.CopyTo(payload[NameLengthSize..]);

        bytesWritten = size;
    }

    /// <summary>Convenience — encode from a <see cref="string"/>. Allocates a UTF-8 buffer. Not intended for hot paths.</summary>
    public static void Encode(Span<byte> destination, long endTimestamp, byte threadSlot, long startTimestamp,
        ulong spanId, ulong parentSpanId, ulong traceIdHi, ulong traceIdLo,
        string name, out int bytesWritten)
    {
        Span<byte> nameBuffer = stackalloc byte[512];
        int byteCount;
        if (Encoding.UTF8.GetByteCount(name) <= nameBuffer.Length)
        {
            byteCount = Encoding.UTF8.GetBytes(name, nameBuffer);
            Encode(destination, endTimestamp, threadSlot, startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo,
                nameBuffer[..byteCount], out bytesWritten);
        }
        else
        {
            var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(name.Length));
            try
            {
                byteCount = Encoding.UTF8.GetBytes(name, rented);
                Encode(destination, endTimestamp, threadSlot, startTimestamp, spanId, parentSpanId, traceIdHi, traceIdLo,
                    rented.AsSpan(0, byteCount), out bytesWritten);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public static NamedSpanEventData Decode(ReadOnlyMemory<byte> source)
    {
        var span = source.Span;
        TraceRecordHeader.ReadCommonHeader(span, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(span[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(span[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(span[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var payload = span[headerSize..];
        var nameByteCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var nameMemory = source.Slice(headerSize + NameLengthSize, nameByteCount);

        return new NamedSpanEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId, nameMemory);
    }
}

