using System;

namespace SwgTatooine.World.Terrain;

/// <summary>What a layer is confined to. <see cref="FeatherM"/> is how far inside the edge the weight ramps from 0 to 1.</summary>
public enum BoundaryKind
{
    Circle,
    Rect,
    Polygon,
    Polyline,
}

/// <summary>A predicate on the terrain built so far.</summary>
public enum FilterKind
{
    /// <summary>Applies within a height band, in metres.</summary>
    Height,

    /// <summary>Applies within a slope band, measured as rise over run, so 1.0 is 45°.</summary>
    Slope,
}

/// <summary>What a layer does where it applies.</summary>
public enum AffectorKind
{
    /// <summary>Levels the ground to a fixed height — what SWG's <c>AHCN</c> does inside a feathered circle to make a town's pad.</summary>
    Constant,

    /// <summary>Adds a fractal term, optionally domain-warped first.</summary>
    Fractal,

    /// <summary>Quantises the height that is already there into bands. The one affector that reads before it writes.</summary>
    Terrace,
}

/// <summary>How an affector's value joins what is already there.</summary>
public enum BlendMode
{
    Add,
    Replace,
    Max,
}

/// <summary>A shape a layer is confined to. Fields not relevant to the kind are ignored.</summary>
public sealed class TerrainBoundary
{
    public BoundaryKind Kind { get; init; }

    public double X { get; init; }

    public double Z { get; init; }

    public double RadiusM { get; init; }

    public double HalfXM { get; init; }

    public double HalfZM { get; init; }

    /// <summary>Flat <c>x, z</c> pairs, in either winding, for a polygon or a polyline.</summary>
    public double[] Points { get; init; } = [];

    /// <summary>A polyline's half-width: everything within this of the line is inside.</summary>
    public double HalfWidthM { get; init; }

    public double FeatherM { get; init; }
}

/// <summary>A band predicate on the accumulator. The bounds mean metres for a height filter and rise over run for a slope one.</summary>
public readonly struct TerrainFilter
{
    public FilterKind Kind { get; init; }

    public double Min { get; init; }

    public double Max { get; init; }

    public double Feather { get; init; }
}

/// <summary>What a layer writes where it applies.</summary>
public readonly struct TerrainAffector
{
    public AffectorKind Kind { get; init; }

    /// <summary><see cref="AffectorKind.Constant"/>: the level to hold.</summary>
    public double HeightM { get; init; }

    /// <summary><see cref="AffectorKind.Terrace"/>: the band height.</summary>
    public double StepM { get; init; }

    /// <summary><see cref="AffectorKind.Terrace"/>: 0 is the identity, 1 confines the rise to about a ninth of a step.</summary>
    public double Sharpness { get; init; }

    public double AmplitudeM { get; init; }

    public double BiasM { get; init; }

    public FractalSpec Fractal { get; init; }

    /// <summary>Metres the sample point may be displaced before the fractal reads it; 0 for no warp.</summary>
    public double WarpM { get; init; }

    public double WarpWavelengthM { get; init; }
}

/// <summary>One layer of the authored tree.</summary>
public sealed class TerrainLayer
{
    /// <summary>For tests and for reading a tree; never shown to a player.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary><see langword="null"/> means global.</summary>
    public TerrainBoundary Boundary { get; init; }

    public TerrainFilter[] Filters { get; init; } = [];

    public TerrainAffector Affector { get; init; }

    public BlendMode Blend { get; init; }
}

