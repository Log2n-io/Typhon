using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Typhon.Profiler;

/// <summary>The HTTP method of an <see cref="TraceEventKind.ExternalRequest"/>, as one byte so a request records no method string.</summary>
public enum ExternalRequestMethod : byte
{
    /// <summary>A method not listed here.</summary>
    Other = 0,
    /// <summary>GET.</summary>
    Get = 1,
    /// <summary>POST.</summary>
    Post = 2,
    /// <summary>PUT.</summary>
    Put = 3,
    /// <summary>DELETE.</summary>
    Delete = 4,
    /// <summary>PATCH.</summary>
    Patch = 5,
    /// <summary>HEAD.</summary>
    Head = 6,
    /// <summary>OPTIONS.</summary>
    Options = 7,
}

/// <summary>Why a client command never reached a system (<see cref="TraceEventKind.IngressDropped"/>).</summary>
public enum IngressDropReason : byte
{
    /// <summary>Unspecified.</summary>
    Unknown = 0,
    /// <summary>The session's ingress ring was full.</summary>
    RingFull = 1,
    /// <summary>The session was past its command rate.</summary>
    RateLimited = 2,
    /// <summary>The message did not decode.</summary>
    Malformed = 3,
    /// <summary>The command was built in a realm its session has since left.</summary>
    RealmChanged = 4,
    /// <summary>The transport refused it (rejected, forbidden, an invalid region, or an application reason).</summary>
    Rejected = 5,
    /// <summary>A newer command of a coalesced type from the same session replaced it in the tick's batch before any system read it.</summary>
    Coalesced = 6,
}

/// <summary>Decoded <see cref="TraceEventKind.ExternalRequest"/>.</summary>
public readonly struct ExternalRequestEventData
{
    /// <summary>Producer thread slot.</summary>
    public byte ThreadSlot { get; init; }
    /// <summary>Request start, Stopwatch ticks.</summary>
    public long StartTimestamp { get; init; }
    /// <summary>Request duration, Stopwatch ticks.</summary>
    public long DurationTicks { get; init; }
    /// <summary>The span's id.</summary>
    public ulong SpanId { get; init; }
    /// <summary>High half of the request's W3C trace id: the causal id its engine spans carry.</summary>
    public ulong TraceIdHi { get; init; }
    /// <summary>Low half of the request's W3C trace id.</summary>
    public ulong TraceIdLo { get; init; }
    /// <summary>The HTTP method.</summary>
    public ExternalRequestMethod Method { get; init; }
    /// <summary>The response status code.</summary>
    public ushort Status { get; init; }
    /// <summary>The endpoint's route template, or the path when no endpoint matched.</summary>
    public string Route { get; init; }
}

/// <summary>
/// Wire codec for <see cref="TraceEventKind.ExternalRequest"/>: a span record (12-byte common header, 25-byte span extension, the 16-byte trace
/// context whenever the trace id is non-zero) followed by <c>method: u8, status: u16, routeByteCount: u16, route: utf8</c>.
/// </summary>
public static class ExternalRequestEventCodec
{
    /// <summary>Longest route the record keeps, in UTF-8 bytes; a longer one is cut.</summary>
    public const int MaxRouteBytes = 128;

    private const int PayloadPrefix = 1 + 2 + 2;

    /// <summary>Wire size for a route of <paramref name="routeByteCount"/> bytes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext, int routeByteCount) =>
        TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadPrefix + Math.Min(routeByteCount, MaxRouteBytes);

    /// <summary>Encodes one record into <paramref name="destination"/>, which must be <see cref="ComputeSize"/> bytes.</summary>
    public static void Encode(Span<byte> destination, byte threadSlot, long startTimestamp, long endTimestamp, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ExternalRequestMethod method, ushort status, ReadOnlySpan<byte> routeUtf8)
    {
        var hasTrace = (traceIdHi | traceIdLo) != 0;
        var route = routeUtf8.Length > MaxRouteBytes ? routeUtf8[..MaxRouteBytes] : routeUtf8;
        var size = ComputeSize(hasTrace, route.Length);
        TraceRecordHeader.WriteCommonHeader(destination, (ushort)size, TraceEventKind.ExternalRequest, threadSlot, startTimestamp);
        TraceRecordHeader.WriteSpanHeaderExtension(destination[TraceRecordHeader.CommonHeaderSize..], endTimestamp - startTimestamp, spanId,
            parentSpanId, hasTrace ? TraceRecordHeader.SpanFlagsHasTraceContext : (byte)0);
        if (hasTrace)
        {
            TraceRecordHeader.WriteTraceContext(destination[TraceRecordHeader.MinSpanHeaderSize..], traceIdHi, traceIdLo);
        }

        var p = destination[TraceRecordHeader.SpanHeaderSize(hasTrace)..];
        p[0] = (byte)method;
        BinaryPrimitives.WriteUInt16LittleEndian(p[1..], status);
        BinaryPrimitives.WriteUInt16LittleEndian(p[3..], (ushort)route.Length);
        route.CopyTo(p[PayloadPrefix..]);
    }

