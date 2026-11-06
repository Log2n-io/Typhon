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

    /// <summary><c>DiscardItem</c>: an item with stats thrown away — worn down, and the vendor would pay nothing for it (Loïc, 2026-11-04).</summary>
    Discard,

    /// <summary><c>MarketCancel</c>: one of its listings taken off the board — its goods reserved for it at that city, collected there (bazaar.md B5).</summary>
    Cancel,
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

    public static MarketCommand Discard(ushort template, int mk, ulong item) => new(MarketStep.Discard, template, mk, 1, 0, 0, item);

    public static MarketCommand Cancel(ulong listing) => new(MarketStep.Cancel, 0, 0, 0, 0, listing);
}

/// <summary>
/// A slot a bot wants better for (<see cref="MarketPlan.GearNeeds"/>): the asset browsed, the slot, and the best Mk and power it has there, 0 for none.
/// </summary>
public readonly record struct GearNeed(string Item, int Slot, int HaveMk, int HavePower);

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

    /// <summary>
    /// The units a bot lists, sells or counts as one: the server's stack before it held 100 000 (inventory-loot.md § 10). A bot's inventory pressure — the
    /// bazaar errand at <c>BotBrain.MarketStacks</c>, the vendor's floor at <see cref="KeepStacks"/> — is counted in these lots, so a bot carrying 300 Hide
    /// in one stack trades as it did when that was four (<see cref="Lots"/>).
    /// </summary>
    public const int LotSize = 99;

    /// <summary>A character's grid (the server's <c>ItemLedger.Slots</c>): 50 since 2026-11-05 (Loïc), the bot's copy for an answer silent on it.</summary>
    public const int GridSlots = 50;

    /// <summary>
    /// The most vendor sales one visit plans: the grid over the floor of <see cref="KeepStacks"/>, what a visit sold at most when a slot was a lot.
    /// </summary>
    public const int MaxVendorSalesPerVisit = GridSlots - KeepStacks;

    /// <summary>What a grid entry counts for against the floors: its lots for units, one for an item with stats.</summary>
    public static int Lots(int count, bool stats) => stats ? 1 : Math.Max(1, (count + LotSize - 1) / LotSize);

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
                    // A worn item to be replaced leaves its slot free for any (crafting-build.md § 6, Gear) — and a broken weapon at once, kit or no kit: it
                    // does nothing, and the spare goes on in the middle of a fight (Loïc, 2026-11-04). Not armour: one held for a kit would be swapped out
                    // and then thrown away unrepaired (review, 2026-11-04).
                    free = (w.Broken && w.Wears == GearPlan.WeaponSlot) || (held != null && Spent(w, held));
                    better = band > 0 && Better(g, w);
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
    /// The items to repair now (crafting-build.md § 6, Repair): worn — or the spare weapon it carries (<see cref="GearPlan.Spare"/>, with a
    /// <paramref name="weapon"/>) — worn down, a repair left, and a kit of its class held for each, the kits shared out in slot order.
    /// </summary>
    public static List<ulong> RepairsDue(IReadOnlyList<GearItem> gear, IReadOnlyList<HeldStack> held, ushort weapon = 0, int band = 5)
    {
        var spare = weapon != 0 ? GearPlan.Spare(gear, weapon, held, band) : 0;
        var any = false;
        foreach (var g in gear)
        {
            any |= (g.Worn || (spare != 0 && g.Id == spare)) && g.WornDown && g.Repairs < MaxRepairs;
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
            if (!(g.Worn || (spare != 0 && g.Id == spare)) || !g.WornDown || g.Repairs >= MaxRepairs)
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
    /// Whether a worn item is to be replaced (crafting-build.md § 6, Gear): <b>worn down</b> — broken, or under 20 % of its condition — and not to be repaired:
    /// past its repairs, or no kit of its class held. Its slot counts as empty: the board's piece or the vendor's is bought, and put on over it.
    /// </summary>
    /// <remarks>
    /// <b>Replaced under the threshold</b> (Loïc, 2026-11-04: "sell armor or destroy if its condition is below a threshold"), which supersedes 2026-11-03's
    /// "worn to the end". That rule answered an item sold at full value and never worn out; the vendor now pays by condition (nothing for a broken item), so a
    /// piece taken off at 20 % sells for a fifth of its value, and one it cannot sell is thrown away (<see cref="Surplus"/>): the sink stays.
    /// </remarks>
    public static bool Spent(GearItem g, IReadOnlyList<HeldStack> held)
    {
        if (!g.Worn || !g.WornDown)
        {
            return false;
        }

        if (g.Repairs >= MaxRepairs)
        {
            return true;
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
        bool vendorRest = false, IReadOnlyList<HeldStack> held = null, IReadOnlyList<StandardOffer> offers = null, int maxListed = MaxListed,
        int stimsKept = GearPlan.StimsKept)
    {
        var plan = new List<MarketCommand>();

        // The stims it drinks are kept; a part-drunk one is never listed — a buyer pays for three doses — nor sold to the vendor (GearPlan.StimsToSell).
        var stimsSold = GearPlan.StimsToSell(gear, stimsKept);
        var wear = NextToWear(gear, band, weapon, held);
        var spare = weapon != 0 ? GearPlan.Spare(gear, weapon, held, band) : 0;

        // The dearest first: the listing slots go to what fetches the most (the night of 2026-11-04: a crafter's slots full of 343 cr gloves, its blasters
        // went to the vendor at 181 cr where the board paid 1 400).
        var byValue = new List<GearItem>(gear);
        byValue.Sort((a, b) => (prices.ValueOf(b.Item, b.Mk) * b.Count).CompareTo(prices.ValueOf(a.Item, a.Mk) * a.Count));
        foreach (var g in byValue)
        {
            // The spare weapon it carries is kept (Loïc, 2026-11-04), as what it puts on next is.
            if (g.Worn || g.Id == wear || g.Id == spare || g.Template is >= CraftPlan.FirstPart and <= CraftPlan.LastPart)
            {
                continue;
            }

            if (g.Template == GearPlan.Stim && !stimsSold.Contains(g.Id))
            {
                continue;
            }

            // Worn down — what a replacement took off: never listed for another bot to buy and never worn again. The vendor takes it at its condition; one
            // broken, or worth nothing to it, is thrown away rather than kept in a slot (Loïc, 2026-11-04).
            var value = prices.ValueOf(g.Item, g.Mk);
            if (g.WornDown && !g.Crate)
            {
                plan.Add(!g.Broken && value > 0d && VendorPayout(1, value * Share(g)) > 0
                    ? new MarketCommand(MarketStep.Vendor, g.Template, g.Mk, 1, 0, 0, g.Id)
                    : MarketCommand.Discard(g.Template, g.Mk, g.Id));
                continue;
            }

            // A crate (crafting-catalog.md § 11.2) is never worn: it is surplus, listed or sold whole.
            if (value <= 0d || (!g.Crate && Wanted(gear, g, band, weapon)))
            {
                continue;
            }

            if (listed < maxListed)
            {
                plan.Add(new MarketCommand(MarketStep.List, g.Template, g.Mk, g.Count, ListPrice(value, g.Item, g.Mk, prices, offers, ref rng), 0, g.Id));
                listed++;
            }
            else if (vendorRest && VendorPayout(g.Count, value) > 0 && prices.ListedOf(g.Item, g.Mk) >= CraftPlan.SaturatedListings)
            {
                // Only what the board is glutted with: anything else sells there for several times the vendor's fifth of its value, and waits in the grid for
                // a free listing slot (the night of 2026-11-04: 6 700 DL-44s destroyed, 87 of them by wear — the rest dumped at 181 cr).
                plan.Add(new MarketCommand(MarketStep.Vendor, g.Template, g.Mk, g.Count, 0, 0, g.Id));
            }
        }

        return plan;
    }

    // A listing's condition as a share of its whole: the vendor's price is for a new piece.
    private static double RowShare(BrowseRow r) => r.MaxCondition > 0 ? Math.Clamp((double)r.Condition / r.MaxCondition, 0d, 1d) : 1d;

    // An item's condition as a share of its whole: what the vendor pays it at.
    private static double Share(GearItem g) => g.MaxCondition > 0 ? Math.Clamp((double)g.Condition / g.MaxCondition, 0d, 1d) : 1d;

    // Whether an item may go in its slot: any armour; the weapon slot only the bot's weapon, when it names one.
    private static bool Fits(GearItem g, ushort weapon) => weapon == 0 || g.Wears != 101 || g.Template == weapon;

    // Whether an unworn item would still be put on: better than what its slot wears and within the band — the next read's NextToWear takes it.
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
                return Better(g, w);
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="g"/> makes the bot stronger than <paramref name="worn"/> in the same slot: more power (<see cref="GearItem.Power"/>) when both
    /// are read, else a higher Mk — what it puts on, keeps and buys by, so it never buys what it would not wear (Loïc, 2026-11-05).
    /// </summary>
    public static bool Better(GearItem g, GearItem worn) => g.Power > 0 && worn.Power > 0 ? g.Power > worn.Power : g.Mk > worn.Mk;

    /// <summary>What a slot weighs in a bot's strength, per mille: the weapon half of it, each armour piece its share of the set (SWG's weights).</summary>
    public static int SlotWeight(int slot) => slot switch
    {
        101 => 500,
        102 => 130,
        103 => 330,
        104 => 220,
        105 => 200,
        _ => 60,
    };

    /// <summary>The same piece of the other armour family (padded, composite): both are browsed for a slot. The weapon has none; null.</summary>
    public static string Twin(string item) =>
        item == null ? null
        : item.StartsWith("armor_padded_", StringComparison.Ordinal) ? item.Replace("_padded_", "_composite_", StringComparison.Ordinal)
        : item.StartsWith("armor_composite_", StringComparison.Ordinal) ? item.Replace("_composite_", "_padded_", StringComparison.Ordinal)
        : null;

    /// <summary>
    /// What a bot lacks to be as strong as its level allows (Loïc, 2026-11-05: "a priority driver of the bots, they must buy what's best for them to be
    /// strong"): every slot it has nothing sound for — the weapon first, then the armour by its share of the set — then every slot whose best is below its
    /// band, the largest gain first (<see cref="SlotWeight"/> × the Mks short). The asset to browse, the slot, the best Mk and power it has there (0 for
    /// none). Empty when its gear is of its band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every slot, not the first.</b> It replaced a search that returned the weapon whenever the weapon was below the band: five DL-44s above Mk I were
    /// listed planet-wide, so a bot of CL 23+ asked for one every visit and never looked at its armour — 37 bots of 40 sampled wore Mk I, one of them at
    /// CL 31 with 216 000 cr, two broken pieces worn and four slots empty (live, 2026-11-05).
    /// </para>
    /// <para>
    /// A crate covers nothing — it is never worn — and with a <paramref name="weapon"/> template, nor does a weapon of another kind: a sword in the grid of a
    /// bot that fights with a pistol left its spent blaster unreplaced (review, 2026-11-03).
    /// </para>
    /// </remarks>
    public static List<GearNeed> GearNeeds(IReadOnlyList<GearItem> gear, IReadOnlyList<StandardOffer> offers, int band, IReadOnlyList<HeldStack> held = null,
        ushort weapon = 0)
    {
        var empty = new List<GearNeed>();
        var below = new List<GearNeed>();
        for (var slot = 101; slot <= 107; slot++)
        {
            GearItem? best = null;
            string spent = null;
            foreach (var g in gear)
            {
                // What its level cannot wear covers nothing: it is surplus (one worn above it cannot be, the server refusing it).
                if (g.Wears != slot || g.Crate || !Fits(g, weapon) || (!g.Worn && g.Mk > Math.Max(1, band)))
                {
                    continue;
                }

                // Worn down and past repair (or held so): it counts as nothing — its replacement is wanted, the same piece.
                if (g.WornDown && (held == null ? g.Broken : g.Worn ? Spent(g, held) : true))
                {
                    spent ??= g.Item;
                    continue;
                }

                if (best is not { } b || Better(g, b))
                {
                    best = g;
                }
            }

            if (best is { } have)
            {
                if (have.Mk < band)
                {
                    below.Add(new GearNeed(have.Item, slot, have.Mk, have.Power));
                }

                continue;
            }

            var item = spent ?? StandardFor(offers, slot, weapon);
            if (item != null)
            {
                empty.Add(new GearNeed(item, slot, 0, 0));
            }
        }

        // The armour by its share of the set, the heaviest first; the weapon, slot 101, is first already.
        // Between equals, the slot's order: a sort that is not stable must not change a bot's mind between two reads.
        empty.Sort((a, b) => a.Slot == 101 || b.Slot == 101 || SlotWeight(a.Slot) == SlotWeight(b.Slot)
            ? a.Slot.CompareTo(b.Slot)
            : SlotWeight(b.Slot).CompareTo(SlotWeight(a.Slot)));
        below.Sort((a, b) =>
        {
            var gain = (SlotWeight(b.Slot) * (band - b.HaveMk)).CompareTo(SlotWeight(a.Slot) * (band - a.HaveMk));
            return gain != 0 ? gain : a.Slot.CompareTo(b.Slot);
        });
        empty.AddRange(below);
        return empty;
    }

    /// <summary>The first of <see cref="GearNeeds"/>: the piece the bot wants most; null when its gear is of its band.</summary>
    public static (string Item, int Slot, int HaveMk)? GearWanted(IReadOnlyList<GearItem> gear, IReadOnlyList<StandardOffer> offers, int band,
        IReadOnlyList<HeldStack> held = null, ushort weapon = 0)
    {
        var needs = GearNeeds(gear, offers, band, held, weapon);
        return needs.Count > 0 ? (needs[0].Item, needs[0].Slot, needs[0].HaveMk) : null;
    }

    // What a slot with nothing in it is browsed by: the vendor's padded piece, or the bot's own weapon; null when the vendor's list names neither.
    private static string StandardFor(IReadOnlyList<StandardOffer> offers, int slot, ushort weapon)
    {
        foreach (var o in offers ?? [])
        {
            if (o.Wears == slot && (slot == 101 ? weapon != 0 && o.Template == weapon : o.Item.StartsWith("armor_padded_", StringComparison.Ordinal)))
            {
                return o.Item;
            }
        }

        return null;
    }

    /// <summary>
    /// The listing to buy for a piece it wants (§ 6, Gear): of a browse page of that piece, the strongest within its band that is better than what it has
    /// (<see cref="Better"/>: more power, or a higher Mk when either is unread) — the best value of equals (the lowest price to value) — at ≤
    /// <see cref="GearOverValue"/> × its value, keeping <see cref="StandardReserve"/> in the wallet, not its own; null when none is — the vendor's standard
    /// issue is the fallback.
    /// </summary>
    /// <param name="havePower">The power of what it has there (<see cref="GearNeed.HavePower"/>); 0 for nothing, or unread.</param>
    public static BrowseRow? ChooseGear(IReadOnlyList<BrowseRow> rows, MarketPrices prices, int haveMk, int band, long wallet, IReadOnlyCollection<ulong> own,
        IReadOnlyList<StandardOffer> offers = null, int havePower = 0)
    {
        BrowseRow? best = null;
        var bestRatio = double.MaxValue;
        var have = new GearItem(0, 0, 0, 0, Mk: haveMk) { Power = havePower };
        foreach (var r in rows)
        {
            var value = prices.ValueOf(r.Item, r.Mk);

            // Up to GearOverValue × its value — or up to the vendor's own price for it, which is what it would pay otherwise (Loïc, 2026-11-04: crafters
            // price under the vendor, and a buyer that refused them bought the vendor's dearer one).
            var cap = Math.Max(GearOverValue * value, StandardPrice(offers, r.Item, r.Mk) * RowShare(r));
            var row = Of(r);

            // A crate (crafting-catalog.md § 11.2) is not bought to wear: it would have to be unpacked, and a bot wants one piece.
            if ((haveMk > 0 && !WorthBuying(row, have)) || r.Mk > Math.Max(1, band) || value <= 0d || r.Unit > cap || wallet - r.Total < StandardReserve
                || Contains(own, r.Id) || r.WornDown || r.Quantity > 1)
            {
                continue;
            }

            var ratio = r.Unit / value;
            if (best is not { } b || Better(row, Of(b)) || (!Better(Of(b), row) && ratio < bestRatio))
            {
                best = r;
                bestRatio = ratio;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether a piece is worth buying over what the slot has: <see cref="UpgradeGain"/> more power when both are read, else a higher Mk — not 1 % more
    /// bought at twice its value.
    /// </summary>
    public static bool WorthBuying(GearItem g, GearItem have) =>
        g.Power > 0 && have.Power > 0 ? g.Power * 100L >= have.Power * (long)(UpgradeGain * 100) : g.Mk > have.Mk;

    /// <summary>The least gain in power a bought upgrade brings: a Mk I to Mk II armour piece is about +75 %, a weapon's +20 %.</summary>
    public const double UpgradeGain = 1.1;

    /// <summary>
    /// The least a listing in another city beats what the bot would have without travelling by, in power, to be bought there and fetched (Loïc,
    /// 2026-11-05: "if they find +20% better elsewhere, they must buy remotely and go pick it up"). Gear only: consumables are bought where it stands.
    /// </summary>
    public const double RemoteGain = 1.2;

    /// <summary>
    /// The listing to buy for a slot from a planet-wide page (<see cref="ChooseGear"/>, the rows of every city): its own city's best — or another city's,
    /// when that is <see cref="RemoteGain"/> stronger than what it would have here: the city's best, else for an empty slot the vendor's issue, else what
    /// it has. <c>Remote</c> says it is bought elsewhere, to fetch (<c>Perceived.PickupRegion</c>).
    /// </summary>
    /// <param name="city">The city it stands in, as the listings name it (a region).</param>
    public static (BrowseRow? Row, bool Remote) ChooseGearAnywhere(IReadOnlyList<BrowseRow> rows, MarketPrices prices, GearNeed need, int band, long wallet,
        IReadOnlyCollection<ulong> own, IReadOnlyList<StandardOffer> offers, int city)
    {
        var here = new List<BrowseRow>();
        var away = new List<BrowseRow>();
        foreach (var r in rows)
        {
            (r.City == city ? here : away).Add(r);
        }

        var local = ChooseGear(here, prices, need.HaveMk, band, wallet, own, offers, need.HavePower);
        var remote = ChooseGear(away, prices, need.HaveMk, band, wallet, own, offers, need.HavePower);
        if (remote is not { } far)
        {
            return (local, false);
        }

        var baseline = local is { } near ? near.Power : need.HaveMk == 0 ? StandardPower(offers, need.Item) : need.HavePower;
        return baseline <= 0
            ? local == null ? (far, true) : (local, false)
            : far.Power * 100L >= baseline * (long)(RemoteGain * 100) ? (far, true) : (local, false);
    }

    // The power of the vendor's issue of a piece, or of its other family's; 0 when it does not sell either.
    private static int StandardPower(IReadOnlyList<StandardOffer> offers, string item)
    {
        var twin = Twin(item);
        foreach (var o in offers ?? [])
        {
            if (o.Item == item || o.Item == twin)
            {
                return o.Power;
            }
        }

        return 0;
    }

    // A listing as the item it would be in the grid: what Better compares.
    private static GearItem Of(BrowseRow r) => new(r.Id, 0, 0, 0, Mk: r.Mk) { Power = r.Power };

    /// <summary>
    /// The most a bot pays for a piece to wear, × its value (Loïc, 2026-11-05: strength is the priority): over the 1.2 a trade good is bought at, as the
    /// board above Mk I is thin and a bot of CL 20+ holds 100 000 cr and more.
    /// </summary>
    public const double GearOverValue = 2.0;

    /// <summary>
    /// The most pieces a visit buys off the board, and the browse pages it reads for them: one per slot wanted, the armour's two families each.
    /// </summary>
    public const int MaxGearBuys = 7;

    public const int MaxGearPages = 24;

    /// <summary>The vendor's price for an item at a Mk — standard issue is Mk I only (crafting-catalog.md § 15.3); 0 when it does not sell it.</summary>
    public static long StandardPrice(IReadOnlyList<StandardOffer> offers, string item, int mk)
    {
        if (offers == null || item == null || mk != 1)
        {
            return 0;
        }

        foreach (var o in offers)
        {
            if (o.Item == item)
            {
                return o.Price;
            }
        }

        return 0;
    }

    /// <summary>What an item with stats is listed at: its value's draw — but under the vendor's price where the vendor sells it, and under the board.</summary>
    /// <remarks>
    /// Loïc, 2026-11-04: CRAFTERS SHOULD GET RICH. A crafted DL-44 Mk I was listed at its value, 906 cr, while the vendor's sells for 1 812: half what the
    /// buyer would pay anyway. Where the vendor sells the piece it is listed at <see cref="UnderVendorLow"/>-<see cref="UnderVendorHigh"/> of its price;
    /// a listing already lower is undercut by 2 % — never below the value.
    /// </remarks>
    public static long ListPrice(double value, string item, int mk, MarketPrices prices, IReadOnlyList<StandardOffer> offers, ref uint rng)
    {
        var price = Price(value, SurplusLow, SurplusHigh, ref rng);
        var vendor = StandardPrice(offers, item, mk);
        if (vendor > 0)
        {
            price = Math.Max(price, Price(vendor, UnderVendorLow, UnderVendorHigh, ref rng));
        }

        // Undercut a cheaper listing — down to a floor, the vendor's price × UnderVendorFloor where the vendor sells it: the lowest is planet-wide and may be
        // its own, and crafters undercutting each other by 2 % slid to the value, half the vendor's (review, 2026-11-04).
        var lowest = prices.LowestOf(item, mk);
        if (lowest > 0 && price >= lowest)
        {
            var floor = Math.Max((long)Math.Ceiling(value), (long)(vendor * UnderVendorFloor));
            price = Math.Max(floor, (long)(lowest * 0.98));
        }

        return Math.Clamp(price, 1L, MaxUnitPrice);
    }

    /// <summary>A kit is listed at its value times this before the draw: no vendor sells one, and it saves a weapon six times its price.</summary>
    public const double KitMarkup = 1.6;

    /// <summary>The share of the vendor's price a piece the vendor also sells is listed at.</summary>
    public const double UnderVendorLow = 0.75;

    public const double UnderVendorHigh = 0.9;

    /// <summary>The lowest share of the vendor's price an undercut goes to.</summary>
    public const double UnderVendorFloor = 0.6;

    /// <summary>
    /// The listings a crafter keeps: it lives off them — the server's cap (<c>TatooineData.MaxListings</c>). At 12 its slots were full of slow armour and
    /// kits, and it crafted 305 blasters for every 7 it could list (the night of 2026-11-04, from the item ledger).
    /// </summary>
    public const int CrafterMaxListed = 20;

    /// <summary>Repair kits of each class a bot keeps for itself; past them, it lists the rest. A kit never goes to the vendor.</summary>
    public const int KitsKept = 2;

    /// <summary>
    /// A crafter's: it makes kits to sell, and two kept each left the board empty (the night of 2026-11-04: 483 armour kits made, none listed).
    /// </summary>
    public const int CrafterKitsKept = 1;

    /// <summary>Kits a listing holds at most: no more than a buyer keeps (<see cref="KitsKept"/>), or it lists the rest again (review, 2026-11-04).</summary>
    public const int KitLot = 2;

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
        IReadOnlyList<HeldStack> held = null, ushort weapon = 0, int band = 5)
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
                // A worn piece covers its slot unless it is broken or past its repairs: one only worn down waits for a kit or the board's piece, not the vendor's
                // Mk I — a composite Mk II swapped for padded Mk I and sold at a twentieth of its value (review, 2026-11-04).
                // Nor one in the grid its level cannot wear (GearNeeds' rule): a looted Mk III torso left a CL 15 bot bare-chested (review, 2026-11-05).
                covered |= g.Wears == o.Wears && !g.Crate && Fits(g, weapon) && (g.Worn || g.Mk <= Math.Max(1, band))
                    && (held == null || (g.Worn ? !(g.Broken || (g.WornDown && g.Repairs >= MaxRepairs)) : !g.WornDown));
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

    /// <summary>The most of its own Mk I core and add-on listings a visit takes off the board: two seconds of commands each, so a few a visit.</summary>
    public const int UnclogPerVisit = 6;

    /// <summary>The trade: collect, list, vendor and — given — buy.</summary>
    /// <param name="held">The stacks it holds.</param>
    /// <param name="mine">Its listings and reservations.</param>
    /// <param name="prices">Market values.</param>
    /// <param name="rng">Its own stream: the prices' draws.</param>
    /// <param name="listMaterial">Whether this visit lists a full material stack (rolled by the caller, <see cref="ListMaterialPercent"/>).</param>
    /// <param name="buy">The listing to buy, or null (<see cref="ChooseBuy"/>).</param>
    /// <param name="moreBuys">Listings bought after it — the gear a visit buys for several slots (<see cref="GearNeeds"/>); null for none.</param>
    /// <returns>The commands, in the order they are sent.</returns>
    public static List<MarketCommand> Plan(IReadOnlyList<HeldStack> held, MarketMine mine, MarketPrices prices, ref uint rng, bool listMaterial,
        BrowseRow? buy, int gridStacks = -1, int maxListed = MaxListed, int kitsKept = KitsKept, IReadOnlyList<BrowseRow> moreBuys = null)
    {
        var plan = new List<MarketCommand>();
        if (mine.Reserved > 0)
        {
            plan.Add(MarketCommand.Collect());
        }

        // Stacks still in the grid as the plan goes: each listed or vendored stack leaves it.
        var left = new List<HeldStack>(held);
        var gone = 0;
        var listed = mine.Listed;

        // Its Mk I cores and add-ons taken off the board (2026-11-05): nobody buys one — a crafter makes its own Mk I core — and 35 000 of them held every
        // bot's listing slots for their 24 h, so the looted cores above Mk I that make a Mk II+ item waited in the grids, unlisted (610 in, 51 listed).
        var cancelled = 0;
        foreach (var l in mine.ListedRows)
        {
            if (l.Mk <= 1 && l.Category is MarketCategory.Core or MarketCategory.Addon && cancelled < UnclogPerVisit)
            {
                plan.Add(MarketCommand.Cancel(l.Id));
                cancelled++;
                listed--;
            }
        }


        // Repair kits past the ones it keeps are listed, a few at a time, at their value's draw under the board (Loïc, 2026-11-04); a kit never goes to the
        // vendor — it is what a bot repairs with, and the vendor pays a fifth of it.
        for (var i = 0; i < left.Count; i++)
        {
            var s = left[i];
            if (!CraftPlan.IsKit(s.Template))
            {
                continue;
            }

            var spare = s.Count - kitsKept;
            var value = prices.ValueOf(s.Item, s.Mk);
            if (spare > 0 && listed < maxListed && value > 0d)
            {
                var lot = Math.Min(spare, KitLot);
                plan.Add(MarketCommand.List(s.Template, s.Mk, lot, ListPrice(value * KitMarkup, s.Item, s.Mk, prices, null, ref rng)));
                listed++;
            }

            left.RemoveAt(i--);
        }

        for (var i = 0; i < left.Count && listed < maxListed; i++)
        {
            var s = left[i];
            var value = prices.ValueOf(s.Item, s.Mk);
            // Above Mk I only: a Mk I core or add-on goes to the vendor — it never sells (2026-11-05, above).
            if (s.Category is MarketCategory.Core or MarketCategory.Addon && s.Mk >= 2 && s.Template != 0 && value > 0d && s.Count > 0)
            {
                // One lot of the stack, as a material's: the rest stays for the vendor, and one lot leaves the grid.
                plan.Add(MarketCommand.List(s.Template, s.Mk, Math.Min(s.Count, LotSize), Price(value, ListLow, ListHigh, ref rng)));
                gone++;
                listed++;
                if (s.Count <= LotSize)
                {
                    left.RemoveAt(i--);
                }
                else
                {
                    left[i] = s with { Count = s.Count - LotSize };
                }
            }
        }

        if (listMaterial && listed < maxListed)
        {
            var full = left.FindIndex(s => s.Category == MarketCategory.Material && s.Template != 0 && s.Count >= LotSize && prices.ValueOf(s.Item, s.Mk) > 0d);
            if (full >= 0)
            {
                // One lot of the stack: the rest stays for the vendor.
                var s = left[full];
                plan.Add(MarketCommand.List(s.Template, s.Mk, LotSize, Price(prices.ValueOf(s.Item, s.Mk), MaterialLow, MaterialHigh, ref rng)));
                gone++;
                if (s.Count == LotSize)
                {
                    left.RemoveAt(full);
                }
                else
                {
                    left[full] = s with { Count = s.Count - LotSize };
                }
            }
        }

        // The vendor: whatever has a value, the cheapest unit first — the materials, then cores and add-ons past the five listed — and the smaller stack of
        // two, until the floor. A core kept because five are listed is a slot lost to the next quest's drops.
        // Not a core or an add-on above Mk I (Loïc, 2026-11-05): a crafted item takes its core's Mk and a crafted core is Mk I, so a looted one is the only
        // way to a Mk II+ item — it waits in the grid for a listing slot rather than going to the vendor at a fifth of its value.
        var materials = left.FindAll(s => s.Template != 0 && s.Count > 0 && prices.ValueOf(s.Item, s.Mk) > 0d
            && !(s.Category is MarketCategory.Core or MarketCategory.Addon && s.Mk >= 2));
        materials.Sort((a, b) =>
        {
            var byValue = prices.ValueOf(a.Item, a.Mk).CompareTo(prices.ValueOf(b.Item, b.Mk));
            return byValue != 0 ? byValue : a.Mk != b.Mk ? a.Mk.CompareTo(b.Mk) : a.Count.CompareTo(b.Count);
        });
        // The floor counts the whole grid when it is known — a crafter's kept inputs, its parts and its items too — not only what this plan may sell: a
        // grid full of what it keeps sends the bot back here for good, freeing nothing (review, 2026-11-02).
        var stacks = gridStacks - gone;
        if (gridStacks < 0)
        {
            stacks = 0;
            foreach (var s in left)
            {
                stacks += Lots(s.Count, false);
            }
        }

        var sales = 0;
        foreach (var s in materials)
        {
            // A lot at a time, the stack's last lot whatever it holds: as many sales as when the stack was that many stacks — and no more than a visit
            // held when a slot was a lot, so a stack of 100 000 is sold over visits, not in one plan of a thousand commands.
            var units = s.Count;
            while (stacks > KeepStacks && units > 0 && sales < MaxVendorSalesPerVisit)
            {
                var take = Math.Min(units, LotSize);
                if (VendorPayout(take, prices.ValueOf(s.Item, s.Mk)) == 0)
                {
                    break;   // the vendor refuses a sale that pays nothing (MarketWorthless): the stack stays
                }

                plan.Add(MarketCommand.Vendor(s.Template, s.Mk, take));
                units -= take;
                stacks--;
                sales++;
            }

            if (stacks <= KeepStacks || sales >= MaxVendorSalesPerVisit)
            {
                break;
            }
        }

        // The buys, then one collect: what it bought is reserved for it here, and taken now rather than at its next visit — a bot buying the weapon it
        // fights with went back to the fight still on its fist (review, 2026-11-04).
        var bought = false;
        if (buy is { } row)
        {
            plan.Add(MarketCommand.Buy(row.Id, row.Unit));
            bought = true;
        }

        foreach (var more in moreBuys ?? [])
        {
            plan.Add(MarketCommand.Buy(more.Id, more.Unit));
            bought = true;
        }

        if (bought)
        {
            plan.Add(MarketCommand.Collect());
        }

        return plan;
    }

    /// <summary>The slots a visit's goods take beyond what it holds: one more than it brings in, for the drop on the walk out.</summary>
    public const int RoomMargin = 1;

    /// <summary>
    /// What makes room for a visit's goods (Loïc, 2026-11-05: "make sure they have enough room in inventory to buy things, otherwise they sell or delete
    /// crap"): with <paramref name="incoming"/> entries to come in and the grid at <paramref name="used"/> of <paramref name="capacity"/> once
    /// <paramref name="plan"/> has gone out, the vendor takes whole stacks of its goods — the cheapest first — then the items with stats it will not use;
    /// what the vendor pays nothing for is thrown away. Never what it wears or puts on next, its spare, its parts, its kits, the stims it keeps, nor what the
    /// plan already moves.
    /// </summary>
    /// <param name="plan">The visit's plan so far: what it lists, vendors and throws away frees room already.</param>
    /// <param name="held">The stacks it may part with (a crafter's kept inputs are not among them).</param>
    /// <param name="gear">Its items with stats.</param>
    /// <param name="short">Slots still short once all of it is gone: that many of the goods do not come in.</param>
    /// <returns>The commands to send before the goods: they go out first.</returns>
    public static List<MarketCommand> MakeRoom(IReadOnlyList<MarketCommand> plan, IReadOnlyList<HeldStack> held, IReadOnlyList<GearItem> gear,
        MarketPrices prices, int capacity, int used, int incoming, int band, ushort weapon, out int @short, int stimsKept = GearPlan.StimsKept)
    {
        var room = new List<MarketCommand>();
        var gone = new HashSet<ulong>();
        var units = new Dictionary<(ushort, int), long>();
        foreach (var c in plan)
        {
            if (c.Step is not (MarketStep.List or MarketStep.Vendor or MarketStep.Discard))
            {
                continue;
            }

            if (c.Item != 0)
            {
                gone.Add(c.Item);
            }
            else
            {
                units[(c.Template, c.Mk)] = units.GetValueOrDefault((c.Template, c.Mk)) + c.Quantity;
            }
        }

        // What the plan empties: an item, or a stack all of whose units it sells or lists.
        var free = capacity - used + gone.Count;
        var left = new List<HeldStack>();
        foreach (var s in held)
        {
            var take = units.GetValueOrDefault((s.Template, s.Mk));
            units[(s.Template, s.Mk)] = Math.Max(0, take - s.Count);
            if (take >= s.Count)
            {
                free++;
            }
            else if (s.Template != 0 && s.Category is MarketCategory.Material or MarketCategory.Core or MarketCategory.Addon)
            {
                // The goods only: a kit, an installation's module, anything else held as units is no room to make.
                left.Add(s with { Count = s.Count - (int)take });
            }
        }

        var need = incoming + RoomMargin - free;
        if (need <= 0)
        {
            @short = 0;
            return room;
        }

        // The goods: a whole stack each, the cheapest first — what is worth least to it is what it parts with.
        left.Sort((a, b) => (prices.ValueOf(a.Item, a.Mk) * a.Count).CompareTo(prices.ValueOf(b.Item, b.Mk) * b.Count));
        foreach (var s in left)
        {
            if (need <= 0)
            {
                break;
            }

            if (VendorPayout(s.Count, prices.ValueOf(s.Item, s.Mk)) > 0)
            {
                room.Add(MarketCommand.Vendor(s.Template, s.Mk, s.Count));
            }
            else if (s.Id != 0)
            {
                room.Add(new MarketCommand(MarketStep.Discard, s.Template, s.Mk, s.Count, 0, 0, s.Id));
            }
            else
            {
                continue;
            }

            need--;
        }

        // Then the gear it will not use — not worn, not what it puts on next, not its spare — and the stims past those it keeps: never a part, a module, an
        // extractor's kit, which are worth nothing to the vendor and everything to their owner.
        var wear = NextToWear(gear, band, weapon, held);
        var spare = weapon != 0 ? GearPlan.Spare(gear, weapon, held, band) : 0;
        var stims = GearPlan.StimsToSell(gear, stimsKept);
        var items = new List<GearItem>();
        foreach (var g in gear)
        {
            var kept = g.Template == GearPlan.Stim
                ? !stims.Contains(g.Id)
                : g.Wears is < 101 or > 107 || (!g.Crate && !g.WornDown && Wanted(gear, g, band, weapon));
            if (!g.Worn && g.Id != wear && g.Id != spare && !gone.Contains(g.Id) && !kept)
            {
                items.Add(g);
            }
        }

        items.Sort((a, b) => (prices.ValueOf(a.Item, a.Mk) * Share(a) * a.Count).CompareTo(prices.ValueOf(b.Item, b.Mk) * Share(b) * b.Count));
        foreach (var g in items)
        {
            if (need <= 0)
            {
                break;
            }

            var value = prices.ValueOf(g.Item, g.Mk);
            // A crate goes whole, its count named (the server refuses less, DiscardBad).
            room.Add(!g.Broken && value > 0d && VendorPayout(g.Count, value * Share(g)) > 0
                ? new MarketCommand(MarketStep.Vendor, g.Template, g.Mk, g.Count, 0, 0, g.Id)
                : new MarketCommand(MarketStep.Discard, g.Template, g.Mk, g.Count, 0, 0, g.Id));
            need--;
        }

        @short = Math.Max(0, need);
        return room;
    }

    /// <summary>
    /// The vendor's standard issue for every slot nothing covers (<see cref="ChooseStandard"/>, one after the other, the weapon first), keeping
    /// <see cref="StandardReserve"/> after each — but not for a slot in <paramref name="covered"/>, which the board's piece fills this visit.
    /// </summary>
    public static List<MarketCommand> StandardIssues(IReadOnlyList<StandardOffer> offers, IReadOnlyList<GearItem> gear, long credits,
        IReadOnlyList<HeldStack> held, ushort weapon, IReadOnlyCollection<int> covered, int band = 5)
    {
        var issues = new List<MarketCommand>();
        if (offers == null)
        {
            return issues;
        }

        // What this visit puts in a slot covers it, as a sound piece worn there would.
        var wearing = new List<GearItem>(gear);
        foreach (var slot in covered)
        {
            wearing.Add(new GearItem(0, weapon, slot + 1000, slot, Mk: 1));
        }

        while (issues.Count < 7 && ChooseStandard(offers, wearing, credits, held, weapon, band) is { } issue)
        {
            issues.Add(issue);
            credits -= issue.UnitPrice;
            foreach (var o in offers)
            {
                if (o.Template == issue.Template)
                {
                    wearing.Add(new GearItem(0, o.Template, o.Wears + 1000, o.Wears, Mk: 1));
                }
            }
        }

        return issues;
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

    internal static bool Contains(IReadOnlyCollection<ulong> set, ulong id)
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
