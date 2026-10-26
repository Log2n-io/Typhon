using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// One planet's lair census (lair-objectives.md L-1): per point of interest, its standing wild lairs and the players near it, with the weight
/// <c>lairs / (1 + players)</c> — where lairs most outnumber the people fighting them.
/// </summary>
/// <remarks>Built whole once a second by the lair director and published by reference; <c>PlayerThink</c>'s parallel chunks only read it.</remarks>
internal sealed class LairCensus
{
    public LairCensus(int pois)
    {
        Lairs = new int[pois];
        Players = new int[pois];
        Cumulative = new float[pois];
        Weight = new float[pois];
    }

    /// <summary>Each point of interest's weight, <c>lairs / (1 + players)</c>; 0 where no lair stands.</summary>
    public float[] Weight { get; }

    /// <summary>Standing wild lairs per point of interest, by its index in <see cref="WorldIndex.Pois"/>.</summary>
    public int[] Lairs { get; }

    /// <summary>Players within each point of interest's radius, the crowd and sessions alike.</summary>
    public int[] Players { get; }

    /// <summary>The running sum of the draw weights.</summary>
    public float[] Cumulative { get; }

    /// <summary>The sum of every weight; 0 when no point of interest has a standing lair.</summary>
    public float Total { get; set; }

    /// <summary>A point of interest drawn in proportion to its weight, from <paramref name="u"/> in [0, 1); -1 when none has a standing lair.</summary>
    public int Draw(float u)
    {
        if (Total <= 0f)
        {
            return -1;
        }

        var t = u * Total;
        var lo = 0;
        var hi = Cumulative.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (Cumulative[mid] > t)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo;
    }

    /// <summary>
    /// A point of interest drawn in proportion to its weight times its nearness to (<paramref name="x"/>, <paramref name="z"/>) —
    /// <c>D² / (D² + d²)</c> with <paramref name="falloffM"/> as D — from <paramref name="u"/> in [0, 1); -1 when none has a standing lair.
    /// </summary>
    /// <remarks>
    /// <b>Nearness, because an unattended place is far from everybody by construction</b>: drawn by weight alone, fighters crossed 1 to 12 km of desert
    /// for it, and in five minutes almost none arrived (measured 2026-10-26). Among the places within reach, the unattended still win.
    /// </remarks>
    public int DrawNear(IReadOnlyList<(float X, float Z, float Radius)> pois, float x, float z, float falloffM, float u)
    {
        var d2 = falloffM * falloffM;
        var total = 0f;
        for (var i = 0; i < Weight.Length; i++)
        {
            total += Near(pois[i], x, z, d2) * Weight[i];
        }

        if (total <= 0f)
        {
            return -1;
        }

        var t = u * total;
        var acc = 0f;
        var last = -1;
        for (var i = 0; i < Weight.Length; i++)
        {
            var w = Near(pois[i], x, z, d2) * Weight[i];
            if (w <= 0f)
            {
                continue;
            }

            acc += w;
            last = i;
            if (t < acc)
            {
                return i;
            }
        }

        return last;
    }

    private static float Near((float X, float Z, float Radius) poi, float x, float z, float falloff2)
    {
        var dx = poi.X - x;
        var dz = poi.Z - z;
        return falloff2 / (falloff2 + (dx * dx) + (dz * dz));
    }
}

