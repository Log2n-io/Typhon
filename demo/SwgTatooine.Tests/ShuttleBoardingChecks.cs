using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A possessed player — a browser or a bot — can board a shuttle, and is refused when it has no business boarding one.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this closes.</b> <c>TryTakeShuttle</c> is server-side AI and the boarding loop admitted only
/// <c>ControllerKind.InProcess</c>, so no client could use a shuttle at all: a bot's routing had three answers — walk,
/// speeder, fly — of which one was unreachable for every client, and a human watching a crowd fly away could not follow.
/// </para>
/// <para>
/// <b>The filter's old rationale was stale, which is why relaxing it is safe rather than brave.</b> It guarded against a
/// body "possessed WHILE already queued" being flown off by a system its client cannot see, and cited
/// <c>PossessPlayers</c> normalising the activity on claim. That function was replaced by avatars in A5: a client is
/// given a freshly spawned body at <see cref="PlayerActivity.Idle"/> and never takes over a queued one. The two things
/// that set <see cref="PlayerActivity.AwaitingShuttle"/> are <c>PlayerThink</c>, which skips possessed players, and the
/// world generator, which builds only in-process bodies — so a possessed player is waiting for a shuttle if and only if
/// its own client asked.
/// </para>
/// <para>
/// Run with a two-second interval and in burst mode, so the landing the case waits for is tens of ticks away rather
/// than the twenty minutes the default cadence would make it.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class ShuttleBoardingChecks
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
        config.TickRateHz = 10;
        config.WorkerCount = 1;

        // A landing every two seconds, and the whole queue on the landing tick: the default cadence is two minutes, which
        // no case can wait for, and trickle boarding would add a coin flip to a case that is not about the coin.
        config.ShuttleIntervalS = 2f;
        config.ShuttleBurst = true;
        return config;
    }

    /// <summary>A client standing at its port and naming another city is flown there.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void AClientAtAPortIsFlownToTheCityItNamed()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();

        Assert.That(
            sim.Index.Shuttleports, Has.Count.EqualTo(sim.Index.Cities.Count),
            "the premise: every city needs its port, or there is nothing to board");

        _harness = new SessionHarness(sim);
        var link = Connect();

        const int From = 0;
        const int Dest = 1;
        var port = sim.Index.Shuttleports[From];
        var destPort = sim.Index.Shuttleports[Dest];
        var player = Worlds.Park(sim, link.Session, port.X, port.Z);

        link.SendCommand(nameof(BoardShuttle), new RecordValues { ["destCity"] = FieldValue.Of(Dest) });
        SessionHarness.Until(() => TatooineReplication.Boardings.Queued >= 1, "the boarding ask to be queued by a tick");

        // The flight happens on the port's next landing tick, which is at most one interval away.
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return Near(sim, player, destPort.X, destPort.Z, 64f);
            },
            $"the player to arrive at city {Dest}'s port");

        Assert.That(TatooineReplication.Boardings.Refused, Is.Zero, "a valid boarding ask was refused");
    }

    /// <summary>Standing nowhere near a port is refused, and the refusal says which of the two reasons it was.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void AClientAwayFromAPortIsRefused()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var link = Connect();

        // Well clear of every port: the boarding radius is 20 m and the cities are kilometres apart, so one city's
        // centre plus a kilometre is nowhere near any of them.
        var port = sim.Index.Shuttleports[0];
        Worlds.Park(sim, link.Session, port.X + 1000f, port.Z + 1000f);

        link.SendCommand(nameof(BoardShuttle), new RecordValues { ["destCity"] = FieldValue.Of(1) });
        SessionHarness.Until(() => TatooineReplication.Boardings.Refused >= 1, "the boarding ask to be refused by a tick");

        // The counter moves inside the tick that refused; the ACKS block reaches the client in the frame that tick publishes, which is assembled
        // after it. Reading the link the instant the counter moves would be reading for an answer that has not been sent yet.
        _harness.Ticks(3);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.Boardings.Queued, Is.Zero, "an ask from the desert was queued for a shuttle");
            Assert.That(Refusals(link), Contains.Item(TatooineReplication.ShuttleNotAtPort), "the client was not told it is not at a port");
        });
    }

    /// <summary>A destination that is the city it is standing in, or no city at all, is refused as a destination.</summary>
    /// <param name="dest">The destination asked for.</param>
    [TestCase(0, TestName = "AskingForTheCityItIsStandingInIsRefused")]
    [TestCase(9999, TestName = "AskingForACityThatDoesNotExistIsRefused")]
    [CancelAfter(90_000)]
    public void ABadDestinationIsRefused(int dest)
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var link = Connect();

        var port = sim.Index.Shuttleports[0];
        Worlds.Park(sim, link.Session, port.X, port.Z);

        link.SendCommand(nameof(BoardShuttle), new RecordValues { ["destCity"] = FieldValue.Of(dest) });
        SessionHarness.Until(() => TatooineReplication.Boardings.Refused >= 1, "the boarding ask to be refused by a tick");

        // The counter moves inside the tick that refused; the ACKS block reaches the client in the frame that tick publishes, which is assembled
        // after it. Reading the link the instant the counter moves would be reading for an answer that has not been sent yet.
        _harness.Ticks(3);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.Boardings.Queued, Is.Zero, "a bad destination was queued for a shuttle");
            Assert.That(Refusals(link), Contains.Item(TatooineReplication.ShuttleBadDestination), "the client was not told the destination is bad");
        });
    }

    private FakeLink Connect()
    {
        var link = _harness.Connect(TatooineReplication.PlayerKind);
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the possession to be claimed by a tick");
        _harness.Ticks(2);
        return link;
    }

    /// <summary>Whether an entity stands within a distance of a point.</summary>
    /// <param name="sim">The simulation.</param>
    /// <param name="entity">The player.</param>
    /// <param name="x">The point, east.</param>
    /// <param name="z">The point, north.</param>
    /// <param name="withinM">How close.</param>
    /// <returns>Whether it does.</returns>
    private static bool Near(TatooineSim sim, EntityId entity, float x, float z, float withinM)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        if (!tx.TryOpen(entity, out var player))
        {
            return false;
        }

        var at = player.Read(Player.Bounds);
        var dx = at.X - x;
        var dz = at.Z - z;
        return ((dx * dx) + (dz * dz)) <= withinM * withinM;
    }

    /// <summary>Every <c>ACKS</c> reason this session was sent.</summary>
    /// <param name="link">The session's link.</param>
    /// <returns>The reasons, in order.</returns>
    private static byte[] Refusals(FakeLink link)
    {
        var sink = new AckSink { Reasons = [] };
        var frame = default(RealmFrame);
        var sent = link.Sent;
        for (var i = 0; i < sent.Length; i++)
        {
            if (sent[i].Length > 0 && sent[i][0] == MessageTypes.Tick)
            {
                TickReader.Read(sent[i], link.Plan, ref frame, ref sink);
            }
        }

        return [.. sink.Reasons];
    }
}
