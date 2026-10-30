using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SwgTatooine.Bots.Brain;
using Typhon.Client;
using Typhon.Protocol;

namespace SwgTatooine.Bots;

/// <summary>
/// The bazaar (bazaar.md § 8): a thinking bot's market — what it reads, the plan it sends at a bazaar, and what it counts — with a browser's reads and
/// commands only. The brain decides when (<see cref="IntentKind.Trade"/>); this decides what.
/// </summary>
public sealed partial class BotSwarm
{
    /// <summary>The bazaar's commands, named as the server declares them (bazaar.md § 3.1).</summary>
    private const string MarketListCommand = "MarketList";

    private const string MarketBuyCommand = "MarketBuy";
    private const string MarketCollectCommand = "MarketCollect";
    private const string SellToVendorCommand = "SellToVendor";

    /// <summary>
    /// The bazaar's refusals (bazaar.md § 3.1): <c>AckReasons.FirstApplicationReason</c> + 23 … + 35, in the design's order. The bot's copy, as
    /// <c>Perception</c> keeps the shuttle's: this project references no server code.
    /// </summary>
    public const byte MarketFirstReason = AckReasons.FirstApplicationReason + 23;

    /// <summary><c>MarketSold</c>: the listing went to somebody else first — a buy's race lost.</summary>
    public const byte MarketSoldReason = MarketFirstReason + 2;

    /// <summary>How many reasons the bazaar has of its own, 151 to 163.</summary>
    public const int MarketReasons = 13;

    /// <summary>
    /// <c>NotInWorld</c> (142): the missions' reason, which the bazaar reuses (bazaar.md § 3.1) — counted as a market refusal only when it answers one of
    /// this bot's market commands, by sequence.
    /// </summary>
    public const byte NotInWorldReason = AckReasons.FirstApplicationReason + 14;

    /// <summary>The refusals a bot counts: the bazaar's own 13, then <see cref="NotInWorldReason"/>.</summary>
    public const int MarketRefusals = MarketReasons + 1;

    /// <summary>The refusals' names in the server's words, by slot: offset from <see cref="MarketFirstReason"/>, <c>notInWorld</c> last.</summary>
    public static readonly string[] MarketReasonNames =
    [
        "notAtBazaar", "wrongCity", "sold", "notEnough", "noItems", "badListing", "listingCap", "reservedCap", "noRoom", "notYours", "ownListing",
        "failed", "worthless", "notInWorld",
    ];

    /// <summary>The slot a refusal is counted in, or -1 for one that is not the bazaar's; a 142 only when it answers a market command.</summary>
    public static int MarketRefusalSlot(byte reason, bool marketCommand)
    {
        var offset = reason - MarketFirstReason;
        return offset is >= 0 and < MarketReasons ? offset : reason == NotInWorldReason && marketCommand ? MarketReasons : -1;
    }

    /// <summary>The fleet's bazaar, summed: trades begun, commands sent by kind, races lost, buys collected, refusals by reason.</summary>
    public MarketTotals Market()
    {
        var t = new MarketTotals { Refused = new long[MarketRefusals] };
        foreach (var bot in Bots)
        {
            if (bot == null)
            {
                continue;
            }

            t.Trades += Volatile.Read(ref bot.MarketTrades);
            t.Listed += Volatile.Read(ref bot.MarketListed);
            t.Vendored += Volatile.Read(ref bot.MarketVendored);
            t.VendorCredits += Volatile.Read(ref bot.MarketVendorCredits);
            t.Bought += Volatile.Read(ref bot.MarketBought);
            t.BuyLost += Volatile.Read(ref bot.MarketBuyLost);
            t.Won += Volatile.Read(ref bot.MarketWon);
            t.Collected += Volatile.Read(ref bot.MarketCollected);
            for (var i = 0; i < MarketRefusals; i++)
            {
                t.Refused[i] += Volatile.Read(ref bot.MarketRefused[i]);
            }
        }

        return t;
    }

