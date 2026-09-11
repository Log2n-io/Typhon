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

    /// <summary>
    /// Dirty-bitmap words per <see cref="FenceWorkKind.PrepSlice"/> (#886 lead D). Doc 11 §3.2 argued 64 — one cache line of validity flags — and 64 was
    /// measured against 128 at W = 8: 128 won the 25 % reference point by 7–20 % (fewer accessors, fewer scopes, fewer plan lookups per dirty cluster) and
    /// lost the 100 % stress point by 8–13 %. The reference point is where workloads live. A static so the partition harness can sweep it.
    /// </summary>
    internal static int PrepSliceWords = 128;
    private const int BitmapBitsPerWord = 64;

    /// <summary>
    /// Lower bound on per-chunk expected wall time in µs.
    /// The chunk-count cap is <c>min(2 × workerCount × oversubscription, floor(totalCost / MinChunkCostUs))</c> — chunks below this floor are wasteful
    /// (wake-up + ChunkAccessor + EpochGuard overhead per dispatch is in the ~10-30µs range, so a 200µs floor keeps overhead under ~15%). Lets light workloads
    /// collapse to fewer chunks while heavy workloads scale to 2× the base cap. Empirical — refine if profiling shows otherwise.
    /// </summary>
    internal const float MinChunkCostUs = 200f;

    /// <summary>
    /// The smallest chunk worth dispatching, in µs of modelled cost (#889). A chunk pays roughly 10–30 µs of wake-up, accessor and epoch scope whatever it
    /// holds, so below this the dispatch would cost more than the work it parallelises. Above it, <see cref="TargetChunkCost"/> spreads the phase over
    /// every worker.
    /// </summary>
    internal static float MinUsefulChunkUs = 32f;

    /// <summary>
    /// A/B switch for #889: <c>true</c> aims a phase's chunk count at the worker count, <c>false</c> is the rule before it — 200 µs per chunk whatever
    /// W is. Static so the partition harness can flip it in one binary (<c>--legacy-chunking</c>).
    /// </summary>
    internal static bool WorkerAwareChunking = true;

    /// <summary>
    /// Dirty-bitmap words per <see cref="FenceWorkKind.FinalizeEmitSlice"/> (#889). The same grain as <see cref="PrepSliceWords"/> and for the same
    /// reason: the emit's cost per dirty cluster is of the same order as Prep's, and a slice is one accessor plus one WAL claim per 256 blocks. A static so
    /// the partition harness can sweep it.
    /// </summary>
    internal static int FinalizeSliceWords = 128;

    /// <summary>
    /// The modelled cost one chunk should carry: <c>clamp(totalCost / (workerCount × oversubscription), MinUsefulChunkUs, MinChunkCostUs)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule before this one ignored the worker count, and that was the bug (#889).</b> "Target each chunk at 200 µs — worker count is irrelevant"
    /// was tuned for CPU efficiency: a chunk pays 10–30 µs of dispatch, so 200 µs keeps that under 15 %. But a fence phase is paid in SPAN — every worker
    /// not holding a chunk is idle at the phase barrier, and its CPU is free. Measured on Matrix M at W = 8, 25 % moving: the index phase had 551 µs of
    /// work and got ONE chunk, so eight items ran on one worker while seven waited, and its span equalled its CPU. The same rule gave Prep 6 chunks for 16
    /// slices and the 100 % point's index phase 2 chunks for 8 parts.
    /// </para>
    /// <para>
    /// This spreads the phase over <c>W × oversubscription</c> chunks as soon as each would carry <see cref="MinUsefulChunkUs"/> of work, and keeps the
    /// 200 µs target when there is more work than that — beyond W × oversubscription chunks the extra ones buy jitter absorption, not width, and 200 µs
    /// is still the right grain for that.
    /// </para>
    /// </remarks>
    internal static float TargetChunkCost(float totalCost, int workerCount, int chunkOversubscription)
    {
        if (UsesLegacyGrain(workerCount))
        {
            return MinChunkCostUs;
        }

        var width = Math.Max(1, workerCount) * Math.Max(1, chunkOversubscription);
        return Math.Clamp(totalCost / width, UsefulFloor, MinChunkCostUs);
    }

    /// <summary>
    /// The rule before #889 — 200 µs per chunk, floor division — for the harness A/B switch and for a single worker. One worker gains nothing from width
    /// and pays every extra chunk's dispatch: Matrix W measured the fence 2.5 % slower at W = 1 with the worker-aware grain applied.
    /// </summary>
    private static bool UsesLegacyGrain(int workerCount) => !WorkerAwareChunking || workerCount <= 1;

    /// <summary><see cref="MinUsefulChunkUs"/> is a harness knob; it must never invert the clamp.</summary>
    private static float UsefulFloor => Math.Min(MinUsefulChunkUs, MinChunkCostUs);

    /// <summary>
    /// How many chunks the grain alone asks for, before the fattest-item, cap and item-count bounds: the width itself when the per-worker share sits
    /// inside <c>[MinUsefulChunkUs, 200 µs]</c>, <c>ceil(total / bound)</c> when a bound binds, <c>floor(total / 200)</c> under the legacy rule.
    /// </summary>
    /// <remarks>
    /// The width is STATED, not derived: <c>ceil(total / (total / width))</c> in single precision reads <c>width + 1</c> for about one total in ten at
    /// widths that are not powers of two (230.66 µs over 7 → a quotient of 7.0000005 → 8). The caps would have hidden it as one extra chunk; the rule
    /// says the width, so the code does.
    /// </remarks>
    internal static int GrainChunkCount(float totalCost, int workerCount, int chunkOversubscription)
    {
        if (UsesLegacyGrain(workerCount))
        {
            return (int)(totalCost / MinChunkCostUs);
        }

        var width = Math.Max(1, workerCount) * Math.Max(1, chunkOversubscription);
        var perWorker = totalCost / width;
        if (perWorker < UsefulFloor)
        {
            return (int)Math.Ceiling(totalCost / UsefulFloor);
        }

        if (perWorker > MinChunkCostUs)
        {
            return (int)Math.Ceiling(totalCost / MinChunkCostUs);
        }

        return width;
    }

    /// <summary>
    /// Maximum chunks (or per-archetype slices) to emit given a cost budget:
    /// <c>max(1, min(2 × workerCount × chunkOversubscription, GrainChunkCount))</c> — the width, a bound's ceiling, or the legacy rule's floor, as
    /// <see cref="GrainChunkCount"/> decides. The <c>2 × workerCount × oversubscription</c> ceiling stops a huge archetype from emitting an unbounded
    /// number of tiny slices (each slice still pays wake-up + ChunkAccessor + EpochGuard cost). <paramref name="workerCount"/> and
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

        var cap = 2 * workerCount * chunkOversubscription;
        return Math.Max(1, Math.Min(cap, GrainChunkCount(totalCost, workerCount, chunkOversubscription)));
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
            case FencePhase.IndexMassUpdate:
                EmitIndexUpdateSliceItems(engine, costModel);
                break;
            case FencePhase.EntityMapUpdate:
                EmitEntityMapUpdateSliceItems(engine, costModel);
                break;
        }

        if (ItemCount == 0)
        {
            return;
        }

        ComputeChunkCountAndPack(workerCount, chunkOversubscription);
    }

    // ─── Phase IndexMassUpdate: one item per (indexed field × leaf-snapped key range) ─────────────────

    /// <summary>
    /// Emits the <c>K × W</c> work items §5.5 calls for: one per (indexed field, key range) pair, never one per tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The partition itself is NOT computed here — <see cref="FenceIndexMassUpdateExecSystem.Prepare"/> has already merged, sorted and leaf-snapped each
    /// field's staged batch, because that needs a chunk accessor and an epoch scope and this method has neither. All that is left is to turn the recorded
    /// part boundaries into items.
    /// </para>
    /// <para>
    /// <b>Partitioning by tree instead would cap parallelism at K</b>, which is 1 for most archetypes — the whole reason the work item names a key range as
    /// well as a field.
    /// </para>
    /// </remarks>
    private void EmitIndexUpdateSliceItems(DatabaseEngine engine, LiveFenceCostModel costModel)
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

            var staging = states[meta.ArchetypeId]?.ClusterState?.IndexUpdates;
            if (staging == null)
            {
                continue;
            }

            for (var fieldId = 0; fieldId < staging.FieldCount; fieldId++)
            {
                var parts = staging.PartCount(fieldId);
                if (parts <= 0)
                {
                    continue;   // nothing staged for this field: the phase is skipped rather than dispatched empty (AC-6.7)
                }

                var boundaries = staging.Boundaries(fieldId);
                for (var p = 0; p < parts; p++)
                {
                    var start = boundaries[p];
                    var count = boundaries[p + 1] - start;
                    AppendItem(new FenceWorkItem
                    {
                        Kind = FenceWorkKind.IndexUpdateSlice,
                        TargetId = meta.ArchetypeId,
                        FieldId = fieldId,
                        Cost = costModel.IndexUpdateCost * count,
                        SliceStart = start,
                        SliceCount = count,
                        UnitCount = count,
                    });
                }
            }
        }
    }

    // ─── Phase EntityMapUpdate: one item per (archetype × bucket range) ─────────────────

    /// <summary>
    /// Turns each archetype's bucket-range partition into work items.
    /// </summary>
    /// <remarks>
    /// One item per part, exactly as the index phase does — but the axis is the BUCKET, not the key (§5.5, "EntityMap — same treatment, different axis").
    /// The partition itself was computed in <see cref="FenceEntityMapUpdateExecSystem.Prepare"/>, which has the map and can resolve buckets; all that is left
    /// here is to name the ranges.
    /// </remarks>
    private void EmitEntityMapUpdateSliceItems(DatabaseEngine engine, LiveFenceCostModel costModel)
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

            var staging = states[meta.ArchetypeId]?.ClusterState?.EntityMapUpdates;
            var parts = staging?.PartCount ?? 0;
            if (parts <= 0)
            {
                continue;   // nothing migrated for this archetype: no item, so the phase is skipped rather than dispatched empty
            }

            var boundaries = staging!.Boundaries;
            for (var p = 0; p < parts; p++)
            {
                var start = boundaries[p];
                var count = boundaries[p + 1] - start;
                AppendItem(new FenceWorkItem
                {
                    Kind = FenceWorkKind.EntityMapUpdateSlice,
                    TargetId = meta.ArchetypeId,
                    Cost = costModel.EntityMapUpdateCost * count,
                    SliceStart = start,
                    SliceCount = count,
                    UnitCount = count,
                });
            }
        }
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

            // The head already ran for this archetype (#886 lead D): its snapshot is in FenceDirtyBits and the bitmap is drained, so the atomic item's
            // own HasDirty test below would read false. The slices are the item.
            if (state.PrepSliceable && state.FenceDirtyBits != null)
            {
                EmitPrepSliceItems(meta, state, costModel);
                continue;
            }

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

    /// <summary>
    /// One <see cref="FenceWorkKind.PrepSlice"/> per <see cref="PrepSliceWords"/>-word range of the head's snapshot that holds at least one dirty word.
    /// A range with nothing dirty gets no item at all — a slice that opens an accessor to find nothing is worse than none. Cost is per dirty cluster,
    /// from the live model, so the FFD packer sees the bimodal reality of a tick where motion is spatially clustered.
    /// </summary>
    private void EmitPrepSliceItems(ArchetypeMetadata meta, ArchetypeClusterState state, LiveFenceCostModel costModel)
    {
        var bits = state.FenceDirtyBits;
        var total = bits.Length;
        for (var start = 0; start < total; start += PrepSliceWords)
        {
            var count = Math.Min(PrepSliceWords, total - start);
            var dirty = 0;
            for (var w = start; w < start + count; w++)
            {
                if (bits[w] != 0)
                {
                    dirty++;
                }
            }

            if (dirty == 0)
            {
                continue;
            }

            AppendItem(new FenceWorkItem
            {
                Kind = FenceWorkKind.PrepSlice,
                TargetId = meta.ArchetypeId,
                Cost = Math.Max(0.5f, costModel.PrepCost * dirty),
                SliceStart = start,
                SliceCount = count,
                UnitCount = dirty,
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

            // The DRAIN PREFIX, not the whole queue — the two are different quantities since #872 step 10 and slicing the wrong one desynchronises Migrate
            // from Finalize (CR-01).
            //
            // Finalize is skipped for an archetype whose Prep returned false (FenceBranchPath == 0, see EmitArchetypeFinalizeItems), while this loop gated
            // on PendingMigrationCount, which survives across ticks. An archetype that queued drift requests on one tick and then went quiet — its
            // clusters drained, or it stopped being spatial — therefore had its whole tail EXECUTED here with no Finalize to compact it away, so the same
            // requests re-executed on every subsequent tick.
            //
            // PrepareArchetypeFence sets the prefix to zero on exactly the paths where it returns false, so reading it here makes Migrate and Finalize
            // agree by construction: no Prep, no drain, nothing to compact. When Prep did return true the prefix equals the queue length at that moment,
            // so this is otherwise the identical slicing.
            var pendingCount = state.PendingMigrationDrainCount;
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

            // The drain prefix is sorted by destCellKey (the Prep tail's OrderDrainAndMeasureArrivals, #910). Slice on cell boundaries: each slice
            // owns a contiguous range of destCellKeys and no two slices share a dest cell — this is what makes the dst-side ClusterClaim path
            // "worker-exclusive" without per-cell locking (review C-2 fix). Starting from the ideal index split, advance until destCellKey changes; if a
            // single cell's migration block exceeds sliceSize, the slice naturally grows to cover the whole block (one cell on one/ worker). The trailing
            // partial slice gets whatever's left.
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

            // The head already ran on the driver (#889): the slices are the item, or — when the head found nothing to emit — there is no item at all,
            // because the atomic path would sweep dormancy a second time.
            if (state.FinalizeHeadRan)
            {
                if (state.FinalizeSliceable && state.FenceDirtyBits != null)
                {
                    EmitFinalizeSliceItems(meta, state, costModel);
                }

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

    /// <summary>How many <paramref name="rangeWords"/>-word ranges of <paramref name="bits"/> hold at least one non-zero word — the slice count the emit
    /// would be carved into, which is what decides whether slicing is worth moving the head onto the driver (#889).</summary>
    internal static int CountPopulatedRanges(long[] bits, int rangeWords)
    {
        var populated = 0;
        for (var start = 0; start < bits.Length; start += rangeWords)
        {
            var end = Math.Min(bits.Length, start + rangeWords);
            for (var w = start; w < end; w++)
            {
                if (bits[w] != 0)
                {
                    populated++;
                    break;
                }
            }
        }

        return populated;
    }

    /// <summary>
    /// One <see cref="FenceWorkKind.FinalizeEmitSlice"/> per <see cref="FinalizeSliceWords"/>-word range of the archetype's dirty bitmap that holds at least
    /// one dirty word, costed per dirty cluster from the live model — the shape of <see cref="EmitPrepSliceItems"/>, for the emit.
    /// </summary>
    private void EmitFinalizeSliceItems(ArchetypeMetadata meta, ArchetypeClusterState state, LiveFenceCostModel costModel)
    {
        var bits = state.FenceDirtyBits;
        var total = bits.Length;
        for (var start = 0; start < total; start += FinalizeSliceWords)
        {
            var count = Math.Min(FinalizeSliceWords, total - start);
            var dirty = 0;
            for (var w = start; w < start + count; w++)
            {
                if (bits[w] != 0)
                {
                    dirty++;
                }
            }

            if (dirty == 0)
            {
                continue;
            }

            AppendItem(new FenceWorkItem
            {
                Kind = FenceWorkKind.FinalizeEmitSlice,
                TargetId = meta.ArchetypeId,
                Cost = Math.Max(0.5f, costModel.FinalizeEmitCost * dirty),
                SliceStart = start,
                SliceCount = count,
                UnitCount = dirty,
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

        // The chunk grain comes from TargetChunkCost: the phase spread over every worker once each chunk carries MinUsefulChunkUs of work, 200 µs per chunk
        // beyond that for jitter absorption (more queued items per worker, peers pick up slack when a chunk runs long). An atomic item bigger than the
        // grain sets the grain — it cannot be split. GrainChunkCount states the width rather than deriving it back from the grain (see its remark).
        // Under the legacy grain the packer keeps its own pre-#889 arithmetic — ceil(total / 200), where ComputeMaxChunks floors — so the A/B switch and
        // the single-worker path reproduce exactly what they did.
        var grain = TargetChunkCost(totalCost, workerCount, chunkOversubscription);
        var chunkCount = maxAtomicCost > grain
            ? (int)Math.Ceiling(totalCost / maxAtomicCost) : UsesLegacyGrain(workerCount) 
                ? (int)Math.Ceiling(totalCost / grain) : GrainChunkCount(totalCost, workerCount, chunkOversubscription);
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
    IndexMassUpdate = 4,
    EntityMapUpdate = 5,
}
