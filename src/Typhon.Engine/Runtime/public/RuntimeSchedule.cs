using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Fluent builder for constructing a runtime schedule — the public API that game developers use to register systems and build a <see cref="DagScheduler"/>.
/// </summary>
/// <remarks>
/// <para>
/// Wraps <see cref="DagBuilder"/> with a developer-friendly interface that supports dependency declaration via <c>after:</c>/<c>afterAll:</c>,
/// <c>runIf:</c> predicates, typed event queues, and overload parameters.
/// </para>
/// <para>
/// Usage: <c>RuntimeSchedule.Create().CallbackSystem(...).PipelineSystem(...).Build(parent)</c>
/// </para>
/// </remarks>
[PublicAPI]
public sealed class RuntimeSchedule
{
    private readonly RuntimeOptions _options;
    private readonly List<SystemRegistration> _registrations = [];
    private readonly List<EventQueueBase> _eventQueues = [];
    private readonly Dictionary<string, List<EventQueueBase>> _produces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EventQueueBase>> _consumes = new(StringComparer.Ordinal);
    private bool _built;

    private RuntimeSchedule(RuntimeOptions options)
    {
        _options = options ?? new RuntimeOptions();
    }

    /// <summary>
    /// Creates a new runtime schedule builder.
    /// </summary>
    /// <param name="options">Runtime configuration. If null, defaults are used.</param>
    public static RuntimeSchedule Create(RuntimeOptions options = null) => new(options);

    // ═══════════════════════════════════════════════════════════════
    // System registration
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Registers a CallbackSystem — lightweight single-invocation, no entity input.
    /// </summary>
    /// <remarks>
    /// This lambda-style overload does NOT support RFC 07 access declarations (<c>Reads&lt;T&gt;</c>, <c>Writes&lt;T&gt;</c>, <c>Phase</c>, etc.).
    /// Systems registered here land in <see cref="RuntimeOptions.DefaultPhase"/> with an empty access descriptor — fine for systems
    /// that don't need conflict detection or auto-DAG. For declared access, use a class-based system and <see cref="Add(CallbackSystem)"/>.
    /// </remarks>
    public RuntimeSchedule CallbackSystem(string name, Action<TickContext> action, string after = null, string[] afterAll = null,
        SystemPriority priority = SystemPriority.Normal, Func<bool> runIf = null, int tickDivisor = 1, int throttledTickDivisor = 1, bool canShed = false)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(action);

