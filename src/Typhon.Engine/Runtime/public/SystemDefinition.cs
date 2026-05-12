using JetBrains.Annotations;
using System;

namespace Typhon.Engine;

/// <summary>
/// Immutable definition of a system node in the system DAG.
/// Created by <see cref="DagBuilder"/> and consumed by <see cref="DagScheduler"/>.
/// </summary>
[PublicAPI]
public sealed class SystemDefinition
{
    /// <summary>Unique name identifying this system in the DAG.</summary>
    public string Name { get; init; }

    /// <summary>Execution model: PipelineSystem (multi-worker chunks), QuerySystem (single-worker entities), or CallbackSystem (inline).</summary>
    public SystemType Type { get; init; }

    /// <summary>Position in the systems array. Set by <see cref="DagBuilder.Build"/>.</summary>
    public int Index { get; internal set; }

    /// <summary>Priority for overload management. Defined but not enforced until #201.</summary>
    public SystemPriority Priority { get; init; } = SystemPriority.Normal;

    /// <summary>
    /// The phase this system belongs to (RFC 07 / Q3). Set by <see cref="RuntimeSchedule.Build"/> from <see cref="SystemBuilder.Phase"/>.
    /// Default-valued (zero <see cref="Phase"/>) when no phase was declared — pair with <see cref="PhaseIndex"/> = -1 to detect.
    /// </summary>
    public Phase Phase { get; internal set; }

    /// <summary>
    /// Index into <see cref="RuntimeOptions.Phases"/> for the resolved phase. -1 when no phase was declared (transitional; Unit 5 of the
    /// auto-DAG migration will require a phase per system).
    /// </summary>
    public int PhaseIndex { get; internal set; } = -1;

    /// <summary>
    /// Declared read/write access for this system (RFC 07 — Unit 2). Populated from <see cref="SystemBuilder"/> declaration methods.
    /// Storage only at the Unit 2 stage — Unit 3 consumes this for conflict detection and DAG-edge derivation.
    /// Public read so tooling (Workbench RFC 07 Unit 6 surfacing) can project declarations into wire records; only the engine sets it.
    /// </summary>
    public SystemAccessDescriptor Access { get; internal set; } = new();

    // ═══════════════════════════════════════════════════════════════
    // Execution delegates
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Delegate for CallbackSystem and QuerySystem systems. Invoked once per tick on a single worker.
    /// Receives <see cref="TickContext"/> with tick-level information.
    /// </summary>
    public Action<TickContext> CallbackAction { get; init; }

    /// <summary>
    /// Delegate for Pipeline systems. Called per chunk with (chunkIndex, totalChunks).
    /// Multiple workers call this concurrently with distinct chunk indices.
    /// No TickContext — Pipeline's entity access goes through Gather/Scatter pipelines.
    /// </summary>
    public Action<int, int> PipelineChunkAction { get; init; }

    // ═══════════════════════════════════════════════════════════════
    // Source attribution (#302 — system-side via PDB sequence points)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Repo-relative or absolute path to the source file declaring this system's entry method.
    /// Resolved from the delegate's PDB at <see cref="DagBuilder"/> registration. Empty/null when
    /// no PDB was available — the Workbench falls back to "no Source row" for the chunk.
    /// </summary>
    public string SourceFilePath { get; init; }

    /// <summary>1-based line number of the entry method's first sequence point. 0 when not resolved.</summary>
    public int SourceLine { get; init; }

    /// <summary>Containing-method short name (e.g. <c>MoveAllAnts</c>) for display in the Source row.</summary>
    public string SourceMethod { get; init; }

    // ═══════════════════════════════════════════════════════════════
    // DAG structure (set by DagBuilder.Build)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Indices of successor systems in the DAG.</summary>
    public int[] Successors { get; internal set; } = [];

    /// <summary>Number of predecessor systems that must complete before this system can execute.</summary>
    public int PredecessorCount { get; internal set; }

    /// <summary>
    /// Number of work chunks for Pipeline systems. Determines parallelism granularity.
    /// For CallbackSystem/QuerySystem systems this is always 1.
    /// Mutable because Pipeline chunk count may change per tick based on query result set size.
    /// </summary>
    public int TotalChunks { get; set; } = 1;

    // ═══════════════════════════════════════════════════════════════
    // Run conditions
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Optional predicate evaluated before dispatch. If it returns false, the system is skipped and its successors are dispatched immediately.
    /// Null means always run. Evaluated before input query to avoid View refresh cost on false predicate.
    /// </summary>
    public Func<bool> RunIf { get; init; }

    // ═══════════════════════════════════════════════════════════════
    // Overload parameters (stored for #201, not enforced yet)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>System runs every N ticks under normal load. Default: 1 (every tick).</summary>
    public int TickDivisor { get; set; } = 1;

    /// <summary>System runs every M ticks under overload. Default: 1 (every tick).</summary>
    public int ThrottledTickDivisor { get; set; } = 1;

