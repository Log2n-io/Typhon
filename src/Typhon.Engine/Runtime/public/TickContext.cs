using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using Typhon.Schema.Definition;
using System.Diagnostics;

namespace Typhon.Engine;

/// <summary>
/// Factory for side-transactions created from a <see cref="TickContext"/>. The <paramref name="discipline"/> selects the
/// commit discipline for SingleVersion-layout writes (<see cref="CommitDiscipline.TickFence"/> default, or
/// <see cref="CommitDiscipline.Commit"/> for zero-loss, atomic, commit-scoped writes).
/// </summary>
[PublicAPI]
public delegate Transaction SideTransactionFactory(DurabilityMode mode, CommitDiscipline discipline = CommitDiscipline.TickFence);

/// <summary>
/// Context passed to CallbackSystem and QuerySystem delegates during tick execution.
/// Provides a valid <see cref="Transaction"/> for entity operations and a factory for side-transactions.
/// </summary>
/// <remarks>
/// <para>
/// Each CallbackSystem/QuerySystem receives its own TickContext with a dedicated <see cref="Transaction"/>
/// created on the worker thread (respecting Transaction's single-thread affinity).
/// The Transaction is committed automatically after the system completes — systems must NOT commit or dispose it.
/// </para>
/// <para>
/// Pipeline systems do NOT receive TickContext — they use an <see cref="System.Action{T1,T2}"/> and access entity data
/// through Gather/Scatter pipelines (separate mechanism).
/// </para>
/// </remarks>
[PublicAPI]
public struct TickContext
{
    /// <summary>Monotonically increasing tick number (0-based).</summary>
    public long TickNumber { get; init; }

    /// <summary>Elapsed time in seconds since the previous tick. Zero on the first tick.</summary>
    public float DeltaTime { get; init; }

    /// <summary>
    /// Transaction for this system's entity operations (Spawn, Open, OpenMut, Query, etc.).
    /// Created on the current worker thread. Valid only during this system's execution.
    /// Do NOT Commit or Dispose — the scheduler manages the Transaction lifecycle.
    /// Null when running without a DatabaseEngine (standalone scheduler tests).
    /// </summary>
    public Transaction Transaction { get; init; }

    /// <summary>
    /// Per-worker EntityAccessor for parallel QuerySystems that do NOT write Versioned components.
    /// Provides Open/OpenMut with warm ChunkAccessor caches, zero per-entity dictionary overhead.
    /// Null when the system uses Transaction-based access (WritesVersioned=true or non-parallel systems).
    /// </summary>
    public EntityAccessor Accessor { get; init; }

    /// <summary>
    /// Filtered entity set for this system's execution.
    /// <list type="bullet">
    /// <item><description>CallbackSystem: empty (no entity input)</description></item>
    /// <item><description>QuerySystem/PipelineSystem without changeFilter: full View entity set</description></item>
    /// <item><description>QuerySystem/PipelineSystem with changeFilter: dirty entities ∪ Added (only entities whose filtered components were written since last tick)</description></item>
    /// </list>
    /// The backing array is pooled — do not hold references beyond the system's Execute scope.
    /// </summary>
    public IReadOnlyCollection<EntityId> Entities { get; init; }

    /// <summary>
    /// Event queues this system consumes. Null if the system has no consumed queues.
    /// Cast to <see cref="EventQueue{T}"/> and call <c>Drain(span)</c> to read events; size the span from <c>Count</c>, which a short span throws on.
    /// </summary>
    public EventQueueBase[] ConsumedQueues { get; init; }

    /// <summary>
    /// Creates a side-transaction with the specified durability mode.
    /// Side-transactions commit independently and are NOT visible to the main tick Transaction (snapshot isolation — the main Transaction's TSN is fixed at
    /// creation).
    /// The caller owns the returned Transaction and must Dispose it.
    /// </summary>
    /// <remarks>
    /// Use for economy-critical operations (trades, purchases, progression) that must be durable immediately, independent of the main tick's commit.
    /// Pass <see cref="CommitDiscipline.Commit"/> to make SingleVersion-layout writes zero-loss, atomic and commit-scoped (no revision chain).
    /// Null when running without a DatabaseEngine.
    /// </remarks>
    public SideTransactionFactory CreateSideTransaction { get; init; }

