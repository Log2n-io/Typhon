using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The bots' gear (crafting-build.md § 6, C1's half): what they put on, what standard issue they buy, and what their inventory read keeps out of the
/// bazaar — pure functions over the server's own answers.
/// </summary>
[TestFixture]
internal sealed class BotGearChecks
{
    private static readonly List<StandardOffer> Offers = MarketJson.Standard(MarketEndpoints.StandardJson());

    [Test]
    public void TheStandardList_IsTheVendors_WithWhereEachIsWorn()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Offers, Has.Count.EqualTo(Equipment.Standard.Length));
            Assert.That(Offers.Find(o => o.Template == ItemCatalog.Blaster).Wears, Is.EqualTo(EquipSlot.Weapon));
            Assert.That(Offers.Find(o => o.Template == ItemCatalog.PaddedHelmet + 4).Price, Is.EqualTo(Equipment.StandardPrice(ItemCatalog.PaddedHelmet + 4)));
        });
    }

    [Test]
    public void ABot_BuysTheCheapestPaddedPieceItLacks_KeepingItsReserve()
    {
        var pack = new List<GearItem> { new(1, ItemCatalog.Blaster, EquipSlot.Weapon, EquipSlot.Weapon) };
        var gloves = Equipment.StandardPrice(ItemCatalog.PaddedHelmet + 4);
        Assert.Multiple(() =>
        {
            var first = MarketPlan.ChooseStandard(Offers, pack, 10_000);
            Assert.That(first?.Step, Is.EqualTo(MarketStep.Standard));
            Assert.That(first?.UnitPrice, Is.EqualTo(gloves), "not the cheapest: the gloves and the boots cost the same, the gloves come first");
            Assert.That(MarketPlan.ChooseStandard(Offers, pack, gloves + MarketPlan.StandardReserve - 1), Is.Null, "it spent into its reserve");

            var covered = new List<GearItem>(pack);
            for (var slot = EquipSlot.Head; slot <= EquipSlot.Feet; slot++)
            {
                covered.Add(new GearItem((ulong)(10 + slot), ItemCatalog.PaddedHelmet, 1, slot));
            }

            Assert.That(MarketPlan.ChooseStandard(Offers, covered, 1_000_000), Is.Null, "every slot is covered, worn or held");
        });
    }

    [Test]
    public void ABot_WearsAnItemWhoseSlotIsFree_AndNotOneWhoseSlotIsWorn()
    {
        var gear = new List<GearItem>
        {
            new(1, ItemCatalog.Blaster, EquipSlot.Weapon, EquipSlot.Weapon),
            new(2, ItemCatalog.Sword, 3, EquipSlot.Weapon),
            new(3, ItemCatalog.PaddedHelmet + 4, 4, EquipSlot.Hands),
        };
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.NextToWear(gear), Is.EqualTo(3UL), "the gloves: the weapon slot is worn");
            Assert.That(MarketPlan.NextToWear(gear.GetRange(0, 2)), Is.Zero);
        });
    }

    /// <summary>§ 6, Gear: a better piece within the band is put on over the worn one — the server swaps them — and never one past the band.</summary>
    [Test]
    public void ABot_WearsAnUpgradeWithinItsBand_AndNothingPastIt()
    {
        var gear = new List<GearItem>
        {
            new(1, ItemCatalog.Blaster, EquipSlot.Weapon, EquipSlot.Weapon, 0, "blaster_dl44", 1),
            new(2, ItemCatalog.Blaster, 3, EquipSlot.Weapon, 0, "blaster_dl44", 3),
            new(3, ItemCatalog.Blaster, 4, EquipSlot.Weapon, 0, "blaster_dl44", 2),
        };
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.NextToWear(gear), Is.Zero, "no band: no upgrade, as before");
            Assert.That(MarketPlan.NextToWear(gear, 1), Is.Zero, "band I: both are past it");
            Assert.That(MarketPlan.NextToWear(gear, 2), Is.EqualTo(3UL), "band II: the Mk II, not the Mk III");
            Assert.That(MarketPlan.NextToWear(gear, 3), Is.EqualTo(2UL));
        });
    }

    /// <summary>
    /// § 6, Gear: what a bot looks for on the board — its weapon below its band first, then an armour slot it has nothing for (the padded piece), then
    /// one whose best is below its band; nothing when all of it is of its band.
    /// </summary>
    [Test]
    public void ABot_LooksForItsWeaponBelowItsBand_ThenAnEmptySlot_ThenOneBelowIt()
    {
        var worn = new List<GearItem> { new(1, ItemCatalog.Blaster, EquipSlot.Weapon, EquipSlot.Weapon, 0, "blaster_dl44", 1) };
        for (var slot = EquipSlot.Head; slot <= EquipSlot.Feet; slot++)
        {
            worn.Add(new GearItem((ulong)slot, ItemCatalog.PaddedHelmet, slot, slot, 0, "armor_padded_x" + slot, slot == EquipSlot.Hands ? 2 : 1));
        }

        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.GearWanted(worn, Offers, 1), Is.Null, "all of band I");
            Assert.That(MarketPlan.GearWanted(worn, Offers, 2), Is.EqualTo(("blaster_dl44", EquipSlot.Weapon, 1)), "the weapon first");
            var armed = worn.ConvertAll(g => g.Wears == EquipSlot.Weapon ? g with { Mk = 2 } : g);
            Assert.That(MarketPlan.GearWanted(armed, Offers, 2), Is.EqualTo(("armor_padded_x" + EquipSlot.Head, EquipSlot.Head, 1)), "then the first below");
            var bare = armed.FindAll(g => g.Wears != EquipSlot.Feet);
            Assert.That(MarketPlan.GearWanted(bare, Offers, 2)?.Item, Is.EqualTo("armor_padded_boots"), "an empty slot before one below the band");
        });
    }

    /// <summary>§ 6, Gear: of the listings of the piece, the highest Mk above what it has and within its band, the best value, keeping its reserve.</summary>
    [Test]
    public void ABot_BuysTheHighestMkWithinItsBand_AtTheBestValue_KeepingItsReserve()
    {
        var prices = MarketPrices.None.With("blaster_dl44", 2, 1_000d).With("blaster_dl44", 3, 2_000d).With("blaster_dl44", 1, 500d);
        BrowseRow Row(ulong id, int mk, long unit) => new(id, 1, "blaster_dl44", MarketCategory.Other, mk, 1, unit, unit, 99);
        List<BrowseRow> rows = [Row(1, 1, 400), Row(2, 2, 1_150), Row(3, 2, 900), Row(4, 3, 1_500), Row(5, 2, 1_300)];
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.ChooseGear(rows, prices, 1, 2, 10_000, [])?.Id, Is.EqualTo(3UL), "the Mk III is past band II, 5 past 1.2 × value");
            Assert.That(MarketPlan.ChooseGear(rows, prices, 1, 3, 10_000, [])?.Id, Is.EqualTo(4UL), "band III: the highest Mk");
            Assert.That(MarketPlan.ChooseGear(rows, prices, 2, 2, 10_000, [])?.Id, Is.Null, "nothing above the Mk it has");
            Assert.That(MarketPlan.ChooseGear(rows, prices, 1, 2, 900 + MarketPlan.StandardReserve - 1, [])?.Id, Is.Null, "into its reserve");
            Assert.That(MarketPlan.ChooseGear(rows, prices, 1, 2, 10_000, [3UL])?.Id, Is.EqualTo(2UL), "never its own");
        });
    }

    [Test]
    public void TheInventoryRead_KeepsGearOutOfTheBazaarsGoods_AndTheWornOutOfTheGrid()
    {
        var json = """
            {"open":true,"slots":40,"items":[
              {"id":"5","slot":101,"item":"blaster_dl44","name":"DL-44 Blaster","category":"weapon","template":69,"mk":1,"count":1,
               "stats":{"damage":81,"delayMs":1021,"rangeDm":300,"wears":101,"condition":1,"maxCondition":1,"lifeMilli":1000,"repairs":0,"crafter":"0"}},
              {"id":"6","slot":1,"item":"mat_hide","name":"Hide","category":"material","template":8,"mk":2,"count":7}],
             "lost":[]}
            """;
        var held = new List<HeldStack>();
        var gear = new List<GearItem>();
        var view = InventoryView.Parse(json, InventoryView.None, false, out _, held, gear);
        Assert.Multiple(() =>
        {
            Assert.That(view.Stacks, Is.EqualTo(1), "the worn blaster counted in the grid");
            Assert.That(held.ConvertAll(h => h.Item), Is.EqualTo(new[] { "mat_hide" }));
            Assert.That(gear, Is.EqualTo(new[] { new GearItem(5, ItemCatalog.Blaster, EquipSlot.Weapon, EquipSlot.Weapon, 0, "blaster_dl44", 1) }));
        });
    }
}
