using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// Configuration builder for class-based system definitions.
/// Used by <see cref="CallbackSystem"/>, <see cref="QuerySystem"/>, and <see cref="PipelineSystem"/> in their Configure method.
/// </summary>
/// <remarks>
/// All methods return the builder so calls can chain: <c>b.Name("X").After("Y").Reads&lt;Position&gt;().Writes&lt;Velocity&gt;()</c>.
/// </remarks>
[PublicAPI]
public sealed class SystemBuilder
{
    internal string _name;
    internal string _after;
    internal string[] _afterAll;
    internal string _before;
    internal SystemPriority _priority = SystemPriority.Normal;
    internal Func<bool> _runIf;
    internal Func<ViewBase> _inputFactory;
    internal Type[] _changeFilter;
    internal int _tickDivisor = 1;
    internal int _throttledTickDivisor = 1;
    internal bool _canShed;
    internal bool _parallel;
    internal bool _writesVersioned;
    internal SimTier _tierFilter = SimTier.All;
    internal int _cellAmortize;
    internal bool _checkerboard;
    internal Phase _phase;
    internal bool _phaseSet;
    internal readonly SystemAccessDescriptor _access = new();

    // ═══════════════════════════════════════════════════════════════
    // Identity, ordering, and overload parameters
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Set the system's unique name in the DAG.</summary>
    public SystemBuilder Name(string name)
    {
        _name = name;
        return this;
    }

    /// <summary>Declare a dependency on another system (this system runs after it).</summary>
    public SystemBuilder After(string dependency)
    {
        _after = dependency;
        return this;
    }

    /// <summary>Declare dependencies on multiple systems (this system runs after all of them).</summary>
    public SystemBuilder AfterAll(params string[] dependencies)
    {
        _afterAll = dependencies;
        return this;
    }

    /// <summary>Declare that this system must run before another (mirror of <see cref="After"/>). Useful for the W×W disambiguation case (RFC 07 / Q5).</summary>
    public SystemBuilder Before(string dependent)
    {
        _before = dependent;
        return this;
    }

    /// <summary>Set the system's overload priority.</summary>
    public SystemBuilder Priority(SystemPriority priority)
    {
        _priority = priority;
        return this;
    }

    /// <summary>Set a predicate that must return true for the system to execute. Evaluated before any input processing.</summary>
    public SystemBuilder RunIf(Func<bool> predicate)
    {
        _runIf = predicate;
        return this;
    }

    /// <summary>Set the View factory providing the system's entity input.</summary>
    public SystemBuilder Input(Func<ViewBase> viewFactory)
    {
        _inputFactory = viewFactory;
        return this;
    }

    /// <summary>Set the component types for change-filtered reactive input. OR logic: entity included if any filtered component was written.</summary>
    public SystemBuilder ChangeFilter(params Type[] componentTypes)
    {
        _changeFilter = componentTypes;
        return this;
    }

    /// <summary>Set the tick divisor (system runs every Nth tick at normal load).</summary>
    public SystemBuilder TickDivisor(int divisor)
    {
        _tickDivisor = divisor;
        return this;
    }

    /// <summary>Set the throttled tick divisor (system runs every Nth tick under overload).</summary>
    public SystemBuilder ThrottledTickDivisor(int divisor)
    {
        _throttledTickDivisor = divisor;
        return this;
    }

    /// <summary>Set whether this system can be shed entirely under severe overload.</summary>
    public SystemBuilder CanShed(bool value)
    {
        _canShed = value;
        return this;
    }

    /// <summary>Enable automatic chunk-parallel execution across workers. QuerySystem only.</summary>
    public SystemBuilder Parallel()
    {
        _parallel = true;
        return this;
    }

    /// <summary>Declare that this parallel QuerySystem writes Versioned components. Forces per-chunk Transaction fallback instead of the optimized PointInTimeAccessor path.</summary>
    public SystemBuilder WritesVersioned()
    {
        _writesVersioned = true;
        return this;
    }

    /// <summary>
    /// Set the simulation-tier dispatch filter (issue #231). Default <see cref="SimTier.All"/> matches pre-#231 behaviour (all clusters dispatched).
    /// Single-tier (e.g. <see cref="SimTier.Tier0"/>) or multi-tier flag combinations (<see cref="SimTier.Near"/>, <see cref="SimTier.Active"/>) are both
    /// supported.
    /// </summary>
    public SystemBuilder Tier(SimTier tier)
    {
        _tierFilter = tier;
        return this;
    }

