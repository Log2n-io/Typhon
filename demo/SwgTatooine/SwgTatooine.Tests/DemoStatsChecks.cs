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
    /// BZ-19: the <c>market</c> section summarises what it is given — the board by city, the price index's inputs per item and Mk, the unsold's age, the
    /// refusals by name — and <c>byKind</c> splits the credits into faucets and sinks.
    /// </summary>
    [Test]
    public void TheMarketSection_SummarisesTheBoardTheSalesAndTheFlows()
    {
        var armor = ItemCatalog.IdOf("core_armor");
        var hide = ItemCatalog.IdOf("mat_hide");
        var refused = new long[MarketStats.ReasonNames.Length];
        refused[MarketStatsReason("sold")] = 99;
        var input = new MarketStatsInput
        {
            Counters = new MarketCounters(3, 1, 0, 23, 5, 6400, 40, 512, 145, 50, 9, 2, 1, 4, 0, 0),
            Refused = refused,
            // City 2 holds four uncollected; city 3 is past the array's end and reads 0.
            ReservedByCity = [0, 4],
            Open =
            [
                new MarketOpenListing(1, armor, 2, 1, 1500, 100),
                new MarketOpenListing(1, hide, 2, 20, 3, 400),
                new MarketOpenListing(3, armor, 2, 2, 1400, 1000),
            ],
            RecentSales = [new MarketSale(armor, 2, 1600), new MarketSale(armor, 2, 1500), new MarketSale(armor, 2, 1700), new MarketSale(armor, 2, 1549),
                new MarketSale(hide, 1, 2)],
            NowTick = 1100,
            TickRateHz = 10,
            ValueMilli = (t, mk) => t == armor ? 1_549_000 : mk == 1 ? 1_700 : 2_800,
        };

        var j = new System.Text.StringBuilder();
        MarketStats.Write(j, input, ["A", "B", "C"], ",\"salesPerMin\":{\"10s\":0,\"60s\":0}");
        using var doc = JsonDocument.Parse(j.ToString());
        var market = doc.RootElement;
        var cities = market.GetProperty("byCity").EnumerateArray().ToArray();
        var armorPrices = market.GetProperty("prices").GetProperty("core_armor").EnumerateArray().ToArray();
        var hidePrices = market.GetProperty("prices").GetProperty("mat_hide").EnumerateArray().ToArray();
        var age = market.GetProperty("unsoldAgeS");
        Assert.Multiple(() =>
        {
            Assert.That(market.GetProperty("listed").GetInt64(), Is.EqualTo(3));
            Assert.That(market.GetProperty("escrowUnits").GetInt64(), Is.EqualTo(23));
            Assert.That(market.GetProperty("volume").GetInt64(), Is.EqualTo(6400));
            Assert.That(market.GetProperty("salesPerMin").TryGetProperty("60s", out _), Is.True, "the rates are carried");

            Assert.That(cities.Select(c => c.GetProperty("city").GetInt32()), Is.EqualTo(new[] { 1, 2, 3 }), "region = city index + 1");
            Assert.That(cities.Select(c => c.GetProperty("name").GetString()), Is.EqualTo(new[] { "A", "B", "C" }));
            Assert.That(cities.Select(c => c.GetProperty("listed").GetInt64()), Is.EqualTo(new long[] { 2, 0, 1 }));
            Assert.That(cities.Select(c => c.GetProperty("listedValue").GetInt64()), Is.EqualTo(new long[] { 1500 + (20 * 3), 0, 2 * 1400 }));
            Assert.That(cities.Select(c => c.GetProperty("reserved").GetInt64()), Is.EqualTo(new long[] { 0, 4, 0 }));

            Assert.That(armorPrices, Has.Length.EqualTo(1));
            Assert.That(armorPrices[0].GetProperty("mk").GetInt32(), Is.EqualTo(2));
            Assert.That(armorPrices[0].GetProperty("sales").GetInt32(), Is.EqualTo(4));
            Assert.That(armorPrices[0].GetProperty("median").GetInt64(), Is.EqualTo(1549), "the lower middle of 1500, 1549, 1600, 1700: a price paid");
            Assert.That(armorPrices[0].GetProperty("min").GetInt64(), Is.EqualTo(1500));
            Assert.That(armorPrices[0].GetProperty("max").GetInt64(), Is.EqualTo(1700));
            Assert.That(armorPrices[0].GetProperty("listed").GetInt64(), Is.EqualTo(2));
            Assert.That(armorPrices[0].GetProperty("lowest").GetInt64(), Is.EqualTo(1400));
            Assert.That(armorPrices[0].GetProperty("value").GetDouble(), Is.EqualTo(1549d), "milli-credits as credits");

            Assert.That(hidePrices.Select(p => p.GetProperty("mk").GetInt32()), Is.EqualTo(new[] { 1, 2 }),
                "Mk ascending; sold-only and listed-only both shown");
            Assert.That(hidePrices[0].GetProperty("median").GetInt64(), Is.EqualTo(2));
            Assert.That(hidePrices[0].GetProperty("lowest").GetInt64(), Is.Zero, "0: nothing listed");
            Assert.That(hidePrices[0].GetProperty("value").GetDouble(), Is.EqualTo(1.7d), "a material's value is under a credit's precision of 1");
            Assert.That(hidePrices[1].GetProperty("sales").GetInt32(), Is.Zero);
            Assert.That(hidePrices[1].GetProperty("median").GetInt64(), Is.Zero, "0: no sale");
            Assert.That(hidePrices[1].GetProperty("lowest").GetInt64(), Is.EqualTo(3));

            // Ages 100, 70 and 10 s at 10 Hz.
            Assert.That(age.GetProperty("p50").GetInt64(), Is.EqualTo(70));
            Assert.That(age.GetProperty("p90").GetInt64(), Is.EqualTo(100));
            Assert.That(age.GetProperty("max").GetInt64(), Is.EqualTo(100));

            Assert.That(market.GetProperty("refused").EnumerateObject().Select(p => p.Name), Is.EqualTo(MarketStats.ReasonNames),
                "every reason named, 0 included");
            Assert.That(market.GetProperty("refused").GetProperty("sold").GetInt64(), Is.EqualTo(99));
        });

        // Faucets: grant, mission, vendor. Sinks: tax, list fee. A tip and a sale only move credits between wallets.
        long[] byKind = [0, 1000, 500, 20, 300, 24, 3, 145];
        var (faucets, sinks) = MarketStats.FaucetsAndSinks(byKind);
        var k = new System.Text.StringBuilder("{");
        MarketStats.WriteByKind(k, byKind);
        using var kinds = JsonDocument.Parse(k.Append('}').ToString());
        var names = kinds.RootElement.GetProperty("byKind");
        Assert.Multiple(() =>
        {
            Assert.That(faucets, Is.EqualTo(1000 + 500 + 145));
            Assert.That(sinks, Is.EqualTo(24 + 3));
            Assert.That(names.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "grant", "mission", "tip", "sale", "tax", "listfee", "vendor" }));
            Assert.That(names.GetProperty("vendor").GetInt64(), Is.EqualTo(145));
            Assert.That(names.GetProperty("listfee").GetInt64(), Is.EqualTo(3));
        });

        static int MarketStatsReason(string name) => Array.IndexOf(MarketStats.ReasonNames, name);
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

            Assert.That(market.GetProperty("byCity").GetArrayLength(), Is.EqualTo(sim.Index.Cities.Count), "a row per city, empty ones included");
            Assert.That(market.GetProperty("byCity")[0].GetProperty("name").GetString(), Is.EqualTo("Port Sable"), "the names a person sees");
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
            Assert.That(MarketStats.ReasonNames, Is.SupersetOf(fleet.MarketRefusedBy.Keys), "the swarm names refusals as the server does");
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
