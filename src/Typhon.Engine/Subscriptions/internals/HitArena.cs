using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;

namespace Typhon.Engine.Internals;

/// <summary>One identity a session's interest stopped reaching: what to send a LEAVE for, and under which archetype.</summary>
/// <remarks>
/// It carries the identity the SESSION was told about, not whoever occupies the slot now — see <see cref="SessionInterestView"/> for why those differ, and
/// why the generation travels with it.
/// </remarks>
internal struct InterestLeave
{
    /// <summary>The identity and its generation, packed by <c>SessionInterestView.Pack</c>.</summary>
    /// <remarks>
    /// The identity alone, with no archetype beside it: the frame stage reaches the archetype through the session's own known-set entry, which is the copy
    /// that decides which sub-list the record joins. A second copy here would be a second opinion about something only the known-set can be right about.
    /// </remarks>
    public ulong Id;

    /// <summary>The view entry the session held it in, or <c>-1</c> when the leave came from somewhere with no entry to name.</summary>
    /// <remarks>
    /// <b>So the frame stage can retract the session's CLAIM as well as tell the client.</b> A view entry keeps the identity it last described in a slot,
    /// and a leave emitted from that slot leaves the claim behind: the known-set forgets the entity, the view goes on naming it, and the two structures
    /// disagree about what the client knows. That disagreement is invisible while every cluster is walked — the walk re-reads the slot and puts the
    /// identity back — and it is exactly what makes a cluster run unsafe to reference without walking it (17 § 18).
    /// </remarks>
    public int ViewIndex;

    /// <summary>The slot inside that entry.</summary>
    public int Slot;
}

/// <summary>Flags on one <see cref="InterestRun"/>.</summary>
internal static class InterestRunFlags
{
    /// <summary>Nothing to say about this run.</summary>
    internal const ushort None = 0;

    /// <summary>
    /// The cluster carried no replication block when the run was recorded, so the run's <see cref="InterestRun.Block"/> is null and the cluster is on the
    /// worker's new-block list. The blocks step creates it; until it has, nothing in this run is watched.
    /// </summary>
    internal const ushort NoBlock = 1 << 0;

    /// <summary>
    /// The run exists only to move the session's view: its cluster is no longer reached, so its whole committed mask left and its new mask is empty.
    /// </summary>
    /// <remarks>
    /// It carries no hits, and the frame stage must not count it as owed. Runs of this kind are what turn "the observer walked away from a cluster" into
    /// leaves without the frame stage having to walk the view itself — the interest pass already knows, because it is the pass that did not reach it.
    /// </remarks>
    internal const ushort Departed = 1 << 1;
}

/// <summary>
/// One run of hits: every entity of one cluster that one session's interest reached this tick, as a slot bitmask.
/// </summary>
/// <remarks>
/// <para>
/// <b>A run, not a hit, is the unit — and that is a deliberate deviation from the ≈ 8 B per hit of
/// <c>design/Subscriptions/09-phase1-build-plan.md § P1-12</c>.</b> The series already requires hits to arrive grouped by cluster so the directory is probed
/// once per run (<c>design/Subscriptions/foundation/02-replication-state-blocks.md § 4</c>); a cluster holds at most 64 entities and one session's interest
/// reaches a given entity at most once, so the grouping is exactly representable as a 64-bit mask. That makes the run self-describing at 24 B for 1-64 hits —
/// ≈ 1.1 B per hit at SWG's <c>N = 21</c>, against 8 B — and, more importantly, it makes the watched-bit update ONE atomic per cluster instead of one per
/// entity (see <see cref="InterestPass"/>).
/// </para>
/// <para>
/// What the mask cannot carry is per-hit data, which Phase 1 has none of: the enter budget's "nearest first" ordering
/// (<c>design/Subscriptions/01-model.md § 4</c>) is a Phase 2 sphere-observer concern and wants a distance per hit. When it arrives it is a side array
/// parallel to the mask's set bits, not a reason to widen every run of a <c>World</c> observer that will never have a distance.
/// </para>
/// <para>
/// <see cref="Block"/> is an <see cref="nint"/> rather than a <c>ReplicationBlockHeader*</c> so the run can live in an ordinary managed array. It addresses a
/// <see cref="ReplicationBlockPool"/> slab, which is native memory that outlives the tick; an <see cref="nint"/> held inside a managed array is not a pointer
/// into that array, which is the same reasoning <see cref="ReplicationDirectory"/> gives for its own storage.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential, Size = 40)]
internal struct InterestRun
{
    /// <summary>The cluster's replication block, or zero when it has none yet (<see cref="InterestRunFlags.NoBlock"/>).</summary>
    public nint Block;

    /// <summary>Bit <c>i</c> is set when slot <c>i</c> of the cluster is a hit for the session this run belongs to.</summary>
    public ulong Slots;

    /// <summary>
    /// The subset of <see cref="Slots"/> that this session's interest did NOT reach on the tick of its last published frame.
    /// </summary>
    /// <remarks>
    /// <b>This is the fact the change mask cannot supply and C-1 lacked</b> ([12 § C-1](12)). A slot that is unchanged this tick may still be one the
    /// session has never been told about, and the two are indistinguishable from the block alone; the interest pass knows because it holds the session's
    /// previous membership. The frame stage reads every entered slot and only those retained slots the block reports changed.
    /// </remarks>
    public ulong Entered;

    /// <summary>Where the cluster sits in the session's interest view, so the frame stage commits membership without looking it up again.</summary>
    public int ViewIndex;

    /// <summary>The cluster's chunk id.</summary>
    public int ChunkId;

    /// <summary>Index of the archetype's compiled plan, so the consumer reaches the layout without a lookup by name.</summary>
    public ushort ArchetypeIndex;

    /// <summary><see cref="InterestRunFlags"/>.</summary>
    public ushort Flags;

