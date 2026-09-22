using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Internals;

/// <summary>
/// Where one session's hits live: which worker's arena holds them, and which of that arena's runs are the session's.
/// </summary>
internal readonly struct SessionHitRange
{
    /// <summary>Creates a range.</summary>
    /// <param name="worker">Index of the arena holding the runs.</param>
    /// <param name="start">First run.</param>
    /// <param name="count">How many runs.</param>
    /// <param name="hits">How many entities those runs carry.</param>
    /// <param name="leaveStart">First leave in that arena.</param>
    /// <param name="leaveCount">How many identities the session's interest stopped reaching.</param>
    public SessionHitRange(int worker, int start, int count, int hits, int leaveStart = 0, int leaveCount = 0)
    {
        Worker = worker;
        Start = start;
        Count = count;
        Hits = hits;
        LeaveStart = leaveStart;
        LeaveCount = leaveCount;
    }

    /// <summary>Index of the arena holding the runs.</summary>
    public int Worker { get; }

    /// <summary>First run in that arena.</summary>
    public int Start { get; }

    /// <summary>How many runs.</summary>
    public int Count { get; }

    /// <summary>How many entities those runs carry.</summary>
    public int Hits { get; }

    /// <summary>Where this session's leaves start in its worker's arena.</summary>
    public int LeaveStart { get; }

    /// <summary>How many identities the session's interest stopped reaching this tick.</summary>
    public int LeaveCount { get; }
}

/// <summary>
/// S2a — one profile's observers become hits, and the hits mark their entities watched.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 1 resolves the <c>World</c> observer and nothing else.</b> <c>Sphere</c>, <c>ClientRegion</c> and <c>Aggregate</c> are declarable and refused at
/// <c>Start</c> by <see cref="SubscriptionsRegistry"/>, so this class never sees one; that refusal is what lets the public shape stay still while the region
/// kinds arrive in Phase 2 (<c>design/Subscriptions/09-phase1-build-plan.md</c> D5). A <c>World</c> observer's region is the archetype, so it needs no spatial
/// query at all: it walks the archetype's active clusters and takes their occupancy word, which is both the cheapest possible producer of hits and the one
/// that produces them already grouped by cluster.
/// </para>
/// <para>
/// <b>Interest runs first so per-entity work follows what clients see</b> (<c>02-execution.md § 3</c>, SUB-13). Its output is three things the rest of the
/// track consumes: the per-session run lists (S2b), the per-worker watched-block lists (S1's partition), and the per-worker new-block lists (the blocks step
/// creates those, not this pass — a cluster that has no block when it is hit is watched from the following tick, never inside a parallel chunk, because the
/// pool and the directory are single-threaded by contract).
/// </para>
/// <para>
/// <b>The watched bit is set with one atomic per cluster, and it is that atomic's RETURN VALUE that claims the block.</b> Several workers reach the same
/// cluster whenever several sessions watch it, so the mask has to be merged rather than stored. <see cref="Interlocked.Or(ref ulong, ulong)"/> hands back the
/// value that was there, and exactly one caller can see zero — so exactly one caller appends the block to its watched list, with no separate claim word and
/// no window between claiming and merging. A tick-stamp claim would have had one: a worker that lost the stamp race could OR into a mask the winner had not
/// yet reset, and its entities would silently go unprojected for that tick.
/// </para>
/// <para>
/// <b>The mask is cleared by the prologue, from those lists</b> — <see cref="BeginTick"/>, which is single-threaded and runs before any chunk. That is why
/// there is no per-tick stamp on the mask and no enumeration of the directory (<see cref="ReplicationDirectory"/> deliberately exposes none). A block marked
/// at tick T is on exactly one worker's list, so tick T+1 clears it once; a block never marked carries a zero mask from
/// <see cref="ReplicationBlockPool.TryRent"/>. The stored pointers stay valid because a pool's slabs are freed only at <c>Dispose</c>, and a block that was
/// returned to the free list in between has a zero mask already.
/// </para>
/// <para>
/// <b>It does not take <c>FenceWindow.EnterWorker()</c>, and that is a measured answer rather than an assumption</b> — open item 4 of
/// <c>foundation/03-subscriptions-track.md § 6</c>. Every <see cref="ExclusiveWindow.NoteMutation"/> call site is a B+Tree, <c>EntityMap</c> or per-cell index
/// mutation; this pass reads cluster occupancy words through a <see cref="ChunkAccessor{TStore}"/> and writes only replication's own block headers and its own
/// arenas, so it reaches none of them. <c>InterestEpochScopeTests</c> runs it at eight workers with the parallel fence on and asserts zero violations against
/// an engine that DID mutate a guarded structure inside the same window, so the zero is not the zero of a window that was never open. The epoch scope itself
/// is still required and is taken by <see cref="SubscriptionsExecSystemBase"/>: reading a cluster column is page access, which PS-02 requires to be inside an
/// <see cref="EpochGuard"/>.
/// </para>
/// <para>
/// <b>Thread safety.</b> <see cref="BeginTick"/> is single-threaded and runs before the dispatch; <see cref="ExecuteChunk"/> runs on pool workers, one per
/// chunk, each owning its own arena and a disjoint slice of the tick's sessions. Everything read back afterwards — <see cref="HitsOf"/>,
/// <see cref="Arena"/> — is read after the dispatch has joined.
/// </para>
/// </remarks>
internal sealed unsafe class InterestPass
{
    /// <summary>One declared profile, resolved: the union of the archetypes its <c>World</c> observers reach, as plan indices.</summary>
    private readonly struct CompiledProfile
    {
        public CompiledProfile(string name, int[] archetypeIndices)
        {
            Name = name;
            ArchetypeIndices = archetypeIndices;
        }

        public string Name { get; }

        /// <summary>Plan indices, deduplicated and in declaration order. Empty for a profile whose observers reach nothing.</summary>
        public int[] ArchetypeIndices { get; }

        /// <summary>The shape every observer in this profile has. A profile mixing shapes is refused at compile time.</summary>
        public ObserverKind Kind { get; init; }

        /// <summary>
        /// The radius a <see cref="ObserverKind.Sphere"/> profile queries at, in world units.
        /// </summary>
        /// <remarks>
        /// It is the LEAVE radius when one was declared, not the enter radius. The watched set has to contain the hysteresis band or the band cannot do its
        /// job: an entity between the two radii must stay watched so that it is not reported as a leave, and a query at the enter radius would drop it from
        /// the walk entirely. Narrowing the band back down to "enter" for entities the session does not yet know is the refinement this defers.
        /// </remarks>
        public double QueryRadius { get; init; }

        /// <summary>
        /// The radius at which a <see cref="ObserverKind.Sphere"/> profile ADMITS an entity it does not already hold, in world units.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The band between this and <see cref="QueryRadius"/> is the hysteresis.</b> An entity the session already holds stays while it is anywhere
        /// inside the query radius; one the session does not hold is admitted only inside this. Without the band an entity hovering on the boundary enters
        /// and leaves on alternate ticks, and every re-entry costs a full ENTER record rather than a state delta.
        /// </para>
        /// <para>
        /// <b>Equal to <see cref="QueryRadius"/> when the profile declared no leave radius</b>, which makes the band's presence a property of the
        /// declaration rather than of the code path: the two masks the narrow phase produces are then identical and the blend is the identity.
        /// </para>
        /// </remarks>
        public double EnterRadius { get; init; }
    }

    private readonly CompiledProjectionPlan[] _plans;
    private readonly ArchetypeReplicationState[] _states;

    /// <summary>The per-archetype replication states. Exposed for the counters the track reports, never for the tick path.</summary>
    internal ArchetypeReplicationState[] ReplicationStates => _states;
    private readonly ArchetypeClusterState[] _clusterStates;

    /// <summary>The frame stage, read for whether a session's next frame will be incremental. Set by the runtime once both exist.</summary>
    internal FrameAssembler Frames;

    /// <summary>Whether the sparse topology path is enabled. <see cref="SubscriptionsOptions.SparseTopology"/>.</summary>
    internal bool SparseTopology;

    /// <summary>The most sessions one cell group may hold; zero for the automatic cap. <see cref="SubscriptionsOptions.InterestGroupCap"/>.</summary>
    internal int GroupCap;

    // Cluster size against the cells that host it, sampled every ClusterSizeEvery ticks.
    private const long ClusterSizeEvery = 64;
    private long _clusterSizeSamples;
    private double _clusterRadiusSum;
    private double _clusterRadiusMax;
    private double _gridCellSide;
    private double _interestCellSide;


    /// <summary>Per session this tick: whether it takes the sparse path. Decided in the prologue, read by the workers and the frame stage.</summary>
    private bool[] _tickSparse = [];

    /// <summary>
    /// Per session this tick: whether its next frame is incremental — its view holds exactly what its last published frame described. Stationary retention
    /// rests on it: re-emitting a held mask is only the kernel's answer when the mask is the one the kernel computed last tick.
    /// </summary>
    private bool[] _tickReady = [];

    /// <summary>Per session this tick: how many runs the sparse path did not emit — what the frame stage adds back to reach the full path's run count.</summary>
    private int[] _tickSparseSkipped = [];

    /// <summary>How many runs the sparse path skipped for this tick's session <paramref name="index"/>.</summary>
    /// <param name="index">The session's index.</param>
    /// <returns>The count.</returns>
    public int SparseSkippedOf(int index) => (uint)index < (uint)_tickSparseSkipped.Length ? _tickSparseSkipped[index] : 0;

    /// <summary>Whether this tick's session <paramref name="index"/> takes the sparse path.</summary>
    /// <param name="index">The session's index in this tick's arrays.</param>
    /// <returns><see langword="true"/> when its runs name only membership changes.</returns>
    public bool IsSparse(int index) => (uint)index < (uint)_tickSparse.Length && _tickSparse[index];

    /// <summary>Per archetype, this tick's clusters opened once and shared by every interest cell. See <see cref="ClusterSnapshotStore"/>.</summary>
    private readonly ClusterSnapshotStore[] _snapshots;
    private readonly SessionTable _sessions;
    private readonly ExclusiveWindow _fenceWindow;
    private readonly CompiledProfile[] _profiles;
    private readonly Dictionary<string, int> _profileByName = new(StringComparer.Ordinal);

    private HitArena[] _arenas = [];
    private SessionId[] _tickSessions = [];
    private int[] _tickProfiles = [];
    private SessionHitRange[] _tickHits = [];
    private Vector3D[] _tickViewpoints = [];
    private bool[] _tickPlaced = [];
    private int _tickSessionCount;
    private long _tickNumber;

    /// <summary>
    /// The interest cell each session's viewpoint falls in, packed, and the group boundaries a sort by it produces.
    /// </summary>
    /// <remarks>
    /// <b>The cell is the unit the broad phase is shared over.</b> Sessions are ordered by it so that everyone standing in one cell is contiguous and lands
    /// on one worker, which is what lets a cell's query run once instead of once per session. The key packs the profile with the cell because two profiles
    /// have two radii and therefore two different cells; a session whose coordinates fall outside the packable range is given a group of its own and takes
    /// the direct path, because a key that wrapped would merge two distant cells and resolve both around the wrong centre.
    /// </remarks>
    private long[] _tickCellKeys = [];

    /// <summary>Scratch for the order the sort produces, so the parallel tick arrays can be permuted in place.</summary>
    private int[] _sortIndices = [];
    private SessionId[] _permSessions = [];
    private int[] _permProfiles = [];
    private Vector3D[] _permViewpoints = [];
    private bool[] _permPlaced = [];

    /// <summary>Half the diagonal of an interest cell, as a multiple of the cell's side.</summary>
    private const double HalfDiagonal = 0.70710678118654752d;

    /// <summary>
    /// Interest cell side, as a fraction of the observer radius.
    /// </summary>
    /// <remarks>
    /// <b>A third of the radius, measured rather than chosen.</b> <c>InterestCostTests</c> sweeps the geometry at the density 13-density.md's d05 measures:
    /// at one cell per radius (192 m here) a conservative broad phase covers 2.25x the disc and NONE of it is shareable without a per-observer test, because
    /// the circle clips every cell it touches. At a third of the radius it covers 1.43x and 52 % needs no test; at a sixth, 1.24x and 82 %. A third is where
    /// the enlarged query stays cheap — its radius grows by half a cell diagonal, so 1.53x the area — while the shareable interior is already the majority.
    /// It is derived from the OBSERVER, not from any world size, so it carries to a deployment with a different map.
    /// </remarks>
    internal const double CellsPerRadius = 3d;

    /// <summary>The interest cell side for an observer radius — the unit sessions are grouped by.</summary>
    /// <param name="radius">The observer radius.</param>
    /// <returns>The cell side, in world units.</returns>
    internal static double CellSideFor(double radius) => radius / CellsPerRadius;

    /// <summary>Cells this tick's broad phase resolved, and sessions served from a shared cell resolution.</summary>
    private long _cellsResolved;
    private long _sessionsShared;

    /// <summary>Whether the interest stage times its own phases. <c>SubscriptionsOptions.MeasureInterestPhases</c>.</summary>
    private readonly bool _measurePhases;

    /// <summary>
    /// What entered and left for the session currently being resolved, accumulated across its clusters.
    /// </summary>
    /// <remarks>
    /// <b>Per worker by construction, not by declaration.</b> A chunk resolves one session at a time to completion, and a session belongs to exactly one
    /// chunk, so these are only ever touched by the thread resolving that session — but they are INSTANCE fields on a pass shared by every worker, which
    /// would be a race if two chunks were ever inside <see cref="FlushSphereRun"/> at once. They are therefore [ThreadStatic].
    /// </remarks>
    [ThreadStatic]
    private static ulong _sessionEntered;

    [ThreadStatic]
    private static ulong _sessionLeft;

    // ── The observer census ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // HOW FAST THE VIEWPOINTS MOVE, which is the one quantity the enter and leave rates are governed by and the only one nothing reported. An observer
    // advancing `d` sweeps a lune of about 2Rd out of the back of its disc every tick, so at a known density the number of entities it MUST drop is
    // arithmetic — and a leave rate is only surprising relative to that number. Read against the entity motion census and the two answer different
    // questions: entities move at the mean of the whole population, of which most are idle, while the leave rate is set by the observers alone.
    //
    // Kept per session SLOT with its generation, because this tick's arrays are ordered by interest cell and a session's index is not stable between
    // ticks. Accumulated in millimetres as an integer: the sum runs to billions over a measurement and a double would stop being exact.
    private Vector3D[] _lastViewpoint = [];
    private ushort[] _lastViewpointGeneration = [];
    private long _observerSteps;
    private long _observerMillimetresMoved;
    private long _observerPlaced;

