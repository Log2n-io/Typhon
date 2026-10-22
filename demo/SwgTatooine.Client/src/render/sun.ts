import { Vector3 } from '@babylonjs/core/Maths/math.vector';

/**
 * The one sun this client lights everything by.
 *
 * <b>There were three, and they disagreed.</b> The entity meshes shaded against `(-0.45, -0.8, -0.4)`, the ground
 * shader against a hard-coded `(0.42, 0.84, 0.34)` pointing the other way (a direction TO the sun, not of travel), and
 * the published assets' `DirectionalLight` against `(-0.6, -0.8, 0.7)` — which is lit from the opposite side in Z. That
 * was invisible while nothing cast a shadow: three sets of surfaces, each self-consistent, and nobody compares a dune's
 * brightness with a wall's. A shadow makes it visible immediately, because a building's shadow would fall on the
 * north side of the ground that the ground itself shades as south-facing.
 *
 * The entities' and the terrain's agreed to within a few degrees, so the asset light is the one that moved: it changes
 * how a facade is lit and nothing about the two things that cover most of the screen.
 *
 * <b>The direction light TRAVELS</b>, as Babylon's `DirectionalLight.direction` means it — so the vector points down and
 * a surface facing the sun has a negative dot with it. About 55 degrees of elevation from the north-west.
 */
export const SUN_DIRECTION = new Vector3(-0.45, -0.8, -0.4).normalize();

/**
 * How bright the sun is, and how bright the sky it is balanced against.
 *
 * <b>These three are one decision and they live together because the RATIO is what matters.</b> Split across two files
 * they drift, and a drift here is not a bug anyone can see in a diff: it is the desert slowly losing its shadows.
 *
 * The sky is {@link SceneLighting.setSky}'s irradiance on a surface facing straight up, as a multiple of the planet
 * palette's own sky colour; the bounce is what a surface facing DOWN gets back from the sand, as a fraction of that.
 * Together they are the diffuse half of the environment — the half that used to be a `0.66` constant in the ground
 * shader, invisible to every building in the scene.
 *
 * <b>Chosen by measurement, against the look that came before the port.</b> Over the same patch of open ground, the
 * same avatar shadow and the same 1 400 x 900 frame:
 *
 * | lighting                                  | lit sand        | shadow / lit | R − B |
 * |-------------------------------------------|-----------------|--------------|-------|
 * | the old hand-rolled `0.66 + 0.5 * lambert`| (173, 154, 125) | 0.50         | 48    |
 * | PBR on the stock probe, sun 1.5           | (192, 174, 168) | 0.72         | 24    |
 * | <b>PBR on the planet's sky, these</b>     | (170, 149, 118) | 0.46         | 52    |
 *
 * The middle row is why this exists. The probe shipped with the asset pipeline is a studio environment and its diffuse
 * harmonics are BLUE-dominant — measured, from the file: a flat term of (0.547, 0.483, 0.621) and a sky term of
 * (0.21, 0.23, 0.41). On a horizontal desert it supplied 69 % of the light, which cost the sand half its warmth and
 * flattened every shadow. A world whose clear colour and haze are a warm dust sky cannot be lit by a blue one.
 */
export const SUN_INTENSITY = 2.2;

/** @see {@link SUN_INTENSITY} — the sky's irradiance, as a multiple of the palette's sky colour. */
export const SKY_IRRADIANCE = 0.5;

/** @see {@link SUN_INTENSITY} — what the ground bounces back up, as a fraction of {@link SKY_IRRADIANCE}. */
export const GROUND_BOUNCE = 0.35;

/**
 * Half the edge of the ground square the sun's shadow map covers, in metres.
 *
 * <b>A slab around the camera, not the world.</b> Tatooine is 16 km across and a map that covered it would put a metre
 * of ground in a texel; `autoUpdateExtends`, which fits the frustum to the casters, is just as wrong here because the
 * casters are every building the session holds — a whole city — and the result is the same texel size with the added
 * property of changing every time a structure streams in.
 *
 * 64 m is a little under the distance at which a building stops being worth its geometry, so the slab covers what is
 * drawn as a real mesh and nothing beyond it. With {@link SHADOW_MAP_SIZE} that is 3.1 cm of ground per texel, which is
 * where a doorway's shadow stops looking like a staircase.
 */
export const SHADOW_RADIUS_M = 64;

/**
 * The sun's shadow map, per side.
 *
 * <b>4 096, which is what the testbench's sun uses</b>, over a slab a third the size of its viewer scene. Together
 * those give 3.1 cm of ground per texel: at 2 048 over 96 m it was 9.4 cm, and a 9 cm step is plainly visible in the
 * edge of a shadow two metres long — the first live frames had the texel grid in them.
 */
export const SHADOW_MAP_SIZE = 4096;

/**
 * How far back along the sun's own direction its shadow camera sits, in metres.
 *
 * It has to clear the tallest thing that may cast into the slab — the shuttleport's dome — with room for the ground to
 * rise under it, because anything nearer the light than the near plane is simply not in the map and stops casting.
 *
 * <b>120, not 220.</b> The backoff sets the depth range the shadow pass has to encode, and every metre of it is
 * precision spent on empty sky: the Babylon forum's own terrain example keeps `shadowMaxZ` at 100 with the comment
 * "KEEP THIS LOW TO AVOID STRIPES". 120 clears a 15 m dome above a 64 m slab with room to spare.
 */
export const SHADOW_BACKOFF_M = 120;

/**
 * The near and far planes of the sun's shadow camera, in metres.
 *
 * <b>Sized to the slab and nothing more.</b> A point in the slab sits at most its own diagonal from the centre
 * (64·√2 ≈ 91 m) and at most a building's height above it, so the depth the pass must encode runs from about
 * {@link SHADOW_BACKOFF_M} − 91 − 15 to {@link SHADOW_BACKOFF_M} + 91. The range was 347 m for a slab that needs 210,
 * which is a third of the encoded range spent on nothing.
 *
 * <b>The gain is real but modest here, and worth saying plainly</b>: this generator writes a FLOAT map, which has
 * precision to spare, so tightening the range buys a better-conditioned bias rather than rescuing a 24-bit depth
 * buffer from banding. It is the free half of the fix; the other half is the terrain moving onto a Babylon material.
 */
export const SHADOW_NEAR_M = 5;
export const SHADOW_FAR_M = 230;
