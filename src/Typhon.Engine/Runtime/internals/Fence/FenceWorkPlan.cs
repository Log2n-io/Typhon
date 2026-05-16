using System;
using System.Buffers;
using System.Collections.Generic;
using JetBrains.Annotations;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-tick, per-phase fence work plan: a flat <see cref="FenceWorkItem"/> buffer partitioned into N chunks, one chunk per worker. Owned by
/// <c>TyphonRuntime</c> and rebuilt every tick — instances are reused tick over tick; internal arrays grow but never shrink.
///
/// <para>Phase Prep emits one <see cref="FenceWorkKind.ArchetypePrep"/> item per cluster-eligible archetype. Phase Migrate emits zero-or-more
/// <see cref="FenceWorkKind.MigrationApply"/> slices per archetype with pending migrations — multiple slices per fat archetype let workers apply migrations for
/// the SAME archetype concurrently. Phase Finalize emits one <see cref="FenceWorkKind.ArchetypeFinalize"/> item per cluster-eligible archetype whose Prep ran
/// a branch that needs finalize work (skips archetypes with branch path 0).</para>
///
/// <para>Bin-packing is FFD-by-cost into <c>ChunkCount</c> chunks, capped at <c>workerCount × chunkOversubscription</c>.</para>
/// </summary>
[PublicAPI]
internal sealed class FenceWorkPlan
{
    private const int InitialItemCapacity = 64;
    private const int MinMigrationSliceSize = 32;     // tiny migration batches stay on one worker (entity count)
    private const int MinAabbSliceClusters = 32;      // tiny AABB sets stay on one worker — floor in CLUSTER units, converted to words in BarrierOnly mode
    private const int BitmapBitsPerWord = 64;

    /// <summary>
    /// Lower bound on per-chunk expected wall time in µs.
    /// The chunk-count cap is <c>min(2 × workerCount × oversubscription, floor(totalCost / MinChunkCostUs))</c> — chunks below this floor are wasteful
    /// (wake-up + ChunkAccessor + EpochGuard overhead per dispatch is in the ~10-30µs range, so a 200µs floor keeps overhead under ~15%). Lets light workloads
    /// collapse to fewer chunks while heavy workloads scale to 2× the base cap. Empirical — refine if profiling shows otherwise.
    /// </summary>
    private const float MinChunkCostUs = 200f;

    /// <summary>
    /// Maximum chunks (or per-archetype slices) to emit given a cost budget. The result is
    /// <c>max(1, min(2 × workerCount × chunkOversubscription, floor(totalCost / MinChunkCostUs)))</c>:
    /// the 200µs floor keeps per-chunk dispatch overhead under control, and the <c>2 × workerCount × oversubscription</c> ceiling stops a huge archetype from
    /// emitting an unbounded number of tiny slices (each slice still pays wake-up + ChunkAccessor + EpochGuard cost). <paramref name="workerCount"/> and
    /// <paramref name="chunkOversubscription"/> are clamped to a minimum of 1.
    /// </summary>
    internal static int ComputeMaxChunks(float totalCost, int workerCount, int chunkOversubscription)
    {
        if (workerCount < 1)
        {
            workerCount = 1;
        }

        if (chunkOversubscription < 1)
        {
            chunkOversubscription = 1;
        }

        var costBased = (int)(totalCost / MinChunkCostUs);
        var cap = 2 * workerCount * chunkOversubscription;
        return Math.Max(1, Math.Min(cap, costBased));
    }

    private FenceWorkItem[] _items = new FenceWorkItem[InitialItemCapacity];
    private int[] _chunkStart = new int[16];
    private int[] _chunkItemCnt = new int[16];

    private readonly PriorityQueue<int, float> _heap = new();

    public FenceWorkItem[] Items => _items;
    public int ItemCount { get; private set; }
    public int[] ChunkStart => _chunkStart;
    public int[] ChunkItemCnt => _chunkItemCnt;
    public int ChunkCount { get; private set; }

    /// <summary>
    /// Build this phase's work plan. Single-threaded — called from TickDriver between user DAG completion and the per-phase parallel dispatch.
    /// </summary>
    public void Build(FencePhase phase, DatabaseEngine engine, LiveFenceCostModel costModel, int workerCount, int chunkOversubscription)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(costModel);
        if (workerCount < 1)
        {
            workerCount = 1;
        }

