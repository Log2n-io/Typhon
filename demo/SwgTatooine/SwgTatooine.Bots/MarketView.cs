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
public readonly record struct HeldStack(ushort Template, string Item, MarketCategory Category, int Mk, int Count)
{
    /// <summary>The stack's id, what a discard names; 0 when unread.</summary>
    public ulong Id { get; init; }
}

/// <summary>
/// An item with stats in a bot's inventory (crafting-build.md § 2.1): its id, template, the slot it sits in, the equip slot it is worn in (0 for none: a part,
/// a potion), a part's grade per mille, its asset and its Mk — what a crafter picks parts by and prices its surplus with.
/// </summary>
public readonly record struct GearItem(ulong Id, ushort Template, int Slot, int Wears, int Grade = 0, string Item = null, int Mk = 0)
{
    /// <summary>Whether it is worn: in an equip slot, out of the grid.</summary>
    public bool Worn => Slot > 100;

    /// <summary>Its condition and max (crafting-catalog.md § 9.1), its repairs used and whether it is broken (§ 9.2); a new item's when unread.</summary>
    public int Condition { get; init; } = 1;

    public int MaxCondition { get; init; } = 1;

    public int Repairs { get; init; }

    public bool Broken { get; init; }

    /// <summary>How many it is: 1, or a crate's count (crafting-catalog.md § 11.2) — listed and sold whole, never worn nor crafted with.</summary>
    public int Count { get; init; } = 1;

    /// <summary>What it packs with (the server's <c>packKey</c>): equal for identical pristine items; null for one that does not pack.</summary>
    public string PackKey { get; init; }

    /// <summary>What it is worth in a fight (<see cref="MarketJson.PowerOf"/>): a weapon's damage a second, a piece's protection; 0 when unread.</summary>
    public int Power { get; init; }

    /// <summary>Whether it is a crate.</summary>
    public bool Crate => Count > 1;

    /// <summary>Worn down (crafting-build.md § 6): broken, or under 20 % of its max — time to repair it or replace it.</summary>
    public bool WornDown => Broken || (MaxCondition > 0 && Condition * 5L < MaxCondition);
}

/// <summary>A standard-issue item the vendor sells (<c>GET /swg/market/standard</c>): its template, asset, price and the slot it is worn in.</summary>
public readonly record struct StandardOffer(ushort Template, string Item, long Price, int Wears)
{
    /// <summary>A vendor material's price a unit, milli-credits (a quantity's price is its total rounded down); 0 for an item with stats.</summary>
    public long PriceMilli { get; init; }

    /// <summary>What the typical craft it sells is worth in a fight (<see cref="MarketJson.PowerOf"/>); 0 for units, or unread.</summary>
    public int Power { get; init; }
}

/// <summary>A bazaar buy collected into the character: the listing it came from (the <c>buy</c> row's <c>ref</c>), and what and how many.</summary>
public readonly record struct WonListing(ulong Listing, ushort Template, string Item, int Mk, int Count);

