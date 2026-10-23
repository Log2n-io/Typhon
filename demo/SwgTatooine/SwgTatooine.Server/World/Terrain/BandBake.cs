using System;
using System.Threading.Tasks;

namespace SwgTatooine.World.Terrain;

/// <summary>
/// Baking the planet in horizontal bands, so the bake uses every core instead of one.
/// </summary>
/// <remarks>
/// <para>
/// The bake is <b>5.1 s measured</b> single-threaded at 4096 posts on a 7950X (the browser client's JavaScript takes
/// 13.2 s for the same work). The demo builds its whole world in 0.8 s, so five seconds of terrain would be the startup,
/// and the work is embarrassingly parallel.
/// </para>
/// <para>
/// <b>Why a band is exact and a tile would not be.</b> Every layer is pointwise in the planet's coordinates <i>except</i>
/// the slope filter, which reads the accumulator one post either side. So a band carrying
/// <see cref="TerrainLayers.HaloRows"/> rows of overlap, that knows the planet's full <see cref="HeightGrid.Posts"/> for
/// its rim and boundary arithmetic, produces the whole-grid heights on its interior bit for bit.
/// </para>
/// <para>
/// The <b>pads</b> are the part that cannot be banded: each levels the ground to the height the landform reached at the
/// site's centre, which may lie in another band. They are applied once, on the assembled field, and cost nothing —
/// fourteen feathered circles against a 16.8 M-post grid.
/// </para>
/// </remarks>
public static class BandBake
{
    /// <summary>
    /// Bands per worker.
    /// </summary>
    /// <remarks>
    /// More than one because the bands are <b>not</b> equal work: a band crossing the mesa belt evaluates two extra
    /// layers over its whole width and one carrying only the global layers does not. Several bands each lets a thread
    /// that drew light ones take another, and the makespan falls back toward the average.
    /// </remarks>
    public const int BandsPerWorker = 3;

    /// <summary>Splits <paramref name="posts"/> rows into <paramref name="bands"/> contiguous ranges, as evenly as they divide.</summary>
    /// <remarks>
    /// The remainder goes to the first bands rather than the last, so no band is more than one row larger than any other.
    /// </remarks>
    public static (int RowOffset, int Rows)[] BandRanges(int posts, int bands)
    {
        int count = Math.Max(1, Math.Min(bands, posts));
        int baseRows = posts / count;
        int extra = posts % count;
        (int RowOffset, int Rows)[] ranges = new (int, int)[count];
        int at = 0;
        for (int i = 0; i < count; i++)
        {
            int rows = baseRows + (i < extra ? 1 : 0);
            ranges[i] = (at, rows);
            at += rows;
        }

        return ranges;
    }

    /// <summary>
    /// Bakes the whole planet across the available cores.
    /// </summary>
    /// <param name="field">The field to fill.</param>
    /// <param name="seed">Overrides <see cref="TatooineTerrain.Seed"/>.</param>
    /// <param name="workers">
    /// Threads to use; defaults to the processor count. There is deliberately no cap tying a band to its halo: every band
    /// recomputes its halo from planet coordinates rather than from its neighbours, so a band smaller than its halo is
    /// wasteful and still correct.
    /// </param>
    public static void Bake(Heightfield field, int seed = TatooineTerrain.Seed, int workers = 0)
    {
        ArgumentNullException.ThrowIfNull(field);
        HeightGrid whole = field.Grid;
        if (whole.Rows != whole.Posts)
        {
            throw new ArgumentException("a banded bake fills a whole planet, not a window", nameof(field));
        }

        TerrainLayer[] layers = TatooineTerrain.LandformLayers(seed);
        int halo = TerrainLayers.HaloRows(layers);
        int threads = workers > 0 ? workers : Environment.ProcessorCount;
        (int RowOffset, int Rows)[] ranges = BandRanges(whole.Posts, Math.Max(1, threads * BandsPerWorker));

        // The widest band a worker can be handed, halo included — the size one reusable weight buffer has to cover.
        int widest = 0;
        foreach ((int RowOffset, int Rows) r in ranges)
        {
            int rows = Math.Min(whole.Posts, r.RowOffset + r.Rows + halo) - Math.Max(0, r.RowOffset - halo);
            widest = Math.Max(widest, rows);
        }

        Parallel.ForEach(
            ranges,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads) },
            // Each band owns its own grid, and each WORKER owns one weight buffer it reuses across the bands it is given.
            // Sharing either across workers would be a data race; allocating the weight buffer per band was neither a race
            // nor free — it put one array per band on the large-object heap, three per worker by BandsPerWorker.
            () => new float[whole.Posts * widest],
            (range, _, weights) =>
            {
                int from = Math.Max(0, range.RowOffset - halo);
                int to = Math.Min(whole.Posts, range.RowOffset + range.Rows + halo);
                HeightGrid band = HeightGrid.Window(whole.Posts, whole.SpacingM, whole.OriginM, from, to - from);
                TerrainLayers.BakeLayers(band, layers, weights);
                Array.Copy(
                    band.Height,
                    (range.RowOffset - from) * whole.Posts,
                    whole.Height,
                    range.RowOffset * whole.Posts,
                    range.Rows * whole.Posts);
                return weights;
            },
            static _ => { });

        // The pads and the scan see the assembled field, and neither is worth a thread.
        TerrainLayers.ApplyLayers(whole, TatooineTerrain.PadLayers(field));
        field.Measure();
    }
}