    /// <summary>A plan's command as the bazaar panel sends it: its catalog name and its fields, named as the server declares them (bazaar.md § 3.1).</summary>
    public static (string Command, RecordValues Values) Encode(MarketCommand c) => c.Step switch
    {
        MarketStep.Collect => (MarketCollectCommand, new RecordValues { ["listing"] = FieldValue.OfUInt64(c.Listing) }),
        MarketStep.List => (MarketListCommand, new RecordValues
        {
            ["template"] = FieldValue.Of((double)c.Template),
            ["mk"] = FieldValue.Of((double)c.Mk),
            ["quantity"] = FieldValue.Of((double)c.Quantity),
            ["unitPrice"] = FieldValue.Of((double)c.UnitPrice),
        }),
        MarketStep.Vendor => (SellToVendorCommand, new RecordValues
        {
            ["template"] = FieldValue.Of((double)c.Template),
            ["mk"] = FieldValue.Of((double)c.Mk),
            ["quantity"] = FieldValue.Of((double)c.Quantity),
        }),
        _ => (MarketBuyCommand, new RecordValues { ["listing"] = FieldValue.OfUInt64(c.Listing), ["unitPrice"] = FieldValue.Of((double)c.UnitPrice) }),
    };

    private sealed partial class Bot
    {
        /// <summary>The bazaar (bazaar.md § 8): trade visits, commands sent by kind, the vendor's expected payout, races lost, buys collected.</summary>
        public long MarketTrades;

        public long MarketListed;
        public long MarketVendored;
        public long MarketVendorCredits;
        public long MarketBought;
        public long MarketBuyLost;
        public long MarketWon;
        public long MarketCollected;

        /// <summary>The bazaar's refusals of this bot's commands, by slot (<see cref="MarketRefusalSlot"/>).</summary>
        public readonly long[] MarketRefused = new long[MarketRefusals];

        /// <summary>The market's prices are read again after this long (bazaar.md § 8).</summary>
        private static readonly TimeSpan PricesFor = TimeSpan.FromSeconds(60);

        /// <summary>Whether this bot trades: its run does, it can read its market, and it thinks.</summary>
        private bool _trades;

        /// <summary>The rush (bazaar.md § 8): this bot keeps listings on the board when <see cref="_rushTarget"/> is not 0, and buys otherwise.</summary>
        private bool _rush;

        private int _rushTarget;
        private Action<int, ulong> _onWin;

        private Uri _mineUri;
        private Uri _pricesUri;
        private Uri _browseUri;
        private Uri _walletUri;

        /// <summary>The market as last read: its listings and reservations, the prices and when, the stacks held.</summary>
        private volatile MarketMine _mine = MarketMine.None;

        private volatile MarketPrices _prices = MarketPrices.None;
        private DateTime _pricesAt = DateTime.MinValue;
        private volatile List<HeldStack> _held = [];

        // `/mine` re-read on each `items` revision while the last answer named something — the browser's rule (bazaar.md § 5, "Told").
        private readonly OwnerFieldPoll _minePoll;

        /// <summary>One trade read at a time; its plan goes to <see cref="_sender"/> and out from the driver at the commands' rate.</summary>
        private int _tradeBusy;

        private readonly MarketSender _sender = new();
        private uint _marketRng;

        /// <summary>The rush's buyer: listings it has tried, and the units it won and has yet to list again.</summary>
        private readonly HashSet<ulong> _rushTried = [];

        private readonly Queue<WonListing> _relist = new();
        private readonly object _relistLock = new();

        /// <summary>Wires the bazaar: whether this bot trades, its rush role, its endpoints. Called once its brain exists.</summary>
        private void SetUpMarket(int index, BotSwarmOptions options)
        {
            _trades = options.Market && _missionsUri != null && !string.IsNullOrEmpty(_token);
            if (!_trades)
            {
                return;
            }

            var server = new Uri(_missionsUri, "/swg/");
            _mineUri = new Uri(server, "market/mine");
            _pricesUri = new Uri(server, "market/prices");
            _browseUri = new Uri(server, "market");
            _walletUri = new Uri(server, "wallet");
            _marketRng = (uint)((index * 2654435769u) ^ 0x5bd1e995u) | 1u;
            _onWin = options.OnMarketWin;
            _brain.Trades = true;

            var rush = options.MarketRushRegion;
            if (rush != 0)
            {
                // The load test: every bot to the rush city's bazaars, missions off. One in ten keeps listings; the rest buy.
                _rush = true;
                _brain.RushRegion = rush;
                _brain.TakesMissions = false;
                var sellers = options.MarketRushSellers;
                _rushTarget = index % 10 == 0 ? Math.Max(1, (options.MarketRushListings + sellers - 1) / sellers) : 0;
            }
        }

