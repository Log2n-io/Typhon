using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SwgTatooine.Bots;
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

        /// <summary>Missions held, as the journal would say, and the lair of the one held, when it has one.</summary>
        public int MissionsHeld;

        public (float X, float Z)? Lair;

        /// <summary>Stacks the inventory holds, and bazaar goods reserved for the bot, as its reads would say.</summary>
        public int Stacks;

        public int Reserved;

        /// <summary>Whether the server holds the body where it is, whatever it is asked: a crowd it cannot push through.</summary>
        public bool Held;

        /// <summary>How many asks to get on the swoop the server refuses before it grants one: a body downed, or queued for a shuttle.</summary>
        public int RefuseMounts;

        /// <summary>A creature in view, which never moves.</summary>
        public (uint Id, float X, float Z)? Creature;

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
            HasMissionLair = Lair.HasValue,
            MissionLairX = Lair?.X ?? 0f,
            MissionLairZ = Lair?.Z ?? 0f,
            InventoryStacks = Stacks,
            Reserved = Reserved,
            NearestCreature = Creature?.Id ?? 0u,
            NearestCreatureX = Creature?.X ?? 0f,
            NearestCreatureZ = Creature?.Z ?? 0f,
            NearestCreatureM = Creature is { } c ? (float)Routing.Distance(X, Z, c.X, c.Z) : 0f,
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
            switch (Held && intent.Kind is IntentKind.Move or IntentKind.MoveTo ? IntentKind.None : intent.Kind)
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
                case IntentKind.Mount when intent.On && RefuseMounts > 0:
                    RefuseMounts--;
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

    /// <summary>
    /// The swoop's class is never asked for before the server says the body is mounted: a refused mount runs instead, and is asked for again only now
    /// and then — three quick asks, then one every <see cref="BotBrain.MountRetrySeconds"/> — never for good, and never without end.
    /// </summary>
    [Test]
    public void ARefusedMountRuns_AndIsAskedAgainOnlyNowAndThen()
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

        // Fifty seconds: three quick asks, then one each ten seconds.
        Assert.That(mounts, Is.InRange(5, 3 + (int)(200 / 4 / BotBrain.MountRetrySeconds) + 1), "a refused mount was given up on, or asked for without end");
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
        // From the detour's first step: a fresh brain's first tick gets off whatever it sits on, which is not the detour's doing. In a city the way
        // round is run (BM-2), so its first step is a heading.
        var detour = sim.Sent.FindIndex(i => i.Kind == IntentKind.MoveTo || (i.Kind == IntentKind.Move && i.SpeedClass != 0));
        Assert.That(brain.PathsPlanned, Is.GreaterThan(0), "the premise: the rider searched a way round");
        Assert.That(detour, Is.GreaterThanOrEqualTo(0), "the premise: it moved");
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

    /// <summary>
    /// BM-2: a city walk round a house is RUN — <c>MoveDir</c> at the run, every decision, from its first point to its last open one — and every corner
    /// is run past, never stood on; <c>MoveTo</c> only for the exact point at the end. No step crosses the house.
    /// </summary>
    /// <remarks>
    /// Loïc, 2026-10-26: bots in town stopped and started at every corner. Each corner was a <c>MoveTo</c> — "go there and stop" — called reached from
    /// 0.75 m, and at 2.5 m of running per decision the server stopped the body on it before the next one was sent.
    /// </remarks>
    [Test]
    public void ACityWalk_IsRun_AndItsCornersRunPast()
    {
        // The house between the bot and the terminal: the walk goes round two of its corners.
        var terminal = (X: Here.X + 2f, Z: Here.Z + 18f);
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, terminal.X, terminal.Z, TerminalAsset + 1, 0),
            () => new Sim
            {
                X = Here.X - 1f,
                Z = Here.Z - 14f,
                Planet = [new SeenStructure(Here.X, Here.Z, 0, HouseAsset, 0, 1), new SeenStructure(terminal.X, terminal.Z, 10, TerminalAsset, 0, 1)],
                Forbidden = (Here.X - 3f, Here.Z - 3f, Here.X + 3f, Here.Z + 3f),
            });

        var (next, count, precise, point) = Leg(brain);
        Assert.That(precise, Is.GreaterThanOrEqualTo(2), $"the premise: a walk with corners, not {brain.LegState}");
        var open = 0;
        var passed = 0;
        for (var tick = 10L; tick < 200 && brain.FacilitiesUsed == 0; tick++)
        {
            var at = (sim.X, sim.Z);
            var intent = brain.Decide(sim.View(), tick);
            var leg = Leg(brain);
            if (leg.Next < leg.Precise && leg.Count == count)
            {
                open++;
                Assert.That((intent.Kind, intent.SpeedClass), Is.EqualTo((IntentKind.Move, Routing.SpeedClassOf(TravelMode.Run))),
                    $"tick {tick}: an open point was walked to and stopped on, or not moved toward ({brain.LegState})");
            }

            // A corner passed while running: the body was not put on it, which is what a MoveTo — stop there — does.
            if (leg.Count == count && leg.Next > next && next < precise)
            {
                passed += Routing.Distance(at.X, at.Z, point.X, point.Z) > 0.05d ? leg.Next - next : 0;
            }

            (next, count, precise, point) = leg;
            sim.Apply(intent);
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.FacilitiesUsed, Is.EqualTo(1), "the walk never reached the terminal");
            Assert.That(open, Is.GreaterThan(4), "the premise: the open part was walked over several decisions");
            Assert.That(passed, Is.EqualTo(precise), "not every open point was run past");
            Assert.That(sim.Sent.FindAll(i => i.Kind == IntentKind.MoveTo), Has.Count.LessThanOrEqualTo(2), "MoveTo for more than the exact point");
        });
    }

    /// <summary>
    /// BM-2: a walker shoved off its walk past a corner runs on to the point now in sight, not back to the corner: the point after the next is checked
    /// for sight — a straight step clear of the walls grown by a body and a margin — and the next is skipped when it is.
    /// </summary>
    [Test]
    public void AShovedWalker_RunsOnToThePointInSight_NotBackToTheCorner()
    {
        var terminal = (X: Here.X + 2f, Z: Here.Z + 18f);
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, terminal.X, terminal.Z, TerminalAsset + 1, 0),
            () => new Sim
            {
                X = Here.X - 1f,
                Z = Here.Z - 14f,
                Planet = [new SeenStructure(Here.X, Here.Z, 0, HouseAsset, 0, 1), new SeenStructure(terminal.X, terminal.Z, 10, TerminalAsset, 0, 1)],
            });

        var (next, _, precise, corner) = Leg(brain);
        Assert.That((next, precise, corner.Z < Here.Z), Is.EqualTo((0, 3, true)), $"the premise: heading for the house's south-east corner ({brain.LegState})");

        // Shoved by the crowd to the house's east side, north of the corner it was heading for: the next point is in plain sight from there.
        (sim.X, sim.Z) = (Here.X + 8f, Here.Z + 6f);
        var step = brain.Decide(sim.View(), 20);
        Assert.Multiple(() =>
        {
            Assert.That(Leg(brain).Next, Is.GreaterThan(0), $"it still heads for the corner behind it ({brain.LegState})");
            Assert.That((step.Kind, MathF.Sin(step.Heading) > 0f), Is.EqualTo((IntentKind.Move, true)), "it did not run on north");
        });
    }

    /// <summary>The walk in progress as the brain's trace line states it: the next point's index, the count, where the exact part starts, the point.</summary>
    private static (int Next, int Count, int Precise, (float X, float Z) Point) Leg(BotBrain brain)
    {
        // "leg {next}/{count} exact {precise} ride … next (x, z)"
        var state = brain.LegState;
        var parts = state.Split(' ');
        var nc = parts[1].Split('/');
        var point = (0f, 0f);
        var at = state.IndexOf(" next (", StringComparison.Ordinal);
        if (at >= 0)
        {
            var xz = state[(at + 7)..].TrimEnd(')').Split(", ");
            point = (float.Parse(xz[0], CultureInfo.InvariantCulture), float.Parse(xz[1], CultureInfo.InvariantCulture));
        }

        return (
            int.Parse(nc[0], CultureInfo.InvariantCulture),
            int.Parse(nc[1], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture),
            point);
    }

    /// <summary>
    /// BM-3: a mission's ride out of town is WALKED to the city's edge — no mount, no swoop inside its disc — then ridden from the edge on, and every step
    /// inside is a run.
    /// </summary>
    /// <remarks>Loïc, 2026-10-26: bots leaving for a lair rode their walk out at the swoop's speed and clipped the houses' corners.</remarks>
    [Test]
    public void AMissionsRideOutOfTown_IsWalkedToTheEdge_ThenRidden()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content());
        var lair = (X: Here.X + Here.Radius + 1200f, Z: Here.Z + 40f);
        var sim = new Sim
        {
            X = Here.X - 12f,
            Z = Here.Z,
            MissionsHeld = 1,
            Lair = lair,
            Planet =
            [
                new SeenStructure(Here.X, Here.Z, 0, HouseAsset, 0, 1),
                new SeenStructure(Here.X + 60f, Here.Z + 3f, 0, HouseAsset, 1, 1),

                // Straddling the disc's edge on the way out, and another in the margin past it: buildings stand round a city, not only in it.
                new SeenStructure(Here.X + Here.Radius, Here.Z + 2f, 0, HouseAsset, 0, 1),
                new SeenStructure(Here.X + Here.Radius + 30f, Here.Z + 6f, 0, HouseAsset, 1, 1),
            ],
            Forbidden = (Here.X - 3f, Here.Z - 3f, Here.X + 3f, Here.Z + 3f),
        };

        double? mountedAt = null;
        var mountsAfter = 0;
        var swoopOutside = 0;
        for (var tick = 1L; tick < 800 && Routing.Distance(sim.X, sim.Z, lair.X, lair.Z) > BotBrain.NoticeM; tick++)
        {
            var fromCentre = Routing.Distance(sim.X, sim.Z, Here.X, Here.Z);
            var intent = brain.Decide(sim.View(), tick);
            if (fromCentre <= Here.Radius)
            {
                Assert.That(intent, Is.Not.EqualTo(Intent.Mount(true)), $"tick {tick}: it got on its swoop in town, {fromCentre:F0} m from the centre");
                Assert.That(intent.Kind == IntentKind.Move && intent.SpeedClass == Routing.SwoopClass, Is.False, $"tick {tick}: it rode in town");
                Assert.That(intent.Kind, Is.Not.EqualTo(IntentKind.MoveTo), $"tick {tick}: the walk out stopped on a point ({brain.LegState})");
            }
            else
            {
                // Out of town the ride is straight: a walk's corners ridden at the swoop's class and walked onto at the run's was the speed jerking.
                Assert.That(intent.Kind, Is.Not.EqualTo(IntentKind.MoveTo), $"tick {tick}: out of town, it rode the walk's points ({brain.LegState})");
                mountsAfter += mountedAt != null && intent.Kind == IntentKind.Mount ? 1 : 0;
                mountedAt ??= intent == Intent.Mount(true) ? fromCentre : null;
                swoopOutside += intent.Kind == IntentKind.Move && intent.SpeedClass == Routing.SwoopClass ? 1 : 0;
            }

            sim.Apply(intent);
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Questing), "the premise: the mission is what it is doing");
            Assert.That(brain.PathsPlanned, Is.GreaterThan(0), "the premise: the town was walked on a searched path");
            Assert.That(mountedAt, Is.Not.Null.And.InRange(Here.Radius + CityPath.CityMarginM, Here.Radius + CityPath.CityLeaveM + 15d),
                "it did not get on its swoop past the buildings round the city's edge");
            Assert.That(mountsAfter, Is.Zero, "on its swoop, it asked to get off or on again: the edge flapped");
            Assert.That(brain.Counters.Legs[(int)LegWhy.CityEdge], Is.LessThanOrEqualTo(1), "the walk out was dropped, searched and dropped again");
            Assert.That(swoopOutside, Is.GreaterThan(10), "the desert was not ridden");
            Assert.That(Routing.Distance(sim.X, sim.Z, lair.X, lair.Z), Is.LessThanOrEqualTo(BotBrain.NoticeM), "it never reached the lair");
        });
    }

    /// <summary>
    /// BM-4: a walk that stops making progress — a crowd, a wall the search did not know — is searched again from where the body stands within a second
    /// and a half, not four.
    /// </summary>
    /// <remarks>Loïc, 2026-10-26: bots leaving town pushed into a house for four seconds, sliding along it, before looking for a way round.</remarks>
    [Test]
    public void AWalkThatStopsMoving_IsSearchedAgainWithinASecondAndAHalf()
    {
        var terminal = (X: Here.X + 120f, Z: Here.Z);
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, terminal.X, terminal.Z, TerminalAsset + 1, 0),
            () => new Sim
            {
                Planet =
                [
                    new SeenStructure(Here.X + 40f, Here.Z + 1f, 0, HouseAsset, 0, 1),
                    new SeenStructure(terminal.X, terminal.Z, 10, TerminalAsset, 0, 1),
                ],
            });

        var tick = 10L;
        for (; tick < 18; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        sim.Held = true;
        var heldAt = tick;
        var before = brain.PathsPlanned;
        for (; tick < heldAt + 40 && brain.PathsPlanned == before; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        // The tick the search ran on is the one the loop stopped after: held for that many decisions, at four a second.
        var heldSeconds = (tick - heldAt) / 4d;

        // Still held: the second search waits longer — a wedged body searching every 1.25 s multiplied the searches (review 6, M3).
        var firstAt = tick;
        var after = brain.PathsPlanned;
        for (; tick < firstAt + 60 && brain.PathsPlanned == after; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        var nextSeconds = (tick - firstAt) / 4d;
        Assert.Multiple(() =>
        {
            Assert.That(brain.PathsPlanned, Is.GreaterThan(before), "a walk held still was never searched again");
            Assert.That(heldSeconds, Is.LessThanOrEqualTo(1.5d + 0.25d), "searched again only after more than a second and a half held still");
            Assert.That(nextSeconds, Is.GreaterThanOrEqualTo(BotBrain.StuckSeconds - 0.25d), "the second search came as quickly as the first");
        });
    }

    /// <summary>
    /// A walk planned from against a wall — the body in the wall's margin, so the search starts from the nearest open cell, which can lie behind it —
    /// never begins by stepping back to that cell: the first step heads for the terminal, and the errand gets there.
    /// </summary>
    /// <remarks>
    /// Live telemetry (2026-10-27): a bot walking to a terminal sent waypoints flipping by 180° in a row, and every re-plan from where it stood began with
    /// the same step back.
    /// </remarks>
    [Test]
    public void AWalkFromAgainstAWall_DoesNotStepBackToItsFirstPoint()
    {
        var behind = 0;
        foreach (var east in new[] { 1f, -1f })
        {
            // Pressed against the house's north face, inside its walls' margin; the terminal off to one side, beyond its end.
            var body = (X: Here.X + (0.2f * east), Z: Here.Z + 3.25f);
            var terminal = (X: Here.X + (25f * east), Z: Here.Z + 9f);
            List<SeenStructure> planet =
            [
                new SeenStructure(Here.X, Here.Z, 0, HouseAsset, 0, 1),
                new SeenStructure(terminal.X, terminal.Z, 10, TerminalAsset, 0, 1),
            ];
            var first = CityPath.Route(planet, Content(), body, (terminal.X, terminal.Z + 0.65f), BotBrain.MaxExpansions)[0];
            behind += ((first.X - body.X) * (terminal.X - body.X)) + ((first.Z - body.Z) * (terminal.Z - body.Z)) < 0f ? 1 : 0;

            var chosen = false;
            for (var seed = 1u; seed < 400u && !chosen; seed++)
            {
                var brain = new BotBrain(BotMap.Cities, seed, 4d, Content());
                brain.Facilities.Observe(1, Routing.MissionTerminalKind, terminal.X, terminal.Z, TerminalAsset + 1, 0);
                var sim = new Sim { X = body.X, Z = body.Z, Planet = planet };
                var step = brain.Decide(sim.View(), 1);
                if (brain.Goal != Goal.Visiting)
                {
                    continue;
                }

                chosen = true;
                var (dx, dz) = step.Kind switch
                {
                    IntentKind.Move => (MathF.Cos(step.Heading), MathF.Sin(step.Heading)),
                    IntentKind.MoveTo => (step.X - body.X, step.Z - body.Z),
                    _ => (0f, 0f),
                };
                Assert.That((dx * (terminal.X - body.X)) + (dz * (terminal.Z - body.Z)), Is.GreaterThan(0f),
                    $"side {east}: the walk began with a step back ({step.Kind} to ({step.X - Here.X:F2}, {step.Z - Here.Z:F2}), {brain.LegState})");

                sim.Apply(step);
                for (var tick = 2L; tick < 200 && brain.FacilitiesUsed == 0; tick++)
                {
                    sim.Apply(brain.Decide(sim.View(), tick));
                }

                Assert.That(brain.FacilitiesUsed, Is.EqualTo(1), $"side {east}: the errand never reached the terminal");
            }

            Assert.That(chosen, Is.True, "no seed under 400 chose the errand at its first decision");
        }

        Assert.That(behind, Is.GreaterThan(0), "the premise: on neither side did the search start from a cell behind the body");
    }

    /// <summary>
    /// A terminal ringed by a crowd — the body held where the ring starts, short of its stand point for good — is used from there when that is inside the
    /// server's reach for a take, and the take is asked for; held beyond that reach, it is not.
    /// </summary>
    /// <remarks>Live telemetry (2026-10-27): walks stuck "0 m to go" at a terminal, and a third of all errands failed.</remarks>
    [TestCase(3f, 1)]
    [TestCase(5.5f, 0)]
    public void ACrowdedTerminal_IsUsedFromWithinTheServersReach(float ring, int used)
    {
        var (brain, sim) = Choosing(
            Goal.Visiting,
            b => b.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0),
            () => new Sim { Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 0, 1)] });

        // This errand only: a second one would start from where the first left the body, and walk a different way.
        for (var tick = 10L; tick < 80 && brain.FacilitiesUsed == 0 && brain.Goal == Goal.Visiting; tick++)
        {
            sim.Held |= Routing.Distance(sim.X, sim.Z, Here.X + 40f, Here.Z) <= ring;
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.Multiple(() =>
        {
            Assert.That(sim.Held, Is.True, "the premise: the crowd held the body");
            Assert.That(brain.FacilitiesUsed, Is.EqualTo(used), $"held at ({sim.X - Here.X - 40f:F1}, {sim.Z - Here.Z:F1}) from the terminal");
            Assert.That(brain.Goal, Is.EqualTo(used == 1 ? Goal.Visiting : Goal.Idling), "the errand did not end as it should");
            Assert.That(sim.Sent.FindAll(i => i.Kind == IntentKind.TakeMission), Has.Count.EqualTo(used));
        });
    }

    /// <summary>
    /// The day's loop: a bot holding no mission, in a city whose nearest terminal lists one it can take, goes to that terminal — before the creature in
    /// view — takes the offer, and sets out on the quest. It never travels, and never hunts first.
    /// </summary>
    [Test]
    public void AMissionlessBot_GoesToTheTerminal_Takes_AndQuests_WithoutTravelling()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content());
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X - 200f, Here.Z, TerminalAsset + 1, 0);
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0);
        var sim = new Sim
        {
            Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 0, 1)],
            Creature = (42u, Here.X - 30f, Here.Z + 30f),
        };

        var goals = new List<Goal>();
        for (var tick = 1L; tick < 400 && brain.Goal != Goal.Questing; tick++)
        {
            var intent = brain.Decide(sim.View(), tick);
            if (goals.Count == 0 || goals[^1] != brain.Goal)
            {
                goals.Add(brain.Goal);
            }

            sim.Apply(intent);
            if (intent.Kind == IntentKind.TakeMission)
            {
                // The server's half: the take listed in the journal, its lair drawn a ride out of town.
                sim.MissionsHeld = 1;
                sim.Lair = (Here.X + Here.Radius + 600f, Here.Z);
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(goals, Is.EqualTo(new[] { Goal.Visiting, Goal.Questing }), "not terminal, then the quest");
            Assert.That(brain.FacilitiesUsed, Is.EqualTo(1));
            Assert.That(Routing.Distance(sim.X, sim.Z, Here.X + 40f, Here.Z), Is.LessThanOrEqualTo(BotBrain.TerminalUseFromM), "not the nearest terminal");
            Assert.That(sim.Sent.FindAll(i => i.Kind == IntentKind.TakeMission), Has.Count.EqualTo(1));
            Assert.That(brain.Counters.Why[(int)GoalWhy.DeskChosen], Is.EqualTo(1));
        });

        // A bot whose run cannot take missions skips the desk: the day starts at the mix it always had.
        var idle = new BotBrain(BotMap.Cities, 7u, 4d, Content()) { TakesMissions = false };
        idle.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0);
        var still = new Sim { Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 0, 1)] };
        for (var tick = 1L; tick < 400; tick++)
        {
            still.Apply(idle.Decide(still.View(), tick));
        }

        Assert.That(idle.Counters.Why[(int)GoalWhy.DeskChosen], Is.Zero, "a bot that cannot take a mission went to the desk for one");
    }

    /// <summary>
    /// Two of its city's terminals in a row with nothing for it — the second a different one — and a roaming bot leaves for another city, saying why;
    /// not before.
    /// </summary>
    [Test]
    public void TwoEmptyTerminalsInARow_AndARoamingBotLeavesTheCity()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content());
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0);
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X - 60f, Here.Z, TerminalAsset + 1, 0);
        var sim = new Sim
        {
            Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 0, 1), new SeenStructure(Here.X - 60f, Here.Z, 10, TerminalAsset, 0, 1)],
        };

        var usedAt = new List<(float X, float Z)>();
        for (var tick = 1L; tick < 600 && brain.Goal != Goal.Travelling; tick++)
        {
            var used = brain.FacilitiesUsed;
            sim.Apply(brain.Decide(sim.View(), tick));
            if (brain.FacilitiesUsed > used)
            {
                usedAt.Add((sim.X - Here.X, sim.Z - Here.Z));
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(usedAt, Has.Count.EqualTo(BotBrain.EmptyDesksToLeave), "it left before trying two terminals, or tried more");
            Assert.That(usedAt.Count == 2 && usedAt[0].X * usedAt[1].X < 0f, Is.True, $"not one terminal, then the other: {string.Join(" ", usedAt)}");
            Assert.That(brain.Goal, Is.EqualTo(Goal.Travelling), "two empty terminals, and it stayed");
            Assert.That(brain.Counters.Why[(int)GoalWhy.DeskEmpty], Is.EqualTo(1), "the travel does not say the desk was empty");
        });
    }

    /// <summary>The desk terminals a brain set out for, in order: read at each change into Visiting.</summary>
    private static List<(float X, float Z)> DeskVisits(BotBrain brain, Sim sim, long ticks, Func<Sim, bool> held = null, Action<long> each = null)
    {
        var targets = new List<(float X, float Z)>();
        var was = brain.Goal;
        for (var tick = 1L; tick < ticks; tick++)
        {
            sim.Held = held?.Invoke(sim) ?? false;
            sim.Apply(brain.Decide(sim.View(), tick));
            if (brain.Goal == Goal.Visiting && was != Goal.Visiting)
            {
                targets.Add((brain.VisitTarget.X - Here.X, brain.VisitTarget.Z - Here.Z));
                each?.Invoke(tick);
            }

            was = brain.Goal;
        }

        return targets;
    }

    /// <summary>
    /// A terminal the bot could not reach — ringed by a crowd further out than it can be used from — is not the next one it sets out for, and two such in
    /// one city send a roaming bot elsewhere; before, the same terminal was walked to, failed, and picked again, for good (review 6, H1).
    /// </summary>
    [Test]
    public void ATerminalNotReached_IsNotTheNextOneTried()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content());
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0);
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X - 60f, Here.Z, TerminalAsset + 1, 0);
        var sim = new Sim
        {
            Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 0, 1), new SeenStructure(Here.X - 60f, Here.Z, 10, TerminalAsset, 0, 1)],
        };

        // A crowd 5.5 m deep round every terminal: none is ever reached, nor close enough to use from.
        var targets = DeskVisits(
            brain,
            sim,
            400,
            s => Routing.Distance(s.X, s.Z, Here.X + 40f, Here.Z) <= 5.5d || Routing.Distance(s.X, s.Z, Here.X - 60f, Here.Z) <= 5.5d);
        Assert.Multiple(() =>
        {
            Assert.That(targets, Has.Count.EqualTo(BotBrain.EmptyDesksToLeave), $"terminals set out for: {string.Join(" ", targets)}");
            Assert.That(targets.Count == 2 && targets[0] != targets[1], Is.True, "the terminal it failed to reach was the next it tried");
            Assert.That(brain.Counters.Why[(int)GoalWhy.DeskEmpty], Is.EqualTo(1), "two terminals out of reach, and the roaming bot stayed");
        });
    }

    /// <summary>A crowd landed at one spot spreads over the street's terminals: each bot picks one of the nearest few, from its own stream.</summary>
    [Test]
    public void BotsLandedTogether_SpreadOverTheNearestTerminals()
    {
        var picked = new HashSet<(float X, float Z)>();
        for (var index = 0u; index < 12u; index++)
        {
            // The fleet's own seeds (BotSwarm.Bot).
            var brain = new BotBrain(BotMap.Cities, (index * 2246822519u) | 1u, 4d, Content());
            foreach (var dx in new[] { 40f, 55f, 70f })
            {
                brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + dx, Here.Z, TerminalAsset + 1, 0);
            }

            brain.Decide(new Sim().View(), 1);
            Assert.That(brain.Goal, Is.EqualTo(Goal.Visiting), "the premise: the desk first");
            picked.Add(brain.VisitTarget);
        }

        Assert.That(picked, Has.Count.GreaterThanOrEqualTo(2), "twelve bots landed together all went to one terminal");
    }

    /// <summary>
    /// A bot's desk is its city's: a terminal of another city — even a nearer one — is never set out for, so an empty one each side cannot ping-pong it;
    /// with its city's one terminal tried, a roaming bot leaves (review 6, H2).
    /// </summary>
    [Test]
    public void AnotherCitysTerminal_IsNeverTheDesk()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content());
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0);
        brain.Facilities.Observe(2, Routing.MissionTerminalKind, Here.X - 30f, Here.Z, TerminalAsset + 1, 0);
        var sim = new Sim
        {
            Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 0, 1), new SeenStructure(Here.X - 30f, Here.Z, 10, TerminalAsset, 0, 2)],
        };

        var targets = DeskVisits(brain, sim, 300);
        Assert.Multiple(() =>
        {
            Assert.That(targets, Is.EqualTo(new[] { (40f, 0f) }), "it went to another city's terminal, or to its own twice");
            Assert.That(brain.Counters.Why[(int)GoalWhy.DeskEmpty], Is.EqualTo(1), "its city's only terminal tried, and the roaming bot stayed");
        });
    }

    /// <summary>A home bot's one-terminal desk, tried and empty, opens again after <see cref="BotBrain.DeskRetrySeconds"/> — and not before.</summary>
    [Test]
    public void AOneTerminalDesk_OpensAgainAfterItsRetry()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content(), homeRegion: 1);
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0);
        var sim = new Sim { Planet = [new SeenStructure(Here.X + 40f, Here.Z, 10, TerminalAsset, 0, 1)] };
        var at = new List<long>();
        var targets = DeskVisits(brain, sim, 900, each: at.Add);
        Assert.Multiple(() =>
        {
            Assert.That(targets.Count, Is.GreaterThanOrEqualTo(2), "the desk never opened again");
            Assert.That(at.Count >= 2 ? (at[1] - at[0]) / 4d : 0d, Is.GreaterThanOrEqualTo(BotBrain.DeskRetrySeconds), "tried again before its retry");
            Assert.That(brain.Counters.Why[(int)GoalWhy.DeskEmpty], Is.Zero, "a home bot left home");
        });
    }

    /// <summary>A home bot out in the desert sets out for its home's terminal, not for a nearer one of another city (review 6, M1).</summary>
    [Test]
    public void AHomeBotInTheDesert_GoesToItsHomesDesk()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content(), homeRegion: 1);
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X + Here.Radius - 50f, Here.Z, TerminalAsset + 1, 0);
        brain.Facilities.Observe(2, Routing.MissionTerminalKind, Here.X + Here.Radius + 130f, Here.Z, TerminalAsset + 1, 0);
        var sim = new Sim { X = Here.X + Here.Radius + 100f, Z = Here.Z };
        brain.Decide(sim.View(), 1);
        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Visiting), "the premise: holding none, the desk first");
            Assert.That(brain.VisitTarget, Is.EqualTo((Here.X + Here.Radius - 50f, Here.Z)), "it set out for another city's terminal");
        });
    }

    /// <summary>
    /// Between cities a bot rides: refused three times (downed, queued), it runs only until an ask is granted, and knocked off its swoop half way it is
    /// back on within a couple of seconds — never thirty seconds on foot in the desert, the swoop's own rule.
    /// </summary>
    /// <remarks>Loïc, 2026-10-27: three bots followed for twenty minutes, some of them running between cities.</remarks>
    [Test]
    public void BetweenCities_ItRides_AndGetsBackOnAfterARefusalOrAForcedDismount()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d);
        var sim = new Sim { X = 0f, Z = 0f, RefuseMounts = 3 };
        var onFoot = 0;
        var longestOnFoot = 0;
        var knockedOffAt = -1L;
        var backOnAfter = -1L;
        var ridden = 0;
        for (var tick = 1L; tick < 1200; tick++)
        {
            var travelling = brain.Goal == Goal.Travelling && brain.Mode == TravelMode.Swoop;
            if (travelling && brain.RegionAt(sim.X, sim.Z) == 0)
            {
                onFoot = sim.Mounted ? 0 : onFoot + 1;
                longestOnFoot = Math.Max(longestOnFoot, onFoot);
                ridden += sim.Mounted ? 1 : 0;
            }

            // Half way, knocked off: a fight, a stance drawn.
            if (knockedOffAt < 0 && ridden == 40)
            {
                sim.Mounted = false;
                knockedOffAt = tick;
            }

            backOnAfter = backOnAfter < 0 && knockedOffAt > 0 && sim.Mounted ? tick - knockedOffAt : backOnAfter;
            sim.Apply(brain.Decide(sim.View(), tick));
        }

        Assert.Multiple(() =>
        {
            Assert.That(ridden, Is.GreaterThan(40), "the premise: a trip between cities, ridden");
            Assert.That(knockedOffAt, Is.GreaterThan(0), "the premise: knocked off its swoop");
            Assert.That(backOnAfter, Is.InRange(1, 12), "not back on its swoop within three seconds");
            Assert.That(longestOnFoot / 4d, Is.LessThan(Routing.RideOverSeconds), "thirty seconds on foot in the desert");
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

    /// <summary>A home bot in Port Sable that trades, with its city's bazaar 40 m east and a mission terminal 60 m west.</summary>
    private static (BotBrain Brain, Sim Sim) Trader(int stacks, int reserved, bool trades = true)
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content(), homeRegion: 1) { Trades = trades };
        brain.Facilities.Observe(1, Routing.BazaarKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0);
        brain.Facilities.Observe(1, Routing.MissionTerminalKind, Here.X - 60f, Here.Z, TerminalAsset + 1, 0);
        var sim = new Sim
        {
            Stacks = stacks,
            Reserved = reserved,
            Planet =
            [
                new SeenStructure(Here.X + 40f, Here.Z, Routing.BazaarKind, TerminalAsset, 0, 1),
                new SeenStructure(Here.X - 60f, Here.Z, Routing.MissionTerminalKind, TerminalAsset, 0, 1),
            ],
        };
        return (brain, sim);
    }

    /// <summary>
    /// bazaar.md § 8: a bot that trades, holding <see cref="BotBrain.MarketStacks"/> stacks or more or goods reserved for it, goes to its city's bazaar
    /// before the desk; a bot with room, nothing reserved, or a run that does not trade goes to the desk as it always did.
    /// </summary>
    [TestCase(BotBrain.MarketStacks, 0, true, true)]
    [TestCase(30, 0, true, true)]
    [TestCase(0, 1, true, true)]
    [TestCase(BotBrain.MarketStacks - 1, 0, true, false)]
    [TestCase(30, 0, false, false)]
    public void AFullInventory_GoesToTheBazaar_ABotWithRoomDoesNot(int stacks, int reserved, bool trades, bool toMarket)
    {
        var (brain, sim) = Trader(stacks, reserved, trades);
        brain.Decide(sim.View(), 1);
        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Visiting), "the premise: an errand first");
            Assert.That(brain.VisitTarget, Is.EqualTo(toMarket ? (Here.X + 40f, Here.Z) : (Here.X - 60f, Here.Z)), "not the bazaar, or not the desk");
            Assert.That(brain.Counters.Why[(int)GoalWhy.MarketChosen] + brain.Counters.Why[(int)GoalWhy.MarketFull], Is.EqualTo(toMarket ? 1 : 0));
        });
    }

    /// <summary>A bot full enough to be losing drops says so: <see cref="GoalWhy.MarketFull"/>, the telemetry's measure of too little headroom.</summary>
    [TestCase(BotBrain.MarketFullStacks, GoalWhy.MarketFull)]
    [TestCase(BotBrain.MarketFullStacks - 1, GoalWhy.MarketChosen)]
    public void AFullBot_SaysItIsFull(int stacks, GoalWhy why)
    {
        var (brain, sim) = Trader(stacks, 0);
        brain.Decide(sim.View(), 1);
        Assert.Multiple(() =>
        {
            Assert.That(brain.VisitTarget, Is.EqualTo((Here.X + 40f, Here.Z)), "not the bazaar");
            Assert.That(brain.Counters.Why[(int)why], Is.EqualTo(1));
            Assert.That(brain.Counters.Why[(int)GoalWhy.MarketChosen] + brain.Counters.Why[(int)GoalWhy.MarketFull], Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A bot back from a quest's lair, its city's bazaar out of errand reach, does not count that as a try: it goes home, and to the bazaar as soon as it
    /// is back in reach — not <see cref="BotBrain.MarketRetrySeconds"/> later, setting out on its next quest full meanwhile.
    /// </summary>
    [Test]
    public void ABazaarOutOfReach_IsNoTry_TheBotGoesOnceBackInReach()
    {
        var (brain, sim) = Trader(BotBrain.MarketFullStacks, 0);
        sim.X = Here.X + Here.Radius + BotBrain.ErrandReachM;
        brain.Decide(sim.View(), 1);
        Assert.That(brain.Goal, Is.EqualTo(Goal.Travelling), "the premise: out in the desert, it goes home first");
        Assert.That(brain.Counters.Why[(int)GoalWhy.MarketFull], Is.Zero, "a bazaar out of reach was set out for");

        // Home: the ride is the travel's, not the subject; the bot stands in its town.
        (sim.X, sim.Z) = (Here.X, Here.Z);
        var marketAt = -1L;
        for (var tick = 2L; tick < 400 && marketAt < 0; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
            marketAt = brain.Counters.Why[(int)GoalWhy.MarketFull] > 0 ? tick : -1L;
        }

        Assert.That(marketAt, Is.InRange(1L, (long)(BotBrain.MarketRetrySeconds * 4) - 1), "back in reach, it did not go to the bazaar before its retry");
    }

    /// <summary>
    /// The bazaar errand end to end: walked to, one <see cref="IntentKind.Trade"/> naming its city, the trade's stay (<see cref="BotBrain.TradeSeconds"/>
    /// on top of a terminal's), <see cref="GoalWhy.MarketDone"/>, and then the day's loop — the desk — not a second trip to the bazaar.
    /// </summary>
    [Test]
    public void AFullInventory_Trades_ThenGoesBackToTheDesk()
    {
        var (brain, sim) = Trader(26, 0);
        var trades = new List<(long Tick, Intent Intent)>();
        var targets = new List<(float X, float Z)>();
        var doneAt = -1L;
        var atBazaar = new List<long>();
        var was = brain.Goal;
        for (var tick = 1L; tick < 600 && targets.Count < 2; tick++)
        {
            var intent = brain.Decide(sim.View(), tick);
            sim.Apply(intent);
            if (brain.AtBazaar)
            {
                atBazaar.Add(tick);
            }

            if (intent.Kind == IntentKind.Trade)
            {
                trades.Add((tick, intent));
                sim.Stacks = MarketPlan.KeepStacks;   // the server's half: the plan vendored the bot down to its floor
            }

            if (doneAt < 0 && brain.Counters.Why[(int)GoalWhy.MarketDone] > 0)
            {
                doneAt = tick;
            }

            if (brain.Goal == Goal.Visiting && was != Goal.Visiting)
            {
                targets.Add((brain.VisitTarget.X - Here.X, brain.VisitTarget.Z - Here.Z));
            }

            was = brain.Goal;
        }

        Assert.Multiple(() =>
        {
            Assert.That(targets, Is.EqualTo(new[] { (40f, 0f), (-60f, 0f) }), "not the bazaar, then the desk");
            Assert.That(trades, Has.Count.EqualTo(1), "not one trade at the bazaar");
            Assert.That(trades.Count == 1 ? trades[0].Intent : default, Is.EqualTo(Intent.Trade(1)), "the trade does not name the bazaar's city");
            Assert.That(doneAt - (trades.Count == 1 ? trades[0].Tick : 0), Is.GreaterThanOrEqualTo((long)((5 + BotBrain.TradeSeconds) * 4)),
                "it left before the plan could be sent");
            Assert.That(brain.Counters.Why[(int)GoalWhy.MarketDone], Is.EqualTo(1));
            Assert.That(brain.TradesAsked, Is.EqualTo(1));

            // AtBazaar — the only time the swarm sends a market command — is the stay at the terminal: not the walk there, not the walk to the desk.
            Assert.That(atBazaar.FirstOrDefault(), Is.EqualTo(trades.Count == 1 ? trades[0].Tick : -1), "at the bazaar before it arrived");
            Assert.That(atBazaar.Count > 0 ? atBazaar[^1] : 0, Is.EqualTo(doneAt - 1), "at the bazaar after it left");
            Assert.That(atBazaar.Count, Is.EqualTo(atBazaar.Count > 0 ? atBazaar[^1] - atBazaar[0] + 1 : 0), "not one stay");
            Assert.That(brain.AtBazaar, Is.False, "at a mission terminal, it counts as at a bazaar");
        });
    }

    /// <summary>
    /// A bazaar that changes nothing — a vendor refusing, a bazaar out of reach — cannot hold a bot: the errand is not chosen again for
    /// <see cref="BotBrain.MarketRetrySeconds"/>, and is chosen again after it.
    /// </summary>
    [Test]
    public void AFullInventoryTheBazaarCannotEmpty_IsNotTakenThereAgainBeforeItsRetry()
    {
        var (brain, sim) = Trader(30, 0);
        var chosenAt = new List<long>();
        var chosen = 0L;
        for (var tick = 1L; tick < 1400; tick++)
        {
            sim.Apply(brain.Decide(sim.View(), tick));
            if (brain.Counters.Why[(int)GoalWhy.MarketChosen] + brain.Counters.Why[(int)GoalWhy.MarketFull] > chosen)
            {
                chosen = brain.Counters.Why[(int)GoalWhy.MarketChosen] + brain.Counters.Why[(int)GoalWhy.MarketFull];
                chosenAt.Add(tick);
            }
        }

        Assert.That(chosenAt, Has.Count.GreaterThanOrEqualTo(2), "the bazaar was never tried again");
        for (var i = 1; i < chosenAt.Count; i++)
        {
            Assert.That((chosenAt[i] - chosenAt[i - 1]) / 4d, Is.GreaterThanOrEqualTo(BotBrain.MarketRetrySeconds), $"tried again at tick {chosenAt[i]}");
        }
    }

    /// <summary>
    /// The rush (bazaar.md § 8): at the rush city, a bot walks to one of its bazaars and trades a round every <see cref="BotBrain.RushRoundSeconds"/>,
    /// for good — no desk, no hunt, no errand ends it.
    /// </summary>
    [Test]
    public void ARushBot_TradesAtItsBazaarEveryRound_ForGood()
    {
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content()) { Trades = true, RushRegion = 1, TakesMissions = false };
        brain.Facilities.Observe(1, Routing.BazaarKind, Here.X + 40f, Here.Z, TerminalAsset + 1, 0);
        var sim = new Sim
        {
            Creature = (42u, Here.X - 20f, Here.Z + 20f),
            Planet = [new SeenStructure(Here.X + 40f, Here.Z, Routing.BazaarKind, TerminalAsset, 0, 1)],
        };
        var trades = new List<long>();
        for (var tick = 1L; tick < 600; tick++)
        {
            var intent = brain.Decide(sim.View(), tick);
            sim.Apply(intent);
            if (intent.Kind == IntentKind.Trade)
            {
                Assert.That(intent.Region, Is.EqualTo(1), "a round names another city");
                trades.Add(tick);
            }

            if (trades.Count > 0)
            {
                Assert.That(brain.Goal, Is.EqualTo(Goal.Visiting), $"tick {tick}: it left its bazaar");
            }
        }

        Assert.That(trades, Has.Count.GreaterThanOrEqualTo(60), "not a round every two seconds");
        for (var i = 1; i < trades.Count; i++)
        {
            Assert.That(trades[i] - trades[i - 1], Is.EqualTo((long)(BotBrain.RushRoundSeconds * 4)), $"round {i} came late or early");
        }

        Assert.Multiple(() =>
        {
            Assert.That(brain.Counters.Why[(int)GoalWhy.CreatureNoticed], Is.Zero, "a rush bot went hunting");

            // One errand for good: a round is not a fresh choice and a fresh walk (a search) every two seconds.
            Assert.That(brain.Counters.Why[(int)GoalWhy.MarketChosen], Is.EqualTo(1), "the bazaar was chosen again for a round");
            Assert.That(brain.Counters.Why[(int)GoalWhy.MarketDone], Is.Zero, "a round ended the errand");
        });
    }

    /// <summary>
    /// No market command leaves a bot farther than the server's reach of the terminal (<c>TatooineData.BazaarReachM</c>, 6 m), so none is refused
    /// <c>MarketNotAtBazaar</c>: the brain and the swarm's sender together, the plan landing <paramref name="lateTicks"/> after the trade was asked — reads
    /// slow under load — and going out two commands a second. A plan in time is sent whole, the bot standing at the terminal until its last command; one
    /// that lands after the bot could wait no longer is dropped, never sent from the walk to the next errand (live: 66 057 refusals in 23 minutes).
    /// </summary>
    [TestCase(40, 30, 0)]
    [TestCase(0, 12, 0)]
    [TestCase(600, 30, 30)]
    public void ABazaarErrand_SendsEveryMarketCommandFromWithinTheTerminalsReach(int lateTicks, int commands, int dropped)
    {
        const double BazaarReachM = 6d;
        var (brain, sim) = Trader(26, 0);
        var sender = new MarketSender();
        var plan = Enumerable.Range(0, commands).Select(_ => MarketCommand.Vendor(8, 1, 5)).ToList();
        var offerAt = long.MaxValue;
        var visit = 0L;
        var reading = false;
        var sent = new List<double>();
        for (var tick = 1L; tick < 1200; tick++)
        {
            if (tick == offerAt)
            {
                sender.Offer(visit, plan);
                reading = false;
            }

            var intent = brain.Decide(sim.View() with { Trading = reading || sender.Busy }, tick);
            sim.Apply(intent);
            if (intent.Kind == IntentKind.Trade && offerAt == long.MaxValue)
            {
                offerAt = tick + lateTicks + 1;
                visit = brain.VisitSerial;
                reading = true;
                sim.Stacks = MarketPlan.KeepStacks;
            }

            if (sender.Next(brain.AtBazaar, brain.VisitSerial, tick, 2) is { })
            {
                sent.Add(Routing.Distance(sim.X, sim.Z, Here.X + 40f, Here.Z));
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(offerAt, Is.LessThan(long.MaxValue), "the premise: the bot traded");
            Assert.That(sent, Has.Count.EqualTo(commands - dropped), "not every command of a plan in time was sent, or one of a late plan was");
            Assert.That(sent.Count == 0 ? 0d : sent.Max(), Is.LessThanOrEqualTo(BazaarReachM), "a command was sent from beyond the terminal's reach");
            Assert.That(sender.Dropped, Is.EqualTo(dropped));
            Assert.That(brain.Counters.Why[(int)GoalWhy.MarketDone], Is.EqualTo(1), "the bot never left the bazaar");
        });
    }

    /// <summary>A rush bot anywhere else goes to the rush city first, saying why — a home bot included.</summary>
    [Test]
    public void ARushBotElsewhere_TravelsToTheRushCity()
    {
        var there = BotMap.Cities[1];
        var brain = new BotBrain(BotMap.Cities, 7u, 4d, Content(), homeRegion: 2) { Trades = true, RushRegion = 1, TakesMissions = false };
        var sim = new Sim { X = there.X, Z = there.Z };
        brain.Decide(sim.View(), 1);
        Assert.Multiple(() =>
        {
            Assert.That(brain.Goal, Is.EqualTo(Goal.Travelling));
            Assert.That(brain.TargetRegion, Is.EqualTo(1), "not to the rush city");
            Assert.That(brain.Counters.Why[(int)GoalWhy.MarketChosen], Is.EqualTo(1));
        });
    }
}

/// <summary>The pure pieces under the errands: the city walk, the room walk, and reading a descriptor.</summary>
[TestFixture]
public sealed class BotPathChecks
{
    private static BotAssets OneWall(PlanBox box) => new([new AssetPlan { Id = "wall", Walls = [box] }]);

    /// <summary>
    /// A searched walk across a staged street — two rows of houses, an alley between each pair — keeps the body's half and <see cref="CityPath.AirM"/>
    /// from every wall: a leg is pulled straight only where it keeps that air, and the corner before it is kept otherwise. A walk that starts or ends
    /// inside a wall's air (a body against a house, a stand point beside it) comes no closer to that wall than its end already is — and keeps the full air
    /// from every other wall, and from the far corners of its own (review 6).
    /// </summary>
    /// <remarks>Without the air, a leg pulled taut round a corner grazed it at the body's half and a few hundredths: a steered body touched it.</remarks>
    [Test]
    public void APulledWalk_KeepsAirAtEveryCorner()
    {
        var house = new PlanBox(-3f, -3f, 3f, 3f, 0f, 2.7f, false);
        const float postR = 0.3f;
        var post = new PlanBox(-postR, -postR, postR, postR, 0f, 3f, false, postR);
        var assets = new BotAssets([new AssetPlan { Id = "house", Walls = [house] }, new AssetPlan { Id = "post", Walls = [post] }]);
        var structures = new List<SeenStructure>();
        for (var i = 0; i < 4; i++)
        {
            structures.Add(new SeenStructure(i * 10f, 0f, 0, 0, 0, 1));    // the south row, alleys 4 m wide between
            structures.Add(new SeenStructure((i * 10f) + 5f, 14f, 0, 0, 0, 1));   // the north row, staggered half a house; the street is 8 m wide
        }

        // Lamp posts down the street: round colliders, whose open cells the grid leaves a body's half from their rim.
        var posts = new List<(float X, float Z)> { (4f, 7f), (16f, 7.5f), (27f, 6.5f) };
        foreach (var (px, pz) in posts)
        {
            structures.Add(new SeenStructure(px, pz, 0, 1, 0, 1));
        }

        var ends = new List<(float X, float Z)>();
        for (var k = 0; k < 7; k++)
        {
            ends.Add((-6f + (k * 7.3f), -7f));   // behind the south row
            ends.Add((-4f + (k * 6.9f), 7f));    // the street
            ends.Add((-2f + (k * 7.1f), 21f));   // behind the north row
        }

        // And ends inside a wall's air, outside its body margin: a body standing against a house's face, at its corner, in an alley.
        ends.AddRange([(3.35f, 1f), (-3.4f, -1.5f), (13.36f, 3.36f), (6.65f, 0.5f), (8.62f, 11.3f), (18.6f, 17.35f)]);

        // Just inside the air, near a corner, so the walk's first or last leg rounds that corner of its own wall — and round a post.
        ends.AddRange([(2.7f, -3.44f), (-2.7f, 3.44f), (13.44f, 2.7f), (6.56f, -2.7f), (7.7f, 17.44f), (21.6f, 10.56f)]);
        foreach (var (px, pz) in posts)
        {
            ends.AddRange([(px + postR + 0.42f, pz + 0.1f), (px - 0.1f, pz - postR - 0.43f)]);
        }
        var air = CityPath.BodyHalfM + CityPath.AirM;

        var closest = double.MaxValue;
        var where = string.Empty;
        var routes = 0;
        var nearEnds = 0;
        foreach (var from in ends)
        {
            foreach (var to in ends)
            {
                // A walk of a grid step or two is the grid's, not a pulled one: two ends 0.7 m apart round one corner have no other way.
                if (Routing.Distance(from.X, from.Z, to.X, to.Z) < 2d || CityPath.Route(structures, assets, from, to, BotBrain.MaxExpansions) is not { } path)
                {
                    continue;
                }

                routes++;
                var a = from;
                foreach (var b in path)
                {
                    foreach (var st in structures)
                    {
                        var (minX, minZ, maxX, maxZ) = (st.X + house.MinX, st.Z + house.MinZ, st.X + house.MaxX, st.Z + house.MaxZ);
                        var d = st.Asset == 1 ? SegmentToPoint(a, b, (st.X, st.Z)) - postR : SegmentToBox(a, b, minX, minZ, maxX, maxZ);

                        // What this leg is held to from this wall: the air, or as close as its nearer end already stands.
                        var ends_ = st.Asset == 1
                            ? Math.Min(SegmentToPoint(a, a, (st.X, st.Z)), SegmentToPoint(b, b, (st.X, st.Z))) - postR
                            : Math.Min(SegmentToBox(a, a, minX, minZ, maxX, maxZ), SegmentToBox(b, b, minX, minZ, maxX, maxZ));
                        nearEnds += ends_ < air ? 1 : 0;
                        var margin = d - Math.Min(air, ends_);
                        if (margin < closest)
                        {
                            (closest, where) = (margin, $"({a.X:F2}, {a.Z:F2}) to ({b.X:F2}, {b.Z:F2}) passes {d:F3} m from the house at ({st.X}, {st.Z})");
                        }
                    }

                    a = b;
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(routes, Is.GreaterThan(500), "the premise: walks across the street were found");
            Assert.That(nearEnds, Is.GreaterThan(0), "the premise: some walks start or end inside a wall's air");
            // Within a tenth of a cell: a single grid step, which no pull made, keeps what its two open cells allow — beside a post, 5 cm short of the air.
            Assert.That(closest, Is.GreaterThanOrEqualTo(-0.05d), $"a leg keeps {closest:F3} m less than its air: {where}");
        });
    }

    /// <summary>A segment's distance to a point.</summary>
    private static double SegmentToPoint((float X, float Z) a, (float X, float Z) b, (float X, float Z) c)
    {
        var (dx, dz) = (b.X - a.X, b.Z - a.Z);
        var len2 = (dx * dx) + (dz * dz);
        var t = len2 > 0f ? Math.Clamp((((c.X - a.X) * dx) + ((c.Z - a.Z) * dz)) / len2, 0f, 1f) : 0f;
        var (ex, ez) = (a.X + (dx * t) - c.X, a.Z + (dz * t) - c.Z);
        return Math.Sqrt((ex * ex) + (ez * ez));
    }

    /// <summary>A segment's distance to a rectangle: 0 when they meet, else the nearest of its ends to the box and of the box's corners to it.</summary>
    private static double SegmentToBox((float X, float Z) a, (float X, float Z) b, float minX, float minZ, float maxX, float maxZ)
    {
        var walls = new List<PlanBox> { new(minX, minZ, maxX, maxZ, 0f, 1f, false) };
        if (!CityPath.SegmentClear(walls, a.X, a.Z, b.X, b.Z))
        {
            return 0d;
        }

        var best = Math.Min(PointToBox(a), PointToBox(b));
        foreach (var c in new[] { (minX, minZ), (minX, maxZ), (maxX, minZ), (maxX, maxZ) })
        {
            best = Math.Min(best, PointToSegment(c));
        }

        return best;

        double PointToBox((float X, float Z) p) =>
            Math.Sqrt(Sq(Math.Max(0f, Math.Max(minX - p.X, p.X - maxX))) + Sq(Math.Max(0f, Math.Max(minZ - p.Z, p.Z - maxZ))));

        double PointToSegment((float X, float Z) c)
        {
            var (dx, dz) = (b.X - a.X, b.Z - a.Z);
            var len2 = (dx * dx) + (dz * dz);
            var t = len2 > 0f ? Math.Clamp((((c.X - a.X) * dx) + ((c.Z - a.Z) * dz)) / len2, 0f, 1f) : 0f;
            return Math.Sqrt(Sq(a.X + (dx * t) - c.X) + Sq(a.Z + (dz * t) - c.Z));
        }

        static double Sq(double v) => v * v;
    }

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
