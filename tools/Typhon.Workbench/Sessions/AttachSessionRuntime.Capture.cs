using System.Buffers.Binary;
using Typhon.Profiler;
using Typhon.Workbench.Dtos.Profiler;

namespace Typhon.Workbench.Sessions;

/// <summary>
/// On-demand tick capture (#805) — the record filter that sits between the engine's Block frames and the
/// <see cref="IncrementalCacheBuilder"/>, plus the absolute-tick-number offset derived from the same walk.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the filter lives here and not in the engine.</b> Gating at the producer (<c>TyphonEvent.BeginPrologue</c> plus
/// the trace-event source generator) would additionally save the 25–50 ns/span the application's worker threads pay and
/// remove the ring/spillover pressure that makes high-rate workloads drop records. It was rejected on blast radius: it
/// needs a bidirectional control channel, a receive thread on a port bound to <c>IPAddress.Any</c>, and a change to a
/// source generator feeding 204 event kinds. Filtering here changes no engine code and cannot regress a non-Workbench
/// user. See <c>claude/design/Profiler/12-on-demand-tick-capture.md</c> §3.
/// </para>
/// <para>
/// <b>Threading.</b> Every field here except <see cref="_pendingArmTicks"/> is touched only from the read-loop task, which
/// is single-threaded per session. <see cref="_pendingArmTicks"/> is written by HTTP request threads and consumed at a
/// tick boundary, so it moves through <see cref="Interlocked"/>.
/// </para>
/// </remarks>
public sealed partial class AttachSessionRuntime
{
    /// <summary>Sentinel for "capture everything" — the pre-#805 behaviour, and what <see cref="CaptureMode.Everything"/> installs.</summary>
    internal const int AlwaysOnTicks = int.MaxValue;

    /// <summary>Sentinel meaning "no arm request outstanding". Zero is a legal request (it means disarm), so it cannot double as the sentinel.</summary>
    private const int NoPendingArm = -1;

    /// <summary>
    /// Kinds that pass the filter whatever the arm state, indexed by <see cref="TraceEventKind"/> ordinal.
    /// </summary>
    /// <remarks>
    /// The rule, so the set can be re-derived rather than trusted: <i>keep every kind emitted at most once per tick that
    /// feeds <c>TickSummary</c> or the viewer's structural state.</i>
    /// <list type="bullet">
    ///   <item><c>TickStart</c>/<c>TickEnd</c> — the tick spine. Tick numbers are not on the wire; the builder derives
    ///   them by counting <c>TickStart</c> markers, so dropping one silently renumbers every later tick.</item>
    ///   <item><c>SchedulerMetronomeWait</c> — a duration input <i>by side effect</i>: the builder lets its timestamp push
    ///   <c>_currentTickLastTs</c> past <c>TickEnd</c> before sealing. Filtering it makes every idle tick's bar
    ///   systematically shorter than every armed one — a step at the window edge that reads as "the profiler slowed my
    ///   app down when I hit Record". Measured at 50 µs vs 90 µs on the same tick by
    ///   <c>MockRecordFactoryTests.MetronomeWait_ExtendsTickDuration_BeyondTickEnd</c>.</item>
    ///   <item><c>SchedulerOverloadDetector</c> — the consecutive overrun/underrun counters on the summary.</item>
    ///   <item><c>PerTickSnapshot</c> — the gauge strip, whose capacity gauges are emitted on the FIRST snapshot only;
    ///   filtering it while idle at session start would permanently break every gauge percentage. It also carries the
    ///   only absolute tick number on the live wire (see <see cref="TryLearnTickOffset"/>).</item>
    ///   <item><c>ThreadInfo</c> — slot→lane names; without them the viewer cannot lay out tracks.</item>
    /// </list>
    /// <c>QueueTickEnd</c> (244) is deliberately absent: it is one record per <i>(tick × active event queue)</i>, scales
    /// with queue count, and is not a summary input.
    /// </remarks>
    private static readonly bool[] ExemptKinds = BuildExemptKinds();

    /// <summary>Ticks still to record in the current window; <see cref="AlwaysOnTicks"/> means unbounded, 0 means idle.</summary>
    private int _remainingTicks;

