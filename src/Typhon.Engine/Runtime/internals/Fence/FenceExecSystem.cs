using System;
using System.Diagnostics;

namespace Typhon.Engine.Internals;

/// <summary>
/// Base class for the four chained fence-phase exec systems (<see cref="FencePrepExecSystem"/>, <see cref="FenceMigrateExecSystem"/>,
/// <see cref="FenceAabbRefreshExecSystem"/>, <see cref="FenceFinalizeExecSystem"/>). Each derived class owns its own <see cref="FenceWorkPlan"/> instance,
/// built lazily by <see cref="Prepare"/> from the shared <see cref="FenceContext"/>, and dispatches the chunk's work items by <see cref="FenceWorkKind"/> in
/// <see cref="Execute"/>.
///
/// <para>All four systems share the same <c>ChunkedParallel(1)</c> placeholder shape — the runtime overrides <c>RuntimeChunkCount</c> per dispatch from the
/// per-phase plan's <see cref="FenceWorkPlan.ChunkCount"/>.</para>
///
/// <para><b>Per-chunk ChangeSet ownership.</b> The shared UoW <see cref="ChangeSet"/> is single-thread-affine
/// (<c>claude/design/Transactions/transaction-overview.md §3.2</c>) — it cannot be threaded into parallel workers. Each chunk that needs page-dirty tracking
/// creates a LOCAL ChangeSet via <see cref="CreateChunkChangeSet"/> (overridden by Prep / Migrate; returns null for Finalize which doesn't dirty pages).
/// The base <see cref="Execute"/> caps the local <c>DirtyCounter</c>s via <c>ReleaseExcessDirtyMarks</c> at chunk end, then discards the ChangeSet.
/// The parallel fence path is WAL-mode only — TickDriver gates dispatch on <c>WalManager != null</c>, so the WAL-less <c>SaveChanges</c> path is unreachable
/// here.</para>
/// </summary>
internal abstract class FencePhaseExecSystemBase : ChunkedCallbackSystem<FenceContext>
{
    protected readonly DatabaseEngine Engine;

    // Per-chunk highest-LSN slot — only the Finalize system publishes WAL, so only it reads back HighestLsn. The Prep and Migrate systems leave their slots at zero.
    private long[] _chunkHighestLsn = new long[16];

    // Per-chunk wall-time + unit-count totals consumed by LiveFenceCostModel after dispatch returns. Stopwatch ticks (not microseconds) — TyphonRuntime
    // converts at update time. Grown together with _chunkHighestLsn in Prepare.
    private long[] _chunkWallTicks = new long[16];
    private long[] _chunkUnitCount = new long[16];

    /// <summary>The per-phase work plan owned by this system, rebuilt every tick inside <see cref="Prepare"/>.</summary>
    private readonly FenceWorkPlan _plan = new();

    /// <summary>Test/diagnostic accessor for the last plan built by this system.</summary>
    internal FenceWorkPlan PlanForTest => _plan;

    /// <summary>Identifies which phase this system represents — used by Plan.Build to emit the right work items.</summary>
    protected abstract FencePhase Phase { get; }

    protected FencePhaseExecSystemBase(DatabaseEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Engine = engine;
    }

    /// <summary>
    /// Default fence-phase Prepare: builds the per-phase plan from the shared <see cref="FenceContext"/> and returns the dynamic chunk count. Derived classes
    /// may override (e.g. FenceMigrate inserts the destCellKey sort first).
    /// </summary>
    protected override int Prepare(FenceContext ctx)
    {
        _plan.Build(Phase, Engine, ctx.CostModel, ctx.WorkerCount, ctx.ChunkOversubscription);
        EnsureChunkArrays(_plan.ChunkCount);
        return _plan.ChunkCount;
    }

    private void EnsureChunkArrays(int chunkCount)
    {
        if (_chunkHighestLsn.Length < chunkCount)
        {
            int grown = Math.Max(chunkCount, _chunkHighestLsn.Length * 2);
            _chunkHighestLsn = new long[grown];
            _chunkWallTicks = new long[grown];
            _chunkUnitCount = new long[grown];
        }
        for (int k = 0; k < chunkCount; k++)
        {
            _chunkHighestLsn[k] = 0;
            _chunkWallTicks[k] = 0;
            _chunkUnitCount[k] = 0;
        }
    }

