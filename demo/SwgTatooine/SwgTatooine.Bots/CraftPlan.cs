using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SwgTatooine.Bots;

/// <summary>What a crafter makes (crafting-build.md § 6, profiles): weapons, armour or potions, a third of the crafters each.</summary>
public enum CraftLine : byte
{
    Weapon,
    Armor,
    Potion,
}

/// <summary>One input slot of a schematic as <c>GET /swg/schematics</c> lists it.</summary>
/// <param name="Kind"><c>quality</c>, <c>bulk</c>, <c>part</c> or <c>core</c>.</param>
/// <param name="Template">What fills it; 0 for an armour piece's segments, padded or composite.</param>
/// <param name="Item">Its asset, which is how the bazaar's browse names it.</param>
/// <param name="Count">How many.</param>
public readonly record struct BookSlot(string Kind, ushort Template, string Item, int Count)
{
    public bool IsPart => Kind == "part";

    public bool IsBulk => Kind == "bulk";
}

/// <summary>A schematic as <c>GET /swg/schematics</c> lists it: what a crafter needs to plan with — its slots, its points, the lines worth a point.</summary>
/// <param name="Rolled">The lines a point moves: those whose range is not one value (a knuckler's reach is 1 m, whatever the roll).</param>
public sealed record BookSchematic(ushort Id, string Kind, int Points, BookSlot[] Slots, int[] Rolled);

/// <summary>The schematics, by id: read once per fleet.</summary>
public sealed class CraftBook
{
    private readonly Dictionary<ushort, BookSchematic> _byId;

    public CraftBook(IEnumerable<BookSchematic> schematics)
    {
        _byId = [];
        foreach (var s in schematics)
        {
            _byId[s.Id] = s;
        }
    }

    public int Count => _byId.Count;

    public BookSchematic Of(ushort id) => _byId.TryGetValue(id, out var s) ? s : null;

    /// <summary>Reads a <c>GET /swg/schematics</c> answer.</summary>
    public static CraftBook Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<BookSchematic>();
        foreach (var s in doc.RootElement.GetProperty("schematics").EnumerateArray())
        {
            var slots = new List<BookSlot>();
            foreach (var slot in s.GetProperty("slots").EnumerateArray())
            {
                slots.Add(new BookSlot(
                    slot.GetProperty("kind").GetString(),
                    (ushort)slot.GetProperty("template").GetInt32(),
                    slot.GetProperty("item").GetString(),
                    slot.GetProperty("count").GetInt32()));
            }

            var rolled = new List<int>();
            var l = 0;
            foreach (var line in s.GetProperty("lines").EnumerateArray())
            {
                if (line.GetProperty("min").GetDouble() != line.GetProperty("max").GetDouble())
                {
                    rolled.Add(l);
                }

                l++;
            }

            list.Add(new BookSchematic(
                (ushort)s.GetProperty("id").GetInt32(),
                s.GetProperty("kind").GetString(),
                s.GetProperty("points").GetInt32(),
                [.. slots],
                [.. rolled]));
        }

        return new CraftBook(list);
    }
}

/// <summary>A part held: an instance with a grade, in the grid.</summary>
public readonly record struct HeldPart(ulong Id, ushort Template, int Grade);

/// <summary>
/// A craft a bot can start now (crafting-build.md C2): the schematic, the Mk of each material or core slot in order, the parts by id, a line per point and
/// the add-on's Mk (0 for none) — what <c>CraftStart</c>, <c>CraftExperiment</c>, <c>CraftEnhance</c> and <c>CraftFinish</c> carry.
/// </summary>
public sealed record CraftJob(ushort Schematic, byte[] Mks, ulong[] Parts, byte[] Points, byte AddonMk);

/// <summary>
/// What a crafter crafts next (crafting-build.md § 6), as a pure function of the book and what it holds: the first of its line's items it can make, or the
/// part or core one of them lacks and it can make — parts first, the way a player works up to an item. Every input is one it holds; the server checks it all
/// again and refuses what does not hold.
/// </summary>
/// <remarks>
/// <para><b>Each material slot takes the highest Mk that covers it, a bulk slot the lowest</b> — the quality where it counts, the cheap units where it does not
/// — and the parts the best-graded first. The panel picks the same way.</para>
/// <para><b>Points spread over the lines a point moves</b>, in turn, from the first: an item's damage or protection first.</para>
/// </remarks>
public static class CraftPlan
{
    // Templates (ItemCatalog's, append-only — see MarketCatalog's remark).
    public const ushort CoreRanged = 12;
    public const ushort CoreMelee = 13;
    public const ushort CoreArmor = 14;
    public const ushort AddonWeapon = 16;
    public const ushort AddonArmor = 17;
    public const ushort AddonAdditive = 19;
    /// <summary>The parts' templates, 20-35 (crafting-catalog.md § 13.4).</summary>
    public const ushort FirstPart = 20;

