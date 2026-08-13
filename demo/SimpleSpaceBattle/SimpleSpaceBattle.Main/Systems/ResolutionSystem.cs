using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SimpleSpaceBattle;

/// <summary>
/// Phase <see cref="BattlePhases.Fire"/> — the bulk of the tick, and the system the whole design exists to make
/// possible.
///
/// <para>One <c>WeaponRange</c> query per ship yields three results at once:</para>
/// <list type="number">
///   <item><b>Incoming damage</b> — for each neighbour, read its published target from the lane; if it points at me,
///         and it fires this tick, and the shot connects, take the damage.</item>
///   <item><b>Pursuit</b> — my own target is usually inside this same scan, so its position comes free.</item>
///   <item><b>Lock validity</b> — if my target was not in the scan it is dead or out of range; drop it.</item>
/// </list>
///
/// <para><b>Every write is to the iterating entity.</b> Damage is <i>pulled</i>: the defender computes what it
/// receives rather than the attacker pushing it. That is what removes the damage queue, the atomics and the
/// checkerboard, and it is only possible because the attacker published its choice one phase earlier
/// (DESIGN.md §6.1).</para>
///
/// <para><b>No component-table reads.</b> The entire cross-entity data flow is
/// <c>ClusterSpatialQueryResult</c> — id, bounds and squared distance, read by the narrowphase straight out of
/// cluster storage — plus one flat lane load. There is therefore no opportunity for a torn read anywhere in this
/// system.</para>
/// </summary>
internal sealed class ResolutionSystem : CallbackSystem
{
    private readonly BattleWorld _world;

    public ResolutionSystem(BattleWorld world) => _world = world;

    protected override void Configure(SystemBuilder b) => b
        .Name("Resolution")
        .ShouldRun(() => !_world.IsTerminal)
        .Phase(BattlePhases.Fire)
        .ChunkedParallel(_world.WorkerCount * _world.Config.ResolutionChunksPerWorker)
        .Reads<HullComponent>()
        .Writes<VitalsComponent>()
        .Writes<MotionComponent>()
        .Writes<TargetingComponent>()
        .ReadsResource("TargetLane");

