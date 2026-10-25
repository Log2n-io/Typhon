import { CITIES } from '../data/world-data';
import { StructureKind } from '../data/swg-schema';

/**
 * Where the nearest shuttleport, bazaar and mission terminal are, for the navigation pane.
 *
 * <b>From what this session already holds, not from a query.</b> The structures in its served radius (1 024 m) are
 * replicated and evaluated every frame for drawing (`ClientApp.structureSamples`), so the nearest of each kind is a
 * scan of a few hundred records — measured, and shown in the pane — rather than a round trip or an engine query. A
 * shuttleport out of range is still found: every city has one at its centre, and the client ships the cities.
 */

/** The kinds the pane finds, in the order it lists them. */
export const NAV_KINDS: readonly number[] = [
  StructureKind.Shuttleport,
  StructureKind.Bazaar,
  StructureKind.MissionTerminal,
];

/** One structure the scan looks at. */
export interface NavCandidate {
  readonly kind: number;
  readonly x: number;
  readonly z: number;
}

/** The nearest of one kind: where it is, how far, and whether it was seen or only known from the map. */
export interface NavTarget {
  kind: number;
  x: number;
  z: number;
  distance: number;
  /** False when it is a shuttleport known from the city list rather than held: further than the served radius. */
  held: boolean;
}

/**
 * The nearest structure of each of {@link NAV_KINDS} to a point, written into `out` (one record per kind, in that order).
 *
 * @param candidates The structures held, first `count` of them.
 * @param count How many are live.
 * @param fromX The point, east.
 * @param fromZ The point, north.
 * @param out One record per kind, reused; `distance` is `Infinity` for a kind nothing was found for.
 */
export function nearestFacilities(
  candidates: readonly NavCandidate[],
  count: number,
  fromX: number,
  fromZ: number,
  out: NavTarget[],
): void {
  for (let k = 0; k < NAV_KINDS.length; k++) {
    if (out.length <= k) {
      out.push({ kind: NAV_KINDS[k], x: 0, z: 0, distance: Infinity, held: false });
    }

    const target = out[k];
    target.kind = NAV_KINDS[k];
    target.distance = Infinity;
    target.held = false;
  }

  for (let i = 0; i < count; i++) {
    const c = candidates[i];
    const k = NAV_KINDS.indexOf(c.kind);
    if (k < 0) {
      continue;
    }

    const d = Math.hypot(c.x - fromX, c.z - fromZ);
    const target = out[k];
    if (d < target.distance) {
      target.x = c.x;
      target.z = c.z;
      target.distance = d;
      target.held = true;
    }
  }

  // The shuttleport out of range: the city centre it stands at, from the client's own map.
  const port = out[NAV_KINDS.indexOf(StructureKind.Shuttleport)];
  if (!port.held) {
    for (const city of CITIES) {
      const d = Math.hypot(city.x - fromX, city.z - fromZ);
      if (d < port.distance) {
        port.x = city.x;
        port.z = city.z;
        port.distance = d;
      }
    }
  }
}

/**
 * Where a target is relative to the way the camera looks, radians: 0 straight ahead, positive to the right, ±π behind.
 *
 * The eye camera looks along `(sin yaw, cos yaw)` in the ground plane (`player-intent.ts` explains why), and on screen
 * +X is to its right — so a target's bearing is `atan2(dx, dz)` in the same convention, and the difference is what an
 * arrow drawn pointing up must be turned by, clockwise.
 */
export function relativeBearing(fromX: number, fromZ: number, toX: number, toZ: number, yaw: number): number {
  const angle = Math.atan2(toX - fromX, toZ - fromZ) - yaw;
  return Math.atan2(Math.sin(angle), Math.cos(angle));
}

/** The city a point is in or nearest to, by the client's names. */
/** Whether a point is inside one of the cities' discs, from the client's own map. */
export function insideCity(x: number, z: number): boolean {
  return CITIES.some((c) => Math.hypot(c.x - x, c.z - z) < c.radius);
}

export function cityNear(x: number, z: number): string {
  let best = CITIES[0].name;
  let bestD = Infinity;
  for (const city of CITIES) {
    const d = Math.hypot(city.x - x, city.z - z);
    if (d < bestD) {
      best = city.name;
      bestD = d;
    }
  }

  return best;
}

/** What the pane calls a target. */
export function navLabel(target: NavTarget): string {
  switch (target.kind) {
    case StructureKind.Shuttleport:
      return `${cityNear(target.x, target.z)} Shuttleport`;
    case StructureKind.Bazaar:
      return 'Bazaar';
    case StructureKind.MissionTerminal:
      return 'Mission Terminal';
    default:
      return 'Structure';
  }
}
