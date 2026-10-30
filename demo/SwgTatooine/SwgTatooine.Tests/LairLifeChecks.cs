using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// Lairs and their creatures are transient (Loïc, 2026-10-24): a creature dies, lies, and is destroyed and replaced; a wild lair is retired when broken or
/// spent, takes its creatures with it through the foreign-key cascade, and is replaced elsewhere in its site.
/// </summary>
/// <remarks>
/// <para>
/// <b>One world, churned hard.</b> Lifetimes of a few seconds retire every wild lair inside the window — most of them more than once — and a full population
/// kills enough creatures to exercise the corpse path. The run is the expensive part; each case asserts one claim about it.
/// </para>
/// <para>
/// <b>The invariants are identities between independently produced numbers</b>, not a counter compared with itself: a lair's <see cref="Lair.AliveCount"/>
/// (the director's bookkeeping) against the creatures whose <see cref="CreatureHome"/> names it (the engine's FK), and a site's <see cref="LairSite.Live"/>
/// against the lairs on disk that carry its number.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class LairLifeChecks
{
    private string _dir;
    private TatooineSim _sim;

    [OneTimeSetUp]
    public void RunWorld()
    {
        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.WorkerCount = 1;
        config.WarmTicks = 0;
        config.MeasuredTicks = 400;

        // Full population, so players meet creatures and kill some (CreatureRevivalTests' measurement: at 5 % nothing dies).
        config.PopulationScale = 1f;

        // Seconds, at 10 Hz: corpses removed after one, replacements after three, and every wild lair retired within twenty (its first life starts part-way
        // through) and replaced after two — long enough that a lair outlives a corpse and the refill it is owed.
        config.CorpseSeconds = 1f;
        config.RespawnSeconds = 3f;
        config.LairLifeMinSeconds = 12f;
        config.LairLifeMaxSeconds = 20f;
        config.LairReplaceSeconds = 2f;
        _sim = new TatooineSim(config);
        _sim.Initialize();
        _sim.Run();
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    [Test]
    public void ADeadCreatureIsDestroyed_AndItsLairReplacesIt()
    {
        var s = _sim.LastStats;
        Assert.Multiple(() =>
        {
            Assert.That(s.CreaturesKilled, Is.GreaterThan(0), "precondition: players killed creatures");
            Assert.That(s.CorpsesRemoved, Is.GreaterThan(0), "no corpse was destroyed: the countdown or the hand-over to the director is broken");
            Assert.That(s.CorpsesRemoved, Is.LessThanOrEqualTo(s.CreaturesKilled), "more corpses were removed than creatures died");
            Assert.That(s.CreaturesRespawned, Is.GreaterThan(0), "no lair replaced a creature it lost");
        });
    }

    [Test]
    public void SpentLairsAreRetired_AndTheirSitesSpawnReplacements()
    {
        var s = _sim.LastStats;
        Assert.Multiple(() =>
        {
            Assert.That(s.LairsRetired, Is.GreaterThan(0), "no wild lair was retired, though every life ends inside the window");
            Assert.That(s.LairsSpawned, Is.GreaterThan(0), "lairs were retired but no site spawned a replacement");
        });
    }

    /// <summary>
    /// CL-8: a refilled creature takes its lair's level — every creature standing, the refills among them, carries its lair's level and the level's stats.
    /// </summary>
    [Test]
    public void ARefilledCreature_TakesItsLairsLevel()
    {
        Assert.That(_sim.LastStats.CreaturesRespawned, Is.GreaterThan(0), "precondition: a lair replaced a creature");
        var lairLevel = new Dictionary<EntityId, int>();
        var wrong = new List<string>();
        var creatures = 0;
        using var tx = _sim.Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
        {
            var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                lairLevel[cluster.GetEntityId(slot)] = spawners[slot].Level;
            }
        }

        foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
        {
            var brains = cluster.GetReadOnlySpan(Creature.Ai);
            var homes = cluster.GetReadOnlySpan(Creature.Home);
            var vitals = cluster.GetReadOnlySpan(Creature.Vitals);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                creatures++;
                var ai = brains[slot];
                var health = ContentLevels.CreatureHealth(ai.Level, CreatureTemplates.IsCamp(ai.Template));
                if (!lairLevel.TryGetValue(homes[slot].Lair, out var level) || level != ai.Level || vitals[slot].MaxHealth != health)
                {
                    wrong.Add($"a level-{ai.Level} creature ({vitals[slot].MaxHealth} hp) in a lair of level {level}");
                }
            }
        }

        Assert.That(creatures, Is.GreaterThan(0), "precondition: the world has creatures");
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {creatures} creatures, first: {(wrong.Count > 0 ? wrong[0] : "")}");
    }

    /// <summary>
    /// CL-8: a replacement lair takes the level of where it stands. Every life ends inside the window, so the wild lairs standing at its end are
    /// replacements; each agrees with the level map at its position, and its kind covers its level.
    /// </summary>
    [Test]
    public void AReplacementLair_TakesTheLevelOfWhereItStands()
    {
        Assert.That(_sim.LastStats.LairsSpawned, Is.GreaterThan(0), "precondition: a site spawned a replacement");
        var levels = _sim.Index.Levels;
        var wrong = new List<string>();
        var wild = 0;
        using var tx = _sim.Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
        {
            var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
            var bounds = cluster.GetReadOnlySpan(CreatureLair.Bounds);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                var lair = spawners[slot];
                if (cluster.Realm.Value != 0 || lair.Site == 0)
                {
                    continue;
                }

                wild++;
                var (x, z) = (bounds[slot].X, bounds[slot].Z);
                if (!levels.Agrees(x, z, lair.Level) || !CreatureTemplates.CoversLevel(lair.CreatureTemplate, lair.Level))
                {
                    wrong.Add($"{CreatureTemplates.Name(lair.CreatureTemplate)} of level {lair.Level} at ({x:F0}, {z:F0}), "
                        + $"edge {levels.EdgeDistance(x, z):F0} m");
                }
            }
        }

        Assert.That(wild, Is.GreaterThan(0), "precondition: wild lairs stand");
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {wild} wild lairs, first: {(wrong.Count > 0 ? wrong[0] : "")}");
    }

    /// <summary>The cascade: a retired lair's creatures went with it — no creature names a lair that is gone.</summary>
    [Test]
    public void NoCreatureOutlivesItsLair()
    {
        var (orphans, creatures, _) = Census();
        Assert.That(creatures, Is.GreaterThan(0), "precondition: the world has creatures");
        Assert.That(orphans, Is.Zero, $"{orphans} of {creatures} creatures name a lair that no longer exists: the FK cascade did not take them");
    }

    /// <summary>What the director counts as a lair's population is exactly the set of creatures whose foreign key names it.</summary>
    [Test]
    public void ALairsCountIsTheCreaturesThatNameIt()
    {
        var (_, _, lairs) = Census();
        var wrong = new List<string>();
        foreach (var (id, (alive, named, limit)) in lairs)
        {
            if (alive != named || named > limit)
            {
                wrong.Add($"{id}: AliveCount {alive}, {named} creatures name it, limit {limit}");
            }
        }

        Assert.That(lairs, Is.Not.Empty, "precondition: the world has lairs");
        Assert.That(wrong, Is.Empty, $"{wrong.Count} of {lairs.Count} lairs disagree with their creatures, first: {(wrong.Count > 0 ? wrong[0] : "")}");
    }

    /// <summary>A site's live count is the lairs on disk carrying its number, and never more than its target.</summary>
    [Test]
    public void EverySiteCountsTheLairsThatStandInIt()
    {
        var counted = new Dictionary<int, int>();
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
            {
                if (cluster.Realm.Value != 0)
                {
                    continue;
                }

                var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var site = spawners[BitOperations.TrailingZeroCount(bits)].Site;
                    if (site != 0)
                    {
                        counted[site] = counted.GetValueOrDefault(site) + 1;
                    }
                }
            }
        }

        var sites = _sim.Index.Sites;
        Assert.That(sites, Is.Not.Empty, "precondition: the world has lair sites");
        Assert.Multiple(() =>
        {
            for (var s = 0; s < sites.Count; s++)
            {
                Assert.That(counted.GetValueOrDefault(s + 1), Is.EqualTo(sites[s].Live), $"site {s}: the director's live count disagrees with the disk");
                Assert.That(sites[s].Live, Is.LessThanOrEqualTo(sites[s].Target), $"site {s} holds more lairs than it was built with");
            }
        });
    }

    /// <summary>Every creature, whether its lair still exists, and per lair: its AliveCount, the creatures naming it, and its limit.</summary>
    private (int Orphans, int Creatures, Dictionary<EntityId, (int Alive, int Named, int Limit)> Lairs) Census()
    {
        var lairs = new Dictionary<EntityId, (int Alive, int Named, int Limit)>();
        var orphans = 0;
        var creatures = 0;
        using var tx = _sim.Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
        {
            var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                lairs[cluster.GetEntityId(slot)] = (spawners[slot].AliveCount, 0, spawners[slot].SpawnLimit);
            }
        }

        foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
        {
            var homes = cluster.GetReadOnlySpan(Creature.Home);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                creatures++;
                EntityId lair = homes[BitOperations.TrailingZeroCount(bits)].Lair;
                if (lairs.TryGetValue(lair, out var row))
                {
                    lairs[lair] = (row.Alive, row.Named + 1, row.Limit);
                }
                else
                {
                    orphans++;
                }
            }
        }

        return (orphans, creatures, lairs);
    }
}
