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
            Assert.That(Offers, Has.Count.EqualTo(Equipment.Standard.Length + Equipment.StandardMaterials.Length), "the items and the Mk I materials");
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

    /// <summary>A bot that fights with a pistol never puts a sword on over it, at any band, and lists the sword instead (review, 2026-11-02).</summary>
    [Test]
    public void ABotThatFightsWithAPistol_NeverWearsASword_AndListsIt()
    {
        var prices = MarketPrices.None.With("sword_vibroblade", 2, 1_000d);
        var gear = new List<GearItem>
        {
            new(1, ItemCatalog.Blaster, EquipSlot.Weapon, EquipSlot.Weapon, 0, "blaster_dl44", 1),
            new(2, ItemCatalog.Sword, 3, EquipSlot.Weapon, 0, "sword_vibroblade", 2),
        };
        var rng = 3u;
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.NextToWear(gear, 2), Is.EqualTo(2UL), "the premise: with no weapon named, the sword is an upgrade");
            Assert.That(MarketPlan.NextToWear(gear, 2, ItemCatalog.Blaster), Is.Zero);
            Assert.That(MarketPlan.Surplus(gear, prices, 0, 2, ref rng, ItemCatalog.Blaster).ConvertAll(c => c.Item), Is.EqualTo(new ulong[] { 2 }));
        });
    }

    /// <summary>§ 6, Surplus: past the listing cap, an item it will not use goes to the vendor — only while the grid is over its floor.</summary>
    [Test]
    public void SurplusPastTheListingCap_GoesToTheVendor_OnlyOverTheFloor()
    {
        var prices = MarketPrices.None.With("sword_vibroblade", 2, 1_000d);
        var gear = new List<GearItem> { new(2, ItemCatalog.Sword, 3, EquipSlot.Weapon, 0, "sword_vibroblade", 2) };
        var rng = 3u;
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.Surplus(gear, prices, MarketPlan.MaxListed, 1, ref rng), Is.Empty, "under the floor it waits for a listing slot");
            var vendored = MarketPlan.Surplus(gear, prices, MarketPlan.MaxListed, 1, ref rng, vendorRest: true);
            Assert.That(vendored.ConvertAll(c => (c.Step, c.Item, c.Quantity)), Is.EqualTo(new[] { (MarketStep.Vendor, 2UL, 1) }));
        });
    }

    /// <summary>
    /// The vendor's floor counts the whole grid when it is known: kept inputs, parts and items too. Ten tradeable stacks in a grid of 30 are sold down
    /// until 8 stacks are left in the grid, not in the tradeable list.
    /// </summary>
    [Test]
    public void TheVendorFloor_CountsTheWholeGrid()
    {
        var prices = MarketPrices.None.With("mat_metal", 1, 10d);
        var held = new List<HeldStack>();
        for (var i = 0; i < 10; i++)
        {
            held.Add(new HeldStack(ItemCatalog.Metal, "mat_metal", MarketCategory.Material, 1, 50));
        }

        var rng = 1u;
        var mine = new MarketMine([], 0);
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.Plan(held, mine, prices, ref rng, false, null), Has.Count.EqualTo(10 - MarketPlan.KeepStacks), "the premise");
            Assert.That(MarketPlan.Plan(held, mine, prices, ref rng, false, null, gridStacks: 30), Has.Count.EqualTo(10), "kept stacks and items held it");
            Assert.That(MarketPlan.Plan(held, mine, prices, ref rng, false, null, gridStacks: 12), Has.Count.EqualTo(4));
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

    private static GearItem Worn(ulong id, int slot, int mk, int condition, int max, int repairs = 0, bool broken = false, string item = "blaster_dl44")
        => new(id, slot == 101 ? ItemCatalog.Blaster : ItemCatalog.PaddedHelmet, slot, slot, 0, item, mk)
        {
            Condition = condition,
            MaxCondition = max,
            Repairs = repairs,
            Broken = broken,
        };

    private static HeldStack Kits(ushort kit, int n)
        => new(kit, kit == MarketPlan.KitWeapon ? "kit_weapon_repair" : "kit_armor_repair", MarketCategory.Other, 0, n);

    /// <summary>
    /// § 6, Repair: a worn item broken or under 20 % with a repair left is repaired when a kit of its class is held — one kit each, in slot order; not one
    /// above 20 %, not one with its two repairs used.
    /// </summary>
    [Test]
    public void ABot_RepairsWhatIsWornDown_WithAKitOfItsClass()
    {
        var gear = new List<GearItem>
        {
            Worn(1, 101, 1, 0, 1000, broken: true),
            Worn(2, 102, 1, 150, 1000, item: "armor_padded_helmet"),
            Worn(3, 103, 1, 199, 1000, item: "armor_padded_torso"),
            Worn(4, 104, 1, 201, 1000, item: "armor_padded_arms"),
            Worn(5, 105, 1, 100, 1000, repairs: 2, item: "armor_padded_legs"),
        };
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.RepairsDue(gear, [Kits(MarketPlan.KitWeapon, 1), Kits(MarketPlan.KitArmor, 1)]), Is.EqualTo(new ulong[] { 1, 2 }),
                "one armour kit: the head first; the arms are above 20 %, the legs spent");
            Assert.That(MarketPlan.RepairsDue(gear, [Kits(MarketPlan.KitArmor, 5)]), Is.EqualTo(new ulong[] { 2, 3 }), "no weapon kit: the blaster waits");
            Assert.That(MarketPlan.RepairsDue(gear, []), Is.Empty);
        });
    }

    /// <summary>
    /// § 6, Gear, as of Loïc's 2026-11-03 rule (decay finishes its job): a worn item is replaced once it is <b>broken</b> with no kit to repair it — its
    /// slot is wanted as if empty, and a sound piece is put on over it, even of the same Mk. Worn down but still working, or past its repairs, it is worn
    /// on: to the repair, or to the wear that destroys it.
    /// </summary>
    [Test]
    public void ABot_ReplacesWhatIsBrokenAndBeyondItsKits_AndWearsTheRestToTheEnd()
    {
        var spent = Worn(1, 101, 1, 0, 1000, broken: true);
        var spare = new GearItem(9, ItemCatalog.Blaster, 4, 101, 0, "blaster_dl44", 1) { Condition = 1000, MaxCondition = 1000 };
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.Spent(spent, []), Is.True);
            Assert.That(MarketPlan.Spent(Worn(1, 101, 1, 100, 1000, repairs: 2), []), Is.False, "past its repairs, worn until the wear destroys it");
            Assert.That(MarketPlan.Spent(Worn(1, 101, 1, 100, 1000), []), Is.False, "worn down, no kit: still worn, not swapped at 20 %");
            Assert.That(MarketPlan.Spent(Worn(1, 101, 1, 0, 1000, broken: true), [Kits(MarketPlan.KitWeapon, 1)]), Is.False, "a repair left, a kit held");
            Assert.That(MarketPlan.Spent(Worn(1, 101, 1, 0, 1000, broken: true), []), Is.True, "a repair left but no kit");
            Assert.That(MarketPlan.GearWanted([spent], null, 1, []), Is.EqualTo(("blaster_dl44", 101, 0)), "the same piece, any Mk of its band");
            Assert.That(MarketPlan.NextToWear([spent, spare], 1, ItemCatalog.Blaster, []), Is.EqualTo(9UL), "a sound spare over the spent one");
            Assert.That(MarketPlan.NextToWear([spent, spare], 1, ItemCatalog.Blaster), Is.Zero, "the premise: without the held stacks, no replacement");
            var downSpare = spare with { Broken = true, Condition = 0 };
            Assert.That(MarketPlan.NextToWear([Worn(1, 101, 1, 900, 1000), downSpare], 3, ItemCatalog.Blaster), Is.Zero, "never a worn-down spare");
        });
    }

    /// <summary>
    /// Review, 2026-11-02: worn-down gear leaves the economy — a bot never buys a worn-down listing, and sells its own worn-down spare to the vendor
    /// instead of listing it — and the vendor's standard issue covers a slot whose worn piece is spent, the bot's weapon included.
    /// </summary>
    [Test]
    public void WornDownGear_IsNeitherBoughtNorListed_AndTheVendorReplacesASpentPiece()
    {
        var prices = MarketPrices.None.With("blaster_dl44", 1, 1_000d).With("blaster_dl44", 2, 2_000d);
        BrowseRow Row(ulong id, int condition, bool broken = false)
            => new(id, 1, "blaster_dl44", MarketCategory.Other, 1, 1, 900, 900, 99) { Condition = condition, MaxCondition = 1000, Broken = broken };
        var spent = Worn(1, 101, 1, 0, 1000, broken: true);
        var spare = new GearItem(2, ItemCatalog.Blaster, 3, 101, 0, "blaster_dl44", 1) { Condition = 50, MaxCondition = 1000 };
        var rng = 1u;
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.ChooseGear([Row(1, 100), Row(2, 0, broken: true)], prices, 0, 1, 10_000, []), Is.Null, "a worn-down listing bought");
            Assert.That(MarketPlan.ChooseGear([Row(1, 100), Row(3, 900)], prices, 0, 1, 10_000, [])?.Id, Is.EqualTo(3UL));
            var gone = MarketPlan.Surplus([spent, spare], prices, 0, 1, ref rng, ItemCatalog.Blaster, false, []);
            Assert.That(gone.ConvertAll(c => (c.Step, c.Item)), Is.EqualTo(new[] { (MarketStep.Vendor, 2UL) }), "the worn-down spare is listed");
            Assert.That(MarketPlan.ChooseStandard(Offers, [spent], 10_000, [], ItemCatalog.Blaster)?.Template, Is.EqualTo(ItemCatalog.Blaster),
                "the spent blaster covers its slot");
            Assert.That(
                MarketPlan.ChooseStandard(Offers, [spent], 10_000)?.Template,
                Is.Not.EqualTo(ItemCatalog.Blaster),
                "the premise: by default, no weapon");
        });
    }

    [Test]
    public void TheInventoryRead_KeepsGearOutOfTheBazaarsGoods_AndTheWornOutOfTheGrid()
    {
        var json = """
            {"open":true,"slots":40,"items":[
              {"id":"5","slot":101,"item":"blaster_dl44","name":"DL-44 Blaster","category":"weapon","template":69,"mk":1,"count":1,
               "stats":{"damage":81,"delayMs":1021,"rangeDm":300,"wears":101,"condition":0,"maxCondition":1000,"lifeMilli":1000,"repairs":1,
                        "broken":true,"crafter":"0"}},
              {"id":"6","slot":1,"item":"mat_hide","name":"Hide","category":"material","template":8,"mk":2,"count":7}],
             "lost":[]}
            """;
        var held = new List<HeldStack>();
        var gear = new List<GearItem>();
        var view = InventoryView.Parse(json, InventoryView.None, false, out _, held, gear);
        Assert.Multiple(() =>
        {
            Assert.That(view.Stacks, Is.EqualTo(1), "the grid counts only the hide: the worn blaster is not in it");
            Assert.That(held.ConvertAll(h => h.Item), Is.EqualTo(new[] { "mat_hide" }));
            Assert.That(gear, Is.EqualTo(new[]
            {
                new GearItem(5, ItemCatalog.Blaster, EquipSlot.Weapon, EquipSlot.Weapon, 0, "blaster_dl44", 1)
                {
                    Condition = 0, MaxCondition = 1000, Repairs = 1, Broken = true,
                },
            }));
        });
    }
}
