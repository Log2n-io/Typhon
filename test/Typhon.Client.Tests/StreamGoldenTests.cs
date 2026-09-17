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
        var recorder = new EventRecorder();
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
            Assert.That(Netids(third, "Ledger"), Is.EqualTo(new uint[] { 3, 7 }), "netId 3 came back as a Ledger a frame after it left as a Beacon");
            Assert.That(Netids(fourth, "Drone"), Is.Empty, "RESET cleared the replica");
            Assert.That(fourth["self"], Is.Null, "RESET cleared the owner state");
        });

        GoldenFiles.Assert("stream-kitchen-sink", stream.ToArray(), new JsonObject
        {
            ["description"] = "Four TICK frames against catalog-kitchen-sink, each framed as u32 length + message, with the replica after each: RESET fill, "
                + "updates with an event naming an entity that leaves in the same frame, netId reuse across archetypes, and a final RESET.",
            ["catalog"] = "catalog-kitchen-sink",
            ["snapshots"] = snapshots,
        });
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
        TickWriter.WriteEntities(ref w, 1000, beacon,
            [Enter(3, [100, 200], ("channel", 1), ("strength", 0.5), ("drift", -1)), Enter(4, [-50.25, 12], ("channel", 2), ("strength", 8), ("drift", 7))],
            [], [], []);
        TickWriter.WriteEntities(ref w, 1000, buoy,
            [new EnterRecord { NetId = 10, Position = [1, 2], T0 = 999, Epoch = 4, Values = Values(("depth", -3), ("reading", 12345)) }], [], [], []);
        TickWriter.WriteEntities(ref w, 1000, drone, [DroneEnter(100, 1000), DroneEnter(101, 998)], [], [], []);
        TickWriter.WriteEntities(ref w, 1000, ledger, [new EnterRecord { NetId = 7, Values = LedgerValues(77, -5) }], [], [], []);
        TickWriter.WriteSelf(ref w, drone, 100, 5, 0b11, new RecordValues
        {
            ["manifest"] = FieldValue.Of([1, 2]), ["fuel"] = FieldValue.Of(2.5), ["pin"] = FieldValue.Of(42), ["vault"] = FieldValue.Of(1),
        });
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
            [new StateRecord { NetId = 4, GroupMask = 1, Values = Values(("strength", 1.5), ("drift", 2)) }], [3]);
        TickWriter.WriteEntities(ref w, 1001, buoy, [], [new SegmentRecord { NetId = 10, Position = [1.5, 2.5], T0 = 1001, Epoch = 4 }], [], []);
        var flags = Values(("armed", 0), ("lights", 1), ("stance", 1));
        TickWriter.WriteEntities(ref w, 1001, drone,
            [],
            [new SegmentRecord { NetId = 100, Position = [11, 21, -31], Velocity = [0.25, 0, 0], T0 = 1001, Epoch = 1 }],
            [new StateRecord { NetId = 101, GroupMask = 0b001, Values = flags }],
            [101]);
        TickWriter.WriteEvents(ref w,
        [
            (plan.EventByName("Ping"), new RecordValues { ["from"] = FieldValue.Of(101), ["path"] = FieldValue.Of(0, 0), ["loud"] = FieldValue.Of(1) }),
        ]);
        TickWriter.WriteSelf(ref w, drone, 100, 6, 0b10, new RecordValues { ["pin"] = FieldValue.Of(43), ["vault"] = FieldValue.Of(0) });
        TickWriter.WriteAcks(ref w, [(6, AckReasons.Rejected)]);
        TickWriter.WriteSources(ref w, [(9, SourceStatus.Applied, 0)]);
        TickWriter.WriteAggregate(ref w, plan.Grids[0], reset: false, [(1u, [0u, 0u]), (7u, [4u, 4u])]);
        frames.Add(w.Written.ToArray());

        // Frame 3 — netId 3, released by the Beacon a frame ago, returns as a Ledger; a new Drone enters.
        w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 1002, TickFlags.None);
        TickWriter.WriteEntities(ref w, 1002, drone, [DroneEnter(102, 1002)], [], [], []);
        TickWriter.WriteEntities(ref w, 1002, ledger, [new EnterRecord { NetId = 3, Values = LedgerValues(9, 5) }], [], [], []);
        frames.Add(w.Written.ToArray());

        // Frame 4 — RESET: only what this frame carries survives.
        w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, 1003, TickFlags.Reset);
        TickWriter.WriteEntities(ref w, 1003, beacon, [Enter(50, [0, 0], ("channel", 3), ("strength", 0), ("drift", 0))], [], [], []);
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
