using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// The bots' bazaar (bazaar.md § 8): what a bot reads of the market, and the plan it sends at a bazaar — as pure functions of what it read and its own
/// draws. The brain's half (when it goes, how long it stays, the rush's rounds) is <see cref="BotErrandChecks"/>'.
/// </summary>
[TestFixture]
public sealed class BotMarketChecks
{
    private static readonly MarketPrices Prices = new MarketPrices()
        .With("core_armor", 2, 1549d)
        .With("core_ranged", 2, 1549d)
        .With("addon_weapon", 2, 1549d)
        .With("addon_weapon", 3, 2600d)
        .With("mat_hide", 1, 1.7d)
        .With("mat_hide", 2, 2.8d)
        .With("mat_bone", 1, 1.7d)
        .With("mat_bone", 3, 4.5d);

    private static HeldStack Core(string item, int mk, int count = 1) =>
        new(MarketCatalog.TemplateOf(item), item, item.StartsWith("core") ? MarketCategory.Core : MarketCategory.Addon, mk, count);

    private static HeldStack Material(string item, int mk, int count) => new(MarketCatalog.TemplateOf(item), item, MarketCategory.Material, mk, count);

    /// <summary>25 stacks: two cores and an add-on, then materials of three values.</summary>
    private static List<HeldStack> FullInventory()
    {
        var held = new List<HeldStack> { Core("core_armor", 2), Core("addon_weapon", 2, 2), Core("core_ranged", 2) };
        for (var i = 0; i < 8; i++)
        {
            held.Add(Material("mat_bone", 3, 40 + i));
            held.Add(Material("mat_hide", 2, 30 + i));
        }

        for (var i = 0; i < 6; i++)
        {
            held.Add(Material("mat_hide", 1, 10 + i));
        }

        return held;
    }

