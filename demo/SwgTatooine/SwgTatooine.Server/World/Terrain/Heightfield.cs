using System;

namespace SwgTatooine.World.Terrain;

/// <summary>
/// The baked heightfield: the one source of truth for ground height on this server, and the same field the browser client
/// bakes for itself.
/// </summary>
/// <remarks>
/// <para>
/// A transcription of <c>demo/SwgTatooine.Client/src/terrain/heightfield.ts</c>. <b>Everything interpolates these posts;
/// nothing re-evaluates the layer tree.</b> That is what keeps the altitude this server writes on a placement and the
/// ground the client draws under it in agreement: they are not two implementations of one function, they are two readers
/// of one array — built from one spec and held to one golden.
/// </para>
/// </remarks>
public sealed class Heightfield
{
    /// <summary>
    /// Posts per side of the baked field. 4096 × 4096 f32 is <b>67.1 MB</b>.
    /// </summary>
    /// <remarks>
    /// It was 2048 (8 m posts), and the reason it is not is measured rather than aesthetic: a hill whose features are
    /// narrower than two posts collapses to a single raised post, and bilinear over one raised post is exactly a
    /// four-sided pyramid. Resolution alone did not fix that — the layers simply aliased a scale down — so the octave
    /// floor in <see cref="TatooineTerrain"/> moved at the same time. The field and the tree that bakes into it are one
    /// decision, not two.
    /// </remarks>
    public const int Posts = 4096;

    /// <summary>4 m between posts, from 16 384 m over 4096 posts.</summary>
    public const double PostSpacingM = TatooineData.PlanetEdgeM / Posts;

    /// <summary>Planet coordinate of post 0 on both axes. The last post is therefore at <c>+8188</c>, and beyond it the field clamps.</summary>
    public const double FieldOriginM = -TatooineData.PlanetHalfExtentM;

    /// <summary>Lowest and highest post, filled by <see cref="Measure"/>.</summary>
    public float MinHeightM { get; private set; }

    /// <summary>Highest post, filled by <see cref="Measure"/>.</summary>
    public float MaxHeightM { get; private set; }

    public HeightGrid Grid { get; }

    public Heightfield()
        : this(HeightGrid.Create(Posts, PostSpacingM, FieldOriginM))
    {
    }

    public Heightfield(HeightGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        Grid = grid;
    }

    /// <summary>Ground height in metres at a planet coordinate, bilinear between posts, clamped outside the field.</summary>
    public double HeightAt(double x, double z)
    {
        int posts = Grid.Posts;
        float[] height = Grid.Height;
        int last = posts - 1;
        double fx = Clamp((x - Grid.OriginM) / Grid.SpacingM, 0, last);
        double fz = Clamp((z - Grid.OriginM) / Grid.SpacingM, Grid.RowOffset, Grid.RowOffset + Grid.Rows - 1);
        int x0 = Math.Min((int)Math.Floor(fx), last);
        int z0 = Math.Min((int)Math.Floor(fz), Grid.RowOffset + Grid.Rows - 1);
        int x1 = Math.Min(x0 + 1, last);
        int z1 = Math.Min(z0 + 1, Grid.RowOffset + Grid.Rows - 1);
        double tx = fx - x0;
        double tz = fz - z0;
        int row0 = (z0 - Grid.RowOffset) * posts;
        int row1 = (z1 - Grid.RowOffset) * posts;
        double a = height[row0 + x0];
        double b = height[row0 + x1];
        double c = height[row1 + x0];
        double d = height[row1 + x1];
        double top = a + ((b - a) * tx);
        double bottom = c + ((d - c) * tx);
        return top + ((bottom - top) * tz);
    }

    /// <summary>Ground gradient at a planet coordinate: <paramref name="dhdx"/> and <paramref name="dhdz"/>, both rise over run.</summary>
    /// <remarks>
    /// A central difference over one post spacing rather than the analytic derivative of the bilinear patch, because the
    /// analytic one is discontinuous at every post edge.
    /// </remarks>
    public void GradientAt(double x, double z, out double dhdx, out double dhdz)
    {
        double s = Grid.SpacingM;
        dhdx = (HeightAt(x + s, z) - HeightAt(x - s, z)) / (2 * s);
        dhdz = (HeightAt(x, z + s) - HeightAt(x, z - s)) / (2 * s);
    }

    /// <summary>Slope as rise over run, so 1.0 is 45°.</summary>
    public double SlopeAt(double x, double z)
    {
        GradientAt(x, z, out double dx, out double dz);
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>Recomputes <see cref="MinHeightM"/> and <see cref="MaxHeightM"/>. Call once after a bake.</summary>
    public void Measure()
    {
        float[] height = Grid.Height;
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        for (int i = 0; i < height.Length; i++)
        {
            float h = height[i];
            if (h < min)
            {
                min = h;
            }

            if (h > max)
            {
                max = h;
            }
        }

        // Finite checks rather than a length check: a single NaN post used to leave the minimum at infinity, which is a
        // worse failure than reporting zero because it propagates silently into anything that compares against it.
        MinHeightM = float.IsFinite(min) ? min : 0;
        MaxHeightM = float.IsFinite(max) ? max : 0;
    }

    /// <summary>
    /// Clamps, and maps NaN to <paramref name="lo"/> rather than passing it through.
    /// </summary>
    /// <remarks>
    /// Written this way round deliberately: <c>v &lt; lo ? lo : v &gt; hi ? hi : v</c> returns NaN for NaN, which then
    /// indexes the height array with NaN and produces a NaN altitude.
    /// </remarks>
    private static double Clamp(double v, double lo, double hi) => v > lo ? (v < hi ? v : hi) : lo;
}
