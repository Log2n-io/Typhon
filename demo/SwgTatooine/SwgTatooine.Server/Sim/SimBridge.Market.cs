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

    private sealed record Orders(int[] ByPrice, int[] ByNew);

    private Orders _orders;

    /// <summary>Indexes into <see cref="Listings"/> by (unit price, listed tick), cheapest first.</summary>
    public int[] ByPrice => Ordered().ByPrice;

    /// <summary>Indexes into <see cref="Listings"/> by listed tick, oldest first.</summary>
    public int[] ByNew => Ordered().ByNew;

    /// <summary>
    /// A board as the desk sorts it: by (template, Mk, unit price, listed tick, id) — what a browse naming an item binary-searches. The one sort the desk's
    /// tick pays per changed city.
    /// </summary>
    /// <remarks>
    /// One sort of <see cref="long"/> keys rather than of the rows: template (7 bits), Mk (3), unit price (20 — at most 1 000 000) and the listed tick
    /// counted from the board's oldest (34 bits: ten years at 50 Hz). Rows equal on all four are in no particular order, which nothing shows.
    /// </remarks>
    public static void SortBoard(ListingView[] rows)
    {
        var n = rows.Length;
        if (n < 2)
        {
            return;
        }

        var oldest = long.MaxValue;
        foreach (ref readonly var v in rows.AsSpan())
        {
            oldest = Math.Min(oldest, v.ListedTick);
        }

        var keys = new long[n];
        var order = new int[n];
        for (var i = 0; i < n; i++)
        {
            ref readonly var v = ref rows[i];
            keys[i] = ((long)v.Template << 57) | ((long)v.Mk << 54) | (Math.Min(v.UnitPrice, (1L << 20) - 1) << 34) | Math.Min(v.ListedTick - oldest, (1L << 34) - 1);
            order[i] = i;
        }

        Array.Sort(keys, order);
        var copy = (ListingView[])rows.Clone();
        for (var i = 0; i < n; i++)
        {
            rows[i] = copy[order[i]];
        }
    }

    /// <summary>
    /// The price and age orders, built by the first browse that needs them — on that request's thread, once per city board, never on the tick (the view
    /// is kept from snapshot to snapshot while its city does not change): two sorts of <see cref="long"/> keys.
    /// </summary>
    private Orders Ordered()
    {
        var known = Volatile.Read(ref _orders);
        if (known != null)
        {
            return known;
        }

        // By age: (listed tick, position), then by price: (price, rank by age). Positions and ranks are below 2^20, a price at most 2^20.
        var rows = Listings;
        var n = rows.Length;
        var keys = new long[n];
        var byNew = new int[n];
        for (var i = 0; i < n; i++)
        {
            keys[i] = (rows[i].ListedTick << 20) | (uint)i;
            byNew[i] = i;
        }

        Array.Sort(keys, byNew);
        var byPrice = new int[n];
        for (var r = 0; r < n; r++)
        {
            keys[byNew[r]] = (rows[byNew[r]].UnitPrice << 24) | (uint)r;
            byPrice[r] = r;
        }

        Array.Sort(keys, byPrice);
        Interlocked.CompareExchange(ref _orders, new Orders(byPrice, byNew), null);
        return Volatile.Read(ref _orders);
    }
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

    private sealed record GlobalOrders(ListingView[] All, int[] ByPrice, int[] ByNew);

    private GlobalOrders _global;

    /// <summary>Every city's listings, one after the other in <see cref="Cities"/>' order: what <see cref="ByPrice"/> and <see cref="ByNew"/> index.</summary>
    public ListingView[] All => Global().All;

    /// <summary>Indexes into <see cref="All"/> by (unit price, listed tick), cheapest first.</summary>
    public int[] ByPrice => Global().ByPrice;

    /// <summary>Indexes into <see cref="All"/> by listed tick, oldest first.</summary>
    public int[] ByNew => Global().ByNew;

    /// <summary>
    /// Every city's orders together, built by the first browse of all cities that needs them — on that request's thread, once per snapshot, never on the
    /// tick: two sorts of <see cref="long"/> keys over every listing.
    /// </summary>
    private GlobalOrders Global()
    {
        var known = Volatile.Read(ref _global);
        if (known != null)
        {
            return known;
        }

        var n = 0;
        foreach (var c in Cities)
        {
            n += c.Listings.Length;
        }

        var all = new ListingView[n];
        var at = 0;
        foreach (var c in Cities)
        {
            Array.Copy(c.Listings, 0, all, at, c.Listings.Length);
            at += c.Listings.Length;
        }

        // By age: (listed tick, position) — positions are below 2^20 (the caps bound the book far under it). Then by price: (price, rank by age); a price is
        // at most 2^20, a rank below 2^24.
        var keys = new long[n];
        var byNew = new int[n];
        for (var i = 0; i < n; i++)
        {
            keys[i] = (all[i].ListedTick << 20) | (uint)i;
            byNew[i] = i;
        }

        Array.Sort(keys, byNew);
        var byPrice = new int[n];
        for (var r = 0; r < n; r++)
        {
            keys[byNew[r]] = (all[byNew[r]].UnitPrice << 24) | (uint)r;
            byPrice[r] = r;
        }

        Array.Sort(keys, byPrice);
        Interlocked.CompareExchange(ref _global, new GlobalOrders(all, byPrice, byNew), null);
        return Volatile.Read(ref _global);
    }
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

    // ── The book ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A listing as the desk remembers it: its entity, its component as the transaction under way left it, and its seller's name.</summary>
    private sealed class BookEntry
    {
        public EntityId Id;
        public Listing L;
        public string SellerName;
    }

    /// <summary>The last 200 unit prices one (template, Mk) sold at, and its sales since start (bazaar.md § 5, <c>/prices</c>).</summary>
    private sealed class PriceRing
    {
        public const int Size = 200;
        public readonly long[] Prices = new long[Size];
        public int Count;
        public int Next;
        public long Sales;
    }

    /// <summary>What one desk transaction did, counted as it goes and published only once it commits (the mission desk's rule).</summary>
    private struct MarketCounts
    {
        public long Sales;
        public long Volume;
        public long ListFees;
        public long SalesTax;
        public long VendorPaid;
        public long VendorUnits;
        public long VendorSales;
        public long Listings;
        public long Cancelled;
        public long Expired;
        public long Collected;
    }

    private bool _marketLoaded;
    private ushort _listingRouting;
    private readonly Dictionary<long, BookEntry> _book = [];
    private readonly Dictionary<long, int> _listedBy = [];
    private readonly Dictionary<long, int> _reservedBy = [];
    private MissionTerminalRef[] _bazaars = [];
    private (ushort Realm, ushort City)[] _bazaarCities = [];

    // The book's counts, as the transaction under way left them: read again with the book after a failed commit.
    private long _bookListed;
    private long _bookSold;
    private long _bookReturned;
    private long _bookEscrow;

    // What the snapshot must rebuild: the cities a change touched since the last publish.
    private readonly HashSet<int> _dirtyCities = [];
    private bool _marketPublishDue;
    private long _lastPublishTick = long.MinValue / 2;
    private long _snapshotVersion;
    private readonly Dictionary<int, MarketCityView> _cityViews = [];
    private readonly Dictionary<int, PriceRing> _priceRings = [];
    private long[] _ageScratch = [];

    // A listing's life in world ticks; 0 = TatooineData.ListingLifeHours. A check stages an expiry by shortening it.
    private long _listingLifeTicks;

    // Per desk transaction.
    private MarketCounts _marketCounts;
    private bool _marketWritten;
    private bool _marketAbort;
    private readonly List<(int Key, long Unit)> _pendingSalePrices = [];
    private readonly Dictionary<EntityId, EntityId> _marketBodies = [];
    private readonly HashSet<long> _marketWalletsMoved = [];
    private readonly HashSet<long> _marketItemsMoved = [];
    private readonly List<ClientCommand<MarketList>> _listsApplied = [];
    private readonly List<ClientCommand<MarketCancel>> _marketCancelsApplied = [];
    private readonly List<ClientCommand<MarketBuy>> _buysApplied = [];
    private readonly List<ClientCommand<MarketCollect>> _collectsApplied = [];
    private readonly List<ClientCommand<SellToVendor>> _vendorsApplied = [];
    private readonly List<BookEntry> _marketScratch = [];

    /// <summary>For a check: listings listed from now on expire after <paramref name="ticks"/> world ticks; 0 restores <see cref="TatooineData.ListingLifeHours"/>.</summary>
    internal void SetListingLifeTicksForChecks(long ticks) => _listingLifeTicks = ticks;

    // For a check: the caps (0 = TatooineData's), and a write failure staged on the next desk transaction that applies anything.
    private int _maxListings;
    private int _maxReserved;
    private bool _failNextMarket;

    /// <summary>For a check: the per-character caps, so reaching one takes a few commands rather than twenty; 0 restores <see cref="TatooineData"/>'s.</summary>
    internal void SetMarketCapsForChecks(int listings, int reserved)
    {
        _maxListings = listings;
        _maxReserved = reserved;
    }

    /// <summary>
    /// For a check: the next desk transaction that applies a command behaves as if a write failed after its validation passed — disposed uncommitted, every
    /// command refused <see cref="TatooineReplication.MarketFailed"/>, counted <c>market.aborted</c>.
    /// </summary>
    internal void FailNextMarketWriteForChecks() => _failNextMarket = true;

    private int MaxListings => _maxListings > 0 ? _maxListings : TatooineData.MaxListings;

    private int MaxReserved => _maxReserved > 0 ? _maxReserved : TatooineData.MaxReserved;

    /// <summary>For a check: the next market tick publishes the snapshot whatever the time since the last.</summary>
    internal void PublishMarketNowForChecks() => _lastPublishTick = long.MinValue / 2;

    private long ListingLifeTicks => _listingLifeTicks > 0 ? _listingLifeTicks : (long)TatooineData.ListingLifeHours * 3600 * _config.TickRateHz;

    private ushort ListingRouting => _listingRouting != 0 ? _listingRouting : _listingRouting = Dbe.ArchetypeIdOf<ListingArch>();

    /// <summary>Whether an item-ledger side names a listing (its escrow) rather than a character or World: by the entity's archetype.</summary>
    private bool IsListing(long key) => key != 0 && EntityId.FromRawValue((ulong)key).ArchetypeId == ListingRouting;

    private static int CityKey(ushort realm, ushort city) => (realm << 16) | city;

    private void MarkCityDirty(in Listing l)
    {
        _dirtyCities.Add(CityKey(l.Realm, l.City));
        _marketPublishDue = true;
    }

    /// <summary>Counts a listing in, or out of, the book's per-state counts and per-character caps.</summary>
    private void CountListing(in Listing l, int delta)
    {
        switch (l.State)
        {
            case ListingState.Listed:
                _bookListed += delta;
                Adjust(_listedBy, l.Seller, delta);
                break;
            case ListingState.Sold:
                _bookSold += delta;
                Adjust(_reservedBy, l.Collector, delta);
                break;
            case ListingState.Returned:
                _bookReturned += delta;
                break;
        }

        static void Adjust(Dictionary<long, int> counts, long key, int delta)
        {
            var n = (counts.TryGetValue(key, out var c) ? c : 0) + delta;
            if (n <= 0)
            {
                counts.Remove(key);
            }
            else
            {
                counts[key] = n;
            }
        }
    }

    /// <summary>Reads every listing on the first use after a start or a failed commit; the bazaar terminals from the world's indexes.</summary>
    private void EnsureMarket(Transaction tx)
    {
        if (_marketLoaded)
        {
            return;
        }

        var bazaars = new List<MissionTerminalRef>();
        var cities = new SortedSet<(ushort, ushort)>();
        foreach (var index in PlanetIndexes)
        {
            foreach (var b in index.Bazaars)
            {
                bazaars.Add(b);
                if (b.Region > 0)
                {
                    cities.Add((b.Realm, (ushort)b.Region));
                }
            }
        }

        _bazaars = bazaars.ToArray();
        _bazaarCities = [.. cities];
        _book.Clear();
        _listedBy.Clear();
        _reservedBy.Clear();
        _bookListed = _bookSold = _bookReturned = _bookEscrow = 0;
        var names = new Dictionary<long, string>();
        using (var found = tx.EnumerateIndex<Listing, long>(Dbe.GetIndexRef<Listing, long>(l => l.Seller), long.MinValue, long.MaxValue))
        {
            while (found.MoveNext())
            {
                var entry = new BookEntry { Id = EntityId.FromRawValue((ulong)found.CurrentEntityPK), L = found.CurrentComponent };
                _book[found.CurrentEntityPK] = entry;
                CountListing(in entry.L, +1);
                _bookEscrow += entry.L.Quantity;
            }
        }

        foreach (var entry in _book.Values)
        {
            if (!names.TryGetValue(entry.L.Seller, out var name))
            {
                name = NameOfCharacter(tx, entry.L.Seller);
                names[entry.L.Seller] = name;
            }

            entry.SellerName = name;
            MarkCityDirty(in entry.L);
        }

        _cityViews.Clear();
        _marketPublishDue = true;
        _marketLoaded = true;
    }

    private static string NameOfCharacter(Transaction tx, long key)
        => tx.TryOpen(EntityId.FromRawValue((ulong)key), out var c) ? c.Read(CharacterArch.Profile).Name.AsString : "?";

    // ── The desk's tick ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>Market</c> system (bazaar.md § 3): cancels, collects, buys, lists, vendor sales, in that order, each kind in arrival order; the expiry pass once a
    /// second; one side transaction; then the snapshot, at most every <see cref="TatooineData.MarketPublishMs"/>.
    /// </summary>
    public void MarketTick(TickContext ctx)
    {
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var subs = ctx.Subscriptions;
        var worldTick = ctx.WorldTick;
        var cancels = subs == null ? default : subs.Commands<MarketCancel>();
        var collects = subs == null ? default : subs.Commands<MarketCollect>();
        var buys = subs == null ? default : subs.Commands<MarketBuy>();
        var lists = subs == null ? default : subs.Commands<MarketList>();
        var vendors = subs == null ? default : subs.Commands<SellToVendor>();
        var commands = cancels.Count + collects.Count + buys.Count + lists.Count + vendors.Count;
        var expiryPass = ctx.TickNumber % Math.Max(1, _config.TickRateHz) == 0;
        if (_marketLoaded && commands == 0 && !(expiryPass && AnyExpired(worldTick)))
        {
            MaybePublishMarket(worldTick);
            return;
        }

        var tick0 = ctx.Transaction;
        _marketCounts = default;
        _marketWritten = false;
        _marketAbort = false;
        _pendingSalePrices.Clear();
        _marketWalletsMoved.Clear();
        _marketItemsMoved.Clear();
        _listsApplied.Clear();
        _marketCancelsApplied.Clear();
        _buysApplied.Clear();
        _collectsApplied.Clear();
        _vendorsApplied.Clear();
        bool committed;
        using (var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit))
        {
            EnsureEconomy(tx);
            EnsureItems(tx);
            EnsureMarket(tx);
            TatooineReplication.FillCharacterBodies(_marketBodies);
            if (subs != null)
            {
                // Cancels first: a cancel frees a listing slot for a list of the same tick; collects before vendor sales: a collect frees room.
                foreach (var command in cancels)
                {
                    CancelFromCommand(tx, tick0, subs, in command, worldTick);
                }

                foreach (var command in collects)
                {
                    CollectFromCommand(tx, tick0, subs, in command, worldTick);
                }

                foreach (var command in buys)
                {
                    BuyFromCommand(tx, tick0, subs, in command, worldTick);
                }

                foreach (var command in lists)
                {
                    ListFromCommand(tx, tick0, subs, in command, worldTick);
                }

                foreach (var command in vendors)
                {
                    VendorFromCommand(tx, tick0, subs, in command, worldTick);
                }
            }

            if (expiryPass && !_marketAbort)
            {
                ExpireListings(tx, tick0, worldTick);
            }

            if (_failNextMarket && _marketWritten)
            {
                _failNextMarket = false;
                _marketAbort = true;
            }

            if (_marketAbort)
            {
                // A write failed after its validation passed: a bug. The transaction is disposed uncommitted, and everything in it is refused.
                committed = false;
            }
            else
            {
                FlushItemHead(tx);
                committed = tx.Commit();
            }
        }

        EndEconomy(committed);
        EndItems(committed);
        EndMarket(committed);
        if (!committed)
        {
            if (_marketAbort)
            {
                Interlocked.Increment(ref _marketAborted);
                Console.WriteLine($"market: a write failed after its validation passed at world tick {worldTick}; the tick's market commands are refused");
            }
            else if (_marketWritten)
            {
                Interlocked.Increment(ref _marketCommitsFailed);
            }

            RefuseAppliedMarket(subs);
            return;
        }

        foreach (var key in _marketWalletsMoved)
        {
            if (_marketBodies.TryGetValue(EntityId.FromRawValue((ulong)key), out var body))
            {
                BumpWallet(tick0, body);
            }
        }

        foreach (var key in _marketItemsMoved)
        {
            if (_marketBodies.TryGetValue(EntityId.FromRawValue((ulong)key), out var body))
            {
                BumpItems(tick0, body);
            }
        }

        MaybePublishMarket(worldTick);
    }

    /// <summary>Every market command a transaction that did not commit had applied: refused <see cref="TatooineReplication.MarketFailed"/>, so its client hears it.</summary>
    private void RefuseAppliedMarket(SubscriptionsCommands subs)
    {
        if (subs == null)
        {
            return;
        }

        foreach (var c in _listsApplied)
        {
            RefuseMarket(subs, in c, TatooineReplication.MarketFailed);
        }

        foreach (var c in _marketCancelsApplied)
        {
            RefuseMarket(subs, in c, TatooineReplication.MarketFailed);
        }

        foreach (var c in _buysApplied)
        {
            RefuseMarket(subs, in c, TatooineReplication.MarketFailed);
        }

        foreach (var c in _collectsApplied)
        {
            RefuseMarket(subs, in c, TatooineReplication.MarketFailed);
        }

        foreach (var c in _vendorsApplied)
        {
            RefuseMarket(subs, in c, TatooineReplication.MarketFailed);
        }
    }

    /// <summary>
    /// The desk's transaction ended. Committed: its counts and the book's are published, and the prices it sold at join their rings. Not, and it wrote
    /// anything: the book is read again on the next use.
    /// </summary>
    private void EndMarket(bool committed)
    {
        if (committed)
        {
            Interlocked.Add(ref _marketSales, _marketCounts.Sales);
            Interlocked.Add(ref _marketVolume, _marketCounts.Volume);
            Interlocked.Add(ref _marketListFees, _marketCounts.ListFees);
            Interlocked.Add(ref _marketSalesTax, _marketCounts.SalesTax);
            Interlocked.Add(ref _marketVendorPaid, _marketCounts.VendorPaid);
            Interlocked.Add(ref _marketVendorUnits, _marketCounts.VendorUnits);
            Interlocked.Add(ref _marketVendorSales, _marketCounts.VendorSales);
            Interlocked.Add(ref _marketListings, _marketCounts.Listings);
            Interlocked.Add(ref _marketCancelled, _marketCounts.Cancelled);
            Interlocked.Add(ref _marketExpired, _marketCounts.Expired);
            Interlocked.Add(ref _marketCollected, _marketCounts.Collected);
            Interlocked.Exchange(ref _marketListedNow, _bookListed);
            Interlocked.Exchange(ref _marketSoldNow, _bookSold);
            Interlocked.Exchange(ref _marketReturnedNow, _bookReturned);
            Interlocked.Exchange(ref _marketEscrowUnits, _bookEscrow);
            foreach (var (key, unit) in _pendingSalePrices)
            {
                if (!_priceRings.TryGetValue(key, out var ring))
                {
                    ring = new PriceRing();
                    _priceRings[key] = ring;
                }

                ring.Prices[ring.Next] = unit;
                ring.Next = (ring.Next + 1) % PriceRing.Size;
                ring.Count = Math.Min(ring.Count + 1, PriceRing.Size);
                ring.Sales++;
            }

            if (_pendingSalePrices.Count > 0)
            {
                _marketPublishDue = true;
            }
        }
        else if (_marketWritten)
        {
            _marketLoaded = false;
        }

        _marketCounts = default;
        _pendingSalePrices.Clear();
    }

    /// <summary>Whether any listed listing is past its expiry: the 1 Hz pass opens a transaction only then.</summary>
    private bool AnyExpired(long worldTick)
    {
        foreach (var entry in _book.Values)
        {
            if (entry.L.State == ListingState.Listed && entry.L.ExpireTick <= worldTick)
            {
                return true;
            }
        }

        return false;
    }

    // ── Who and where ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The session's character and where its body stands; <see langword="false"/> for a session with no body, or a character with no wallet yet.</summary>
    private bool MarketActor(Transaction tick0, SessionId session, out long key, out ushort realm, out float x, out float z)
    {
        key = 0;
        realm = 0;
        x = z = 0f;
        if (!TatooineReplication.TryBodyOfSession(session, out var body, out var character) || !tick0.TryOpen(body, out var player))
        {
            return false;
        }

        key = (long)character.RawValue;
        var at = player.Read(Player.Bounds);
        realm = player.Read(Player.Realm).Value;
        x = at.X;
        z = at.Z;
        return _walletOf.ContainsKey(key);
    }

    /// <summary>The nearest bazaar terminal within <see cref="TatooineData.BazaarReachM"/> of a point on a planet.</summary>
    private bool BazaarNear(ushort realm, float x, float z, out MissionTerminalRef terminal)
    {
        terminal = default;
        var best = TatooineData.BazaarReachM * TatooineData.BazaarReachM;
        var found = false;
        foreach (var b in _bazaars)
        {
            if (b.Realm != realm)
            {
                continue;
            }

            var d2 = Distance2(b.X, b.Z, x, z);
            if (d2 <= best)
            {
                best = d2;
                terminal = b;
                found = true;
            }
        }

        return found;
    }

    /// <summary>The body of a character online now, standing at a bazaar of the listing's city: where its goods can be handed over at once (B4).</summary>
    private bool AtListingCity(Transaction tick0, long character, in Listing l)
    {
        if (!_marketBodies.TryGetValue(EntityId.FromRawValue((ulong)character), out var body) || !tick0.TryOpen(body, out var player))
        {
            return false;
        }

        var at = player.Read(Player.Bounds);
        return BazaarNear(player.Read(Player.Realm).Value, at.X, at.Z, out var t) && t.Realm == l.Realm && t.Region == l.City;
    }

    private long BalanceOf(Transaction tx, long key)
        => _walletOf.TryGetValue(key, out var w) && tx.TryOpen(w, out var e) ? e.Read(WalletArch.Purse).Balance : 0;

    private void RefuseMarket<T>(SubscriptionsCommands subs, in ClientCommand<T> command, byte reason) where T : unmanaged
    {
        subs.Reject(command, reason);
        Interlocked.Increment(ref _marketRefusedBy[reason]);
    }

    // ── The commands ────────────────────────────────────────────────────────────────────────────────────────────────

    private void ListFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<MarketList> command, long worldTick)
    {
        if (_marketAbort)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
            return;
        }

        if (!MarketActor(tick0, command.Session, out var seller, out var realm, out var x, out var z))
        {
            RefuseMarket(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (!BazaarNear(realm, x, z, out var terminal) || terminal.Region <= 0)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNotAtBazaar);
            return;
        }

        var v = command.Value;
        if (!ItemCatalog.TryGet(v.Template, out var t) || !ItemCatalog.MkFits(in t, v.Mk) || v.Quantity is < 1 or > ItemLedger.StackMax
            || v.UnitPrice < 1 || v.UnitPrice > TatooineData.MaxUnitPrice)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketBadListing);
            return;
        }

        if (UnitsOf(ItemsOf(seller), v.Template, v.Mk) < v.Quantity)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNoItems);
            return;
        }

        if (_listedBy.TryGetValue(seller, out var listed) && listed >= MaxListings)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketListingCap);
            return;
        }

        var fee = TatooineData.ListFee((long)v.UnitPrice * v.Quantity);
        if (BalanceOf(tx, seller) < fee)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNotEnough);
            return;
        }

        // Validated: the listing, its escrow, its fee — one transaction.
        var l = new Listing
        {
            Seller = seller,
            UnitPrice = v.UnitPrice,
            ListedTick = worldTick,
            ExpireTick = worldTick + ListingLifeTicks,
            Fee = fee,
            City = (ushort)terminal.Region,
            Realm = terminal.Realm,
            Template = v.Template,
            Mk = v.Mk,
            State = ListingState.Listed,
        };
        var id = tx.Spawn<ListingArch>(ListingArch.Entry.Set(in l));
        var key = (long)id.RawValue;
        var entry = new BookEntry { Id = id, L = l, SellerName = NameOfCharacter(tx, seller) };
        _book[key] = entry;
        CountListing(in entry.L, +1);
        MarkCityDirty(in entry.L);
        _marketWritten = true;
        if (ItemTransfer(tx, seller, key, v.Template, v.Mk, v.Quantity, ItemMoveKind.List, key, worldTick) != v.Quantity
            || !Transfer(tx, seller, EconomyLedger.World, fee, TransferKind.ListFee, key, worldTick))
        {
            _marketAbort = true;
            RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
            return;
        }

        _marketCounts.Listings++;
        _marketCounts.ListFees += fee;
        _marketWalletsMoved.Add(seller);
        _marketItemsMoved.Add(seller);
        _listsApplied.Add(command);
    }

    private void CancelFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<MarketCancel> command, long worldTick)
    {
        if (_marketAbort)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
            return;
        }

        if (!MarketActor(tick0, command.Session, out var seller, out _, out _, out _))
        {
            RefuseMarket(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (!_book.TryGetValue((long)command.Value.Listing, out var entry) || entry.L.Seller != seller)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNotYours);
            return;
        }

        if (entry.L.State != ListingState.Listed)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketSold);
            return;
        }

        if (!Close(tx, tick0, entry, ListingState.Returned, seller, worldTick))
        {
            _marketAbort = true;
            RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
            return;
        }

        _marketCounts.Cancelled++;
        _marketCancelsApplied.Add(command);
    }

    private void BuyFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<MarketBuy> command, long worldTick)
    {
        if (_marketAbort)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
            return;
        }

        if (!MarketActor(tick0, command.Session, out var buyer, out var realm, out var x, out var z))
        {
            RefuseMarket(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (!BazaarNear(realm, x, z, out var terminal))
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNotAtBazaar);
            return;
        }

        // The race: a second buyer of one listing finds it sold here, in the book, and writes nothing.
        var listingKey = (long)command.Value.Listing;
        if (!_book.TryGetValue(listingKey, out var entry) || entry.L.State != ListingState.Listed || entry.L.UnitPrice != command.Value.UnitPrice)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketSold);
            return;
        }

        if (entry.L.Seller == buyer)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketOwnListing);
            return;
        }

        // A purchase handed over at once is never reserved, so the cap is only asked of one that will be.
        var deliversHere = terminal.Realm == entry.L.Realm && terminal.Region == entry.L.City
            && RoomFor(ItemsOf(buyer), entry.L.Template, entry.L.Mk) >= entry.L.Quantity;
        if (!deliversHere && _reservedBy.TryGetValue(buyer, out var reserved) && reserved >= MaxReserved)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketReservedCap);
            return;
        }

        var total = entry.L.UnitPrice * entry.L.Quantity;
        if (BalanceOf(tx, buyer) < total || !_walletOf.ContainsKey(entry.L.Seller))
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNotEnough);
            return;
        }

        // Validated: the sale, the tax, the state, and — here, with room — the goods.
        _marketWritten = true;
        var seller = entry.L.Seller;
        var tax = TatooineData.SalesTax(total);
        if (!Transfer(tx, buyer, seller, total, TransferKind.Sale, listingKey, worldTick)
            || (tax > 0 && !Transfer(tx, seller, EconomyLedger.World, tax, TransferKind.Tax, listingKey, worldTick))
            || !Close(tx, tick0, entry, ListingState.Sold, buyer, worldTick, deliversHere))
        {
            _marketAbort = true;
            RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
            return;
        }

        _pendingSalePrices.Add((KeyOf(entry.L.Template, entry.L.Mk), entry.L.UnitPrice));
        _marketCounts.Sales++;
        _marketCounts.Volume += total;
        _marketCounts.SalesTax += tax;
        _marketWalletsMoved.Add(buyer);
        _marketWalletsMoved.Add(seller);
        _marketItemsMoved.Add(seller);
        _buysApplied.Add(command);
    }

    private void CollectFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<MarketCollect> command, long worldTick)
    {
        if (_marketAbort)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
            return;
        }

        if (!MarketActor(tick0, command.Session, out var collector, out var realm, out var x, out var z))
        {
            RefuseMarket(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (!BazaarNear(realm, x, z, out var terminal))
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNotAtBazaar);
            return;
        }

        var items = ItemsOf(collector);
        if (command.Value.Listing != 0)
        {
            if (!_book.TryGetValue((long)command.Value.Listing, out var one) || one.L.State is not (ListingState.Sold or ListingState.Returned)
                || one.L.Collector != collector)
            {
                RefuseMarket(subs, in command, TatooineReplication.MarketNotYours);
                return;
            }

            if (terminal.Realm != one.L.Realm || terminal.Region != one.L.City)
            {
                RefuseMarket(subs, in command, TatooineReplication.MarketWrongCity);
                return;
            }

            if (RoomFor(items, one.L.Template, one.L.Mk) < one.L.Quantity)
            {
                RefuseMarket(subs, in command, TatooineReplication.MarketNoRoom);
                return;
            }

            if (!Deliver(tx, one, worldTick))
            {
                _marketAbort = true;
                RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
                return;
            }

            _collectsApplied.Add(command);
            return;
        }

        // All of this character's reserved here, oldest first, each that fits.
        _marketScratch.Clear();
        var anywhere = false;
        foreach (var entry in _book.Values)
        {
            if (entry.L.Collector == collector && entry.L.State is ListingState.Sold or ListingState.Returned)
            {
                anywhere = true;
                if (entry.L.Realm == terminal.Realm && entry.L.City == terminal.Region)
                {
                    _marketScratch.Add(entry);
                }
            }
        }

        if (_marketScratch.Count == 0)
        {
            RefuseMarket(subs, in command, anywhere ? TatooineReplication.MarketWrongCity : TatooineReplication.MarketNotYours);
            return;
        }

        _marketScratch.Sort(static (a, b) => a.L.ClosedTick != b.L.ClosedTick ? a.L.ClosedTick.CompareTo(b.L.ClosedTick) : a.Id.RawValue.CompareTo(b.Id.RawValue));
        var delivered = 0;
        foreach (var entry in _marketScratch)
        {
            if (RoomFor(items, entry.L.Template, entry.L.Mk) < entry.L.Quantity)
            {
                continue;
            }

            if (!Deliver(tx, entry, worldTick))
            {
                _marketAbort = true;
                RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
                return;
            }

            delivered++;
        }

        if (delivered == 0)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNoRoom);
            return;
        }

        _collectsApplied.Add(command);
    }

    private void VendorFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<SellToVendor> command, long worldTick)
    {
        if (_marketAbort)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
            return;
        }

        if (!MarketActor(tick0, command.Session, out var seller, out var realm, out var x, out var z))
        {
            RefuseMarket(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (!BazaarNear(realm, x, z, out _))
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNotAtBazaar);
            return;
        }

        var v = command.Value;
        if (!ItemCatalog.TryGet(v.Template, out var t) || !ItemCatalog.MkFits(in t, v.Mk) || v.Quantity is < 1 or > ItemLedger.StackMax)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketBadListing);
            return;
        }

        if (UnitsOf(ItemsOf(seller), v.Template, v.Mk) < v.Quantity)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketNoItems);
            return;
        }

        var payout = MarketValues.VendorPayout(v.Template, v.Mk, v.Quantity);
        if (payout <= 0)
        {
            RefuseMarket(subs, in command, TatooineReplication.MarketWorthless);
            return;
        }

        // The two rows name each other: the item rows the credit row's sequence number, the credit row the first item row's — both known before either is
        // written, because each ledger numbers its rows from its head.
        _marketWritten = true;
        var firstItemSeq = _itemHead.LastSeq + 1;
        var creditSeq = _head.LastSeq + 1;
        if (ItemTransfer(tx, seller, ItemLedger.World, v.Template, v.Mk, v.Quantity, ItemMoveKind.Vendor, creditSeq, worldTick) != v.Quantity
            || !Transfer(tx, EconomyLedger.World, seller, payout, TransferKind.Vendor, firstItemSeq, worldTick))
        {
            _marketAbort = true;
            RefuseMarket(subs, in command, TatooineReplication.MarketFailed);
            return;
        }

        _marketCounts.VendorPaid += payout;
        _marketCounts.VendorUnits += v.Quantity;
        _marketCounts.VendorSales++;
        _marketWalletsMoved.Add(seller);
        _marketItemsMoved.Add(seller);
        _vendorsApplied.Add(command);
    }

    /// <summary>
    /// Every listed listing past its expiry: returned to its seller, the fee not refunded — handed over at once when its seller stands at a bazaar of its
    /// city with room (B4).
    /// </summary>
    private void ExpireListings(Transaction tx, Transaction tick0, long worldTick)
    {
        _marketScratch.Clear();
        foreach (var entry in _book.Values)
        {
            if (entry.L.State == ListingState.Listed && entry.L.ExpireTick <= worldTick)
            {
                _marketScratch.Add(entry);
            }
        }

        foreach (var entry in _marketScratch)
        {
            _marketWritten = true;
            if (!Close(tx, tick0, entry, ListingState.Returned, entry.L.Seller, worldTick))
            {
                _marketAbort = true;
                return;
            }

            _marketCounts.Expired++;
        }
    }

    // ── State changes ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A listed listing sold or returned: its state, collector and close tick written; its goods handed over at once when the collector stands at a bazaar
    /// of its city with room (B4) — <paramref name="deliverHere"/> when the caller has decided that already.
    /// </summary>
    /// <returns><see langword="false"/> when a write failed: the caller aborts the transaction.</returns>
    private bool Close(Transaction tx, Transaction tick0, BookEntry entry, byte state, long collector, long worldTick, bool? deliverHere = null)
    {
        CountListing(in entry.L, -1);
        entry.L.State = state;
        entry.L.Collector = collector;
        entry.L.ClosedTick = worldTick;
        CountListing(in entry.L, +1);
        tx.OpenMut(entry.Id).Set(ListingArch.Entry, entry.L);
        MarkCityDirty(in entry.L);
        _marketWritten = true;
        _marketItemsMoved.Add(entry.L.Seller);
        _marketItemsMoved.Add(collector);
        var here = deliverHere ?? (AtListingCity(tick0, collector, in entry.L)
            && RoomFor(ItemsOf(collector), entry.L.Template, entry.L.Mk) >= entry.L.Quantity);
        return !here || Deliver(tx, entry, worldTick);
    }

    /// <summary>A sold or returned listing's goods moved into its collector's inventory — <c>buy</c> or <c>return</c> rows — and the listing destroyed.</summary>
    /// <returns><see langword="false"/> when a write failed: the caller aborts the transaction.</returns>
    private bool Deliver(Transaction tx, BookEntry entry, long worldTick)
    {
        var key = (long)entry.Id.RawValue;
        var kind = entry.L.State == ListingState.Sold ? ItemMoveKind.Buy : ItemMoveKind.Return;
        var quantity = entry.L.Quantity;
        var collector = entry.L.Collector;
        if (ItemTransfer(tx, key, collector, entry.L.Template, entry.L.Mk, quantity, kind, key, worldTick) != quantity)
        {
            return false;
        }

        CountListing(in entry.L, -1);
        tx.Destroy(entry.Id);
        _book.Remove(key);
        MarkCityDirty(in entry.L);
        _marketWritten = true;
        _marketCounts.Collected++;
        _marketItemsMoved.Add(collector);
        return true;
    }

    // ── The snapshot ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Publishes the board when it changed and <see cref="TatooineData.MarketPublishMs"/> of world time has passed since the last publish.</summary>
    private void MaybePublishMarket(long worldTick)
    {
        if (!_marketLoaded || !_marketPublishDue)
        {
            return;
        }

        var every = Math.Max(1L, (long)TatooineData.MarketPublishMs * _config.TickRateHz / 1000);
        if (worldTick - _lastPublishTick < every)
        {
            return;
        }

        _lastPublishTick = worldTick;
        _marketPublishDue = false;
        Volatile.Write(ref _marketSnapshot, BuildMarketSnapshot(worldTick));
        _dirtyCities.Clear();
    }

    /// <summary>
    /// One pass of the book: the dirty cities' boards rebuilt and sorted (the others kept from the last snapshot), the prices and the unsold ages recounted.
    /// </summary>
    private MarketSnapshot BuildMarketSnapshot(long worldTick)
    {
        var rebuilt = new Dictionary<int, (List<ListingView> Listed, int Reserved, long Value)>();
        foreach (var key in _dirtyCities)
        {
            rebuilt[key] = ([], 0, 0);
        }

        var lowest = new Dictionary<int, (int Listed, long Lowest)>();
        if (_ageScratch.Length < _book.Count)
        {
            _ageScratch = new long[Math.Max(64, _book.Count * 2)];
        }

        var ages = 0;
        foreach (var (id, entry) in _book)
        {
            ref readonly var l = ref entry.L;
            var cityKey = CityKey(l.Realm, l.City);
            var dirty = rebuilt.TryGetValue(cityKey, out var board);
            if (l.State == ListingState.Listed)
            {
                var k = KeyOf(l.Template, l.Mk);
                var (n, low) = lowest.TryGetValue(k, out var c) ? c : (0, long.MaxValue);
                lowest[k] = (n + 1, Math.Min(low, l.UnitPrice));
                _ageScratch[ages++] = Math.Max(0, worldTick - l.ListedTick);
                if (dirty)
                {
                    board.Listed.Add(new ListingView(id, l.Template, l.Mk, l.Quantity, l.UnitPrice, l.Seller, entry.SellerName, l.ListedTick, l.City, l.Realm));
                    board.Value += l.UnitPrice * l.Quantity;
                    rebuilt[cityKey] = board;
                }
            }
            else if (dirty)
            {
                board.Reserved++;
                rebuilt[cityKey] = board;
            }
        }

        foreach (var (key, board) in rebuilt)
        {
            var array = board.Listed.ToArray();
            MarketCityView.SortBoard(array);
            var realm = (ushort)(key >> 16);
            var city = (ushort)(key & 0xFFFF);
            _cityViews[key] = new MarketCityView
            {
                Realm = realm,
                City = city,
                Name = city >= 1 && city <= _map.Cities.Count ? _map.Cities[city - 1].Name : $"city {city}",
                Listings = array,
                Reserved = board.Reserved,
                ListedValue = board.Value,
            };
        }

        var cities = new MarketCityView[_bazaarCities.Length];
        var total = 0;
        for (var i = 0; i < cities.Length; i++)
        {
            var key = CityKey(_bazaarCities[i].Realm, _bazaarCities[i].City);
            if (!_cityViews.TryGetValue(key, out var view))
            {
                view = new MarketCityView
                {
                    Realm = _bazaarCities[i].Realm,
                    City = _bazaarCities[i].City,
                    Name = _bazaarCities[i].City <= _map.Cities.Count ? _map.Cities[_bazaarCities[i].City - 1].Name : $"city {_bazaarCities[i].City}",
                };
                _cityViews[key] = view;
            }

            cities[i] = view;
            total += view.Listings.Length;
        }

        return new MarketSnapshot
        {
            Version = ++_snapshotVersion,
            WorldTick = worldTick,
            TickRateHz = Math.Max(1, _config.TickRateHz),
            Cities = cities,
            Total = total,
            Prices = BuildPrices(lowest),
            UnsoldAgeS = Ages(_ageScratch, ages, Math.Max(1, _config.TickRateHz)),
        };

        static (long, long, long) Ages(long[] ticks, int n, int hz)
        {
            if (n == 0)
            {
                return (0, 0, 0);
            }

            Array.Sort(ticks, 0, n);
            return (ticks[(n - 1) / 2] / hz, ticks[(int)((n - 1) * 0.9)] / hz, ticks[n - 1] / hz);
        }
    }

    /// <summary>Per (template, Mk) listed, held or ever sold: its value, what the board asks and what it sold at.</summary>
    private MarketPriceView[] BuildPrices(Dictionary<int, (int Listed, long Lowest)> lowest)
    {
        var prices = new List<MarketPriceView>();
        var sorted = new long[PriceRing.Size];
        foreach (var t in ItemCatalog.Templates)
        {
            for (byte mk = 0; mk < MkSpan; mk++)
            {
                if (!ItemCatalog.MkFits(in t, mk))
                {
                    continue;
                }

                var k = KeyOf(t.Id, mk);
                var listedHere = lowest.TryGetValue(k, out var low);
                _priceRings.TryGetValue(k, out var ring);
                if (!listedHere && ring == null && ItemsHeld(t.Id, mk) == 0)
                {
                    continue;
                }

                long median = 0, min = 0, max = 0;
                if (ring is { Count: > 0 })
                {
                    Array.Copy(ring.Prices, sorted, ring.Count);
                    Array.Sort(sorted, 0, ring.Count);
                    median = sorted[(ring.Count - 1) / 2];
                    min = sorted[0];
                    max = sorted[ring.Count - 1];
                }

                prices.Add(new MarketPriceView(t.Id, mk, MarketValues.CreditsOf(t.Id, mk),
                    MarketValues.MilliOf(t.Id, mk) * TatooineData.VendorBuysPerMille / 1000, listedHere ? low.Listed : 0, listedHere ? low.Lowest : 0, median,
                    min, max, ring?.Sales ?? 0));
            }
        }

        return [.. prices];
    }
}