    /// <summary>Arm request from an HTTP thread, consumed at the next <c>TickStart</c>. <see cref="NoPendingArm"/> when none.</summary>
    private int _pendingArmTicks = NoPendingArm;

    /// <summary>Whether the tick currently being walked is being recorded in full. Read-loop only.</summary>
    private bool _armedForCurrentTick;

    /// <summary>The capture mode chosen at attach time. Immutable for the session's lifetime.</summary>
    private CaptureMode _captureMode = CaptureMode.Everything;

    /// <summary>Count of <c>TickStart</c> markers passed downstream — mirrors the builder's own derived tick counter exactly.</summary>
    private uint _derivedTickCount;

    /// <summary>
    /// <c>engineTick - derivedTick</c>, learned from the first <see cref="TraceEventKind.PerTickSnapshot"/>. Applied when
    /// projecting tick numbers so the client sees true simulation ticks rather than attach-relative ones.
    /// </summary>
    private long _tickNumberOffset;

    /// <summary>True once <see cref="_tickNumberOffset"/> has been established from a gauge record.</summary>
    /// <remarks>
    /// Written on the read loop, read from HTTP request threads (<see cref="CaptureState"/>) and from
    /// <see cref="ToAbsoluteTick"/> on the chunk-fetch path. That is cross-thread publication outside any lock, so it
    /// goes through <see cref="Volatile"/> per the engine's memory-ordering discipline — acquire/release is free on x64
    /// and is what makes this correct on arm64 rather than accidentally so.
    /// </remarks>
    private bool _tickOffsetKnown;

    /// <summary>Set when the offset drifts — the signature of a dropped <c>TickStart</c>. Surfaced, never silently corrected.</summary>
    private bool _tickNumberingSuspect;

    /// <summary>Total ticks whose detail was recorded across the whole session. Drives the "was anything captured?" auto-save guard.</summary>
    private long _recordedTickCount;

    /// <summary>Bytes seen before filtering, for the per-kind volume census the design calls for.</summary>
    private long _recordBytesReceived;

    /// <summary>Bytes actually handed to the builder.</summary>
    private long _recordBytesRetained;

    private static bool[] BuildExemptKinds()
    {
        var exempt = new bool[256];
        exempt[(int)TraceEventKind.TickStart] = true;
        exempt[(int)TraceEventKind.TickEnd] = true;
        exempt[(int)TraceEventKind.SchedulerMetronomeWait] = true;
        exempt[(int)TraceEventKind.SchedulerOverloadDetector] = true;
        exempt[(int)TraceEventKind.PerTickSnapshot] = true;
        exempt[(int)TraceEventKind.ThreadInfo] = true;
        return exempt;
    }

    /// <summary>Whether a kind survives the filter regardless of arm state.</summary>
    internal static bool IsExemptKind(TraceEventKind kind) => ExemptKinds[(int)kind];

    /// <summary>Current capture state, as surfaced over HTTP and SSE.</summary>
    public CaptureStateDto CaptureState
    {
        get
        {
            var remaining = Volatile.Read(ref _remainingTicks);
            var pending = Volatile.Read(ref _pendingArmTicks);
            // A request that has not reached a tick boundary yet still reads as Recording — the operator pressed Record
            // and the window is committed; showing Idle for up to one tick would look like the click was lost.
            var effective = pending != NoPendingArm ? pending : remaining;
            var state = effective switch
            {
                AlwaysOnTicks => CaptureRunState.Everything,
                <= 0 => CaptureRunState.Idle,
                _ => CaptureRunState.Recording,
            };
            return new CaptureStateDto(
                State: state.ToString(),
                Remaining: effective == AlwaysOnTicks || effective < 0 ? 0 : effective,
                RecordedTicks: Interlocked.Read(ref _recordedTickCount),
                Mode: _captureMode.ToString(),
                TickNumbersAbsolute: Volatile.Read(ref _tickOffsetKnown),
                TickNumberingSuspect: Volatile.Read(ref _tickNumberingSuspect),
                BytesReceived: Interlocked.Read(ref _recordBytesReceived),
                BytesRetained: Interlocked.Read(ref _recordBytesRetained));
        }
    }

