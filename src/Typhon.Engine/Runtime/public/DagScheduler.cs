using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Typhon.Profiler;

namespace Typhon.Engine;

/// <summary>
/// Production DAG scheduler for the Typhon Runtime. Executes a static system DAG on a pool of worker threads, with any-worker dispatch and
/// inline continuation.
/// </summary>
/// <remarks>
/// <para>
/// Derives from <see cref="HighResolutionTimerServiceBase"/> to leverage its three-phase wait (Sleep → Yield → Spin), self-calibrated sleep threshold,
/// timing error tracking, and <see cref="ResourceNode"/> lifecycle.
/// </para>
/// <para>
/// The timer thread acts as a metronome: it resets per-tick state, bumps the generation counter (waking workers), and waits for tick completion.
/// All dispatch decisions happen on workers (POC decision D2: any-worker dispatch, no scheduler thread).
/// </para>
/// <para>
/// Between ticks, each worker blocks on its own wake event — a <see cref="ManualResetEventSlim"/> constructed with <c>spinCount: 0</c> — inside a loop that
/// re-checks <c>_tickGeneration</c>, with a 50 ms timeout as a shutdown-liveness backstop. The three-phase wait (Sleep → Yield → Spin) belongs to the timer
/// thread's metronome in <see cref="HighResolutionTimerServiceBase"/>, not to the workers; <c>_nextTickTimestamp</c> is likewise metronome state, read by
/// <c>GetNextTick</c> and never by a worker.
/// </para>
/// </remarks>
[PublicAPI]
public sealed partial class DagScheduler : HighResolutionTimerServiceBase
{
    // ═══════════════════════════════════════════════════════════════
    // Immutable DAG structure
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// All registered systems across every track and DAG, indexed by system index. Used by the scheduler for dispatch, lookup, and topological reasoning — the
    /// index identity is load-bearing for chunk dispatch state and dependency wiring, so this array stays as the canonical source for everything index-keyed
    /// (including diagnostics/profiler that walk every entry by raw index).
    /// </summary>
    /// <remarks>
    /// When you only care about systems the user registered (counting them in a test, iterating them in a DAG-viewer that hides engine plumbing), prefer
    /// <see cref="UserSystems"/> — it filters out systems whose track carries the <see cref="Track.EngineTag"/> (e.g. the Fence DAG) so the count is stable
    /// regardless of which engine-internal DAGs the runtime registers.
    /// </remarks>
    public SystemDefinition[] Systems { get; }

    /// <summary>
    /// The runtime partitioning hierarchy — tracks in execution order, each carrying its DAGs. Static for the scheduler's lifetime.
    /// </summary>
    public IReadOnlyList<Track> Tracks => _tracks;

    /// <summary>
    /// User-registered systems only (filtered <see cref="Systems"/> with engine-tagged-track entries removed). Indices in this view do NOT match the canonical
    /// system index — use this only for iteration and counting, not for index-keyed lookups.
    /// </summary>
    public SystemDefinition[] UserSystems
    {
        get
        {
            if (field != null)
            {
                return field;
            }

            var arr = new SystemDefinition[SystemCount];
            int j = 0;
            for (int i = 0; i < AllSystemCount; i++)
            {
                if (!_systemIsEngine[i])
                {
                    arr[j++] = Systems[i];
                }
            }
            field = arr;
            return arr;
        }
    }

    private readonly Track[] _tracks;                       // public-facing track objects, in execution order
    private readonly ScheduledTrack[] _scheduledTracks;     // per-track dispatch state (roots + members), parallel to _tracks
    private readonly int _deferredTrackStartIndex;          // tracks [this, count) are dispatched by the runtime, not the in-tick loop
    private readonly int[] _systemTrackIndex;               // per-system → owning track index
    private readonly bool[] _systemIsEngine;                // per-system → owning track carries the engine tag
    private readonly int _workerCount;
    private readonly RuntimeOptions _options;
    private readonly int[] _topologicalOrder;
    private readonly EventQueueBase[] _eventQueues;

    /// <summary>Per-track dispatch state: the track's root systems (zero predecessors) and its full member set.</summary>
    private sealed class ScheduledTrack
    {
        public string Name;
        public bool IsEngine;

        /// <summary>Whether a system failing on this track latches the terminal fence-failure verdict. See <see cref="Track.FailureIsTerminal"/>.</summary>
        public bool FailureIsTerminal = true;

        public int[] Roots = [];
        public int[] Members = [];
        public int MemberCount => Members.Length;
    }

    // ═══════════════════════════════════════════════════════════════
    // Per-system mutable state (reset each tick)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-system claim word: the live dispatch's chunk count in the high 32 bits, the next chunk to hand out in the low 32. A worker claims with ONE
    /// <c>Interlocked.Increment</c> and gets back both halves of the same dispatch, so whether a chunk is its to run is decided by the claim itself (CD-01).
    /// </summary>
    /// <remarks>
    /// <para><b>Why one word.</b> Workers are not fenced out between dispatches: a parallel system's ready flag stays set once it completes, and a worker can
    /// be preempted anywhere in its claim loop and resume one or more dispatches later. With the count and the next index held apart, three windows let such a
    /// worker run a chunk of a dispatch it was never part of — each reproduced in <c>ChunkClaimStragglerTests</c>:</para>
    /// <list type="bullet">
    ///   <item>the ready flag read in one tick and a counter reset to 0 read in the next: the SWG x64/w16 aborts on <c>CellClusterPool</c>'s single-writer
    ///         detector, a stale fence chunk allocating or freeing a cluster under the next tick's systems;</item>
    ///   <item>an increment past the end of one dispatch judged against the NEXT dispatch's larger count: a chunk run twice, and the system completed one
    ///         chunk early;</item>
    ///   <item>a drainer that cached a failed dispatch's count swallowing chunks of the next dispatch.</item>
    /// </list>
    /// <para>With one word a claim either names a chunk of the dispatch whose word it incremented — a legitimate claim, whatever the worker did before — or
    /// answers "nothing left".</para>
    /// <para><b>Open and closed.</b> <see cref="ResetTickState"/> stores 0, a count of 0: closed. A dispatch publishes its word as its LAST store
    /// (<see cref="OpenChunkClaims"/>, a release after <c>_remainingChunks</c> and the prepared state). Nothing needs to close the word between two dispatches
    /// in one tick: a dispatch completes only after every chunk has been claimed, so the word it leaves behind is exhausted and refuses every claim.</para>
    /// </remarks>
    private readonly CacheLinePaddedLong[] _claims;

    private readonly CacheLinePaddedInt[] _remainingChunks;
    private readonly CacheLinePaddedInt[] _remainingDeps;
    private readonly CacheLinePaddedInt[] _isReady;
    private readonly bool[] _systemFailed;

    // Reset templates (immutable after construction)
    private readonly int[] _templateDeps;
    private readonly int[] _templateChunks;

    // ═══════════════════════════════════════════════════════════════
    // Strict tick abort (#567) — RuntimeOptions.SystemExceptionPolicy = AbortTickAndStop
    // ═══════════════════════════════════════════════════════════════

    // Terminal abort latch. 0 = running, 1 = a fatal system exception cancelled a tick and this scheduler is dead.
    //
    // NOT per-tick state, despite living next to the arrays above: `AbortTickAndStop` is terminal by design, so this is deliberately excluded from
    // ResetTickState / the `Array.Clear(_systemFailed)` sites. Clearing it would silently resume ticking on a simulation whose systems only partly ran.
    //
    // Padded because every worker reads it on the dispatch path. It is written exactly once in the scheduler's lifetime, so the read is a shared-clean hit in
    // steady state; padding keeps that line from being invalidated by the hot counters either side of it.
    private CacheLinePaddedInt _tickAborted;

    // Fence-failure latch (#890). Separate from _tickAborted because the two are different verdicts: an application system throwing under AbortTickAndStop
    // cancels the REST of the tick, while an engine-track system throwing means the work that ENDS the tick did not finish — there is nothing left to cancel
    // and, per design/Runtime/08-strict-tick-abort.md D3, it is reported as its own TickOutcomeReason rather than as an abort.
    private CacheLinePaddedInt _fenceFailed;

    // First-failure record, written by whichever thread wins the CAS on _fenceFailed. Same publication argument as the abort's: the latch is taken first, and
    // the detail is read at tick end, after the tick has drained through a barrier every worker passes.
    private int _fenceFailedSystemIndex = -1;
    private Exception _fenceFailedException;
    private long _fenceFailedTickNumber;

    // First-failure record, written by whichever thread wins the CAS on _tickAborted — see TryRecordTickAbort for why the latch is taken before these are
    // written, and why that ordering is safe.
    private int _abortedSystemIndex = -1;
    private Exception _abortedException;
    private long _abortedTickNumber;

    private readonly SystemExceptionPolicy _exceptionPolicy;

    /// <summary>
    /// True once a fatal system exception has aborted a tick under <see cref="SystemExceptionPolicy.AbortTickAndStop"/>.
    /// Terminal — never cleared. No further ticks execute.
    /// </summary>
    public bool IsTickAborted => Volatile.Read(ref _tickAborted.Value) != 0;

    /// <summary>
    /// Records the first fatal system failure of a tick and latches the terminal abort, exactly once across all workers.
    /// No-op unless the policy is <see cref="SystemExceptionPolicy.AbortTickAndStop"/> and the system belongs to an application track — a throw on an engine
    /// track is a different class (rule D3) and never aborts a tick.
    /// </summary>
    /// <returns>True if THIS call latched the abort (the caller is the first-failure recorder).</returns>
    private bool TryRecordTickAbort(int sysIdx, Exception ex)
    {
        if (_exceptionPolicy != SystemExceptionPolicy.AbortTickAndStop)
        {
            return false;
        }
        if ((uint)sysIdx < (uint)_systemIsEngine.Length && _systemIsEngine[sysIdx])
        {
            return false;
        }

        // Latch FIRST, then record. The CAS elects exactly one winner (AC11); writing the detail before it would let racing losers stomp the winner's record.
        // The inverse hazard — a reader seeing the latch before the detail is written — cannot bite: the dispatch gates only ever test the latch, never the
        // detail, and the detail is read (via AbortedOutcome) at tick end, after `_systemsRemaining` has drained to zero. That drain is a barrier every
        // worker passes through, so it orders the winner's stores ahead of any read of them.
        if (Interlocked.CompareExchange(ref _tickAborted.Value, 1, 0) != 0)
        {
            return false;   // another worker got there first — its failure is THE first failure
        }

        _abortedSystemIndex = sysIdx;
        _abortedException = ex;
        _abortedTickNumber = _currentTickNumber;
        return true;
    }

    /// <summary>
    /// The single funnel for "a system failed with this exception": logs it, captures it for <see cref="DumpHangDiagnostic"/>, and —
    /// under <see cref="SystemExceptionPolicy.AbortTickAndStop"/> — latches the terminal tick abort. Every catch site in the system-execution paths calls this
    /// and nothing else, so a new catch site cannot accidentally opt out of one of the three.
    /// </summary>
    private void RecordSystemFailure(int sysIdx, string systemName, Exception ex)
    {
        LogSystemException(sysIdx, systemName, ex);
        CaptureSystemException(sysIdx, ex);
        TryRecordTickAbort(sysIdx, ex);
        RecordEngineTrackFailure(sysIdx, systemName, ex);
    }

    /// <summary>
    /// An engine-track system failed: latch the tick's fence-failure verdict and tell the host, whichever gate caught it — <c>ShouldRun</c>, <c>Prepare</c>
    /// or <c>Execute</c>, on any dispatch path (#890).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this sits in the funnel and not at the catch sites.</b> Before #890 only ONE catch site — the WorkerLoop's outer safety net — invoked
    /// <see cref="UnhandledExceptionCallback"/>, so a fence phase whose <c>Prepare</c> threw was recorded in per-system telemetry, had every successor
    /// skipped as <see cref="SkipReason.DependencyFailed"/>, and reached the host through nothing at all. That is what happened for #889's
    /// <c>NullReferenceException</c>: Prep ran and then no WAL emit, no dormancy sweep and no dirty-ring archive, on every tick with dirty data, while the
    /// process kept ticking. TP-01a's reasoning applies unchanged — a fence that did not finish leaves cluster pages mutated, dirty and un-logged, which the
    /// checkpoint then persists on its own schedule.
    /// </para>
    /// <para>
    /// <b>Application systems are deliberately untouched.</b> Their failures keep the documented contract: the per-system handlers log and capture, and the
    /// callback stays reserved for what escaped every inner handler. Widening it to every user-system throw is a separate decision with a much larger blast
    /// radius, and <see cref="SkipReason.Exception"/> already carries that detail.
    /// </para>
    /// </remarks>
    private void RecordEngineTrackFailure(int sysIdx, string systemName, Exception ex)
    {
        if ((uint)sysIdx >= (uint)_systemIsEngine.Length || !_systemIsEngine[sysIdx])
        {
            return;
        }

        // Not every engine track's failure is terminal. The latch below stops the runtime for good, which is the correct response to a fence that did not
        // finish — its half-written pages are dirty and un-logged, and every further tick adds more. It is the wrong response to engine work with no
        // durability role: the Engine-Subscriptions track writes only RAM-only replication blocks, so a throw there endangers nothing a later tick compounds,
        // and stopping the database over it would make a bug in the newest subsystem the most destructive kind of bug there is. Such a track still logs, still
        // captures, and still reaches the host through the callback below — it simply does not take the engine with it.
        if (_scheduledTracks[_systemTrackIndex[sysIdx]].FailureIsTerminal)
        {
            // Latch before the detail, exactly as TryRecordTickAbort does and for the same reason: the CAS elects one recorder and racing losers must not
            // stomp it.
            if (Interlocked.CompareExchange(ref _fenceFailed.Value, 1, 0) == 0)
            {
                _fenceFailedSystemIndex = sysIdx;
                _fenceFailedException = ex;
                _fenceFailedTickNumber = _currentTickNumber;
            }
        }

        // Fired per failure rather than once per tick: a second engine phase failing for a second reason is a second thing the host has to be told about.
        try { UnhandledExceptionCallback?.Invoke(sysIdx, systemName, ex); }
        catch { /* swallow — the callback itself threw; we are the last line of defence */ }
    }

    /// <summary>
    /// The fence failed on the tick DRIVER rather than inside a phase — the serial <c>WriteTickFence</c>, or the parallel fence's serial prep (#890).
    /// </summary>
    /// <remarks>
    /// Same latch, same callback, same terminal gate as a phase failure: the host's question is "did the fence finish", and which thread the answer came
    /// from is not part of it. <c>sysIdx</c> is -1 because no system owns this failure, which is also what the tick driver's own safety net reports.
    /// </remarks>
    internal void RecordFenceDriverFailure(Exception ex)
    {
        LogSystemException(-1, FenceDriverName, ex);
        if (Interlocked.CompareExchange(ref _fenceFailed.Value, 1, 0) == 0)
        {
            _fenceFailedSystemIndex = -1;
            _fenceFailedException = ex;
            _fenceFailedTickNumber = _currentTickNumber;
        }

        try { UnhandledExceptionCallback?.Invoke(-1, FenceDriverName, ex); }
        catch { /* swallow — the callback itself threw; we are the last line of defence */ }
    }

