using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using Typhon.Profiler;

namespace Typhon.Engine.Internals;

/// <summary>
/// In-process listener for the <c>Microsoft-Windows-DotNETRuntime</c> EventSource. Subscribes to GC lifecycle events at <c>Informational</c>
/// level with <c>GCKeyword</c> (0x1), translates incoming events into <see cref="GcEventRecord"/> structs, and enqueues them onto a
/// <see cref="GcEventQueue"/> for asynchronous consumption by <see cref="GcIngestionThread"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Cross-platform:</b> the provider name retains its legacy ETW label, but the CoreCLR emits these events identically on Windows, Linux, and
/// macOS through the in-process EventPipe pipeline — the same path <c>dotnet-counters</c> uses on all platforms. No OS-specific code here.
/// </para>
/// <para>
/// <b>Ctor-during-base-ctor footgun:</b> the documented-and-important rule for <see cref="EventListener"/> is that
/// <see cref="OnEventSourceCreated"/> can fire for already-alive sources <i>before</i> our derived constructor body completes. The
/// <c>Microsoft-Windows-DotNETRuntime</c> source is always already alive, so we <i>will</i> see that early callback. The <c>_ready</c>
/// gate pattern ensures we latch the source pointer but defer <see cref="EventListener.EnableEvents(EventSource, EventLevel, EventKeywords)"/> until the
/// queue field is confirmed assigned; the ctor tail then enables once ready.
/// </para>
/// <para>
/// <b>Event-ID dispatch:</b> hot path is a single <c>switch</c> on <see cref="EventWrittenEventArgs.EventId"/>. Only four IDs are handled
/// (1=GCStart, 2=GCEnd, 9=SuspendEEBegin, 3=RestartEEEnd) — all others fall through to a no-op, which is cheap. We intentionally do not
/// subscribe to Verbose level, so <c>GCAllocationTick</c> (ID 10) is not delivered in the first place. <c>GCSuspendEEBegin</c> fires for
/// <i>every</i> execution-engine suspension, not just GC — the EventPipe CPU sample profiler suspends the EE ~1000×/s to walk stacks — so
/// suspend / restart events are filtered to GC-reason suspensions (see <see cref="IsGcSuspendReason"/>); recording the rest would flood the trace.
/// </para>
/// <para>
/// <b>Timestamp source:</b> the event's own <see cref="EventWrittenEventArgs.TimeStamp"/>, taken by the runtime when the event fired, converted
/// to Stopwatch ticks through a (UTC, Stopwatch) anchor pair (<see cref="ToStopwatchTicks(DateTime, long, long, long)"/>), re-latched every <see cref="ReanchorSeconds"/> so a
/// wall-clock step (an NTP correction, a manual change) shifts at most that window's records rather than every later one. Never
/// <see cref="Stopwatch.GetTimestamp()"/> in the callback: runtime events reach an in-process listener through EventPipe's dispatch thread, a
/// managed thread that is itself suspended during the pause, so SuspendEEBegin and RestartEEEnd were both delivered — and stamped — after the
/// restart, back to back. From #243 to 2026-10-27 every recorded GC pause measured ~0.03 ms where the runtime's own trace of the same pauses
/// showed a 7 ms median and a 60 ms max, and each pause sat as late as the dispatch delay put it.
/// </para>
/// </remarks>
internal sealed class GcEventListener : EventListener
{
    private const string DotNetRuntimeSourceName = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;

    private readonly GcEventQueue _queue;
    private const int ReanchorSeconds = 60;

    // Touched only by the listener's dispatch thread (and the ctor, before it is enabled): no synchronisation.
    private long _anchorUtcTicks;
    private long _anchorStopwatch;
    private volatile EventSource _runtimeSource;
    private bool _ready;

    /// <summary>
    /// Latched in <see cref="HandleSuspendBegin"/>: true when the current EE suspension is a GC suspension we recorded, so the unpayloaded
    /// <c>GCRestartEEEnd</c> can be paired to it. EE suspend/restart is globally serialized and never nests, so a single flag is sufficient.
    /// </summary>
    private bool _suspendRecorded;