        /// <summary>Counts the bazaar's refusals of this session's commands, from each applied frame's <c>ACKS</c>: the only answer a command gets.</summary>
        public void WatchMarket()
        {
            if (_trades)
            {
                Client.FrameApplied += CountRefusals;
            }
        }

        /// <summary>The sequences of its last market commands, for a refusal whose reason is not the bazaar's own (142).</summary>
        private readonly ushort[] _marketSeqs = new ushort[16];

        private int _marketSeqAt;

        private bool SentMarket(ushort seq)
        {
            lock (_marketSeqs)
            {
                return seq != 0 && Array.IndexOf(_marketSeqs, seq) >= 0;
            }
        }

        private void CountRefusals(WorldStore store)
        {
            var acks = store.Acks;
            for (var i = 0; i < acks.Count; i++)
            {
                var slot = MarketRefusalSlot(acks[i].Reason, SentMarket(acks[i].Seq));
                if (slot >= 0)
                {
                    Interlocked.Increment(ref MarketRefused[slot]);
                    if (acks[i].Reason == MarketSoldReason)
                    {
                        Interlocked.Increment(ref MarketBuyLost);
                    }
                }
            }
        }

        /// <summary>What the brain is told of the market: the stacks held and the goods reserved.</summary>
        private Perceived WithMarket(Perceived world) =>
            _trades
                ? world with { InventoryStacks = _inventory.Stacks, Reserved = _mine.Reserved, Trading = Volatile.Read(ref _tradeBusy) != 0 || _sender.Busy }
                : world;

        /// <summary>The bot stands at a bazaar: read what the plan needs, and plan. One at a time; a trade asked while one is read is the same trade.</summary>
        private void BeginTrade(long tick, int region)
        {
            if (!_trades || Interlocked.CompareExchange(ref _tradeBusy, 1, 0) != 0)
            {
                return;
            }

            MarketTrades++;
            var level = _brain.CombatLevel;
            _ = TradeAsync(region, level, _brain.VisitSerial);
        }

