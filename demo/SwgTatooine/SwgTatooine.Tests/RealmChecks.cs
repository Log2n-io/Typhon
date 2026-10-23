using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SwgTatooine.Server;
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

    /// <summary>
    /// The tick rate of every fixture that serves a world to fake clients: fast, because their waits are ticks and the waits are most of their cost.
    /// </summary>
    /// <remarks>
    /// Every interval the server reads is in seconds and converted at the configured rate (S0-3), so a case that counts ticks and a case that counts
    /// seconds both mean the same thing at any rate. Not higher, because the gate runs this suite on a free 4-core runner, where a 10 ms budget is one the
    /// overload manager would start shedding systems against after five slow ticks. One that genuinely waits on seconds of game time, such as a weapon's
    /// cooldown, is slower at a faster rate and keeps its own (AttackEventChecks).
    /// </remarks>
    public const int ServedHz = 50;

    /// <summary>A small world paced at <see cref="ServedHz"/>, for a fixture that serves it to fake clients.</summary>
    /// <param name="dir">Its database directory.</param>
    /// <returns>The configuration, which the caller may adjust further.</returns>
    public static SimConfig Served(string dir)
    {
        var config = Small(dir);
        config.Unpaced = false;
        config.TickRateHz = ServedHz;
        return config;
    }

    /// <summary>The planet side of the door into <paramref name="interior"/>, as its row in the world's door table.</summary>
    /// <param name="sim">The simulation.</param>
    /// <param name="interior">A room's realm.</param>
    /// <returns>The row; the case fails when no door leads there.</returns>
    public static int DoorInto(TatooineSim sim, ushort interior)
    {
        var doors = sim.Portals;
        for (var row = 0; row < doors.Count; row++)
        {
            if (doors.Realm[row] < sim.Config.Planets && doors.Door[row].TargetRealm == interior)
            {
                return row;
            }
        }

        Assert.Fail($"no door leads into realm {interior}");
        return -1;
    }

    /// <summary>Which realm an entity is in — 0 is the planet, and on a multi-planet server each planet is one.</summary>
    /// <param name="sim">The simulation.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Its realm.</returns>
    public static ushort RealmOf(TatooineSim sim, EntityId entity)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        Assert.That(tx.TryOpen(entity, out var player), Is.True, "the entity whose realm was asked for does not exist");
        return player.Read(Player.Realm).Value;
    }

    /// <summary>Puts the player a session controls at a place in a realm, parked, and returns it.</summary>
    /// <param name="sim">The simulation.</param>
    /// <param name="session">The session whose player to place.</param>
    /// <param name="x">Where, east.</param>
    /// <param name="z">Where, north.</param>
    /// <param name="realm">Which realm, 0 for the planet.</param>
    /// <returns>The player entity.</returns>
    /// <remarks>
    /// <b>Parked, not merely moved</b>: the activity is set to idle for longer than any case runs and the velocity zeroed, because a player that resumed
    /// deciding would walk out of whatever the case placed it in — out of earshot, or out of a city whose group set the case is asserting on.
    /// </remarks>
    public static EntityId Park(TatooineSim sim, SessionId session, float x, float z, ushort realm = 0)
    {
        var entity = TatooineReplication.ControlledBy(sim.Dbe, session);
        Assert.That(entity, Is.Not.EqualTo(EntityId.Null), "the session controls no player to place");
        Park(sim, entity, x, z, realm);
        return entity;
    }

    /// <summary>Puts a player at a place in a realm, parked in an activity it will not leave.</summary>
    /// <param name="sim">The simulation.</param>
    /// <param name="entity">The player.</param>
    /// <param name="x">Where, east.</param>
    /// <param name="z">Where, north.</param>
    /// <param name="realm">Which realm, 0 for the planet.</param>
    /// <param name="activity">What it is doing there: <c>Idle</c>, or <c>Inside</c> for a player PlayerThink must not walk back out of a room.</param>
    /// <returns>The tick that applied the placement, or -1 when the runtime was not running yet.</returns>
    /// <remarks>
    /// <b>Through <see cref="TatooineSim.WriteInTick(System.Action{Transaction})"/>, never a quick transaction of its own.</b> A placement writes a
    /// spatial archetype, which a thread outside the tick must not (EW-02): done from the test thread while the server ticked, it raced the fence and
    /// aborted a tick on CR-05 in two runs of three at 50 Hz.
    /// </remarks>
    public static long Park(TatooineSim sim, EntityId entity, float x, float z, ushort realm = 0, int activity = PlayerActivity.Idle)
        => sim.WriteInTick(tx =>
        {
            var player = tx.OpenMut(entity);
            var state = player.Read(Player.State);
            state.Activity = activity;
            state.ActivityTicks = int.MaxValue / 2;
            player.Set(Player.State, state);
            var move = player.Read(Player.Move);
            move.VelX = 0f;
            move.VelZ = 0f;
            player.Set(Player.Move, move);

            var at = default(PlayerPlacement);
            at.SetAt(x, z, 0f, player.Read(Player.Bounds).HalfExtent);
            tx.Teleport(entity, Player.Bounds, new RealmId(realm), in at);
        });

    /// <summary>Moves a player to a place in a realm and changes nothing else about it, inside a tick for the reason <c>Park</c> gives.</summary>
    /// <param name="sim">The simulation.</param>
    /// <param name="entity">The player.</param>
    /// <param name="realm">Which realm.</param>
    /// <param name="at">Where.</param>
    /// <returns>The tick that applied it, or -1 when the runtime was not running yet.</returns>
    public static long Teleport(TatooineSim sim, EntityId entity, ushort realm, PlayerPlacement at)
        => sim.WriteInTick(tx => tx.Teleport(entity, Player.Bounds, new RealmId(realm), in at));

    /// <summary>The widest city in a built world: the one whose radius most exceeds a player's sphere.</summary>
    /// <param name="sim">A simulation whose world is built.</param>
    /// <returns>Its index and its circle.</returns>
    public static (int Index, float X, float Z, float Radius) WidestCity(TatooineSim sim)
    {
        // A named premise rather than an IndexOutOfRangeException from the line below: a world with no cities is a
        // world the caller's case cannot mean anything in, and saying so here is three layers closer to the cause.
        Assert.That(sim.Index.Cities, Is.Not.Empty, "the world has no cities, so there is no widest one to stand in");

        var widest = 0;
        for (var i = 0; i < sim.Index.Cities.Count; i++)
        {
            if (sim.Index.Cities[i].Radius > sim.Index.Cities[widest].Radius)
            {
                widest = i;
            }
        }

        var city = sim.Index.Cities[widest];
        return (widest, city.X, city.Z, city.Radius);
    }

    /// <summary>
    /// Where this process's worlds go: one folder per test process, so <see cref="TempWorldsCleanup"/> can remove whatever a case left
    /// behind, and a sharded run's processes never share one.
    /// </summary>
    public static readonly string Root = Path.Combine(
        Path.GetTempPath(), "typhon-swg-realms", $"{Environment.ProcessId}-{Guid.NewGuid():N}");

    public static string NewDirectory()
    {
        var dir = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Removes a world's directory; a case calls it from its teardown, which NUnit runs whether the case passed or not.</summary>
    /// <param name="dir">The directory <see cref="NewDirectory"/> handed out.</param>
    /// <remarks>
    /// <b>Retried once after a collection, and said out loud when it still fails.</b> A database file still mapped by a handle waiting for its
    /// finalizer refuses the delete; swallowing that silently is how 768 directories and 12 GB built up in <c>%TEMP%</c> unnoticed. What
    /// survives is removed with <see cref="Root"/> at the end of the run, and counted there.
    /// </remarks>
    public static void Delete(string dir)
    {
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    continue;
                }

                TestContext.Progress.WriteLine($"could not delete the world at {dir}: {e.Message} (left for the end-of-run sweep)");
            }
        }
    }

    /// <summary>Everything a query box can cover in any of the demo's realms.</summary>
    public static readonly AABB2F Everywhere = new() { MinX = -1e6f, MinY = -1e6f, MaxX = 1e6f, MaxY = 1e6f };

    public static PlayerPlacement At(float x, float z)
    {
        var p = default(PlayerPlacement);
        p.SetAt(x, z, 0f, 0.5f);
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
[Category("Integration")]
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
            config.Shuttles = false;
            using var sim = new TatooineSim(config);
            sim.Initialize();
            var dbe = sim.Dbe;
            Assert.That(sim.InteriorsPerPlanet, Is.EqualTo(WorldBuilder.CountEnterable(sim.Map)).And.GreaterThan(0));

            var id = sim.Index.Players[0];
            var interior = (ushort)config.Planets;   // the first building of planet 0
            var front = sim.Portals.Door[Worlds.DoorInto(sim, interior)];
            var door = (X: front.CenterX, Z: front.CenterZ);
            int missions;
            using (var tx = dbe.CreateQuickTransaction())
            {
                missions = tx.Open(id).Read(Player.State).MissionsCompleted;
            }

            dbe.WriteTickFence(1_000);
            using (var tx = dbe.CreateQuickTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
            {
                tx.Teleport(id, Player.Bounds, new RealmId(interior), Worlds.At(front.ArriveX, front.ArriveZ));
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
/// Realms G2's positive half: while a player is inside a building, that building's realm is pinned and runs — and its neighbour, which nobody entered, sleeps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the door, not through a teleport.</b> The pin follows a COMMITTED crossing (<c>SimBridge.TeleportTick</c>), so a player simply placed in an
/// interior realm would be indoors with nothing pinning it -- which is the state the demo never produces and this case would then be asserting about. The
/// player is therefore put at a door in <see cref="PlayerActivity.ToPortal"/> and the world ticked: <c>PlayerThink</c> sees it arrive, queues the crossing,
/// <c>TeleportTick</c> commits it and takes the pin, exactly as a wandering player's would be.
/// </para>
/// <para>
/// <b>Nobody else may cross, which is what makes the sleeping neighbour a claim.</b> <see cref="SimConfig.InteriorShare"/> is zero, so no other player can
/// decide to walk into a building: every interior but the staged one is untouched for the whole run, and finding one of them awake would mean the sleep
/// condition is not the absence of an occupant.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class OccupiedInteriorTests
{
    private string _dir;
    private TatooineSim _sim;
    private ushort _occupied;
    private ushort _empty;
    private EntityId _player;

    [OneTimeSetUp]
    public void RunWorld()
    {
        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.Shuttles = false;
        config.Dungeons = 0;

        // Nobody wanders indoors on their own: the only crossing in this run is the staged one.
        config.InteriorShare = 0f;

        // Longer than the window, so the staged player is still inside when the run stops, and short enough that the sleep has time to take the others:
        // 10 Hz x 120 ticks is 12 s of play against a 2 s sleep.
        config.InteriorStayS = 120f;
        config.InteriorSleepS = 2f;
        config.TickRateHz = 10;
        config.WarmTicks = 0;
        config.MeasuredTicks = 120;
        config.Unpaced = true;
        config.WorkerCount = 1;
        _sim = new TatooineSim(config);
        _sim.Initialize();
        Assert.That(_sim.InteriorsPerPlanet, Is.GreaterThan(1), "precondition: the map has at least two enterable buildings");

        // The first building of planet 0, and the second as the one nobody enters.
        _occupied = (ushort)config.Planets;
        _empty = (ushort)(config.Planets + 1);
        _player = _sim.Index.Players[0];
        var row = Worlds.DoorInto(_sim, _occupied);
        var (doorX, doorZ) = _sim.Portals.ApproachStart[row];

        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            var player = tx.OpenMut(_player);

            // At the start of the door's path and walking to it, which is the state TryWalkToPortal leaves a player in on the tick before it arrives.
            // ShuttleDest carries the door's row while the activity is ToPortal (PlayerState.ShuttleDest), and the timer only has to outlast the walk.
            var state = player.Read(Player.State);
            state.Activity = PlayerActivity.ToPortal;
            state.ActivityTicks = 200 * config.TickRateHz;
            state.ShuttleDest = row;
            player.Set(Player.State, state);
            var move = player.Read(Player.Move);
            move.SpeedMps = TatooineData.PlayerRunSpeedMps;
            move.VelX = 0f;
            move.VelZ = 0f;
            move.DestX = doorX;
            move.DestZ = doorZ;
            player.Set(Player.Move, move);

            var at = default(PlayerPlacement);
            at.SetAt(doorX, doorZ, 0f, player.Read(Player.Bounds).HalfExtent);
            tx.Teleport(_player, Player.Bounds, RealmId.Default, in at);
            tx.Commit();
        }

        _sim.Run();
    }

    [OneTimeTearDown]
    public void Dispose()
    {
        _sim?.Dispose();
        Worlds.Delete(_dir);
    }

    [Test]
    public void APlayerInsideABuilding_PinsItsRealmActive_AndTheBuildingNobodyEnteredSleeps()
    {
        // Read out under one transaction: a spatial query needs the epoch scope a transaction opens, and EntityRef is a ref struct that cannot cross into
        // Assert.Multiple's lambda (CS8175).
        ushort realm;
        HashSet<EntityId> inside;
        using (var tx = _sim.Dbe.CreateQuickTransaction())
        {
            realm = tx.Open(_player).Read(Player.Realm).Value;
            inside = Worlds.PlayersIn(_sim.Dbe, _occupied, Worlds.Everywhere);
        }

        var occupiedState = _sim.Dbe.Realms.StateOf(new RealmId(_occupied));
        var emptyState = _sim.Dbe.Realms.StateOf(new RealmId(_empty));

        Assert.Multiple(() =>
        {
            Assert.That(_sim.CrossingTotals.Entries, Is.EqualTo(1), "exactly the staged crossing happened, so the rest of this is about it");
            Assert.That(realm, Is.EqualTo(_occupied), "the player did not end up inside the building it walked into");
            Assert.That(inside, Is.EquivalentTo(new[] { _player }), "the interior's index holds the crosser");
            Assert.That(_sim.IsInteriorPinned(_occupied), Is.True, "an interior holds a player and nothing pins it: Realms G2's pin is gone");
            Assert.That(occupiedState, Is.EqualTo(RealmRunState.Active), "the interior a player is standing in is not being run");
            Assert.That(emptyState, Is.EqualTo(RealmRunState.Dormant), "a building nobody ever entered is awake: interiors are not sleeping on absence");
        });
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
        config.Space = true;
        config.InteriorShare = 1f;
        config.InteriorStayS = 15f;   // 15-60 s: some players are still inside when the run stops, and early ones have left

        // <b>The crowd walks through walls here, deliberately.</b> What this fixture is about is the teleport system —
        // that a crossing happens in every direction and the realm bookkeeping survives it — and the scripted crowd
        // steers in a straight line at its destination with no way around an obstacle. With collision on (the default
        // since 2026-10-22) a body whose door is behind another building walks into that building and stands there,
        // which cut measured entries from 47 to 12 over 2 000 ticks and to zero in a window this short. That is a real
        // limitation and it has its own work; letting it decide this fixture would turn a crossing test into a
        // navigation test and it would fail for a reason it does not name.
        config.CollideCrowd = false;
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

            // Both kinds of realm are centred on their origin: a room is in its own coordinates (WorldBuilder.InteriorEdgeM).
            var edge = interior ? WorldBuilder.InteriorEdgeM : _sim.Config.WorldEdgeM;
            var lo = -edge * 0.5f;
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

    /// <summary>Realms G2 at scale: a galaxy of 1 200 interiors sleeps, and any one that happens to hold a player is pinned.</summary>
    /// <remarks>
    /// <b>The positive half of this claim is <see cref="OccupiedInteriorTests"/>, and it used to be a precondition here.</b> The line was
    /// <c>occupied &gt; 0</c> -- "some interior is occupied at the end" -- which samples one instant, the last tick of a 400-tick run, and
    /// asks a population on 15-to-60-second stays to have somebody indoors at exactly that moment. It is the same emergent-event
    /// fragility as AC-3's leash was, and it failed the same way. What a snapshot of a galaxy can honestly state is the negative half --
    /// most interiors hold nobody and sleep -- so that is what stayed, and a player is now walked through a door deliberately to state
    /// the other.
    /// </remarks>
    [Test]
    public void InteriorsGoDormantAtScale_AndAnyOccupiedOneIsPinned()
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

        // No assertion that `occupied` is non-zero: see the remark. Whether anybody is indoors on the last tick of this run is the activity
        // mix's business, and a case that demanded it would be asserting on the seed.
        Assert.That(occupied, Is.LessThanOrEqualTo(_sim.Census.Players), "more interiors hold a player than there are players");
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
