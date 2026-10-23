using System;

namespace SwgTatooine.World.Terrain;

/// <summary>
/// The terrain's noise primitive: an <b>integer hash</b> rather than the usual <c>fract(sin(dot(…)))</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is a transcription of <c>demo/SwgTatooine.Client/src/terrain/hash.ts</c>, and the two must agree to the last bit.
/// Three runtimes produce heights for the same post — this one, the browser client, and indirectly the GPU, which samples
/// the baked field as a texture — and an entity whose server altitude disagrees with the ground the client drew is an
/// entity that floats or sinks.
/// </para>
/// <para>
/// A float hash cannot deliver that. GPU <c>fract</c>/<c>sin</c> hashes are not bit-identical across drivers, and neither
/// <c>Math.sin</c> nor <see cref="Math.Sin"/> is specified to the last bit. So everything below the final interpolation is
/// <see cref="uint"/> multiply / xor / shift — exact in JavaScript through <c>Math.imul</c> and <c>&gt;&gt;&gt; 0</c>,
/// exact here through <c>unchecked</c> <see cref="uint"/>, and reproducible in GLSL ES 3.0, which has real integer
/// operations.
/// </para>
/// <para>
/// The float part is <see cref="double"/> throughout, which IEEE-754 pins exactly <b>given the same operation order</b> —
/// so the order below is transcribed rather than rearranged, and <c>TerrainGoldenChecks</c> holds it to
/// <c>demo/SwgTatooine.Client/test/golden/terrain-hash.json</c>, the same file the TypeScript test reads.
/// </para>
/// </remarks>
public static class TerrainHash
{
    /// <summary>2⁻³², the exact scale from a <see cref="uint"/> to <c>[0, 1)</c>. A power of two, so the multiply is lossless.</summary>
    private const double U32ToUnit = 2.3283064365386963e-10;

    /// <summary>
    /// Finalises a hash: xorshift-multiply avalanche, so two posts one metre apart share no visible structure.
    /// </summary>
    /// <remarks>
    /// The constants are the well-known 32-bit MurmurHash3 finaliser; nothing here depends on them beyond their being odd
    /// and well-mixed, but they must be identical in every twin.
    /// </remarks>
    private static uint Avalanche(uint h)
    {
        unchecked
        {
            uint x = h;
            x ^= x >> 16;
            x *= 0x85ebca6b;
            x ^= x >> 13;
            x *= 0xc2b2ae35;
            x ^= x >> 16;
            return x;
        }
    }

