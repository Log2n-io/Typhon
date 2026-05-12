using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Typhon.Profiler;

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
    private readonly RuntimeOptions _options;
    private readonly ILogger _logger;

    // Tick-level UoW (created at tick start, disposed at tick end)
    private UnitOfWork _currentUow;

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
    private readonly PartitionEntityView[][] _partitionViews;      // Per-system per-worker partition views [sysIdx][chunkIdx]

    // Issue #231: per-system cluster-id partition source for tier-filtered dispatch. Non-null only when the system has a tier filter AND has a cluster state.
    // For non-amortized systems this points DIRECTLY at the per-archetype TierClusterIndex's per-tier buffer (zero-copy). For amortized systems
    // (cellAmortize > 0) it points at the per-system grow-on-demand buffer in _systemAmortizationBuffers, which contains only this tick's bucket.
    // Refreshed each tick inside OnParallelQueryPrepare. Decoupling these into per-system slots avoids the BUG-2 race on shared state.
    private readonly int[][] _systemTierClusterIds;
    private readonly int[] _systemTierClusterCount;
    // Issue #231 BUG-2 fix: per-system grow-on-demand buffer for amortized cluster ids. Owned exclusively by OnParallelQueryPrepare → ExecuteChunkWith*. Reused
    // across ticks; doubles on overflow. Null until the first amortized dispatch.
    private readonly int[][] _systemAmortizationBuffers;
    // Issue #231: per-system cluster-range entity view, allocated lazily the first time a tier-filtered system runs Path 1 (full non-versioned). Reused across
    // ticks. [sysIdx][workerIdx]. Null slot = not allocated yet.
    private readonly ClusterRangeEntityView[][] _tierRangeViews;

    // Issue #234: checkerboard two-phase dispatch. Phase tracking + Red/Black cluster buffers per system.
    // _checkerboardPhase: 0 = not checkerboard or reset, 1 = Red (phase A active), 2 = Black (phase B active).
    private readonly int[] _checkerboardPhase;
    private readonly int[][] _checkerboardRedIds;
    private readonly int[] _checkerboardRedCount;
    private readonly int[][] _checkerboardBlackIds;
    private readonly int[] _checkerboardBlackCount;

    // Cached delegate — avoids per-TickContext allocation from method group conversion
    private readonly Func<DurabilityMode, Transaction> _createSideTxDelegate;

    // First-tick flag
    private bool _firstTickExecuted;

    // Issue #234: per-tier budget metrics. Computed at tick end, exposed as _previousTickMetrics on the next tick's TickContext.
    private TierBudgetMetrics _previousTickMetrics;

    // DeltaTime tracking
    private long _previousTickTimestamp;
    private float _currentDeltaTime;

    // ═══════════════════════════════════════════════════════════════
    // Subscription server
    // ═══════════════════════════════════════════════════════════════

    private readonly PublishedViewRegistry _publishedViewRegistry = new();
    private readonly ClientConnectionManager _clientConnectionManager = new();
    private SubscriptionOutputPhase _subscriptionOutputPhase;
    private TcpSubscriptionServer _tcpServer;

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

    /// <summary>Telemetry ring buffer for diagnostic inspection.</summary>
    public TickTelemetryRing Telemetry => Scheduler.Telemetry;

    /// <summary>Number of ticks executed so far.</summary>
    public long CurrentTickNumber => Scheduler.CurrentTickNumber;

    /// <summary>Current overload response level.</summary>
    public OverloadLevel CurrentOverloadLevel => Scheduler.CurrentOverloadLevel;

    /// <summary>Static DAG system definitions (name, type, priority, dependencies).</summary>
    public SystemDefinition[] Systems => Scheduler.Systems;

    /// <summary>
    /// Phase order from <see cref="RuntimeOptions.Phases"/>. Returned as a fresh string array so
    /// callers (notably profiler exporters that ship the list to the Workbench) can hand it
    /// straight to <c>ProfilerSessionMetadata</c> without having to know about the
    /// <see cref="Phase"/> struct or the underlying options object.
    /// </summary>
    public string[] PhaseNames
    {
        get
        {
            var phases = _options.Phases;
            var names = new string[phases.Length];
            for (var i = 0; i < phases.Length; i++) names[i] = phases[i].Name;
            return names;
        }
    }

    /// <summary>Fires when overload reaches <see cref="OverloadLevel.PlayerShedding"/>. Game code decides what to do (migrate, disconnect, split).</summary>
    public event Action<TyphonRuntime> OnCriticalOverload;

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
    public static TyphonRuntime Create(DatabaseEngine engine, Action<RuntimeSchedule> configure, RuntimeOptions options = null, IResource parent = null,
        ILogger logger = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(configure);

        var opts = options ?? new RuntimeOptions();
        var schedule = RuntimeSchedule.Create(opts);
        configure(schedule);

        var resourceParent = parent ?? engine.Parent; // DatabaseEngine registers under DataEngine node
        var scheduler = schedule.Build(resourceParent, logger);

        return new TyphonRuntime(engine, scheduler, opts, logger);
    }

    private TyphonRuntime(DatabaseEngine engine, DagScheduler scheduler, RuntimeOptions options, ILogger logger)
    {
        Engine = engine;
        Scheduler = scheduler;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _systemTransactions = new Transaction[scheduler.SystemCount];
        _systemViews = new ViewBase[scheduler.SystemCount];
        _systemQueryPlanStartTicks = new long[scheduler.SystemCount];
        _systemChangeFilterTables = new ComponentTable[scheduler.SystemCount][];
        _systemClusterStates = new ArchetypeClusterState[scheduler.SystemCount];
        _systemArchetypeIds = new ushort[scheduler.SystemCount];
        Array.Fill(_systemArchetypeIds, ushort.MaxValue);
        _systemEntityLists = new PooledEntityList[scheduler.SystemCount];
        _systemConsumedQueues = new EventQueueBase[scheduler.SystemCount][];
        _parallelEntityLists = new PooledEntityList[scheduler.SystemCount];
        _multiTableFilterSets = new HashMap<long>[scheduler.SystemCount];
        _parallelAccessors = new PointInTimeAccessor[scheduler.SystemCount];
        _partitionViews = new PartitionEntityView[scheduler.SystemCount][];
        _systemTierClusterIds = new int[scheduler.SystemCount][];
        _systemTierClusterCount = new int[scheduler.SystemCount];
        _systemAmortizationBuffers = new int[scheduler.SystemCount][];
        _tierRangeViews = new ClusterRangeEntityView[scheduler.SystemCount][];
        _checkerboardPhase = new int[scheduler.SystemCount];
        _checkerboardRedIds = new int[scheduler.SystemCount][];
        _checkerboardRedCount = new int[scheduler.SystemCount];
        _checkerboardBlackIds = new int[scheduler.SystemCount][];
        _checkerboardBlackCount = new int[scheduler.SystemCount];
        _createSideTxDelegate = CreateSideTransactionInternal;

        ResolveChangeFilters(scheduler);

        // Initialize subscription infrastructure
        var subOptions = options.SubscriptionServer ?? new SubscriptionServerOptions();
        _subscriptionOutputPhase = new SubscriptionOutputPhase(engine, _publishedViewRegistry, _clientConnectionManager, subOptions, logger);

        // Wire tick lifecycle hooks
        Scheduler.TickStartCallback = OnTickStartInternal;
        Scheduler.TickEndCallback = OnTickEndInternal;
        Scheduler.SystemStartCallback = OnSystemStartInternal;
        Scheduler.SystemEndCallback = OnSystemEndInternal;
        Scheduler.ParallelQueryPrepareCallback = OnParallelQueryPrepare;
        Scheduler.ParallelQueryChunkCallback = OnParallelQueryChunk;
        Scheduler.ParallelQueryCleanupCallback = OnParallelQueryCleanup;

        // Wire subscription telemetry enrichment
        Scheduler.TelemetryEnrichCallback = (ref t) =>
        {
            if (_subscriptionOutputPhase != null)
            {
                t.OutputPhaseMs = _subscriptionOutputPhase.LastOutputPhaseMs;
                t.SubscriptionDeltasPushed = _subscriptionOutputPhase.LastDeltasPushed;
                t.SubscriptionOverflowCount = _subscriptionOutputPhase.LastOverflowCount;
            }
        };

        // Wire profiler gauge snapshot — only when gauges are enabled, so the callback pointer stays null otherwise and the scheduler's
        // null-check is the only cost. See TyphonRuntime.GaugeSnapshot.cs for the collection + emit implementation.
        if (TelemetryConfig.ProfilerGaugesActive)
        {
            Scheduler.GaugeSnapshotCallback = EmitGaugeSnapshotFromScheduler;
        }

        Scheduler.OnCriticalOverloadCallback = () => OnCriticalOverload?.Invoke(this);
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Starts the scheduler (worker threads + tick driver) and the subscription server (if configured).</summary>
    public void Start()
    {
        Scheduler.Start();

        // Start TCP subscription server if a port is configured
        var subOptions = _options.SubscriptionServer;
        if (subOptions != null && subOptions.Port > 0)
        {
            _tcpServer = new TcpSubscriptionServer(subOptions, _clientConnectionManager, _subscriptionOutputPhase, _logger);
            _tcpServer.Start();
        }
    }

    /// <summary>
    /// Gracefully shuts down the runtime. Stops the subscription server, fires <see cref="OnShutdown"/>, then stops the scheduler.
    /// </summary>
    public void Shutdown()
    {
        // Stop accepting new connections and flush remaining data
        _tcpServer?.Shutdown();

        // Execute OnShutdown callback with a dedicated transaction
        if (OnShutdown != null)
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
                SpatialGrid = new SpatialGridAccessor(Engine?.SpatialGrid)
            };
            OnShutdown.Invoke(ctx);
            tx.Commit();
        }

        Scheduler.Shutdown();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _tcpServer?.Dispose();
        Scheduler.Dispose();

        // Dispose per-system PTAs AFTER scheduler — workers must be fully stopped
        // before we flush their per-thread EntityAccessors' ChangeSets.
        for (var i = 0; i < _parallelAccessors.Length; i++)
        {
            _parallelAccessors[i]?.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Subscription API
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Publish a shared View for client subscriptions. All subscribers see the same data; the delta is serialized once and memcpy'd.
    /// </summary>
    /// <remarks>
    /// <para>The View must be a dedicated instance — it must NOT be used as a system input. Published Views are refreshed only during
    /// the Output phase; using the same View as system input would consume ring buffer entries needed by subscriptions.</para>
    /// </remarks>
    /// <param name="name">Human-readable name clients use to identify this subscription.</param>
    /// <param name="view">A dedicated ViewBase instance for subscriptions.</param>
    /// <param name="priority">Subscription priority for overload throttling.</param>
    /// <returns>The published View handle.</returns>
    public PublishedView PublishView(string name, ViewBase view, SubscriptionPriority priority = SubscriptionPriority.Normal) =>
        _publishedViewRegistry.RegisterShared(name, view, priority);

    /// <summary>
    /// Publish a per-client View factory. A new View is created per subscriber, parameterized by <see cref="ClientContext"/>.
    /// </summary>
    /// <param name="name">Human-readable name clients use to identify this subscription.</param>
    /// <param name="factory">Factory that creates a View for each subscribing client.</param>
    /// <param name="priority">Subscription priority for overload throttling.</param>
    /// <returns>The published View handle.</returns>
    public PublishedView PublishView(string name, Func<ClientContext, ViewBase> factory, SubscriptionPriority priority = SubscriptionPriority.Normal) =>
        _publishedViewRegistry.RegisterPerClient(name, factory, priority);

    /// <summary>
    /// Set a client's subscription set. Replaces the previous set atomically. The transition is applied during the next tick's Output phase.
    /// Looks up the connection by <see cref="ClientContext.ConnectionId"/> — the public client identity. If the connection has been
    /// dropped between the caller obtaining the context and this call, the request is silently ignored (the next tick will see no
    /// pending change for a disposed client anyway).
    /// </summary>
    /// <remarks>If called multiple times within a tick, the last call wins.</remarks>
    public void SetSubscriptions(ClientContext client, params PublishedView[] views)
    {
        ArgumentNullException.ThrowIfNull(client);
        var connection = _clientConnectionManager.Get(client.ConnectionId);
        connection?.SetSubscriptions(views);
    }

    /// <summary>The published View registry (for diagnostics and testing).</summary>
    public PublishedViewRegistry PublishedViews => _publishedViewRegistry;

    /// <summary>The client connection manager (for diagnostics and testing).</summary>
    internal ClientConnectionManager ClientConnections => _clientConnectionManager;

    // ═══════════════════════════════════════════════════════════════
    // Side-transaction factory
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a side-transaction with the specified durability mode.
    /// The caller owns the returned Transaction and must Commit + Dispose it.
    /// Side-transactions are NOT visible to the current tick's main Transactions (snapshot isolation).
    /// </summary>
    public Transaction CreateSideTransaction(DurabilityMode durability = DurabilityMode.Immediate) => Engine.CreateQuickTransaction(durability);

    private Transaction CreateSideTransactionInternal(DurabilityMode durability) => Engine.CreateQuickTransaction(durability);

    // ═══════════════════════════════════════════════════════════════
    // #197: Change filter resolution
    // ═══════════════════════════════════════════════════════════════

    private void ResolveChangeFilters(DagScheduler scheduler)
    {
        for (var i = 0; i < scheduler.SystemCount; i++)
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

                if (_systemViews[i].IsPublished)
                {
                    throw new InvalidOperationException(
                        $"System '{sys.Name}': Input View (ViewId={_systemViews[i].ViewId}) is already published for subscriptions. " +
                        "Published Views must be separate instances from system input Views. Create a new View with the same query.");
                }

                _systemViews[i].IsSystemInput = true;

                // Detect cluster-eligible archetype for parallel cluster dispatch.
                // Checks if any entity already in the view belongs to a cluster-eligible archetype.
                if (sys.IsParallelQuery && Engine != null)
                {
                    foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
                    {
                        if (meta.IsClusterEligible && meta.ArchetypeId < Engine._archetypeStates.Length)
                        {
                            var es = Engine._archetypeStates[meta.ArchetypeId];
                            if (es?.ClusterState is { ActiveClusterCount: > 0 })
                            {
                                _systemClusterStates[i] = es.ClusterState;
                                _systemArchetypeIds[i] = meta.ArchetypeId;
                                break;
                            }
                        }
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

                    if (ct.StorageMode == Schema.Definition.StorageMode.Versioned)
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
        bool tierFiltered = effectiveTier != SimTier.All && Engine.SpatialGrid != null;

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
                    for (int wordIdx = 0; wordIdx < snapshot.Length; wordIdx++)
                    {
                        long word = snapshot[wordIdx];
                        while (word != 0)
                        {
                            int bit = BitOperations.TrailingZeroCount((ulong)word);
                            word &= word - 1;

                            byte* clusterBase = clusterAccessor.GetChunkAddress(wordIdx);
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
                for (int wordIdx = 0; wordIdx < snapshot.Length; wordIdx++)
                {
                    long word = snapshot[wordIdx];
                    while (word != 0)
                    {
                        int bit = BitOperations.TrailingZeroCount((ulong)word);
                        word &= word - 1;

                        byte* clusterBase = clusterAccessor.GetChunkAddress(wordIdx);
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

        var sys = Scheduler.Systems[sysIdx];
        var effectiveTier = (SimTier)((byte)sys.TierFilter & (byte)view.TierFilter);
        var cs = _systemClusterStates[sysIdx];

        // Detect a system tier filter and a view tier filter that are mutually exclusive (e.g. system declares Tier0 and the view was created via
        // WithTier(Tier1)). Their bit-AND is None, which would otherwise silently materialize an empty entity set.
        if (effectiveTier == SimTier.None && sys.TierFilter != SimTier.None && view.TierFilter != SimTier.None)
        {
            throw new InvalidOperationException(
                $"System '{sys.Name}': system tier filter '{sys.TierFilter}' and view tier filter '{view.TierFilter}' have no overlap. " +
                "Their intersection is SimTier.None, which would dispatch zero entities. Make the filters compatible " +
                "(e.g. system Tier0 + view Near, where view's tier set is a superset of the system's).");
        }

        if (effectiveTier != SimTier.All && cs != null && Engine != null && Engine.SpatialGrid != null)
        {
            return BuildTierScopedEntityList(cs, effectiveTier, view);
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
    /// Materialize an entity list by walking only the tier's clusters (issue #231 Q9 pattern). Each cluster's occupancy bitmap is decoded via TZCNT to emit
    /// entity ids in cluster order. Cost is proportional to the tier's actual entity count (popcount-summed), not an upper bound or the full view.
    /// </summary>
    private PooledEntityList BuildTierScopedEntityList(ArchetypeClusterState cs, SimTier tier, ViewBase view)
    {
        // TierIndex is pre-created in ResolveChangeFilters and rebuilt at TickStart. Here we only READ. The fallback path (TierIndex == null)
        // covers archetypes that gained a tier-using system after construction — safe because BuildTierScopedEntityList runs from a single-threaded context
        // (OnSystemStartInternal or PrepareVersionedFallback, both of which are serialized by the scheduler).
        if (cs.TierIndex == null)
        {
            cs.TierIndex = new TierClusterIndex();
        }
        cs.TierIndex.RebuildIfStale(Engine.SpatialGrid, cs);
        var tierClusters = cs.TierIndex.GetClustersArray(tier, out int tierCount);
        if (tierCount == 0)
        {
            return PooledEntityList.Empty;
        }

        // Support pure-Transient archetypes (ClusterSegment == null) by falling back to TransientSegment.
        // Layout.EntityIdsOffset is the same in both stores — chunk ids are synchronized via lockstep allocation.
        if (cs.ClusterSegment != null)
        {
            return BuildTierScopedEntityListPersistent(cs, view, tierClusters, tierCount);
        }
        if (cs.TransientSegment != null)
        {
            return BuildTierScopedEntityListTransient(cs, view, tierClusters, tierCount);
        }
        return PooledEntityList.Empty;
    }

    private unsafe PooledEntityList BuildTierScopedEntityListPersistent(ArchetypeClusterState cs, ViewBase view, int[] tierClusters, int tierCount)
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
            if (exactCount == 0)
            {
                return PooledEntityList.Empty;
            }

            var list = PooledEntityList.Rent(exactCount);
            var span = list.AsSpan();
            int count = 0;
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

            if (count == 0)
            {
                list.Return();
                return PooledEntityList.Empty;
            }
            return new PooledEntityList(list.BackingArray, count);
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private unsafe PooledEntityList BuildTierScopedEntityListTransient(ArchetypeClusterState cs, ViewBase view, int[] tierClusters, int tierCount)
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
            if (exactCount == 0)
            {
                return PooledEntityList.Empty;
            }

            var list = PooledEntityList.Rent(exactCount);
            var span = list.AsSpan();
            int count = 0;
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

            if (count == 0)
            {
                list.Return();
                return PooledEntityList.Empty;
            }
            return new PooledEntityList(list.BackingArray, count);
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
    /// Prepare phase: selects the dispatch path based on WritesVersioned and change filter presence.
    /// For non-Versioned systems, creates/advances a long-lived PointInTimeAccessor.
    /// For the full non-Versioned path (Path 1), NO entity list is materialized — O(1).
    /// </summary>
    private int OnParallelQueryPrepare(int sysIdx)
    {
        var sys = Scheduler.Systems[sysIdx];
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

        // Issue #231: read the per-archetype tier cluster list. The rebuild itself was hoisted to BuildTierIndexesAtTickStart (runs single-threaded at
        // TickStart, before any parallel system dispatch). Here we only READ the prepared per-tier buffer and, if amortized, slice it into a per-system bucket.
        _systemTierClusterIds[sysIdx] = null;
        _systemTierClusterCount[sysIdx] = 0;
        if (sys.TierFilter != SimTier.All)
        {
            var cs = _systemClusterStates[sysIdx];
            if (cs != null && cs.TierIndex != null)
            {
                var tierArr = cs.TierIndex.GetClustersArray(sys.TierFilter, out int tierCnt);
                if (sys.CellAmortize > 0)
                {
                    // Per-system amortization bucket: stride the tier list by `cellAmortize`, starting at `tickNumber % cellAmortize`.
                    // Index-based modulo gives perfectly uniform distribution regardless of cell-key encoding (Morton vs row-major). The bucket lives
                    // in this system's own buffer — no shared mutable state.
                    long tick = Scheduler.CurrentTickNumber;
                    int amortize = sys.CellAmortize;
                    int startOffset = (int)((ulong)tick % (uint)amortize);
                    int bucketCount = tierCnt > startOffset ? (tierCnt - startOffset + amortize - 1) / amortize : 0;
                    var buf = _systemAmortizationBuffers[sysIdx];
                    if (buf == null || buf.Length < Math.Max(1, bucketCount))
                    {
                        buf = new int[Math.Max(16, bucketCount)];
                        _systemAmortizationBuffers[sysIdx] = buf;
                    }
                    int written = 0;
                    for (int i = startOffset; i < tierCnt; i += amortize)
                    {
                        buf[written++] = tierArr[i];
                    }
                    _systemTierClusterIds[sysIdx] = buf;
                    _systemTierClusterCount[sysIdx] = written;
                }
                else
                {
                    // Direct zero-copy reference into the TierClusterIndex buffer. Safe because the rebuild was already done at TickStart and won't run again
                    // until next TickStart (after all parallel systems for this tick have finished).
                    _systemTierClusterIds[sysIdx] = tierArr;
                    _systemTierClusterCount[sysIdx] = tierCnt;
                }
            }
        }

        // Issue #233: dormancy filter — remove sleeping clusters from the dispatch list.
        // Handles both tier-filtered and non-tier-filtered systems. When SleepingClusterCount == 0 this block is skipped (zero overhead).
        {
            var cs = _systemClusterStates[sysIdx];
            if (cs?.SleepingClusterCount > 0 && cs.SleepStates != null)
            {
                var srcIds = _systemTierClusterIds[sysIdx];
                int srcCount = _systemTierClusterCount[sysIdx];

                if (srcIds == null && sys.TierFilter == SimTier.All)
                {
                    // Non-tier-filtered system with sleeping clusters: "promote" to use a filtered copy of ActiveClusterIds
                    // so the tier-filtered dispatch path in ExecuteChunkWithAccessor handles it.
                    srcIds = cs.ActiveClusterIds;
                    srcCount = cs.ActiveClusterCount;
                }

                if (srcIds != null)
                {
                    // Always filter into the per-system amortization buffer (reusable, grows on demand).
                    // When the source IS the amortization buffer (amortized tier), this filters in-place (safe: we only compact, never expand).
                    var buf = _systemAmortizationBuffers[sysIdx];
                    bool inPlace = ReferenceEquals(buf, srcIds);
                    if (!inPlace && (buf == null || buf.Length < srcCount))
                    {
                        buf = new int[Math.Max(16, srcCount)];
                        _systemAmortizationBuffers[sysIdx] = buf;
                    }

                    int written = 0;
                    var sleepStates = cs.SleepStates;
                    for (int i = 0; i < srcCount; i++)
                    {
                        int chunkId = srcIds[i];
                        if (chunkId >= sleepStates.Length || sleepStates[chunkId] != ClusterSleepState.Sleeping)
                        {
                            buf[written++] = chunkId;
                        }
                    }

                    _systemTierClusterIds[sysIdx] = buf;
                    _systemTierClusterCount[sysIdx] = written;
                }
            }
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
                        _systemTierClusterIds[sysIdx] = cs2.ActiveClusterIds;
                        _systemTierClusterCount[sysIdx] = cs2.ActiveClusterCount;
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
                var sys = Scheduler.Systems[sysIdx];
                var entityList = BuildTierScopedEntityList(cs, sys.TierFilter, view);
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

    /// <summary>Paths 3 & 4: Versioned fallback — materialize entity list (same as original path).</summary>
    private int PrepareVersionedFallback(int sysIdx, bool hasChangeFilter)
    {
        PooledEntityList entityList;
        if (hasChangeFilter)
        {
            entityList = BuildFilteredEntitySet(sysIdx);
        }
        else if (_systemViews[sysIdx] != null)
        {
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

    private int ComputeChunkCount(int entityCount, int sysIdx)
    {
        var workerCount = Scheduler.WorkerCount;
        var minChunkSize = _options.ParallelQueryMinChunkSize;
        var maxChunks = Math.Max(1, (entityCount + minChunkSize - 1) / minChunkSize);

        // Per-system oversubscription: lift the workerCount cap by ChunksPerWorker (default 1.0 = no change).
        // Round-to-nearest so 1.5 × 16 = 24 exactly; small bumps like 1.1 × 16 = 17.6 → 18.
        var chunksPerWorker = Scheduler.Systems[sysIdx].ChunksPerWorker;
        var workerCap = Math.Max(1, (int)MathF.Round(workerCount * chunksPerWorker));
        return Math.Min(workerCap, maxChunks);
    }

    /// <summary>
    /// Chunk execution: dispatches to the appropriate path based on WritesVersioned.
    /// Non-Versioned: uses shared PointInTimeAccessor (no per-chunk Transaction).
    /// Versioned: creates a per-chunk Transaction (original fallback path).
    /// </summary>
    private void OnParallelQueryChunk(int sysIdx, int chunkIndex, int totalChunks, int workerId)
    {
        if (Scheduler.Systems[sysIdx].WritesVersioned)
        {
            ExecuteChunkWithTransaction(sysIdx, chunkIndex, totalChunks);
            return;
        }

        ExecuteChunkWithAccessor(sysIdx, chunkIndex, totalChunks, workerId);
    }

    /// <summary>Paths 1 & 2: Non-Versioned chunk execution with per-worker EntityAccessor from per-system PTA.</summary>
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

            // ClusterIds: tier list when present, otherwise the archetype's ActiveClusterIds. Game systems iterating via
            // ctx.Accessor.GetClusterEnumerator(ctx.ClusterIds, ...) still get the correct cluster set.
            if (tierIds != null)
            {
                int tierCount = _systemTierClusterCount[sysIdx];
                var tierBase = tierCount / totalChunks;
                var tierRem = tierCount % totalChunks;
                clusterStart = chunkIndex * tierBase + Math.Min(chunkIndex, tierRem);
                clusterEnd = clusterStart + tierBase + (chunkIndex < tierRem ? 1 : 0);
                clusterIdArray = tierIds;
            }
            else
            {
                var cs = _systemClusterStates[sysIdx];
                if (cs != null)
                {
                    var totalClusters = cs.ActiveClusterCount;
                    var cBase = totalClusters / totalChunks;
                    var cRemainder = totalClusters % totalChunks;
                    clusterStart = chunkIndex * cBase + Math.Min(chunkIndex, cRemainder);
                    clusterEnd = clusterStart + cBase + (chunkIndex < cRemainder ? 1 : 0);
                    clusterIdArray = cs.ActiveClusterIds;
                }
            }
        }
        else if (tierIds != null)
        {
            // Tier-filtered, no change filter: walk the tier's clusters via ClusterRangeEntityView.
            int tierCount = _systemTierClusterCount[sysIdx];
            var tierBase = tierCount / totalChunks;
            var tierRem = tierCount % totalChunks;
            clusterStart = chunkIndex * tierBase + Math.Min(chunkIndex, tierRem);
            clusterEnd = clusterStart + tierBase + (chunkIndex < tierRem ? 1 : 0);
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
                var totalClusters = cs.ActiveClusterCount;
                var cBase = totalClusters / totalChunks;
                var cRemainder = totalClusters % totalChunks;
                clusterStart = chunkIndex * cBase + Math.Min(chunkIndex, cRemainder);
                clusterEnd = clusterStart + cBase + (chunkIndex < cRemainder ? 1 : 0);
                clusterIdArray = cs.ActiveClusterIds;
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
            SpatialGrid = new SpatialGridAccessor(Engine?.SpatialGrid),
            WorkerId = workerId
        };

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
    /// Red = clusters in cells where <c>(cellX + cellY) % 2 == 0</c>, Black = the rest. Reads <see cref="_systemTierClusterIds"/>
    /// + <see cref="_systemTierClusterCount"/> as input, writes to the per-system Red/Black buffers.
    /// </summary>
    private void SplitCheckerboardClusters(int sysIdx)
    {
        var srcIds = _systemTierClusterIds[sysIdx];
        int srcCount = _systemTierClusterCount[sysIdx];
        var cs = _systemClusterStates[sysIdx];
        var grid = Engine?.SpatialGrid;

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
            var (x, y) = grid.CellKeyToCoords(cellKey);
            if ((x + y) % 2 == 0)
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

    /// <summary>Paths 3 & 4: Versioned fallback — per-chunk Transaction (original path).</summary>
    private void ExecuteChunkWithTransaction(int sysIdx, int chunkIndex, int totalChunks)
    {
        var fullList = _parallelEntityLists[sysIdx];
        var totalEntities = fullList.Count;

        // Balanced partitioning: first `remainder` chunks get one extra entity
        var baseSize = totalEntities / totalChunks;
        var remainder = totalEntities % totalChunks;
        var start = chunkIndex * baseSize + Math.Min(chunkIndex, remainder);
        var count = baseSize + (chunkIndex < remainder ? 1 : 0);

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
            int[] clusterIdArray = null;
            var tierIds = _systemTierClusterIds[sysIdx];
            if (tierIds != null)
            {
                int tierCount = _systemTierClusterCount[sysIdx];
                var tierBase = tierCount / totalChunks;
                var tierRem = tierCount % totalChunks;
                clusterStart = chunkIndex * tierBase + Math.Min(chunkIndex, tierRem);
                clusterEnd = clusterStart + tierBase + (chunkIndex < tierRem ? 1 : 0);
                clusterIdArray = tierIds;
            }
            else
            {
                var cs = _systemClusterStates[sysIdx];
                if (cs != null)
                {
                    var totalClusters = cs.ActiveClusterCount;
                    var cBase = totalClusters / totalChunks;
                    var cRemainder = totalClusters % totalChunks;
                    clusterStart = chunkIndex * cBase + Math.Min(chunkIndex, cRemainder);
                    clusterEnd = clusterStart + cBase + (chunkIndex < cRemainder ? 1 : 0);
                    clusterIdArray = cs.ActiveClusterIds;
                }
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
                SpatialGrid = new SpatialGridAccessor(Engine?.SpatialGrid)
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

        // Create UoW for this tick (Deferred — batch all system commits, single WAL flush at end)
        _currentUow = Engine.CreateUnitOfWork();
        TyphonEvent.EmitRuntimePhaseUoWCreate(scheduler.CurrentTickNumber);

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
                SpatialGrid = new SpatialGridAccessor(Engine?.SpatialGrid)
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
    /// Walk every system that declares a tier filter, and rebuild the per-archetype <see cref="TierClusterIndex"/> once per tick on the scheduler thread.
    /// The version-skip in <see cref="TierClusterIndex.RebuildIfStale"/> means redundant calls (multiple systems on the same archetype) short-circuit on a
    /// two-int compare. The actual rebuild only runs when the grid tier version OR the archetype cluster set has changed since the previous tick.
    /// </summary>
    private void BuildTierIndexesAtTickStart()
    {
        var grid = Engine?.SpatialGrid;
        if (grid == null)
        {
            return;
        }

        // Issue #233: transition WakePending → Active for all archetypes BEFORE rebuilding tier indexes.
        // This ensures woken clusters appear in this tick's per-tier lists. The TransitionWakePendingToActive method is guarded by _lastWakeTransitionTick
        // so calling it for the same archetype via multiple systems is a no-op after the first call.
        long tick = Scheduler.CurrentTickNumber;
        for (int i = 0; i < Scheduler.SystemCount; i++)
        {
            var cs = _systemClusterStates[i];
            if (cs?.SleepStates != null)
            {
                cs.TransitionWakePendingToActive(tick);
            }
        }

        for (int i = 0; i < Scheduler.SystemCount; i++)
        {
            var sys = Scheduler.Systems[i];
            if (sys.TierFilter == SimTier.All)
            {
                continue;
            }

            var cs = _systemClusterStates[i];

            // Late-spawn recovery: if the archetype was empty when ResolveChangeFilters ran (construction time), _systemClusterStates[i] is null. Re-evaluate
            // now — entities may have been spawned between construction and the first tick (e.g. via OnFirstTick). This check runs once per tick per
            // tier-filtered system with a null slot; the inner archetype scan is O(registered archetypes) ≈ O(10), negligible.
            if (cs == null && sys.IsParallelQuery && sys.InputFactory != null)
            {
                foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
                {
                    if (meta.IsClusterEligible && meta.ArchetypeId < Engine._archetypeStates.Length)
                    {
                        var es = Engine._archetypeStates[meta.ArchetypeId];
                        if (es?.ClusterState is { ActiveClusterCount: > 0 })
                        {
                            cs = es.ClusterState;
                            cs.TierIndex ??= new TierClusterIndex();
                            _systemClusterStates[i] = cs;
                            break;
                        }
                    }
                }
            }

            if (cs == null || cs.TierIndex == null)
            {
                continue;
            }
            cs.TierIndex.RebuildIfStale(grid, cs);
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
        InspectorPhase(TickPhase.WriteTickFence, () => Engine.WriteTickFence(scheduler.CurrentTickNumber, _currentUow?.ChangeSet));

        // Flush the UoW to make all Deferred writes (including the tick fence publishes above) durable, then dispose. UoW.Flush in WAL mode calls
        // WalManager.RequestFlush + WaitForDurable(currentLsn), where currentLsn is captured at the moment of the call — so it includes every publish made
        // in WriteTickFence.
        InspectorPhase(TickPhase.UowFlush, () =>
        {
            try
            {
                _currentUow?.Flush();
            }
            finally
            {
                _currentUow?.Dispose();
                _currentUow = null;
                TyphonEvent.EmitRuntimePhaseUoWFlush(scheduler.CurrentTickNumber, 0);
            }
        });

        // Issue #234: compute per-tier budget metrics from this tick's system telemetry, for the next tick's TickContext.
        ComputeTierBudgetMetrics();

        // #199: Output phase — subscription deltas.
        // Runs AFTER WriteTickFence so that:
        //   1. Ring buffer has ALL entries (commit-time + shadow-time) for correct View membership
        //   2. PreviousTickDirtyBitmap has this tick's dirty chunks for Modified detection
        //   3. All state is quiescent (no concurrent writers)
        InspectorPhase(TickPhase.OutputPhase, () =>
        {
            using var subSpan = TyphonEvent.BeginRuntimeSubscriptionOutputExecute(
                scheduler.CurrentTickNumber, (byte)Scheduler.CurrentOverloadLevel);
            _subscriptionOutputPhase?.Execute(scheduler.CurrentTickNumber, Scheduler.CurrentOverloadLevel);
            // Stats fields (clientCount, viewsRefreshed, deltasPushed, overflowCount) populated when Phase 9 wires per-tick subscription metrics back from SubscriptionOutputPhase.
        });
    }

    /// <summary>
    /// Wraps a tick phase with paired profiler boundary events. When <see cref="TelemetryConfig.ProfilerActive"/> is false the JIT folds both
    /// Emit calls to no-ops — this method compiles to just <c>action()</c>.
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
        var metrics = new TierBudgetMetrics { BudgetMs = 1000f / _options.BaseTickRate };

        for (int i = 0; i < Scheduler.SystemCount; i++)
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

    private TickContext OnSystemStartInternal(int sysIdx)
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
            SpatialGrid = new SpatialGridAccessor(Engine?.SpatialGrid)
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
