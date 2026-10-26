using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Navigation;

namespace SwgTatooine.Tests;

/// <summary>
/// The bot's city life — the swoop, a door, a terminal, the ticket terminal — driven against a body this test moves the way the server would.
/// </summary>
/// <remarks>
/// <b>The server's half is simulated, and only its geometry-free half.</b> A <c>MoveTo</c> puts the body on its point, a heading moves it one tick of
/// its class, <c>SetMount</c> sets the replicated flag, and a door is "crossed" when the body stands past the door's last published point. What the
/// brain decides is the subject; whether the server walks a body up a ramp is <c>InteriorChecks</c>'.
/// </remarks>
[TestFixture]
public sealed class BotErrandChecks
{
    private const int HouseAsset = 0;
    private const int RoomAsset = 1;
    private const int TerminalAsset = 2;
    private const int PortAsset = 3;
    private const int BlockAsset = 4;
    private const int LargeHouseAsset = 5;
    private const int WalledPortAsset = 6;

    /// <summary>The published <c>tat_house_large</c>'s walls, as the server serves them in <c>demo.json</c> (already in the world's convention).</summary>
    private static readonly PlanBox[] LargeHouseWalls =
    [
        new(-2.95f, 4.3f, -0.05f, 4.85f, 0.7f, 2.7f, false), new(4.05f, 6.1f, 5.55f, 6.6f, 2.45f, 2.7f, false),
        new(5.55f, 6.1f, 8.5f, 6.6f, 0.7f, 2.7f, false), new(1.2f, 6.1f, 4.05f, 6.6f, 0.7f, 2.7f, false),
        new(8f, 3.5f, 8.5f, 6.6f, 0.7f, 2.7f, false), new(-3.2f, 6.89f, 0.2f, 11.25f, -1.28f, 0.7f, true),
        new(-7.5f, -4.5f, 3.5f, 4.5f, 0.7f, 2.7f, false), new(-0.15f, 4.35f, 0.7f, 5.15f, 0.7f, 2.7f, false),
        new(-3.7f, 4.35f, -2.85f, 5.15f, 0.7f, 2.7f, false), new(-9f, -7f, 9f, 7f, -1f, 0.7f, false),
        new(-2.7f, 4.85f, -0.3f, 5.1f, 0.7f, 1f, false), new(-2.7f, 5.03f, -0.3f, 6.09f, 0.44f, 1f, true),
        new(-9.8f, 2.2f, -5.2f, 6.8f, 0.7f, 2.7f, false, 2.3f), new(3.3f, -4f, 8.2f, 3.5f, 0.7f, 2.7f, false),
    ];

    /// <summary>Port Sable, where every case stands.</summary>
    private static readonly City Here = BotMap.Cities[0];

