using System;
using NUnit.Framework;
using Typhon.Protocol;

namespace Typhon.Client.Tests;

/// <summary>
/// The store's own rules, independent of any vector: stable slots, leaves applied last, netId replacement inside a frame, anomalies counted rather than
/// thrown, and no allocation while applying a large steady-state frame (AC-6).
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

    [Test]
    public void AnEnterForALiveNetIdReplacesItAndItsLeaveInTheSameFrameIsIgnored()
    {
        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(1, TickFlags.None, enters: [5]));
        applier.Apply(Frame(2, TickFlags.None, enters: [5], leaves: [5]));

        Assert.Multiple(() =>
        {
            Assert.That(store.TryLocate(5, out _, out _), Is.True, "the new holder of netId 5 survives the old holder's leave");
            Assert.That(store.Archetypes[Beacon.Idx].LiveCount, Is.EqualTo(1));
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

    /// <summary>AC-6: once the store has grown to the frame's size, applying a 10 000-record frame allocates nothing.</summary>
    [Test]
    public void ASteadyStateFrameAllocatesNothing()
    {
        var netIds = new uint[10_000];
        for (var i = 0; i < netIds.Length; i++)
        {
            netIds[i] = (uint)i + 1;
        }

        var store = new WorldStore(Plan);
        var applier = new FrameApplier(store);
        applier.Apply(Frame(1, TickFlags.None, enters: netIds));
        var update = Frame(2, TickFlags.None, states: netIds);
        applier.Apply(update);
        applier.Apply(update);

        var before = GC.GetAllocatedBytesForCurrentThread();
        applier.Apply(update);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Multiple(() =>
        {
            Assert.That(allocated, Is.Zero);
            Assert.That(store.Anomalies, Is.Zero);
        });
    }

    private static byte[] Frame(uint tick, TickFlags flags, uint[] enters = null, uint[] states = null, uint[] leaves = null)
    {
        var buffer = new byte[1 << 20];
        var w = new WireWriter(buffer);
        TickWriter.WriteHeader(ref w, tick, flags);
        var enterRecords = Array.ConvertAll(enters ?? [], id => new EnterRecord
        {
            NetId = id, Position = [id % 100, -(double)(id % 100)], Values = Values(1, 0.5, -2),
        });
        var stateRecords = Array.ConvertAll(states ?? [], id => new StateRecord { NetId = id, GroupMask = 1, Values = Values(1, tick, (int)tick) });
        TickWriter.WriteEntities(ref w, tick, Beacon, enterRecords, [], stateRecords, leaves ?? []);
        return w.Written.ToArray();
    }

    private static RecordValues Values(double channel, double strength, double drift) => new()
    {
        ["channel"] = FieldValue.Of(channel), ["strength"] = FieldValue.Of(strength), ["drift"] = FieldValue.Of(drift),
    };
}
