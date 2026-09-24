using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime;
using Typhon.Protocol;

namespace Typhon.Engine.Tests;

/// <summary>
/// Half-space regions (<c>claude/design/Subscriptions/10-phase15-3d-groundwork.md</c> § 6, 1.5.4): one type — the intersection of a hull's half-spaces —
/// a polygon's in a flat world, a polyhedron's in a deep one; the codec from the grid; the 3D hull; the classification primitive.
/// </summary>
[TestFixture]
[NonParallelizable]
class ClientRegion3DTests : TestBase<ClientRegion3DTests>
{
    // ── The hull ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static ClientRegionCommand Hull(params double[] xyz)
    {
        var points = new RegionVertex[xyz.Length / 3];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new RegionVertex { X = xyz[i * 3], Y = xyz[(i * 3) + 1], Z = xyz[(i * 3) + 2] };
        }

        Assert.That(ConvexHull.Build(points, 3, 0f, 0, out var region), Is.EqualTo(ClientRegionOutcome.Accepted));
        Assert.That(region.BuildPlanes(), Is.True);
        return region;
    }

    [Test]
    public void AFrustumsEightCornersGiveSixPlanes()
    {
        // A camera at the origin looking along +x: a near face 2 m wide at 1 m, a far face 200 m wide at 100 m.
        var region = Hull(
            1, -1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1,
            100, -100, -100, 100, 100, -100, 100, 100, 100, 100, -100, 100);

        Assert.Multiple(() =>
        {
            Assert.That(region.VertexCount, Is.EqualTo(8));
            Assert.That(region.PlaneCount, Is.EqualTo(6), "twelve triangles, merged to the frustum's six sides");
            Assert.That(region.Contains(50, 0, 0), Is.True);
            Assert.That(region.Contains(50, 49, 49), Is.True);
            Assert.That(region.Contains(50, 60, 0), Is.False, "outside the side plane at 50 m, whose half-width is 50");
            Assert.That(region.Contains(0.5, 0, 0), Is.False, "in front of the near plane");
            Assert.That(region.Contains(101, 0, 0), Is.False, "beyond the far plane");
        });
    }

    [Test]
    public void ACubeGivesSixPlanesAndATetrahedronFour()
    {
        var cube = Hull(0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0, 0, 0, 10, 10, 0, 10, 10, 10, 10, 0, 10, 10, 5, 5, 5);
        var tetra = Hull(0, 0, 0, 10, 0, 0, 0, 10, 0, 0, 0, 10);

        Assert.Multiple(() =>
        {
            Assert.That((cube.VertexCount, cube.PlaneCount), Is.EqualTo((8, 6)), "the interior point is not on the hull");
            Assert.That((tetra.VertexCount, tetra.PlaneCount), Is.EqualTo((4, 4)));
            Assert.That(tetra.Contains(1, 1, 1), Is.True);
            Assert.That(tetra.Contains(5, 5, 5), Is.False, "past the slanted face x + y + z = 10");
        });
    }

    [Test]
    public void CoplanarOrCollinearPointsHaveNoHull()
    {
        RegionVertex[] coplanar = [new() { X = 0, Y = 0 }, new() { X = 10, Y = 0 }, new() { X = 10, Y = 10 }, new() { X = 0, Y = 10 }, new() { X = 5, Y = 5 }];
        RegionVertex[] collinear = [new() { X = 0 }, new() { X = 1, Y = 1, Z = 1 }, new() { X = 2, Y = 2, Z = 2 }, new() { X = 3, Y = 3, Z = 3 }];
        RegionVertex[] three = [new() { X = 0 }, new() { X = 1 }, new() { Y = 1 }];

        Assert.Multiple(() =>
        {
            Assert.That(ConvexHull.Build(coplanar, 3, 0f, 0, out _), Is.EqualTo(ClientRegionOutcome.Invalid), "a flat square has no volume");
            Assert.That(ConvexHull.Build(collinear, 3, 0f, 0, out _), Is.EqualTo(ClientRegionOutcome.Invalid));
            Assert.That(ConvexHull.Build(three, 3, 0f, 0, out _), Is.EqualTo(ClientRegionOutcome.Invalid), "a deep region needs four points");
        });
    }

    /// <summary>
    /// The classification primitive against the brute force, on 10⁶ random cells over random hulls: Inside exactly when all eight corners are inside;
    /// Outside only when no point of the box is inside (sampled); a box with some corners in and some out is Straddling.
    /// </summary>
    [Test]
    public void TheClassificationAgreesWithABruteForceCornerTest([Values(2, 3)] int dims)
    {
        var random = new Random(1540 + dims);
        var cells = 0;
        var seen = new int[3];
        for (var h = 0; h < 200; h++)
        {
            var points = new RegionVertex[4 + random.Next(13)];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = new RegionVertex { X = random.Next(-100, 100), Y = random.Next(-100, 100), Z = dims == 3 ? random.Next(-100, 100) : 0 };
            }

            if (ConvexHull.Build(points, dims, 0f, 0, out var region) != ClientRegionOutcome.Accepted)
            {
                continue;
            }

            Assert.That(region.BuildPlanes(), Is.True);
            for (var c = 0; c < 5_000; c++, cells++)
            {
                var side = 1 + random.Next(40);
                var x0 = random.Next(-130, 130);
                var y0 = random.Next(-130, 130);
                var z0 = dims == 3 ? random.Next(-130, 130) : 0;
                var z1 = dims == 3 ? z0 + side : 0;
                var overlap = region.Classify(x0, y0, z0, x0 + side, y0 + side, z1);
                seen[(int)overlap]++;
                var corners = 0;
                var inside = 0;
                for (var k = 0; k < 8; k++)
                {
                    corners++;
                    inside += region.Contains(x0 + ((k & 1) * side), y0 + (((k >> 1) & 1) * side), (k & 4) == 0 ? z0 : z1) ? 1 : 0;
                }

                Assert.That(overlap == RegionOverlap.Inside, Is.EqualTo(inside == corners), $"hull {h} cell {c}: Inside is all corners inside");
                if (inside > 0 && inside < corners)
                {
                    Assert.That(overlap, Is.EqualTo(RegionOverlap.Straddling), $"hull {h} cell {c}: some corners in, some out");
                }

                if (overlap == RegionOverlap.Outside)
                {
                    for (var sample = 0; sample < 8; sample++)
                    {
                        var px = x0 + (random.NextDouble() * side);
                        var py = y0 + (random.NextDouble() * side);
                        var pz = dims == 3 ? z0 + (random.NextDouble() * side) : 0;
                        Assert.That(region.Contains(px, py, pz), Is.False, $"hull {h} cell {c}: an Outside box holds an inside point");
                    }
                }
            }
        }

        Assert.That(cells, Is.GreaterThan(500_000), "too few hulls were accepted for the comparison to mean anything");
        Assert.That(seen, Has.All.GreaterThan(1_000), "every classification must occur");
    }

    /// <summary>
    /// In a flat grid a region is its footprint: every plane has n_z = 0, so a cell's z = 0 passes both bounds and a camera's altitude is moot.
    /// </summary>
    [Test]
    public void AFlatRegionIsItsFootprint()
    {
        RegionVertex[] quad = [new() { X = -10, Y = -10 }, new() { X = 10, Y = -10 }, new() { X = 10, Y = 10 }, new() { X = -10, Y = 10 }];
        Assert.That(ConvexHull.Build(quad, 2, 0f, 0, out var region), Is.EqualTo(ClientRegionOutcome.Accepted));
        Assert.That(region.BuildPlanes(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(region.PlaneCount, Is.EqualTo(4));
            for (var i = 0; i < region.PlaneCount; i++)
            {
                Assert.That(region.Planes[i].Nz, Is.Zero);
            }

            Assert.That(region.Classify(-5, -5, 0, 5, 5, 0), Is.EqualTo(RegionOverlap.Inside));
            Assert.That(region.Classify(20, 20, 0, 30, 30, 0), Is.EqualTo(RegionOverlap.Outside));
            Assert.That(region.Classify(5, 5, 0, 15, 15, 0), Is.EqualTo(RegionOverlap.Straddling));
        });
    }

    // ── Through the ingress ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Deep : IDisposable
    {
        private readonly DatabaseEngine _engine;
        private long _tick;

        public Deep(DatabaseEngine engine, Action<SubscriptionsRegistry> declare)
        {
            _engine = engine;
            Registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ClientRegion3DTests" });
            Allocator = new MemoryAllocator(Registry, new MemoryAllocatorOptions { Name = "ClientRegion3DAllocator" });
            var options = new SubscriptionsOptions { MaxSessions = 32, IngressRingBytes = 4096, IngressPoolBudgetBytes = 1L * 1024 * 1024 };
            Subs = new SubscriptionsRegistry(options);
            Subs.Sessions.Kinds("god");
            Subs.Sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Spectator);
            declare(Subs);
            var plans = ProjectionCompiler.Compile(Subs, engine, ProjectionTestSchema.TickPeriodSeconds, largestTickMultiplier: 1);
            var export = CatalogBuilder.Build(Subs, plans, CatalogBuilder.DefaultAppName, appRevision: 0, tickPeriodUs: 10_000, systemNames: [],
                engine.SpatialGrid.Config);
            Plan = CatalogPlan.Compile(export.Canonical);
            SessionTable = new SessionTable("Sessions", Registry.Runtime, Allocator, options, Subs.Sessions.SessionEvents);
            CommandTypes = CommandRegistry.Build(Subs, Plan);
            Pool = new IngressRingPool("IngressRings", Registry.Runtime, Allocator, options);
            Ingress = new SubscriptionsIngress(SessionTable, Subs, CommandTypes, new CommandTypeBuffers(CommandTypes, options.MaxSessions), Pool,
                options.MaxSessions);
        }

        public ResourceRegistry Registry { get; }

        public MemoryAllocator Allocator { get; }

        public SubscriptionsRegistry Subs { get; }

        public CatalogPlan Plan { get; }

        public SessionTable SessionTable { get; }

        public CommandRegistry CommandTypes { get; }

        public IngressRingPool Pool { get; }

        public SubscriptionsIngress Ingress { get; }

        public SubscriptionsContext Context { get; } = new();

        public SessionId Admit()
        {
            var request = new AdmissionRequest("god", null, 0, ReadOnlySpan<byte>.Empty, null, null, null, "harness");
            Assert.That(SessionTable.TryAdmit(Subs.Sessions, request, out var session, out _, out _), Is.True);
            Tick();
            Assert.That(SessionTable.SetProfile(session, "god"), Is.True);
            return session;
        }

        public void Tick()
        {
            Context.Reset(++_tick, workerCount: 1);
            var chunks = Ingress.BeginTick(Context);
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                Ingress.DrainChunk(chunk, chunks);
            }

            Assert.That(Ingress.DrainFaults, Is.Zero, $"the drain threw: {Ingress.LastDrainFault}");
        }

        /// <summary>A tick's drain with nothing around it that allocates — what the allocation test measures.</summary>
        public void TickBare()
        {
            Context.Reset(++_tick, workerCount: 1);
            var chunks = Ingress.BeginTick(Context);
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                Ingress.DrainChunk(chunk, chunks);
            }
        }

        public byte[] RegionMessage(ushort seq, double[] flattened)
        {
            var values = new RecordValues
            {
                [BuiltInCommands.RegionVerticesField] = FieldValue.Of(flattened),
                [BuiltInCommands.RegionAltitudeField] = FieldValue.Of(120.0),
                [BuiltInCommands.RegionBudgetField] = FieldValue.Of(256),
            };

            var buffer = new byte[4096];
            var writer = new WireWriter(buffer);
            CommandsMessage.Write(ref writer, clientTick: 1,
                new List<(MessagePlan, ushort, RecordValues)> { (Plan.CommandByName(BuiltInCommands.ClientRegion), seq, values) });
            return writer.Written.ToArray();
        }

        public byte AckFor(SessionId session, ushort seq)
        {
            foreach (var ack in Ingress.Buffers.Acks.AsSpan())
            {
                if (ack.Session == session.Value && ack.Seq == seq)
                {
                    return ack.Reason;
                }
            }

            return 0;
        }

        public void Dispose()
        {
            Ingress.Dispose();
            Pool.Dispose();
            SessionTable.Dispose();
            Allocator.Dispose();
            Registry.Dispose();
            _engine.Dispose();
        }
    }

    private static void DeclareFlyerRegion(SubscriptionsRegistry subs)
    {
        subs.Archetype<ProjFlyer>(a => a
            .Motion(ProjFlyer.Bounds, m => m.Teleport(ProjectionTestSchema.MaxSpeedMps))
            .Field(ProjFlyer.Ai, x => x.Level, Codec.U16, name: "level"));
        subs.Profile("god", p => p.ClientRegion(4000).Of<ProjFlyer>());
    }

    private static readonly double[] Frustum =
    [
        1, -1, -1, 1, 1, -1, 1, 1, 1, 1, -1, 1,
        100, -100, -100, 100, 100, -100, 100, 100, 100, 100, -100, 100,
    ];

    /// <summary>A deep world's catalog carries a pos3 region of 4–16 points, whose frustum arrives as six planes.</summary>
    [Test]
    public void ADeepWorldTakesAPolyhedron()
    {
        using var deep = new Deep(ProjectionTestSchema.SetupEngine(ServiceProvider, volumetric: true), DeclareFlyerRegion);
        var vertices = Array.Find(deep.Plan.CommandByName(BuiltInCommands.ClientRegion).Body.Fields, f => f.Name == BuiltInCommands.RegionVerticesField);
        Assert.Multiple(() =>
        {
            Assert.That(vertices.Element.Kind, Is.EqualTo(CodecKind.Pos3), "the codec comes from the grid: three axes in a deep world");
            Assert.That(vertices.Codec.MinCount, Is.EqualTo(BuiltInCommands.MinRegionVertices3));
            Assert.That(vertices.Codec.MaxCount, Is.EqualTo(BuiltInCommands.MaxRegionVertices));
        });

        var session = deep.Admit();
        deep.Ingress.OnCommands(session, deep.RegionMessage(1, Frustum));
        deep.Tick();
        var row = deep.Ingress.RowOf(session);
        Assert.Multiple(() =>
        {
            Assert.That(row.HasRegion, Is.True);
            Assert.That(row.Region.Dims, Is.EqualTo(3));
            Assert.That(row.Region.PlaneCount, Is.EqualTo(6));
            Assert.That(row.Region.Contains(50, 0, 0), Is.True);
        });

        Assert.Throws<ArgumentException>(() => deep.RegionMessage(2, new double[17 * 3]), "seventeen points cannot even be written");
    }

    [Test]
    public void ACoplanarRegionIsRefusedAndThePreviousOneKept()
    {
        using var deep = new Deep(ProjectionTestSchema.SetupEngine(ServiceProvider, volumetric: true), DeclareFlyerRegion);
        var session = deep.Admit();
        deep.Ingress.OnCommands(session, deep.RegionMessage(1, Frustum));
        deep.Tick();
        deep.Ingress.OnCommands(session, deep.RegionMessage(2, [0, 0, 5, 10, 0, 5, 10, 10, 5, 0, 10, 5]));
        deep.Tick();

        var row = deep.Ingress.RowOf(session);
        Assert.Multiple(() =>
        {
            Assert.That(row.Region.PlaneCount, Is.EqualTo(6), "the frustum is still in force");
            Assert.That(deep.AckFor(session, 2), Is.EqualTo(AckReasons.RegionInvalid));
        });
    }

    /// <summary>A flat world's regions need no 2D archetype: the codec is the grid's (the refusal that took it from an archetype is gone).</summary>
    [Test]
    public void AFlatRuntimeWithNoTwoDimensionalArchetypeAcceptsRegions()
    {
        using var flat = new Deep(ProjectionTestSchema.SetupEngine(ServiceProvider), DeclareFlyerRegion);
        var vertices = Array.Find(flat.Plan.CommandByName(BuiltInCommands.ClientRegion).Body.Fields, f => f.Name == BuiltInCommands.RegionVerticesField);
        Assert.That(vertices.Element.Kind, Is.EqualTo(CodecKind.Pos2), "a flat world's region is a polygon, whatever its archetypes' codecs");

        var session = flat.Admit();
        flat.Ingress.OnCommands(session, flat.RegionMessage(1, [-100, -100, 100, -100, 100, 100, -100, 100]));
        flat.Tick();
        var row = flat.Ingress.RowOf(session);
        Assert.That((row.HasRegion, row.Region.Dims, row.Region.PlaneCount), Is.EqualTo((true, (byte)2, 4)));
    }

    /// <summary>Ingress allocates nothing: the decode, the 3D hull, the ring record and the drain's planes all live on the stack or in place.</summary>
    [Test]
    public void TheIngressOfARegionAllocatesNothing()
    {
        using var deep = new Deep(ProjectionTestSchema.SetupEngine(ServiceProvider, volumetric: true), DeclareFlyerRegion);
        var session = deep.Admit();
        var messages = new byte[40][];
        for (var i = 0; i < messages.Length; i++)
        {
            messages[i] = deep.RegionMessage((ushort)(i + 1), Frustum);
        }

        // Warm: the first drains JIT and size whatever grows once (measured: 80 B in each of the first four, then nothing).
        const int Warm = 10;
        for (var i = 0; i < Warm; i++)
        {
            deep.Ingress.OnCommands(session, messages[i]);
            deep.TickBare();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = Warm; i < messages.Length; i++)
        {
            deep.Ingress.OnCommands(session, messages[i]);
            deep.TickBare();
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(deep.Ingress.DrainFaults, Is.Zero);
        Assert.That(allocated, Is.Zero, "thirty regions through the ingress, steady state");
        Assert.That(deep.Ingress.RowOf(session).Region.PlaneCount, Is.EqualTo(6));
    }
}
