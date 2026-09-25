using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>Which movers a <see cref="VolumeOracle"/> run spawns: both kinds, or one, for the degeneracy comparison.</summary>
internal enum VolumeMovers
{
    /// <summary>3D flyers (<see cref="ProjFlyer"/>, <c>pos3</c>) and 2D walkers on the plane z = 0 (<see cref="ProjCreature"/>, <c>pos2</c>).</summary>
    Both,

    /// <summary>Flyers only.</summary>
    Flyers,

    /// <summary>Walkers only.</summary>
    Walkers,
}

/// <summary>
/// The push oracle in three dimensions (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 10): a world of 3D flyers and 2D walkers on the
/// plane z = 0, churned by a seeded workload, served to sessions that walk, climb and teleport, each replica compared against its own sphere whenever the
/// world is quiet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deep or flat.</b> Over a volumetric spatial world the grid is deep and the deep implementation serves it; over a flat one the flat implementation
/// does — or the deep one, forced, which must agree with it. In a flat grid every geometric z is 0 (10 § 3.4), so a flyer's altitude changes nothing about
/// who holds it: distances are taken in the plane, while its position is still compared on all three axes.
/// </para>
/// <para>
/// <b>What is compared</b> is <see cref="OracleHarness"/>'s geometric comparison: an entity well inside a session's sphere must be held, one well outside
/// must not be, one in the band between may be either; every held entity's position and fields must be the server's; no published record may be illegal
/// (the shadow oracle).
/// </para>
/// </remarks>
internal sealed unsafe class VolumeOracle : IDisposable
{
    public const string Profile = "volume";

    /// <summary>How many quiet ticks precede a comparison, as <see cref="OracleHarness.QuietTicks"/>.</summary>
    public const int QuietTicks = 8;

    private const double MaxSpeedMps = ProjectionTestSchema.MaxSpeedMps;

    private readonly FrameHarness _harness;
    private readonly SessionId[] _sessions;
    private readonly int[] _skipPercent;
    private readonly Random[] _delivery;
    private readonly Random _random;
    private readonly Random _altitude;
    private readonly Random _walker;
    private readonly bool _volumetric;
    private readonly VolumeMovers _movers;
    private readonly double _limit;
    private readonly double _positionStep;
    private readonly Vector3D[] _viewpoints;
    private readonly double[] _radii;
    private readonly List<EntityId> _flyers = [];
    private readonly List<EntityId> _walkers = [];
    private readonly List<EntityId> _rocks = [];
    private readonly Dictionary<ulong, int> _spawnOrder = [];
    private readonly Dictionary<int, int> _wire = [];
    private readonly int _flyerIndex;
    private readonly int _creatureIndex;
    private readonly int _rockIndex;
    private int _nextTemplate = 1;
    private int _spawned;
    private long _tick;

    // The region mode (09 § 7): each session's region, a frustum it pans, reshapes and jumps — a convex polyhedron of eight corners in a deep grid.
    private readonly double _regionEdge;
    private readonly Frustum[] _frustums = [];
    private bool _budgeted;

    private struct Frustum
    {
        public double X;
        public double Y;
        public double Z;
        public double Ux;
        public double Uy;
        public double Uz;
        public double Length;
        public double Near;
        public double Far;
    }

    private VolumeOracle(FrameHarness harness, int seed, int[] skipPercent, bool volumetric, VolumeMovers movers, double radius, double spanM,
        double regionEdge = 0)
    {
        _harness = harness;
        _skipPercent = skipPercent;
        _volumetric = volumetric;
        _movers = movers;
        _random = new Random(seed);
        _altitude = new Random(seed ^ 0xA17);
        _walker = new Random(seed ^ 0x5EED);
        _limit = spanM > 0 ? spanM : (volumetric ? ProjectionTestSchema.VolumeExtentM : ProjectionTestSchema.WorldExtentM) - 64.0;
        _positionStep = volumetric ? ProjectionTestSchema.VolumePositionStepM : ProjectionTestSchema.PositionStepM;
        _sessions = harness.OpenSessions(skipPercent.Length, Profile);
        _viewpoints = new Vector3D[_sessions.Length];
        _radii = new double[_sessions.Length];
        _delivery = new Random[_sessions.Length];
        for (var i = 0; i < _sessions.Length; i++)
        {
            _radii[i] = radius;
            _viewpoints[i] = RandomViewpoint();
            Assert.That(harness.Sessions.SetViewpoint(_sessions[i], _viewpoints[i]), Is.True);
            _delivery[i] = new Random(seed + (7919 * (i + 1)));
        }

        _flyerIndex = harness.PlanIndex(nameof(ProjFlyer));
        _creatureIndex = harness.PlanIndex(nameof(ProjCreature));
        _rockIndex = harness.PlanIndex(nameof(ProjRock));

        // A replica's archetypes are the catalog's, in wire order; the engine's plans are in declaration order.
        foreach (var (plan, name) in new[] { (_flyerIndex, nameof(ProjFlyer)), (_creatureIndex, nameof(ProjCreature)), (_rockIndex, nameof(ProjRock)) })
        {
            _wire[plan] = Array.FindIndex(harness.CatalogPlan.Archetypes, a => a.Name == name);
        }

        _regionEdge = regionEdge;
        if (regionEdge > 0)
        {
            _frustums = new Frustum[_sessions.Length];
            for (var i = 0; i < _sessions.Length; i++)
            {
                _frustums[i] = RandomFrustum();
                SendRegion(i);
            }
        }
    }

