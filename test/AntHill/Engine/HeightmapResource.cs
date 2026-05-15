using Godot;

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
/// The <c>float[]</c> stores raw signed displacement in metres (no centre-bias). <see cref="ToImage"/>
/// produces a <c>FORMAT_RF</c> image suitable for direct shader sampling; the shader interprets the
/// sampled R value as metres of Y displacement.
/// </summary>
public sealed class HeightmapResource
{
    public const int DefaultResolution = 1024;
    public const float DefaultRelief = 1.0f;       // ±1.0 m peak amplitude — FBM concentrates near 0, so typical relief is ~½ this
    public const float WorldSize = AntRenderer.WorldSizeM;

    public int Resolution { get; }
    public float Relief { get; }
    public float[] Data { get; }

    private readonly float _cellSize;            // metres per cell
    private readonly float _invCellSize;

    private HeightmapResource(int resolution, float relief, float[] data)
    {
        Resolution = resolution;
        Relief = relief;
        Data = data;
        _cellSize = WorldSize / resolution;
        _invCellSize = 1f / _cellSize;
    }

    public static HeightmapResource GeneratePerlin(int resolution = DefaultResolution, float relief = DefaultRelief, int seed = 1337)
    {
        // Tuned for a 100 m × 100 m bug-world.
        //   • Base frequency 0.15 → ≈6.7 m wavelength on the largest octave → ~15 dominant features across the world.
        //   • 5 octaves at lacunarity 2.0 → finest octave wavelength ≈42 cm, giving cm-scale bumps for the Loupe band.
        //   • Gain 0.55 keeps higher octaves contributing visibly (default 0.5 already fades them fast).
        var noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin,
            Seed = seed,
            Frequency = 0.15f,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 5,
            FractalLacunarity = 2.0f,
            FractalGain = 0.55f,
        };

        var data = new float[resolution * resolution];
        float worldStep = WorldSize / resolution;
        for (int z = 0; z < resolution; z++)
        {
            float wz = (z + 0.5f) * worldStep;
            for (int x = 0; x < resolution; x++)
            {
                float wx = (x + 0.5f) * worldStep;
                // GetNoise2D returns roughly [-1, 1] but with FBM the distribution is bunched near 0 — most cells end up
                // shallow. Boost contrast with a signed-power (preserves sign, exaggerates large values) so peaks and
                // valleys read clearly. Exponent 0.6 = mild stretch; <1 pushes values away from 0.
                float n = noise.GetNoise2D(wx, wz);
                float sign = n < 0 ? -1f : 1f;
                float stretched = sign * Mathf.Pow(Mathf.Abs(n), 0.6f);
                data[z * resolution + x] = stretched * relief;
            }
        }

        return new HeightmapResource(resolution, relief, data);
    }

    /// <summary>Bilinear sample at world (x, z) in metres. Clamps to edges.</summary>
    public float Sample(float worldX, float worldZ)
    {
        float fx = Mathf.Clamp(worldX * _invCellSize - 0.5f, 0f, Resolution - 1.001f);
        float fz = Mathf.Clamp(worldZ * _invCellSize - 0.5f, 0f, Resolution - 1.001f);
        int ix = (int)fx;
        int iz = (int)fz;
        float tx = fx - ix;
        float tz = fz - iz;

        float h00 = Data[iz * Resolution + ix];
        float h10 = Data[iz * Resolution + ix + 1];
        float h01 = Data[(iz + 1) * Resolution + ix];
        float h11 = Data[(iz + 1) * Resolution + ix + 1];

        float h0 = h00 + (h10 - h00) * tx;
        float h1 = h01 + (h11 - h01) * tx;
        return h0 + (h1 - h0) * tz;
    }

    /// <summary>Returns an Image of the heightmap as FORMAT_RF (one float per pixel).</summary>
    public Image ToImage()
    {
        // Float[] → byte[] (little-endian, matches Godot's expected byte layout for Rf).
        var bytes = new byte[Data.Length * 4];
        System.Buffer.BlockCopy(Data, 0, bytes, 0, bytes.Length);
        return Image.CreateFromData(Resolution, Resolution, false, Image.Format.Rf, bytes);
    }
}
