namespace Typhon.Profiler;

/// <summary>
/// Scan strategy the ECS query engine chose for a given <c>Execute</c>/<c>Count</c>/<c>Any</c> call. Mirrors the old string-valued
/// <c>typhon.ecs.query.scan_mode</c> tag exactly — each numeric value corresponds to one of the five scan modes the old code could attach to an
/// <see cref="System.Diagnostics.Activity"/>.
/// </summary>
/// <remarks>
/// Wire stability: numeric values are part of the <c>.typhon-trace</c> file format. Never renumber. New modes must be appended.
/// </remarks>
public enum EcsQueryScanMode : byte
{
    /// <summary>No entities — the archetype mask matched nothing.</summary>
    Empty = 0,

    /// <summary>Full scan of every matching archetype.</summary>
    Broad = 1,

    /// <summary>Targeted scan using field predicates via the pipeline executor.</summary>
    Targeted = 2,

    /// <summary>Targeted scan that uses per-cluster index lookups.</summary>
    TargetedCluster = 3,

    /// <summary>Spatial-index-driven scan (R-Tree candidates filtered by archetype mask).</summary>
    Spatial = 4,
}

/// <summary>
/// Path the view refresh took this call. Covers the three mutually-exclusive branches in <c>EcsView.Refresh</c>.
/// </summary>
public enum EcsViewRefreshMode : byte
{
    /// <summary>Full re-query (no field evaluators, pull-only view).</summary>
    Pull = 0,

    /// <summary>Incremental delta-drain succeeded — applied delta entries without a full re-query.</summary>
    Incremental = 1,

    /// <summary>Delta ring buffer overflowed — fell back to a full re-query.</summary>
    Overflow = 2,
}

/// <summary>
/// Identifies a phase within a tick's lifecycle. Used by the scheduler to drive execution and surfaced on the wire
/// as the <c>u8 phase</c> payload of <see cref="TraceEventKind.PhaseStart"/> / <see cref="TraceEventKind.PhaseEnd"/>
/// instant records.
/// </summary>
/// <remarks>
/// Wire stability: numeric values are part of the <c>.typhon-trace</c> file format. Never renumber. New phases must be appended.
/// </remarks>
public enum TickPhase : byte
{
    /// <summary>System dispatch phase — the DAG scheduler is running systems.</summary>
    SystemDispatch = 0,

    /// <summary>UoW flush — deferred writes becoming durable (WAL write).</summary>
    UowFlush = 1,

    /// <summary>Write tick fence — dirty bitmap snapshot, shadow entry processing, spatial index update.</summary>
    WriteTickFence = 2,

    /// <summary>Subscription output — refresh published Views, compute deltas, push to clients.</summary>
    OutputPhase = 3,

    /// <summary>Tier index rebuild — rebuild per-archetype tier cluster indexes at tick start.</summary>
    TierIndexRebuild = 4,

    /// <summary>Dormancy sweep — advance sleep counters, transition idle clusters.</summary>
    DormancySweep = 5,
}

/// <summary>
/// What triggered a checkpoint cycle. Carried as the <c>reason</c> byte on <see cref="TraceEventKind.CheckpointCycle"/> spans.
/// </summary>
public enum CheckpointReason : byte
{
    /// <summary>Periodic timer wake (CheckpointIntervalMs elapsed).</summary>
    Periodic = 0,

    /// <summary>Explicit <c>DatabaseEngine.ForceCheckpoint</c> call (page cache backpressure or user-initiated).</summary>
    Forced = 1,

    /// <summary>Shutdown — final checkpoint before process exit.</summary>
    Shutdown = 2,
}
