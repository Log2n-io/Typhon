using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

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
/// Pipeline systems do NOT receive TickContext — they use <c>Action&lt;int, int&gt;</c> and access entity data
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
    /// Cast to <c>EventQueue&lt;T&gt;</c> and call <c>Drain(span)</c> or <c>AsSpan()</c> to read events.
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
    /// Null when running without a DatabaseEngine.
    /// </remarks>
    public Func<DurabilityMode, Transaction> CreateSideTransaction { get; init; }

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
    /// Zero-based worker index for the thread executing this system chunk. Range: [0, WorkerCount).
    /// For non-parallel systems, always 0. Use this to index into per-worker data structures
    /// (e.g., per-worker render buffers) without any synchronization.
    /// </summary>
    public int WorkerId { get; init; }

    /// <summary>
    /// Game-facing accessor for the engine's spatial grid (issue #232). Provides cell tier assignment, coordinate conversion, and multi-observer
    /// helpers (<see cref="SpatialGridAccessor.SetCellTierMin"/>, <see cref="SpatialGridAccessor.ResetAllTiers"/>,
    /// <see cref="SpatialGridAccessor.SetTierInAABB"/>). Check <see cref="SpatialGridAccessor.IsValid"/> before use — false when no grid is configured.
    /// </summary>
    public SpatialGridAccessor SpatialGrid { get; init; }
}
