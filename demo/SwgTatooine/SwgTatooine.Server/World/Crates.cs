using System.Globalization;
using System.Text;

namespace SwgTatooine.Server;

/// <summary>
/// Crates (crafting-catalog.md § 11.2; crafting-build.md § 2.4): identical pristine items with stats held as one instance whose <see cref="ItemStack.Count"/>
/// is how many — one slot, one listing, one stats record for them all.
/// </summary>
/// <remarks>
/// <para><b>Identical</b>: the same template and Mk, made by the same crafter, every stat line, the max condition and the life factor equal — a blueprint's
/// items by construction, hand-crafted ones only when their rolls matched. The craft's serial is not compared: a crate keeps the first item's.</para>
/// <para><b>Pristine</b>: never used nor repaired, its condition whole and not broken — a worn item differs from its twin by its condition.</para>
/// <para><b>Fungible goods do not crate</b>: materials, cores and add-ons are stacks of 99 already.</para>
/// </remarks>
public static class Crates
{
    /// <summary>A crate's capacity (§ 11.2, D2).</summary>
    public const int Capacity = 10;

    /// <summary>Whether an item may be packed: an item with stats, pristine.</summary>
    public static bool Packable(ushort template, in ItemInstance s)
        => ItemCatalog.HasStats(template)
            && s.Repairs == 0
            && (s.Flags & ItemFlags.Broken) == 0
            && s.Condition == s.MaxCondition
            && (Equipment.SlotOf(template) == 0 || (s.Flags & ItemFlags.Pristine) != 0);

    /// <summary>Whether two items of one template and Mk are identical, so may share a crate. <see cref="AppendKey"/> writes these fields.</summary>
    public static bool Identical(in ItemInstance a, in ItemInstance b)
        => a.Crafter == b.Crafter && a.Stat0 == b.Stat0 && a.Stat1 == b.Stat1 && a.Stat2 == b.Stat2 && a.Stat3 == b.Stat3
            && a.MaxCondition == b.MaxCondition && a.LifeMilli == b.LifeMilli;

    /// <summary>
    /// Appends what a packable item packs with, as text: equal for two items exactly when they are <see cref="Identical"/>, of one template and Mk — what a
    /// client groups the inventory by (<c>/swg/inventory</c>'s <c>packKey</c>). The same fields as <see cref="Identical"/>, in its order: the craft's serial
    /// and the flags are not compared, so they are not in it. Nothing is appended for an item that does not pack.
    /// </summary>
    /// <returns>Whether it packs.</returns>
    public static bool AppendKey(StringBuilder json, ushort template, byte mk, in ItemInstance s)
    {
        if (!Packable(template, in s))
        {
            return false;
        }

        var inv = CultureInfo.InvariantCulture;
        json.Append(template.ToString(inv)).Append('.').Append(mk.ToString(inv)).Append('.').Append(s.Crafter.ToString(inv))
            .Append('.').Append(s.Stat0.ToString(inv)).Append('.').Append(s.Stat1.ToString(inv)).Append('.').Append(s.Stat2.ToString(inv))
            .Append('.').Append(s.Stat3.ToString(inv)).Append('.').Append(s.MaxCondition.ToString(inv)).Append('.').Append(s.LifeMilli.ToString(inv));
        return true;
    }
}
