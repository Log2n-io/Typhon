using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SwgTatooine.Bots;

/// <summary>What a bot's inventory last said (inventory-loot.md § 7): its stacks, and how far its rows and lost notes were read.</summary>
/// <param name="Stacks">Stacks held.</param>
/// <param name="LastSeq">The highest item-ledger sequence met in its rows.</param>
/// <param name="LastLost">The highest lost note number met.</param>
public sealed record InventoryView(int Stacks, ulong LastSeq, int LastLost)
{
    public static InventoryView None { get; } = new(0, 0, 0);

    /// <summary>
    /// Reads a <c>GET /swg/inventory</c> answer against the last one, as the browser's loot lines do: a row is new when its sequence is past every row met
    /// before, a lost note when its number is past every note met before. Only the drops count as loot rows (<c>kill</c>, <c>lair</c>, <c>mission</c>).
    /// </summary>
    /// <param name="json">The answer.</param>
    /// <param name="before">What the last answer said.</param>
    /// <param name="count">Whether new rows and notes count: false on the first read, which only learns what was already there.</param>
    /// <param name="lootRows">Loot rows met for the first time, when counted.</param>
    /// <param name="unitsLost">Units in lost notes met for the first time, when counted.</param>
    /// <returns>What this answer says.</returns>
    public static InventoryView Parse(string json, InventoryView before, bool count, out int lootRows, out int unitsLost) =>
        Parse(json, before, count, out lootRows, out unitsLost, null, null);

    /// <summary>
    /// <see cref="Parse(string, InventoryView, bool, out int, out int)"/>, and what the bazaar needs besides (bazaar.md § 8): every stack held, and the
    /// bazaar buys collected — <c>buy</c> rows into the character met for the first time, when counted — each with its listing.
    /// </summary>
    /// <param name="held">Filled with the stacks held, by slot; null when not wanted.</param>
    /// <param name="won">Filled with the buys collected since the last read: the listing (the row's <c>ref</c>), what and how many; null when not wanted.</param>
    public static InventoryView Parse(string json, InventoryView before, bool count, out int lootRows, out int unitsLost, List<HeldStack> held,
        List<WonListing> won)
    {
        lootRows = 0;
        unitsLost = 0;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var lastSeq = before.LastSeq;
        foreach (var row in root.GetProperty("recent").EnumerateArray())
        {
            var seq = ulong.Parse(row.GetProperty("seq").GetString() ?? "0", CultureInfo.InvariantCulture);
            if (seq <= before.LastSeq)
            {
                continue;
            }

            lastSeq = Math.Max(lastSeq, seq);
            var kind = row.GetProperty("kind").GetString();
            if (count && kind is "kill" or "lair" or "mission")
            {
                lootRows++;
            }

            // A bazaar buy collected: the listing's escrow moved into this character (bazaar.md § 2.2, item kind 6 `buy`, its Ref the listing).
            if (count && won != null && kind == "buy" && row.TryGetProperty("dir", out var dir) && dir.GetString() == "in")
            {
                var item = row.GetProperty("item").GetString();
                won.Add(new WonListing(
                    MarketJson.Id(row.GetProperty("ref")),
                    MarketCatalog.TemplateOf(row, item),
                    item,
                    row.GetProperty("mk").GetInt32(),
                    row.GetProperty("count").GetInt32()));
            }
        }

        var lastLost = before.LastLost;
        foreach (var note in root.GetProperty("lost").EnumerateArray())
        {
            var n = note.GetProperty("n").GetInt32();
            if (n <= before.LastLost)
            {
                continue;
            }

            lastLost = Math.Max(lastLost, n);
            unitsLost += count ? note.GetProperty("count").GetInt32() : 0;
        }

        var items = root.GetProperty("items");
        if (held != null)
        {
            foreach (var s in items.EnumerateArray())
            {
                var item = s.GetProperty("item").GetString();
                held.Add(new HeldStack(
                    MarketCatalog.TemplateOf(s, item),
                    item,
                    MarketCatalog.CategoryOf(s.TryGetProperty("category", out var c) ? c.GetString() : null),
                    s.GetProperty("mk").GetInt32(),
                    s.GetProperty("count").GetInt32()));
            }
        }

        return new InventoryView(items.GetArrayLength(), lastSeq, lastLost);
    }
}
