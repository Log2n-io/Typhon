using System;
using System.Collections.Generic;

namespace SwgTatooine.World.Terrain;

/// <summary>
/// The planet's authored layer tree, and the bake that turns it into a field.
/// </summary>
/// <remarks>
/// <para>
/// A transcription of <c>demo/SwgTatooine.Client/src/terrain/tatooine-terrain.ts</c>. <b>Every constant below is part of
/// the contract</b>: change one here without changing it there and the server's altitudes stop matching the ground the
/// browser draws. <c>TerrainGoldenChecks</c> holds both to one golden file.
/// </para>
/// <para>
/// Nothing here is aimed at the real Tatooine. Loïc, 2026-09-29: <i>"I dont care about a true replica of the real planet
/// — I just want something with the same level of fidelity for the player."</i> The regions are invented to make the demo
/// read well and to put interesting slope where the action is.
/// </para>
/// <para>
/// Two rules govern the tree, both measured rather than felt. <b>No layer may author a feature narrower than four
/// posts</b>, because anything narrower collapses to a single raised post and bilinear interpolation over one raised post
/// is exactly a four-sided pyramid. And <b>what the eye reads is amplitude over wavelength</b>, not amplitude: two
/// earlier versions carried 130 m of relief on kilometre wavelengths and both looked like a plane.
/// </para>
/// </remarks>
public static class TatooineTerrain
{
    /// <summary>Everything is derived from this one number, so the whole planet can be re-rolled by changing it.</summary>
    public const int Seed = 0x7a700100;

    /// <summary>The mesa belt: a band across the east. Invented, in flat <c>x, z</c> pairs.</summary>
    private static readonly double[] MesaBelt = [200, 1200, 5200, 900, 7900, 3400, 6600, 6400, 700, 4600];

    /// <summary>The dune sea: the western basin.</summary>
    private static readonly double[] DuneSea = [-7800, -1200, -3000, -2400, -1200, 1500, -3600, 6800, -7800, 5200];

    /// <summary>The Scar: a canyon network across the southern centre, cut <b>down</b> rather than raised up.</summary>
    private static readonly double[] TheScar = [-3000, -2500, 2400, -3300, 3900, -6500, -300, -7700, -3400, -5900];

    /// <summary>The Broken Highlands: a raised, terraced tableland in the south-west.</summary>
    private static readonly double[] BrokenHighlands = [-7900, -7800, -3500, -7500, -2900, -4100, -6300, -2500, -7900, -4300];

