using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// A2: signup and login against a live engine, from threads that are not the tick's.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fixture runs a real world rather than a bare engine, and that is the point.</b> <see cref="AccountStore"/>'s whole
/// claim is that an account can be written from a Kestrel thread <i>while the simulation ticks</i> — <c>EW-02</c> allows it
/// because <c>Account</c> is Versioned on a non-spatial archetype. A fixture holding a quiescent engine would pass without
/// exercising the only thing in doubt, so <see cref="WhileTheWorldTicks_ConcurrentSignupsAndLogins_AllSucceed"/> keeps the
/// tick loop running underneath.
/// </para>
/// <para>
/// <b>A-2 is the criterion these were written for</b>: a duplicate name must be refused <i>by the unique index</i>, not by a
/// lookup followed by an insert. <see cref="SimultaneousSignupsForOneName_ExactlyOneWins"/> is the test that can tell the
/// difference — a pre-check would let both through under contention.
/// </para>
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class AccountStoreChecks
{
    private string _dir;
    private TatooineSim _sim;

    /// <summary>A password that passes the length floor, reused where the password is not what is under test.</summary>
    private const string GoodPassword = "bantha-poodoo";

    [SetUp]
    public void SetUp()
    {
        _dir = Worlds.NewDirectory();
        var config = Worlds.Small(_dir);
        config.WarmTicks = 0;
        config.MeasuredTicks = 0;
        _sim = new TatooineSim(config);
        _sim.Initialize();
    }

    [TearDown]
    public void TearDown()
    {
        _sim?.Dispose();
        _sim = null;
        Worlds.Delete(_dir);
    }

    private DatabaseEngine Dbe => _sim.Dbe;

    /// <summary>AC A-1 (the identity half): an account can be made and then logged into.</summary>
    [Test]
    [CancelAfter(15_000)]
    [Category("Integration")]
    public void Signup_ThenLogin_Succeeds()
    {
        Assert.That(AccountStore.TrySignup(Dbe, "Han_Solo", GoodPassword, out var created), Is.EqualTo(AccountStore.SignupOutcome.Created));
        Assert.That(created, Is.Not.EqualTo(default(EntityId)));

        var outcome = AccountStore.TryLogin(Dbe, "Han_Solo", GoodPassword, out var loggedIn);
        Assert.Multiple(() =>
        {
            Assert.That(outcome, Is.EqualTo(AccountStore.LoginOutcome.Ok));
            Assert.That(loggedIn, Is.EqualTo(created), "login must resolve the account that signup created");
        });
    }

    /// <summary>The name is normalised, so the case somebody types is not part of their identity.</summary>
    /// <remarks>
    /// Both halves matter. Logging in with a different case must WORK (or the account is unreachable from a phone keyboard),
    /// and signing up with a different case must be REFUSED (or two indistinguishable accounts exist).
    /// </remarks>
    [Test]
    [CancelAfter(15_000)]
    [Category("Integration")]
    public void NameCase_IsNotPartOfTheIdentity()
    {
        Assert.That(AccountStore.TrySignup(Dbe, "Leia", GoodPassword, out var created), Is.EqualTo(AccountStore.SignupOutcome.Created));

        Assert.Multiple(() =>
        {
            Assert.That(AccountStore.TryLogin(Dbe, "LEIA", GoodPassword, out var byShout), Is.EqualTo(AccountStore.LoginOutcome.Ok));
            Assert.That(byShout, Is.EqualTo(created));
            Assert.That(AccountStore.TrySignup(Dbe, "lEiA", GoodPassword, out _), Is.EqualTo(AccountStore.SignupOutcome.NameTaken),
                "a differently-cased name is the same name, or two accounts nobody can tell apart exist");
        });
    }

    /// <summary>AC A-2: a taken name is refused, and the refusal is the index's.</summary>
    [Test]
    [CancelAfter(15_000)]
    [Category("Integration")]
    public void SignupWithATakenName_IsRefused()
    {
        Assert.That(AccountStore.TrySignup(Dbe, "Chewie", GoodPassword, out _), Is.EqualTo(AccountStore.SignupOutcome.Created));
        Assert.That(AccountStore.TrySignup(Dbe, "Chewie", "a-different-password", out var second), Is.EqualTo(AccountStore.SignupOutcome.NameTaken));
        Assert.That(second, Is.EqualTo(default(EntityId)), "a refused signup must hand back no entity");

        // And the first account's password still works — the refused signup must not have overwritten anything.
        Assert.That(AccountStore.TryLogin(Dbe, "Chewie", GoodPassword, out _), Is.EqualTo(AccountStore.LoginOutcome.Ok));
    }

    /// <summary>
    /// AC A-2, the half a pre-check cannot pass: N threads signing up one name at once, and exactly one account exists after.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that distinguishes the index from a lookup.</b> <c>TrySignup</c> deliberately does not probe for the
    /// name first; it spawns and lets the unique B+Tree decide. A probe-then-insert implementation passes
    /// <see cref="SignupWithATakenName_IsRefused"/> and fails here, because every thread's probe comes back empty before any
    /// of them commits.
    /// </remarks>
    [Test]
    [CancelAfter(30_000)]
    [Category("Integration")]
    public void SimultaneousSignupsForOneName_ExactlyOneWins()
    {
        const int racers = 8;
        var outcomes = new AccountStore.SignupOutcome[racers];
        var ids = new EntityId[racers];
        using var start = new Barrier(racers);

        var runners = new Task[racers];
        for (var i = 0; i < racers; i++)
        {
            var slot = i;
            runners[slot] = Task.Run(() =>
            {
                // The barrier is what makes this a race rather than a sequence: every thread has its KDF work done and is
                // sitting on the commit when the last one arrives.
                start.SignalAndWait();
                outcomes[slot] = AccountStore.TrySignup(Dbe, "Lando", GoodPassword, out ids[slot]);
            });
        }

        Task.WaitAll(runners);

        var created = 0;
        var taken = 0;
        var distinct = new HashSet<EntityId>();
        for (var i = 0; i < racers; i++)
        {
            switch (outcomes[i])
            {
                case AccountStore.SignupOutcome.Created:
                    created++;
                    distinct.Add(ids[i]);
                    break;
                case AccountStore.SignupOutcome.NameTaken:
                    taken++;
                    break;
                default:
                    Assert.Fail($"thread {i} got {outcomes[i]}, which is neither winning nor losing the race");
                    break;
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.EqualTo(1), "exactly one signup may win a contested name");
            Assert.That(taken, Is.EqualTo(racers - 1), "every loser must be told the name is taken");
            Assert.That(distinct, Has.Count.EqualTo(1));
        });

        // The winner's account is the one the index resolves, and it is usable.
        Assert.That(AccountStore.TryLogin(Dbe, "Lando", GoodPassword, out var resolved), Is.EqualTo(AccountStore.LoginOutcome.Ok));
        Assert.That(distinct, Does.Contain(resolved));
    }

    /// <summary>A wrong password is refused, and distinguishably from an absent account only to the server.</summary>
    [Test]
    [CancelAfter(15_000)]
    [Category("Integration")]
    public void WrongPassword_AndUnknownName_AreBothRefused()
    {
        Assert.That(AccountStore.TrySignup(Dbe, "Jabba", GoodPassword, out _), Is.EqualTo(AccountStore.SignupOutcome.Created));

        Assert.Multiple(() =>
        {
            Assert.That(AccountStore.TryLogin(Dbe, "Jabba", "not-the-password", out var a), Is.EqualTo(AccountStore.LoginOutcome.WrongPassword));
            Assert.That(a, Is.EqualTo(default(EntityId)));
            Assert.That(AccountStore.TryLogin(Dbe, "Greedo", GoodPassword, out var b), Is.EqualTo(AccountStore.LoginOutcome.NoSuchAccount));
            Assert.That(b, Is.EqualTo(default(EntityId)));
        });
    }

    /// <summary>A name or password the rules refuse never reaches the KDF or the engine.</summary>
    [Test]
    [CancelAfter(15_000)]
    [Category("Integration")]
    public void BadNamesAndShortPasswords_AreRefusedBeforeAnythingIsWritten()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccountStore.TrySignup(Dbe, "ab", GoodPassword, out _), Is.EqualTo(AccountStore.SignupOutcome.BadName), "too short");
            Assert.That(AccountStore.TrySignup(Dbe, "9lives", GoodPassword, out _), Is.EqualTo(AccountStore.SignupOutcome.BadName), "must start with a letter");
            Assert.That(AccountStore.TrySignup(Dbe, "han solo", GoodPassword, out _), Is.EqualTo(AccountStore.SignupOutcome.BadName), "no spaces");
            Assert.That(AccountStore.TrySignup(Dbe, "han;drop", GoodPassword, out _), Is.EqualTo(AccountStore.SignupOutcome.BadName), "no punctuation");
            Assert.That(AccountStore.TrySignup(Dbe, new string('a', 25), GoodPassword, out _), Is.EqualTo(AccountStore.SignupOutcome.BadName), "too long");
            Assert.That(AccountStore.TrySignup(Dbe, "Wicket", "short", out _), Is.EqualTo(AccountStore.SignupOutcome.BadPassword));
            Assert.That(AccountStore.TryLogin(Dbe, "ab", GoodPassword, out _), Is.EqualTo(AccountStore.LoginOutcome.BadName));
        });
    }

    /// <summary>A login stamps the account, which is the one write a login makes.</summary>
    [Test]
    [CancelAfter(15_000)]
    [Category("Integration")]
    public void Login_StampsLastSeen()
    {
        Assert.That(AccountStore.TrySignup(Dbe, "Ackbar", GoodPassword, out _), Is.EqualTo(AccountStore.SignupOutcome.Created));
        Assert.That(AccountStore.TryFind(Dbe, "ackbar", out _, out var fresh), Is.True);
        Assert.That(fresh.LastSeenUtcTicks, Is.Zero, "an account that has never logged in carries no stamp");
        Assert.That(fresh.CreatedUtcTicks, Is.GreaterThan(0));

        Assert.That(AccountStore.TryLogin(Dbe, "Ackbar", GoodPassword, out _), Is.EqualTo(AccountStore.LoginOutcome.Ok));

        Assert.That(AccountStore.TryFind(Dbe, "ackbar", out _, out var seen), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(seen.LastSeenUtcTicks, Is.GreaterThanOrEqualTo(fresh.CreatedUtcTicks), "the stamp must be set, and not before the account existed");
            Assert.That(seen.CreatedUtcTicks, Is.EqualTo(fresh.CreatedUtcTicks), "the stamping write must not have disturbed the rest of the component");
        });

        // The stamp rewrote the whole component, name included. If that had moved the index key, this lookup would miss.
        Assert.That(AccountStore.TryLogin(Dbe, "Ackbar", GoodPassword, out _), Is.EqualTo(AccountStore.LoginOutcome.Ok),
            "the stamping write must not have disturbed the name index");
    }

    /// <summary>
    /// EW-02 in anger: distinct accounts created and logged into from several threads while the tick loop runs.
    /// </summary>
    /// <remarks>
    /// <b>What would fail here if the detector's old, engine-wide arming were still in place:</b> every one of these commits
    /// writes the <c>EntityMap</c> and the name's B+Tree while the fence is open somewhere in the same engine, and that is
    /// precisely the shape <c>ExclusiveWindow.NoteMutation</c> used to reject. It is legal — both structures are written under
    /// their own latches — and this fixture is where the demo depends on it.
    /// </remarks>
    [Test]
    [CancelAfter(60_000)]
    [Category("Integration")]
    public void WhileTheWorldTicks_ConcurrentSignupsAndLogins_AllSucceed()
    {
        const int threads = 4;
        const int perThread = 5;

        // The fence, driven directly and as hard as it will go. That is deliberately harsher than TyphonRuntime's paced loop: the exclusive window this test
        // needs to overlap with is open only while WriteTickFence runs, so against a 10 Hz runtime it would be shut for almost the whole test and any overlap
        // would be luck rather than coverage.
        using var stopTicking = new CancellationTokenSource();
        Exception fenceFault = null;
        var ticking = Task.Run(() =>
        {
            try
            {
                var tick = 1L;
                while (!stopTicking.IsCancellationRequested)
                {
                    Dbe.WriteTickFence(tick++);
                }
            }
            catch (Exception ex)
            {
                // A fence that throws is this test's failure seen from the other side: it means an off-tick commit disturbed it.
                fenceFault = ex;
            }
        });

        var failures = new List<string>();
        var runners = new Task[threads];
        for (var t = 0; t < threads; t++)
        {
            var thread = t;
            runners[thread] = Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    var name = $"racer_{thread}_{i}";
                    var signup = AccountStore.TrySignup(Dbe, name, GoodPassword, out var id);
                    if (signup != AccountStore.SignupOutcome.Created)
                    {
                        lock (failures)
                        {
                            failures.Add($"{name}: signup {signup}");
                        }

                        continue;
                    }

                    var login = AccountStore.TryLogin(Dbe, name, GoodPassword, out var resolved);
                    if (login != AccountStore.LoginOutcome.Ok || resolved != id)
                    {
                        lock (failures)
                        {
                            failures.Add($"{name}: login {login}, resolved {resolved} vs {id}");
                        }
                    }
                }
            });
        }

        Task.WaitAll(runners);
        stopTicking.Cancel();
        ticking.Wait();

        Assert.Multiple(() =>
        {
            Assert.That(fenceFault, Is.Null, "the tick fence must not be disturbed by a concurrent off-tick account commit");
            Assert.That(failures, Is.Empty, "an off-tick account write must not be disturbed by the tick fence");
        });

        for (var t = 0; t < threads; t++)
        {
            for (var i = 0; i < perThread; i++)
            {
                Assert.That(AccountStore.TryFind(Dbe, $"racer_{t}_{i}", out _, out _), Is.True, $"racer_{t}_{i} must be in the index");
            }
        }
    }
}
