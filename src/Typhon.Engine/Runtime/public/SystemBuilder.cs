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
    internal Func<bool> _shouldRun;
    internal Func<ViewBase> _inputFactory;
    internal Type[] _changeFilter;
    internal int _tickDivisor = 1;
    internal int _throttledTickDivisor = 1;
    internal bool _canShed;
    internal bool _parallel;
    internal bool _writesVersioned;
    internal float _chunksPerWorker = 1f;
    internal int _explicitChunkCount;       // > 0 → chunked-parallel CallbackSystem (no entity input)
    internal SimTier _tierFilter = SimTier.All;
    internal int _cellAmortize;
    internal bool _checkerboard;
    internal Phase _phase;
    internal bool _phaseSet;
    internal bool _isInternal;
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
    public SystemBuilder ShouldRun(Func<bool> predicate)
    {
        _shouldRun = predicate;
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

    /// <summary>
    /// Enable chunk-parallel execution for a <see cref="CallbackSystem"/> with an explicit chunk count, independent of any entity input. The system's
    /// <c>Execute</c> is invoked <paramref name="chunkCount"/> times in parallel across workers; each invocation receives <see cref="TickContext.ChunkIndex"/>
    /// and <see cref="TickContext.ChunkCount"/> so the implementation can offset its data accordingly.
    /// <para>
    /// Designed for non-entity-iterating work that's naturally chunkable — e.g. SIMD sweeps over flat arrays, image downsamples, parallel reductions.
    /// Skips all entity-prep infrastructure (Accessor, Entities, per-chunk Transaction): the TickContext passed in carries only tick metadata + ChunkIndex/ChunkCount.
    /// </para>
    /// <para>
    /// Incompatible with <see cref="Input"/>, <see cref="ChangeFilter"/>, <see cref="WritesVersioned"/>, <see cref="Tier"/>, <see cref="CellAmortize"/>,
    /// <see cref="Checkerboard"/>, and <see cref="ChunksPerWorker"/> — all of those are entity-context concepts.
    /// </para>
    /// </summary>
    public SystemBuilder ChunkedParallel(int chunkCount)
    {
        if (chunkCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkCount), "chunkCount must be >= 1");
        }

        _parallel = true;
        _explicitChunkCount = chunkCount;
        return this;
    }

    /// <summary>Declare that this parallel QuerySystem writes Versioned components. Forces per-chunk Transaction fallback instead of the optimized PointInTimeAccessor path.</summary>
    public SystemBuilder WritesVersioned()
    {
        _writesVersioned = true;
        return this;
    }

    /// <summary>
    /// Oversubscription factor for parallel chunk dispatch. The effective worker-cap on chunk count becomes
    /// <c>round(WorkerCount × factor)</c> instead of <c>WorkerCount</c>. Default <c>1.0f</c>.
    /// <para>
    /// Use values above 1.0 (e.g. 1.5, 2.0) when a parallel system shows poor worker efficiency because one slow chunk holds
    /// back the critical path — extra chunks let fast workers steal more work via the dynamic dispatch loop. Final chunk count
    /// is still capped by <c>ceil(entityCount / ParallelQueryMinChunkSize)</c>, so small populations don't proliferate chunks.
    /// </para>
    /// Validated at <see cref="RuntimeSchedule.Build"/>: must be in <c>[1.0, 64.0]</c>, and rejected on non-parallel systems where it has no effect.
    /// </summary>
    public SystemBuilder ChunksPerWorker(float factor)
    {
        _chunksPerWorker = factor;
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

    /// <summary>
    /// Mark this system as engine-internal. It is registered on the same <see cref="RuntimeSchedule"/> but partitioned into a separate internal sub-DAG
    /// dispatched after the user DAG completes (used by <c>FenceExec</c>). Internal systems are not surfaced in user-facing tooling views by default.
    /// Intended for engine code only.
    /// </summary>
    public SystemBuilder Internal()
    {
        _isInternal = true;
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

/// <summary>
/// Typed fluent builder for systems deriving from <see cref="ChunkedCallbackSystem{TContext}"/>. Wraps a non-generic <see cref="SystemBuilder"/> and exposes
/// typed <see cref="ShouldRun"/> / <see cref="Prepare"/> overloads that receive the ambient TContext. All other methods forward to the inner builder unchanged.
/// </summary>
[PublicAPI]
public sealed class SystemBuilder<TContext> where TContext : class
{
    private readonly SystemBuilder _inner;
    private readonly ChunkedCallbackSystem<TContext> _system;

    internal SystemBuilder(SystemBuilder inner, ChunkedCallbackSystem<TContext> system)
    {
        _inner = inner;
        _system = system;
    }

    /// <summary>Set a typed predicate evaluated before dispatch. Stored on the system instance; read by <see cref="ChunkedCallbackSystem{TContext}.OnShouldRun"/>.</summary>
    public SystemBuilder<TContext> ShouldRun(Func<TContext, bool> predicate)
    {
        _system.SetShouldRunLambda(predicate);
        return this;
    }

    /// <summary>Set a typed plan builder evaluated before dispatch. Returns the dynamic chunk count (0 = skip, &gt;0 = dispatch, -1 = no opinion).</summary>
    public SystemBuilder<TContext> Prepare(Func<TContext, int> planBuilder)
    {
        _system.SetPrepareLambda(planBuilder);
        return this;
    }

    // Forwarded fluent methods. The typed builder intentionally does NOT expose the untyped
    // ShouldRun(Func<bool>) overload — typed-system authors use the typed signature above.
    public SystemBuilder<TContext> Name(string name) { _inner.Name(name); return this; }
    public SystemBuilder<TContext> After(string dependency) { _inner.After(dependency); return this; }
    public SystemBuilder<TContext> AfterAll(params string[] dependencies) { _inner.AfterAll(dependencies); return this; }
    public SystemBuilder<TContext> Before(string dependent) { _inner.Before(dependent); return this; }
    public SystemBuilder<TContext> Priority(SystemPriority priority) { _inner.Priority(priority); return this; }
    public SystemBuilder<TContext> Input(Func<ViewBase> viewFactory) { _inner.Input(viewFactory); return this; }
    public SystemBuilder<TContext> ChangeFilter(params Type[] componentTypes) { _inner.ChangeFilter(componentTypes); return this; }
    public SystemBuilder<TContext> TickDivisor(int divisor) { _inner.TickDivisor(divisor); return this; }
    public SystemBuilder<TContext> ThrottledTickDivisor(int divisor) { _inner.ThrottledTickDivisor(divisor); return this; }
    public SystemBuilder<TContext> CanShed(bool value) { _inner.CanShed(value); return this; }
    public SystemBuilder<TContext> Parallel() { _inner.Parallel(); return this; }
    public SystemBuilder<TContext> ChunkedParallel(int chunkCount) { _inner.ChunkedParallel(chunkCount); return this; }
    public SystemBuilder<TContext> WritesVersioned() { _inner.WritesVersioned(); return this; }
    public SystemBuilder<TContext> ChunksPerWorker(float factor) { _inner.ChunksPerWorker(factor); return this; }
    public SystemBuilder<TContext> Tier(SimTier tier) { _inner.Tier(tier); return this; }
    public SystemBuilder<TContext> CellAmortize(int denominator) { _inner.CellAmortize(denominator); return this; }
    public SystemBuilder<TContext> Checkerboard() { _inner.Checkerboard(); return this; }
    public SystemBuilder<TContext> Phase(Phase phase) { _inner.Phase(phase); return this; }
    public SystemBuilder<TContext> Internal() { _inner.Internal(); return this; }

    public SystemBuilder<TContext> Reads<T>() where T : unmanaged { _inner.Reads<T>(); return this; }
    public SystemBuilder<TContext> ReadsFresh<T>() where T : unmanaged { _inner.ReadsFresh<T>(); return this; }
    public SystemBuilder<TContext> ReadsSnapshot<T>() where T : unmanaged { _inner.ReadsSnapshot<T>(); return this; }
    public SystemBuilder<TContext> AdditionalReads<T>() where T : unmanaged { _inner.AdditionalReads<T>(); return this; }
    public SystemBuilder<TContext> Writes<T>() where T : unmanaged { _inner.Writes<T>(); return this; }
    public SystemBuilder<TContext> SideWrites<T>() where T : unmanaged { _inner.SideWrites<T>(); return this; }
    public SystemBuilder<TContext> WritesEvents(EventQueueBase queue) { _inner.WritesEvents(queue); return this; }
    public SystemBuilder<TContext> ReadsEvents(EventQueueBase queue) { _inner.ReadsEvents(queue); return this; }
    public SystemBuilder<TContext> WritesResource(string name) { _inner.WritesResource(name); return this; }
    public SystemBuilder<TContext> ReadsResource(string name) { _inner.ReadsResource(name); return this; }
    public SystemBuilder<TContext> ExclusivePhase() { _inner.ExclusivePhase(); return this; }
}
