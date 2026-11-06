using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The crafters' economy (Loïc, 2026-11-04: "CRAFTER SHOULD GET RICH"): repair kits have a value and are made, the scarcest goods are crafted first, a
/// piece the vendor also sells is listed under its price and bought up to it, kits are listed and bought and never sold to the vendor, and a crafter goes to
/// sell what it made.
/// </summary>
[TestFixture]
internal sealed class BotCrafterEconomyChecks
{
    private static readonly List<StandardOffer> Offers = MarketJson.Standard(MarketEndpoints.StandardJson());

    private static HeldStack Kits(ushort kit, int n)
        => new(kit, kit == CraftPlan.KitWeapon ? "kit_weapon_repair" : "kit_armor_repair", MarketCategory.Other, 0, n);

    /// <summary>A repair kit is worth 150 cr on the server and to the bots alike, whatever its class, and nothing at a Mk it does not have.</summary>
    [Test]
    public void ARepairKit_HasAValue()
    {
        Assert.Multiple(() =>
        {
            Assert.That(MarketValues.CreditsOf(ItemCatalog.KitWeaponRepair, 0), Is.EqualTo(150));
            Assert.That(MarketValues.CreditsOf(ItemCatalog.KitArmorRepair, 0), Is.EqualTo(150));
            Assert.That(MarketValues.CreditsOf(ItemCatalog.KitWeaponRepair, 1), Is.Zero, "a kit has no Mk");
            Assert.That(MarketValues.VendorPayout(ItemCatalog.KitWeaponRepair, 0, 1), Is.EqualTo(30));
            Assert.That(MarketCatalog.ModelValue("kit_weapon_repair", 0), Is.EqualTo(MarketValues.KitMilli / 1000d), "the bots' copy");
        });
    }

    /// <summary>A crafter keeps as many listings as the server lets anyone (its living is the board).</summary>
    [Test]
    public void ACrafter_ListsUpToTheServersCap() => Assert.That(MarketPlan.CrafterMaxListed, Is.EqualTo(TatooineData.MaxListings));

    /// <summary>Each line makes its kit; the scarcest on the board comes first, the line's order between equals.</summary>
    [Test]
    public void ACrafter_MakesTheScarcestFirst_KitsIncluded()
    {
        var weapons = CraftPlan.Targets(CraftLine.Weapon);
        var prices = MarketPrices.None;
        var board = new MarketPrices()
            .WithBoard("blaster_dl44", 1, 21, 827)
            .WithBoard("blaster_dl44", 2, 1, 1925)
            .WithBoard("sword_vibroblade", 1, 32, 837)
            .WithBoard("vibroknuckler", 1, 49, 818);
        Assert.Multiple(() =>
        {
            Assert.That(weapons, Does.Contain(CraftPlan.KitWeapon));
            Assert.That(CraftPlan.Targets(CraftLine.Armor), Does.Contain(CraftPlan.KitArmor));
            Assert.That(CraftPlan.ByDemand(weapons, prices), Is.EqualTo(weapons), "nothing read: the line's order");
            Assert.That(
                CraftPlan.ByDemand(weapons, board),
                Is.EqualTo(new[] { CraftPlan.KitWeapon, CraftPlan.Blaster, CraftPlan.Sword }),
                "no kit listed, then 22 blasters, 32 swords — and 49 knucklers is a glut: not made");
            var glut = new MarketPrices().WithBoard("blaster_dl44", 1, 90, 900).WithBoard("kit_weapon_repair", 0, 50, 200)
                .WithBoard("sword_vibroblade", 1, 60, 800).WithBoard("vibroknuckler", 1, 70, 800);
            Assert.That(CraftPlan.ByDemand(weapons, glut), Is.EqualTo(new[] { CraftPlan.KitWeapon }), "everything glutted: the scarcest still made");
            var selling = new MarketPrices().WithBoard("blaster_dl44", 1, 2, 1_422, 146).WithBoard("kit_weapon_repair", 0, 30, 218, 47)
                .WithBoard("sword_vibroblade", 1, 10, 800, 0).WithBoard("vibroknuckler", 1, 5, 800, 0);
            Assert.That(
                CraftPlan.ByDemand(weapons, selling),
                Is.EqualTo(new[] { CraftPlan.Blaster, CraftPlan.KitWeapon, CraftPlan.Knuckler, CraftPlan.Sword }),
                "the night's board: blasters sell out, kits sell, the melee weapons sit");
        });
    }

