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
    public void AFirstLoginCreatesACharacterAndSpawnsInATown()
    {
        using var sim = Start(Config());
        var before = Census(sim);

        var (_, avatar) = LogIn(sim, AnAccount(0x5001));

        var at = Where(sim, avatar);

        Assert.Multiple(() =>
        {
            Assert.That(TatooineReplication.CharactersCreated, Is.EqualTo(1), "a first login must create exactly one character");
            Assert.That(TatooineReplication.AvatarsSpawned, Is.EqualTo(1));
            Assert.That(CharacterCount(sim), Is.EqualTo(1));

            // Any town, not the capital: arrivals are spread across all seven by the map's playerWeight. What matters is that it is IN
            // one of them rather than in the open desert the origin is.
            Assert.That(TownHolding(sim, at.X, at.Z), Is.Not.Null, "a new avatar must arrive inside a town, not at the origin");
            Assert.That(at.Realm, Is.Zero);
            Assert.That(Census(sim).Total, Is.EqualTo(before.Total + 1), "the avatar must be ADDITIONAL to the pooled crowd");
        });
    }

    /// <summary>
    /// <b>Arrivals are spread over the town, which is the whole reason the spawn is a disc.</b> A single point put every new character
    /// on one tile: inside every other one's 192 m sphere, so each replica held the entire population and the client's per-session cost
    /// grew with the crowd instead of with the world — a thousand bot sessions reached 42 GB on that alone. It also showed a player
    /// logging in to a world reporting ten thousand players and rendering nobody, because the crowd was one tile wide.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void ArrivalsAreSpreadAcrossTheTowns()
    {
        using var sim = Start(Config());

        var points = new List<(float X, float Z)>();
        var towns = new HashSet<string>();
        for (ulong i = 0; i < 24; i++)
        {
            var (_, avatar) = LogIn(sim, AnAccount(0x6100 + i), expectedBindings: (long)i + 1);
            var at = Where(sim, avatar);
            points.Add((at.X, at.Z));

            var town = TownHolding(sim, at.X, at.Z);
            Assert.That(town, Is.Not.Null, $"account {i} arrived outside every town, at {at.X:F0},{at.Z:F0}");
            towns.Add(town);
        }

        var closest = float.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                var dx = points[i].X - points[j].X;
                var dz = points[i].Z - points[j].Z;
                closest = MathF.Min(closest, MathF.Sqrt((dx * dx) + (dz * dz)));
            }
        }

        Assert.Multiple(() =>
        {
            // A floor on the CLOSEST pair, which also says they are all distinct. The point of the spread is that neighbours fall out
            // of each other's sphere, not merely that two floats differ, and a disc that ever collapsed towards its centre would fail
            // here rather than quietly stack the crowd again.
            Assert.That(closest, Is.GreaterThan(1f), "the arrivals are spread, not jittered by centimetres");

            // <b>More than one town, which is the behaviour this test is named for.</b> Twenty-four accounts against weights whose
            // smallest is 5 % will not reach all seven reliably, and a test that demanded seven would be asserting the sampler's luck
            // rather than the distribution — so the claim is that arrivals are not all in one place.
            Assert.That(towns, Has.Count.GreaterThan(1), "arrivals must be distributed across towns, not stacked in the capital");
        });
    }

    /// <summary>Which town holds a point, or <see langword="null"/> when it is outside all of them.</summary>
    /// <param name="sim">The server, for its map.</param>
    /// <param name="x">World x.</param>
    /// <param name="z">World z.</param>
    /// <returns>The town's name, or null.</returns>
    private static string TownHolding(TatooineSim sim, float x, float z)
    {
        foreach (var city in sim.Map.Cities)
        {
            var dx = x - city.X;
            var dz = z - city.Z;
            if (MathF.Sqrt((dx * dx) + (dz * dz)) <= city.Radius)
            {
                return city.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// <c>--spawn-spread 0</c> keeps the old single point in the capital, which is what the cases that place fixtures near its centre
    /// depend on.
    /// </summary>
    [Test]
    [CancelAfter(60_000)]
    public void APinnedSpawnSpreadPutsEveryArrivalOnOnePoint()
    {
        var config = Config();
        config.SpawnSpread = 0;

        using var sim = Start(config);
        var capital = sim.Map.Cities[0];

        var (_, first) = LogIn(sim, AnAccount(0x6201));
        var (_, second) = LogIn(sim, AnAccount(0x6202), expectedBindings: 2);

        var a = Where(sim, first);
        var b = Where(sim, second);

        Assert.Multiple(() =>
        {
            Assert.That(MathF.Sqrt(((a.X - capital.X) * (a.X - capital.X)) + ((a.Z - capital.Z) * (a.Z - capital.Z))), Is.LessThan(1f));
            Assert.That(MathF.Sqrt(((b.X - capital.X) * (b.X - capital.X)) + ((b.Z - capital.Z) * (b.Z - capital.Z))), Is.LessThan(1f));
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

    /// <summary>
    /// A client that drops and reconnects with no tick in between is bound, not refused — which is what the release-before-bind
    /// ordering exists for, and what counting sessions instead of comparing them silently broke.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The close and the open have to reach ONE tick, and this is the shape that makes them.</b> Every other case in this fixture
    /// waits for the destroy and then ticks, so the two halves always land in different drains and the bug is invisible: the suite had
    /// a leak case doing twenty rounds of exactly that and it stayed green. Here nothing is waited for between the close and the
    /// connect, so the next <c>BeginTick</c> drains both events, and <c>Seen</c> loses one member while gaining another.
    /// </para>
    /// <para>
    /// <b>Why rounds rather than one attempt.</b> A free-running runtime may still tick between the two calls, which makes any single
    /// round the harmless interleaving. Twelve rounds against the same account make the same-tick landing certain enough to be a
    /// regression test, and each round asserts the thing that must hold in BOTH interleavings — so no round is flaky, and the case
    /// fails only if the same-tick one is handled wrongly. Measured against the old guard, it fails on the first or second round.
    /// </para>
    /// <para>
    /// The account is the SAME one throughout, because the damage is the account hold: the dead session's hold was never dropped, so
    /// the reconnect was refused <c>4103</c> — "that account is already playing", by its own corpse.
    /// </para>
    /// </remarks>
    [Test]
    [CancelAfter(180_000)]
    public void AReconnectInTheSameTickAsItsOwnDisconnectIsBound()
    {
        const int rounds = 12;

        using var sim = Start(Config(saveIntervalS: 0d));
        var account = AnAccount(0x5007);
        var (link, _) = LogIn(sim, account);

        for (var round = 0; round < rounds; round++)
        {
            // No wait between these two: that is the whole point of the case.
            link.Close(CloseCodes.Normal, "dropped");
            var next = _harness.Connect(TatooineReplication.PlayerKind, account);

            SessionHarness.Until(
                () => next.Closed != null || TatooineReplication.ControlledBy(sim.Dbe, next.Session) != EntityId.Null,
                $"round {round}'s reconnect to be bound or refused");

            Assert.That(
                next.Closed,
                Is.Null,
                $"round {round}: the reconnect was refused {next.Closed?.Code} — the dead session had not released the account");

            link = next;
        }

        // And the population is back to one body for one account, so neither the bind nor the retire double-counted.
        Assert.Multiple(() =>
        {
            Assert.That(Census(sim).Human, Is.EqualTo(1), "exactly one avatar must be driven by a session");
            Assert.That(TatooineReplication.AvatarsSpawned - TatooineReplication.AvatarsDestroyed, Is.EqualTo(1));
            Assert.That(TatooineReplication.CharactersCreated, Is.EqualTo(1), "one account has one character however often it reconnects");
        });
    }

    /// <summary>
    /// A second session for one account is refused ONCE, with the duplicate code, and is not then refused again as anonymous.
    /// </summary>
    /// <remarks>
    /// <b>A kick takes a tick to land, which is what made this a double refusal.</b> The refused session is still open on the next
    /// tick and still has no avatar, so the bind reconsiders it — and because the duplicate path dropped its identity on the way out,
    /// the second pass found none and sent <c>4102</c> "log in again" on top of the <c>4103</c> it had already sent. The advice was
    /// wrong (the token was perfectly good) and <c>LoginsRefusedAnonymous</c> counted an authenticated session.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void ASecondSessionForOneAccountIsRefusedOnceAsADuplicate()
    {
        using var sim = Start(Config(saveIntervalS: 0d));
        var account = AnAccount(0x5008);
        var (first, _) = LogIn(sim, account);

        var second = _harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => second.Closed != null, "the duplicate session to be refused");

        // Several more ticks: this is the window in which the second refusal used to arrive.
        _harness.Ticks(6);

        Assert.Multiple(() =>
        {
            Assert.That(second.Closed?.Code, Is.EqualTo(TatooineReplication.AccountAlreadyPlayingCloseCode));
            Assert.That(TatooineReplication.LoginsRefusedDuplicate, Is.EqualTo(1), "the duplicate must be counted once");
            Assert.That(
                TatooineReplication.LoginsRefusedAnonymous,
                Is.Zero,
                "the refused session must never be reconsidered as anonymous — the staged kick closes it before the next walk");
            Assert.That(first.Closed, Is.Null, "the incumbent must be untouched");
            Assert.That(Census(sim).Human, Is.EqualTo(1), "the duplicate must not have been given a body");
        });
    }

    /// <summary>
    /// A session that opens and closes without ever being bound leaves nothing behind in the avatar bookkeeping.
    /// </summary>
    /// <remarks>
    /// <b>The one leak the fixture's other leak case cannot see.</b> <c>RetireAvatars</c> walks the sessions that HAVE avatars, so a
    /// session that never got one was never swept: its <c>AccountOfSession</c> entry — made the moment the session opened — stayed for
    /// the life of the process. Every other observable comes back to where it started, which is why twenty rounds of login-and-logout
    /// never caught it. This case connects clients that go away before a bind can happen and asserts the dictionaries directly.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void ASessionThatClosesBeforeItIsBoundLeavesNothingBehind()
    {
        const int rounds = 15;

        using var sim = Start(Config(saveIntervalS: 0d));
        _ = sim;

        for (var round = 0; round < rounds; round++)
        {
            // Connected and dropped with no tick in between, so the bind never sees it as anything but gone.
            var link = _harness.Connect(TatooineReplication.PlayerKind);
            link.Close(CloseCodes.Normal, "changed my mind");
        }

        _harness.Ticks(6);

        var accounting = TatooineReplication.SessionAccounting;
        Assert.Multiple(() =>
        {
            Assert.That(accounting.Identities, Is.Zero, "an identity was kept for a session that is gone");
            Assert.That(accounting.Bound, Is.Zero);
            Assert.That(accounting.Characters, Is.Zero);
            Assert.That(accounting.AccountHolds, Is.Zero, "an account hold outlived the session that took it");
        });
    }

    // =======================================================================
    // A6 - the cadence, and what a kill -9 costs
    // =======================================================================

    /// <summary>
    /// A reopened database comes back with no avatars, so a login does not find a second body already wearing its character.
    /// </summary>
    /// <remarks>
    /// <b><c>Player</c> is <c>ClusterDurability.Checkpoint</c>, which is why this is worth asserting rather than assuming.</b> A run
    /// killed with avatars in the world could plausibly checkpoint them, and nothing sweeps <c>ControllerKind.Human</c> bodies at open —
    /// so the failure this guards against is a reopen that starts with bodies owned by session ids that no longer exist, in the spatial
    /// grid for ever, with the next login spawning a SECOND body for the same character. Measured: it does not happen, the reopened
    /// population is the simulation's own crowd and nothing else, and one login yields exactly one driven body. Pinned because the
    /// durability setting that makes it a question is still there.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void AReopenedDatabaseHasNoOrphanedAvatars()
    {
        var account = AnAccount(0x500A);

        using (var first = Start(Config(persist: true, saveIntervalS: 0d)))
        {
            LogIn(first, account);
            Assert.That(Census(first).Human, Is.EqualTo(1), "the first run must have an avatar to orphan");
            _harness.Ticks(8);
            _harness.Release();

            // No logout and no shutdown: dropped the way a kill -9 drops it, with the avatar still in the world.
        }

        using var second = Start(Config(persist: true, saveIntervalS: 0d));
        Assert.That(second.Reopened, Is.True, "the second run did not reopen the first's database");
        _harness.Ticks(4);

        var reopened = Census(second);
        Assert.That(reopened.Human, Is.Zero, "the reopened run came back with an avatar owned by a session that no longer exists");

        // And the character gets ONE body, not a second one beside a survivor. Whether the CHARACTER itself survived is not asserted
        // here and must not be: this case runs with the cadence off and too few ticks to reach a checkpoint, so the character row may
        // legitimately be gone. AKillWithNoLogoutCostsAtMostTheCadence is the case that owns character survival.
        var (_, againAvatar) = LogIn(second, account, expectedBindings: 1);
        var afterLogin = Census(second);
        Assert.Multiple(() =>
        {
            Assert.That(againAvatar, Is.Not.EqualTo(EntityId.Null), "the account could not log in after the reopen");
            Assert.That(afterLogin.Human, Is.EqualTo(1), "exactly one body may be driven by the one session");
            Assert.That(afterLogin.Total, Is.EqualTo(reopened.Total + 1), "the login must have added exactly one body");
        });
    }

    /// <summary>
    /// A graceful shutdown writes every live avatar's position, so stopping the server costs no walking at all.
    /// </summary>
    /// <remarks>
    /// <b>A6's cadence bounds what a <c>kill -9</c> costs, and it was wrongly also the bound on an operator's stop.</b> The shutdown
    /// branch kicks every session and returns before the avatar binding, so the last save before a Ctrl-C was whatever the cadence had
    /// last done — up to a full interval of movement thrown away on the one path where nothing had to be lost. The interval here is
    /// long enough that the cadence cannot be what writes the position: if it is saved, the shutdown saved it.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    public void AGracefulShutdownSavesEveryAvatarsPosition()
    {
        // Far longer than this case runs, so a cadence save is impossible and only the shutdown can account for a write.
        using var sim = Start(Config(saveIntervalS: 3_600d));
        var account = AnAccount(0x5009);
        var (link, avatar) = LogIn(sim, account);

        var spawned = Saved(sim, account);

        // Move the avatar somewhere the saved position is not, and wait until the world agrees it is there.
        link.SendCommand("MoveTo", ClientSays.MoveTo(spawned.X + 60f, spawned.Z + 60f));
        SessionHarness.Until(
            () =>
            {
                var now = Where(sim, avatar);
                return Math.Abs(now.X - spawned.X) > 5f || Math.Abs(now.Z - spawned.Z) > 5f;
            },
            "the avatar to walk away from where it spawned");

        var walked = Where(sim, avatar);
        Assert.That(Saved(sim, account).X, Is.EqualTo(spawned.X).Within(0.01f), "the cadence must not have saved anything yet");

        TatooineReplication.RequestShutdown("test stop");
        _harness.Ticks(4);

        var saved = Saved(sim, account);
        Assert.Multiple(() =>
        {
            Assert.That(saved.X, Is.EqualTo(walked.X).Within(1.5f), "the shutdown did not write the avatar's position back");
            Assert.That(saved.Z, Is.EqualTo(walked.Z).Within(1.5f));
            Assert.That(saved.SavedTicks, Is.GreaterThan(spawned.SavedTicks), "the character's save stamp did not move");
        });
    }

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
