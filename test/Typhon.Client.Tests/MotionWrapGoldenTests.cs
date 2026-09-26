using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The second cross-language motion pin: the two cases <c>stream-kitchen-sink</c> cannot reach — a segment ring that wraps, and a motion epoch that changes
/// mid-stream — as a stream of their own (<c>stream-motion</c>) and the evaluations it must produce (<c>motion-eval-wrap</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second stream rather than more frames on the first.</b> The kitchen-sink stream is four frames and every other suite pins its bytes; a ring eight
/// deep needs seventeen segments to turn twice, and an epoch change needs samples either side of it. Extending that vector would move bytes
/// <c>golden-stream</c>, <c>motion-eval</c> and their TypeScript twins already commit. This one is deliberately thin instead — no <c>STATS</c>, no
/// <c>AGG</c>, no events, no owner state, one <c>ENTITIES</c> block per archetype per frame — so what it pins is motion and nothing else.
/// </para>
/// <para>
/// <b>What each entity is for.</b> <c>Buoy</c> 10 (<c>model: none</c>) takes a sample every frame for seventeen frames, so the depth-8 ring turns twice and
/// the oldest segment held is far younger than the stream's start. Its samples follow <c>y = x²/4</c>, so no two intervals share a velocity and a straight
/// line between samples is visibly wrong — an implementation that interpolated against the wrong pair, or extrapolated, lands somewhere else.
/// <c>Buoy</c> 11 and <c>Drone</c> 100 both take a teleport mid-stream: the <c>none</c> model must hold its sample across the change rather than interpolate
/// into the far side, and the <c>linear</c> model must keep extrapolating the segment in force rather than reach for the post-teleport one.
/// </para>
/// </remarks>
[TestFixture]
public class MotionWrapGoldenTests
{
    private const string StreamName = "stream-motion";
    private const string VectorName = "motion-eval-wrap";

    /// <summary>The kitchen-sink catalog's 50 ms period under the 300 ms default render-delay cap: ⌈300/50⌉ + 2.</summary>
    private const int RingDepth = 8;

    /// <summary>The first frame's tick; the stream runs one frame per tick from here.</summary>
    private const uint FirstTick = 2000;

    /// <summary>Frames in the stream: an enter plus sixteen samples for Buoy 10, which turns a depth-8 ring exactly twice.</summary>
    private const int FrameCount = 17;

    /// <summary>The frame the teleporting Buoy and Drone enter on; their epoch changes two frames later.</summary>
    private const int EpochEnterFrame = 12;

    private const string StreamDescription =
        "Seventeen TICK frames against catalog-kitchen-sink, each framed as u32 length + message, with the replica after each. Motion only: no STATS, no AGG, "
        + "no events, no owner state. Buoy 10 (model: none) enters on frame 0 and takes a sample on every frame after it — seventeen segments over a ring "
        + "eight deep, so the head returns to 0 twice and the oldest segment held is seven ticks old, not the stream's start. Its samples walk y = x²/4, so "
        + "every interval has its own velocity and interpolation between two of them is visibly not the curve. Buoy 11 and Drone 100 enter on frame 12 and "
        + "teleport on frame 14 (epoch 4 → 5 and 1 → 2), with samples either side: the none model must hold rather than interpolate across the change, and "
        + "the linear model must keep extrapolating the segment in force.";

    private const string VectorDescription =
        "Motion evaluated over the committed stream-motion replay, in the schema of motion-eval: after frame `afterFrame`, every live entity of the "
        + "archetypes a case names, evaluated at renderTick + renderFrac and rendered as IEEE bit strings. These are the two cases motion-eval says it "
        + "cannot reach. The ring: a render time before the ring wrapped, one inside a ring entry that has been overwritten, one whose next sample is entry "
        + "0 reached from entry 7 (the modular step the wrap exists to exercise), one past the newest sample, and one older than the oldest segment the ring "
        + "still holds — meaningful only once a wrap has thrown older segments away. The epoch: just before a teleport, between the two epochs' samples, and "
        + "just after it, for a none-model Buoy and a linear Drone at once. Render time is split into an integer tick and a fraction in [0, 1) — never one "
        + "float — so evaluation stays exact however long the server has been running (03 § 6), and `epoch` is the motion epoch in force at renderTick. "
        + "Entities are named by archetype and netId, never by slot, ordered by catalog archetype then ascending netId; `position` and `velocity` each carry "
        + "`dims` values in axis order. segmentHistory is the ring depth the vector was generated with.";

