using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace SwgTatooine;

/// <summary>
/// Combat, death and revival, run from the creature's side.
/// </summary>
/// <remarks>
/// <para><b>Two ways to run the same combat</b> (<see cref="SwgTatooine.CombatModel"/>). <b>Pull</b>, the default: every creature ready to take fire
/// asks which players are in range and applies its own damage. <b>Push</b>: every player asks which creatures are in range and pushes one event per
/// creature; a serial drain folds the events into a per-creature shooter count, and the creature side applies it. Push replays pull's range test exactly,
/// so both decide the same hits and differ only in what that costs.</para>
/// <para><b>Why neither has the player write the creature.</b> A player-side system that opens the creature through <c>ctx.Transaction</c> and writes its
/// vitals stalls the tick loop at tick 1 — engine bug #907, not a rule; writing another archetype's entity from a system is legal. Push routes around it
/// the way a parallel system has to anyway: a chunk must not write an entity another chunk may be writing, so the cross-entity half travels as
/// events.</para>
/// <para><b>Death is pooled, not structural.</b> A killed creature goes to <see cref="AiMode.Dead"/> and is revived at
/// its lair after the respawn interval rather than being destroyed and re-created. That is close to what SWG lairs did —
/// a lair owns a fixed set of spawn slots — but it does mean this workload exercises cluster-OCCUPANCY churn only
/// through the world build, not per tick, and the report says so. What it does exercise hard is migration: a revive is a
/// teleport from wherever the creature died back to its lair, which is the largest single position jump in the
/// simulation and forces both a cell change and a cluster-bound recomputation.</para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>
    /// [CORE3] Revival interval, in ticks. A wild lair respawns a killed mobile after
    /// <c>random(RESPAWN_TIME_MAX - RESPAWN_TIME_MIN) + RESPAWN_TIME_MIN</c>, which is a uniform 120-240 s
    /// (<c>LairObserver.idl</c>) — 1 200-2 400 ticks at 10 Hz. The midpoint is used.
    /// </summary>
    /// <remarks>
    /// The first pass estimated 30 s. Six times too fast, which mattered: it made the world churn far more than SWG's
    /// did. A destroy-mission lair, by contrast, never respawns at all — <c>checkRespawn</c> short-circuits for one.
    /// </remarks>
    private const int RespawnTicks = 1800;

    /// <summary>Shooters counted per creature before it stops asking: four players is as much fire as the damage model applies.</summary>
    private const int MaxShooters = 4;

    /// <summary>
    /// A creature in a player's line of fire takes damage; at zero health it dies; after the interval it revives at its lair.
    /// </summary>
    public void CreatureCombatTick(TickContext ctx)
    {
        long engaged = 0;
        long killed = 0;
        long revived = 0;
        long queries = 0;
        long candidates = 0;
        var minDelay = (int)(TatooineData.MinAttackDelaySec * _config.TickRateHz);
        var delaySpan = Math.Max(1, (int)((TatooineData.MaxAttackDelaySec - TatooineData.MinAttackDelaySec) * _config.TickRateHz));
        var batch = _config.CombatApi == CombatApi.Batch;

        // Push: the drain has already folded this tick's hit events into the lane; the resource edge orders this system after it.
        var push = _config.CombatModel == CombatModel.Push;
        var lane = push ? Volatile.Read(ref _shooterLane) : null;
        var stamp = LaneStamp(ctx.TickNumber);
        var verify = push && _config.CombatVerify;
        long verified = 0;
        long mismatches = 0;
        long pushHigher = 0;
        Span<BSphere2F> members = stackalloc BSphere2F[64];
        Span<int> slots = stackalloc int[64];
        Span<int> shooters = stackalloc int[64];

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Creature>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Creature>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits0 = cluster.OccupancyBits;
            if (bits0 == 0)
            {
                continue;
            }

            var places = cluster.GetReadOnlySpan(Creature.Bounds);
            var vitals = cluster.GetSpan(Creature.Vitals);
            var brains = cluster.GetSpan(Creature.Ai);
            var chunk = cluster.ChunkId;

            if (batch)
            {
                CombatBatch(
                    ctx.TickNumber,
                    chunk,
                    bits0,
                    places,
                    vitals,
                    brains,
                    members,
                    slots,
                    shooters,
                    minDelay,
                    delaySpan,
                    ref engaged,
                    ref killed,
                    ref revived,
                    ref queries,
                    ref candidates);
                continue;
            }

            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var ai = ref brains[idx];
                ref var v = ref vitals[idx];
                if (!ReadyToTakeFire(ref v, ref ai, ref revived))
                {
                    continue;
                }

                if (push)
                {
                    var pushed = ShootersAt(lane, (chunk << 6) | idx, stamp);
                    if (verify)
                    {
                        verified++;
                        var pulled = CountShooters(places[idx].X, places[idx].Z);
                        if (pulled != pushed)
                        {
                            mismatches++;
                            pushHigher += pushed > pulled ? 1 : 0;
                        }
                    }

                    TakeFire(ref v, ref ai, pushed, ctx.TickNumber, chunk, idx, minDelay, delaySpan, ref engaged, ref killed);
                    continue;
                }

                var sphere = new BSphere2F { CenterX = places[idx].X, CenterY = places[idx].Z, Radius = RangedRange };
                using var epoch = EpochGuard.Enter(Dbe.EpochManager);
                var e = Dbe.ClusterSpatialQuery<Player>().Radius(in sphere);
                var count = 0;
                queries++;
                try
                {
                    while (e.MoveNext())
                    {
                        count++;
                        if (count >= MaxShooters)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    e.Dispose();
                }

                candidates += count;
                TakeFire(ref v, ref ai, count, ctx.TickNumber, chunk, idx, minDelay, delaySpan, ref engaged, ref killed);
            }
        }

        if (engaged != 0)
        {
            Interlocked.Add(ref _playersEngaged, engaged);
        }

        if (killed != 0)
        {
            Interlocked.Add(ref _creaturesKilled, killed);
        }

        if (revived != 0)
        {
            Interlocked.Add(ref _creaturesRespawned, revived);
        }

        if (queries != 0 && InMeasuredWindow(ctx.TickNumber))
        {
            Interlocked.Add(ref _combatQueries, queries);
            Interlocked.Add(ref _combatCandidates, candidates);
        }

        if (verify && InMeasuredWindow(ctx.TickNumber))
        {
            Interlocked.Add(ref _combatVerified, verified);
            Interlocked.Add(ref _combatMismatches, mismatches);
            Interlocked.Add(ref _combatPushHigher, pushHigher);
        }
    }

    /// <summary><c>--combat-verify</c>: pull's own answer for a creature at (<paramref name="x"/>, <paramref name="z"/>), capped as pull caps it.</summary>
    private int CountShooters(float x, float z)
    {
        var sphere = new BSphere2F { CenterX = x, CenterY = z, Radius = RangedRange };
        using var epoch = EpochGuard.Enter(Dbe.EpochManager);
        var e = Dbe.ClusterSpatialQuery<Player>().Radius(in sphere);
        var count = 0;
        try
        {
            while (count < MaxShooters && e.MoveNext())
            {
                count++;
            }
        }
        finally
        {
            e.Dispose();
        }

        return count;
    }

    /// <summary>
    /// The part of a creature's turn that needs no query: a dead one counts down to its revival, and one whose attacker's weapon is still cycling waits.
    /// True when the creature can be fired on this tick.
    /// </summary>
    private static bool ReadyToTakeFire(ref CreatureVitals v, ref CreatureBrain ai, ref long revived)
    {
        if (ai.Mode == AiMode.Dead)
        {
            if (--ai.ThinkCooldown > 0)
            {
                return false;
            }

            // Revive at the lair with full health. The teleport is the point: it is the biggest position jump
            // this simulation makes, and it forces a cell change plus a cluster-bound recomputation.
            v.Health = v.MaxHealth;
            ai.Mode = AiMode.Wander;
            ai.ThinkCooldown = 1;
            revived++;
            return false;
        }

        // Only a creature that is already engaged, or one a player has walked up to, is under fire. The cooldown
        // is the weapon's, not the creature's: 1-3 s at 10 Hz is 10-30 ticks.
        if (v.AttackCooldown > 0)
        {
            v.AttackCooldown--;
            return false;
        }

        return true;
    }

    /// <summary>
    /// <see cref="CombatApi.Batch"/>: the cluster's creatures under fire as one batch. Each still asks exactly its own question, and still stops at four
    /// shooters.
    /// </summary>
    /// <remarks>Its own NoInlining method, like the awareness drains: <see cref="CreatureCombatTick"/> must JIT against an engine build that lacks the
    /// batch API, so a DLL-swap A/B can run the per-query arm.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CombatBatch(
        long tick,
        int chunk,
        ulong bits,
        ReadOnlySpan<CreaturePlacement> places,
        Span<CreatureVitals> vitals,
        Span<CreatureBrain> brains,
        Span<BSphere2F> members,
        Span<int> slots,
        Span<int> shooters,
        int minDelay,
        int delaySpan,
        ref long engaged,
        ref long killed,
        ref long revived,
        ref long queries,
        ref long candidates)
    {
        var m = 0;
        for (var b = bits; b != 0; b &= b - 1)
        {
            var idx = BitOperations.TrailingZeroCount(b);
            if (ReadyToTakeFire(ref vitals[idx], ref brains[idx], ref revived))
            {
                members[m] = new BSphere2F { CenterX = places[idx].X, CenterY = places[idx].Z, Radius = RangedRange };
                slots[m++] = idx;
            }
        }

        if (m == 0)
        {
            return;
        }

        shooters[..m].Clear();
        var sink = new ShooterSink(shooters);
        using (EpochGuard.Enter(Dbe.EpochManager))
        {
            Dbe.ClusterSpatialQuery<Player>().ForEachInRadius(members[..m], ref sink);
        }

        queries += m;
        for (var j = 0; j < m; j++)
        {
            var idx = slots[j];
            candidates += shooters[j];
            TakeFire(ref vitals[idx], ref brains[idx], shooters[j], tick, chunk, idx, minDelay, delaySpan, ref engaged, ref killed);
        }
    }

    // ── Push ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The resource the drain writes and the creature side reads — the declaration that orders the one after the other.</summary>
    public const string ShooterLaneResource = "CombatShooterLane";

    /// <summary>Push only: the hit events players publish, drained once a tick by <see cref="CombatDrainTick"/>. Set when the schedule is built.</summary>
    public EventQueue<CombatHit> CombatQueue { get; set; }

    public CombatModel CombatModel => _config.CombatModel;

    public bool CombatVerify => _config.CombatVerify;

    // Per creature, indexed by (clusterChunkId << 6) | slot: (tick stamp << 3) | shooters. Written only by the drain, read by the creature side after it.
    // A stamp from another tick reads as no shooters, so nothing is ever cleared, and a tick the runtime skips the drain for leaves nothing stale behind.
    private uint[] _shooterLane = [];
    private CombatHit[] _drainScratch = [];

    /// <summary>
    /// Push: every player queries the creatures in weapon range and publishes one event per creature that pull's own range test would have counted.
    /// </summary>
    public void PlayerFireTick(TickContext ctx)
    {
        long queries = 0;
        long candidates = 0;
        long dropped = 0;
        double range = RangedRange;
        var writer = ctx.Writer(CombatQueue);

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Player>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Player>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            var places = cluster.GetReadOnlySpan(Player.Bounds);
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref readonly var player = ref places[idx];
                var sphere = new BSphere2F { CenterX = player.X, CenterY = player.Z, Radius = RangedRange };
                using var epoch = EpochGuard.Enter(Dbe.EpochManager);
                var e = Dbe.ClusterSpatialQuery<Creature>().Radius(in sphere);
                queries++;
                try
                {
                    while (e.MoveNext())
                    {
                        candidates++;
                        var hit = e.Current;
                        if (CreatureSeesPlayer(in hit, in player.Bounds, range) && !writer.Push(new CombatHit((hit.ClusterChunkId << 6) | hit.SlotIndex)))
                        {
                            dropped++;
                        }
                    }
                }
                finally
                {
                    e.Dispose();
                }
            }
        }

        if (InMeasuredWindow(ctx.TickNumber))
        {
            Interlocked.Add(ref _combatQueries, queries);
            Interlocked.Add(ref _combatCandidates, candidates);
            Interlocked.Add(ref _combatDropped, dropped);
        }
    }

    /// <summary>Push: folds the tick's hit events into per-creature shooter counts, capped at <see cref="MaxShooters"/> as pull's query is.</summary>
    /// <remarks>The serial part of push. Its cost is one lane update per event — per player-creature pair in range, not per creature.</remarks>
    public void CombatDrainTick(TickContext ctx)
    {
        var queue = CombatQueue;
        var pending = queue.Count;
        if (pending == 0)
        {
            return;
        }

        if (_drainScratch.Length < pending)
        {
            _drainScratch = new CombatHit[(int)BitOperations.RoundUpToPowerOf2((uint)pending)];
        }

        var n = queue.Drain(_drainScratch);
        var stamp = LaneStamp(ctx.TickNumber);
        var lane = _shooterLane;
        for (var i = 0; i < n; i++)
        {
            var key = _drainScratch[i].Key;
            if ((uint)key >= (uint)lane.Length)
            {
                var grown = new uint[(int)BitOperations.RoundUpToPowerOf2((uint)key + 1u)];
                lane.CopyTo(grown, 0);
                lane = grown;
            }

            var entry = lane[key];
            if (entry >> 3 != stamp)
            {
                lane[key] = (stamp << 3) | 1u;
            }
            else if ((entry & 7u) < MaxShooters)
            {
                lane[key] = entry + 1u;
            }
        }

        // Read by the creature side on other threads once this system completes.
        Volatile.Write(ref _shooterLane, lane);
        if (InMeasuredWindow(ctx.TickNumber))
        {
            Interlocked.Add(ref _combatEvents, n);
        }
    }

    /// <summary>
    /// Pull's range test, replayed from the player's side: the creature's centre against the player's box, in the doubles the narrowphase compares.
    /// </summary>
    /// <remarks>
    /// The player-centred query that returned <paramref name="hit"/> finds a superset of what pull accepts: a creature's box (1.5 m half-extent) is wider
    /// than a player's (1 m), so the distance from a player's centre to it can only be shorter. This narrows it back to pull's answer, boundary included.
    /// </remarks>
    private static bool CreatureSeesPlayer(in ClusterSpatialQueryResult hit, in AABB2F player, double range)
    {
        // CreaturePlacement.X / Z, computed in float from the bounds the narrowphase widened exactly.
        double cx = ((float)hit.MinX + (float)hit.MaxX) * 0.5f;
        double cy = ((float)hit.MinY + (float)hit.MaxY) * 0.5f;
        if (player.MaxX < cx - range || player.MinX > cx + range || player.MaxY < cy - range || player.MinY > cy + range)
        {
            return false;
        }

        var dx = Math.Max(0d, Math.Max(player.MinX - cx, cx - player.MaxX));
        var dy = Math.Max(0d, Math.Max(player.MinY - cy, cy - player.MaxY));
        return (dx * dx) + (dy * dy) <= range * range;
    }

    private static uint LaneStamp(long tick) => ((uint)tick + 1u) & 0x1FFF_FFFFu;

    private static int ShootersAt(uint[] lane, int key, uint stamp)
    {
        if ((uint)key >= (uint)lane.Length)
        {
            return 0;
        }

        var entry = lane[key];
        return entry >> 3 == stamp ? (int)(entry & 7u) : 0;
    }

    private bool InMeasuredWindow(long tick) => tick >= _config.WarmTicks && tick < _config.WarmTicks + _config.MeasuredTicks;

    /// <summary>The damage from <paramref name="shooters"/> players in range, and what it does to the creature.</summary>
    private static void TakeFire(
        ref CreatureVitals v,
        ref CreatureBrain ai,
        int shooters,
        long tick,
        int chunk,
        int idx,
        int minDelay,
        int delaySpan,
        ref long engaged,
        ref long killed)
    {
        if (shooters == 0)
        {
            return;
        }

        engaged++;
        v.AttackCooldown = minDelay + (int)(Hash01(Salt(tick, chunk, idx, 0x27220A95u)) * delaySpan);
        v.Health -= PlayerDamagePerHit * shooters;
        if (v.Health > 0)
        {
            // Wounded and now angry: the creature turns on whoever is shooting, which is what pulls a lair.
            if (ai.Mode == AiMode.Wander)
            {
                ai.Mode = AiMode.Pursue;
                ai.ThinkCooldown = 0;
            }

            return;
        }

        v.Health = 0;
        ai.Mode = AiMode.Dead;
        ai.ThinkCooldown = RespawnTicks;
        killed++;
    }

    /// <summary><see cref="CombatApi.Batch"/>'s sink: counts each creature's shooters and retires it at <see cref="MaxShooters"/>.</summary>
    private ref struct ShooterSink : IRadiusBatchSink
    {
        private readonly Span<int> _shooters;

        public ShooterSink(Span<int> shooters) => _shooters = shooters;

        public bool Hit(int member, in ClusterSpatialQueryResult hit) => ++_shooters[member] < MaxShooters;
    }

    /// <summary>[EST] Damage one player lands per weapon cycle. Kills a womp rat in two hits and a bantha in ten.</summary>
    private const int PlayerDamagePerHit = 95;
}

/// <summary>Push only: a player has a creature in weapon range. <see cref="Key"/> is the creature's <c>(clusterChunkId &lt;&lt; 6) | slot</c>.</summary>
public readonly record struct CombatHit(int Key);
