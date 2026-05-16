using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
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
/// Between ticks, workers use a three-phase wait referencing <c>_nextTickTimestamp</c> (set by the timer thread). This saves CPU during the idle gap while
/// maintaining sub-microsecond wake latency in the final spin phase.
/// </para>
/// </remarks>
[PublicAPI]
public sealed partial class DagScheduler : HighResolutionTimerServiceBase
{
    // ═══════════════════════════════════════════════════════════════
    // Immutable DAG structure
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// All registered systems (user + internal), indexed by system index. Used by the scheduler for dispatch, lookup, and topological reasoning — the index
    /// identity is load-bearing for chunk dispatch state and dependency wiring, so this array stays as the canonical source for everything index-keyed
    /// (including diagnostics/profiler that walk every entry by raw index).
    /// </summary>
    /// <remarks>
    /// When you only care about systems the user registered (counting them in a test, iterating them in a DAG-viewer that hides engine plumbing), prefer
    /// <see cref="UserSystems"/> — it filters out <see cref="SystemDefinition.IsInternal"/> entries (e.g. <c>FenceExec</c>) so the count is stable regardless
    /// of which internal systems the runtime registers.
    /// </remarks>
    public SystemDefinition[] Systems { get; }

    /// <summary>
    /// User-registered systems only (filtered <see cref="Systems"/> with <see cref="SystemDefinition.IsInternal"/> removed). Indices in this view do NOT match
    /// the canonical system index — use this only for iteration and counting, not for index-keyed lookups.
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
                if (!Systems[i].IsInternal)
                {
                    arr[j++] = Systems[i];
                }
            }
            field = arr;
            return arr;
        }
    }

    private readonly int[] _rootSystems;          // User DAG roots (PredecessorCount == 0 AND !IsInternal)
    private readonly int[] _internalRootSystems;  // Internal sub-DAG roots (PredecessorCount == 0 AND IsInternal)
    private readonly int _internalSystemCount;    // Count of IsInternal systems
    private readonly int _workerCount;
    private readonly RuntimeOptions _options;
    private readonly int[] _topologicalOrder;
    private readonly EventQueueBase[] _eventQueues;

    // ═══════════════════════════════════════════════════════════════
    // Per-system mutable state (reset each tick)
    // ═══════════════════════════════════════════════════════════════

    private readonly CacheLinePaddedInt[] _nextChunk;
    private readonly CacheLinePaddedInt[] _remainingChunks;
    private readonly CacheLinePaddedInt[] _remainingDeps;
    private readonly CacheLinePaddedInt[] _isReady;
    private readonly bool[] _systemFailed;

    // Reset templates (immutable after construction)
    private readonly int[] _templateDeps;
    private readonly int[] _templateChunks;

    // ═══════════════════════════════════════════════════════════════
    // Workers
    // ═══════════════════════════════════════════════════════════════

    private readonly Thread[] _workers;

    // ═══════════════════════════════════════════════════════════════
    // Tick synchronization
    // ═══════════════════════════════════════════════════════════════

    private int _tickGeneration;
    private int _tickInProgress;
    private int _workerShutdown;
    private CacheLinePaddedInt _systemsRemaining;
    private long _nextTickTimestamp;       // Used by GetNextTick for metronome advancement
    private long _currentTickNumber;

    // Between-tick wake signal. Workers block on this (kernel wait = zero CPU).
    // Timer thread sets it when bumping the generation counter.
    // SpinCount=0: go straight to kernel wait (no user-mode spinning — the tick interval is ms-scale, so spinning would waste CPU for no benefit).
    private readonly ManualResetEventSlim _tickStartSignal = new(false, 0);

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

    /// <summary>Called before a CallbackSystem/QuerySystem executes. Creates per-system Transaction. Returns TickContext.</summary>
    internal Func<int, TickContext> SystemStartCallback;

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
    /// Public hook fired when the WorkerLoop's outer safety net catches an unhandled exception that escaped every inner try/catch (engine bug, runaway
    /// exception from inside a catch handler, etc.). User code can subscribe to log to file, send to telemetry, request graceful shutdown, or escalate to a
    /// debugger break. Invoked on the worker thread that caught the exception. Defaults to null (no-op).
    /// <para>
    /// <b>Note:</b> The vast majority of user-code exceptions are already caught by the per-system handlers in
    /// <see cref="ProcessParallelQuery"/>, <see cref="ProcessCallbackOrQuery"/>, <see cref="ProcessPipeline"/>, and
    /// <see cref="ExecuteInline"/>. Those paths log via <c>LogSystemException</c> and capture via
    /// <c>CaptureSystemException</c> without invoking this callback. This callback fires ONLY when the outer WorkerLoop safety net catches something — meaning
    /// an inner handler didn't, which is itself a bug worth surfacing prominently.
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
        if (_workerShutdown != 0)
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
        if (_workerShutdown != 0)
        {
            return;
        }

        if (_workerCount == 1)
        {
            ExecuteTickSingleThreaded(actualTick);
        }
        else
        {
            ExecuteTickMultiThreaded(actualTick);
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
    /// <param name="options">Runtime configuration.</param>
    /// <param name="parent">Parent resource node (typically <see cref="IResourceRegistry.Runtime"/>).</param>
    /// <param name="eventQueues">Event queues to reset at each tick start. Can be empty.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public DagScheduler(SystemDefinition[] systems, int[] topologicalOrder, RuntimeOptions options, IResource parent, EventQueueBase[] eventQueues = null, 
        ILogger logger = null) : base("DagScheduler", parent)
    {
        ArgumentNullException.ThrowIfNull(systems);
        ArgumentNullException.ThrowIfNull(topologicalOrder);
        ArgumentNullException.ThrowIfNull(options);

        Systems = systems;
        // #302: register PDB-resolved system source locations into the runtime manifest so the
        // exporters (FileExporter / TcpExporter) ship them alongside the compile-time call-site
        // attribution. Synthetic ids = 0x8000 | systemIndex distinguish system entries from spans.
        RuntimeSourceLocationManifest.SetSystems(systems);
        _topologicalOrder = topologicalOrder;
        AllSystemCount = systems.Length;
        _options = options;
        _eventQueues = eventQueues ?? [];
        // Assign stable queue IDs (#311) so the per-queue telemetry path can carry a small u16 instead of the queue's name on the wire.
        // QueueId == array index here; the cache builder writes a parallel `QueueNameTable` section so consumers can map index → name.
        for (var qi = 0; qi < _eventQueues.Length; qi++)
        {
            _eventQueues[qi].QueueId = (ushort)qi;
        }
        _logger = logger ?? NullLogger.Instance;
        _workerCount = options.ResolveWorkerCount();

        // Tick interval
        _tickIntervalTicks = Stopwatch.Frequency / options.BaseTickRate;

        // Find root systems (zero predecessors). Partition into user-DAG roots and internal-sub-DAG roots so the two schedules can be dispatched independently
        // — user systems run as the main tick body, internal systems (e.g. FenceExec) run in a second wake/barrier cycle invoked from TickEndCallback.
        var userRootCount = 0;
        var internalRootCount = 0;
        var internalCount = 0;
        for (var i = 0; i < AllSystemCount; i++)
        {
            if (systems[i].IsInternal)
            {
                internalCount++;
                if (systems[i].PredecessorCount == 0)
                {
                    internalRootCount++;
                }
            }
            else if (systems[i].PredecessorCount == 0)
            {
                userRootCount++;
            }
        }
        _internalSystemCount = internalCount;
        SystemCount = AllSystemCount - internalCount;

        _rootSystems = new int[userRootCount];
        _internalRootSystems = new int[internalRootCount];
        var userIdx = 0;
        var iIdx = 0;
        for (var i = 0; i < AllSystemCount; i++)
        {
            if (systems[i].PredecessorCount != 0)
            {
                continue;
            }

            if (systems[i].IsInternal)
            {
                _internalRootSystems[iIdx++] = i;
            }
            else
            {
                _rootSystems[userIdx++] = i;
            }
        }

        // Allocate per-system state arrays
        _nextChunk = new CacheLinePaddedInt[AllSystemCount];
        _remainingChunks = new CacheLinePaddedInt[AllSystemCount];
        _remainingDeps = new CacheLinePaddedInt[AllSystemCount];
        _isReady = new CacheLinePaddedInt[AllSystemCount];
        _systemFailed = new bool[AllSystemCount];

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

        // Create worker threads (not started yet)
        if (_workerCount > 1)
        {
            _workers = new Thread[_workerCount];
            for (var i = 0; i < _workerCount; i++)
            {
                var workerId = i;
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

        // Start worker threads
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
    /// Throws if a typed system was registered but no matching <c>RegisterContext&lt;TContext&gt;</c> call ran.
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
    /// Shuts down the scheduler: stops accepting new ticks, waits for the current tick to finish, joins worker threads, then stops the timer thread.
    /// </summary>
    public void Shutdown()
    {
        LogShutdownRequested();

        // Signal workers to exit
        _workerShutdown = 1;
        Interlocked.Increment(ref _tickGeneration);
        _tickStartSignal.Set(); // Wake any blocked workers

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
            _workerShutdown = 1;
            Interlocked.Increment(ref _tickGeneration);
            _tickStartSignal.Set();
            JoinWorkers();
            _tickStartSignal.Dispose();
        }

        // Base class stops the timer thread and disposes the resource node
        base.Dispose(disposing);
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

    /// <summary>Number of worker threads.</summary>
    public int WorkerCount => _workerCount;

    /// <summary>
    /// Number of user-registered systems in the DAG (excludes <see cref="SystemDefinition.IsInternal"/> entries like <c>FenceExec</c>). This is the count
    /// callers usually want — "the systems I registered". For the total including engine-internal systems use <see cref="AllSystemCount"/>.
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

        // 3. Mark root systems ready (evaluate ShouldRun and ReactiveSkip for roots before waking workers)
        var readyNow = Stopwatch.GetTimestamp();
        foreach (var root in _rootSystems)
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
                    MarkSystemReady(root);
                }
            }
        }

        // 3. Activate tick — bump generation + signal workers (D7)
        _tickInProgress = 1;
        Interlocked.Increment(ref _tickGeneration);
        _tickStartSignal.Set();

        // 4. Wait for all systems to complete.
        //    The timer thread must spin here — Thread.Yield() on Windows can delay up to 15.6ms,
        //    which would cascade into all subsequent ticks. One core spinning for the tick
        //    duration (~0.1-5ms) is acceptable; delayed completion detection is not.
        while (_systemsRemaining.Value > 0)
        {
            Thread.SpinWait(1);
        }

        _tickInProgress = 0;
        _tickStartSignal.Reset(); // Workers will block again on next between-tick wait

        // 5. Tick end hook (TyphonRuntime flushes/disposes UoW)
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

        // Execute in topological order
        for (var i = 0; i < _topologicalOrder.Length; i++)
        {
            var sysIdx = _topologicalOrder[i];
            var sys = Systems[sysIdx];

            // Internal systems (engine-internal sub-DAG, e.g. FenceExec) are not part of the user DAG cycle. The runtime dispatches them separately from
            // TickEndCallback after the user DAG completes — see DispatchInternalSchedule.
            if (sys.IsInternal)
            {
                continue;
            }

            var readyTick = Stopwatch.GetTimestamp();
            _currentTickSystemMetrics[sysIdx].ReadyTick = readyTick;
            InspectorSystemReady(sysIdx, readyTick);

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

                continue;
            }

            // Evaluate ShouldRun (untyped delegate + typed virtual for ChunkedCallbackSystem<TContext>).
            bool shouldRunSingle = sys.ShouldRun?.Invoke() ?? true;
            if (shouldRunSingle && sys.Instance is ChunkedCallbackSystem ccsSingle)
            {
                try { shouldRunSingle = ccsSingle.OnShouldRun(); }
                catch { _systemFailed[sysIdx] = true; continue; }
            }
            if (!shouldRunSingle)
            {
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.ShouldRunFalse;
                InspectorSystemSkipped(sysIdx, SkipReason.ShouldRunFalse, Stopwatch.GetTimestamp());
                continue;
            }

            // Prepare gate (chunked systems only). Single-threaded mode: still needs to run so plans get built.
            if (sys.Instance is ChunkedCallbackSystem ccsSingle2)
            {
                int chunks;
                try { chunks = ccsSingle2.OnPrepare(); }
                catch { _systemFailed[sysIdx] = true; continue; }

                if (chunks == 0)
                {
                    _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.EmptyInput;
                    InspectorSystemSkipped(sysIdx, SkipReason.EmptyInput, Stopwatch.GetTimestamp());
                    continue;
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
                continue;
            }

            {
                var overloadSkip = CheckOverloadSkip(sysIdx);
                if (overloadSkip != SkipReason.NotSkipped)
                {
                    _currentTickSystemMetrics[sysIdx].SkipReason = overloadSkip;
                    InspectorSystemSkipped(sysIdx, overloadSkip, Stopwatch.GetTimestamp());
                    continue;
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
                                LogSystemException(sysIdx, sys.Name, ex); CaptureSystemException(sysIdx, ex);
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
                            LogSystemException(sysIdx, sys.Name, ex); CaptureSystemException(sysIdx, ex);
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
                    var ctx = SystemStartCallback?.Invoke(sysIdx) ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f };
                    SystemAccessValidator.EnterSystem(sys.Access, sys.Name);
                    try
                    {
                        sys.CallbackAction(ctx);
                    }
                    catch (Exception ex)
                    {
                        _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                        _systemFailed[sysIdx] = true;
                        LogSystemException(sysIdx, sys.Name, ex); CaptureSystemException(sysIdx, ex);
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

        // Tick end hook
        TickEndCallback?.Invoke(this);

        var tickEndTimestamp = Stopwatch.GetTimestamp();
        InspectorTickEnd(_currentTickNumber, tickEndTimestamp);

        // Profiler gauge snapshot (post-TickEnd). Same contract as the multi-threaded path.
        GaugeSnapshotCallback?.Invoke(this);

        ComputeAndRecordTelemetry(tickStartTimestamp, tickEndTimestamp);
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

        var lastGen = _tickGeneration;

        while (_workerShutdown == 0)
        {
            // ═══ Between-tick: kernel wait on signal ═══
            // Workers block here with zero CPU cost. The timer thread signals _tickStartSignal when the next tick fires. Wake latency is ~1-5µs
            // (kernel transition) — negligible against a 16ms tick gap.
            var betweenTickSpan = TyphonEvent.BeginSchedulerWorkerBetweenTick((byte)workerId);
            var btStart = Stopwatch.GetTimestamp();
            while (_tickGeneration == lastGen)
            {
                if (_workerShutdown != 0)
                {
                    betweenTickSpan.WakeReason = 1; // shutdown
                    var btEnd = Stopwatch.GetTimestamp();
                    var btUs1 = (btEnd - btStart) * 1_000_000L / Stopwatch.Frequency;
                    betweenTickSpan.WaitUs = (uint)Math.Min(btUs1, uint.MaxValue);
                    betweenTickSpan.Dispose();
                    return;
                }

                _tickStartSignal.Wait(TimeSpan.FromMilliseconds(50));
            }
            {
                var btEnd = Stopwatch.GetTimestamp();
                var btUs2 = (btEnd - btStart) * 1_000_000L / Stopwatch.Frequency;
                betweenTickSpan.WakeReason = 0; // signal
                betweenTickSpan.WaitUs = (uint)Math.Min(btUs2, uint.MaxValue);
                betweenTickSpan.Dispose();
                TyphonEvent.EmitSchedulerWorkerWake((byte)workerId, (uint)Math.Min(btUs2, uint.MaxValue));
            }

            if (_workerShutdown != 0)
            {
                return;
            }

            lastGen = _tickGeneration;

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
                        LogSystemException(sysIdx, sysName, ex);
                        CaptureSystemException(sysIdx, ex);
                        try { UnhandledExceptionCallback?.Invoke(sysIdx, sysName, ex); }
                        catch { /* swallow — the callback itself threw; we're the last line of defense */ }
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
                    if (idleSpins <= 100)
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
    /// Linear scan of ready systems. Returns the index of a system that can be processed, or -1 if no work is available.
    /// </summary>
    /// <remarks>
    /// POC validated this O(n) scan is negligible up to 1,000 systems.
    /// The _isReady array fits in 2 cache lines for 16 systems — hot in L1.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FindReadySystem()
    {
        for (var i = 0; i < AllSystemCount; i++)
        {
            if (_isReady[i].Value != 1)
            {
                continue;
            }

            if (Systems[i].Type == SystemType.PipelineSystem || Systems[i].IsParallelQuery)
            {
                // Multi-chunk system: only return if chunks remain
                if (_nextChunk[i].Value < Systems[i].TotalChunks)
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
        TyphonEvent.EmitSchedulerDispense((ushort)sysIdx, 0, (byte)workerId);

        // ShouldRun was already evaluated at dispatch time (OnSystemComplete or root marking).
        // System lifecycle hook: create per-system Transaction (called on the worker thread)
        var ctx = SystemStartCallback?.Invoke(sysIdx) ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f };
        var workStart = Stopwatch.GetTimestamp();
        RecordFirstChunkGrab(sysIdx, workStart);
        InspectorChunkStart(sysIdx, 0, workStart, 1);

        var success = true;
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
            LogSystemException(sysIdx, Systems[sysIdx].Name, ex); CaptureSystemException(sysIdx, ex);
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

        while (true)
        {
            // Failure-drain: a prior chunk threw. We must NOT just break — `FindReadySystem`
            // still returns this sysIdx as long as `_nextChunk < TotalChunks` (chunks unclaimed),
            // so workers would loop into `ProcessPipeline`, hit this branch, break, and spin —
            // a full-CPU wedge that never fires `OnSystemComplete` and never dispatches successors.
            // Instead, claim the remaining chunks via `_nextChunk` (advancing it past TotalChunks
            // closes the FindReadySystem gate) and decrement `_remainingChunks` for each so the
            // last decrementer can fire `OnSystemComplete`. The chunks themselves stay unrun —
            // remaining work is discarded as before.
            if (_systemFailed[sysIdx])
            {
                DrainFailedSystemChunks(sysIdx, workerId, trackUtilization);
                return;
            }

            var chunk = Interlocked.Increment(ref _nextChunk[sysIdx].Value) - 1;
            if (chunk >= sys.TotalChunks)
            {
                break;
            }
            TyphonEvent.EmitSchedulerDispense((ushort)sysIdx, chunk, (byte)workerId);

            if (chunk == 0)
            {
                RecordFirstChunkGrab(sysIdx, Stopwatch.GetTimestamp());
            }

            var workStart = Stopwatch.GetTimestamp();
            InspectorChunkStart(sysIdx, chunk, workStart, sys.TotalChunks);
            SystemAccessValidator.EnterSystem(sys.Access, sys.Name);
            try
            {
                sys.PipelineChunkAction(chunk, sys.TotalChunks);
            }
            catch (Exception ex)
            {
                _systemFailed[sysIdx] = true;
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                LogSystemException(sysIdx, sys.Name, ex); CaptureSystemException(sysIdx, ex);
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
    /// Drain mode for a failed multi-chunk system. Claims every chunk that was never grabbed
    /// (advances <c>_nextChunk</c> past <c>TotalChunks</c> via repeated <c>Increment</c>), and
    /// decrements <c>_remainingChunks</c> once per claim — without running the chunk body. The
    /// last decrementer (whichever worker happens to drive remaining to zero, possibly the same
    /// worker that hit the original exception) fires <see cref="OnSystemComplete"/> so successors
    /// get dispatched and the tick can finish.
    /// </summary>
    /// <remarks>
    /// Why drain instead of break-and-bail: <see cref="FindReadySystem"/> uses
    /// <c>_nextChunk[i].Value &lt; TotalChunks</c> as the "still has work" signal. Without
    /// advancing <c>_nextChunk</c>, every worker that picks this sysIdx after the failure walks
    /// into <see cref="ProcessPipeline"/> / <see cref="ProcessParallelQuery"/>, sees
    /// <c>_systemFailed</c>, breaks, and immediately re-enters the worker loop — a tight
    /// full-CPU spin that never fires <c>OnSystemComplete</c>. Multiple workers can race here
    /// concurrently; <c>Interlocked.Increment</c> + <c>Interlocked.Decrement</c> give us a clean
    /// claim/decrement protocol where exactly one worker observes <c>remaining == 0</c>.
    /// </remarks>
    private void DrainFailedSystemChunks(int sysIdx, int workerId, bool trackUtilization)
    {
        var totalChunks = Systems[sysIdx].TotalChunks;
        while (true)
        {
            var chunk = Interlocked.Increment(ref _nextChunk[sysIdx].Value) - 1;
            if (chunk >= totalChunks)
            {
                // No more chunks to claim. Either we already drove _remainingChunks to zero
                // (handled below) or another worker did — either way, this worker's job is done.
                return;
            }
            // Successfully claimed `chunk` without running it. Decrement remaining so the last
            // claim fires OnSystemComplete and unblocks successor dispatch.
            var remaining = Interlocked.Decrement(ref _remainingChunks[sysIdx].Value);
            if (remaining == 0)
            {
                RecordSystemDone(sysIdx, Stopwatch.GetTimestamp());
                // Mirror the success-path SystemEndCallback so a failed parallel system still gets its lifecycle hook
                // (e.g. Phase A telemetry, transaction rollback wiring). success=false because we got here by drain.
                SystemEndCallback?.Invoke(sysIdx, false);
                OnSystemComplete(sysIdx, workerId, trackUtilization);
                return;
            }
        }
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
        while (true)
        {
            // See `ProcessPipeline` for the full rationale — same wedge applies here. Drain the
            // remaining chunks so `FindReadySystem` stops returning this sysIdx and the last
            // decrementer fires `OnSystemComplete` to dispatch successors.
            if (_systemFailed[sysIdx])
            {
                DrainFailedSystemChunks(sysIdx, workerId, trackUtilization);
                return;
            }

            var chunk = Interlocked.Increment(ref _nextChunk[sysIdx].Value) - 1;
            if (chunk >= sys.TotalChunks)
            {
                break;
            }
            TyphonEvent.EmitSchedulerDispense((ushort)sysIdx, chunk, (byte)workerId);

            if (chunk == 0)
            {
                RecordFirstChunkGrab(sysIdx, Stopwatch.GetTimestamp());
            }

            var workStart = Stopwatch.GetTimestamp();
            InspectorChunkStart(sysIdx, chunk, workStart, sys.TotalChunks);
            SystemAccessValidator.EnterSystem(sys.Access, sys.Name);
            try
            {
                ParallelQueryChunkCallback?.Invoke(sysIdx, chunk, sys.TotalChunks, workerId);
            }
            catch (Exception ex)
            {
                _systemFailed[sysIdx] = true;
                _currentTickSystemMetrics[sysIdx].SkipReason = SkipReason.Exception;
                LogSystemException(sysIdx, sys.Name, ex); CaptureSystemException(sysIdx, ex);
            }
            finally
            {
                SystemAccessValidator.LeaveSystem();
            }

            var workEnd = Stopwatch.GetTimestamp();
            InspectorChunkEnd(sysIdx, chunk, workEnd, _currentTickSystemMetrics[sysIdx].EntitiesProcessed);

            if (trackUtilization)
            {
                _workerActiveTicks[workerId] += workEnd - workStart;
            }

            // D8: countdown — last completer dispatches successors and runs cleanup
            var remaining = Interlocked.Decrement(ref _remainingChunks[sysIdx].Value);
            if (remaining == 0)
            {
                RecordSystemDone(sysIdx, workEnd);
                _currentTickSystemMetrics[sysIdx].WorkersTouched = sys.TotalChunks;
                // Issue #234: cleanup may return true to re-dispatch for another phase (checkerboard Black after Red).
                var reDispatch = ParallelQueryCleanupCallback?.Invoke(sysIdx) ?? false;
                if (reDispatch)
                {
                    DispatchParallelQuery(sysIdx, workerId, trackUtilization);
                }
                else
                {
                    // System is genuinely done (no further checkerboard phase). Fire the system-end lifecycle hook so
                    // #327 Phase A emits its per-(system, archetype) row — this path was missing the call entirely,
                    // which is why parallel-query systems produced zero `SchedulerSystemArchetypeEvent` records on
                    // real workloads despite being correctly bound at runtime construction.
                    SystemEndCallback?.Invoke(sysIdx, !_systemFailed[sysIdx]);
                    OnSystemComplete(sysIdx, workerId, trackUtilization);
                }
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
        // Issue #234: reset chunk counter for re-dispatch (checkerboard phase B). No-op for first dispatch (already 0 from ResetTickState).
        _nextChunk[sysIdx].Value = 0;

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
        // MEMORY: relies on x86 TSO for store ordering — needs release/acquire barrier on ARM
        MarkSystemReady(sysIdx);
    }

    // ═══════════════════════════════════════════════════════════════
    // Completion and successor dispatch
    // ═══════════════════════════════════════════════════════════════

    private void OnSystemComplete(int sysIdx, int workerId, bool trackUtilization)
    {
        Interlocked.Decrement(ref _systemsRemaining.Value);

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
                    var readyTs = Stopwatch.GetTimestamp();
                    _currentTickSystemMetrics[succIdx].ReadyTick = readyTs;
                    InspectorSystemReady(succIdx, readyTs);
                    TyphonEvent.EmitSchedulerDependencyReady((ushort)sysIdx, (ushort)succIdx, (ushort)successors.Length, 0);
                    var succ = Systems[succIdx];

                    // Check if any predecessor failed — skip this system entirely
                    if (_systemFailed[succIdx])
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
            catch
            {
                _systemFailed[succIdx] = true;
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
            catch
            {
                _systemFailed[succIdx] = true;
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
        var ctx = SystemStartCallback?.Invoke(sysIdx) ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f };
        var workStart = Stopwatch.GetTimestamp();
        RecordFirstChunkGrab(sysIdx, workStart);
        InspectorChunkStart(sysIdx, 0, workStart, 1);

        var success = true;
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
            LogSystemException(sysIdx, Systems[sysIdx].Name, ex); CaptureSystemException(sysIdx, ex);
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

    private void JoinWorkers()
    {
        foreach (var worker in _workers)
        {
            if ((worker.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
            {
                worker.Join(TimeSpan.FromSeconds(5));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkSystemReady(int sysIdx) => _isReady[sysIdx].Value = 1;

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
        // Resets per-system state for every entry in Systems[] — both user and internal — but sets _systemsRemaining to the USER count only, so the
        // user-DAG dispatch loop terminates when all user systems are done. Internal systems' state sits idle until DispatchInternalSchedule wakes them in the
        // second cycle. Internal systems are never marked ready during user dispatch (they're filtered out of _rootSystems, and any cross-edge from a user
        // successor to an internal predecessor is treated as a configuration error — internal systems form an independent sub-DAG by contract).
        for (var i = 0; i < AllSystemCount; i++)
        {
            _nextChunk[i].Value = 0;
            _remainingChunks[i].Value = _templateChunks[i];
            _remainingDeps[i].Value = _templateDeps[i];
            _isReady[i].Value = 0;
            _currentTickSystemMetrics[i] = default;
        }

        _systemsRemaining.Value = SystemCount;
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
    /// Dispatches the internal sub-DAG (systems flagged via <see cref="SystemBuilder.Internal"/>). Called by <c>TyphonRuntime.OnTickEndInternal</c> after the
    /// user DAG completes — typically after FencePrep has bound the per-tick <c>FenceWorkPlan</c> and set <see cref="SystemDefinition.RuntimeChunkCount"/> on
    /// the chunked-callback systems.
    ///
    /// <para>Reuses the same worker wake/barrier primitives (<c>_tickStartSignal</c> + <c>_systemsRemaining</c>) as the user dispatch. Workers in
    /// <c>WorkerLoop</c> simply pick up the next ready system from <see cref="Systems"/> — they don't need to know about the schedule kind because internal
    /// systems were filtered out of the user roots and only get marked ready here.</para>
    ///
    /// <para>No-op when there are zero internal systems registered.</para>
    /// </summary>
    public void DispatchInternalSchedule()
    {
        if (_internalSystemCount == 0)
        {
            return;
        }

        if (_workerCount == 1)
        {
            // Single-threaded mode: no worker pool exists, so we can't use the wake/barrier path. Run internal systems synchronously on the TickDriver thread,
            // mirroring how ExecuteTickSingleThreaded handles parallel queries (loop chunks directly via ParallelQueryChunkCallback, no _systemsRemaining
            // mechanics).
            foreach (var root in _internalRootSystems)
            {
                DispatchInternalSystemSync(root);
            }
            return;
        }

        // Multi-threaded mode: reuse the worker pool. Per-system state was reset at user-tick start (ResetTickState). Between the user DAG completion and now,
        // internal systems were never marked ready, so _remainingDeps / _nextChunk / _remainingChunks / _isReady still hold their post-reset values for
        // internal indices. DispatchParallelQuery's prepare callback (TyphonRuntime.OnParallelQueryPrepare) reads RuntimeChunkCount and sets
        // TotalChunks + _remainingChunks accordingly when MarkSystemReady is called below.
        _systemsRemaining.Value = _internalSystemCount;

        var readyNow = Stopwatch.GetTimestamp();
        foreach (var root in _internalRootSystems)
        {
            _currentTickSystemMetrics[root].ReadyTick = readyNow;
            InspectorSystemReady(root, readyNow);
            var sys = Systems[root];
            ushort rootSkipUnused = 0;
            if (!EvaluateShouldRunAndPrepare(root, -1, false, ref rootSkipUnused))
            {
                // Skip / Prepare-dispatch already handled by helper.
            }
            else if (sys.IsParallelQuery)
            {
                DispatchParallelQuery(root, -1, false);
            }
            else
            {
                MarkSystemReady(root);
            }
        }

        // Wake workers for the internal pass. Workers exited their dispatch loop when _systemsRemaining hit 0 at the end of the user pass; they're now back in
        // the between-tick Wait. Bumping _tickGeneration + setting the signal pulls them back into FindReadySystem, where only internal systems are marked ready.
        _tickInProgress = 1;
        Interlocked.Increment(ref _tickGeneration);
        _tickStartSignal.Set();

        while (_systemsRemaining.Value > 0)
        {
            Thread.SpinWait(1);
        }

        _tickInProgress = 0;
        _tickStartSignal.Reset();
    }

    /// <summary>
    /// Dispatch ONE internal system (not its successors) in isolation. Used by the 3-phase parallel fence — TickDriver calls this once per phase (Prep, then
    /// Migrate, then Finalize), doing inter-phase serial bookkeeping between calls. The system's <c>DependsOn</c> predecessor edge is ignored: the caller is
    /// responsible for sequencing.
    /// </summary>
    /// <remarks>
    /// Resets the per-system chunk-claim / completion state for this single system before dispatch, since <see cref="ResetTickState"/> only runs once at
    /// user-tick start — a 2nd or 3rd phase dispatch in the same tick would otherwise see stale "completed" state from the prior phase.
    /// </remarks>
    public void DispatchInternalSystem(int sysIdx)
    {
        if (_workerCount == 1)
        {
            DispatchInternalSystemSync(sysIdx);
            return;
        }

        // Reset per-system state for this single phase entry. Match the per-index slice of ResetTickState so the ready-mark / chunk-claim machinery sees a
        // clean slot.
        _nextChunk[sysIdx].Value = 0;
        _remainingChunks[sysIdx].Value = 0;
        _isReady[sysIdx].Value = 0;
        _remainingDeps[sysIdx].Value = 0; // bypass DAG predecessor count — caller serializes phases
        _currentTickSystemMetrics[sysIdx] = default;
        _systemFailed[sysIdx] = false;

        _systemsRemaining.Value = 1;

        var readyNow = Stopwatch.GetTimestamp();
        _currentTickSystemMetrics[sysIdx].ReadyTick = readyNow;
        InspectorSystemReady(sysIdx, readyNow);
        var sys = Systems[sysIdx];
        ushort dispatchSkipUnused = 0;
        if (!EvaluateShouldRunAndPrepare(sysIdx, -1, false, ref dispatchSkipUnused))
        {
            // Skip / Prepare-dispatch already handled by helper.
        }
        else if (sys.IsParallelQuery)
        {
            DispatchParallelQuery(sysIdx, -1, false);
        }
        else
        {
            MarkSystemReady(sysIdx);
        }

        // Wake the worker pool. Bumping _tickGeneration is required for workers parked in their between-tick Wait.
        _tickInProgress = 1;
        Interlocked.Increment(ref _tickGeneration);
        _tickStartSignal.Set();

        while (_systemsRemaining.Value > 0)
        {
            Thread.SpinWait(1);
        }

        _tickInProgress = 0;
        _tickStartSignal.Reset();
    }

    /// <summary>
    /// Synchronous internal-system dispatch for single-threaded mode (<c>WorkerCount == 1</c>). Runs the system's callback or chunks directly on the TickDriver
    /// thread, then recurses into any internal-flagged successors. Mirrors the ExecuteTickSingleThreaded structure for parallel queries — minus the
    /// failure/overload/skip machinery, which doesn't apply to engine-internal systems.
    /// </summary>
    private void DispatchInternalSystemSync(int sysIdx)
    {
        var sys = Systems[sysIdx];
        if (sys.ShouldRun != null && !sys.ShouldRun())
        {
            return;
        }

        if (sys.IsParallelQuery)
        {
            var totalChunks = ParallelQueryPrepareCallback?.Invoke(sysIdx) ?? 0;
            if (totalChunks > 0)
            {
                Systems[sysIdx].TotalChunks = totalChunks;
                for (var chunk = 0; chunk < totalChunks; chunk++)
                {
                    ParallelQueryChunkCallback?.Invoke(sysIdx, chunk, totalChunks, 0);
                }
            }
        }
        else
        {
            var ctx = SystemStartCallback?.Invoke(sysIdx) ?? new TickContext { TickNumber = _currentTickNumber, DeltaTime = 0f };
            sys.CallbackAction(ctx);
        }

        // Walk internal successors so chained internal systems run in topological order. In v1 the internal sub-DAG contains only FenceExec with no
        // successors — this loop is a no-op in practice but future-proofs the path.
        var successors = sys.Successors;
        for (var i = 0; i < successors.Length; i++)
        {
            var succ = successors[i];
            if (Systems[succ].IsInternal)
            {
                DispatchInternalSystemSync(succ);
            }
        }
    }

}
