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
    public SessionHitRange(int worker, int start, int count, int hits)
    {
        Worker = worker;
        Start = start;
        Count = count;
        Hits = hits;
    }

    /// <summary>Index of the arena holding the runs.</summary>
    public int Worker { get; }

    /// <summary>First run in that arena.</summary>
    public int Start { get; }

    /// <summary>How many runs.</summary>
    public int Count { get; }

    /// <summary>How many entities those runs carry.</summary>
    public int Hits { get; }
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
    }

    private readonly CompiledProjectionPlan[] _plans;
    private readonly ArchetypeReplicationState[] _states;
    private readonly ArchetypeClusterState[] _clusterStates;
    private readonly SessionTable _sessions;
    private readonly ExclusiveWindow _fenceWindow;
    private readonly CompiledProfile[] _profiles;
    private readonly Dictionary<string, int> _profileByName = new(StringComparer.Ordinal);

    private HitArena[] _arenas = [];
    private SessionId[] _tickSessions = [];
    private int[] _tickProfiles = [];
    private SessionHitRange[] _tickHits = [];
    private int _tickSessionCount;
    private long _tickNumber;

    /// <summary>
    /// Resolves the declared profiles against the compiled plans and the engine's cluster states, once, at <c>Start</c>.
    /// </summary>
    /// <param name="engine">The engine whose clusters the observers walk.</param>
    /// <param name="plans">One compiled plan per replicated archetype.</param>
    /// <param name="states">The per-archetype replication state, parallel to <paramref name="plans"/>.</param>
    /// <param name="registry">The declarations, for the profiles.</param>
    /// <param name="sessions">The session table, whose open rows are this pass's input.</param>
    /// <exception cref="InvalidOperationException">An observer names an archetype that has no projection, so it could never be replicated.</exception>
    public InterestPass(DatabaseEngine engine, CompiledProjectionPlan[] plans, ArchetypeReplicationState[] states, SubscriptionsRegistry registry,
        SessionTable sessions)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(sessions);

        _plans = plans;
        _states = states;
        _sessions = sessions;
        _fenceWindow = engine.EpochManager.FenceWindow;

        _clusterStates = new ArchetypeClusterState[plans.Length];
        for (var i = 0; i < plans.Length; i++)
        {
            var archetypeStates = engine._archetypeStates;
            var catalogId = plans[i].ArchetypeCatalogId;
            _clusterStates[i] = archetypeStates != null && catalogId < archetypeStates.Length ? archetypeStates[catalogId]?.ClusterState : null;
        }

        _profiles = CompileProfiles(registry, plans);
        for (var i = 0; i < _profiles.Length; i++)
        {
            _profileByName[_profiles[i].Name] = i;
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
            }

            _tickProfiles[_tickSessionCount] = profile;
            _tickHits[_tickSessionCount] = default;
            _tickSessions[_tickSessionCount++] = session;
        }

        return Math.Min(workers, _tickSessionCount);
    }

    /// <summary>Resolves the interest of the sessions this chunk owns.</summary>
    /// <param name="chunkIndex">The chunk, which is also the index of the arena it fills.</param>
    /// <param name="chunkCount">How many chunks the stage dispatched.</param>
    /// <remarks>
    /// Sessions are partitioned by index, the same partition <c>Frames</c> uses, so a session's runs are still in the worker's cache when its frame is
    /// assembled (<c>foundation/03 § 2.5</c>).
    /// </remarks>
    public void ExecuteChunk(int chunkIndex, int chunkCount)
    {
        if (chunkCount <= 0 || (uint)chunkIndex >= (uint)_arenas.Length)
        {
            return;
        }

        var arena = _arenas[chunkIndex];
        var start = (int)((long)chunkIndex * _tickSessionCount / chunkCount);
        var end = (int)((long)(chunkIndex + 1) * _tickSessionCount / chunkCount);

        // Accumulated in locals and flushed once below: see HitArena's remarks on why a counter moved per cluster would be a shared line.
        var probes = 0L;
        var hits = 0L;

        for (var i = start; i < end; i++)
        {
            var archetypes = _profiles[_tickProfiles[i]].ArchetypeIndices;
            var runStart = arena.RunCount;
            var sessionHits = 0;

            for (var a = 0; a < archetypes.Length; a++)
            {
                sessionHits += WalkArchetype(arena, archetypes[a], ref probes);
            }

            _tickHits[i] = new SessionHitRange(chunkIndex, runStart, arena.RunCount - runStart, sessionHits);
            hits += sessionHits;
        }

        arena.Note(probes, hits, _fenceWindow.IsOpen);
    }

    /// <summary>
    /// Walks one archetype's active clusters for one session, recording a run per non-empty cluster and marking its entities watched.
    /// </summary>
    /// <param name="arena">The worker's arena.</param>
    /// <param name="archetypeIndex">The archetype's plan index.</param>
    /// <param name="probes">Directory probes, accumulated.</param>
    /// <returns>Hits recorded.</returns>
    private int WalkArchetype(HitArena arena, int archetypeIndex, ref long probes)
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

            arena.AddRun(archetypeIndex, chunkId, blockAddress, occupancy, flags);
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

            foreach (var observer in declaration.Observers)
            {
                if (observer.Kind != ObserverKind.World)
                {
                    // Unreachable through TyphonRuntime, which freezes the registry and refuses every other kind with the phase that builds it. A pass built
                    // directly against an unfrozen registry would otherwise treat a Sphere as a World, which is a wrong answer rather than a missing one.
                    throw new NotSupportedException(
                        $"Profile '{declaration.Name}' declares a {observer.Kind} observer, which Phase 2 builds. Phase 1 resolves the World observer only.");
                }

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

            profiles[p] = new CompiledProfile(declaration.Name, indices.ToArray());
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