    /// <summary>Whether co-located sessions share one query. <see cref="SubscriptionsOptions.CellKeyedInterest"/>.</summary>
    private readonly bool _cellKeyed;

    // ── Dynamic group dispatch (17 § 17) ────────────────────────────────────────────────────────────────────────────────────────────────────────────
    //
    // The unit of interest work is a CELL GROUP, not a session: one query serves everyone in the cell, so a group has to stay together and its members
    // have to be contiguous in the arena. A static split of the session list gives every chunk the same number of SESSIONS, which is the wrong quantity
    // twice over — a chunk can get one crowded cell or thirty empty ones, and a crowded cell costs one query plus n filters while thirty empty ones cost
    // thirty queries.
    //
    // The groups are formed once in the serial prologue, where the sessions have just been sorted by cell, and handed out from a shared cursor. That also
    // fixes the split-group artefact of the static form: a cell straddling a chunk boundary was queried once per chunk that held part of it.
    private int[] _groupStart = [];
    private int[] _groupCount = [];
    private int _groupTotal;
    private int _groupCursor;
    private int _tickWorkerCount = 1;

    /// <summary>
    /// Each session's current interest membership, or <see langword="null"/> when the pass runs in Phase 1's shape.
    /// </summary>
    /// <remarks>
    /// <b>With it, a run carries a DIFFERENCE; without it, a run carries a hit set</b> (15 § 3.2). The pass compares this tick's per-cluster slot mask
    /// against the one the session's last published frame described — one 64-bit operation per cluster — and hands the frame stage the slots that entered,
    /// the identities that left, and nothing else. The frame stage then reads the entered slots plus whichever retained slots S1 marked changed, which at
    /// the density 13 § 6 measures is ~300 of ~2 000. The comparison CANNOT be made here against the change mask, because S1 has not run yet on this tick's
    /// track order; what interest supplies is the half of the question the block cannot answer — which slots are new to THIS session.
    /// </remarks>
    private readonly SessionViewStore _views;

    /// <summary>Whether this pass expresses interest as a difference against each session's previous membership.</summary>
    internal bool IncrementalInterest => _views != null;

    /// <summary>
    /// Whether every archetype a profile names is indexed on TWO axes, so its observers may share a broad phase.
    /// </summary>
    /// <remarks>
    /// <b>The shared narrowphase is two-dimensional, and so is the cluster narrowphase it repeats</b> — X and Y, with Z carried as an infinity sentinel.
    /// When the archetype's spatial field IS three-dimensional the engine's own loop adds a Z overlap test and a dz term, and the filter here does neither:
    /// it would admit entities outside the sphere in Z, and the one broad query is centred on a single member's Z, so a member standing higher or lower
    /// would lose candidates a query of its own would have returned. Both directions are silent. Rather than carry a half-correct Z through the candidate
    /// buffer, a profile that reaches any 3D archetype is refused the shared path and every one of its sessions resolves directly, which is exactly the
    /// behaviour that preceded this feature.
    /// </remarks>
    private readonly bool[] _profileIs2D;

    /// <summary>
    /// Resolves the declared profiles against the compiled plans and the engine's cluster states, once, at <c>Start</c>.
    /// </summary>
    /// <param name="engine">The engine whose clusters the observers walk.</param>
    /// <param name="plans">One compiled plan per replicated archetype.</param>
    /// <param name="states">The per-archetype replication state, parallel to <paramref name="plans"/>.</param>
    /// <param name="registry">The declarations, for the profiles.</param>
    /// <param name="sessions">The session table, whose open rows are this pass's input.</param>
    /// <param name="cellKeyedInterest">
    /// Whether sessions sharing an interest cell resolve from one query; see <see cref="SubscriptionsOptions.CellKeyedInterest"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">An observer names an archetype that has no projection, so it could never be replicated.</exception>
    /// <param name="views">Per-session interest membership, or <see langword="null"/> to resolve interest the way Phase 1 did.</param>
    /// <param name="measureInterestPhases">Whether the stage times its broad phase, narrow phase and run assembly separately.</param>
    public InterestPass(DatabaseEngine engine, CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states, SubscriptionsRegistry registry,
        SessionTable sessions, bool cellKeyedInterest = true, SessionViewStore views = null,
        bool measureInterestPhases = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sessions);

        _plans = plans;
        _states = states;
        _sessions = sessions;
        _cellKeyed = cellKeyedInterest;
        _measurePhases = measureInterestPhases;
        _views = views;
        _fenceWindow = engine.EpochManager.FenceWindow;

        _clusterStates = new ArchetypeClusterState[plans.Length];
        _snapshots = new ClusterSnapshotStore[plans.Length];
        for (var i = 0; i < plans.Length; i++)
        {
            var archetypeStates = engine._archetypeStates;
            var catalogId = plans[i].ArchetypeCatalogId;
            _clusterStates[i] = archetypeStates != null && catalogId < archetypeStates.Length ? archetypeStates[catalogId]?.ClusterState : null;
            _snapshots[i] = _clusterStates[i] != null ? new ClusterSnapshotStore() : null;
        }