    /// <summary>A 6 m house with its door on +Z, a room behind it, a mission terminal and a shuttleport, as plans in the world's (mirrored) frame.</summary>
    private static BotAssets Content()
    {
        var house = new AssetPlan
        {
            Id = "house",
            Walls = [new PlanBox(-3f, -3f, 3f, 3f, 0f, 2.7f, false)],
            Door = new DoorPlan([(0f, 6f), (0f, 4f), (0f, 2.6f)], "room"),
        };

        // Three nodes in an L; cells 1 m from (-2, -2), every cell served by its nearest node.
        var nodes = new (float X, float Z)[] { (0f, 0f), (1.5f, 0f), (0f, 1.5f) };
        var cells = new int[4 * 4];
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                // The grid is the descriptor's, unmirrored: cell centre x = -2 + c + 0.5 there is -(that) here.
                var x = -(-2f + c + 0.5f);
                var z = -2f + r + 0.5f;
                var best = 0;
                for (var n = 1; n < nodes.Length; n++)
                {
                    if (Sq(nodes[n].X - x) + Sq(nodes[n].Z - z) < Sq(nodes[best].X - x) + Sq(nodes[best].Z - z))
                    {
                        best = n;
                    }
                }

                cells[(r * 4) + c] = best;
            }
        }

        var room = new AssetPlan
        {
            Id = "room",
            Door = new DoorPlan([(0f, 1.5f), (0f, 2.3f)], "house"),
            Nav = new RoomNav(nodes, [(0, 1), (0, 2)], -2f, -2f, 1f, 4, 4, cells),
        };

        var terminal = new AssetPlan { Id = "mission_terminal", Walls = [new PlanBox(-0.4f, -0.3f, 0.4f, 0.3f, 0f, 1.6f, false)], UsePath = [(0f, 0.65f)] };
        var port = new AssetPlan { Id = "port", UsePath = [(-2f, 25f), (-2f, 21.4f)] };
        // A city block: 120 m of wall, deep enough that no search starting inside it gets out, and wide enough to stand on a detour's point whichever
        // western city a seed rides to.
        var block = new AssetPlan { Id = "block", Walls = [new PlanBox(-60f, -60f, 60f, 60f, 0f, 3f, false)] };
        return new BotAssets([house, room, terminal, port, block, new AssetPlan { Id = "tat_house_large", Walls = LargeHouseWalls }, WalledPort()]);
    }

    /// <summary>A shuttleport as the published one is built: a 20 m ring of wall with a gate on +Z, its plaza floor and its raised pad.</summary>
    private static AssetPlan WalledPort() => new()
    {
        Id = "tat_shuttleport",
        Walls = [.. RingWalls().Select(r => new PlanBox(r.MinX, r.MinZ, r.MaxX, r.MaxZ, 0.3f, 2.3f, false)),
            new PlanBox(-21.6f, -21.6f, 21.6f, 21.6f, -1f, 0.3f, false, 21.6f),
            new PlanBox(-12f, -12f, 12f, 12f, 0.3f, 1.26f, false, 12f)],
        UsePath = [(0f, 26f), (0f, 16f)],
    };

    /// <summary>The ring's blocks, in the port's frame: 1.8 m squares every 5 degrees at 20 m, but for the gate.</summary>
    private static IEnumerable<(float MinX, float MinZ, float MaxX, float MaxZ)> RingWalls()
    {
        for (var deg = 0; deg < 360; deg += 5)
        {
            var x = 20f * MathF.Cos(deg * MathF.PI / 180f);
            var z = 20f * MathF.Sin(deg * MathF.PI / 180f);
            if (z > 0f && MathF.Abs(x) < 4.4f)
            {
                continue;   // the gate
            }

            yield return (x - 0.9f, z - 0.9f, x + 0.9f, z + 0.9f);
        }
    }

    private static float Sq(float v) => v * v;

    /// <summary>The world as the test's server holds it, and one step of it.</summary>
    private sealed class Sim
    {
        public float X = Here.X;
        public float Z = Here.Z;
        public bool Mounted;
        public int Realm;
        public int PortRegion;
        public List<SeenStructure> Planet = [];
        public List<SeenStructure> Room = [new SeenStructure(0f, 0f, 8, RoomAsset, 0, 0)];
        public readonly List<Intent> Sent = [];

        /// <summary>Missions held, as the journal would say.</summary>
        public int MissionsHeld;

        /// <summary>A plan rectangle no step may cross — a house, grown by nothing: the walls the brain must go round. Checked along each step's segment.</summary>
        public (float MinX, float MinZ, float MaxX, float MaxZ)? Forbidden;

        /// <summary>Whether <see cref="Forbidden"/> is enforced: not for the steps of goals a seed chose before the one under test.</summary>
        public bool Checking = true;

        /// <summary>A plan rectangle the server stops a body at, as its collision does: a step into it ends where it touched.</summary>
        public (float MinX, float MinZ, float MaxX, float MaxZ)? Wall;

        /// <summary>More of them, in the world's frame: a shuttleport's ring.</summary>
        public List<(float MinX, float MinZ, float MaxX, float MaxZ)> Walls = [];

        /// <summary>How far a <c>MoveTo</c> falls short east of its point: the crowd and a terminal's collider shoving a body off a stand point.</summary>
        public float ShoveM;

        /// <summary>Where the body was when it last got off its swoop.</summary>
        public (float X, float Z)? GotOffAt;

        public Perceived View() => new()
        {
            HasBody = true,
            X = X,
            Z = Z,
            Realm = Realm,
            Mounted = Mounted,
            PortRegion = PortRegion,
            MissionsHeld = MissionsHeld,
            Structures = new ListStructures(Realm == 0 ? Planet : Room),
        };

        public void Apply(Intent intent)
        {
            Sent.Add(intent);
            var (ax, az) = (X, Z);
            Step(intent);
            var (bx, bz) = (X, Z);
            foreach (var w in Wall is { } one ? Walls.Append(one) : Walls)
            {
                for (var s = 0f; s <= 1f; s += 0.01f)
                {
                    var x = ax + ((bx - ax) * s);
                    var z = az + ((bz - az) * s);
                    if (x > w.MinX && x < w.MaxX && z > w.MinZ && z < w.MaxZ)
                    {
                        // Stopped at the first wall the step touches, whichever of them it is.
                        var back = MathF.Max(0f, s - 0.01f);
                        var (sx, sz) = (ax + ((bx - ax) * back), az + ((bz - az) * back));
                        if (Routing.Distance(ax, az, sx, sz) < Routing.Distance(ax, az, X, Z))
                        {
                            (X, Z) = (sx, sz);
                        }

                        break;
                    }
                }
            }

            if (Checking && Forbidden is { } f)
            {
                // The whole segment: a ten-metre swoop step hops a wall a point sample would miss.
                for (var s = 0f; s <= 1f; s += 0.02f)
                {
                    var x = ax + ((X - ax) * s);
                    var z = az + ((Z - az) * s);
                    Assert.That(x > f.MinX && x < f.MaxX && z > f.MinZ && z < f.MaxZ, Is.False, $"step {Sent.Count} ({intent.Kind} c{intent.SpeedClass}) from ({ax:F2}, {az:F2}) to ({X:F2}, {Z:F2}) went through a wall at ({x:F2}, {z:F2})");
                }
            }
        }

        private void Step(Intent intent)
        {
            switch (intent.Kind)
            {
                case IntentKind.MoveTo:
                    X = intent.X + ShoveM;
                    Z = intent.Z;
                    break;
                case IntentKind.Move:
                    var step = intent.SpeedClass == Routing.SwoopClass ? 10f : intent.SpeedClass == 0 ? 0f : 2.5f;
                    X += MathF.Cos(intent.Heading) * step;
                    Z += MathF.Sin(intent.Heading) * step;
                    break;
                case IntentKind.Mount:
                    GotOffAt = Mounted && !intent.On ? (X, Z) : GotOffAt;
                    Mounted = intent.On;
                    break;
            }
        }
    }

    /// <summary>Runs a brain until a goal is chosen from a seed, trying seeds until one picks it: the goal roll is the brain's, not the test's.</summary>
    private static (BotBrain Brain, Sim Sim) Choosing(Goal wanted, Action<BotBrain> teach, Func<Sim> world) =>
        Choosing(b => b.Goal == wanted, wanted.ToString(), teach, world);

    private static (BotBrain Brain, Sim Sim) Choosing(Func<BotBrain, bool> chosen, string what, Action<BotBrain> teach, Func<Sim> world)
    {
        for (var seed = 1u; seed < 400u; seed++)
        {
            var brain = new BotBrain(BotMap.Cities, seed, 4d, Content());
            teach(brain);
            var sim = world();
            for (var tick = 1L; tick < 4 && !chosen(brain); tick++)
            {
                // The step of the tick that chose the goal is the goal's first, and is held to it; the steps before belong to other goals.
                var intent = brain.Decide(sim.View(), tick);
                sim.Checking = chosen(brain);
                sim.Apply(intent);
                sim.Checking = true;
            }

            if (chosen(brain))
            {
                return (brain, sim);
            }
        }

        Assert.Fail($"no seed under 400 chose {what}");
        return default;
    }

    /// <summary>A trip longer than thirty seconds on foot gets on the swoop, rides at its class, and gets off on arrival.</summary>
    [Test]
    public void ALongTripIsRiddenOnTheSwoop()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d);
        var sim = new Sim { X = 0f, Z = 0f };
        for (var tick = 1L; tick < 40 && brain.Goal != Goal.Travelling; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.That(brain.Mode, Is.EqualTo(TravelMode.Swoop), "the premise: a city kilometres from the origin is ridden");

        var t = 100L;
        var mount = brain.Decide(sim.View(), t++);
        sim.Apply(mount);
        var ride = brain.Decide(sim.View(), t++);
        sim.Apply(ride);
        Assert.Multiple(() =>
        {
            Assert.That(mount, Is.EqualTo(Intent.Mount(true)), "a long trip did not get on the swoop first");
            Assert.That(ride.Kind, Is.EqualTo(IntentKind.Move));
            Assert.That(ride.SpeedClass, Is.EqualTo(Routing.SwoopClass), "mounted, it did not ask for the swoop's class");
        });

        var city = BotMap.Cities[brain.TargetRegion - 1];
        sim.X = city.X;
        sim.Z = city.Z;
        var stop = brain.Decide(sim.View(), t++);
        var off = brain.Decide(sim.View(), t + 10);
        Assert.Multiple(() =>
        {
            Assert.That(stop.SpeedClass, Is.Zero, "arriving did not stop");
            Assert.That(off, Is.EqualTo(Intent.Mount(false)), "arrived and idling, it stayed on the swoop");
        });
    }

    /// <summary>The swoop's class is never asked for before the server says the body is mounted: a refused mount runs instead.</summary>
    [Test]
    public void ARefusedMountRunsAndStopsAsking()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d);
        var world = new Perceived { HasBody = true };
        var mounts = 0;
        for (var tick = 1L; tick < 200; tick++)
        {
            var intent = brain.Decide(in world, tick);
            mounts += intent.Kind == IntentKind.Mount ? 1 : 0;
            Assert.That(intent.SpeedClass, Is.Not.EqualTo(Routing.SwoopClass), "the swoop's class was asked for while not mounted");
        }

        Assert.That(mounts, Is.InRange(1, 3), "a refused mount was asked for without end, or never");
    }

    /// <summary>A bot in a city walks round a house to its door, walks the door's published path, and the crossing takes it in.</summary>
    [Test]
    public void ABotWalksRoundAHouseAndThroughItsDoor()
    {
        // The house stands between the bot and its door: the bot is due south, the door faces north.
        var (brain, sim) = Choosing(
            Goal.Indoors,
            _ => { },
            () => new Sim
            {
                X = Here.X,
                Z = Here.Z - 10f,
                Planet = [new SeenStructure(Here.X, Here.Z, 0, HouseAsset, 0, 1)],
            });

        for (var tick = 10L; tick < 200 && sim.Realm == 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));

            // Never inside the house's walls, grown by the body.
            Assert.That(MathF.Abs(sim.X - Here.X) < 3.3f && MathF.Abs(sim.Z - Here.Z) < 3.3f && sim.Z < Here.Z + 2.6f, Is.False,
                $"the walk went through the house at ({sim.X - Here.X:F1}, {sim.Z - Here.Z:F1})");

            // The server's crossing: past the door's last point.
            if (sim.Z >= Here.Z + 2.6f - 0.01f && MathF.Abs(sim.X - Here.X) < 0.5f && sim.Z < Here.Z + 3.5f)
            {
                sim.Realm = 5;
                sim.X = 0f;
                sim.Z = 1.4f;
            }
        }

        Assert.That(sim.Realm, Is.EqualTo(5), "the bot never went through the door");
        var through = sim.Sent.FindLastIndex(i => i.Kind == IntentKind.MoveTo);
        Assert.That(sim.Sent[through], Is.EqualTo(Intent.MoveTo(Here.X, Here.Z + 2.6f)), "the last step was not the door's published last point");
        Assert.That(brain.PathsPlanned, Is.GreaterThanOrEqualTo(1), "the walk round the house was not searched");

        // In, it stays: three seconds at least before it moves. A timer left over from the street turned the fourth live run's bot round in the doorway.
        for (var tick = 1000L; tick < 1012; tick++)
        {
            Assert.That(brain.Decide(sim.View(), tick).Kind, Is.EqualTo(IntentKind.None), $"tick {tick - 1000} inside, it was already on its way out");
        }
    }

    /// <summary>Inside, it looks round on the room's graph and then leaves by the way out, and outside it is a bot in the street again.</summary>
    [Test]
    public void InsideItLeavesByTheRoomsWayOut()
    {
        var brain = new BotBrain(BotMap.Cities, 11u, 4d, Content());
        var sim = new Sim { Realm = 5, X = 0f, Z = 1.4f };
        var left = false;
        for (var tick = 1L; tick < 400 && !left; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
            Assert.That(brain.Goal, Is.EqualTo(Goal.Indoors));
            if (sim.Z >= 2.3f - 0.01f)
            {
                left = true;
            }
        }

        Assert.That(left, Is.True, "the bot never walked out of the room");
        Assert.That(sim.Sent[^1], Is.EqualTo(Intent.MoveTo(0f, 2.3f)), "it left by something other than the published way out");

        // Back on the planet, by the door it came through.
        sim.Realm = 0;
        sim.X = Here.X;
        sim.Z = Here.Z + 5.4f;
        brain.Decide(sim.View(), 500);
        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Idling), "out of the building, it did not stand a moment before deciding again");
            Assert.That(brain.DoorsCrossed, Is.EqualTo(1), "the crossing out was not counted");
        });
    }

    /// <summary>An errand walks to the nearest mission terminal's stand point, uses it a while, then decides again.</summary>
    [Test]
    public void AnErrandWalksToTheNearestTerminalAndUsesIt()
    {
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b =>
            {
                b.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 300f, Here.Z, TerminalAsset + 1, 0);
                b.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 2);
                b.Facilities.Observe(1, Routing.BazaarKind, Here.X + 40f, Here.Z + 10f, TerminalAsset + 1, 0);
            },
            () => new Sim { Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 2, 1)] });

        for (var tick = 10L; tick < 200 && brain.FacilitiesUsed == 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        // The near terminal turned a half turn: its stand point (0, 0.65) is (0, -0.65) from it. Used from within reach of it, not on it.
        var toMission = Routing.Distance(sim.X, sim.Z, Here.X + 40f, Here.Z - 0.65f);
        var toBazaar = Routing.Distance(sim.X, sim.Z, Here.X + 40f, Here.Z + 10f + 0.65f);
        Assert.Multiple(() =>
        {
            Assert.That(brain.FacilitiesUsed, Is.EqualTo(1), "the errand never reached a terminal");
            Assert.That(Math.Min(toMission, toBazaar), Is.LessThanOrEqualTo(BotBrain.UseReachM),
                $"it used a terminal from outside its reach, at ({sim.X - Here.X}, {sim.Z - Here.Z})");
        });
    }

    /// <summary>
    /// A long errand inside a city is walked round the house the body stands against, never ridden (Loïc, 2026-10-26: in a city, on foot), and no step
    /// passes through the house.
    /// </summary>
    /// <remarks>
    /// It was ridden: a 380 m errand on the swoop, which in a city's streets cannot navigate. The house check is the first live run's — a long walk's
    /// first stretch was a straight line through the house it stood against — and each step's whole segment is checked.
    /// </remarks>
    [Test]
    public void AnErrandInACity_IsWalked_RoundTheHouseItStandsAgainst()
    {
        var house = (X: Here.X, Z: Here.Z);
        var terminal = (X: Here.X - 380f, Z: Here.Z);
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, terminal.X, terminal.Z, TerminalAsset + 1, 0),
            () => new Sim
            {
                X = house.X + 3.4f,
                Z = house.Z,
                Planet = [new SeenStructure(house.X, house.Z, 0, HouseAsset, 0, 1), new SeenStructure(terminal.X, terminal.Z, 10, TerminalAsset, 0, 1)],
                Forbidden = (house.X - 3f, house.Z - 3f, house.X + 3f, house.Z + 3f),
            });

        for (var tick = 10L; tick < 1200 && brain.FacilitiesUsed == 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.FacilitiesUsed, Is.EqualTo(1), "the errand never reached the terminal");
            Assert.That(sim.Sent.Exists(i => i.Kind == IntentKind.Mount && i.On), Is.False, "it got on the swoop in a city");
            Assert.That(sim.Sent.Exists(i => i.Kind == IntentKind.Move && i.SpeedClass == Routing.SwoopClass), Is.False, "it rode in a city");
        });
    }

    /// <summary>A ride from the desert into a city ends at the city's edge: the rest of the way is on foot (Loïc, 2026-10-26).</summary>
    [Test]
    public void ARideIntoACity_EndsAtItsEdge()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, null, homeRegion: 1);
        var sim = new Sim { X = Here.X + Here.Radius + 900f, Z = Here.Z };
        var rodeOutside = false;
        var swoopInside = 0;
        var reached = false;
        for (var tick = 1L; tick < 4000 && !reached; tick++)
        {
            var inside = Routing.Distance(sim.X, sim.Z, Here.X, Here.Z) <= Here.Radius;
            var intent = brain.Decide(sim.View(), tick);
            if (intent.Kind == IntentKind.Move && intent.SpeedClass == Routing.SwoopClass)
            {
                rodeOutside |= !inside;
                swoopInside += inside ? 1 : 0;
            }

            sim.Apply(intent);
            reached = Routing.Distance(sim.X, sim.Z, Here.X, Here.Z) <= BotBrain.ArrivedM;
        }

        Assert.Multiple(() =>
        {
            Assert.That(rodeOutside, Is.True, "the premise: 900 m of desert was ridden");
            Assert.That(reached, Is.True, "it never reached the city's centre");
            Assert.That(swoopInside, Is.LessThanOrEqualTo(1), "it rode on inside the city: one swoop step is the tick its dismount is asked on");
            Assert.That(sim.Mounted, Is.False, "it is still on the swoop in the city");
        });
    }

    /// <summary>A rider travelling off a house it is pressed against gets round it — the first live run, where four bots never moved again.</summary>
    /// <remarks>
    /// The straight line to the city ran through the house, the stuck watchdog searched a way round, and the ride called the corner waypoints
    /// reached from eight metres off: so it steered at the third, through the house, and the server held it against the wall for the rest of the run.
    /// The simulated server here stops a step at the wall, as the real one does, so a brain that cannot get round stays where it started.
    /// </remarks>
    [Test]
    public void ARiderPressedAgainstAHouseGetsRoundIt()
    {
        var house = (X: Here.X, Z: Here.Z);
        var (brain, sim) = Choosing(
            b => b.Goal == Goal.Travelling && b.Mode == TravelMode.Swoop && BotMap.Cities[b.TargetRegion - 1].X < Here.X - 1000f,
            "a ride west",
            _ => { },
            () => new Sim
            {
                X = house.X + 3.4f,
                Z = house.Z,
                Mounted = true,
                Planet = [new SeenStructure(house.X, house.Z, 0, HouseAsset, 0, 1)],
                Wall = (house.X - 3.3f, house.Z - 3.3f, house.X + 3.3f, house.Z + 3.3f),
            });

        for (var tick = 10L; tick < 130; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.That(sim.X, Is.LessThan(house.X - 60f), $"a rider pressed against a house never got round it; it is at ({sim.X - house.X:F1}, {sim.Z - house.Z:F1})");
        // From the detour's first step: a fresh brain's first tick gets off whatever it sits on, which is not the detour's doing.
        var detour = sim.Sent.FindIndex(i => i.Kind == IntentKind.MoveTo);
        Assert.That(detour, Is.GreaterThanOrEqualTo(0), "the premise: the rider searched a way round");
        Assert.That(sim.Sent.Skip(detour), Does.Not.Contain(Intent.Mount(false)), "it got off its swoop to go round a house, and on again after");
    }

    /// <summary>A detour whose point ahead stands inside a city block is searched nearer instead, and the rider still gets round the house.</summary>
    /// <remarks>
    /// The second live run: a bot in Dry Wells pushed against one wall for four minutes, every search failing, because the one goal it searched for
    /// was inside the next building and a search that cannot reach its goal finds nothing.
    /// </remarks>
    [Test]
    public void ADetourWhosePointIsInsideABuildingSearchesNearer()
    {
        var house = (X: Here.X, Z: Here.Z);
        var (brain, sim) = Choosing(
            b => b.Goal == Goal.Travelling && b.Mode == TravelMode.Swoop && BotMap.Cities[b.TargetRegion - 1].X < Here.X - 1000f,
            "a ride west",
            _ => { },
            () => new Sim
            {
                X = house.X + 3.4f,
                Z = house.Z,
                Mounted = true,
                Planet = [new SeenStructure(house.X, house.Z, 0, HouseAsset, 0, 1), new SeenStructure(house.X - 200f, house.Z, 0, BlockAsset, 0, 1)],
                Wall = (house.X - 3.3f, house.Z - 3.3f, house.X + 3.3f, house.Z + 3.3f),
            });

        for (var tick = 10L; tick < 400 && sim.X > house.X - 60f; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.That(sim.X, Is.LessThan(house.X - 60f), $"the rider never got round the house; it is at ({sim.X - house.X:F1}, {sim.Z - house.Z:F1})");
    }

    /// <summary>A terminal is used from where the crowd lets the body stand: within its radius, not on its exact point.</summary>
    /// <remarks>The first live run: a bot shoved 0.7 m off the stand point circled it until the watchdog gave up, and no terminal was ever used.</remarks>
    [Test]
    public void ATerminalIsUsedFromWithinItsReachNotItsExactPoint()
    {
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0),
            () => new Sim { ShoveM = 0.7f, Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 0, 1)] });

        for (var tick = 10L; tick < 200 && brain.FacilitiesUsed == 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.That(brain.FacilitiesUsed, Is.EqualTo(1), "a bot shoved 0.7 m off a terminal's stand point never used it");
    }

    /// <summary>A body standing up on a house's porch walks off it rather than into the walls round it.</summary>
    /// <remarks>
    /// Three live bots stood a whole run on <c>tat_house_large</c>'s porch, in the pocket between its east room and its east wall. The porch is the
    /// foundation, 0.7 m up, so searched from the ground it is a wall: the start snapped out past the east wall and the first leg ran into it.
    /// </remarks>
    [Test]
    public void ABodyOnAPorchWalksOffItNotIntoItsWalls()
    {
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X - 60f, Here.Z + 30f, TerminalAsset + 1, 0),
            () => new Sim
            {
                X = Here.X + 7.6f,
                Z = Here.Z + 4.2f,
                Planet = [new SeenStructure(Here.X, Here.Z, 0, LargeHouseAsset, 0, 1), new SeenStructure(Here.X - 60f, Here.Z + 30f, 10, TerminalAsset, 0, 1)],

                // The east wall, as the server holds a body off it: grown by the body's half.
                Forbidden = (Here.X + 7.7f, Here.Z + 3.2f, Here.X + 8.8f, Here.Z + 6.9f),
            });

        for (var tick = 10L; tick < 300 && brain.FacilitiesUsed == 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.That(brain.FacilitiesUsed, Is.EqualTo(1), "the errand from the porch never reached the terminal");
    }

    /// <summary>A body on the ground beside a porch goes round the house: the porch is a floor only to a body already standing on it.</summary>
    /// <remarks>The fix for the porch-bound bots first searched the porch as floor for any body in its walls' margin, and walked one across it.</remarks>
    [Test]
    public void ABodyBesideAPorchGoesRoundIt()
    {
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X - 60f, Here.Z, TerminalAsset + 1, 0),
            () => new Sim
            {
                X = Here.X + 9.25f,
                Z = Here.Z,
                Planet = [new SeenStructure(Here.X, Here.Z, 0, LargeHouseAsset, 0, 1), new SeenStructure(Here.X - 60f, Here.Z, 10, TerminalAsset, 0, 1)],

                // The foundation, which a body on the ground cannot step up onto.
                Forbidden = (Here.X - 9f, Here.Z - 7f, Here.X + 9f, Here.Z + 7f),
            });

        for (var tick = 10L; tick < 300 && brain.FacilitiesUsed == 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.That(brain.FacilitiesUsed, Is.EqualTo(1), "the errand from beside the house never reached the terminal");
    }

    /// <summary>A ticket terminal no walk can reach is searched for once, not on every tick.</summary>
    /// <remarks>The review's worst finding: a failed plan cleared the leg, which read as "not planned yet", and a whole-grid search ran every tick.</remarks>
    [Test]
    public void AnUnreachableTicketTerminalIsNotSearchedEveryTick()
    {
        var port = (X: Here.X, Z: Here.Z - 60f);
        var (brain, sim) = Choosing(
            b => b.Goal == Goal.Travelling && b.Mode == TravelMode.Shuttle,
            "a flight",
            b => b.Facilities.Observe(1, Routing.ShuttleportKind, port.X, port.Z, PortAsset + 1, 0),
            () => new Sim { Planet = [new SeenStructure(port.X - 2f, port.Z + 25f, 0, BlockAsset, 0, 1)] });

        var before = brain.PathsPlanned;
        for (var tick = 10L; tick < 50; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.That(brain.PathsPlanned - before, Is.LessThanOrEqualTo(4), "the terminal's walk was searched again and again");
    }

    /// <summary>After an errand the shuttle: the ticket terminal's published path, then the ask — never the ask from across the street.</summary>
    [Test]
    public void AFlightWalksToTheTicketTerminalBeforeAsking()
    {
        var port = (X: Here.X, Z: Here.Z - 60f);
        var (brain, sim) = Choosing(
            b => b.Goal == Goal.Travelling && b.Mode == TravelMode.Shuttle,
            "a flight",
            b => b.Facilities.Observe(1, Routing.ShuttleportKind, port.X, port.Z, PortAsset + 1, 0),
            () => new Sim());

        Intent asked = default;
        for (var tick = 10L; tick < 200 && asked.Kind != IntentKind.Board; tick++)
        {
            sim.PortRegion = Routing.Distance(sim.X, sim.Z, port.X, port.Z) <= 30d ? 1 : 0;
            asked = brain.Decide(sim.View(), tick);
            sim.Apply(asked);
        }

        Assert.Multiple(() =>
        {
            Assert.That(asked.Kind, Is.EqualTo(IntentKind.Board), "the bot never asked to board");
            Assert.That((sim.X, sim.Z), Is.EqualTo((port.X - 2f, port.Z + 21.4f)), "it asked from somewhere other than the ticket terminal");
        });
    }

    /// <summary>A bot standing inside a shuttleport's walls with nothing to do there walks out by the gate, and the walls never hold it.</summary>
    /// <remarks>
    /// Loïc, 2026-10-26: bots stood trapped against the inside of Port Sable's wall, and reconnecting put them back where they stood. Here, a bot that
    /// lives in this city wakes on the far side of the plaza from the gate: its home has nothing for it to do, so all it would ever do is stand there.
    /// </remarks>
    [Test]
    public void ABotInsideAShuttleport_WalksOutByItsGate()
    {
        var port = (X: Here.X + 100f, Z: Here.Z);
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content(), homeRegion: 1);
        brain.Facilities.Observe(1, Routing.ShuttleportKind, port.X, port.Z, WalledPortAsset + 1, 0);
        var sim = new Sim
        {
            X = port.X,
            Z = port.Z - 16f,
            Planet = [new SeenStructure(port.X, port.Z, Routing.ShuttleportKind, WalledPortAsset, 0, 1)],
            Walls = [.. RingWalls().Select(r => (r.MinX + port.X, r.MinZ + port.Z, r.MaxX + port.X, r.MaxZ + port.Z))],
        };

        var outAt = -1L;
        for (var tick = 1L; tick < 400 && outAt < 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
            outAt = Routing.Distance(sim.X, sim.Z, port.X, port.Z) > 22d ? tick : -1;
        }

        Assert.Multiple(() =>
        {
            Assert.That(outAt, Is.GreaterThan(0), $"the bot never left the plaza; it is at ({sim.X - port.X:F1}, {sim.Z - port.Z:F1}) from the port");
            Assert.That(sim.Z - port.Z, Is.GreaterThan(19f), "it left by somewhere other than the gate");
            Assert.That(brain.PortsLeft, Is.EqualTo(1));
        });

        // Out, it stays out: it has nothing to do in there.
        for (var tick = outAt + 1; tick < outAt + 200; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
            Assert.That(Routing.Distance(sim.X, sim.Z, port.X, port.Z), Is.GreaterThan(19d), $"tick {tick}: it walked back into the plaza");
        }

        Assert.That(brain.InPort, Is.False, "out, it is still counted as standing in a port");
    }

    /// <summary>
    /// A walk across a city whose straight line runs in through a shuttleport's gate is searched round the port, and never sets foot in its plaza.
    /// </summary>
    /// <remarks>
    /// The straight line until stuck walked in through the gate and against the far wall, where the unstick searches failed for one start in twenty.
    /// The bot is not told where the port is (it has learned no facility), so only the walk's search can keep it out.
    /// </remarks>
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(5)]
    public void AWalkAcrossTown_GoesRoundAShuttleport_NotInByItsGate(int home)
    {
        var city = BotMap.Cities[home - 1];
        var length = (float)Routing.Distance(Here.X, Here.Z, city.X, city.Z);
        var dir = (X: (city.X - Here.X) / length, Z: (city.Z - Here.Z) / length);

        // The port 60 m along the way, turned so its gate faces the bot; the bot 15 m back from the gate, on the line through it.
        var port = (X: Here.X + (dir.X * 60f), Z: Here.Z + (dir.Z * 60f));
        var gates = new[] { (X: 0f, Z: 1f), (X: 1f, Z: 0f), (X: 0f, Z: -1f), (X: -1f, Z: 0f) };
        var yaw = Enumerable.Range(0, 4).MaxBy(q => -((gates[q].X * dir.X) + (gates[q].Z * dir.Z)));
        var gate = (X: port.X + (gates[yaw].X * 20f), Z: port.Z + (gates[yaw].Z * 20f));
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content(), homeRegion: home);
        var sim = new Sim
        {
            X = gate.X - (dir.X * 15f),
            Z = gate.Z - (dir.Z * 15f),
            Planet = [new SeenStructure(port.X, port.Z, Routing.ShuttleportKind, WalledPortAsset, yaw, 1)],
            Walls = [.. RingWalls().Select(r => CityPath.Turn(yaw, new PlanBox(r.MinX, r.MinZ, r.MaxX, r.MaxZ, 0f, 2f, false)))
                .Select(b => (b.MinX + port.X, b.MinZ + port.Z, b.MaxX + port.X, b.MaxZ + port.Z))],
        };

        var past = false;
        for (var tick = 1L; tick < 600 && !past; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
            Assert.That(Routing.Distance(sim.X, sim.Z, port.X, port.Z), Is.GreaterThan(19.5d),
                $"tick {tick}: it walked into the port's plaza, at ({sim.X - port.X:F1}, {sim.Z - port.Z:F1}) from it");
            past = ((sim.X - port.X) * dir.X) + ((sim.Z - port.Z) * dir.Z) > 40f;
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Travelling), "the premise: it is on its way home");
            Assert.That(past, Is.True, $"it never got past the port; it is at ({sim.X - port.X:F1}, {sim.Z - port.Z:F1}) from it");
        });
    }

    /// <summary>
    /// A long errand whose straight line runs in through a shuttleport's gate is walked a searched stretch at a time, round the port, and gets there.
    /// </summary>
    /// <remarks>
    /// The errand bots trapped in the ports after the first fix (live, 2026-10-26): a walk past 200 m was searched out of its street and into its goal's,
    /// and walked straight between — across the city, in through the gate.
    /// </remarks>
    [Test]
    public void ALongErrand_GoesRoundAShuttleport_NotInByItsGate()
    {
        var port = (X: Here.X + 300f, Z: Here.Z);
        var terminal = (X: Here.X + 600f, Z: Here.Z);
        const int yaw = 3;   // the gate faces west, toward the bot
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, terminal.X, terminal.Z, TerminalAsset + 1, 0),
            () => new Sim
            {
                Planet = [new SeenStructure(port.X, port.Z, Routing.ShuttleportKind, WalledPortAsset, yaw, 1),
                    new SeenStructure(terminal.X, terminal.Z, 10, TerminalAsset, 0, 1)],
                Walls = [.. RingWalls().Select(r => CityPath.Turn(yaw, new PlanBox(r.MinX, r.MinZ, r.MaxX, r.MaxZ, 0f, 2f, false)))
                    .Select(b => (b.MinX + port.X, b.MinZ + port.Z, b.MaxX + port.X, b.MaxZ + port.Z))],
            });

        for (var tick = 10L; tick < 3000 && brain.FacilitiesUsed == 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
            Assert.That(Routing.Distance(sim.X, sim.Z, port.X, port.Z), Is.GreaterThan(19.5d),
                $"tick {tick}: the errand walked into the port's plaza, at ({sim.X - port.X:F1}, {sim.Z - port.Z:F1}) from it");
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.FacilitiesUsed, Is.EqualTo(1), $"the errand never reached the terminal; the bot is at ({sim.X - Here.X:F1}, {sim.Z - Here.Z:F1})");
            Assert.That(brain.PortsLeft, Is.Zero, "it had to be walked out of the port");
        });
    }

    /// <summary>
    /// A port encloses its plaza up to its ring's walls — against the wall on an axis too, which a radius small enough for the diagonals missed — and
    /// nothing beyond the ring, even on its floor; a house encloses nothing.
    /// </summary>
    [Test]
    public void APortEncloses_ItsPlazaUpToTheWall_NotBeyondIt()
    {
        var port = WalledPort();
        Assert.Multiple(() =>
        {
            Assert.That(port.HasEnclosure, Is.True);
            Assert.That(port.Encloses(0f, -16f), Is.True, "mid-plaza");
            Assert.That(port.Encloses(18.9f, 0f), Is.True, "against the wall on an axis: the ring's block starts at 19.1 m");
            Assert.That(port.Encloses(13f, 13f), Is.True, "on a diagonal, short of its blocks' corners at 18.7 m");
            Assert.That(port.Encloses(15f, 15f), Is.False, "21.2 m on a diagonal: past the ring, on the floor's rim");
            Assert.That(port.Encloses(21f, 0f), Is.False, "outside the wall on an axis");
            Assert.That(port.Encloses(0f, 19.5f), Is.False, "in the gate: the origin is in sight through it, and so is the way out");
            Assert.That(port.Encloses(1f, 21f), Is.False, "across the gate's mouth, on the way to the terminals beside it");
            Assert.That(port.Encloses(0f, 15f), Is.False, "in line with the gate, inside: straight on out is through it");
            Assert.That(port.Encloses(0f, 26f), Is.False, "where the approach starts, outside");
            Assert.That(new AssetPlan { Id = "house", Walls = LargeHouseWalls }.HasEnclosure, Is.False, "a house rings nothing in");
            Assert.That(new AssetPlan { Id = "bare" }.HasEnclosure, Is.False);
        });
    }

    /// <summary>B-1: at a mission terminal, holding none, the errand ends in a take; holding one, it only looks.</summary>
    [TestCase(0, 1)]
    [TestCase(1, 0)]
    public void AtAMissionTerminal_ItTakesOneWhenItHoldsNone(int held, int takes)
    {
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0),
            () => new Sim { MissionsHeld = held, Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 0, 1)] });

        for (var tick = 10L; tick < 200 && brain.FacilitiesUsed == 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.FacilitiesUsed, Is.EqualTo(1), "the premise: the errand reached the terminal");
            Assert.That(sim.Sent.FindAll(i => i.Kind == IntentKind.TakeMission), Has.Count.EqualTo(takes));
            Assert.That(brain.MissionsAsked, Is.EqualTo(takes));
        });
    }

}