    /// <summary>
    /// Each Mk a crafter can make is weighed apart (the night of 2026-11-06): Mk I pieces glutted, Mk II of each sold out with its core on the board — the
    /// line makes every piece, the most sold-out first, where every Mk counted together cut four pieces of six and the armour line made arms and kits.
    /// </summary>
    [Test]
    public void ACrafter_WeighsEachMkItCanMake_AMkIGlutCutsNotTheMkIIPiece()
    {
        var book = CraftBook.Parse(CraftEndpoints.SchematicsJson());
        var armour = CraftPlan.Targets(CraftLine.Armor);
        var board = new MarketPrices()
            .WithBoard("core_armor", 2, 834, 1_433)
            .WithBoard("kit_armor_repair", 0, 22, 217, 1_843)
            .WithBoard("armor_padded_helmet", 1, 72, 988, 85).WithBoard("armor_padded_helmet", 2, 0, 0, 16)
            .WithBoard("armor_padded_torso", 1, 20, 3_319, 18)
            .WithBoard("armor_padded_arms", 1, 36, 2_193, 41)
            .WithBoard("armor_padded_legs", 1, 76, 2_032, 50).WithBoard("armor_padded_legs", 2, 0, 0, 10)
            .WithBoard("armor_padded_gloves", 1, 71, 456, 153).WithBoard("armor_padded_gloves", 2, 0, 0, 35)
            .WithBoard("armor_padded_boots", 1, 63, 456, 179).WithBoard("armor_padded_boots", 2, 0, 0, 40);
        var noCore = new MarketPrices()
            .WithBoard("blaster_dl44", 1, 1_315, 1_087, 1_543).WithBoard("blaster_dl44", 2, 0, 0, 39)
            .WithBoard("kit_weapon_repair", 0, 12, 216, 174)
            .WithBoard("sword_vibroblade", 1, 72, 1_087).WithBoard("vibroknuckler", 1, 83, 1_087)
            .WithBoard("core_melee", 2, 277, 1_426).WithBoard("core_melee", 3, 82, 2_926)
            .WithBoard("sword_vibroblade", 2, 23, 1_664).WithBoard("vibroknuckler", 3, 2, 3_457);
        Assert.Multiple(() =>
        {
            Assert.That(
                CraftPlan.ByDemand(armour, board, book),
                Is.EqualTo(new ushort[] { CraftPlan.KitArmor, 49, 48, 44, 47, 46, 45 }),
                "kits, then boots, gloves, helmet, legs by their sold-out Mk II; arms and torso on their Mk I");
            Assert.That(CraftPlan.ByDemand(armour, board), Is.EqualTo(new ushort[] { CraftPlan.KitArmor, 46, 45 }), "without the book: every Mk together");
            Assert.That(
                CraftPlan.ByDemand(CraftPlan.Targets(CraftLine.Weapon), noCore, book),
                Is.EqualTo(new[] { CraftPlan.KitWeapon }),
                "Mk I blasters glutted, no Mk II ranged core to make more: not made; melee glutted whole — none sold at any Mk, its cores on the board");
        });
    }

    /// <summary>
    /// A target the board is glutted with at Mk I is crafted only with a looted core in hand (the night of 2026-11-06: crafters short of a Mk II core
    /// crafted a Mk I one, and 2 296 Mk I weapons were listed within the hour); one that is not, or a kit, as before.
    /// </summary>
    [Test]
    public void APieceGluttedAtMkI_IsCraftedOnlyWithALootedCore()
    {
        var book = CraftBook.Parse(CraftEndpoints.SchematicsJson());
        var weapons = CraftPlan.Targets(CraftLine.Weapon);
        var board = new MarketPrices().WithBoard("blaster_dl44", 1, 1_315, 1_087, 1_543).WithBoard("kit_weapon_repair", 0, 195, 216, 174);
        var lootedOnly = CraftPlan.MkIGlutted(weapons, board, book);
        var core = CraftPlan.CoreOf(book, CraftPlan.Blaster).Value;
        HeldStack Core(int mk) => new(core.Template, core.Item, MarketCategory.Core, mk, core.Count);
        List<HeldStack> bulk =
        [
            new(ItemCatalog.Metal, "mat_metal", MarketCategory.Material, 1, 60), new(ItemCatalog.Crystal, "mat_crystal", MarketCategory.Material, 1, 5),
        ];
        List<HeldPart> parts = [new(1, Schematics.BarrelPistol, 300), new(3, Schematics.PowerCell, 400), new(4, Schematics.Grip, 500)];
        Assert.Multiple(() =>
        {
            Assert.That(lootedOnly, Is.EquivalentTo(new[] { CraftPlan.Blaster }), "blasters glutted at Mk I; kits have no core; melee not listed");
            Assert.That(CraftPlan.MkIGlutted(weapons, board, null), Is.Empty, "without the book");
            Assert.That(CraftPlan.HoldsLootedCore(book, CraftPlan.Blaster, [Core(1)]), Is.False, "a Mk I core is no looted one");
            Assert.That(CraftPlan.HoldsLootedCore(book, CraftPlan.Blaster, [Core(2)]), Is.True);
            Assert.That(CraftPlan.Next(book, [CraftPlan.Blaster], 0, bulk, parts)?.Schematic, Is.EqualTo(ItemCatalog.CoreRanged),
                "the premise: from bulk it would craft a Mk I core, toward a Mk I blaster");
            Assert.That(CraftPlan.Next(book, [CraftPlan.Blaster], 0, bulk, parts, lootedOnly), Is.Null, "glutted at Mk I: no Mk I core, no Mk I blaster");
            Assert.That(CraftPlan.Next(book, [CraftPlan.Blaster], 0, [.. bulk, Core(2)], parts, lootedOnly)?.Schematic, Is.EqualTo(CraftPlan.Blaster),
                "a looted Mk II core: the blaster");
        });
    }