    /// <summary>
    /// The plan's order and bounds (§ 8): collect first when goods are reserved; cores and add-ons listed while fewer than 5 are; materials sold to the
    /// vendor, the cheapest unit first, until <see cref="MarketPlan.KeepStacks"/> stacks are left; a buy last.
    /// </summary>
    [Test]
    public void ThePlan_Collects_ListsUpToFive_VendorsTheCheapestDownToItsFloor_AndBuysLast()
    {
        var held = FullInventory();
        var rng = 7u;
        var buy = new BrowseRow(900, 1, "core_melee", MarketCategory.Core, 2, 1, 1600, 1600, 3);
        var plan = MarketPlan.Plan(held, new MarketMine([11, 12, 13], 2), Prices, ref rng, listMaterial: false, buy);

        var steps = plan.Select(c => c.Step).ToList();
        var lists = plan.Where(c => c.Step == MarketStep.List).ToList();
        var vendors = plan.Where(c => c.Step == MarketStep.Vendor).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(steps[0], Is.EqualTo(MarketStep.Collect), "goods reserved, and not collected first");
            Assert.That(plan[0].Listing, Is.Zero, "a collect of one listing, not of all here");
            Assert.That(lists, Has.Count.EqualTo(2), "three listed already: two more make five");
            Assert.That(lists.Select(c => c.Template), Is.EqualTo(new[] { MarketCatalog.TemplateOf("core_armor"), MarketCatalog.TemplateOf("addon_weapon") }));
            Assert.That(lists[1].Quantity, Is.EqualTo(2), "a stack is listed whole");
            Assert.That(held.Count - lists.Count - vendors.Count, Is.EqualTo(MarketPlan.KeepStacks), "not down to the floor");
            Assert.That(vendors.Select(v => Prices.ValueOf(MarketCatalog.TemplateOf("mat_hide") == v.Template ? "mat_hide" : "mat_bone", v.Mk)), Is.Ordered,
                "not the cheapest unit first");
            Assert.That(vendors.Take(6).All(v => v.Template == MarketCatalog.TemplateOf("mat_hide") && v.Mk == 1), Is.True, "not the cheapest unit first");
            Assert.That(vendors.Take(6).Select(v => v.Quantity), Is.Ordered, "of two stacks of one value, not the smaller first");
            Assert.That(steps[^1], Is.EqualTo(MarketStep.Buy), "the buy is not last");
            Assert.That((plan[^1].Listing, plan[^1].UnitPrice), Is.EqualTo((900UL, 1600L)), "the buy is not at the price shown");
            Assert.That(steps.IndexOf(MarketStep.Vendor), Is.GreaterThan(steps.LastIndexOf(MarketStep.List)), "vendored before listing");
        });
    }

    /// <summary>
    /// Cores and add-ons past the five listed go to the vendor too — after every material, the dearer units — when the floor needs their slots: a core kept
    /// is a slot lost to the next quest's drops.
    /// </summary>
    [Test]
    public void CoresPastTheFiveListed_GoToTheVendor_AfterTheMaterials()
    {
        var rng = 7u;
        var held = new List<HeldStack>();
        for (var mk = 1; mk <= 5; mk++)
        {
            held.Add(Core("core_armor", mk));
            held.Add(Core("core_ranged", mk));
        }

        for (var i = 0; i < 4; i++)
        {
            held.Add(Material("mat_hide", 1, 3));
        }

        var plan = MarketPlan.Plan(held, new MarketMine([1, 2, 3, 4, 5], 0), Prices, ref rng, listMaterial: false, null);
        var vendors = plan.Where(c => c.Step == MarketStep.Vendor).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(plan.Where(c => c.Step == MarketStep.List), Is.Empty, "listed past five");
            Assert.That(vendors, Has.Count.EqualTo(held.Count - MarketPlan.KeepStacks), "not down to the floor");
            Assert.That(vendors.Take(4).All(v => v.Template == MarketCatalog.TemplateOf("mat_hide")), Is.True, "a core sold before a material");
            Assert.That(vendors.Skip(4).Select(v => v.Mk), Is.Ordered, "not the cheaper core first");
        });
    }

    /// <summary>
    /// The vendor's payout is the server's, to the credit (<c>MarketValues.VendorPayout</c>: ⌊count × milli × 200 / 10⁶⌋), and a stack it would pay 0 for
    /// is not sent — the server refuses it <c>MarketWorthless</c> (live: 517 in 23 minutes) — the next cheapest is.
    /// </summary>
    [Test]
    public void AStackTheVendorPaysNothingFor_IsNotSold()
    {
        var rng = 7u;
        var held = new List<HeldStack> { Material("mat_hide", 1, 2), Material("mat_hide", 2, 2), Material("mat_bone", 1, 3) };
        for (var i = 0; i < MarketPlan.KeepStacks; i++)
        {
            held.Add(Material("mat_venom", 4, 50));
        }

        var vendors = MarketPlan.Plan(held, MarketMine.None, Prices, ref rng, listMaterial: false, null).Where(c => c.Step == MarketStep.Vendor).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.VendorPayout(2, 1.7d), Is.Zero);
            Assert.That(MarketPlan.VendorPayout(3, 1.7d), Is.EqualTo(1L));
            Assert.That(MarketPlan.VendorPayout(99, 11.4d), Is.EqualTo(225L));
            Assert.That(MarketPlan.VendorPayout(1, 726d), Is.EqualTo(145L));
            Assert.That(vendors.Select(v => (v.Template, v.Mk, v.Quantity)), Is.EqualTo(new[]
            {
                (MarketCatalog.TemplateOf("mat_bone"), 1, 3), (MarketCatalog.TemplateOf("mat_hide"), 2, 2), (MarketCatalog.TemplateOf("mat_venom"), 4, 50),
            }), "a worthless stack sent, or the next cheapest not sold in its place");
        });
    }

    /// <summary>
    /// The sender sends a plan only to the visit it was read for and only while the bot stands at the bazaar: a plan landing during another visit — even
    /// at another bazaar — or a bot walked off drops what is left, counted.
    /// </summary>
    [Test]
    public void ThePlanGoesOutOnlyAtItsOwnBazaarVisit()
    {
        List<MarketCommand> Plan(int n) => Enumerable.Range(0, n).Select(i => MarketCommand.Vendor(8, 1, 5 + i)).ToList();
        var sender = new MarketSender();
        sender.Offer(1, Plan(3));
        Assert.Multiple(() =>
        {
            Assert.That(sender.Busy, Is.True);
            Assert.That(sender.Next(atBazaar: true, visit: 2, tick: 1, spacing: 2), Is.Null, "a plan read for visit 1 sent at visit 2's bazaar");
            Assert.That((sender.Dropped, sender.Busy), Is.EqualTo((3L, false)));
        });

        sender.Offer(3, Plan(3));
        var first = sender.Next(atBazaar: true, visit: 3, tick: 10, spacing: 2);
        Assert.Multiple(() =>
        {
            Assert.That(first?.Quantity, Is.EqualTo(5), "not the plan's first command");
            Assert.That(sender.Next(atBazaar: true, visit: 3, tick: 11, spacing: 2), Is.Null, "faster than the rate");
            Assert.That(sender.Next(atBazaar: true, visit: 3, tick: 12, spacing: 2)?.Quantity, Is.EqualTo(6));
            Assert.That(sender.Next(atBazaar: false, visit: 3, tick: 14, spacing: 2), Is.Null, "sent from the walk away");
            Assert.That((sender.Dropped, sender.Busy), Is.EqualTo((4L, false)));
        });
    }

    /// <summary>A plan for a bot with room, nothing reserved and five listed is empty: it has nothing to do.</summary>
    [Test]
    public void ABotWithRoomAndFiveListed_PlansNothing()
    {
        var rng = 7u;
        var held = new List<HeldStack> { Core("core_armor", 2), Material("mat_hide", 1, 5) };
        Assert.That(MarketPlan.Plan(held, new MarketMine([1, 2, 3, 4, 5], 0), Prices, ref rng, listMaterial: true, null), Is.Empty);
    }

    /// <summary>A core or add-on is listed at its value × U(0.9, 1.3); a full material stack at × U(0.9, 1.1) — never past either, whatever the draw.</summary>
    [Test]
    public void ListPrices_AreTheValueTimesTheirBand()
    {
        var core = new List<long>();
        var material = new List<long>();
        for (var seed = 1u; seed < 400u; seed++)
        {
            var rng = seed * 2246822519u | 1u;
            var held = new List<HeldStack> { Core("core_armor", 2), Material("mat_bone", 3, MarketPlan.StackMax) };
            var plan = MarketPlan.Plan(held, MarketMine.None, Prices, ref rng, listMaterial: true, null);
            core.Add(plan.Single(c => c.Template == MarketCatalog.TemplateOf("core_armor")).UnitPrice);
            material.Add(plan.Single(c => c.Template == MarketCatalog.TemplateOf("mat_bone") && c.Step == MarketStep.List).UnitPrice);
        }

        Assert.Multiple(() =>
        {
            Assert.That(core.Min(), Is.GreaterThanOrEqualTo((long)(1549 * MarketPlan.ListLow)));
            Assert.That(core.Max(), Is.LessThanOrEqualTo((long)System.Math.Ceiling(1549 * MarketPlan.ListHigh)));
            Assert.That(core.Distinct().Count(), Is.GreaterThan(100), "the draw does not spread the prices");
            Assert.That(material.Min(), Is.EqualTo(4L), "a 4.5 cr unit listed under 0.9 times it, rounded");
            Assert.That(material.Max(), Is.EqualTo(5L), "a 4.5 cr unit listed past 1.1 times it, rounded");
        });
    }

    /// <summary>
    /// What the board has not priced yet is priced at the model's value (a fresh board prices nothing held until somebody lists); what has no value at
    /// all — an item the bot cannot name — is neither listed nor bought; the vendor takes materials either way.
    /// </summary>
    [Test]
    public void WhatTheBoardDoesNotPrice_IsPricedAtTheModel_AndWhatHasNoValue_IsNeitherListedNorBought()
    {
        var rng = 7u;
        var held = new List<HeldStack> { Core("core_propulsion", 4), new(0, "unknown", MarketCategory.Core, 2, 1) };
        for (var i = 0; i < 20; i++)
        {
            held.Add(Material("mat_venom", 4, 5));
        }

        var plan = MarketPlan.Plan(held, MarketMine.None, Prices, ref rng, listMaterial: false, null);
        var lists = plan.Where(c => c.Step == MarketStep.List).ToList();
        BrowseRow Row(ulong id, string item) => new(id, 1, item, MarketCategory.Core, 4, 1, 10, 10, 2);
        Assert.Multiple(() =>
        {
            Assert.That(MarketCatalog.ModelValue("core_propulsion", 4), Is.EqualTo(5_950d));
            Assert.That(MarketCatalog.ModelValue("mat_venom", 5), Is.EqualTo(11.4d));
            Assert.That(MarketCatalog.ModelValue("crate", 1), Is.Zero);
            Assert.That(lists.Select(c => c.Template), Is.EqualTo(new[] { MarketCatalog.TemplateOf("core_propulsion") }), "not the core alone");
            Assert.That(lists.Single().UnitPrice, Is.InRange((long)(5_950 * MarketPlan.ListLow), (long)(5_950 * MarketPlan.ListHigh) + 1), "not at the model's value");
            Assert.That(plan.Count(c => c.Step == MarketStep.Vendor), Is.EqualTo(held.Count - 1 - MarketPlan.KeepStacks), "a material was kept");
            Assert.That(MarketPlan.ChooseBuy([Row(1, "unknown")], Prices, 4, 1_000_000, []), Is.Null, "an item with no value was bought");
            Assert.That(MarketPlan.ChooseBuy([Row(1, "unknown"), Row(2, "core_propulsion")], Prices, 4, 1_000_000, [])?.Id, Is.EqualTo(2UL));
        });
    }

    /// <summary>
    /// A buy (§ 8): of its band, a core, an add-on or a material at ≤ 1.2 × its value a unit and ≤ 30 % of its wallet, never its own listing — the best
    /// bargain, the lowest price to value.
    /// </summary>
    [Test]
    public void ABuy_IsTheBestBargainOfItsBand_WithinValueAndWallet_NotItsOwn()
    {
        BrowseRow Row(ulong id, string item, int mk, long unit, int qty = 1) =>
            new(id, 1, item, MarketCatalog.CategoryOf(item.StartsWith("core") ? "core" : item.StartsWith("addon") ? "addon" : "material"), mk, qty, unit,
                unit * qty, 5);

        var rows = new List<BrowseRow>
        {
            Row(1, "mat_hide", 2, 3),                // a material at 1.07 × its 2.8 cr
            Row(2, "core_armor", 3, 100),            // another band
            Row(3, "core_armor", 2, 1900),           // past 1.2 × 1549
            Row(4, "core_armor", 2, 1400),           // its own
            Row(5, "addon_weapon", 2, 1500, 3),      // 4 500 in all: past 30 % of 10 000
            Row(6, "core_ranged", 2, 1700),          // 1.10 × its value
            Row(7, "addon_weapon", 2, 1550),         // the best bargain: 1.0006 × its value
            Row(8, "mat_bone", 2, 4),                // 1.43 × the model's 2.8 cr: past 1.2
        };

        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.ChooseBuy(rows, Prices, 2, 10_000, [4UL])?.Id, Is.EqualTo(7UL));
            Assert.That(MarketPlan.ChooseBuy(rows, Prices, 2, 5_000, [4UL])?.Id, Is.EqualTo(1UL), "past 30 % of a 5 000 cr wallet, the material is all");
            Assert.That(MarketPlan.ChooseBuy(rows, Prices, 2, 5, [4UL]), Is.Null, "past 30 % of a 5 cr wallet");
            Assert.That(MarketPlan.Band(1), Is.EqualTo(1));
            Assert.That(MarketPlan.Band(10), Is.EqualTo(1));
            Assert.That(MarketPlan.Band(11), Is.EqualTo(2));
            Assert.That(MarketPlan.Band(50), Is.EqualTo(5));
        });
    }

    /// <summary>The rush's seller (§ 8): one material unit at 1 cr per listing it is short of its share, within the round's budget.</summary>
    [Test]
    public void ARushSeller_TopsItsListingsUpToItsShare_OneUnitAt1Cr()
    {
        var held = new List<HeldStack> { Core("core_armor", 2), Material("mat_hide", 1, 2), Material("mat_bone", 1, 9) };
        var plan = MarketPlan.RushSell(held, listed: 1, target: 4, budget: 4);
        Assert.Multiple(() =>
        {
            Assert.That(plan, Has.Count.EqualTo(3), "not topped up to its share");
            Assert.That(plan.All(c => c is { Step: MarketStep.List, Quantity: 1, UnitPrice: 1 }), Is.True, "not one unit at 1 cr");
            Assert.That(plan.Count(c => c.Template == MarketCatalog.TemplateOf("mat_hide")), Is.EqualTo(2), "more units listed than the stack held");
            Assert.That(MarketPlan.RushSell(held, listed: 4, target: 4, budget: 4), Is.Empty, "listed past its share");
            Assert.That(MarketPlan.RushSell(held, listed: 0, target: 20, budget: 4), Has.Count.EqualTo(4), "past the round's budget");
        });
    }

    /// <summary>The rush's buyer: the three cheapest listings of the page it has not tried, never its own; a listing is tried once.</summary>
    [Test]
    public void ARushBuyer_TriesTheThreeCheapestUntried_NotItsOwn()
    {
        var rows = Enumerable.Range(1, 8).Select(i => new BrowseRow((ulong)i, 1, "mat_hide", MarketCategory.Material, 1, 1, i > 6 ? 2 : 1, 1, 9)).Reverse().ToList();
        var tried = new HashSet<ulong>();
        var first = MarketPlan.RushBuy(rows, tried, [2UL], 3);
        var second = MarketPlan.RushBuy(rows, tried, [2UL], 3);
        Assert.Multiple(() =>
        {
            Assert.That(first.Select(c => c.Listing), Is.EqualTo(new[] { 1UL, 3UL, 4UL }), "not the cheapest three, or its own among them");
            Assert.That(second.Select(c => c.Listing), Is.EqualTo(new[] { 5UL, 6UL, 7UL }), "a listing tried again");
            Assert.That(first.All(c => c.UnitPrice == 1), Is.True, "not at the price shown");
        });
    }

    /// <summary>
    /// The reads (§ 5): <c>/mine</c>, <c>/prices</c>, a browse page; ids as strings past a JavaScript number, or numbers; a template from the answer
    /// when it carries one, else the server's own id for the asset.
    /// </summary>
    [Test]
    public void TheMarketsAnswers_AreReadAsTheServerWritesThem()
    {
        var mine = MarketMine.Parse("""
            {"listed":[{"id":"18446744073709551000","city":1,"item":"core_armor","name":"Armor Core","mk":2,"qty":1,"unit":1600}],
             "reserved":[{"id":"7","city":2,"item":"mat_hide","name":"Hide","mk":1,"qty":5,"unit":2,"state":"sold","closedTick":9}],
             "caps":{"listings":20,"reserved":20}}
            """);
        var prices = MarketPrices.Parse("""
            {"prices":[{"item":"core_armor","mk":2,"value":1549,"vendor":309800,"listed":3,"lowest":1500,"median":1600,"sales":4},
                       {"item":"mat_hide","mk":1,"value":1.7,"vendor":340,"listed":0,"lowest":0,"median":0,"sales":0}]}
            """);
        var page = MarketJson.Browse("""
            {"v":4,"city":1,"total":2,"page":1,"pages":1,"listings":[
              {"id":"31","city":1,"item":"addon_armor","name":"Armor Enhancement","category":"addon","mk":3,"qty":2,"unit":2700,"total":5400,
               "seller":"12","sellerName":"bot000012","ageS":40},
              {"id":32,"city":1,"item":"mat_bone","name":"Bone","category":"material","mk":1,"qty":99,"unit":2,"total":198,"seller":13,
               "sellerName":"x","ageS":2}]}
            """);
        Assert.Multiple(() =>
        {
            Assert.That(mine.ListedIds, Is.EqualTo(new[] { 18446744073709551000UL }));
            Assert.That((mine.Listed, mine.Reserved, mine.Empty), Is.EqualTo((1, 1, false)));
            Assert.That(MarketMine.Parse("""{"listed":[],"reserved":[],"caps":{}}""").Empty, Is.True);
            Assert.That(prices.ValueOf("core_armor", 2), Is.EqualTo(1549d));
            Assert.That(prices.ValueOf("mat_hide", 1), Is.EqualTo(1.7d));
            Assert.That(prices.ValueOf("mat_hide", 2), Is.EqualTo(2.8d), "an (item, Mk) the answer did not name is not at the model's value");
            Assert.That(page, Is.EqualTo(new[]
            {
                new BrowseRow(31, 1, "addon_armor", MarketCategory.Addon, 3, 2, 2700, 5400, 12),
                new BrowseRow(32, 1, "mat_bone", MarketCategory.Material, 1, 99, 2, 198, 13),
            }));
            Assert.That(MarketJson.Credits("""{"open":true,"credits":1234}"""), Is.EqualTo(1234L));
            Assert.That(MarketCatalog.TemplateOf("mat_ore"), Is.EqualTo((ushort)1));
            Assert.That(MarketCatalog.TemplateOf("core_armor"), Is.EqualTo((ushort)14));
            Assert.That(MarketCatalog.TemplateOf("addon_additive"), Is.EqualTo((ushort)19));
            Assert.That(MarketCatalog.TemplateOf("crate"), Is.Zero, "an item the bot cannot trade has a template");
        });
    }

    /// <summary>An inventory read (§ 8): every stack held, with its template. A buy collected reaches the bot as a notice, never as a ledger row.</summary>
    [Test]
    public void AnInventoryRead_GivesTheStacks()
    {
        const string answer = """
            {"open":true,"slots":30,"items":[{"id":"9","slot":1,"item":"mat_hide","name":"Hide","category":"material","mk":1,"count":4},
            {"id":"10","slot":2,"template":14,"item":"core_armor","name":"Armor Core","category":"core","mk":2,"count":1}],"lost":[]}
            """;

        var held = new List<HeldStack>();
        var view = InventoryView.Parse(answer, InventoryView.None, count: true, out _, held);
        Assert.Multiple(() =>
        {
            Assert.That(held, Is.EqualTo(new[]
            {
                new HeldStack(8, "mat_hide", MarketCategory.Material, 1, 4),
                new HeldStack(14, "core_armor", MarketCategory.Core, 2, 1),
            }));
            Assert.That(view.Stacks, Is.EqualTo(2));
            Assert.That(MarketCatalog.AssetOf(14), Is.EqualTo("core_armor"));
            Assert.That(MarketCatalog.AssetOf(0), Is.EqualTo("unknown"));
        });
    }

    /// <summary>
    /// The bazaar's refusals are the server's (§ 3.1): <c>AckReasons.FirstApplicationReason</c> + 23 … + 35, 151-163, Sold the third, and the
    /// reused NotInWorld (142) — counted only when it answers a market command. The bot's copy
    /// must name the same numbers, or it counts the wrong races.
    /// </summary>
    [Test]
    public void TheRefusalsTheBotCounts_AreTheDesignsNumbers()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BotSwarm.MarketFirstReason, Is.EqualTo(151));
            Assert.That(BotSwarm.MarketSoldReason, Is.EqualTo(153));
            Assert.That(BotSwarm.MarketReasonNames, Has.Length.EqualTo(BotSwarm.MarketRefusals));
            Assert.That(BotSwarm.MarketReasonNames[^1], Is.EqualTo("notInWorld"));
            Assert.That(BotSwarm.MarketRefusalSlot(151, false), Is.Zero);
            Assert.That(BotSwarm.MarketRefusalSlot(163, false), Is.EqualTo(12));
            Assert.That(BotSwarm.MarketRefusalSlot(142, true), Is.EqualTo(13), "a 142 answering a market command is not counted as notInWorld");
            Assert.That(BotSwarm.MarketRefusalSlot(142, false), Is.EqualTo(-1), "a 142 answering a mission's command is counted as the market's");
            Assert.That(BotSwarm.MarketRefusalSlot(150, true), Is.EqualTo(-1));
            Assert.That(BotSwarm.MarketRefusalSlot(164, true), Is.EqualTo(-1));
            Assert.That(BotSwarmOptions.RegionOf("Port Sable"), Is.EqualTo(1));
            Assert.That(BotSwarmOptions.RegionOf("Nowhere"), Is.Zero);
            Assert.That(new BotSwarmOptions { Count = 1000 }.MarketRushSellers, Is.EqualTo(100));
            Assert.That(new BotSwarmOptions { Count = 5 }.MarketRushSellers, Is.EqualTo(1));
        });
    }
}

