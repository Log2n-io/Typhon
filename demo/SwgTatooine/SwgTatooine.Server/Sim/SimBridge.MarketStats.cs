using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SwgTatooine.Server;

/// <summary>The market's committed counters (bazaar.md § 9), read together: the book now, and what the desk has done since start.</summary>
/// <param name="Listed">Listings on the board now (<c>Listed</c>).</param>
/// <param name="Sold">Listings sold and not yet collected by their buyer.</param>
/// <param name="Returned">Listings cancelled or expired and not yet collected by their seller.</param>
/// <param name="EscrowUnits">Units held in listings, any state: what the <c>held</c> counters of <c>items</c> leave out.</param>
/// <param name="Sales">Buys applied since start.</param>
/// <param name="Volume">Credits paid buyer → seller since start, before tax.</param>
/// <param name="ListFees">List fees paid to World since start.</param>
/// <param name="SalesTax">Sales tax paid to World since start.</param>
/// <param name="VendorPaid">Credits the vendor minted since start.</param>
/// <param name="VendorUnits">Units the vendor destroyed since start.</param>
/// <param name="Listings">Listings made since start.</param>
/// <param name="Cancelled">Listings cancelled since start.</param>
/// <param name="Expired">Listings expired since start.</param>
/// <param name="Collected">Listings collected (and so destroyed) since start.</param>
/// <param name="CommitsFailed">Market transactions that did not commit.</param>
/// <param name="Aborted">Market transactions disposed uncommitted because a write failed after its validation passed — a bug.</param>
internal readonly record struct MarketCounters(
    long Listed,
    long Sold,
    long Returned,
    long EscrowUnits,
    long Sales,
    long Volume,
    long ListFees,
    long SalesTax,
    long VendorPaid,
    long VendorUnits,
    long Listings,
    long Cancelled,
    long Expired,
    long Collected,
    long CommitsFailed,
    long Aborted);

/// <summary>One listing on the board, as the stats read it off the desk's published snapshot.</summary>
/// <param name="City">Its region: city index + 1.</param>
internal readonly record struct MarketOpenListing(int City, ushort Template, byte Mk, int Quantity, long UnitPrice, long ListedTick);

/// <summary>One recent sale, off the desk's sale ring: what was sold, at what unit price.</summary>
internal readonly record struct MarketSale(ushort Template, byte Mk, long UnitPrice);

/// <summary>Everything the <c>market</c> section is written from, read once per answer.</summary>
internal sealed class MarketStatsInput
{
    public MarketCounters Counters { get; init; }

    /// <summary>Refusals since start, in <see cref="MarketStats.ReasonNames"/>' order.</summary>
    public long[] Refused { get; init; } = new long[MarketStats.ReasonNames.Length];

    /// <summary>Uncollected <c>Sold</c> and <c>Returned</c> listings per city, index for index with the world's cities; shorter reads 0.</summary>
    public int[] ReservedByCity { get; init; } = [];

    public MarketOpenListing[] Open { get; init; } = [];

    public MarketSale[] RecentSales { get; init; } = [];

    /// <summary>The world tick the open listings' ages are measured to; 0 when unknown, which reports every age as 0.</summary>
    public long NowTick { get; init; }

    public int TickRateHz { get; init; } = 1;

    /// <summary>An item's market value in milli-credits per unit, by (template, Mk); 0 for none.</summary>
    public Func<ushort, byte, long> ValueMilli { get; init; } = static (_, _) => 0;
}

/// <summary>
/// The <c>market</c> section of <c>GET /swg/stats</c> and the <c>economy.byKind</c> line (bazaar.md § 9, crafting-catalog § 15.6): the board by city, the
/// flows and their rates, the price index per item and Mk, how long the unsold wait — written from a <see cref="MarketStatsInput"/>, so the shape is
/// checked without a running desk.
/// </summary>
internal static class MarketStats
{
    /// <summary>The first market refusal reason (<c>MarketNotAtBazaar</c>); <see cref="ReasonNames"/>[i] is reason <c>ReasonFirst + i</c>.</summary>
    public const int ReasonFirst = 151;