    /// <summary>How many region jumps the walk made: each moves a region clear of its delivered cells, which resets its session.</summary>
    public int RegionJumps { get; private set; }

    private Frustum RandomFrustum()
    {
        var f = new Frustum();
        Recentre(ref f);
        Reshape(ref f);
        return f;
    }

    // A centre in the populated cube; a third of the time on the plane z = 0, where the 2D walkers live.
    private void Recentre(ref Frustum f)
    {
        var limit = _limit * 0.9;
        f.X = ((_walker.NextDouble() * 2.0) - 1.0) * limit;
        f.Y = ((_walker.NextDouble() * 2.0) - 1.0) * limit;
        f.Z = _walker.Next(3) == 0 ? 0.0 : ((_walker.NextDouble() * 2.0) - 1.0) * limit;
    }

    // A random axis and a frustum along it — a camera's view, narrow near and wide far — now and then wider than the profile accepts (ingress clamps it).
    private void Reshape(ref Frustum f)
    {
        var theta = _walker.NextDouble() * Math.PI * 2.0;
        var phi = (_walker.NextDouble() - 0.5) * Math.PI;
        (f.Ux, f.Uy, f.Uz) = (Math.Cos(theta) * Math.Cos(phi), Math.Sin(theta) * Math.Cos(phi), Math.Sin(phi));
        var scale = _regionEdge * (_walker.Next(10) == 0 ? 1.6 : 1.0);
        f.Length = scale * (0.3 + (0.2 * _walker.NextDouble()));
        f.Near = scale * (0.05 + (0.1 * _walker.NextDouble()));
        f.Far = scale * (0.15 + (0.1 * _walker.NextDouble()));
    }

