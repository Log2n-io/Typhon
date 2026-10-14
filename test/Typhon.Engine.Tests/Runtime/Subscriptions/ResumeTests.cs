using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Typhon.Protocol;

namespace Typhon.Engine.Tests.Runtime.Subscriptions;

/// <summary>
/// Resume: a link-dead session a client can come back to, and the one-of-two guarantee that makes it safe to act on (AC-18, SUB-12).
/// </summary>
/// <remarks>
/// <para>
/// <b>What an application does with these events is destructive, which is why exactly-once is a rule and not a nicety.</b> A server with characters destroys
/// an avatar on <c>ResumeExpired</c> and re-binds one on <c>Opened{resumedFrom}</c>. If a resume racing its own deadline could produce both, the avatar is
/// destroyed under the client that just resumed it; if it could produce neither, the avatar stands in the world for ever with nobody driving it and no event
/// left to clean it up. Both are silent, and both are a session's worth of state.
/// </para>
/// <para>
/// The grace is driven through <see cref="SessionTable.Clock"/> rather than waited on: the interesting instant is the one where the deadline and the reconnect
/// coincide, and sleeping cannot reach it. The last test does run the two sides on real threads, because the deterministic ones pin the outcome of each order
/// and only a race pins that the compare-exchange is what decides between them.
/// </para>
/// <para>
/// Several tests open every session they need BEFORE closing any: the record table is indexed by slot, the free stack is LIFO, so a connect-drop-connect loop
/// hands the second session the first one's slot and its record on purpose. That case has a test of its own
/// (<see cref="ASecondSessionOnOneSlotTakesTheRecordAndTheOlderPendingExpires"/>); the others would be testing it by accident.
/// </para>
/// </remarks>
[TestFixture]
class ResumeTests
{
    private const int GraceMs = 60_000;

    /// <summary>The distinctive text of the verifier's own assertion, which is what makes the mutant's failure evidence rather than noise.</summary>
    private const string NoSecondEventMarker = "the sweep must not also report an expiry";

    private ResourceRegistry _registry;
    private MemoryAllocator _allocator;
    private SubscriptionsSessions _sessions;
    private long _now;