    /// <summary>How many entities this run carries.</summary>
    public readonly int HitCount => BitOperations.PopCount(Slots);
}

/// <summary>
/// One worker's scratch for a tick of S2a: the runs it produced, the clusters it found with no block, and the blocks it marked watched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per worker, so nothing here is shared and nothing here is synchronized.</b> A chunk owns its arena for the whole of its execution; the only memory two
/// chunks touch in common is the replication block header, whose watched mask is updated with one atomic per cluster.
/// </para>
/// <para>
/// <b>It grows once and is then reused for the life of the runtime</b> (SUB-07). Every buffer is cleared rather than reallocated at
/// <see cref="BeginTick"/>, so after the first few ticks a pass allocates nothing at all — which is what <c>InterestPassTests</c> asserts by measuring the
/// driving thread's allocations across two identical passes.
/// </para>
/// <para>
/// <b>The counters are written once per chunk, not once per run.</b> <see cref="InterestPass"/> accumulates them in locals and flushes through
/// <see cref="Note"/> at the end of its chunk. Arenas are separate small objects that the allocator may well place on one cache line, so a counter moved per
/// cluster would be a line ping-ponging between every worker for the whole stage.
/// </para>
/// </remarks>
internal sealed class HitArena
{
    private InterestRun[] _runs = new InterestRun[256];
    private int _runCount;

    /// <summary>Clusters this worker hit that carry no block yet, packed <c>(archetypeIndex &lt;&lt; 32) | chunkId</c>.</summary>
    private readonly List<long> _newBlocks = [];

    /// <summary>
    /// Membership test for <see cref="_newBlocks"/>, so one worker asks for a cluster's block once however many of its sessions hit it.
    /// </summary>
    /// <remarks>
    /// Touched only on a directory MISS, which is a genuinely new cluster and therefore rare after the first tick. Duplicates would not be a correctness
    /// problem — <see cref="ArchetypeReplicationState.TryAttachBlock"/> refuses a second block for a cluster that already has one — but at 110 sessions
    /// they
    /// would be a 110× list, and the blocks step is single-threaded.
    /// </remarks>
    private readonly HashSet<long> _newBlockSeen = [];

    /// <summary>Blocks this worker was the first to mark watched this tick; the next tick's prologue clears their masks.</summary>
    private readonly List<nint> _watchedBlocks = [];

    /// <summary>Runs recorded so far this tick.</summary>
    public int RunCount => _runCount;

    /// <summary>Directory probes this arena's worker made last tick — one per cluster run, never one per hit.</summary>
    public long DirectoryProbes { get; private set; }

    /// <summary>Hits this arena's worker recorded last tick.</summary>
    public long Hits { get; private set; }

    /// <summary>Chunks this worker executed with the engine's EW-01 window open — the observation <c>InterestEpochScopeTests</c> reads.</summary>
    public long ChunksInsideOpenWindow { get; private set; }

    /// <summary>
    /// The clusters this worker found with no block. The blocks step creates them; each entry is <c>(archetypeIndex &lt;&lt; 32) | chunkId</c>.
    /// </summary>
    public IReadOnlyList<long> NewBlocks => _newBlocks;

    private int[] _sphereChunks = new int[64];
    private ulong[] _sphereMasks = new ulong[64];

    /// <summary>Per sphere run, the subset of <see cref="_sphereMasks"/> inside the observer's ENTER radius rather than merely inside the band.</summary>
    private ulong[] _sphereNear = new ulong[64];
    private int _sphereCount;

    /// <summary>
    /// Open-addressed map from cluster id to its index in <see cref="_sphereChunks"/>, so merging a hit into its run is a probe rather than a scan.
    /// </summary>
    /// <remarks>
    /// <b>The scan this replaces was O(hits x clusters).</b> A 192 m disc at the density 13-density.md measures spans SIXTY clusters and holds two thousand
    /// entities, so the linear merge averaged thirty comparisons per hit — sixty-two thousand per session per archetype per tick, twelve million at two
    /// hundred sessions. The code's own remarks justified the scan on "the cluster count inside a sphere is small", which is true at the eight entities per
    /// disc the shipped world has and false at two thousand. Slots hold <c>chunkId + 1</c> so that zero means empty, and only the entries actually used are
    /// cleared between sessions, which is O(clusters) rather than O(capacity).
    /// </remarks>
    private int[] _sphereMapKeys = new int[256];
    private int[] _sphereMapValues = new int[256];
    private int _sphereMapMask = 255;

    /// <summary>
    /// The broad phase's candidate set for one cell: every entity the enlarged query reached, kept so that each session in the cell filters it instead of
    /// running its own query.
    /// </summary>
    /// <remarks>
    /// <b>Structure of arrays, because the narrow phase reads the four bounds and nothing else.</b> A candidate is a cluster slot plus the tight AABB the
    /// query already read for it, so the per-session pass is a distance test over contiguous doubles with no second component-table read and no probe. The
    /// entity id and the squared distance the query also reports are deliberately not kept: the distance is to the CELL's centre, not to any session's
    /// viewpoint, and keeping it would invite exactly the mistake of reusing it.
    /// </remarks>
    private int[] _candChunks = new int[1024];
    private int[] _candSlots = new int[1024];
    private double[] _candMinX = new double[1024];
    private double[] _candMinY = new double[1024];
    private double[] _candMaxX = new double[1024];
    private double[] _candMaxY = new double[1024];
    private int _candCount;

