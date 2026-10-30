using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Numerics;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime.Subscriptions.Oracle;

/// <summary>
/// A whole-world session filling a large world while much of it moves every tick and some of it dies and respawns — the SWG god camera's workload,
/// scaled down. The fill must converge: the client ends up holding the world, with no RESET along the way.
/// </summary>
[TestFixture]
[NonParallelizable]
class PushWorldChurnTests : TestBase<PushWorldChurnTests>
{
    private const string Profile = "god";

    // The god camera given a fill budget of its own: ten engine budgets.
    private const string FastProfile = "god-fast";
    private const int FastFill = 10 * EnterBudget;

    // The god camera under --god-region: a ClientRegion profile, sent a square region as wide as a flat grid of 256 m cells allows.
    private const string RegionProfile = "god-region";
    private const double RegionHalfM = 6000d;

    private sealed class World
    {
        public readonly List<EntityId> Creatures = [];
        public readonly Random Random;
        public World(int seed) => Random = new Random(seed);
    }

    private static FrameHarness Harness(DatabaseEngine dbe, string name, int frameBytes, int enterBudget)
    {
        var harness = FrameHarness.Create(dbe, subs =>
        {
            ProjectionTestSchema.DeclareCreature(subs);
            subs.Profile(Profile, p => p.World().Of<ProjCreature>());
            subs.Profile(FastProfile, p => p.World().Fill(FastFill).Of<ProjCreature>());
            subs.Profile(RegionProfile, p => p.ClientRegion(2d * RegionHalfM).Of<ProjCreature>());
            subs.Event<ProjNotice>(e => e.Broadcast());
        }, name, new SubscriptionsOptions
        {
            FrameBytes = frameBytes,
            EnterBudgetPerFrame = enterBudget,
            CheckFrameMeterForTest = true,
            IngressBytesPerSecond = TestIngress.Budget,
            ReplicationCellM = 256d,
            PushShadow = true,
            MaxSessions = 16,
            StatePoolBudgetBytes = 64L * 1024 * 1024,
            FramePoolBudgetBytes = 64L * 1024 * 1024,
        });
        harness.RunFence = true;
        harness.DrainNetIds = true;
        return harness;
    }

    /// <summary>
    /// The longest run of frames that began with updates owed and carried none of them: an owed update's staleness, in frames, beyond the round robin's turn.
    /// </summary>
    private sealed class Starvation
    {
        private readonly HashSet<uint> _owed = [];
        private int _run;

        public int Longest { get; private set; }

        /// <summary>Notes, before the tick, which updates the session owes: the owed set past its enters.</summary>
        public void Before(FrameHarness harness, SessionId session)
        {
            _owed.Clear();
            var state = harness.StateOf(session);
            for (var i = state?.OwedEnters ?? 0; i < (state?.OwedCount ?? 0); i++)
            {
                _owed.Add(state.Owed[i].NetId);
            }
        }

        /// <summary>Delivers the tick's frame and notes whether it carried one of the updates owed when it began.</summary>
        public void Deliver(FrameHarness harness, SessionId session)
        {
            foreach (var log in harness.DeliverLogged(session))
            {
                var carried = false;
                foreach (var netId in log.Segments)
                {
                    carried |= _owed.Contains(netId);
                }

                foreach (var (netId, _) in log.States)
                {
                    carried |= _owed.Contains(netId);
                }

                _run = _owed.Count > 0 && !carried ? _run + 1 : 0;
                Longest = Math.Max(Longest, _run);
            }
        }
    }

    private static ProjBounds At(float x, float y) => new()
    {
        Bounds = new AABB2F { MinX = x - 0.5f, MinY = y - 0.5f, MaxX = x + 0.5f, MaxY = y + 0.5f },
        Speed = 1f,
    };

    private const float Limit = ProjectionTestSchema.WorldExtentM - 64f;

    private static float Coordinate(Random random) => (float)(((random.NextDouble() * 2.0) - 1.0) * Limit);