    /// <summary>What each case pins. Frames ascend, and within a frame the declaration order holds.</summary>
    private static readonly EvalCase[] Cases =
    [
        // Frame 5 — six segments held, the ring has not wrapped yet: the newest sample render time has reached is entry 3, and entry 4 is what it interpolates
        // toward. The pair's velocity (1, 1.75) belongs to that interval alone.
        new("before-the-wrap", 5, 2003, 0.5, ["Buoy"]),

        // Frame 16 — the ring is full and has turned twice: it holds ticks 2009..2016 only.
        // Entry 3 was written by the 3rd sample and again by the 11th; landing on it proves the reader follows the head backwards, not the entry order.
        new("inside-the-wrap", 16, 2011, 0.5, ["Buoy"]),

        // Entry 7's next sample is entry 0 — the modular step, and the one an off-by-one ring reader gets wrong.
        new("across-the-ring-index-wrap", 16, 2015, 0.5, ["Buoy"]),

        // Past the newest sample: a none-model entity holds it, with zero velocity.
        new("after-the-wrap", 16, 2016, 0.25, ["Buoy"]),

        // Older than the oldest segment the ring still holds (2009): the oldest held applies, and holds, because render time never reached its start.
        new("older-than-the-oldest-held", 16, 2004, 0, ["Buoy"]),

        // The teleport, from both sides. Buoy 11 and Drone 100 change epoch at tick 2014; Buoy 10 rides along, still inside the wrapped ring.
        new("just-before-the-epoch-change", 16, 2012, 0.5, ["Buoy", "Drone"]),
        new("between-the-two-epochs-samples", 16, 2013, 0.5, ["Buoy", "Drone"]),
        new("just-after-the-epoch-change", 16, 2014, 0.25, ["Buoy", "Drone"]),
    ];

