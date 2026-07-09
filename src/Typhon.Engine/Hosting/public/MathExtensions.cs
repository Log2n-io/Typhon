using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Typhon.Engine;

/// <summary>
/// Formatting and small numeric helpers — human-friendly renderings of sizes, counts, durations, and bandwidth, plus a few power-of-two utilities. Formatting
/// uses a fixed <c>en-us</c> culture so output is stable regardless of the host locale.
/// </summary>
[PublicAPI]
[ExcludeFromCodeCoverage]
public static class MathExtensions
{
    #region Constants

    private static readonly CultureInfo DefaultCulture = new("en-us");

    #endregion

    #region Public APIs

    #region Methods

    /// <summary>Formats a throughput as a human-readable rate — <paramref name="size"/> divided by <paramref name="elapsed"/> seconds, scaled 1024-based with
    /// bit-style units, e.g. <c>1.5Mb/sec</c>.</summary>
    /// <param name="size">Amount transferred (bytes).</param>
    /// <param name="elapsed">Elapsed time, in seconds.</param>
    /// <returns>The rate string, in the fixed <c>en-us</c> culture.</returns>
    public static string Bandwidth(int size, double elapsed) => string.Create(DefaultCulture, $"{(size / elapsed).FriendlySize()}/sec");

    /// <summary>Formats a throughput as a human-readable rate — <paramref name="size"/> divided by <paramref name="elapsed"/> seconds, scaled 1024-based with
    /// bit-style units, e.g. <c>1.5Mb/sec</c>.</summary>
    /// <param name="size">Amount transferred (bytes).</param>
    /// <param name="elapsed">Elapsed time, in seconds.</param>
    /// <returns>The rate string, in the fixed <c>en-us</c> culture.</returns>
    public static string Bandwidth(long size, double elapsed) => string.Create(DefaultCulture, $"{(size / elapsed).FriendlySize()}/sec");

    /// <summary>Formats a byte/element count with a 1024-based scale suffix (none, <c>K</c>, <c>M</c>, <c>B</c>), e.g. <c>1.5M</c>.</summary>
    /// <param name="val">The value to format.</param>
    /// <returns>The formatted string, in the fixed <c>en-us</c> culture.</returns>
    public static string FriendlySize(this long val)
    {
        var scalesF = new[] { "", "K", "M", "B" };
        var f = (double)val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1024)
            {
                break;
            }