    /// <summary>
    /// The landform layers, in application order.
    /// </summary>
    /// <remarks>
    /// The pads are <b>not</b> here: a town's pad levels the ground to the height the landform reached at that spot, which
    /// is not known until the landform is baked. See <see cref="PadLayers"/>.
    /// </remarks>
    public static TerrainLayer[] LandformLayers(int seed = Seed)
    {
        return
        [
            // Continental scale: which parts of the planet are high and which are low, so a horizon has somewhere to go.
            // Grade is only 2.9 %, and that is correct for this layer — its job is elevation variety, not visible slope.
            new TerrainLayer
            {
                Name = "continental rise",
                Blend = BlendMode.Add,
                Affector = new TerrainAffector
                {
                    Kind = AffectorKind.Fractal,
                    AmplitudeM = 152,
                    BiasM = -76,
                    Fractal = new FractalSpec
                    {
                        Seed = unchecked(seed ^ 0x11),
                        Octaves = 4,
                        WavelengthXM = 5200,
                        WavelengthZM = 4400,
                        Lacunarity = 2.03,
                        Gain = 0.5,
                        Ridged = false,
                    },
                },
            },

            // Long swells, so the horizon is never a straight line wherever you stand. The wavelength came down from
            // 2 400 m at the same time the amplitude went up: at 2 400 m even 46 m was a 1.9 % grade and measured as a plane.
            new TerrainLayer
            {
                Name = "base swells",
                Blend = BlendMode.Add,
                Affector = new TerrainAffector
                {
                    Kind = AffectorKind.Fractal,
                    AmplitudeM = 78,
                    BiasM = -39,
                    Fractal = new FractalSpec
                    {
                        Seed = seed,
                        Octaves = 5,
                        WavelengthXM = 1250,
                        WavelengthZM = 1520,
                        Lacunarity = 2.07,
                        Gain = 0.5,
                        Ridged = false,
                    },
                },
            },

            // The mid scale: what you actually walk over.
            new TerrainLayer
            {
                Name = "dune ridges",
                Blend = BlendMode.Add,
                Affector = new TerrainAffector
                {
                    Kind = AffectorKind.Fractal,
                    AmplitudeM = 40,
                    BiasM = -20,
                    WarpM = 120,
                    WarpWavelengthM = 1400,
                    Fractal = new FractalSpec
                    {
                        Seed = unchecked(seed ^ 0x2b),
                        Octaves = 4,
                        WavelengthXM = 380,
                        WavelengthZM = 480,
                        Lacunarity = 2.09,
                        Gain = 0.52,
                        Ridged = false,
                    },
                },
            },

            // Crest lines EVERYWHERE, and the single biggest reason only one region used to read as terrain. Plain fBm is
            // a sum of smooth functions and is therefore smooth at every amplitude — no parameter setting of it produces
            // a crease. The ridged fold does, and until now it was applied in exactly one polygon.
            new TerrainLayer
            {
                Name = "crest lines",
                Blend = BlendMode.Add,
                Affector = new TerrainAffector
                {
                    Kind = AffectorKind.Fractal,
                    AmplitudeM = 46,
                    BiasM = -14,
                    Fractal = new FractalSpec
                    {
                        Seed = unchecked(seed ^ 0x6f),
                        Octaves = 4,
                        WavelengthXM = 700,
                        WavelengthZM = 880,
                        Lacunarity = 2.11,
                        Gain = 0.5,
                        Ridged = true,
                    },
                },
            },

            // Domain-warped, so the crests curve the way wind-driven sand does instead of sitting on a lattice. The warp
            // is what separates a dune field from a blob field, and it costs one extra noise evaluation.
            new TerrainLayer
            {
                Name = "dune sea",
                Boundary = new TerrainBoundary { Kind = BoundaryKind.Polygon, Points = DuneSea, FeatherM = 900 },
                Blend = BlendMode.Add,
                Affector = new TerrainAffector
                {
                    Kind = AffectorKind.Fractal,
                    AmplitudeM = 44,
                    BiasM = -17,
                    WarpM = 260,
                    WarpWavelengthM = 2600,
                    Fractal = new FractalSpec
                    {
                        Seed = unchecked(seed ^ 0x51),
                        Octaves = 5,
                        WavelengthXM = 620,
                        WavelengthZM = 980,
                        Lacunarity = 2.13,
                        Gain = 0.52,
                        Ridged = false,
                    },
                },
            },

            // Ridged noise: folding about the midline turns the creases into ridge lines and the squaring flattens the
            // basins between them, which is the mesa-and-canyon silhouette.
            new TerrainLayer
            {
                Name = "mesa belt",
                Boundary = new TerrainBoundary { Kind = BoundaryKind.Polygon, Points = MesaBelt, FeatherM = 1100 },
                Blend = BlendMode.Add,
                Affector = new TerrainAffector
                {
                    Kind = AffectorKind.Fractal,
                    AmplitudeM = 236,
                    BiasM = 0,
                    Fractal = new FractalSpec
                    {
                        Seed = unchecked(seed ^ 0xa3),
                        Octaves = 5,
                        WavelengthXM = 1300,
                        WavelengthZM = 1150,
                        Lacunarity = 2.11,
                        Gain = 0.46,
                        Ridged = true,
                    },
                },
            },

            // Broader blocks and wider gaps than the belt: same mechanism, 1 500 m instead of 1 300 and a shallower gain,
            // which is what makes it a different KIND of place rather than the same one relocated.
            new TerrainLayer
            {
                Name = "broken highlands",
                Boundary = new TerrainBoundary { Kind = BoundaryKind.Polygon, Points = BrokenHighlands, FeatherM = 1000 },
                Blend = BlendMode.Add,
                Affector = new TerrainAffector
                {
                    Kind = AffectorKind.Fractal,
                    AmplitudeM = 152,
                    BiasM = 0,
                    Fractal = new FractalSpec
                    {
                        Seed = unchecked(seed ^ 0xc5),
                        Octaves = 5,
                        WavelengthXM = 1500,
                        WavelengthZM = 1250,
                        Lacunarity = 2.05,
                        Gain = 0.48,
                        Ridged = true,
                    },
                },
            },

            // Canyons: the same ridged fold as the belt with the amplitude NEGATED and the bias carrying the surface back
            // up, so the ridge lines become channel floors and the ground between them keeps the height it already had.
            new TerrainLayer
            {
                Name = "the scar",
                Boundary = new TerrainBoundary { Kind = BoundaryKind.Polygon, Points = TheScar, FeatherM = 850 },
                Blend = BlendMode.Add,
                Affector = new TerrainAffector
                {
                    Kind = AffectorKind.Fractal,
                    AmplitudeM = -124,
                    BiasM = 34,
                    Fractal = new FractalSpec
                    {
                        Seed = unchecked(seed ^ 0x9d),
                        Octaves = 5,
                        WavelengthXM = 1100,
                        WavelengthZM = 1400,
                        Lacunarity = 2.07,
                        Gain = 0.5,
                        Ridged = true,
                    },
                },
            },

            // The strata, filtered on SLOPE rather than on absolute height. A height band worked only while sea level and
            // "risen ground" were the same thing; `continental rise` broke that, and slope is what the layer actually means.
            new TerrainLayer
            {
                Name = "mesa strata",
                Boundary = new TerrainBoundary { Kind = BoundaryKind.Polygon, Points = MesaBelt, FeatherM = 1100 },
                Filters = [new TerrainFilter { Kind = FilterKind.Slope, Min = 0.1, Max = 40, Feather = 0.06 }],
                Blend = BlendMode.Replace,
                Affector = new TerrainAffector { Kind = AffectorKind.Terrace, StepM = 30, Sharpness = 0.08 },
            },

            // Same idea over the highlands, one step shallower so the two regions do not band at the same altitudes.
            new TerrainLayer
            {
                Name = "highland strata",
                Boundary = new TerrainBoundary { Kind = BoundaryKind.Polygon, Points = BrokenHighlands, FeatherM = 1000 },
                Filters = [new TerrainFilter { Kind = FilterKind.Slope, Min = 0.12, Max = 40, Feather = 0.07 }],
                Blend = BlendMode.Replace,
                Affector = new TerrainAffector { Kind = AffectorKind.Terrace, StepM = 26, Sharpness = 0.07 },
            },

            // Rock where the ground is ALREADY steep. This is the mechanism that makes terrain look caused rather than
            // drawn, and it is four lines of configuration over the slope of the layers above.
            new TerrainLayer
            {
                Name = "cliff detail",
                Filters = [new TerrainFilter { Kind = FilterKind.Slope, Min = 0.35, Max = 40, Feather = 0.2 }],
                Blend = BlendMode.Add,
                Affector = new TerrainAffector
                {
                    Kind = AffectorKind.Fractal,
                    AmplitudeM = 9,
                    BiasM = -3,
                    Fractal = new FractalSpec
                    {
                        Seed = unchecked(seed ^ 0xd7),
                        Octaves = 3,
                        WavelengthXM = 472,
                        WavelengthZM = 400,
                        Lacunarity = 2.09,
                        Gain = 0.55,
                        Ridged = true,
                    },
                },
            },
        ];
    }