        private async Task TradeAsync(int region, int level, long visit)
        {
            try
            {
                var plan = _rush ? await RushRoundAsync(region).ConfigureAwait(false) : await PlanTradeAsync(region, level).ConfigureAwait(false);
                if (plan is { Count: > 0 })
                {
                    _sender.Offer(visit, plan);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                          or FormatException or OverflowException)
            {
                // Nothing sent: the next visit plans again.
            }
            finally
            {
                Volatile.Write(ref _tradeBusy, 0);
            }
        }

        /// <summary>A visit's plan (<see cref="MarketPlan.Plan"/>): its market read, a full material stack listed one visit in five, a buy one in four.</summary>
        private async Task<List<MarketCommand>> PlanTradeAsync(int region, int level)
        {
            var mine = await ReadMineAsync().ConfigureAwait(false);
            var prices = await PricesAsync().ConfigureAwait(false);
            var listMaterial = MarketPlan.Next(ref _marketRng) % 100u < MarketPlan.ListMaterialPercent;
            BrowseRow? buy = null;
            if (MarketPlan.Next(ref _marketRng) % 100u < MarketPlan.BuyPercent && region != 0)
            {
                // The cheapest of its band in this city: the browser's first page, filtered by Mk.
                var band = MarketPlan.Band(level);
                var page = MarketJson.Browse(await GetAsync(new Uri(_browseUri, $"?city={region}&mk={band}&sort=price&page=1")).ConfigureAwait(false));
                var wallet = MarketJson.Credits(await GetAsync(_walletUri).ConfigureAwait(false));
                buy = MarketPlan.ChooseBuy(page, prices, band, wallet, mine.ListedIds);
            }

            return MarketPlan.Plan(_held, mine, prices, ref _marketRng, listMaterial, buy);
        }

        /// <summary>
        /// A round of the rush (bazaar.md § 8): a seller tops its listings up to its share with one material unit each at 1 cr; a buyer lists again what
        /// it won, then buys the three cheapest listings of the city's first page it has not tried. Four commands at most: two seconds at the rate.
        /// </summary>
        private async Task<List<MarketCommand>> RushRoundAsync(int region)
        {
            const int Budget = 4;
            if (_rushTarget > 0)
            {
                var mine = await ReadMineAsync().ConfigureAwait(false);
                return MarketPlan.RushSell(_held, mine.Listed, _rushTarget, Budget);
            }

            var plan = new List<MarketCommand>();
            lock (_relistLock)
            {
                if (_relist.TryPeek(out var won) && won.Template != 0)
                {
                    _relist.Dequeue();
                    plan.Add(MarketCommand.List(won.Template, won.Mk, Math.Min(won.Count, MarketPlan.StackMax), 1));
                }
                else if (_relist.Count > 0)
                {
                    _relist.Dequeue();   // an item the bot cannot name to the server: not listed
                }
            }

            var page = MarketJson.Browse(await GetAsync(new Uri(_browseUri, $"?city={region}&sort=price&page=1")).ConfigureAwait(false));
            plan.AddRange(MarketPlan.RushBuy(page, _rushTried, _mine.ListedIds, Budget - 1));
            return plan;
        }

        /// <summary>Sends the next command of the plan when one is due — two a second — while the bot still stands at the bazaar; drops it otherwise.</summary>
        private async Task SendMarketAsync(long tick, CancellationToken ct)
        {
            if (!_trades)
            {
                return;
            }

            if (_sender.Next(_brain.AtBazaar, _brain.VisitSerial, tick, Math.Max(1L, _ticksPerSecond / 2)) is not { } c)
            {
                return;
            }

            var (command, values) = Encode(c);
            if (Client.Plan.CommandByName(command) == null)
            {
                return;   // a server without the bazaar: nothing sent, nothing counted
            }

            var seq = await Client.SendCommandAsync(command, values, ct).ConfigureAwait(false);
            lock (_marketSeqs)
            {
                _marketSeqs[_marketSeqAt++ % _marketSeqs.Length] = seq;
            }

            switch (c.Step)
            {
                case MarketStep.Collect:
                    MarketCollected++;
                    break;
                case MarketStep.List:
                    MarketListed++;
                    break;
                case MarketStep.Vendor:
                    MarketVendored++;
                    MarketVendorCredits += VendorPayout(c);
                    break;
                case MarketStep.Buy:
                    MarketBought++;
                    break;
            }

            IntentsSent++;
        }

        /// <summary>What the vendor should pay for a sale, as the bot can know it (the design's ⌊count × value × 20 %⌋): the report's estimate.</summary>
        private long VendorPayout(MarketCommand c)
        {
            foreach (var s in _held)
            {
                if (s.Template == c.Template && s.Mk == c.Mk)
                {
                    return MarketPlan.VendorPayout(c.Quantity, _prices.ValueOf(s.Item, s.Mk));
                }
            }

            return 0;
        }

        /// <summary>
        /// The bazaar's half of an inventory read: the stacks held, for the plan, and each buy seen collected — a win, logged with its listing and, in
        /// the rush, listed again.
        /// </summary>
        private void SawInventory(List<HeldStack> held)
        {
            if (_trades)
            {
                _held = held;
            }
        }

        /// <summary>Bazaar buys collected, as the session was told of them: each is a listing won, and a rush bot relists it.</summary>
        private void SawWins(List<WonListing> won)
        {
            if (!_trades)
            {
                return;
            }

            foreach (var w in won)
            {
                Interlocked.Increment(ref MarketWon);
                _onWin?.Invoke(Index, w.Listing);
                if (_rush && _rushTarget == 0)
                {
                    lock (_relistLock)
                    {
                        _relist.Enqueue(w);
                    }
                }
            }
        }

        /// <summary>
        /// Re-reads <c>/mine</c> on an <c>items</c> revision while the last answer named something, or — a rush bot — while it stands at its bazaar: the
        /// browser's rule, panel open or not.
        /// </summary>
        private void PollMine(long tick)
        {
            var self = Client.Store?.Self;
            if (!_trades || self == null || (_mine.Empty && !(_rush && _brain.Goal == Goal.Visiting)))
            {
                return;
            }

            if (_minePoll.TryBegin(_minePoll.Revision(self), tick))
            {
                _ = ReadMineAsync(tick);
            }
        }

        private async Task ReadMineAsync(long tick)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var ok = false;
            try
            {
                await ReadMineAsync().ConfigureAwait(false);
                ok = true;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                          or FormatException or OverflowException)
            {
                // Asked again after the poll's back-off, counted from now.
            }
            finally
            {
                _minePoll.End(ok, tick + TicksSince(started));
            }
        }