/// <summary>The pure pieces under the errands: the city walk, the room walk, and reading a descriptor.</summary>
[TestFixture]
public sealed class BotPathChecks
{
    private static BotAssets OneWall(PlanBox box) => new([new AssetPlan { Id = "wall", Walls = [box] }]);

    /// <summary>A wall across the way is walked round, every leg of the walk is clear, and the walk ends on the goal.</summary>
    [Test]
    public void AWallIsWalkedRound()
    {
        var assets = OneWall(new PlanBox(-20f, 10f, 20f, 11f, 0f, 2f, false));
        var structures = new List<SeenStructure> { new(0f, 0f, 0, 0, 0, 1) };
        var grid = CityPath.Walls(structures, assets, -40f, -10f, 40f, 30f);
        var path = CityPath.Find(grid, 0f, 0f, 0f, 20f);
        Assert.That(path, Is.Not.Null);
        Assert.That(path[^1], Is.EqualTo((0f, 20f)));
        var (ax, az) = (0f, 0f);
        foreach (var (bx, bz) in path)
        {
            Assert.That(CityPath.LineClear(grid, ax, az, bx, bz), Is.True, "a leg of the walk crosses the wall");
            (ax, az) = (bx, bz);
        }

        Assert.That(path.Exists(p => MathF.Abs(p.X) > 20f), Is.True, "the walk did not go round the wall's end");
    }