    /// <summary>True if at least one tick's detail has been recorded — the auto-save guard for cherry-pick sessions.</summary>
    public bool HasRecordedAnything => Interlocked.Read(ref _recordedTickCount) > 0;

    /// <summary>The capture mode chosen when the session was created.</summary>
    public CaptureMode Mode => _captureMode;

    /// <summary>
    /// Request that the next <paramref name="tickCount"/> ticks be recorded in full. Takes effect at the next
    /// <c>TickStart</c>, never mid-tick, so a window can never contain a partial tick. Pass 0 to stop an in-flight
    /// capture.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The session was attached in <see cref="CaptureMode.Everything"/>. Arming would leave the session reporting a
    /// bounded window while its mode still said "everything" — a state the operator never asked for and the UI cannot
    /// describe. The mode is a choice made at attach; detach and re-attach to change it.
    /// </exception>
    public CaptureStateDto Arm(int tickCount)
    {
        ThrowIfDisposed();
        if (tickCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickCount), "tick count must be zero (stop) or positive");
        }
        if (_captureMode != CaptureMode.CherryPick)
        {
            throw new InvalidOperationException(
                "This session was attached in capture-everything mode. Re-attach in cherry-pick mode to record specific tick windows.");
        }
        Interlocked.Exchange(ref _pendingArmTicks, tickCount);
        LogCaptureArmRequested(tickCount);
        var state = CaptureState;
        PublishCaptureState(state);
        return state;
    }

    /// <summary>Fires whenever the capture state changes — SSE handlers forward it as a <c>captureStateChanged</c> delta.</summary>
    public event Action<CaptureStateDto> CaptureStateChanged;

    /// <summary>Raise the in-process event and fan the same state out to SSE subscribers.</summary>
    private void PublishCaptureState(CaptureStateDto state = null)
    {
        state ??= CaptureState;
        CaptureStateChanged?.Invoke(state);
        BroadcastDelta(new LiveStreamEventDto(Kind: "captureStateChanged", CaptureState: state));
    }

    /// <summary>
    /// Walk <paramref name="records"/>, keeping every record that belongs in the cache and dropping the rest.
    /// Returns the number of bytes written into <paramref name="destination"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filtering is <b>per record, never per block</b>. A Block frame is a timestamp-ordered merge across all thread slots
    /// drained on a 1 ms cadence, so one block can straddle a tick boundary; dropping whole blocks would both leak
    /// pre-arm records and lose post-arm ones.
    /// </para>
    /// <para>
    /// The same walk also maintains the derived tick counter and learns the absolute-tick offset, because both need to
    /// see exactly the records that reach the builder — deriving them anywhere else would let the two drift.
    /// </para>
    /// </remarks>
    private int FilterRecords(ReadOnlySpan<byte> records, Span<byte> destination)
    {
        var pos = 0;
        var written = 0;

        while (pos + MinRecordSize <= records.Length)
        {
            var size = BinaryPrimitives.ReadUInt16LittleEndian(records[pos..]);
            if (size < MinRecordSize || pos + size > records.Length)
            {
                // Malformed tail — mirror the builder's bail-out rather than inventing a different one.
                break;
            }

            var kind = (TraceEventKind)records[pos + 2];

            if (kind == TraceEventKind.TickStart)
            {
                OpenTick();
            }

            var keep = _armedForCurrentTick || ExemptKinds[(int)kind];
            if (keep)
            {
                records.Slice(pos, size).CopyTo(destination[written..]);
                written += size;

                if (kind == TraceEventKind.PerTickSnapshot)
                {
                    TryLearnTickOffset(records.Slice(pos, size));
                }
            }

            pos += size;
        }

        return written;
    }

    /// <summary>
    /// Tick-boundary transition. Consumes any pending arm request, decides whether this tick is recorded, and advances
    /// the derived tick counter. Called for every <c>TickStart</c>, which is always kept, so the counter cannot drift
    /// from the builder's.
    /// </summary>
    private void OpenTick()
    {
        var pending = Interlocked.Exchange(ref _pendingArmTicks, NoPendingArm);
        if (pending != NoPendingArm)
        {
            Volatile.Write(ref _remainingTicks, pending);
        }

        var remaining = Volatile.Read(ref _remainingTicks);
        _armedForCurrentTick = remaining > 0;

        if (_armedForCurrentTick)
        {
            Interlocked.Increment(ref _recordedTickCount);
            if (remaining != AlwaysOnTicks)
            {
                Volatile.Write(ref _remainingTicks, remaining - 1);
                // Notify on every armed tick so the UI badge counts down live, and so the final tick of a window
                // clears it on exactly the tick it closes on.
                PublishCaptureState();
            }
        }

        _derivedTickCount++;
    }

    /// <summary>
    /// Read the absolute tick number a <see cref="TraceEventKind.PerTickSnapshot"/> carries at wire offset 12 — the only
    /// absolute tick number in the live stream — and reconcile it with the derived count.
    /// </summary>
    /// <remarks>
    /// First snapshot establishes the offset. Every later snapshot re-checks it: a mismatch can only mean the derived
    /// count skipped or gained a tick, i.e. a <c>TickStart</c> was dropped somewhere. That is the one silent-corruption
    /// mode this filter could introduce, so it is surfaced as <see cref="_tickNumberingSuspect"/> and logged rather than
    /// quietly re-based.
    /// </remarks>
    private void TryLearnTickOffset(ReadOnlySpan<byte> snapshotRecord)
    {
        if (snapshotRecord.Length < MinRecordSize + 4)
        {
            return;
        }
        var engineTick = BinaryPrimitives.ReadUInt32LittleEndian(snapshotRecord[MinRecordSize..]);
        var offset = (long)engineTick - _derivedTickCount;

        if (!Volatile.Read(ref _tickOffsetKnown))
        {
            // Offset first, flag second: a reader that observes the flag must never see a stale offset.
            Volatile.Write(ref _tickNumberOffset, offset);
            Volatile.Write(ref _tickOffsetKnown, true);
            LogTickOffsetLearned(engineTick, _derivedTickCount, offset);
            return;
        }

        if (offset != Volatile.Read(ref _tickNumberOffset) && !Volatile.Read(ref _tickNumberingSuspect))
        {
            Volatile.Write(ref _tickNumberingSuspect, true);
            LogTickNumberingDrift(engineTick, _derivedTickCount, Volatile.Read(ref _tickNumberOffset), offset);
        }
    }

    /// <summary>
    /// Translate a builder-derived tick number into the engine's absolute one. Applied at the DTO boundary so the client
    /// only ever sees one coordinate system.
    /// </summary>
    internal uint ToAbsoluteTick(uint derivedTick)
    {
        if (!Volatile.Read(ref _tickOffsetKnown))
        {
            return derivedTick;
        }
        var offset = Volatile.Read(ref _tickNumberOffset);
        if (offset == 0)
        {
            return derivedTick;
        }
        var absolute = derivedTick + offset;
        return absolute < 0 ? 0 : (uint)Math.Min(absolute, uint.MaxValue);
    }

    /// <summary>Minimum record size — the 12-byte common header present on every record.</summary>
    private const int MinRecordSize = 12;

    /// <summary>Path of the replay written by <see cref="AutoSaveOnTeardown"/>, or <c>null</c> if nothing was saved.</summary>
    public string AutoSavedPath { get; private set; }

    /// <summary>
    /// Suppresses the teardown auto-save. Set when something else is already guaranteeing a durable artifact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Auto-save exists because a live stream leaves nothing behind: the session's temp cache dies with it, and a
    /// deliberately-recorded window would go with it.
    /// </para>
    /// <para>
    /// <b>Nothing sets this today.</b> It briefly was set for sessions with a database, on the reasoning that the engine
    /// writes a complete capture into that database's <c>profilings/</c> anyway, so a second file would only duplicate
    /// it. That reasoning rested on a defect — a configured live port had been made to force the engine's default file
    /// destination, which is precisely what on-demand capture exists to avoid. With a live-only run writing no file, the
    /// recorded window is the ONLY artifact and suppressing its save discards the operator's armed ticks. The hook stays
    /// for a caller that genuinely guarantees durability by other means; "there is probably a file somewhere" is not
    /// that guarantee.
    /// </para>
    /// </remarks>
    public bool SuppressAutoSave { get; set; }

    /// <summary>
    /// Where <see cref="AutoSaveOnTeardown"/> writes. Null means the machine-local captures directory.
    /// </summary>
    /// <remarks>
    /// A session with a database sets this to that database's <c>profilings/</c>, which is where the Profiles list
    /// looks — an endpoint-only Attach session has no database to co-locate with and keeps the default. This is the
    /// whole difference between a capture the user can reopen and one they have to go find in local-app-data.
    /// </remarks>
    public string AutoSaveDirectory { get; set; }

    /// <summary>Fires when a capture is auto-saved on teardown, carrying the resolved path.</summary>
    public event Action<string> CaptureAutoSaved;

    /// <summary>
    /// Persist the session before it is torn down, so a deliberately-recorded window is never lost to an engine
    /// restart or crash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only in cherry-pick mode, and only if a window was actually recorded.</b> In cherry-pick the retained data is
    /// small, deliberate and precious — losing it is the bad outcome and an unwanted file is a trivial one. In
    /// capture-everything mode the data is large and incidental, and silently writing multiple GB to the user's
    /// local-app-data on every restart is not something a tool should do unasked; that flow prompts instead.
    /// An attach where Record was never pressed leaves nothing behind at all.
    /// </para>
    /// <para>
    /// <b>Ordering is mandatory.</b> <c>Dispose()</c> deletes the temp file and does not flush — the periodic timers do.
    /// So the in-flight chunk and the open tick must be flushed here, before the save reads the manifest, or the most
    /// recent ticks (usually the interesting ones) are missing from the saved replay.
    /// </para>
    /// <para>Best-effort: a failure to save must never take down the read loop or mask the reason the session ended.</para>
    /// </remarks>
    internal void AutoSaveOnTeardown(string reason)
    {
        if (_disposed || SuppressAutoSave || _captureMode != CaptureMode.CherryPick || !HasRecordedAnything || AutoSavedPath != null)
        {
            return;
        }
        if (_builder == null || _tempFile == null || _initialMetadataBytes == null)
        {
            return;
        }

        try
        {
            lock (_builderLock)
            {
                _builder.FlushCurrentChunk();
                _builder.FlushTrailingTick();
                _builder.FlushCurrentChunk();
            }

            var path = CaptureStorage.ResolveAutoSavePath(AutoSaveDirectory);
            var bytes = SaveSessionAsync(path, CancellationToken.None).GetAwaiter().GetResult();
            AutoSavedPath = path;
            LogCaptureAutoSaved(path, bytes, Interlocked.Read(ref _recordedTickCount));
            CaptureAutoSaved?.Invoke(path);

            // Prune only after a successful write, so a budget that is already over cannot delete an older capture in
            // exchange for one we then fail to produce. Retention follows the file: whichever directory it landed in is
            // the one that just grew, and engine-written captures there are untouched because the two policies scan
            // disjoint extensions.
            CaptureStorage.ApplyRetention(path, LogCaptureAutoSaveFailed);
        }
        catch (Exception ex)
        {
            LogCaptureAutoSaveFailed($"{reason}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>How an attach session treats incoming profiler records.</summary>
public enum CaptureMode
{
    /// <summary>Record everything for as long as the session lives — the behaviour of every attach session before #805.</summary>
    Everything = 0,

    /// <summary>Record nothing until the operator arms a window; keep only the per-tick skeleton in between.</summary>
    CherryPick = 1,
}

/// <summary>Coarse capture state, surfaced to the UI.</summary>
public enum CaptureRunState
{
    /// <summary>Cherry-pick mode with no window open — only the exempt per-tick skeleton is retained.</summary>
    Idle = 0,

    /// <summary>A bounded window is open.</summary>
    Recording = 1,

    /// <summary>Capture-everything mode — unbounded.</summary>
    Everything = 2,
}
