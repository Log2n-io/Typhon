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
            Assert.That(gear, Is.EqualTo(new[] { new GearItem(5, ItemCatalog.Blaster, EquipSlot.Weapon, EquipSlot.Weapon) }));
        });
    }
}
