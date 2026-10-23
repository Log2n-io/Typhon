using System;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
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
[Category("Integration")]
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
    [Category("Integration")]
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
    /// <b>Bounded by construction, not by a cap.</b> The <c>List&lt;int&gt;</c> this replaced took one entry per port that saw an arrival, every tick, for the
    /// life of the process — and the report needs a total, a count, a maximum and two thresholds, none of which needs the samples kept. Five scalars cannot
    /// grow, which is a stronger guarantee than a cap a test could measure, so what is asserted here is that they are CORRECT: a bound over a wrong number is
    /// not progress. Groups placed on either side of each threshold, because that is where a counter that is wrong is wrong.
    /// </remarks>
    [Test]
    public void TheArrivalReportIsFiveCountersThatAddUp()
    {
        var arrivals = default(ArrivalCounters);
        foreach (var landed in new[] { 0, 1, 15, 16, 63, 64, 3, 0 })
        {
            arrivals.Record(landed);
        }

        Assert.Multiple(() =>
        {
            Assert.That(arrivals.Total, Is.EqualTo(162), "every player landed is counted once");
            Assert.That(arrivals.PortTicks, Is.EqualTo(6), "a tick that landed nobody is not a port-tick");
            Assert.That(arrivals.Largest, Is.EqualTo(64));
            Assert.That(arrivals.AtLeast16, Is.EqualTo(3), "16, 63 and 64: the threshold is inclusive and 15 is under it");
            Assert.That(arrivals.AtLeast64, Is.EqualTo(1), "64 and not 63");
        });
    }

    /// <summary>A running world feeds the arrival report: shuttles land players and the counters see them.</summary>
    /// <remarks>
    /// What <see cref="TheArrivalReportIsFiveCountersThatAddUp"/> cannot see is whether the shuttle path calls them at all. A player decides to travel only
    /// while it is inside a city, then walks to its port, so the shuttles run every two seconds with every player a traveller. Measured at this size: seven
    /// arrivals in thirty seconds of world, under a second of wall clock.
    /// </remarks>
    [Test]
    [Category("Integration")]
    public void ARunningWorldFeedsTheArrivalReport()
    {
        var config = Config();
        config.Unpaced = true;
        config.TickRateHz = 10;
        config.PopulationScale = 0.2f;
        config.WarmTicks = 0;
        config.MeasuredTicks = 300;
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
            Assert.That(arrivals.Total, Is.GreaterThan(0), "no shuttle landed anyone, so the report was never fed");
            Assert.That(arrivals.PortTicks, Is.GreaterThan(0).And.LessThanOrEqualTo(arrivals.Total), "a port-tick with an arrival carries at least one");
            Assert.That(arrivals.Largest, Is.GreaterThan(0).And.LessThanOrEqualTo(arrivals.Total));
        });
    }

    // ── Admission control ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A request as the transport builds one, for the admission hook.
    /// </summary>
    /// <param name="kind">The session kind the client names.</param>
    /// <returns>The request.</returns>
    /// <remarks>
    /// <b>A <c>player</c> gets a freshly minted token, because since A3 an unauthenticated one is refused before the caps are even
    /// consulted.</b> These cases are about the caps and the roles, so they present a valid credential and let the thing under test
    /// be the thing under test. The refusal itself is pinned separately by
    /// <see cref="AnUnauthenticatedPlayerIsRefused"/> and <see cref="AGodSessionNeedsNoToken"/>.
    /// </remarks>
    private static AdmissionRequest Asking(string kind)
        => Asking(kind, string.Equals(kind, TatooineReplication.PlayerKind, StringComparison.Ordinal) ? AuthToken.Mint(SomeAccount) : "opaque");

    /// <summary>A request presenting exactly the token given, however bad.</summary>
    /// <param name="kind">The session kind.</param>
    /// <param name="token">The credential presented.</param>
    /// <returns>The request.</returns>
    private static AdmissionRequest Asking(string kind, string token)
        => new(kind, token, 0, [], null, null, null, "fake");

    /// <summary>An account id for a minted token. Nothing resolves it — see <c>SessionHarness.SyntheticAccount</c> for why.</summary>
    private static readonly EntityId SomeAccount = EntityId.FromRawValue(1);

    /// <summary>
    /// AC A-6, first half: a <c>player</c> session carrying no valid token is refused, and counted.
    /// </summary>
    /// <remarks>
    /// Every shape of bad credential, because they must be indistinguishable to the client: absent, empty, rubbish, and a
    /// well-formed token whose MAC does not check out. The last is the one that matters — a forged token is the attack, and the
    /// others are just mistakes.
    /// </remarks>
    [Test]
    public void AnUnauthenticatedPlayerIsRefused()
    {
        TatooineReplication.MaxClients = 0;
        var minted = AuthToken.Mint(SomeAccount);

        // One character of the token flipped: same length, same shape, wrong MAC. 'A' and 'B' are both base64url, so this stays decodable.
        var forged = minted[..^1] + (minted[^1] == 'A' ? 'B' : 'A');

        Assert.Multiple(() =>
        {
            foreach (var bad in new[] { null, string.Empty, "not-a-token", forged })
            {
                var refusal = TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind, bad));
                Assert.That(refusal.IsAccepted, Is.False, $"a player presenting {bad ?? "null"} was admitted");
                Assert.That(refusal.RejectCode, Is.EqualTo(TatooineReplication.NotAuthenticatedCloseCode),
                    "the code must say 'log in again' and not 'come back later', or a client cannot tell a retry from a re-login");
                Assert.That(refusal.RejectReason, Is.Not.Null.And.Not.Empty);
            }

            Assert.That(TatooineReplication.RefusedUnauthenticated, Is.EqualTo(4), "every refusal must be counted");
            Assert.That(TatooineReplication.RefusedFull, Is.Zero, "a refusal for want of a token must not be charged to the cap");
        });
    }

    /// <summary>
    /// AC A-6, second half: a <c>god</c> session still connects anonymously, and carries no identity.
    /// </summary>
    /// <remarks>
    /// The god camera is the one tool that has to work on a box nobody has an account on, and it can do nothing with an identity
    /// anyway: it is <see cref="SessionRole.Spectator"/>, and every game intent is <c>Roles(Player)</c>.
    /// </remarks>
    [Test]
    public void AGodSessionNeedsNoToken()
    {
        TatooineReplication.MaxSpectators = 0;

        var admitted = TatooineReplication.Admit(Asking(TatooineReplication.GodKind, null));
        Assert.Multiple(() =>
        {
            Assert.That(admitted.IsAccepted, Is.True, "a god session must not need a login");
            Assert.That(admitted.Role, Is.EqualTo(SessionRole.Spectator));
            Assert.That(admitted.AppData, Is.Null, "a god session is anonymous: it must carry no account");
        });
    }

    /// <summary>An admitted player carries the account its token named, which is how A5 knows whose avatar to bind.</summary>
    [Test]
    public void AnAdmittedPlayerCarriesItsAccount()
    {
        TatooineReplication.MaxClients = 0;
        var account = EntityId.FromRawValue(0x4D2);

        var admitted = TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind, AuthToken.Mint(account)));
        Assert.Multiple(() =>
        {
            Assert.That(admitted.IsAccepted, Is.True);
            Assert.That(admitted.AppData, Is.InstanceOf<PlayerIdentity>(), "the account must reach the tick as a NAMED type, not a boxed id");
            Assert.That(((PlayerIdentity)admitted.AppData).Account, Is.EqualTo(account));
        });
    }

    /// <summary>An expired token is refused, which is the only bound on a stolen one.</summary>
    /// <remarks>
    /// There is no revocation list — a token this server does not store cannot be withdrawn — so expiry is the whole of the defence
    /// and it has to actually be checked. A negative lifetime is how that is staged rather than waited for.
    /// </remarks>
    [Test]
    public void AnExpiredTokenIsRefused()
    {
        TatooineReplication.MaxClients = 0;
        var stale = AuthToken.Mint(SomeAccount, TimeSpan.FromSeconds(-30));

        var refusal = TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind, stale));
        Assert.Multiple(() =>
        {
            Assert.That(refusal.IsAccepted, Is.False);
            Assert.That(refusal.RejectCode, Is.EqualTo(TatooineReplication.NotAuthenticatedCloseCode));
        });
    }

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
            Assert.That(third.RejectReason, Is.Not.Null.And.Not.Empty,
                "a refusal a client cannot explain to its user is a dropped connection with extra steps");
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
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind)).IsAccepted, Is.True,
                "players have their own cap, which is unlimited");
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

            // The other half, which the web client's replication overlay now depends on being true (CLI3D-03): DEBUG carries the
            // replication grid, the session's shape and its delivered cells, which is a map of where everyone is. A player asks
            // for the cap — the SDK requests STATS | DEBUG for every session — and must simply not be granted it.
            Assert.That(TatooineReplication.Admit(Asking(TatooineReplication.PlayerKind)).Limits.AllowDebug, Is.False,
                "a player was granted DEBUG: the engine's own map of the world is a spectator tool, not a gameplay one");
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
    [Category("Integration")]
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

            // The ORDER, as a number. Asserting only that both happened is satisfied by a transport that closed first and sent the KICK into a dead link,
            // which is exactly the behaviour the protocol forbids and the one this whole item exists to produce.
            Assert.That(link.IndexOf(MessageTypes.Kick), Is.GreaterThanOrEqualTo(0).And.LessThan(link.SentWhenClosed),
                "the KICK was written after the close, so the client was dropped and then told why");
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
    [Category("Integration")]
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