    protected override void Execute(TickContext ctx)
    {
        using ClusterWork work = ClusterWork.Open(_world, in ctx);
        if (work.IsEmpty)
        {
            return;
        }

        SimulationConfig cfg = _world.Config;
        DatabaseEngine dbe = _world.Dbe;
        TargetLane lane = _world.Lane;
        long[] laneBacking = lane.Backing;
        int laneLength = laneBacking.Length;
        int clusterSize = lane.ClusterSize;

        ulong tick = (ulong)ctx.TickNumber;
        int fireMask = cfg.FireIntervalTicks - 1;

        // The scan runs at ACQUISITION range, not weapon range, and damage is filtered to weapon range inside the
        // loop. Scanning at weapon range instead would mean a ship could never hold a lock on a target it is still
        // closing on: the target would be absent from every scan until it came inside 30 units, so the lock would be
        // dropped the same tick it was acquired and pursuit could never happen. Attackers within weapon range are a
        // subset of those within acquisition range, so incoming-fire detection stays exact.
        float scanRange = cfg.AcquisitionRange;
        float weaponRangeSq = cfg.WeaponRangeSq;
        uint damagePerHit = cfg.DamagePerHit;
        // Fixed timestep — ctx.DeltaTime is wall-clock and would make the run irreproducible (DESIGN.md §9).
        float turnDelta = cfg.TurnRate * cfg.DeltaTime;
        float cruiseSpeed = cfg.CruiseSpeed;

        List<EntityId> deaths = _world.DeathsForWorker(ctx.WorkerId);
        ref WorkerCounters counters = ref _world.CountersForWorker(ctx.WorkerId);
        long shots = 0;
        long hits = 0;
        long locksLost = 0;

        NeighbourGather gather = _world.GatherForWorker(ctx.WorkerId);
        float scanRangeSq = scanRange * scanRange;

        // Gather is keyed on the CELL, not the cluster: a cell holds ~3 clusters whose AABBs are all approximately
        // the cell, so a per-cluster gather ran the same query ~3x over. Clusters are allocated per cell, so the
        // active list keeps a cell's clusters adjacent and a one-entry cache captures nearly all of the reuse.
        SpatialGridAccessor grid = ctx.SpatialGrid;
        float cellSize = cfg.CellSize;
        int cachedCellKey = -1;
        long gathers = 0;

        using ClusterEnumerator<Ship> clusters = work.Clusters();

        foreach (ClusterRef<Ship> cluster in clusters)
        {
            ulong bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            ReadOnlySpan<HullComponent> hulls = cluster.GetReadOnlySpan(Ship.Hull);
            Span<VitalsComponent> vitals = cluster.GetSpan(Ship.Vitals);
            Span<MotionComponent> motions = cluster.GetSpan(Ship.Motion);
            Span<TargetingComponent> targeting = cluster.GetSpan(Ship.Targeting);
            ReadOnlySpan<long> ids = cluster.EntityIds;

            // ONE spatial query per CELL, counting-sorted into a bin grid (see NeighbourGather). Every ship in the
            // cell lies inside the cell extent, so that extent expanded by scanRange is a superset of every one of
            // their individual neighbourhoods — one query legitimately serves the cell's ~125 ships.
            ClusterSpatialAabb cb = cluster.SpatialBounds;
            int cellKey = grid.WorldToCell((cb.MinX + cb.MaxX) * 0.5f, (cb.MinY + cb.MaxY) * 0.5f);

            if (cellKey != cachedCellKey)
            {
                (int cellX, int cellY) = grid.GetCellCoords(cellKey);
                float cellMinX = cellX * cellSize;
                float cellMinY = cellY * cellSize;

                // Z is not partitioned by the grid (§3.2), so the cell spans the whole world depth.
                gather.FillBox(
                    dbe,
                    cellMinX, cellMinY, 0f,
                    cellMinX + cellSize, cellMinY + cellSize, cfg.WorldZ,
                    scanRange, laneBacking, clusterSize);

                cachedCellKey = cellKey;
                gathers++;
            }

            int candidateCount = gather.Count;
            float[] gx = gather.X, gy = gather.Y, gz = gather.Z;
            long[] gid = gather.Id, gtarget = gather.Target;
            int[] binStart = gather.BinStart;
            int binsX = gather.BinsX;
            int binsY = gather.BinsY;

            while (bits != 0)
            {
                int i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                long me = ids[i];
                long myTarget = targeting[i].TargetRawId;
                float x = hulls[i].Bounds.MinX;
                float y = hulls[i].Bounds.MinY;
                float z = hulls[i].Bounds.MinZ;

                uint damage = 0;
                bool targetSeen = false;
                float tx = 0f, ty = 0f, tz = 0f;

                // Sweep only the bins this ship's own sphere touches, not the whole cluster gather. Bins are
                // x-major, so each (z,y) pair yields ONE contiguous candidate run — the inner loop stays a
                // straight walk over sequential float arrays.
                if (candidateCount > 0)
                {
                    gather.Window(x, y, z, scanRange, out int bx0, out int bx1, out int by0, out int by1, out int bz0, out int bz1);

                    for (int bz = bz0; bz <= bz1; bz++)
                    {
                        for (int by = by0; by <= by1; by++)
                        {
                            int rowBase = (bz * binsY + by) * binsX;
                            int end = binStart[rowBase + bx1 + 1];

                            for (int c = binStart[rowBase + bx0]; c < end; c++)
                            {
                                float dx = gx[c] - x;
                                float dy = gy[c] - y;
                                float dz = gz[c] - z;
                                float distSq = dx * dx + dy * dy + dz * dz;

                                if (distSq > scanRangeSq)
                                {
                                    continue;
                                }

                                long other = gid[c];
                                if (other == me)
                                {
                                    continue;
                                }

                                // (a) Incoming fire — only from attackers actually inside weapon range.
                                if (distSq <= weaponRangeSq && gtarget[c] == me && CombatRules.Fires(other, tick, fireMask))
                                {
                                    shots++;
                                    if (CombatRules.Hits(other, me, tick, distSq, weaponRangeSq))
                                    {
                                        // uint accumulation: attackers arrive in a worker-dependent order, and
                                        // integer addition is associative where float is not (§9).
                                        damage += damagePerHit;
                                        hits++;
                                    }
                                }

                                // (b) Pursuit — my own target is inside this same sweep, so its position is free.
                                if (other == myTarget)
                                {
                                    targetSeen = true;
                                    tx = gx[c];
                                    ty = gy[c];
                                    tz = gz[c];
                                }
                            }
                        }
                    }
                }

                uint health = vitals[i].Health;
                if (damage >= health)
                {
                    vitals[i].Health = 0u;
                    deaths.Add(cluster.GetEntityId(i));
                    continue;
                }

                vitals[i].Health = health - damage;

                if (targetSeen)
                {
                    SteerToward(ref motions[i], x, y, z, tx, ty, tz, turnDelta, cruiseSpeed);
                }
                else if (myTarget != TargetingComponent.Unlocked)
                {
                    // Dead or out of weapon range. Drop the lock; Acquire re-locks next tick.
                    // Deliberately NOT written to the lane: the lane is immutable for the whole Fire phase, which is
                    // what makes every read of it deterministic regardless of worker count.
                    targeting[i].TargetRawId = TargetingComponent.Unlocked;
                    locksLost++;
                }
            }
        }

        counters.Shots += shots;
        counters.Hits += hits;
        counters.LocksLost += locksLost;
        counters.Gathers += gathers;
    }