    /// <summary>The market's refusal reasons 151-163 by name (bazaar.md § 3.1), then <c>NotInWorld</c> (142) as refused to a market command.</summary>
    public static readonly string[] ReasonNames =
    [
        "notAtBazaar", "wrongCity", "sold", "notEnough", "noItems", "badListing", "listingCap", "reservedCap", "noRoom", "notYours", "ownListing", "failed",
        "worthless", "notInWorld",
    ];

    /// <summary>Where a credit kind's money goes: minted from World, destroyed to World, or moved between two wallets.</summary>
    public enum Flow : byte
    {
        Faucet,
        Sink,
        Transfer,
    }

    /// <summary>Every credit kind (economy-foundation § 2, bazaar.md § 2.2), its name and its flow: the "faucets and sinks by channel" line.</summary>
    public static readonly (byte Kind, string Name, Flow Flow)[] CreditKinds =
    [
        (1, "grant", Flow.Faucet), (2, "mission", Flow.Faucet), (3, "tip", Flow.Transfer), (4, "sale", Flow.Transfer), (5, "tax", Flow.Sink),
        (6, "listfee", Flow.Sink), (7, "vendor", Flow.Faucet),
    ];

    /// <summary>The highest credit kind counted, so a by-kind array is <c>MaxKind + 1</c> long.</summary>
    public const int MaxKind = 7;

    /// <summary>Credits minted (faucets) and destroyed (sinks) since start, from the credits moved per kind.</summary>
    public static (long Faucets, long Sinks) FaucetsAndSinks(ReadOnlySpan<long> byKind)
    {
        long faucets = 0, sinks = 0;
        foreach (var (kind, _, flow) in CreditKinds)
        {
            var credits = kind < byKind.Length ? byKind[kind] : 0;
            faucets += flow == Flow.Faucet ? credits : 0;
            sinks += flow == Flow.Sink ? credits : 0;
        }

        return (faucets, sinks);
    }

    /// <summary><c>"byKind":{"grant":n,…}</c>: the credits moved per kind since start, every kind named, 0 included.</summary>
    public static void WriteByKind(StringBuilder j, ReadOnlySpan<long> byKind)
    {
        j.Append("\"byKind\":{");
        for (var i = 0; i < CreditKinds.Length; i++)
        {
            var (kind, name, _) = CreditKinds[i];
            j.Append(i == 0 ? "\"" : ",\"").Append(name).Append("\":").Append((kind < byKind.Length ? byKind[kind] : 0).ToString(CultureInfo.InvariantCulture));
        }

        j.Append('}');
    }

