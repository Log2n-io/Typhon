using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SwgTatooine.Bots;

/// <summary>What kind of thing an item is, to the bazaar's plan: the catalogue's category (<c>material</c>, <c>core</c>, <c>addon</c>), anything else Other.</summary>
public enum MarketCategory : byte
{
    None = 0,
    Material,
    Core,
    Addon,
    Other,
}

/// <summary>One stack an inventory holds, as <c>GET /swg/inventory</c> said: its template, asset, category, Mk and count.</summary>
public readonly record struct HeldStack(ushort Template, string Item, MarketCategory Category, int Mk, int Count);

/// <summary>A bazaar buy collected into the character: the listing it came from (the <c>buy</c> row's <c>ref</c>), and what and how many.</summary>
public readonly record struct WonListing(ulong Listing, ushort Template, string Item, int Mk, int Count);

/// <summary>One listing of a browse page (bazaar.md § 5): <c>{"id","city","item","category","mk","qty","unit","total","seller",…}</c>.</summary>
public readonly record struct BrowseRow(ulong Id, int City, string Item, MarketCategory Category, int Mk, int Quantity, long Unit, long Total, ulong Seller);

/// <summary>The tradeable templates by asset, for an answer that names an item by its asset only.</summary>
/// <remarks>
/// <b>The server's ids, copied: <c>ItemCatalog</c>'s, which are append-only for ever</b> (a ledger row stores them), so the copy cannot go stale — only
/// grow. This project references no server code, as <c>Perception</c>'s copies of two ack reasons do not. An answer that carries <c>template</c> is
/// believed first.
/// </remarks>
public static class MarketCatalog
{
    /// <summary>Templates 1-19, by id: the materials, the cores and the add-ons — what drops, and so what a bot holds to trade.</summary>
    private static readonly string[] Tradeable =
    [
        "mat_ore", "mat_water", "mat_metal", "mat_crystal", "mat_gas", "mat_chemical", "mat_fiber", "mat_hide", "mat_bone", "mat_gland", "mat_venom",
        "core_ranged", "core_melee", "core_armor", "core_propulsion",
        "addon_weapon", "addon_armor", "addon_tuning", "addon_additive",
    ];

    /// <summary>The template of an asset; 0 for one this table does not hold.</summary>
    public static ushort TemplateOf(string asset)
    {
        var i = Array.IndexOf(Tradeable, asset);
        return i < 0 ? (ushort)0 : (ushort)(i + 1);
    }

    /// <summary>A row's <c>template</c> when the answer carries one, else its asset's.</summary>
    public static ushort TemplateOf(JsonElement row, string asset) =>
        row.TryGetProperty("template", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetUInt16() : TemplateOf(asset);

    /// <summary>A category's name as the server writes it (<c>ItemCategory.NameOf</c>).</summary>
    public static MarketCategory CategoryOf(string name) => name switch
    {
        "material" => MarketCategory.Material,
        "core" => MarketCategory.Core,
        "addon" => MarketCategory.Addon,
        null => MarketCategory.None,
        _ => MarketCategory.Other,
    };
}

/// <summary>The shapes the market's answers share.</summary>
public static class MarketJson
{
    /// <summary>A 64-bit id, which the server writes as a string (past a JavaScript number) — or a number, read the same.</summary>
    public static ulong Id(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? ulong.Parse(e.GetString() ?? "0", CultureInfo.InvariantCulture)
        : e.GetUInt64();

    /// <summary>An amount of credits, whole or not.</summary>
    public static double Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0d;

    /// <summary>The credits a <c>GET /swg/wallet</c> answer says the character holds.</summary>
    public static long Credits(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (long)Number(doc.RootElement, "credits");
    }

    /// <summary>A browse page's listings (<c>GET /swg/market?…</c>), in the order sent.</summary>
    public static List<BrowseRow> Browse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var rows = new List<BrowseRow>();
        foreach (var l in doc.RootElement.GetProperty("listings").EnumerateArray())
        {
            rows.Add(new BrowseRow(
                Id(l.GetProperty("id")),
                l.TryGetProperty("city", out var city) ? city.GetInt32() : 0,
                l.GetProperty("item").GetString(),
                MarketCatalog.CategoryOf(l.TryGetProperty("category", out var c) ? c.GetString() : null),
                l.GetProperty("mk").GetInt32(),
                l.GetProperty("qty").GetInt32(),
                (long)Number(l, "unit"),
                (long)Number(l, "total"),
                l.TryGetProperty("seller", out var s) ? Id(s) : 0));
        }

        return rows;
    }
}

/// <summary>
/// What <c>GET /swg/market/mine</c> last said (bazaar.md § 5): the character's listings still listed, and the goods reserved for it — bought
/// elsewhere, or a listing returned.
/// </summary>
/// <param name="ListedIds">The listings still listed.</param>
/// <param name="Reserved">How many are reserved for it.</param>
public sealed record MarketMine(ulong[] ListedIds, int Reserved)
{
    public static MarketMine None { get; } = new([], 0);

    /// <summary>Listings still listed.</summary>
    public int Listed => ListedIds.Length;

    /// <summary>Whether it names anything: a browser re-reads it on each <c>items</c> revision while it does.</summary>
    public bool Empty => ListedIds.Length == 0 && Reserved == 0;

    /// <summary>Reads an answer: <c>{"listed":[{"id",…}],"reserved":[{"id",…}],"caps":{…}}</c>.</summary>
    public static MarketMine Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var listed = root.GetProperty("listed");
        var ids = new ulong[listed.GetArrayLength()];
        var i = 0;
        foreach (var l in listed.EnumerateArray())
        {
            ids[i++] = MarketJson.Id(l.GetProperty("id"));
        }

        return new MarketMine(ids, root.GetProperty("reserved").GetArrayLength());
    }
}

/// <summary>What <c>GET /swg/market/prices</c> last said: each (item, Mk)'s market value, credits a unit — what a bot prices a listing and a buy by.</summary>
public sealed class MarketPrices
{
    private readonly Dictionary<(string Item, int Mk), double> _value = [];

    public static MarketPrices None { get; } = new();

    /// <summary>Entries read.</summary>
    public int Count => _value.Count;

    /// <summary>The market value of one unit, credits; 0 for an (item, Mk) the answer did not name — which a bot neither lists nor buys.</summary>
    public double ValueOf(string item, int mk) => item != null && _value.TryGetValue((item, mk), out var v) ? v : 0d;

    /// <summary>Sets one value: a test's prices, or a parse's.</summary>
    public MarketPrices With(string item, int mk, double value)
    {
        _value[(item, mk)] = value;
        return this;
    }

    /// <summary>Reads an answer: <c>{"prices":[{"item","mk","value",…}]}</c> — or the array alone.</summary>
    public static MarketPrices Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("prices");
        var prices = new MarketPrices();
        foreach (var p in rows.EnumerateArray())
        {
            prices.With(p.GetProperty("item").GetString(), p.GetProperty("mk").GetInt32(), MarketJson.Number(p, "value"));
        }

        return prices;
    }
}