    /// <summary>Builds the stream and the replica after each of its frames, and commits both.</summary>
    [Test]
    [Order(1)]
    public void TheWrappingStreamIsTheCommittedVector()
    {
        var plan = KitchenSink();
        var store = new WorldStore(plan);
        var applier = new FrameApplier(store);
        var snapshots = new JsonArray();
        var stream = new List<byte>();
        foreach (var frame in BuildFrames(plan))
        {
            applier.Apply(frame);
            snapshots.Add(StreamSnapshot.Render(store, []));
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)frame.Length);
            stream.AddRange(length);
            stream.AddRange(frame);
        }

        Assert.That(store.Anomalies, Is.Zero, "the stream is self-consistent: every segment names an entity the store holds");

        GoldenFiles.Assert(StreamName, stream.ToArray(), new JsonObject
        {
            ["description"] = StreamDescription,
            ["catalog"] = "catalog-kitchen-sink",
            ["snapshots"] = snapshots,
        });
    }

    /// <summary>Seventeen segments over a ring eight deep: the head is back where it started, and only the last eight ticks survive.</summary>
    [Test]
    [Order(2)]
    public void TheWrappingBuoyTurnsTheRingTwice()
    {
        var store = Replay();
        var buoy = store.Archetypes[store.Plan.ArchetypeByName("Buoy").Idx];
        var slot = SlotOf(buoy, 10);
        var last = FirstTick + FrameCount - 1;

        Assert.Multiple(() =>
        {
            Assert.That(store.SegmentHistory, Is.EqualTo(RingDepth), "50 000 µs ticks, a 300 ms delay: ⌈300/50⌉ + 2");
            Assert.That(buoy.Segments.Held(slot), Is.EqualTo(RingDepth), "the ring is full");
            Assert.That(buoy.Segments.Head(slot), Is.Zero, "sixteen pushes over eight entries bring the head back to where the enter left it");
            Assert.That(buoy.Segments.T0(slot, 0), Is.EqualTo(last), "the head holds the newest sample");
            Assert.That(buoy.Segments.T0(slot, 1), Is.EqualTo(last - RingDepth + 1), "the oldest held, seven ticks back — everything older was overwritten");

            if (!GoldenFiles.Updating)
            {
                Assert.That(
                    GoldenFiles.ReadJson(VectorName)["segmentHistory"]!.GetValue<int>(),
                    Is.EqualTo(store.SegmentHistory),
                    $"{VectorName}.json was generated against another ring depth — every case's segment choice may have moved, in both SDKs");
            }
        });
    }

    /// <summary>Replaying the golden stream and evaluating it gives, to the last bit, what the committed vector says — through both evaluation paths.</summary>
    [Test]
    [Order(3)]
    public void TheWrappingStreamEvaluatesToTheCommittedVector()
    {
        var store = new WorldStore(KitchenSink());
        var applier = new FrameApplier(store);
        var cases = new JsonArray();
        var frame = 0;
        foreach (var message in MotionEvalVector.Frames(GoldenFiles.ReadBin(StreamName)))
        {
            applier.Apply(message);
            foreach (var evaluation in Cases)
            {
                if (evaluation.AfterFrame == frame)
                {
                    cases.Add(MotionEvalVector.Render(store, evaluation));
                }
            }

            frame++;
        }

        Assert.That(frame, Is.EqualTo(FrameCount), "the committed stream must carry every frame the cases name");
        Assert.That(cases.Count, Is.EqualTo(Cases.Length), "every case must name a frame the stream carries");

        GoldenFiles.Assert(VectorName, [], new JsonObject
        {
            ["description"] = VectorDescription,
            ["catalog"] = "catalog-kitchen-sink",
            ["stream"] = StreamName,
            ["segmentHistory"] = store.SegmentHistory,
            ["cases"] = cases,
        });
    }

    private static WorldStore Replay()
    {
        var store = new WorldStore(KitchenSink());
        var applier = new FrameApplier(store);
        foreach (var message in MotionEvalVector.Frames(GoldenFiles.ReadBin(StreamName)))
        {
            applier.Apply(message);
        }

        return store;
    }

    private static int SlotOf(ArchetypeStore archetype, uint netId)
    {
        for (var i = 0; i < archetype.LiveCount; i++)
        {
            if (archetype.NetIds[archetype.Live[i]] == netId)
            {
                return archetype.Live[i];
            }
        }

        Assert.Fail($"{archetype.Plan.Name} {netId} is not live");
        return -1;
    }

    private static List<byte[]> BuildFrames(CatalogPlan plan)
    {
        var buoy = plan.ArchetypeByName("Buoy");
        var drone = plan.ArchetypeByName("Drone");
        var frames = new List<byte[]>();
        var buffer = new byte[16 * 1024];

        for (var k = 0; k < FrameCount; k++)
        {
            var tick = FirstTick + (uint)k;
            var w = new WireWriter(buffer);
            TickWriter.WriteHeader(ref w, tick, k == 0 ? TickFlags.Reset | TickFlags.ViewComplete : TickFlags.None);
            if (k == 0)
            {
                TickWriter.WriteRealm(ref w, TestFrames.Kitchen);
            }

            var enters = new List<EnterRecord>();
            var segments = new List<SegmentRecord>();

            // Buoy 10 — the wrapping sampler: one sample a tick, on the curve y = x²/4.
            if (k == 0)
            {
                enters.Add(new EnterRecord { NetId = 10, Position = Curve(0), T0 = tick, Epoch = 4, Values = BuoyValues(-3, 12345) });
            }
            else
            {
                segments.Add(new SegmentRecord { NetId = 10, Position = Curve(k), T0 = tick, Epoch = 4 });
            }

            // Buoy 11 — two samples at epoch 4, then the teleport and two more at epoch 5.
            if (k == EpochEnterFrame)
            {
                enters.Add(new EnterRecord { NetId = 11, Position = [100, 200], T0 = tick, Epoch = 4, Values = BuoyValues(-7, 4242) });
            }
            else if (k is EpochEnterFrame + 1 or EpochEnterFrame + 2 or EpochEnterFrame + 3)
            {
                segments.Add(new SegmentRecord { NetId = 11, Position = BuoySample(k), T0 = tick, Epoch = k <= EpochEnterFrame + 1 ? (byte)4 : (byte)5 });
            }

            TickWriter.WriteEntities(ref w, tick, buoy, enters, segments, [], [], TestFrames.Kitchen);

            // Drone 100 — the same teleport under the linear model, each segment carrying a velocity of its own so the chosen one is identifiable.
            if (k == EpochEnterFrame)
            {
                TickWriter.WriteEntities(ref w, tick, drone, [DroneEnter(100, tick)], [], [], [], TestFrames.Kitchen);
            }
            else if (k is EpochEnterFrame + 1 or EpochEnterFrame + 2 or EpochEnterFrame + 3)
            {
                TickWriter.WriteEntities(ref w, tick, drone, [], [DroneSegment(100, k, tick)], [], [], TestFrames.Kitchen);
            }

            frames.Add(w.Written.ToArray());
        }

        return frames;
    }

    // y = x²/4 sampled at every tick: quarter-metre steps, exact in the Buoy's 24-bit position codec over ±8192 (its step is 2⁻¹⁰), and no two intervals
    // share a velocity, so an interpolation across the wrong pair of samples is visible in the result rather than hidden by a constant speed.
    private static double[] Curve(int k) => [k, k * k / 4.0];

    // Buoy 11: one more sample at epoch 4, then the far side of the teleport and one step along it.
    private static double[] BuoySample(int k)
    {
        if (k == EpochEnterFrame + 1)
        {
            return [102, 200];
        }

        return k == EpochEnterFrame + 2 ? [500, 600] : [502, 601];
    }

    private static SegmentRecord DroneSegment(uint netId, int k, uint tick) => k == EpochEnterFrame + 1
        ? new SegmentRecord { NetId = netId, Position = [10.5, 20, -30.25], Velocity = [-0.75, 0, 0.125], T0 = tick, Epoch = 1 }
        : k == EpochEnterFrame + 2
            ? new SegmentRecord { NetId = netId, Position = [-200, -40, 300], Velocity = [0.25, 0.015625, 0.5], T0 = tick, Epoch = 2 }
            : new SegmentRecord { NetId = netId, Position = [-199.75, -39.984375, 300.5], Velocity = [-0.5, -0.03125, 0.25], T0 = tick, Epoch = 2 };

    private static EnterRecord DroneEnter(uint netId, uint tick) => new()
    {
        NetId = netId,
        Position = [10, 20, -30],
        Velocity = [0.5, 0, -0.25],
        T0 = tick,
        Epoch = 1,
        Values = new RecordValues
        {
            ["serial"] = FieldValue.Of([7, 7, 7, (byte)netId]),
            ["label"] = FieldValue.Of($"d{netId}"),
            ["armed"] = FieldValue.Of(1),
            ["lights"] = FieldValue.Of(0),
            ["stance"] = FieldValue.Of(2),
            ["heading"] = FieldValue.Of(0.5),
            ["rotation"] = FieldValue.Of(0, 0, 0, 1),
            ["thrust"] = FieldValue.Of(1, 2, 3),
            ["battery"] = FieldValue.Of(1),
            ["lastHit"] = FieldValue.Of(tick - 10),
            ["target"] = FieldValue.Of(10),
            ["temperature"] = FieldValue.Of(20),
            ["tilt"] = FieldValue.Of(0),
        },
    };

    private static RecordValues BuoyValues(double depth, double reading) => new()
    {
        ["depth"] = FieldValue.Of(depth), ["reading"] = FieldValue.Of(reading),
    };

    private static CatalogPlan KitchenSink() => CatalogPlan.Compile(CatalogSerializer.FromUtf8(GoldenFiles.ReadBin("catalog-kitchen-sink")));
}