    /// <summary>
    /// Inclusive start index into <see cref="ClusterIds"/> for this worker's assigned cluster range. Used by cluster-native systems that iterate
    /// via <c>ctx.Accessor.GetClusterEnumerator&lt;TArch&gt;(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)</c> for 2-3 ns/entity performance.
    /// Default 0.
    /// </summary>
    /// <remarks>
    /// <para>Before issue #231 this range indexed directly into <c>ArchetypeClusterState.ActiveClusterIds</c>. After #231 it indexes
    /// into <see cref="ClusterIds"/>, which points at either the full <c>ActiveClusterIds</c> (for <see cref="SimTier.All"/> systems) or a per-tier cluster
    /// list (for tier-filtered systems). Game code that passed <c>ctx.StartClusterIndex</c> / <c>ctx.EndClusterIndex</c> to the old two-argument
    /// <c>GetClusterEnumerator(int, int)</c> overload must migrate to the new three-argument overload that takes <see cref="ClusterIds"/> explicitly.</para>
    /// <para>Default 0 (not -1) due to struct constraint. Check <c>EndClusterIndex &gt; StartClusterIndex</c> for validity — a zero range means not applicable
    /// (non-parallel, non-cluster, or entity-level dispatch).</para>
    /// </remarks>
    public int StartClusterIndex { get; init; }

    /// <summary>Exclusive end index into <see cref="ClusterIds"/> for this worker's assigned cluster range.</summary>
    /// <remarks>Default 0. Check <c>EndClusterIndex &gt; StartClusterIndex</c> for validity — a zero range means not applicable.</remarks>
    public int EndClusterIndex { get; init; }

    /// <summary>
    /// Source array for the <see cref="StartClusterIndex"/> / <see cref="EndClusterIndex"/> partition (issue #231).
    /// Points at <see cref="ArchetypeClusterState.ActiveClusterIds"/> for systems with no tier filter, or at a per-tier (or per-bucket, for <c>cellAmortize</c>)
    /// cluster list for tier-filtered systems. Null when the system has no cluster partition (non-parallel, non-cluster-eligible, or empty view).
    /// </summary>
    public int[] ClusterIds { get; init; }

    /// <summary>
    /// Elapsed time in seconds since the last tick this system processed this cell bucket (issue #231). Equal to <see cref="DeltaTime"/> when the system has
    /// no <c>cellAmortize</c>. For amortized systems, <c>AmortizedDeltaTime = DeltaTime × CellAmortize</c>, which is the effective integration step for
    /// movement, decay, or state-machine updates that happen once per amortization cycle.
    /// </summary>
    public float AmortizedDeltaTime { get; init; }

    /// <summary>
    /// Per-tier cost and entity count metrics from the previous tick (issue #234). Available to all systems — primarily consumed by <c>TierAssignment</c>
    /// <see cref="CallbackSystem"/> for adaptive tier boundary adjustment. Zero on the first tick (no previous-tick data).
    /// </summary>
    public TierBudgetMetrics TierBudgetMetrics { get; init; }

    /// <summary>
    /// Sentinel <see cref="WorkerId"/> for a context that is <b>not</b> executing on a scheduler worker — the runtime lifecycle hooks
    /// (<c>OnFirstTick</c>, <c>OnShutdown</c>), which run on the tick thread or the caller's thread rather than on a dispatched worker.
    /// </summary>
    /// <remarks>
    /// Deliberately negative so that indexing a per-worker array with it throws instead of silently aliasing worker 0 (#860). Code that indexes
    /// per-worker storage by <see cref="WorkerId"/> must either be unreachable from a lifecycle hook or handle this value explicitly.
    /// </remarks>
    public const int NonWorkerId = -1;

