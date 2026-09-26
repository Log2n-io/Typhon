using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The segment ring and <see cref="MotionEvaluator"/>, case for case against the TypeScript SDK's <c>test/motion.test.ts</c> over the same inputs: the two
/// implementations are a port of one another and must answer "where is this entity at time τ" with the same double.
/// </summary>
[TestFixture]
public class MotionEvaluatorTests
{
    private const int Mover = 0;
    private const int Ghost = 1;
    private const int Flyer = 2;
    private const int Buoy = 3;
    private const int Tower = 4;

    /// <summary>The ring spans the render delay in ticks plus two, never fewer than four and never more than 255.</summary>
    [Test]
    public void TheRingIsSizedFromTheTickPeriod()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SegmentRing.DepthFor(1_000_000), Is.EqualTo(4), "1 Hz: the floor of four entries");
            Assert.That(SegmentRing.DepthFor(100_000), Is.EqualTo(5), "10 Hz");
            Assert.That(SegmentRing.DepthFor(50_000), Is.EqualTo(8), "20 Hz");
            Assert.That(SegmentRing.DepthFor(16_667), Is.EqualTo(20), "60 Hz");
            Assert.That(SegmentRing.DepthFor(1_000), Is.EqualTo(255), "1 kHz: the u8 cap");
            Assert.That(SegmentRing.DepthFor(100_000, 150), Is.EqualTo(4), "a shorter delay than the floor covers");
            Assert.That(SegmentRing.DepthFor(16_667, 150), Is.EqualTo(11), "60 Hz at the clock's lower bound");
        });
    }

    /// <summary>The record is laid out exactly as the TypeScript store lays it out, so the two can be compared field by field.</summary>
    [Test]
    public void TheRecordIsLaidOutPerDimensionCountAndRingSize()
    {
        var world = World();
        Assert.Multiple(() =>
        {
            Assert.That(
                new[] { SegmentRing.SegmentsOffsetFor(4), SegmentRing.RecordBytesFor(2, 4), SegmentRing.RecordBytesFor(3, 4) },
                Is.EqualTo(new[] { 24, 152, 216 }));
            Assert.That(
                new[] { SegmentRing.SegmentsOffsetFor(5), SegmentRing.RecordBytesFor(2, 5), SegmentRing.RecordBytesFor(3, 5) },
                Is.EqualTo(new[] { 32, 192, 272 }));
            Assert.That(SegmentRing.SegmentsOffsetFor(255), Is.EqualTo(1280));
            Assert.That(SegmentRing.RecordBytesFor(2, 8), Is.EqualTo(304), "20 Hz, 2D");
            Assert.That(SegmentRing.RecordBytesFor(3, 8), Is.EqualTo(432), "20 Hz, 3D");
            Assert.That(SegmentRing.RecordBytesFor(2, 20), Is.EqualTo(744), "60 Hz, 2D");
            Assert.That(SegmentRing.RecordBytesFor(3, 20), Is.EqualTo(1064), "60 Hz, 3D");

            var layout = new List<int[]>();
            foreach (var store in world.Archetypes)
            {
                layout.Add([store.Dims, store.MotionStride, store.SegmentHistory, store.Segments.RecordBytes]);
            }

            Assert.That(layout, Is.EqualTo(new[]
            {
                new[] { 2, 4, 5, 192 },
                new[] { 0, 0, 5, 0 },
                new[] { 3, 6, 5, 272 },
                new[] { 2, 4, 5, 192 },
                new[] { 3, 6, 5, 272 },
            }));
        });
    }

    /// <summary>The linear model is <c>p0 + v·(τ − t0)</c>, with the integer ticks subtracted before the fraction is added.</summary>
    [Test]
    public void ALinearSegmentExtrapolates()
    {
        var (world, slot) = MakeMover(1000);
        Assert.That(XAt(world, slot, 1002, 0.5), Is.EqualTo(125).Within(1e-9));
    }

    /// <summary>A segment that has not started yet is not used, and the switch to it is a jump, not a blend: it may be a teleport.</summary>
    [Test]
    public void AnOlderSegmentHoldsUntilRenderTimeReachesTheNextStart()
    {
        var (world, slot) = MakeMover(1000);
        var store = world.Archetypes[Mover];
        store.PushSegment(slot, [-500, -500], [0, 0], 1004, 1);

        Assert.Multiple(() =>
        {
            Assert.That(XAt(world, slot, 1003, 0.99), Is.EqualTo(139.9).Within(1e-9));
            Assert.That(MotionEvaluator.EpochAt(store, slot, 1003), Is.Zero);
            Assert.That(XAt(world, slot, 1004, 0), Is.EqualTo(-500));
            Assert.That(MotionEvaluator.EpochAt(store, slot, 1004), Is.EqualTo(1));
        });
    }

    /// <summary>Render time trails the newest frame, so several segments land before it reaches the first one's start.</summary>
    [Test]
    public void TheRingFindsTheSegmentInForceWhenSeveralArriveFirst()
    {
        var (world, slot) = MakeMover(1000);
        var store = world.Archetypes[Mover];
        store.PushSegment(slot, [110, 50], [-1, 0], 1001, 0);
        store.PushSegment(slot, [109, 50], [0, 2], 1002, 0);
        store.PushSegment(slot, [109, 52], [0, 0], 1003, 0);

        Assert.Multiple(() =>
        {
            Assert.That(XAt(world, slot, 1000, 0.5), Is.EqualTo(105));
            Assert.That(XAt(world, slot, 1001, 0.5), Is.EqualTo(109.5));
            Assert.That(XAt(world, slot, 1003, 0.5), Is.EqualTo(109));
        });
    }

    /// <summary>Wrapping keeps the newest <c>H</c> segments; before all of them, the oldest held is extrapolated backwards.</summary>
    [Test]
    public void TheRingWrapsAndKeepsItsNewestSegments()
    {
        var (world, slot) = MakeMover(1000);
        var store = world.Archetypes[Mover];
        var n = store.SegmentHistory + 1;
        for (var k = 1; k <= n; k++)
        {
            store.PushSegment(slot, [100 + k, 0], [1, 0], (uint)(1000 + (10 * k)), 0);
        }

        Assert.Multiple(() =>
        {
            Assert.That(store.HeadEntry(slot), Is.EqualTo(n % store.SegmentHistory), "the head wrapped");
            Assert.That(store.Segments.Held(slot), Is.EqualTo(store.SegmentHistory), "the ring is full");

            // Segments 2..n remain (t0 1020..); 1015 precedes all of them: the oldest, at t0 1020, backwards.
            Assert.That(XAt(world, slot, 1015, 0), Is.EqualTo(102 - 5));
            Assert.That(XAt(world, slot, 1000 + (10 * n) + 2, 0), Is.EqualTo(100 + n + 2));
        });
    }

    /// <summary>A <c>none</c>-model pair that straddles the wrap interpolates from entry H − 1 to entry 0.</summary>
    [Test]
    public void SamplesInterpolateAcrossTheRingWrap()
    {
        var world = World();
        var store = world.Archetypes[Buoy];
        var slot = Enter(world, Buoy, 1);
        var h = store.SegmentHistory;
        store.ResetMotion(slot, [0, 0], [], 100, 0);
        for (var k = 1; k <= h; k++)
        {
            store.PushSegment(slot, [10 * k, 0], [], (uint)(100 + (2 * k)), 0);
        }

        Assert.Multiple(() =>
        {
            Assert.That(store.HeadEntry(slot), Is.Zero, "the newest sample wrapped into entry 0");
            Assert.That(At(world, Buoy, slot, 100 + (2 * h) - 1, 0), Is.EqualTo(new double[] { (10 * h) - 5, 0, 5, 0 }));
            Assert.That(At(world, Buoy, slot, 100 + (2 * h) - 2, 0.5), Is.EqualTo(new double[] { (10 * h) - 7.5, 0, 5, 0 }));
        });
    }

    /// <summary>Render time older than everything the ring holds evaluates the oldest segment rather than throwing.</summary>
    [Test]
    public void RenderTimeBeyondTheRingDegradesToTheOldestSegment()
    {
        var world = World();
        var moving = world.Archetypes[Mover];
        var sampled = world.Archetypes[Buoy];
        var mover = Enter(world, Mover, 1);
        var buoy = Enter(world, Buoy, 2);
        moving.ResetMotion(mover, [0, 0], [1, 0], 1000, 0);
        sampled.ResetMotion(buoy, [0, 0], [], 1000, 0);
        var pushes = 3 * moving.SegmentHistory;
        for (var k = 1; k <= pushes; k++)
        {
            moving.PushSegment(mover, [k, 0], [1, 0], (uint)(1000 + k), 0);
            sampled.PushSegment(buoy, [k, 0], [], (uint)(1000 + k), 0);
        }

        // The oldest held starts at tick 1000 + pushes − H + 1; render time is 100 ticks before the first segment ever sent.
        var oldest = pushes - moving.SegmentHistory + 1;
        var all = new double[sampled.LiveCount * sampled.MotionStride];

        Assert.Multiple(() =>
        {
            Assert.That(At(world, Mover, mover, 900, 0), Is.EqualTo(new double[] { oldest - (1000 + oldest - 900), 0, 1, 0 }));
            Assert.That(At(world, Buoy, buoy, 900, 0), Is.EqualTo(new double[] { oldest, 0, 0, 0 }));
            Assert.DoesNotThrow(() =>
            {
                MotionEvaluator.EvaluateLive(sampled, 900, 0, all);
                MotionEvaluator.EvaluateLive(moving, 900, 0, new double[moving.LiveCount * moving.MotionStride]);
            });
            Assert.That(all[..2], Is.EqualTo(new double[] { oldest, 0 }));
        });
    }

    /// <summary>Integer ticks are subtracted first, so a tick a float32 cannot hold costs no precision.</summary>
    [Test]
    public void EvaluationStaysExactAtTicksAFloatCouldNotRepresent()
    {
        const uint t0 = 4_000_000_001;
        Assert.That((double)(float)t0, Is.Not.EqualTo((double)t0), "the premise: a float32 cannot hold this tick");
        var (world, slot) = MakeMover(t0);
        Assert.That(XAt(world, slot, t0 + 3, 0.125), Is.EqualTo(100 + (10 * 3.125)));
    }

    /// <summary>Three dimensions: <c>p[3]</c> then <c>v[3]</c>, with the epoch change jumped rather than blended.</summary>
    [Test]
    public void EvaluationWorksInThreeDimensions()
    {
        var world = World();
        var store = world.Archetypes[Flyer];
        var slot = Enter(world, Flyer, 1);
        store.ResetMotion(slot, [1, 2, 3], [0.5, -1, 0.25], 10, 0);
        Assert.That(At(world, Flyer, slot, 12, 0), Is.EqualTo(new double[] { 2, 0, 3.5, 0.5, -1, 0.25 }));

        store.PushSegment(slot, [-10, 20, 30], [0, 0, 1], 12, 1);
        Assert.Multiple(() =>
        {
            Assert.That(At(world, Flyer, slot, 11, 0.5), Is.EqualTo(new double[] { 1.75, 0.5, 3.375, 0.5, -1, 0.25 }));
            Assert.That(At(world, Flyer, slot, 13, 0.5), Is.EqualTo(new double[] { -10, 20, 31.5, 0, 0, 1 }));
        });
    }

    /// <summary>A static archetype holds the position it entered with, at zero velocity, for ever.</summary>
    [Test]
    public void AStaticPositionHolds()
    {
        var world = World();
        var slot = Enter(world, Tower, 1);
        world.Archetypes[Tower].ResetMotion(slot, [5, 6, 7], [], 0, 0);
        Assert.That(At(world, Tower, slot, 1000, 0.5), Is.EqualTo(new double[] { 5, 6, 7, 0, 0, 0 }));
    }

    /// <summary>The <c>none</c> model interpolates between two samples of one epoch, and holds past the newest or across an epoch change.</summary>
    [Test]
    public void TheNoneModelInterpolatesBetweenSamplesOfOneEpoch()
    {
        var world = World();
        var store = world.Archetypes[Buoy];
        var slot = Enter(world, Buoy, 1);
        store.ResetMotion(slot, [0, 0], [], 100, 0);
        Assert.That(At(world, Buoy, slot, 101, 0), Is.EqualTo(new double[] { 0, 0, 0, 0 }), "one sample: it holds");

        store.PushSegment(slot, [10, -20], [], 104, 0);
        Assert.Multiple(() =>
        {
            Assert.That(At(world, Buoy, slot, 102, 0), Is.EqualTo(new double[] { 5, -10, 2.5, -5 }), "halfway between the samples");
            Assert.That(At(world, Buoy, slot, 104, 0.5), Is.EqualTo(new double[] { 10, -20, 0, 0 }), "past the newest: it holds");
        });

        store.PushSegment(slot, [500, 500], [], 106, 1);
        Assert.Multiple(() =>
        {
            Assert.That(At(world, Buoy, slot, 105, 0), Is.EqualTo(new double[] { 10, -20, 0, 0 }), "never interpolated across the epoch change");
            Assert.That(At(world, Buoy, slot, 106, 0), Is.EqualTo(new double[] { 500, 500, 0, 0 }));
        });
    }

    /// <summary>The batch path answers exactly what the per-slot path does, for every model.</summary>
    [Test]
    public void EvaluatingEveryLiveEntityMatchesPerSlotEvaluation()
    {
        var world = World();
        foreach (var archetype in new[] { Mover, Flyer, Buoy })
        {
            var store = world.Archetypes[archetype];
            var dims = store.Dims;
            for (var id = 1; id <= 6; id++)
            {
                var slot = Enter(world, archetype, (uint)((archetype * 100) + id));
                var p = new double[dims];
                var v = new double[dims];
                for (var a = 0; a < dims; a++)
                {
                    p[a] = id + a;
                    v[a] = 0.5;
                }

                store.ResetMotion(slot, p, store.Linear ? v : [], 10, 0);
                if (id % 2 == 0)
                {
                    var p2 = new double[dims];
                    var v2 = new double[dims];
                    for (var a = 0; a < dims; a++)
                    {
                        p2[a] = -id;
                        v2[a] = -1;
                    }

                    store.PushSegment(slot, p2, store.Linear ? v2 : [], 12, (byte)(id % 4 == 0 ? 1 : 0));
                }
            }
        }

        world.BeginFrame();
        Release(world, Mover, 2);
        Release(world, Buoy, 302);

        Assert.Multiple(() =>
        {
            foreach (var archetype in new[] { Mover, Flyer, Buoy })
            {
                var store = world.Archetypes[archetype];
                var stride = store.MotionStride;
                var all = new double[store.LiveCount * stride];
                var one = new double[MotionEvaluator.MaxStride];
                foreach (var tick in new[] { 11, 12, 13 })
                {
                    MotionEvaluator.EvaluateLive(store, tick, 0.25, all);
                    for (var i = 0; i < store.LiveCount; i++)
                    {
                        MotionEvaluator.EvaluateSlot(store, store.Live[i], tick, 0.25, one);
                        Assert.That(all[(i * stride)..((i + 1) * stride)], Is.EqualTo(one[..stride]), $"{store.Plan.Name} live {i} at tick {tick}");
                    }
                }
            }
        });
    }

    /// <summary>An archetype with no position has nothing to evaluate, and says so rather than reading an empty ring.</summary>
    [Test]
    public void EvaluatingAnArchetypeWithoutAPositionThrows()
    {
        var world = World();
        Enter(world, Ghost, 1);
        Assert.That(
            () => MotionEvaluator.EvaluateLive(world.Archetypes[Ghost], 1, 0, new double[4]),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("position"));
    }

    /// <summary>A heading is <c>atan2(u, v)</c>, and a stationary entity keeps the one it had.</summary>
    [Test]
    public void AHeadingComesFromTwoVelocityComponents()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MotionEvaluator.HeadingOf(0, 1, 7), Is.Zero);
            Assert.That(MotionEvaluator.HeadingOf(1, 0, 7), Is.EqualTo(Math.PI / 2).Within(1e-12));
            Assert.That(MotionEvaluator.HeadingOf(0, 0, 7), Is.EqualTo(7));
        });
    }

    /// <summary>Evaluation is what a renderer runs every frame: it must not allocate.</summary>
    [Test]
    public void EvaluationAllocatesNothing()
    {
        var world = World();
        var store = world.Archetypes[Flyer];
        for (uint id = 1; id <= 64; id++)
        {
            var slot = Enter(world, Flyer, id);
            store.ResetMotion(slot, [id, 0, 0], [1, 0, -0.5], 10, 0);
            store.PushSegment(slot, [id, 1, 0], [0, 1, 0], 12, 0);
        }

        var all = new double[store.LiveCount * store.MotionStride];
        var one = new double[MotionEvaluator.MaxStride];
        MotionEvaluator.EvaluateLive(store, 13, 0.5, all);
        MotionEvaluator.EvaluateSlot(store, 0, 13, 0.5, one);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16; i++)
        {
            MotionEvaluator.EvaluateLive(store, 11 + i, 0.25, all);
            MotionEvaluator.EvaluateSlot(store, i, 11 + i, 0.25, one);
            MotionEvaluator.EpochAt(store, i, 11 + i);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
    }

    private static double[] At(WorldStore world, int archetype, int slot, long tick, double frac)
    {
        var store = world.Archetypes[archetype];
        var buffer = new double[MotionEvaluator.MaxStride];
        MotionEvaluator.EvaluateSlot(store, slot, tick, frac, buffer);
        return buffer[..store.MotionStride];
    }

    private static double XAt(WorldStore world, int slot, long tick, double frac) => At(world, Mover, slot, tick, frac)[0];

    private static (WorldStore World, int Slot) MakeMover(uint t0)
    {
        var world = World();
        var slot = Enter(world, Mover, 1);
        // 10 m/tick along +X from (100, 50).
        world.Archetypes[Mover].ResetMotion(slot, [100, 50], [10, 0], t0, 0);
        return (world, slot);
    }

    private static int Enter(WorldStore world, int archetype, uint netId)
    {
        var slot = world.Archetypes[archetype].Allocate(netId);
        world.TryMap(netId, archetype, slot);
        return slot;
    }

    private static void Release(WorldStore world, int archetype, uint netId)
    {
        Assert.That(world.TryLocate(netId, out var found, out var slot), Is.True);
        Assert.That(found, Is.EqualTo(archetype));
        world.Archetypes[archetype].Release(slot, immediate: false);
        world.Unmap(netId);
    }

    /// <summary>The five-archetype world of <c>motion.test.ts</c>, at its 10 Hz tick period.</summary>
    private static WorldStore World() => new(CatalogPlan.Compile(new Catalog
    {
        Protocol = new CatalogProtocolVersion { Major = 3, Minor = 0 },
        App = new CatalogApp { Name = "Motion", Revision = 1 },
        Tick = new CatalogTick { PeriodUs = 100_000, PingHz = 4 },
        Limits = new CatalogLimits { FrameBytes = 262_144, ClientMessageBytes = 1024, ResumeGraceMs = 60_000 },
        SessionKinds = ["viewer"],
        Archetypes =
        [
            Archetype(Mover, "Mover", Motion(2, CatalogPosition.LinearModel)),
            Archetype(Ghost, "Ghost", null),
            Archetype(Flyer, "Flyer", Motion(3, CatalogPosition.LinearModel)),
            Archetype(Buoy, "Buoy", Motion(2, CatalogPosition.NoneModel)),
            Archetype(Tower, "Tower", Static(3)),
        ],
    }));

    private static CatalogArchetype Archetype(int idx, string name, CatalogPosition position) =>
        new() { Idx = idx, Name = name, Groups = [], Fields = [], Position = position };

    private static CatalogPosition Motion(int dims, string model) => new()
    {
        Kind = CatalogPosition.MotionKind,
        Model = model,
        Pos = Pos(dims),
        Vel = model == CatalogPosition.LinearModel
            ? new CatalogCodec { Kind = dims == 2 ? CodecKind.Vel2 : CodecKind.Vel3, Bits = 8, UnitExp = -12 }
            : null,
    };

    private static CatalogPosition Static(int dims) => new() { Kind = CatalogPosition.StaticKind, Pos = Pos(dims) };

    // Realm-framed (typhon.3): the catalog names the kind; width and bounds are a REALM block's.
    private static CatalogCodec Pos(int dims) => new() { Kind = dims == 2 ? CodecKind.Pos2 : CodecKind.Pos3 };
}