    internal long HighestLsn
    {
        get
        {
            long max = 0;
            for (int k = 0; k < _plan.ChunkCount; k++)
            {
                long v = _chunkHighestLsn[k];
                if (v > max)
                {
                    max = v;
                }
            }
            return max;
        }
    }

    /// <summary>Sum of <see cref="Stopwatch.GetTimestamp"/> deltas across all chunks of the last dispatch.
    /// Fed to <see cref="LiveFenceCostModel.UpdatePhase"/> by TyphonRuntime after the fence sub-DAG completes.</summary>
    internal long TotalWallTicks
    {
        get
        {
            long sum = 0;
            for (int k = 0; k < _plan.ChunkCount; k++)
            {
                sum += _chunkWallTicks[k];
            }

            return sum;
        }
    }

    /// <summary>Sum of <c>FenceWorkItem.UnitCount</c> across every item dispatched by the last run (entities for MigrationApply, clusters for AabbRefreshSlice,
    /// zero for archetype-atomic kinds).</summary>
    internal long TotalUnitCount
    {
        get
        {
            long sum = 0;
            for (int k = 0; k < _plan.ChunkCount; k++)
            {
                sum += _chunkUnitCount[k];
            }

            return sum;
        }
    }

    private void SetChunkLsn(int chunkIndex, long lsn) => _chunkHighestLsn[chunkIndex] = lsn;

    /// <summary>
    /// Override in derived classes that need page-dirty tracking. Returns a fresh local ChangeSet to be used for every accessor / segment alloc inside this
    /// chunk's work items. Base returns null (no tracking — Finalize).
    /// </summary>
    protected virtual ChangeSet CreateChunkChangeSet() => null;

    /// <summary>Override to pre-initialize per-chunk state (e.g. clear a buffer). Called inside the EpochGuard.</summary>
    protected virtual void OnBeforeChunk(int chunkIndex) { }

    /// <summary>Override to flush per-chunk state (e.g. drain a buffer under a lock). Called inside the EpochGuard before the ChangeSet is released.
    /// Receives the chunk index that just finished.</summary>
    protected virtual void OnAfterChunk(int chunkIndex) { }

    protected override void Execute(TickContext ctx)
    {
        var plan = _plan;
        int k = ctx.ChunkIndex;
        if (k < 0 || k >= plan.ChunkCount)
        {
            return;
        }

        int start = plan.ChunkStart[k];
        int count = plan.ChunkItemCnt[k];
        if (count == 0)
        {
            return;
        }

        long localHighest = 0;
        long unitsInChunk = 0;
        var chunkCs = CreateChunkChangeSet();
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            using (EpochGuard.Enter(Engine.EpochManager))
            {
                OnBeforeChunk(k);
                for (int i = 0; i < count; i++)
                {
                    ref var item = ref plan.Items[start + i];
                    long lsn = DispatchItem(k, in item, chunkCs);
                    if (lsn > localHighest)
                    {
                        localHighest = lsn;
                    }

                    unitsInChunk += item.UnitCount;
                }
                OnAfterChunk(k);
            }
        }
        finally
        {
            // WAL-mode contract: cap DirtyCounter at 1 for every page touched by this chunk so the next checkpoint cycle can transition them to evictable
            // (DC: 1 → 0). Matches UnitOfWork.Dispose's WAL-mode cleanup. RunParallelFence gates the whole parallel path on WalManager != null, so we never
            // need the WAL-less SaveChanges path here.
            if (chunkCs != null)
            {
                chunkCs.ReleaseExcessDirtyMarks();
                Engine.MMF.ReturnChangeSet(chunkCs); // pool reuse — saves ~thousands of allocations/sec at 60 Hz
            }
        }
        _chunkWallTicks[k] = Stopwatch.GetTimestamp() - t0;
        _chunkUnitCount[k] = unitsInChunk;
        SetChunkLsn(k, localHighest);
    }

    protected abstract long DispatchItem(int chunkIndex, in FenceWorkItem item, ChangeSet changeSet);
}

/// <summary>
/// Phase 1 — runs <see cref="DatabaseEngine.PrepareArchetypeFence"/> on each <see cref="FenceWorkKind.ArchetypePrep"/> item assigned to this chunk.
/// The local ChangeSet is threaded into <c>ProcessClusterShadowEntries</c>'s B+Tree index segment accessors.
/// </summary>
internal sealed class FencePrepExecSystem : FencePhaseExecSystemBase
{
    public const string SystemName = "FencePrep";

