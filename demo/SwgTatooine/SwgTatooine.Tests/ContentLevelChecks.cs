using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Terrain;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// Content levels (content-levels.md): the ramp from every city's edge, the bands beyond it, every band a short ride from every city, a lair's kind that
/// covers its level, and creature stats from the level as <c>balance_model.py</c> has them.
/// </summary>
[TestFixture]
public sealed class ContentLevelChecks
{
    /// <summary>A planet's sites and level map, planned as the build plans them.</summary>
    private static WorldIndex Plan(int seed = 1, float worldKm = TatooineData.PlanetEdgeM / 1000f, ushort realm = 0)
    {
        var config = new SimConfig { Seed = seed, WorldEdgeKm = worldKm };
        var index = new WorldIndex();
        WorldBuilder.PlanSites(TatooineMap.Build(config), config, index, realm);
        return index;
    }

    /// <summary>A point <paramref name="edgeM"/> beyond a city's edge, on the bearing away from the nearest other city.</summary>
    private static (float X, float Z) BeyondEdge(TatooineMap map, int city, float edgeM)
    {
        var c = map.Cities[city];
        var (ax, az) = (1f, 0f);
        var nearest = float.MaxValue;
        for (var o = 0; o < map.Cities.Count; o++)
        {
            var dx = c.X - map.Cities[o].X;
            var dz = c.Z - map.Cities[o].Z;
            var d = MathF.Sqrt((dx * dx) + (dz * dz));
            if (o != city && d < nearest)
            {
                nearest = d;
                (ax, az) = (dx / d, dz / d);
            }
        }

        return (c.X + (ax * (c.Radius + edgeM)), c.Z + (az * (c.Radius + edgeM)));
    }

    /// <summary>CL-1: level 1 within 100 m of every city's edge, one more every 100 m, 10 at 950 m — and the ramp draws nothing.</summary>
    [Test]
    public void TheRamp_IsLevelOneAtEveryCityEdge_AndTenAtOneKilometre()
    {
        var config = new SimConfig();
        var map = TatooineMap.Build(config);
        var levels = Plan().Levels;
        Assert.Multiple(() =>
        {
            for (var c = 0; c < map.Cities.Count; c++)
            {
                foreach (var (edge, want) in new[] { (0f, 1), (50f, 1), (150f, 2), (550f, 6), (950f, 10) })
                {
                    var (x, z) = BeyondEdge(map, c, edge);
                    var rng = new Rng(5);
                    var untouched = rng;
                    Assert.That(levels.LevelAt(x, z, ref rng), Is.EqualTo(want), $"{map.Cities[c].Name} at {edge} m");
                    Assert.That(rng.Next(), Is.EqualTo(untouched.Next()), $"{map.Cities[c].Name} at {edge} m: the ramp drew a number");
                }
            }
        });
    }

    /// <summary>CL-1: inside a city it is level 1.</summary>
    [Test]
    public void InsideACity_IsLevelOne()
    {
        var map = TatooineMap.Build(new SimConfig());
        var levels = Plan().Levels;
        foreach (var city in map.Cities)
        {
            var rng = new Rng(9);
            Assert.That(levels.LevelAt(city.X, city.Z, ref rng), Is.EqualTo(1), city.Name);
            Assert.That(levels.EdgeDistance(city.X, city.Z), Is.LessThan(0f), city.Name);
        }
    }

