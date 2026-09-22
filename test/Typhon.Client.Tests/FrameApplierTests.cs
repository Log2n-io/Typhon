using System;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The store's own rules, independent of any vector: stable slots, leaves applied last, netId replacement, apply order whatever the block order, owner
/// state per controlled entity, anomalies counted rather than thrown, and no allocation while applying a large steady-state frame (AC-6).
/// </summary>
[TestFixture]
public class FrameApplierTests
{
    private static readonly CatalogPlan Plan = CatalogPlan.Compile(CatalogSerializer.FromUtf8(GoldenFiles.ReadBin("catalog-kitchen-sink")));

    private static ArchetypePlan Beacon => Plan.ArchetypeByName("Beacon");

    [Test]
    public void AFreedSlotIsReusedOnlyInALaterFrame()
    {
        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(1, TickFlags.None, enters: [1, 2]));
        store.TryLocate(1, out _, out var slotOf1);

        applier.Apply(Frame(2, TickFlags.None, enters: [3], leaves: [1]));
        store.TryLocate(3, out _, out var slotOf3);
        applier.Apply(Frame(3, TickFlags.None, enters: [4]));
        store.TryLocate(4, out _, out var slotOf4);

        Assert.Multiple(() =>
        {
            Assert.That(slotOf3, Is.Not.EqualTo(slotOf1), "a slot freed in frame 2 cannot be handed out in frame 2");
            Assert.That(slotOf4, Is.EqualTo(slotOf1), "it is free again from frame 3");
            Assert.That(store.Anomalies, Is.Zero);
        });
    }

    /// <summary>
    /// 03 § 10: a correct server never enters a live netId (its leave and its reuse travel in different frames), so a replacement is an anomaly.
    /// </summary>
    [Test]
    public void AnEnterForALiveNetIdReplacesItsHolderAndCountsAnAnomaly()
    {
        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(1, TickFlags.None, enters: [5]));
        applier.Apply(Frame(2, TickFlags.None, enters: [5]));

        Assert.Multiple(() =>
        {
            Assert.That(store.TryLocate(5, out _, out _), Is.True);
            Assert.That(store.Archetypes[Beacon.Idx].LiveCount, Is.EqualTo(1));
            Assert.That(store.Anomalies, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// Leaves apply last, to whatever holds the netId then. A correct server never sends this frame (D1); a store that receives it still ends with nothing
    /// held rather than a ghost.
    /// </summary>
    [Test]
    public void ALeaveAppliesToTheHolderOnceTheFramesEntersAreIn()
    {
        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(1, TickFlags.None, enters: [5], leaves: [5]));

        Assert.Multiple(() =>
        {
            Assert.That(store.TryLocate(5, out _, out _), Is.False);
            Assert.That(store.Archetypes[Beacon.Idx].LiveCount, Is.Zero);
        });
    }

    /// <summary>
    /// § 5's order holds whatever order the blocks travel in: events sent before the <c>ENTITIES</c> block still resolve the entity entering in it and the
    /// entity leaving in it.
    /// </summary>
    [Test]
    public void AnEventSeesTheEntersAndLeavesOfItsFrameWhateverTheBlockOrder()
    {
        var store = new WorldStore(Plan);
        var handler = new ResolvingHandler(store);
        var applier = new FrameApplier(store, handler);
        applier.Apply(Frame(1, TickFlags.None, enters: [9]));

        applier.Apply(Frame(2, TickFlags.None, enters: [8], leaves: [9], events: [Ping(8), Ping(9)]));

        Assert.Multiple(() =>
        {
            Assert.That(handler.Resolved, Is.EqualTo(2), "both events ran after the enter and before the leave");
            Assert.That(store.TryLocate(8, out _, out _), Is.True);
            Assert.That(store.TryLocate(9, out _, out _), Is.False, "the leave still applied");
        });
    }

    /// <summary>A leave belongs to its block's archetype, as a segment or a state record does: one naming another archetype's entity changes nothing.</summary>
    [Test]
    public void ALeaveInAnotherArchetypesBlockIsAnAnomaly()
    {
        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(1, TickFlags.None, enters: [9]));

        var w = new WireWriter(new byte[4096]);
        TickWriter.WriteHeader(ref w, 2, TickFlags.None);
        TickWriter.WriteEntities(ref w, 2, Plan.ArchetypeByName("Ledger"), [], [], [], [9]);
        applier.Apply(w.Written.ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(store.TryLocate(9, out _, out _), Is.True);
            Assert.That(store.Anomalies, Is.EqualTo(1));
        });
    }

    /// <summary>W17: owner values belong to one entity — a control change to another netId of the same archetype starts from nothing.</summary>
    [Test]
    public void OwnerStateResetsWhenTheControlledNetIdChanges()
    {
        var drone = Plan.ArchetypeByName("Drone");
        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);

        var w = new WireWriter(new byte[4096]);
        TickWriter.WriteHeader(ref w, 1, TickFlags.None);
        TickWriter.WriteSelf(ref w, drone, 100, 1, 0b10, new RecordValues { ["pin"] = FieldValue.Of(42), ["vault"] = FieldValue.Of(1) });
        applier.Apply(w.Written.ToArray());
        var pin = Array.FindIndex(drone.OwnerFields, f => f.Name == "pin");
        var heldBefore = store.Self.Numbers[pin] != null;

        w = new WireWriter(new byte[4096]);
        TickWriter.WriteHeader(ref w, 2, TickFlags.None);
        TickWriter.WriteSelf(ref w, drone, 101, 2, 0, new RecordValues());
        applier.Apply(w.Written.ToArray());

        Assert.Multiple(() =>
        {
            Assert.That(heldBefore, Is.True);
            Assert.That(store.Self.NetId, Is.EqualTo(101u));
            Assert.That(store.Self.Numbers[pin], Is.Null, "netId 100's pin does not show as netId 101's");
        });
    }

    [Test]
    public void UnknownNetIdsAreCountedNotThrown()
    {
        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(1, TickFlags.None, states: [42], leaves: [43]));

        Assert.That(store.Anomalies, Is.EqualTo(2));
    }

    [Test]
    public void ANetIdAboveTheMapLimitIsAnAnomalyNotAnAllocation()
    {
        var store = new WorldStore(Plan, maxNetId: 1000);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(1, TickFlags.None, enters: [1001]));

        Assert.Multiple(() =>
        {
            Assert.That(store.Anomalies, Is.EqualTo(1));
            Assert.That(store.Archetypes[Beacon.Idx].LiveCount, Is.Zero);
        });
    }

    [Test]
    public void ResetClearsBeforeTheFrameApplies()
    {
        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(1, TickFlags.None, enters: [1, 2, 3]));
        applier.Apply(Frame(2, TickFlags.Reset, enters: [2]));

        Assert.Multiple(() =>
        {
            Assert.That(store.Archetypes[Beacon.Idx].LiveCount, Is.EqualTo(1));
            Assert.That(store.TryLocate(1, out _, out _), Is.False);
            Assert.That(store.TryLocate(2, out _, out _), Is.True);
        });
    }

    /// <summary>
    /// The <c>tick-limits</c> vector through the store: its five leaves name netIds nobody holds (2³² − 1 beyond the map), so five anomalies and nothing
    /// thrown. The TypeScript store asserts the same count.
    /// </summary>
    [Test]
    public void TheLimitsVectorAppliesWithFiveAnomalies()
    {
        var wide = CatalogPlan.Compile(CatalogSerializer.FromUtf8(GoldenFiles.ReadBin("catalog-wide")));
        var store = new WorldStore(wide);
        new FrameApplier(store).Apply(GoldenFiles.ReadBin("tick-limits"));

        Assert.Multiple(() =>
        {
            Assert.That(store.Anomalies, Is.EqualTo(5));
            Assert.That(store.TryLocate(2, out _, out _), Is.True, "A127's enter");
            Assert.That(store.Archetypes[wide.ArchetypeByName("A254").Idx].LiveCount, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// AC-6: once the store has grown to the frames' size, applying 10 000 state records, 100 enters or 100 leaves and 100 events per frame — both passes
    /// and the leaves — allocates nothing.
    /// </summary>
    [Test]
    public void ASteadyStateFrameAllocatesNothing()
    {
        var netIds = Range(1, 10_000);
        var churn = Range(10_001, 100);
        var events = new (MessagePlan, RecordValues)[100];
        Array.Fill(events, Ping(1));

        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store, new ResolvingHandler(store));
        applier.Apply(Frame(1, TickFlags.None, enters: netIds));
        var arrive = Frame(2, TickFlags.None, enters: churn, states: netIds, events: events);
        var depart = Frame(3, TickFlags.None, states: netIds, leaves: churn, events: events);
        var tick = 3u;
        for (var i = 0; i < 3; i++)
        {
            applier.Apply(Retick(arrive, ++tick));
            applier.Apply(Retick(depart, ++tick));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        applier.Apply(Retick(arrive, ++tick));
        applier.Apply(Retick(depart, ++tick));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Multiple(() =>
        {
            Assert.That(allocated, Is.Zero);
            Assert.That(store.Anomalies, Is.Zero);
        });
    }

    /// <summary>A frame that repeats or goes back in tick is an anomaly unless it resets the store (a restarted server begins again low).</summary>
    [Test]
    public void ATickThatDoesNotAdvanceIsAnAnomalyUnlessTheFrameResets()
    {
        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(5, TickFlags.None));
        applier.Apply(Frame(5, TickFlags.None));
        applier.Apply(Frame(4, TickFlags.None));
        var afterRepeats = store.Anomalies;
        applier.Apply(Frame(1, TickFlags.Reset));

        Assert.Multiple(() =>
        {
            Assert.That(afterRepeats, Is.EqualTo(2));
            Assert.That(store.Anomalies, Is.EqualTo(2), "a RESET frame may go back");
            Assert.That(store.Tick, Is.EqualTo(1u));
        });
    }

    // The tick sits right after the message type (03 § 3); rewriting it in place keeps the measured loop free of allocation.
    private static byte[] Retick(byte[] frame, uint tick)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), tick);
        return frame;
    }

    private static uint[] Range(uint first, int count)
    {
        var ids = new uint[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = first + (uint)i;
        }

        return ids;
    }

    private static (MessagePlan, RecordValues) Ping(uint from) =>
        (Plan.EventByName("Ping"), new RecordValues { ["from"] = FieldValue.Of(from), ["path"] = FieldValue.Of(0, 0), ["loud"] = FieldValue.Of(0) });

    // Events, when given, travel before the ENTITIES block, so every test using them also exercises the apply order.
    private static byte[] Frame(
        uint tick,
        TickFlags flags,
        uint[] enters = null,
        uint[] states = null,
        uint[] leaves = null,
        (MessagePlan, RecordValues)[] events = null)
    {
        var buffer = new byte[1 << 20];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, tick, flags);
        if (events != null)
        {
            TickWriter.WriteEvents(ref w, events);
        }

        var enterRecords = Array.ConvertAll(enters ?? [], id => new EnterRecord
        {
            NetId = id, Position = [id % 100, -(double)(id % 100)], Values = Values(1, 0.5, -2),
        });
        var stateRecords = Array.ConvertAll(states ?? [], id => new StateRecord { NetId = id, GroupMask = 1, Values = Values(1, tick, (int)tick) });
        TickWriter.WriteEntities(ref w, tick, Beacon, enterRecords, [], stateRecords, leaves ?? []);
        return w.Written.ToArray();
    }

    private sealed class ResolvingHandler(WorldStore store) : IEventHandler
    {
        public int Resolved { get; private set; }

        public void Event(MessagePlan type)
        {
        }

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
            if (field.Name == "from" && store.TryLocate((uint)components[0], out _, out _))
            {
                Resolved++;
            }
        }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
        {
        }

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
        {
        }

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
        {
        }
    }

    private static RecordValues Values(double channel, double strength, double drift) => new()
    {
        ["channel"] = FieldValue.Of(channel), ["strength"] = FieldValue.Of(strength), ["drift"] = FieldValue.Of(drift),
    };
}
