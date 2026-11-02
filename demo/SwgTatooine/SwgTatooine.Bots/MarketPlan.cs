using System;
using System.Collections.Generic;

namespace SwgTatooine.Bots;

/// <summary>A bazaar command, as the bazaar panel sends it (bazaar.md § 3.1).</summary>
public enum MarketStep : byte
{
    /// <summary><c>MarketCollect</c>: <see cref="MarketCommand.Listing"/>, 0 for all its goods reserved at this city.</summary>
    Collect,

    /// <summary><c>MarketList</c>: template, Mk, quantity and unit price.</summary>
    List,

    /// <summary><c>SellToVendor</c>: template, Mk and quantity.</summary>
    Vendor,

    /// <summary><c>MarketBuy</c>: the listing and the unit price it was shown at.</summary>
    Buy,

    /// <summary><c>BuyStandard</c> (crafting-catalog.md § 15.3): the template and the price it was shown at.</summary>
    Standard,
}

/// <summary>One command of a trade.</summary>
/// <remarks><see cref="Item"/> names an item with stats, listed or sold one at a time by its id (crafting-build.md § 2.1); 0 for units.</remarks>
public readonly record struct MarketCommand(MarketStep Step, ushort Template, int Mk, int Quantity, long UnitPrice, ulong Listing, ulong Item = 0)
{
    public static MarketCommand Collect(ulong listing = 0) => new(MarketStep.Collect, 0, 0, 0, 0, listing);

    public static MarketCommand List(ushort template, int mk, int quantity, long unit) => new(MarketStep.List, template, mk, quantity, unit, 0);

    public static MarketCommand Vendor(ushort template, int mk, int quantity) => new(MarketStep.Vendor, template, mk, quantity, 0, 0);

    public static MarketCommand Buy(ulong listing, long unit) => new(MarketStep.Buy, 0, 0, 0, unit, listing);

    public static MarketCommand Standard(ushort template, long price) => new(MarketStep.Standard, template, 1, 1, price, 0);
}

/// <summary>
/// What a bot does at a bazaar (bazaar.md § 8), as a pure function of what it read — its stacks, its listings, the prices, a browse page — and its own
/// rolls: the plan the swarm then sends at the commands' rate.
/// </summary>
/// <remarks>
/// <para>
/// <b>In order: collect, list, vendor, buy.</b> Collect first, so goods waiting for it come in; list its cores and add-ons at
/// <c>value × U(0.9, 1.3)</c> while it has fewer than <see cref="MaxListed"/>; one in five visits also lists one full material stack at
/// <c>value × U(0.9, 1.1)</c>; then the vendor takes stacks, the cheapest unit first (the materials, then cores past those listed), until
/// <see cref="KeepStacks"/> are left — the floor that keeps a drop from being lost. A buy, when the visit rolled one, comes last.
/// </para>
/// <para>
/// <b>An item with no value is neither listed nor bought</b>: a bot does not invent a price. The value is the board's, else the model's
/// (<see cref="MarketPrices.ValueOf"/>). The vendor takes what has a value, the cheapest unit first.
/// </para>
/// </remarks>
public static class MarketPlan
{
    /// <summary>A bot keeps at most this many listings (the design's 5; the server's cap is 20).</summary>
    public const int MaxListed = 5;

    /// <summary>The vendor takes material stacks until this many stacks are left: 22 slots free for the next quest's drops.</summary>
    /// <remarks>18 left twelve, and the bots back at the bazaar after a quest were full again within it (live, 2026-10-27; see BotBrain.MarketStacks).</remarks>
    public const int KeepStacks = 8;

    /// <summary>A full stack, the server's <c>ItemLedger.StackMax</c>.</summary>
    public const int StackMax = 99;

    /// <summary>The highest unit price the server lists at (<c>TatooineData.MaxUnitPrice</c>).</summary>
    public const long MaxUnitPrice = 1_000_000;

    /// <summary>A core or add-on is listed at its value times a draw in this range.</summary>
    public const double ListLow = 0.9;

    public const double ListHigh = 1.3;

    /// <summary>A full material stack is listed at its value times a draw in this range: near its value, so a buyer at ≤ <see cref="BuyOverValue"/> takes it.</summary>
    public const double MaterialLow = 0.9;

    public const double MaterialHigh = 1.1;

    /// <summary>One visit in this many hundred lists a full material stack, and in <see cref="BuyPercent"/> buys.</summary>
    public const int ListMaterialPercent = 20;

    public const int BuyPercent = 25;

    /// <summary>The item with stats a bot puts on next: the first in its grid whose equip slot nothing is worn in; 0 when there is none.</summary>
    public static ulong NextToWear(IReadOnlyList<GearItem> gear)
    {
        foreach (var g in gear)
        {
            if (g.Worn || g.Wears == 0)
            {
                continue;
            }

            var free = true;
            foreach (var w in gear)
            {
                free &= !(w.Worn && w.Slot == g.Wears);
            }

            if (free)
            {
                return g.Id;
            }
        }

        return 0;
    }

