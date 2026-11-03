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

    public static MarketCommand Standard(ushort template, long price, int quantity = 1) => new(MarketStep.Standard, template, 1, quantity, price, 0);
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

    /// <summary>
    /// The item with stats a bot puts on next: the first in its grid whose equip slot nothing is worn in; else, from its <paramref name="band"/>, the first
    /// of a higher Mk than what is worn there and no higher than the band — an upgrade, which the server swaps in (crafting-build.md § 6, Gear). 0 when
    /// there is none; a band of 0 never upgrades. With a <paramref name="weapon"/> template, the weapon slot takes only that weapon: a bot that fights
    /// with a pistol does not disarm itself with a sword (review, 2026-11-02).
    /// </summary>
    public static ulong NextToWear(IReadOnlyList<GearItem> gear, int band = 0, ushort weapon = 0, IReadOnlyList<HeldStack> held = null)
    {
        foreach (var g in gear)
        {
            if (g.Worn || g.Crate || g.Wears == 0 || g.Mk > Math.Max(1, band) || !Fits(g, weapon) || g.WornDown)
            {
                continue;
            }

            var free = true;
            var better = false;
            foreach (var w in gear)
            {
                if (w.Worn && w.Slot == g.Wears)
                {
                    // A worn item to be replaced leaves its slot free for any (crafting-build.md § 6, Gear).
                    free = held != null && Spent(w, held);
                    better = band > 0 && g.Mk > w.Mk;
                }
            }

            if (free || better)
            {
                return g.Id;
            }
        }

        return 0;
    }

    // Nothing to repair: one list, never written.
    private static readonly List<ulong> NoRepairs = [];

    /// <summary>The bulk materials the vendor sells at Mk I (Loïc, 2026-11-02): Ore, Water, Metal, Crystal, Gas, Chemical, Fiber — ItemCatalog's ids.</summary>
    public static bool VendorSells(ushort template) => template is >= 1 and <= 7;

    /// <summary>Repairs an item takes at most (crafting-catalog.md § 9.2).</summary>
    public const int MaxRepairs = 2;

    /// <summary>The repair kits: a weapon's, an armour piece's (ItemCatalog's ids).</summary>
    public const ushort KitWeapon = 62;

    public const ushort KitArmor = 63;

    /// <summary>The kit an item of slot <paramref name="wears"/> is repaired with.</summary>
    public static ushort KitFor(int wears) => wears == 101 ? KitWeapon : KitArmor;

    /// <summary>
    /// The worn items to repair now (crafting-build.md § 6, Repair): worn down, a repair left, and a kit of its class held for each — the kits shared out
    /// in slot order.
    /// </summary>
    public static List<ulong> RepairsDue(IReadOnlyList<GearItem> gear, IReadOnlyList<HeldStack> held)
    {
        var any = false;
        foreach (var g in gear)
        {
            any |= g.Worn && g.WornDown && g.Repairs < MaxRepairs;
        }

        if (!any)
        {
            return NoRepairs;   // the common case: nothing allocated on every inventory read
        }

        var due = new List<ulong>();
        int weaponKits = 0, armorKits = 0;
        foreach (var h in held)
        {
            weaponKits += h.Template == KitWeapon ? h.Count : 0;
            armorKits += h.Template == KitArmor ? h.Count : 0;
        }

        foreach (var g in gear)
        {
            if (!g.Worn || !g.WornDown || g.Repairs >= MaxRepairs)
            {
                continue;
            }

            ref var kits = ref g.Wears == 101 ? ref weaponKits : ref armorKits;
            if (kits > 0)
            {
                kits--;
                due.Add(g.Id);
            }
        }

        return due;
    }

    /// <summary>
    /// Whether a worn item is to be replaced (crafting-build.md § 6, Gear): <b>broken</b> — at 0 with a repair left — and no kit of its class held to
    /// repair it. Its slot counts as empty: the board's piece or the vendor's is bought, and put on over it.
    /// </summary>
    /// <remarks>
    /// <b>Worn to the end, not swapped at 20 %</b> (Loïc, 2026-11-03: decay must finish its job). Replaced worn down, an item never used its repairs and never
    /// wore out — 3 574 broken and 0 destroyed in a night — and it was sold at full value, the vendor paying for wear. A worn item with no repair left is not
    /// spent either: it is worn until the wear destroys it, and its slot is then empty.
    /// </remarks>
    public static bool Spent(GearItem g, IReadOnlyList<HeldStack> held)
    {
        if (!g.Worn || !g.Broken || g.Repairs >= MaxRepairs)
        {
            return false;
        }

        var kit = KitFor(g.Wears);
        foreach (var h in held)
        {
            if (h.Template == kit && h.Count > 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What it lists is listed at its value times a draw in this range (crafting-build.md § 6, Surplus).</summary>
    public const double SurplusLow = 0.9;

    public const double SurplusHigh = 1.1;

    /// <summary>
    /// The items with stats it will not use, to list (§ 6, Surplus): those in its grid that are neither a part — a crafter crafts with them — nor the one it
    /// puts on next, nor of a slot it would still wear them in, at value × U(<see cref="SurplusLow"/>, <see cref="SurplusHigh"/>), while it has fewer than
    /// <see cref="MaxListed"/> listings. What it made, what an upgrade took off, what it cannot wear at its level.
    /// </summary>
    /// <remarks>
    /// With <paramref name="vendorRest"/> — the grid past its floor — what the listing cap leaves over goes to the vendor, one item at a time (§ 6,
    /// Surplus: "then sell to the vendor"): an item it will never use must not hold a slot the next quest's drops need.
    /// </remarks>
    public static List<MarketCommand> Surplus(IReadOnlyList<GearItem> gear, MarketPrices prices, int listed, int band, ref uint rng, ushort weapon = 0,
        bool vendorRest = false, IReadOnlyList<HeldStack> held = null)
    {
        var plan = new List<MarketCommand>();
        var wear = NextToWear(gear, band, weapon, held);
        foreach (var g in gear)
        {
            var value = prices.ValueOf(g.Item, g.Mk);
            // A crate (crafting-catalog.md § 11.2) is never worn: it is surplus, listed or sold whole.
            if (g.Worn || g.Id == wear || g.Template is >= CraftPlan.FirstPart and <= CraftPlan.LastPart || value <= 0d
                || (!g.Crate && Wanted(gear, g, band, weapon)))
            {
                continue;
            }

            // Worn down — what a replacement took off: never listed for another bot to buy and never wear; the vendor takes it (review, 2026-11-02).
            if (g.WornDown)
            {
                if (VendorPayout(1, value) > 0)
                {
                    plan.Add(new MarketCommand(MarketStep.Vendor, g.Template, g.Mk, 1, 0, 0, g.Id));
                }

                continue;
            }

            if (listed < MaxListed)
            {
                plan.Add(new MarketCommand(MarketStep.List, g.Template, g.Mk, g.Count, Price(value, SurplusLow, SurplusHigh, ref rng), 0, g.Id));
                listed++;
            }
            else if (vendorRest && VendorPayout(g.Count, value) > 0)
            {
                plan.Add(new MarketCommand(MarketStep.Vendor, g.Template, g.Mk, g.Count, 0, 0, g.Id));
            }
        }

        return plan;
    }

    // Whether an item may go in its slot: any armour; the weapon slot only the bot's weapon, when it names one.
    private static bool Fits(GearItem g, ushort weapon) => weapon == 0 || g.Wears != 101 || g.Template == weapon;

    // Whether an unworn item would still be put on: its slot's worn Mk is lower and it is within the band — the next read's NextToWear takes it.
    private static bool Wanted(IReadOnlyList<GearItem> gear, GearItem g, int band, ushort weapon)
    {
        if (g.Wears == 0 || g.Mk > Math.Max(1, band) || !Fits(g, weapon) || g.WornDown)
        {
            return false;
        }

        foreach (var w in gear)
        {
            if (w.Worn && w.Slot == g.Wears)
            {
                return g.Mk > w.Mk;
            }
        }

        return true;
    }

    /// <summary>
    /// The piece a bot looks for on the board (crafting-build.md § 6, Gear): its weapon when the best it has is below its band; else the first armour slot
    /// (102-107) it has nothing for, as the padded piece; else the first armour slot whose best is below its band, as the piece it wears. The asset to
    /// browse, the slot, and the best Mk it already has there (0 for none); null when its gear is of its band.
    /// </summary>
    /// <remarks>
    /// A crate covers nothing — it is never worn — and with a <paramref name="weapon"/> template, nor does a weapon of another kind: a sword in the grid of a
    /// bot that fights with a pistol left its spent blaster unreplaced (review, 2026-11-03).
    /// </remarks>
    public static (string Item, int Slot, int HaveMk)? GearWanted(IReadOnlyList<GearItem> gear, IReadOnlyList<StandardOffer> offers, int band,
        IReadOnlyList<HeldStack> held = null, ushort weapon = 0)
    {
        (string, int, int)? below = null;
        for (var slot = 101; slot <= 107; slot++)
        {
            var have = 0;
            string item = null;
            string spent = null;
            foreach (var g in gear)
            {
                if (g.Wears != slot || g.Crate || !Fits(g, weapon))
                {
                    continue;
                }

                // Worn down and past repair (or held so): it counts as nothing — its replacement is wanted, the same piece.
                if (g.WornDown && (held == null ? g.Broken : g.Worn ? Spent(g, held) : true))
                {
                    spent ??= g.Item;
                    continue;
                }

                if (g.Mk > have)
                {
                    have = g.Mk;
                    item = g.Item;
                }
            }

            if (have == 0 && spent != null)
            {
                return (spent, slot, 0);
            }

            if (have == 0 && slot > 101 && offers != null)
            {
                foreach (var o in offers)
                {
                    if (o.Wears == slot && o.Item.StartsWith("armor_padded_", StringComparison.Ordinal))
                    {
                        return (o.Item, slot, 0);
                    }
                }
            }

            if (have > 0 && have < band && item != null && (slot == 101 || below == null))
            {
                if (slot == 101)
                {
                    return (item, slot, have);
                }

                below = (item, slot, have);
            }
        }

        return below;
    }

    /// <summary>
    /// The listing to buy for a piece it wants (§ 6, Gear): of a browse page of that piece, the highest Mk above what it has and within its band, the best
    /// value of those (the lowest price to value), at ≤ <see cref="BuyOverValue"/> × its value, keeping <see cref="StandardReserve"/> in the wallet, not
    /// its own; null when none is — the vendor's standard issue is the fallback.
    /// </summary>
    public static BrowseRow? ChooseGear(IReadOnlyList<BrowseRow> rows, MarketPrices prices, int haveMk, int band, long wallet, IReadOnlyCollection<ulong> own)
    {
        BrowseRow? best = null;
        var bestRatio = double.MaxValue;
        foreach (var r in rows)
        {
            var value = prices.ValueOf(r.Item, r.Mk);
            // A crate (crafting-catalog.md § 11.2) is not bought to wear: it would have to be unpacked, and a bot wants one piece.
            if (r.Mk <= haveMk || r.Mk > Math.Max(1, band) || value <= 0d || r.Unit > BuyOverValue * value || wallet - r.Total < StandardReserve
                || Contains(own, r.Id) || r.WornDown || r.Quantity > 1)
            {
                continue;
            }

            var ratio = r.Unit / value;
            if (best is not { } b || r.Mk > b.Mk || (r.Mk == b.Mk && ratio < bestRatio))
            {
                best = r;
                bestRatio = ratio;
            }
        }

        return best;
    }

    /// <summary>What a bot keeps in its wallet after buying standard issue: a few repairs' and fees' worth (crafting-build.md § 6).</summary>
    public const long StandardReserve = 1_500;

    /// <summary>
    /// The standard-issue armour piece a bot buys (crafting-build.md § 6, C1's gear rule): the cheapest padded one for an armour slot nothing worn or held
    /// covers, if the wallet keeps <see cref="StandardReserve"/> after it; none when every slot is covered or it cannot afford one.
    /// </summary>
    /// <remarks>
    /// With the stacks held, a worn piece to be replaced (<see cref="Spent"/>) or a worn-down one in the grid covers nothing, and the bot's own weapon
    /// (<paramref name="weapon"/>) is bought too when its slot is so: the vendor is the fallback of the replacement rule (review, 2026-11-02).
    /// </remarks>
    public static MarketCommand? ChooseStandard(IReadOnlyList<StandardOffer> offers, IReadOnlyList<GearItem> gear, long credits,
        IReadOnlyList<HeldStack> held = null, ushort weapon = 0)
    {
        if (offers == null)
        {
            return null;
        }

        StandardOffer? best = null;
        foreach (var o in offers)
        {
            var piece = o.Wears is >= 102 and <= 107 && o.Item.StartsWith("armor_padded_", StringComparison.Ordinal);
            var arms = o.Wears == 101 && weapon != 0 && o.Template == weapon;
            if ((!piece && !arms) || credits - o.Price < StandardReserve)
            {
                continue;
            }

            var covered = false;
            foreach (var g in gear)
            {
                covered |= g.Wears == o.Wears && !g.Crate && Fits(g, weapon) && (held == null || (g.Worn ? !Spent(g, held) : !g.WornDown));
            }

            // The weapon first — a bot fighting with its fist is worse off than one short of gloves — then the cheapest piece.
            if (!covered && (best == null || (arms && best.Value.Wears != 101) || (arms == (best.Value.Wears == 101) && o.Price < best.Value.Price)))
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
        BrowseRow? buy, int gridStacks = -1)
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
        // The floor counts the whole grid when it is known — a crafter's kept inputs, its parts and its items too — not only what this plan may sell: a
        // grid full of what it keeps sends the bot back here for good, freeing nothing (review, 2026-11-02).
        var stacks = gridStacks >= 0 ? gridStacks - (held.Count - left.Count) : left.Count;
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
