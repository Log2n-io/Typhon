using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Typhon.Engine.Internals;

/// <summary>
/// Intra-cell drift detection and relocation placement — <c>AC-10.1</c> through <c>AC-10.4</c> of #872 step 10.
/// </summary>
/// <remarks>
/// <para><b>The asymmetry is the design (§5.2).</b> You can afford to LOOK at every entity that moved; you cannot afford to MOVE them. So detection is a
/// scan budgeted at 0.576 ns/entity and relocation is budgeted per drifter, and the two numbers are three orders of magnitude apart. Everything here is
/// shaped by keeping the first cheap.</para>
/// <para><b>Why this exists at all.</b> Placement happens once, when a slot is claimed, and <c>ClaimSlotInCell</c> takes the first cluster with a free
/// slot — no AABB awareness at all. Under motion that decays until a cluster's bound covers most of its cell, and a narrow query then opens every cluster
/// instead of the few it overlaps. Nothing repaired that before this step.</para>
/// </remarks>
internal sealed unsafe partial class ArchetypeClusterState
{

    /// <summary>
    /// The per-slot entity centres of one cluster, gathered once and read by every consumer that needs them (<c>D1</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>What this replaces.</b> A written cluster used to be walked up to four times per tick: once by <c>RecomputeClusterAabb</c> for its bound,
    /// once by <c>FlagOutliersForMigration</c> for the cell-escape test, once by drift detection to total a centroid, and once more by drift detection to
    /// test each entity against the target region. The last three all want the same value — <c>SpatialGrid.ReadSpatialCenter3D</c> of the same field
    /// pointer — and each one re-derived the component offset and stride, re-read the occupancy word and chased the same pointers again. §5.2 budgets
    /// detection at 0.576 ns/entity on the grounds that it is "sequential in memory", which a walk repeated three times is not.</para>
    /// <para><b>SoA, not an array of triples</b>, because the two consumers scan one axis at a time against a scalar and the hot inner loops then read
    /// three contiguous runs instead of striding a 12-byte record. 64 slots is the cluster capacity ceiling, so the whole thing is 768 bytes of stack —
    /// one cache-line-aligned 12-line block that stays resident for both consumers.</para>
    /// <para><b>The bound's walk is deliberately NOT folded in.</b> It reads through <c>SpatialMaintainer.ReadAndValidateBoundsFromPtr</c>, which returns
    /// doubles and applies the directed rounding C15 requires, whereas a centre is float midpoint arithmetic — and for a <c>BSphere</c> field the stored
    /// centre is not the midpoint of the derived bounds at all, only equal to it up to a rounding step. Deriving one from the other would shift drift
    /// decisions by an ULP at the target-region boundary for no measurable gain, and would silently decouple production from the test oracle, which reads
    /// the component the same way this does.</para>
    /// <para><b>Non-finite slots are recorded in <see cref="ValidMask"/> rather than skipped silently</b>, so both consumers skip exactly the same slots
    /// the separate walks used to skip individually. Dropping that would make the centroid and the per-entity test disagree about the population.</para>
    /// </remarks>
    internal readonly ref struct ClusterCentres
    {
        private readonly Span<float> _xs;
        private readonly Span<float> _ys;
        private readonly Span<float> _zs;

        internal ClusterCentres(Span<float> xs, Span<float> ys, Span<float> zs, ulong validMask, int count,
            float centroidX, float centroidY, float centroidZ)
        {
            _xs = xs;
            _ys = ys;
            _zs = zs;
            ValidMask = validMask;
            Count = count;
            CentroidX = centroidX;
            CentroidY = centroidY;
            CentroidZ = centroidZ;
        }

        /// <summary>Occupied slots whose centre read back finite — the population both consumers agree on.</summary>
        internal ulong ValidMask { get; }

        /// <summary>Popcount of <see cref="ValidMask"/>; zero means the cluster contributed nothing.</summary>
        internal int Count { get; }

        /// <summary>Mean of the valid centres. The target region is centred here, never on the midpoint of the bound.</summary>
        internal float CentroidX { get; }

        /// <inheritdoc cref="CentroidX"/>
        internal float CentroidY { get; }

        /// <inheritdoc cref="CentroidX"/>
        internal float CentroidZ { get; }

        internal float X(int slot) => _xs[slot];

        internal float Y(int slot) => _ys[slot];

        internal float Z(int slot) => _zs[slot];
    }

