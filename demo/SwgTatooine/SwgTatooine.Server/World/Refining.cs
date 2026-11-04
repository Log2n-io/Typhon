namespace SwgTatooine.Server;

/// <summary>
/// Refine (crafting-catalog.md § 11.1): N items of Mk <i>k</i> → 1 of Mk <i>k</i>+1 of the same kind, at a crafting station, for a fee of 5 % of the
/// output's value. Whatever is looted or extracted refines — materials, cores and add-ons; crafted items have a grade, not a tier, and do not.
/// </summary>
/// <remarks>
/// <para><b>N</b>: 3 / 4 / 5 / 7 for cores and add-ons, 12 / 3 / 4 / 6 for materials, by the step's input Mk (§ 11.1's two tables).</para>
/// <para><b>One crate per action</b> (§ 11.1): a core or an add-on refines at most <see cref="MaxItemsPerAction"/> outputs at once, a material a stack's worth.
/// The catalogue's 5 s per input is not built: no craft step is timed either.</para>
/// <para><b>Crafted Mk I cores refine too</b>, against § 11.1's "Basic cores": a crafted core is the looted core's template, so nothing tells the two apart
/// yet. It matters once extractors (C5) make bulk Mk I material cheap.</para>
/// </remarks>
public static class Refining
{
    /// <summary>
    /// The fee, thousandths of the output's market value (§ 15.4): 77 / 154 / 297 / 555 cr for a core into Mk II-V, rounded down, 1 at least.
    /// </summary>
    public const int FeePerMille = 50;

    /// <summary>The most cores or add-ons one refine makes: a crate's capacity (§ 11.2, D2).</summary>
    public const int MaxItemsPerAction = 10;

    private static readonly int[] ItemRatio = [0, 3, 4, 5, 7];
    private static readonly int[] MaterialRatio = [0, 12, 3, 4, 6];

    /// <summary>How many of <paramref name="template"/> at Mk <paramref name="mk"/> make one of the next Mk; 0 when it does not refine (or is Mk V).</summary>
    public static int RatioOf(ushort template, byte mk)
    {
        if (mk is < 1 or > 4 || !ItemCatalog.TryGet(template, out var t) || !t.HasMk)
        {
            return 0;
        }

        return t.Category switch
        {
            ItemCategory.Material => MaterialRatio[mk],
            ItemCategory.Core or ItemCategory.Addon => ItemRatio[mk],
            _ => 0,
        };
    }

    /// <summary>The most outputs one refine of <paramref name="template"/> makes; 0 when it does not refine.</summary>
    public static int MaxOutputs(ushort template)
        => !ItemCatalog.TryGet(template, out var t) ? 0
            : t.Category == ItemCategory.Material ? ItemLedger.StackMax
            : t.Category is ItemCategory.Core or ItemCategory.Addon ? MaxItemsPerAction : 0;

    /// <summary>
    /// The fee for <paramref name="outputs"/> of the next Mk, whole credits, rounded down — and 1 at least: a material's 5 % is a fraction of a credit, and
    /// a refine split into small actions would otherwise pay nothing at all (review, 2026-11-03).
    /// </summary>
    public static long FeeOf(ushort template, byte mk, int outputs)
    {
        var milli = MarketValues.MilliOf(template, (byte)(mk + 1)) * outputs;
        return milli <= 0 ? 0 : System.Math.Max(1, milli * FeePerMille / 1000 / 1000);
    }
}