    /// <summary>
    /// Set the cell-level amortization denominator (issue #231). When greater than 0, the system processes <c>1/N</c> of the tier's clusters per tick,
    /// and <see cref="TickContext.AmortizedDeltaTime"/> becomes <c>DeltaTime × N</c>. Requires a non-<see cref="SimTier.All"/> <see cref="Tier"/>.
    /// </summary>
    public SystemBuilder CellAmortize(int denominator)
    {
        _cellAmortize = denominator;
        return this;
    }

    /// <summary>Enable two-phase checkerboard dispatch (issue #234). Requires <see cref="Parallel"/>. Clusters are split into Red/Black sets
    /// based on cell coordinates — no two adjacent cells are processed simultaneously.</summary>
    public SystemBuilder Checkerboard()
    {
        _checkerboard = true;
        return this;
    }

    /// <summary>
    /// Assign this system to a phase (RFC 07 / Q3). Phases form a total order in <see cref="RuntimeOptions.Phases"/> — all systems in
    /// phase N complete before any system in phase N+1 starts. If not called, the system is unaffected by phase ordering (transitional;
    /// Unit 5 of the auto-DAG migration will tighten this to require a phase per system).
    /// </summary>
    public SystemBuilder Phase(Phase phase)
    {
        _phase = phase;
        _phaseSet = true;
        return this;
    }

    // ═══════════════════════════════════════════════════════════════
    // Access declarations (RFC 07 — Unit 2)
    // Storage only; conflict detection + DAG-edge derivation lands in Unit 3.
    // Generic constraint matches EntityRef.Write&lt;T&gt;: where T : unmanaged.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Declare that this system reads component T. Unit 3 errors at <c>Build()</c> if a same-phase writer of T exists — upgrade to <see cref="ReadsFresh{T}"/> or <see cref="ReadsSnapshot{T}"/>.</summary>
    public SystemBuilder Reads<T>() where T : unmanaged
    {
        _access.Reads.Add(typeof(T));
        return this;
    }

    /// <summary>Declare that this system reads component T and must be ordered AFTER any same-phase writer of T (sees this-tick value).</summary>
    public SystemBuilder ReadsFresh<T>() where T : unmanaged
    {
        _access.ReadsFresh.Add(typeof(T));
        return this;
    }

    /// <summary>Declare that this system reads component T and must be ordered BEFORE any same-phase writer of T (sees previous-tick value, enables parallelism with the writer).</summary>
    public SystemBuilder ReadsSnapshot<T>() where T : unmanaged
    {
        _access.ReadsSnapshot.Add(typeof(T));
        return this;
    }

    /// <summary>Declare a read of T beyond the system's primary View input (cross-entity read).</summary>
    public SystemBuilder AdditionalReads<T>() where T : unmanaged
    {
        _access.AdditionalReads.Add(typeof(T));
        return this;
    }

    /// <summary>Declare that this system mutates component T via <c>EntityRef.Write&lt;T&gt;()</c>. Unit 3 errors at <c>Build()</c> if another system in the same phase also declares <c>Writes&lt;T&gt;</c> without an explicit <see cref="After"/>/<see cref="Before"/>.</summary>
    public SystemBuilder Writes<T>() where T : unmanaged
    {
        _access.Writes.Add(typeof(T));
        return this;
    }

    /// <summary>Declare that this system writes T via a side-transaction (<see cref="DurabilityMode.Immediate"/>). Surfaced in tooling but does NOT affect scheduler ordering.</summary>
    public SystemBuilder SideWrites<T>() where T : unmanaged
    {
        _access.SideWrites.Add(typeof(T));
        return this;
    }

    /// <summary>Declare that this system publishes to the given event queue. Unit 3 derives a producer→consumer edge from this against any matching <see cref="ReadsEvents"/> in the same phase.</summary>
    public SystemBuilder WritesEvents(EventQueueBase queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _access.WritesEvents.Add(queue);
        return this;
    }

    /// <summary>Declare that this system consumes from the given event queue.</summary>
    public SystemBuilder ReadsEvents(EventQueueBase queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        _access.ReadsEvents.Add(queue);
        return this;
    }

    /// <summary>Declare that this system mutates a named resource (e.g., a shared physics-world handle that isn't a component).</summary>
    public SystemBuilder WritesResource(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _access.WritesResources.Add(name);
        return this;
    }

    /// <summary>Declare that this system reads a named resource.</summary>
    public SystemBuilder ReadsResource(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _access.ReadsResources.Add(name);
        return this;
    }

