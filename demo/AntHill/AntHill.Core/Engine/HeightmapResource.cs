namespace AntHill;

/// <summary>
/// Procedural heightmap for the 100 m × 100 m forest-floor world. Phase 1 implementation —
/// owned by the AntHill project for now; Phase 3 promotes this to a Typhon shared resource so
/// the slope-aware <c>MoveAll</c> system can sample it from sim-side without going through
/// Godot's RenderingServer.
///
/// Defaults:
///   • Resolution 256 × 256 (≈39 cm/cell — tunable per plan §3.8)
///   • Relief ±0.5 m (Perlin amplitude)
///   • 3 octaves, frequency 0.05 → ≈20 m dominant wavelength + 2 m secondary bumps
///
/// The <c>float[]</c> stores raw signed displacement in metres (no centre-bias). <c>HeightmapFactory.ToImage</c>
/// produces a <c>FORMAT_RF</c> image suitable for direct shader sampling; the shader interprets the
/// sampled R value as metres of Y displacement.
/// </summary>
/// <remarks>
/// The Godot-free data + sampling core, owned by <c>AntHill.Core</c>. Procedural generation
/// (<c>FastNoiseLite</c>) and <c>Image</c> export live in <c>HeightmapFactory</c> in <c>AntHill.Demo</c>
/// (the Godot project), which constructs instances of this class via its public constructor.
/// </remarks>
public sealed class HeightmapResource
{
    public const int DefaultResolution = 1024;
    public const float DefaultRelief = 1.0f;       // ±1.0 m peak amplitude — FBM concentrates near 0, so typical relief is ~½ this

    /// <summary>World extent in metres. Mirrors <c>AntRenderer.WorldSizeM</c>, kept as a literal so the core stays Godot-free.</summary>
    public const float WorldSize = 100f;

    public int Resolution { get; }
    public float Relief { get; }
    public float[] Data { get; }

    private readonly float _cellSize;            // metres per cell
    private readonly float _invCellSize;

    public HeightmapResource(int resolution, float relief, float[] data)
    {
        Resolution = resolution;
        Relief = relief;
        Data = data;
        _cellSize = WorldSize / resolution;
        _invCellSize = 1f / _cellSize;
    }

    /// <summary>Bilinear sample at world (x, z) in metres. Clamps to edges.</summary>
    public float Sample(float worldX, float worldZ)
    {
        var fx = System.Math.Clamp(worldX * _invCellSize - 0.5f, 0f, Resolution - 1.001f);
        var fz = System.Math.Clamp(worldZ * _invCellSize - 0.5f, 0f, Resolution - 1.001f);
        var ix = (int)fx;
        var iz = (int)fz;
        var tx = fx - ix;
        var tz = fz - iz;

        var h00 = Data[iz * Resolution + ix];
        var h10 = Data[iz * Resolution + ix + 1];
        var h01 = Data[(iz + 1) * Resolution + ix];
        var h11 = Data[(iz + 1) * Resolution + ix + 1];

        var h0 = h00 + (h10 - h00) * tx;
        var h1 = h01 + (h11 - h01) * tx;
        return h0 + (h1 - h0) * tz;
    }
}