    /// <summary>
    /// Writes the <c>market</c> object: counters, <c>byCity</c>, <c>refused</c>, then <paramref name="rates"/> (pre-written, comma-led), <c>prices</c> and
    /// <c>unsoldAgeS</c>.
    /// </summary>
    /// <param name="cityNames">The cities as a person sees them, index for index with the world's; region = index + 1.</param>
    public static void Write(StringBuilder j, MarketStatsInput m, IReadOnlyList<string> cityNames, string rates)
    {
        var inv = CultureInfo.InvariantCulture;
        var c = m.Counters;
        j.Append("{\"listed\":").Append(c.Listed.ToString(inv))
            .Append(",\"sold\":").Append(c.Sold.ToString(inv))
            .Append(",\"returned\":").Append(c.Returned.ToString(inv))
            .Append(",\"escrowUnits\":").Append(c.EscrowUnits.ToString(inv));

        // The board by city: what is listed there and its worth, and what waits there to be collected.
        var listed = new long[cityNames.Count];
        var listedValue = new long[cityNames.Count];
        foreach (var l in m.Open)
        {
            var city = l.City - 1;
            if ((uint)city < (uint)listed.Length)
            {
                listed[city]++;
                listedValue[city] += l.UnitPrice * l.Quantity;
            }
        }

        j.Append(",\"byCity\":[");
        for (var i = 0; i < cityNames.Count; i++)
        {
            var reserved = i < m.ReservedByCity.Length ? m.ReservedByCity[i] : 0;
            j.Append(i == 0 ? "{\"city\":" : ",{\"city\":").Append((i + 1).ToString(inv))
                .Append(",\"name\":\"").Append(cityNames[i]).Append('"')
                .Append(",\"listed\":").Append(listed[i].ToString(inv))
                .Append(",\"reserved\":").Append(reserved.ToString(inv))
                .Append(",\"listedValue\":").Append(listedValue[i].ToString(inv)).Append('}');
        }

        j.Append(']')
            .Append(",\"sales\":").Append(c.Sales.ToString(inv))
            .Append(",\"volume\":").Append(c.Volume.ToString(inv))
            .Append(",\"listFees\":").Append(c.ListFees.ToString(inv))
            .Append(",\"salesTax\":").Append(c.SalesTax.ToString(inv))
            .Append(",\"vendorPaid\":").Append(c.VendorPaid.ToString(inv))
            .Append(",\"vendorUnits\":").Append(c.VendorUnits.ToString(inv))
            .Append(",\"listings\":").Append(c.Listings.ToString(inv))
            .Append(",\"cancelled\":").Append(c.Cancelled.ToString(inv))
            .Append(",\"expired\":").Append(c.Expired.ToString(inv))
            .Append(",\"collected\":").Append(c.Collected.ToString(inv))
            .Append(",\"commitsFailed\":").Append(c.CommitsFailed.ToString(inv))
            .Append(",\"aborted\":").Append(c.Aborted.ToString(inv));

        j.Append(",\"refused\":{");
        for (var i = 0; i < ReasonNames.Length; i++)
        {
            j.Append(i == 0 ? "\"" : ",\"").Append(ReasonNames[i]).Append("\":").Append((i < m.Refused.Length ? m.Refused[i] : 0).ToString(inv));
        }

        j.Append('}').Append(rates);
        WritePrices(j, m);
        WriteUnsoldAge(j, m);
        j.Append('}');
    }

    /// <summary>
    /// <c>"prices":{"core_armor":[{"mk","sales","median","min","max","listed","lowest","value"}]}</c>: every (item, Mk) with a recent sale or a listing.
    /// Prices in credits per unit; 0 means none (no sale, no listing). <c>value</c> is the market value in credits — the index's denominator.
    /// </summary>
    private static void WritePrices(StringBuilder j, MarketStatsInput m)
    {
        var inv = CultureInfo.InvariantCulture;
        var groups = new SortedDictionary<int, (List<long> Prices, long Listed, long Lowest)>();
        foreach (var s in m.RecentSales)
        {
            var key = (s.Template << 8) | s.Mk;
            var g = groups.TryGetValue(key, out var held) ? held : ([], 0, 0);
            g.Prices.Add(s.UnitPrice);
            groups[key] = g;
        }

        foreach (var l in m.Open)
        {
            var key = (l.Template << 8) | l.Mk;
            var g = groups.TryGetValue(key, out var held) ? held : ([], 0, 0);
            groups[key] = (g.Prices, g.Listed + 1, g.Lowest == 0 ? l.UnitPrice : Math.Min(g.Lowest, l.UnitPrice));
        }

        j.Append(",\"prices\":{");
        var lastTemplate = -1;
        foreach (var (key, g) in groups)
        {
            var template = (ushort)(key >> 8);
            var mk = (byte)(key & 0xFF);
            if (template != lastTemplate)
            {
                j.Append(lastTemplate < 0 ? "\"" : "],\"").Append(ItemCatalog.TryGet(template, out var t) ? t.Asset : $"t{template}").Append("\":[");
                lastTemplate = template;
            }
            else
            {
                j.Append(',');
            }

            g.Prices.Sort();
            var n = g.Prices.Count;
            j.Append("{\"mk\":").Append(mk.ToString(inv))
                .Append(",\"sales\":").Append(n.ToString(inv))
                .Append(",\"median\":").Append((n == 0 ? 0 : Rank(g.Prices, 0.5)).ToString(inv))
                .Append(",\"min\":").Append((n == 0 ? 0 : g.Prices[0]).ToString(inv))
                .Append(",\"max\":").Append((n == 0 ? 0 : g.Prices[^1]).ToString(inv))
                .Append(",\"listed\":").Append(g.Listed.ToString(inv))
                .Append(",\"lowest\":").Append(g.Lowest.ToString(inv))
                .Append(",\"value\":").Append((m.ValueMilli(template, mk) / 1000d).ToString("0.###", inv)).Append('}');
        }

        j.Append(lastTemplate < 0 ? "}" : "]}");
    }

