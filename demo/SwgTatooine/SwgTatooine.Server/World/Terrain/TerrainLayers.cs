using System;

namespace SwgTatooine.World.Terrain;

/// <summary>
/// A small height-only layer system, in the shape of SWG's terrain engine and at about a third of its scale.
/// </summary>
/// <remarks>
/// <para>
/// A transcription of <c>demo/SwgTatooine.Client/src/terrain/layers.ts</c>. See <see cref="TerrainHash"/> for why the two
/// must agree to the bit rather than to a tolerance.
/// </para>
/// <para>
/// The reason for a layer system rather than a stack of fBm terms is not fidelity to SWG — it is that <b>plain fBm is
/// statistically identical everywhere at every scale</b>, which is exactly the tell between ground a player reads as a
/// place and ground they read as noise. Three mechanisms do that perceptual work and all three are cheap: boundaries
/// confine a layer to a shape, filters apply it only where the terrain already satisfies a predicate, and terracing makes
/// the banded flat-topped mesa silhouette that octave noise cannot produce at any parameter setting.
/// </para>
/// </remarks>
public static class TerrainLayers
{
    /// <summary>
    /// Evaluates a layer tree into a grid, layer by layer over the whole grid, clearing it first.
    /// </summary>
    /// <param name="grid">Receives the heights; cleared first.</param>
    /// <param name="layers">Applied in order. Later layers see the terrain earlier ones made.</param>
    /// <param name="scratch">Reusable weight buffer of <c>posts × rows</c>; supplied by a repeated bake to allocate nothing.</param>
    public static void BakeLayers(HeightGrid grid, ReadOnlySpan<TerrainLayer> layers, float[] scratch = null)
    {
        Array.Clear(grid.Height);
        ApplyLayers(grid, layers, scratch);
    }

    /// <summary>
    /// Applies layers <b>onto</b> what the grid already holds, without clearing it.
    /// </summary>
    /// <remarks>
    /// This exists because one authored layer cannot be written down until the landform is baked: a town's pad has to
    /// level the ground to the height the terrain reached at that spot, which is only known afterwards. So the tree is
    /// applied in two stages, and the second stage's constants are read out of the first stage's result.
    /// </remarks>
    public static void ApplyLayers(HeightGrid grid, ReadOnlySpan<TerrainLayer> layers, float[] scratch = null)
    {
        int n = grid.Posts * grid.Rows;
        float[] weight = scratch != null && scratch.Length >= n ? scratch : new float[n];
        Span<int> range = stackalloc int[4];
        foreach (TerrainLayer layer in layers)
        {
            ApplyLayer(grid, layer, weight, range);
        }
    }

    /// <summary>
    /// Rows of overlap a window needs on each side to reproduce the whole-grid bake exactly.
    /// </summary>
    /// <remarks>
    /// One row per slope-filtered layer, and the reason it is not simply one: a slope filter reads the accumulator one row
    /// either side, so the layer <b>before</b> it has to be correct one row further out again, and so on down the tree.
    /// Getting this wrong does not fail loudly — it leaves a faint seam along each band boundary — which is why it is
    /// derived from the tree rather than written down as a constant.
    /// </remarks>
    public static int HaloRows(ReadOnlySpan<TerrainLayer> layers)
    {
        int slopeFiltered = 0;
        foreach (TerrainLayer layer in layers)
        {
            foreach (TerrainFilter filter in layer.Filters)
            {
                if (filter.Kind == FilterKind.Slope)
                {
                    slopeFiltered++;
                    break;
                }
            }
        }

        return slopeFiltered;
    }