    /// <summary>
    /// A walker standing deep inside a solid block — further in than the usual snap — still gets a walk out of it, from <see cref="CityPath.Route"/>'s wide
    /// second try.
    /// </summary>
    /// <remarks>
    /// The crowd's clones used to wake on the middle of a shuttleport's pad, ~10 m into a collider the search could not snap out of: 29 of 123 of its
    /// searches failed, and each failure walked a body straight into the ring wall.
    /// </remarks>
    [Test]
    public void AWalkerDeepInsideABlockStillGetsAWayOut()
    {
        var assets = OneWall(new PlanBox(-15f, -15f, 15f, 15f, 0f, 3f, false));
        var structures = new List<SeenStructure> { new(0f, 0f, 0, 0, 0, 1) };
        var grid = CityPath.Walls(structures, assets, -60f, -60f, 60f, 60f);
        Assert.That(CityPath.Find(grid, 0f, 0f, 40f, 0f), Is.Null, "precondition: the usual snap cannot reach out of the block");

        var path = CityPath.Route(structures, assets, (0f, 0f), (40f, 0f), 120_000);
        Assert.That(path, Is.Not.Null, "the wide second try found no way out");
        Assert.That(path[^1], Is.EqualTo((40f, 0f)));
    }

    /// <summary>From a body pressed against a house, the walk first steps out of the wall's margin, and every leg after that is clear.</summary>
    /// <remarks>Pulled straight from where the body stood, the first leg crossed the house's corner: the start cell is walled off by the body's own width.</remarks>
    [Test]
    public void FromAgainstAWallTheWalkStepsOutFirst()
    {
        var assets = OneWall(new PlanBox(-3f, -3f, 3f, 3f, 0f, 2.7f, false));
        var structures = new List<SeenStructure> { new(0f, 0f, 0, 0, 0, 1) };
        var grid = CityPath.Walls(structures, assets, -60f, -40f, 40f, 40f);
        var path = CityPath.Find(grid, 3.3f, -2.7f, -40f, -10f);
        Assert.That(path, Is.Not.Null);
        Assert.That(Routing.Distance(3.3f, -2.7f, path[0].X, path[0].Z), Is.LessThan(1.5d), "the walk did not begin by stepping out of the wall's margin");
        for (var i = 1; i < path.Count; i++)
        {
            Assert.That(CityPath.LineClear(grid, path[i - 1].X, path[i - 1].Z, path[i].X, path[i].Z), Is.True, $"leg {i} of the walk crosses the house");
        }
    }