    /// <summary>Decodes one record.</summary>
    public static ExternalRequestEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var slot, out var start);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..], out var duration, out var spanId, out _, out var flags);
        var hasTrace = (flags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        ulong hi = 0, lo = 0;
        if (hasTrace)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out hi, out lo);
        }

        var p = source[TraceRecordHeader.SpanHeaderSize(hasTrace)..];
        var routeLength = BinaryPrimitives.ReadUInt16LittleEndian(p[3..]);
        return new ExternalRequestEventData
        {
            ThreadSlot = slot,
            StartTimestamp = start,
            DurationTicks = duration,
            SpanId = spanId,
            TraceIdHi = hi,
            TraceIdLo = lo,
            Method = (ExternalRequestMethod)p[0],
            Status = BinaryPrimitives.ReadUInt16LittleEndian(p[1..]),
            Route = Encoding.UTF8.GetString(p.Slice(PayloadPrefix, routeLength)),
        };
    }
}

/// <summary>
/// A decoded correlation lifecycle instant (<see cref="TraceEventKind.IngressReceived"/> through <see cref="TraceEventKind.EgressSent"/>). Fields a
/// kind does not carry are zero.
/// </summary>
public readonly struct CorrelationInstantData
{
    /// <summary>Which of the five kinds.</summary>
    public TraceEventKind Kind { get; init; }
    /// <summary>Producer thread slot.</summary>
    public byte ThreadSlot { get; init; }
    /// <summary>When, Stopwatch ticks.</summary>
    public long Timestamp { get; init; }
    /// <summary>The session's raw id.</summary>
    public uint Session { get; init; }
    /// <summary>The command's sequence number (all but <see cref="TraceEventKind.EgressSent"/>).</summary>
    public ushort Seq { get; init; }
    /// <summary>The command type's wire index (Received, Admitted).</summary>
    public ushort WireIdx { get; init; }
    /// <summary>Message bytes (Received, Sent).</summary>
    public uint Bytes { get; init; }
    /// <summary>The tick: the drain tick (Admitted) or the frame's tick (Sent).</summary>
    public uint Tick { get; init; }
    /// <summary>The consuming system's index (Consumed).</summary>
    public ushort SysIdx { get; init; }
    /// <summary>Why it was refused (Dropped).</summary>
    public IngressDropReason Reason { get; init; }
    /// <summary>Commands acknowledged in the frame (Sent).</summary>
    public ushort Acks { get; init; }
}

/// <summary>
/// Wire codec for the five correlation lifecycle instants, kinds 236-240. Each is the 12-byte common header followed by a fixed payload:
/// <code>
/// IngressReceived  session u32, seq u16, wireIdx u16, bytes u16        (10 B)
/// IngressAdmitted  session u32, seq u16, wireIdx u16, drainTick u32    (12 B)
/// IngressDropped   session u32, seq u16, reason u8                     (7 B)
/// CommandConsumed  session u32, seq u16, sysIdx u16                    (8 B)
/// EgressSent       session u32, tick u32, bytes u32, acks u16          (14 B)
/// </code>
/// </summary>
public static class CorrelationInstantCodec
{
    /// <summary>Wire size of <paramref name="kind"/>, or 0 for a kind this codec does not cover.</summary>
    public static int SizeOf(TraceEventKind kind) => TraceRecordHeader.CommonHeaderSize + kind switch
    {
        TraceEventKind.IngressReceived => 10,
        TraceEventKind.IngressAdmitted => 12,
        TraceEventKind.IngressDropped => 7,
        TraceEventKind.CommandConsumed => 8,
        TraceEventKind.EgressSent => 14,
        _ => -TraceRecordHeader.CommonHeaderSize,
    };

    /// <summary>True for kinds 236-240.</summary>
    public static bool Covers(TraceEventKind kind) => kind is >= TraceEventKind.IngressReceived and <= TraceEventKind.EgressSent;

    /// <summary>Encodes <see cref="TraceEventKind.IngressReceived"/>.</summary>
    public static void WriteReceived(Span<byte> d, byte slot, long ts, uint session, ushort seq, ushort wireIdx, ushort bytes)
    {
        Header(d, TraceEventKind.IngressReceived, slot, ts, session, seq);
        BinaryPrimitives.WriteUInt16LittleEndian(d[18..], wireIdx);
        BinaryPrimitives.WriteUInt16LittleEndian(d[20..], bytes);
    }