/// <summary>
/// The bots' bazaar commands against the server's own catalog (bazaar.md § 3.1): each one a bot sends is declared, for a player, and encodes with the
/// fields the bot names — a misspelt field would throw at every send, and a fleet counting faults instead of trades looks like a quiet market.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class BotMarketCatalogChecks : ServedChecks
{
    [Test]
    [CancelAfter(60_000)]
    public void EveryCommandABotSends_IsDeclared_AndEncodesAgainstTheServersCatalog()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Assert.That(
            AccountStore.TrySignup(sim.Dbe, "bot000007", "a-bot-password-1", out var bot, bot: true),
            Is.EqualTo(AccountStore.SignupOutcome.Created));
        var link = Harness.Connect(TatooineReplication.PlayerKind, bot);
        var plan = link.Plan;

        MarketCommand[] sent =
        [
            MarketCommand.Collect(),
            MarketCommand.Collect(18_446_744_073_709_551_000UL),
            MarketCommand.List(MarketCatalog.TemplateOf("core_armor"), 2, 1, 1_000_000),
            MarketCommand.Vendor(MarketCatalog.TemplateOf("mat_hide"), 1, 99),
            MarketCommand.Buy(42UL, 1549),
        ];
        Assert.Multiple(() =>
        {
            foreach (var c in sent)
            {
                var (name, values) = BotSwarm.Encode(c);
                var command = plan.CommandByName(name);
                Assert.That(command, Is.Not.Null, $"{name} is not declared");
                if (command == null)
                {
                    continue;
                }

                Assert.That(
                    () =>
                    {
                        var writer = new WireWriter(new byte[256]);
                        CommandsMessage.Write(ref writer, clientTick: 1, [(command, (ushort)1, values)]);
                    },
                    Throws.Nothing,
                    $"{c.Step} does not encode as {name}");
            }
        });
    }
}

