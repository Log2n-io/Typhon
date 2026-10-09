using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using NUnit.Framework;
using SwgTatooine.World.Terrain;

namespace SwgTatooine.Tests;

/// <summary>
/// The C# terrain and the TypeScript terrain are one terrain, held to one golden file.
/// </summary>
/// <remarks>
/// <para>
/// The server writes an entity's altitude and the browser draws the ground under it. If the two disagree by a metre the
/// entity floats or sinks by a metre, and no amount of care on either side finds that — each is self-consistent. So the
/// contract is not "the same algorithm", it is <b>the same bits</b>, and this fixture reads
/// <c>demo/SwgTatooine.Client/test/golden/terrain-hash.json</c>: the very file
/// <c>demo/SwgTatooine.Client/test/terrain-hash.test.ts</c> reads. Two copies of a golden is two goldens, and they drift.
/// </para>
/// <para>
/// Everything is compared as <b>raw IEEE-754 bits</b> rather than to a tolerance. A tolerance would pass a twin that is
/// subtly wrong and then fail months later on a coordinate nobody tested; the whole design of the noise — integer hash,
/// documented operation order, seed reduced to 32 bits at the source — exists to make exactness achievable, and a
/// tolerance here would throw that away.
/// </para>
/// </remarks>
[TestFixture]
public sealed class TerrainGoldenChecks
{
    private JsonElement _golden;

    [OneTimeSetUp]
    public void Load()
    {
        _golden = JsonDocument.Parse(File.ReadAllText(GoldenPath())).RootElement.Clone();
    }

    [Test]
    public void HashPost_ReproducesEveryGoldenVector()
    {
        int seen = 0;
        foreach (JsonElement row in _golden.GetProperty("hashPost").EnumerateArray())
        {
            int ix = row.GetProperty("ix").GetInt32();
            int iz = row.GetProperty("iz").GetInt32();
            int seed = SeedOf(row.GetProperty("seed"));
            uint expected = row.GetProperty("u32").GetUInt32();
            Assert.That(TerrainHash.HashPost(ix, iz, seed), Is.EqualTo(expected), $"hashPost({ix}, {iz}, {seed})");
            seen++;
        }

        // The vectors include int.MinValue and int.MaxValue, which is where a twin that hashes through a double breaks.
        Assert.That(seen, Is.GreaterThanOrEqualTo(8), "the golden lost its hash vectors");
    }

    [Test]
    public void ValueNoise_ReproducesEveryGoldenVector()
    {
        foreach (JsonElement row in _golden.GetProperty("valueNoise").EnumerateArray())
        {
            double x = row.GetProperty("x").GetDouble();
            double z = row.GetProperty("z").GetDouble();
            int seed = SeedOf(row.GetProperty("seed"));
            AssertBits(TerrainHash.ValueNoise(x, z, seed), row.GetProperty("bits").GetString(), $"valueNoise({x}, {z})");
        }
    }

    [Test]
    public void FractalAt_ReproducesBothGoldenSpecs()
    {
        foreach ((string section, string specName) in new[] { ("fractal", "fractalSpec"), ("ridged", "ridgedSpec") })
        {
            FractalSpec spec = SpecFrom(_golden.GetProperty(specName));
            foreach (JsonElement row in _golden.GetProperty(section).EnumerateArray())
            {
                double x = row.GetProperty("x").GetDouble();
                double z = row.GetProperty("z").GetDouble();
                AssertBits(TerrainHash.FractalAt(spec, x, z), row.GetProperty("bits").GetString(), $"{section}({x}, {z})");
            }
        }
    }

    [Test]
    public void WarpPoint_ReproducesEveryGoldenVector()
    {
        // The warp's constants are the shipping dune-sea layer's, pinned in the tool that writes the golden.
        foreach (JsonElement row in _golden.GetProperty("warp").EnumerateArray())
        {
            double x = row.GetProperty("x").GetDouble();
            double z = row.GetProperty("z").GetDouble();
            TerrainHash.WarpPoint(x, z, 0x7a700151, 260, 2600, out double wx, out double wz);
            AssertBits(wx, row.GetProperty("xBits").GetString(), $"warpX({x}, {z})");
            AssertBits(wz, row.GetProperty("zBits").GetString(), $"warpZ({x}, {z})");
        }
    }

    [Test]
    public void TheBakedGrid_ReproducesEveryGoldenPost()
    {
        // The whole tree, at the golden's coarse resolution: boundaries, both slope filters, the terraces, the warp, the
        // f32 accumulator and every one of the fourteen pads, in the order the client applies them. A single constant
        // transcribed wrongly moves a post, and a post is what this compares.
        JsonElement grid = _golden.GetProperty("grid");
        int posts = grid.GetProperty("posts").GetInt32();
        double spacingM = grid.GetProperty("spacingM").GetDouble();
        double originM = grid.GetProperty("originM").GetDouble();

        Heightfield field = new(HeightGrid.Create(posts, spacingM, originM));
        TatooineTerrain.Bake(field);

        AssertBits(field.MinHeightM, grid.GetProperty("minHeightM").GetString(), "minHeightM");
        AssertBits(field.MaxHeightM, grid.GetProperty("maxHeightM").GetString(), "maxHeightM");

        int seen = 0;
        foreach (JsonElement row in grid.GetProperty("posts_").EnumerateArray())
        {
            int ix = row.GetProperty("ix").GetInt32();
            int iz = row.GetProperty("iz").GetInt32();
            AssertBits(field.Grid.Height[(iz * posts) + ix], row.GetProperty("bits").GetString(), $"post ({ix}, {iz})");
            seen++;
        }

        Assert.That(seen, Is.GreaterThan(40), "the golden lost its grid posts");
    }

