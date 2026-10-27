using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
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
    /// vendor, the cheapest unit first, until 18 stacks are left; a buy last.
    /// </summary>
    [Test]
    public void ThePlan_Collects_ListsUpToFive_VendorsTheCheapestDownTo18_AndBuysLast()
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
            Assert.That(vendors.All(v => v.Template == MarketCatalog.TemplateOf("mat_hide") && v.Mk == 1), Is.True, "not the cheapest unit first");
            Assert.That(vendors.Select(v => v.Quantity), Is.Ordered, "of two stacks of one value, not the smaller first");
            Assert.That(steps[^1], Is.EqualTo(MarketStep.Buy), "the buy is not last");
            Assert.That((plan[^1].Listing, plan[^1].UnitPrice), Is.EqualTo((900UL, 1600L)), "the buy is not at the price shown");
            Assert.That(steps.IndexOf(MarketStep.Vendor), Is.GreaterThan(steps.LastIndexOf(MarketStep.List)), "vendored before listing");
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

    /// <summary>A core or add-on is listed at its value × U(0.9, 1.3); a full material stack at × U(1.0, 1.4) — never past either, whatever the draw.</summary>
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
            Assert.That(material.Min(), Is.EqualTo(5L), "a 4.5 cr unit listed under its value, rounded");
            Assert.That(material.Max(), Is.EqualTo(6L), "a 4.5 cr unit listed past 1.4 times it, rounded");
        });
    }

    /// <summary>A bot does not invent a price: what the prices do not value is neither listed nor bought; the vendor still takes an unvalued material.</summary>
    [Test]
    public void WhatIsNotPriced_IsNeitherListedNorBought_ButIsVendored()
    {
        var rng = 7u;
        var held = new List<HeldStack> { Core("core_propulsion", 4) };
        for (var i = 0; i < 20; i++)
        {
            held.Add(Material("mat_venom", 4, 5));
        }

        var plan = MarketPlan.Plan(held, MarketMine.None, Prices, ref rng, listMaterial: false, null);
        var row = new BrowseRow(1, 1, "core_propulsion", MarketCategory.Core, 4, 1, 10, 10, 2);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Where(c => c.Step == MarketStep.List), Is.Empty, "an unpriced core was listed");
            Assert.That(plan.Count(c => c.Step == MarketStep.Vendor), Is.EqualTo(held.Count - MarketPlan.KeepStacks), "an unpriced material was kept");
            Assert.That(MarketPlan.ChooseBuy([row], Prices, 4, 1_000_000, []), Is.Null, "an unpriced core was bought");
        });
    }

    /// <summary>
    /// A buy (§ 8): the cheapest core or add-on of its band, at ≤ 1.1 × its value a unit and ≤ 30 % of its wallet, never its own listing, never a
    /// material.
    /// </summary>
    [Test]
    public void ABuy_IsTheCheapestOfItsBand_WithinValueAndWallet_NotItsOwn()
    {
        BrowseRow Row(ulong id, string item, int mk, long unit, int qty = 1) =>
            new(id, 1, item, MarketCatalog.CategoryOf(item.StartsWith("core") ? "core" : item.StartsWith("addon") ? "addon" : "material"), mk, qty, unit,
                unit * qty, 5);

        var rows = new List<BrowseRow>
        {
            Row(1, "mat_hide", 2, 1),                // a material
            Row(2, "core_armor", 3, 100),            // another band
            Row(3, "core_armor", 2, 1800),           // past 1.1 × 1549
            Row(4, "core_armor", 2, 1400),           // its own
            Row(5, "addon_weapon", 2, 1500, 3),      // 4 500 in all: past 30 % of 10 000
            Row(6, "core_ranged", 2, 1700),          // the one
            Row(7, "addon_weapon", 2, 1703),         // dearer
        };

        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.ChooseBuy(rows, Prices, 2, 10_000, [4UL])?.Id, Is.EqualTo(6UL));
            Assert.That(MarketPlan.ChooseBuy(rows, Prices, 2, 5_000, [4UL]), Is.Null, "past 30 % of a 5 000 cr wallet");
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
            Assert.That(prices.ValueOf("mat_hide", 2), Is.Zero, "an (item, Mk) the answer did not name has a value");
            Assert.That(page, Is.EqualTo(new[]
            {
                new BrowseRow(31, 1, "addon_armor", MarketCategory.Addon, 3, 2, 2700, 5400, 12),
                new BrowseRow(32, 1, "mat_bone", MarketCategory.Material, 1, 99, 2, 198, 13),
            }));
            Assert.That(MarketJson.Credits("""{"open":true,"credits":1234,"transfers":[]}"""), Is.EqualTo(1234L));
            Assert.That(MarketCatalog.TemplateOf("mat_ore"), Is.EqualTo((ushort)1));
            Assert.That(MarketCatalog.TemplateOf("core_armor"), Is.EqualTo((ushort)14));
            Assert.That(MarketCatalog.TemplateOf("addon_additive"), Is.EqualTo((ushort)19));
            Assert.That(MarketCatalog.TemplateOf("crate"), Is.Zero, "an item the bot cannot trade has a template");
        });
    }

    /// <summary>
    /// An inventory read (§ 8): every stack held, with its template; and each bazaar buy collected — a <c>buy</c> row into the bot — once, with its
    /// listing, and never on the first read, which only learns what was there.
    /// </summary>
    [Test]
    public void AnInventoryRead_GivesTheStacks_AndEachBuyCollectedOnce()
    {
        const string first = """
            {"open":true,"slots":30,"items":[{"id":"9","slot":1,"item":"mat_hide","name":"Hide","category":"material","mk":1,"count":4}],
            "recent":[{"seq":"5","tick":10,"kind":"buy","item":"mat_hide","name":"Hide","mk":1,"count":4,"ref":"70","dir":"in"}],"lost":[]}
            """;
        const string second = """
            {"open":true,"slots":30,"items":[{"id":"9","slot":1,"item":"mat_hide","name":"Hide","category":"material","mk":1,"count":4},
            {"id":"10","slot":2,"template":14,"item":"core_armor","name":"Armor Core","category":"core","mk":2,"count":1}],
            "recent":[{"seq":"8","tick":20,"kind":"list","item":"mat_bone","name":"Bone","mk":1,"count":1,"ref":"80","dir":"out"},
                      {"seq":"7","tick":12,"kind":"buy","item":"core_armor","name":"Armor Core","mk":2,"count":1,"ref":"71","dir":"in"},
                      {"seq":"5","tick":10,"kind":"buy","item":"mat_hide","name":"Hide","mk":1,"count":4,"ref":"70","dir":"in"}],"lost":[]}
            """;

        var held = new List<HeldStack>();
        var won = new List<WonListing>();
        var view = InventoryView.Parse(first, InventoryView.None, count: false, out _, out _, held, won);
        Assert.That(won, Is.Empty, "the first read counted a buy that was already there");

        held.Clear();
        view = InventoryView.Parse(second, view, count: true, out _, out _, held, won);
        Assert.Multiple(() =>
        {
            Assert.That(won, Is.EqualTo(new[] { new WonListing(71, 14, "core_armor", 2, 1) }), "not the one buy collected since, with its listing");
            Assert.That(held, Is.EqualTo(new[]
            {
                new HeldStack(8, "mat_hide", MarketCategory.Material, 1, 4),
                new HeldStack(14, "core_armor", MarketCategory.Core, 2, 1),
            }));
            Assert.That(view.Stacks, Is.EqualTo(2));
        });

        won.Clear();
        InventoryView.Parse(second, view, count: true, out _, out _, null, won);
        Assert.That(won, Is.Empty, "an answer read twice counted its buy twice");
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
