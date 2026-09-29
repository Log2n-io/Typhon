import { CITIES, POIS } from '../data/world-data';
import { Heightfield, POST_SPACING_M } from './heightfield';
import { applyLayers, bakeLayers, type TerrainLayer } from './layers';

/**
 * The planet's authored layer tree.
 *
 * **Nothing here is aimed at the real Tatooine.** Loïc, 2026-09-29: *"I dont care about a true replica of the real planet
 * — I just want something with the same level of fidelity for the player."* So the target is the quality bar, not the
 * geography: the regions below are invented to make the demo read well and to put interesting slope where the action is,
 * and `06-gameplay.md` § 7 (original display names, no franchise geography) stands unamended. No SWG data is used;
 * decision **C5** is *"generated ground, never SWG's"*.
 *
 * ## Two rules this tree is written to, both of them measured rather than felt
 *
 * **1. No layer may author a feature narrower than {@link MIN_FEATURE_M}.** A feature narrower than two posts collapses to
 * a single raised post, and bilinear interpolation over one raised post is exactly a four-sided pyramid — which is what the
 * planet was covered in: 5.4 spikes per km², about 1 500 cones, 93 % of them from `cliff detail` alone, whose fourth octave
 * landed at 9.9 m against a 16 m Nyquist limit. Raising the field's resolution did **not** fix it (7.9 spikes per km² at
 * 4 m posts — the layers simply aliased a scale down), which is why the floor is expressed against the post spacing and
 * asserted by `test/terrain-layers.test.ts` rather than left to whoever edits a wavelength next.
 *
 * **2. What the eye reads is amplitude over wavelength, not amplitude.** The first version of this file carried 18 m over
 * a 1 500 m wavelength and measured 7–10 m of relief per 2 km outside the two authored regions — a 0.5 % grade, which the
 * eye reads as a plane. The second raised the amplitude and kept the wavelength, and was still flat. Every layer here
 * therefore states its grade, and no global layer sits under about 3 %.
 *
 * ## The relief bound, and why it moved
 *
 * Total relief is ~450 m over a 16 km map. It was 144 m, held under the 256 m spatial grid cell on the strength of the
 * note in `Ecs/Components.cs:117-120` — *"a third partitioned axis would be one cell deep"*. That argument is about a
 * hypothetical third partitioned axis: **the spatial index is 2D and never reads Y at all**, so the bound was costing real
 * scenery to protect a decision nobody has taken. Loïc lifted it on 2026-09-29. The consequence to keep in view is that
 * terrain rung (b) — a `float Y` on placements — matters more now, not less: the server still believes every entity is at
 * altitude 0, and it is now wrong by up to 450 m instead of 144.
 */

/** Everything is derived from this one number, so the whole planet can be re-rolled by changing it. */
export const TERRAIN_SEED = 0x7a700100;

/**
 * The narrowest feature any layer may author, in metres: **four posts**.
 *
 * Two posts is the Nyquist limit and is not enough — a half-wavelength landing between two posts still reconstructs as a
 * corner. Four is where the measured spike count reaches zero and the curvature ratio settles, and it is the number the
 * octave budget of every fractal layer below is written against.
 */
export const MIN_FEATURE_M = 4 * POST_SPACING_M;

/**
 * The mesa belt: a band across the east, containing Red Mesa, Tallow and Bone Fields. Invented, in flat `x, z` pairs.
 */
const MESA_BELT = [200, 1200, 5200, 900, 7900, 3400, 6600, 6400, 700, 4600] as const;

/** The dune sea: the western basin, containing Dry Wells, Scrap Hold and Raider Fort. */
const DUNE_SEA = [-7800, -1200, -3000, -2400, -1200, 1500, -3600, 6800, -7800, 5200] as const;

/**
 * The Scar: a canyon network across the southern centre, cut **down** rather than raised up.
 *
 * It exists because the south held four settlements on ground with no authored landform at all — Port Sable, Whitestone,
 * Tern's Rest and Old Farm sat on the generic base, which is the "everything outside two regions is dead flat" report. The
 * mechanism is deliberately the inverse of the mesa belt's: ridged noise with a negative amplitude and a positive bias, so
 * the crests become channel floors and the ground between them stays at the level it already had.
 */
