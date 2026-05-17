using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// A dependency graph of systems — the third level of the runtime partitioning hierarchy (<c>Engine → Track → DAG → Phase → System</c>). A DAG belongs to
/// exactly one <see cref="Track"/> and declares its own ordered phase sequence.
/// </summary>
/// <remarks>
/// <para>
/// Every DAG is declared and executed the same way — there is no public/internal distinction. Systems are registered with <see cref="CallbackSystem"/> /
/// <see cref="QuerySystem"/> / <see cref="PipelineSystem"/> (lambda style) or <see cref="Add(CallbackSystem)"/> (class-based, full RFC 07 access declarations).
/// </para>
/// <para>
/// Phases are <b>DAG-local</b>: a DAG that declares no phases via <see cref="Phases"/> gets a single implicit phase, and <c>.After()</c> edges order its
/// systems. There is no engine-global phase namespace.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class Dag
{
    /// <summary>Name of the single implicit phase a DAG gets when it declares no phases of its own.</summary>
    internal const string ImplicitPhaseName = "Default";

    private readonly List<SystemRegistration> _registrations = [];
    private Phase[] _phases;
    private Phase _defaultPhase;
    private bool _defaultPhaseSet;

    internal Dag(Track track, string name)
    {
        Track = track;
        Name = name;
    }

    /// <summary>The track this DAG belongs to.</summary>
    public Track Track { get; }

    /// <summary>DAG name — unique across the whole schedule.</summary>
    public string Name { get; }

    /// <summary>Flat global DAG id. Assigned by <see cref="RuntimeSchedule.Build"/>; -1 before the schedule is built.</summary>
    public int Id { get; internal set; } = -1;

    /// <summary>
    /// Canonical indices (into <see cref="DagScheduler.Systems"/>) of the systems belonging to this DAG.
    /// Populated by <see cref="RuntimeSchedule.Build"/>; empty before the schedule is built.
    /// </summary>
    public int[] SystemIndices { get; internal set; } = [];

    internal IReadOnlyList<SystemRegistration> Registrations => _registrations;

    /// <summary>
    /// The DAG's ordered phase sequence. When <see cref="Phases"/> was never called this is a single implicit phase (<see cref="ImplicitPhaseName"/>).
    /// Phases form a DAG-local total order — every system in phase <c>N</c> completes before any system in phase <c>N+1</c> of the same DAG.
    /// </summary>
    public Phase[] ResolvedPhases => _phases ?? [new Phase(ImplicitPhaseName)];

    /// <summary>The phase assigned to systems registered without an explicit <c>b.Phase(...)</c> call.</summary>
    public Phase ResolvedDefaultPhase => _defaultPhaseSet ? _defaultPhase : ResolvedPhases[0];

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Phase declaration
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Declares this DAG's ordered phase sequence. Phases form a DAG-local total order. May be called once;
    /// omit it entirely for a trivial DAG that orders its systems with <c>.After()</c> edges only.
    /// </summary>
    public Dag Phases(params Phase[] phases)
    {
        Track.Schedule.ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(phases);
        if (phases.Length == 0)
        {
            throw new InvalidOperationException($"DAG '{Name}': Phases(...) requires at least one phase. Omit the call entirely for an implicit single-phase DAG.");
        }

        if (_phases != null)
        {
            throw new InvalidOperationException($"DAG '{Name}': Phases(...) was already declared. Declare the full ordered phase list in a single call.");
        }

        _phases = phases;
        return this;
    }

    /// <summary>Sets the phase assigned to systems that don't call <c>b.Phase(...)</c>. Defaults to the first declared phase.</summary>
    public Dag DefaultPhase(Phase phase)
    {
        Track.Schedule.ThrowIfBuilt();
        _defaultPhase = phase;
        _defaultPhaseSet = true;
        return this;
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // System registration — lambda style (no RFC 07 access declarations)
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Registers a CallbackSystem — lightweight single-invocation, no entity input.</summary>
    public Dag CallbackSystem(string name, Action<TickContext> action, string after = null, string[] afterAll = null, 
        SystemPriority priority = SystemPriority.Normal, Func<bool> shouldRun = null, int tickDivisor = 1, int throttledTickDivisor = 1, bool canShed = false)
    {
        Track.Schedule.ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(action);

        _registrations.Add(new SystemRegistration
        {
            Name = name,
            Type = SystemType.CallbackSystem,
            CallbackAction = action,
            Priority = priority,
            ShouldRun = shouldRun,
            After = after,
            AfterAll = afterAll,
            TickDivisor = tickDivisor,
            ThrottledTickDivisor = throttledTickDivisor,
            CanShed = canShed
        });
        return this;
    }

    /// <summary>Registers a QuerySystem — single-worker entity iteration.</summary>
    public Dag QuerySystem(string name, Action<TickContext> action, string after = null, string[] afterAll = null,
        SystemPriority priority = SystemPriority.Normal, Func<bool> shouldRun = null, Func<ViewBase> input = null, Type[] changeFilter = null,
        int tickDivisor = 1, int throttledTickDivisor = 1, bool canShed = false, bool parallel = false, bool writesVersioned = false,
        SimTier tier = SimTier.All, int cellAmortize = 0, bool checkerboard = false, float chunksPerWorker = 1f)
    {
        Track.Schedule.ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(action);

        _registrations.Add(new SystemRegistration
        {
            Name = name,
            Type = SystemType.QuerySystem,
            CallbackAction = action,
            Priority = priority,
            ShouldRun = shouldRun,
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
            Checkerboard = checkerboard,
            ChunksPerWorker = chunksPerWorker
        });
        return this;
    }

    /// <summary>Registers a PipelineSystem — multi-worker chunk-parallel execution.</summary>
    public Dag PipelineSystem(string name, Action<int, int> chunkAction, int totalChunks, string after = null, string[] afterAll = null,
        SystemPriority priority = SystemPriority.Normal, Func<bool> shouldRun = null, Func<ViewBase> input = null, Type[] changeFilter = null,
        int tickDivisor = 1, int throttledTickDivisor = 1, bool canShed = false)
    {
        Track.Schedule.ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(chunkAction);

        _registrations.Add(new SystemRegistration
        {
            Name = name,
            Type = SystemType.PipelineSystem,
            PipelineChunkAction = chunkAction,
            TotalChunks = totalChunks,
            Priority = priority,
            ShouldRun = shouldRun,
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

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // System registration — class-based (full RFC 07 access declarations)
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Register a class-based CallbackSystem.</summary>
    public Dag Add(CallbackSystem system)
    {
        Track.Schedule.ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(system);

        var builder = new SystemBuilder();
        Engine.CallbackSystem.InvokeConfigure(system, builder);
        ValidateBuilderName(builder, nameof(Typhon.Engine.CallbackSystem));
        system.Name = builder._name;

        _registrations.Add(new SystemRegistration
        {
            Name = builder._name,
            Type = SystemType.CallbackSystem,
            CallbackAction = ctx => Engine.CallbackSystem.InvokeExecute(system, ctx),
            SystemInstance = system,
            Priority = builder._priority,
            ShouldRun = builder._shouldRun,
            After = builder._after,
            AfterAll = builder._afterAll,
            TickDivisor = builder._tickDivisor,
            ThrottledTickDivisor = builder._throttledTickDivisor,
            CanShed = builder._canShed,
            InputFactory = builder._inputFactory,
            ChangeFilter = builder._changeFilter,
            Parallel = builder._parallel,
            WritesVersioned = builder._writesVersioned,
            ExplicitChunkCount = builder._explicitChunkCount,
            Phase = builder._phase,
            PhaseSet = builder._phaseSet,
            Before = builder._before,
            Access = builder._access
        });
        return this;
    }

    /// <summary>Register a class-based QuerySystem.</summary>
    public Dag Add(QuerySystem system)
    {
        Track.Schedule.ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(system);

        var builder = new SystemBuilder();
        Engine.QuerySystem.InvokeConfigure(system, builder);
        ValidateBuilderName(builder, nameof(Typhon.Engine.QuerySystem));
        system.Name = builder._name;

        _registrations.Add(new SystemRegistration
        {
            Name = builder._name,
            Type = SystemType.QuerySystem,
            CallbackAction = ctx => Engine.QuerySystem.InvokeExecute(system, ctx),
            SystemInstance = system,
            Priority = builder._priority,
            ShouldRun = builder._shouldRun,
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
            ChunksPerWorker = builder._chunksPerWorker,
            Phase = builder._phase,
            PhaseSet = builder._phaseSet,
            Before = builder._before,
            Access = builder._access
        });
        return this;
    }

    /// <summary>Register a class-based PipelineSystem. Not yet supported — execution model pending Patate design.</summary>
    public Dag Add(PipelineSystem system)
    {
        Track.Schedule.ThrowIfBuilt();
        ArgumentNullException.ThrowIfNull(system);
        throw new NotSupportedException("PipelineSystem registration not yet supported — execution model pending Patate design.");
    }

    /// <summary>Register a CompoundSystem. Expands all sub-systems into this DAG.</summary>
    public Dag Add(CompoundSystem compound)
    {
        Track.Schedule.ThrowIfBuilt();
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

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Schedule-level forwarders — let a single declared DAG drive the whole fluent chain
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Creates a typed event queue on the owning schedule. Forwards to <see cref="RuntimeSchedule.CreateEventQueue{T}"/>.</summary>
    public EventQueue<T> CreateEventQueue<T>(string name, int capacity = 1024) => Track.Schedule.CreateEventQueue<T>(name, capacity);

    /// <summary>Declares that a system produces the given event queues. Forwards to <see cref="RuntimeSchedule.Produces"/>.</summary>
    public Dag Produces(string systemName, params EventQueueBase[] queues)
    {
        Track.Schedule.Produces(systemName, queues);
        return this;
    }

    /// <summary>Declares that a system consumes the given event queues. Forwards to <see cref="RuntimeSchedule.Consumes"/>.</summary>
    public Dag Consumes(string systemName, params EventQueueBase[] queues)
    {
        Track.Schedule.Consumes(systemName, queues);
        return this;
    }

    /// <summary>Builds the owning schedule into a <see cref="DagScheduler"/>. Forwards to <see cref="RuntimeSchedule.Build"/>.</summary>
    public DagScheduler Build(IResource parent, ILogger logger = null) => Track.Schedule.Build(parent, logger);

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Internal registration record
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Build-time capture of a single registered system. Consumed by <see cref="RuntimeSchedule.Build"/>.</summary>
    internal sealed class SystemRegistration
    {
        public string Name;
        public SystemType Type;
        public Action<TickContext> CallbackAction;
        public Action<int, int> PipelineChunkAction;
        public int TotalChunks = 1;
        public SystemPriority Priority;
        public Func<bool> ShouldRun;
        public string After;
        public string[] AfterAll;
        public int TickDivisor = 1;
        public int ThrottledTickDivisor = 1;
        public bool CanShed;
        public Func<ViewBase> InputFactory;
        public Type[] ChangeFilter;
        public bool Parallel;
        public bool WritesVersioned;
        public float ChunksPerWorker = 1f;
        public int ExplicitChunkCount;          // > 0 → chunked-parallel CallbackSystem (no entity context)
        public SimTier TierFilter = SimTier.All;
        public int CellAmortize;
        public bool Checkerboard;
        public Phase Phase;
        public bool PhaseSet;
        public string Before;
        public SystemAccessDescriptor Access;

        /// <summary>
        /// Class-based system instance (subclass of <see cref="QuerySystem"/>, <see cref="CallbackSystem"/>, or <see cref="PipelineSystem"/>) when this
        /// registration came from the <c>Add(QuerySystem)</c> family rather than a delegate overload. Null for delegate registrations. Used by
        /// <see cref="RuntimeSchedule.Build"/> to redirect source attribution to the user's <c>Execute</c> override and to write back the engine-assigned
        /// <see cref="ISystem.Name"/> / <see cref="ISystem.Index"/>.
        /// </summary>
        public ISystem SystemInstance;
    }
}