    /// <summary>A round collider walls off its disc, not its bounding square, and a tall leaning strut is a wall where a ramp is not.</summary>
    /// <remarks>
    /// The third live run: a bot inside a shuttleport never got out, because the landing pad's 24 m square closed the corridor between the pad and the
    /// ring wall that the server's cylinder leaves open.
    /// </remarks>
    [Test]
    public void ARoundPadIsADiscAndALeaningStrutIsAWall()
    {
        var assets = new BotAssets(
        [
            new AssetPlan
            {
                Id = "port",
                Walls =
                [
                    new PlanBox(-12f, -12f, 12f, 12f, 0.3f, 1.26f, false, 12f),
                    new PlanBox(19.5f, -0.5f, 21.3f, 0.5f, -0.08f, 2.3f, true),
                ],
            },
        ]);
        var grid = CityPath.Walls([new SeenStructure(0f, 0f, 2, 0, 0, 1)], assets, -30f, -30f, 30f, 30f);
        Assert.Multiple(() =>
        {
            Assert.That(CityPath.LineClear(grid, 10.5f, 10.5f, 11.5f, 9.5f), Is.True, "the pad's corner, outside its circle, was walled off");
            Assert.That(CityPath.LineClear(grid, 0f, -20f, 0f, 20f), Is.False, "the pad's disc was not a wall");
            Assert.That(CityPath.LineClear(grid, 20.4f, -4f, 20.4f, 4f), Is.False, "a 2.3 m leaning strut was walked over like a ramp");
        });
    }

