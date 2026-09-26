using System;
using System.Collections.Generic;

namespace SwgTatooine;

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
    public static WorldCensus Populate(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldIndex index = null, ushort realm = 0)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(config);

        var census = new WorldCensus();
        // Planet 0's seed is the run's seed, so a one-planet run is the world it always was; a further planet draws its own.
        var rng = new Rng((uint)config.Seed + realm * 0x9E3779B9u);
        index ??= new WorldIndex();
        _realm = realm;

        SpawnCities(dbe, map, config, census, ref rng, index);
        SpawnPointsOfInterest(dbe, map, config, census, ref rng, index);
        SpawnWilderness(dbe, map, config, census, ref rng, index);
        SpawnPlayerStructures(dbe, map, config, census, ref rng);
        SpawnPlayers(dbe, map, config, census, ref rng, index);
        SpawnMissionLairPool(dbe, map, config, census, ref rng, index);

        return census;
    }

    // The planet being populated. The build is serial (one planet after another, on the opening thread), so every spawn helper reads it here rather than
    // threading a parameter through all of them.
    private static ushort _realm;

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
    private static void SpawnCities(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index)
    {
        foreach (var city in map.Cities)
        {
            var buildings = city.Buildings;
            var npcs = Scale(city.Npcs, config.PopulationScale);
            var firstPortal = index.Portals.Count;

            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < buildings; i++)
                {
                    var (x, z) = rng.PointInDisc(city.X, city.Z, city.Radius);
                    var kind = i == 0 ? StructureKind.Shuttleport : (i % 9 == 0 ? StructureKind.Terminal : StructureKind.Building);
                    if (i == 0)
                    {
                        // Kept for shuttle travel (#910). Recording a coordinate the RNG already produced draws nothing, so the world is unchanged.
                        index.Shuttleports.Add((x, z));
                    }
                    else if (IsEnterable(i))
                    {
                        index.Portals.Add((x, z));   // Realms G1b: this building's door. Draws nothing, like the shuttleport above.
                    }

                    SpawnStructure(tx, x, z, 12f, kind, ownerRegion: 0, tickPeriod: 0, ref rng);
                    census.StaticObjects++;
                }

                tx.Commit();
            }

            var made = 0;
            while (made < npcs)
            {
                var n = Math.Min(SpawnBatch, npcs - made);
                using var tx = dbe.CreateQuickTransaction();
                for (var i = 0; i < n; i++)
                {
                    var (x, z) = rng.PointInDisc(city.X, city.Z, city.Radius);
                    var bounds = default(NpcPlacement);
                    bounds.SetAt(x, z, 0.5f);

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

            index.Cities.Add((city.X, city.Z, city.Radius, city.PlayerWeight));
            index.CityPortals.Add((firstPortal, index.Portals.Count - firstPortal));
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
    /// The NPCs standing in each of one planet's interiors: portal <c>j</c>'s interior is realm <paramref name="firstRealm"/> + j. Its own RNG stream, so the
    /// planets' worlds are the same with interiors on or off.
    /// </summary>
    public static int PopulateInteriors(DatabaseEngine dbe, SimConfig config, WorldIndex index, int firstRealm, WorldCensus census)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        var rng = new Rng(((uint)config.Seed ^ 0x51ED27A3u) + ((uint)firstRealm * 0x9E3779B9u));
        var made = 0;
        var tx = dbe.CreateQuickTransaction();
        try
        {
            for (var j = 0; j < index.Portals.Count; j++)
            {
                var realm = new NpcRealm((ushort)(firstRealm + j));
                for (var n = 0; n < config.InteriorNpcs; n++)
                {
                    var c = InteriorEdgeM * 0.5f;
                    var (x, z) = rng.PointInDisc(c, c, InteriorEdgeM * 0.25f);
                    var bounds = default(NpcPlacement);
                    bounds.SetAt(x, z, 0.5f);
                    var ai = new NpcBrain { Mode = rng.NextFloat() < 0.5f ? AiMode.Wander : AiMode.Idle, HomeX = x, HomeZ = z, LeashRadius = 6f };
                    var npcTimers = new NpcTimers { MoveUntilTick = 0, RestUntilTick = rng.NextInt(1, 40) };
                    var move = new NpcMotion { SpeedMps = 1.2f };
                    tx.Spawn<CityNpc>(CityNpc.Bounds.Set(in bounds), CityNpc.Ai.Set(in ai), CityNpc.Timers.Set(in npcTimers), CityNpc.Move.Set(in move),
                        CityNpc.Realm.Set(in realm));
                    census.CityNpcs++;
                    if (++made % SpawnBatch == 0)
                    {
                        tx.Commit();
                        tx.Dispose();
                        tx = dbe.CreateQuickTransaction();
                    }
                }
            }

            tx.Commit();
        }
        finally
        {
            tx.Dispose();
        }

        return made;
    }

    // ── Points of interest ──────────────────────────────────────────────────────────────────────────────────────────

    private static void SpawnPointsOfInterest(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index)
    {
        foreach (var poi in map.Pois)
        {
            using (var tx = dbe.CreateQuickTransaction())
            {
                for (var i = 0; i < poi.Props; i++)
                {
                    var (x, z) = rng.PointInDisc(poi.X, poi.Z, poi.Radius);
                    SpawnStructure(tx, x, z, 6f, StructureKind.PoiProp, ownerRegion: -1, tickPeriod: 0, ref rng);
                    census.StaticObjects++;
                }

                tx.Commit();
            }

            // A landmark's lairs are the reason players go there, so they get the aggressive templates.
            var lairs = Scale(poi.Lairs, config.PopulationScale);
            SpawnLairsIn(dbe, poi.X, poi.Z, poi.Radius, lairs, CreatureTemplates.TuskenRaider, config, census, ref rng, index);
            index.Pois.Add((poi.X, poi.Z, poi.Radius));
        }
    }

    // ── Wilderness ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static void SpawnWilderness(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index)
    {
        foreach (var region in map.SpawnRegions)
        {
            var areaSqKm = MathF.PI * region.Radius * region.Radius / 1_000_000f;
            var lairs = Scale((int)MathF.Round(areaSqKm * region.LairsPerSqKm), config.PopulationScale);
            SpawnLairsIn(dbe, region.X, region.Z, region.Radius, lairs, region.CreatureTemplate, config, census, ref rng, index);
        }
    }

    /// <summary>
    /// Place <paramref name="lairCount"/> lairs inside a disc and fill each with its creatures.
    /// </summary>
    /// <remarks>
    /// Lair and creatures go in the SAME transaction, so the batch Morton sort sees them together and a lair's creatures
    /// are born into the cluster their lair is in rather than wherever the free-slot search happened to be. That is the
    /// difference between a world whose clusters are tight from birth and one that spends its first hundred ticks
    /// repairing itself into shape.
    /// </remarks>
    private static void SpawnLairsIn(DatabaseEngine dbe, float cx, float cz, float radius, int lairCount, int template,
        SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index)
    {
        if (lairCount <= 0)
        {
            return;
        }

        var perLair = CreatureTemplates.SpawnLimit[template];
        var spawnRadius = CreatureTemplates.LairSpawnRadius[template] * config.ContentScale;
        var lairsPerBatch = Math.Max(1, SpawnBatch / (perLair + 1));
        var done = 0;

        while (done < lairCount)
        {
            var n = Math.Min(lairsPerBatch, lairCount - done);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < n; i++)
            {
                var (lx, lz) = rng.PointInDisc(cx, cz, radius);
                var lairBounds = default(LairPlacement);
                lairBounds.SetAt(lx, lz, 4f);
                var lair = new Lair
                {
                    CreatureTemplate = template,
                    SpawnLimit = perLair,
                    AliveCount = perLair,
                    RespawnCooldown = 0,
                    SpawnRadius = spawnRadius,
                    MissionId = 0,
                };
                var lairVitals = new LairVitals { Health = 2400, MaxHealth = 2400 };
                var lairId = tx.Spawn<CreatureLair>(
                    CreatureLair.Bounds.Set(in lairBounds),
                    CreatureLair.Spawner.Set(in lair),
                    CreatureLair.Vitals.Set(in lairVitals),
                    CreatureLair.Realm.Set(new LairRealm(_realm)));
                census.Lairs++;
                index.Lairs.Add(lairId);

                for (var k = 0; k < perLair; k++)
                {
                    SpawnCreature(tx, ref rng, lairId, template, lx, lz, spawnRadius, config);
                    census.Creatures++;
                }
            }

            tx.Commit();
            done += n;
        }
    }

    /// <summary>Create one creature belonging to a lair.</summary>
    internal static EntityId SpawnCreature(Transaction tx, ref Rng rng, EntityId lairId, int template,
        float lairX, float lairZ, float spawnRadius, SimConfig config)
    {
        var (x, z) = rng.PointInDisc(lairX, lairZ, spawnRadius);
        var bounds = default(CreaturePlacement);
        bounds.SetAt(x, z, 1.5f);

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

            Lair = lairId,
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
            Creature.Realm.Set(new CreatureRealm(_realm)));
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
    private static void SpawnPlayerStructures(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng)
    {
        var total = Scale(BaselinePlayerStructures, config.PopulationScale);
        if (total <= 0)
        {
            return;
        }

        // Two thirds of structures sit in player-city knots; the rest are lone harvesters out in the resource fields.
        var sites = BuildPlayerCitySites(map, config, ref rng);
        var inCities = (int)(total * 0.66f);
        var loose = total - inCities;

        var made = 0;
        while (made < total)
        {
            var n = Math.Min(SpawnBatch, total - made);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < n; i++)
            {
                float x, z;
                if (made + i < inCities && sites.Count > 0)
                {
                    var site = sites[rng.NextInt(0, sites.Count)];
                    (x, z) = rng.PointInDisc(site.X, site.Z, site.Radius);
                }
                else
                {
                    (x, z) = RandomWildernessPoint(map, config, ref rng);
                }

                // A house never ticks. A factory runs a batch; a harvester extracts. SWG's own numbers: manufacturing is
                // complexity x 8 seconds per unit, and a harvester's rate is units per minute — so both are periods of
                // tens of seconds to minutes, which at this tick rate is hundreds to thousands of ticks.
                var roll = rng.NextFloat();
                var (kind, period) = roll < 0.55f
                    ? (StructureKind.PlayerHouse, 0)
                    : roll < 0.78f
                        ? (StructureKind.Harvester, 600)
                        : (StructureKind.Factory, 1800);

                SpawnStructure(tx, x, z, kind == StructureKind.PlayerHouse ? 10f : 14f, kind, ownerRegion: -1, tickPeriod: period, ref rng);
                census.PlayerStructures++;
            }

            tx.Commit();
            made += n;
        }

        _ = loose;
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
                sites.Add((x, z, TatooineData.PlayerCityRadiusM[rank] * config.ContentScale));
            }
        }

        return sites;
    }

    // ── Players ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void SpawnPlayers(DatabaseEngine dbe, TatooineMap map, SimConfig config, WorldCensus census, ref Rng rng, WorldIndex index)
    {
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
                    var port = index.Shuttleports[cityIndex];
                    (x, z) = rng.PointInDisc(port.X, port.Z, 10f);
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
                    (x, z) = rng.PointInDisc(city.X, city.Z, city.Radius * 6f);
                }

                var bounds = default(PlayerPlacement);
                bounds.SetAt(x, z, 1f);
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
        var count = Scale(BaselinePlayers / 4, config.PopulationScale);
        if (count <= 0)
        {
            return;
        }

        var template = CreatureTemplates.MissionDefender;
        var spawnRadius = CreatureTemplates.LairSpawnRadius[template] * config.ContentScale;
        var perLair = CreatureTemplates.SpawnLimit[template];
        var made = 0;

        while (made < count)
        {
            var n = Math.Min(Math.Max(1, SpawnBatch / (perLair + 1)), count - made);
            using var tx = dbe.CreateQuickTransaction();
            for (var i = 0; i < n; i++)
            {
                var (lx, lz) = RandomWildernessPoint(map, config, ref rng);
                var bounds = default(LairPlacement);
                bounds.SetAt(lx, lz, 4f);
                var lair = new Lair
                {
                    CreatureTemplate = template,
                    SpawnLimit = perLair,
                    AliveCount = 0,
                    RespawnCooldown = rng.NextInt(1, 50),
                    SpawnRadius = spawnRadius,
                    MissionId = 0,
                };

                // [CORE3] difficulty x (900 + random(200)); a mid difficulty until one is assigned.
                var vitals = new LairVitals { Health = 4500, MaxHealth = 4500 };
                var lairId = tx.Spawn<CreatureLair>(
                    CreatureLair.Bounds.Set(in bounds),
                    CreatureLair.Spawner.Set(in lair),
                    CreatureLair.Vitals.Set(in vitals),
                    CreatureLair.Realm.Set(new LairRealm(_realm)));
                census.Lairs++;
                index.Lairs.Add(lairId);

                for (var k = 0; k < perLair; k++)
                {
                    SpawnCreature(tx, ref rng, lairId, template, lx, lz, spawnRadius, config);
                    census.Creatures++;
                }
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

    /// <summary>Ticks between AI decisions, from Core3's 400-1000 ms behaviour interval at this simulation's tick rate.</summary>
    internal static int AiTicksMax(SimConfig config) => Math.Max(2, TatooineData.AiIntervalMaxMs * config.TickRateHz / 1000);

    internal static int AiTicksMin(SimConfig config) => Math.Max(1, TatooineData.AiIntervalMinMs * config.TickRateHz / 1000);

    private static void SpawnStructure(Transaction tx, float x, float z, float halfExtent, int kind, int ownerRegion, int tickPeriod, ref Rng rng)
    {
        var bounds = default(StructurePlacement);
        bounds.SetAt(x, z, halfExtent);
        var s = new Structure
        {
            Kind = kind,
            OwnerRegion = ownerRegion,
            TickPeriod = tickPeriod,

            // Staggered, so the economy does not arrive as a once-a-minute spike that the median tick never sees.
            TickCountdown = tickPeriod == 0 ? 0 : rng.NextInt(1, tickPeriod),
        };
        tx.Spawn<WorldObject>(WorldObject.Bounds.Set(in bounds), WorldObject.Struct.Set(in s), WorldObject.Realm.Set(new StructureRealm(_realm)));
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
