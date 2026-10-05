using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using SwgTatooine.Accounts;
using Typhon.Engine;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace SwgTatooine.Tests;

/// <summary>
/// A5: a login spawns the account's avatar where its character left off, and a logout saves and destroys it.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are the acceptance criteria that distinguish A5 from the possession it replaced.</b> Possession satisfied "a session
/// drives a body"; it could not satisfy "the same body, in the same place, next time", because the body was whichever pooled player
/// happened to be free. So the cases below are about identity across a disconnect and across a restart, and about the two leaks a
/// spawn-per-session introduces that a borrow-per-session could not: an avatar nobody destroys, and a character created twice.
/// </para>
/// <para>
/// The avatar is moved with a real <c>MoveTo</c> intent rather than by writing its placement, which would be an off-tick write to a
/// spatial archetype — the one thing <c>EW-02</c> forbids outright.
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
internal sealed class AvatarLoginChecks
{
    private const int TickRateHz = 50;

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

    private SimConfig Config(bool persist = false, double saveIntervalS = TatooineReplication.DefaultSaveIntervalSeconds)
    {
        var config = Worlds.Small(_dir);
        config.Unpaced = false;
        config.TickRateHz = TickRateHz;
        config.Persist = persist;
        config.AvatarSaveIntervalS = saveIntervalS;
        return config;
    }

    /// <summary>An account id nothing resolves — see <c>SessionHarness.NextSyntheticAccount</c> for why that is legitimate.</summary>
    private static EntityId AnAccount(ulong id) => EntityId.FromRawValue(id);

    /// <summary>Starts a server and a harness against it.</summary>
    /// <param name="config">The world.</param>
    /// <returns>The simulation; the caller disposes it.</returns>
    private TatooineSim Start(SimConfig config)
    {
        var sim = new TatooineSim(config);
        sim.Initialize();
        _harness = new SessionHarness(sim);
        return sim;
    }

    /// <summary>Connects a player for an account and waits until its avatar exists.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="account">The account the token names.</param>
    /// <param name="expectedBindings">How many bindings should have happened by the time this returns.</param>
    /// <returns>The link and the avatar.</returns>
    private (FakeLink Link, EntityId Avatar) LogIn(TatooineSim sim, EntityId account, long expectedBindings = 1)
    {
        var link = _harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= expectedBindings, "the avatar to be spawned");

        // The bind stages a Control request, which the engine applies in the NEXT tick's prologue — so a case that read the session's
        // subject on the binding tick would see nothing and be right to.
        _harness.Ticks(2);

