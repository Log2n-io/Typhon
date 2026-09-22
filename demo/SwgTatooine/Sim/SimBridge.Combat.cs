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
/// <para><b>Why the creature applies the damage to itself rather than the player applying it to the creature.</b> A
/// system may only write components of its own input archetype. Reaching across — opening a Creature from a system whose
/// input is Player, through <c>ctx.Transaction</c>, and writing its vitals — stalls the tick loop outright: the runtime
/// reached tick 1 and never advanced again, with Spawn and Destroy both disabled, so it is the cross-archetype
/// open-and-write itself. That is filed against the engine. The model here is inverted instead, and the inversion is not
/// a distortion of the workload: the spatial query, the range test, the weapon cadence and the health arithmetic are
/// identical, and only which side of the exchange executes them has moved.</para>
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
        var minDelay = (int)(TatooineData.MinAttackDelaySec * _config.TickRateHz);
        var delaySpan = Math.Max(1, (int)((TatooineData.MaxAttackDelaySec - TatooineData.MinAttackDelaySec) * _config.TickRateHz));
        var batch = _config.CombatApi == CombatApi.Batch;
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
                    ref revived);
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

                var sphere = new BSphere2F { CenterX = places[idx].X, CenterY = places[idx].Z, Radius = RangedRange };
                using var epoch = EpochGuard.Enter(Dbe.EpochManager);
                var e = Dbe.ClusterSpatialQuery<Player>().Radius(in sphere);
                var count = 0;
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
        ref long revived)
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

        for (var j = 0; j < m; j++)
        {
            var idx = slots[j];
            TakeFire(ref vitals[idx], ref brains[idx], shooters[j], tick, chunk, idx, minDelay, delaySpan, ref engaged, ref killed);
        }
    }

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