    /// <summary>
    /// Per-worker scratch for <see cref="BuildRelocationCandidates"/>, reused across ticks and across archetypes.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not a local.</b> <c>RecomputeDirtyClusterAabbsSlice</c> runs once per archetype on the serial path and once per SLICE on the
    /// parallel one, so a <c>new List</c> in its body allocated on every tick — before it was even known whether a single cluster was gated — and then
    /// re-walked the doubling sequence up to the cell's cluster count, which §1.1 targets at ~1 563. That is roughly eleven gen-0 array allocations
    /// discarded per tick, on the path whose per-entity budget is 0.576 ns, and it broke AC-10.8's "a still tick allocates nothing" outright.</para>
    /// <para>An earlier comment justified the local by pointing at the neighbouring outlier buffer, which was wrong: that one is defended by "outlier
    /// fires are rare", so it normally stays at capacity 0 with an <c>Array.Empty</c> backing and never allocates an array at all. This list is touched on
    /// every drifter-bearing cluster, which is the opposite.</para>
    /// <para><b>Why <see cref="ThreadStaticAttribute"/> is sound here.</b> A fence worker executes one slice at a time and the list is live only between
    /// <see cref="BuildRelocationCandidates"/> and the placement scan that consumes it — no reentrancy, no escape, no cross-thread reference.
    /// <c>BuildRelocationCandidates</c> clears it on entry, so sharing it between archetypes processed on the same thread carries nothing between
    /// them.</para>
    /// <para><b>What it retains.</b> One list per worker thread, whose capacity converges to the largest cell that worker has placed into and then stops
    /// growing — which is the point. It is deliberately never trimmed: reclaiming the capacity would reintroduce the growth sequence this exists to
    /// remove.</para>
    /// </remarks>
    [ThreadStatic]
    private static List<RelocationCandidate> CandidateScratch;

    /// <summary>Slots per cluster ceiling — <c>ArchetypeClusterInfo.FullMask</c> is at most 64 bits wide.</summary>
    internal const int MaxSlotsPerCluster = 64;

    /// <summary>
    /// Walk one cluster's occupied slots once, caching every entity centre and totalling the centroid (<c>D1</c>).
    /// </summary>
    /// <param name="clusterChunkId">The cluster to walk.</param>
    /// <param name="accessor">Chunk accessor for the cluster segment; must already be inside an epoch scope.</param>
    /// <param name="scratch">
    /// At least <c>3 * <see cref="MaxSlotsPerCluster"/></c> floats, supplied by the caller so the allocation is hoisted out of the per-cluster loop.
    /// Sliced into three SoA runs. </param>
    internal ClusterCentres GatherClusterCentres(int clusterChunkId, ref ChunkAccessor<PersistentStore> accessor, Span<float> scratch)
    {
        var xs = scratch.Slice(0, MaxSlotsPerCluster);
        var ys = scratch.Slice(MaxSlotsPerCluster, MaxSlotsPerCluster);
        var zs = scratch.Slice(2 * MaxSlotsPerCluster, MaxSlotsPerCluster);

        ref readonly var ss = ref SpatialSlot;
        var clusterBase = accessor.GetChunkAddress(clusterChunkId);
        var occupancy = *(ulong*)clusterBase;
        var compOffset = Layout.ComponentOffset(ss.Slot);
        var compStride = Layout.ComponentSize(ss.Slot);
        var fieldType = ss.FieldInfo.FieldType;

        float sumX = 0f, sumY = 0f, sumZ = 0f;
        var counted = 0;
        ulong valid = 0;

        var bits = occupancy;
        while (bits != 0)
        {
            var slot = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;

            var fieldPtr = clusterBase + compOffset + slot * compStride + ss.FieldOffset;
            SpatialGrid.ReadSpatialCenter3D(fieldPtr, fieldType, out var posX, out var posY, out var posZ);

            if (!float.IsFinite(posX) || !float.IsFinite(posY) || !float.IsFinite(posZ))
            {
                continue; // defensive — non-finite positions are rejected upstream; excluded from BOTH consumers alike
            }

            xs[slot] = posX;
            ys[slot] = posY;
            zs[slot] = posZ;
            valid |= 1UL << slot;

            sumX += posX;
            sumY += posY;
            sumZ += posZ;
            counted++;
        }

        if (counted == 0)
        {
            return new ClusterCentres(xs, ys, zs, 0, 0, 0f, 0f, 0f);
        }

        var inv = 1f / counted;
        return new ClusterCentres(xs, ys, zs, valid, counted, sumX * inv, sumY * inv, sumZ * inv);
    }