    /// <summary>
    /// Worker slot for the thread executing this system chunk. Use it to index per-worker data structures (per-worker scratch buffers, accumulators)
    /// without any synchronization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Range is <c>[0, WorkerCount]</c> — inclusive of the upper bound</b>, so per-worker arrays must be sized
    /// <see cref="DagScheduler.WorkerSlotCount"/>, not <c>WorkerCount</c>. Slots <c>[0, WorkerCount)</c> are the pool's worker threads; the extra slot
    /// <see cref="DagScheduler.DispatcherWorkerId"/> belongs to the dispatcher (timer) thread, which runs a system body when a skipped root chains its
    /// successor into <c>ExecuteInline</c>. The dispatcher slot is disjoint from every worker slot by construction, which is what makes it safe to write
    /// without synchronization — see <see cref="DagScheduler.DispatcherWorkerId"/> for why disjointness, not quiescence, is the guarantee.
    /// </para>
    /// <para>
    /// <b>Single-worker runtimes never produce the dispatcher slot.</b> <c>WorkerCount == 1</c> takes <c>ExecuteTickSingleThreaded</c>, which runs the
    /// whole tick inline on the tick thread and stamps slot 0 — truthfully, since that thread is the one and only worker. The dispatcher slot arises
    /// only from multi-worker track dispatch. Size per-worker arrays by <see cref="DagScheduler.WorkerSlotCount"/> regardless, so the same code is
    /// correct under both.
    /// </para>
    /// <para>
    /// Contexts handed to the runtime lifecycle hooks (<c>OnFirstTick</c>, <c>OnShutdown</c>) carry <see cref="NonWorkerId"/> instead — those run on the
    /// tick thread or on whichever thread called <c>Shutdown</c>, outside system dispatch entirely, so no slot belongs to them.
    /// </para>
    /// <para>
    /// <b>Withdrawn in #860:</b> this used to be documented as "for non-parallel systems, always 0", and four of the six construction sites left it at
    /// the default 0 — so every thread claimed slot 0 and per-worker partitioning aliased instead of separating. A non-parallel system now reports
    /// whichever pool worker picked it up that tick, which varies from tick to tick. Code that read <c>perWorker[0]</c> on the strength of the old
    /// wording must aggregate across all slots instead.
    /// </para>
    /// </remarks>
    public int WorkerId { get; init; }

    /// <summary>
    /// Opens a writer on <paramref name="queue"/> bound to this context's worker slot (#861). Resolve it once per system body and push through it.
    /// </summary>
    /// <remarks>
    /// This is the only supported way to produce events: it supplies <see cref="WorkerId"/> for you, so a producer cannot accidentally write another
    /// worker's segment. Calling it from a lifecycle hook throws — those contexts carry <see cref="NonWorkerId"/> and own no segment.
    /// </remarks>
    /// <typeparam name="T">The event type.</typeparam>
    /// <param name="queue">The queue to produce into, or null for a no-op writer — the <c>queue?.Push(...)</c> idiom, kept working.
    /// The system should declare the queue via <c>WritesEvents</c> / <c>produces:</c>.</param>
    public readonly EventWriter<T> Writer<T>(EventQueue<T> queue) => queue == null ? default : queue.GetWriter(WorkerId);

    /// <summary>
    /// Debug-only guard that <see cref="WorkerId"/> is a usable worker slot on every context that reaches user system code (#860).
    /// </summary>
    /// <param name="slotCount"><see cref="DagScheduler.WorkerSlotCount"/> — worker threads plus the dispatcher slot.</param>
    /// <param name="systemName">System whose context is being validated; named in the assertion message.</param>
    /// <remarks>
    /// Compiled out entirely in Release. It exists because the failure it catches is silent: an unassigned <c>init</c> property leaves
    /// <see cref="WorkerId"/> at 0, so every worker claims slot 0 and per-worker partitioning aliases instead of throwing. <see cref="NonWorkerId"/> is
    /// rejected here on purpose — lifecycle-hook contexts carry it, but they never reach system dispatch.
    /// </remarks>
    [Conditional("DEBUG")]
    internal readonly void DebugValidateWorkerId(int slotCount, string systemName) => DebugValidateWorkerSlot(WorkerId, slotCount, systemName);

