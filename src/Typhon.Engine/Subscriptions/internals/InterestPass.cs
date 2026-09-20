using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;

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

    /// <summary>Whether co-located sessions share one query. <see cref="SubscriptionsOptions.CellKeyedInterest"/>.</summary>
    private readonly bool _cellKeyed;

    /// <summary>Whether interest is resolved at cluster granularity, reading no entity. <c>SubscriptionsOptions.ResidentInterest</c>.</summary>
    private readonly bool _resident;

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
    /// <param name="residentInterest">Whether a sphere is resolved at cluster granularity, reading no entity.</param>
    public InterestPass(DatabaseEngine engine, CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states, SubscriptionsRegistry registry,
        SessionTable sessions, bool cellKeyedInterest = true, SessionViewStore views = null, bool residentInterest = false)
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
        _resident = residentInterest;
        _views = views;
        _fenceWindow = engine.EpochManager.FenceWindow;

        _clusterStates = new ArchetypeClusterState[plans.Length];
        for (var i = 0; i < plans.Length; i++)
        {
            var archetypeStates = engine._archetypeStates;
            var catalogId = plans[i].ArchetypeCatalogId;
            _clusterStates[i] = archetypeStates != null && catalogId < archetypeStates.Length ? archetypeStates[catalogId]?.ClusterState : null;
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
    public long ClusterCandidatesCollected
    {
        get
        {
            var total = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                total += _arenas[i].ClusterCandidatesCollected;
            }

            return total;
        }
    }

    /// <summary>Cluster candidates a session accepted, summed over every session and worker of this run.</summary>
    public long ClusterCandidatesAccepted
    {
        get
        {
            var total = 0L;
            for (var i = 0; i < _arenas.Length; i++)
            {
                total += _arenas[i].ClusterCandidatesAccepted;
            }

            return total;
        }
    }

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
        _tickNumber = tickNumber;
        var workers = Math.Max(1, workerCount);
        EnsureArenas(workers);

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

        // Reset here, with the rest of the tick's state: the properties below are documented per tick and were accumulating for the life of the process, so
        // anything reading them as a tick figure — a panel, an assertion — was reading a total.
        Volatile.Write(ref _cellsResolved, 0);
        Volatile.Write(ref _sessionsShared, 0);

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
                Array.Resize(ref _tickCellKeys, grown);
                Array.Resize(ref _sortIndices, grown);
                Array.Resize(ref _permSessions, grown);
                Array.Resize(ref _permProfiles, grown);
                Array.Resize(ref _permViewpoints, grown);
                Array.Resize(ref _permPlaced, grown);
            }

            // Read HERE, single-threaded, rather than inside the chunk. The table's viewpoint slot is written by application systems on the tick thread and
            // read by every worker; snapshotting it once at the partition point means the chunks read a private array instead of racing the table, and it
            // also fixes the tick's answer — a session cannot resolve two archetypes around two different centres.
            _tickPlaced[_tickSessionCount] = _sessions.TryGetViewpoint(session, out var viewpoint);
            _tickViewpoints[_tickSessionCount] = viewpoint;
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
        }

        Array.Copy(_permSessions, _tickSessions, _tickSessionCount);
        Array.Copy(_permProfiles, _tickProfiles, _tickSessionCount);
        Array.Copy(_permViewpoints, _tickViewpoints, _tickSessionCount);
        Array.Copy(_permPlaced, _tickPlaced, _tickSessionCount);
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
        var cap = Math.Max(8, _tickSessionCount / Math.Max(1, _tickWorkerCount));

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

    /// <summary>Interest cells the broad phase resolved last tick.</summary>
    public long CellsResolved => Volatile.Read(ref _cellsResolved);

    /// <summary>Sessions last tick that were served from a cell resolution somebody else paid for.</summary>
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
        var from = (int)((long)chunkIndex * _tickSessionCount / chunkCount);
        var to = (int)((long)(chunkIndex + 1) * _tickSessionCount / chunkCount);

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
            if (count > 1)
            {
                if (_resident)
                {
                    ResolveCellGroupResident(arena, chunkIndex, i, count, ref probes, ref hits);
                }
                else
                {
                    ResolveCellGroup(arena, chunkIndex, i, count, ref probes, ref hits);
                }

                cells++;
                shared += count - 1;
            }
            else
            {
                hits += ResolveSessionDirect(arena, chunkIndex, i, ref probes);
            }
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
            _tickHits[i] = CloseSession(arena, view, chunkIndex, runStart, leaveStart, 0);
            return 0;
        }

        var sessionHits = 0;
        for (var a = 0; a < archetypes.Length; a++)
        {
            sessionHits += profile.Kind == ObserverKind.Sphere
                ? _resident
                    ? WalkSphereResident(arena, view, archetypes[a], _tickViewpoints[i], profile.EnterRadius, profile.QueryRadius, ref probes)
                    : WalkSphere(arena, view, archetypes[a], _tickViewpoints[i], profile.EnterRadius, profile.QueryRadius, ref probes)
                : WalkArchetype(arena, view, archetypes[a], ref probes);
        }

        _tickHits[i] = CloseSession(arena, view, chunkIndex, runStart, leaveStart, sessionHits);
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
    private SessionHitRange CloseSession(HitArena arena, SessionInterestView view, int chunkIndex, int runStart, int leaveStart, int hits)
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
            }
        }

        return new SessionHitRange(chunkIndex, runStart, arena.RunCount - runStart, hits, leaveStart, arena.LeaveCount - leaveStart);
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
        var centreX = (Math.Floor(_tickViewpoints[start].X / cell) + 0.5d) * cell;
        var centreY = (Math.Floor(_tickViewpoints[start].Y / cell) + 0.5d) * cell;
        var broadRadius = radius + (cell * HalfDiagonal);

        // BROAD PHASE first, for EVERY archetype, before any session is filtered.
        //
        // The order matters and is not a preference. A SessionHitRange is a CONTIGUOUS span of the arena's run list, so all of one session's runs must be
        // appended together. Querying one archetype and filtering every session through it, then moving to the next, interleaves the sessions' runs — session
        // zero's second archetype lands after session one's first — and every range then covers its neighbours' runs as well as its own. With a single-
        // archetype profile the interleaving cannot happen, which is why it survived a fixture that had one and showed up as internal errors the moment a
        // real profile with several archetypes ran.
        arena.BeginCell();
        var ranges = arena.CellRanges(archetypes.Length);

        for (var a = 0; a < archetypes.Length; a++)
        {
            ranges[a] = arena.CandidateCount;
            var clusterState = _clusterStates[archetypes[a]];
            if (clusterState == null || clusterState.Grid == null)
            {
                continue;
            }

            foreach (var hit in clusterState.QueryRadius(clusterState.Grid, centreX, centreY, _tickViewpoints[start].Z, broadRadius))
            {
                arena.AddCandidate(hit.ClusterChunkId, hit.SlotIndex, hit.MinX, hit.MinY, hit.MaxX, hit.MaxY);
            }
        }

        arena.NoteCellCollected();

        // NARROW PHASE, one session at a time, so each session's runs are contiguous.
        for (var i = start; i < start + count; i++)
        {
            var viewpoint = _tickViewpoints[i];
            var runStart = arena.RunCount;
            var leaveStart = arena.LeaveCount;
            var view = BeginView(i);
            var sessionHits = 0;

            for (var a = 0; a < archetypes.Length; a++)
            {
                var to = a + 1 < archetypes.Length ? ranges[a + 1] : arena.CandidateCount;
                if (ranges[a] == to)
                {
                    continue;
                }

                arena.BeginSphere();
                arena.FilterCandidatesInto(viewpoint.X, viewpoint.Y, enterRadius, radius, ranges[a], to);

                for (var r = 0; r < arena.SphereCount; r++)
                {
                    sessionHits += FlushSphereRun(arena, view, archetypes[a], arena.SphereChunk(r), arena.SphereNear(r), arena.SphereMask(r), ref probes);
                }
            }

            _tickHits[i] = CloseSession(arena, view, chunkIndex, runStart, leaveStart, sessionHits);
            hits += sessionHits;
        }
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

    /// <summary>
    /// Resolves a cell group's sessions at CLUSTER granularity, reading no entity at all.
    /// </summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="chunkIndex">The chunk, for the hit range's worker field.</param>
    /// <param name="start">First session of the group.</param>
    /// <param name="count">How many share the cell.</param>
    /// <param name="probes">Directory probes, accumulated.</param>
    /// <param name="hits">Hits recorded, accumulated.</param>
    /// <remarks>
    /// <para>
    /// <b>This is the interest half's third form, and what separates it from the second is what it never touches.</b> The cell-keyed pass above shares one
    /// ENTITY query between the cell's sessions and then filters its entities per session; this shares one CLUSTER query and then filters BOXES per
    /// session. At d06 that is about 540 boxes where the other collects about 11 900 entities for the same cell, and the entity columns are not read by
    /// interest on any tick.
    /// </para>
    /// <para>
    /// <b>What it costs is exactness.</b> A cluster reaching inside the disc is taken whole, so a cluster straddling the boundary contributes entities
    /// outside it. The direction is the safe one, and the only one that is safe: every entity a direct query would find lives in a cluster this admits,
    /// which is what ResidentInterestTests asserts entity by entity. The size of the over-approximation is the cluster extent, which the spatial index's
    /// own drift gate bounds, and it is reported as ClusterCandidatesCollected/Accepted so a running server states it rather than a geometry argument.
    /// </para>
    /// <para>
    /// <b>The order is the cell-keyed pass's, for the same reason</b>: the whole broad phase first, for every archetype, and only then one session at a
    /// time. A SessionHitRange is a contiguous span of the arena's run list, so interleaving two sessions' runs makes every range cover its neighbours'.
    /// </para>
    /// </remarks>
    private void ResolveCellGroupResident(HitArena arena, int chunkIndex, int start, int count, ref long probes, ref long hits)
    {
        var profile = _profiles[_tickProfiles[start]];
        var archetypes = profile.ArchetypeIndices;
        var radius = profile.QueryRadius;
        var enterRadius = profile.EnterRadius;
        var cell = CellSideFor(radius);
        var centreX = (Math.Floor(_tickViewpoints[start].X / cell) + 0.5d) * cell;
        var centreY = (Math.Floor(_tickViewpoints[start].Y / cell) + 0.5d) * cell;
        var broadRadius = radius + (cell * HalfDiagonal);

        arena.BeginClusterCell();
        var ranges = arena.CellRanges(archetypes.Length);
        Span<ClusterBroadphaseHit> batch = stackalloc ClusterBroadphaseHit[64];

        for (var a = 0; a < archetypes.Length; a++)
        {
            ranges[a] = arena.ClusterCandidateCount;
            var clusterState = _clusterStates[archetypes[a]];
            if (clusterState == null || clusterState.Grid == null)
            {
                continue;
            }

            var query = clusterState.QueryRadius(clusterState.Grid, centreX, centreY, _tickViewpoints[start].Z, broadRadius);
            int filled;
            while ((filled = query.FillClusters(batch)) > 0)
            {
                for (var k = 0; k < filled; k++)
                {
                    ref readonly var cluster = ref batch[k];
                    arena.AddClusterCandidate(cluster.ChunkId, cluster.Slots, cluster.MinX, cluster.MinY, cluster.MaxX, cluster.MaxY);
                }
            }
        }

        arena.NoteClusterCellCollected();

        for (var i = start; i < start + count; i++)
        {
            var viewpoint = _tickViewpoints[i];
            var runStart = arena.RunCount;
            var leaveStart = arena.LeaveCount;
            var view = BeginView(i);
            var sessionHits = 0;

            for (var a = 0; a < archetypes.Length; a++)
            {
                var to = a + 1 < archetypes.Length ? ranges[a + 1] : arena.ClusterCandidateCount;
                if (ranges[a] == to)
                {
                    continue;
                }

                arena.BeginPick();
                arena.SelectClustersInto(viewpoint.X, viewpoint.Y, enterRadius, radius, ranges[a], to);
                for (var r = 0; r < arena.PickCount; r++)
                {
                    sessionHits += FlushSphereRun(arena, view, archetypes[a], arena.PickChunk(r), arena.PickNear(r), arena.PickMask(r), ref probes);
                }
            }

            _tickHits[i] = CloseSession(arena, view, chunkIndex, runStart, leaveStart, sessionHits);
            hits += sessionHits;
        }
    }

    /// <summary>
    /// Resolves one archetype for one sphere session at cluster granularity, the ungrouped counterpart of <see cref="ResolveCellGroupResident"/>.
    /// </summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="view">The session's interest membership, or <see langword="null"/> when the pass is not incremental.</param>
    /// <param name="archetypeIndex">The archetype's plan index.</param>
    /// <param name="centre">The sphere's centre.</param>
    /// <param name="enterRadius">The radius something the session does not already hold must be inside, in world units.</param>
    /// <param name="radius">The radius it may stay inside once held; the query runs at this one.</param>
    /// <param name="probes">Directory probes, accumulated.</param>
    /// <returns>Hits recorded.</returns>
    /// <remarks>
    /// The query is the session's own disc rather than an enlarged cell, but the broadphase tests the disc's enclosing BOX, so a cluster sitting in a
    /// corner of it passes the box and misses the disc. The closest-point test is repeated here for that reason, and so that this path's membership is the
    /// grouped one's.
    /// </remarks>
    private int WalkSphereResident(HitArena arena, SessionInterestView view, int archetypeIndex, Vector3D centre, double enterRadius, double radius,
        ref long probes)
    {
        var clusterState = _clusterStates[archetypeIndex];
        if (clusterState == null || clusterState.Grid == null)
        {
            return 0;
        }

        Span<ClusterBroadphaseHit> batch = stackalloc ClusterBroadphaseHit[64];
        var query = clusterState.QueryRadius(clusterState.Grid, centre.X, centre.Y, centre.Z, radius);
        var radiusSq = radius * radius;
        var enterSq = enterRadius * enterRadius;
        var hits = 0;
        int filled;
        while ((filled = query.FillClusters(batch)) > 0)
        {
            for (var k = 0; k < filled; k++)
            {
                ref readonly var cluster = ref batch[k];
                var dx = Math.Max(Math.Max(cluster.MinX - centre.X, 0d), centre.X - cluster.MaxX);
                var dy = Math.Max(Math.Max(cluster.MinY - centre.Y, 0d), centre.Y - cluster.MaxY);
                var distSq = (dx * dx) + (dy * dy);
                if (distSq > radiusSq)
                {
                    continue;
                }

                // All or nothing per cluster: this form reads no entity, so the cluster's closest point is the only thing it can say about the band.
                var near = distSq <= enterSq ? cluster.Slots : 0UL;
                hits += FlushSphereRun(arena, view, archetypeIndex, cluster.ChunkId, near, cluster.Slots, ref probes);
            }
        }

        return hits;
    }

    /// <summary>Records one cluster's worth of sphere hits as a run, marking the slots watched.</summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="archetypeIndex">The archetype's plan index.</param>
    /// <param name="chunkId">The cluster, or -1 for "nothing accumulated yet".</param>
    /// <param name="nearMask">The slots inside the observer's ENTER radius, which it may take whether or not it already holds them.</param>
    /// <param name="farMask">
    /// The slots inside the LEAVE radius, a superset of <paramref name="nearMask"/>. Those outside the near mask are admitted only for a session that
    /// already holds them; equal to it when the profile declared no band, which makes the blend the identity.
    /// </param>
    /// <param name="probes">Directory probes, accumulated.</param>
    /// <returns>Hits recorded.</returns>
    /// <param name="view">The session's interest membership, or <see langword="null"/> when the pass is not incremental.</param>
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
        var mask = farMask;
        if (nearMask != farMask)
        {
            mask = nearMask | (farMask & (view?.HeldMask(SessionInterestView.KeyOf((ushort)archetypeIndex, chunkId)) ?? 0UL));
            if (mask == 0)
            {
                return 0;
            }
        }

        var directory = _states[archetypeIndex].Directory;
        var stamp = (uint)_tickNumber;
        probes++;

        nint blockAddress = 0;
        ushort flags = InterestRunFlags.None;
        if (directory.TryGetBlock(chunkId, out var block))
        {
            blockAddress = (nint)block;
            MarkWatched(arena, block, mask, stamp);
        }
        else
        {
            flags = InterestRunFlags.NoBlock;
            arena.AddNewBlock(archetypeIndex, chunkId);
        }

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
            viewIndex = view.Touch(SessionInterestView.KeyOf((ushort)archetypeIndex, chunkId), _tickNumber);
            var held = view.MaskAt(viewIndex);
            entered = mask & ~held;

            var left = held & ~mask;
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