    private static RegionVertex[] Corners(in Frustum f)
    {
        // An orthonormal basis (v, w) across the axis u.
        double ax = Math.Abs(f.Ux) < 0.9 ? 1 : 0, ay = ax == 1 ? 0 : 1, az = 0;
        var vx = (f.Uy * az) - (f.Uz * ay);
        var vy = (f.Uz * ax) - (f.Ux * az);
        var vz = (f.Ux * ay) - (f.Uy * ax);
        var vl = Math.Sqrt((vx * vx) + (vy * vy) + (vz * vz));
        (vx, vy, vz) = (vx / vl, vy / vl, vz / vl);
        var wx = (f.Uy * vz) - (f.Uz * vy);
        var wy = (f.Uz * vx) - (f.Ux * vz);
        var wz = (f.Ux * vy) - (f.Uy * vx);
        var corners = new RegionVertex[8];
        var k = 0;
        foreach (var (along, half) in new[] { (-f.Length / 2, f.Near), (f.Length / 2, f.Far) })
        {
            foreach (var (sv, sw) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
            {
                corners[k++] = new RegionVertex
                {
                    X = f.X + (f.Ux * along) + (((vx * sv) + (wx * sw)) * half),
                    Y = f.Y + (f.Uy * along) + (((vy * sv) + (wy * sw)) * half),
                    Z = f.Z + (f.Uz * along) + (((vz * sv) + (wz * sw)) * half),
                };
            }
        }

        return corners;
    }

    private void SendRegion(int session) =>
        Assert.That(_harness.Subscriptions.Ingress.SetRegionForTest(_sessions[session], Corners(in _frustums[session]), 3), Is.True,
            "a frustum is a convex polyhedron");

    /// <summary>Moves every session's region: mostly a pan of a fraction of a cell in 3D, sometimes a new frustum in place, now and then a jump.</summary>
    private void MoveRegions()
    {
        var limit = _limit * 0.9;
        var step = Push.CellSize * 0.3;
        for (var i = 0; i < _sessions.Length; i++)
        {
            var roll = _walker.Next(100);
            ref var f = ref _frustums[i];
            if (roll < 2)
            {
                Recentre(ref f);
                RegionJumps++;
            }
            else if (roll < 12)
            {
                Reshape(ref f);
            }
            else if (roll < 80)
            {
                var theta = _walker.NextDouble() * Math.PI * 2.0;
                var phi = (_walker.NextDouble() - 0.5) * Math.PI;
                f.X = Math.Clamp(f.X + (Math.Cos(theta) * Math.Cos(phi) * step), -limit, limit);
                f.Y = Math.Clamp(f.Y + (Math.Sin(theta) * Math.Cos(phi) * step), -limit, limit);
                f.Z = Math.Clamp(f.Z + (Math.Sin(phi) * step), -limit, limit);
            }
            else
            {
                continue;
            }

            SendRegion(i);
        }
    }

    /// <summary>Builds the oracle over a fresh engine and runs its first tick.</summary>
    /// <param name="engine">The engine: <c>ProjectionTestSchema.SetupEngine(…, volumetric)</c>.</param>
    /// <param name="volumetric">Whether the engine's spatial world is the volumetric cube.</param>
    /// <param name="radius">The profile's sphere radius, the largest a session takes.</param>
    /// <param name="cellM">The replication cell side.</param>
    /// <param name="forceDeep">Serve a flat world with the deep implementation.</param>
    /// <param name="spanM">Confine entities and viewers to ±span on the plane axes; zero for the whole world less a margin.</param>
    /// <param name="regionEdgeM">When positive, the profile is a ClientRegion of this extent (09 § 7), and each session sends a frustum it moves.</param>
    /// <param name="nearBudget">The ClientRegion's near budget; 0 for none.</param>
    /// <param name="aggregateCells">When positive, an Aggregate of flyers beside the entity observer, tiles this many cells wide.</param>
    public static VolumeOracle Create(DatabaseEngine engine, bool volumetric, int seed, int[] skipPercent, string name, double radius, double cellM,
        VolumeMovers movers = VolumeMovers.Both, bool forceDeep = false, int flyers = 300, int walkers = 150, int rocks = 60, double spanM = 0,
        double startRadius = 0, double regionEdgeM = 0, int nearBudget = 0, int aggregateCells = 0)
    {
        var options = new SubscriptionsOptions
        {
            IngressBytesPerSecond = TestIngress.Budget,
            ReplicationCellM = cellM,
            PushShadow = true,
            ForceDeepReplicationForTest = forceDeep,
            MaxSessions = 64,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
        };

        var harness = FrameHarness.Create(engine, subs =>
        {
            subs.Archetype<ProjFlyer>(a => a
                .Motion(ProjFlyer.Bounds, m => m.Tolerance(0.05).Teleport(MaxSpeedMps))
                .OnEnter(ProjFlyer.Ai, x => x.Template, Codec.U8, name: "template")
                .Field(ProjFlyer.Ai, x => x.Level, Codec.U16, name: "level"));
            ProjectionTestSchema.DeclareCreature(subs);
            ProjectionTestSchema.DeclareRock(subs);
            // With a start radius, the profile is Sphere(start, max: radius): sessions begin at the start and SetRadius ranges up to radius (09 § 4).
            if (regionEdgeM > 0)
            {
                subs.Profile(Profile, p =>
                {
                    var region = p.Detection(PushDetection.Explicit).ClientRegion(regionEdgeM);
                    if (nearBudget > 0)
                    {
                        region.Near(nearBudget);
                    }

                    region.Of<ProjFlyer>().Of<ProjCreature>().Of<ProjRock>();
                    if (aggregateCells > 0)
                    {
                        p.Aggregate(aggregateCells * cellM, rateHz: 10).Of<ProjFlyer>();
                    }
                });
                return;
            }

            subs.Profile(Profile, p => p.Detection(PushDetection.Explicit)
                .Sphere(startRadius > 0 ? startRadius : radius, max: startRadius > 0 ? radius : 0)
                .Of<ProjFlyer>().Of<ProjCreature>().Of<ProjRock>());
        }, name, options);

        try
        {
            harness.DrainNetIds = regionEdgeM > 0;
            var oracle = new VolumeOracle(harness, seed, skipPercent, volumetric, movers, startRadius > 0 ? startRadius : radius, spanM, regionEdgeM)
            {
                _budgeted = nearBudget > 0,
            };
            oracle.Seed(movers == VolumeMovers.Walkers ? 0 : flyers, movers == VolumeMovers.Flyers ? 0 : walkers, rocks);
            oracle._tick = 1;
            engine.WriteTickFence(1);
            harness.RunTick(1);
            return oracle;
        }
        catch
        {
            harness.Dispose();
            throw;
        }
    }

    public PushReplication Push => _harness.Subscriptions.Push;

    /// <summary>The frame harness underneath.</summary>
    public FrameHarness Frames => _harness;

    public SessionId[] Sessions => _sessions;

    public DatabaseEngine Engine => _harness.Engine;

    /// <summary>Entities compared at the last point, across every session.</summary>
    public long ComparedAtLastPoint { get; private set; }

    /// <summary>Entities the last point required a session to hold.</summary>
    public long RequiredAtLastPoint { get; private set; }

    /// <summary>Teleports of flyers across the world, each a migration and a motion epoch.</summary>
    public int Teleports { get; private set; }

    /// <summary>Climbs: flyer moves with a vertical component.</summary>
    public int Climbs { get; private set; }

    public int Destroyed { get; private set; }

    /// <summary>Viewpoint teleports, each a reset.</summary>
    public int ViewpointTeleports { get; private set; }

    /// <summary>A session's sphere radius from now on, through <c>SetRadius</c> (09 § 4): the next frames sweep the shell between the two spheres.</summary>
    public void SetRadius(int session, double radius)
    {
        _radii[session] = radius;
        Assert.That(_harness.Subscriptions.Commands.SetRadius(_sessions[session], radius), Is.True);
    }

    // ── The world ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private void Seed(int flyers, int walkers, int rocks)
    {
        for (var i = 0; i < Math.Max(flyers, walkers); i++)
        {
            SpawnMover();
        }

        using var tx = Engine.CreateQuickTransaction();
        for (var i = 0; i < rocks; i++)
        {
            var bounds = Bounds2(Coordinate(), Coordinate());
            var ai = new ProjAi { Template = (byte)(_nextTemplate++ & 0xFF) };
            var id = tx.Spawn<ProjRock>(ProjRock.Bounds.Set(in bounds), ProjRock.Ai.Set(in ai));
            _rocks.Add(id);
            _spawnOrder[id.RawValue] = _spawned++;
        }

        tx.Commit();
    }

    private double Coordinate() => ((_random.NextDouble() * 2.0) - 1.0) * _limit;

    // A flyer's altitude from its own generator, so a flyers-only and a walkers-only run draw the same plane coordinates (the degeneracy case).
    private float Altitude() => _volumetric ? (float)(((_altitude.NextDouble() * 2.0) - 1.0) * _limit) : (float)(1.0 + (_altitude.NextDouble() * 250.0));

    private static ProjBounds Bounds2(double x, double y) => new()
    {
        Bounds = new AABB2F { MinX = (float)x - 0.5f, MinY = (float)y - 0.5f, MaxX = (float)x + 0.5f, MaxY = (float)y + 0.5f },
        Speed = 1f,
    };

    private static ProjBounds3 Bounds3(double x, double y, double z) => new()
    {
        Bounds = new AABB3F
        {
            MinX = (float)x - 0.5f, MinY = (float)y - 0.5f, MinZ = (float)z - 0.5f, MaxX = (float)x + 0.5f, MaxY = (float)y + 0.5f, MaxZ = (float)z + 0.5f,
        },
        Speed = 1f,
    };

    private double Clamp(double v) => Math.Clamp(v, -_limit, _limit);

    private float ClampAltitude(double z) => (float)(_volumetric ? Math.Clamp(z, -_limit, _limit) : Math.Clamp(z, 1.0, 251.0));

    // One mover of each kind the run spawns, from the same plane coordinates.
    private void SpawnMover()
    {
        var x = Coordinate();
        var y = Coordinate();
        var template = (byte)(_nextTemplate++ & 0xFF);
        var level = (ushort)_random.Next(1, 500);
        using var tx = Engine.CreateQuickTransaction();
        if (_movers != VolumeMovers.Walkers)
        {
            var bounds = Bounds3(x, y, Altitude());
            var ai = new ProjAi { Template = template, Level = level, Mode = ProjAiMode.Wander, Alerted = 1 };
            var id = tx.Spawn<ProjFlyer>(ProjFlyer.Bounds.Set(in bounds), ProjFlyer.Ai.Set(in ai));
            _flyers.Add(id);
            _spawnOrder[id.RawValue] = _spawned;
        }

        if (_movers != VolumeMovers.Flyers)
        {
            var bounds = Bounds2(x, y);
            var ai = new ProjAi { Template = template, Level = level, Mode = ProjAiMode.Wander, Alerted = 1 };
            var vitals = new ProjVitals { Health = 10, MaxHealth = 20 };
            var id = tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(in bounds), ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(in vitals));
            _walkers.Add(id);
            _spawnOrder[id.RawValue] = _spawned;
        }

        _spawned++;
        tx.Commit();
    }