/// <summary>One listing of a browse page (bazaar.md § 5): <c>{"id","city","item","category","mk","qty","unit","total","seller",…}</c>.</summary>
public readonly record struct BrowseRow(ulong Id, int City, string Item, MarketCategory Category, int Mk, int Quantity, long Unit, long Total, ulong Seller)
{
    /// <summary>An item with stats' condition, its max and whether it is broken (crafting-catalog.md § 9); a new item's for units.</summary>
    public int Condition { get; init; } = 1;

    public int MaxCondition { get; init; } = 1;

    public bool Broken { get; init; }

    /// <summary>What it is worth in a fight, as <see cref="GearItem.Power"/>; 0 for units, or unread.</summary>
    public int Power { get; init; }

    /// <summary>Broken, or under 20 % of its max: not worth buying to wear (crafting-build.md § 6, Gear).</summary>
    public bool WornDown => Broken || (MaxCondition > 0 && Condition * 5L < MaxCondition);
}

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

    /// <summary>The asset of a tradeable template; <c>"unknown"</c> for one this table does not hold.</summary>
    public static string AssetOf(ushort template) => template >= 1 && template <= Tradeable.Length ? Tradeable[template - 1] : "unknown";

    /// <summary>The template of a tradeable asset; 0 for one this table does not hold.</summary>
    public static ushort TemplateOf(string asset)
    {
        var i = Array.IndexOf(Tradeable, asset);
        return i < 0 ? (ushort)0 : (ushort)(i + 1);
    }

    /// <summary>The balance model's value of a material unit by Mk (<c>MarketValues</c>, bazaar.md § 2.3), credits.</summary>
    private static readonly double[] MaterialValue = [0d, 1.7d, 2.8d, 4.5d, 7.2d, 11.4d];

    /// <summary>... of a core by Mk; an add-on's is a core's [EST].</summary>
    private static readonly double[] CoreValue = [0d, 726d, 1_549d, 3_099d, 5_950d, 11_106d];

    /// <summary>A repair kit's value, credits (the server's <c>MarketValues.KitMilli</c>).</summary>
    public const double KitValue = 150d;

    /// <summary>A stim's value, credits (the server's <c>MarketValues.StimMilli</c>): no Mk.</summary>
    public const double StimValue = 231.2d;

    /// <summary>
    /// The model's market value of one unit, credits — what <c>/swg/market/prices</c> says of any (item, Mk) it names — or 0 for an item without one.
    /// </summary>
    /// <remarks>
    /// <b>The bot's copy of <c>MarketValues</c>, for what the prices do not name yet.</b> The board prices a held (item, Mk) only when it is next
    /// published, and it is published on a listing's change or a sale: on a board nobody has listed on, nothing a bot holds is priced, and a bot that
    /// waited for a price would never list the first core. The server's answer is believed first.
    /// </remarks>
    public static double ModelValue(string asset, int mk)
    {
        // A repair kit: no Mk, one value (the server's MarketValues.KitMilli).
        if (asset != null && asset.StartsWith("kit_", StringComparison.Ordinal))
        {
            return mk == 0 ? KitValue : 0d;
        }

        if (asset == GearPlan.StimItem)
        {
            return mk == 0 ? StimValue : 0d;
        }

        if (asset == null || mk is < 1 or > 5 || TemplateOf(asset) == 0)
        {
            return 0d;
        }

        return asset.StartsWith("mat_", StringComparison.Ordinal) ? MaterialValue[mk] : CoreValue[mk];
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

    /// <summary>
    /// What an item with stats is worth in a fight, from its <c>stats</c> (crafting-catalog.md § 4-5): a weapon's damage a second, × 1 000 — its damage over
    /// its delay; an armour piece's protection, the mean of its kinetic and energy. Compared within a slot only; 0 for anything else.
    /// </summary>
    public static int PowerOf(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (stats.TryGetProperty("damage", out var damage) && stats.TryGetProperty("delayMs", out var delay) && delay.GetInt32() > 0)
        {
            return (int)(damage.GetInt64() * 1000L / delay.GetInt32());
        }

        return stats.TryGetProperty("kinetic", out var kinetic) && stats.TryGetProperty("energy", out var energy)
            ? (kinetic.GetInt32() + energy.GetInt32()) / 2
            : 0;
    }

    /// <summary>An amount of credits, whole or not.</summary>
    public static double Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0d;

    /// <summary>The credits a <c>GET /swg/wallet</c> answer says the character holds.</summary>
    public static long Credits(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (long)Number(doc.RootElement, "credits");
    }

    /// <summary>What the vendor sells (<c>GET /swg/market/standard</c>).</summary>
    public static List<StandardOffer> Standard(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var offers = new List<StandardOffer>();
        foreach (var o in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var wears = o.TryGetProperty("stats", out var s) && s.TryGetProperty("wears", out var w) ? w.GetInt32() : 0;
            offers.Add(new StandardOffer((ushort)o.GetProperty("template").GetInt32(), o.GetProperty("item").GetString(), (long)Number(o, "price"), wears)
            {
                PriceMilli = o.TryGetProperty("priceMilli", out var milli) ? milli.GetInt64() : 0,
                Power = s.ValueKind == JsonValueKind.Object ? PowerOf(s) : 0,
            });
        }

        return offers;
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
                l.TryGetProperty("seller", out var s) ? Id(s) : 0)
            {
                Condition = l.TryGetProperty("stats", out var st) && st.TryGetProperty("condition", out var cond) ? cond.GetInt32() : 1,
                MaxCondition = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("maxCondition", out var max) ? max.GetInt32() : 1,
                Broken = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("broken", out var broken) && broken.ValueKind == JsonValueKind.True,
                Power = PowerOf(st),
            });
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
    /// <summary>Its listings still listed, with what each holds: the item's category and Mk (what a visit takes off the board); empty when unread.</summary>
    public MineListing[] ListedRows { get; init; } = [];

    public static MarketMine None { get; } = new([], 0);

    /// <summary>Listings still listed.</summary>
    public int Listed => ListedIds.Length;

    /// <summary>Whether it names anything: a browser re-reads it on each <c>items</c> revision while it does.</summary>
    public bool Empty => ListedIds.Length == 0 && Reserved == 0;

    /// <summary>The city (region, a city plus one) each reserved good waits in, as the answer said; empty when it named none.</summary>
    public int[] ReservedCities { get; init; } = [];

    /// <summary>Each reserved good's asset, by the same index; null where the answer named none.</summary>
    public string[] ReservedItems { get; init; } = [];

    /// <summary>Whether a good of <paramref name="item"/> waits for it in a city other than <paramref name="region"/>: bought there, not yet fetched.</summary>
    public bool WaitsElsewhere(string item, int region)
    {
        for (var i = 0; item != null && i < ReservedItems.Length && i < ReservedCities.Length; i++)
        {
            if (ReservedItems[i] == item && ReservedCities[i] != region)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The goods reserved for it in one city — the only ones a collect there takes (the server refuses the rest, <c>MarketWrongCity</c>). The night of
    /// 2026-11-04: bots holding goods returned in another city collected nothing, were refused at every visit, and — "goods wait for it" — bought no gear.
    /// </summary>
    public int ReservedIn(int region)
    {
        var n = 0;
        foreach (var c in ReservedCities)
        {
            n += c == region && region != 0 ? 1 : 0;
        }

        return ReservedCities.Length == 0 ? 0 : n;
    }

    /// <summary>The first city other than <paramref name="region"/> holding goods reserved for it: where to fetch them; 0 for none, or none named.</summary>
    /// <remarks>Gear only — a weapon or an armour piece, what is worth the trip (Loïc, 2026-11-05); a returned core or material waits for a visit there.</remarks>
    public int ElsewhereThan(int region)
    {
        for (var i = 0; i < ReservedCities.Length; i++)
        {
            var item = i < ReservedItems.Length ? ReservedItems[i] : null;
            if (ReservedCities[i] != 0 && ReservedCities[i] != region && IsGear(item))
            {
                return ReservedCities[i];
            }
        }

        return 0;
    }

    // A weapon or an armour piece, by its asset.
    private static bool IsGear(string item) =>
        item != null && (item.StartsWith("armor_", StringComparison.Ordinal) || item.StartsWith("blaster_", StringComparison.Ordinal)
            || item.StartsWith("sword_", StringComparison.Ordinal) || item == "vibroknuckler");

    /// <summary>Reads an answer: <c>{"listed":[{"id",…}],"reserved":[{"id",…}],"caps":{…}}</c>.</summary>
    public static MarketMine Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var listed = root.GetProperty("listed");
        var ids = new ulong[listed.GetArrayLength()];
        var rows = new MineListing[ids.Length];
        var i = 0;
        foreach (var l in listed.EnumerateArray())
        {
            rows[i] = new MineListing(
                MarketJson.Id(l.GetProperty("id")),
                l.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() : null,
                MarketCatalog.CategoryOf(l.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String ? cat.GetString() : null),
                l.TryGetProperty("mk", out var mk) && mk.ValueKind == JsonValueKind.Number ? mk.GetInt32() : 0);
            ids[i] = rows[i].Id;
            i++;
        }

        var reserved = root.GetProperty("reserved");
        var cities = new int[reserved.GetArrayLength()];
        var items = new string[cities.Length];
        var r = 0;
        foreach (var g in reserved.EnumerateArray())
        {
            items[r] = g.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            cities[r++] = g.TryGetProperty("city", out var city) && city.ValueKind == JsonValueKind.Number ? city.GetInt32() : 0;
        }

        return new MarketMine(ids, cities.Length) { ReservedCities = cities, ReservedItems = items, ListedRows = rows };
    }
}

