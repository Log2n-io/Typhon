using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using Typhon.Schema.Definition;

namespace SwgTatooine;

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
        var tick = ctx.TickNumber;
        var thinkMin = WorldBuilder.AiTicksMin(_config);
        var thinkSpan = Math.Max(1, WorldBuilder.AiTicksMax(_config) - thinkMin + 1);
        long aggroQueries = 0;
        long aggroHits = 0;

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
            var vitals = cluster.GetReadOnlySpan(Creature.Vitals);
            var chunk = cluster.ChunkId;

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

                t.ThinkCooldown = thinkMin + (int)(Hash01(Salt(tick, chunk, idx, 0x51ED2701u)) * thinkSpan);

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
                        PickWanderDestination(ref move, in ai, tick, chunk, idx);
                    }
                    else
                    {
                        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, ai.HomeX, ai.HomeZ);
                    }

                    continue;
                }

                if (ai.Mode is AiMode.Pursue or AiMode.Fighting)
                {
                    // Close to weapon range, then STAND AND SHOOT.
                    //
                    // A pursuer that keeps steering once it is already in range writes a new position on every tick of every fight, which is the same
                    // defect as a creature that never rests and costs the same downstream. Real combat is a closing phase and then a stationary one: the
                    // creature walks until the target is within its weapon's reach and holds position while it attacks.
                    //
                    // The break range is deliberately wider than the attack range. At equal thresholds a creature sitting on the boundary alternates
                    // between stopping and closing on successive decisions, which writes MORE than pursuing would.
                    var dxT = move.DestX - x;
                    var dzT = move.DestZ - z;
                    var targetSq = (dxT * dxT) + (dzT * dzT);

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
                    PickWanderDestination(ref move, in ai, tick, chunk, idx);
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);

                    var legTicks = 1 + (int)(Hash01(Salt(tick, chunk, idx, 0x1B873593u)) * _wanderLegTicks);
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
                        float tx = 0f, tz = 0f;
                        while (e.MoveNext())
                        {
                            var hit = e.Current;
                            if (hit.DistanceSq < bestSq)
                            {
                                // The query result is in the engine's f64 world frame (#914); the simulation keeps f32 positions.
                                bestSq = hit.DistanceSq;
                                tx = (float)((hit.MinX + hit.MaxX) * 0.5);
                                tz = (float)((hit.MinY + hit.MaxY) * 0.5);
                                found = true;
                            }
                        }

                        if (found)
                        {
                            aggroHits++;
                            SetCreatureMode(cluster, ref brainsRw, idx, AiMode.Pursue);
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
    }

    /// <summary>Pick a new wander destination inside the leash radius.</summary>
    private static void PickWanderDestination(ref CreatureMotion move, in CreatureBrain ai, long tick, int chunk, int idx)
    {
        var a = Hash01(Salt(tick, chunk, idx, 0x9E3779B9u)) * MathF.PI * 2f;
        var r = ai.LeashRadius * 0.7f * MathF.Sqrt(Hash01(Salt(tick, chunk, idx, 0x85EBCA6Bu)));
        move.DestX = ai.HomeX + (MathF.Cos(a) * r);
        move.DestZ = ai.HomeZ + (MathF.Sin(a) * r);
    }

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
    /// Takes the two velocity components by <c>ref</c> rather than the motion component, because the three motion types
    /// are distinct for the scheduler's benefit (see the header of <c>Components.cs</c>) and a shared helper would
    /// otherwise have to be written three times.
    /// </remarks>
    private void Steer(ref float velX, ref float velZ, float speedMps, float x, float z, float destX, float destZ)
    {
        var dx = destX - x;
        var dz = destZ - z;
        var len = MathF.Sqrt((dx * dx) + (dz * dz));
        if (len < 0.001f)
        {
            velX = 0f;
            velZ = 0f;
            return;
        }

        var step = speedMps * MetresPerTick;
        if (step > len)
        {
            step = len;
        }

        velX = dx / len * step;
        velZ = dz / len * step;
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Think — players
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

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
        var tick = ctx.TickNumber;
        var hz = _config.TickRateHz;

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
            var chunk = cluster.ChunkId;
            var realm = cluster.Realm.Value;
            var k = ctx.Realms.TicksPerVisit(cluster.Realm);   // Realms G2: N ticks elapse between two visits of a strided realm at divisor N

            // Explicit replication (ADR-067): the players whose replicated activity this pass changes. Positions are pushed by WriteSpatial.
            var pushSlots = 0UL;
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var state = ref states[idx];
                ref var move = ref motions[idx];
                var x = places[idx].X;
                var z = places[idx].Z;

                if (state.ActivityTicks > 0)
                {
                    state.ActivityTicks = Math.Max(0, state.ActivityTicks - k);
                    if (state.Activity is PlayerActivity.Travelling or PlayerActivity.Roaming or PlayerActivity.Combat or PlayerActivity.ToShuttle
                        or PlayerActivity.ToPortal)
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
                            else if (state.Activity == PlayerActivity.ToPortal)
                            {
                                // At the door: queue the crossing, which TeleportSystem applies next tick. Standing still until then, so Move
                                // writes nothing that the teleport would overwrite.
                                EnterPortal(ref state, cluster.GetEntityId(idx), realm, places[idx].HalfExtent, Salt(tick, chunk, idx, 0x0D1CE5A7u));
                                pushSlots |= 1UL << idx;
                            }
                            else if (state.Activity != PlayerActivity.Combat)
                            {
                                state.ActivityTicks = 0;
                            }
                        }
                        else
                        {
                            Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
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
                    // In an interior and done there: out through the door it came in by.
                    ExitInterior(ref state, ref move, cluster.GetEntityId(idx), realm, places[idx].HalfExtent, Salt(tick, chunk, idx, 0x3A0B7C11u));
                    pushSlots |= 1UL << idx;
                    continue;
                }

                var roll = Hash01(Salt(tick, chunk, idx, 0xC2B2AE35u));
                if (roll < 0.40f
                    && TryWalkToPortal(ref state, ref move, x, z, realm, Salt(tick, chunk, idx, 0x7F4A7C15u), Salt(tick, chunk, idx, 0x2C1B3C6Du)))
                {
                    // Into a building (Realms G1b): walking to its door, where the crossing is queued.
                }
                else if (roll < 0.40f)
                {
                    // Idle in a city. Stationary, so free to the fence — and still expensive to every awareness query.
                    state.Activity = PlayerActivity.Idle;
                    state.ActivityTicks = (20 * hz) + (int)(Hash01(Salt(tick, chunk, idx, 0x27D4EB2Fu)) * 100 * hz);
                    move.VelX = 0f;
                    move.VelZ = 0f;
                }
                else if (roll < 0.60f
                         && TryTakeShuttle(ref state, ref move, x, z, realm, Salt(tick, chunk, idx, 0x3C6EF372u), Salt(tick, chunk, idx, 0x165667B1u)))
                {
                    // Taking the shuttle (#910): walking to this city's port, where the Shuttle system will board it.
                }
                else if (roll < 0.60f)
                {
                    // Travel to another city — the long legs across open desert, which is where cell crossings come from.
                    var c = PickCity(Salt(tick, chunk, idx, 0x165667B1u));
                    var jitter = Hash01(Salt(tick, chunk, idx, 0x9E3779B1u)) * c.Radius;
                    var ang = Hash01(Salt(tick, chunk, idx, 0x61C88647u)) * MathF.PI * 2f;
                    move.DestX = c.X + (MathF.Cos(ang) * jitter);
                    move.DestZ = c.Z + (MathF.Sin(ang) * jitter);

                    // Roughly half of travel was mounted or in a speeder: 12 m/s against 5 on foot, and the difference
                    // shows up directly as a cell-crossing rate.
                    move.SpeedMps = Hash01(Salt(tick, chunk, idx, 0x2545F491u)) < 0.5f
                        ? TatooineData.PlayerRunSpeedMps
                        : TatooineData.PlayerMountSpeedMps;
                    state.Activity = PlayerActivity.Travelling;
                    state.ActivityTicks = 200 * hz;
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                }
                else if (roll < 0.82f)
                {
                    // Go and fight. The destination is a point of interest, which is where the lairs are.
                    var poi = PickPoi(Salt(tick, chunk, idx, 0x85EBCA77u));
                    var ang = Hash01(Salt(tick, chunk, idx, 0x1B873593u)) * MathF.PI * 2f;
                    var r = poi.Radius * MathF.Sqrt(Hash01(Salt(tick, chunk, idx, 0xCC9E2D51u)));
                    move.DestX = poi.X + (MathF.Cos(ang) * r);
                    move.DestZ = poi.Z + (MathF.Sin(ang) * r);
                    move.SpeedMps = TatooineData.PlayerMountSpeedMps;
                    state.Activity = PlayerActivity.Combat;
                    state.ActivityTicks = 120 * hz;
                    state.MissionX = move.DestX;
                    state.MissionZ = move.DestZ;
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                }
                else
                {
                    // Roaming: wander near where you already are — surveying, harvesting, looking around.
                    var ang = Hash01(Salt(tick, chunk, idx, 0xCC9E2D51u)) * MathF.PI * 2f;
                    var r = (200f + (Hash01(Salt(tick, chunk, idx, 0x1B873593u)) * 800f)) * _config.ContentScale;
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

            TatooineReplication.Replicate(in cluster, pushSlots);
        }
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

    private (float X, float Z, float Radius) PickPoi(uint salt)
    {
        if (_index.Pois.Count == 0)
        {
            return PickCity(salt);
        }

        var i = (int)(Hash01(salt) * _index.Pois.Count) % _index.Pois.Count;
        return _index.Pois[i];
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // Move
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Integrate creature positions. The only creature system that writes a placement component.</summary>
    public void CreatureMoveTick(TickContext ctx)
    {
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
            var timers = cluster.GetReadOnlySpan(Creature.Timers);
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

                if (ai.Mode == AiMode.Wander && timers[idx].ThinkCooldown == 1 && p.X != ai.HomeX)
                {
                    // Just revived: teleport home. The largest position jump the simulation makes, and the one that
                    // forces both a cell change and a cluster-bound recomputation in the same tick.
                    x = ai.HomeX;
                    z = ai.HomeZ;
                }
                else if ((move.VelX == 0f && move.VelZ == 0f) || ai.Mode == AiMode.Dead)
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
                nb.SetAt(x, z, h);
                if (batched)
                {
                    next[idx] = nb;
                    moved |= 1UL << idx;
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
        var half = _config.WorldEdgeM * 0.5f;
        var batched = _config.BatchedSpatialWrites;
        Span<PlayerPlacement> next = stackalloc PlayerPlacement[64];

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
            var k = ctx.Realms.TicksPerVisit(cluster.Realm);   // Realms G2, as for creatures

            var moved = 0UL;
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                var move = motions[idx];
                if (move.VelX == 0f && move.VelZ == 0f)
                {
                    continue;
                }

                var p = places[idx];
                var h = p.HalfExtent;
                var x = Math.Clamp(p.X + (move.VelX * k), -half + h, half - h);
                var z = Math.Clamp(p.Z + (move.VelZ * k), -half + h, half - h);
                var nb = default(PlayerPlacement);
                nb.SetAt(x, z, h);
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

            if (batched)
            {
                cluster.WriteSpatial(Player.Bounds, moved, next);
            }
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
        var tick = ctx.TickNumber;
        var batched = _config.BatchedSpatialWrites;
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
            var chunk = cluster.ChunkId;
            var k = ctx.Realms.TicksPerVisit(cluster.Realm);   // Realms G2, as for creatures

            var moved = 0UL;
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

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
                    var ang = Hash01(Salt(tick, chunk, idx, 0x846CA68Bu)) * MathF.PI * 2f;
                    move.DestX = brain.HomeX + (MathF.Cos(ang) * brain.LeashRadius);
                    move.DestZ = brain.HomeZ + (MathF.Sin(ang) * brain.LeashRadius);
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, p.X, p.Z, move.DestX, move.DestZ);

                    var legTicks = 1 + (int)(Hash01(Salt(tick, chunk, idx, 0x7FEB352Du)) * _wanderLegTicks);
                    ai.MoveUntilTick = tick + legTicks;
                    ai.RestUntilTick = ai.MoveUntilTick + (legTicks * WanderRestToMoveRatio);
                }

                if (move.VelX == 0f && move.VelZ == 0f)
                {
                    continue;
                }

                var nb = default(NpcPlacement);
                nb.SetAt(p.X + (move.VelX * k), p.Z + (move.VelZ * k), p.HalfExtent);
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

            if (batched)
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