    /// <summary>What a bot keeps in its wallet after buying standard issue: a few repairs' and fees' worth (crafting-build.md § 6).</summary>
    public const long StandardReserve = 1_500;

    /// <summary>
    /// The standard-issue armour piece a bot buys (crafting-build.md § 6, C1's gear rule): the cheapest padded one for an armour slot nothing worn or held
    /// covers, if the wallet keeps <see cref="StandardReserve"/> after it; none when every slot is covered or it cannot afford one.
    /// </summary>
    public static MarketCommand? ChooseStandard(IReadOnlyList<StandardOffer> offers, IReadOnlyList<GearItem> gear, long credits)
    {
        if (offers == null)
        {
            return null;
        }

        StandardOffer? best = null;
        foreach (var o in offers)
        {
            if (o.Wears is < 102 or > 107 || !o.Item.StartsWith("armor_padded_", StringComparison.Ordinal) || credits - o.Price < StandardReserve)
            {
                continue;
            }

            var covered = false;
            foreach (var g in gear)
            {
                covered |= g.Wears == o.Wears;
            }

            if (!covered && (best == null || o.Price < best.Value.Price))
            {
                best = o;
            }
        }

        return best is { } b ? MarketCommand.Standard(b.Template, b.Price) : null;
    }

    /// <summary>A buy pays at most this times the value a unit, and at most <see cref="BuyWalletShare"/> of the wallet.</summary>
    /// <remarks>
    /// At 1.1 and one visit in ten, 2 000 bots listing 1 901 times in five minutes made one sale (live, 2026-10-27): cores are listed at up to 1.3 × value.
    /// </remarks>
    public const double BuyOverValue = 1.2;

    public const double BuyWalletShare = 0.3;

    /// <summary>
    /// What the vendor pays for <paramref name="count"/> units worth <paramref name="value"/> credits each: the server's floor of count × value × 20 %,
    /// computed as it computes it, in milli-credits (<c>MarketValues.VendorPayout</c>); 0 is refused <c>MarketWorthless</c>.
    /// </summary>
    public static long VendorPayout(int count, double value)
    {
        var milli = (long)Math.Round(value * 1000d, MidpointRounding.AwayFromZero);
        return count <= 0 ? 0 : count * milli * VendorBuysPerMille / 1000 / 1000;
    }

    /// <summary>The vendor's share of the value, per mille (<c>TatooineData.VendorBuysPerMille</c>).</summary>
    public const long VendorBuysPerMille = 200;

    /// <summary>The Mk band of a combat level (content-levels.md: <c>min(5, (level − 1) / 10 + 1)</c>): the Mk a bot of that level buys.</summary>
    public static int Band(int level) => Math.Clamp(((Math.Max(1, level) - 1) / 10) + 1, 1, 5);

    /// <summary>A unit price: <paramref name="value"/> times a draw in [<paramref name="low"/>, <paramref name="high"/>), whole credits, 1 at least.</summary>
    public static long Price(double value, double low, double high, ref uint rng)
    {
        var draw = low + ((high - low) * ((Next(ref rng) >> 8) / 16_777_216d));
        return Math.Clamp((long)Math.Round(value * draw, MidpointRounding.AwayFromZero), 1L, MaxUnitPrice);
    }

    /// <summary>The trade: collect, list, vendor and — given — buy.</summary>
    /// <param name="held">The stacks it holds.</param>
    /// <param name="mine">Its listings and reservations.</param>
    /// <param name="prices">Market values.</param>
    /// <param name="rng">Its own stream: the prices' draws.</param>
    /// <param name="listMaterial">Whether this visit lists a full material stack (rolled by the caller, <see cref="ListMaterialPercent"/>).</param>
    /// <param name="buy">The listing to buy, or null (<see cref="ChooseBuy"/>).</param>
    /// <returns>The commands, in the order they are sent.</returns>
    public static List<MarketCommand> Plan(IReadOnlyList<HeldStack> held, MarketMine mine, MarketPrices prices, ref uint rng, bool listMaterial,
        BrowseRow? buy)
    {
        var plan = new List<MarketCommand>();
        if (mine.Reserved > 0)
        {
            plan.Add(MarketCommand.Collect());
        }

        // Stacks still in the grid as the plan goes: each listed or vendored stack leaves it.
        var left = new List<HeldStack>(held);
        var listed = mine.Listed;
        for (var i = 0; i < left.Count && listed < MaxListed; i++)
        {
            var s = left[i];
            var value = prices.ValueOf(s.Item, s.Mk);
            if (s.Category is MarketCategory.Core or MarketCategory.Addon && s.Template != 0 && value > 0d && s.Count > 0)
            {
                plan.Add(MarketCommand.List(s.Template, s.Mk, Math.Min(s.Count, StackMax), Price(value, ListLow, ListHigh, ref rng)));
                left.RemoveAt(i--);
                listed++;
            }
        }

        if (listMaterial && listed < MaxListed)
        {
            var full = left.FindIndex(s => s.Category == MarketCategory.Material && s.Template != 0 && s.Count >= StackMax && prices.ValueOf(s.Item, s.Mk) > 0d);
            if (full >= 0)
            {
                var s = left[full];
                plan.Add(MarketCommand.List(s.Template, s.Mk, StackMax, Price(prices.ValueOf(s.Item, s.Mk), MaterialLow, MaterialHigh, ref rng)));
                left.RemoveAt(full);
            }
        }

        // The vendor: whatever has a value, the cheapest unit first — the materials, then cores and add-ons past the five listed — and the smaller stack of
        // two, until the floor. A core kept because five are listed is a slot lost to the next quest's drops.
        var materials = left.FindAll(s => s.Template != 0 && s.Count > 0 && prices.ValueOf(s.Item, s.Mk) > 0d);
        materials.Sort((a, b) =>
        {
            var byValue = prices.ValueOf(a.Item, a.Mk).CompareTo(prices.ValueOf(b.Item, b.Mk));
            return byValue != 0 ? byValue : a.Mk != b.Mk ? a.Mk.CompareTo(b.Mk) : a.Count.CompareTo(b.Count);
        });
        var stacks = left.Count;
        foreach (var s in materials)
        {
            if (stacks <= KeepStacks)
            {
                break;
            }

            if (VendorPayout(Math.Min(s.Count, StackMax), prices.ValueOf(s.Item, s.Mk)) == 0)
            {
                continue;   // the vendor refuses a sale that pays nothing (MarketWorthless): the stack stays
            }

            plan.Add(MarketCommand.Vendor(s.Template, s.Mk, Math.Min(s.Count, StackMax)));
            stacks--;
        }

        if (buy is { } row)
        {
            plan.Add(MarketCommand.Buy(row.Id, row.Unit));
        }

        return plan;
    }

