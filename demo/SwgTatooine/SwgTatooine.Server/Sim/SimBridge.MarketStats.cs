using System;
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

/// <summary>Everything the <c>market</c> section is written from, read once per answer.</summary>
internal sealed class MarketStatsInput
{
    public MarketCounters Counters { get; init; }

    /// <summary>Refusals since start, index for index with <see cref="SimBridge.MarketRefusalNames"/>.</summary>
    public long[] Refused { get; init; } = [];

    /// <summary>The board as the desk last published it: the cities, the price rows, the unsold's age.</summary>
    public MarketSnapshot Board { get; init; } = MarketSnapshot.Empty;
}

/// <summary>
/// The <c>market</c> section of <c>GET /swg/stats</c> and the <c>economy.byKind</c> line (bazaar.md § 9, crafting-catalog § 15.6): the counters, the board
/// by city, the price index's inputs per item and Mk, the unsold's age — written from a <see cref="MarketStatsInput"/>, so the shape is checked without a
/// running desk.
/// </summary>
internal static class MarketStats
{
    /// <summary>Where a credit kind's money goes: minted from World, destroyed to World, or moved between two wallets.</summary>
    public enum Flow : byte
    {
        Faucet,
        Sink,
        Transfer,
    }

    /// <summary>Every credit kind (economy-foundation § 2, bazaar.md § 2.2) and its flow: the "faucets and sinks by channel" line.</summary>
    public static readonly (byte Kind, Flow Flow)[] CreditKinds =
    [
        (TransferKind.Grant, Flow.Faucet), (TransferKind.MissionReward, Flow.Faucet), (TransferKind.Tip, Flow.Transfer),
        (TransferKind.Sale, Flow.Transfer), (TransferKind.Tax, Flow.Sink), (TransferKind.ListFee, Flow.Sink), (TransferKind.Vendor, Flow.Faucet),
    ];

    /// <summary>Credits minted (faucets) and destroyed (sinks) since start, from the credits moved per kind.</summary>
    public static (long Faucets, long Sinks) FaucetsAndSinks(ReadOnlySpan<long> byKind)
    {
        long faucets = 0, sinks = 0;
        foreach (var (kind, flow) in CreditKinds)
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
            var kind = CreditKinds[i].Kind;
            j.Append(i == 0 ? "\"" : ",\"").Append(TransferKind.NameOf(kind)).Append("\":")
                .Append((kind < byKind.Length ? byKind[kind] : 0).ToString(CultureInfo.InvariantCulture));
        }

        j.Append('}');
    }

    /// <summary>
    /// Writes the <c>market</c> object: counters, <c>byCity</c>, <c>refused</c>, then <paramref name="rates"/> (pre-written, comma-led), <c>prices</c> and
    /// <c>unsoldAgeS</c>.
    /// </summary>
    public static void Write(StringBuilder j, MarketStatsInput m, string rates)
    {
        var inv = CultureInfo.InvariantCulture;
        var c = m.Counters;
        var board = m.Board;
        j.Append("{\"listed\":").Append(c.Listed.ToString(inv))
            .Append(",\"sold\":").Append(c.Sold.ToString(inv))
            .Append(",\"returned\":").Append(c.Returned.ToString(inv))
            .Append(",\"escrowUnits\":").Append(c.EscrowUnits.ToString(inv));

        // The board by city, as published: what is listed there and its worth, and what waits there to be collected.
        j.Append(",\"byCity\":[");
        for (var i = 0; i < board.Cities.Length; i++)
        {
            var city = board.Cities[i];
            j.Append(i == 0 ? "{\"city\":" : ",{\"city\":").Append(city.City.ToString(inv))
                .Append(",\"realm\":").Append(city.Realm.ToString(inv))
                .Append(",\"name\":\"").Append(city.Name).Append('"')
                .Append(",\"listed\":").Append(city.Listings.Length.ToString(inv))
                .Append(",\"reserved\":").Append(city.Reserved.ToString(inv))
                .Append(",\"listedValue\":").Append(city.ListedValue.ToString(inv)).Append('}');
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
        var names = SimBridge.MarketRefusalNames;
        for (var i = 0; i < names.Length; i++)
        {
            j.Append(i == 0 ? "\"" : ",\"").Append(names[i].Name).Append("\":").Append((i < m.Refused.Length ? m.Refused[i] : 0).ToString(inv));
        }

        j.Append('}').Append(rates);
        WritePrices(j, board);
        j.Append(",\"unsoldAgeS\":{\"p50\":").Append(board.UnsoldAgeS.P50.ToString(inv))
            .Append(",\"p90\":").Append(board.UnsoldAgeS.P90.ToString(inv))
            .Append(",\"max\":").Append(board.UnsoldAgeS.Max.ToString(inv)).Append("}}");
    }

    /// <summary>
    /// <c>"prices":{"core_armor":[{"mk","sales","median","min","max","listed","lowest","value"}]}</c>: the published price rows, grouped by item, credits
    /// per unit, 0 meaning none. <c>value</c> is the market value to the milli-credit — a material's is under one credit, and the index divides by it.
    /// </summary>
    private static void WritePrices(StringBuilder j, MarketSnapshot board)
    {
        var inv = CultureInfo.InvariantCulture;
        // Grouped by item whatever order the desk published them in: a copy sorted by (template, Mk), ≤ 85 rows.
        var rows = (MarketPriceView[])board.Prices.Clone();
        Array.Sort(rows, static (a, b) => a.Template != b.Template ? a.Template.CompareTo(b.Template) : a.Mk.CompareTo(b.Mk));
        j.Append(",\"prices\":{");
        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            var opens = i == 0 || rows[i - 1].Template != r.Template;
            if (opens)
            {
                j.Append(i == 0 ? "\"" : "],\"").Append(ItemCatalog.TryGet(r.Template, out var t) ? t.Asset : $"t{r.Template}").Append("\":[");
            }

            j.Append(opens ? "{\"mk\":" : ",{\"mk\":").Append(r.Mk.ToString(inv))
                .Append(",\"sales\":").Append(r.Sales.ToString(inv))
                .Append(",\"median\":").Append(r.Median.ToString(inv))
                .Append(",\"min\":").Append(r.Min.ToString(inv))
                .Append(",\"max\":").Append(r.Max.ToString(inv))
                .Append(",\"listed\":").Append(r.Listed.ToString(inv))
                .Append(",\"lowest\":").Append(r.Lowest.ToString(inv))
                .Append(",\"value\":").Append((MarketValues.MilliOf(r.Template, r.Mk) / 1000d).ToString("0.###", inv)).Append('}');
        }

        j.Append(rows.Length == 0 ? "}" : "]}");
    }
}

