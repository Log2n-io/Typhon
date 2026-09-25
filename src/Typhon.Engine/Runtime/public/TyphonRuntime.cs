using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Typhon.Engine.internals;
using Typhon.Profiler;
using Typhon.Schema.Definition;

namespace Typhon.Engine;

/// <summary>
/// Top-level runtime for Typhon game servers. Wraps <see cref="DatabaseEngine"/> and <see cref="DagScheduler"/>, managing the per-tick UoW/Transaction
/// lifecycle so game developers never handle commits manually.
/// </summary>
/// <remarks>
/// <para>
/// Each tick: creates a UoW (Deferred) → for each CallbackSystem/QuerySystem, creates a Transaction on the executing worker
/// thread (respecting thread affinity) → commits after each system → flushes UoW at tick end.
/// </para>
/// <para>
/// Pipeline systems do not receive Transactions — their entity access goes through Gather/Scatter pipelines.
/// </para>
/// </remarks>
[PublicAPI]
public sealed partial class TyphonRuntime : IDisposable
{
    private readonly ILogger _logger;

    // Tick-level UoW (created at tick start, disposed at tick end)
    private UnitOfWork _currentUow;

    // Engine-internal fence executors (parallel WriteTickFence — chained Prep→Migrate→Finalize phases). Null when EnableParallelFence is false —
    // OnTickEndInternal then falls back to the legacy single-threaded WriteTickFence path. Each phase has its own FenceWorkPlan instance (rebuilt every tick).
    private readonly FencePrepExecSystem _fencePrepExec;
    private readonly FenceMigrateExecSystem _fenceMigrateExec;
    private readonly FenceIndexMassUpdateExecSystem _fenceIndexMassUpdateExec;
    private readonly FenceEntityMapUpdateExecSystem _fenceEntityMapUpdateExec;
    private readonly FenceAabbRefreshExecSystem _fenceAabbRefreshExec;
    private readonly FenceFinalizeExecSystem _fenceFinalizeExec;
    private readonly LiveFenceCostModel _liveFenceCost;
    private readonly bool _parallelFenceEnabled;

    // Engine-owned replication (#955). The track is always declared — unlike the Fence DAG it has no alternative implementation, so
    // there is nothing to fall back to and no switch worth offering. With nothing subscribed its cost is the four gate checks, but only because
    // DispatchTrackMultiThreaded now returns before waking the pool for a track that drained inline; until that was fixed a declared-but-idle track cost
    // a full wake/barrier cycle every tick. The context is owned here rather than on DatabaseEngine because nothing in the engine touches v2; see
    // SubscriptionsContext's remarks.
    private readonly SubscriptionsContext _subscriptionsContext = new();

    // The database's network identities, shared by every replicated archetype because netIds are global: the wire encodes an event once and memcpy's it to
    // every receiver on the strength of that, and an entityRef arrives with no archetype to disambiguate it. Constructed in the ctor rather than inline
    // because it is a resource-graph node and needs a parent.
    private readonly NetIdAllocator _netIds;

    // What the application declares about replication: projections, profiles, sessions, commands, events and metrics. Holds no tick-time state of its own —
    // Start compiles it, and from then on it is frozen and the compiled plan is what the track reads.
    private readonly SubscriptionsRegistry _subscriptions;

    // What those declarations became: the compiled plan, the catalog WELCOME carries, the session table, the pools and the per-archetype replication state.
    // Built at Start because compiling needs the engine's archetypes initialised, and disposed with the runtime. Every later replication field belongs on IT,
    // not here and not on the tick context — see SubscriptionsRuntime's remarks.
    private SubscriptionsRuntime _subscriptionsRuntime;

    /// <summary>The replication track's tick-scoped context. Internal: the session count is written by ingress, and tests read its ordering journal.</summary>
    internal SubscriptionsContext SubscriptionsContextForTest => _subscriptionsContext;

    /// <summary>
    /// Invoked on the TickDriver at the very end of every tick that ran, with the context whose ordering journal has just been sealed.
    /// </summary>
    /// <remarks>
    /// The instrument SUB-02's verifier is written against. Asserting a phase ORDER from the test thread means reading four counters that the tick driver is
    /// concurrently resetting, and any such read can straddle a tick boundary; handing the sealed journal to a callback ON the driver thread removes the race
    /// rather than narrowing it. Null in every production path, so the cost is one null check per tick.
    /// </remarks>
    internal Action<SubscriptionsContext> SubscriptionsJournalObserver;

    // Per-system transaction tracking. Only one worker processes a given system index at a time (CAS on _isReady ensures single claimer), so no contention
    // on these slots.
    private readonly Transaction[] _systemTransactions;

    private readonly ViewBase[] _systemViews;                      // Resolved View per system (null if no input)
    // Per-system Stopwatch start ticks captured at OnSystemStart / OnParallelQueryPrepare; consumed by OnSystemEnd to emit a per-tick QueryPlan span (#342
    // follow-up). Zero means "no plan tracked this tick" — pull-mode views never produce a QueryPlan from BuildPlan, so the runtime drives the bracket explicitly.
    private readonly long[] _systemQueryPlanStartTicks;
    private readonly ComponentTable[][] _systemChangeFilterTables; // ComponentTables for changeFilter types (null if no filter)
    private readonly ArchetypeClusterState[] _systemClusterStates; // Cluster state for single-archetype cluster-eligible systems (null if not applicable)
    // Workbench Data Flow module (#327): the archetype id this system operates on, parallel to _systemClusterStates.
    // ushort.MaxValue means "not bound to a single archetype" — used by SchedulerSystemArchetypeEvent emission to skip systems
    // that don't fit the per-(system, archetype) telemetry model (callbacks, multi-archetype scans).
    private readonly ushort[] _systemArchetypeIds;
    private readonly PooledEntityList[] _systemEntityLists;        // For returning ArrayPool buffers
    private readonly EventQueueBase[][] _systemConsumedQueues;     // Pre-allocated consumed queue refs per system (null if none)
    private readonly PooledEntityList[] _parallelEntityLists;      // Full entity set for parallel QuerySystem chunk slicing
    private readonly HashMap<long>[] _multiTableFilterSets;        // Cached dedup sets for multi-table BuildFilteredEntitySet (avoids per-tick alloc)
    private readonly PointInTimeAccessor[] _parallelAccessors;      // Per-system reusable PTAs — Attach()ed each tick (per-system to avoid race with DAG-concurrent systems)
    private readonly PartitionEntityView[][] _partitionViews;      // Per-system per-worker partition views [sysIdx][workerId] — inner index is workerId, NOT chunkIndex (chunks may exceed worker count when ChunksPerWorker > 1)

    // Issue #231: per-system cluster-id partition source for tier-filtered dispatch. Non-null only when the system has a tier filter AND has a cluster state.
    // For non-amortized systems this points DIRECTLY at the per-archetype TierClusterIndex's per-tier buffer (zero-copy). For amortized systems
    // (cellAmortize > 0) it points at the per-system grow-on-demand buffer in _systemAmortizationBuffers, which contains only this tick's bucket.
    // Refreshed each tick inside OnParallelQueryPrepare. Decoupling these into per-system slots avoids the BUG-2 race on shared state.
    private readonly int[][] _systemTierClusterIds;
    private readonly int[] _systemTierClusterCount;
    // Issue #231 BUG-2 fix: per-system grow-on-demand buffer for amortized cluster ids. Owned exclusively by OnParallelQueryPrepare → ExecuteChunkWith*. Reused
    // across ticks; doubles on overflow. Null until the first amortized dispatch.
    private readonly int[][] _systemAmortizationBuffers;
    // RT-1 (Realms): how many times each system has run — incremented once per run at its entry point (OnParallelQueryPrepare's first call of a tick,
    // OnSystemStartInternal), never for a tick the scheduler skipped. The cellAmortize bucket is keyed on it rather than on the tick number, so a
    // TickDivisor sharing a factor with cellAmortize cannot starve a bucket (tick % 2 is always 0 for a system that runs every other tick).
    private readonly long[] _systemRunCount;
    // Issue #231: per-system cluster-range entity view, allocated lazily the first time a tier-filtered system runs Path 1 (full non-versioned). Reused across
    // ticks. [sysIdx][workerIdx]. Null slot = not allocated yet.
    private readonly ClusterRangeEntityView[][] _tierRangeViews;
    // The cost rule's input (RuntimeOptions.CostBasedChunking): each parallel QuerySystem's worker time per entity, in Stopwatch ticks, at its last
    // dispatch. Written at tick end on the tick driver (CaptureChunkCosts), read by the next dispatch's Prepare. Zero = no measurement yet: entity rule.
    private readonly double[] _chunkTicksPerEntity;
    // The cluster list a parallel QuerySystem's live dispatch splits, and its length, read once in Prepare. Its chunks walk this array and split this
    // length, never the live pair (CD-02).
    private readonly int[][] _dispatchClusterIds;
    private readonly int[] _dispatchClusterCount;
    // Issue #234: checkerboard two-phase dispatch. Phase tracking + Red/Black cluster buffers per system.
    // _checkerboardPhase: 0 = not checkerboard or reset, 1 = Red (phase A active), 2 = Black (phase B active).
    private readonly int[] _checkerboardPhase;
    private readonly int[][] _checkerboardRedIds;
    private readonly int[] _checkerboardRedCount;
    private readonly int[][] _checkerboardBlackIds;
    private readonly int[] _checkerboardBlackCount;

    // Cached delegate — avoids per-TickContext allocation from method group conversion
    private readonly SideTransactionFactory _createSideTxDelegate;

    // First-tick flag
    private bool _firstTickExecuted;

    // Issue #234: per-tier budget metrics. Computed at tick end, exposed as _previousTickMetrics on the next tick's TickContext.
    private TierBudgetMetrics _previousTickMetrics;

    // DeltaTime tracking
    private long _previousTickTimestamp;
    private float _currentDeltaTime;

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle events
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired once on the first tick. Use to rebuild transient state after crash recovery. The callback receives a valid Transaction for entity operations.
    /// </summary>
    public event Action<TickContext> OnFirstTick;

    /// <summary>
    /// Fired during <see cref="Shutdown"/>. Use for cleanup (save player state, etc.). The callback receives a dedicated Transaction (Immediate durability).
    /// </summary>
    public event Action<TickContext> OnShutdown;

    // ═══════════════════════════════════════════════════════════════
    // Public properties
    // ═══════════════════════════════════════════════════════════════

    /// <summary>The underlying database engine.</summary>
    public DatabaseEngine Engine { get; }

    /// <summary>The DAG scheduler driving tick execution.</summary>
    public DagScheduler Scheduler { get; }

    /// <summary>The runtime options this runtime was created with. The profiler derives session metadata from these.</summary>
    public RuntimeOptions Options { get; }

    /// <summary>Telemetry ring buffer for diagnostic inspection.</summary>
    public TickTelemetryRing Telemetry => Scheduler.Telemetry;

    /// <summary>Number of ticks executed so far.</summary>
    public long CurrentTickNumber => Scheduler.CurrentTickNumber;

    /// <summary>
    /// The archetype this system was bound to for parallel cluster dispatch, or <see cref="ushort.MaxValue"/> when it was bound to none — gate 1 of the
    /// Data Flow touch rollup (#327). Exposed for tests: the binding is otherwise unobservable, and a rollup that silently emits nothing is exactly the
    /// failure #631 reported.
    /// </summary>
    internal ushort SystemArchetypeIdOf(int sysIdx) => (uint)sysIdx < (uint)_systemArchetypeIds.Length ? _systemArchetypeIds[sysIdx] : ushort.MaxValue;

    /// <summary>Current overload response level.</summary>
    public OverloadLevel CurrentOverloadLevel => Scheduler.CurrentOverloadLevel;

    /// <summary>
    /// All registered systems including engine-internal ones (e.g. <c>FenceExec</c>). Use this when you need the full system list, indexed by canonical system
    /// index. For "the systems the user registered" use <see cref="UserSystems"/> instead.
    /// </summary>
    public SystemDefinition[] Systems => Scheduler.Systems;

    /// <summary>User-registered systems only (filters out engine-tagged-track systems such as the Fence DAG).</summary>
    public SystemDefinition[] UserSystems => Scheduler.UserSystems;

    /// <summary>Fires when overload reaches <see cref="OverloadLevel.PlayerShedding"/>. Game code decides what to do (migrate, disconnect, split).</summary>
    public event Action<TyphonRuntime> OnCriticalOverload;

    /// <summary>
    /// Outcome of the most recently completed tick. Refreshed every tick under every
    /// <see cref="RuntimeOptions.SystemExceptionPolicy"/>, so it is never stale.
    /// </summary>
    /// <remarks>
    /// This is the primary surface for a host that gates its own publication on tick success: it is pull-checkable, so the host's next loop iteration can ask
    /// "did that tick succeed?" without having subscribed to anything, and a host that wires up late does not silently miss the answer.
    /// <see cref="OnTickAborted"/> is the push counterpart.
    /// </remarks>
    public TickOutcome LastTickOutcome { get; private set; }

    /// <summary>
    /// Fires once when a tick is aborted under <see cref="SystemExceptionPolicy.AbortTickAndStop"/>, on the tick thread, after the tick has fully drained.
    /// Never fires for a successful tick, and never fires twice — the runtime is terminal after an abort. The handler should stop the runtime
    /// (<see cref="FatalStop"/>) and let the host decide whether to exit or rebuild the engine.
    /// <para>
    /// "Drained" means every system has run or been skipped — it does NOT mean the tick is over. The handler runs INSIDE the tick, on the tick thread, and
    /// the tick's own end-of-tick accounting still runs after the handler returns. So <see cref="FatalStop"/> called from here takes effect after the current
    /// tick completes, and <see cref="CurrentTickNumber"/> advances once more once it does. Do not block in this handler: you are holding up the tick thread.
    /// </para>
    /// </summary>
    public event Action<TyphonRuntime, TickOutcome> OnTickAborted;

    // Latches OnTickAborted to a single invocation. Only ever touched on the TickDriver thread inside OnTickEndInternal.
    private bool _tickAbortedNotified;

    // ═══════════════════════════════════════════════════════════════
    // Factory
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a new TyphonRuntime from a DatabaseEngine and a schedule configuration.
    /// </summary>
    /// <param name="engine">The database engine for entity storage.</param>
    /// <param name="configure">Action to register systems on the <see cref="RuntimeSchedule"/>.</param>
    /// <param name="options">Runtime options. If null, defaults are used.</param>
    /// <param name="parent">Parent resource node. If null, uses the registry's Runtime node.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serviceProvider">
    /// Optional DI container. When supplied, a profiler-launch override registered via <c>AddTyphonProfiler</c> is resolved
    /// from it. The zero-host-code profiler path works without it — pass it only when a host wants to override config in code.
    /// </param>
    public static TyphonRuntime Create(DatabaseEngine engine, Action<RuntimeSchedule> configure, RuntimeOptions options = null, IResource parent = null,
        ILogger logger = null, IServiceProvider serviceProvider = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configure);

        var opts = options ?? new RuntimeOptions();
        var schedule = RuntimeSchedule.Create(opts);
        configure(schedule);

        // Declare the engine's parallel Fence as a normal DAG on the schedule's Engine-Post track. Only when the user opts in via
        // RuntimeOptions.EnableParallelFence (default true); otherwise the legacy serial WriteTickFence runs from OnTickEndInternal as before.
        FenceExecBundle? fenceBundle = null;
        if (opts.EnableParallelFence)
        {
            fenceBundle = FenceDagBuilder.DeclareFenceDag(schedule, engine);
        }

        // Engine-owned replication on the Engine-Subscriptions track (#955). Declared unconditionally and with no option to suppress it: a disable switch would
        // reintroduce, as configuration, the same "silently never runs" trap the serial-fence dispatch below closes.
        SubscriptionsDagBuilder.DeclareSubscriptionsDag(schedule, engine);

        var resourceParent = parent ?? engine.Parent; // DatabaseEngine registers under DataEngine node
        var scheduler = schedule.Build(resourceParent, logger);

        var runtime = new TyphonRuntime(engine, scheduler, opts, logger, fenceBundle);

        // Self-wire the profiler from configuration (issue #332) — a no-op unless typhon.telemetry.json enables it.
        // Done here, with the runtime fully built and the engine fully populated, so the session metadata is complete.
        ProfilerBootstrap.TryStart(runtime, serviceProvider);