    /// <summary>
    /// The post range a boundary can possibly reach, as <c>[x0, z0, x1, z1]</c> inclusive, clamped to the grid.
    /// </summary>
    /// <remarks>
    /// This is the difference between a bake that takes seconds and one that takes a fraction of one, and the reason is
    /// the pads: fourteen of them, each covering a few thousand posts of a 16.8 M-post grid. Sweeping the whole grid per
    /// layer to find them cost <b>4.1 s measured</b> at 2048 posts, most of it in circle tests always going to return zero.
    /// </remarks>
    private static void BoundaryRange(TerrainBoundary boundary, HeightGrid grid, Span<int> outRange)
    {
        int last = grid.Posts - 1;
        if (boundary == null)
        {
            outRange[0] = 0;
            outRange[1] = 0;
            outRange[2] = last;
            outRange[3] = last;
            return;
        }

        double minX, minZ, maxX, maxZ;
        switch (boundary.Kind)
        {
            case BoundaryKind.Circle:
                minX = boundary.X - boundary.RadiusM;
                maxX = boundary.X + boundary.RadiusM;
                minZ = boundary.Z - boundary.RadiusM;
                maxZ = boundary.Z + boundary.RadiusM;
                break;
            case BoundaryKind.Rect:
                minX = boundary.X - boundary.HalfXM;
                maxX = boundary.X + boundary.HalfXM;
                minZ = boundary.Z - boundary.HalfZM;
                maxZ = boundary.Z + boundary.HalfZM;
                break;
            default:
            {
                // An odd-length or empty points array would make the whole box NaN and silently drop the layer — a
                // malformed region rendering as nothing at all, with no error anywhere. A layer tree is authored, so this
                // is a programming mistake and should say so.
                if (boundary.Points.Length < 2 || boundary.Points.Length % 2 != 0)
                {
                    throw new ArgumentException(
                        $"terrain boundary {boundary.Kind} needs a non-empty even-length points array, got {boundary.Points.Length}",
                        nameof(boundary));
                }

                double pad = boundary.Kind == BoundaryKind.Polyline ? boundary.HalfWidthM : 0;
                minX = double.PositiveInfinity;
                minZ = double.PositiveInfinity;
                maxX = double.NegativeInfinity;
                maxZ = double.NegativeInfinity;
                for (int i = 0; i < boundary.Points.Length; i += 2)
                {
                    minX = Math.Min(minX, boundary.Points[i] - pad);
                    maxX = Math.Max(maxX, boundary.Points[i] + pad);
                    minZ = Math.Min(minZ, boundary.Points[i + 1] - pad);
                    maxZ = Math.Max(maxZ, boundary.Points[i + 1] + pad);
                }

                break;
            }
        }

        outRange[0] = ClampPost((int)Math.Floor((minX - grid.OriginM) / grid.SpacingM), last);
        outRange[1] = ClampPost((int)Math.Floor((minZ - grid.OriginM) / grid.SpacingM), last);
        outRange[2] = ClampPost((int)Math.Ceiling((maxX - grid.OriginM) / grid.SpacingM), last);
        outRange[3] = ClampPost((int)Math.Ceiling((maxZ - grid.OriginM) / grid.SpacingM), last);
    }

    private static int ClampPost(int index, int last) => index < 0 ? 0 : index > last ? last : index;

    /// <summary>Where a layer applies, and how strongly, in <c>[0, 1]</c> per post. Reports whether it applies anywhere at all.</summary>
    private static bool FillWeights(HeightGrid grid, TerrainLayer layer, float[] weight, ReadOnlySpan<int> range)
    {
        int posts = grid.Posts;
        double spacingM = grid.SpacingM;
        double originM = grid.OriginM;
        TerrainFilter[] filters = layer.Filters;
        int first = Math.Max(range[1], grid.RowOffset);
        int lastRow = Math.Min(range[3], grid.RowOffset + grid.Rows - 1);
        bool any = false;
        for (int iz = first; iz <= lastRow; iz++)
        {
            double z = originM + (iz * spacingM);
            int row = (iz - grid.RowOffset) * posts;
            for (int ix = range[0]; ix <= range[2]; ix++)
            {
                double x = originM + (ix * spacingM);
                double w = layer.Boundary == null ? 1 : BoundaryWeight(layer.Boundary, x, z);
                // The filters read the ACCUMULATOR, which this pass does not touch — that is what the two passes buy. In
                // one pass a filtered layer's result would depend on the order posts happen to be visited in.
                for (int f = 0; f < filters.Length && w > 0; f++)
                {
                    w *= FilterWeight(filters[f], grid, ix, iz);
                }

                weight[row + ix] = (float)w;
                any |= w > 0;
            }
        }

        return any;
    }

    private static void ApplyLayer(HeightGrid grid, TerrainLayer layer, float[] weight, Span<int> range)
    {
        BoundaryRange(layer.Boundary, grid, range);
        if (!FillWeights(grid, layer, weight, range))
        {
            return;
        }

        int posts = grid.Posts;
        double spacingM = grid.SpacingM;
        double originM = grid.OriginM;
        float[] height = grid.Height;
        int first = Math.Max(range[1], grid.RowOffset);
        int lastRow = Math.Min(range[3], grid.RowOffset + grid.Rows - 1);
        for (int iz = first; iz <= lastRow; iz++)
        {
            double z = originM + (iz * spacingM);
            int row = (iz - grid.RowOffset) * posts;
            for (int ix = range[0]; ix <= range[2]; ix++)
            {
                int at = row + ix;
                float w = weight[at];
                if (w <= 0)
                {
                    continue;
                }

                float current = height[at];
                double value = AffectorAt(layer.Affector, originM + (ix * spacingM), z, current);
                height[at] = (float)BlendAt(layer.Blend, current, value, w);
            }
        }
    }

    private static double BlendAt(BlendMode blend, double current, double value, double w) => blend switch
    {
        BlendMode.Add => current + (value * w),
        BlendMode.Replace => current + ((value - current) * w),
        // Feathered so it stays continuous: at w = 1 this is a hard max, and below that it lerps toward it.
        _ => current + ((Math.Max(current, value) - current) * w),
    };