    /// <summary>What <see cref="RecordFenceDriverFailure"/> reports as the failing system's name.</summary>
    internal const string FenceDriverName = "<tick fence>";

    /// <summary>
    /// True once an engine-track (Fence-DAG) system has thrown. Terminal — never cleared; the tick that follows one is not a tick that completed.
    /// </summary>
    public bool IsFenceFailed => Volatile.Read(ref _fenceFailed.Value) != 0;

    /// <summary>The outcome describing the fence failure. Only meaningful once <see cref="IsFenceFailed"/> is true.</summary>
    /// <remarks>
    /// The barrier is not decoration. The abort's twin justifies its plain reads by "the tick drained through a barrier every worker passes", but that drain
    /// is the plain-load spin at <c>DispatchTrackMultiThreaded</c>, which EQ-02 names as exactly what a reader may not lean on under arm64 — and this outcome
    /// is read one drain later than the abort's, not a whole tick later. Acquiring <c>_fenceFailed</c> orders nothing useful either: the writer sets the latch
    /// BEFORE the detail. So the detail reads get the fence, JIT-folded away on x64 (<c>X86Base.IsSupported</c> is a constant there).
    /// </remarks>
    internal TickOutcome FenceFailureOutcome
    {
        get
        {
            if (!X86Base.IsSupported)
            {
                Interlocked.MemoryBarrier();
            }

            var idx = _fenceFailedSystemIndex;
            // -1 is the driver's own failure, which has a name but no system; a host reading the outcome should not have to special-case it.
            var name = (uint)idx < (uint)Systems.Length ? Systems[idx].Name : FenceDriverName;
            return new TickOutcome(_fenceFailedTickNumber, TickOutcomeReason.FenceFailure, idx, name, _fenceFailedException);
        }
    }

    /// <summary>
    /// Marks a system cancelled by a tick abort and drives its completion so the tick's countdown still drains.
    /// Cancellation in Typhon is dispatch-and-skip, never halt-dispatch: whoever claims the system must complete it, or
    /// <c>_systemsRemaining</c> never reaches zero and the tick hangs.
    /// </summary>
    private void SkipSystemForTickAbort(int sysIdx, int workerId, bool trackUtilization)
    {
        _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.TickAborted;
        InspectorSystemSkipped(sysIdx, SkipReason.TickAborted, Stopwatch.GetTimestamp());
        OnSystemComplete(sysIdx, workerId, trackUtilization);
    }

    /// <summary>
    /// Cancels a parallel / pipeline system whose chunk 0 this worker just claimed as the tick aborted. Claiming chunk 0 is the "this system begins" decision
    /// and it is atomic — the counter hands 0 to exactly one worker — so the abort is decided once per system (rule D1: granularity is the system, never the chunk).
    /// </summary>
    /// <remarks>
    /// <c>_systemFailed</c> is set first so a peer whose claim comes back after it diverts into the drain instead of running the chunk. A peer that read the
    /// flag before it was set runs its chunk — that is in-flight work, which finishes by design. The tick is partially applied by construction anyway (systems
    /// that ran before the failure committed), so a stray chunk is inside the envelope the policy already accepts, not a new failure mode.
    /// </remarks>
    private bool AbortSystemFromChunkZero(int sysIdx, int workerId, bool trackUtilization, int totalChunks)
    {
        _systemFailed[sysIdx] = true;
        _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.TickAborted;
        InspectorSystemSkipped(sysIdx, SkipReason.TickAborted, Stopwatch.GetTimestamp());

        // Account for the chunk 0 we claimed but will not run; true when it was the only chunk. Chunks 1..N-1 then drain through the claim loop's
        // failed-system path (correction C2): a not-yet-started system must still drive its counter to zero, one claim at a time like any failed system.
        return DrainClaimedChunk(sysIdx, workerId, trackUtilization, totalChunks);
    }