    public FencePrepExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override FencePhase Phase => FencePhase.Prep;

    protected override void Configure(SystemBuilder<FenceContext> b) => b
        .Name(SystemName)
        .ChunkedParallel(1); // RuntimeChunkCount overridden per-dispatch from plan.ChunkCount

    protected override ChangeSet CreateChunkChangeSet() => Engine.MMF.RentChangeSet();

    protected override long DispatchItem(int chunkIndex, in FenceWorkItem item, ChangeSet changeSet)
    {
        if (item.Kind != FenceWorkKind.ArchetypePrep)
        {
            return 0;
        }

        var meta = ArchetypeRegistry.GetMetadata((ushort)item.TargetId);
        Engine.PrepareArchetypeFence(meta, Context.TickNumber, changeSet);
        return 0;
    }
}

/// <summary>
/// Phase 2 — applies a contiguous slice of one archetype's <c>PendingMigrations</c> per <see cref="FenceWorkKind.MigrationApply"/> item. Multiple slices per
/// fat archetype enable parallel apply. The local ChangeSet is threaded into the cluster / transient / idx / EntityMap accessors and the <c>AllocateChunk</c>
/// growth path inside <c>ClaimSlotInCell</c>.
/// </summary>
internal sealed class FenceMigrateExecSystem : FencePhaseExecSystemBase
{
    public const string SystemName = "FenceMigrate";

    public FenceMigrateExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override FencePhase Phase => FencePhase.Migrate;

    protected override void Configure(SystemBuilder<FenceContext> b) => b
        .Name(SystemName)
        .ChunkedParallel(1)
        .After(FencePrepExecSystem.SystemName);

    protected override ChangeSet CreateChunkChangeSet() => Engine.MMF.RentChangeSet();

    protected override int Prepare(FenceContext ctx)
    {
        // Inter-phase serial step (was in RunParallelFence): sort each archetype's pending migrations by destCellKey so the slice planner can carve
        // cell-disjoint ranges. Runs single-threaded by construction (only one worker decrements the last predecessor dep to zero and reaches this Prepare).
        var states = Engine._archetypeStates;
        if (states != null)
        {
            for (int aid = 0; aid < states.Length; aid++)
            {
                var st = states[aid]?.ClusterState;
                if (st == null || st.PendingMigrationCount <= 0)
                {
                    continue;
                }

                st.SortPendingMigrationsByDestCellKey();
            }
        }
        return base.Prepare(ctx);
    }

    protected override long DispatchItem(int chunkIndex, in FenceWorkItem item, ChangeSet changeSet)
    {
        if (item.Kind != FenceWorkKind.MigrationApply)
        {
            return 0;
        }

        var meta = ArchetypeRegistry.GetMetadata((ushort)item.TargetId);
        // Each migration's dirty-bit clear/set goes into this chunk's local buffer (review false-sharing fix).
        // OnAfterChunk flushes the buffer under each touched archetype's _finalizeLock.
        var buffer = GetChunkDirtyBuffer(chunkIndex);
        Engine.ExecuteMigrationsSlice(meta, item.SliceStart, item.SliceCount, changeSet, buffer);
        return 0;
    }

    // Per-chunk worker-local accumulator for dirty-bit deltas. Pooled across ticks — never reallocated per system execution. List<T>.Clear() preserves
    // capacity, so steady-state allocates zero. Indexed by chunkIndex so workers running concurrent chunks never share a buffer.
    private System.Collections.Generic.List<DirtyBitDelta>[] _chunkDirtyBuffers
        = new System.Collections.Generic.List<DirtyBitDelta>[16];

    private System.Collections.Generic.List<DirtyBitDelta> GetChunkDirtyBuffer(int chunkIndex)
    {
        if (chunkIndex >= _chunkDirtyBuffers.Length)
        {
            var grown = new System.Collections.Generic.List<DirtyBitDelta>[Math.Max(chunkIndex + 1, _chunkDirtyBuffers.Length * 2)];
            Array.Copy(_chunkDirtyBuffers, grown, _chunkDirtyBuffers.Length);
            _chunkDirtyBuffers = grown;
        }
        var bucket = _chunkDirtyBuffers[chunkIndex];
        if (bucket == null)
        {
            bucket = new System.Collections.Generic.List<DirtyBitDelta>(256);
            _chunkDirtyBuffers[chunkIndex] = bucket;
        }
        return bucket;
    }