        if (chunkOversubscription < 1)
        {
            chunkOversubscription = 1;
        }

        ItemCount = 0;
        ChunkCount = 0;

        switch (phase)
        {
            case FencePhase.Prep:
                EmitArchetypePrepItems(engine, costModel);
                break;
            case FencePhase.Migrate:
                EmitMigrationApplyItems(engine, costModel, workerCount, chunkOversubscription);
                break;
            case FencePhase.AabbRefresh:
                EmitAabbRefreshSliceItems(engine, costModel, workerCount, chunkOversubscription);
                break;
            case FencePhase.Finalize:
                EmitArchetypeFinalizeItems(engine, costModel);
                break;
        }

        if (ItemCount == 0)
        {
            return;
        }

        ComputeChunkCountAndPack(workerCount, chunkOversubscription);
    }

    // ─── Phase Prep: one item per cluster-eligible archetype ─────────────────

    private void EmitArchetypePrepItems(DatabaseEngine engine, LiveFenceCostModel costModel)
    {
        var states = engine._archetypeStates;
        if (states == null)
        {
            return;
        }

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!meta.IsClusterEligible || meta.ArchetypeId >= states.Length)
            {
                continue;
            }

            var state = states[meta.ArchetypeId]?.ClusterState;
            if (state == null)
            {
                continue;
            }

            // Reset MigrationHint after snapshot (race-tolerant) — see legacy EmitArchetypeItems.
            // Ordering note: Prep runs on TickDriver AFTER the user DAG has completed (all user-system workers have joined), so the read here observes all
            // in-tick WriteSpatial increments. Callers that emit increments OUTSIDE the user DAG window (side-transaction, post-tick callback, async work) would
            // race and lose increments — not a supported pattern today, flagged here for future maintainers.
            var migHint = state.MigrationHint;
            state.MigrationHint = 0;

            var hasDirty = state.ClusterDirtyBitmap.HasDirty;
            var spatialCleanRefresh = !hasDirty && state.SpatialSlot.HasSpatialIndex && state.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic
                                      && state.ActiveClusterCount > 0 && state.ClusterSegment != null;

            var cost = ComputeArchetypeCost(meta, state, migHint, hasDirty, spatialCleanRefresh, costModel);
            AppendItem(new FenceWorkItem
            {
                Kind = FenceWorkKind.ArchetypePrep,
                TargetId = meta.ArchetypeId,
                Cost = cost,
            });
        }
    }

    // ─── Phase Migrate: zero-or-more slices per archetype with pending migrations ─

    private void EmitMigrationApplyItems(DatabaseEngine engine, LiveFenceCostModel costModel, int workerCount, int chunkOversubscription)
    {
        var states = engine._archetypeStates;
        if (states == null)
        {
            return;
        }

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!meta.IsClusterEligible || meta.ArchetypeId >= states.Length)
            {
                continue;
            }

            var state = states[meta.ArchetypeId]?.ClusterState;
            if (state == null)
            {
                continue;
            }

            var pendingCount = state.PendingMigrationCount;
            if (pendingCount <= 0)
            {
                continue;
            }

            // Per-archetype slice ceiling: scaled with archetype cost so a fat archetype produces enough slices to hit 200µs chunks. Same formula as the
            // global chunk-count cap.
            var archetypeCost = costModel.MigrationCost * pendingCount;
            var maxSlicesPerArchetype = ComputeMaxChunks(archetypeCost, workerCount, chunkOversubscription);

            var idealSliceSize = (pendingCount + maxSlicesPerArchetype - 1) / maxSlicesPerArchetype;
            var sliceSize = Math.Max(idealSliceSize, MinMigrationSliceSize);

            // PendingMigrations was sorted by destCellKey (TickDriver step before Migrate dispatch). Slice on cell boundaries: each slice owns a contiguous
            // range of destCellKeys and no two slices share a dest cell — this is what makes the dst-side ClusterClaim path "worker-exclusive" without per-cell
            // locking (review C-2 fix). Starting from the ideal index split, advance until destCellKey changes; if a single cell's migration block exceeds
            // sliceSize, the slice naturally grows to cover the whole block (one cell on one/ worker). The trailing partial slice gets whatever's left.
            var pending = state.PendingMigrations;
            var cursor = 0;
            while (cursor < pendingCount)
            {
                var idealEnd = Math.Min(cursor + sliceSize, pendingCount);
                var end = idealEnd;
                if (idealEnd < pendingCount)
                {
                    // Advance to the first index whose destCellKey differs from idealEnd-1's.
                    var boundaryKey = pending[idealEnd - 1].DestCellKey;
                    while (end < pendingCount && pending[end].DestCellKey == boundaryKey)
                    {
                        end++;
                    }
                }
                var count = end - cursor;
                AppendItem(new FenceWorkItem
                {
                    Kind = FenceWorkKind.MigrationApply,
                    TargetId = meta.ArchetypeId,
                    Cost = costModel.MigrationCost * count,
                    SliceStart = cursor,
                    SliceCount = count,
                    UnitCount = count,
                });
                cursor = end;
            }
        }
    }

    // ─── Phase AabbRefresh: zero-or-more slices per cluster-eligible Dynamic-spatial archetype ───
    //
    // Slicing axis differs by iteration mode (captured at plan time from ArchetypeClusterState.SpatialBarrierOnly):
    //   BarrierOnly → slice ClusterProcessBitmap by word range. SliceStart=startWord, SliceCount=wordCount.
    //   Legacy      → slice ActiveClusterIds by index range.    SliceStart=activeIdx, SliceCount=count.
    // The exec system passes the slice to RecomputeDirtyClusterAabbsSlice which interprets the slice axis based on the archetype's mode. The bookkeeping clear
    // (ClusterProcessBitmap, ClusterMigrationPendingSlots, ClusterShrinkPendingAxes) is deferred to FinalizeArchetypeFence — runs once per archetype, cheap.

    private void EmitAabbRefreshSliceItems(DatabaseEngine engine, LiveFenceCostModel costModel, int workerCount, int chunkOversubscription)
    {
        var states = engine._archetypeStates;
        if (states == null)
        {
            return;
        }

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!meta.IsClusterEligible || meta.ArchetypeId >= states.Length)
            {
                continue;
            }

            var state = states[meta.ArchetypeId]?.ClusterState;
            if (state == null)
            {
                continue;
            }

            // Skip archetypes whose Prep returned false (FenceBranchPath stayed 0) — Finalize already short-circuits.
            if (state.FenceBranchPath == 0)
            {
                continue;
            }

            // Skip non-Dynamic-spatial archetypes — RecomputeDirtyClusterAabbs is a no-op for them.
            if (!state.SpatialSlot.HasSpatialIndex || state.SpatialSlot.FieldInfo.Mode != SpatialMode.Dynamic)
            {
                continue;
            }

            if (state.ClusterSpatialIndexSlot == null || state.ClusterAabbs == null)
            {
                continue;
            }

            if (state.PerCellIndex == null || state.ClusterCellMap == null)
            {
                continue;
            }

            // Choose slicing axis: BarrierOnly iterates ClusterProcessBitmap by word; Legacy iterates ActiveClusterIds.
            int total;
            if (state.SpatialBarrierOnly && state.ClusterProcessBitmap != null)
            {
                total = state.ClusterProcessBitmap.Length;
            }
            else
            {
                total = state.ActiveClusterCount;
            }
            if (total <= 0)
            {
                continue;
            }

            // Slicing policy:
            //   BarrierOnly: 1 WORD per slice. Each slice carries ≤64 dirty bits ⇒ ≤64 cluster recomputes ⇒ ≤~150µs at typical AabbCost.
            //     Keeps per-item cost UNDER the 200µs bin-packer floor so the packer can subdivide work freely (it cannot split an atomic item).
            //     Empty-word slices have cost=0 and get aggregated with neighbours by FFD packing.
            //   Legacy: clusters per slice = MinAabbSliceClusters (32) — already in cluster units.
            int sliceSize;
            if (state.SpatialBarrierOnly && state.ClusterProcessBitmap != null)
            {
                sliceSize = 1; // 1 bitmap word
            }
            else
            {
                sliceSize = MinAabbSliceClusters;
            }
            var sliceCount = (total + sliceSize - 1) / sliceSize;
            if (sliceCount < 1)
            {
                sliceCount = 1;
            }

            for (var s = 0; s < sliceCount; s++)
            {
                var start = s * sliceSize;
                var count = Math.Min(sliceSize, total - start);
                if (count <= 0)
                {
                    break;
                }

                // Cost must be cluster-accurate (AabbCost is per-cluster µs). In BarrierOnly mode `count` is bitmap words — popcount to get cluster count.
                // In Legacy mode `count` already is cluster count.
                var clusterCount = state.CountClustersInAabbSlice(start, count);
                AppendItem(new FenceWorkItem
                {
                    Kind = FenceWorkKind.AabbRefreshSlice,
                    TargetId = meta.ArchetypeId,
                    Cost = costModel.AabbCost * clusterCount,
                    SliceStart = start,
                    SliceCount = count,
                    UnitCount = clusterCount,
                });
            }
        }
    }

    // ─── Phase Finalize: one item per cluster-eligible archetype with Prep work to finish ─
    //
    // Note: AABB recompute has moved out of Finalize into the FenceAabbRefresh phase. Finalize now does only the bookkeeping clear, dormancy sweep,
    // dirty-ring archive, ComponentTable flag propagation, and WAL emit.

    private void EmitArchetypeFinalizeItems(DatabaseEngine engine, LiveFenceCostModel costModel)
    {
        var states = engine._archetypeStates;
        if (states == null)
        {
            return;
        }

        foreach (var meta in ArchetypeRegistry.GetAllArchetypes())
        {
            if (!meta.IsClusterEligible || meta.ArchetypeId >= states.Length)
            {
                continue;
            }

            var state = states[meta.ArchetypeId]?.ClusterState;
            if (state == null)
            {
                continue;
            }

            // Skip archetypes whose Prep returned false (FenceBranchPath stayed 0).
            if (state.FenceBranchPath == 0)
            {
                continue;
            }

            // Cost = WAL hint only (AABB lives in the AabbRefresh phase now).
            var c = 0f;
            if (state.FenceEntryCount > 0)
            {
                c += costModel.ShadowCost * state.FenceEntryCount * 0.25f; // WAL-payload proxy
            }
            if (c < 0.5f)
            {
                c = 0.5f;
            }

            AppendItem(new FenceWorkItem
            {
                Kind = FenceWorkKind.ArchetypeFinalize,
                TargetId = meta.ArchetypeId,
                Cost = c,
            });
        }
    }

    private static float ComputeArchetypeCost(ArchetypeMetadata meta, ArchetypeClusterState state, int migHint, bool hasDirty, bool spatialCleanRefresh, 
        LiveFenceCostModel costModel)
    {
        var c = 0f;
        c += costModel.MigrationCost * migHint;
        if (hasDirty)
        {
            c += costModel.AabbCost * state.ActiveClusterCount;
            if (state.IndexSlots != null)
            {
                c += costModel.ShadowCost * state.ActiveClusterCount;
            }
            if (state.SpatialSlot.HasSpatialIndex && state.SpatialSlot.FieldInfo.Mode == SpatialMode.Dynamic)
            {
                c += costModel.SpatialCost * state.ActiveClusterCount;
            }
        }
        else if (spatialCleanRefresh)
        {
            c += costModel.SpatialCost * state.ActiveClusterCount;
        }
        if (c < 0.5f)
        {
            c = 0.5f;
        }

        return c;
    }

    /// <summary>Test hook — drives the packer with synthetic items. Returns ChunkCount after pack.</summary>
    internal int PackSyntheticForTest(float[] costs, int workerCount, int chunkOversubscription)
    {
        ItemCount = 0;
        ChunkCount = 0;
        for (var i = 0; i < costs.Length; i++)
        {
            AppendItem(new FenceWorkItem { Kind = FenceWorkKind.MigrationApply, Cost = costs[i] });
        }
        if (ItemCount == 0)
        {
            return 0;
        }

        ComputeChunkCountAndPack(workerCount, chunkOversubscription);
        return ChunkCount;
    }

    // ─── Bin-packing (FFD-by-cost) ──────────────────────────────────────────

    private void ComputeChunkCountAndPack(int workerCount, int chunkOversubscription)
    {
        var totalCost = 0f;
        var maxAtomicCost = 0f;
        for (var i = 0; i < ItemCount; i++)
        {
            var c = _items[i].Cost;
            totalCost += c;
            if (c > maxAtomicCost)
            {
                maxAtomicCost = c;
            }
        }

        // Target each chunk at exactly MinChunkCostUs (200µs) — maximize chunk count for jitter absorption. The 200µs floor protects against dispatch overhead
        // dominating; everything above flows into more chunks (more queued items per worker, peers pick up slack when a chunk runs long). Worker count is
        // irrelevant here — fewer workers just means more chunks serialize through them, but no chunk takes >200µs and one slow chunk doesn't stall the tick.
        var targetChunkCost = MinChunkCostUs;
        if (maxAtomicCost > targetChunkCost)
        {
            targetChunkCost = maxAtomicCost;
        }

        var chunkCount = (int)Math.Ceiling(totalCost / targetChunkCost);
        if (chunkCount < 1)
        {
            chunkCount = 1;
        }

        if (chunkCount > ItemCount)
        {
            chunkCount = ItemCount;
        }

        EnsureChunkArrays(chunkCount);
        for (var k = 0; k < chunkCount; k++)
        {
            _chunkStart[k] = 0;
            _chunkItemCnt[k] = 0;
        }
        ChunkCount = chunkCount;

        Array.Sort(_items, 0, ItemCount, FenceWorkItemCostDescComparer.Instance);

        // FFD with O(N log K) heap-based load tracking (review M-2): _chunkLoadAcc holds the running load per chunk. Dequeue lightest, append item, update load,
        // re-enqueue with new priority. Total: O(N log K) vs the prior O(N² ) GetChunkLoad scan.
        EnsureChunkLoadCapacity(chunkCount);
        for (var k = 0; k < chunkCount; k++)
        {
            _chunkLoadAcc[k] = 0f;
        }

        _heap.Clear();
        for (var k = 0; k < chunkCount; k++)
        {
            _heap.Enqueue(k, 0f);
        }

        var assignment = ArrayPool<int>.Shared.Rent(ItemCount);
        try
        {
            for (var i = 0; i < ItemCount; i++)
            {
                var k = _heap.Dequeue();
                assignment[i] = k;
                _chunkItemCnt[k]++;
                _chunkLoadAcc[k] += _items[i].Cost;
                _heap.Enqueue(k, _chunkLoadAcc[k]);
            }

            var running = 0;
            for (var k = 0; k < chunkCount; k++)
            {
                _chunkStart[k] = running;
                running += _chunkItemCnt[k];
                _chunkItemCnt[k] = 0;
            }

            var sortedCopy = ArrayPool<FenceWorkItem>.Shared.Rent(ItemCount);
            try
            {
                Array.Copy(_items, sortedCopy, ItemCount);
                for (var i = 0; i < ItemCount; i++)
                {
                    var k = assignment[i];
                    var writeIdx = _chunkStart[k] + _chunkItemCnt[k]++;
                    _items[writeIdx] = sortedCopy[i];
                }
            }
            finally
            {
                ArrayPool<FenceWorkItem>.Shared.Return(sortedCopy);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(assignment);
        }
    }

    private float[] _chunkLoadAcc = new float[16];

    private void EnsureChunkLoadCapacity(int chunkCount)
    {
        if (_chunkLoadAcc.Length < chunkCount)
        {
            _chunkLoadAcc = new float[Math.Max(chunkCount, _chunkLoadAcc.Length * 2)];
        }
    }

    private void AppendItem(in FenceWorkItem item)
    {
        if (ItemCount == _items.Length)
        {
            var grown = new FenceWorkItem[_items.Length * 2];
            Array.Copy(_items, grown, ItemCount);
            _items = grown;
        }
        _items[ItemCount++] = item;
    }

    private void EnsureChunkArrays(int chunkCount)
    {
        if (_chunkStart.Length < chunkCount)
        {
            _chunkStart = new int[Math.Max(chunkCount, _chunkStart.Length * 2)];
            _chunkItemCnt = new int[_chunkStart.Length];
        }
    }

    private sealed class FenceWorkItemCostDescComparer : IComparer<FenceWorkItem>
    {
        public static readonly FenceWorkItemCostDescComparer Instance = new();
        public int Compare(FenceWorkItem x, FenceWorkItem y) => y.Cost.CompareTo(x.Cost);
    }
}

/// <summary>Phase discriminator for <see cref="FenceWorkPlan.Build"/>.</summary>
internal enum FencePhase : byte
{
    Prep = 0,
    Migrate = 1,
    AabbRefresh = 2,
    Finalize = 3,
}