    /// <summary>The outcome describing the abort. Only meaningful once <see cref="IsTickAborted"/> is true.</summary>
    internal TickOutcome AbortedOutcome
    {
        get
        {
            var idx = _abortedSystemIndex;
            var name = (uint)idx < (uint)Systems.Length ? Systems[idx].Name : null;
            return new TickOutcome(_abortedTickNumber, TickOutcomeReason.SystemException, idx, name, _abortedException);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Workers
    // ═══════════════════════════════════════════════════════════════

    private readonly Thread[] _workers;

    // ═══════════════════════════════════════════════════════════════
    // Tick synchronization
    // ═══════════════════════════════════════════════════════════════

    private int _tickGeneration;

    /// <summary>
    /// <see cref="_tickGeneration"/> as <see cref="Start"/> found it, before any dispatch: every worker starts from it, so a worker whose thread first runs
    /// after the first bump still sees that dispatch as new. Published to the workers by <see cref="Thread.Start()"/>.
    /// </summary>
    private int _generationAtStart;
    private int _tickInProgress;
    /// <summary>
    /// Set once to stop the worker pool. Every access goes through <see cref="Volatile"/> — this is the stop signal two SPIN loops key on (the worker's
    /// within-tick dispatch loop and the timer thread's completion barrier in <c>DispatchTrackMultiThreaded</c>), and a plain read inside a spin loop may be
    /// hoisted out of it by the JIT once tiered compilation optimises the method, after which the thread can never observe the store. On arm64 the plain
    /// store carries no ordering guarantee either. Cheap: acquire/release are plain movs on x64, and this is read once per spin iteration, never per element.
    /// </summary>
    private int _workerShutdown;

    /// <summary>Whether <see cref="Shutdown"/> or <see cref="Dispose(bool)"/> has asked the workers to stop. For tests that must hold a tick open across it.</summary>
    internal bool IsShutdownRequested => Volatile.Read(ref _workerShutdown) != 0;

    /// <summary>
    /// How long the tick-completion barrier tolerates a STALL — no system completing at all — after shutdown has been requested, before abandoning the tick.
    /// Not a cap on tick duration: the timer resets on every completion, so an actively-progressing tick is never cut short however slow its systems are.
    /// Sits well under <c>JoinWorkers</c>'s 5 s join window on purpose — the barrier must give up FIRST so it can clear <c>_tickInProgress</c> and release
    /// the workers, which then exit inside their own window.
    /// </summary>
    private static readonly TimeSpan ShutdownDrainGrace = TimeSpan.FromMilliseconds(250);
    private CacheLinePaddedInt _systemsRemaining;
    private long _nextTickTimestamp;       // Used by GetNextTick for metronome advancement
    private long _currentTickNumber;

    // Between-tick wake: one event per worker. The dispatcher Sets every worker's event after bumping _tickGeneration (WakeWorkers); each worker Resets its
    // own (WorkerLoop). SpinCount 0: straight to a kernel wait — the gap after a tick is ms-scale, and spinning through it would burn CPU for nothing.
    //
    // Not one event for the pool: its Set released every parked worker at once, and each had to re-take the event's lock to leave Wait, so they queued on
    // it. Measured with 32 parked workers: the median worker ran 293 µs after the Set and the last 5.9 ms; with an event each, 85 µs and 146 µs. In the
    // SWG demo that queue was ~4 ms of thread time per tick in Monitor.Enter_Slowpath under Wait, at x1 and x64 alike; the tick gained 8-12 % at x1 / x2
    // and nothing measurable from x4 up, where the first workers to wake already carried each dispatch.
    private readonly ManualResetEventSlim[] _workerWake;

    /// <summary>
    /// How many idle iterations a worker spins before it starts yielding the core, WHILE A TICK IS IN PROGRESS. Default 4096.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the dispatch ramp, and it is a latency knob, not a throughput one.</b> A worker that finds no ready system inside a tick is waiting for a
    /// sibling to finish a system it depends on — work that is coming in microseconds. Yielding gives the core up, and on a box with two threads per core
    /// and every worker yielding, coming back costs tens of microseconds: measured on the SWG demo at 1 000 sessions, the last of 31 chunks of the frame
    /// stage started <b>1.88 ms</b> after the first, which was 51 % of that stage's whole span and capped its parallel efficiency at 79 %.
    /// </para>
    /// <para>
    /// <b>The budget is bounded by the tick, not by the gap between ticks.</b> Between ticks a worker parks on its own event and burns nothing — that path
    /// is untouched. This one only spins while <c>_tickInProgress</c> is set and there are systems left, so the worst case is a worker spinning for the
    /// remainder of a tick it has no work in, which is what a core would otherwise be idle for anyway.
    /// </para>
    /// <para>
    /// <b>Lower it</b> on a box that is oversubscribed, or where the engine shares cores with other processes: there the yield is the right answer and the
    /// spin is theft. 0 restores the pre-#906 behaviour of yielding after the first microsecond.
    /// </para>
    /// </remarks>
    public static int WorkerIdleSpinBudget { get; set; } = 100;

    /// <summary>
    /// Test seam: how long a parked worker waits before re-checking the generation by itself. 50 ms, a shutdown-liveness backstop; a test raises it so a
    /// worker the dispatcher failed to wake stalls the tick instead of arriving 50 ms late.
    /// </summary>
    internal TimeSpan BetweenTickWaitBackstop = TimeSpan.FromMilliseconds(50);

    /// <summary>Test seam: called with the worker's id when it has found the generation unchanged and is about to park. Null outside tests.</summary>
    internal Action<int> BetweenTickWaitProbe;

    /// <summary>Test seam: called with the worker's id as its thread starts, before it reads any scheduler state. Null outside tests.</summary>
    internal Action<int> WorkerStartProbe;

    /// <summary>Test seam: a worker's own wake event.</summary>
    internal ManualResetEventSlim WorkerWakeEvent(int workerId) => _workerWake[workerId];

    /// <summary>Test seam: called by <see cref="WakeWorkers"/> with each worker's id just before its Set. Null outside tests.</summary>
    internal Action<int> WakeProbe;

    /// <summary>Test seam: called with the worker's id when its between-tick wait times out, before the lost-wake check. Null outside tests.</summary>
    internal Action<int> BackstopProbe;

    /// <summary>
    /// The generation whose wake round is complete: stored by <see cref="DispatchTrackMultiThreaded"/> once <see cref="WakeWorkers"/> has Set every worker's
    /// event for it. A worker whose backstop fires while this is newer than the generation it last joined, with its own event still clear, missed that
    /// round's Set — nothing but the worker itself Resets its event. Stored after the round, never before, so a backstop firing between a bump and this
    /// worker's Set, with its Set still on the way, finds it older.
    /// </summary>
    private int _wokenGeneration;

    // Lost wakes since the scheduler was built (LostWakeCount); the timer thread's tick-end snapshot of it, for TickTelemetry.LostWakes; and the latch of the
    // one-shot warning.
    private long _lostWakes;
    private long _lostWakesAtLastTick;
    private int _lostWakeLogged;

    // Tick interval in Stopwatch ticks
    private readonly long _tickIntervalTicks;

    // ═══════════════════════════════════════════════════════════════
    // Overload management
    // ═══════════════════════════════════════════════════════════════

    private readonly OverloadDetector _overloadDetector;
    private int _tickMultiplier = 1;

    // ═══════════════════════════════════════════════════════════════
    // Telemetry
    // ═══════════════════════════════════════════════════════════════

    private readonly TickTelemetryRing _telemetryRing;
    private readonly SystemTelemetry[] _currentTickSystemMetrics;
    private long _previousTickStart; // For tick-to-tick interval measurement

    // Per-worker telemetry accumulators (deep mode)
    private readonly long[] _workerActiveTicks;
    private readonly long[] _workerIdleTicks;

    // ═══════════════════════════════════════════════════════════════
    // Tick lifecycle hooks (set by TyphonRuntime)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Called at tick start (before root dispatch). Creates UoW.</summary>
    internal Action<DagScheduler> TickStartCallback;

    /// <summary>Called at tick end (after all systems complete). Flushes/disposes UoW.</summary>
    internal Action<DagScheduler> TickEndCallback;

    /// <summary>
    /// Called before a CallbackSystem/QuerySystem executes. Creates per-system Transaction. Returns TickContext.
    /// Arguments: <c>sysIdx</c>, then the zero-based index of the worker running the system (#860) — the callback stamps it onto
    /// <see cref="TickContext.WorkerId"/> so system code can partition per-worker state without synchronization.
    /// </summary>
    internal Func<int, int, TickContext> SystemStartCallback;

    /// <summary>Called after a CallbackSystem/QuerySystem completes. Commits/disposes per-system Transaction.</summary>
    internal Action<int, bool> SystemEndCallback;

    /// <summary>
    /// Optional callback to enrich <see cref="TickTelemetry"/> with additional metrics (e.g., subscription Output phase).
    /// Called during <see cref="ComputeAndRecordTelemetry"/> before recording.
    /// </summary>
    internal EnrichTelemetryDelegate TelemetryEnrichCallback;

    internal delegate void EnrichTelemetryDelegate(ref TickTelemetry telemetry);

    /// <summary>
    /// Optional callback invoked after <c>TickEnd</c> is emitted on the scheduler thread. Intended use: collect per-tick gauge values
    /// (memory, page cache, WAL, tx) and push a <see cref="TraceEventKind.PerTickSnapshot"/> record. Called from the scheduler's own
    /// <see cref="ThreadSlot"/> so the snapshot co-locates with the <c>TickStart</c>/<c>TickEnd</c> pair in the trace file.
    /// </summary>
    /// <remarks>
    /// Wired by <see cref="TyphonRuntime"/> only when <see cref="TelemetryConfig.ProfilerGaugesActive"/> is <c>true</c> — when the gate is
    /// off the callback is never set, so even the delegate invocation is skipped.
    /// </remarks>
    internal Action<DagScheduler> GaugeSnapshotCallback;

    /// <summary>
    /// Public hook fired when something threw that the host has to be told about. User code can subscribe to log to file, send to telemetry, request graceful
    /// shutdown, or escalate to a debugger break. Invoked on the thread that caught the exception — a worker or the tick driver — so do not block in it.
    /// Defaults to null (no-op).
    /// <para>
    /// <b>Two sources, and only two.</b> (1) The WorkerLoop's outer safety net and the tick driver's, when an exception escaped every inner try/catch —
    /// meaning an inner handler didn't catch it, which is itself a bug worth surfacing prominently. (2) Since #890, ANY failure of a system on an
    /// ENGINE track — the Fence DAG — from its <c>ShouldRun</c>, <c>Prepare</c> or <c>Execute</c>, on any dispatch path, because the fence is the work
    /// that makes a tick durable and a fence that did not finish must not be silent (see <see cref="RecordEngineTrackFailure"/>).
    /// </para>
    /// <para>
    /// <b>Application-track systems keep the original contract:</b> the per-system handlers in <see cref="ProcessParallelQuery"/>,
    /// <see cref="ProcessCallbackOrQuery"/>, <see cref="ProcessPipeline"/> and <see cref="ExecuteInline"/> log via <c>LogSystemException</c> and capture via
    /// <c>CaptureSystemException</c> without invoking this callback; their detail lives in <see cref="SkipReason.Exception"/> in the telemetry ring.
    /// </para>
    /// </summary>
    public Action<int, string, Exception> UnhandledExceptionCallback;

    /// <summary>Called when overload level transitions to <see cref="OverloadLevel.PlayerShedding"/>.</summary>
    internal Action OnCriticalOverloadCallback;

    // ── Parallel QuerySystem callbacks (set by TyphonRuntime) ──

    /// <summary>Called once before parallel QuerySystem chunk dispatch. Builds entity set, returns totalChunks (0 = skip).</summary>
    internal Func<int, int> ParallelQueryPrepareCallback;

    /// <summary>Called per chunk: creates Transaction on worker thread, slices entities, calls Execute, commits. Args: (sysIdx, chunkIndex, totalChunks, workerId).</summary>
    internal Action<int, int, int, int> ParallelQueryChunkCallback;

    /// <summary>Called once after all chunks of a phase complete (or on skip). Returns <c>true</c> to re-dispatch the system for another phase
    /// (checkerboard two-phase dispatch, issue #234); <c>false</c> to proceed to successor dispatch.</summary>
    internal Func<int, bool> ParallelQueryCleanupCallback;

    // ═══════════════════════════════════════════════════════════════
    // Logging
    // ═══════════════════════════════════════════════════════════════

    private readonly ILogger _logger;

    // ═══════════════════════════════════════════════════════════════
    // HighResolutionTimerServiceBase overrides
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc />
    protected override string ThreadName => "Typhon.TickDriver";

    /// <inheritdoc />
    protected override long GetNextTick()
    {
        if (Volatile.Read(ref _workerShutdown) != 0)
        {
            return long.MaxValue;
        }

        _nextTickTimestamp += _tickIntervalTicks * _tickMultiplier;

        // Phase 4: Scheduler:Overload:TickMultiplier instant — emitted per tick (Tier-2-gated, leaf default OFF).
        var mult = (byte)Math.Min(_tickMultiplier, byte.MaxValue);
        TyphonEvent.EmitSchedulerOverloadTickMultiplier(_currentTickNumber, mult, mult);

        return _nextTickTimestamp;
    }

    /// <inheritdoc />
    protected override void ExecuteCallbacks(long scheduledTick, long actualTick)
    {
        if (Volatile.Read(ref _workerShutdown) != 0)
        {
            return;
        }

        // Terminal tick-abort (#567). AbortTickAndStop leaves the simulation logically incomplete — some systems ran, some never did — so the runtime must
        // not silently resume on top of it. The host is expected to have stopped us from its OnTickAborted handler; this is the backstop for the ticks that
        // fire before it gets there.
        if (IsTickAborted)
        {
            return;
        }

        // Terminal fence failure (#890). Same backstop as the abort above and for a stronger reason: the fence is what makes a tick durable, so a tick that
        // follows one whose fence did not finish would layer more un-logged page mutations on top of the first. design/Runtime/08-strict-tick-abort.md D3
        // calls this "straight to fatal stop"; the host is expected to react to OnTickAborted, and this covers the ticks that fire before it gets there.
        if (IsFenceFailed)
        {
            return;
        }

        // Outer safety net — this runs on the timer thread (HighResolutionTimerServiceBase.TimerLoop), a RAW thread with no catch of its own. An
        // exception escaping a tick here would propagate unhandled and ABORT THE PROCESS (a "Test host process crashed" / production host crash) —
        // strictly worse than a dropped tick. The single-threaded path (ExecuteTickSingleThreaded) runs the whole tick inline on this thread, so it has
        // no other net; the multi-threaded path's worker exceptions are already netted in WorkerLoop, but its coordination code runs here too. Mirror
        // the WorkerLoop net: log loudly, surface via the hook, drop this tick. (A persistently-throwing tick keeps being surfaced every tick rather
        // than silently — the hook can escalate to graceful shutdown.)
        try
        {
            if (_workerCount == 1)
            {
                ExecuteTickSingleThreaded(actualTick);
            }
            else
            {
                ExecuteTickMultiThreaded(actualTick);
            }
        }
        catch (Exception ex)
        {
            LogSystemException(-1, "<tick driver>", ex);
            try { UnhandledExceptionCallback?.Invoke(-1, "<tick driver>", ex); }
            catch { /* swallow — the callback itself threw; we are the last line of defense before the host aborts */ }
        }
    }

    /// <summary>
    /// Emit a <see cref="TraceEventKind.SchedulerMetronomeWait"/> span for the inter-tick wait that just completed. Called on the TickDriver thread
    /// by <see cref="HighResolutionTimerServiceBase"/>.
    /// </summary>
    /// <remarks>
    /// <b>Why this exists</b> (issue #289 follow-up). Without this hook the timer thread's three-phase wait between ticks emits no profiler events — appearing
    /// as dead time on the trace even when the engine is intentionally throttling itself via <see cref="OverloadDetector.TickMultiplier"/>. The span carries
    /// the multiplier and an <c>intentClass</c> byte (CatchUp / Throttled / Headroom) so a trace viewer can answer <i>why</i> the metronome was waiting.
    /// </remarks>
    protected override void OnWaitComplete(long scheduledTimestamp, long startTimestamp, long endTimestamp, byte phaseFlags)
    {
        if (!TelemetryConfig.SchedulerMetronomeWaitActive)
        {
            return;
        }

        // intentClass: 0=CatchUp, 1=Throttled, 2=Headroom.
        // CatchUp wins over Throttled — if we've fallen behind, the multiplier is irrelevant for *this* wait (we're not waiting because of it; we're not
        // waiting at all).
        byte intentClass;
        if (startTimestamp >= scheduledTimestamp)
        {
            intentClass = 0;
        }
        else if (_tickMultiplier > 1)
        {
            intentClass = 1;
        }
        else
        {
            intentClass = 2;
        }

        var multByte = (byte)Math.Min(_tickMultiplier, byte.MaxValue);
        TyphonEvent.EmitSchedulerMetronomeWait(startTimestamp, endTimestamp, scheduledTimestamp, multByte, intentClass, phaseFlags);
    }

    // ═══════════════════════════════════════════════════════════════
    // Constructor
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new DAG scheduler.
    /// </summary>
    /// <param name="systems">System definitions from <see cref="DagBuilder.Build"/>.</param>
    /// <param name="topologicalOrder">Topological order from <see cref="DagBuilder.Build"/>.</param>
    /// <param name="tracks">The track hierarchy in execution order — each track carries its DAGs and their resolved system indices.</param>
    /// <param name="deferredTrackStartIndex">Tracks from this index onward are dispatched on demand by the runtime (e.g. Engine-Post after serial fence prep),
    /// not by the in-tick track loop.</param>
    /// <param name="options">Runtime configuration.</param>
    /// <param name="parent">Parent resource node (typically <see cref="IResourceRegistry.Runtime"/>).</param>
    /// <param name="eventQueues">Event queues to reset at each tick start. Can be empty.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DagScheduler(SystemDefinition[] systems, int[] topologicalOrder, IReadOnlyList<Track> tracks, int deferredTrackStartIndex, RuntimeOptions options,
        IResource parent, EventQueueBase[] eventQueues = null, ILogger logger = null) : base("DagScheduler", parent)
    {
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentNullException.ThrowIfNull(topologicalOrder);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(options);

        Systems = systems;
        // #302: register PDB-resolved system source locations into the runtime manifest so the
        // exporters (FileExporter / TcpExporter) ship them alongside the compile-time call-site
        // attribution. Synthetic ids = 0x8000 | systemIndex distinguish system entries from spans.
        RuntimeSourceLocationManifest.SetSystems(systems);
        _topologicalOrder = topologicalOrder;
        AllSystemCount = systems.Length;
        _options = options;
        _exceptionPolicy = options.SystemExceptionPolicy;   // hoisted: read on the dispatch path, never changes after construction
        _eventQueues = eventQueues ?? [];
        _logger = logger ?? NullLogger.Instance;
        _workerCount = options.ResolveWorkerCount();

        // Assign stable queue IDs (#311) so the per-queue telemetry path can carry a small u16 instead of the queue's name on the wire.
        // QueueId == array index here; the cache builder writes a parallel `QueueNameTable` section so consumers can map index → name.
        //
        // Same pass sizes each queue's per-worker segments (#861). This is the earliest point the resolved worker count is known, and it is
        // single-threaded — the workers do not exist yet — which is what rule MD-02 requires of a per-worker array.
        for (var qi = 0; qi < _eventQueues.Length; qi++)
        {
            _eventQueues[qi].QueueId = (ushort)qi;
            _eventQueues[qi].BindWorkerSlots(_workerCount + 1);
        }

        // Tick interval
        _tickIntervalTicks = Stopwatch.Frequency / options.BaseTickRate;

        // Build per-track dispatch state. Track order is the execution sequence: each track is dispatched as its own
        // wake/barrier cycle (DispatchTrack), tracks [deferredTrackStartIndex, count) on demand from the runtime.
        _tracks = [.. tracks];
        _deferredTrackStartIndex = deferredTrackStartIndex;
        _scheduledTracks = new ScheduledTrack[_tracks.Length];
        _systemTrackIndex = new int[AllSystemCount];
        _systemIsEngine = new bool[AllSystemCount];

        var userSystemCount = 0;
        for (var t = 0; t < _tracks.Length; t++)
        {
            var track = _tracks[t];
            var members = new List<int>();
            var roots = new List<int>();
            foreach (var dag in track.Dags)
            {
                foreach (var sysIdx in dag.SystemIndices)
                {
                    members.Add(sysIdx);
                    _systemTrackIndex[sysIdx] = t;
                    _systemIsEngine[sysIdx] = track.IsEngine;
                    if (systems[sysIdx].PredecessorCount == 0)
                    {
                        roots.Add(sysIdx);
                    }
                }
            }

            _scheduledTracks[t] = new ScheduledTrack
            {
                Name = track.Name,
                IsEngine = track.IsEngine,
                FailureIsTerminal = track.FailureIsTerminal,
                Members = [.. members],
                Roots = [.. roots],
            };

            if (!track.IsEngine)
            {
                userSystemCount += members.Count;
            }
        }
        SystemCount = userSystemCount;

        // Allocate per-system state arrays
        _claims = new CacheLinePaddedLong[AllSystemCount];
        _remainingChunks = new CacheLinePaddedInt[AllSystemCount];
        _remainingDeps = new CacheLinePaddedInt[AllSystemCount];
        _isReady = new CacheLinePaddedInt[AllSystemCount];
        _systemFailed = new bool[AllSystemCount];
        _lastSystemException = new Exception[AllSystemCount];   // eager: the lazy `??=` it replaced raced across workers — see DagScheduler.Diagnostic.cs

        // Build reset templates
        _templateDeps = new int[AllSystemCount];
        _templateChunks = new int[AllSystemCount];
        for (var i = 0; i < AllSystemCount; i++)
        {
            _templateDeps[i] = systems[i].PredecessorCount;
            _templateChunks[i] = systems[i].TotalChunks;
        }

        // Overload detection
        _overloadDetector = new OverloadDetector(options.Overload, options.BaseTickRate);

        // Telemetry
        var ringCapacity = options.TelemetryRingCapacity;
        if (ringCapacity < 1 || (ringCapacity & (ringCapacity - 1)) != 0)
        {
            ringCapacity = 1024;
        }

        _telemetryRing = new TickTelemetryRing(ringCapacity, AllSystemCount);
        _currentTickSystemMetrics = new SystemTelemetry[AllSystemCount];

        // Per-worker telemetry
        _workerActiveTicks = new long[_workerCount];
        _workerIdleTicks = new long[_workerCount];

        // Create worker threads (not started yet), each with its own wake event
        if (_workerCount > 1)
        {
            _workers = new Thread[_workerCount];
            _workerWake = new ManualResetEventSlim[_workerCount];
            for (var i = 0; i < _workerCount; i++)
            {
                var workerId = i;
                _workerWake[i] = new ManualResetEventSlim(false, 0);
                _workers[i] = new Thread(() => WorkerLoop(workerId))
                {
                    IsBackground = true,
                    Name = $"Typhon.Worker-{i}"
                };
            }
        }
        else
        {
            _workers = [];
            _workerWake = [];
        }

        // Initialize next tick timestamp to now (first GetNextTick call will advance it)
        _nextTickTimestamp = Stopwatch.GetTimestamp();
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Starts the worker threads and the tick driver timer thread.
    /// </summary>
    public new void Start()
    {
        ValidateContextBindings();
        _started = true;
        LogStarted(AllSystemCount, _workerCount, _options.BaseTickRate);

        // Start worker threads, each from the generation before the first dispatch
        _generationAtStart = Volatile.Read(ref _tickGeneration);
        for (var i = 0; i < _workers.Length; i++)
        {
            _workers[i].Start();
            LogWorkerStarted(i);
        }

        // Start the timer thread (HighResolutionTimerServiceBase.Start)
        base.Start();
    }

    private bool _started;

    /// <summary>
    /// Bind an ambient <typeparamref name="TContext"/> onto every registered system deriving from <see cref="ChunkedCallbackSystem{TContext}"/>.
    /// Must be called after the schedule is built (systems must already be registered) and before <see cref="Start"/>.
    /// </summary>
    public void RegisterContext<TContext>(TContext context) where TContext : class
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_started)
        {
            throw new InvalidOperationException("RegisterContext must be called before Start.");
        }

        for (var i = 0; i < Systems.Length; i++)
        {
            if (Systems[i].Instance is ChunkedCallbackSystem<TContext> typed)
            {
                typed.BindContext(context);
            }
        }
    }