    /// <summary>
    /// What a buying visit buys from a browse page of its city: of its band, a core, an add-on or a material at ≤ <see cref="BuyOverValue"/> × its value
    /// a unit and ≤ <see cref="BuyWalletShare"/> of its wallet in all, not one of its own — the best bargain, the lowest price to value; null when none is.
    /// </summary>
    /// <remarks>
    /// Materials too, though the design names cores and add-ons: a core of band I is 726 cr and ≤ 30 % of a 1 000 cr wallet is 300, so a new bot could buy
    /// none, and a market whose buyers cannot pay is the one sale in five minutes measured live.
    /// </remarks>
    public static BrowseRow? ChooseBuy(IReadOnlyList<BrowseRow> rows, MarketPrices prices, int band, long wallet, IReadOnlyCollection<ulong> own)
    {
        BrowseRow? best = null;
        var bestRatio = double.MaxValue;
        foreach (var r in rows)
        {
            var value = prices.ValueOf(r.Item, r.Mk);
            if (r.Category is not (MarketCategory.Core or MarketCategory.Addon or MarketCategory.Material) || r.Mk != band || value <= 0d
                || r.Unit > BuyOverValue * value || r.Total > BuyWalletShare * wallet || Contains(own, r.Id))
            {
                continue;
            }

            var ratio = r.Unit / value;
            if (best is not { } b || ratio < bestRatio || (ratio == bestRatio && r.Unit < b.Unit))
            {
                best = r;
                bestRatio = ratio;
            }
        }

        return best;
    }

    /// <summary>A rush seller's round (bazaar.md § 8): one material unit listed at 1 cr for each listing it is short of <paramref name="target"/>.</summary>
    public static List<MarketCommand> RushSell(IReadOnlyList<HeldStack> held, int listed, int target, int budget)
    {
        var plan = new List<MarketCommand>();
        foreach (var s in held)
        {
            for (var n = 0; n < s.Count && listed < target && plan.Count < budget && s.Category == MarketCategory.Material && s.Template != 0; n++)
            {
                plan.Add(MarketCommand.List(s.Template, s.Mk, 1, 1));
                listed++;
            }
        }

        return plan;
    }

    /// <summary>A rush buyer's round: the <paramref name="count"/> cheapest listings of the page it has not tried and are not its own, each marked tried.</summary>
    public static List<MarketCommand> RushBuy(IReadOnlyList<BrowseRow> rows, HashSet<ulong> tried, IReadOnlyCollection<ulong> own, int count)
    {
        var sorted = new List<BrowseRow>(rows);
        sorted.Sort((a, b) => a.Unit != b.Unit ? a.Unit.CompareTo(b.Unit) : a.Id.CompareTo(b.Id));
        var plan = new List<MarketCommand>();
        foreach (var r in sorted)
        {
            if (plan.Count >= count)
            {
                break;
            }

            if (!Contains(own, r.Id) && tried.Add(r.Id))
            {
                plan.Add(MarketCommand.Buy(r.Id, r.Unit));
            }
        }

        return plan;
    }

    private static bool Contains(IReadOnlyCollection<ulong> set, ulong id)
    {
        foreach (var x in set)
        {
            if (x == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>xorshift32, the bots' generator.</summary>
    public static uint Next(ref uint rng)
    {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        return rng;
    }
}
