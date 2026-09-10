using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Per-archetype runtime state for cluster storage. Manages the cluster segment, active cluster tracking, and slot claiming for entity spawn/destroy.
/// </summary>
/// <remarks>
/// <para>Each cluster-eligible archetype gets one <see cref="ArchetypeClusterState"/> instance, created during <c>DatabaseEngine.InitializeArchetypes</c>.</para>
/// <para>Active clusters are tracked in a compact array for O(N_clusters) iteration.
/// Free slot discovery uses bitmask TZCNT on OccupancyBits.</para>
/// </remarks>
internal sealed unsafe partial class ArchetypeClusterState
{
    /// <summary>ChunkBasedSegment backing cluster data (SV + V components). Null for pure-Transient archetypes.</summary>
    public ChunkBasedSegment<PersistentStore> ClusterSegment;

    /// <summary>ChunkBasedSegment backing Transient component data. Null if archetype has no Transient components.
    /// Uses identical layout as <see cref="ClusterSegment"/> (same stride, same offsets). Chunk IDs are synchronized
    /// via lockstep allocation/free.</summary>
    public ChunkBasedSegment<TransientStore> TransientSegment;

    /// <summary>TransientStore instance kept alive for heap-backed TransientSegment. Null if no Transient components.</summary>
    internal TransientStore? TransientClusterStore;

    /// <summary>Precomputed layout info (offsets, sizes, cluster size N).</summary>
    public ArchetypeClusterInfo Layout;

    /// <summary>
    /// Compact array of chunk IDs for active clusters. Occupancy &gt; 0 for every entry <b>except</b> inside the deferred-drain window: a Migrate-phase worker
    /// that clears a cluster's last bit does not remove it here, so a drained-but-not-yet-finalized cluster stays listed until
    /// <see cref="DrainPendingClusterFinalizations"/> runs in Finalize. Readers that need "occupancy &gt; 0" must check the occupancy word, not membership.
    /// </summary>
    public int[] ActiveClusterIds;

    /// <summary>Number of active clusters (valid entries in <see cref="ActiveClusterIds"/>).</summary>
    public int ActiveClusterCount;

    /// <summary>Chunk ID of first cluster with at least one free slot. -1 = none (allocate new).</summary>
    public int FreeClusterHead;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Per-archetype structural-mutation latch. Despite the name it does NOT guard a finalize section — nothing ever takes it to finalize a drained cluster
    // (see the deferred-drain banner below for how that is actually made safe). It serializes the paths that GROW or APPEND to shared per-archetype state
    // while sibling Migrate-phase workers run, and nothing else. Every acquisition, exhaustively:
    //   ApplyDirtyBitDeltas            — batch-apply a worker's local dirty-bit deltas into FenceDirtyBits
    //   GrowFenceDirtyBitsForChunkId   — Array.Resize of FenceDirtyBits
    //   RecordClusterDrain             — fallback grow of _drainedClusterIds ONLY; the normal append is lock-free
    //   TryClaimSlotInCluster (x2)     — new-cluster slow path: lockstep dual-segment AllocateChunk, AddToActiveList, CellClusterPool.AddCluster,
    //                                    ClusterCellMap back-pointer. The hot path (CAS into an existing cluster) does not take it.
    //   EnqueueMigrationsBulk          — bulk append to PendingMigrations
    //   EnqueuePromotedAppliesBulk     — bulk append to PendingPromotedApplies (AabbRefresh's divert of promoted cells)
    //   EnqueueRepairNominationsBulk   — bulk append to the repair nomination list (ArchetypeClusterState.Repair.cs)
    //   RegisterPrepSliceCrossings     — a Prep slice filing its cell crossings under its own slice key
    //   AllocateNewClusterLatched      — the repair path's cluster allocation; the twin of TryClaimSlotInCluster's slow path
    //   EnsureClusterVisibilityCapacity — GROWTH ONLY, behind a double-checked length compare; the fold itself never takes it. Serializing growers against
    //                                    each other is not the whole fix — see NoteClusterBorn for why a fold must also re-read the array reference after
    //                                    its CAS — but it is the half that stops two growers dropping each other's copy.
    //   EnsureClusterAabbsCapacity        ┐ GROWTH ONLY, all five behind a double-checked length compare, all five with a ...Locked body a caller already
    //   EnsureClusterSpatialIndexSlotCapacity │ holding the latch calls directly. See the PER-ARCHETYPE ARRAY GROWTH banner below for what they are
    //   EnsureClusterCellMapCapacity      │ protecting against and why growth from inside a Migrate slice is refused rather than serialised.
    //   EnsureClusterWriteBookkeepingCapacity │
    //   EnsurePerCellIndexCapacity        ┘
    //   TryEnsureCellTreeSegment       — first-use creation of the archetype's shared cell-tree segment. Two unsynchronised creators would each build a
    //                                    segment and one would win, orphaning every tree already built on the loser.
    // This list is load-bearing: it is what the next person reasons about lock order from, so an acquisition added without a line here is worse than one
    // added with a wrong line. EnsureClusterVisibilityCapacity was added in #722 and this entry with it; the five capacity growers, the three bulk appends,
    // AllocateNewClusterLatched and TryEnsureCellTreeSegment were added later and the list had drifted behind them.
    // Note the asymmetry this leaves: AddToActiveList runs under the latch, RemoveFromActiveList never does — removal happens only from serial contexts
    // (Finalize, or a single-threaded Transaction.Destroy). No reader takes it either, so the latch orders writers against writers, not readers.
    // Padded to 64 bytes so the latch field owns a full cache line and uncontended acquisitions don't ping-pong with adjacent hot fields like
    // ActiveClusterCount / MigrationHint / LastTickMigrationCount. See rule MD-03 in rules/spatial.md. The acquisition counter shares that line on purpose:
    // it is written only by the thread already holding the latch, so it rides a line that thread owns exclusively and costs nothing (#912).
    //
    // EVERY acquisition goes through PaddedFinalizeLock.Enter/Exit rather than through .Lock directly, which is what makes FinalizeLockAcquisitions a
    // complete count. A site added straight onto .Lock would be invisible to it, and an incomplete count is worse than none — it reads as evidence.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct PaddedFinalizeLock
    {
        [FieldOffset(0)] public AccessControlSmall Lock;

        /// <summary>Exclusive acquisitions of this latch since the last per-tick reset. See <see cref="Enter"/>.</summary>
        [FieldOffset(8)] public long Acquisitions;

        /// <summary>
        /// Acquire the latch and count it. Every acquisition in the archetype goes through here rather than through <c>Lock</c> directly.
        /// </summary>
        /// <remarks>
        /// <para><b>A count and not a duration, deliberately (#912).</b> This latch is the only archetype-wide serialisation point a Migrate or AabbRefresh
        /// worker can reach, so "does it get taken more often as the worker count rises" is the question that separates a phase which is genuinely contended
        /// from one whose per-worker cost merely looks worse because there are more workers. Timing it would need two <see cref="Stopwatch.GetTimestamp"/>
        /// calls per acquisition — ~50 ns against a critical section that is frequently shorter than that — and would change the thing being measured. The
        /// increment below costs nothing: it runs INSIDE the exclusive region, on a cache line this thread already owns exclusively, so it needs no
        /// <c>Interlocked</c> and adds no coherence traffic that the acquisition has not already paid for.</para>
        /// <para>Reset from Prep with the rest of the per-tick block, and ordered against these writes by the fence phase barrier — the same plain-store
        /// discipline the migration counters beside it use, and for the same reason.</para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Enter(ref WaitContext ctx)
        {
            Lock.EnterExclusiveAccess(ref ctx);
            Acquisitions++;
        }

        /// <inheritdoc cref="Enter"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Exit() => Lock.ExitExclusiveAccess();
    }

    private PaddedFinalizeLock _finalizeLock;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Deferred-drain list. Migrate-phase workers atomically clear slot bits — when a clear flips the LAST bit of a cluster, the worker DOES NOT immediately
    // finalize-and-free the chunk (would race with concurrent ClaimSlotInCell on the same cluster: the claimant could CAS-set a fresh bit between the AND
    // and any subsequent lock, then we'd free a chunk that has live data). Instead, the worker records the chunkId here. FinalizeArchetypeFence walks the
    // list serially (one atomic work item per archetype, dispatched after the Migrate and AabbRefresh phase barriers), re-checks occupancy, and frees only
    // clusters that are still empty. The re-check needs NO lock: the barriers mean no ClaimSlotInCell or ReleaseSlot can be in flight for this archetype by
    // then. Deferral plus the barrier is what makes the finalize safe — not mutual exclusion. See review C-1.
    //
    // Slot reservation is lock-free: Interlocked.Increment on _drainedCount, then write into _drainedClusterIds[slot].
    // Capacity is pre-sized by PreSizeMigrationBuffers to PendingMigrationCount (one migration releases at most one source slot, so cluster-drain
    // count ≤ migration count).
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    internal int[] _drainedClusterIds;
    internal int _drainedCount;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Shadow-drain ordering scratch (#882). See BuildShadowDrainOrder.
    //
    // Per-archetype and reused across ticks, so the steady state is zero allocation. Two arrays: a permutation of entry indices, and a histogram over cluster
    // chunk ids. The histogram is cleared over [min, max] of the ids actually seen rather than over its whole length, so a sparse tick pays for its own span
    // and not for the segment's capacity.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    private int[] _shadowDrainOrder = [];
    private int[] _shadowDrainCounts = [];

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Prep slicing (#886 lead D). The parallel fence's Prep phase used to be one item per archetype; it is now a serial HEAD on the driver
    // (DatabaseEngine.PrepareArchetypeFenceHead), N PrepSlice items over disjoint ranges of FenceDirtyBits words (DatabaseEngine.RunPrepSlice), and a
    // serial TAIL in the Migrate phase's Prepare (DatabaseEngine.PrepareArchetypeFenceTail). Everything below is head-written, slice-read-or-folded,
    // tail-consumed.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Set by the head when this tick's Prep runs as slices; cleared by the tail, and by the atomic path so a serial tick never inherits it.</summary>
    internal bool PrepSliceable;

    /// <summary>The zone-map rotation phase for this tick, advanced ONCE per tick by <c>DatabaseEngine.BeginZoneMapTick</c> and read by every slice.</summary>
    internal int ZoneMapRetightenPhase;

    /// <summary>
    /// Each slice's cell-crossing requests, keyed by the slice's first word. The tail concatenates them in ascending word order, which is the order the
    /// serial detector appended in — so the queue the throttle sees is bit-identical to the unsliced one (TH-01 admits the same first N).
    /// </summary>
    internal readonly List<(int Start, List<MigrationRequest> Requests)> PrepSliceCrossings = [];

    /// <summary>Hysteresis-absorbed count folded by the slices, applied to the per-tick counters by the tail.</summary>
    internal int PrepSliceHysteresisAbsorbed;

    /// <summary>
    /// True on a worker while it runs a <c>PrepSlice</c>. A slice must never grow an array another slice can be reading; the sites that grow assert on it.
    /// </summary>
    [ThreadStatic]
    internal static bool InPrepSlice;

    /// <summary>Marks the current thread as inside a Prep slice. Compiled out with the asserts that read the flag, so Release pays nothing.</summary>
    [Conditional("DEBUG")]
    internal static void EnterPrepSlice() => InPrepSlice = true;

    /// <inheritdoc cref="EnterPrepSlice"/>
    [Conditional("DEBUG")]
    internal static void ExitPrepSlice() => InPrepSlice = false;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // PER-ARCHETYPE ARRAY GROWTH under the parallel Migrate phase.
    //
    // Migrate slices are carved on DestCellKey boundaries, so no two workers share a destination CELL. That is what makes the per-cell structures safe. It
    // says nothing about the per-ARCHETYPE arrays every worker indexes by cluster chunk id — ClusterAabbs, ClusterSpatialIndexSlot, ClusterCellMap, the
    // write-bookkeeping quartet, PerCellIndex. Each of those used to grow by a bare Array.Resize from whichever worker happened to need the room, which is
    // MD-02's "Array.Resize from worker → lost writes from siblings holding the old array reference", and it is worse than the two-growers race alone:
    //   * a sibling that already loaded the reference writes its handle into the abandoned copy — the cluster is silently absent from its cell index;
    //   * ExecuteMigrationsSlice holds `ref ClusterAabbs[dstChunkId]` ACROSS the union, so a concurrent grow sends the whole union into the dead array;
    //   * for ClusterSpatialIndexSlot the resize additionally drags RebindCellTreeBackPointers behind it, and a rebind cannot be made safe against a
    //     sibling worker's tree.Add no matter what lock the grower holds — the sibling is not holding it.
    // The growth itself is therefore serialised on _finalizeLock (double-checked, so the fast path stays a lock-free length compare), and growth from INSIDE
    // a Migrate slice is refused outright. That refusal is not a limitation: PreSizeArchetypeFence sizes every one of these arrays, before the Migrate
    // dispatch, to a bound the phase provably cannot exceed — a slice allocates at most one new cluster per pending migration, so the largest chunk id it
    // can produce is below PrimarySegmentCapacity + PendingMigrationCount, and the pre-size adds twice that plus 64. Reaching the throw means that reasoning
    // broke, and a loud fence failure (TickOutcomeReason.FenceFailure) is the correct answer to it rather than a silently dropped index entry.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// True on a worker while it runs a Migrate slice. Unlike <see cref="InPrepSlice"/> this is NOT compiled out in Release: what it guards is silent index
    /// loss rather than a development-time ordering mistake.
    /// </summary>
    [ThreadStatic]
    internal static bool InMigrateSlice;

    /// <summary>
    /// The bound the last <see cref="PreSizeMigrationBuffers"/> used. Diagnostic only — it makes a growth refusal say WHY the bound was short.
    /// </summary>
    internal int LastPreSizeUpperBound;

    /// <summary>Marks the current thread as inside a parallel Migrate slice.</summary>
    internal static void EnterMigrateSlice() => InMigrateSlice = true;

    /// <inheritdoc cref="EnterMigrateSlice"/>
    internal static void ExitMigrateSlice() => InMigrateSlice = false;

    /// <summary>Refuse to reallocate a shared per-archetype array while sibling Migrate workers may be holding the current one. See the banner above.</summary>
    private void ThrowIfGrowingInsideMigrateSlice(string arrayName, int requiredLength, int currentLength)
    {
        if (!InMigrateSlice)
        {
            return;
        }

        ThrowHelper.ThrowInvalidOp(
            $"Archetype {ArchetypeId}'s {arrayName} needs {requiredLength} entries but holds {currentLength}, and the request came from inside a parallel "
            + "Migrate slice. Growing it there would hand every sibling worker an abandoned array and silently drop their writes (MD-02). The fence "
            + "pre-sizes this array in PreSizeArchetypeFence to a bound the Migrate phase cannot exceed, so reaching this means that bound is wrong. "
            + $"[last pre-size bound {LastPreSizeUpperBound}, PrimarySegmentCapacity now {PrimarySegmentCapacity}, PendingMigrationCount "
            + $"{PendingMigrationCount}]");
    }

    /// <summary>
    /// Forgets whatever a previous sliced tick left behind. Called from the per-tick reset, so a Prep that threw — its tail never ran (the scheduler
    /// fails Migrate's dependency and skips its Prepare) — cannot hand stale slice lists to the next sliced tail, whose slots may since have changed hands.
    /// </summary>
    internal void ResetPrepSliceState()
    {
        PrepSliceable = false;
        PrepSliceCrossings.Clear();
        PrepSliceHysteresisAbsorbed = 0;
    }

    /// <summary>
    /// Test hook: invoked once per archetype per tick at the end of Prep's serial tail — atomic item or sliced tail alike — with the drain prefix set.
    /// The equivalence fixture uses it to snapshot <c>PendingMigrations[0, PendingMigrationDrainCount)</c> from inside the fence, which is the only
    /// place the queue the throttle saw is observable. Null in production; the call is a null test.
    /// </summary>
    /// <remarks>
    /// <para><b>It fires LAST, after the pre-size, and that ordering is load-bearing.</b> The archetype it hands a probe is therefore exactly what the Migrate
    /// phase will find, which is what lets a test falsify the pre-size bound at the one point a wrong bound would leave it —
    /// <c>CellTreeDensityTransitionTests.AShortPerArchetypeArrayAbortsTheTickInsteadOfGrowingUnderSiblingWorkers</c> depends on it and silently stops
    /// distinguishing a live guard from an absent one if ⑧ is ever moved back above this call.</para>
    /// <para><b>The consequence for probe implementations:</b> one that FILES migration requests or allocates clusters now does so after the bound was taken,
    /// and will trip the in-slice growth refusal as a false positive. Probes observe; they do not produce.</para>
    /// </remarks>
    internal static Action<ArchetypeClusterState, long> PrepQueueProbe;

    /// <summary>Test-visible count of <c>PrepSlice</c> items executed, process-wide. Proves a fixture exercised the sliced path, not the atomic one.</summary>
    internal static int PrepSlicesRun;

    /// <summary>Bumps <see cref="PrepSlicesRun"/>. Debug-only, so Release never shares a cache line across every engine and worker for a test counter.</summary>
    [Conditional("DEBUG")]
    internal static void NotePrepSliceRun() => Interlocked.Increment(ref PrepSlicesRun);

    /// <summary>Files one slice's crossings for the tail. One lock acquisition per slice.</summary>
    internal void RegisterPrepSliceCrossings(int sliceStart, List<MigrationRequest> requests)
    {
        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref nullCtx);
        try
        {
            PrepSliceCrossings.Add((sliceStart, requests));
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    private sealed class PrepSliceCrossingsByStart : IComparer<(int Start, List<MigrationRequest> Requests)>
    {
        public static readonly PrepSliceCrossingsByStart Instance = new();
        public int Compare((int Start, List<MigrationRequest> Requests) x, (int Start, List<MigrationRequest> Requests) y) => x.Start.CompareTo(y.Start);
    }

    /// <summary>Appends every slice's crossings to <see cref="PendingMigrations"/> in ascending slice order, then forgets them. Tail only.</summary>
    internal void DrainPrepSliceCrossings()
    {
        PrepSliceCrossings.Sort(PrepSliceCrossingsByStart.Instance);
        for (var i = 0; i < PrepSliceCrossings.Count; i++)
        {
            EnqueueMigrationsBulk(PrepSliceCrossings[i].Requests);
        }

        PrepSliceCrossings.Clear();
    }

    /// <summary>
    /// Grows <see cref="PendingMigrations"/> to what this tick's detection is expected to need — a quarter over last tick's count, and never below the
    /// requests already retained — so that the per-slice detection never has to. Runs in the head; the atomic path calls it from the same place it
    /// always did.
    /// </summary>
    /// <remarks>
    /// <c>[0, PendingMigrationCount)</c> holds LIVE requests when this runs (#877): a fresh array without the copy turned them into default-valued
    /// requests — source cluster 0, slot 0, destination cell 0 — which Migrate then executed.
    /// </remarks>
    internal void EnsurePendingMigrationCapacityForTick()
    {
        var prevMigrations = PreviousTickMigrationCount;
        var expectedCapacity = Math.Max(16, prevMigrations + (prevMigrations >> 2));
        var retained = PendingMigrationCount;
        expectedCapacity = Math.Max(expectedCapacity, retained);
        if (PendingMigrations == null || PendingMigrations.Length < expectedCapacity)
        {
            var grown = new MigrationRequest[expectedCapacity];
            if (PendingMigrations != null && retained > 0)
            {
                Array.Copy(PendingMigrations, grown, Math.Min(retained, PendingMigrations.Length));
            }

            PendingMigrations = grown;
        }
    }

    /// <summary>Builds every non-empty shadow buffer's drain plan (see <see cref="FieldShadowBuffer.BuildDrainPlan"/>) — head only, once per tick.</summary>
    internal void BuildShadowDrainPlans()
    {
        BuildShadowDrainPlans(IndexSlots);
        BuildShadowDrainPlans(TransientIndexSlots);
    }

    private static void BuildShadowDrainPlans<TStore>(ClusterIndexSlot<TStore>[] ixSlots) where TStore : struct, IPageStore
    {
        if (ixSlots == null)
        {
            return;
        }

        for (var s = 0; s < ixSlots.Length; s++)
        {
            for (var f = 0; f < ixSlots[s].Fields.Length; f++)
            {
                ixSlots[s].ShadowBuffers[f].BuildDrainPlan();
            }
        }
    }

    /// <summary>Resets every shadow buffer of both index homes and the shadow bitmap: the tail's counterpart of the atomic drain's per-field reset.</summary>
    /// <remarks>
    /// The bitmap is null for an archetype with no shadowable index, and this dereferenced it (#889). On such an archetype every sliced-Prep tick threw
    /// here, inside Migrate's Prepare, and the scheduler recorded the exception in the phase's telemetry and skipped Index, EntityMap, AabbRefresh and
    /// Finalize as DependencyFailed — no WAL emit and no dormancy sweep, on every tick with dirty data, with nothing thrown to the host. Found by
    /// <c>FinalizeEmitSliceEquivalenceTests</c>, the first fixture to run a large index-less archetype through the parallel fence.
    /// </remarks>
    internal void ResetShadowBuffersAfterSlices()
    {
        ResetShadowBuffers(IndexSlots);
        ResetShadowBuffers(TransientIndexSlots);
        ClusterShadowBitmap?.Clear();
    }

    private static void ResetShadowBuffers<TStore>(ClusterIndexSlot<TStore>[] ixSlots) where TStore : struct, IPageStore
    {
        if (ixSlots == null)
        {
            return;
        }

        for (var s = 0; s < ixSlots.Length; s++)
        {
            for (var f = 0; f < ixSlots[s].Fields.Length; f++)
            {
                ixSlots[s].ShadowBuffers[f].ClearDrainPlan();
                ixSlots[s].ShadowBuffers[f].Reset();
            }
        }
    }

    /// <summary>Pre-grows every zone map of both homes so no slice ever takes the grow latch exclusively.</summary>
    internal void EnsureZoneMapCapacity(int capacity)
    {
        EnsureZoneMapCapacity(IndexSlots, capacity);
        EnsureZoneMapCapacity(TransientIndexSlots, capacity);
    }

    private static void EnsureZoneMapCapacity<TStore>(ClusterIndexSlot<TStore>[] ixSlots, int capacity) where TStore : struct, IPageStore
    {
        if (ixSlots == null)
        {
            return;
        }

        for (var s = 0; s < ixSlots.Length; s++)
        {
            for (var f = 0; f < ixSlots[s].Fields.Length; f++)
            {
                ixSlots[s].Fields[f].ZoneMap?.EnsureCapacity(capacity);
            }
        }
    }


    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Fence-tick intermediate state. Populated by the Prep phase of the parallel fence (DatabaseEngine.PrepareArchetypeFence), consumed by the Migrate phase
    // (ExecuteMigrationsSlice) and the Finalize phase (FinalizeArchetypeFence). Single-archetype-scoped; reset at the top of Prep each tick.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Branch path selected by Prep. 0 = no work (pure-transient with no dirty / no-spatial clean / non-cluster-eligible),
    /// 1 = clean-bitmap spatial refresh path (local occupancy bits, no WAL), 2 = dirty-bitmap path (full snapshot + WAL).</summary>
    internal byte FenceBranchPath;

    /// <summary>Dirty-bits snapshot for this fence tick, set by Prep, mutated atomically by Migrate (slot bit flips), read by Finalize for AABB + WAL.
    /// On branch path 1 this is the local occupancy-only spatialBits buffer; on path 2 it's the real <c>ClusterDirtyBitmap.Snapshot()</c> result.</summary>
    internal long[] FenceDirtyBits;

    /// <summary>Popcount of dirty entries after occupancy-masking. Drives WAL chunk sizing in Finalize. Path 1 leaves this at 0.</summary>
    internal int FenceEntryCount;

    /// <summary>Sentinel meaning "assume every component slot was written" — the fail-safe value (#559 §4.5).</summary>
    internal const int AllSlotsWritten = -1;

    /// <summary>
    /// Union over the whole archetype of the component slots written this tick. Bit <c>s</c> set =&gt; component slot <c>s</c> was
    /// written somewhere; <see cref="AllSlotsWritten"/> means "unknown — emit everything".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately ONE value for the archetype rather than one per cluster. All blocks in a fence batch share a single column
    /// set, so the union is the only granularity the emitter can act on — a per-cluster array would be extra state that gets
    /// collapsed to this anyway.
    /// </para>
    /// <para>
    /// It also has to be one value for performance. A per-cluster array is written by every worker across ~27 cache lines and
    /// false-shares badly (measured: +2 ms/tick median and a 10 ms spread on a 20k-entity archetype). A single field written with
    /// test-then-<see cref="Interlocked.Or(ref int, int)"/> goes read-only after the first write of each component in the tick, so
    /// the line is shared rather than ping-ponged.
    /// </para>
    /// </remarks>
    internal int WrittenSlotUnion;

    /// <summary>Prep-time snapshot of <see cref="WrittenSlotUnion"/> — the value Finalize reads when choosing which columns to emit.</summary>
    internal int FenceWrittenSlots;

    /// <summary>Dirty cluster count (per-word non-zero count) at the end of Prep. Used for telemetry only.</summary>
    internal int FenceDirtyClusterCount;

    /// <summary>
    /// Popcount of <see cref="ClusterProcessBitmap"/> captured at end of Prep. Read by the AabbRefresh planner to size per-archetype cost without redoing the
    /// popcount on TickDriver (review D-4). -1 indicates "not computed this tick" (non-BarrierOnly archetypes use <see cref="ActiveClusterCount"/> directly).
    /// </summary>
    internal int FenceProcessBitmapClusterCount;

    /// <summary>
    /// Apply a contiguous run of <see cref="DirtyBitDelta"/> entries to <see cref="FenceDirtyBits"/>. Called from <see cref="DatabaseEngine.FlushDirtyBitDeltas"/>
    /// after each chunk's Migrate phase completes — the chunk's worker-local buffer is sorted by archetypeId, then this method applies all deltas for one
    /// archetype under a single <see cref="_finalizeLock"/> acquisition. Plain bit ops (no Interlocked) are correct under the lock:
    /// only one worker writes to this archetype's FenceDirtyBits at a time, eliminating cross-worker cache-line false-sharing on adjacent chunkIds.
    /// </summary>
    /// <param name="buffer">Worker-local list of <see cref="DirtyBitDelta"/> entries, sorted by archetypeId; this archetype's run is contiguous.</param>
    /// <param name="offset">Index of the first entry in <paramref name="buffer"/> belonging to this archetype's run.</param>
    /// <param name="count">Number of contiguous entries starting at <paramref name="offset"/> to apply; a value &lt;= 0 is a no-op.</param>
    internal void ApplyDirtyBitDeltas(List<DirtyBitDelta> buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref nullCtx);
        try
        {
            // First pass: find the max chunkId referenced so we grow FenceDirtyBits once if needed.
            var maxChunkId = -1;
            for (var i = 0; i < count; i++)
            {
                var d = buffer[offset + i];
                if (d.SrcChunkId > maxChunkId)
                {
                    maxChunkId = d.SrcChunkId;
                }

                if (d.DstChunkId > maxChunkId)
                {
                    maxChunkId = d.DstChunkId;
                }
            }
            if (FenceDirtyBits == null || maxChunkId >= FenceDirtyBits.Length)
            {
                var required = maxChunkId + 1;
                if (FenceDirtyBits == null)
                {
                    FenceDirtyBits = new long[Math.Max(required, 16)];
                }
                else
                {
                    var newLen = FenceDirtyBits.Length;
                    while (newLen < required)
                    {
                        newLen = Math.Max(newLen * 2, required);
                    }

                    Array.Resize(ref FenceDirtyBits, newLen);
                }
            }

            // Second pass: apply clears and sets. Plain bit ops — we hold the lock, no other worker is writing.
            var bits = FenceDirtyBits;
            for (var i = 0; i < count; i++)
            {
                var d = buffer[offset + i];
                if (d.SrcClearMask != 0 && d.SrcChunkId >= 0 && d.SrcChunkId < bits.Length)
                {
                    bits[d.SrcChunkId] &= ~d.SrcClearMask;
                }
                if (d.DstSetMask != 0 && d.DstChunkId >= 0 && d.DstChunkId < bits.Length)
                {
                    bits[d.DstChunkId] |= d.DstSetMask;
                }

                // #912. The source cluster's shrink axes and process bit, deferred out of the migrant loop into this drain. Plain writes: the latch above
                // excludes every other worker from both arrays, so the Interlocked pair the per-migrant path needs is not merely cheaper here — it is gone.
                if (DeferMigrateClusterFlags && d.SrcChunkId >= 0)
                {
                    FlagClusterForShrinkRefreshLocked(d.SrcChunkId);
                }
            }
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>
    /// Grow <see cref="FenceDirtyBits"/> on-demand under <see cref="_finalizeLock"/> so a Migrate-phase worker can safely write to <c>FenceDirtyBits[chunkId]</c>
    /// when its dstChunkId exceeds the pre-sized length. The lock excludes concurrent grows; callers must re-read <see cref="FenceDirtyBits"/> after the call
    /// to pick up the (possibly grown) array reference. Idempotent — if another worker already grew the array beyond <paramref name="chunkId"/>, returns
    /// without further work.
    /// </summary>
    internal void GrowFenceDirtyBitsForChunkId(int chunkId)
    {
        if (FenceDirtyBits != null && chunkId < FenceDirtyBits.Length)
        {
            return;
        }

        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref nullCtx);
        try
        {
            // Re-check under lock — another worker may have already grown past us.
            var required = chunkId + 1;
            if (FenceDirtyBits == null)
            {
                FenceDirtyBits = new long[Math.Max(required, 16)];
            }
            else if (FenceDirtyBits.Length < required)
            {
                var newLen = FenceDirtyBits.Length;
                while (newLen < required)
                {
                    newLen = Math.Max(newLen * 2, required);
                }

                Array.Resize(ref FenceDirtyBits, newLen);
            }
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>
    /// Grow <see cref="FenceDirtyBits"/> and supporting per-cluster arrays to an upper-bound size that the parallel Migrate phase will never exceed. Called by
    /// TickDriver between the Prep and Migrate phase dispatches — guarantees worker threads never need to <c>Array.Resize</c> a buffer during their parallel
    /// apply.
    /// </summary>
    /// <param name="upperBound">Worst-case maximum cluster chunk ID + 1 that this fence tick could touch. Typically, <c>PrimarySegmentCapacity +
    /// PendingMigrationCount</c> — one new cluster per migration in the worst case.</param>
    /// <param name="cellUpperBound">Worst-case maximum cell key + 1. <see cref="PerCellIndex"/> is the one shared array a worker indexes by CELL rather than
    /// by cluster, and it was the one this pre-size did not cover — leaving <c>AddClusterToPerCellIndex</c> as the last site that could reallocate a shared
    /// array from a Migrate slice. Cell keys are pool slots handed out when a cell is first occupied, and the Migrate phase occupies no cell that crossing
    /// detection has not already created in Prep, so the grid's current cell count bounds it.</param>
    internal void PreSizeMigrationBuffers(int upperBound, int cellUpperBound = 0)
    {
        LastPreSizeUpperBound = upperBound;
        Debug.Assert(!InPrepSlice, "a Prep slice must not grow the per-cluster arrays another slice is reading (#886)");
        if (upperBound <= 0)
        {
            return;
        }

        // FenceDirtyBits is per-cluster (one long word per cluster chunk id). Grow to at least the upper bound.
        if (FenceDirtyBits == null)
        {
            FenceDirtyBits = new long[upperBound];
        }
        else if (FenceDirtyBits.Length < upperBound)
        {
            // Preserve existing dirty bits set during Prep — Array.Resize copies; we just need more tail space for migrations that may target chunk ids beyond
            // the snapshot length.
            var oldLen = FenceDirtyBits.Length;
            var newLen = oldLen;
            while (newLen < upperBound)
            {
                newLen = Math.Max(newLen * 2, upperBound);
            }

            Array.Resize(ref FenceDirtyBits, newLen);
        }

        // Per-cluster AABB + cell-mapping + spatial-index-slot arrays need to cover any newly allocated dst cluster.
        EnsureClusterAabbsCapacity(upperBound);
        EnsureClusterSpatialIndexSlotCapacity(upperBound);
        EnsureClusterCellMapCapacity(upperBound);
        EnsureClusterWriteBookkeepingCapacity(upperBound);

        if (cellUpperBound > 0)
        {
            EnsurePerCellIndexCapacity(cellUpperBound);
        }

        // Deferred-drain list sized to PendingMigrationCount (each migration drains at most one source slot, so the cluster-drain count cannot exceed migration
        // count). _drainedCount is zeroed by Prep.
        var drainCap = Math.Max(16, PendingMigrationCount);
        if (_drainedClusterIds == null || _drainedClusterIds.Length < drainCap)
        {
            _drainedClusterIds = new int[Math.Max(drainCap, (_drainedClusterIds?.Length ?? 0) * 2)];
        }
    }

    // Ping-pong partner for the radix sort, the queue's capacity, grown with it. Only the Migrate phase's Prepare touches either.
    private MigrationRequest[] _migrationSortScratch;
    private int[] _radixCounts;

    /// <summary>
    /// Sort <see cref="PendingMigrations"/> in place by destination cell key so the parallel Migrate phase can give each worker a contiguous slice and have all
    /// of that worker's destination cells be disjoint from every other worker's destination cells. Called from <c>FenceMigrateExecSystem.Prepare</c>, between
    /// Prep and Migrate. Stable since #889 (<see cref="RadixSortByDestCellKey"/>).
    /// </summary>
    internal void SortPendingMigrationsByDestCellKey()
    {
        if (PendingMigrations == null || PendingMigrationCount < 2)
        {
            return;
        }

        RadixSortByDestCellKey(PendingMigrations, PendingMigrationCount);
    }

    /// <summary>
    /// Orders the first <paramref name="count"/> requests by ascending <see cref="MigrationRequest.DestCellKey"/> — <see cref="RadixSort"/>, STABLE, so
    /// requests sharing a cell keep the order they were enqueued in.
    /// </summary>
    /// <remarks>
    /// <para><b>Why not <c>Array.Sort</c> (#889).</b> Measured on Matrix M at 100 % moving: 0.71–0.88 ms per tick for ~5 000 requests, 150–175 ns a
    /// request — introsort's <c>n log n</c> comparisons through an <c>IComparer</c> interface call, each moving a 20-byte struct. It ran on the driver
    /// between the Prep tails and the Migrate dispatch, so every one of those microseconds was fence span. The keys are 32-bit cell ids of which a live
    /// world uses a few thousand, so the radix sort is <c>O(n)</c> with a small constant — one or two passes for a world whose cells sit in a narrow key
    /// range. Measured at 63 µs. The sort itself was lifted into <see cref="RadixSort"/> once other fence sorts wanted it (#891); this is the site that owns
    /// the queue's scratch. The <c>Array.Sort</c> it replaced was kept behind a switch for the A/B and deleted once the numbers were accepted.</para>
    /// <para><b>Signed order.</b> <see cref="DestCellKeyOf"/> flips the sign bit, so negative keys order before positive ones exactly as
    /// <see cref="int.CompareTo(int)"/> would; nothing enqueues a negative destination today, and this costs an XOR.</para>
    /// <para><b>Stable, where <c>Array.Sort</c> was not</b>, and that is a behaviour change worth stating: a cell's requests now apply in enqueue order —
    /// ascending source cluster for crossings, the planner's emission order for repairs — where introsort permuted them. See the remark on
    /// <see cref="MigrationRequest.DestSlotIndex"/>, which was written against the unstable sort.</para>
    /// </remarks>
    internal void RadixSortByDestCellKey(MigrationRequest[] items, int count)
    {
        if (_migrationSortScratch == null || _migrationSortScratch.Length < count)
        {
            _migrationSortScratch = new MigrationRequest[Math.Max(count, items.Length)];
        }

        _radixCounts ??= new int[RadixSort.Buckets];
        RadixSort.Sort<MigrationRequest, DestCellKeyOf>(items.AsSpan(0, count), _migrationSortScratch, _radixCounts);
    }

    /// <summary>The queue sort's key: the destination cell only — that is what the slice planner carves on — sign-flipped so the radix order is
    /// <see cref="int"/> order.</summary>
    /// <remarks>
    /// <b>A secondary sort on the SOURCE cluster chunk id was tried here and REFUTED — do not re-add it without a new measurement.</b> The reasoning
    /// was sound on its face: the migrate loop's first act is <c>GetChunkAddress(srcChunkId, dirty: true)</c>, the accessor window holds three clusters per
    /// page, and <c>ChunkAccessor.LoadAndGet</c> is called 108 473 times in this phase per traced run — the same access pattern #882's counting sort removed
    /// from the shadow drain. An interleaved A/B over five pairs measured <b>0.95x to 1.13x on the Migrate phase, straddling 1.0</b>: no effect. (With a
    /// stable radix sort it would be a second pass on the source key before this one; the reasoning below is why that pass would still buy nothing.)
    /// <para>The reason it cannot help is upstream. <c>DetectClusterMigrations</c> builds this queue by walking <c>dirtyBits</c> in ascending word order, so
    /// requests are appended in ascending SOURCE order already, and a stable sort by destination keeps that order inside every cell; the entries sharing
    /// any one destination cell are few, so there was almost no source disorder left inside a run for a secondary key to fix even when the sort was not
    /// stable. The drain's problem was different in kind — its buffer is in user WRITE order, which is random with respect to chunk id.</para>
    /// </remarks>
    private readonly struct DestCellKeyOf : IRadixKey<MigrationRequest>
    {
        public static ulong Key(in MigrationRequest item) => RadixSort.SignedKey(item.DestCellKey);
    }

    /// <summary>
    /// Order the first <paramref name="count"/> entries of <paramref name="buffer"/> by ascending cluster chunk id, returning a permutation of their indices.
    /// The buffer itself is never permuted — entries are 24 bytes in fixed blocks and moving them would cost more than the ordering saves.
    /// </summary>
    /// <remarks>
    /// <para><b>#882 — why this exists.</b> Shadow entries are appended in the order user code wrote the entities, which is random with respect to cluster
    /// chunk id. The drain resolves a chunk address per entry, and a <see cref="ChunkAccessor{TStore}"/>'s page window holds <b>32 pages</b> against a
    /// cluster archetype that places one or two clusters per page — so a random walk over a few thousand dirty clusters misses on nearly every entry, and a
    /// miss is a dictionary lookup plus three interlocked read-modify-writes on shared <c>PageInfo</c> cache lines. Ascending order turns that into at most
    /// one miss per page. Measured before the change: the drain was <b>43 %</b> of the fence's Prep phase at the 25 % reference point of the #872 matrix,
    /// and almost none of it was B+Tree work.</para>
    /// <para><b>Counting sort, not a comparison sort.</b> O(n + span) against O(n log n): at the reference point n is ~16 000 and an
    /// <see cref="Array.Sort(Array, Array)"/> over that would cost roughly what the misses do. The histogram is cleared over the observed
    /// <c>[min, max]</c> span only, so this stays proportional to the work of the tick.</para>
    /// <para><b>Order among entries of one cluster is preserved</b> (the scatter walks ascending and the buckets fill in encounter order), so within a
    /// cluster the drain sees exactly the sequence it saw before. Across clusters it does not, which is the point; see the rule note on
    /// <c>RejectUniqueIndexCollision</c> in <c>DatabaseEngine.DrainClusterShadowSlots</c>.</para>
    /// </remarks>
    internal ReadOnlySpan<int> BuildShadowDrainOrder(FieldShadowBuffer buffer, int count)
        => BuildDrainOrder(buffer, count, ref _shadowDrainOrder, ref _shadowDrainCounts);

    /// <inheritdoc cref="BuildShadowDrainOrder"/>
    /// <remarks>Static and taking its scratch by reference so the permutation can be tested on its own, without standing an archetype up. The instance
    /// method is the call site; this is the algorithm.</remarks>
    internal static ReadOnlySpan<int> BuildDrainOrder(FieldShadowBuffer buffer, int count, ref int[] scratchOrder, ref int[] scratchCounts)
    {
        Debug.Assert(count <= buffer.Count, "the caller's count must not outrun the buffer, or the clear below cannot retrace what the histogram touched");

        // An empty drain has no min or max to bound, and the sentinels would wrap `max - min + 1` into a bogus span. Callers already skip an empty buffer;
        // this makes the method safe to call directly, which it now is.
        if (count <= 0)
        {
            return default;
        }

        if (scratchOrder.Length < count)
        {
            scratchOrder = new int[Math.Max(count, Math.Max(256, scratchOrder.Length * 2))];
        }

        var order = scratchOrder;

        // One pass to bound the id span. A single-cluster tick then clears exactly one bucket, and the common case — a contiguous run of dirty clusters —
        // clears only that run.
        var min = int.MaxValue;
        var max = int.MinValue;
        for (var e = 0; e < count; e++)
        {
            var clusterChunkId = buffer[e].ChunkId >> 6;
            if (clusterChunkId < min)
            {
                min = clusterChunkId;
            }

            if (clusterChunkId > max)
            {
                max = clusterChunkId;
            }
        }

        var span = max - min + 1;
        if (scratchCounts.Length < span)
        {
            scratchCounts = new int[Math.Max(span, Math.Max(256, scratchCounts.Length * 2))];
        }

        var counts = scratchCounts;

        // Histogram, exclusive prefix sum, scatter.
        for (var e = 0; e < count; e++)
        {
            counts[(buffer[e].ChunkId >> 6) - min]++;
        }

        var running = 0;
        for (var b = 0; b < span; b++)
        {
            var c = counts[b];
            counts[b] = running;
            running += c;
        }

        for (var e = 0; e < count; e++)
        {
            order[counts[(buffer[e].ChunkId >> 6) - min]++] = e;
        }

        // The clear is O(span), and span is the SPREAD of cluster ids rather than the amount of work: two entries at clusters 0 and 30 000 cost a
        // 30 001-int memset to undo two increments. That is stated rather than hidden because an earlier revision of this comment claimed the cost was
        // proportional to the tick's work, and it is not.
        //
        // Retracing the entries instead — zeroing only the buckets the histogram incremented — is O(count) and WRONG: the prefix sum above writes a running
        // total into EVERY bucket of the span, empty ones included, so a retrace leaves those non-zero and the next call scatters through a poisoned
        // histogram. Caught by ShadowDrainOrderPermutationTests.TheScratchBuffersAreReusableAcrossCallsWithDifferentShapes, which is exactly what it is for.
        //
        // The memset stands because span is bounded by the archetype's cluster capacity (a few thousand, so tens of microseconds at the very worst) and
        // because it is a memset against a pass whose per-entry cost is a page-window probe. If a workload ever makes it matter, the fix is a different
        // algorithm — a two-pass radix on the low bits, whose buckets are bounded by the radix and not by the id spread — not a cheaper clear.
        Array.Clear(counts, 0, span);
        return new ReadOnlySpan<int>(order, 0, count);
    }

    /// <summary>
    /// Record a cluster that's been drained to empty by ReleaseSlot. Lock-free append via <see cref="Interlocked.Increment(ref int)"/>. Capacity is guaranteed
    /// by <see cref="PreSizeMigrationBuffers"/>. Same cluster may legitimately be recorded twice if a previous tick's drain wasn't followed by a finalize and
    /// this tick re-empties it after a refill+drain cycle — <see cref="DrainPendingClusterFinalizations"/> re-checks occupancy after the phase barrier and
    /// skips non-empty entries. The <see cref="_finalizeLock"/> acquisition below covers only the fallback grow, never the append or the later re-check.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RecordClusterDrain(int clusterChunkId)
    {
        var idx = Interlocked.Increment(ref _drainedCount) - 1;
        if (_drainedClusterIds == null || idx >= _drainedClusterIds.Length)
        {
            // PreSizeMigrationBuffers should have covered this — fall back to a synchronized grow.
            ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
            _finalizeLock.Enter(ref nullCtx);
            try
            {
                if (_drainedClusterIds == null)
                {
                    _drainedClusterIds = new int[Math.Max(16, idx + 1)];
                }
                else if (_drainedClusterIds.Length <= idx)
                {
                    var newLen = _drainedClusterIds.Length * 2;
                    while (newLen <= idx)
                    {
                        newLen *= 2;
                    }

                    Array.Resize(ref _drainedClusterIds, newLen);
                }
            }
            finally
            {
                _finalizeLock.Exit();
            }
        }
        _drainedClusterIds[idx] = clusterChunkId;
    }

    /// <summary>
    /// Walk the deferred-drain list serially (called once per archetype from <see cref="DatabaseEngine.FinalizeArchetypeFence"/> after all Migrate-phase slices
    /// have completed). For each drained cluster, re-check occupancy: if still empty, run finalize + free; if a concurrent Claim re-filled it during Migrate,
    /// leave it alone. Resets <see cref="_drainedCount"/> to zero.
    /// <para>
    /// <b>Concurrency invariant:</b> Finalize-for-one-archetype runs on exactly one worker (one work item per archetype, dispatched atomically). By the time
    /// this method runs, the Migrate and AabbRefresh phase barriers have both passed — no concurrent ClaimSlotInCell or ReleaseSlot can mutate this archetype's
    /// clusters. The occupancy re-read is therefore single-threaded and the per-archetype lock is unnecessary here.
    /// </para>
    /// <para>
    /// Note: a cluster can appear in the drain list AND have non-zero occupancy if a Migrate-phase Claim refilled it after the drain was recorded. That's a
    /// Migrate-phase Claim arriving AFTER a Migrate-phase Release: legal because the cluster was still in the cell's claim list, and the Claim correctly
    /// re-occupied it. Skip the finalize.
    /// </para>
    /// </summary>
    internal void DrainPendingClusterFinalizations(SpatialGrid grid)
    {
        var count = _drainedCount;
        if (count == 0)
        {
            return;
        }

        var ids = _drainedClusterIds;
        var hasCluster = ClusterSegment != null;
        // `using`, because GetChunkAddress(chunkId, dirty: true) below registers an ActiveChunkWriter on each
        // chunk's page and only Dispose -> CommitChanges releases it (CP-13). Without it every drained cluster left
        // one ACW on its page forever, so CP-11 skipped that page in EVERY checkpoint cycle: CK-03's coverage gate
        // never opened, CheckpointLSN never advanced, no WAL segment was ever recycled, and the log grew at the
        // full write rate until the writer stalled and the process died with WalBackPressureTimeout (#817).
        // Disposing a `default` accessor is safe — Dispose returns immediately when _segment is null.
        using var clusterAccessor = hasCluster ? ClusterSegment.CreateChunkAccessor() : default;
        using var transientAccessor = TransientSegment?.CreateChunkAccessor() ?? default;

        for (var i = 0; i < count; i++)
        {
            var chunkId = ids[i];
            var clusterBase = hasCluster ? clusterAccessor.GetChunkAddress(chunkId, true) : transientAccessor.GetChunkAddress(chunkId, true);
            if (*(ulong*)clusterBase != 0)
            {
                continue; // Claim re-filled this cluster after the drain — keep alive
            }

            FinaliseEmptyClusterCellState(grid, chunkId);
            RemoveFromActiveList(chunkId);
            ResetClusterVisibility(chunkId);   // the id is about to be recyclable — see ResetClusterVisibility
            ClusterSegment?.FreeChunk(chunkId);
            TransientSegment?.FreeChunk(chunkId);
        }
        _drainedCount = 0;
    }

    /// <summary>
    /// Per-cluster cell membership for spatial archetypes (issue #229 Phase 1+2). Flat array indexed by <c>clusterChunkId</c>, value is the spatial
    /// grid <c>cellKey</c> the cluster is attached to, or <c>-1</c> if unmapped (cluster not yet allocated, or archetype is not opted into the grid).
    /// </summary>
    /// <remarks>
    /// Lazily allocated by <see cref="ClaimSlotInCell(int, ref ChunkAccessor{PersistentStore}, ChangeSet, SpatialGrid, long)"/> or
    /// <see cref="RebuildCellState"/>. Non-spatial archetypes and spatial archetypes running without a configured <see cref="SpatialGrid"/> leave this
    /// field <c>null</c> — the existing <see cref="ClaimSlot(ref ChunkAccessor{PersistentStore}, ChangeSet, long)"/> path is unchanged for them.
    /// </remarks>
    public int[] ClusterCellMap;

    // ═══════════════════════════════════════════════════════════════════════
    // Migration queue (issue #229 Phase 3). Lazily allocated; only used when
    // SpatialSlot.HasSpatialIndex AND a SpatialGrid is configured AND cell crossings
    // actually occur. Population is sequential (detection loop runs single-threaded
    // inside WriteClusterTickFence), drained by ExecuteMigrations in the same loop.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype pending migration queue. Populated by cell-crossing detection in
    /// <c>DetectClusterMigrations</c>, drained by <see cref="DatabaseEngine.ExecuteMigrations"/> at the tick fence.
    /// Null until the first cell-crossing is detected.</summary>
    internal MigrationRequest[] PendingMigrations;

    /// <summary>
    /// How many entries of <see cref="PendingMigrations"/> this tick's Migrate phase is draining — the prefix
    /// <c>[0, PendingMigrationDrainCount)</c>. Snapshotted at the end of Prep and consumed by the Finalize compaction.
    /// </summary>
    /// <remarks>
    /// <para><b>The queue has two producers on opposite sides of its consumer.</b> <c>DetectClusterMigrations</c> files during Prep, which precedes Migrate;
    /// <c>FlagOutliersForMigration</c> and step 10's drift detection file during AabbRefresh, which follows it. A phase that drains "everything" and a Finalize
    /// that then zeroed the count therefore destroyed the second producer's work every tick — which is why the outlier guard could fire, report itself in
    ///  telemetry, and never migrate anything.</para>
    /// <para>Recording the prefix is what lets Finalize remove exactly what was executed and keep the rest for the next tick, turning this back into the queue
    /// its callers already believed it was.</para>
    /// </remarks>
    internal int PendingMigrationDrainCount;

    /// <summary>
    /// Drop the prefix this tick's Migrate phase executed, keeping requests filed after it for the next tick.
    /// </summary>
    /// <remarks>
    /// Single-threaded: called from <c>FinalizeArchetypeFence</c>, after the Migrate and AabbRefresh barriers, so no producer or consumer is in flight.
    /// The move is a straight <c>Array.Copy</c> of the tail over the head — the queue is small (a tick's migrants) and the ordering it preserves is the arrival
    /// order the drain relies on.
    /// </remarks>
    internal void CompactPendingMigrations()
    {
        var drained = PendingMigrationDrainCount;
        PendingMigrationDrainCount = 0;

        if (drained <= 0)
        {
            return;
        }

        var remaining = PendingMigrationCount - drained;
        if (remaining > 0 && PendingMigrations != null)
        {
            Array.Copy(PendingMigrations, drained, PendingMigrations, 0, remaining);
            PendingMigrationCount = remaining;
            return;
        }

        PendingMigrationCount = 0;
    }

    /// <summary>Number of valid entries in <see cref="PendingMigrations"/>. Reset to zero at the start
    /// of every <see cref="DatabaseEngine.ExecuteMigrations"/> call.</summary>
    internal int PendingMigrationCount;

    /// <summary>Telemetry counter: number of migrations executed in the most recently completed tick.</summary>
    public int LastTickMigrationCount;

    /// <summary>Telemetry counter: number of position changes that crossed the raw cell boundary but were
    /// absorbed by the hysteresis margin (no migration queued). Useful for tuning
    /// <see cref="SpatialGridConfig.MigrationHysteresisRatio"/>.</summary>
    public int LastTickHysteresisAbsorbedCount;

    /// <summary>
    /// Write-time crossing flags the drain found describing an entity that is home — written out and back within the tick, or a spawn's slot a
    /// writer reached before its data landed. Dropped, not executed (CC-02); see <c>DrainPreFlaggedMigrations</c>.
    /// </summary>
    public int LastTickStaleFlagsDropped;

    /// <summary>Telemetry counter: wall-clock duration of <see cref="DatabaseEngine.ExecuteMigrations"/> in milliseconds,
    /// for the most recently completed tick.</summary>
    /// <remarks>
    /// <b>The migrant LOOP only, which is well under half of what a migration costs.</b> Since #872 step 6 that loop <i>stages</i> each migrant's index
    /// value updates and, since step 7, its EntityMap patch; both are applied later, by the IndexMassUpdate and EntityMapUpdate phases, outside this
    /// bracket. The secondary index alone was measured at ~48 % of a migration's total cost, so anything deriving a per-entity cost from this field
    /// under-counts by roughly half. Use <see cref="LastTickMigrationTotalMs"/> for that.
    /// </remarks>
    public double LastTickMigrationExecuteMs;

    /// <summary>
    /// Telemetry counter: <see cref="Stopwatch"/> ticks spent APPLYING the migrant loop's staged work — the bulk index descent and the bulk EntityMap
    /// patch — in the most recently completed tick, summed across every worker that took a slice (#872 step 11).
    /// </summary>
    /// <remarks>
    /// <para><b>Why it had to exist.</b> Step 11's budget admits repair units against a projected <c>entities x ns</c>, and step 12 shipped that
    /// projection reading a hand-set constant. Replacing the constant with a measurement is only an improvement if the measurement covers the whole
    /// migration: an estimator built on <see cref="LastTickMigrationExecuteMs"/> alone excludes both apply phases and would over-admit by about 2x every
    /// tick — worse than the constant it replaces.</para>
    /// <para><b>Ticks, not milliseconds, and <see cref="Interlocked"/> rather than a CAS loop.</b> Seven call sites accumulate into this from parallel
    /// workers — the two serial helpers on <c>ProcessArchetypeFence</c>, and both the <c>Prepare</c> and <c>DispatchItem</c> halves of the two parallel
    /// exec systems, the index one of which also accumulates once per indexed field. A <see cref="long"/> add is one uncontended
    /// atomic; the double next door needs a compare-exchange loop because .NET has no <c>Interlocked.Add(double)</c>. The conversion to milliseconds
    /// happens once, in <see cref="LastTickMigrationTotalMs"/>.</para>
    /// </remarks>
    public long LastTickMigrationApplyTicks;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // #912 — what LastTickMigrationExecuteMs is a sum OVER.
    //
    // That field is a sum of per-SLICE spans divided, by every consumer, by a per-ENTITY count. The slice count is not a constant: FenceWorkPlan sizes the
    // Migrate phase's slices from `2 x WorkerCount x FenceChunkOversubscription`, so raising W raises the number of spans summed into the numerator while the
    // denominator is fixed by the workload. Every per-slice fixed cost inside the bracket — three CreateChunkAccessor rentals and the span construction — is
    // therefore charged to every entity, once per slice.
    //
    // Without the three counters below, "CPU per entity rose 3.4x from W = 2 to W = 8" cannot be told apart from "the same work was cut into four times as
    // many pieces". §5.8.1 recorded that ratio as an anomaly and left it unowned for three steps precisely because nothing published the denominator.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Telemetry counter: <c>ExecuteMigrations</c> slices that ran in the most recently completed tick — the number of spans summed into
    /// <see cref="LastTickMigrationExecuteMs"/>. One on the serial fence; <c>FenceWorkPlan</c>'s slicing decides it on the parallel one.</summary>
    /// <remarks>
    /// <b>Read it as the denominator's denominator.</b> <c>MigrationExecuteMs / MigrationCount</c> is a per-entity figure only while this is 1; above that
    /// it also carries <c>slices x per-slice fixed cost / entities</c>, which grows with the worker count on a workload of fixed size.
    /// </remarks>
    public int LastTickMigrationSliceCount;

    /// <summary>Telemetry counter: zone-map batches a tick's Migrate slices opened — one per indexed field per slice (#926).</summary>
    /// <remarks>
    /// <para><b>The counter that proves the latch is no longer taken per migrant.</b> Before #926 the Migrate loop called <c>ZoneMapArray.Widen</c> once per
    /// migrant per indexed field, and each call took the archetype-wide grow latch shared — two atomics on one cache line, from every worker, tens of
    /// thousands of times a tick. It now opens one batch per field per slice instead, and the identity that pins it is
    /// <c>ZoneMapBatchOpens == MigrationSliceCount x indexed fields</c>: a value INDEPENDENT of the migration count, which is exactly the property that
    /// fails the moment a per-migrant acquire survives anywhere on the path.</para>
    /// <para>Always counted, never <c>[Conditional("DEBUG")]</c>: the measurement it backs runs in Release, and a Debug-only counter used as proof is green
    /// in Debug and absent where it matters.</para>
    /// </remarks>
    public int LastTickZoneMapBatchOpens;

    /// <summary><see cref="Stopwatch"/> ticks the tick's Migrate slices spent BEFORE their first migrant — the span construction and the three chunk-accessor
    /// rentals — summed across slices.</summary>
    /// <remarks>
    /// Inside <see cref="LastTickMigrationExecuteMs"/>, not beside it: the bracket has always covered the rentals, and #911 records why moving either end of
    /// it silently breaks every timeline comparison against an older trace. This measures that prologue rather than relocating it, so the two can be
    /// subtracted without changing what the span means. Two <see cref="Stopwatch.GetTimestamp"/> calls per SLICE — tens of them per tick, not thousands.
    /// </remarks>
    public long LastTickMigrationPrologueTicks;

    /// <summary><see cref="Stopwatch"/> ticks the tick's Migrate slices spent AFTER their last migrant — the three accessor disposals and the span's own
    /// publish — summed across slices.</summary>
    /// <inheritdoc cref="LastTickMigrationPrologueTicks"/>
    public long LastTickMigrationEpilogueTicks;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Executed migrations, split by kind. #911 gave the three-way split to the PROFILER SPAN, behind TelemetryConfig.ProfilerActive; these are the same three
    // numbers on the always-on telemetry surface.
    //
    // The distinction matters because the profiler is the wrong instrument for this question. A Migrate slice mixes all three kinds by construction — the
    // queue is sorted by destination cell key, not by kind — so attributing a cost to relocation rather than to crossing needs the split; and turning the
    // profiler on to get it changes the measurement, because the ring emits from inside the bracket being measured. §5.8's per-kind findings were all reached
    // with throwaway counters on a branch for exactly this reason.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Telemetry counter: cell-crossing migrations executed in the most recently completed tick.</summary>
    /// <remarks>This and its two siblings sum EXACTLY to <see cref="LastTickMigrationCount"/>, which is what makes the split checkable rather than
    /// trusted — the same property #911 gave the trace record.</remarks>
    public int LastTickCrossingsExecuted;

    /// <summary>Telemetry counter: intra-cell relocations executed in the most recently completed tick.</summary>
    /// <inheritdoc cref="LastTickCrossingsExecuted"/>
    public int LastTickRelocationsExecuted;

    /// <summary>Telemetry counter: repair moves executed in the most recently completed tick.</summary>
    /// <inheritdoc cref="LastTickCrossingsExecuted"/>
    public int LastTickRepairsExecuted;

    /// <summary>Telemetry counter: exclusive acquisitions of the archetype-wide finalize latch in the most recently completed tick.</summary>
    /// <remarks>
    /// <para><b>The one archetype-wide serialisation point a fence worker can reach.</b> Twenty-one call sites take it — the three bulk enqueues, the
    /// per-archetype array growths, the new-cluster slow paths and the cell-tree segment creation — and a phase whose per-worker cost rises with W is
    /// contended here or nowhere. A count rather than a held-time: see <c>PaddedFinalizeLock.Enter</c>.</para>
    /// <para>The reading that answers #912: run the same workload at two worker counts. This staying flat while per-entity CPU rises eliminates the latch;
    /// this rising with W names it.</para>
    /// </remarks>
    public long FinalizeLockAcquisitions => _finalizeLock.Acquisitions;

    /// <summary>Zero <see cref="FinalizeLockAcquisitions"/> for the tick about to run. Called from Prep with the rest of the per-tick block.</summary>
    internal void ResetFinalizeLockAcquisitions() => _finalizeLock.Acquisitions = 0L;

    /// <summary>
    /// <see cref="Stopwatch"/> ticks the last Finalize spent emitting this archetype's fence WAL records — the block loop and the collection-content walk —
    /// against everything Finalize does before it. One plain store by the atomic item's worker, or an <c>Interlocked.Add</c> per emit slice (#889) — a CPU
    /// sum, not a span, on a sliced tick. Read after the fence DAG has joined.
    /// </summary>
    public long LastTickFinalizeEmitTicks;

    /// <summary>The part of <see cref="LastTickFinalizeEmitTicks"/> spent inside the WAL append (measure, claim, codec copy, publish), as opposed to walking
    /// the dirty words and resolving cluster pages. Same summation convention.</summary>
    public long LastTickFinalizeAppendTicks;

    /// <summary>
    /// The columns Finalize's head narrowed the WAL emit to, published for the emit slices (#889). Sized to the archetype's component count once and
    /// reused tick over tick; only the head writes it and only after the AabbRefresh barrier, only the emit reads it and only before Finalize's own.
    /// </summary>
    internal sealed class FenceEmitPlan
    {
        public readonly int[] SlotIndices;
        public readonly int[] CompSizes;
        public readonly int[] CompOffsets;
        public int ColumnCount;
        public int TotalCompSize;
        public int EntityIdsOffset;
        public ulong[] ColumnHandleRanges = [];
        public int HandleRangeCount;

        public FenceEmitPlan(int componentCount)
        {
            SlotIndices = new int[componentCount];
            CompSizes = new int[componentCount];
            CompOffsets = new int[componentCount];
        }

        public void EnsureHandleRanges(int count)
        {
            if (ColumnHandleRanges.Length < count)
            {
                ColumnHandleRanges = new ulong[count];
            }
        }
    }

    /// <summary>See <see cref="FenceEmitPlan"/>. Null until the archetype's first emit.</summary>
    internal FenceEmitPlan FenceEmit;

    /// <summary>Finalize's head ran on the driver this tick (#889): the planner must not emit the atomic item, whatever <see cref="FinalizeSliceable"/>
    /// says.</summary>
    internal bool FinalizeHeadRan;

    /// <summary>The head ran and found something to emit: the planner carves <c>FenceDirtyBits</c> into emit slices.</summary>
    internal bool FinalizeSliceable;

    /// <summary>
    /// The whole cost of the most recently completed tick's migrations, in milliseconds: the migrant loop plus both apply phases, summed across workers.
    /// This is the number a per-entity cost model divides by <see cref="LastTickMigrationCount"/>.
    /// </summary>
    /// <remarks>
    /// <b>CPU-milliseconds, not span.</b> Every contributing site adds its own elapsed time, so W workers each busy for 1 ms report 4, not 1. That is the
    /// unit <c>SpatialGridConfig.ReclusterBudgetMs</c> is defined in and the unit <c>RepairNsPerEntity</c> was measured in; reading it as a frame latency
    /// would over-state the cost by up to W.
    /// </remarks>
    public double LastTickMigrationTotalMs =>
        LastTickMigrationExecuteMs + (Volatile.Read(ref LastTickMigrationApplyTicks) * 1000d / Stopwatch.Frequency);

    /// <summary>
    /// Live accumulator for hysteresis-absorbed crossings on the <see cref="SpatialBarrierOnly"/> path, bumped by <c>ClusterRef.MaybeFlagMigration</c> as writes
    /// happen and drained into <see cref="LastTickHysteresisAbsorbedCount"/> at the top of the next fence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because the barrier-only path could not count absorption at all. <c>DatabaseEngine.DetectClusterMigrations</c> only increments inside step (b),
    /// its legacy dirty-bits scan, and the barrier-only branch returns before reaching it — so the counter that tunes
    /// <see cref="SpatialGridConfig.MigrationHysteresisRatio"/> read a structural zero on precisely the path both demos use. Counting has to happen where the
    /// decision is made, which is at write time.
    /// </para>
    /// <para>
    /// <b>Non-atomic on purpose</b>, matching <see cref="MigrationHint"/>: many workers write one archetype's spatial field per tick, and a lost increment under
    /// contention costs a fraction of a ratio that is read for order-of-magnitude tuning. An <c>Interlocked</c> here would put an uncontended-but-shared atomic
    /// on the hottest write path in the engine to buy precision nothing consumes.
    /// </para>
    /// </remarks>
    public int HysteresisAbsorbedLive;

    /// <summary>
    /// The value <see cref="LastTickMigrationCount"/> held for the PREVIOUS tick, snapshotted immediately before the fence zeroes it.
    /// </summary>
    /// <remarks>
    /// Sizes the <c>PendingMigrations</c> queue in <c>DatabaseEngine.DetectClusterMigrations</c>. That pre-sizing used to read
    /// <see cref="LastTickMigrationCount"/> directly, but <c>PrepareArchetypeFence</c> zeroes that field earlier in the SAME fence, so the estimate was always
    /// <c>Max(16, 0)</c> and the queue regrew from 16 on every migration-heavy tick — the amortisation the code was written to get never happened.
    /// </remarks>
    public int PreviousTickMigrationCount;

    /// <summary>
    /// Telemetry counter: clusters examined by the intra-cell drifter scan in the most recently completed tick.
    /// <para>Produced since #872 step 10 by <c>RecomputeDirtyClusterAabbsSlice</c>. Counts clusters WRITTEN this tick, not clusters that exist: a settled world
    /// scans nothing, which is the denominator that makes <see cref="LastTickDriftersDetected"/> mean something. Zero is no longer ambiguous between "not
    /// built" and "nothing moved" — it now means the latter.</para>
    /// </summary>
    public int LastTickClustersScanned;

    /// <summary>
    /// Telemetry counter: entity slots the AABB refresh actually WALKED in the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// <para>The denominator of the refresh's own cost, and the one number that shows whether the pass is doing work proportional to what MOVED or
    /// proportional to what EXISTS. <see cref="LastTickClustersScanned"/> cannot answer that: it is incremented after the <c>boundsMoved</c> skip, so it
    /// counts clusters that had something to say, not clusters the pass opened and read.</para>
    /// <para>Added with the fix that gated the non-barrier arm on <c>ClusterNeedsAabbRecompute</c>. Before it, this counter would have read
    /// <c>activeClusters x occupancy</c> on every tick regardless of motion — 63 000 on a 64 000-entity world where 640 entities moved. It exists so that
    /// regression cannot come back unmeasured.</para>
    /// </remarks>
    public int LastTickSlotsScanned;

    /// <summary>
    /// Set to 1 when <c>ClusterRef.GetSpan</c> hands out a mutable span over this archetype's spatial column; cleared by
    /// <see cref="ClearAabbRefreshBookkeeping"/> once the refresh has consumed it.
    /// </summary>
    /// <remarks>
    /// <para>The escape hatch for the one writer that cannot be tracked per cluster. Every other path leaves a per-cluster signal — a dirty bit, a shrink
    /// mask, a process bit — and <see cref="ClusterNeedsAabbRecompute"/> reads those. A raw span leaves nothing at all, and the caller is not required to say
    /// whether it even wrote, so the only honest per-cluster answer would be "assume all of them" — which is this flag.</para>
    /// <para><b>It is a fallback, not a mode.</b> Setting it costs the archetype one tick of the unconditional walk the gate exists to remove; it does not
    /// disable the gate, and the next tick is gated again unless a span is handed out again. An archetype that moves entities through <c>OpenMut</c> or
    /// <c>WriteSpatial</c> — everything the engine's own paths use — never sets it.</para>
    /// </remarks>
    public int SpatialSpanHandedOut;

    /// <summary>
    /// Slots genuinely VACATED this tick — a destroy, not a migration. Reset by <see cref="ClearAabbRefreshBookkeeping"/>.
    /// </summary>
    /// <remarks>
    /// <para>Exists to make one gate provable. The shadow drain would like to skip an index slot whose component nothing wrote — its keys cannot have moved,
    /// so every entry for it compares equal and the walk is pure cost (at 100 % moving that is 64 000 entries for a field the benchmark never writes, 22 % of
    /// the whole fence). The obstacle is the drain's <c>occupancy == 0</c> branch: it is also the destroy-side REMOVAL for fence-maintained slots, so an
    /// entity that was written for component T and then destroyed relies on that walk to take its indexed component S out of the tree — and S is exactly what
    /// the written-slot test would skip.</para>
    /// <para>A count of releases settles it without guessing: <b>zero releases ⟹ that case cannot exist</b>, and the gate is exact. A tick with destroys pays
    /// the full drain exactly as before. Migration releases are excluded (<c>deferFinalize</c>) because a move is not a destroy — the entity is still indexed,
    /// at its new location.</para>
    /// </remarks>
    public int SlotReleasesThisTick;

    /// <summary>Tick counter driving the zone-map exact-re-derive rotation. Wraps harmlessly; only its low bits are read.</summary>
    internal int ZoneMapRetightenTick;

    /// <summary>
    /// Telemetry counter: entities the intra-cell scan found outside their cluster's target region in the most recently completed tick.
    /// <para>Produced since #872 step 10. Counts DETECTION, not outcome: an entity is counted the moment the target-region rule rejects it, whether or not
    /// placement then found it a home. See <see cref="LastTickClustersScanned"/>.</para>
    /// </summary>
    public int LastTickDriftersDetected;

    /// <summary>
    /// Wave-2 counter (K1): clusters that passed the intra-cell drift gate in the most recently completed tick — the population the centre gather,
    /// the overshoot test and candidate selection ran on. Folded per slice like <see cref="LastTickDriftersDetected"/>.
    /// </summary>
    public int LastTickDriftGatedClusters;

    /// <summary>
    /// Clusters that exceeded the configured floor (<c>ClusterTargetExtentRatio</c>) but not their cell's density-derived target, so they were neither
    /// drift-gated nor repair-gated and the drift scan never ran on them — the work step 14's target function removed. Disjoint from
    /// <see cref="LastTickDriftGatedClusters"/>, not a subset of it.
    /// </summary>
    public int LastTickDriftSuppressedByDensity;

    /// <summary>
    /// Telemetry counter: intra-cell drifters left in place because they were inside the drift dead zone, in the most recently completed tick.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT folded into <see cref="LastTickHysteresisAbsorbedCount"/>. That counter measures the margin around a CELL boundary and is the
    /// input that tunes <c>MigrationHysteresisRatio</c>; this one measures the margin around a cluster's TARGET REGION and tunes
    /// <c>ClusterDriftMarginRatio</c>. Summing them would produce a number that tunes neither, and the two cannot even fire for the same entity — the
    /// cell-crossing detectors emit only when the cell key changes, which an intra-cell drifter by definition does not do.
    /// </remarks>
    public int LastTickDriftAbsorbedCount;

    /// <summary>
    /// Telemetry counter: milliseconds of the per-tick re-clustering budget consumed in the most recently completed tick.
    /// <b>No producer yet</b> — reads zero until step 11 (throttle, priority queue, safety valve) of the VDB partitioning design.
    /// See <see cref="LastTickClustersScanned"/>.
    /// </summary>
    public double LastTickReclusterBudgetUsedMs;

    /// <summary>
    /// Telemetry counter: migrations executed since this cluster state was created. Unlike <see cref="LastTickMigrationCount"/>, which is reset at the top of
    /// every fence, this one only grows — a per-tick gauge sampled asynchronously (an OTel consumer polls every few seconds) reports one arbitrary tick out of
    /// hundreds and cannot answer "what is the migration RATE", which is what sizes the throttle budget.
    /// <para><b>Not engine lifetime.</b> <c>DatabaseEngine.InitializeArchetypes</c> reallocates <c>_archetypeStates</c>, so a repeat call discards every
    /// cluster state and this counter restarts at zero. That call is rare and explicitly tolerated; the alternative — hoisting the counter to survive it — would
    /// attribute one database's migrations to the next.</para>
    /// </summary>
    public long TotalMigrationCount;

    /// <summary>
    /// Telemetry counter: boundary crossings absorbed by the hysteresis margin since this cluster state was created. Cumulative counterpart to
    /// <see cref="LastTickHysteresisAbsorbedCount"/>; see <see cref="TotalMigrationCount"/> for why the cumulative form is the one a scrape can use, and for
    /// the repeat-<c>InitializeArchetypes</c> caveat that applies here too.
    /// <para><b>Reads zero on the barrier-only path</b>, which is the path both demos use. Its source
    /// <c>DatabaseEngine.DetectClusterMigrations</c> only ever increments inside step (b), the legacy dirty-bits scan; the
    /// <see cref="SpatialBarrierOnly"/> branch returns before reaching it, and <c>ClusterRef.WriteSpatial</c> — the barrier-only writer — returns without
    /// counting when a move stays inside the margin. So a ratio against <see cref="TotalMigrationCount"/> tunes
    /// <see cref="SpatialGridConfig.MigrationHysteresisRatio"/> ONLY for archetypes that have not opted into barrier-only. See #872.</para>
    /// </summary>
    public long TotalHysteresisAbsorbedCount;

    /// <summary>
    /// Coarse work-estimate counter bumped on every cell-crossing flagged by <c>WriteSpatial</c>. Read and reset (snapshot-then-zero) by the fence work-planner
    /// to size the per-archetype migration cost. Non-atomic on purpose: order-of-magnitude is enough for chunk bucketing; lost increments under contention are
    /// tolerable.
    /// </summary>
    internal int MigrationHint;

    /// <summary>
    /// Test observation hook: length (in long words) of the <c>dirtyBits</c> snapshot at the end of <c>ExecuteMigrations</c>. Used by regression tests to
    /// verify the snapshot was grown when migration allocated a brand-new destination cluster whose chunk id exceeded the pre-migration length.
    /// Zero when no migrations ran.
    /// </summary>
    public int LastMigrationDirtyBitsWordCount;

    /// <summary>Per-entity dirty tracking for tick fence WAL serialization. Index = clusterChunkId * 64 + slotIndex.</summary>
    public DirtyBitmap ClusterDirtyBitmap;

    /// <summary>
    /// Per-cluster tight 2D AABB plus category mask for spatially-active clusters (issue #230).
    /// Indexed by clusterChunkId. Populated by spawn/destroy/migration hooks and the tick-fence recompute pass. Null for non-spatial archetypes or before the
    /// first spatial write. In-memory only — rebuilt at startup via <see cref="RebuildClusterAabbs"/> from entity positions (Q2/Q6 transient-state decision).
    /// Phase 1 is 2D f32 only.
    /// </summary>
    internal ClusterSpatialAabb[] ClusterAabbs;

    /// <summary>
    /// Per-cluster back-pointer into its cell's <see cref="CellSpatialIndex.ClusterIds"/> SoA array.
    /// <c>-1</c> for clusters not currently in the per-cell index (non-spatial archetypes, Static-mode archetypes in Phase 1, or before the first insertion).
    /// Indexed by clusterChunkId.
    /// </summary>
    internal int[] ClusterSpatialIndexSlot;

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // MVCC visibility summary (H1). The SoA scan's born/died gate does an EntityMap point-read PER MATCH — measured at 166-241 ns/entity against 27 ns for the
    // entire rest of the scan, because XxHash32 full avalanche scatters consecutive keys into unrelated buckets. These two arrays answer "can any entity in
    // this cluster be invisible to a reader at txTsn?" from one sequential read, letting a clean cluster skip the probe for every slot it holds.
    //
    // The summary is CONSERVATIVE in one direction only: it may say "probe" when probing was unnecessary (slower, still correct), and must never say "clean"
    // when an entity could be invisible. Every value therefore starts at the pessimistic end and is only relaxed by a site that knows the true TSN.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Per-cluster maximum <c>BornTSN</c> over the entities it holds, or <see cref="VisibilityUnknown"/> when no site has established it. Indexed by
    /// clusterChunkId. A reader at snapshot <c>txTsn</c> may skip the per-entity born check for this cluster iff the value is <see cref="VisibilityUnknown"/>
    /// -free and <c>&lt;= txTsn</c>.
    /// </summary>
    /// <remarks>
    /// In-memory only, like <see cref="ClusterAabbs"/> — a reopen rewrites every record with <c>BornTSN = 0</c> (committed before this open, visible at every
    /// snapshot), so the rebuild seeds 0 rather than persisting anything.
    /// </remarks>
    internal long[] ClusterMaxBornTsn;

    /// <summary>
    /// Per-cluster maximum <c>DiedTSN</c> over the entities it has ever held, or 0 when nothing has died there. Indexed by clusterChunkId. A reader at
    /// snapshot <c>txTsn</c> may skip the per-entity died check for this cluster iff the value is <c>&lt;= txTsn</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This was a sticky bit until #722.</b> "Has anything ever died here?" is a question that can only be un-asked by a full re-scan, so the bit was never
    /// cleared and a churning archetype latched permanently onto the per-entity probe — a documented ceiling for <c>Count()</c>, and fatal for anything that
    /// runs every tick, which is what an unfiltered View's refresh does.
    /// </para>
    /// <para>
    /// The question a reader actually needs answered is not <i>whether</i> something died but <i>whether every death here is already visible to it</i>, and
    /// that is a maximum rather than a boolean. <c>ReleaseSlot</c> clears the occupancy bit at destroy commit while the tombstone lives on the EntityMap
    /// record, so the only hazard a death creates is for a reader OLDER than it: occupancy has already dropped an entity that reader must still see. A reader
    /// newer than every death in the cluster is exact. A maximum only ever rises, so it needs no re-scan to fall — it never falls — and the gate recovers on
    /// its own as snapshots advance past the last death.
    /// </para>
    /// </remarks>
    internal long[] ClusterMaxDiedTsn;

    /// <summary>Sentinel for "no site has established this cluster's maximum BornTSN" — forces the per-entity probe.</summary>
    internal const long VisibilityUnknown = long.MaxValue;

    /// <summary>
    /// Why a FRESHLY ALLOCATED cluster is left at <see cref="VisibilityUnknown"/> by the claim, while an existing one has its bound raised before the CAS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two directions need opposite treatment, and doing the same thing to both is a bug either way round.
    /// </para>
    /// <para>
    /// <b>Existing cluster — fold BEFORE the publishing CAS.</b> The hazard is an OLDER reader: it sees the new bit paired with a bound that predates the
    /// entity, so the gate vouches for a cluster holding an entity born after its snapshot. Raising first closes that, and raising early is free because the
    /// bound is conservative upward.
    /// </para>
    /// <para>
    /// <b>Fresh cluster — do NOT fold; leave it unknown.</b> The hazard is a NEWER reader. The claim publishes the occupancy bit before the caller writes the
    /// slot's EntityId and its EntityMap record, so a reader at or past <c>bornTsn</c> that passed the gate would count a slot whose entity does not exist yet
    /// — and <c>EcsQuery.TryCountViaOccupancy</c> has no per-entity probe to catch it. An unestablished bound denies the gate outright, which is exactly the
    /// protection a fresh cluster had before the fold moved into the claim. The caller establishes the real value once the slot's contents exist.
    /// </para>
    /// <para>
    /// <b>Why the caller may safely ESTABLISH rather than merely raise.</b> <see cref="NoteClusterBorn"/> overwrites an unknown bound with whatever it is
    /// given, which would lose a higher value on a populated cluster. A fresh cluster holds exactly one entity, so the value the caller establishes is exact.
    /// Do not extend that establish-from-unknown path to clusters that already hold entities.
    /// </para>
    /// <para>
    /// <b>Known residual, NOT fixed here.</b> The same half-written-slot window still exists for an EXISTING cluster whose bound was already established: the
    /// bit is published by the claim and the EntityMap record is written by the caller, so a newer reader can count a slot in flight. That predates this change
    /// — the fold moving into the claim neither created nor widened it — and closing it needs a two-phase reserve/publish primitive rather than more fold
    /// ordering, because the defect is what the SLOT contains, not what the summary says.
    /// </para>
    /// </remarks>
    internal const string FreshClusterStaysUnknown = "see the remarks on this field";

    /// <summary>
    /// Clears a destination slot whose entity turned out to be gone from the EntityMap: occupancy bit, entity id, and every component's enabled bit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Safe from any thread, which is what lets it run in a bucket-partitioned phase.</b> The occupancy and enabled-bit clears are <c>Interlocked.And</c>
    /// against words shared with other slots; the entity-id store is a plain write to a slot this migrant claimed exclusively. None of it needs the Migrate
    /// phase's cell-disjointness, so moving it out of <c>ExecuteMigrations</c> (#872 step 7) costs no synchronisation.
    /// </para>
    /// <para>
    /// <b>Reaching this at all should be impossible.</b> The entity can only have left the map through a destroy, and a destroy inside the tick fence is an
    /// <c>EW-01</c> violation that <c>ExclusiveWindow</c> throws on. The slot is cleared regardless — leaving it set would keep a ghost visible to spatial
    /// queries until a later spawn reclaimed it — and the event is reported rather than absorbed.
    /// </para>
    /// </remarks>
    internal void RollbackOrphanedDestinationSlot(int dstChunkId, int dstSlot, long entityKey, ChangeSet changeSet)
    {
        if (ClusterSegment != null)
        {
            var accessor = ClusterSegment.CreateChunkAccessor(changeSet);
            try
            {
                ClearSlotBits(accessor.GetChunkAddress(dstChunkId, true), dstSlot);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        else if (TransientSegment != null)
        {
            var accessor = TransientSegment.CreateChunkAccessor();
            try
            {
                ClearSlotBits(accessor.GetChunkAddress(dstChunkId, true), dstSlot);
            }
            finally
            {
                accessor.Dispose();
            }
        }

        ReportOrphanedMigrant(entityKey, dstChunkId, dstSlot);
    }

    private void ClearSlotBits(byte* clusterBase, int dstSlot)
    {
        Interlocked.And(ref *(long*)clusterBase, ~(1L << dstSlot));
        *(long*)(clusterBase + Layout.EntityIdsOffset + dstSlot * 8) = 0;
        for (var s = 0; s < Layout.ComponentCount; s++)
        {
            Interlocked.And(ref *(long*)(clusterBase + Layout.EnabledBitsOffset(s)), ~(1L << dstSlot));
        }
    }

    /// <summary>
    /// Migrants found missing from the EntityMap at the moment their location patch was applied. Must stay zero.
    /// </summary>
    /// <remarks>
    /// <b>A counter, and NEITHER a <c>Console.WriteLine</c> NOR a <c>Debug.Fail</c>.</b> The whole argument for deferring the EntityMap write out of
    /// <c>ExecuteMigrations</c> (#872 step 7) is that this case cannot happen inside the fence — a destroy would be an <c>EW-01</c> violation that
    /// <c>ExclusiveWindow</c> throws on. That argument is only worth anything if a violation would be NOTICED, and the two obvious ways to be loud both fail
    /// at exactly that: a line printed to stdout from a worker thread in Release surfaces nowhere, and <c>Debug.Fail</c> terminates the process
    /// uncatchably — so in Debug, the configuration the whole suite runs in, it would abort the test host and lose every fixture with no attribution, while
    /// being compiled out of Release entirely. <c>TickContext.cs</c> records that same conclusion for the same reason. A counter is readable from a host and
    /// is the thing a test can actually assert on.
    /// </remarks>
    internal long OrphanedMigrantCount;

    /// <summary>EntityKey of the first orphaned migrant, for diagnosing one if it ever appears. Zero when there has been none.</summary>
    internal long FirstOrphanedMigrantKey;

    /// <summary>
    /// Packed <c>(chunkId &lt;&lt; 8) | slot</c> of the first orphaned migrant's destination, so the counter alone is not the whole diagnosis.
    /// </summary>
    internal long FirstOrphanedMigrantDst;

    private void ReportOrphanedMigrant(long entityKey, int dstChunkId, int dstSlot)
    {
        FirstOrphanedMigrantDst = ((long)dstChunkId << 8) | (uint)(dstSlot & 0xFF);
        Interlocked.CompareExchange(ref FirstOrphanedMigrantKey, entityKey, 0);
        Interlocked.Increment(ref OrphanedMigrantCount);
    }

    /// <summary>
    /// Record that an entity whose <c>BornTSN</c> is <paramref name="bornTsn"/> now occupies <paramref name="clusterChunkId"/>. Called by EVERY site that
    /// associates an entity with a cluster — spawn commit, WAL replay, chain rebuild, and spatial cluster migration — because the summary is only sound if it
    /// bounds every entity actually present.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This must run BEFORE the store that publishes the slot's occupancy bit, never after</b> (<c>BIND-04</c>). Every claim path therefore calls it itself
    /// rather than leaving it to the caller: published the other way round, a reader that sees the bit can still see a maximum that predates the entity, and
    /// <see cref="IsClusterFullyVisibleAt(int, long)"/> then vouches for a cluster holding an entity born after that reader's snapshot —
    /// <c>EcsQuery.TryCountViaOccupancy</c>, which popcounts the word on the strength of that vouch and has no per-entity probe to fall back on, over-counts.
    /// The reader's half is the mirror: acquire-read the occupancy word, then read the summary.
    /// </para>
    /// <para>
    /// <b>The CAS covers the element, NOT the array.</b> Two concurrent growers each allocate, copy and publish, and the later publish drops the earlier's
    /// contents — so a fold that lands between another thread's copy and its publish is lost regardless of how atomically it was made. That hazard predates
    /// this method's CAS and is not fixed by it. <see cref="EnsureClusterVisibilityCapacity"/> now serialises growers on <c>_finalizeLock</c>, which removes
    /// the grower-versus-grower half; what remains is a FOLD racing a grow, which is why this method re-reads the array reference after its CAS rather than
    /// trusting the one it started with. (This paragraph used to say the capacity method was unsynchronised. It was, until #722.)
    /// </para>
    /// <para>
    /// <b>The maximum is an upper bound, not the exact largest BornTSN present, and it is not monotone with the TSN clock.</b> Cluster migration folds the TSN
    /// high-water mark because the migrated entity's own <c>BornTSN</c> is not readable until after the claim, so a migrated-into cluster can carry a bound
    /// ABOVE anything committed — and a later spawn into it then moves nothing. Every consumer must therefore treat "unmoved" as "no information", never as
    /// "unchanged", and must read the occupancy word unconditionally rather than gating that read on the maximum having moved — which is what
    /// <c>EcsQuery.TryCountViaOccupancy</c> does. No consumer currently uses this value as a CHANGE TOKEN, and one that did would be unsound for exactly this
    /// reason. Overshooting is otherwise free of consequence: it only denies the whole-cluster shortcut to readers who would have been granted it, which costs
    /// a probe and never emits an entity.
    /// </para>
    /// </remarks>
    internal void NoteClusterBorn(int clusterChunkId, long bornTsn)
    {
        EnsureClusterVisibilityCapacity(clusterChunkId + 1);

        // CAS rather than a plain store, because two claimants can be folding into the SAME cluster at once — #708 records that Transient spawns commit
        // concurrently from independent transactions, and that is the very path this now runs on. A read-modify-write of a maximum loses updates under that:
        // both read 5, one writes 12, the other writes 10 last, and the cluster ends up vouching for a snapshot at 10 while holding an entity born at 12.
        // Silent, and produces a phantom rather than a slow query.
        //
        // The re-read of the array reference after the CAS is the other half, and serialising growers against each other does NOT cover it: a folder can pass
        // the capacity check, take a reference to the CURRENT array, and CAS into it while a grower — holding the lock, entirely correctly — has already
        // Array.Copy'd and is about to publish the replacement. The fold lands in an array nobody will read again. Confirming the reference is unchanged after
        // the CAS, and retrying when it is not, is what makes the fold durable across a grow.
        while (true)
        {
            var slots = Volatile.Read(ref ClusterMaxBornTsn);
            var current = Volatile.Read(ref slots![clusterChunkId]);
            if (current != VisibilityUnknown && bornTsn <= current)
            {
                // Even a no-op has to confirm it read the live array — a "already high enough" decision taken against a doomed copy is not a decision.
                if (ReferenceEquals(Volatile.Read(ref ClusterMaxBornTsn), slots))
                {
                    return;
                }

                continue;
            }

            if (Interlocked.CompareExchange(ref slots[clusterChunkId], bornTsn, current) == current
                && ReferenceEquals(Volatile.Read(ref ClusterMaxBornTsn), slots))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The one way to read the (count, array) pair of the active-cluster list. Count FIRST, then the array, both acquire, then clamp.
    /// </summary>
    /// <remarks>
    /// CLUSTERWALK-02. <see cref="AddToActiveList"/> stores the grown array plainly and publishes the count with a release, so acquiring the count guarantees
    /// seeing an array at least that long — and loading the array first needs no instruction reordering to fault, just a plain interleaving: read the
    /// length-16 array, let a concurrent spawn resize and bump the count to 17, read 17, index 16. The rule requires every call site to come through one
    /// reader rather than get the order right independently, because the sites that were right were right by accident.
    /// <para>
    /// It lives here rather than on <c>TyphonRuntime</c> (where it was <c>private static</c>) because it reads this type's fields and this type is not the
    /// runtime's alone — <c>EcsQuery.TryCountViaOccupancy</c> needs it too, and reproducing the four lines at that call site is what the rule forbids.
    /// </para>
    /// <para>
    /// This makes the pair CONSISTENT; it does not make the walk SAFE. A walker racing <see cref="RemoveFromActiveList"/> can still see one cluster twice and
    /// skip another, whose chunk is freed two lines later — CLUSTERWALK-01, which needs a snapshot or epoch protocol and is unfixed.
    /// </para>
    /// </remarks>
    internal int[] ReadActiveClusterList(out int count)
    {
        count = Volatile.Read(ref ActiveClusterCount);
        var ids = Volatile.Read(ref ActiveClusterIds);
        if (ids == null)
        {
            count = 0;
            return null;
        }

        if (count > ids.Length)
        {
            count = ids.Length;
        }

        return ids;
    }

    /// <summary>
    /// Return a cluster's visibility entries to the unestablished state. MUST be called by every site that frees a cluster chunk, before the chunk id can be
    /// handed back out.
    /// </summary>
    /// <remarks>
    /// Chunk ids are RECYCLED — <c>ChunkBasedSegment</c> keeps a free list — so without this a brand-new cluster inherits whatever the previous occupant of
    /// that id left behind. That defeats <see cref="FreshClusterStaysUnknown"/> entirely: the fresh-cluster claim skips the fold precisely so the gate denies
    /// until the slot has contents, and it can only deny if the entry actually holds the sentinel. Concretely — cluster 7 drains with born=100, died=120 and
    /// is freed; a spawn at TSN 500 gets id 7 back and release-stores occupancy bit 0; a reader at txTsn=300 loads the word, reads born=100 and died=120, both
    /// satisfied, and counts a slot whose EntityId tail and EntityMap record do not exist yet. It also stops a permanent-deny value (the migration tombstone
    /// guard) being inherited by an unrelated cluster and nailing it to the slow path forever.
    /// </remarks>
    internal void ResetClusterVisibility(int clusterChunkId)
    {
        var born = Volatile.Read(ref ClusterMaxBornTsn);
        if (born != null && (uint)clusterChunkId < (uint)born.Length)
        {
            Volatile.Write(ref born[clusterChunkId], VisibilityUnknown);
        }

        var died = Volatile.Read(ref ClusterMaxDiedTsn);
        if (died != null && (uint)clusterChunkId < (uint)died.Length)
        {
            Volatile.Write(ref died[clusterChunkId], 0);
        }
    }

    /// <summary>
    /// Record that an entity in <paramref name="clusterChunkId"/> died at <paramref name="diedTsn"/>, forcing the per-entity probe for that cluster until a
    /// reader's snapshot reaches that TSN. Called by EVERY site that tombstones an entity — destroy commit, WAL replay and spatial cluster migration —
    /// because the summary is only sound if it bounds every death actually recorded.
    /// </summary>
    /// <remarks>
    /// A site that tombstones WITHOUT clearing the occupancy bit must fold <see cref="VisibilityUnknown"/> rather than the death's TSN. The watermark's whole
    /// argument is that a reader past the last death is exact because occupancy already reflects it; where the bit survives, that is false and a satisfiable
    /// watermark grants the gate over a tombstone. Two sites are in that shape today — recovery replay and cluster migration — and both pass the sentinel.
    /// </remarks>
    internal void NoteClusterDied(int clusterChunkId, long diedTsn)
    {
        EnsureClusterVisibilityCapacity(clusterChunkId + 1);

        // CAS, and the same post-CAS array re-read as NoteClusterBorn — see there. A lost update on this side under-records the watermark, which admits
        // exactly the readers between the recorded value and the real death and makes them miss a tombstone they must still see. 0 is "no death", so it is
        // never a legal value to fold.
        while (true)
        {
            var slots = Volatile.Read(ref ClusterMaxDiedTsn);
            var current = Volatile.Read(ref slots![clusterChunkId]);
            if (diedTsn <= current)
            {
                if (ReferenceEquals(Volatile.Read(ref ClusterMaxDiedTsn), slots))
                {
                    return;
                }

                continue;
            }

            if (Interlocked.CompareExchange(ref slots[clusterChunkId], diedTsn, current) == current
                && ReferenceEquals(Volatile.Read(ref ClusterMaxDiedTsn), slots))
            {
                return;
            }
        }
    }

    /// <summary>
    /// True when every entity in <paramref name="clusterChunkId"/> is visible to a reader at <paramref name="txTsn"/>, so the scan may skip the per-entity
    /// EntityMap probe for the whole cluster. False whenever the answer is not certain — an unsized array, an unestablished maximum, or any recorded death.
    /// </summary>
    /// <remarks>
    /// There was a second overload handing the two watermark values back to the caller, added for a view-refresh prototype that would have kept them as a
    /// per-cluster CHANGE TOKEN. That prototype was never merged (#722), the overload had no callers, and the idea it existed for is unsound anyway — the
    /// born maximum is an upper bound that migration can move without a spawn and that a spawn can leave unmoved, so "unmoved" is not "unchanged". See the
    /// remark on <see cref="NoteClusterBorn"/>. Reinstating it needs that argument answered first, not just the two <c>out</c> parameters back.
    /// </remarks>
    internal bool IsClusterFullyVisibleAt(int clusterChunkId, long txTsn)
    {
        // Acquire loads, and in THIS order. The growth path publishes the resized ClusterMaxDiedTsn before release-storing ClusterMaxBornTsn, so a reader that
        // reads maxBorn first is guaranteed the died array it then reads is at least as new. Reading them the other way round could pair a new maxBorn with a
        // stale short died array — and a missing died entry reads as "no death", which is a phantom.
        var maxBorn = Volatile.Read(ref ClusterMaxBornTsn);
        if (maxBorn == null || (uint)clusterChunkId >= (uint)maxBorn.Length)
        {
            return false;
        }

        var born = Volatile.Read(ref maxBorn[clusterChunkId]);

        // A died array that is absent or too short is NOT evidence of "nobody died" — it is evidence that this reader cannot tell. Fall back to the probe.
        var died = Volatile.Read(ref ClusterMaxDiedTsn);
        if (died == null || (uint)clusterChunkId >= (uint)died.Length)
        {
            return false;
        }

        var diedWatermark = Volatile.Read(ref died[clusterChunkId]);

        if (born == VisibilityUnknown || born > txTsn)
        {
            return false;
        }

        // Unlike the born side there is no "unknown" sentinel: 0 means no death has been recorded, and 0 <= txTsn for every real snapshot, so a cluster
        // nothing has died in never blocks. A reader whose snapshot has reached the last death sees an occupancy word that already reflects it.
        return diedWatermark <= txTsn;
    }

    /// <summary>
    /// Grow both visibility arrays to hold at least <paramref name="requiredLength"/> clusters, seeding new entries at the pessimistic end
    /// (<see cref="VisibilityUnknown"/>, no deaths recorded). Mirrors <see cref="EnsureClusterSpatialIndexSlotCapacity"/>.
    /// </summary>
    internal void EnsureClusterVisibilityCapacity(int requiredLength)
    {
        // Double-checked growth. The fast path is a plain length compare and stays lock-free; only an actual grow serialises. Unsynchronised, two growers each
        // allocate, Array.Copy and publish, and the later publish silently drops every fold the earlier one copied — a lost BORN fold leaves the cluster
        // vouching for an entity born after it, which is a phantom. Reachable since the fold moved into TryClaimSlotInCluster, which parallel Migrate-phase
        // workers call. No caller holds _finalizeLock across this: ClaimSlotInCell's lock block covers AllocateNewCluster and the cell bookkeeping only, and
        // every fold site sits outside it.
        var current = Volatile.Read(ref ClusterMaxBornTsn);
        if (current != null && current.Length >= requiredLength)
        {
            return;
        }

        ref var growCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref growCtx);
        try
        {
            GrowClusterVisibilityCapacityLocked(requiredLength);
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>The growth body of <see cref="EnsureClusterVisibilityCapacity"/>. Caller must hold <c>_finalizeLock</c> exclusively.</summary>
    private void GrowClusterVisibilityCapacityLocked(int requiredLength)
    {
        if (ClusterMaxBornTsn == null)
        {
            var initial = Math.Max(16, requiredLength);
            var fresh = new long[initial];
            Array.Fill(fresh, VisibilityUnknown);
            // Zero-filled is correct and is NOT the pessimistic end for this array: 0 means "no death recorded", which is the truth for a cluster that has
            // never held one. The born array needs its sentinel because an unestablished maximum must not read as 0 (= visible to everyone).
            Volatile.Write(ref ClusterMaxDiedTsn, new long[initial]);
            // Publish the sized-and-filled array as one store: a reader that sees a non-null reference must see it fully initialized, or it would read a
            // default 0 as "clean" and skip the probe. See M3/M4/M5 in the pre-merge review for the same hazard in ZoneMapArray.
            Volatile.Write(ref ClusterMaxBornTsn, fresh);
            return;
        }
        if (ClusterMaxBornTsn.Length >= requiredLength)
        {
            return;
        }

        var newLen = Math.Max(ClusterMaxBornTsn.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }

        var oldLen = ClusterMaxBornTsn.Length;
        var grown = new long[newLen];
        Array.Copy(ClusterMaxBornTsn, grown, oldLen);
        Array.Fill(grown, VisibilityUnknown, oldLen, newLen - oldLen);
        var grownDied = new long[newLen];
        Array.Copy(ClusterMaxDiedTsn, grownDied, ClusterMaxDiedTsn.Length);
        Volatile.Write(ref ClusterMaxDiedTsn, grownDied);
        Volatile.Write(ref ClusterMaxBornTsn, grown);
    }

    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Write-time spatial bookkeeping. Populated by ClusterRef.WriteSpatial(...) at the write site. Consumed by the fence-time sparse-iteration pass — only
    // clusters with bits set here do any work at fence time. See claude/design/spatial/write-time-spatial.md.
    // ═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One bit per cluster — set whenever WriteSpatial detects pending work (migration or shrink-rescan). Drives the fence-time loop, replacing the
    /// unconditional scan of every active cluster. Indexed by clusterChunkId; word at <c>i / 64</c>, bit <c>i % 64</c>. Lazy-allocated alongside ClusterAabbs.
    /// </summary>
    internal long[] ClusterProcessBitmap;

    /// <summary>
    /// Per-cluster bitmap of slots needing migration this tick — one u64 per cluster, bit <c>i</c> set means slot <c>i</c>'s entity has crossed the
    /// cell+hysteresis boundary. Drained at fence by <see cref="DatabaseEngine.DetectClusterMigrations"/>. Indexed by clusterChunkId.
    /// </summary>
    internal ulong[] ClusterMigrationPendingSlots;

    /// <summary>
    /// Per-cluster destination cell key for the migration batch in <see cref="ClusterMigrationPendingSlots"/>. <c>-1</c> when no migration is pending.
    /// By cluster-coherence invariant, all flagged slots in a single cluster migrate to the same destination cell key (the first writer wins; conflicting
    /// writes are resolved at fence time by re-reading the slot's position). Indexed by clusterChunkId.
    /// </summary>
    internal int[] ClusterMigrationDestCellKeys;

    /// <summary>
    /// Per-cluster shrink-pending axes mask. Bit layout: 0x01=MinX, 0x02=MaxX, 0x04=MinY, 0x08=MaxY, 0x10=MinZ, 0x20=MaxZ. Set when an entity at an axis
    /// extreme moves inward — fence must rescan this cluster on the flagged axes only. Indexed by clusterChunkId.
    /// </summary>
    internal byte[] ClusterShrinkPendingAxes;

    /// <summary>
    /// Per-archetype per-cell spatial slot, indexed by cellKey. Null entries for cells where this archetype has no clusters. Lazy-allocated:
    /// the <see cref="PerCellSpatialSlot"/> is created on first cluster insertion into that cell. The DynamicIndex inside is also lazy (created on first
    /// <see cref="CellSpatialIndex.Add"/>). Null entirely for non-spatial archetypes or before grid opt-in.
    /// </summary>
    internal PerCellSpatialSlot[] PerCellIndex;

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Per-cell R-Tree promotion (#872 step 9)
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Cluster count at which a cell's linear index is replaced by a <see cref="CellClusterTree"/>. <see cref="int.MaxValue"/> disables promotion entirely,
    /// which is the default.
    /// </summary>
    /// <remarks>
    /// <para><b>Default off, on measured grounds.</b> The crossover was measured across cluster count and query selectivity: below ~512 clusters the linear
    /// scan wins a selective query — 6x at 80 clusters, which is what AntHill's densest zones hold — and the tree's update path is 22-38x dearer per moved
    /// cluster. Above ~1500 the tree wins by 3.6x, and by 25.8x at 15625. A default that promoted eagerly would make the common case slower to help a case
    /// no measured workload reaches.</para>
    /// <para><b>And the regime is harder to reach than it looks.</b> Clusters-per-cell is the world TIMES the cell size, so the same population crosses this
    /// threshold or not depending on a number the developer picks. On a clumped population the mean was 1.8 clusters per cell with the worst cell at 102 —
    /// nowhere near. Reaching 512 needed hotspots so tight that any query into one returns tens of thousands of entities, at which point the broadphase is
    /// 0.2% of the query and the tree improves the worst case by 0.03%. Promotion is therefore a hedge against a mis-sized grid, not a normal operating mode,
    /// and it is opt-in so that turning it on is a decision somebody made.</para>
    /// </remarks>
    internal int CellTreePromoteThreshold = int.MaxValue;

    /// <summary>
    /// Cluster count at which a promoted cell falls back to a linear index. Must be meaningfully below <see cref="CellTreePromoteThreshold"/>.
    /// </summary>
    /// <remarks>
    /// The gap between the two is not tuning slack, it is what stops thrashing: a cell hovering at the threshold would otherwise rebuild its whole structure
    /// twice per tick, and a rebuild is O(C) on a cell whose C is by definition large. Half the promote threshold gives a cell room to breathe across a
    /// spawn/destroy cycle without changing shape.
    /// </remarks>
    internal int CellTreeDemoteThreshold = int.MaxValue;


    /// <summary>
    /// Mean cluster extent, as a fraction of the cell edge, at or below which a cell half may promote. <c>1</c> = count only.
    /// See <see cref="SpatialOptions.CellTreePromoteTightness"/> for the measurement behind the default.
    /// </summary>
    internal float CellTreePromoteTightness = 1f;

    /// <summary>Mean cluster extent at which a promoted half falls back to the linear scan. Twice <see cref="CellTreePromoteTightness"/>.</summary>
    internal float CellTreeDemoteTightness = 1f;

    /// <summary>
    /// Cells whose linear half holds enough clusters to promote and whose clusters are still too loose for a tree to prune between them.
    /// </summary>
    /// <remarks>
    /// The count gate is evaluated when a cluster joins a cell, which is the only moment the count changes; the TIGHTNESS gate has no such moment — a
    /// repair re-packs a cell without adding a cluster to it, and the cell would then wait for an unrelated arrival to notice it now qualifies. This
    /// list is that missing moment: <c>MaybePromoteCellHalf</c> records the cell it turned down on tightness alone, and
    /// <see cref="EvaluateCellTreeTightnessTransitions"/> re-reads it once per fence, when the tick's bounds are final. It holds only cells at or above
    /// the count threshold, so it is empty in every database that never fills one, and the fence-time pass is one null check there.
    /// </remarks>
    private List<int> _tightnessBlockedCells;

    /// <summary>
    /// Cell keys whose half currently holds a tree. Kept so the fence's demote pass costs <c>O(promoted)</c> rather than a scan of every cell that exists.
    /// </summary>
    /// <remarks>
    /// Lazily compacted rather than maintained exactly: <see cref="DemoteCellHalf"/> has four call sites and only two know their cell key, so an entry
    /// whose tree has gone is dropped by the pass that next walks past it. A stale entry costs one null check; a missing one cannot happen, because the
    /// only producer of a tree is the promotion that appends here.
    /// </remarks>
    private List<int> _promotedCells;

    /// <summary>Segment shared by every cell tree of this archetype. Created on first promotion, never per cell — see <see cref="CellClusterTree"/>.</summary>
    internal ChunkBasedSegment<TransientStore> CellTreeSegment;

    /// <summary>Store kept alive for <see cref="CellTreeSegment"/>.</summary>
    internal TransientStore? CellTreeStore;

    /// <summary>
    /// Supplies the shared cell-tree segment on first promotion. Set by <c>DatabaseEngine</c> at construction; null leaves promotion unavailable, which is
    /// what every non-engine construction path (tests, harnesses) gets.
    /// </summary>
    /// <remarks>
    /// A factory rather than an eagerly-created segment because a <see cref="ChunkBasedSegment{TStore}"/> costs at least two pages, and the overwhelmingly
    /// common case is that no cell in the archetype ever crosses the threshold. A factory rather than service references on this type because
    /// <see cref="ArchetypeClusterState"/> is constructed by static factory methods that hold none.
    /// </remarks>
    internal Func<int, (ChunkBasedSegment<TransientStore> segment, TransientStore store)> CellTreeSegmentFactory;

    /// <summary>Cells currently served by a tree, counted for telemetry and for the tests that assert promotion did or did not happen.</summary>
    internal int PromotedCellCount;

    /// <summary>
    /// One deferred index write for a cluster in a promoted cell. Carries no bounds — <see cref="ClusterAabbs"/> already holds them by the time this is
    /// recorded, and duplicating a 28-byte struct into a buffer only to read the same value back would be two sources for one fact.
    /// </summary>
    internal readonly struct PromotedAabbApply
    {
        internal readonly int ClusterChunkId;
        internal readonly int CellKey;

        internal PromotedAabbApply(int clusterChunkId, int cellKey)
        {
            ClusterChunkId = clusterChunkId;
            CellKey = cellKey;
        }
    }

    /// <summary>
    /// Index writes deferred out of the parallel AabbRefresh phase because their cell is promoted. Drained by <see cref="DrainPromotedAabbApplies"/>.
    /// </summary>
    internal List<PromotedAabbApply> PendingPromotedApplies;

    /// <summary>Deferred applies drained by the last fence, and the microseconds the drain took — the escalation trigger's two inputs (#872 step 9).</summary>
    internal int PromotedApplyCount;

    /// <summary>See <see cref="PromotedApplyCount"/>.</summary>
    internal double PromotedApplyUs;

    /// <summary>Merge one worker's deferred applies into the archetype's pending list, under the same lock the outlier buffer uses.</summary>
    internal void EnqueuePromotedAppliesBulk(List<PromotedAabbApply> buffer)
    {
        if (buffer == null || buffer.Count == 0)
        {
            return;
        }

        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref nullCtx);
        try
        {
            PendingPromotedApplies ??= new List<PromotedAabbApply>(buffer.Count);
            PendingPromotedApplies.AddRange(buffer);
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>
    /// Apply every index write the parallel phase deferred, on one thread. Runs in the fence's per-archetype tail, before the loose-leaf refit.
    /// </summary>
    /// <remarks>
    /// <para><b>Order is by cluster id, not arrival order.</b> Arrival order depends on how the planner happened to slice and on which worker finished first,
    /// so applying in it would make the resulting tree — and every handle in <c>ClusterSpatialIndexSlot</c> — a function of the worker count. Sorting costs
    /// O(n log n) on a list that is empty in every unpromoted archetype, and buys a result identical across machines, which is the same constraint the rebuild
    /// reduce already carries.</para>
    /// <para><b>This is where §4.1's parallelism claim is spent.</b> The design says cells are independent and R-Tree maintenance partitions by cell; this
    /// satisfies the exclusivity half in full and defers the throughput half to one partition. <see cref="PromotedApplyUs"/> is the measurement that says when
    /// that stops being the right trade.</para>
    /// </remarks>
    internal void DrainPromotedAabbApplies()
    {
        PromotedApplyCount = 0;
        PromotedApplyUs = 0d;
        var pending = PendingPromotedApplies;
        if (pending == null || pending.Count == 0)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        pending.Sort(static (a, b) => a.ClusterChunkId.CompareTo(b.ClusterChunkId));
        for (var i = 0; i < pending.Count; i++)
        {
            var entry = pending[i];
            UpdateClusterInPerCellIndex(entry.ClusterChunkId, entry.CellKey, in ClusterAabbs[entry.ClusterChunkId]);
        }

        PromotedApplyCount = pending.Count;
        PromotedApplyUs = (Stopwatch.GetTimestamp() - started) * 1_000_000d / Stopwatch.Frequency;
        pending.Clear();
    }

    /// <summary>
    /// Snapshot of the previous tick's dirty bitmap (occupancy-masked). Set during <c>WriteClusterTickFence</c>, consumed
    /// by <c>TyphonRuntime.BuildFilteredClusterEntities</c> for change-filtered parallel dispatch.
    /// Word index = clusterChunkId, bit position = slotIndex. Null when no entities were dirty.
    /// </summary>
    public long[] PreviousTickDirtySnapshot;

    // ═══════════════════════════════════════════════════════════════════════
    // Per-archetype B+Tree indexes. Null if archetype has no indexed fields.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype B+Tree index slots for SingleVersion / Versioned components. Null if no such slot has indexed fields.</summary>
    public ClusterIndexSlot<PersistentStore>[] IndexSlots;

    /// <summary>
    /// Where the tick fence's Migrate phase stages <c>(key, oldValue, newValue)</c> for this archetype's indexed fields, and where the IndexMassUpdate phase
    /// reads them from (#872 step 6). Null until <c>InitializeIndexes</c> has run; empty for an archetype with no indexed field.
    /// </summary>
    internal IndexUpdateStaging IndexUpdates;

    /// <summary>
    /// Where the tick fence's Migrate phase stages each migrant's EntityMap location patch, and where the EntityMapUpdate phase reads them (#872 step 7).
    /// </summary>
    /// <remarks>
    /// Constructed unconditionally, unlike <see cref="IndexUpdates"/>, which is built inside <c>InitializeIndexes</c> and is therefore absent for an archetype
    /// with no indexed field. Every migrant needs its EntityMap entry repointed, indexed or not.
    /// </remarks>
    internal readonly EntityMapUpdateStaging EntityMapUpdates = new();

    /// <summary>
    /// Whether this tick's migrations stage their EntityMap patches for the bulk phase, or apply them inline as they always did.
    /// </summary>
    /// <remarks>
    /// Decided once per tick in the Migrate phase's <c>Prepare</c> from <c>PendingMigrationCount / EntityMap.LiveBucketCount</c> against
    /// <c>RuntimeOptions.EntityMapBulkMinEntriesPerBucket</c> — see that option for the measurement. A batch far smaller than the bucket count has no runs to
    /// amortise over, so the bulk path is pure overhead there.
    /// </remarks>
    internal bool UseBulkEntityMapUpdate;

    /// <summary>
    /// The same, for <see cref="StorageMode.Transient"/> component slots. Null when the archetype has no indexed Transient field.
    /// </summary>
    /// <remarks>
    /// A separate array rather than entries in <see cref="IndexSlots"/> because the two are different closed generic types: a slot's trees live in the store
    /// its component's data lives in. Every consumer walks whichever array it has an accessor for, and the generic drain / capture / query paths are
    /// instantiated once per store. Transient slots were excluded from cluster storage entirely until #655.
    /// </remarks>
    public ClusterIndexSlot<TransientStore>[] TransientIndexSlots;

    /// <summary>
    /// Per-slot SingleVersion <see cref="ComponentCollection{T}"/> descriptors — the buffers to release when a slot is freed on
    /// destroy. SV CC has no revision chain, so the cluster slot is the buffer's sole owner and must release it directly (unlike
    /// Versioned CC, whose buffers are owned by content chunks and released by the revision cleanup). Null when the archetype has
    /// no SingleVersion component carrying a ComponentCollection field — zero overhead on the destroy hot path.
    /// </summary>
    internal ClusterCollectionSlot[] CollectionSlots;

    /// <summary>Shadow guard bitmap. Guards first-write-per-tick shadow capture. Same index semantics as <see cref="ClusterDirtyBitmap"/>.</summary>
    public DirtyBitmap ClusterShadowBitmap;

    /// <summary>
    /// Approximate count of index mutations since the last statistics rebuild, and the threshold <see cref="StatisticsWorker"/> trips on (#665). The only such
    /// counter now — its per-ComponentTable counterpart went with that index home (#629), never having been incremented by any write path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non-atomic on purpose, exactly as the ComponentTable field is: it only gates a background refresh, so a lost increment under contention costs at most
    /// a delayed rebuild. Reset to zero by the worker after a successful rebuild.
    /// </para>
    /// <para>
    /// Counted only where the index actually changed. An update that leaves every indexed field alone does no tree work (the unchanged-field guards), so it
    /// must not push the statistics toward a rebuild either — and that makes "no work" directly observable in a test, which no other counter here is.
    /// </para>
    /// <para>
    /// <b>Padded, and the increment is hoisted out of the per-field loop</b> (review M4). Measured before padding, this field sat 52 bytes from
    /// <see cref="ActiveClusterCount"/> — and since objects are 8-byte aligned rather than 64, that put the two in one cache line for 2 of the 8 possible
    /// alignments. Not "always" and not "never": decided per instance by where the GC happened to put the object, which is the one answer you cannot reason
    /// about. Both cluster scans re-read <c>ActiveClusterCount</c> in their loop CONDITION, so an archetype that lost that dice roll had every commit
    /// invalidating the scan's line. The 64-byte wrapper guarantees separation from the fields placed AFTER it — one-sided, exactly like
    /// <c>PaddedFinalizeLock</c> above, and the same convention (rule MD-03).
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <c>internal</c> rather than private only so <c>ClusterIndexStatisticsTests</c> can take a <c>ref</c> to it and measure its distance from
    /// <see cref="ActiveClusterCount"/> — the padding is invisible to every other kind of test, and un-padding it would otherwise be a silent regression.
    /// Write through <see cref="MutationsSinceRebuild"/> — with one exception: the tick fence's sliced shadow drain is the only writer that runs W-wide on
    /// one archetype, and it folds through <c>Interlocked.Add</c> on this field (#886). The commit-path <c>+=</c> stays non-atomic, as the contract says.
    /// </remarks>
    internal CacheLinePaddedInt _mutationsSinceRebuild;

    /// <inheritdoc cref="_mutationsSinceRebuild"/>
    /// <remarks>
    /// Kept as an <see cref="int"/> property over the padded field so every call site — including <c>x.MutationsSinceRebuild++</c>, which compiles to
    /// get/add/set — reads exactly as it did before the padding, and stays as deliberately non-atomic as the field's own contract says.
    /// </remarks>
    internal int MutationsSinceRebuild
    {
        get => _mutationsSinceRebuild.Value;
        set => _mutationsSinceRebuild.Value = value;
    }

    /// <summary>Shared <see cref="ChunkBasedSegment{TStore}"/> backing all per-archetype B+Trees for this archetype.</summary>
    public ChunkBasedSegment<PersistentStore> IndexSegment;

    /// <summary>
    /// Second per-archetype index segment, striped for <see cref="String64"/> B+Tree nodes. Null when the archetype indexes no
    /// <c>String64</c> field.
    /// </summary>
    /// <remarks>
    /// A segment serves exactly one node size — every B+Tree variant asserts <c>segment.Stride == sizeof(its node)</c>. The
    /// <c>Index16/32/64Chunk</c> layouts are all 256 bytes (they differ only in key width, hence capacity 38/29/19), so one segment
    /// covers every numeric key type. <c>IndexString64Chunk</c> is larger, so it needs its own — exactly the split
    /// <c>ComponentTable</c> has always had between <c>DefaultIndexSegment</c> and <c>String64IndexSegment</c>. The cluster path
    /// originally allocated only the 256-byte segment and handed it to every field type, so indexing a <c>String64</c> field on a
    /// cluster-backed archetype tripped the stride assert in Debug and would have written past the chunk in Release (issue #658).
    /// </remarks>
    public ChunkBasedSegment<PersistentStore> IndexSegmentString64;

    /// <summary>
    /// Index segment backing the archetype's <see cref="StorageMode.Transient"/> B+Trees. Null when no Transient field is indexed.
    /// </summary>
    /// <remarks>
    /// Allocated from <see cref="TransientClusterStore"/>, so it is heap-backed, never checkpointed and **never given an SPI** — a Transient tree recorded in
    /// a persisted segment would be reloaded on the next open pointing at data that no longer exists. Transient data does not survive the process, so its
    /// index must not either: the correct state after a reopen is an empty tree, not a restored one (#655).
    /// </remarks>
    public ChunkBasedSegment<TransientStore> TransientIndexSegment;

    /// <summary>
    /// The <see cref="String64"/>-stride counterpart of <see cref="TransientIndexSegment"/>; null unless a Transient String64 field is indexed.
    /// </summary>
    public ChunkBasedSegment<TransientStore> TransientIndexSegmentString64;

    /// <summary>Backing store for <see cref="TransientIndexSegment"/>, held so it stays alive for the segment's lifetime. One store per segment, as
    /// <c>ComponentTable.CreateTransientSegments</c> does.</summary>
    internal TransientStore? TransientIndexStore;

    /// <summary>Backing store for <see cref="TransientIndexSegmentString64"/>.</summary>
    internal TransientStore? TransientIndexStoreString64;

    // ═══════════════════════════════════════════════════════════════════════
    // Per-archetype Spatial R-Tree. Null if archetype has no spatial fields.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-archetype spatial R-Tree state. Check <c>SpatialSlot.HasSpatialIndex</c> for presence.</summary>
    public ClusterSpatialSlot SpatialSlot;

    /// <summary>
    /// Per-archetype <see cref="DirtyBitmapRing"/> consumed by <c>SpatialInterestSystem</c> for delta queries and the 64-tick staleness fallback
    /// (issue #230 Phase 3). Populated at <see cref="InitializeSpatial"/>; archived at the tick fence. Relocated from <c>ClusterSpatialSlot.DirtyRing</c>
    /// to decouple the ring from the legacy per-entity tree that's being removed in Phase 3 — the ring's lifecycle belongs to the archetype's cluster state,
    /// not to any particular spatial index implementation.
    /// </summary>
    public DirtyBitmapRing ClusterDirtyRing;

    /// <summary>
    /// Per-archetype per-cell cluster claim list (issue #229 Q10 resolution). Holds the cluster chunk IDs of THIS archetype's clusters attached to each
    /// grid cell. Before Q10 this pool was owned by <see cref="SpatialGrid"/> and shared across archetypes, which meant two spatial archetypes couldn't
    /// coexist on the same grid (their cluster chunk IDs would collide at the cell level). Under Q10 each archetype owns its own pool — queries and
    /// spawn-time "find a free slot in this cell" scans only see clusters of the current archetype. <c>null</c> when the archetype has no spatial field
    /// or when no grid is configured. Allocated during <see cref="InitializeSpatial"/> when the grid is known.
    /// </summary>
    internal CellClusterPool CellClusterPool;

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #231: Tier dispatch state. The version counter is bumped whenever
    // a cluster is added to or removed from the active list — the per-archetype
    // TierClusterIndex reads it to skip rebuilds when the cluster set is stable.
    // The index itself is allocated lazily on the first tier-filtered dispatch.
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Monotonic counter, incremented by <see cref="AddToActiveList"/> and <see cref="RemoveFromActiveList"/>.
    /// Consumed by <see cref="TierClusterIndex.RebuildIfStale"/> to short-circuit when no cluster has been added or removed since the last rebuild. Issue #231.
    /// </summary>
    public int ClusterSetVersion { get; private set; }

    /// <summary>Lazily-allocated per-archetype tier index (issue #231). Built on demand by <c>TyphonRuntime.OnParallelQueryPrepare</c> the first time a
    /// tier-filtered system runs against this archetype. Subsequent rebuilds are version-guarded and usually no-ops.</summary>
    internal TierClusterIndex TierIndex;

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #233: Cluster dormancy state. Per-cluster sleep tracking for
    // skipping idle clusters during dispatch. Null arrays = dormancy not
    // enabled (non-spatial archetypes or threshold not set).
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Per-cluster sleep state, indexed by cluster chunk ID. Null for non-spatial archetypes (zero overhead).
    /// Allocated eagerly for spatial archetypes in <see cref="InitializeSpatial"/>. Issue #233.</summary>
    internal ClusterSleepState[] SleepStates;

    /// <summary>Per-cluster ticks-since-last-dirty counter. ushort gives ~18 minutes at 60Hz before wrap, which far exceeds
    /// any reasonable <see cref="SleepThresholdTicks"/>. Same sizing/lifecycle as <see cref="SleepStates"/>. Issue #233.</summary>
    internal ushort[] SleepCounters;

    /// <summary>Number of consecutive clean ticks before a cluster transitions to <see cref="ClusterSleepState.Sleeping"/>.
    /// 0 = dormancy disabled (counters still increment but no transition). Default 0. Set by game code. Issue #233.
    /// Clamped to [0, 65535] because <see cref="SleepCounters"/> uses <c>ushort</c> storage (~18 minutes at 60Hz).</summary>
    private int _sleepThresholdTicks;
    public int SleepThresholdTicks
    {
        get => _sleepThresholdTicks;
        set => _sleepThresholdTicks = Math.Clamp(value, 0, ushort.MaxValue);
    }

    /// <summary>When &gt; 0, sleeping clusters periodically wake on a staggered schedule: cluster wakes when
    /// <c>(tickNumber % HeartbeatIntervalTicks) == (chunkId % HeartbeatIntervalTicks)</c>. 0 = no heartbeat. Issue #233.</summary>
    public int HeartbeatIntervalTicks;

    /// <summary>Count of clusters currently in <see cref="ClusterSleepState.Sleeping"/> state. When 0, all dormancy filtering
    /// in <c>OnParallelQueryPrepare</c> is skipped (zero overhead). Issue #233.</summary>
    public int SleepingClusterCount;

    /// <summary>Archetype ID for this cluster state. Set during <see cref="InitializeSpatial"/>. Used by
    /// <see cref="MarkEntityDirty"/> to tag wake requests via <see cref="DormancyReporter"/>. Issue #233.</summary>
    internal int ArchetypeId;

    /// <summary>Back-reference to the engine's <see cref="SpatialGrid"/>. Set during <see cref="InitializeSpatial"/>. Used by <c>ClusterRef.WriteSpatial</c> to
    /// evaluate cell-boundary crossings at the write site without plumbing the grid through every call layer. <c>null</c> for non-spatial archetypes.</summary>
    internal SpatialGrid Grid;

    /// <summary>
    /// When <c>true</c>, the engine treats <c>ClusterRef.WriteSpatial</c> as the canonical (and only) writer of this archetype's spatial component. Enables two
    /// fence-time optimizations:
    /// <list type="bullet">
    ///   <item><c>DatabaseEngine.DetectClusterMigrations</c> skips its legacy dirtyBits scan
    ///         (step (b)) — all migrations are expected to come from <see cref="ClusterMigrationPendingSlots"/>.</item>
    ///   <item><see cref="RecomputeDirtyClusterAabbs"/> iterates <see cref="ClusterProcessBitmap"/>
    ///         (sparse) instead of <see cref="ActiveClusterIds"/> (full).</item>
    /// </list>
    /// Setting this on an archetype whose spatial field is mutated via raw <c>GetSpan</c> / <c>OpenMut + Write</c> will cause those mutations to be invisible
    /// to the engine's spatial maintenance — only set when you've migrated ALL spatial writers to <c>WriteSpatial</c>.
    /// Default <c>false</c>: legacy behaviour (full scan), safe for any caller.
    /// </summary>
    internal bool SpatialBarrierOnly;

    /// <summary>Tick number of the last <see cref="TransitionWakePendingToActive"/> call. Guards against redundant scans
    /// when multiple systems reference the same archetype. Issue #233.</summary>
    private long _lastWakeTransitionTick = -1;

    private ArchetypeClusterState() { }

    /// <summary>
    /// A state carrying nothing but the active-cluster list — for testing <see cref="AddToActiveList"/> / <see cref="RemoveFromActiveList"/>'s publication
    /// order (#582 face 2) without standing up segments and an engine. Those two methods touch only plain fields, so the list behaves identically here.
    /// </summary>
    internal static ArchetypeClusterState CreateActiveListOnlyForTests() =>
        new() { ActiveClusterIds = new int[16], ActiveClusterCount = 0, FreeClusterHead = -1 };

    /// <summary>Chunk capacity of the primary (non-null) segment.</summary>
    internal int PrimarySegmentCapacity => ClusterSegment?.ChunkCapacity ?? TransientSegment.ChunkCapacity;

    /// <summary>
    /// Mark an entity slot dirty for tick-fence processing, recording WHICH component slot was written so the fence can emit only
    /// the columns that actually changed (#559 §4.5).
    /// </summary>
    /// <param name="clusterChunkId">Cluster chunk holding the entity.</param>
    /// <param name="slotIndex">The entity's slot within the cluster.</param>
    /// <param name="componentSlot">Per-archetype component slot that was written.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDirty(int clusterChunkId, int slotIndex, int componentSlot)
    {
        // Test-then-OR: only the first write of this component in the tick pays the atomic; every later write is a load and a
        // branch against a line no core is writing any more.
        var bit = 1 << componentSlot;
        if ((WrittenSlotUnion & bit) == 0)
        {
            Interlocked.Or(ref WrittenSlotUnion, bit);
        }

        // NOTE: must NOT delegate to the component-less overload — that one poisons the mask to AllSlotsWritten, which would
        // undo the narrowing this overload exists to perform.
        MarkEntityDirty(clusterChunkId, slotIndex);
    }

    /// <summary>
    /// Mark an entity slot as dirty for tick fence processing, without identifying the component written.
    /// </summary>
    /// <remarks>
    /// This is the FAIL-SAFE overload: it marks the cluster's written-slot mask as <see cref="AllSlotsWritten"/>, so the fence
    /// emits every durable column for that cluster — exactly the behaviour before #559 §4.5. Callers that know which component
    /// they wrote should use <see cref="SetDirty(int, int, int)"/> instead to narrow the emission. A caller that is added later
    /// and forgets therefore over-emits (redundant bytes) rather than under-emits (lost data).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDirty(int clusterChunkId, int slotIndex)
    {
        if (WrittenSlotUnion != AllSlotsWritten)
        {
            Interlocked.Exchange(ref WrittenSlotUnion, AllSlotsWritten);
        }

        MarkEntityDirty(clusterChunkId, slotIndex);
    }

    /// <summary>Dirty-bit + dormancy-wake half of <see cref="SetDirty(int, int)"/>, shared by both overloads.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void MarkEntityDirty(int clusterChunkId, int slotIndex)
    {
        var entityIndex = clusterChunkId * 64 + slotIndex;
        ClusterDirtyBitmap.Set(entityIndex);

        // Issue #233: if this cluster is sleeping, request a deferred wake. The null check on SleepStates is the zero-cost bypass for non-spatial archetypes.
        // The byte read + compare is branch-predicted not-taken for Active clusters (common case). Race: parallel workers may see stale state — false negative
        // means one extra tick of sleep (dirty bit still records the writes); false positive is a harmless duplicate request.
        if (SleepStates != null && clusterChunkId < SleepStates.Length && SleepStates[clusterChunkId] == ClusterSleepState.Sleeping)
        {
            DormancyReporter.RequestWake(ArchetypeId, clusterChunkId);
        }
    }

    /// <summary>
    /// Engine-internal non-generic entry point for f32 AABB queries against the per-cell cluster spatial index (issue #230 Phase 3). Mirrors the game-facing
    /// generic entry point <see cref="ClusterSpatialQuery{TArch}.AABB{TBox}"/> but without the <c>TArch</c> compile-time type — consumers that iterate cluster
    /// archetypes at runtime (<c>SpatialTriggerSystem</c>, <c>SpatialInterestSystem</c>, <c>EcsQuery</c>) use this overload directly. Both entry points return
    /// the same <see cref="AabbClusterEnumerator"/> and therefore share a single state machine. Handles both 2D and 3D cluster archetype storage tiers — 2D
    /// callers pass <see cref="float.NegativeInfinity"/> / <see cref="float.PositiveInfinity"/> for the Z bounds to trivially satisfy the Z overlap test
    /// against 2D cluster storage.
    /// </summary>
    /// <param name="grid">The engine's spatial grid. Passed explicitly rather than stored on the state because the grid is a <see cref="DatabaseEngine"/>-owned
    /// singleton and the state has no other reason to hold a reference to it.</param>
    /// <param name="minX">Query bounds min-X.</param>
    /// <param name="minY">Query bounds min-Y.</param>
    /// <param name="minZ">Query bounds min-Z. For 2D queries against a 2D cluster archetype, pass <see cref="double.NegativeInfinity"/>.</param>
    /// <param name="maxX">Query bounds max-X.</param>
    /// <param name="maxY">Query bounds max-Y.</param>
    /// <param name="maxZ">Query bounds max-Z. For 2D queries against a 2D cluster archetype, pass <see cref="double.PositiveInfinity"/>.</param>
    /// <param name="categoryMask">Category bitmask; a cluster is skipped if its union mask does not intersect. Pass <see cref="uint.MaxValue"/> to accept all.</param>
    /// <remarks>
    /// This method does not validate <see cref="ClusterSpatialSlot.HasSpatialIndex"/> — the enumerator returns an empty result set naturally when the per-cell
    /// index is null or empty. Callers that want to skip the work entirely (to avoid constructing a dead enumerator) should check <c>HasSpatialIndex</c>
    /// themselves first. This matches the ergonomics the existing cluster-archetype iteration loops in <c>SpatialTriggerSystem</c> and <c>SpatialInterestSystem</c>
    /// expect.
    /// </remarks>
    public AabbClusterEnumerator QueryAabb(SpatialGrid grid, double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
        uint categoryMask = uint.MaxValue) => new(this, grid, minX, minY, minZ, maxX, maxY, maxZ, categoryMask);

    /// <summary>
    /// Radius (sphere) query against the per-cell cluster spatial index (issue #230 Phase 3). Returns an enumerator over every entity whose tight AABB is
    /// within <paramref name="radius"/> of the query center, using the closest-point-on-AABB semantic that matches the legacy
    /// <see cref="SpatialRTree{T}.QueryRadius"/>. The enumerator drives the broadphase with the sphere's enclosing AABB and applies the sphere distance
    /// check at narrowphase. <see cref="ClusterSpatialQueryResult.DistanceSq"/> is populated on each hit.
    /// </summary>
    /// <param name="grid">The engine's spatial grid.</param>
    /// <param name="centerX">Sphere center X.</param>
    /// <param name="centerY">Sphere center Y.</param>
    /// <param name="centerZ">Sphere center Z. For 2D archetypes, this parameter is ignored — the Z bounds of the query AABB are set to infinity so the
    /// Z overlap test trivially passes against 2D entities.</param>
    /// <param name="radius">Sphere radius in world units.</param>
    /// <param name="categoryMask">Category bitmask; <c>0</c> means "no filter".</param>
    public AabbClusterEnumerator QueryRadius(SpatialGrid grid, double centerX, double centerY, double centerZ, double radius,
        uint categoryMask = uint.MaxValue)
    {
        var minX = centerX - radius;
        var minY = centerY - radius;
        var maxX = centerX + radius;
        var maxY = centerY + radius;
        var is3D = SpatialSlot.FieldInfo.FieldType.Is3D();
        var minZ = is3D ? centerZ - radius : double.NegativeInfinity;
        var maxZ = is3D ? centerZ + radius : double.PositiveInfinity;
        var effectiveCenterZ = is3D ? centerZ : 0d;
        return new AabbClusterEnumerator(this, grid, minX, minY, minZ, maxX, maxY, maxZ, categoryMask, radius * radius, centerX, centerY, effectiveCenterZ);
    }


    /// <summary>
    /// Create a new ArchetypeClusterState for a cluster-eligible archetype (fresh database).
    /// </summary>
    /// <param name="layout">Precomputed cluster layout (shared by both segments).</param>
    /// <param name="segment">PersistentStore backing segment for SV+V components. Null for pure-Transient archetypes.</param>
    /// <param name="transientSegment">TransientStore backing segment for Transient components. Default (null) if no Transient.</param>
    /// <param name="transientStore">TransientStore instance to keep alive. Null if no Transient.</param>
    public static ArchetypeClusterState Create(ArchetypeClusterInfo layout, ChunkBasedSegment<PersistentStore> segment,
        ChunkBasedSegment<TransientStore> transientSegment = null, TransientStore? transientStore = null)
    {
        Debug.Assert(segment != null || transientSegment != null, "At least one cluster segment must be provided");
        var capacity = segment?.ChunkCapacity ?? transientSegment.ChunkCapacity;
        return new ArchetypeClusterState
        {
            ClusterSegment = segment,
            TransientSegment = transientSegment,
            TransientClusterStore = transientStore,
            Layout = layout,
            ActiveClusterIds = new int[16],
            ActiveClusterCount = 0,
            FreeClusterHead = -1,
            // Index = clusterChunkId * 64 + slotIndex. The 64 multiplier is fixed (not cluster size N)
            // because it aligns each cluster to exactly one bitmap word for O(1) per-cluster dirty scan.
            ClusterDirtyBitmap = new DirtyBitmap(Math.Max(64, capacity * 64)),
        };
    }

    /// <summary>
    /// Create an ArchetypeClusterState from an existing persisted segment (database reopen).
    /// Scans cluster occupancy bitmaps to rebuild <see cref="ActiveClusterIds"/> and <see cref="FreeClusterHead"/>.
    /// </summary>
    public static ArchetypeClusterState CreateFromExisting(ArchetypeClusterInfo layout, ChunkBasedSegment<PersistentStore> segment,
        ChunkBasedSegment<TransientStore> transientSegment = null, TransientStore? transientStore = null)
    {
        Debug.Assert(segment != null || transientSegment != null, "At least one cluster segment must be provided");
        var capacity = segment?.ChunkCapacity ?? transientSegment.ChunkCapacity;
        var state = new ArchetypeClusterState
        {
            ClusterSegment = segment,
            TransientSegment = transientSegment,
            TransientClusterStore = transientStore,
            Layout = layout,
            ActiveClusterIds = new int[16],
            ActiveClusterCount = 0,
            FreeClusterHead = -1,
            // Index = clusterChunkId * 64 + slotIndex. The 64 multiplier is fixed (not cluster size N)
            // because it aligns each cluster to exactly one bitmap word for O(1) per-cluster dirty scan.
            ClusterDirtyBitmap = new DirtyBitmap(Math.Max(64, capacity * 64)),
        };

        state.RebuildActiveList();
        return state;
    }

    /// <summary>
    /// Scan all allocated chunks in the segment, read OccupancyBits, and rebuild <see cref="ActiveClusterIds"/>,
    /// <see cref="ActiveClusterCount"/>, and <see cref="FreeClusterHead"/> from persisted data.
    /// </summary>
    private void RebuildActiveList()
    {
        ActiveClusterCount = 0;
        FreeClusterHead = -1;

        // Scan primary segment (PersistentStore for mixed/SV, TransientStore for pure-Transient)
        if (ClusterSegment != null)
        {
            var accessor = ClusterSegment.CreateChunkAccessor();
            try
            {
                ScanActiveChunks(ref accessor, ClusterSegment.ChunkCapacity);
            }
            finally
            {
                accessor.Dispose();
            }
        }
        else if (TransientSegment != null)
        {
            var accessor = TransientSegment.CreateChunkAccessor();
            try
            {
                ScanActiveChunksTransient(ref accessor, TransientSegment.ChunkCapacity);
            }
            finally
            {
                accessor.Dispose();
            }
        }
    }

    private void ScanActiveChunks(ref ChunkAccessor<PersistentStore> accessor, int capacity)
    {
        for (var chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!ClusterSegment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            var clusterBase = accessor.GetChunkAddress(chunkId);
            var occupancy = *(ulong*)clusterBase;

            if (occupancy == 0)
            {
                continue;
            }

            AddToActiveList(chunkId);

            if (FreeClusterHead < 0 && (~occupancy & Layout.FullMask) != 0)
            {
                FreeClusterHead = chunkId;
            }
        }
    }

    private void ScanActiveChunksTransient(ref ChunkAccessor<TransientStore> accessor, int capacity)
    {
        for (var chunkId = 1; chunkId < capacity; chunkId++)
        {
            if (!TransientSegment.IsChunkAllocated(chunkId))
            {
                continue;
            }

            var clusterBase = accessor.GetChunkAddress(chunkId);
            var occupancy = *(ulong*)clusterBase;

            if (occupancy == 0)
            {
                continue;
            }

            AddToActiveList(chunkId);

            if (FreeClusterHead < 0 && (~occupancy & Layout.FullMask) != 0)
            {
                FreeClusterHead = chunkId;
            }
        }
    }

    /// <summary>
    /// Claims one free bit in <paramref name="occupancy"/> by CAS, retrying until it wins or the word is full. Returns the slot index, or <c>-1</c> when no
    /// bit is free. The returned bit is exclusively this caller's.
    /// </summary>
    /// <remarks>
    /// #708. Both <c>ClaimSlot</c> overloads used to CAS ONCE and, on failure, re-read and commit the second attempt with a PLAIN store, commented
    /// "single-writer (no concurrent commit)". That premise does not hold — Transient spawns commit concurrently from independent transactions — and two
    /// losers of the first CAS then read the same occupancy word, pick the same trailing-zero slot, and both store it. Two entities end up in one slot with
    /// distinct EntityIds, so the second silently overwrites the first's component data and a thread reads back another thread's value.
    /// A retry that abandons the atomicity of the attempt it is retrying is not a retry; the loop below keeps every attempt a CAS.
    /// </remarks>
    private static int ClaimFreeBit(ref ulong occupancy, ulong fullMask)
    {
        while (true)
        {
            var current = Volatile.Read(ref occupancy);
            var available = ~current & fullMask;
            if (available == 0)
            {
                return -1;
            }

            var slot = BitOperations.TrailingZeroCount(available);
            var desired = current | (1UL << slot);
            if (Interlocked.CompareExchange(ref occupancy, desired, current) == current)
            {
                return slot;
            }
        }
    }

    /// <summary>
    /// Claim a free slot in an existing cluster, or allocate a new cluster.
    /// Returns the cluster chunk ID and the slot index within the cluster.
    /// </summary>
    /// <remarks>
    /// <para><b>Multi-writer.</b> The sentence that stood here until #842 — "FinalizeSpawns is single-writer (no concurrent commit), so CAS always succeeds
    /// on first try" — stopped being true at #708, which put concurrent Transient commits on this path, and the code kept being written as though it held:
    /// the free-cluster head was read twice around its own test, and cluster allocation ran outside the finalize latch that <c>ClaimSlotInCell</c> takes
    /// around the same work. Both are fixed below. What the CAS in <see cref="ClaimFreeBit"/> does cover is two claimants landing on the same cluster: each
    /// takes a distinct slot or reads the cluster full and falls through to allocate, so the remaining races on the head itself cost at most one extra
    /// allocation.</para>
    /// <para>The OccupancyBit is set immediately by this method. The caller MUST write component data and EntityKey before the next iteration boundary to
    /// maintain the invariant that occupied slots contain valid data.</para>
    /// <para><paramref name="bornTsn"/> is folded into the cluster's H1 visibility summary BEFORE the bit is published — see
    /// <see cref="NoteClusterBorn"/> and the class-level ordering note.</para>
    /// </remarks>
    public (int clusterChunkId, int slotIndex) ClaimSlot(ref ChunkAccessor<PersistentStore> accessor, ChangeSet changeSet, long bornTsn)
    {
        // Try existing cluster with free slots (O(1) when FreeClusterHead is valid).
        // ONE read, deliberately. Testing the field and then re-reading it is what #842 crashed on: a peer that fills this cluster stores -1 between the two,
        // the loser passes the test and takes -1 as its cluster id, and NoteClusterBorn indexes an array at -1. The exception is the mild outcome — the
        // address for chunk -1 has already been computed by then, and only the throw stops a CAS into the word one chunk below chunk 0.
        var clusterId = Volatile.Read(ref FreeClusterHead);
        if (clusterId >= 0)
        {
            var clusterBase = accessor.GetChunkAddress(clusterId, true);
            ref var occupancy = ref *(ulong*)clusterBase;

            // Fold BEFORE the claim: the CAS inside ClaimFreeBit is what publishes the bit, and its full fence keeps this store on the correct side of it.
            // Folding for a claim that then fails (cluster full) only raises the maximum, which is conservative — never a relaxation.
            NoteClusterBorn(clusterId, bornTsn);

            var slot = ClaimFreeBit(ref occupancy, Layout.FullMask);
            if (slot >= 0)
            {
                // If the cluster is now full, reset the head — the next call allocates a new one (O(1)).
                if ((Volatile.Read(ref occupancy) & Layout.FullMask) == Layout.FullMask)
                {
                    FreeClusterHead = -1;
                }

                return (clusterId, slot);
            }

            // Current free cluster is actually full — reset and fall through to allocate
            FreeClusterHead = -1;
        }

        // No free clusters — allocate a new one, under the finalize latch for the same three reasons ClaimSlotInCell states at its own slow path: the
        // dual-segment AllocateChunk must return matching ids, and AddToActiveList appends at ActiveClusterIds[ActiveClusterCount] and then publishes the
        // incremented count. That append is single-writer code, and #708 put concurrent Transient commits on this path: two allocators write the same index,
        // one increment is lost, and a live cluster is silently absent from the active list — the fence never visits it. The IndexOutOfRange when the two
        // race Array.Resize is the loud version of the same thing, and the only one anybody noticed (#842). The hot path above does NOT take this lock.
        var newClusterId = AllocateNewClusterLatched(changeSet);
        var newBase = accessor.GetChunkAddress(newClusterId, true);

        // Claim slot 0 in the fresh cluster. NO fold here — see FreshClusterStaysUnknown. Release store, paired with the reader's acquire read of the word.
        Volatile.Write(ref *(ulong*)newBase, 1UL); // OccupancyBit 0 set
        FreeClusterHead = Layout.ClusterSize > 1 ? newClusterId : -1;

        return (newClusterId, 0);
    }

    /// <summary>
    /// Claim a free slot for pure-Transient archetypes (no PersistentStore segment).
    /// Same logic as the PersistentStore overload but using TransientStore accessor.
    /// </summary>
    public (int clusterChunkId, int slotIndex) ClaimSlot(ref ChunkAccessor<TransientStore> accessor, long bornTsn)
    {
        // One read — see the PersistentStore overload and #842.
        var clusterId = Volatile.Read(ref FreeClusterHead);
        if (clusterId >= 0)
        {
            var clusterBase = accessor.GetChunkAddress(clusterId, true);
            ref var occupancy = ref *(ulong*)clusterBase;

            NoteClusterBorn(clusterId, bornTsn);   // before the publishing CAS — see the PersistentStore overload

            var slot = ClaimFreeBit(ref occupancy, Layout.FullMask);
            if (slot >= 0)
            {
                if ((Volatile.Read(ref occupancy) & Layout.FullMask) == Layout.FullMask)
                {
                    FreeClusterHead = -1;
                }
                return (clusterId, slot);
            }

            FreeClusterHead = -1;
        }

        var newClusterId = AllocateNewClusterLatched(null);   // see the PersistentStore overload and #842
        var newBase = accessor.GetChunkAddress(newClusterId, true);
        Volatile.Write(ref *(ulong*)newBase, 1UL);   // no fold — see FreshClusterStaysUnknown
        FreeClusterHead = Layout.ClusterSize > 1 ? newClusterId : -1;

        return (newClusterId, 0);
    }

    // ══════════════════════════════════════════════════════════════════════════════
    // Phase 1+2 of issue #229 — spatially coherent slot claiming. Only used when the
    // engine has a configured SpatialGrid AND this archetype has a spatial field.
    // All entities in a given cluster will share the same grid cell.
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempt to claim one free slot in <paramref name="clusterChunkId"/>. Returns the slot index, or <c>-1</c> if the cluster is full. Shared by both
    /// scan phases of both <c>ClaimSlotInCell</c> overloads — collapses what would otherwise be four copies of the dirty-aware claim block.
    /// </summary>
    /// <remarks>
    /// The cluster is first read with <c>dirty:false</c> — the scan must not dirty full clusters it skips (that would inflate ActiveChunkWriters /
    /// ChangeSet / writeback pressure for nothing). Only once a free slot is confirmed is the cluster re-fetched with <c>dirty:true</c>: this raises
    /// ActiveChunkWriters BEFORE the occupancy mutation (ACW-before-write invariant) and is an MRU cache hit. The CAS commit plus its retry loop keeps
    /// concurrent claimants on the same cluster safe (single-writer today; parallel-fence migration paths can hit the same dst cluster from cell-partitioned
    /// workers). The <c>dirty:true</c> re-fetch does not move the chunk, so the <c>occupancy</c> ref taken from the first fetch stays valid.
    /// </remarks>
    private int TryClaimSlotInCluster<TStore>(ref ChunkAccessor<TStore> accessor, int clusterChunkId, long bornTsn)
        where TStore : struct, IPageStore
    {
        var clusterBase = accessor.GetChunkAddress(clusterChunkId);
        ref var occupancy = ref *(ulong*)clusterBase;

        var current = occupancy;
        var available = ~current & Layout.FullMask;
        if (available == 0)
        {
            return -1;
        }

        // Free slot found — dirty the page before mutating occupancy (ACW-before-write). MRU hit: clusterChunkId was just read above.
        accessor.GetChunkAddress(clusterChunkId, true);

        // Fold the visibility summary BEFORE the CAS that publishes the bit. Placed after the full-cluster early-out so a scan that merely walks past a full
        // cluster does not raise its maximum for nothing; a fold whose CAS loop then finds the cluster full is only conservative, never wrong.
        NoteClusterBorn(clusterChunkId, bornTsn);

        while (true)
        {
            var slot = BitOperations.TrailingZeroCount(available);
            var desired = current | (1UL << slot);
            if (Interlocked.CompareExchange(ref occupancy, desired, current) == current)
            {
                return slot;
            }

            // CAS lost to a concurrent writer — re-read occupancy and retry. Cluster already dirtied above; the dirty mark is idempotent.
            current = occupancy;
            available = ~current & Layout.FullMask;
            if (available == 0)
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// Attempt to claim ONE NAMED slot in <paramref name="clusterChunkId"/>. Returns <see langword="true"/> on success; <see langword="false"/> if that slot
    /// is already occupied, out of the layout's range, or lost to a concurrent claimant.
    /// </summary>
    /// <remarks>
    /// <para><b>Why an exact-slot claim exists (#872 step 12).</b> The repair path re-sorts a cell and computes the whole destination layout before moving
    /// anything — which entity lands in which cluster AND in which slot is the output of the Morton sort. First fit reproduces that packing only when the
    /// requests are executed in queue order on one thread, which the Migrate phase does not promise: it slices the queue across workers. So the slot has to
    /// be nameable, or the sorted order survives only by scheduling luck (<c>AC-12.4</c>).</para>
    /// <para>Same dirty-then-mutate discipline as <see cref="TryClaimSlotInCluster{TStore}"/>: read with <c>dirty:false</c>, bail before touching
    /// ActiveChunkWriters when the slot is taken, and only then re-fetch dirty. The visibility fold sits after that early-out, so a request whose slot was
    /// ALREADY occupied costs nothing — but a request that loses the CAS has folded and dirtied for a claim that did not happen. Conservative in both
    /// directions (a raised maximum only gates a snapshot for longer; a dirty mark only writes a page back), and identical to what
    /// <see cref="TryClaimSlotInCluster{TStore}"/> does on its own retry path.</para>
    /// </remarks>
    private bool TryClaimExactSlotInCluster<TStore>(ref ChunkAccessor<TStore> accessor, int clusterChunkId, int slotIndex, long bornTsn)
        where TStore : struct, IPageStore
    {
        // Range first, and the order is load-bearing: C# masks a shift count to 6 bits, so `1UL << 64` is 1, not 0. Computing the bit before the check
        // would leave slot 64 aliasing slot 0 and relying on `||` short-circuiting to save it — correct today, and a trap for anyone who reorders the
        // clauses or splits them.
        if ((uint)slotIndex >= MaxSlotsPerCluster)
        {
            return false;
        }

        var bit = 1UL << slotIndex;
        if ((Layout.FullMask & bit) == 0)
        {
            return false;
        }

        var clusterBase = accessor.GetChunkAddress(clusterChunkId);
        ref var occupancy = ref *(ulong*)clusterBase;

        var current = occupancy;
        if ((current & bit) != 0)
        {
            return false;
        }

        accessor.GetChunkAddress(clusterChunkId, true);
        NoteClusterBorn(clusterChunkId, bornTsn);

        // One CAS, no retry loop. A retry would have to re-test the same bit, and if a sibling took it the answer cannot change — unlike first fit, where a
        // lost CAS only means "try a different slot". Losing here degrades to the caller's fallback chain, which is the correct response.
        return Interlocked.CompareExchange(ref occupancy, current | bit, current) == current;
    }

    /// <summary>
    /// Claim a slot in <paramref name="preferredClusterChunkId"/> if that cluster is still a live member of
    /// <paramref name="cellKey"/> and still has room; otherwise fall back to the ordinary first-fit scan.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a pin exists at all (#872 step 10).</b> First-fit is what this whole issue is repairing: it places an entity in whichever cluster the
    /// cursor reaches first, with no regard for how much that cluster's AABB has to grow, which is how bounds end up covering ~90 % of their cell. Intra-cell
    /// relocation computes a least-enlargement destination during detection; without a way to name it, the claim would scan from the cursor, find the SOURCE
    /// cluster — which is in the same cell's list and usually has a free slot — and hand the entity straight back.</para>
    /// <para><b>The pin is a preference, and the fallback is not a formality.</b> Detection runs a whole phase before the drain, so the chosen cluster can fill
    /// up or be drained and freed in between. Landing in a worse cluster costs selectivity; refusing the migration would strand the entity in a cluster it no
    /// longer belongs to, which is a correctness problem rather than a quality one. So a failed pin degrades to first fit.</para>
    /// <para><b>The <see cref="ClusterCellMap"/> check is an identity check, not a bounds check.</b> A chunk id that was freed can be reallocated to a
    /// different cell, and it would look like a perfectly good cluster with free slots. Claiming into it would put the entity in the wrong cell — <c>C13</c>
    /// broken, and
    /// silently, because every counter still balances. Verifying the pinned cluster still maps to the cell the request names is what rejects that.</para>
    /// </remarks>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, int preferredClusterChunkId, int preferredSlotIndex,
        ref ChunkAccessor<PersistentStore> accessor, ChangeSet changeSet, SpatialGrid grid, long bornTsn)
    {
        if (TryClaimPinnedSlot(cellKey, preferredClusterChunkId, preferredSlotIndex, ref accessor, grid, bornTsn, out var pinnedSlot))
        {
            return (preferredClusterChunkId, pinnedSlot);
        }

        return ClaimSlotInCell(cellKey, ref accessor, changeSet, grid, bornTsn);
    }

    /// <inheritdoc cref="ClaimSlotInCell(int, int, int, ref ChunkAccessor{PersistentStore}, ChangeSet, SpatialGrid, long)"/>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, int preferredClusterChunkId, int preferredSlotIndex,
        ref ChunkAccessor<TransientStore> accessor, SpatialGrid grid, long bornTsn)
    {
        if (TryClaimPinnedSlot(cellKey, preferredClusterChunkId, preferredSlotIndex, ref accessor, grid, bornTsn, out var pinnedSlot))
        {
            return (preferredClusterChunkId, pinnedSlot);
        }

        return ClaimSlotInCell(cellKey, ref accessor, grid, bornTsn);
    }

    /// <summary>
    /// Claim a slot for a <see cref="MigrationRequest.FreshCluster"/> relocation: in the cluster this Migrate slice already allocated for
    /// <paramref name="cellKey"/> if it still has room, otherwise in a new one — never by first fit, which would find the slots the slice's own drained
    /// sources just freed and move the entity back into the box it was leaving (step 14).
    /// </summary>
    /// <remarks>
    /// <para><b>The memo is the caller's, and it is slice-local by construction.</b> Migrate slices are carved on <c>DestCellKey</c>, and a relocation's
    /// destination cell is its source cell, so every FreshCluster request for one cell drains on one worker; a pair of locals in the drain loop is the
    /// whole of the bookkeeping, and no two workers can allocate for the same cell in one tick.</para>
    /// <para><b>Same allocation as the first-fit scan's slow path</b> — under <c>_finalizeLock</c>, cell map, pool membership, counts, occupancy bit 0
    /// written before the base is handed back — except that the scan cursor is left alone: this cluster is a spill target, not the next first-fit home.
    /// The pre-size bound already counts one allocation per pending migration, which is what this is.</para>
    /// </remarks>
    internal (int clusterChunkId, int slotIndex) ClaimSlotInFreshCluster<TStore>(int cellKey, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet,
        SpatialGrid grid, long bornTsn, ref int freshCell, ref int freshCluster) where TStore : struct, IPageStore
    {
        ref var cell = ref grid.GetCell(cellKey);
        if (freshCell == cellKey && freshCluster >= 0)
        {
            var slot = TryClaimSlotInCluster(ref accessor, freshCluster, bornTsn);
            if (slot >= 0)
            {
                Interlocked.Increment(ref cell.EntityCount);
                return (freshCluster, slot);
            }
        }

        int newChunkId;
        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        // The tree segment's creation takes _finalizeLock, so it is ensured before the latch; the fresh cluster's index add under it then promotes
        // the cell if the count says so, and never re-enters.
        var treeSegmentReady = CellTreePromoteThreshold == int.MaxValue || TryEnsureCellTreeSegment();
        _finalizeLock.Enter(ref nullCtx);
        try
        {
            newChunkId = AllocateNewCluster(changeSet);
            // Occupancy bit 0 is written BEFORE AddCluster publishes the cluster into the cell's pool. Published first, a concurrent claimer scanning the
            // cell could CAS a slot in the fresh cluster and have its bit clobbered by this store — a lost entity, measured 1 in 12 runs of
            // ClusterPlacementTests.ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell once least enlargement scanned every cluster (step 15). On arm64 the
            // order rests on CellClusterPool.AddCluster's release store of the cell's count, which this store precedes — not on the latch.
            Volatile.Write(ref *(ulong*)accessor.GetChunkAddress(newChunkId, true), 1UL); // no fold — see FreshClusterStaysUnknown
            EnsureClusterCellMapCapacityLocked(newChunkId + 1);
            ClusterCellMap[newChunkId] = cellKey;
            // Into the cell's per-cell index with an empty box BEFORE the pool publishes it — see AddClusterToPerCellIndexLocked for the two races
            // that letting the first spawner do it left open. The reset of a reused chunk id's stale box moves here for the same reason.
            EnsureClusterAabbsCapacityLocked(newChunkId + 1);
            var emptyBox = ClusterSpatialAabb.Empty;
            ClusterAabbs[newChunkId] = emptyBox;
            AddClusterToPerCellIndexLocked(newChunkId, cellKey, in emptyBox, treeSegmentReady);
            CellClusterPool.AddCluster(cellKey, newChunkId);
        }
        finally
        {
            _finalizeLock.Exit();
        }

        Interlocked.Increment(ref cell.ClusterCount);
        Interlocked.Increment(ref cell.EntityCount);

        TyphonEvent.EmitSpatialGridClusterCellAssign(newChunkId, cellKey, (ushort)Math.Min(ArchetypeId, ushort.MaxValue));
        freshCell = cellKey;
        freshCluster = newChunkId;
        return (newChunkId, 0);
    }

    /// <summary>
    /// The pinned half of the two overloads above: validate the pin, claim, and bump the cell's entity count.
    /// </summary>
    /// <remarks>
    /// <see cref="TryClaimSlotInCluster{TStore}"/> deliberately does NOT touch <see cref="CellState.EntityCount"/> — the scan overloads bump it at each of
    /// their three success sites instead. A pinned claim is a fourth success site and owes the same increment; omitting it makes the cell under-count by
    /// one per relocation, which nothing would fail on until a cell reports fewer entities than it holds.
    /// </remarks>
    private bool TryClaimPinnedSlot<TStore>(int cellKey, int preferredClusterChunkId, int preferredSlotIndex, ref ChunkAccessor<TStore> accessor,
        SpatialGrid grid, long bornTsn, out int slotIndex) where TStore : struct, IPageStore
    {
        slotIndex = -1;

        // ONE read of the field, into a local, and an acquire one. This runs on a Migrate worker while a sibling worker can be growing the same array —
        // EnsureClusterCellMapCapacity replaces the reference under _finalizeLock and then publishes entries into the replacement. Re-reading the field per
        // access let the length check land on the grown array and the index on the old one (an out-of-range throw), and on arm64 the plain reference store is
        // unordered against the element copies behind it, so a reader could see the new reference with stale contents. Volatile.Read pairs with that
        // publication and costs nothing on x64.
        var cellMap = Volatile.Read(ref ClusterCellMap);
        if (preferredClusterChunkId < 0)
        {
            return false;   // AnyCluster — not a pin, so nothing to reject
        }

        if (cellMap == null || (uint)preferredClusterChunkId >= (uint)cellMap.Length || cellMap[preferredClusterChunkId] != cellKey)
        {
            // Wave-2 K5. Rare by design (a stale pin), so an Interlocked increment on the rejection path costs nothing on the tick that matters.
            Interlocked.Increment(ref LastTickPinsRejected);
            return false;
        }

        // Two-step within the pin: the named slot first (repair), then first fit in the same cluster (step-10 relocation, and repair's own first fallback).
        // Ordered this way so a repair request that loses its exact slot still lands in the cluster the sort chose, which keeps the cluster's AABB right even
        // though the intra-cluster ordering is no longer the sorted one — Morton order inside a cluster buys nothing a query can see, the cluster's BOUND is
        // what selectivity reads.
        var slot = preferredSlotIndex >= 0 && TryClaimExactSlotInCluster(ref accessor, preferredClusterChunkId, preferredSlotIndex, bornTsn)
            ? preferredSlotIndex : TryClaimSlotInCluster(ref accessor, preferredClusterChunkId, bornTsn);
        if (slot < 0)
        {
            Interlocked.Increment(ref LastTickPinsRejected);   // wave-2 K5: the pinned cluster was live but full
            return false;
        }

        ref var cell = ref grid.GetCell(cellKey);
        Interlocked.Increment(ref cell.EntityCount);
        slotIndex = slot;
        return true;
    }


    /// <summary>Non-full clusters examined per placement before the best seen is taken. Bounds the scan on a cell holding hundreds of clusters.</summary>
    internal const int PlacementScanLimit = 64;

    /// <summary>
    /// Claim a slot in the non-full cluster of <paramref name="cellKey"/> whose stored bound grows least to admit the CELL-RELATIVE point
    /// (<paramref name="px"/>, <paramref name="py"/>, <paramref name="pz"/>). <c>false</c> when the cell has fewer than two clusters, no bound array,
    /// no non-full cluster among the first <see cref="PlacementScanLimit"/> examined, or the chosen cluster filled between the look and the claim —
    /// every one of which degrades to the cursor scan rather than failing.
    /// </summary>
    /// <remarks>
    /// <para><b>Every read here is one the cursor scan already makes, plus one indexed load of <see cref="ClusterAabbs"/> per non-full cluster.</b>
    /// The occupancy word is read through the accessor without dirtying (the reason <see cref="TryClaimSlotInCluster{TStore}"/> gives). The bound array
    /// reference is taken ONCE with an acquire read: a concurrent grow publishes a copy under <c>_finalizeLock</c> and this scan keeps reading the one it
    /// started on, which is complete for every cluster that existed when it was published — a chunk id past its end is a cluster born since, i.e. one
    /// holding at most a handful of entities, and <see cref="ClusterSpatialAabb.Empty"/> is what its box would read as.</para>
    /// <para><b>Boxes are copied, and a torn copy is harmless.</b> Outside the fence the six floats are widened by <c>ClusterRef.WriteSpatial</c>'s
    /// per-axis CAS and by the spawn path's union, both of which only ever move a min down or a max up, so any interleaving of old and new axes is still
    /// a box with min ≤ max. The one non-monotone write — the reset to <c>Empty</c> on a reused chunk id's first entity — can be seen half-applied as
    /// <c>+∞</c> min against a finite max, which the <c>MinX</c> test treats as empty (growth 0) or the growth computes as <c>+∞</c> (never chosen); a
    /// negative growth from the other half is clamped. None of it can misplace an entity outside its cell: candidates come from the cell's own list
    /// (<c>C13</c>), and the claim is the same CAS the cursor scan performs.</para>
    /// <para><b>Ties go to the lowest chunk id</b> (<c>AC-10.3</c>) for the reason <see cref="ChooseRelocationTarget"/> gives: the cell's list is in
    /// allocation order, which depends on worker interleaving, and placement must not be a function of scheduling.</para>
    /// <para><b>The cap implies ranking.</b> It is a decision about the least-enlargement candidate, so <see cref="SpatialGridConfig.GrowthCapPlacement"/>
    /// ranks whether or not <see cref="SpatialGridConfig.LeastEnlargementPlacement"/> is on: three arms from one binary — off, least enlargement, least
    /// enlargement with the cap. The first-fit-plus-cap arm §5.8.5 measured (81 % at birth on an unsorted fill) opened a fresh cluster for every arrival
    /// the cursor cluster could not admit, up to the open limit, and stretched the cursor cluster past the cap once it was reached; it was dropped when
    /// the batch ordering took over birth tightness, and the cap's numbers are the LE+CAP arm's.</para>
    /// </remarks>
    private bool TryClaimPlaced<TStore>(int cellKey, float px, float py, float pz, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet,
        SpatialGrid grid, long bornTsn, out int clusterChunkId, out int slotIndex) where TStore : struct, IPageStore
    {
        clusterChunkId = -1;
        slotIndex = -1;

        ref readonly var placement = ref grid.Config;
        var cap = placement.GrowthCapPlacement;
        var rank = placement.LeastEnlargementPlacement || cap;   // the cap is a decision about the least-enlargement candidate, so it ranks
        var excludeDraining = _repairSourceExclusions.Count > 0;
        if (!rank)
        {
            return false;
        }

        var clusters = CellClusterPool.GetClusters(cellKey);
        var length = clusters.Length;
        if (length == 0 || (length < 2 && !cap))
        {
            return false;   // nothing to rank (and, without the cap, nothing to open): the cursor scan is already O(1) here
        }

        var aabbs = Volatile.Read(ref ClusterAabbs);
        if (aabbs == null)
        {
            return false;
        }

        // Start at the cursor and wrap, for the reason the cursor exists: the prefix before it is probably full, and a full cluster costs an address
        // chase to learn nothing.
        var scanStart = CellClusterPool.GetScanCursor(cellKey);
        if (scanStart >= length)
        {
            scanStart = 0;
        }

        var scanLimit = PlacementScanLimit;
        var best = -1;
        var bestGrowth = float.PositiveInfinity;
        var bestSize = float.PositiveInfinity;
        var bestBox = ClusterSpatialAabb.Empty;
        var open = 0;
        for (var k = 0; k < length && open < scanLimit; k++)
        {
            var i = scanStart + k;
            if (i >= length)
            {
                i -= length;
            }

            var id = clusters[i];
            var occupancy = *(ulong*)accessor.GetChunkAddress(id);
            if ((~occupancy & Layout.FullMask) == 0)
            {
                continue;
            }

            // A cluster this tick's migrations are draining — a repair unit's source above all — is not a destination, however tight its box reads.
            // It reads tight BECAUSE it is being emptied, and an arrival placed into it keeps it alive: measured under Cruise at 8 ms, least
            // enlargement refilling the planner's half-drained sources left 1 461 clusters at 17 % occupancy where first fit had 530 at 47 %, for a
            // tightness that repair had already bought. The set is Prep's for this tick; outside the fence it is last tick's, and skipping a cluster
            // that was drained a tick ago costs at most one candidate.
            if (excludeDraining && _repairSourceExclusions.ContainsCluster(id))
            {
                continue;
            }

            if (IsRepairDestination(id))
            {
                continue;   // reserved for a repair plan's output — see _repairDestinationReservations
            }

            open++;

            var box = (uint)id < (uint)aabbs.Length ? aabbs[id] : ClusterSpatialAabb.Empty;
            float growth;
            var size = 0f;
            if (float.IsPositiveInfinity(box.MinX))
            {
                growth = 0f;   // an empty box fits the point exactly — the best destination there is (AC-10.4)
            }
            else
            {
                var flat = float.IsPositiveInfinity(box.MinZ) || float.IsNegativeInfinity(box.MaxZ);
                growth = GrowthToAdmit(in box, px, py, pz, flat, out size);
                if (growth < 0f)
                {
                    growth = 0f;
                }
            }

            // Least enlargement, then least resulting size, then chunk id — see GrowthToAdmit for why the size term is not optional.
            if (best < 0 || growth < bestGrowth || (growth == bestGrowth && (size < bestSize || (size == bestSize && id < best))))
            {
                bestGrowth = growth;
                bestSize = size;
                best = id;
                bestBox = box;
            }
        }

        if (best < 0)
        {
            return false;
        }

        if (cap && open < placement.MaxOpenClustersPerCell && ExceedsGrowthCap(in bestBox, px, py, pz, grid, cellKey))
        {
            clusterChunkId = AllocateClusterInCell(cellKey, ref accessor, changeSet, grid);
            slotIndex = 0;
            return true;
        }

        var slot = TryClaimSlotInCluster(ref accessor, best, bornTsn);
        if (slot < 0)
        {
            return false;   // filled between the look and the claim — degrade to first fit rather than re-rank
        }

        clusterChunkId = best;
        slotIndex = slot;
        return true;
    }

    /// <summary>
    /// Would admitting the CELL-RELATIVE point stretch <paramref name="box"/> past the population-aware cap on any axis? An empty box never does.
    /// </summary>
    /// <remarks>
    /// The cap is the cell's density-derived target (<see cref="DensityTargetRatio"/>, the same function the drift gate uses, never below
    /// <c>ClusterTargetExtentRatio</c>) times <see cref="SpatialGridConfig.GrowthCapSlack"/>. A cell whose population fits the bound resolves to the cell itself, so
    /// the cap never fires there; in constant mode the configured ratio is the cap. One root per arrival that reaches this test.
    /// </remarks>
    private bool ExceedsGrowthCap(in ClusterSpatialAabb box, float px, float py, float pz, SpatialGrid grid, int cellKey)
    {
        if (float.IsPositiveInfinity(box.MinX))
        {
            return false;
        }

        ref readonly var cfg = ref grid.Config;
        var flat = cfg.GridDepth == 1 || float.IsPositiveInfinity(box.MinZ) || float.IsNegativeInfinity(box.MaxZ);
        var density = DensityTargetRatio(grid.GetCell(cellKey).EntityCount, BitOperations.PopCount(Layout.FullMask), flat, cfg.ClusterTargetPackingSlack);
        var ratio = density > 0f ? MathF.Max(density, cfg.ClusterTargetExtentRatio) : cfg.ClusterTargetExtentRatio;
        var limit = ratio * cfg.GrowthCapSlack * cfg.CellSize;

        if (MathF.Max(box.MaxX, px) - MathF.Min(box.MinX, px) > limit || MathF.Max(box.MaxY, py) - MathF.Min(box.MinY, py) > limit)
        {
            return true;
        }

        return !flat && MathF.Max(box.MaxZ, pz) - MathF.Min(box.MinZ, pz) > limit;
    }

    /// <summary>
    /// Open a fresh cluster attached to <paramref name="cellKey"/> with slot 0 claimed, and return its chunk id. The slow path both cursor-scan
    /// overloads inline, lifted so the growth cap can take it while the cell still has room elsewhere.
    /// </summary>
    /// <remarks>
    /// Same latch and the same three operations for the same three reasons the scan overloads state (dual-segment lockstep allocation, the active-list
    /// append, the per-cell list plus back-pointer). Two deliberate differences: the scan cursor is NOT advanced, because the clusters ahead of this one
    /// still have room by construction — the cap opened this one while they were open — and <see cref="CellState.EntityCount"/> is left to the caller,
    /// which increments it at its success site exactly as every other claim path does. <see cref="CellState.ClusterCount"/> is bumped here.
    /// </remarks>
    private int AllocateClusterInCell<TStore>(int cellKey, ref ChunkAccessor<TStore> accessor, ChangeSet changeSet, SpatialGrid grid)
        where TStore : struct, IPageStore
    {
        int newChunkId;
        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        // The tree segment's creation takes _finalizeLock, so it is ensured before the latch; the fresh cluster's index add under it then promotes
        // the cell if the count says so, and never re-enters.
        var treeSegmentReady = CellTreePromoteThreshold == int.MaxValue || TryEnsureCellTreeSegment();
        _finalizeLock.Enter(ref nullCtx);
        try
        {
            newChunkId = AllocateNewCluster(changeSet);
            // Occupancy bit 0 is written BEFORE AddCluster publishes the cluster into the cell's pool. Published first, a concurrent claimer scanning the
            // cell could CAS a slot in the fresh cluster and have its bit clobbered by this store — a lost entity, measured 1 in 12 runs of
            // ClusterPlacementTests.ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell once least enlargement scanned every cluster (step 15). On arm64 the
            // order rests on CellClusterPool.AddCluster's release store of the cell's count, which this store precedes — not on the latch.
            Volatile.Write(ref *(ulong*)accessor.GetChunkAddress(newChunkId, true), 1UL); // no fold — see FreshClusterStaysUnknown
            // ...Locked: we already hold _finalizeLock and AccessControlSmall is not reentrant.
            EnsureClusterCellMapCapacityLocked(newChunkId + 1);
            ClusterCellMap[newChunkId] = cellKey;
            // Into the cell's per-cell index with an empty box BEFORE the pool publishes it — see AddClusterToPerCellIndexLocked for the two races
            // that letting the first spawner do it left open. The reset of a reused chunk id's stale box moves here for the same reason.
            EnsureClusterAabbsCapacityLocked(newChunkId + 1);
            var emptyBox = ClusterSpatialAabb.Empty;
            ClusterAabbs[newChunkId] = emptyBox;
            AddClusterToPerCellIndexLocked(newChunkId, cellKey, in emptyBox, treeSegmentReady);
            CellClusterPool.AddCluster(cellKey, newChunkId);
        }
        finally
        {
            _finalizeLock.Exit();
        }

        Interlocked.Increment(ref grid.GetCell(cellKey).ClusterCount);

        TyphonEvent.EmitSpatialGridClusterCellAssign(newChunkId, cellKey, (ushort)Math.Min(ArchetypeId, ushort.MaxValue));
        return newChunkId;
    }

    /// <summary>
    /// Position-aware claim: least-enlargement placement of the CELL-RELATIVE point when <see cref="SpatialGridConfig.LeastEnlargementPlacement"/> is on and the cell
    /// offers a choice, else the cursor first-fit scan. Every success site bumps <see cref="CellState.EntityCount"/> exactly as the scan overloads do.
    /// </summary>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, float px, float py, float pz, ref ChunkAccessor<PersistentStore> accessor,
        ChangeSet changeSet, SpatialGrid grid, long bornTsn)
    {
        if (TryClaimPlaced(cellKey, px, py, pz, ref accessor, changeSet, grid, bornTsn, out var clusterChunkId, out var slotIndex))
        {
            Interlocked.Increment(ref grid.GetCell(cellKey).EntityCount);
            return (clusterChunkId, slotIndex);
        }

        return ClaimSlotInCell(cellKey, ref accessor, changeSet, grid, bornTsn);
    }

    /// <inheritdoc cref="ClaimSlotInCell(int, float, float, float, ref ChunkAccessor{PersistentStore}, ChangeSet, SpatialGrid, long)"/>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, float px, float py, float pz, ref ChunkAccessor<TransientStore> accessor,
        SpatialGrid grid, long bornTsn)
    {
        if (TryClaimPlaced(cellKey, px, py, pz, ref accessor, null, grid, bornTsn, out var clusterChunkId, out var slotIndex))
        {
            Interlocked.Increment(ref grid.GetCell(cellKey).EntityCount);
            return (clusterChunkId, slotIndex);
        }

        return ClaimSlotInCell(cellKey, ref accessor, grid, bornTsn);
    }

    /// <summary>
    /// Pinned claim with a position: the named cluster and slot first (relocation, repair), then least-enlargement among the cell's clusters, then first
    /// fit. A cell-crossing request names <see cref="MigrationRequest.AnyCluster"/> and so takes the second step straight away.
    /// </summary>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, int preferredClusterChunkId, int preferredSlotIndex, float px, float py, float pz,
        ref ChunkAccessor<PersistentStore> accessor, ChangeSet changeSet, SpatialGrid grid, long bornTsn)
    {
        if (TryClaimPinnedSlot(cellKey, preferredClusterChunkId, preferredSlotIndex, ref accessor, grid, bornTsn, out var pinnedSlot))
        {
            return (preferredClusterChunkId, pinnedSlot);
        }

        return ClaimSlotInCell(cellKey, px, py, pz, ref accessor, changeSet, grid, bornTsn);
    }

    /// <inheritdoc cref="ClaimSlotInCell(int, int, int, float, float, float, ref ChunkAccessor{PersistentStore}, ChangeSet, SpatialGrid, long)"/>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, int preferredClusterChunkId, int preferredSlotIndex, float px, float py, float pz,
        ref ChunkAccessor<TransientStore> accessor, SpatialGrid grid, long bornTsn)
    {
        if (TryClaimPinnedSlot(cellKey, preferredClusterChunkId, preferredSlotIndex, ref accessor, grid, bornTsn, out var pinnedSlot))
        {
            return (preferredClusterChunkId, pinnedSlot);
        }

        return ClaimSlotInCell(cellKey, px, py, pz, ref accessor, grid, bornTsn);
    }

    /// <summary>
    /// Claim a free slot in a cluster belonging to the given spatial <paramref name="cellKey"/>, allocating a new cluster attached to the cell if none of
    /// its existing clusters has a free slot.
    /// </summary>
    /// <remarks>
    /// <para>This is the spatial-aware counterpart of <see cref="ClaimSlot(ref ChunkAccessor{PersistentStore}, ChangeSet, long)"/>. Unlike <c>ClaimSlot</c> it
    /// ignores <see cref="FreeClusterHead"/> — that hint is a global free-slot cache that cannot distinguish cells, so it's useless once spatial
    /// coherence is required. Instead, we scan this archetype's own cluster list for the target cell (typically ≤80 entries for AntHill-scale
    /// density, ≤15-30 ns scan cost).</para>
    /// <para>Under the Q10 resolution the scanned list is strictly this archetype's — other spatial archetypes sharing the grid have their own
    /// <see cref="CellClusterPool"/> instances, so no cross-archetype cluster chunk IDs ever appear in this scan.</para>
    /// <para>Every successful claim bumps the global <see cref="CellState.EntityCount"/>. Allocation of a new cluster additionally bumps the global
    /// <see cref="CellState.ClusterCount"/>, appends the cluster to this archetype's per-cell claim list, and records the mapping in
    /// <see cref="ClusterCellMap"/>.</para>
    /// </remarks>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(
        int cellKey,
        ref ChunkAccessor<PersistentStore> accessor,
        ChangeSet changeSet,
        SpatialGrid grid,
        long bornTsn)
    {
        ref var cell = ref grid.GetCell(cellKey);
        var clusters = CellClusterPool.GetClusters(cellKey);

        // Scan this archetype's existing clusters attached to this cell for a free slot. The scan is split into two phases around the per-cell cursor — the
        // logical index of the first cluster that might still have a free slot. Phase 1 walks [scanStart, len): clusters fill front-to-back, so for an
        // append-only spawn this is O(1) amortized (the cursor sits on the sole non-full tail cluster). Phase 2 walks [0, scanStart) and runs ONLY when
        // phase 1 found nothing — it self-heals a stale-high cursor (cross-tick swap-with-last drift in RemoveCluster, or a slot freed behind the cursor by
        // a parallel-migration release which — unlike serial destroy — deliberately does NOT reset the cursor). Phase 1 ∪ phase 2 cover the whole list, so a
        // new cluster is allocated only when every existing cluster is genuinely full. This makes the cursor a pure hint: stale values cost a redundant
        // scan, never a missed free slot.
        var scanStart = CellClusterPool.GetScanCursor(cellKey);
        if (scanStart > clusters.Length)
        {
            scanStart = clusters.Length;
        }

        // Phase 1 — forward scan from the cursor. firstNonFull tracks the contiguous full-cluster prefix so the cursor only advances past clusters that are
        // genuinely full (a non-full cluster earlier in the scan pins it — the cursor must never skip a cluster that still has a free slot).
        var firstNonFull = scanStart;
        for (var i = scanStart; i < clusters.Length; i++)
        {
            var clusterId = clusters[i];
            if (IsRepairDestination(clusterId))
            {
                continue;   // reserved for a repair plan's re-pack output this tick — see _repairDestinationReservations
            }

            var slot = TryClaimSlotInCluster(ref accessor, clusterId, bornTsn);
            if (slot < 0)
            {
                if (i == firstNonFull)
                {
                    firstNonFull = i + 1;
                }
                continue;
            }
            Debug.Assert(ClusterCellMap[clusterId] == cellKey, "the cell's cluster list handed out a cluster of another cell (CC-02)");
            CellClusterPool.AdvanceScanCursor(cellKey, firstNonFull);
            Interlocked.Increment(ref cell.EntityCount);
            return (clusterId, slot);
        }

        // Phase 2 — scan of the [0, scanStart) prefix the cursor skipped. Reached only when phase 1 found no free slot. On success the cursor is moved
        // BACKWARD (SetScanCursor, not the monotonic AdvanceScanCursor) to this phase's own contiguous-full prefix, so subsequent claims start in the
        // reclaimed region instead of re-walking the now-full tail. Safe as a plain write — the cell is worker-exclusive on the migration path and
        // single-threaded on spawn/destroy.
        var prefixFirstNonFull = 0;
        for (var i = 0; i < scanStart; i++)
        {
            var clusterId = clusters[i];
            if (IsRepairDestination(clusterId))
            {
                continue;   // reserved for a repair plan's re-pack output this tick — see _repairDestinationReservations
            }

            var slot = TryClaimSlotInCluster(ref accessor, clusterId, bornTsn);
            if (slot < 0)
            {
                if (i == prefixFirstNonFull)
                {
                    prefixFirstNonFull = i + 1;
                }
                continue;
            }
            Debug.Assert(ClusterCellMap[clusterId] == cellKey, "the cell's cluster list handed out a cluster of another cell (CC-02)");
            CellClusterPool.SetScanCursor(cellKey, prefixFirstNonFull);
            Interlocked.Increment(ref cell.EntityCount);
            return (clusterId, slot);
        }

        // No free slot in any cluster of this cell — allocate a new cluster and attach it to this archetype's per-cell claim list.
        // Slow path: protected by the per-archetype finalize latch. Three operations must be atomic w.r.t. other workers:
        //   (1) Dual-segment AllocateChunk — ClusterSegment + TransientSegment must return matching chunk IDs (lockstep).
        //       Worker interleave would mismatch them and crash the Debug.Assert.
        //   (2) AddToActiveList — appends to ActiveClusterIds[], increments ActiveClusterCount, bumps ClusterSetVersion.
        //   (3) CellClusterPool.AddCluster + ClusterCellMap[newChunkId] = cellKey — per-cell pool mutation + back-pointer.
        // These all happen here. The hot path (existing-cluster CAS above) does NOT take this lock.
        int newChunkId;
        ref var nullCtx0 = ref Unsafe.NullRef<WaitContext>();
        // The tree segment's creation takes _finalizeLock, so it is ensured before the latch; the fresh cluster's index add under it then promotes
        // the cell if the count says so, and never re-enters.
        var treeSegmentReady = CellTreePromoteThreshold == int.MaxValue || TryEnsureCellTreeSegment();
        _finalizeLock.Enter(ref nullCtx0);
        try
        {
            newChunkId = AllocateNewCluster(changeSet);
            // Occupancy bit 0 is written BEFORE AddCluster publishes the cluster into the cell's pool. Published first, a concurrent claimer scanning the
            // cell could CAS a slot in the fresh cluster and have its bit clobbered by this store — a lost entity, measured 1 in 12 runs of
            // ClusterPlacementTests.ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell once least enlargement scanned every cluster (step 15). On arm64 the
            // order rests on CellClusterPool.AddCluster's release store of the cell's count, which this store precedes — not on the latch.
            Volatile.Write(ref *(ulong*)accessor.GetChunkAddress(newChunkId, true), 1UL); // no fold — see FreshClusterStaysUnknown
            // ...Locked: we already hold _finalizeLock and AccessControlSmall is not reentrant.
            EnsureClusterCellMapCapacityLocked(newChunkId + 1);
            ClusterCellMap[newChunkId] = cellKey;
            // Into the cell's per-cell index with an empty box BEFORE the pool publishes it — see AddClusterToPerCellIndexLocked for the two races
            // that letting the first spawner do it left open. The reset of a reused chunk id's stale box moves here for the same reason.
            EnsureClusterAabbsCapacityLocked(newChunkId + 1);
            var emptyBox = ClusterSpatialAabb.Empty;
            ClusterAabbs[newChunkId] = emptyBox;
            AddClusterToPerCellIndexLocked(newChunkId, cellKey, in emptyBox, treeSegmentReady);
            CellClusterPool.AddCluster(cellKey, newChunkId);
            // The fresh cluster is appended at the end of the cell list and is the only one with free slots — point the cursor at it so the next claim
            // skips straight to it instead of re-scanning the now-full prefix.
            CellClusterPool.AdvanceScanCursor(cellKey, CellClusterPool.GetClusterCount(cellKey) - 1);
        }
        finally
        {
            _finalizeLock.Exit();
        }

        // Cell counters use Interlocked unconditionally (other archetypes sharing this grid may bump them too).
        Interlocked.Increment(ref cell.ClusterCount);
        Interlocked.Increment(ref cell.EntityCount);

        // Phase 3: Spatial:Grid:ClusterCellAssign instant — fired when a new cluster is bound to a cell.
        TyphonEvent.EmitSpatialGridClusterCellAssign(newChunkId, cellKey, (ushort)Math.Min(ArchetypeId, ushort.MaxValue));
        return (newChunkId, 0);
    }

    /// <summary>
    /// Pure-Transient overload of <see cref="ClaimSlotInCell(int, ref ChunkAccessor{PersistentStore}, ChangeSet, SpatialGrid, long)"/>. Identical logic,
    /// different accessor type.
    /// </summary>
    public (int clusterChunkId, int slotIndex) ClaimSlotInCell(int cellKey, ref ChunkAccessor<TransientStore> accessor, SpatialGrid grid, long bornTsn)
    {
        ref var cell = ref grid.GetCell(cellKey);
        var clusters = CellClusterPool.GetClusters(cellKey);

        // Two-phase cursor scan — see the PersistentStore overload above for the full rationale (O(M²) re-scan collapse, phase-2 self-heal, hint semantics).
        var scanStart = CellClusterPool.GetScanCursor(cellKey);
        if (scanStart > clusters.Length)
        {
            scanStart = clusters.Length;
        }

        // Phase 1 — forward scan from the cursor.
        var firstNonFull = scanStart;
        for (var i = scanStart; i < clusters.Length; i++)
        {
            var clusterId = clusters[i];
            if (IsRepairDestination(clusterId))
            {
                continue;   // reserved for a repair plan's re-pack output this tick — see _repairDestinationReservations
            }

            var slot = TryClaimSlotInCluster(ref accessor, clusterId, bornTsn);
            if (slot < 0)
            {
                if (i == firstNonFull)
                {
                    firstNonFull = i + 1;
                }
                continue;
            }
            CellClusterPool.AdvanceScanCursor(cellKey, firstNonFull);
            Interlocked.Increment(ref cell.EntityCount);
            return (clusterId, slot);
        }

        // Phase 2 — prefix scan, reached only when phase 1 found nothing. On success the cursor is moved backward to the reclaimed region.
        var prefixFirstNonFull = 0;
        for (var i = 0; i < scanStart; i++)
        {
            var clusterId = clusters[i];
            if (IsRepairDestination(clusterId))
            {
                continue;   // reserved for a repair plan's re-pack output this tick — see _repairDestinationReservations
            }

            var slot = TryClaimSlotInCluster(ref accessor, clusterId, bornTsn);
            if (slot < 0)
            {
                if (i == prefixFirstNonFull)
                {
                    prefixFirstNonFull = i + 1;
                }
                continue;
            }
            Debug.Assert(ClusterCellMap[clusterId] == cellKey, "the cell's cluster list handed out a cluster of another cell (CC-02)");
            CellClusterPool.SetScanCursor(cellKey, prefixFirstNonFull);
            Interlocked.Increment(ref cell.EntityCount);
            return (clusterId, slot);
        }

        // No free slot — allocate a new cluster and attach it to this archetype's per-cell claim list.
        // See PersistentStore overload above for the rationale on locking this slow path.
        int newChunkId;
        ref var nullCtx1 = ref Unsafe.NullRef<WaitContext>();
        // The tree segment's creation takes _finalizeLock, so it is ensured before the latch; the fresh cluster's index add under it then promotes
        // the cell if the count says so, and never re-enters.
        var treeSegmentReady = CellTreePromoteThreshold == int.MaxValue || TryEnsureCellTreeSegment();
        _finalizeLock.Enter(ref nullCtx1);
        try
        {
            newChunkId = AllocateNewCluster(null);
            // Occupancy bit 0 is written BEFORE AddCluster publishes the cluster into the cell's pool. Published first, a concurrent claimer scanning the
            // cell could CAS a slot in the fresh cluster and have its bit clobbered by this store — a lost entity, measured 1 in 12 runs of
            // ClusterPlacementTests.ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell once least enlargement scanned every cluster (step 15). On arm64 the
            // order rests on CellClusterPool.AddCluster's release store of the cell's count, which this store precedes — not on the latch.
            Volatile.Write(ref *(ulong*)accessor.GetChunkAddress(newChunkId, true), 1UL); // no fold — see FreshClusterStaysUnknown
            // ...Locked: we already hold _finalizeLock and AccessControlSmall is not reentrant.
            EnsureClusterCellMapCapacityLocked(newChunkId + 1);
            ClusterCellMap[newChunkId] = cellKey;
            // Into the cell's per-cell index with an empty box BEFORE the pool publishes it — see AddClusterToPerCellIndexLocked for the two races
            // that letting the first spawner do it left open. The reset of a reused chunk id's stale box moves here for the same reason.
            EnsureClusterAabbsCapacityLocked(newChunkId + 1);
            var emptyBox = ClusterSpatialAabb.Empty;
            ClusterAabbs[newChunkId] = emptyBox;
            AddClusterToPerCellIndexLocked(newChunkId, cellKey, in emptyBox, treeSegmentReady);
            CellClusterPool.AddCluster(cellKey, newChunkId);
            // Point the cursor at the fresh cluster — see the PersistentStore overload.
            CellClusterPool.AdvanceScanCursor(cellKey, CellClusterPool.GetClusterCount(cellKey) - 1);
        }
        finally
        {
            _finalizeLock.Exit();
        }

        Interlocked.Increment(ref cell.ClusterCount);
        Interlocked.Increment(ref cell.EntityCount);

        // Phase 3: Spatial:Grid:ClusterCellAssign instant — fired when a new cluster is bound to a cell.
        TyphonEvent.EmitSpatialGridClusterCellAssign(newChunkId, cellKey, (ushort)Math.Min(ArchetypeId, ushort.MaxValue));
        return (newChunkId, 0);
    }

    /// <summary>
    /// Reconstruct <see cref="ClusterCellMap"/> and the grid's per-cell state from the current active clusters' entity positions. Called at startup for
    /// spatial archetypes after the <see cref="SpatialGrid"/> is configured — on a fresh database this is a no-op (no active clusters); on a reopened
    /// database it re-derives the cluster→cell mapping from persisted data.
    /// </summary>
    /// <remarks>
    /// <para>Reads the first occupied entity's spatial field from each active cluster and uses
    /// <see cref="SpatialGrid.WorldToCellKeyFromSpatialField"/> to compute its cell. This relies on the spatial coherence invariant (all entities in a
    /// cluster belong to the same cell) — reading only the first entity is sufficient.</para>
    /// <para>Non-spatial archetypes and archetypes without a configured grid are no-ops. Pure-Transient archetypes are also skipped since their data doesn't
    /// survive restart.</para>
    /// <para><b>Precondition — NOT idempotent on a dirty grid.</b> This method ADDS to <see cref="CellState.EntityCount"/> /
    /// <see cref="CellState.ClusterCount"/> and appends cluster IDs to this archetype's <see cref="CellClusterPool"/>. Callers MUST pass either a
    /// fresh <see cref="SpatialGrid"/> or one that has been reset via <see cref="SpatialGrid.ResetCellState"/> (and a freshly ALLOCATED per-archetype pool;
    /// the pool has no reset of its own) — calling twice without that double-counts entities and duplicates cluster IDs in the pool. The single caller today
    /// (<c>DatabaseEngine.InitializeArchetypes</c>) constructs a fresh grid + allocates a fresh per-archetype pool inside <see cref="InitializeSpatial"/>
    /// immediately before this loop, satisfying the precondition.</para>
    /// </remarks>
    internal void RebuildCellState(SpatialGrid grid)
    {
        if (grid == null || !SpatialSlot.HasSpatialIndex || ClusterSegment == null)
        {
            return;
        }
        if (ActiveClusterCount == 0)
        {
            return;
        }

        EnsureClusterCellMapCapacity(PrimarySegmentCapacity);
        Array.Fill(ClusterCellMap, -1);

        ref readonly var ss = ref SpatialSlot;
        var componentOffset = Layout.ComponentOffset(ss.Slot);
        var compStride = Layout.ComponentSize(ss.Slot);
        var fieldType = ss.FieldInfo.FieldType;

        Interlocked.Increment(ref RebuildSegmentPassCount);
        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < ActiveClusterCount; i++)
            {
                var chunkId = ActiveClusterIds[i];
                var clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;
                if (occupancy == 0)
                {
                    continue;
                }

                var firstSlot = BitOperations.TrailingZeroCount(occupancy);
                var fieldPtr = clusterBase + componentOffset + firstSlot * compStride + ss.FieldOffset;
                var cellKey = grid.WorldToCellKeyFromSpatialField(fieldPtr, fieldType);

                ClusterCellMap[chunkId] = cellKey;
                CellClusterPool.AddCluster(cellKey, chunkId);
                ref var cell = ref grid.GetCell(cellKey);
                cell.ClusterCount++;
                cell.EntityCount += BitOperations.PopCount(occupancy);
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Number of logical passes over the cluster segment made by the startup rebuild paths since this object was created. Diagnostic: it exists so a test can
    /// assert the merged rebuild reads the segment ONCE where the legacy pair read it twice, rather than inferring that from a timing comparison (#872 AC-2.2).
    /// </summary>
    /// <remarks>
    /// Counts <b>passes, not accessors</b>. The parallel map opens one accessor per partition over a disjoint slice of the cluster range, which is still a
    /// single pass over the data — counting accessor creations instead would report worker count and make the assertion measure scheduling. Incremented once
    /// per rebuild call, so it is meaningful at every degree of parallelism rather than only at W=1.
    /// </remarks>
    internal int RebuildSegmentPassCount;

    /// <summary>
    /// Cluster AABBs whose recompute produced a different box since the last <see cref="ResetEscapeRateCounters"/> — the denominator of <c>Q2</c>'s escape
    /// rate (#872 step 9, <c>AC-9.8</c>).
    /// </summary>
    internal long AabbChangeCount;

    /// <summary>
    /// Of those, the ones whose new box is NOT contained by the box it replaced — the numerator of the escape rate.
    /// </summary>
    /// <remarks>
    /// <para><b>This is a bound on the real escape rate, not the rate itself, and the direction is stated so nobody reads it as exact.</b> <c>C5</c> defines
    /// an escape as a bound leaving its LEAF NODE's MBR; a leaf's MBR is the union of up to eleven entries and is therefore always at least as large as any
    /// one of them. So a box still inside its own previous box is certainly still inside the leaf: <c>containedInPrevious ⟹ inPlace</c>. This counter's
    /// complement is an UPPER bound on escapes, and the true in-place rate can only be better than what it reports.</para>
    /// <para>Measured before the tree exists on purpose. <c>C5</c>'s economics — ~14 µs/cell/tick escape-bound against ~94-235 µs for remove-and-reinsert —
    /// rest entirely on this ratio, and it is knowable today with the linear index still in place. Building the tree first and measuring after would mean
    /// discovering the premise was wrong with the implementation already committed to it.</para>
    /// </remarks>
    internal long AabbEscapeCount;

    /// <summary>Zero the escape-rate counters. Tests and benchmark harnesses call this to scope a measurement to one workload.</summary>
    internal void ResetEscapeRateCounters()
    {
        Interlocked.Exchange(ref AabbChangeCount, 0);
        Interlocked.Exchange(ref AabbEscapeCount, 0);
    }

    /// <summary>
    /// Record one AABB change for <c>Q2</c>. <paramref name="previous"/> is the box being replaced, <paramref name="fresh"/> the new one; both are in the
    /// same cell-relative frame, which is what makes the containment test meaningful.
    /// </summary>
    /// <remarks>
    /// Interlocked because the AABB refresh phase runs across fence workers on disjoint CLUSTER ranges — disjoint for the arrays they write, but not for
    /// these two counters. A plain increment would under-report by exactly the contention, which is worst in precisely the dense workloads the number is
    /// being collected for.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteAabbChange(in ClusterSpatialAabb previous, in ClusterSpatialAabb fresh)
    {
        Interlocked.Increment(ref AabbChangeCount);

        // A degenerate previous box (the Empty sentinel) is a first fill, not a move: +inf/-inf contains nothing, so counting it as an escape would inflate
        // the rate by the whole spawn burst.
        if (float.IsPositiveInfinity(previous.MinX))
        {
            return;
        }

        var contained = fresh.MinX >= previous.MinX && fresh.MinY >= previous.MinY && fresh.MinZ >= previous.MinZ
                        && fresh.MaxX <= previous.MaxX && fresh.MaxY <= previous.MaxY && fresh.MaxZ <= previous.MaxZ;
        if (!contained)
        {
            Interlocked.Increment(ref AabbEscapeCount);
        }
    }

    /// <summary>
    /// Per-cluster result of the rebuild map phase. Pure function of one cluster's bytes, so it can be computed on any worker without touching shared state;
    /// the reduce phase folds these into the grid, the pool and the per-cell index serially.
    /// </summary>
    private struct ClusterRebuildMapResult
    {
        /// <summary>
        /// The cluster's cell as COORDINATES, not a key. Resolving to a key means creating the cell, and creating cells from the parallel map phase would
        /// make every pool-slot index a function of the worker count — which the reduce's serial ordering exists to prevent (#872 step 8).
        /// </summary>
        public int CellX;
        public int CellY;
        public int CellZ;
        public int PopCount;
        public ClusterSpatialAabb Aabb;
    }

    /// <summary>
    /// Derive one cluster's cell key, occupancy and AABB in a single visit to its bytes. The pure half of <see cref="RebuildSpatialStateFromData"/>.
    /// </summary>
    /// <remarks>
    /// The cell coordinates and the AABB are NOT the same read: the coordinates come from
    /// <see cref="SpatialGrid.ReadCellCoordsFromSpatialField"/> on the first occupied slot, while the AABB unions
    /// <c>SpatialMaintainer.ReadAndValidateBoundsFromPtr</c> over every occupied slot. The first slot is therefore decoded twice, two different ways —
    /// merging the passes saves the WALK, not that read.
    /// <para>The asymmetry is deliberate and load-bearing: the AABB union SKIPS a slot whose bounds fail validation, while the cell uses the first occupied
    /// slot unconditionally. A cluster whose first slot is degenerate therefore gets a real cell and an AABB that ignores it — which is exactly what the
    /// two-pass version did, and what the differential test pins.</para>
    /// </remarks>
    private ClusterRebuildMapResult MapClusterForRebuild(int chunkId, SpatialGrid grid, ref ChunkAccessor<PersistentStore> accessor)
    {
        var result = default(ClusterRebuildMapResult);
        result.Aabb = ClusterSpatialAabb.Empty;

        var clusterBase = accessor.GetChunkAddress(chunkId);
        var occupancy = *(ulong*)clusterBase;
        result.PopCount = BitOperations.PopCount(occupancy);
        if (occupancy == 0)
        {
            return result;
        }

        ref readonly var ss = ref SpatialSlot;
        var firstSlot = BitOperations.TrailingZeroCount(occupancy);
        var firstFieldPtr = clusterBase + Layout.ComponentOffset(ss.Slot) + firstSlot * Layout.ComponentSize(ss.Slot) + ss.FieldOffset;
        grid.ReadCellCoordsFromSpatialField(firstFieldPtr, ss.FieldInfo.FieldType, out result.CellX, out result.CellY, out result.CellZ);

        // Delegate the union rather than inlining a twin of it. Inlining would save one GetChunkAddress (an MRU-cache hit on a line this method just touched)
        // and one occupancy load — worth far less than the ~18 % the merge itself buys — at the price of a SECOND copy of a [fatal][silent] CA-01 computation
        // that could silently diverge from this one. The 3D branch in particular is only covered through RecomputeClusterAabb, so a twin's 3D half would have
        // had no test at all.
        grid.CellOriginFromCoords(result.CellX, result.CellY, result.CellZ, out var originX, out var originY, out var originZ);
        result.Aabb = RecomputeClusterAabb(chunkId, ref accessor, originX, originY, originZ);
        return result;
    }

    /// <summary>
    /// Startup rebuild of the whole transient spatial layer from persisted cluster data, in ONE walk of the cluster segment. Replaces the back-to-back
    /// <see cref="RebuildCellState"/> + <see cref="RebuildClusterAabbs"/> pair at the two production call sites (#872 step 2).
    /// </summary>
    /// <param name="grid">The spatial grid to populate. Must be fresh or reset — see the precondition below.</param>
    /// <param name="epochManager">Epoch manager the map workers pin against. Required because the pin is per-thread: see the remarks.</param>
    /// <param name="maxWorkers">Degree of parallelism for the map phase. <c>1</c> forces the serial path; <c>0</c> or negative means
    /// <see cref="Environment.ProcessorCount"/>.</param>
    /// <remarks>
    /// <para>
    /// <b>Map in parallel, reduce serially.</b> Deriving a cluster's cell key and AABB is a pure function of that cluster's bytes, so the
    /// <c>O(entities)</c> half fans out across workers. Folding the results in is NOT pure — <see cref="AddClusterToPerCellIndex"/> grows arrays, lazily
    /// allocates a <c>PerCellSpatialSlot</c> and assigns index slots by APPEND ORDER — so the reduce runs single-threaded in
    /// <see cref="ActiveClusterIds"/> order. That ordering is what makes the output bit-identical regardless of worker count; parallelising the reduce would
    /// make <see cref="ClusterSpatialIndexSlot"/> depend on thread interleaving.
    /// </para>
    /// <para>
    /// <b>Each worker pins its own epoch.</b> <c>EpochManager.EnterScope</c> pins the CALLING thread, so the caller's guard does not cover a worker; each
    /// takes its own <see cref="EpochGuard"/> alongside its own <c>ChunkAccessor</c>, which is likewise not thread-safe.
    /// </para>
    /// <para>
    /// <b>Precondition — NOT idempotent on a dirty grid</b>, unchanged from <see cref="RebuildCellState"/>. This ADDS to <see cref="CellState.EntityCount"/> /
    /// <see cref="CellState.ClusterCount"/> and appends to <see cref="CellClusterPool"/>; calling twice without a fresh grid and pool double-counts. Kept as a
    /// caller obligation rather than absorbed here deliberately (#872 AC-2.3): resetting the grid from inside would let a caller silently discard a
    /// partially-populated one, and the sole production caller allocates both fresh in <see cref="InitializeSpatial"/> immediately beforehand.
    /// </para>
    /// </remarks>
    public void RebuildSpatialStateFromData(SpatialGrid grid, EpochManager epochManager, int maxWorkers = 0)
    {
        if (grid == null || !SpatialSlot.HasSpatialIndex || ClusterSegment == null)
        {
            return;
        }
        if (ActiveClusterCount == 0)
        {
            return;
        }

        // Union of both legacy passes' capacity preconditions, in their original order.
        EnsureClusterCellMapCapacity(PrimarySegmentCapacity);
        Array.Fill(ClusterCellMap, -1);
        EnsureClusterAabbsCapacity(PrimarySegmentCapacity);
        EnsureClusterSpatialIndexSlotCapacity(PrimarySegmentCapacity);
        EnsureClusterWriteBookkeepingCapacity(PrimarySegmentCapacity);
        if (PerCellIndex != null)
        {
            // Before the clear, not after: clearing the slots drops the last reference to every promoted cell's tree, and those trees own chunks of a
            // TRANSIENT segment that nothing reclaims. See ReleaseAllCellTrees.
            ReleaseAllCellTrees(epochManager);
            Array.Clear(PerCellIndex);

            // The counter has to follow the clear. Leaving it stale makes RefitPromotedCellTrees and RebindCellTreeBackPointers walk an array of nulls
            // forever after a rebuild — harmless — but it also makes PromotedCellCount lie to the tests that use it as their non-vacuity guard, which is how
            // a promotion test passes without promoting anything.
            PromotedCellCount = 0;
        }
        Array.Fill(ClusterSpatialIndexSlot, -1);

        var count = ActiveClusterCount;

        // The only allocation here that scales with the database: 36 B per cluster (two ints plus a 28 B ClusterSpatialAabb), so ~360 KB at 10 K clusters and
        // ~56 MB — on the LOH — at 1.5 M. Startup-only and freed immediately, but worth stating because it is the figure that would decide whether a very
        // large open needs the map streamed in slices instead of materialised whole.
        var mapped = new ClusterRebuildMapResult[count];
        var workers = maxWorkers <= 0 ? Environment.ProcessorCount : maxWorkers;

        // ─── Map ───
        var autoWorkers = maxWorkers <= 0;
        Interlocked.Increment(ref RebuildSegmentPassCount);

        if (workers <= 1 || epochManager == null || (autoWorkers && count < ParallelRebuildThreshold))
        {
            var accessor = ClusterSegment.CreateChunkAccessor();
            try
            {
                for (var i = 0; i < count; i++)
                {
                    mapped[i] = MapClusterForRebuild(ActiveClusterIds[i], grid, ref accessor);
                }
            }
            finally
            {
                accessor.Dispose();
            }
        }
        else
        {
            // Partition the cluster range by hand rather than using Parallel.For's TLocal overload. Three reasons, each of which bit:
            //
            //   1. The TLocal form has no exception guarantee worth relying on. If the initializer throws between EnterScope and CreateChunkAccessor the
            //      half-built value is discarded and the epoch pin leaks — a ThreadPool thread pinned forever freezes MinActiveEpoch and blocks page
            //      eviction for the life of the process. And if the BODY throws, the value it would have returned is lost, so the accessor's page-slot
            //      ref-counts are never released. That path is reachable: a non-finite coordinate in a cluster's first occupied slot throws out of
            //      WorldToCellKey.
            //   2. ChunkAccessor is [NoCopy] and ~430 bytes, and the TLocal delegate signature copies it in and out on EVERY iteration.
            //   3. localInit fires once per TASK, not once per worker, so a per-init counter measures scheduling rather than passes.
            //
            // A plain range-per-partition loop gives ordinary try/finally, one accessor per partition held by ref, and a deterministic partition count.
            var partitions = Math.Min(workers, count);
            var perPartition = (count + partitions - 1) / partitions;
            var options = new ParallelOptions { MaxDegreeOfParallelism = workers };

            Parallel.For(0, partitions, options, p =>
            {
                var start = p * perPartition;
                if (start >= count)
                {
                    return;
                }
                var end = Math.Min(start + perPartition, count);

                // EnterScope pins the CALLING thread, so each partition takes its own pin; the caller's guard does not reach here.
                var depth = epochManager.EnterScope();
                try
                {
                    var accessor = ClusterSegment.CreateChunkAccessor();
                    try
                    {
                        for (var i = start; i < end; i++)
                        {
                            mapped[i] = MapClusterForRebuild(ActiveClusterIds[i], grid, ref accessor);
                        }
                    }
                    finally
                    {
                        accessor.Dispose();
                    }
                }
                finally
                {
                    epochManager.ExitScope(depth);
                }
            });
        }

        // ─── Reduce ───
        // Serial, in ActiveClusterIds order, so the append-ordered index slots and pool contents do not depend on how the map was scheduled.
        for (var i = 0; i < count; i++)
        {
            var chunkId = ActiveClusterIds[i];
            ref var m = ref mapped[i];

            // Written even for an empty cluster: RebuildClusterAabbs stored Empty for those, and bit-identical means bit-identical.
            ClusterAabbs[chunkId] = m.Aabb;

            if (m.PopCount == 0)
            {
                continue;   // RebuildCellState skipped these outright, leaving ClusterCellMap at -1
            }

            // The cell is CREATED here, in the serial reduce, from the coordinates the parallel map produced. Creation order is therefore ActiveClusterIds
            // order — the same ordering that already makes ClusterSpatialIndexSlot independent of the worker count, and what keeps the whole rebuild's output
            // bit-identical across W (see the map/reduce rationale above).
            var cellKey = grid.ComputeCellKey(m.CellX, m.CellY, m.CellZ);

            ClusterCellMap[chunkId] = cellKey;
            CellClusterPool.AddCluster(cellKey, chunkId);
            ref var cell = ref grid.GetCell(cellKey);
            cell.ClusterCount++;
            cell.EntityCount += m.PopCount;

            if (float.IsPositiveInfinity(m.Aabb.MinX))
            {
                continue;   // degenerate — every slot failed validation
            }

            AddClusterToPerCellIndex(chunkId, cellKey, m.Aabb);
        }
    }

    /// <summary>
    /// Below this many active clusters the AUTOMATIC worker choice stays single-threaded.
    /// <para><b>Not measured.</b> It assumes a full-ish cluster (~64 entities), which makes this roughly 4 000 entities of map work — enough to cover a
    /// <c>Parallel.For</c> fan-out. A sparse grid gives far fewer entities per cluster (the rebuild benchmark's own fixture averages ~20), so the real
    /// break-even moves with occupancy. Revisit with a measurement rather than trusting this number.</para>
    /// <para>It gates only <c>maxWorkers &lt;= 0</c>. An explicit worker count is an explicit request and always fans out — otherwise a determinism test could
    /// not exercise the parallel path without spawning 4 000 entities, and would silently assert on the serial path instead while appearing to cover W.</para>
    /// </summary>
    internal const int ParallelRebuildThreshold = 64;

    /// <summary>
    /// Grow <see cref="ClusterCellMap"/> to hold at least <paramref name="requiredLength"/> entries, initializing new slots to <c>-1</c> (unmapped).
    /// Called lazily by <c>ClaimSlotInCell</c> when a new cluster chunk ID lands beyond the current bounds.
    /// </summary>
    /// <remarks>See the PER-ARCHETYPE ARRAY GROWTH banner: the fast path is a lock-free length compare, only an actual grow serialises.</remarks>
    internal void EnsureClusterCellMapCapacity(int requiredLength)
    {
        if (Volatile.Read(ref ClusterCellMap) is { } current && current.Length >= requiredLength)
        {
            return;
        }

        ThrowIfGrowingInsideMigrateSlice(nameof(ClusterCellMap), requiredLength, ClusterCellMap?.Length ?? 0);

        ref var growCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref growCtx);
        try
        {
            EnsureClusterCellMapCapacityLocked(requiredLength);
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>
    /// The <c>…Locked</c> bodies below all document "caller must hold <c>_finalizeLock</c>", and a comment is not a contract. Calling one without the latch
    /// reintroduces exactly the lost-write this whole family exists to stop, and it does so silently, so the assertion is worth more than the check costs.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertFinalizeLockHeld(string caller) =>
        Debug.Assert(_finalizeLock.Lock.IsLockedByCurrentThread, $"{caller} mutates shared per-archetype state and must be called with _finalizeLock held");

    /// <summary>The growth body of <see cref="EnsureClusterCellMapCapacity"/>. Caller must hold <c>_finalizeLock</c> exclusively.</summary>
    /// <remarks>
    /// Carries the in-slice refusal a second time, and that is not redundant: <c>ClaimSlotInCell</c>'s new-cluster path already holds the latch and so calls
    /// this body DIRECTLY, bypassing the wrapper — and a parallel Migrate worker reaches that path whenever a migration needs a fresh destination cluster.
    /// Without the check here, <see cref="ClusterCellMap"/> would be the one array of the five that a slice could still reallocate, which is exactly what
    /// MD-02 now says cannot happen. It stays unreachable for the same reason as its siblings: the fence pre-sizes this array to the same bound.
    /// </remarks>
    internal void EnsureClusterCellMapCapacityLocked(int requiredLength)
    {
        AssertFinalizeLockHeld(nameof(EnsureClusterCellMapCapacityLocked));
        if (ClusterCellMap != null && ClusterCellMap.Length < requiredLength)
        {
            ThrowIfGrowingInsideMigrateSlice(nameof(ClusterCellMap), requiredLength, ClusterCellMap.Length);
        }

        if (ClusterCellMap == null)
        {
            var initial = Math.Max(16, requiredLength);
            var seeded = new int[initial];
            Array.Fill(seeded, -1);
            Volatile.Write(ref ClusterCellMap, seeded);
            return;
        }
        if (ClusterCellMap.Length >= requiredLength)
        {
            return;
        }
        // Defensive: if ClusterCellMap.Length is ever 0 (shouldn't happen through normal
        // construction — we always allocate >= 16 — but a future constructor path could regress)
        // start the doubling from 1 instead of 0 to avoid an infinite loop.
        var newLen = Math.Max(ClusterCellMap.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        var oldLen = ClusterCellMap.Length;
        // Copy-fill-publish rather than Array.Resize(ref field): the resize overload stores the new reference BEFORE the tail is seeded, so a lock-free
        // reader can observe an array whose new entries still read 0 — a valid cell key — instead of -1. Same shape as GrowClusterVisibilityCapacityLocked.
        var grown = new int[newLen];
        Array.Copy(ClusterCellMap, grown, oldLen);
        Array.Fill(grown, -1, oldLen, newLen - oldLen);
        Volatile.Write(ref ClusterCellMap, grown);
    }

    /// <summary>
    /// Grow <see cref="ClusterAabbs"/> to hold at least <paramref name="requiredLength"/> entries. Issue #230.
    /// New slots are left at <see cref="ClusterSpatialAabb.Empty"/> (neutral seed for subsequent unions).
    /// </summary>
    /// <remarks>See the PER-ARCHETYPE ARRAY GROWTH banner: the fast path is a lock-free length compare, only an actual grow serialises.</remarks>
    internal void EnsureClusterAabbsCapacity(int requiredLength)
    {
        if (Volatile.Read(ref ClusterAabbs) is { } current && current.Length >= requiredLength)
        {
            return;
        }

        // Refused from a slice like the other cluster-indexed arrays, and this is the one the refusal matters most for: ExecuteMigrations takes
        // `ref ClusterAabbs[dstChunkId]` and holds that interior reference across the whole bounds union. A sibling growing concurrently would send the union
        // into the abandoned array — a lost bound rather than a lost handle, which queries then prune against.
        ThrowIfGrowingInsideMigrateSlice(nameof(ClusterAabbs), requiredLength, ClusterAabbs?.Length ?? 0);

        ref var growCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref growCtx);
        try
        {
            EnsureClusterAabbsCapacityLocked(requiredLength);
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>The growth body of <see cref="EnsureClusterAabbsCapacity"/>. Caller must hold <c>_finalizeLock</c> exclusively.</summary>
    internal void EnsureClusterAabbsCapacityLocked(int requiredLength)
    {
        AssertFinalizeLockHeld(nameof(EnsureClusterAabbsCapacityLocked));
        if (ClusterAabbs == null)
        {
            var initial = Math.Max(16, requiredLength);
            var seeded = new ClusterSpatialAabb[initial];
            for (var i = 0; i < initial; i++)
            {
                seeded[i] = ClusterSpatialAabb.Empty;
            }
            Volatile.Write(ref ClusterAabbs, seeded);
            return;
        }
        if (ClusterAabbs.Length >= requiredLength)
        {
            return;
        }
        var newLen = Math.Max(ClusterAabbs.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        var oldLen = ClusterAabbs.Length;
        // Copy-fill-publish — Array.Resize(ref field) would publish the reference before the Empty seed runs, and a zero-filled ClusterSpatialAabb is a
        // DEGENERATE box at the cell origin rather than a neutral union identity, so a reader in that window widens every subsequent union to the origin.
        var grown = new ClusterSpatialAabb[newLen];
        Array.Copy(ClusterAabbs, grown, oldLen);
        for (var i = oldLen; i < newLen; i++)
        {
            grown[i] = ClusterSpatialAabb.Empty;
        }
        Volatile.Write(ref ClusterAabbs, grown);
    }

    /// <summary>
    /// Grow <see cref="ClusterSpatialIndexSlot"/> to hold at least <paramref name="requiredLength"/> entries, initializing new slots to <c>-1</c> (not in
    /// the per-cell index). Issue #230.
    /// </summary>
    /// <remarks>See the PER-ARCHETYPE ARRAY GROWTH banner: the fast path is a lock-free length compare, only an actual grow serialises.</remarks>
    internal void EnsureClusterSpatialIndexSlotCapacity(int requiredLength)
    {
        if (Volatile.Read(ref ClusterSpatialIndexSlot) is { } current && current.Length >= requiredLength)
        {
            return;
        }

        ThrowIfGrowingInsideMigrateSlice(nameof(ClusterSpatialIndexSlot), requiredLength, ClusterSpatialIndexSlot?.Length ?? 0);

        ref var growCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref growCtx);
        try
        {
            EnsureClusterSpatialIndexSlotCapacityLocked(requiredLength);
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>The growth body of <see cref="EnsureClusterSpatialIndexSlotCapacity"/>. Caller must hold <c>_finalizeLock</c> exclusively.</summary>
    internal void EnsureClusterSpatialIndexSlotCapacityLocked(int requiredLength)
    {
        AssertFinalizeLockHeld(nameof(EnsureClusterSpatialIndexSlotCapacityLocked));
        if (ClusterSpatialIndexSlot == null)
        {
            var initial = Math.Max(16, requiredLength);
            var seeded = new int[initial];
            Array.Fill(seeded, -1);
            Volatile.Write(ref ClusterSpatialIndexSlot, seeded);
            return;
        }
        if (ClusterSpatialIndexSlot.Length >= requiredLength)
        {
            return;
        }
        var newLen = Math.Max(ClusterSpatialIndexSlot.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        var oldLen = ClusterSpatialIndexSlot.Length;
        var grown = new int[newLen];
        Array.Copy(ClusterSpatialIndexSlot, grown, oldLen);
        Array.Fill(grown, -1, oldLen, newLen - oldLen);
        Volatile.Write(ref ClusterSpatialIndexSlot, grown);

        // The new array REPLACES the old one, and this array doubles as every cell tree's PayloadBackPointers — see RebindCellTreeBackPointers.
        RebindCellTreeBackPointers();
    }

    /// <summary>
    /// Grow the four write-time bookkeeping arrays — <see cref="ClusterProcessBitmap"/>,
    /// <see cref="ClusterMigrationPendingSlots"/>, <see cref="ClusterMigrationDestCellKeys"/>,
    /// <see cref="ClusterShrinkPendingAxes"/> — in lockstep. Called alongside
    /// <see cref="EnsureClusterAabbsCapacity"/> so the four arrays are always sized to match the cluster segment's chunk-id range.
    /// </summary>
    /// <remarks>See the PER-ARCHETYPE ARRAY GROWTH banner: the fast path is a lock-free length compare, only an actual grow serialises.</remarks>
    internal void EnsureClusterWriteBookkeepingCapacity(int requiredLength)
    {
        var requiredBitmapWords = (requiredLength + 63) >> 6;
        if (Volatile.Read(ref ClusterMigrationPendingSlots) is { } slots && slots.Length >= requiredLength
            && Volatile.Read(ref ClusterProcessBitmap) is { } bitmap && bitmap.Length >= requiredBitmapWords)
        {
            return;
        }

        // Named by whichever of the two the fast path actually found short — the bitmap is sized in WORDS, so reporting the slot array's length for a
        // bitmap-driven grow would point the reader at a bound that is not the one that failed.
        var slotsShort = ClusterMigrationPendingSlots == null || ClusterMigrationPendingSlots.Length < requiredLength;
        ThrowIfGrowingInsideMigrateSlice(
            slotsShort ? nameof(ClusterMigrationPendingSlots) : nameof(ClusterProcessBitmap),
            slotsShort ? requiredLength : requiredBitmapWords,
            slotsShort ? ClusterMigrationPendingSlots?.Length ?? 0 : ClusterProcessBitmap?.Length ?? 0);

        ref var growCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref growCtx);
        try
        {
            EnsureClusterWriteBookkeepingCapacityLocked(requiredLength);
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>The growth body of <see cref="EnsureClusterWriteBookkeepingCapacity"/>. Caller must hold <c>_finalizeLock</c> exclusively.</summary>
    internal void EnsureClusterWriteBookkeepingCapacityLocked(int requiredLength)
    {
        AssertFinalizeLockHeld(nameof(EnsureClusterWriteBookkeepingCapacityLocked));
        Debug.Assert(!InPrepSlice, "a Prep slice must not grow the per-cluster arrays another slice is reading (#886)");
        // ClusterProcessBitmap: 1 bit per cluster → (requiredLength + 63) / 64 long words.
        var requiredWords = (requiredLength + 63) >> 6;
        if (ClusterProcessBitmap == null)
        {
            Volatile.Write(ref ClusterProcessBitmap, new long[Math.Max(1, requiredWords)]);
        }
        else if (ClusterProcessBitmap.Length < requiredWords)
        {
            var newLen = Math.Max(ClusterProcessBitmap.Length, 1);
            while (newLen < requiredWords)
            {
                newLen *= 2;
            }

            var grownBitmap = new long[newLen];
            Array.Copy(ClusterProcessBitmap, grownBitmap, ClusterProcessBitmap.Length);
            Volatile.Write(ref ClusterProcessBitmap, grownBitmap);
        }

        // Per-cluster arrays sized 1:1 with clusterChunkId range.
        if (ClusterMigrationPendingSlots == null)
        {
            var initial = Math.Max(16, requiredLength);
            var seededKeys = new int[initial];
            Array.Fill(seededKeys, -1);
            Volatile.Write(ref ClusterMigrationDestCellKeys, seededKeys);
            Volatile.Write(ref ClusterShrinkPendingAxes, new byte[initial]);
            // Published LAST: it is the array the lock-free fast path length-checks, so every sibling array must already be visible behind it.
            Volatile.Write(ref ClusterMigrationPendingSlots, new ulong[initial]);
            return;
        }
        if (ClusterMigrationPendingSlots.Length >= requiredLength)
        {
            return;
        }
        var newClusterLen = Math.Max(ClusterMigrationPendingSlots.Length, 1);
        while (newClusterLen < requiredLength)
        {
            newClusterLen *= 2;
        }

        var oldLen = ClusterMigrationPendingSlots.Length;
        var grownSlots = new ulong[newClusterLen];
        Array.Copy(ClusterMigrationPendingSlots, grownSlots, oldLen);
        var grownKeys = new int[newClusterLen];
        Array.Copy(ClusterMigrationDestCellKeys, grownKeys, oldLen);
        Array.Fill(grownKeys, -1, oldLen, newClusterLen - oldLen);
        var grownAxes = new byte[newClusterLen];
        Array.Copy(ClusterShrinkPendingAxes, grownAxes, oldLen);
        Volatile.Write(ref ClusterMigrationDestCellKeys, grownKeys);
        Volatile.Write(ref ClusterShrinkPendingAxes, grownAxes);
        Volatile.Write(ref ClusterMigrationPendingSlots, grownSlots);
    }

    /// <summary>
    /// Grow <see cref="PerCellIndex"/> to hold at least <paramref name="requiredLength"/> entries. New slots are left <c>null</c> —
    /// each <see cref="PerCellSpatialSlot"/> is lazily allocated on first cluster insertion into that cell via <see cref="AddClusterToPerCellIndex"/>.
    /// Issue #230.
    /// </summary>
    /// <remarks>See the PER-ARCHETYPE ARRAY GROWTH banner: the fast path is a lock-free length compare, only an actual grow serialises.</remarks>
    internal void EnsurePerCellIndexCapacity(int requiredLength)
    {
        if (Volatile.Read(ref PerCellIndex) is { } current && current.Length >= requiredLength)
        {
            return;
        }

        ThrowIfGrowingInsideMigrateSlice(nameof(PerCellIndex), requiredLength, PerCellIndex?.Length ?? 0);

        ref var growCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref growCtx);
        try
        {
            EnsurePerCellIndexCapacityLocked(requiredLength);
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>The growth body of <see cref="EnsurePerCellIndexCapacity"/>. Caller must hold <c>_finalizeLock</c> exclusively.</summary>
    internal void EnsurePerCellIndexCapacityLocked(int requiredLength)
    {
        AssertFinalizeLockHeld(nameof(EnsurePerCellIndexCapacityLocked));
        if (PerCellIndex == null)
        {
            Volatile.Write(ref PerCellIndex, new PerCellSpatialSlot[Math.Max(16, requiredLength)]);
            return;
        }
        if (PerCellIndex.Length >= requiredLength)
        {
            return;
        }
        var newLen = Math.Max(PerCellIndex.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        var grown = new PerCellSpatialSlot[newLen];
        Array.Copy(PerCellIndex, grown, PerCellIndex.Length);
        Volatile.Write(ref PerCellIndex, grown);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Issue #233: Dormancy capacity + core logic
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Grow <see cref="SleepStates"/> and <see cref="SleepCounters"/> to hold at least <paramref name="requiredLength"/> entries.
    /// New entries initialize to <see cref="ClusterSleepState.Active"/> / 0. Issue #233.
    /// </summary>
    internal void EnsureSleepStateCapacity(int requiredLength)
    {
        if (SleepStates == null || SleepCounters == null)
        {
            return; // Dormancy not enabled for this archetype
        }
        if (SleepStates.Length >= requiredLength)
        {
            return;
        }
        var newLen = Math.Max(SleepStates.Length, 1);
        while (newLen < requiredLength)
        {
            newLen *= 2;
        }
        // SleepStates: new entries default to 0 = Active (Array.Resize zero-fills)
        Array.Resize(ref SleepStates, newLen);
        // SleepCounters: new entries default to 0 (Array.Resize zero-fills)
        Array.Resize(ref SleepCounters, newLen);
    }

    /// <summary>
    /// Advance sleep counters for all active clusters and transition idle clusters to <see cref="ClusterSleepState.Sleeping"/>.
    /// Also handles heartbeat wake for already-sleeping clusters. Called single-threaded from <c>WriteClusterTickFence</c>
    /// after migrations and AABB recomputation. Issue #233.
    /// </summary>
    /// <param name="dirtyBits">Occupancy-masked dirty bitmap snapshot from the tick fence. Word index = chunkId.
    /// A nonzero word means at least one entity in that cluster was written this tick.</param>
    /// <param name="tickNumber">Current tick number, used for heartbeat staggering.</param>
    internal void DormancySweep(long[] dirtyBits, long tickNumber)
    {
        if (SleepStates == null || SleepThresholdTicks <= 0)
        {
            return;
        }

        for (var i = 0; i < ActiveClusterCount; i++)
        {
            var chunkId = ActiveClusterIds[i];
            if (chunkId >= SleepStates.Length)
            {
                continue;
            }

            var state = SleepStates[chunkId];

            if (state == ClusterSleepState.Active)
            {
                // Check dirty bitmap: nonzero word means at least one entity written this tick
                var dirty = chunkId < dirtyBits.Length && dirtyBits[chunkId] != 0;
                if (dirty)
                {
                    SleepCounters[chunkId] = 0;
                }
                else
                {
                    var counter = SleepCounters[chunkId] + 1;
                    if (counter >= SleepThresholdTicks)
                    {
                        SleepStates[chunkId] = ClusterSleepState.Sleeping;
                        SleepingClusterCount++;
                    }
                    else
                    {
                        SleepCounters[chunkId] = (ushort)counter;
                    }
                }
            }
            else if (state == ClusterSleepState.Sleeping && HeartbeatIntervalTicks > 0)
            {
                // Heartbeat: staggered wake so only ~1/N sleeping clusters wake per tick
                if ((int)(tickNumber % HeartbeatIntervalTicks) == chunkId % HeartbeatIntervalTicks)
                {
                    SleepStates[chunkId] = ClusterSleepState.WakePending;
                    // SleepingClusterCount is decremented when WakePending→Active in TransitionWakePendingToActive
                }
            }
            // WakePending clusters are left alone — they'll transition to Active at tick start.
        }
    }

    /// <summary>
    /// Process a single wake request: if the cluster is <see cref="ClusterSleepState.Sleeping"/>, transition to <see cref="ClusterSleepState.WakePending"/>.
    /// Deduplication is implicit: calling on an already-WakePending cluster is a no-op. Called single-threaded from <c>WriteClusterTickFence</c> after
    /// draining <see cref="DormancyReporter"/>. Issue #233.
    /// </summary>
    internal void ProcessWakeRequest(int chunkId)
    {
        if (SleepStates == null || chunkId >= SleepStates.Length)
        {
            return;
        }
        if (SleepStates[chunkId] == ClusterSleepState.Sleeping)
        {
            SleepStates[chunkId] = ClusterSleepState.WakePending;
            // SleepingClusterCount is decremented in TransitionWakePendingToActive (next tick start)
        }
    }

    /// <summary>
    /// Transition all <see cref="ClusterSleepState.WakePending"/> clusters to <see cref="ClusterSleepState.Active"/>.
    /// Called single-threaded from <c>BuildTierIndexesAtTickStart</c> before tier index rebuild so woken clusters appear in this tick's per-tier lists.
    /// Guarded by <see cref="_lastWakeTransitionTick"/> to avoid redundant scans when multiple systems reference the same archetype. Issue #233.
    /// </summary>
    internal void TransitionWakePendingToActive(long currentTick)
    {
        if (SleepStates == null || _lastWakeTransitionTick == currentTick)
        {
            return;
        }
        _lastWakeTransitionTick = currentTick;

        for (var i = 0; i < ActiveClusterCount; i++)
        {
            var chunkId = ActiveClusterIds[i];
            if (chunkId < SleepStates.Length && SleepStates[chunkId] == ClusterSleepState.WakePending)
            {
                SleepStates[chunkId] = ClusterSleepState.Active;
                SleepCounters[chunkId] = 0;
                SleepingClusterCount--;
            }
        }
    }

    /// <summary>
    /// Recompute the tight 2D AABB and category-mask union of a cluster by scanning its occupied slots. The spatial field is read
    /// via <see cref="SpatialMaintainer.ReadAndValidateBoundsFromPtr"/> which dispatches on the archetype's <see cref="SpatialFieldInfo.FieldType"/>.
    /// Degenerate entities (NaN/Inf bounds) are skipped. Issue #230.
    /// </summary>
    /// <remarks>
    /// Cost: one pass over <see cref="ArchetypeClusterInfo.ClusterSize"/> occupancy bits, ~50-100 ns per occupied entity on the L1-hot common path.
    /// Category mask is the OR of per-entity masks; in Phase 1 all entities use the default <c>uint.MaxValue</c> mask, so this collapses to <c>uint.MaxValue</c>.
    /// </remarks>
    internal ClusterSpatialAabb RecomputeClusterAabb(int clusterChunkId, ref ChunkAccessor<PersistentStore> accessor,
        double originX, double originY, double originZ)
        => RecomputeClusterAabb(clusterChunkId, ref accessor, originX, originY, originZ, out _);

    /// <remarks>
    /// <paramref name="originX"/>/<paramref name="originY"/>/<paramref name="originZ"/> are the world-space minimum corner of the cluster's cell: the result
    /// is <c>C15</c> CELL-RELATIVE, not world-space (#872 step 9). The caller supplies the origin rather than this method deriving it, because both hot
    /// callers already hold the cell — the rebuild map phase has just computed the cell COORDINATES and has no key yet, and the dirty pass has read the key
    /// out of <c>ClusterCellMap</c> two statements earlier. Deriving it here would repeat that work once per cluster per tick for nothing.
    /// </remarks>
    internal ClusterSpatialAabb RecomputeClusterAabb(int clusterChunkId, ref ChunkAccessor<PersistentStore> accessor,
        double originX, double originY, double originZ, out int slotsScanned)
    {
        ref readonly var ss = ref SpatialSlot;
        var clusterBase = accessor.GetChunkAddress(clusterChunkId);
        var occupancy = *(ulong*)clusterBase;
        slotsScanned = BitOperations.PopCount(occupancy);
        var componentOffset = Layout.ComponentOffset(ss.Slot);
        var componentStride = Layout.ComponentSize(ss.Slot);

        // -- Specialised f32 scans, and why they are not merely "the switch hoisted" ---------------------------------
        //
        // The generic loop below is the decoder for all eight SpatialFieldType shapes, and it pays for that generality once per ENTITY: an eight-case switch
        // on a loop-invariant field, a nine-branch degeneracy test, six float-to-double widenings into a stackalloc Span<double>, six loads back out, and then
        // six double subtractions each followed by a narrowing and a re-promotion to compare against. Measured in isolation (--profile-aabb), that is 27.4 ns
        // per occupied slot on a 7950X -- roughly 137 cycles to reduce six floats -- at a 1.02 GB/s effective read rate on a machine that will do fifty times
        // that. The scan is not memory-bound; it is bound on work it does not need to do.
        //
        // The transformation that matters is NOT the switch. It is that ToCellRelativeMin and ToCellRelativeMax are MONOTONE in the world value, so
        //
        //         min_i( ToCellRelativeMin(w_i, origin) )  ==  ToCellRelativeMin( min_i(w_i), origin )
        //
        // and likewise for max. The cell-relative conversion -- the double subtract, the narrowing, the directed-rounding guard -- therefore belongs OUTSIDE
        // the reduction, once per cluster per axis, instead of inside it once per entity per axis. At the ~33 occupied slots these clusters carry that is a
        // 33x reduction in conversions, and the result is bit-identical rather than approximated: min/max over f32 values is exact, and applying a monotone
        // map to the winner gives the same answer as applying it to every candidate and taking the winner. CA-01's outward-rounding guarantee is preserved
        // exactly, because the same guarded conversion still runs -- just once.
        //
        // What is left in the loop is six loads and six compares, which is what the operation actually is.
        //
        // The degeneracy test collapses with it. `IsNaN(a) || IsNaN(b) || a > b` is `!(a <= b)` under IEEE comparison -- NaN makes every ordered compare false
        // -- so nine branches become three, with identical semantics.
        var fieldType = ss.FieldInfo.FieldType;
        if (fieldType == SpatialFieldType.AABB3F)
        {
            return RecomputeAabb3F(clusterBase + componentOffset + ss.FieldOffset, componentStride, occupancy, originX, originY, originZ,
                ss.FieldInfo.Category);
        }

        if (fieldType == SpatialFieldType.AABB2F)
        {
            return RecomputeAabb2F(clusterBase + componentOffset + ss.FieldOffset, componentStride, occupancy, originX, originY, ss.FieldInfo.Category);
        }

        var aabb = ClusterSpatialAabb.Empty;
        // 6 doubles covers both 2D ([minX, minY, maxX, maxY]) and 3D ([minX, minY, minZ, maxX, maxY, maxZ]) layouts produced by
        // SpatialMaintainer.ReadAndValidateBoundsFromPtr. The tail slots cost nothing for 2D reads.
        Span<double> coords = stackalloc double[6];
        var is3D = ss.FieldInfo.FieldType.Is3D();

        var bits = occupancy;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;

            var fieldPtr = clusterBase + componentOffset + slot * componentStride + ss.FieldOffset;
            if (!SpatialMaintainer.ReadAndValidateBoundsFromPtr(fieldPtr, ss.FieldInfo, coords))
            {
                continue; // skip degenerate slot
            }

            // The narrowing to f32 happens HERE, through the directed-rounding helpers, and not as the bare `(float)` casts this used to do. coords is
            // double; subtracting the origin and rounding to nearest can land a min bound above — or a max bound below — the entity it must contain, which
            // is a CA-01 violation and therefore a silent SQ-01 false negative. See ClusterSpatialAabb.ToCellRelativeMin.
            if (is3D)
            {
                aabb.Union3F(
                    ClusterSpatialAabb.ToCellRelativeMin(coords[0], originX),
                    ClusterSpatialAabb.ToCellRelativeMin(coords[1], originY),
                    ClusterSpatialAabb.ToCellRelativeMin(coords[2], originZ),
                    ClusterSpatialAabb.ToCellRelativeMax(coords[3], originX),
                    ClusterSpatialAabb.ToCellRelativeMax(coords[4], originY),
                    ClusterSpatialAabb.ToCellRelativeMax(coords[5], originZ),
                    ss.FieldInfo.Category);
            }
            else
            {
                aabb.Union2F(
                    ClusterSpatialAabb.ToCellRelativeMin(coords[0], originX),
                    ClusterSpatialAabb.ToCellRelativeMin(coords[1], originY),
                    ClusterSpatialAabb.ToCellRelativeMax(coords[2], originX),
                    ClusterSpatialAabb.ToCellRelativeMax(coords[3], originY),
                    ss.FieldInfo.Category);
            }
        }

        return aabb;
    }

    /// <summary>
    /// The <see cref="SpatialFieldType.AABB3F"/> scan: reduce in world f32, convert to cell-relative once per axis. See the remarks at the dispatch site in
    /// <see cref="RecomputeClusterAabb(int, ref ChunkAccessor{PersistentStore}, double, double, double, out int)"/> for why that is exact.
    /// </summary>
    private static ClusterSpatialAabb RecomputeAabb3F(byte* firstField, int stride, ulong occupancy, double originX, double originY, double originZ,
        uint category)
    {
        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var minZ = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;
        var maxZ = float.NegativeInfinity;
        var any = false;

        var bits = occupancy;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;

            ref var b = ref *(AABB3F*)(firstField + slot * stride);

            if (!(b.MinX <= b.MaxX) || !(b.MinY <= b.MaxY) || !(b.MinZ <= b.MaxZ))
            {
                continue;
            }

            if (b.MinX < minX) { minX = b.MinX; }
            if (b.MinY < minY) { minY = b.MinY; }
            if (b.MinZ < minZ) { minZ = b.MinZ; }
            if (b.MaxX > maxX) { maxX = b.MaxX; }
            if (b.MaxY > maxY) { maxY = b.MaxY; }
            if (b.MaxZ > maxZ) { maxZ = b.MaxZ; }
            any = true;
        }

        if (!any)
        {
            // Every slot degenerate, or none occupied. The caller distinguishes this by testing MinX for positive infinity, exactly as with the generic path.
            return ClusterSpatialAabb.Empty;
        }

        var result = ClusterSpatialAabb.Empty;
        result.Union3F(
            ClusterSpatialAabb.ToCellRelativeMin(minX, originX),
            ClusterSpatialAabb.ToCellRelativeMin(minY, originY),
            ClusterSpatialAabb.ToCellRelativeMin(minZ, originZ),
            ClusterSpatialAabb.ToCellRelativeMax(maxX, originX),
            ClusterSpatialAabb.ToCellRelativeMax(maxY, originY),
            ClusterSpatialAabb.ToCellRelativeMax(maxZ, originZ),
            category);
        return result;
    }

    /// <summary>The <see cref="SpatialFieldType.AABB2F"/> scan. Same transformation as <see cref="RecomputeAabb3F"/>, two axes.</summary>
    private static ClusterSpatialAabb RecomputeAabb2F(byte* firstField, int stride, ulong occupancy, double originX, double originY, uint category)
    {
        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;
        var any = false;

        var bits = occupancy;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;

            ref var b = ref *(AABB2F*)(firstField + slot * stride);
            if (!(b.MinX <= b.MaxX) || !(b.MinY <= b.MaxY))
            {
                continue;
            }

            if (b.MinX < minX) { minX = b.MinX; }
            if (b.MinY < minY) { minY = b.MinY; }
            if (b.MaxX > maxX) { maxX = b.MaxX; }
            if (b.MaxY > maxY) { maxY = b.MaxY; }
            any = true;
        }

        if (!any)
        {
            return ClusterSpatialAabb.Empty;
        }

        var result = ClusterSpatialAabb.Empty;
        result.Union2F(
            ClusterSpatialAabb.ToCellRelativeMin(minX, originX),
            ClusterSpatialAabb.ToCellRelativeMin(minY, originY),
            ClusterSpatialAabb.ToCellRelativeMax(maxX, originX),
            ClusterSpatialAabb.ToCellRelativeMax(maxY, originY),
            category);
        return result;
    }

    /// <summary>
    /// Startup rebuild of per-cluster AABBs from entity positions. Mirrors <see cref="RebuildCellState"/>:
    /// both derive transient state from persistent cluster data on database reopen. Iterates all active clusters, recomputes each AABB, stores it
    /// in <see cref="ClusterAabbs"/>, and adds the cluster to its cell's <see cref="PerCellSpatialSlot.DynamicIndex"/> (lazy-allocated).
    /// Back-pointer recorded in <see cref="ClusterSpatialIndexSlot"/> so subsequent updates are O(1). Issue #230.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phase 1 supports Dynamic mode only. Static-mode archetypes are skipped — they keep using the existing per-archetype R-Tree path.
    /// </para>
    /// <para>
    /// Precondition: <see cref="RebuildCellState"/> has already run, so <see cref="ClusterCellMap"/> is populated and every active cluster's cell is known.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Takes the grid since #872 step 9: bounds are C15 cell-relative, so recomputing one needs its cell's origin. The legacy two-pass path this belongs to
    /// has no production caller left (the merged rebuild replaced it in step 2) but it is the differential oracle the merge is asserted against.
    /// </remarks>
    internal void RebuildClusterAabbs(SpatialGrid grid)
    {
        // #872 step 11. Every queued repair candidate carries a degradation measured against the bounds this method is about to recompute, and a cell key
        // that under the VDB grid is a POOL SLOT rather than a coordinate — so after a rebuild a retained candidate can rank a cell on evidence gathered
        // somewhere else entirely. Dropped rather than migrated: the next AABB pass re-nominates whatever still deserves it, at its real degradation.
        RepairQueue?.Clear();

        if (!SpatialSlot.HasSpatialIndex || ClusterSegment == null)
        {
            return;
        }
        // Issue #230 Phase 3 Option B: both Dynamic and Static cluster archetypes rebuild from data on reopen. AddClusterToPerCellIndex (called below) routes
        // to PerCellSpatialSlot.DynamicIndex / StaticIndex based on the archetype's SpatialMode, so the rebuild is mode-agnostic at this level.
        if (ActiveClusterCount == 0)
        {
            return;
        }

        EnsureClusterAabbsCapacity(PrimarySegmentCapacity);
        EnsureClusterSpatialIndexSlotCapacity(PrimarySegmentCapacity);
        EnsureClusterWriteBookkeepingCapacity(PrimarySegmentCapacity);

        // Reset the per-cell index before rebuilding so repeated calls to RebuildClusterAabbs (e.g. a startup reopen of a database that was reopened in the
        // same process) do not double-count clusters that already have entries in the index from a prior spawn/migration path.
        if (PerCellIndex != null)
        {
            // Before the clear, not after: clearing the slots drops the last reference to every promoted cell's tree, and those trees own chunks of a
            // TRANSIENT segment that nothing reclaims. See ReleaseAllCellTrees.
            // No epoch manager on this signature. It has no production caller (RebuildSpatialStateFromData replaced it at both), so rather than widen a
            // public signature for a path nothing takes, this passes null and the release is skipped — see ReleaseAllCellTrees.
            ReleaseAllCellTrees(null);
            Array.Clear(PerCellIndex);

            // The counter has to follow the clear. Leaving it stale makes RefitPromotedCellTrees and RebindCellTreeBackPointers walk an array of nulls
            // forever after a rebuild — harmless — but it also makes PromotedCellCount lie to the tests that use it as their non-vacuity guard, which is how
            // a promotion test passes without promoting anything.
            PromotedCellCount = 0;
        }
        Array.Fill(ClusterSpatialIndexSlot, -1);

        Interlocked.Increment(ref RebuildSegmentPassCount);
        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < ActiveClusterCount; i++)
            {
                var chunkId = ActiveClusterIds[i];

                // The cell is resolved FIRST now: a C15 cell-relative AABB cannot be computed without the cell's origin, so the order that used to be
                // "recompute, then find the cell" is inverted. A cluster with no cell keeps the Empty sentinel rather than an AABB measured from nowhere.
                if (ClusterCellMap == null || chunkId >= ClusterCellMap.Length)
                {
                    // Same reasoning as the cellKey < 0 branch below: no cell means no frame, and the slot must be cleared rather than left stale.
                    ClusterAabbs[chunkId] = ClusterSpatialAabb.Empty;
                    continue;
                }
                var cellKey = ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    // No cell, so no frame — but the slot must still be CLEARED rather than left holding whatever a previous life of this chunk id wrote.
                    // Reordering the cell lookup ahead of the recompute (C15 needs the origin first) made "continue" silently mean "keep the stale value";
                    // ClusterRebuildMergeTests.MergedRebuild_MatchesOracle_WhenAClusterIsEmpty caught it as a 50.0f where the merged path had +Infinity.
                    ClusterAabbs[chunkId] = ClusterSpatialAabb.Empty;
                    continue;
                }

                grid.CellOrigin(cellKey, out var originX, out var originY, out var originZ);
                var aabb = RecomputeClusterAabb(chunkId, ref clusterAccessor, originX, originY, originZ);
                ClusterAabbs[chunkId] = aabb;

                if (float.IsPositiveInfinity(aabb.MinX))
                {
                    continue; // empty — no valid entities
                }

                AddClusterToPerCellIndex(chunkId, cellKey, aabb);
            }
        }
        finally
        {
            clusterAccessor.Dispose();
        }
    }

    /// <summary>
    /// Tick-fence pass: re-tighten cluster AABBs and propagate to the per-cell index. Now driven by the write-time bookkeeping arrays
    /// (<see cref="ClusterProcessBitmap"/>, <see cref="ClusterShrinkPendingAxes"/>) populated by <c>ClusterRef.WriteSpatial</c>.
    /// <para>
    /// For each cluster with its process bit set:
    /// <list type="bullet">
    ///   <item>If <c>ShrinkPendingAxes != 0</c>: an entity at an axis extreme moved inward, so the stored AABB no longer fits — rescan this cluster's occupied
    ///         slots to recompute the tight AABB.</item>
    ///   <item>Otherwise the process bit was set by an inline AABB grow (already applied) or a migration flag — just propagate the (already-current)
    ///         <see cref="ClusterAabbs"/> entry to <c>PerCellIndex.UpdateAt</c>.</item>
    /// </list>
    /// </para>
    /// <para>
    /// All three bookkeeping arrays (<see cref="ClusterProcessBitmap"/>, <see cref="ClusterMigrationPendingSlots"/>, <see cref="ClusterShrinkPendingAxes"/>)
    /// are cleared at the end of the pass. The migration drain is expected to have already happened in <c>DatabaseEngine.DetectClusterMigrations</c> (which
    /// runs immediately before this method).
    /// </para>
    /// <para>
    /// The <paramref name="dirtyBits"/> parameter is retained for API stability; this method no longer reads it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <paramref name="grid"/> lost its <c>null</c> default in #872 step 9: C15 cell-relative bounds cannot be computed without a cell origin, so every
    /// recompute now dereferences the grid unconditionally. Leaving the default in place would have kept a null-argument overload compiling at call sites
    /// that no longer have a valid meaning.
    /// </remarks>
    internal void RecomputeDirtyClusterAabbs(long[] dirtyBits, ref ChunkAccessor<PersistentStore> accessor, SpatialGrid grid)
    {
        // Still unread, and now deliberately so rather than by omission. The gate this parameter used to drive lives in RecomputeDirtyClusterAabbsSlice
        // (ClusterNeedsAabbRecompute), which reads FenceDirtyBits off the state — the same array every caller passes here — because the PARALLEL path
        // dispatches the slice directly and never comes through this wrapper. Threading it as an argument as well would give the two paths two sources of
        // truth for the same question. Kept in the signature because every call site already holds it and the name documents what the pass is gated on.
        _ = dirtyBits;

        if (!SpatialSlot.HasSpatialIndex)
        {
            return;
        }

        if (SpatialSlot.FieldInfo.Mode != SpatialMode.Dynamic)
        {
            return;
        }

        if (ClusterSpatialIndexSlot == null || ClusterAabbs == null)
        {
            return;
        }

        if (PerCellIndex == null || ClusterCellMap == null)
        {
            return;
        }

        // Whole-archetype convenience wrapper: serial WriteTickFence path. The parallel path dispatches RecomputeDirtyClusterAabbsSlice across workers directly
        // and then runs ClearAabbRefreshBookkeeping in Finalize.
        var refreshSpan = TyphonEvent.BeginSpatialClusterAabbRefresh((ushort)ArchetypeId, ActiveClusterCount);
        try
        {
            var totalWork = (SpatialBarrierOnly && ClusterProcessBitmap != null) ? ClusterProcessBitmap.Length : ActiveClusterCount;
            if (totalWork > 0)
            {
                var outlierBuffer = new List<MigrationRequest>(0);
                // Appended to DIRECTLY rather than through EnqueueRepairNominationsBulk. This wrapper is the serial path — it is the single writer, so the
                // lock the bulk enqueue takes would be uncontended overhead, and the parallel path's reason for a worker-local buffer (many slices, one
                // list) does not exist here.
                //
                // Null when repair is switched off, matching the parallel call site EXACTLY. Without the gate the two paths disagree: the planner discards
                // nominations unread at a zero budget, so the serial fence would nominate every degraded cluster every tick and then rent a ChunkAccessor,
                // copy the list into scratch and clear it, all to return at the budget check. That is the configuration the four step-10 fixtures now run in,
                // so the cost would have landed on exactly the measurements it must not perturb.
                var repairNominationBuffer = grid != null && grid.Config.ReclusterBudgetMs > 0f ? RepairNominations : null;
                // No deferral buffer on the serial path: it is already the single writer, so a promoted cell's tree can be written directly and the drain
                // below has nothing to do. Passing null is what selects that — see the divert in the slice.
                RecomputeDirtyClusterAabbsSlice(0, totalWork, ref accessor, grid, null, outlierBuffer, repairNominationBuffer, out var aabbsChanged,
                    out var slotsScanned, out var outlierGuardFires, out var clustersScanned, out var driftersDetected, out var driftAbsorbed,
                    out var driftersUnplaced, out var driftGatedClusters, out var driftSuppressedByDensity, out var driftersUnplacedNoCandidate,
                    out var driftersSpilled, out var tightness);
                EnqueueMigrationsBulk(outlierBuffer);
                FoldTightnessSample(in tightness);
                Interlocked.Add(ref LastTickClustersScanned, clustersScanned);
                Interlocked.Add(ref LastTickSlotsScanned, slotsScanned);
                Interlocked.Add(ref LastTickDriftersDetected, driftersDetected);
                Interlocked.Add(ref LastTickDriftAbsorbedCount, driftAbsorbed);
                Interlocked.Add(ref LastTickDriftersUnplaced, driftersUnplaced);
                Interlocked.Add(ref LastTickDriftGatedClusters, driftGatedClusters);
                Interlocked.Add(ref LastTickDriftSuppressedByDensity, driftSuppressedByDensity);
                Interlocked.Add(ref LastTickDriftersUnplacedNoCandidate, driftersUnplacedNoCandidate);
                Interlocked.Add(ref LastTickDriftersSpilled, driftersSpilled);
                refreshSpan.AabbsChanged = aabbsChanged;
                refreshSpan.SlotsScanned = slotsScanned;
                refreshSpan.OutlierGuardFires = outlierGuardFires;
            }
            ClearAabbRefreshBookkeeping();
        }
        finally
        {
            refreshSpan.Dispose();
        }
    }

    /// <summary>
    /// Apply the AABB recompute pass to a contiguous slice of this archetype's clusters. Safe to call concurrently across DISJOINT slices of the SAME archetype
    /// (used by the parallel-fence AabbRefresh phase).
    /// <para>
    /// Slicing axis depends on iteration mode (captured from <see cref="SpatialBarrierOnly"/>):
    /// <list type="bullet">
    ///   <item><b>BarrierOnly</b>: slice <see cref="ClusterProcessBitmap"/> by word range. <paramref name="sliceStart"/>=startWord,
    ///         <paramref name="sliceCount"/>=wordCount. Each word's bits are disjoint cluster chunk-IDs so no two slices touch the same cluster.</item>
    ///   <item><b>Legacy</b>: slice <see cref="ActiveClusterIds"/> by index range. <paramref name="sliceStart"/>=activeIdx,
    ///         <paramref name="sliceCount"/>=count. Each slice owns a disjoint range of active-list indices.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Thread-safety</b>: writes only to per-cluster slots (<see cref="ClusterAabbs"/>[chunkId]) and per-cell index slots
    /// (<c>PerCellIndex[cellKey].DynamicIndex.UpdateAt(indexSlot, ...)</c>). Different clusters always have different <c>indexSlot</c>s even within the same
    /// cell, so SoA writes don't collide. The rare <see cref="FlagOutliersForMigration"/> path (extent-guard fire) serializes
    /// <see cref="EnqueueMigration(int, int, int)"/> internally via <c>_finalizeLock</c>.
    /// </para>
    /// <para>
    /// <b>PRECONDITION — caller must hold the tick fence barrier (CA-01, issue #573).</b> The AABB write below is a <i>blind store</i>
    /// (<c>stored = fresh</c>), not a union against the previous value. That is correct only because the current fence barrier guarantees no concurrent
    /// <c>WriteSpatial</c> can be flagging new geometry into a cell while this runs. Under any concurrent or pipelined fence scheme
    /// (<c>claude/design/Runtime/09-tick-pipelining.md</c>, <c>10-fence-parallelisation.md</c>) a racing <c>WriteSpatial</c> is silently dropped and the
    /// resulting AABB is <b>too tight</b> — spatial queries then stop returning entities that are genuinely inside the query region, with no error and no
    /// crash. Whoever relaxes this barrier must first convert the store to a grow-merge (union with <c>stored</c>, handling the shrink path explicitly).
    /// </para>
    /// </summary>
    internal void RecomputeDirtyClusterAabbsSlice(int sliceStart, int sliceCount, ref ChunkAccessor<PersistentStore> accessor, SpatialGrid grid,
        List<PromotedAabbApply> promotedApplyBuffer, List<MigrationRequest> outlierBuffer, List<RepairNomination> repairNominationBuffer, out int aabbsChanged,
        out int slotsScanned, out int outlierGuardFires, out int clustersScanned, out int driftersDetected, out int driftAbsorbed, out int driftersUnplaced,
        out int driftGatedClusters, out int driftSuppressedByDensity, out int driftersUnplacedNoCandidate, out int driftersSpilled,
        out ClusterTightnessSample tightness)
    {
        aabbsChanged = 0;
        slotsScanned = 0;
        outlierGuardFires = 0;
        clustersScanned = 0;
        driftersDetected = 0;
        driftersUnplaced = 0;
        driftAbsorbed = 0;
        driftGatedClusters = 0;
        driftSuppressedByDensity = 0;
        driftersUnplacedNoCandidate = 0;
        driftersSpilled = 0;
        tightness = default;

        if (!SpatialSlot.HasSpatialIndex)
        {
            return;
        }

        if (SpatialSlot.FieldInfo.Mode != SpatialMode.Dynamic)
        {
            return;
        }

        if (ClusterSpatialIndexSlot == null || ClusterAabbs == null)
        {
            return;
        }

        if (PerCellIndex == null || ClusterCellMap == null)
        {
            return;
        }

        if (sliceCount <= 0)
        {
            return;
        }

        var maxExtent = 0f;
        var cellSize = 0f;
        var inverseCellSize = 0f;
        var outlierGuardActive = grid != null && (cellSize = (float)grid.Config.CellSize) > 0f;
        var driftTargetExtent = 0f;
        // #872 step 12 (P7). A THIRD threshold, deliberately not one of the two above. The design proposes reusing the outlier guard's cellSize x 1.2, but
        // that check exists to catch a cluster whose bound has escaped its own cell — which only happens when it holds entities that should have migrated
        // out. A cluster whose entities all belong to its cell tops out near 1.05 x cellSize (the hysteresis margin), so 1.2 is unreachable for the
        // intra-cell degradation repair exists to fix, and AC-12.1's own "AABBs at ~90 % of the cell" sits below it. See ClusterRepairExtentRatio.
        var repairExtent = 0f;
        if (outlierGuardActive)
        {
            maxExtent = cellSize * 1.2f;
            driftTargetExtent = cellSize * grid.Config.ClusterTargetExtentRatio;
            repairExtent = repairNominationBuffer != null ? cellSize * grid.Config.ClusterRepairExtentRatio : 0f;
            // Hoisted for the nomination's degradation ratio (#872 step 11). One reciprocal per slice against one division per nominated cluster, on a
            // path whose whole justification is that the per-cluster test is three compares.
            inverseCellSize = cellSize > 0f ? 1f / cellSize : 0f;
        }

        // Step 14 (D1). The two extents above are the FLOORS; the operative target is a function of the cell's population, resolved once per cell
        // change rather than per cluster — clusters of one cell are adjacent in both branches' iteration order often enough that the cache hits far more
        // than it misses, and a miss is one CellState load, one root and a handful of multiplies. `flat` is a property of the field, not of the cluster:
        // every cluster of this archetype packs in the same number of dimensions.
        var targets = new CellTargetResolver(grid, cellSize, driftTargetExtent, repairExtent, BitOperations.PopCount(Layout.FullMask),
            SpatialSlot.HasSpatialIndex && SpatialSlot.FieldInfo.FieldType is SpatialFieldType.AABB2F or SpatialFieldType.BSphere2F or SpatialFieldType.AABB2D
                or SpatialFieldType.BSphere2D,
            DriftTargetBoost);

        // Hoisted out of the per-cluster loop, which is the whole point of taking it as a parameter (D1). 64 slots is the cluster capacity ceiling and
        // three axes are cached, so this is 768 bytes on the slice worker's stack, reused for every cluster the slice touches. Allocating it per cluster
        // would put a stackalloc inside a loop.
        Span<double> centreScratch = stackalloc double[3 * MaxSlotsPerCluster];

        // Per-WORKER, not per-slice — see _candidateScratch. Reused across ticks, so the steady state allocates nothing.
        var candidateScratch = CandidateScratch ??= new List<RelocationCandidate>(64);

        // Hoisted for the same reason as the extents above: one division per SLICE against one per cluster. Zero means "no limit" — see
        // ComputeDriftNominationCap, which is also where the 43:1 measurement that motivates it is recorded.
        var driftNominationCap = grid != null ? ComputeDriftNominationCap(in grid.Config) : 0;

        if (SpatialBarrierOnly && ClusterProcessBitmap != null)
        {
            var wordEnd = Math.Min(sliceStart + sliceCount, ClusterProcessBitmap.Length);
            for (var wordIdx = sliceStart; wordIdx < wordEnd; wordIdx++)
            {
                var word = ClusterProcessBitmap[wordIdx];
                if (word == 0)
                {
                    continue;
                }

                while (word != 0)
                {
                    var chunkId = (wordIdx << 6) + BitOperations.TrailingZeroCount((ulong)word);
                    word &= word - 1;

                    if (chunkId >= ClusterSpatialIndexSlot.Length)
                    {
                        continue;
                    }

                    var indexSlot = ClusterSpatialIndexSlot[chunkId];
                    if (indexSlot < 0)
                    {
                        continue;
                    }

                    if (chunkId >= ClusterCellMap.Length)
                    {
                        continue;
                    }

                    var cellKey = ClusterCellMap[chunkId];
                    if (cellKey < 0)
                    {
                        continue;
                    }

                    var slot = PerCellIndex[cellKey];
                    // DynamicClusterCount, not DynamicIndex != null: a promoted cell has no linear index at all, so testing the field skips the recompute
                    // for exactly the cells the tree was introduced to serve. Silent — the fence simply stops updating those clusters' bounds, and CA-01
                    // decays from there.
                    if (slot == null || slot.DynamicClusterCount == 0)
                    {
                        continue;
                    }

                    var shrinkMask = ClusterShrinkPendingAxes != null && chunkId < ClusterShrinkPendingAxes.Length
                        ? ClusterShrinkPendingAxes[chunkId]
                        : (byte)0;

                    ref var stored = ref ClusterAabbs[chunkId];
                    ClusterSpatialAabb fresh;
                    if (shrinkMask != 0)
                    {
                        grid.CellOrigin(cellKey, out var shrinkOriginX, out var shrinkOriginY, out var shrinkOriginZ);
                        fresh = RecomputeClusterAabb(chunkId, ref accessor, shrinkOriginX, shrinkOriginY, shrinkOriginZ, out var clusterSlots);
                        slotsScanned += clusterSlots;
                        if (float.IsPositiveInfinity(fresh.MinX))
                        {
                            continue;
                        }
                    }
                    else
                    {
                        fresh = stored;
                        if (float.IsPositiveInfinity(fresh.MinX))
                        {
                            continue;
                        }
                    }

                    // The per-cell index is NOT a copy of ClusterAabbs, and deciding whether to write it by comparing ClusterAabbs against ITSELF was
                    // an SQ-01 false negative. ClusterAabbs has TWO writers — the write-time CAS in ClusterRef.MaybeGrowAndFlagShrink, whose own summary says
                    // a grow means "the cluster needs a fence-time PerCellIndex.UpdateAt with the fresh AABB" — while the index has ONE, this loop. When the
                    // CAS has already applied the grow, `stored` and `fresh` agree and the old short-circuit skipped the index, leaving it holding the
                    // previous tick's SMALLER box. A query whose edge fell in the gap pruned the cluster and lost every entity in it, with nothing raised.
                    //
                    // On this branch it was not even a race: `fresh` is ASSIGNED from `stored` when no shrink is pending, so the comparison was a tautology
                    // and a grow-only tick could NEVER update the index at all. Both demos run barrier-only, so that was the shipped path.
                    //
                    // The bit is the signal, and it costs nothing here because it is what we are already iterating. A cluster reaches this loop only because
                    // WriteSpatial set its ClusterProcessBitmap bit, and it sets that bit on exactly `aabbChanged || migrationFlagged` — either way the index
                    // wants the current box. ClearAabbRefreshBookkeeping zeroes the bitmap once per tick, so a set bit always refers to this tick.
                    var boundsMoved = stored.MinX != fresh.MinX || stored.MinY != fresh.MinY || stored.MinZ != fresh.MinZ ||
                                      stored.MaxX != fresh.MaxX || stored.MaxY != fresh.MaxY || stored.MaxZ != fresh.MaxZ;

                    fresh.CategoryMask = ReadStoredCategoryMask(slot, chunkId, indexSlot);
                    NoteClusterOverhang(in fresh, cellSize);
                    if (boundsMoved)
                    {
                        // CA-01 PRECONDITION (see the method's remarks): `stored = fresh` is a BLIND STORE, not a grow-merge. Sound only under the tick fence
                        // barrier, which guarantees no concurrent WriteSpatial is flagging new geometry into this cell. Relaxing that barrier without
                        // converting it to a union against `stored` silently drops the racing write and leaves the AABB too tight (#573).
                        NoteAabbChange(in stored, in fresh);
                        stored = fresh;
                        aabbsChanged++;
                    }

                    ApplyOrDeferClusterUpdate(chunkId, cellKey, in fresh, slot, promotedApplyBuffer);
                    TyphonEvent.EmitSpatialCellIndexUpdate(cellKey, indexSlot);

                    // The Z term matters because FlagOutliersForMigration tests all three axes: without it a cluster that drifts purely on Z never
                    // reaches the check that would notice, and the Z half of that method is dead in exactly the case it was written for.
                    // LIVE since #914 — it was written speculatively while WriteSpatial handled AABB2F only, so no cluster AABB could grow on Z at write
                    // time and a 2D union left MinZ/MaxZ at the ±Infinity sentinel whose difference is -Infinity. The 3D write tiers make it reachable,
                    // and OutlierGuardZAxisTests covers it. A 2D archetype still produces -Infinity here, which fails the extent test as it always did.
                    // ── D1: ONE gather, then two cheap consumers ──────────────────────────────────────────────────
                    //
                    // Gated so a healthy cluster still costs nothing per entity. The guard's threshold is cellSize x 1.2 and
                    // the drift gate is cellSize x ClusterTargetExtentRatio (0.25 by default), so the drift gate is the wider
                    // of the two and a cluster that trips the guard has always tripped it as well — but both are tested
                    // rather than assuming the ratio stays below 1.2, because it is a tunable and step 11 will tune it.
                    //
                    // Both extents come from `fresh`, which is already in registers, so the decision to walk is three float
                    // compares. Only a cluster that has actually spread pays for the walk, which is what makes §5.2's
                    // "you can afford to LOOK at everything" true of clusters rather than only of entities.
                    var guardFires = outlierGuardActive &&
                                     ((fresh.MaxX - fresh.MinX) > maxExtent || (fresh.MaxY - fresh.MinY) > maxExtent || (fresh.MaxZ - fresh.MinZ) > maxExtent);

                    // Step 14: the gates are the CELL's, resolved from its population (D1), and a cluster repair will re-sort is not one relocation is
                    // asked to nudge (D2) — greedy least-enlargement has no gradient once every box in the cell is wide, and was measured making tightness
                    // worse the more budget it had. Constant mode (slack 0) keeps the pre-step-14 behaviour, which scanned repair-gated clusters too.
                    // Nomination goes to the CELL, not the cluster: the repair unit is a cell's worst clusters, and which
                    // those are is a ranking the planner performs over the whole cell rather than over whichever clusters this slice happened to hold.
                    // The extent is the MAX of the three axes rather than "any axis over", because the ranking needs to know HOW degraded the cell is;
                    // the two agree on every finite input and differ only on NaN, which bounds validation makes unreachable (see MaxAxisExtent).
                    targets.Resolve(cellKey);
                    var maxAxisExtent = MaxAxisExtent(in fresh);
                    var repairGated = targets.RepairExtent > 0f && maxAxisExtent > targets.RepairExtent;
                    var driftGated = (!repairGated || targets.ConstantMode) && targets.DriftExtent > 0f && maxAxisExtent > targets.DriftExtent;

                    clustersScanned++;
                    tightness.Note(outlierGuardActive, maxAxisExtent, inverseCellSize, targets.PackingBound);
                    if (repairGated)
                    {
                        repairNominationBuffer.Add(new RepairNomination(cellKey, maxAxisExtent * inverseCellSize));
                    }

                    if (driftGated)
                    {
                        driftGatedClusters++;
                    }
                    else if (!repairGated && driftTargetExtent > 0f && maxAxisExtent > driftTargetExtent)
                    {
                        driftSuppressedByDensity++;
                    }

                    if (!guardFires && !driftGated)
                    {
                        continue;
                    }

                    var centres = GatherClusterCentres(chunkId, ref accessor, centreScratch);
                    ulong guardClaimed = 0;
                    if (guardFires)
                    {
                        outlierGuardFires++;
                        guardClaimed = FlagOutliersForMigration(chunkId, cellKey, grid, in centres, outlierBuffer);
                    }

                    if (driftGated && !DriftNominationBudgetSpent(driftNominationCap))
                    {
                        var beforeDrift = outlierBuffer.Count;
                        DetectDriftersInCluster(chunkId, cellKey, in fresh, grid, ref accessor, in centres, guardClaimed, outlierBuffer,
                            candidateScratch, targets.DriftExtent, ref driftersDetected, ref driftAbsorbed, ref driftersUnplaced,
                            ref driftersUnplacedNoCandidate, ref driftersSpilled);
                        NoteDriftNominations(outlierBuffer.Count - beforeDrift);
                    }
                }
            }
        }
        else
        {
            var activeEnd = Math.Min(sliceStart + sliceCount, ActiveClusterCount);
            for (var activeIdx = sliceStart; activeIdx < activeEnd; activeIdx++)
            {
                var chunkId = ActiveClusterIds[activeIdx];

                if (chunkId >= ClusterSpatialIndexSlot.Length)
                {
                    continue;
                }

                var indexSlot = ClusterSpatialIndexSlot[chunkId];
                if (indexSlot < 0)
                {
                    continue;
                }

                if (chunkId >= ClusterCellMap.Length)
                {
                    continue;
                }

                var cellKey = ClusterCellMap[chunkId];
                if (cellKey < 0)
                {
                    continue;
                }

                var slot = PerCellIndex[cellKey];
                // See the sliced path above: a promoted cell has no DynamicIndex, and testing the field would skip it.
                if (slot == null || slot.DynamicClusterCount == 0)
                {
                    continue;
                }

                // ── Recompute only what changed ────────────────────────────────────────────────────────────────────
                //
                // This branch used to recompute UNCONDITIONALLY, and that was a regression, not a design. Before the
                // fence system landed (#350) this method walked `dirtyBits` and skipped `dirtyBits[chunkId] == 0`; the
                // rewrite replaced the walk with `ActiveClusterCount` and left the parameter as `_ = dirtyBits;`. The two
                // sets coincide on a fully-moving world — 2 007 dirty of 2 020 active at 100 % moving — so every campaign
                // run at the stress point measured the bug as free. At 1 % moving it is 531 dirty of 1 969 active: the
                // fence walked 46 700 slots per tick to prove nothing had changed in them.
                //
                // Skipping is safe because the stored bound is EXACT for a cluster nothing touched: the recompute is never
                // skipped when a signal fires, so ClusterAabbs holds last tick's exact value, no entity in the cluster was
                // written, and the recompute would return a bound identical to it. `boundsMoved` then goes false and the
                // loop `continue`s at the same place it does today — the walk was pure cost.
                //
                // THREE signals, and the third is the one that is easy to miss:
                //   • FenceDirtyBits[chunkId]        — any component write to an occupied slot (SetDirty), plus the src/dst
                //                                      deltas ExecuteMigrations flushed before this phase ran.
                //   • ClusterShrinkPendingAxes       — a slot was VACATED. Migration sources flag it (ClusterMigration.cs);
                //                                      ReleaseSlot now flags it too, because a destroy sets no dirty bit
                //                                      (the bit is masked away by occupancy in Prep step 2) and would
                //                                      otherwise leave a dead entity's position inside the bound forever.
                //   • the process bit               — WriteSpatial writers, which deliberately do not SetDirty.
                // Out of range or a null bitmap means "no information", which recomputes. Conservative by construction.
                ClusterSpatialAabb fresh;
                if (ClusterNeedsAabbRecompute(chunkId))
                {
                    grid.CellOrigin(cellKey, out var dirtyOriginX, out var dirtyOriginY, out var dirtyOriginZ);
                    fresh = RecomputeClusterAabb(chunkId, ref accessor, dirtyOriginX, dirtyOriginY, dirtyOriginZ, out var clusterSlots);
                    slotsScanned += clusterSlots;
                }
                else
                {
                    fresh = ClusterAabbs[chunkId];
                }

                if (float.IsPositiveInfinity(fresh.MinX))
                {
                    continue;
                }

                ref var stored = ref ClusterAabbs[chunkId];
                var boundsMoved = stored.MinX != fresh.MinX || stored.MinY != fresh.MinY || stored.MinZ != fresh.MinZ ||
                                  stored.MaxX != fresh.MaxX || stored.MaxY != fresh.MaxY || stored.MaxZ != fresh.MaxZ;

                // See the bitmap branch above for why equality is the wrong question. Here `fresh` IS recomputed from the entities, so the comparison is not
                // a tautology — but it still answers "did the fence learn anything new", not "is the index current". The write-time CAS can have moved
                // ClusterAabbs to exactly what the recompute produces, and then the fence has nothing to store while the index is still a tick behind. The
                // process bit separates the two: WriteSpatial sets it, and the OpenMut / GetSpan writers that leave ClusterAabbs for the fence to recompute
                // do not — which is precisely the case where equality really does mean nothing happened.
                // This gate carries TWO meanings, and they cannot currently be separated. Read it before changing it.
                //
                // Meaning one, which is correct: a cluster nobody touched needs neither an index write nor a scan, and
                // skipping it is what makes a settled world cost nothing (AC-10.8). This loop walks EVERY active cluster —
                // there is no dirty-bit filter on this branch — so without the skip a quiet tick would scan the whole
                // archetype.
                //
                // Meaning two, which is a known false negative: an entity moved through OpenMut or GetSpan (writers that set
                // no process bit) to a new position INSIDE its cluster's existing bound also leaves `boundsMoved` false, and
                // is therefore never examined by drift detection. It is a genuine AC-10.1 miss, and a galling one — an entity
                // drifting inside a bound that is already too large is precisely what step 10 exists to repair.
                //
                // Separating the two needs a per-cluster "was written this tick" signal that this branch does not carry;
                // `boundsMoved` is a proxy for it and the process bit covers only the WriteSpatial writers. Lifting the skip
                // so detection always runs was tried and reddens AMotionlessTick_DetectsNothing, because it converts the
                // quiet-tick guarantee into a full scan. Recorded as a scoped exception on CR-03 rather than papered over.
                //
                // Exposure is narrow: an archetype whose spatial writes all go through WriteSpatial sets the process bit and
                // is unaffected, and TYPHON009 flags the mutation sites that do not.
                // ── #872 step 12: nominate BEFORE the skip, not after ──────────────────────────────────────────────────
                //
                // Repair nomination is the one consumer on this branch that must see a cluster NOBODY WROTE. The design's
                // own trigger list for the repair path is "a cell that degraded badly while throttled", "a teleport dumping
                // a fleet into a cell", and "initial load / rebuild" — and the last of those is a cell that is loaded wrong
                // and then never touched again. Below the skip it is invisible, so repair would fire only on cells that are
                // ALSO moving, which is the population the delta path already handles. Measured before the move: a cell
                // spawned in scattered order sat at a mean extent of 86.4 of 100 for six consecutive ticks with
                // clustersScanned = 0 and not one nomination.
                //
                // It is free here in a way it would not be on the other branch: this loop has already run
                // RecomputeClusterAabb over every occupied slot of every active cluster, unconditionally, so `fresh` is in
                // registers and the nomination is three float compares against a walk that has already been paid for.
                //
                // KNOWN GAP, barrier-only mode. The other branch iterates ClusterProcessBitmap, which by construction
                // holds only clusters written this tick, so there is no equivalent place to put this and a still cell in
                // that mode is never nominated. Closing it needs a signal that ranks CELLS rather than reacting to cluster
                // writes — which is exactly step 11's priority queue ("candidate cells ... re-ranked lazily", §5.6).
                targets.Resolve(cellKey);
                if (targets.RepairExtent > 0f)
                {
                    var repairMaxExtent = MaxAxisExtent(in fresh);
                    if (repairMaxExtent > targets.RepairExtent)
                    {
                        repairNominationBuffer.Add(new RepairNomination(cellKey, repairMaxExtent * inverseCellSize));
                    }
                }

                if (!boundsMoved && !IsClusterProcessBitSet(chunkId))
                {
                    continue;
                }

                fresh.CategoryMask = ReadStoredCategoryMask(slot, chunkId, indexSlot);
                NoteClusterOverhang(in fresh, cellSize);
                if (boundsMoved)
                {
                    NoteAabbChange(in stored, in fresh);
                    stored = fresh;
                }

                // The ActiveClusterIds branch is sliced by active-list index and is just as parallel as the bitmap branch above, so it defers identically.
                // Leaving it undiverted was the shape of the original defect: one branch protected, the other silently writing a shared tree from a worker.
                //
                // Gated rather than unconditional for the CA-02 reason: when neither the bound moved nor a write-time CAS
                // touched this cluster, the index already holds the current box and rewriting it is pure cost.
                ApplyOrDeferClusterUpdate(chunkId, cellKey, in fresh, slot, promotedApplyBuffer);
                if (boundsMoved)
                {
                    aabbsChanged++;
                }

                TyphonEvent.EmitSpatialCellIndexUpdate(cellKey, indexSlot);

                // The Z term matters because FlagOutliersForMigration tests all three axes: without it a cluster that drifts purely on Z never
                // reaches the check that would notice, and the Z half of that method is dead in exactly the case it was written for.
                // LIVE since #914 — see the identical note on the parallel arm above.

                // See the bitmap branch above — one gather, same gating, same reason. The repair nomination for this branch sits before the
                // process-bit skip above, so `targets` has already been resolved for this cell by the time this runs.
                var guardFires = outlierGuardActive && ((fresh.MaxX - fresh.MinX) > maxExtent
                                                        || (fresh.MaxY - fresh.MinY) > maxExtent
                                                        || (fresh.MaxZ - fresh.MinZ) > maxExtent);
                var activeMaxAxisExtent = MaxAxisExtent(in fresh);
                var activeRepairGated = targets.RepairExtent > 0f && activeMaxAxisExtent > targets.RepairExtent;
                var driftGated = (!activeRepairGated || targets.ConstantMode) && targets.DriftExtent > 0f && activeMaxAxisExtent > targets.DriftExtent;
                clustersScanned++;
                tightness.Note(outlierGuardActive, activeMaxAxisExtent, inverseCellSize, targets.PackingBound);
                if (driftGated)
                {
                    driftGatedClusters++;
                }
                else if (!activeRepairGated && driftTargetExtent > 0f && activeMaxAxisExtent > driftTargetExtent)
                {
                    driftSuppressedByDensity++;
                }

                if (!guardFires && !driftGated)
                {
                    continue;
                }

                var centres = GatherClusterCentres(chunkId, ref accessor, centreScratch);
                ulong guardClaimed = 0;
                if (guardFires)
                {
                    outlierGuardFires++;
                    guardClaimed = FlagOutliersForMigration(chunkId, cellKey, grid, in centres, outlierBuffer);
                }

                if (driftGated && !DriftNominationBudgetSpent(driftNominationCap))
                {
                    var beforeDrift = outlierBuffer.Count;
                    DetectDriftersInCluster(chunkId, cellKey, in fresh, grid, ref accessor, in centres, guardClaimed, outlierBuffer,
                        candidateScratch, targets.DriftExtent, ref driftersDetected, ref driftAbsorbed, ref driftersUnplaced,
                        ref driftersUnplacedNoCandidate, ref driftersSpilled);
                    NoteDriftNominations(outlierBuffer.Count - beforeDrift);
                }
            }
        }
    }

    /// <summary>
    /// Count the clusters actually represented by an AABB-refresh slice. Used for the per-slice telemetry span (<c>ClusterScanned</c> field). Legacy mode:
    /// <paramref name="sliceCount"/> directly. Barrier mode: popcount of the slice's bitmap words.
    /// </summary>
    internal int CountClustersInAabbSlice(int sliceStart, int sliceCount)
    {
        if (sliceCount <= 0)
        {
            return 0;
        }

        if (SpatialBarrierOnly && ClusterProcessBitmap != null)
        {
            var end = Math.Min(sliceStart + sliceCount, ClusterProcessBitmap.Length);
            var total = 0;
            for (var w = sliceStart; w < end; w++)
            {
                total += BitOperations.PopCount((ulong)ClusterProcessBitmap[w]);
            }
            return total;
        }
        return Math.Min(sliceCount, Math.Max(0, ActiveClusterCount - sliceStart));
    }

    /// <summary>
    /// The largest distance by which any of this archetype's cluster boxes reaches OUTSIDE its own cell, in world units. Zero until a cluster proves otherwise.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a cluster can leave its cell at all.</b> Cell membership is decided by an entity's CENTRE — <c>SpatialGrid.ReadSpatialCenter3D</c>, and the
    /// migration check in <c>DetectClusterMigrations</c> uses the same point — so an entity with extent protrudes past its cell by up to its own
    /// half-extent, and the cluster box that unions it protrudes with it. <c>C13</c> makes a cluster belong to exactly one cell; it does not make its geometry
    /// fit inside one.</para>
    /// <para><b>What breaks without it.</b> Every grid search that argues "rings 0..R cover a radius of R cells, so nothing outside can be nearer" is wrong by
    /// exactly this much. <see cref="CoveredRadiusSq"/> made that argument and dropped true nearest neighbours whenever entities had extent — invisible to a
    /// test that spawns points, which is what the first kNN fixture did.</para>
    /// <para><b>Cell-relative bounds make it free to compute.</b> C15 stores <see cref="ClusterAabbs"/> as offsets from the cell origin, so the overhang is
    /// <c>max(-Min, Max - cellSize)</c> per axis with no origin lookup and no second pass.</para>
    /// <para><b>It only ever rises.</b> Too large merely widens a search; too small loses results — so the asymmetry is deliberate and it is never lowered.
    /// The consequence to know: an entity whose overhang exceeds anything seen before becomes visible to kNN at the fence following its write, not at the
    /// write itself. Entity sizes are effectively static in the workloads this serves, so that window is "a larger-than-ever entity was just spawned", not
    /// steady-state motion.</para>
    /// </remarks>
    internal float MaxClusterOverhang;

    /// <summary>Raise <see cref="MaxClusterOverhang"/> to cover one cluster box, given in that cluster's own cell frame.</summary>
    /// <remarks>
    /// CAS rather than a plain store because the parallel AabbRefresh slices all reach it; the loop is a max, so a lost race just retries. The common case is
    /// the first compare failing, which is a load and a branch.
    /// </remarks>
    internal void NoteClusterOverhang(in ClusterSpatialAabb aabb, float cellSize)
    {
        if (float.IsPositiveInfinity(aabb.MinX))
        {
            return;   // an empty box (a fresh cluster's) has no extent to note
        }

        if (!(cellSize > 0f))
        {
            return;
        }

        var over = MathF.Max(-aabb.MinX, aabb.MaxX - cellSize);
        over = MathF.Max(over, MathF.Max(-aabb.MinY, aabb.MaxY - cellSize));

        // A 2D archetype leaves Z at the +/-Infinity sentinel; feeding that in would poison the max with an infinity that widens every search forever.
        if (!float.IsPositiveInfinity(aabb.MinZ) && !float.IsNegativeInfinity(aabb.MaxZ))
        {
            over = MathF.Max(over, MathF.Max(-aabb.MinZ, aabb.MaxZ - cellSize));
        }

        if (!float.IsFinite(over) || over <= 0f)
        {
            return;
        }

        var current = Volatile.Read(ref MaxClusterOverhang);
        while (over > current)
        {
            var prior = Interlocked.CompareExchange(ref MaxClusterOverhang, over, current);
            if (prior == current)
            {
                return;
            }
            current = prior;
        }
    }

    /// <summary>
    /// The density-derived intra-cell target as a fraction of the cell edge, for a cell of <paramref name="entitiesInCell"/> entities packed into clusters
    /// of <paramref name="slotsPerCluster"/> slots in <c>d</c> = 2 or 3 dimensions (step 14, D1): <c>slack × (slotsPerCluster / E)^(1/d)</c>. Returns
    /// <c>1</c> — <b>off</b> — when the cell's population fits the bound already, and <c>0</c> when <paramref name="slack"/> is zero (constant mode: the
    /// caller falls back to the configured floors).
    /// </summary>
    /// <remarks>
    /// <para><b>The packing bound is geometry, not tuning.</b> A full cluster in a cell of <c>E</c> entities cannot have an axis shorter than
    /// <c>(slotsPerCluster / E)^(1/d)</c> of the cell — that is what a perfect Morton tiling reaches and nothing reaches less without fragmenting into
    /// emptier clusters. A constant target below the bound gates every written cluster on every tick and nominates drifters that have nowhere to go; a
    /// target above it by <paramref name="slack"/> is what an online packing under motion can hold.</para>
    /// <para>Roots rather than <c>MathF.Pow</c>: <c>Sqrt</c> and <c>Cbrt</c> are one instruction and one short polynomial respectively, and this resolves
    /// once per cell change, not per cluster.</para>
    /// </remarks>
    internal static float DensityTargetRatio(int entitiesInCell, int slotsPerCluster, bool flat, float slack) =>
        slack <= 0f ? 0f : DensityTargetFromBound(PackingBoundRatio(entitiesInCell, slotsPerCluster, flat), slack);

    /// <summary>
    /// <see cref="DensityTargetRatio"/> for a caller that already holds the cell's <see cref="PackingBoundRatio"/> — the AABB refresh's per-cell memo, which
    /// needs both readings and must not take the root twice (#911 O2).
    /// </summary>
    /// <remarks>
    /// <c>packingBound >= 1</c> is exactly the old <c>entitiesInCell &lt;= slotsPerCluster</c> test, because that is the only input for which
    /// <see cref="PackingBoundRatio"/> returns 1. It returns before the slack multiply, which matters when slack is below 1: a bound that is already the
    /// whole cell must not be scaled under it.
    /// </remarks>
    internal static float DensityTargetFromBound(float packingBound, float slack)
    {
        if (slack <= 0f)
        {
            return 0f;
        }

        if (packingBound >= 1f)
        {
            return 1f;
        }

        var ratio = slack * packingBound;
        return ratio >= 1f ? 1f : ratio;
    }

    /// <summary>
    /// The packing bound alone — <c>(slotsPerCluster / E)^(1/d)</c> as a fraction of the cell edge, with no slack and no floor. The tightest a full cluster
    /// can be in a cell of <paramref name="entitiesInCell"/> entities without fragmenting into emptier ones.
    /// </summary>
    /// <remarks>
    /// <para>Split out of <see cref="DensityTargetRatio"/> for #911 O2, which needs the bound itself rather than the target derived from it: measured extent
    /// ÷ bound is the reading the whole subsystem is judged on, and it cannot be recovered from the target by dividing out the slack — the target is clamped
    /// at 1 first, so every cell whose slack-scaled bound reaches the cell reports the same 1 regardless of how far under it the bound sits.</para>
    /// <para>Geometry, not tuning: it is what a perfect Morton tiling reaches. <c>1</c> at or below <paramref name="slotsPerCluster"/> entities per cell —
    /// one cluster IS the cell there, which is why intra-cell maintenance correctly switches itself off in that basin.</para>
    /// </remarks>
    internal static float PackingBoundRatio(int entitiesInCell, int slotsPerCluster, bool flat)
    {
        if (entitiesInCell <= slotsPerCluster)
        {
            return 1f;
        }

        var fill = slotsPerCluster / (float)entitiesInCell;
        var bound = flat ? MathF.Sqrt(fill) : MathF.Cbrt(fill);
        return bound >= 1f ? 1f : bound;
    }

    /// <summary>
    /// One AABB-refresh slice's tightness accumulation (#911 O2): measured cluster extent against the cell's packing bound, summed so the archetype-wide
    /// means can be folded from every worker's slice.
    /// </summary>
    /// <remarks>
    /// <para><b>The population is clusters WRITTEN this tick, not clusters that exist.</b> This rides the AABB refresh because that is where the extent is
    /// already in registers and the cell's <c>EntityCount</c> has already been read for the density target — an add and a divide on a path that has both
    /// operands. The price is that a settled world contributes NO samples, which is why <see cref="Samples"/> is published: a mean of zero over zero samples
    /// is "nothing moved", not "the clusters are points". <c>SpatialPartitionMatrix.MeasurePartition</c> takes the other reading — a full
    /// <c>O(active clusters)</c> sweep — and the two are not interchangeable.</para>
    /// <para>Doubles rather than floats for the sums: a slice can accumulate tens of thousands of ratios near 1, and f32 stops adding at ~16 M.</para>
    /// </remarks>
    internal struct ClusterTightnessSample
    {
        /// <summary>Clusters that contributed a reading — zero when the slice scanned nothing, or when the archetype has no cell size to normalise by.</summary>
        internal int Samples;

        /// <summary>Sum of each cluster's largest axis extent as a fraction of the cell edge.</summary>
        internal double SumExtentRatio;

        /// <summary>Sum of each cluster's cell's packing bound, as a fraction of the cell edge. The denominator tightness is judged against.</summary>
        internal double SumPackingBound;

        /// <summary>Fold one cluster in. <paramref name="active"/> is the slice's "there is a grid with a positive cell size" flag; false makes this a no-op.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Note(bool active, float maxAxisExtent, float inverseCellSize, float packingBound)
        {
            // A cluster whose bound is still the Empty sentinel yields a non-finite extent; counting it would poison the mean with an infinity. The gate is
            // the same three-compare shape the callers already use, so a healthy cluster pays a compare and two adds.
            if (!active || !float.IsFinite(maxAxisExtent) || maxAxisExtent < 0f)
            {
                return;
            }

            Samples++;
            SumExtentRatio += maxAxisExtent * inverseCellSize;
            SumPackingBound += packingBound;
        }
    }

    /// <summary>Fold one AabbRefresh slice's tightness accumulation into the archetype-wide per-tick sums. One atomic trio per slice, not per cluster.</summary>
    internal void FoldTightnessSample(in ClusterTightnessSample tightness)
    {
        if (tightness.Samples == 0)
        {
            return;
        }

        Interlocked.Add(ref LastTickTightnessSamples, tightness.Samples);
        InterlockedAddDouble(ref LastTickTightnessExtentSum, tightness.SumExtentRatio);
        InterlockedAddDouble(ref LastTickTightnessBoundSum, tightness.SumPackingBound);
    }

    /// <summary>
    /// Add to a <see cref="double"/> field that parallel fence workers share. .NET has no <c>Interlocked.Add(ref double)</c>, so this is the same
    /// compare-exchange-over-the-bit-pattern loop the Migrate phase uses for <see cref="LastTickMigrationExecuteMs"/>.
    /// </summary>
    /// <remarks>Called once per SLICE, not per cluster — the per-cluster accumulation happens in a worker-local <see cref="ClusterTightnessSample"/>.</remarks>
    internal static void InterlockedAddDouble(ref double target, double addend)
    {
        SpinWait sw = default;
        while (true)
        {
            var current = Volatile.Read(ref target);
            var candidate = current + addend;
            var currentBits = BitConverter.DoubleToInt64Bits(current);
            if (Interlocked.CompareExchange(ref Unsafe.As<double, long>(ref target), BitConverter.DoubleToInt64Bits(candidate), currentBits) == currentBits)
            {
                return;
            }

            sw.SpinOnce();
        }
    }

    /// <summary>
    /// Per-slice memo of the two gate extents for the cell being scanned (step 14). Resolves on a cell change only; both branches of the AABB refresh
    /// visit a cell's clusters in runs, so the common case is a compare against the cached key.
    /// </summary>
    private struct CellTargetResolver
    {
        private readonly SpatialGrid _grid;
        private readonly float _cellSize;
        private readonly float _driftFloor;
        private readonly float _repairFloor;
        private readonly float _repairFloorRatio;
        private readonly float _minRatio;
        private readonly float _slack;
        private readonly float _boost;
        private readonly int _slotsPerCluster;
        private readonly bool _flat;
        private readonly bool _active;
        private int _cellKey;

        /// <summary><c>ClusterTargetPackingSlack</c> is zero: the configured floors are the gates, untouched by density and by the boost.</summary>
        internal readonly bool ConstantMode;

        /// <summary>The drift gate, in world units; <c>0</c> when intra-cell relocation is off for this cell.</summary>
        internal float DriftExtent;

        /// <summary>The repair-nomination gate, in world units; <c>0</c> when repair is off for this cell (or for the archetype).</summary>
        internal float RepairExtent;

        /// <summary>
        /// The cell's packing bound as a fraction of the cell edge (#911 O2) — the denominator of tightness-to-bound. <c>1</c> until the first cell resolves,
        /// and for a slice with no grid, because a cell whose population fits one cluster IS bounded at the cell.
        /// </summary>
        /// <remarks>
        /// Free here and nowhere else: <see cref="Resolve"/> already loads the cell's <c>EntityCount</c> and already takes the root, so publishing the bound
        /// it computed costs one store per cell change. Computing it in the caller would repeat both.
        /// </remarks>
        internal float PackingBound;

        internal CellTargetResolver(SpatialGrid grid, float cellSize, float driftFloor, float repairFloor, int slotsPerCluster, bool flat, float boost)
        {
            _grid = grid;
            _cellSize = cellSize;
            _driftFloor = driftFloor;
            _repairFloor = repairFloor;
            _repairFloorRatio = cellSize > 0f ? repairFloor / cellSize : 0f;
            _slotsPerCluster = slotsPerCluster;
            _flat = flat;
            _boost = boost;
            _cellKey = -1;
            _active = grid != null && cellSize > 0f;
            _minRatio = _active ? grid.Config.ClusterTargetExtentRatio : 0f;
            _slack = _active ? grid.Config.ClusterTargetPackingSlack : 0f;
            ConstantMode = !_active || _slack <= 0f;
            // The floors until the first cell resolves; a slice with no grid never resolves and keeps them, which for a grid-less archetype are 0.
            DriftExtent = driftFloor;
            RepairExtent = repairFloor;
            PackingBound = 1f;
        }

        internal void Resolve(int cellKey)
        {
            if (cellKey == _cellKey || !_active)
            {
                return;
            }

            _cellKey = cellKey;

            // One root per cell change, feeding both readings (#911 O2). The bound is pure geometry and is published even in constant mode, where the GATES
            // ignore it — a tightness reading is not a tuning decision and has no reason to go dark because the targets were pinned to constants.
            var bound = PackingBoundRatio(_grid.GetCell(cellKey).EntityCount, _slotsPerCluster, _flat);
            PackingBound = bound;
            var density = DensityTargetFromBound(bound, _slack);
            if (density <= 0f)
            {
                // Constant mode: the configured floors, untouched by density and by the boost — the pre-step-14 behaviour, byte for byte.
                DriftExtent = _driftFloor;
                RepairExtent = _repairFloor;
                return;
            }

            // The drift target is the density target, never below the configured floor, raised by the throttle's boost (D2) and clamped at the cell —
            // where it means off. The repair target is the density target, never below ITS configured floor: between the two, relocation maintains; above,
            // repair re-sorts; and a cell whose density target is the cell itself cannot be re-sorted into anything tighter either.
            var driftRatio = MathF.Max(density, _minRatio) * _boost;
            DriftExtent = driftRatio >= 1f ? 0f : _cellSize * driftRatio;

            var repairRatio = MathF.Max(density, _repairFloorRatio);
            RepairExtent = _repairFloor > 0f && repairRatio < 1f ? _cellSize * repairRatio : 0f;
        }
    }

    /// <summary>
    /// Multiplier the throttle applies to the intra-cell target extent (step 14, D2): raised while relocations are being refused, decayed once the throttle
    /// runs clean, so the budget bounds what is DETECTED rather than truncating what was already nominated. Read by the AABB refresh, written from Prep.
    /// </summary>
    internal float DriftTargetBoost = 1f;

    /// <summary>Consecutive ticks the throttle refused nothing, for the boost's decay hysteresis.</summary>
    private int _driftBoostCleanTicks;

    /// <summary>Per-tick boost step. Four steps span 1 → 2.44; the cap is wherever the target reaches the cell, which <see cref="CellTargetResolver.Resolve"/> clamps.</summary>
    internal const float DriftTargetBoostStep = 1.25f;

    /// <summary>Clean ticks before the boost decays one step. Long enough that a boost is not undone by the quiet tick its own effect produced.</summary>
    internal const int DriftTargetBoostDecayTicks = 4;

    /// <summary>
    /// Fold last tick's throttle verdict into <see cref="DriftTargetBoost"/>. Called from Prep before the counters are reset, once per archetype per
    /// tick. A no-op in constant mode, where the resolver never reads the boost — accumulating it there would only produce a number that grows
    /// without bound under sustained throttling.
    /// </summary>
    /// <param name="config">The grid configuration; <c>ClusterTargetPackingSlack</c> = 0 is constant mode.</param>
    internal void UpdateDriftTargetBoost(in SpatialGridConfig config)
    {
        if (config.ClusterTargetPackingSlack <= 0f)
        {
            return;
        }

        if (LastTickRelocationsThrottled > 0)
        {
            _driftBoostCleanTicks = 0;
            // Capped where the floor itself would reach the cell: past that every target is already off and a larger boost only lengthens the decay.
            var cap = config.ClusterTargetExtentRatio > 0f ? 1f / config.ClusterTargetExtentRatio : DriftTargetBoostStep;
            DriftTargetBoost = MathF.Min(DriftTargetBoost * DriftTargetBoostStep, cap);
            return;
        }

        if (DriftTargetBoost <= 1f)
        {
            return;
        }

        if (++_driftBoostCleanTicks < DriftTargetBoostDecayTicks)
        {
            return;
        }

        _driftBoostCleanTicks = 0;
        DriftTargetBoost = MathF.Max(1f, DriftTargetBoost / DriftTargetBoostStep);
    }

    /// <summary>
    /// Whether <c>ClusterRef.WriteSpatial</c> touched this cluster since the last fence — that is, whether <see cref="ClusterAabbs"/> may have been advanced
    /// by the write-time CAS rather than by the fence's own recompute.
    /// </summary>
    /// <remarks>
    /// A plain read, not <see cref="Interlocked"/>: writers SET the bit with <c>Interlocked.Or</c>, but this reads it from inside the fence, after the barrier
    /// that closes the write window, so nothing is concurrent with it. <see cref="ClearAabbRefreshBookkeeping"/> zeroes the array once per tick, so a set bit
    /// always refers to this tick and never carries over.
    /// </remarks>
    /// <summary>
    /// Does this cluster's AABB have to be re-derived from its entities this tick, or is the stored bound still exact?
    /// </summary>
    /// <remarks>
    /// <para>Used only by the <c>ActiveClusterIds</c> (non-barrier) arm of <see cref="RecomputeDirtyClusterAabbsSlice"/>, which has no dirty filter of its
    /// own — the barrier arm gets the same effect for free by iterating <see cref="ClusterProcessBitmap"/>.</para>
    /// <para><b>Every "no information" answer is <c>true</c>.</b> A null or short <see cref="FenceDirtyBits"/> means Prep has not published a snapshot this
    /// tick, or a cluster was allocated past the pre-sized bound during Migrate; either way the honest answer is "recompute". Being wrong in the other
    /// direction is a bound that silently stops tracking its entities, which is the failure mode this whole path exists to prevent.</para>
    /// <para><b>The three signals are not interchangeable and none is redundant.</b> A write sets the dirty bit but not the process bit; a
    /// <c>WriteSpatial</c> sets the process bit and deliberately not the dirty bit (<c>ClusterRef.cs:300</c>); a destroy or a migration-out sets NEITHER,
    /// because Prep step 2 masks the dirty word with occupancy and a vacated slot is no longer occupied — which is why the vacating sites flag a shrink.
    /// Dropping any one of the three leaves a class of change invisible to the refresh.</para>
    /// </remarks>
    /// <summary>
    /// Has this tick's drift scan already nominated as many relocations as the budget can possibly admit?
    /// </summary>
    /// <remarks>
    /// Checked once per CLUSTER, before the per-entity walk, and read without synchronisation on purpose. Slices run concurrently, so an exact count would
    /// need a contended atomic on the hot path to enforce a bound that is itself an estimate; a relaxed read can let a few extra clusters through, which
    /// costs a few nominations the throttle would have dropped anyway. See <see cref="ComputeDriftNominationCap"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool DriftNominationBudgetSpent(int cap) => cap > 0 && Volatile.Read(ref DriftNominationsThisTick) >= cap;

    /// <summary>Record nominations produced by one cluster's drift scan.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void NoteDriftNominations(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref DriftNominationsThisTick, count);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ClusterNeedsAabbRecompute(int chunkId)
    {
        var dirty = FenceDirtyBits;
        if (dirty != null)
        {
            // Past the snapshot Prep sized: a cluster allocated during Migrate has no entry here, and "no entry" is not
            // evidence of "not written". Recompute.
            if ((uint)chunkId >= (uint)dirty.Length)
            {
                return true;
            }

            if (dirty[chunkId] != 0)
            {
                return true;
            }
        }

        // A NULL array is "Prep published nothing", NOT "Prep has not run". FenceDirtyBits is cleared at the top of every
        // Prep and re-published at its end only when the archetype had work (FenceBranchPath 2); the refresh runs strictly
        // after Prep on both the serial and the parallel path, so by the time control reaches here the only way to see null
        // is that this archetype's Prep found nothing dirty. Treating it as "unknown, recompute everything" is what a first
        // version of this gate did, and it made the QUIET tick — the one case the gate exists for — the one case it did not
        // help: a world where nothing moved re-read every entity position exactly as before.
        //
        // Falling THROUGH rather than returning false is the point. A destroy reaches the fence with no dirty bit at all
        // (Prep step 2 masks the freed slot away, so an archetype whose only event was a destroy publishes null), and it is
        // the shrink flag below that carries it.
        var shrink = ClusterShrinkPendingAxes;
        if (shrink != null && (uint)chunkId < (uint)shrink.Length && shrink[chunkId] != 0)
        {
            return true;
        }

        if (IsClusterProcessBitSet(chunkId))
        {
            return true;
        }

        // Last, because it is the coarsest and the rarest: somebody took a raw mutable span over the spatial column this tick, so no per-cluster signal can
        // be trusted to be complete. See SpatialSpanHandedOut.
        return Volatile.Read(ref SpatialSpanHandedOut) != 0;
    }

    private bool IsClusterProcessBitSet(int chunkId)
    {
        var bitmap = ClusterProcessBitmap;
        var wordIdx = chunkId >> 6;
        return bitmap != null && (uint)wordIdx < (uint)bitmap.Length && (bitmap[wordIdx] & (1L << (chunkId & 63))) != 0;
    }

    /// <summary>
    /// Clear the write-time bookkeeping arrays (<see cref="ClusterProcessBitmap"/>, <see cref="ClusterMigrationPendingSlots"/>,
    /// <see cref="ClusterShrinkPendingAxes"/>) for the next tick. Single-threaded — called once per archetype from
    /// <see cref="DatabaseEngine.FinalizeArchetypeFence"/> after all AABB slices finished.
    /// </summary>
    internal void ClearAabbRefreshBookkeeping()
    {
        // BEFORE the early return, and that ordering is load-bearing: an archetype with no process bitmap still hands out spans, and a flag that is never
        // cleared turns one GetSpan call into an unconditional full walk for the rest of the process. Cleared here rather than at the top of Prep because
        // GetSpan is called by SYSTEMS, which run before the fence — clearing on entry would discard the very signal the refresh is meant to consume.
        Volatile.Write(ref SpatialSpanHandedOut, 0);
        Volatile.Write(ref DriftNominationsThisTick, 0);
        Volatile.Write(ref SlotReleasesThisTick, 0);

        if (ClusterProcessBitmap == null)
        {
            return;
        }

        for (var wordIdx = 0; wordIdx < ClusterProcessBitmap.Length; wordIdx++)
        {
            var word = ClusterProcessBitmap[wordIdx];
            if (word == 0)
            {
                continue;
            }

            while (word != 0)
            {
                var chunkId = (wordIdx << 6) + BitOperations.TrailingZeroCount((ulong)word);
                word &= word - 1;
                if (ClusterMigrationPendingSlots != null && chunkId < ClusterMigrationPendingSlots.Length)
                {
                    ClusterMigrationPendingSlots[chunkId] = 0;
                    ClusterMigrationDestCellKeys[chunkId] = -1;
                }
                if (ClusterShrinkPendingAxes != null && chunkId < ClusterShrinkPendingAxes.Length)
                {
                    ClusterShrinkPendingAxes[chunkId] = 0;
                }
            }
            ClusterProcessBitmap[wordIdx] = 0;
        }
    }

    /// <summary>
    /// Safety valve for the "Max Cluster AABB Extent" invariant from design doc 01-spatial-clusters.md (issue #230 Phase 3 closure of Phase 1 gap). Scans a
    /// cluster whose recomputed AABB has grown beyond <c>cellSize × 1.2</c> and enqueues migration for any entity that has drifted outside the current cell's
    /// raw bounds. This bypasses the hysteresis dead zone that <c>DatabaseEngine.DetectClusterMigrations</c> normally honors — the point is
    /// exactly to force-migrate entities that the hysteresis had absorbed individually but whose accumulated drift is degrading the cluster's spatial
    /// coherence.
    /// </summary>
    /// <remarks>
    /// <para>Rare path. Runs inside <see cref="RecomputeDirtyClusterAabbs"/> only when the extent check fires — well-behaved workloads never hit it. The
    /// enqueued migrations are drained on the next tick (not this one), because this runs AFTER
    /// <see cref="DatabaseEngine.ExecuteMigrations"/> in the tick fence order. That one-tick lag is the "safety valve, not a common case" note from the
    /// design doc.</para>
    /// <para><b>Reads centres from the caller's single gather pass since <c>D1</c>, and no longer walks the cluster itself.</b> It used to re-fetch the chunk
    /// base, re-read the occupancy word and re-hoist the component offsets to obtain exactly the values drift detection was about to read again two lines
    /// later. The returned mask is what lets detection exclude the slots this method claimed — see the exclusion in
    /// <see cref="DetectDriftersInCluster"/>, which was previously only an ordering convention.</para>
    /// </remarks>
    /// <returns>The slots queued for another cell, so intra-cell detection can skip them.</returns>
    private ulong FlagOutliersForMigration(int clusterChunkId, int cellKey, SpatialGrid grid, in ClusterCentres centres,
        List<MigrationRequest> outlierBuffer)
    {
        var (cellX, cellY, cellZ) = grid.CellKeyToCoords(cellKey);
        ref readonly var cfg = ref grid.Config;
        var cellMinX = cfg.WorldMin.X + cellX * cfg.CellSize;
        var cellMinY = cfg.WorldMin.Y + cellY * cfg.CellSize;
        var cellMinZ = cfg.WorldMin.Z + cellZ * cfg.CellSize;
        var cellMaxX = cellMinX + cfg.CellSize;
        var cellMaxY = cellMinY + cfg.CellSize;
        var cellMaxZ = cellMinZ + cfg.CellSize;

        ulong claimed = 0;
        var bits = centres.ValidMask;
        while (bits != 0)
        {
            var slotIndex = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;

            var posX = centres.X(slotIndex);
            var posY = centres.Y(slotIndex);
            var posZ = centres.Z(slotIndex);

            // Raw cell boundary (no hysteresis) — force migrate anything outside. A 2D field reports posZ = 0 and the grid is one cell deep there, so the Z
            // pair is always false for a flat world: the third axis costs two comparisons and changes no flat-world outcome.
            //
            // For a 3D archetype it is load-bearing, and only became reachable with #914's 3D write tiers: an entity that leaves its cell purely on Z passes
            // both the X and the Y test, so deleting the Z pair would strand it in the wrong cell with every counter balancing. OutlierGuardZAxisTests is the
            // test that reddens if it goes.
            if (posX < cellMinX || posX > cellMaxX || posY < cellMinY || posY > cellMaxY || posZ < cellMinZ || posZ > cellMaxZ)
            {
                var newCellKey = grid.WorldToCellKey(posX, posY, posZ);
                if (newCellKey != cellKey)
                {
                    // Worker-local buffer: caller bulk-appends under _finalizeLock once at slice end. Avoids per-entity lock acquisition (review D-2).
                    // For serial callers (RecomputeDirtyClusterAabbs whole-archetype wrapper), the buffer is appended without contention.
                    outlierBuffer.Add(new MigrationRequest(clusterChunkId, slotIndex, newCellKey));
                    claimed |= 1UL << slotIndex;
                }
            }
        }

        return claimed;
    }

    /// <summary>
    /// Add a cluster to its cell's <see cref="PerCellSpatialSlot"/> — routed to <see cref="PerCellSpatialSlot.DynamicIndex"/> for Dynamic archetypes and
    /// <see cref="PerCellSpatialSlot.StaticIndex"/> for Static archetypes. Lazily allocates the slot and index as needed. Records the back-pointer in
    /// <see cref="ClusterSpatialIndexSlot"/> for O(1) subsequent updates. Issue #230.
    /// </summary>
    internal void AddClusterToPerCellIndex(int clusterChunkId, int cellKey, in ClusterSpatialAabb aabb)
    {
        NoteClusterOverhang(in aabb, (float)(Grid?.Config.CellSize ?? 0d));

        // The growers take _finalizeLock themselves (non-reentrant), so they run BEFORE the latch below; what they publish is monotonic, so the
        // references re-read under the latch are current and at least as long as what was just ensured.
        EnsurePerCellIndexCapacity(cellKey + 1);
        EnsureClusterSpatialIndexSlotCapacity(clusterChunkId + 1);
        EnsureClusterWriteBookkeepingCapacity(clusterChunkId + 1);

        // Promotion needs the tree segment, whose creation takes _finalizeLock too — ensured here, ahead of the latch, so the promotion under it hits
        // the volatile fast path and never re-enters. Cheap: one volatile read once the segment exists. A factory that handed back nothing would leave
        // the segment null; the Locked body then skips promotion rather than re-entering the latch for it (step 15 review).
        var treeSegmentReady = CellTreePromoteThreshold == int.MaxValue || TryEnsureCellTreeSegment();

        // ── Under the archetype's finalize latch (step 15) ──────────────────────────────────────────────────────────
        //
        // The fence's callers of this method are exclusive by construction (AabbRefresh slices, cell-disjoint Migrate slices, Prep). The SPAWN path is
        // not: two transactions committing on two threads can each open a fresh cluster in the same cell and reach here together, and this used to run
        // latch-free — `PerCellIndex[cellKey] = new PerCellSpatialSlot()` from both (one slot, and every cluster in it, lost), two `CellSpatialIndex.Add`
        // calls racing on one count (an IndexOutOfRange at the append, 7 of 30 runs of
        // ClusterPlacementTests.ConcurrentSpawnsAndBoundGrowthKeepClustersInTheirCell), and a write into an array a concurrent grower had just
        // replaced. One uncontended latch per fresh cluster is the whole cost; a fresh cluster is rare against the claims that fill it.
        ref var addCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref addCtx);
        try
        {
            AddClusterToPerCellIndexLocked(clusterChunkId, cellKey, in aabb, treeSegmentReady);
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>
    /// The body of <see cref="AddClusterToPerCellIndex"/>, for a caller that already holds <c>_finalizeLock</c> — the four cluster-allocation sites,
    /// which index the fresh cluster with an EMPTY box before the pool publishes it. That is what makes "is this cluster in its cell's index" a
    /// question with one answer: every spawner that finds the cluster in the pool, the allocator included, sees it indexed and only ever widens.
    /// Left to whichever spawner wrote the first entity, two concurrent claimants both read "not indexed", both reset the box and both added it —
    /// a duplicate index entry, and a reset that wiped the other's widening (step 15 review; CA-01, CA-02). Idempotent for a linear half: a cluster
    /// whose back-pointer is already set is not added twice.
    /// </summary>
    internal void AddClusterToPerCellIndexLocked(int clusterChunkId, int cellKey, in ClusterSpatialAabb aabb, bool treeSegmentReady)
    {
        AssertFinalizeLockHeld(nameof(AddClusterToPerCellIndexLocked));
        EnsurePerCellIndexCapacityLocked(cellKey + 1);
        EnsureClusterSpatialIndexSlotCapacityLocked(clusterChunkId + 1);
        EnsureClusterWriteBookkeepingCapacityLocked(clusterChunkId + 1);

        var isStatic = SpatialSlot.FieldInfo.Mode == SpatialMode.Static;
        var perCell = PerCellIndex;
        var slot = perCell[cellKey];
        if (slot == null)
        {
            slot = new PerCellSpatialSlot();
            perCell[cellKey] = slot;
        }

        var backPointers = ClusterSpatialIndexSlot;
        var tree = isStatic ? slot.StaticTree : slot.DynamicTree;
        if (tree != null)
        {
            tree.Add(clusterChunkId, in aabb);
            TyphonEvent.EmitSpatialCellIndexAdd(cellKey, backPointers[clusterChunkId], clusterChunkId, tree.ClusterCount);
            return;
        }

        if (backPointers[clusterChunkId] >= 0)
        {
            return;   // already in this cell's linear half — the allocation site indexed it; the spawner's own add is a no-op
        }

        if (isStatic)
        {
            slot.StaticIndex ??= new CellSpatialIndex();
            var indexSlot = slot.StaticIndex.Add(clusterChunkId, aabb);
            backPointers[clusterChunkId] = indexSlot;
            TyphonEvent.EmitSpatialCellIndexAdd(cellKey, indexSlot, clusterChunkId, slot.StaticIndex.Capacity);
        }
        else
        {
            slot.DynamicIndex ??= new CellSpatialIndex();
            var indexSlot = slot.DynamicIndex.Add(clusterChunkId, aabb);
            backPointers[clusterChunkId] = indexSlot;
            TyphonEvent.EmitSpatialCellIndexAdd(cellKey, indexSlot, clusterChunkId, slot.DynamicIndex.Capacity);
        }

        if (treeSegmentReady)
        {
            MaybePromoteCellHalf(slot, isStatic, cellKey);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Promotion and demotion between the linear index and a per-cell R-Tree (#872 step 9)
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Swap a cell half over to a <see cref="CellClusterTree"/> once it holds <see cref="CellTreePromoteThreshold"/> clusters. No-op when promotion is
    /// disabled, which is the default.
    /// </summary>
    /// <remarks>
    /// The rebuild is O(C) and happens at most once per crossing thanks to the demote gap. Failure to obtain a segment is not an error: an archetype
    /// constructed outside <c>DatabaseEngine</c> has no factory, and the correct behaviour there is to keep scanning, not to throw at a caller that never
    /// asked for a tree.
    /// </remarks>
    /// <param name="slot">The cell's per-cell spatial slot.</param>
    /// <param name="isStatic">Which half of it — the archetype's <see cref="SpatialMode"/>.</param>
    /// <param name="cellKey">
    /// The cell being considered. A cell turned down on TIGHTNESS alone is recorded against this key for the fence to reconsider
    /// (see <see cref="_tightnessBlockedCells"/>).
    /// </param>
    private void MaybePromoteCellHalf(PerCellSpatialSlot slot, bool isStatic, int cellKey)
    {
        if (CellTreePromoteThreshold == int.MaxValue)
        {
            return;
        }

        var linear = isStatic ? slot.StaticIndex : slot.DynamicIndex;
        if (linear == null || linear.ClusterCount < CellTreePromoteThreshold || !TryEnsureCellTreeSegment())
        {
            return;
        }

        // The count says the scan is long; this says the tree could prune it. Both, or neither is worth paying for — see
        // SpatialOptions.CellTreePromoteTightness for the sweep that put the boundary at a tenth of a cell.
        if (!CellHalfIsTightEnough(linear, CellTreePromoteTightness))
        {
            if (cellKey >= 0)
            {
                (_tightnessBlockedCells ??= new List<int>()).Add(cellKey);
            }

            return;
        }

        var tree = new CellClusterTree(CellTreeSegment, ClusterSpatialIndexSlot);

        // Retire the LINEAR slot indices before re-issuing tree handles into the same array. The two representations share ClusterSpatialIndexSlot, and a
        // linear slot index is just a small non-negative int — indistinguishable from a packed handle. Without this the tree cannot tell an entry it already
        // holds from one the linear index used to hold, so its own duplicate-add guard fires on every promotion.
        for (var i = 0; i < linear.ClusterCount; i++)
        {
            ClusterSpatialIndexSlot[linear.ClusterIds[i]] = SpatialRTree<TransientStore>.NullHandle;
        }

        for (var i = 0; i < linear.ClusterCount; i++)
        {
            var aabb = new ClusterSpatialAabb
            {
                MinX = linear.MinX[i],
                MinY = linear.MinY[i],
                MinZ = linear.MinZ[i],
                MaxX = linear.MaxX[i],
                MaxY = linear.MaxY[i],
                MaxZ = linear.MaxZ[i],
                CategoryMask = linear.CategoryMasks[i],
            };
            tree.Add(linear.ClusterIds[i], in aabb);
        }

        if (isStatic)
        {
            slot.PublishStaticTree(tree);
        }
        else
        {
            slot.PublishDynamicTree(tree);
        }
        // Interlocked because this counter is ARCHETYPE-wide while the Migrate slices that reach it are only CELL-disjoint. A lost increment makes
        // RefitPromotedCellTrees and RebindCellTreeBackPointers early-return, so ST-07's loose-leaf window outlives the fence and ST-05's rebind is skipped
        // after a resize — both silent.
        Interlocked.Increment(ref PromotedCellCount);
        Interlocked.Increment(ref LastTickCellTreePromotions);   // same reachability as the line above, so the same atomicity
        if (cellKey >= 0)
        {
            (_promotedCells ??= new List<int>()).Add(cellKey);
        }
    }

    /// <summary>
    /// Is the mean largest-axis extent of this cell half's clusters at or below <paramref name="limit"/> of the cell edge?
    /// </summary>
    /// <remarks>
    /// The MEAN rather than the maximum, and deliberately: one straddling cluster is what a Z-order fill produces about once in twenty (the P90 the step-15
    /// measurement records at ~101 % against a mean of 1.35x the bound), and letting it veto a cell that is otherwise packed would mean never promoting.
    /// Bounds are cell-relative (<c>C15</c>), so an extent is already a length in cell units; an unestablished box contributes nothing.
    /// </remarks>
    private bool CellHalfIsTightEnough(CellSpatialIndex linear, float limit)
    {
        if (limit >= 1f)
        {
            return true;   // the tightness gate is off — count alone decides
        }

        var cellSize = Grid?.Config.CellSize ?? 0f;
        if (!(cellSize > 0f))
        {
            return true;
        }

        var total = 0d;
        var counted = 0;
        for (var i = 0; i < linear.ClusterCount; i++)
        {
            var minX = linear.MinX[i];
            if (float.IsPositiveInfinity(minX))
            {
                continue;
            }

            var extent = MathF.Max(linear.MaxX[i] - minX, linear.MaxY[i] - linear.MinY[i]);
            var minZ = linear.MinZ[i];
            if (!float.IsPositiveInfinity(minZ) && !float.IsNegativeInfinity(linear.MaxZ[i]))
            {
                extent = MathF.Max(extent, linear.MaxZ[i] - minZ);
            }

            if (float.IsFinite(extent) && extent >= 0f)
            {
                total += extent;
                counted++;
            }
        }

        // A half whose every box is still the Empty sentinel is not "tight": promoting it would build a tree over nothing, and the next fence — once the
        // bounds fill in — would demote it again. Two O(C) rebuilds, which is exactly what the promote/demote gap exists to prevent.
        return counted > 0 && (total / counted) <= limit * cellSize;
    }

    /// <summary>The same mean over a PROMOTED half, read from <see cref="ClusterAabbs"/> because the tree keeps no indexable copy of its bounds.</summary>
    private double MeanExtentOfPromotedHalf(CellClusterTree tree)
    {
        var total = 0d;
        var counted = 0;
        var aabbs = ClusterAabbs;
        foreach (var chunkId in tree.EnumerateClusterIds())
        {
            if ((uint)chunkId >= (uint)aabbs.Length)
            {
                continue;
            }

            ref var box = ref aabbs[chunkId];
            if (float.IsPositiveInfinity(box.MinX))
            {
                continue;
            }

            var extent = MathF.Max(box.MaxX - box.MinX, box.MaxY - box.MinY);
            if (!float.IsPositiveInfinity(box.MinZ) && !float.IsNegativeInfinity(box.MaxZ))
            {
                extent = MathF.Max(extent, box.MaxZ - box.MinZ);
            }

            if (float.IsFinite(extent) && extent >= 0f)
            {
                total += extent;
                counted++;
            }
        }

        return counted == 0 ? 0d : total / counted;
    }

    /// <summary>
    /// Reconsider the cells whose shape — not whose cluster count — decides their broadphase, once the tick's bounds are final: promote a cell the repair
    /// has packed, demote one that motion has pulled apart. Serial, inside <c>FinalizeArchetypeFence</c>, which is what <c>PC-01</c> requires of any
    /// writer to a promoted cell's tree.
    /// </summary>
    /// <remarks>
    /// Both halves are bounded by density rather than by world size: the promote list holds only cells at or above the count threshold, and the demote walk
    /// runs only while some cell is promoted. A database that never fills a cell pays two field reads per fence for this.
    /// </remarks>
    internal void EvaluateCellTreeTightnessTransitions()
    {
        if (CellTreePromoteThreshold == int.MaxValue || PerCellIndex == null)
        {
            return;
        }

        var isStatic = SpatialSlot.FieldInfo.Mode == SpatialMode.Static;
        var blocked = _tightnessBlockedCells;
        if (blocked != null && blocked.Count > 0)
        {
            // Rebuilt rather than filtered: MaybePromoteCellHalf re-adds a cell it turns down again, so the list is the tick's answer, not a running one.
            var pending = blocked.ToArray();
            blocked.Clear();
            for (var i = 0; i < pending.Length; i++)
            {
                var cellKey = pending[i];
                if ((uint)cellKey >= (uint)PerCellIndex.Length)
                {
                    continue;
                }

                var slot = PerCellIndex[cellKey];
                if (slot != null)
                {
                    MaybePromoteCellHalf(slot, isStatic, cellKey);
                }
            }
        }

        if (PromotedCellCount == 0 || CellTreeDemoteTightness >= 1f)
        {
            return;
        }

        var cellSize = Grid?.Config.CellSize ?? 0f;
        if (!(cellSize > 0f))
        {
            return;
        }

        // Over the promoted cells, not over every cell that exists: RefitPromotedCellTrees already pays one scan of PerCellIndex per fence, and a second
        // would be ~1 ms per archetype at a couple of million cells for the sake of one promoted cell.
        var promoted = _promotedCells;
        if (promoted == null)
        {
            return;
        }

        var limit = CellTreeDemoteTightness * cellSize;
        for (var i = promoted.Count - 1; i >= 0; i--)
        {
            var cellKey = promoted[i];
            var slot = (uint)cellKey < (uint)PerCellIndex.Length ? PerCellIndex[cellKey] : null;
            var tree = slot == null ? null : (isStatic ? slot.StaticTree : slot.DynamicTree);
            if (tree == null || tree.ClusterCount == 0)
            {
                promoted.RemoveAt(i);   // demoted elsewhere (a cell that emptied below the count threshold), or the cell is gone
                continue;
            }

            if (MeanExtentOfPromotedHalf(tree) >= limit)
            {
                DemoteCellHalf(slot, isStatic);
                promoted.RemoveAt(i);
                LastTickCellTreeDemotions++;   // serial: FinalizeArchetypeFence is the only caller of this pass

                // Back on the blocked list, because the cell is back to a linear half that still holds enough clusters to promote. The only other way
                // onto that list is a cluster JOINING the cell, and a cell just demoted for looseness need never receive another one — so without this a
                // demotion is one-way until an unrelated arrival, and a cell the repair later re-packs stays on the linear scan for good.
                (_tightnessBlockedCells ??= new List<int>()).Add(cellKey);
            }
        }
    }

    /// <summary>Cell halves this tick's fence promoted to a tree, and fell back from one, on tightness.</summary>
    internal int LastTickCellTreePromotions;

    /// <inheritdoc cref="LastTickCellTreePromotions"/>
    internal int LastTickCellTreeDemotions;

    /// <summary>Clusters that contributed a tightness reading during the most recently completed tick's AABB refresh (#911 O2).</summary>
    /// <remarks>Written by every AabbRefresh slice via <see cref="Interlocked.Add(ref int, int)"/>, reset once per tick in <c>PrepareArchetypeFence</c>.</remarks>
    internal int LastTickTightnessSamples;

    /// <summary>Sum of the measured extent ratios behind <see cref="LastTickTightnessSamples"/>. See <see cref="ClusterTightnessSample"/>.</summary>
    internal double LastTickTightnessExtentSum;

    /// <summary>Sum of the packing bounds behind <see cref="LastTickTightnessSamples"/>. See <see cref="ClusterTightnessSample"/>.</summary>
    internal double LastTickTightnessBoundSum;

    /// <summary>Fall back to a linear index once a promoted cell half drops to <see cref="CellTreeDemoteThreshold"/>.</summary>
    private void DemoteCellHalf(PerCellSpatialSlot slot, bool isStatic)
    {
        var tree = isStatic ? slot.StaticTree : slot.DynamicTree;
        if (tree == null)
        {
            return;
        }

        // Ids come from a full-extent query; the BOUNDS come from ClusterAabbs, which already holds every cluster's cell-relative box indexed by chunk id.
        // Reading them back out of the tree would need a result type carrying coordinates, and keeping a second list of ids inside the tree would be one more
        // copy of a fact the tree already owns — the exact shape ST-05 was written about.
        var linear = new CellSpatialIndex(Math.Max(CellSpatialIndex.DefaultInitialCapacity, tree.ClusterCount));
        var ids = new int[tree.ClusterCount];
        var found = 0;
        foreach (var clusterChunkId in tree.EnumerateClusterIds())
        {
            if (found < ids.Length)
            {
                ids[found++] = clusterChunkId;
            }
        }

        // Empty the tree BEFORE building the linear index, and in that order for two independent reasons. The first is the leak: the tree's nodes are chunks
        // of the archetype's SHARED CellTreeSegment, and simply dropping the reference retires nothing — the segment is transient and has no GC to notice, so
        // every promote/fall-back cycle on one cell would strand that cell's whole node set for the life of the database. A cell oscillating across the
        // threshold does that once per crossing. RemoveAt frees each leaf as it empties and the root with the last entry (SpatialRTree.Remove).
        // The second is ST-05: RemoveAt reads the cluster's handle out of ClusterSpatialIndexSlot, and the loop below OVERWRITES that same array with linear
        // slot indices. Interleaving the two would hand RemoveAt a small non-negative int that is a valid-looking packed handle, and it would unpack to a leaf
        // and slot belonging to nothing.
        for (var i = 0; i < found; i++)
        {
            tree.RemoveAt(ids[i]);
        }

        // The removals cascade leaf frees, but the final empty root has no parent to unlink it from and survives them all — measured at exactly one stranded
        // chunk per promote/fall-back cycle before this call existed.
        tree.Release();

        for (var i = 0; i < found; i++)
        {
            var clusterChunkId = ids[i];
            var indexSlot = linear.Add(clusterChunkId, in ClusterAabbs[clusterChunkId]);
            ClusterSpatialIndexSlot[clusterChunkId] = indexSlot;
        }

        if (isStatic)
        {
            slot.PublishStaticIndex(linear);
        }
        else
        {
            slot.PublishDynamicIndex(linear);
        }
        Interlocked.Decrement(ref PromotedCellCount);
    }

    /// <summary>
    /// Close <c>ST-07</c>'s window: refit every leaf the in-place update path left loose, across every promoted cell of this archetype.
    /// </summary>
    /// <remarks>
    /// Must run after the last AABB slice and before any query — the fence's single-threaded per-archetype tail is the one point that satisfies both. Cheap
    /// when nothing was promoted, which is the default, and cheap when nothing moved, because a tree with no recorded loose leaves returns immediately.
    /// </remarks>
    internal void RefitPromotedCellTrees()
    {
        if (PromotedCellCount == 0 || PerCellIndex == null)
        {
            return;
        }

        for (var i = 0; i < PerCellIndex.Length; i++)
        {
            var slot = PerCellIndex[i];
            slot?.DynamicTree?.RefitLooseLeaves();
            slot?.StaticTree?.RefitLooseLeaves();
        }
    }

    /// <summary>
    /// Apply a cluster's refreshed bounds, or defer them when the cell is promoted AND a deferral sink was supplied.
    /// </summary>
    /// <remarks>
    /// <para><b>The fallback is mandatory, not optional.</b> A null sink means the caller is already the single writer — the serial whole-archetype recompute —
    /// so the write happens immediately. Writing this as <c>buffer?.Add(...)</c> with no else, which is what it was first, makes a null sink DISCARD the update
    /// instead: <see cref="ClusterAabbs"/> advances, the tree does not, and the two diverge permanently into <c>SQ-01</c> false negatives with nothing raised.
    /// That failure was reachable on the only configuration the guard leaves open, because the guard tells users to run the serial fence.</para>
    /// <para>Both fence branches route here — the <c>ClusterProcessBitmap</c> one and the <c>ActiveClusterIds</c> one. Neither is cell-partitioned, so neither
    /// may write a promoted cell's tree from a worker (ADR-044, invariant O2).</para>
    /// </remarks>
    private void ApplyOrDeferClusterUpdate(int chunkId, int cellKey, in ClusterSpatialAabb fresh, PerCellSpatialSlot slot, 
        List<PromotedAabbApply> promotedApplyBuffer)
    {
        if (slot.HasDynamicTree && promotedApplyBuffer != null)
        {
            promotedApplyBuffer.Add(new PromotedAabbApply(chunkId, cellKey));
            return;
        }

        UpdateClusterInPerCellIndex(chunkId, cellKey, in fresh);
    }

    /// <summary>
    /// Overwrite a cluster's stored bounds in its cell, through whichever structure serves that cell.
    /// </summary>
    /// <remarks>
    /// Every caller that used to dereference <c>slot.DynamicIndex</c> directly must come through here, because that field is NULL once the cell is promoted.
    /// Missing one is a <see cref="NullReferenceException"/> at the threshold and nowhere else — which is exactly what the first run of
    /// <c>CellTreePromotionTests</c> found in the spawn path, a site the grep for update calls had not covered.
    /// </remarks>
    internal void UpdateClusterInPerCellIndex(int clusterChunkId, int cellKey, in ClusterSpatialAabb aabb)
    {
        // Guarded like RemoveClusterFromPerCellIndex. This became the single funnel for four call sites, one of which is the deferred drain that runs a whole
        // fence phase after the ids were recorded — long enough for a cell to have been torn down underneath them.
        if (PerCellIndex == null || (uint)cellKey >= (uint)PerCellIndex.Length)
        {
            return;
        }

        var slot = PerCellIndex[cellKey];
        if (slot == null)
        {
            return;
        }

        var isStatic = SpatialSlot.FieldInfo.Mode == SpatialMode.Static;
        var tree = isStatic ? slot.StaticTree : slot.DynamicTree;
        if (tree != null)
        {
            tree.UpdateAt(clusterChunkId, in aabb, out _);
            return;
        }

        var linear = isStatic ? slot.StaticIndex : slot.DynamicIndex;
        if (linear == null || ClusterSpatialIndexSlot == null || (uint)clusterChunkId >= (uint)ClusterSpatialIndexSlot.Length)
        {
            return;
        }

        var indexSlot = ClusterSpatialIndexSlot[clusterChunkId];
        if (indexSlot < 0)
        {
            return;
        }

        linear.UpdateAt(indexSlot, in aabb);
    }

    /// <summary>
    /// <see cref="UpdateClusterInPerCellIndex"/> for the SPAWN path, which runs on user threads with no latch and only ever widens. A linear half is
    /// widened by per-axis CAS that survives a concurrent latched grow (<see cref="CellSpatialIndex.WidenAt"/>); a promoted half is written under
    /// <c>_finalizeLock</c>, because <c>PC-01</c> makes the tree single-writer by the caller's discipline and a user thread has none of the fence's.
    /// The fence's own callers keep <see cref="UpdateClusterInPerCellIndex"/>: they are exclusive by construction and may also shrink.
    /// </summary>
    /// <remarks>
    /// A promotion that lands between this method's read of the linear half and its widen leaves the widen in the linear half the tree was built
    /// from; the fence's next AabbRefresh of the (dirty) cluster republishes the bound. Tolerated for the same reason CR-02 tolerates a stale box:
    /// one tick of a too-tight leaf is a slower query, never a wrong cell.
    /// </remarks>
    internal void WidenClusterInPerCellIndex(int clusterChunkId, int cellKey, in ClusterSpatialAabb aabb)
    {
        // A spawn straddling its cell's edge widens every KNN ring — noted here as the add did.
        NoteClusterOverhang(in aabb, (float)(Grid?.Config.CellSize ?? 0d));

        var perCell = Volatile.Read(ref PerCellIndex);
        if (perCell == null || (uint)cellKey >= (uint)perCell.Length)
        {
            return;
        }

        var slot = perCell[cellKey];
        if (slot == null)
        {
            return;
        }

        var isStatic = SpatialSlot.FieldInfo.Mode == SpatialMode.Static;
        var tree = isStatic ? slot.StaticTree : slot.DynamicTree;
        if (tree != null)
        {
            ref var treeCtx = ref Unsafe.NullRef<WaitContext>();
            _finalizeLock.Enter(ref treeCtx);
            try
            {
                tree.UpdateAt(clusterChunkId, in aabb, out _);
            }
            finally
            {
                _finalizeLock.Exit();
            }

            return;
        }

        var linear = isStatic ? slot.StaticIndex : slot.DynamicIndex;
        var backPointers = Volatile.Read(ref ClusterSpatialIndexSlot);
        if (linear == null || backPointers == null || (uint)clusterChunkId >= (uint)backPointers.Length)
        {
            return;
        }

        var indexSlot = backPointers[clusterChunkId];
        if (indexSlot < 0)
        {
            return;
        }

        linear.WidenAt(indexSlot, in aabb);
    }

    /// <summary>
    /// The category mask currently stored for a cluster in its cell. A promoted tree DOES carry a mask per leaf entry (written by
    /// <c>CellClusterTree.Add</c> / <c>UpdateAt</c>); what it lacks is an INDEXABLE side array, so there is nothing to read back by slot. The value therefore
    /// comes from <see cref="ClusterAabbs"/>, which is the authority and holds the same value the linear index would have been holding.
    /// </summary>
    internal uint ReadStoredCategoryMask(PerCellSpatialSlot slot, int clusterChunkId, int indexSlot) =>
        slot.HasDynamicTree ? ClusterAabbs[clusterChunkId].CategoryMask : slot.DynamicIndex.CategoryMasks[indexSlot];

    /// <summary>
    /// Return every live cell tree's chunks to the archetype's shared segment. Call before discarding <see cref="PerCellIndex"/> wholesale.
    /// </summary>
    /// <remarks>
    /// The rebuild paths clear the per-cell index and reset <see cref="PromotedCellCount"/>, which drops the last reference to every promoted cell's tree.
    /// On a <c>TransientStore</c> segment that reclaims nothing — the chunks stay allocated and the segment is never rebuilt, so a rebuild that runs after
    /// cells have promoted strands their whole node sets for the life of the database. Reachable because these same methods PROMOTE while rebuilding: they
    /// call <see cref="AddClusterToPerCellIndex"/> per cluster, which evaluates the threshold.
    /// </remarks>
    /// <param name="epochManager">
    /// Pins the calling thread while the trees are walked. Emptying a tree opens a query enumerator over its own segment, and a
    /// <c>ChunkAccessor</c> may only be created inside an epoch scope — the demotion path inherits one from the fence, these rebuild paths have none of
    /// their own. Null skips the release rather than asserting: a caller with no epoch manager cannot walk the trees safely, and leaking chunks on a path
    /// that has no production caller is a better outcome than a torn read.
    /// </param>
    private void ReleaseAllCellTrees(EpochManager epochManager)
    {
        if (PerCellIndex == null || CellTreeSegment == null || epochManager == null)
        {
            return;
        }

        var depth = epochManager.EnterScope();
        try
        {
            ReleaseAllCellTreesInScope();
        }
        finally
        {
            epochManager.ExitScope(depth);
        }
    }

    /// <inheritdoc cref="ReleaseAllCellTrees"/>
    private void ReleaseAllCellTreesInScope()
    {
        for (var i = 0; i < PerCellIndex.Length; i++)
        {
            var slot = PerCellIndex[i];
            if (slot == null)
            {
                continue;
            }

            slot.DynamicTree?.ReleaseAll();
            slot.StaticTree?.ReleaseAll();
        }
    }

    /// <summary>Obtain the archetype's shared cell-tree segment, creating it on first use. False when no factory was supplied.</summary>
    /// <remarks>
    /// Double-checked under <c>_finalizeLock</c>. Promotion is decided per CELL and the Migrate phase's slices are cell-disjoint, so two workers can promote
    /// two different cells in the same instant — and both would reach a bare lazy initialiser. Each would build a whole
    /// <see cref="ChunkBasedSegment{TStore}"/>, the later store would win, and every tree the loser's cells had already built would be reading nodes out of a
    /// segment nothing else refers to. Queries against those cells would answer from an orphaned structure with nothing raised.
    /// </remarks>
    private bool TryEnsureCellTreeSegment()
    {
        if (Volatile.Read(ref CellTreeSegment) != null)
        {
            return true;
        }
        if (CellTreeSegmentFactory == null)
        {
            return false;
        }

        ref var createCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref createCtx);
        try
        {
            if (CellTreeSegment != null)
            {
                return true;
            }

            var (segment, store) = CellTreeSegmentFactory(SpatialNodeDescriptor.ForVariant(CellClusterTree.Variant).Stride);
            CellTreeStore = store;
            // Published last, and with release semantics: CellTreeSegment is the field every other thread's fast path tests, so the store it carries must
            // already be visible to anyone who sees it.
            Volatile.Write(ref CellTreeSegment, segment);
            return segment != null;
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>
    /// Re-point every live cell tree at the current <see cref="ClusterSpatialIndexSlot"/> array.
    /// </summary>
    /// <remarks>
    /// That array doubles as the trees' <c>PayloadBackPointers</c>, and growing it is an <see cref="Array.Resize{T}"/> — a REALLOCATION, which leaves every
    /// tree holding the abandoned copy. A tree writing handles into an array nobody reads is the stale-handle failure of ST-05 with an extra layer of
    /// indirection, so the rebind is not optional and has to happen on the same call that grew the array.
    /// </remarks>
    private void RebindCellTreeBackPointers()
    {
        if (PromotedCellCount == 0 || PerCellIndex == null)
        {
            return;
        }

        for (var i = 0; i < PerCellIndex.Length; i++)
        {
            var slot = PerCellIndex[i];
            slot?.DynamicTree?.RebindBackPointers(ClusterSpatialIndexSlot);
            slot?.StaticTree?.RebindBackPointers(ClusterSpatialIndexSlot);
        }
    }

    /// <summary>
    /// Remove a cluster from its cell's <see cref="PerCellSpatialSlot"/>. Routes to Static or Dynamic based on the archetype's
    /// <see cref="SpatialFieldInfo.Mode"/>. Fixes up the back-pointer of any cluster that was swapped into the removed slot by the SoA swap-with-last.
    /// Clears <see cref="ClusterSpatialIndexSlot"/> for the removed cluster. Issue #230.
    /// </summary>
    internal void RemoveClusterFromPerCellIndex(int clusterChunkId, int cellKey)
    {
        if (PerCellIndex == null || cellKey < 0 || cellKey >= PerCellIndex.Length)
        {
            return;
        }
        var slot = PerCellIndex[cellKey];
        if (slot == null)
        {
            return;
        }
        if (ClusterSpatialIndexSlot == null || clusterChunkId >= ClusterSpatialIndexSlot.Length)
        {
            return;
        }
        var indexSlot = ClusterSpatialIndexSlot[clusterChunkId];
        if (indexSlot < 0)
        {
            return; // not in the index
        }

        var isStatic = SpatialSlot.FieldInfo.Mode == SpatialMode.Static;
        var tree = isStatic ? slot.StaticTree : slot.DynamicTree;
        if (tree != null)
        {
            // Remove retires this payload's handle to NullHandle and repairs the swapped entry's, both through ClusterSpatialIndexSlot — so unlike the linear
            // path there is no back-pointer fix-up to do here.
            tree.RemoveAt(clusterChunkId);
            TyphonEvent.EmitSpatialCellIndexRemove(cellKey, indexSlot, -1);
            if (tree.ClusterCount <= CellTreeDemoteThreshold)
            {
                DemoteCellHalf(slot, isStatic);
            }
            return;
        }

        var targetIndex = isStatic ? slot.StaticIndex : slot.DynamicIndex;
        if (targetIndex == null)
        {
            return;
        }

        var swappedClusterId = targetIndex.RemoveAt(indexSlot);
        TyphonEvent.EmitSpatialCellIndexRemove(cellKey, indexSlot, swappedClusterId);
        if (swappedClusterId >= 0 && swappedClusterId < ClusterSpatialIndexSlot.Length)
        {
            // The swapped cluster now lives at indexSlot; fix its back-pointer.
            ClusterSpatialIndexSlot[swappedClusterId] = indexSlot;
        }
        ClusterSpatialIndexSlot[clusterChunkId] = -1;
    }

    /// <summary>
    /// Append a migration request to the per-archetype queue. Lazily allocates the backing array on first use
    /// and doubles its capacity on overflow. Issue #229 Phase 3.
    /// </summary>
    /// <remarks>
    /// Called only from the cell-crossing detection loop in <c>DetectClusterMigrations</c> — single-threaded,
    /// no synchronization needed. The typical hot path writes a handful of entries per tick; even on a busy tick
    /// with thousands of migrations the array doubles ~10-12 times total (initial 16 -> 32K).
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnqueueMigration(int sourceClusterChunkId, int sourceSlotIndex, int destCellKey) =>
        EnqueueMigration(new MigrationRequest(sourceClusterChunkId, sourceSlotIndex, destCellKey));

    /// <summary>
    /// Append a fully-formed request — the overload the #872 step-12 repair planner uses, since a repair pins both the destination cluster and the
    /// destination slot and so cannot express itself as a cell key.
    /// </summary>
    /// <inheritdoc cref="EnqueueMigration(int, int, int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void EnqueueMigration(in MigrationRequest request)
    {
        Debug.Assert(!InPrepSlice, "a Prep slice files its crossings through RegisterPrepSliceCrossings, never into the shared queue (#886)");
        if (PendingMigrations == null)
        {
            PendingMigrations = new MigrationRequest[16];
        }
        else if (PendingMigrationCount == PendingMigrations.Length)
        {
            Array.Resize(ref PendingMigrations, PendingMigrations.Length * 2);
        }
        PendingMigrations[PendingMigrationCount++] = request;
    }

    /// <summary>
    /// Grow <see cref="_drainedClusterIds"/> so that <paramref name="required"/> entries fit, on a single thread.
    /// </summary>
    /// <remarks>
    /// <see cref="PreSizeMigrationBuffers"/> sizes this list from <see cref="PendingMigrationCount"/>, on the premise that one migration releases at most
    /// one source slot. The #872 step-12 repair planner runs AFTER that pre-size and breaks the premise from both ends — it files more migrations, and it
    /// consumes drain entries of its own for the empty destinations it allocates. Without a top-up the Migrate phase overflows into
    /// <see cref="RecordClusterDrain"/>'s fallback, which is reached from parallel workers and writes its entry after releasing the grow lock: an entry can
    /// be dropped by a concurrent resize, and a dropped drain record is a cluster nothing will ever free.
    /// </remarks>
    internal void PreSizeDrainedClusterIds(int required)
    {
        if (required <= 0)
        {
            return;
        }

        if (_drainedClusterIds == null)
        {
            _drainedClusterIds = new int[Math.Max(16, required)];
            return;
        }

        if (_drainedClusterIds.Length >= required)
        {
            return;
        }

        var capacity = _drainedClusterIds.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref _drainedClusterIds, capacity);
    }

    /// <summary>
    /// Grow <see cref="PendingMigrations"/> once so that <paramref name="additional"/> more requests fit without a doubling per append.
    /// </summary>
    /// <remarks>
    /// For the #872 step-12 repair planner, which knows its emission count before it emits and runs on Prep — the single-threaded phase every archetype
    /// waits on. Appending 2 000 requests one at a time walked the doubling sequence about seven times, copying ~4 000 entries of 20 bytes for nothing.
    /// </remarks>
    internal void ReservePendingMigrationCapacity(int additional)
    {
        var required = PendingMigrationCount + additional;
        if (PendingMigrations == null)
        {
            PendingMigrations = new MigrationRequest[Math.Max(required, 16)];
            return;
        }

        if (PendingMigrations.Length >= required)
        {
            return;
        }

        var capacity = PendingMigrations.Length;
        while (capacity < required)
        {
            capacity *= 2;
        }

        Array.Resize(ref PendingMigrations, capacity);
    }

    /// <summary>
    /// Bulk-append a worker-local outlier-buffer to <see cref="PendingMigrations"/>. Takes <see cref="_finalizeLock"/> once per slice (review D-2).
    /// Empty buffer = no-op, no lock acquisition.
    /// </summary>
    internal void EnqueueMigrationsBulk(List<MigrationRequest> outlierBuffer)
    {
        if (outlierBuffer == null || outlierBuffer.Count == 0)
        {
            return;
        }

        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref nullCtx);
        try
        {
            var n = outlierBuffer.Count;
            if (PendingMigrations == null)
            {
                var initCap = Math.Max(16, n);
                var p = 1;
                while (p < initCap)
                {
                    p <<= 1;
                }

                PendingMigrations = new MigrationRequest[p];
            }
            else if (PendingMigrationCount + n > PendingMigrations.Length)
            {
                var newLen = PendingMigrations.Length * 2;
                while (newLen < PendingMigrationCount + n)
                {
                    newLen *= 2;
                }

                Array.Resize(ref PendingMigrations, newLen);
            }
            for (var i = 0; i < n; i++)
            {
                PendingMigrations[PendingMigrationCount++] = outlierBuffer[i];
            }
        }
        finally
        {
            _finalizeLock.Exit();
        }
        outlierBuffer.Clear();
    }

    /// <summary>
    /// Allocate a new cluster from both segments (lockstep). Initializes to zero and adds to active list.
    /// </summary>
    public int AllocateNewCluster(ChangeSet changeSet)
    {
        int chunkId;
        if (ClusterSegment != null)
        {
            chunkId = ClusterSegment.AllocateChunk(true, changeSet);
        }
        else
        {
            // Pure-Transient: allocate from TransientStore only
            chunkId = TransientSegment.AllocateChunk(true);
        }

        // Dual-segment: allocate matching chunk in TransientSegment (lockstep ensures same chunk IDs)
        if (TransientSegment != null && ClusterSegment != null)
        {
            var transientChunkId = TransientSegment.AllocateChunk(true);
            Debug.Assert(transientChunkId == chunkId, $"Dual-segment chunk ID mismatch: PS={chunkId}, TS={transientChunkId}");
        }

        AddToActiveList(chunkId);
        return chunkId;
    }

    /// <summary>
    /// <see cref="AllocateNewCluster"/> under the per-archetype finalize latch — the form every caller reached from a commit must use.
    /// </summary>
    /// <remarks>
    /// The body is not safe to run twice at once: the two segments' <c>AllocateChunk</c> calls have to interleave with nobody, or the ids stop matching, and
    /// <see cref="AddToActiveList"/> writes <c>ActiveClusterIds[ActiveClusterCount]</c> before publishing the incremented count, which loses a cluster
    /// outright when two allocators do it together. <c>ClaimSlotInCell</c> has always taken this latch around exactly this work; the plain
    /// <c>ClaimSlot</c> overloads did not, which is #842. Nothing inside takes the latch itself, so there is no re-entrancy here —
    /// <see cref="NoteClusterBorn"/>, the one path that would, is not called for a freshly allocated cluster (see <c>FreshClusterStaysUnknown</c>).
    /// </remarks>
    private int AllocateNewClusterLatched(ChangeSet changeSet)
    {
        ref var nullCtx = ref Unsafe.NullRef<WaitContext>();
        _finalizeLock.Enter(ref nullCtx);
        try
        {
            return AllocateNewCluster(changeSet);
        }
        finally
        {
            _finalizeLock.Exit();
        }
    }

    /// <summary>Add a cluster chunk ID to the active list.</summary>
    /// <remarks>
    /// #582 face 2. Worker threads read <see cref="ActiveClusterIds"/> and <see cref="ActiveClusterCount"/> live, concurrently with this. The publication
    /// order is what makes a (count, array) pair usable: the grown array is released FIRST, the count that indexes into it SECOND, so a reader that acquires
    /// a given count is guaranteed to see an array at least that long. Readers must load the pair in the mirror order — count, then array — and both loads
    /// must be `Volatile.Read` (see `TyphonRuntime.ReadActiveClusterList`). Loading the array first admits a plain interleaving that needs no reordering at
    /// all to fault: old array of length 16, concurrent resize, count read as 17, index 16 of the old array.
    /// <para>
    /// This orders the GROWTH of the list. It does not make the list safe to walk against a concurrent <see cref="RemoveFromActiveList"/>, whose
    /// swap-with-last can still show a walker one cluster twice and skip another — #582 face 1, which needs a real snapshot protocol, not an ordering fix.
    /// </para>
    /// </remarks>
    public void AddToActiveList(int chunkId)
    {
        if (ActiveClusterCount >= ActiveClusterIds.Length)
        {
            Array.Resize(ref ActiveClusterIds, ActiveClusterIds.Length * 2);
        }

        // The array store above is a plain store, deliberately: the release below is what orders it. A Volatile.Write cannot sink a preceding store past
        // itself, so a reader that ACQUIRES this count is guaranteed to see the grown array. Caching the array in a local first — the obvious way to write
        // this — is what must not be done: it widens the writer's own (array, count) window and produced an IndexOutOfRange in parallel spawn.
        ActiveClusterIds[ActiveClusterCount] = chunkId;
        Volatile.Write(ref ActiveClusterCount, ActiveClusterCount + 1);
        // Issue #231: any change to the active cluster set invalidates the tier index.
        ClusterSetVersion++;
        // Issue #233: ensure dormancy arrays cover the new chunkId, initialize to Active/0.
        if (SleepStates != null)
        {
            EnsureSleepStateCapacity(chunkId + 1);
            SleepStates[chunkId] = ClusterSleepState.Active;
            SleepCounters[chunkId] = 0;
        }
    }

    /// <summary>Remove a cluster chunk ID from the active list (swap-with-last, O(1)).</summary>
    /// <remarks>
    /// <b>Re-sorting the list in Prep was built, measured and REFUTED (#886 lead B) — do not re-add it without a new measurement.</b> The reasoning was
    /// sound on its face: every swap-with-last displaces one id to a random earlier index, nothing ever puts it back, the AabbRefresh walk slices this list by
    /// index range through a 32-page accessor window, and a fresh world (ascending by construction) is the only world a 40-tick benchmark ever sees. The
    /// partition harness gained <c>--churn</c> to age a world first, and the aging is real: 1 000 adjacent inversions on 2 025 clusters after 60 warm-up
    /// ticks. An ascending list was then measured against the scrambled one, interleaved, five pairs, Matrix P at 25 % moving and W = 8: <b>sorted every tick
    /// 4.91 ms against 3.87 ms scrambled; sorted once and left alone 4.26 ms against 4.03 ms</b> — slower in ten of ten pairs, with CPU up in every phase,
    /// not just AabbRefresh. On a fresh world the two arms are equal (5.96 / 5.90). The page-window argument does not survive contact: whatever locality an
    /// ascending walk buys, the order the allocator produced is worth more, and the mechanism was not pinned down. What is known is that the answer is
    /// "no", and the harness arm that shows it is <c>--churn 0.05 --churn-ticks 60</c>.
    /// </remarks>
    public void RemoveFromActiveList(int chunkId)
    {
        for (var i = 0; i < ActiveClusterCount; i++)
        {
            if (ActiveClusterIds[i] == chunkId)
            {
                // Issue #233: if the removed cluster was sleeping or wake-pending, adjust the count.
                // WakePending clusters are still counted in SleepingClusterCount (they were incremented at the
                // Active→Sleeping transition and decremented only when WakePending→Active completes).
                if (SleepStates != null && chunkId < SleepStates.Length)
                {
                    var sleepState = SleepStates[chunkId];
                    if (sleepState == ClusterSleepState.Sleeping || sleepState == ClusterSleepState.WakePending)
                    {
                        SleepingClusterCount--;
                    }
                }

                ActiveClusterIds[i] = ActiveClusterIds[ActiveClusterCount - 1];
                // Released, to pair with the acquiring readers described on AddToActiveList. Shrinking is the benign direction — a reader that sees the
                // stale larger count reads an index that is still in range — but leaving one store of the pair plain would make the pairing accidental.
                Volatile.Write(ref ActiveClusterCount, ActiveClusterCount - 1);

                // If the removed cluster was the free head, reset
                if (FreeClusterHead == chunkId)
                {
                    FreeClusterHead = -1;
                }

                // Issue #231: any change to the active cluster set invalidates the tier index.
                ClusterSetVersion++;
                return;
            }
        }
    }

    /// <summary>
    /// Release one occupied slot of a Versioned/Transient mixed cluster on the persistent segment. Atomically clears the slot's OccupancyBit + EnabledBits + EntityKey
    /// via <see cref="ClearSlotMetadata"/>, maintains the per-cell entity counter when the slot was actually occupied, and — if the slot was the cluster's last occupant —
    /// either finalises the cluster immediately or defers finalisation to the per-tick fence depending on <paramref name="deferFinalize"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Slot-level work.</b> <see cref="ClearSlotMetadata"/> returns the occupancy bitmap as it was BEFORE the clear, so the method can tell (a) whether the
    /// slot was actually occupied (no-op release on a free slot is silently absorbed — no cell-count decrement, no drain handling) and (b) whether clearing
    /// this single bit transitioned the cluster from non-empty to empty in one observation. Cell-entity bookkeeping
    /// (<see cref="DecrementCellEntityCountOnRelease"/>) fires only on the genuinely-was-occupied path to keep <see cref="CellState.EntityCount"/>
    /// consistent with the occupancy bitmaps under repeated-release idempotence.
    /// </para>
    /// <para>
    /// <b>Drain branches.</b> When this release drains the cluster (last bit cleared), the cluster must exit the active set, get removed from its cell's pool
    /// segment, and have its chunks returned to both <see cref="ClusterSegment"/> and <see cref="TransientSegment"/>. Two paths:
    /// <list type="bullet">
    ///   <item><b><paramref name="deferFinalize"/> = false</b> (default — single-threaded callers like <c>Transaction.Destroy</c>): finalise immediately.
    ///     Safe because no concurrent claimer can CAS a slot back in between our last-bit-clear and the segment free.</item>
    ///   <item><b><paramref name="deferFinalize"/> = true</b> (parallel-fence migration path, review C-1): record the drained cluster via
    ///     <see cref="RecordClusterDrain"/> and let <c>FinalizeArchetypeFence</c> do the finalize+free pass after all workers have quiesced. Skipping immediate
    ///     free closes the race where another worker mid-<c>ClaimSlotInCell</c> has already CAS-claimed a slot in this cluster between our last-bit-clear and
    ///     any subsequent lock — finalising now would free a chunk the claimer is about to write into.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Free-list hint.</b> On a release that does NOT drain the cluster, the cluster still has free capacity and is a good candidate for the next claim. If
    /// <see cref="FreeClusterHead"/> is currently unset (-1), it's biased to this cluster so the next <c>ClaimSlotInCell</c> hits an O(1) lookup. This is a
    /// hint only — the claim path validates the head still has a free bit before using it.
    /// </para>
    /// <para>
    /// <b>Threading.</b> There is no mutex on either path. When <paramref name="deferFinalize"/> = false the caller is single-threaded for this
    /// <see cref="ArchetypeClusterState"/> (<c>Transaction.Destroy</c> and friends) — that, not a lock, is what makes the inline finalize-and-free safe, so a
    /// concurrent caller on this path would be a defect no lock currently prevents. When deferred, the caller is the parallel fence path where workers operate
    /// on disjoint clusters; <see cref="RecordClusterDrain"/> uses <see cref="System.Threading.Interlocked.Increment(ref int)"/> to reserve a slot in the
    /// per-archetype drain list, so concurrent drain records are safe.
    /// </para>
    /// </remarks>
    /// <param name="accessor">Chunk accessor bound to the <see cref="PersistentStore"/> segment — provides the in-memory address of the cluster chunk for
    /// direct metadata mutation.</param>
    /// <param name="clusterChunkId">Chunk id of the cluster containing the slot to release.</param>
    /// <param name="slotIndex">Zero-based index of the slot within the cluster (0..<see cref="ArchetypeClusterInfo.ClusterSize"/>-1). The slot's occupancy bit,
    /// enabled bits, and entity key are all cleared.</param>
    /// <param name="changeSet">Change set threaded through for WAL / dirty-page bookkeeping on the persistent segment writes performed
    /// by <see cref="ClearSlotMetadata"/>.</param>
    /// <param name="grid">
    /// Optional spatial grid. When non-null <em>and</em> <see cref="ClusterCellMap"/> is populated for the released cluster, this method maintains the cell
    /// descriptor: <see cref="CellState.EntityCount"/> always decrements (only on genuinely-was-occupied releases), and a going-empty cluster is removed
    /// from its cell's pool segment at finalise time. Pass <c>null</c> when the archetype has no spatial slot — the cell bookkeeping is then a no-op.
    /// </param>
    /// <param name="deferFinalize">
    /// When <c>true</c>, postpones the drain finalisation (cell removal + active-list eviction + segment free) so the per-tick fence can run it after the
    /// parallel migration pass completes. Set by the cluster-migration / parallel-fence call sites; default <c>false</c> for single-threaded callers
    /// (<c>Transaction.Destroy</c>, etc.) that can safely finalise inline.
    /// </param>
    public void ReleaseSlot(ref ChunkAccessor<PersistentStore> accessor, int clusterChunkId, int slotIndex, ChangeSet changeSet, SpatialGrid grid = null,
        bool deferFinalize = false)
    {
        var clusterBase = accessor.GetChunkAddress(clusterChunkId, true);

        // Release SV ComponentCollection buffers held in this slot BEFORE clearing it — but only on a true destroy.
        // Migration passes deferFinalize:true and is a MOVE: the handle was byte-copied to the destination slot, so the
        // buffer must NOT be freed here. SV CC has no revision chain; the cluster slot is the buffer's sole owner.
        if (!deferFinalize && CollectionSlots != null)
        {
            ReleaseSlotCollections(clusterBase, slotIndex, changeSet);
        }

        var slotMask = 1UL << slotIndex;
        var prevOccupancy = ClearSlotMetadata(clusterBase, slotIndex);
        var wasOccupied = (prevOccupancy & slotMask) != 0;
        var clusterDrained = wasOccupied && (prevOccupancy & ~slotMask) == 0;

        if (wasOccupied)
        {
            // resetCursor: !deferFinalize — serial releases reset the cursor for immediate reuse; parallel-migration releases skip it (see the method doc).
            DecrementCellEntityCountOnRelease(grid, clusterChunkId, !deferFinalize);
        }

        // ── A vacated slot needs an AABB shrink, and NOTHING else records that ──────────────────────────────────────
        //
        // A destroy sets no dirty bit: ReleaseSlot never calls SetDirty, and even if the slot had been written earlier
        // in the tick, Prep step 2 ANDs the dirty word with occupancy, so a freed slot's bit is masked away before the
        // refresh ever sees it. It sets no process bit either — that is WriteSpatial's. So a destroyed entity's position
        // stayed inside its cluster's bound until something unrelated happened to write that cluster, which on a settled
        // cell may be never.
        //
        // That was survivable only because the non-barrier refresh recomputed every active cluster unconditionally and
        // silently absorbed it. It stops being survivable the moment that walk is gated (ClusterNeedsAabbRecompute), so
        // the flag goes in at the site that actually knows a slot was vacated. It is also the right fix for the barrier
        // arm on its own terms — that arm has always visited only ClusterProcessBitmap and has therefore always missed
        // this, the same gap FlagClusterForShrinkRefresh's own remarks describe for migration sources.
        //
        // `!clusterDrained`: a cluster that just lost its LAST entity is being freed or queued for finalisation, so there
        // is no bound left to tighten and flagging one would point the refresh at a chunk id about to be recycled.
        // `!deferFinalize`: the migration path already flags its source unconditionally two statements after its own
        // ReleaseSlot call (DatabaseEngine.ClusterMigration.cs), so flagging here would be a second CAS per migration.
        if (wasOccupied && !deferFinalize)
        {
            // Counted even when the cluster drains: the entity is gone either way, and the shadow drain's gate asks "could a destroy have happened", not
            // "did a cluster survive one".
            Interlocked.Increment(ref SlotReleasesThisTick);
        }

        if (wasOccupied && !clusterDrained && !deferFinalize)
        {
            FlagClusterShrinkAxesOnly(clusterChunkId);
        }

        if (clusterDrained)
        {
            if (deferFinalize)
            {
                // Parallel-fence migration path (review C-1). Defer finalize-and-free to FinalizeArchetypeFence — freeing here would race with a concurrent
                // ClaimSlotInCell that may have just CAS-claimed a slot in this cluster between our last-bit-clear and any lock acquire. The deferred list is
                // per-archetype, slot reservation lock-free via Interlocked.Increment.
                RecordClusterDrain(clusterChunkId);
            }
            else
            {
                // Single-threaded caller (Transaction.Destroy, etc.) — safe to finalize immediately.
                FinaliseEmptyClusterCellState(grid, clusterChunkId);
                RemoveFromActiveList(clusterChunkId);
                ResetClusterVisibility(clusterChunkId);   // the id is about to be recyclable — see ResetClusterVisibility
                ClusterSegment.FreeChunk(clusterChunkId);
                TransientSegment?.FreeChunk(clusterChunkId);
            }
        }
        else if (FreeClusterHead < 0)
        {
            FreeClusterHead = clusterChunkId;
        }
    }

    /// <summary>
    /// Release a slot for pure-Transient archetypes (no PersistentStore segment).
    /// </summary>
    public void ReleaseSlot(ref ChunkAccessor<TransientStore> accessor, int clusterChunkId, int slotIndex, SpatialGrid grid = null, bool deferFinalize = false)
    {
        var clusterBase = accessor.GetChunkAddress(clusterChunkId, true);

        var slotMask = 1UL << slotIndex;
        var prevOccupancy = ClearSlotMetadata(clusterBase, slotIndex);
        var wasOccupied = (prevOccupancy & slotMask) != 0;
        var clusterDrained = wasOccupied && (prevOccupancy & ~slotMask) == 0;

        if (wasOccupied)
        {
            // resetCursor: !deferFinalize — serial releases reset the cursor for immediate reuse; parallel-migration releases skip it (see the method doc).
            DecrementCellEntityCountOnRelease(grid, clusterChunkId, !deferFinalize);
        }

        // Same reasoning as the PersistentStore overload above — see the block there. A pure-Transient archetype can carry a Dynamic spatial slot, so it
        // reaches the same refresh pass and needs the same signal; FlagClusterForShrinkRefresh null-checks both arrays, so this is free for the rest.
        if (wasOccupied && !deferFinalize)
        {
            // Counted even when the cluster drains: the entity is gone either way, and the shadow drain's gate asks "could a destroy have happened", not
            // "did a cluster survive one".
            Interlocked.Increment(ref SlotReleasesThisTick);
        }

        if (wasOccupied && !clusterDrained && !deferFinalize)
        {
            FlagClusterShrinkAxesOnly(clusterChunkId);
        }

        if (clusterDrained)
        {
            if (deferFinalize)
            {
                RecordClusterDrain(clusterChunkId);
            }
            else
            {
                FinaliseEmptyClusterCellState(grid, clusterChunkId);
                RemoveFromActiveList(clusterChunkId);
                ResetClusterVisibility(clusterChunkId);   // the id is about to be recyclable — see ResetClusterVisibility
                TransientSegment.FreeChunk(clusterChunkId);
            }
        }
        else if (FreeClusterHead < 0)
        {
            FreeClusterHead = clusterChunkId;
        }
    }

    /// <summary>
    /// Build the SingleVersion <c>ComponentCollection</c> descriptor (<see cref="CollectionSlots"/>) used by <c>ReleaseSlot</c> to free CC buffers on destroy.
    /// Only SingleVersion CC fields are tracked: the cluster slot is their sole owner. Versioned CC is owned by content chunks (released via the revision
    /// cleanup); Transient CC is rejected at registration. No-op for archetypes without an SV CC field (leaves <see cref="CollectionSlots"/> null).
    /// </summary>
    public void InitializeCollections(ComponentTable[] slotToTable)
    {
        List<ClusterCollectionSlot> slots = null;
        for (var slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            if (table == null || table.StorageMode != StorageMode.SingleVersion || !table.HasCollections)
            {
                continue;
            }

            // CollectionFieldInfo.OffsetInComponentStorage is the offset within the component's pure data; cluster slots have no overhead, so it IS the
            // slot-relative field offset — which is why the table's own descriptor can be shared here rather than copied into a cluster-local twin.
            (slots ??= []).Add(new ClusterCollectionSlot { Slot = slot, Fields = table.CollectionFields });
        }

        CollectionSlots = slots?.ToArray();
    }

    /// <summary>
    /// Release the SingleVersion ComponentCollection buffers held in one cluster slot. Called from <c>ReleaseSlot</c> on a true destroy (not migration),
    /// before the slot data is cleared.
    /// </summary>
    private void ReleaseSlotCollections(byte* clusterBase, int slotIndex, ChangeSet changeSet)
    {
        var layout = Layout;
        foreach (var cs in CollectionSlots)
        {
            var compBase = clusterBase + layout.ComponentOffset(cs.Slot) + slotIndex * layout.ComponentSize(cs.Slot);
            foreach (var f in cs.Fields)
            {
                var bufferId = *(int*)(compBase + f.OffsetInComponentStorage);
                if (bufferId != 0)
                {
                    var ca = f.Vsbs.Segment.CreateChunkAccessor(changeSet);
                    f.Vsbs.BufferRelease(bufferId, ref ca);
                    ca.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Decrement the cell's entity count when a slot is released. No-op if cluster is unmapped.
    /// <para><paramref name="resetCursor"/> controls the scan-cursor reset: on the serial release path (<c>Transaction.Destroy</c>, <c>deferFinalize</c>
    /// false) we reset to 0 so the freed slot is immediately reusable by the next claim. On the parallel-fence migration path (<c>deferFinalize</c> true)
    /// the reset is SKIPPED — releases there touch arbitrary, non-worker-exclusive source cells, so resetting would zero the cursors of destination cells
    /// other workers are actively claiming into (cursor thrash) and pound a shared array (false sharing). Phase-2 of
    /// <see cref="ClaimSlotInCell(int, ref ChunkAccessor{PersistentStore}, ChangeSet, SpatialGrid, long)"/> recovers any slot freed behind the cursor, so
    /// skipping the reset costs at most a redundant scan, never a missed free slot.</para>
    /// </summary>
    private void DecrementCellEntityCountOnRelease(SpatialGrid grid, int clusterChunkId, bool resetCursor)
    {
        if (grid == null || ClusterCellMap == null || clusterChunkId >= ClusterCellMap.Length)
        {
            return;
        }
        var cellKey = ClusterCellMap[clusterChunkId];
        if (cellKey < 0)
        {
            return;
        }
        Interlocked.Decrement(ref grid.GetCell(cellKey).EntityCount);

        if (resetCursor)
        {
            // Serial release — a slot just freed up in this cell; reset the scan cursor so the next ClaimSlotInCell re-scans from 0 and immediately reuses
            // the freed slot (or a free slot in a cluster the swap-with-last RemoveCluster shuffled ahead of the old cursor).
            CellClusterPool?.ResetScanCursor(cellKey);
        }
    }

    /// <summary>Detach an empty cluster from this archetype's per-cell claim list and clear its cell mapping.</summary>
    private void FinaliseEmptyClusterCellState(SpatialGrid grid, int clusterChunkId)
    {
        if (grid == null || ClusterCellMap == null || clusterChunkId >= ClusterCellMap.Length)
        {
            return;
        }
        var cellKey = ClusterCellMap[clusterChunkId];
        if (cellKey < 0)
        {
            return;
        }
        // Issue #229 Q10: per-archetype pool removal. Only decrements the global CellState.ClusterCount if the pool actually owned this cluster id.
        // Both callers are serial for this archetype — DrainPendingClusterFinalizations runs once per archetype after the fence phase barriers, and the
        // inline ReleaseSlot path has a single-threaded caller — so the CellClusterPool mutation needs no lock. (It does NOT run under _finalizeLock; that
        // latch guards growth/append only.) The cell descriptor counter is shared ACROSS archetypes, which are not serialized against each other, so that
        // one still needs Interlocked.
        if (CellClusterPool.RemoveCluster(cellKey, clusterChunkId))
        {
            Interlocked.Decrement(ref grid.GetCell(cellKey).ClusterCount);
        }

        // Issue #230 Phase 1: also remove from the per-cell cluster AABB index and reset the cluster's stored AABB. Runs before we clear ClusterCellMap so
        // RemoveClusterFromPerCellIndex can look up the cell key internally.
        RemoveClusterFromPerCellIndex(clusterChunkId, cellKey);
        if (ClusterAabbs != null && clusterChunkId < ClusterAabbs.Length)
        {
            ClusterAabbs[clusterChunkId] = ClusterSpatialAabb.Empty;
        }

        ClusterCellMap[clusterChunkId] = -1;
    }

    /// <summary>
    /// Atomically clear EnabledBits, OccupancyBit, and EntityId for a slot (store-agnostic pointer math). Returns the PRE-AND occupancy word so the caller
    /// can detect "this clear flipped the last bit" via <c>(prev &amp; slotMask) != 0 &amp;&amp; (prev &amp; ~slotMask) == 0</c>. The parallel-fence migration
    /// path uses this last-bit-wins signal to decide which worker <see cref="RecordClusterDrain">records the drain</see>; no worker finalizes anything, and
    /// no lock is entered on the strength of it.
    /// </summary>
    /// <remarks>
    /// All bit mutations use <see cref="Interlocked.And(ref long, long)"/> so concurrent releases of different slots in the same cluster (parallel workers
    /// handling cell-partitioned migrations whose sources share a cluster) compose without lost updates. The EntityId scalar write is independent (different
    /// 8-byte slot per release) so it stays a plain store.
    /// </remarks>
    private ulong ClearSlotMetadata(byte* clusterBase, int slotIndex)
    {
        var slotMask = 1L << slotIndex;
        var inverseMask = ~slotMask;

        for (var slot = 0; slot < Layout.ComponentCount; slot++)
        {
            Interlocked.And(ref *(long*)(clusterBase + Layout.EnabledBitsOffset(slot)), inverseMask);
        }

        var prevOccupancy = (ulong)Interlocked.And(ref *(long*)clusterBase, inverseMask);

        *(long*)(clusterBase + Layout.EntityIdsOffset + slotIndex * 8) = 0;

        return prevOccupancy;
    }

    /// <summary>
    /// Initialize per-archetype B+Tree index infrastructure from the component tables.
    /// Called after cluster state creation for archetypes with <see cref="ArchetypeMetadata.HasClusterIndexes"/>.
    /// </summary>
    /// <param name="slotToTable">This archetype's component tables, by slot.</param>
    /// <param name="indexSegment">Persisted 256-byte-stride segment for SingleVersion / Versioned trees.</param>
    /// <param name="string64IndexSegment">Persisted String64-stride segment; null unless a non-Transient String64 field is indexed.</param>
    /// <param name="transientIndexSegment">Heap-backed 256-byte-stride segment for Transient trees; null unless a Transient field is indexed (#655).</param>
    /// <param name="transientString64IndexSegment">Heap-backed String64-stride segment; null unless a Transient String64 field is indexed.</param>
    /// <param name="load">Reopen a persisted directory rather than creating trees. Never applies to the Transient segments — nothing persisted them.</param>
    /// <param name="changeSet">Change set for the persisted segments' page writes.</param>
    public void InitializeIndexes(ComponentTable[] slotToTable, ChunkBasedSegment<PersistentStore> indexSegment,
        ChunkBasedSegment<PersistentStore> string64IndexSegment, ChunkBasedSegment<TransientStore> transientIndexSegment,
        ChunkBasedSegment<TransientStore> transientString64IndexSegment, bool load, ChangeSet changeSet)
    {
        IndexSegment = indexSegment;
        IndexSegmentString64 = string64IndexSegment;
        TransientIndexSegment = transientIndexSegment;
        TransientIndexSegmentString64 = transientString64IndexSegment;

        var slotCount = 0;
        var transientSlotCount = 0;
        for (var slot = 0; slot < slotToTable.Length; slot++)
        {
            var infos = slotToTable[slot].IndexedFieldInfos;
            if (infos == null || infos.Length == 0)
            {
                continue;
            }
            if (slotToTable[slot].StorageMode == StorageMode.Transient)
            {
                transientSlotCount++;
            }
            else
            {
                slotCount++;
            }
        }

        Debug.Assert(transientSlotCount == 0 || transientIndexSegment != null,
            "An archetype with an indexed Transient field needs its heap-backed index segment — eligibility detection and allocation have diverged (#655).");

        IndexSlots = new ClusterIndexSlot<PersistentStore>[slotCount];
        TransientIndexSlots = transientSlotCount > 0 ? new ClusterIndexSlot<TransientStore>[transientSlotCount] : null;
        var idx = 0;
        var transientIdx = 0;
        // Sequential counter for AllowMultiple indexed fields across ALL component slots in this archetype.
        // Drives each field's MultiFieldIndex, which selects the corresponding section in the cluster layout's elementId tail
        // (see ArchetypeClusterInfo.IndexElementIdOffset). Must match the flat count passed to ArchetypeClusterInfo.Compute at archetype registration time.
        // Transient slots participate too since #655 — the tail is per-entity in the cluster, and a Transient entity occupies the same ClusterLocation.
        var multiFieldCounter = 0;
        for (var slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            var infos = table.IndexedFieldInfos;
            if (infos == null || infos.Length == 0)
            {
                continue;
            }

            if (table.StorageMode == StorageMode.Transient)
            {
                TransientIndexSlots![transientIdx++] = BuildIndexSlot(table, slot, TransientIndexSegment, TransientIndexSegmentString64, false,
                    null, ref multiFieldCounter);
                continue;
            }

            IndexSlots[idx++] = BuildIndexSlot(table, slot, indexSegment, string64IndexSegment, load, changeSet, ref multiFieldCounter);
        }

        // Sanity: the MultiFieldIndex counter must match the count supplied to ArchetypeClusterInfo.Compute.
        // A mismatch means the cluster layout tail is mis-sized or fields will read the wrong slots.
        Debug.Assert(multiFieldCounter == Layout.MultipleIndexedFieldCount,
            $"Cluster elementId tail: InitializeIndexes counted {multiFieldCounter} AllowMultiple fields but Layout reserves {Layout.MultipleIndexedFieldCount}");

        ClusterShadowBitmap = new DirtyBitmap(Math.Max(64, PrimarySegmentCapacity * 64));

        // The flattened (slot, field) map the tick fence's IndexMassUpdate phase stages against. Persistent slots only: the migration path that produces the
        // staged entries writes IndexSlots and nothing else (DatabaseEngine.ClusterMigration.cs, the `hasIdxAccessor && IndexSlots != null` block).
        var fieldCount = 0;
        for (var s = 0; s < IndexSlots.Length; s++)
        {
            fieldCount += IndexSlots[s].Fields?.Length ?? 0;
        }

        var fieldRefs = new IndexUpdateStaging.FieldRef[fieldCount];
        var fid = 0;
        for (var s = 0; s < IndexSlots.Length; s++)
        {
            var fields = IndexSlots[s].Fields;
            for (var f = 0; f < (fields?.Length ?? 0); f++)
            {
                fieldRefs[fid++] = new IndexUpdateStaging.FieldRef(s, f);
            }
        }

        IndexUpdates = new IndexUpdateStaging(fieldRefs);
    }

    /// <summary>
    /// Builds one component slot's index metadata + B+Trees against <paramref name="defaultSegment"/> / <paramref name="string64Segment"/>.
    /// </summary>
    /// <remarks>
    /// Generic over the store so the SingleVersion/Versioned and Transient walks are the same code rather than a copy that drifts (#655). The only asymmetry
    /// is at the call site: Transient trees are always created, never loaded, because nothing persisted them.
    /// </remarks>
    private ClusterIndexSlot<TStore> BuildIndexSlot<TStore>(ComponentTable table, int slot, ChunkBasedSegment<TStore> defaultSegment,
        ChunkBasedSegment<TStore> string64Segment, bool load, ChangeSet changeSet, ref int multiFieldCounter)
        where TStore : struct, IPageStore
    {
        var infos = table.IndexedFieldInfos;
        var fields = new ClusterIndexField<TStore>[infos.Length];
        var shadowBuffers = new FieldShadowBuffer[infos.Length];
        var stats = new IndexStatistics[infos.Length];

        // Iterate component definition fields to find indexed ones (in stable order matching IndexedFieldInfos)
        var fi = 0;
        for (var i = 0; i < table.Definition.MaxFieldId && fi < infos.Length; i++)
        {
            var fieldDef = table.Definition[i];
            if (fieldDef == null || !fieldDef.HasIndex)
            {
                continue;
            }

            ref var ifi = ref infos[fi];
            // FieldOffset in cluster = field offset within pure component data (no ComponentOverhead in clusters)
            var clusterFieldOffset = ifi.OffsetToField - table.ComponentOverhead;
            // Node stride is per key type: String64 nodes don't fit the 256-byte segment (#658). Mirrors ComponentTable.CreateIndexForField.
            var fieldSegment = fieldDef.Type == FieldType.String64 ? string64Segment : defaultSegment;
            Debug.Assert(fieldSegment != null,
                $"Archetype index segment missing for field '{fieldDef.Name}' of type {fieldDef.Type} — the String64 segment is allocated only when a "
                + "String64 field is indexed, so eligibility detection and allocation have diverged.");
            // Key on (fieldId, slot), NOT fieldId alone: this segment is shared by every component slot in the archetype and field ids restart at 0 per
            // component, so two components each indexing their field #0 would otherwise register two entries with the same key — and on reopen both
            // trees would resolve to the first one's root (#657).
            var indexKey = new BTreeStableKey((short)fieldDef.FieldId, (short)slot);
            var btree = ComponentTable.CreateIndexForFieldCore(fieldDef, indexKey, load, fieldSegment, changeSet);
            // AllowMultiple fields claim the next sequential slot in the cluster's elementId tail.
            // Single-value fields don't allocate tail space and use MultiFieldIndex = -1.
            var multiFieldIndex = ifi.AllowMultiple ? multiFieldCounter++ : -1;
            fields[fi] = new ClusterIndexField<TStore>
            {
                FieldOffset = clusterFieldOffset,
                FieldSize = ifi.Size,
                Index = btree,
                AllowMultiple = ifi.AllowMultiple,
                // Zone maps are a numeric min/max per cluster used to prune Path-B scans; a 64-byte String64 key has no such summary,
                // so it gets none. Every producer uses `ZoneMap?.` and both consumers null-check, so a null map simply means
                // "no cluster pruning for this field" — the correct behaviour rather than a special case (#658).
                ZoneMap = fieldDef.Type == FieldType.String64 ? null
                    : new ZoneMapArray(PrimarySegmentCapacity, ifi.Size,
                        fieldDef.Type == FieldType.Float, fieldDef.Type == FieldType.Double,
                        (fieldDef.Type & FieldType.Unsigned) != 0),
                MultiFieldIndex = multiFieldIndex,
            };
            shadowBuffers[fi] = new FieldShadowBuffer();
            stats[fi] = new IndexStatistics(btree);
            fi++;
        }

        return new ClusterIndexSlot<TStore>
        {
            Slot = slot,
            Fields = fields,
            ShadowBuffers = shadowBuffers,
            Stats = stats,
        };
    }


    /// <summary>
    /// Rebuild per-archetype B+Tree indexes from cluster data (scan all occupied entities).
    /// Used on reopen when index segment is not persisted or is corrupted.
    /// </summary>
    /// <returns>
    /// The number of index entries dropped because the recovered data could not satisfy a UNIQUE constraint (#710). Zero on every healthy rebuild.
    /// </returns>
    public int RebuildIndexesFromData(ChangeSet changeSet)
    {
        if (IndexSlots == null || IndexSlots.Length == 0)
        {
            return 0;
        }

        var uniqueConflicts = 0;

        // Index rebuild reads from primary segment (SV/V data — Transient excluded from IndexSlots)
        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        var idxAccessor = IndexSegment.CreateChunkAccessor(changeSet);
        // A field's nodes live in whichever segment its stride requires, so rebuild needs an accessor per segment, not per archetype (#658).
        var hasString64 = IndexSegmentString64 != null;
        var idxAccessorS64 = hasString64 ? IndexSegmentString64.CreateChunkAccessor(changeSet) : default;
        try
        {
            for (var c = 0; c < ActiveClusterCount; c++)
            {
                var chunkId = ActiveClusterIds[c];
                var clusterBase = clusterAccessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;

                while (occupancy != 0)
                {
                    var slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    var clusterLocation = chunkId * 64 + slotIndex;

                    for (var s = 0; s < IndexSlots.Length; s++)
                    {
                        ref var ixSlot = ref IndexSlots[s];
                        var compBase = clusterBase + Layout.ComponentOffset(ixSlot.Slot);
                        var compSize = Layout.ComponentSize(ixSlot.Slot);
                        for (var f = 0; f < ixSlot.Fields.Length; f++)
                        {
                            ref var field = ref ixSlot.Fields[f];
                            var fieldPtr = compBase + slotIndex * compSize + field.FieldOffset;

                            int elementId;
                            try
                            {
                                elementId = hasString64 && ReferenceEquals(field.Index.Segment, IndexSegmentString64)
                                    ? field.Index.Add(fieldPtr, clusterLocation, ref idxAccessorS64)
                                    : field.Index.Add(fieldPtr, clusterLocation, ref idxAccessor);
                            }
                            catch (UniqueConstraintViolationException)
                            {
                                // #710. RB-01 says derived structures are rebuilt from primary data; it has no clause for primary data that CANNOT satisfy
                                // the constraint. That state is reachable and legitimate: a hard crash under TickFence loses up to one tick of SingleVersion
                                // VALUES while keeping every lifecycle record, so an archetype can come back with all its entities and all their keys zeroed.
                                // Rebuilding a unique index over N identical keys then threw out of InitializeArchetypes, uncatchably, and the state is on
                                // disk — so the next open repeated it. Losing ≤1 tick of values is the documented trade; a database that will not open is not.
                                //
                                // Skipping the entry keeps the entity itself alive and reachable by scan, which is the honest outcome: the value the key was
                                // derived from is gone, so there is no key to index. The caller logs the count — silence here would trade an unopenable
                                // database for a quietly incomplete index, which is a worse bargain.
                                //
                                // The throw-per-conflict cost is accepted: this runs once, at open, only on a database that has already lost data.
                                uniqueConflicts++;
                                continue;
                            }

                            // Rebuild writes a fresh elementId into the cluster tail, overwriting any stale
                            // value from the previous (torn-down) BTree state. Issue #229 Phase 3.
                            if (field.AllowMultiple)
                            {
                                *(int*)(clusterBase + Layout.IndexElementIdOffset(field.MultiFieldIndex, slotIndex)) = elementId;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            if (hasString64)
            {
                idxAccessorS64.Dispose();
            }
            idxAccessor.Dispose();
            clusterAccessor.Dispose();
        }

        return uniqueConflicts;
    }

    /// <summary>
    /// Initialize per-archetype spatial state (issue #230 Phase 3 Option B, Q10 multi-archetype resolution). Sets up the <see cref="SpatialSlot"/>
    /// metadata, the <see cref="ClusterDirtyRing"/>, and the per-archetype <see cref="CellClusterPool"/>. The per-cell index itself is lazily populated
    /// by spawn/migration hooks (or rebuilt from cluster data by <see cref="RebuildCellState"/> + <see cref="RebuildClusterAabbs"/> on reopen).
    /// </summary>
    /// <param name="slotToTable">Component tables indexed by slot (used to find the spatial field).</param>
    /// <param name="grid">The engine's configured spatial grid. Used to size the per-archetype <see cref="CellClusterPool"/> so its per-cell arrays cover
    /// every valid cell key. Under Q10 the pool is per-archetype — each cluster-spatial archetype sharing the grid gets its own instance sized to the
    /// grid's cell count.</param>
    /// <param name="archetypeId">Numeric id of this archetype, stored into <see cref="ArchetypeId"/>; keys this archetype's per-cell cluster claims within the
    /// shared grid so scans only walk its own clusters. Defaults to 0.</param>
    public void InitializeSpatial(ComponentTable[] slotToTable, SpatialGrid grid, int archetypeId = 0)
    {
        ArchetypeId = archetypeId;
        Grid = grid;

        for (var slot = 0; slot < slotToTable.Length; slot++)
        {
            var table = slotToTable[slot];
            if (table.SpatialIndex == null)
            {
                continue;
            }

            var tableFi = table.SpatialIndex.FieldInfo;
            // FieldOffset in cluster = field offset within pure component data (no ComponentOverhead in clusters)
            var clusterFieldOffset = tableFi.FieldOffset - table.ComponentOverhead;
            var variant = tableFi.ToVariant();
            var descriptor = SpatialNodeDescriptor.ForVariant(variant);

            // Create a modified SpatialFieldInfo with cluster-relative offset
            var fi = new SpatialFieldInfo(clusterFieldOffset, tableFi.FieldSize, tableFi.FieldType, tableFi.CellSize, tableFi.Mode, tableFi.Category);

            // Dirty ring lives exclusively on ArchetypeClusterState after issue #230 Phase 3 legacy purge. Consumers (SpatialInterestSystem,
            // DatabaseEngine.WriteClusterTickFence) read ClusterDirtyRing directly.
            ClusterDirtyRing = new DirtyBitmapRing(Math.Max(4, ClusterSegment.ChunkCapacity));

            // Issue #229 Q10: allocate this archetype's own CellClusterPool. Other cluster-spatial archetypes sharing the same grid each get their own
            // instance, so claim-list scans at spawn time only walk clusters of the current archetype.
            CellClusterPool = new CellClusterPool(grid.CellCount);

            // Issue #233: allocate dormancy arrays for spatial archetypes. Non-spatial archetypes leave SleepStates null (zero overhead).
            var capacity = Math.Max(16, PrimarySegmentCapacity);
            SleepStates = new ClusterSleepState[capacity];
            SleepCounters = new ushort[capacity];

            SpatialSlot = new ClusterSpatialSlot
            {
                HasSpatialIndex = true,
                Slot = slot,
                FieldOffset = clusterFieldOffset,
                FieldInfo = fi,
                Descriptor = descriptor,
            };
            break; // Only one spatial field per archetype
        }
    }

    /// <summary>
    /// Rebuild Versioned component HEAD values in cluster slots from revision chains.
    /// Called on database reopen when the cluster slot WAL might be stale (crash between commit and tick fence).
    /// For each occupied entity, walks the revision chain to find the HEAD and copies its value to the cluster slot.
    /// Returns the number of occupied (entity, Versioned slot) pairs it could NOT rebuild — see <paramref name="skips"/>.
    /// </summary>
    /// <param name="meta">Metadata of the archetype being rebuilt.</param>
    /// <param name="engineState">Per-archetype engine state; supplies the EntityMap and the per-slot ComponentTables.</param>
    /// <param name="changeSet">ChangeSet the rebuilt slot writes are tracked in.</param>
    /// <param name="skips">
    /// Receives a breakdown of the pairs this pass gave up on. Each is a slot left holding whatever was in it — on a fresh reopen, zero. #688 is that outcome
    /// reaching a caller as if it were committed state: <c>IsValid</c> passes, the rebuild count is non-zero, and the value is silently wrong. The rebuild
    /// cannot repair these — the chain it needs is genuinely not reachable — but it can stop being quiet about them, which is the difference between a
    /// diagnosable defect and one that took a 1-in-4 arm64 nightly to notice.
    /// </param>
    /// <param name="enabledBitsTrusted">
    /// False when the EntityMap this reads was re-derived on the crash path, where <c>EnabledBits</c> is reconstructed from the cluster SoA copy whose
    /// durability is the open gap in #398. A rootless slot cannot then be classified, so it is counted as a defect rather than as expected absence.
    /// </param>
    public void RebuildVersionedHeadFromChain(ArchetypeMetadata meta, ArchetypeEngineState engineState, ChangeSet changeSet, bool enabledBitsTrusted,
        out VersionedHeadRebuildSkips skips)
    {
        skips = default;
        if (meta.VersionedSlotMask == 0)
        {
            return;
        }

        // Invariant: VersionedSlotMask != 0 implies ArchetypeClusterInfo.Compute allocated a non-null
        // SlotToVersionedIndex array (see ArchetypeClusterInfo.cs — the array is only allocated when
        // versionedSlotMask != 0). Cache the reference in a local so the null check is expressed once
        // at the top of the method instead of at every indexing site, and the compiler's nullability
        // analysis sees a non-null local for the rest of the body.
        var slotToVi = Layout.SlotToVersionedIndex;
        if (slotToVi == null)
        {
            return;
        }

        var clusterAccessor = ClusterSegment.CreateChunkAccessor();
        var mapAccessor = engineState.EntityMap.Segment.CreateChunkAccessor();
        var recordSize = meta._entityRecordSize;
        var recordBuf = stackalloc byte[recordSize];

        // Pre-create accessors for each Versioned slot's tables (hoisted out of entity/slot loops)
        var compRevAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        var contentAccessors = new ChunkAccessor<PersistentStore>[meta.ComponentCount];
        for (var slot = 0; slot < meta.ComponentCount; slot++)
        {
            if (slotToVi[slot] >= 0)
            {
                var table = engineState.SlotToComponentTable[slot];
                compRevAccessors[slot] = table.CompRevTableSegment.CreateChunkAccessor();
                contentAccessors[slot] = table.ComponentSegment.CreateChunkAccessor();
            }
        }

        try
        {
            for (var c = 0; c < ActiveClusterCount; c++)
            {
                var chunkId = ActiveClusterIds[c];
                var clusterBase = clusterAccessor.GetChunkAddress(chunkId, true);
                var occupancy = *(ulong*)clusterBase;

                while (occupancy != 0)
                {
                    var slotIndex = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;

                    // Read entity key from cluster
                    var entityPK = *(long*)(clusterBase + Layout.EntityIdsOffset + slotIndex * 8);

                    // C3 (#680's review, applied here per #688): a LIVE cluster slot always carries a non-zero entity id, so a zero at an occupied slot proves
                    // the geometry being read through is wrong — whatever the cause. Elsewhere (ClusterMigration) a zero here is a legitimate race with a
                    // concurrent clear and is skipped; that defence does not apply on this path, which runs single-threaded at OPEN before any transaction
                    // exists. Loud, because the alternative is serving a zeroed component from a reopened database as if it were committed state.
                    if (entityPK == 0)
                    {
                        throw new CorruptionException(meta.Name, chunkId,
                            $"cluster slot {slotIndex} is marked occupied but carries entity id 0 while rebuilding Versioned HEADs at open — "
                            + "the cluster geometry being read does not match the occupancy bitmap");
                    }

                    var entityKey = EntityId.FromRaw(entityPK).EntityKey;

                    // Read ClusterEntityRecord from EntityMap to get compRevFirstChunkId
                    if (!engineState.EntityMap.TryGet(entityKey, recordBuf, ref mapAccessor))
                    {
                        // The entity is in the cluster but not in the EntityMap. Its Versioned slots keep whatever they held — zero on a fresh reopen — and
                        // that value is then served as committed state. RB-01's ordering caveat is the known way to get here: on the crash path the loaded
                        // EntityMap is not yet trusted, and a mixed cluster archetype runs this pass before the map is rebuilt.
                        skips.EntityNotInMap++;
                        continue;
                    }

                    // For each Versioned slot: walk chain → find HEAD → copy to cluster slot
                    for (var slot = 0; slot < meta.ComponentCount; slot++)
                    {
                        int vi = slotToVi[slot];
                        if (vi < 0)
                        {
                            continue;
                        }

                        var compRevFirstChunkId = ClusterEntityRecordAccessor.GetCompRevFirstChunkId(recordBuf, vi);
                        if (compRevFirstChunkId == 0)
                        {
                            // No chain root for this Versioned slot. The enabled bit says which of the two states this is, and they are not the same event:
                            // bit CLEAR is a component the spawn never supplied — absent by design since #845, routine on any partially-spawned entity — while
                            // bit SET means the component has a value whose pointer is gone. Only the latter is counted toward the warned Total; counting both
                            // would fire a Warning on every healthy database and train operators to ignore the log #688 exists to produce.
                            if (!enabledBitsTrusted)
                            {
                                skips.RootlessUnclassifiable++;
                            }
                            else if ((EntityRecordAccessor.GetHeader(recordBuf).EnabledBits & (1 << slot)) != 0)
                            {
                                skips.ChainRootLost++;
                            }
                            else
                            {
                                skips.AbsentByDesign++;
                            }

                            continue;
                        }

                        // Walk chain to find HEAD (latest committed entry)
                        ref var compRevAccessor = ref compRevAccessors[slot];
                        var chainResult = RevisionChainReader.WalkChain(ref compRevAccessor, compRevFirstChunkId, long.MaxValue);
                        if (chainResult.IsFailure)
                        {
                            // A chain root exists and the walk failed. Never benign: the slot keeps its stale or zero value with no other signal.
                            skips.ChainWalkFailed++;
                            continue;
                        }

                        // Read HEAD value from content chunk and copy to cluster slot
                        var headChunkId = chainResult.Value.CurCompContentChunkId;
                        ref var contentAccessor = ref contentAccessors[slot];
                        var srcAddr = contentAccessor.GetChunkAddress(headChunkId);
                        var compSize = Layout.ComponentSize(slot);
                        var dstSlot = clusterBase + Layout.ComponentOffset(slot) + slotIndex * compSize;
                        Unsafe.CopyBlockUnaligned(dstSlot, srcAddr + engineState.SlotToComponentTable[slot].ComponentOverhead, (uint)compSize);
                    }
                }
            }
        }
        finally
        {
            // Dispose all hoisted accessors
            for (var slot = 0; slot < meta.ComponentCount; slot++)
            {
                if (slotToVi[slot] >= 0)
                {
                    compRevAccessors[slot].Dispose();
                    contentAccessors[slot].Dispose();
                }
            }

            mapAccessor.Dispose();
            clusterAccessor.Dispose();
        }
    }
}

/// <summary>
/// Per-component-slot index state for a cluster-eligible archetype. One per component slot that has indexed fields.
/// </summary>
/// <remarks>
/// Generic over the page store since #655. A component slot has exactly one storage mode, so a slot's trees are either all
/// <see cref="PersistentStore"/>-backed (SingleVersion / Versioned, in the persisted index segments) or all <see cref="TransientStore"/>-backed (Transient, in
/// the heap segment that is never persisted). The generic parameter is what lets one drain, one capture and one query path serve both instead of two
/// hand-written copies — <c>BTreeBase&lt;TStore&gt;</c> was already generic; only this metadata was pinned to one instantiation.
/// </remarks>
internal struct ClusterIndexSlot<TStore> where TStore : struct, IPageStore
{
    /// <summary>Component slot index within the archetype.</summary>
    public int Slot;

    /// <summary>Per-indexed-field B+Tree instances (per-archetype ownership).</summary>
    public ClusterIndexField<TStore>[] Fields;

    /// <summary>Per-indexed-field shadow buffers for old value capture before mutation.</summary>
    public FieldShadowBuffer[] ShadowBuffers;

    /// <summary>
    /// Per-indexed-field selectivity statistics, parallel to <see cref="Fields"/> — the only such array since the per-ComponentTable one was removed with
    /// that index home (#665, #629).
    /// </summary>
    /// <remarks>
    /// Not shared with the ComponentTable's array on purpose. Statistics describe a key DISTRIBUTION, and a ComponentTable is shared across every archetype
    /// holding that component: folding several archetypes into one array blends their distributions, so a predicate that is highly selective within one
    /// archetype reads as unselective and the planner picks the wrong path. <see cref="IndexStatistics"/> wraps the store-agnostic
    /// <see cref="IBTreeIndex"/>, so the same type serves either index home with no generic surgery.
    /// </remarks>
    public IndexStatistics[] Stats;
}

/// <summary>
/// Per-archetype spatial index metadata for a cluster-eligible archetype with a <c>[SpatialIndex]</c> field. Holds the narrowphase-facing metadata
/// (<see cref="Slot"/>, <see cref="FieldOffset"/>, <see cref="FieldInfo"/>, <see cref="Descriptor"/>) that both the legacy per-entity tree (being removed
/// in issue #230 Phase 3) and the new per-cell cluster index path (<see cref="ArchetypeClusterState.PerCellIndex"/>) read during spatial bound dispatch.
/// </summary>
internal struct ClusterSpatialSlot
{
    /// <summary>
    /// <c>true</c> when <see cref="ArchetypeClusterState.InitializeSpatial"/> has populated this slot with a configured spatial field. This is the single
    /// check for "does this archetype have a cluster spatial index?" — the per-cell index (<see cref="ArchetypeClusterState.PerCellIndex"/>) itself is
    /// lazily allocated and provides no always-on existence sentinel of its own.
    /// </summary>
    public bool HasSpatialIndex;

    /// <summary>Component slot index that has the spatial field.</summary>
    public int Slot;

    /// <summary>Byte offset of spatial field within cluster component SoA (no ComponentOverhead).</summary>
    public int FieldOffset;

    /// <summary>Spatial field metadata (mode, field type, category).</summary>
    public SpatialFieldInfo FieldInfo;

    /// <summary>Node layout descriptor.</summary>
    public SpatialNodeDescriptor Descriptor;
}

/// <summary>
/// Per-indexed-field B+Tree state within a cluster-eligible archetype.
/// </summary>
internal struct ClusterIndexField<TStore> where TStore : struct, IPageStore
{
    /// <summary>Byte offset of this field within the pure component data (no ComponentOverhead — clusters have no overhead).</summary>
    public int FieldOffset;

    /// <summary>Field size in bytes.</summary>
    public int FieldSize;

    /// <summary>Per-archetype B+Tree instance. Value = ClusterLocation (clusterChunkId * 64 + slotIndex).</summary>
    public BTreeBase<TStore> Index;

    /// <summary>Whether index allows multiple values per key.</summary>
    public bool AllowMultiple;

    /// <summary>Zone map for cluster-level query pruning. Non-null for numeric field types.</summary>
    public ZoneMapArray ZoneMap;

    /// <summary>
    /// Sequential index into the cluster's elementId tail section (0..<see cref="ArchetypeClusterInfo.MultipleIndexedFieldCount"/>-1),
    /// or <c>-1</c> when <see cref="AllowMultiple"/> is false (no tail section allocated for this field).
    /// Used by the cluster destroy/migrate path to locate the per-entity elementId via
    /// <see cref="ArchetypeClusterInfo.IndexElementIdOffset"/> and pass it to
    /// <see cref="BTreeBase{TStore}.RemoveValue"/>, so that only this entity's specific
    /// <c>(key, clusterLocation)</c> entry is removed — not the entire buffer at the key.
    /// </summary>
    public int MultiFieldIndex;
}

/// <summary>
/// Per-component-slot SingleVersion ComponentCollection state for a cluster-eligible archetype. One entry per SV component slot that carries a
/// ComponentCollection field. The cluster slot is the sole owner of these buffers (SV has no revision chain), so they are released directly in
/// <c>ReleaseSlot</c> on destroy.
/// </summary>
internal struct ClusterCollectionSlot
{
    /// <summary>Component slot index within the archetype.</summary>
    public int Slot;

    /// <summary>
    /// The ComponentCollection fields of this SingleVersion component — the owning <see cref="ComponentTable"/>'s own descriptor, shared not copied. A
    /// cluster slot carries no component overhead, so the table's value-relative offsets are already slot-relative.
    /// </summary>
    public ComponentTable.CollectionFieldInfo[] Fields;
}
