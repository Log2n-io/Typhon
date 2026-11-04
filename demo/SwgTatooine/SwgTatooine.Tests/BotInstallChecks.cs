using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// What an installation owner does (<see cref="InstallPlan"/>, crafting-build.md § 10): buys a kit when it has none and nothing placed, visits an
/// installation when its hopper fills or its upkeep runs low, collects it and pays a few hours ahead while its wallet keeps a reserve. Its numbers are the
/// server's.
/// </summary>
[TestFixture]
internal sealed class BotInstallChecks
{
    [Test]
    public void TheBotsNumbers_AreTheServers()
    {
        Assert.Multiple(() =>
        {
            Assert.That(InstallPlan.Kit, Is.EqualTo(ItemCatalog.Extractor));
            Assert.That(InstallPlan.KitPrice, Is.EqualTo(Installations.StandardKitPrice));
            Assert.That(InstallPlan.UpkeepPerHour, Is.EqualTo(Installations.ExtractorUpkeepPerHour));
            Assert.That(InstallPlan.ReachM, Is.LessThan(Installations.ReachM));
            for (var i = 0; i < 7; i++)
            {
                Assert.That(Installations.Extracts(InstallPlan.FamilyFor(i)), Is.True, $"family {i}");
            }
        });
    }

    [Test]
    public void AnOwner_VisitsWhenItsHopperFillsOrItsUpkeepRunsLow()
    {
        var fresh = new BotInstallation(1, 0, 0, Held: 10, Hopper: 500, PaidSeconds: 4 * 3600);
        var filling = fresh with { Held = 250 };
        var lapsing = fresh with { PaidSeconds = 3600 };
        Assert.Multiple(() =>
        {
            Assert.That(InstallPlan.Due(in fresh), Is.False);
            Assert.That(InstallPlan.Due(in filling), Is.True, "a hopper 40 % full");
            Assert.That(InstallPlan.Due(in lapsing), Is.True, "under two hours of upkeep");
            Assert.That(InstallPlan.NextDue(new List<BotInstallation> { fresh, lapsing })?.Id, Is.EqualTo(1UL));
        });
    }

    [Test]
    public void AnOwner_CollectsAndPaysAhead_KeepingItsReserve()
    {
        var lapsing = new BotInstallation(1, 0, 0, Held: 30, Hopper: 500, PaidSeconds: 1800);
        Assert.Multiple(() =>
        {
            Assert.That(InstallPlan.Tend(in lapsing, 10_000), Is.EqualTo((true, 400u)), "four hours ahead, in whole hours");
            Assert.That(InstallPlan.Tend(in lapsing, InstallPlan.Reserve + 250), Is.EqualTo((true, 200u)), "what the reserve leaves, in whole hours");
            Assert.That(InstallPlan.Tend(in lapsing, InstallPlan.Reserve + 50), Is.EqualTo((true, 0u)), "not an hour's worth past the reserve");
            Assert.That(InstallPlan.Tend(lapsing with { PaidSeconds = 3 * 3600, Held = 0 }, 10_000), Is.EqualTo((false, 0u)), "nothing due");
        });
    }

    [Test]
    public void AnOwner_BuysAKit_OnlyWithNothingToPlace()
    {
        var enough = InstallPlan.KitPrice + InstallPlan.Reserve;
        Assert.Multiple(() =>
        {
            Assert.That(InstallPlan.BuysKit(true, 0, false, enough), Is.True);
            Assert.That(InstallPlan.BuysKit(false, 0, false, enough), Is.False, "not an owner");
            Assert.That(InstallPlan.BuysKit(true, 1, false, enough), Is.False, "one placed");
            Assert.That(InstallPlan.BuysKit(true, 0, true, enough), Is.False, "a kit held");
            Assert.That(InstallPlan.BuysKit(true, 0, false, enough - 1), Is.False, "its reserve");
        });
    }

    [Test]
    public void AnInstallationErrand_IsShownAsTending()
        => Assert.That(BotBrain.ActivityOf(Goal.Tending, 0), Is.EqualTo(Activity.Tending));
}
