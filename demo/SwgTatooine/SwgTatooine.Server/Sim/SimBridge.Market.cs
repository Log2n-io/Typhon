using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>A listing as the browse snapshot shows it (bazaar.md § 5): immutable, read off the tick by <c>GET /swg/market</c>.</summary>
/// <param name="Id">The listing's raw id: what <see cref="MarketBuy"/> names.</param>
/// <param name="Template">The item template.</param>
/// <param name="Mk">Its Mk, 0 for an item without one.</param>
/// <param name="Quantity">Units, 1-99.</param>
/// <param name="UnitPrice">Credits per unit.</param>
/// <param name="Seller">The seller's character raw id.</param>
/// <param name="SellerName">The seller's name, read once at listing.</param>
/// <param name="ListedTick">The world tick it was listed on.</param>
/// <param name="City">Its city's group key, region + 1.</param>
/// <param name="Realm">Its planet.</param>
public readonly record struct ListingView(
    long Id,
    ushort Template,
    byte Mk,
    int Quantity,
    long UnitPrice,
    long Seller,
    string SellerName,
    long ListedTick,
    ushort City,
    ushort Realm);

/// <summary>One city's board in a <see cref="MarketSnapshot"/>: its listed listings sorted by (template, Mk, unit price, listed tick), and its counts.</summary>
public sealed class MarketCityView
{
    /// <summary>The planet.</summary>
    public ushort Realm { get; init; }

    /// <summary>The city's group key, region + 1.</summary>
    public ushort City { get; init; }

    /// <summary>The city's name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Its <see cref="ListingState.Listed"/> listings, sorted by (template, Mk, unit price, listed tick, id).</summary>
    public ListingView[] Listings { get; init; } = [];

    /// <summary>Listings sold or returned there and not collected yet.</summary>
    public int Reserved { get; init; }

    /// <summary>Σ unit price × quantity of its listed listings, credits.</summary>
    public long ListedValue { get; init; }
}

/// <summary>What the board asks and what it sold at, per (template, Mk) held anywhere or ever sold (bazaar.md § 5, <c>/swg/market/prices</c>).</summary>
/// <param name="Template">The template.</param>
/// <param name="Mk">The Mk.</param>
/// <param name="Value">Its market value, whole credits (<see cref="MarketValues"/>).</param>
/// <param name="VendorMilli">What the vendor pays per unit, milli-credits.</param>
/// <param name="Listed">Listings of it on the board, every city.</param>
/// <param name="Lowest">The lowest unit price listed; 0 when none is.</param>
/// <param name="Median">The median unit price of its last 200 sales; 0 before the first.</param>
/// <param name="Min">The lowest unit price of those sales.</param>
/// <param name="Max">The highest unit price of those sales.</param>
/// <param name="Sales">Its sales since start.</param>
public readonly record struct MarketPriceView(ushort Template, byte Mk, long Value, long VendorMilli, int Listed, long Lowest, long Median, long Min, long Max,
    long Sales);

/// <summary>
/// The board as the desk last published it (bazaar.md § 5): immutable, swapped whole by <c>Volatile.Write</c>, read by the endpoints and <c>/swg/stats</c>
/// without a transaction. Stale by at most <see cref="TatooineData.MarketPublishMs"/>.
/// </summary>
public sealed class MarketSnapshot
{
    /// <summary>Before the desk's first publish.</summary>
    public static readonly MarketSnapshot Empty = new();

    /// <summary>Rises with every publish: the browse <c>ETag</c>.</summary>
    public long Version { get; init; }

    /// <summary>The world tick it was built on.</summary>
    public long WorldTick { get; init; }

    /// <summary>Ticks per second, to turn ticks into seconds.</summary>
    public int TickRateHz { get; init; } = 1;

    /// <summary>Every city with a bazaar, by planet then city.</summary>
    public MarketCityView[] Cities { get; init; } = [];

    /// <summary>Listed listings, every city.</summary>
    public int Total { get; init; }

    /// <summary>Per (template, Mk) listed, held or sold.</summary>
    public MarketPriceView[] Prices { get; init; } = [];

    /// <summary>The age of the listed listings, seconds: median, 90th percentile, oldest.</summary>
    public (long P50, long P90, long Max) UnsoldAgeS { get; init; }
}

/// <summary>
/// The market desk (bazaar.md § 3): one serial system applying every list, cancel, buy, collect and vendor sale in arrival order, in one side transaction per
/// tick with work; the book of listings in memory; the browse snapshot it publishes.
/// </summary>
public sealed partial class SimBridge
{
    private MarketSnapshot _marketSnapshot = MarketSnapshot.Empty;

    // Since start, published when the desk's transaction commits.
    private long _marketSales;
    private long _marketVolume;
    private long _marketListFees;
    private long _marketSalesTax;
    private long _marketVendorPaid;
    private long _marketVendorUnits;
    private long _marketVendorSales;
    private long _marketListings;
    private long _marketCancelled;
    private long _marketExpired;
    private long _marketCollected;
    private long _marketCommitsFailed;
    private long _marketAborted;