/// <summary>
/// The crowd's lair objectives (lair-objectives.md; plan G1): a fighter goes where lairs are left unattended, breaks the nearest, and stays until it falls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lair density is fixed per site</b> — a retired lair is replaced on its site — so "too many lairs" is read as lairs nobody is fighting. The census
/// counts, per point of interest, its standing wild lairs (the site's <see cref="LairSite.Live"/>, which the director keeps) and the players within its
/// radius; a fighter draws a point of interest in proportion to <c>lairs / (1 + players)</c>.
/// </para>
/// <para>
/// <b>The objective is a lair's position, in <see cref="PlayerState.MissionX"/>/<see cref="PlayerState.MissionZ"/></b> — unused by the crowd since it stopped
/// taking missions. Wild lairs never move, so "does my objective still stand" is a 2 m radius query at that point. The fighter's destination is a stand-off
/// point on its own side of the lair, inside weapon range: arrived, it stops and fights what <c>PlayerCombatTick</c> finds — the occupants first, the lair
/// once they are down.
/// </para>
/// <para>
/// <b>Mission lairs are never the crowd's</b>: a stranger's break only redraws a holder's lair (missions.md M-6). Neither an objective nor a target.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>Where a fighter stands from its objective, as a share of the crowd's weapon range: its occupants, round the lair, are in range too.</summary>
    internal const float ObjectiveStandOff = 0.6f;

    /// <summary>A fight not resolved after this long is given up, counted (plan G2: a give-up is a decision with a reason).</summary>
    internal const int ObjectiveGiveUpSeconds = 300;

    /// <summary>How far from a fallen objective the next one is looked for: the same point of interest, roughly.</summary>
    internal const float NextObjectiveM = 150f;

    /// <summary>The distance at which a point of interest's draw weight is halved (<see cref="LairCensus.DrawNear"/>).</summary>
    internal const float ObjectiveFalloffM = 2000f;

    // One census per planet, published whole once a second.
    private LairCensus[] _censuses;

    private long _objectivesTaken;
    private long _objectivesFallen;
    private long _objectivesGivenUp;
    private long _objectivesHeldInRange;
    private long _lairHitsTotal;
    private long _objectivesOutOfRange;

    /// <summary>Lairs a fighter of the crowd made its objective, first or next.</summary>
    public long ObjectivesTaken => Interlocked.Read(ref _objectivesTaken);

    /// <summary>Objectives that fell — broken, or retired — while a fighter held them.</summary>
    public long ObjectivesFallen => Interlocked.Read(ref _objectivesFallen);

    /// <summary>Fights given up after <see cref="ObjectiveGiveUpSeconds"/> with the objective still standing.</summary>
    public long ObjectivesGivenUp => Interlocked.Read(ref _objectivesGivenUp);

    /// <summary>
    /// Objective checks of a fighter standing at its stand-off point, its lair standing: each one is L-3's claim tested — it is within weapon range.
    /// </summary>
    public long ObjectivesHeldInRange => Interlocked.Read(ref _objectivesHeldInRange);

    /// <summary>Those checks that found the fighter out of weapon range of its standing objective: L-3 broken. Must stay 0.</summary>
    public long ObjectivesOutOfRange => Interlocked.Read(ref _objectivesOutOfRange);

    /// <summary>The crowd's weapon range, metres: what a fighter at its objective must be within.</summary>
    public float CrowdWeaponRangeM => RangedRange;

    /// <summary>Lair hits since start — <c>SimStats.LairHits</c> is the sampler's interval and resets.</summary>
    public long LairHitsTotal => Interlocked.Read(ref _lairHitsTotal);

    /// <summary>The crowd's objective counters as JSON, for <c>GET /swg/crowd/stats</c> (lair-objectives.md L-6).</summary>
    public string CrowdCountersJson()
        => FormattableString.Invariant(
            $"{{\"lairHits\":{LairHitsTotal},\"objectivesTaken\":{ObjectivesTaken},\"objectivesFallen\":{ObjectivesFallen},\"objectivesGivenUp\":{ObjectivesGivenUp},\"heldInRange\":{ObjectivesHeldInRange},\"outOfRange\":{ObjectivesOutOfRange}}}");

    /// <summary>The census of one planet, or <see langword="null"/> before the first one is taken.</summary>
    internal LairCensus CensusOf(int realm)
    {
        var all = Volatile.Read(ref _censuses);
        return all != null && realm < all.Length ? all[realm] : null;
    }

    /// <summary>Counts, per point of interest of every planet, its standing wild lairs and the players near it. The lair director's, once a second.</summary>
    private void TakeCensus()
    {
        var indexes = PlanetIndexes ?? [_index];
        var all = new LairCensus[indexes.Length];
        for (var planet = 0; planet < indexes.Length; planet++)
        {
            var index = indexes[planet];
            var census = new LairCensus(index.Pois.Count);
            var realm = new RealmId((ushort)planet);
            var total = 0f;
            for (var i = 0; i < index.Pois.Count; i++)
            {
                // The points of interest are the first sites, in the same order (WorldBuilder.PlanSites).
                var lairs = i < index.Sites.Count ? Math.Max(0, index.Sites[i].Live) : 0;
                var poi = index.Pois[i];
                var players = 0;
                if (lairs > 0)
                {
                    var sphere = new BSphere2F { CenterX = poi.X, CenterY = poi.Z, Radius = poi.Radius };
                    var near = Dbe.ClusterSpatialQuery<Player>(realm).Radius(in sphere);
                    try
                    {
                        while (near.MoveNext())
                        {
                            players++;
                        }
                    }
                    finally
                    {
                        near.Dispose();
                    }
                }

                census.Lairs[i] = lairs;
                census.Players[i] = players;
                census.Weight[i] = lairs / (1f + players);
                total += census.Weight[i];
                census.Cumulative[i] = total;
            }

            census.Total = total;
            all[planet] = census;
        }

        Volatile.Write(ref _censuses, all);
    }

    /// <summary>
    /// The "go and fight" decision: a point of interest drawn by the census, and the standing wild lair in it nearest to the fighter as its objective.
    /// </summary>
    /// <returns><see langword="false"/>, having changed nothing, when no point of interest has a standing lair the fighter can be sent to.</returns>
    private bool TryTakeObjective(TickContext ctx, ref PlayerState state, ref PlayerMotion move, ushort realm, float x, float z, uint salt)
    {
        var census = CensusOf(realm);
        if (census == null || realm >= (PlanetIndexes?.Length ?? 1))
        {
            return false;
        }

        var pois = (PlanetIndexes ?? [_index])[realm].Pois;
        var poiIndex = census.DrawNear(pois, x, z, ObjectiveFalloffM * _config.ContentScale, Hash01(salt));
        if (poiIndex < 0)
        {
            return false;
        }

        var poi = pois[poiIndex];
        if (!TryNearestWildLair(ctx, realm, poi.X, poi.Z, poi.Radius, x, z, out var lx, out var lz))
        {
            return false;
        }

        move.SpeedMps = TatooineData.PlayerMountSpeedMps;
        state.Activity = PlayerActivity.Combat;
        state.ActivityTicks = AimAt(ref state, ref move, x, z, lx, lz);
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
        return true;
    }

    /// <summary>
    /// A fighter's objective, checked: still standing — or, fallen, replaced by the next standing wild lair near it.
    /// </summary>
    /// <returns><see langword="false"/> when it fell and there is no next one: the fight is over.</returns>
    private bool KeepObjective(TickContext ctx, ref PlayerState state, ref PlayerMotion move, ushort realm, float x, float z, ref long taken, ref long fallen)
    {
        if (ObjectiveStands(ctx, realm, state.MissionX, state.MissionZ))
        {
            // L-3, tested on every check rather than sampled: a fighter at its stand-off point is within range of the lair it is there for.
            var ax = move.DestX - x;
            var az = move.DestZ - z;
            if ((ax * ax) + (az * az) < 25f)
            {
                var ox = state.MissionX - x;
                var oz = state.MissionZ - z;
                Interlocked.Increment(ref (ox * ox) + (oz * oz) <= RangedRange * RangedRange ? ref _objectivesHeldInRange : ref _objectivesOutOfRange);
            }

            return true;
        }

        fallen++;
        var scale = _config.ContentScale;
        if (!TryNearestWildLair(ctx, realm, state.MissionX, state.MissionZ, NextObjectiveM * scale, x, z, out var lx, out var lz))
        {
            return false;
        }

        state.ActivityTicks = AimAt(ref state, ref move, x, z, lx, lz);
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
        taken++;
        return true;
    }

    /// <summary>Makes the lair at (<paramref name="lx"/>, <paramref name="lz"/>) the objective: the destination its stand-off point on the fighter's side.</summary>
    /// <returns>
    /// The fight's time, ticks: the ride there at the fighter's speed and then <see cref="ObjectiveGiveUpSeconds"/> — the give-up times the fight, not the
    /// ride, which reached 5 to 10 km to a point of interest in the desert.
    /// </returns>
    private int AimAt(ref PlayerState state, ref PlayerMotion move, float x, float z, float lx, float lz)
    {
        var dx = x - lx;
        var dz = z - lz;
        var d = MathF.Sqrt((dx * dx) + (dz * dz));
        if (d < 1f)
        {
            dx = 1f;
            dz = 0f;
            d = 1f;
        }

        var standOff = RangedRange * ObjectiveStandOff;
        state.MissionX = lx;
        state.MissionZ = lz;
        move.DestX = lx + (dx / d * standOff);
        move.DestZ = lz + (dz / d * standOff);
        var ride = MathF.Max(0f, d - standOff) / MathF.Max(1f, move.SpeedMps);
        return (int)((ride + ObjectiveGiveUpSeconds) * _config.TickRateHz);
    }

    /// <summary>Whether a standing wild lair is at the objective's point. Wild lairs never move, so 2 m is its own position.</summary>
    private bool ObjectiveStands(TickContext ctx, ushort realm, float ox, float oz)
        => TryNearestWildLair(ctx, realm, ox, oz, 2f, ox, oz, out _, out _);

    /// <summary>The standing wild lair within <paramref name="radius"/> of a centre that is nearest to (<paramref name="fromX"/>, <paramref name="fromZ"/>).</summary>
    private bool TryNearestWildLair(TickContext ctx, ushort realm, float cx, float cz, float radius, float fromX, float fromZ, out float lx, out float lz)
    {
        lx = 0f;
        lz = 0f;
        var best = float.MaxValue;
        var sphere = new BSphere2F { CenterX = cx, CenterY = cz, Radius = radius };
        var lairs = Dbe.ClusterSpatialQuery<CreatureLair>(new RealmId(realm)).Radius(in sphere);
        try
        {
            while (lairs.MoveNext())
            {
                var hit = lairs.Current;
                if (!ctx.Accessor.TryOpen(hit.Entity, out var lair))
                {
                    continue;
                }

                var spawner = lair.Read(CreatureLair.Spawner);
                if (!spawner.Mission.IsNull || !IsStandingLair(spawner, lair.Read(CreatureLair.Vitals)))
                {
                    continue;
                }

                var at = lair.Read(CreatureLair.Bounds);
                var dx = at.X - fromX;
                var dz = at.Z - fromZ;
                var d2 = (dx * dx) + (dz * dz);
                if (d2 < best)
                {
                    best = d2;
                    lx = at.X;
                    lz = at.Z;
                }
            }
        }
        finally
        {
            lairs.Dispose();
        }

        return best < float.MaxValue;
    }
}
