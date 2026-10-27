using System;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SwgTatooine.Server;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary><c>GET /swg/stats</c>: the demo's own numbers, with their rates, and who is playing where.</summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class DemoStatsChecks : ServedChecks
{
    [Test]
    public void TheRateWindow_IsTheCounterOverTheWindow()
    {
        var window = new RateWindow(2, 60);
        for (var s = 0; s <= 70; s++)
        {
            window.Sample(s, [s * 10L, s * s]);
        }

        Span<double> r10 = stackalloc double[2];
        window.Rates(10, r10);
        var linear = r10[0];
        var square = r10[1];
        Assert.Multiple(() =>
        {
            Assert.That(linear, Is.EqualTo(10d).Within(1e-9), "10 a second over the last 10 s");
            Assert.That(square, Is.EqualTo(((70d * 70d) - (60d * 60d)) / 10d).Within(1e-9), "the last 10 s of a square, not its whole run");
        });

        var fresh = new RateWindow(1, 60);
        Span<double> none = stackalloc double[1];
        fresh.Rates(10, none);
        var empty = none[0];
        Assert.That(empty, Is.Zero, "a rate from fewer than two samples");
    }

    [Test]
    [CancelAfter(60_000)]
    public void TheDemoStats_AreJson_AndCountWhoPlays()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x7301));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar");
        Harness.Ticks(Worlds.ServedHz * 3);

        using var doc = JsonDocument.Parse(sim.Bridge.DemoStatsJson());
        var root = doc.RootElement;
        var population = root.GetProperty("population");
        Assert.Multiple(() =>
        {
            Assert.That(population.GetProperty("people").GetInt32(), Is.EqualTo(1), "the one person logged in");
            Assert.That(population.GetProperty("bots").GetInt32(), Is.Zero);
            Assert.That(population.GetProperty("crowd").GetInt32(), Is.GreaterThan(0), "the scripted crowd");
            Assert.That(population.GetProperty("botsByCity").EnumerateObject().Count(), Is.EqualTo(sim.Index.Cities.Count));
            Assert.That(root.GetProperty("economy").GetProperty("walletsOpened").GetInt64(), Is.EqualTo(1));
            Assert.That(root.GetProperty("economy").GetProperty("transfersPerSec").GetProperty("60s").GetDouble(), Is.GreaterThan(0d), "the grant shows in the rate");
            Assert.That(root.GetProperty("missions").GetProperty("offers").GetInt32(), Is.GreaterThan(0));
            Assert.That(root.GetProperty("crowd").TryGetProperty("lairHitsPerSec", out _), Is.True);
        });

        // Content levels (content-levels.md § Monitoring): the director's walk counts the standing lairs by band — every one of them — and the ones the
        // crowd may fight, which the fringes fill.
        var standing = 0;
        var belowCrowd = 0;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
            {
                var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var lair = spawners[System.Numerics.BitOperations.TrailingZeroCount(bits)];
                    standing++;
                    belowCrowd += lair.Level <= TatooineData.CrowdLevel && lair.Mission.IsNull ? 1 : 0;
                }
            }
        }

        var levels = root.GetProperty("levels");
        var byBand = levels.GetProperty("lairsByBand").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(byBand, Has.Length.EqualTo(5));
            Assert.That(byBand.Sum(), Is.EqualTo(standing).Within(3), "the bands do not count the lairs standing");
            Assert.That(byBand[0], Is.GreaterThan(0), "no lair in band I: the fringes are empty");
            Assert.That(levels.GetProperty("creaturesByBand").GetArrayLength(), Is.EqualTo(5));
            Assert.That(levels.GetProperty("lairsBelowCrowdLevel").GetInt32(), Is.EqualTo(belowCrowd).Within(3).And.GreaterThan(0));
            Assert.That(levels.GetProperty("crowdObjectiveLevelMax").GetInt32(), Is.InRange(0, TatooineData.CrowdLevel));
        });
    }

    /// <summary>CH-11: <c>/swg/stats</c> carries the progression section: XP by source, the gates, the bands, XP per character-hour against the model's.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void TheStats_CarryTheProgressionSection()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x7311));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar");
        Harness.Ticks(Worlds.ServedHz * 3);

        using var doc = JsonDocument.Parse(sim.Bridge.DemoStatsJson());
        var progression = doc.RootElement.GetProperty("progression");
        Assert.Multiple(() =>
        {
            foreach (var key in new[] { "levelUps", "killXp", "missionXp", "greyKills", "worthlessKills", "noCharacterKills", "crowdKills", "noKillerKills",
                         "missionLairKills", "killsDrained", "commitsFailed", "killXpOverflow", "killLootOverflow", "progressOverflow", "xpRatePercent",
                         "modelXpPerCharHour" })
            {
                Assert.That(progression.TryGetProperty(key, out _), Is.True, key);
            }

            var bands = progression.GetProperty("bands");
            Assert.That(bands.GetProperty("people").EnumerateArray().Select(b => b.GetInt32()), Is.EqualTo(new[] { 1, 0, 0, 0, 0 }), "one person, CL 1");
            Assert.That(bands.GetProperty("bots").EnumerateArray().Sum(b => b.GetInt32()), Is.Zero);
            Assert.That(progression.GetProperty("meanCl").GetProperty("people").GetDouble(), Is.EqualTo(1d));
            Assert.That(progression.GetProperty("modelXpPerCharHour").GetDouble(), Is.EqualTo(1200d), "1 200 × mean CL 1");
            Assert.That(progression.GetProperty("xpPerCharHour").TryGetProperty("60s", out _), Is.True);
            Assert.That(progression.GetProperty("levelUpsPerMin").TryGetProperty("10s", out _), Is.True);
            Assert.That(progression.GetProperty("xpRatePercent").GetInt32(), Is.EqualTo(100));
            Assert.That(progression.GetProperty("progressOverflow").GetInt64(), Is.Zero);
        });
    }

    /// <summary>IL-13: the <c>items</c> section counts the rows, the drops and the units minted and held by template and Mk, as the rows say.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheItemsSection_CountsUnitsByTemplateAndMk()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x7302));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar");
        var character = EntityId.Null;
        SessionHarness.Until(() => SwgTatooine.Server.Accounts.CharacterStore.TryFind(sim.Dbe, EntityId.FromRawValue(0x7302), out character, out _),
            "the character to be committed");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        var owner = (long)character.RawValue;
        sim.Bridge.StageTransfer(ItemLedger.World, owner, ItemCatalog.Hide, 2, 7, ItemMoveKind.Kill, 1);
        sim.Bridge.StageTransfer(ItemLedger.World, owner, ItemCatalog.Bone, 1, 3, ItemMoveKind.Kill, 1);
        sim.Bridge.StageTransfer(ItemLedger.World, owner, ItemCatalog.Hide, 2, 2, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Items.Rows(sim).Count == 3, "the three rows");
        Harness.Ticks(Worlds.ServedHz * 2);

        using var doc = JsonDocument.Parse(sim.Bridge.DemoStatsJson());
        var items = doc.RootElement.GetProperty("items");
        Assert.Multiple(() =>
        {
            Assert.That(items.GetProperty("rows").GetInt64(), Is.EqualTo(3));
            Assert.That(items.GetProperty("units").GetInt64(), Is.EqualTo(12));
            Assert.That(items.GetProperty("stacks").GetInt64(), Is.EqualTo(2));
            Assert.That(items.GetProperty("minted").GetProperty("mat_hide").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(new long[] { 0, 9, 0, 0, 0 }));
            Assert.That(items.GetProperty("held").GetProperty("mat_hide").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(new long[] { 0, 9, 0, 0, 0 }));
            Assert.That(items.GetProperty("held").GetProperty("mat_bone").EnumerateArray().Select(e => e.GetInt64()), Is.EqualTo(new long[] { 3, 0, 0, 0, 0 }));
            Assert.That(items.GetProperty("minted").TryGetProperty("mat_metal", out _), Is.False, "a template with nothing minted");
            Assert.That(items.GetProperty("destroyed").EnumerateObject().Count(), Is.Zero);
            Assert.That(items.GetProperty("unitsPerMin").GetProperty("60s").GetDouble(), Is.GreaterThan(0d), "the units show in the rate");
            Assert.That(items.TryGetProperty("bySource", out _) && items.TryGetProperty("lostPerMin", out _) && items.TryGetProperty("queueOverflow", out _), Is.True);
        });
    }

    /// <summary>
    /// BZ-19: the <c>market</c> section writes what the desk counted and published — the board by city, the price rows grouped by item with the market
    /// value to the milli-credit, the unsold's age, the refusals by name — and <c>byKind</c> splits the credits into faucets and sinks.
    /// </summary>
    [Test]
    public void TheMarketSection_WritesTheCountersTheBoardAndTheFlows()
    {
        var armor = ItemCatalog.IdOf("core_armor");
        var hide = ItemCatalog.IdOf("mat_hide");
        var names = SimBridge.MarketRefusalNames.ToArray();
        var refused = new long[names.Length];
        refused[Array.FindIndex(names, n => n.Reason == TatooineReplication.MarketSold)] = 99;
        var listing = new ListingView(77, armor, 2, 1, 1500, 5, "Kel", 100, 1, 0);
        var input = new MarketStatsInput
        {
            Counters = new MarketCounters(3, 1, 0, 23, 5, 6400, 40, 512, 145, 50, 9, 2, 1, 4, 0, 0),
            Refused = refused,
            Board = new MarketSnapshot
            {
                Cities =
                [
                    new MarketCityView { City = 1, Name = "Port Sable", Listings = [listing, listing with { Id = 78 }], Reserved = 0, ListedValue = 1560 },
                    new MarketCityView { City = 2, Name = "Whitestone", Reserved = 4 },
                ],
                // Published out of order: the section groups by item, Mk ascending.
                Prices =
                [
                    new MarketPriceView(hide, 2, 2, 560, 1, 3, 0, 0, 0, 0),
                    new MarketPriceView(armor, 2, 1549, 309_800, 2, 1400, 1549, 1500, 1700, 4),
                    new MarketPriceView(hide, 1, 1, 340, 0, 0, 2, 2, 2, 1),
                ],
                UnsoldAgeS = (70, 100, 120),
            },
        };

        var j = new System.Text.StringBuilder();
        MarketStats.Write(j, input, ",\"salesPerMin\":{\"10s\":0,\"60s\":0}");
        using var doc = JsonDocument.Parse(j.ToString());
        var market = doc.RootElement;
        var cities = market.GetProperty("byCity").EnumerateArray().ToArray();
        var armorPrices = market.GetProperty("prices").GetProperty("core_armor").EnumerateArray().ToArray();
        var hidePrices = market.GetProperty("prices").GetProperty("mat_hide").EnumerateArray().ToArray();
        var age = market.GetProperty("unsoldAgeS");
        Assert.Multiple(() =>
        {
            Assert.That(market.GetProperty("listed").GetInt64(), Is.EqualTo(3));
            Assert.That(market.GetProperty("sold").GetInt64(), Is.EqualTo(1));
            Assert.That(market.GetProperty("escrowUnits").GetInt64(), Is.EqualTo(23));
            Assert.That(market.GetProperty("volume").GetInt64(), Is.EqualTo(6400));
            Assert.That(market.GetProperty("salesTax").GetInt64(), Is.EqualTo(512));
            Assert.That(market.GetProperty("vendorPaid").GetInt64(), Is.EqualTo(145));
            Assert.That(market.GetProperty("listings").GetInt64(), Is.EqualTo(9));
            Assert.That(market.GetProperty("collected").GetInt64(), Is.EqualTo(4));
            Assert.That(market.GetProperty("salesPerMin").TryGetProperty("60s", out _), Is.True, "the rates are carried");

            Assert.That(cities.Select(c => c.GetProperty("city").GetInt32()), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(cities.Select(c => c.GetProperty("name").GetString()), Is.EqualTo(new[] { "Port Sable", "Whitestone" }));
            Assert.That(cities.Select(c => c.GetProperty("listed").GetInt64()), Is.EqualTo(new long[] { 2, 0 }), "the city's listings");
            Assert.That(cities.Select(c => c.GetProperty("listedValue").GetInt64()), Is.EqualTo(new long[] { 1560, 0 }));
            Assert.That(cities.Select(c => c.GetProperty("reserved").GetInt64()), Is.EqualTo(new long[] { 0, 4 }));

            Assert.That(armorPrices, Has.Length.EqualTo(1));
            Assert.That(armorPrices[0].GetProperty("mk").GetInt32(), Is.EqualTo(2));
            Assert.That(armorPrices[0].GetProperty("sales").GetInt64(), Is.EqualTo(4));
            Assert.That(armorPrices[0].GetProperty("median").GetInt64(), Is.EqualTo(1549));
            Assert.That(armorPrices[0].GetProperty("min").GetInt64(), Is.EqualTo(1500));
            Assert.That(armorPrices[0].GetProperty("max").GetInt64(), Is.EqualTo(1700));
            Assert.That(armorPrices[0].GetProperty("listed").GetInt64(), Is.EqualTo(2));
            Assert.That(armorPrices[0].GetProperty("lowest").GetInt64(), Is.EqualTo(1400));
            Assert.That(armorPrices[0].GetProperty("value").GetDouble(), Is.EqualTo(1549d));

            Assert.That(hidePrices.Select(p => p.GetProperty("mk").GetInt32()), Is.EqualTo(new[] { 1, 2 }), "grouped by item, Mk ascending");
            Assert.That(hidePrices[0].GetProperty("median").GetInt64(), Is.EqualTo(2));
            Assert.That(hidePrices[0].GetProperty("value").GetDouble(), Is.EqualTo(1.7d), "a material's value, under a whole credit's precision");
            Assert.That(hidePrices[1].GetProperty("median").GetInt64(), Is.Zero, "0: no sale");
            Assert.That(hidePrices[1].GetProperty("lowest").GetInt64(), Is.EqualTo(3));
            Assert.That(hidePrices[1].GetProperty("value").GetDouble(), Is.EqualTo(2.8d));

            Assert.That(age.GetProperty("p50").GetInt64(), Is.EqualTo(70));
            Assert.That(age.GetProperty("p90").GetInt64(), Is.EqualTo(100));
            Assert.That(age.GetProperty("max").GetInt64(), Is.EqualTo(120));

            Assert.That(market.GetProperty("refused").EnumerateObject().Select(p => p.Name), Is.EqualTo(names.Select(n => n.Name)),
                "every reason named, 0 included");
            Assert.That(market.GetProperty("refused").GetProperty("sold").GetInt64(), Is.EqualTo(99));
        });

        // Faucets: grant, mission, vendor. Sinks: tax, list fee. A tip and a sale only move credits between wallets.
        long[] byKind = [0, 1000, 500, 20, 300, 24, 3, 145];
        var (faucets, sinks) = MarketStats.FaucetsAndSinks(byKind);
        var k = new System.Text.StringBuilder("{");
        MarketStats.WriteByKind(k, byKind);
        using var kinds = JsonDocument.Parse(k.Append('}').ToString());
        var byName = kinds.RootElement.GetProperty("byKind");
        Assert.Multiple(() =>
        {
            Assert.That(faucets, Is.EqualTo(1000 + 500 + 145));
            Assert.That(sinks, Is.EqualTo(24 + 3));
            Assert.That(byName.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "grant", "mission", "tip", "sale", "tax", "listfee", "vendor" }));
            Assert.That(byName.GetProperty("vendor").GetInt64(), Is.EqualTo(145));
            Assert.That(byName.GetProperty("listfee").GetInt64(), Is.EqualTo(3));
            Assert.That(MarketStats.CreditKinds, Has.Length.EqualTo(TransferKind.Count - 1), "every credit kind has a flow");
        });
    }

    /// <summary>BZ-19: <c>/swg/stats</c> carries <c>market</c> with every key of bazaar.md § 9, a row per city, and <c>economy.byKind</c>.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheMarketSection_IsInTheDemoStats_WithEveryKey()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x7303));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar");
        Harness.Ticks(Worlds.ServedHz * 2);

        using var doc = JsonDocument.Parse(sim.Bridge.DemoStatsJson());
        var market = doc.RootElement.GetProperty("market");
        var economy = doc.RootElement.GetProperty("economy");
        Assert.Multiple(() =>
        {
            foreach (var key in new[] { "listed", "sold", "returned", "escrowUnits", "byCity", "sales", "volume", "listFees", "salesTax", "vendorPaid",
                         "vendorUnits", "listings", "cancelled", "expired", "collected", "commitsFailed", "aborted", "refused", "salesPerMin", "volumePerMin",
                         "vendorPerMin", "feesPerMin", "prices", "unsoldAgeS" })
            {
                Assert.That(market.TryGetProperty(key, out _), Is.True, key);
            }

            Assert.That(market.GetProperty("byCity").ValueKind, Is.EqualTo(JsonValueKind.Array), "the board's cities, as the desk publishes them");
            Assert.That(market.GetProperty("unsoldAgeS").TryGetProperty("p90", out _), Is.True);
            Assert.That(economy.GetProperty("byKind").EnumerateObject().Count(), Is.EqualTo(MarketStats.CreditKinds.Length));
            Assert.That(economy.TryGetProperty("faucets", out _) && economy.TryGetProperty("sinks", out _), Is.True);
            Assert.That(economy.GetProperty("faucetsPerMin").TryGetProperty("60s", out _), Is.True);
            Assert.That(sim.Bridge.MarketCountersJson(), Does.StartWith("{\"listed\":"), "the exit line");
        });

        using var exit = JsonDocument.Parse(sim.Bridge.MarketCountersJson());
        Assert.That(exit.RootElement.GetProperty("byKind").EnumerateObject().Count(), Is.EqualTo(MarketStats.CreditKinds.Length));
    }

    /// <summary>
    /// The bots page's market column reads the swarm's market keys (BotReport.cs, bazaar.md § 8) — optional, so a swarm without the market still reports —
    /// and the refusals by the server's reason names.
    /// </summary>
    [Test]
    public void TheMarketSection_ReachesTheBotsPage()
    {
        const string report = """
            {"v":1,"run":"r1","at":"2026-10-27T12:00:00Z","hz":4,"tick":100,
             "fleet":{"bots":30,"windowS":60,"stuckS":0,"goals":{},"totals":{"hunts":2,"listed":7,"bought":3,"buyLost":9,"vendorCredits":145},
                      "perMin":{"listed":1.5,"vendored":2,"bought":0.5},"marketRefusedBy":{"sold":9,"notEnough":1}},
             "traced":[]}
            """;
        const string before = """{"v":1,"run":"r2","at":"2026-10-27T12:00:00Z","hz":4,"tick":100,"fleet":{"bots":1,"totals":{"hunts":1}},"traced":[]}""";
        var board = new SwgTatooine.Dashboard.BotBoard();
        var taken = board.Ingest(JsonSerializer.Deserialize<SwgTatooine.Dashboard.BotReport>(report, SwgTatooine.Dashboard.BotReport.Json), DateTime.UtcNow,
            out var error);
        var fleet = board.Latest.Fleet;
        Assert.Multiple(() =>
        {
            Assert.That(taken, Is.True, error);
            Assert.That(SwgTatooine.Dashboard.BotBoard.Of(fleet.Totals, "buyLost"), Is.EqualTo(9));
            Assert.That(SwgTatooine.Dashboard.BotBoard.Of(fleet.Totals, "vendorCredits"), Is.EqualTo(145));
            Assert.That(SwgTatooine.Dashboard.BotBoard.Of(fleet.PerMin, "vendored"), Is.EqualTo(2d));
            Assert.That(fleet.MarketRefusedBy["sold"], Is.EqualTo(9));
            Assert.That(SimBridge.MarketRefusalNames.ToArray().Select(n => n.Name), Is.SupersetOf(fleet.MarketRefusedBy.Keys),
                "the swarm names refusals as the server does");
            Assert.That(board.History("market trades/min").Single(), Is.EqualTo(1.5d + 2d + 0.5d));
        });

        taken = board.Ingest(JsonSerializer.Deserialize<SwgTatooine.Dashboard.BotReport>(before, SwgTatooine.Dashboard.BotReport.Json), DateTime.UtcNow,
            out error);
        Assert.Multiple(() =>
        {
            Assert.That(taken, Is.True, error);
            Assert.That(board.Latest.Fleet.Totals.ContainsKey("listed"), Is.False, "a swarm without the market: the page says so");
            Assert.That(board.Latest.Fleet.MarketRefusedBy, Is.Empty);
        });
    }
}