    /// <summary>
    /// Test one written cluster's entities against its target region, emitting a relocation request per drifter.
    /// </summary>
    /// <remarks>
    /// <para><b>Two levels, and the gate is the point.</b> A cluster whose largest axis extent already fits the target extent is tight, so no entity in it
    /// can be improved by moving — the method returns after three float compares and never touches entity data. Only a cluster that has spread past the
    /// target pays the per-entity walk. That is what "detect broadly" means here: broad in the number of CLUSTERS considered, not in the work done to
    /// each.</para>
    /// <para><b>The target region is built in WORLD space and never converted.</b> Worth stating precisely, because an earlier version of this comment
    /// claimed a hoisted frame conversion that the code does not perform, and C15 bugs are exactly the ones that hide behind a confident sentence about
    /// frames. The centroid comes from <c>ReadSpatialCenter3D</c>, which reports world coordinates, and every entity is tested against it in world
    /// coordinates. The cell origin is read once per cluster but consumed only on the drifter path, where the chosen point must be handed to <see
    /// cref="ChooseRelocationTarget"/> in the cell-relative frame <see cref="ClusterAabbs"/> uses.</para>
    /// <para><b>Z is skipped for a flat archetype rather than tested.</b> A 2D cluster leaves Z at the ±Infinity sentinel, so its Z extent is
    /// <c>-Infinity</c> and every comparison against it is false in a way that reads like a decision. <c>ReadSpatialCenter3D</c> reports <c>posZ = 0</c>
    /// for those entities, which would then be compared against an infinite box. Detecting flatness once and dropping the axis is both faster and
    /// honest.</para>
    /// </remarks>
    internal void DetectDriftersInCluster(int clusterChunkId, int cellKey, in ClusterSpatialAabb clusterAabb, SpatialGrid grid,
        ref ChunkAccessor<PersistentStore> accessor, in ClusterCentres centres, ulong guardClaimedSlots, List<MigrationRequest> driftBuffer,
        List<RelocationCandidate> candidateScratch, float targetExtent, ref int driftersDetected, ref int driftAbsorbed, ref int driftersUnplaced,
        ref int driftersUnplacedNoCandidate, ref int driftersSpilled)
    {
        // The target extent is the CALLER's — the cell's density-derived one since step 14, or the configured constant in constant mode. It used to be
        // recomputed here from ClusterTargetExtentRatio, so a cluster admitted by a 0.75 density gate had its entities tested against a 0.25 box and
        // most of them filed as drifters: the storm §5.8.2 describes, narrowed to the density band and hidden behind the boost.
        ref readonly var cfg = ref grid.Config;
        if (!(targetExtent > 0f))
        {
            return;
        }

        var flat = float.IsPositiveInfinity(clusterAabb.MinZ) || float.IsNegativeInfinity(clusterAabb.MaxZ);

        // ── Level 1: the per-cluster gate ──────────────────────────────────────────────────────────────────────────
        var extentX = clusterAabb.MaxX - clusterAabb.MinX;
        var extentY = clusterAabb.MaxY - clusterAabb.MinY;
        var spread = extentX > targetExtent || extentY > targetExtent;
        if (!flat && !spread)
        {
            spread = (clusterAabb.MaxZ - clusterAabb.MinZ) > targetExtent;
        }

        if (!spread)
        {
            return;
        }

        // ── The target region, in world space ──────────────────────────────────────────────────────────────────────
        grid.CellOrigin(cellKey, out var originX, out var originY, out var originZ);
        var half = targetExtent * 0.5f;
        var margin = cfg.CellSize * cfg.ClusterDriftMarginRatio;

        // The target region is centred on the cluster's CENTROID, not on the centre of its AABB, and the difference is the whole behaviour of this rule
        // rather than a refinement.
        //
        // An AABB centre sits halfway between the two extremes, so ONE far outlier drags it half the distance to itself. Thirty entities at x≈12 and one
        // at x=90 put the box centre at 50 — which is empty space, where nothing lives — and a target region around 50 then reports the outlier AND all
        // thirty core entities as drifters. Relocating the majority away from where the cluster actually is, to chase a point defined by the one entity
        // that should have left, is precisely backwards, and it turns a one-entity repair into a full cluster shuffle.
        //
        // The centroid is dragged by 1/N instead of 1/2: the same population puts it at ~14.5, the core sits inside the target region, and only the
        // outlier is flagged. Since D1 it costs nothing here at all — the caller's single gather pass totals it while it is caching the centres this loop
        // reads.
        if (centres.Count == 0)
        {
            return;
        }

        var centreX = centres.CentroidX;
        var centreY = centres.CentroidY;
        var centreZ = flat ? 0f : centres.CentroidZ;

        // Slots the outlier guard has already queued for ANOTHER cell are excluded here rather than merely ordered after it. The guard force-migrates
        // entities whose cluster has sprawled past the cell — a correctness escape — while this relocates within the cell, a quality improvement; queueing
        // one entity for both would have two requests naming the same source slot, and whichever drained second would find it empty. Running the guard
        // first made that unlikely rather than impossible, and the shared gather makes the exclusion exact.
        var candidatesBuilt = false;
        var bits = centres.ValidMask & ~guardClaimedSlots;
        while (bits != 0)
        {
            var slotIndex = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;

            var posX = centres.X(slotIndex);
            var posY = centres.Y(slotIndex);
            var posZ = centres.Z(slotIndex);

            var outX = AxisOvershoot(posX, centreX, half);
            var outY = AxisOvershoot(posY, centreY, half);
            var outZ = flat ? 0f : AxisOvershoot(posZ, centreZ, half);
            var overshoot = MathF.Max(outX, MathF.Max(outY, outZ));

            if (overshoot <= 0f)
            {
                continue; // inside the target region — AC-10.2, and the common case in a cluster that is merely lopsided
            }

            if (overshoot <= margin)
            {
                // Inside the dead zone. Counted, not moved: an entity jittering across the boundary would otherwise pay a full migration every tick to
                // move a few units, which is the thrash P4's note warns about.
                driftAbsorbed++;
                continue;
            }

            // Counted HERE, before placement is attempted. "Drifter" is a property of the entity against its target region — AC-10.1 defines it against a
            // brute-force run of exactly this test — and DriftersDetected is documented as "candidates for intra-cell relocation". Counting only the ones
            // that found a home would fold a placement outcome into a detection number, and the gap between the two is precisely the signal step 11 needs:
            // a tick that finds 99 drifters and places 79 is a cell that has run out of room, which is a different problem from a cell that is not
            // drifting.
            driftersDetected++;

            // Built once, on the first drifter of this cluster — every drifter leaving it sees the same candidate set.
            if (!candidatesBuilt)
            {
                BuildRelocationCandidates(cellKey, clusterChunkId, ref accessor, candidateScratch);
                candidatesBuilt = true;
            }

            var destCluster = ChooseRelocationTarget(candidateScratch, posX - originX, posY - originY, flat ? 0f : posZ - originZ, flat);

            if (destCluster < 0)
            {
                if (candidateScratch.Count == 0)
                {
                    // A drifter with nowhere better to go is left in place rather than queued: relocating it into an equally bad cluster costs a full
                    // migration and buys no selectivity. Counted since #872 step 11, not merely skipped — a drifter with nowhere to go and one the budget
                    // refused are the same absence from MigrationCount, and telling them apart is the difference between "the world is drifting faster
                    // than the budget" and "this cell has run out of room", two problems with opposite remedies.
                    driftersUnplaced++;
                    driftersUnplacedNoCandidate++;
                    continue;
                }

                // The cell had candidates and this pass has filled every free slot they offered (step 14's capacity ledger). Filed for a cluster the
                // drain allocates for this cell and reuses through the slice — NOT first fit, which once earlier drifters of this cluster have drained
                // finds the freed slots in the SOURCE and moves the entity back where it came from at full cost. The design's "allocate a new cluster
                // if none qualifies"; step 17's split turns it into a median cut. A cell with NO candidate at all took the branch above instead: its
                // drifters stay where they are, which keeps CR-03's "drifters and no migrations" signal for a cell that has genuinely run out of room.
                driftersSpilled++;
                destCluster = MigrationRequest.FreshCluster;
            }

            driftBuffer.Add(new MigrationRequest(clusterChunkId, slotIndex, cellKey, destCluster, MigrationRequest.AnySlot, MigrationKind.Relocation));
        }
    }