    /// <summary><c>"unsoldAgeS":{"p50","p90","max"}</c>: how long the listings on the board have waited, seconds of world time.</summary>
    private static void WriteUnsoldAge(StringBuilder j, MarketStatsInput m)
    {
        var inv = CultureInfo.InvariantCulture;
        var ages = new List<long>(m.Open.Length);
        if (m.NowTick > 0)
        {
            var hz = Math.Max(1, m.TickRateHz);
            foreach (var l in m.Open)
            {
                ages.Add(Math.Max(0, m.NowTick - l.ListedTick) / hz);
            }
        }

        ages.Sort();
        var none = ages.Count == 0;
        j.Append(",\"unsoldAgeS\":{\"p50\":").Append((none ? 0 : Rank(ages, 0.5)).ToString(inv))
            .Append(",\"p90\":").Append((none ? 0 : Rank(ages, 0.9)).ToString(inv))
            .Append(",\"max\":").Append((none ? 0 : ages[^1]).ToString(inv)).Append('}');
    }

    /// <summary>The nearest-rank percentile of a sorted, non-empty list: a value it holds, the lower middle for an even median.</summary>
    public static long Rank(List<long> sorted, double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}

/// <summary>The market's numbers for <c>/swg/stats</c> and the exit line (bazaar.md § 9).</summary>
public sealed partial class SimBridge
{
    // ---- What the desk exposes (bazaar.md § 12, agent A's counters). Until they are wired, the market reads as empty: the shape is the contract.

    /// <summary>The desk's committed counters.</summary>
    private MarketCounters ReadMarketCounters() => default;

    /// <summary>Credits moved per credit kind since start, committed only, indexed by kind.</summary>
    private void ReadCreditsByKind(Span<long> into) => into.Clear();

    /// <summary>The counters, the refusals, the board as published, the recent sales and the world tick, read once for one answer.</summary>
    private MarketStatsInput GatherMarketStats() => new() { Counters = ReadMarketCounters(), TickRateHz = Math.Max(1, _config.TickRateHz) };

    // ---- Written from them.

    /// <summary>The cities as a person sees them, index for index with the world's.</summary>
    private string[] CityNames()
    {
        var names = new string[_index.Cities.Count];
        for (var c = 0; c < names.Length; c++)
        {
            names[c] = c < CityDisplayNames.Length ? CityDisplayNames[c] : _map.Cities[c].Name;
        }

        return names;
    }

    /// <summary>The <c>economy</c> object's by-kind tail: <c>,"byKind":{…},"faucets":n,"sinks":n,"faucetsPerMin":…,"sinksPerMin":…</c>.</summary>
    private void AppendCreditsByKind(StringBuilder j, ReadOnlySpan<double> r10, ReadOnlySpan<double> r60)
    {
        Span<long> byKind = stackalloc long[MarketStats.MaxKind + 1];
        ReadCreditsByKind(byKind);
        var (faucets, sinks) = MarketStats.FaucetsAndSinks(byKind);
        var inv = CultureInfo.InvariantCulture;
        j.Append(',');
        MarketStats.WriteByKind(j, byKind);
        j.Append(",\"faucets\":").Append(faucets.ToString(inv)).Append(",\"sinks\":").Append(sinks.ToString(inv));
        AppendRate(j, "faucetsPerMin", r10[SeriesFaucets], r60[SeriesFaucets]);
        AppendRate(j, "sinksPerMin", r10[SeriesSinks], r60[SeriesSinks]);
    }