        _registrations.Add(new SystemRegistration
        {
            Name = name,
            Type = SystemType.CallbackSystem,
            CallbackAction = action,
            Priority = priority,
            RunIf = runIf,
            After = after,
            AfterAll = afterAll,
            TickDivisor = tickDivisor,
            ThrottledTickDivisor = throttledTickDivisor,
            CanShed = canShed
        });
        return this;
    }

    /// <summary>
    /// Registers a QuerySystem — single-worker entity iteration.
    /// </summary>
    /// <remarks>
    /// This lambda-style overload does NOT support RFC 07 access declarations.
    /// See <see cref="CallbackSystem(string,Action{TickContext},string,string[],SystemPriority,Func{bool},int,int,bool)"/> for the same caveat —
    /// use <see cref="Add(QuerySystem)"/> with a class-based system for declared access.
    /// </remarks>
    public RuntimeSchedule QuerySystem(string name, Action<TickContext> action, string after = null, string[] afterAll = null,
        SystemPriority priority = SystemPriority.Normal, Func<bool> runIf = null, Func<ViewBase> input = null, Type[] changeFilter = null,
        int tickDivisor = 1, int throttledTickDivisor = 1, bool canShed = false, bool parallel = false, bool writesVersioned = false,
        SimTier tier = SimTier.All, int cellAmortize = 0, bool checkerboard = false)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(action);

        _registrations.Add(new SystemRegistration
        {
            Name = name,
            Type = SystemType.QuerySystem,
            CallbackAction = action,
            Priority = priority,
            RunIf = runIf,
            InputFactory = input,
            ChangeFilter = changeFilter,
            After = after,
            AfterAll = afterAll,
            TickDivisor = tickDivisor,
            ThrottledTickDivisor = throttledTickDivisor,
            CanShed = canShed,
            Parallel = parallel,
            WritesVersioned = writesVersioned,
            TierFilter = tier,
            CellAmortize = cellAmortize,
            Checkerboard = checkerboard
        });
        return this;
    }

    /// <summary>
    /// Registers a PipelineSystem — multi-worker chunk-parallel execution.
    /// </summary>
    /// <remarks>
    /// This lambda-style overload does NOT support RFC 07 access declarations. See the CallbackSystem lambda overload for the same caveat — declared access
    /// requires the class-based API.
    /// </remarks>
    public RuntimeSchedule PipelineSystem(string name, Action<int, int> chunkAction, int totalChunks, string after = null, string[] afterAll = null,
        SystemPriority priority = SystemPriority.Normal, Func<bool> runIf = null, Func<ViewBase> input = null, Type[] changeFilter = null,
        int tickDivisor = 1, int throttledTickDivisor = 1, bool canShed = false)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(chunkAction);

        _registrations.Add(new SystemRegistration
        {
            Name = name,
            Type = SystemType.PipelineSystem,
            PipelineChunkAction = chunkAction,
            TotalChunks = totalChunks,
            Priority = priority,
            RunIf = runIf,
            InputFactory = input,
            ChangeFilter = changeFilter,
            After = after,
            AfterAll = afterAll,
            TickDivisor = tickDivisor,
            ThrottledTickDivisor = throttledTickDivisor,
            CanShed = canShed
        });
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // Class-based system registration
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Register a class-based CallbackSystem.</summary>
    public RuntimeSchedule Add(CallbackSystem system)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(system);

        var builder = new SystemBuilder();
        Engine.CallbackSystem.InvokeConfigure(system, builder);
        ValidateBuilderName(builder, nameof(Typhon.Engine.CallbackSystem));

        _registrations.Add(new SystemRegistration
        {
            Name = builder._name,
            Type = SystemType.CallbackSystem,
            CallbackAction = ctx => Engine.CallbackSystem.InvokeExecute(system, ctx),
            Priority = builder._priority,
            RunIf = builder._runIf,
            After = builder._after,
            AfterAll = builder._afterAll,
            TickDivisor = builder._tickDivisor,
            ThrottledTickDivisor = builder._throttledTickDivisor,
            CanShed = builder._canShed,
            InputFactory = builder._inputFactory,
            ChangeFilter = builder._changeFilter,
            Parallel = builder._parallel,
            WritesVersioned = builder._writesVersioned,
            Phase = builder._phase,
            PhaseSet = builder._phaseSet,
            Before = builder._before,
            Access = builder._access
        });
        return this;
    }

    /// <summary>Register a class-based QuerySystem.</summary>
    public RuntimeSchedule Add(QuerySystem system)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(system);

        var builder = new SystemBuilder();
        Engine.QuerySystem.InvokeConfigure(system, builder);
        ValidateBuilderName(builder, nameof(Typhon.Engine.QuerySystem));

        _registrations.Add(new SystemRegistration
        {
            Name = builder._name,
            Type = SystemType.QuerySystem,
            CallbackAction = ctx => Engine.QuerySystem.InvokeExecute(system, ctx),
            Priority = builder._priority,
            RunIf = builder._runIf,
            After = builder._after,
            AfterAll = builder._afterAll,
            TickDivisor = builder._tickDivisor,
            ThrottledTickDivisor = builder._throttledTickDivisor,
            CanShed = builder._canShed,
            InputFactory = builder._inputFactory,
            ChangeFilter = builder._changeFilter,
            Parallel = builder._parallel,
            WritesVersioned = builder._writesVersioned,
            TierFilter = builder._tierFilter,
            CellAmortize = builder._cellAmortize,
            Checkerboard = builder._checkerboard,
            Phase = builder._phase,
            PhaseSet = builder._phaseSet,
            Before = builder._before,
            Access = builder._access
        });
        return this;
    }

    /// <summary>Register a class-based PipelineSystem. Not yet supported — execution model pending Patate design.</summary>
    public RuntimeSchedule Add(PipelineSystem system)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(system);
        throw new NotSupportedException("PipelineSystem registration not yet supported — execution model pending Patate design.");
    }

    /// <summary>Register a CompoundSystem. Expands all sub-systems into the schedule.</summary>
    public RuntimeSchedule Add(CompoundSystem compound)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(compound);

        CompoundSystem.InvokeConfigure(compound);
        foreach (var sys in compound._systems)
        {
            switch (sys)
            {
                case CallbackSystem cb:
                    Add(cb);
                    break;
                case QuerySystem qs:
                    Add(qs);
                    break;
                case PipelineSystem ps:
                    Add(ps);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown system type in CompoundSystem: {sys.GetType().Name}");
            }
        }

        return this;
    }

    private static void ValidateBuilderName(SystemBuilder builder, string systemTypeName)
    {
        if (builder._name == null)
        {
            throw new InvalidOperationException($"{systemTypeName} must have a name. Call b.Name(...) in Configure.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Event queues
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a typed event queue for inter-system communication.
    /// </summary>
    /// <typeparam name="T">Event type.</typeparam>
    /// <param name="name">Diagnostic name for the queue.</param>
    /// <param name="capacity">Maximum events per tick. Must be a power of 2.</param>
    public EventQueue<T> CreateEventQueue<T>(string name, int capacity = 1024)
    {
        ThrowIfBuilt();
        var queue = new EventQueue<T>(name, capacity);
        _eventQueues.Add(queue);
        return queue;
    }

    /// <summary>
    /// Declares that a system produces (writes to) the specified event queues.
    /// </summary>
    public RuntimeSchedule Produces(string systemName, params EventQueueBase[] queues)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(systemName);

        if (!_produces.TryGetValue(systemName, out var list))
        {
            list = [];
            _produces[systemName] = list;
        }

        list.AddRange(queues);
        return this;
    }

    /// <summary>
    /// Declares that a system consumes (reads from) the specified event queues.
    /// </summary>
    public RuntimeSchedule Consumes(string systemName, params EventQueueBase[] queues)
    {
        ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(systemName);

        if (!_consumes.TryGetValue(systemName, out var list))
        {
            list = [];
            _consumes[systemName] = list;
        }

        list.AddRange(queues);
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // Build
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Validates the schedule and builds a <see cref="DagScheduler"/>.
    /// </summary>
    /// <param name="parent">Parent resource node (typically <see cref="IResourceRegistry.Runtime"/>).</param>
    /// <param name="logger">Optional logger.</param>
    /// <returns>A ready-to-start <see cref="DagScheduler"/>.</returns>
    /// <exception cref="InvalidOperationException">DAG contains a cycle, duplicate names, or invalid references.</exception>
    public DagScheduler Build(IResource parent, ILogger logger = null)
    {
        ThrowIfBuilt();

        // ── Resolve phase index map (RFC 07 / Q3) ──
        var phaseIndexMap = BuildPhaseIndexMap(_options.Phases);

        if (!phaseIndexMap.TryGetValue(_options.DefaultPhase.Name, out var defaultPhaseIndex))
        {
            throw new InvalidOperationException(
                $"RuntimeOptions.DefaultPhase '{_options.DefaultPhase.Name}' must be present in RuntimeOptions.Phases. " +
                "Either add it to Phases or set DefaultPhase to one of the listed phases.");
        }

        // ── Build-time validation ──
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reg in _registrations)
        {
            if (!seenNames.Add(reg.Name))
            {
                throw new InvalidOperationException($"Duplicate system name: '{reg.Name}'.");
            }

            if (reg.Type == SystemType.CallbackSystem)
            {
                if (reg.ChangeFilter is { Length: > 0 })
                {
                    throw new InvalidOperationException(
                        $"System '{reg.Name}': ChangeFilter is not valid for CallbackSystem. CallbackSystem is proactive and does not have a View input.");
                }

                if (reg.InputFactory != null)
                {
                    throw new InvalidOperationException(
                        $"System '{reg.Name}': Input is not valid for CallbackSystem. CallbackSystem is proactive and does not process entities from a View.");
                }

                if (reg.Parallel)
                {
                    throw new InvalidOperationException(
                        $"System '{reg.Name}': Parallel is not valid for CallbackSystem.");
                }
            }

            if (reg.Type == SystemType.PipelineSystem && reg.Parallel)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': Parallel is not valid for PipelineSystem. PipelineSystem has its own chunk-parallel execution model.");
            }

            if (reg.Parallel && reg.InputFactory == null)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': Parallel requires an Input View. Specify b.Input(() => view) alongside b.Parallel().");
            }

            if (reg.ChangeFilter is { Length: > 0 } && reg.InputFactory == null)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': ChangeFilter requires an Input View. Specify input: () => view alongside changeFilter.");
            }

            if (reg.WritesVersioned && !reg.Parallel)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': WritesVersioned is only meaningful for parallel QuerySystems. Add b.Parallel() or parallel: true.");
            }

            // Issue #231: tier filter + amortization validation.
            if (reg.TierFilter == SimTier.None)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': tier: SimTier.None would dispatch zero clusters. Use a specific tier (e.g. SimTier.Tier0), " +
                    "a flag combination (e.g. SimTier.Near), or SimTier.All (the default) to disable tier filtering.");
            }

            if (reg.CellAmortize < 0)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': cellAmortize must be >= 0 (0 = no amortization), got {reg.CellAmortize}.");
            }

            if (reg.CellAmortize > 0 && reg.TierFilter == SimTier.All)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': cellAmortize requires a tier filter. Amortizing the full cluster set without tier scoping " +
                    "is not supported — amortization is a per-tier policy (typically used with coarse tiers like SimTier.Tier2).");
            }

            if (reg.TierFilter != SimTier.All && reg.TierFilter != SimTier.None && reg.Type != SystemType.QuerySystem)
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}': tier filter is only supported on QuerySystem, not {reg.Type}.");
            }

            // Issue #234: checkerboard validation.
            if (reg.Checkerboard && !reg.Parallel)
            {
                throw new InvalidOperationException($"System '{reg.Name}': checkerboard dispatch requires parallel: true. Add b.Parallel() or parallel: true.");
            }
        }

        _built = true;

        var dagBuilder = new DagBuilder();

        // Phase 1: Register all systems
        foreach (var reg in _registrations)
        {
            switch (reg.Type)
            {
                case SystemType.CallbackSystem:
                    dagBuilder.AddCallbackSystem(reg.Name, reg.CallbackAction, reg.Priority, reg.RunIf);
                    break;
                case SystemType.QuerySystem:
                    dagBuilder.AddQuerySystem(reg.Name, reg.CallbackAction, reg.Priority, reg.RunIf);
                    break;
                case SystemType.PipelineSystem:
                    dagBuilder.AddPipelineSystem(reg.Name, reg.PipelineChunkAction, reg.TotalChunks, reg.Priority, reg.RunIf);
                    break;
            }
        }

        // Phase 1b: Resolve each registration's phase index up-front (RFC 07 / Q3 — needed by access-edge derivation).
        // Systems that didn't call b.Phase(...) get RuntimeOptions.DefaultPhase so every system lands in some phase
        // (RFC 07 / Unit 5 — closes the PhaseIndex==-1 escape hatch from Unit 1).
        var regPhaseIndex = new Dictionary<string, int>(_registrations.Count, StringComparer.Ordinal);
        foreach (var reg in _registrations)
        {
            if (!reg.PhaseSet)
            {
                regPhaseIndex[reg.Name] = defaultPhaseIndex;
                continue;
            }

            if (!phaseIndexMap.TryGetValue(reg.Phase.Name, out var phaseIdx))
            {
                throw new InvalidOperationException(
                    $"System '{reg.Name}' declares phase '{reg.Phase.Name}' which is not in RuntimeOptions.Phases. " +
                    "Add it to options.Phases or use a phase already listed there.");
            }

            regPhaseIndex[reg.Name] = phaseIdx;
        }

        // Phase 2: Collect dependency edges (explicit, declared via .After/.AfterAll/.Before)
        var explicitEdges = new List<(string From, string To)>();
        foreach (var reg in _registrations)
        {
            if (reg.After != null)
            {
                explicitEdges.Add((reg.After, reg.Name));
            }

            if (reg.AfterAll != null)
            {
                foreach (var dep in reg.AfterAll)
                {
                    explicitEdges.Add((dep, reg.Name));
                }
            }

            if (reg.Before != null)
            {
                explicitEdges.Add((reg.Name, reg.Before));
            }
        }

        // Phase 2b: Derive access-based edges + validate conflicts (RFC 07 — Unit 3)
        var systemInfos = new List<AccessDagDeriver.SystemInfo>(_registrations.Count);
        foreach (var reg in _registrations)
        {
            systemInfos.Add(new AccessDagDeriver.SystemInfo(reg.Name, regPhaseIndex[reg.Name], reg.Access));
        }

        var derivedEdges = AccessDagDeriver.DeriveAndValidate(systemInfos, explicitEdges);

        // Phase 2c: Apply all edges (explicit + derived) to the DAG builder
        foreach (var (from, to) in explicitEdges)
        {
            dagBuilder.AddEdge(from, to);
        }

        foreach (var (from, to) in derivedEdges)
        {
            dagBuilder.AddEdge(from, to);
        }

        // Phase 3: Build DAG (validates acyclicity, computes predecessors/successors)
        var (systems, topologicalOrder) = dagBuilder.Build();

        // Phase 4: Wire event queue indices into SystemDefinitions
        var nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sys in systems)
        {
            nameToIndex[sys.Name] = sys.Index;
        }

        var queueToIndex = new Dictionary<EventQueueBase, int>();
        for (var i = 0; i < _eventQueues.Count; i++)
        {
            queueToIndex[_eventQueues[i]] = i;
        }

        foreach (var (sysName, queues) in _produces)
        {
            if (!nameToIndex.TryGetValue(sysName, out var sysIdx))
            {
                throw new InvalidOperationException($"Produces: system '{sysName}' not found.");
            }

            var indices = new int[queues.Count];
            for (var i = 0; i < queues.Count; i++)
            {
                indices[i] = queueToIndex[queues[i]];
            }

            systems[sysIdx].ProducesQueueIndices = indices;
        }

        foreach (var (sysName, queues) in _consumes)
        {
            if (!nameToIndex.TryGetValue(sysName, out var sysIdx))
            {
                throw new InvalidOperationException($"Consumes: system '{sysName}' not found.");
            }

            var indices = new int[queues.Count];
            for (var i = 0; i < queues.Count; i++)
            {
                indices[i] = queueToIndex[queues[i]];
            }

            systems[sysIdx].ConsumesQueueIndices = indices;
        }

        // Phase 4b: Validate parallel QuerySystem constraints (must run after queue wiring)
        foreach (var reg in _registrations)
        {
            if (reg.Parallel && nameToIndex.TryGetValue(reg.Name, out var pIdx))
            {
                if (systems[pIdx].ConsumesQueueIndices is { Length: > 0 })
                {
                    throw new InvalidOperationException(
                        $"System '{reg.Name}': Parallel QuerySystem cannot consume event queues. " +
                        "Event queue drain is single-consumer. Move event processing to a separate non-parallel system upstream.");
                }
            }
        }

        // Phase 5: Store overload params, input, and change filter from registrations
        foreach (var reg in _registrations)
        {
            if (!nameToIndex.TryGetValue(reg.Name, out var sysIdx))
            {
                continue;
            }

            // TickDivisor, ThrottledTickDivisor, CanShed are already set via init properties
            // in DagBuilder. But DagBuilder doesn't pass them through yet, so set them here.
            systems[sysIdx].TickDivisor = reg.TickDivisor;
            systems[sysIdx].ThrottledTickDivisor = reg.ThrottledTickDivisor;
            systems[sysIdx].CanShed = reg.CanShed;
            systems[sysIdx].InputFactory = reg.InputFactory;
            systems[sysIdx].ChangeFilterTypes = reg.ChangeFilter;
            systems[sysIdx].IsParallelQuery = reg.Parallel;
            systems[sysIdx].WritesVersioned = reg.WritesVersioned;
            systems[sysIdx].TierFilter = reg.TierFilter;
            systems[sysIdx].CellAmortize = reg.CellAmortize;
            systems[sysIdx].IsCheckerboard = reg.Checkerboard;

            if (reg.Access != null)
            {
                systems[sysIdx].Access = reg.Access;
            }
        }

        // Phase 5b: Stamp resolved phase + index onto each SystemDefinition (RFC 07 / Q3-Q5 — index already resolved in Phase 1b).
        // Undeclared systems get RuntimeOptions.DefaultPhase via the same path.
        foreach (var reg in _registrations)
        {
            if (!nameToIndex.TryGetValue(reg.Name, out var sysIdx))
            {
                continue;
            }

            systems[sysIdx].Phase = reg.PhaseSet ? reg.Phase : _options.DefaultPhase;
            systems[sysIdx].PhaseIndex = regPhaseIndex[reg.Name];
        }

        // Phase 6: Create scheduler
        return new DagScheduler(systems, topologicalOrder, _options, parent, [.. _eventQueues], logger);
    }

    /// <summary>
    /// Builds the phase-token → index map from <see cref="RuntimeOptions.Phases"/>. Validates that the list is non-empty and contains no duplicates.
    /// </summary>
    private static Dictionary<string, int> BuildPhaseIndexMap(Phase[] phases)
    {
        if (phases is null || phases.Length == 0)
        {
            throw new InvalidOperationException("RuntimeOptions.Phases must contain at least one phase. Default is RuntimeOptions.DefaultPhases.");
        }

        var map = new Dictionary<string, int>(phases.Length, StringComparer.Ordinal);
        for (var i = 0; i < phases.Length; i++)
        {
            var name = phases[i].Name;
            if (!map.TryAdd(name, i))
            {
                throw new InvalidOperationException($"Duplicate phase '{name}' in RuntimeOptions.Phases at index {i}. Each phase must appear at most once.");
            }
        }

        return map;
    }

    private void ThrowIfBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("This schedule has already been built. Create a new RuntimeSchedule.");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal registration record
    // ═══════════════════════════════════════════════════════════════

    private sealed class SystemRegistration
    {
        public string Name;
        public SystemType Type;
        public Action<TickContext> CallbackAction;
        public Action<int, int> PipelineChunkAction;
        public int TotalChunks = 1;
        public SystemPriority Priority;
        public Func<bool> RunIf;
        public string After;
        public string[] AfterAll;
        public int TickDivisor = 1;
        public int ThrottledTickDivisor = 1;
        public bool CanShed;
        public Func<ViewBase> InputFactory;
        public Type[] ChangeFilter;
        public bool Parallel;
        public bool WritesVersioned;
        public SimTier TierFilter = SimTier.All;
        public int CellAmortize;
        public bool Checkerboard;
        public Phase Phase;
        public bool PhaseSet;
        public string Before;
        public SystemAccessDescriptor Access;
    }
}