    /// <summary>
    /// The bound-only half of <see cref="FlagClusterForShrinkRefresh"/>: mark every axis for re-derivation WITHOUT setting the process bit.
    /// </summary>
    /// <remarks>
    /// <para><b>The process bit is not a synonym for "recompute me".</b> It means "visit this cluster and republish it", and the refresh acts on it — a set
    /// bit takes a cluster past the <c>!boundsMoved</c> skip into <c>ApplyOrDeferClusterUpdate</c>, the overhang note, the outlier guard and drift detection.
    /// For a cluster that merely lost a slot that is all wrong work: its geometry only ever gets SMALLER, so it cannot have started overhanging its cell or
    /// drifting, and pushing it through detection perturbs relocation. Three <c>ClusterRelocationTests.Placement_*</c> cases caught exactly that — the extra
    /// visits repacked the cell and the fixture's two half-full clusters became one.</para>
    /// <para>The shrink mask alone is what a vacated slot needs, and it is enough: <c>ClusterNeedsAabbRecompute</c> tests these axes directly, so the refresh
    /// re-derives the bound and the ordinary <c>boundsMoved</c> comparison then decides whether the index wants the new box.</para>
    /// <para><b>Barrier-only mode is deliberately NOT covered by this.</b> That arm iterates <see cref="ArchetypeClusterState.ClusterProcessBitmap"/>, so
    /// without the bit it never visits the cluster at all — a destroy there stays invisible exactly as it is today. Closing that needs the bit, and the bit
    /// costs what the paragraph above describes; it is a separate decision, not a side effect of this one.</para>
    /// </remarks>
    internal void FlagClusterShrinkAxesOnly(int chunkId)
    {
        var shrink = ClusterShrinkPendingAxes;
        if (shrink != null && (uint)chunkId < (uint)shrink.Length)
        {
            InterlockedOrShrinkAxes(shrink, chunkId, 0x0F);
        }
    }