    /// <summary>Whether this system can be shed (dropped entirely) under severe overload.</summary>
    public bool CanShed { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // Event queue references (indices into scheduler's queue array)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Indices of event queues this system produces (writes to).</summary>
    public int[] ProducesQueueIndices { get; internal set; } = [];

    /// <summary>Indices of event queues this system consumes (reads from).</summary>
    public int[] ConsumesQueueIndices { get; internal set; } = [];

    // ═══════════════════════════════════════════════════════════════
    // View input and change filter (#197)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Factory returning the View that provides this system's entity input. Null for CallbackSystems.</summary>
    public Func<ViewBase> InputFactory { get; set; }

    /// <summary>Component types for change-filtered reactive input. Null/empty means no filter (process all). OR logic: entity included if any filtered component was written.</summary>
    public Type[] ChangeFilterTypes { get; set; }

    /// <summary>
    /// Reactive skip predicate evaluated after <see cref="RunIf"/> passes. Returns true if the system should be skipped
    /// because its change-filtered input is empty (no dirty entities and no newly added entities).
    /// Set by <see cref="TyphonRuntime"/> at init — not configured by game code.
    /// </summary>
    public Func<bool> ReactiveSkip { get; set; }

    // ═══════════════════════════════════════════════════════════════
    // Parallel query dispatch
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// True if this QuerySystem uses parallel chunk dispatch across workers.
    /// When true, Execute is called once per chunk with a per-chunk Transaction and entity slice.
    /// Set by <see cref="RuntimeSchedule"/> from <see cref="SystemBuilder.Parallel"/>.
    /// </summary>
    public bool IsParallelQuery { get; internal set; }

    /// <summary>
    /// True if this parallel QuerySystem writes Versioned components (copy-on-write MVCC).
    /// When true, per-chunk Transactions are used instead of the optimized PointInTimeAccessor path.
    /// Set by <see cref="RuntimeSchedule"/> from <see cref="SystemBuilder.WritesVersioned"/>.
    /// </summary>
    public bool WritesVersioned { get; internal set; }

    /// <summary>
    /// Oversubscription factor for parallel chunk dispatch. The effective worker-cap on chunk count becomes
    /// <c>round(WorkerCount × ChunksPerWorker)</c> instead of <c>WorkerCount</c>. Default <c>1.0f</c> preserves the
    /// pre-knob behaviour (one chunk per worker).
    /// <para>
    /// Use values above 1.0 (e.g. 1.5, 2.0) on parallel systems where worker efficiency suffers because a single slow chunk
    /// holds back the critical path — extra chunks let fast workers steal more work via the existing dynamic <c>_nextChunk</c>
    /// loop in <see cref="DagScheduler"/>. Values below 1.0 are rejected at <see cref="RuntimeSchedule.Build"/>.
    /// </para>
    /// <para>
    /// The final chunk count is still capped by <c>ceil(entityCount / ParallelQueryMinChunkSize)</c>, so small populations
    /// won't proliferate trivial chunks. Cost trade-off: every extra chunk pays its own prepare/dispatch overhead — Versioned
    /// path also creates an extra <c>Transaction</c> per chunk.
    /// </para>
    /// Set by <see cref="RuntimeSchedule"/> from <see cref="SystemBuilder.ChunksPerWorker"/>.
    /// </summary>
    public float ChunksPerWorker { get; internal set; } = 1f;

    // ═══════════════════════════════════════════════════════════════
    // Issue #231: Tier dispatch filter
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Simulation-tier dispatch filter (issue #231). When set to anything other than <see cref="SimTier.All"/>, the parallel cluster partition is computed
    /// over the per-tier cluster list instead of <see cref="ArchetypeClusterState.ActiveClusterIds"/>, and this system only processes entities whose
    /// cluster lives in a cell with a matching tier. The default <see cref="SimTier.All"/> preserves the pre-#231 fast path.
    /// </summary>
    public SimTier TierFilter { get; internal set; } = SimTier.All;

    /// <summary>
    /// Cell-level amortization denominator (issue #231). When greater than 0, this system processes only <c>1/N</c> of the tier's clusters per tick,
    /// rotating through buckets as <c>tickNumber % N</c>. The callback's <see cref="TickContext.AmortizedDeltaTime"/> is set to <c>DeltaTime × CellAmortize</c>
    /// so integrations over the full elapsed time happen in one step. Must be paired with a non-<see cref="SimTier.All"/> <see cref="TierFilter"/>; amortizing
    /// the full cluster set without tier scoping is rejected at <c>RuntimeSchedule.Build</c>.
    /// </summary>
    public int CellAmortize { get; internal set; }

    /// <summary>
    /// When true, this parallel QuerySystem uses two-phase checkerboard dispatch (issue #234). Clusters are split into Red
    /// (<c>(cellX + cellY) % 2 == 0</c>) and Black sets, dispatched as two sequential parallel phases within one DAG node.
    /// No two adjacent cells are processed simultaneously, enabling conflict-free cross-cell reads/writes (e.g. pheromone diffusion).
    /// </summary>
    public bool IsCheckerboard { get; internal set; }
}