    /// <summary>
    /// What a crafter cannot craft at Mk III it refines Mk II into, whatever its band (Loïc, 2026-11-06: that is the feature's point): N Mk II cores of its
    /// target into a Mk III when the board wants the Mk III item and lists no Mk III core — buying the Mk II it lacks — and its add-ons N at a time; its
    /// craft planned from what the refines leave.
    /// </summary>
    [Test]
    public void ACrafter_RefinesMkIIIntoTheMkIIIItCannotBuy_AndBuysTheMkIIItLacks()
    {
        var book = CraftBook.Parse(CraftEndpoints.SchematicsJson());
        var core = CraftPlan.CoreOf(book, 44).Value;
        var n = RefinePlan.RatioOf(2);
        HeldStack Core(int mk, int count) => new(core.Template, core.Item, MarketCategory.Core, mk, count);
        HeldStack Addon(int mk, int count) => new(CraftPlan.AddonArmor, "addon_armor", MarketCategory.Addon, mk, count);
        var noMkIII = new MarketPrices().WithBoard(core.Item, 2, 40, 1_433).WithBoard("armor_padded_helmet", 3, 0, 0, 15);
        var mkIIIListed = new MarketPrices().WithBoard(core.Item, 2, 40, 1_433).WithBoard(core.Item, 3, 2, 2_936);
        var gluttedAtIII = new MarketPrices().WithBoard(core.Item, 2, 40, 1_433).WithBoard("armor_padded_helmet", 3, 45, 3_418);
        List<HeldStack> held = [Core(2, n * core.Count + 1), Addon(2, 9), Addon(3, 2)];
        Assert.Multiple(() =>
        {
            Assert.That(
                CraftPlan.RefineUp(book, 44, held, noMkIII),
                Is.EqualTo(new List<(ushort, int, int)> { (core.Template, 2, core.Count), (CraftPlan.AddonArmor, 2, 9 / n) }),
                "N Mk II cores into the Mk III core, 9 Mk II add-ons into two Mk III — the 2 Mk III add-ons held are short of N");
            Assert.That(CraftPlan.RefineUp(book, 44, held, mkIIIListed), Has.None.Matches<(ushort T, int M, int C)>(r => r.T == core.Template),
                "a Mk III core on the board: bought, not refined");
            Assert.That(CraftPlan.RefineUp(book, 44, held, gluttedAtIII), Has.None.Matches<(ushort T, int M, int C)>(r => r.T == core.Template),
                "the Mk III item glutted: no reason to");
            Assert.That(CraftPlan.RefineUp(book, 44, [Core(2, n), Core(3, core.Count)], noMkIII), Is.Empty, "a Mk III core held: it crafts that");
            Assert.That(CraftPlan.RefineUp(book, 44, [Core(1, 40)], noMkIII), Is.Empty, "never from Mk I, a crafted core");

            var left = CraftPlan.Without(held, CraftPlan.RefineUp(book, 44, held, noMkIII));
            Assert.That(left, Is.EquivalentTo(new[] { Core(2, 1), Addon(2, 9 % n), Addon(3, 2) }), "what the craft plans from: the refines' inputs gone");

            Assert.That(CraftPlan.RefineBuy(book, 44, [Core(2, 1)], noMkIII), Is.EqualTo((core, 2, (n * core.Count) - 1)), "it buys what it lacks of N");
            Assert.That(CraftPlan.RefineBuy(book, 44, [Core(2, 1)], mkIIIListed), Is.Null, "the Mk III core listed: the core buy's, not this");
            Assert.That(CraftPlan.RefineBuy(book, 44, held, noMkIII), Is.Null, "N held already");
        });
    }