    /// <summary>
    /// Mark a cluster as needing a full AABB recompute at this tick's refresh — every axis, and visible to the pass.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a migration source needs this (<c>AC-10.9</c>).</b> <c>ExecuteMigrations</c> has always left the source cluster's bound conservative —
    /// its own comment says so: <i>"The src cluster's AABB stays conservative (not shrunk) — Phase 1 trade-off."</i> That was defensible when migration
    /// meant a cell crossing, where the source cluster keeps most of its entities and a slightly loose bound is a rounding error. It is not defensible for
    /// step 10, whose entire purpose is to make bounds TIGHTER: relocating the one entity that was stretching a cluster, and then leaving the cluster
    /// stretched, buys nothing at all.</para>
    /// <para><b>Both flags, and the second is the one that is easy to miss.</b> The shrink mask says <i>how</i> to recompute; the process bit is what
    /// makes the refresh pass VISIT the cluster in the first place. Migration also clears the source slot's dirty bit, so a cluster whose last migrant
    /// just left can drop out of the pass entirely — the flag would then sit unread until something else happened to write that cluster, which on a
    /// settled cell may be never.</para>
    /// <para><b>All four axes, not the one that moved.</b> The departing entity may have been the extreme on any axis, or on several; the refresh path
    /// only re-derives an axis whose bit is set, and the cost is one scan of a cluster that is being scanned anyway.</para>
    /// <para>Ordering is what makes this work within one tick: Migrate runs before AabbRefresh, and <c>ClearAabbRefreshBookkeeping</c> runs after it in
    /// Finalize, so a bit set here is consumed by this tick's refresh and cleared before the next.</para>
    /// </remarks>
    internal void FlagClusterForShrinkRefresh(int chunkId)
    {
        var shrink = ClusterShrinkPendingAxes;
        if (shrink != null && (uint)chunkId < (uint)shrink.Length)
        {
            // All four axis bits (MinX | MaxX | MinY | MaxY) — the layout ClusterRef.MaybeGrowAndFlagShrink documents.
            InterlockedOrShrinkAxes(shrink, chunkId, 0x0F);
        }

        var bitmap = ClusterProcessBitmap;
        var wordIdx = chunkId >> 6;
        if (bitmap != null && (uint)wordIdx < (uint)bitmap.Length)
        {
            Interlocked.Or(ref bitmap[wordIdx], 1L << (chunkId & 63));
        }
    }

