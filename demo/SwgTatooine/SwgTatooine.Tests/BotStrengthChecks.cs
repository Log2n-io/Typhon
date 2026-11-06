using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// Strength as the bots' priority driver (Loïc, 2026-11-05: "they must buy what's best for them to be strong/resistance/equiped … make sure also they have
/// enough room in inventory to buy things, otherwise they sell or delete crap"): every slot weighed, not the weapon's alone; the strongest listing, not the
/// highest Mk; a whole set bought in one visit; room made for it.
/// </summary>
[TestFixture]
internal sealed class BotStrengthChecks
{
    private const ushort Blaster = ItemCatalog.Blaster;

    private static readonly List<StandardOffer> Offers = MarketJson.Standard(MarketEndpoints.StandardJson());

    private static readonly string[] Pieces = ["helmet", "torso", "arms", "legs", "gloves", "boots"];

    private static GearItem Weapon(ulong id, int slot, int mk = 1, int power = 80, bool broken = false)
        => new(id, Blaster, slot, EquipSlot.Weapon, 0, "blaster_dl44", mk)
        {
            Condition = broken ? 0 : 1000, MaxCondition = 1000, Broken = broken, Power = power,
        };

    private static GearItem Piece(ulong id, byte wears, int slot, int mk = 1, int power = 110, bool broken = false, int repairs = 0, bool composite = false)
        => new(id, (ushort)((composite ? ItemCatalog.CompositeHelmet : ItemCatalog.PaddedHelmet) + wears - EquipSlot.Head), slot, wears, 0,
            $"armor_{(composite ? "composite" : "padded")}_{Pieces[wears - EquipSlot.Head]}", mk)
        {
            Condition = broken ? 0 : 1000, MaxCondition = 1000, Broken = broken, Repairs = repairs, Power = power,
        };

    // Two full stims: six doses, none wanted — what would make a trip due on its own.
    private static GearItem Stim(ulong id) => new(id, GearPlan.Stim, 30 + (int)id, 0, 0, GearPlan.StimItem, 0) { Condition = 3, MaxCondition = 3 };

    private static BrowseRow Row(ulong id, string item, int mk, long unit, int power)
        => new(id, 1, item, MarketCategory.Other, mk, 1, unit, unit, 99) { Condition = 1000, MaxCondition = 1000, Power = power };

    /// <summary>
    /// The CL 31 bot of the live sample: a Mk I blaster, a broken helmet and broken legs worn (no kit), four slots empty, 216 000 cr. It asked for a DL-44
    /// above Mk I every visit — five listed planet-wide — and so for nothing else. Now every empty or spent slot comes first, the heaviest piece first, and
    /// the weapon's upgrade after; the vendor alone makes the trip due.
    /// </summary>
    [Test]
    public void EverySpentOrEmptySlot_ComesFirst_TheWeaponUpgradeDoesNotHideTheArmour()
    {
        var gear = new List<GearItem>
        {
            Weapon(1, EquipSlot.Weapon),
            Piece(3, EquipSlot.Head, EquipSlot.Head, broken: true, repairs: 1),
            Piece(4, EquipSlot.Legs, EquipSlot.Legs, broken: true, repairs: 1),
            Stim(5),
            Stim(6),
        };
        var needs = MarketPlan.GearNeeds(gear, Offers, 4, [], Blaster);
        Assert.Multiple(() =>
        {
            Assert.That(needs.Select(n => (int)n.Slot), Is.EqualTo(new[]
            {
                EquipSlot.Torso, EquipSlot.Arms, EquipSlot.Legs, EquipSlot.Head, EquipSlot.Hands, EquipSlot.Feet, EquipSlot.Weapon,
            }), "the armour by its share of the set, then the weapon below its band");
            Assert.That(needs.Take(6).All(n => n.HaveMk == 0), Is.True, "nothing sound in any armour slot");
            Assert.That(needs[0].Item, Is.EqualTo("armor_padded_torso"), "an empty slot browses the vendor's padded piece");
            Assert.That(needs[2].Item, Is.EqualTo("armor_padded_legs"), "a spent one its own piece");
            Assert.That(needs[6], Is.EqualTo(new GearNeed("blaster_dl44", EquipSlot.Weapon, 1, 80)), "the weapon it has, its Mk and power");
            Assert.That(GearPlan.Due(gear, [], Offers, 4, 216_000, Blaster, new MarketPrices()), Is.True, "nothing listed: the vendor's armour is a trip");
        });
    }