    public const ushort LastPart = 35;
    public const ushort PaddedSegment = 29;
    public const ushort CompositeSegment = 30;
    public const ushort Knuckler = 67;
    public const ushort Sword = 68;
    public const ushort Blaster = 69;
    public const ushort KitWeapon = MarketPlan.KitWeapon;
    public const ushort KitArmor = MarketPlan.KitArmor;

    /// <summary>The bazaar's most a crafter pays for an input, × its value (crafting-catalog.md § 15.2).</summary>
    public const double InputOverValue = 1.5;

    /// <summary>Stacks of one kept template a crafter keeps: about its next two crafts' worth; the rest are traded like anyone's (§ 6, Surplus).</summary>
    public const int KeptStacksPerTemplate = 2;

    /// <summary>
    /// The lines crafters work: weapons, armour and potions, in thirds (crafting-build.md § 6). The potion line makes stims only — the one potion a bot drinks
    /// and the market values (Loïc, 2026-11-05); the others wait for a use.
    /// </summary>
    public static readonly CraftLine[] Lines = [CraftLine.Weapon, CraftLine.Armor, CraftLine.Potion];

    /// <summary>
    /// What a line's crafters make, in the order they try them when the board says nothing (<see cref="ByDemand"/>): the line's items, and its repair kit
    /// (Loïc, 2026-11-04: crafters make what the market lacks — no kit had ever been listed).
    /// </summary>
    public static ushort[] Targets(CraftLine line) => line switch
    {
        CraftLine.Weapon => [Blaster, Sword, Knuckler, KitWeapon],
        CraftLine.Armor => [44, 45, 46, 47, 48, 49, KitArmor],
        _ => [GearPlan.Stim],
    };

    /// <summary>The asset a target is browsed and priced by; null for one this table does not name.</summary>
    public static string OutputItem(ushort template) => template switch
    {
        Blaster => "blaster_dl44",
        Sword => "sword_vibroblade",
        Knuckler => "vibroknuckler",
        KitWeapon => "kit_weapon_repair",
        KitArmor => "kit_armor_repair",
        GearPlan.Stim => GearPlan.StimItem,
        44 => "armor_padded_helmet",
        45 => "armor_padded_torso",
        46 => "armor_padded_arms",
        47 => "armor_padded_legs",
        48 => "armor_padded_gloves",
        49 => "armor_padded_boots",
        _ => null,
    };

    /// <summary>The items' worth of vendor inputs a crafter buys at once (<see cref="FromVendorAll"/>), a lot of 99 a material at most.</summary>
    public const int InputBatch = 3;

    /// <summary>A crafter goes to sell once it holds this many goods (<see cref="ForSale"/>).</summary>
    public const int SellAt = 3;

    /// <summary>
    /// A crafter's goods to sell: the items with stats in its grid it will neither wear nor keep as its spare, sound, not parts — what it made — and its
    /// kits past those it keeps (<see cref="MarketPlan.CrafterKitsKept"/>), a listing's lot each.
    /// </summary>
    public static int ForSale(IReadOnlyList<GearItem> gear, IReadOnlyList<HeldStack> held, ulong spare, ulong wear)
    {
        var n = 0;
        foreach (var g in gear)
        {
            n += !g.Worn && g.Wears != 0 && !g.WornDown && g.Id != spare && g.Id != wear && g.Template is not (>= FirstPart and <= LastPart) ? 1 : 0;
        }

        foreach (var h in held)
        {
            n += IsKit(h.Template) && h.Count > MarketPlan.CrafterKitsKept
                ? (h.Count - MarketPlan.CrafterKitsKept + MarketPlan.KitLot - 1) / MarketPlan.KitLot
                : 0;
        }

        // The stims it made, past the one it keeps: Surplus's own rule, a listing each.
        return n + GearPlan.StimsToSell(gear, GearPlan.CrafterStimsKept).Count;
    }

    /// <summary>Whether a template is a repair kit (weapon, armour, vehicle, structure: 62-65).</summary>
    public static bool IsKit(ushort template) => template is >= 62 and <= 65;

