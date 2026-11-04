using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>How an item with stats left the world (crafting-build.md § 8's C3 measures; Loïc, 2026-11-03: "measure decay first").</summary>
public enum GearEnd : byte
{
    /// <summary>Worn to nothing on its owner with no repair left: a <see cref="ItemMoveKind.Decay"/> row from the wear.</summary>
    WornOut,

    /// <summary>Broken, handed to the vendor, who takes it for nothing: a <see cref="ItemMoveKind.Decay"/> row from the market.</summary>
    Scrapped,

    /// <summary>Sold to the vendor, paid by the condition it had left.</summary>
    Sold,

    /// <summary>Salvaged at a station for some of its materials.</summary>
    Salvaged,
}

/// <summary>
/// The end of every item with stats, by how it ended (<see cref="GearEnd"/>): how many, of which Mk, how much of its condition was left, how many repairs it
/// had used — what says whether decay finishes its job (an item worn out) or is cut short (an item sold with half its life). For <c>/swg/stats</c>'s
/// <c>decay.ends</c> and the dashboard's decay card.
/// </summary>
/// <remarks>
/// Noted in the transaction that ends the item and published when it commits (<see cref="EndItems"/>, which every writer of items calls), so an end that
/// did not commit is not counted. A life in play-hours would need the item's mint time, which no component holds; the condition left and the repairs used
/// say the same thing — how much of what it could have given was given — without a schema change.
/// </remarks>
public sealed partial class SimBridge
{
    private const int GearEndKinds = 4;

    // Per end: count, Σ condition left (per mille of its max), Σ repairs used, a histogram of the condition left by tenths, and the count by Mk band.
    private readonly long[] _gearEnds = new long[GearEndKinds];
    private readonly long[] _gearEndLeftMilli = new long[GearEndKinds];
    private readonly long[] _gearEndRepairs = new long[GearEndKinds];
    private readonly long[] _gearEndLeftTenths = new long[GearEndKinds * 11];
    private readonly long[] _gearEndByBand = new long[GearEndKinds * 6];
    private readonly List<(GearEnd End, byte Mk, int LeftMilli, byte Repairs)> _pendingGearEnds = [];

    /// <summary>Items with stats ended by <paramref name="end"/> since start.</summary>
    public long GearEnds(GearEnd end) => Interlocked.Read(ref _gearEnds[(int)end]);

    /// <summary>Notes an item with stats ending, in the transaction under way: its stats as they were when it ended.</summary>
    private void NoteGearEnd(GearEnd end, byte mk, in ItemInstance stats)
    {
        // An item without a condition (a part: a grade, nothing to wear) is counted whole.
        var left = stats.MaxCondition > 0 ? (int)Math.Clamp(stats.Condition * 1000L / stats.MaxCondition, 0, 1000) : 1000;
        _pendingGearEnds.Add((end, mk, left, stats.Repairs));
        _itemsDirty = true;
    }

    /// <summary>The ends a transaction noted, published when it committed and forgotten when it did not. Called by <see cref="EndItems"/>.</summary>
    private void EndGearEnds(bool committed)
    {
        if (committed)
        {
            foreach (var (end, mk, left, repairs) in _pendingGearEnds)
            {
                var e = (int)end;
                Interlocked.Increment(ref _gearEnds[e]);
                Interlocked.Add(ref _gearEndLeftMilli[e], left);
                Interlocked.Add(ref _gearEndRepairs[e], repairs);
                Interlocked.Increment(ref _gearEndLeftTenths[(e * 11) + (left / 100)]);
                Interlocked.Increment(ref _gearEndByBand[(e * 6) + Math.Min((int)mk, 5)]);
            }
        }

        _pendingGearEnds.Clear();
    }

    /// <summary>
    /// <c>,"ends":{"wornOut":{"count","byMk":[…],"conditionLeftMedianPct","conditionLeftMeanPct","repairsMean"},"scrapped":{…},"sold":{…},"salvaged":{…}}</c>.
    /// </summary>
    private void AppendGearEndsJson(StringBuilder j)
    {
        var inv = CultureInfo.InvariantCulture;
        j.Append(",\"ends\":{");
        for (var e = 0; e < GearEndKinds; e++)
        {
            var n = Interlocked.Read(ref _gearEnds[e]);
            j.Append(e == 0 ? "\"" : ",\"").Append(NameOf((GearEnd)e)).Append("\":{\"count\":").Append(n.ToString(inv)).Append(",\"byMk\":[");
            for (var mk = 1; mk <= 5; mk++)
            {
                j.Append(mk == 1 ? "" : ",").Append(Interlocked.Read(ref _gearEndByBand[(e * 6) + mk]).ToString(inv));
            }

            j.Append("],\"conditionLeftMedianPct\":").Append(n == 0 ? "null" : MedianTenths(e).ToString(inv))
                .Append(",\"conditionLeftMeanPct\":")
                .Append(n == 0 ? "null" : (Interlocked.Read(ref _gearEndLeftMilli[e]) / 10d / n).ToString("0.#", inv))
                .Append(",\"repairsMean\":").Append(n == 0 ? "null" : ((double)Interlocked.Read(ref _gearEndRepairs[e]) / n).ToString("0.##", inv))
                .Append('}');
        }

        j.Append('}');
    }

    // The median of the condition left, to the tenth it falls in, as a percentage: 0, 10, … 100.
    private int MedianTenths(int e)
    {
        var n = Interlocked.Read(ref _gearEnds[e]);
        var seen = 0L;
        for (var t = 0; t <= 10; t++)
        {
            seen += Interlocked.Read(ref _gearEndLeftTenths[(e * 11) + t]);
            if (seen * 2 >= n)
            {
                return t * 10;
            }
        }

        return 100;
    }

    private static string NameOf(GearEnd end) => end switch
    {
        GearEnd.WornOut => "wornOut",
        GearEnd.Scrapped => "scrapped",
        GearEnd.Sold => "sold",
        _ => "salvaged",
    };
}
