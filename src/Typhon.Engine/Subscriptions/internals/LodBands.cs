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

    public LodBands(IReadOnlyList<DistanceBand> bands)
    {
        Count = bands.Count;
        F1 = Count > 0 ? bands[0].Beyond : 1d;
        N1 = Count > 0 ? bands[0].Every : 1;
        F2 = Count > 1 ? bands[1].Beyond : 1d;
        N2 = Count > 1 ? bands[1].Every : 1;
        F3 = Count > 2 ? bands[2].Beyond : 1d;
        N3 = Count > 2 ? bands[2].Every : 1;
    }

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
