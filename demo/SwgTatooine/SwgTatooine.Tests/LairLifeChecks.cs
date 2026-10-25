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

    /// <summary>
    /// A live destroy-mission lair stands with its defenders round it. The pool used to teleport the lair to its player and leave them where they stood: 381
    /// of 386 live mission camps had nobody near them (measured, 2026-10-25). It is replaced at the mission's point now, defenders and all.
    /// </summary>
    [Test]
    public void ALiveMissionLairHasItsDefendersRoundIt()
    {
        var lairs = new Dictionary<EntityId, (float X, float Z)>();
        var near = 0;
        var far = 0;
        var endless = 0;
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
            {
                var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                var places = cluster.GetReadOnlySpan(CreatureLair.Bounds);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    if (spawners[slot].MissionId != 0)
                    {
                        lairs[cluster.GetEntityId(slot)] = (places[slot].X, places[slot].Z);
                        endless += spawners[slot].ExpireTick == 0 ? 1 : 0;
                    }
                }
            }

            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                var places = cluster.GetReadOnlySpan(Creature.Bounds);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = BitOperations.TrailingZeroCount(bits);
                    if (lairs.TryGetValue(homes[slot].Lair, out var at))
                    {
                        var d = MathF.Sqrt(((places[slot].X - at.X) * (places[slot].X - at.X)) + ((places[slot].Z - at.Z) * (places[slot].Z - at.Z)));
                        if (d <= 30f)
                        {
                            near++;
                        }
                        else
                        {
                            far++;
                        }
                    }
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(_sim.MissionLairsRehoused, Is.GreaterThan(0), "precondition: missions were issued");
            Assert.That(lairs.Count, Is.GreaterThan(0), "precondition: a mission is live at the end of the run");
            Assert.That(near, Is.GreaterThan(0), "a live mission lair has no defender");
            Assert.That(far, Is.LessThanOrEqualTo(near / 10), $"{far} defenders stand far from their live mission lair, {near} near it");
            Assert.That(endless, Is.Zero, "a live mission has no end: the replacement lair did not carry it over");
        });
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