    /// <summary>
    /// Atomic OR of a mask into one byte of a <c>byte[]</c>, via CAS on the aligned int word that contains it.
    /// </summary>
    /// <remarks>
    /// A copy of the primitive <c>ClusterRef</c> uses for the same array, for the same reason: <see cref="Interlocked"/> has no byte overload, and
    /// migration workers flag sources in different cells that can share an int word. Writers targeting different bytes of one word are safe because each
    /// re-reads and retries.
    /// </remarks>
    private static void InterlockedOrShrinkAxes(byte[] array, int index, byte mask)
    {
        var wordIndex = index >> 2;
        var shift = (index & 3) * 8;
        var orValue = mask << shift;

        fixed (byte* basePtr = array)
        {
            ref var word = ref Unsafe.AsRef<int>(basePtr + wordIndex * 4);
            var current = Volatile.Read(ref word);
            while (true)
            {
                var desired = current | orValue;
                if (desired == current)
                {
                    return;
                }

                var prior = Interlocked.CompareExchange(ref word, desired, current);
                if (prior == current)
                {
                    return;
                }
                current = prior;
            }
        }
    }

    /// <summary>How far <paramref name="p"/> lies outside <c>[centre - half, centre + half]</c>, or 0 when inside.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float AxisOvershoot(float p, float centre, float half)
    {
        var d = MathF.Abs(p - centre) - half;
        return d > 0f ? d : 0f;
    }


    /// <summary>One admissible relocation destination: a cluster of the cell with room, and the box it would grow.</summary>
    internal struct RelocationCandidate
    {
        internal RelocationCandidate(int chunkId, in ClusterSpatialAabb box, int freeSlots)
        {
            ChunkId = chunkId;
            Box = box;
            FreeSlots = freeSlots;
        }

        internal readonly int ChunkId;

        /// <summary>Snapshotted BY VALUE — see <see cref="BuildRelocationCandidates"/> for why a reference will not do.</summary>
        internal readonly ClusterSpatialAabb Box;

        /// <summary>
        /// Slots this candidate can still take THIS pass. Decremented by <see cref="ChooseRelocationTarget"/> as drifters are assigned, so the second
        /// drifter of a cluster is not sent to a destination the first one just filled (step 14 — measured: 91–99 % of admitted relocations rejected
        /// their pin and fell through to first fit, because every drifter in a cell named the same empty cluster).
        /// </summary>
        internal int FreeSlots;
    }

