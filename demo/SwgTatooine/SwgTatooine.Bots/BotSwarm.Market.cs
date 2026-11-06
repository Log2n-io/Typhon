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
    private const string BuyStandardCommand = "BuyStandard";
    private const string MarketCollectCommand = "MarketCollect";
    private const string SellToVendorCommand = "SellToVendor";
    private const string MarketCancelCommand = "MarketCancel";

    /// <summary>The inventory window's discard (inventory-loot.md § 10): what a worn-down item the vendor pays nothing for goes by.</summary>
    private const string DiscardItemCommand = "DiscardItem";

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
        MarketStep.Cancel => (MarketCancelCommand, new RecordValues { ["listing"] = FieldValue.OfUInt64(c.Listing) }),
        MarketStep.List => (MarketListCommand, new RecordValues
        {
            ["template"] = FieldValue.Of((double)c.Template),
            ["mk"] = FieldValue.Of((double)c.Mk),
            ["quantity"] = FieldValue.Of((double)c.Quantity),
            ["unitPrice"] = FieldValue.Of((double)c.UnitPrice),
            ["item"] = FieldValue.OfUInt64(c.Item),
        }),
        MarketStep.Vendor => (SellToVendorCommand, new RecordValues
        {
            ["template"] = FieldValue.Of((double)c.Template),
            ["mk"] = FieldValue.Of((double)c.Mk),
            ["quantity"] = FieldValue.Of((double)c.Quantity),
            ["item"] = FieldValue.OfUInt64(c.Item),
        }),
        MarketStep.Standard => (BuyStandardCommand, new RecordValues
        {
            ["template"] = FieldValue.Of((double)c.Template),
            ["price"] = FieldValue.Of((double)c.UnitPrice),
            ["quantity"] = FieldValue.Of((double)Math.Max(1, c.Quantity)),
        }),
        MarketStep.Discard => (DiscardItemCommand, new RecordValues
        {
            // The units planned — a stack's, a crate's whole count — not one: a stack thrown away for room left its slot taken (review, 2026-11-05).
            ["item"] = FieldValue.OfUInt64(c.Item), ["count"] = FieldValue.Of((double)Math.Max(1, c.Quantity)),
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

        /// <summary>Standard-issue items bought (crafting-build.md § 6).</summary>
        public long StandardBought;

        /// <summary>Gear of its band bought off the board (§ 6, Gear), and items with stats it will not use listed (§ 6, Surplus).</summary>
        public long GearBought;

        public long ItemsListed;
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
        private Uri _standardUri;
        private List<StandardOffer> _standard;

        /// <summary>The market as last read: its listings and reservations, the prices and when, the stacks held.</summary>
        private volatile MarketMine _mine = MarketMine.None;

        private volatile MarketPrices _prices = MarketPrices.None;
        private DateTime _pricesAt = DateTime.MinValue;
        private volatile List<HeldStack> _held = [];

        // The items with stats it holds, worn or not, as the last inventory read said (crafting-build.md § 2.1).
        private volatile List<GearItem> _gear = [];

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
            _standardUri = new Uri(server, "market/standard");
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

            if (_trades)
            {
                Client.FrameApplied += CountCraftRefusals;
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
                ? world with
                {
                    InventoryStacks = _inventory.Stacks,
                    InventorySlots = _inventory.Slots,
                    // Goods waiting in the city whose bazaar it would trade at (BotBrain.Bazaar: its home's, else where it stands): goods in another
                    // city are no errand here.
                    Reserved = _mine.ReservedIn(_brain.HomeRegion != 0 ? _brain.HomeRegion : _brain.RegionAt(world.X, world.Z)),
                    // Goods anywhere else: gear bought where it was stronger, to fetch (crafting-build.md § 6.1).
                    PickupRegion = _mine.ElsewhereThan(_brain.HomeRegion != 0 ? _brain.HomeRegion : _brain.RegionAt(world.X, world.Z)),
                    Trading = Volatile.Read(ref _tradeBusy) != 0 || _sender.Busy,
                }
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

        /// <summary>
        /// A visit's plan (<see cref="MarketPlan.Plan"/>, crafting-build.md § 6): its market read; its gear first — a listing for every slot it lacks or could
        /// better, in this city or, 20 % stronger, in another to fetch; the vendor's standard issue for every slot still empty (Loïc, 2026-11-05: strength
        /// is the priority driver) — then a kit, a stim, a spare, what its next craft lacks, else one visit in four the cheapest of its band; a full material
        /// stack listed one visit in five; the items with stats it will not use listed; and room made in its grid for all it brings in, or the least wanted
        /// of it left. What it brings in goes out first, what it sells after.
        /// </summary>
        private async Task<List<MarketCommand>> PlanTradeAsync(int region, int level)
        {
            var all = await ReadMineAsync().ConfigureAwait(false);

            // What waits for it here, not anywhere: a collect takes only this city's, and goods elsewhere must not hold its gear back.
            var mine = all with { Reserved = all.ReservedIn(region) };
            var prices = await PricesAsync().ConfigureAwait(false);
            var listMaterial = MarketPlan.Next(ref _marketRng) % 100u < MarketPlan.ListMaterialPercent;
            var band = MarketPlan.Band(level);
            var gear = _gear;
            long? wallet = null;
            async Task<long> Wallet() => wallet ??= MarketJson.Credits(await GetAsync(_walletUri).ConfigureAwait(false));
            async Task<List<BrowseRow>> PageIn(int city, string query) =>
                MarketJson.Browse(await GetAsync(new Uri(_browseUri, $"?city={city}&{query}&sort=price&page=1")).ConfigureAwait(false));
            Task<List<BrowseRow>> Page(string query) => PageIn(region, query);

            // What the visit brings in: its gear off the board (here, or elsewhere to fetch), the vendor's issues, the spare's, the other buys off the board
            // (a kit, a stim, a spare, a craft's input, the roll's buy), the stim and the craft inputs from the vendor.
            var gearBuys = new List<(BrowseRow Row, GearNeed Need, bool Remote)>();
            var issues = new List<MarketCommand>();
            MarketCommand? spareIssue = null;
            var otherBuys = new List<BrowseRow>();
            MarketCommand? stimIssue = null;
            List<MarketCommand> inputsFromVendor = [];
            long budget = -1;

            // Not while goods wait for it: the piece or the input it bought last time is among them, and its gear and stacks are read before the collect.
            if (region != 0 && mine.Reserved == 0)
            {
                _standard ??= await Optional(async () => MarketJson.Standard(await GetAsync(_standardUri).ConfigureAwait(false))).ConfigureAwait(false);
                budget = await Wallet().ConfigureAwait(false);
                IReadOnlyList<HeldStack> held = _held;

                // A kit before a replacement (Loïc, 2026-11-04): worn down with a repair left, a kit and the station's fee cost a fraction of a new piece —
                // and the piece it repairs is no longer wanted.
                if (GearPlan.KitWanted(gear, _held, CraftPlan.Blaster, band) is var kit && kit != 0
                    && await Optional(async () => await Page($"item={CraftPlan.OutputItem(kit)}").ConfigureAwait(false)).ConfigureAwait(false) is { } kits
                    && GearPlan.ChooseKit(kits, prices, budget, mine.ListedIds) is { } kitRow)
                {
                    otherBuys.Add(kitRow);
                    budget -= kitRow.Total;
                    held = [.. _held, new HeldStack(kit, kitRow.Item, MarketCategory.Other, 0, kitRow.Quantity)];
                    Interlocked.Increment(ref KitsAsked);
                }

                // Its gear (crafting-build.md § 6.1): every slot it lacks or could better, the most wanted first — the strongest listing of its band in either
                // family of armour, planet-wide, read a Mk at a time where the board lists any (a page is the 20 cheapest, every Mk mixed: the Mk I filled
                // it). Bought here, or elsewhere and fetched when 20 % stronger. A slot with nothing keeps the vendor's price back for its standard issue,
                // which fills it meanwhile, so an upgrade elsewhere cannot spend it.
                var vendorSlots = Slots(MarketPlan.StandardIssues(_standard, gear, long.MaxValue / 4, held, CraftPlan.Blaster, [], band));
                var covered = new List<int>();
                long kept = 0;
                var pages = 0;
                foreach (var need in MarketPlan.GearNeeds(gear, _standard, band, held, CraftPlan.Blaster))
                {
                    if (gearBuys.Count >= MarketPlan.MaxGearBuys || pages >= MarketPlan.MaxGearPages || budget - kept < MarketPlan.StandardReserve)
                    {
                        break;
                    }

                    // Its piece bought elsewhere and not yet fetched: not bought twice — the vendor's issue fills an empty slot till the pick-up.
                    if (all.WaitsElsewhere(need.Item, region) || all.WaitsElsewhere(MarketPlan.Twin(need.Item), region))
                    {
                        kept += need.HaveMk == 0 && vendorSlots.Contains(need.Slot) ? GearPlan.PriceOf(_standard, need.Item) : 0;
                        continue;
                    }

                    var rows = new List<BrowseRow>();
                    foreach (var item in new[] { need.Item, MarketPlan.Twin(need.Item) })
                    {
                        for (var mk = Math.Max(1, band); item != null && mk > need.HaveMk && pages < MarketPlan.MaxGearPages; mk--)
                        {
                            if (prices.ListedOf(item, mk) > 0)
                            {
                                rows.AddRange(await Optional(async () => await PageIn(0, $"item={item}&mk={mk}").ConfigureAwait(false)).ConfigureAwait(false)
                                    ?? []);
                                pages++;
                            }
                        }
                    }

                    var vendorFills = need.HaveMk == 0 && vendorSlots.Contains(need.Slot);
                    var (row, remote) = MarketPlan.ChooseGearAnywhere(rows, prices, need, band, budget - kept, mine.ListedIds, _standard, region);
                    if (row is { } bought)
                    {
                        gearBuys.Add((bought, need, remote));
                        budget -= bought.Total;
                        if (!remote)
                        {
                            covered.Add(need.Slot);
                        }
                    }

                    if (vendorFills && (row == null || remote))
                    {
                        kept += GearPlan.PriceOf(_standard, need.Item);
                    }
                }

                // The vendor's standard issue for every slot still empty — one whose piece comes from another city too, till it is fetched — the weapon
                // first: a bot on its fist is armed before anything else.
                issues = MarketPlan.StandardIssues(_standard, gear, budget, held, CraftPlan.Blaster, covered, band);
                foreach (var issue in issues)
                {
                    budget -= issue.UnitPrice;
                }

                // Short of stims (Loïc, 2026-11-05): the board's cheapest full one under the vendor's price, else the vendor's.
                var stimPrice = GearPlan.PriceOf(_standard, GearPlan.StimItem);
                if (GearPlan.StimWanted(gear, budget, stimPrice))
                {
                    var stims = await Optional(async () => await Page($"item={GearPlan.StimItem}").ConfigureAwait(false)).ConfigureAwait(false) ?? [];
                    if (GearPlan.ChooseStim(stims, stimPrice, budget, mine.ListedIds) is { } stim)
                    {
                        otherBuys.Add(stim);
                        budget -= stim.Total;
                    }
                    else
                    {
                        stimIssue = MarketCommand.Standard(GearPlan.Stim, stimPrice);
                        budget -= stimPrice;
                    }

                    Interlocked.Increment(ref StimBuysPlanned);
                }

                // A spare (Loïc, 2026-11-04): armed, carrying none, no weapon coming this visit, and its wallet holds one and SpareReserve over it — the
                // board's best value, else the vendor's.
                var sparePrice = GearPlan.PriceOf(_standard, GearPlan.WeaponItem(_standard, CraftPlan.Blaster));
                if (!gearBuys.Exists(b => b.Need.Slot == GearPlan.WeaponSlot) && !issues.Exists(c => c.Template == CraftPlan.Blaster)
                    && GearPlan.SpareWanted(gear, CraftPlan.Blaster, held, budget, sparePrice, band))
                {
                    var item = GearPlan.WeaponItem(_standard, CraftPlan.Blaster);
                    var rows = await Optional(async () => await Page($"item={item}").ConfigureAwait(false)).ConfigureAwait(false) ?? [];
                    if (MarketPlan.ChooseGear(rows, prices, 0, band, budget - GearPlan.SpareReserve, mine.ListedIds, _standard) is { } spare)
                    {
                        otherBuys.Add(spare);
                        budget -= spare.Total;
                    }
                    else
                    {
                        spareIssue = MarketCommand.Standard(CraftPlan.Blaster, sparePrice);
                        budget -= sparePrice;
                    }

                    Interlocked.Increment(ref SparesAsked);
                }

                // Then what its next craft lacks (§ 6, Inputs): demand from the recipe, not a roll — when it buys nothing for its gear.
                if (gearBuys.Count == 0 && issues.Count == 0 && spareIssue == null && otherBuys.Count == 0 && CraftWant() is { } want
                    && await Optional(async () => await Page($"item={want.Item}").ConfigureAwait(false)).ConfigureAwait(false) is { } inputs)
                {
                    var input = CraftPlan.ChooseInput(inputs, prices, want, budget, mine.ListedIds);
                    CraftInputsBought += input != null ? 1 : 0;
                    if (input is { } bought)
                    {
                        otherBuys.Add(bought);
                        budget -= bought.Total;
                    }
                    else
                    {
                        // None on the board: the vendor's Mk I, every material its next target lacks that the vendor sells (Loïc, 2026-11-02).
                        inputsFromVendor = CraftPlan.FromVendorAll(_book, _craftTargets[_craftTurn % _craftTargets.Length], _held, CraftPlan.PartsOf(_gear),
                            _standard, budget);
                        CraftInputsBought += inputsFromVendor.Count;
                    }

                    if (input == null && inputsFromVendor.Count == 0)
                    {
                        Volatile.Write(ref _inputBackoffUntil, Environment.TickCount64 + InputBackoffMs);
                    }
                }
            }

            // A crafter's core above Mk I, the highest the board lists and is not glutted with the item at: what makes a Mk II+ item (CraftPlan.CoreMks).
            if (region != 0 && mine.Reserved == 0 && Crafts && _craftTargets.Length > 0 && budget > 0)
            {
                var target = _craftTargets[_craftTurn % _craftTargets.Length];
                var coreBought = false;
                foreach (var mk in CraftPlan.CoreMks(_book, target, _held, prices))
                {
                    var core = CraftPlan.CoreOf(_book, target).Value;
                    var rows = await Optional(async () => await Page($"item={core.Item}&mk={mk}").ConfigureAwait(false)).ConfigureAwait(false) ?? [];
                    if (CraftPlan.ChooseInput(rows, prices, core, budget, mine.ListedIds) is { } bought)
                    {
                        otherBuys.Add(bought);
                        budget -= bought.Total;
                        Interlocked.Increment(ref CoresBought);
                        coreBought = true;
                        break;
                    }
                }

                // No core of the Mk it would craft on the board: the Mk below, to refine up at the station (CraftPlan.RefineBuy).
                if (!coreBought && CraftPlan.RefineBuy(_book, target, _held, prices) is { } lack)
                {
                    var rows = await Optional(async () => await Page($"item={lack.Core.Item}&mk={lack.Mk}").ConfigureAwait(false)).ConfigureAwait(false) ?? [];
                    foreach (var row in CraftPlan.ChooseMany(rows, prices, lack.Core.Item, lack.Count, budget, mine.ListedIds))
                    {
                        otherBuys.Add(row);
                        budget -= row.Total;
                        Interlocked.Increment(ref CoresForRefine);
                    }
                }
            }

            if (gearBuys.Count == 0 && issues.Count == 0 && spareIssue == null && otherBuys.Count == 0 && inputsFromVendor.Count == 0 && region != 0
                && MarketPlan.Next(ref _marketRng) % 100u < MarketPlan.BuyPercent
                && MarketPlan.ChooseBuy(await Page($"mk={band}").ConfigureAwait(false), prices, band, await Wallet().ConfigureAwait(false), mine.ListedIds)
                    is { } roll)
            {
                // The cheapest of its band in this city: the browser's first page, filtered by Mk.
                otherBuys.Add(roll);
            }

            // What the assessment asked for has been asked for: the next assessment in its own time.
            Traded();

            // An installation owner with nothing to place and nothing placed buys the vendor's extractor kit (crafting-build.md § 10) — out of what the visit
            // has left, not the wallet it came with.
            var install = region != 0
                && InstallPlan.BuysKit(Installs, _installs.Count, KitHeld() != 0, budget >= 0 ? budget : await Wallet().ConfigureAwait(false));

            // What it sells: collect aside (it goes first), its stacks listed and vendored, and the items with stats it will not use — on the board where
            // the plan left room, to the vendor past it while the grid is over its floor (§ 6, Surplus).
            var maxListed = Crafts ? MarketPlan.CrafterMaxListed : MarketPlan.MaxListed;
            var stimsKept = Crafts ? GearPlan.CrafterStimsKept : GearPlan.StimsKept;
            var selling = MarketPlan.Plan(Tradeable(_held), mine with { Reserved = 0 }, prices, ref _marketRng, listMaterial, null, _inventory.Stacks,
                maxListed, Crafts ? MarketPlan.CrafterKitsKept : MarketPlan.KitsKept);
            var gone = selling.FindAll(c => c.Step is MarketStep.List or MarketStep.Vendor).Count;
            selling.AddRange(MarketPlan.Surplus(gear, prices, mine.Listed + selling.FindAll(c => c.Step == MarketStep.List).Count, band, ref _marketRng,
                CraftPlan.Blaster, _inventory.Stacks - gone > MarketPlan.KeepStacks, _held, _standard, maxListed, stimsKept));

            // Room for what comes in (Loïc, 2026-11-05) — before it, so before what it sells: the cheapest goods and the items it will not use go to the vendor
            // or the bin; failing room, the least wanted of what it would bring in stays where it is (Drop).
            while (true)
            {
                var incoming = mine.Reserved + gearBuys.FindAll(b => !b.Remote).Count + issues.Count + (spareIssue != null ? 1 : 0) + otherBuys.Count
                    + (stimIssue != null ? 1 : 0) + (install ? 1 : 0) + NewStacks(inputsFromVendor);
                var room = MarketPlan.MakeRoom([], Tradeable(_held), gear, prices, _inventory.Capacity, _inventory.Slots, incoming, band, CraftPlan.Blaster,
                    out var shortBy, stimsKept);
                if (shortBy > 0 && Drop())
                {
                    continue;
                }

                var plan = new List<MarketCommand>(room);
                if (mine.Reserved > 0)
                {
                    plan.Add(MarketCommand.Collect());
                }

                var anyHere = false;
                foreach (var b in gearBuys)
                {
                    plan.Add(MarketCommand.Buy(b.Row.Id, b.Row.Unit));
                    anyHere |= !b.Remote;
                    GearRemote += b.Remote ? 1 : 0;
                }

                foreach (var b in otherBuys)
                {
                    plan.Add(MarketCommand.Buy(b.Id, b.Unit));
                    anyHere = true;
                }

                // One collect for what was bought here; what was bought elsewhere waits there, for the pick-up (Perceived.PickupRegion).
                if (anyHere)
                {
                    plan.Add(MarketCommand.Collect());
                }

                plan.AddRange(issues);
                if (spareIssue is { } spareNow)
                {
                    plan.Add(spareNow);
                }

                if (stimIssue is { } stimNow)
                {
                    plan.Add(stimNow);
                }

                plan.AddRange(inputsFromVendor);
                if (install)
                {
                    plan.Add(MarketCommand.Standard(InstallPlan.Kit, InstallPlan.KitPrice));
                    Interlocked.Increment(ref KitsBought);
                }

                // Then what it sells, but what the room already took.
                foreach (var c in selling)
                {
                    if (!room.Exists(r => c.Item != 0 ? r.Item == c.Item : r.Item == 0 && r.Template == c.Template && r.Mk == c.Mk)
                        && !room.Exists(r => r.Step == MarketStep.Discard && c.Item == 0 && r.Template == c.Template && r.Mk == c.Mk))
                    {
                        plan.Add(c);
                    }
                }

                GearBought += gearBuys.Count;
                GearRoomMade += room.Count;
                ItemsListed += plan.FindAll(c => c.Step == MarketStep.List && c.Item != 0).Count;
                return plan;
            }

            // Leaves the least wanted of what it would bring in: the installation kit, the craft's vendor inputs, the other buys from the last, the vendor's
            // stim and spare, the upgrades from the smallest gain, the armour for an empty slot, the weapon last. False when nothing is left to leave.
            bool Drop()
            {
                GearLeftForRoom++;
                if (install)
                {
                    install = false;
                }
                else if (inputsFromVendor.Count > 0)
                {
                    inputsFromVendor = [];
                }
                else if (otherBuys.Count > 0)
                {
                    otherBuys.RemoveAt(otherBuys.Count - 1);
                }
                else if (stimIssue != null)
                {
                    stimIssue = null;
                }
                else if (spareIssue != null)
                {
                    spareIssue = null;
                }
                else if (gearBuys.FindLastIndex(b => !b.Remote && b.Need.HaveMk > 0) is var upgrade && upgrade >= 0)
                {
                    gearBuys.RemoveAt(upgrade);
                }
                else if (issues.FindLastIndex(c => c.Template != CraftPlan.Blaster) is var piece && piece >= 0)
                {
                    issues.RemoveAt(piece);
                }
                else if (gearBuys.FindLastIndex(b => !b.Remote && b.Need.Slot != GearPlan.WeaponSlot) is var fill && fill >= 0)
                {
                    gearBuys.RemoveAt(fill);
                }
                else if (issues.Count > 0)
                {
                    issues.RemoveAt(issues.Count - 1);
                }
                else if (gearBuys.FindLastIndex(b => !b.Remote) is var weaponBuy && weaponBuy >= 0)
                {
                    gearBuys.RemoveAt(weaponBuy);
                }
                else
                {
                    GearLeftForRoom--;
                    return false;
                }

                return true;
            }
        }

        // The equip slots a list of the vendor's issues fills.
        private List<int> Slots(List<MarketCommand> issues)
        {
            var slots = new List<int>();
            foreach (var issue in issues)
            {
                foreach (var o in _standard ?? [])
                {
                    if (o.Template == issue.Template)
                    {
                        slots.Add(o.Wears);
                    }
                }
            }

            return slots;
        }

        // The vendor's inputs that open a new stack: a material it holds none of at that Mk.
        private int NewStacks(List<MarketCommand> inputs)
        {
            var n = 0;
            foreach (var c in inputs)
            {
                n += _held.Exists(h => h.Template == c.Template && h.Mk == c.Mk) ? 0 : 1;
            }

            return n;
        }

        /// <summary>
        /// Commands a visit sent to make room for its goods (<see cref="MarketPlan.MakeRoom"/>), goods it left for want of room, and gear bought in another
        /// city to fetch.
        /// </summary>
        public long GearRoomMade;

        public long GearLeftForRoom;
        public long GearRemote;

        /// <summary>Cores above Mk I a crafter bought off the board, to craft above Mk I (<see cref="CraftPlan.CoreMks"/>).</summary>
        public long CoresBought;

        /// <summary>Cores a crafter bought to refine up (<see cref="CraftPlan.RefineBuy"/>): N of the Mk below the one the board lacks.</summary>
        public long CoresForRefine;

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
                    plan.Add(MarketCommand.List(won.Template, won.Mk, Math.Min(won.Count, MarketPlan.LotSize), 1));
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
                case MarketStep.Standard:
                    StandardBought++;
                    break;
                case MarketStep.Discard:
                    Interlocked.Increment(ref GearDiscarded);
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

        /// <summary>An optional read of a trade: its failure costs that part of the plan, not the visit's collect and vendoring.</summary>
        private static async Task<T> Optional<T>(Func<Task<T>> read) where T : class
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                          or FormatException)
            {
                return null;
            }
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

/// <summary>A trade's plan on its way out (bazaar.md § 8): see <see cref="PlanSender{T}"/>.</summary>
public sealed class MarketSender : PlanSender<MarketCommand>;

/// <summary>
/// A plan on its way out — a trade's (bazaar.md § 8), a craft's (crafting-build.md C2): one command at a time, at the commands' rate, and only while the bot
/// stands at the terminal of the visit the plan was read for. Anything else is dropped, counted: a market command from a body farther than 6 m from a
/// terminal is refused <c>MarketNotAtBazaar</c>, and live that was 48 refusals a second — plans landing after the bot had left, and queues drained on the
/// walk to the next errand.
/// </summary>
public class PlanSender<T> where T : struct
{
    private sealed record Offered(long Visit, List<T> Plan);

    private int _abort;

    /// <summary>Drops what is still queued, at the next <see cref="Next"/>; any thread — a refusal read off the driver.</summary>
    public void Abort() => Volatile.Write(ref _abort, 1);

    private readonly Queue<T> _queue = new();
    private Offered _offered;
    private long _visit;
    private long _nextAt = long.MinValue;

    /// <summary>Commands dropped unsent: their visit was over, or a newer plan replaced them.</summary>
    public long Dropped { get; private set; }

    /// <summary>Whether a plan is still to go out: the bot stands at the bazaar meanwhile (<c>Perceived.Trading</c>).</summary>
    public bool Busy => _queue.Count > 0 || Volatile.Read(ref _offered) != null;

    /// <summary>Hands over the plan read for <paramref name="visit"/>; any thread.</summary>
    public void Offer(long visit, List<T> plan) => Volatile.Write(ref _offered, new Offered(visit, plan));

    /// <summary>The command to send now, or null; on the driver's thread.</summary>
    /// <param name="atBazaar">Whether the bot stands at the plan's terminal using it.</param>
    /// <param name="visit">Its errand now: a plan read for another is dropped.</param>
    /// <param name="tick">The driver's tick.</param>
    /// <param name="spacing">Ticks between two commands: the rate.</param>
    public T? Next(bool atBazaar, long visit, long tick, long spacing)
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

        if (Interlocked.Exchange(ref _abort, 0) != 0)
        {
            Dropped += _queue.Count;
            _queue.Clear();
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