/// <summary>One of its listings as <c>/mine</c> says: the listing, its item's asset, category and Mk.</summary>
public readonly record struct MineListing(ulong Id, string Item, MarketCategory Category, int Mk);

/// <summary>What <c>GET /swg/market/prices</c> last said: each (item, Mk)'s market value, credits a unit — what a bot prices a listing and a buy by.</summary>
public sealed class MarketPrices
{
    private readonly Dictionary<(string Item, int Mk), double> _value = [];
    private readonly Dictionary<(string Item, int Mk), (int Listed, long Lowest, int Sales)> _board = [];

    public static MarketPrices None { get; } = new();

    /// <summary>Entries read.</summary>
    public int Count => _value.Count;

    /// <summary>
    /// The market value of one unit, credits: the answer's, or for an (item, Mk) it did not name the model's (<see cref="MarketCatalog.ModelValue"/>); 0 for
    /// an item that has none — which a bot neither lists nor buys.
    /// </summary>
    public double ValueOf(string item, int mk) => item != null && _value.TryGetValue((item, mk), out var v) ? v : MarketCatalog.ModelValue(item, mk);

    /// <summary>Sets one value: a test's prices, or a parse's.</summary>
    public MarketPrices With(string item, int mk, double value)
    {
        _value[(item, mk)] = value;
        return this;
    }

