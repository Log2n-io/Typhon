using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Numerics;
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace AsteroidField.Server;

/// <summary>What the server is started with. Every value has a default that makes a good demo.</summary>
public sealed class ServerOptions
{
    /// <summary>The TCP port clients connect to; 0 binds an ephemeral one (read <see cref="AsteroidServer.Port"/>).</summary>
    public int Port { get; set; } = 9300;

    /// <summary>Listen on every interface rather than loopback, so a Unity player on another machine can join.</summary>
    public bool AnyAddress { get; set; }

    public int TickRate { get; set; } = 20;

    public int Asteroids { get; set; } = 5000;

    public int Drones { get; set; } = 8;

    /// <summary>Ore a hold takes before the server says it is full.</summary>
    public int HoldCapacity { get; set; } = AsteroidServer.DefaultHoldCapacity;

    /// <summary>How far from the belt's centre a joining pilot's ship appears, per axis.</summary>
    public float PilotSpawnRadiusM { get; set; } = 200f;

    /// <summary>Where the database lives; a fresh temporary directory when null, deleted on dispose.</summary>
    public string DataDirectory { get; set; }

    /// <summary>The seed of the world's layout: the same seed, the same belt.</summary>
    public int Seed { get; set; } = 1977;
}

/// <summary>
/// The Asteroid Field server: a belt of drifting asteroids, a few drones mining them, and one ship per connected pilot.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it shows of Typhon.</b> The world is ECS data in a database, simulated by four tick systems. Replication is declared, not coded: each pilot's
/// session holds what is within 300 m of its ship (a Sphere around the controlled entity), sees drifting asteroids as motion segments — a straight drift
/// is one segment until something changes it, so thousands of them cost almost nothing to keep in sync — and its own cargo and credits as owner-only
/// fields. Commands come back in: steering (latest wins), mining a target the server checks the client was shown, selling. Two events go out: a burst
/// when an asteroid is mined out (best effort, to whoever is near), and "hold full" (reliable, to the pilot only).
/// </para>
/// <para>Systems run in this order each tick: <c>Sessions</c> (ships for pilots who join, gone for pilots who leave), <c>Orders</c> (mine and sell),
/// <c>Ships</c> (flight and mining), <c>Drift</c> (asteroids).</para>
/// </remarks>
public sealed class AsteroidServer : IDisposable
{
    public const string Kind = "pilot";
    public const string Profile = "pilot";
    public const float WorldHalfM = 2048f;
    public const double ViewRadiusM = 300;
    public const float MineRangeM = 60f;
    public const int DefaultHoldCapacity = 300;
    public const int CreditsPerOre = 5;

    private const float MaxShipSpeed = 80f;
    private const float ShipAccel = 40f;
    private const float ShipTurnRate = 2.5f;
    private const float ShipDrag = 0.6f;
    private const int OrePerTick = 1;

    private readonly ServerOptions _options;
    private readonly bool _ownsDirectory;
    private readonly string _directory;
    private readonly DatabaseEngine _dbe;
    private readonly TyphonRuntime _runtime;
    private readonly TcpSubscriptionTransport _tcp;
    private readonly Random _random;
    private readonly float _dt;

    // Serial systems are the only writers of these: the tick runs them one after the other.
    private readonly Dictionary<uint, EntityId> _shipOf = [];
    private readonly List<EntityId> _asteroids = [];