    // Destroys the index-th mover of each kind the run spawns: the same one, by spawn order, in a one-kind run.
    private void DestroyMover()
    {
        var count = _movers == VolumeMovers.Walkers ? _walkers.Count : _flyers.Count;
        if (count == 0)
        {
            return;
        }

        var index = _random.Next(count);
        using var tx = Engine.CreateQuickTransaction();
        if (_movers != VolumeMovers.Walkers)
        {
            tx.Destroy(_flyers[index]);
            _flyers.RemoveAt(index);
        }

        if (_movers != VolumeMovers.Flyers)
        {
            tx.Destroy(_walkers[index]);
            _walkers.RemoveAt(index);
        }

        Destroyed++;
        tx.Commit();
    }

    /// <summary>One tick of churn.</summary>
    public void Churn()
    {
        var roll = _random.Next(100);
        if (roll < 10)
        {
            SpawnMover();
        }
        else if (roll < 18)
        {
            DestroyMover();
        }
        else if (roll < 50)
        {
            MoveMovers(Stride.Drift);
        }
        else if (roll < 75)
        {
            MoveMovers(Stride.Climb);
        }
        else if (roll < 87)
        {
            MoveMovers(Stride.Teleport);
        }
        else
        {
            WriteLevels();
        }
    }

    private enum Stride
    {
        // Under the motion tolerance, through a span and an explicit Replicate: nothing need be sent.
        Drift,

        // About a metre and a half in 3D (a flyer climbs; a walker only moves in the plane), through WriteSpatial: may cross a cluster or a cell.
        Climb,

        // Anywhere, through WriteSpatial: a migration and a motion epoch.
        Teleport,
    }

    // Moves one to three movers, chosen by index into the live list so a one-kind run moves the same ones.
    private void MoveMovers(Stride stride)
    {
        var count = _movers == VolumeMovers.Walkers ? _walkers.Count : _flyers.Count;
        if (count == 0)
        {
            return;
        }

        var targets = new HashSet<int>();
        var wanted = 1 + _random.Next(3);
        for (var i = 0; i < wanted; i++)
        {
            targets.Add(_random.Next(count));
        }

        // Every draw is taken whatever the kinds, so both kinds see the same plane moves.
        var moves = new Dictionary<int, (double Dx, double Dy, double Dz, double X, double Y)>();
        foreach (var t in targets)
        {
            var dx = ((_random.NextDouble() * 2.0) - 1.0) * (stride == Stride.Drift ? 0.02 : 1.0);
            var dy = ((_random.NextDouble() * 2.0) - 1.0) * (stride == Stride.Drift ? 0.02 : 1.0);
            var dz = ((_altitude.NextDouble() * 2.0) - 1.0) * (stride == Stride.Drift ? 0.02 : 1.0);
            moves[t] = (dx, dy, dz, Coordinate(), Coordinate());
        }

        if (stride == Stride.Climb)
        {
            Climbs += targets.Count;
        }
        else if (stride == Stride.Teleport)
        {
            Teleports += targets.Count;
        }

        using var tx = Engine.CreateQuickTransaction();
        if (_movers != VolumeMovers.Walkers)
        {
            MoveFlyers(tx, moves, stride);
        }

        if (_movers != VolumeMovers.Flyers)
        {
            MoveWalkers(tx, moves, stride);
        }

        tx.Commit();
    }