        return runtime;
    }

    private TyphonRuntime(DatabaseEngine engine, DagScheduler scheduler, RuntimeOptions options, ILogger logger, FenceExecBundle? fenceBundle = null)
    {
        if (fenceBundle.HasValue)
        {
            _fencePrepExec = fenceBundle.Value.Prep;
            _fenceMigrateExec = fenceBundle.Value.Migrate;
            _fenceIndexMassUpdateExec = fenceBundle.Value.IndexMassUpdate;
            _fenceEntityMapUpdateExec = fenceBundle.Value.EntityMapUpdate;
            _fenceAabbRefreshExec = fenceBundle.Value.AabbRefresh;
            _fenceFinalizeExec = fenceBundle.Value.Finalize;
            _liveFenceCost = new LiveFenceCostModel(options.FenceCostModel);
            _parallelFenceEnabled = true;
        }
        Engine = engine;
        Scheduler = scheduler;
        Options = options;

        // Parented under the scheduler, not under engine.Parent. The scheduler is always present, whereas a runtime built against an engine with no resource
        // parent would otherwise throw here — during EVERY runtime construction, for a subsystem nothing has switched on yet.
        // The quarantine spans the whole window a session may be stalled for, plus the tick of the release itself (D1): an identity must not be reissued
        // while any session could still owe a frame that names its previous holder. It is derived from the SAME conversion the frame assembler does, at this
        // runtime's own tick rate, so the two cannot drift: a quarantine sized from a different number than the one that closes sessions is a SUB-06 hole.
        var closeBoundTicks = SkipPolicy.CloseBoundTicks(options.Subscriptions, SubscriptionsRuntime.NominalTickPeriodUsFor(options.BaseTickRate));
        _netIds = new NetIdAllocator("Subscriptions.NetIds", scheduler, quarantineTicks: Math.Max(1, closeBoundTicks) + 1);
        _subscriptions = new SubscriptionsRegistry(options.Subscriptions);
        _logger = logger ?? NullLogger.Instance;
        _systemTransactions = new Transaction[scheduler.AllSystemCount];
        _systemViews = new ViewBase[scheduler.AllSystemCount];
        _systemQueryPlanStartTicks = new long[scheduler.AllSystemCount];
        _systemChangeFilterTables = new ComponentTable[scheduler.AllSystemCount][];
        _systemClusterStates = new ArchetypeClusterState[scheduler.AllSystemCount];
        _systemArchetypeIds = new ushort[scheduler.AllSystemCount];
        Array.Fill(_systemArchetypeIds, ushort.MaxValue);
        _systemEntityLists = new PooledEntityList[scheduler.AllSystemCount];
        _systemConsumedQueues = new EventQueueBase[scheduler.AllSystemCount][];
        _parallelEntityLists = new PooledEntityList[scheduler.AllSystemCount];
        _multiTableFilterSets = new HashMap<long>[scheduler.AllSystemCount];
        _parallelAccessors = new PointInTimeAccessor[scheduler.AllSystemCount];
        _partitionViews = new PartitionEntityView[scheduler.AllSystemCount][];
        _systemTierClusterIds = new int[scheduler.AllSystemCount][];
        _systemTierClusterCount = new int[scheduler.AllSystemCount];
        _chunkTicksPerEntity = new double[scheduler.AllSystemCount];
        _dispatchClusterIds = new int[scheduler.AllSystemCount][];
        _dispatchClusterCount = new int[scheduler.AllSystemCount];
        _systemAmortizationBuffers = new int[scheduler.AllSystemCount][];
        _systemRunCount = new long[scheduler.AllSystemCount];
        _tierRangeViews = new ClusterRangeEntityView[scheduler.AllSystemCount][];
        _checkerboardPhase = new int[scheduler.AllSystemCount];
        _checkerboardRedIds = new int[scheduler.AllSystemCount][];
        _checkerboardRedCount = new int[scheduler.AllSystemCount];
        _checkerboardBlackIds = new int[scheduler.AllSystemCount][];
        _checkerboardBlackCount = new int[scheduler.AllSystemCount];
        _createSideTxDelegate = CreateSideTransactionInternal;

        ResolveChangeFilters(scheduler);

        // Wire tick lifecycle hooks
        Scheduler.TickStartCallback = OnTickStartInternal;
        Scheduler.TickEndCallback = OnTickEndInternal;
        Scheduler.SystemStartCallback = OnSystemStartInternal;
        Scheduler.SystemEndCallback = OnSystemEndInternal;
        Scheduler.ParallelQueryPrepareCallback = OnParallelQueryPrepare;
        Scheduler.ParallelQueryChunkCallback = OnParallelQueryChunk;
        Scheduler.ParallelQueryCleanupCallback = OnParallelQueryCleanup;

        // Wire profiler gauge snapshot — only when gauges are enabled, so the callback pointer stays null otherwise and the scheduler's
        // null-check is the only cost. See TyphonRuntime.GaugeSnapshot.cs for the collection + emit implementation.
        if (TelemetryConfig.ProfilerGaugesActive)
        {
            Scheduler.GaugeSnapshotCallback = EmitGaugeSnapshotFromScheduler;
        }

        Scheduler.OnCriticalOverloadCallback = () => OnCriticalOverload?.Invoke(this);

        // Bind the engine's shared FenceContext onto every typed fence-phase system. Done in the ctor — after schedule build, before Start.
        // Required for Start-time context-binding validation to pass on parallel-fence runtimes.
        if (_parallelFenceEnabled)
        {
            Scheduler.RegisterContext(Engine.FenceContext);
        }

        // Same window as the fence context: after Build, before Start, or Start's binding validation rejects the typed stages. Unconditional, because the
        // Subscriptions track is unconditional — a stage left with a null Context would throw at Start rather than quietly not run.
        _subscriptionsContext.AttachScheduler(Scheduler);
        Scheduler.RegisterContext(_subscriptionsContext);
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// What clients may see, send and hear: projections, interest profiles, session kinds, commands, events and metrics. Configure it before
    /// <see cref="Start"/>; afterwards it is frozen and every declaring call throws.
    /// </summary>
    /// <remarks>
    /// <b>Replication belongs to the runtime, not to <see cref="DatabaseEngine"/>, because it rides the tick.</b> The declarations here are compiled exactly
    /// once, at <see cref="Start"/>, into the plan the subscriptions track walks and the catalog clients negotiate against — which is why a late declaration
    /// is refused loudly rather than silently ignored: it would be a declaration no client had ever been told about.
    /// </remarks>
    public SubscriptionsRegistry Subscriptions => _subscriptions;

    /// <summary>
    /// The canonical catalog this runtime compiled, as UTF-8 JSON. Empty before <c>Start</c>, and on a runtime that declares nothing.
    /// </summary>
    /// <remarks>
    /// Exactly the bytes <c>WELCOME</c> carries, which is the point: a host serving them at <c>/typhon/catalog.json</c> for build-time codegen and for tools
    /// must not be serving a second rendering of the same declarations, because the hash a client echoes is computed over these bytes. Never mutated after
    /// <c>Start</c>, so it is handed out directly rather than copied.
    /// </remarks>
    public ReadOnlyMemory<byte> SubscriptionsCatalogJson => _subscriptionsRuntime?.Catalog?.Utf8;

    /// <summary>
    /// The acceptor a transport hands its connections to. <see langword="null"/> before <see cref="Start"/>, and on a runtime whose application declared no
    /// subscriptions.
    /// </summary>
    /// <remarks>
    /// <b>Public since P1-08, which is the slice that had to answer it.</b> The interfaces it is used through — <see cref="ISubscriptionTransport"/>,
    /// <see cref="ISubscriptionAcceptor"/>, <see cref="ISubscriptionLink"/> — were public already, because a transport is a thing an application writes;
    /// <c>Typhon.Subscriptions.AspNetCore</c> is a separate assembly and has to be able to register one. The shape is the smallest that works: hand the
    /// listener over, it is started once and given the acceptor, and stopping it stays the caller's through its own <c>StopAsync</c>.
    /// </remarks>
    internal ISubscriptionAcceptor SubscriptionAcceptor => _subscriptionsRuntime?.Acceptor;

    /// <summary>
    /// Starts <paramref name="transport"/> against this runtime's replication.
    /// </summary>
    /// <param name="transport">The listener. It is started once and handed the acceptor; stopping it is the caller's, through its own <c>StopAsync</c>.</param>
    /// <exception cref="InvalidOperationException">The runtime has not started, or it declares no subscriptions.</exception>
    /// <remarks>
    /// Both refusals are loud, and deliberately: a transport bound to a runtime that can never admit anyone is a listener that accepts connections and closes
    /// every one of them, which reads to an operator as a network fault rather than as a missing declaration.
    /// </remarks>
    public void StartSubscriptionTransport(ISubscriptionTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        if (_subscriptionsRuntime == null)
        {
            throw new InvalidOperationException(
                "A subscription transport can only be started after TyphonRuntime.Start(): the catalog a client negotiates against is compiled there.");
        }

        var acceptor = _subscriptionsRuntime.Acceptor;
        if (acceptor == null)
        {
            throw new InvalidOperationException(
                "This runtime declares no subscriptions, so it has no catalog, no session table and nothing to admit a client to. Declare at least one " +
                "archetype, profile or session kind on TyphonRuntime.Subscriptions before Start().");
        }

        transport.Start(acceptor);
    }

    /// <summary>Starts the scheduler (worker threads + tick driver).</summary>
    public void Start()
    {
        // Before the scheduler, deliberately: compiling the declarations can refuse the configuration, and refusing it on a runtime whose workers have not yet
        // started leaves nothing to unwind. Building here rather than in the constructor is equally deliberate — resolving a projection reads the engine's
        // archetype layouts and its spatial grid, and an application configures both between Create and Start.
        _subscriptions.Freeze();
        if (_subscriptionsRuntime == null)
        {
            // The context's telemetry, not a fresh one: the stages write to the instance the context owns, and STATS has to read the same object or the
            // track metric would report zeros from a ring nothing fills.
            var built = new SubscriptionsRuntime(Engine, _subscriptions, Options, Scheduler, _netIds, SystemNames(), _subscriptionsContext.Telemetry);
            _subscriptionsRuntime = built;
            if (built.Grid is { } grid)
            {
                LogReplicationGrid(grid.CellM, Engine.Realm0Grid.Config.CellSize, grid.DimX, grid.DimY, grid.DimZ, grid.Window, grid.Radius,
                    grid.Flat ? "flat" : "deep");
            }

            // Published to the stages before a worker exists to read it: Scheduler.Start is what creates them, and starting a thread is itself a barrier.
            _subscriptionsContext.AttachSubscriptions(built);
        }

        Scheduler.Start();
    }

    /// <summary>The scheduled systems' names, in schedule order — the labels of the built-in per-system metric the catalog declares.</summary>
    private string[] SystemNames()
    {
        var names = new string[Scheduler.AllSystemCount];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = Scheduler.Systems[i]?.Name;
        }

        return names;
    }

    /// <summary>
    /// Bind an ambient context onto every registered system deriving from <see cref="ChunkedCallbackSystem{TContext}"/>.
    /// Must be called after Configure (systems must already be registered) and before <see cref="Start"/>.
    /// </summary>
    public void RegisterContext<TContext>(TContext context) where TContext : class => Scheduler.RegisterContext(context);

    /// <summary>Test/diagnostic accessor for the adaptive fence cost model. Null when parallel fence is disabled.</summary>
    internal LiveFenceCostModel LiveFenceCost => _liveFenceCost;

    /// <summary>Test/diagnostic accessor for the four fence-phase exec systems (parallel fence only).</summary>
    internal FencePrepExecSystem FencePrepExec => _fencePrepExec;
    internal FenceMigrateExecSystem FenceMigrateExec => _fenceMigrateExec;
    internal FenceAabbRefreshExecSystem FenceAabbRefreshExec => _fenceAabbRefreshExec;
    internal FenceFinalizeExecSystem FenceFinalizeExec => _fenceFinalizeExec;
    internal FenceIndexMassUpdateExecSystem FenceIndexMassUpdateExec => _fenceIndexMassUpdateExec;
    internal FenceEntityMapUpdateExecSystem FenceEntityMapUpdateExec => _fenceEntityMapUpdateExec;

    /// <summary>
    /// Gracefully shuts down the runtime. Stops the subscription server, fires <see cref="OnShutdown"/>, then stops the scheduler.
    /// </summary>
    public void Shutdown() => StopInternal(true);

    /// <summary>
    /// Stops the runtime **without** running the <see cref="OnShutdown"/> hook — the fatal path, for a host reacting to <see cref="OnTickAborted"/> (issue #567).
    /// </summary>
    /// <remarks>
    /// <see cref="OnShutdown"/> deliberately runs its handlers inside an <see cref="DurabilityMode.Immediate"/> transaction, which is the right thing for an
    /// orderly stop and the wrong thing after a fatal tick: the abort means the simulation is logically incomplete, so committing a final round of shutdown
    /// writes on top of it would persist state derived from a tick that never finished. Everything else — subscription server, profiler, scheduler — is
    /// torn down exactly as in <see cref="Shutdown"/>.
    /// <para>
    /// <b>Neither this nor <see cref="Shutdown"/> is a quiescence point.</b> Both stop new ticks; neither waits for the tick in flight. The expected caller
    /// is an <see cref="OnTickAborted"/> handler, which runs on the tick thread — so this typically returns into the very tick it is stopping, which then
    /// finishes and posts its accounting. Dispose the runtime when you need "nothing is running": <see cref="Dispose"/> joins the tick thread.
    /// </para>
    /// </remarks>
    public void FatalStop() => StopInternal(false);

    private void StopInternal(bool runOnShutdown)
    {
        // Publish the final tick number to any in-flight capture before teardown starts (#614 D-5) — the trace header is patched after the scheduler is gone.
        ProfilerCaptureCounters.RecordRuntimeTick(CurrentTickNumber);

        // Begin the async CPU-sampler stop first so its (seconds-long) .nettrace transcode overlaps the rest of teardown.
        // No-op unless the profiler was self-wired by ProfilerBootstrap.TryStart.
        ProfilerBootstrap.BeginStop();

        // Execute OnShutdown callback with a dedicated transaction
        if (runOnShutdown && OnShutdown != null)
        {
            using var tx = Engine.CreateQuickTransaction(DurabilityMode.Immediate);
            var ctx = new TickContext
            {
                TickNumber = Scheduler.CurrentTickNumber,
                DeltaTime = 0f,
                Transaction = tx,
                CreateSideTransaction = _createSideTxDelegate,
                Entities = PooledEntityList.Empty,
                TierBudgetMetrics = _previousTickMetrics,
                SpatialGrid = new SpatialGridAccessor(Engine?.Realm0Grid),
                Subscriptions = _subscriptionsRuntime?.Commands,
                // Runs on whichever thread called Shutdown()/FatalStop() — no worker slot belongs to it (#860).
                WorkerId = TickContext.NonWorkerId,
                ChunkCount = 1
            };
            OnShutdown.Invoke(ctx);
            tx.Commit();
        }

        Scheduler.Shutdown();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Scheduler.Dispose();

        // AFTER the scheduler too, and for the same reason as the identity allocator below, only harder: the replication state hands the ECS a reference the
        // cluster-drain hook calls, and its directory holds raw pointers into the block pool's native slabs. Tearing it down while a tick could still reach
        // it is a use-after-free rather than a disposed-object exception. Shutdown() is not the place either — neither it nor FatalStop is a quiescence
        // point; Scheduler.Dispose is the line that joins the workers and stops the timer thread.
        _subscriptionsRuntime?.Dispose();

        // AFTER the scheduler, not before. A tick already past the shutdown check keeps dispatching on the timer thread, and every tick ends by draining this
        // allocator's quarantine — so disposing it first opens a window where that drain runs against a disposed object. Scheduler.Dispose joins the workers
        // and stops the timer thread, so nothing can reach it once this line is passed. DrainQuarantine tolerates disposal as well, because belt and braces is
        // what the equivalent disposed-signal bug cost to learn the first time.
        _netIds?.Dispose();

        // Dispose per-system PTAs AFTER scheduler — workers must be fully stopped
        // before we flush their per-thread EntityAccessors' ChangeSets.
        for (var i = 0; i < _parallelAccessors.Length; i++)
        {
            _parallelAccessors[i]?.Dispose();
        }

        // The profiler is intentionally NOT finalized here. ProfilerBootstrap finalizes the trace from the engine
        // storage's Disposing event (ManagedPagedMMF, disposed after DatabaseEngine) — that runs deterministically on
        // every host AND after the engine's shutdown teardown, so those events still reach the trace. Stopping it here
        // would precede the engine teardown and drop it. Shutdown() above only pre-warms the async CPU-sampler stop.
    }

    // ═══════════════════════════════════════════════════════════════
    // Side-transaction factory
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a side-transaction with the specified durability mode.
    /// The caller owns the returned Transaction and must Commit + Dispose it.
    /// Side-transactions are NOT visible to the current tick's main Transactions (snapshot isolation).
    /// </summary>
    /// <remarks>
    /// <b>Commit and dispose it before the system that created it returns</b> (rule <c>EW-01</c>). This is an ordinary transaction — it can write an indexed
    /// field and therefore mutate a B+Tree — and nothing joins it to the tick. Held past the system's epilogue and committed later, it can land while the tick
    /// fence is rewriting those same structures, which is the one path inside the runtime that can violate the fence's exclusivity. Per-system transactions
    /// carry no such risk: the runtime commits and disposes those itself.
    /// </remarks>
    public Transaction CreateSideTransaction(DurabilityMode durability = DurabilityMode.Immediate,
        CommitDiscipline discipline = CommitDiscipline.TickFence) => Engine.CreateQuickTransaction(durability, discipline);

    private Transaction CreateSideTransactionInternal(DurabilityMode durability, CommitDiscipline discipline)
        => Engine.CreateQuickTransaction(durability, discipline);

    // ═══════════════════════════════════════════════════════════════
    // #197: Change filter resolution
    // ═══════════════════════════════════════════════════════════════

    private void ResolveChangeFilters(DagScheduler scheduler)
    {
        for (var i = 0; i < scheduler.AllSystemCount; i++)
        {
            var sys = scheduler.Systems[i];

            // Resolve input View
            if (sys.InputFactory != null)
            {
                _systemViews[i] = sys.InputFactory();
                if (_systemViews[i] == null)
                {
                    throw new InvalidOperationException($"System '{sys.Name}': InputFactory returned null. The View must be created before the runtime starts.");
                }

                _systemViews[i].IsSystemInput = true;
                ThrowIfTierFiltersDisjoint(sys, _systemViews[i]);

                // Resolve the cluster state for parallel cluster dispatch from THIS system's own input view. It feeds ctx.ClusterIds /
                // ctx.StartClusterIndex / ctx.EndClusterIndex, the tier index and the checkerboard split, so binding the wrong archetype hands a system
                // another archetype's cluster ids — a page-index-out-of-range throw when the counts differ, or silent double/zero processing when they
                // happen to match. This previously scanned the global ArchetypeRegistry and took the FIRST cluster-eligible archetype, which is correct
                // only in a world with exactly one; every multi-archetype schema using parallel cluster-native systems was broken.
                // RT-1: bound for EVERY QuerySystem, not only parallel ones. The non-parallel path reads the same binding for its tier scope and its sleep
                // filter; unbound, a non-parallel tier system silently processed every entity of every tier, and with cellAmortize N did so with N× dt.
                if (sys.Type == SystemType.QuerySystem && Engine != null)
                {
                    var viewArchetypeId = _systemViews[i].QueriedArchetypeId;
                    foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
                    {
                        // Same shape as before, with one difference that is the whole fix: match the view's archetype instead of taking the first
                        // cluster-eligible one found.
                        if (meta.ArchetypeId != viewArchetypeId || !meta.IsClusterEligible || meta.ArchetypeId >= Engine._archetypeStates.Length)
                        {
                            continue;
                        }

                        // #631: this used to also require `ActiveClusterCount > 0`, which reads a TRANSIENT condition to make a PERMANENT decision. This
                        // runs once, in the constructor; an application that builds its runtime before loading its data — the ordering every sample and
                        // every Workbench capture harness uses — leaves the system unbound for the rest of the session. It then silently falls back to
                        // materializing a per-entity id list from the view instead of taking cluster-RANGE dispatch, and the #327 touch rollup, whose
                        // first gate is exactly this id, can never emit a row.
                        //
                        // Dropping the count is safe because the code already tolerates a bound state whose count is zero: nothing un-binds a system whose
                        // archetype is later fully drained, so `ActiveClusterCount == 0` on a bound system was always reachable. Both dispatch paths read
                        // the count LIVE per tick (`PrepareFullNonVersioned` → 0 chunks → EmptyInput skip; `ExecuteChunkWithAccessor` → an empty range),
                        // so an archetype that is empty at construction and empty forever behaves exactly as before.
                        var es = Engine._archetypeStates[meta.ArchetypeId];
                        if (es?.ClusterState != null)
                        {
                            _systemClusterStates[i] = es.ClusterState;
                            _systemArchetypeIds[i] = meta.ArchetypeId;
                        }

                        break;
                    }
                }

                // Issue #231 BUG-3 fix: pre-create the per-archetype TierClusterIndex eagerly when ANY system on this archetype declares a tier filter.
                // Removes the racy lazy-init from OnParallelQueryPrepare. Single-threaded constructor context, so plain assignment is safe.
                if (sys.TierFilter != SimTier.All && _systemClusterStates[i] != null)
                {
                    _systemClusterStates[i].TierIndex ??= new TierClusterIndex();
                }
            }

            // Resolve changeFilter component types → ComponentTable references
            if (sys.ChangeFilterTypes is { Length: > 0 })
            {
                var tables = new ComponentTable[sys.ChangeFilterTypes.Length];
                for (var j = 0; j < sys.ChangeFilterTypes.Length; j++)
                {
                    var ct = Engine.GetComponentTable(sys.ChangeFilterTypes[j]);
                    if (ct == null)
                    {
                        throw new InvalidOperationException(
                            $"System '{sys.Name}': ChangeFilter type '{sys.ChangeFilterTypes[j].Name}' is not a registered component type.");
                    }

                    if (ct.StorageMode == StorageMode.Versioned)
                    {
                        throw new InvalidOperationException(
                            $"System '{sys.Name}': ChangeFilter type '{sys.ChangeFilterTypes[j].Name}' uses Versioned storage mode, " +
                            "which does not support dirty tracking. ChangeFilter requires SingleVersion or Transient storage.");
                    }

                    tables[j] = ct;
                }

                _systemChangeFilterTables[i] = tables;

                // Build ReactiveSkip closure: returns true when no dirty entities exist for this system's change filter.
                // Uses PreviousTickHadDirtyEntities (reliable, works regardless of EntityPK overhead).
                var filterTables = tables;
                sys.ReactiveSkip = () =>
                {
                    for (var t = 0; t < filterTables.Length; t++)
                    {
                        if (filterTables[t].PreviousTickHadDirtyEntities)
                        {
                            return false; // Dirty entities exist — don't skip
                        }
                    }

                    return true; // No dirty entities — skip
                };
            }

            // Pre-allocate consumed queue refs (zero allocation per tick)
            if (sys.ConsumesQueueIndices is { Length: > 0 })
            {
                var consumed = new EventQueueBase[sys.ConsumesQueueIndices.Length];
                for (var j = 0; j < sys.ConsumesQueueIndices.Length; j++)
                {
                    consumed[j] = scheduler.GetEventQueue(sys.ConsumesQueueIndices[j]);
                }

                _systemConsumedQueues[i] = consumed;

                // Extend ReactiveSkip: don't skip if any consumed queue has events
                var originalSkip = sys.ReactiveSkip;
                var queueRefs = consumed;
                sys.ReactiveSkip = () =>
                {
                    for (var q = 0; q < queueRefs.Length; q++)
                    {
                        if (!queueRefs[q].IsEmpty)
                        {
                            return false; // Events pending — don't skip
                        }
                    }

                    // No events — fall through to original skip check (dirty entities) or default skip
                    return originalSkip == null || originalSkip();
                };
            }
        }
    }

    /// <summary>
    /// Build the filtered entity set for a system with change filter.
    /// Iterates the raw dirty bitmap from the previous tick, reads entity PKs from chunk offset 0, and intersects with the View's entity set. OR logic
    /// across multiple changeFilter tables.
    /// Falls back to full View when PK resolution is unavailable (first tick, or SV without indexed fields).
    /// </summary>
    private PooledEntityList BuildFilteredEntitySet(int sysIdx)
    {
        var view = _systemViews[sysIdx];
        var filterTables = _systemChangeFilterTables[sysIdx];

        // Single-table fast path: skip intermediate collection, write directly to result array.
        // Most systems filter on a single component type — this eliminates HashSet allocation + copy.
        if (filterTables.Length == 1)
        {
            return BuildFilteredSingleTable(sysIdx, view, filterTables[0]);
        }

        // Multi-table path: deduplicate across tables using cached HashMap<long> (zero alloc after first tick)
        var dirtyInView = _multiTableFilterSets[sysIdx] ??= new HashMap<long>();
        dirtyInView.Clear();

        for (var t = 0; t < filterTables.Length; t++)
        {
            if (!ScanDirtyBitmapIntoSet(sysIdx, view, filterTables[t], dirtyInView, out var fallback))
            {
                return fallback;
            }
        }

        if (dirtyInView.Count == 0)
        {
            return PooledEntityList.Empty;
        }

        var list = PooledEntityList.Rent(dirtyInView.Count);
        var span = list.AsSpan();
        var idx = 0;
        foreach (var pk in dirtyInView)
        {
            span[idx++] = EntityId.FromRaw(pk);
        }

        return list;
    }

    /// <summary>
    /// Single-table fast path: scan dirty bitmap → View intersection → result array.
    /// No intermediate collection, no dedup (only one table → no duplicates possible).
    /// Includes cluster entity scanning (Phase 4a): reads PreviousTickDirtySnapshot from each cluster archetype that references this table.
    /// </summary>
    private unsafe PooledEntityList BuildFilteredSingleTable(int sysIdx, ViewBase view, ComponentTable table)
    {
        if (!table.PreviousTickHadDirtyEntities)
        {
            return PooledEntityList.Empty;
        }

        var bitmap = table.PreviousTickDirtyBitmap;
        if (bitmap == null || table.IndexedFieldInfos == null || table.IndexedFieldInfos.Length == 0)
        {
            return BuildFullViewEntitySet(sysIdx);
        }

        // Upper bound: view.Count (dirty ∩ view can't exceed view size). Avoids separate cluster estimate scan.
        var list = PooledEntityList.Rent(view.Count);
        var span = list.AsSpan();
        int count = 0;

        // Non-cluster path: scan ComponentTable dirty bitmap
        if (bitmap.Length > 0)
        {
            var accessor = table.ComponentSegment.CreateChunkAccessor();
            try
            {
                for (var wordIdx = 0; wordIdx < bitmap.Length; wordIdx++)
                {
                    var word = bitmap[wordIdx];
                    while (word != 0)
                    {
                        var bit = BitOperations.TrailingZeroCount((ulong)word);
                        var chunkId = wordIdx * 64 + bit;
                        word &= word - 1;

                        if (table.IsChunkDestroyed(chunkId))
                        {
                            continue;
                        }

                        var entityPK = *(long*)accessor.GetChunkAddress(chunkId);
                        if (view.Contains(entityPK))
                        {
                            span[count++] = EntityId.FromRaw(entityPK);
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }

        // Cluster path (Phase 4a): scan cluster dirty bitmaps for archetypes referencing this table.
        // Issue #231: tier-filtered systems scope the scan to the tier's clusters (Q9) instead of walking the full snapshot bitmap.
        var sys = Scheduler.Systems[sysIdx];
        var effectiveTier = (SimTier)((byte)sys.TierFilter & (byte)view.TierFilter);
        count = ScanClusterDirtyEntities(table, view, effectiveTier, span, count);

        if (count == 0)
        {
            list.Return();
            return PooledEntityList.Empty;
        }

        return new PooledEntityList(list.BackingArray, count);
    }

    /// <summary>
    /// Scan cluster dirty bitmaps for all archetypes referencing the given table, adding matching entities to the result span.
    /// Uses direct array loop over <see cref="ArchetypeRegistry"/> (no yield-return allocation).
    /// When <paramref name="effectiveTier"/> is non-<see cref="SimTier.All"/> and the archetype has a configured spatial grid, the scan walks only the
    /// tier's clusters (issue #231 Q9). Returns the updated count.
    /// </summary>
    private unsafe int ScanClusterDirtyEntities(ComponentTable table, ViewBase view, SimTier effectiveTier, Span<EntityId> span, int count)
    {
        int maxArchId = Math.Min(ArchetypeRegistry.MaxArchetypeId, Engine._archetypeStates.Length - 1);
        bool tierFiltered = effectiveTier != SimTier.All && Engine.Realm0Grid != null;

        for (int archId = 0; archId <= maxArchId; archId++)
        {
            var es = Engine._archetypeStates[archId];
            var cs = es?.ClusterState;
            if (cs?.PreviousTickDirtySnapshot == null)
            {
                continue;
            }

            if (!ArchetypeReferencesTable(es, table))
            {
                continue;
            }

            var snapshot = cs.PreviousTickDirtySnapshot;
            var clusterAccessor = cs.ClusterSegment.CreateChunkAccessor();
            try
            {
                if (tierFiltered && cs.TierIndex != null)
                {
                    // Tier-scoped path: the archetype has a TierIndex (pre-created in ResolveChangeFilters and rebuilt at TickStart).
                    // Scan only the tier's clusters.
                    var tierClusters = cs.TierIndex.GetClustersArray(effectiveTier, out int tierCount);
                    var sleepStates = cs.SleepStates; // Issue #233: may be null for non-spatial secondary archetypes
                    for (int i = 0; i < tierCount; i++)
                    {
                        int chunkId = tierClusters[i];
                        // Issue #233: skip sleeping clusters in the dirty scan
                        if (sleepStates != null && chunkId < sleepStates.Length && sleepStates[chunkId] == ClusterSleepState.Sleeping)
                        {
                            continue;
                        }
                        if (chunkId >= snapshot.Length)
                        {
                            continue;
                        }
                        long word = snapshot[chunkId];
                        if (word == 0)
                        {
                            continue;
                        }
                        byte* clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                        while (word != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount((ulong)word);
                            word &= word - 1;
                            long entityPK = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + bit * 8);
                            if (view.Contains(entityPK))
                            {
                                span[count++] = EntityId.FromRaw(entityPK);
                            }
                        }
                    }
                }
                else
                {
                    // RT-1: sleeping clusters are skipped on this branch too, as on the tier branch above (issue #233) and on every other dispatch path.
                    var sleepStates = cs.SleepingClusterCount > 0 ? cs.SleepStates : null;
                    for (int wordIdx = 0; wordIdx < snapshot.Length; wordIdx++)
                    {
                        long word = snapshot[wordIdx];
                        if (word == 0 || (sleepStates != null && wordIdx < sleepStates.Length && sleepStates[wordIdx] == ClusterSleepState.Sleeping))
                        {
                            continue;
                        }

                        byte* clusterBase = clusterAccessor.GetChunkAddress(wordIdx);
                        while (word != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount((ulong)word);
                            word &= word - 1;

                            long entityPK = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + bit * 8);
                            if (view.Contains(entityPK))
                            {
                                span[count++] = EntityId.FromRaw(entityPK);
                            }
                        }
                    }
                }
            }
            finally
            {
                clusterAccessor.Dispose();
            }
        }

        return count;
    }

    /// <summary>
    /// Scan a single table's dirty bitmap and add matching entities to the dedup set.
    /// Returns false if a fallback is needed (first tick, no indexed fields).
    /// </summary>
    private unsafe bool ScanDirtyBitmapIntoSet(int sysIdx, ViewBase view, ComponentTable table, HashMap<long> dirtyInView, out PooledEntityList fallback)
    {
        fallback = default;

        if (!table.PreviousTickHadDirtyEntities)
        {
            return true;
        }

        var bitmap = table.PreviousTickDirtyBitmap;
        if (bitmap == null || table.IndexedFieldInfos == null || table.IndexedFieldInfos.Length == 0)
        {
            fallback = BuildFullViewEntitySet(sysIdx);
            return false;
        }

        // Non-cluster path
        var accessor = table.ComponentSegment.CreateChunkAccessor();
        try
        {
            for (var wordIdx = 0; wordIdx < bitmap.Length; wordIdx++)
            {
                var word = bitmap[wordIdx];
                while (word != 0)
                {
                    var bit = BitOperations.TrailingZeroCount((ulong)word);
                    var chunkId = wordIdx * 64 + bit;
                    word &= word - 1;

                    if (table.IsChunkDestroyed(chunkId))
                    {
                        continue;
                    }

                    var entityPK = *(long*)accessor.GetChunkAddress(chunkId);
                    if (view.Contains(entityPK))
                    {
                        dirtyInView.TryAdd(entityPK);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        // Cluster path (Phase 4a): scan cluster dirty bitmaps for archetypes referencing this table
        ScanClusterDirtyEntitiesIntoSet(table, view, dirtyInView);

        return true;
    }

    /// <summary>
    /// Scan cluster dirty bitmaps for all archetypes referencing the given table, adding matching entities to the dedup set.
    /// Multi-table variant that adds to HashMap instead of Span.
    /// </summary>
    private unsafe void ScanClusterDirtyEntitiesIntoSet(ComponentTable table, ViewBase view, HashMap<long> dirtyInView)
    {
        int maxArchId = Math.Min(ArchetypeRegistry.MaxArchetypeId, Engine._archetypeStates.Length - 1);
        for (int archId = 0; archId <= maxArchId; archId++)
        {
            var es = Engine._archetypeStates[archId];
            var cs = es?.ClusterState;
            if (cs?.PreviousTickDirtySnapshot == null)
            {
                continue;
            }

            if (!ArchetypeReferencesTable(es, table))
            {
                continue;
            }

            var snapshot = cs.PreviousTickDirtySnapshot;
            var clusterAccessor = cs.ClusterSegment.CreateChunkAccessor();
            try
            {
                // RT-1: sleeping clusters are skipped, as on every other dispatch path (issue #233).
                var sleepStates = cs.SleepingClusterCount > 0 ? cs.SleepStates : null;
                for (int wordIdx = 0; wordIdx < snapshot.Length; wordIdx++)
                {
                    long word = snapshot[wordIdx];
                    if (word == 0 || (sleepStates != null && wordIdx < sleepStates.Length && sleepStates[wordIdx] == ClusterSleepState.Sleeping))
                    {
                        continue;
                    }

                    byte* clusterBase = clusterAccessor.GetChunkAddress(wordIdx);
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount((ulong)word);
                        word &= word - 1;

                        long entityPK = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + bit * 8);
                        if (view.Contains(entityPK))
                        {
                            dirtyInView.TryAdd(entityPK);
                        }
                    }
                }
            }
            finally
            {
                clusterAccessor.Dispose();
            }
        }
    }

    /// <summary>
    /// Check whether an archetype's component slots include the given ComponentTable.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ArchetypeReferencesTable(ArchetypeEngineState es, ComponentTable table)
    {
        for (int slot = 0; slot < es.SlotToComponentTable.Length; slot++)
        {
            if (es.SlotToComponentTable[slot] == table)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Build entity set from full View (no change filter — all entities). When the system has a tier filter or the view itself has one (issue #231),
    /// the materialization is scoped to the tier's clusters instead of walking the view's full HashMap. The effective tier is the bit-AND of the system filter
    /// and the view filter.
    /// </summary>
    private PooledEntityList BuildFullViewEntitySet(int sysIdx)
    {
        var view = _systemViews[sysIdx];
        if (view.Count == 0)
        {
            return PooledEntityList.Empty;
        }

        // RT-1: materialize exactly what this run dispatches — the selection its entry point made (OnSystemStartInternal, or the parallel Prepare, whose
        // checkerboard half it is) — never a selection of its own: a second one would rewrite the buffer the dispatch already handed out, and could rebuild
        // the tier index from a worker (TI-01). No selection: the whole view.
        if (_systemTierClusterIds[sysIdx] != null)
        {
            var sys = Scheduler.Systems[sysIdx];
            var cs = _systemClusterStates[sysIdx];
            return BuildClusterScopedEntityList(cs, _dispatchClusterIds[sysIdx], _dispatchClusterCount[sysIdx], view,
                WithDescendants(sys, cs, EffectiveTier(sysIdx)));
        }

        var list = PooledEntityList.Rent(view.Count);
        var span = list.AsSpan();
        var idx = 0;
        // Iterate the internal HashMap directly: ViewBase.GetEnumerator() is internal, so foreach over `view` would/ bind to the explicit IEnumerable<long>
        // interface and box the enumerator. EntityIdsInternal exposes the value-type HashMap<long>.Enumerator (warning CS0279 — no boxing).
        foreach (var pk in view.EntityIdsInternal)
        {
            span[idx++] = EntityId.FromRaw(pk);
        }

        return list;
    }

    /// <summary>
    /// True when a materialized selection must also carry the view's entities of other archetypes than the bound one. A view over an archetype with
    /// descendants holds their entities too, but the selection walks only the bound archetype's clusters. Carried — unfiltered, as before RT-1 — only when
    /// the selection is a sleep filter alone: a tier or checkerboard selection never included them (the bound archetype's grid cells decide both).
    /// </summary>
    private static bool WithDescendants(SystemDefinition sys, ArchetypeClusterState cs, SimTier tier) =>
        tier == SimTier.All && !sys.IsCheckerboard && ArchetypeRegistry.GetMetadata((ushort)cs.ArchetypeId)?.SubtreeArchetypeIds is { Length: > 1 };

    /// <summary>
    /// Materialize the entities of the selected clusters (issue #231 Q9 pattern, RT-1): each cluster's occupancy bitmap is decoded via TZCNT to emit entity
    /// ids in cluster order, kept when the view holds them. Cost is proportional to the selected clusters' entities, not to the view. With
    /// <paramref name="withDescendants"/> the view's entities of other archetypes follow (see <see cref="WithDescendants"/>).
    /// </summary>
    private PooledEntityList BuildClusterScopedEntityList(ArchetypeClusterState cs, int[] clusterIds, int clusterCount, ViewBase view, bool withDescendants)
    {
        var extra = 0;
        if (withDescendants)
        {
            foreach (var pk in view.EntityIdsInternal)
            {
                if (EntityId.FromRaw(pk).ArchetypeId != cs.ArchetypeId)
                {
                    extra++;
                }
            }
        }

        if (clusterCount == 0 && extra == 0)
        {
            return PooledEntityList.Empty;
        }

        // Support pure-Transient archetypes (ClusterSegment == null) by falling back to TransientSegment.
        // Layout.EntityIdsOffset is the same in both stores — chunk ids are synchronized via lockstep allocation.
        PooledEntityList list;
        int count;
        if (cs.ClusterSegment != null)
        {
            list = BuildTierScopedEntityListPersistent(cs, view, clusterIds, clusterCount, extra, out count);
        }
        else if (cs.TransientSegment != null)
        {
            list = BuildTierScopedEntityListTransient(cs, view, clusterIds, clusterCount, extra, out count);
        }
        else
        {
            list = extra > 0 ? PooledEntityList.Rent(extra) : PooledEntityList.Empty;
            count = 0;
        }

        if (extra > 0)
        {
            var span = list.AsSpan();
            foreach (var pk in view.EntityIdsInternal)
            {
                if (EntityId.FromRaw(pk).ArchetypeId != cs.ArchetypeId)
                {
                    span[count++] = EntityId.FromRaw(pk);
                }
            }
        }

        if (count == 0)
        {
            list.Return();
            return PooledEntityList.Empty;
        }

        return new PooledEntityList(list.BackingArray, count);
    }

    private unsafe PooledEntityList BuildTierScopedEntityListPersistent(ArchetypeClusterState cs, ViewBase view, int[] tierClusters, int tierCount, int extra,
        out int count)
    {
        // ChunkAccessor construction asserts an epoch scope is active. The Versioned tier path (PrepareVersionedFallback → BuildFullViewEntitySet → here) runs
        // from the scheduler thread without an outer scope, so we enter one explicitly. The non-Versioned change-filter path piggybacks on the outer EpochGuard
        // set up by the existing scheduler infrastructure, but we keep our own to be safe. EpochGuard supports nesting (only the outermost scope advances the
        // global epoch). Always enter to keep semantics simple — the cost is one atomic increment/decrement when already inside a scope.
        using var guard = EpochGuard.Enter(Engine.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            // Pre-count via popcount of OccupancyBits. Avoids (tierCount × ClusterSize) over-rent for sparse clusters.
            // The first pass touches header words sequentially — L1/L2-hot for the second pass.
            int exactCount = 0;
            for (int i = 0; i < tierCount; i++)
            {
                byte* clusterBase = accessor.GetChunkAddress(tierClusters[i]);
                exactCount += BitOperations.PopCount(*(ulong*)clusterBase);
            }
            count = 0;
            if (exactCount + extra == 0)
            {
                return PooledEntityList.Empty;
            }

            // Returned rented, not trimmed: the caller appends up to `extra` more and trims (BuildClusterScopedEntityList).
            var list = PooledEntityList.Rent(exactCount + extra);
            var span = list.AsSpan();
            for (int i = 0; i < tierCount; i++)
            {
                byte* clusterBase = accessor.GetChunkAddress(tierClusters[i]);
                ulong bits = *(ulong*)clusterBase;
                while (bits != 0)
                {
                    int slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    long pk = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8);
                    if (view.Contains(pk))
                    {
                        span[count++] = EntityId.FromRaw(pk);
                    }
                }
            }

            return list;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private unsafe PooledEntityList BuildTierScopedEntityListTransient(ArchetypeClusterState cs, ViewBase view, int[] tierClusters, int tierCount, int extra,
        out int count)
    {
        // EpochGuard supports nesting (only the outermost scope advances the global epoch). Always enter to keep semantics simple — the cost is one atomic
        // increment/decrement when already inside a scope.
        using var guard = EpochGuard.Enter(Engine.EpochManager);
        var accessor = cs.TransientSegment.CreateChunkAccessor();
        try
        {
            int exactCount = 0;
            for (int i = 0; i < tierCount; i++)
            {
                byte* clusterBase = accessor.GetChunkAddress(tierClusters[i]);
                exactCount += BitOperations.PopCount(*(ulong*)clusterBase);
            }
            count = 0;
            if (exactCount + extra == 0)
            {
                return PooledEntityList.Empty;
            }

            // Returned rented, not trimmed: the caller appends up to `extra` more and trims (BuildClusterScopedEntityList).
            var list = PooledEntityList.Rent(exactCount + extra);
            var span = list.AsSpan();
            for (int i = 0; i < tierCount; i++)
            {
                byte* clusterBase = accessor.GetChunkAddress(tierClusters[i]);
                ulong bits = *(ulong*)clusterBase;
                while (bits != 0)
                {
                    int slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    long pk = *(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8);
                    if (view.Contains(pk))
                    {
                        span[count++] = EntityId.FromRaw(pk);
                    }
                }
            }

            return list;
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel QuerySystem callbacks (called by DagScheduler)
    //
    // Four dispatch paths based on (WritesVersioned × HasChangeFilter):
    //   Path 1: Full, Non-Versioned  — O(1) prepare, PTA + PartitionEntityView (zero-copy)
    //   Path 2: Filtered, Non-Versioned — O(dirty) prepare, PTA + PooledEntitySlice
    //   Path 3: Full, Versioned (fallback) — O(N) prepare, per-chunk Transaction
    //   Path 4: Filtered, Versioned (fallback) — O(dirty) prepare, per-chunk Transaction
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// The tier a system runs over: its own filter AND its input view's (<see cref="ViewBase.TierFilter"/>, a materialization scope). A tier is a
    /// property of grid cells, so without a grid it is not applied — on every path alike. A disjoint pair yields <see cref="SimTier.None"/> (nothing
    /// dispatched): it is refused at construction (<see cref="ThrowIfTierFiltersDisjoint"/>), and the dispatch path, which must not throw, only meets it
    /// when a view's <c>WithTier</c> changed afterwards.
    /// </summary>
    private SimTier EffectiveTier(int sysIdx)
    {
        var sys = Scheduler.Systems[sysIdx];
        var view = _systemViews[sysIdx];
        var tier = view == null ? sys.TierFilter : (SimTier)((byte)sys.TierFilter & (byte)view.TierFilter);
        return Engine?.Realm0Grid == null ? SimTier.All : tier;
    }

    /// <summary>
    /// Refuses a system tier filter and a view tier filter that are mutually exclusive (e.g. system Tier0, view <c>WithTier(Tier1)</c>): their AND is
    /// <see cref="SimTier.None"/>, which would silently dispatch zero entities.
    /// </summary>
    private static void ThrowIfTierFiltersDisjoint(SystemDefinition sys, ViewBase view)
    {
        if (view == null || sys.TierFilter == SimTier.None || view.TierFilter == SimTier.None
            || ((byte)sys.TierFilter & (byte)view.TierFilter) != 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"System '{sys.Name}': system tier filter '{sys.TierFilter}' and view tier filter '{view.TierFilter}' have no overlap. " +
            "Their intersection is SimTier.None, which would dispatch zero entities. Make the filters compatible " +
            "(e.g. system Tier0 + view Near, where view's tier set is a superset of the system's).");
    }

    /// <summary>
    /// RT-1 — the one cluster selection every QuerySystem path dispatches from: the clusters of <paramref name="tier"/> (the archetype's whole active
    /// list for <see cref="SimTier.All"/>), strided to this run's <c>cellAmortize</c> bucket, minus sleeping clusters.
    /// </summary>
    /// <returns>
    /// The selected chunk ids (the first <paramref name="count"/> entries), or null when nothing narrows the system: it then covers its whole view, which
    /// is the zero-copy path and the only one taken when no tier, no amortization and no sleeping cluster apply.
    /// </returns>
    /// <remarks>
    /// Reads only what tick start prepared single-threaded (TI-01) and writes only this system's own buffer, so the Prepares of different systems may run
    /// concurrently. The caller advances <see cref="_systemRunCount"/> for the run first.
    /// </remarks>
    private int[] SelectDispatchClusters(int sysIdx, SimTier tier, out int count)
    {
        count = 0;
        var cs = _systemClusterStates[sysIdx];
        if (cs == null)
        {
            return null;
        }

        var sys = Scheduler.Systems[sysIdx];
        int[] ids = null;
        if (tier != SimTier.All && cs.TierIndex != null)
        {
            // Prepared at tick start (TI-01). A multi-tier set tick start did not prepare — a view whose WithTier changed mid-tick — is merged into this
            // system's own buffer rather than into the index's shared cache, which concurrent Prepares would otherwise fill together.
            if (!cs.TierIndex.TryGetPreparedClusters(tier, out var tierIds, out var tierCount))
            {
                tierIds = EnsureSelectionBuffer(sysIdx, cs.TierIndex.CountClusters(tier));
                tierCount = cs.TierIndex.CopyClusters(tier, tierIds);
            }

            if (sys.CellAmortize > 0)
            {
                // Stride the tier list by index, which spreads it evenly whatever the cell-key encoding (Morton or row-major). The bucket is keyed on the
                // system's run count, not the tick: under TickDivisor 2 the tick is always even, and a tick-keyed cellAmortize 2 never left bucket 0.
                var amortize = sys.CellAmortize;
                var startOffset = (int)((ulong)Math.Max(0, _systemRunCount[sysIdx] - 1) % (uint)amortize);
                var bucketCount = tierCount > startOffset ? (tierCount - startOffset + amortize - 1) / amortize : 0;
                // In place when the tier list already is this buffer (merged just above): the write cursor never overtakes the read cursor.
                var buf = ReferenceEquals(tierIds, _systemAmortizationBuffers[sysIdx]) ? tierIds : EnsureSelectionBuffer(sysIdx, bucketCount);
                for (var i = startOffset; i < tierCount; i += amortize)
                {
                    buf[count++] = tierIds[i];
                }
                ids = buf;
            }
            else
            {
                // Zero-copy into the TierClusterIndex buffer: rebuilt at tick start, and not again before every system of this tick has run.
                ids = tierIds;
                count = tierCount;
            }
        }

        // Issue #233: sleeping clusters leave the selection. A system nothing else narrowed is "promoted" to a filtered copy of its archetype's active
        // list, so the dispatch walks clusters from here on. SleepingClusterCount == 0 skips it all (DM-02's fast path).
        if (cs.SleepingClusterCount > 0 && cs.SleepStates != null)
        {
            if (ids == null && tier == SimTier.All)
            {
                ids = ReadActiveClusterList(cs, out count);
            }

            if (ids != null)
            {
                // Filtered into this system's buffer; in place when the source already is that buffer (an amortized bucket) — compaction never overtakes
                // the read cursor.
                var buf = ReferenceEquals(ids, _systemAmortizationBuffers[sysIdx]) ? ids : EnsureSelectionBuffer(sysIdx, count);
                var sleepStates = cs.SleepStates;
                var written = 0;
                for (var i = 0; i < count; i++)
                {
                    var chunkId = ids[i];
                    if (chunkId >= sleepStates.Length || sleepStates[chunkId] != ClusterSleepState.Sleeping)
                    {
                        buf[written++] = chunkId;
                    }
                }

                ids = buf;
                count = written;
            }
        }

        return ids;
    }

    /// <summary>This system's own selection buffer, grown (never shrunk) to hold at least <paramref name="needed"/> ids.</summary>
    private int[] EnsureSelectionBuffer(int sysIdx, int needed)
    {
        var buf = _systemAmortizationBuffers[sysIdx];
        if (buf == null || buf.Length < needed)
        {
            // Doubling, not an exact fit: a slowly growing active list would otherwise reallocate on every run.
            buf = new int[Math.Max(Math.Max(16, needed), (buf?.Length ?? 0) * 2)];
            _systemAmortizationBuffers[sysIdx] = buf;
        }

        return buf;
    }

    /// <summary>
    /// Prepare phase: selects the dispatch path based on WritesVersioned and change filter presence.
    /// For non-Versioned systems, creates/advances a long-lived PointInTimeAccessor.
    /// For the full non-Versioned path (Path 1), NO entity list is materialized — O(1).
    /// </summary>
    private int OnParallelQueryPrepare(int sysIdx)
    {
        var sys = Scheduler.Systems[sysIdx];

        // Chunked-CallbackSystem fast-path: skip all entity-prep (no view, no PTA, no tier index, no change-filter materialization).
        // The scheduler dispatches exactly ExplicitChunkCount chunks (or RuntimeChunkCount if the runtime set a per-dispatch override — used by FenceExec to
        // size chunks from the per-tick FenceWorkPlan) and OnParallelQueryChunk routes to the simple dispatch below.
        if (sys.ExplicitChunkCount > 0)
        {
            ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);
            metrics.EntitiesProcessed = 0;
            return sys.RuntimeChunkCount > 0 ? sys.RuntimeChunkCount : sys.ExplicitChunkCount;
        }

        var hasView = _systemViews[sysIdx] != null;
        var hasChangeFilter = hasView && _systemChangeFilterTables[sysIdx] != null;

        // P2 of umbrella #342 — emit the catalog descriptor for this view's query identity. The tracker dedups across the session so only the first call per
        // (Kind, LocalId) actually writes to the trace; subsequent ticks pay one ConcurrentDictionary.ContainsKey-equivalent lookup inside the emit helper.
        // Pull-mode/system-input views never go through View.Refresh, so this is the only hot path where the descriptor can be emitted with the profiler gate
        // definitely open.
        if (hasView && TelemetryConfig.QueryActive)
        {
            _systemViews[sysIdx].EmitDescriptorIfNeeded();
            // Capture once per tick — Prepare can be called multiple times (checkerboard phases). The first call sets
            // it; subsequent calls keep the original tick-start ts so the QueryPlan spans the full system body.
            if (_systemQueryPlanStartTicks[sysIdx] == 0)
            {
                _systemQueryPlanStartTicks[sysIdx] = Stopwatch.GetTimestamp();
            }
        }

        // RT-1: the one selection seam — tier ∩ this run's cellAmortize bucket ∩ awake clusters, null when none applies. Once per run: a checkerboard
        // system's second Prepare of the tick serves the Black half of the selection its first one split (below), and must not select or count again.
        if (!sys.IsCheckerboard || _checkerboardPhase[sysIdx] == 0)
        {
            _systemRunCount[sysIdx]++;
            _systemTierClusterIds[sysIdx] = SelectDispatchClusters(sysIdx, EffectiveTier(sysIdx), out var selectedCount);
            _systemTierClusterCount[sysIdx] = selectedCount;
        }

        // Issue #234: checkerboard two-phase dispatch. On first call (phase 0→1), split filtered cluster list into Red/Black and serve Red. On second call
        // (phase 1→2, after re-dispatch), serve Black.
        if (sys.IsCheckerboard)
        {
            var phase = _checkerboardPhase[sysIdx];
            if (phase == 0)
            {
                // BUG-2 fix: for non-tier-filtered checkerboard systems (SimTier.All, no sleeping clusters), _systemTierClusterIds
                // is null at this point. Promote from ActiveClusterIds so the split has cluster data to work with.
                if (_systemTierClusterIds[sysIdx] == null)
                {
                    var cs2 = _systemClusterStates[sysIdx];
                    if (cs2 != null)
                    {
                        _systemTierClusterIds[sysIdx] = ReadActiveClusterList(cs2, out var promotedCount);
                        _systemTierClusterCount[sysIdx] = promotedCount;
                    }
                }

                // First call this tick: split into Red/Black, serve Red
                _checkerboardPhase[sysIdx] = 1;
                SplitCheckerboardClusters(sysIdx);
                _systemTierClusterIds[sysIdx] = _checkerboardRedIds[sysIdx];
                _systemTierClusterCount[sysIdx] = _checkerboardRedCount[sysIdx];
            }
            else
            {
                // Second call (re-dispatched after Red): serve Black
                _checkerboardPhase[sysIdx] = 2;
                _systemTierClusterIds[sysIdx] = _checkerboardBlackIds[sysIdx];
                _systemTierClusterCount[sysIdx] = _checkerboardBlackCount[sysIdx];
            }
        }

        // CD-02: the dispatch splits the cluster list as it stands now, and its chunks walk this array and split this length, not the live pair. A spawn can
        // append to the archetype's list while they run (AddToActiveList, under its latch), and chunks that read two lengths would not tile it; an append
        // leaves the array's first entries as they are, even when it moves the list to a larger array. A removal does not, and no Destroy commit on the
        // archetype may overlap the walk (CLUSTERWALK-01).
        var dispatchIds = _systemTierClusterIds[sysIdx];
        var dispatchClusters = _systemTierClusterCount[sysIdx];
        if (dispatchIds == null)
        {
            dispatchClusters = 0;
            var dispatchState = _systemClusterStates[sysIdx];
            if (dispatchState != null)
            {
                dispatchIds = ReadActiveClusterList(dispatchState, out dispatchClusters);
            }
        }

        _dispatchClusterIds[sysIdx] = dispatchIds;
        _dispatchClusterCount[sysIdx] = dispatchClusters;

        if (sys.WritesVersioned)
        {
            // Paths 3 & 4: Versioned fallback — materialize entity list, per-chunk Transactions
            return PrepareVersionedFallback(sysIdx, hasChangeFilter);
        }

        // Paths 1 & 2: Non-Versioned — use PointInTimeAccessor
        if (hasChangeFilter)
        {
            return PrepareFilteredNonVersioned(sysIdx);
        }

        return PrepareFullNonVersioned(sysIdx);
    }

    /// <summary>Lazy-init per-system PTA and PartitionEntityViews on first use. Zero-alloc on subsequent ticks.</summary>
    private void EnsureParallelResources(int sysIdx)
    {
        if (_parallelAccessors[sysIdx] == null)
        {
            _parallelAccessors[sysIdx] = new PointInTimeAccessor();
            _partitionViews[sysIdx] = new PartitionEntityView[Scheduler.WorkerCount];
            for (var w = 0; w < Scheduler.WorkerCount; w++)
            {
                _partitionViews[sysIdx][w] = new PartitionEntityView();
            }
        }

        _parallelAccessors[sysIdx].Attach(Engine, Scheduler.WorkerCount);
    }

    /// <summary>Path 1: Full View, Non-Versioned. O(1) prepare — no entity list materialization.</summary>
    private int PrepareFullNonVersioned(int sysIdx)
    {
        var view = _systemViews[sysIdx];
        if (view == null || view.Count == 0)
        {
            return 0;
        }

        EnsureParallelResources(sysIdx);

        ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);

        // Issue #231: tier-filtered Path 1 partitions across the tier's cluster count (× cluster size as a proxy for entity count), not the full view.
        // The chunk execution reads ctx.ClusterIds from the _systemTierClusterIds slot prepared in OnParallelQueryPrepare.
        if (_systemTierClusterIds[sysIdx] != null)
        {
            int tierClusterCount = _systemTierClusterCount[sysIdx];
            if (tierClusterCount == 0)
            {
                metrics.EntitiesProcessed = 0;
                return 0;
            }

            var cs = _systemClusterStates[sysIdx];

            // Pre-allocate per-worker tier range view array (single-threaded) to avoid racy lazy-init from worker threads.
            if (_tierRangeViews[sysIdx] == null)
            {
                _tierRangeViews[sysIdx] = new ClusterRangeEntityView[Scheduler.WorkerCount];
            }

            // Pure-Transient fallback: materialize entity list here (single-threaded) to avoid the BUG where each worker would
            // independently build and store a full list in _parallelEntityLists[sysIdx], leaking (WorkerCount-1) pooled lists per tick.
            if (cs.ClusterSegment == null)
            {
                var entityList = BuildClusterScopedEntityList(cs, _systemTierClusterIds[sysIdx], tierClusterCount, view, withDescendants: false);
                _parallelEntityLists[sysIdx] = entityList;
                metrics.EntitiesProcessed = entityList.Count;
                return entityList.Count == 0 ? 0 : ComputeChunkCount(entityList.Count, sysIdx);
            }

            int clusterSize = cs.Layout.ClusterSize;
            int approxEntityCount = tierClusterCount * clusterSize;
            metrics.EntitiesProcessed = approxEntityCount;
            return ComputeChunkCount(approxEntityCount, sysIdx);
        }

        metrics.EntitiesProcessed = view.Count;
        return ComputeChunkCount(view.Count, sysIdx);
    }

    /// <summary>Path 2: Change-filtered, Non-Versioned. O(dirty) prepare — materialize dirty list, use PTA for access.</summary>
    private int PrepareFilteredNonVersioned(int sysIdx)
    {
        var entityList = BuildFilteredEntitySet(sysIdx);
        _parallelEntityLists[sysIdx] = entityList;

        EnsureParallelResources(sysIdx);

        ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);
        metrics.EntitiesProcessed = entityList.Count;
        if (_systemViews[sysIdx] != null)
        {
            metrics.EntitiesSkippedByChangeFilter = _systemViews[sysIdx].Count - entityList.Count;
        }

        if (entityList.Count == 0)
        {
            return 0;
        }

        return ComputeChunkCount(entityList.Count, sysIdx);
    }

    /// <summary>Paths 3 &amp; 4: Versioned fallback — materialize entity list (same as original path).</summary>
    private int PrepareVersionedFallback(int sysIdx, bool hasChangeFilter)
    {
        PooledEntityList entityList;
        if (hasChangeFilter)
        {
            entityList = BuildFilteredEntitySet(sysIdx);
        }
        else if (_systemViews[sysIdx] != null)
        {
            // RT-1: the clusters this dispatch covers — Prepare's selection, or its checkerboard half — not a fresh walk of the tier: that walk skipped
            // neither sleeping clusters nor the cellAmortize bucket, and handed a checkerboard system the whole tier in both of its phases.
            entityList = BuildFullViewEntitySet(sysIdx);
        }
        else
        {
            entityList = PooledEntityList.Empty;
        }

        _parallelEntityLists[sysIdx] = entityList;

        ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);
        metrics.EntitiesProcessed = entityList.Count;
        if (hasChangeFilter && _systemViews[sysIdx] != null)
        {
            metrics.EntitiesSkippedByChangeFilter = _systemViews[sysIdx].Count - entityList.Count;
        }

        if (entityList.Count == 0)
        {
            return 0;
        }

        return ComputeChunkCount(entityList.Count, sysIdx);
    }

    // The cost rule's grain (RuntimeOptions.CostBasedChunking): the reasoning of the fence's FenceWorkPlan.TargetChunkCost, at a query chunk's smaller
    // dispatch cost — a claim, a context and a view reset, a few µs, where a fence chunk pays 10-30 µs. Below the floor a chunk costs more to hand out than
    // the parallelism it buys; above the ceiling one chunk can keep the whole pool waiting at the end of its system.
    internal const double ChunkCostFloorUs = 25;
    internal const double ChunkCostCeilingUs = 100;

    private int ComputeChunkCount(int entityCount, int sysIdx)
    {
        var workerCount = Scheduler.WorkerCount;
        var sys = Scheduler.Systems[sysIdx];

        // Per-system oversubscription: lift the workerCount cap by ChunksPerWorker (default 1.0 = no change).
        // Round-to-nearest so 1.5 × 16 = 24 exactly; small bumps like 1.1 × 16 = 17.6 → 18.
        var workerCap = Math.Max(1, (int)MathF.Round(workerCount * sys.ChunksPerWorker));

        // The cost rule, from the system's second dispatch on: its previous dispatch's worker time per entity (CaptureChunkCosts). The option is read
        // here too, not only at capture, so a host switching it off mid-run is obeyed from the next dispatch.
        var ticksPerEntity = _chunkTicksPerEntity[sysIdx];
        if (ticksPerEntity > 0 && Options.CostBasedChunking)
        {
            var costUs = entityCount * ticksPerEntity * 1_000_000.0 / Stopwatch.Frequency;
            return CostChunkCount(costUs, workerCap, ChunkUnits(entityCount, workerCap, sysIdx));
        }

        // The entity rule: a first dispatch, or a system the cost rule does not size. Per-system floor, falling back to the global one. The global value is
        // a bet that per-entity work is roughly uniform across the schedule — it is the same entity count for every system — and a system whose per-entity
        // cost is orders above its neighbours' is starved of workers by it: 320 entities against a 64 floor is five chunks no matter how high
        // ChunksPerWorker goes, because the entity cap and not the worker cap is binding.
        var minChunkSize = sys.MinChunkSize > 0 ? sys.MinChunkSize : Options.ParallelQueryMinChunkSize;
        var maxChunks = Math.Max(1, (entityCount + minChunkSize - 1) / minChunkSize);
        return Math.Min(workerCap, maxChunks);
    }

    /// <summary>
    /// The cost rule's chunk count for <paramref name="costUs"/> of work: <paramref name="width"/> chunks while each would carry between
    /// <see cref="ChunkCostFloorUs"/> and <see cref="ChunkCostCeilingUs"/>; below that band fewer chunks, of the floor; above it more, of the ceiling, up to
    /// twice the width. At least one, at most <paramref name="units"/>.
    /// </summary>
    /// <remarks>
    /// <para>Beyond the width the extra chunks buy the tail, not parallelism: each goes to whichever worker comes free, so the pool no longer waits on one
    /// long last chunk (SWG x1, entity rule: Awareness ran as 5 chunks on a 32-worker pool; x64: the pool waited 3.9 ms per tick at the median for the
    /// slowest of its 64).</para>
    /// <para>Twice the width is the fence's cap too (FenceWorkPlan.ComputeMaxChunks), and for the same reason: every chunk pays for its own chunk
    /// accessors, which is not free. Uncapped, SWG x64's Awareness went to 1,092 chunks, one per player cluster, for ~13 ms more worker time per tick,
    /// while past twice the width the slowest chunk is already a small share of the span.</para>
    /// </remarks>
    internal static int CostChunkCount(double costUs, int width, int units)
    {
        var share = costUs / width;
        var chunks = share < ChunkCostFloorUs ? Math.Ceiling(costUs / ChunkCostFloorUs)
            : share > ChunkCostCeilingUs ? Math.Min(2.0 * width, Math.Ceiling(costUs / ChunkCostCeilingUs)) : width;
        return (int)Math.Clamp(chunks, 1, Math.Max(1, units));
    }

    /// <summary>
    /// The most chunks worth dispatching: one entity each, and beyond the width one cluster each — the smallest piece a system walking
    /// <c>ctx.ClusterIds</c> can be handed; a chunk past it would find its cluster range empty.
    /// </summary>
    private int ChunkUnits(int entityCount, int width, int sysIdx)
    {
        // The length Prepare counted (CD-02), not the live one: a spawn since must not size the chunks against a longer list than they split.
        var clusters = _systemTierClusterIds[sysIdx] != null || _systemClusterStates[sysIdx] is { ClusterSegment: not null }
            ? _dispatchClusterCount[sysIdx]
            : int.MaxValue;
        return Math.Min(entityCount, Math.Max(width, clusters));
    }

    /// <summary>
    /// The cost rule's input: each parallel QuerySystem's worker time per entity this tick, which sizes its next dispatch. Tick end, on the tick driver:
    /// every system of the tick has completed. A checkerboard system (two dispatches, one entity count) and one with its own MinChunkSize keep the entity
    /// rule. A failed or aborted dispatch is not a measurement — its drained chunks add no time against the full entity count — so the system keeps its
    /// last one.
    /// </summary>
    private void CaptureChunkCosts()
    {
        if (!Options.CostBasedChunking)
        {
            return;
        }

        for (var i = 0; i < Scheduler.AllSystemCount; i++)
        {
            var sys = Scheduler.Systems[i];
            if (!sys.IsParallelQuery || sys.ExplicitChunkCount > 0 || sys.IsCheckerboard || sys.MinChunkSize > 0)
            {
                continue;
            }

            ref var m = ref Scheduler.GetCurrentSystemMetrics(i);
            if (!m.WasSkipped && m.EntitiesProcessed > 0 && m.WorkTicks > 0)
            {
                _chunkTicksPerEntity[i] = (double)m.WorkTicks / m.EntitiesProcessed;
            }
        }
    }

    /// <summary>
    /// The cluster range chunk <paramref name="chunkIndex"/> of <paramref name="totalChunks"/> walks: its share of an equal split of the list its dispatch
    /// counted in Prepare, the first <c>clusters % totalChunks</c> chunks taking one cluster more. The ranges tile that list (CD-02).
    /// </summary>
    private void ChunkClusterRange(int sysIdx, int chunkIndex, int totalChunks, out int start, out int end)
    {
        var clusters = _dispatchClusterCount[sysIdx];
        var size = clusters / totalChunks;
        var remainder = clusters % totalChunks;
        start = chunkIndex * size + Math.Min(chunkIndex, remainder);
        end = start + size + (chunkIndex < remainder ? 1 : 0);
    }

    /// <summary>
    /// Chunk execution: dispatches to the appropriate path based on WritesVersioned.
    /// Non-Versioned: uses shared PointInTimeAccessor (no per-chunk Transaction).
    /// Versioned: creates a per-chunk Transaction (original fallback path).
    /// </summary>
    private void OnParallelQueryChunk(int sysIdx, int chunkIndex, int totalChunks, int workerId)
    {
        var sys = Scheduler.Systems[sysIdx];

        // Chunked-CallbackSystem fast-path: no Accessor, no per-chunk Transaction, no entity slice.
        // The system body uses ctx.ChunkIndex / ctx.ChunkCount to compute its own data slice.
        if (sys.ExplicitChunkCount > 0)
        {
            ExecuteChunkedCallback(sysIdx, chunkIndex, totalChunks, workerId);
            return;
        }

        if (sys.WritesVersioned)
        {
            ExecuteChunkWithTransaction(sysIdx, chunkIndex, totalChunks, workerId);
            return;
        }

        ExecuteChunkWithAccessor(sysIdx, chunkIndex, totalChunks, workerId);
    }

    /// <summary>
    /// Chunked-parallel <see cref="CallbackSystem"/> dispatch. Builds a minimal <see cref="TickContext"/> (no entity Accessor, no per-chunk Transaction, 
    /// empty Entities) carrying tick metadata + chunk coordinates, then invokes the system callback. Used by systems registered with
    /// <see cref="SystemBuilder.ChunkedParallel"/>.
    /// </summary>
    private void ExecuteChunkedCallback(int sysIdx, int chunkIndex, int totalChunks, int workerId)
    {
        var sys = Scheduler.Systems[sysIdx];
        var ctx = new TickContext
        {
            TickNumber = Scheduler.CurrentTickNumber,
            DeltaTime = _currentDeltaTime,
            AmortizedDeltaTime = _currentDeltaTime,
            WorkerId = workerId,
            ChunkIndex = chunkIndex,
            ChunkCount = totalChunks,
            TierBudgetMetrics = _previousTickMetrics,
            SpatialGrid = new SpatialGridAccessor(Engine?.Realm0Grid),
            Subscriptions = _subscriptionsRuntime?.CommandsFor(workerId)
        };
        ctx.DebugValidateWorkerId(Scheduler.WorkerSlotCount, sys.Name);

        // RT-01: every system body runs inside an epoch scope. This shape is the one that had none — a serial system gets one from its transaction and a
        // parallel query system from its per-worker accessor, but a chunked callback builds a bare TickContext and calls straight into user code. Without
        // this, ChunkedCallbackSystem is a PUBLIC shape from which the public spatial query cannot legally be called (#909). EpochGuard nests and only the
        // outermost scope advances the global epoch, so the fence's own guards stay correct and this costs one atomic pair per chunk.
        // The null check is NOT dead code, whatever the construction path alone suggests. This file guards Engine at ten sites — two of them explicit
        // `Engine != null` tests on dispatch paths (:588, :1064) — and the TickContext built just above already reads `Engine?.SpatialGrid`. A dispatch that
        // reaches here without a live engine must not take an NullReferenceException on the tick path, which is what dereferencing unguarded would give it.
        // When it does happen the body runs unscoped: that is exactly the pre-#909 behaviour for this shape, and strictly better than throwing, so RT-01
        // carries the caveat rather than this method asserting it away.
        var epochManager = Engine?.EpochManager;
        if (epochManager == null)
        {
            sys.CallbackAction(ctx);
            return;
        }

        using (EpochGuard.Enter(epochManager))
        {
            sys.CallbackAction(ctx);
        }
    }

    /// <summary>Paths 1 &amp; 2: Non-Versioned chunk execution with per-worker EntityAccessor from per-system PTA.</summary>
    private void ExecuteChunkWithAccessor(int sysIdx, int chunkIndex, int totalChunks, int workerId)
    {
        var pta = _parallelAccessors[sysIdx];
        var hasChangeFilter = _systemChangeFilterTables[sysIdx] != null;
        var sys = Scheduler.Systems[sysIdx];

        IReadOnlyCollection<EntityId> entities;
        int clusterStart = 0, clusterEnd = 0;
        int[] clusterIdArray = null;

        // Change filter MUST take precedence over tier filter for the entities source.
        //   - Change-filtered: ctx.Entities = sliced materialized list (already tier-scoped upstream).
        //     Tier list still wins for ctx.ClusterIds so cluster-iterating systems see the tier subset.
        //   - Tier-filtered (no change filter): ctx.Entities = ClusterRangeEntityView walking the tier's clusters.
        //   - Neither: existing Path 1 — PartitionEntityView over the View HashMap.
        var tierIds = _systemTierClusterIds[sysIdx];
        if (hasChangeFilter)
        {
            // Path 2 with optional tier scoping. The materialized list already contains only tier-scoped dirty entities
            // (tier scoping happens in BuildFilteredSingleTable → ScanClusterDirtyEntities).
            var fullList = _parallelEntityLists[sysIdx];
            var totalEntities = fullList.Count;
            var baseSize = totalEntities / totalChunks;
            var remainder = totalEntities % totalChunks;
            var start = chunkIndex * baseSize + Math.Min(chunkIndex, remainder);
            var count = baseSize + (chunkIndex < remainder ? 1 : 0);
            entities = new PooledEntitySlice(fullList.BackingArray, start, count);

            // ClusterIds: the list Prepare captured — the tier list when present, otherwise the archetype's ActiveClusterIds. Game systems iterating via
            // ctx.Accessor.GetClusterEnumerator(ctx.ClusterIds, ...) still get the correct cluster set.
            clusterIdArray = _dispatchClusterIds[sysIdx];
            if (clusterIdArray != null)
            {
                ChunkClusterRange(sysIdx, chunkIndex, totalChunks, out clusterStart, out clusterEnd);
            }
        }
        else if (tierIds != null)
        {
            // Tier-filtered, no change filter: walk the tier's clusters via ClusterRangeEntityView.
            ChunkClusterRange(sysIdx, chunkIndex, totalChunks, out clusterStart, out clusterEnd);
            clusterIdArray = tierIds;

            var cs = _systemClusterStates[sysIdx];
            if (cs.ClusterSegment != null)
            {
                // PersistentStore path: ClusterRangeEntityView for sequential cluster-order iteration.
                // Per-worker pool (sized to WorkerCount) — must index by workerId, not chunkIndex,
                // since oversubscription (ChunksPerWorker > 1) allows chunkIndex >= workerCount.
                var rangeView = GetOrCreateTierRangeView(sysIdx, workerId);
                rangeView.Reset(cs, cs.ClusterSegment, tierIds, clusterStart, clusterEnd);
                entities = rangeView;
            }
            else
            {
                // Pure-Transient fallback: entity list was pre-materialized in PrepareFullNonVersioned (single-threaded) to avoid
                // per-worker pool leak. Each worker slices the shared list by its chunk partition.
                var entityList = _parallelEntityLists[sysIdx];
                var totalEntities = entityList.Count;
                var baseSize = totalEntities / totalChunks;
                var remainder = totalEntities % totalChunks;
                var start = chunkIndex * baseSize + Math.Min(chunkIndex, remainder);
                var count = baseSize + (chunkIndex < remainder ? 1 : 0);
                entities = new PooledEntitySlice(entityList.BackingArray, start, count);
            }
        }
        else
        {
            // Path 1: Full — zero-copy partition view over HashMap buckets (per-system views, safe for concurrent systems).
            // Per-worker pool (sized to WorkerCount) — must index by workerId, not chunkIndex, since oversubscription
            // (ChunksPerWorker > 1) allows chunkIndex >= workerCount. Reset() reconfigures the view for this chunk's slice.
            var partView = _partitionViews[sysIdx][workerId];
            partView.Reset(_systemViews[sysIdx].EntityIdsInternal, chunkIndex, totalChunks);
            entities = partView;

            // Cluster-aware parallel dispatch: partition ActiveClusterIds range for this chunk.
            // Systems that use GetClusterEnumerator(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex) get
            // correct work partitioning without iterating the full cluster set on every worker.
            var cs = _systemClusterStates[sysIdx];
            if (cs != null)
            {
                clusterIdArray = _dispatchClusterIds[sysIdx];
                ChunkClusterRange(sysIdx, chunkIndex, totalChunks, out clusterStart, out clusterEnd);
            }
        }

        // Get this worker's EntityAccessor — direct array lookup, zero dictionary overhead
        var workerAccessor = pta.GetWorkerAccessor(workerId);

        float amortizedDt = sys.CellAmortize > 0 ? _currentDeltaTime * sys.CellAmortize : _currentDeltaTime;

        var ctx = new TickContext
        {
            TickNumber = Scheduler.CurrentTickNumber,
            DeltaTime = _currentDeltaTime,
            AmortizedDeltaTime = amortizedDt,
            Accessor = workerAccessor,
            CreateSideTransaction = _createSideTxDelegate,
            Entities = entities,
            ConsumedQueues = null,
            StartClusterIndex = clusterStart,
            EndClusterIndex = clusterEnd,
            ClusterIds = clusterIdArray,
            TierBudgetMetrics = _previousTickMetrics,
            SpatialGrid = new SpatialGridAccessor(Engine?.Realm0Grid),
            Subscriptions = _subscriptionsRuntime?.CommandsFor(workerId),
            WorkerId = workerId,
            ChunkIndex = chunkIndex,
            ChunkCount = totalChunks
        };

        ctx.DebugValidateWorkerId(Scheduler.WorkerSlotCount, Scheduler.Systems[sysIdx].Name);
        Scheduler.Systems[sysIdx].CallbackAction(ctx);
    }

    /// <summary>
    /// Lazy-init helper for the per-system, per-worker <see cref="ClusterRangeEntityView"/> pool used by tier-filtered Path 1 dispatch (issue #231).
    /// Returns a view that is reconfigured each chunk via <see cref="ClusterRangeEntityView.Reset"/> — the allocation only happens the first time a given
    /// system runs under tier-filtered dispatch. Indexed by <paramref name="workerId"/> (not chunkIndex) so oversubscription
    /// (<see cref="SystemDefinition.ChunksPerWorker"/> &gt; 1) stays within the WorkerCount-sized pool.
    /// </summary>
    private ClusterRangeEntityView GetOrCreateTierRangeView(int sysIdx, int workerId)
    {
        var perWorker = _tierRangeViews[sysIdx];
        if (perWorker == null)
        {
            perWorker = new ClusterRangeEntityView[Scheduler.WorkerCount];
            _tierRangeViews[sysIdx] = perWorker;
        }
        var view = perWorker[workerId];
        if (view == null)
        {
            view = new ClusterRangeEntityView();
            perWorker[workerId] = view;
        }
        return view;
    }

    /// <summary>
    /// Split the filtered cluster list for a checkerboard system into Red and Black sets based on cell coordinates (issue #234).
    /// Red = clusters in cells where <c>(cellX + cellY + cellZ) % 2 == 0</c>, Black = the rest. Reads <see cref="_systemTierClusterIds"/>
    /// + <see cref="_systemTierClusterCount"/> as input, writes to the per-system Red/Black buffers.
    /// </summary>
    private void SplitCheckerboardClusters(int sysIdx)
    {
        var srcIds = _systemTierClusterIds[sysIdx];
        int srcCount = _systemTierClusterCount[sysIdx];
        var cs = _systemClusterStates[sysIdx];
        var grid = Engine?.Realm0Grid;

        // If no cluster data or no grid, Red = full list, Black = empty (degenerate: non-spatial archetype)
        if (srcIds == null || cs?.ClusterCellMap == null || grid == null)
        {
            _checkerboardRedIds[sysIdx] = srcIds;
            _checkerboardRedCount[sysIdx] = srcCount;
            _checkerboardBlackIds[sysIdx] = _checkerboardBlackIds[sysIdx] ?? [];
            _checkerboardBlackCount[sysIdx] = 0;
            return;
        }

        // Ensure Red/Black buffers have sufficient capacity
        if (_checkerboardRedIds[sysIdx] == null || _checkerboardRedIds[sysIdx].Length < srcCount)
        {
            _checkerboardRedIds[sysIdx] = new int[Math.Max(16, srcCount)];
        }
        if (_checkerboardBlackIds[sysIdx] == null || _checkerboardBlackIds[sysIdx].Length < srcCount)
        {
            _checkerboardBlackIds[sysIdx] = new int[Math.Max(16, srcCount)];
        }

        int redCount = 0, blackCount = 0;
        var redBuf = _checkerboardRedIds[sysIdx];
        var blackBuf = _checkerboardBlackIds[sysIdx];
        var cellMap = cs.ClusterCellMap;

        for (int i = 0; i < srcCount; i++)
        {
            int chunkId = srcIds[i];
            int cellKey = (chunkId < cellMap.Length) ? cellMap[chunkId] : -1;
            if (cellKey < 0)
            {
                // Unmapped cluster — put in Red as fallback
                redBuf[redCount++] = chunkId;
                continue;
            }
            // (x + y + z) % 2 — the three-dimensional 2-colouring. Still exhaustive and disjoint, and still gives no two 6-neighbour-adjacent cells the same
            // colour, which is the property CB-01 actually depends on. A flat world has z = 0 throughout, so its Red/Black split is unchanged.
            var (x, y, z) = grid.CellKeyToCoords(cellKey);
            if ((x + y + z) % 2 == 0)
            {
                redBuf[redCount++] = chunkId;
            }
            else
            {
                blackBuf[blackCount++] = chunkId;
            }
        }

        _checkerboardRedCount[sysIdx] = redCount;
        _checkerboardBlackCount[sysIdx] = blackCount;
    }

    /// <summary>Paths 3 &amp; 4: Versioned fallback — per-chunk Transaction (original path).</summary>
    private void ExecuteChunkWithTransaction(int sysIdx, int chunkIndex, int totalChunks, int workerId)
    {
        var fullList = _parallelEntityLists[sysIdx];
        var totalEntities = fullList.Count;

        // Balanced partitioning: first `remainder` chunks get one extra entity
        var baseSize = totalEntities / totalChunks;
        var remainder = totalEntities % totalChunks;
        var start = chunkIndex * baseSize + Math.Min(chunkIndex, remainder);
        var count = baseSize + (chunkIndex < remainder ? 1 : 0);

        TickContext.DebugValidateWorkerSlot(workerId, Scheduler.WorkerSlotCount, Scheduler.Systems[sysIdx].Name);

        // Create per-chunk Transaction on THIS worker thread (respects thread affinity)
        var tx = _currentUow.CreateTransaction();
        var success = true;
        try
        {
            var slice = new PooledEntitySlice(fullList.BackingArray, start, count);
            var sys = Scheduler.Systems[sysIdx];
            float amortizedDt = sys.CellAmortize > 0 ? _currentDeltaTime * sys.CellAmortize : _currentDeltaTime;

            // Populate ClusterIds + StartClusterIndex/EndClusterIndex for tier-filtered Versioned systems so game code that iterates via
            // ctx.Accessor.GetClusterEnumerator(ctx.ClusterIds, ...) sees the correct tier scope. The cluster partition is computed independently of
            // the entity partition above.
            int clusterStart = 0, clusterEnd = 0;
            var clusterIdArray = _dispatchClusterIds[sysIdx];
            if (clusterIdArray != null)
            {
                ChunkClusterRange(sysIdx, chunkIndex, totalChunks, out clusterStart, out clusterEnd);
            }

            var ctx = new TickContext
            {
                TickNumber = Scheduler.CurrentTickNumber,
                DeltaTime = _currentDeltaTime,
                AmortizedDeltaTime = amortizedDt,
                Transaction = tx,
                CreateSideTransaction = _createSideTxDelegate,
                Entities = slice,
                ConsumedQueues = null,
                StartClusterIndex = clusterStart,
                EndClusterIndex = clusterEnd,
                ClusterIds = clusterIdArray,
                TierBudgetMetrics = _previousTickMetrics,
                SpatialGrid = new SpatialGridAccessor(Engine?.Realm0Grid),
                Subscriptions = _subscriptionsRuntime?.CommandsFor(workerId),
                WorkerId = workerId,
                ChunkIndex = chunkIndex,
                ChunkCount = totalChunks
            };

            Scheduler.Systems[sysIdx].CallbackAction(ctx);
        }
        catch
        {
            success = false;
            throw; // Re-throw — DagScheduler's ProcessParallelQuery handles logging + _systemFailed
        }
        finally
        {
            if (success)
            {
                tx.Commit();
            }
            else
            {
                tx.Rollback();
            }

            tx.Dispose();
        }
    }

    /// <summary>
    /// Cleanup: returns pooled entity lists (if any) and resets state.
    /// Long-lived PTAs are NOT disposed here — they persist across ticks.
    /// </summary>
    private bool OnParallelQueryCleanup(int sysIdx)
    {
        // Batch epoch flush: flush all workers that participated (once per system, not per chunk).
        // This avoids N×chunks epoch refreshes and reduces global EpochManager contention.
        var pta = _parallelAccessors[sysIdx];
        if (pta != null)
        {
            for (int w = 0; w < Scheduler.WorkerCount; w++)
            {
                pta.FlushWorker(w);
            }
        }

        _parallelEntityLists[sysIdx].Return();
        _parallelEntityLists[sysIdx] = default;

        // Issue #234: checkerboard re-dispatch. After Red phase (1), return true to trigger Black phase.
        // After Black phase (2) or non-checkerboard (0), return false to proceed to successor dispatch.
        var phase = _checkerboardPhase[sysIdx];
        if (phase == 1)
        {
            return true; // Re-dispatch for Black phase
        }
        // Reset for next tick (phase 2 → 0, or was already 0)
        _checkerboardPhase[sysIdx] = 0;
        return false;
    }

    // ═══════════════════════════════════════════════════════════════
    // Tick lifecycle hooks (called by DagScheduler)
    // ═══════════════════════════════════════════════════════════════

    private void OnTickStartInternal(DagScheduler scheduler)
    {
        var now = Stopwatch.GetTimestamp();
        _currentDeltaTime = _previousTickTimestamp > 0 ? (float)((now - _previousTickTimestamp) / (double)Stopwatch.Frequency) : 0f;
        _previousTickTimestamp = now;

        // Where the tick is, for the transport threads that answer WELCOME and PONG: the tick number, the period it is running at, and the instant it began.
        // Here rather than from a timer of replication's own, because this is the statement that already knows all three — `now` is the tick's origin, and it
        // has just been taken. Three volatile writes, no allocation, and nothing at all when the application declared no subscriptions.
        _subscriptionsRuntime?.PublishTickState(scheduler.CurrentTickNumber, now, scheduler.CurrentTickMultiplier);

        // Every checkerboard system starts the tick at phase 0 (CB-02). A system that failed in its Red phase starts no Black phase, and its cleanup has left
        // phase 1 behind; kept, it would make this tick's first prepare serve the previous tick's Black list and skip Red.
        Array.Clear(_checkerboardPhase);

        // Create UoW for this tick (Deferred — batch all system commits, single WAL flush at end)
        _currentUow = Engine.CreateUnitOfWork();
        TyphonEvent.EmitRuntimePhaseUoWCreate(scheduler.CurrentTickNumber);

        // A pull-mode system input is a snapshot until someone re-queries it (#718). Must run BEFORE the tier-index rebuild and before any dispatch: both
        // read the view's entity set, and a set refreshed after them is a set the tier index does not know about.
        RefreshSystemInputViewsAtTickStart();

        // Rebuild per-archetype tier indexes ONCE per tick on the scheduler thread, before any parallel system dispatch. This eliminates the race where
        // multiple worker threads concurrently invoking OnParallelQueryPrepare for different systems on the same archetype would corrupt shared
        // TierClusterIndex buffers. After this point, every reader (parallel prepare callbacks, change-filter scans, view materialization) only READS the tier
        // index — no concurrent rebuilds possible.
        BuildTierIndexesAtTickStart();

        // OnFirstTick: runs once, on the timer thread before workers wake
        if (!_firstTickExecuted && OnFirstTick != null)
        {
            var tx = _currentUow.CreateTransaction();
            var ctx = new TickContext
            {
                TickNumber = scheduler.CurrentTickNumber,
                DeltaTime = _currentDeltaTime,
                Transaction = tx,
                CreateSideTransaction = _createSideTxDelegate,
                Entities = PooledEntityList.Empty,
                TierBudgetMetrics = _previousTickMetrics,
                SpatialGrid = new SpatialGridAccessor(Engine?.Realm0Grid),
                Subscriptions = _subscriptionsRuntime?.Commands,
                // Runs on the tick thread before any worker wakes — no worker slot belongs to it (#860).
                WorkerId = TickContext.NonWorkerId,
                ChunkCount = 1
            };

            try
            {
                OnFirstTick.Invoke(ctx);
                tx.Commit();
            }
            finally
            {
                tx.Dispose();
                _firstTickExecuted = true; // Set in finally — prevents infinite retry if handler throws
            }
        }
    }

    /// <summary>
    /// Re-query every pull-mode View the scheduler feeds to a system, once per tick, on the scheduler thread (#718).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ToView()</c> has two modes and only one of them is live. With a <c>WhereField</c> predicate it takes the incremental path and subscribes to the
    /// <see cref="ViewRegistry"/>, so spawns and destroys reach it as deltas. Without one — the plain <c>Query&lt;T&gt;().ToView()</c> that every sample and
    /// every doc uses to mean "all entities of this archetype" — it takes the pull path, which registers nothing. Membership was therefore frozen at the
    /// moment the view was constructed, and a system declared with <c>input: () =&gt; view</c> ran against that snapshot for the entire life of the runtime:
    /// no system ever processed an entity spawned after startup. Silent — the system ran every tick and reported a plausible count.
    /// </para>
    /// <para>
    /// The mechanism to fix it already existed: <c>EcsView.Refresh</c> dispatches a pull view into a full re-query and diff. Nothing called it, because
    /// system-input views are consumed as cached entity sets rather than refreshed. This drives it once per tick, single-threaded, before anything reads the
    /// set.
    /// </para>
    /// <para>
    /// This is the interim of the three directions #718 lists, and its cost is honest: an O(N) re-query per tick per pull system input, where the incremental
    /// path pays O(deltas). It is correct for every pull view including the <c>.Where(lambda)</c> and spatial ones, which is what the cheaper alternatives are
    /// not — a delta channel keyed on archetype membership would still be exact only for the UNFILTERED case. The endpoint is a lifecycle-level view
    /// notification channel (direction 1), which is a design change to the view subsystem rather than a repair.
    /// </para>
    /// </remarks>
    private void RefreshSystemInputViewsAtTickStart()
    {
        Transaction tx = null;
        try
        {
            for (var i = 0; i < _systemViews.Length; i++)
            {
                var view = _systemViews[i];
                // Incremental views are excluded deliberately, not incidentally: they already receive spawns and destroys as deltas, and draining their ring
                // buffer here would consume entries the per-system consumption path expects to still be there.
                if (view == null || !view.IsPullMode || view.IsDisposed || view.LastSystemInputRefreshTick == Scheduler.CurrentTickNumber)
                {
                    continue;
                }

                // Created lazily: a schedule with no pull system input must not pay for a transaction it never uses.
                tx ??= _currentUow.CreateTransaction();
                view.RefreshFromScheduler(tx);
                view.ClearDelta();
                view.LastSystemInputRefreshTick = Scheduler.CurrentTickNumber;
            }

            tx?.Commit();
        }
        finally
        {
            tx?.Dispose();
        }
    }

    /// <summary>
    /// Walk every system that declares a tier filter, and rebuild the per-archetype <see cref="TierClusterIndex"/> once per tick on the scheduler thread.
    /// The version-skip in <see cref="TierClusterIndex.RebuildIfStale"/> means redundant calls (multiple systems on the same archetype) short-circuit on a
    /// two-int compare. The actual rebuild only runs when the grid tier version OR the archetype cluster set has changed since the previous tick.
    /// </summary>
    private void BuildTierIndexesAtTickStart()
    {
        var grid = Engine?.Realm0Grid;
        if (grid == null)
        {
            return;
        }

        // Issue #233: transition WakePending → Active for all archetypes BEFORE rebuilding tier indexes.
        // This ensures woken clusters appear in this tick's per-tier lists. The TransitionWakePendingToActive method is guarded by _lastWakeTransitionTick
        // so calling it for the same archetype via multiple systems is a no-op after the first call.
        long tick = Scheduler.CurrentTickNumber;
        for (int i = 0; i < Scheduler.AllSystemCount; i++)
        {
            var cs = _systemClusterStates[i];
            if (cs?.SleepStates != null)
            {
                cs.TransitionWakePendingToActive(tick);
            }
        }

        for (int i = 0; i < Scheduler.AllSystemCount; i++)
        {
            var sys = Scheduler.Systems[i];
            var cs = _systemClusterStates[i];

            // Late-spawn recovery: if the archetype had no ClusterState at all when ResolveChangeFilters ran, _systemClusterStates[i] is null. Re-evaluate
            // now — the state may have been created between construction and the first tick. One registry lookup per tick per unbound QuerySystem.
            // RT-1: for every QuerySystem, not only tier ones — the sleep filter reads the same binding.
            //
            // #662 again: this used to take the FIRST cluster-eligible archetype it found rather than the system's own view archetype — the exact defect
            // #662 fixed at the construction site, left behind in the recovery path. In any schema with more than one cluster archetype it hands the system
            // another archetype's cluster ids, which is a page-index-out-of-range throw when the counts differ and silent wrong work when they match. It
            // also never set `_systemArchetypeIds`, so a system rescued here kept gate 1 of the #327 touch rollup shut for the rest of the session.
            if (cs == null && sys.Type == SystemType.QuerySystem && sys.InputFactory != null && _systemViews[i] != null)
            {
                var viewArchetypeId = _systemViews[i].QueriedArchetypeId;
                if (viewArchetypeId < Engine._archetypeStates.Length)
                {
                    var meta = ArchetypeRegistry.GetMetadata(viewArchetypeId);
                    var es = Engine._archetypeStates[viewArchetypeId];
                    if (meta is { IsClusterEligible: true } && es?.ClusterState != null)
                    {
                        cs = es.ClusterState;
                        _systemClusterStates[i] = cs;
                        _systemArchetypeIds[i] = viewArchetypeId;
                    }
                }
            }

            if (cs == null || _systemViews[i] == null)
            {
                continue;
            }

            // RT-1 / TI-01: every tier a system of this tick can select — its own AND its view's — is rebuilt and prepared HERE, single-threaded. Dispatch
            // only reads: a rebuild from a worker would zero the per-tier arrays under a parallel system walking them zero-copy. A disjoint pair is left for
            // the dispatch path to report (EffectiveTier throws there, as it always did); the tick path itself never throws.
            var tier = (SimTier)((byte)sys.TierFilter & (byte)_systemViews[i].TierFilter);
            if (tier == SimTier.All || tier == SimTier.None)
            {
                continue;
            }

            cs.TierIndex ??= new TierClusterIndex();
            cs.TierIndex.RebuildIfStale(grid, cs);
            // A multi-tier set (SimTier.Near, …) is served from a merge cache the first read after a rebuild fills; fill it here so dispatch never does.
            cs.TierIndex.GetClustersArray(tier, out _);
        }
    }

    private void OnTickEndInternal(DagScheduler scheduler)
    {
        // All system transactions have been committed individually.
        //
        // Issue #229 Phase 3 ordering — WriteTickFence runs BEFORE UoW.Flush.
        // Reason: the cluster tick fence publishes WAL records (ClusterTickFence chunks) describing the tick's dirty cluster-content changes.
        // It is ALSO the point where the Phase 3 migration fence runs (DetectClusterMigrations + ExecuteMigrations), mutating cluster pages directly.
        // By running WriteTickFence first, the subsequent UoW.Flush waits for a currentLsn that includes those publishes, so all migration writes become
        // per-tick durable via the/ same fsync that covers normal system commits. Moving WriteTickFence after Flush (the pre-Phase-3 ordering) would make
        // migration writes durable only at the NEXT tick's flush — a one-tick lag that's acceptable for the original R-Tree maintenance use case but unsafe
        // for persistent cluster content mutation.
        //
        // See debate decision Q1 in the Phase 3 design notes, and claude/design/Spatial/SpatialTiers/01-spatial-clusters.md §"Migration fence WAL atomicity".
        // Pass the per-tick UoW's shared ChangeSet so all dirty pages mutated during the tick fence (migrations, shadow drains, spatial maintenance) flow
        // through one accounting bucket. UoW.Flush below handles the writeback per the configured DurabilityMode (and skips it entirely in WAL mode where
        // WAL records carry durability). Without this, each tick-fence callee would create+commit its own private ChangeSet, doing redundant disk I/O on
        // every tick (measured at ~22 ms / 88% of ExecuteMigrations time on a 1071-migration AntHill storm).
        // Caught, on BOTH arms, and that is #890's other half. The serial arm runs the whole fence inline on this thread, so a throw from it used to
        // escape OnTickEndInternal entirely: the UoW flush and dispose below never ran (`_currentUow` leaked and was overwritten by the next tick's), the
        // outcome stayed the PREVIOUS tick's `Success`, and the runtime went on ticking — the same silence the parallel arm had, reached a different way.
        // The parallel arm's own phases are caught by the scheduler and reported through RecordSystemFailure; what can still reach here from it is a throw
        // in RunParallelFence's serial prep, which is engine code on the tick driver and belongs in the same verdict.

        // Reset the replication context BEFORE the fence, not beside the dispatch. A fence that throws never reaches its dispatch, so a reset placed there
        // would leave the previous tick's journal standing on exactly the tick whose emptiness is the thing worth observing.
        _subscriptionsContext.Reset(scheduler.CurrentTickNumber, scheduler.WorkerCount);

        // The tick boundary the identity quarantine is defined against. Every netId released during the previous tick becomes reissuable here and not before,
        // so no frame can carry both the leave of an identity's old holder and the enter of its new one — the wire applies leaves last, so such a frame would
        // land the leave on the entity that just entered. Once per tick, on the driver thread, before the track dispatches.
        _netIds.DrainQuarantine();

        try
        {
            if (_parallelFenceEnabled)
            {
                // Timed from OUT HERE rather than inside RunParallelFence, and that placement is the point: the stall a host feels is the whole call, which
                // includes the serial prep before the DAG is dispatched and the epoch fence window's close after it. `LastFenceWallTicks` — what
                // `LastFenceSpanMs` publishes — starts at Prep's Prepare, so it cannot see the serial prep, and a worker-count sweep reading only the span
                // reports a speed-up on a fraction of the interruption. `finally` so a fence that throws still reports how long it blocked the host before
                // it did: the tick is failed either way (#890), but a stall is a stall.
                var stallStart = Stopwatch.GetTimestamp();
                try
                {
                    // RunParallelFence brackets its own serial-prep portion with the WriteTickFence phase marker and dispatches the Fence DAG *outside* it —
                    // the four Fence systems carry their own Engine-Post telemetry, so wrapping the dispatch would double-count them into `writeTickFenceUs`.
                    RunParallelFence(scheduler);
                }
                finally
                {
                    Engine.SetLastFenceStallTicks(Stopwatch.GetTimestamp() - stallStart);
                }
            }
            else
            {
                // EW-01's window is opened INSIDE DatabaseEngine.WriteTickFence and closes when that returns, so a track dispatched afterwards would run
                // OUTSIDE it — while the parallel path runs the same track inside one. Opening a window here holds it across both the fence and the dispatch,
                // so the two fence paths offer the same quiescence, which is what SingleVersion and Transient reads require (AC-05 / SNAP-02: no snapshot to
                // read from, so the guarantee has to be that nothing is writing). ExclusiveWindow keeps a DEPTH rather than a flag precisely so this nests —
                // the inner Open() takes it to 2 and back to 1, and the track still sees an open window.
                using var window = Engine.EpochManager.FenceWindow.Open();
                InspectorPhase(TickPhase.WriteTickFence, () => Engine.WriteTickFence(scheduler.CurrentTickNumber, _currentUow?.ChangeSet));

                // The trap #955 closes. DispatchDeferredTracks had exactly ONE call site — inside RunParallelFence — so with EnableParallelFence = false
                // every deferred track silently never executed. That was invisible while the Fence DAG was the only one, because it is not declared in
                // serial mode at all: the loop was empty, so nothing was missing. A second deferred track inherits the trap instead of revealing it.
                _subscriptionsContext.NoteFence();
                scheduler.DispatchDeferredTracks();
            }
        }
        catch (Exception ex)
        {
            // Latched through the scheduler so there is ONE fence-failure verdict however the fence failed, and so the terminal gate in ExecuteCallbacks and
            // the host callback both fire exactly as they do for a phase that threw. Execution then continues to the flush below: TP-01a's other half is
            // that the flush is mandatory, and a partly-run fence's pages are exactly the ones that must not be left un-flushed AND un-logged.
            scheduler.RecordFenceDriverFailure(ex);
        }

        // Flush the UoW to make all Deferred writes (including the tick fence publishes above) durable, then dispose. UoW.Flush in WAL mode calls
        // WalManager.RequestFlush + WaitForDurable(currentLsn), where currentLsn is captured at the moment of the call — so it includes every publish made
        // in WriteTickFence.
        InspectorPhase(TickPhase.UowFlush, () =>
        {
            try
            {
                _currentUow?.Flush();
            }
            catch (Exception)
            {
                // SUB-02's fourth clause, at the only point it can be observed: the flush throws out of this phase and the publication gate below is never
                // reached, so the frames this tick produced would sit in their slots forever. Every session that produced one is closed here instead.
                _subscriptionsRuntime?.DiscardFrames();
                throw;
            }
            finally
            {
                _currentUow?.Dispose();
                _currentUow = null;
                TyphonEvent.EmitRuntimePhaseUoWFlush(scheduler.CurrentTickNumber, 0);

                // In the `finally`, so a flush that THREW still stamps. SUB-02's fourth clause is about exactly that tick — a flush that fails after
                // frames were produced has to close every session, because produced-but-unpublished frames leave client baselines ahead of the
                // clients — and a verifier for it needs to see that the flush was reached at all.
                _subscriptionsContext.NoteFlush();
            }
        });

        // Issue #234: compute per-tier budget metrics from this tick's system telemetry, for the next tick's TickContext.
        ComputeTierBudgetMetrics();
        CaptureChunkCosts();

        // Publish this tick's outcome BEFORE the output phase, so a host reading LastTickOutcome from a subscription callback already sees the verdict.
        // Written on EVERY tick under EVERY policy (#567 AC8b) — a stale outcome must never be mistaken for a fresh one. Under Isolate this is always Success:
        // a tick in which a system threw and its branch was skipped completed exactly as that policy promises. Per-system detail stays in SkipReason.
        // A fence phase that threw is its own verdict (#890, design/Runtime/08-strict-tick-abort.md D3): the fence is the work that ends the tick, so there is
        // no "rest of the tick" to cancel and it is never reported as an abort — but it is emphatically not a Success either, and before #890 it WAS, because
        // nothing but per-system telemetry recorded it. Checked after the abort so a tick carrying both keeps naming the user system that started it.
        var tickAborted = scheduler.IsTickAborted;
        var fenceFailed = !tickAborted && scheduler.IsFenceFailed;
        LastTickOutcome = tickAborted ? scheduler.AbortedOutcome : fenceFailed 
            ? scheduler.FenceFailureOutcome : TickOutcome.ForSuccess(scheduler.CurrentTickNumber);

        // Publication is the ONE tick-end act carrying tick-wide "this was a good tick" semantics, so it is the only one of the three that may be skipped on an
        // aborted or fence-failed tick (#567) — the fence and the flush above ran unconditionally and must keep doing so, rule TP-01a.
        if (!tickAborted && !fenceFailed)
        {
            // Replication publishes here, after the flush, under the same gate as the compute half PLUS the fault check — SUB-02 makes the two skippable
            // together and only together. The extra condition exists because making a replication failure non-terminal (Track.FailureIsTerminal) removed the
            // signal this gate used to key on: a stage throw latches neither `tickAborted` nor `fenceFailed`, so without it a tick whose compute blew up would
            // publish as though it had succeeded — moving every receiving session's baseline past records it was never sent.
            if (!_subscriptionsContext.Faulted)
            {
                _subscriptionsContext.NotePublish();

                // The frames themselves leave here, and nowhere else. Releasing the committed tick is what makes them sendable, and it happens after the
                // flush above because a frame must never tell a session a story the WAL does not carry (SUB-02).
                _subscriptionsRuntime?.PublishFrames(scheduler.CurrentTickNumber);
            }
            else
            {
                // A replication stage faulted after frames were produced. They describe a tick that will never be published, so they can never be sent —
                // and the sessions holding them would fill their slots and stall. Closing them is SUB-02's fourth clause.
                _subscriptionsRuntime?.DiscardFrames();
            }
        }
        else
        {
            // Aborted or fence-failed: same reasoning as the faulted case above, and it has to happen on every such tick rather than only on the first, since
            // each one can have produced frames of its own.
            _subscriptionsRuntime?.DiscardFrames();
        }

        if ((tickAborted || fenceFailed) && !_tickAbortedNotified)
        {
            // One event for both verdicts: a host that reacts to OnTickAborted by stopping the runtime wants to do exactly that here too, and
            // TickOutcome.Reason tells the two apart.
            // Fires once, on the TickDriver, after the tick has fully drained — so every worker's stores are ordered ahead of the handler reading the outcome.
            // Subsequent ticks never reach here: DagScheduler.ExecuteCallbacks returns early once the abort latch is set.
            _tickAbortedNotified = true;
            OnTickAborted?.Invoke(this, LastTickOutcome);
        }

        // Last statement of the tick, and on EVERY path through it — an aborted or fence-failed tick seals a journal too, because "nothing was computed or
        // published" is an observation that needs a sealed record to be read from, not an absence of one.
        SubscriptionsJournalObserver?.Invoke(_subscriptionsContext);
    }

    /// <summary>
    /// Parallel cluster tick fence orchestration. Runs on TickDriver. Resets the shared <see cref="FenceContext"/>, drains dormancy wake requests, runs the
    /// serial component-table fences, then triggers ONE <see cref="DagScheduler.DispatchDeferredTracks"/> call. The scheduler walks the four chained Fence-DAG
    /// exec systems (Prep → Migrate → AabbRefresh → Finalize) via the declared <c>.After()</c> edges — each phase's typed <c>Prepare(FenceContext)</c> builds
    /// its plan and sets the dynamic chunk count. Aggregates the highest WAL LSN across tables + Finalize and publishes it via
    /// <see cref="DatabaseEngine.UpdateLastTickFenceLSNAtomic"/>. See <c>claude/design/Spatial/SpatialTiers/01-spatial-clusters.md</c>
    /// §"Parallel migration apply" and rule MD-02 in <c>rules/spatial.md</c>.
    /// </summary>
    private void RunParallelFence(DagScheduler scheduler)
    {
        // WAL + checkpoint are mandatory (ADR-054), so the per-worker ChangeSet cleanup via ReleaseDirtyMarks is always correct here: the checkpoint
        // thread drains the capped pages. The serial WriteTickFence path is reached only via the EnableParallelFence=false opt-out (call site in
        // OnTickEndInternal).
        var ctx = Engine.FenceContext;

        // EW-01's window covers the WHOLE phase — the serial prep AND the Fence DAG dispatched below — because both mutate the structures the rule names.
        // Opening it enrols the TickDriver thread; each fence worker enrols itself in FencePhaseExecSystemBase.Execute.
        using var window = Engine.EpochManager.FenceWindow.Open();

        // `TickPhase.WriteTickFence` brackets ONLY the serial prep — context reset, dormancy drain, and the serial component-table fences. This is the sole
        // genuinely-serial post-tick fence cost; the Fence DAG dispatched below is four chained systems on the Engine-Post track, each with its own per-system
        // telemetry. Pre-#354 the marker wrapped `DispatchDeferredTracks` too, so `writeTickFenceUs` double-counted the Fence systems' wall-time.
        InspectorPhase(TickPhase.WriteTickFence, () =>
        {
            ctx.Reset(scheduler.CurrentTickNumber, _currentUow?.ChangeSet, scheduler.WorkerCount, Options.FenceChunkOversubscription, _liveFenceCost,
                Options.EntityMapBulkMinEntriesPerBucket);

            // Drain this engine's dormancy wake requests on TickDriver (single-threaded contract from issue #233).
            Engine.DrainDormancyWakeRequests();

            // Serial table fences on TickDriver. Uses the UoW's ChangeSet (single-thread context).
            //
            // The epoch scope is NOT optional: ProcessTableFence creates a ChunkAccessor over the component segment, which requires one, and the serial
            // WriteTickFenceCore and the worker-side FenceExecSystem both take one for exactly this reason. This path did not, and was reachable only
            // through #837 — a spawn-staging chunk in a table's DirtyBitmap was the sole way to make entryCount non-zero for a cluster archetype, so the
            // accessor was never created and the omission never surfaced. Scoped to the loop alone: an epoch scope pins every page touched inside it for
            // its whole life (EP-01), and the Fence DAG dispatched below takes its own per-worker scopes.
            using (EpochGuard.Enter(Engine.EpochManager))
            {
                foreach (var table in Engine.GetAllComponentTables())
                {
                    if (table.StorageMode == StorageMode.Versioned || table.DirtyBitmap == null)
                    {
                        continue;
                    }

                    long lsn = Engine.ProcessTableFence(table, ctx.TickNumber, ctx.UowChangeSet);
                    if (lsn > ctx.HighestTableLsn)
                    {
                        ctx.HighestTableLsn = lsn;
                    }
                }
            }
        });

        // Dispatch the deferred Engine-Post track (the Fence DAG) — OUTSIDE the WriteTickFence marker (see above). The scheduler walks Prep → Migrate →
        // AabbRefresh → Finalize via the declared `.After()` edges. Each phase's Prepare(ctx) builds its plan from FenceContext and sets RuntimeChunkCount;
        // ShouldRun/Prepare returning 0 skips cleanly with successor fan-out.
        // Stamped immediately before the dispatch, because this ONE call walks Engine-Post and Engine-Subscriptions in order: there is no statement between
        // the Fence DAG and the replication track to stamp from. Track order is a barrier (PH-01), and THAT is what guarantees every fence phase completes
        // before replication's first stage starts — the stamp records the serial fence prep, and the barrier does the rest.
        _subscriptionsContext.NoteFence();
        scheduler.DispatchDeferredTracks();

        ctx.HighestArchetypeLsn = _fenceFinalizeExec.HighestLsn;
        long overall = Math.Max(ctx.HighestTableLsn, ctx.HighestArchetypeLsn);
        if (overall > 0)
        {
            Engine.UpdateLastTickFenceLSNAtomic(overall);
        }

        // Step 14 (D2): the migration cost model charges a frame budget, so it needs the migration phases' CPU-to-span ratio — how many workers' worth of
        // CPU one unit of span bought. Summed across the three phases a migration passes through; a tick that moved nothing leaves the previous value.
        var migrationCpuTicks = _fenceMigrateExec.TotalWallTicks + _fenceIndexMassUpdateExec.TotalWallTicks + _fenceEntityMapUpdateExec.TotalWallTicks;
        var migrationSpanTicks = _fenceMigrateExec.PhaseSpanTicks + _fenceIndexMassUpdateExec.PhaseSpanTicks + _fenceEntityMapUpdateExec.PhaseSpanTicks;
        if (migrationSpanTicks > 0 && migrationCpuTicks > 0)
        {
            Engine.SetLastFenceMigrationParallelism(migrationCpuTicks / (double)migrationSpanTicks);
        }

        // #911 — the fence's own span, published beside the ratio above because they are read together: the summed-CPU figures on the telemetry surface are
        // uninterpretable without the span they were spent in. Pushed rather than pulled for the same reason the parallelism is — the engine has no handle
        // on the runtime, and this is the one place that knows the phase timings.
        Engine.SetLastFenceSpanTicks(LastFenceWallTicks);

        if (Options.AdaptiveFenceCost)
        {
            _liveFenceCost.UpdatePhase(FencePhase.Prep, _fencePrepExec.TotalWallTicks, _fencePrepExec.TotalUnitCount);
            _liveFenceCost.UpdatePhase(FencePhase.Migrate, _fenceMigrateExec.TotalWallTicks, _fenceMigrateExec.TotalUnitCount);
            _liveFenceCost.UpdatePhase(FencePhase.IndexMassUpdate, _fenceIndexMassUpdateExec.TotalWallTicks, _fenceIndexMassUpdateExec.TotalUnitCount);
            _liveFenceCost.UpdatePhase(FencePhase.EntityMapUpdate, _fenceEntityMapUpdateExec.TotalWallTicks, _fenceEntityMapUpdateExec.TotalUnitCount);
            _liveFenceCost.UpdatePhase(FencePhase.AabbRefresh, _fenceAabbRefreshExec.TotalWallTicks, _fenceAabbRefreshExec.TotalUnitCount);
            _liveFenceCost.UpdatePhase(FencePhase.Finalize, _fenceFinalizeExec.TotalWallTicks, _fenceFinalizeExec.TotalUnitCount);
        }
    }

    /// <summary>
    /// Last tick's Prep phase — cell-crossing detection, the relocation throttle and the repair planner.
    /// </summary>
    /// <remarks>
    /// <b>Exposed because a fence measured only at Migrate, IndexMassUpdate and EntityMapUpdate leaves most of its cost unattributed.</b> #872's partition
    /// campaign measured a 128 000-entity fence at 47 ms of which migration accounted for 10.4 ms, and had no instrumentation to say what the other 36.7 ms
    /// was — which made the largest term in the measurement the one nobody could name. Prep, AabbRefresh and Finalize are where it lives, and all three
    /// already keep the counters; only the accessors were missing.
    /// </remarks>
    internal (long SpanTicks, long CpuTicks, long Units, int Chunks) LastPrepStats
        => _fencePrepExec == null
            ? (0, 0, 0, 0)
            : (_fencePrepExec.PhaseSpanTicks, _fencePrepExec.TotalWallTicks, _fencePrepExec.TotalUnitCount, _fencePrepExec.PlanForTest.ChunkCount);

    /// <summary>Last tick's AabbRefresh phase — cluster bound recompute, drift detection and the outlier guard.</summary>
    /// <inheritdoc cref="LastPrepStats" path="/remarks"/>
    internal (long SpanTicks, long CpuTicks, long Units, int Chunks) LastAabbRefreshStats
        => _fenceAabbRefreshExec == null
            ? (0, 0, 0, 0)
            : (_fenceAabbRefreshExec.PhaseSpanTicks,
               _fenceAabbRefreshExec.TotalWallTicks,
               _fenceAabbRefreshExec.TotalUnitCount,
               _fenceAabbRefreshExec.PlanForTest.ChunkCount);

    /// <summary>Last tick's Finalize phase — bookkeeping clear, dormancy sweep, cluster finalization and the WAL emit.</summary>
    /// <inheritdoc cref="LastPrepStats" path="/remarks"/>
    internal (long SpanTicks, long CpuTicks, long Units, int Chunks) LastFinalizeStats
        => _fenceFinalizeExec == null
            ? (0, 0, 0, 0)
            : (_fenceFinalizeExec.PhaseSpanTicks,
               _fenceFinalizeExec.TotalWallTicks,
               _fenceFinalizeExec.TotalUnitCount,
               _fenceFinalizeExec.PlanForTest.ChunkCount);

    /// <summary>Last tick's Migrate phase, in the same shape. Needed to compare the inline EntityMap path against the staged one: the inline path's cost
    /// lands here, the staged path's in <see cref="LastEntityMapUpdateStats"/>.</summary>
    internal (long SpanTicks, long CpuTicks, long Units, int Chunks) LastMigrateStats
        => _fenceMigrateExec == null
            ? (0, 0, 0, 0)
            : (_fenceMigrateExec.PhaseSpanTicks, _fenceMigrateExec.TotalWallTicks, _fenceMigrateExec.TotalUnitCount, _fenceMigrateExec.PlanForTest.ChunkCount);

    /// <summary>Last tick's EntityMapUpdate phase, in the same shape as <see cref="LastIndexMassUpdateStats"/> (#872 step 7).</summary>
    internal (long SpanTicks, long CpuTicks, long Units, int Chunks) LastEntityMapUpdateStats
        => _fenceEntityMapUpdateExec == null
            ? (0, 0, 0, 0)
            : (_fenceEntityMapUpdateExec.PhaseSpanTicks,
               _fenceEntityMapUpdateExec.TotalWallTicks,
               _fenceEntityMapUpdateExec.TotalUnitCount,
               _fenceEntityMapUpdateExec.PlanForTest.ChunkCount);

    /// <summary>
    /// Last tick's IndexMassUpdate phase: summed per-chunk wall time in <see cref="Stopwatch"/> ticks, entries applied, and chunks dispatched.
    /// </summary>
    /// <remarks>
    /// Exposed so <c>AC-6.5</c> can be measured on the PHASE rather than on the primitive underneath it. Hand-rolling W threads around
    /// <c>BTree.UpdateValues</c> measures the descent's scaling and misses everything the phase actually pays for: the planner choosing its own chunk count
    /// from the cost model, bin-packing, dependency resolution, the per-chunk ChangeSet and EpochGuard, and the barriers either side.
    /// </remarks>
    internal (long SpanTicks, long CpuTicks, long Units, int Chunks) LastIndexMassUpdateStats
        => _fenceIndexMassUpdateExec == null
            ? (0, 0, 0, 0)
            : (_fenceIndexMassUpdateExec.PhaseSpanTicks,
               _fenceIndexMassUpdateExec.TotalWallTicks,
               _fenceIndexMassUpdateExec.TotalUnitCount,
               _fenceIndexMassUpdateExec.PlanForTest.ChunkCount);

    /// <summary>
    /// Last tick's fence from the start of Prep's Prepare to the end of the last phase that dispatched a chunk — the six spans PLUS the scheduler's gaps
    /// between them. The sum of the six is what the partitioning costs; this is what the host waits.
    /// </summary>
    internal long LastFenceWallTicks
    {
        get
        {
            if (_fencePrepExec == null)
            {
                return 0;
            }

            var start = _fencePrepExec.PhaseStartTicks;
            var end = Math.Max(
                Math.Max(_fenceFinalizeExec.PhaseEndTicks, _fenceAabbRefreshExec.PhaseEndTicks),
                Math.Max(Math.Max(_fenceEntityMapUpdateExec.PhaseEndTicks, _fenceIndexMassUpdateExec.PhaseEndTicks),
                    Math.Max(_fenceMigrateExec.PhaseEndTicks, _fencePrepExec.PhaseEndTicks)));
            return start > 0 && end > start ? end - start : 0;
        }
    }

    /// <summary>
    /// Last tick's serial steps inside the phases, in <see cref="Stopwatch"/> ticks: the #886 Prep tails (Migrate's Prepare — the sliced archetypes'
    /// drain-order sort among them since #910; every archetype's sort is the <c>PrepSortMs</c> sub-span), the merge and leaf-snap (index Prepare), the
    /// merge and bucket partition (EntityMap Prepare), and the WAL emit summed over every archetype Finalize handled. Each is a piece of a phase span that
    /// no worker count can shrink, which is why they are reported apart from the spans.
    /// </summary>
    internal (long MigrateTail, long IndexMerge, long EntityMapMerge, long FinalizeEmit, long FinalizeAppend) LastFenceSerialTicks
    {
        get
        {
            if (_fenceMigrateExec == null)
            {
                return (0, 0, 0, 0, 0);
            }

            long emit = 0;
            long append = 0;
            var states = Engine._archetypeStates;
            if (states != null)
            {
                for (var aid = 0; aid < states.Length; aid++)
                {
                    var cs = states[aid]?.ClusterState;
                    if (cs != null)
                    {
                        emit += cs.LastTickFinalizeEmitTicks;
                        append += cs.LastTickFinalizeAppendTicks;
                    }
                }
            }

            return (_fenceMigrateExec.LastTailTicks, _fenceIndexMassUpdateExec.LastSerialPrepareTicks, _fenceEntityMapUpdateExec.LastSerialPrepareTicks,
                emit, append);
        }
    }

    /// <summary>
    /// Last tick's per-chunk sort CPU in the Migrate phase's <c>OnAfterChunk</c>, in <see cref="Stopwatch"/> ticks summed over the chunks: the index runs,
    /// the EntityMap runs, the dirty-delta grouping. Worker time, not span — the chunks run in parallel — reported so the sorts can be A/B'd.
    /// </summary>
    internal (long IndexSort, long MapSort, long DirtySort) LastFenceChunkSortTicks => _fenceMigrateExec?.ChunkSortTicks ?? default;

    /// <summary>
    /// Wraps a tick phase with paired profiler boundary events. When <see cref="TelemetryConfig.ProfilerActive"/> is false the JIT folds both Emit calls to
    /// no-ops — this method compiles to just <c>action()</c>.
    /// </summary>
    private void InspectorPhase(TickPhase phase, Action action)
    {
        // Real span (not paired instants) so child spans started inside the action — PageCacheFlush, BTreeInsert, ClusterMigration, etc. —
        // attach via parentSpanId. The previous EmitPhaseStart/EmitPhaseEnd instant pair is gone: phases are now first-class spans rendered
        // in the profiler's phase track. PhaseStart/PhaseEnd kinds are still defined in TraceEventKind.cs for old-trace decode compatibility,
        // but no producer emits them anymore.
        using var phaseScope = TyphonEvent.BeginRuntimePhase(phase);
        action();
    }

    /// <summary>
    /// Aggregate per-system telemetry by tier for <see cref="TierBudgetMetrics"/> (issue #234). Each system's <see cref="SystemTelemetry.DurationUs"/> is
    /// attributed to the tier(s) in its <see cref="SystemDefinition.TierFilter"/>. Multi-tier systems contribute equally to each matching tier.
    /// </summary>
    private void ComputeTierBudgetMetrics()
    {
        var metrics = new TierBudgetMetrics { BudgetMs = 1000f / Options.BaseTickRate };

        for (int i = 0; i < Scheduler.AllSystemCount; i++)
        {
            ref var t = ref Scheduler.GetCurrentSystemMetrics(i);
            if (t.WasSkipped || t.FirstChunkGrabTick == 0)
            {
                continue;
            }

            // DurationUs hasn't been computed yet (ComputeAndRecordTelemetry runs after OnTickEndInternal).
            // Compute from raw Stopwatch ticks directly.
            long durationTicks = t.LastChunkDoneTick - t.FirstChunkGrabTick;
            if (durationTicks <= 0)
            {
                continue; // Defensive: skip systems with unset or corrupted timestamps
            }
            float costMs = (float)((double)durationTicks / Stopwatch.Frequency * 1000.0);
            metrics.TotalCostMs += costMs;

            var tier = Scheduler.Systems[i].TierFilter;
            if (tier == SimTier.All || tier == SimTier.None)
            {
                // Non-tier-filtered systems contribute to total but not per-tier buckets
                continue;
            }

            int tierCount = tier.TierCountOf();
            float costPerTier = tierCount > 1 ? costMs / tierCount : costMs;
            int entitiesPerTier = tierCount > 1 ? t.EntitiesProcessed / tierCount : t.EntitiesProcessed;

            if (((byte)tier & (byte)SimTier.Tier0) != 0) { metrics.Tier0CostMs += costPerTier; metrics.Tier0EntityCount += entitiesPerTier; }
            if (((byte)tier & (byte)SimTier.Tier1) != 0) { metrics.Tier1CostMs += costPerTier; metrics.Tier1EntityCount += entitiesPerTier; }
            if (((byte)tier & (byte)SimTier.Tier2) != 0) { metrics.Tier2CostMs += costPerTier; metrics.Tier2EntityCount += entitiesPerTier; }
            if (((byte)tier & (byte)SimTier.Tier3) != 0) { metrics.Tier3CostMs += costPerTier; metrics.Tier3EntityCount += entitiesPerTier; }
        }

        metrics.UtilizationRatio = metrics.BudgetMs > 0 ? metrics.TotalCostMs / metrics.BudgetMs : 0f;
        _previousTickMetrics = metrics;
    }

    /// <summary>
    /// Reads the <c>(ActiveClusterIds, ActiveClusterCount)</c> pair as a usable snapshot — #582 face 2.
    /// </summary>
    /// <remarks>
    /// The pair is mutated by committing workers while other workers walk it, with nothing excluding the overlap. `AddToActiveList` releases the grown
    /// array before the count; this acquires them in the mirror order, so the array is never older than the count and `count &lt;= array.Length` holds.
    /// Loading the array first — which three call sites used to do — admits a plain interleaving that needs no reordering to fault: read the old
    /// length-16 array, a concurrent spawn resizes and bumps the count, read the count as 17, index 16 of the old array.
    /// <para>
    /// This makes the pair CONSISTENT. It does not make the walk safe against a concurrent destroy: `RemoveFromActiveList` swaps with the last element, so
    /// a walker holding a count from before it can still visit one cluster twice and skip the removed one, whose chunk may then be freed under it. That is
    /// #582 face 1 and needs a snapshot or epoch protocol, not an ordering fix.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int[] ReadActiveClusterList(ArchetypeClusterState cs, out int count) => cs.ReadActiveClusterList(out count);

    private TickContext OnSystemStartInternal(int sysIdx, int workerId)
    {
        TyphonEvent.BeginRuntimeTransactionLifecycleTls((ushort)sysIdx);

        // P2 of umbrella #342 — emit the catalog descriptor for this view's query identity (see OnParallelQueryPrepare for the parallel-path analogue).
        // Tracker dedups across the session.
        if (_systemViews[sysIdx] != null && TelemetryConfig.QueryActive)
        {
            _systemViews[sysIdx].EmitDescriptorIfNeeded();
            // Capture the per-tick QueryPlan start ts; OnSystemEndInternal emits the span with this start and Stopwatch.GetTimestamp() as end.
            // Pull-mode views never go through BuildPlan, so the Execution Inspector would be empty without this synthetic bracket.
            _systemQueryPlanStartTicks[sysIdx] = Stopwatch.GetTimestamp();
        }

        // RT-1: one run of this system, and its one selection — the cellAmortize bucket is keyed on the run count. Stored where the parallel path keeps its
        // own, so every materialization (BuildFullViewEntitySet, the change-filter fallback) reads the same clusters.
        _systemRunCount[sysIdx]++;
        if (_systemViews[sysIdx] != null)
        {
            var selected = SelectDispatchClusters(sysIdx, EffectiveTier(sysIdx), out var selectedCount);
            _systemTierClusterIds[sysIdx] = selected;
            _dispatchClusterIds[sysIdx] = selected;
            _dispatchClusterCount[sysIdx] = selected != null ? selectedCount : 0;
        }

        // Create a Transaction on the CALLING THREAD (worker thread).
        // This respects Transaction's single-thread affinity constraint.
        var tx = _currentUow.CreateTransaction();
        _systemTransactions[sysIdx] = tx;

        // #197: Build entity set based on input View and change filter
        IReadOnlyCollection<EntityId> entities;
        var hasChangeFilter = _systemViews[sysIdx] != null && _systemChangeFilterTables[sysIdx] != null;
        if (hasChangeFilter)
        {
            var list = BuildFilteredEntitySet(sysIdx);
            _systemEntityLists[sysIdx] = list;
            entities = list;
        }
        else if (_systemViews[sysIdx] != null)
        {
            var list = BuildFullViewEntitySet(sysIdx);
            _systemEntityLists[sysIdx] = list;
            entities = list;
        }
        else
        {
            entities = PooledEntityList.Empty;
        }

        // #198: Record entity counts into per-system telemetry
        ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);
        var entityCount = entities is PooledEntityList pel ? pel.Count : 0;
        metrics.EntitiesProcessed = entityCount;
        if (hasChangeFilter && _systemViews[sysIdx] != null)
        {
            metrics.EntitiesSkippedByChangeFilter = _systemViews[sysIdx].Count - entityCount;
        }

        var sys = Scheduler.Systems[sysIdx];
        float amortizedDt = sys.CellAmortize > 0 ? _currentDeltaTime * sys.CellAmortize : _currentDeltaTime;
        return new TickContext
        {
            TickNumber = Scheduler.CurrentTickNumber,
            DeltaTime = _currentDeltaTime,
            AmortizedDeltaTime = amortizedDt,
            Transaction = tx,
            CreateSideTransaction = _createSideTxDelegate,
            Entities = entities,
            ConsumedQueues = _systemConsumedQueues[sysIdx],
            TierBudgetMetrics = _previousTickMetrics,
            SpatialGrid = new SpatialGridAccessor(Engine?.Realm0Grid),
            Subscriptions = _subscriptionsRuntime?.CommandsFor(workerId),
            WorkerId = workerId,
            // Single-invocation system: one chunk, index 0. Left at the default 0 before #860, which made the documented slicing formula
            // (start = ChunkIndex * len / ChunkCount) divide by zero for any non-chunked system that used it.
            ChunkCount = 1
        };
    }

    private void OnSystemEndInternal(int sysIdx, bool success)
    {
        EmitSchedulerSystemArchetypeIfActive(sysIdx);

        // P7 follow-up of umbrella #342 — emit one QueryPlan span per (system, tick) bracketing the system body.
        // Without this, pull-mode/system-input views (the common AntHill case: tx.Query<Ant>().ToView()) never produce QueryPlan events at consumption time,
        // and the Workbench Execution Inspector stays empty.
        var planStart = _systemQueryPlanStartTicks[sysIdx];
        if (planStart != 0)
        {
            if (_systemViews[sysIdx] != null && TelemetryConfig.QueryActive)
            {
                _systemViews[sysIdx].EmitPerTickQueryPlan(planStart, Stopwatch.GetTimestamp(), (ushort)sysIdx);
            }
            _systemQueryPlanStartTicks[sysIdx] = 0;
        }

        var tx = _systemTransactions[sysIdx];
        if (tx == null)
        {
            return;
        }

        try
        {
            if (success)
            {
                tx.Commit();
            }
            else
            {
                tx.Rollback();
            }
        }
        finally
        {
            tx.Dispose();
            _systemTransactions[sysIdx] = null;
            TyphonEvent.EmitRuntimeTransactionLifecycleEnd(success);

            // #197: Return pooled entity list to ArrayPool
            _systemEntityLists[sysIdx].Return();
            _systemEntityLists[sysIdx] = default;
        }
    }

    // Workbench Data Flow module (#327): per-(system, archetype) entity-touch rollup.
    // Fires once per system per tick from OnSystemEndInternal, after all parallel-query chunks have completed.
    // Skip when (a) the gate is off, (b) the system isn't bound to a single archetype (callbacks, multi-archetype scans),
    // or (c) the system did no useful work this tick (skipped or zero entities).
    private void EmitSchedulerSystemArchetypeIfActive(int sysIdx)
    {
        if (!TelemetryConfig.SchedulerArchetypeTouchesActive)
        {
            return;
        }

        var archetypeId = _systemArchetypeIds[sysIdx];
        if (archetypeId == ushort.MaxValue)
        {
            return;
        }

        ref var metrics = ref Scheduler.GetCurrentSystemMetrics(sysIdx);
        var entityCount = metrics.EntitiesProcessed;
        if (entityCount <= 0)
        {
            return;
        }

        var startTs = metrics.FirstChunkGrabTick;
        var endTs = metrics.LastChunkDoneTick;
        if (startTs <= 0 || endTs <= 0 || endTs < startTs)
        {
            return;
        }

        var chunkCount = Scheduler.Systems[sysIdx].TotalChunks;
        TyphonEvent.EmitSchedulerSystemArchetype(startTs, endTs, sysIdx, archetypeId, entityCount, chunkCount);
    }
}