    /// <summary>
    /// Clusters this cell's broad phase admitted WHOLE, with no entity read and no per-entity test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A cell's query is centred on the CELL, not on any member</b>, so every member's disc contains the disc of radius <c>enter - cell*sqrt(2)/2</c>
    /// about that centre: a member sits somewhere in the cell, at most a half-diagonal from the centre, and the triangle inequality does the rest. A
    /// cluster box lying wholly inside that inscribed disc therefore holds entities every member can see, and the answer is the cluster's occupancy word
    /// rather than sixty-four distance tests repeated per member.
    /// </para>
    /// <para>
    /// <b>It is the HOISTING that makes this pay, not the predicate.</b> The same containment test applied per session AFTER the candidates were collected
    /// was built, measured slower and has since been deleted: by that point the scattered entity reads are already paid and all the split can do is fragment
    /// a vector kernel into short runs. Decided here, the entities are never read at all and the decision is taken once for the whole cell instead of once
    /// per member.
    /// </para>
    /// </remarks>
    private int[] _intChunks = new int[256];
    private ulong[] _intSlots = new ulong[256];
    private int[] _intRanges = [];
    private int _intCount;

    /// <summary>Clusters admitted whole for the cell being resolved.</summary>
    public int InteriorCount => _intCount;

    /// <summary>The chunk id of one whole-admitted cluster.</summary>
    /// <param name="i">Its index.</param>
    /// <returns>The chunk id.</returns>
    public int InteriorChunk(int i) => _intChunks[i];

    /// <summary>The occupancy of one whole-admitted cluster — the mask every member of the cell sees.</summary>
    /// <param name="i">Its index.</param>
    /// <returns>The slots.</returns>
    public ulong InteriorSlots(int i) => _intSlots[i];

    /// <summary>Clusters admitted whole, summed over the cells this worker resolved.</summary>
    public long InteriorClustersAdmitted;

    /// <summary>Entity reads those whole admissions avoided — the occupancy popcount that never reached the narrow phase.</summary>
    public long InteriorEntitiesSkipped;

    /// <summary>The per-archetype range array for the interior list, grown to fit.</summary>
    /// <param name="archetypeCount">How many archetypes the profile names.</param>
    /// <returns>An array of at least that length, whose contents the caller overwrites.</returns>
    public int[] CellInteriorRanges(int archetypeCount)
    {
        if (_intRanges.Length < archetypeCount)
        {
            _intRanges = new int[Math.Max(8, archetypeCount)];
        }

        return _intRanges;
    }

    /// <summary>Records a cluster every member of this cell sees whole.</summary>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="slots">Its occupancy — the mask, because containment makes every occupied slot a hit.</param>
    public void AddInteriorCluster(int chunkId, ulong slots)
    {
        if (_intCount == _intChunks.Length)
        {
            var grown = _intChunks.Length * 2;
            Array.Resize(ref _intChunks, grown);
            Array.Resize(ref _intSlots, grown);
        }

        _intChunks[_intCount] = chunkId;
        _intSlots[_intCount] = slots;
        _intCount++;

        InteriorClustersAdmitted++;
        InteriorEntitiesSkipped += BitOperations.PopCount(slots);
    }

    /// <summary>Candidates the broad phase collected for the cell being resolved.</summary>
    public int CandidateCount => _candCount;

    /// <summary>
    /// Candidates this worker collected, and how many survived a member's distance test, over the tick.
    /// </summary>
    /// <remarks>
    /// The ratio is the broad phase's own efficiency — how much the enlarged query over-reaches. Without it the design's over-send figures rest on
    /// geometry alone and cannot be checked against a running server on a workload nobody modelled.
    /// </remarks>
    public long CandidatesCollected { get; private set; }

    /// <summary>Candidate tests that passed, summed over every member of every cell this worker resolved.</summary>
    public long CandidatesAccepted => _candAccepted;

    private long _candAccepted;

    /// <summary>Where each archetype's candidates begin, for the cell being resolved.</summary>
    /// <remarks>
    /// <b>On the arena because an arena is per worker.</b> Several workers resolve cells at the same time, so a buffer holding "this cell's layout" on the
    /// pass would be one buffer serving all of them. Every other scratch here has the same ownership for the same reason.
    /// </remarks>
    private int[] _cellRanges = new int[8];

    /// <summary>The per-archetype range array, grown to fit.</summary>
    /// <param name="archetypeCount">How many archetypes the profile names.</param>
    /// <returns>An array of at least that length, whose contents the caller overwrites.</returns>
    public int[] CellRanges(int archetypeCount)
    {
        if (_cellRanges.Length < archetypeCount)
        {
            _cellRanges = new int[Math.Max(8, archetypeCount)];
        }

        return _cellRanges;
    }

    /// <summary>Discards the previous cell's candidates.</summary>
    public void BeginCell()
    {
        _candCount = 0;
        _intCount = 0;
    }

    /// <summary>Notes the candidates this cell's broad phase collected, once it is complete.</summary>
    public void NoteCellCollected() => CandidatesCollected += _candCount;

    /// <summary>
    /// Distinct clusters the broad phase reached, summed over the cells this worker resolved.
    /// </summary>
    /// <remarks>
    /// <b>The ratio against <see cref="CandidatesCollected"/> is what decides whether a cached cluster list can pay.</b> The broad phase produces one
    /// candidate per ENTITY, and an entity's box moves every tick, so what a cache could reuse is the cluster MEMBERSHIP and never the boxes. If a cell
    /// reaches a hundred clusters to collect ten thousand entities, caching the membership removes the tree walk and leaves the ten thousand scattered
    /// reads exactly where they were. That is an arithmetic question and this counter is the arithmetic.
    /// </remarks>
    public long BroadClustersReached;

    /// <summary>Counts one cluster the broad phase reached. Called on a chunk-id transition, which is free: the query walks cluster-major.</summary>
    public void NoteBroadCluster() => BroadClustersReached++;