const THE_SCAR = [-3000, -2500, 2400, -3300, 3900, -6500, -300, -7700, -3400, -5900] as const;

/**
 * The Broken Highlands: a raised, terraced tableland in the south-west, containing Far Reach, The Citadel and The Sink.
 *
 * Third *kind* of landform rather than a third instance of one: the belt is ridged-and-strata'd at a 1 300 m wavelength,
 * this is ridged-and-strata'd at 1 500 m with a shallower gain, which reads as broader blocks cut by wider gaps.
 */
const BROKEN_HIGHLANDS = [-7900, -7800, -3500, -7500, -2900, -4100, -6300, -2500, -7900, -4300] as const;

/**
 * The landform layers, in application order. The pads are **not** here: a town's pad levels the ground to the height the
 * landform reached at that spot, which is not known until the landform is baked (see {@link padLayers}).
 *
 * The comment on each fractal layer states its **finest octave** and its **grade** (amplitude over base wavelength). Those
 * are the two numbers the two rules at the top of this file are about, and they are the two an edit here has to re-check.
 */
export function landformLayers(seed = TERRAIN_SEED): readonly TerrainLayer[] {
  return [
    {
      // Continental scale: which parts of the planet are high and which are low, so a horizon has somewhere to go. Grade
      // is only 2.9 %, and that is correct for this layer — its job is elevation variety, not visible slope. Finest
      // octave 622 m.
      name: 'continental rise',
      boundary: null,
      filters: [],
      blend: 'add',
      affector: {
        kind: 'fractal',
        amplitudeM: 152,
        biasM: -76,
        fractal: {
          seed: seed ^ 0x11,
          octaves: 4,
          wavelengthXM: 5200,
          wavelengthZM: 4400,
          lacunarity: 2.03,
          gain: 0.5,
          ridged: false,
        },
      },
    },
    {
      // Long swells, so the horizon is never a straight line wherever you stand. Grade 6.2 %, finest octave 68 m. The
      // wavelength came down from 2 400 m at the same time the amplitude went up: at 2 400 m even 46 m was a 1.9 % grade
      // and measured as a plane.
      name: 'base swells',
      boundary: null,
      filters: [],
      blend: 'add',
      affector: {
        kind: 'fractal',
        amplitudeM: 78,
        biasM: -39,
        fractal: {
          seed,
          octaves: 5,
          wavelengthXM: 1250,
          wavelengthZM: 1520,
          lacunarity: 2.07,
          gain: 0.5,
          ridged: false,
        },
      },
    },
    {
      // The mid scale: what you actually walk over. Grade 10.5 %, finest octave 42 m.
      name: 'dune ridges',
      boundary: null,
      filters: [],
      blend: 'add',
      affector: {
        kind: 'fractal',
        amplitudeM: 40,
        biasM: -20,
        warpM: 120,
        warpWavelengthM: 1400,
        fractal: {
          seed: seed ^ 0x2b,
          octaves: 4,
          wavelengthXM: 380,
          wavelengthZM: 480,
          lacunarity: 2.09,
          gain: 0.52,
          ridged: false,
        },
      },
    },
    {
      // Crest lines EVERYWHERE, and the single biggest reason only one region used to read as terrain. Plain fBm is a sum
      // of smooth functions and is therefore smooth at every amplitude — no parameter setting of it produces a crease. The
      // ridged fold does, and until now it was applied in exactly one polygon, so the rest of the planet was blobs however
      // loud they were made. Grade 6.6 %, finest octave 75 m.
      name: 'crest lines',
      boundary: null,
      filters: [],
      blend: 'add',
      affector: {
        kind: 'fractal',
        amplitudeM: 46,
        biasM: -14,
        fractal: {
          seed: seed ^ 0x6f,
          octaves: 4,
          wavelengthXM: 700,
          wavelengthZM: 880,
          lacunarity: 2.11,
          gain: 0.5,
          ridged: true,
        },
      },
    },
    {
      // Domain-warped, so the crests curve the way wind-driven sand does instead of sitting on a lattice. The warp is what
      // separates a dune field from a blob field, and it costs one extra noise evaluation. Grade 7.1 %, finest octave 30 m.
      name: 'dune sea',
      boundary: { kind: 'polygon', points: [...DUNE_SEA], featherM: 900 },
      filters: [],
      blend: 'add',
      affector: {
        kind: 'fractal',
        amplitudeM: 44,
        biasM: -17,
        warpM: 260,
        warpWavelengthM: 2600,
        fractal: {
          seed: seed ^ 0x51,
          octaves: 5,
          wavelengthXM: 620,
          wavelengthZM: 980,
          lacunarity: 2.13,
          gain: 0.52,
          ridged: false,
        },
      },
    },
    {
      // Ridged noise: folding about the midline turns the creases into ridge lines and the squaring flattens the basins
      // between them, which is the mesa-and-canyon silhouette. Grade 18 %, finest octave 68 m.
      name: 'mesa belt',
      boundary: { kind: 'polygon', points: [...MESA_BELT], featherM: 1100 },
      filters: [],
      blend: 'add',
      affector: {
        kind: 'fractal',
        amplitudeM: 236,
        biasM: 0,
        fractal: {
          seed: seed ^ 0xa3,
          octaves: 5,
          wavelengthXM: 1300,
          wavelengthZM: 1150,
          lacunarity: 2.11,
          gain: 0.46,
          ridged: true,
        },
      },
    },
    {
      // Broader blocks and wider gaps than the belt: same mechanism, 1 500 m instead of 1 300 and a shallower gain, which
      // is what makes it a different KIND of place rather than the same one relocated. Grade 10 %, finest octave 85 m.
      name: 'broken highlands',
      boundary: { kind: 'polygon', points: [...BROKEN_HIGHLANDS], featherM: 1000 },
      filters: [],
      blend: 'add',
      affector: {
        kind: 'fractal',
        amplitudeM: 152,
        biasM: 0,
        fractal: {
          seed: seed ^ 0xc5,
          octaves: 5,
          wavelengthXM: 1500,
          wavelengthZM: 1250,
          lacunarity: 2.05,
          gain: 0.48,
          ridged: true,
        },
      },
    },
    {
      // Canyons: the same ridged fold as the belt with the amplitude NEGATED and the bias carrying the surface back up, so
      // the ridge lines become channel floors and the ground between them keeps the height it already had. Subtractive
      // relief is a different silhouette from additive relief and there was none of it anywhere on the planet.
      // Grade −11 %, finest octave 60 m.
      name: 'the scar',
      boundary: { kind: 'polygon', points: [...THE_SCAR], featherM: 850 },
      filters: [],
      blend: 'add',
      affector: {
        kind: 'fractal',
        amplitudeM: -124,
        biasM: 34,
        fractal: {
          seed: seed ^ 0x9d,
          octaves: 5,
          wavelengthXM: 1100,
          wavelengthZM: 1400,
          lacunarity: 2.07,
          gain: 0.5,
          ridged: true,
        },
      },
    },
    {
      // The strata, filtered on SLOPE rather than on absolute height.
      //
      // It used to be a height band (`minM: 8`), which worked only because sea level and "risen ground" were the same
      // thing. `continental rise` broke that: ground can now sit 90 m up and be dead flat, and a height filter would band
      // the plain. Slope is what the layer actually means — terrace the flanks, leave the tops and the flats alone — and
      // it is the same filter mechanism, so this costs nothing.
      //
      // `sharpness` came down from 0.9 because the riser at 0.9 is about a ninth of a step, which on a steep face is under
      // one post wide. That was the other 7 % of the spikes.
      name: 'mesa strata',
      boundary: { kind: 'polygon', points: [...MESA_BELT], featherM: 1100 },
      filters: [{ kind: 'slope', min: 0.1, max: 40, feather: 0.06 }],
      blend: 'replace',
      affector: { kind: 'terrace', stepM: 30, sharpness: 0.6 },
    },
    {
      // Same idea over the highlands, one step shallower so the two regions do not band at the same altitudes.
      name: 'highland strata',
      boundary: { kind: 'polygon', points: [...BROKEN_HIGHLANDS], featherM: 1000 },
      filters: [{ kind: 'slope', min: 0.12, max: 40, feather: 0.07 }],
      blend: 'replace',
      affector: { kind: 'terrace', stepM: 26, sharpness: 0.55 },
    },
    {
      // Rock where the ground is ALREADY steep. This is the mechanism that makes terrain look caused rather than drawn,
      // and it is four lines of configuration over the slope of the layers above.
      //
      // Its wavelength nearly doubled (90 m → 190 m) for one reason: at 90 m over four octaves the finest octave was
      // 9.9 m, below the 16 m the field could carry, and the result was ~1 500 one-post pyramids. The finest octave is the
      // number that matters and it is set by the SHORTER of the two wavelengths — 160 / 2.09³ = 17.5 m, above
      // {@link MIN_FEATURE_M}. Grade 7.9 %.
      name: 'cliff detail',
      boundary: null,
      filters: [{ kind: 'slope', min: 0.35, max: 40, feather: 0.2 }],
      blend: 'add',
      affector: {
        kind: 'fractal',
        amplitudeM: 15,
        biasM: -5,
        fractal: {
          seed: seed ^ 0xd7,
          octaves: 4,
          wavelengthXM: 190,
          wavelengthZM: 160,
          lacunarity: 2.09,
          gain: 0.55,
          ridged: true,
        },
      },
    },
  ];
}

