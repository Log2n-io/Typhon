import { add, quatFromYaw, quatMul, rotate, type Quat, type Vec3 } from '../math/vec';

/**
 * From an asset's glTF frame to the world frame — the one place this conversion is written.
 *
 * <b>The world frame is the server's and the client's</b>: metres, +Y up, ground in X/Z, read by a default
 * (left-handed) Babylon scene with no handedness flag, exactly as SwgTatooine.Client renders it. glTF is right-handed,
 * and Babylon's glTF loader reconciles the two by putting every model under a `__root__` that mirrors X
 * (`rotation.y = π` with `scaling.z = -1`). So a glTF point `(x, y, z)` is drawn at `(-x, y, z)` in the asset's own
 * space, and anything read from a descriptor (colliders, portals, spawns, children) has to take the same mirror, or it
 * would land on the other side of the model from the mesh it describes.
 *
 * A mirror is its own inverse and conjugating by it is a homomorphism (`M(A·B) = M(A)·M(B)`), which is what lets a
 * child asset be placed with its parent's mirrored pose and then mirror its own geometry, without a double flip.
 *
 * For a quaternion, mirroring X keeps the X component and negates Y and Z. Every collision shape of schema 1 (box,
 * sphere, cylinder, capsule) is symmetric under that mirror, so a mirrored collider is the same shape with the
 * mirrored pose.
 *
 * Ported unchanged from the asset testbench (`BlenderGenTest/tools/testbench/src/assets/asset-frame.ts`), which is where
 * it was derived against the real exported assets, together with `test/asset-frame.test.ts`.
 */

/** A rigid pose in the world frame. Placements are translation + yaw (the physics convention), children compose. */
export interface Pose {
  readonly pos: Vec3;
  readonly rot: Quat;
}

/** A world placement: translation plus a turn about +Y (Babylon's `rotation.y`: +Z towards +X). */
export function placement(pos: Vec3, yaw = 0): Pose {
  return { pos, rot: quatFromYaw(yaw) };
}

export function mirrorPoint(p: Vec3): Vec3 {
  return [-p[0], p[1], p[2]];
}

export function mirrorQuat(q: Quat): Quat {
  return [q[0], -q[1], -q[2], q[3]];
}

/** A point given in an asset's glTF frame, in the world. */
export function pointToWorld(pose: Pose, p: Vec3): Vec3 {
  return add(pose.pos, rotate(pose.rot, mirrorPoint(p)));
}

/** A direction given in an asset's glTF frame, in the world (no translation). */
export function directionToWorld(pose: Pose, d: Vec3): Vec3 {
  return rotate(pose.rot, mirrorPoint(d));
}

/** An orientation given in an asset's glTF frame, in the world. */
export function rotationToWorld(pose: Pose, q: Quat): Quat {
  return quatMul(pose.rot, mirrorQuat(q));
}

/** The world pose of something posed `(pos, rot)` in the asset's glTF frame — a child asset, a collider, a portal. */
export function poseToWorld(pose: Pose, pos: Vec3, rot: Quat): Pose {
  return { pos: pointToWorld(pose, pos), rot: rotationToWorld(pose, rot) };
}

/** A descriptor axis name ("+Z", "-Z", ...) as a unit vector in the glTF frame. */
export function axisVector(axis: string): Vec3 {
  const s = axis.startsWith('-') ? -1 : 1;
  switch (axis.slice(-1)) {
    case 'X':
      return [s, 0, 0];
    case 'Y':
      return [0, s, 0];
    default:
      return [0, 0, s];
  }
}

/**
 * The yaw (Babylon `rotation.y`) that turns a model facing `forward` (glTF frame) to face the world `heading`.
 *
 * `heading` is the server's: radians from +X towards +Z. A world direction `(cos h, 0, sin h)`; a turn of `θ` about +Y
 * takes +Z to `(sin θ, 0, cos θ)`. For a model facing glTF +Z (still +Z after the mirror), `θ = π/2 − h`.
 */
export function yawForHeading(heading: number, forward: Vec3 = [0, 0, 1]): number {
  const f = mirrorPoint(forward);
  const modelYaw = Math.atan2(f[0], f[2]);
  return Math.PI / 2 - heading - modelYaw;
}