    /// <summary>
    /// Entity candidates the broad phase reached, CUMULATIVE — the partner of <see cref="BroadClustersReached"/>.
    /// </summary>
    /// <remarks>
    /// <b>Not <see cref="CandidatesCollected"/>, which <see cref="BeginTick"/> resets per tick.</b> Dividing a cumulative count by a per-tick one is the
    /// error the block above this reset warns about, and it reported 0.0 entities per cluster over a 400-session run — self-evidently wrong, and wrong in a
    /// direction a reader could have believed.
    /// </remarks>
    public long BroadEntitiesReached;

    /// <summary>Counts one entity candidate the broad phase reached.</summary>
    public void NoteBroadEntity() => BroadEntitiesReached++;

    /// <summary>Records one entity the enlarged cell query reached.</summary>
    /// <param name="chunkId">Its cluster.</param>
    /// <param name="slot">Its slot within that cluster.</param>
    /// <param name="minX">Tight AABB minimum X, as the query read it.</param>
    /// <param name="minY">Tight AABB minimum Y.</param>
    /// <param name="maxX">Tight AABB maximum X.</param>
    /// <param name="maxY">Tight AABB maximum Y.</param>
    public void AddCandidate(int chunkId, int slot, double minX, double minY, double maxX, double maxY)
    {
        if (_candCount == _candChunks.Length)
        {
            var grown = _candChunks.Length * 2;
            Array.Resize(ref _candChunks, grown);
            Array.Resize(ref _candSlots, grown);
            Array.Resize(ref _candMinX, grown);
            Array.Resize(ref _candMinY, grown);
            Array.Resize(ref _candMaxX, grown);
            Array.Resize(ref _candMaxY, grown);
        }

        _candChunks[_candCount] = chunkId;
        _candSlots[_candCount] = slot;
        _candMinX[_candCount] = minX;
        _candMinY[_candCount] = minY;
        _candMaxX[_candCount] = maxX;
        _candMaxY[_candCount] = maxY;

        _candCount++;
    }

        /// <summary>
    /// Merges every candidate within <paramref name="radius"/> of a session's viewpoint into that session's runs.
    /// </summary>
    /// <param name="cx">The session's viewpoint X.</param>
    /// <param name="cy">Its viewpoint Y.</param>
    /// <param name="radius">Its observer radius.</param>
    /// <param name="from">First candidate of the archetype's range.</param>
    /// <param name="to">One past its last.</param>
    /// <remarks>
    /// <b>The test is the engine's own narrowphase, repeated exactly.</b> <c>NarrowphaseAabb2F</c> rejects an entity whose tight box does not overlap the
    /// query box, and then one whose closest point is further than the radius — both in X and Y only, because the cluster narrowphase is two-dimensional.
    /// Repeating it rather than approximating it is what makes this path's membership identical to a direct query's, which is the property the whole broad
    /// phase depends on and which <c>CellKeyedInterestTests</c> asserts entity by entity.
    /// </remarks>
    public void FilterCandidatesInto(double cx, double cy, double radius, int from, int to) =>
        FilterCandidatesInto(cx, cy, radius, radius, from, to);

