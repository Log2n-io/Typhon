using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// The per-tick behaviour bodies: AI, player activity, movement, awareness and the economy.
/// </summary>
public sealed partial class SimBridge
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Think — creatures
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creature AI. Decides a mode and a velocity; writes no position.
    /// </summary>
    /// <remarks>
    /// <para><b>Creatures do not think every tick, and that is faithful rather than a saving.</b> Core3's behaviour
    /// interval is 400-1000 ms, so at 10 Hz a creature decides something once every four to ten ticks. The cooldown is
    /// re-seeded from a per-entity hash, so a lair's six creatures think on six different ticks instead of arriving as a
    /// spike.</para>
    /// <para><b>Only aggressive templates run the aggro query.</b> A bantha never asks who is nearby, which takes most of
    /// the population out of the query load — and is exactly what the game did.</para>
    /// </remarks>
    public void CreatureThinkTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var tick = ctx.TickNumber;
        var thinkMin = WorldBuilder.AiTicksMin(_config);
        var thinkSpan = Math.Max(1, WorldBuilder.AiTicksMax(_config) - thinkMin + 1);
        long aggroQueries = 0;
        long aggroHits = 0;
        long chaseLost = 0;
        long chaseGivenUp = 0;
        var corpses = ctx.Writer(CorpseQueue);

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

            // READ-ONLY by default, and taken mutably only on the tick a mode actually changes. GetSpan marks the cluster changed on the HANDOUT, so a
            // mutable span taken every tick to read Mode claims a change on every tick whether or not one happened — which is the whole of what the
            // projected-component mask cannot see through, since CreatureBrain genuinely is projected.
            var brains = cluster.GetReadOnlySpan(Creature.Ai);
            Span<CreatureBrain> brainsRw = default;

            // Scheduling: written every tick by design, read by nobody on the wire, and in a component no projection names.
            var timers = cluster.GetSpan(Creature.Timers);
            var motions = cluster.GetSpan(Creature.Move);

            // Read-only: the aggro gate reads the health, and nothing here writes it.
            var vitals = cluster.GetReadOnlySpan(Creature.Vitals);

            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                // A COPY, not a ref: the span above is read-only. Every mode write below ends its iteration, so the copy is never read after being stale.
                var ai = brains[idx];
                ref var t = ref timers[idx];
                if (ai.Mode == AiMode.Dead)
                {
                    // The corpse's countdown. When it runs out the creature is handed to the lair director to be DESTROYED (SimBridge.Lairs.cs) — this
                    // system is parallel and may not destroy — and its countdown is parked so it is handed over once.
                    if (t.ThinkCooldown > 0 && --t.ThinkCooldown == 0)
                    {
                        corpses.Push(new CorpseEvent { Creature = cluster.GetEntityId(idx) });
                        t.ThinkCooldown = int.MaxValue;
                    }

                    continue;
                }

                // AMBIENT population, skipped BEFORE the write below, and the order is the whole point. A creature that merely counts down still writes
                // its brain on every tick of its life, which keeps its cluster permanently dirty and defeats every change-detection mechanism
                // downstream. See SimConfig.IdleCreatureFraction.
                if (ai.Mode == AiMode.Idle)
                {
                    continue;
                }

                if (t.ThinkCooldown > 0)
                {
                    t.ThinkCooldown--;
                    continue;
                }

                // Taken here rather than at the top of the loop: only a creature that actually decides this tick draws, and an unconditional lookup would
                // cost one per creature per tick for nothing.
                var key = cluster.GetEntityId(idx).EntityKey;
                t.ThinkCooldown = thinkMin + (int)(Hash01(Salt(tick, key, 0x51ED2701u)) * thinkSpan);

                ref var move = ref motions[idx];
                var x = places[idx].X;
                var z = places[idx].Z;
                var dxHome = x - ai.HomeX;
                var dzHome = z - ai.HomeZ;
                var homeSq = (dxHome * dxHome) + (dzHome * dzHome);

                // Leashing overrides everything: a creature dragged past its tether goes home and ignores the world on
                // the way. Without it one kited creature walks off the map and its cluster's bound follows.
                if (homeSq > ai.LeashRadius * ai.LeashRadius)
                {
                    SetCreatureMode(cluster, ref brainsRw, idx, AiMode.Leashing);
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, ai.HomeX, ai.HomeZ);
                    continue;
                }

                if (ai.Mode == AiMode.Leashing)
                {
                    if (homeSq < 16f)
                    {
                        SetCreatureMode(cluster, ref brainsRw, idx, AiMode.Wander);
                        PickWanderDestination(ref move, in ai, x, z, tick, key);
                    }
                    else
                    {
                        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, ai.HomeX, ai.HomeZ);
                    }

                    continue;
                }

                if (ai.Mode is AiMode.Pursue or AiMode.Fighting)
                {
                    // SWG-02: a pursuer chases an ENTITY, and re-reads where that entity is on every decision.
                    //
                    // Before this the destination was the point the target occupied on the tick the aggro query found it, and it was never refreshed — so a
                    // creature walked to a place its target had left and parked there for the rest of its life, with the parked population growing over a long
                    // run (gap G6). The chase is also now finite: past MaxChaseRangeM the creature gives up, which is the constant Core3 calls MAX_OOS_RANGE
                    // and which this world declared and never used.
                    // A downed quarry — knocked out or dead — is nobody's target: the creature lets it lie and goes back to its lair's ground.
                    if (ai.Target.IsNull || !ctx.Accessor.TryOpen(ai.Target, out var quarry) || quarry.Read(Player.Vitals).Health <= 0)
                    {
                        SetCreatureMode(cluster, ref brainsRw, idx, AiMode.Wander);
                        ClearTarget(in cluster, ref brainsRw, idx);
                        PickWanderDestination(ref move, in ai, x, z, tick, key);
                        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                        chaseLost++;
                        continue;
                    }

                    var quarryAt = quarry.Read(Player.Bounds);
                    var dxT = quarryAt.X - x;
                    var dzT = quarryAt.Z - z;
                    var targetSq = (dxT * dxT) + (dzT * dzT);

                    // AC-3's leash. Distinct from LeashRadius above, which measures from the lair: this one measures from the QUARRY, and it is what stops a
                    // creature following a mounted player across the planet at a speed it can never match.
                    if (targetSq > MaxChaseRange * MaxChaseRange)
                    {
                        SetCreatureMode(cluster, ref brainsRw, idx, AiMode.Wander);
                        ClearTarget(in cluster, ref brainsRw, idx);
                        PickWanderDestination(ref move, in ai, x, z, tick, key);
                        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                        chaseGivenUp++;
                        continue;
                    }

                    // Guarded, like StandStill and for the same reason: this span is GetSpan-backed, so an unconditional store on a quarry that has not moved
                    // marks the cluster changed for nothing.
                    if (move.DestX != quarryAt.X || move.DestZ != quarryAt.Z)
                    {
                        move.DestX = quarryAt.X;
                        move.DestZ = quarryAt.Z;
                    }

                    // Close to weapon range, then STAND AND SHOOT.
                    //
                    // A pursuer that keeps steering once it is already in range writes a new position on every tick of every fight, which is the same
                    // defect as a creature that never rests and costs the same downstream. Real combat is a closing phase and then a stationary one: the
                    // creature walks until the target is within its weapon's reach and holds position while it attacks.
                    //
                    // The break range is deliberately wider than the attack range. At equal thresholds a creature sitting on the boundary alternates
                    // between stopping and closing on successive decisions, which writes MORE than pursuing would.
                    if (ai.Mode == AiMode.Fighting)
                    {
                        var breakRange = _creatureAttackRange * AttackRangeHysteresis;
                        if (targetSq > breakRange * breakRange)
                        {
                            SetCreatureMode(cluster, ref brainsRw, idx, AiMode.Pursue);
                            Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                        }

                        // Otherwise in range and already stopped: nothing is written, which is the whole gain.
                        continue;
                    }

                    if (targetSq <= _creatureAttackRange * _creatureAttackRange)
                    {
                        SetCreatureMode(cluster, ref brainsRw, idx, AiMode.Fighting);
                        StandStill(ref move);
                    }
                    else
                    {
                        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                    }

                    continue;
                }

                // Wander as amble-then-graze rather than a permanent walk.
                //
                // Three states, distinguished by two absolute tick stamps so that neither of the two common ones writes anything: walking the current leg
                // (the velocity already points the right way and the Move system applies it), standing still (written once, on the transition), and
                // picking the next leg. See CreatureBrain.MoveUntilTick.
                if (tick < t.MoveUntilTick)
                {
                    // Mid-leg. Re-steering here would only re-derive the velocity it already has.
                }
                else if (tick < t.RestUntilTick)
                {
                    StandStill(ref move);
                }
                else
                {
                    PickWanderDestination(ref move, in ai, x, z, tick, key);
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);

                    var legTicks = 1 + (int)(Hash01(Salt(tick, key, 0x1B873593u)) * _wanderLegTicks);
                    t.MoveUntilTick = tick + legTicks;
                    t.RestUntilTick = t.MoveUntilTick + (legTicks * WanderRestToMoveRatio);
                }

                // The aggro query. A 24 m bubble against a few hundred players spread over a 16 km planet returns
                // nothing the overwhelming majority of the time — which is the point: the cost an engine has to make
                // cheap here is the NEGATIVE case, not the hit.
                if (ai.AggroRadius > 0f && vitals[idx].Health > 0)
                {
                    aggroQueries++;
                    var sphere = new BSphere2F { CenterX = x, CenterY = z, Radius = ai.AggroRadius };
                    // The cluster's realm: a creature aggroes on the players of its own planet (Realms G1).
                    var e = Dbe.ClusterSpatialQuery<Player>(cluster.Realm).Radius(in sphere);
                    try
                    {
                        var bestSq = double.MaxValue;
                        var found = false;
                        var bestEntity = EntityId.Null;
                        float tx = 0f, tz = 0f;
                        while (e.MoveNext())
                        {
                            var hit = e.Current;

                            // A downed player is no threat and no prey. Read only for a hit nearer than the best so far, which in a 24 m bubble is a handful.
                            if (hit.DistanceSq < bestSq && ctx.Accessor.TryOpen(hit.Entity, out var candidate)
                                && candidate.Read(Player.Vitals).Health > 0)
                            {
                                // The query result is in the engine's f64 world frame (#914); the simulation keeps f32 positions.
                                bestSq = hit.DistanceSq;
                                tx = (float)((hit.MinX + hit.MaxX) * 0.5);
                                tz = (float)((hit.MinY + hit.MaxY) * 0.5);
                                bestEntity = hit.Entity;
                                found = true;
                            }
                        }

                        if (found)
                        {
                            aggroHits++;
                            SetCreatureMode(cluster, ref brainsRw, idx, AiMode.Pursue);

                            // The identity, not only the point (SWG-02). Without it the pursuit below has nothing to re-read and nothing to attack: shooter
                            // and quarry identities were never captured anywhere, which is gap G7.
                            BrainsRw(in cluster, ref brainsRw)[idx].Target = bestEntity;
                            move.DestX = tx;
                            move.DestZ = tz;
                            Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, tx, tz);
                        }
                    }
                    finally
                    {
                        e.Dispose();
                    }
                }
            }
        }

        if (aggroQueries != 0)
        {
            Interlocked.Add(ref _aggroQueries, aggroQueries);
            Interlocked.Add(ref _aggroHits, aggroHits);
        }

        if (chaseLost != 0)
        {
            Interlocked.Add(ref _creaturesLostTarget, chaseLost);
        }

        if (chaseGivenUp != 0)
        {
            Interlocked.Add(ref _chaseGivenUp, chaseGivenUp);
        }
    }

    /// <summary>Pick a new wander destination inside the leash radius, on the creature's own side of its lair.</summary>
    /// <remarks>
    /// <para>
    /// <b>Never through the lair.</b> A lair is a prop now — a nest, a camp — and its creatures are born round it (<c>WorldBuilder.LairRingM</c>). A leg drawn
    /// anywhere in the disc sent about a third of them straight across the centre and through the prop, which nothing on the server collides a creature
    /// against: 7.4 % of a small world's wanderers stood inside the nest at any moment. So a leg keeps within 75° of the creature's own bearing from home
    /// and ends outside the spawn ring; over several legs a creature still covers its whole side of the disc.
    /// </para>
    /// <para>A creature standing on its home point — an older world, a leash return — has no bearing, and draws one.</para>
    /// </remarks>
    private void PickWanderDestination(ref CreatureMotion move, in CreatureBrain ai, float x, float z, long tick, long key)
    {
        var dx = x - ai.HomeX;
        var dz = z - ai.HomeZ;
        var bearing = (dx * dx) + (dz * dz) > 0.01f ? MathF.Atan2(dz, dx) : Hash01(Salt(tick, key, 0x2545F491u)) * MathF.PI * 2f;
        var a = bearing + ((Hash01(Salt(tick, key, 0x9E3779B9u)) - 0.5f) * WanderArcRad);
        // Within LairWanderRadiusM of the lair, or the leash's own 0.7 when a scaled-down world has made that the smaller.
        var reach = MathF.Max(WorldBuilder.LairRingM + 1f, MathF.Min(ai.LeashRadius * 0.7f, TatooineData.LairWanderRadiusM));
        var r = WorldBuilder.LairRingM + ((reach - WorldBuilder.LairRingM) * MathF.Sqrt(Hash01(Salt(tick, key, 0x85EBCA6Bu))));
        move.DestX = ai.HomeX + (MathF.Cos(a) * r);
        move.DestZ = ai.HomeZ + (MathF.Sin(a) * r);
    }

    /// <summary>The arc a wander leg is drawn in, centred on the creature's bearing from its lair: ±75°.</summary>
    private const float WanderArcRad = MathF.PI * 150f / 180f;

    /// <summary>Writes a creature's mode, taking the mutable brain span on the first write of this cluster and not before.</summary>
    /// <param name="cluster">The cluster being walked.</param>
    /// <param name="rw">The mutable span, empty until the first write.</param>
    /// <param name="idx">The slot.</param>
    /// <param name="mode">The new mode.</param>
    /// <remarks>
    /// <c>GetSpan</c> marks its cluster changed on the handout rather than on a write, so taking one per tick to read a field claims a change per tick.
    /// Mode transitions are rare — a creature aggroes, leashes, engages or dies — so deferring the handout to the tick one happens is the difference
    /// between a cluster that is dirty always and one that is dirty when something actually changed.
    /// </remarks>
    private static void SetCreatureMode(in ClusterRef<Creature> cluster, ref Span<CreatureBrain> rw, int idx, int mode)
    {
        if (rw.IsEmpty)
        {
            rw = cluster.GetSpan(Creature.Ai);
        }

        rw[idx].Mode = mode;
        TatooineReplication.Replicate(in cluster, idx);
    }

    /// <summary>Stops a mover, writing only when it was actually moving.</summary>
    /// <param name="move">The motion component.</param>
    /// <remarks>
    /// The guard is the point rather than a micro-optimisation: these components are <c>GetSpan</c>-backed, so an unconditional store marks the cluster
    /// changed on every tick of a rest and gives back exactly what the rest was introduced to save.
    /// </remarks>
    private static void StandStill(ref CreatureMotion move)
    {
        if (move.VelX != 0f || move.VelZ != 0f)
        {
            move.VelX = 0f;
            move.VelZ = 0f;
        }
    }

    /// <summary>
    /// Point a velocity at a destination at a given speed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the two velocity components by <c>ref</c> rather than the motion component, because the three motion types
    /// are distinct for the scheduler's benefit (see the header of <c>Components.cs</c>) and a shared helper would
    /// otherwise have to be written three times.
    /// </para>
    /// <para>
    /// <b>Static and internal, so that the intent path uses THIS integration rather than a copy of it.</b> An intent is a destination and the server's own
    /// movement is what carries a player to it — which is the whole of why a client cannot move faster than the server allows — and that argument holds only
    /// while there is one integration. It was briefly two, line for line, with the tick period supplied separately to each; two implementations that must agree
    /// and cannot be compared is the shape of the bug, not a duplication to tidy up later.
    /// </para>
    /// </remarks>
    /// <param name="velX">The velocity's X component, written.</param>
    /// <param name="velZ">The velocity's Z component, written.</param>
    /// <param name="speedMps">Metres per second.</param>
    /// <param name="metresPerTick">One tick's share of a second.</param>
    /// <param name="x">Where the entity is.</param>
    /// <param name="z">Where the entity is.</param>
    /// <param name="destX">Where it is going.</param>
    /// <param name="destZ">Where it is going.</param>
    /// <returns>Whether the entity is going anywhere: <see langword="false"/> when it has arrived or has no speed.</returns>
    internal static bool Steer(ref float velX, ref float velZ, float speedMps, float metresPerTick, float x, float z, float destX, float destZ)
    {
        var dx = destX - x;
        var dz = destZ - z;
        var len = MathF.Sqrt((dx * dx) + (dz * dz));
        if (len < 0.001f || speedMps <= 0f)
        {
            velX = 0f;
            velZ = 0f;
            return false;
        }

        var step = speedMps * metresPerTick;
        if (step > len)
        {
            step = len;
        }

        velX = dx / len * step;
        velZ = dz / len * step;
        return true;
    }

    /// <summary>Diagnostic switch, read once from <c>SWG_ALT_PROBE</c>: logs possessed bodies whose altitude is off the terrain under them.</summary>
    /// <remarks>
    /// A client read a player's server altitude 35 m below and 18 m above the terrain (2026-10-27). The candidates are a resumed avatar's
    /// saved altitude, which nothing re-derives while it stands still, and a collision disc's remembered floor. Each line carries what tells
    /// them apart: the terrain, the disc's floor and whether the body is moving. Off by default; the JIT folds the test away.
    /// </remarks>
    private static readonly bool AltitudeProbe = Environment.GetEnvironmentVariable("SWG_ALT_PROBE") == "1";

    /// <summary>How often <see cref="AltitudeProbe"/> samples, in ticks: every 5 s at 50 Hz.</summary>
    private const long AltitudeProbeEveryTicks = 250;

    /// <summary>Logs one possessed body when its altitude is more than a metre off the terrain.</summary>
    private void ProbeAltitude(RealmId realm, EntityId body, in PlayerPlacement place, in PlayerMotion move, in PlayerProximity disc)
    {
        if (realm.Value >= _config.Planets)
        {
            return;
        }

        var ground = GroundAt(realm, place.X, place.Z);
        if (MathF.Abs(place.Y - ground) < 1f)
        {
            return;
        }

        Console.WriteLine(
            $"[alt-probe] {body} at ({place.X:F1}, {place.Z:F1}) y {place.Y:F2} terrain {ground:F2} dy {place.Y - ground:+0.00;-0.00} "
            + $"disc floor {disc.SurfaceY:F2} disc realm {disc.Realm} vel ({move.VelX:F3}, {move.VelZ:F3})");
    }

    /// <summary>
    /// The A/B switch for rung (c)'s cost, read once from <c>SWG_NO_SLOPE</c>.
    /// </summary>
    /// <remarks>
    /// The claim rung (c) makes is that it adds a second ground sample only where the terrain actually slows something, so
    /// the flat majority of the planet pays arithmetic and nothing else. That is a claim about a measurement, and this is
    /// what measures it: a <see langword="static readonly"/> <see cref="bool"/> the JIT folds after the static
    /// constructor, so both arms are the same binary and neither pays a branch.
    /// </remarks>
    private static readonly bool SlopeDisabled = Environment.GetEnvironmentVariable("SWG_NO_SLOPE") == "1";

    /// <summary>
    /// Shortens a step that climbs, in place — terrain rung (c).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called from the MOVE phase and not from <see cref="Steer"/> on purpose. Steer sets a velocity once, when an entity
    /// re-decides; a leg then runs for tens of ticks. Scaling there would fix the speed of a whole leg to the grade at the
    /// moment it was chosen, so a creature that set off across a flat basin would cross the mesa beyond it at basin speed.
    /// The step is what the terrain acts on, and the step is here.
    /// </para>
    /// <para>
    /// <b>The second ground sample is taken only when the step actually shortens.</b> The caller has already sampled the
    /// full-speed end for its own <c>Y</c>, and <paramref name="groundY"/> is that sample; on flat ground — which is most
    /// of the planet and all of every interior — the factor is 1, nothing is re-read, and this costs three subtractions
    /// and a square root. Where it does shorten, the step ends somewhere else, and rung (b)'s invariant is that <c>Y</c> is
    /// the ground at the position actually written — so it is re-read there rather than approximated.
    /// </para>
    /// <para>
    /// The run is measured from the CLAMPED positions rather than from the intended step, so a step the world edge cut
    /// short reports the grade over the distance actually covered instead of a shallower one over a distance that was not.
    /// </para>
    /// </remarks>
    /// <param name="realm">The realm being moved in; a flat one can never slow anything, since both ends read zero.</param>
    /// <param name="from">Where the step began. Its <c>Y</c> is the ground there, which is what makes this free.</param>
    /// <param name="x">The step's end, shortened in place.</param>
    /// <param name="z">The step's end, shortened in place.</param>
    /// <param name="groundY">The ground at that end — re-sampled in place if the step moves.</param>
    /// <param name="halfExtent">The entity's half extent, for the world-edge clamp.</param>
    /// <param name="half">The world's half extent.</param>
    internal void Slow<TPlace>(RealmId realm, in TPlace from, ref float x, ref float z, ref float groundY, float halfExtent, float half)
        where TPlace : unmanaged, IGroundPlacement
    {
        if (SlopeDisabled)
        {
            return;
        }

        var dx = x - from.X;
        var dz = z - from.Z;
        var factor = SlopeSpeed.FactorForStep(from.Y, groundY, MathF.Sqrt((dx * dx) + (dz * dz)));
        if (factor >= 1f)
        {
            return;
        }

        x = Math.Clamp(from.X + (dx * factor), -half + halfExtent, half - halfExtent);
        z = Math.Clamp(from.Z + (dz * factor), -half + halfExtent, half - halfExtent);
        groundY = GroundAt(realm, x, z);
    }

    /// <summary>The instance form, for the simulation's own systems: the same integration at this run's tick period.</summary>
    /// <param name="velX">The velocity's X component, written.</param>
    /// <param name="velZ">The velocity's Z component, written.</param>
    /// <param name="speedMps">Metres per second.</param>
    /// <param name="x">Where the entity is.</param>
    /// <param name="z">Where the entity is.</param>
    /// <param name="destX">Where it is going.</param>
    /// <param name="destZ">Where it is going.</param>
    private void Steer(ref float velX, ref float velZ, float speedMps, float x, float z, float destX, float destZ)
        => Steer(ref velX, ref velZ, speedMps, MetresPerTick, x, z, destX, destZ);

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Think — players
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Player-ticks <see cref="PlayerThinkTick"/> declined to decide for because a client is driving that player (SWG-01).</summary>
    private long _possessedSkipped;

    /// <summary>
    /// How many player-ticks the activity mix did not decide. Zero in a measurement run, which is what makes it a usable assertion: a non-zero value means a
    /// session is driving somebody.
    /// </summary>
    public long PossessedSkipped => Interlocked.Read(ref _possessedSkipped);

    /// <summary>
    /// How often a simulated player says something, in seconds of simulated time (SWG-09).
    /// </summary>
    /// <remarks>
    /// A demo figure and not an engine one: what it sets is how much work the 50 m fan-out is given to do, which is the thing
    /// worth watching. It was a minute; two since speech is drawn as a bubble over the speaker (2026-10-25), because a town
    /// square of scripted players talking once a minute each is a wall of text. See <see cref="NpcChatEverySeconds"/>.
    /// </remarks>
    private const float ChatEverySeconds = 120f;

    /// <summary>
    /// How often a city NPC says something, in seconds of simulated time (SWG-09).
    /// </summary>
    /// <remarks>
    /// <b>Set by what the screen shows, now that speech is a bubble over the speaker</b> (Loïc, 2026-10-25: "it's too much
    /// right now, makes it too crowdy on screen"). It was 8 s, chosen when speech was only a line in the chat panel and a
    /// silent panel read as broken; drawn over heads, the same rate put ten bubbles at once over a town square. Five times
    /// slower keeps a town audibly alive and leaves the bubbles readable — and a player's own line stands out.
    /// </remarks>
    private const float NpcChatEverySeconds = 40f;

    /// <summary>
    /// Player behaviour: an activity mix rather than one loop.
    /// </summary>
    /// <remarks>
    /// <para>The mix is the load-bearing part and it is <b>estimated</b> — nobody published SWG telemetry. It is reasoned
    /// from what the game was: a pre-CU galaxy where crafting and entertaining were first-class professions and a large
    /// share of the population was parked in a cantina at any moment. Hence 40 % idle in a city, 20 % travelling, 22 %
    /// fighting, 18 % roaming.</para>
    /// <para>Why it matters more than the individual behaviours: an idle player still sits in everyone's awareness set
    /// and still costs a query, but it writes no position and so costs the fence nothing. A planet whose players are all
    /// running produces a completely different fence load from one whose players are standing in Mos Eisley, and the real
    /// game was much closer to the latter.</para>
    /// </remarks>
    public void PlayerThinkTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var tick = ctx.TickNumber;
        var hz = _config.TickRateHz;

        // About one line per simulated player per minute: enough that a town is audible and little enough that the wire
        // is not mostly chat. Chat routes to sessions within 50 m, so what this costs a client scales with how crowded
        // the place it is looking at is, not with the population.
        var chatChance = 1f / MathF.Max(1f, ChatEverySeconds * hz);

        // Accumulated locally and added once, rather than an interlocked increment per possessed player per tick from every parallel chunk. It is a diagnostic
        // counter sitting next to its siblings in the same cache line, so the contended form would have chunks invalidating each other's line to maintain a
        // number nobody reads inside the tick.
        var skipped = 0L;
        var objectives = default(ObjectiveCounts);

        // A fighter's objective is checked once a second (lair-objectives.md L-4), each on its own beat.
        var objectiveEvery = Math.Max(1, hz);

        // Bodies a door or a shuttle put somewhere else this tick (Teleport, in Spawn, before this phase): see the possessed branch below.
        var landed = LandedThisTick;

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
            var states = cluster.GetSpan(Player.State);
            var motions = cluster.GetSpan(Player.Move);

            // Whether a player is down, read-only: a downed body is left alone.
            var vitals = cluster.GetReadOnlySpan(Player.Vitals);

            // The way round a city's buildings (SimBridge.Routes.cs). Not projected, so a write here reaches no subscriber.
            var routes = cluster.GetSpan(Player.Route);

            // Read-only, so asking who drives these players does not mark the cluster changed. One byte per player, contiguous: a cache line covers 64 of
            // them, which is why possession is its own component rather than three more fields on PlayerState (SWG-01).
            var controls = cluster.GetReadOnlySpan(Player.Control);
            var realm = cluster.Realm.Value;
            var k = ctx.Realms.TicksPerVisit(cluster.Realm);   // Realms G2: N ticks elapse between two visits of a strided realm at divisor N

            // Explicit replication (ADR-067): the players whose replicated activity this pass changes. Positions are pushed by WriteSpatial.
            var pushSlots = 0UL;
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                // A possessed player is its client's to drive, and two deciders on one entity is the bug this guards (SWG-01). Nothing below runs for it: not
                // the arrival test, not the re-decide, not the timer — a possessed player has no server-side activity timer at all, so ActivityTicks is zero
                // and the re-decide below would fire on every tick and overwrite the destination the client just sent.
                if (controls[idx].Kind != ControllerKind.Scripted)
                {
                    // Except that a possessed body a door or a shuttle just moved stands on its landing. Its destination and velocity were the realm it
                    // left — a point in another frame — so it walked on at it: out through the room's way out a second later, back in through the front,
                    // or into a room's corner. Here and not in the crossing's own transaction, which is durable: a motion written there was three WAL
                    // records a crossing tick (CausalityChecks.AC2). Its client renews what it wants on the ticks that follow.
                    if (landed != null && landed.Contains(cluster.GetEntityId(idx)))
                    {
                        ref var stop = ref motions[idx];
                        stop.VelX = 0f;
                        stop.VelZ = 0f;
                        stop.DestX = places[idx].X;
                        stop.DestZ = places[idx].Z;
                    }

                    if (AltitudeProbe && tick % AltitudeProbeEveryTicks == 0)
                    {
                        ProbeAltitude(cluster.Realm, cluster.GetEntityId(idx), in places[idx], in motions[idx],
                            in cluster.GetReadOnlySpan(Player.Proximity)[idx]);
                    }

                    skipped++;
                    continue;
                }

                ref var state = ref states[idx];
                ref var move = ref motions[idx];
                var x = places[idx].X;
                var z = places[idx].Z;

                // Down: it lies where it fell until its combat pass revives it. Nothing here moves it, chats or decides.
                if (vitals[idx].Down != Downed.Up)
                {
                    continue;
                }

                // A fighter of the crowd draws a pistol, and puts it away once it is doing anything else: its stance is what every client draws in its
                // hand and swings on each blow. Its shots stay its Activity's (PlayerCombatTick), at the crowd's own range.
                var wantStance = state.Activity == PlayerActivity.Combat ? Stances.Pistol : Stances.None;
                if (state.Stance != wantStance)
                {
                    state.Stance = wantStance;
                    pushSlots |= 1UL << idx;
                }

                // Per player per tick, unlike the creatures': a player draws on nearly every branch below, and there are orders of magnitude fewer of them.
                var key = cluster.GetEntityId(idx).EntityKey;

                // SWG-09: the world talks to itself, so the 50 m fan-out is something you can watch rather than a counter.
                // A hashed cadence rather than a per-player timer: it needs no state, no component field and no extra
                // write, and it spreads the utterances across ticks instead of bunching them on a shared countdown.
                if (Hash01(Salt(tick, key, 0x5A17C8A7u)) < chatChance)
                {
                    TatooineReplication.Speak(
                        ctx,
                        cluster.GetEntityId(idx),
                        in ChatLines.For(state.Activity, Salt(tick, key, 0x0C4A771Eu)),
                        x,
                        z,
                        realm);
                }

                if (state.ActivityTicks > 0)
                {
                    state.ActivityTicks = Math.Max(0, state.ActivityTicks - k);

                    // A fight ends on its objective (lair-objectives.md L-4, plan G2): a fallen lair is replaced by the next of its point of interest, or
                    // the fight is over and the fighter decides again; one still standing when the time runs out is given up, and counted as such.
                    // Only a fight that HAS an objective: a fight-back (CombatResolve's Answer) and the world's first fighters are Combat too, with a
                    // MissionX/Z that is no lair (review, 2026-10-26).
                    if (state.Activity == PlayerActivity.Combat && routes[idx].Objective == 1)
                    {
                        if (state.ActivityTicks == 0)
                        {
                            if (ObjectiveStands(ctx, realm, state.MissionX, state.MissionZ))
                            {
                                objectives.GivenUp++;
                            }

                            routes[idx].Objective = 0;
                        }
                        else if ((tick + key) % objectiveEvery < k && !KeepObjective(ctx, ref state, ref move, realm, x, z, ref objectives))
                        {
                            routes[idx].Objective = 0;
                            state.ActivityTicks = 0;
                            move.VelX = 0f;
                            move.VelZ = 0f;
                            pushSlots |= 1UL << idx;
                            continue;
                        }
                    }

                    // To, through or out of a door: its own waypoints and the crossing test (SimBridge.Portals).
                    if (ThinkPortal(ref state, ref move, ref routes[idx], realm, cluster.GetEntityId(idx), x, z, k, places[idx].HalfExtent))
                    {
                        pushSlots |= 1UL << idx;
                        continue;
                    }

                    if (state.Activity is PlayerActivity.Travelling or PlayerActivity.Roaming or PlayerActivity.Combat or PlayerActivity.ToShuttle)
                    {
                        var dx = move.DestX - x;
                        var dz = move.DestZ - z;
                        var reach = MathF.Max(5f, k * move.SpeedMps * MetresPerTick);
                        if ((dx * dx) + (dz * dz) < reach * reach)
                        {
                            // Arrived. A traveller stops rather than running past; a fighter stays put and swings.
                            move.VelX = 0f;
                            move.VelZ = 0f;
                            if (state.Activity == PlayerActivity.ToShuttle)
                            {
                                // At the port: queue. The Shuttle system boards it while the shuttle is down; the timer is how long it waits
                                // before giving up and deciding something else.
                                state.Activity = PlayerActivity.AwaitingShuttle;
                                state.ActivityTicks = ShuttleWaitTicks;
                                pushSlots |= 1UL << idx;
                            }
                            else if (state.Activity != PlayerActivity.Combat)
                            {
                                state.ActivityTicks = 0;
                            }
                        }
                        else
                        {
                            // Round the buildings of a city it is walking through, straight across the desert.
                            SteerRouted(ref routes[idx], ref move, realm, x, z, k);
                        }
                    }

                    continue;
                }

                if (IsDungeonRealm(realm))
                {
                    continue;   // a dungeon party waits for its dungeon to send it home (G2)
                }

                if (realm >= _config.Planets)
                {
                    // In an interior and done there: walk out through its way out.
                    LeaveRoom(ref state, ref move, cluster.GetEntityId(idx), realm, x, z, places[idx].HalfExtent);
                    pushSlots |= 1UL << idx;
                    continue;
                }

                // A new decision: whatever the last fight's objective was, it is not this one's.
                routes[idx].Objective = 0;
                var roll = Hash01(Salt(tick, key, 0xC2B2AE35u));
                if (roll < 0.40f
                    && TryWalkToPortal(ref state, ref move, x, z, realm, Salt(tick, key, 0x7F4A7C15u), Salt(tick, key, 0x2C1B3C6Du)))
                {
                    // Into a building (Realms G1b): walking to its door, where the crossing is queued.
                }
                else if (roll < 0.40f)
                {
                    // Idle in a city. Stationary, so free to the fence — and still expensive to every awareness query.
                    state.Activity = PlayerActivity.Idle;
                    state.ActivityTicks = (20 * hz) + (int)(Hash01(Salt(tick, key, 0x27D4EB2Fu)) * 100 * hz);
                    move.VelX = 0f;
                    move.VelZ = 0f;
                }
                else if (roll < 0.60f
                         && TryTakeShuttle(ref state, ref move, x, z, realm, Salt(tick, key, 0x3C6EF372u), Salt(tick, key, 0x165667B1u)))
                {
                    // Taking the shuttle (#910): walking to this city's port, where the Shuttle system will board it.
                }
                else if (roll < 0.60f)
                {
                    // Travel to another city — the long legs across open desert, which is where cell crossings come from.
                    var c = PickCity(Salt(tick, key, 0x165667B1u));
                    var jitter = Hash01(Salt(tick, key, 0x9E3779B1u)) * c.Radius;
                    var ang = Hash01(Salt(tick, key, 0x61C88647u)) * MathF.PI * 2f;
                    move.DestX = c.X + (MathF.Cos(ang) * jitter);
                    move.DestZ = c.Z + (MathF.Sin(ang) * jitter);

                    // Roughly half of travel was mounted or in a speeder: 12 m/s against 5 on foot, and the difference
                    // shows up directly as a cell-crossing rate.
                    move.SpeedMps = Hash01(Salt(tick, key, 0x2545F491u)) < 0.5f
                        ? TatooineData.PlayerRunSpeedMps
                        : TatooineData.PlayerMountSpeedMps;
                    state.Activity = PlayerActivity.Travelling;
                    state.ActivityTicks = 200 * hz;
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                }
                else if (roll < 0.82f && TryTakeObjective(ctx, ref state, ref move, ref routes[idx], realm, x, z, Salt(tick, key, 0x85EBCA77u)))
                {
                    // Go and fight (lair-objectives.md): where lairs are left unattended, the nearest standing wild lair there as the objective. The crowd
                    // takes no missions (economy-foundation.md § 1); with no lair to send it to, it roams instead — below.
                    objectives.Taken++;
                }
                else
                {
                    // Roaming: wander near where you already are — surveying, harvesting, looking around.
                    var ang = Hash01(Salt(tick, key, 0xCC9E2D51u)) * MathF.PI * 2f;
                    var r = (200f + (Hash01(Salt(tick, key, 0x1B873593u)) * 800f)) * _config.ContentScale;
                    var half = _config.WorldEdgeM * 0.48f;
                    move.DestX = Math.Clamp(x + (MathF.Cos(ang) * r), -half, half);
                    move.DestZ = Math.Clamp(z + (MathF.Sin(ang) * r), -half, half);
                    move.SpeedMps = TatooineData.PlayerRunSpeedMps;
                    state.Activity = PlayerActivity.Roaming;
                    state.ActivityTicks = 60 * hz;
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                }

                // Every branch above assigns an activity.
                pushSlots |= 1UL << idx;
            }

            if (pushSlots != 0)
            {
                TatooineReplication.Replicate(in cluster, pushSlots);
            }
        }

        if (skipped != 0)
        {
            Interlocked.Add(ref _possessedSkipped, skipped);
        }

        Publish(in objectives);
    }

    private (float X, float Z, float Radius) PickCity(uint salt)
    {
        if (_index.Cities.Count == 0)
        {
            return (0f, 0f, 100f);
        }

        var c = _index.Cities[PickCityIndex(salt)];
        return (c.X, c.Z, c.Radius);
    }

    /// <summary>A city index, weighted by the share of players who call each city home. Requires at least one city.</summary>
    private int PickCityIndex(uint salt)
    {
        var roll = Hash01(salt);
        var acc = 0f;
        for (var i = 0; i < _index.Cities.Count; i++)
        {
            acc += _index.Cities[i].PlayerWeight;
            if (roll <= acc)
            {
                return i;
            }
        }

        return _index.Cities.Count - 1;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Move
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Integrate creature positions. The only creature system that writes a placement component.</summary>
    public void CreatureMoveTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var half = _config.WorldEdgeM * 0.5f;
        var batched = _config.BatchedSpatialWrites;
        var dormancy = _config.DormancyTicks > 0;
        Span<CreaturePlacement> next = stackalloc CreaturePlacement[64];

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
            var motions = cluster.GetReadOnlySpan(Creature.Move);
            var brains = cluster.GetReadOnlySpan(Creature.Ai);

            // Realms G2: a realm at divisor N reaches this system once in N ticks, so its creatures cover N ticks' ground (1 at full rate).
            var k = ctx.Realms.TicksPerVisit(cluster.Realm);

            var moved = 0UL;
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                var move = motions[idx];
                var ai = brains[idx];
                var p = places[idx];
                var h = p.HalfExtent;
                float x, z;

                if ((move.VelX == 0f && move.VelZ == 0f) || ai.Mode == AiMode.Dead)
                {
                    continue;
                }
                else
                {
                    x = Math.Clamp(p.X + (move.VelX * k), -half + h, half - h);
                    z = Math.Clamp(p.Z + (move.VelZ * k), -half + h, half - h);
                }

                // WriteSpatial rather than a plain span write: the barrier flags migration and AABB growth inline, which
                // is what makes SetSpatialBarrierOnly correct for this archetype and lets the fence skip its slot scan.
                var nb = default(CreaturePlacement);
                var gy = GroundAt(cluster.Realm, x, z);
                Slow(cluster.Realm, in p, ref x, ref z, ref gy, h, half);
                nb.SetAt(x, z, gy, h);
                if (batched)
                {
                    next[idx] = nb;
                }
                else
                {
                    cluster.WriteSpatial(Creature.Bounds, idx, nb);
                }

                moved |= 1UL << idx;
            }

            // The moved slots in one call: the barrier's bookkeeping once per cluster rather than once per creature.
            if (batched && moved != 0)
            {
                cluster.WriteSpatial(Creature.Bounds, moved, next);
            }

            // WriteSpatial raises no dirty bit by design, so under dormancy a cluster whose creatures only MOVE looks clean to the fence's sweep, is put
            // to sleep, stops being dispatched, and freezes in place. Marking the column is the documented contract for combining the two. Once per
            // CLUSTER, and only for a cluster that actually moved something — marking unconditionally would keep every cluster awake forever, which is
            // the same as not having dormancy at all.
            if (dormancy && moved != 0)
            {
                cluster.MarkDirty(Creature.Bounds);
            }
        }
    }

    /// <summary>Integrate player positions.</summary>
    public void PlayerMoveTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var half = _config.WorldEdgeM * 0.5f;
        var batched = _config.BatchedSpatialWrites;
        var tick = ctx.TickNumber;
        Span<PlayerPlacement> next = stackalloc PlayerPlacement[64];

        // The static neighbourhood's scratch, per worker and allocated once; see SimBridge.Proximity.cs. Taken outside the
        // cluster loop because one cluster is one batch and every cluster reuses these buffers.
        var prox = CollisionDisabled ? null : (_proximity ??= new ProximityScratch());

        // Hoisted: read once for the whole tick rather than per body, beside the controller-kind test it widens.
        // See SimConfig.CollideCrowd -- off in every normal run, and the only way to put 3 000 bodies on this path.
        var collideCrowd = _config.CollideCrowd;

        // Read once per worker pass, not per body: a disc taken before the last static placement is retired, and the value
        // only ever moves when something fixed is created, destroyed or moved.
        var generation = Volatile.Read(ref _structureGeneration);
        var audit = prox != null && _config.CollisionAudit;

        // Bodies a door or a shuttle put somewhere else this tick, still in their old realm's cluster until the fence: see _landedThisTick.
        var landed = LandedThisTick;
        long held = 0;
        long refreshed = 0;
        long batches = 0;
        long neighbours = 0;
        long contacts = 0;
        long overflows = 0;
        long unresolved = 0;

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
            var motions = cluster.GetReadOnlySpan(Player.Move);
            var controls = prox == null ? default : cluster.GetReadOnlySpan(Player.Control);
            var k = ctx.Realms.TicksPerVisit(cluster.Realm);   // Realms G2, as for creatures

            // The discs, handed out on the first body of this cluster that has actually moved and not before: a cluster of
            // parked players must not pay for a writable span it never writes.
            var discs = Span<PlayerProximity>.Empty;
            var pending = 0;

            var moved = 0UL;
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                var move = motions[idx];

                // A14b: the tick a client's intent changed in is split between the velocity before the change and the one after, so that both sides
                // integrate the same duration. The velocity carries the whole tick and this is the remainder owed to the old one, stamped with the tick it
                // belongs to (TatooineReplication.SteerTo). Read BEFORE the standing-still test, because the commonest bias of all belongs to a STOP,
                // whose new velocity is exactly zero — testing first would drop the last fraction of every run a player ever makes.
                var owed = move.BiasTick == tick;
                var biasX = owed ? move.BiasX : 0f;
                var biasZ = owed ? move.BiasZ : 0f;
                if (move.VelX == 0f && move.VelZ == 0f && biasX == 0f && biasZ == 0f)
                {
                    continue;
                }

                if (landed != null && landed.Contains(cluster.GetEntityId(idx)))
                {
                    continue;   // stands on its landing until the fence has moved it into its new realm
                }

                var p = places[idx];
                var h = p.HalfExtent;

                // The bias is one tick's remainder and is NOT scaled by `k`: a realm visited every k-th tick moves k ticks of velocity in one visit, but
                // the split happened once.
                var x = Math.Clamp(p.X + (move.VelX * k) + biasX, -half + h, half - h);
                var z = Math.Clamp(p.Z + (move.VelZ * k) + biasZ, -half + h, half - h);
                var gy = GroundAt(cluster.Realm, x, z);

                // The floor this body is standing on, if the disc it holds remembers one; -inf until a refresh says so.
                var discSurface = float.NegativeInfinity;
                Slow(cluster.Realm, in p, ref x, ref z, ref gy, h, half);

                // The step is settled now: integrated, held inside the world and shortened by the grade. Everything a fixed
                // thing has to say about it is said here, on this value, which is why collision lives in the move rather
                // than in a system of its own.
                // A non-finite step cannot be collided and must not be handed to the batch: the query refuses a member that is
                // not finite by throwing, and an exception out of a system body aborts the tick. A client intent carrying NaN
                // is the only way to get one here, and dropping it back to the uncollided path is the same answer the
                // simulation gave before collision existed.
                if (prox != null && (collideCrowd || controls[idx].Kind != ControllerKind.Scripted) && float.IsFinite(x) && float.IsFinite(z))
                {
                    if (discs.IsEmpty)
                    {
                        discs = cluster.GetSpan(Player.Proximity);
                    }

                    if (!DiscHolds(in discs[idx], cluster.Realm, generation, x, z, h))
                    {
                        // Out of its disc: this body joins the cluster's batch and is written once the query comes back.
                        prox.Slot[pending] = idx;
                        prox.Half[pending] = h;
                        prox.CandX[pending] = x;
                        prox.CandZ[pending] = z;

                        // Where it came from and how high it stood: the resolver walks the path rather than testing
                        // the endpoint, and decides what each box IS against the feet that meet it.
                        prox.FromX[pending] = places[idx].X;
                        prox.FromZ[pending] = places[idx].Z;
                        prox.FeetY[pending] = places[idx].Y;
                        prox.Count[pending] = 0;
                        prox.Overflow[pending] = false;
                        prox.Probe[pending] = new BSphere2F
                        {
                            CenterX = x,
                            CenterY = z,
                            Radius = h + ProbeReachM,
                        };
                        pending++;
                        continue;
                    }

                    held++;
                    discSurface = discs[idx].SurfaceY;
                    if (audit)
                    {
                        // The held path matters MORE to the oracle than the refreshed one: a disc that wrongly decided
                        // nothing was near is the failure the disc's own bookkeeping cannot see.
                        Audit(ctx.Accessor, cluster.Realm, x, z, h, places[idx].Y);
                    }
                }

                var nb = default(PlayerPlacement);

                // A held disc remembers the floor the body is standing on; without it the body drops to the terrain
                // and the floor becomes a wall. Bodies with no disc (scripted, or collision off) keep the terrain.
                nb.SetAt(x, z, discSurface > gy ? discSurface : gy, h);
                if (batched)
                {
                    next[idx] = nb;
                    moved |= 1UL << idx;
                }
                else
                {
                    cluster.WriteSpatial(Player.Bounds, idx, nb);
                }
            }

            // One query for the whole cluster. A cluster's occupancy word is 64 bits and the batched query takes 64
            // members, so the batch boundary IS the cluster boundary -- and because clusters are spatial, these bodies are
            // co-located, which is the case the batch exists for: the cells and clusters they share are opened once.
            if (pending > 0)
            {
                // One member is the common case -- a single client body in a cluster of scripted ones -- and the batch zeroes
                // several kilobytes of inline member buffers per call. The single enumerator costs none of that.
                var sink = new ProximitySink(prox);
                if (pending == 1)
                {
                    var probe0 = prox.Probe[0];
                    var e = Dbe.ClusterSpatialQuery<WorldObject>(cluster.Realm).Radius(in probe0);
                    try
                    {
                        while (e.MoveNext())
                        {
                            // Copied out because the enumerator's Current is a property returning by value, and the sink
                            // takes its hit by reference.
                            var hit = e.Current;
                            sink.Hit(0, in hit);
                        }
                    }
                    finally
                    {
                        e.Dispose();
                    }
                }
                else
                {
                    Dbe.ClusterSpatialQuery<WorldObject>(cluster.Realm).ForEachInRadius(prox.Probe.AsSpan(0, pending), ref sink);
                }

                batches++;
                refreshed += pending;

                // Loop-invariant, and it carries the one-entry memo that keeps a body inside a footprint from
                // re-opening its structure on every pass; see StructureShapes.
                var shapes = new StructureShapes(ctx.Accessor, AssetShapes);
                for (var m = 0; m < pending; m++)
                {
                    var slot = prox.Slot[m];
                    var mh = prox.Half[m];
                    var mx = prox.CandX[m];
                    var mz = prox.CandZ[m];
                    neighbours += prox.Count[m];
                    if (prox.Overflow[m])
                    {
                        overflows++;
                    }

                    var boxes = prox.Neighbours(m);
                    if (PushOut(boxes, mh, half, ref mx, ref mz, out var stuck, ref shapes, prox.FeetY[m], prox.FromX[m], prox.FromZ[m]))
                    {
                        contacts++;
                        if (stuck)
                        {
                            unresolved++;
                        }
                    }

                    // <b>A door, before the position is written.</b> The step is resolved now -- pushed out of whatever it
                    // walked into -- so this is the first and only moment both ends of it are known and both are legal
                    // places to stand. A body stopped by a door frame never reaches the plane and so cannot cross, which
                    // is the behaviour wanted: you go through a doorway, not through a wall beside one.
                    //
                    // The crossing only for a body a client drives: the scripted crowd crosses on the same test in its own
                    // think (ThinkPortal), and running both would queue it twice. The disc bound for every body.
                    var probe = prox.Probe[m];
                    var doorClear = InteriorsActive
                        ? DoorsAround(cluster.Realm, in probe, controls[slot].Kind != ControllerKind.Scripted ? cluster.GetEntityId(slot) : EntityId.Null,
                            mh, prox.FromX[m], prox.FromZ[m], mx, mz)
                        : float.PositiveInfinity;

                    // Re-sampled because the push moved the body, for the reason the grade step re-samples: the altitude
                    // written has to be the ground under the position actually written -- and the ground includes any
                    // published surface the body is standing ON, which the terrain alone knows nothing about.
                    var mterrain = GroundAt(cluster.Realm, mx, mz);
                    var mgy = GroundOnShapes(boxes, mh, mx, mz, mterrain, prox.FeetY[m], ref shapes);
                    discs[slot] = new PlayerProximity
                    {
                        AnchorX = mx,
                        AnchorZ = mz,

                        // Only the surface, not the ground: the terrain under a body changes as it walks and is
                        // re-sampled every tick, while the deck it is standing on does not.
                        SurfaceY = mgy > mterrain ? mgy : float.NegativeInfinity,

                        // A full set is still correct, because the sink keeps the NEAREST sixteen: every box it dropped is
                        // farther than every box it held, hence farther than the disc this takes the minimum of.
                        ClearM = MathF.Max(0f, MathF.Min(
                            DiscFor(boxes, probe.CenterX, probe.CenterY, probe.Radius, mx, mz, ref shapes, prox.Overflow[m], mgy),
                            doorClear)),
                        Realm = cluster.Realm.Value,
                        Generation = (ushort)generation,
                    };

                    if (audit)
                    {
                        Audit(ctx.Accessor, cluster.Realm, mx, mz, mh, mgy);
                    }

                    var mb = default(PlayerPlacement);
                    mb.SetAt(mx, mz, mgy, mh);
                    if (batched)
                    {
                        next[slot] = mb;
                        moved |= 1UL << slot;
                    }
                    else
                    {
                        cluster.WriteSpatial(Player.Bounds, slot, mb);
                    }
                }

                pending = 0;
            }

            // The moved slots in one call, and only when something moved: 40 % of players are parked idle in a city, so
            // without the mask test most clusters pay the barrier's bookkeeping for an empty write.
            if (batched && moved != 0)
            {
                cluster.WriteSpatial(Player.Bounds, moved, next);
            }
        }

        if (prox != null)
        {
            // Once per worker rather than once per body: six atomics on a parallel system body, not six per step.
            Interlocked.Add(ref _discsHeld, held);
            Interlocked.Add(ref _discsRefreshed, refreshed);
            Interlocked.Add(ref _proximityBatches, batches);
            Interlocked.Add(ref _neighboursSeen, neighbours);
            Interlocked.Add(ref _contacts, contacts);
            Interlocked.Add(ref _discOverflows, overflows);
            Interlocked.Add(ref _unresolvedContacts, unresolved);
        }
    }

    /// <summary>
    /// Integrate the small fraction of city NPCs that shuffle around inside a shop.
    /// </summary>
    /// <remarks>
    /// Its own system rather than folded into the creature pass, because the population is an order larger and almost
    /// entirely stationary: the question this asks the engine is what an archetype costs when 88 % of its entities never
    /// move and the other 12 % move two metres.
    /// </remarks>
    public void NpcMoveTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var tick = ctx.TickNumber;
        var batched = _config.BatchedSpatialWrites;

        // A townsperson speaks about this often. They are the dense population, so this is the figure that decides
        // whether a town sounds alive; the players' cadence is slower because there are far fewer of them per square.
        var npcChatChance = 1f / MathF.Max(1f, NpcChatEverySeconds * _config.TickRateHz);
        Span<NpcPlacement> next = stackalloc NpcPlacement[64];

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<CityNpc>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<CityNpc>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits0 = cluster.OccupancyBits;
            if (bits0 == 0)
            {
                continue;
            }

            var places = cluster.GetReadOnlySpan(CityNpc.Bounds);

            // Read-only: this loop only ever READS Mode. Nothing here changes an NPC's mode, so the mutable span it used to take claimed a change on
            // every tick of every city in the world for no write at all.
            var brains = cluster.GetReadOnlySpan(CityNpc.Ai);
            var timers = cluster.GetSpan(CityNpc.Timers);
            var motions = cluster.GetSpan(CityNpc.Move);
            var k = ctx.Realms.TicksPerVisit(cluster.Realm);   // Realms G2, as for creatures

            var moved = 0UL;
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                // SWG-09: the town talks. BEFORE the wander gate, because the NPCs standing still are most of them and a
                // stall-holder who never speaks is exactly the one you would expect to. This is what makes a 50 m fan-out
                // visible from a camera — players are one per few hundred square metres, and a town is dozens in one
                // square.
                // The key once, not three times: this roll is paid by EVERY townsperson every tick — that is the point of
                // its placement — and the NPCs are the demo's densest population, so a repeated cluster lookup here is the
                // one that multiplies. The surrounding loops hoist theirs for the same reason.
                var npcId = cluster.GetEntityId(idx);
                if (Hash01(Salt(tick, npcId.EntityKey, 0x70171CE1u)) < npcChatChance)
                {
                    TatooineReplication.Speak(
                        ctx,
                        npcId,
                        in ChatLines.Townsperson(Salt(tick, npcId.EntityKey, 0x7A1C0FFEu)),
                        places[idx].X,
                        places[idx].Z,
                        cluster.Realm.Value);
                }

                if (brains[idx].Mode != AiMode.Wander)
                {
                    continue;
                }

                ref var ai = ref timers[idx];
                ref var move = ref motions[idx];
                var p = places[idx];

                // The same amble-then-stand cycle the creatures use, and for the same reason: the countdown this replaced wrote the brain on every tick of
                // every wandering NPC, and the steer below it wrote a new position on every tick as well. A city NPC shuffles between stalls; it does not
                // march. Only 12 % of NPCs wander at all (WorldBuilder), so this is a small population writing continuously rather than a large one.
                if (tick < ai.MoveUntilTick)
                {
                    // Mid-leg: the velocity already points at the destination.
                }
                else if (tick < ai.RestUntilTick)
                {
                    if (move.VelX != 0f || move.VelZ != 0f)
                    {
                        move.VelX = 0f;
                        move.VelZ = 0f;
                    }

                    continue;
                }
                else
                {
                    var brain = brains[idx];
                    var key = cluster.GetEntityId(idx).EntityKey;
                    var ang = Hash01(Salt(tick, key, 0x846CA68Bu)) * MathF.PI * 2f;
                    move.DestX = brain.HomeX + (MathF.Cos(ang) * brain.LeashRadius);
                    move.DestZ = brain.HomeZ + (MathF.Sin(ang) * brain.LeashRadius);
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, p.X, p.Z, move.DestX, move.DestZ);

                    var legTicks = 1 + (int)(Hash01(Salt(tick, key, 0x7FEB352Du)) * _wanderLegTicks);
                    ai.MoveUntilTick = tick + legTicks;
                    ai.RestUntilTick = ai.MoveUntilTick + (legTicks * WanderRestToMoveRatio);
                }

                if (move.VelX == 0f && move.VelZ == 0f)
                {
                    continue;
                }

                var nb = default(NpcPlacement);
                var nx = p.X + (move.VelX * k);
                var nz = p.Z + (move.VelZ * k);
                var ngy = GroundAt(cluster.Realm, nx, nz);
                Slow(cluster.Realm, in p, ref nx, ref nz, ref ngy, p.HalfExtent, _config.WorldEdgeM * 0.5f);
                nb.SetAt(nx, nz, ngy, p.HalfExtent);
                if (batched)
                {
                    next[idx] = nb;
                    moved |= 1UL << idx;
                }
                else
                {
                    cluster.WriteSpatial(CityNpc.Bounds, idx, nb);
                }
            }

            // Only 12 % of city NPCs wander at all, so the empty-mask case is the common one here.
            if (batched && moved != 0)
            {
                cluster.WriteSpatial(CityNpc.Bounds, moved, next);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Awareness
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Interest management: for every player, everything within the awareness radius.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the query the study is about.</b> A real server runs it to decide what to tell each client, and
    /// it is the only query whose cost scales with how crowded the world is around the ASKER rather than with how many
    /// entities exist. In Mos Eisley it returns hundreds of buildings and NPCs; in the Dune Sea it returns almost
    /// nothing. The spread between those two is what a cell-size sweep is really tuning.</para>
    /// <para>Four archetypes are queried separately because the engine's cluster query is per-archetype — which is also
    /// what the real server does: an interest set is assembled from several object types.</para>
    /// </remarks>
    public void AwarenessTick(TickContext ctx) => AwarenessTick(ctx, null);

    /// <summary>
    /// Interest management for one target archetype, or for all four when <paramref name="only"/> is null.
    /// </summary>
    public void AwarenessTick(TickContext ctx, AwarenessTarget? only)
    {
        var chunkStart = _config.ChunkStats ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
        long queries = 0;
        long hits = 0;
        var probe = _config.WorkProbe && ctx.TickNumber >= _config.WarmTicks;
        Span<long> work = stackalloc long[WorkProbeCounters * 4];
        work.Clear();
        var batch = _config.AwarenessApi == AwarenessApi.Batch;
        Span<BSphere2F> members = stackalloc BSphere2F[64];
        Span<int> counts = stackalloc int[64];

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
            if (batch)
            {
                // The cluster's players as one batch per target archetype: the same spheres, the same counts, one walk over the cells they share.
                var m = 0;
                var sampled = 0UL;
                for (var b = bits0; b != 0; b &= b - 1)
                {
                    var idx = BitOperations.TrailingZeroCount(b);
                    members[m] = new BSphere2F { CenterX = places[idx].X, CenterY = places[idx].Z, Radius = AwarenessRadius };
                    if (probe && ((cluster.ChunkId * 64) + idx) % WorkProbeSampleEvery == 0)
                    {
                        sampled |= 1UL << m;
                    }

                    m++;
                }

                AwarenessBatch(cluster.Realm, members[..m], counts[..m], only, sampled, work, ref queries, ref hits);
                continue;
            }

            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                var sphere = new BSphere2F { CenterX = places[idx].X, CenterY = places[idx].Z, Radius = AwarenessRadius };
                var sample = probe && ((cluster.ChunkId * 64) + idx) % WorkProbeSampleEvery == 0;
                if (only is null or AwarenessTarget.Structures)
                {
                    var n = CountInRadius<WorldObject>(in sphere, cluster.Realm);
                    hits += n;
                    queries++;
                    if (sample)
                    {
                        ProbeWork(0, StateOf<WorldObject>(), in sphere, n, work);
                    }
                }

                if (only is null or AwarenessTarget.Creatures)
                {
                    var n = CountInRadius<Creature>(in sphere, cluster.Realm);
                    hits += n;
                    queries++;
                    if (sample)
                    {
                        ProbeWork(1, StateOf<Creature>(), in sphere, n, work);
                    }
                }

                if (only is null or AwarenessTarget.Npcs)
                {
                    var n = CountInRadius<CityNpc>(in sphere, cluster.Realm);
                    hits += n;
                    queries++;
                    if (sample)
                    {
                        ProbeWork(2, StateOf<CityNpc>(), in sphere, n, work);
                    }
                }

                if (only is null or AwarenessTarget.Players)
                {
                    var n = CountInRadius<Player>(in sphere, cluster.Realm);
                    hits += n;
                    queries++;
                    if (sample)
                    {
                        ProbeWork(3, StateOf<Player>(), in sphere, n, work);
                    }
                }
            }
        }

        if (queries != 0)
        {
            Interlocked.Add(ref _awarenessQueries, queries);
            Interlocked.Add(ref _awarenessHits, hits);
        }

        if (probe)
        {
            FoldWork(work);
        }

        if (_config.ChunkStats && ctx.TickNumber >= _config.WarmTicks)
        {
            RecordChunk(ctx.TickNumber, chunkStart, System.Diagnostics.Stopwatch.GetTimestamp(), hits, queries);
        }
    }

    // Each drain is its own NoInlining method, so a run never JIT-compiles the drains it does not use. That lets an A/B run an engine build that lacks
    // the newer query API under the same host binary.
    private long CountInRadius<TArch>(in BSphere2F sphere, RealmId realm) where TArch : Archetype<TArch>, new() => _config.AwarenessApi switch
    {
        AwarenessApi.MoveNext => CountByMoveNext<TArch>(in sphere, realm),
        AwarenessApi.Fill => CountByFill<TArch>(in sphere, realm),
        // A lone query — the shuttle port probe — has no batch to join: the batch arm counts it as the count arm does, so the two arms differ in the
        // awareness drain and nowhere else.
        AwarenessApi.Count or AwarenessApi.Batch => CountByCount<TArch>(in sphere, realm),
        _ => throw new ArgumentOutOfRangeException(nameof(_config.AwarenessApi), _config.AwarenessApi, "no drain for this awareness API"),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long CountByCount<TArch>(in BSphere2F sphere, RealmId realm) where TArch : Archetype<TArch>, new()
    {
        var e = Dbe.ClusterSpatialQuery<TArch>(realm).Radius(in sphere);
        try
        {
            return e.Count();
        }
        finally
        {
            e.Dispose();
        }
    }

    /// <summary>One per worker thread, allocated once: a stackalloc here would zero 4.6 KB on every query and bill it to Fill.</summary>
    [ThreadStatic]
    private static ClusterSpatialQueryResult[] _fillBuffer;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long CountByFill<TArch>(in BSphere2F sphere, RealmId realm) where TArch : Archetype<TArch>, new()
    {
        var buffer = _fillBuffer ??= new ClusterSpatialQueryResult[64];
        var e = Dbe.ClusterSpatialQuery<TArch>(realm).Radius(in sphere);
        try
        {
            long n = 0;
            int got;
            while ((got = e.Fill(buffer)) > 0)
            {
                n += got;
            }

            return n;
        }
        finally
        {
            e.Dispose();
        }
    }

    /// <summary>
    /// <see cref="AwarenessApi.Batch"/>: one <c>CountRadius</c> per target archetype for a source cluster's players, inside the epoch
    /// scope RT-01 supplies. Each player's
    /// count is exactly its own query's, so the statistics are those of the per-player path.
    /// </summary>
    private void AwarenessBatch(
        RealmId realm,
        ReadOnlySpan<BSphere2F> members,
        Span<int> counts,
        AwarenessTarget? only,
        ulong sampled,
        Span<long> work,
        ref long queries,
        ref long hits)
    {
        if (only is null or AwarenessTarget.Structures)
        {
            hits += CountBatch<WorldObject>(realm, members, counts, 0, sampled, work);
            queries += members.Length;
        }

        if (only is null or AwarenessTarget.Creatures)
        {
            hits += CountBatch<Creature>(realm, members, counts, 1, sampled, work);
            queries += members.Length;
        }

        if (only is null or AwarenessTarget.Npcs)
        {
            hits += CountBatch<CityNpc>(realm, members, counts, 2, sampled, work);
            queries += members.Length;
        }

        if (only is null or AwarenessTarget.Players)
        {
            hits += CountBatch<Player>(realm, members, counts, 3, sampled, work);
            queries += members.Length;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long CountBatch<TArch>(RealmId realm, ReadOnlySpan<BSphere2F> members, Span<int> counts, int target, ulong sampled, Span<long> work)
        where TArch : Archetype<TArch>, new()
    {
        Dbe.ClusterSpatialQuery<TArch>(realm).CountRadius(members, counts);
        long n = 0;
        for (var j = 0; j < members.Length; j++)
        {
            n += counts[j];
        }

        for (var s = sampled; s != 0; s &= s - 1)
        {
            var j = BitOperations.TrailingZeroCount(s);
            ProbeWork(target, StateOf<TArch>(), in members[j], counts[j], work);
        }

        return n;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long CountByMoveNext<TArch>(in BSphere2F sphere, RealmId realm) where TArch : Archetype<TArch>, new()
    {
        var e = Dbe.ClusterSpatialQuery<TArch>(realm).Radius(in sphere);
        try
        {
            long n = 0;
            while (e.MoveNext())
            {
                n++;
            }

            return n;
        }
        finally
        {
            e.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Economy
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Harvesters extracting and factories manufacturing.
    /// </summary>
    /// <remarks>
    /// <para>Periods come from the game's own arithmetic: manufacturing is <c>complexity x 8</c> seconds per unit and a
    /// harvester's rate is units per minute, so both are tens of seconds to minutes — hundreds to thousands of ticks
    /// here. Countdowns are staggered at spawn, so the economy is a low continuous trickle rather than a once-a-minute
    /// spike the median tick never sees and the p99 always does.</para>
    /// <para>The interesting property for the engine is that this walks the LARGEST archetype in the world every tick to
    /// find the few hundred entities with anything to do. That is a real shape — a game server does exactly this — and
    /// it is precisely the case a change filter would want to eliminate.</para>
    /// </remarks>
    public void EconomyTick(TickContext ctx)
    {
        // Stopped by a client (TatooineReplication.SetPaused, a demo control), or past a measured run's last tick (Halted). The simulation does nothing;
        // replication, the session system and the engine's own stages keep running, or no client could ever ask to resume.
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        long ticked = 0;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<WorldObject>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<WorldObject>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits0 = cluster.OccupancyBits;
            if (bits0 == 0)
            {
                continue;
            }

            var structs = cluster.GetSpan(WorldObject.Struct);
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var s = ref structs[idx];
                if (s.TickPeriod == 0 || --s.TickCountdown > 0)
                {
                    continue;
                }

                s.TickCountdown = s.TickPeriod;
                s.Accumulated += s.Kind == StructureKind.Harvester ? 14 : 1;
                ticked++;
            }
        }

        if (ticked != 0)
        {
            Interlocked.Add(ref _economyTicks, ticked);
        }
    }
}