    protected override void OnBeforeChunk(int chunkIndex)
    {
        var bucket = _chunkDirtyBuffers.Length > chunkIndex ? _chunkDirtyBuffers[chunkIndex] : null;
        bucket?.Clear(); // preserves capacity — no realloc steady-state
    }

    protected override void OnAfterChunk(int chunkIndex)
    {
        var bucket = _chunkDirtyBuffers.Length > chunkIndex ? _chunkDirtyBuffers[chunkIndex] : null;
        if (bucket == null || bucket.Count == 0)
        {
            return;
        }

        // Group by archetypeId so we take each archetype's _finalizeLock exactly once. Typical AntHill tick has one spatial archetype → one sort pass + one
        // lock acquisition. Sort is in-place on the chunk's buffer.
        bucket.Sort(static (a, b) => a.ArchetypeId.CompareTo(b.ArchetypeId));

        int i = 0;
        int n = bucket.Count;
        while (i < n)
        {
            ushort aid = bucket[i].ArchetypeId;
            int j = i + 1;
            while (j < n && bucket[j].ArchetypeId == aid)
            {
                j++;
            }

            // bucket[i..j) all target archetype `aid`. Apply under that archetype's lock.
            Engine.FlushDirtyBitDeltas(aid, bucket, i, j - i);
            i = j;
        }
    }
}

/// <summary>
/// Phase 3 — applies a contiguous slice of one archetype's AABB recompute per <see cref="FenceWorkKind.AabbRefreshSlice"/> item. Multiple slices per fat
/// archetype enable per-archetype parallel AABB refresh. No ChangeSet needed: the recompute writes to managed per-cluster arrays (<c>ClusterAabbs</c>) and
/// per-cell index SoA slots (<c>CellSpatialIndex.UpdateAt</c>) — neither is page-backed. The rare outlier-guard path (<c>FlagOutliersForMigration →
/// EnqueueMigration</c>) serializes the per-archetype mutation via <c>_finalizeLock</c> inside <c>ArchetypeClusterState</c>.
/// </summary>
internal sealed class FenceAabbRefreshExecSystem : FencePhaseExecSystemBase
{
    public const string SystemName = "FenceAabbRefresh";

    public FenceAabbRefreshExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override FencePhase Phase => FencePhase.AabbRefresh;

    protected override void Configure(SystemBuilder<FenceContext> b) => b
        .Name(SystemName)
        .ChunkedParallel(1)
        .After(FenceMigrateExecSystem.SystemName);

    protected override long DispatchItem(int chunkIndex, in FenceWorkItem item, ChangeSet changeSet)
    {
        if (item.Kind != FenceWorkKind.AabbRefreshSlice)
        {
            return 0;
        }

        var meta = ArchetypeRegistry.GetMetadata((ushort)item.TargetId);
        Engine.RecomputeArchetypeAabbsSlice(meta, item.SliceStart, item.SliceCount);
        return 0;
    }
}

/// <summary>
/// Phase 4 — runs <see cref="DatabaseEngine.FinalizeArchetypeFence"/> on each <see cref="FenceWorkKind.ArchetypeFinalize"/> item; returns the per-archetype
/// highest WAL LSN so the runtime can fold it into <c>_lastTickFenceLSN</c>. Finalize reads cluster bytes via accessors without a ChangeSet (no dirty
/// marking needed for WAL emit), so this system has no per-chunk ChangeSet to manage.
/// </summary>
internal sealed class FenceFinalizeExecSystem : FencePhaseExecSystemBase
{
    public const string SystemName = "FenceFinalize";

    public FenceFinalizeExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override FencePhase Phase => FencePhase.Finalize;

    protected override void Configure(SystemBuilder<FenceContext> b) => b
        .Name(SystemName)
        .ChunkedParallel(1)
        .After(FenceAabbRefreshExecSystem.SystemName);

    protected override long DispatchItem(int chunkIndex, in FenceWorkItem item, ChangeSet changeSet)
    {
        if (item.Kind != FenceWorkKind.ArchetypeFinalize)
        {
            return 0;
        }

        var meta = ArchetypeRegistry.GetMetadata((ushort)item.TargetId);
        return Engine.FinalizeArchetypeFence(meta, Context.TickNumber, null); // Finalize doesn't touch ChangeSet
    }
}