    /// <summary>
    /// A whole set of Mk I at band 3 with nothing listed but a Mk II torso: the torso makes the trip, the weapon's upgrade listed nowhere does not stop it.
    /// </summary>
    [Test]
    public void AnArmourUpgradeListed_IsATrip_ThoughTheWeaponIsBelowItsBandToo()
    {
        var gear = new List<GearItem> { Weapon(1, EquipSlot.Weapon), Weapon(2, 10), Stim(3), Stim(4) };   // its spare, its stims
        for (var slot = EquipSlot.Head; slot <= EquipSlot.Feet; slot++)
        {
            gear.Add(Piece(slot, slot, slot));
        }

        var listed = new MarketPrices().With("armor_composite_torso", 2, 4_374d).WithBoard("armor_composite_torso", 2, 3, 4_400);
        Assert.Multiple(() =>
        {
            Assert.That(GearPlan.Due(gear, [], Offers, 3, 100_000, Blaster, new MarketPrices()), Is.False, "the premise: nothing listed, nothing empty");
            Assert.That(GearPlan.Due(gear, [], Offers, 3, 100_000, Blaster, listed), Is.True, "the other family of the piece counts");
            Assert.That(GearPlan.Due(gear, [], Offers, 3, 5_000, Blaster, listed), Is.False, "not one it cannot pay for over its reserve");
        });
    }

