using System;
using System.Collections.Generic;
using SwgTatooine.Server;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>What a session holds, what it was refused, and which realm it is in, decoded from the frames it received.</summary>
internal struct HoldSink : ITickSink
{
    /// <summary>Every netId currently held, by archetype name: entered and not since left.</summary>
    public Dictionary<string, HashSet<uint>> Held;

    /// <summary>Every refusal reason, in order.</summary>
    public List<byte> Refusals;

    /// <summary>How many frames carried <c>RESET</c>.</summary>
    public int Resets;

    /// <summary>The realm of the last <c>REALM</c> block, or -1 before the first.</summary>
    public int RealmId;

    private string _archetype;

    public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
    {
    }

    public void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
    {
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

    public void BeginTick(uint tick, TickFlags flags, uint periodUs)
    {
        if ((flags & TickFlags.Reset) != 0)
        {
            Resets++;
            Held.Clear();
        }
    }

    public void Realm(RealmFrame frame) => RealmId = frame.RealmId;

    public void BeginEntities(ArchetypePlan archetype) => _archetype = archetype.Name;

    public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
        if (_archetype == null)
        {
            return;
        }

        if (!Held.TryGetValue(_archetype, out var ids))
        {
            ids = [];
            Held[_archetype] = ids;
        }

        ids.Add(netId);
    }

    public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch)
    {
    }

    public void State(uint netId, byte groupMask)
    {
    }

    // A netId is not qualified by archetype on the wire, so a leave is removed from wherever it is held. Ids are
    // database-wide and unique, so at most one set can hold it.
    public void Leave(uint netId)
    {
        foreach (var ids in Held.Values)
        {
            if (ids.Remove(netId))
            {
                return;
            }
        }
    }

    public void Event(MessagePlan type)
    {
    }

    public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask)
    {
    }

    public void Ack(ushort seq, byte reason) => Refusals.Add(reason);

    public void Source(ushort requestId, byte status, ushort code)
    {
    }

    public void BeginAggregate(CatalogGrid grid, bool reset)
    {
    }

    public void Aggregate(int channel, int tile, double value)
    {
    }

    public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts)
    {
    }

    public void Metric(MetricPlan metric, int instance, double value)
    {
    }

    public void Debug(byte kind, scoped ReadOnlySpan<byte> payload)
    {
    }

    public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload)
    {
    }

    public void UnknownBlock(byte blockType)
    {
    }

    public void EndTick()
    {
    }
}

/// <summary>A connected player whose frames are decoded on demand.</summary>
internal sealed class PlayerProbe
{
    private readonly FakeLink _link;
    private int _read;
    private RealmFrame _frame;
    private HoldSink _sink = new() { Held = [], Refusals = [], RealmId = -1 };

    public PlayerProbe(SessionHarness harness)
    {
        Harness = harness;
        _link = harness.Connect(TatooineReplication.PlayerKind);
        SessionHarness.Until(() => _link.Received(MessageTypes.Welcome), "WELCOME");

        // The session's profile is applied by the tick after its claim, and SetViewRadius needs it applied: a radius
        // for a profile that is not there yet is what makes the engine's SetRadius throw rather than refuse.
        var possessed = TatooineReplication.Intents.Possessions + 1;
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= possessed, "the possession to be claimed by a tick");
        Harness.Ticks(2);
    }

    public SessionHarness Harness { get; }

    /// <summary>This session's id, so a case can place the body it controls.</summary>
    public SessionId Session => _link.Session;

    /// <summary>Decodes every frame that arrived since the last call.</summary>
    public HoldSink Drain()
    {
        var sent = _link.Sent;
        for (; _read < sent.Length; _read++)
        {
            var message = sent[_read];
            if (message.Length > 0 && message[0] == MessageTypes.Tick)
            {
                TickReader.Read(message, _link.Plan, ref _frame, ref _sink);
            }
        }

        return _sink;
    }

    public void AskRadius(uint metres) =>
        _link.SendCommand(nameof(SetViewRadius), new RecordValues { ["radiusM"] = FieldValue.Of(metres) });

    public void AskOverview(bool on) =>
        _link.SendCommand(nameof(Overview), new RecordValues { ["on"] = FieldValue.Of(on ? 1u : 0u) });

    /// <summary>How many of one archetype this session currently holds.</summary>
    public int Holding(string archetype) => Drain().Held.TryGetValue(archetype, out var ids) ? ids.Count : 0;

    /// <summary>Ticks until the session holds at least one building, so a case starts from a view that is filled.</summary>
    public void UntilSettled()
    {
        SessionHarness.Until(
            () =>
            {
                Harness.Ticks(1);
                return Holding(nameof(WorldObject)) > 0;
            },
            "the session to hold a building at its own radius");
    }
}