/**
 * The town and landmark pads: a height-**constant** affector inside a feathered circle, which is what SWG did to level its
 * towns (`AHCN` inside a `BCIR` — a terrace affector makes steps, not level ground).
 *
 * The constant is read out of the baked landform at the site's own centre, so a town on a mesa is level on the mesa rather
 * than sunk to sea level. The feather is a fraction of the site radius, so the pad meets the surrounding terrain in a skirt
 * instead of a wall.
 *
 * The **level part reaches 1.1 × the site radius**, not exactly the radius. It used to end exactly there, which meant a
 * building placed at the rim — and `Rng.PointInDisc` places plenty — stood on the first centimetre of the skirt rather
 * than on the pad. Harmless on 144 m of relief and not harmless on 450: the skirt is three times steeper now.
 *
 * @param field The landform, already baked.
 */
export function padLayers(field: Heightfield): readonly TerrainLayer[] {
  const layers: TerrainLayer[] = [];
  for (const site of CITIES) {
    layers.push(padLayer(`pad ${site.name}`, site.x, site.z, site.radius, field));
  }

  for (const site of POIS) {
    layers.push(padLayer(`pad ${site.name}`, site.x, site.z, site.radius, field));
  }

  return layers;
}

function padLayer(name: string, x: number, z: number, radiusM: number, field: Heightfield): TerrainLayer {
  return {
    name,
    // Level out to 1.1 × the radius, then a skirt out to 1.6 ×. The two numbers are one decision: the boundary's feather
    // ramps INWARD from its edge, so the level zone is `radiusM − featherM` and the margin is the difference between them.
    boundary: { kind: 'circle', x, z, radiusM: radiusM * 1.6, featherM: radiusM * 0.5 },
    filters: [],
    blend: 'replace',
    affector: { kind: 'constant', heightM: field.heightAt(x, z) },
  };
}

/**
 * Bakes the whole planet: landform, then pads read from it, then the min/max scan.
 *
 * @param field The field to fill.
 * @param seed Overrides {@link TERRAIN_SEED}; tests use it to show the tree is a function of its seed.
 * @param scratch Reusable weight buffer, to bake twice without allocating twice.
 */
export function bakeTatooine(field: Heightfield, seed = TERRAIN_SEED, scratch?: Float32Array): void {
  // One weight buffer for BOTH stages. The parameter exists to prevent a second allocation and no caller ever passed it,
  // so every bake allocated two 67 MB buffers at the shipping resolution — the exact cost the parameter was added for.
  const weights = scratch ?? new Float32Array(field.grid.posts * field.grid.posts);
  bakeLayers(field.grid, landformLayers(seed), weights);
  applyLayers(field.grid, padLayers(field), weights);
  field.measure();
}
