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
 * How far the shadow test is allowed to be wrong, in metres, before a surface shadows itself.
 *
 * <b>Slope-scaled, not flat.</b> A flat bias has to be as large as the worst case — ground nearly edge-on to the sun,
 * where one texel spans metres of depth — and that much slack everywhere detaches a shadow from the thing casting it
 * (a body appears to hover). Scaling it by how obliquely the sun strikes the ground pays the large bias only where it
 * is needed. The constant is the perpendicular case.
 */
export const SHADOW_BIAS_M = 0.06;

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