    private AsteroidServer(ServerOptions options)
    {
        _options = options;
        _random = new Random(options.Seed);
        _dt = 1f / options.TickRate;
        _ownsDirectory = options.DataDirectory == null;
        _directory = options.DataDirectory ?? Path.Combine(Path.GetTempPath(), $"asteroid-field-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        _dbe = DatabaseEngine.Open(Path.Combine(_directory, "belt.typhon"), o => o
            .Register<Body>().Register<Rock>().Register<Pilot>()
            .ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(-WorldHalfM, -WorldHalfM), new Vector2(WorldHalfM, WorldHalfM), cellSize: 128f)));
        Populate();

        _runtime = TyphonRuntime.Create(_dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Belt");
            dag.CallbackSystem("Sessions", Sessions);
            dag.CallbackSystem("Orders", Orders, after: "Sessions");
            dag.CallbackSystem("Ships", Ships, after: "Orders");
            dag.CallbackSystem("Drift", Drift, after: "Ships");
        }, new RuntimeOptions
        {
            WorkerCount = 2,
            BaseTickRate = options.TickRate,
            Subscriptions = new SubscriptionsOptions
            {
                // A third of the view radius: each session's view is delivered and swept in cells of this size.
                ReplicationCellM = ViewRadiusM / 3,
                IngressBytesPerSecond = 16 * 1024,
                ReliableEventBytes = 64 * 1024,
            },
        });

        Declare(_runtime.Subscriptions);
        _runtime.Start();
        _tcp = new TcpSubscriptionTransport(new TcpSubscriptionOptions
        {
            Port = options.Port,
            Address = options.AnyAddress ? IPAddress.Any : IPAddress.Loopback,
        });
        _runtime.StartSubscriptionTransport(_tcp);
    }

    /// <summary>The port clients connect to.</summary>
    public int Port => _tcp.BoundEndPoint.Port;

    /// <summary>Ships flown by connected pilots.</summary>
    public int Pilots { get; private set; }

    /// <summary>Asteroids mined out since start.</summary>
    public int Depletions { get; private set; }

    public static AsteroidServer Start(ServerOptions options) => new(options ?? new ServerOptions());

    public void Dispose()
    {
        _tcp.StopAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _runtime.Shutdown();
        _runtime.Dispose();
        _dbe.Dispose();
        if (_ownsDirectory)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ── Replication: what each pilot sees, sends and hears ─────────────────────────────────────────────────────────────────────────────────────────

    private static void Declare(SubscriptionsRegistry subs)
    {
        subs.Sessions.Kinds(Kind);

        // An asteroid drifts in a straight line: one motion segment describes it until it wraps around the belt's edge (a teleport).
        subs.Archetype<Asteroid>(a => a
            .Motion(Asteroid.Body, m => m.Tolerance(0.1).Teleport(maxSpeedMps: 40))
            .OnEnter(Asteroid.Rock, r => r.Size, Codec.U8, name: "size")
            .Field(Asteroid.Rock, r => r.Ore, Codec.U16, name: "ore"));

        // A ship: its motion, its heading (a ship turns in place, which its velocity cannot say), what it mines — the asteroid's netId, so every client
        // draws the beam — and, for its pilot alone, its hold and credits.
        subs.Archetype<Ship>(a => a
            .Motion(Ship.Body, m => m.Tolerance(0.05).Teleport(maxSpeedMps: 130))
            .Heading(Ship.Body, b => b.Angle, bits: 16, toleranceDeg: 1, name: "heading")
            .OnEnter(Ship.Pilot, p => p.Drone, Codec.U8, name: "drone")
            .Field(Ship.Pilot, p => p.Target, Codec.EntityRef, name: "target")
            .Owner(o => o
                .Field(Ship.Pilot, p => p.Cargo, Codec.Exact, name: "cargo")
                .Field(Ship.Pilot, p => p.Credits, Codec.Exact, name: "credits")));

        subs.Command<Steer>(c => c
            .Coalesce(CommandCoalesce.LatestPerSession)
            .Rate(30, 60)
            .Precheck(static (in s) => float.IsFinite(s.Thrust) && float.IsFinite(s.Turn))
            .Field(s => s.Thrust, Codec.F32, "thrust")
            .Field(s => s.Turn, Codec.F32, "turn"));
        subs.Command<Mine>(c => c.Rate(4, 8).Field(m => m.Target, Codec.EntityRef, "target"));
        subs.Command<Sell>(c => c.Rate(2, 4).Field(s => s.Unused, Codec.U8, "unused"));

        subs.Event<Depleted>(e => e
            .RouteNear(d => new Vector3D(d.X, d.Y, 0d), radiusM: 400)
            .Field(d => d.X, Codec.F32, "x")
            .Field(d => d.Y, Codec.F32, "y")
            .Field(d => d.Size, Codec.U8, "size"));
        subs.Event<CargoFull>(e => e
            .RouteToOwner(c => c.Ship)
            .Reliable()
            .Entity(c => c.Ship, "ship")
            .Field(c => c.Cargo, Codec.Exact, "cargo"));

        subs.Profile(Profile, p => p.Sphere(ViewRadiusM).AroundControlled().Of<Asteroid>().Of<Ship>());
    }

    // ── The world at start ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Populate()
    {
        // In batches: one transaction holds every page it writes until it commits.
        var made = 0;
        while (made < _options.Asteroids)
        {
            using var tx = _dbe.CreateQuickTransaction();
            for (var i = 0; i < 500 && made < _options.Asteroids; i++, made++)
            {
                _asteroids.Add(SpawnAsteroid(tx, RandomPoint()));
            }

            tx.Commit();
        }

        using (var tx = _dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < _options.Drones; i++)
            {
                SpawnShip(tx, RandomPoint() * 0.3f, session: 0, drone: true);
            }

            tx.Commit();
        }

        _dbe.WriteTickFence(1);
    }

    private EntityId SpawnAsteroid(Transaction tx, Vector2 at)
    {
        var size = (byte)(1 + _random.Next(4));
        var speed = 1f + ((float)_random.NextDouble() * 7f);
        var direction = (float)(_random.NextDouble() * Math.PI * 2);
        var body = new Body { Bounds = Box(at), Vx = speed * MathF.Sin(direction), Vy = speed * MathF.Cos(direction) };
        var rock = new Rock { Ore = (ushort)(40 * size * size), Size = size };
        return tx.Spawn<Asteroid>(Asteroid.Body.Set(in body), Asteroid.Rock.Set(in rock));
    }

    private EntityId SpawnShip(Transaction tx, Vector2 at, uint session, bool drone)
    {
        var body = new Body { Bounds = Box(at), Angle = (float)(_random.NextDouble() * Math.PI * 2) };
        var pilot = new Pilot { Session = session, Drone = drone ? (byte)1 : (byte)0 };
        return tx.Spawn<Ship>(Ship.Body.Set(in body), Ship.Pilot.Set(in pilot));
    }

    private Vector2 RandomPoint() =>
        new(((float)_random.NextDouble() * 2f - 1f) * (WorldHalfM - 16f), ((float)_random.NextDouble() * 2f - 1f) * (WorldHalfM - 16f));

    private static AABB2F Box(Vector2 at) => new() { MinX = at.X, MinY = at.Y, MaxX = at.X, MaxY = at.Y };

    private static Vector2 Center(in AABB2F b) => new((b.MinX + b.MaxX) * 0.5f, (b.MinY + b.MaxY) * 0.5f);

    // ── Systems ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    // A pilot who joins gets a ship near the belt's centre and a view around it; a pilot who leaves takes the ship along.
    private void Sessions(TickContext ctx)
    {
        var subs = ctx.Subscriptions;
        var tx = ctx.Transaction;
        if (subs == null || tx == null)
        {
            return;
        }

        foreach (ref readonly var e in subs.SessionEvents)
        {
            if (e.Kind == SessionEventKind.Opened)
            {
                var ship = SpawnShip(tx, RandomPoint() * (_options.PilotSpawnRadiusM / WorldHalfM), e.Session.Value, drone: false);
                _shipOf[e.Session.Value] = ship;
                subs.Session(e.Session).Profile(Profile).Control(ship);
                Pilots++;
            }
            else if (e.Kind == SessionEventKind.Closed && _shipOf.Remove(e.Session.Value, out var ship))
            {
                tx.Destroy(ship);
                Pilots--;
            }
        }
    }

    // Mine and sell. Mine names its target by the netId the client was shown: TryResolve refuses anything the session does not hold.
    private void Orders(TickContext ctx)
    {
        var subs = ctx.Subscriptions;
        var tx = ctx.Transaction;
        if (subs == null || tx == null)
        {
            return;
        }

        foreach (ref readonly var command in subs.Commands<Mine>())
        {
            if (!_shipOf.TryGetValue(command.Session.Value, out var ship) || !tx.TryOpenMut(ship, out var e))
            {
                continue;
            }

            var pilot = e.Read(Ship.Pilot);
            pilot.Target = command.Value.Target != 0 && subs.TryResolve(command.Session, command.Value.Target, out var target)
                && target.ArchetypeId == Asteroid.CatalogId
                    ? target
                    : EntityId.Null;
            e.Set(Ship.Pilot, in pilot);
            subs.Replicate(in e);
        }

        foreach (ref readonly var command in subs.Commands<Sell>())
        {
            if (!_shipOf.TryGetValue(command.Session.Value, out var ship) || !tx.TryOpenMut(ship, out var e))
            {
                continue;
            }

            var pilot = e.Read(Ship.Pilot);
            pilot.Credits += pilot.Cargo * CreditsPerOre;
            pilot.Cargo = 0;
            e.Set(Ship.Pilot, in pilot);
            subs.Replicate(in e);
        }
    }

    // Flight for every ship — a pilot's stick, or a drone's autopilot — then mining for those in range of their target.
    private void Ships(TickContext ctx)
    {
        var subs = ctx.Subscriptions;
        var tx = ctx.Transaction;
        if (subs == null || tx == null)
        {
            return;
        }

        var steer = subs.Commands<Steer>();
        foreach (var cluster in tx.For<Ship>().GetClusterEnumerator())
        {
#pragma warning disable TYPHON009 // the spatial column is read here and written back through WriteSpatial below
            var bodies = cluster.GetSpan(Ship.Body);
#pragma warning restore TYPHON009
            var pilots = cluster.GetSpan(Ship.Pilot);
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                var body = bodies[slot];
                ref var pilot = ref pilots[slot];

                float thrust = 0, turn = 0;
                if (pilot.Drone != 0)
                {
                    Autopilot(tx, ref pilot, in body, out thrust, out turn);
                }
                else if (steer.TryGetLatest(SessionId.FromValue(pilot.Session), out var input))
                {
                    thrust = Math.Clamp(input.Value.Thrust, -1f, 1f);
                    turn = Math.Clamp(input.Value.Turn, -1f, 1f);
                }

                Fly(ref body, thrust, turn);
                cluster.WriteSpatial(Ship.Body, slot, body);

                if (MineTarget(tx, subs, ref pilot, in body, cluster.GetEntityId(slot)))
                {
                    subs.Replicate(in cluster, slot);
                }

                // Turning in place changes the heading and nothing the engine tracks on its own: say so.
                if (turn != 0)
                {
                    subs.Replicate(in cluster, slot);
                }
            }
        }
    }

    private void Fly(ref Body body, float thrust, float turn)
    {
        body.Angle = WrapAngle(body.Angle + (turn * ShipTurnRate * _dt));
        body.Vx += MathF.Sin(body.Angle) * thrust * ShipAccel * _dt;
        body.Vy += MathF.Cos(body.Angle) * thrust * ShipAccel * _dt;
        var drag = MathF.Pow(ShipDrag, _dt);
        body.Vx *= drag;
        body.Vy *= drag;
        var speed = MathF.Sqrt((body.Vx * body.Vx) + (body.Vy * body.Vy));
        if (speed > MaxShipSpeed)
        {
            body.Vx *= MaxShipSpeed / speed;
            body.Vy *= MaxShipSpeed / speed;
        }

        var at = Center(in body.Bounds) + (new Vector2(body.Vx, body.Vy) * _dt);
        body.Bounds = Box(Wrap(at));
    }

    // One ore a tick while in range; a mined-out asteroid bursts and a new one drifts in somewhere else; a full hold stops the beam and tells the pilot.
    // Returns whether the pilot changed (its target or its hold), so the caller replicates it.
    private bool MineTarget(Transaction tx, SubscriptionsCommands subs, ref Pilot pilot, in Body ship, EntityId shipId)
    {
        if (pilot.Target.IsNull)
        {
            return false;
        }

        if (!tx.TryOpenMut(pilot.Target, out var asteroid))
        {
            pilot.Target = EntityId.Null;   // mined out by someone else
            return true;
        }

        var rockAt = Center(asteroid.Read(Asteroid.Body).Bounds);
        if (Vector2.Distance(rockAt, Center(in ship.Bounds)) > MineRangeM)
        {
            return false;
        }

        var rock = asteroid.Read(Asteroid.Rock);
        var mined = Math.Min(OrePerTick, (int)rock.Ore);
        rock.Ore -= (ushort)mined;
        pilot.Cargo += mined;
        if (rock.Ore == 0)
        {
            subs.Emit(new Depleted { X = rockAt.X, Y = rockAt.Y, Size = rock.Size });
            ReplaceAsteroid(tx, pilot.Target);
            pilot.Target = EntityId.Null;
            Depletions++;
        }
        else
        {
            asteroid.Set(Asteroid.Rock, in rock);
            subs.Replicate(in asteroid);
        }

        if (pilot.Cargo >= _options.HoldCapacity)
        {
            pilot.Target = EntityId.Null;
            if (pilot.Drone != 0)
            {
                pilot.Credits += pilot.Cargo * CreditsPerOre;
                pilot.Cargo = 0;
            }
            else
            {
                subs.Emit(new CargoFull { Ship = shipId, Cargo = pilot.Cargo });
            }
        }

        return true;
    }

    private void ReplaceAsteroid(Transaction tx, EntityId depleted)
    {
        tx.Destroy(depleted);
        var index = _asteroids.IndexOf(depleted);
        var fresh = SpawnAsteroid(tx, RandomPoint());
        if (index >= 0)
        {
            _asteroids[index] = fresh;
        }
    }

    // A drone flies to a random asteroid, mines it, and picks another; it sells on its own when full.
    private void Autopilot(Transaction tx, ref Pilot pilot, in Body body, out float thrust, out float turn)
    {
        thrust = 0;
        turn = 0;
        if (pilot.Target.IsNull || !tx.TryOpen(pilot.Target, out var target))
        {
            pilot.Target = _asteroids[_random.Next(_asteroids.Count)];
            return;
        }

        var to = Center(target.Read(Asteroid.Body).Bounds) - Center(in body.Bounds);
        var distance = to.Length();
        var wanted = MathF.Atan2(to.X, to.Y);
        var error = WrapAngle(wanted - body.Angle);
        turn = Math.Clamp(error * 2f, -1f, 1f);
        thrust = distance > MineRangeM * 0.6f && MathF.Abs(error) < 0.6f ? Math.Clamp(distance / 200f, 0.2f, 1f) : -0.2f;
    }

    // Asteroids drift; one that leaves the belt comes back on the other side.
    private void Drift(TickContext ctx)
    {
        var tx = ctx.Transaction;
        if (tx == null)
        {
            return;
        }

        foreach (var cluster in tx.For<Asteroid>().GetClusterEnumerator())
        {
#pragma warning disable TYPHON009
            var bodies = cluster.GetSpan(Asteroid.Body);
#pragma warning restore TYPHON009
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                var body = bodies[slot];
                var at = Center(in body.Bounds) + (new Vector2(body.Vx, body.Vy) * _dt);
                body.Bounds = Box(Wrap(at));
                cluster.WriteSpatial(Asteroid.Body, slot, body);
            }
        }
    }

    private static Vector2 Wrap(Vector2 at)
    {
        static float W(float v) => v < -WorldHalfM ? v + (2 * WorldHalfM) : v >= WorldHalfM ? v - (2 * WorldHalfM) : v;
        return new Vector2(W(at.X), W(at.Y));
    }

    private static float WrapAngle(float a)
    {
        while (a > MathF.PI)
        {
            a -= 2 * MathF.PI;
        }

        while (a < -MathF.PI)
        {
            a += 2 * MathF.PI;
        }

        return a;
    }
}