    /// <summary>
    /// Verify every <see cref="ChunkedCallbackSystem{TContext}"/> instance has a bound Context.
    /// Throws if a typed system was registered but no matching <see cref="TyphonRuntime.RegisterContext{TContext}"/> call ran.
    /// </summary>
    private void ValidateContextBindings()
    {
        for (var i = 0; i < Systems.Length; i++)
        {
            var instance = Systems[i].Instance;
            if (instance == null)
            {
                continue;
            }

            var type = instance.GetType();
            while (type != null && type != typeof(object))
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ChunkedCallbackSystem<>))
                {
                    var ctxProp = type.GetProperty("Context", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (ctxProp != null && ctxProp.GetValue(instance) == null)
                    {
                        var ctxType = type.GetGenericArguments()[0];
                        throw new InvalidOperationException(
                            $"System '{Systems[i].Name}' derives from ChunkedCallbackSystem<{ctxType.Name}> but no RegisterContext<{ctxType.Name}>(...) call was made.");
                    }
                    break;
                }
                type = type.BaseType;
            }
        }
    }

    /// <summary>
    /// Stops the worker pool: signals shutdown, releases any worker parked between ticks, and joins them.
    /// <para>
    /// A tick already in flight is ABANDONED, not drained. That is not a choice so much as an admission: this method signals every worker to stop before it
    /// waits, so the systems remaining in that tick have nobody left to run them, and "waiting for the current tick to finish" could only ever mean waiting
    /// forever. A caller that needs the in-flight tick's work to land must stop dispatching and let the tick complete BEFORE calling this.
    /// </para>
    /// <para>
    /// Does NOT stop the timer thread — <see cref="Dispose(bool)"/> does, via the base class, and that is the only quiescence point. The thread stays alive
    /// here but stops producing ticks: <see cref="GetNextTick"/> returns <c>long.MaxValue</c> and <see cref="ExecuteCallbacks"/> bails out, both gated at
    /// tick ENTRY. A tick already past that gate therefore runs to completion, and the increment of <see cref="CurrentTickNumber"/> is the last statement of
    /// that tick's telemetry finalizer — so the counter can advance by ONE after this method returns. Never by more: <c>_workerShutdown</c> is published
    /// before the join, so every later tick early-returns at the gate. Callers that need "nothing is running" must use <see cref="Dispose(bool)"/>.
    /// </para>
    /// <para>
    /// May legitimately be called ON the timer thread. <see cref="TyphonRuntime.OnTickAborted"/> fires inside the tick-end callback and its documented
    /// response is <see cref="TyphonRuntime.FatalStop"/>, which lands here — so on that path this returns INTO the tick it just stopped, and the one-tick
    /// advance above is guaranteed rather than merely possible. Do not "fix" that by joining the timer thread here: it would be a self-join, which
    /// <c>StopTimerThread</c>'s 2 s bound turns into a stall that returns <c>false</c> and changes nothing. See issue #404.
    /// </para>
    /// </summary>
    public void Shutdown()
    {
        LogShutdownRequested();

        // Signal workers to exit
        Volatile.Write(ref _workerShutdown, 1);
        Interlocked.Increment(ref _tickGeneration);
        WakeWorkers(); // Wake any blocked workers

        // Join worker threads (guard against unstarted threads)
        JoinWorkers();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (IsDisposed)
        {
            return;
        }

        if (disposing)
        {
            // Ensure workers are signaled to stop
            Volatile.Write(ref _workerShutdown, 1);
            Interlocked.Increment(ref _tickGeneration);
            WakeWorkers();
            JoinWorkers();
        }

        // Base class stops the timer thread and disposes the resource node — BEFORE the wake events are disposed. A tick already past ExecuteCallbacks'
        // shutdown check keeps dispatching its tracks on the timer thread. When the pool shared one event, each track ended with its Reset, which throws
        // ObjectDisposedException on a disposed event: disposed first, that surfaced as a FenceFailure in '<tick fence>' on the last tick, about one SWG
        // demo run in ten. The dispatcher now only Sets, which a disposed event tolerates, but a worker Resets and Waits on its own, so the order stays.
        base.Dispose(disposing);

        // Only once nothing can touch them. Both joins are bounded (timer 2 s, workers 5 s), and a Dispose issued on the timer thread cannot join itself;
        // in those cases the events are left to the GC, which costs nothing here — a ManualResetEventSlim only owns a kernel handle once its WaitHandle
        // has been read, and nothing reads it.
        if (disposing && !IsRunning && Array.TrueForAll(_workers, static w => !w.IsAlive))
        {
            foreach (var wake in _workerWake)
            {
                wake.Dispose();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Public API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Telemetry ring buffer for diagnostic inspection.</summary>
    public TickTelemetryRing Telemetry => _telemetryRing;

    /// <summary>Returns a ref to the current tick's SystemTelemetry for the given system index. Used by TyphonRuntime to write entity counts.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref SystemTelemetry GetCurrentSystemMetrics(int sysIdx) => ref _currentTickSystemMetrics[sysIdx];

    /// <summary>Returns the event queue at the given index. Used by TyphonRuntime to populate TickContext.ConsumedQueues.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EventQueueBase GetEventQueue(int index) => _eventQueues[index];

    /// <summary>Number of event queues registered.</summary>
    internal int EventQueueCount => _eventQueues.Length;

    /// <summary>
    /// All registered event queues. Read-only view exposed for static-data introspection (the profiler builds the v7 <see cref="Typhon.Profiler.EventQueueRecord"/>
    /// catalog from this) — not for hot-path use; system code should go through the pre-allocated <see cref="TickContext.ConsumedQueues"/> array instead.
    /// </summary>
    public IReadOnlyList<EventQueueBase> EventQueues => _eventQueues;

    /// <summary>Current overload response level.</summary>
    public OverloadLevel CurrentOverloadLevel => _overloadDetector.CurrentLevel;

    /// <summary>
    /// The multiplier the current tick's deadline was computed with: 1 normally, 2-6 while the runtime is dilating time under overload.
    /// </summary>
    /// <remarks>
    /// Written by <see cref="ComputeAndRecordTelemetry"/> at the end of a tick, for the next one, and read on the same timer thread — so a plain field access
    /// is the whole protocol. Replication reads it at tick start to publish the CURRENT tick period, which a client turns into its tick-to-time map: the
    /// nominal period alone would make a dilated tick look like a dropped frame.
    /// </remarks>
    internal int CurrentTickMultiplier => _tickMultiplier;

    /// <summary>
    /// Lost wakes that cost a worker its between-tick backstop (50 ms), since the scheduler was built: the backstop fired after a dispatch whose Set never
    /// reached the worker. Non-zero means the wake protocol is broken. Zero does not prove it is not: a lost wake that the next dispatch's Set rescues before
    /// the backstop fires leaves nothing to count, so at tick rates faster than the backstop most would go unseen. Per tick:
    /// <see cref="TickTelemetry.LostWakes"/>.
    /// </summary>
    public long LostWakeCount => Volatile.Read(ref _lostWakes);

    /// <summary>Number of worker threads.</summary>
    public int WorkerCount => _workerCount;

    /// <summary>
    /// The reserved worker slot for the dispatcher (timer) thread — always <see cref="WorkerCount"/>, one past the last real worker (#860).
    /// </summary>
    /// <remarks>
    /// The timer thread runs system bodies in one narrow case: <see cref="MarkTrackRootsReady"/> skips a root, and <c>OnSystemComplete</c> chains the
    /// successor into <c>ExecuteInline</c> right there. Internally the scheduler passes <c>workerId = -1</c> for this thread; <see cref="ToWorkerSlot"/>
    /// translates it to this slot so system code can index per-worker state on every path that runs system code.
    /// <para>
    /// <b>Why a dedicated slot is safe: disjointness, not quiescence.</b> It is tempting to argue that the inline execution happens before
    /// <c>WakeWorkers()</c> wakes the pool and is therefore serialized against every worker. That is NOT true across tracks:
    /// <see cref="DispatchTrackMultiThreaded"/> clears <c>_tickInProgress</c> only after its completion barrier, so a worker that has already evaluated
    /// its <c>while (_tickInProgress == 1 &amp;&amp; _systemsRemaining.Value &gt; 0)</c> loop condition can still be inside <c>FindReadySystem</c> while
    /// the timer thread has advanced into the next track's <see cref="MarkTrackRootsReady"/>. The slot is safe because the dispatcher never writes a
    /// worker's slot and no worker ever writes this one — do not weaken that to "the dispatcher can share a worker slot because nothing overlaps".
    /// </para>
    /// <para>
    /// Consequence: per-worker structures reachable from system code must be sized <c>WorkerCount + 1</c>, and
    /// <see cref="TickContext.WorkerId"/> ranges over <c>[0, WorkerCount]</c>, not <c>[0, WorkerCount)</c>.
    /// </para>
    /// </remarks>
    public int DispatcherWorkerId => _workerCount;

    /// <summary>
    /// Number of distinct worker slots a context can report — <see cref="WorkerCount"/> real workers plus the dispatcher slot (#860).
    /// </summary>
    /// <remarks>
    /// <b>Two different indices share the name "worker id" — size against the right one.</b> This value sizes arrays indexed by
    /// <see cref="TickContext.WorkerId"/>, the slot a system body sees. The scheduler's own per-worker pools (<c>_partitionViews</c>, the tier-range view
    /// pool, <c>ParallelTransactionAccessor.GetWorkerAccessor</c>) are indexed by the <i>internal</i> <c>workerId</c> that chunk dispatch hands out,
    /// which is always a real pool index and never the dispatcher slot — they are correctly sized <see cref="WorkerCount"/> and must stay that way.
    /// </remarks>
    public int WorkerSlotCount => _workerCount + 1;

    /// <summary>
    /// Maps an internal scheduler <c>workerId</c> — where <c>-1</c> means "the dispatcher thread" — onto the worker slot a
    /// <see cref="TickContext"/> reports (#860).
    /// </summary>
    /// <remarks>
    /// Only <c>ExecuteInline</c> can actually be reached with <c>-1</c> (via <see cref="MarkTrackRootsReady"/> → <c>OnSystemComplete</c>);
    /// <c>ProcessCallbackOrQuery</c> is reached only from <c>WorkerLoop</c>, where the id is always a real pool index. It is applied on both for
    /// uniformity — the two call sites are otherwise identical and diverging them invites the wrong one being copied.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int ToWorkerSlot(int workerId) => workerId < 0 ? _workerCount : workerId;

    /// <summary>
    /// Number of user-registered systems (systems whose track does not carry the <see cref="Track.EngineTag"/> — i.e. excludes the Fence DAG). This is the
    /// count callers usually want — "the systems I registered". For the total including engine-internal systems use <see cref="AllSystemCount"/>.
    /// </summary>
    public int SystemCount { get; }

    /// <summary>Total number of systems in the DAG, including engine-internal systems (e.g. <c>FenceExec</c>).</summary>
    public int AllSystemCount { get; }

    /// <summary>Number of ticks executed so far.</summary>
    public long CurrentTickNumber => _currentTickNumber;

    // ═══════════════════════════════════════════════════════════════
    // Multi-threaded tick execution
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteTickMultiThreaded(long tickStartTimestamp)
    {
        // 1. Reset per-system state
        ResetTickState();

        // Publish the current tick number to this (timer) thread's TLS so every TyphonEvent emit on this thread — TickStart/TickEnd/SystemReady/
        // SystemSkipped/Phase — tags its TraceEvent with the right TickNumber. Workers do the same in WorkerLoop when they wake for a new tick.
        TyphonEvent.CurrentTickNumber = (int)_currentTickNumber;

        // 2. Tick start hook (TyphonRuntime creates UoW)
        TickStartCallback?.Invoke(this);
        InspectorTickStart(_currentTickNumber, tickStartTimestamp);

        // 3. Dispatch the in-tick tracks in execution order — one wake/barrier cycle per non-empty track. Tracks
        //    [_deferredTrackStartIndex, count) (Engine-Post) are dispatched later by the runtime after serial fence prep.
        for (var t = 0; t < _deferredTrackStartIndex; t++)
        {
            DispatchTrackMultiThreaded(t);
        }

        // 5. Tick end hook (TyphonRuntime flushes/disposes UoW, then dispatches the Engine-Post track)
        TickEndCallback?.Invoke(this);

        var tickEndTimestamp = Stopwatch.GetTimestamp();
        InspectorTickEnd(_currentTickNumber, tickEndTimestamp);

        // 5b. Profiler gauge snapshot (post-TickEnd so the snapshot record lands immediately after the TickEnd marker in the ring).
        //     Callback is null unless TyphonRuntime has wired it up under ProfilerGaugesActive, so the null check is the only cost when off.
        GaugeSnapshotCallback?.Invoke(this);

        // 6. Record telemetry
        //    Note: _nextTickTimestamp is updated by GetNextTick() which the base timer loop
        //    calls immediately after this method returns. Workers briefly see a stale value
        //    (~100ns) and spin, then GetNextTick publishes the correct target and they sleep.
        ComputeAndRecordTelemetry(tickStartTimestamp, tickEndTimestamp);
    }

    // ═══════════════════════════════════════════════════════════════
    // Single-threaded tick execution (WorkerCount == 1)
    // ═══════════════════════════════════════════════════════════════

    private void ExecuteTickSingleThreaded(long tickStartTimestamp)
    {
        // Reset metrics, event queues, and failure flags
        for (var i = 0; i < AllSystemCount; i++)
        {
            _currentTickSystemMetrics[i] = default;
        }

        Array.Clear(_systemFailed);

        foreach (var queue in _eventQueues)
        {
            queue.Reset();
        }

        // Publish current tick number to TLS for profiler emits on this (timer) thread. See ExecuteTickMultiThreaded for rationale.
        TyphonEvent.CurrentTickNumber = (int)_currentTickNumber;

        // Tick start hook
        TickStartCallback?.Invoke(this);
        InspectorTickStart(_currentTickNumber, tickStartTimestamp);

        // Dispatch the in-tick tracks in execution order. Engine-Post is dispatched separately by the runtime (DispatchTrack).
        for (var t = 0; t < _deferredTrackStartIndex; t++)
        {
            RunTrackSingleThreaded(t);
        }

        // Tick end hook
        TickEndCallback?.Invoke(this);

        var tickEndTimestampSt = Stopwatch.GetTimestamp();
        InspectorTickEnd(_currentTickNumber, tickEndTimestampSt);

        // Profiler gauge snapshot (post-TickEnd). Same contract as the multi-threaded path.
        GaugeSnapshotCallback?.Invoke(this);

        ComputeAndRecordTelemetry(tickStartTimestamp, tickEndTimestampSt);
    }

    /// <summary>Runs every system of one track in topological order on the calling thread (single-threaded / synchronous track dispatch).</summary>
    private void RunTrackSingleThreaded(int trackIndex)
    {
        for (var i = 0; i < _topologicalOrder.Length; i++)
        {
            var sysIdx = _topologicalOrder[i];
            if (_systemTrackIndex[sysIdx] == trackIndex)
            {
                RunSystemSingleThreaded(sysIdx);

                // Propagate `readyUs` to successors — the serial analog of `OnSystemComplete`'s fan-out. A successor becomes ready when its last predecessor
                // finishes; topological order runs predecessors first, and the last one to run overwrites last, so the successor's `ReadyTick` settles on its
                // gating predecessor's completion time. Without this, a successor's `ReadyTick` would be stamped only when the topo loop *reached* it (its own
                // entry) — i.e. after every earlier-running sibling, not when its predecessor completed. Skipped systems leave `LastChunkDoneTick == 0` and
                // propagate nothing; a successor gated only by skipped predecessors falls back to its own entry stamp.
                var doneTick = _currentTickSystemMetrics[sysIdx].LastChunkDoneTick;
                if (doneTick > 0)
                {
                    foreach (var succ in Systems[sysIdx].Successors)
                    {
                        _currentTickSystemMetrics[succ].ReadyTick = doneTick;
                    }
                }
            }
        }
    }

    /// <summary>Executes a single system synchronously on the calling thread, with the full ShouldRun / Prepare / overload / failure machinery.</summary>
    private void RunSystemSingleThreaded(int sysIdx)
    {
        var sys = Systems[sysIdx];

        // `readyUs` contract: a system is ready when its last predecessor completed. In serial topological dispatch the predecessors have already run and
        // stamped this slot via the successor-propagation tail in `RunTrackSingleThreaded`; a root — or a system gated only by skipped predecessors — has
        // no stamp yet, so it is ready as of now.
        var readyTick = _currentTickSystemMetrics[sysIdx].ReadyTick;
        if (readyTick == 0)
        {
            readyTick = Stopwatch.GetTimestamp();
            _currentTickSystemMetrics[sysIdx].ReadyTick = readyTick;
        }

        InspectorSystemReady(sysIdx, readyTick);

        // Strict tick-abort (#567) — single-threaded dispatch path. Same gate as the multi-worker paths; without it a WorkerCount == 1 runtime would keep
        // executing systems after the abort.
        if (IsTickAborted && !_systemIsEngine[sysIdx])
        {
            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.TickAborted;
            InspectorSystemSkipped(sysIdx, SkipReason.TickAborted, Stopwatch.GetTimestamp());
            foreach (var succ in sys.Successors)
            {
                _systemFailed[succ] = true;
            }

            return;
        }

        // Check if a predecessor failed
        if (_systemFailed[sysIdx])
        {
            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.DependencyFailed;
            InspectorSystemSkipped(sysIdx, SkipReason.DependencyFailed, Stopwatch.GetTimestamp());
            // Propagate failure to successors
            foreach (var succ in sys.Successors)
            {
                _systemFailed[succ] = true;
            }

            return;
        }

        // Evaluate ShouldRun (untyped delegate + typed virtual for ChunkedCallbackSystem<TContext>).
        bool shouldRunSingle = sys.ShouldRun?.Invoke() ?? true;
        if (shouldRunSingle && sys.Instance is ChunkedCallbackSystem ccsSingle)
        {
            try
            {
                shouldRunSingle = ccsSingle.OnShouldRun();
            }
            catch (Exception ex)
            {
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                _systemFailed[sysIdx] = true;
                RecordSystemFailure(sysIdx, sys.Name, ex);
                return;
            }
        }
        if (!shouldRunSingle)
        {
            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.ShouldRunFalse;
            InspectorSystemSkipped(sysIdx, SkipReason.ShouldRunFalse, Stopwatch.GetTimestamp());
            return;
        }

        // Prepare gate (chunked systems only). Single-threaded mode: still needs to run so plans get built.
        if (sys.Instance is ChunkedCallbackSystem ccsSingle2)
        {
            int chunks;
            try
            {
                chunks = ccsSingle2.OnPrepare();
            }
            catch (Exception ex)
            {
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                _systemFailed[sysIdx] = true;
                RecordSystemFailure(sysIdx, sys.Name, ex);
                return;
            }

            if (chunks == 0)
            {
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.EmptyInput;
                InspectorSystemSkipped(sysIdx, SkipReason.EmptyInput, Stopwatch.GetTimestamp());
                return;
            }
            if (chunks > 0)
            {
                sys.RuntimeChunkCount = chunks;
            }
        }

        if (sys.ReactiveSkip != null && sys.ReactiveSkip())
        {
            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.EmptyInput;
            InspectorSystemSkipped(sysIdx, SkipReason.EmptyInput, Stopwatch.GetTimestamp());
            return;
        }

        {
            var overloadSkip = CheckOverloadSkip(sysIdx);
            if (overloadSkip != SkipReason.NotSkipped)
            {
                _currentTickSystemMetrics[sysIdx].SkipReason = overloadSkip;
                InspectorSystemSkipped(sysIdx, overloadSkip, Stopwatch.GetTimestamp());
                return;
            }
        }

        var startTick = Stopwatch.GetTimestamp();
        _currentTickSystemMetrics[sysIdx].FirstChunkGrabTick = startTick;

        // Phase 4: Scheduler:System:SingleThreaded span — wraps the per-system synchronous tick body. ChunkCount filled once known.
        var stScope = TyphonEvent.BeginSchedulerSystemSingleThreaded(
            (ushort)sysIdx,
            sys.IsParallelQuery ? (byte)1 : (byte)0,
            0);
        try
        {
            if (sys.IsParallelQuery)
            {
                // Issue #234: do/while loop supports checkerboard two-phase dispatch. For non-checkerboard systems, cleanup returns false on
                // the first iteration → loop executes exactly once → zero overhead.
                bool morePhases;
                do
                {
                    var totalChunks = ParallelQueryPrepareCallback?.Invoke(sysIdx) ?? 0;
                    if (totalChunks <= 0)
                    {
                        morePhases = ParallelQueryCleanupCallback?.Invoke(sysIdx) ?? false;
                        continue;
                    }

                    Systems[sysIdx].TotalChunks = totalChunks;
                    var chunkFailed = false;
                    for (var chunk = 0; chunk < totalChunks; chunk++)
                    {
                        var chunkStartTs = Stopwatch.GetTimestamp();
                        SystemAccessValidator.EnterSystem(sys.Access, sys.Name);
                        try
                        {
                            ParallelQueryChunkCallback?.Invoke(sysIdx, chunk, totalChunks, 0);
                        }
                        catch (Exception ex)
                        {
                            chunkFailed = true;
                            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                            _systemFailed[sysIdx] = true;
                            RecordSystemFailure(sysIdx, sys.Name, ex);
                            foreach (var succ in sys.Successors)
                            {
                                _systemFailed[succ] = true;
                            }
                        }
                        finally
                        {
                            SystemAccessValidator.LeaveSystem();

                            // WorkUs, as the multi-worker path sums it; one thread, so a plain add.
                            _currentTickSystemMetrics[sysIdx].WorkTicks += Stopwatch.GetTimestamp() - chunkStartTs;
                        }
                    }

                    morePhases = ParallelQueryCleanupCallback?.Invoke(sysIdx) ?? false;
                    if (chunkFailed)
                    {
                        morePhases = false; // Abort remaining phases on failure
                    }
                } while (morePhases);
            }
            else if (sys.Type == SystemType.PipelineSystem)
            {
                for (var chunk = 0; chunk < sys.TotalChunks; chunk++)
                {
                    SystemAccessValidator.EnterSystem(sys.Access, sys.Name);
                    try
                    {
                        sys.PipelineChunkAction(chunk, sys.TotalChunks);
                    }
                    catch (Exception ex)
                    {
                        _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                        _systemFailed[sysIdx] = true;
                        RecordSystemFailure(sysIdx, sys.Name, ex);
                        foreach (var succ in sys.Successors)
                        {
                            _systemFailed[succ] = true;
                        }
                    }
                    finally
                    {
                        SystemAccessValidator.LeaveSystem();
                    }
                }
            }
            else // CallbackSystem or non-parallel QuerySystem — single invocation
            {
                // Single-threaded dispatch: the tick thread is the one and only worker, so worker 0 is the truthful id (#860).
                var ctx = SystemStartCallback?.Invoke(sysIdx, 0)
                          ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f, WorkerId = 0, ChunkCount = 1 };
                ctx.DebugValidateWorkerId(WorkerSlotCount, sys.Name);
                SystemAccessValidator.EnterSystem(sys.Access, sys.Name);
                try
                {
                    sys.CallbackAction(ctx);
                }
                catch (Exception ex)
                {
                    _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                    _systemFailed[sysIdx] = true;
                    RecordSystemFailure(sysIdx, sys.Name, ex);
                    // Propagate failure to successors
                    foreach (var succ in sys.Successors)
                    {
                        _systemFailed[succ] = true;
                    }
                }
                finally
                {
                    SystemAccessValidator.LeaveSystem();
                }
            }

            var endTick = Stopwatch.GetTimestamp();
            _currentTickSystemMetrics[sysIdx].LastChunkDoneTick = endTick;
            _currentTickSystemMetrics[sysIdx].WorkersTouched = sys.IsParallelQuery ? sys.TotalChunks : 1;

            stScope.ChunkCount = (ushort)Math.Min(sys.IsParallelQuery ? sys.TotalChunks : 1, ushort.MaxValue);

            // Fire system-end lifecycle for every system kind on the single-threaded path. Earlier this only ran inside
            // the callback-system `else` branch above, which silently skipped the parallel-query and pipeline branches —
            // hiding the entire #327 Phase A emit path on real workloads. Placed after LastChunkDoneTick is set so the
            // emit's `endTs > 0` guard sees a populated timestamp.
            SystemEndCallback?.Invoke(sysIdx, !_systemFailed[sysIdx]);
        }
        finally
        {
            stScope.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Worker loop
    // ═══════════════════════════════════════════════════════════════

    private void WorkerLoop(int workerId)
    {
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        LogWorkerStarted(workerId);

        // Worker threads have no meaningful outer Activity — opt out of Activity.Current capture once so every subsequent BeginSpan skips
        // the AsyncLocal read (~5–9 ns saved per span on the dominant producer).
        TyphonEvent.SuppressActivityContextOnThisThread();

        WorkerStartProbe?.Invoke(workerId);
        var wake = _workerWake[workerId];
        // Not _tickGeneration as this thread first reads it: a thread that starts after the first bump would take that dispatch for one it had already seen
        // and sit it out.
        var lastGen = _generationAtStart;

        while (Volatile.Read(ref _workerShutdown) == 0)
        {
            // ═══ Between-tick: kernel wait on this worker's own event ═══
            // Zero CPU while parked. The event is this worker's alone, so the dispatcher's Set wakes one waiter and nothing queues behind it (see
            // _workerWake).
            var betweenTickSpan = TyphonEvent.BeginSchedulerWorkerBetweenTick((byte)workerId);
            var btStart = Stopwatch.GetTimestamp();
            byte wakeReason = 0; // woken: by the signal, or by the backstop with no wake lost
            while (Volatile.Read(ref _tickGeneration) == lastGen)
            {
                // First after the check, so a Reset anywhere between the check and the Wait would come after it (WorkerWakeTests).
                BetweenTickWaitProbe?.Invoke(workerId);
                if (Volatile.Read(ref _workerShutdown) != 0)
                {
                    betweenTickSpan.WakeReason = 1; // shutdown
                    var btEnd = Stopwatch.GetTimestamp();
                    var btUs1 = (btEnd - btStart) * 1_000_000L / Stopwatch.Frequency;
                    betweenTickSpan.WaitUs = (uint)Math.Min(btUs1, uint.MaxValue);
                    betweenTickSpan.Dispose();
                    return;
                }

                if (!wake.Wait(BetweenTickWaitBackstop))
                {
                    BackstopProbe?.Invoke(workerId);

                    // The backstop fired after a wake round this worker belonged to had completed, and its event is clear: that round's Set never
                    // reached it. The round is read first, with acquire, so a Set of it is visible to the IsSet read that follows.
                    if (unchecked(Volatile.Read(ref _wokenGeneration) - lastGen) > 0 && !wake.IsSet)
                    {
                        wakeReason = 2; // resumed by the backstop after a lost wake
                        OnLostWake(workerId, unchecked(lastGen + 1));
                    }
                }

                // Reset after every return, before the loop re-checks the generation — never between the check and the wait, where it would swallow a Set
                // that landed in between. Without it, a Set whose generation this worker had already seen (one that landed after the worker left this loop
                // without parking) would keep the event set, and the loop would spin on it until the next dispatch. The re-check must not be read ahead of
                // the Reset, a store followed by a load, which x64 reorders too: hence the barrier, stated here rather than left to Reset's own interlocked
                // update. The dispatcher bumps the generation before it Sets, so any Set the Reset clears belongs to a generation the re-check sees.
                wake.Reset();
                Interlocked.MemoryBarrier();
            }
            {
                var btEnd = Stopwatch.GetTimestamp();
                var btUs2 = (btEnd - btStart) * 1_000_000L / Stopwatch.Frequency;
                betweenTickSpan.WakeReason = wakeReason;
                betweenTickSpan.WaitUs = (uint)Math.Min(btUs2, uint.MaxValue);
                betweenTickSpan.Dispose();
                TyphonEvent.EmitSchedulerWorkerWake((byte)workerId, (uint)Math.Min(btUs2, uint.MaxValue));
            }

            if (Volatile.Read(ref _workerShutdown) != 0)
            {
                return;
            }

            lastGen = Volatile.Read(ref _tickGeneration);

            // Publish the current scheduler tick number to this worker's TLS so every TyphonEvent emit below (ChunkStart/ChunkEnd and any
            // BeginSpan calls from inside a system body) tags its TraceEvent with the right TickNumber. Without this, worker-emitted events land
            // in "tick 0" and the viewer collapses every chunk into a single tick group.
            TyphonEvent.CurrentTickNumber = (int)_currentTickNumber;

            // ═══ Within-tick: find and process work ═══
            var trackUtilization = TelemetryConfig.SchedulerActive && TelemetryConfig.SchedulerTrackWorkerUtilization;

            var idleSpins = 0;
            // Worker:Idle span lifecycle — tracks the contiguous "spell" of being idle (consecutive FindReadySystem returning -1).
            // Started when idleSpins first goes from 0 → 1; ended when work is found.
            var idleSpan = default(SchedulerWorkerIdleEvent);
            var idleSpellStart = 0L;
            // Deliberately NOT gated on _workerShutdown: a worker that is mid-tick must finish the tick, or shutdown silently drops the work of the tick in
            // flight (it cost a real regression to learn this — Telemetry_ReadyTick_NotInflatedBySibling went red with ReadyTick 0 because the last tick was
            // abandoned). The escape hatch is _tickInProgress, which the completion barrier in DispatchTrackMultiThreaded now always clears — including when
            // it gives up on a tick that can no longer complete. That is what stops this loop spinning forever.
            while (_tickInProgress == 1 && _systemsRemaining.Value > 0)
            {
                var sysIdx = FindReadySystem();
                if (sysIdx >= 0)
                {
                    if (idleSpins > 0)
                    {
                        // End of idle spell — close the span if one was started.
                        if (idleSpellStart != 0)
                        {
                            var idleEnd = Stopwatch.GetTimestamp();
                            var idleUs = (idleEnd - idleSpellStart) * 1_000_000L / Stopwatch.Frequency;
                            idleSpan.SpinCount = (ushort)Math.Min(idleSpins, ushort.MaxValue);
                            idleSpan.IdleUs = (uint)Math.Min(idleUs, uint.MaxValue);
                            idleSpan.Dispose();
                            idleSpellStart = 0L;
                        }
                    }
                    idleSpins = 0;
                    // Outer safety net: ProcessSystem and its descendants (ProcessParallelQuery, ProcessCallbackOrQuery,/ ProcessPipeline, ExecuteInline) each
                    // have their own try/catch around the user-system invocation. This wrap-around is defense-in-depth: ensures no unforeseen exception path
                    // (engine-internal bug, exception from inside a catch handler, OOM during cleanup, etc.) can ever kill the worker thread. A dead worker
                    // would leave _systemsRemaining stuck > 0 and the tick would never complete — the simulation appears frozen ("everything is still") while
                    // the timer thread keeps firing ticks.
                    try
                    {
                        ProcessSystem(sysIdx, workerId, trackUtilization);
                    }
                    catch (Exception ex)
                    {
                        // Mark the system as failed so its successors get skipped instead of waiting forever for the unbounded-dep counter. Capture the
                        // exception for DumpHangDiagnostic. Surface to user code via the UnhandledExceptionCallback hook if registered.
                        if ((uint)sysIdx < (uint)_systemFailed.Length)
                        {
                            _systemFailed[sysIdx] = true;
                            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                        }
                        var sysName = (uint)sysIdx < (uint)Systems.Length ? Systems[sysIdx].Name : "<unknown>";
                        RecordSystemFailure(sysIdx, sysName, ex);
                        if ((uint)sysIdx >= (uint)_systemIsEngine.Length || !_systemIsEngine[sysIdx])
                        {
                            // An engine-track failure was already surfaced by the funnel (#890); firing again here would report it twice.
                            try { UnhandledExceptionCallback?.Invoke(sysIdx, sysName, ex); }
                            catch { /* swallow — the callback itself threw; we're the last line of defense */ }
                        }
                    }
                }
                else
                {
                    // D5: spin briefly, then yield.
                    // First ~100 iterations (~1µs) spin with PAUSE for lowest latency.
                    // After that, yield the core — there's genuinely no work and spinning wastes CPU on narrow DAGs. Adds ~1µs dispatch latency but saves a core.
                    if (idleSpins == 0 && idleSpellStart == 0)
                    {
                        // First idle iter — start the Idle span.
                        idleSpan = TyphonEvent.BeginSchedulerWorkerIdle((byte)workerId);
                        idleSpellStart = Stopwatch.GetTimestamp();
                    }
                    idleSpins++;
                    if (idleSpins <= WorkerIdleSpinBudget)
                    {
                        if (trackUtilization)
                        {
                            var idleStart = Stopwatch.GetTimestamp();
                            Thread.SpinWait(4);
                            _workerIdleTicks[workerId] += Stopwatch.GetTimestamp() - idleStart;
                        }
                        else
                        {
                            Thread.SpinWait(4);
                        }
                    }
                    else
                    {
                        if (trackUtilization)
                        {
                            var idleStart = Stopwatch.GetTimestamp();
                            Thread.Yield();
                            _workerIdleTicks[workerId] += Stopwatch.GetTimestamp() - idleStart;
                        }
                        else
                        {
                            Thread.Yield();
                        }
                    }
                }
            }

            // Tick ended — close any pending idle span left from end-of-tick idle.
            if (idleSpellStart != 0)
            {
                var idleEnd = Stopwatch.GetTimestamp();
                var idleUs = (idleEnd - idleSpellStart) * 1_000_000L / Stopwatch.Frequency;
                idleSpan.SpinCount = (ushort)Math.Min(idleSpins, ushort.MaxValue);
                idleSpan.IdleUs = (uint)Math.Min(idleUs, uint.MaxValue);
                idleSpan.Dispose();
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // System discovery and dispatch
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Test seam: called with <c>-1</c> as each <see cref="FindReadySystem"/> scan begins, and with a multi-chunk system's index between reading its ready
    /// flag and its claim counter. Null outside tests, so the cost is one field load per scan.
    /// </summary>
    internal Action<int> FindReadySystemProbe;

    /// <summary>
    /// Test seam: called in every chunk-claim loop with the system's index and <c>0</c> just before a claim, <c>1</c> just after it. Null outside tests.
    /// </summary>
    internal Action<int, int> ClaimProbe;

    /// <summary>
    /// Linear scan of ready systems. Returns the index of a system that can be processed, or -1 if no work is available.
    /// </summary>
    /// <remarks>
    /// POC validated this O(n) scan is negligible up to 1,000 systems.
    /// The _isReady array fits in 2 cache lines for 16 systems — hot in L1.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindReadySystem()
    {
        var probe = FindReadySystemProbe;
        probe?.Invoke(-1);
        for (var i = 0; i < AllSystemCount; i++)
        {
            // Acquire load, paired with MarkSystemReady's release. For a multi-chunk system it is only a filter: what orders a claim after the dispatch's
            // stores is the claim word itself (OpenChunkClaims' release, the claim's interlocked increment).
            if (Volatile.Read(ref _isReady[i].Value) != 1)
            {
                continue;
            }

            if (Systems[i].Type == SystemType.PipelineSystem || Systems[i].IsParallelQuery)
            {
                probe?.Invoke(i);

                // Multi-chunk system: only return if its live dispatch has chunks left. One load reads both halves of one dispatch's word; compared
                // unsigned, like every claim, so no count of increments can make an exhausted word read as open.
                var word = Volatile.Read(ref _claims[i].Value);
                if ((uint)word < (uint)(word >> 32))
                {
                    return i;
                }
            }
            else
            {
                // CallbackSystem/non-parallel QuerySystem: return index, CAS claim happens in ProcessSystem
                return i;
            }
        }

        return -1;
    }

    private void ProcessSystem(int sysIdx, int workerId, bool trackUtilization)
    {
        var sys = Systems[sysIdx];

        if (sys.IsParallelQuery)
        {
            ProcessParallelQuery(sysIdx, workerId, trackUtilization);
        }
        else if (sys.Type == SystemType.PipelineSystem)
        {
            ProcessPipeline(sysIdx, workerId, trackUtilization);
        }
        else // CallbackSystem or non-parallel QuerySystem — same single-invocation path
        {
            ProcessCallbackOrQuery(sysIdx, workerId, trackUtilization);
        }
    }

    private void ProcessCallbackOrQuery(int sysIdx, int workerId, bool trackUtilization)
    {
        // Atomic claim: only one worker wins
        if (Interlocked.CompareExchange(ref _isReady[sysIdx].Value, 0, 1) != 1)
        {
            return;
        }

        // Strict tick-abort gate (#567, correction C1). Deliberately AFTER the claim, never inside FindReadySystem:
        // this worker now owns the system, so it is the one obliged to drive the countdown. Returning -1 at find time would leave _isReady set and
        // _systemsRemaining unadvanced — the tick would hang, which is precisely the failure this gate exists to prevent. Engine tracks are exempt
        // (rule D3) — the fence runs no matter what.
        if (IsTickAborted && !_systemIsEngine[sysIdx])
        {
            SkipSystemForTickAbort(sysIdx, workerId, trackUtilization);
            return;
        }

        TyphonEvent.EmitSchedulerDispense((ushort)sysIdx, 0, (byte)workerId);

        // ShouldRun was already evaluated at dispatch time (OnSystemComplete or root marking).
        // System lifecycle hook: create per-system Transaction (called on the worker thread)
        var workerSlot = ToWorkerSlot(workerId);
        var ctx = SystemStartCallback?.Invoke(sysIdx, workerSlot)
                  ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f, WorkerId = workerSlot, ChunkCount = 1 };
        var workStart = Stopwatch.GetTimestamp();
        RecordFirstChunkGrab(sysIdx, workStart);
        InspectorChunkStart(sysIdx, 0, workStart, 1);

        var success = true;
        ctx.DebugValidateWorkerId(WorkerSlotCount, Systems[sysIdx].Name);
        SystemAccessValidator.EnterSystem(Systems[sysIdx].Access, Systems[sysIdx].Name);
        try
        {
            Systems[sysIdx].CallbackAction(ctx);
        }
        catch (Exception ex)
        {
            success = false;
            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
            _systemFailed[sysIdx] = true;
            RecordSystemFailure(sysIdx, Systems[sysIdx].Name, ex);
        }
        finally
        {
            SystemAccessValidator.LeaveSystem();
            // System lifecycle hook: commit/dispose per-system Transaction
            SystemEndCallback?.Invoke(sysIdx, success);

            var workEnd = Stopwatch.GetTimestamp();
            InspectorChunkEnd(sysIdx, 0, workEnd, _currentTickSystemMetrics[sysIdx].EntitiesProcessed);
            if (trackUtilization)
            {
                _workerActiveTicks[workerId] += workEnd - workStart;
            }

            RecordSystemDone(sysIdx, workEnd);
            _currentTickSystemMetrics[sysIdx].WorkersTouched = 1;
        }

        OnSystemComplete(sysIdx, workerId, trackUtilization);
    }

    private void ProcessPipeline(int sysIdx, int workerId, bool trackUtilization)
    {
        // ShouldRun was already evaluated at dispatch time. If we're here, the system should execute.
        var sys = Systems[sysIdx];
        var probe = ClaimProbe;

        while (true)
        {
            probe?.Invoke(sysIdx, 0);
            var claim = Interlocked.Increment(ref _claims[sysIdx].Value) - 1;
            probe?.Invoke(sysIdx, 1);
            var chunk = (int)claim;
            var totalChunks = (int)(claim >> 32);
            if ((uint)chunk >= (uint)totalChunks)
            {
                break;
            }

            // A failed system's chunks are claimed and counted down without running (DrainClaimedChunk). Tested after the claim, so the flag read belongs to
            // the dispatch the claim came from.
            if (_systemFailed[sysIdx])
            {
                if (DrainClaimedChunk(sysIdx, workerId, trackUtilization, totalChunks))
                {
                    break;
                }

                continue;
            }

            TyphonEvent.EmitSchedulerDispense((ushort)sysIdx, chunk, (byte)workerId);

            if (chunk == 0)
            {
                // Strict tick-abort start gate — see the identical gate in ProcessParallelQuery.
                if (IsTickAborted && !_systemIsEngine[sysIdx])
                {
                    if (AbortSystemFromChunkZero(sysIdx, workerId, trackUtilization, totalChunks))
                    {
                        break;
                    }

                    continue;
                }
                RecordFirstChunkGrab(sysIdx, Stopwatch.GetTimestamp());
            }

            var workStart = Stopwatch.GetTimestamp();
            InspectorChunkStart(sysIdx, chunk, workStart, totalChunks);
            SystemAccessValidator.EnterSystem(sys.Access, sys.Name);
            try
            {
                sys.PipelineChunkAction(chunk, totalChunks);
            }
            catch (Exception ex)
            {
                _systemFailed[sysIdx] = true;
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                RecordSystemFailure(sysIdx, sys.Name, ex);
            }
            finally
            {
                SystemAccessValidator.LeaveSystem();
            }

            var workEnd = Stopwatch.GetTimestamp();
            InspectorChunkEnd(sysIdx, chunk, workEnd, 0);

            if (trackUtilization)
            {
                _workerActiveTicks[workerId] += workEnd - workStart;
            }

            // D8: countdown — last completer dispatches successors
            var remaining = Interlocked.Decrement(ref _remainingChunks[sysIdx].Value);
            if (remaining == 0)
            {
                RecordSystemDone(sysIdx, workEnd);
                OnSystemComplete(sysIdx, workerId, trackUtilization);
                break;
            }
        }
    }

    /// <summary>
    /// Count one claimed chunk of a failed system down without running it; true when that completed the system.
    /// </summary>
    /// <remarks>
    /// Why count down instead of bailing out: <see cref="FindReadySystem"/> keeps offering a system while its claim word has chunks left, so a worker that
    /// broke out on seeing the failure would come straight back — a full-CPU spin that never fires <see cref="OnSystemComplete"/> and never dispatches the
    /// successors. Claiming each chunk and counting it down exhausts the word, and whichever worker takes <c>_remainingChunks</c> to zero completes the system.
    /// One claim at a time from the ordinary claim loop, not a loop of its own: a drain loop that cached the failed dispatch's size swallowed chunks of the
    /// NEXT dispatch when its worker was preempted across the tick boundary (CD-01, <c>ChunkClaimStragglerTests</c>).
    /// </remarks>
    private bool DrainClaimedChunk(int sysIdx, int workerId, bool trackUtilization, int totalChunks)
    {
        if (Interlocked.Decrement(ref _remainingChunks[sysIdx].Value) != 0)
        {
            return false;
        }

        var doneTs = Stopwatch.GetTimestamp();
        if (Systems[sysIdx].IsParallelQuery)
        {
            // The same completion as the last chunk that ran: a failed parallel query still owes its cleanup.
            CompleteParallelDispatch(sysIdx, workerId, trackUtilization, doneTs, totalChunks);
            return true;
        }

        RecordSystemDone(sysIdx, doneTs);
        // Mirror the success-path SystemEndCallback so a failed system still gets its lifecycle hook (e.g. Phase A telemetry, transaction rollback wiring).
        SystemEndCallback?.Invoke(sysIdx, false);
        OnSystemComplete(sysIdx, workerId, trackUtilization);
        return true;
    }

    /// <summary>
    /// A parallel query's dispatch has counted its last chunk down, run or drained: its cleanup, then either the next phase (#234 checkerboard) or the
    /// system's completion.
    /// </summary>
    /// <remarks>
    /// <para><b>The cleanup runs however the dispatch ended.</b> It returns the dispatch's pooled entity list, flushes the workers' accessors and resets the
    /// checkerboard phase. When a failed system completed through the drain it used to be skipped: the list was never returned and the phase was left
    /// behind.</para>
    /// <para><b>A failed system starts no further phase</b>, whatever the cleanup asks for — the single-threaded path's rule. It used to re-dispatch phase
    /// B after a failed phase A, only for every chunk of it to be drained.</para>
    /// </remarks>
    private void CompleteParallelDispatch(int sysIdx, int workerId, bool trackUtilization, long doneTs, int totalChunks)
    {
        RecordSystemDone(sysIdx, doneTs);
        _currentTickSystemMetrics[sysIdx].WorkersTouched = totalChunks;
        var nextPhase = ParallelQueryCleanupCallback?.Invoke(sysIdx) ?? false;
        if (nextPhase && !_systemFailed[sysIdx])
        {
            DispatchParallelQuery(sysIdx, workerId, trackUtilization);
            return;
        }

        // System is genuinely done (no further checkerboard phase). Fire the system-end lifecycle hook so #327 Phase A emits its per-(system, archetype)
        // row — this path was missing the call entirely, which is why parallel-query systems produced zero `SchedulerSystemArchetypeEvent` records on real
        // workloads despite being correctly bound at runtime construction.
        SystemEndCallback?.Invoke(sysIdx, !_systemFailed[sysIdx]);
        OnSystemComplete(sysIdx, workerId, trackUtilization);
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel QuerySystem dispatch
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Multi-worker chunk dispatch for parallel QuerySystems. Each worker grabs chunks via atomic counter.
    /// Unlike <see cref="ProcessPipeline"/>, each chunk has its own Transaction lifecycle (managed by the chunk callback).
    /// </summary>
    private void ProcessParallelQuery(int sysIdx, int workerId, bool trackUtilization)
    {
        var sys = Systems[sysIdx];
        var probe = ClaimProbe;
        while (true)
        {
            probe?.Invoke(sysIdx, 0);
            var claim = Interlocked.Increment(ref _claims[sysIdx].Value) - 1;
            probe?.Invoke(sysIdx, 1);
            var chunk = (int)claim;
            var totalChunks = (int)(claim >> 32);
            if ((uint)chunk >= (uint)totalChunks)
            {
                break;
            }

            // See ProcessPipeline: a failed system's chunks are counted down without running, one claim at a time.
            if (_systemFailed[sysIdx])
            {
                if (DrainClaimedChunk(sysIdx, workerId, trackUtilization, totalChunks))
                {
                    break;
                }

                continue;
            }

            TyphonEvent.EmitSchedulerDispense((ushort)sysIdx, chunk, (byte)workerId);

            if (chunk == 0)
            {
                // Strict tick-abort start gate (#567, corrections C1/C2). A claim hands chunk 0 to exactly one worker, so this is the system's single "does it
                // begin?" decision — rule D1, granularity is the system, never the chunk. Folded into a branch that already existed, so it is free on the hot
                // path.
                if (IsTickAborted && !_systemIsEngine[sysIdx])
                {
                    if (AbortSystemFromChunkZero(sysIdx, workerId, trackUtilization, totalChunks))
                    {
                        break;
                    }

                    continue;
                }
                RecordFirstChunkGrab(sysIdx, Stopwatch.GetTimestamp());
            }

            var workStart = Stopwatch.GetTimestamp();
            InspectorChunkStart(sysIdx, chunk, workStart, totalChunks);
            SystemAccessValidator.EnterSystem(sys.Access, sys.Name);
            try
            {
                ParallelQueryChunkCallback?.Invoke(sysIdx, chunk, totalChunks, workerId);
            }
            catch (Exception ex)
            {
                _systemFailed[sysIdx] = true;
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                RecordSystemFailure(sysIdx, sys.Name, ex);
            }
            finally
            {
                SystemAccessValidator.LeaveSystem();
            }

            var workEnd = Stopwatch.GetTimestamp();
            InspectorChunkEnd(sysIdx, chunk, workEnd, _currentTickSystemMetrics[sysIdx].EntitiesProcessed);

            // The system's worker time: WorkUs, and the cost per entity that sizes its next dispatch (TyphonRuntime.ComputeChunkCount).
            Interlocked.Add(ref _currentTickSystemMetrics[sysIdx].WorkTicks, workEnd - workStart);

            if (trackUtilization)
            {
                _workerActiveTicks[workerId] += workEnd - workStart;
            }

            // D8: countdown — last completer runs cleanup, then re-dispatches (#234 checkerboard) or completes the system
            if (Interlocked.Decrement(ref _remainingChunks[sysIdx].Value) == 0)
            {
                CompleteParallelDispatch(sysIdx, workerId, trackUtilization, workEnd, totalChunks);
                break;
            }
        }
    }

    /// <summary>
    /// Prepares and dispatches a parallel QuerySystem. Called from <see cref="OnSystemComplete"/> (successor dispatch)
    /// or root system marking. Runs the prepare callback to compute chunk count, then either marks the system ready
    /// for multi-worker chunk grabbing or handles the zero-entity skip case.
    /// </summary>
    private void DispatchParallelQuery(int sysIdx, int workerId, bool trackUtilization)
    {
        // Nothing to close before preparing: a first dispatch finds the word closed by ResetTickState, and a re-dispatch (#234 checkerboard phase B)
        // finds phase A's word exhausted — every chunk was claimed before phase A completed — so either refuses every claim until the publish below.
        var totalChunks = ParallelQueryPrepareCallback?.Invoke(sysIdx) ?? 0;
        if (totalChunks <= 0)
        {
            // Empty entity set — cleanup may trigger re-dispatch (checkerboard: zero Red clusters but non-zero Black).
            var reDispatch = ParallelQueryCleanupCallback?.Invoke(sysIdx) ?? false;
            if (reDispatch)
            {
                DispatchParallelQuery(sysIdx, workerId, trackUtilization);
                return;
            }
            OnSystemComplete(sysIdx, workerId, trackUtilization);
            return;
        }

        Systems[sysIdx].TotalChunks = totalChunks;
        _remainingChunks[sysIdx].Value = totalChunks;
        // Publish, claims first: a worker holding a ready flag from an earlier dispatch claims as soon as the word opens, so the open must follow the stores
        // above (its release orders them). MarkSystemReady's release then does the same for workers that gate on the flag's acquire load (FindReadySystem).
        // Correct on arm64; free on x64 (TSO).
        OpenChunkClaims(sysIdx, totalChunks);
        MarkSystemReady(sysIdx);
    }

    // ═══════════════════════════════════════════════════════════════
    // Completion and successor dispatch
    // ═══════════════════════════════════════════════════════════════

    private void OnSystemComplete(int sysIdx, int workerId, bool trackUtilization)
    {
        Interlocked.Decrement(ref _systemsRemaining.Value);

        // `readyUs` contract: a successor becomes ready the instant its last predecessor completes. `sysIdx` IS that last predecessor for every successor this
        // call decrements to zero, so all of them share one ready timestamp — `sysIdx`'s completion — captured once here, before the loop. Capturing it
        // per-successor *inside* the loop (the old code) drifted late: an earlier sibling dispatched via `ExecuteInline` runs to completion *within* the loop,
        // so a later sibling's `GetTimestamp()` measured successor-loop arrival time, not predecessor-completion time — putting it spuriously off the Critical
        // Path. (#354 CP-view diagnosis.)
        var readyTs = Stopwatch.GetTimestamp();

        // D2: any-worker dispatch — iterate successors
        var successors = Systems[sysIdx].Successors;
        var fanOutSpan = TyphonEvent.BeginSchedulerDependencyFanOut((ushort)sysIdx);
        var fanOutSkipped = (ushort)0;
        try
        {
            foreach (var succIdx in successors)
            {
                // Propagate failure: writing true to a bool is idempotent and atomic on x86
                if (_systemFailed[sysIdx])
                {
                    _systemFailed[succIdx] = true;
                }

                var depsLeft = Interlocked.Decrement(ref _remainingDeps[succIdx].Value);
                if (depsLeft == 0)
                {
                    _currentTickSystemMetrics[succIdx].ReadyTick = readyTs;
                    InspectorSystemReady(succIdx, readyTs);
                    TyphonEvent.EmitSchedulerDependencyReady((ushort)sysIdx, (ushort)succIdx, (ushort)successors.Length, 0);
                    var succ = Systems[succIdx];

                    // Strict tick-abort (#567) — checked BEFORE the failed-predecessor branch and before EvaluateShouldRunAndPrepare, so no user
                    // ShouldRun / Prepare body runs once the tick is cancelled.
                    // Reported as TickAborted rather than DependencyFailed: this system has no failed predecessor, the tick was cancelled out from under it.
                    // Engine tracks are exempt (rule D3).
                    if (IsTickAborted && !_systemIsEngine[succIdx])
                    {
                        _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.TickAborted;
                        InspectorSystemSkipped(succIdx, SkipReason.TickAborted, Stopwatch.GetTimestamp());
                        fanOutSkipped++;
                        OnSystemComplete(succIdx, workerId, trackUtilization);
                    }
                    // Check if any predecessor failed — skip this system entirely
                    else if (_systemFailed[succIdx])
                    {
                        _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.DependencyFailed;
                        InspectorSystemSkipped(succIdx, SkipReason.DependencyFailed, Stopwatch.GetTimestamp());
                        fanOutSkipped++;
                        OnSystemComplete(succIdx, workerId, trackUtilization);
                    }
                    // Unified ShouldRun + Prepare gate. Single-threaded by construction: only the thread that decremented _remainingDeps to zero reaches this
                    // branch. The typed OnShouldRun/OnPrepare path serves ChunkedCallbackSystem<TContext>; non-chunked systems use the untyped delegate.
                    else if (!EvaluateShouldRunAndPrepare(succIdx, workerId, trackUtilization, ref fanOutSkipped))
                    {
                        // Skip/dispatch already handled inside EvaluateShouldRunAndPrepare.
                    }
                    else if (succ.ReactiveSkip != null && succ.ReactiveSkip())
                    {
                        _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.EmptyInput;
                        InspectorSystemSkipped(succIdx, SkipReason.EmptyInput, Stopwatch.GetTimestamp());
                        fanOutSkipped++;
                        OnSystemComplete(succIdx, workerId, trackUtilization);
                    }
                    else
                    {
                        var overloadSkip = CheckOverloadSkip(succIdx);
                        if (overloadSkip != SkipReason.NotSkipped)
                        {
                            _currentTickSystemMetrics[succIdx].SkipReason = overloadSkip;
                            InspectorSystemSkipped(succIdx, overloadSkip, Stopwatch.GetTimestamp());
                            fanOutSkipped++;
                            OnSystemComplete(succIdx, workerId, trackUtilization);
                        }
                        else if (succ.IsParallelQuery)
                        {
                            // Parallel QuerySystem: prepare entity set, then mark ready for multi-worker chunk grab
                            DispatchParallelQuery(succIdx, workerId, trackUtilization);
                        }
                        else if (succ.Type == SystemType.CallbackSystem || succ.Type == SystemType.QuerySystem)
                        {
                            // D3: inline continuation for single-invocation successors
                            ExecuteInline(succIdx, workerId, trackUtilization);
                        }
                        else
                        {
                            // A pipeline: TotalChunks is fixed and _remainingChunks was seeded at tick reset, so opening its claims is all the
                            // publish there is.
                            OpenChunkClaims(succIdx, Systems[succIdx].TotalChunks);
                            MarkSystemReady(succIdx);
                        }
                    }
                }
            }
        }
        finally
        {
            fanOutSpan.SuccCount = (ushort)Math.Min(successors.Length, ushort.MaxValue);
            fanOutSpan.SkippedCount = fanOutSkipped;
            fanOutSpan.Dispose();
        }
    }

    /// <summary>
    /// Evaluate the unified ShouldRun + Prepare gate for a system. For <see cref="ChunkedCallbackSystem"/> instances, invokes the typed virtuals
    /// (<see cref="ChunkedCallbackSystem.OnShouldRun"/>, <see cref="ChunkedCallbackSystem.OnPrepare"/>). For all systems, also evaluates the untyped
    /// <see cref="SystemDefinition.ShouldRun"/> delegate.
    ///
    /// <para>Returns <c>true</c> if downstream branches (ReactiveSkip / overload / dispatch) should run. Returns <c>false</c> if this method already handled
    /// the system (skip-recurse or full dispatch).</para>
    /// </summary>
    private bool EvaluateShouldRunAndPrepare(int succIdx, int workerId, bool trackUtilization, ref ushort fanOutSkipped)
    {
        var succ = Systems[succIdx];

        // ─── ShouldRun gate ───
        // Untyped delegate (fluent .ShouldRun(Func<bool>)) gate.
        bool shouldRun = succ.ShouldRun?.Invoke() ?? true;

        // Typed virtual gate (overridden by ChunkedCallbackSystem<TContext>).
        if (shouldRun && succ.Instance is ChunkedCallbackSystem ccs)
        {
            try
            {
                shouldRun = ccs.OnShouldRun();
            }
            catch (Exception ex)
            {
                _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.Exception;
                _systemFailed[succIdx] = true;
                RecordSystemFailure(succIdx, succ.Name, ex);
                fanOutSkipped++;
                OnSystemComplete(succIdx, workerId, trackUtilization);
                return false;
            }
        }

        if (!shouldRun)
        {
            _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.ShouldRunFalse;
            InspectorSystemSkipped(succIdx, SkipReason.ShouldRunFalse, Stopwatch.GetTimestamp());
            fanOutSkipped++;
            OnSystemComplete(succIdx, workerId, trackUtilization);
            return false;
        }

        // ─── Prepare gate (chunked systems only) ───
        if (succ.Instance is ChunkedCallbackSystem ccs2)
        {
            int chunks;
            try
            {
                chunks = ccs2.OnPrepare();
            }
            catch (Exception ex)
            {
                _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.Exception;
                _systemFailed[succIdx] = true;
                RecordSystemFailure(succIdx, succ.Name, ex);
                fanOutSkipped++;
                OnSystemComplete(succIdx, workerId, trackUtilization);
                return false;
            }

            if (chunks == 0)
            {
                _currentTickSystemMetrics[succIdx].SkipReason = SkipReason.EmptyInput;
                InspectorSystemSkipped(succIdx, SkipReason.EmptyInput, Stopwatch.GetTimestamp());
                fanOutSkipped++;
                OnSystemComplete(succIdx, workerId, trackUtilization);
                return false;
            }

            if (chunks > 0)
            {
                // For IsParallelQuery chunked systems, ParallelQueryPrepareCallback reads RuntimeChunkCount.
                // Set it and fall through to the existing dispatch flow (IsParallelQuery → DispatchParallelQuery).
                succ.RuntimeChunkCount = chunks;
            }
            // chunks == -1 → fall through to existing dispatch flow.
        }

        return true;
    }

    private void ExecuteInline(int sysIdx, int workerId, bool trackUtilization)
    {
        // ShouldRun was already evaluated by the caller (OnSystemComplete).
        var workerSlot = ToWorkerSlot(workerId);
        var ctx = SystemStartCallback?.Invoke(sysIdx, workerSlot)
                  ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f, WorkerId = workerSlot, ChunkCount = 1 };
        var workStart = Stopwatch.GetTimestamp();
        RecordFirstChunkGrab(sysIdx, workStart);
        InspectorChunkStart(sysIdx, 0, workStart, 1);

        var success = true;
        ctx.DebugValidateWorkerId(WorkerSlotCount, Systems[sysIdx].Name);
        SystemAccessValidator.EnterSystem(Systems[sysIdx].Access, Systems[sysIdx].Name);
        try
        {
            Systems[sysIdx].CallbackAction(ctx);
        }
        catch (Exception ex)
        {
            success = false;
            _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
            _systemFailed[sysIdx] = true;
            RecordSystemFailure(sysIdx, Systems[sysIdx].Name, ex);
        }
        finally
        {
            SystemAccessValidator.LeaveSystem();
            SystemEndCallback?.Invoke(sysIdx, success);

            var workEnd = Stopwatch.GetTimestamp();
            InspectorChunkEnd(sysIdx, 0, workEnd, _currentTickSystemMetrics[sysIdx].EntitiesProcessed);
            if (trackUtilization)
            {
                _workerActiveTicks[workerId] += workEnd - workStart;
            }

            RecordSystemDone(sysIdx, workEnd);
            _currentTickSystemMetrics[sysIdx].WorkersTouched = 1;
        }

        // Recursively dispatch successors
        OnSystemComplete(sysIdx, workerId, trackUtilization);
    }

    // ═══════════════════════════════════════════════════════════════
    // Overload skip check
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether a system should be skipped due to tick divisor or overload throttling/shedding.
    /// Returns <see cref="SkipReason.NotSkipped"/> if the system should execute normally.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private SkipReason CheckOverloadSkip(int sysIdx)
    {
        var sys = Systems[sysIdx];

        // Baseline TickDivisor (active even at Normal load)
        if (sys.TickDivisor > 1 && _currentTickNumber % sys.TickDivisor != 0)
        {
            TyphonEvent.EmitSchedulerOverloadSystemShed((ushort)sysIdx, (byte)_overloadDetector.CurrentLevel, (ushort)sys.TickDivisor, 1);
            return SkipReason.Throttled;
        }

        var level = _overloadDetector.CurrentLevel;
        if (level == OverloadLevel.Normal)
        {
            return SkipReason.NotSkipped;
        }

        // Level 1+: Shed Low-priority systems with CanShed
        if (sys.Priority == SystemPriority.Low && sys.CanShed)
        {
            TyphonEvent.EmitSchedulerOverloadSystemShed((ushort)sysIdx, (byte)level, 0, 2);
            return SkipReason.Shed;
        }

        // Level 1+: Throttle Normal-priority systems via ThrottledTickDivisor
        if (sys.Priority == SystemPriority.Normal && sys.ThrottledTickDivisor > 1 && _currentTickNumber % sys.ThrottledTickDivisor != 0)
        {
            TyphonEvent.EmitSchedulerOverloadSystemShed((ushort)sysIdx, (byte)level, (ushort)sys.ThrottledTickDivisor, 1);
            return SkipReason.Throttled;
        }

        return SkipReason.NotSkipped;
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Joins the worker threads, bounded so a wedged worker cannot hang the caller forever. A worker that misses its join window is REPORTED: discarding
    /// <see cref="Thread.Join(TimeSpan)"/>'s result is what turned a transient shutdown hang into permanent silent damage — the thread stays alive spinning
    /// at ~100% of a core for the life of the process while Shutdown returns as though it had stopped cleanly, so the cost lands on whatever runs next.
    /// </summary>
    private void JoinWorkers()
    {
        for (var i = 0; i < _workers.Length; i++)
        {
            var worker = _workers[i];
            if ((worker.ThreadState & System.Threading.ThreadState.Unstarted) != 0)
            {
                continue;
            }

            if (!worker.Join(TimeSpan.FromSeconds(5)))
            {
                LogWorkerJoinTimeout(i, worker.ManagedThreadId);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // Release store: orders the preceding TotalChunks/_remainingChunks writes ahead of the ready flag (paired with the acquire load in FindReadySystem). Free on x64 (TSO); stlr on arm64.
    private void MarkSystemReady(int sysIdx) => Volatile.Write(ref _isReady[sysIdx].Value, 1);

    /// <summary>Opens a multi-chunk system's claims for a dispatch of <paramref name="totalChunks"/>: its LAST store. See <see cref="_claims"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OpenChunkClaims(int sysIdx, int totalChunks) => Volatile.Write(ref _claims[sysIdx].Value, (long)totalChunks << 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordFirstChunkGrab(int sysIdx, long timestamp)
    {
        var prior = Interlocked.CompareExchange(ref _currentTickSystemMetrics[sysIdx].FirstChunkGrabTick, timestamp, 0);
        if (prior == 0)
        {
            // Only emit on the first successful grab (CompareExchange replaced 0 with timestamp).
            TyphonEvent.EmitSchedulerSystemStartExecution((ushort)sysIdx);
            var readyTs = _currentTickSystemMetrics[sysIdx].ReadyTick;
            if (readyTs > 0)
            {
                var queueWaitTicks = timestamp - readyTs;
                var queueWaitUs = (uint)Math.Min((queueWaitTicks * 1_000_000L) / Stopwatch.Frequency, uint.MaxValue);
                TyphonEvent.EmitSchedulerSystemQueueWait((ushort)sysIdx, queueWaitUs);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordSystemDone(int sysIdx, long timestamp)
    {
        _currentTickSystemMetrics[sysIdx].LastChunkDoneTick = timestamp;
        if (TelemetryConfig.SchedulerSystemCompletionActive)
        {
            var startTs = _currentTickSystemMetrics[sysIdx].FirstChunkGrabTick;
            var durationTicks = startTs > 0 ? timestamp - startTs : 0;
            var durationUs = (uint)Math.Min((durationTicks * 1_000_000L) / Stopwatch.Frequency, uint.MaxValue);
            var reason = (byte)_currentTickSystemMetrics[sysIdx].SkipReason;
            TyphonEvent.EmitSchedulerSystemCompletion((ushort)sysIdx, reason, durationUs);
        }
    }

    private void ResetTickState()
    {
        // Resets per-system state for every system across every track. _systemsRemaining is NOT seeded here — each DispatchTrack call seeds it with its own
        // track's member count, so the per-track dispatch loop terminates when that track's systems are done. A track's systems are only marked ready during
        // that track's dispatch.
        for (var i = 0; i < AllSystemCount; i++)
        {
            _claims[i].Value = 0L;   // closed: a count of 0 until a dispatch publishes its word — see _claims
            _remainingChunks[i].Value = _templateChunks[i];
            _remainingDeps[i].Value = _templateDeps[i];
            _isReady[i].Value = 0;
            _currentTickSystemMetrics[i] = default;
        }

        _systemsRemaining.Value = 0;
        Array.Clear(_systemFailed);

        // Reset event queues at tick start
        foreach (var queue in _eventQueues)
        {
            queue.Reset();
        }

        // Reset per-worker utilization counters
        if (TelemetryConfig.SchedulerActive && TelemetryConfig.SchedulerTrackWorkerUtilization)
        {
            Array.Clear(_workerActiveTicks);
            Array.Clear(_workerIdleTicks);
        }
    }

    /// <summary>
    /// Marks one track's root systems ready (evaluating ShouldRun / Prepare / ReactiveSkip / overload-skip). Shared by every track's multi-threaded dispatch
    /// — the in-tick tracks and the runtime-deferred Engine-Post track alike.
    /// </summary>
    private void MarkTrackRootsReady(int[] roots)
    {
        var readyNow = Stopwatch.GetTimestamp();
        foreach (var root in roots)
        {
            _currentTickSystemMetrics[root].ReadyTick = readyNow;
            InspectorSystemReady(root, readyNow);
            var sys = Systems[root];
            ushort rootSkipUnused = 0;
            if (!EvaluateShouldRunAndPrepare(root, -1, false, ref rootSkipUnused))
            {
                // Skip / Prepare-dispatch already handled by helper.
            }
            else if (sys.ReactiveSkip != null && sys.ReactiveSkip())
            {
                _currentTickSystemMetrics[root].SkipReason = SkipReason.EmptyInput;
                InspectorSystemSkipped(root, SkipReason.EmptyInput, Stopwatch.GetTimestamp());
                OnSystemComplete(root, -1, false);
            }
            else
            {
                var overloadSkip = CheckOverloadSkip(root);
                if (overloadSkip != SkipReason.NotSkipped)
                {
                    _currentTickSystemMetrics[root].SkipReason = overloadSkip;
                    InspectorSystemSkipped(root, overloadSkip, Stopwatch.GetTimestamp());
                    OnSystemComplete(root, -1, false);
                }
                else if (sys.IsParallelQuery)
                {
                    DispatchParallelQuery(root, -1, false);
                }
                else
                {
                    if (sys.Type == SystemType.PipelineSystem)
                    {
                        OpenChunkClaims(root, sys.TotalChunks);
                    }

                    MarkSystemReady(root);
                }
            }
        }
    }

    /// <summary>
    /// Dispatches one track on the worker pool — a single wake/barrier cycle. Marks the track's roots ready, wakes the workers, then spin-waits for the track's
    /// systems to complete. No-op for an empty track.
    /// </summary>
    private void DispatchTrackMultiThreaded(int trackIndex)
    {
        var track = _scheduledTracks[trackIndex];
        if (track.MemberCount == 0)
        {
            return;
        }

        // Seed the completion counter with this track's member count, then mark its roots ready. Systems of other tracks are never marked ready during this
        // cycle, so the worker dispatch loop terminates on this track only.
        _systemsRemaining.Value = track.MemberCount;
        MarkTrackRootsReady(track.Roots);

        // Every member already finished, on this thread, inside MarkTrackRootsReady — so there is nothing for a worker to do and no reason to wake one. A
        // system whose ShouldRun returns false completes inline and fans out inline (EvaluateShouldRunAndPrepare → OnSystemComplete), so a track that is
        // entirely gated off for this tick drains to zero before we get here.
        //
        // Without this, a declared-but-idle track costs a full wake/barrier cycle — a generation bump plus a ManualResetEventSlim.Set per worker,
        // ≈ 0.1 ms — on EVERY tick of EVERY runtime. That is what the Engine-Subscriptions track would have charged every existing user for a feature
        // none of them have switched on yet, and it is charged again by any future built-in track that spends most of its life gated off. A plain read
        // is correct here: no worker has been woken for this round, so this thread is the only writer.
        if (_systemsRemaining.Value == 0)
        {
            return;
        }

        // Activate — bump the generation, wake the workers, then publish the round as complete (the lost-wake check in WorkerLoop keys on it).
        _tickInProgress = 1;
        var generation = Interlocked.Increment(ref _tickGeneration);
        WakeWorkers();
        Volatile.Write(ref _wokenGeneration, generation);

        // Wait for the track's systems to complete. The timer thread must spin — Thread.Yield() on Windows can stall up to 15.6 ms, cascading into every
        // subsequent tick.
        //
        // This wait used to be UNBOUNDED, and there is a race that makes it unsatisfiable. Shutdown() bumps _tickGeneration — the same field workers use to
        // detect a new tick — so a Shutdown landing between the Increment above and the workers waking makes every worker take the `_workerShutdown != 0`
        // exit in WorkerLoop and return WITHOUT processing a single system. _systemsRemaining then never reaches 0, and this loop span forever at ~100% of a
        // core for the remaining life of the process, while JoinWorkers() timed out, discarded its result, and let Shutdown() report success. On a machine
        // with spare cores nobody noticed; pinned to the 3 cores of the arm64 nightly runner, each leaked spinner permanently removed a third of the box and
        // the suite eventually failed VSTest's heartbeat and was killed as "Test host process crashed".
        //
        // Normal operation is untouched: nothing below runs until shutdown has been requested. After that the drain is bounded by STALL, not by elapsed time
        // — the clock resets every time a system completes, so a tick that is still making progress is never cut short no matter how slow its systems are,
        // while the unsatisfiable case (no worker left to decrement anything) gives up one grace period after the last completion. Bounding on elapsed time
        // instead charged the full grace to every occurrence and cost ~2 s per race; bounding on stall charges it once, only when genuinely stuck.
        var lastRemaining = -1;
        var stallStart = 0L;
        while (_systemsRemaining.Value > 0)
        {
            if (Volatile.Read(ref _workerShutdown) != 0)
            {
                var remaining = _systemsRemaining.Value;
                if (remaining != lastRemaining)
                {
                    lastRemaining = remaining;
                    stallStart = Stopwatch.GetTimestamp();
                }
                else if (Stopwatch.GetElapsedTime(stallStart) > ShutdownDrainGrace)
                {
                    LogTickDrainAbandonedOnShutdown(trackIndex, remaining);
                    break;
                }
            }

            Thread.SpinWait(1);
        }

        // Always cleared, including on the abandoned path above — this is the escape hatch the workers' within-tick dispatch loop keys on, so leaving it set
        // would strand every worker still inside the tick.
        _tickInProgress = 0;
    }

    /// <summary>
    /// Wakes the pool: a <see cref="ManualResetEventSlim.Set"/> on every worker's own event. Called after the generation bump, so a worker it wakes finds
    /// the generation changed. Each worker Resets its own event (<see cref="WorkerLoop"/>), so nothing here is undone after the track.
    /// </summary>
    private void WakeWorkers()
    {
        var probe = WakeProbe;
        for (var i = 0; i < _workerWake.Length; i++)
        {
            probe?.Invoke(i);
            _workerWake[i].Set();
        }
    }

    /// <summary>
    /// Counts a wake the pool lost (<see cref="LostWakeCount"/>) and warns about the first one, naming the first generation the worker missed. Cold: only
    /// a backstop that caught one calls it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void OnLostWake(int workerId, int lostGeneration)
    {
        Interlocked.Increment(ref _lostWakes);
        if (Interlocked.CompareExchange(ref _lostWakeLogged, 1, 0) == 0)
        {
            LogLostWake(workerId, lostGeneration, BetweenTickWaitBackstop.TotalMilliseconds);
        }
    }

    /// <summary>
    /// Dispatches a single track by its execution-order index. The in-tick tracks are dispatched automatically each tick; the runtime calls this
    /// (via <see cref="DispatchDeferredTracks"/>) for the deferred Engine-Post track after serial fence prep. Branches on worker count: single-threaded mode
    /// runs the track synchronously on the caller.
    /// </summary>
    public void DispatchTrack(int trackIndex)
    {
        if (_workerCount == 1)
        {
            RunTrackSingleThreaded(trackIndex);
        }
        else
        {
            DispatchTrackMultiThreaded(trackIndex);
        }
    }

    /// <summary>
    /// Dispatches every runtime-deferred track (Engine-Post and beyond) in execution order. Called by <c>TyphonRuntime.OnTickEndInternal</c> after the serial
    /// <c>WriteTickFence</c> prep, so the Fence DAG sees a populated <c>FenceContext</c>. No-op when every deferred track is empty (e.g. parallel fence disabled).
    /// </summary>
    public void DispatchDeferredTracks()
    {
        for (var t = _deferredTrackStartIndex; t < _scheduledTracks.Length; t++)
        {
            DispatchTrack(t);
        }
    }

}