    /// <summary>
    /// Filters the cell's candidates to one session's disc, splitting them into those inside the enter radius and those only inside the hysteresis band.
    /// </summary>
    /// <param name="cx">The viewpoint X.</param>
    /// <param name="cy">The viewpoint Y.</param>
    /// <param name="enterRadius">The radius an entity the session does not already hold must be inside.</param>
    /// <param name="leaveRadius">The radius an entity the session already holds may stay inside; never smaller than <paramref name="enterRadius"/>.</param>
    /// <param name="from">First candidate.</param>
    /// <param name="to">One past the last.</param>
    /// <remarks>
    /// <b>One pass, two compares.</b> The loads, the box rejection and the closest-point distance are what this kernel costs and they are done once; the
    /// band adds one more compare against an already-computed squared distance and one more mask extraction per step. Running the kernel twice at two
    /// radii would double the part that is expensive to buy the part that is nearly free.
    /// </remarks>
    public void FilterCandidatesInto(double cx, double cy, double enterRadius, double leaveRadius, int from, int to)
    {
        var radius = leaveRadius;
        var radiusSq = radius * radius;
        var enterSq = enterRadius * enterRadius;
        var qMinX = cx - radius;
        var qMaxX = cx + radius;
        var qMinY = cy - radius;
        var qMaxY = cy + radius;

        var i = from;

        // VECTORISED, because the scalar form gives back what the shared query saves. The engine resolves a disc with an AVX-512 narrowphase over sixteen
        // entities per block; a broad phase that queries once and then filters per session with a scalar loop trades a vector kernel run n times for a
        // vector kernel run once and a SCALAR loop run n times, and at the candidate counts a dense disc produces that is a losing trade. The candidate
        // buffer is a structure of arrays precisely so that this loop can be what the kernel it replaces is.
        // AVX-512 first, because the kernel this deliberately reproduces — NarrowphaseAabb2F.Half512 — is 512 bits wide, and running the replacement at
        // half that width on the same box gives back part of what the shared query saves. Same arithmetic, same miss conditions, eight candidates a step.
        if (Vector512.IsHardwareAccelerated)
        {
            var vcx = Vector512.Create(cx);
            var vcy = Vector512.Create(cy);
            var vr2 = Vector512.Create(radiusSq);
            var vEnter2 = Vector512.Create(enterSq);
            var vqMinX = Vector512.Create(qMinX);
            var vqMaxX = Vector512.Create(qMaxX);
            var vqMinY = Vector512.Create(qMinY);
            var vqMaxY = Vector512.Create(qMaxY);
            var zero = Vector512<double>.Zero;

            for (; i + Vector512<double>.Count <= to; i += Vector512<double>.Count)
            {
                var minX = Vector512.LoadUnsafe(ref _candMinX[i]);
                var maxX = Vector512.LoadUnsafe(ref _candMaxX[i]);
                var minY = Vector512.LoadUnsafe(ref _candMinY[i]);
                var maxY = Vector512.LoadUnsafe(ref _candMaxY[i]);

                var miss = Vector512.LessThan(maxX, vqMinX)
                    | Vector512.GreaterThan(minX, vqMaxX)
                    | Vector512.LessThan(maxY, vqMinY)
                    | Vector512.GreaterThan(minY, vqMaxY);

                var dx = Vector512.MaxNative(zero, Vector512.MaxNative(minX - vcx, vcx - maxX));
                var dy = Vector512.MaxNative(zero, Vector512.MaxNative(minY - vcy, vcy - maxY));
                var distSq = (dx * dx) + (dy * dy);
                miss |= Vector512.GreaterThan(distSq, vr2);

                var hit = ~miss.ExtractMostSignificantBits() & 0xFFu;
                var near = ~(miss | Vector512.GreaterThan(distSq, vEnter2)).ExtractMostSignificantBits() & 0xFFu;
                while (hit != 0)
                {
                    var lane = BitOperations.TrailingZeroCount(hit);
                    hit &= hit - 1;
                    _candAccepted++;
                    AddSphereHit(_candChunks[i + lane], _candSlots[i + lane], (near & (1u << lane)) != 0);
                }
            }
        }

        if (Vector256.IsHardwareAccelerated)
        {
            var vcx = Vector256.Create(cx);
            var vcy = Vector256.Create(cy);
            var vr2 = Vector256.Create(radiusSq);
            var vEnter2 = Vector256.Create(enterSq);
            var vqMinX = Vector256.Create(qMinX);
            var vqMaxX = Vector256.Create(qMaxX);
            var vqMinY = Vector256.Create(qMinY);
            var vqMaxY = Vector256.Create(qMaxY);
            var zero = Vector256<double>.Zero;

            for (; i + Vector256<double>.Count <= to; i += Vector256<double>.Count)
            {
                var minX = Vector256.LoadUnsafe(ref _candMinX[i]);
                var maxX = Vector256.LoadUnsafe(ref _candMaxX[i]);
                var minY = Vector256.LoadUnsafe(ref _candMinY[i]);
                var maxY = Vector256.LoadUnsafe(ref _candMaxY[i]);

                // The miss conditions, in the same form and the same order as NarrowphaseAabb2F.Half512 — the positive forms differ on a NaN bound.
                var miss = Vector256.LessThan(maxX, vqMinX)
                    | Vector256.GreaterThan(minX, vqMaxX)
                    | Vector256.LessThan(maxY, vqMinY)
                    | Vector256.GreaterThan(minY, vqMaxY);

                var dx = Vector256.MaxNative(zero, Vector256.MaxNative(minX - vcx, vcx - maxX));
                var dy = Vector256.MaxNative(zero, Vector256.MaxNative(minY - vcy, vcy - maxY));
                var distSq = (dx * dx) + (dy * dy);
                miss |= Vector256.GreaterThan(distSq, vr2);

                var hit = ~miss.ExtractMostSignificantBits() & 0xFu;
                var near = ~(miss | Vector256.GreaterThan(distSq, vEnter2)).ExtractMostSignificantBits() & 0xFu;
                while (hit != 0)
                {
                    var lane = BitOperations.TrailingZeroCount(hit);
                    hit &= hit - 1;
                    _candAccepted++;
                    AddSphereHit(_candChunks[i + lane], _candSlots[i + lane], (near & (1u << lane)) != 0);
                }
            }
        }

        for (; i < to; i++)
        {
            double minX = _candMinX[i], maxX = _candMaxX[i], minY = _candMinY[i], maxY = _candMaxY[i];

            // Written as the miss conditions, the way the narrowphase writes them: the positive forms differ on a NaN bound.
            if (maxX < qMinX || minX > qMaxX || maxY < qMinY || minY > qMaxY)
            {
                continue;
            }

            // double.MaxNative, not Math.Max: the engine's kernel uses Vector*.MaxNative, and the two differ on NaN and on signed zero. Nothing
            // upstream can produce a NaN bound today, but "repeated exactly" has to stay true of the arithmetic and not only of the shape.
            var dx = double.MaxNative(0d, double.MaxNative(minX - cx, cx - maxX));
            var dy = double.MaxNative(0d, double.MaxNative(minY - cy, cy - maxY));
            var distSq = (dx * dx) + (dy * dy);
            if (distSq > radiusSq)
            {
                continue;
            }

            _candAccepted++;
            AddSphereHit(_candChunks[i], _candSlots[i], distSq <= enterSq);
        }
    }

    /// <summary>Where each live entry sits in the map, so a reset zeroes exactly those slots.</summary>
    /// <remarks>
    /// <b>Clearing by re-probing would be wrong.</b> Zeroing a slot breaks the probe chain of anything that collided past it, so an entry cleared earlier can
    /// make a later one unreachable: the probe stops at the hole, returns the wrong slot, and the stale key survives into the next session, where it maps a
    /// cluster to another cluster's run. Recording the slot at insert time makes the reset exact and independent of order.
    /// </remarks>
    private int[] _sphereMapSlots = new int[64];

    /// <summary>The blocks this worker marked watched this tick.</summary>
    public IReadOnlyList<nint> WatchedBlocks => _watchedBlocks;

