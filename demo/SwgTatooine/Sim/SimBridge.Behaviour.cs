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
            var brains = cluster.GetSpan(Creature.Ai);
            var motions = cluster.GetSpan(Creature.Move);
            var vitals = cluster.GetReadOnlySpan(Creature.Vitals);
            var chunk = cluster.ChunkId;

            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var ai = ref brains[idx];
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

                if (ai.ThinkCooldown > 0)
                {
                    ai.ThinkCooldown--;
                    continue;
                }

                ai.ThinkCooldown = thinkMin + (int)(Hash01(Salt(tick, chunk, idx, 0x51ED2701u)) * thinkSpan);

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
                    ai.Mode = AiMode.Leashing;
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, ai.HomeX, ai.HomeZ);
                    continue;
                }

                if (ai.Mode == AiMode.Leashing)
                {
                    if (homeSq < 16f)
                    {
                        ai.Mode = AiMode.Wander;
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
                    // Keep closing on where the target was last seen; combat owns the transition out of these modes.
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                    continue;
                }

                var dxd = move.DestX - x;
                var dzd = move.DestZ - z;
                if ((dxd * dxd) + (dzd * dzd) < 4f)
                {
                    PickWanderDestination(ref move, in ai, tick, chunk, idx);
                }
                else
                {
                    Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
                }

                // The aggro query. A 24 m bubble against a few hundred players spread over a 16 km planet returns
                // nothing the overwhelming majority of the time — which is the point: the cost an engine has to make
                // cheap here is the NEGATIVE case, not the hit.
                if (ai.AggroRadius > 0f && vitals[idx].Health > 0)
                {
                    aggroQueries++;
                    var sphere = new BSphere2F { CenterX = x, CenterY = z, Radius = ai.AggroRadius };
                    var e = Dbe.ClusterSpatialQuery<Player>().Radius(in sphere);
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
                            ai.Mode = AiMode.Pursue;
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
                    state.ActivityTicks--;
                    if (state.Activity is PlayerActivity.Travelling or PlayerActivity.Roaming or PlayerActivity.Combat or PlayerActivity.ToShuttle)
                    {
                        var dx = move.DestX - x;
                        var dz = move.DestZ - z;
                        if ((dx * dx) + (dz * dz) < 25f)
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

                var roll = Hash01(Salt(tick, chunk, idx, 0xC2B2AE35u));
                if (roll < 0.40f)
                {
                    // Idle in a city. Stationary, so free to the fence — and still expensive to every awareness query.
                    state.Activity = PlayerActivity.Idle;
                    state.ActivityTicks = (20 * hz) + (int)(Hash01(Salt(tick, chunk, idx, 0x27D4EB2Fu)) * 100 * hz);
                    move.VelX = 0f;
                    move.VelZ = 0f;
                }
                else if (roll < 0.60f
                         && TryTakeShuttle(ref state, ref move, x, z, Salt(tick, chunk, idx, 0x3C6EF372u), Salt(tick, chunk, idx, 0x165667B1u)))
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
            }
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

                if (ai.Mode == AiMode.Wander && ai.ThinkCooldown == 1 && p.X != ai.HomeX)
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
                    x = Math.Clamp(p.X + move.VelX, -half + h, half - h);
                    z = Math.Clamp(p.Z + move.VelZ, -half + h, half - h);
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
                var x = Math.Clamp(p.X + move.VelX, -half + h, half - h);
                var z = Math.Clamp(p.Z + move.VelZ, -half + h, half - h);
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
            var brains = cluster.GetSpan(CityNpc.Ai);
            var motions = cluster.GetSpan(CityNpc.Move);
            var chunk = cluster.ChunkId;

            var moved = 0UL;
            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var ai = ref brains[idx];
                if (ai.Mode != AiMode.Wander)
                {
                    continue;
                }

                ref var move = ref motions[idx];
                var p = places[idx];
                if (ai.ThinkCooldown > 0)
                {
                    ai.ThinkCooldown--;
                }
                else
                {
                    ai.ThinkCooldown = 20 + (int)(Hash01(Salt(tick, chunk, idx, 0x7FEB352Du)) * 60);
                    var ang = Hash01(Salt(tick, chunk, idx, 0x846CA68Bu)) * MathF.PI * 2f;
                    move.DestX = ai.HomeX + (MathF.Cos(ang) * ai.LeashRadius);
                    move.DestZ = ai.HomeZ + (MathF.Sin(ang) * ai.LeashRadius);
                }

                Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, p.X, p.Z, move.DestX, move.DestZ);
                if (move.VelX == 0f && move.VelZ == 0f)
                {
                    continue;
                }

                var nb = default(NpcPlacement);
                nb.SetAt(p.X + move.VelX, p.Z + move.VelZ, p.HalfExtent);
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

                AwarenessBatch(members[..m], counts[..m], only, sampled, work, ref queries, ref hits);
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
                    var n = CountInRadius<WorldObject>(in sphere);
                    hits += n;
                    queries++;
                    if (sample)
                    {
                        ProbeWork(0, StateOf<WorldObject>(), in sphere, n, work);
                    }
                }

                if (only is null or AwarenessTarget.Creatures)
                {
                    var n = CountInRadius<Creature>(in sphere);
                    hits += n;
                    queries++;
                    if (sample)
                    {
                        ProbeWork(1, StateOf<Creature>(), in sphere, n, work);
                    }
                }

                if (only is null or AwarenessTarget.Npcs)
                {
                    var n = CountInRadius<CityNpc>(in sphere);
                    hits += n;
                    queries++;
                    if (sample)
                    {
                        ProbeWork(2, StateOf<CityNpc>(), in sphere, n, work);
                    }
                }

                if (only is null or AwarenessTarget.Players)
                {
                    var n = CountInRadius<Player>(in sphere);
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
    private long CountInRadius<TArch>(in BSphere2F sphere) where TArch : Archetype<TArch>, new() => _config.AwarenessApi switch
    {
        AwarenessApi.MoveNext => CountByMoveNext<TArch>(in sphere),
        AwarenessApi.Fill => CountByFill<TArch>(in sphere),
        // A lone query — the shuttle port probe — has no batch to join: the batch arm counts it as the count arm does, so the two arms differ in the
        // awareness drain and nowhere else.
        AwarenessApi.Count or AwarenessApi.Batch => CountByCount<TArch>(in sphere),
        _ => throw new ArgumentOutOfRangeException(nameof(_config.AwarenessApi), _config.AwarenessApi, "no drain for this awareness API"),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long CountByCount<TArch>(in BSphere2F sphere) where TArch : Archetype<TArch>, new()
    {
        var e = Dbe.ClusterSpatialQuery<TArch>().Radius(in sphere);
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
    private long CountByFill<TArch>(in BSphere2F sphere) where TArch : Archetype<TArch>, new()
    {
        var buffer = _fillBuffer ??= new ClusterSpatialQueryResult[64];
        var e = Dbe.ClusterSpatialQuery<TArch>().Radius(in sphere);
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
            hits += CountBatch<WorldObject>(members, counts, 0, sampled, work);
            queries += members.Length;
        }

        if (only is null or AwarenessTarget.Creatures)
        {
            hits += CountBatch<Creature>(members, counts, 1, sampled, work);
            queries += members.Length;
        }

        if (only is null or AwarenessTarget.Npcs)
        {
            hits += CountBatch<CityNpc>(members, counts, 2, sampled, work);
            queries += members.Length;
        }

        if (only is null or AwarenessTarget.Players)
        {
            hits += CountBatch<Player>(members, counts, 3, sampled, work);
            queries += members.Length;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long CountBatch<TArch>(ReadOnlySpan<BSphere2F> members, Span<int> counts, int target, ulong sampled, Span<long> work)
        where TArch : Archetype<TArch>, new()
    {
        Dbe.ClusterSpatialQuery<TArch>().CountRadius(members, counts);
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
    private long CountByMoveNext<TArch>(in BSphere2F sphere) where TArch : Archetype<TArch>, new()
    {
        var e = Dbe.ClusterSpatialQuery<TArch>().Radius(in sphere);
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
