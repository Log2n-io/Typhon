using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// One planet's lair census (lair-objectives.md L-1): per crowd place — a city's fringe since content levels (content-levels.md § 5) — its standing wild
/// lairs and the players at it, with the weight <c>lairs / (1 + players)</c> — where lairs most outnumber the people fighting them.
/// </summary>
/// <remarks>Built whole once a second by the lair director and published by reference; <c>PlayerThink</c>'s parallel chunks only read it.</remarks>
internal sealed class LairCensus
{
    public LairCensus(int pois)
    {
        Lairs = new int[pois];
        Players = new int[pois];
        Weight = new float[pois];
    }

    /// <summary>Each point of interest's weight, <c>lairs / (1 + players)</c>; 0 where no lair stands.</summary>
    public float[] Weight { get; }

    /// <summary>Standing wild lairs per crowd place, by its index in <see cref="WorldIndex.CrowdSites"/>.</summary>
    public int[] Lairs { get; }

    /// <summary>Players within each point of interest's radius, the crowd and sessions alike.</summary>
    public int[] Players { get; }

    /// <summary>A point of interest's weight: its standing wild lairs over the players there, plus one.</summary>
    public static float WeightOf(int lairs, int players) => lairs <= 0 ? 0f : lairs / (1f + Math.Max(0, players));

    /// <summary>
    /// A point of interest drawn in proportion to its weight times its nearness to (<paramref name="x"/>, <paramref name="z"/>) —
    /// <c>D² / (D² + d²)</c> with <paramref name="falloffM"/> as D — from <paramref name="u"/> in [0, 1); -1 when none has a standing lair.
    /// </summary>
    /// <remarks>
    /// <b>Nearness, because an unattended place is far from everybody by construction</b>: drawn by weight alone, fighters crossed 1 to 12 km of desert
    /// for it, and in five minutes almost none arrived (measured 2026-10-26). Among the places within reach, the unattended still win.
    /// </remarks>
    public int DrawNear(IReadOnlyList<(float X, float Z, float Radius, float InnerRadius)> pois, float x, float z, float falloffM, float u)
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

    private static float Near((float X, float Z, float Radius, float InnerRadius) poi, float x, float z, float falloff2)
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
/// <b>Mission lairs are never the crowd's</b>: a reserved lair is a holder's, and a stranger's break only moves its mission (missions.md MS-6). Neither an objective nor a target.
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
    private long _lastCensusTick = long.MinValue / 2;
    private long _objectivesOutOfRange;

    /// <summary>Lairs a fighter of the crowd made its objective, first or next.</summary>
    public long ObjectivesTaken => Interlocked.Read(ref _objectivesTaken);

    /// <summary>Objectives that fell — broken, or retired — while a fighter held them.</summary>
    public long ObjectivesFallen => Interlocked.Read(ref _objectivesFallen);

    /// <summary>Fights given up after <see cref="ObjectiveGiveUpSeconds"/> with the objective still standing.</summary>
    public long ObjectivesGivenUp => Interlocked.Read(ref _objectivesGivenUp);

    /// <summary>
    /// Objective checks that tested L-3 and found it held: the fighter's destination is within weapon range of its standing lair, and — once it has
    /// stopped — the fighter itself is.
    /// </summary>
    public long ObjectivesHeldInRange => Interlocked.Read(ref _objectivesHeldInRange);

    /// <summary>Those checks that found the destination, or the stopped fighter, out of range of its standing objective: L-3 broken. Must stay 0.</summary>
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

    /// <summary>
    /// Counts, per crowd place of every planet — each city's fringe (content-levels.md § 5, R1) — its standing wild lairs and the players at it, the city
    /// itself not counted. The lair director's, once a second.
    /// </summary>
    private void TakeCensus(long tick)
    {
        if (tick - _lastCensusTick < Math.Max(1, _config.TickRateHz))
        {
            return;
        }

        _lastCensusTick = tick;
        var indexes = PlanetIndexes ?? [_index];
        var all = new LairCensus[indexes.Length];
        for (var planet = 0; planet < indexes.Length; planet++)
        {
            var index = indexes[planet];
            var census = new LairCensus(index.CrowdSites.Count);
            var realm = new RealmId((ushort)planet);
            for (var i = 0; i < index.CrowdSites.Count; i++)
            {
                var lairs = Math.Max(0, index.Sites[index.CrowdSites[i]].Live);
                var place = index.CrowdPlaces[i];
                var players = 0;
                if (lairs > 0)
                {
                    // At the fringe: in its outer disc and not in the city, whose crowd is shopping, not fighting.
                    players = PlayersWithin(realm, place.X, place.Z, place.Radius) - PlayersWithin(realm, place.X, place.Z, place.InnerRadius);
                }

                census.Lairs[i] = lairs;
                census.Players[i] = players;
                census.Weight[i] = LairCensus.WeightOf(lairs, players);
            }

            all[planet] = census;
        }

        Volatile.Write(ref _censuses, all);
    }

    /// <summary>Players of every kind within a disc.</summary>
    private int PlayersWithin(RealmId realm, float x, float z, float radius)
    {
        var players = 0;
        var sphere = new BSphere2F { CenterX = x, CenterY = z, Radius = radius };
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

        return players;
    }

