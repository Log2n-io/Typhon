using System;
using System.Collections.Generic;

namespace SwgTatooine.Bots;

/// <summary>
/// A bot's look at its own gear (Loïc, 2026-11-04: bots spend their money to get stronger, and replace what is broken): whether it is down to its fist,
/// the spare weapon it carries, whether a spare is worth buying, and whether a trip to the bazaar is due — pure functions over its inventory read.
/// </summary>
/// <remarks>
/// <para>
/// <b>A broken weapon is no weapon.</b> It stays worn and does nothing (crafting-catalog.md § 9.2): the body fights with its fist, which reaches a metre and
/// strikes only in the unarmed stance. In the first garrison siege 94 % of the bots' blasters were broken and nothing sent them to replace one — the visit
/// plan knew how (<see cref="MarketPlan.GearWanted"/>), but a visit came only with a full grid.
/// </para>
/// <para>
/// <b>The assessment is the trigger</b>: every <see cref="AssessSeconds"/>, and after a mission handed in, the bot weighs what it wears against its band
/// and its wallet; a trip is due when a piece is to be replaced or bettered and it can pay for one, or when a spare is affordable. Down to its fist with no
/// spare, the trip is due at once, whatever the clock.
/// </para>
/// </remarks>
public static class GearPlan
{
    /// <summary>Seconds between two assessments; a mission handed in brings the next one forward.</summary>
    public const double AssessSeconds = 600d;

    // ── Stims (crafting-catalog.md § 5.4; Loïc, 2026-11-05: potions crafted, sold and drunk — by the bots too) ───────────────────────────────────────

    /// <summary>The stim's template and asset (the server's <c>ItemCatalog.Stim</c>): no Mk.</summary>
    public const ushort Stim = 56;

    public const string StimItem = "potion_stim";

    /// <summary>A bot drinks below this share of its health, while something is on it: out of a fight it heals by resting, for nothing.</summary>
    public const float DrinkBelow = 0.5f;

    /// <summary>Seconds a bot waits between two drinks: the server's 15 s cooldown and a margin, so it never asks within it.</summary>
    public const double DrinkEverySeconds = 15.5d;

    /// <summary>Stims a bot keeps — the welcome pack's three; past them, it lists the rest. A crafter of them keeps one, and lives off the others.</summary>
    public const int StimsKept = 3;

    public const int CrafterStimsKept = 1;

    /// <summary>A bot holding fewer doses than this buys a stim — less than two stims' worth: one full one and a dose.</summary>
    public const int DosesWanted = 4;

    /// <summary>
    /// The stim to drink now, or 0: hurt below <see cref="DrinkBelow"/>, standing, attacked — the one with the fewest doses left, a crate last (the server
    /// unpacks one from it, which needs a free slot).
    /// </summary>
    public static ulong StimToDrink(IReadOnlyList<GearItem> gear, float health, bool attacked)
    {
        if (!attacked || health <= 0f || health >= DrinkBelow)
        {
            return 0;
        }

        GearItem? best = null;
        foreach (var g in gear)
        {
            if (g.Template != Stim || g.Condition <= 0)
            {
                continue;
            }

            if (best is not { } b || (b.Crate && !g.Crate) || (b.Crate == g.Crate && g.Condition < b.Condition))
            {
                best = g;
            }
        }

        return best?.Id ?? 0;
    }

    /// <summary>The stims it holds, a crate's count included.</summary>
    public static int StimsHeld(IReadOnlyList<GearItem> gear)
    {
        var n = 0;
        foreach (var g in gear)
        {
            n += g.Template == Stim ? g.Count : 0;
        }

        return n;
    }

    /// <summary>The doses it holds: each stim's left, a crate's count of full ones.</summary>
    public static int StimDoses(IReadOnlyList<GearItem> gear)
    {
        var n = 0;
        foreach (var g in gear)
        {
            n += g.Template == Stim ? g.Count * Math.Max(0, g.Condition) : 0;
        }

        return n;
    }

    /// <summary>Whether it buys a stim: fewer than <see cref="DosesWanted"/> doses held, and the vendor's price and its reserve in the wallet.</summary>
    public static bool StimWanted(IReadOnlyList<GearItem> gear, long credits, long price)
        => StimDoses(gear) < DosesWanted && credits >= price + MarketPlan.StandardReserve;

