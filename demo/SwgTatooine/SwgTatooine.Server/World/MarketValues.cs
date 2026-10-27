namespace SwgTatooine.Server;

/// <summary>
/// The market value of an item, per (category, Mk), in milli-credits (bazaar.md § 2.3): what the vendor pays a share of (C3), and what the Sell tab and the
/// bots price from.
/// </summary>
/// <remarks>
/// <para>
/// From <c>balance_model.py</c>'s <c>mat_value(b)</c> and <c>core_value(b)</c> (claude/design/SwgTatooine/design/balance/balance_model.py): a material unit
/// rounded to 100 milli-credits, a core to the credit. <b>An add-on is valued as a core [EST]</b> — the model has no add-on value, as it has no add-on drop
/// rate (inventory-loot § 4). Parts, armour, potions, kits and the crate have none yet: nothing makes them, and the vendor refuses them. Pinned by
/// <c>MarketValueChecks</c> against the model's own numbers.
/// </para>
/// </remarks>
public static class MarketValues
{
    // Index Mk 1..5; 0 unused.
    private static readonly long[] MaterialMilli = [0, 1_700, 2_800, 4_500, 7_200, 11_400];
    private static readonly long[] CoreMilli = [0, 726_000, 1_549_000, 3_099_000, 5_950_000, 11_106_000];

    /// <summary>One unit's market value, milli-credits; 0 for an item without one (the vendor refuses it).</summary>
    public static long MilliOf(ushort template, byte mk)
    {
        if (!ItemCatalog.TryGet(template, out var t) || !ItemCatalog.MkFits(in t, mk) || mk is < 1 or > 5)
        {
            return 0;
        }

        return t.Category switch
        {
            ItemCategory.Material => MaterialMilli[mk],
            ItemCategory.Core or ItemCategory.Addon => CoreMilli[mk],
            _ => 0,
        };
    }

    /// <summary>One unit's market value, whole credits, rounded down.</summary>
    public static long CreditsOf(ushort template, byte mk) => MilliOf(template, mk) / 1000;

    /// <summary>What the vendor pays for <paramref name="count"/> units: ⌊count × value × 20 %⌋ credits (B7); 0 is refused.</summary>
    public static long VendorPayout(ushort template, byte mk, int count)
        => count <= 0 ? 0 : count * MilliOf(template, mk) * TatooineData.VendorBuysPerMille / 1000 / 1000;
}
