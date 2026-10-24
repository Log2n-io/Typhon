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

/**
 * How far from the published spawn it landed on a body may be when the client first sees it, metres.
 *
 * <b>Not "standing on it".</b> A body that comes through a door at a run keeps running: the server lands it on the spawn and
 * moves it on the next tick, and the client's first look at it is a render delay and a tick or two later. Measured at 9.7
 * m/s, it was first seen 1.0 m past the room's entry, and the 0.6 m this used to be found no spawn: the screen stayed black
 * for the whole wait and came back with the door beside the body. A run of 10 m/s over about 0.3 s is 3 m; spawns of
 * different structures stand further apart than this, and the nearest one wins anyway.
 */
export const ARRIVAL_RADIUS_M = 4;

/** A published spawn found near a body: the way it faces, and how far the body is from it. */
export interface SpawnNear {
  readonly facing: readonly [number, number];
  readonly distance: number;
}

/**
 * The published spawn of a placed asset nearest a body, within {@link ARRIVAL_RADIUS_M}, or `null`.
 *
 * @returns Its facing, the world direction in the ground plane, unit length, and its distance from the body.
 */
export function spawnNear(descriptor: Descriptor, pose: Pose, x: number, z: number): SpawnNear | null {
  let best: SpawnNear | null = null;
  for (const s of descriptor.spawns) {
    const p = pointToWorld(pose, s.pos);
    const d = Math.hypot(p[0] - x, p[2] - z);
    if (d <= ARRIVAL_RADIUS_M && (best === null || d < best.distance)) {
      const f = directionToWorld(pose, rotate(s.rot, axisVector(s.forward)));
      const length = Math.hypot(f[0], f[2]) || 1;
      best = { facing: [f[0] / length, f[2] / length], distance: d };
    }
  }

  return best;
}

/** The facing of {@link spawnNear}, or `null`. */
export function spawnFacingAt(
  descriptor: Descriptor,
  pose: Pose,
  x: number,
  z: number,
): readonly [number, number] | null {
  return spawnNear(descriptor, pose, x, z)?.facing ?? null;
}
