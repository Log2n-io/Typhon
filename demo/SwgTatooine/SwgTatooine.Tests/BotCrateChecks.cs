using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// What a bot does with crates (<see cref="CratePlan"/>, crafting-catalog.md § 11.2): packs its identical spare gear, one pack a group a visit, never what
/// it is about to wear nor parts; never wears a crate nor crafts with one; lists and sells a crate whole.
/// </summary>
[TestFixture]
internal sealed class BotCrateChecks
{
    private static GearItem Blaster(ulong id, int slot, int count = 1, string key = "k", int mk = 1)
        => new(id, ItemCatalog.Blaster, slot, EquipSlot.Weapon, 0, "blaster_dl44", mk) { Count = count, PackKey = key };

    [Test]
    public void TheBotsCapacity_IsTheServers() => Assert.That(CratePlan.Capacity, Is.EqualTo(Crates.Capacity));

    [Test]
    public void ABotPacks_ItsIdenticalSpareGear_SmallestIntoFullest_NotWhatItWillWear()
    {
        var gear = new List<GearItem>
        {
            Blaster(1, 1), Blaster(2, 2, count: 4), Blaster(3, 3), Blaster(4, 4, key: "other"), Blaster(5, EquipSlot.Weapon),
            Blaster(6, 6, count: 10), Blaster(7, 7, key: "x"), Blaster(8, 8, key: "x"),
        };

        Assert.Multiple(() =>
        {
            Assert.That(CratePlan.Packs(gear), Is.EqualTo(new List<(ulong, ulong)> { (3, 2), (8, 7) }), "the full crate is left; worn is left");
            Assert.That(CratePlan.Packs(gear, wear: 8), Is.EqualTo(new List<(ulong, ulong)> { (3, 2) }), "what it will wear is not packed");
        });
    }

    /// <summary>
    /// A crate is not gear to a bot (review, 2026-11-03): it does not buy one to wear, nor does a crate — or a weapon of another kind — cover its slot when
    /// it looks for a piece or buys standard issue.
    /// </summary>
    [Test]
    public void ACrate_IsNotBoughtToWear_NorCoversASlot()
    {
        var prices = MarketPrices.None.With("blaster_dl44", 2, 1_000d);
        var rows = new List<BrowseRow> { new(1, 0, "blaster_dl44", MarketCategory.Other, 2, 3, 900, 2_700, 9) };
        var crate = new List<GearItem> { Blaster(5, 1, count: 3, mk: 2) };
        var sword = new List<GearItem> { new(6, ItemCatalog.Sword, 1, EquipSlot.Weapon, 0, "sword_vibroblade", 2) };
        var offers = new List<StandardOffer> { new(ItemCatalog.Blaster, "blaster_dl44", 1_812, EquipSlot.Weapon) };
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.ChooseGear(rows, prices, 1, 2, 100_000, []), Is.Null, "a crate bought as a piece");
            Assert.That(MarketPlan.GearWanted(crate, null, 2, null, ItemCatalog.Blaster)?.HaveMk ?? 0, Is.Zero, "a crate covered the weapon");
            Assert.That(MarketPlan.GearWanted(sword, null, 2, null, ItemCatalog.Blaster)?.HaveMk ?? 0, Is.Zero, "a sword covered a pistol fighter");
            Assert.That(MarketPlan.ChooseStandard(offers, crate, 100_000, null, ItemCatalog.Blaster), Is.Not.Null, "a crate counted as the weapon");
        });
    }

    [Test]
    public void ABotNeverWearsACrate_AndListsItWhole()
    {
        var prices = MarketPrices.None.With("blaster_dl44", 2, 1_000d);
        var gear = new List<GearItem> { Blaster(1, 1, count: 3, mk: 2) };
        var rng = 3u;
        var plan = MarketPlan.Surplus(gear, prices, 0, 2, ref rng);
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.NextToWear(gear, 2), Is.Zero, "a crate is not worn");
            Assert.That(plan.ConvertAll(c => (c.Step, c.Item, c.Quantity)), Is.EqualTo(new[] { (MarketStep.List, 1UL, 3) }), "listed whole");
        });
    }
}
