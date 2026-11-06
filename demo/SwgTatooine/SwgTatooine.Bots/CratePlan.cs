using System.Collections.Generic;

namespace SwgTatooine.Bots;

/// <summary>
/// What a bot packs (crafting-catalog.md § 11.2, the parity rule): its identical spare gear — the same <see cref="GearItem.PackKey"/>, in the grid, not what
/// it is about to wear — into crates, so its surplus lists as one listing. Parts and potions are left loose: its crafts and its fights take them one by one.
/// </summary>
/// <remarks>The bot's copy of the server's <c>Crates.Capacity</c>: a refusal is the authority.</remarks>
public static class CratePlan
{
    /// <summary>The command's name in the server's catalog.</summary>
    public const string Command = "PackItem";

    /// <summary>A crate's capacity.</summary>
    public const int Capacity = 10;

    /// <summary>
    /// The packs due, one per group of identical gear: the smallest packed into the fullest it fits. One a group a visit, as a person clicks — the next
    /// inventory read plans the next.
    /// </summary>
    /// <param name="gear">Its items with stats, as the inventory read gave them.</param>
    /// <param name="wear">The item it is about to wear (<see cref="MarketPlan.NextToWear"/>); 0 for none.</param>
    public static List<(ulong Item, ulong Into)> Packs(IReadOnlyList<GearItem> gear, ulong wear = 0, ulong spare = 0)
    {
        var groups = new Dictionary<string, List<GearItem>>();
        foreach (var g in gear)
        {
            // Nor its spare weapon (GearPlan.Spare): a crate is never worn, and the spare packed with a twin was gone (review, 2026-11-04).
            if (g.PackKey == null || g.Worn || g.Wears == 0 || g.Id == wear || (spare != 0 && g.Id == spare) || g.Count >= Capacity)
            {
                continue;
            }

            if (!groups.TryGetValue(g.PackKey, out var group))
            {
                group = [];
                groups[g.PackKey] = group;
            }

            group.Add(g);
        }

        var packs = new List<(ulong, ulong)>();
        foreach (var group in groups.Values)
        {
            if (group.Count < 2)
            {
                continue;
            }

            group.Sort((a, b) => b.Count != a.Count ? b.Count - a.Count : a.Slot - b.Slot);
            var into = group[0];
            for (var i = group.Count - 1; i > 0; i--)
            {
                if (into.Count + group[i].Count <= Capacity)
                {
                    packs.Add((group[i].Id, into.Id));
                    break;
                }
            }
        }

        packs.Sort();
        return packs;
    }
}