    /// <summary>The cheapest listings that make a count together, within the wallet's share; none when they do not.</summary>
    [Test]
    public void ChooseMany_TakesTheCheapestThatMakeTheCount()
    {
        var prices = new MarketPrices().WithBoard("core_armor", 2, 3, 1_400);
        BrowseRow Row(ulong id, long unit) => new(id, 1, "core_armor", MarketCategory.Core, 2, 1, unit, unit, 9);
        List<BrowseRow> rows = [Row(1, 1_600), Row(2, 1_400), Row(3, 1_500), Row(4, 900_000)];
        Assert.Multiple(() =>
        {
            Assert.That(CraftPlan.ChooseMany(rows, prices, "core_armor", 2, 1_000_000, []).ConvertAll(r => r.Id), Is.EqualTo(new ulong[] { 2, 3 }));
            Assert.That(CraftPlan.ChooseMany(rows, prices, "core_armor", 4, 1_000_000, []), Is.Empty, "the fourth is past its value: three only");
            Assert.That(CraftPlan.ChooseMany(rows, prices, "core_armor", 2, 1_000, []), Is.Empty, "past the wallet's share");
            Assert.That(CraftPlan.ChooseMany(rows, prices, "core_armor", 2, 1_000_000, [2UL]).ConvertAll(r => r.Id), Is.EqualTo(new ulong[] { 3, 1 }),
                "never its own");
        });
    }

    /// <summary>
    /// A piece the vendor sells is listed at 75-90 % of the vendor's price — not at its value, half what a buyer pays anyway — and under a cheaper listing,
    /// never under its value; a buyer pays up to the vendor's price for it, where BuyOverValue alone refused the crafters'.
    /// </summary>
    [Test]
    public void APieceIsListedUnderTheVendor_AndBoughtUpToIt()
    {
        var vendor = MarketPlan.StandardPrice(Offers, "blaster_dl44", 1);
        var rng = 7u;
        var empty = new MarketPrices().With("blaster_dl44", 1, 906d);
        var cheap = new MarketPrices().With("blaster_dl44", 1, 906d).WithBoard("blaster_dl44", 1, 3, 1_000);
        var prices = new List<long>();
        for (var i = 0; i < 50; i++)
        {
            prices.Add(MarketPlan.ListPrice(906d, "blaster_dl44", 1, empty, Offers, ref rng));
        }

        BrowseRow Row(long unit) => new(1, 1, "blaster_dl44", MarketCategory.Other, 1, 1, unit, unit, 99) { Condition = 1000, MaxCondition = 1000 };
        Assert.Multiple(() =>
        {
            Assert.That(vendor, Is.GreaterThan(906), "the premise: the vendor sells the DL-44 over its value");
            Assert.That(prices, Has.All.InRange((long)(vendor * MarketPlan.UnderVendorLow) - 1, (long)(vendor * MarketPlan.UnderVendorHigh) + 1));
            Assert.That(MarketPlan.ListPrice(906d, "blaster_dl44", 1, cheap, Offers, ref rng), Is.EqualTo((long)(vendor * MarketPlan.UnderVendorFloor)),
                "under the lowest listing, but never under 60 % of the vendor's: an undercut slid to the value");
            var dear = new MarketPrices().With("blaster_dl44", 1, 906d).WithBoard("blaster_dl44", 1, 3, 1_500);
            Assert.That(MarketPlan.ListPrice(906d, "blaster_dl44", 1, dear, Offers, ref rng), Is.LessThanOrEqualTo(1_470), "2 % under a dearer one");
            Assert.That(MarketPlan.ListPrice(906d, "blaster_dl44", 2, empty, Offers, ref rng), Is.InRange(815, 997), "no vendor at Mk II: its value's draw");

            Assert.That(MarketPlan.ChooseGear([Row(vendor - 1)], empty, 0, 1, 100_000, [], Offers), Is.Not.Null, "under the vendor: bought");
            Assert.That(MarketPlan.ChooseGear([Row(vendor + 1)], empty, 0, 1, 100_000, [], Offers), Is.Null, "dearer than the vendor: not");
            var over = (long)(906d * MarketPlan.GearOverValue) + 1;
            Assert.That(MarketPlan.ChooseGear([Row(over)], empty, 0, 1, 100_000, []), Is.Null, "without the vendor's list, GearOverValue × value");
        });
    }

