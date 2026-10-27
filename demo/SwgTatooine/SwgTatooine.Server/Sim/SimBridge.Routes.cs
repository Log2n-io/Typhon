using System;
using System.Collections.Generic;
using System.Numerics;
using SwgTatooine.Navigation;

namespace SwgTatooine.Server;

/// <summary>
/// The scripted crowd walks a city round its buildings, on the bots' own search (<see cref="CityPath"/>) — Loïc, 2026-10-24: "I'd rather have them
/// behaving correctly".
/// </summary>
/// <remarks>
/// <para>
/// <b>What it replaces.</b> <c>PlayerThink</c> steered every leg in a straight line. Once the crowd was collided (2026-10-22) a straight line through a
/// building ended against it: round one shuttleport 34 of 35 bodies stood still, several on exactly the same coordinates, in its corners — walking to its
/// gate from the far side, or across town to a mission.
/// </para>
/// <para>
/// <b>Searched where there are buildings, straight where there are none.</b> A leg is planned when its body stands in a city, round that city's buildings
/// to the goal or to where the leg leaves the city; across the desert it is straight; and the first time a straight-walking body stands in a city — a leg
/// that crosses a town or ends in one — it is planned there. A search reaches at most <see cref="SearchReachM"/>, as the bot's does, and a longer way is
/// planned again from where the last one ended.
/// </para>
/// <para>
/// <b>Planned in <c>PlayerThink</c>, on its workers.</b> The search's scratch is thread-local, a plan is a few hundred microseconds on a city's metre grid,
/// and a body plans once per leg and per city it walks through.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>How far round a city's disc its buildings stand to be walked round, metres: its port and the houses on its edge — the bots' too.</summary>
    private const float CityMarginM = CityPath.CityMarginM;

    /// <summary>The longest walk one search plans, metres — the bot's <c>SearchReachM</c>, doubled for a crowd that crosses whole towns.</summary>
    private const float SearchReachM = 400f;

    /// <summary>A search that expands this many cells without reaching its goal gives up — the bot's <c>MaxExpansions</c>.</summary>
    private const int SearchExpansions = 120_000;

    /// <summary>How close counts as at a waypoint, metres: the steering caps its last step at the distance left, so the body lands on it.</summary>
    private const float RouteReachM = 0.5f;

    /// <summary>Each planet's cities' structures, as the search reads them: [planet][city]. Null until <see cref="PlanCityWalks"/>.</summary>
    private List<SeenStructure>[][] _cityStructures;

    /// <summary>The walls of every structure, by the index <see cref="SeenStructure.Asset"/> carries here. See <see cref="CrowdWalls"/>.</summary>
    private CrowdWalls _crowdWalls;

    private long _routesPlanned;
    private long _routesFailed;

    /// <summary>Routes planned, and searches that found no way (the body walked straight), since the process started.</summary>
    public (long Planned, long Failed) RouteTotals => (System.Threading.Interlocked.Read(ref _routesPlanned), System.Threading.Interlocked.Read(ref _routesFailed));

    /// <summary>
    /// Reads every planet's structures into its cities' lists, once, after the world is built or reopened: structures never move and are never built at
    /// run time.
    /// </summary>
    public void PlanCityWalks()
    {
        _crowdWalls = new CrowdWalls(AssetShapes);
        var planets = PlanetIndexes?.Length ?? 1;
        _cityStructures = new List<SeenStructure>[planets][];
        for (var p = 0; p < planets; p++)
        {
            var cities = (PlanetIndexes?[p] ?? _index).Cities;
            _cityStructures[p] = new List<SeenStructure>[cities.Count];
            for (var c = 0; c < cities.Count; c++)
            {
                _cityStructures[p][c] = [];
            }
        }

        using var tx = Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<WorldObject>().GetClusterEnumerator())
        {
            var planet = cluster.Realm.Value;
            if (planet >= planets)
            {
                continue;   // a room: its own floor, walked on its own navigation
            }

            var cities = (PlanetIndexes?[planet] ?? _index).Cities;
            var places = cluster.GetReadOnlySpan(WorldObject.Bounds);
            var structures = cluster.GetReadOnlySpan(WorldObject.Struct);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                var x = places[slot].X;
                var z = places[slot].Z;
                for (var c = 0; c < cities.Count; c++)
                {
                    var dx = x - cities[c].X;
                    var dz = z - cities[c].Z;
                    var reach = cities[c].Radius + CityMarginM + 40f;
                    if ((dx * dx) + (dz * dz) <= reach * reach)
                    {
                        ref readonly var s = ref structures[slot];
                        _cityStructures[planet][c].Add(new SeenStructure(x, z, s.Kind, _crowdWalls.IndexOf(s.Asset, s.Kind), s.Yaw, c + 1));
                    }
                }
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// Steers a scripted body at its destination (<c>move.DestX/DestZ</c>), round the buildings of whatever city it is walking through.
    /// </summary>
    /// <param name="route">Its route, planned again whenever the destination changed.</param>
    /// <param name="move">Its motion: the destination is read, the velocity written.</param>
    /// <param name="realm">Where it stands; only a planet has cities.</param>
    /// <param name="x">Where it stands, east.</param>
    /// <param name="z">Where it stands, north.</param>
    /// <param name="k">Ticks per visit of its realm (Realms G2).</param>
    private void SteerRouted(ref PlayerRoute route, ref PlayerMotion move, ushort realm, float x, float z, int k)
    {
        if (_cityStructures == null || realm >= _cityStructures.Length)
        {
            Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, move.DestX, move.DestZ);
            return;
        }

        if (route.GoalX != move.DestX || route.GoalZ != move.DestZ)
        {
            Plan(ref route, realm, x, z, move.DestX, move.DestZ);
        }
        else if (route.Next >= route.Count)
        {
            // Walking straight: into a city it has not been planned through, it is planned there.
            var city = CityNear(realm, x, z, CityMarginM);
            if (city >= 0 && city + 1 != route.City)
            {
                Plan(ref route, realm, x, z, move.DestX, move.DestZ);
            }
        }

        // As SteerToWaypoint does, for every point including the destination: aimed so the next visit finds the body ON it, whatever its realm's stride.
        // The destination too, and that is not a nicety — a door's path starts at one, 5 cm wide, and on a planet visited every third tick a body aimed
        // straight at it overshot and oscillated round it until its walk timed out (InteriorChecks.OnAStridedPlanet...).
        var over = k > 1 ? 1f / k : 1f;
        var points = route.Points();
        while (route.Next < route.Count)
        {
            var wx = points[route.Next * 2];
            var wz = points[(route.Next * 2) + 1];
            var dx = wx - x;
            var dz = wz - z;
            if ((dx * dx) + (dz * dz) > RouteReachM * RouteReachM)
            {
                Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, x + (dx * over), z + (dz * over));
                return;
            }

            route.Next++;
        }

        Steer(ref move.VelX, ref move.VelZ, move.SpeedMps, x, z, x + ((move.DestX - x) * over), z + ((move.DestZ - z) * over));
    }

    /// <summary>Plans the part of a walk to (<paramref name="gx"/>, <paramref name="gz"/>) that lies in a city; straight everywhere else.</summary>
    private void Plan(ref PlayerRoute route, ushort realm, float x, float z, float gx, float gz)
    {
        route.GoalX = gx;
        route.GoalZ = gz;
        route.Count = 0;
        route.Next = 0;
        route.City = 0;

        var city = CityNear(realm, x, z, CityMarginM);
        var goalCity = CityNear(realm, gx, gz, CityMarginM);
        if (city < 0)
        {
            // In the desert: straight, until it stands in a city.
            return;
        }

        // To the goal when it is in this city; otherwise to where the straight line leaves it, a few metres past the margin.
        var cities = (PlanetIndexes?[realm] ?? _index).Cities;
        var (tx, tz) = goalCity == city ? (gx, gz) : LeavePoint(cities[city], x, z, gx, gz);

        // At most SearchReachM: a longer way is planned again from where this one ends, which is what leaving route.City at 0 asks for.
        var whole = true;
        var ddx = tx - x;
        var ddz = tz - z;
        var length = MathF.Sqrt((ddx * ddx) + (ddz * ddz));
        if (length > SearchReachM)
        {
            tx = x + (ddx * SearchReachM / length);
            tz = z + (ddz * SearchReachM / length);
            whole = false;
        }

        var path = CityPath.Route(_cityStructures[realm][city], _crowdWalls, (x, z), (tx, tz), SearchExpansions);
        System.Threading.Interlocked.Increment(ref _routesPlanned);
        if (path == null)
        {
            // Walled off, or past the search's budget: straight, as before, and not searched again in this city on every tick.
            System.Threading.Interlocked.Increment(ref _routesFailed);
            route.City = (byte)(city + 1);
            return;
        }

        var points = route.Points();
        var n = 0;
        foreach (var (px, pz) in path)
        {
            if (n == PlayerRoute.Capacity)
            {
                whole = false;   // the rest is planned again when these run out
                break;
            }

            var dx = px - x;
            var dz = pz - z;
            if (n == 0 && (dx * dx) + (dz * dz) < RouteReachM * RouteReachM)
            {
                continue;   // the search's first point is where the body already stands
            }

            points[n * 2] = px;
            points[(n * 2) + 1] = pz;
            n++;
        }

        route.Count = (byte)n;
        route.City = whole ? (byte)(city + 1) : (byte)0;
    }

    /// <summary>The city whose disc, grown by <paramref name="margin"/>, holds a point; -1 for none.</summary>
    private int CityNear(ushort realm, float x, float z, float margin)
    {
        var cities = (PlanetIndexes?[realm] ?? _index).Cities;
        for (var c = 0; c < cities.Count; c++)
        {
            var dx = x - cities[c].X;
            var dz = z - cities[c].Z;
            var r = cities[c].Radius + margin;
            if ((dx * dx) + (dz * dz) < r * r)
            {
                return c;
            }
        }

        return -1;
    }

    /// <summary>Where a straight line from (<paramref name="x"/>, <paramref name="z"/>) to a goal outside a city crosses out of it, a little past its margin.</summary>
    private static (float X, float Z) LeavePoint((float X, float Z, float Radius, float PlayerWeight) city, float x, float z, float gx, float gz)
    {
        var dx = gx - x;
        var dz = gz - z;
        var len = MathF.Sqrt((dx * dx) + (dz * dz));
        if (len < 1e-3f)
        {
            return (gx, gz);
        }

        dx /= len;
        dz /= len;

        // |p + t·d − c| = R: the positive root, the exit.
        var r = city.Radius + CityPath.CityLeaveM;
        var ox = x - city.X;
        var oz = z - city.Z;
        var b = (ox * dx) + (oz * dz);
        var c = (ox * ox) + (oz * oz) - (r * r);
        var t = -b + MathF.Sqrt(MathF.Max(0f, (b * b) - c));
        t = MathF.Min(t, len);
        return (x + (dx * t), z + (dz * t));
    }

    /// <summary>
    /// The walls the crowd's search reads: the server's own colliders for an asset that publishes them, and the footprint the server collides with for
    /// one that does not — a factory drawn as a box is still a box a body cannot walk through.
    /// </summary>
    /// <remarks>
    /// Indexed as the search asks: an asset index for a published collider set, and <see cref="FootprintBase"/> + kind for a footprint, so one table
    /// serves both without a structure carrying its size.
    /// </remarks>
    private sealed class CrowdWalls : IWallPlans
    {
        /// <summary>Where the footprint entries start: past every asset index.</summary>
        private const int FootprintBase = 1024;

        /// <summary>A footprint's wall height, metres: tall enough to be a wall to the search, which is all it asks.</summary>
        private const float FootprintTopM = 10f;

        private readonly PlanBox[][] _assets;
        private readonly PlanBox[][] _footprints = new PlanBox[64][];

        public CrowdWalls(AssetColliders shapes)
        {
            var count = StructureAssets.Table.Length;
            _assets = new PlanBox[count][];
            for (var i = 0; i < count; i++)
            {
                var boxes = shapes?.ArrayFor(i) ?? [];
                if (boxes.Length == 0)
                {
                    continue;
                }

                var walls = new PlanBox[boxes.Length];
                for (var b = 0; b < boxes.Length; b++)
                {
                    var box = boxes[b];
                    walls[b] = new PlanBox(box.MinX, box.MinZ, box.MaxX, box.MaxZ, box.MinY, box.MaxY, box.SlopeX != 0f || box.SlopeZ != 0f, box.Radius);
                }

                _assets[i] = walls;
            }
        }

        /// <summary>The index a structure's walls are found at: its asset's colliders, or its kind's footprint.</summary>
        public int IndexOf(int asset, int kind)
            => asset >= 0 && asset < _assets.Length && _assets[asset] != null ? asset : FootprintBase + Math.Clamp(kind, 0, _footprints.Length - 1);

        public PlanBox[] WallsOf(int asset)
        {
            if (asset < FootprintBase)
            {
                return asset >= 0 && asset < _assets.Length ? _assets[asset] : null;
            }

            var kind = asset - FootprintBase;
            var walls = _footprints[kind];
            if (walls == null)
            {
                var h = StructureFootprint.HalfExtentM(kind);
                walls = [new PlanBox(-h, -h, h, h, 0f, FootprintTopM, false)];

                // Benign race: two workers may build the same one-element array, and either is right.
                _footprints[kind] = walls;
            }

            return walls;
        }
    }
}