/// <summary>
/// A destroy mission runs out: its lair goes back to the pool unpaid when the time its owner was given has passed, and keeps it until then (2026-10-25).
/// </summary>
/// <remarks>
/// Nothing but a lair's death used to end a mission, so a mission its owner stopped pursuing held its lair for good, and a small pool ran dry. Staged rather
/// than waited for: two pool lairs put on a mission, one already past its end, and held off the pool's next offer, which would replace the lair read back.
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class MissionExpiryChecks
{
    [Test]
    public void AMissionPastItsEnd_GoesBackToThePoolUnpaid_AndOneBeforeItsEndStands()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.WorkerCount = 1;
            config.WarmTicks = 0;
            config.MeasuredTicks = 5;
            using var sim = new TatooineSim(config);
            sim.Initialize();
            var owner = sim.Index.Players[0];
            var pool = new List<EntityId>();
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
                {
                    var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                    for (var bits = cluster.OccupancyBits; bits != 0 && pool.Count < 2; bits &= bits - 1)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        if (spawners[slot].CreatureTemplate == CreatureTemplates.MissionDefender && spawners[slot].MissionId == 0)
                        {
                            pool.Add(cluster.GetEntityId(slot));
                        }
                    }
                }

                Assert.That(pool, Has.Count.EqualTo(2), "the premise: two dormant pool lairs");
                for (var i = 0; i < pool.Count; i++)
                {
                    var lair = tx.OpenMut(pool[i]);
                    var spawner = lair.Read(CreatureLair.Spawner);
                    spawner.MissionId = 5;
                    spawner.Owner = owner;
                    spawner.ExpireTick = i == 0 ? 1 : 1_000_000;

                    // Not offered again inside the window: an offer replaces the lair (SimBridge.Rehouse), and this one is to be read back.
                    spawner.RespawnCooldown = 1_000;
                    lair.Set(CreatureLair.Spawner, spawner);
                    var vitals = lair.Read(CreatureLair.Vitals);
                    vitals.Health = vitals.MaxHealth / 2;
                    lair.Set(CreatureLair.Vitals, vitals);
                }

                Assert.That(tx.Commit(), Is.True);
            }

            sim.Run();

            using var read = sim.Dbe.CreateQuickTransaction();
            var ran = read.Open(pool[0]);
            var stands = read.Open(pool[1]);
            var ranOut = ran.Read(CreatureLair.Spawner);
            var ranVitals = ran.Read(CreatureLair.Vitals);
            var standing = stands.Read(CreatureLair.Spawner);
            var s = sim.LastStats;
            Assert.Multiple(() =>
            {
                Assert.That(ranOut.MissionId, Is.Zero, "a mission past its end still holds its lair");
                Assert.That(ranOut.Owner.IsNull, Is.True);
                Assert.That(ranOut.ExpireTick, Is.Zero);
                Assert.That(ranVitals.Health, Is.EqualTo(ranVitals.MaxHealth), "back in the pool unhealed");
                Assert.That(s.MissionsExpired, Is.EqualTo(1));
                Assert.That(s.MissionsCompleted, Is.Zero, "an expiry was counted as a completion");
                Assert.That(s.MissionRewards, Is.Zero, "an expired mission paid its owner");
                Assert.That(standing.MissionId, Is.EqualTo(5), "a mission before its end was ended");
                Assert.That((EntityId)standing.Owner, Is.EqualTo(owner));
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}

/// <summary>
/// A mission lair moved on a pass tick, while it owed a defender: <c>Rehouse</c> destroys it, the pass's walk still yields it (#1482), and its refill
/// opened a lair the transaction had destroyed — the tick aborted and the server stopped (2026-10-25, twice).
/// </summary>
[TestFixture]
public sealed class MovedLairRefillChecks
{
    [Test]
    public void AMissionLairMovedOnAPassTick_WhileOwedADefender_IsNotRefilled_AndTheTickStands()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.WorkerCount = 1;
            config.WarmTicks = 0;
            config.MeasuredTicks = 12;
            using var sim = new TatooineSim(config);
            sim.Initialize();
            var staged = 0;
            using (var tx = sim.Dbe.CreateQuickTransaction())
            {
                var owner = EntityId.Null;
                foreach (var p in sim.Index.Players)
                {
                    if (tx.Open(p).Read(Player.Realm).Value == RealmId.Default.Value)
                    {
                        owner = p;
                        break;
                    }
                }

                Assert.That(owner.IsNull, Is.False, "the premise: a player outdoors");
                var at = tx.Open(owner).Read(Player.Bounds);
                var pool = new List<EntityId>();
                foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
                {
                    var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                    for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                    {
                        var slot = BitOperations.TrailingZeroCount(bits);
                        if (spawners[slot].CreatureTemplate == CreatureTemplates.MissionDefender && spawners[slot].MissionId == 0)
                        {
                            pool.Add(cluster.GetEntityId(slot));
                        }
                    }
                }

                // The four dormant lairs at the player, offered on four consecutive ticks round the first pass tick. A lair is offered on the tick its
                // cooldown runs out (cooldown c: tick c - 1, ticks numbered from 0), and LairLife runs only on a tick with an event in its queues — the
                // move is that event — so the pass runs on the tick its lair is moved. Each owes a defender already due: the walk picks it for a refill.
                var passTick = Math.Max(1, config.TickRateHz / SimBridge.LairPassHz);
                for (var i = 0; i < pool.Count && staged < 4; i++)
                {
                    var lair = tx.OpenMut(pool[i]);
                    var place = lair.Read(CreatureLair.Bounds);
                    place.SetAt(at.X, at.Z, at.Y, place.HalfExtent);
                    lair.Set(CreatureLair.Bounds, place);
                    var spawner = lair.Read(CreatureLair.Spawner);
                    spawner.RespawnCooldown = passTick + staged;
                    spawner.AliveCount = Math.Max(0, spawner.SpawnLimit - 1);
                    spawner.NextSpawnTick = 1;
                    lair.Set(CreatureLair.Spawner, spawner);
                    staged++;
                }

                Assert.That(tx.Commit(), Is.True);
            }

            Assert.That(staged, Is.EqualTo(4), "the premise: the small world's four dormant pool lairs");
            sim.Run();
            Assert.Multiple(() =>
            {
                Assert.That(sim.TickAbort, Is.Null, $"a tick aborted: {sim.TickAbort?.SystemName} {sim.TickAbort?.Reason}");
                Assert.That(sim.MissionLairsRehoused, Is.GreaterThanOrEqualTo(4), "the premise: each staged lair was offered and moved");
                Assert.That(sim.RefillsOfGoneLairs, Is.GreaterThan(0), "the premise: a moved lair was picked for a refill on its pass tick");
            });
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}
