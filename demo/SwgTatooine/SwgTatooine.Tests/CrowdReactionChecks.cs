using System;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// A body of the scripted crowd that a player attacks fights back or runs, never stands — and the crowd round it joins in (V33, Loïc 2026-10-26: "either run
/// away or fight back, but not standing waiting to die"). See <c>SimBridge.ReactToAttack</c>.
/// </summary>
/// <remarks>
/// <para>
/// Staged: the attacker and its quarry parked ten metres apart in open ground, the quarry in the activity the case is about and picked by its temperament
/// (<see cref="SimBridge.FightsBack"/>), so each case is the branch it names and not a coin toss. A "traveller" is parked travelling at a crawl towards a
/// point far off, so it keeps its activity while the case sets up and is not re-decided by the activity mix.
/// </para>
/// <para>
/// Reads go through the tick (<see cref="Read"/>): a player is SingleVersion and spatial, and an off-tick walk could see it half-written (#1496).
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class CrowdReactionChecks : ServedChecks
{
    private TatooineSim Start()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        config.CrowdTakesMissions = false;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    /// <summary>What the checks read of a player, on the tick.</summary>
    private readonly record struct Seen(float X, float Z, int Activity, EntityId Target, int Health, int MaxHealth, int Down, float DestX, float DestZ);

    private static Seen Read(TatooineSim sim, EntityId body)
    {
        Seen seen = default;
        sim.WriteInTick(tx =>
        {
            var p = tx.Open(body);
            var at = p.Read(Player.Bounds);
            var v = p.Read(Player.Vitals);
            var m = p.Read(Player.Move);
            seen = new Seen(at.X, at.Z, p.Read(Player.State).Activity, p.Read(Player.Session).Target, v.Health, v.MaxHealth, v.Down, m.DestX, m.DestZ);
        });
        return seen;
    }

    /// <summary>A body of the crowd on the planet, standing, of the temperament asked for, and none of <paramref name="besides"/>.</summary>
    private static EntityId CrowdBody(TatooineSim sim, bool fightsBack, params EntityId[] besides)
    {
        var found = EntityId.Null;
        var skip = new HashSet<EntityId>(besides);
        sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
            {
                var controls = cluster.GetReadOnlySpan(Player.Control);
                var vitals = cluster.GetReadOnlySpan(Player.Vitals);
                for (var bits = cluster.OccupancyBits; bits != 0 && found.IsNull; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    var id = cluster.GetEntityId(slot);
                    if (cluster.Realm.Value == 0 && controls[slot].Kind == ControllerKind.Scripted && vitals[slot].Down == Downed.Up && !skip.Contains(id)
                        && sim.Bridge.FightsBack(id) == fightsBack)
                    {
                        found = id;
                    }
                }
            }
        });
        Assert.That(found.IsNull, Is.False, $"the premise: a body of the crowd that {(fightsBack ? "fights back" : "runs")}");
        return found;
    }

    /// <summary>Logs the attacker in and parks it; returns its link and body.</summary>
    private (FakeLink Link, EntityId Body) LogIn(TatooineSim sim, float x, float z)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x7001));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar to be spawned");
        Harness.Ticks(2);
        return (link, Worlds.Park(sim, link.Session, x, z));
    }

    /// <summary>Parks a body of the crowd at a point in an activity; a traveller crawls towards a point a kilometre north, so it keeps travelling.</summary>
    /// <returns>The tick of the last write, which a wait for the attacker to name the body counts from.</returns>
    private static long Place(TatooineSim sim, EntityId body, float x, float z, int activity)
    {
        Worlds.Park(sim, body, x, z, 0, activity);
        return sim.WriteInTick(tx =>
        {
            var p = tx.OpenMut(body);
            var move = p.Read(Player.Move);
            move.DestX = x;
            move.DestZ = z + 1000f;
            move.SpeedMps = 0.01f;
            p.Set(Player.Move, move);
        });
    }

    /// <summary>The planet's far south-west, inside the world: no city, lair site or terminal near, so nothing else is in the fight.</summary>
    private static (float X, float Z) OpenGround(TatooineSim sim)
    {
        var reach = sim.Config.WorldEdgeM * 0.5f * WorldBuilder.InsideEdge * 0.9f;
        return (-reach, -reach);
    }

    /// <summary>The attacker in open ground and the quarry ten metres east of it, in an activity.</summary>
    private (FakeLink Link, EntityId Body) Stage(TatooineSim sim, EntityId quarry, int activity)
    {
        var (x, z) = OpenGround(sim);
        var (link, body) = LogIn(sim, x, z);
        var placed = Place(sim, quarry, x + 10f, z, activity);
        SessionHarness.UntilNamed(link.Session, quarry, "the quarry to reach the attacker's view", placed);
        return (link, body);
    }

    private static void Attack(FakeLink link, EntityId quarry)
    {
        link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(SessionHarness.NetIdOf(link.Session, quarry)));
        link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
    }

    private static float Distance(Seen a, Seen b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Z - b.Z) * (a.Z - b.Z)));

    private static float Distance(float ax, float az, float bx, float bz) => MathF.Sqrt(((ax - bx) * (ax - bx)) + ((az - bz) * (az - bz)));

    // ── The body attacked ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A traveller that fights back takes its attacker as its target, goes to Combat, says so, and its shots land on the attacker.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void ABraveTravellerAttacked_FightsBack_AndSaysSo()
    {
        using var sim = Start();
        var quarry = CrowdBody(sim, fightsBack: true);
        var (link, body) = Stage(sim, quarry, PlayerActivity.Travelling);
        var before = Read(sim, body).Health;

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdFoughtBack >= 1, "the quarry to answer the first blow");
        var seen = Read(sim, quarry);
        Assert.Multiple(() =>
        {
            Assert.That(seen.Target, Is.EqualTo(body), "it did not take its attacker as its target");
            Assert.That(seen.Activity, Is.EqualTo(PlayerActivity.Combat));
            Assert.That(sim.Bridge.CrowdFled, Is.Zero, "a body that fights back ran");
            Assert.That(sim.Bridge.CrowdShouts, Is.EqualTo(1), "the first blow of an attack is said out loud, once");
        });

        SessionHarness.Until(() => Read(sim, body).Health < before, "the quarry's shots to land on its attacker");
        Assert.That(sim.Bridge.CrowdShouts, Is.EqualTo(1), "a later blow of the same attack shouted again");
    }

    /// <summary>A traveller that runs goes Travelling, away from its attacker, says so, and gets further from it.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void ACowardlyTravellerAttacked_RunsAway_AndSaysSo()
    {
        using var sim = Start();
        var quarry = CrowdBody(sim, fightsBack: false);
        var (link, body) = Stage(sim, quarry, PlayerActivity.Travelling);
        var start = Distance(Read(sim, body), Read(sim, quarry));

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdFled >= 1, "the quarry to answer the first blow");
        SessionHarness.Until(() => Distance(Read(sim, body), Read(sim, quarry)) > start + 15f, "the quarry to get away from its attacker");
        var seen = Read(sim, quarry);
        Assert.Multiple(() =>
        {
            Assert.That(seen.Activity, Is.EqualTo(PlayerActivity.Travelling));
            Assert.That(seen.Target.IsNull, Is.True, "a body running away kept a target");
            Assert.That(sim.Bridge.CrowdFoughtBack, Is.Zero, "a body that runs fought back");
            Assert.That(sim.Bridge.CrowdShouts, Is.EqualTo(1));
        });
    }

    /// <summary>A body idling was not looking for trouble: it runs, however brave.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void AnIdleBodyAttacked_Runs_WhateverItsTemperament()
    {
        using var sim = Start();
        var quarry = CrowdBody(sim, fightsBack: true);
        var (link, _) = Stage(sim, quarry, PlayerActivity.Idle);

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdFled + sim.Bridge.CrowdFoughtBack >= 1, "the quarry to answer the first blow");
        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.CrowdFled, Is.EqualTo(1), "an idle body stood and fought");
            Assert.That(Read(sim, quarry).Activity, Is.EqualTo(PlayerActivity.Travelling));
        });
    }

    /// <summary>
    /// An idle body that ran keeps running at every later blow, however brave: the first blow decides. Re-deciding read the <c>Travelling</c> the first
    /// blow had set — temperament's — and a brave one turned round on the second shot (seen live).
    /// </summary>
    [Test]
    [CancelAfter(90_000)]
    public void ARunner_KeepsRunningAtLaterBlows()
    {
        using var sim = Start();
        var quarry = CrowdBody(sim, fightsBack: true);
        var (link, _) = Stage(sim, quarry, PlayerActivity.Idle);

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdFled >= 3 || Read(sim, quarry).Down != Downed.Up, "three blows to land on the runner");
        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.CrowdFled, Is.GreaterThanOrEqualTo(3), "the premise: it took three blows standing");
            Assert.That(sim.Bridge.CrowdFoughtBack, Is.Zero, "a runner turned round at a later blow");
        });
    }

    /// <summary>A body already fighting fights its attacker too, however timid.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void AFighterAttacked_FightsBack_WhateverItsTemperament()
    {
        using var sim = Start();
        var quarry = CrowdBody(sim, fightsBack: false);
        var (link, body) = Stage(sim, quarry, PlayerActivity.Combat);

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdFled + sim.Bridge.CrowdFoughtBack >= 1, "the quarry to answer the first blow");
        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.CrowdFoughtBack, Is.EqualTo(1), "a fighter ran from a fight");
            Assert.That(Read(sim, quarry).Target, Is.EqualTo(body));
        });
    }

    /// <summary>One that would fight back runs instead once it is under a third of its health.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void ABraveBodyHurt_RunsAway()
    {
        using var sim = Start();
        var quarry = CrowdBody(sim, fightsBack: true);
        var (link, _) = Stage(sim, quarry, PlayerActivity.Travelling);
        sim.WriteInTick(tx =>
        {
            var p = tx.OpenMut(quarry);
            var v = p.Read(Player.Vitals);
            v.Health = Math.Max(2, (int)(v.MaxHealth * TatooineData.CrowdFleeBelowHealth * 0.5f));
            p.Set(Player.Vitals, v);
        });

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdFled >= 1 || Read(sim, quarry).Down != Downed.Up, "the hurt quarry to answer the blow");
        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.CrowdFled, Is.GreaterThanOrEqualTo(1), "the premise: the blow did not put it down at once");
            Assert.That(sim.Bridge.CrowdFoughtBack, Is.Zero, "a hurt body stood and fought");
            Assert.That(Read(sim, quarry).Activity, Is.EqualTo(PlayerActivity.Travelling));
        });
    }

    /// <summary>One walking to a door when it is attacked drops the walk and answers: seen live, the walk used to carry it on under fire.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void ABodyWalkingToADoor_StillAnswers()
    {
        using var sim = Start();
        var quarry = CrowdBody(sim, fightsBack: true);
        var (link, body) = Stage(sim, quarry, PlayerActivity.Travelling);

        // A real walk to a door, as TryWalkToPortal starts one: the door held by its room's realm, the body aimed at the door's approach. Parked in
        // ToPortal without a door, ThinkPortal puts it back to Idle at once, and the case proves nothing.
        var room = Worlds.RoomOf(sim, StructureKind.Building);
        var door = Worlds.DoorInto(sim, room);
        sim.WriteInTick(tx =>
        {
            var p = tx.OpenMut(quarry);
            var state = p.Read(Player.State);
            state.Activity = PlayerActivity.ToPortal;
            state.ActivityTicks = 200 * sim.Config.TickRateHz;
            state.ShuttleDest = room;
            p.Set(Player.State, state);
            var move = p.Read(Player.Move);
            (move.DestX, move.DestZ) = sim.Portals.ApproachStart[door];
            move.SpeedMps = TatooineData.PlayerRunSpeedMps;
            p.Set(Player.Move, move);
        });
        Harness.Ticks(3);
        Assert.That(Read(sim, quarry).Activity, Is.EqualTo(PlayerActivity.ToPortal), "the premise: the quarry is walking to a door");

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdFoughtBack >= 1, "the quarry to answer the first blow");
        var seen = Read(sim, quarry);
        Assert.Multiple(() =>
        {
            Assert.That(seen.Activity, Is.EqualTo(PlayerActivity.Combat), "it kept walking to its door");
            Assert.That(seen.Target, Is.EqualTo(body));
        });
    }

    // ── The crowd round it ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The crowd within 20 m of the body attacked joins in at the first blow, each by its own temperament — a brave one fights the attacker, a timid one
    /// scatters — and a body further off does not.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheCrowdRoundTheBodyAttacked_JoinsIn()
    {
        using var sim = Start();
        var quarry = CrowdBody(sim, fightsBack: true);
        var brave = CrowdBody(sim, fightsBack: true, quarry);
        var timid = CrowdBody(sim, fightsBack: false, quarry, brave);
        var far = CrowdBody(sim, fightsBack: true, quarry, brave, timid);
        var (x, z) = OpenGround(sim);
        var (link, body) = LogIn(sim, x, z);
        var placed = Place(sim, quarry, x + 10f, z, PlayerActivity.Travelling);
        Place(sim, brave, x + 14f, z + 4f, PlayerActivity.Travelling);
        Place(sim, timid, x + 14f, z - 4f, PlayerActivity.Travelling);
        Place(sim, far, x + 10f, z + 45f, PlayerActivity.Travelling);
        SessionHarness.UntilNamed(link.Session, quarry, "the quarry to reach the attacker's view", placed);
        Harness.Ticks(3);   // the parked bodies into the spatial index, which the crowd is found through

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdJoined >= 2, "the crowd round the quarry to join in");
        var b = Read(sim, brave);
        var t = Read(sim, timid);
        var f = Read(sim, far);
        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.CrowdJoined, Is.EqualTo(2), "only the two within reach joined");
            Assert.That(b.Target, Is.EqualTo(body), "the brave neighbour did not take on the attacker");
            Assert.That(b.Activity, Is.EqualTo(PlayerActivity.Combat));
            Assert.That(t.Activity, Is.EqualTo(PlayerActivity.Travelling), "the timid neighbour did not scatter");
            Assert.That(t.Target.IsNull, Is.True);
            Assert.That(f.Target.IsNull && f.Activity == PlayerActivity.Travelling && f.DestZ > z + 1000f, Is.True, "a body 45 m off joined in");
        });
    }

    // ── Where it runs ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A body attacked just outside a city, the city ahead of it, runs into the city — halfway in from the edge — not into the desert.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void ARunnerNearACity_MakesForIt()
    {
        using var sim = Start();
        var city = Worlds.WidestCity(sim);
        var quarry = CrowdBody(sim, fightsBack: false);
        var (link, _) = LogIn(sim, city.X + city.Radius + 150f, city.Z);
        var placed = Place(sim, quarry, city.X + city.Radius + 140f, city.Z, PlayerActivity.Travelling);
        SessionHarness.UntilNamed(link.Session, quarry, "the quarry to reach the attacker's view", placed);

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdFled >= 1, "the quarry to answer the first blow");
        var seen = Read(sim, quarry);
        Assert.That(Distance(seen.DestX, seen.DestZ, city.X, city.Z), Is.EqualTo(city.Radius * 0.5f).Within(2f), "it did not make for the city");
    }

    /// <summary>One already in a city's heart runs straight away: halfway in is where it stands, and it "ran" nowhere (seen live).</summary>
    [Test]
    [CancelAfter(60_000)]
    public void ARunnerInACitysHeart_RunsStraightAway()
    {
        using var sim = Start();
        var city = Worlds.WidestCity(sim);
        var (x, z) = (city.X + 5f, city.Z);
        var (dx, dz) = sim.Bridge.FleeTo(0, x, z, -1f, 0f);
        Assert.That(Distance(x, z, dx, dz), Is.EqualTo(TatooineData.CrowdFleeM).Within(0.5f), "it did not run its 60 m");
        Assert.That(dx, Is.LessThan(x), "it did not run away from its attacker");
    }

    /// <summary>One whose attacker stands between it and the city runs the other way, straight out: never past its attacker.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void ARunnerWithTheAttackerBetweenItAndTheCity_RunsStraightAway()
    {
        using var sim = Start();
        var city = Worlds.WidestCity(sim);
        var quarry = CrowdBody(sim, fightsBack: false);
        var (link, _) = LogIn(sim, city.X + city.Radius + 140f, city.Z);
        var placed = Place(sim, quarry, city.X + city.Radius + 150f, city.Z, PlayerActivity.Travelling);
        SessionHarness.UntilNamed(link.Session, quarry, "the quarry to reach the attacker's view", placed);

        Attack(link, quarry);
        SessionHarness.Until(() => sim.Bridge.CrowdFled >= 1, "the quarry to answer the first blow");
        var seen = Read(sim, quarry);
        Assert.Multiple(() =>
        {
            Assert.That(seen.DestX, Is.GreaterThan(seen.X + (TatooineData.CrowdFleeM * 0.8f)), "it did not run straight away from its attacker");
            Assert.That(Distance(seen.DestX, seen.DestZ, city.X, city.Z), Is.GreaterThan(city.Radius + 150f), "it ran past its attacker into the city");
        });
    }
}