    /// <summary>
    /// A line's targets, the most wanted first (Loïc, 2026-11-04: crafters identify what needs to be crafted and sold): by the item's listings against its
    /// recent sales planet-wide — what sells as fast as it is listed first — and the line's own order between equals. With the book, each Mk it can make
    /// is weighed on its own: Mk I, and each Mk II-V whose core the board lists (<see cref="CoreMks"/>); without it, or for a target without a core, every
    /// Mk counted together.
    /// </summary>
    public static ushort[] ByDemand(ushort[] targets, MarketPrices prices, CraftBook book = null)
    {
        var order = (ushort[])targets.Clone();
        var glutted = new bool[order.Length];
        var demand = new double[order.Length];
        for (var i = 0; i < order.Length; i++)
        {
            var item = OutputItem(order[i]);
            var listed = 0;
            var sold = 0;
            for (var mk = 0; mk <= 5; mk++)
            {
                listed += prices.ListedOf(item, mk);
                sold += prices.SalesOf(item, mk);
            }

            // Listed against sold: what sells as fast as it is listed first (the night of 2026-11-04: weapon kits, none listed, went first and sold 47;
            // blasters, two listed at a time, sold 146).
            demand[i] = (listed + 1d) / (sold + 1d);
            glutted[i] = listed >= SaturatedListings;

            // What none buys at any Mk is glutted whole, its few Mk II listings notwithstanding (the night of 2026-11-06: weighed by Mk, swords and
            // knucklers came back, 72 and 83 Mk I listed and none sold at any Mk).
            if (book == null || (glutted[i] && sold == 0) || CoreOf(book, order[i]) is not { } core)
            {
                continue;
            }

            // Its Mks apart: the night of 2026-11-06, 63-76 Mk I helmets, legs, gloves and boots listed — and Mk II-IV of each none, sold out — cut the
            // pieces whole, and the armour line made arms and kits alone. An item is glutted when every Mk it can make is; it is as wanted as its
            // scarcest Mk that is not.
            glutted[i] = true;
            demand[i] = double.MaxValue;
            var scarcest = double.MaxValue;
            for (var mk = 1; mk <= 5; mk++)
            {
                if (mk >= 2 && prices.ListedOf(core.Item, mk) == 0)
                {
                    continue;
                }

                var atMk = prices.ListedOf(item, mk);
                var ratio = (atMk + 1d) / (prices.SalesOf(item, mk) + 1d);
                scarcest = Math.Min(scarcest, ratio);
                if (atMk < SaturatedListings)
                {
                    glutted[i] = false;
                    demand[i] = Math.Min(demand[i], ratio);
                }
            }

            if (glutted[i])
            {
                demand[i] = scarcest;
            }
        }

        var index = new int[order.Length];
        for (var i = 0; i < index.Length; i++)
        {
            index[i] = i;
        }

        Array.Sort(index, (a, b) => demand[a] != demand[b] ? demand[a].CompareTo(demand[b]) : a.CompareTo(b));

        // What the board is glutted with is not made at all, the scarcest kept when all is: the night of 2026-11-04 ended with 83 knucklers and 59 swords
        // listed and none sold (bots fight with blasters), crafted when a blaster's inputs were short. Each glutted target skipped, not the rest after it.
        var kept = 0;
        foreach (var i in index)
        {
            kept += glutted[i] ? 0 : 1;
        }

        var sorted = new ushort[Math.Max(1, kept)];
        var n = 0;
        foreach (var i in index)
        {
            if (!glutted[i])
            {
                sorted[n++] = order[i];
            }
        }

        if (kept == 0)
        {
            sorted[0] = order[index[0]];
        }

        return sorted;
    }

    /// <summary>The listings of an item, planet-wide, past which a crafter stops making it (<see cref="ByDemand"/>).</summary>
    public const int SaturatedListings = 40;

    /// <summary>
    /// The craft to start now, or null: the first target, from <paramref name="first"/> round, that it can make or work toward — of those in
    /// <paramref name="lootedOnly"/> (<see cref="MkIGlutted"/>), only one it holds a looted core for.
    /// </summary>
    public static CraftJob Next(CraftBook book, ushort[] targets, int first, IReadOnlyList<HeldStack> held, IReadOnlyList<HeldPart> parts,
        HashSet<ushort> lootedOnly = null)
    {
        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[(first + i) % targets.Length];
            if (lootedOnly != null && lootedOnly.Contains(target) && !HoldsLootedCore(book, target, held))
            {
                continue;
            }

            if (Step(book, target, held, parts, 0) is { } job)
            {
                return job;
            }
        }

