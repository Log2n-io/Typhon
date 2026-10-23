using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SwgTatooine.World.Terrain;

/// <summary>
/// The planet's ground height, baked once per process and shared by everything that needs it.
/// </summary>
/// <remarks>
/// <para>
/// The field is a pure function of the seed and the content scale, and it costs <b>64 MB and about 320 ms</b> (see
/// <see cref="BandBake"/>). A demo run builds one world and a test run builds a hundred, so baking per world would put
/// six gigabytes and half a minute into the test suite for a field every one of them would have agreed on. It is cached
/// per scale instead, for the lifetime of the process.
/// </para>
/// <para>
/// <b>Content scale is a stretch, not a re-authoring.</b> <see cref="SimConfig.ContentScale"/> multiplies every city,
/// landmark and spawn region's coordinates, so a scale of 2 is the same planet twice as wide. The terrain follows by
/// dividing the sample point rather than by baking a bigger field: relief stays 360 m while the world widens, which makes
/// a scaled planet flatter — correct, since the scaled world is a study of density and not of a different geography — and
/// leaves scale 1, the only scale the browser client knows, sampling the authored field exactly.
/// </para>
/// </remarks>
public sealed class TerrainField
{
    private static readonly ConcurrentDictionary<double, TerrainField> Cache = new();

    /// <summary>
    /// The baked field itself, keyed on its <b>seed</b> — which is the only thing the bake reads.
    /// </summary>
    /// <remarks>
    /// Content scale divides the sample point (see <see cref="GroundAt"/>); it never reaches <see cref="BandBake.Bake"/>.
    /// Keying the whole field on the scale therefore bought two byte-identical 64 MB arrays and two 320 ms bakes for
    /// <c>Shared(1)</c> and <c>Shared(2)</c> — and three of each across a sweep, whose <c>SweepWorlds</c> has three
    /// entries. The scale belongs on the wrapper, which is what it multiplies.
    /// </remarks>
    private static readonly ConcurrentDictionary<int, (Heightfield Field, long BakeMs)> Fields = new();

    private readonly double _contentScale;

    private TerrainField(Heightfield field, double contentScale, long bakeMs)
    {
        Field = field;
        _contentScale = contentScale;
        BakeMs = bakeMs;
    }

    /// <summary>The baked field, in authored (unscaled) coordinates.</summary>
    public Heightfield Field { get; }

    /// <summary>Wall-clock milliseconds the bake took, for the startup line.</summary>
    public long BakeMs { get; }

    /// <summary>Lowest ground on the planet, in metres.</summary>
    public float MinHeightM => Field.MinHeightM;

    /// <summary>Highest ground on the planet, in metres.</summary>
    public float MaxHeightM => Field.MaxHeightM;

    /// <summary>
    /// The field for a content scale, baking it on first use and reusing it afterwards.
    /// </summary>
    /// <remarks>
    /// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/> may run the factory more than
    /// once under a race and keeps one result; two identical bakes cost time and no correctness, which is the right trade
    /// against holding a lock across 320 ms of work.
    /// </remarks>
    public static TerrainField Shared(double contentScale)
    {
        return Cache.GetOrAdd(contentScale, static scale =>
        {
            (Heightfield field, long bakeMs) = Fields.GetOrAdd(TatooineTerrain.Seed, static seed =>
            {
                Heightfield baked = new();
                Stopwatch sw = Stopwatch.StartNew();
                BandBake.Bake(baked, seed);
                sw.Stop();
                return (baked, sw.ElapsedMilliseconds);
            });

            return new TerrainField(field, scale, bakeMs);
        });
    }

    /// <summary>
    /// Ground height in metres at a planet coordinate, in the world's own (scaled) coordinates.
    /// </summary>
    /// <remarks>
    /// <b>This is the one call the rest of the demo makes</b>, and it returns a <see cref="float"/> because that is what
    /// a placement stores. The double-precision interpolation happens inside, so the value stored is the correctly
    /// rounded one rather than the result of interpolating between rounded values.
    /// </remarks>
    public float GroundAt(float x, float z) => (float)Field.HeightAt(x / _contentScale, z / _contentScale);

    /// <summary>Forgets every cached field. For tests that want to measure a bake, and for nothing else.</summary>
    public static void ResetForTests()
    {
        Cache.Clear();
        Fields.Clear();
    }
}