/// <summary>
/// The bots' trade against the real desk (bazaar.md § 8, the in-process half of BZ-16): bots with full inventories at a bazaar read the server's own answers
/// (<c>/swg/inventory</c>, <c>/mine</c>, <c>/prices</c>, a browse page) with the bots' parsers, plan with <see cref="MarketPlan"/> and send the plan as
/// <see cref="BotSwarm.Encode"/> encodes it, at the commands' rate. Nothing is refused, the vendor takes them down to their floor, the cores are listed at
/// value × U(0.9, 1.3), a rush buyer wins a rush seller's 1 cr listing and sees it collected, and the three audits stay OK.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class BotMarketDeskChecks : ServedChecks
{
    private sealed record Trader(FakeLink Link, EntityId Character)
    {
        public long Key => unchecked((long)Character.RawValue);
    }

    private Trader LogIn(TatooineSim sim, ulong account, (float X, float Z) at, long bindings)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        Worlds.Park(sim, link.Session, at.X, at.Z);
        Assert.That(CharacterStore.TryFind(sim.Dbe, TestAccounts.For(sim.Dbe, account), out var character, out _), Is.True, "the account has no character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return new Trader(link, character);
    }

    /// <summary>Two cores, then materials (Mk I-IV, five units each) until the inventory's 30 slots are full: a bot back from a day's hunting.</summary>
    private static void Fill(TatooineSim sim, Trader t)
    {
        sim.Bridge.StageTransfer(ItemLedger.World, t.Key, ItemCatalog.CoreArmor, 2, 1, ItemMoveKind.Kill, 1);
        sim.Bridge.StageTransfer(ItemLedger.World, t.Key, ItemCatalog.AddonWeapon, 2, 1, ItemMoveKind.Kill, 1);
        var n = 2;
        for (var template = ItemCatalog.Metal; template <= ItemCatalog.Venom && n < ItemLedger.Slots; template++)
        {
            for (byte mk = 1; mk <= 4 && n < ItemLedger.Slots; mk++, n++)
            {
                sim.Bridge.StageTransfer(ItemLedger.World, t.Key, template, mk, 5, ItemMoveKind.Kill, 1);
            }
        }
    }

    /// <summary>What the bot reads before it plans: its stacks, its board, the prices — the server's own answers through the bots' parsers.</summary>
    private static (List<HeldStack> Held, MarketMine Mine, MarketPrices Prices) Read(TatooineSim sim, Trader t)
    {
        var held = new List<HeldStack>();
        InventoryView.Parse(ItemEndpoints.InventoryJson(sim.Dbe, t.Character), InventoryView.None, false, out _, held);
        var board = sim.Bridge.MarketBoard;
        return (held, MarketMine.Parse(MarketEndpoints.MineJson(sim.Dbe, t.Character, board)), MarketPrices.Parse(MarketEndpoints.PricesJson(board)));
    }

    /// <summary>Sends a plan as a bot does: each command as <see cref="BotSwarm.Encode"/> builds it, two a second.</summary>
    private void Send(Trader t, List<MarketCommand> plan)
    {
        foreach (var c in plan)
        {
            var (name, values) = BotSwarm.Encode(c);
            t.Link.SendCommand(name, values);
            Harness.Ticks((Worlds.ServedHz / 2) + 1);
        }

        Harness.Ticks(Worlds.ServedHz);
    }

    [Test]
    [CancelAfter(240_000)]
    public void BotsWithFullInventories_ListVendorAndBuy_AgainstTheRealDesk_AndTheAuditsStayOk()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Assert.That(sim.Index.Bazaars, Is.Not.Empty, "the premise: the world has a bazaar");
        var home = sim.Index.Bazaars[0];
        var sellers = new[]
        {
            LogIn(sim, 0x9100, (home.X + 2.5f, home.Z), 1),
            LogIn(sim, 0x9101, (home.X + 2.5f, home.Z + 0.6f), 2),
        };
        var buyer = LogIn(sim, 0x9102, (home.X + 2.5f, home.Z - 0.6f), 3);
        foreach (var s in sellers)
        {
            Fill(sim, s);
        }

        SessionHarness.Until(() => sellers.All(s => Items.Stacks(sim, s.Character).Count == ItemLedger.Slots), "the inventories to be full");
        Harness.Ticks(2);

        // Two visits each, as the brain makes them. The board has never been published with these cores on it — it prices what is held only when a
        // listing changes — so the first visit lists them at the model's value; the second has nothing left to list or sell.
        var rng = 7u;
        var vendored = 0;
        var listed = new List<MarketCommand>();
        for (var visit = 0; visit < 2; visit++)
        {
            foreach (var s in sellers)
            {
                var (held, mine, prices) = Read(sim, s);
                var plan = MarketPlan.Plan(held, mine, prices, ref rng, listMaterial: false, buy: null);
                vendored += plan.Count(c => c.Step == MarketStep.Vendor);
                // Each listing kept with its seller in the (unused) listing field, to find it on the board.
                listed.AddRange(plan.Where(c => c.Step == MarketStep.List).Select(c => c with { Listing = (ulong)s.Key }));
                Send(s, plan);
            }
        }

        SessionHarness.Until(() => Market.Listings(sim).Count >= listed.Count, "the listings");
        var (_, _, valued) = Read(sim, sellers[0]);

        // The rush: a seller keeps one 1 cr listing up, a buyer reads the city's first page and races for the cheapest.
        var rushSeller = sellers[1];
        var (rushHeld, rushMine, _) = Read(sim, rushSeller);
        Send(rushSeller, MarketPlan.RushSell(rushHeld, rushMine.Listed, rushMine.Listed + 1, 4));
        var query = new MarketQuery { City = home.Region, Realm = home.Realm, Sort = "price" };
        SessionHarness.Until(
            () => MarketJson.Browse(MarketEndpoints.BrowseJson(sim.Bridge.MarketBoard, query)).Any(r => r.Unit == 1),
            "the 1 cr listing on the board");
        var page = MarketJson.Browse(MarketEndpoints.BrowseJson(sim.Bridge.MarketBoard, query));
        var buys = MarketPlan.RushBuy(page, [], [], 1);
        Send(buyer, buys);

        // The buy collected, as the bot hears it: a notice to its session, never a ledger row (2026-10-29).
        var won = new List<WonListing>();
        SessionHarness.Until(
            () =>
            {
                won = Notices.Heard(buyer.Link)
                    .FindAll(n => n.Items && n.Incoming && n.Kind == "buy")
                    .ConvertAll(n => new WonListing((ulong)n.Ref, n.Template, MarketCatalog.AssetOf(n.Template), n.Mk, n.Count));
                return won.Count > 0;
            },
            "the buy to be collected");

        var (ok, why) = Market.Audits(sim);
        var rows = Ledgers.Rows(sim);
        Assert.Multiple(() =>
        {
            foreach (var t in sellers.Append(buyer))
            {
                Assert.That(Ledgers.Refusals(t.Link), Is.Empty, $"character {t.Key}'s commands were refused");
            }

            foreach (var s in sellers)
            {
                Assert.That(Items.Stacks(sim, s.Character).Count, Is.LessThanOrEqualTo(MarketPlan.KeepStacks), "the vendor did not take a bot to its floor");
            }

            Assert.That(vendored, Is.GreaterThanOrEqualTo(2 * (ItemLedger.Slots - MarketPlan.KeepStacks - 2)), "the premise: the plans vendored");
            Assert.That(rows.Count(r => r.Kind == TransferKind.Vendor), Is.EqualTo(vendored), "not one vendor payment a sale");
            Assert.That(listed, Has.Count.EqualTo(4), "the cores and add-ons were not all listed");
            foreach (var c in listed)
            {
                var item = MarketCatalog.TemplateOf("core_armor") == c.Template ? "core_armor" : "addon_weapon";
                var value = valued.ValueOf(item, c.Mk);
                Assert.That(c.UnitPrice, Is.InRange((long)(value * MarketPlan.ListLow), (long)System.Math.Ceiling(value * MarketPlan.ListHigh)), $"{item} priced off its value");
                Assert.That(Market.Listings(sim).Values.Any(l => l.Seller == (long)c.Listing && l.Template == c.Template && l.UnitPrice == c.UnitPrice), Is.True,
                    $"{item} at {c.UnitPrice} is not on the board");
            }

            Assert.That(buys, Has.Count.EqualTo(1));
            Assert.That(won.Select(w => w.Listing), Is.EqualTo(buys.Select(b => b.Listing)), "the buy collected is not the listing bought");
            Assert.That(rows.Count(r => r.Kind == TransferKind.Sale && (ulong)r.Ref == buys[0].Listing), Is.EqualTo(1), "not one sale of it");
            Assert.That(ok, Is.True, why);
        });
    }

    /// <summary>
    /// BZ-16's measure in process: drops lost to a full inventory with the bots' market and without it. Three bots hunt in quests — ten kills each, through
    /// the real loot path, with no bazaar in between — and between two quests a trading bot does what its brain does: at
    /// <see cref="BotBrain.MarketStacks"/> stacks or more, the bazaar, its plan read and sent as a bot sends it. The market must lose at most a quarter of
    /// what no market loses, and the audits stay OK.
    /// </summary>
    /// <remarks>
    /// Live, before this tuning (2026-10-27): ~4 800 units lost a minute with the market against ~5 400 without — 11 % fewer — two thirds of the bots
    /// questing at any time, at a trigger of 24 stacks and a floor of 18.
    /// </remarks>
    [Test]
    [CancelAfter(300_000)]
    public void TheMarket_CutsTheDropsLostToAFullInventory()
    {
        var without = LostOverQuests(market: false, Dir, out _);
        Harness?.Release();
        Harness = null;
        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        var dir = Worlds.NewDirectory();
        try
        {
            var with = LostOverQuests(market: true, dir, out var audits);
            TestContext.Progress.WriteLine($"lost to a full inventory over {Quests} quests of {KillsPerQuest} kills, 3 bots: {without} without the market, {with} with it");
            Assert.Multiple(() =>
            {
                Assert.That(without, Is.GreaterThan(40), "the premise: without a market, full inventories lose drops");
                Assert.That(with * 4, Is.LessThanOrEqualTo(without), $"the market lost {with} units against {without} without it");
                Assert.That(audits.Ok, Is.True, audits.Why);
            });
        }
        finally
        {
            Harness?.Release();
            Harness = null;
            Worlds.Delete(dir);
        }
    }

    private const int Quests = 8;
    private const int KillsPerQuest = 16;

    private long LostOverQuests(bool market, string dir, out (bool Ok, string Why) audits)
    {
        var config = Worlds.Served(dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        var home = sim.Index.Bazaars[0];
        var bots = new List<(Trader T, EntityId Body, (float X, float Z) At)>();
        for (var i = 0; i < 3; i++)
        {
            var at = (home.X + 2.5f, home.Z + (i * 0.6f));
            var t = LogIn(sim, 0x9200UL + (ulong)i, at, i + 1);
            bots.Add((t, TatooineReplication.ControlledBy(sim.Dbe, t.Link.Session), at));
        }

        var victim = 0xB0000UL;
        var rng = 11u;
        for (var quest = 0; quest < Quests; quest++)
        {
            // The quest: kills of every wild species, levels across the bands, so drops spread over many (template, Mk) — and no bazaar.
            var drops = sim.Bridge.ItemDrops + sim.Bridge.ItemsLost;
            foreach (var (_, body, at) in bots)
            {
                for (var k = 0; k < KillsPerQuest; k++, victim++)
                {
                    var kill = Items.Kill(body, victim, at.X, at.Z, (byte)(victim % CreatureTemplates.WildCount));
                    kill.VictimLevel = (byte)(1 + (victim * 7 % 40));
                    sim.Bridge.StageKill(kill);
                }
            }

            SessionHarness.Until(() => sim.Bridge.LootCommits > 0 && sim.Bridge.ItemDrops + sim.Bridge.ItemsLost > drops, "the quest's kills to be looted");
            Harness.Ticks(4);
            if (!market)
            {
                continue;
            }

            // Between quests, the brain's choice: the bazaar at MarketStacks or more. The bots' plans go out together, two commands a second each.
            var plans = new List<(Trader T, List<MarketCommand> Plan)>();
            foreach (var (t, _, _) in bots)
            {
                var (held, mine, prices) = Read(sim, t);
                if (held.Count >= BotBrain.MarketStacks)
                {
                    plans.Add((t, MarketPlan.Plan(held, mine, prices, ref rng, listMaterial: false, buy: null)));
                }
            }

            for (var i = 0; plans.Any(p => i < p.Plan.Count); i++)
            {
                foreach (var (t, plan) in plans)
                {
                    if (i < plan.Count)
                    {
                        var (name, values) = BotSwarm.Encode(plan[i]);
                        t.Link.SendCommand(name, values);
                    }
                }

                Harness.Ticks((Worlds.ServedHz / 2) + 1);
            }

            Harness.Ticks(Worlds.ServedHz);
        }

        audits = Market.Audits(sim);
        return sim.Bridge.ItemsLost;
    }
}
