using System;
using System.Collections.Generic;

namespace SwgTatooine.Bots;

/// <summary>
/// What a bot refines at a station (crafting-catalog.md § 11.1, the parity rule): the cores and add-ons it holds below its band — the lesser loot a player
/// has no use for — N of a Mk into one of the next, as many as one action makes. Materials are left: they feed its crafts in bulk.
/// </summary>
/// <remarks>The bot's copy of the server's <c>Refining</c> numbers, as <see cref="Brain.Perception"/> copies its reasons: a refusal is the authority.</remarks>
public static class RefinePlan
{
    /// <summary>The command's name in the server's catalog.</summary>
    public const string Command = "RefineItems";

    /// <summary>The most cores or add-ons one refine makes: a crate (§ 11.2).</summary>
    public const int MaxPerAction = 10;

    private static readonly int[] Ratio = [0, 3, 4, 5, 7];

    /// <summary>N for a core or an add-on refined from Mk <paramref name="mk"/>; 0 past Mk IV.</summary>
    public static int RatioOf(int mk) => mk is >= 1 and <= 4 ? Ratio[mk] : 0;

    /// <summary>
    /// The refines due: each core or add-on below <paramref name="band"/> held at least N times, not an input its next craft takes at that Mk, with how
    /// many of the next Mk to make.
    /// </summary>
    /// <param name="held">Its stacks, as the inventory read gave them.</param>
    /// <param name="band">Its band (CL 1-10 is 1, …).</param>
    /// <param name="keep">(template, Mk) its next craft uses; null for none.</param>
    public static List<(ushort Template, int Mk, int Count)> Due(IReadOnlyList<HeldStack> held, int band, HashSet<(ushort, int)> keep = null)
    {
        var units = new Dictionary<(ushort, int), int>();
        foreach (var s in held)
        {
            if (s.Category is MarketCategory.Core or MarketCategory.Addon && s.Mk >= 1 && s.Mk < band && RatioOf(s.Mk) > 0
                && keep?.Contains((s.Template, s.Mk)) != true)
            {
                units[(s.Template, s.Mk)] = (units.TryGetValue((s.Template, s.Mk), out var n) ? n : 0) + s.Count;
            }
        }

        var due = new List<(ushort, int, int)>();
        foreach (var ((template, mk), count) in units)
        {
            var make = Math.Min(count / RatioOf(mk), MaxPerAction);
            if (make > 0)
            {
                due.Add((template, mk, make));
            }
        }

        due.Sort();
        return due;
    }
}