        var avatar = TatooineReplication.ControlledBy(sim.Dbe, link.Session);
        Assert.That(avatar, Is.Not.EqualTo(EntityId.Null), "the session was given no body");
        return (link, avatar);
    }

    /// <summary>Where an avatar is and which realm it is in, read outside the tick.</summary>
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

    /// <summary>How many players exist, and how many of those a session drives.</summary>
    /// <param name="sim">The server.</param>
    /// <returns>The two counts.</returns>
    private static (int Total, int Human) Census(TatooineSim sim)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        var total = 0;
        var human = 0;
        foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
        {
            var control = cluster.GetReadOnlySpan(Player.Control);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                total++;
                if (control[slot].Kind != ControllerKind.InProcess)
                {
                    human++;
                }
            }
        }

        return (total, human);
    }

    /// <summary>How many characters exist, for the "created twice" leak.</summary>
    /// <param name="sim">The server.</param>
    /// <returns>The count.</returns>
    private static int CharacterCount(TatooineSim sim)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        var count = 0;
        foreach (var cluster in tx.For<CharacterArch>().GetClusterEnumerator())
        {
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Walks the avatar away from where it logged in, and returns where it ended up.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="link">The client.</param>
    /// <param name="avatar">Its avatar.</param>
    /// <returns>The position after walking.</returns>
    /// <remarks>
    /// A real intent, repeated over enough ticks to cover ground. Writing the placement directly from the test would be an off-tick
    /// write to a spatial archetype, which <c>EW-02</c> forbids — and would also not prove that what the server saves is what the
    /// server simulated.
    /// </remarks>
    private (float X, float Y, float Z) WalkAway(TatooineSim sim, FakeLink link, EntityId avatar)
    {
        var start = Where(sim, avatar);

        // Far enough that one tick cannot reach it, so the avatar is genuinely in motion when the ticks below run.
        link.SendCommand(nameof(MoveTo), ClientSays.MoveTo(start.X + 500f, start.Z + 500f));
        _harness.Ticks(40);

        var moved = Where(sim, avatar);
        var travelled = MathF.Sqrt(((moved.X - start.X) * (moved.X - start.X)) + ((moved.Z - start.Z) * (moved.Z - start.Z)));
        Assert.That(travelled, Is.GreaterThan(1f), "the avatar did not move, so a saved position would be indistinguishable from the spawn point");
        return (moved.X, moved.Y, moved.Z);
    }

    /// <summary>A first login creates a character and spawns the avatar at the configured spawn point.</summary>
    /// <remarks>
    /// The spawn point is the capital's centre, taken from the map — not the origin, which on this planet is empty desert. A regression
    /// to the origin would put every new player a long way from anything, which reads as a broken client rather than a wrong constant.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void AFirstLoginCreatesACharacterAndSpawnsAtTheCapital()
    {
        using var sim = Start(Config());
        var before = Census(sim);

        var (_, avatar) = LogIn(sim, AnAccount(0x5001));

        var at = Where(sim, avatar);
        var capital = sim.Map.Cities[0];
        var distance = MathF.Sqrt(((at.X - capital.X) * (at.X - capital.X)) + ((at.Z - capital.Z) * (at.Z - capital.Z)));

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.CharactersCreated, Is.EqualTo(1), "a first login must create exactly one character");
            Assert.That(TatooineReplication.AvatarsSpawned, Is.EqualTo(1));
            Assert.That(CharacterCount(sim), Is.EqualTo(1));
            Assert.That(distance, Is.LessThan(1f), "a new avatar must arrive at the capital, not at the origin");
            Assert.That(at.Realm, Is.Zero);
            Assert.That(Census(sim).Total, Is.EqualTo(before.Total + 1), "the avatar must be ADDITIONAL to the pooled crowd");
        });
    }

    /// <summary>
    /// AC A-3: log out, log in again, and the avatar is within a metre of where it left, in the same realm.
    /// </summary>
    /// <remarks>
    /// <b>The case possession could not pass.</b> A session used to be handed whichever pooled player was free, so the second login's
    /// body was a different character in a different place — which is the requirement this phase exists for.
    /// </remarks>
    [Test]
    [CancelAfter(90_000)]
    public void LogOutAndBackIn_TheAvatarIsWhereItLeft()
    {
        using var sim = Start(Config());
        var account = AnAccount(0x5002);

        var (first, avatar) = LogIn(sim, account);
        var left = WalkAway(sim, first, avatar);
        var leftRealm = Where(sim, avatar).Realm;

        first.Close(CloseCodes.Normal, "logging out");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= 1, "the avatar to be saved and destroyed");
        _harness.Ticks(2);

        var (_, second) = LogIn(sim, account, expectedBindings: 2);
        var back = Where(sim, second);
        var drift = MathF.Sqrt(((back.X - left.X) * (back.X - left.X)) + ((back.Z - left.Z) * (back.Z - left.Z)));

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Not.EqualTo(avatar), "the second login must be a NEW body — the first was destroyed");
            Assert.That(drift, Is.LessThan(1f), $"came back {drift:F2} m from where it logged out");
            Assert.That(back.Realm, Is.EqualTo(leftRealm));
            Assert.That(TatooineReplication.CharactersCreated, Is.EqualTo(1), "the second login must REUSE the character, not create one");
            Assert.That(CharacterCount(sim), Is.EqualTo(1));
            Assert.That(TatooineReplication.LocationsSaved, Is.GreaterThanOrEqualTo(1));
        });
    }

    /// <summary>
    /// AC A-4: the same, across a restart of the server.
    /// </summary>
    /// <remarks>
    /// <b>What this adds over A-3 is the WAL.</b> A-3 passes on in-memory state alone; this one closes the database and opens it
    /// again, so the character's position has to have been durable rather than merely remembered. <c>Character</c> is
    /// <c>StorageMode.Versioned</c> precisely for this.
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    public void AcrossARestart_TheAvatarIsStillWhereItLeft()
    {
        var account = AnAccount(0x5003);
        (float X, float Y, float Z) left;
        ushort leftRealm;

        using (var first = Start(Config(persist: true)))
        {
            var (link, avatar) = LogIn(first, account);
            left = WalkAway(first, link, avatar);
            leftRealm = Where(first, avatar).Realm;

            link.Close(CloseCodes.Normal, "logging out");
            SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= 1, "the avatar to be saved and destroyed");
            _harness.Ticks(2);
            _harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();

        using var second = Start(Config(persist: true));
        Assert.That(second.Reopened, Is.True, "the second run must have reopened the first's database, or this proves nothing");

        var (_, reborn) = LogIn(second, account);
        var back = Where(second, reborn);
        var drift = MathF.Sqrt(((back.X - left.X) * (back.X - left.X)) + ((back.Z - left.Z) * (back.Z - left.Z)));

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.CharactersCreated, Is.Zero, "the character must have survived the restart, not been created again");
            Assert.That(drift, Is.LessThan(1f), $"came back {drift:F2} m from where it logged out before the restart");
            Assert.That(back.Realm, Is.EqualTo(leftRealm));
        });
    }

    /// <summary>One account may be in the world once: a second session for it is refused and told why.</summary>
    /// <remarks>
    /// Two avatars for one account would be two bodies with one wallet, and whichever logged out last would overwrite the other's
    /// position — so "where you were" would depend on the order two of your own clients happened to close.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void ASecondSessionForOneAccount_IsRefused()
    {
        using var sim = Start(Config());
        var account = AnAccount(0x5004);

        var (incumbent, avatar) = LogIn(sim, account);

        var intruder = _harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => TatooineReplication.LoginsRefusedDuplicate >= 1, "the second session for one account to be refused");
        SessionHarness.Until(() => intruder.Closed != null, "the refused session's link to be closed");
        _harness.Ticks(3);

        Assert.Multiple(() =>
        {
            Assert.That(intruder.Closed.Value.Code, Is.EqualTo(TatooineReplication.AccountAlreadyPlayingCloseCode),
                "a client must be able to tell 'already playing' from 'come back later'");
            Assert.That(intruder.Closed.Value.Reason, Is.Not.Null.And.Not.Empty);
            Assert.That(TatooineReplication.AvatarsSpawned, Is.EqualTo(1), "the refused session must not have been given a body");
            Assert.That(Census(sim).Human, Is.EqualTo(1), "exactly one body may be driven by a session");
            Assert.That(TatooineReplication.ControlledBy(sim.Dbe, intruder.Session), Is.EqualTo(EntityId.Null));

            // And the incumbent is untouched, which is the half that makes the refusal the right behaviour rather than merely a refusal.
            Assert.That(TatooineReplication.ControlledBy(sim.Dbe, incumbent.Session), Is.EqualTo(avatar));
            Assert.That(incumbent.Closed, Is.Null, "the session that was already playing must not have been disturbed");
        });
    }

    /// <summary>
    /// AC A-8: the pooled crowd is untouched by a human walking around.
    /// </summary>
    /// <remarks>
    /// <b>An avatar is additional, not borrowed.</b> The 320 x <c>--pop</c> pooled players stay <c>InProcess</c> and keep being driven
    /// by <c>PlayerThink</c>; nothing in A5 claims one. A regression to possession would show up as a pooled player going
    /// <c>Human</c> and the total staying flat.
    /// </remarks>
    [Test]
    [CancelAfter(90_000)]
    public void ThePooledCrowdIsUnaffectedByAHuman()
    {
        using var sim = Start(Config());
        var before = Census(sim);
        Assert.That(before.Human, Is.Zero, "no pooled player may start out possessed");

        var (link, avatar) = LogIn(sim, AnAccount(0x5005));
        WalkAway(sim, link, avatar);

        var during = Census(sim);
        Assert.Multiple(() =>
        {
            Assert.That(during.Total, Is.EqualTo(before.Total + 1), "exactly one body was added");
            Assert.That(during.Human, Is.EqualTo(1), "and exactly one is driven by a session");
            Assert.That(during.Total - during.Human, Is.EqualTo(before.Total), "the pooled crowd's count must be unchanged");
        });

        link.Close(CloseCodes.Normal, "leaving");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= 1, "the avatar to go");
        _harness.Ticks(3);

        var after = Census(sim);
        Assert.Multiple(() =>
        {
            Assert.That(after.Total, Is.EqualTo(before.Total), "the world must come back to the population it started with");
            Assert.That(after.Human, Is.Zero);
        });
    }

    /// <summary>
    /// AC A-9, scaled to a fixture: many logins and logouts leak no avatar, no character and no binding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three leaks, and they fail differently.</b> An avatar nobody destroys stays in every awareness query for the life of the
    /// process; a character created on each login means the saved position is written to a row the next login does not read, so
    /// "where you were" silently becomes "where you started"; and a binding left in the maps means an account can never log in again.
    /// </para>
    /// <para>
    /// Twenty rather than the criterion's fifty, because each round trip costs several ticks of a real runtime and the suite's budget
    /// is seconds. Fifty belongs in the phase-exit run against a live server, with the Workbench attached — which is W5's job and is
    /// where the criterion is actually met.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(180_000)]
    public void ManyLoginsAndLogouts_LeakNothing()
    {
        const int rounds = 20;

        // The cadence is OFF for this case, so LocationsSaved counts logouts and nothing else. With it on, the assertion below is
        // timing-dependent: this case spins real ticks waiting on each round, and a slower run crosses the save interval and records
        // one extra write — which it did, as 21 against 20, under the full suite but never in isolation. The cadence has its own
        // cases; this one is about leaks.
        using var sim = Start(Config(saveIntervalS: 0d));
        var before = Census(sim);
        var account = AnAccount(0x5006);
        var avatars = new HashSet<EntityId>();

        for (var i = 0; i < rounds; i++)
        {
            var (link, avatar) = LogIn(sim, account, expectedBindings: i + 1);
            avatars.Add(avatar);

            link.Close(CloseCodes.Normal, "round over");
            SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= i + 1, $"round {i}'s avatar to be destroyed");
            _harness.Ticks(2);
        }

        _harness.Ticks(4);
        var after = Census(sim);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.AvatarsSpawned, Is.EqualTo(rounds));
            Assert.That(TatooineReplication.AvatarsDestroyed, Is.EqualTo(rounds), "every avatar spawned must have been destroyed");
            Assert.That(avatars, Has.Count.EqualTo(rounds), "each round must have had a body of its own");
            Assert.That(after.Total, Is.EqualTo(before.Total), "the population must come back to where it started");
            Assert.That(after.Human, Is.Zero, "nothing may still be marked as driven by a session");
            Assert.That(TatooineReplication.CharactersCreated, Is.EqualTo(1), "one account has one character, however many times it logs in");
            Assert.That(CharacterCount(sim), Is.EqualTo(1));
            Assert.That(TatooineReplication.LocationsSaved, Is.EqualTo(rounds), "every logout must have written the position back, and only the logouts");
        });

        // And the account is still usable, which is what a leaked binding would prevent.
        var (_, final) = LogIn(sim, account, expectedBindings: rounds + 1);
        Assert.That(final, Is.Not.EqualTo(EntityId.Null), "the account could not log in again, so a binding leaked");
    }

    // =======================================================================
    // A6 - the cadence, and what a kill -9 costs
    // =======================================================================

    /// <summary>Reads a character's saved position through the account index.</summary>
    /// <param name="sim">The server.</param>
    /// <param name="account">The account whose character to read.</param>
    /// <returns>The saved position and realm.</returns>
    private static (float X, float Z, ushort Realm, long SavedTicks) Saved(TatooineSim sim, EntityId account)
    {
        Assert.That(CharacterStore.TryFind(sim.Dbe, account, out _, out var profile), Is.True, "the account has no character");
        return (profile.LastPosition.X, profile.LastPosition.Z, profile.LastRealm, profile.LastSavedUtcTicks);
    }

    /// <summary>
    /// The cadence writes a live avatar's position back WITHOUT a logout, which is what bounds a hard kill (A6).
    /// </summary>
    /// <remarks>
    /// A short interval so the case costs ticks rather than seconds. The assertion is the pair: the character's saved position tracks
    /// the avatar while it is still logged in, and the counter moved — either alone would pass against a save that happened at login
    /// and never again.
    /// </remarks>
    [Test]
    [CancelAfter(90_000)]
    public void TheCadenceSavesWhileTheAvatarIsStillLoggedIn()
    {
        // Four ticks at 50 Hz. Rounded to ticks by ConfigureAvatars, which is why this is expressed in seconds.
        using var sim = Start(Config(saveIntervalS: 4d / TickRateHz));
        Assert.That(TatooineReplication.SaveIntervalSeconds, Is.GreaterThan(0d), "the cadence is disabled, so this case tests nothing");

        var account = AnAccount(0x6001);
        var (link, avatar) = LogIn(sim, account);
        var atLogin = Saved(sim, account);

        var walked = WalkAway(sim, link, avatar);

        // No logout. Enough ticks that the stagger cannot miss this one session's slot.
        _harness.Ticks(12);

        var saved = Saved(sim, account);
        var drift = MathF.Sqrt(((saved.X - walked.X) * (saved.X - walked.X)) + ((saved.Z - walked.Z) * (saved.Z - walked.Z)));

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.LocationsSaved, Is.GreaterThan(0), "the cadence never ran");
            Assert.That(saved.SavedTicks, Is.GreaterThan(atLogin.SavedTicks), "the saved stamp did not move, so nothing was written after the login");
            Assert.That(drift, Is.LessThan(5f), $"the saved position is {drift:F2} m from where the avatar actually is");
            Assert.That(TatooineReplication.AvatarsDestroyed, Is.Zero, "the avatar must still be logged in for this to mean anything");
        });
    }

    /// <summary>With the cadence off, nothing is written until the logout — which is the state A6 exists to remove.</summary>
    /// <remarks>
    /// <b>The complement, and it is what makes the case above falsifiable.</b> If the position were being written by something other
    /// than the cadence — the login, a stray save in the intent path — the case above would pass with the cadence deleted. This one
    /// turns the cadence off and requires that nothing happens.
    /// </remarks>
    [Test]
    [CancelAfter(90_000)]
    public void WithTheCadenceOff_NothingIsSavedUntilTheLogout()
    {
        using var sim = Start(Config(saveIntervalS: 0d));
        Assert.That(TatooineReplication.SaveIntervalSeconds, Is.Zero, "the cadence must be disabled for this case");

        var account = AnAccount(0x6002);
        var (link, avatar) = LogIn(sim, account);
        var atLogin = Saved(sim, account);

        WalkAway(sim, link, avatar);
        _harness.Ticks(30);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.LocationsSaved, Is.Zero, "something wrote the position with the cadence off");
            Assert.That(Saved(sim, account).SavedTicks, Is.EqualTo(atLogin.SavedTicks));
        });

        // And the logout still saves, so turning the cadence off costs the cadence and nothing else.
        link.Close(CloseCodes.Normal, "leaving");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= 1, "the logout to save and destroy");
        _harness.Ticks(2);

        Assert.That(TatooineReplication.LocationsSaved, Is.EqualTo(1), "the logout must still write the position");
    }

    /// <summary>
    /// AC A-5: a server killed with no logout costs at most the cadence's worth of walking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The kill is modelled by disposing the simulation with the client still connected</b> — no <c>Close</c>, so no session event,
    /// so no logout path runs. That is as close to <c>SIGKILL</c> as a fixture can get in-process: what it cannot model is losing the
    /// WAL's unflushed tail, which is the engine's own durability contract and has its own coverage.
    /// </para>
    /// <para>
    /// The assertion is deliberately one-sided. It requires that the character came back somewhere the avatar actually walked and NOT
    /// at the spawn point — which is the failure A6 removes — rather than pinning a distance the tick rate and the run speed would make
    /// brittle.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    public void AKillWithNoLogoutCostsAtMostTheCadence()
    {
        var account = AnAccount(0x6003);
        (float X, float Z) spawn;
        (float X, float Y, float Z) walked;

        using (var first = Start(Config(persist: true, saveIntervalS: 4d / TickRateHz)))
        {
            var (link, avatar) = LogIn(first, account);
            spawn = (Where(first, avatar).X, Where(first, avatar).Z);
            walked = WalkAway(first, link, avatar);

            // Let the cadence run, then pull the plug: no Close, no logout, no clean shutdown of the session.
            _harness.Ticks(12);
            _harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();

        using var second = Start(Config(persist: true, saveIntervalS: 4d / TickRateHz));
        Assert.That(second.Reopened, Is.True, "the second run did not reopen the first's database");

        var (_, reborn) = LogIn(second, account);
        var back = Where(second, reborn);

        var fromWalked = MathF.Sqrt(((back.X - walked.X) * (back.X - walked.X)) + ((back.Z - walked.Z) * (back.Z - walked.Z)));
        var fromSpawn = MathF.Sqrt(((back.X - spawn.X) * (back.X - spawn.X)) + ((back.Z - spawn.Z) * (back.Z - spawn.Z)));
        var walkedDistance = MathF.Sqrt(((walked.X - spawn.X) * (walked.X - spawn.X)) + ((walked.Z - spawn.Z) * (walked.Z - spawn.Z)));

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.CharactersCreated, Is.Zero, "the character did not survive the kill");
            Assert.That(fromSpawn, Is.GreaterThan(walkedDistance * 0.5f),
                $"came back {fromSpawn:F1} m from the spawn after walking {walkedDistance:F1} m — the cadence did not save, so the session's walking was lost");
            Assert.That(fromWalked, Is.LessThan(walkedDistance),
                "came back further from where it was killed than the spawn point is, which is not a cadence at all");
        });
    }
}