    /// <summary>
    /// Resets the arena for a new tick. Called single-threaded, from the stage's <c>Prepare</c>, after the prologue has read the watched list.
    /// </summary>
    public void BeginTick()
    {
        _runCount = 0;
        _leaveCount = 0;
        _newBlocks.Clear();
        _newBlockSeen.Clear();
        _watchedBlocks.Clear();
        DirectoryProbes = 0;
        Hits = 0;
        ChunksInsideOpenWindow = 0;
        CandidatesCollected = 0;
        _candAccepted = 0;

        // ── The phase and coherence counters are NOT reset here, and that is the fix for a race rather than an oversight ──────────────────────────────
        //
        // Reset per tick, they were read by a reporting system running elsewhere in the tick with no fence against this prologue, so a read could land
        // after some arenas had been cleared and before the others had been filled. At d07 that produced "0 of 3 sessions" for a 400-session run and a
        // phase split of "broad 0 %, narrow 100 %, assemble 0 %" — self-evidently wrong, and wrong in a direction a reader could have believed.
        //
        // Cumulative, the RATIO these exist to report is immune to when it is read: a torn read costs a little recency, never a wrong proportion. The
        // absolute values are then totals since the runtime started, which is what ObserverMotion already does and why it never had this problem.
    }

    /// <summary>
    /// Starts gathering one archetype's sphere hits for one session.
    /// </summary>
    /// <remarks>
    /// <b>The scratch lives here because an arena is per worker.</b> A sphere query reports hits grouped by cluster only if the spatial index happens to
    /// walk them that way, which is its property and not this pass's, so the hits are merged into one run per cluster before any of them is recorded. Holding
    /// that merge state on the pass itself would be a buffer shared by every worker resolving a session at the same time; holding it on the arena gives each
    /// worker its own, which is the same ownership every other buffer here has.
    /// </remarks>
    public void BeginSphere()
    {
        // Clear only what the previous session used. A memset of the whole table would be the same cost every time however few clusters the last disc
        // touched, and an empty disc is the common case for a session standing in open country.
        for (var i = 0; i < _sphereCount; i++)
        {
            _sphereMapKeys[_sphereMapSlots[i]] = 0;
        }

        _sphereCount = 0;
    }

    /// <summary>Probes to the slot holding <paramref name="chunkId"/>, or to the first empty slot if it is absent.</summary>
    /// <param name="chunkId">The cluster to look up.</param>
    /// <returns>The index into <see cref="_sphereMapKeys"/>.</returns>
    private int FindMapSlot(int chunkId)
    {
        var key = chunkId + 1;

        // Fibonacci hashing: chunk ids are dense and near-sequential, so the low bits alone would collide in long runs.
        var slot = (int)(((uint)chunkId * 2654435769u) >> 8) & _sphereMapMask;
        while (true)
        {
            var k = _sphereMapKeys[slot];
            if (k == 0 || k == key)
            {
                return slot;
            }

            slot = (slot + 1) & _sphereMapMask;
        }
    }

    /// <summary>Doubles the map and reinserts the live entries. Called when the load factor would pass one half.</summary>
    private void GrowSphereMap()
    {
        var capacity = _sphereMapKeys.Length * 2;
        _sphereMapKeys = new int[capacity];
        _sphereMapValues = new int[capacity];
        _sphereMapMask = capacity - 1;

        for (var i = 0; i < _sphereCount; i++)
        {
            var slot = FindMapSlot(_sphereChunks[i]);
            _sphereMapKeys[slot] = _sphereChunks[i] + 1;
            _sphereMapValues[slot] = i;
            _sphereMapSlots[i] = slot;
        }
    }

    /// <summary>Merges one sphere hit into the run for its cluster.</summary>
    /// <param name="chunkId">The cluster the hit is in.</param>
    /// <param name="slot">Its slot within the cluster.</param>
    public void AddSphereHit(int chunkId, int slot) => AddSphereHit(chunkId, slot, near: true);

    /// <summary>Merges one accepted entity into its cluster's run, recording whether it is inside the enter radius or only inside the band.</summary>
    /// <param name="chunkId">Its cluster.</param>
    /// <param name="slot">Its slot.</param>
    /// <param name="near">
    /// <see langword="false"/> when the entity is between the enter and leave radii. Such a slot is admitted only for a session that already holds it,
    /// which is what stops an entity on the boundary entering and leaving on alternate ticks.
    /// </param>
    public void AddSphereHit(int chunkId, int slot, bool near)
    {
        if (chunkId < 0 || (uint)slot >= 64u)
        {
            return;
        }

        var bit = 1UL << slot;
        var nearBit = near ? bit : 0UL;
        var mapSlot = FindMapSlot(chunkId);
        if (_sphereMapKeys[mapSlot] != 0)
        {
            var at = _sphereMapValues[mapSlot];
            _sphereMasks[at] |= bit;
            _sphereNear[at] |= nearBit;
            return;
        }

        if (_sphereCount == _sphereChunks.Length)
        {
            Array.Resize(ref _sphereChunks, _sphereChunks.Length * 2);
            Array.Resize(ref _sphereMasks, _sphereChunks.Length);
            Array.Resize(ref _sphereNear, _sphereChunks.Length);
            Array.Resize(ref _sphereMapSlots, _sphereChunks.Length);
        }

        _sphereChunks[_sphereCount] = chunkId;
        _sphereMasks[_sphereCount] = bit;
        _sphereNear[_sphereCount] = nearBit;

        // Grown BEFORE the insert would take the table past half full, so FindMapSlot's probe loop always has an empty slot to terminate on. Growing after
        // would leave this insert probing a full table, which does not return.
        if ((_sphereCount + 1) * 2 > _sphereMapKeys.Length)
        {
            GrowSphereMap();
            mapSlot = FindMapSlot(chunkId);
        }

        _sphereMapKeys[mapSlot] = chunkId + 1;
        _sphereMapValues[mapSlot] = _sphereCount;
        _sphereMapSlots[_sphereCount] = mapSlot;
        _sphereCount++;
    }

    /// <summary>How many distinct clusters the sphere reached.</summary>
    public int SphereCount => _sphereCount;

    /// <summary>The cluster of one gathered run.</summary>
    /// <param name="index">Its position.</param>
    /// <returns>The chunk id.</returns>
    public int SphereChunk(int index) => _sphereChunks[index];