    /// <summary>Encodes <see cref="TraceEventKind.IngressAdmitted"/>.</summary>
    public static void WriteAdmitted(Span<byte> d, byte slot, long ts, uint session, ushort seq, ushort wireIdx, uint drainTick)
    {
        Header(d, TraceEventKind.IngressAdmitted, slot, ts, session, seq);
        BinaryPrimitives.WriteUInt16LittleEndian(d[18..], wireIdx);
        BinaryPrimitives.WriteUInt32LittleEndian(d[20..], drainTick);
    }

    /// <summary>Encodes <see cref="TraceEventKind.IngressDropped"/>.</summary>
    public static void WriteDropped(Span<byte> d, byte slot, long ts, uint session, ushort seq, IngressDropReason reason)
    {
        Header(d, TraceEventKind.IngressDropped, slot, ts, session, seq);
        d[18] = (byte)reason;
    }

    /// <summary>Encodes <see cref="TraceEventKind.CommandConsumed"/>.</summary>
    public static void WriteConsumed(Span<byte> d, byte slot, long ts, uint session, ushort seq, ushort sysIdx)
    {
        Header(d, TraceEventKind.CommandConsumed, slot, ts, session, seq);
        BinaryPrimitives.WriteUInt16LittleEndian(d[18..], sysIdx);
    }

    /// <summary>Encodes <see cref="TraceEventKind.EgressSent"/>.</summary>
    public static void WriteSent(Span<byte> d, byte slot, long ts, uint session, uint tick, uint bytes, ushort acks)
    {
        TraceRecordHeader.WriteCommonHeader(d, (ushort)SizeOf(TraceEventKind.EgressSent), TraceEventKind.EgressSent, slot, ts);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], session);
        BinaryPrimitives.WriteUInt32LittleEndian(d[16..], tick);
        BinaryPrimitives.WriteUInt32LittleEndian(d[20..], bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(d[24..], acks);
    }

    /// <summary>Decodes any of the five kinds.</summary>
    public static CorrelationInstantData Decode(ReadOnlySpan<byte> s)
    {
        TraceRecordHeader.ReadCommonHeader(s, out _, out var kind, out var slot, out var ts);
        var session = BinaryPrimitives.ReadUInt32LittleEndian(s[12..]);
        return kind switch
        {
            TraceEventKind.IngressReceived => new CorrelationInstantData
            {
                Kind = kind, ThreadSlot = slot, Timestamp = ts, Session = session, Seq = U16(s, 16), WireIdx = U16(s, 18), Bytes = U16(s, 20),
            },
            TraceEventKind.IngressAdmitted => new CorrelationInstantData
            {
                Kind = kind, ThreadSlot = slot, Timestamp = ts, Session = session, Seq = U16(s, 16), WireIdx = U16(s, 18),
                Tick = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
            },
            TraceEventKind.IngressDropped => new CorrelationInstantData
            {
                Kind = kind, ThreadSlot = slot, Timestamp = ts, Session = session, Seq = U16(s, 16), Reason = (IngressDropReason)s[18],
            },
            TraceEventKind.CommandConsumed => new CorrelationInstantData
            {
                Kind = kind, ThreadSlot = slot, Timestamp = ts, Session = session, Seq = U16(s, 16), SysIdx = U16(s, 18),
            },
            TraceEventKind.EgressSent => new CorrelationInstantData
            {
                Kind = kind, ThreadSlot = slot, Timestamp = ts, Session = session, Tick = BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
                Bytes = BinaryPrimitives.ReadUInt32LittleEndian(s[20..]), Acks = U16(s, 24),
            },
            _ => throw new ArgumentException($"{kind} is not a correlation instant", nameof(s)),
        };
    }

    /// <summary>
    /// The causal id of a client command: <c>hi = "TYC" marker | session</c>, <c>lo = drainTick &lt;&lt; 16 | seq</c>. Unique per session and tick although
    /// <c>seq</c> is 16 bits and wraps; never zero, so it never reads as "no causal id".
    /// </summary>
    public static (ulong Hi, ulong Lo) CommandCausalId(uint session, uint drainTick, ushort seq) =>
        (CommandMarker | session, ((ulong)drainTick << 16) | seq);

    /// <summary>The marker in the high half of a command's causal id.</summary>
    public const ulong CommandMarker = 0x5459_4300_0000_0000UL;

    /// <summary>True when <paramref name="hi"/> is the high half of a command's causal id rather than a W3C trace id.</summary>
    public static bool IsCommandCausalId(ulong hi) => (hi & 0xFFFF_FF00_0000_0000UL) == CommandMarker;

    private static void Header(Span<byte> d, TraceEventKind kind, byte slot, long ts, uint session, ushort seq)
    {
        TraceRecordHeader.WriteCommonHeader(d, (ushort)SizeOf(kind), kind, slot, ts);
        BinaryPrimitives.WriteUInt32LittleEndian(d[12..], session);
        BinaryPrimitives.WriteUInt16LittleEndian(d[16..], seq);
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);
}