    /// <summary>
    /// Collect the admissible destinations for drifters leaving <paramref name="sourceClusterChunkId"/>, once per cluster.
    /// </summary>
    /// <remarks>
    /// <para><b>The candidate set is a property of the CELL and the source, not of the entity.</b> Every drifter leaving one cluster sees the same list,
    /// so rebuilding it per drifter multiplied the work by the cluster's drifter count for no change in any decision — and the rebuild is the expensive
    /// part: for each candidate it chased a chunk address to read an occupancy word and then indexed <see cref="ClusterAabbs"/>, two dependent misses
    /// apiece. At the density §1.1 targets (~1 563 clusters per cell) that put a single drifter's placement three orders of magnitude above §5.2's 50-80
    /// ns budget for relocating one.</para>
    /// <para><b>Built lazily, on the first drifter.</b> Hoisting it unconditionally to the top of each cluster would charge the scan to clusters that turn
    /// out to have no drifters at all — the common case in a world that is merely lopsided, and the case the two-level gate exists to keep free. Deferring
    /// to first use keeps "detect broadly, relocate narrowly" true of this step too.</para>
    /// <para><b>Boxes are copied, not referenced.</b> Sibling AabbRefresh slices blind-store whole <c>ClusterSpatialAabb</c> structs for the clusters they
    /// own, so holding a <c>ref</c> across the drifter loop would let a candidate's six fields be re-read — and mix — at every comparison. A mixed box
    /// whose min exceeds its max scores NEGATIVE growth and wins unconditionally, which is the one outcome <c>CR-02</c>'s read-tolerance does not cover.
    /// One snapshot per cluster also makes placement self-consistent across the drifters of that cluster, which per-drifter re-reads were not.</para>
    /// <para>Occupancy is safe to cache for the same reason it is safe to read at all here: the Migrate phase has completed and AabbRefresh writes no
    /// cluster storage, so no slot can be claimed or released while this list is in use.</para>
    /// </remarks>
    internal void BuildRelocationCandidates(int cellKey, int sourceClusterChunkId, ref ChunkAccessor<PersistentStore> accessor,
        List<RelocationCandidate> candidates)
    {
        candidates.Clear();
        if (CellClusterPool == null || ClusterAabbs == null)
        {
            return;
        }

        var excludeDraining = _repairSourceExclusions.Count > 0;

        var clusters = CellClusterPool.GetClusters(cellKey);
        for (var i = 0; i < clusters.Length; i++)
        {
            var candidate = clusters[i];
            if (candidate == sourceClusterChunkId || (uint)candidate >= (uint)ClusterAabbs.Length)
            {
                continue;
            }

            // Not a cluster this tick's migrations drained (step 15): by AabbRefresh its box reads empty or tight because it was just vacated, and a
            // repair source in particular is about to be freed at Finalize — a pin to it is rejected at the next drain and lands first fit (CR-02).
            if (excludeDraining && _repairSourceExclusions.ContainsCluster(candidate))
            {
                continue;
            }

            if (IsRepairDestination(candidate))
            {
                continue;   // reserved for a repair plan's output — see _repairDestinationReservations
            }

            // Read without dirtying: raising ActiveChunkWriters on a cluster that is merely being inspected inflates ChangeSet and writeback pressure for
            // nothing, which is the reasoning TryClaimSlotInCluster already documents.
            var candidateBase = accessor.GetChunkAddress(candidate);
            var occupancy = *(ulong*)candidateBase;
            var freeSlots = BitOperations.PopCount(~occupancy & Layout.FullMask);
            if (freeSlots == 0)
            {
                continue; // full — a destination with no room is not a candidate
            }

            // Copied into a local first: ClusterAabbs[candidate] is a ref to a live array element and cannot be passed by here, and taking a snapshot is
            // what this method is for anyway.
            var box = ClusterAabbs[candidate];
            candidates.Add(new RelocationCandidate(candidate, in box, freeSlots));
        }
    }

