using System;

namespace SwgTatooine.Bots.Telemetry;

/// <summary>
/// Which bots a run traces (bot-telemetry.md): one in a hundred, never fewer than <see cref="Min"/> nor more than <see cref="Max"/>, or the count asked for —
/// every k-th index from 0, so the same bots are traced on every run of the same size.
/// </summary>
/// <remarks>
/// Every k-th rather than the first n: the first indices are the bots that live in the home city, and a sample of them alone would never show a bot
/// travelling between cities.
/// </remarks>
public static class BotTraceSampler
{
    /// <summary>The share of the fleet traced when no count is asked for.</summary>
    public const double Share = 0.01;

    /// <summary>The fewest bots traced: below this a sample says nothing about a fleet.</summary>
    public const int Min = 10;

    /// <summary>The most: each traced bot is a ring of events and a trail the report carries.</summary>
    public const int Max = 50;

    /// <summary>How many bots of <paramref name="population"/> are traced.</summary>
    /// <param name="population">The fleet's size.</param>
    /// <param name="requested">The count asked for (<c>--trace-bots</c>), or 0 for one in a hundred within [<see cref="Min"/>, <see cref="Max"/>].</param>
    public static int Count(int population, int requested)
    {
        if (population <= 0)
        {
            return 0;
        }

        var wanted = requested > 0 ? requested : Math.Clamp((int)Math.Round(population * Share), Min, Max);
        return Math.Min(population, wanted);
    }

    /// <summary>Whether bot <paramref name="index"/> is one of them.</summary>
    public static bool IsTraced(int index, int population, int requested)
    {
        var count = Count(population, requested);
        if (count == 0 || index < 0)
        {
            return false;
        }

        var stride = population / count;
        return index % stride == 0 && index / stride < count;
    }

    /// <summary>The traced indices, ascending.</summary>
    public static int[] Indices(int population, int requested)
    {
        var count = Count(population, requested);
        var indices = new int[count];
        var stride = count == 0 ? 0 : population / count;
        for (var i = 0; i < count; i++)
        {
            indices[i] = i * stride;
        }

        return indices;
    }
}