    /// <summary>The search's reused buffers give the answer a fresh search gives, whatever searched before it on the thread.</summary>
    [Test]
    public void ARepeatedSearchFindsTheSamePath()
    {
        var wall = OneWall(new PlanBox(-20f, 10f, 20f, 11f, 0f, 2f, false));
        var open = new BotAssets([new AssetPlan { Id = "nothing" }]);
        var structures = new List<SeenStructure> { new(0f, 0f, 0, 0, 0, 1) };
        var walled = CityPath.Walls(structures, wall, -40f, -10f, 40f, 30f);
        var first = CityPath.Find(walled, 0f, 0f, 0f, 20f);

        // A larger grid, a different answer, in between.
        var bare = CityPath.Walls(structures, open, -80f, -80f, 80f, 80f);
        Assert.That(CityPath.Find(bare, -70f, -70f, 70f, 70f), Has.Count.EqualTo(1));

        var again = CityPath.Find(CityPath.Walls(structures, wall, -40f, -10f, 40f, 30f), 0f, 0f, 0f, 20f);
        Assert.That(again, Is.EqualTo(first));
    }

    /// <summary>A walk to a walled goal ends on the side it comes from, not on the first open cell in scan order.</summary>
    [Test]
    public void AWalledGoalIsReachedFromItsNearSide()
    {
        var kiosk = OneWall(new PlanBox(-1f, -1f, 1f, 1f, 0f, 1.6f, false));
        var grid = CityPath.Walls([new SeenStructure(0f, 0f, 0, 0, 0, 1)], kiosk, -20f, -20f, 20f, 20f);
        var path = CityPath.Find(grid, -15f, 0f, 0f, 0f);
        Assert.That(path, Is.Not.Null);
        Assert.That(path[^1].X, Is.LessThan(-1f), "a walk from the west ended on the kiosk's far side");
    }

