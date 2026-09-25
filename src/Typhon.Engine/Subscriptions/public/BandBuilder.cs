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
/// <b>N is 2, 4 or 8</b>, each band's larger than the one inside it: the flush ticks of a band are then flush ticks of every band inside it, which is
/// what lets one fold serve them all. At most three bands, their boundaries rising in (0, 1) — fractions of the radius the session is gathered at.
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
    /// Beyond <paramref name="beyond"/> of the radius, updates are sent every <paramref name="n"/> ticks.
    /// </summary>
    /// <param name="n">2, 4 or 8, larger than the band inside it's.</param>
    /// <param name="beyond">The band's inner boundary, a fraction of the radius in (0, 1), beyond the band inside it's.</param>
    /// <returns>This builder.</returns>
    public BandBuilder Every(int n, double beyond)
    {
        if (n is not (2 or 4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(n), n, "A band's period is 2, 4 or 8 ticks: nested phases need powers of two within the push log.");
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
            if (n <= inner.Every || beyond <= inner.Beyond)
            {
                throw new ArgumentOutOfRangeException(n <= inner.Every ? nameof(n) : nameof(beyond), n <= inner.Every ? n : beyond,
                    $"Bands go outward and slower: this one ({n} beyond {beyond}) must have a larger period and boundary than the one inside it "
                    + $"({inner.Every} beyond {inner.Beyond}).");
            }
        }

        _bands.Add(new DistanceBand(n, beyond));
        return this;
    }
}

/// <summary>One distance band: beyond <see cref="Beyond"/> of the radius, updates every <see cref="Every"/> ticks (09 § 9).</summary>
/// <param name="Every">The period, in ticks: 2, 4 or 8.</param>
/// <param name="Beyond">The inner boundary, a fraction of the radius.</param>
[PublicAPI]
public readonly record struct DistanceBand(int Every, double Beyond);
