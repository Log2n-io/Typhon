using SwgTatooine.Server.Terrain;
using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Turns a <see cref="TatooineMap"/> into entities.
/// </summary>
/// <remarks>
/// <para><b>Everything here is placed where the game would place it, and that is the whole point.</b> A uniform sprinkle
/// over the planet would be a page of code instead of this one, and it would produce a workload whose cluster bounds are
/// a restatement of its density — the trap the earlier space scenario fell into. Cities are dense discs of buildings and
/// NPCs; points of interest are tight knots; the wilderness is lairs at a few per square kilometre with their creatures
/// scattered inside a spawn radius. That is a genuinely multi-scale distribution, and it is the thing worth measuring.</para>
/// <para>Spawning happens in chunked transactions rather than one enormous one. At the baseline population it would not
/// matter; at a x100 population multiplier a single transaction would be millions of entities and the batch-Morton sort
/// would be sorting a working set far larger than cache.</para>
/// </remarks>
public static class WorldBuilder
{
    /// <summary>Entities per spawn transaction. Large enough for the batch Morton sort to pay, small enough to stay in cache.</summary>
    private const int SpawnBatch = 20_000;

    /// <summary>
    /// Populate the database. Returns what was actually created, and fills <paramref name="index"/> with the entity ids
    /// the simulation systems need to address later.
    /// </summary>
    /// <param name="realm">The planet's realm: every entity spawned here carries it (Realms G1). Planet 0 is Tatooine as it always was; a further planet
    /// is its twin — the same map, populated from its own seed.</param>
    /// <param name="firstInteriorRealm">The room of this planet's first enterable building; the next building's is the next realm. 0 makes no building
    /// enterable.</param>
    public static WorldCensus Populate(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldIndex index = null, ushort realm = 0,
        int firstInteriorRealm = 0)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(config);

        var census = new WorldCensus();
        // Planet 0's seed is the run's seed, so a one-planet run is the world it always was; a further planet draws its own.
        var rng = new Rng((uint)config.Seed + realm * 0x9E3779B9u);
        index ??= new WorldIndex();
        _realm = realm;
        _nextInterior = firstInteriorRealm;

        if (map.ClampedSites > 0 && realm == 0)
        {
            // Once per run, not once per planet or once per city: the cause is the world size, which is one decision, and a line per city would bury it.
            Console.WriteLine(
                $"  !! --world {config.WorldEdgeKm:G} compresses the map past what its sites can hold: {map.ClampedSites} sites had their structure count "
                + $"cut, {map.ClampedStructures} structures dropped. Structure counts are not comparable with the real planet size.");
        }

        // ONE planner for the whole planet, not one per site. Every structure on the ground is tested against every other one near it, whichever site
        // planted it — see SitePlanner's own remarks for the 74 overlapping pairs that per-site planners left in a finished world. Cities go in first, so
        // the authored content wins every contest against a player's house.
        var plan = new SitePlanner(StructureFootprint.HalfExtentM(StructureKind.Shuttleport));

        SpawnCities(dbe, map, config, census, ref rng, index, plan);
        PlanSites(map, config, index, realm);
        SpawnPointsOfInterest(dbe, map, config, census, ref rng, index, plan);
        SpawnWilderness(dbe, config, census, ref rng, index);
        SpawnPlayerStructures(dbe, map, config, census, ref rng, plan);
        SpawnPlayers(dbe, map, config, census, ref rng, index, plan);
        SpawnMissionLairPool(dbe, map, config, census, ref rng, index);

