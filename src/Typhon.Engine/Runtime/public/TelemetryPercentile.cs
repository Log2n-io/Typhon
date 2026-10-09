using System;
using JetBrains.Annotations;

namespace Typhon.Engine;

/// <summary>
/// The engine's one definition of a percentile over telemetry samples: <b>nearest-rank</b>, no interpolation.
/// </summary>
/// <remarks>
/// <para>
/// It lives in one place because two paths report the same numbers to different audiences — the <c>STATS</c> wire block to game clients, and
/// <see cref="TyphonRuntime.ReadStats"/> to an HTTP endpoint, a CLI verb or a log line. Two implementations of "p99" that disagree by a rank produce two
/// plausible-looking figures for one tick, and whichever a reader happens to see becomes the one they trust. A shared function makes them equal by
/// construction rather than by review.
/// </para>
/// <para>
/// <b>Nearest-rank, deliberately.</b> An interpolated percentile invents a value between two measured ticks, which for a latency figure means reporting a
/// duration no tick had. At the window sizes here — one second, so 50 samples at 50 Hz — interpolation also moves the answer more than the sampling noise it
/// is meant to smooth.
/// </para>
/// </remarks>
[PublicAPI]
public static class TelemetryPercentile
{
    /// <summary>
    /// The nearest-rank percentile of the first <paramref name="count"/> samples.
    /// </summary>
    /// <param name="samples">The samples. <b>Reordered in place</b> — the caller must not rely on their order afterwards.</param>
    /// <param name="count">How many leading entries of <paramref name="samples"/> are live samples.</param>
    /// <param name="q">The percentile, in [0, 1]. 0.5 is the median.</param>
    /// <returns>The sample at the nearest rank, or <c>0</c> when there are no samples.</returns>
    /// <remarks>
    /// Sorting in place is what keeps this allocation-free on the <c>STATS</c> path, which reuses one buffer sized at construction and must not allocate
    /// after it. Zero for an empty window is a reading, not a sentinel: a runtime that has not ticked has no duration, and a caller that needs to tell the
    /// two apart reads the sample count it passed in.
    /// </remarks>
    public static double NearestRank(double[] samples, int count, double q)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (count <= 0)
        {
            return 0;
        }

        count = Math.Min(count, samples.Length);
        Array.Sort(samples, 0, count);
        var rank = (int)Math.Ceiling(q * count) - 1;
        return samples[Math.Clamp(rank, 0, count - 1)];
    }
}