    private void MoveFlyers(Transaction tx, Dictionary<int, (double Dx, double Dy, double Dz, double X, double Y)> moves, Stride stride)
    {
        var targets = new Dictionary<ulong, (double Dx, double Dy, double Dz, double X, double Y)>();
        foreach (var (index, move) in moves)
        {
            targets[_flyers[index].RawValue] = move;
        }

        var accessor = tx.For<ProjFlyer>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
                var bounds = stride == Stride.Drift ? cluster.GetSpan(ProjFlyer.Bounds) : default;
#pragma warning restore TYPHON009
                var touched = false;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (!targets.TryGetValue(cluster.GetEntityId(slot).RawValue, out var move))
                    {
                        continue;
                    }

                    var current = cluster.GetReadOnly(ProjFlyer.Bounds, slot).Bounds;
                    var x = (current.MinX + current.MaxX) * 0.5;
                    var y = (current.MinY + current.MaxY) * 0.5;
                    var z = (current.MinZ + current.MaxZ) * 0.5;
                    var next = stride == Stride.Teleport
                        ? Bounds3(Clamp(move.X), Clamp(move.Y), Altitude())
                        : Bounds3(Clamp(x + move.Dx), Clamp(y + move.Dy), ClampAltitude(z + move.Dz));
                    if (stride == Stride.Drift)
                    {
                        bounds[slot] = next;
                        touched = true;
                        _harness.Subscriptions.Commands.Replicate(in cluster, slot);
                    }
                    else
                    {
                        cluster.WriteSpatial(ProjFlyer.Bounds, slot, next);
                    }
                }

