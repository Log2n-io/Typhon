using System;
using System.Collections.Generic;
using Typhon.Engine;
using Typhon.Workbench.Dtos.Storage;

namespace Typhon.Workbench.Storage;

/// <summary>
/// Builds the Hilbert aggregate pyramid (§5.3) from the coarse page-type array. Each quadtree node at level
/// <c>L</c> covers a contiguous run of <c>4^(order−L)</c> pages — the Hilbert curve is parameterised so that
/// page index equals curve position, which makes every quadtree node a contiguous page-index range.
/// </summary>
internal static class StorageMapPyramid
{
    /// <summary>Upper bound on <see cref="StoragePageType"/> ordinals — sizes the per-node tally buffer.</summary>
    private const int PageTypeCount = 16;

    /// <summary>
    /// Reduces <paramref name="pageType"/> into the top <paramref name="maxLevels"/> pyramid levels (0-based,
    /// capped at <paramref name="hilbertOrder"/>). Each node reports its used-page count and dominant type.
    /// </summary>
    public static StorageOverviewDto BuildOverview(StoragePageType[] pageType, int hilbertOrder, int maxLevels)
    {
        var topLevel = Math.Min(hilbertOrder, maxLevels - 1);
        var levels = new List<StoragePyramidLevelDto>(topLevel + 1);
        Span<int> tally = stackalloc int[PageTypeCount];

        for (var level = 0; level <= topLevel; level++)
        {
            var nodeCount = 1 << (2 * level);
            var pagesPerNode = 1L << (2 * (hilbertOrder - level));
            var dominant = new byte[nodeCount];
            var used = new int[nodeCount];

            for (var k = 0; k < nodeCount; k++)
            {
                var start = k * pagesPerNode;
                if (start >= pageType.Length)
                {
                    // Hilbert tail — node covers no real pages.
                    dominant[k] = (byte)StoragePageType.Free;
                    used[k] = 0;
                    continue;
                }

                tally.Clear();
                var end = (int)Math.Min(start + pagesPerNode, pageType.Length);
                var usedCount = 0;
                for (var p = (int)start; p < end; p++)
                {
                    var t = pageType[p];
                    tally[(int)t]++;
                    if (t != StoragePageType.Free)
                    {
                        usedCount++;
                    }
                }
                dominant[k] = (byte)DominantType(tally);
                used[k] = usedCount;
            }

            levels.Add(new StoragePyramidLevelDto(level, nodeCount, Convert.ToBase64String(dominant), used));
        }

        return new StorageOverviewDto(hilbertOrder, levels.ToArray());
    }

    /// <summary>The dominant non-free page type in a node; an all-free or empty node reports <c>Free</c>.</summary>
    private static StoragePageType DominantType(ReadOnlySpan<int> tally)
    {
        var bestType = StoragePageType.Free;
        var bestCount = 0;
        for (var i = 0; i < tally.Length; i++)
        {
            if (i == (int)StoragePageType.Free)
            {
                continue;
            }
            if (tally[i] > bestCount)
            {
                bestCount = tally[i];
                bestType = (StoragePageType)i;
            }
        }
        return bestCount > 0 ? bestType : StoragePageType.Free;
    }
}