    /// <summary>
    /// The "go and fight" decision: a point of interest drawn by the census, and the standing wild lair in it nearest to the fighter as its objective.
    /// </summary>
    /// <returns><see langword="false"/>, having changed nothing, when no point of interest has a standing lair the fighter can be sent to.</returns>
    private bool TryTakeObjective(TickContext ctx, ref PlayerState state, ref PlayerMotion move, ref PlayerRoute route, ushort realm, float x, float z,
        uint salt)
    {
        var census = CensusOf(realm);
        if (census == null || realm >= (PlanetIndexes?.Length ?? 1))
        {
            return false;
        }

        var pois = (PlanetIndexes ?? [_index])[realm].CrowdPlaces;
        var poiIndex = census.DrawNear(pois, x, z, ObjectiveFalloffM * _config.ContentScale, Hash01(salt));
        if (poiIndex < 0)
        {
            return false;
        }

        var poi = pois[poiIndex];
        if (!TryNearestWildLair(ctx, realm, poi.X, poi.Z, poi.Radius, x, z, out var lx, out var lz, out var level))
        {
            return false;
        }

        NoteObjectiveLevel(level);
        move.SpeedMps = TatooineData.PlayerMountSpeedMps;
        state.Activity = PlayerActivity.Combat;
        state.ActivityTicks = AimAt(ref state, ref move, x, z, lx, lz);
        route.Objective = 1;
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
        return true;
    }

    /// <summary>
    /// A fighter's objective, checked: still standing — or, fallen, replaced by the next standing wild lair near it.
    /// </summary>
    /// <returns><see langword="false"/> when it fell and there is no next one: the fight is over.</returns>
    private bool KeepObjective(TickContext ctx, ref PlayerState state, ref PlayerMotion move, ushort realm, float x, float z, ref ObjectiveCounts counts)
    {
        if (ObjectiveStands(ctx, realm, state.MissionX, state.MissionZ))
        {
            // L-3, tested on every check of every holder rather than sampled: where it is going is within range of its lair — so nothing steered it away —
            // and, once it has stopped, so is where it stands.
            var range2 = RangedRange * RangedRange;
            var dx = state.MissionX - move.DestX;
            var dz = state.MissionZ - move.DestZ;
            var held = (dx * dx) + (dz * dz) <= range2;
            if (move.VelX == 0f && move.VelZ == 0f)
            {
                var ox = state.MissionX - x;
                var oz = state.MissionZ - z;
                held &= (ox * ox) + (oz * oz) <= range2;
            }

            if (held)
            {
                counts.HeldInRange++;
            }
            else
            {
                counts.OutOfRange++;
            }

            return true;
        }

        counts.Fallen++;
        var scale = _config.ContentScale;
        if (!TryNearestWildLair(ctx, realm, state.MissionX, state.MissionZ, NextObjectiveM * scale, x, z, out var lx, out var lz, out var level))
        {
            return false;
        }

        NoteObjectiveLevel(level);
        state.ActivityTicks = AimAt(ref state, ref move, x, z, lx, lz);
        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
        counts.Taken++;
        return true;
    }

    /// <summary><c>PlayerThink</c>'s chunk-local objective counts, added to the shared counters once per chunk.</summary>
    private struct ObjectiveCounts
    {
        public long Taken;
        public long Fallen;
        public long GivenUp;
        public long HeldInRange;
        public long OutOfRange;
    }

    /// <summary>Adds a chunk's counts to the shared counters.</summary>
    private void Publish(in ObjectiveCounts counts)
    {
        if (counts.Taken + counts.Fallen + counts.GivenUp + counts.HeldInRange + counts.OutOfRange == 0)
        {
            return;
        }

        Interlocked.Add(ref _objectivesTaken, counts.Taken);
        Interlocked.Add(ref _objectivesFallen, counts.Fallen);
        Interlocked.Add(ref _objectivesGivenUp, counts.GivenUp);
        Interlocked.Add(ref _objectivesHeldInRange, counts.HeldInRange);
        Interlocked.Add(ref _objectivesOutOfRange, counts.OutOfRange);
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
        => TryNearestWildLair(ctx, realm, ox, oz, 2f, ox, oz, out _, out _, out _);

    /// <summary>Raises <see cref="CrowdObjectiveLevelMax"/> to an objective's level.</summary>
    private void NoteObjectiveLevel(int level)
    {
        var seen = Volatile.Read(ref _crowdObjectiveLevelMax);
        while (level > seen)
        {
            var was = Interlocked.CompareExchange(ref _crowdObjectiveLevelMax, level, seen);
            if (was == seen)
            {
                return;
            }

            seen = was;
        }
    }

    private int _crowdObjectiveLevelMax;

    /// <summary>The highest level of a lair the crowd was ever sent to: must stay at or below <see cref="TatooineData.CrowdLevel"/> (CL-10).</summary>
    public int CrowdObjectiveLevelMax => Volatile.Read(ref _crowdObjectiveLevelMax);

    /// <summary>
    /// The standing wild lair within <paramref name="radius"/> of a centre that is nearest to (<paramref name="fromX"/>, <paramref name="fromZ"/>), at or
    /// below <see cref="TatooineData.CrowdLevel"/>: a fringe's lairs are by construction, and this also covers a region lair or a patch edge in the disc.
    /// </summary>
    private bool TryNearestWildLair(TickContext ctx, ushort realm, float cx, float cz, float radius, float fromX, float fromZ, out float lx, out float lz,
        out int level)
    {
        lx = 0f;
        lz = 0f;
        level = 0;
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
                if (!spawner.Mission.IsNull || spawner.Level > TatooineData.CrowdLevel || !IsStandingLair(spawner, lair.Read(CreatureLair.Vitals)))
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
                    level = spawner.Level;
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