                if (touched)
                {
                    cluster.MarkDirty(ProjFlyer.Bounds);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    private void MoveWalkers(Transaction tx, Dictionary<int, (double Dx, double Dy, double Dz, double X, double Y)> moves, Stride stride)
    {
        var targets = new Dictionary<ulong, (double Dx, double Dy, double Dz, double X, double Y)>();
        foreach (var (index, move) in moves)
        {
            targets[_walkers[index].RawValue] = move;
        }

        var accessor = tx.For<ProjCreature>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
#pragma warning disable TYPHON009
                var bounds = stride == Stride.Drift ? cluster.GetSpan(ProjCreature.Bounds) : default;
#pragma warning restore TYPHON009
                var touched = false;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (!targets.TryGetValue(cluster.GetEntityId(slot).RawValue, out var move))
                    {
                        continue;
                    }

                    var current = cluster.GetReadOnly(ProjCreature.Bounds, slot).Bounds;
                    var x = (current.MinX + current.MaxX) * 0.5;
                    var y = (current.MinY + current.MaxY) * 0.5;
                    var next = stride == Stride.Teleport ? Bounds2(Clamp(move.X), Clamp(move.Y)) : Bounds2(Clamp(x + move.Dx), Clamp(y + move.Dy));
                    if (stride == Stride.Drift)
                    {
                        bounds[slot] = next;
                        touched = true;
                        _harness.Subscriptions.Commands.Replicate(in cluster, slot);
                    }
                    else
                    {
                        cluster.WriteSpatial(ProjCreature.Bounds, slot, next);
                    }
                }

                if (touched)
                {
                    cluster.MarkDirty(ProjCreature.Bounds);
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // A new level on a few movers of each kind.
    private void WriteLevels()
    {
        var count = _movers == VolumeMovers.Walkers ? _walkers.Count : _flyers.Count;
        if (count == 0)
        {
            return;
        }

        var index = _random.Next(count);
        var level = (ushort)_random.Next(1, 500);
        using var tx = Engine.CreateQuickTransaction();
        if (_movers != VolumeMovers.Walkers)
        {
            WriteLevel<ProjFlyer>(tx, _flyers[index], ProjFlyer.Ai, level);
        }

        if (_movers != VolumeMovers.Flyers)
        {
            WriteLevel<ProjCreature>(tx, _walkers[index], ProjCreature.Ai, level);
        }

        tx.Commit();
    }

    private void WriteLevel<T>(Transaction tx, EntityId target, Comp<ProjAi> ai, ushort level) where T : Archetype<T>
    {
        var accessor = tx.For<T>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                var occupancy = cluster.OccupancyBits;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (cluster.GetEntityId(slot).RawValue != target.RawValue)
                    {
                        continue;
                    }

#pragma warning disable TYPHON009
                    var span = cluster.GetSpan(ai);
#pragma warning restore TYPHON009
                    span[slot].Level = level;
                    cluster.MarkDirty(ai);
                    _harness.Subscriptions.Commands.Replicate(in cluster, slot);
                    return;
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // ── The viewers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private Vector3D RandomViewpoint()
    {
        var limit = _limit * 0.9;
        var z = _volumetric ? ((_walker.NextDouble() * 2.0) - 1.0) * limit : 0.0;
        return new Vector3D(((_walker.NextDouble() * 2.0) - 1.0) * limit, ((_walker.NextDouble() * 2.0) - 1.0) * limit, z);
    }

    /// <summary>
    /// Every session strides a hundredth of its radius in a random direction — climbing too, in a volumetric world — and now and then teleports.
    /// </summary>
    public void Walk(bool teleports = true)
    {
        var limit = _limit * 0.9;
        for (var i = 0; i < _sessions.Length; i++)
        {
            var roll = _walker.Next(100);
            if (teleports && roll < 3)
            {
                _viewpoints[i] = RandomViewpoint();
                ViewpointTeleports++;
            }
            else if (roll < 90)
            {
                var theta = _walker.NextDouble() * Math.PI * 2.0;
                var phi = _volumetric ? (_walker.NextDouble() - 0.5) * Math.PI : 0.0;
                var stride = _radii[i] * 0.01;
                var v = _viewpoints[i];
                _viewpoints[i] = new Vector3D(
                    Math.Clamp(v.X + (Math.Cos(theta) * Math.Cos(phi) * stride), -limit, limit),
                    Math.Clamp(v.Y + (Math.Sin(theta) * Math.Cos(phi) * stride), -limit, limit),
                    _volumetric ? Math.Clamp(v.Z + (Math.Sin(phi) * stride), -limit, limit) : 0.0);
            }

            _harness.Sessions.SetViewpoint(_sessions[i], _viewpoints[i]);
        }
    }

    // ── Ticks ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Churn, a walk, the fence, the track, and each session's delivery by its skip rate.</summary>
    public void Step(bool churn = true, bool walk = true, bool teleports = true)
    {
        if (churn)
        {
            Churn();
        }

        if (walk && _regionEdge > 0)
        {
            MoveRegions();
        }
        else if (walk)
        {
            Walk(teleports);
        }

        _tick++;
        Engine.WriteTickFence(_tick);
        _harness.RunTick(_tick);
        for (var i = 0; i < _sessions.Length; i++)
        {
            if (_delivery[i].Next(100) >= _skipPercent[i])
            {
                _harness.Deliver(_sessions[i]);
            }
        }
    }

    /// <summary>No churn, no walk, every frame delivered, until nothing is owed.</summary>
    public void Quiesce()
    {
        for (var q = 0; q < QuietTicks; q++)
        {
            _tick++;
            Engine.WriteTickFence(_tick);
            _harness.RunTick(_tick);
            foreach (var session in _sessions)
            {
                _harness.Deliver(session);
            }
        }

        foreach (var session in _sessions)
        {
            Assert.That(_harness.HasFrame(session), Is.False, "a frame is still owed after the quiet window");
        }
    }

    // ── The comparison ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Per session, the spawn orders of the flyers and of the walkers its replica holds: in a <see cref="VolumeMovers.Both"/> run a flyer and a
    /// walker share each spawn order and every plane move, so the two sets are the same exactly when the third axis changes nothing.</summary>
    public List<(SortedSet<int> Flyers, SortedSet<int> Walkers)> HeldMoversBySpawnOrder()
    {
        var truth = ServerTruth(null);
        var result = new List<(SortedSet<int>, SortedSet<int>)>();
        foreach (var session in _sessions)
        {
            var replica = _harness.Replica(session);
            result.Add((Held(replica, _flyerIndex, truth[_flyerIndex]), Held(replica, _creatureIndex, truth[_creatureIndex])));
        }

        return result;

        SortedSet<int> Held(SessionReplica replica, int plan, Dictionary<uint, EntityId> byNetId)
        {
            var held = new SortedSet<int>();
            foreach (var netId in replica.NetIds(_wire[plan]))
            {
                if (byNetId.TryGetValue(netId, out var entity) && _spawnOrder.TryGetValue(entity.RawValue, out var order))
                {
                    held.Add(order);
                }
            }

            return held;
        }
    }

    /// <summary>Per session, the spawn-order indices of every entity its replica holds: comparable across runs, where netIds are not.</summary>
    public List<SortedSet<int>> HeldBySpawnOrder()
    {
        var truth = ServerTruth(null);
        var result = new List<SortedSet<int>>();
        foreach (var session in _sessions)
        {
            var replica = _harness.Replica(session);
            var held = new SortedSet<int>();
            foreach (var (plan, byNetId) in truth)
            {
                foreach (var netId in replica.NetIds(_wire[plan]))
                {
                    if (byNetId.TryGetValue(netId, out var entity) && _spawnOrder.TryGetValue(entity.RawValue, out var order))
                    {
                        held.Add((plan * 1_000_000) + order);
                    }
                }
            }

            result.Add(held);
        }

        return result;
    }

    /// <summary>Asserts every session's replica against its sphere, field for field.</summary>
    public void AssertConverged(string because)
    {
        var divergences = new List<string>();
        if (Push.ShadowIllegal != 0)
        {
            divergences.Add($"the push path published {Push.ShadowIllegal} record(s) a client could not legally apply");
        }

        var truth = ServerTruth(divergences);

        // No identity leaks (SUB-06): live = leased and unspent + one per described entity, and no orphan refused.
        var heldIds = 0L;
        foreach (var byNetId in truth.Values)
        {
            heldIds += byNetId.Count;
        }

        foreach (var state in _harness.Subscriptions.ReplicationStates)
        {
            heldIds += state?.NetIdLeases.LeasedCount ?? 0;
            if (state != null && state.OrphanReleaseFaults != 0)
            {
                divergences.Add($"{state.OrphanReleaseFaults} orphaned identities were refused by the allocator");
            }
        }

        if (_harness.Replication.NetIds.LiveCount != heldIds)
        {
            divergences.Add($"the allocator counts {_harness.Replication.NetIds.LiveCount} identities live and {heldIds} are held: an identity leaked");
        }

        ComparedAtLastPoint = 0;
        RequiredAtLastPoint = 0;
        using (var tx = Engine.CreateQuickTransaction())
        {
            for (var s = 0; s < _sessions.Length; s++)
            {
                var replica = _harness.Replica(_sessions[s]);
                foreach (var (plan, expected) in truth)
                {
                    if (_regionEdge > 0)
                    {
                        CompareRegion(tx, replica, s, plan, expected, divergences);
                    }
                    else
                    {
                        CompareSphere(tx, replica, s, plan, expected, divergences);
                    }
                }

                if (replica.Store.Anomalies != 0)
                {
                    divergences.Add($"session {s}: the decoder recorded {replica.Store.Anomalies} anomalies");
                }
            }
        }

        if (divergences.Count == 0)
        {
            return;
        }

        var report = new StringBuilder();
        report.Append(because).Append(" — the client's world is not the server's at tick ").Append(_tick).Append(':').AppendLine();
        foreach (var d in divergences.GetRange(0, Math.Min(40, divergences.Count)))
        {
            report.Append("  ").AppendLine(d);
        }

        Assert.Fail(report.ToString());
    }

    private Dictionary<int, Dictionary<uint, EntityId>> ServerTruth(List<string> divergences)
    {
        var truth = new Dictionary<int, Dictionary<uint, EntityId>>();
        foreach (var plan in new[] { _flyerIndex, _creatureIndex, _rockIndex })
        {
            var byNetId = new Dictionary<uint, EntityId>();
            var state = _harness.Subscriptions.ReplicationStates[plan];
            var layout = state.Layout;
            foreach (var (chunkId, occupancy) in _harness.Replication.LiveClusters(plan))
            {
                if (!state.Directory.TryGetBlock(chunkId, out var block))
                {
                    continue;
                }

                var live = occupancy;
                while (live != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(live);
                    live &= live - 1;
                    var hot = (ReplicationHotEntry*)((byte*)block + layout.HotOffset + (slot * layout.HotStride));
                    if (hot->NetId == NetIdAllocator.NoNetId)
                    {
                        divergences?.Add($"plan {plan} chunk {chunkId} slot {slot} is live but holds no netId");
                        continue;
                    }

                    if (!byNetId.TryAdd(hot->NetId, hot->Entity))
                    {
                        divergences?.Add($"plan {plan}: netId {hot->NetId} is leased to two live slots at once");
                    }
                }
            }

            truth[plan] = byNetId;
        }

        return truth;
    }

    private (double X, double Y, double Z) TruePosition(Transaction tx, int plan, EntityId entity)
    {
        var reference = tx.Open(entity);
        if (plan == _flyerIndex)
        {
            var b = reference.Read(ProjFlyer.Bounds).Bounds;
            return ((b.MinX + b.MaxX) * 0.5, (b.MinY + b.MaxY) * 0.5, (b.MinZ + b.MaxZ) * 0.5);
        }

        var b2 = plan == _creatureIndex ? reference.Read(ProjCreature.Bounds).Bounds : reference.Read(ProjRock.Bounds).Bounds;
        return ((b2.MinX + b2.MaxX) * 0.5, (b2.MinY + b2.MaxY) * 0.5, 0.0);
    }

    private void CompareSphere(Transaction tx, SessionReplica replica, int session, int plan, Dictionary<uint, EntityId> expected, List<string> divergences)
    {
        var wire = _wire[plan];
        var held = new HashSet<uint>(replica.NetIds(wire));
        var radius = _radii[session];
        var slack = Math.Min(Push.Radius / 48.0, Push.CellSize / 2.0);
        var tolerance = 0.05 + (2.0 * _positionStep) + 0.005;

        // v̂ trails a mover by up to h (09 § 2): held within R − h, dropped past R + h.
        var margin = slack + _harness.Subscriptions.Plans[plan].VisibilitySlackM + tolerance + 0.01;
        var viewpoint = _viewpoints[session];
        foreach (var netId in held)
        {
            if (!expected.ContainsKey(netId))
            {
                divergences.Add($"session {session}: plan {plan} netId {netId} is in the client's world and not in the server's");
            }
        }

        foreach (var (netId, entity) in expected)
        {
            var p = TruePosition(tx, plan, entity);

            // The geometric z: 0 for everything in a flat grid, a 2D archetype's 0 in a deep one (10 § 3.4).
            var dz = _volumetric ? p.Z - viewpoint.Z : 0.0;
            var dx = p.X - viewpoint.X;
            var dy = p.Y - viewpoint.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            var holds = held.Contains(netId);
            if (distance <= radius - margin)
            {
                RequiredAtLastPoint++;
                if (!holds)
                {
                    divergences.Add($"session {session}: plan {plan} netId {netId} (entity {entity.RawValue}) is {distance:F2} m from the viewpoint, inside "
                        + $"the {radius} m sphere, and the client does not hold it");
                    continue;
                }
            }
            else if (distance > radius + margin && holds)
            {
                divergences.Add($"session {session}: plan {plan} netId {netId} is {distance:F2} m from the viewpoint, outside the {radius} m sphere, and the "
                    + "client still holds it");
                continue;
            }

            if (!holds)
            {
                continue;
            }

            ComparedAtLastPoint++;
            ComparePosition(replica, session, plan, netId, p, tolerance, divergences);
            var ai = plan == _flyerIndex ? tx.Open(entity).Read(ProjFlyer.Ai) : plan == _creatureIndex ? tx.Open(entity).Read(ProjCreature.Ai)
                : tx.Open(entity).Read(ProjRock.Ai);
            var field = plan == _rockIndex ? "kind" : "template";
            var template = replica.Value(wire, netId, field);
            if (template == null || (int)template.Value != ai.Template)
            {
                divergences.Add($"session {session}: plan {plan} netId {netId} has {field} {template?.ToString() ?? "none"}, the server {ai.Template}");
            }

            if (plan != _rockIndex)
            {
                var level = replica.Value(wire, netId, "level");
                if (level == null || (int)level.Value != ai.Level)
                {
                    divergences.Add($"session {session}: plan {plan} netId {netId} has level {level?.ToString() ?? "none"}, the server {ai.Level}");
                }
            }
        }
    }

    // Every cell within the margin of a point delivered: v̂'s cell may be a neighbour of the true position's.
    private bool DeliveredAround(int session, (double X, double Y, double Z) p, double margin)
    {
        for (var c = 0; c < 8; c++)
        {
            var x = p.X + ((c & 1) != 0 ? margin : -margin);
            var y = p.Y + ((c & 2) != 0 ? margin : -margin);
            var z = p.Z + ((c & 4) != 0 ? margin : -margin);
            if (!Push.RegionDelivers(_sessions[session], x, y, z))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The number of entities of a plan a session's replica holds.</summary>
    public int HeldCount(int session, string archetype) =>
        _harness.Replica(_sessions[session]).NetIds(_wire[_harness.PlanIndex(archetype)]).Length;

    /// <summary>
    /// The region comparison in 3D (09 § 7): an entity inside every face of the session's hull by more than the margin, in a delivered cell, must be held;
    /// one outside a face by more than it must not be. A 2D archetype lies on the plane z = 0 (10 § 3.4).
    /// </summary>
    private void CompareRegion(Transaction tx, SessionReplica replica, int session, int plan, Dictionary<uint, EntityId> expected, List<string> divergences)
    {
        var wire = _wire[plan];
        var held = new HashSet<uint>(replica.NetIds(wire));
        var hull = _harness.Subscriptions.Ingress.RowOf(_sessions[session]).Region;
        var tolerance = 0.05 + (2.0 * _positionStep) + 0.005;
        var margin = _harness.Subscriptions.Plans[plan].VisibilitySlackM + tolerance + 0.01;
        foreach (var netId in held)
        {
            if (!expected.ContainsKey(netId))
            {
                divergences.Add($"session {session}: plan {plan} netId {netId} is in the client's world and not in the server's");
            }
        }

        foreach (var (netId, entity) in expected)
        {
            var p = TruePosition(tx, plan, entity);
            var outside = double.MinValue;
            for (var f = 0; f < hull.PlaneCount; f++)
            {
                var face = hull.Planes[f];
                outside = Math.Max(outside, (face.Nx * p.X) + (face.Ny * p.Y) + (face.Nz * p.Z) - face.D);
            }

            var holds = held.Contains(netId);
            var required = outside < -margin && (!_budgeted || DeliveredAround(session, p, margin));
            if (required)
            {
                RequiredAtLastPoint++;
                if (!holds)
                {
                    divergences.Add($"session {session}: plan {plan} netId {netId} (entity {entity.RawValue}) at ({p.X:F1}, {p.Y:F1}, {p.Z:F1}) is "
                        + $"{-outside:F2} m inside the region, in a delivered cell, and the client does not hold it");
                    continue;
                }
            }
            else if (outside > margin && holds)
            {
                divergences.Add($"session {session}: plan {plan} netId {netId} is {outside:F2} m outside the region and the client still holds it");
                continue;
            }

            if (holds)
            {
                ComparedAtLastPoint++;
                ComparePosition(replica, session, plan, netId, p, tolerance, divergences);
            }
        }
    }

    private void ComparePosition(SessionReplica replica, int session, int plan, uint netId, (double X, double Y, double Z) truth, double tolerance,
        List<string> divergences)
    {
        double[] actual;
        var wire = _wire[plan];
        if (plan == _rockIndex)
        {
            actual = replica.Position(wire, netId);
            tolerance = (2.0 * _positionStep) + 0.001;
        }
        else
        {
            if (!replica.Store.TryLocate(netId, out _, out var slot))
            {
                divergences.Add($"session {session}: plan {plan} netId {netId} vanished from the replica");
                return;
            }

            var archetype = replica.Store.Archetypes[wire];
            var anchor = archetype.HeadPosition(slot);
            var velocity = archetype.HeadVelocity(slot);
            var elapsed = (double)((uint)_tick - archetype.HeadT0(slot));
            actual = new double[anchor.Length];
            for (var a = 0; a < anchor.Length; a++)
            {
                actual[a] = anchor[a] + (velocity[a] * elapsed);
            }
        }

        double[] expected = actual.Length == 3 ? [truth.X, truth.Y, truth.Z] : [truth.X, truth.Y];
        for (var a = 0; a < expected.Length; a++)
        {
            if (Math.Abs(actual[a] - expected[a]) > tolerance)
            {
                divergences.Add($"session {session}: plan {plan} netId {netId} axis {a} is {actual[a]:F4} on the client and {expected[a]:F4} on the server");
            }
        }
    }

    public void Dispose() => _harness.Dispose();
}