    [SetUp]
    public void SetUp()
    {
        _registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "ResumeTests" });
        _allocator = new MemoryAllocator(_registry, new MemoryAllocatorOptions { Name = "ResumeTestAllocator" });
        _sessions = new SubscriptionsSessions();
        _now = 1_000_000;
    }

    [TearDown]
    public void TearDown()
    {
        _allocator?.Dispose();
        _registry?.Dispose();
    }

    private static long Ms(long count) => count * (Stopwatch.Frequency / 1000L);

    private SessionTable NewTable(int maxSessions = 8, int graceMs = GraceMs, int maxPendingResumes = 0)
    {
        var options = new SubscriptionsOptions { MaxSessions = maxSessions, ResumeGraceMs = graceMs, MaxPendingResumes = maxPendingResumes };
        return new SessionTable("Resume", _registry.Runtime, _allocator, options, _sessions.SessionEvents) { Clock = () => Volatile.Read(ref _now) };
    }

    private static AdmissionRequest Request() => new(string.Empty, "token", 0, ReadOnlySpan<byte>.Empty, null, null, null, "fake");

    private bool Admit(SessionTable table, out SessionId session, ReadOnlySpan<byte> resumeToken = default)
        => table.TryAdmit(_sessions, Request(), out session, out _, out _, resumeToken);

    /// <summary>Opens a session and delivers its <c>Opened</c>, which is what puts it in the tick's work and lets a transport close it.</summary>
    /// <param name="table">The table.</param>
    /// <param name="token">The token its client may come back with.</param>
    /// <returns>The session.</returns>
    private SessionId Connect(SessionTable table, out byte[] token)
    {
        Assert.That(Admit(table, out var session), Is.True);
        token = new byte[SessionTable.ResumeTokenBytes];
        Assert.That(table.TryWriteResumeToken(session, token), Is.True, "a server with a grace issues a token to every session it opens");
        table.BeginTick();
        Drain(table);
        return session;
    }

    /// <summary>Reconnects, presenting a token, and answers which session the new one resumed.</summary>
    /// <param name="table">The table.</param>
    /// <param name="token">The token.</param>
    /// <returns>The session the new one resumed, or <see cref="SessionId.None"/>.</returns>
    private SessionId Reconnect(SessionTable table, ReadOnlySpan<byte> token)
    {
        Assert.That(Admit(table, out _, token), Is.True);
        table.BeginTick();
        return Single(table, SessionEventKind.Opened).ResumedFrom;
    }

    /// <summary>Drops the link under a session the way a transport does, and lets the tick turn that into a close.</summary>
    /// <param name="table">The table.</param>
    /// <param name="session">The session.</param>
    /// <param name="reason">Why the link went.</param>
    private static void Drop(SessionTable table, SessionId session, SessionCloseReason reason = SessionCloseReason.LinkLost)
    {
        Assert.That(table.RequestClose(session, reason, CloseCodes.GoingAway), Is.True);
        table.ApplyPendingCloses();
        table.BeginTick();
    }

    /// <summary>Runs the tick that gives a closed session's slot back, which a one-slot table needs before anything else can open.</summary>
    /// <param name="table">The table.</param>
    private static void Recycle(SessionTable table)
    {
        table.BeginTick();
        Drain(table);
    }

    private static List<SessionEvent> Drain(SessionTable table)
    {
        var events = new List<SessionEvent>();
        var batch = table.Events.AsSpan();
        for (var i = 0; i < batch.Length; i++)
        {
            events.Add(batch[i]);
        }

        return events;
    }

    /// <summary>The one event of a kind in this tick's batch.</summary>
    /// <param name="table">The table.</param>
    /// <param name="kind">The kind.</param>
    /// <returns>The event.</returns>
    private static SessionEvent Single(SessionTable table, SessionEventKind kind)
    {
        var found = new List<SessionEvent>();
        foreach (var e in Drain(table))
        {
            if (e.Kind == kind)
            {
                found.Add(e);
            }
        }

        Assert.That(found, Has.Count.EqualTo(1), $"expected exactly one {kind} this tick");
        return found[0];
    }

    private static bool IsAllZero(byte[] token)
    {
        foreach (var b in token)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    // ── the token ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void EverySessionGetsItsOwnTokenAndNoneOfThemReadsAsTheWiresNone()
    {
        using var table = NewTable();
        var seen = new HashSet<string>();

        for (var i = 0; i < 8; i++)
        {
            Connect(table, out var token);

            Assert.That(IsAllZero(token), Is.False, "all-zero is how the wire says there is no resume, so no issued token may read as one");
            Assert.That(seen.Add(Convert.ToHexString(token)), Is.True, "a token shared between two sessions hands one client the other's avatar");
        }
    }

    [Test]
    public void AServerWithNoGraceIssuesNoTokenAtAll()
    {
        using var table = NewTable(graceMs: 0);

        Assert.That(Admit(table, out var session), Is.True);
        table.BeginTick();

        var token = new byte[SessionTable.ResumeTokenBytes];

        Assert.Multiple(() =>
        {
            Assert.That(table.ResumeEnabled, Is.False);
            Assert.That(table.TryWriteResumeToken(session, token), Is.False, "so the connection sends the zero token, which tells the SDK to reconnect");
            Assert.That(table.PendingResumeCount, Is.Zero);
        });

        // And the close path must not reach for a record table that was never committed.
        Drop(table, session);

        Assert.Multiple(() =>
        {
            Assert.That(table.PendingResumeCount, Is.Zero);

            // A link-dead close of a resumable KIND, on a server with no record to resume from. Reporting it as resumable is what an application acts on, so
            // it would keep whatever that session controlled waiting for one of two events that can never arrive. Found by the SWG demo's A13 cases, where it
            // held a player's body in the world for the life of the process.
            Assert.That(Single(table, SessionEventKind.Closed).Resumable, Is.False, "resumable means a record exists, not that the reason was of the kind");
        });
    }

    // ── the happy path ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AReconnectWithinTheGraceOpensANewSessionThatNamesTheOldOne()
    {
        using var table = NewTable();
        _sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player);

        var first = Connect(table, out var token);
        Drop(table, first);

        var closed = Single(table, SessionEventKind.Closed);

        Assert.Multiple(() =>
        {
            Assert.That(closed.Resumable, Is.True, "the application reads this to decide whether to keep what the session controlled");
            Assert.That(table.PendingResumeCount, Is.EqualTo(1));
        });

        _now += Ms(GraceMs / 2);

        Assert.That(Admit(table, out var second, token), Is.True);
        table.BeginTick();

        var opened = Single(table, SessionEventKind.Opened);

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Not.EqualTo(first), "resume opens a NEW session: the old identity is spent and nothing may name it again");
            Assert.That(opened.Session, Is.EqualTo(second));
            Assert.That(opened.ResumedFrom, Is.EqualTo(first), "which is the handle an application re-binds from");
            Assert.That(opened.Role, Is.EqualTo(SessionRole.Player), "the hook decides the role again — a resume is not a way past admission");
            Assert.That(table.PendingResumeCount, Is.Zero, "and the record is spent");
        });
    }

    [Test]
    public void AResumedSessionInheritsTheApplicationDataOnlyWhenAdmissionAttachedNone()
    {
        using var table = NewTable();
        var account = new object();

        _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, SessionLimits.Default, account);

        var hooked = Connect(table, out var hookedToken);
        var bare = Connect(table, out var bareToken);
        Drop(table, hooked);
        Drop(table, bare);
        Drain(table);

        Assert.That(Admit(table, out _, hookedToken), Is.True);
        table.BeginTick();

        Assert.That(Single(table, SessionEventKind.Opened).AppData, Is.SameAs(account),
            "the hook is the authenticated, current answer; a minute-old one must not overwrite it");

        _sessions.Admit = static (in AdmissionRequest _) => Admission.Accept(SessionRole.Player);

        Assert.That(Admit(table, out _, bareToken), Is.True);
        table.BeginTick();

        Assert.That(Single(table, SessionEventKind.Opened).AppData, Is.SameAs(account),
            "a hook that attaches nothing gets the old session's data back instead, so an application needs no token table of its own");
    }

    // ── what may not resume ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [TestCase(SessionCloseReason.ClientLeft)]
    [TestCase(SessionCloseReason.Kicked)]
    [TestCase(SessionCloseReason.ProtocolError)]
    [TestCase(SessionCloseReason.Abusive)]
    [TestCase(SessionCloseReason.Lagging)]
    [TestCase(SessionCloseReason.ServerShutdown)]
    [TestCase(SessionCloseReason.InternalError)]
    public void OnlyALinkThatDiedOnItsOwnLeavesAResume(SessionCloseReason reason)
    {
        using var table = NewTable();

        var first = Connect(table, out var token);
        Drop(table, first, reason);

        Assert.Multiple(() =>
        {
            Assert.That(Single(table, SessionEventKind.Closed).Resumable, Is.False);
            Assert.That(table.PendingResumeCount, Is.Zero, "no record, so the token names nothing");
        });

        Assert.That(Reconnect(table, token), Is.EqualTo(SessionId.None), "the client gets a fresh session, which is the honest answer");
    }

    [Test]
    public void AClientThatStoppedAcknowledgingMayComeBack()
    {
        using var table = NewTable();

        var first = Connect(table, out var token);
        Drop(table, first, SessionCloseReason.Unacknowledged);

        Assert.That(Single(table, SessionEventKind.Closed).Resumable, Is.True);
        Assert.That(Reconnect(table, token), Is.EqualTo(first), "a client whose keep-alive stopped is link-dead, not a client that left");
    }

    [Test]
    public void ATokenIsSpentByTheFirstReconnectThatPresentsIt()
    {
        using var table = NewTable();

        var first = Connect(table, out var token);
        Drop(table, first);
        Drain(table);

        Assert.That(Reconnect(table, token), Is.EqualTo(first));
        Assert.That(Reconnect(table, token), Is.EqualTo(SessionId.None),
            "single use: a token replayed from a log or a second tab must not take over a session someone is already holding");
    }

    [Test]
    public void AnAlteredTokenNamesNothing()
    {
        // Room for the loop below: every refused reconnect is still an admitted session, and none of them is closed.
        using var table = NewTable(maxSessions: 64);

        var first = Connect(table, out var token);
        Drop(table, first);
        Drain(table);

        // From bit 16 up: the low two bytes are the slot, and flipping those asks a different question — whether a token can name another session's record,
        // which it cannot, because the rest of it has to match too. That is the same answer, reached by the loop below from the other side.
        for (var bit = 16; bit < SessionTable.ResumeTokenBytes * 8; bit += 7)
        {
            var forged = (byte[])token.Clone();
            forged[bit / 8] ^= (byte)(1 << (bit % 8));

            Assert.That(Reconnect(table, forged), Is.EqualTo(SessionId.None), $"bit {bit} flipped and the record still matched");
        }

        Assert.Multiple(() =>
        {
            Assert.That(table.PendingResumeCount, Is.EqualTo(1), "and none of those attempts consumed the real one");
            Assert.That(Reconnect(table, token), Is.EqualTo(first));
        });
    }

    [Test]
    public void AZeroTokenAndAShortOneAreBothNoToken()
    {
        using var table = NewTable();

        var first = Connect(table, out var token);
        Drop(table, first);
        Drain(table);

        Assert.Multiple(() =>
        {
            Assert.That(Reconnect(table, new byte[SessionTable.ResumeTokenBytes]), Is.EqualTo(SessionId.None), "all-zero is the wire's none");
            Assert.That(Reconnect(table, token.AsSpan(0, 8)), Is.EqualTo(SessionId.None), "and a token of the wrong length is not a token");
            Assert.That(table.PendingResumeCount, Is.EqualTo(1), "neither of them spent the record");
            Assert.That(Reconnect(table, token), Is.EqualTo(first));
        });
    }

    // ── the deadline, and SUB-12 ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    [VerifiesRule("SUB-12")]
    public void AGraceThatElapsesTellsTheApplicationOnceAndTheTokenThenNamesNothing()
    {
        using var table = NewTable();
        var account = new object();
        _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, SessionLimits.Default, account);

        var first = Connect(table, out var token);
        Drop(table, first);
        Drain(table);

        _now += Ms(GraceMs + 1);

        Assert.That(table.BeginTick(), Is.EqualTo(1));

        var expired = Single(table, SessionEventKind.ResumeExpired);

        Assert.Multiple(() =>
        {
            Assert.That(expired.Session, Is.EqualTo(first), "which session will not be coming back");
            Assert.That(expired.AppData, Is.SameAs(account), "and what the application attached to it, so it can let go without a table of its own");
            Assert.That(table.PendingResumeCount, Is.Zero);
        });

        Assert.That(table.BeginTick(), Is.Zero, "once: an application that destroys an avatar here would otherwise destroy it twice");
        Assert.That(Reconnect(table, token), Is.EqualTo(SessionId.None), "and a token past its deadline is refused even before the sweep notices");
    }

    [Test]
    [VerifiesRule("SUB-12")]
    public void AResumeConsumedInTheTickItsGraceElapsesLeavesNoExpiry()
    {
        using var table = NewTable();
        AssertAResumeInTheGraceLeavesNoExpiry(table);
    }

    /// <summary>
    /// The same scenario against a consume that reads its record instead of claiming it: the verifier above must reject it.
    /// </summary>
    /// <remarks>
    /// Without the compare-exchange the record stays armed, so the sweep in the same tick reports the session expired as well — the application destroys the
    /// avatar it has just handed back to the client that resumed it. The mutant is one condition on the production path, not a copy of it.
    /// </remarks>
    [Test]
    [RuleMutant("SUB-12")]
    public void AConsumeThatDoesNotClaimItsRecordIsDetected()
    {
        using var table = NewTable();
        table.ConsumeWithoutClaimForTest = true;

        RuleMutants.AssertDetects("SUB-12", NoSecondEventMarker, () => AssertAResumeInTheGraceLeavesNoExpiry(table));
    }

    /// <summary>Presents a token inside the grace and sweeps past the deadline in the same tick: one resume, and no expiry for it.</summary>
    /// <param name="table">The table.</param>
    private void AssertAResumeInTheGraceLeavesNoExpiry(SessionTable table)
    {
        var first = Connect(table, out var token);
        Drop(table, first);
        Drain(table);

        // Inside the grace when the token is presented, past it when the tick sweeps: the ordering the exactly-once property is about, with the consume first.
        Assert.That(Admit(table, out _, token), Is.True);
        _now += Ms(GraceMs + 1);
        table.BeginTick();

        var events = Drain(table);

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1), $"the resume happened, so {NoSecondEventMarker} for it");
            Assert.That(events[0].Kind, Is.EqualTo(SessionEventKind.Opened));
            Assert.That(events[0].ResumedFrom, Is.EqualTo(first));
        });
    }

    [Test]
    [VerifiesRule("SUB-12")]
    public void AResumeArrivingAfterTheSweepIsAFreshSessionAndNotASecondExpiry()
    {
        using var table = NewTable();

        var first = Connect(table, out var token);
        Drop(table, first);
        Drain(table);

        _now += Ms(GraceMs + 1);
        table.BeginTick();

        Assert.That(Single(table, SessionEventKind.ResumeExpired).Session, Is.EqualTo(first));

        Assert.That(Admit(table, out var second, token), Is.True);
        table.BeginTick();

        var events = Drain(table);

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1), "the expiry was already reported; the other order must not report it again");
            Assert.That(events[0].Kind, Is.EqualTo(SessionEventKind.Opened));
            Assert.That(events[0].ResumedFrom, Is.EqualTo(SessionId.None));
            Assert.That(second, Is.Not.EqualTo(first));
        });
    }

    /// <summary>
    /// A resume consumed by an admission that then fails is still reported, because "neither event" strands state in the application.
    /// </summary>
    /// <remarks>
    /// The record is claimed before the new session is opened — it has to be, since the open is what carries <c>resumedFrom</c> — so anything that throws in
    /// between spends the resume and produces no <c>Opened</c>. The sweep then finds the record already consumed and says nothing, and an application that
    /// kept an avatar on the strength of <c>Closed{resumable}</c> holds it for ever. This is the half of SUB-12 that nothing else would report: the engine's
    /// own state is consistent either way.
    /// </remarks>
    [Test]
    [VerifiesRule("SUB-12")]
    public void AResumeSpentByAnAdmissionThatThrowsIsStillReportedExactlyOnce()
    {
        using var table = NewTable();
        var account = new object();
        _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, SessionLimits.Default, account);

        var first = Connect(table, out var token);
        Drop(table, first);
        Drain(table);

        table.ThrowFromOpenForTest = true;
        Assert.That(() => table.TryAdmit(_sessions, Request(), out _, out _, out _, token), Throws.InvalidOperationException);
        table.ThrowFromOpenForTest = false;

        table.BeginTick();
        var events = Drain(table);

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(1), "exactly one, for a resume that was spent and could not be used");
            Assert.That(events[0].Kind, Is.EqualTo(SessionEventKind.ResumeExpired));
            Assert.That(events[0].Session, Is.EqualTo(first));
            Assert.That(events[0].AppData, Is.SameAs(account), "so the application can let go of what it was holding");
            Assert.That(table.PendingResumeCount, Is.Zero);
        });

        // Spent is spent: the client is not given a second attempt at a record that no longer exists.
        Assert.That(Reconnect(table, token), Is.EqualTo(SessionId.None));
    }

    [Test]
    public void AConsumedRecordLetsGoOfTheApplicationsObject()
    {
        using var table = NewTable();
        var account = new object();
        _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, SessionLimits.Default, account);

        var first = Connect(table, out var token);
        Drop(table, first);
        Drain(table);

        Assert.That(table.ResumeAppDataForTest(first.Slot), Is.SameAs(account), "the record holds it while the client may still come back");
        Assert.That(Reconnect(table, token), Is.EqualTo(first));

        // The consume cannot clear it — it is a managed write, and a link thread may not make one (SUB-05) — so the next sweep does.
        table.BeginTick();

        Assert.That(table.ResumeAppDataForTest(first.Slot), Is.Null,
            "a spent record that keeps the reference roots an application object in engine memory until that slot is used again");
    }

    [Test]
    [VerifiesRule("SUB-12")]
    [Category("Sensitive")]
    public void AReconnectRacingTheSweepOnRealThreadsStillProducesExactlyOneOutcome()
    {
        const int Rounds = 100;
        var account = new object();
        _sessions.Admit = (in AdmissionRequest _) => Admission.Accept(SessionRole.Player, SessionLimits.Default, account);

        for (var round = 0; round < Rounds; round++)
        {
            using var table = NewTable();

            var first = Connect(table, out var token);
            Drop(table, first);
            Drain(table);

            // The deadline is already behind both of them, so neither side can be ordered by construction: the consume refuses on the deadline it reads, the
            // sweep claims on the one it reads, and the compare-exchange is all that stands between them.
            _now += Ms(GraceMs + 1);

            var ready = new ManualResetEventSlim();
            var resumed = false;
            object handedBack = null;

            // TryConsumeResume rather than a whole admission, because this is the half that runs on a link thread: the events the other side produces are
            // read by the tick alone, and reading them from here would be the race this test is not about.
            var consumer = Task.Run(() =>
            {
                ready.Wait();
                // The appData is captured rather than discarded: it is the only thing a consume that reads its record in the wrong ORDER corrupts, and
                // discarding it here is what let two such defects through the first time these ran.
                resumed = table.TryConsumeResume(token, out _, out handedBack);
            });

            var sweeper = Task.Run(() =>
            {
                ready.Wait();
                table.BeginTick();
            });

            ready.Set();
            Task.WaitAll(consumer, sweeper);

            var expiries = 0;
            foreach (var e in Drain(table))
            {
                if (e.Kind == SessionEventKind.ResumeExpired)
                {
                    expiries++;
                }
            }

            // The sweep may not have reached the record before the consume took it; then the record is simply unlinked by the next tick and there is no event.
            table.BeginTick();
            foreach (var e in Drain(table))
            {
                if (e.Kind == SessionEventKind.ResumeExpired)
                {
                    expiries++;
                }
            }

            Assert.That((resumed ? 1 : 0) + expiries, Is.EqualTo(1),
                $"round {round}: exactly one of resumed and expired — both destroys an avatar under the client holding it, neither leaks it for ever");

            if (resumed)
            {
                Assert.That(handedBack, Is.SameAs(account), $"round {round}: the winner was handed the wrong session's handle, or none");
            }
        }
    }

    // ── the cap ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void PastTheCapTheOldestPendingResumeExpiresEarly()
    {
        using var table = NewTable(maxSessions: 8, maxPendingResumes: 2);

        var a = Connect(table, out var tokenA);
        var b = Connect(table, out var tokenB);
        var c = Connect(table, out var tokenC);

        Drop(table, a);
        _now += Ms(1);
        Drop(table, b);
        Drain(table);

        Assert.That(table.PendingResumeCount, Is.EqualTo(2));

        _now += Ms(1);
        Drop(table, c);

        var events = Drain(table);

        Assert.Multiple(() =>
        {
            Assert.That(table.PendingResumeCount, Is.EqualTo(2), "the cap is a cap, not a target");
            Assert.That(events, Has.Count.EqualTo(2), "one ResumeExpired and one Closed, in the same tick");
            Assert.That(events[0].Kind, Is.EqualTo(SessionEventKind.ResumeExpired));
            Assert.That(events[0].Session, Is.EqualTo(a), "the OLDEST promise is the one broken, so the client that just dropped keeps the one just made");
        });

        Assert.Multiple(() =>
        {
            Assert.That(Reconnect(table, tokenA), Is.EqualTo(SessionId.None), "and the evicted token names nothing");
            Assert.That(Reconnect(table, tokenB), Is.EqualTo(b));
            Assert.That(Reconnect(table, tokenC), Is.EqualTo(c));
        });
    }

    [Test]
    public void ASecondSessionOnOneSlotTakesTheRecordAndTheOlderPendingExpires()
    {
        using var table = NewTable(maxSessions: 1);

        var first = Connect(table, out var tokenFirst);
        Drop(table, first);
        Drain(table);

        // The slot comes back on the tick AFTER its Closed was delivered, never in it, so that an application reading that event can still look the session
        // up. With one slot in the table there is nowhere else for the next session to go, so this tick is what lets it open at all.
        Recycle(table);

        // One slot, so this is necessarily the same slot with the next generation — the case the record table being indexed by slot has to answer for.
        var second = Connect(table, out var tokenSecond);

        Assert.Multiple(() =>
        {
            Assert.That(second.Slot, Is.EqualTo(first.Slot));
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(tokenSecond, Is.Not.EqualTo(tokenFirst), "a recycled slot does not inherit the secret of the session that held it");
            Assert.That(table.PendingResumeCount, Is.EqualTo(1), "and the older pending is still standing while nothing has displaced it");
        });

        Drop(table, second);

        var events = Drain(table);

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(2));
            Assert.That(events[0].Kind, Is.EqualTo(SessionEventKind.ResumeExpired));
            Assert.That(events[0].Session, Is.EqualTo(first));
            Assert.That(table.PendingResumeCount, Is.EqualTo(1));
        });

        Recycle(table);

        Assert.That(Admit(table, out var third, tokenFirst), Is.True);
        table.BeginTick();

        Assert.That(Single(table, SessionEventKind.Opened).ResumedFrom, Is.EqualTo(SessionId.None), "the taken-over record cannot be resumed by its token");

        // That reconnect took the only slot, so reading the surviving record means closing it first — cleanly, which leaves no second pending behind.
        Drop(table, third, SessionCloseReason.ClientLeft);
        Drain(table);
        Recycle(table);

        Assert.That(Reconnect(table, tokenSecond), Is.EqualTo(second), "the surviving record is the newer one, and it is intact");
    }

    [Test]
    public void PendingResumesExpireInDeadlineOrderAndCostTheTickNothingUntilTheyDo()
    {
        const int Count = 32;

        using var table = NewTable(maxSessions: Count);
        var sessions = new List<SessionId>();

        for (var i = 0; i < Count; i++)
        {
            sessions.Add(Connect(table, out _));
        }

        for (var i = 0; i < Count; i++)
        {
            Drop(table, sessions[i]);
            _now += Ms(1);   // one millisecond between deadlines, so the order below is the order they were dropped in
        }

        Drain(table);

        Assert.That(table.PendingResumeCount, Is.EqualTo(Count));
        Assert.That(table.BeginTick(), Is.Zero, "nothing has expired, and the sweep read one record to find that out");

        // Far enough for the first half only.
        _now += Ms(GraceMs) - Ms(Count / 2);

        Assert.That(table.BeginTick(), Is.EqualTo(Count / 2));

        var events = Drain(table);

        Assert.Multiple(() =>
        {
            Assert.That(table.PendingResumeCount, Is.EqualTo(Count / 2));
            for (var i = 0; i < Count / 2; i++)
            {
                Assert.That(events[i].Kind, Is.EqualTo(SessionEventKind.ResumeExpired));
                Assert.That(events[i].Session, Is.EqualTo(sessions[i]), $"expiry {i} came out of deadline order");
            }
        });
    }
}
