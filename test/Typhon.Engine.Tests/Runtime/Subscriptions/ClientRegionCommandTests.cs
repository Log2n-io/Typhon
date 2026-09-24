using NUnit.Framework;
using System;
using System.Collections.Generic;
using Typhon.Engine.Internals;
using Typhon.Engine.Tests.Runtime;
using Typhon.Protocol;

namespace Typhon.Engine.Tests;

/// <summary>
/// P1-05 — the built-in <c>ClientRegion</c> at reserved index 0: 3-16 vertices, the convex hull taken on the server, a degenerate footprint answered with
/// <c>ACK REGION_INVALID</c> and the previous region kept, and the profile's <c>maxEdgeM</c> clamp.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hull is a correctness step, not tidying.</b> Vertices arrive quantized over the world grid, and quantization can push a convex polygon concave; a
/// concave footprint turned into half-planes selects the wrong entities with nothing reporting it. Taking the hull of whatever arrived makes the query well
/// defined for every input a client can send (03-wire-protocol § 12 W28).
/// </para>
/// <para>
/// <b>Phase 1 stores the region; Phase 2 queries with it.</b> What is asserted here is the decode, the hull, the refusal and the clamp — everything the engine
/// owes the command before an observer exists to read it.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClientRegionCommandTests : TestBase<ClientRegionCommandTests>
{
    private const double MaxEdgeM = 512;

    /// <summary>The ingress path of a runtime whose profile declares a <c>ClientRegion</c> observer, so the catalog carries the built-in.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly DatabaseEngine _engine;
        private long _tick;

        public Harness(DatabaseEngine engine, Action<SubscriptionsRegistry> declare = null, bool regionCodecFromGrid = false)
        {
            _engine = engine;
            Registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ClientRegionCommandTests" });
            Allocator = new MemoryAllocator(Registry, new MemoryAllocatorOptions { Name = "ClientRegionAllocator" });

            var options = new SubscriptionsOptions { MaxSessions = 32, IngressRingBytes = 4096, IngressPoolBudgetBytes = 1L * 1024 * 1024 };

            Subs = new SubscriptionsRegistry(options);
            Subs.Sessions.Kinds("god");
            Subs.Sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Spectator);
            if (declare != null)
            {
                declare(Subs);
            }
            else
            {
                ProjectionTestSchema.DeclareCreature(Subs);

                // The catalog lists ClientRegion only when a profile declares the observer that reads it (W27), so the profile turns the built-in on.
                Subs.Profile("god", p => p.ClientRegion(MaxEdgeM).Of<ProjCreature>());
            }

            var plans = ProjectionCompiler.Compile(Subs, engine, ProjectionTestSchema.TickPeriodSeconds, largestTickMultiplier: 1);
            var export = CatalogBuilder.Build(Subs, plans, CatalogBuilder.DefaultAppName, appRevision: 0, tickPeriodUs: 10_000, systemNames: [],
                regionCodecFromGrid ? engine.SpatialGrid.Config : null);
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

        public SessionId Admit(string profile = "god")
        {
            var request = new AdmissionRequest("god", null, 0, ReadOnlySpan<byte>.Empty, null, null, null, "harness");
            Assert.That(SessionTable.TryAdmit(Subs.Sessions, request, out var session, out _, out _), Is.True);
            Tick();
            Assert.That(SessionTable.SetProfile(session, profile), Is.True, "the session has to carry a profile for the edge clamp to have a source");
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

        /// <summary>Sends a region as a client would, through the protocol's own writer.</summary>
        public void SendRegion(SessionId session, ushort seq, double[] flattenedVertices, double altitudeM = 120, int budgetKiBps = 256)
        {
            var values = new RecordValues
            {
                [BuiltInCommands.RegionVerticesField] = FieldValue.Of(flattenedVertices),
                [BuiltInCommands.RegionAltitudeField] = FieldValue.Of(altitudeM),
                [BuiltInCommands.RegionBudgetField] = FieldValue.Of(budgetKiBps),
            };

            var commands = new List<(MessagePlan, ushort, RecordValues)>
            {
                (Plan.CommandByName(BuiltInCommands.ClientRegion), seq, values),
            };

            var buffer = new byte[4096];
            var writer = new WireWriter(buffer);
            CommandsMessage.Write(ref writer, clientTick: 1, commands);
            Ingress.OnCommands(session, writer.Written);
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

    private Harness NewHarness() => new(ProjectionTestSchema.SetupEngine(ServiceProvider));

    /// <summary>A quad is accepted, stored on the session, and its vertices come back as a convex hull.</summary>
    [Test]
    public void AQuadIsAcceptedAndStoredOnTheSession()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        harness.SendRegion(session, 1, [-100, -100, 100, -100, 100, 100, -100, 100]);
        harness.Tick();

        var row = harness.Ingress.RowOf(session);
        Assert.That(row, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(row.HasRegion, Is.True, "a well-formed quad has to reach the session");
            Assert.That(row.Region.VertexCount, Is.EqualTo(4), "the hull of a convex quad is that quad");
            Assert.That(row.Region.DoubledArea(), Is.GreaterThan(0), "the hull is counter-clockwise, so its signed area is positive");
            Assert.That(row.Region.BudgetKiBps, Is.EqualTo(256));
            Assert.That(row.Region.AltitudeM, Is.EqualTo(120f).Within(0.5f), "altitude travels as an f16, so it arrives to about a part in a thousand");
            Assert.That(harness.Ingress.Buffers.Acks.Count, Is.Zero, "an accepted region is not acknowledged; only a refusal is");
        });
    }

    /// <summary>Sixteen vertices are accepted — the wire's ceiling, and the shape a horizon-clipped frustum can reach.</summary>
    [Test]
    public void SixteenVerticesAreAccepted()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        var vertices = new double[BuiltInCommands.MaxRegionVertices * 2];
        for (var i = 0; i < BuiltInCommands.MaxRegionVertices; i++)
        {
            var angle = 2 * Math.PI * i / BuiltInCommands.MaxRegionVertices;
            vertices[i * 2] = 200 * Math.Cos(angle);
            vertices[(i * 2) + 1] = 200 * Math.Sin(angle);
        }

        harness.SendRegion(session, 1, vertices);
        harness.Tick();

        var row = harness.Ingress.RowOf(session);
        Assert.That(row.HasRegion, Is.True);
        Assert.That(row.Region.VertexCount, Is.EqualTo(BuiltInCommands.MaxRegionVertices), "a convex 16-gon is its own hull");
    }

    /// <summary>A concave footprint is replaced by its convex hull, so the half-plane query it feeds is well defined.</summary>
    [Test]
    public void AConcaveFootprintIsReplacedByItsHull()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        // An arrowhead: four points of which one is inside the triangle the other three make.
        harness.SendRegion(session, 1, [-100, -100, 100, -100, 0, 100, 0, -50]);
        harness.Tick();

        var row = harness.Ingress.RowOf(session);
        Assert.Multiple(() =>
        {
            Assert.That(row.HasRegion, Is.True);
            Assert.That(row.Region.VertexCount, Is.EqualTo(3), "the interior point is not on the hull");
            Assert.That(row.Region.DoubledArea(), Is.GreaterThan(0));
        });
    }

    /// <summary>A hull with no area is refused with <c>REGION_INVALID</c>, and the region already in force is kept.</summary>
    [Test]
    public void ACollinearFootprintIsRefusedAndThePreviousRegionIsKept()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        harness.SendRegion(session, 1, [-100, -100, 100, -100, 100, 100, -100, 100]);
        harness.Tick();
        var accepted = harness.Ingress.RowOf(session).Region;

        harness.SendRegion(session, 2, [0, 0, 10, 10, 20, 20]);
        harness.Tick();

        var row = harness.Ingress.RowOf(session);
        Assert.Multiple(() =>
        {
            Assert.That(row.HasRegion, Is.True);
            Assert.That(row.Region.VertexCount, Is.EqualTo(accepted.VertexCount), "a refused region must not replace the one in force");
            Assert.That(row.Region.DoubledArea(), Is.EqualTo(accepted.DoubledArea()));
        });

        Assert.That(AckFor(harness, session, seq: 2), Is.EqualTo(AckReasons.RegionInvalid),
            "a footprint with no area owes the client an ACK REGION_INVALID, or it goes on sending one that does nothing");
    }

    /// <summary>Three identical points collapse to one, which is fewer than three, so the region is refused rather than accepted as a dot.</summary>
    [Test]
    public void AFootprintThatCollapsesBelowThreePointsIsRefused()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        harness.SendRegion(session, 7, [50, 50, 50, 50, 50, 50]);
        harness.Tick();

        Assert.Multiple(() =>
        {
            Assert.That(harness.Ingress.RowOf(session).HasRegion, Is.False, "there was never a region to keep, so there is none");
            Assert.That(AckFor(harness, session, seq: 7), Is.EqualTo(AckReasons.RegionInvalid));
        });
    }

    /// <summary>
    /// Fewer than three vertices never reaches the engine at all: the count is bounded by the codec, in both directions.
    /// </summary>
    /// <remarks>
    /// The bound is asserted on the catalog the engine emits rather than by feeding the decoder a hand-built message. The decode-side refusal — a <c>list</c>
    /// count outside <c>[minCount, maxCount]</c> answered with 1007 — is <c>Typhon.Protocol</c>'s own property and is pinned by its <c>wire-refusals</c> golden
    /// vectors; re-asserting it here with bytes this fixture wrote itself would be a second spelling of the wire, green in the same build as a red decoder.
    /// What IS this slice's property is that the bound the engine publishes is the built-in's, so no client can form a degenerate region in the first place.
    /// </remarks>
    [Test]
    public void FewerThanThreeVerticesCannotBeFormedAtAll()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        // By name, not by ordinal: canonicalization orders a body's fields ordinally, so the vertices are last rather than first (W11).
        var vertices = Array.Find(harness.Plan.CommandByName(BuiltInCommands.ClientRegion).Body.Fields, f => f.Name == BuiltInCommands.RegionVerticesField);
        Assert.Multiple(() =>
        {
            Assert.That(vertices, Is.Not.Null);
            Assert.That(vertices.Kind, Is.EqualTo(CodecKind.List));
            Assert.That(vertices.Codec.MinCount, Is.EqualTo(BuiltInCommands.MinRegionVertices), "three points is the fewest a footprint can have");
            Assert.That(vertices.Codec.MaxCount, Is.EqualTo(BuiltInCommands.MaxRegionVertices), "sixteen is the ceiling the wire declares");
            Assert.That(vertices.Element.Kind, Is.EqualTo(CodecKind.Pos2),
                "vertices quantize exactly like an archetype's position, so a decoded region can never leave the world");
        });

        var ex = Assert.Throws<ArgumentException>(() => harness.SendRegion(session, 1, [0, 0, 10, 10]),
            "an SDK cannot even encode a two-point footprint, which is where a client learns it rather than at a close code");
        Assert.That(ex.Message, Does.Contain("3..16"));
    }

    /// <summary>A footprint larger than the profile allows is scaled about its centre, not refused: a client looking too far is not a client in error.</summary>
    [Test]
    public void AFootprintWiderThanMaxEdgeIsClampedAboutItsCentre()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        // 4 000 m across, against the profile's 512 m ceiling.
        harness.SendRegion(session, 1, [-2000, -2000, 2000, -2000, 2000, 2000, -2000, 2000]);
        harness.Tick();

        var region = harness.Ingress.RowOf(session).Region;
        double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        for (var i = 0; i < region.VertexCount; i++)
        {
            minX = Math.Min(minX, region.Vertices[i].X);
            maxX = Math.Max(maxX, region.Vertices[i].X);
            minZ = Math.Min(minZ, region.Vertices[i].Y);
            maxZ = Math.Max(maxZ, region.Vertices[i].Y);
        }

        Assert.Multiple(() =>
        {
            Assert.That(maxX - minX, Is.EqualTo(MaxEdgeM).Within(1.0), "the footprint's extent is clamped to the profile's ceiling");
            Assert.That(maxZ - minZ, Is.EqualTo(MaxEdgeM).Within(1.0));
            Assert.That((minX + maxX) / 2, Is.EqualTo(0).Within(1.0), "clamping scales about the centre, so the client keeps looking where it was looking");
            Assert.That(region.VertexCount, Is.EqualTo(4), "clamping changes the size, never the shape");
        });
    }

    /// <summary>A footprint inside the ceiling is left exactly as the client sent it.</summary>
    [Test]
    public void AFootprintInsideMaxEdgeIsNotTouched()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        harness.SendRegion(session, 1, [-100, -100, 100, -100, 100, 100, -100, 100]);
        harness.Tick();

        var region = harness.Ingress.RowOf(session).Region;
        double maxX = double.MinValue;
        for (var i = 0; i < region.VertexCount; i++)
        {
            maxX = Math.Max(maxX, region.Vertices[i].X);
        }

        // The quantizer's step over the grid is sub-millimetre, so the value comes back to within it and not merely "about right".
        Assert.That(maxX, Is.EqualTo(100).Within(0.01), "a region within the ceiling must be the client's own, quantization aside");
    }

    /// <summary>Latest wins: two regions in one tick leave the newer one in force.</summary>
    [Test]
    public void TheNewestRegionOfATickWins()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        harness.SendRegion(session, 1, [-100, -100, 100, -100, 100, 100, -100, 100]);
        harness.SendRegion(session, 2, [-10, -10, 10, -10, 10, 10, -10, 10]);
        harness.Tick();

        var region = harness.Ingress.RowOf(session).Region;
        double maxX = double.MinValue;
        for (var i = 0; i < region.VertexCount; i++)
        {
            maxX = Math.Max(maxX, region.Vertices[i].X);
        }

        Assert.That(maxX, Is.EqualTo(10).Within(0.01), "the region is latest-wins, and the drain applies them in arrival order");
    }

    /// <summary>A region is never delivered as a typed command: the engine interprets it itself.</summary>
    [Test]
    public void TheRegionIsNotDeliveredAsATypedCommand()
    {
        using var harness = NewHarness();
        var session = harness.Admit();

        var info = harness.CommandTypes.ByWireIdx(BuiltInCommands.ClientRegionIdx);
        Assert.Multiple(() =>
        {
            Assert.That(info, Is.Not.Null, "the catalog declares the built-in, so the registry has to bind it");
            Assert.That(info.IsClientRegion, Is.True);
            Assert.That(harness.Ingress.Buffers.ByWireIdx(BuiltInCommands.ClientRegionIdx), Is.Null,
                "there is no application struct behind the built-in, so there is no typed buffer for it");
        });

        harness.SendRegion(session, 1, [-100, -100, 100, -100, 100, 100, -100, 100]);
        harness.Tick();
        Assert.That(harness.Ingress.RowOf(session).HasRegion, Is.True);
    }

    /// <summary>The reason code of the <c>ACK</c> this session received for a sequence number, or zero when there is none.</summary>
    private static byte AckFor(Harness harness, SessionId session, ushort seq)
    {
        foreach (var ack in harness.Ingress.Buffers.Acks.AsSpan())
        {
            if (ack.Session == session.Value && ack.Seq == seq)
            {
                return ack.Reason;
            }
        }

        return 0;
    }
}
