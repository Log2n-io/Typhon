using System;
using System.Numerics;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Combat, death and revival: who fires at whom, and what the hit does.
/// </summary>
/// <remarks>
/// <para><b>SWG-02 turned this round.</b> Until then combat ran from the creature's side — every living creature asked
/// "is a player within 75 m?" and took damage from however many it found, whatever those players were doing and whether
/// or not they had chosen to fight it. That is gap G8, and it was not a modelling shortcut: it was forced, because the
/// only side that could safely write a creature's vitals was the creature's own system. Nothing anywhere stored who was
/// fighting whom (G7), no creature ever pursued a particular player (G6), and a player could not be hurt at all (G4).</para>
/// <para><b>What replaces it is three systems and one queue.</b> Two parallel producers decide, each walking its own
/// archetype and writing nothing outside it: <see cref="PlayerCombatTick"/> for the players that are fighting and
/// <see cref="CreatureCombatTick"/> for the creatures that are. Each pushes a <see cref="CombatEvent"/> naming the target
/// and the amount. One serial consumer, <see cref="CombatResolveTick"/>, drains the queue and opens each target through
/// the tick's own transaction. That is <c>06-gameplay.md § 2</c> principle 3, and it is what makes the cross-archetype
/// write single-threaded — the shape #907 is still open about — while leaving the expensive half (the spatial query, the
/// range test, the weapon cadence) parallel and next to the entities it reads.</para>
/// <para><b>The workload changed with it, and in the cheaper direction.</b> The old model ran one radius query per living
/// creature per weapon cycle: at the faithful baseline that is on the order of 850 queries a tick. The new one runs a
/// query only when a player that is FIGHTING needs a target it does not have, which is a couple per tick, and a creature
/// that is already fighting runs none at all — it knows what it is fighting and reads its position. The negative-case
/// query load the study is about is unaffected, because that lives in <c>CreatureThink</c>'s 24 m aggro bubble, which is
/// untouched.</para>
/// <para><b>Death is still pooled, not structural.</b> A killed creature goes to <see cref="AiMode.Dead"/> and is revived
/// at its lair after the respawn interval rather than destroyed and re-created (#907 again). So this workload exercises
/// cluster-OCCUPANCY churn only through the world build. What it does exercise hard is migration: a revive is a teleport
/// from wherever the creature died back to its lair, the largest single position jump in the simulation, and it forces
/// both a cell change and a cluster-bound recomputation.</para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>The queue every effect on somebody else's entity travels through. Created by the run and bound to the schedule; see <c>TatooineSim.BuildSchedule</c>.</summary>
    /// <remarks>
    /// Sized for the whole tick rather than for one worker: the engine splits the request across per-worker segments and grows a skewed one on demand, so
    /// 1 024 is a per-tick budget and not a cap. At the faithful baseline a tick produces single digits of events; at x16 with the population fighting it is
    /// tens. The figure is deliberately an order of magnitude above the worst case observed, because an overflow is a DROPPED effect and the only evidence
    /// would be <c>EventQueueBase.OverflowCount</c>, which the run asserts is zero.
    /// </remarks>
    internal EventQueue<CombatEvent> CombatQueue { get; set; }

    /// <summary>
    /// A fighting player fires at its target: <see cref="SimPhases.Resolve"/>, over <see cref="Player"/>, in parallel.
    /// </summary>
    /// <remarks>
    /// <para>Nothing here writes another archetype. It writes the shooter's own weapon cooldown and the shooter's own
    /// target, and pushes one event per shot.</para>
    /// <para><b>Both refusals are counted, which is the whole of AC-4.</b> "Only a player in Combat shoots, and only at its
    /// own target" is a claim about what does NOT happen, so the check is a counter of the players that were skipped and
    /// the shots that were refused for range — a silent zero would be indistinguishable from a system that never ran.</para>
    /// </remarks>
    public void PlayerCombatTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var writer = ctx.Writer(CombatQueue);
        var minDelay = (int)(TatooineData.MinAttackDelaySec * _config.TickRateHz);
        var delaySpan = Math.Max(1, (int)((TatooineData.MaxAttackDelaySec - TatooineData.MinAttackDelaySec) * _config.TickRateHz));
        var rangeSq = RangedRange * RangedRange;
        var regenEvery = Math.Max(1, _config.TickRateHz);
        var regenQuiet = (long)MathF.Round(TatooineData.RegenQuietSec * _config.TickRateHz);
        long shots = 0;
        long idle = 0;
        long outOfRange = 0;
        long noTarget = 0;
        long shielded = 0;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Player>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Player>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits0 = cluster.OccupancyBits;
            if (bits0 == 0)
            {
                continue;
            }

            var places = cluster.GetReadOnlySpan(Player.Bounds);
            var states = cluster.GetReadOnlySpan(Player.State);
            var controls = cluster.GetReadOnlySpan(Player.Control);

            // READ-ONLY until a player in this cluster actually fires. GetSpan marks its cluster changed on the HANDOUT, so taking these mutably to read a
            // cooldown would claim a change for every player on every tick — and a cluster of parked players in a cantina writes nothing today, which is
            // precisely the property the activity mix exists to produce.
            var vitals = cluster.GetReadOnlySpan(Player.Vitals);
            var sessions = cluster.GetReadOnlySpan(Player.Session);
            Span<PlayerVitals> vitalsRw = default;
            Span<PlayerSession> sessionsRw = default;

            // The players whose health this pass gave back, replicated once per cluster (ADR-067).
            var regenSlots = 0UL;
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                // Down: knocked out or dead. It does nothing but wait, and asks once to be revived when its time is up; the drain owns the clone's teleport.
                if (vitals[idx].Down != Downed.Up)
                {
                    var upTick = vitals[idx].UpTick;
                    if (upTick != 0 && ctx.TickNumber >= upTick)
                    {
                        writer.Push(new CombatEvent
                        {
                            Target = cluster.GetEntityId(idx),
                            Kind = CombatEventKind.Revive,
                            TargetKind = CombatTargetKind.Player,
                        });
                        PlayerVitalsRw(in cluster, ref vitalsRw)[idx].UpTick = 0;
                    }

                    continue;
                }

                // AC-4's first half. A player travelling, idling in a cantina, roaming or waiting for a shuttle does not shoot, and is counted rather than
                // silently skipped.
                //
                // <b>A POSSESSED player fights what it targeted, whatever its activity.</b> Its activity is the server's description of a body the person is
                // steering — a step makes it Travelling, a stop Idle — and `Combat` is only ever set for the scripted crowd (PlayerThink, a mission). Gating a
                // human on it meant a click on a creature stored a target that no shot was ever fired at. It does not acquire one of its own, either: a
                // person picks what they fight.
                // A building or an NPC it selected is a target it holds and never fires at (CombatTargetKind.Unshootable). And it fights only in a stance
                // (PlayerState.Stance, the client's action bar 2–4): a target with no stance is a selection, not an attack.
                var possessed = controls[idx].Kind != ControllerKind.Scripted;
                var stance = states[idx].Stance;
                if (possessed
                        ? sessions[idx].Target.IsNull || sessions[idx].TargetKind == CombatTargetKind.Unshootable || stance <= Stances.None
                            || stance >= Stances.Count
                        : states[idx].Activity != PlayerActivity.Combat)
                {
                    // Out of a fight — no stance, or the crowd not in Combat — a player heals, once a second on its own beat. A person's player holding a stance
                    // with nothing targeted is still in one.
                    var max = vitals[idx].MaxHealth;
                    if ((!possessed || stance <= Stances.None) && vitals[idx].Health > 0 && vitals[idx].Health < max
                        && (vitals[idx].LastHitTick == 0 || ctx.TickNumber - vitals[idx].LastHitTick >= regenQuiet)
                        && (ctx.TickNumber + cluster.GetEntityId(idx).EntityKey) % regenEvery == 0)
                    {
                        ref var healed = ref PlayerVitalsRw(in cluster, ref vitalsRw)[idx];
                        healed.Health = Math.Min(max, healed.Health + Math.Max(1, (int)(max * TatooineData.RegenPerSecFraction)));
                        regenSlots |= 1UL << idx;
                    }

                    idle++;
                    continue;
                }

                // Down and waiting for the clone: see CombatResolveTick. Its health is restored by the teleport, not here.
                if (vitals[idx].Health <= 0)
                {
                    continue;
                }

                if (vitals[idx].AttackCooldown > 0)
                {
                    PlayerVitalsRw(in cluster, ref vitalsRw)[idx].AttackCooldown--;
                    continue;
                }

                var x = places[idx].X;
                var z = places[idx].Z;
                var target = sessions[idx].Target;
                var targetKind = sessions[idx].TargetKind;

                if (!target.IsNull)
                {
                    // A stance reaches between the two bodies' edges; the crowd's weapon reaches between centres, as it always has.
                    var verdict = possessed
                        ? CheckTarget(ctx, target, targetKind, x, z, 0f, TatooineData.StanceReachM[stance] + places[idx].HalfExtent)
                        : CheckTarget(ctx, target, targetKind, x, z, rangeSq, -1f);
                    if (verdict == TargetVerdict.Gone)
                    {
                        // Dead or destroyed — a broken lair, a mission ended. Nobody's choice survives that, so the target is dropped for a possessed
                        // player too and the client is free to name another.
                        PlayerSessionsRw(in cluster, ref sessionsRw)[idx].Target = EntityId.Null;
                        target = EntityId.Null;
                        if (possessed)
                        {
                            continue;
                        }
                    }
                    else if (verdict == TargetVerdict.Shielded)
                    {
                        // Its occupants first. A person keeps the lair targeted and fires once they are down; the crowd turns to them now.
                        shielded++;
                        if (!possessed)
                        {
                            PlayerSessionsRw(in cluster, ref sessionsRw)[idx].Target = EntityId.Null;
                        }

                        continue;
                    }
                    else if (verdict == TargetVerdict.OutOfRange)
                    {
                        // AC-4's second half: no damage, counted. A POSSESSED player keeps the target — the server does not unilaterally drop what a human
                        // chose just because they backed away — while the simulation's own player drops it, or it would stand next to a lair for ever
                        // holding a target it walked away from.
                        outOfRange++;
                        if (!possessed)
                        {
                            PlayerSessionsRw(in cluster, ref sessionsRw)[idx].Target = EntityId.Null;
                        }

                        continue;
                    }
                }

                if (target.IsNull)
                {
                    // The only spatial query on the player's side of a fight, and it runs only for a fighting player that has nobody to shoot.
                    if (!TryAcquireTarget(ctx, cluster.Realm, x, z, out target, out targetKind))
                    {
                        // A SCAN CADENCE, not a wasted tick. A player walking the one to two kilometres to its mission is in Combat the whole way with
                        // nothing in range, and without this it ran two radius queries every tick of that walk: measured at 12 500 query pairs over a
                        // 600-tick run with 64 players, for nine shots. Re-arming the weapon puts the scan on the weapon's own cadence — about once a
                        // second — which is also what a player does rather than sweeping the horizon ten times a second.
                        PlayerVitalsRw(in cluster, ref vitalsRw)[idx].AttackCooldown = minDelay;
                        noTarget++;
                        continue;
                    }

                    ref var owner = ref PlayerSessionsRw(in cluster, ref sessionsRw)[idx];
                    owner.Target = target;
                    owner.TargetKind = targetKind;
                }

                writer.Push(new CombatEvent
                {
                    Target = target,
                    Attacker = cluster.GetEntityId(idx),
                    Amount = vitals[idx].AttackDamage,
                    Kind = CombatEventKind.Damage,
                    TargetKind = targetKind,
                });

                // A stance strikes on its own steady beat, which a client plays one attack clip per hit to; the crowd keeps its random weapon delay.
                var key = cluster.GetEntityId(idx).EntityKey;
                PlayerVitalsRw(in cluster, ref vitalsRw)[idx].AttackCooldown = possessed
                    ? Math.Max(1, (int)MathF.Round(TatooineData.StanceDelaySec[stance] * _config.TickRateHz))
                    : minDelay + (int)(Hash01(Salt(ctx.TickNumber, key, 0x27220A95u)) * delaySpan);
                shots++;
            }

            if (regenSlots != 0)
            {
                TatooineReplication.Replicate(in cluster, regenSlots);
            }
        }

        if (shots != 0)
        {
            Interlocked.Add(ref _playerShots, shots);
        }

        if (idle != 0)
        {
            Interlocked.Add(ref _shotsSkippedNotFighting, idle);
        }

        if (outOfRange != 0)
        {
            Interlocked.Add(ref _shotsRefusedRange, outOfRange);
        }

        if (noTarget != 0)
        {
            Interlocked.Add(ref _shotsWithoutTarget, noTarget);
        }

        if (shielded != 0)
        {
            Interlocked.Add(ref _shotsAtShieldedLair, shielded);
        }
    }

    /// <summary>What a stored target is worth this tick.</summary>
    private enum TargetVerdict
    {
        /// <summary>Alive and within weapon range: fire.</summary>
        InRange,

        /// <summary>Alive but too far away.</summary>
        OutOfRange,

        /// <summary>Destroyed or dead — not a target any more, whoever chose it.</summary>
        Gone,

        /// <summary>A lair whose occupants still stand: there, and in reach or not, but nothing hits it until they are dead.</summary>
        Shielded,
    }

    /// <summary>Is a stored target still worth shooting at? One entity read, and no spatial query.</summary>
    /// <param name="ctx">The tick.</param>
    /// <param name="target">The stored target.</param>
    /// <param name="kind">What it was classified as when it was stored.</param>
    /// <param name="x">The shooter's X.</param>
    /// <param name="z">The shooter's Z.</param>
    /// <param name="rangeSq">The weapon's reach squared, between centres; read when <paramref name="edgeReach"/> is negative.</param>
    /// <param name="edgeReach">
    /// A stance's reach plus the shooter's own half extent: in range when the centre distance minus the target's half extent is within it — the gap
    /// between the two bodies' edges. Negative for a centre-to-centre reach.
    /// </param>
    /// <remarks>
    /// Read-only throughout: a rejected target must not dirty the page it lives on, which is why this takes
    /// <c>TryOpen</c> rather than <c>TryOpenMut</c> even though the caller may be about to damage what it finds. The
    /// damage itself happens in the consumer, on one thread, through the tick transaction.
    /// </remarks>
    private static TargetVerdict CheckTarget(TickContext ctx, EntityId target, byte kind, float x, float z, float rangeSq, float edgeReach)
    {
        if (!ctx.Accessor.TryOpen(target, out var entity))
        {
            return TargetVerdict.Gone;
        }

        float tx, tz, half;
        switch (kind)
        {
            case CombatTargetKind.Creature:
            {
                if (entity.Read(Creature.Vitals).Health <= 0)
                {
                    return TargetVerdict.Gone;
                }

                var p = entity.Read(Creature.Bounds);
                tx = p.X;
                tz = p.Z;
                half = p.HalfExtent;
                break;
            }

            case CombatTargetKind.Lair:
            {
                var spawner = entity.Read(CreatureLair.Spawner);
                if (!IsStandingLair(spawner, entity.Read(CreatureLair.Vitals)))
                {
                    return TargetVerdict.Gone;
                }

                if (IsShielded(in spawner))
                {
                    return TargetVerdict.Shielded;
                }

                var p = entity.Read(CreatureLair.Bounds);
                tx = p.X;
                tz = p.Z;
                half = p.HalfExtent;
                break;
            }

            case CombatTargetKind.Player:
            {
                if (entity.Read(Player.Vitals).Health <= 0)
                {
                    return TargetVerdict.Gone;
                }

                var p = entity.Read(Player.Bounds);
                tx = p.X;
                tz = p.Z;
                half = p.HalfExtent;
                break;
            }

            default:
                // Nothing it could damage. Gated out before this for a session's player; Gone rather than a read of components it does not have.
                return TargetVerdict.Gone;
        }

        var dx = tx - x;
        var dz = tz - z;
        var d2 = (dx * dx) + (dz * dz);
        if (edgeReach >= 0f)
        {
            var reach = edgeReach + half;
            return d2 <= reach * reach ? TargetVerdict.InRange : TargetVerdict.OutOfRange;
        }

        return d2 <= rangeSq ? TargetVerdict.InRange : TargetVerdict.OutOfRange;
    }

    /// <summary>
    /// The nearest LIVING creature within weapon range, or failing that a standing lair — what a player in
    /// <see cref="PlayerActivity.Combat"/> shoots at when it has nobody.
    /// </summary>
    /// <remarks>
    /// <para><b>Creatures before the lair, which is Core3's order by accident rather than by design.</b> A mission's
    /// lair arrives with defenders around it, so a player that prefers whatever is closest clears the defenders and then
    /// breaks the lair — which is what the mission felt like — without anything having to sequence it.</para>
    /// <para><b>The aliveness test is per candidate and it is not optional.</b> Taking the nearest hit and letting the
    /// consumer discard damage to a corpse deadlocks the player: a dead creature stays in the index for its whole respawn
    /// interval, so the nearest hit is the same corpse on the next cycle and the next, and the player never fires at
    /// anything. A few dozen component reads per shot, a couple of shots per tick, is not a cost worth that bug.</para>
    /// <para><b>Any standing lair is a target</b> (<see cref="IsStandingLair"/>). A wild one used not to be, because nothing
    /// repaired it and a lair at zero would have stood broken for the life of the process. Lairs are transient now: one at
    /// zero is retired by the lair director and replaced elsewhere (<c>SimBridge.Lairs.cs</c>).</para>
    /// </remarks>
    private bool TryAcquireTarget(TickContext ctx, RealmId realm, float x, float z, out EntityId target, out byte kind)
    {
        var sphere = new BSphere2F { CenterX = x, CenterY = z, Radius = RangedRange };
        target = EntityId.Null;
        kind = CombatTargetKind.Creature;

        var bestSq = double.MaxValue;
        var e = Dbe.ClusterSpatialQuery<Creature>(realm).Radius(in sphere);
        try
        {
            while (e.MoveNext())
            {
                var hit = e.Current;
                if (hit.DistanceSq >= bestSq || !ctx.Accessor.TryOpen(hit.Entity, out var candidate)
                    || candidate.Read(Creature.Vitals).Health <= 0)
                {
                    continue;
                }

                bestSq = hit.DistanceSq;
                target = hit.Entity;
            }
        }
        finally
        {
            e.Dispose();
        }

        if (!target.IsNull)
        {
            return true;
        }

        var lairs = Dbe.ClusterSpatialQuery<CreatureLair>(realm).Radius(in sphere);
        try
        {
            while (lairs.MoveNext())
            {
                var hit = lairs.Current;
                if (hit.DistanceSq >= bestSq || !ctx.Accessor.TryOpen(hit.Entity, out var candidate)
                    || !IsStandingLair(candidate.Read(CreatureLair.Spawner), candidate.Read(CreatureLair.Vitals))
                    || IsShielded(candidate.Read(CreatureLair.Spawner)))
                {
                    continue;
                }

                bestSq = hit.DistanceSq;
                target = hit.Entity;
                kind = CombatTargetKind.Lair;
            }
        }
        finally
        {
            lairs.Dispose();
        }

        return !target.IsNull;
    }

    /// <summary>
    /// A creature's turn: a dead one counts down to its revival, and a fighting one attacks the player it is fighting.
    /// <see cref="SimPhases.Resolve"/>, over <see cref="Creature"/>, in parallel.
    /// </summary>
    /// <remarks>
    /// No spatial query at all, which is the arithmetic that made SWG-02 cheaper rather than dearer. A creature that is
    /// fighting knows what it is fighting — <see cref="CreatureBrain.Target"/> — and reads that entity's position; one
    /// that is not fighting does nothing here. Finding a player to fight in the first place is <c>CreatureThink</c>'s 24 m
    /// aggro bubble, unchanged.
    /// </remarks>
    public void CreatureCombatTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var writer = ctx.Writer(CombatQueue);
        long attacks = 0;
        long lostTarget = 0;
        var minDelay = (int)(TatooineData.MinAttackDelaySec * _config.TickRateHz);
        var delaySpan = Math.Max(1, (int)((TatooineData.MaxAttackDelaySec - TatooineData.MinAttackDelaySec) * _config.TickRateHz));

        // The creature's own reach, with the same hysteresis band CreatureThink stops closing at, so a creature that has decided it is in range agrees with
        // the system that decided it. At equal thresholds the two disagree on the boundary and a creature alternates between swinging and shuffling.
        var reach = _creatureAttackRange * AttackRangeHysteresis;
        var reachSq = reach * reach;

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

            // READ-ONLY, taken mutably only on the tick a creature is actually revived. Vitals and Ai are both projected, and GetSpan marks its cluster
            // changed on the HANDOUT: taking them mutably every tick to read a health or a mode claimed a change for every creature on every tick, which is
            // what kept the projection re-encoding every watched creature whether or not anything about it had moved. The weapon's cooldown, which does
            // change every tick of a fight, lives in the unprojected timers for the same reason.
            var vitals = cluster.GetReadOnlySpan(Creature.Vitals);
            var brains = cluster.GetReadOnlySpan(Creature.Ai);
            Span<CreatureBrain> brainsRw = default;
            var timers = cluster.GetSpan(Creature.Timers);

            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var t = ref timers[idx];

                // Dead creatures are CreatureThink's: it owns the respawn countdown and the revival, because both are mode transitions on the AI cadence and
                // because this phase must not write CreatureVitals — the player's side of a fight reads them here.
                if (brains[idx].Mode == AiMode.Dead)
                {
                    continue;
                }

                if (t.AttackCooldown > 0)
                {
                    t.AttackCooldown--;
                    continue;
                }

                // Only a creature that has closed to its reach and stopped is swinging. Pursue is still walking, and every other mode is not in a fight.
                if (brains[idx].Mode != AiMode.Fighting)
                {
                    continue;
                }

                var target = brains[idx].Target;
                if (target.IsNull || !ctx.Accessor.TryOpen(target, out var player))
                {
                    // The target has gone. Dropping straight back to Wander here rather than waiting for the next decision matters:
                    // Fighting stands still, so a creature left Fighting at a target that no longer exists would stand there until its think cooldown
                    // expired, which at 400-1000 ms is several ticks of a creature frozen mid-swing at nothing.
                    SetCreatureMode(in cluster, ref brainsRw, idx, AiMode.Wander);
                    ClearTarget(in cluster, ref brainsRw, idx);
                    t.ThinkCooldown = 0;
                    lostTarget++;
                    continue;
                }

                var p = player.Read(Player.Bounds);
                var dx = p.X - places[idx].X;
                var dz = p.Z - places[idx].Z;
                if ((dx * dx) + (dz * dz) > reachSq)
                {
                    // Walked out of reach between two decisions. CreatureThink re-closes on its own cadence; nothing is written here, and in particular the
                    // weapon is NOT re-armed, so a creature cannot bank cooldown by being out of range.
                    continue;
                }

                writer.Push(new CombatEvent
                {
                    Target = target,
                    Attacker = cluster.GetEntityId(idx),
                    Amount = vitals[idx].AttackDamage,
                    Kind = CombatEventKind.Damage,
                    TargetKind = CombatTargetKind.Player,
                });

                t.AttackCooldown = minDelay + (int)(Hash01(Salt(ctx.TickNumber, cluster.GetEntityId(idx).EntityKey, 0x1E35A7BDu)) * delaySpan);
                attacks++;
            }
        }

        if (attacks != 0)
        {
            Interlocked.Add(ref _creatureAttacks, attacks);
        }

        if (lostTarget != 0)
        {
            Interlocked.Add(ref _creaturesLostTarget, lostTarget);
        }
    }

    /// <summary>
    /// Every effect this tick produced, applied to the entity it names: <see cref="SimPhases.Resolve"/>, serial, after both producers.
    /// </summary>
    /// <remarks>
    /// <para><b>Serial because the queue is single-consumer</b> (<c>EventQueue.Drain</c>), and that is a good bargain
    /// rather than a constraint worked around: it is also the one place in the tick that writes another archetype's
    /// components, so keeping it on one thread is what makes the #907 shape safe by construction rather than by argument.
    /// The volume is bounded by weapon cooldowns — a shot is one event per player per one to three seconds — so at the
    /// faithful baseline this is single digits of events a tick.</para>
    /// <para><b>It writes through <c>ctx.Transaction</c>, not a side transaction.</b> That is AC-2: the reward and the
    /// loot land in the TICK's unit of work, so the tick's flush waits for the WAL records they produced and
    /// <c>TickTelemetry.UowFlushMs</c> stops being a hard zero. A side transaction would commit on its own and the tick
    /// would carry nothing — which is the difference between exercising the durability path and describing it.</para>
    /// <para><b>It is the only system that writes <see cref="Inventory"/>, the world's one Versioned component.</b> Before
    /// SWG-02 nothing wrote it after the world build (gap G3), so the per-archetype durability split — a Player whose
    /// cluster half is checkpoint-durable and whose inventory half is WAL-logged — was a declaration nothing tested.</para>
    /// </remarks>
    public void CombatResolveTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var pending = CombatQueue.Count;
        if (pending == 0)
        {
            return;
        }

        // Stack for the overwhelmingly common tick, heap for a burst. Drain THROWS on a short span rather than truncating, so the size comes from Count and
        // never from a guess — a stackalloc[16] loop is exactly the drain-side loss the engine made loud.
        Span<CombatEvent> events = pending <= 128 ? stackalloc CombatEvent[128] : new CombatEvent[pending];
        var n = CombatQueue.Drain(events);
        var tx = ctx.Transaction;
        var corpseTicks = Math.Max(1, (int)(_config.CorpseSeconds * _config.TickRateHz));
        long damageApplied = 0;
        long killed = 0;
        long lairHits = 0;
        long incapacitated = 0;
        long revived = 0;
        long stale = 0;

        for (var i = 0; i < n; i++)
        {
            ref readonly var ev = ref events[i];
            if (ev.Kind == CombatEventKind.Revive)
            {
                if (tx.TryOpenMut(ev.Target, out var downed) && Revive(tx, ref downed, ev.Target, out var moved))
                {
                    revived++;

                    // A clone stands in a city now, whole: a blow still queued behind this event was struck at the body it left, so it is withdrawn
                    // rather than drawn between two entities kilometres apart.
                    if (moved)
                    {
                        stale += WithdrawAfterClone(events, i + 1, n, ev.Target);
                    }
                }
                else
                {
                    stale++;
                }

                continue;
            }

            if (!tx.TryOpenMut(ev.Target, out var target))
            {
                // The target was destroyed between the push and here. Nothing in this demo destroys anything a shot can name, so this is latent — and it is
                // counted rather than ignored, because a non-zero value would mean an assumption above has stopped holding.
                stale++;
                continue;
            }

            var landed = false;
            switch (ev.TargetKind)
            {
                case CombatTargetKind.Creature:
                    landed = ApplyToCreature(tx, ref target, in ev, corpseTicks, ref killed, ref damageApplied);
                    break;

                case CombatTargetKind.Lair:
                    landed = ApplyToLair(ref target, in ev);
                    if (landed)
                    {
                        lairHits++;
                        damageApplied++;
                    }
                    else
                    {
                        stale++;
                    }

                    break;

                default:
                    // A lethal blow leaves the player down where it stands, health 0, which is what keeps a second shooter from killing it again: every later
                    // blow in this drain or the next is refused until it is revived.
                    landed = ApplyToPlayer(ref target, in ev, ctx.TickNumber, ref incapacitated);
                    if (landed)
                    {
                        damageApplied++;
                    }
                    else
                    {
                        stale++;
                    }

                    break;
            }

            // On the wire only when the blow actually landed: an event for a hit that was refused (already dead, mission
            // over) would have a client draw a line for something that did not happen.
            if (landed)
            {
                TatooineReplication.Strike(
                    ctx,
                    new Attack
                    {
                        Attacker = ev.Attacker,
                        Target = ev.Target,
                        Amount = (ushort)Math.Clamp(ev.Amount, 0, ushort.MaxValue),
                    });
            }
        }

        if (damageApplied != 0)
        {
            Interlocked.Add(ref _damageApplied, damageApplied);
        }

        if (killed != 0)
        {
            Interlocked.Add(ref _creaturesKilled, killed);
        }

        if (lairHits != 0)
        {
            Interlocked.Add(ref _lairHits, lairHits);
        }

        if (incapacitated != 0)
        {
            Interlocked.Add(ref _playersIncapacitated, incapacitated);
        }

        if (revived != 0)
        {
            Interlocked.Add(ref _playersRevived, revived);
        }

        if (stale != 0)
        {
            Interlocked.Add(ref _eventsStale, stale);
        }
    }

    /// <summary>
    /// Withdraws every <see cref="CombatEventKind.Damage"/> still to be drained that names a player just cloned.
    /// </summary>
    /// <remarks>
    /// A clone restores full health, so the <c>Health &lt;= 0</c> guard that stops two shooters both killing one target
    /// no longer applies to this player. Withdrawn by blanking the target, which the drain already treats as an event
    /// whose world moved under it — it counts rather than swallows.
    /// </remarks>
    /// <param name="events">The drained span.</param>
    /// <param name="from">The first index not yet applied.</param>
    /// <param name="n">How many of the span are live.</param>
    /// <param name="target">The player that was cloned.</param>
    /// <returns>How many were withdrawn.</returns>
    internal static long WithdrawAfterClone(Span<CombatEvent> events, int from, int n, EntityId target)
    {
        long withdrawn = 0;
        for (var j = from; j < n; j++)
        {
            if (events[j].Kind == CombatEventKind.Damage && events[j].Target == target)
            {
                events[j].Target = EntityId.Null;
                withdrawn++;
            }
        }

        return withdrawn;
    }

    /// <summary>Damage to a creature: health down, a grudge against the shooter, and loot to whoever landed the last hit.</summary>
    /// <returns>Whether the blow landed; <see langword="false"/> when the creature was already dead this tick.</returns>
    private bool ApplyToCreature(Transaction tx, ref EntityRefMut target, in CombatEvent ev, int corpseTicks, ref long killed, ref long damageApplied)
    {
        var v = target.Read(Creature.Vitals);
        if (v.Health <= 0)
        {
            return false;   // already dead this tick, from another shooter's event in the same drain
        }

        damageApplied++;
        var health = v.Health - ev.Amount;
        var ai = target.Read(Creature.Ai);
        if (health > 0)
        {
            v.Health = health;
            target.Set(Creature.Vitals, v);

            // Wounded and now angry: the creature turns on WHOEVER IS SHOOTING, which is what pulls a lair. Before SWG-02 it set Pursue with no destination
            // and no attacker, so it pursued nothing (gap G7).
            if (ai.Mode is AiMode.Wander or AiMode.Idle)
            {
                ai.Mode = AiMode.Pursue;
                ai.Target = ev.Attacker;
                target.Set(Creature.Ai, ai);
                var timers = target.Read(Creature.Timers);
                timers.ThinkCooldown = 0;
                target.Set(Creature.Timers, timers);
            }
            else if (ai.Target.IsNull)
            {
                ai.Target = ev.Attacker;
                target.Set(Creature.Ai, ai);
            }

            TatooineReplication.Replicate(in target);
            return true;
        }

        v.Health = 0;
        target.Set(Creature.Vitals, v);
        ai.Mode = AiMode.Dead;
        ai.Target = EntityId.Null;
        target.Set(Creature.Ai, ai);
        var t = target.Read(Creature.Timers);
        t.ThinkCooldown = corpseTicks;
        t.AttackCooldown = 0;
        target.Set(Creature.Timers, t);
        TatooineReplication.Replicate(in target);
        killed++;

        // One fewer standing between the lair and whoever comes for it: at none, it can be destroyed.
        EntityId home = target.Read(Creature.Home).Lair;
        if (tx.TryOpenMut(home, out var lair))
        {
            var spawner = lair.Read(CreatureLair.Spawner);
            spawner.Guards = Math.Max(0, spawner.Guards - 1);
            lair.Set(CreatureLair.Spawner, spawner);
            TatooineReplication.Replicate(in lair);
        }

        // The loot, written to the shooter's Inventory in this transaction. A counter rather than an Item entity, and deliberately: an event cannot spawn
        // another archetype's entity until #907 is fixed, so real items are S3's work and out of WP-3's scope. What the counter DOES exercise is the thing
        // worth exercising — a Versioned write on a cluster archetype, in the tick's unit of work, every time something dies.
        Loot(tx, ev.Attacker, ai.Template);
        return true;
    }

    /// <summary>
    /// Damage to a lair. A wild lair at zero is retired by the lair director in this tick's Economy phase; a mission's is the mission desk's, after it in the
    /// same phase — completed if its holder's blow is among those that broke it (<see cref="Lair.OwnerHit"/>), drawn again if not.
    /// </summary>
    private static bool ApplyToLair(ref EntityRefMut target, in CombatEvent ev)
    {
        var v = target.Read(CreatureLair.Vitals);
        var spawner = target.Read(CreatureLair.Spawner);
        if (!IsStandingLair(spawner, v) || IsShielded(in spawner))
        {
            return false;   // broken already this tick, or an occupant stood up again
        }

        v.Health = Math.Max(0, v.Health - ev.Amount);
        target.Set(CreatureLair.Vitals, v);
        if (spawner.OwnerHit == 0 && !spawner.Owner.IsNull && spawner.Owner.Id == ev.Attacker)
        {
            spawner.OwnerHit = 1;
            target.Set(CreatureLair.Spawner, spawner);
        }

        TatooineReplication.Replicate(in target);
        return true;
    }

    /// <summary>Damage to a player; at no health, the player goes down where it stands.</summary>
    /// <remarks>
    /// <b>Knocked out or dead, even odds</b> (Loïc, 2026-10-24), and the roll is the server's, keyed on the player and the tick. Both lie where they fell, stop,
    /// lose their target and are nobody's target (<c>CreatureThink</c> drops a downed quarry and its aggro skips one). Neither is the end: the player's own
    /// combat pass asks for a <see cref="CombatEventKind.Revive"/> at <see cref="PlayerVitals.UpTick"/> — a knocked-out one gets up where it lies, a dead one
    /// wakes as a clone at the nearest city (<see cref="Revive"/>).
    /// </remarks>
    private bool ApplyToPlayer(ref EntityRefMut target, in CombatEvent ev, long tick, ref long incapacitated)
    {
        var v = target.Read(Player.Vitals);
        if (v.Health <= 0)
        {
            return false;   // already down
        }

        // Being hit is what stops the healing, until it has been quiet a while.
        v.LastHitTick = (int)Math.Max(1, tick);
        var health = v.Health - ev.Amount;
        if (health > 0)
        {
            v.Health = health;
            target.Set(Player.Vitals, v);
            TatooineReplication.Replicate(in target);
            return true;
        }

        var dead = Hash01(Salt(tick, ev.Target.EntityKey, 0x7F4A7C15u)) < 0.5f;
        var lies = dead ? TatooineData.CloneDelaySec : TatooineData.KnockOutSec;
        v.Health = 0;
        v.AttackCooldown = 0;
        v.Down = dead ? Downed.Dead : Downed.KnockedOut;
        v.UpTick = (int)(tick + Math.Max(1, (int)MathF.Round(lies * _config.TickRateHz)));
        target.Set(Player.Vitals, v);

        // Where it fell: a body walking when the blow landed would otherwise slide on at its last velocity.
        var place = target.Read(Player.Bounds);
        var move = target.Read(Player.Move);
        move.VelX = 0f;
        move.VelZ = 0f;
        move.DestX = place.X;
        move.DestZ = place.Z;
        target.Set(Player.Move, move);

        // Its target falls with it, or it would get up still aiming at whatever it was fighting.
        var session = target.Read(Player.Session);
        session.Target = EntityId.Null;
        target.Set(Player.Session, session);

        // And off its swoop: a body lies on the ground, not on a seat.
        var state = target.Read(Player.State);
        if (state.Mounted != 0)
        {
            state.Mounted = 0;
            target.Set(Player.State, state);
        }

        incapacitated++;
        TatooineReplication.Replicate(in target);
        return true;
    }

    /// <summary>
    /// A downed player's time is up: a knocked-out one gets up where it lies with a fifth of its health; a dead one wakes whole as a clone outside the nearest
    /// city's shuttleport — one teleport, no wounds and no insurance (AC-5).
    /// </summary>
    /// <param name="tx">The tick's transaction, which the teleport goes through.</param>
    /// <param name="target">The downed player.</param>
    /// <param name="id">Its identity.</param>
    /// <param name="moved">Set when it was cloned somewhere else: the caller withdraws the blows still queued against the body it left.</param>
    /// <returns>Whether it was down, and is up now.</returns>
    private bool Revive(Transaction tx, ref EntityRefMut target, EntityId id, out bool moved)
    {
        moved = false;
        var v = target.Read(Player.Vitals);
        if (v.Down == Downed.Up)
        {
            return false;
        }

        var dead = v.Down == Downed.Dead;
        v.Health = dead ? v.MaxHealth : Math.Max(1, (int)(v.MaxHealth * TatooineData.KnockOutRecoveryFraction));
        v.Down = Downed.Up;
        v.UpTick = 0;
        v.AttackCooldown = 0;
        target.Set(Player.Vitals, v);

        // The crowd's player wakes idle and stays put a while before deciding anything; a clone puts its weapon away, as a door or a flight does.
        var state = target.Read(Player.State);
        state.Activity = PlayerActivity.Idle;
        state.ActivityTicks = CloneRecoverySeconds * _config.TickRateHz;
        if (dead)
        {
            state.Stance = Stances.None;
        }

        target.Set(Player.State, state);

        // Only on a planet. A player who died inside a building or a dungeon wakes where it lies: cloning it to a city would be a cross-realm teleport competing
        // with the systems that own those crossings, and the interior would keep a pin on a player that had left.
        var realm = target.Read(Player.Realm).Value;
        if (!dead || realm >= _config.Planets)
        {
            TatooineReplication.Replicate(in target);
            return true;
        }

        var place = target.Read(Player.Bounds);
        var city = CloneSpot((ushort)realm, place.X, place.Z, id.EntityKey);
        var at = default(PlayerPlacement);
        // The realm the player is teleported INTO is the one that owns the ground, and it is the same one the teleport
        // below names. Passing RealmId.Default here was right only because every planet currently shares one field.
        var home = new RealmId((ushort)realm);
        at.SetAt(city.X, city.Z, GroundAt(home, city.X, city.Z), place.HalfExtent);
        tx.Teleport(id, Player.Bounds, home, in at);
        TatooineReplication.Replicate(in target);
        moved = true;
        return true;
    }

    /// <summary>Points a scripted body at its mission's lair, and sets it walking (the mission desk, at the take and whenever the lair moves).</summary>
    /// <remarks>
    /// <para><b>The crowd's only.</b> A person is told over the wire — the journal and its waypoint — and walks there or not, which is what a mission is; a
    /// destination written onto a possessed body would overwrite the one its client sent, the two-deciders bug SWG-01 exists to prevent.</para>
    /// <para><b>The timer is sized for the walk, not for the fight.</b> A lair 0.5-2 km out at the mounted speed is one to three minutes away, and the 120 s the
    /// activity mix hands a fighter would expire mid-desert and re-roll the activity — which is how a mission that was taken would still never be reached.</para>
    /// </remarks>
    private void SendOnMission(Transaction tx, EntityId body, float x, float z)
    {
        if (!tx.TryOpenMut(body, out var player) || player.Read(Player.Control).Kind != ControllerKind.Scripted)
        {
            return;
        }

        var state = player.Read(Player.State);
        state.Activity = PlayerActivity.Combat;
        state.ActivityTicks = MissionWalkSeconds * _config.TickRateHz;
        state.MissionX = x;
        state.MissionZ = z;
        player.Set(Player.State, state);
        var move = player.Read(Player.Move);
        move.DestX = x;
        move.DestZ = z;
        move.SpeedMps = TatooineData.PlayerMountSpeedMps;
        var place = player.Read(Player.Bounds);
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, MetresPerTick, place.X, place.Z, x, z);
        player.Set(Player.Move, move);
        TatooineReplication.Replicate(in player);
        Interlocked.Increment(ref _missionsAssigned);
    }

    /// <summary>[EST] Seconds a scripted body is given to walk to its mission and fight there; past it the crowd gives the mission up.</summary>
    private const int MissionWalkSeconds = 300;

    /// <summary>A completed mission's payout, into its holder's <see cref="Inventory"/> — a Versioned write in the tick's unit of work.</summary>
    private void Reward(Transaction tx, EntityId body, int credits)
    {
        if (!tx.TryOpenMut(body, out var player))
        {
            return;
        }

        var inv = player.Read(Player.Inventory);
        inv.Credits += credits;
        player.Set(Player.Inventory, inv);
        var state = player.Read(Player.State);
        state.MissionsCompleted++;
        player.Set(Player.State, state);
        TatooineReplication.Replicate(in player);
        Interlocked.Increment(ref _missionRewards);
    }

    /// <summary>What a kill drops, into the shooter's <see cref="Inventory"/>.</summary>
    /// <remarks>
    /// Silently does nothing when the attacker has gone — a kill still counts, because the creature is just as dead whether or not anybody was left to
    /// collect. The alternative, dropping the whole kill, would make a creature immortal whenever its killer logged out on the same tick.
    /// </remarks>
    private static void Loot(Transaction tx, EntityId attacker, byte template)
    {
        if (attacker.IsNull || !tx.TryOpenMut(attacker, out var player))
        {
            return;
        }

        var credits = template < CreatureTemplates.LootCredits.Length ? CreatureTemplates.LootCredits[template] : 0;
        var inv = player.Read(Player.Inventory);
        inv.Credits += credits;
        inv.ItemCount++;
        inv.ItemValue += credits;
        player.Set(Player.Inventory, inv);
    }

    /// <summary>
    /// Whether a lair's occupants still stand between it and whoever attacks it: nothing damages it until they are dead (Loïc, 2026-10-24).
    /// </summary>
    /// <remarks>
    /// <b>Every lair, a mission's included.</b> The destroy-mission pool's were exempt, because the pool teleported a lair and left its defenders where they
    /// stood; a mission's lair is drawn with its occupants round it now (missions.md § 3).
    /// </remarks>
    internal static bool IsShielded(in Lair lair) => lair.Guards > 0;

    /// <summary>Whether a lair can still be damaged at all: it has health left.</summary>
    internal static bool IsStandingLair(in Lair lair, in LairVitals vitals) => vitals.Health > 0;

    /// <summary>Where a cloned player wakes up: on the lane outside the gate of the nearest city's shuttleport.</summary>
    /// <remarks>
    /// <para>
    /// <b>Not the city's centre, which is where the shuttleport stands.</b> A clone used to be teleported to the city's own coordinates — the middle of
    /// the port's landing pad — and could not walk out: its route search started inside the pad, deeper than any search snaps, so it walked straight and
    /// stopped at the ring wall. Most of the crowd stuck "inside the shuttleport" (Loïc, 2026-10-24) had been cloned there. The gate's lane is the free
    /// ground shuttle arrivals already land on (<see cref="WorldBuilder.OutsideGate"/>), spread across and along it by the body's key.
    /// </para>
    /// <para>Linear over the cities, which is nine of them: an index would be a data structure to answer a question asked once per player death.</para>
    /// </remarks>
    private (float X, float Z) CloneSpot(ushort planet, float x, float z, long key)
    {
        var bestSq = float.MaxValue;
        var best = -1;
        for (var i = 0; i < _index.Cities.Count; i++)
        {
            var c = _index.Cities[i];
            var dx = c.X - x;
            var dz = c.Z - z;
            var d2 = (dx * dx) + (dz * dz);
            if (d2 < bestSq)
            {
                bestSq = d2;
                best = i;
            }
        }

        var ports = PortsOf(planet);
        if (best < 0 || best >= ports.Count)
        {
            return best < 0 ? (x, z) : (_index.Cities[best].X, _index.Cities[best].Z);
        }

        var (portX, portZ) = ports[best];
        return WorldBuilder.OutsideGate(portX, portZ, Hash01((uint)key ^ 0x3C6EF372u), Hash01((uint)key ^ 0x9E3779B9u));
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

    /// <summary>The cluster's mutable player vitals, handed out on the first write of this cluster's walk and not before.</summary>
    private static Span<PlayerVitals> PlayerVitalsRw(in ClusterRef<Player> cluster, ref Span<PlayerVitals> rw)
    {
        if (rw.IsEmpty)
        {
            rw = cluster.GetSpan(Player.Vitals);
        }

        return rw;
    }

    /// <summary>The cluster's mutable player sessions, handed out on the first write of this cluster's walk and not before.</summary>
    private static Span<PlayerSession> PlayerSessionsRw(in ClusterRef<Player> cluster, ref Span<PlayerSession> rw)
    {
        if (rw.IsEmpty)
        {
            rw = cluster.GetSpan(Player.Session);
        }

        return rw;
    }

    /// <summary>Forgets what a creature was fighting, taking the mutable brain span on the first write of this cluster.</summary>
    private static void ClearTarget(in ClusterRef<Creature> cluster, ref Span<CreatureBrain> rw, int idx)
        => BrainsRw(in cluster, ref rw)[idx].Target = EntityId.Null;

    /// <summary>[EST] Seconds a cloned player spends idle in the city before deciding what to do next.</summary>
    private const int CloneRecoverySeconds = 10;
}