    /// <summary>
    /// The night of 2026-11-04: a crafter's listing slots went to cheap gloves and its blasters to the vendor at a fifth of their value. The dearest take
    /// the slots first, and a sound piece goes to the vendor only when the board is glutted with it — anything else waits for a slot.
    /// </summary>
    [Test]
    public void TheDearestAreListed_AndAPieceTheVendorSellsIsNotDumped()
    {
        var prices = new MarketPrices().With("blaster_dl44", 1, 906d).With("armor_padded_gloves", 1, 343d).With("blaster_dl44", 2, 1795d)
            .WithBoard("armor_padded_gloves", 1, 81, 343).WithBoard("blaster_dl44", 1, 3, 1_231);
        GearItem Blast(ulong id, int slot, int mk = 1) => new(id, CraftPlan.Blaster, slot, 101, 0, "blaster_dl44", mk) { Condition = 1000, MaxCondition = 1000 };
        var gloves = new GearItem(5, 48, 7, 106, 0, "armor_padded_gloves", 1) { Condition = 1000, MaxCondition = 1000 };
        var wornGloves = gloves with { Id = 6, Slot = 106 };
        var gear = new List<GearItem> { Blast(1, 101), Blast(2, 3), gloves, wornGloves, Blast(3, 4), Blast(4, 5, 2) };
        var rng = 5u;

        // One slot left: the Mk II blaster takes it, not the gloves; the Mk I ones wait (three listed: they sell), the gloves (81 listed) go to the vendor.
        var plan = MarketPlan.Surplus(gear, prices, MarketPlan.MaxListed - 1, 1, ref rng, CraftPlan.Blaster, vendorRest: true, held: [], offers: Offers);
        Assert.Multiple(() =>
        {
            Assert.That(plan.FindAll(c => c.Step == MarketStep.List).ConvertAll(c => c.Item), Is.EqualTo(new ulong[] { 4 }), "the dearest first");
            Assert.That(plan.Exists(c => c.Step == MarketStep.Vendor && c.Template == CraftPlan.Blaster), Is.False, "a sound DL-44 dumped on the vendor");
            Assert.That(plan.Exists(c => c.Step == MarketStep.Vendor && c.Item == 5UL), Is.True, "the premise: what the board is glutted with still goes");
        });
    }

    /// <summary>
    /// Kits past the two it keeps are listed, two a listing, at the kit markup; the kits it keeps, and every kit, stay out of the vendor's sale even with
    /// the grid over its floor.
    /// </summary>
    [Test]
    public void Kits_AreListedPastTheKept_AndNeverVendored()
    {
        var prices = new MarketPrices().With("kit_weapon_repair", 0, 150d).With("mat_metal", 1, 1.7d);
        var held = new List<HeldStack> { Kits(CraftPlan.KitWeapon, 7), Kits(CraftPlan.KitArmor, 2) };
        for (var i = 0; i < 20; i++)
        {
            held.Add(new HeldStack(3, "mat_metal", MarketCategory.Material, 1, 99));
        }

        var rng = 3u;
        var plan = MarketPlan.Plan(held, MarketMine.None, prices, ref rng, false, null, 40);
        var listedKits = plan.FindAll(c => c.Step == MarketStep.List && CraftPlan.IsKit(c.Template));
        Assert.Multiple(() =>
        {
            Assert.That(listedKits.ConvertAll(c => (c.Template, c.Quantity)), Is.EqualTo(new[] { (CraftPlan.KitWeapon, 2) }), "five spare: a lot of two");
            Assert.That(listedKits[0].UnitPrice, Is.InRange(150 * MarketPlan.KitMarkup * 0.9 - 1, 150 * MarketPlan.KitMarkup * 1.1 + 1));
            Assert.That(plan.Exists(c => c.Step == MarketStep.Vendor && CraftPlan.IsKit(c.Template)), Is.False, "a kit sold to the vendor");
            Assert.That(plan.Exists(c => c.Step == MarketStep.Vendor), Is.True, "the premise: the grid is over its floor, the metal goes");
        });
    }

