import { evaluateSlot, MAX_MOTION_STRIDE, type ArchetypeStore } from '@typhondb/client';
import { Placement } from '../data/placement';

/**
 * Finding the shuttleport a player is standing at, from the entities the server sent it.
 *
 * <b>From the replicated view and not from the client's own map data, because the ports are not in it.</b> A city's
 * coordinates are authored content the client ships; which building inside that city is its shuttleport is a draw the
 * world generator made, so the only way to know where one is, is to be sent it. That is deliberate rather than
 * unavoidable: the bot brain locates a port the same way, by looking at what it holds, so the client and the bot are
 * answering one question with one mechanism and a port that moved would move for both.
 *
 * The `region` a port carries is `city + 1`, with 0 for a structure in the wild — a sparse index never files key 0, so
 * that is what "no region" has to be. Callers wanting the city subtract one.
 */

/** `StructureKind.Shuttleport`, as the server's `kind` field spells it. */
export const SHUTTLEPORT_KIND = 2;

/** A shuttleport the avatar is near. */
export interface NearbyShuttleport {
  /** The city it serves, already decoded from the wire's `region = city + 1`. */
  readonly city: number;
  readonly x: number;
  readonly z: number;
  /** How far the avatar is from it, in metres. */
  readonly distanceM: number;
}

/**
 * The nearest shuttleport within `withinM` of a point, or null.
 *
 * @param store The `WorldObject` store, or null before one is bound.
 * @param tick The render tick, as the packer evaluates slots at.
 * @param frac The render fraction.
 * @param x Where the avatar is, east.
 * @param z Where the avatar is, north.
 * @param withinM How far to look, in metres.
 * @returns The nearest port, or null when none is in range — including when the server does not replicate `kind`.
 */
export function nearestShuttleport(
  store: ArchetypeStore | null,
  tick: number,
  frac: number,
  x: number,
  z: number,
  withinM: number,
): NearbyShuttleport | null {
  if (store === null) {
    return null;
  }

  // Both are asked for by name and may be absent: a server that does not replicate them cannot be asked to fly
  // anywhere, and that is a server this panel must stay quiet on rather than guess about.
  const kindIndex = store.fieldIndex('kind');
  const regionIndex = store.fieldIndex('region');
  if (kindIndex < 0 || regionIndex < 0) {
    return null;
  }

  const kinds = store.fieldAt(kindIndex);
  const regions = store.fieldAt(regionIndex);
  const live = store.live;
  const count = store.liveCount;
  const dims = store.dims;
  const motion = new Float64Array(MAX_MOTION_STRIDE);
  const at = new Placement();
  const limit = withinM * withinM;

  let best: NearbyShuttleport | null = null;
  let bestSquared = limit;
  for (let i = 0; i < count; i++) {
    const slot = live[i];
    if (kinds[slot] !== SHUTTLEPORT_KIND) {
      continue;
    }

    const region = regions[slot];
    if (region === 0) {
      // A port in no region serves no city, so there is nothing to fly to. The generator makes none, and a client that
      // believed otherwise would offer a destination the server refuses.
      continue;
    }

    evaluateSlot(store, slot, tick, frac, motion, 0);
    at.read(dims, motion, 0);
    const dx = at.x - x;
    const dz = at.z - z;
    const squared = dx * dx + dz * dz;
    if (squared <= bestSquared) {
      bestSquared = squared;
      best = { city: region - 1, x: at.x, z: at.z, distanceM: Math.sqrt(squared) };
    }
  }

  return best;
}
