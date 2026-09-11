using System;
using System.Numerics;
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

            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var ai = ref brains[idx];
                ref var v = ref vitals[idx];

                if (ai.Mode == AiMode.Dead)
                {
                    if (--ai.ThinkCooldown > 0)
                    {
                        continue;
                    }

                    // Revive at the lair with full health. The teleport is the point: it is the biggest position jump
                    // this simulation makes, and it forces a cell change plus a cluster-bound recomputation.
                    v.Health = v.MaxHealth;
                    ai.Mode = AiMode.Wander;
                    ai.ThinkCooldown = 1;
                    revived++;
                    continue;
                }

                // Only a creature that is already engaged, or one a player has walked up to, is under fire. The cooldown
                // is the weapon's, not the creature's: 1-3 s at 10 Hz is 10-30 ticks.
                if (v.AttackCooldown > 0)
                {
                    v.AttackCooldown--;
                    continue;
                }

                var x = places[idx].X;
                var z = places[idx].Z;
                var sphere = new BSphere2F { CenterX = x, CenterY = z, Radius = RangedRange };
                using var epoch = EpochGuard.Enter(Dbe.EpochManager);
                var e = Dbe.ClusterSpatialQuery<Player>().Radius(in sphere);
                var shooters = 0;
                try
                {
                    while (e.MoveNext())
                    {
                        shooters++;
                        if (shooters >= 4)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    e.Dispose();
                }

                if (shooters == 0)
                {
                    continue;
                }

                engaged++;
                v.AttackCooldown = minDelay + (int)(Hash01(Salt(ctx.TickNumber, chunk, idx, 0x27220A95u)) * delaySpan);
                v.Health -= PlayerDamagePerHit * shooters;
                if (v.Health > 0)
                {
                    // Wounded and now angry: the creature turns on whoever is shooting, which is what pulls a lair.
                    if (ai.Mode == AiMode.Wander)
                    {
                        ai.Mode = AiMode.Pursue;
                        ai.ThinkCooldown = 0;
                    }

                    continue;
                }

                v.Health = 0;
                ai.Mode = AiMode.Dead;
                ai.ThinkCooldown = RespawnTicks;
                killed++;
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

    /// <summary>[EST] Damage one player lands per weapon cycle. Kills a womp rat in two hits and a bantha in ten.</summary>
    private const int PlayerDamagePerHit = 95;
}