    /// <summary>
    /// CL-2: for every city and every band II-V a frontier patch of that band whose disc lies 1.0-2.0 km from the city's edge, inside the world, and outside
    /// every other city's ramp — for twenty seeds, at the planet's scale and at a world half its size.
    /// </summary>
    [Test]
    public void EveryBand_IsAShortRideFromEveryCity()
    {
        foreach (var km in new[] { TatooineData.PlanetEdgeM / 1000f, TatooineData.PlanetEdgeM / 2000f })
        {
            var config = new SimConfig { WorldEdgeKm = km };
            var s = config.ContentScale;
            var map = TatooineMap.Build(config);
            var half = config.WorldEdgeM * 0.5f * WorldBuilder.InsideEdge;
            for (var seed = 1; seed <= 20; seed++)
            {
                var index = Plan(seed, km);
                Assert.That(index.FrontiersDisplaced, Is.Zero, $"seed {seed} at {km} km: {string.Join(", ", index.DisplacedFrontiers)}");
                for (var c = 0; c < map.Cities.Count; c++)
                {
                    var city = map.Cities[c];
                    for (var band = 2; band <= 5; band++)
                    {
                        var found = false;
                        foreach (var site in index.Sites)
                        {
                            if (site.Kind != LairSiteKind.Frontier || site.City != c || site.Band != band)
                            {
                                continue;
                            }

                            var edge = MathF.Sqrt(Sq(site.X - city.X) + Sq(site.Z - city.Z)) - city.Radius;
                            var ok = edge - site.Radius >= (1000f * s) - 0.5f && edge + site.Radius <= (2000f * s) + 0.5f
                                && MathF.Abs(site.X) + site.Radius <= half && MathF.Abs(site.Z) + site.Radius <= half;
                            for (var o = 0; o < map.Cities.Count; o++)
                            {
                                var other = map.Cities[o];
                                ok &= o == c || MathF.Sqrt(Sq(site.X - other.X) + Sq(site.Z - other.Z)) - site.Radius >= other.Radius + (1000f * s) - 0.5f;
                            }

                            found |= ok;
                        }

                        Assert.That(found, Is.True, $"seed {seed} at {km} km: {city.Name} has no valid band {band} patch");
                    }
                }
            }
        }
    }

    /// <summary>
    /// CL-7: the placement every lair goes through — the build's and the director's — never puts one inside a city or within its no-spawn margin, even for a
    /// site whose disc covers the city, and a fringe's lairs stand in its annulus.
    /// </summary>
    [Test]
    public void ALairIsNeverPlacedInACity()
    {
        var config = new SimConfig();
        var map = TatooineMap.Build(config);
        var index = Plan();
        var margin = ContentLevels.NoSpawnMarginM * config.ContentScale;
        var rng = new Rng(41);
        var placed = 0;
        foreach (var city in map.Cities)
        {
            // A disc over the city, reaching 200 m past its edge: most draws fall inside it.
            var site = new LairSite(0, city.X, city.Z, city.Radius + 200f, [CreatureTemplates.Rat], 1);
            for (var n = 0; n < 500; n++)
            {
                if (WorldBuilder.TryPlaceLair(site, index.Levels, ref rng, config, out var x, out var z))
                {
                    placed++;
                    Assert.That(index.Levels.EdgeDistance(x, z), Is.GreaterThanOrEqualTo(margin - 0.01f), $"a lair placed in {city.Name}");
                }
            }
        }

        Assert.That(placed, Is.GreaterThan(500), "almost nothing was placed: the case tested nothing");
        foreach (var s in index.CrowdSites)
        {
            var fringe = index.Sites[s];
            for (var n = 0; n < 200; n++)
            {
                if (WorldBuilder.TryPlaceLair(fringe, index.Levels, ref rng, config, out var x, out var z))
                {
                    var r = MathF.Sqrt(Sq(x - fringe.X) + Sq(z - fringe.Z));
                    Assert.That(r, Is.InRange(fringe.InnerRadius - 0.01f, fringe.Radius + 0.01f), "a fringe's lair outside its annulus");
                }
            }
        }
    }

    /// <summary>CL-3: two plans with one seed are equal site for site; another seed moves at least one patch.</summary>
    [Test]
    public void TheLevelMap_IsDeterministicFromTheSeed()
    {
        var a = Plan(7);
        var b = Plan(7);
        var other = Plan(8);
        Assert.That(a.Sites, Has.Count.EqualTo(b.Sites.Count));
        var moved = false;
        for (var i = 0; i < a.Sites.Count; i++)
        {
            var (x, y) = (a.Sites[i], b.Sites[i]);
            Assert.That(
                (y.X, y.Z, y.Radius, y.InnerRadius, y.Band, y.Target, y.Kind, y.City),
                Is.EqualTo((x.X, x.Z, x.Radius, x.InnerRadius, x.Band, x.Target, x.Kind, x.City)),
                $"site {i}");
            moved |= other.Sites[i].X != x.X || other.Sites[i].Z != x.Z;
        }

        Assert.That(moved, Is.True, "another seed planned the same sites");
        Assert.That(a.Levels.Anchors, Is.EqualTo(b.Levels.Anchors), "the anchors differ");
        Assert.That(a.Sites.Count, Is.EqualTo(TatooineData.Pois.Length + TatooineData.SpawnRegions.Length + (5 * TatooineData.Cities.Length)),
            "landmarks, regions, a fringe and four patches per city");
    }