    private static void Spawn(DatabaseEngine dbe, World world, int count)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count; i++)
        {
            var ai = new ProjAi { Template = (byte)(1 + world.Random.Next(200)), Mode = (ProjAiMode)(1 + world.Random.Next(4)), Alerted = 1 };
            world.Creatures.Add(tx.Spawn<ProjCreature>(ProjCreature.Bounds.Set(At(Coordinate(world.Random), Coordinate(world.Random))),
                ProjCreature.Ai.Set(in ai), ProjCreature.Vitals.Set(new ProjVitals { Health = 10, MaxHealth = 20 })));
        }

        tx.Commit();
    }

    private static void Destroy(DatabaseEngine dbe, World world, int count)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < count && world.Creatures.Count > 0; i++)
        {
            var index = world.Random.Next(world.Creatures.Count);
            tx.Destroy(world.Creatures[index]);
            world.Creatures[index] = world.Creatures[^1];
            world.Creatures.RemoveAt(world.Creatures.Count - 1);
        }

        tx.Commit();
    }

    // A fraction of the creatures walk a stride in a fresh direction: a new segment each tick, as a swoop or a hunting creature turns.
    private static void Walk(DatabaseEngine dbe, World world, double fraction, float stride)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ProjCreature>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var occupancy = cluster.OccupancyBits;
            while (occupancy != 0)
            {
                var slot = BitOperations.TrailingZeroCount(occupancy);
                occupancy &= occupancy - 1;
                if (world.Random.NextDouble() >= fraction)
                {
                    continue;
                }

                var heading = world.Random.NextDouble() * Math.PI * 2;
                var b = cluster.GetReadOnly(ProjCreature.Bounds, slot).Bounds;
                var x = Math.Clamp(((b.MinX + b.MaxX) * 0.5f) + (float)(Math.Cos(heading) * stride), -Limit, Limit);
                var y = Math.Clamp(((b.MinY + b.MaxY) * 0.5f) + (float)(Math.Sin(heading) * stride), -Limit, Limit);
                cluster.WriteSpatial(ProjCreature.Bounds, slot, At(x, y));
            }
        }

        tx.Commit();
    }

    private const int FrameBytes = 8192;
    private const int EnterBudget = 16;

    // The cap on owed enters (15 § 4.3): four enter budgets.
    private const int OwedCap = 4 * EnterBudget;

    private static void AssertLegal(FrameHarness harness, SessionId session)
    {
        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.MeterViolations, Is.Zero, "the meter bounds every record it let through");
            Assert.That(harness.Assembler.OversizeSkips, Is.Zero, "no frame was refused for its size");
            Assert.That(harness.LargestFrame, Is.LessThanOrEqualTo(FrameBytes), "every frame within the limit");
            Assert.That(harness.Replica(session).Store.Anomalies, Is.Zero);
            Assert.That(harness.Subscriptions.Push.ShadowIllegal, Is.Zero);
        });
    }

    /// <summary>
    /// The SWG god camera's workload: a whole-world session fills a world two fifths of which turn every tick — more updates than a frame carries once
    /// the session holds a fraction of it — while creatures die and spawn. On main the owed set passed its cap every few seconds and each time the session
    /// was RESET and refilled from nothing, for ever. The updates are carried round robin instead, the enters ahead of them: the fill completes, nothing is
    /// RESET, and once the world stands still the client holds it as it is.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void AWholeWorldOfMoversOutrunningTheFrameFillsWithoutAReset()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var world = new World(1509);
        Spawn(dbe, world, 3000);
        using var harness = Harness(dbe, nameof(AWholeWorldOfMoversOutrunningTheFrameFillsWithoutAReset), FrameBytes, EnterBudget);
        var session = harness.OpenSessions(1, Profile)[0];
        var plan = harness.PlanIndex(nameof(ProjCreature));
        var tick = 0L;
        var completedUnderLoad = false;
        var largest = 0;
        var starvation = new Starvation();
        for (var t = 0; t < 150; t++)
        {
            Walk(dbe, world, 0.4, 1.5f);
            Destroy(dbe, world, 2);
            Spawn(dbe, world, 2);
            starvation.Before(harness, session);
            harness.RunTick(++tick);
            starvation.Deliver(harness, session);
            completedUnderLoad |= harness.StateOf(session).ViewComplete;
            largest = Math.Max(largest, harness.Assembler.OwedLargest);
        }

        var owing = harness.Assembler.FramesOwing;
        for (var t = 0; t < 60; t++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
        }

        var replica = harness.Replica(session);
        var misplaced = 0;
        foreach (var creature in world.Creatures)
        {
            var netId = harness.NetIdOf(creature);
            var held = netId != 0 ? replica.Position(plan, netId) : null;
            using var tx = dbe.CreateQuickTransaction();
            var b = tx.Open(creature).Read(ProjCreature.Bounds).Bounds;
            if (held == null || Math.Abs(held[0] - ((b.MinX + b.MaxX) * 0.5)) > 0.1 || Math.Abs(held[1] - ((b.MinY + b.MaxY) * 0.5)) > 0.1)
            {
                misplaced++;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(owing, Is.GreaterThan(50), "the movers' updates outran the frame: records were carried");
            Assert.That(harness.Assembler.OwedResets, Is.Zero, "carried, never repaired by a RESET");
            Assert.That(harness.Subscriptions.Push.Resets, Is.Zero);
            Assert.That(completedUnderLoad, Is.True, "the fill completes while the world still moves");
            Assert.That(harness.StateOf(session).OwedCount, Is.Zero, "a standing world drains what it owes");
            Assert.That(largest, Is.GreaterThan(OwedCap), "the owed updates, uncapped, passed the enters' cap: the gauge showed the backlog");
            Assert.That(starvation.Longest, Is.Zero, "every frame that began owing updates carried some of them: the round robin turned under load");
            Assert.That(harness.Assembler.OwedLargest, Is.Zero, "and shows none once drained");
            Assert.That(harness.StateOf(session).ViewComplete, Is.True);
            Assert.That(replica.NetIds(plan), Has.Length.EqualTo(world.Creatures.Count), "the client holds the whole world");
            Assert.That(misplaced, Is.Zero, "and every creature where it stands: no update was lost");
        });
        AssertLegal(harness, session);
    }

    /// <summary>
    /// A World observer with a fill budget of its own (<c>Fill</c>): a still world fills in a fraction of the frames the engine's budget takes, each frame
    /// within its limit, owing nothing — the frame's byte allowance paces it all the same — and no RESET.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-36")]
    [VerifiesRule("SUB-22")]
    public void AWorldWithAFillOfItsOwnFillsInAFractionOfTheFrames()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var world = new World(1511);
        Spawn(dbe, world, 2000);
        using var harness = Harness(dbe, nameof(AWorldWithAFillOfItsOwnFillsInAFractionOfTheFrames), FrameBytes, EnterBudget);
        var sessions = new[] { harness.OpenSessions(1, Profile)[0], harness.OpenSessions(1, FastProfile)[0] };
        var plan = harness.PlanIndex(nameof(ProjCreature));
        var frames = new int[2];
        var tick = 0L;
        for (var t = 0; t < 300 && harness.StateOf(sessions[0]) is not { ViewComplete: true }; t++)
        {
            harness.RunTick(++tick);
            for (var s = 0; s < 2; s++)
            {
                harness.Deliver(sessions[s]);
                frames[s] += harness.StateOf(sessions[s]) is { ViewComplete: true } ? 0 : 1;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Replica(sessions[0]).NetIds(plan), Has.Length.EqualTo(2000));
            Assert.That(harness.Replica(sessions[1]).NetIds(plan), Has.Length.EqualTo(2000));
            Assert.That(frames[1] * 4, Is.LessThan(frames[0]), $"the own fill took {frames[1]} frames against the engine budget's {frames[0]}");
            Assert.That(harness.Assembler.OwedResets, Is.Zero);
        });
        AssertLegal(harness, sessions[0]);
        AssertLegal(harness, sessions[1]);
    }

    /// <summary>
    /// Review 8, M1: a fill of its own pace under a stream of events. The fill takes half the frame's limit and the events the other half, so the entity
    /// records' target is about what the fill alone takes; with the enters ahead of the updates, every update was owed every frame, and the entities the
    /// client held froze while it filled. While updates are owed, the enters take at most half of what is left: every frame carries some updates.
    /// </summary>
    [Test]
    [VerifiesRule("SUB-36")]
    public void UpdatesOwedUnderAFillAndAStreamOfEventsAreNeverStarved()
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var world = new World(1512);
        Spawn(dbe, world, 6000);
        using var harness = Harness(dbe, nameof(UpdatesOwedUnderAFillAndAStreamOfEventsAreNeverStarved), FrameBytes, EnterBudget);
        var session = harness.OpenSessions(1, FastProfile)[0];
        var starvation = new Starvation();
        var tick = 0L;
        var filling = 0;
        var owing = 0;
        for (var t = 0; t < 60; t++)
        {
            Walk(dbe, world, 0.4, 1.5f);
            for (var e = 0; e < 1000; e++)
            {
                harness.Subscriptions.Commands.Emit(new ProjNotice { Seq = e, Kind = 1 });
            }

            starvation.Before(harness, session);
            harness.RunTick(++tick);
            var state = harness.StateOf(session);
            filling += state is { ViewComplete: false } ? 1 : 0;
            owing += state != null && state.OwedCount > state.OwedEnters && !state.ViewComplete ? 1 : 0;
            starvation.Deliver(harness, session);
        }

        Assert.Multiple(() =>
        {
            Assert.That(filling, Is.GreaterThan(10), "the session filled for many frames");
            Assert.That(owing, Is.GreaterThan(5), "while owing updates");
            Assert.That(starvation.Longest, Is.Zero, "and every frame that began owing updates carried some of them");
            Assert.That(harness.Assembler.OwedResets, Is.Zero);
        });
        AssertLegal(harness, session);
    }

    private static int InRegion(DatabaseEngine dbe, World world)
    {
        using var tx = dbe.CreateQuickTransaction();
        var inside = 0;
        foreach (var creature in world.Creatures)
        {
            var b = tx.Open(creature).Read(ProjCreature.Bounds).Bounds;
            inside += Math.Abs((b.MinX + b.MaxX) * 0.5) < RegionHalfM && Math.Abs((b.MinY + b.MaxY) * 0.5) < RegionHalfM ? 1 : 0;
        }

        return inside;
    }

    /// <summary>
    /// A World or ClientRegion fill meets cells whose entities all had an event this tick — a world spawned at once, met by a fresh session. The event step
    /// enters them, and the fill meters them all the same: on main it metered only the others, walked the whole world in one frame, and owed thousands of
    /// enters past the cap — a RESET before the session had held anything.
    /// </summary>
    /// <param name="region">A ClientRegion session over the whole world rather than a World one.</param>
    [Test]
    [VerifiesRule("SUB-36")]
    public void AWorldFillPacesTheMoversOfTheCellsItDelivers([Values(false, true)] bool region)
    {
        var dbe = ProjectionTestSchema.SetupEngine(ServiceProvider);
        var world = new World(1510);
        Spawn(dbe, world, 2000);
        using var harness = Harness(dbe, nameof(AWorldFillPacesTheMoversOfTheCellsItDelivers), FrameBytes, EnterBudget);
        var session = harness.OpenSessions(1, region ? RegionProfile : Profile)[0];
        if (region)
        {
            const double E = RegionHalfM;
            Assert.That(harness.Subscriptions.Ingress.SetRegionForTest(session,
                [new RegionVertex { X = -E, Y = -E }, new RegionVertex { X = E, Y = -E }, new RegionVertex { X = E, Y = E }, new RegionVertex { X = -E, Y = E }], 2),
                Is.True);
        }

        var plan = harness.PlanIndex(nameof(ProjCreature));
        var mostOwed = 0;
        var tick = 0L;
        for (var t = 0; t < 250 && harness.StateOf(session) is not { ViewComplete: true }; t++)
        {
            harness.RunTick(++tick);
            harness.Deliver(session);
            mostOwed = Math.Max(mostOwed, harness.StateOf(session).OwedEnters);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Assembler.OwedResets, Is.Zero);
            Assert.That(mostOwed, Is.LessThanOrEqualTo(OwedCap), "the fill never owes past the cap");
            Assert.That(harness.StateOf(session).ViewComplete, Is.True);
            Assert.That(harness.Replica(session).NetIds(plan), Has.Length.EqualTo(region ? InRegion(dbe, world) : 2000));
        });
        AssertLegal(harness, session);
    }
}
