using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>Builds a small SWG world under %TEMP% and removes it afterwards.</summary>
internal static class Worlds
{
    public static SimConfig Small(string dir) => new()
    {
        DatabaseDirectory = dir,
        PopulationScale = 0.05f,
        PageCacheMiB = 256,
        WorkerCount = 4,
        TickRateHz = 10,
        Unpaced = true,
    };

    public static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typhon-swg-realms", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Delete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort: a file still mapped by a finalizer-pending handle is removed with the temp directory later
        }
    }

    /// <summary>Everything a query box can cover in any of the demo's realms.</summary>
    public static readonly AABB2F Everywhere = new() { MinX = -1e6f, MinY = -1e6f, MaxX = 1e6f, MaxY = 1e6f };

    public static PlayerPlacement At(float x, float z)
    {
        var p = default(PlayerPlacement);
        p.SetAt(x, z, 0.5f);
        return p;
    }

    /// <summary>The player ids a realm's spatial index answers for a box, through the engine's realm-scoped query.</summary>
    public static HashSet<EntityId> PlayersIn(DatabaseEngine dbe, ushort realm, in AABB2F box)
    {
        var ids = new HashSet<EntityId>();
        Span<ClusterSpatialQueryResult> buffer = new ClusterSpatialQueryResult[64];
        var e = dbe.ClusterSpatialQuery<Player>(new RealmId(realm)).AABB(in box);
        try
        {
            int got;
            while ((got = e.Fill(buffer)) > 0)
            {
                for (var i = 0; i < got; i++)
                {
                    ids.Add(buffer[i].Entity);
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        return ids;
    }
}

/// <summary>
/// Realms G1d: a portal crossing keeps the entity — same id, same components — and moves it between the planet's index and the interior's.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class PortalTests
{
    [Test]
    public void Portal_RoundTrip_EntityIdStable()
    {
        var dir = Worlds.NewDirectory();
        try
        {
            var config = Worlds.Small(dir);
            config.Interiors = true;
            config.Shuttles = false;
            using var sim = new TatooineSim(config);
            sim.Initialize();
            var dbe = sim.Dbe;
            Assert.That(sim.InteriorsPerPlanet, Is.EqualTo(WorldBuilder.CountEnterable(sim.Map)).And.GreaterThan(0));

            var id = sim.Index.Players[0];
            var door = sim.Index.Portals[0];
            var interior = (ushort)config.Planets;   // portal 0 of planet 0
            int missions;
            using (var tx = dbe.CreateQuickTransaction())
            {
                missions = tx.Open(id).Read(Player.State).MissionsCompleted;
            }

            dbe.WriteTickFence(1_000);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
            {
                tx.Teleport(id, Player.Bounds, new RealmId(interior), Worlds.At(32.5f, 6.5f));
                Assert.That(tx.Commit(), Is.True);
            }

            dbe.WriteTickFence(1_001);
            using (var tx = dbe.CreateQuickTransaction())
            {
                Assert.That(Worlds.PlayersIn(dbe, interior, Worlds.Everywhere), Is.EquivalentTo(new[] { id }), "the interior's index holds exactly the crosser");
                Assert.That(Worlds.PlayersIn(dbe, 0, Worlds.Everywhere), Does.Not.Contain(id), "the planet's index no longer answers for it");
                var e = tx.Open(id);
                Assert.That(e.Read(Player.Realm).Value, Is.EqualTo(interior));
                Assert.That(e.Read(Player.State).MissionsCompleted, Is.EqualTo(missions), "the crossing moved the entity, it did not respawn it");
            }

            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
            {
                tx.Teleport(id, Player.Bounds, RealmId.Default, Worlds.At(door.X + 8f, door.Z));
                Assert.That(tx.Commit(), Is.True);
            }

            dbe.WriteTickFence(1_002);
            using (var tx = dbe.CreateQuickTransaction())
            {
                Assert.That(Worlds.PlayersIn(dbe, interior, Worlds.Everywhere), Is.Empty);
                var near = new AABB2F { MinX = door.X, MinY = door.Z - 1f, MaxX = door.X + 9f, MaxY = door.Z + 1f };
                Assert.That(Worlds.PlayersIn(dbe, 0, near), Does.Contain(id), "back on the planet, a step outside the door");
                Assert.That(tx.IsAlive(id), Is.True);
                Assert.That(tx.Open(id).Read(Player.Realm).Value, Is.EqualTo(0));
            }
        }
        finally
        {
            Worlds.Delete(dir);
        }
    }
}

/// <summary>
/// Realms G1d, at scale: a galaxy — two planets, their interiors, space — ticked through the demo's own systems. Players cross portals and take shuttles to
/// the other planet; afterwards every player is in exactly one realm, the one its key names, and every sampled query answers exactly what a brute-force
/// scan of that realm's entities answers.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class GalaxyTests
{
    private string _dir;
    private TatooineSim _sim;
    private (long Entries, long Exits, long InterPlanet) _crossings;

    [OneTimeSetUp]
    public void RunGalaxy()
    {
        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.Planets = 2;
        config.Interiors = true;
        config.Space = true;
        config.InteriorShare = 1f;
        config.InteriorStayS = 15f;   // 15-60 s: some players are still inside when the run stops, and early ones have left
        config.ShuttleShare = 1f;
        config.ShuttleIntervalS = 2f;
        config.BoardingWindowS = 1f;
        config.InterPlanetShare = 1f;
        // 2 Hz: the behaviour is defined in seconds, so 400 ticks are 200 s of play — long enough to walk to a door and back — at a small world's cost.
        config.TickRateHz = 2;
        config.WarmTicks = 10;
        config.MeasuredTicks = 400;

        // Realms G2: interiors sleep after 2 s unobserved, planet 1 and space run at divisor 2, and four dungeons open and close during the run.
        config.InteriorSleepS = 2f;
        config.PlanetDivisor = 2;
        config.SpaceDivisor = 2;
        config.Dungeons = 4;
        config.DungeonIntervalS = 30f;
        config.DungeonStayS = 20f;
        config.DungeonParty = 4;
        _sim = new TatooineSim(config);
        _sim.Initialize();
        _sim.Run();
        _crossings = _sim.CrossingTotals;
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    /// <summary>Every realm a player may be in: the planets, the interiors and any dungeon still open.</summary>
    private IEnumerable<ushort> PlayerRealms()
    {
        for (var id = 0; id < _sim.Dbe.Realms.MaxRealms; id++)
        {
            if (id != _sim.SpaceRealm && _sim.Dbe.Realms.IsRegistered(new RealmId((ushort)id)))
            {
                yield return (ushort)id;
            }
        }
    }

    [Test]
    public void Crossings_HappenInEveryDirection()
    {
        Assert.That(_crossings.Entries, Is.GreaterThan(0), "players walked into buildings");
        Assert.That(_crossings.Exits, Is.GreaterThan(0), "and out again");
        Assert.That(_crossings.InterPlanet, Is.GreaterThan(0), "and took shuttles to the other planet");
    }

    [Test]
    public void EveryPlayer_IsInExactlyOneRealm_TheOneItsKeyNames()
    {
        var dbe = _sim.Dbe;
        using var tx = dbe.CreateQuickTransaction();
        var seen = new Dictionary<EntityId, ushort>();
        foreach (var realm in PlayerRealms())
        {
            foreach (var id in Worlds.PlayersIn(dbe, realm, Worlds.Everywhere))
            {
                Assert.That(seen.TryAdd(id, realm), Is.True, $"player {id} answers in realm {realm} and in realm {(seen.TryGetValue(id, out var o) ? o : -1)}");
                Assert.That(tx.Open(id).Read(Player.Realm).Value, Is.EqualTo(realm), $"player {id} is indexed in realm {realm} but its key says otherwise");
            }
        }

        Assert.That(seen.Count, Is.EqualTo(_sim.Census.Players), "no player lost or duplicated by the crossings");
        Assert.That(new HashSet<ushort>(seen.Values).Count, Is.GreaterThanOrEqualTo(2), "players ended up on both planets");
    }

    [Test]
    public void Realm_QueriesNeverCrossRealms()
    {
        var dbe = _sim.Dbe;
        using var tx = dbe.CreateQuickTransaction();

        // The oracle: every player's realm from its KEY column and its box, read cluster by cluster — none of the realm's grid or index structures.
        var byRealm = new Dictionary<ushort, List<(EntityId Id, AABB2F Box)>>();
        foreach (var cluster in tx.GetClusterEnumerator<Player>())
        {
            var places = cluster.GetReadOnlySpan(Player.Bounds);
            var keys = cluster.GetReadOnlySpan(Player.Realm);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var idx = System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (!byRealm.TryGetValue(keys[idx].Value, out var list))
                {
                    byRealm[keys[idx].Value] = list = [];
                }

                list.Add((cluster.GetEntityId(idx), places[idx].Bounds));
            }
        }

        var rng = new Random(1019);
        var queries = 0;
        foreach (var realm in PlayerRealms())
        {
            var interior = realm >= _sim.Config.Planets;
            if (interior && rng.Next(8) != 0)
            {
                continue;   // a sample of the 1 200+ interiors; every planet
            }

            var edge = interior ? WorldBuilder.InteriorEdgeM : _sim.Config.WorldEdgeM;
            var lo = interior ? 0f : -edge * 0.5f;
            for (var q = 0; q < (interior ? 4 : 64); q++)
            {
                var size = (float)(rng.NextDouble() * edge * (interior ? 0.6 : 0.2)) + 0.37f;
                var x = lo + (float)(rng.NextDouble() * (edge - size));
                var z = lo + (float)(rng.NextDouble() * (edge - size));
                var box = new AABB2F { MinX = x, MinY = z, MaxX = x + size, MaxY = z + size };
                var expected = new HashSet<EntityId>();
                if (byRealm.TryGetValue(realm, out var members))
                {
                    foreach (var (id, b) in members)
                    {
                        if (b.MinX <= box.MaxX && b.MaxX >= box.MinX && b.MinY <= box.MaxY && b.MaxY >= box.MinY)
                        {
                            expected.Add(id);
                        }
                    }
                }

                Assert.That(Worlds.PlayersIn(dbe, realm, box), Is.EquivalentTo(expected), $"realm {realm}, box {x},{z} +{size}");
                queries++;
            }
        }

        Assert.That(queries, Is.GreaterThan(128));
    }

    [Test]
    public void UnobservedInteriors_GoDormant_OccupiedOnesStayActive()
    {
        // Counted, not timed. That a dormant realm's clusters reach no system is the engine's RLM-04 (RealmPolicyTests); here, that the demo's
        // interiors actually go dormant at scale while the occupied ones stay active.
        var counts = _sim.Dbe.Realms.Counts;
        Assert.That(counts.Dormant, Is.GreaterThan(_sim.InteriorsPerPlanet), "most interiors hold no player and sleep");
        Assert.That(counts.Divided, Is.EqualTo(2), "planet 1 and space run at their divisor");
        var occupied = 0;
        for (var r = _sim.Config.Planets; r < _sim.Config.Planets * (1 + _sim.InteriorsPerPlanet); r++)
        {
            var realm = new RealmId((ushort)r);
            if (Worlds.PlayersIn(_sim.Dbe, realm.Value, Worlds.Everywhere).Count > 0)
            {
                occupied++;
                // Pinned, not "not Dormant": a player entering in the last tick is indexed there while the realm still reads the state that tick's
                // start decided — the pin takes effect at the next tick start (review #4).
                Assert.That(_sim.IsInteriorPinned(realm.Value), Is.True, $"interior {r} holds a player and nothing pins it");
            }
        }

        Assert.That(occupied, Is.GreaterThan(0), "precondition: some interior is occupied at the end");
    }

    [Test]
    public void Dungeons_OpenAndClose_AndAClosedOneLeavesNothingBehind()
    {
        var (opened, closed) = _sim.DungeonTotals;
        Assert.That(opened, Is.GreaterThanOrEqualTo(3), "dungeons opened during the run");
        Assert.That(opened - closed, Is.InRange(0, 1), "every dungeon but the last is closed");
        for (var slot = 0; slot < closed; slot++)
        {
            var realm = new RealmId((ushort)(_sim.FirstDungeonRealm + slot));
            Assert.That(_sim.Dbe.Realms.IsRegistered(realm), Is.False, $"dungeon {slot} was removed once emptied");
        }

        Assert.That(_sim.Dbe.Realms.Counts.Closing, Is.Zero, "nothing waits in Closing: each emptied dungeon went at its fence");
    }

    [Test]
    public void Space_ShipsAnswerOnlyInSpace()
    {
        var dbe = _sim.Dbe;
        using var tx = dbe.CreateQuickTransaction();
        var space = new RealmId((ushort)_sim.SpaceRealm);
        var all = new AABB3D { MinX = -1e7, MinY = -1e7, MinZ = -1e7, MaxX = 1e7, MaxY = 1e7, MaxZ = 1e7 };
        var e = dbe.ClusterSpatialQuery<Starship>(space).AABB(in all);
        int inSpace;
        try
        {
            inSpace = e.Count();
        }
        finally
        {
            e.Dispose();
        }

        Assert.That(inSpace, Is.EqualTo(_sim.Census.Starships).And.GreaterThan(0));
        var planet = dbe.ClusterSpatialQuery<Starship>(RealmId.Default).AABB(in all);
        try
        {
            Assert.That(planet.Count(), Is.Zero, "a planet's index never answers for a starship");
        }
        finally
        {
            planet.Dispose();
        }
    }
}
