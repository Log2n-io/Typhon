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

            // READ-ONLY, and taken mutably only on the tick a creature is actually hit or revived. Vitals and Ai are both projected, and GetSpan marks
            // its cluster changed on the HANDOUT: taking them mutably every tick to read a health or a mode claimed a change for every creature on every
            // tick, which is what kept the projection re-encoding every watched creature whether or not anything about it had moved. The weapon's
            // cooldown, which does change every tick, lives in the unprojected timers for the same reason.
            var vitals = cluster.GetReadOnlySpan(Creature.Vitals);
            var brains = cluster.GetReadOnlySpan(Creature.Ai);
            Span<CreatureVitals> vitalsRw = default;
            Span<CreatureBrain> brainsRw = default;
            var timers = cluster.GetSpan(Creature.Timers);
            var chunk = cluster.ChunkId;

            if (batch)
            {
                CombatBatch(
                    ctx.TickNumber,
                    in cluster,
                    chunk,
                    bits0,
                    places,
                    vitals,
                    brains,
                    timers,
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

                ref var t = ref timers[idx];
                if (!ReadyToTakeFire(in cluster, vitals, brains, ref vitalsRw, ref brainsRw, ref t, idx, ref revived))
                {
                    continue;
                }

                var sphere = new BSphere2F { CenterX = places[idx].X, CenterY = places[idx].Z, Radius = RangedRange };
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

                TakeFire(in cluster, vitals, brains, ref vitalsRw, ref brainsRw, ref t, count, ctx.TickNumber, chunk, idx, minDelay, delaySpan, ref engaged,
                    ref killed);
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
    private static bool ReadyToTakeFire(
        in ClusterRef<Creature> cluster,
        ReadOnlySpan<CreatureVitals> vitals,
        ReadOnlySpan<CreatureBrain> brains,
        ref Span<CreatureVitals> vitalsRw,
        ref Span<CreatureBrain> brainsRw,
        ref CreatureTimers t,
        int idx,
        ref long revived)
    {
        if (brains[idx].Mode == AiMode.Dead)
        {
            if (--t.ThinkCooldown > 0)
            {
                return false;
            }

            // Revive at the lair with full health. The teleport is the point: it is the biggest position jump
            // this simulation makes, and it forces a cell change plus a cluster-bound recomputation.
            VitalsRw(in cluster, ref vitalsRw)[idx].Health = vitals[idx].MaxHealth;
            BrainsRw(in cluster, ref brainsRw)[idx].Mode = AiMode.Wander;
            SwgTatooine.Replication.TatooineReplication.Replicate(in cluster, idx);
            t.ThinkCooldown = 1;

            // Cleared, or a creature revived part-way through an old rest keeps standing until a schedule from its previous life runs out. Zero is in the
            // past for every tick, so the next decision picks a fresh leg.
            t.MoveUntilTick = 0;
            t.RestUntilTick = 0;
            revived++;
            return false;
        }

        // Only a creature that is already engaged, or one a player has walked up to, is under fire. The cooldown
        // is the weapon's, not the creature's: 1-3 s at 10 Hz is 10-30 ticks.
        if (t.AttackCooldown > 0)
        {
            t.AttackCooldown--;
            return false;
        }

        return true;
    }

    /// <summary>The cluster's mutable vitals, handed out on the first write of this cluster's walk and not before. See <see cref="CreatureCombatTick"/>.</summary>
    private static Span<CreatureVitals> VitalsRw(in ClusterRef<Creature> cluster, ref Span<CreatureVitals> rw)
    {
        if (rw.IsEmpty)
        {
            rw = cluster.GetSpan(Creature.Vitals);
        }

        return rw;
    }

    /// <summary>The cluster's mutable brains, handed out on the first write of this cluster's walk and not before.</summary>
    private static Span<CreatureBrain> BrainsRw(in ClusterRef<Creature> cluster, ref Span<CreatureBrain> rw)
    {
        if (rw.IsEmpty)
        {
            rw = cluster.GetSpan(Creature.Ai);
        }

        return rw;
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
        in ClusterRef<Creature> cluster,
        int chunk,
        ulong bits,
        ReadOnlySpan<CreaturePlacement> places,
        ReadOnlySpan<CreatureVitals> vitals,
        ReadOnlySpan<CreatureBrain> brains,
        Span<CreatureTimers> timers,
        Span<BSphere2F> members,
        Span<int> slots,
        Span<int> shooters,
        int minDelay,
        int delaySpan,
        ref long engaged,
        ref long killed,
        ref long revived)
    {
        Span<CreatureVitals> vitalsRw = default;
        Span<CreatureBrain> brainsRw = default;
        var m = 0;
        for (var b = bits; b != 0; b &= b - 1)
        {
            var idx = BitOperations.TrailingZeroCount(b);
            if (ReadyToTakeFire(in cluster, vitals, brains, ref vitalsRw, ref brainsRw, ref timers[idx], idx, ref revived))
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
        Dbe.ClusterSpatialQuery<Player>().ForEachInRadius(members[..m], ref sink);

        for (var j = 0; j < m; j++)
        {
            var idx = slots[j];
            TakeFire(in cluster, vitals, brains, ref vitalsRw, ref brainsRw, ref timers[idx], shooters[j], tick, chunk, idx, minDelay, delaySpan, ref engaged,
                ref killed);
        }
    }

    /// <summary>The damage from <paramref name="shooters"/> players in range, and what it does to the creature.</summary>
    private static void TakeFire(
        in ClusterRef<Creature> cluster,
        ReadOnlySpan<CreatureVitals> vitals,
        ReadOnlySpan<CreatureBrain> brains,
        ref Span<CreatureVitals> vitalsRw,
        ref Span<CreatureBrain> brainsRw,
        ref CreatureTimers t,
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
        t.AttackCooldown = minDelay + (int)(Hash01(Salt(tick, chunk, idx, 0x27220A95u)) * delaySpan);
        var health = vitals[idx].Health - (PlayerDamagePerHit * shooters);
        if (health > 0)
        {
            VitalsRw(in cluster, ref vitalsRw)[idx].Health = health;
            SwgTatooine.Replication.TatooineReplication.Replicate(in cluster, idx);

            // Wounded and now angry: the creature turns on whoever is shooting, which is what pulls a lair.
            if (brains[idx].Mode == AiMode.Wander)
            {
                BrainsRw(in cluster, ref brainsRw)[idx].Mode = AiMode.Pursue;
                t.ThinkCooldown = 0;
            }

            return;
        }

        VitalsRw(in cluster, ref vitalsRw)[idx].Health = 0;
        BrainsRw(in cluster, ref brainsRw)[idx].Mode = AiMode.Dead;
        SwgTatooine.Replication.TatooineReplication.Replicate(in cluster, idx);
        t.ThinkCooldown = RespawnTicks;
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