            f /= 1024;
        }
        return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
    }

    /// <summary>Formats a byte/element count with a 1024-based scale suffix (none, <c>K</c>, <c>M</c>, <c>B</c>), e.g. <c>1.5M</c>.</summary>
    /// <param name="val">The value to format.</param>
    /// <returns>The formatted string, in the fixed <c>en-us</c> culture.</returns>
    public static string FriendlySize(this int val)
    {
        var scalesF = new[] { "", "K", "M", "B" };
        var f = (double)val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1024)
            {
                break;
            }

            f /= 1024;
        }
        return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
    }

    /// <summary>Formats a value with a 1024-based scale using bit-style units (<c>b</c>, <c>Kb</c>, <c>Mb</c>, <c>Gb</c>).</summary>
    /// <param name="val">The value to format.</param>
    /// <returns>The formatted string, in the fixed <c>en-us</c> culture.</returns>
    public static string FriendlySize(this double val)
    {
        var scalesF = new[] { "b", "Kb", "Mb", "Gb" };
        var f = val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1024)
            {
                break;
            }

            f /= 1024;
        }
        return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
    }

    /// <summary>Formats a count with a 1000-based scale suffix (none, <c>K</c>, <c>M</c>, <c>B</c>), e.g. <c>1.5M</c> for 1,500,000.</summary>
    /// <param name="val">The value to format.</param>
    /// <returns>The formatted string, in the fixed <c>en-us</c> culture.</returns>
    public static string FriendlyAmount(this int val)
    {
        var scalesF = new[] { "", "K", "M", "B" };
        var f = (double)val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1000)
            {
                break;
            }

            f /= 1000;
        }
        return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
    }

    /// <summary>Formats a count with a 1000-based scale suffix (none, <c>K</c>, <c>M</c>, <c>G</c>).</summary>
    /// <param name="val">The value to format.</param>
    /// <returns>The formatted string, in the fixed <c>en-us</c> culture.</returns>
    public static string FriendlyAmount(this double val)
    {
        var scalesF = new[] { "", "K", "M", "G" };
        var f = val;
        var iF = 0;
        for (; iF < 3; iF++)
        {
            if (f < 1000)
            {
                break;
            }

            f /= 1000;
        }
        return string.Create(DefaultCulture, $"{f:0.###}{scalesF[iF]}");
    }

    /// <summary>
    /// Formats a duration (in seconds) with the largest fitting unit — <c>sec</c>, <c>ms</c>, <c>µs</c>, or <c>ns</c>. When <paramref name="displayRate"/> is
    /// <c>true</c>, appends the reciprocal as a per-second rate, e.g. <c>1.5µs (666.667K/sec)</c>.
    /// </summary>
    /// <param name="val">The duration, in seconds.</param>
    /// <param name="displayRate">When <c>true</c>, also append the <c>1/val</c> rate in parentheses.</param>
    /// <returns>The formatted string, in the fixed <c>en-us</c> culture.</returns>
    public static string FriendlyTime(this double val, bool displayRate = true)
    {
        var scalesE = new[] { "sec", "ms", "µs", "ns" };
        var e = val;
        var iE = 0;
        for (; iE < 3; iE++)
        {
            if (Math.Abs(e) > 1)
            {
                break;
            }

            e *= 1000;
        }

        if (displayRate)
        {
            var scalesF = new[] { "", "K", "M", "B" };
            var f = 1 / val;
            var iF = 0;
            for (; iF < 3; iF++)
            {
                if (f < 1000)
                {
                    break;
                }

                f /= 1000;
            }
            return string.Create(DefaultCulture, $"{e:0.###}{scalesE[iE]} ({f:0.###}{scalesF[iF]}/sec)");
        }
        else
        {
            return string.Create(DefaultCulture, $"{e:0.###}{scalesE[iE]}");
        }
    }

    /// <summary>Whether <paramref name="x"/> is a power of two. Returns <c>true</c> for <c>0</c>.</summary>
    /// <param name="x">The value to test.</param>
    /// <returns><c>true</c> if <paramref name="x"/> has at most one bit set.</returns>
    public static bool IsPowerOf2(this int x) => (x & (x - 1)) == 0;
    /// <summary>Whether <paramref name="x"/> is a power of two. Returns <c>true</c> for <c>0</c>.</summary>
    /// <param name="x">The value to test.</param>
    /// <returns><c>true</c> if <paramref name="x"/> has at most one bit set.</returns>
    public static bool IsPowerOf2(this long x) => (x & (x - 1)) == 0;

    /// <summary>
    /// Return the next power of 2 of the given value
    /// </summary>
    /// <param name="v">The value</param>
    /// <returns>The next power of 2</returns>
    /// <remarks>
    /// If the given value is already a power of 2, this method will return the next one.
    /// </remarks>
    public static int NextPowerOf2(this int v)
    {
        v |= v >> 1;         v |= v >> 2;
        v |= v >> 4;         v |= v >> 8;
        v |= v >> 16;
        v++;
        return v;
    }

    /// <summary>Converts <see cref="TimeSpan"/> <paramref name="ticks"/> to seconds.</summary>
    /// <param name="ticks">A tick count (100 ns units).</param>
    /// <returns>The equivalent number of seconds.</returns>
    public static double TicksToSeconds(this long ticks) => ((double)ticks / TimeSpan.TicksPerSecond);

    /// <summary>Converts <see cref="TimeSpan"/> <paramref name="ticks"/> to total seconds via <see cref="TimeSpan.FromTicks"/>.</summary>
    /// <param name="ticks">A tick count (100 ns units).</param>
    /// <returns>The equivalent number of seconds.</returns>
    public static double TotalSeconds(this int ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;
    /// <summary>Converts <see cref="TimeSpan"/> <paramref name="ticks"/> to total seconds via <see cref="TimeSpan.FromTicks"/>.</summary>
    /// <param name="ticks">A tick count (100 ns units).</param>
    /// <returns>The equivalent number of seconds.</returns>
    public static double TotalSeconds(this long ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;

    #endregion

    #endregion
}