    /// <summary>
    /// The town and landmark pads: a height-<b>constant</b> affector inside a feathered circle, which is what SWG did to
    /// level its towns (an <c>AHCN</c> inside a <c>BCIR</c> — a terrace affector makes steps, not level ground).
    /// </summary>
    /// <remarks>
    /// The constant is read out of the baked landform at the site's own centre, so a town on a mesa is level on the mesa
    /// rather than sunk to sea level. The level part reaches 1.1 × the site radius, not exactly the radius: a building
    /// placed at the rim used to stand on the first centimetre of the skirt, which is not harmless on 360 m of relief.
    /// <b>The order of the sites is part of the contract</b> — a pad is a <c>replace</c>, so two overlapping pads do not
    /// commute, and the client walks its cities then its landmarks in exactly this order.
    /// </remarks>
    public static TerrainLayer[] PadLayers(Heightfield field)
    {
        ArgumentNullException.ThrowIfNull(field);
        List<TerrainLayer> layers = new(TatooineData.Cities.Length + TatooineData.Pois.Length);
        foreach (CityDef site in TatooineData.Cities)
        {
            layers.Add(PadLayer($"pad {site.Name}", site.X, site.Z, site.Radius, field));
        }

        foreach (PoiDef site in TatooineData.Pois)
        {
            layers.Add(PadLayer($"pad {site.Name}", site.X, site.Z, site.Radius, field));
        }

        return layers.ToArray();
    }

    private static TerrainLayer PadLayer(string name, double x, double z, double radiusM, Heightfield field) => new()
    {
        Name = name,
        Boundary = new TerrainBoundary
        {
            Kind = BoundaryKind.Circle,
            X = x,
            Z = z,
            RadiusM = radiusM * 1.6,
            FeatherM = radiusM * 0.5,
        },
        Blend = BlendMode.Replace,
        Affector = new TerrainAffector { Kind = AffectorKind.Constant, HeightM = field.HeightAt(x, z) },
    };

    /// <summary>Bakes the whole planet: landform, then pads read from it, then the min/max scan.</summary>
    /// <param name="field">The field to fill.</param>
    /// <param name="seed">Overrides <see cref="Seed"/>; tests use it to show the tree is a function of its seed.</param>
    /// <param name="scratch">Reusable weight buffer, to bake twice without allocating twice.</param>
    public static void Bake(Heightfield field, int seed = Seed, float[] scratch = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        // One weight buffer for BOTH stages.
        float[] weights = scratch ?? new float[field.Grid.Posts * field.Grid.Rows];
        TerrainLayers.BakeLayers(field.Grid, LandformLayers(seed), weights);
        TerrainLayers.ApplyLayers(field.Grid, PadLayers(field), weights);
        field.Measure();
    }
}
