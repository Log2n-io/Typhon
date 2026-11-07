using JetBrains.Annotations;
using System;
using System.Collections.Generic;

namespace Typhon.Engine;

/// <summary>
/// Declares a Sphere's distance bands (09 § 9): beyond a fraction of the radius, an entity's updates are sent every N ticks instead of every tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a band defers and what it never does.</b> An update to an entity held in a band — before and after the frame — is sent on the entity's flush
/// tick for that band, <c>(netId + tick) mod N = 0</c>, as the groups it changed in the last N ticks. Enters and leaves are never deferred, and an entity
/// that comes inward across a band's boundary gets its whole state at once.
/// </para>
/// <para>
/// <b>N is 1, 2, 4 or 8</b>, never smaller than the band inside it's: the flush ticks of a band are then flush ticks of every band inside it, which is
/// what lets one fold serve them all. At most three bands, their boundaries rising in (0, 1) — fractions of the radius the session is gathered at.
/// </para>
/// <para>
/// <b>A band can also coarsen motion</b> (#1615): <c>tolerance: 4</c> sends its sessions a motion segment only when their own extrapolation is off by more
/// than four times the archetype's tolerance — a far entity is not re-sent on the 20 cm error a near one is. Each distinct tolerance is one more segment
/// the engine keeps per moving entity (at most three across every profile), encoded once and shared by every session that sees the entity in such a band.
/// A band's period and tolerance both grow outward, and at least one of them grows; a period of 1 is a band that only coarsens motion.
/// </para>
/// </remarks>
[PublicAPI]
public sealed class BandBuilder
{
    /// <summary>The most bands a Sphere may declare.</summary>
    public const int MaxBands = 3;

    private readonly List<DistanceBand> _bands = [];

    internal BandBuilder()
    {
    }

    /// <summary>The bands declared, innermost first.</summary>
    internal IReadOnlyList<DistanceBand> Bands => _bands;

    /// <summary>
    /// Beyond <paramref name="beyond"/> of the radius, updates are sent every <paramref name="n"/> ticks, and motion within <paramref name="tolerance"/>
    /// times the archetype's tolerance.
    /// </summary>
    /// <param name="n">1, 2, 4 or 8, at least the band inside it's.</param>
    /// <param name="beyond">The band's inner boundary, a fraction of the radius in (0, 1), beyond the band inside it's.</param>
    /// <param name="tolerance">
    /// The motion tolerance's multiplier in this band, 1 to 64, at least the band inside it's: 1 keeps the archetype's own. Above 1 the band's sessions are
    /// sent a segment only when their extrapolation is off by more than that many tolerances.
    /// </param>
    /// <returns>This builder.</returns>
    public BandBuilder Every(int n, double beyond, double tolerance = 1d)
    {
        if (n is not (1 or 2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "A band's period is 1, 2, 4 or 8 ticks: nested phases need powers of two within the push log.");
        }

        if (!double.IsFinite(tolerance) || tolerance < 1 || tolerance > MaxTolerance)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance,
                $"A band's motion tolerance is a multiplier of the archetype's, from 1 (its own) to {MaxTolerance}.");
        }

        if (n == 1 && tolerance == 1)
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "A band every tick at the archetype's own tolerance changes nothing: give it a period or a tolerance.");
        }

        if (!double.IsFinite(beyond) || beyond <= 0 || beyond >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(beyond), beyond, "A band starts at a fraction of the radius in (0, 1).");
        }

        if (_bands.Count == MaxBands)
        {
            throw new InvalidOperationException($"A Sphere declares at most {MaxBands} bands: each boundary costs a crescent sweep per anchor move.");
        }

        if (_bands.Count > 0)
        {
            var inner = _bands[^1];
            if (beyond <= inner.Beyond)
            {
                throw new ArgumentOutOfRangeException(nameof(beyond), beyond,
                    $"Bands go outward: this one's boundary ({beyond}) must be beyond the one inside it ({inner.Beyond}).");
            }

            if (n < inner.Every || tolerance < inner.Tolerance || (n == inner.Every && tolerance == inner.Tolerance))
            {
                throw new ArgumentOutOfRangeException(n < inner.Every ? nameof(n) : nameof(tolerance), n < inner.Every ? n : tolerance,
                    $"Bands go outward and coarser: this one (every {n}, tolerance ×{tolerance}) must keep or grow both the period and the tolerance of the one "
                    + $"inside it (every {inner.Every}, ×{inner.Tolerance}), and grow at least one.");
            }
        }

        _bands.Add(new DistanceBand(n, beyond, tolerance));
        return this;
    }

    /// <summary>The largest band tolerance multiplier.</summary>
    public const double MaxTolerance = 64;
}

/// <summary>
/// One distance band: beyond <see cref="Beyond"/> of the radius, updates every <see cref="Every"/> ticks (09 § 9) and motion within <see cref="Tolerance"/>
/// times the archetype's tolerance (#1615).
/// </summary>
/// <param name="Every">The period, in ticks: 1, 2, 4 or 8.</param>
/// <param name="Beyond">The inner boundary, a fraction of the radius.</param>
/// <param name="Tolerance">The motion tolerance's multiplier, 1 (the archetype's own) to <see cref="BandBuilder.MaxTolerance"/>.</param>
[PublicAPI]
public readonly record struct DistanceBand(int Every, double Beyond, double Tolerance = 1d);