    /// <summary>Hashes an integer lattice point to a <see cref="uint"/>.</summary>
    /// <param name="ix">Lattice x, any 32-bit integer.</param>
    /// <param name="iz">Lattice z, any 32-bit integer.</param>
    /// <param name="seed">The layer's seed: two layers with the same geometry and different seeds share no structure.</param>
    public static uint HashPost(int ix, int iz, int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h = (h ^ (uint)ix) * 0x27d4eb2d;
            h = (h ^ (uint)iz) * 0x165667b1;
            return Avalanche(h);
        }
    }

    /// <summary>The lattice point's value in <c>[0, 1)</c>.</summary>
    public static double UnitAtPost(int ix, int iz, int seed) => HashPost(ix, iz, seed) * U32ToUnit;

    /// <summary>Hermite ease, <c>3t² − 2t³</c>, the smoothstep the interpolation uses. Defined once so every twin eases identically.</summary>
    private static double Ease(double t) => t * t * (3 - 2 * t);

    /// <summary>
    /// Value noise in <c>[0, 1)</c>: the four surrounding lattice values, eased and bilinearly blended.
    /// </summary>
    /// <remarks>
    /// Value noise rather than gradient (Perlin) noise because the lattice value IS the hash — there is no gradient vector
    /// to agree on, so the cross-runtime contract is as small as it can be. Its one weakness, axis-aligned structure at low
    /// octave counts, is invisible once five octaves are summed at a lacunarity that is not exactly 2.
    /// </remarks>
    public static double ValueNoise(double x, double z, int seed)
    {
        // `Math.Floor` then a cast, not a cast alone: a cast truncates toward zero, so every negative coordinate would
        // land on the lattice cell above it and the planet would be mirrored about its own origin.
        double fx = Math.Floor(x);
        double fz = Math.Floor(z);
        int ix = (int)fx;
        int iz = (int)fz;
        double ux = Ease(x - fx);
        double uz = Ease(z - fz);
        double a = UnitAtPost(ix, iz, seed);
        double b = UnitAtPost(ix + 1, iz, seed);
        double c = UnitAtPost(ix, iz + 1, seed);
        double d = UnitAtPost(ix + 1, iz + 1, seed);
        double top = a + ((b - a) * ux);
        double bottom = c + ((d - c) * ux);
        return top + ((bottom - top) * uz);
    }

    /// <summary>
    /// Evaluates a fractal term at a planet coordinate, normalised to <c>[0, 1]</c>.
    /// </summary>
    /// <remarks>
    /// Normalising by the summed amplitude rather than by the theoretical maximum keeps the output's <i>mean</i> near 0.5
    /// whatever the octave count, so changing <see cref="FractalSpec.Octaves"/> changes the detail and not the elevation —
    /// which matters because the authored tree tunes amplitudes by eye.
    /// </remarks>
    public static double FractalAt(in FractalSpec spec, double x, double z)
    {
        double fx = 1 / spec.WavelengthXM;
        double fz = 1 / spec.WavelengthZM;
        double amplitude = 1;
        double sum = 0;
        double total = 0;
        for (int o = 0; o < spec.Octaves; o++)
        {
            // Each octave takes its own seed so the octaves are independent fields rather than one field at several zooms;
            // with a shared seed, a lacunarity near 2 makes octave n+1 partly a copy of octave n.
            //
            // The seed is reduced to 32 bits HERE, matching the TypeScript's `| 0`. In JavaScript the expression reaches
            // 1.06e10 as a double and works only because the hash re-truncates; unchecked int arithmetic here wraps to the
            // same value, and that is the whole reason both sides write the reduction out rather than relying on it.
            // Unsigned arithmetic, then one cast: `0x9e3779b1` does not fit an int, and the whole expression is defined
            // modulo 2³² on both sides. JavaScript computes it exactly as a double (o ≤ 5, so well under 2⁵³) and then
            // truncates with `| 0`; wrapping uint arithmetic reaches the same 32 bits, which is what `| 0` means.
            int seed = unchecked((int)((uint)spec.Seed + ((uint)o * 0x9e3779b1u)));
            double n = ValueNoise(x * fx, z * fz, seed);
            double shaped = spec.Ridged ? Ridge(n) : n;
            sum += shaped * amplitude;
            total += amplitude;
            fx *= spec.Lacunarity;
            fz *= spec.Lacunarity;
            amplitude *= spec.Gain;
        }

        return sum / total;
    }

    /// <summary>One ridged octave: fold about the midline, then square.</summary>
    private static double Ridge(double n)
    {
        double folded = 1 - Math.Abs((2 * n) - 1);
        return folded * folded;
    }

    /// <summary>
    /// Warps the sample point by a second noise field before the first reads it — the cheapest way to break the
    /// "everything-is-a-blob" signature of raw fBm, and what makes a dune sea read as wind-driven rather than as noise.
    /// </summary>
    /// <param name="amountM">How far a point may be displaced, in metres.</param>
    /// <param name="wavelengthM">Wavelength of the warp field; much longer than the warped field's, or the result is mush.</param>
    public static void WarpPoint(double x, double z, int seed, double amountM, double wavelengthM, out double outX, out double outZ)
    {
        double f = 1 / wavelengthM;
        outX = x + (((ValueNoise(x * f, z * f, seed) * 2) - 1) * amountM);
        outZ = z + (((ValueNoise(x * f, z * f, unchecked(seed ^ 0x5bf03635)) * 2) - 1) * amountM);
    }
}

/// <summary>
/// A fractal noise term: the parameter set of SWG's <c>MapFractal</c>, worth copying as a <i>shape</i> because it is a
/// good one — independent x/z wavelengths, lacunarity, gain, and a ridged mode for the sharp-crested landforms octave
/// noise cannot make.
/// </summary>
public readonly struct FractalSpec
{
    /// <summary>Distinguishes this term from every other.</summary>
    public int Seed { get; init; }

    /// <summary>How many octaves to sum. Five or six is the useful range; more is invisible at 4 m posts.</summary>
    public int Octaves { get; init; }

    /// <summary>Wavelength of the first octave along x, in metres.</summary>
    public double WavelengthXM { get; init; }

    /// <summary>Wavelength of the first octave along z, in metres.</summary>
    public double WavelengthZM { get; init; }

    /// <summary>
    /// Frequency ratio between octaves. Deliberately <b>not</b> exactly 2: at exactly 2 the lattices of successive octaves
    /// coincide on the same integer posts and the axis-aligned structure of value noise reinforces into a visible grid.
    /// </summary>
    public double Lacunarity { get; init; }

    /// <summary>Amplitude ratio between octaves. ~0.5 gives the usual 1/f spectrum.</summary>
    public double Gain { get; init; }

    /// <summary>
    /// Ridged mode: each octave becomes <c>(1 − |2n − 1|)²</c>, which folds the noise about its midline and squares the
    /// fold. The creases become ridge lines and the squaring flattens the basins between them — the mesa-and-canyon look.
    /// </summary>
    public bool Ridged { get; init; }
}