    /// <summary>
    /// The stims to sell, past the <paramref name="kept"/> it keeps: full ones only — a part-drunk one is always kept, and spends none of the keeping — in
    /// grid order, a crate as one listing. What <see cref="MarketPlan.Surplus"/> lists and <see cref="CraftPlan.ForSale"/> counts, so the two agree.
    /// </summary>
    public static List<ulong> StimsToSell(IReadOnlyList<GearItem> gear, int kept)
    {
        var sell = new List<ulong>();
        var left = kept;
        foreach (var g in gear)
        {
            if (g.Template != Stim || g.Condition < g.MaxCondition)
            {
                continue;
            }

            if (left > 0)
            {
                left -= g.Count;
                continue;
            }

            sell.Add(g.Id);
        }

        return sell;
    }

    /// <summary>The stim listing to buy: the cheapest single full one under the vendor's price, keeping its reserve; null for none.</summary>
    public static BrowseRow? ChooseStim(IReadOnlyList<BrowseRow> rows, long vendorPrice, long wallet, IReadOnlyCollection<ulong> own)
    {
        BrowseRow? best = null;
        foreach (var r in rows)
        {
            if (r.Item != StimItem || r.Quantity != 1 || r.Condition < r.MaxCondition || r.Unit >= vendorPrice || wallet - r.Total < MarketPlan.StandardReserve
                || MarketPlan.Contains(own, r.Id))
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

    /// <summary>What a bot keeps over a spare's price before it buys one: a replacement's and a few repairs' worth.</summary>
    public const long SpareReserve = 5_000;

    /// <summary>What a piece is taken to cost when the vendor's list does not name it: about standard issue's Mk I (crafting-catalog.md § 15.3).</summary>
    public const long PieceFloor = 2_000;

    /// <summary>The weapon slot (<c>EquipSlot.Weapon</c>).</summary>
    public const int WeaponSlot = 101;

    /// <summary>
    /// Whether the bot fights with its fist: nothing sound worn in the weapon slot — no weapon, or a broken one. With a <paramref name="weapon"/> template,
    /// only that weapon counts.
    /// </summary>
    public static bool Unarmed(IReadOnlyList<GearItem> gear, ushort weapon)
    {
        foreach (var g in gear)
        {
            if (g.Worn && g.Wears == WeaponSlot && !g.Broken && !g.Crate && (weapon == 0 || g.Template == weapon))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The spare weapon it carries: in the grid, its own weapon, not a crate, of a Mk it may wear (<paramref name="band"/>) — the best sound one (the highest
    /// Mk, then the best condition); failing that a worn-down one it can repair (a kit held and a repair left). 0 for none.
    /// </summary>
    /// <remarks>Within its band (review, 2026-11-04): a spare it cannot put on left it on its fist for good, nothing else being bought.</remarks>
    public static ulong Spare(IReadOnlyList<GearItem> gear, ushort weapon, IReadOnlyList<HeldStack> held = null, int band = 5)
    {
        GearItem? best = null;
        var bestSound = false;
        var kits = held != null && HoldsKit(held, MarketPlan.KitWeapon);
        foreach (var g in gear)
        {
            if (g.Worn || g.Crate || g.Wears != WeaponSlot || (weapon != 0 && g.Template != weapon) || g.Mk > Math.Max(1, band))
            {
                continue;
            }

            var sound = !g.WornDown;
            if (!sound && !(kits && g.Repairs < MarketPlan.MaxRepairs))
            {
                continue;
            }

            if (best is not { } b || (sound && !bestSound) || (sound == bestSound && (g.Mk > b.Mk || (g.Mk == b.Mk && Share(g) > Share(b)))))
            {
                best = g;
                bestSound = sound;
            }
        }

        return best?.Id ?? 0;
    }

    /// <summary>Whether its spare (<see cref="Spare"/>) is one it can put on now: sound, not waiting for a repair.</summary>
    public static bool SoundSpare(IReadOnlyList<GearItem> gear, ushort weapon, IReadOnlyList<HeldStack> held = null, int band = 5)
    {
        var id = Spare(gear, weapon, held, band);
        foreach (var g in gear)
        {
            if (g.Id == id && id != 0)
            {
                return !g.WornDown;
            }
        }

        return false;
    }

    /// <summary>
    /// Down to its fist with nothing sound to put on instead, and nothing it can repair (its weapon or its spare, a kit held): a weapon is wanted before
    /// anything else. One it can repair sends it to a station instead (<see cref="MarketPlan.RepairsDue"/>).
    /// </summary>
    public static bool Urgent(IReadOnlyList<GearItem> gear, ushort weapon, IReadOnlyList<HeldStack> held = null, int band = 5)
        => Unarmed(gear, weapon) && !SoundSpare(gear, weapon, held, band) && !RepairsWeapon(gear, weapon, held, band);

    // Whether a kit is held for its weapon or its spare, worn down with a repair left.
    private static bool RepairsWeapon(IReadOnlyList<GearItem> gear, ushort weapon, IReadOnlyList<HeldStack> held, int band)
    {
        foreach (var id in MarketPlan.RepairsDue(gear, held ?? [], weapon, band))
        {
            foreach (var g in gear)
            {
                if (g.Id == id && g.Wears == WeaponSlot)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a spare weapon is worth buying (Loïc, 2026-11-04): armed, carrying none, and its wallet holds the spare's price and
    /// <see cref="SpareReserve"/> over it.
    /// </summary>
    public static bool SpareWanted(IReadOnlyList<GearItem> gear, ushort weapon, IReadOnlyList<HeldStack> held, long credits, long price, int band = 5)
        => !Unarmed(gear, weapon) && Spare(gear, weapon, held, band) == 0 && credits >= price + SpareReserve;

    /// <summary>
    /// The assessment: whether a trip to the bazaar is due — no weapon it can fight with; a piece to replace or better (<see cref="MarketPlan.GearWanted"/>)
    /// that it can pay for over its reserve; or a spare it can afford.
    /// </summary>
    /// <param name="gear">What it wears and carries.</param>
    /// <param name="held">Its stacks: the kits that repair instead.</param>
    /// <param name="offers">The vendor's standard issue, for the prices; null when unread.</param>
    /// <param name="band">The highest Mk it may wear.</param>
    /// <param name="credits">Its wallet.</param>
    /// <param name="weapon">The weapon it fights with.</param>
    /// <param name="prices">The board as last read: an upgrade is due only when one is listed, at a price it can pay; null when unread.</param>
    /// <remarks>
    /// Review, 2026-11-04: every case must be one it can pay for — a bot with nothing walked to the bazaar after every mission, and one below its band went
    /// every ten minutes for an upgrade the vendor does not sell (Mk I only) and nobody listed.
    /// </remarks>
    public static bool Due(IReadOnlyList<GearItem> gear, IReadOnlyList<HeldStack> held, IReadOnlyList<StandardOffer> offers, int band, long credits,
        ushort weapon, MarketPrices prices = null)
    {
        var weaponPrice = PriceOf(offers, WeaponItem(offers, weapon));
        if (Urgent(gear, weapon, held, band))
        {
            return credits >= weaponPrice + MarketPlan.StandardReserve;
        }

        // A kit only when one is listed — nobody sells one but the crafters (the night of 2026-11-04: 78 % of assessments were due, most for a kit nobody had
        // listed and a piece the vendor would not replace, every ten minutes).
        var kit = KitWanted(gear, held, weapon, band);
        if (kit != 0 && (prices == null || prices.ListedOf(CraftPlan.OutputItem(kit), 0) > 0)
            && credits - MarketPlan.StandardReserve >= (long)(MarketCatalog.KitValue * KitOverValue))
        {
            return true;
        }

        // Short of stims: the vendor always sells one.
        if (StimWanted(gear, credits, PriceOf(offers, StimItem)))
        {
            return true;
        }

        // Every slot it lacks or could better, not the first (Loïc, 2026-11-05): a replacement at the vendor's price where the vendor would sell it (a slot
        // empty, a piece broken or past its repairs), else the board's cheapest of its band; an upgrade at the cheapest listing above what it has, within
        // the band — either family of armour.
        var vendorSlots = MarketPlan.StandardIssues(offers, gear, long.MaxValue / 4, held, weapon, [], band);
        foreach (var piece in MarketPlan.GearNeeds(gear, offers, band, held, weapon))
        {
            var vendor = piece.HaveMk == 0 && StandardCovers(offers, vendorSlots, piece.Slot);
            var listed = CheapestListed(prices, piece.Item, piece.HaveMk + 1, Math.Max(1, band), offers);
            var twin = CheapestListed(prices, MarketPlan.Twin(piece.Item), piece.HaveMk + 1, Math.Max(1, band), offers);
            listed = listed == 0 || (twin > 0 && twin < listed) ? twin : listed;
            var price = vendor ? Math.Min(PriceOf(offers, piece.Item), listed > 0 ? listed : long.MaxValue) : listed;
            if (price > 0 && credits - MarketPlan.StandardReserve >= price)
            {
                return true;
            }
        }

        return SpareWanted(gear, weapon, held, credits, weaponPrice, band);
    }

    // Whether the vendor's standard issue fills a slot: one of the issues it would sell names it.
    private static bool StandardCovers(IReadOnlyList<StandardOffer> offers, List<MarketCommand> issues, int slot)
    {
        foreach (var issue in issues)
        {
            foreach (var o in offers)
            {
                if (o.Template == issue.Template && o.Wears == slot)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // The lowest listing of an item between two Mks, planet-wide — a visit buys in any city — at a price the visit would pay (MarketPlan.ChooseGear's cap);
    // 0 when none is, the board is unread, or there is no item.
    private static long CheapestListed(MarketPrices prices, string item, int fromMk, int toMk, IReadOnlyList<StandardOffer> offers = null)
    {
        long best = 0;
        for (var mk = fromMk; prices != null && item != null && mk <= toMk; mk++)
        {
            var cap = Math.Max(MarketPlan.GearOverValue * prices.ValueOf(item, mk), MarketPlan.StandardPrice(offers, item, mk));
            var lowest = prices.ListedOf(item, mk) > 0 ? prices.LowestOf(item, mk) : 0;
            best = lowest > 0 && lowest <= cap && (best == 0 || lowest < best) ? lowest : best;
        }

        return best;
    }

    /// <summary>
    /// The repair kit to buy (Loïc, 2026-11-04): what it wears — or its spare weapon — is worn down with a repair left and no kit of its class is held. A kit
    /// and the station's fee are a fraction of a replacement; 0 when none is wanted.
    /// </summary>
    public static ushort KitWanted(IReadOnlyList<GearItem> gear, IReadOnlyList<HeldStack> held, ushort weapon = 0, int band = 5)
    {
        var spare = weapon != 0 ? Spare(gear, weapon, held, band) : 0;
        foreach (var g in gear)
        {
            if ((g.Worn || (spare != 0 && g.Id == spare)) && g.WornDown && g.Repairs < MarketPlan.MaxRepairs && !g.Crate)
            {
                var kit = MarketPlan.KitFor(g.Wears);
                if (held == null || !HoldsKit(held, kit))
                {
                    return kit;
                }
            }
        }

        return 0;
    }

    /// <summary>The most a bot pays for a kit, × its value: still a fraction of what it saves.</summary>
    public const double KitOverValue = 3d;

    /// <summary>The kit listing to buy: the cheapest unit, at ≤ <see cref="KitOverValue"/> × its value, keeping its reserve; null for none.</summary>
    public static BrowseRow? ChooseKit(IReadOnlyList<BrowseRow> rows, MarketPrices prices, long wallet, IReadOnlyCollection<ulong> own)
    {
        BrowseRow? best = null;
        foreach (var r in rows)
        {
            var value = prices.ValueOf(r.Item, r.Mk);
            if (value <= 0d || r.Unit > KitOverValue * value || wallet - r.Total < MarketPlan.StandardReserve || MarketPlan.Contains(own, r.Id))
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

    /// <summary>The vendor's price for an item, or <see cref="PieceFloor"/> when its list does not name it.</summary>
    public static long PriceOf(IReadOnlyList<StandardOffer> offers, string item)
    {
        if (offers != null && item != null)
        {
            foreach (var o in offers)
            {
                if (o.Item == item)
                {
                    return o.Price;
                }
            }
        }

        return PieceFloor;
    }

    /// <summary>The asset of the weapon it fights with, from the vendor's list; the blaster's when the list does not name it.</summary>
    public static string WeaponItem(IReadOnlyList<StandardOffer> offers, ushort weapon)
    {
        if (offers != null)
        {
            foreach (var o in offers)
            {
                if (o.Template == weapon)
                {
                    return o.Item;
                }
            }
        }

        return "blaster_dl44";
    }

    private static bool HoldsKit(IReadOnlyList<HeldStack> held, ushort kit)
    {
        foreach (var h in held)
        {
            if (h.Template == kit && h.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static double Share(GearItem g) => g.MaxCondition > 0 ? (double)g.Condition / g.MaxCondition : 0d;
}
