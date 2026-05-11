using System;

// Keep this namespace, Typhon.Generators uses it to declare partial members of TraceEventDecoder
// ReSharper disable once CheckNamespace
namespace Typhon.Profiler.Events;

/// <summary>
/// Hand-glue partial-method implementations for the wire shapes that don't fit the generator's standard
/// template. As of 2026-05-10 this file holds only the catch-all fallback — every <c>[TraceEvent]</c> kind's
/// wire layout is now owned by the generator (slot-padding for shared slots is expressed via
/// <c>[Optional(WireSize=…)]</c>, and kind-conditional payload slots are now per-kind ref structs).
/// </summary>
public static partial class TraceEventDecoder
{
    // ─────────────────────────────────────────────────────────────────────
    // Fallback for kinds without a [TraceEvent] declaration
    // ─────────────────────────────────────────────────────────────────────
    //
    // Instant kinds (TickStart, TickEnd, PhaseStart/End, SystemReady/Skipped, etc.) don't have a
    // [TraceEvent]-decorated ref struct on the producer side — they're written through
    // InstantEventCodec directly. They surface as OtherTraceEventDto with the original kind preserved
    // numerically. Forward-compat new kinds (added after this consumer was built) follow the same path
    // so the dispatch stays graceful instead of dropping records.

    internal static partial TraceEventDto HandGlue_DecodeFallback(TraceEventKind kind, ReadOnlySpan<byte> source, int currentTick, long ticksPerUs)
    {
        // Read the common header — every record carries it regardless of kind. After that we can't decode
        // payload generically (each kind has its own layout), so we surface what we have.
        TraceRecordHeader.ReadCommonHeader(source, out _, out _, out var threadSlot, out var startTimestamp);

        bool isSpan;
        try
        {
            isSpan = kind.IsSpan();
        }
        catch
        {
            // Unknown kind value (forward-compat) — IsSpan() may not classify it. Treat as instant.
            isSpan = false;
        }

        double? durationUs = null;
        if (isSpan && source.Length >= TraceRecordHeader.MinSpanHeaderSize)
        {
            TraceRecordHeader.ReadSpanHeaderExtension(source[TraceRecordHeader.CommonHeaderSize..],
                out var durationTicks, out _, out _, out _);
            durationUs = durationTicks / (double)ticksPerUs;
        }

        return new OtherTraceEventDto
        {
            ThreadSlot = threadSlot,
            TickNumber = currentTick,
            TimestampUs = startTimestamp / (double)ticksPerUs,
            OriginalKind = (int)kind,
            IsSpan = isSpan,
            DurationUs = durationUs,
        };
    }
}
