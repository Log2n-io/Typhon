using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

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
/// The base <see cref="Execute"/> caps the local <c>DirtyCounter</c>s via <c>ReleaseDirtyMarks</c> at chunk end, then discards the ChangeSet.
/// Capping (not <c>SaveChanges</c>) is the correct lifecycle because WAL + checkpoint are mandatory (ADR-054): the checkpoint thread always drains the capped
/// pages.</para>
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

    // The phase's wall-clock SPAN: when Prepare began, and when the last chunk to finish did.
    private long _phaseStartTicks;
    private long _phaseEndTicks;

    /// <summary>
    /// Set by a derived <see cref="Prepare"/> BEFORE its own serial work, so the span covers that work too. Zero means "start the span in
    /// <see cref="Prepare"/>".
    /// </summary>
    /// <remarks>
    /// Needed because a derived Prepare does its serial step FIRST and calls <c>base.Prepare</c> last — the Migrate phase runs the sliced Prep tails, the
    /// IndexMassUpdate phase merges, sorts and leaf-snaps every staged batch. Starting the clock in the base would leave all of that outside the measurement,
    /// and for IndexMassUpdate that is the majority of the phase.
    /// </remarks>
    protected long PendingPhaseStart;

    /// <summary>Set by a derived <see cref="Prepare"/> to the ticks its own serial work took, before calling <c>base.Prepare</c>; published as
    /// <see cref="LastSerialPrepareTicks"/> there.</summary>
    protected long PendingSerialTicks;

    /// <summary>
    /// Wall-clock <see cref="Stopwatch"/> ticks from the start of <see cref="Prepare"/> to the moment the last chunk finished — how long the phase actually
    /// took, not how much CPU it consumed.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="TotalWallTicks"/>, and the distinction matters. That one SUMS per-chunk wall time because
    /// <see cref="LiveFenceCostModel"/> wants CPU per unit for its cost coefficients; a sum cannot express a speedup, and reading one as a latency gives
    /// nonsense in both directions — it falls with more chunks on a memory-stall-bound batch and rises with more chunks once per-chunk setup dominates.
    /// This is the number to quote for "how long did the phase take".
    /// </remarks>
    internal long PhaseSpanTicks => _phaseEndTicks > _phaseStartTicks ? _phaseEndTicks - _phaseStartTicks : 0;

    /// <summary>When the phase's <see cref="Prepare"/> began, in <see cref="Stopwatch"/> ticks; zero before the first tick.</summary>
    internal long PhaseStartTicks => _phaseStartTicks;

    /// <summary>When the phase's last chunk finished; zero when the phase dispatched no chunk this tick.</summary>
    internal long PhaseEndTicks => _phaseEndTicks;

    /// <summary>
    /// The serial part of the last <see cref="Prepare"/> that is NOT plan building: the Migrate phase's Prep tails (the sliced archetypes' drain-order
    /// sort among them since #910), the index phase's merge and leaf-snap, the EntityMap phase's merge and bucket partition. Zero for phases whose Prepare
    /// is the plan alone.
    /// </summary>
    /// <remarks>
    /// Exposed because a phase's span is Prepare plus dispatch, and only the dispatch scales with workers: at W = 8 the index phase's span was 0.69 ms
    /// against 1.18 ms of summed chunk time, which says the serial half is the larger one without saying by how much. This is the number that says.
    /// </remarks>
    internal long LastSerialPrepareTicks { get; private set; }

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
        _phaseStartTicks = PendingPhaseStart != 0 ? PendingPhaseStart : Stopwatch.GetTimestamp();
        PendingPhaseStart = 0;
        _phaseEndTicks = 0;
        LastSerialPrepareTicks = PendingSerialTicks;
        PendingSerialTicks = 0;

        _plan.Build(Phase, Engine, ctx.CostModel, ctx.WorkerCount, ctx.ChunkOversubscription);
        EnsureChunkArrays(_plan.ChunkCount);
        if (_plan.ChunkCount == 0)
        {
            // No chunk will end this phase, so end it here: the serial work Prepare just did — Migrate's tails, the index merge, Finalize's
            // heads for archetypes with nothing to emit — is fence time whether or not anything was dispatched after it, and PhaseSpanTicks and
            // TyphonRuntime.LastFenceWallTicks would otherwise read it as zero (#889 review).
            _phaseEndTicks = Stopwatch.GetTimestamp();
        }

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
            // The worker is part of the fence, so its writes are the legal ones. Without this every chunk's first index mutation would trip EW-01's guard,
            // which is the correct default: a thread is foreign until the fence says otherwise.
            using (Engine.EpochManager.FenceWindow.EnterWorker())
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
            // Cap DirtyCounter at 1 for every page touched by this chunk so the next checkpoint cycle can transition them to evictable (DC: 1 → 0). Matches
            // UnitOfWork.Dispose's cleanup. WAL + checkpoint are mandatory (ADR-054), so the checkpoint thread always drains these — no per-worker SaveChanges.
            if (chunkCs != null)
            {
                chunkCs.ReleaseDirtyMarks();
                Engine.MMF.ReturnChangeSet(chunkCs); // pool reuse — saves ~thousands of allocations/sec at 60 Hz
            }
        }
        var t1 = Stopwatch.GetTimestamp();
        _chunkWallTicks[k] = t1 - t0;
        _chunkUnitCount[k] = unitsInChunk;
        SetChunkLsn(k, localHighest);

        // The phase ends when its LAST chunk does, whichever that turns out to be. CAS rather than a plain store: chunks finish concurrently and a plain
        // max-then-store would let a later finisher lose to an earlier one.
        long prevEnd;
        do
        {
            prevEnd = Volatile.Read(ref _phaseEndTicks);
            if (t1 <= prevEnd)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _phaseEndTicks, t1, prevEnd) != prevEnd);
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

    // One crossing list per PrepSlice work item, pooled across ticks (#886 lead D): a slice files its list with the archetype and the tail drains and
    // clears it, so the list must outlive the chunk and cannot be per-worker. Indexed by the item's position in the plan, which is unique per tick.
    // Sized HERE, on the driver, before any worker runs — a worker growing a shared array while another indexes it is the race the dirty buffers'
    // comment above warns about. The per-chunk cursor turns (chunk, i-th item) into that plan position without threading an index through DispatchItem.
    private List<MigrationRequest>[] _crossingsPool = [];
    private int[] _itemCursor = [];

    protected override int Prepare(FenceContext ctx)
    {
        // The head is Prep work and is timed as such — the same way Migrate's Prepare claims the sliced Prep tails (#886 lead D).
        PendingPhaseStart = Stopwatch.GetTimestamp();
        Engine.PrepareArchetypeFenceHeads(ctx.WorkerCount);
        var chunkCount = base.Prepare(ctx);
        var itemCount = PlanForTest.ItemCount;
        if (_crossingsPool.Length < itemCount)
        {
            Array.Resize(ref _crossingsPool, Math.Max(itemCount, _crossingsPool.Length * 2));
        }

        for (var i = 0; i < itemCount; i++)
        {
            _crossingsPool[i] ??= [];
        }

        if (_itemCursor.Length < chunkCount)
        {
            Array.Resize(ref _itemCursor, Math.Max(chunkCount, _itemCursor.Length * 2));
        }

        return chunkCount;
    }

    protected override void OnBeforeChunk(int chunkIndex) => _itemCursor[chunkIndex] = 0;

    protected override long DispatchItem(int chunkIndex, in FenceWorkItem item, ChangeSet changeSet)
    {
        switch (item.Kind)
        {
            case FenceWorkKind.ArchetypePrep:
                Engine.PrepareArchetypeFence(ArchetypeRegistry.GetMetadata((ushort)item.TargetId), Context.TickNumber, changeSet);
                return 0;
            case FenceWorkKind.PrepSlice:
                var planIndex = PlanForTest.ChunkStart[chunkIndex] + _itemCursor[chunkIndex]++;
                Engine.RunPrepSlice(ArchetypeRegistry.GetMetadata((ushort)item.TargetId), item.SliceStart, item.SliceCount, changeSet, _crossingsPool[planIndex]);
                return 0;
            default:
                return 0;
        }
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

    /// <summary>Ticks the last Prepare spent in the #886 Prep tails — which since #910 include the sliced archetypes' drain-order sort.</summary>
    internal long LastTailTicks { get; private set; }

    // The three per-chunk sorts OnAfterChunk runs, in Stopwatch ticks, one padded slot of SortTicksStride longs per chunk: each worker writes its own
    // chunk's slot and no two slots share a cache line, so the timing costs no RMW on a line every worker contends (MD-03). Cleared and sized in
    // Prepare; summed by the driver after the barrier through ChunkSortTicks — worker CPU, not span, since the chunks run in parallel.
    private const int SortTicksStride = 8;
    private long[] _chunkSortTicks = new long[16 * SortTicksStride];
    private int _lastChunkCount;

    /// <summary>Last tick's per-chunk sort CPU summed over its chunks: the index runs, the EntityMap runs, the dirty-delta grouping.</summary>
    internal (long IndexSort, long MapSort, long DirtySort) ChunkSortTicks
    {
        get
        {
            long index = 0;
            long map = 0;
            long dirty = 0;
            for (var c = 0; c < _lastChunkCount; c++)
            {
                var slot = c * SortTicksStride;
                index += _chunkSortTicks[slot];
                map += _chunkSortTicks[slot + 1];
                dirty += _chunkSortTicks[slot + 2];
            }

            return (index, map, dirty);
        }
    }

    protected override int Prepare(FenceContext ctx)
    {
        PendingPhaseStart = Stopwatch.GetTimestamp();

        // #886 lead D: the serial tail of every archetype whose Prep ran as slices — crossings concatenated in slice order, buffers reset, then ⑥ ⑦,
        // the drain-order sort (#910) and ⑧ exactly as the atomic path runs them. Single-threaded by construction; inside the fence window like a chunk,
        // because the repair planner it runs opens cluster accessors and may allocate.
        var tailChangeSet = Engine.MMF.RentChangeSet();
        try
        {
            using (Engine.EpochManager.FenceWindow.EnterWorker())
            using (EpochGuard.Enter(Engine.EpochManager))
            {
                Engine.PrepareArchetypeFenceTails(ctx.TickNumber, tailChangeSet);
            }
        }
        finally
        {
            tailChangeSet.ReleaseDirtyMarks();
            Engine.MMF.ReturnChangeSet(tailChangeSet);
        }

        LastTailTicks = Stopwatch.GetTimestamp() - PendingPhaseStart;
        PendingSerialTicks = LastTailTicks;

        // The queue arrives here already in drain order. The destination-cell sort the slice planner carves on used to run at this point; #910 moved it
        // into the Prep tail (ArchetypeClusterState.OrderDrainAndMeasureArrivals), where it runs on the serial fence too and before the pre-size. Its cost
        // is the PrepSortMs sub-span now.
        var states = Engine._archetypeStates;

        var chunkCount = base.Prepare(ctx);
        EnsureChunkDirtyBuffers(chunkCount);

        // Size and clear this tick's index-update staging for the same reason the dirty buffers are sized here: a worker that had to grow a shared array
        // would race every other worker doing the same, and that is exactly the bug the dirty-buffer comment above records.
        if (states != null)
        {
            for (int aid = 0; aid < states.Length; aid++)
            {
                states[aid]?.ClusterState?.IndexUpdates?.BeginTick(chunkCount);
                states[aid]?.ClusterState?.EntityMapUpdates?.BeginTick(chunkCount);
                DecideEntityMapPath(states[aid], ctx.EntityMapBulkMinEntriesPerBucket);
            }
        }

        return chunkCount;
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
        // Marks the thread for the duration of the slice so the shared per-archetype arrays refuse to REALLOCATE under it — see the PER-ARCHETYPE ARRAY
        // GROWTH banner in ArchetypeClusterState. Cleared in a finally: a slice that throws still leaves the thread to the pool, and a stale flag would
        // turn the next legitimate serial grow on that thread into a spurious failure.
        ArchetypeClusterState.EnterMigrateSlice();
        try
        {
            Engine.ExecuteMigrationsSlice(meta, item.SliceStart, item.SliceCount, changeSet, buffer, chunkIndex);
        }
        finally
        {
            ArchetypeClusterState.ExitMigrateSlice();
        }
        return 0;
    }

    /// <summary>
    /// Sorts this chunk's staged index-update runs, on the worker that produced them, before it leaves the chunk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is where the IndexMassUpdate phase's sort now happens, and moving it here is the difference between a phase that scales and one that does
    /// not.</b> Sorting the whole batch in that phase's <c>Prepare</c> is <c>O(n log n)</c> on one thread: measured at 100 000 migrants it was ~6.3 ms
    /// against ~0.5 ms of parallel apply, an 88 % serial fraction that Amdahl capped at ~1.15x however many workers the phase was given. Each chunk's buffer
    /// is owned exclusively by the worker running it, so sorting it here needs no synchronisation and rides a parallel region that was already open; the
    /// phase is then left with an <c>O(n log W)</c> merge of W sorted runs.
    /// </para>
    /// <para>
    /// Chunks that staged nothing cost a bounds check per field. The archetype scan is over every cluster-eligible archetype rather than only the ones this
    /// chunk touched, because a chunk's work items may span several and the dirty-delta bucket beside it is grouped by archetype only after the fact.
    /// </para>
    /// </remarks>
    private void SortStagedIndexRuns(int chunkIndex)
    {
        var states = Engine._archetypeStates;
        if (states == null)
        {
            return;
        }

        for (var aid = 0; aid < states.Length; aid++)
        {
            var clusterState = states[aid]?.ClusterState;
            var staging = clusterState?.IndexUpdates;
            if (staging == null || staging.FieldCount == 0 || clusterState.IndexSlots == null)
            {
                continue;
            }

            for (var fieldId = 0; fieldId < staging.FieldCount; fieldId++)
            {
                var run = staging.ChunkSpan(chunkIndex, fieldId);
                if (run.Length == 0)
                {
                    continue;
                }

                var fieldRef = staging.Field(fieldId);
                ref var field = ref clusterState.IndexSlots[fieldRef.SlotIndex].Fields[fieldRef.FieldIndex];
                field.Index.SortBulkEntries(run, staging.SortScratch(chunkIndex, run.Length), RadixCounts(chunkIndex), field.AllowMultiple);
            }
        }
    }

    /// <summary>
    /// Chooses, once per tick per archetype, between staging this tick's EntityMap patches for the bulk phase and applying them inline.
    /// </summary>
    /// <remarks>
    /// <b>The bulk path's gain is entries that share a bucket, and a small batch has none.</b> Expected entries per touched bucket is
    /// <c>migrants / bucketCount</c>; below the configured minimum the batch produces runs of one, amortises nothing, and still pays the staging, the sort,
    /// the merge, the partition and a phase barrier. Measured at both ends on <c>--fence-phase</c> — see
    /// <c>RuntimeOptions.EntityMapBulkMinEntriesPerBucket</c>, which carries the numbers.
    /// </remarks>
    private static void DecideEntityMapPath(ArchetypeEngineState state, float minEntriesPerBucket)
    {
        var clusterState = state?.ClusterState;
        if (clusterState == null)
        {
            return;
        }

        var buckets = state.EntityMap?.LiveBucketCount ?? 0;
        clusterState.UseBulkEntityMapUpdate = buckets <= 0 || clusterState.PendingMigrationCount >= (long)(minEntriesPerBucket * buckets);
    }

    /// <summary>
    /// Sorts this chunk's staged EntityMap patches by bucket, on the worker that produced them, before it leaves the chunk.
    /// </summary>
    /// <remarks>
    /// The twin of <see cref="SortStagedIndexRuns"/> and it exists for the same measured reason: sorting the whole batch in the consuming phase's
    /// <c>Prepare</c> is serial work that Amdahl caps the phase at, whereas each chunk's buffer is owned exclusively by this worker and can be sorted here
    /// with no synchronisation, inside a parallel region that is already open. The phase is then left with a merge of already-sorted runs.
    /// </remarks>
    private void SortStagedEntityMapRuns(int chunkIndex)
    {
        var states = Engine._archetypeStates;
        if (states == null)
        {
            return;
        }

        for (var aid = 0; aid < states.Length; aid++)
        {
            states[aid]?.ClusterState?.EntityMapUpdates?.SortChunk(chunkIndex, RadixCounts(chunkIndex));
        }
    }

    // Per-chunk worker-local accumulator for dirty-bit deltas. Pooled across ticks — never reallocated per system execution. List<T>.Clear() preserves
    // capacity, so steady-state allocates zero. Indexed by chunkIndex so workers running concurrent chunks never share a buffer.
    private List<DirtyBitDelta>[] _chunkDirtyBuffers
        = new List<DirtyBitDelta>[16];

    // Per-chunk scratch for the radix sorts a worker runs in OnAfterChunk: the histogram every one of them shares, and the dirty-delta grouping's
    // ping-pong partner. The histograms are sized with the dirty buffers, from Prepare, for the same reason they are; the delta scratch grows on the
    // worker, like the List beside it, and only when a bucket outgrows what its chunk has seen.
    private int[][] _chunkRadixCounts = new int[16][];
    private DirtyBitDelta[][] _chunkDirtyScratch = new DirtyBitDelta[16][];

    /// <summary>This chunk's radix histogram. Sized by <see cref="Prepare"/>; a worker only ever reads the slot.</summary>
    private Span<int> RadixCounts(int chunkIndex) => _chunkRadixCounts[chunkIndex];

    /// <summary>
    /// Size and populate the per-chunk buffer array for this tick's chunk count. Called ONLY from <see cref="Prepare"/>, which the scheduler runs
    /// single-threaded.
    /// </summary>
    /// <remarks>
    /// This used to be done on demand from <see cref="GetChunkDirtyBuffer"/>, i.e. from concurrent Migrate workers, with no synchronisation: two workers
    /// could each read the same array, <c>Array.Copy</c> it, and publish — the second store dropping the first's bucket. The chunk count reaches ~45 in the
    /// shapes this fence targets while the array starts at 16, so crossing the grow threshold is routine rather than pathological. The lost bucket is
    /// currently benign, because the worker that created it still holds its own reference and flushes it in <see cref="OnAfterChunk"/> — but only that
    /// detail makes it benign, and the plain reference store is also unordered against the <c>Array.Copy</c> on arm64. Sizing here removes the grow from
    /// the worker path entirely rather than relying on the flush shape to stay as it is.
    /// </remarks>
    private void EnsureChunkDirtyBuffers(int chunkCount)
    {
        if (chunkCount > _chunkDirtyBuffers.Length)
        {
            var size = Math.Max(chunkCount, _chunkDirtyBuffers.Length * 2);
            var grown = new List<DirtyBitDelta>[size];
            Array.Copy(_chunkDirtyBuffers, grown, _chunkDirtyBuffers.Length);
            _chunkDirtyBuffers = grown;

            var grownCounts = new int[size][];
            Array.Copy(_chunkRadixCounts, grownCounts, _chunkRadixCounts.Length);
            _chunkRadixCounts = grownCounts;

            var grownScratch = new DirtyBitDelta[size][];
            Array.Copy(_chunkDirtyScratch, grownScratch, _chunkDirtyScratch.Length);
            _chunkDirtyScratch = grownScratch;

            _chunkSortTicks = new long[size * SortTicksStride];   // cleared below; last tick's values were read at its end
        }

        // The four per-chunk arrays grow in lockstep above; a future edit to one of them would leave a worker indexing an empty span.
        Debug.Assert(_chunkRadixCounts.Length == _chunkDirtyBuffers.Length && _chunkDirtyScratch.Length == _chunkDirtyBuffers.Length
            && _chunkSortTicks.Length == _chunkDirtyBuffers.Length * SortTicksStride, "per-chunk arrays out of step");

        for (int k = 0; k < chunkCount; k++)
        {
            _chunkDirtyBuffers[k] ??= new List<DirtyBitDelta>(256);
            _chunkRadixCounts[k] ??= new int[RadixSort.Buckets];
        }

        _chunkSortTicks.AsSpan(0, chunkCount * SortTicksStride).Clear();
        _lastChunkCount = chunkCount;
    }

    /// <summary>
    /// This chunk's delta buffer. Never grows the backing array — <see cref="Prepare"/> has already sized and populated it for every chunk this tick
    /// dispatches, so a worker only ever reads an element. An out-of-range index means the plan and the buffer array disagree, which is a defect rather
    /// than something to paper over with a lazy grow, so it is left to throw.
    /// </summary>
    private List<DirtyBitDelta> GetChunkDirtyBuffer(int chunkIndex) => _chunkDirtyBuffers[chunkIndex];

    protected override void OnBeforeChunk(int chunkIndex)
    {
        var bucket = _chunkDirtyBuffers.Length > chunkIndex ? _chunkDirtyBuffers[chunkIndex] : null;
        bucket?.Clear(); // preserves capacity — no realloc steady-state
    }

    protected override void OnAfterChunk(int chunkIndex)
    {
        var t0 = Stopwatch.GetTimestamp();
        SortStagedIndexRuns(chunkIndex);
        var t1 = Stopwatch.GetTimestamp();
        SortStagedEntityMapRuns(chunkIndex);
        var t2 = Stopwatch.GetTimestamp();
        var ticksSlot = chunkIndex * SortTicksStride;
        _chunkSortTicks[ticksSlot] = t1 - t0;
        _chunkSortTicks[ticksSlot + 1] = t2 - t1;

        var bucket = _chunkDirtyBuffers.Length > chunkIndex ? _chunkDirtyBuffers[chunkIndex] : null;
        if (bucket == null || bucket.Count == 0)
        {
            return;
        }

        // Group by archetypeId so we take each archetype's _finalizeLock exactly once. Typical AntHill tick has one spatial archetype → one sort pass + one
        // lock acquisition. Sort is in-place on the chunk's buffer. The radix sort's bound pass sees the one-archetype tick as all-equal keys and returns
        // after one read per entry, where the delegate List.Sort it replaced (#891) still ran its n log n; a ushort key needs at most two passes otherwise.
        var scratch = _chunkDirtyScratch[chunkIndex];
        if (scratch == null || scratch.Length < bucket.Count)
        {
            scratch = new DirtyBitDelta[Math.Max(bucket.Count, Math.Max(256, (scratch?.Length ?? 0) * 2))];
            _chunkDirtyScratch[chunkIndex] = scratch;
        }

        RadixSort.Sort<DirtyBitDelta, DirtyDeltaArchetypeKey>(CollectionsMarshal.AsSpan(bucket), scratch, RadixCounts(chunkIndex));

        _chunkSortTicks[ticksSlot + 2] = Stopwatch.GetTimestamp() - t2;

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

    /// <summary>The dirty-delta grouping's key: the archetype id, unsigned already.</summary>
    private readonly struct DirtyDeltaArchetypeKey : IRadixKey<DirtyBitDelta>
    {
        public static ulong Key(in DirtyBitDelta item) => item.ArchetypeId;
    }
}

/// <summary>
/// Applies one archetype's staged EntityMap location patches, one bucket-range slice per worker (#872 step 7, §5.4/§5.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Its own phase, between IndexMassUpdate and AabbRefresh.</b> §5.5 gives the EntityMap "the same treatment, different axis": the index side partitions by
/// KEY range snapped to leaf edges, this one by BUCKET range. Neither partition can be expressed in the other's terms, and mixing two work kinds into one
/// plan would leave the planner unable to cost them apart.
/// </para>
/// <para>
/// <b>The H1 fold and the orphan rollback happen here, not in Migrate.</b> Migration used to branch on <c>TryUpdateInPlace</c>'s return value; staging the
/// patch means the verdict arrives one phase later. <c>NoteClusterBorn</c>/<c>NoteClusterDied</c> are CAS-based, so folding from a bucket-partitioned worker
/// is safe, and the rollback is three <c>Interlocked.And</c>s plus one store to a destination slot the migrant owns exclusively — it never needed Migrate's
/// cell-disjointness. What the deferral does change is documented at the staging call site in <c>ExecuteMigrations</c>.
/// </para>
/// </remarks>
internal sealed class FenceEntityMapUpdateExecSystem : FencePhaseExecSystemBase
{
    public const string SystemName = "FenceEntityMapUpdate";

    public FenceEntityMapUpdateExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override FencePhase Phase => FencePhase.EntityMapUpdate;

    protected override void Configure(SystemBuilder<FenceContext> b) => b
        .Name(SystemName)
        .ChunkedParallel(1)
        .After(FenceIndexMassUpdateExecSystem.SystemName);

    protected override ChangeSet CreateChunkChangeSet() => Engine.MMF.RentChangeSet();

    /// <summary>Merges each archetype's per-chunk runs and partitions the result by bucket range.</summary>
    /// <remarks>
    /// Single-threaded by construction: only the worker that decrements the last IndexMassUpdate dependency to zero reaches a phase's Prepare.
    /// </remarks>
    protected override int Prepare(FenceContext ctx)
    {
        // Before the merge and partition below, so the phase's span covers the serial work rather than reporting a phase several times faster than it is.
        PendingPhaseStart = Stopwatch.GetTimestamp();

        var states = Engine._archetypeStates;
        if (states != null)
        {
            for (var aid = 0; aid < states.Length; aid++)
            {
                var state = states[aid];
                var staging = state?.ClusterState?.EntityMapUpdates;
                if (staging == null)
                {
                    continue;
                }

                // #872 step 11: charged to the archetype whose migrations staged the work, so the parallel path's cost model sees the same phases the
                // serial one does. This loop is per-archetype, so the attribution is exact even though the method walks every archetype.
                var prepStart = Stopwatch.GetTimestamp();
                staging.ClearPrepared();
                var desiredParts = Math.Max(1, ctx.WorkerCount);
                var count = staging.MergeAndPartition(desiredParts);
                if (count == 0)
                {
                    Interlocked.Add(ref state.ClusterState.LastTickMigrationApplyTicks, Stopwatch.GetTimestamp() - prepStart);
                    continue;
                }

                var parts = state.EntityMap.PartitionByBucketRuns<EntityLocationUpdate, ClusterLocationBulkUpdater>(
                    staging.Prepared.AsSpan(0, count), desiredParts, staging.Boundaries);
                staging.SetPartCount(parts);
                Interlocked.Add(ref state.ClusterState.LastTickMigrationApplyTicks, Stopwatch.GetTimestamp() - prepStart);
            }
        }

        PendingSerialTicks = Stopwatch.GetTimestamp() - PendingPhaseStart;
        return base.Prepare(ctx);
    }

    protected override long DispatchItem(int chunkIndex, in FenceWorkItem item, ChangeSet changeSet)
    {
        if (item.Kind != FenceWorkKind.EntityMapUpdateSlice)
        {
            return 0;
        }

        var state = Engine._archetypeStates[item.TargetId];
        var clusterState = state?.ClusterState;
        var staging = clusterState?.EntityMapUpdates;
        if (staging == null || staging.PreparedCount == 0)
        {
            return 0;
        }

        var applyStart = Stopwatch.GetTimestamp();
        try
        {
            var slice = staging.Prepared.AsSpan(item.SliceStart, item.SliceCount);
            var accessor = state.EntityMap.Segment.CreateChunkAccessor(changeSet);
            try
            {
                // This slice's buckets belong to it alone — the partition advances every cut to a bucket change — so no two workers are ever inside one bucket
                // chunk, which is what makes the per-bucket latch uncontended rather than merely correct.
                state.EntityMap.UpdateValuesBulk<EntityLocationUpdate, ClusterLocationBulkUpdater>(slice, ref accessor);
            }
            finally
            {
                accessor.Dispose();
            }

            ApplyVisibilityAndOrphans(clusterState, slice, changeSet);
        }
        finally
        {
            // #872 step 11. Summed across every worker that took a slice, exactly as the Migrate phase sums its own — CPU, not span.
            Interlocked.Add(ref clusterState.LastTickMigrationApplyTicks, Stopwatch.GetTimestamp() - applyStart);
        }

        return 0;
    }

    /// <summary>
    /// Folds each applied migrant's TSNs into its destination cluster, and rolls back the destination slot of any migrant the map no longer holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>H1.</b> A migration moves an entity between clusters of one archetype without changing its <c>BornTSN</c>, so the destination cluster's visibility
    /// summary has to absorb it — otherwise a cluster that was "clean" silently acquires an entity younger than a live reader's snapshot and the scan skips
    /// the probe that would have hidden it. The DIED side has no pre-fold at all: a migrated entity carrying a tombstone is only discovered here, and
    /// <c>VisibilityUnknown</c> is what restores the sticky deny for a cluster holding a set occupancy bit for a dead entity.
    /// </para>
    /// <para>
    /// <b>The orphan case should be unreachable, and is reported loudly rather than assumed away.</b> It means the entity left the EntityMap between the
    /// Migrate phase's occupancy check and this phase — which inside the tick fence requires a mutation <c>EW-01</c> forbids and <c>ExclusiveWindow</c> now
    /// throws on. The destination slot is still rolled back, because leaving it set would keep a ghost visible to spatial queries.
    /// </para>
    /// </remarks>
    private void ApplyVisibilityAndOrphans(ArchetypeClusterState clusterState, Span<EntityLocationUpdate> slice, ChangeSet changeSet)
    {
        for (var i = 0; i < slice.Length; i++)
        {
            ref var entry = ref slice[i];
            if (!entry.Found)
            {
                clusterState.RollbackOrphanedDestinationSlot(entry.DstChunkId, entry.DstSlot, entry.EntityKey, changeSet);
                continue;
            }

            clusterState.NoteClusterBorn(entry.DstChunkId, entry.ObservedBornTsn);
            if (entry.ObservedDiedTsn != 0)
            {
                clusterState.NoteClusterDied(entry.DstChunkId, ArchetypeClusterState.VisibilityUnknown);
            }
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

    /// <remarks>
    /// <b>The <c>.After(EntityMapUpdate)</c> edge is load-bearing, not a chain reorder.</b> #872 step 7 deferred the H1 DIED-side fold into that phase, so
    /// between Migrate and EntityMapUpdate a destination cluster holds a SET occupancy bit for a tombstoned migrant with no <c>VisibilityUnknown</c>
    /// recorded against it. The born side has no such gap — the claim folds <c>NextFreeId</c>, an upper bound, before publishing the bit. Nothing inside the
    /// fence reads visibility today, and this edge is what keeps that true; moving AabbRefresh earlier would make the gap observable.
    /// </remarks>
    protected override void Configure(SystemBuilder<FenceContext> b) => b
        .Name(SystemName)
        .ChunkedParallel(1)
        .After(FenceEntityMapUpdateExecSystem.SystemName);

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

    /// <summary>Runs the serial Finalize head for every archetype whose emit will be sliced (#889), before the plan is built from what it decided.</summary>
    /// <remarks>
    /// Single-threaded by construction, like every phase Prepare. Inside the fence window and an epoch scope for the same reason the Migrate tails are: the
    /// head frees drained clusters and refits promoted cell trees, work a chunk does under both.
    /// </remarks>
    protected override int Prepare(FenceContext ctx)
    {
        PendingPhaseStart = Stopwatch.GetTimestamp();
        using (Engine.EpochManager.FenceWindow.EnterWorker())
        using (EpochGuard.Enter(Engine.EpochManager))
        {
            Engine.PrepareArchetypeFinalizeHeads(ctx.TickNumber, ctx.WorkerCount);
        }

        PendingSerialTicks = Stopwatch.GetTimestamp() - PendingPhaseStart;
        return base.Prepare(ctx);
    }

    protected override long DispatchItem(int chunkIndex, in FenceWorkItem item, ChangeSet changeSet) =>
        item.Kind switch
        {
            FenceWorkKind.ArchetypeFinalize => Engine.FinalizeArchetypeFence(
                ArchetypeRegistry.GetMetadata((ushort)item.TargetId),
                Context.TickNumber,
                null) // no ChangeSet
            ,
            FenceWorkKind.FinalizeEmitSlice => Engine.EmitArchetypeFenceSlice(
                ArchetypeRegistry.GetMetadata((ushort)item.TargetId),
                Context.TickNumber,
                item.SliceStart,
                item.SliceCount),
            _ => 0
        };
}

/// <summary>
/// Phase 2.5 — applies every indexed field's staged value updates, one partitioning descent per leaf-snapped key range (#872 step 6, §5.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>It sits between Migrate and AabbRefresh, with a barrier either side, and both barriers matter.</b> Migrate is what stages the entries, so this cannot
/// start before Migrate has finished; and until this has finished, every migrated entity's index entry still points at its OLD cluster location, so nothing
/// downstream may read the index.
/// </para>
/// <para>
/// <b>The serial half lives in <see cref="Prepare"/>, and only because it has to.</b> Merging the per-chunk staging buffers, sorting each field's batch by
/// key and snapping the part boundaries to leaf edges all need a chunk accessor and an epoch scope, and <c>FenceWorkPlan.Build</c> has neither. The sort is
/// the part to watch: the staged batch arrives in destination-cell order, not key order, so this adds an O(n log n) pass the design assumed away ("the batch
/// is already sorted by key"). It is measured as its own line rather than folded into the phase total.
/// </para>
/// <para>
/// <b>No ChangeSet override is needed for the leaf writes themselves</b> — the descent dirties the index pages through the accessor this system opens per
/// work item, which carries the chunk's local ChangeSet exactly as Prep and Migrate do.
/// </para>
/// </remarks>
internal sealed class FenceIndexMassUpdateExecSystem : FencePhaseExecSystemBase
{
    public const string SystemName = "FenceIndexMassUpdate";

    public FenceIndexMassUpdateExecSystem(DatabaseEngine engine) : base(engine) { }

    protected override FencePhase Phase => FencePhase.IndexMassUpdate;

    protected override void Configure(SystemBuilder<FenceContext> b) => b
        .Name(SystemName)
        .ChunkedParallel(1)
        .After(FenceMigrateExecSystem.SystemName);

    protected override ChangeSet CreateChunkChangeSet() => Engine.MMF.RentChangeSet();

    /// <summary>
    /// Merge, sort and leaf-snap each field's staged batch, then let the base class turn the resulting parts into work items.
    /// </summary>
    /// <remarks>
    /// Single-threaded by construction: only the worker that decrements the last Migrate dependency to zero reaches a phase's Prepare. That is also what makes
    /// it safe to write the staging's prepared state here without synchronisation.
    /// </remarks>
    protected override int Prepare(FenceContext ctx)
    {
        // Before the merge / sort / leaf-snap below, not after: that serial work is the majority of this phase and leaving it outside the span would report
        // a phase several times faster than it is.
        PendingPhaseStart = Stopwatch.GetTimestamp();

        var states = Engine._archetypeStates;
        if (states != null)
        {
            using (EpochGuard.Enter(Engine.EpochManager))
            {
                for (var aid = 0; aid < states.Length; aid++)
                {
                    var clusterState = states[aid]?.ClusterState;
                    var staging = clusterState?.IndexUpdates;
                    if (staging == null || staging.FieldCount == 0)
                    {
                        continue;
                    }

                    // #872 step 11: charged per archetype. This serial half is "the majority of this phase" by the comment above, so a cost model that
                    // measured only DispatchItem would see a fraction of what the index actually costs.
                    // W parts, not W × oversubscription: measured on Matrix M at W = 8 (#889), sixteen parts left the index span identical at every
                    // point (136 vs 136 µs at 25 % moving) — the apply is ~90 µs of parallel work, and a part's dispatch overhead is what the better
                    // balance would have bought back. Refuted; do not re-add without a new measurement.
                    var prepStart = Stopwatch.GetTimestamp();
                    PrepareArchetype(clusterState, staging, Math.Max(1, ctx.WorkerCount));
                    Interlocked.Add(ref clusterState.LastTickMigrationApplyTicks, Stopwatch.GetTimestamp() - prepStart);
                }
            }
        }

        PendingSerialTicks = Stopwatch.GetTimestamp() - PendingPhaseStart;
        return base.Prepare(ctx);
    }

    private static void PrepareArchetype(ArchetypeClusterState clusterState, IndexUpdateStaging staging, int desiredParts)
    {
        staging.ClearPrepared();

        for (var fieldId = 0; fieldId < staging.FieldCount; fieldId++)
        {
            var stagedBytes = staging.StagedBytes(fieldId);
            if (stagedBytes == 0)
            {
                continue;
            }

            var fieldRef = staging.Field(fieldId);
            ref var field = ref clusterState.IndexSlots[fieldRef.SlotIndex].Fields[fieldRef.FieldIndex];
            var tree = field.Index;
            var multi = field.AllowMultiple;
            var stride = tree.BulkEntryStride(multi);

            // The runs arrived sorted from the Migrate workers, so this is an O(n log W) merge rather than an O(n log n) sort.
            var merged = staging.MergeSortedRuns(fieldId, stride, tree, multi, out var byteCount);
            var entryCount = byteCount / stride;
            if (entryCount == 0)
            {
                continue;
            }

            var boundaries = staging.RentBoundaries(fieldId, desiredParts);

            var accessor = tree.Segment.CreateChunkAccessor();
            try
            {
                var parts = tree.PartitionBulkEntries(merged.AsSpan(0, byteCount), multi, desiredParts, boundaries, ref accessor);
                staging.SetPrepared(fieldId, byteCount, stride, boundaries, parts);
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    protected override long DispatchItem(int chunkIndex, in FenceWorkItem item, ChangeSet changeSet)
    {
        if (item.Kind != FenceWorkKind.IndexUpdateSlice)
        {
            return 0;
        }

        var clusterState = Engine._archetypeStates[item.TargetId]?.ClusterState;
        var staging = clusterState?.IndexUpdates;
        if (staging == null)
        {
            return 0;
        }

        var fieldRef = staging.Field(item.FieldId);
        ref var field = ref clusterState.IndexSlots[fieldRef.SlotIndex].Fields[fieldRef.FieldIndex];
        var tree = field.Index;
        var multi = field.AllowMultiple;
        var stride = staging.Stride(item.FieldId);
        var buffer = staging.Prepared(item.FieldId);

        var applyStart = Stopwatch.GetTimestamp();
        var accessor = tree.Segment.CreateChunkAccessor(changeSet);
        try
        {
            // The slice is this worker's alone: the boundaries were snapped forward to leaf edges in Prepare, so no two slices of the same tree can reach the
            // same leaf, which is what lets the descent below run with no latch and no version validation.
            tree.ApplyBulkEntries(buffer.AsSpan(item.SliceStart * stride, item.SliceCount * stride), multi, ref accessor, out _);
        }
        finally
        {
            accessor.Dispose();
            // #872 step 11. Summed across workers AND across fields — a two-field archetype pays two descents per migrant and the model must see both.
            Interlocked.Add(ref clusterState.LastTickMigrationApplyTicks, Stopwatch.GetTimestamp() - applyStart);
        }

        return 0;
    }
}