    /// <summary>The slots of one gathered run.</summary>
    /// <param name="index">Its position.</param>
    /// <returns>The mask.</returns>
    public ulong SphereMask(int index) => _sphereMasks[index];

    /// <summary>The subset of <see cref="SphereMask"/> inside the observer's enter radius; equal to it when the profile declared no band.</summary>
    public ulong SphereNear(int index) => _sphereNear[index];

    /// <summary>Records one cluster run.</summary>
    /// <param name="archetypeIndex">Index of the archetype's compiled plan.</param>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="block">The cluster's replication block, or zero when it has none.</param>
    /// <param name="slots">The hit slots, which is never zero — an empty cluster is skipped before a run is opened.</param>
    /// <param name="entered">The subset of <paramref name="slots"/> the session did not already hold; equal to it when the pass is not incremental.</param>
    /// <param name="viewIndex">Where the cluster sits in the session's interest view, or −1 when the pass is not incremental.</param>
    /// <param name="flags"><see cref="InterestRunFlags"/>.</param>
    public void AddRun(int archetypeIndex, int chunkId, nint block, ulong slots, ulong entered, int viewIndex, ushort flags)
    {
        if (_runCount == _runs.Length)
        {
            Array.Resize(ref _runs, _runs.Length * 2);
        }

        ref var run = ref _runs[_runCount++];
        run.Block = block;
        run.Slots = slots;
        run.Entered = entered;
        run.ViewIndex = viewIndex;
        run.ChunkId = chunkId;
        run.ArchetypeIndex = (ushort)archetypeIndex;
        run.Flags = flags;
    }

    /// <summary>Identities this tick's interest stopped reaching, gathered per session for the frame stage to turn into LEAVE records.</summary>
    private InterestLeave[] _leaves = new InterestLeave[256];
    private int _leaveCount;

    /// <summary>How many leaves this worker has recorded so far this tick, which is where the next session's span starts.</summary>
    public int LeaveCount => _leaveCount;

    /// <summary>The leaves in <c>[start, start + count)</c>.</summary>
    public ReadOnlySpan<InterestLeave> Leaves(int start, int count) => new(_leaves, start, count);

    /// <summary>Records an identity a session's interest no longer reaches.</summary>
    /// <param name="id">The identity and generation the session was told about, from <c>SessionInterestView.Pack</c>.</param>
    /// <param name="viewIndex">The view entry the session held it in, or <c>-1</c> when there is none to name.</param>
    /// <param name="slot">The slot inside that entry.</param>
    public void AddLeave(ulong id, int viewIndex = -1, int slot = 0)
    {
        if (_leaveCount == _leaves.Length)
        {
            Array.Resize(ref _leaves, _leaves.Length * 2);
        }

        ref var leave = ref _leaves[_leaveCount++];
        leave.Id = id;
        leave.ViewIndex = viewIndex;
        leave.Slot = slot;
    }

    /// <summary>Asks the blocks step for a block on <paramref name="chunkId"/>, once per cluster per worker per tick.</summary>
    /// <param name="archetypeIndex">Index of the archetype's compiled plan.</param>
    /// <param name="chunkId">The cluster.</param>
    public void AddNewBlock(int archetypeIndex, int chunkId)
    {
        var packed = ((long)archetypeIndex << 32) | (uint)chunkId;
        if (_newBlockSeen.Add(packed))
        {
            _newBlocks.Add(packed);
        }
    }

    /// <summary>Records that this worker was the one that took <paramref name="block"/> from unwatched to watched this tick.</summary>
    /// <param name="block">The block.</param>
    public void AddWatchedBlock(nint block)
    {
        _watchedBlocks.Add(block);
    }

    // ── The interest phase split ────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // Where the interest stage's time actually goes, which no counter reported and which decides whether caching a session's CLUSTER set could pay.
    // A maintained cluster list would remove the broad phase, the directory probes and the run assembly; it would NOT remove the per-entity narrow
    // phase, because knowing which cluster is in range says nothing about which of its 64 slots are. So the question "is the narrow phase the
    // dominant term" has to be answered before that work is worth doing, and two points on a density ladder cannot answer it: cluster count inside a
    // disc scales with density just as entity count does, so both terms move together and neither can be read off the total.
    //
    // Accumulated in raw Stopwatch ticks, per worker, on the arena, so no line is shared. Gated, because three timestamp pairs per session is real
    // cost on a stage measured in single-digit milliseconds.
    private long _broadTicks;
    private long _narrowTicks;
    private long _flushTicks;

    /// <summary>Raw timestamp ticks spent in the broad phase, the per-entity narrow phase and the run assembly, SINCE START. Needs <c>MeasureInterestPhases</c>.</summary>
    public (long Broad, long Narrow, long Flush) PhaseTicks => (_broadTicks, _narrowTicks, _flushTicks);

    /// <summary>Adds one phase sample.</summary>
    /// <param name="broad">Broad-phase ticks.</param>
    /// <param name="narrow">Narrow-phase ticks.</param>
    /// <param name="flush">Run-assembly ticks.</param>
    public void NotePhases(long broad, long narrow, long flush)
    {
        _broadTicks += broad;
        _narrowTicks += narrow;
        _flushTicks += flush;
    }

    // ── The coherence ceiling ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // How much of a session's answer is UNCHANGED since the last one it published. A run whose slot mask equals the mask the view already holds is a
    // run the session could have been served from a cache, and a session all of whose runs are unchanged is one whose whole query could have been
    // skipped. That second figure is the ceiling on what any residency or live-set scheme can ever save, and it costs two compares per run that the
    // caller has already loaded into registers.
    private long _runsUnchanged;
    private long _runsTotal;

    /// <summary>Runs whose mask was already what the session's view held, beside the total. Cumulative since start.</summary>
    public (long Unchanged, long Total) RunCoherence => (_runsUnchanged, _runsTotal);