    // Now: the book's counts, set when a commit publishes it.
    private long _marketListedNow;
    private long _marketSoldNow;
    private long _marketReturnedNow;
    private long _marketEscrowUnits;

    // Refusals by reason code (AckReasons.FirstApplicationReason + n), counted when sent.
    private readonly long[] _marketRefusedBy = new long[256];

    /// <summary>The board as last published: what <c>GET /swg/market</c>, <c>/prices</c> and <c>/swg/stats</c> read.</summary>
    public MarketSnapshot MarketBoard => Volatile.Read(ref _marketSnapshot);

    /// <summary>Sales since start.</summary>
    public long MarketSales => Interlocked.Read(ref _marketSales);

    /// <summary>Credits sales moved buyer → seller since start.</summary>
    public long MarketVolume => Interlocked.Read(ref _marketVolume);

    /// <summary>List fees paid to World since start.</summary>
    public long MarketListFees => Interlocked.Read(ref _marketListFees);

    /// <summary>Sales tax paid to World since start.</summary>
    public long MarketSalesTax => Interlocked.Read(ref _marketSalesTax);

    /// <summary>Credits the vendor paid from World since start.</summary>
    public long MarketVendorPaid => Interlocked.Read(ref _marketVendorPaid);

    /// <summary>Units sold to the vendor, destroyed, since start.</summary>
    public long MarketVendorUnits => Interlocked.Read(ref _marketVendorUnits);

    /// <summary>Vendor sales since start.</summary>
    public long MarketVendorSales => Interlocked.Read(ref _marketVendorSales);

    /// <summary>Listings created since start.</summary>
    public long MarketListingsCreated => Interlocked.Read(ref _marketListings);

    /// <summary>Listings cancelled by their seller since start.</summary>
    public long MarketCancelled => Interlocked.Read(ref _marketCancelled);

    /// <summary>Listings expired since start.</summary>
    public long MarketExpired => Interlocked.Read(ref _marketExpired);

    /// <summary>Listings collected — bought or returned goods moved into an inventory — since start.</summary>
    public long MarketCollected => Interlocked.Read(ref _marketCollected);

    /// <summary>Desk transactions that did not commit: every command in them refused <see cref="TatooineReplication.MarketFailed"/>.</summary>
    public long MarketCommitsFailed => Interlocked.Read(ref _marketCommitsFailed);

    /// <summary>Desk transactions disposed uncommitted because a write failed after its validation passed — a bug.</summary>
    public long MarketAborted => Interlocked.Read(ref _marketAborted);

    /// <summary>Listings on the board now.</summary>
    public long MarketListedNow => Interlocked.Read(ref _marketListedNow);

    /// <summary>Listings sold and not collected now.</summary>
    public long MarketSoldNow => Interlocked.Read(ref _marketSoldNow);

    /// <summary>Listings returned and not collected now.</summary>
    public long MarketReturnedNow => Interlocked.Read(ref _marketReturnedNow);

    /// <summary>Units in escrow now: every live listing's quantity.</summary>
    public long MarketEscrowUnits => Interlocked.Read(ref _marketEscrowUnits);

    /// <summary>Market commands refused with <paramref name="reason"/> since start (<see cref="TatooineReplication.MarketSold"/>, …, <see cref="TatooineReplication.NotInWorld"/>).</summary>
    public long MarketRefused(byte reason) => Interlocked.Read(ref _marketRefusedBy[reason]);

    /// <summary>Market commands refused since start, whatever the reason.</summary>
    public long MarketRefusedTotal
    {
        get
        {
            var n = 0L;
            for (var r = 0; r < _marketRefusedBy.Length; r++)
            {
                n += Interlocked.Read(ref _marketRefusedBy[r]);
            }

            return n;
        }
    }

    /// <summary>The reason codes a market command can be refused with, and the names <c>/swg/stats</c> shows them by.</summary>
    public static ReadOnlySpan<(byte Reason, string Name)> MarketRefusalNames => RefusalNames;

    private static readonly (byte Reason, string Name)[] RefusalNames =
    [
        (TatooineReplication.NotInWorld, "notInWorld"),
        (TatooineReplication.MarketNotAtBazaar, "notAtBazaar"),
        (TatooineReplication.MarketWrongCity, "wrongCity"),
        (TatooineReplication.MarketSold, "sold"),
        (TatooineReplication.MarketNotEnough, "notEnough"),
        (TatooineReplication.MarketNoItems, "noItems"),
        (TatooineReplication.MarketBadListing, "badListing"),
        (TatooineReplication.MarketListingCap, "listingCap"),
        (TatooineReplication.MarketReservedCap, "reservedCap"),
        (TatooineReplication.MarketNoRoom, "noRoom"),
        (TatooineReplication.MarketNotYours, "notYours"),
        (TatooineReplication.MarketOwnListing, "ownListing"),
        (TatooineReplication.MarketFailed, "failed"),
        (TatooineReplication.MarketWorthless, "worthless"),
    ];
}
