using JetBrains.Annotations;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Typhon.Engine;

[PublicAPI]
[ExcludeFromCodeCoverage]
public static class MathExtensions
{
    #region Constants

    private static readonly CultureInfo DefaultCulture = new("en-us");

    #endregion

    #region Public APIs

    #region Methods

    public static string Bandwidth(int size, double elapsed) => string.Create(DefaultCulture, $"{(size / elapsed).FriendlySize()}/sec");

    public static string Bandwidth(long size, double elapsed) => string.Create(DefaultCulture, $"{(size / elapsed).FriendlySize()}/sec");

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

    public static bool IsPowerOf2(this int x) => (x & (x - 1)) == 0;
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

    public static double TicksToSeconds(this long ticks) => ((double)ticks / TimeSpan.TicksPerSecond);

    public static double TotalSeconds(this int ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;
    public static double TotalSeconds(this long ticks) => TimeSpan.FromTicks(ticks).TotalSeconds;

    #endregion

    #endregion
}
