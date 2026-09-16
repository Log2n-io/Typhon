using System;
using System.Numerics;
using System.Threading;

namespace SwgTatooine;

/// <summary>
/// <c>--work-probe</c>: what an interest query does, counted rather than timed, per queried archetype.
/// </summary>
/// <remarks>
/// For a sample of players, each of the four awareness queries is replayed through a mirror of <c>AabbClusterEnumerator</c>'s linear path — the path
/// every query takes while no cell is promoted to a tree, which is the engine default — and its work is counted: cell halves visited, clusters the
/// broadphase scans, clusters whose bound overlaps the query box and are opened, entities the narrowphase then tests, and the distinct pages those
/// clusters live on. The hit count is the real query's. The counting runs inside <c>Awareness</c>, so a run with the probe on is a count, not a timing.
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>One player in this many is sampled.</summary>
    private const int WorkProbeSampleEvery = 16;

    /// <summary>
    /// Counters per queried archetype: queries, cell halves, clusters scanned, clusters opened, entities tested, hits, distinct pages, and the halves and
    /// clusters scanned that only the overhang widening reached.
    /// </summary>
    private const int WorkProbeCounters = 10;

    private static readonly string[] WorkProbeNames = ["WorldObject", "Creature", "CityNpc", "Player"];
    private readonly long[] _work = new long[WorkProbeCounters * 4];

    private ArchetypeClusterState StateOf<TArch>() where TArch : Archetype<TArch>, new()
        => Dbe._archetypeStates[Archetype<TArch>.Metadata.ArchetypeId]?.ClusterState;

    /// <summary>Count one query's work into <paramref name="work"/>. Runs inside a system body, so RT-01 supplies the epoch scope.</summary>
    private unsafe void ProbeWork(int target, ArchetypeClusterState cs, in BSphere2F sphere, long hits, Span<long> work)
    {
        var perCell = cs?.PerCellIndex;
        if (perCell == null)
        {
            return;
        }

        var grid = Dbe.SpatialGrid;
        double minX = sphere.CenterX - sphere.Radius, minY = sphere.CenterY - sphere.Radius;
        double maxX = sphere.CenterX + sphere.Radius, maxY = sphere.CenterY + sphere.Radius;

        // The engine grows the cell range by ClusterReach (SQ-01): a cluster is filed by its entities' centres, so its box can reach that far into the
        // next cell. Mirrored here, and the halves only that growth reaches are counted on their own. The few outliers the engine visits by name
        // (EscapedClusters) are not mirrored: at most 16, and only where a query overlaps one.
        double overhang = Volatile.Read(ref cs.ClusterReach);
        grid.WorldToCellRange(minX - overhang, minY - overhang, 0d, maxX + overhang, maxY + overhang, 0d,
            out var x0, out var y0, out _, out var x1, out var y1, out _);
        grid.WorldToCellRange(minX, minY, 0d, maxX, maxY, 0d, out var ownX0, out var ownY0, out _, out var ownX1, out var ownY1, out _);

        long halves = 0, scanned = 0, opened = 0, tested = 0, widenedHalves = 0, widenedScanned = 0;
        Span<int> pages = stackalloc int[1024];
        var pageCount = 0;
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    if (!grid.TryGetCellKey(x, y, grid.FlatPlaneZ, out var cellKey) || cellKey >= perCell.Length || perCell[cellKey] == null)
                    {
                        continue;
                    }

                    grid.CellOrigin(cellKey, out var ox, out var oy, out _);
                    var qMinX = ClusterSpatialAabb.ToCellRelativeMin(minX, ox);
                    var qMinY = ClusterSpatialAabb.ToCellRelativeMin(minY, oy);
                    var qMaxX = ClusterSpatialAabb.ToCellRelativeMax(maxX, ox);
                    var qMaxY = ClusterSpatialAabb.ToCellRelativeMax(maxY, oy);
                    for (var half = 0; half < 2; half++)
                    {
                        var index = perCell[cellKey].ReadIndex(half == 1);
                        if (index == null || index.ClusterCount == 0)
                        {
                            continue;
                        }

                        halves++;
                        scanned += index.ClusterCount;
                        if (x < ownX0 || x > ownX1 || y < ownY0 || y > ownY1)
                        {
                            widenedHalves++;
                            widenedScanned += index.ClusterCount;
                        }

                        for (var i = 0; i < index.ClusterCount; i++)
                        {
                            if (index.MaxX[i] < qMinX || index.MinX[i] > qMaxX || index.MaxY[i] < qMinY || index.MinY[i] > qMaxY)
                            {
                                continue;
                            }

                            opened++;
                            var chunkId = index.ClusterIds[i];
                            tested += BitOperations.PopCount(*(ulong*)accessor.GetChunkAddress(chunkId));
                            var page = cs.ClusterSegment.GetChunkLocation(chunkId).segmentIndex;
                            if (pageCount < pages.Length && pages[..pageCount].IndexOf(page) < 0)
                            {
                                pages[pageCount++] = page;
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        var b = target * WorkProbeCounters;
        work[b] += 1;
        work[b + 1] += halves;
        work[b + 2] += scanned;
        work[b + 3] += opened;
        work[b + 4] += tested;
        work[b + 5] += hits;
        work[b + 6] += pageCount;
        work[b + 7] += widenedHalves;
        work[b + 8] += widenedScanned;
        work[b + 9] += (long)(overhang * 1000d);   // the reach this query walked with, in mm: it is recomputed at every fence, so it is averaged per query

    }

    private void FoldWork(ReadOnlySpan<long> work)
    {
        for (var k = 0; k < work.Length; k++)
        {
            if (work[k] != 0)
            {
                Interlocked.Add(ref _work[k], work[k]);
            }
        }
    }

    /// <summary>The probe's per-query means, per queried archetype.</summary>
    public void PrintWorkProbe()
    {
        if (!_config.WorkProbe)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  interest-query work per query, 1 player in {WorkProbeSampleEvery} sampled after warm-up; halves+ and scanned+ are the part "
            + "of halves and scanned that only the overhang widening reached");
        Console.WriteLine($"  {"target",-12} {"queries",10} {"halves",7} {"scanned",9} {"opened",8} {"tested",9} {"hits",8} {"tested/hit",10} "
            + $"{"opened/hit",10} {"pages",7} {"overhang",9} {"halves+",8} {"scanned+",9}");
        ArchetypeClusterState[] states = [StateOf<WorldObject>(), StateOf<Creature>(), StateOf<CityNpc>(), StateOf<Player>()];
        for (var t = 0; t < WorkProbeNames.Length; t++)
        {
            var b = t * WorkProbeCounters;
            var q = _work[b];
            if (q == 0)
            {
                continue;
            }

            var n = (double)q;
            var hits = _work[b + 5] / n;

            // Mean reach per sampled query, not the end-of-run value: the reach can fall at any fence. Hits a query found through a named outlier are in
            // `hits` but not in `opened` / `tested`, which mirror the walk only — at most 16 clusters per archetype, and only where a query overlaps one.
            var overhang = _work[b + 9] / n / 1000d;
            Console.WriteLine($"  {WorkProbeNames[t],-12} {q,10:N0} {_work[b + 1] / n,7:F2} {_work[b + 2] / n,9:F1} {_work[b + 3] / n,8:F1} "
                + $"{_work[b + 4] / n,9:F1} {hits,8:F1} {(hits == 0 ? 0 : _work[b + 4] / n / hits),10:F2} {(hits == 0 ? 0 : _work[b + 3] / n / hits),10:F3} "
                + $"{_work[b + 6] / n,7:F1} {overhang,9:F1} {_work[b + 7] / n,8:F2} {_work[b + 8] / n,9:F1}");
        }
    }
}