        return null;
    }

    /// <summary>
    /// The targets the board is glutted with at Mk I (<see cref="SaturatedListings"/>), wanted only above it (<see cref="ByDemand"/>): crafted only with a
    /// looted core in hand. The night of 2026-11-06, crafters short of a Mk II core crafted a Mk I one and made the Mk I piece: 2 296 Mk I weapons and
    /// 1 302 Mk I armour pieces listed within the hour. Empty without the book.
    /// </summary>
    public static HashSet<ushort> MkIGlutted(ushort[] targets, MarketPrices prices, CraftBook book)
    {
        var glutted = new HashSet<ushort>();
        foreach (var t in targets)
        {
            if (book != null && CoreOf(book, t) != null && prices.ListedOf(OutputItem(t), 1) >= SaturatedListings)
            {
                glutted.Add(t);
            }
        }

        return glutted;
    }

    /// <summary>
    /// A crafter's refines up (crafting-catalog.md § 11.1; Loïc, 2026-11-06: what it cannot craft at Mk III it refines Mk II into — that is the
    /// feature's point), whatever its band: the core of <paramref name="target"/> into the highest Mk III-V the board wants the item at (not glutted,
    /// <see cref="SaturatedListings"/>) and does not list the core at, from N of the Mk below it holds — never from Mk I, a crafted core — while it holds
    /// none at that Mk or above; and every add-on of the target's kind held N times at Mk II-IV into the next, the craft taking its best.
    /// </summary>
    public static List<(ushort Template, int Mk, int Count)> RefineUp(CraftBook book, ushort target, IReadOnlyList<HeldStack> held, MarketPrices prices)
    {
        var up = new List<(ushort, int, int)>();
        if (book == null)
        {
            return up;
        }

        if (CoreOf(book, target) is { } core && UpTo(core, OutputItem(target), held, prices) is { } mk
            && UnitsAt(held, core.Template, mk - 1) >= RefinePlan.RatioOf(mk - 1) * core.Count)
        {
            up.Add((core.Template, mk - 1, core.Count));
        }

        var addon = AddonOf(book, target);
        for (var k = 2; k <= 4 && addon != 0; k++)
        {
            var make = Math.Min(UnitsAt(held, addon, k) / RefinePlan.RatioOf(k), RefinePlan.MaxPerAction);
            if (make > 0)
            {
                up.Add((addon, k, make));
            }
        }

        return up;
    }

    /// <summary>
    /// The cores a crafter buys to refine up (<see cref="RefineUp"/>): of the Mk below the one it would refine into, what it lacks of N, when the board
    /// lists that many; null when it would not refine, or holds them already.
    /// </summary>
    public static (BookSlot Core, int Mk, int Count)? RefineBuy(CraftBook book, ushort target, IReadOnlyList<HeldStack> held, MarketPrices prices)
    {
        if (book == null || CoreOf(book, target) is not { } core || UpTo(core, OutputItem(target), held, prices) is not { } mk)
        {
            return null;
        }

        var lack = (RefinePlan.RatioOf(mk - 1) * core.Count) - UnitsAt(held, core.Template, mk - 1);
        return lack > 0 && prices.ListedOf(core.Item, mk - 1) >= lack ? (core, mk - 1, lack) : null;
    }

    /// <summary>
    /// Of a list of cores' listings, the cheapest that make <paramref name="count"/> together, each at most <see cref="InputOverValue"/> × its value, not its
    /// own, the whole within <see cref="MarketPlan.BuyWalletShare"/> of the wallet; empty when they do not.
    /// </summary>
    public static List<BrowseRow> ChooseMany(IReadOnlyList<BrowseRow> rows, MarketPrices prices, string item, int count, long wallet,
        IReadOnlyCollection<ulong> own)
    {
        var fit = new List<BrowseRow>();
        foreach (var r in rows)
        {
            var value = prices.ValueOf(r.Item, r.Mk);
            if (r.Item == item && value > 0d && r.Unit <= InputOverValue * value && !Owns(own, r.Id))
            {
                fit.Add(r);
            }
        }

        fit.Sort((a, b) => a.Unit.CompareTo(b.Unit));
        var chosen = new List<BrowseRow>();
        var units = 0;
        var total = 0L;
        foreach (var r in fit)
        {
            if (units >= count)
            {
                break;
            }

            chosen.Add(r);
            units += r.Quantity;
            total += r.Total;
        }

        return units >= count && total <= MarketPlan.BuyWalletShare * wallet ? chosen : [];
    }

    // The Mk a crafter would refine a target's core into: the highest III-V the item is wanted at and the core not listed at, while it holds none at it or
    // above; null for none.
    private static int? UpTo(BookSlot core, string item, IReadOnlyList<HeldStack> held, MarketPrices prices)
    {
        for (var mk = 5; mk >= 3; mk--)
        {
            if (Units(held, core.Template, mk, 5, core.Count) > 0)
            {
                return null;
            }

            if (prices.ListedOf(item, mk) < SaturatedListings && prices.ListedOf(core.Item, mk) == 0
                && prices.ListedOf(core.Item, mk - 1) + UnitsAt(held, core.Template, mk - 1) > 0)
            {
                return mk;
            }
        }

        return null;
    }

    /// <summary>Its stacks less what <paramref name="refines"/> take (N of a Mk for each one made): what its craft plans from while they wait.</summary>
    public static List<HeldStack> Without(IReadOnlyList<HeldStack> held, List<(ushort Template, int Mk, int Count)> refines)
    {
        var take = new Dictionary<(ushort, int), int>();
        foreach (var (template, mk, count) in refines)
        {
            take[(template, mk)] = (take.TryGetValue((template, mk), out var n) ? n : 0) + (count * RefinePlan.RatioOf(mk));
        }

        var left = new List<HeldStack>(held.Count);
        foreach (var h in held)
        {
            if (take.TryGetValue((h.Template, h.Mk), out var t) && t > 0)
            {
                var off = Math.Min(t, h.Count);
                take[(h.Template, h.Mk)] = t - off;
                if (h.Count > off)
                {
                    left.Add(h with { Count = h.Count - off });
                }

                continue;
            }

            left.Add(h);
        }

        return left;
    }

    private static int UnitsAt(IReadOnlyList<HeldStack> held, ushort template, int mk)
    {
        var n = 0;
        foreach (var h in held)
        {
            n += h.Template == template && h.Mk == mk ? h.Count : 0;
        }

        return n;
    }

    private static ushort AddonOf(CraftBook book, ushort target) => book.Of(target)?.Kind switch
    {
        "weapon" => AddonWeapon,
        "armor" => AddonArmor,
        "potion" => AddonAdditive,
        _ => 0,
    };

    /// <summary>Whether it holds a core above Mk I for a target, enough for one craft.</summary>
    public static bool HoldsLootedCore(CraftBook book, ushort target, IReadOnlyList<HeldStack> held)
        => CoreOf(book, target) is { } core && Units(held, core.Template, 2, 5, core.Count) > 0;

    /// <summary>
    /// The templates a crafter keeps from the vendor and off the board: every material, core and add-on its targets' recipes take, down to their parts'.
    /// </summary>
    public static HashSet<ushort> Keeps(CraftBook book, ushort[] targets)
    {
        var keep = new HashSet<ushort>();
        foreach (var t in targets)
        {
            Collect(book, book.Of(t), keep, 0);
        }

        return keep;
    }

    /// <summary>
    /// The material its first target lacks first, walking down its tree the way <see cref="Next"/> would craft it: the input to buy, or null when nothing
    /// is lacking or the tree has no material leaf missing.
    /// </summary>
    public static BookSlot? Wants(CraftBook book, ushort target, IReadOnlyList<HeldStack> held, IReadOnlyList<HeldPart> parts)
        => Lacks(book, book.Of(target), held, parts, 0);

    /// <summary>
    /// The stacks a crafter trades (§ 6, Surplus): every stack but, of each template its recipes take (<see cref="Keeps"/>), the
    /// <see cref="KeptStacksPerTemplate"/> largest — the highest Mk of equal ones. Bounded, so the kept stacks never fill the grid.
    /// </summary>
    /// <remarks>The stacks its planned craft takes (<paramref name="uses"/>) are kept first, whatever their size: a small high-Mk stack a quality slot
    /// fills from is what the craft is for.</remarks>
    public static List<HeldStack> Tradeable(IReadOnlyList<HeldStack> held, HashSet<ushort> keeps, HashSet<(ushort, int)> uses = null)
    {
        var kept = new List<HeldStack>();
        var trade = new List<HeldStack>();
        foreach (var s in held)
        {
            if (uses != null && uses.Contains((s.Template, s.Mk)))
            {
                continue;   // the planned craft's: kept, and counted against nothing
            }

            if (keeps.Contains(s.Template) && s.Category == MarketCategory.Core && s.Mk >= 2)
            {
                continue;   // a looted core its recipes take: what crafts above Mk I (CoreMks), never traded however small its stack
            }

            (keeps.Contains(s.Template) ? kept : trade).Add(s);
        }

        kept.Sort((a, b) => a.Template != b.Template ? a.Template.CompareTo(b.Template)
            : a.Count != b.Count ? b.Count.CompareTo(a.Count)
            : b.Mk.CompareTo(a.Mk));
        for (var i = 0; i < kept.Count; i++)
        {
            var nth = 0;
            for (var j = i - 1; j >= 0 && kept[j].Template == kept[i].Template; j--)
            {
                nth++;
            }

            if (nth >= KeptStacksPerTemplate)
            {
                trade.Add(kept[i]);
            }
        }

        return trade;
    }

    /// <summary>
    /// The listing to buy for an input it lacks (§ 6, Inputs): of a browse page of that item, the cheapest unit at ≤ <see cref="InputOverValue"/> × its
    /// value, enough to fill the slot, at most <see cref="MarketPlan.BuyWalletShare"/> of the wallet, not its own; null when none is.
    /// </summary>
    public static BrowseRow? ChooseInput(IReadOnlyList<BrowseRow> rows, MarketPrices prices, BookSlot want, long wallet, IReadOnlyCollection<ulong> own)
    {
        BrowseRow? best = null;
        foreach (var r in rows)
        {
            var value = prices.ValueOf(r.Item, r.Mk);
            if (r.Item != want.Item || r.Quantity < want.Count || value <= 0d || r.Unit > InputOverValue * value
                || r.Total > MarketPlan.BuyWalletShare * wallet || Owns(own, r.Id))
            {
                continue;
            }

            if (best is not { } b || r.Unit < b.Unit)
            {
                best = r;
            }
        }

        return best;
    }

    /// <summary>The (template, Mk) of every material and core a job takes from the grid: what its craft is waiting for.</summary>
    public static HashSet<(ushort, int)> Uses(CraftBook book, CraftJob job)
    {
        var uses = new HashSet<(ushort, int)>();
        if (job == null || book.Of(job.Schematic) is not { } s)
        {
            return uses;
        }

        var k = 0;
        foreach (var slot in s.Slots)
        {
            if (!slot.IsPart && k < job.Mks.Length)
            {
                uses.Add((slot.Template, job.Mks[k++]));
            }
        }

        if (job.AddonMk != 0)
        {
            uses.Add((s.Kind switch { "weapon" => AddonWeapon, "armor" => AddonArmor, _ => AddonAdditive }, job.AddonMk));
        }

        return uses;
    }

    /// <summary>
    /// The vendor's Mk I units of a material it lacks (Loïc, 2026-11-02): the slot's count, priced as the vendor prices it — the total, rounded down —
    /// within <paramref name="budget"/>; null when the vendor does not sell it or it costs more.
    /// </summary>
    public static MarketCommand? FromVendor(IReadOnlyList<StandardOffer> offers, BookSlot want, long budget)
    {
        if (offers == null)
        {
            return null;
        }

        foreach (var o in offers)
        {
            if (o.Template != want.Template || o.PriceMilli <= 0)
            {
                continue;
            }

            var quantity = Math.Clamp(want.Count, 1, MarketPlan.LotSize);
            var price = o.PriceMilli * quantity / 1000;
            return price > 0 && price <= budget ? MarketCommand.Standard(o.Template, price, quantity) : null;
        }

        return null;
    }

    /// <summary>
    /// What its target's whole tree still needs of each material (Loïc, 2026-11-02; review, 2026-11-02): the target's slots, the parts it lacks — the
    /// held ones taken first — and the core when none is held, each from its own schematic; less what it holds of each, every Mk counted. Positive
    /// deficits only, by template.
    /// </summary>
    public static SortedDictionary<ushort, int> Deficits(CraftBook book, ushort target, IReadOnlyList<HeldStack> held, IReadOnlyList<HeldPart> parts)
    {
        var need = new Dictionary<ushort, int>();
        var spare = new List<HeldPart>(parts);
        Needs(book, book.Of(target), held, spare, need, 0);
        var deficits = new SortedDictionary<ushort, int>();
        // A slot is filled from one Mk (Units, Assemble): what is held counts by the Mk holding the most, not summed across Mks (review, 2026-11-03).
        Span<int> byMk = stackalloc int[6];
        foreach (var (template, count) in need)
        {
            byMk.Clear();
            foreach (var h in held)
            {
                if (h.Template == template && h.Mk is >= 0 and <= 5)
                {
                    byMk[h.Mk] += h.Count;
                }
            }

            var have = 0;
            foreach (var n in byMk)
            {
                have = Math.Max(have, n);
            }

            if (count > have)
            {
                deficits[template] = count - have;
            }
        }

        return deficits;
    }

    private static void Needs(CraftBook book, BookSchematic s, IReadOnlyList<HeldStack> held, List<HeldPart> spare, Dictionary<ushort, int> need, int depth)
    {
        if (s == null || depth > 3)
        {
            return;
        }

        foreach (var slot in s.Slots)
        {
            if (slot.IsPart)
            {
                for (var n = 0; n < slot.Count; n++)
                {
                    var at = spare.FindIndex(p => slot.Template == 0 ? p.Template is PaddedSegment or CompositeSegment : p.Template == slot.Template);
                    if (at >= 0)
                    {
                        spare.RemoveAt(at);
                    }
                    else
                    {
                        Needs(book, book.Of(slot.Template != 0 ? slot.Template : PaddedSegment), held, spare, need, depth + 1);
                    }
                }
            }
            else if (slot.Kind == "core" && Units(held, slot.Template, 1, 5, slot.Count) == 0)
            {
                Needs(book, book.Of(slot.Template), held, spare, need, depth + 1);
            }
            else if (slot.Kind != "core")
            {
                need[slot.Template] = (need.TryGetValue(slot.Template, out var c) ? c : 0) + slot.Count;
            }
        }
    }

    /// <summary>
    /// The vendor's Mk I units of every material its target's tree lacks and the vendor sells (Loïc, 2026-11-02): one buy per material, its deficit (at
    /// most a stack), within <paramref name="wallet"/> × <see cref="MarketPlan.BuyWalletShare"/> in all; what the vendor does not sell — Hide, Bone —
    /// is skipped, not a stop.
    /// </summary>
    public static List<MarketCommand> FromVendorAll(CraftBook book, ushort target, IReadOnlyList<HeldStack> held, IReadOnlyList<HeldPart> parts,
        IReadOnlyList<StandardOffer> offers, long wallet)
    {
        var buys = new List<MarketCommand>();
        var budget = (long)(MarketPlan.BuyWalletShare * wallet);

        // The next InputBatch items' worth, not one (the night of 2026-11-04: a crafter bought one tree's inputs, crafted it, and walked back to the vendor
        // — its 1 200 blasters in three hours were its own replacements, none left to sell): what this one lacks, and whole trees for the rest.
        var whole = Deficits(book, target, [], []);
        foreach (var (template, deficit) in Deficits(book, target, held, parts))
        {
            var want = deficit + (whole.TryGetValue(template, out var tree) ? tree * (InputBatch - 1) : 0);
            if (buys.Count < 6 && FromVendor(offers, new BookSlot("bulk", template, null, want), budget) is { } buy)
            {
                buys.Add(buy);
                budget -= buy.UnitPrice;
            }
        }

        return buys;
    }

    /// <summary>The core slot of a target's schematic — an item takes its core's Mk (the server's <c>StartPlan</c>) — or null for one without a core.</summary>
    public static BookSlot? CoreOf(CraftBook book, ushort target)
    {
        foreach (var slot in book.Of(target)?.Slots ?? [])
        {
            if (slot.Kind == "core")
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// The Mks of looted core a crafter looks for on the board, the highest first (Loïc, 2026-11-05: the bots buy the best gear they can wear, and the
    /// crafters made 1 391 Mk I blasters to 6 above it in 39 min): the item takes its core's Mk, a crafted core is Mk I, so a Mk II+ item needs a looted
    /// core. Each Mk II-V the board lists the core at and is not glutted with the item at (<see cref="SaturatedListings"/>); none while it holds a core
    /// above Mk I already, nor for a target without a core.
    /// </summary>
    public static List<int> CoreMks(CraftBook book, ushort target, IReadOnlyList<HeldStack> held, MarketPrices prices)
    {
        var mks = new List<int>();
        if (CoreOf(book, target) is not { } core || Units(held, core.Template, 2, 5, core.Count) > 0)
        {
            return mks;
        }

        var item = OutputItem(target);
        for (var mk = 5; mk >= 2; mk--)
        {
            if (prices.ListedOf(core.Item, mk) > 0 && prices.ListedOf(item, mk) < SaturatedListings)
            {
                mks.Add(mk);
            }
        }

        return mks;
    }

    /// <summary>The parts in an inventory's grid: its items with stats of a part's template, with their grades.</summary>
    public static List<HeldPart> PartsOf(IReadOnlyList<GearItem> gear)
    {
        var parts = new List<HeldPart>();
        foreach (var g in gear)
        {
            if (!g.Worn && !g.Crate && g.Template is >= FirstPart and <= LastPart)
            {
                parts.Add(new HeldPart(g.Id, g.Template, g.Grade));
            }
        }

        return parts;
    }

    private static bool Owns(IReadOnlyCollection<ulong> own, ulong id)
    {
        foreach (var o in own)
        {
            if (o == id)
            {
                return true;
            }
        }

        return false;
    }

    private static CraftJob Step(CraftBook book, ushort id, IReadOnlyList<HeldStack> held, IReadOnlyList<HeldPart> parts, int depth)
    {
        var s = book.Of(id);
        if (s == null || depth > 3)
        {
            return null;
        }

        if (Assemble(s, held, parts) is { } job)
        {
            return job;
        }

        // Not makeable: the part or core it lacks, if that can be made.
        foreach (var slot in s.Slots)
        {
            if (slot.IsPart)
            {
                if (CountParts(parts, slot.Template) >= slot.Count)
                {
                    continue;
                }

                var sub = slot.Template != 0 ? Step(book, slot.Template, held, parts, depth + 1)
                    : Step(book, PaddedSegment, held, parts, depth + 1) ?? Step(book, CompositeSegment, held, parts, depth + 1);
                if (sub != null)
                {
                    return sub;
                }
            }
            else if (slot.Kind == "core" && Units(held, slot.Template, 1, 5, slot.Count) == 0 && Step(book, slot.Template, held, parts, depth + 1) is { } core)
            {
                return core;
            }
        }

        return null;
    }

    private static CraftJob Assemble(BookSchematic s, IReadOnlyList<HeldStack> held, IReadOnlyList<HeldPart> parts)
    {
        // What is left as the slots take theirs: the same (template, Mk) asked twice must be held for both.
        var left = new Dictionary<(ushort, int), int>();
        foreach (var h in held)
        {
            left[(h.Template, h.Mk)] = (left.TryGetValue((h.Template, h.Mk), out var n) ? n : 0) + h.Count;
        }

        var used = new HashSet<ulong>();
        var mks = new List<byte>();
        var ids = new List<ulong>();
        foreach (var slot in s.Slots)
        {
            if (slot.IsPart)
            {
                for (var n = 0; n < slot.Count; n++)
                {
                    var best = -1;
                    for (var p = 0; p < parts.Count; p++)
                    {
                        var fits = slot.Template == 0 ? parts[p].Template is PaddedSegment or CompositeSegment : parts[p].Template == slot.Template;
                        if (fits && !used.Contains(parts[p].Id) && (best < 0 || parts[p].Grade > parts[best].Grade))
                        {
                            best = p;
                        }
                    }

                    if (best < 0)
                    {
                        return null;
                    }

                    used.Add(parts[best].Id);
                    ids.Add(parts[best].Id);
                }

                continue;
            }

            var mk = 0;
            for (var m = 1; m <= 5; m++)
            {
                var k = slot.IsBulk ? m : 6 - m;
                if (left.TryGetValue((slot.Template, k), out var have) && have >= slot.Count)
                {
                    mk = k;
                    break;
                }
            }

            if (mk == 0)
            {
                return null;
            }

            left[(slot.Template, mk)] -= slot.Count;
            mks.Add((byte)mk);
        }

        var points = new byte[s.Rolled.Length == 0 ? 0 : s.Points];
        for (var p = 0; p < points.Length; p++)
        {
            points[p] = (byte)s.Rolled[p % s.Rolled.Length];
        }

        // The add-on, its best Mk, when one of its kind is held past what the slots took.
        var addon = s.Kind switch { "weapon" => AddonWeapon, "armor" => AddonArmor, "potion" => AddonAdditive, _ => (ushort)0 };
        byte addonMk = 0;
        for (var m = 5; m >= 1 && addon != 0 && addonMk == 0; m--)
        {
            addonMk = left.TryGetValue((addon, m), out var have) && have > 0 ? (byte)m : (byte)0;
        }

        return new CraftJob(s.Id, [.. mks], [.. ids], points, addonMk);
    }

    private static BookSlot? Lacks(CraftBook book, BookSchematic s, IReadOnlyList<HeldStack> held, IReadOnlyList<HeldPart> parts, int depth)
    {
        if (s == null || depth > 3)
        {
            return null;
        }

        foreach (var slot in s.Slots)
        {
            if (slot.IsPart)
            {
                if (CountParts(parts, slot.Template) < slot.Count
                    && Lacks(book, book.Of(slot.Template != 0 ? slot.Template : PaddedSegment), held, parts, depth + 1) is { } sub)
                {
                    return sub;
                }
            }
            else if (slot.Kind == "core")
            {
                if (Units(held, slot.Template, 1, 5, slot.Count) == 0 && Lacks(book, book.Of(slot.Template), held, parts, depth + 1) is { } sub)
                {
                    return sub;
                }
            }
            else if (Units(held, slot.Template, 1, 5, slot.Count) == 0)
            {
                return slot;
            }
        }

        return null;
    }

    private static void Collect(CraftBook book, BookSchematic s, HashSet<ushort> keep, int depth)
    {
        if (s == null || depth > 3)
        {
            return;
        }

        keep.Add(s.Kind switch { "weapon" => AddonWeapon, "armor" => AddonArmor, "potion" => AddonAdditive, _ => (ushort)0 });
        foreach (var slot in s.Slots)
        {
            if (slot.IsPart)
            {
                Collect(book, book.Of(slot.Template != 0 ? slot.Template : PaddedSegment), keep, depth + 1);
                if (slot.Template == 0)
                {
                    Collect(book, book.Of(CompositeSegment), keep, depth + 1);
                }
            }
            else
            {
                keep.Add(slot.Template);
                if (slot.Kind == "core")
                {
                    Collect(book, book.Of(slot.Template), keep, depth + 1);
                }
            }
        }

        keep.Remove(0);
    }

    private static int CountParts(IReadOnlyList<HeldPart> parts, ushort template)
    {
        var n = 0;
        foreach (var p in parts)
        {
            n += (template == 0 ? p.Template is PaddedSegment or CompositeSegment : p.Template == template) ? 1 : 0;
        }

        return n;
    }

    /// <summary>The first Mk in [<paramref name="from"/>, <paramref name="to"/>] at which one Mk's units cover <paramref name="count"/>; 0 for none.</summary>
    private static int Units(IReadOnlyList<HeldStack> held, ushort template, int from, int to, int count)
    {
        for (var mk = from; mk <= to; mk++)
        {
            var n = 0;
            foreach (var h in held)
            {
                n += h.Template == template && h.Mk == mk ? h.Count : 0;
            }

            if (n >= count)
            {
                return mk;
            }
        }

        return 0;
    }
}
