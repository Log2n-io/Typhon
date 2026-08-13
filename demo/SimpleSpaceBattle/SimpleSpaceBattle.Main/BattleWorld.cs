using System.Numerics;
using System.Runtime.InteropServices;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SimpleSpaceBattle;

/// <summary>Per-worker scratch, padded to a cache line so parallel systems never false-share their counters.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct WorkerCounters
{
    [FieldOffset(0)] public long Shots;
    [FieldOffset(8)] public long Hits;
    [FieldOffset(16)] public long Acquisitions;
    [FieldOffset(24)] public long LocksLost;
    [FieldOffset(32)] public long Gathers;
}

/// <summary>How a run ended.</summary>
public enum BattleOutcome
{
    Running = 0,
    Winner = 1,
    Draw = 2,
    TimedOut = 3,
}

/// <summary>
/// Shared state the five systems hang off — the engine handle, the view, the target lane, the per-worker death
/// buffers and the counters. Deliberately a plain object rather than a service graph: this is a demo, and the
/// indirection would obscure the thing being demonstrated.
/// </summary>
internal sealed class BattleWorld
{
    private readonly List<EntityId>[] _deaths;
    private readonly WorkerCounters[] _counters;
    private readonly NeighbourGather[] _gathers;
    private readonly Transaction[] _workerTx;
    private readonly long[] _workerTxTick;

