using System;
using System.Collections.Generic;

namespace Typhon.Engine.Internals;

/// <summary>
/// A Sphere profile's distance bands, compiled (09 § 9): up to three boundaries as fractions of the radius, and each band's period. Band 0 is the near
/// region, inside the first boundary, sent every tick.
/// </summary>
internal readonly struct LodBands
{
    /// <summary>How many bands are declared; 0 when every update is sent every tick.</summary>
    public readonly int Count;

    /// <summary>Each band's inner boundary, a fraction of the radius, innermost first.</summary>
    public readonly double F1, F2, F3;

    /// <summary>Each band's period, in ticks.</summary>
    public readonly int N1, N2, N3;

    /// <summary>
    /// Each band's window, in ticks: the history a flush carries. The period, except for a while after a session's level fell (<see cref="AtLevel"/>).
    /// </summary>
    public readonly int W1, W2, W3;

    /// <summary>Where a profile with no bands starts its implicit one at a level above zero (09 § 10): half the radius.</summary>
    public const double ImplicitBeyond = 0.5;

    /// <summary>The longest period a level may reach: a flush's history must stay in the push log.</summary>
    public const int MaxPeriod = 8;

    public LodBands(IReadOnlyList<DistanceBand> bands)
    {
        Count = bands.Count;
        F1 = Count > 0 ? bands[0].Beyond : 1d;
        N1 = Count > 0 ? bands[0].Every : 1;
        F2 = Count > 1 ? bands[1].Beyond : 1d;
        N2 = Count > 1 ? bands[1].Every : 1;
        F3 = Count > 2 ? bands[2].Beyond : 1d;
        N3 = Count > 2 ? bands[2].Every : 1;
        W1 = N1;
        W2 = N2;
        W3 = N3;
    }

    private LodBands(int count, double f1, double f2, double f3, int n1, int n2, int n3, int w1, int w2, int w3)
    {
        Count = count;
        F1 = f1;
        F2 = f2;
        F3 = f3;
        N1 = n1;
        N2 = n2;
        N3 = n3;
        W1 = w1;
        W2 = w2;
        W3 = w3;
    }

    /// <summary>
    /// The bands a session at LOD level <paramref name="level"/> is gathered with (09 § 10): every period doubled per level, up to
    /// <see cref="MaxPeriod"/>; a profile with none gets one beyond <see cref="ImplicitBeyond"/> of the radius, every <c>2^level</c> ticks. The boundaries
    /// never move. <paramref name="windowLevel"/> widens the windows to that level's periods: after a level falls, a band's next flush must still carry
    /// what the slower schedule held back.
    /// </summary>
    /// <param name="level">The level, 0–3.</param>
    /// <param name="windowLevel">The level whose periods the windows span, when above <paramref name="level"/>; 0 otherwise.</param>
    /// <returns>The bands.</returns>
    public LodBands AtLevel(int level, int windowLevel)
    {
        var wide = Math.Max(level, windowLevel);
        if (wide == 0)
        {
            return this;
        }

        if (Count == 0)
        {
            // Without a band of its own at this level, a profile has nothing held back: every change was sent, whatever it held back before.
            return level == 0 ? this : new LodBands(1, ImplicitBeyond, 1d, 1d, Scale(1, level), 1, 1, Scale(1, wide), 1, 1);
        }

        return new LodBands(Count, F1, F2, F3, Scale(N1, level), Scale(N2, level), Scale(N3, level), Scale(N1, wide), Scale(N2, wide), Scale(N3, wide));
    }

    private static int Scale(int n, int level) => Math.Min(n << level, MaxPeriod);

    /// <summary>The innermost band's period — the fold's phase; 0 without bands.</summary>
    public int MinEvery => Count == 0 ? 0 : N1;

    /// <summary>The outermost band's period — the fold's window; 0 without bands.</summary>
    public int MaxEvery => Count switch
    {
        0 => 0,
        1 => N1,
        2 => N2,
        _ => N3,
    };
}