    private long _sessionsCoherent;
    private long _sessionsTotal;

    /// <summary>Sessions whose entire answer was unchanged since their last published frame, beside the total resolved. Cumulative since start.</summary>
    /// <remarks>
    /// <b>The ceiling on a residency cache.</b> A session in this set produced no entered slot, no left slot and no departed cluster: its query could
    /// have been skipped entirely and the previous answer reused, had the pass been able to prove that nothing new had come into range. That proof is
    /// the hard part and this number does not supply it — it supplies the size of the prize.
    /// </remarks>
    public (long Coherent, long Total) SessionCoherence => (_sessionsCoherent, _sessionsTotal);

    /// <summary>Notes one session's coherence.</summary>
    /// <param name="coherent"><see langword="true"/> when nothing at all changed for it this tick.</param>
    public void NoteSessionCoherence(bool coherent)
    {
        _sessionsTotal++;
        if (coherent)
        {
            _sessionsCoherent++;
        }
    }

    /// <summary>Notes one run's coherence.</summary>
    /// <param name="unchanged"><see langword="true"/> when nothing entered or left the run.</param>
    public void NoteRunCoherence(bool unchanged)
    {
        _runsTotal++;
        if (unchanged)
        {
            _runsUnchanged++;
        }
    }

    // ── Run flow: how a session's cluster set turns over, as opposed to how its slot masks do ──────────────────────────────────────────────────────
    //
    // RunCoherence above answers "was this run's MASK what the view already held". This answers the coarser question in front of it: was this cluster
    // in the session's set at all last time it published. The two are not the same number and the difference is the whole case for maintaining the
    // candidate set incrementally rather than re-querying it — a set that turns over by a few clusters a tick can be edited, one that turns over by a
    // third of itself cannot.
    private long _runsFirstSeen;
    private long _runsCarried;
    private long _runsDeparted;

    /// <summary>
    /// Clusters a session reached for the first time since its last published frame, clusters it reached again, and clusters it stopped reaching
    /// entirely. Cumulative since start.
    /// </summary>
    /// <remarks>
    /// <b>What this bounds.</b> An incremental candidate set pays the cost of the clusters that CHANGE and keeps the rest for free, so
    /// <c>(FirstSeen + Departed) / (FirstSeen + Carried)</c> is the fraction of per-session interest work such a scheme would still have to do. It is
    /// deliberately measured against the published view rather than against last tick, because the view is what the incremental scheme would be
    /// editing: a session skipped for three ticks must re-derive against the frame it actually sent, not against a tick it never told anyone about.
    /// </remarks>
    public (long FirstSeen, long Carried, long Departed) RunFlow => (_runsFirstSeen, _runsCarried, _runsDeparted);

    /// <summary>Notes one cluster the session reached, and whether its view already held that cluster.</summary>
    /// <param name="held"><see langword="true"/> when the session's last published frame already described this cluster.</param>
    public void NoteRunFlow(bool held)
    {
        if (held)
        {
            _runsCarried++;
        }
        else
        {
            _runsFirstSeen++;
        }
    }

    /// <summary>Notes one cluster the session stopped reaching entirely this tick.</summary>
    public void NoteRunDeparted() => _runsDeparted++;

    // ── The interior/boundary split, which 15 § 3.4 called "almost certainly right" and nobody measured ────────────────────────────────────────────
    //
    // A cluster whose box lies WHOLLY inside the enter radius cannot contain an entity the session may not take, so every per-entity distance test
    // this pass runs against it is known in advance to pass. Only a cluster the disc CLIPS needs one. The ratio below is how much of the narrow phase
    // that observation could delete, and it is a property of the geometry rather than of any arm: the same clusters intersect the same disc whichever
    // granularity the pass then resolves at.
    private long _interiorSlots;
    private long _boundarySlots;

    /// <summary>Occupied slots in clusters wholly inside the enter radius, beside those in clusters the disc clips. Cumulative since start.</summary>
    public (long Interior, long Boundary) ClusterContainment => (_interiorSlots, _boundarySlots);

    /// <summary>Notes one accepted cluster's containment.</summary>
    /// <param name="slots">Its occupied slots.</param>
    /// <param name="interior"><see langword="true"/> when its farthest corner is still inside the enter radius.</param>
    public void NoteContainment(ulong slots, bool interior)
    {
        var n = BitOperations.PopCount(slots);
        if (interior)
        {
            _interiorSlots += n;
        }
        else
        {
            _boundarySlots += n;
        }
    }

    /// <summary>Publishes a chunk's accumulated counters. Called once per chunk, never per run.</summary>
    /// <param name="probes">Directory probes the chunk made.</param>
    /// <param name="hits">Hits the chunk recorded.</param>
    /// <param name="insideOpenWindow"><see langword="true"/> when the chunk ran with the engine's EW-01 window open.</param>
    public void Note(long probes, long hits, bool insideOpenWindow)
    {
        DirectoryProbes += probes;
        Hits += hits;
        if (insideOpenWindow)
        {
            ChunksInsideOpenWindow++;
        }
    }

    /// <summary>A window onto the runs of one session, as the consumer reads them back.</summary>
    /// <param name="start">First run.</param>
    /// <param name="count">How many.</param>
    /// <returns>The runs.</returns>
    public ReadOnlySpan<InterestRun> Runs(int start, int count) => new(_runs, start, count);

    /// <summary>The archetype index a packed new-block entry names.</summary>
    /// <param name="packed">The entry.</param>
    /// <returns>The archetype's plan index.</returns>
    public static int NewBlockArchetype(long packed) => (int)(packed >> 32);

    /// <summary>The chunk id a packed new-block entry names.</summary>
    /// <param name="packed">The entry.</param>
    /// <returns>The cluster's chunk id.</returns>
    public static int NewBlockChunkId(long packed) => (int)packed;
}