    /// <summary>Mark this system as having exclusive control of its phase — no other system in the same phase may run concurrently. Use sparingly for systems that own the tick boundary (e.g. archetype cleanup, spatial-index update).</summary>
    public SystemBuilder ExclusivePhase()
    {
        _access.ExclusivePhase = true;
        return this;
    }

    // ─── Batch overloads (RFC 07 / Q1 — verbosity relief) ───
    // Identical to calling the single-T variant N times. Generated for arities 2..6.

    public SystemBuilder Reads<T1, T2>() where T1 : unmanaged where T2 : unmanaged => Reads<T1>().Reads<T2>();

    public SystemBuilder Reads<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged => Reads<T1>().Reads<T2>().Reads<T3>();

    public SystemBuilder Reads<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged 
        => Reads<T1>().Reads<T2>().Reads<T3>().Reads<T4>();

    public SystemBuilder Reads<T1, T2, T3, T4, T5>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged
        => Reads<T1>().Reads<T2>().Reads<T3>().Reads<T4>().Reads<T5>();

    public SystemBuilder Reads<T1, T2, T3, T4, T5, T6>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged
        where T6 : unmanaged => Reads<T1>().Reads<T2>().Reads<T3>().Reads<T4>().Reads<T5>().Reads<T6>();

    public SystemBuilder Writes<T1, T2>() where T1 : unmanaged where T2 : unmanaged => Writes<T1>().Writes<T2>();

    public SystemBuilder Writes<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged => Writes<T1>().Writes<T2>().Writes<T3>();

    public SystemBuilder Writes<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged 
        => Writes<T1>().Writes<T2>().Writes<T3>().Writes<T4>();

    public SystemBuilder Writes<T1, T2, T3, T4, T5>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged where T5 : unmanaged
        => Writes<T1>().Writes<T2>().Writes<T3>().Writes<T4>().Writes<T5>();

    public SystemBuilder Writes<T1, T2, T3, T4, T5, T6>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        where T5 : unmanaged where T6 : unmanaged => Writes<T1>().Writes<T2>().Writes<T3>().Writes<T4>().Writes<T5>().Writes<T6>();

    public SystemBuilder ReadsFresh<T1, T2>() where T1 : unmanaged where T2 : unmanaged => ReadsFresh<T1>().ReadsFresh<T2>();

    public SystemBuilder ReadsFresh<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
        => ReadsFresh<T1>().ReadsFresh<T2>().ReadsFresh<T3>();

    public SystemBuilder ReadsFresh<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        => ReadsFresh<T1>().ReadsFresh<T2>().ReadsFresh<T3>().ReadsFresh<T4>();

    public SystemBuilder ReadsFresh<T1, T2, T3, T4, T5>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        where T5 : unmanaged => ReadsFresh<T1>().ReadsFresh<T2>().ReadsFresh<T3>().ReadsFresh<T4>().ReadsFresh<T5>();

    public SystemBuilder ReadsFresh<T1, T2, T3, T4, T5, T6>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        where T5 : unmanaged where T6 : unmanaged => ReadsFresh<T1>().ReadsFresh<T2>().ReadsFresh<T3>().ReadsFresh<T4>().ReadsFresh<T5>().ReadsFresh<T6>();

    public SystemBuilder ReadsSnapshot<T1, T2>() where T1 : unmanaged where T2 : unmanaged => ReadsSnapshot<T1>().ReadsSnapshot<T2>();

    public SystemBuilder ReadsSnapshot<T1, T2, T3>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
        => ReadsSnapshot<T1>().ReadsSnapshot<T2>().ReadsSnapshot<T3>();

    public SystemBuilder ReadsSnapshot<T1, T2, T3, T4>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        => ReadsSnapshot<T1>().ReadsSnapshot<T2>().ReadsSnapshot<T3>().ReadsSnapshot<T4>();

    public SystemBuilder ReadsSnapshot<T1, T2, T3, T4, T5>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        where T5 : unmanaged => ReadsSnapshot<T1>().ReadsSnapshot<T2>().ReadsSnapshot<T3>().ReadsSnapshot<T4>().ReadsSnapshot<T5>();

    public SystemBuilder ReadsSnapshot<T1, T2, T3, T4, T5, T6>() where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        where T5 : unmanaged where T6 : unmanaged => ReadsSnapshot<T1>().ReadsSnapshot<T2>().ReadsSnapshot<T3>().ReadsSnapshot<T4>().ReadsSnapshot<T5>().ReadsSnapshot<T6>();
}