    /// <summary>CL-4: beyond the ramp the 8 regions draw one band each from II-V, each band twice; every landmark one band from III-V.</summary>
    [Test]
    public void ASiteBeyondTheRamp_DrawsOneBand()
    {
        for (var seed = 1; seed <= 20; seed++)
        {
            var index = Plan(seed);
            var perBand = new int[6];
            foreach (var site in index.Sites)
            {
                if (site.Kind == LairSiteKind.Region)
                {
                    perBand[site.Band]++;
                }
                else if (site.Kind == LairSiteKind.Landmark)
                {
                    Assert.That(site.Band, Is.InRange(3, 5), $"seed {seed}: a landmark of band {site.Band}");
                }
                else if (site.Kind == LairSiteKind.Fringe)
                {
                    Assert.That(site.Band, Is.Zero, $"seed {seed}: a fringe with a band");
                }
            }

            Assert.That(perBand, Is.EqualTo(new[] { 0, 0, 2, 2, 2, 2 }), $"seed {seed}: the regions' bands");
        }
    }

    /// <summary>CL-5: every level 1-50 has a wild kind, and no camp below 25.</summary>
    [Test]
    public void EveryLevel_HasAWildKind()
    {
        for (var level = 1; level <= ContentLevels.MaxLevel; level++)
        {
            var any = false;
            for (var k = 0; k < CreatureTemplates.WildCount; k++)
            {
                any |= CreatureTemplates.CoversLevel(k, level);
                Assert.That(CreatureTemplates.IsCamp(k) && CreatureTemplates.CoversLevel(k, level) && level < 25, Is.False, $"a camp at level {level}");
            }

            Assert.That(any, Is.True, $"no wild kind at level {level}");
        }
    }

    /// <summary>CL-5: over every site's kinds, every level and 200 draws, the kind drawn covers the level.</summary>
    [Test]
    public void ALairsKind_CoversItsLevel()
    {
        var index = Plan();
        var rng = new Rng(17);
        foreach (var site in index.Sites)
        {
            for (var level = 1; level <= ContentLevels.MaxLevel; level++)
            {
                for (var n = 0; n < 200; n++)
                {
                    var kind = CreatureTemplates.PickLair(site.Templates, level, ref rng);
                    if (!CreatureTemplates.CoversLevel(kind, level) || kind >= CreatureTemplates.WildCount)
                    {
                        Assert.Fail($"{site.Kind} site drew {CreatureTemplates.Name(kind)} at level {level}");
                    }
                }
            }
        }
    }

    /// <summary>CL-6: health and hit are balance_model.py's creature() at every level (§ 14.2's rows, ±1 for rounding); a lair outlasts its occupant.</summary>
    [Test]
    public void TheStatsMatchTheBalanceModel()
    {
        Assert.Multiple(() =>
        {
            foreach (var (level, foe, hp, hit) in new[]
                     {
                         (1, false, 659, 31), (10, false, 805, 44), (25, false, 1125, 70), (50, false, 1965, 131), (1, true, 988, 40), (50, true, 2948, 170),
                     })
            {
                Assert.That(ContentLevels.CreatureHealth(level, foe), Is.EqualTo(hp).Within(1), $"health at {level}, foe {foe}");
                Assert.That(ContentLevels.CreatureHit(level, foe), Is.EqualTo(hit).Within(1), $"hit at {level}, foe {foe}");
            }

            for (var level = 1; level <= ContentLevels.MaxLevel; level++)
            {
                // The script's formulas, independently: 8 s on-level to kill, 75 s to die to one, 2 s between a creature's attacks on average.
                var dps = 90 * Math.Pow(1.25, (level - 5) / 10.0);
                var effHp = (1000 + (40.0 * level)) / (1 - (0.12 + (0.006 * (level - 5))));
                Assert.That(ContentLevels.CreatureHealth(level, false), Is.EqualTo(dps * 8).Within(1), $"beast health at {level}");
                Assert.That(ContentLevels.CreatureHit(level, true), Is.EqualTo(effHp / 75 * 1.3 * 2).Within(1), $"foe hit at {level}");
                Assert.That(ContentLevels.LairHealth(level, CreatureTemplates.Rat), Is.GreaterThan(ContentLevels.CreatureHealth(level, false)));
            }
        });
    }

