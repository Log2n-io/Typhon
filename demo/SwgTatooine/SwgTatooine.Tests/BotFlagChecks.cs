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
    /// A bot reads its inventory the way a browser does (inventory-loot.md § 7, IL-12): once per <c>items</c> revision, one read at a time, a failed read
    /// again after the retry delay or at once on the next revision.
    /// </summary>
    /// <remarks>
    /// Against stubbed answers: the server's half (the <c>items</c> owner field, <c>GET /swg/inventory</c>) lands in another branch, and the served case —
    /// a bot's <c>LootRowsSeen</c> following its rows in a real world — is to be written on the merge. What can be pinned here is the rule and the counting.
    /// </remarks>
    [Test]
    public void ABot_ReadsItsInventoryOnItsRevision()
    {
        var poll = new OwnerFieldPoll("items", retryTicks: 12);
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
        poll.End(ok: false, 3);
        Assert.Multiple(() =>
        {
            Assert.That(poll.TryBegin(2, 14), Is.False, "a failed read asked again before the retry delay");
            Assert.That(poll.TryBegin(2, 15), Is.True, "a failed read not asked again after the retry delay");
        });
        poll.End(ok: false, 15);
        Assert.That(poll.TryBegin(3, 16), Is.True, "a failed read not asked again at once on the next revision");
    }

    /// <summary>
    /// What a bot counts from its inventory reads (IL-12): the first read only learns what was there; after it, each loot row and each lost unit counts
    /// once however often it is read, a row that is not a drop does not count as loot, and the stacks are the last answer's.
    /// </summary>
    [Test]
    public void ABot_CountsEachLootRowAndLossOnce_AndNothingOnItsFirstRead()
    {
        const string first = """
            {"open":true,"slots":30,"items":[{"id":"9","slot":1,"item":"mat_hide","name":"Hide","category":"material","mk":1,"count":4}],
            "recent":[{"seq":"5","tick":10,"kind":"kill","item":"mat_hide","name":"Hide","mk":1,"count":4,"ref":"77"}],
            "lost":[{"n":1,"tick":8,"item":"mat_bone","name":"Bone","mk":1,"count":2}]}
            """;
        const string second = """
            {"open":true,"slots":30,"items":[{"id":"9","slot":1,"item":"mat_hide","name":"Hide","category":"material","mk":1,"count":6},
            {"id":"10","slot":2,"item":"core_armor","name":"Armor Core","category":"core","mk":2,"count":1}],
            "recent":[{"seq":"8","tick":20,"kind":"give","item":"mat_hide","name":"Hide","mk":1,"count":1,"ref":"3"},
                      {"seq":"7","tick":12,"kind":"kill","item":"mat_hide","name":"Hide","mk":1,"count":2,"ref":"78"},
                      {"seq":"6","tick":12,"kind":"kill","item":"core_armor","name":"Armor Core","mk":2,"count":1,"ref":"78"},
                      {"seq":"5","tick":10,"kind":"kill","item":"mat_hide","name":"Hide","mk":1,"count":4,"ref":"77"}],
            "lost":[{"n":2,"tick":12,"item":"mat_bone","name":"Bone","mk":2,"count":3},{"n":1,"tick":8,"item":"mat_bone","name":"Bone","mk":1,"count":2}]}
            """;

        var view = InventoryView.Parse(first, InventoryView.None, count: false, out var rows, out var lost);
        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.Zero, "the first read counted rows that were already there");
            Assert.That(lost, Is.Zero, "the first read counted losses that were already there");
            Assert.That(view, Is.EqualTo(new InventoryView(1, 5, 1)));
        });

        view = InventoryView.Parse(second, view, count: true, out rows, out lost);
        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.EqualTo(2), "the drop's two rows, and not the row that is not a drop");
            Assert.That(lost, Is.EqualTo(3), "the new note's units");
            Assert.That(view, Is.EqualTo(new InventoryView(2, 8, 2)));
        });

        view = InventoryView.Parse(second, view, count: true, out rows, out lost);
        Assert.That((rows, lost), Is.EqualTo((0, 0)), "an answer read twice counted twice");
    }
}