    private static double AffectorAt(TerrainAffector affector, double x, double z, double current)
    {
        switch (affector.Kind)
        {
            case AffectorKind.Constant:
                return affector.HeightM;
            case AffectorKind.Terrace:
                return TerraceAt(current, affector.StepM, affector.Sharpness);
            default:
            {
                double sx = x;
                double sz = z;
                if (affector.WarpM != 0)
                {
                    TerrainHash.WarpPoint(
                        x,
                        z,
                        unchecked(affector.Fractal.Seed ^ 0x2545f491),
                        affector.WarpM,
                        affector.WarpWavelengthM == 0 ? 2000 : affector.WarpWavelengthM,
                        out sx,
                        out sz);
                }

                return affector.BiasM + (TerrainHash.FractalAt(affector.Fractal, sx, sz) * affector.AmplitudeM);
            }
        }
    }

    /// <summary>Quantises a height into bands of <paramref name="stepM"/>, with <paramref name="sharpness"/> controlling how much of a band the rise occupies.</summary>
    public static double TerraceAt(double h, double stepM, double sharpness)
    {
        double k = h / stepM;
        double band = Math.Floor(k);
        double frac = k - band;
        // Gain 1 at sharpness 0 is the exact identity, which is what makes the parameter safe to sweep up from nothing.
        double gain = 1 + (sharpness * 8);
        double shaped = Math.Min(Math.Max(((frac - 0.5) * gain) + 0.5, 0), 1);
        return (band + shaped) * stepM;
    }

    /// <summary>
    /// A boundary's weight at a planet point: 1 well inside, 0 outside, a Hermite ramp across the feather just inside the edge.
    /// </summary>
    /// <remarks>
    /// Feathering <b>inward</b> rather than straddling the edge is deliberate: a layer then never reaches outside its
    /// declared shape, which keeps one region's landform from leaking into its neighbour.
    /// </remarks>
    public static double BoundaryWeight(TerrainBoundary boundary, double x, double z) => boundary.Kind switch
    {
        BoundaryKind.Circle => Feather(
            boundary.RadiusM - Math.Sqrt(((x - boundary.X) * (x - boundary.X)) + ((z - boundary.Z) * (z - boundary.Z))),
            boundary.FeatherM),
        BoundaryKind.Rect => Feather(
            Math.Min(boundary.HalfXM - Math.Abs(x - boundary.X), boundary.HalfZM - Math.Abs(z - boundary.Z)),
            boundary.FeatherM),
        BoundaryKind.Polygon => Feather(SignedDistanceInPolygon(boundary.Points, x, z), boundary.FeatherM),
        _ => Feather(boundary.HalfWidthM - DistanceToPolyline(boundary.Points, x, z), boundary.FeatherM),
    };

    /// <summary>A Hermite ramp over <paramref name="width"/> metres inside the edge. <paramref name="inside"/> is the distance in, negative outside.</summary>
    private static double Feather(double inside, double width)
    {
        if (inside <= 0)
        {
            return 0;
        }

        if (width <= 0 || inside >= width)
        {
            return 1;
        }

        double t = inside / width;
        return t * t * (3 - (2 * t));
    }

    private static double FilterWeight(TerrainFilter filter, HeightGrid grid, int ix, int iz)
    {
        if (filter.Kind == FilterKind.Height)
        {
            return BandWeight(grid.Height[((iz - grid.RowOffset) * grid.Posts) + ix], filter.Min, filter.Max, filter.Feather);
        }

        return BandWeight(SlopeAtPost(grid, ix, iz), filter.Min, filter.Max, filter.Feather);
    }

    /// <summary>1 inside <c>[min, max]</c>, ramping to 0 over <paramref name="feather"/> on each side.</summary>
    /// <remarks>
    /// <b>NaN weighs nothing.</b> A slope read outside a row window is NaN by design (see <see cref="SlopeAtPost"/>), and
    /// both <c>value &lt; min</c> and <c>value &gt; max</c> are false for it — so without this line the function falls
    /// through to <c>return 1</c> and applies the layer at <b>full strength</b> exactly where it knows nothing. Today the
    /// halo is wide enough that the NaN never reaches a row the band keeps, so the guard changes no baked value; it stops
    /// a fourth slope-filtered layer, or one reading two posts out, from turning that margin into a stripe of rock along
    /// every band boundary whose shape depends on the core count.
    /// </remarks>
    private static double BandWeight(double value, double min, double max, double feather)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        if (value < min)
        {
            return feather <= 0 ? 0 : RampUp((value - (min - feather)) / feather);
        }

        if (value > max)
        {
            return feather <= 0 ? 0 : RampUp((max + feather - value) / feather);
        }

