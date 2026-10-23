import { axisVector, directionToWorld, pointToWorld, type Pose } from '../assets/asset-frame';
import type { Descriptor } from '../assets/descriptor';
import { rotate } from '../math/vec';

/**
 * What the client knows about doors without asking: where a placed asset's door planes are, whether a step went through
 * one, and which way a body faces when it lands on a published spawn.
 *
 * The server decides every crossing (`SimBridge.Portals`). The client predicts it only to start the fade a quarter of a
 * second early — the testbench's look — and reads the spawn's facing to put the camera behind the body on arrival, which
 * the server cannot do: it has no heading to send.
 */

/** A door's mid-plane in the world: the opening's centre, the unit direction it is crossed in, half its width. */
export interface DoorPlane {
  readonly x: number;
  readonly z: number;
  readonly fx: number;
  readonly fz: number;
  readonly half: number;
}

/** Every door of a placed asset, in the world — the same planes the server built from the same descriptor. */
export function doorPlanes(descriptor: Descriptor, pose: Pose): DoorPlane[] {
  return descriptor.portals.map((p) => {
    const c = pointToWorld(pose, p.center);
    const f = directionToWorld(pose, rotate(p.rot, axisVector(p.forward)));
    const length = Math.hypot(f[0], f[2]) || 1;
    return { x: c[0], z: c[2], fx: f[0] / length, fz: f[2] / length, half: p.half[0] };
  });
}

/**
 * Whether a step went through a door, in its direction: `SimBridge.CrossedDoor`, the rule the server applies.
 *
 * @param door The plane.
 * @param fromX Where the step started.
 * @param fromZ The same, north.
 * @param toX Where it ended.
 * @param toZ The same, north.
 */
export function crossedDoor(door: DoorPlane, fromX: number, fromZ: number, toX: number, toZ: number): boolean {
  const d0 = (fromX - door.x) * door.fx + (fromZ - door.z) * door.fz;
  const d1 = (toX - door.x) * door.fx + (toZ - door.z) * door.fz;
  if (d0 >= 0 || d1 < 0) {
    return false;
  }

  const t = d0 / (d0 - d1);
  const px = fromX + (toX - fromX) * t;
  const pz = fromZ + (toZ - fromZ) * t;
  return Math.abs((px - door.x) * -door.fz + (pz - door.z) * door.fx) <= door.half;
}

/** How near a published spawn a body must stand to be taken as having landed on it, metres. */
export const ARRIVAL_RADIUS_M = 0.6;

/**
 * The direction a body faces on a published spawn of a placed asset it stands on, or `null` when it stands on none.
 *
 * @returns The world direction in the ground plane, unit length.
 */
export function spawnFacingAt(
  descriptor: Descriptor,
  pose: Pose,
  x: number,
  z: number,
): readonly [number, number] | null {
  let best: readonly [number, number] | null = null;
  let bestD = ARRIVAL_RADIUS_M;
  for (const s of descriptor.spawns) {
    const p = pointToWorld(pose, s.pos);
    const d = Math.hypot(p[0] - x, p[2] - z);
    if (d <= bestD) {
      const f = directionToWorld(pose, rotate(s.rot, axisVector(s.forward)));
      const length = Math.hypot(f[0], f[2]) || 1;
      best = [f[0] / length, f[2] / length];
      bestD = d;
    }
  }

  return best;
}
