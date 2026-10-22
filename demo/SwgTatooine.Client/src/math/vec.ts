/**
 * Small immutable vector and quaternion maths on plain tuples.
 *
 * <b>Plain tuples rather than Babylon's `Vector3` / `Quaternion`, on purpose.</b> Everything that decides where a thing
 * is allowed to be — asset frames, collision shapes, portals, later a character motor — has to run in a Vitest node
 * environment and, eventually, beside the server's own arithmetic. None of it should need an engine; the renderer
 * converts at the edge, with `Vector3.FromArray` / `Quaternion.FromArray`.
 *
 * Quaternions are `[x, y, z, w]` (glTF's order) under the usual Hamilton product. Babylon uses the same algebra and the
 * same component order, so a quaternion here is the same four numbers there.
 *
 * Ported from the asset testbench (`BlenderGenTest/tools/testbench/src/math/vec.ts`), trimmed to what the asset path
 * uses: this client's own 2.5-D arithmetic lives in `render/view-math.ts` and is unaffected.
 */

export type Vec3 = readonly [number, number, number];
export type Quat = readonly [number, number, number, number];

export const IDENTITY: Quat = [0, 0, 0, 1];

export function add(a: Vec3, b: Vec3): Vec3 {
  return [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
}

export function quatMul(a: Quat, b: Quat): Quat {
  const [ax, ay, az, aw] = a;
  const [bx, by, bz, bw] = b;
  return [
    aw * bx + ax * bw + ay * bz - az * by,
    aw * by - ax * bz + ay * bw + az * bx,
    aw * bz + ax * by - ay * bx + az * bw,
    aw * bw - ax * bx - ay * by - az * bz,
  ];
}

/** `q v q*`, expanded — no temporary quaternions on a path that runs per collider. */
export function rotate(q: Quat, v: Vec3): Vec3 {
  const [qx, qy, qz, qw] = q;
  const tx = 2 * (qy * v[2] - qz * v[1]);
  const ty = 2 * (qz * v[0] - qx * v[2]);
  const tz = 2 * (qx * v[1] - qy * v[0]);
  return [
    v[0] + qw * tx + (qy * tz - qz * ty),
    v[1] + qw * ty + (qz * tx - qx * tz),
    v[2] + qw * tz + (qx * ty - qy * tx),
  ];
}

/** The shortest signed way round from one angle to another, wrapped into [-pi, pi]. */
export function shortestAngle(radians: number): number {
  const wrapped = (radians + Math.PI) % (2 * Math.PI);
  return (wrapped < 0 ? wrapped + 2 * Math.PI : wrapped) - Math.PI;
}

/**
 * `current` turned towards `target` by at most `maxStep` radians, the short way round.
 *
 * <b>A body does not change which way it faces instantaneously, and the renderer must not pretend it does.</b> The
 * velocity a client reads is not always locomotion: when a predicted body is corrected, the replicated motion walks it
 * backwards for a tick or two, and a facing taken straight from that vector spins the character through 180 degrees and
 * back. Bounding the rate makes a correction a few degrees of lean instead of an about-face, and costs a real turn only
 * the time a real turn takes.
 *
 * @param current Where the body faces now, radians.
 * @param target Where it is trying to face.
 * @param maxStep The most it may turn this frame, radians. Non-positive snaps.
 * @returns The new facing.
 */
export function turnToward(current: number, target: number, maxStep: number): number {
  const delta = shortestAngle(target - current);
  if (!(maxStep > 0) || Math.abs(delta) <= maxStep) {
    return target;
  }

  return current + Math.sign(delta) * maxStep;
}

/** Rotation of `angle` radians about +Y — Babylon's `rotation.y`, which turns +Z towards +X. */
export function quatFromYaw(angle: number): Quat {
  return [0, Math.sin(angle / 2), 0, Math.cos(angle / 2)];
}