    public GcEventListener(GcEventQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _queue = queue;
        Reanchor();
        _ready = true;

        // ── Post-base-ctor enable catch-up ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        // Documented .NET contract (MSDN, EventListener): when a new EventListener is instantiated, its BASE ctor enumerates every pre-existing EventSource in
        // the process and invokes OnEventSourceCreated for each — via virtual dispatch into OUR override. Because Microsoft-Windows-DotNETRuntime is created by
        // the CLR at process startup (long before any user code runs), the runtime EventSource ALWAYS pre-exists, and OnEventSourceCreated ALWAYS fires at
        // least once during base()'s execution.
        //
        // When that happens, the override sets _runtimeSource but cannot call EnableEvents — _ready is false (our ctor body hasn't set it yet), so the guard in
        // OnEventSourceCreated suppresses the EnableEvents call. Without the tail below, the listener would hold a reference to the runtime source but never
        // actually subscribe, and no GC events would ever flow. (That symptom — thread slot claimed, zero GcStart/GcEnd/GcSuspension records in the trace — was
        // observed in production when this tail was previously removed based on static-analyzer advice. The analyzer is unable to trace virtual dispatch across
        // the base-class ctor and incorrectly concluded _runtimeSource could not have been written at this point; the empirical behavior contradicts that
        // analysis.)
        //
        // The pragma below silences CA1508 ("avoid dead conditional code"). The condition IS reachable; the analyzer is reasoning about a constructor-local
        // view that happens to miss virtual dispatch into a base-ctor callback..
#pragma warning disable CA1508
        // ReSharper disable once ExpressionIsAlwaysNull
        var source = _runtimeSource;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalse
        // ReSharper disable HeuristicUnreachableCode
        if (source != null)
        {
            EnableEvents(source, EventLevel.Informational, GcKeyword);
        }
        // ReSharper restore HeuristicUnreachableCode
#pragma warning restore CA1508
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == DotNetRuntimeSourceName)
        {
            _runtimeSource = eventSource;
            // _ready gate — two cases this guards:
            // (1) During base ctor (before our derived ctor body runs): _ready is false. We record the source but skip
            //     EnableEvents here; the derived ctor tail (above) does it once _ready has flipped to true. Without this
            //     guard, EnableEvents would fire before _queue was assigned, and the first GC callback could hit a null
            //     reference.
            // (2) During/after Dispose: Dispose() sets _ready=false on teardown, and any in-flight OnEventSourceCreated
            //     callback that lands after must NOT re-subscribe on a dying listener.
            if (_ready)
            {
                EnableEvents(eventSource, EventLevel.Informational, GcKeyword);
            }
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_ready)
        {
            return;
        }

        if (Stopwatch.GetTimestamp() - _anchorStopwatch > ReanchorSeconds * Stopwatch.Frequency)
        {
            Reanchor();
        }