    /// <summary>
    /// CL-7 on a small built world: every lair's level is the map's at its position (exact in the ramp, in its band beyond), every creature carries its
    /// lair's level, MaxHealth and AttackDamage are the formula's, no lair stands in a city, and every band but the empty ones of a 5 % world is populated.
    /// </summary>
    [Test]
    [Category("Integration")]
    public void ABuiltWorld_LevelsEveryLairAndCreature()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            using var sim = new TatooineSim(Worlds.Small(dir));
            sim.Initialize();
            var levels = sim.Index.Levels;
            var margin = ContentLevels.NoSpawnMarginM * sim.Config.ContentScale;
            var lairLevel = new Dictionary<EntityId, (int Level, int Template)>();
            var byBand = new int[5];
            using var tx = sim.Dbe.CreateQuickTransaction();
            var problems = new List<string>();
            foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
            {
                if (cluster.Realm.Value != 0)
                {
                    continue;
                }

                var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                var bounds = cluster.GetReadOnlySpan(CreatureLair.Bounds);
                var vitals = cluster.GetReadOnlySpan(CreatureLair.Vitals);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    var lair = spawners[slot];
                    var (x, z) = (bounds[slot].X, bounds[slot].Z);
                    lairLevel[cluster.GetEntityId(slot)] = (lair.Level, lair.CreatureTemplate);
                    byBand[ContentLevels.Band(lair.Level) - 1]++;
                    if (!levels.Agrees(x, z, lair.Level))
                    {
                        problems.Add($"lair at ({x:F0}, {z:F0}) is level {lair.Level}, edge {levels.EdgeDistance(x, z):F0} m, band {levels.BandAt(x, z)}");
                    }

                    if (levels.EdgeDistance(x, z) < margin - 0.01f)
                    {
                        problems.Add($"lair at ({x:F0}, {z:F0}) stands in a city");
                    }

                    if (!CreatureTemplates.CoversLevel(lair.CreatureTemplate, lair.Level))
                    {
                        problems.Add($"{CreatureTemplates.Name(lair.CreatureTemplate)} at level {lair.Level}");
                    }

                    if (vitals[slot].MaxHealth != ContentLevels.LairHealth(lair.Level, lair.CreatureTemplate))
                    {
                        problems.Add($"lair of level {lair.Level} has {vitals[slot].MaxHealth} hp");
                    }
                }
            }

            var creatures = 0;
            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                if (cluster.Realm.Value != 0)
                {
                    continue;
                }

                var brains = cluster.GetReadOnlySpan(Creature.Ai);
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                var vitals = cluster.GetReadOnlySpan(Creature.Vitals);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    creatures++;
                    var ai = brains[slot];
                    var foe = CreatureTemplates.IsCamp(ai.Template);
                    if (!lairLevel.TryGetValue(homes[slot].Lair, out var home) || home.Level != ai.Level)
                    {
                        problems.Add($"a creature of level {ai.Level} in a lair of level {home.Level}");
                    }

                    if (vitals[slot].MaxHealth != ContentLevels.CreatureHealth(ai.Level, foe)
                        || vitals[slot].AttackDamage != ContentLevels.CreatureHit(ai.Level, foe))
                    {
                        problems.Add($"a level-{ai.Level} creature with {vitals[slot].MaxHealth} hp hitting for {vitals[slot].AttackDamage}");
                    }
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(lairLevel, Is.Not.Empty, "no lair was built");
                Assert.That(creatures, Is.GreaterThan(lairLevel.Count), "no creatures were built");
                Assert.That(problems, Is.Empty, string.Join("\n", problems.GetRange(0, Math.Min(10, problems.Count))));
                Assert.That(byBand[0], Is.GreaterThan(0), "no lair in the ramp: the fringes are empty");
                Assert.That(sim.Census.LairsByBand, Is.EqualTo(byBand), "the census's bands disagree with the lairs'");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }

    private static float Sq(float v) => v * v;
}
