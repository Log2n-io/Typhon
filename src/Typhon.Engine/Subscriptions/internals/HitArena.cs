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

    // ── The CLUSTER-level candidate buffer (17 § 14) ──────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // The same idea as the entity buffer above, one level up: the enlarged cell query reports the CLUSTERS it reaches rather than their entities, and each
    // session tests those boxes instead of the entities inside them. At d06 that is about 540 boxes against about 11 900 entities for the same cell, and
    // the broad phase stops reading entity memory altogether.
    //
    // Structure of arrays for the same reason the entity buffer is: the per-session test reads four doubles per candidate and nothing else, so the filter
    // is a linear scan over contiguous columns and the slots and chunk ids are only touched for the ones that survive.
    private int[] _clusterChunks = new int[256];
    private ulong[] _clusterSlots = new ulong[256];
    private double[] _clusterMinX = new double[256];
    private double[] _clusterMinY = new double[256];
    private double[] _clusterMaxX = new double[256];
    private double[] _clusterMaxY = new double[256];
    private int _clusterCount;

    /// <summary>
    /// Per candidate: whether this cluster was absent from the same cell's candidates on the previous tick.
    /// </summary>
    /// <remarks>
    /// <b>This is what keeps the incremental residency pass exact.</b> A session that skips its full scan walks only the clusters its view already holds,
    /// so a cluster that appeared from nowhere — born, relocated, or moving fast enough to cross the whole margin in one tick — would be invisible to it
    /// until the next full scan. Flagging the ones the cell did not have last tick costs one comparison per candidate, shared by the cell's sessions, and
    /// turns "eventually" into "this tick".
    /// </remarks>
    private byte[] _clusterNew = new byte[256];

    private int[] _pickChunks = new int[256];
    private ulong[] _pickMasks = new ulong[256];

    /// <summary>Where each accepted candidate came from in the candidate columns, so its bounds can be read back without copying them.</summary>
    private int[] _pickSources = new int[256];
    private int _pickCount;

    /// <summary>Cluster candidates the broad phase collected for the cell being resolved.</summary>
    public int ClusterCandidateCount => _clusterCount;

    /// <summary>Cluster candidates this worker collected over the tick, and how many survived a member's test.</summary>
    public long ClusterCandidatesCollected { get; private set; }

    /// <summary>Cluster candidates that passed, summed over every member of every cell this worker resolved.</summary>
    public long ClusterCandidatesAccepted { get; private set; }

    /// <summary>Discards the previous cell's cluster candidates.</summary>
    public void BeginClusterCell() => _clusterCount = 0;

    /// <summary>Notes the cluster candidates this cell's broad phase collected, once it is complete.</summary>
    public void NoteClusterCellCollected() => ClusterCandidatesCollected += _clusterCount;

    /// <summary>Records one cluster the enlarged cell query reached.</summary>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="slots">Its occupied slots.</param>
    /// <param name="minX">World-space minimum X of its tight bounds.</param>
    /// <param name="minY">World-space minimum Y.</param>
    /// <param name="maxX">World-space maximum X.</param>
    /// <param name="maxY">World-space maximum Y.</param>
    /// <param name="isNew">Whether the cell did not hold this cluster on the previous tick.</param>
    public void AddClusterCandidate(int chunkId, ulong slots, double minX, double minY, double maxX, double maxY, bool isNew = false)
    {
        if (_clusterCount == _clusterChunks.Length)
        {
            var grown = _clusterChunks.Length * 2;
            Array.Resize(ref _clusterChunks, grown);
            Array.Resize(ref _clusterSlots, grown);
            Array.Resize(ref _clusterMinX, grown);
            Array.Resize(ref _clusterMinY, grown);
            Array.Resize(ref _clusterMaxX, grown);
            Array.Resize(ref _clusterMaxY, grown);
            Array.Resize(ref _clusterNew, grown);
        }

        _clusterNew[_clusterCount] = isNew ? (byte)1 : (byte)0;

        _clusterChunks[_clusterCount] = chunkId;
        _clusterSlots[_clusterCount] = slots;
        _clusterMinX[_clusterCount] = minX;
        _clusterMinY[_clusterCount] = minY;
        _clusterMaxX[_clusterCount] = maxX;
        _clusterMaxY[_clusterCount] = maxY;
        _clusterCount++;
    }

    /// <summary>Starts gathering one archetype's cluster picks for one session.</summary>
    public void BeginPick() => _pickCount = 0;

    /// <summary>
    /// Selects every cluster candidate whose bounds reach within <paramref name="radius"/> of a session's viewpoint.
    /// </summary>
    /// <param name="cx">The session's viewpoint X.</param>
    /// <param name="cy">Its viewpoint Y.</param>
    /// <param name="radius">Its observer radius.</param>
    /// <param name="from">First candidate of the archetype's range.</param>
    /// <param name="to">One past its last.</param>
    /// <remarks>
    /// <b>The test is the closest point on the box, which is the entity narrowphase's test applied to the cluster's bounds.</b> A cluster passes when the
    /// nearest point of its tight box is within the radius, so every entity inside the disc is inside a cluster that passes — the over-approximation goes
    /// one way only, and <c>ResidentInterestTests</c> asserts that direction entity by entity against a direct query.
    /// </remarks>
    public void SelectClustersInto(double cx, double cy, double radius, int from, int to) => SelectClustersInto(cx, cy, radius, from, to, onlyNew: false);

    /// <summary>
    /// Selects cluster candidates within <paramref name="radius"/>, optionally only those the cell did not hold last tick.
    /// </summary>
    /// <param name="cx">The session's viewpoint X.</param>
    /// <param name="cy">Its viewpoint Y.</param>
    /// <param name="radius">The distance to admit within.</param>
    /// <param name="from">First candidate of the archetype's range.</param>
    /// <param name="to">One past its last.</param>
    /// <param name="onlyNew">
    /// When set, only candidates flagged new are considered. That is the incremental residency pass's second half: everything else it needs is already an
    /// entry of the session's view, and only a cluster the cell has never shown it can require a map lookup.
    /// </param>
    public void SelectClustersInto(double cx, double cy, double radius, int from, int to, bool onlyNew)
    {
        var radiusSq = radius * radius;
        var accepted = 0;
        for (var i = from; i < to; i++)
        {
            if (onlyNew && _clusterNew[i] == 0)
            {
                continue;
            }

            // Closest-point distance to the box, per axis: zero inside the span, the gap outside it.
            var dx = Math.Max(Math.Max(_clusterMinX[i] - cx, 0d), cx - _clusterMaxX[i]);
            var dy = Math.Max(Math.Max(_clusterMinY[i] - cy, 0d), cy - _clusterMaxY[i]);
            if ((dx * dx) + (dy * dy) > radiusSq)
            {
                continue;
            }

            if (_pickCount == _pickChunks.Length)
            {
                Array.Resize(ref _pickChunks, _pickChunks.Length * 2);
                Array.Resize(ref _pickMasks, _pickChunks.Length);
                Array.Resize(ref _pickSources, _pickChunks.Length);
            }

            _pickChunks[_pickCount] = _clusterChunks[i];
            _pickMasks[_pickCount] = _clusterSlots[i];
            _pickSources[_pickCount] = i;
            _pickCount++;
            accepted++;
        }

        ClusterCandidatesAccepted += accepted;
    }

    /// <summary>How many cluster candidates the last selection accepted.</summary>
    public int PickCount => _pickCount;

    /// <summary>The cluster of one accepted candidate.</summary>
    /// <param name="index">Its position.</param>
    /// <returns>The chunk id.</returns>
    public int PickChunk(int index) => _pickChunks[index];

    /// <summary>The squared closest-point distance from a viewpoint to one accepted candidate's bounds.</summary>
    /// <param name="index">Its position in the accepted list.</param>
    /// <param name="cx">The viewpoint X.</param>
    /// <param name="cy">The viewpoint Y.</param>
    /// <returns>The squared distance, zero when the point is inside the box.</returns>
    public double PickDistanceSq(int index, double cx, double cy)
    {
        var source = _pickSources[index];
        var dx = Math.Max(Math.Max(_clusterMinX[source] - cx, 0d), cx - _clusterMaxX[source]);
        var dy = Math.Max(Math.Max(_clusterMinY[source] - cy, 0d), cy - _clusterMaxY[source]);
        return (dx * dx) + (dy * dy);
    }

    /// <summary>The occupied slots of one accepted candidate.</summary>
    /// <param name="index">Its position.</param>
    /// <returns>The mask.</returns>
    public ulong PickMask(int index) => _pickMasks[index];

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
    public void BeginCell() => _candCount = 0;

    /// <summary>Notes the candidates this cell's broad phase collected, once it is complete.</summary>
    public void NoteCellCollected() => CandidatesCollected += _candCount;

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
    public void FilterCandidatesInto(double cx, double cy, double radius, int from, int to)
    {
        var radiusSq = radius * radius;
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
                miss |= Vector512.GreaterThan((dx * dx) + (dy * dy), vr2);

                var hit = ~miss.ExtractMostSignificantBits() & 0xFFu;
                while (hit != 0)
                {
                    var lane = BitOperations.TrailingZeroCount(hit);
                    hit &= hit - 1;
                    _candAccepted++;
                    AddSphereHit(_candChunks[i + lane], _candSlots[i + lane]);
                }
            }
        }

        if (Vector256.IsHardwareAccelerated)
        {
            var vcx = Vector256.Create(cx);
            var vcy = Vector256.Create(cy);
            var vr2 = Vector256.Create(radiusSq);
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
                miss |= Vector256.GreaterThan((dx * dx) + (dy * dy), vr2);

                var hit = ~miss.ExtractMostSignificantBits() & 0xFu;
                while (hit != 0)
                {
                    var lane = BitOperations.TrailingZeroCount(hit);
                    hit &= hit - 1;
                    _candAccepted++;
                    AddSphereHit(_candChunks[i + lane], _candSlots[i + lane]);
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
            if (((dx * dx) + (dy * dy)) > radiusSq)
            {
                continue;
            }

            _candAccepted++;
            AddSphereHit(_candChunks[i], _candSlots[i]);
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
    public void AddSphereHit(int chunkId, int slot)
    {
        if (chunkId < 0 || (uint)slot >= 64u)
        {
            return;
        }

        var bit = 1UL << slot;
        var mapSlot = FindMapSlot(chunkId);
        if (_sphereMapKeys[mapSlot] != 0)
        {
            _sphereMasks[_sphereMapValues[mapSlot]] |= bit;
            return;
        }

        if (_sphereCount == _sphereChunks.Length)
        {
            Array.Resize(ref _sphereChunks, _sphereChunks.Length * 2);
            Array.Resize(ref _sphereMasks, _sphereChunks.Length);
            Array.Resize(ref _sphereMapSlots, _sphereChunks.Length);
        }

        _sphereChunks[_sphereCount] = chunkId;
        _sphereMasks[_sphereCount] = bit;

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