    /// <summary>
    /// A kit is wanted for a worn-down piece with a repair left and none of its class held — its spare weapon's too — and bought at the cheapest unit up to
    /// three times its value, keeping the reserve; the assessment counts it as a trip.
    /// </summary>
    [Test]
    public void AKit_IsBoughtForWhatItCanRepair()
    {
        var worn = new GearItem(1, CraftPlan.Blaster, 101, 101, 0, "blaster_dl44", 1) { Condition = 100, MaxCondition = 1000 };
        var helmet = new GearItem(2, ItemCatalog.PaddedHelmet, 102, 102, 0, "armor_padded_helmet", 1) { Condition = 0, MaxCondition = 1000, Broken = true };
        var prices = new MarketPrices().With("kit_weapon_repair", 0, 150d);
        BrowseRow Row(ulong id, long unit, int qty = 1)
            => new(id, 1, "kit_weapon_repair", MarketCategory.Other, 0, qty, unit, unit * qty, 99);
        Assert.Multiple(() =>
        {
            Assert.That(GearPlan.KitWanted([worn], []), Is.EqualTo(CraftPlan.KitWeapon));
            Assert.That(GearPlan.KitWanted([worn], [Kits(CraftPlan.KitWeapon, 1)]), Is.Zero, "one held");
            Assert.That(GearPlan.KitWanted([worn with { Repairs = MarketPlan.MaxRepairs }], []), Is.Zero, "past its repairs: replaced, not repaired");
            Assert.That(GearPlan.KitWanted([worn with { Condition = 900 }, helmet], []), Is.EqualTo(CraftPlan.KitArmor), "the broken helmet");

            Assert.That(GearPlan.ChooseKit([Row(1, 400), Row(2, 300), Row(3, 451)], prices, 10_000, [])?.Id, Is.EqualTo(2UL), "the cheapest");
            Assert.That(GearPlan.ChooseKit([Row(3, 451)], prices, 10_000, []), Is.Null, "past three times its value");
            Assert.That(GearPlan.ChooseKit([Row(2, 300)], prices, MarketPlan.StandardReserve + 299, []), Is.Null, "into its reserve");
            Assert.That(GearPlan.ChooseKit([Row(2, 300)], prices, 10_000, [2UL]), Is.Null, "its own listing");

            Assert.That(GearPlan.Due([worn], [], Offers, 1, 1_000_000, CraftPlan.Blaster), Is.True, "a kit to buy: a trip");
        });
    }

    /// <summary>
    /// The night of 2026-11-04: goods reserved for a bot in another city held its gear back ("not while goods wait") and drew a refused collect at every
    /// visit. What waits is counted by city.
    /// </summary>
    [Test]
    public void ReservedGoods_AreCountedByCity()
    {
        var mine = MarketMine.Parse("""{"listed":[{"id":"5"}],"reserved":[{"id":"7","city":3},{"id":"8","city":3},{"id":"9","city":1}],"caps":{}}""");
        Assert.Multiple(() =>
        {
            Assert.That(mine.Reserved, Is.EqualTo(3));
            Assert.That(mine.ReservedIn(3), Is.EqualTo(2));
            Assert.That(mine.ReservedIn(1), Is.EqualTo(1));
            Assert.That(mine.ReservedIn(2), Is.Zero);
            Assert.That(mine.ReservedIn(0), Is.Zero, "in the desert, no bazaar's");
        });
    }

    /// <summary>What a crafter counts as goods to sell: what it made and will neither wear nor keep as its spare, and kits past the kept, a lot each.</summary>
    [Test]
    public void ACrafter_CountsItsGoods()
    {
        GearItem Blast(ulong id, int slot, int condition = 1000) => new(id, CraftPlan.Blaster, slot, 101, 0, "blaster_dl44", 1)
        {
            Condition = condition,
            MaxCondition = 1000,
        };

        var gear = new List<GearItem> { Blast(1, 101), Blast(2, 3), Blast(3, 4), Blast(4, 5), Blast(5, 6, 0) with { Broken = true } };
        gear.Add(new GearItem(6, 25, 7, 0, 800, "part_grip", 0));
        Assert.Multiple(() =>
        {
            Assert.That(CraftPlan.ForSale(gear, [], 2, 0), Is.EqualTo(2), "two made, past the worn, the spare, the broken and the part");
            Assert.That(CraftPlan.ForSale(gear, [Kits(CraftPlan.KitWeapon, 9)], 2, 3), Is.EqualTo(1 + 4), "seven kits past the kept: four lots of two");
        });
    }
}