    public BattleWorld(DatabaseEngine dbe, SimulationConfig config, int workerCount)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workerCount);

        Dbe = dbe;
        Config = config;
        WorkerCount = workerCount;

        _deaths = new List<EntityId>[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _deaths[i] = new List<EntityId>(256);
        }

        _counters = new WorkerCounters[workerCount];

        _gathers = new NeighbourGather[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _gathers[i] = new NeighbourGather();
        }

        _workerTx = new Transaction[workerCount];
        _workerTxTick = new long[workerCount];
        Array.Fill(_workerTxTick, -1L);
    }

    public DatabaseEngine Dbe { get; }

    public SimulationConfig Config { get; }

    public int WorkerCount { get; }

    public TargetLane Lane { get; set; }

    /// <summary>
    /// Active cluster count, refreshed once per tick from the sequential Reap phase. The three parallel systems
    /// partition this range across their chunks (see <see cref="ClusterWork"/>).
    /// <para>
    /// Deliberately NOT read per chunk: it would be a cross-thread read of a value the Reaper mutates, and the
    /// count only changes when a destroy empties a cluster — which happens in Reap, after every parallel phase has
    /// finished. Sampling it once per tick is both cheaper and race-free.
    /// </para>
    /// </summary>
    public int ActiveClusterCount { get; private set; }

    /// <summary>
    /// Shared read accessor for systems that need cluster storage but issue no spatial query, so need no epoch
    /// scope. Re-attached once per tick from the sequential Reap phase, which is also the only place that can
    /// change cluster membership.
    /// </summary>
    public PointInTimeAccessor Accessor { get; } = new();

    /// <summary>
    /// Re-attach the shared snapshot and re-sample the active cluster count. Sequential phases only.
    /// <para>
    /// The count is read through the freshly-attached accessor rather than a throwaway transaction: opening one
    /// here cost <b>5.5 ms</b> on the sequential path, which at a 40 ms budget is 14 % of the tick spent on a single
    /// integer read.
    /// </para>
    /// </summary>
    public void RefreshClusterCount() => ActiveClusterCount = Accessor.GetWorkerAccessor(0).For<Ship>().ClusterCount;

    /// <summary>
    /// Re-attach the shared read snapshot. Only needed when a destroy may have invalidated a cached chunk accessor;
    /// it costs ~5 ms, so it is deliberately kept off the every-tick path (see <see cref="ReaperSystem"/>).
    /// </summary>
    public void ReattachAccessor() => Accessor.Attach(Dbe, WorkerCount);

    public TyphonRuntime Runtime { get; set; }

    // ── Live counters, read by the Observer ────────────────────────────────

    public int AliveCount { get; set; }

    public long TotalDeaths { get; private set; }

    public long TotalShots { get; private set; }

    public long TotalHits { get; private set; }

    public long TotalAcquisitions { get; private set; }

    /// <summary>Spatial queries issued by Resolution. Measures how well the per-cell gather cache is working:
    /// the floor is one per occupied cell per tick, the ceiling one per cluster.</summary>
    public long TotalGathers { get; private set; }

    public ulong CompletedTicks { get; set; }

    public BattleOutcome Outcome { get; set; } = BattleOutcome.Running;

    public EntityId Winner { get; set; }

    /// <summary>Set by the Reaper when the run reaches a terminal state; the host polls it to shut down.</summary>
    public bool IsTerminal => Outcome != BattleOutcome.Running;

    // ── Per-worker lanes ───────────────────────────────────────────────────

    /// <summary>
    /// A worker's death buffer. Indexed by <c>ctx.WorkerId</c>, never <c>ctx.ChunkIndex</c> — the latter stays 0 on
    /// the parallel-query path (only <c>ExecuteChunkedCallback</c> populates it), and with
    /// <c>ChunksPerWorker &gt; 1</c> a chunk index can exceed <c>WorkerCount</c> anyway.
    /// <para>
    /// This exists instead of an <c>EventQueue&lt;T&gt;</c> because <c>EventQueue.Push</c> is <c>_buffer[_count++]</c>
    /// — a plain non-atomic increment. AntHill races on it and survives because a dropped stats event is invisible;
    /// a dropped death event here would mean a ship never dies.
    /// </para>
    /// </summary>
    public List<EntityId> DeathsForWorker(int workerId) => _deaths[workerId];

    public ref WorkerCounters CountersForWorker(int workerId) => ref _counters[workerId];

    /// <summary>Per-worker neighbour scratch, reused across clusters and ticks — no tick-loop allocation.</summary>
    public NeighbourGather GatherForWorker(int workerId) => _gathers[workerId];

    /// <summary>
    /// The worker's transaction for this tick, created on first use and reused by every chunk that worker runs —
    /// across both the Acquire and Fire phases.
    ///
    /// <para><b>Why.</b> A transaction exists here only to provide the ambient <c>EpochGuard</c> that
    /// <c>ClusterSpatialQuery</c> requires (<c>EpochGuard</c> is <c>internal</c>, so game code has no other way to
    /// open one). Opening one per <i>chunk</i> meant ~90 per tick, and dotTrace put
    /// <c>CreateUnitOfWork → AccessControlSmall.EnterExclusiveAccess</c> at <b>42 % of ResolutionSystem</b> — pure
    /// contention, 30 workers claiming one exclusive lock at once. Per <i>worker</i> takes that to 30 (issue #798).</para>
    ///
    /// <para><b>Thread affinity.</b> Create and dispose both happen on the worker's own thread: the disposal of
    /// tick T's transaction is performed by the first chunk of tick T+1 on that same worker. A transaction is
    /// therefore never touched from a thread other than the one that made it.</para>
    ///
    /// <para><b>Accepted cost.</b> The transaction spans the tick fence, so its epoch defers page reclamation by one
    /// tick. At ~3 MB of live cluster state against an 88 MB page cache that is immaterial; it would matter for a
    /// workload near its memory envelope.</para>
    /// </summary>
    public Transaction TransactionForWorker(int workerId, long tick)
    {
        if (_workerTxTick[workerId] == tick)
        {
            return _workerTx[workerId];
        }

        // Same thread that created it — this is the only place disposal happens.
        _workerTx[workerId]?.Dispose();
        Transaction tx = Dbe.CreateQuickTransaction();
        _workerTx[workerId] = tx;
        _workerTxTick[workerId] = tick;
        return tx;
    }

    /// <summary>
    /// Best-effort teardown of any worker transaction still open at shutdown. Cross-thread, hence the catch: the
    /// tick loop has already stopped, so nothing is racing, but the engine may still object to a foreign-thread
    /// dispose. Leaking one at process exit is harmless.
    /// </summary>
    public void DisposeWorkerTransactions()
    {
        for (int w = 0; w < WorkerCount; w++)
        {
            try
            {
                _workerTx[w]?.Dispose();
            }
            catch (Exception)
            {
                // Deliberately swallowed — see summary.
            }

            _workerTx[w] = null;
            _workerTxTick[w] = -1;
        }
    }

    /// <summary>
    /// Fold the per-worker lanes into the totals and hand back the merged death set. Called from the sequential Reap
    /// phase only. Merging in ascending <c>WorkerId</c> makes the destruction order independent of thread scheduling,
    /// which is what keeps the run deterministic across worker counts.
    /// </summary>
    public void DrainWorkerLanes(List<EntityId> mergedDeaths)
    {
        mergedDeaths.Clear();

        for (int w = 0; w < WorkerCount; w++)
        {
            List<EntityId> deaths = _deaths[w];
            for (int i = 0; i < deaths.Count; i++)
            {
                mergedDeaths.Add(deaths[i]);
            }

            deaths.Clear();

            ref WorkerCounters c = ref _counters[w];
            TotalShots += c.Shots;
            TotalHits += c.Hits;
            TotalAcquisitions += c.Acquisitions;
            TotalGathers += c.Gathers;
            c.Gathers = 0;
            c.Shots = 0;
            c.Hits = 0;
            c.Acquisitions = 0;
            c.LocksLost = 0;
        }

        TotalDeaths += mergedDeaths.Count;
    }

    // ── Bootstrap ──────────────────────────────────────────────────────────

    /// <summary>
    /// Spawn the initial fleet in one transaction. Positions uniform in the world box, velocities uniform on the
    /// sphere at cruise speed, health full, unlocked. Sequential — parallel bulk loading is deferred (#236) — and
    /// timed separately so it never contaminates tick statistics.
    /// </summary>
    public TimeSpan SpawnFleet()
    {
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        SimulationConfig cfg = Config;
        var rng = new SplitMix64(cfg.Seed);

        using (Transaction tx = Dbe.CreateQuickTransaction())
        {
            for (int i = 0; i < cfg.ShipCount; i++)
            {
                float x = rng.NextFloat() * cfg.WorldX;
                float y = rng.NextFloat() * cfg.WorldY;
                float z = rng.NextFloat() * cfg.WorldZ;

                var hull = new HullComponent
                {
                    Bounds = new AABB3F { MinX = x, MinY = y, MinZ = z, MaxX = x, MaxY = y, MaxZ = z },
                };

                rng.NextUnitVector(out float dx, out float dy, out float dz);
                var motion = new MotionComponent
                {
                    X = dx * cfg.CruiseSpeed,
                    Y = dy * cfg.CruiseSpeed,
                    Z = dz * cfg.CruiseSpeed,
                };

                var vitals = new VitalsComponent { Health = cfg.MaximumHealth };
                var targeting = new TargetingComponent { TargetRawId = TargetingComponent.Unlocked };

                tx.Spawn<Ship>(
                    Ship.Hull.Set(in hull),
                    Ship.Motion.Set(in motion),
                    Ship.Vitals.Set(in vitals),
                    Ship.Targeting.Set(in targeting));
            }

            tx.Commit();
        }

        AliveCount = cfg.ShipCount;
        return System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
    }

    /// <summary>
    /// Size the target lane to cover every cluster that exists after the fleet is spawned. Clusters are only ever
    /// created by spawn, and the only spawn is the bootstrap above, so a single sizing pass here is provably
    /// sufficient for the whole run — destroy frees slots but never adds clusters.
    /// </summary>
    public void SizeTargetLane()
    {
        using Transaction tx = Dbe.CreateQuickTransaction();
        int maxChunkId = 0;
        int clusterSize = 0;
        int clusterCount = 0;

        // Transaction derives from EntityAccessor, so the cluster enumerator is available directly.
        using (ClusterEnumerator<Ship> clusters = tx.GetClusterEnumerator<Ship>())
        {
            foreach (ClusterRef<Ship> cluster in clusters)
            {
                maxChunkId = Math.Max(maxChunkId, cluster.ChunkId);
                clusterSize = cluster.ClusterSize;
                clusterCount++;
            }
        }

        if (clusterSize == 0)
        {
            throw new InvalidOperationException("SizeTargetLane: no Ship clusters exist — was the fleet spawned?");
        }

        ClusterCount = clusterCount;
        ClusterSize = clusterSize;

        // 2× headroom over the highest observed chunk id. Costs ~1 MB and removes any chance of an index fault in a
        // parallel phase, where growing the array is not an option (a resize swaps the backing reference).
        Lane = new TargetLane(clusterSize, (maxChunkId + 1) * 2 + 64);
    }

    public int ClusterSize { get; private set; }

    public int ClusterCount { get; private set; }

    /// <summary>
    /// Order-independent fingerprint of the whole fleet: alive count plus additive checksums over health and
    /// target. Additive specifically — cluster walk order depends on how work was partitioned, so any
    /// order-sensitive digest would report a false divergence between worker counts.
    /// </summary>
    public (int Alive, ulong HealthChecksum, ulong TargetChecksum) Checksum()
    {
        ulong health = 0;
        ulong targets = 0;
        int alive = 0;

        using Transaction tx = Dbe.CreateQuickTransaction();
        using ClusterEnumerator<Ship> clusters = tx.GetClusterEnumerator<Ship>();

        foreach (ClusterRef<Ship> cluster in clusters)
        {
            ulong bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            ReadOnlySpan<VitalsComponent> vitals = cluster.GetReadOnlySpan(Ship.Vitals);
            ReadOnlySpan<TargetingComponent> targeting = cluster.GetReadOnlySpan(Ship.Targeting);
            ReadOnlySpan<long> ids = cluster.EntityIds;

            while (bits != 0)
            {
                int i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                alive++;

                // Mix each entity's identity into its contribution so the sum is sensitive to WHICH ship holds
                // WHICH value, not just to the multiset of values.
                ulong key = (ulong)ids[i];
                health += key * 31UL + vitals[i].Health;
                targets += key * 17UL + (ulong)targeting[i].TargetRawId;
            }
        }

        return (alive, health, targets);
    }

    /// <summary>
    /// Replay the <see cref="ResolutionSystem"/> gather over every cluster and count how many times a single query
    /// yields the same entity more than once. Diagnostic only — see the test of the same name.
    /// </summary>
    public int CountDuplicateQueryHits(float scanRange, out int clustersProbed, out int worstCluster)
    {
        int duplicates = 0;
        clustersProbed = 0;
        worstCluster = 0;
        var seen = new HashSet<long>();

        using Transaction tx = Dbe.CreateQuickTransaction();
        using ClusterEnumerator<Ship> clusters = tx.GetClusterEnumerator<Ship>();

        foreach (ClusterRef<Ship> cluster in clusters)
        {
            if (cluster.OccupancyBits == 0)
            {
                continue;
            }

            clustersProbed++;
            seen.Clear();
            int localDuplicates = 0;

            ClusterSpatialAabb b = cluster.SpatialBounds;
            var box = new AABB3F
            {
                MinX = b.MinX - scanRange,
                MinY = b.MinY - scanRange,
                MinZ = b.MinZ - scanRange,
                MaxX = b.MaxX + scanRange,
                MaxY = b.MaxY + scanRange,
                MaxZ = b.MaxZ + scanRange,
            };

            foreach (ClusterSpatialQueryResult hit in Dbe.ClusterSpatialQuery<Ship>().AABB(in box))
            {
                if (!seen.Add(hit.EntityId))
                {
                    localDuplicates++;
                }
            }

            duplicates += localDuplicates;
            worstCluster = Math.Max(worstCluster, localDuplicates);
        }

        return duplicates;
    }

    /// <summary>
    /// Count ships the spatial index can no longer find at their own current position.
    ///
    /// <para>This is the enforcement for <see cref="MovementSystem"/>'s load-bearing negative: it writes
    /// <c>Hull</c> through <c>GetSpan</c> (because <c>WriteSpatial</c> rejects <c>AABB3F</c>) and depends on
    /// <c>SpatialBarrierOnly</c> staying <b>false</b> so the fence rescans every active cluster. Calling
    /// <c>SetSpatialBarrierOnly&lt;Ship&gt;</c> would make those writes invisible to spatial maintenance and freeze
    /// the index — with no exception, no warning, and a simulation that still produces plausible-looking output.
    /// A comment cannot catch that; this can.</para>
    ///
    /// <para>Probing each ship at its own stored position is the sharpest form of the check: a frozen index leaves
    /// the ship's cluster registered to the cell it has since left, so a query at the new position never visits it.</para>
    /// </summary>
    public int CountShipsMissingFromIndex(float probeRadius)
    {
        int missing = 0;

        using Transaction tx = Dbe.CreateQuickTransaction();
        using ClusterEnumerator<Ship> clusters = tx.GetClusterEnumerator<Ship>();

        foreach (ClusterRef<Ship> cluster in clusters)
        {
            ulong bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            ReadOnlySpan<HullComponent> hulls = cluster.GetReadOnlySpan(Ship.Hull);
            ReadOnlySpan<long> ids = cluster.EntityIds;

            while (bits != 0)
            {
                int i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                var sphere = new BSphere3F
                {
                    CenterX = hulls[i].Bounds.MinX,
                    CenterY = hulls[i].Bounds.MinY,
                    CenterZ = hulls[i].Bounds.MinZ,
                    Radius = probeRadius,
                };

                long wanted = ids[i];
                bool found = false;

                foreach (ClusterSpatialQueryResult hit in Dbe.ClusterSpatialQuery<Ship>().Radius(in sphere))
                {
                    if (hit.EntityId == wanted)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    missing++;
                }
            }
        }

        return missing;
    }

    public SpatialGridConfig GridConfig => new(
        Vector2.Zero,
        new Vector2(Config.WorldX, Config.WorldY),
        Config.CellSize);
}