        _profiles = CompileProfiles(registry, plans);
        _profileIs2D = new bool[_profiles.Length];
        for (var i = 0; i < _profiles.Length; i++)
        {
            _profileByName[_profiles[i].Name] = i;

            // Resolved once, at Start, because the spatial field's type cannot change afterwards and the tick path must not ask.
            var archetypes = _profiles[i].ArchetypeIndices;
            var twoDimensional = archetypes.Length > 0;
            for (var a = 0; a < archetypes.Length; a++)
            {
                var clusterState = _clusterStates[archetypes[a]];
                if (clusterState == null || !clusterState.SpatialSlot.HasSpatialIndex || clusterState.SpatialSlot.FieldInfo.FieldType.Is3D())
                {
                    twoDimensional = false;
                    break;
                }
            }

            _profileIs2D[i] = twoDimensional;
        }
    }

    /// <summary>Sessions this tick's chunks are partitioned over: the open sessions bound to a declared profile.</summary>
    public int TickSessionCount => _tickSessionCount;

    /// <summary>Hits produced last tick, across every worker.</summary>
    public long HitCount
    {
        get
        {
            var total = 0L;
            foreach (var arena in _arenas)
            {
                total += arena.Hits;
            }

            return total;
        }
    }

    /// <summary>
    /// Directory probes made last tick, across every worker. One per cluster run — never one per hit, which is the property
    /// <c>foundation/02 § 4</c> rests the map-over-flat-array decision on.
    /// </summary>
    public long DirectoryProbes
    {
        get
        {
            var total = 0L;
            foreach (var arena in _arenas)
            {
                total += arena.DirectoryProbes;
            }

            return total;
        }
    }

    /// <summary>Blocks marked watched last tick, across every worker. This is S1's partition and its chunk count.</summary>
    public int WatchedBlockCount
    {
        get
        {
            var total = 0;
            foreach (var arena in _arenas)
            {
                total += arena.WatchedBlocks.Count;
            }

            return total;
        }
    }

    /// <summary>Clusters hit last tick that carried no block, across every worker. The blocks step creates one for each.</summary>
    public int NewBlockCount
    {
        get
        {
            var total = 0;
            foreach (var arena in _arenas)
            {
                total += arena.NewBlocks.Count;
            }

            return total;
        }
    }

    /// <summary>Chunks that executed with the engine's EW-01 window open, across every worker.</summary>
    public long ChunksInsideOpenFenceWindow
    {
        get
        {
            var total = 0L;
            foreach (var arena in _arenas)
            {
                total += arena.ChunksInsideOpenWindow;
            }

            return total;
        }
    }

    /// <summary>Workers this pass has arenas for.</summary>
    public int ArenaCount => _arenas.Length;

    /// <summary>One worker's arena, for the stages that read the new-block and watched-block lists back.</summary>
    /// <param name="worker">The worker index, which is the chunk index that filled it.</param>
    /// <returns>The arena.</returns>
    public HitArena Arena(int worker) => _arenas[worker];

    /// <summary>The session at <paramref name="index"/> in this tick's partition.</summary>
    /// <param name="index">The index.</param>
    /// <returns>The identity.</returns>
    public SessionId SessionAt(int index) => _tickSessions[index];

    /// <summary>The runs a session's interest produced this tick, in cluster order within each archetype.</summary>
    /// <param name="index">The session's index in this tick's partition.</param>
    /// <returns>The runs, valid until the next <see cref="BeginTick"/>.</returns>
    public ReadOnlySpan<InterestRun> HitsOf(int index)
    {
        var range = _tickHits[index];
        return range.Count == 0 ? default : _arenas[range.Worker].Runs(range.Start, range.Count);
    }

    /// <summary>How many entities a session's runs carry this tick.</summary>
    /// <param name="index">The session's index in this tick's partition.</param>
    /// <returns>The hit count.</returns>
    public int HitsCountOf(int index) => _tickHits[index].Hits;

    /// <summary>Cluster candidates the cluster-granular broad phase collected this run, summed over the workers.</summary>
    /// <summary>Span handouts the projected-component mask dropped, against those it let through.</summary>
    public (long Suppressed, long Admitted) SpanClaims
    {
        get
        {
            var suppressed = 0L;
            var admitted = 0L;
            for (var i = 0; i < _states.Length; i++)
            {
                var cs = _states[i]?.ClusterState;
                if (cs == null)
                {
                    continue;
                }

                suppressed += cs.UnprojectedSpanClaims;
                admitted += cs.ProjectedSpanClaims;
            }

            return (suppressed, admitted);
        }
    }

    /// <summary>Clusters admitted whole by the box test, and the entity reads that avoided.</summary>
    public (long Clusters, long EntitiesSkipped) InteriorAdmission
    {
        get
        {
            var clusters = 0L;
            var skipped = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                clusters += _arenas[i].InteriorClustersAdmitted;
                skipped += _arenas[i].InteriorEntitiesSkipped;
            }

            return (clusters, skipped);
        }
    }

    /// <summary>Live slots the broad phase reached, and those whose structure changed. Cumulative.</summary>
    public (long Reached, long Changed) BroadSlots
    {
        get
        {
            long r = 0, c = 0;
            for (var i = 0; i < _arenas.Length; i++)
            {
                r += _arenas[i].BroadSlotsReached;
                c += _arenas[i].BroadSlotsChanged;
            }

            return (r, c);
        }
    }

    /// <summary>Runs a moving member's view retained in one pass instead of a probe each. Cumulative.</summary>
    public long RunsRetainedByView
    {
        get
        {
            var t = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                t += _arenas[i].RunsRetainedByView;
            }

            return t;
        }
    }

    /// <summary>Runs sparse sessions did not emit because their membership there did not move. Cumulative.</summary>
    public long SparseRunsSkipped
    {
        get
        {
            var t = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                t += _arenas[i].SparseRunsSkipped;
            }

            return t;
        }
    }

    /// <summary>Clusters opened to fill the shared snapshot, against those read from it without an open. Cumulative.</summary>
    public (long Opens, long Reads) SnapshotUse
    {
        get
        {
            long o = 0, r = 0;
            for (var i = 0; i < _arenas.Length; i++)
            {
                o += _arenas[i].SnapshotOpens;
                r += _arenas[i].SnapshotReads;
            }

            return (o, r);
        }
    }

    /// <summary>Clusters the broad phase reached whose structure changed this tick, cumulative.</summary>
    public long BroadClustersStructureChanged
    {
        get
        {
            var t = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                t += _arenas[i].BroadClustersStructureChanged;
            }

            return t;
        }
    }

    /// <summary>Distinct clusters every cell's broad phase reached this run, against the entities it collected from them.</summary>
    public long BroadClustersReached
    {
        get
        {
            var total = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                total += _arenas[i].BroadClustersReached;
            }

            return total;
        }
    }

    /// <summary>Entity candidates every cell's broad phase reached this run, cumulative — the partner of <see cref="BroadClustersReached"/>.</summary>
    public long EntityCandidatesCollected
    {
        get
        {
            var total = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                total += _arenas[i].BroadEntitiesReached;
            }

            return total;
        }
    }

        /// <summary>Cluster candidates a session accepted, summed over every session and worker of this run.</summary>
        /// <summary>
    /// Whether two sessions on this session's profile necessarily reach the same clusters, which is what makes a frame encode shareable between them.
    /// </summary>
    /// <param name="index">The session's index in this tick's partition.</param>
    /// <returns><see langword="true"/> only for an observer whose region does not depend on where the session is standing.</returns>
    /// <remarks>
    /// <b>A <c>World</c> observer reaches every cluster of its archetypes, so any two sessions bound to one such profile see the same entities and a frame
    /// built for either describes both.</b> A <c>Sphere</c> observer does not: its members stand in different places, and two of them agreeing on how many
    /// records they emit says nothing about WHICH. That is the whole of the distinction the frame stage's share key cannot make for itself.
    /// </remarks>
    public bool SharesFramesByProfile(int index) => _profiles[_tickProfiles[index]].Kind == ObserverKind.World;

    /// <summary>The identities this session's interest stopped reaching this tick, for the frame stage to turn into LEAVE records.</summary>
    /// <param name="index">The session's index in this tick's partition.</param>
    /// <returns>The span, empty when the pass runs in Phase 1's shape or nothing left.</returns>
    public ReadOnlySpan<InterestLeave> LeavesOf(int index)
    {
        ref readonly var range = ref _tickHits[index];
        return range.LeaveCount == 0 ? default : _arenas[range.Worker].Leaves(range.LeaveStart, range.LeaveCount);
    }

    /// <summary>How many runs a session's interest produced this tick.</summary>
    /// <param name="index">The session's index in this tick's partition.</param>
    /// <returns>The run count.</returns>
    public int RunCountOf(int index) => _tickHits[index].Count;

    /// <summary>Which worker resolved a session this tick, which is also the arena its runs live in.</summary>
    /// <param name="index">The session's index in this tick's partition.</param>
    /// <returns>The worker index.</returns>
    public int WorkerOf(int index) => _tickHits[index].Worker;

    /// <summary>One run of a session's interest, for a caller that cannot hold a <see cref="ReadOnlySpan{T}"/>.</summary>
    /// <param name="index">The session's index in this tick's partition.</param>
    /// <param name="run">The run's position in that session's window.</param>
    /// <returns>The run.</returns>
    public InterestRun RunOf(int index, int run)
    {
        var range = _tickHits[index];
        return _arenas[range.Worker].Runs(range.Start, range.Count)[run];
    }

    // ── The stage's parallel shape (mirrors the frame stage's, 17 § 17) ────────────────────────────────────────────────────────────────────────────
    //
    // A stage's wall time is its slowest chunk. Summed busy over span is the effective worker count; the start spread says whether the chunks ran at the
    // same time at all; the heaviest group says whether one cell's crowd is the straggler that no cursor can split. Written by one chunk each, folded by
    // the next tick's prologue, the stage's one single-threaded point.
    private long[] _chunkBusy = [];
    private long[] _chunkStart = [];
    private long[] _chunkEnd = [];
    private int _lastChunkCount;
    private long _spanTicks;
    private long _busySumTicks;
    private long _busyMaxTicks;
    private long _startSpreadTicks;
    private long _heaviestGroupTicks;
    private long _heaviestGroupMembers;
    private long _spanCount;
    private long _prologueTicks;
    private long _prologueCount;

    private void FoldChunkSpan()
    {
        var sum = 0L;
        var max = 0L;
        var first = long.MaxValue;
        var lastStart = 0L;
        var last = 0L;
        for (var i = 0; i < _lastChunkCount && i < _chunkBusy.Length; i++)
        {
            if (_chunkStart[i] == 0)
            {
                continue;
            }

            sum += _chunkBusy[i];
            max = Math.Max(max, _chunkBusy[i]);
            first = Math.Min(first, _chunkStart[i]);
            lastStart = Math.Max(lastStart, _chunkStart[i]);
            last = Math.Max(last, _chunkEnd[i]);
            _chunkStart[i] = 0;
        }

        var heaviest = 0L;
        var members = 0;
        for (var w = 0; w < _arenas.Length; w++)
        {
            var arena = _arenas[w];
            if (arena != null && arena.HeaviestGroupTicks > heaviest)
            {
                heaviest = arena.HeaviestGroupTicks;
                members = arena.HeaviestGroupMembers;
            }

            if (arena != null)
            {
                arena.HeaviestGroupTicks = 0;
            }
        }

        if (last > first && first != long.MaxValue)
        {
            _spanTicks += last - first;
            _busySumTicks += sum;
            _busyMaxTicks += max;
            _startSpreadTicks += lastStart - first;
            _heaviestGroupTicks += heaviest;
            _heaviestGroupMembers += members;
            _spanCount++;
        }
    }

    /// <summary>
    /// Per tick, averaged: the stage's wall span, the CPU its chunks summed, the slowest chunk, how late the last chunk started, the heaviest single group
    /// and its member count, and the serial prologue.
    /// </summary>
    public (double SpanMs, double BusyMs, double MaxChunkMs, double StartSpreadMs, double HeaviestGroupMs, double HeaviestGroupMembers, double PrologueMs)
        ChunkSpan
    {
        get
        {
            var n = Math.Max(1L, _spanCount);
            var ms = 1000d / Stopwatch.Frequency;
            return (_spanTicks * ms / n, _busySumTicks * ms / n, _busyMaxTicks * ms / n, _startSpreadTicks * ms / n, _heaviestGroupTicks * ms / n,
                (double)_heaviestGroupMembers / n, _prologueTicks * ms / Math.Max(1L, _prologueCount));
        }
    }

    /// <summary>
    /// The prologue and the tick's plan: clears the masks this pass set last tick, then partitions the open sessions that have a profile.
    /// </summary>
    /// <param name="tickNumber">The tick.</param>
    /// <param name="workerCount">Worker-pool width.</param>
    /// <returns>Chunks the stage should dispatch; zero when no session has interest, which costs the track its gate and nothing else.</returns>
    /// <remarks>
    /// Single-threaded, run by the scheduler before the dispatch — the same hook the Fence DAG's phases use, which is why
    /// <c>foundation/03 § 2.5</c> puts serial replication work here instead of in a stage of its own that would cost a whole barrier.
    /// </remarks>
    public int BeginTick(long tickNumber, int workerCount)
    {
        var prologueFrom = Stopwatch.GetTimestamp();
        FoldChunkSpan();
        _tickNumber = tickNumber;
        var workers = Math.Max(1, workerCount);
        EnsureArenas(workers);

        // Sized here, single-threaded, so the stores never grow under the workers that fill them. Chunk ids are bounded by the cluster AABB table, which
        // covers every id that can have bounds; interest runs after the fence, so no cluster is created while the stage runs.
        for (var a = 0; a < _snapshots.Length; a++)
        {
            var aabbs = _clusterStates[a]?.ClusterAabbs;
            if (_snapshots[a] != null && aabbs != null)
            {
                _snapshots[a].EnsureCapacity(aabbs.Length);
            }

            if (aabbs != null)
            {
                for (var w = 0; w < _arenas.Length; w++)
                {
                    _arenas[w]?.EnsureDeferCapacity(a, _clusterStates.Length, aabbs.Length);
                }
            }
        }

        // The prologue: last tick's marks are dropped, so a slot that is no longer reached by anybody reads as unwatched rather than as stale. Plain stores —
        // the previous dispatch has joined and the next has not begun, so there is no other thread to order against.
        for (var w = 0; w < _arenas.Length; w++)
        {
            var arena = _arenas[w];
            var watched = arena.WatchedBlocks;
            for (var i = 0; i < watched.Count; i++)
            {
                ((ReplicationBlockHeader*)watched[i])->WatchedMask = 0;
            }

            arena.BeginTick();
        }

        // ── These are NOT reset here, and the reason is a measurement that was read wrong ────────────────────────────────────────────────────────────
        //
        // They used to be per tick. A run at d07 then reported "0 cells resolved, 0 sessions shared" in every one of its reports, which was read as the
        // cell grouping failing to engage at the one density where it matters — and written up as a defect. It was not. The reports were printed after
        // the bot swarm had disconnected, and a LAST-TICK counter with no session left to resolve is zero however well the tick before it went. Measured
        // again with sessions attached: 302 of 400 sessions shared, 74 cells, at the same density and the same binary.
        //
        // That is 18 § 8.4's lesson arriving a second time through a different counter: a per-tick figure read by a reporting system with no fence
        // against the tick produces a plausible zero, and a plausible zero is believed. Cumulative, a torn read costs recency and never a wrong ratio,
        // and "no sessions right now" can no longer impersonate "sharing is broken".
        _tickSessionCount = 0;
        foreach (var session in _sessions)
        {
            var profile = ProfileIndexOf(session);
            if (profile < 0)
            {
                continue;
            }

            if (_tickSessionCount == _tickSessions.Length)
            {
                var grown = Math.Max(16, _tickSessions.Length * 2);
                Array.Resize(ref _tickSessions, grown);
                Array.Resize(ref _tickProfiles, grown);
                Array.Resize(ref _tickHits, grown);
                Array.Resize(ref _tickViewpoints, grown);
                Array.Resize(ref _tickPlaced, grown);
                Array.Resize(ref _tickSparse, grown);
                Array.Resize(ref _tickReady, grown);
                Array.Resize(ref _tickSparseSkipped, grown);
                Array.Resize(ref _tickDisplacement, grown);
                Array.Resize(ref _tickCellKeys, grown);
                Array.Resize(ref _sortIndices, grown);
                Array.Resize(ref _permSessions, grown);
                Array.Resize(ref _permProfiles, grown);
                Array.Resize(ref _permViewpoints, grown);
                Array.Resize(ref _permPlaced, grown);
                Array.Resize(ref _permDisplacement, grown);
            }

            // Read HERE, single-threaded, rather than inside the chunk. The table's viewpoint slot is written by application systems on the tick thread and
            // read by every worker; snapshotting it once at the partition point means the chunks read a private array instead of racing the table, and it
            // also fixes the tick's answer — a session cannot resolve two archetypes around two different centres.
            _tickPlaced[_tickSessionCount] = _sessions.TryGetViewpoint(session, out var viewpoint);
            _tickViewpoints[_tickSessionCount] = viewpoint;
            NoteObserverMotion(session, viewpoint, _tickPlaced[_tickSessionCount]);
            _tickProfiles[_tickSessionCount] = profile;
            _tickHits[_tickSessionCount] = default;
            _tickSessions[_tickSessionCount++] = session;
        }

        _tickWorkerCount = Math.Max(1, workers);

        OrderSessionsByCell();

        // UNCONDITIONALLY, and outside the ordering: the chunks take their work from this list whether or not cell keying reordered anything. Building it
        // inside OrderSessionsByCell left it empty on every arm that returns early — cell keying off, or no session at all — and a chunk then found no
        // group to take and resolved nothing. Every hit count in the suite went to zero, which is what a dispatch with no work looks like from outside.
        BuildGroups();

        var chunkCount = Math.Min(workers, _tickSessionCount);
        if (_chunkBusy.Length < chunkCount)
        {
            Array.Resize(ref _chunkBusy, Math.Max(16, chunkCount));
            Array.Resize(ref _chunkStart, _chunkBusy.Length);
            Array.Resize(ref _chunkEnd, _chunkBusy.Length);
        }

        _lastChunkCount = chunkCount;
        if (_measurePhases && _tickNumber % ClusterSizeEvery == 0)
        {
            SampleClusterSizes();
        }

        _prologueTicks += Stopwatch.GetTimestamp() - prologueFrom;
        _prologueCount++;

        // The chunk count is bounded by the SESSION count, not by the group count. Bounding it by groups would put a cell holding every session on one
        // worker — which is the crowd this feature exists for — and ExecuteChunk splits a group across chunks when it has to (see its remarks).
        return Math.Min(workers, _tickSessionCount);
    }

    /// <summary>
    /// Orders this tick's sessions so that everyone sharing an interest cell is contiguous.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ordering only.</b> The groups themselves are formed by <see cref="BuildGroups"/>, which the prologue calls next and calls on every arm — this
    /// method returns early when cell keying is off, and a group list built from inside it would then be empty.
    /// </para>
    /// <para>
    /// <b>Unplaced and non-sphere sessions carry <see cref="NoCellKey"/></b>, which never groups. They have no cell, and giving them one would be inventing a
    /// viewpoint; the direct path they take is the one every session took before this existed.
    /// </para>
    /// <para>
    /// <b>With the feature off nothing is reordered at all</b>, so the arm this is measured against is the shape that preceded it rather than that shape plus
    /// a sort.
    /// </para>
    /// </remarks>
    private void OrderSessionsByCell()
    {
        if (_tickSessionCount == 0)
        {
            return;
        }

        // With the feature off the tick must be the shape it was BEFORE it existed, not that shape plus a sort. Leaving the ordering in place would have put
        // its cost — an O(S log S) sort and five array permutes on the serial prologue — into the arm it is being compared against, and a measurement whose
        // control carries part of the treatment is not a measurement.
        if (!_cellKeyed)
        {
            return;
        }

        for (var i = 0; i < _tickSessionCount; i++)
        {
            _sortIndices[i] = i;
            _tickCellKeys[i] = CellKeyOf(i);

            // #983-adjacent diagnostic: separate "no session could be keyed" from "every session got its own key". Those have opposite causes and the
            // sharing counters cannot tell them apart — both report zero cells resolved.
            if (_tickCellKeys[i] == NoCellKey)
            {
                _diagUngroupable++;
            }
            else
            {
                _diagKeyed++;
                var vp = _tickViewpoints[i];
                _diagMinX = Math.Min(_diagMinX, vp.X);
                _diagMaxX = Math.Max(_diagMaxX, vp.X);
                _diagMinY = Math.Min(_diagMinY, vp.Y);
                _diagMaxY = Math.Max(_diagMaxY, vp.Y);
                _diagCellSide = CellSideFor(_profiles[_tickProfiles[i]].QueryRadius);
            }
        }

        // Sorted by the key, so equal keys are adjacent. Array.Sort over the key array with the index array as items is the standard permutation sort and it
        // is O(n log n) on the session count, once per tick, single-threaded — against the O(sessions x hits) it saves.
        Array.Sort(_tickCellKeys, _sortIndices, 0, _tickSessionCount);

        for (var i = 0; i < _tickSessionCount; i++)
        {
            var from = _sortIndices[i];
            _permSessions[i] = _tickSessions[from];
            _permProfiles[i] = _tickProfiles[from];
            _permViewpoints[i] = _tickViewpoints[from];
            _permPlaced[i] = _tickPlaced[from];
            _permDisplacement[i] = _tickDisplacement[from];
        }

        Array.Copy(_permSessions, _tickSessions, _tickSessionCount);
        Array.Copy(_permProfiles, _tickProfiles, _tickSessionCount);
        Array.Copy(_permViewpoints, _tickViewpoints, _tickSessionCount);
        Array.Copy(_permPlaced, _tickPlaced, _tickSessionCount);
        Array.Copy(_permDisplacement, _tickDisplacement, _tickSessionCount);
    }

    /// <summary>Decides, per session, whether it takes the sparse path this tick. Serial; after the sessions are in their final order.</summary>
    /// <remarks>
    /// The condition is the frame stage's own — the session's next frame is incremental — read here because the frame stage has finished the previous
    /// tick and not begun this one, so nothing it depends on can move between this decision and the gather that relies on it.
    /// </remarks>
    private void DecideSparse()
    {
        var frames = Frames;
        for (var i = 0; i < _tickSessionCount; i++)
        {
            var ready = frames != null && _views != null && frames.WillGatherIncrementally(_tickSessions[i], _tickNumber);
            _tickReady[i] = ready && frames.CanRetainStationary(_tickSessions[i], _tickNumber);
            _tickSparse[i] = SparseTopology && ready;
        }
    }

    /// <summary>
    /// Forms this tick's cell groups from the sorted session list, once, so the chunks can take them from a cursor instead of splitting by count.
    /// </summary>
    /// <remarks>
    /// <b>Here and not in the chunk, and that is a correctness point as well as a balance one.</b> Forming groups inside a chunk means a cell whose members
    /// straddle a chunk boundary becomes two groups, each paying its own enlarged query — the crowd this feature exists for is exactly the crowd most likely
    /// to straddle. Formed once over the whole sorted list, a cell is one group however the work is later divided.
    /// </remarks>
    private void BuildGroups()
    {
        DecideSparse();

        if (_groupStart.Length < _tickSessionCount)
        {
            Array.Resize(ref _groupStart, Math.Max(16, _tickSessionCount));
            Array.Resize(ref _groupCount, Math.Max(16, _tickSessionCount));
        }

        // ── Why a crowded cell is still cut into pieces ────────────────────────────────────────────────────────────────────────────────────────────
        //
        // A group is resolved by ONE worker, so a cell holding six hundred of a thousand sessions would put six hundred narrowphases on one thread while
        // thirty-one others idled — which is the workload this whole feature exists for. The static split avoided that by accident, by forming groups
        // inside each chunk's range; measured with the cap removed, `cells resolved` fell from ~50 to 18 and interest went from 4.5 ms to 12.5.
        //
        // So a large cell is cut into pieces of at most this many sessions, each paying its own enlarged query exactly as a chunk-split group used to.
        // The cap is the mean chunk size, which is what the static form produced, and it is a balance knob rather than a policy: pieces smaller than this
        // buy nothing and pay another query each.
        var cap = GroupCap > 0 ? GroupCap : Math.Max(8, _tickSessionCount / Math.Max(1, _tickWorkerCount));

        _groupTotal = 0;
        _groupCursor = 0;
        if (_tickSessionCount == 0)
        {
            return;
        }

        var i = 0;
        while (i < _tickSessionCount)
        {
            var key = _tickCellKeys[i];
            var count = 1;
            if (_cellKeyed && key != NoCellKey)
            {
                while (i + count < _tickSessionCount && _tickCellKeys[i + count] == key && count < cap)
                {
                    count++;
                }
            }

            _groupStart[_groupTotal] = i;
            _groupCount[_groupTotal] = count;
            _groupTotal++;
            i += count;
        }

        _groupCursor = 0;
    }

    /// <summary>How far each of this tick's sessions moved since it was last resolved, or infinity when it has no previous viewpoint.</summary>
    private double[] _tickDisplacement = [];

    /// <summary>Permutation scratch for <see cref="_tickDisplacement"/>, sorted with the rest when sessions are grouped by cell.</summary>
    private double[] _permDisplacement = [];

    /// <summary>The key meaning "this session cannot be grouped": it takes the direct path alone.</summary>
    private const long NoCellKey = long.MinValue;

    /// <summary>Packs the interest cell a session's viewpoint falls in, with its profile.</summary>
    /// <param name="index">The session's index in this tick's arrays.</param>
    /// <returns>The packed key, or <see cref="NoCellKey"/> when the session cannot share a resolution.</returns>
    private long CellKeyOf(int index)
    {
        var profileIndex = _tickProfiles[index];
        var profile = _profiles[profileIndex];
        if (profile.Kind != ObserverKind.Sphere || !_tickPlaced[index] || profile.QueryRadius <= 0d || !_profileIs2D[profileIndex])
        {
            return NoCellKey;
        }

        var cell = CellSideFor(profile.QueryRadius);
        var viewpoint = _tickViewpoints[index];
        var fx = Math.Floor(viewpoint.X / cell);
        var fy = Math.Floor(viewpoint.Y / cell);

        // Twenty-three bits per axis, so +/- 4 million cells — 256 000 km at the demo's 64 m cell, and 4 000 km at a one-metre one. Outside that the key
        // would wrap and two distant cells would share a group, resolving both around a centre that contains neither; a session there takes the direct path.
        const double Limit = 4_000_000d;
        if (!(Math.Abs(fx) < Limit) || !(Math.Abs(fy) < Limit) || profileIndex >= 1 << 14)
        {
            return NoCellKey;
        }

        var cx = (long)fx + 0x400000L;
        var cy = (long)fy + 0x400000L;
        return ((long)profileIndex << 48) | (cx << 24) | cy;
    }

    /// <summary>Accumulates how far one session's viewpoint moved since the last tick it was placed on.</summary>
    /// <param name="session">The session.</param>
    /// <param name="viewpoint">Its viewpoint this tick.</param>
    /// <param name="placed">Whether it has one at all.</param>
    /// <remarks>
    /// A session that was not placed last tick contributes no step, only a placement: an observer appearing out of nowhere has not travelled the distance
    /// between where it was not and where it now is, and counting that would put every admission into the mean.
    /// </remarks>
    private void NoteObserverMotion(SessionId session, Vector3D viewpoint, bool placed)
    {
        // Written before any early return: this slot is reused every tick, and a stale zero left by a previous occupant would read as "did not move".
        // Infinity means "unknown distance", which the topology maintenance reads as "cannot reuse".
        _tickDisplacement[_tickSessionCount] = double.PositiveInfinity;
        if (!placed)
        {
            return;
        }

        var slot = session.Slot;
        if (slot >= _lastViewpoint.Length)
        {
            var grown = Math.Max(16, Math.Max(slot + 1, _lastViewpoint.Length * 2));
            Array.Resize(ref _lastViewpoint, grown);
            Array.Resize(ref _lastViewpointGeneration, grown);
        }

        _observerPlaced++;
        if (_lastViewpointGeneration[slot] == session.Generation)
        {
            var previous = _lastViewpoint[slot];
            var dx = viewpoint.X - previous.X;
            var dy = viewpoint.Y - previous.Y;
            var dz = viewpoint.Z - previous.Z;
            _observerSteps++;
            var moved = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            _observerMillimetresMoved += (long)(moved * 1000d);
            _tickDisplacement[_tickSessionCount] = moved;

            // Exactly zero: a bit-identical viewpoint over a cluster with no structure change has identical inputs, so an identical answer.
            if (dx == 0d && dy == 0d && dz == 0d)
            {
                _observerStationary++;
            }
        }

        _lastViewpoint[slot] = viewpoint;
        _lastViewpointGeneration[slot] = session.Generation;
    }

    /// <summary>
    /// How far the OBSERVERS moved: steps compared, total displacement in millimetres, and placed sessions seen — cumulative since start.
    /// </summary>
    /// <remarks>
    /// <b>The input the enter and leave rates are consequences of.</b> An observer that advances <c>d</c> in a tick must drop about <c>2Rd x density</c>
    /// entities out of the back of its disc and take up as many at the front, whatever the engine does; a churn figure read without it is a number with no
    /// expected value beside it. It is NOT the entity motion census: that averages over the whole replicated population, most of which stands still, and
    /// using it to predict churn understates the answer by the ratio between the two.
    /// </remarks>
    public (long Steps, long MillimetresMoved, long Placed) ObserverMotion =>
        (Volatile.Read(ref _observerSteps), Volatile.Read(ref _observerMillimetresMoved), Volatile.Read(ref _observerPlaced));

    /// <summary>Session-ticks whose viewpoint was bit-identical to the previous tick's.</summary>
    public long ObserverStationary => Volatile.Read(ref _observerStationary);

    private long _observerStationary;

    /// <summary>Runs a session re-emitted from its maintained topology without running the kernel, cumulative.</summary>
    public long TopologyRunsRetained
    {
        get
        {
            var t = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                t += _arenas[i].StationaryRunsRetained;
            }

            return t;
        }
    }

    /// <summary>
    /// Where the interest stage's time has gone since start, in microseconds: the shared broad query, the per-entity narrow filter, and the run assembly.
    /// </summary>
    /// <remarks>Populated only under <c>SubscriptionsOptions.MeasureInterestPhases</c>. The direct path cannot separate broad from narrow and charges
    /// both to <c>Narrow</c>; see <c>ResolveSessionDirect</c>.</remarks>
    public (double BroadUs, double NarrowUs, double FlushUs) InterestPhases
    {
        get
        {
            long broad = 0, narrow = 0, flush = 0;
            for (var w = 0; w < _arenas.Length; w++)
            {
                var p = _arenas[w].PhaseTicks;
                broad += p.Broad;
                narrow += p.Narrow;
                flush += p.Flush;
            }

            var perTick = 1_000_000d / Stopwatch.Frequency;
            return (broad * perTick, narrow * perTick, flush * perTick);
        }
    }

    /// <summary>
    /// How much of the interest answer has been what the sessions already held, since start: runs unchanged, and sessions whose entire answer was unchanged.
    /// </summary>
    /// <remarks>
    /// <b>The second pair is the ceiling on any residency or live-set scheme.</b> A session counted coherent produced no entered slot, no left slot and no
    /// departed cluster — its query could have been skipped and its previous answer reused, had the pass been able to prove nothing new had come into
    /// range. This does not supply that proof; it supplies the size of the prize.
    /// </remarks>
    public (long RunsUnchanged, long RunsTotal, long SessionsCoherent, long SessionsTotal) InterestCoherence
    {
        get
        {
            long ru = 0, rt = 0, sc = 0, st = 0;
            for (var w = 0; w < _arenas.Length; w++)
            {
                var r = _arenas[w].RunCoherence;
                var c = _arenas[w].SessionCoherence;
                ru += r.Unchanged;
                rt += r.Total;
                sc += c.Coherent;
                st += c.Total;
            }

            return (ru, rt, sc, st);
        }
    }

    /// <summary>
    /// How a session's CLUSTER set turns over, since start: clusters reached for the first time since the last published frame, clusters reached again,
    /// and clusters no longer reached at all.
    /// </summary>
    /// <remarks>
    /// <b>The coarser question in front of <see cref="InterestCoherence"/>.</b> That one asks whether a run's slot mask changed; this asks whether the
    /// cluster was in the set at all. An incremental candidate set edits the clusters that move and keeps the rest, so the churn fraction here is what
    /// such a scheme would still have to pay — and unlike whole-session coherence it does not collapse with the number of clusters a session holds.
    /// </remarks>
    public (long FirstSeen, long Carried, long Departed) RunFlow
    {
        get
        {
            long f = 0, c = 0, d = 0;
            for (var w = 0; w < _arenas.Length; w++)
            {
                var r = _arenas[w].RunFlow;
                f += r.FirstSeen;
                c += r.Carried;
                d += r.Departed;
            }

            return (f, c, d);
        }
    }

    /// <summary>
    /// Occupied slots in clusters lying wholly inside the enter radius, beside those in clusters the disc clips, since start.
    /// </summary>
    /// <remarks>
    /// <b>The size of the interior/boundary split</b> ([15 § 3.4]): a cluster wholly inside the disc holds no entity the session may not take, so every
    /// per-entity distance test against it is known to pass before it runs. Only populated on the cluster-granularity path, which is the one that holds
    /// a cluster's box — the cell path holds entity boxes and cannot answer it. The ratio is a property of the geometry and transfers between the two.
    /// </remarks>
    public (long Interior, long Boundary) ClusterContainment
    {
        get
        {
            long i = 0, b = 0;
            for (var w = 0; w < _arenas.Length; w++)
            {
                var c = _arenas[w].ClusterContainment;
                i += c.Interior;
                b += c.Boundary;
            }

            return (i, b);
        }
    }

    /// <summary>Slots the projection pass addressed, and the subset it named as changed, since start — across every archetype.</summary>
    /// <remarks>
    /// <b>The ceiling on dirty-driven projection.</b> S1 re-encodes and byte-compares every watched slot because the ECS raises no per-entity signal a
    /// projection may trust (SUB-10). The changed fraction is what a detector that could be trusted would leave it doing.
    /// </remarks>
    public (long Addressed, long Changed) ProjectionSlots
    {
        get
        {
            long a = 0, c = 0;
            for (var i = 0; i < _states.Length; i++)
            {
                if (_states[i] == null)
                {
                    continue;
                }

                a += _states[i].SlotsProjected;
                c += _states[i].ChangedSlotsPublished;
            }

            return (a, c);
        }
    }

    private long _diagUngroupable;
    private long _diagKeyed;
    private double _diagMinX = double.MaxValue;
    private double _diagMaxX = double.MinValue;
    private double _diagMinY = double.MaxValue;
    private double _diagMaxY = double.MinValue;
    private double _diagCellSide;

    /// <summary>
    /// Why sharing did or did not happen: sessions that could not be keyed at all, sessions that were, the span their viewpoints covered and the interest
    /// cell side. Cumulative for the counts; the span and the side are last tick's.
    /// </summary>
    /// <remarks>
    /// <b>Zero cells resolved has two opposite causes</b> and <see cref="CellsResolved"/> reports the same zero for both: every session failed a predicate
    /// in <c>CellKeyOf</c> and took the direct path, or every session was keyed and no two keys collided. The first is a bug, the second is geometry. The
    /// viewpoint span against the cell side says which, because a span of N cells cannot hold S sessions without collisions when S &gt; N squared.
    /// </remarks>
    public (long Ungroupable, long Keyed, double SpanX, double SpanY, double CellSide) GroupingDiagnostic =>
        (Volatile.Read(ref _diagUngroupable), Volatile.Read(ref _diagKeyed),
         _diagMaxX < _diagMinX ? 0d : _diagMaxX - _diagMinX,
         _diagMaxY < _diagMinY ? 0d : _diagMaxY - _diagMinY,
         _diagCellSide);

    /// <summary>
    /// Cluster runs accepted whole against runs the disc clipped, and the slots each accounted for. Cumulative since start.
    /// </summary>
    /// <remarks>
    /// Slice 2's measurement: 20 § 3.2 put 74.4 % of accepted slots in clusters wholly inside the enter radius, measured on the cluster-granularity arm.
    /// This reports what the cell path actually achieves, where the run's bound is the union of the candidate boxes rather than the cluster's stored AABB.
    /// </remarks>
        /// <summary>Interest cells the broad phase has resolved, cumulative since start.</summary>
    public long CellsResolved => Volatile.Read(ref _cellsResolved);

    /// <summary>Sessions served from a cell resolution somebody else paid for, cumulative since start.</summary>
    public long SessionsShared => Volatile.Read(ref _sessionsShared);

    /// <summary>Resolves the interest of the sessions this chunk owns.</summary>
    /// <param name="chunkIndex">The chunk, which is also the index of the arena it fills.</param>
    /// <param name="chunkCount">How many chunks the stage dispatched.</param>
    /// <remarks>
    /// <para>
    /// <b>Sessions are partitioned by index over an array ordered by interest cell</b>, so a cell's members are adjacent and usually land on one chunk. That
    /// is no longer the same partition <c>Frames</c> uses — it partitions the same index space, but this tick's order is the cell order, not the session
    /// table's — so the locality this used to claim with <c>Frames</c> is gone. Correctness does not depend on it: a session's runs are found through
    /// <see cref="SessionHitRange.Worker"/>, and <c>Frames</c> resolves identities through <see cref="SessionAt"/> rather than by assuming an order.
    /// </para>
    /// <para>
    /// <b>A cell straddling a chunk boundary is resolved by both chunks.</b> That repeats one broad query rather than serialising the cell's members onto one
    /// worker, which is the trade that matters: at eight workers a cell is queried at most eight times instead of once per session, and the alternative put
    /// two hundred sessions standing in one cell on a single thread.
    /// </para>
    /// </remarks>
    public void ExecuteChunk(int chunkIndex, int chunkCount)
    {
        if (chunkCount <= 0 || (uint)chunkIndex >= (uint)_arenas.Length)
        {
            return;
        }

        var arena = _arenas[chunkIndex];
        var chunkFrom = Stopwatch.GetTimestamp();

        // Accumulated in locals and flushed once below: see HitArena's remarks on why a counter moved per cluster would be a shared line.
        var probes = 0L;
        var hits = 0L;
        var cells = 0L;
        var shared = 0L;

        // Groups, not sessions, and from a cursor rather than a slice: see BuildGroups. A chunk takes the next group until there are none left, so a
        // worker that drew a crowded cell is not also holding thirty empty ones behind it.
        while (true)
        {
            var g = Interlocked.Increment(ref _groupCursor) - 1;
            if (g >= _groupTotal)
            {
                break;
            }

            var i = _groupStart[g];
            var count = _groupCount[g];
            var groupFrom = Stopwatch.GetTimestamp();
            // A cell of ONE still takes the cell path when it has a key. The direct path opens every cluster it reaches through the enumerator, which is
            // exactly the redundant open the shared snapshot removed; profiled at a thousand sessions, the ~13 % of sessions alone in their cell were 39 %
            // of this stage for that reason. Keyless sessions — unplaced, non-sphere, 3D — still go direct: they have no cell to be resolved as.
            if (count > 1 || (_cellKeyed && _tickCellKeys[i] != NoCellKey))
            {
                ResolveCellGroup(arena, chunkIndex, i, count, ref probes, ref hits);
                cells++;
                shared += count - 1;
            }
            else
            {
                hits += ResolveSessionDirect(arena, chunkIndex, i, ref probes);
            }

            var groupTicks = Stopwatch.GetTimestamp() - groupFrom;
            if (groupTicks > arena.HeaviestGroupTicks)
            {
                arena.HeaviestGroupTicks = groupTicks;
                arena.HeaviestGroupMembers = count;
            }
        }

        if ((uint)chunkIndex < (uint)_chunkBusy.Length)
        {
            var now = Stopwatch.GetTimestamp();
            _chunkBusy[chunkIndex] = now - chunkFrom;
            _chunkStart[chunkIndex] = chunkFrom;
            _chunkEnd[chunkIndex] = now;
        }

        arena.Note(probes, hits, _fenceWindow.IsOpen);
        if (cells > 0)
        {
            Interlocked.Add(ref _cellsResolved, cells);
            Interlocked.Add(ref _sessionsShared, shared);
        }
    }

    /// <summary>Resolves one session by querying around its own viewpoint — the path every session took before the broad phase existed.</summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="chunkIndex">The chunk, for the hit range's worker field.</param>
    /// <param name="i">The session's index in this tick's arrays.</param>
    /// <param name="probes">Directory probes, accumulated.</param>
    /// <returns>Hits recorded for the session.</returns>
    private int ResolveSessionDirect(HitArena arena, int chunkIndex, int i, ref long probes)
    {
        // Set for EVERY session, not only on the cell path: the flag lives on the worker's arena, so a session resolved here would otherwise inherit whatever
        // the previous session on this worker was — and skip runs the frame stage, which asks IsSparse for THIS session, would never replace.
        arena.Sparse = _tickSparse[i];
        arena.SessionSparseSkipped = 0;
        var profile = _profiles[_tickProfiles[i]];
        var archetypes = profile.ArchetypeIndices;
        var runStart = arena.RunCount;
        var leaveStart = arena.LeaveCount;
        var view = BeginView(i);

        if (profile.Kind == ObserverKind.Sphere && !_tickPlaced[i])
        {
            // Placed nowhere, so it sees nothing. Recording an empty window rather than skipping the session keeps the index space of this tick's
            // partition intact, which is what Frames walks by the same index — and the departure sweep still runs, because a session that loses its
            // viewpoint has to be told that everything it held is gone.
            _tickHits[i] = CloseSession(arena, view, chunkIndex, runStart, leaveStart, 0, retainUnchanged: false, ref probes);
            return 0;
        }

        var sessionHits = 0;
        var from = _measurePhases ? Stopwatch.GetTimestamp() : 0L;
        for (var a = 0; a < archetypes.Length; a++)
        {
            sessionHits += profile.Kind == ObserverKind.Sphere
                ? WalkSphere(arena, view, archetypes[a], _tickViewpoints[i], profile.EnterRadius, profile.QueryRadius, ref probes)
                : WalkArchetype(arena, view, archetypes[a], ref probes);
        }

        if (_measurePhases)
        {
            // The direct path queries and filters in one enumerator pass, so its broad and narrow phases cannot be separated without changing what it
            // does. Charged to `narrow` whole, which is where the bulk of it is and which keeps the split honest about what it can and cannot see.
            arena.NotePhases(0L, Stopwatch.GetTimestamp() - from, 0L);
        }

        var runsBefore = arena.RunCount;
        var coherent = _sessionEntered == 0 && _sessionLeft == 0;
        _tickHits[i] = CloseSession(arena, view, chunkIndex, runStart, leaveStart, sessionHits, retainUnchanged: false, ref probes);
        _tickSparseSkipped[i] = arena.SessionSparseSkipped;
        arena.NoteSessionCoherence(coherent && arena.RunCount == runsBefore && view != null);
        _sessionEntered = 0;
        _sessionLeft = 0;
        return sessionHits;
    }

    /// <summary>
    /// Readies a session's interest view for this tick, or returns <see langword="null"/> when the pass runs in Phase 1's shape.
    /// </summary>
    /// <remarks>
    /// The compaction happens HERE and nowhere else. A run carries the entry index the frame stage commits through, so entries may only move while no
    /// index is outstanding, and the top of a session's resolution is the one point in the tick where that holds.
    /// </remarks>
    private SessionInterestView BeginView(int i)
    {
        if (_views == null)
        {
            return null;
        }

        var session = _tickSessions[i];
        var view = _views.ViewOf(session.Slot, session.Generation);
        view?.Compact();
        return view;
    }

    /// <summary>
    /// Closes a session's window: everything its view held and this tick's interest did not reach has left, and its cluster is recorded as departed.
    /// </summary>
    /// <remarks>
    /// <b>This is what replaces the frame stage's known-set sweep</b> ([14 § 5.2](14), the worst-scaling function measured). It walks the session's
    /// CLUSTERS — some tens — rather than its known entities, and it emits a leave only for a slot the session was actually told about.
    /// </remarks>
    private SessionHitRange CloseSession(HitArena arena, SessionInterestView view, int chunkIndex, int runStart, int leaveStart, int hits,
        bool retainUnchanged, ref long probes)
    {
        if (view != null)
        {
            var tick = _tickNumber;
            var entries = view.EntryCount;
            for (var e = 0; e < entries; e++)
            {
                var mask = view.MaskAt(e);
                if (mask == 0 || view.TouchedAt(e) == tick)
                {
                    continue;
                }

                if (retainUnchanged && TryRetainUnchanged(arena, view, e, mask, ref probes))
                {
                    hits += BitOperations.PopCount(mask);
                    continue;
                }

                var archetype = SessionInterestView.ArchetypeOf(view.KeyAt(e));
                var bits = mask;
                while (bits != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var id = view.IdAt(e, slot);
                    if (id != 0)
                    {
                        arena.AddLeave(id, e, slot);
                    }
                }

                arena.AddRun(archetype, (int)(view.KeyAt(e) & 0xFFFFFFFFL), 0, 0, 0, e, InterestRunFlags.Departed);
                arena.NoteRunDeparted();
            }
        }

        return new SessionHitRange(chunkIndex, runStart, arena.RunCount - runStart, hits, leaveStart, arena.LeaveCount - leaveStart);
    }

    /// <summary>
    /// Re-emits a held cluster's run unchanged when its answer provably cannot have changed, skipping the kernel that would have recomputed it.
    /// </summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="view">The session's view.</param>
    /// <param name="e">The held entry.</param>
    /// <param name="mask">Its held mask.</param>
    /// <param name="probes">Directory probes, accumulated.</param>
    /// <returns><see langword="true"/> when the run was re-emitted; <see langword="false"/> when the caller must depart the entry as before.</returns>
    /// <remarks>
    /// <para>
    /// <b>Only ever called for a member whose viewpoint is bit-identical to last tick's.</b> For such a member, a cluster whose contents did not change has
    /// exactly the inputs it had then, so the kernel would return exactly the held mask. What this skips is that recomputation, and nothing else: the run
    /// it emits is the one <see cref="FlushSphereRun"/> produces for an unchanged mask — block marked watched, entry touched, <c>entered</c> zero — so the
    /// frame stage, which reads runs and not how they were made, sees byte-identical input. That is what keeps a session that is behind on acks correct:
    /// its full gather walks these runs exactly as it would have walked the kernel's.
    /// </para>
    /// <para>
    /// <b>Every condition is a reason the inputs might differ, and each one declines rather than guesses.</b> The entry must have been resolved on the
    /// previous tick, or something could have changed in a tick it skipped. It must owe nothing, or the enter backlog would stop draining. The archetype
    /// must have published a change list for this tick, the cluster must be absent from it, and it must not have been RETIRED this tick — retirement
    /// clears the change bits on purpose, so a cluster emptied and retired reads as unchanged and would otherwise be kept forever.
    /// </para>
    /// </remarks>
    private bool TryRetainUnchanged(HitArena arena, SessionInterestView view, int e, ulong mask, ref long probes)
    {
        var tick = _tickNumber;
        if (view.TouchedAt(e) != tick - 1 || view.OwedAt(e) != 0UL)
        {
            return false;
        }

        var key = view.KeyAt(e);
        var archetypeIndex = SessionInterestView.ArchetypeOf(key);
        var chunkId = (int)(key & 0xFFFFFFFFL);
        if (archetypeIndex >= _clusterStates.Length)
        {
            return false;
        }

        var clusterState = _clusterStates[archetypeIndex];
        if (clusterState == null
            || Volatile.Read(ref clusterState.StructureTick) != tick
            || clusterState.StructureCoversAll
            || clusterState.StructureSlotsOf(chunkId) != 0UL
            || clusterState.RetiredOn(chunkId, tick))
        {
            return false;
        }

        nint blockAddress = 0;
        ushort flags = InterestRunFlags.None;
        if (TryGetBlock(archetypeIndex, chunkId, out var block))
        {
            blockAddress = (nint)block;
            MarkWatched(arena, block, mask, (uint)tick);
            if (arena.Sparse)
            {
                // Marked like any other, emitted as nothing: see FlushSphereRun.
                probes++;
                view.TouchAt(e, tick);
                arena.NoteRunCoherence(true);
                arena.NoteRunFlow(true);
                arena.SparseRunsSkipped++;
                arena.SessionSparseSkipped++;
                arena.StationaryRunsRetained++;
                return true;
            }
        }
        else
        {
            flags = InterestRunFlags.NoBlock;
            arena.AddNewBlock(archetypeIndex, chunkId);
        }

        probes++;
        view.TouchAt(e, tick);
        arena.NoteRunCoherence(true);
        arena.NoteRunFlow(true);
        arena.AddRun(archetypeIndex, chunkId, blockAddress, mask, 0UL, e, flags);
        arena.StationaryRunsRetained++;
        return true;
    }

    /// <summary>
    /// Resolves every session in one interest cell from a single query per archetype — the broad phase, then a distance filter per session.
    /// </summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="chunkIndex">The chunk, for the hit ranges' worker field.</param>
    /// <param name="start">First session of the group.</param>
    /// <param name="count">How many sessions the group holds; always at least two.</param>
    /// <param name="probes">Directory probes, accumulated.</param>
    /// <param name="hits">Hits recorded, accumulated.</param>
    /// <remarks>
    /// <para>
    /// <b>One query for the cell, enlarged to cover every viewpoint inside it.</b> A session may stand anywhere in its cell, so the union of the discs it
    /// could have is a disc of the observer radius plus half the cell's diagonal, centred on the cell. At a cell of a third of the radius that is 1.53x the
    /// area of one session's disc — so a cell holding two sessions already costs less than resolving them separately, and one holding ten costs a sixth.
    /// </para>
    /// <para>
    /// <b>The filter is per session and exact.</b> Each session then keeps the candidates within its own radius of its own viewpoint, using the engine's own
    /// narrowphase arithmetic, so the run set it ends with is the one a direct query would have produced. The enlargement is invisible downstream: it buys
    /// the sharing and is removed again before anything is recorded.
    /// </para>
    /// </remarks>
    private void ResolveCellGroup(HitArena arena, int chunkIndex, int start, int count, ref long probes, ref long hits)
    {
        var profile = _profiles[_tickProfiles[start]];
        var archetypes = profile.ArchetypeIndices;
        var radius = profile.QueryRadius;
        var enterRadius = profile.EnterRadius;
        var cell = CellSideFor(radius);

        // The cell's centre, recomputed from the first member's viewpoint. Every member shares the key, so every member shares this.
        // A cell of one is resolved around its OWN viewpoint rather than its cell's centre: nothing else has to fit in the disc, so it needs no enlargement
        // and its inscribed disc is its enter radius itself. That is a direct query, answered from the shared snapshot instead of by opening clusters.
        var single = count == 1;
        var halfDiagonal = single ? 0d : cell * HalfDiagonal;
        var centreX = single ? _tickViewpoints[start].X : (Math.Floor(_tickViewpoints[start].X / cell) + 0.5d) * cell;
        var centreY = single ? _tickViewpoints[start].Y : (Math.Floor(_tickViewpoints[start].Y / cell) + 0.5d) * cell;
        var broadRadius = radius + halfDiagonal;

        // BROAD PHASE first, for EVERY archetype, before any session is filtered.
        //
        // The order matters and is not a preference. A SessionHitRange is a CONTIGUOUS span of the arena's run list, so all of one session's runs must be
        // appended together. Querying one archetype and filtering every session through it, then moving to the next, interleaves the sessions' runs — session
        // zero's second archetype lands after session one's first — and every range then covers its neighbours' runs as well as its own. With a single-
        // archetype profile the interleaving cannot happen, which is why it survived a fixture that had one and showed up as internal errors the moment a
        // real profile with several archetypes ran.
        arena.BeginCell();
        var ranges = arena.CellRanges(archetypes.Length);
        var broadFrom = _measurePhases ? Stopwatch.GetTimestamp() : 0L;

        // ── The inscribed disc: what EVERY member of this cell can see without being asked ────────────────────────────────────────────────────────
        //
        // A member sits somewhere inside the cell, so it is at most a half-diagonal from the centre. For an entity inside the disc of this radius about
        // the centre, the triangle inequality gives dist(entity, member) <= interior + halfDiagonal = enterRadius — for every member, whatever its
        // viewpoint. The cluster AABB contains every entity box it holds and is conservative by construction (ClusterNeedsAabbRecompute recomputes on
        // every signal and falls through to "recompute" when there is none), so a cluster box inside this disc is a cluster every member sees WHOLE.
        //
        // The ENTER radius, not the leave radius, because admitting a slot as near is the stronger claim of the two and enter <= leave; a slot inside the
        // enter radius is inside both, which is what lets the admission carry the same (near, mask) pair FilterCandidatesInto would have computed.
        var interior = enterRadius - halfDiagonal;
        var interiorSq = interior * interior;

        // A profile whose enter radius is smaller than the cell's half-diagonal has no inscribed disc at all, so nothing can be admitted whole and every
        // cluster takes the per-entity path. Geometry, not a policy: the cell side is derived from the radius, so this is only reachable for a degenerate
        // profile.
        var admitInterior = interior > 0d;
        var interiorRanges = arena.CellInteriorRanges(archetypes.Length);
        Span<ClusterSpatialQueryResult> drain = stackalloc ClusterSpatialQueryResult[64];

        for (var a = 0; a < archetypes.Length; a++)
        {
            ranges[a] = arena.CandidateCount;
            interiorRanges[a] = arena.InteriorCount;
            var clusterState = _clusterStates[archetypes[a]];
            if (clusterState == null || clusterState.Grid == null)
            {
                continue;
            }

            // Whether this archetype published its STRUCTURE changes for this tick — the writes that can change who sees an entity: positions, spawns,
            // releases. Content changes (health, mode) are deliberately not in it; they change what an entity looks like, never whether it is visible.
            // Without the signal every cluster reads as changed and a stationary member takes the ordinary path, so a missing signal can only switch
            // retention OFF, never wrongly on.
            var signal = Volatile.Read(ref clusterState.StructureTick) == _tickNumber && !clusterState.StructureCoversAll;

            using var e = clusterState.QueryRadius(clusterState.Grid, centreX, centreY, _tickViewpoints[start].Z, broadRadius);

            // ── OPEN ONCE, SHARE ACROSS CELLS ─────────────────────────────────────────────────────────────────────────────────────────────────────
            //
            // Profiled at d06 with a thousand sessions, opening clusters was about half of this stage: each cell's query opens every cluster its disc
            // reaches, and about 165 overlapping cells reach each one some fifteen times a tick. The broadphase here only FINDS clusters — from the spatial
            // index and the cluster AABB table, with no page read — and the occupancy and entity bounds come from a store filled by whichever worker
            // reached the cluster first this tick. The same filters the enumerator's drain applies are applied below, so the candidates are identical.
            var snap = _snapshots[archetypes[a]];
            if (snap != null && e.IsAabb2F)
            {
                var fieldsOffset = e.SpatialFieldsOffset;
                var stride = e.SpatialStride;
                var qMinX = centreX - broadRadius;
                var qMaxX = centreX + broadRadius;
                var qMinY = centreY - broadRadius;
                var qMaxY = centreY + broadRadius;
                var broadSq = broadRadius * broadRadius;

                while (e.MoveNextClusterUnopened(out var chunkId, out var bMinX, out var bMinY, out var bMaxX, out var bMaxY))
                {
                    arena.NoteBroadCluster();
                    var occ = snap.TryGet(chunkId, _tickNumber, out var mustFill);
                    byte* privateBase = null;
                    if (mustFill)
                    {
                        byte* basePtr;
                        nint fillBlock;
                        try
                        {
                            basePtr = e.OpenCluster(chunkId);
                            fillBlock = _states[archetypes[a]].Directory.TryGetBlock(chunkId, out var found) ? (nint)found : 0;
                        }
                        catch
                        {
                            // The claim is this worker's; released so the cluster's other readers re-claim rather than spin on a fill that never comes.
                            snap.Abandon(chunkId, _tickNumber);
                            throw;
                        }

                        occ = snap.Fill(chunkId, _tickNumber, basePtr, fieldsOffset, stride, fillBlock);
                        arena.SnapshotOpens++;
                        if (!snap.Covers(chunkId))
                        {
                            privateBase = basePtr;
                        }
                    }
                    else
                    {
                        arena.SnapshotReads++;
                    }

                    if (occ == 0UL)
                    {
                        continue;
                    }

                    e.TallyOccupancy(occ);
                    var sSlots = signal ? clusterState.StructureSlotsOf(chunkId) : ulong.MaxValue;
                    var sChanged = sSlots != 0UL;
                    if (_measurePhases)
                    {
                        arena.BroadSlotsReached += BitOperations.PopCount(occ);
                        arena.BroadSlotsChanged += BitOperations.PopCount(sSlots & occ);
                    }
                    if (sChanged)
                    {
                        arena.BroadClustersStructureChanged++;
                    }

                    var sdx = Math.Max(centreX - bMinX, bMaxX - centreX);
                    var sdy = Math.Max(centreY - bMinY, bMaxY - centreY);
                    if (admitInterior && sdx <= interior && sdy <= interior && (sdx * sdx) + (sdy * sdy) <= interiorSq)
                    {
                        arena.AddInteriorCluster(chunkId, occ, sChanged);
                        continue;
                    }

                    var sFrom = arena.CandidateCount;
                    var sBits = occ;
                    while (sBits != 0UL)
                    {
                        var slot = BitOperations.TrailingZeroCount(sBits);
                        sBits &= sBits - 1;

                        AABB2F box;
                        if (privateBase != null)
                        {
                            box = *(AABB2F*)(privateBase + fieldsOffset + (slot * stride));
                        }
                        else
                        {
                            box = snap.Box(chunkId, slot);
                        }

                        double eMinX = box.MinX, eMinY = box.MinY, eMaxX = box.MaxX, eMaxY = box.MaxY;

                        // The enumerator's drain, restated: degenerate bounds skipped, the query box, then the closest-point distance to the cell centre.
                        if (!(eMinX <= eMaxX) || !(eMinY <= eMaxY))
                        {
                            continue;
                        }

                        if (eMaxX < qMinX || eMinX > qMaxX || eMaxY < qMinY || eMinY > qMaxY)
                        {
                            continue;
                        }

                        var ndx = Math.Max(Math.Max(eMinX - centreX, 0d), centreX - eMaxX);
                        var ndy = Math.Max(Math.Max(eMinY - centreY, 0d), centreY - eMaxY);
                        if ((ndx * ndx) + (ndy * ndy) > broadSq)
                        {
                            continue;
                        }

                        arena.NoteBroadEntity();
                        arena.AddCandidate(chunkId, slot, eMinX, eMinY, eMaxX, eMaxY);
                    }

                    if (sChanged)
                    {
                        arena.NoteChangedRange(a, sFrom, arena.CandidateCount);
                    }
                }

                continue;
            }

            while (e.MoveNextCluster(out var c))
            {
                arena.NoteBroadCluster();
                var changed = !signal || clusterState.StructureSlotsOf(c.ChunkId) != 0UL;
                if (changed)
                {
                    arena.BroadClustersStructureChanged++;
                }

                // Farthest corner of the box from the centre: inside the disc iff the whole box is.
                var dx = Math.Max(centreX - c.MinX, c.MaxX - centreX);
                var dy = Math.Max(centreY - c.MinY, c.MaxY - centreY);
                if (admitInterior && dx <= interior && dy <= interior && (dx * dx) + (dy * dy) <= interiorSq)
                {
                    arena.AddInteriorCluster(c.ChunkId, c.Slots, changed);
                    continue;
                }

                var clusterFrom = arena.CandidateCount;
                int n;
                while ((n = e.FillCurrentCluster(drain)) > 0)
                {
                    for (var i = 0; i < n; i++)
                    {
                        ref readonly var hit = ref drain[i];
                        arena.NoteBroadEntity();
                        arena.AddCandidate(hit.ClusterChunkId, hit.SlotIndex, hit.MinX, hit.MinY, hit.MaxX, hit.MaxY);
                    }
                }

                if (changed)
                {
                    arena.NoteChangedRange(a, clusterFrom, arena.CandidateCount);
                }
            }
        }

        // After every archetype's full range, so none of them is broken: the changed clusters' candidates again, grouped by archetype, for the members
        // that only need to look at what moved.
        // Only a stationary member reads the changed region, and building it copies the changed clusters' candidates a second time — in a moving world,
        // nearly all of them. Built when some member may take that path, which is decided from the same inputs the member loop uses.
        var anyStationary = false;
        for (var i = start; i < start + count && !anyStationary; i++)
        {
            anyStationary = MayBeStationary(i);
        }

        var changedRegion = anyStationary ? arena.BuildChangedRegion(archetypes.Length) : null;
        var fullEnd = anyStationary ? changedRegion[0] : arena.CandidateCount;

        arena.NoteCellCollected();
        var broadTicks = _measurePhases ? Stopwatch.GetTimestamp() - broadFrom : 0L;
        var narrowTicks = 0L;
        var flushTicks = 0L;

        // NARROW PHASE, one session at a time, so each session's runs are contiguous.
        for (var i = start; i < start + count; i++)
        {
            var viewpoint = _tickViewpoints[i];
            var runStart = arena.RunCount;
            var leaveStart = arena.LeaveCount;
            var view = BeginView(i);
            var sessionHits = 0;

            // ── A member that did not move ────────────────────────────────────────────────────────────────────────────────────────────────────────
            //
            // Its viewpoint is bit-identical to last tick's, so for every cluster whose contents also did not change, the kernel would compute exactly the
            // mask it computed then — the inputs are the same bytes. Such a member runs the kernel over the CHANGED region only, and CloseSession re-emits
            // everything else it held as the same run it would have produced. Displacement is infinity when there is no previous viewpoint, so a session
            // seen for the first time can never take this path.
            // Only when the view holds what the last published frame described and owes nothing: then the held mask IS the kernel's last answer. A view
            // behind its frames (a skipped publish), with a pending cluster (no block yet, counted as owed) or with no view at all is recomputed in full.
            var stationary = anyStationary && MayBeStationary(i) && view != null && view.OwedCount == 0;
            arena.Sparse = _tickSparse[i];
            arena.SessionSparseSkipped = 0;

            // A MOVING member's runs are held back, not flushed: most of them name a cluster whose membership did not change, and finding that out
            // cluster by cluster costs a probe into the member's view each. One sequential pass over the view afterwards retains those in place, and
            // only the rest are flushed. A stationary member already skips its unchanged clusters by a stronger rule; see CloseSession.
            var defer = !stationary && view != null;
            if (defer)
            {
                arena.BeginDeferral();
            }

            for (var a = 0; a < archetypes.Length; a++)
            {
                var to = a + 1 < archetypes.Length ? ranges[a + 1] : fullEnd;
                var cFrom = stationary ? changedRegion[a] : 0;
                var cTo = stationary ? (a + 1 < archetypes.Length ? changedRegion[a + 1] : arena.CandidateCount) : 0;

                // Admitted whole by the box test: no distance is computed, and the mask IS the occupancy. One flush per cluster per member against the
                // sixty-four tests per cluster per member it replaces.
                var iTo = a + 1 < archetypes.Length ? interiorRanges[a + 1] : arena.InteriorCount;
                for (var k = interiorRanges[a]; k < iTo; k++)
                {
                    if (stationary && !arena.InteriorChanged(k))
                    {
                        continue;
                    }

                    var slots = arena.InteriorSlots(k);
                    if (_measurePhases && !stationary)
                    {
                        arena.ViewClustersAdmittedWhole++;
                    }

                    if (!defer || !arena.TryDefer(archetypes[a], arena.InteriorChunk(k), slots, slots))
                    {
                        sessionHits += FlushSphereRun(arena, view, archetypes[a], arena.InteriorChunk(k), slots, slots, ref probes);
                    }
                }

                var kFrom = stationary ? cFrom : ranges[a];
                var kTo = stationary ? cTo : to;
                if (kFrom == kTo)
                {
                    continue;
                }

                var narrowFrom = _measurePhases ? Stopwatch.GetTimestamp() : 0L;
                arena.BeginSphere();
                arena.FilterCandidatesInto(viewpoint.X, viewpoint.Y, enterRadius, radius, kFrom, kTo);
                if (_measurePhases)
                {
                    narrowTicks += Stopwatch.GetTimestamp() - narrowFrom;
                    narrowFrom = Stopwatch.GetTimestamp();
                }

                if (_measurePhases && !stationary)
                {
                    NoteViewShape(arena, archetypes[a]);
                }

                for (var r = 0; r < arena.SphereCount; r++)
                {
                    if (!defer || !arena.TryDefer(archetypes[a], arena.SphereChunk(r), arena.SphereNear(r), arena.SphereMask(r)))
                    {
                        sessionHits += FlushSphereRun(arena, view, archetypes[a], arena.SphereChunk(r), arena.SphereNear(r), arena.SphereMask(r), ref probes);
                    }
                }

                if (_measurePhases)
                {
                    flushTicks += Stopwatch.GetTimestamp() - narrowFrom;
                }
            }

            if (defer)
            {
                sessionHits += RetainDeferred(arena, view, ref probes);
            }

            // A session is coherent only if NOTHING changed for it: no slot entered, none left, and no cluster departed. CloseSession appends the
            // departures, so the run count it added is the last term and has to be read after it.
            var runsBefore = arena.RunCount;
            var coherent = _sessionEntered == 0 && _sessionLeft == 0;
            _tickHits[i] = CloseSession(arena, view, chunkIndex, runStart, leaveStart, sessionHits, retainUnchanged: stationary, ref probes);
            _tickSparseSkipped[i] = arena.SessionSparseSkipped;
            arena.NoteSessionCoherence(coherent && arena.RunCount == runsBefore && view != null);
            _sessionEntered = 0;
            _sessionLeft = 0;
            hits += sessionHits;
        }

        if (_measurePhases)
        {
            arena.NotePhases(broadTicks, narrowTicks, flushTicks);
        }
    }

    /// <summary>
    /// Settles a moving member's deferred runs: one pass over its view retains every run that names an entry whose membership did not change, then the
    /// rest are flushed in the order they were produced.
    /// </summary>
    /// <returns>Hits recorded.</returns>
    /// <remarks>
    /// <para>
    /// <b>The same decision <see cref="FlushSphereRun"/> makes, reached from the other side.</b> A run is retained exactly when FlushSphereRun would have
    /// found an entry, computed the same mask as the entry holds, and seen nothing owed — the mask is computed the same way, hysteresis included. What
    /// changes is how the entry is found: by walking the view, which is sequential and which the departure sweep walks anyway, instead of one hash probe
    /// per cluster. A retained run is marked watched and, for a session that is not sparse, emitted with nothing entered, as FlushSphereRun would.
    /// </para>
    /// <para>
    /// Anything the pass cannot settle — no entry, a changed mask, a debt, no block — is left deferred and flushed normally, so every run is decided by
    /// the one path that already handles it.
    /// </para>
    /// </remarks>
    private int RetainDeferred(HitArena arena, SessionInterestView view, ref long probes)
    {
        var hits = 0;
        var tick = _tickNumber;
        var stamp = (uint)tick;
        var entries = view.EntryCount;
        for (var e = 0; e < entries; e++)
        {
            var held = view.MaskAt(e);
            if (held == 0UL || view.TouchedAt(e) == tick)
            {
                continue;
            }

            var key = view.KeyAt(e);
            var archetype = SessionInterestView.ArchetypeOf(key);
            var chunkId = (int)(key & 0xFFFFFFFFL);
            if (!arena.TryDeferred(archetype, chunkId, out var nearMask, out var farMask))
            {
                continue;
            }

            var mask = nearMask == farMask ? farMask : nearMask | (farMask & held);
            if (mask != held || view.OwedAt(e) != 0UL || !TryGetBlock(archetype, chunkId, out var block))
            {
                continue;
            }

            MarkWatched(arena, block, mask, stamp);
            probes++;
            view.TouchAt(e, tick);
            arena.NoteRunCoherence(true);
            arena.NoteRunFlow(true);
            if (arena.Sparse)
            {
                arena.SparseRunsSkipped++;
                arena.SessionSparseSkipped++;
            }
            else
            {
                arena.AddRun(archetype, chunkId, (nint)block, mask, 0UL, e, InterestRunFlags.None);
            }

            arena.ConsumeDeferred(archetype, chunkId);
            arena.RunsRetainedByView++;
            hits += BitOperations.PopCount(mask);
        }

        for (var d = 0; d < arena.DeferredCount; d++)
        {
            arena.Deferred(d, out var archetype, out var chunkId, out var nearMask, out var farMask, out var live);
            if (live)
            {
                hits += FlushSphereRun(arena, view, archetype, chunkId, nearMask, farMask, ref probes);
            }
        }

        return hits;
    }

    /// <summary>Whether session <paramref name="i"/> may take the stationary path: it did not move, and its view holds its last published frame.</summary>
    private bool MayBeStationary(int i) => _tickDisplacement[i] == 0d && _tickReady[i];

    /// <summary>
    /// Counts, for one member's kernel output, the clusters wholly inside its view and those only partly inside. The occupancy is the snapshot's, which is
    /// current for every cluster the member reached: filled this tick when it changed, unchanged since its last fill otherwise.
    /// </summary>
    private void NoteViewShape(HitArena arena, int archetypeIndex)
    {
        var snap = _snapshots[archetypeIndex];
        if (snap == null)
        {
            return;
        }

        for (var r = 0; r < arena.SphereCount; r++)
        {
            var occ = snap.OccupancyOf(arena.SphereChunk(r));
            var mask = arena.SphereMask(r) & occ;
            if (mask == occ)
            {
                arena.ViewClustersTestedWhole++;
            }
            else
            {
                arena.ViewClustersPartial++;
                arena.ViewPartialInside += BitOperations.PopCount(mask);
                arena.ViewPartialTotal += BitOperations.PopCount(occ);
            }
        }
    }

    /// <summary>Samples the replicated archetypes' clusters: their radius (box half-diagonal) against the grid cell and the interest cell. Serial.</summary>
    private void SampleClusterSizes()
    {
        for (var a = 0; a < _clusterStates.Length; a++)
        {
            var cs = _clusterStates[a];
            var aabbs = cs == null ? null : Volatile.Read(ref cs.ClusterAabbs);
            var cellMap = cs == null ? null : Volatile.Read(ref cs.ClusterCellMap);
            if (aabbs == null || cellMap == null || cs.Grid == null)
            {
                continue;
            }

            _gridCellSide = cs.Grid.Config.CellSize;
            var n = Math.Min(aabbs.Length, cellMap.Length);
            for (var chunk = 0; chunk < n; chunk++)
            {
                if (cellMap[chunk] < 0)
                {
                    continue;
                }

                ref readonly var box = ref aabbs[chunk];
                if (!(box.MinX <= box.MaxX) || !(box.MinY <= box.MaxY))
                {
                    continue;
                }

                var hw = ((double)box.MaxX - box.MinX) * 0.5d;
                var hh = ((double)box.MaxY - box.MinY) * 0.5d;
                var radius = Math.Sqrt((hw * hw) + (hh * hh));
                _clusterRadiusSum += radius;
                _clusterRadiusMax = Math.Max(_clusterRadiusMax, radius);
                _clusterSizeSamples++;
            }
        }

        for (var p = 0; p < _profiles.Length; p++)
        {
            if (_profiles[p].Kind == ObserverKind.Sphere && _profiles[p].QueryRadius > 0d)
            {
                _interestCellSide = CellSideFor(_profiles[p].QueryRadius);
                break;
            }
        }
    }

    /// <summary>Mean and largest cluster radius (box half-diagonal), the grid cell that hosts clusters, and the interest cell, in world units.</summary>
    public (double MeanRadius, double MaxRadius, double GridCellSide, double InterestCellSide, long Samples) ClusterSize =>
        (_clusterSizeSamples == 0 ? 0d : _clusterRadiusSum / _clusterSizeSamples, _clusterRadiusMax, _gridCellSide, _interestCellSide, _clusterSizeSamples);

    /// <summary>What moving members' views were made of: clusters wholly inside (admitted without a test, or tested), partly inside, and the partial ones' entities.</summary>
    public (long AdmittedWhole, long TestedWhole, long Partial, long PartialInside, long PartialTotal) ViewShape
    {
        get
        {
            long aw = 0, tw = 0, p = 0, pi = 0, pt = 0;
            for (var i = 0; i < _arenas.Length; i++)
            {
                var ar = _arenas[i];
                aw += ar.ViewClustersAdmittedWhole;
                tw += ar.ViewClustersTestedWhole;
                p += ar.ViewClustersPartial;
                pi += ar.ViewPartialInside;
                pt += ar.ViewPartialTotal;
            }

            return (aw, tw, p, pi, pt);
        }
    }

    /// <summary>A cluster's replication block: from this tick's snapshot when the cluster was filled into it, from the directory otherwise.</summary>
    /// <param name="archetypeIndex">The archetype.</param>
    /// <param name="chunkId">The cluster.</param>
    /// <param name="block">The block.</param>
    /// <returns><see langword="true"/> when the cluster has a block.</returns>
    private bool TryGetBlock(int archetypeIndex, int chunkId, out ReplicationBlockHeader* block)
    {
        var snapshot = (uint)archetypeIndex < (uint)_snapshots.Length ? _snapshots[archetypeIndex] : null;
        if (snapshot != null && snapshot.TryBlockOf(chunkId, _tickNumber, out var cached))
        {
            block = (ReplicationBlockHeader*)cached;
            return cached != 0;
        }

        return _states[archetypeIndex].Directory.TryGetBlock(chunkId, out block);
    }

    /// <summary>
    /// Resolves one archetype's interest for a session whose observer is a sphere, through the engine's own spatial index.
    /// </summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="archetypeIndex">The archetype's plan index.</param>
    /// <param name="centre">The sphere's centre.</param>
    /// <param name="enterRadius">The radius something the session does not already hold must be inside, in world units.</param>
    /// <param name="radius">The radius it may stay inside once held; the query runs at this one.</param>
    /// <param name="probes">Directory probes, accumulated.</param>
    /// <returns>Hits recorded.</returns>
    /// <remarks>
    /// <para>
    /// <b>The narrowphase is the engine's, not a second one written here.</b> <c>ArchetypeClusterState.QueryRadius</c> already drives the per-cell cluster
    /// index with the sphere's enclosing box and applies the distance test per entity, and it reports the cluster and the slot of every hit — which is
    /// exactly the coordinate this pass records interest in. Writing a sphere test over the active-cluster list instead would be a second implementation of
    /// the query the engine exists to provide, and it would be the slow one: it would visit every cluster of the archetype, which is the cost this observer
    /// is here to remove.
    /// </para>
    /// <para>
    /// <b>One run per cluster, whatever order the hits arrive in.</b> The unit of interest is a run — a cluster plus the mask of slots inside it — and the
    /// walk accumulates every hit into the run for its cluster before any of them is recorded, rather than flushing when the cluster id changes. Flushing on
    /// change would be correct only if the enumerator never revisited a cluster, which is a property of the spatial query rather than of this code, and it
    /// would fail in a way nothing reports: a slot appearing in two runs is counted twice by the frame assembler's hit total, which is what the changed-only
    /// gather proves "no leaves are outstanding" from — an over-count there makes the proof succeed when it should not and a leave is silently never sent.
    /// The cluster count inside a sphere is small, so the linear scan that merges a hit into its run is cheaper than the stamp table that would replace it.
    /// </para>
    /// </remarks>
    /// <param name="view">The session's interest membership, or <see langword="null"/> when the pass is not incremental.</param>
    private int WalkSphere(HitArena arena, SessionInterestView view, int archetypeIndex, Vector3D centre, double enterRadius, double radius,
        ref long probes)
    {
        var clusterState = _clusterStates[archetypeIndex];
        if (clusterState == null || clusterState.Grid == null)
        {
            return 0;
        }

        arena.BeginSphere();
        // The band, from the bounds the query already hands back rather than from a second query at the enter radius. Same closest-point arithmetic as
        // the cell-keyed narrow phase, and it costs four loads the caller would otherwise have taken from the component table.
        var enterSq = enterRadius * enterRadius;
        foreach (var hit in clusterState.QueryRadius(clusterState.Grid, centre.X, centre.Y, centre.Z, radius))
        {
            var hx = double.MaxNative(0d, double.MaxNative(hit.MinX - centre.X, centre.X - hit.MaxX));
            var hy = double.MaxNative(0d, double.MaxNative(hit.MinY - centre.Y, centre.Y - hit.MaxY));
            arena.AddSphereHit(hit.ClusterChunkId, hit.SlotIndex, ((hx * hx) + (hy * hy)) <= enterSq);
        }

        var hits = 0;
        for (var i = 0; i < arena.SphereCount; i++)
        {
            hits += FlushSphereRun(arena, view, archetypeIndex, arena.SphereChunk(i), arena.SphereNear(i), arena.SphereMask(i), ref probes);
        }

        return hits;
    }

    private int FlushSphereRun(HitArena arena, SessionInterestView view, int archetypeIndex, int chunkId, ulong nearMask, ulong farMask, ref long probes)
    {
        if (chunkId < 0 || farMask == 0)
        {
            return 0;
        }

        // ── The hysteresis band ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // The query ran at the LEAVE radius, so `farMask` names everything the session may keep and `nearMask` the subset it may newly take. A slot the
        // session already holds stays while it is anywhere inside the band; one it does not hold is admitted only inside the enter radius. Without that,
        // an entity hovering on the boundary enters and leaves on alternate ticks and every re-entry costs a full ENTER record rather than a delta.
        //
        // Asked WITHOUT touching the view, because touching creates the entry and stamps it as reached — which is the claim this is still deciding
        // whether to make. A cluster that reaches only the band and holds nothing of this session's is not reached at all, and stamping one would tell
        // CloseSession the session still holds a cluster it does not.
        //
        // With no declared band the two masks are equal and this is the identity, which is what makes the two shapes one binary apart.
        // ── ONE keyed lookup for the whole run ────────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // This method used to ask the view three separate keyed questions about one cluster — HeldMask for the hysteresis blend, then the block cache,
        // then Touch to stamp it — each probing the same map for the same key, plus the replication directory for a fourth. At roughly 192 000 runs a
        // tick that is four hash lookups where one index answers everything, and 92.9 % of those runs are unchanged from the tick before (18 § 8.3).
        //
        // Found BEFORE the admission decision, because the hysteresis blend needs the held mask and must not create an entry: touching one would stamp
        // the cluster as reached, which is the claim still being decided.
        var viewKey = SessionInterestView.KeyOf((ushort)archetypeIndex, chunkId);
        var entry = view?.IndexOf(viewKey) ?? -1;
        var held = entry >= 0 ? view.MaskAt(entry) : 0UL;

        var mask = farMask;
        if (nearMask != farMask)
        {
            mask = nearMask | (farMask & held);
            if (mask == 0)
            {
                return 0;
            }
        }

        // A sparse session whose membership here did not move emits NOTHING for this cluster: the block is still marked watched, so it is projected as
        // before, and a content change reaches the session through projection's changed-block table. The entry is only touched, so the departure sweep
        // keeps it. An entry that
        // owes slots is not skipped — its run is what serves the enter backlog a slice at a time.
        if (arena.Sparse && entry >= 0 && mask == held && view.OwedAt(entry) == 0UL
            && TryGetBlock(archetypeIndex, chunkId, out var heldBlock))
        {
            // Still marked: projection must see exactly the slots the full walk would have marked, or a slot's continuity — and with it the segment a
            // later enter carries — would depend on which path a session took. Only the run and the frame stage's walk over it are saved.
            MarkWatched(arena, heldBlock, mask, (uint)_tickNumber);
            probes++;
            view.TouchAt(entry, _tickNumber);
            arena.NoteRunCoherence(true);
            arena.NoteRunFlow(true);
            arena.SparseRunsSkipped++;
            arena.SessionSparseSkipped++;
            return BitOperations.PopCount(mask);
        }

        var directory = _states[archetypeIndex].Directory;
        var stamp = (uint)_tickNumber;

        // ── The session's own answer from last time, before the directory's ───────────────────────────────────────────────────────────────────────
        //
        // Which block a cluster has is a property of the CLUSTER, and this pass asks the directory once per run per SESSION — some 192 000 hash probes
        // a tick at d06/400, for an answer that changes for almost none of them: 92.9 % of runs are unchanged tick to tick (18 § 8.3). The view is
        // already keyed by cluster and about to be touched anyway, so it is where the answer belongs.
        //
        // Caching the BLOCK in the entry as well was tried and removed: validated against the header's chunk id it was correct, and it bought nothing
        // measurable — four samples put interest at 4.11 ms against 4.16 — while costing eight bytes per entry per session. The directory probe is not
        // where this pass spends its time either.
        nint blockAddress = 0;
        ushort flags = InterestRunFlags.None;
        if (TryGetBlock(archetypeIndex, chunkId, out var block))
        {
            blockAddress = (nint)block;
            MarkWatched(arena, block, mask, stamp);
        }
        else
        {
            flags = InterestRunFlags.NoBlock;
            arena.AddNewBlock(archetypeIndex, chunkId);
        }

        probes++;

        // ── The difference (15 § 3.2) ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        //
        // One 64-bit comparison against what this session's last published frame described, in place of a known-set probe per entity. `entered` is the half
        // of the question a block cannot answer, and the reason C-1 was unsound; `left` is what replaces the frame stage's walk of the whole known-set.
        // The new mask is NOT committed here: a frame that is skipped must leave the session's baseline where it was, so the frame stage commits it and
        // only on a publish.
        var entered = mask;
        var viewIndex = -1;
        if (view != null)
        {
            // The entry is created only here, once the cluster is admitted — and only when the lookup above did not already find it.
            if (entry >= 0)
            {
                viewIndex = entry;
                view.TouchAt(entry, _tickNumber);
            }
            else
            {
                viewIndex = view.Touch(viewKey, _tickNumber);
                held = view.MaskAt(viewIndex);
            }

            entered = mask & ~held;

            var left = held & ~mask;
            arena.NoteRunCoherence(entered == 0 && left == 0);

            // Held is the MASK, so a cluster the session reaches for the first time and one it has held all along are told apart by whether that mask is
            // empty — a cluster in the view with no slots is one every occupant of which has already left, which is the departed case and not this one.
            arena.NoteRunFlow(held != 0);
            _sessionEntered |= entered;
            _sessionLeft |= left;
            while (left != 0)
            {
                var slot = BitOperations.TrailingZeroCount(left);
                left &= left - 1;
                var id = view.IdAt(viewIndex, slot);
                if (id != 0)
                {
                    arena.AddLeave(id, viewIndex, slot);
                }
            }
        }

        arena.AddRun(archetypeIndex, chunkId, blockAddress, mask, entered, viewIndex, flags);
        return BitOperations.PopCount(mask);
    }

    /// <summary>
    /// Walks one archetype's active clusters for one session, recording a run per non-empty cluster and marking its entities watched.
    /// </summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="archetypeIndex">The archetype's plan index.</param>
    /// <param name="probes">Directory probes, accumulated.</param>
    /// <returns>Hits recorded.</returns>
    /// <param name="view">The session's interest membership, or <see langword="null"/> when the pass is not incremental.</param>
    private int WalkArchetype(HitArena arena, SessionInterestView view, int archetypeIndex, ref long probes)
    {
        var clusterState = _clusterStates[archetypeIndex];
        var segment = clusterState?.ClusterSegment;
        if (segment == null)
        {
            return 0;
        }

        // CLUSTERWALK-02: the count is read before the array and the pair is clamped, through the one reader that exists for it. CLUSTERWALK-01's racing
        // walker does not apply here — the track runs after Engine-Post has completed (PH-01), inside the EW-01 window, so no thread is adding to or removing
        // from this list while the chunk runs.
        var ids = clusterState.ReadActiveClusterList(out var clusterCount);
        if (ids == null || clusterCount == 0)
        {
            return 0;
        }

        var directory = _states[archetypeIndex].Directory;
        var fullMask = _plans[archetypeIndex].ClusterLayout.FullMask;
        var stamp = (uint)_tickNumber;
        var hits = 0;

        // One accessor for the whole walk rather than one per cluster: its page cache is what turns 570 cluster reads into a few page resolutions. It is
        // rebuilt per session, which is the cost Q-M2 of the build plan asks to measure once the pipeline is complete.
        using var accessor = segment.CreateChunkAccessor();

        for (var i = 0; i < clusterCount; i++)
        {
            var chunkId = ids[i];
            if (chunkId < 0)
            {
                continue;
            }

            var occupancy = *(ulong*)accessor.GetChunkAddress(chunkId) & fullMask;
            if (occupancy == 0)
            {
                // On the active list with nothing in it: a cluster that drained this tick and has not been finalized yet. It is not a hit and it must not
                // ask for a block.
                continue;
            }

            probes++;
            nint blockAddress = 0;
            ushort flags = InterestRunFlags.None;

            if (directory.TryGetBlock(chunkId, out var block))
            {
                blockAddress = (nint)block;
                MarkWatched(arena, block, occupancy, stamp);
            }
            else
            {
                flags = InterestRunFlags.NoBlock;
                arena.AddNewBlock(archetypeIndex, chunkId);
            }

            // The same difference the sphere path takes; see FlushSphereRun for why the new mask is not committed here.
            var entered = occupancy;
            var viewIndex = -1;
            if (view != null)
            {
                viewIndex = view.Touch(SessionInterestView.KeyOf((ushort)archetypeIndex, chunkId), _tickNumber);
                var held = view.MaskAt(viewIndex);
                entered = occupancy & ~held;

                var left = held & ~occupancy;
                while (left != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(left);
                    left &= left - 1;
                    var id = view.IdAt(viewIndex, slot);
                    if (id != 0)
                    {
                        arena.AddLeave(id, viewIndex, slot);
                    }
                }
            }

            arena.AddRun(archetypeIndex, chunkId, blockAddress, occupancy, entered, viewIndex, flags);
            hits += BitOperations.PopCount(occupancy);
        }

        return hits;
    }

    /// <summary>Merges a run's slots into its block's watched mask, claiming the block for this tick if nobody had.</summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="block">The cluster's block.</param>
    /// <param name="slots">The run's slots, never zero.</param>
    /// <param name="stamp">This tick, as the block's eviction clock stores it.</param>
    /// <remarks>
    /// <b>The contract this rests on, for whoever writes the blocks step: nothing but this method ever SETS a watched bit.</b> The claim is the previous value
    /// of the atomic, so a block whose mask was filled in by anything else is claimed by nobody — it never reaches a worker's watched list, and therefore
    /// never has its mask cleared again and never appears in S1's partition. A block is created with a zero mask (<see cref="ReplicationBlockPool.TryRent"/>
    /// already leaves it that way) and is marked by the following tick's interest pass, which is the one-tick lag the blocks step's position in the DAG
    /// already implies. <c>WorldObserverTests.AnEntityThatLeavesTheWorldIsUnmarked</c> is what fails if that stops being true.
    /// </remarks>
    private static void MarkWatched(HitArena arena, ReplicationBlockHeader* block, ulong slots, uint stamp)
    {
        // The overwhelmingly common case at 110 world sessions: the first session to reach this cluster already set every bit, and the other 109 workers do
        // one shared read of a line nobody is writing.
        if ((Volatile.Read(ref block->WatchedMask) & slots) == slots)
        {
            return;
        }

        if (Interlocked.Or(ref block->WatchedMask, slots) != 0)
        {
            return;
        }

        // Exactly one caller sees a zero previous value, because `slots` is never zero. That caller owns the block for this tick: it stamps the eviction
        // clock (single writer, so a plain store) and puts the block on its own list for the next tick's prologue to clear.
        block->LastWatchedTick = stamp;
        arena.AddWatchedBlock((nint)block);
    }

    /// <summary>The index of the profile a session is bound to, or <c>-1</c> when it has none or it reaches nothing.</summary>
    /// <param name="session">The identity.</param>
    /// <returns>The profile index.</returns>
    private int ProfileIndexOf(SessionId session)
    {
        var name = _sessions.ProfileName(session);
        if (name == null || !_profileByName.TryGetValue(name, out var index))
        {
            return -1;
        }

        return _profiles[index].ArchetypeIndices.Length == 0 ? -1 : index;
    }

    private void EnsureArenas(int workers)
    {
        if (_arenas.Length >= workers)
        {
            return;
        }

        var grown = new HitArena[workers];
        Array.Copy(_arenas, grown, _arenas.Length);
        for (var i = _arenas.Length; i < workers; i++)
        {
            grown[i] = new HitArena();
        }

        _arenas = grown;
    }

    /// <summary>
    /// Turns each declared profile into the deduplicated set of plan indices its observers reach.
    /// </summary>
    /// <param name="registry">The declarations.</param>
    /// <param name="plans">The compiled plans.</param>
    /// <returns>One compiled profile per declaration, in declaration order.</returns>
    /// <remarks>
    /// Resolved at <c>Start</c> so the tick path never looks an archetype up by <see cref="Type"/> and never walks a declaration. A profile naming an
    /// archetype with no projection is refused here rather than skipped: an observer pointed at an archetype clients can never be sent is a declaration that
    /// would do nothing at all, and doing nothing quietly is how a system comes to be believed to run.
    /// </remarks>
    private static CompiledProfile[] CompileProfiles(SubscriptionsRegistry registry, CompiledProjectionPlan[] plans)
    {
        var profiles = new CompiledProfile[registry.Profiles.Count];
        var indices = new List<int>();

        for (var p = 0; p < registry.Profiles.Count; p++)
        {
            var declaration = registry.Profiles[p];
            indices.Clear();
            var kind = ObserverKind.World;
            var queryRadius = 0d;
            var enterRadius = 0d;
            var first = true;

            foreach (var observer in declaration.Observers)
            {
                if (observer.Kind is not (ObserverKind.World or ObserverKind.Sphere))
                {
                    // Unreachable through TyphonRuntime, which freezes the registry and refuses these kinds with the phase that builds them. A pass built
                    // directly against an unfrozen registry would otherwise treat a ClientRegion as a World, which is a wrong answer rather than a missing one.
                    throw new NotSupportedException(
                        $"Profile '{declaration.Name}' declares a {observer.Kind} observer, which a later phase builds. This one resolves World and Sphere.");
                }

                if (!first && observer.Kind != kind)
                {
                    // A profile whose observers have different shapes is a near/far tier, and the tiers differ in more than their region: they have separate
                    // budgets, separate rates and separate record kinds. Resolving them as one union would be a quiet wrong answer, so it is refused until
                    // the tiering that gives them meaning exists.
                    throw new NotSupportedException(
                        $"Profile '{declaration.Name}' mixes a {kind} observer with a {observer.Kind} one. A profile's observers must have one shape until "
                        + "the near/far tiers that make a mixture meaningful are built.");
                }

                if (observer.Kind == ObserverKind.Sphere)
                {
                    if (!double.IsFinite(observer.Radius) || observer.Radius <= 0)
                    {
                        throw new InvalidOperationException(
                            $"Profile '{declaration.Name}' declares a Sphere observer with radius {observer.Radius}. A sphere needs a positive radius.");
                    }

                    // TWO radii, and the distinction is the whole of the band. The query runs at the larger, because the watched set must contain the
                    // band or an entity inside it is dropped from the walk and reported as a leave — the flapping the band exists to prevent. The enter
                    // radius is what an entity the session does NOT already hold has to be inside, and keeping it is what was missing: folding both into
                    // one number left nothing downstream able to tell the band from the disc, so the disc was a hard edge at the larger of the two.
                    enterRadius = Math.Max(enterRadius, observer.Radius);
                    queryRadius = Math.Max(queryRadius, Math.Max(observer.Radius, observer.LeaveRadius));
                }

                kind = observer.Kind;
                first = false;

                foreach (var archetype in observer.Archetypes)
                {
                    var index = IndexOfArchetype(plans, archetype);
                    if (index < 0)
                    {
                        throw new InvalidOperationException(
                            $"Profile '{declaration.Name}' observes '{archetype.Name}', which declares no projection. An observer reaches an archetype "
                            + "through its projection, so declare one with subs.Archetype<" + archetype.Name + ">(...) or drop it from the profile.");
                    }

                    if (!indices.Contains(index))
                    {
                        indices.Add(index);
                    }
                }
            }

            profiles[p] = new CompiledProfile(declaration.Name, indices.ToArray())
            {
                Kind = kind,
                QueryRadius = queryRadius,

                // No declared band means no band: the two radii are equal and every blend below is the identity. The engine does not invent one, because
                // the width that would stop an entity flapping is a function of how fast things move relative to an observer and how long a tick is, and
                // an engine default guessed without those is a number derived from nothing.
                EnterRadius = enterRadius > 0d ? enterRadius : queryRadius,
            };
        }

        return profiles;
    }

    private static int IndexOfArchetype(CompiledProjectionPlan[] plans, Type archetype)
    {
        for (var i = 0; i < plans.Length; i++)
        {
            if (plans[i].ArchetypeType == archetype)
            {
                return i;
            }
        }

        return -1;
    }
}