    /// <summary>A walled-off goal gives up within its expansion budget instead of costing the whole grid.</summary>
    [Test]
    public void AWalledOffGoalGivesUpWithinItsBudget()
    {
        var pen = new BotAssets([new AssetPlan { Id = "pen", Walls =
        [
            new PlanBox(-6f, -6f, -5f, 6f, 0f, 2f, false), new PlanBox(5f, -6f, 6f, 6f, 0f, 2f, false),
            new PlanBox(-6f, -6f, 6f, -5f, 0f, 2f, false), new PlanBox(-6f, 5f, 6f, 6f, 0f, 2f, false),
        ] }]);
        var grid = CityPath.Walls([new SeenStructure(0f, 0f, 0, 0, 0, 1)], pen, -100f, -100f, 100f, 100f);
        Assert.That(CityPath.Find(grid, -90f, -90f, 0f, 0f, maxExpansions: 1000), Is.Null);
    }

    /// <summary>A ramp, a step and an overhead beam are walked: only what a body cannot step onto is a wall.</summary>
    [Test]
    public void RampsStepsAndBeamsAreNotWalls()
    {
        var assets = new BotAssets(
        [
            new AssetPlan
            {
                Id = "porch",
                Walls =
                [
                    new PlanBox(-1f, -1f, 1f, 1f, 0f, 0.3f, false),
                    new PlanBox(4f, -1f, 6f, 1f, 0f, 1.2f, true),
                    new PlanBox(9f, -1f, 11f, 1f, 2.2f, 2.5f, false),
                ],
            },
        ]);
        var grid = CityPath.Walls([new SeenStructure(0f, 0f, 0, 0, 0, 1)], assets, -5f, -5f, 15f, 5f);
        Assert.Multiple(() =>
        {
            Assert.That(CityPath.LineClear(grid, 0f, -4f, 0f, 4f), Is.True, "a step was a wall");
            Assert.That(CityPath.LineClear(grid, 5f, -4f, 5f, 4f), Is.True, "a ramp was a wall");
            Assert.That(CityPath.LineClear(grid, 10f, -4f, 10f, 4f), Is.True, "an overhead beam was a wall");
        });
    }