    /// <summary>
    /// The candidate whose AABB grows least to admit a point, or <c>-1</c> when there is none. Coordinates are CELL-RELATIVE, matching <see
    /// cref="ClusterAabbs"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Boxes come from <see cref="ClusterAabbs"/>, not from the per-cell structure.</b> That array is what both representations are fed from and
    /// is indexed by chunk id, so this works identically whether the cell is served by a linear index or a promoted R-Tree — the same reason
    /// <c>DemoteCellHalf</c> reads it. Candidate enumeration is the cell's cluster list, which at the densities this design targets is the ~80 entries
    /// <c>ClaimSlotInCell</c> already scans on every spawn.</para>
    /// <para><b>An empty cluster is enlargement 0, not infinity (<c>AC-10.4</c>).</b> Its stored bound is the <see cref="ClusterSpatialAabb.Empty"/>
    /// sentinel — <c>+∞</c> min against <c>-∞</c> max — so computing growth from it naively yields infinity or NaN and the cluster is never chosen. That
    /// is backwards: an empty cluster is the BEST destination available, because the entity lands in a box that fits it exactly. Special-casing it is what
    /// lets a drained cluster be refilled tightly instead of being skipped until something else fills it loosely.</para>
    /// <para><b>Ties go to the lowest chunk id</b> (<c>AC-10.3</c>). Cluster ids come from an allocator whose order depends on worker interleaving, so
    /// "first one seen" would make placement — and therefore the resulting AABBs, and therefore query costs — a function of scheduling. Two candidates
    /// that grow by the same amount are genuinely equivalent, so the tiebreak only has to be stable.</para>
    /// <para><b>Free-slot testing does not dirty.</b> The occupancy word is read through the accessor without the dirty flag; raising
    /// <c>ActiveChunkWriters</c> on a cluster that is merely being inspected inflates ChangeSet and writeback pressure for nothing, which is the reasoning
    /// <c>TryClaimSlotInCluster</c> already documents.</para>
    /// </remarks>
    internal int ChooseRelocationTarget(List<RelocationCandidate> candidates, float px, float py, float pz, bool flat)
    {
        var best = -1;
        var bestIndex = -1;
        var bestGrowth = float.PositiveInfinity;
        var bestSize = float.PositiveInfinity;

        // A span over the list's buffer, because the winner's FreeSlots is decremented in place — the list is this pass's running ledger of capacity.
        var span = CollectionsMarshal.AsSpan(candidates);
        for (var i = 0; i < span.Length; i++)
        {
            ref var candidate = ref span[i];
            if (candidate.FreeSlots <= 0)
            {
                continue; // filled by an earlier drifter of this pass
            }

            var size = 0f;
            var growth = float.IsPositiveInfinity(candidate.Box.MinX) ? 0f : GrowthToAdmit(in candidate.Box, px, py, pz, flat, out size);

            // Clamped because a box snapshotted mid-store can still be internally inconsistent: min above max yields a grown area SMALLER than the
            // original, hence negative growth, which would beat every honest candidate. The snapshot in BuildRelocationCandidates makes that far less
            // likely; the clamp makes it harmless.
            if (growth < 0f)
            {
                growth = 0f;
            }

            if (growth < bestGrowth || (growth == bestGrowth && (size < bestSize || (size == bestSize && candidate.ChunkId < best))))
            {
                bestGrowth = growth;
                bestSize = size;
                best = candidate.ChunkId;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            span[bestIndex].FreeSlots--;
        }

        return best;
    }

    /// <summary>Increase in a box's area (2D) or volume (3D) needed to admit a point.</summary>
    /// <remarks>
    /// Area rather than perimeter because that is what the R-Tree's own <c>ChooseSubtree</c> minimises, and placement that disagrees with the structure it
    /// feeds would fight it. A degenerate box — one entity, zero extent — has zero area, so growth from it is the area of the box spanning the two points,
    /// which is exactly the right answer and needs no case.
    /// </remarks>
    private static float GrowthToAdmit(in ClusterSpatialAabb box, float px, float py, float pz, bool flat) =>
        GrowthToAdmit(in box, px, py, pz, flat, out _);

    /// <summary>
    /// The enlargement admitting a point costs a box, and the box's size once it has: the second is the tie-break (step 15). Every box that already
    /// contains the point grows by exactly zero, so plain least enlargement ranks a cluster spanning the whole cell level with a tight one beside the
    /// point and lets the chunk id decide — which is how arrivals were measured flowing into the widest boxes under motion. Among equal growth the
    /// SMALLER resulting box wins (R*-tree's "least area" after "least enlargement"); an empty box has area zero and keeps winning outright.
    /// </summary>
    private static float GrowthToAdmit(in ClusterSpatialAabb box, float px, float py, float pz, bool flat, out float resultingSize)
    {
        var minX = MathF.Min(box.MinX, px);
        var maxX = MathF.Max(box.MaxX, px);
        var minY = MathF.Min(box.MinY, py);
        var maxY = MathF.Max(box.MaxY, py);

        if (flat)
        {
            resultingSize = (maxX - minX) * (maxY - minY);
            return resultingSize - ((box.MaxX - box.MinX) * (box.MaxY - box.MinY));
        }

        var minZ = MathF.Min(box.MinZ, pz);
        var maxZ = MathF.Max(box.MaxZ, pz);
        resultingSize = (maxX - minX) * (maxY - minY) * (maxZ - minZ);
        return resultingSize - ((box.MaxX - box.MinX) * (box.MaxY - box.MinY) * (box.MaxZ - box.MinZ));
    }
}
