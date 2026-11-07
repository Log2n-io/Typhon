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

    /// <summary>
    /// Each band's motion head (#1615): 0 for the archetype's own tolerance — the base segment — or 1 … 3, the index of its tolerance in
    /// <see cref="BandTolerances"/>. A level never changes it: the per-session LOD level stretches periods only.
    /// </summary>
    public readonly byte H1, H2, H3;

    /// <summary>Where a profile with no bands starts its implicit one at a level above zero (09 § 10): half the radius.</summary>
    public const double ImplicitBeyond = 0.5;

    /// <summary>The longest period a level may reach: a flush's history must stay in the push log.</summary>
    public const int MaxPeriod = 8;

    /// <param name="bands">The declared bands, innermost first.</param>
    /// <param name="tolerances">The runtime's distinct band tolerances (<see cref="BandTolerances.Of"/>), which a band's tolerance indexes into.</param>
    public LodBands(IReadOnlyList<DistanceBand> bands, double[] tolerances = null)
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
        H1 = Count > 0 ? BandTolerances.HeadOf(tolerances, bands[0].Tolerance) : (byte)0;
        H2 = Count > 1 ? BandTolerances.HeadOf(tolerances, bands[1].Tolerance) : (byte)0;
        H3 = Count > 2 ? BandTolerances.HeadOf(tolerances, bands[2].Tolerance) : (byte)0;
    }

    private LodBands(int count, double f1, double f2, double f3, int n1, int n2, int n3, int w1, int w2, int w3, byte h1 = 0, byte h2 = 0, byte h3 = 0)
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
        H1 = h1;
        H2 = h2;
        H3 = h3;
    }

    /// <summary>Whether any band coarsens motion: a session gathered with these bands may be sent band heads (#1615).</summary>
    public bool HasHeads => (H1 | H2 | H3) != 0;

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

        return new LodBands(Count, F1, F2, F3, Scale(N1, level), Scale(N2, level), Scale(N3, level), Scale(N1, wide), Scale(N2, wide), Scale(N3, wide), H1, H2,
            H3);
    }

    private static int Scale(int n, int level) => Math.Min(n << level, MaxPeriod);

    /// <summary>
    /// The innermost band's period above one — the fold's phase; 0 without such a band. A period-1 band only coarsens motion (#1615): it holds nothing
    /// back, so it needs no flush.
    /// </summary>
    public int MinEvery => Count > 0 && N1 > 1 ? N1 : Count > 1 && N2 > 1 ? N2 : Count > 2 && N3 > 1 ? N3 : 0;

    /// <summary>The outermost band's period — the fold's window; 0 without bands.</summary>
    public int MaxEvery => Count switch
    {
        0 => 0,
        1 => N1,
        2 => N2,
        _ => N3,
    };
}

/// <summary>
/// The runtime's distinct band tolerances (#1615): every multiplier above one that a Sphere band declares, across every profile and variant, ascending. Each is
/// one motion head every moving entity keeps; a band's head is its tolerance's index here, plus one.
/// </summary>
internal static class BandTolerances
{
    /// <summary>The most distinct tolerances a runtime may declare: each is a segment kept per moving entity, in its hot entry.</summary>
    public const int MaxHeads = 3;

    /// <summary>The distinct band tolerances <paramref name="registry"/> declares, ascending; empty when none coarsens motion.</summary>
    /// <param name="registry">The declarations.</param>
    /// <returns>The tolerances.</returns>
    /// <exception cref="InvalidOperationException">More than <see cref="MaxHeads"/> distinct tolerances are declared.</exception>
    public static double[] Of(SubscriptionsRegistry registry)
    {
        var found = new List<double>();
        foreach (var profile in registry.Profiles)
        {
            Collect(profile, found);
            foreach (var variant in profile.Variants.Values)
            {
                Collect(variant, found);
            }
        }

        if (found.Count > MaxHeads)
        {
            throw new InvalidOperationException(
                $"The profiles declare {found.Count} distinct band tolerances ({string.Join(", ", found)}); at most {MaxHeads}: each is a motion segment kept "
                + "per moving entity. Share tolerances between profiles.");
        }

        found.Sort();
        return found.ToArray();
    }

    /// <summary>The head of a band of tolerance <paramref name="tolerance"/>: 0 for the archetype's own, else its index in <paramref name="tolerances"/> plus one.</summary>
    public static byte HeadOf(double[] tolerances, double tolerance)
    {
        if (tolerance <= 1 || tolerances == null)
        {
            return 0;
        }

        var at = Array.IndexOf(tolerances, tolerance);
        return at < 0 ? (byte)0 : (byte)(at + 1);
    }

    private static void Collect(ProfileDeclaration profile, List<double> found)
    {
        foreach (var observer in profile.Observers)
        {
            foreach (var band in observer.Bands)
            {
                if (band.Tolerance > 1 && !found.Contains(band.Tolerance))
                {
                    found.Add(band.Tolerance);
                }
            }
        }
    }
}