    /// <summary>A turned structure's walls are where its published points say it stands: the box turn and the point placement agree.</summary>
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void TurnedWallsAndPlacedPointsAgree(int quarters)
    {
        var box = new PlanBox(1f, 2f, 3f, 5f, 0f, 2f, false);
        var turned = CityPath.Turn(quarters, box);
        foreach (var corner in new[] { (1f, 2f), (3f, 5f), (1f, 5f), (3f, 2f) })
        {
            var (x, z) = BotAssets.Place(0f, 0f, quarters, corner);
            Assert.That(x, Is.InRange(turned.MinX - 1e-4f, turned.MaxX + 1e-4f), $"a corner turned {quarters} quarters left its box in X");
            Assert.That(z, Is.InRange(turned.MinZ - 1e-4f, turned.MaxZ + 1e-4f), $"a corner turned {quarters} quarters left its box in Z");
        }
    }

    /// <summary>The room's walk: to the start cell's node, along the graph, to the point — read through the X mirror.</summary>
    [Test]
    public void ARoomIsCrossedOnItsGraph()
    {
        // The published tat_house_small_interior's shape, in miniature: nodes in the descriptor's frame, read mirrored.
        const string Json = """
            { "nodes": [[2.0, 0, 0], [0, 0, 0], [0, 0, 2.0]],
              "edges": [[0, 1], [1, 2]],
              "cells": { "origin": [-1.0, -1.0], "size": 1.0, "cols": 4, "rows": 4,
                         "node": [1, 1, 0, 0,   1, 1, 0, 0,   2, 2, 1, 0,   2, 2, 2, -1] } }
            """;
        using var doc = JsonDocument.Parse(Json);
        var nav = RoomNav.Read(doc.RootElement);
        Assert.That(nav.Nodes[0], Is.EqualTo((-2f, 0f)), "a node was not mirrored");

        // From beside node 0 (mirrored to -2, 0) to the top: through node 1, to node 2, then the point.
        var route = nav.Route(-2.5f, 0.2f, -0.5f, 2.6f);
        Assert.That(route, Is.EqualTo(new List<(float, float)> { (-2f, 0f), (0f, 0f), (0f, 2f), (-0.5f, 2.6f) }));
    }

    /// <summary>A descriptor is read as the server and the browser read it: the first portal's approach and target, mirrored; a child's stand path.</summary>
    [Test]
    public void ADescriptorIsReadMirrored()
    {
        const string House = """
            { "portals": [{ "approach": [[-2.0, 0, 9.4], [-2.0, 0.77, 4.07]], "target": "tat_house_small_interior" }],
              "interactions": [], "children": [] }
            """;
        const string Port = """
            { "portals": [], "interactions": [],
              "children": [{ "asset": "ticket_terminal", "pos": [2.1434, 0.3, 20.4714], "approach": [[2.1434, 0, 25.2], [2.1434, 0.2, 21.3714]] }] }
            """;
        const string Ticket = """{ "interactions": [{ "kind": "shuttle_ticket", "pos": [0, 0, 0.9] }] }""";
        using var house = JsonDocument.Parse(House);
        using var port = JsonDocument.Parse(Port);
        using var ticket = JsonDocument.Parse(Ticket);

        var h = BotAssets.Plan("tat_house_small", [], house.RootElement, null);
        var p = BotAssets.Plan("tat_shuttleport", [], port.RootElement, ticket.RootElement);
        Assert.Multiple(() =>
        {
            Assert.That(h.Door.Target, Is.EqualTo("tat_house_small_interior"));
            Assert.That(h.Door.Approach, Is.EqualTo(new[] { (2f, 9.4f), (2f, 4.07f) }), "the door's path was not mirrored");
            Assert.That(p.UsePath.Length, Is.EqualTo(2), "a path that already ends on the stand point gained a point");
            Assert.That(p.UsePath[^1].X, Is.EqualTo(-2.1434f).Within(1e-4f));
            Assert.That(p.UsePath[^1].Z, Is.EqualTo(21.3714f).Within(1e-4f), "the shuttleport's use path does not end on the ticket terminal's stand point");
        });
    }
}