/// <summary>
/// A square grid of height posts covering the planet, or a window of contiguous rows of one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grid is the contract</b>, not the continuous function: everything that samples height — this server, the
/// browser client's CPU, and the GPU through a texture — reads these posts and interpolates between them. That is what
/// makes the cross-runtime agreement test a comparison of texels rather than of a function's tail digits.
/// </para>
/// <para>
/// What a second implementation must match, exactly. These are not implementation details, they are the contract's terms,
/// and every one of them changes the resulting heights:
/// </para>
/// <list type="number">
/// <item><b>The accumulator is f32.</b> Every layer's output is rounded to <see cref="float"/> on store, and the next
/// layer's filters read those rounded values. A twin holding the accumulator in <see cref="double"/> diverges immediately.</item>
/// <item><b>The weights are f32 too.</b> A layer's per-post weight is written to a float scratch buffer and read back
/// before blending, so the value actually blended is <c>f32(boundary × filters)</c>, not the double product.</item>
/// <item><b>Intermediates are f64</b>, in the operation order written here.</item>
/// <item><b>Layer order is the tree's order</b>, and the second stage's constants are read out of the finished first stage.</item>
/// <item><b>The bake is whole-grid and NOT tileable.</b> The slope filter takes a one-sided difference at the grid rim,
/// bounding boxes clamp to grid bounds, and each pad's constant is sampled from the completed landform. A row window is
/// the one exception and weakens nothing: it still knows the whole planet's <see cref="Posts"/>, so rim handling and
/// boundary extents are computed against the planet, and a window carrying <see cref="TerrainLayers.HaloRows"/> rows of
/// overlap reproduces the whole-grid heights on its interior exactly.</item>
/// </list>
/// </remarks>
public sealed class HeightGrid
{
    /// <summary>Posts per side <b>of the planet</b>, not of this window.</summary>
    public int Posts { get; }

    /// <summary>Metres between posts.</summary>
    public double SpacingM { get; }

    /// <summary>Planet coordinate of post 0, on both axes.</summary>
    public double OriginM { get; }

    /// <summary>First planet row this grid holds. 0 for a whole grid.</summary>
    public int RowOffset { get; }

    /// <summary>Rows held. Equal to <see cref="Posts"/> for a whole grid.</summary>
    public int Rows { get; }

    /// <summary><c>Posts × Rows</c> heights in metres, row-major with z outer. <b>f32</b>, so a twin must round the same way.</summary>
    public float[] Height { get; }

    private HeightGrid(int posts, double spacingM, double originM, int rowOffset, int rows, float[] height)
    {
        Posts = posts;
        SpacingM = spacingM;
        OriginM = originM;
        RowOffset = rowOffset;
        Rows = rows;
        Height = height;
    }

    /// <summary>Allocates a whole grid covering <c>[originM, originM + (posts − 1) · spacingM]</c> on both axes.</summary>
    public static HeightGrid Create(int posts, double spacingM, double originM) => Window(posts, spacingM, originM, 0, posts);

    /// <summary>
    /// Allocates a grid holding only rows <c>[rowOffset, rowOffset + rows)</c> of the planet.
    /// </summary>
    /// <remarks>
    /// Everything else about it is the planet's: <see cref="Posts"/> stays the planet's side, so a boundary's extent, the
    /// rim of the slope filter and every planet coordinate are computed exactly as in a whole-grid bake. Only the storage
    /// and the loop bounds narrow.
    /// </remarks>
    public static HeightGrid Window(int posts, double spacingM, double originM, int rowOffset, int rows)
    {
        if (rowOffset < 0 || rows <= 0 || rowOffset + rows > posts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rows),
                $"height grid window [{rowOffset}, {rowOffset + rows}) does not fit {posts} planet rows");
        }

        return new HeightGrid(posts, spacingM, originM, rowOffset, rows, new float[posts * rows]);
    }

    /// <summary>
    /// Wraps an existing buffer as a whole grid, without copying it.
    /// </summary>
    /// <exception cref="ArgumentException">The buffer is not exactly <c>posts²</c>, which would otherwise index out of it later.</exception>
    public static HeightGrid Adopt(int posts, double spacingM, double originM, float[] height)
    {
        ArgumentNullException.ThrowIfNull(height);
        if (height.Length != posts * posts)
        {
            throw new ArgumentException($"height buffer of {height.Length} does not hold {posts}² posts", nameof(height));
        }

        return new HeightGrid(posts, spacingM, originM, 0, posts, height);
    }
}
