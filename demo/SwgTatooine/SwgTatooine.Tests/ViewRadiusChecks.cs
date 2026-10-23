using System;
using System.Collections.Generic;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A7 — <see cref="SetViewRadius"/>: a player asks to be served further out, and the buildings arrive.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted on the frames the session received, never on a server-side figure.</b> "The radius was widened" is only
/// interesting because it means "this client now holds things it did not hold before", and the only honest evidence of
/// that is the entities that entered. A case asserting <c>ViewRadiusAsks.Applied</c> would be green in a build where the
/// engine accepted the call and swept nothing.
/// </para>
/// <para>
/// <b>There is deliberately no test of the window-bound arithmetic</b> (<c>2⌈R / c⌉ + 5 ≤ 15</c>). The engine checks it
/// at <c>Start</c> and throws naming the cell side it would need, so every case in this suite that builds a world is
/// already that test; restating the formula in an assertion would only prove the formula was copied correctly.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class ViewRadiusChecks
{
    private string _dir;

    private SessionHarness _harness;

    [SetUp]
    public void SetUp()
    {
        _dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
    }

    [TearDown]
    public void TearDown()
    {
        _harness?.Release();
        _harness = null;
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        Worlds.Delete(_dir);
    }

    private SimConfig Config()
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;

        // 40 Hz, as SpectateChecks runs at: every Ticks(1) is a wall-clock wait and these cases step a dozen at a time.
        config.TickRateHz = 40;
        config.WorkerCount = 1;
        return config;
    }

    /// <summary>
    /// Stands the session's player just outside the widest city, so the buildings it holds are the sphere's business and not a group's.
    /// </summary>
    /// <param name="sim">The simulation.</param>
    /// <param name="player">The connected session.</param>
    /// <remarks>
    /// <b>Because the city group set holds a city's structures whatever the sphere does, which is what it is for.</b> A player inside a city already holds
    /// every building the city has, so widening its radius delivers nothing new and narrowing it drops nothing — both of these cases passed vacuously, or
    /// rather failed honestly, the day the set was declared. Standing outside the wall puts the buildings back under the radius's control.
    /// <para>
    /// Placed beyond the reach AND its hysteresis, with a margin: at exactly the reach the session would join and leave the set as the quantised position
    /// moved, and a case would be measuring the set rather than the radius. The city's near edge then sits inside the widened sphere and outside the narrow
    /// one, which is the condition both cases need.
    /// </para>
    /// </remarks>
    private static void StandOutsideACity(TatooineSim sim, Player player)
    {
        var city = Worlds.WidestCity(sim);
        var standoff = city.Radius + (float)(TatooineReplication.CityReachM + TatooineReplication.CityLeaveM) + 32f;
        Assert.That(
            (double)standoff,
            Is.LessThan(TatooineReplication.PlayerMaxRadiusM),
            "the premise: a player standing clear of the city's set must still reach its buildings at the widest radius");

        Worlds.Park(sim, player.Session, city.X + standoff, city.Z);
        player.Harness.Ticks(2);
    }

    /// <summary>What the session holds, and what it was refused.</summary>
    private struct HoldSink : ITickSink
    {
        /// <summary>Every netId currently held, by archetype name: entered and not since left.</summary>
        public Dictionary<string, HashSet<uint>> Held;

        /// <summary>Every refusal reason, in order.</summary>
        public List<byte> Refusals;

        /// <summary>How many frames carried <c>RESET</c>. Widening a radius must cost none: the client keeps what it has.</summary>
        public int Resets;

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

        public void Realm(RealmFrame frame)
        {
        }

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
    private sealed class Player
    {
        private readonly FakeLink _link;
        private int _read;
        private RealmFrame _frame;
        private HoldSink _sink = new() { Held = [], Refusals = [] };

        public Player(SessionHarness harness)
        {
            Harness = harness;
            _link = harness.Connect(TatooineReplication.PlayerKind);
            SessionHarness.Until(() => _link.Received(MessageTypes.Welcome), "WELCOME");

            // The session's profile is applied by the tick after its claim, and SetViewRadius needs it applied: a radius
            // for a profile that is not there yet is what makes the engine's SetRadius throw rather than refuse.
            SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the possession to be claimed by a tick");
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

    /// <summary>
    /// A wider radius delivers buildings the session did not hold, without a reset.
    /// </summary>
    /// <remarks>
    /// <b>The two halves are one claim.</b> That it grows is the feature; that it grows WITHOUT a <c>RESET</c> is what makes
    /// it usable from a slider, because a reset re-sends the whole view — at 320 m, every building and creature in a
    /// 0.32 km² disc — and dragging a slider would then cost one full view per step. The engine's <c>SetRadius</c> sweeps
    /// the shell between the two radii instead, which is the property being relied on and therefore the one to assert.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Category("Integration")]
    public void AWiderRadiusDeliversMoreBuildings()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var player = new Player(_harness);
        StandOutsideACity(sim, player);
        player.UntilSettled();

        var before = player.Holding(nameof(WorldObject));
        var resetsBefore = player.Drain().Resets;

        player.AskRadius((uint)TatooineReplication.PlayerMaxRadiusM);
        SessionHarness.Until(() => TatooineReplication.ViewRadiusAsks.Applied >= 1, "the radius to be applied by a tick");

        // The shell is swept over the ticks that follow, not in the one that applied it.
        var after = before;
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                after = player.Holding(nameof(WorldObject));
                return after > before;
            },
            $"a building beyond {TatooineReplication.PlayerRadiusM} m to arrive (held {before} at the profile's own radius)");

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.GreaterThan(before), "a wider radius delivered nothing further out");
            Assert.That(player.Drain().Resets, Is.EqualTo(resetsBefore), "widening the radius reset the view, so a slider costs a full view per step");
            Assert.That(player.Drain().Refusals, Is.Empty, "a radius inside the declared range was refused");
        });
    }

    /// <summary>
    /// Narrowing it back to the profile's own radius drops what is now out of range.
    /// </summary>
    /// <remarks>
    /// 0 is the engine's own spelling of "back to the profile's own", and it is the value a client sends when the person
    /// drags the slider home — so the path matters as much as the widening one. Asserted as a drop rather than as an exact
    /// count: what the session ends up holding is whatever is within 192 m of wherever the avatar has walked to by then.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Category("Integration")]
    public void NarrowingBackDropsWhatIsNowOutOfRange()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var player = new Player(_harness);
        StandOutsideACity(sim, player);
        player.UntilSettled();

        player.AskRadius((uint)TatooineReplication.PlayerMaxRadiusM);
        var wide = 0;
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                wide = player.Holding(nameof(WorldObject));
                return TatooineReplication.ViewRadiusAsks.Applied >= 1 && wide > 0;
            },
            "the wide radius to fill");

        // Ticked a few more times so the sweep has finished: a leave count taken mid-sweep would be compared against a
        // view that was still growing.
        _harness.Ticks(10);
        wide = player.Holding(nameof(WorldObject));

        player.AskRadius(0);
        var narrow = wide;
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                narrow = player.Holding(nameof(WorldObject));
                return narrow < wide;
            },
            $"a building to leave when the radius returns to {TatooineReplication.PlayerRadiusM} m (held {wide} at the maximum)");

        Assert.That(narrow, Is.LessThan(wide), "narrowing the radius left the session holding everything the wide one did");
    }

    /// <summary>
    /// A radius outside the declared range is refused and counted, not clamped.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than clamped for the reason a speed class is</b>: a clamp lets a slider sit at two kilometres
    /// while the session is served 320 m, with the client drawing its near-radius ring at a figure nothing honours and no
    /// way to discover it. The client is told both ends of the range in <c>/typhon/demo.json</c>, so an ask outside it
    /// means the two disagree about the server — which is worth an <c>ACKS</c> and a counter rather than silence.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Category("Integration")]
    public void ARadiusOutsideTheRangeIsRefusedAndCounted()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var player = new Player(_harness);
        player.UntilSettled();

        var before = player.Holding(nameof(WorldObject));

        // Past the ceiling, and below the profile's own radius: both ends are refused, and a case that only tried one
        // would pass against a check that tested the other.
        player.AskRadius((uint)TatooineReplication.PlayerMaxRadiusM + 500);
        SessionHarness.Until(() => TatooineReplication.ViewRadiusAsks.Refused >= 1, "the oversized radius to be refused");

        player.AskRadius(16);
        SessionHarness.Until(() => TatooineReplication.ViewRadiusAsks.Refused >= 2, "the undersized radius to be refused");
        _harness.Ticks(5);

        var sink = player.Drain();
        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.ViewRadiusAsks.Refused, Is.EqualTo(2), "the refusal count is the only number that says anything is trying");
            Assert.That(TatooineReplication.ViewRadiusAsks.Applied, Is.Zero, "a refused radius was counted as applied");
            Assert.That(sink.Refusals, Has.Member(TatooineReplication.ViewRadiusRefused), "the client was not told why its radius was refused");
            Assert.That(player.Holding(nameof(WorldObject)), Is.EqualTo(before), "a refused radius changed what the session holds");
        });
    }
}