    /// <summary>
    /// The listing bought is the strongest of its band that is worth it — power, not Mk — within twice its value and the wallet's reserve.
    /// </summary>
    [Test]
    public void TheStrongestListing_IsBought_ByPowerWithinTheBand_AndWorthItsPrice()
    {
        var prices = new MarketPrices().With("armor_padded_torso", 2, 4_374d).With("armor_padded_torso", 3, 8_656d).With("armor_composite_torso", 3, 8_656d);
        var rows = new List<BrowseRow>
        {
            Row(1, "armor_padded_torso", 2, 4_400, 190),
            Row(2, "armor_padded_torso", 3, 8_700, 250),
            Row(3, "armor_composite_torso", 3, 9_000, 270),
            Row(4, "armor_composite_torso", 3, 17_313, 300),   // over twice its value
        };
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.ChooseGear(rows, prices, 1, 3, 1_000_000, [], Offers, 110)?.Id, Is.EqualTo(3UL), "the strongest it may pay for");
            Assert.That(MarketPlan.ChooseGear(rows, prices, 1, 2, 1_000_000, [], Offers, 110)?.Id, Is.EqualTo(1UL), "within its band");
            Assert.That(MarketPlan.ChooseGear(rows, prices, 1, 3, 10_499, [], Offers, 110)?.Id, Is.EqualTo(2UL), "keeping its reserve");
            Assert.That(MarketPlan.ChooseGear(rows, prices, 2, 3, 1_000_000, [], Offers, 250), Is.Null, "8 % more is not worth it");
            rows.Add(Row(5, "armor_composite_torso", 3, 9_000, 280));
            Assert.That(MarketPlan.ChooseGear(rows, prices, 2, 3, 1_000_000, [], Offers, 250)?.Id, Is.EqualTo(5UL), "12 % more is");
            Assert.That(MarketPlan.ChooseGear(rows, prices, 1, 3, 1_000_000, [ 3UL ], Offers, 110)?.Id, Is.EqualTo(5UL), "never its own listing");
        });
    }

    /// <summary>What it buys by power it wears by power and keeps: a stronger piece of the same Mk goes on, a weaker one is surplus.</summary>
    [Test]
    public void AStrongerPieceOfTheSameMk_IsWorn_AndAWeakerOneIsSurplus()
    {
        var prices = new MarketPrices().With("armor_composite_torso", 1, 2_092d);
        var worn = Piece(1, EquipSlot.Torso, EquipSlot.Torso);
        var stronger = Piece(2, EquipSlot.Torso, 5, power: 170, composite: true);
        var weaker = Piece(3, EquipSlot.Torso, 6, power: 100, composite: true);
        var rng = 7u;
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.NextToWear([worn, stronger], 1, Blaster), Is.EqualTo(2UL), "the stronger one goes on");
            Assert.That(MarketPlan.NextToWear([worn, weaker], 1, Blaster), Is.Zero, "not the weaker");
            var surplus = MarketPlan.Surplus([worn, weaker, stronger], prices, 0, 1, ref rng, Blaster);
            Assert.That(surplus.Select(c => c.Item), Is.EqualTo(new[] { 3UL }), "the weaker is listed, the stronger kept to wear");
        });
    }

    /// <summary>Every slot still empty gets the vendor's piece in one visit, the weapon first, as far as the wallet goes — not one the board fills.</summary>
    [Test]
    public void TheVendorsIssue_FillsEveryEmptySlot_TheWeaponFirst_WithinTheWallet()
    {
        var gear = new List<GearItem> { Weapon(2, 1, broken: true) };
        var blaster = GearPlan.PriceOf(Offers, "blaster_dl44");
        var all = MarketPlan.StandardIssues(Offers, gear, 1_000_000, [], Blaster, [EquipSlot.Torso]);
        var poor = MarketPlan.StandardIssues(Offers, gear, blaster + MarketPlan.StandardReserve, [], Blaster, []);
        Assert.Multiple(() =>
        {
            Assert.That(all[0].Template, Is.EqualTo(Blaster), "the weapon first");
            Assert.That(all, Has.Count.EqualTo(6), "the weapon and five pieces: the torso comes off the board");
            Assert.That(all.Select(c => c.Template), Does.Not.Contain((ushort)(ItemCatalog.PaddedHelmet + 1)), "not the torso");
            Assert.That(poor.Select(c => c.Template), Is.EqualTo(new[] { Blaster }), "a wallet for the blaster buys the blaster only");
        });
    }

    /// <summary>A visit's buys go out one after the other, then one collect takes them all.</summary>
    [Test]
    public void SeveralBuys_GoOutTogether_ThenOneCollect()
    {
        var rng = 3u;
        var plan = MarketPlan.Plan([], MarketMine.None, new MarketPrices(), ref rng, false, null,
            moreBuys: [Row(11, "armor_padded_torso", 2, 4_400, 190), Row(12, "blaster_dl44", 2, 1_800, 97)]);
        Assert.That(plan, Is.EqualTo(new[] { MarketCommand.Buy(11, 4_400), MarketCommand.Buy(12, 1_800), MarketCommand.Collect() }));
    }

    /// <summary>
    /// Room for what a visit brings in: a full grid sells its cheapest stacks whole, throws away what the vendor pays nothing for, then parts with the items it
    /// will not use — never what it wears, its spare, its parts; and says how many slots it is still short.
    /// </summary>
    [Test]
    public void AFullGrid_MakesRoom_FromItsCheapestGoods_AndNeverFromWhatItKeeps()
    {
        var prices = new MarketPrices().With("blaster_dl44", 1, 906d);
        var held = new List<HeldStack>();
        for (ushort t = 1; t <= 6; t++)
        {
            held.Add(new HeldStack(t, MarketCatalog.AssetOf(t), MarketCategory.Material, 1, 50 * t) { Id = t });
        }

        held.Add(new HeldStack(200, "junk", MarketCategory.Material, 1, 3) { Id = 77 });   // no value: the vendor would refuse it
        var gear = new List<GearItem>
        {
            Weapon(101, EquipSlot.Weapon),
            Weapon(102, 10),   // its spare
            Weapon(103, 11),   // a second one: nothing to it
            Piece(104, EquipSlot.Head, 12, broken: true),
            new(105, CraftPlan.FirstPart, 13, 0, 500, "part_x", 1),
        };

        var room = MarketPlan.MakeRoom([], held, gear, prices, 50, 50, 3, 1, Blaster, out var shortBy);
        var bare = MarketPlan.MakeRoom([], [], gear, prices, 50, 50, 3, 1, Blaster, out var bareShort);
        var sold = new List<MarketCommand> { MarketCommand.Vendor(1, 1, 50), MarketCommand.Vendor(2, 1, 100), MarketCommand.Vendor(3, 1, 150) };
        var planned = MarketPlan.MakeRoom(sold, held, gear, prices, 50, 49, 3, 1, Blaster, out _);
        Assert.Multiple(() =>
        {
            Assert.That(room, Is.EqualTo(new[]
            {
                new MarketCommand(MarketStep.Discard, 200, 1, 3, 0, 0, 77),
                MarketCommand.Vendor(1, 1, 50),
                MarketCommand.Vendor(2, 1, 100),
                MarketCommand.Vendor(3, 1, 150),
            }), "the worthless thrown away, then the cheapest stacks whole: four slots for three goods and the margin");
            Assert.That(shortBy, Is.Zero);
            Assert.That(bare.Select(c => (c.Step, c.Item)), Is.EqualTo(new[] { (MarketStep.Discard, 104UL), (MarketStep.Vendor, 103UL) }),
                "no goods: the broken helmet thrown away, the second blaster sold — the worn one, the spare and the part kept");
            Assert.That(bareShort, Is.EqualTo(2), "two slots short: two goods stay out");
            Assert.That(planned, Is.Empty, "what the plan already sells is room");
        });
    }

    /// <summary>
    /// Loïc, 2026-11-05: "if they find +20% better elsewhere, they must buy remotely and go pick it up" — another city's listing only when 20 % stronger than
    /// what it would have here: the city's best, the vendor's issue for an empty slot, else what it has.
    /// </summary>
    [Test]
    public void AnotherCitysListing_IsBought_OnlyWhenTwentyPercentStronger()
    {
        var prices = new MarketPrices().With("armor_padded_torso", 2, 4_374d).With("armor_padded_torso", 3, 8_656d);
        BrowseRow In(ulong id, int city, int mk, int power) => Row(id, "armor_padded_torso", mk, 4_400, power) with { City = city };
        var upgrade = new GearNeed("armor_padded_torso", EquipSlot.Torso, 1, 110);
        var empty = new GearNeed("armor_padded_torso", EquipSlot.Torso, 0, 0);
        var vendor = Offers.First(o => o.Item == "armor_padded_torso").Power;
        var weak = (vendor * 6 / 5) - 1;
        Assert.Multiple(() =>
        {
            Assert.That(vendor, Is.GreaterThan(0), "the premise: the vendor's list says its piece's protection");
            Assert.That(MarketPlan.ChooseGearAnywhere([In(1, 1, 2, 200), In(2, 3, 3, 239)], prices, upgrade, 3, 1_000_000, [], Offers, 1),
                Is.EqualTo(((BrowseRow?)In(1, 1, 2, 200), false)), "19 % stronger elsewhere: bought here");
            Assert.That(MarketPlan.ChooseGearAnywhere([In(1, 1, 2, 200), In(2, 3, 3, 240)], prices, upgrade, 3, 1_000_000, [], Offers, 1),
                Is.EqualTo(((BrowseRow?)In(2, 3, 3, 240), true)), "20 % stronger: bought there, to fetch");
            Assert.That(MarketPlan.ChooseGearAnywhere([In(2, 3, 3, 131)], prices, upgrade, 3, 1_000_000, [], Offers, 1).Row, Is.Null,
                "nothing here: 20 % over what it has, not the 10 % a local buy needs");
            Assert.That(MarketPlan.ChooseGearAnywhere([In(2, 3, 3, 132)], prices, upgrade, 3, 1_000_000, [], Offers, 1).Remote, Is.True);
            Assert.That(MarketPlan.ChooseGearAnywhere([In(2, 3, 2, weak)], prices, empty, 3, 1_000_000, [], Offers, 1).Row, Is.Null,
                "an empty slot: the vendor's issue here is what it would have");
            Assert.That(MarketPlan.ChooseGearAnywhere([In(2, 3, 2, weak + 2)], prices, empty, 3, 1_000_000, [], Offers, 1).Remote, Is.True);
        });
    }

    /// <summary>The goods reserved for it, read off <c>/mine</c>: where each waits and what it is — the pick-up's city, and a piece not bought twice.</summary>
    [Test]
    public void ReservedGoods_SayWhereTheyWait_AndWhatTheyAre()
    {
        const string answer = """
            {"listed":[],"reserved":[{"id":"7","city":3,"item":"armor_composite_torso","mk":3,"qty":1},{"id":"8","city":1,"item":"mat_hide","mk":1,"qty":5}],
             "caps":{}}
            """;
        var mine = MarketMine.Parse(answer);
        Assert.Multiple(() =>
        {
            Assert.That(mine.ElsewhereThan(1), Is.EqualTo(3));
            Assert.That(mine.ElsewhereThan(3), Is.Zero, "a returned hide is no trip: gear only");
            Assert.That(MarketMine.Parse("{\"listed\":[],\"reserved\":[],\"caps\":{}}").ElsewhereThan(1), Is.Zero);
            Assert.That(mine.WaitsElsewhere("armor_composite_torso", 1), Is.True);
            Assert.That(mine.WaitsElsewhere("armor_composite_torso", 3), Is.False, "where it stands is no elsewhere");
            Assert.That(mine.WaitsElsewhere("armor_padded_torso", 1), Is.False);
        });
    }

    /// <summary>A discard names the units planned — a stack's, a crate's whole count — not one: a stack thrown away for room must leave its slot.</summary>
    [Test]
    public void ADiscard_SendsTheCountPlanned()
    {
        var (command, values) = BotSwarm.Encode(new MarketCommand(MarketStep.Discard, 200, 1, 37, 0, 0, 77));
        var (_, item) = BotSwarm.Encode(MarketCommand.Discard(45, 1, 78));
        Assert.Multiple(() =>
        {
            Assert.That(command, Is.EqualTo("DiscardItem"));
            Assert.That(values["count"].Numbers[0], Is.EqualTo(37d));
            Assert.That(item["count"].Numbers[0], Is.EqualTo(1d), "an item: one");
        });
    }

    /// <summary>
    /// A piece in the grid its level cannot wear covers nothing — for the vendor's issue as for the board's (review, 2026-11-05: a looted Mk III torso left
    /// a CL 15 bot bare-chested) — and a listing dearer than a visit would pay makes no trip.
    /// </summary>
    [Test]
    public void APieceAboveItsBand_CoversNothing_AndADearListingIsNoTrip()
    {
        var looted = Piece(1, EquipSlot.Torso, 5, mk: 3, power: 250);
        var torso = (ushort)(ItemCatalog.PaddedHelmet + 1);
        var gear = new List<GearItem> { Weapon(2, EquipSlot.Weapon), Weapon(3, 10), Stim(4), Stim(5) };
        for (var slot = EquipSlot.Head; slot <= EquipSlot.Feet; slot++)
        {
            gear.Add(Piece(slot, slot, slot));
        }

        var dear = new MarketPrices().With("armor_padded_torso", 2, 4_374d).WithBoard("armor_padded_torso", 2, 1, 8_749);
        var fair = new MarketPrices().With("armor_padded_torso", 2, 4_374d).WithBoard("armor_padded_torso", 2, 1, 8_748);
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.StandardIssues(Offers, [looted], 1_000_000, [], Blaster, [], 2).Select(c => c.Template), Does.Contain(torso),
                "band 2: the Mk III covers nothing");
            Assert.That(MarketPlan.StandardIssues(Offers, [looted], 1_000_000, [], Blaster, [], 3).Select(c => c.Template), Does.Not.Contain(torso),
                "band 3: it does");
            Assert.That(GearPlan.Due(gear, [], Offers, 2, 1_000_000, Blaster, dear), Is.False, "over twice its value: no trip");
            Assert.That(GearPlan.Due(gear, [], Offers, 2, 1_000_000, Blaster, fair), Is.True, "at it: a trip");
        });
    }

    /// <summary>
    /// The supply (Loïc, 2026-11-05): an item takes its core's Mk and a crafted core is Mk I, so a crafter looks for a looted core above Mk I — the highest
    /// the board lists and is not glutted with the item at — keeps it, and a hunter never sells one to the vendor.
    /// </summary>
    [Test]
    public void ACrafter_BuysALootedCoreAboveMkI_AndNobodyVendorsOne()
    {
        var book = CraftBook.Parse(CraftEndpoints.SchematicsJson());
        var prices = new MarketPrices().WithBoard("core_ranged", 2, 97, 1_524).WithBoard("core_ranged", 3, 9, 3_720).WithBoard("core_ranged", 4, 0, 0)
            .WithBoard("blaster_dl44", 3, CraftPlan.SaturatedListings, 3_000);
        HeldStack Core(int mk, int count = 1) => new(CraftPlan.CoreRanged, "core_ranged", MarketCategory.Core, mk, count);
        var rng = 5u;
        var held = new List<HeldStack> { Core(1, 40), Core(2), new(3, "mat_metal", MarketCategory.Material, 1, 400) };
        var plan = MarketPlan.Plan(held, MarketMine.None, new MarketPrices(), ref rng, false, null, 30, maxListed: 0);
        Assert.Multiple(() =>
        {
            Assert.That(CraftPlan.CoreOf(book, CraftPlan.Blaster)?.Item, Is.EqualTo("core_ranged"));
            Assert.That(CraftPlan.CoreMks(book, CraftPlan.Blaster, [], prices), Is.EqualTo(new[] { 2 }),
                "Mk IV listed by nobody, Mk III blasters glutted: Mk II");
            Assert.That(CraftPlan.CoreMks(book, CraftPlan.Blaster, [Core(3)], prices), Is.Empty, "one held already");
            Assert.That(CraftPlan.CoreMks(book, CraftPlan.Blaster, [Core(1)], prices), Is.EqualTo(new[] { 2 }), "a Mk I core is no reason");
            Assert.That(CraftPlan.CoreMks(book, GearPlan.Stim, [], prices), Is.Empty, "a stim has no core");
            Assert.That(plan.Where(c => c.Step == MarketStep.Vendor).Select(c => (c.Template, c.Mk)), Does.Not.Contain((CraftPlan.CoreRanged, 2)),
                "the Mk II core waits for a listing");
            Assert.That(plan.Where(c => c.Step == MarketStep.Vendor).Select(c => (c.Template, c.Mk)), Does.Contain((CraftPlan.CoreRanged, 1)),
                "the premise: the Mk I ones go");
            var keeps = CraftPlan.Keeps(book, [CraftPlan.Blaster]);
            Assert.That(CraftPlan.Tradeable([Core(1, 40), Core(1, 30), Core(3)], keeps).Select(s => s.Mk), Does.Not.Contain(3),
                "a crafter keeps its looted core, small as its stack is");
        });
    }

    /// <summary>
    /// 2026-11-05: 35 000 Mk I cores and add-ons held the board, none sold, and every bot's listing slots with them — so the looted cores above Mk I waited
    /// in the grids unlisted. A Mk I core or add-on is never listed but vendored, its own listings of them are taken off the board, a few a visit, and a
    /// Mk II+ core takes the freed slot.
    /// </summary>
    [Test]
    public void MkICoresAndAddOns_AreNeverListed_AndTheirListingsComeOff()
    {
        var rng = 9u;
        var listedRows = Enumerable.Range(1, 5).Select(i => new MineListing((ulong)i, "core_armor", MarketCategory.Core, 1)).ToArray();
        var mine = new MarketMine([1, 2, 3, 4, 5], 0) { ListedRows = listedRows };
        var held = new List<HeldStack>
        {
            new(CraftPlan.CoreArmor, "core_armor", MarketCategory.Core, 1, 3),
            new(CraftPlan.CoreArmor, "core_armor", MarketCategory.Core, 3, 1),
        };
        var plan = MarketPlan.Plan(held, mine, new MarketPrices(), ref rng, false, null, 30);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Where(c => c.Step == MarketStep.Cancel).Select(c => c.Listing), Is.EqualTo(new ulong[] { 1, 2, 3, 4, 5 }));
            Assert.That(plan.Where(c => c.Step == MarketStep.List).Select(c => c.Mk), Is.EqualTo(new[] { 3 }), "the Mk III takes a freed slot");
            Assert.That(plan.Where(c => c.Step == MarketStep.Vendor).Select(c => c.Mk), Is.EqualTo(new[] { 1 }), "the Mk I goes to the vendor");
            Assert.That(BotSwarm.Encode(MarketCommand.Cancel(4)).Command, Is.EqualTo("MarketCancel"));
        });
    }

    /// <summary>A listing's and an item's power: a weapon's damage a second, × 1 000; an armour piece's protection, the mean of its two.</summary>
    [Test]
    public void Power_IsTheWeaponsDamageASecond_OrThePiecesMeanProtection()
    {
        const string page = """
            {"listings":[
              {"id":"1","city":1,"item":"blaster_dl44","category":"weapon","mk":2,"qty":1,"unit":1795,"total":1795,"seller":"9",
               "stats":{"damage":97,"delayMs":1007,"condition":10,"maxCondition":10,"broken":false}},
              {"id":"2","city":1,"item":"armor_padded_torso","category":"armor","mk":3,"qty":1,"unit":8000,"total":8000,"seller":"9",
               "stats":{"kinetic":250,"energy":246,"condition":10,"maxCondition":10,"broken":false}},
              {"id":"3","city":1,"item":"mat_hide","category":"material","mk":1,"qty":5,"unit":2,"total":10,"seller":"9"}]}
            """;
        Assert.That(MarketJson.Browse(page).Select(r => r.Power), Is.EqualTo(new[] { 97 * 1000 / 1007, 248, 0 }));
    }
}