        private async Task<MarketMine> ReadMineAsync()
        {
            var mine = MarketMine.Parse(await GetAsync(_mineUri).ConfigureAwait(false));
            _mine = mine;
            return mine;
        }

        /// <summary>The prices, read again when a minute old.</summary>
        private async Task<MarketPrices> PricesAsync()
        {
            if (DateTime.UtcNow - _pricesAt < PricesFor)
            {
                return _prices;
            }

            var prices = MarketPrices.Parse(await GetAsync(_pricesUri).ConfigureAwait(false));
            _prices = prices;
            _pricesAt = DateTime.UtcNow;
            return prices;
        }

        /// <summary>A GET with the bot's own token, as the browser's; a refusal throws, and the trade is planned again next visit.</summary>
        private async Task<string> GetAsync(Uri uri)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var response = await JournalHttp.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>The fleet's bazaar, summed (bazaar.md § 8).</summary>
public struct MarketTotals
{
    public long Trades;
    public long Listed;
    public long Vendored;
    public long VendorCredits;
    public long Bought;
    public long BuyLost;
    public long Won;
    public long Collected;

    /// <summary>Refusals by slot (<see cref="BotSwarm.MarketRefusalSlot"/>): the bazaar's reasons, then <c>notInWorld</c>.</summary>
    public long[] Refused;

    /// <summary>Every refusal.</summary>
    public readonly long RefusedTotal
    {
        get
        {
            long sum = 0;
            foreach (var r in Refused ?? [])
            {
                sum += r;
            }

            return sum;
        }
    }
}

/// <summary>
/// A trade's plan on its way out (bazaar.md § 8): one command at a time, at the commands' rate, and only while the bot stands at the bazaar of the visit the
/// plan was read for. Anything else is dropped, counted: a market command from a body farther than 6 m from a terminal is refused
/// <c>MarketNotAtBazaar</c>, and live that was 48 refusals a second — plans landing after the bot had left, and queues drained on the walk to the next errand.
/// </summary>
public sealed class MarketSender
{
    private sealed record Offered(long Visit, List<MarketCommand> Plan);

    private readonly Queue<MarketCommand> _queue = new();
    private Offered _offered;
    private long _visit;
    private long _nextAt = long.MinValue;

    /// <summary>Commands dropped unsent: their visit was over, or a newer plan replaced them.</summary>
    public long Dropped { get; private set; }

    /// <summary>Whether a plan is still to go out: the bot stands at the bazaar meanwhile (<c>Perceived.Trading</c>).</summary>
    public bool Busy => _queue.Count > 0 || Volatile.Read(ref _offered) != null;

    /// <summary>Hands over the plan read for <paramref name="visit"/>; any thread.</summary>
    public void Offer(long visit, List<MarketCommand> plan) => Volatile.Write(ref _offered, new Offered(visit, plan));

    /// <summary>The command to send now, or null; on the driver's thread.</summary>
    /// <param name="atBazaar">Whether the bot stands at a bazaar's terminal using it.</param>
    /// <param name="visit">Its errand now: a plan read for another is dropped.</param>
    /// <param name="tick">The driver's tick.</param>
    /// <param name="spacing">Ticks between two commands: the rate.</param>
    public MarketCommand? Next(bool atBazaar, long visit, long tick, long spacing)
    {
        if (Interlocked.Exchange(ref _offered, null) is { } offered)
        {
            Dropped += _queue.Count;
            _queue.Clear();
            _visit = offered.Visit;
            foreach (var c in offered.Plan)
            {
                _queue.Enqueue(c);
            }
        }

        if (_queue.Count == 0)
        {
            return null;
        }

        if (!atBazaar || visit != _visit)
        {
            Dropped += _queue.Count;
            _queue.Clear();
            return null;
        }

        if (tick < _nextAt)
        {
            return null;
        }

        _nextAt = tick + spacing;
        return _queue.Dequeue();
    }
}
