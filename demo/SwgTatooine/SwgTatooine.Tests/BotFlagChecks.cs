using System;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A bot says it is one at sign-up, and its body carries it to every viewer (<c>PlayerLook.Bot</c>, replicated <c>bot</c>); a person's carries 0. Data
/// only: the HUD's player count split into the crowd, the bots and the people (Loïc, 2026-10-26).
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class BotFlagChecks : ServedChecks
{
    [Test]
    [CancelAfter(60_000)]
    public void ABotsBody_SaysItIsABot_AndAPersonsDoesNot()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);

        Assert.That(AccountStore.TrySignup(sim.Dbe, "bot000042", "a-bot-password-1", out var bot, bot: true), Is.EqualTo(AccountStore.SignupOutcome.Created));
        Assert.That(AccountStore.TrySignup(sim.Dbe, "Leia", "a-person-password-1", out var person), Is.EqualTo(AccountStore.SignupOutcome.Created));

        var botLink = Harness.Connect(TatooineReplication.PlayerKind, bot);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the bot's avatar");
        var personLink = Harness.Connect(TatooineReplication.PlayerKind, person);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 2, "the person's avatar");
        Harness.Ticks(2);

        var botFlag = -1;
        var personFlag = -1;
        sim.WriteInTick(tx =>
        {
            botFlag = tx.Open(TatooineReplication.ControlledBy(sim.Dbe, botLink.Session)).Read(Player.Look).Bot;
            personFlag = tx.Open(TatooineReplication.ControlledBy(sim.Dbe, personLink.Session)).Read(Player.Look).Bot;
        });
        Assert.Multiple(() =>
        {
            Assert.That(botFlag, Is.EqualTo(1), "a bot's body does not say it is one");
            Assert.That(personFlag, Is.Zero, "a person's body says it is a bot");
        });
    }

    /// <summary>
    /// A bot reads its inventory the way a browser does (inventory-loot.md § 7, IL-12): once per <c>items</c> revision, one read at a time. A failed read
    /// backs off from when it ENDED, doubling per failure in a row with a jitter of up to half the delay, and a moved revision does not cut the wait short:
    /// a swarm that a slow server failed together must not come back together (#1532).
    /// </summary>
    [Test]
    public void ABot_ReadsItsInventoryOnItsRevision_AndBacksOffAfterAFailure()
    {
        var poll = new OwnerFieldPoll("items", retryTicks: 12, seed: 7);
        Assert.Multiple(() =>
        {
            Assert.That(poll.TryBegin(double.NaN, 1), Is.False, "read before the body reported the field");
            Assert.That(poll.TryBegin(1, 1), Is.True, "not read on its first revision");
            Assert.That(poll.TryBegin(2, 2), Is.False, "two reads in flight at once");
        });
        poll.End(ok: true, 1);
        Assert.Multiple(() =>
        {
            Assert.That(poll.TryBegin(1, 1_000), Is.False, "read again on a revision that did not move");
            Assert.That(poll.TryBegin(2, 3), Is.True, "not read on a moved revision");
        });

        // First failure, ended on tick 3: due in [3 + 12, 3 + 12 + 6].
        poll.End(ok: false, 3);
        Assert.Multiple(() =>
        {
            Assert.That(poll.TryBegin(2, 14), Is.False, "a failed read asked again before the retry delay");
            Assert.That(poll.TryBegin(3, 14), Is.False, "a moved revision cut the back-off short");
        });
        Assert.That(poll.TryBegin(3, 22), Is.True, "a failed read not asked again once its delay and jitter passed");

        // Second failure in a row, ended on tick 22: the delay doubled, due in [22 + 24, 22 + 24 + 12].
        poll.End(ok: false, 22);
        Assert.Multiple(() =>
        {
            Assert.That(poll.Failures, Is.EqualTo(2), "failures in a row");
            Assert.That(poll.TryBegin(3, 45), Is.False, "the second failure did not double the delay");
        });
        Assert.That(poll.TryBegin(3, 59), Is.True, "not asked again after the doubled delay");

        // A success ends the back-off: the next read waits for a moved revision only.
        poll.End(ok: true, 59);
        Assert.Multiple(() =>
        {
            Assert.That(poll.Failures, Is.Zero, "a success did not reset the failures");
            Assert.That(poll.TryBegin(3, 60), Is.False, "read again on a revision that did not move, after a success");
            Assert.That(poll.TryBegin(4, 60), Is.True, "not read on a moved revision after a success");
        });
    }

    /// <summary>
    /// The back-off is capped at <see cref="OwnerFieldPoll.MaxBackoffShift"/> doublings, and two bots that fail on the same tick come back on different ones.
    /// </summary>
    [Test]
    public void ABotsBackOff_IsCapped_AndSpreadAcrossTheSwarm()
    {
        const long retry = 12;
        var cap = (retry << OwnerFieldPoll.MaxBackoffShift) * 3 / 2;
        var poll = new OwnerFieldPoll("items", retry, seed: 3);
        var tick = 0L;
        for (var failure = 0; failure < 10; failure++)
        {
            Assert.That(poll.TryBegin(1, tick + cap + 1), Is.True, $"failure {failure}: not asked again within the capped delay");
            tick += cap + 1;
            poll.End(ok: false, tick);
        }

        // 200 bots failing on one tick: the ticks they come back on.
        var dueAt = new System.Collections.Generic.HashSet<long>();
        for (var bot = 0; bot < 200; bot++)
        {
            var p = new OwnerFieldPoll("items", retry, seed: bot);
            p.TryBegin(1, 0);
            p.End(ok: false, 0);
            var t = retry;
            while (!p.TryBegin(1, t))
            {
                t++;
            }

            dueAt.Add(t);
        }

        Assert.That(dueAt, Has.Count.GreaterThanOrEqualTo(5), "a swarm that failed together comes back together");
    }

    /// <summary>
    /// The fleet's reconciler waits longer after each round that opened nothing (#1532): its base, doubled per round up to the cap, with up to half again
    /// of jitter, and back to the base once a round succeeds.
    /// </summary>
    [Test]
    public void TheRampsBackOff_Doubles_IsCapped_AndResets()
    {
        var backoff = new RampBackoff(TimeSpan.FromSeconds(2), maxShift: 5, seed: 11);
        var delays = Enumerable.Range(0, 8).Select(_ => backoff.Next().TotalSeconds).ToArray();
        backoff.Reset();
        var again = backoff.Next().TotalSeconds;
        Assert.Multiple(() =>
        {
            for (var i = 0; i < delays.Length; i++)
            {
                var floor = 2.0 * (1 << Math.Min(i, 5));
                Assert.That(delays[i], Is.InRange(floor, floor * 1.5), $"round {i}");
            }

            Assert.That(again, Is.InRange(2.0, 3.0), "a success did not reset the back-off");
        });
    }

    /// <summary>
    /// What a bot counts from its inventory reads (IL-12): the first read only learns what was there; after it, each lost unit counts once however often
    /// it is read, and the stacks are the last answer's. The answer has no ledger rows (Loïc, 2026-10-29): drops reach the bot as notices.
    /// </summary>
    [Test]
    public void ABot_CountsEachLossOnce_AndNothingOnItsFirstRead()
    {
        const string first = """
            {"open":true,"slots":30,"items":[{"id":"9","slot":1,"item":"mat_hide","name":"Hide","category":"material","mk":1,"count":4}],
            "lost":[{"n":1,"tick":8,"item":"mat_bone","name":"Bone","mk":1,"count":2}]}
            """;
        const string second = """
            {"open":true,"slots":30,"items":[{"id":"9","slot":1,"item":"mat_hide","name":"Hide","category":"material","mk":1,"count":6},
            {"id":"10","slot":2,"item":"core_armor","name":"Armor Core","category":"core","mk":2,"count":1}],
            "lost":[{"n":2,"tick":12,"item":"mat_bone","name":"Bone","mk":2,"count":3},{"n":1,"tick":8,"item":"mat_bone","name":"Bone","mk":1,"count":2}]}
            """;

        var view = InventoryView.Parse(first, InventoryView.None, count: false, out var lost);
        Assert.Multiple(() =>
        {
            Assert.That(lost, Is.Zero, "the first read counted losses that were already there");
            Assert.That(view, Is.EqualTo(new InventoryView(1, 1)));
        });

        view = InventoryView.Parse(second, view, count: true, out lost);
        Assert.Multiple(() =>
        {
            Assert.That(lost, Is.EqualTo(3), "the new note's units");
            Assert.That(view, Is.EqualTo(new InventoryView(2, 2)));
        });

        view = InventoryView.Parse(second, view, count: true, out lost);
        Assert.That(lost, Is.Zero, "an answer read twice counted twice");
    }
}
