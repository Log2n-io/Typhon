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
/// <c>ControllerKind.Scripted</c>, so no client could use a shuttle at all: a bot's routing had three answers — walk,
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
[Category("Integration")]
[TestFixture]
[NonParallelizable]
public sealed class ShuttleBoardingChecks
{
    /// <summary>The server's boarding radius, as a float so that a <c>[TestCase]</c> can sit either side of it.</summary>
    private const float ShuttleBoardM = (float)TatooineReplication.ShuttleBoardM;

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
    [Category("Integration")]
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
    [Category("Integration")]
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
    [Category("Integration")]
    [TestCase(0, TestName = "AskingForTheCityItIsStandingInIsRefused")]
    [TestCase(9999, TestName = "AskingForACityThatDoesNotExistIsRefused")]
    [TestCase(-1, TestName = "AskingForANegativeCityIsRefused")]
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

    /// <summary>The boarding radius is the radius the server says it is, at both sides of it.</summary>
    /// <param name="offsetM">How far from the port to stand.</param>
    /// <param name="accepted">Whether the ask must be accepted.</param>
    /// <remarks>
    /// <b>The only negative case before this one stood 1 414 m from the port</b>, so every assertion here held just as
    /// well with a boarding radius ten times too large — and the client's own prompt threshold is a copy of this number,
    /// which makes it the kind of constant that drifts quietly. One metre inside and one metre outside pin it.
    /// </remarks>
    [Category("Integration")]
    [TestCase(ShuttleBoardM - 1f, true, TestName = "JustInsideTheBoardingRadiusIsAccepted")]
    [TestCase(ShuttleBoardM + 1f, false, TestName = "JustOutsideTheBoardingRadiusIsRefused")]
    [CancelAfter(90_000)]
    public void TheBoardingRadiusIsWhereTheServerSaysItIs(float offsetM, bool accepted)
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var link = Connect();

        var port = sim.Index.Shuttleports[0];
        Worlds.Park(sim, link.Session, port.X + offsetM, port.Z);

        link.SendCommand(nameof(BoardShuttle), new RecordValues { ["destCity"] = FieldValue.Of(1) });
        SessionHarness.Until(
            () => TatooineReplication.Boardings.Queued + TatooineReplication.Boardings.Refused >= 1,
            "the boarding ask to be answered by a tick");

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.Boardings.Queued, Is.EqualTo(accepted ? 1 : 0), $"standing {offsetM} m from the port");
            Assert.That(TatooineReplication.Boardings.Refused, Is.EqualTo(accepted ? 0 : 1), $"standing {offsetM} m from the port");
        });
    }

    /// <summary>Walking away gives up the place in the queue instead of being carried off anyway.</summary>
    /// <remarks>
    /// The boarding handler zeroes the body's motion and its comment says "stopped moving first", but <c>MoveDir</c> is
    /// drained AFTER the boarding loop in the same <c>ApplyIntents</c>: a client holding a direction key re-set its
    /// destination on the same tick, walked away from the port for a whole interval, and was still teleported when the
    /// shuttle came. The queue is what has to go — refusing the walk would freeze an avatar for up to two minutes.
    /// </remarks>
    [Test]
    [CancelAfter(90_000)]
    [Category("Integration")]
    public void WalkingAwayCancelsTheBoarding()
    {
        using var sim = new TatooineSim(Config());
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var link = Connect();

        const int Dest = 1;
        var port = sim.Index.Shuttleports[0];
        var destPort = sim.Index.Shuttleports[Dest];
        var player = Worlds.Park(sim, link.Session, port.X, port.Z);

        link.SendCommand(nameof(BoardShuttle), new RecordValues { ["destCity"] = FieldValue.Of(Dest) });
        SessionHarness.Until(() => TatooineReplication.Boardings.Queued >= 1, "the boarding ask to be queued by a tick");

        // Due east at a run, which is what a held key sends.
        link.SendCommand(
            nameof(MoveDir),
            new RecordValues { ["heading"] = FieldValue.Of(0.0), ["speedClass"] = FieldValue.Of(2.0), ["changePhase"] = FieldValue.Of(0.0) });
        SessionHarness.Until(() => TatooineReplication.Boardings.Cancelled >= 1, "the queued boarding to be cancelled by the movement");

        // Two full intervals at 10 Hz: if the queue had survived, the next landing would have flown it.
        _harness.Ticks(45);

        Assert.That(
            Near(sim, player, destPort.X, destPort.Z, 128f), Is.False,
            "a player who walked away from the port was still flown to the city it had asked for");
    }

    /// <summary>A client's boarding stays on its own planet, however the scripted crowd's inter-planet traffic is set.</summary>
    /// <remarks>
    /// <b>The share is forced to 1, so this is a certainty rather than a coin.</b> At the default 0.2 a broken build
    /// carries a client to another planet one ask in five, which is exactly the kind of defect a single-boarding test
    /// reports as passing four times out of five. <c>BoardShuttle</c> names a city and nothing else; another planet's
    /// city of the same index is not what was asked for, and arriving there is a realm change behind the client's back.
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    [Category("Integration")]
    public void AClientIsNeverFlownToAnotherPlanet()
    {
        var config = Config();
        config.Planets = 2;
        config.InterPlanetShare = 1f;

        using var sim = new TatooineSim(config);
        sim.Initialize();
        _harness = new SessionHarness(sim);
        var link = Connect();

        const int Dest = 1;
        var port = sim.Index.Shuttleports[0];
        var player = Worlds.Park(sim, link.Session, port.X, port.Z);
        var realm = Worlds.RealmOf(sim, player);

        link.SendCommand(nameof(BoardShuttle), new RecordValues { ["destCity"] = FieldValue.Of(Dest) });
        SessionHarness.Until(() => TatooineReplication.Boardings.Queued >= 1, "the boarding ask to be queued by a tick");

        var destPort = sim.Index.Shuttleports[Dest];
        SessionHarness.Until(
            () =>
            {
                _harness.Ticks(1);
                return Near(sim, player, destPort.X, destPort.Z, 128f);
            },
            $"the player to arrive at city {Dest}'s port on its own planet");

        Assert.That(Worlds.RealmOf(sim, player), Is.EqualTo(realm), "a client that asked for a city was flown to another planet");
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
