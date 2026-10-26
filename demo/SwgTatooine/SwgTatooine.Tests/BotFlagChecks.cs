using NUnit.Framework;
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
}
