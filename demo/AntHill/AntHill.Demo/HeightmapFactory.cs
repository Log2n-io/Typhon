using AntHill.Core;
using Godot;

namespace AntHill.Demo;

/// <summary>
/// Godot-coupled construction + export for <see cref="HeightmapResource"/> — procedural generation
/// (<c>FastNoiseLite</c>) and <c>Image</c> export. Lives in <c>AntHill.Demo</c> (the Godot project);
/// <c>AntHill.Core</c> owns the Godot-free <see cref="HeightmapResource"/> data + sampling core.
/// </summary>
internal static class HeightmapFactory
{
    public static HeightmapResource GeneratePerlin(
        int resolution = HeightmapResource.DefaultResolution,
        float relief = HeightmapResource.DefaultRelief,
        int seed = 1337)
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
        var worldStep = HeightmapResource.WorldSize / resolution;
        for (var z = 0; z < resolution; z++)
        {
            var wz = (z + 0.5f) * worldStep;
            for (var x = 0; x < resolution; x++)
            {
                var wx = (x + 0.5f) * worldStep;
                // GetNoise2D returns roughly [-1, 1] but with FBM the distribution is bunched near 0 — most cells end up
                // shallow. Boost contrast with a signed-power (preserves sign, exaggerates large values) so peaks and
                // valleys read clearly. Exponent 0.6 = mild stretch; <1 pushes values away from 0.
                var n = noise.GetNoise2D(wx, wz);
                var sign = n < 0 ? -1f : 1f;
                var stretched = sign * Mathf.Pow(Mathf.Abs(n), 0.6f);
                data[z * resolution + x] = stretched * relief;
            }
        }

        return new HeightmapResource(resolution, relief, data);
    }

    /// <summary>Returns an Image of the heightmap as FORMAT_RF (one float per pixel).</summary>
    public static Image ToImage(HeightmapResource heightmap)
    {
        // Float[] → byte[] (little-endian, matches Godot's expected byte layout for Rf).
        var bytes = new byte[heightmap.Data.Length * 4];
        System.Buffer.BlockCopy(heightmap.Data, 0, bytes, 0, bytes.Length);
        return Image.CreateFromData(heightmap.Resolution, heightmap.Resolution, false, Image.Format.Rf, bytes);
    }
}