    /// <summary>Sets what the board holds of one (item, Mk), planet-wide: its listings and the lowest unit price (0 for none).</summary>
    public MarketPrices WithBoard(string item, int mk, int listed, long lowest, int sales = 0)
    {
        _board[(item, mk)] = (listed, lowest, sales);
        return this;
    }

    /// <summary>The listings of an (item, Mk) on the board, planet-wide; 0 when none, or unread.</summary>
    public int ListedOf(string item, int mk) => item != null && _board.TryGetValue((item, mk), out var b) ? b.Listed : 0;

    /// <summary>The lowest unit price it is listed at, planet-wide; 0 when it is not listed, or unread.</summary>
    public long LowestOf(string item, int mk) => item != null && _board.TryGetValue((item, mk), out var b) ? b.Lowest : 0;

    /// <summary>Its recent sales on the board, planet-wide (the answer's <c>sales</c>, the server's last sales kept); 0 when none, or unread.</summary>
    public int SalesOf(string item, int mk) => item != null && _board.TryGetValue((item, mk), out var b) ? b.Sales : 0;

    /// <summary>Reads an answer: <c>{"prices":[{"item","mk","value",…}]}</c> — or the array alone.</summary>
    public static MarketPrices Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var rows = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("prices");
        var prices = new MarketPrices();
        foreach (var p in rows.EnumerateArray())
        {
            var item = p.GetProperty("item").GetString();
            var mk = p.GetProperty("mk").GetInt32();
            prices.With(item, mk, MarketJson.Number(p, "value"));
            if (p.TryGetProperty("listed", out var listed) && listed.ValueKind == JsonValueKind.Number)
            {
                var sales = p.TryGetProperty("sales", out var sold) && sold.ValueKind == JsonValueKind.Number ? sold.GetInt32() : 0;
                prices.WithBoard(item, mk, listed.GetInt32(), (long)MarketJson.Number(p, "lowest"), sales);
            }
        }

        return prices;
    }
}