    [Test]
    public void TheWholeField_HashesToTheGoldenDigest()
    {
        // The 128-post grid pins the tree; this pins the whole field at a resolution where the parts that DEPEND on
        // resolution differ — boundary ranges measured in posts, the grid rim the slope filter reads, and the f32
        // rounding of sixteen times as many accumulated values. A digest rather than a fixture, because the alternative
        // is a megabyte of floats in the repository and this is the same evidence.
        JsonElement field = _golden.GetProperty("field");
        int posts = field.GetProperty("posts").GetInt32();
        Heightfield baked = new(HeightGrid.Create(posts, field.GetProperty("spacingM").GetDouble(), field.GetProperty("originM").GetDouble()));
        TatooineTerrain.Bake(baked);
        Assert.That(DigestOf(baked.Grid.Height), Is.EqualTo(field.GetProperty("digest").GetString()));
    }

    /// <summary>FNV-1a over the field's raw bytes, as two 32-bit halves so neither runtime needs 64-bit integers.</summary>
    private static string DigestOf(float[] values)
    {
        ReadOnlySpan<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan());
        uint hi = 0x811c9dc5;
        uint lo = 0x811c9dc5;
        unchecked
        {
            foreach (byte b in bytes)
            {
                lo = (lo ^ b) * 0x01000193;
                hi = (hi ^ lo) * 0x01000193;
            }
        }

        return hi.ToString("x8", CultureInfo.InvariantCulture) + lo.ToString("x8", CultureInfo.InvariantCulture);
    }

    [Test]
    public void TheAccumulatorIsF32_WhichChangesTheResult()
    {
        // Contract term 1: the accumulator is float, and the next layer's filters read those ROUNDED values. A twin that
        // accumulated in double would pass a tolerance test and fail the golden, so this states the mechanism directly
        // rather than leaving it to be inferred from a bit comparison somewhere else.
        HeightGrid grid = HeightGrid.Create(4, 100, -150);
        TerrainLayers.BakeLayers(
            grid,
            [
                new TerrainLayer
                {
                    Name = "a third",
                    Blend = BlendMode.Add,
                    Affector = new TerrainAffector { Kind = AffectorKind.Constant, HeightM = 1.0 / 3.0 },
                },
            ]);

        Assert.That(grid.Height[0], Is.EqualTo((float)(1.0 / 3.0)));
        Assert.That((double)grid.Height[0], Is.Not.EqualTo(1.0 / 3.0), "an f32 store must lose the tail, or the contract is not being kept");
    }

    private static FractalSpec SpecFrom(JsonElement e) => new()
    {
        Seed = SeedOf(e.GetProperty("seed")),
        Octaves = e.GetProperty("octaves").GetInt32(),
        WavelengthXM = e.GetProperty("wavelengthXM").GetDouble(),
        WavelengthZM = e.GetProperty("wavelengthZM").GetDouble(),
        Lacunarity = e.GetProperty("lacunarity").GetDouble(),
        Gain = e.GetProperty("gain").GetDouble(),
        Ridged = e.GetProperty("ridged").GetBoolean(),
    };

    /// <summary>
    /// A seed from the golden, which writes it the way JavaScript holds it: a positive number up to 2³².
    /// </summary>
    /// <remarks>
    /// <c>GetInt32</c> throws on <c>0x9e3779b1</c>, which is one of the pinned vectors and is exactly the value the
    /// TypeScript comments warn about. Reading it as a <see cref="long"/> and truncating is what <c>| 0</c> does.
    /// </remarks>
    private static int SeedOf(JsonElement e) => unchecked((int)e.GetInt64());

    /// <summary>Compares a double to the golden's hex, which is its eight bytes in memory order — little-endian on both runtimes.</summary>
    private static void AssertBits(double actual, string expectedHex, string what)
    {
        Assert.That(BitsOf(actual), Is.EqualTo(expectedHex), $"{what}: {actual:R} is not the golden value");
    }

    private static string BitsOf(double v)
    {
        byte[] bytes = BitConverter.GetBytes(v);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return Convert.ToHexString(bytes).ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The golden, found by walking up for the solution file.
    /// </summary>
    /// <remarks>
    /// A relative path from the test binary would be a path into <c>bin/Debug/net10.0</c> and would break the first time
    /// the target framework moved. Walking up for a sentinel is the same trick the repo's scripts use and it fails with a
    /// sentence rather than a <c>FileNotFoundException</c> naming a directory nobody recognises.
    /// </remarks>
    private static string GoldenPath()
    {
        DirectoryInfo dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Typhon.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new FileNotFoundException("no Typhon.slnx above the test binary, so the repository root is unknown");
        }

        string path = Path.Combine(dir.FullName, "demo", "SwgTatooine.Client", "test", "golden", "terrain-hash.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"the terrain golden is missing at {path}. It is generated by the client's tools/bake-golden.ts and is "
                + "the only thing holding the C# and TypeScript terrains together.");
        }

        return path;
    }
}