/// <summary>The market's numbers for <c>/swg/stats</c> and the exit line (bazaar.md § 9), read from the desk's counters and its published board.</summary>
public sealed partial class SimBridge
{
    /// <summary>The desk's committed counters.</summary>
    private MarketCounters ReadMarketCounters()
        => new(MarketListedNow, MarketSoldNow, MarketReturnedNow, MarketEscrowUnits, MarketSales, MarketVolume, MarketListFees, MarketSalesTax,
            MarketVendorPaid, MarketVendorUnits, MarketListingsCreated, MarketCancelled, MarketExpired, MarketCollected, MarketCommitsFailed, MarketAborted);

    /// <summary>Credits moved per credit kind since start, committed only, indexed by kind.</summary>
    private void ReadCreditsByKind(Span<long> into)
    {
        for (var k = 0; k < into.Length; k++)
        {
            into[k] = EconomyMovedBy((byte)k);
        }
    }

    /// <summary>The counters, the refusals and the board as published, read once for one answer.</summary>
    private MarketStatsInput GatherMarketStats()
    {
        var names = MarketRefusalNames;
        var refused = new long[names.Length];
        for (var i = 0; i < refused.Length; i++)
        {
            refused[i] = MarketRefused(names[i].Reason);
        }

        return new MarketStatsInput { Counters = ReadMarketCounters(), Refused = refused, Board = MarketBoard };
    }

    /// <summary>The <c>economy</c> object's by-kind tail: <c>,"byKind":{…},"faucets":n,"sinks":n,"faucetsPerMin":…,"sinksPerMin":…</c>.</summary>
    private void AppendCreditsByKind(StringBuilder j, ReadOnlySpan<double> r10, ReadOnlySpan<double> r60)
    {
        Span<long> byKind = stackalloc long[TransferKind.Count];
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
        MarketStats.Write(j, GatherMarketStats(), rates.ToString());
    }

    // ,"name":{"10s":x,"60s":y}, per minute from per-second rates.
    private static void AppendRate(StringBuilder j, string name, double per10, double per60)
        => j.Append(",\"").Append(name).Append("\":{\"10s\":").Append((per10 * 60d).ToString("0.##", CultureInfo.InvariantCulture))
            .Append(",\"60s\":").Append((per60 * 60d).ToString("0.##", CultureInfo.InvariantCulture)).Append('}');

    /// <summary>The market's series for the stats window: sales, volume, vendor credits, fees (list fee + tax), and credits minted and destroyed.</summary>
    private void SampleMarket(Span<long> values)
    {
        Span<long> byKind = stackalloc long[TransferKind.Count];
        ReadCreditsByKind(byKind);
        var (faucets, sinks) = MarketStats.FaucetsAndSinks(byKind);
        values[SeriesMarketSales] = MarketSales;
        values[SeriesMarketVolume] = MarketVolume;
        values[SeriesVendorPaid] = MarketVendorPaid;
        values[SeriesMarketFees] = MarketListFees + MarketSalesTax;
        values[SeriesFaucets] = faucets;
        values[SeriesSinks] = sinks;
    }

    /// <summary>The market's counters for the exit line, and the credits moved per kind.</summary>
    public string MarketCountersJson()
    {
        var c = ReadMarketCounters();
        Span<long> byKind = stackalloc long[TransferKind.Count];
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
        j.Append(FormattableString.Invariant($"\"refused\":{MarketRefusedTotal},\"faucets\":{faucets},\"sinks\":{sinks},"));
        MarketStats.WriteByKind(j, byKind);
        return j.Append('}').ToString();
    }
}