        return 1;
    }

    private static double RampUp(double t)
    {
        double c = Math.Min(Math.Max(t, 0), 1);
        return c * c * (3 - (2 * c));
    }

    /// <summary>
    /// Slope at a post as rise over run, from a central difference of its neighbours.
    /// </summary>
    /// <remarks>
    /// Edge posts take a one-sided difference rather than wrapping: the grid is a planet, not a torus, and wrapping would
    /// put a cliff along the seam that a slope-filtered layer would then dutifully decorate. The rim is the PLANET's,
    /// never a row window's — a band that mistook its own first row for the edge of the world would take a one-sided
    /// difference in the middle of open ground and put a seam there.
    /// </remarks>
    public static double SlopeAtPost(HeightGrid grid, int ix, int iz)
    {
        int posts = grid.Posts;
        double spacingM = grid.SpacingM;
        float[] height = grid.Height;
        int x0 = ix > 0 ? ix - 1 : ix;
        int x1 = ix + 1 < posts ? ix + 1 : ix;
        int z0 = iz > 0 ? iz - 1 : iz;
        int z1 = iz + 1 < posts ? iz + 1 : iz;
        double runX = (x1 - x0) * spacingM;
        double runZ = (z1 - z0) * spacingM;

        // A window's OUTERMOST row reads one row outside itself, and that is by design.
        //
        // The rim clamp above is the planet's, so on a band that does not touch the planet edge `z0`/`z1` can fall
        // outside the window. JavaScript reads past a `Float32Array` as `undefined`, which makes the slope NaN, which
        // makes the band filter return 1 — and the halo is sized so that every row poisoned this way is discarded before
        // the interior begins. Returning NaN here is therefore not a tolerance, it is the transcription: it reproduces
        // what the client computes, on rows neither runtime keeps. C# would otherwise throw, which is how this was found.
        if (z0 < grid.RowOffset || z1 >= grid.RowOffset + grid.Rows)
        {
            return double.NaN;
        }

        int here = (iz - grid.RowOffset) * posts;
        // Widen to double BEFORE subtracting. `float - float` is float arithmetic in C# and rounds the difference;
        // JavaScript reads a `Float32Array` element as a double and subtracts exactly. Two posts 40 m apart in height
        // differ by a value f32 cannot always hold to the last bit, so the two runtimes disagreed by one ULP on about
        // 0.4 % of posts — invisible in any single reading, and enough to change which side of the slope filter a post
        // falls on. This is the only line in the port where the languages differ by default, and it took a per-layer
        // digest bisect to find: layers 1-8 matched, layer 9 was the first with a slope filter.
        double hereX1 = height[here + x1];
        double hereX0 = height[here + x0];
        double thereZ1 = height[((z1 - grid.RowOffset) * posts) + ix];
        double thereZ0 = height[((z0 - grid.RowOffset) * posts) + ix];
        double dx = runX == 0 ? 0 : (hereX1 - hereX0) / runX;
        double dz = runZ == 0 ? 0 : (thereZ1 - thereZ0) / runZ;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>Positive inside the polygon, negative outside; the magnitude is the distance to the nearest edge.</summary>
    private static double SignedDistanceInPolygon(double[] points, double x, double z)
    {
        int count = points.Length >> 1;
        bool inside = false;
        double nearest = double.PositiveInfinity;
        double jx = points[(count - 1) * 2];
        double jz = points[((count - 1) * 2) + 1];
        for (int i = 0; i < count; i++)
        {
            double vx = points[i * 2];
            double vz = points[(i * 2) + 1];
            if (vz > z != jz > z && x < (((jx - vx) * (z - vz)) / (jz - vz)) + vx)
            {
                inside = !inside;
            }

            nearest = Math.Min(nearest, DistanceToSegment(x, z, vx, vz, jx, jz));
            jx = vx;
            jz = vz;
        }

        return inside ? nearest : -nearest;
    }

    private static double DistanceToPolyline(double[] points, double x, double z)
    {
        int count = points.Length >> 1;
        double nearest = double.PositiveInfinity;
        for (int i = 1; i < count; i++)
        {
            nearest = Math.Min(
                nearest,
                DistanceToSegment(x, z, points[(i - 1) * 2], points[((i - 1) * 2) + 1], points[i * 2], points[(i * 2) + 1]));
        }

        return nearest;
    }

    private static double DistanceToSegment(double px, double pz, double ax, double az, double bx, double bz)
    {
        double abx = bx - ax;
        double abz = bz - az;
        double denominator = (abx * abx) + (abz * abz);
        double t = denominator <= 0 ? 0 : Math.Min(Math.Max((((px - ax) * abx) + ((pz - az) * abz)) / denominator, 0), 1);
        double dx = px - (ax + (abx * t));
        double dz = pz - (az + (abz * t));
        return Math.Sqrt((dx * dx) + (dz * dz));
    }
}
