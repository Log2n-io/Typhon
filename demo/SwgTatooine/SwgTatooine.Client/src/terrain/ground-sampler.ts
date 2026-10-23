/**
 * Ground height at a planet coordinate.
 *
 * An interface rather than the `Heightfield` itself, so that everything which has to stand on the ground — instances,
 * attack lines, labels, the eye camera — depends on **one method**, and a test can pass a ramp or a plane without baking a
 * planet.
 *
 * It lives here rather than beside the renderer because the cameras need it too, and a camera has no business importing
 * from `render/`.
 */
export interface GroundSampler {
  heightAt(x: number, z: number): number;
}

/** A planet with no relief: the default everywhere, and what every test that does not care about terrain passes. */
export const FLAT_GROUND: GroundSampler = { heightAt: () => 0 };