        census.Crowded = plan.Crowded;
        return census;
    }

    // The planet being populated. The build is serial (one planet after another, on the opening thread), so every spawn helper reads it here rather than
    // threading a parameter through all of them.
    private static ushort _realm;

    // The room the next enterable building leads to, or 0 when this build makes none enterable. Serial for the same reason as _realm.
    private static int _nextInterior;

    // ── Cities ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Buildings and NPCs, laid out as discs around each city centre.
    /// </summary>
    /// <remarks>
    /// Placement is uniform-by-area inside the disc (the square root on the radius is what makes it uniform rather than
    /// centre-heavy), because a SWG city is a grid of streets rather than a spire. Buildings get a real footprint —
    /// a 12 m box — so their cluster bounds are not degenerate points, which matters: an index over zero-extent boxes
    /// behaves differently from one over real ones, and buildings are the largest population in the world.
    /// </remarks>
    private static void SpawnCities(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index,
        SitePlanner plan)
    {
        // The planet's ground, baked once per process (see TerrainField). Taken here rather than per entity: the
        // cache lookup is cheap but not free, and this loop runs seventeen thousand times.
        var terrain = TerrainField.Shared(config.ContentScale);
        var cityIndex = 0;
        foreach (var city in map.Cities)
        {
            var buildings = city.Buildings;
            var npcs = Scale(city.Npcs, config.PopulationScale);
            var crowdedBefore = plan.Crowded;
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < buildings; i++)
                {
                    var kind = CityKind(i);
                    var half = StructureFootprint.HalfExtentM(kind);

                    // <b>The shuttleport stands at the city's centre, and the city is built around it</b> (Loïc,
                    // 2026-10-22). It is placed first, so reserving the middle costs nothing and every later building
                    // avoids it: a town square rather than a dome wherever the first random draw happened to land. It
                    // also puts the one structure a traveller is looking for where a traveller would look for it.
                    // The town hall is the second, across the way from its gate (Loïc, 2026-10-25), and draws nothing either.
                    var (x, z, _) = i switch
                    {
                        0 => (city.X, city.Z, plan.PlaceAt(city.X, city.Z, half)),
                        1 => (city.X, city.Z + TownHallZ, plan.PlaceAt(city.X, city.Z + TownHallZ, half)),
                        _ => plan.Place(ref rng, city.X, city.Z, city.Radius, half),
                    };
                    var interior = 0;
                    if (i == 0)
                    {
                        // Kept for shuttle travel (#910). Recording a coordinate the RNG already produced draws nothing, so the world is unchanged.
                        index.Shuttleports.Add((x, z));
                    }
                    else if (IsEnterable(i) && _nextInterior != 0)
                    {
                        // Realms G1b: this building's room, written onto the building. Draws nothing, like the shuttleport above. Its doors and the room
                        // itself are spawned by BuildInteriors, which needs the published asset to know where they are.
                        interior = _nextInterior++;
                    }

                    // The entity's bounds are its FOOTPRINT, not a uniform 12 m for every kind. They used to be the latter, which
                    // made the dome and a crafting station the same size to every spatial query and to anything asking what stands at a
                    // point -- and collision has to ask exactly that.
                    SpawnStructure(tx, x, z, terrain.GroundAt(x, z), half, kind, ownerRegion: cityIndex + 1, tickPeriod: 0, ref rng, interior,
                        yaw: kind == StructureKind.TownHall ? TownHallYaw : -1);
                    census.StaticObjects++;
                    if (i == 0)
                    {
                        // Before any other building, so the gate's ground is the first claimed: a passenger walks out onto a bazaar and a mission terminal.
                        SpawnGateFacilities(tx, x, z, cityIndex, terrain, census, plan);
                    }
                }

                SpawnFacilities(tx, city, config, cityIndex, terrain, census, plan);
                tx.Commit();
            }

            if (plan.Crowded > crowdedBefore)
            {
                // Said rather than swallowed. The map has already capped this count to what the disc holds
                // (StructureFootprint.CapacityOf), so reaching here means the cap is wrong rather than the map — the packing ceiling it is derived from has
                // been measured too high, or something else was planted across this city first.
                Console.WriteLine(
                    $"  !! {city.Name}: {plan.Crowded - crowdedBefore} of {buildings} structures had no room in a {city.Radius:F0} m disc and overlap");
            }

            var made = 0;
            while (made < npcs)
            {
                var n = Math.Min(SpawnBatch, npcs - made);
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < n; i++)
                {
                    var (x, z) = rng.PointInDisc(city.X, city.Z, city.Radius);

                    // Never born inside a structure: a draw that lands in one is pushed out to its nearest street. The draw used to be taken as it
                    // fell, and the shuttleport alone — the city's centre, 46 m across — held dozens of NPCs inside its ring wall with no way out.
                    plan.PushOut(ref x, ref z, NpcHalfM);
                    var bounds = default(NpcPlacement);
                    bounds.SetAt(x, z, terrain.GroundAt(x, z), NpcHalfM);

                    // A city NPC stands still and thinks rarely. Its leash radius is a few metres because the ones that
                    // move at all are shuffling behind a counter, not patrolling.
                    var ai = new NpcBrain
                    {
                        Mode = rng.NextFloat() < 0.12f ? AiMode.Wander : AiMode.Idle,
                        HomeX = x,
                        HomeZ = z,
                        LeashRadius = 8f,
                    };

                    // Staggered so a city's NPCs do not all pick a leg on the same tick. Absolute stamps, so standing still writes nothing.
                    var npcTimers = new NpcTimers { MoveUntilTick = 0, RestUntilTick = rng.NextInt(1, 40) };
                    var move = new NpcMotion { SpeedMps = 1.2f };
                    tx.Spawn<CityNpc>(
                        CityNpc.Bounds.Set(in bounds),
                        CityNpc.Ai.Set(in ai),
                        CityNpc.Timers.Set(in npcTimers),
                        CityNpc.Move.Set(in move),
                        CityNpc.Realm.Set(new NpcRealm(_realm)));
                    census.CityNpcs++;
                }

                tx.Commit();
                made += n;
            }

            // The city's own region entity, which is what makes this city a group: its key is what every structure above carries, and its circle is what a
            // session's reach is measured to. One per city per planet — the regions of a planet other than 0 are that planet's because this archetype is
            // spatial (see CityRegion).
            using (var tx = dbe.CreateQuickTransaction())
            {
                var bounds = new AABB2F
                {
                    MinX = city.X - city.Radius,
                    MinY = city.Z - city.Radius,
                    MaxX = city.X + city.Radius,
                    MaxY = city.Z + city.Radius,
                };
                tx.Spawn<CityRegion>(
                    CityRegion.Bounds.Set(new CityPlacement { Bounds = bounds }),
                    CityRegion.Area.Set(new CityArea
                    {
                        Key = cityIndex + 1,
                        Circle = new BSphere2F { CenterX = city.X, CenterY = city.Z, Radius = city.Radius },
                    }),
                    CityRegion.Realm.Set(new CityRegionRealm(_realm)));
                tx.Commit();
            }

            index.Cities.Add((city.X, city.Z, city.Radius, city.PlayerWeight));
            cityIndex++;
        }
    }

    /// <summary>
    /// Where a passenger leaving a shuttleport comes out, relative to its centre: the gate in the ring wall on the asset's +Z side, its outside start
    /// on open ground (`tat_shuttleport`'s ticket terminal approach starts at z 25.2; the gate spans x -3..3). The shuttleport is never turned and the
    /// server's mirror is on X only, so the published +Z is the world's.
    /// </summary>
    public const float GateOutsideZ = 25.2f;

    /// <summary>How wide the clear ground outside a shuttleport's gate is that passengers stand on, metres: the gate's own width, less a body.</summary>
    public const float GateLaneM = 4.8f;

    /// <summary>How far out from the gate's outside start that ground reaches, metres: the reserved corridor and into the plaza beyond it.</summary>
    public const float GateLaneDepthM = 8f;

    /// <summary>
    /// A spot on the clear ground just outside a shuttleport's gate: where passengers queue for its shuttle and where an arriving one is set down.
    /// </summary>
    /// <param name="portX">The port's centre, east.</param>
    /// <param name="portZ">The port's centre, north.</param>
    /// <param name="across">0..1 across the lane, west to east.</param>
    /// <param name="outward">0..1 out from the gate, 0 being the corridor's start.</param>
    /// <returns>The spot.</returns>
    /// <remarks>
    /// Clear by construction rather than by a test: the corridor is reserved and the plaza beyond it planted before any building
    /// (<see cref="SpawnGateFacilities"/>), and the pair either side stands 5.5 m off the axis, clear of the lane's 2.4. Not the pad: the ring wall's one
    /// way out is the gate, and nothing that walks in a straight line finds it.
    /// </remarks>
    public static (float X, float Z) OutsideGate(float portX, float portZ, float across, float outward) =>
        (portX + ((across - 0.5f) * GateLaneM), portZ + GateOutsideZ + 2f + (outward * GateLaneDepthM));

    /// <summary>A city NPC's body, half its edge in metres.</summary>
    private const float NpcHalfM = 0.5f;

    /// <summary>The bazaar and the mission terminal beside the gate: either side of its 6 m corridor, a few metres out.</summary>
    public const float GateFacilityX = 5.5f;

    /// <summary>How far out from the shuttleport's centre the gate's pair stands, north.</summary>
    public const float GateFacilityZ = GateOutsideZ + 2.3f;

    /// <summary>
    /// The bazaar and the mission terminal at a shuttleport's gate, and the corridor between them kept clear of buildings.
    /// </summary>
    /// <remarks>
    /// Both face north (yaw 0), so the point a user stands at is on the far side from the gate and the passengers walking out between them. Beyond them
    /// stands the town hall (<see cref="TownHallZ"/>), which took the place of a plaza reserved there to keep a house from being planted across the gate.
    /// </remarks>
    private static void SpawnGateFacilities(Transaction tx, float portX, float portZ, int cityIndex, TerrainField terrain, WorldCensus census,
        SitePlanner plan)
    {
        var none = default(Rng);
        var z = portZ + GateFacilityZ;
        var bx = portX - GateFacilityX;
        var mx = portX + GateFacilityX;
        plan.PlaceAt(bx, z, StructureFootprint.HalfExtentM(StructureKind.Bazaar));
        SpawnStructure(tx, bx, z, terrain.GroundAt(bx, z), StructureFootprint.HalfExtentM(StructureKind.Bazaar), StructureKind.Bazaar,
            ownerRegion: cityIndex + 1, tickPeriod: 0, ref none, faceNorth: true);
        plan.PlaceAt(mx, z, StructureFootprint.HalfExtentM(StructureKind.MissionTerminal));
        SpawnStructure(tx, mx, z, terrain.GroundAt(mx, z), StructureFootprint.HalfExtentM(StructureKind.MissionTerminal), StructureKind.MissionTerminal,
            ownerRegion: cityIndex + 1, tickPeriod: 0, ref none, faceNorth: true);
        // The corridor passengers land in (SimBridge.Shuttles: GateOutsideZ + 2, ±2.4 m), reserved: the margins of the pair and the dome leave a crafting
        // station's width of room in it, and one was free to be planted exactly where a session's passenger is set down (review). Reserved rather than
        // placed, because it stands inside those margins by design.
        plan.Reserve(portX, portZ + GateOutsideZ + 2f, 2.5f);
        census.StaticObjects += 2;
    }

    /// <summary>What a city's structure <paramref name="i"/> is: the shuttleport first, its town hall second, then a crafting station every ninth.</summary>
    public static int CityKind(int i) => i switch
    {
        0 => StructureKind.Shuttleport,
        1 => StructureKind.TownHall,
        _ => i % 9 == 0 ? StructureKind.CraftingStation : StructureKind.Building,
    };

    /// <summary>
    /// How far north of its shuttleport's centre a city's town hall stands, metres: across the way from the gate, its door facing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its ramp starts where the passengers' ground ends.</b> Turned half round (<see cref="TownHallYaw"/>), the published hall's approach starts 18.25 m
    /// in front of its origin and its ramp's foot 17.75 m: at 54 m, 35.75 and 36.25 m north of the port, just past the lane an arriving passenger is set
    /// down on (<see cref="OutsideGate"/>, to 35.2 m), 10.5 m out from the gate, and its doorway 17.5 m out. Nearer and the ramp's foot is under the lane;
    /// its reservation would clear the gate's pair by the planner's margin from 48.7 m.
    /// </para>
    /// <para>
    /// <b>Planted like the port, at a point rather than drawn</b> (<see cref="SitePlanner.PlaceAt"/>), so it costs no draw and a city too small for it says so
    /// in <see cref="SitePlanner.Crowded"/>.
    /// </para>
    /// </remarks>
    public const float TownHallZ = 54f;

    /// <summary>A town hall's quarter turn: half round, so its door, on the asset's +Z, faces south to the gate.</summary>
    public const int TownHallYaw = 2;

    /// <summary>How many of one facility kind a city gets beyond the pair at its shuttleport, from its area and the density.</summary>
    /// <param name="radiusM">The city's radius.</param>
    /// <param name="densityPerKm2">See <see cref="SimConfig.FacilityDensityPerKm2"/>.</param>
    /// <returns>A count, 0 or more.</returns>
    public static int SpreadFacilities(float radiusM, float densityPerKm2)
    {
        var areaKm2 = MathF.PI * radiusM * radiusM / 1_000_000f;
        return Math.Max(0, (int)MathF.Round(densityPerKm2 * areaKm2));
    }

    /// <summary>Candidate spots drawn per facility, of which the one furthest from its kind's others is kept.</summary>
    private const int FacilityCandidates = 16;

    /// <summary>
    /// The city's other bazaars and mission terminals, spread over its disc.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Spread, not scattered.</b> Each is the best of <see cref="FacilityCandidates"/> free spots drawn uniformly over the disc — the one furthest from
    /// every facility of its kind already standing, the gate's included (Mitchell's best-candidate sampling). Uniform draws alone put two terminals
    /// side by side as often as not, and a player's nearest terminal is then far away for no reason.
    /// </para>
    /// <para>
    /// <b>Their own random stream</b>, seeded from the world's and the city, so their draws take nothing from the world's stream. What they occupy still
    /// changes where later structures fit — the planner is shared — so the world's layout is not the one it was before facilities existed.
    /// </para>
    /// </remarks>
    private static void SpawnFacilities(Transaction tx, CityDef city, SimConfig config, int cityIndex, TerrainField terrain, WorldCensus census,
        SitePlanner plan)
    {
        var count = SpreadFacilities(city.Radius, config.FacilityDensityPerKm2);
        if (count == 0)
        {
            return;
        }

        var rng = new Rng(((uint)config.Seed ^ 0xFAC11173u) + ((uint)cityIndex * 0x9E3779B9u) + ((uint)_realm * 0x85EBCA6Bu));
        var port = (X: city.X, Z: city.Z + GateFacilityZ);
        Span<int> kinds = [StructureKind.Bazaar, StructureKind.MissionTerminal];
        Span<(float X, float Z)> placed = stackalloc (float, float)[2 * (count + 1)];
        foreach (var kind in kinds)
        {
            var half = StructureFootprint.HalfExtentM(kind);
            var n = 0;
            placed[n++] = (port.X + (kind == StructureKind.Bazaar ? -GateFacilityX : GateFacilityX), port.Z);
            for (var f = 0; f < count; f++)
            {
                var best = (X: 0f, Z: 0f);
                var bestD = -1f;
                for (var c = 0; c < FacilityCandidates; c++)
                {
                    var (x, z) = rng.PointInDisc(city.X, city.Z, city.Radius - half);
                    if (!plan.Fits(x, z, half))
                    {
                        continue;
                    }

                    var nearest = float.MaxValue;
                    for (var k = 0; k < n; k++)
                    {
                        var dx = placed[k].X - x;
                        var dz = placed[k].Z - z;
                        nearest = MathF.Min(nearest, (dx * dx) + (dz * dz));
                    }

                    if (nearest > bestD)
                    {
                        bestD = nearest;
                        best = (x, z);
                    }
                }

                if (bestD < 0f)
                {
                    continue;   // no free spot in the draws: a full city gets fewer, rather than one planted inside a house
                }

                plan.PlaceAt(best.X, best.Z, half);
                placed[n++] = best;
                SpawnStructure(tx, best.X, best.Z, terrain.GroundAt(best.X, best.Z), half, kind, ownerRegion: cityIndex + 1, tickPeriod: 0, ref rng);
                census.StaticObjects++;
            }
        }
    }

    // ── Space (Realms G1c) ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Edge of the space realm's cube, metres.</summary>
    public const double SpaceEdgeM = 16_000d;

    /// <summary>A starship's half extent, metres.</summary>
    public const double ShipHalfExtentM = 20d;

    /// <summary>Spawn the space realm's starships, each at a random point flying to a random waypoint. Its own RNG stream.</summary>
    public static int PopulateSpace(DatabaseEngine dbe, SimConfig config, ushort realm)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var ships = Scale(config.Starships, config.PopulationScale);
        var rng = new Rng((uint)config.Seed ^ 0x5BD1E995u);
        var key = new ShipRealm(realm);
        var made = 0;
        while (made < ships)
        {
            var n = Math.Min(SpawnBatch, ships - made);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < n; i++)
            {
                var bounds = default(ShipPlacement);
                bounds.SetAt(SpacePoint(ref rng), SpacePoint(ref rng), SpacePoint(ref rng), ShipHalfExtentM);
                var move = new ShipMotion
                {
                    DestX = SpacePoint(ref rng),
                    DestY = SpacePoint(ref rng),
                    DestZ = SpacePoint(ref rng),
                    SpeedMps = 100f + (rng.NextFloat() * 150f),
                };
                tx.Spawn<Starship>(Starship.Bounds.Set(in bounds), Starship.Move.Set(in move), Starship.Realm.Set(in key));
            }

            tx.Commit();
            made += n;
        }

        return made;
    }

    /// <summary>A coordinate inside the space cube, clear of its faces by a ship's size.</summary>
    public static double SpacePoint(ref Rng rng) => (rng.NextFloat() - 0.5f) * (SpaceEdgeM - (4 * ShipHalfExtentM));

    // ── Interiors (Realms G1b) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Edge of an interior realm, metres: one cell, so an interior costs one cell's structures per archetype present.</summary>
    /// <remarks>
    /// <b>Centred on the origin</b>, from -32 to +32 m: a room is authored around its own origin and stands at the realm's, so the room's coordinates —
    /// its walls, its spawns, its navigation — are the realm's with nothing added. It was 0 to 64 m, and the room, the crowd and the interior NPCs then
    /// stood in three different places in one realm.
    /// </remarks>
    public const float InteriorEdgeM = 64f;

    /// <summary>A city's building <paramref name="i"/> has an interior: every one but the shuttleport (0) and the mission terminals (multiples of 9).</summary>
    public static bool IsEnterable(int i) => i != 0 && i % 9 != 0;

    /// <summary>Enterable buildings on one planet — the same on every planet, which share the map. Counted from the map, so realms are registered before
    /// the world is built.</summary>
    public static int CountEnterable(TatooineMap map)
    {
        var n = 0;
        foreach (var city in map.Cities)
        {
            for (var i = 0; i < city.Buildings; i++)
            {
                n += IsEnterable(i) ? 1 : 0;
            }
        }

        return n;
    }

    /// <summary>
    /// Every enterable building of one planet gets its room: the room as a structure of its interior realm, a <see cref="Portal"/> on each side of the
    /// door linked to each other, and the NPCs standing inside.
    /// </summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="config">The run's configuration, for the seed and the NPC count.</param>
    /// <param name="shapes">The published assets, or <see langword="null"/> on a run with none: every door is then the stand-in of its footprint.</param>
    /// <param name="planet">The planet whose buildings are furnished.</param>
    /// <param name="census">Counts the NPCs and the rooms.</param>
    /// <returns>How many buildings were given a room.</returns>
    /// <remarks>
    /// <para>
    /// <b>After the planet is built, from what the build wrote.</b> A building's room realm is on its <see cref="Structure.InteriorRealm"/>, and its turn,
    /// asset and position on the entity; this reads them back rather than threading a list out of the build, so what it furnishes is by construction what
    /// the database holds. Sorted by room so the RNG draws in the same order on every run.
    /// </para>
    /// <para>
    /// <b>Everything a crossing needs is computed here, once, and stored.</b> The door's plane is the published portal, mirrored, turned by the building's
    /// quarter turn and moved to the building; the landing on each side is the published spawn the other side names. A reopened world needs no asset to
    /// know its doors — and a door that moved in a later asset revision moves when the world is rebuilt, like the walls.
    /// </para>
    /// <para>
    /// <b>The interior NPCs stand on the room's navigation nodes</b>, which are published as places a body stands clear of everything, and they stay put: they
    /// are not collided, so a wanderer would walk through the furniture. The node the exit path starts on is left free, and no two stand on one node while a
    /// node is free: a town hall's 10 to 20 on its 50 nodes drawn with replacement put pairs inside each other.
    /// </para>
    /// </remarks>
    public static int BuildInteriors(DatabaseEngine dbe, SimConfig config, AssetColliders shapes, ushort planet, WorldCensus census)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(census);

        var houses = new List<(EntityId Id, StructurePlacement Place, Structure Struct)>();
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<WorldObject>().GetClusterEnumerator())
            {
                if (cluster.Realm.Value != planet)
                {
                    continue;
                }

                var places = cluster.GetReadOnlySpan(WorldObject.Bounds);
                var structs = cluster.GetReadOnlySpan(WorldObject.Struct);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (structs[slot].InteriorRealm != 0)
                    {
                        houses.Add((cluster.GetEntityId(slot), places[slot], structs[slot]));
                    }
                }
            }
        }

        houses.Sort((a, b) => a.Struct.InteriorRealm.CompareTo(b.Struct.InteriorRealm));
        var terrain = TerrainField.Shared(config.ContentScale);
        var rng = new Rng(((uint)config.Seed ^ 0x51ED27A3u) + ((uint)planet * 0x9E3779B9u));
        var spots = new List<(float X, float Z)>();
        var made = 0;
        var tx2 = dbe.CreateQuickTransaction();
        try
        {
            foreach (var (houseId, place, st) in houses)
            {
                var interior = (ushort)st.InteriorRealm;
                var door = shapes?.DoorFor(st.Asset) ?? default;
                if (!door.Exists)
                {
                    door = AssetColliders.StandInDoor(place.HalfExtent);
                }

                var roomAsset = door.TargetAsset;
                var roomDoor = roomAsset != StructureAssets.NoAsset ? shapes.DoorFor(roomAsset) : default;
                if (!roomDoor.Exists)
                {
                    roomAsset = StructureAssets.NoAsset;
                    roomDoor = AssetColliders.StandInRoomDoor;
                }

                // The room, at its realm's origin on its flat floor. A room no asset describes is an open floor and has no structure to stand for it.
                EntityId roomId = default;
                if (roomAsset != StructureAssets.NoAsset)
                {
                    var roomBounds = default(StructurePlacement);
                    roomBounds.SetAt(0f, 0f, 0f, StructureFootprint.HalfExtentM(StructureKind.Interior));
                    var room = new Structure { Kind = StructureKind.Interior, Asset = roomAsset };
                    roomId = tx2.Spawn<WorldObject>(
                        WorldObject.Bounds.Set(in roomBounds),
                        WorldObject.Struct.Set(in room),
                        WorldObject.Region.Set(new StructureRegion(0)),
                        WorldObject.Realm.Set(new StructureRealm(interior)));
                    census.StaticObjects++;
                }

                // The front door, turned and placed with its building; it lands on the room's entry spawn, in the room's own frame.
                var (sin, cos) = WorldIndex.QuarterTurn((byte)st.Yaw);
                var (cx, cz) = Turn(door.CenterX, door.CenterZ, sin, cos);
                var (fx, fz) = Turn(door.ForwardX, door.ForwardZ, sin, cos);
                var (hx, hz) = Turn(door.HalfX, door.HalfZ, sin, cos);
                var front = new PortalDoor
                {
                    CenterX = place.X + cx,
                    CenterZ = place.Z + cz,
                    ForwardX = fx,
                    ForwardZ = fz,
                    HalfWidth = door.HalfX,
                    ArriveX = door.ArriveX,
                    ArriveY = door.ArriveY,
                    ArriveZ = door.ArriveZ,
                    TargetRealm = interior,
                    Owner = houseId,
                };
                var frontId = tx2.Spawn<Portal>(
                    Portal.Bounds.Set(new PortalPlacement { Bounds = BoxAt(front.CenterX, front.CenterZ, MathF.Abs(hx), MathF.Abs(hz)) }),
                    Portal.Door.Set(in front),
                    Portal.Realm.Set(new PortalRealm(planet)));

                // The way out, in the room at the identity; it lands on the building's exit spawn, turned and placed like the door.
                var (ex, ez) = Turn(door.ExitX, door.ExitZ, sin, cos);
                var back = new PortalDoor
                {
                    CenterX = roomDoor.CenterX,
                    CenterZ = roomDoor.CenterZ,
                    ForwardX = roomDoor.ForwardX,
                    ForwardZ = roomDoor.ForwardZ,
                    HalfWidth = roomDoor.HalfX,
                    ArriveX = place.X + ex,
                    ArriveY = door.ExitOnGround ? terrain.GroundAt(place.X + ex, place.Z + ez) : place.Y + door.ExitY,
                    ArriveZ = place.Z + ez,
                    TargetRealm = planet,
                    Twin = frontId,
                    Owner = roomId,
                };
                tx2.Spawn<Portal>(
                    Portal.Bounds.Set(new PortalPlacement { Bounds = BoxAt(back.CenterX, back.CenterZ, roomDoor.HalfX, roomDoor.HalfZ) }),
                    Portal.Door.Set(in back),
                    Portal.Realm.Set(new PortalRealm(interior)));

                // The pair is linked ONE way, by the way out naming the door into its room: the front door is spawned first and its twin is not
                // known yet, and a second write to link it back would leave a window in which a stored world had doors half linked. The table pairs
                // them at open (PortalTable.Build).
                made++;
                spots.Clear();
                NpcSpots(shapes?.NavFor(roomAsset), roomDoor, spots);
                var npcRealm = new NpcRealm(interior);
                var free = spots.Count;
                var npcs = st.Kind == StructureKind.TownHall
                    ? rng.NextInt(config.TownHallNpcsMin, Math.Max(config.TownHallNpcsMin, config.TownHallNpcsMax) + 1)
                    : config.InteriorNpcs;
                for (var n = 0; n < npcs; n++)
                {
                    float x;
                    float z;
                    if (free > 0)
                    {
                        // Without replacement while a node is free: the one taken is swapped behind the ones left.
                        var pick = rng.NextInt(0, free);
                        (x, z) = spots[pick];
                        free--;
                        (spots[pick], spots[free]) = (spots[free], spots[pick]);
                    }
                    else
                    {
                        (x, z) = spots.Count > 0 ? spots[rng.NextInt(0, spots.Count)] : rng.PointInDisc(roomDoor.ExitX, roomDoor.ExitZ, 1.5f);
                    }

                    var bounds = default(NpcPlacement);
                    // An interior is its own realm with its own flat floor, so its altitude is 0 and NOT the planet's ground under the building.
                    bounds.SetAt(x, z, 0f, 0.5f);
                    var ai = new NpcBrain { Mode = AiMode.Idle, HomeX = x, HomeZ = z, LeashRadius = 0f };
                    var npcTimers = new NpcTimers { MoveUntilTick = 0, RestUntilTick = rng.NextInt(1, 40) };
                    var move = new NpcMotion { SpeedMps = 1.2f };
                    tx2.Spawn<CityNpc>(CityNpc.Bounds.Set(in bounds), CityNpc.Ai.Set(in ai), CityNpc.Timers.Set(in npcTimers), CityNpc.Move.Set(in move),
                        CityNpc.Realm.Set(in npcRealm));
                    census.CityNpcs++;
                }

                if (made % 256 == 0)
                {
                    tx2.Commit();
                    tx2.Dispose();
                    tx2 = dbe.CreateQuickTransaction();
                }
            }

            tx2.Commit();
        }
        finally
        {
            tx2.Dispose();
        }

        return made;
    }

    /// <summary>A point or direction in an asset's plan, turned by a placement's quarter turn.</summary>
    private static (float X, float Z) Turn(float x, float z, float sin, float cos) => ((x * cos) - (z * sin), (x * sin) + (z * cos));

    /// <summary>A plan box around a centre.</summary>
    private static AABB2F BoxAt(float x, float z, float halfX, float halfZ) =>
        new() { MinX = x - halfX, MinY = z - halfZ, MaxX = x + halfX, MaxY = z + halfZ };

    /// <summary>Where an interior's NPCs may stand: its navigation nodes, less the one its exit path starts on.</summary>
    /// <param name="nav">The room's navigation, or <see langword="null"/>.</param>
    /// <param name="exit">The room's way out.</param>
    /// <param name="into">Receives the spots; left empty when there is no navigation.</param>
    internal static void NpcSpots(NavGraph nav, in AssetColliders.DoorPlan exit, List<(float X, float Z)> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (nav == null)
        {
            return;
        }

        var door = nav.NodeExactlyAt(exit.ApproachX, exit.ApproachZ);
        for (var i = 0; i < nav.NodeCount; i++)
        {
            if (i != door)
            {
                into.Add(nav.Node(i));
            }
        }
    }

    // ── Points of interest ──────────────────────────────────────────────────────────────────────────────────────────

    private static void SpawnPointsOfInterest(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index,
        SitePlanner plan)
    {
        // The planet's ground, baked once per process (see TerrainField). Taken here rather than per entity: the
        // cache lookup is cheap but not free, and this loop runs seventeen thousand times.
        var terrain = TerrainField.Shared(config.ContentScale);
        // A point of interest's region id continues the cities' numbering; see Structure.OwnerRegion, which stores it + 1 so that 0 can mean "no region".
        var poiIndex = map.Cities.Count;
        foreach (var poi in map.Pois)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                var propHalf = StructureFootprint.HalfExtentM(StructureKind.PoiProp);
                for (var i = 0; i < poi.Props; i++)
                {
                    var (x, z, _) = plan.Place(ref rng, poi.X, poi.Z, poi.Radius, propHalf);
                    SpawnStructure(tx, x, z, terrain.GroundAt(x, z), propHalf, StructureKind.PoiProp, ownerRegion: poiIndex + 1, tickPeriod: 0, ref rng);
                    census.StaticObjects++;
                }

                tx.Commit();
            }

            // A landmark's lairs are the reason players go there, so they are its camps (TatooineData.PoiLairTemplates). Its site is the POI's own
            // position in the list PlanSites made, which numbers the points of interest first.
            SpawnLairsIn(dbe, index, poiIndex - map.Cities.Count, config, census, ref rng);
            index.Pois.Add((poi.X, poi.Z, poi.Radius));
            poiIndex++;
        }
    }

    // ── Wilderness ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static void SpawnWilderness(DatabaseEngine dbe, SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index)
    {
        // The regions follow the points of interest in the site list (PlanSites).
        for (var site = index.Pois.Count; site < index.Sites.Count; site++)
        {
            SpawnLairsIn(dbe, index, site, config, census, ref rng);
        }
    }

    /// <summary>
    /// Plans a planet's lair sites — every point of interest, then every wilderness region — with the number of lairs each keeps standing.
    /// </summary>
    /// <remarks>
    /// <b>From the map and the configuration alone, so a reopened world plans the identical list</b> (<c>WorldRebuild</c>): a lair on disk names its site by
    /// position in it (<see cref="Lair.Site"/>), and the director refills each site to the target planned here.
    /// </remarks>
    /// <param name="map">The planet's geometry.</param>
    /// <param name="config">The run's scales.</param>
    /// <param name="index">The index whose <see cref="WorldIndex.Sites"/> is filled. Must hold none yet.</param>
    /// <param name="realm">The planet.</param>
    internal static void PlanSites(TatooineMap map, SimConfig config, WorldIndex index, ushort realm)
    {
        if (index.Sites.Count != 0)
        {
            throw new InvalidOperationException($"PlanSites needs an index with no sites; this one holds {index.Sites.Count}.");
        }

        foreach (var poi in map.Pois)
        {
            index.Sites.Add(new LairSite(realm, poi.X, poi.Z, poi.Radius, TatooineData.PoiLairTemplates, Scale(poi.Lairs, config.PopulationScale)));
        }

        foreach (var region in map.SpawnRegions)
        {
            var areaSqKm = MathF.PI * region.Radius * region.Radius / 1_000_000f;
            var lairs = Scale((int)MathF.Round(areaSqKm * region.LairsPerSqKm), config.PopulationScale);
            index.Sites.Add(new LairSite(realm, region.X, region.Z, region.Radius, region.Templates, lairs));
        }
    }

    /// <summary>
    /// Fills one site with its lairs, each with its creatures, in batches.
    /// </summary>
    /// <remarks>
    /// Lair and creatures go in the SAME transaction, so the batch Morton sort sees them together and a lair's creatures
    /// are born into the cluster their lair is in rather than wherever the free-slot search happened to be. That is the
    /// difference between a world whose clusters are tight from birth and one that spends its first hundred ticks
    /// repairing itself into shape.
    /// </remarks>
    private static void SpawnLairsIn(DatabaseEngine dbe, WorldIndex index, int site, SimConfig config, WorldCensus census, ref Rng rng)
    {
        // The planet's ground, baked once per process (see TerrainField). Taken here rather than per entity: the
        // cache lookup is cheap but not free, and this loop runs seventeen thousand times.
        var terrain = TerrainField.Shared(config.ContentScale);
        var at = index.Sites[site];
        if (at.Target <= 0)
        {
            return;
        }

        // A lair is at most nine entities, so a batch of SpawnBatch / 10 lairs stays inside the transaction size the rest of the build uses.
        var lairsPerBatch = Math.Max(1, SpawnBatch / 10);
        var done = 0;

        // Part-way through a life already: see SimConfig.LairLifeMinSeconds for why the first retirements are spread rather than arriving as one wave.
        var lifeMin = (long)(config.LairLifeMinSeconds * 0.25f * config.TickRateHz);
        var lifeMax = (long)(config.LairLifeMaxSeconds * config.TickRateHz);
        while (done < at.Target)
        {
            var n = Math.Min(lairsPerBatch, at.Target - done);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < n; i++)
            {
                var (lx, lz) = Inside(rng.PointInDisc(at.X, at.Z, at.Radius), config);
                var template = CreatureTemplates.PickLair(at.Templates, ref rng);
                var expire = lifeMin + (long)(rng.NextFloat() * (lifeMax - lifeMin));
                var lairId = SpawnLair(tx, ref rng, template, lx, lz, site + 1, expire, config, terrain, _realm);
                census.Lairs++;
                census.Creatures += CreatureTemplates.SpawnLimit[template];
                census.CreaturesByTemplate[template] += CreatureTemplates.SpawnLimit[template];
                index.Lairs.Add(lairId);
            }

            tx.Commit();
            done += n;
        }

        at.Live = at.Target;
    }

    /// <summary>The half-extent of a lair's placement box, metres: the published nest and camps are about 6 m across.</summary>
    internal const float LairHalfM = 4f;

    /// <summary>
    /// How far from the lair's centre its creatures are born, metres: the published <c>creature_nest</c>'s four <c>creature.N</c> spawn markers stand
    /// 3.4 m out, on the diagonals.
    /// </summary>
    internal const float LairRingM = 3.4f;

    /// <summary>
    /// Creates a lair and every creature it keeps alive, in one transaction — the world build's and the lair director's one way to make a lair.
    /// </summary>
    /// <param name="tx">The transaction both go in.</param>
    /// <param name="rng">Draws the creatures' places and outfits.</param>
    /// <param name="template">The <see cref="CreatureTemplates"/> entry.</param>
    /// <param name="x">Where, east.</param>
    /// <param name="z">Where, north.</param>
    /// <param name="site">The <see cref="Lair.Site"/>: one-based, 0 for a destroy-mission lair.</param>
    /// <param name="expireTick">The tick the lair is retired at; 0 never.</param>
    /// <param name="config">The run's scales.</param>
    /// <param name="terrain">The planet's ground.</param>
    /// <param name="realm">The planet.</param>
    /// <returns>The lair.</returns>
    internal static EntityId SpawnLair(Transaction tx, ref Rng rng, int template, float x, float z, int site, long expireTick, SimConfig config,
        TerrainField terrain, ushort realm)
    {
        var perLair = CreatureTemplates.SpawnLimit[template];
        var lairBounds = default(LairPlacement);
        lairBounds.SetAt(x, z, terrain.GroundAt(x, z), LairHalfM);
        var lair = new Lair
        {
            CreatureTemplate = template,
            SpawnLimit = perLair,
            AliveCount = perLair,
            Guards = perLair,
            RespawnCooldown = 0,
            SpawnRadius = CreatureTemplates.LairSpawnRadius[template] * config.ContentScale,
            MissionId = 0,
            Site = site,
            ExpireTick = expireTick,
        };
        var lairHealth = CreatureTemplates.LairHealth(template);
        var lairVitals = new LairVitals { Health = lairHealth, MaxHealth = lairHealth };
        var lairId = tx.Spawn<CreatureLair>(
            CreatureLair.Bounds.Set(in lairBounds),
            CreatureLair.Spawner.Set(in lair),
            CreatureLair.Vitals.Set(in lairVitals),
            CreatureLair.Realm.Set(new LairRealm(realm)));

        for (var k = 0; k < perLair; k++)
        {
            SpawnCreature(tx, ref rng, lairId, template, x, z, k, config, terrain, realm);
        }

        return lairId;
    }

    /// <summary>Create one creature belonging to a lair.</summary>
    /// <param name="slot">
    /// Which place round the lair it is born at: the four spawn markers, then a second ring outside them, each with a little jitter so a lair's creatures
    /// do not stand on one another.
    /// </param>
    /// <param name="terrain">
    /// The planet's ground, baked once per process (see <see cref="TerrainField"/>). A <b>parameter</b>, because this
    /// method IS the per-entity call site — it runs once per creature, seventeen thousand times in a small world — and
    /// <see cref="TerrainField.Shared"/> is a concurrent-dictionary probe. The comment that used to sit here claimed the
    /// opposite of what the code did.
    /// </param>
    internal static EntityId SpawnCreature(Transaction tx, ref Rng rng, EntityId lairId, int template,
        float lairX, float lairZ, int slot, SimConfig config, TerrainField terrain, ushort realm)
    {
        // Jittered along the ring and OUTWARD only: inward would put a body in the nest's own collider (2.15 m) or the camp's tent.
        var ring = (slot / 4) + 1;
        var angle = (MathF.PI * 0.25f) + ((slot % 4) * MathF.PI * 0.5f) + ((ring - 1) * MathF.PI * 0.25f) + rng.NextRange(-0.25f, 0.25f);
        // Not scaled by ContentScale: the ring is the published prop's own geometry, which is the same size in every world.
        var radius = (LairRingM * ring) + rng.NextRange(0f, 1.2f);
        var (x, z) = Inside(lairX + (MathF.Cos(angle) * radius), lairZ + (MathF.Sin(angle) * radius), config);
        var bounds = default(CreaturePlacement);
        bounds.SetAt(x, z, terrain.GroundAt(x, z), 1.5f);

        // AMBIENT: a creature that never thinks and never moves, so its cluster can actually go quiet.
        //
        // Decided PER LAIR, not per creature, and that is the whole difference between a knob that works and one that does not. Clusters are spatial and
        // a lair's creatures are spawned inside one disc, so they share clusters; drawing the coin per creature leaves every cluster holding a mix, one
        // active member is enough to keep a cluster dirty, and nothing ever sleeps. Measured: half the creatures ambient, drawn per creature, produced
        // ZERO dormant clusters. The hash is of the lair's own position, so it is stable across arms without threading extra state through the call.
        var lairHash = (uint)(BitConverter.SingleToInt32Bits(lairX) * 0x9E3779B1) ^ (uint)(BitConverter.SingleToInt32Bits(lairZ) * 0x85EBCA77);
        var ambient = config.IdleCreatureFraction > 0d
            && (lairHash % 1000u) < (uint)(config.IdleCreatureFraction * 1000d);

        var ai = new CreatureBrain
        {
            Mode = ambient ? AiMode.Idle : AiMode.Wander,
            HomeX = lairX,
            HomeZ = lairZ,
            LeashRadius = TatooineData.LeashRadiusM * config.ContentScale,
            AggroRadius = CreatureTemplates.Aggressive[template] ? TatooineData.AggroRadiusM * config.ContentScale : 0f,
            Template = (byte)template,
            Skin = (byte)rng.NextInt(0, CreatureTemplates.Bodies[template].Length),
        };

        // Staggered so a lair's creatures do not all think on the same tick. Core3's own interval is 400-1000 ms, which at this tick rate is four to ten
        // ticks — the AI is deliberately not a per-tick cost. In its own component, because nothing on the wire reads it (see CreatureTimers).
        var timers = new CreatureTimers { ThinkCooldown = rng.NextInt(1, AiTicksMax(config)), AttackCooldown = rng.NextInt(0, 8) };
        var move = new CreatureMotion { SpeedMps = CreatureTemplates.SpeedMps[template] };
        var vitals = new CreatureVitals
        {
            Health = CreatureTemplates.Health[template],
            MaxHealth = CreatureTemplates.Health[template],
            AttackDamage = CreatureTemplates.Damage[template],
        };
        return tx.Spawn<Creature>(
            Creature.Bounds.Set(in bounds),
            Creature.Ai.Set(in ai),
            Creature.Timers.Set(in timers),
            Creature.Move.Set(in move),
            Creature.Vitals.Set(in vitals),
            Creature.Realm.Set(new CreatureRealm(realm)),
            Creature.Home.Set(new CreatureHome { Lair = lairId }));
    }

    // ── Player structures ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Houses, factories and harvesters, scattered over the wilderness the way players actually built them.
    /// </summary>
    /// <remarks>
    /// <para>The real placement rule is a terrain-flag test rather than a radius, and no single numeric minimum was ever
    /// published — but two things about it ARE documented and are honoured here: structures may not go inside an NPC
    /// city, and a player city needs a thousand metres of clearance from another. So structures are rejected inside city
    /// radii, and clumped around a set of player-city sites that respect the 1 000 m separation.</para>
    /// <para>Clumping matters more than the exact rule. Player construction in SWG was intensely social — houses went up
    /// next to friends' houses — so the structure population is a set of knots in otherwise empty desert, and that is a
    /// different spatial signature from the uniform scatter a naive model would produce.</para>
    /// </remarks>
    private static void SpawnPlayerStructures(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng, SitePlanner plan)
    {
        // The planet's ground, baked once per process (see TerrainField). Taken here rather than per entity: the
        // cache lookup is cheap but not free, and this loop runs seventeen thousand times.
        var terrain = TerrainField.Shared(config.ContentScale);
        var total = Scale(BaselinePlayerStructures, config.PopulationScale);
        if (total <= 0)
        {
            return;
        }

        // Two thirds of structures sit in player-city knots; the rest are lone harvesters out in the resource fields.
        var sites = BuildPlayerCitySites(map, config, ref rng);
        var inCities = (int)(total * 0.66f);

        // PLANNED, where these used to be raw draws — the defect SitePlanner exists for and the one place it had not reached. A rank-0 knot is 150 m
        // across and takes about 143 of the 2 600 structures, so player houses stood inside each other at the REAL planet size, not merely at a compressed
        // one: 74 overlapping pairs in a finished 16.384 km world, every one of them a player structure.
        //
        // A knot that is full sends its next structure out to the desert instead, which keeps the total the map asked for and is a shape the model already
        // has — a third of them are lone harvesters anyway. Fullness is discovered rather than computed: the planner is asked and says no, which is exact,
        // where a capacity would have to guess the mix of houses, factories and harvesters a knot happens to draw. It is the CALLER's state because the
        // planner is the planet's, so one full knot must not stop another from being searched.
        //
        // Held per knot as a SIZE rather than a flag: a knot with no room for a 14 m house usually still has room for a 6 m factory, and a flag would
        // send both out to the desert. What the knot has proved is that nothing this big fits, so that is what is remembered.
        var fullAt = new float[sites.Count];
        Array.Fill(fullAt, float.MaxValue);
        var homeless = 0;

        var made = 0;
        while (made < total)
        {
            var n = Math.Min(SpawnBatch, total - made);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < n; i++)
            {
                // The KIND first, because it decides the footprint a spot has to be planned for. A house never ticks. A factory runs a batch; a harvester
                // extracts. SWG's own numbers: manufacturing is complexity x 8 seconds per unit, and a harvester's rate is units per minute — so both are
                // periods of tens of seconds to minutes, which at this tick rate is hundreds to thousands of ticks.
                var roll = rng.NextFloat();
                var (kind, period) = roll < 0.55f
                    ? (StructureKind.PlayerHouse, 0)
                    : roll < 0.78f
                        ? (StructureKind.Harvester, 600)
                        : (StructureKind.Factory, 1800);

                // The footprint the client draws for this kind, which used to be a flat 10 or 14 here while the city loop had already moved to the table.
                var half = StructureFootprint.HalfExtentM(kind);

                var site = made + i < inCities && sites.Count > 0 ? rng.NextInt(0, sites.Count) : -1;
                if (site >= 0 && half >= fullAt[site])
                {
                    site = -1;
                }

                float x, z;
                if (site >= 0 && plan.TryPlace(ref rng, sites[site].X, sites[site].Z, sites[site].Radius, half, out x, out z))
                {
                    // In the knot it drew.
                }
                else
                {
                    if (site >= 0)
                    {
                        // The knot had no room for this one, so it has none for the next of this size or larger.
                        fullAt[site] = MathF.Min(fullAt[site], half);
                    }

                    // Out in the desert, where the candidates come from a keep-out rejection rather than from a disc, so the planner only gets to veto them.
                    (x, z) = RandomWildernessPoint(map, config, ref rng);
                    var fitted = plan.TryPlaceAt(x, z, half);
                    for (var attempt = 1; !fitted && attempt < SitePlanner.Attempts; attempt++)
                    {
                        (x, z) = RandomWildernessPoint(map, config, ref rng);
                        fitted = plan.TryPlaceAt(x, z, half);
                    }

                    if (!fitted)
                    {
                        homeless++;
                    }
                }

                // ownerRegion 0: these stand in the wild, and 0 is how Structure.OwnerRegion spells "no region" now that it is a sparse index's key.
                SpawnStructure(tx, x, z, terrain.GroundAt(x, z), half, kind, ownerRegion: 0, tickPeriod: period, ref rng);
                census.PlayerStructures++;
            }

            tx.Commit();
            made += n;
        }

        if (homeless > 0)
        {
            // Not the knots being full — that is handled by sending the structure out here — but the DESERT being full, which means the planet itself has
            // no clear ground left. Only reachable by compressing the world far enough that 2 600 structures do not fit on it.
            Console.WriteLine($"  !! {homeless} of {total} player structures found no clear ground on the whole planet and overlap");
        }
    }

    /// <summary>
    /// Choose player-city sites that honour Core3's 1 000 m minimum separation and stay out of the NPC cities.
    /// </summary>
    private static List<(float X, float Z, float Radius)> BuildPlayerCitySites(TatooineMap map, SimConfig config, ref Rng rng)
    {
        var sites = new List<(float X, float Z, float Radius)>();
        var separation = TatooineData.PlayerCityMinSeparationM * config.ContentScale;
        var attempts = 0;

        // Twelve is not a sourced number — the per-planet city cap was never published, only that one existed. It is
        // chosen so the sites cannot tile the planet: at 1 km separation a 16 km world would hold far more, and a world
        // whose player cities are everywhere is not the world SWG had.
        while (sites.Count < 12 && attempts++ < 4000)
        {
            var (x, z) = RandomWildernessPoint(map, config, ref rng);
            var ok = true;
            foreach (var s in sites)
            {
                var dx = s.X - x;
                var dz = s.Z - z;
                if ((dx * dx) + (dz * dz) < separation * separation)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                // Rank drawn from the tier table; most player cities never grew past Township.
                var rank = rng.NextFloat() < 0.55f ? 0 : rng.NextFloat() < 0.7f ? 2 : 4;

                // The disc is TRIMMED to what lies inside the planet. A site centre is drawn at 0.97 of the half-extent and a rank-4 radius is 450 m, so an
                // untrimmed site reaches past the edge; a structure drawn out there is clamped back in (#1073) to a spot nothing tested, and two structures
                // clamped against the same edge stand inside each other. Trimming is what makes a planned spot the spot actually used.
                var reach = (config.WorldEdgeM * 0.5f * InsideEdge) - MathF.Max(MathF.Abs(x), MathF.Abs(z));
                var radius = MathF.Min(TatooineData.PlayerCityRadiusM[rank] * config.ContentScale, reach);
                if (radius > 0f)
                {
                    sites.Add((x, z, radius));
                }
            }
        }

        return sites;
    }

    // ── Players ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void SpawnPlayers(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index,
        SitePlanner plan)
    {
        // The planet's ground, baked once per process (see TerrainField). Taken here rather than per entity: the
        // cache lookup is cheap but not free, and this loop runs seventeen thousand times.
        var terrain = TerrainField.Shared(config.ContentScale);
        var total = Scale(BaselinePlayers, config.PopulationScale);
        var made = 0;
        while (made < total)
        {
            var n = Math.Min(SpawnBatch, total - made);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < n; i++)
            {
                // Players are born mid-activity, not all standing in a city waiting to decide.
                //
                // The first version put every player in a city with an Idle timer, and a measurement taken over the next
                // few hundred ticks then reported a world in which nobody had yet reached anywhere: a player travelling
                // to a point of interest at 12 m/s needs several minutes of simulated time to arrive, so combat, kills
                // and respawns were all zero for the whole measured window. Seeding the activity mix AT SPAWN — and
                // placing each player where that activity would have taken it — puts the world in steady state on tick
                // one, which is what a running server actually looks like.
                var cityIndex = WeightedCity(map, ref rng);
                var city = map.Cities[cityIndex];
                var roll = rng.NextFloat();
                float x, z;
                int activity;
                var shuttleDest = 0;
                var activityTicks = 0;
                if (roll < 0.40f)
                {
                    activity = PlayerActivity.Idle;
                    (x, z) = rng.PointInDisc(city.X, city.Z, city.Radius);
                }
                else if (roll < 0.60f && config.Shuttles && index.Shuttleports.Count == map.Cities.Count && map.Cities.Count >= 2
                         && rng.NextFloat() < config.ShuttleShare * 0.5f)
                {
                    // Queued at the home port for a shuttle (#910), so the first landing of every port finds a queue — a run long enough to walk there
                    // is longer than a measurement. Half the shuttle share of travellers [EST], about 5 % of players at the default. The extra draw is
                    // taken only with shuttles on, so a --no-shuttles world is the pre-shuttle world exactly.
                    activity = PlayerActivity.AwaitingShuttle;
                    // Outside the gate, not on the pad inside the ring wall, where a passenger left behind could never walk out. Two draws, as the disc
                    // round the pad took, so every later body is where it was.
                    var port = index.Shuttleports[cityIndex];
                    (x, z) = OutsideGate(port.X, port.Z, rng.NextFloat(), rng.NextFloat());
                    shuttleDest = (cityIndex + rng.NextInt(1, map.Cities.Count)) % map.Cities.Count;
                    activityTicks = (int)(2f * (config.ShuttleIntervalS + config.BoardingWindowS) * config.TickRateHz);
                }
                else if (roll < 0.60f)
                {
                    // Somewhere along a leg between two cities.
                    activity = PlayerActivity.Travelling;
                    var other = map.Cities[rng.NextInt(0, map.Cities.Count)];
                    var f = rng.NextFloat();
                    x = city.X + ((other.X - city.X) * f);
                    z = city.Z + ((other.Z - city.Z) * f);
                }
                else if (roll < 0.82f && map.Pois.Count > 0)
                {
                    // At a point of interest, fighting — which is where the lairs are.
                    activity = PlayerActivity.Combat;
                    var poi = map.Pois[rng.NextInt(0, map.Pois.Count)];
                    (x, z) = rng.PointInDisc(poi.X, poi.Z, poi.Radius);
                }
                else
                {
                    activity = PlayerActivity.Roaming;
                    // Six city radii clears the planet for any city near the rim. The shipped map happens to leave 688 m
                    // of margin; a city edit or a radius retune is all it would take to spend it (#1073).
                    (x, z) = Inside(rng.PointInDisc(city.X, city.Z, city.Radius * 6f), config);
                }

                if (activity != PlayerActivity.AwaitingShuttle)
                {
                    // Out of any structure the draw landed in, as a city NPC is (see SpawnCities). Not a queued passenger: its lane is reserved ground,
                    // which counts as taken.
                    plan.PushOut(ref x, ref z, TatooineData.PlayerBodyHalfM);
                }

                var bounds = default(PlayerPlacement);
                bounds.SetAt(x, z, terrain.GroundAt(x, z), TatooineData.PlayerBodyHalfM);
                var state = new PlayerState
                {
                    Activity = activity,
                    ActivityTicks = activity == PlayerActivity.AwaitingShuttle ? activityTicks : rng.NextInt(1, 400),
                    MissionX = x,
                    MissionZ = z,
                    HomeCity = cityIndex,
                    ShuttleFrom = cityIndex,
                    ShuttleDest = shuttleDest,
                };
                var move = new PlayerMotion
                {
                    SpeedMps = TatooineData.PlayerRunSpeedMps,
                    DestX = x,
                    DestZ = z,
                };
                var vitals = new PlayerVitals { Health = 1400, MaxHealth = 1400, AttackDamage = 95, AttackCooldown = rng.NextInt(0, 10) };
                var inv = new Inventory { Credits = 5_000, ItemCount = 0, ItemValue = 0 };
                var id = tx.Spawn<Player>(
                    Player.Bounds.Set(in bounds),
                    Player.Move.Set(in move),
                    Player.State.Set(in state),
                    Player.Vitals.Set(in vitals),
                    Player.Inventory.Set(in inv),
                    Player.Realm.Set(new PlayerRealm(_realm)));
                index.Players.Add(id);
                census.Players++;
            }

            tx.Commit();
            made += n;
        }
    }

    // ── Destroy-mission lair pool ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A pool of dormant destroy-mission lairs, recycled by <c>SimBridge.MissionTick</c>.
    /// </summary>
    /// <remarks>
    /// <para>A pool rather than spawning one per mission, because structural spawns during a tick are unavailable
    /// (#907). A dormant lair sits where it was born until it finds a player, then teleports 1-2 km from them and goes
    /// live; when destroyed it goes dormant again where it stands. The population is therefore constant and the churn is
    /// all migration, which is the half of the cost this workload can measure honestly.</para>
    /// <para>The size is <see cref="BaselinePlayers"/> / 4 — SWG let a player hold two missions at a time, and a
    /// fraction of the population is on one at any moment. It scales with population because mission demand does.</para>
    /// </remarks>
    private static void SpawnMissionLairPool(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index)
    {
        // The planet's ground, baked once per process (see TerrainField). Taken here rather than per entity: the
        // cache lookup is cheap but not free, and this loop runs seventeen thousand times.
        var terrain = TerrainField.Shared(config.ContentScale);
        var count = Scale(BaselinePlayers / 4, config.PopulationScale);
        if (count <= 0)
        {
            return;
        }

        var template = CreatureTemplates.MissionDefender;
        var perLair = CreatureTemplates.SpawnLimit[template];
        var made = 0;

        while (made < count)
        {
            var n = Math.Min(Math.Max(1, SpawnBatch / (perLair + 1)), count - made);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < n; i++)
            {
                var (lx, lz) = Inside(RandomWildernessPoint(map, config, ref rng), config);
                var lairId = SpawnLair(tx, ref rng, template, lx, lz, site: 0, expireTick: 0, config, terrain, _realm);

                // Dormant: no mission, and an offer cadence staggered so the pool does not seek players on one tick. [CORE3] difficulty x (900 +
                // random(200)) hit points; a mid difficulty until one is assigned.
                var pooled = tx.OpenMut(lairId);
                var lair = pooled.Read(CreatureLair.Spawner);
                lair.RespawnCooldown = rng.NextInt(1, 50);
                pooled.Set(CreatureLair.Spawner, lair);
                pooled.Set(CreatureLair.Vitals, new LairVitals { Health = 4500, MaxHealth = 4500 });
                census.Lairs++;
                census.Creatures += perLair;
                census.CreaturesByTemplate[template] += perLair;
                index.Lairs.Add(lairId);
            }

            tx.Commit();
            made += n;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EST] Concurrent players on a busy Tatooine. A live galaxy peaked in the low thousands across roughly ten planets,
    /// with the starter worlds carrying two to three times their share — so several hundred is the defensible order. The
    /// emulator era is far smaller (SWGEmu's Basilisk peaked at 122 concurrent for the WHOLE galaxy), which is a useful
    /// reminder that this baseline is the ambitious reading, not the conservative one.
    /// </summary>
    private const int BaselinePlayers = 320;

    /// <summary>
    /// [EST] Player structures standing on a busy planet. No per-planet cap was ever published; what is known is that
    /// each character held ten lots and that houses, factories and harvesters all consumed them. A planet whose active
    /// population is a few hundred has a total playerbase many times that, and structures persist while players do not.
    /// </summary>
    private const int BaselinePlayerStructures = 2_600;

    private static int Scale(int baseline, float scale) => (int)MathF.Round(baseline * scale);

    /// <summary>
    /// The fraction of the half-extent a generated placement is kept inside (#1073).
    /// </summary>
    /// <remarks>
    /// The structure path has used this since it was written; the lair path did not, and several spawn regions reach
    /// past the planet on their own geometry — <c>Southern Wastes</c> is centred at z = −6 800 with a 2 300 m radius
    /// against a half-extent of 8 192. A creature was directly observed alive at z = −8 285.9, 94 m outside the world.
    /// </remarks>
    internal const float InsideEdge = 0.97f;

    /// <summary>
    /// Keeps a generated point inside the world, and <b>counts every time it had to</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counting is the point. The engine clamps a position written outside its grid and says nothing, so a region
    /// whose geometry does not fit produces a knot of entities pressed against the boundary that looks like real
    /// geography to anything measuring cell occupancy — which is what the cell-size sweep measures. A clamp that reports
    /// itself is a finding; a silent one is a distortion of every number taken afterwards.
    /// </para>
    /// <para>
    /// It clamps rather than rejects because the region definitions are authored data and a demo that refuses to build
    /// its world helps nobody at 3 a.m.; <see cref="ClampedPlacements"/> is how it says so instead.
    /// </para>
    /// </remarks>
    public static (float X, float Z) Inside((float X, float Z) at, SimConfig config) => Inside(at.X, at.Z, config);

    public static (float X, float Z) Inside(float x, float z, SimConfig config)
    {
        var half = config.WorldEdgeM * 0.5f * InsideEdge;
        var cx = Math.Clamp(x, -half, half);
        var cz = Math.Clamp(z, -half, half);
        if (cx != x || cz != z)
        {
            Interlocked.Increment(ref _clampedPlacements);
        }

        return (cx, cz);
    }

    private static long _clampedPlacements;

    /// <summary>
    /// How many generated placements the world edge had to move, since the process started (#1073).
    /// </summary>
    /// <remarks>
    /// Non-zero means a spawn region's disc reaches past the planet. That is authored data being wrong rather than the
    /// builder being wrong, and it is worth seeing rather than absorbing.
    /// </remarks>
    public static long ClampedPlacements => Interlocked.Read(ref _clampedPlacements);

    /// <summary>Forgets the count. For a test that wants to attribute clamps to one world build.</summary>
    public static void ResetClampedPlacements() => Interlocked.Exchange(ref _clampedPlacements, 0);

    /// <summary>Ticks between AI decisions, from Core3's 400-1000 ms behaviour interval at this simulation's tick rate.</summary>
    internal static int AiTicksMax(SimConfig config) => Math.Max(2, TatooineData.AiIntervalMaxMs * config.TickRateHz / 1000);

    internal static int AiTicksMin(SimConfig config) => Math.Max(1, TatooineData.AiIntervalMinMs * config.TickRateHz / 1000);

    /// <param name="groundY">
    /// Altitude of the ground under it. A parameter rather than a lookup inside, because this helper places both planet
    /// buildings and the props inside an interior, and an interior's floor is 0 rather than the planet's relief.
    /// </param>
    private static void SpawnStructure(Transaction tx, float x, float z, float groundY, float halfExtent, int kind, int ownerRegion, int tickPeriod,
        ref Rng rng, int interiorRealm = 0, bool faceNorth = false, int yaw = -1)
    {
        var bounds = default(StructurePlacement);
        bounds.SetAt(x, z, groundY, halfExtent);
        var s = new Structure
        {
            Kind = kind,
            InteriorRealm = interiorRealm,
            TickPeriod = tickPeriod,

            // A quarter turn, drawn here so it is the world's and not a renderer's guess. The shuttleport keeps 0: it
            // is the one structure whose orientation means something, because its landing pad is where shuttles arrive.
            Yaw = yaw >= 0 ? yaw : kind == StructureKind.Shuttleport || faceNorth ? 0 : rng.NextInt(0, 4),
            Asset = StructureAssets.Pick(kind, ref rng),

            // Staggered, so the economy does not arrive as a once-a-minute spike that the median tick never sees.
            TickCountdown = tickPeriod == 0 ? 0 : rng.NextInt(1, tickPeriod),
        };
        tx.Spawn<WorldObject>(
            WorldObject.Bounds.Set(in bounds),
            WorldObject.Struct.Set(in s),
            WorldObject.Region.Set(new StructureRegion(ownerRegion)),
            WorldObject.Realm.Set(new StructureRealm(_realm)));
    }

    /// <summary>A point on the planet that is not inside an NPC city.</summary>
    private static (float X, float Z) RandomWildernessPoint(TatooineMap map, SimConfig config, ref Rng rng)
    {
        var half = config.WorldEdgeM * 0.5f * 0.97f;
        for (var attempt = 0; attempt < 24; attempt++)
        {
            var x = rng.NextRange(-half, half);
            var z = rng.NextRange(-half, half);
            var clear = true;
            foreach (var c in map.Cities)
            {
                var dx = c.X - x;
                var dz = c.Z - z;
                var keepOut = c.Radius * 1.5f;
                if ((dx * dx) + (dz * dz) < keepOut * keepOut)
                {
                    clear = false;
                    break;
                }
            }

            if (clear)
            {
                return (x, z);
            }
        }

        return (rng.NextRange(-half, half), rng.NextRange(-half, half));
    }

    private static int WeightedCity(TatooineMap map, ref Rng rng)
    {
        var roll = rng.NextFloat();
        var acc = 0f;
        for (var i = 0; i < map.Cities.Count; i++)
        {
            acc += map.Cities[i].PlayerWeight;
            if (roll <= acc)
            {
                return i;
            }
        }

        return map.Cities.Count - 1;
    }
}
