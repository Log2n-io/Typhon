using System;
using NUnit.Framework;
using SwgTatooine.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A13: a link that dies leaves the player's body standing, and the client that comes back is given that same body.
/// </summary>
/// <remarks>
/// <para>
/// <b>What separates this from A5's logout is that nothing is read.</b> A login asks the character where it was and spawns a body there; a resume asks
/// nothing, because the body never left — so the cases below assert on the avatar's <see cref="EntityId"/> and on its exact position, not on a position
/// within a metre of a saved one. An implementation that respawned at the saved point would pass an "it is where I left it" check and fail these.
/// </para>
/// <para>
/// <b>The close code is the whole input.</b> The engine makes a session resumable for a link that died on its own and for nothing else, and
/// <c>SubscriptionConnection.OnClosed</c> reads that off the code: <c>Normal</c> with no error is a client that chose to leave, anything else is a link that
/// went. So these cases differ from A5's by one argument, and that argument is what a browser tab closing (1001) and a logout button (1000) actually send.
/// </para>
/// <para>
/// <b>Why a fresh login takes the body over rather than being refused.</b> The demo lets one account into the world once, and a held avatar still holds its
/// account — so without the takeover a player whose link died and whose client lost its token would be told "that account is already playing" about
/// themselves, for the whole grace. The grace exists to spare them a respawn, not to lock them out. It is handed the same body a resume would have given it,
/// because a client that proved the ACCOUNT has made a stronger claim than one that merely held a token.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class ResumeAvatarChecks
{
    private const int TickRateHz = 50;

    /// <summary>1001: what a browser sends when its tab closes, and what the engine reads as a link that went away on its own.</summary>
    private const ushort LinkDied = CloseCodes.GoingAway;

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

    private TatooineSim Start(double resumeGraceS = 60d, double saveIntervalS = TatooineReplication.DefaultSaveIntervalSeconds)
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;
        config.TickRateHz = TickRateHz;
        config.ResumeGraceS = resumeGraceS;
        config.AvatarSaveIntervalS = saveIntervalS;

        var sim = new TatooineSim(config);
        sim.Initialize();
        _harness = new SessionHarness(sim);
        return sim;
    }

    private static EntityId AnAccount(ulong id) => EntityId.FromRawValue(id);

    /// <summary>Connects a player and waits for its avatar.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="account">The account its token names.</param>
    /// <param name="expectedBindings">How many bindings should have happened by the time this returns.</param>
    /// <returns>The link and the avatar.</returns>
    private (FakeLink Link, EntityId Avatar) LogIn(TatooineSim sim, EntityId account, long expectedBindings = 1)
    {
        var link = _harness.Connect(TatooineReplication.PlayerKind, account);
        return Bound(sim, link, expectedBindings);
    }

    /// <summary>Waits until a link's session drives a body, and answers which.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="link">The client.</param>
    /// <param name="expectedBindings">The binding count to wait for.</param>
    /// <returns>The link and the avatar.</returns>
    private (FakeLink Link, EntityId Avatar) Bound(TatooineSim sim, FakeLink link, long expectedBindings)
    {
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= expectedBindings, "the session to be given a body");

        // Control is staged and applied by the next tick's prologue, so a read on the binding tick would see nothing and be right to.
        _harness.Ticks(2);

        var avatar = TatooineReplication.ControlledBy(sim.Dbe, link.Session);
        Assert.That(avatar, Is.Not.EqualTo(EntityId.Null), "the session was given no body");
        return (link, avatar);
    }

    /// <summary>Where an avatar is, read outside the tick.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="avatar">The avatar.</param>
    /// <returns>Its position and realm.</returns>
    private static (float X, float Y, float Z, ushort Realm) Where(TatooineSim sim, EntityId avatar)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        var body = tx.For<Player>().Open(avatar);
        var place = body.Read(Player.Bounds);
        return (place.X, place.Y, place.Z, body.Read(Player.Realm).Value);
    }

    /// <summary>Whether an avatar still exists at all.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="avatar">The avatar.</param>
    /// <returns><see langword="true"/> when the entity is still there.</returns>
    private static bool Exists(TatooineSim sim, EntityId avatar)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.For<Player>().TryOpen(avatar, out _);
    }

    /// <summary>Walks the avatar away from where it logged in, so that a respawn is distinguishable from a resume.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="link">The client.</param>
    /// <param name="avatar">Its avatar.</param>
    /// <returns>Where it ended up.</returns>
    private (float X, float Y, float Z) WalkAway(TatooineSim sim, FakeLink link, EntityId avatar)
    {
        var start = Where(sim, avatar);
        link.SendCommand(nameof(MoveTo), ClientSays.MoveTo(start.X + 500f, start.Z + 500f));
        _harness.Ticks(40);

        var moved = Where(sim, avatar);
        var travelled = MathF.Sqrt(((moved.X - start.X) * (moved.X - start.X)) + ((moved.Z - start.Z) * (moved.Z - start.Z)));
        Assert.That(travelled, Is.GreaterThan(1f), "the avatar did not move, so a respawn would be indistinguishable from a resume");
        return (moved.X, moved.Y, moved.Z);
    }

    // ── the hold ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void ALinkThatDiesLeavesTheAvatarStandingAndTheClientThatComesBackGetsThatSameBody()
    {
        using var sim = Start();
        var account = AnAccount(0xA13_0001);

        var (first, avatar) = LogIn(sim, account);
        var token = first.ResumeToken;
        Assert.That(token, Is.Not.Null, "a server with a grace tells every session what to come back with");

        var walked = WalkAway(sim, first, avatar);

        first.Close(LinkDied, "the link died");
        _harness.Ticks(4);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.ResumeHoldsStanding, Is.EqualTo(1), "the avatar is held, not retired");
            Assert.That(TatooineReplication.AvatarsDestroyed, Is.Zero);
            Assert.That(Exists(sim, avatar), Is.True, "the body is still in the world, which is what everyone else sees");
            Assert.That(Where(sim, avatar).X, Is.EqualTo(walked.X).Within(0.001f), "and it is standing where its player left it");
        });

        var (second, resumed) = Bound(sim, _harness.ConnectResuming(TatooineReplication.PlayerKind, account, token), 2);

        Assert.Multiple(() =>
        {
            Assert.That(resumed, Is.EqualTo(avatar), "THE SAME BODY: a resume hands the entity back, it does not spawn a new one at a saved point");
            Assert.That(TatooineReplication.AvatarsSpawned, Is.EqualTo(1), "nothing was spawned for the second session");
            Assert.That(TatooineReplication.AvatarsDestroyed, Is.Zero, "and nothing was retired");
            Assert.That(TatooineReplication.ResumesRebound, Is.EqualTo(1));
            Assert.That(TatooineReplication.ResumeHoldsStanding, Is.Zero, "the hold is spent");
            Assert.That(second.Session, Is.Not.EqualTo(first.Session), "a resume is a NEW session — the old identity is spent");
        });

        var after = Where(sim, resumed);

        Assert.Multiple(() =>
        {
            Assert.That(after.X, Is.EqualTo(walked.X).Within(0.001f));
            Assert.That(after.Z, Is.EqualTo(walked.Z).Within(0.001f));
        });
    }

    [Test]
    public void AClientThatSaidGoodbyeIsRetiredAtOnceAndItsTokenNamesNothing()
    {
        using var sim = Start();
        var account = AnAccount(0xA13_0002);

        var (link, avatar) = LogIn(sim, account);
        var token = link.ResumeToken;
        WalkAway(sim, link, avatar);

        // One argument different from the case above, and it is the one the engine reads: a clean 1000 with no error is a client that chose to leave.
        link.Close(CloseCodes.Normal, "logging out");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= 1, "the avatar to be retired");
        _harness.Ticks(2);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.ResumeHoldsStanding, Is.Zero, "a goodbye is not a dropped link, so nothing is held");
            Assert.That(TatooineReplication.ResumesHeld, Is.Zero);
            Assert.That(Exists(sim, avatar), Is.False);
        });

        var (_, fresh) = Bound(sim, _harness.ConnectResuming(TatooineReplication.PlayerKind, account, token), 2);

        Assert.Multiple(() =>
        {
            Assert.That(fresh, Is.Not.EqualTo(avatar), "the token named a session that was never resumable; this is an ordinary login");
            Assert.That(TatooineReplication.ResumesRebound, Is.Zero);
            Assert.That(TatooineReplication.AvatarsSpawned, Is.EqualTo(2));
        });
    }

    // ── the deadline ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AGraceThatElapsesRetiresTheHeldAvatarAndSavesWhereItStood()
    {
        // Short enough to elapse inside a test, and long enough that the close and the first few ticks are comfortably inside it.
        using var sim = Start(resumeGraceS: 0.25d);
        var account = AnAccount(0xA13_0003);

        var (link, avatar) = LogIn(sim, account);
        var walked = WalkAway(sim, link, avatar);

        link.Close(LinkDied, "the link died");
        _harness.Ticks(4);

        Assert.That(TatooineReplication.ResumeHoldsStanding, Is.EqualTo(1), "held first, which is what makes the expiry below mean anything");

        SessionHarness.Until(() => TatooineReplication.ResumeHoldsExpired >= 1, "the grace to elapse");
        _harness.Ticks(2);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.ResumeHoldsStanding, Is.Zero);
            Assert.That(TatooineReplication.AvatarsDestroyed, Is.EqualTo(1), "an expiry is a logout that waited to see whether it was one");
            Assert.That(Exists(sim, avatar), Is.False);
        });

        // And the position it was standing at was saved on the way out, so the next login is where the player actually was.
        var (_, next) = Bound(sim, _harness.Connect(TatooineReplication.PlayerKind, account), 2);
        var where = Where(sim, next);

        Assert.Multiple(() =>
        {
            Assert.That(next, Is.Not.EqualTo(avatar));
            Assert.That(where.X, Is.EqualTo(walked.X).Within(1f));
            Assert.That(where.Z, Is.EqualTo(walked.Z).Within(1f));
        });
    }

    // ── what the operator can see ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An expiry for a god session is not an orphan: it never had an avatar, and its close is resumable like any other.
    /// </summary>
    /// <remarks>
    /// <b>This is the case that decides whether the counter is worth having.</b> <c>ResumeExpired</c> reaches the demo for every resumable close, and the
    /// handler used to discard the ones with no hold behind them — correct for this session and indistinguishable from an avatar left standing for nobody,
    /// which is the state 245 bot characters were once found in. A counter that fired here too would have a zero that means nothing, so the predicate is
    /// what is under test as much as the count.
    /// </remarks>
    [Test]
    public void AGodSessionsExpiryIsNotAnOrphan()
    {
        using var sim = Start(resumeGraceS: 0.25d);

        var god = _harness.Connect(TatooineReplication.GodKind);
        _harness.Ticks(2);
        god.Close(LinkDied, "the link died");

        // 40 ticks at 50 Hz is about 0.8 s of wall clock against a 0.25 s grace, so the expiry has certainly been delivered — and read — by the end of it.
        // Waiting on the counter instead would be circular: the claim is that it stays at zero.
        _harness.Ticks(40);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.ResumeExpiriesWithoutHold, Is.Zero,
                "a god session has no body to leave standing, so its expiry is not a disagreement about one");
            Assert.That(TatooineReplication.ResumeHoldsStanding, Is.Zero, "and nothing is being held for it");
        });
    }

    /// <summary>
    /// A held avatar is visible as a standing hold while it waits, and the count goes back to zero when the grace retires it.
    /// </summary>
    /// <remarks>
    /// The population, not the events. A fleet whose sockets had all dropped reported 3 001 possessions taken and 0 released, which reads as a leak and
    /// was in part a report that never mentioned the state every one of those sessions was in: held, by design, for the grace. The run report prints this
    /// number now, and this case is what says the number tracks the thing.
    /// </remarks>
    [Test]
    public void AHoldIsCountedWhileItStandsAndNotAfterwards()
    {
        using var sim = Start(resumeGraceS: 0.25d);
        var account = AnAccount(0xA13_0009);

        var (link, avatar) = LogIn(sim, account);
        Assert.That(TatooineReplication.ResumeHoldsStanding, Is.Zero, "premise: nothing is held while the client is connected");

        link.Close(LinkDied, "the link died");
        _harness.Ticks(4);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.ResumeHoldsStanding, Is.EqualTo(1), "one body standing for a client that is not connected right now");
            Assert.That(Exists(sim, avatar), Is.True);
        });

        SessionHarness.Until(() => TatooineReplication.ResumeHoldsExpired >= 1, "the grace to elapse");
        _harness.Ticks(2);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.ResumeHoldsStanding, Is.Zero, "the hold is gone, so the population is back to zero");
            Assert.That(TatooineReplication.ResumeExpiriesWithoutHold, Is.Zero,
                "the expiry found its own hold, which is the whole of what this counter being zero claims");
        });
    }

    /// <summary>
    /// A takeover spends the hold, and the expiry that arrives afterwards is not counted as an orphan either.
    /// </summary>
    /// <remarks>
    /// The second benign case. The engine still owes the old session exactly one of its two events (SUB-12) and delivers the expiry after the takeover has
    /// already retired the hold, so the demo sees an expiry with nothing behind it — for a reason that is the system working.
    /// </remarks>
    [Test]
    public void AnExpiryAfterATakeoverIsNotAnOrphan()
    {
        using var sim = Start(resumeGraceS: 0.25d);
        var account = AnAccount(0xA13_000A);

        var (first, _) = LogIn(sim, account);
        first.Close(LinkDied, "the link died");
        _harness.Ticks(4);
        Assert.That(TatooineReplication.ResumeHoldsStanding, Is.EqualTo(1), "premise: the body is held");

        // The same account logs in fresh rather than resuming: the takeover retires the hold and hands this session the body.
        Bound(sim, _harness.Connect(TatooineReplication.PlayerKind, account), 2);
        Assert.That(TatooineReplication.ResumeHoldsTakenOver, Is.EqualTo(1), "premise: the hold was taken over, not expired");

        // Then the old session's grace runs out and the engine delivers its expiry.
        _harness.Ticks(40);

        Assert.That(TatooineReplication.ResumeExpiriesWithoutHold, Is.Zero,
            "the hold was spent by the takeover, so its expiry arriving with nothing behind it is expected");
    }
    // ── the takeover ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AFreshLoginForAHeldAccountTakesItsOwnBodyOverRatherThanBeingRefused()
    {
        using var sim = Start();
        var account = AnAccount(0xA13_0004);

        var (link, avatar) = LogIn(sim, account);
        var walked = WalkAway(sim, link, avatar);

        link.Close(LinkDied, "the link died");
        _harness.Ticks(4);

        Assert.That(TatooineReplication.ResumeHoldsStanding, Is.EqualTo(1));

        // No resume token: the player reopened the browser, or is on another device. The account hold is still standing for the held body.
        var (second, taken) = Bound(sim, _harness.Connect(TatooineReplication.PlayerKind, account), 2);

        Assert.Multiple(() =>
        {
            Assert.That(second.Closed, Is.Null, "refusing this login would lock the player out of their own character for the whole grace");
            Assert.That(TatooineReplication.LoginsRefusedDuplicate, Is.Zero);
            Assert.That(TatooineReplication.ResumeHoldsTakenOver, Is.EqualTo(1));
            Assert.That(TatooineReplication.ResumeHoldsStanding, Is.Zero, "the hold is resolved either way");

            // The SAME body, as a resume would have given it. This client proved the account, which is a stronger claim than a token, so there is nothing a
            // respawn would protect — and a respawn would mean destroying and spawning one player's body in one tick to put it back where it was.
            Assert.That(taken, Is.EqualTo(avatar));
            Assert.That(TatooineReplication.AvatarsSpawned, Is.EqualTo(1), "no second body for one player");
            Assert.That(TatooineReplication.AvatarsDestroyed, Is.Zero);
            Assert.That(TatooineReplication.ResumesRebound, Is.Zero, "this was not a resume: no token was presented");
        });

        var where = Where(sim, taken);

        Assert.Multiple(() =>
        {
            Assert.That(where.X, Is.EqualTo(walked.X).Within(1f));
            Assert.That(where.Z, Is.EqualTo(walked.Z).Within(1f));
        });
    }

    [Test]
    public void ASecondClientForAPlayingAccountIsStillRefused()
    {
        using var sim = Start();
        var account = AnAccount(0xA13_0005);

        LogIn(sim, account);

        // Nothing was dropped and nothing is held: this is the ordinary duplicate login, and the takeover above must not have weakened it.
        var second = _harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => TatooineReplication.LoginsRefusedDuplicate >= 1, "the second client to be refused");
        _harness.Ticks(3);

        Assert.Multiple(() =>
        {
            Assert.That(second.Closed?.Code, Is.EqualTo(TatooineReplication.AccountAlreadyPlayingCloseCode));
            Assert.That(TatooineReplication.ResumeHoldsTakenOver, Is.Zero);
            Assert.That(TatooineReplication.AvatarsSpawned, Is.EqualTo(1));
        });
    }

    // ── the shutdown ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An operator stopping the server saves a HELD avatar's position too, not only the ones a session is driving.
    /// </summary>
    /// <remarks>
    /// <b>A13 re-opened the loss A6's flush exists to prevent, and this is the case that would have found it.</b> A hold is deliberately out of
    /// <c>BoundPlayer</c>, and the shutdown flush walked only that — so a player who dropped mid-walk and whose server was stopped before their grace
    /// elapsed came back at the last cadence save, up to a whole interval of walking thrown away on the one path where nothing has to be lost. With only
    /// holds standing the flush returned at its first clause and wrote nothing at all.
    /// <para>
    /// The save interval is an hour so that a cadence write is impossible: if the position is saved, the shutdown is what saved it.
    /// </para>
    /// </remarks>
    [Test]
    public void AGracefulShutdownSavesAHeldAvatarsPositionToo()
    {
        using var sim = Start(saveIntervalS: 3_600d);
        var account = AnAccount(0xA13_0007);

        var (link, avatar) = LogIn(sim, account);
        var spawned = Saved(sim, account);
        var walked = WalkAway(sim, link, avatar);

        Assert.That(Saved(sim, account).X, Is.EqualTo(spawned.X).Within(0.01f), "the cadence must not have saved anything yet");

        link.Close(LinkDied, "the link died");
        _harness.Ticks(4);

        Assert.That(TatooineReplication.ResumeHoldsStanding, Is.EqualTo(1), "the body is held, so nothing else will write it back");

        TatooineReplication.RequestShutdown("test stop");
        _harness.Ticks(4);

        var saved = Saved(sim, account);

        Assert.Multiple(() =>
        {
            Assert.That(saved.X, Is.EqualTo(walked.X).Within(1.5f), "the shutdown did not write the held avatar's position back");
            Assert.That(saved.Z, Is.EqualTo(walked.Z).Within(1.5f));
            Assert.That(saved.SavedTicks, Is.GreaterThan(spawned.SavedTicks), "the character's save stamp did not move");
        });
    }

    /// <summary>Reads a character's saved position through the account index.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="account">The account whose character to read.</param>
    /// <returns>The saved position and the stamp.</returns>
    private static (float X, float Z, ushort Realm, long SavedTicks) Saved(TatooineSim sim, EntityId account)
    {
        Assert.That(CharacterStore.TryFind(sim.Dbe, account, out _, out var profile), Is.True, "the account has no character");
        return (profile.LastPosition.X, profile.LastPosition.Z, profile.LastRealm, profile.LastSavedUtcTicks);
    }

    // ── the option ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void AServerWithNoGraceIssuesNoTokenAndRetiresOnADroppedLink()
    {
        using var sim = Start(resumeGraceS: 0d);
        var account = AnAccount(0xA13_0006);

        var (link, avatar) = LogIn(sim, account);

        Assert.Multiple(() =>
        {
            Assert.That(link.ResumeToken, Is.Null, "0 means this server has no resume, and the all-zero token on the wire is how a client is told");

            // The other half of how a client is told, and the half an SDK branches on: ReconnectingClient presents its token only while
            // `now < close + limits.resumeGraceMs`, so a zero here is what makes it reconnect rather than resume. Read off the catalog the CLIENT was sent.
            Assert.That(link.Plan.Catalog.Limits.ResumeGraceMs, Is.Zero);
        });

        link.Close(LinkDied, "the link died");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= 1, "the avatar to be retired");
        _harness.Ticks(2);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.ResumesHeld, Is.Zero, "with no record there is nothing to hold the body on");
            Assert.That(Exists(sim, avatar), Is.False);
        });
    }
}
