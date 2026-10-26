using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The crowd's lair objectives (lair-objectives.md; plan G1): the census that sends fighters where lairs are left unattended, and fighters that walk to
/// their lair, stop in range of it, and stay while it stands.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class ObjectiveChecks
{
    /// <summary>L-2: a point of interest's weight is its lairs over its players plus one, and nothing where no lair stands.</summary>
    [Test]
    public void TheWeight_IsLairsOverPlayersPlusOne()
    {
        Assert.Multiple(() =>
        {
            Assert.That(LairCensus.WeightOf(4, 3), Is.EqualTo(1f));
            Assert.That(LairCensus.WeightOf(3, 0), Is.EqualTo(3f));
            Assert.That(LairCensus.WeightOf(0, 0), Is.Zero, "a point of interest with no standing lair has weight");
            Assert.That(LairCensus.WeightOf(2, -1), Is.EqualTo(2f), "a negative player count raised the weight");
        });
    }

    /// <summary>L-2: near the fighter, an unattended point of interest beats a crowded one; a far one is drawn rarely; one with no lair never.</summary>
    [Test]
    public void TheDraw_FavoursNearAndUnattended()
    {
        var pois = new List<(float X, float Z, float Radius, float InnerRadius)>
        {
            (100f, 0f, 50f, 0f), (0f, 100f, 50f, 0f), (8000f, 0f, 50f, 0f), (-100f, 0f, 50f, 0f),
        };
        var census = new LairCensus(4);
        census.Weight[0] = 4f / (1 + 3);   // near, crowded
        census.Weight[1] = 4f;             // near, unattended
        census.Weight[2] = 4f;             // far, unattended
        census.Weight[3] = 0f;             // near, no lair
        var drawn = new int[4];
        for (var n = 0; n < 10_000; n++)
        {
            drawn[census.DrawNear(pois, 0f, 0f, 2000f, (n + 0.5f) / 10_000f)]++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(drawn[3], Is.Zero, "a point of interest with no standing lair was drawn");
            Assert.That(drawn[1], Is.GreaterThan(drawn[0] * 3), "the unattended one is not preferred to the crowded one");
            Assert.That(drawn[2], Is.LessThan(drawn[1] / 10), "the far one is drawn as often as a near one");
            Assert.That(drawn[2], Is.GreaterThan(0), "the far one is never drawn");
            Assert.That(new LairCensus(2).DrawNear(pois, 0f, 0f, 2000f, 0.5f), Is.EqualTo(-1));
        });
    }

    // ── One world, run once: the census, the fighters at their fringes, and the creatures they must leave alone ──────────────────────────────────────

    private string _dir;
    private TatooineSim _sim;

    // Lairs staged above the crowd's level beside every fringe lair, before the run: passive level-20 snakes, which a fighter at its objective has in range.
    private readonly HashSet<EntityId> _aboveCrowd = [];

    /// <summary>A five-minute run of a small world, with a level-20 lair staged 9 m from every fringe lair.</summary>
    [OneTimeSetUp]
    public void RunWorld()
    {
        _dir = Worlds.NewDirectory();
        _sim = new TatooineSim(Causality.LongEnough(_dir, 3_000));
        _sim.Initialize();

        // Nothing serves before Run, so a plain transaction stages. Site 0 and no mission: a wild lair the director never retires, which the crowd's filters
        // see like any other — only its level keeps it out.
        var config = _sim.Config;
        var terrain = Server.Terrain.TerrainField.Shared(config.ContentScale);
        var index = _sim.Index;
        var fringe = new HashSet<int>();
        foreach (var s in index.CrowdSites)
        {
            fringe.Add(s + 1);
        }

        var at = new List<(float X, float Z)>();
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
            {
                var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                var bounds = cluster.GetReadOnlySpan(CreatureLair.Bounds);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (cluster.Realm.Value == 0 && fringe.Contains(spawners[slot].Site))
                    {
                        at.Add((bounds[slot].X, bounds[slot].Z));
                    }
                }
            }

            var rng = new Rng(29);
            foreach (var (x, z) in at)
            {
                _aboveCrowd.Add(WorldBuilder.SpawnLair(tx, ref rng, CreatureTemplates.Snake, 20, x + 9f, z, site: 0, expireTick: 0, config, terrain, 0));
            }

            tx.Commit();
        }

        Assert.That(_aboveCrowd, Is.Not.Empty, "no fringe lair to stage beside: the world has no fringe");
        _sim.Run();
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    /// <summary>
    /// L-3, L-4 with the crowd at the city fringes (content-levels.md § 5): fighters take objectives and see them fall; and on every once-a-second check of a
    /// fighter standing at its stand-off point, its standing lair is within weapon range — never out of it.
    /// </summary>
    [Test]
    public void AFighter_WalksToItsLair_AndStaysInRangeWhileItStands()
    {
        var b = _sim.Bridge;
        Assert.Multiple(() =>
        {
            Assert.That(b.ObjectivesTaken, Is.GreaterThan(0), "no fighter took a lair as its objective");
            Assert.That(b.ObjectivesFallen, Is.GreaterThan(0), "no objective fell");
            Assert.That(b.ObjectivesHeldInRange, Is.GreaterThan(0), "no fighter was ever checked at its objective: L-3 was never tested");
            Assert.That(b.ObjectivesOutOfRange, Is.Zero, $"{b.ObjectivesOutOfRange} checks found a fighter out of range of its standing objective");
        });
    }

    /// <summary>
    /// L-1 over the city fringes (CL-10): the census has one place per city, and counts each fringe's standing wild lairs as its site does — counted here from
    /// the lairs themselves, not from the site's own counter, which is what the census reads.
    /// </summary>
    [Test]
    public void TheCensus_CountsTheCityFringes()
    {
        var census = _sim.Bridge.CensusOf(0);
        Assert.That(census, Is.Not.Null, "no census was ever taken");
        var index = _sim.Index;
        Assert.That(census.Lairs, Has.Length.EqualTo(index.Cities.Count), "the census is not one place per city");

        var standing = new int[index.CrowdSites.Count];
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
            {
                var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                var vitals = cluster.GetReadOnlySpan(CreatureLair.Vitals);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    var place = index.CrowdSites.IndexOf(spawners[slot].Site - 1);
                    if (cluster.Realm.Value == 0 && place >= 0 && SimBridge.IsStandingLair(spawners[slot], vitals[slot]))
                    {
                        standing[place]++;
                    }
                }
            }
        }

        Assert.Multiple(() =>
        {
            for (var i = 0; i < standing.Length; i++)
            {
                Assert.That(index.Sites[index.CrowdSites[i]].Kind, Is.EqualTo(LairSiteKind.Fringe), $"crowd place {i} is not a fringe");

                // Taken at the last whole second; a lair or two may have been broken, retired or replaced since.
                Assert.That(census.Lairs[i], Is.EqualTo(standing[i]).Within(2), $"fringe {i}: the census and the lairs standing there disagree");
            }

            Assert.That(standing.Sum(), Is.GreaterThan(0), "no fringe has a standing lair");
        });
    }

    /// <summary>
    /// CL-10: every objective the crowd took was at or below <see cref="TatooineData.CrowdLevel"/>, and the level-20 lairs staged beside the fringe lairs —
    /// in weapon range of every fighter there — were never hit, nor were their snakes.
    /// </summary>
    [Test]
    public void TheCrowd_FightsOnlyAtOrBelowItsLevel()
    {
        Assert.That(_sim.Bridge.ObjectivesTaken, Is.GreaterThan(0), "no objective was taken: the case tested nothing");
        Assert.That(_sim.Bridge.CrowdObjectiveLevelMax, Is.InRange(1, TatooineData.CrowdLevel), "an objective above the crowd's level");

        var hurtLairs = 0;
        var hurtCreatures = 0;
        var creatures = 0;
        using var tx = _sim.Dbe.CreateQuickTransaction();
        foreach (var id in _aboveCrowd)
        {
            var vitals = tx.Open(id).Read(CreatureLair.Vitals);
            hurtLairs += vitals.Health < vitals.MaxHealth ? 1 : 0;
        }

        foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
        {
            var homes = cluster.GetReadOnlySpan(Creature.Home);
            var vitals = cluster.GetReadOnlySpan(Creature.Vitals);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (_aboveCrowd.Contains(homes[slot].Lair))
                {
                    creatures++;
                    hurtCreatures += vitals[slot].Health < vitals[slot].MaxHealth ? 1 : 0;
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(creatures, Is.GreaterThan(0), "the staged snakes are gone");
            Assert.That(hurtLairs, Is.Zero, "the crowd hit a lair above its level");
            Assert.That(hurtCreatures, Is.Zero, $"the crowd hit {hurtCreatures} of {creatures} creatures above its level");
        });
    }
}
