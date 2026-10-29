using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SwgTatooine.Bots;

/// <summary>What a bot's inventory last said (inventory-loot.md § 7): its stacks, and how far its lost notes were read.</summary>
/// <param name="Stacks">Stacks held.</param>
/// <param name="LastLost">The highest lost note number met.</param>
/// <remarks>
/// <b>No ledger rows.</b> The answer carries none: the ledger is the operators' audit trail, never read by a game client (Loïc, 2026-10-29). Drops and
/// bazaar buys reach the bot as the server's notices (<see cref="BotEvents"/>).
/// </remarks>
public sealed record InventoryView(int Stacks, int LastLost)
{
    public static InventoryView None { get; } = new(0, 0);

    /// <summary>Reads a <c>GET /swg/inventory</c> answer against the last one: a lost note is new when its number is past every note met before.</summary>
    /// <param name="json">The answer.</param>
    /// <param name="before">What the last answer said.</param>
    /// <param name="count">Whether new notes count: false on the first read, which only learns what was already there.</param>
    /// <param name="unitsLost">Units in lost notes met for the first time, when counted.</param>
    /// <param name="held">Filled with the stacks held, by slot; null when not wanted.</param>
    /// <returns>What this answer says.</returns>
    public static InventoryView Parse(string json, InventoryView before, bool count, out int unitsLost, List<HeldStack> held = null)
    {
        unitsLost = 0;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
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

        return new InventoryView(items.GetArrayLength(), lastLost);
    }
}
