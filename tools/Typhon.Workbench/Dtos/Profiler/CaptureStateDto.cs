namespace Typhon.Workbench.Dtos.Profiler;

/// <summary>
/// On-demand tick capture state for a live attach session (#805).
/// </summary>
/// <param name="State">
/// <c>Idle</c> — cherry-pick mode with no window open, only the per-tick skeleton retained.
/// <c>Recording</c> — a bounded window is open.
/// <c>Everything</c> — unbounded capture, the pre-#805 behaviour.
/// </param>
/// <param name="Remaining">Ticks still to record in the current window; 0 when idle or unbounded.</param>
/// <param name="RecordedTicks">Ticks whose detail has been recorded across the whole session. Drives the auto-save guard.</param>
/// <param name="Mode">The capture mode chosen at attach time — <c>Everything</c> or <c>CherryPick</c>.</param>
/// <param name="TickNumbersAbsolute">
/// True once a gauge record has established the mapping from derived to true simulation tick numbers. False means the
/// engine is running without gauges, so the timeline is numbered relative to attach and the UI must say so.
/// </param>
/// <param name="TickNumberingSuspect">
/// True if the absolute tick number stopped agreeing with the derived count — the signature of a lost <c>TickStart</c>.
/// Reported rather than silently corrected: a renumbered timeline that looks plausible is worse than one that admits it.
/// </param>
/// <param name="BytesReceived">Raw record bytes received from the engine, before filtering.</param>
/// <param name="BytesRetained">Record bytes actually retained. The ratio is the feature's measured value.</param>
public record CaptureStateDto(
    string State,
    int Remaining,
    long RecordedTicks,
    string Mode,
    bool TickNumbersAbsolute,
    bool TickNumberingSuspect,
    long BytesReceived,
    long BytesRetained);

/// <summary>Request body for <c>POST /api/sessions/{id}/profiler/capture</c>.</summary>
/// <param name="TickCount">
/// How many consecutive ticks to record in full, starting at the next tick boundary. Zero stops an in-flight capture.
/// The unit is deliberately ticks and not milliseconds: the timeline draws one bar per tick, and Typhon throttles its
/// tick rate under overload — precisely when someone is recording — so a duration would buy an unpredictable number of bars.
/// </param>
public record CaptureRequest(int TickCount);

/// <summary>Request body for <c>POST /api/sessions/{id}/profiler/watch</c>.</summary>
/// <param name="CherryPick">
/// Defaults to <c>true</c> here, the opposite of the endpoint-only Attach flow. Watching is entered from a database
/// whose application is mid-run: you are there to grab a window around something you are about to see, and the complete
/// record is already being written to the database's own <c>profilings/</c> by the engine. Recording everything a
/// second time, over TCP, would duplicate an artifact you are guaranteed to get anyway.
/// </param>
public record WatchRequest(bool CherryPick = true);
