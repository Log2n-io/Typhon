using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SwgTatooine.Server.Accounts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SwgTatooine.Server;

/// <summary>A browse query (bazaar.md § 5): the city, the filters, the order and the page.</summary>
public sealed class MarketQuery
{
    /// <summary>The city's group key, region + 1; 0 for every city.</summary>
    public int City { get; init; }

    /// <summary>The planet; -1 for every planet.</summary>
    public int Realm { get; init; } = -1;

    /// <summary>An <see cref="ItemCategory"/>; 0 for any.</summary>
    public byte Category { get; init; }

    /// <summary>One template; 0 for any.</summary>
    public ushort Template { get; init; }

    /// <summary>One Mk; -1 for any.</summary>
    public int Mk { get; init; } = -1;

    /// <summary>The highest unit price; 0 for any.</summary>
    public long MaxUnit { get; init; }

    /// <summary><c>price</c>, <c>-price</c>, <c>new</c> or <c>mk</c>.</summary>
    public string Sort { get; init; } = "price";

    /// <summary>From 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Reads a query string; an unknown value is the default, never an error.</summary>
    public static MarketQuery From(IQueryCollection q)
    {
        var category = (string)q["cat"] switch
        {
            "material" => ItemCategory.Material,
            "core" => ItemCategory.Core,
            "addon" => ItemCategory.Addon,
            _ => (byte)0,
        };
        return new MarketQuery
        {
            City = int.TryParse(q["city"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? Math.Max(0, c) : 0,
            // A city's number is a region of one planet, and every planet has the same ones: named without its planet, it is planet 0's.
            Realm = int.TryParse(q["realm"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r
                : int.TryParse(q["city"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var named) && named > 0 ? 0 : -1,
            Category = category,
            Template = string.IsNullOrEmpty(q["item"]) ? (ushort)0 : ItemCatalog.IdOf(q["item"]),
            Mk = int.TryParse(q["mk"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mk) ? mk : -1,
            MaxUnit = long.TryParse(q["max"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) ? Math.Max(0, max) : 0,
            Sort = (string)q["sort"] is "-price" or "new" or "mk" ? q["sort"] : "price",
            Page = int.TryParse(q["page"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? Math.Max(1, p) : 1,
        };
    }
}

/// <summary>The verdict of one market audit (bazaar.md § 7): the listings read, and every rule MA-1…MA-7 found broken, each named.</summary>
public sealed class MarketAuditResult
{
    /// <summary>The most violations kept by name; the count is exact beyond it.</summary>
    public const int ViolationsKept = 50;

    public bool Ok => ViolationCount == 0;

    public int Listings { get; internal set; }

    public int Listed { get; internal set; }

    public int Sold { get; internal set; }

    public int Returned { get; internal set; }

    /// <summary><c>sale</c> rows in the credit ledger.</summary>
    public long Sales { get; internal set; }

    /// <summary>Units in escrow: Σ of the stored listings' quantities.</summary>
    public long EscrowUnits { get; internal set; }

    public int ViolationCount { get; internal set; }

    public List<string> Violations { get; } = [];

    public double Milliseconds { get; internal set; }

    private readonly HashSet<string> _broken = [];

    internal void Violation(string rule, string what)
    {
        ViolationCount++;
        _broken.Add(rule);
        if (Violations.Count < ViolationsKept)
        {
            Violations.Add(rule + ": " + what);
        }
    }

    /// <summary>Whether a violation of <paramref name="rule"/> (<c>"MA-3"</c>) was found, among those kept or past them.</summary>
    public bool Breaks(string rule) => _broken.Contains(rule);

    public string ToJson()
    {
        var json = new StringBuilder(256);
        json.Append("{\"ok\":").Append(Ok ? "true" : "false")
            .Append(",\"listings\":").Append(Listings)
            .Append(",\"listed\":").Append(Listed)
            .Append(",\"sold\":").Append(Sold)
            .Append(",\"returned\":").Append(Returned)
            .Append(",\"sales\":").Append(Sales)
            .Append(",\"escrowUnits\":").Append(EscrowUnits)
            .Append(",\"violationCount\":").Append(ViolationCount)
            .Append(",\"violations\":[");
        for (var i = 0; i < Violations.Count; i++)
        {
            json.Append(i == 0 ? "\"" : ",\"").Append(EconomyEndpoints.Escape(Violations[i])).Append('"');
        }

        return json.Append("],\"ms\":").Append(Milliseconds.ToString("0.###", CultureInfo.InvariantCulture)).Append('}').ToString();
    }
}

/// <summary>
/// The bazaar over HTTP (bazaar.md § 5): <c>GET /swg/market</c> browses the published board; <c>/swg/market/mine</c> answers the bearer's own listings and
/// reservations; <c>/swg/market/prices</c> what is asked and what sold; <c>/swg/market/audit</c> checks listings against both ledgers.
/// </summary>
/// <remarks>
/// <b>Browse and prices read the snapshot only — no transaction</b>: thousands of listings, browsed from everywhere, rarely. Mine and the audit read the
/// database off the tick, legal under <c>EW-02</c> (listings are Versioned and non-spatial), each in one snapshot.
/// </remarks>
public static class MarketEndpoints
{
    /// <summary>Maps the four endpoints.</summary>
    /// <param name="app">The host.</param>
    /// <param name="dbe">The engine, for <c>/mine</c> and the audit.</param>
    /// <param name="board">
    /// The served simulation's published board (<c>SimBridge.MarketBoard</c>), asked at each request. Required: a host mapped without one would serve an
    /// empty board and an audit that skips MA-7 and says OK.
    /// </param>
    public static IEndpointRouteBuilder MapMarket(this IEndpointRouteBuilder app, DatabaseEngine dbe, Func<MarketSnapshot> board)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dbe);
        ArgumentNullException.ThrowIfNull(board);
        app.MapGet("/swg/market", (HttpContext context) => GetBrowse(board() ?? MarketSnapshot.Empty, context));
        app.MapGet("/swg/market/mine", (HttpContext context) => GetMine(dbe, board() ?? MarketSnapshot.Empty, context));
        app.MapGet("/swg/market/prices", (HttpContext context) => GetPrices(board() ?? MarketSnapshot.Empty, context));
        app.MapGet("/swg/market/audit", (HttpContext context) => GetAudit(dbe, board() ?? MarketSnapshot.Empty, context));
        app.MapGet("/swg/market/standard", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Text(StandardJson(), "application/json");
        });
        return app;
    }

    /// <summary>The mine handler itself, for the checks: a request in, the result out.</summary>
    internal static IResult GetMineForChecks(DatabaseEngine dbe, MarketSnapshot board, HttpContext context) => GetMine(dbe, board, context);

    /// <summary>The prices handler itself, for the checks.</summary>
    internal static IResult GetPricesForChecks(MarketSnapshot board, HttpContext context) => GetPrices(board, context);

    private static IResult GetBrowse(MarketSnapshot board, HttpContext context)
    {
        // The board's version AND the query: one ETag per answer, so a cached page of one query is never served for another.
        var etag = "\"m" + board.Version.ToString(CultureInfo.InvariantCulture) + "-" + Fnv(context.Request.QueryString.Value).ToString("x8", CultureInfo.InvariantCulture)
            + "\"";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = etag;
        var query = MarketQuery.From(context.Request.Query);
        if (context.Request.Headers.IfNoneMatch.ToString() == etag)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Text(BrowseJson(board, query), "application/json");
    }

    private static uint Fnv(string s)
    {
        var h = 2166136261u;
        foreach (var c in s ?? "")
        {
            h = (h ^ c) * 16777619u;
        }

        return h;
    }

    private static IResult GetMine(DatabaseEngine dbe, MarketSnapshot board, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        const string scheme = "Bearer ";
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
        if (!AuthToken.TryRead(token, out var account, out var expired) || (expired && !TatooineReplication.IsPlaying(account)))
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        // Built without a string (PooledJson): every bot re-reads it on each items revision while it names something — 2.6 MB/s live (2026-11-05).
        return CharacterStore.TryFind(dbe, account, out var character, out _)
            ? PooledJson.Of((dbe, character, board), static (json, s) => AppendMine(json, s.dbe, s.character, s.board))
            : Results.Text(MineEmpty, "application/json");
    }

    private static IResult GetPrices(MarketSnapshot board, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-cache";

        // Encoded once per snapshot (MarketSnapshot.PricesUtf8): the same 29 KB for every caller, rebuilt per request at 1.8 MB/s live (2026-11-05).
        return PooledJson.Of(board.PricesUtf8 ??= Encoding.UTF8.GetBytes(PricesJsonCore(board)));
    }

    private static IResult GetAudit(DatabaseEngine dbe, MarketSnapshot board, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = Audit(dbe, board);
        Console.WriteLine(
            $"  market audit: {(result.Ok ? "OK" : "VIOLATED")} - {result.Listings} listings ({result.Listed} listed, {result.Sold} sold, {result.Returned} returned), "
            + $"{result.Sales} sales, {result.EscrowUnits} units in escrow, {result.ViolationCount} violation(s), {result.Milliseconds:0.#} ms");
        return Results.Text(result.ToJson(), "application/json");
    }

    // ── Browse ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One page of the board as JSON: <c>{"v","city","total","page","pages","listings":[{"id","city","cityName","realm","item","name","category","mk","qty",
    /// "unit","total","seller","sellerName","ageS"}]}</c>. <c>id</c> and <c>seller</c> are strings: 64-bit ids are past what a JavaScript number holds.
    /// </summary>
    /// <remarks>
    /// <b>From the snapshot only.</b> A query naming an item is a binary search of each city's board, which is sorted by (template, Mk, unit price, listed);
    /// any other is a scan of the cities' boards, then — only for its matches — a sort into the order asked. At 20 000 listings either is well under a
    /// millisecond (BZ-12).
    /// </remarks>
    public static string BrowseJson(MarketSnapshot board, MarketQuery q)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(q);
        if (q.Template == 0 && q.Sort != "mk")
        {
            return PresortedPage(board, q);
        }

        var matches = new List<ListingView>();
        foreach (var city in board.Cities)
        {
            if ((q.City != 0 && city.City != q.City) || (q.Realm >= 0 && city.Realm != q.Realm))
            {
                continue;
            }

            var rows = city.Listings;
            int start = 0, end = rows.Length;
            if (q.Template != 0)
            {
                start = LowerBound(rows, q.Template, q.Mk < 0 ? (byte)0 : (byte)q.Mk);
                end = LowerBound(rows, q.Template, q.Mk < 0 ? byte.MaxValue : (byte)q.Mk, upper: true);
            }

            for (var i = start; i < end; i++)
            {
                ref readonly var v = ref rows[i];
                if (Matches(in v, q))
                {
                    matches.Add(v);
                }
            }
        }

        matches.Sort(q.Sort switch
        {
            "-price" => static (a, b) => b.UnitPrice != a.UnitPrice ? b.UnitPrice.CompareTo(a.UnitPrice) : Tie(a, b),
            "new" => static (a, b) => b.ListedTick != a.ListedTick ? b.ListedTick.CompareTo(a.ListedTick) : b.Id.CompareTo(a.Id),
            "mk" => static (a, b) => a.Mk != b.Mk ? b.Mk.CompareTo(a.Mk) : a.UnitPrice != b.UnitPrice ? a.UnitPrice.CompareTo(b.UnitPrice) : Tie(a, b),
            _ => static (a, b) => a.UnitPrice != b.UnitPrice ? a.UnitPrice.CompareTo(b.UnitPrice) : Tie(a, b),
        });
        var pages = Math.Max(1, (matches.Count + TatooineData.BrowsePage - 1) / TatooineData.BrowsePage);
        var page = Math.Min(q.Page, pages);
        var from = (page - 1) * TatooineData.BrowsePage;
        var to = Math.Min(matches.Count, from + TatooineData.BrowsePage);
        return PageJson(board, q.City, matches.Count, page, pages, matches.GetRange(from, to - from));

        static int Tie(ListingView a, ListingView b) => a.ListedTick != b.ListedTick ? a.ListedTick.CompareTo(b.ListedTick) : a.Id.CompareTo(b.Id);
    }

    /// <summary>
    /// A page with no item named, in price or age order: one scan of the presorted order — the city's when one is named, every city's otherwise — counting
    /// the matches and copying only the page's.
    /// </summary>
    private static string PresortedPage(MarketSnapshot board, MarketQuery q)
    {
        var city = -1;
        var cities = 0;
        for (var c = 0; c < board.Cities.Length; c++)
        {
            if ((q.City == 0 || board.Cities[c].City == q.City) && (q.Realm < 0 || board.Cities[c].Realm == q.Realm))
            {
                city = c;
                cities++;
            }
        }

        var newest = q.Sort == "new";
        var one = cities == 1 ? board.Cities[city] : null;
        var order = one != null
            ? newest ? one.ByNew : one.ByPrice
            : newest ? board.ByNew : board.ByPrice;
        var backwards = newest || q.Sort == "-price";
        var all = one != null ? one.Listings : board.All;
        var from = (q.Page - 1) * TatooineData.BrowsePage;
        var rows = new List<ListingView>(TatooineData.BrowsePage);
        var total = 0;
        for (var k = 0; k < order.Length; k++)
        {
            ref readonly var v = ref all[order[backwards ? order.Length - 1 - k : k]];
            if ((cities != 1 && ((q.City != 0 && v.City != q.City) || (q.Realm >= 0 && v.Realm != q.Realm))) || !Matches(in v, q))
            {
                continue;
            }

            if (total >= from && rows.Count < TatooineData.BrowsePage)
            {
                rows.Add(v);
            }

            total++;
        }

        var pages = Math.Max(1, (total + TatooineData.BrowsePage - 1) / TatooineData.BrowsePage);
        if (q.Page > pages)
        {
            // Past the last page: the last page, as the sorted path answers.
            return PresortedPage(board, new MarketQuery
            {
                City = q.City, Realm = q.Realm, Category = q.Category, Mk = q.Mk, MaxUnit = q.MaxUnit, Sort = q.Sort, Page = pages,
            });
        }

        return PageJson(board, q.City, total, q.Page, pages, rows);
    }

    private static string PageJson(MarketSnapshot board, int queryCity, int total, int page, int pages, List<ListingView> rows)
    {
        var json = new StringBuilder(256 + (TatooineData.BrowsePage * 256));
        json.Append("{\"v\":").Append(board.Version)
            .Append(",\"city\":").Append(queryCity)
            .Append(",\"total\":").Append(total)
            .Append(",\"page\":").Append(page)
            .Append(",\"pages\":").Append(pages)
            .Append(",\"listings\":[");
        for (var i = 0; i < rows.Count; i++)
        {
            var v = rows[i];
            ItemCatalog.TryGet(v.Template, out var t);
            json.Append(i == 0 ? "{" : ",{")
                .Append("\"id\":\"").Append(unchecked((ulong)v.Id)).Append('"')
                .Append(",\"city\":").Append(v.City)
                .Append(",\"cityName\":\"").Append(EconomyEndpoints.Escape(CityName(board, v.Realm, v.City))).Append('"')
                .Append(",\"realm\":").Append(v.Realm)
                .Append(",\"template\":").Append(v.Template)
                .Append(",\"item\":\"").Append(t.Asset).Append('"')
                .Append(",\"name\":\"").Append(EconomyEndpoints.Escape(t.Name ?? "")).Append('"')
                .Append(",\"category\":\"").Append(ItemCategory.NameOf(t.Category)).Append('"')
                .Append(",\"mk\":").Append(v.Mk)
                .Append(",\"qty\":").Append(v.Quantity)
                .Append(",\"unit\":").Append(v.UnitPrice)
                .Append(",\"total\":").Append(v.UnitPrice * v.Quantity)
                .Append(",\"seller\":\"").Append(unchecked((ulong)v.Seller)).Append('"')
                .Append(",\"sellerName\":\"").Append(EconomyEndpoints.Escape(v.SellerName ?? "")).Append('"')
                .Append(",\"ageS\":").Append(Math.Max(0, board.WorldTick - v.ListedTick) / board.TickRateHz);
            if (ItemCatalog.HasStats(v.Template))
            {
                ItemEndpoints.Stats(json, in t, v.Stats);
            }

            json.Append('}');
        }

        return json.Append("]}").ToString();
    }

    private static bool Matches(in ListingView v, MarketQuery q)
    {
        if ((q.Mk >= 0 && v.Mk != q.Mk) || (q.MaxUnit > 0 && v.UnitPrice > q.MaxUnit))
        {
            return false;
        }

        return q.Category == 0 || (ItemCatalog.TryGet(v.Template, out var t) && t.Category == q.Category);
    }

    // The first row at or after (template, mk) — or, upper, the first after it — in a board sorted by (template, Mk, …).
    private static int LowerBound(ListingView[] rows, ushort template, byte mk, bool upper = false)
    {
        int lo = 0, hi = rows.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            var c = rows[mid].Template != template ? rows[mid].Template.CompareTo(template) : rows[mid].Mk.CompareTo(mk);
            if (c < 0 || (upper && c == 0))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static string CityName(MarketSnapshot board, ushort realm, ushort city)
    {
        foreach (var c in board.Cities)
        {
            if (c.Realm == realm && c.City == city)
            {
                return c.Name;
            }
        }

        return "";
    }

    // ── Mine ────────────────────────────────────────────────────────────────────────────────────────────────────────

    internal const string MineEmpty = "{\"listed\":[],\"reserved\":[],\"caps\":{\"listings\":20,\"reserved\":20}}";

    /// <summary>
    /// A character's own board as JSON: <c>{"listed":[{"id","city","cityName","realm","item","name","category","mk","qty","unit","total","fee","listedTick",
    /// "expireTick","ageS"}],"reserved":[{"id","city","cityName","realm","item","name","category","mk","qty","unit","state","closedTick"}],"caps"}</c> —
    /// one quick transaction, two index scans.
    /// </summary>
    public static string MineJson(DatabaseEngine dbe, EntityId character, MarketSnapshot board)
    {
        var json = new StringBuilder(1024);
        AppendMine(json, dbe, character, board);
        return json.ToString();
    }

    // The listings' seller and collector indexes, resolved once per engine: an expression tree a lookup built two of on every request.
    private static (DatabaseEngine Dbe, IndexRef Seller, IndexRef Collector) _mineIndexes;
    private static readonly object MineIndexesLock = new();

    private static (IndexRef Seller, IndexRef Collector) MineIndexes(DatabaseEngine dbe)
    {
        lock (MineIndexesLock)
        {
            if (!ReferenceEquals(_mineIndexes.Dbe, dbe))
            {
                _mineIndexes = (dbe, dbe.GetIndexRef<Listing, long>(l => l.Seller), dbe.GetIndexRef<Listing, long>(l => l.Collector));
            }

            return (_mineIndexes.Seller, _mineIndexes.Collector);
        }
    }

    /// <summary><see cref="MineJson"/> written into <paramref name="json"/>.</summary>
    public static void AppendMine(StringBuilder json, DatabaseEngine dbe, EntityId character, MarketSnapshot board)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        board ??= MarketSnapshot.Empty;
        var key = unchecked((long)character.RawValue);
        var (byseller, bycollector) = MineIndexes(dbe);
        using var tx = dbe.CreateQuickTransaction();
        json.Append("{\"listed\":[");
        var first = true;
        using (var mine = tx.EnumerateIndex<Listing, long>(byseller, key, key))
        {
            while (mine.MoveNext())
            {
                var l = mine.CurrentComponent;
                if (l.State != ListingState.Listed)
                {
                    continue;
                }

                json.Append(first ? "{" : ",{");
                first = false;
                Common(json, board, mine.CurrentEntityPK, in l);
                json.Append(",\"total\":").Append(l.UnitPrice * l.Quantity)
                    .Append(",\"fee\":").Append(l.Fee)
                    .Append(",\"listedTick\":").Append(l.ListedTick)
                    .Append(",\"expireTick\":").Append(l.ExpireTick)
                    .Append(",\"ageS\":").Append(Math.Max(0, board.WorldTick - l.ListedTick) / board.TickRateHz)
                    .Append('}');
            }
        }

        json.Append("],\"reserved\":[");
        first = true;
        using (var mine = tx.EnumerateIndex<Listing, long>(bycollector, key, key))
        {
            while (mine.MoveNext())
            {
                var l = mine.CurrentComponent;
                if (l.State is not (ListingState.Sold or ListingState.Returned))
                {
                    continue;
                }

                json.Append(first ? "{" : ",{");
                first = false;
                Common(json, board, mine.CurrentEntityPK, in l);
                json.Append(",\"state\":\"").Append(ListingState.NameOf(l.State)).Append('"')
                    .Append(",\"closedTick\":").Append(l.ClosedTick)
                    .Append('}');
            }
        }

        json.Append("],\"caps\":{\"listings\":").Append(TatooineData.MaxListings)
            .Append(",\"reserved\":").Append(TatooineData.MaxReserved).Append("}}");

        static void Common(StringBuilder json, MarketSnapshot board, long id, in Listing l)
        {
            ItemCatalog.TryGet(l.Template, out var t);
            json.Append("\"id\":\"").Append(unchecked((ulong)id)).Append('"')
                .Append(",\"template\":").Append(l.Template)
                .Append(",\"city\":").Append(l.City)
                .Append(",\"cityName\":\"").Append(EconomyEndpoints.Escape(CityName(board, l.Realm, l.City))).Append('"')
                .Append(",\"realm\":").Append(l.Realm)
                .Append(",\"item\":\"").Append(t.Asset).Append('"')
                .Append(",\"name\":\"").Append(EconomyEndpoints.Escape(t.Name ?? "")).Append('"')
                .Append(",\"category\":\"").Append(ItemCategory.NameOf(t.Category)).Append('"')
                .Append(",\"mk\":").Append(l.Mk)
                .Append(",\"qty\":").Append(l.Quantity)
                .Append(",\"unit\":").Append(l.UnitPrice);
        }
    }

    // ── Prices ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>{"v","prices":[{"item","name","category","mk","value","vendor","listed","lowest","median","min","max","sales"}]}</c>: <c>value</c> whole credits,
    /// <c>vendor</c> what the vendor pays per unit in milli-credits (×1000), the rest from the board and its last 200 sales.
    /// </summary>
    public static string PricesJson(MarketSnapshot board)
        => PricesJsonCore(board);

    /// <summary>
    /// What the vendor sells (crafting-catalog.md § 15.3): <c>{"items":[{"template","item","name","category","mk","price","stats"}]}</c>, standard issue,
    /// Mk I, at twice its value — the client owns no item table, so the bazaar's standard-issue tab lists this. Fixed: the same for every caller.
    /// </summary>
    public static string StandardJson()
    {
        var json = new StringBuilder(2048);
        json.Append("{\"items\":[");
        var first = true;
        foreach (var s in Equipment.Standard)
        {
            ItemCatalog.TryGet(s.Template, out var t);
            json.Append(first ? "{" : ",{")
                .Append("\"template\":").Append(s.Template)
                .Append(",\"item\":\"").Append(t.Asset).Append('"')
                .Append(",\"name\":\"").Append(EconomyEndpoints.Escape(t.Name ?? "")).Append('"')
                .Append(",\"category\":\"").Append(ItemCategory.NameOf(t.Category)).Append('"')
                .Append(",\"mk\":").Append(Equipment.StandardMk(s.Template))
                .Append(",\"price\":").Append(Equipment.StandardPrice(s.Template));
            ItemEndpoints.Stats(json, in t, SimBridge.StandardStats(in s));
            json.Append('}');
            first = false;
        }

        // The extractor kit (crafting-build.md § 10, O-14): no Mk, its rate and hopper as its stats.
        ItemCatalog.TryGet(ItemCatalog.Extractor, out var kitTemplate);
        json.Append(",{")
            .Append("\"template\":").Append(ItemCatalog.Extractor)
            .Append(",\"item\":\"").Append(kitTemplate.Asset).Append('"')
            .Append(",\"name\":\"").Append(EconomyEndpoints.Escape(kitTemplate.Name ?? "")).Append('"')
            .Append(",\"category\":\"").Append(ItemCategory.NameOf(kitTemplate.Category)).Append('"')
            .Append(",\"mk\":0")
            .Append(",\"price\":").Append(Installations.StandardKitPrice);
        ItemEndpoints.Stats(json, in kitTemplate, Installations.StandardKit());
        json.Append('}');

        // The Mk I bulk materials, by the unit: "priceMilli" a unit, a quantity's price its total rounded down (Equipment.StandardMaterialPrice).
        foreach (var m in Equipment.StandardMaterials)
        {
            ItemCatalog.TryGet(m, out var t);
            json.Append(",{")
                .Append("\"template\":").Append(m)
                .Append(",\"item\":\"").Append(t.Asset).Append('"')
                .Append(",\"name\":\"").Append(EconomyEndpoints.Escape(t.Name ?? "")).Append('"')
                .Append(",\"category\":\"").Append(ItemCategory.NameOf(t.Category)).Append('"')
                .Append(",\"mk\":1")
                .Append(",\"price\":").Append(Equipment.StandardMaterialPrice(m, 1))
                .Append(",\"priceMilli\":").Append(Equipment.StandardMaterialMilli(m))
                .Append('}');
        }

        return json.Append("]}").ToString();
    }

    private static string PricesJsonCore(MarketSnapshot board)
    {
        ArgumentNullException.ThrowIfNull(board);
        var json = new StringBuilder(256 + (board.Prices.Length * 160));
        json.Append("{\"v\":").Append(board.Version).Append(",\"prices\":[");
        for (var i = 0; i < board.Prices.Length; i++)
        {
            var p = board.Prices[i];
            ItemCatalog.TryGet(p.Template, out var t);
            json.Append(i == 0 ? "{" : ",{")
                .Append("\"item\":\"").Append(t.Asset).Append('"')
                .Append(",\"template\":").Append(p.Template)
                .Append(",\"name\":\"").Append(EconomyEndpoints.Escape(t.Name ?? "")).Append('"')
                .Append(",\"category\":\"").Append(ItemCategory.NameOf(t.Category)).Append('"')
                .Append(",\"mk\":").Append(p.Mk)
                .Append(",\"value\":").Append((MarketValues.MilliOf(p.Template, p.Mk) / 1000m).ToString("0.###", CultureInfo.InvariantCulture))
                .Append(",\"vendor\":").Append(p.VendorMilli)
                .Append(",\"listed\":").Append(p.Listed)
                .Append(",\"lowest\":").Append(p.Lowest)
                .Append(",\"median\":").Append(p.Median)
                .Append(",\"min\":").Append(p.Min)
                .Append(",\"max\":").Append(p.Max)
                .Append(",\"sales\":").Append(p.Sales)
                .Append('}');
        }

        return json.Append("]}").ToString();
    }

    // ── Audit ───────────────────────────────────────────────────────────────────────────────────────────────────────

    // One vendor sale as the item walk reads it: its rows, consecutive, naming one credit row.
    private struct VendorSale
    {
        public long CreditSeq;
        public long FirstItem;
        public long Seller;
        public ushort Template;
        public byte Mk;
        public int Units;
        public bool One;
    }

    private sealed class Trail
    {
        public long Seller;
        public long Collector;
        public long Listed;
        public long Net;
        public bool Bought;
        public bool ReturnedHome;
        public long LastTick;
        public long Rows;
        public long LastSeq;
        public ushort Template;
        public byte Mk;
    }

    /// <summary>
    /// The market audit (bazaar.md § 7): one snapshot reading every listing, the item ledger and the credit ledger; every rule MA-1…MA-7 checked, each
    /// violation named by its rule.
    /// </summary>
    /// <param name="dbe">The engine.</param>
    /// <param name="board">The published board, for MA-7 and for the cities that have a bazaar (MA-1).</param>
    public static MarketAuditResult Audit(DatabaseEngine dbe, MarketSnapshot board)
    {
        ArgumentNullException.ThrowIfNull(dbe);
        using var tx = dbe.CreateReadOnlyTransaction();
        return Audit(dbe, board, tx);
    }

    /// <summary>
    /// The audit read through <paramref name="tx"/>'s snapshot — a check passes a reader older than the commits it stages. Every listing is read, so by an
    /// archetype scan.
    /// </summary>
    internal static MarketAuditResult Audit(DatabaseEngine dbe, MarketSnapshot board, Transaction tx)
    {
        board ??= MarketSnapshot.Empty;
        var started = Stopwatch.GetTimestamp();
        var result = new MarketAuditResult();
        var routing = dbe.ArchetypeIdOf<ListingArch>();
        bool IsListing(long key) => key != 0 && EntityId.FromRawValue((ulong)key).ArchetypeId == routing;

        // The listings as stored.
        var stored = new Dictionary<long, Listing>();
        foreach (var listing in tx.Query<ListingArch>())
        {
            stored[unchecked((long)listing.Id.RawValue)] = listing.Read(ListingArch.Entry);
        }

        // The item ledger's market rows: each listing's escrow replayed, who filled it and who emptied it; the vendor sales' rows grouped by their credit row.
        var trails = new Dictionary<long, Trail>();
        Trail TrailOf(long id)
        {
            if (!trails.TryGetValue(id, out var t))
            {
                t = new Trail();
                trails[id] = t;
            }

            return t;
        }

        // The credit ledger's market rows, by the listing they name — read beside the item ledger rather than after it. MA-5 pairs each vendor sale's item
        // rows with its credit row as the item walk closes that sale: both are written in one transaction, so the sales come in the same order in both
        // ledgers and nothing is held per sale. Held per sale, it was two tables of 17 M entries each on a seeded 29 GB world (index-scan, 2026-11-04).
        var sales = new Dictionary<long, List<LedgerEntry>>();
        var taxes = new Dictionary<long, List<LedgerEntry>>();
        var fees = new Dictionary<long, List<LedgerEntry>>();
        var credits = tx.EnumerateIndex<LedgerEntry, long>(dbe.GetIndexRef<LedgerEntry, long>(e => e.Seq), long.MinValue, long.MaxValue);
        var vendor = default(LedgerEntry);
        var hasVendor = false;
        var open = default(VendorSale);
        var inSale = false;

        // Vendor credits the walk passed to reach a later sale's: held only on a broken ledger, so a sale after a broken one still finds its own.
        var passed = new Dictionary<long, LedgerEntry>();

        // The credit rows up to the next vendor row, the market's filed by listing; false at the ledger's end.
        bool NextVendorCredit(ref Transaction.IndexEntityEnumerator<LedgerEntry, long> credit, out LedgerEntry found)
        {
            while (credit.MoveNext())
            {
                var r = credit.CurrentComponent;
                switch (r.Kind)
                {
                    case TransferKind.Sale:
                        Add(sales, r.Ref, in r);
                        result.Sales++;
                        break;
                    case TransferKind.Tax:
                        Add(taxes, r.Ref, in r);
                        break;
                    case TransferKind.ListFee:
                        Add(fees, r.Ref, in r);
                        break;
                    case TransferKind.Vendor:
                        found = r;
                        return true;
                }
            }

            found = default;
            return false;
        }

        // MA-5: one sale's item rows and the credit row they name, which name each other.
        void CloseSale(ref Transaction.IndexEntityEnumerator<LedgerEntry, long> credit, in VendorSale s)
        {
            if (!passed.Remove(s.CreditSeq, out var paidBy))
            {
                if (!hasVendor)
                {
                    hasVendor = NextVendorCredit(ref credit, out vendor);
                }

                while (hasVendor && vendor.Seq < s.CreditSeq)
                {
                    passed[vendor.Seq] = vendor;
                    hasVendor = NextVendorCredit(ref credit, out vendor);
                }

                if (!hasVendor || vendor.Seq != s.CreditSeq)
                {
                    result.Violation("MA-5", $"vendor item rows from seq {s.FirstItem} name credit seq {s.CreditSeq}, which is not a vendor row");
                    return;
                }

                paidBy = vendor;
                hasVendor = false;
            }

            if (paidBy.Ref != s.FirstItem)
            {
                result.Violation("MA-5", $"vendor credit seq {s.CreditSeq} names item seq {paidBy.Ref}, its item rows start at {s.FirstItem}");
                return;
            }

            // One sale is one seller's units of one item, paid the floor on all of them, to that seller. An item with stats is paid by the condition it had
            // left (Loïc, 2026-11-03), which its destroyed entity no longer says: from 1 credit to the floor of a new one.
            var payout = MarketValues.VendorPayout(s.Template, s.Mk, s.Units);
            var paid = ItemCatalog.HasStats(s.Template) ? paidBy.Amount >= 1 && paidBy.Amount <= payout : paidBy.Amount == payout;
            if (!s.One || paidBy.To != s.Seller || !paid)
            {
                result.Violation("MA-5", $"vendor credit seq {s.CreditSeq} paid {paidBy.Amount} to {paidBy.To}; its rows sold {s.Units} of {s.Template} Mk "
                    + $"{s.Mk} from {s.Seller}{(s.One ? "" : " and other items or sellers")}, worth {payout}");
            }
        }

        try
        {
            using (var rows = tx.EnumerateIndex<ItemLedgerEntry, long>(dbe.GetIndexRef<ItemLedgerEntry, long>(e => e.Seq), long.MinValue, long.MaxValue))
            {
                while (rows.MoveNext())
                {
                    var r = rows.CurrentComponent;
                    switch (r.Kind)
                    {
                        case ItemMoveKind.List:
                            if (!IsListing(r.To) || r.ToStack != r.To || IsListing(r.From) || r.From == ItemLedger.World)
                            {
                                result.Violation("MA-4", $"item seq {r.Seq}: a list row must go from a character into a listing, not {r.From} to {r.To}");
                                continue;
                            }

                            var listed = TrailOf(r.To);
                            listed.Seller = r.From;
                            listed.Listed += r.Count;
                            listed.Net += r.Count;
                            Touch(listed, in r);
                            break;
                        case ItemMoveKind.Buy:
                        case ItemMoveKind.Return:
                            if (!IsListing(r.From) || r.FromStack != r.From || IsListing(r.To) || r.To == ItemLedger.World)
                            {
                                result.Violation(
                                    "MA-4",
                                    $"item seq {r.Seq}: a {ItemMoveKind.NameOf(r.Kind)} row must go from a listing to a character, not {r.From} to {r.To}");
                                continue;
                            }

                            var emptied = TrailOf(r.From);
                            emptied.Net -= r.Count;
                            emptied.Collector = r.To;
                            emptied.Bought |= r.Kind == ItemMoveKind.Buy;
                            emptied.ReturnedHome |= r.Kind == ItemMoveKind.Return;
                            Touch(emptied, in r);
                            break;
                        case ItemMoveKind.Vendor:
                            if (IsListing(r.From) || r.From == ItemLedger.World || r.To != ItemLedger.World)
                            {
                                result.Violation("MA-5", $"item seq {r.Seq}: a vendor row must go from a character to World, not {r.From} to {r.To}");
                            }

                            // A sale's rows are consecutive (one ItemTransfer): a row naming another credit row closes the sale before it.
                            if (inSale && r.Ref == open.CreditSeq)
                            {
                                open.Units += r.Count;
                                open.One &= open.Seller == r.From && open.Template == r.Template && open.Mk == r.Mk;
                            }
                            else
                            {
                                if (inSale)
                                {
                                    CloseSale(ref credits, in open);
                                }

                                open = new VendorSale
                                {
                                    CreditSeq = r.Ref, FirstItem = r.Seq, Seller = r.From, Template = r.Template, Mk = r.Mk, Units = r.Count, One = true,
                                };
                                inSale = true;
                            }

                            break;
                    }
                }
            }

            if (inSale)
            {
                CloseSale(ref credits, in open);
            }

            // The rest of the credit ledger: its market rows filed, and every vendor credit no item row named.
            if (hasVendor)
            {
                passed[vendor.Seq] = vendor;
            }

            while (NextVendorCredit(ref credits, out vendor))
            {
                passed[vendor.Seq] = vendor;
            }

            foreach (var (seq, unnamed) in passed)
            {
                result.Violation("MA-5", $"vendor credit seq {seq} paid {unnamed.Amount} and no item row names it");
            }
        }
        finally
        {
            credits.Dispose();
        }

        // MA-1, MA-6: every stored listing on its own, and the caps.
        var cities = new HashSet<(ushort, ushort)>();
        foreach (var c in board.Cities)
        {
            cities.Add((c.Realm, c.City));
        }

        var walletIndex = dbe.GetIndexRef<Wallet, long>(w => w.Owner);
        var listedBy = new Dictionary<long, int>();
        var reservedBy = new Dictionary<long, int>();
        foreach (var (id, l) in stored)
        {
            result.Listings++;
            result.EscrowUnits += l.Quantity;
            switch (l.State)
            {
                case ListingState.Listed:
                    result.Listed++;
                    listedBy[l.Seller] = (listedBy.TryGetValue(l.Seller, out var n) ? n : 0) + 1;
                    if (l.Collector != 0)
                    {
                        result.Violation("MA-1", $"listing {id}: listed and collected by {l.Collector}");
                    }

                    break;
                case ListingState.Sold:
                    result.Sold++;
                    reservedBy[l.Collector] = (reservedBy.TryGetValue(l.Collector, out var m) ? m : 0) + 1;
                    if (l.Collector == 0 || l.Collector == l.Seller)
                    {
                        result.Violation("MA-1", $"listing {id}: sold to {l.Collector}, its seller {l.Seller}");
                    }

                    break;
                case ListingState.Returned:
                    result.Returned++;
                    listedBy[l.Seller] = (listedBy.TryGetValue(l.Seller, out var held) ? held : 0) + 1;
                    if (l.Collector != l.Seller)
                    {
                        result.Violation("MA-1", $"listing {id}: returned to {l.Collector}, not its seller {l.Seller}");
                    }

                    break;
                default:
                    result.Violation("MA-1", $"listing {id}: state {l.State}");
                    break;
            }

            if (l.Quantity is < 1 or > ItemLedger.StackMax)
            {
                result.Violation("MA-1", $"listing {id}: quantity {l.Quantity}, outside 1..{ItemLedger.StackMax}");
            }

            var escrow = trails.TryGetValue(id, out var trail) ? trail.Net : 0;
            if (l.Quantity != escrow)
            {
                result.Violation("MA-1", $"listing {id}: holds {l.Quantity}, its escrow replays to {escrow}");
            }

            if (trail != null && (trail.Template != l.Template || trail.Mk != l.Mk || trail.LastSeq != l.LastSeq || trail.Rows != l.Rows))
            {
                result.Violation("MA-1", $"listing {id}: {l.Template} Mk {l.Mk} last moved at seq {l.LastSeq} in {l.Rows} rows, its rows say {trail.Template} Mk "
                    + $"{trail.Mk} at seq {trail.LastSeq} in {trail.Rows}");
            }

            using (var wallet = tx.EnumerateIndex<Wallet, long>(walletIndex, l.Seller, l.Seller))
            {
                if (!wallet.MoveNext())
                {
                    result.Violation("MA-1", $"listing {id}: its seller {l.Seller} has no wallet");
                }
            }

            if (cities.Count > 0 && !cities.Contains((l.Realm, l.City)))
            {
                result.Violation("MA-1", $"listing {id}: city {l.City} of realm {l.Realm} has no bazaar");
            }
        }

        foreach (var (seller, n) in listedBy)
        {
            if (n > TatooineData.MaxListings)
            {
                result.Violation("MA-6", $"seller {seller}: {n} listings listed or returned, above {TatooineData.MaxListings}");
            }
        }

        foreach (var (collector, n) in reservedBy)
        {
            if (n > TatooineData.MaxReserved)
            {
                result.Violation("MA-6", $"collector {collector}: {n} purchases reserved, above {TatooineData.MaxReserved}");
            }
        }

        // MA-2, MA-3, MA-4: every listing ever listed against its rows, the stored ones and the destroyed ones alike.
        var ever = new HashSet<long>(trails.Keys);
        ever.UnionWith(stored.Keys);
        foreach (var id in ever)
        {
            var live = stored.TryGetValue(id, out var l);
            trails.TryGetValue(id, out var trail);
            if (trail == null || trail.Listed == 0)
            {
                result.Violation("MA-4", $"listing {id}: no list row ever filled it");
                continue;
            }

            if (!live && trail.Net != 0)
            {
                result.Violation("MA-4", $"listing {id}: destroyed with {trail.Net} units in escrow");
            }

            var sold = (live && l.State == ListingState.Sold) || trail.Bought;
            var saleRows = sales.TryGetValue(id, out var s) ? s : null;
            var taxRows = taxes.TryGetValue(id, out var x) ? x : null;
            if (sold)
            {
                if (saleRows is not { Count: 1 })
                {
                    result.Violation("MA-2", $"listing {id}: sold, with {saleRows?.Count ?? 0} sale rows");
                }
                else
                {
                    var sale = saleRows[0];
                    var buyer = live ? l.Collector : trail.Collector;
                    if (sale.From != buyer || sale.To != trail.Seller)
                    {
                        result.Violation("MA-2", $"listing {id}: its sale moved {sale.From} to {sale.To}, its buyer is {buyer} and its seller {trail.Seller}");
                    }

                    if (live && sale.Amount != l.UnitPrice * l.Quantity)
                    {
                        result.Violation("MA-2", $"listing {id}: sold for {sale.Amount}, its price is {l.UnitPrice * l.Quantity}");
                    }

                    // Destroyed, its price is gone with it: what is left is that a whole listing was sold, at a whole unit price.
                    if (!live && sale.Amount % trail.Listed != 0)
                    {
                        result.Violation("MA-2", $"listing {id}: sold for {sale.Amount}, not a whole price for its {trail.Listed} units");
                    }

                    var tax = TatooineData.SalesTax(sale.Amount);
                    var taxed = taxRows?.Count ?? 0;
                    if ((tax > 0 && (taxed != 1 || taxRows[0].Amount != tax || taxRows[0].From != trail.Seller || taxRows[0].To != EconomyLedger.World))
                        || (tax == 0 && taxed != 0))
                    {
                        result.Violation("MA-2", $"listing {id}: a sale of {sale.Amount} owes a tax of {tax}, its tax rows are {taxed}");
                    }
                }
            }
            else if (saleRows != null || taxRows != null)
            {
                result.Violation("MA-2", $"listing {id}: not sold, with {saleRows?.Count ?? 0} sale and {taxRows?.Count ?? 0} tax rows");
            }

            var feeRows = fees.TryGetValue(id, out var f) ? f : null;
            if (feeRows is not { Count: 1 })
            {
                result.Violation("MA-3", $"listing {id}: {feeRows?.Count ?? 0} list fee rows, not one");
            }
            else if ((live && feeRows[0].Amount != l.Fee) || feeRows[0].From != trail.Seller || feeRows[0].To != EconomyLedger.World)
            {
                result.Violation("MA-3", $"listing {id}: its fee row moved {feeRows[0].Amount} from {feeRows[0].From}, its fee is {(live ? l.Fee : feeRows[0].Amount)} from {trail.Seller}");
            }
            else
            {
                // The fee the rule charges: from the stored price while the listing stands, from its sale once sold and destroyed; a returned one, collected,
                // left no price behind.
                var total = live ? l.UnitPrice * l.Quantity : sold && saleRows is { Count: 1 } ? saleRows[0].Amount : 0;
                if (total > 0 && feeRows[0].Amount != TatooineData.ListFee(total))
                {
                    result.Violation("MA-3", $"listing {id}: charged {feeRows[0].Amount}, the fee on {total} is {TatooineData.ListFee(total)}");
                }
            }
        }

        foreach (var id in sales.Keys)
        {
            if (!ever.Contains(id))
            {
                result.Violation("MA-2", $"sale rows name {id}, which was never listed");
            }
        }

        foreach (var id in taxes.Keys)
        {
            if (!ever.Contains(id))
            {
                result.Violation("MA-2", $"tax rows name {id}, which was never listed");
            }
        }

        foreach (var id in fees.Keys)
        {
            if (!ever.Contains(id))
            {
                result.Violation("MA-3", $"a list fee row names {id}, which was never listed");
            }
        }

        // MA-7: the published board against the stored listed set, as of the tick it was built on. A difference made after that tick is not drift.
        var shown = new HashSet<long>();
        foreach (var city in board.Cities)
        {
            foreach (var v in city.Listings)
            {
                shown.Add(v.Id);
                if (stored.TryGetValue(v.Id, out var l))
                {
                    if (l.State == ListingState.Listed)
                    {
                        if (l.UnitPrice != v.UnitPrice || l.Quantity != v.Quantity || l.City != v.City || l.Realm != v.Realm || v.City != city.City)
                        {
                            result.Violation("MA-7", $"listing {v.Id}: the board shows {v.Quantity} at {v.UnitPrice} in city {v.City}, it is {l.Quantity} at "
                                + $"{l.UnitPrice} in city {l.City}");
                        }
                    }
                    else if (l.ClosedTick <= board.WorldTick)
                    {
                        result.Violation("MA-7", $"listing {v.Id}: the board of tick {board.WorldTick} shows it listed, it closed at tick {l.ClosedTick}");
                    }
                }
                else if (!trails.TryGetValue(v.Id, out var gone) || gone.LastTick <= board.WorldTick)
                {
                    result.Violation("MA-7", $"listing {v.Id}: the board of tick {board.WorldTick} shows it, and it was gone by then");
                }
            }
        }

        if (board.Version > 0)
        {
            foreach (var (id, l) in stored)
            {
                if (l.State == ListingState.Listed && !shown.Contains(id) && l.ListedTick <= board.WorldTick)
                {
                    result.Violation("MA-7", $"listing {id}: listed at tick {l.ListedTick}, missing from the board of tick {board.WorldTick}");
                }
            }
        }

        result.Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return result;

        static void Touch(Trail t, in ItemLedgerEntry r)
        {
            t.Template = r.Template;
            t.Mk = r.Mk;
            t.LastTick = r.WorldTick;
            t.LastSeq = r.Seq;
            t.Rows++;
        }

        static void Add(Dictionary<long, List<LedgerEntry>> map, long key, in LedgerEntry r)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }

            list.Add(r);
        }
    }
}
