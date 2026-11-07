using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The store-level golden vector: a stream of <c>TICK</c> frames against the kitchen-sink catalog, TCP-framed (<c>u32 len LE | message</c>, W31), and the
/// store's rendered state after each frame. Where the <c>tick-*</c> vectors pin what a decoder reports, this one pins what a client's replica holds —
/// apply order, leaves last, netId reuse across frames, owner state accumulating, aggregates, metrics and <c>RESET</c>.
/// </summary>
/// <remarks>
/// The catalog is read from the committed <c>catalog-kitchen-sink</c> vector exactly as a client receives it in <c>WELCOME</c>, so this suite depends on
/// the wire, never on the C# sample that produced it.
/// </remarks>
[TestFixture]
public class StreamGoldenTests
{
    [Test]
    public void KitchenSinkStream()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(GoldenFiles.ReadBin("catalog-kitchen-sink")));
        var frames = BuildFrames(plan);

        var store = new WorldStore(plan);
        var recorder = new EventRecorder(store);
        var applier = new FrameApplier(store, recorder);
        var snapshots = new JsonArray();
        var stream = new List<byte>();
        foreach (var frame in frames)
        {
            recorder.Events.Clear();
            applier.Apply(frame);
            snapshots.Add(StreamSnapshot.Render(store, recorder.Events));
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)frame.Length);
            stream.AddRange(length);
            stream.AddRange(frame);
        }

        var second = (JsonObject)snapshots[1]!;
        var third = (JsonObject)snapshots[2]!;
        var fourth = (JsonObject)snapshots[3]!;
        Assert.Multiple(() =>
        {
            Assert.That(store.Anomalies, Is.Zero, "the stream is self-consistent: every update names an entity the store holds");
            Assert.That(second["events"]![0]!["fields"]!["from"]![0]!.GetValue<string>(), Is.EqualTo(GoldenFiles.Bits(101)),
                "the event names Drone 101, which leaves in the same frame");
            Assert.That(Netids(second, "Drone"), Is.EqualTo(new uint[] { 100 }), "the leave applied after the event");
            Assert.That(second["events"]![0]!["known"]!["from"]!.GetValue<bool>(), Is.True, "the event resolved Drone 101 before its leave");
            Assert.That(third["events"]![1]!["known"]!["from"]!.GetValue<bool>(), Is.True, "an event resolves an enter whose block travels after it");
            Assert.That(third["events"]![2]!["known"]!["from"]!.GetValue<bool>(), Is.False, "Drone 101 left a frame ago");
            Assert.That(Netids(third, "Ledger"), Is.EqualTo(new uint[] { 3, 7 }), "netId 3 came back as a Ledger a frame after it left as a Beacon");
            Assert.That(Netids(fourth, "Drone"), Is.Empty, "RESET cleared the replica");
            Assert.That(fourth["self"], Is.Null, "RESET cleared the owner state");
        });

        GoldenFiles.Assert("stream-kitchen-sink", stream.ToArray(), new JsonObject
        {
            ["description"] = "Four TICK frames against catalog-kitchen-sink, each framed as u32 length + message, with the replica after each: RESET fill, "
                + "updates with an event naming an entity that leaves in the same frame, netId reuse across archetypes a frame after its leave, events "
                + "travelling before the ENTITIES block (text and bytes; an entityRef to an entering entity, known, and to a departed one, not), and a "
                + "final RESET. An event's `known` records whether each entityRef field resolved in the store when the event applied.",
            ["catalog"] = "catalog-kitchen-sink",
            ["snapshots"] = snapshots,
        });
    }

    /// <summary>
    /// #1616: a ground-clamped walker's segments, grounded and airborne — the grounded ones without their z axis — and the replica after each frame, its
    /// grounded height NaN. Every SDK replays this stream to the same store, bit for bit.
    /// </summary>
    [Test]
    public void GroundStream()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(GoldenFiles.ReadBin("catalog-ground")));
        var walker = plan.ArchetypeByName("Walker");
        var nan = FieldCodec.GroundedHeight;

        // The kitchen frame's bounds (z the narrow axis, the walker's up), in the default realm kind: catalog-ground declares no other.
        var frame = new RealmFrame(3, 7, 0, 0xC0FFEE, 24, 256, deep: true, [-8192, -8192, -64], [8192, 8192, 64]);
        var frames = new List<byte[]>();
        var buffer = new byte[4096];

        // Frame 1 — RESET: walker 1 enters grounded, walker 2 airborne.
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 500, TickFlags.Reset | TickFlags.ViewComplete);
        TickWriter.WriteRealm(ref w, frame);
        TickWriter.WriteEntities(ref w, 500, walker,
        [
            new EnterRecord { NetId = 1, Position = [10.5, -20.25, nan], Velocity = [0.25, -0.125, 0], T0 = 499, Epoch = 0, Values = Values(("hp", 90)) },
            new EnterRecord { NetId = 2, Position = [5, 6, 3.5], Velocity = [0.125, 0, 0.0625], T0 = 500, Epoch = 3, Values = Values(("hp", 40)) },
        ], [], [], [], frame);
        frames.Add(w.Written.ToArray());

        // Frame 2 — walker 1 jumps (airborne, full 3D), walker 2 lands (grounded, no z).
        w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 501, TickFlags.None);
        TickWriter.WriteEntities(ref w, 501, walker, [],
        [
            new SegmentRecord { NetId = 1, Position = [10.75, -20.375, 1.25], Velocity = [0.25, -0.125, 0.5], T0 = 501, Epoch = 0 },
            new SegmentRecord { NetId = 2, Position = [5.125, 6, nan], Velocity = [0.125, 0, 0], T0 = 501, Epoch = 3 },
        ], [], [], frame);
        frames.Add(w.Written.ToArray());

        // Frame 3 — walker 1 lands again; walker 2 leaves.
        w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 502, TickFlags.None);
        TickWriter.WriteEntities(ref w, 502, walker, [],
            [new SegmentRecord { NetId = 1, Position = [11, -20.5, nan], Velocity = [0.25, -0.125, 0], T0 = 502, Epoch = 0 }], [], [2], frame);
        frames.Add(w.Written.ToArray());

        var store = new WorldStore(plan);
        var recorder = new EventRecorder(store);
        var applier = new FrameApplier(store, recorder);
        var snapshots = new JsonArray();
        var stream = new List<byte>();
        foreach (var message in frames)
        {
            recorder.Events.Clear();
            applier.Apply(message);
            snapshots.Add(StreamSnapshot.Render(store, recorder.Events));
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)message.Length);
            stream.AddRange(length);
            stream.AddRange(message);
        }

        store.TryLocate(1, out var archetype, out var slot);
        var walkers = store.Archetypes[archetype];
        Assert.Multiple(() =>
        {
            Assert.That(store.Anomalies, Is.Zero);
            Assert.That(double.IsNaN(walkers.HeadPosition(slot)[2]), Is.True, "walker 1 ends grounded: its height is the client's terrain");
            Assert.That(frames[1].Length, Is.GreaterThan(frames[2].Length), "an airborne segment carries more than a grounded one");
        });

        GoldenFiles.Assert("stream-ground", stream.ToArray(), new JsonObject
        {
            ["description"] = "Three TICK frames against catalog-ground (#1616), each framed as u32 length + message, with the replica after each: a RESET "
                + "entering one walker grounded (no z on the wire; the replica's height is the quiet NaN 7ff8000000000000) and one airborne; then the first "
                + "jumps (a full pos3 segment) as the second lands; then the first lands and the second leaves.",
            ["catalog"] = "catalog-ground",
            ["snapshots"] = snapshots,
        });
    }

    /// <summary>
    /// #1616: evaluating a ground-clamped archetype fills a grounded entity's height from the application's terrain — called with the two horizontal
    /// axes (x, y when z is up) — and leaves an airborne one's, and every velocity, as decoded; without a callback the height stays NaN.
    /// </summary>
    [Test]
    public void EvaluatingAGroundedEntityTakesItsHeightFromTheTerrain()
    {
        var plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(GoldenFiles.ReadBin("catalog-ground")));
        var store = new WorldStore(plan);
        var applier = new FrameApplier(store, new EventRecorder(store));
        var stream = GoldenFiles.ReadBin("stream-ground");
        var frames = 0;
        for (var at = 0; frames < 2; frames++)
        {
            // After frame 2: walker 1 airborne, walker 2 grounded.
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(at));
            applier.Apply(stream.AsSpan(at + 4, length));
            at += 4 + length;
        }

        var walkers = store.Archetypes[plan.ArchetypeByName("Walker").Idx];
        Span<double> raw = stackalloc double[walkers.LiveCount * walkers.MotionStride];
        Span<double> grounded = stackalloc double[raw.Length];
        MotionEvaluator.EvaluateLive(walkers, 501, 0.5, raw);
        MotionEvaluator.EvaluateLive(walkers, 501, 0.5, grounded, static (x, y) => 100 + x + (1000 * y));
        for (var i = 0; i < walkers.LiveCount; i++)
        {
            var netId = walkers.NetIds[walkers.Live[i]];
            var o = i * walkers.MotionStride;
            if (netId == 2)
            {
                Assert.That(double.IsNaN(raw[o + 2]), Is.True, "without a callback a grounded height stays NaN");
                Assert.That(grounded[o + 2], Is.EqualTo(100 + grounded[o] + (1000 * grounded[o + 1])), "the terrain at (x, y) is the height");
                Assert.That(grounded[o + 5], Is.Zero, "a grounded segment carries no vertical velocity");
            }
            else
            {
                Assert.That(grounded.Slice(o, walkers.MotionStride).ToArray(), Is.EqualTo(raw.Slice(o, walkers.MotionStride).ToArray()),
                    "an airborne entity is evaluated as decoded");
            }
        }
    }

    private static IEnumerable<uint> Netids(JsonObject snapshot, string archetype) =>
        snapshot["archetypes"]![archetype]!["entities"]!.AsArray().Select(e => e!["netId"]!.GetValue<uint>());

    private static List<byte[]> BuildFrames(CatalogPlan plan)
    {
        var beacon = plan.ArchetypeByName("Beacon");
        var buoy = plan.ArchetypeByName("Buoy");
        var drone = plan.ArchetypeByName("Drone");
        var ledger = plan.ArchetypeByName("Ledger");
        var frames = new List<byte[]>();
        var buffer = new byte[16 * 1024];

        // Frame 1 — a RESET fill of every kind of archetype, owner state, aggregates and metrics.
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 1000, TickFlags.Reset | TickFlags.ViewComplete);
        TickWriter.WriteRealm(ref w, TestFrames.Kitchen);
        TickWriter.WriteEntities(ref w, 1000, beacon,
            [Enter(3, [100, 200], ("channel", 1), ("strength", 0.5), ("drift", -1)), Enter(4, [-50.25, 12], ("channel", 2), ("strength", 8), ("drift", 7))],
            [], [], [], TestFrames.Kitchen);
        TickWriter.WriteEntities(ref w, 1000, buoy,
            [new EnterRecord { NetId = 10, Position = [1, 2], T0 = 999, Epoch = 4, Values = Values(("depth", -3), ("reading", 12345)) }], [], [], [], TestFrames.Kitchen);
        TickWriter.WriteEntities(ref w, 1000, drone, [DroneEnter(100, 1000), DroneEnter(101, 998)], [], [], [], TestFrames.Kitchen);
        TickWriter.WriteEntities(ref w, 1000, ledger, [new EnterRecord { NetId = 7, Values = LedgerValues(77, -5) }], [], [], [], TestFrames.Kitchen);
        TickWriter.WriteSelf(ref w, drone, 100, 5, 0b11, new RecordValues
        {
            ["manifest"] = FieldValue.Of([1, 2]), ["fuel"] = FieldValue.Of(2.5), ["pin"] = FieldValue.Of(42), ["vault"] = FieldValue.Of(1),
        }, TestFrames.Kitchen);
        TickWriter.WriteAggregate(ref w, plan.Grids[0], reset: true, [(1u, [2u, 1u]), (5u, [0u, 3u])]);
        TickWriter.WriteStats(ref w, plan, new Dictionary<string, double[]>
        {
            ["typhon.tick.p50"] = [2.5], ["typhon.system.mean"] = [1, 2, 3], ["typhon.session.skippedFrames"] = [0], ["app.load"] = [0.25], ["app.queue"] = [1],
        });
        frames.Add(w.Written.ToArray());

        // Frame 2 — updates, an event naming an entity that leaves in this frame, owner group 1 only, acks, sources, incremental aggregates.
        w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 1001, TickFlags.None, periodUs: 100_000);
        TickWriter.WriteEntities(ref w, 1001, beacon, [], [],
            [new StateRecord { NetId = 4, GroupMask = 1, Values = Values(("strength", 1.5), ("drift", 2)) }], [3], TestFrames.Kitchen);
        TickWriter.WriteEntities(ref w, 1001, buoy, [], [new SegmentRecord { NetId = 10, Position = [1.5, 2.5], T0 = 1001, Epoch = 4 }], [], [], TestFrames.Kitchen);
        var flags = Values(("armed", 0), ("lights", 1), ("stance", 1));
        TickWriter.WriteEntities(ref w, 1001, drone,
            [],
            [new SegmentRecord { NetId = 100, Position = [11, 21, -31], Velocity = [0.25, 0, 0], T0 = 1001, Epoch = 1 }],
            [new StateRecord { NetId = 101, GroupMask = 0b001, Values = flags }],
            [101], TestFrames.Kitchen);
        TickWriter.WriteEvents(ref w,
        [
            (plan.EventByName("Ping"), new RecordValues { ["from"] = FieldValue.Of(101), ["path"] = FieldValue.Of(0, 0), ["loud"] = FieldValue.Of(1) }),
        ], TestFrames.Kitchen);
        TickWriter.WriteSelf(ref w, drone, 100, 6, 0b10, new RecordValues { ["pin"] = FieldValue.Of(43), ["vault"] = FieldValue.Of(0) }, TestFrames.Kitchen);
        TickWriter.WriteAcks(ref w, [(6, AckReasons.Rejected)]);
        // Two groups completed (crew 3, zone -7), and one released that never completed: nothing to remove.
        TickWriter.WriteSources(ref w, [(9, SourceStatus.Applied, 0)],
            [(SourceStatus.GroupComplete, 1, -7), (SourceStatus.GroupComplete, 0, 3), (SourceStatus.GroupReleased, 0, 99)]);
        TickWriter.WriteAggregate(ref w, plan.Grids[0], reset: false, [(1u, [0u, 0u]), (7u, [4u, 4u])]);
        frames.Add(w.Written.ToArray());

        // Frame 3 — netId 3, released by the Beacon a frame ago, returns as a Ledger; a new Drone enters. The EVENTS block travels first: a Chat event with
        // text and bytes, a Ping naming the entering Drone (known: events apply after enters) and one naming Drone 101, gone since frame 2 (unknown).
        w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 1002, TickFlags.None);
        TickWriter.WriteEvents(ref w,
        [
            (plan.EventByName("Chat"), new RecordValues
            {
                ["text"] = new FieldValue { Text = "héllo \uFEFF☀" }, ["attachment"] = new FieldValue { Bytes = [0x00, 0xFF, 0x10] },
            }),
            (plan.EventByName("Ping"), new RecordValues { ["from"] = FieldValue.Of(102), ["path"] = FieldValue.Of(1, 1), ["loud"] = FieldValue.Of(0) }),
            (plan.EventByName("Ping"), new RecordValues { ["from"] = FieldValue.Of(101), ["path"] = FieldValue.Of(1, 1), ["loud"] = FieldValue.Of(1) }),
        ], TestFrames.Kitchen);
        TickWriter.WriteEntities(ref w, 1002, drone, [DroneEnter(102, 1002)], [], [], [], TestFrames.Kitchen);
        TickWriter.WriteEntities(ref w, 1002, ledger, [new EnterRecord { NetId = 3, Values = LedgerValues(9, 5) }], [], [], [], TestFrames.Kitchen);

        // zone -7 released; crew 3 stays complete.
        TickWriter.WriteSources(ref w, [], [(SourceStatus.GroupReleased, 1, -7)]);
        frames.Add(w.Written.ToArray());

        // Frame 4 — RESET: only what this frame carries survives.
        w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 1003, TickFlags.Reset);
        TickWriter.WriteRealm(ref w, TestFrames.Kitchen);
        TickWriter.WriteEntities(ref w, 1003, beacon, [Enter(50, [0, 0], ("channel", 3), ("strength", 0), ("drift", 0))], [], [], [], TestFrames.Kitchen);

        // The RESET dropped crew 3; only what this frame completes is held.
        TickWriter.WriteSources(ref w, [], [(SourceStatus.GroupComplete, 1, 5)]);
        frames.Add(w.Written.ToArray());

        return frames;
    }

    private static EnterRecord Enter(uint netId, double[] position, params (string Name, double Value)[] values) =>
        new() { NetId = netId, Position = position, Values = Values(values) };

    private static EnterRecord DroneEnter(uint netId, uint t0) => new()
    {
        NetId = netId,
        Position = [netId, -netId, 0.5],
        Velocity = [0.5, 0, -0.25],
        T0 = t0,
        Epoch = 1,
        Values = new RecordValues
        {
            ["serial"] = FieldValue.Of([1, 2, 3, (byte)netId]),
            ["label"] = FieldValue.Of($"d{netId}"),
            ["armed"] = FieldValue.Of(1),
            ["lights"] = FieldValue.Of(0),
            ["stance"] = FieldValue.Of(2),
            ["heading"] = FieldValue.Of(0.5),
            ["rotation"] = FieldValue.Of(0, 0, 0, 1),
            ["thrust"] = FieldValue.Of(1, 2, 3),
            ["battery"] = FieldValue.Of(1),
            ["lastHit"] = FieldValue.Of(t0 - 10),
            ["target"] = FieldValue.Of(7),
            ["temperature"] = FieldValue.Of(20),
            ["tilt"] = FieldValue.Of(0),
        },
    };

    private static RecordValues LedgerValues(double owner, double amount) => new()
    {
        ["owner"] = FieldValue.Of(owner), ["amount"] = FieldValue.Of(amount), ["future"] = FieldValue.Of([9, 9, 9]),
    };

    private static RecordValues Values(params (string Name, double Value)[] values)
    {
        var record = new RecordValues();
        foreach (var (name, value) in values)
        {
            record[name] = FieldValue.Of(value);
        }

        return record;
    }
}