        switch (eventData.EventId)
        {
            case 1: HandleGcStart(eventData); break;
            case 2: HandleGcEnd(eventData); break;
            case 9: HandleSuspendBegin(eventData); break;
            case 3: HandleRestartEnd(eventData); break;
            // All other runtime events are intentionally ignored.
        }
    }

    private void HandleGcStart(EventWrittenEventArgs e)
    {
        // GCStart_V2 payload order: Count(u32), Depth(u32), Reason(u32), Type(u32), ClrInstanceID(u16)
        var payload = e.Payload;
        if (payload == null || payload.Count < 4)
        {
            return;
        }
        var count = Convert.ToUInt32(payload[0]);
        var depth = (byte)Convert.ToUInt32(payload[1]);
        var reason = (byte)Convert.ToUInt32(payload[2]);
        var type = (byte)Convert.ToUInt32(payload[3]);
        _queue.TryEnqueue(GcEventRecord.ForGcStart(ToStopwatchTicks(e.TimeStamp), depth, reason, type, count));
    }

    private void HandleGcEnd(EventWrittenEventArgs e)
    {
        // GCEnd_V1 payload order: Count(u32), Depth(u32), ClrInstanceID(u16)
        var payload = e.Payload;
        if (payload == null || payload.Count < 2)
        {
            return;
        }
        var count = Convert.ToUInt32(payload[0]);
        var depth = (byte)Convert.ToUInt32(payload[1]);
        _queue.TryEnqueue(GcEventRecord.ForGcEnd(ToStopwatchTicks(e.TimeStamp), depth, count));
    }

    private void HandleSuspendBegin(EventWrittenEventArgs e)
    {
        // The event fires for EVERY EE suspension, not just GC (see IsGcSuspendReason) — non-GC suspensions are dropped, and the decision is
        // latched so the unpayloaded GCRestartEEEnd can be paired to it.
        var reason = SuspendReasonOf(e.PayloadNames, e.Payload);
        _suspendRecorded = IsGcSuspendReason(reason);
        if (_suspendRecorded)
        {
            _queue.TryEnqueue(GcEventRecord.ForSuspendBegin(ToStopwatchTicks(e.TimeStamp), reason));
        }
    }

    private void HandleRestartEnd(EventWrittenEventArgs e)
    {
        // GCRestartEEEnd carries no Reason payload, so pair it to the preceding suspend-begin: emit only when that
        // suspension was a GC suspension we recorded. EE suspend/restart is globally serialized and never nests, so
        // the single latched flag always reflects the matching suspend.
        if (_suspendRecorded)
        {
            _queue.TryEnqueue(GcEventRecord.ForRestartEnd(ToStopwatchTicks(e.TimeStamp)));
        }
    }

    private void Reanchor()
    {
        _anchorStopwatch = Stopwatch.GetTimestamp();
        _anchorUtcTicks = DateTime.UtcNow.Ticks;
    }

    private long ToStopwatchTicks(DateTime timeStamp) => ToStopwatchTicks(timeStamp, _anchorUtcTicks, _anchorStopwatch, Stopwatch.Frequency);

    /// <summary>
    /// When the runtime raised an event, in Stopwatch ticks — the trace's clock — from its <see cref="EventWrittenEventArgs.TimeStamp"/>, through an
    /// (UTC ticks, Stopwatch ticks) anchor pair taken at the same instant. A <see cref="DateTimeKind.Local"/> stamp is converted to UTC first.
    /// </summary>
    internal static long ToStopwatchTicks(DateTime timeStamp, long anchorUtcTicks, long anchorStopwatch, long stopwatchFrequency)
    {
        var utcTicks = (timeStamp.Kind == DateTimeKind.Local ? timeStamp.ToUniversalTime() : timeStamp).Ticks;
        return anchorStopwatch + (long)((utcTicks - anchorUtcTicks) * ((double)stopwatchFrequency / TimeSpan.TicksPerSecond));
    }

    /// <summary>
    /// The suspension's reason from a <c>GCSuspendEEBegin_V1</c> payload — read by its field NAME, and by position 0 only when the names are absent.
    /// </summary>
    /// <remarks>
    /// The payload is <c>Reason(u32), Count(u32), ClrInstanceID(u16)</c>, observed on .NET 10 (2026-10-27). Two Microsoft pages disagree on that order,
    /// and this listener followed the wrong one from #243 on: it read <c>payload[1]</c>, the GC COUNT, as the reason. Harmless while every suspension
    /// was recorded; since #351 filtered on the reason, it dropped every GC suspension — no GC pause reached a trace (a count of 1 or 6 aside, by luck).
    /// By name, a schema that moves the field cannot silently do that again.
    /// </remarks>
    internal static byte SuspendReasonOf(IReadOnlyList<string> names, IReadOnlyList<object> payload)
    {
        if (payload == null || payload.Count == 0)
        {
            return 0;
        }

        var index = 0;
        if (names != null)
        {
            index = -1;
            for (var i = 0; i < names.Count; i++)
            {
                if (names[i] == "Reason")
                {
                    index = i;
                    break;
                }
            }
        }

        return index >= 0 && index < payload.Count ? (byte)Convert.ToUInt32(payload[index]) : (byte)0;
    }

    /// <summary>
    /// True when <paramref name="reason"/> (a <see cref="GcSuspendReason"/> value) is a garbage-collection suspension —
    /// <see cref="GcSuspendReason.ForGC"/> or <see cref="GcSuspendReason.ForGCPrep"/>. <c>GCSuspendEEBegin</c> fires for every
    /// execution-engine suspension; the EventPipe CPU sample profiler suspends the EE ~1000×/s with <see cref="GcSuspendReason.Other"/>
    /// to walk stacks, and those must not be recorded as GC events.
    /// </summary>
    internal static bool IsGcSuspendReason(byte reason) =>
        (GcSuspendReason)reason is GcSuspendReason.ForGC or GcSuspendReason.ForGCPrep;

    public override void Dispose()
    {
        var source = _runtimeSource;
        if (source != null)
        {
            DisableEvents(source);
        }
        _ready = false;
        base.Dispose();
    }
}
