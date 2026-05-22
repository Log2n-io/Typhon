using System;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// Common contract implemented by every typed-event <c>ref struct</c>: the two methods needed to serialize a completed event into a ring-buffer
/// slot. Used only via C# 13 generic constraints with <c>allows ref struct</c> to let <see cref="TyphonEvent.PublishEvent{T}"/> share its
/// Dispose-time publish logic across all event types without boxing or per-event code duplication.
/// </summary>
internal interface ITraceEventEncoder
{
    /// <summary>
    /// The <see cref="TraceEventKind"/> byte this event encodes — used by drop-counting diagnostics so that when
    /// the ring buffer rejects a record we can attribute the drop to the right event type (without reading the
    /// kind from the destination buffer, which we don't have on the failure path). Static-abstract so each event
    /// struct supplies a literal: zero per-call cost, the JIT folds it to a constant.
    /// </summary>
    static abstract byte Kind { get; }

    /// <summary>Total bytes this event will write when serialized.</summary>
    int ComputeSize();

    /// <summary>Serialize this event into <paramref name="destination"/>. <paramref name="endTimestamp"/> is the span-close time.</summary>
    void EncodeTo(Span<byte> destination, long endTimestamp, out int bytesWritten);
}
