using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Unicode;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

// Request correlation emitters (#1517, claude/design/Profiler/13-request-correlation.md). All gated on ProfilerCorrelationActive and reserved on the
// slot's chain, so a full primary ring spills rather than drops.
internal static partial class TyphonEvent
{
    internal static void EmitExternalRequest(long startTimestamp, long endTimestamp, CausalId id, ExternalRequestMethod method, ushort status,
        string route)
    {
        if (!TelemetryConfig.ProfilerCorrelationActive || SuppressedKinds[(int)TraceEventKind.ExternalRequest] || !TryClaim(out var slotIdx, out var slot))
        {
            return;
        }

        // Cut at MaxRouteBytes of UTF-8, not chars: a non-ASCII path encodes to up to 3 bytes a char. FromUtf16 stops at the buffer's end (and on a
        // whole code point) instead of throwing, which keeps this call off the request's failure path.
        Span<byte> routeUtf8 = stackalloc byte[ExternalRequestEventCodec.MaxRouteBytes];
        Utf8.FromUtf16(route ?? string.Empty, routeUtf8, out _, out var routeLength);
        var size = ExternalRequestEventCodec.ComputeSize(!id.IsNone, routeLength);
        if (!TryReserveOnChain(slot, size, (byte)TraceEventKind.ExternalRequest, out var dst, out var ring))
        {
            return;
        }

        ExternalRequestEventCodec.Encode(dst, (byte)slotIdx, startTimestamp, endTimestamp, SpanIdGenerator.NextId(slotIdx, slot), 0, id.Hi, id.Lo,
            method, status, routeUtf8[..routeLength]);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitIngressReceived(long timestamp, uint session, ushort seq, ushort wireIdx, int bytes)
    {
        if (!TelemetryConfig.ProfilerCorrelationActive || !Reserve(TraceEventKind.IngressReceived, out var slotIdx, out var dst, out var ring))
        {
            return;
        }

        CorrelationInstantCodec.WriteReceived(dst, (byte)slotIdx, timestamp, session, seq, wireIdx, (ushort)Math.Min(bytes, ushort.MaxValue));
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitIngressAdmitted(uint session, ushort seq, ushort wireIdx, uint drainTick)
    {
        if (!TelemetryConfig.ProfilerCorrelationActive || !Reserve(TraceEventKind.IngressAdmitted, out var slotIdx, out var dst, out var ring))
        {
            return;
        }

        CorrelationInstantCodec.WriteAdmitted(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), session, seq, wireIdx, drainTick);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitIngressDropped(uint session, ushort seq, IngressDropReason reason)
    {
        if (!TelemetryConfig.ProfilerCorrelationActive || !Reserve(TraceEventKind.IngressDropped, out var slotIdx, out var dst, out var ring))
        {
            return;
        }

        CorrelationInstantCodec.WriteDropped(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), session, seq, reason);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitCommandConsumed(uint session, ushort seq, ushort sysIdx)
    {
        if (!TelemetryConfig.ProfilerCorrelationActive || !Reserve(TraceEventKind.CommandConsumed, out var slotIdx, out var dst, out var ring))
        {
            return;
        }

        CorrelationInstantCodec.WriteConsumed(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), session, seq, sysIdx);
        ring.Publish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitEgressSent(uint session, uint tick, int bytes, int acks)
    {
        if (!TelemetryConfig.ProfilerCorrelationActive || !Reserve(TraceEventKind.EgressSent, out var slotIdx, out var dst, out var ring))
        {
            return;
        }

        CorrelationInstantCodec.WriteSent(dst, (byte)slotIdx, Stopwatch.GetTimestamp(), session, tick, (uint)Math.Max(bytes, 0),
            (ushort)Math.Clamp(acks, 0, ushort.MaxValue));
        ring.Publish();
    }

    private static bool TryClaim(out int slotIdx, out ThreadSlot slot)
    {
        slotIdx = ThreadSlotRegistry.GetOrAssignSlot();
        slot = slotIdx < 0 ? null : ThreadSlotRegistry.GetSlot(slotIdx);
        return slot != null;
    }

    private static bool Reserve(TraceEventKind kind, out int slotIdx, out Span<byte> dst, out TraceRecordRing ring)
    {
        if (SuppressedKinds[(int)kind] || !TryClaim(out slotIdx, out var slot))
        {
            slotIdx = -1;
            dst = default;
            ring = null;
            return false;
        }

        return TryReserveOnChain(slot, CorrelationInstantCodec.SizeOf(kind), (byte)kind, out dst, out ring);
    }
}
