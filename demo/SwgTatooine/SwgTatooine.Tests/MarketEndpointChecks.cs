using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;
using SwgTatooine.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;

namespace SwgTatooine.Tests;

/// <summary>
/// The bazaar over HTTP (bazaar.md § 5, BZ-12, BZ-13): browse filters, sorts and pages the published board without a transaction, a page at 20 000
/// listings well under a millisecond, the board following a commit within the publish interval; <c>/mine</c> the bearer's own.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class MarketEndpointChecks : ServedChecks
{
    // ── A board built by hand ───────────────────────────────────────────────────────────────────────────────────────

    private static readonly ushort[] Tradeable =
    [
        ItemCatalog.Hide, ItemCatalog.Bone, ItemCatalog.Metal, ItemCatalog.Venom, ItemCatalog.CoreArmor, ItemCatalog.CoreMelee, ItemCatalog.AddonArmor,
    ];

    /// <summary>A board of <paramref name="n"/> listings over <paramref name="cities"/> cities of one planet, deterministic for a seed.</summary>
    private static MarketSnapshot Board(int n, int cities, int seed, bool sorted = true)
    {
        var random = new Random(seed);
        var byCity = new List<ListingView>[cities];
        for (var c = 0; c < cities; c++)
        {
            byCity[c] = [];
        }

        for (var i = 0; i < n; i++)
        {
            var city = random.Next(cities);
            var template = Tradeable[random.Next(Tradeable.Length)];
            byCity[city].Add(new ListingView(1_000 + i, template, (byte)random.Next(1, 5), random.Next(1, 100), random.Next(1, 2_000), 7 + random.Next(50),
                "seller", random.Next(10_000), (ushort)(city + 1), 0));
        }

        var views = new MarketCityView[cities];
        for (var c = 0; c < cities; c++)
        {
            var rows = byCity[c].ToArray();
            if (sorted)
            {
                MarketCityView.SortBoard(rows);
            }

            views[c] = new MarketCityView { Realm = 0, City = (ushort)(c + 1), Name = $"City {c + 1}", Listings = rows };
        }

        return new MarketSnapshot { Version = 3, WorldTick = 20_000, TickRateHz = 50, Cities = views, Total = n };
    }

    /// <summary>What a query should answer, computed the plain way: every listing, filtered, sorted, paged.</summary>
    private static (int Total, List<long> Ids) Reference(MarketSnapshot board, MarketQuery q)
    {
        IEnumerable<ListingView> rows = board.Cities.SelectMany(c => c.Listings);
        rows = rows.Where(v => (q.City == 0 || v.City == q.City) && (q.Realm < 0 || v.Realm == q.Realm) && (q.Template == 0 || v.Template == q.Template)
            && (q.Mk < 0 || v.Mk == q.Mk) && (q.MaxUnit == 0 || v.UnitPrice <= q.MaxUnit)
            && (q.Category == 0 || (ItemCatalog.TryGet(v.Template, out var t) && t.Category == q.Category)));
        var all = rows.ToList();
        var total = all.Count;
        IEnumerable<ListingView> sorted = q.Sort switch
        {
            "-price" => all.OrderByDescending(v => v.UnitPrice),
            "new" => all.OrderByDescending(v => v.ListedTick),
            "mk" => all.OrderByDescending(v => v.Mk).ThenBy(v => v.UnitPrice),
            _ => all.OrderBy(v => v.UnitPrice),
        };
        var pages = Math.Max(1, (total + TatooineData.BrowsePage - 1) / TatooineData.BrowsePage);
        var page = Math.Min(q.Page, pages);
        return (total, sorted.Skip((page - 1) * TatooineData.BrowsePage).Take(TatooineData.BrowsePage).Select(v => v.Id).ToList());
    }

    private static (int Total, int Page, int Pages, List<ListingView> Rows) Parse(MarketSnapshot board, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rows = new List<ListingView>();
        foreach (var l in root.GetProperty("listings").EnumerateArray())
        {
            var id = long.Parse(l.GetProperty("id").GetString());
            rows.Add(board.All.Single(v => v.Id == id));
            Assert.That(l.GetProperty("total").GetInt64(), Is.EqualTo(l.GetProperty("unit").GetInt64() * l.GetProperty("qty").GetInt64()));
        }

        return (root.GetProperty("total").GetInt32(), root.GetProperty("page").GetInt32(), root.GetProperty("pages").GetInt32(), rows);
    }

    /// <summary>
    /// BZ-12: browse filters by city, category, item, Mk and price, sorts by price either way, by age and by Mk, and pages 20 at a time — every answer the
    /// plain filter-sort-page of the same listings, over 400 random queries; prices tie, so a page is compared as the prices and the set it shows.
    /// </summary>
    [Test]
    public void Browse_FiltersSortsAndPages_AsThePlainQueryWould()
    {
        var board = Board(1_500, 6, seed: 11);
        var random = new Random(5);
        string[] sorts = ["price", "-price", "new", "mk"];
        byte[] categories = [0, ItemCategory.Material, ItemCategory.Core, ItemCategory.Addon];
        for (var i = 0; i < 400; i++)
        {
            var q = new MarketQuery
            {
                City = random.Next(4) == 0 ? 0 : random.Next(1, 7),
                Category = categories[random.Next(categories.Length)],
                Template = random.Next(3) == 0 ? Tradeable[random.Next(Tradeable.Length)] : (ushort)0,
                Mk = random.Next(3) == 0 ? random.Next(1, 5) : -1,
                MaxUnit = random.Next(3) == 0 ? random.Next(100, 2_000) : 0,
                Sort = sorts[random.Next(sorts.Length)],
                Page = random.Next(1, 6),
            };
            var (total, ids) = Reference(board, q);
            var answer = Parse(board, MarketEndpoints.BrowseJson(board, q));
            var expected = ids.Select(id => board.All.Single(v => v.Id == id)).ToList();
            var what = $"query {i}: city {q.City} cat {q.Category} item {q.Template} mk {q.Mk} max {q.MaxUnit} sort {q.Sort} page {q.Page}";
            Assert.That(answer.Total, Is.EqualTo(total), what);
            Assert.That(answer.Rows, Has.Count.EqualTo(expected.Count), what);
            Func<ListingView, long> key = q.Sort switch
            {
                "new" => v => v.ListedTick,
                "mk" => v => (v.Mk * 10_000_000L) - v.UnitPrice,
                _ => v => v.UnitPrice,
            };
            Assert.That(answer.Rows.Select(key), Is.EqualTo(expected.Select(key)), what + ": not in its order");
        }

        // Every listing reached by paging through, once.
        var seen = new HashSet<long>();
        var pages = 0;
        for (var page = 1; ; page++)
        {
            var (_, _, count, rows) = Parse(board, MarketEndpoints.BrowseJson(board, new MarketQuery { City = 2, Sort = "new", Page = page }));
            pages = count;
            rows.ForEach(r => Assert.That(seen.Add(r.Id), Is.True, "a listing on two pages"));
            if (page >= count)
            {
                break;
            }
        }

        Assert.That(seen, Has.Count.EqualTo(board.Cities[1].Listings.Length), $"paging through {pages} pages did not reach every listing once");
    }

    /// <summary>
    /// BZ-12: at 20 000 listings a page is under a millisecond (the median of 200 queries of every shape), and indexing the board for browsing well under
    /// the 5 ms its rebuild is allowed. The bounds are Release's; Debug only checks the same code runs within ten times them.
    /// </summary>
    [Test]
    public void Browse_IsAPageUnderAMillisecond_At20000Listings()
    {
#if DEBUG
        const double scale = 10;
#else
        const double scale = 1;
#endif
        // What the desk's tick pays when every city changed: each city's board sorted.
        var index = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            var fresh = Board(20_000, 10, seed: 3, sorted: false);
            var started = Stopwatch.GetTimestamp();
            foreach (var city in fresh.Cities)
            {
                MarketCityView.SortBoard(city.Listings);
            }

            index.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        // The orders the first browse of a snapshot builds, off the tick: every city's and all cities'.
        var global = new List<double>();
        for (var i = 0; i < 9; i++)
        {
            var fresh = Board(20_000, 10, seed: 3);
            var started = Stopwatch.GetTimestamp();
            _ = fresh.ByPrice;
            foreach (var city in fresh.Cities)
            {
                _ = city.ByPrice;
            }

            global.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        var board = Board(20_000, 10, seed: 3);
        _ = board.ByPrice;
        foreach (var city in board.Cities)
        {
            _ = city.ByPrice;
        }

        var random = new Random(9);
        string[] sorts = ["price", "-price", "new"];
        var times = new List<double>();
        for (var i = 0; i < 260; i++)
        {
            var q = new MarketQuery
            {
                City = random.Next(3) == 0 ? 0 : random.Next(1, 11),
                Category = random.Next(2) == 0 ? (byte)0 : ItemCategory.Core,
                Template = random.Next(3) == 0 ? Tradeable[random.Next(Tradeable.Length)] : (ushort)0,
                Mk = random.Next(3) == 0 ? random.Next(1, 5) : -1,
                Sort = sorts[random.Next(sorts.Length)],
                Page = random.Next(1, 4),
            };
            var started = Stopwatch.GetTimestamp();
            var json = MarketEndpoints.BrowseJson(board, q);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.That(json, Does.StartWith("{\"v\":"));
            if (i >= 60)
            {
                times.Add(ms);
            }
        }

        times.Sort();
        index.Sort();
        global.Sort();
        var median = times[times.Count / 2];
        var p90 = times[(int)(times.Count * 0.9)];
        TestContext.Out.WriteLine($"browse at 20 000 listings: median {median:0.###} ms, p90 {p90:0.###} ms, max {times[^1]:0.###} ms; "
            + $"boards sorted min {index[0]:0.###} median {index[index.Count / 2]:0.###} ms; first-browse orders min {global[0]:0.###} median {global[global.Count / 2]:0.###} ms");
        Assert.Multiple(() =>
        {
            Assert.That(median, Is.LessThan(1.0 * scale), "a page's median");
            Assert.That(index[0], Is.LessThan(5.0 * scale), "sorting every city's board, at its quickest of nine: other processes share the box");
        });
    }

    // ── Live ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private TatooineSim Start()
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private (FakeLink Link, EntityId Body, EntityId Character) LogIn(TatooineSim sim, ulong account, MissionTerminalRef at, long bindings)
        => LogIn(sim, TestAccounts.For(sim.Dbe, account), at, bindings);

    private (FakeLink Link, EntityId Body, EntityId Character) LogIn(TatooineSim sim, EntityId account, MissionTerminalRef at, long bindings)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        var body = Worlds.Park(sim, link.Session, at.X + 2.5f, at.Z);
        Assert.That(CharacterStore.TryFind(sim.Dbe, account, out var character, out _), Is.True);
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return (link, body, character);
    }

    private static int Rev(TatooineSim sim, EntityId body, bool wallet)
    {
        var rev = 0;
        sim.WriteInTick(tx =>
        {
            var state = tx.Open(body).Read(Player.State);
            rev = wallet ? state.WalletRev : state.ItemsRev;
        });
        return rev;
    }

    /// <summary>
    /// BZ-12: the board follows a committed listing within <see cref="TatooineData.MarketPublishMs"/> of world time, and the browse answer — from the board,
    /// no transaction — shows it with its city and seller.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void Browse_FollowsACommit_WithinThePublishInterval()
    {
        using var sim = Start();
        var home = sim.Index.Bazaars[0];
        var a = LogIn(sim, 0x8C01, home, 1);
        sim.Bridge.StageTransfer(ItemLedger.World, unchecked((long)a.Character.RawValue), ItemCatalog.Bone, 2, 4, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Market.Units(sim, a.Character, ItemCatalog.Bone, 2) == 4, "the units");
        Harness.Ticks(Worlds.ServedHz);
        var version = sim.Bridge.MarketBoard.Version;
        Market.List(a.Link, ItemCatalog.Bone, 2, 4, 25);
        SessionHarness.Until(() => Market.Listings(sim).Count == 1, "the listing");
        var listedAt = sim.Runtime.CurrentTickNumber;
        SessionHarness.Until(() => sim.Bridge.MarketBoard.Total == 1, "the board to show it");
        var shownAt = sim.Runtime.CurrentTickNumber;
        var board = sim.Bridge.MarketBoard;
        var json = MarketEndpoints.BrowseJson(board, new MarketQuery { City = home.Region, Category = ItemCategory.Material });
        var id = Market.Listings(sim).Keys.Single();
        Assert.Multiple(() =>
        {
            Assert.That(shownAt - listedAt, Is.LessThanOrEqualTo((TatooineData.MarketPublishMs * Worlds.ServedHz / 1000) + 3), "the board lagged the commit");
            Assert.That(board.Version, Is.GreaterThan(version));
            Assert.That(json, Does.Contain($"\"id\":\"{unchecked((ulong)id)}\""));
            Assert.That(json, Does.Contain("\"item\":\"mat_bone\",\"name\":\"Bone\",\"category\":\"material\",\"mk\":2,\"qty\":4,\"unit\":25,\"total\":100"));
            Assert.That(json, Does.Contain($"\"seller\":\"{a.Character.RawValue}\""));
            Assert.That(MarketEndpoints.BrowseJson(board, new MarketQuery { City = home.Region, Category = ItemCategory.Core }), Does.Contain("\"total\":0"));
            Assert.That(MarketEndpoints.PricesJson(board), Does.Contain("{\"item\":\"mat_bone\",\"template\":9,\"name\":\"Bone\",\"category\":\"material\",\"mk\":2,\"value\":2.8,\"vendor\":560,\"listed\":1,\"lowest\":25"));
        });
    }

    /// <summary>
    /// H1 of review 9: the routes the host maps answer from the simulation's board, through the provider it is handed — browse, prices and the audit (whose
    /// MA-7 runs only against a published board) — and a host mapped without one refuses to start rather than serving an empty board.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task TheMappedRoutes_AnswerFromTheSimulationsBoard()
    {
        using var sim = Start();
        var home = sim.Index.Bazaars[0];
        var a = LogIn(sim, 0x8E01, home, 1);
        sim.Bridge.StageTransfer(ItemLedger.World, unchecked((long)a.Character.RawValue), ItemCatalog.Venom, 3, 2, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Market.Units(sim, a.Character, ItemCatalog.Venom, 3) == 2, "the units");
        Market.List(a.Link, ItemCatalog.Venom, 3, 2, 33);
        SessionHarness.Until(() => sim.Bridge.MarketBoard.Total == 1, "the board to show the listing");
        var id = unchecked((ulong)Market.Listings(sim).Keys.Single());

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        Assert.That(() => app.MapMarket(sim.Dbe, null), Throws.ArgumentNullException, "a host mapped without the board");
        app.MapMarket(sim.Dbe, () => sim.Bridge.MarketBoard);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var browse = await client.GetStringAsync($"/swg/market?city={home.Region}");
            var prices = await client.GetStringAsync("/swg/market/prices");
            var audit = await client.GetStringAsync("/swg/market/audit");
            var other = await client.GetAsync($"/swg/market?city={home.Region}&sort=new");
            var first = await client.GetAsync($"/swg/market?city={home.Region}");
            Assert.Multiple(() =>
            {
                Assert.That(browse, Does.Contain($"\"id\":\"{id}\""), "the route's board is not the simulation's");
                Assert.That(browse, Does.Contain("\"total\":1"));
                Assert.That(prices, Does.Contain("\"item\":\"mat_venom\",\"template\":11").And.Contain("\"lowest\":33"));
                Assert.That(audit, Does.StartWith("{\"ok\":true,\"listings\":1,\"listed\":1"));
                Assert.That(other.Headers.ETag, Is.Not.EqualTo(first.Headers.ETag), "two queries of one board share an ETag");
            });
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    /// <c>GET /swg/wallet/of?name=…</c>, for the selection pane (Loïc, 2026-10-27): a player's balance by its account name, through the route the host
    /// maps — the balance and nothing of its transfers — and 404 for a name no account has.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public async Task AnotherPlayersBalance_IsReadByItsName_WithoutItsTransfers()
    {
        using var sim = Start();
        Assert.That(AccountStore.TrySignup(sim.Dbe, "walletpeek", "wallet-peek-pass-1", out var account), Is.EqualTo(AccountStore.SignupOutcome.Created));
        var a = LogIn(sim, account, sim.Index.Bazaars[0], 1);
        var balance = Ledgers.Balance(sim, a.Character);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapEconomy(sim.Dbe);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var known = await client.GetStringAsync("/swg/wallet/of?name=WalletPeek");
            var unknown = await client.GetAsync("/swg/wallet/of?name=nobody-here");
            var empty = await client.GetAsync("/swg/wallet/of");
            Assert.Multiple(() =>
            {
                Assert.That(known, Is.EqualTo($"{{\"open\":true,\"credits\":{balance}}}"), "the balance, by the name as typed in any case");
                Assert.That(unknown.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(empty.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            });
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>
    /// BZ-13: <c>/swg/market/mine</c> answers the bearer's own listings and reservations and nobody else's, 401 without a token; the items and wallet
    /// revisions move for both sides of a sale.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void Mine_IsTheBearersOwn()
    {
        using var sim = Start();
        var home = sim.Index.Bazaars[0];
        var elsewhere = sim.Index.Bazaars.First(b => b.Realm == home.Realm && b.Region != home.Region);
        var a = LogIn(sim, 0x8D01, home, 1);
        var b = LogIn(sim, 0x8D02, elsewhere, 2);
        sim.Bridge.StageTransfer(ItemLedger.World, unchecked((long)a.Character.RawValue), ItemCatalog.Hide, 3, 6, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Market.Units(sim, a.Character, ItemCatalog.Hide, 3) == 6, "the units");
        Market.List(a.Link, ItemCatalog.Hide, 3, 3, 20);
        Market.List(a.Link, ItemCatalog.Hide, 3, 3, 30);
        SessionHarness.Until(() => Market.Listings(sim).Count == 2, "the two listings");
        Harness.Ticks(2);
        var sold = Market.Listings(sim).Single(kv => kv.Value.UnitPrice == 20).Key;
        var aItems = Rev(sim, a.Body, false);
        var aWallet = Rev(sim, a.Body, true);
        var bWallet = Rev(sim, b.Body, true);
        Market.Buy(b.Link, sold, 20);
        SessionHarness.Until(() => Market.Listings(sim)[sold].State == ListingState.Sold, "the buy");
        Harness.Ticks(2);

        (int Status, string Body) Get(string authorization)
        {
            var context = new DefaultHttpContext();
            if (authorization != null)
            {
                context.Request.Headers.Authorization = authorization;
            }

            return MarketEndpoints.GetMineForChecks(sim.Dbe, sim.Bridge.MarketBoard, context) switch
            {
                ContentHttpResult c => (c.StatusCode ?? 200, c.ResponseContent),
                IStatusCodeHttpResult s => (s.StatusCode ?? 0, null),
                var other => throw new AssertionException($"unexpected result {other}"),
            };
        }

        var mine = Get("Bearer " + AuthToken.Mint(TestAccounts.For(sim.Dbe, 0x8D01)));
        var theirs = Get("Bearer " + AuthToken.Mint(TestAccounts.For(sim.Dbe, 0x8D02)));
        using var aDoc = JsonDocument.Parse(mine.Body);
        using var bDoc = JsonDocument.Parse(theirs.Body);
        Assert.Multiple(() =>
        {
            Assert.That(Get(null).Status, Is.EqualTo(401), "no token");
            Assert.That(Get("Bearer rubbish").Status, Is.EqualTo(401), "a rubbish token");
            Assert.That(mine.Status, Is.EqualTo(200));
            Assert.That(aDoc.RootElement.GetProperty("listed").GetArrayLength(), Is.EqualTo(1), "the seller's listed: only the unsold one");
            Assert.That(aDoc.RootElement.GetProperty("listed")[0].GetProperty("unit").GetInt64(), Is.EqualTo(30));
            Assert.That(aDoc.RootElement.GetProperty("reserved").GetArrayLength(), Is.Zero, "the seller sees the buyer's reservation");
            Assert.That(bDoc.RootElement.GetProperty("listed").GetArrayLength(), Is.Zero);
            Assert.That(bDoc.RootElement.GetProperty("reserved").GetArrayLength(), Is.EqualTo(1));
            var r = bDoc.RootElement.GetProperty("reserved")[0];
            Assert.That(r.GetProperty("id").GetString(), Is.EqualTo(unchecked((ulong)sold).ToString()));
            Assert.That(r.GetProperty("state").GetString(), Is.EqualTo("sold"));
            Assert.That(r.GetProperty("city").GetInt32(), Is.EqualTo(home.Region));
            Assert.That(bDoc.RootElement.GetProperty("caps").GetProperty("reserved").GetInt32(), Is.EqualTo(TatooineData.MaxReserved));
            Assert.That(Rev(sim, a.Body, false), Is.GreaterThan(aItems), "the seller's items revision did not move at the sale");
            Assert.That(Rev(sim, a.Body, true), Is.GreaterThan(aWallet), "the seller's wallet revision did not move");
            Assert.That(Rev(sim, b.Body, true), Is.GreaterThan(bWallet), "the buyer's wallet revision did not move");
        });
    }
}