    /// <summary>The <c>market</c> object, its per-minute rates from the stats window.</summary>
    private void AppendMarketJson(StringBuilder j, ReadOnlySpan<double> r10, ReadOnlySpan<double> r60)
    {
        var rates = new StringBuilder(200);
        AppendRate(rates, "salesPerMin", r10[SeriesMarketSales], r60[SeriesMarketSales]);
        AppendRate(rates, "volumePerMin", r10[SeriesMarketVolume], r60[SeriesMarketVolume]);
        AppendRate(rates, "vendorPerMin", r10[SeriesVendorPaid], r60[SeriesVendorPaid]);
        AppendRate(rates, "feesPerMin", r10[SeriesMarketFees], r60[SeriesMarketFees]);
        MarketStats.Write(j, GatherMarketStats(), CityNames(), rates.ToString());
    }

    // ,"name":{"10s":x,"60s":y}, per minute from per-second rates.
    private static void AppendRate(StringBuilder j, string name, double per10, double per60)
        => j.Append(",\"").Append(name).Append("\":{\"10s\":").Append((per10 * 60d).ToString("0.##", CultureInfo.InvariantCulture))
            .Append(",\"60s\":").Append((per60 * 60d).ToString("0.##", CultureInfo.InvariantCulture)).Append('}');

    /// <summary>The market's series for the stats window: sales, volume, vendor credits, fees (list fee + tax), and credits minted and destroyed.</summary>
    private void SampleMarket(Span<long> values)
    {
        var c = ReadMarketCounters();
        Span<long> byKind = stackalloc long[MarketStats.MaxKind + 1];
        ReadCreditsByKind(byKind);
        var (faucets, sinks) = MarketStats.FaucetsAndSinks(byKind);
        values[SeriesMarketSales] = c.Sales;
        values[SeriesMarketVolume] = c.Volume;
        values[SeriesVendorPaid] = c.VendorPaid;
        values[SeriesMarketFees] = c.ListFees + c.SalesTax;
        values[SeriesFaucets] = faucets;
        values[SeriesSinks] = sinks;
    }

    /// <summary>The market's counters for the exit line, and the credits moved per kind.</summary>
    public string MarketCountersJson()
    {
        var c = ReadMarketCounters();
        var refused = 0L;
        foreach (var n in GatherMarketStats().Refused)
        {
            refused += n;
        }

        Span<long> byKind = stackalloc long[MarketStats.MaxKind + 1];
        ReadCreditsByKind(byKind);
        var (faucets, sinks) = MarketStats.FaucetsAndSinks(byKind);
        var j = new StringBuilder(400);
        j.Append(FormattableString.Invariant(
            $"{{\"listed\":{c.Listed},\"sold\":{c.Sold},\"returned\":{c.Returned},\"escrowUnits\":{c.EscrowUnits},"));
        j.Append(FormattableString.Invariant($"\"listings\":{c.Listings},\"sales\":{c.Sales},"));
        j.Append(FormattableString.Invariant(
            $"\"volume\":{c.Volume},\"listFees\":{c.ListFees},\"salesTax\":{c.SalesTax},\"vendorPaid\":{c.VendorPaid},\"vendorUnits\":{c.VendorUnits},"));
        j.Append(FormattableString.Invariant(
            $"\"cancelled\":{c.Cancelled},\"expired\":{c.Expired},\"collected\":{c.Collected},\"commitsFailed\":{c.CommitsFailed},\"aborted\":{c.Aborted},"));
        j.Append(FormattableString.Invariant($"\"refused\":{refused},\"faucets\":{faucets},\"sinks\":{sinks},"));
        MarketStats.WriteByKind(j, byKind);
        return j.Append('}').ToString();
    }
}