    /// <summary>
    /// Rotate the velocity toward the target by at most <paramref name="turnDelta"/> radians, preserving speed.
    /// Implemented as a clamped lerp toward the desired heading followed by a renormalise — cheaper than a true
    /// slerp and indistinguishable at these turn rates.
    /// </summary>
    private static void SteerToward(
        ref MotionComponent motion,
        float x, float y, float z,
        float targetX, float targetY, float targetZ,
        float turnDelta,
        float cruiseSpeed)
    {
        float dx = targetX - x;
        float dy = targetY - y;
        float dz = targetZ - z;
        float distSq = dx * dx + dy * dy + dz * dz;
        if (distSq < 1e-6f)
        {
            return;
        }

        float invDist = 1f / MathF.Sqrt(distSq);
        dx *= invDist;
        dy *= invDist;
        dz *= invDist;

        float speedSq = motion.X * motion.X + motion.Y * motion.Y + motion.Z * motion.Z;
        float invSpeed = speedSq > 1e-6f ? 1f / MathF.Sqrt(speedSq) : 0f;
        float hx = motion.X * invSpeed;
        float hy = motion.Y * invSpeed;
        float hz = motion.Z * invSpeed;

        // turnDelta is in radians; for the small per-tick angles here the chord length is within 1 % of the arc, so
        // using it directly as a lerp weight is accurate and avoids a trig call per ship per tick.
        float t = turnDelta > 1f ? 1f : turnDelta;
        float nx = hx + (dx - hx) * t;
        float ny = hy + (dy - hy) * t;
        float nz = hz + (dz - hz) * t;

        float nLenSq = nx * nx + ny * ny + nz * nz;
        if (nLenSq < 1e-6f)
        {
            return;
        }

        float invLen = cruiseSpeed / MathF.Sqrt(nLenSq);
        motion.X = nx * invLen;
        motion.Y = ny * invLen;
        motion.Z = nz * invLen;
    }
}
