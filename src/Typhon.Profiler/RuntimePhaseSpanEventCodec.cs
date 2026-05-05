using System;
using System.Runtime.CompilerServices;

namespace Typhon.Profiler;

/// <summary>Decoded form of a <see cref="TraceEventKind.RuntimePhaseSpan"/> event.</summary>
public readonly struct RuntimePhaseSpanEventData
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

    /// <summary>Tick lifecycle phase covered by this span (cast to <c>TickPhase</c> at decode time).</summary>
    public byte Phase { get; }

    public bool HasTraceContext => TraceIdHi != 0 || TraceIdLo != 0;

    public RuntimePhaseSpanEventData(byte threadSlot, long startTimestamp, long durationTicks, ulong spanId, ulong parentSpanId,
        ulong traceIdHi, ulong traceIdLo, ushort sourceLocationId, byte phase)
    {
        ThreadSlot = threadSlot;
        StartTimestamp = startTimestamp;
        DurationTicks = durationTicks;
        SpanId = spanId;
        ParentSpanId = parentSpanId;
        TraceIdHi = traceIdHi;
        TraceIdLo = traceIdLo;
        SourceLocationId = sourceLocationId;
        Phase = phase;
    }
}

/// <summary>
/// Wire codec for <see cref="TraceEventKind.RuntimePhaseSpan"/>. Payload: <c>u8 phase</c> (TickPhase enum). The span itself supersedes the
/// previous <c>PhaseStart</c>+<c>PhaseEnd</c> instant pair on the producer side — child spans inside the phase now attach via <c>parentSpanId</c>.
/// </summary>
public static class RuntimePhaseSpanEventCodec
{
    private const int PhaseSize = 1;
    private const int PayloadSize = PhaseSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeSize(bool hasTraceContext)
        => TraceRecordHeader.SpanHeaderSize(hasTraceContext) + PayloadSize;

    public static RuntimePhaseSpanEventData Decode(ReadOnlySpan<byte> source)
    {
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);
        TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
            out var durationTicks, out var spanId, out var parentSpanId, out var spanFlags);

        var hasTraceContext = (spanFlags & TraceRecordHeader.SpanFlagsHasTraceContext) != 0;
        var hasSourceLocation = (spanFlags & TraceRecordHeader.SpanFlagsHasSourceLocation) != 0;
        ulong traceIdHi = 0, traceIdLo = 0;
        if (hasTraceContext)
        {
            TraceRecordHeader.ReadTraceContext(source[TraceRecordHeader.MinSpanHeaderSize..], out traceIdHi, out traceIdLo);
        }
        ushort sourceLocationId = 0;
        if (hasSourceLocation)
        {
            sourceLocationId = TraceRecordHeader.ReadSourceLocationId(source[TraceRecordHeader.SourceLocationIdOffset(hasTraceContext)..]);
        }

        var headerSize = TraceRecordHeader.SpanHeaderSize(hasTraceContext, hasSourceLocation);
        var phase = source[headerSize];

        return new RuntimePhaseSpanEventData(threadSlot, startTimestamp, durationTicks, spanId, parentSpanId, traceIdHi, traceIdLo, sourceLocationId, phase);
    }
}
