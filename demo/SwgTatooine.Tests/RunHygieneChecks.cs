using System;
using NUnit.Framework;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// SWG-07 — the four run-hygiene defects that are not about the command line: a transaction held for the process lifetime, a report that grew per tick, a
/// server with no admission control, and a shutdown that dropped its clients without telling them.
/// </summary>
/// <remarks>
/// Every case here builds a real small world, because every one of the four is about what happens over time in a process that is running — which is exactly
/// what a unit test of the pieces in isolation cannot see.
/// </remarks>
[TestFixture]
[NonParallelizable]
public sealed class RunHygieneChecks
{
    private string _dir;

    /// <summary>The harness a case built, so teardown can unhook it from <c>SessionHarness.Until</c>'s keepalive.</summary>
    private SessionHarness _harness;

    [SetUp]
    public void SetUp()
    {
        _dir = Worlds.NewDirectory();
        TatooineReplication.ResetSessionAccounting();
    }

    [TearDown]
    public void TearDown()
    {
        _harness?.Release();
        _harness = null;
        TatooineReplication.ResetSessionAccounting();
        Worlds.Delete(_dir);
    }

    /// <summary>A world small enough to build in under a second, paced slowly enough that a tick boundary is observable.</summary>
    private SimConfig Config()
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;
        config.TickRateHz = 50;
        return config;
    }

    // ── The transaction that was never given back ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The views keep working after the transaction that created them is disposed — so a server does not have to hold an epoch scope open for its lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was wrong and why it mattered.</b> Both entry points kept the creating <c>Transaction</c> in a field until the object was disposed.
    /// <c>Transaction</c>'s constructor calls <c>EpochManager.EnterScope</c> and its <c>Dispose</c> calls <c>ExitScopeUnordered</c>, so nothing deferred
    /// behind that epoch could be reclaimed while it was held — for a measured run, until the run ended; for <c>--serve</c>, never.
    /// </para>
    /// <para>
    /// <b>What this asserts, and why that is the right observable.</b> <c>VIEW-01</c> says a retained view query holds no transaction between operations and
    /// that the creator's lease ends at construction. If that were wrong, a view would dereference a pooled transaction on its first refresh — so the claim
    /// is checked where it shows: a run whose systems read six views produces interest queries with hits in them. A view that came back empty, or a refresh
    /// that threw, gives zero.
    /// </para>
    /// </remarks>
    [Test]
    public void TheViewsOutliveTheTransactionThatCreatedThem()
    {
        var config = Config();
        config.WarmTicks = 2;
        config.MeasuredTicks = 10;
        config.ForceAwareness = true;

        using var sim = new TatooineSim(config);
        sim.Initialize();
        var result = sim.Run();
        var stats = sim.LastStats;

        Assert.Multiple(() =>
        {
            Assert.That(result.TicksMeasured, Is.EqualTo(10), "the run did not complete");
            Assert.That(stats.AwarenessQueries, Is.GreaterThan(0),
                "no interest query ran, so PlayerView produced nothing — the creating transaction's disposal broke the view");
            Assert.That(sim.Census.Players, Is.GreaterThan(0), "the world has no players, so this case cannot claim anything about their view");
        });
    }

    // ── The report that grew once per port per tick ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The shuttle arrival report is five counters, and they add up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bounded by construction, not by a cap.</b> The <c>List&lt;int&gt;</c> this replaced took one entry per port that saw an arrival, every tick, for the
    /// life of the process — and the report needs a total, a count, a maximum and two thresholds, none of which needs the samples kept. Five scalars cannot
    /// grow, which is a stronger guarantee than a cap a test could measure, so what is asserted here is that they are CORRECT: a bound over a wrong number is
    /// not progress.
    /// </para>
    /// <para>
    /// <b>The world is bigger and the run longer than elsewhere here, because an arrival is not cheap to provoke.</b> A player decides to travel only while
    /// it is inside a city, then walks to its port, and after arriving it idles for <c>200 × TickRateHz</c> ticks — so at the fixture's 0.05 population scale
    /// a two-thousand-tick run produced exactly one arrival, and a case asserting over that would have been reporting on a world in which nothing happened.
    /// Ten times the population and 600 unpaced ticks give ~17 arrivals across ~15 port-ticks, which is what these invariants need to mean anything.
    /// </para>
    /// </remarks>
    [Test]
    public void TheArrivalReportIsFiveCountersThatAddUp()
    {
        var config = Config();
        config.Unpaced = true;
        config.TickRateHz = 10;
        config.PopulationScale = 0.5f;
        config.WarmTicks = 0;
        config.MeasuredTicks = 600;
        config.ShuttleIntervalS = 2f;
        config.BoardingWindowS = 1f;
        config.ShuttleShare = 1f;
        config.ShuttleBurst = true;

        using var sim = new TatooineSim(config);
        sim.Initialize();
        sim.Run();

        var arrivals = sim.Bridge.ArrivalSummary;
        Assert.Multiple(() =>
        {
            Assert.That(arrivals.Total, Is.GreaterThan(0), "no shuttle landed, so the counters were never exercised");
            Assert.That(arrivals.PortTicks, Is.GreaterThan(0).And.LessThanOrEqualTo(arrivals.Total), "a port-tick with an arrival carries at least one");
            Assert.That(arrivals.Largest, Is.GreaterThan(0).And.LessThanOrEqualTo(arrivals.Total), "the largest burst is one of the arrivals");
            Assert.That(arrivals.AtLeast64, Is.LessThanOrEqualTo(arrivals.AtLeast16), "every burst of 64 is a burst of 16");
            Assert.That(arrivals.AtLeast16, Is.LessThanOrEqualTo(arrivals.PortTicks), "a threshold counts port-ticks, so it cannot exceed them");
        });
    }

    // ── Admission control ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A request as the transport builds one, for the admission hook.</summary>
    /// <param name="kind">The session kind the client names.</param>
    /// <returns>The request.</returns>
    private static AdmissionRequest Asking(string kind)
        => new(kind, "opaque", 0, [], null, null, null, "fake");

    /// <summary>
    /// The hook admits up to the cap and refuses past it, with a code in the application range and a reason.
    /// </summary>
    /// <remarks>
    /// Asserted against the hook rather than through a connection: the hook is a pure function of the caps and the counters, it is what the engine calls, and
    /// reaching it through four handshakes would test the handshake. That the engine calls it at all is what
    /// <see cref="AKickedSessionIsToldWhyBeforeItsLinkCloses"/> shows, by connecting for real.
    /// </remarks>
    [Test]
    public void TheHookRefusesPastTheClientCap()
    {
        TatooineReplication.MaxClients = 2;

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind)).IsAccepted, Is.True, "the first player is under the cap");
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind)).IsAccepted, Is.True, "the second player is at the cap");

            var third = TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind));
            Assert.That(third.IsAccepted, Is.False, "the third player is past the cap");
            Assert.That(third.RejectCode, Is.EqualTo(TatooineReplication.HouseFullCloseCode));
            Assert.That(third.RejectCode, Is.InRange(CloseCodes.FirstApplicationCode, CloseCodes.LastApplicationCode),
                "a protocol code would tell an SDK something different about whether to come back");
            Assert.That(third.RejectReason, Is.Not.Null.And.Not.Empty, "a refusal a client cannot explain to its user is a dropped connection with extra steps");
            Assert.That(TatooineReplication.RefusedFull, Is.EqualTo(1), "a refusal nobody counts is a refusal nobody notices");
        });
    }

    /// <summary>
    /// The two caps are separate, so a full house of spectators does not lock players out of their own world.
    /// </summary>
    [Test]
    public void TheSpectatorCapDoesNotBindPlayers()
    {
        TatooineReplication.MaxSpectators = 1;
        TatooineReplication.MaxClients = 0;

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.GodKind)).IsAccepted, Is.True);
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.GodKind)).IsAccepted, Is.False, "the spectator house is full");
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind)).IsAccepted, Is.True, "players have their own cap, which is unlimited");
        });
    }

    /// <summary>Zero is unlimited, which is the default and what every measurement run has relied on.</summary>
    [Test]
    public void ZeroIsUnlimited()
    {
        TatooineReplication.MaxClients = 0;
        for (var i = 0; i < 50; i++)
        {
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind)).IsAccepted, Is.True, $"admission {i} was refused under no cap");
        }
    }

    /// <summary>Roles come from the session kind, which is what makes a cap per role possible at all.</summary>
    [Test]
    public void TheKindDecidesTheRole()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind)).Role, Is.EqualTo(SessionRole.Player));
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.GodKind)).Role, Is.EqualTo(SessionRole.Spectator));
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.GodKind)).Limits.AllowDebug, Is.True,
                "a god camera is the tooling preset: it is allowed to see how the server is arranged");
        });
    }

    /// <summary>A connection arriving after a shutdown was asked for is refused rather than admitted and immediately kicked.</summary>
    [Test]
    public void AShuttingDownServerRefusesNewConnections()
    {
        TatooineReplication.RequestShutdown("going away");
        var admission = TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind));
        Assert.Multiple(() =>
        {
            Assert.That(admission.IsAccepted, Is.False);
            Assert.That(admission.RejectCode, Is.EqualTo(TatooineReplication.ShutdownCloseCode));
        });
    }

    // ── The shutdown nobody was told about ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A session open when the server is asked to stop receives a <c>KICK</c> carrying the code and the reason, and only then is its link closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this is worth.</b> The host used to stop Kestrel and let every socket die, which a client cannot distinguish from its own network failing: an
    /// SDK's reconnect policy backs off and retries a server that is deliberately gone. This is the whole path — a real handshake through the real acceptor,
    /// a real session in the runtime's table, the request staged by a tick and the frame written by the send pump — asserted on the bytes the client received.
    /// </para>
    /// <para>
    /// <b>Order matters and is asserted.</b> <c>KICK</c> then close, not close then nothing: the protocol sends both and they carry the same code
    /// (<c>03 § 3</c>). A link that was closed without a <c>KICK</c> reaching it would satisfy a weaker case that only looked at the close.
    /// </para>
    /// </remarks>
    [Test]
    public void AKickedSessionIsToldWhyBeforeItsLinkCloses()
    {
        var config = Config();
        using var sim = new TatooineSim(config);
        sim.Initialize();

        var harness = _harness = new SessionHarness(sim);
        var link = harness.Connect(TatooineReplication.GodKind);

        // The handshake answers WELCOME from the transport thread; the session becomes a row in the table on the next tick's prologue, which is when
        // RecountSessions can see it. Both are waited for, because a kick staged before the session exists would kick nothing and pass.
        SessionHarness.Until(() => link.Received(MessageTypes.Welcome), "WELCOME");
        SessionHarness.Until(() => TatooineReplication.LiveSessions.Spectators == 1, "the session to be counted by a tick");

        TatooineReplication.RequestShutdown("the box is going down");
        SessionHarness.Until(() => TatooineReplication.KicksStaged >= 1, "the kick to be staged by a tick");
        SessionHarness.Until(() => link.Closed != null, "the link to be closed");

        var kick = link.FirstOf(MessageTypes.Kick, m => KickMessage.Parse(m));
        Assert.Multiple(() =>
        {
            Assert.That(kick, Is.Not.Null, "the link was closed without the client ever being told why");
            Assert.That(kick.Value.Code, Is.EqualTo(TatooineReplication.ShutdownCloseCode));
            Assert.That(kick.Value.Reason, Is.EqualTo("the box is going down"), "the operator's reason is what reaches the client");
            Assert.That(link.Closed.Value.Code, Is.EqualTo(TatooineReplication.ShutdownCloseCode), "the close carries the same code as the KICK");
        });
    }

    /// <summary>
    /// EVERY open session is kicked, not the first one the walk reaches — and each exactly once, over the ticks that follow.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it establishes:</b> a shutdown reaches more than one client, both of them get a <c>KICK</c>, and the staged count settles at the number of
    /// sessions rather than climbing.
    /// </para>
    /// <para>
    /// <b>What it does NOT establish, measured rather than assumed.</b> Two mutations survive it, and both were run: stopping the walk after the first
    /// session, and removing the already-kicked guard. Both survive for the same reason — the shutdown branch runs again on every tick while the flag is set,
    /// and applying a kick closes the session in the next tick's prologue, so a session the walk skipped is picked up by the next tick and a session already
    /// kicked has left the table. Idempotence and completeness are properties of that retry, not of the guard or of the loop shape, and neither is falsifiable
    /// from outside the runtime. The already-kicked set is kept as a belt to that brace for the case where a request fails to apply and a session survives a
    /// tick — in which case <c>KicksStaged</c> would stop being a count of SESSIONS and the host's drain would end early with a second client's <c>KICK</c>
    /// unwritten. That path is not reachable from here and is not claimed.
    /// </para>
    /// </remarks>
    [Test]
    public void EverySessionOpenAtShutdownIsKickedExactlyOnce()
    {
        var config = Config();
        using var sim = new TatooineSim(config);
        sim.Initialize();

        var harness = _harness = new SessionHarness(sim);
        var first = harness.Connect(TatooineReplication.GodKind);
        var second = harness.Connect(TatooineReplication.GodKind);
        SessionHarness.Until(() => TatooineReplication.LiveSessions.Spectators == 2, "both sessions to be counted by a tick");

        TatooineReplication.RequestShutdown("once");
        SessionHarness.Until(() => first.Closed != null && second.Closed != null, "both links to be closed");
        harness.Ticks(5);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.KicksStaged, Is.EqualTo(2), "one kick per open session, and no more over the ticks that followed");
            Assert.That(first.Received(MessageTypes.Kick), Is.True, "the first session was closed without a KICK");
            Assert.That(second.Received(MessageTypes.Kick), Is.True, "the second session was closed without a KICK");
        });
    }
}