    /// <summary>
    /// Slot-value overload of <see cref="DebugValidateWorkerId"/>, for dispatch paths that build their context inside a <c>try</c> and so must
    /// validate the slot before entering it — otherwise the enclosing handler converts the failure into an ordinary system failure.
    /// </summary>
    /// <param name="workerSlot">The slot about to be stamped onto a context.</param>
    /// <param name="slotCount"><see cref="DagScheduler.WorkerSlotCount"/> — worker threads plus the dispatcher slot.</param>
    /// <param name="systemName">System whose dispatch is being validated; named in the assertion message.</param>
    [Conditional("DEBUG")]
    internal static void DebugValidateWorkerSlot(int workerSlot, int slotCount, string systemName)
    {
        // Branch first, format second: Debug.Assert(bool, string) evaluates its message eagerly, so an interpolated string there would allocate on
        // every system dispatch and every chunk in Debug — the configuration the whole test suite runs in. Same reasoning as CLAUDE.md's
        // [LoggerMessage] rule.
        if (workerSlot >= 0 && workerSlot < slotCount)
        {
            return;
        }

        // Throw, do NOT Debug.Fail: Fail terminates the process and is not catchable, so in Debug — the configuration the whole suite runs in — a
        // single mis-stamped slot would abort the test host and lose every fixture with no attribution, instead of producing one red test.
        throw new InvalidOperationException(
            $"TickContext.WorkerId ({workerSlot}) is outside [0, {slotCount}) for system '{systemName}'. "
            + "Every dispatch path must stamp the executing worker's slot onto the context (#860).");
    }

    /// <summary>
    /// Zero-based chunk index for chunked-parallel systems (e.g. <see cref="ChunkedCallbackSystem"/>).
    /// Range: [0, <see cref="ChunkCount"/>). For non-chunked systems, always 0. Use to compute the per-chunk slice of arbitrary work:
    /// <c>start = ChunkIndex × totalSize / ChunkCount</c>.
    /// </summary>
    public int ChunkIndex { get; init; }

    /// <summary>
    /// Total number of chunks for chunked-parallel systems. For non-chunked systems, always 1.
    /// For a <see cref="ChunkedCallbackSystem"/>, the value passed to <see cref="SystemBuilder.ChunkedParallel"/> (or its Prepare's). For a parallel
    /// QuerySystem, the live dispatch's count, which <see cref="RuntimeOptions.CostBasedChunking"/> sets from measured cost: it can change from one tick
    /// to the next and reach twice <c>round(WorkerCount × ChunksPerWorker)</c>, so size nothing by that ceiling.
    /// </summary>
    public int ChunkCount { get; init; }

    /// <summary>
    /// Game-facing accessor for the engine's spatial grid (issue #232). Provides cell tier assignment, coordinate conversion, and multi-observer
    /// helpers (<see cref="SpatialGridAccessor.SetCellTierMin"/>, <see cref="SpatialGridAccessor.ResetAllTiers"/>,
    /// <see cref="SpatialGridAccessor.SetTierInAABB"/>). Check <see cref="SpatialGridAccessor.IsValid"/> before use — false when no grid is configured.
    /// </summary>
    public SpatialGridAccessor SpatialGrid { get; init; }

    /// <summary>
    /// The realm policy this tick (Realms D4): each realm's state and divisor, and the delta time a cluster of a realm integrates over —
    /// <c>ctx.Realms.DeltaTime(cluster.Realm)</c> in a system that runs over realms simulated at a divisor.
    /// </summary>
    public RealmsAccessor Realms { get; init; }

    /// <summary>
    /// What a system reads this tick's client commands through, and answers them with — <c>ctx.Subscriptions.Commands&lt;MoveIntent&gt;()</c>
    /// (design/Subscriptions/01-model.md § 7). <see langword="null"/> when the application declared no subscriptions, and on a context built without a
    /// <see cref="TyphonRuntime"/> at all (standalone scheduler tests).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is the runtime's own object, not a per-tick wrapper.</b> <c>SubscriptionsRuntime</c> creates exactly one at <c>Start</c> and never replaces it,
    /// so this is one reference copied into the context — no allocation per tick, per system or per chunk, and nothing to dispose. Everything it exposes
    /// describes the tick the context belongs to, because the Engine-Pre drain refills the buffers it reads before the application's track runs.
    /// </para>
    /// <para>
    /// <b>Null is a real answer, and the reason to check it is the same as for <see cref="Transaction"/>.</b> A runtime whose application never touched
    /// <c>TyphonRuntime.Subscriptions</c> builds no session table, no pools and no command registry — "an unused subsystem costs a database exactly nothing" —
    /// so there is no empty instance to hand out that would not be a 544 KiB allocation for nobody.
    /// </para>
    /// </remarks>
    public SubscriptionsCommands Subscriptions { get; init; }
}
