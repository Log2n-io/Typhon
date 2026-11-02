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

    /// <summary>The bazaar's most a crafter pays for an input, × its value (crafting-catalog.md § 15.2).</summary>
    public const double InputOverValue = 1.5;

    /// <summary>Stacks of one kept template a crafter keeps: about its next two crafts' worth; the rest are traded like anyone's (§ 6, Surplus).</summary>
    public const int KeptStacksPerTemplate = 2;

    /// <summary>
    /// The lines crafters work today: weapons and armour, in halves. Potions wait for a use and a value — no bot drinks one, the vendor refuses them and
    /// they cannot be listed — so a potion crafter would only fill its grid (review, 2026-11-02; potions usable is C2-6, #1394).
    /// </summary>
    public static readonly CraftLine[] Lines = [CraftLine.Weapon, CraftLine.Armor];

    /// <summary>What a line's crafters make, in the order they try them.</summary>
    public static ushort[] Targets(CraftLine line) => line switch
    {
        CraftLine.Weapon => [Blaster, Sword, Knuckler],
        CraftLine.Armor => [44, 45, 46, 47, 48, 49],
        _ => [56, 57, 58, 59, 60, 61],
    };

    /// <summary>The craft to start now, or null: the first target, from <paramref name="first"/> round, that it can make or work toward.</summary>
    public static CraftJob Next(CraftBook book, ushort[] targets, int first, IReadOnlyList<HeldStack> held, IReadOnlyList<HeldPart> parts)
    {
        for (var i = 0; i < targets.Length; i++)
        {
            if (Step(book, targets[(first + i) % targets.Length], held, parts, 0) is { } job)
            {
                return job;
            }
        }

        return null;
    }

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

            var quantity = Math.Clamp(want.Count, 1, MarketPlan.StackMax);
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
        foreach (var (template, count) in need)
        {
            var have = 0;
            foreach (var h in held)
            {
                have += h.Template == template ? h.Count : 0;
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
        foreach (var (template, deficit) in Deficits(book, target, held, parts))
        {
            if (buys.Count < 6 && FromVendor(offers, new BookSlot("bulk", template, null, deficit), budget) is { } buy)
            {
                buys.Add(buy);
                budget -= buy.UnitPrice;
            }
        }

        return buys;
    }

    /// <summary>The parts in an inventory's grid: its items with stats of a part's template, with their grades.</summary>
    public static List<HeldPart> PartsOf(IReadOnlyList<GearItem> gear)
    {
        var parts = new List<HeldPart>();
        foreach (var g in gear)
        {
            if (!g.Worn && g.Template is >= FirstPart and <= LastPart)
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
