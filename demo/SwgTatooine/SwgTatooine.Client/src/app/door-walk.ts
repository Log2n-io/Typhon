import { pointToWorld, type Pose } from '../assets/asset-frame';
import type { Descriptor, NavDesc } from '../assets/descriptor';
import { SpeedClass } from '../data/source';
import { rotate, type Quat, type Vec3 } from '../math/vec';
import type { MoveIntent } from './player-intent';

/**
 * Walking a body through a door the way a program would: to where the door's published path starts, then along it
 * (`ASSET_FORMAT.md` § 5.7) — and out of a room through its navigation to its way out (§ 5.9).
 *
 * <b>The client steers, with the commands a player sends.</b> The server cannot tell this from someone holding W: the
 * walker hands the frame loop a {@link MoveIntent} in place of the keys, so the prediction, the refresh window and the
 * stop at the end are the keyboard's own. A key pressed while it walks takes the body back.
 *
 * <b>Getting to the path's start is open-ground navigation, and this has only a little of it:</b> a straight line, bent
 * round the building it is walking to when the line would cross it. Another building in the way still stops it — which
 * a bot will need a real planner for.
 */

/** A point in the ground plane, world metres. */
export type Waypoint = readonly [number, number];

/** How close counts as at a waypoint, metres. The prediction moves the body locally at once, so this can be tight. */
export const WAYPOINT_REACH_M = 0.35;

/** How far a detour stands off a building's collision bounds, metres: a body's half-extent and a margin. */
export const DETOUR_MARGIN_M = 1.2;

/** How long without getting closer to the next waypoint before the walk gives up, ms. Something is in the way. */
export const STALL_MS = 3000;

/** How close counts as arrived at the end of a walk that stops there (`start(..., stopAtEnd)`), metres. */
export const ARRIVED_M = 0.5;

/** An axis-aligned rectangle in the ground plane, world metres. */
export interface Rect {
  readonly minX: number;
  readonly minZ: number;
  readonly maxX: number;
  readonly maxZ: number;
}

/** The ground-plane bounds of an asset's collision box, placed — the rectangle a detour goes round. */
export function footprintOf(descriptor: Descriptor, pose: Pose): Rect | null {
  const box = descriptor.bounds.collision;
  if (box === null) {
    return null;
  }

  const [lo, hi] = box;
  let minX = Infinity;
  let minZ = Infinity;
  let maxX = -Infinity;
  let maxZ = -Infinity;
  for (const x of [lo[0], hi[0]]) {
    for (const z of [lo[2], hi[2]]) {
      const p = pointToWorld(pose, [x, 0, z]);
      minX = Math.min(minX, p[0]);
      maxX = Math.max(maxX, p[0]);
      minZ = Math.min(minZ, p[2]);
      maxZ = Math.max(maxZ, p[2]);
    }
  }

  return { minX, minZ, maxX, maxZ };
}

/** Whether the segment from `a` to `b` passes through the rectangle's interior (Liang–Barsky). */
export function crosses(a: Waypoint, b: Waypoint, r: Rect): boolean {
  let t0 = 0;
  let t1 = 1;
  const dx = b[0] - a[0];
  const dz = b[1] - a[1];
  const clip = (p: number, q: number): boolean => {
    if (p === 0) {
      return q > 0;
    }

    const t = q / p;
    if (p < 0) {
      if (t > t1) {
        return false;
      }

      t0 = Math.max(t0, t);
    } else {
      if (t < t0) {
        return false;
      }

      t1 = Math.min(t1, t);
    }

    return true;
  };

  return (
    clip(-dx, a[0] - r.minX) &&
    clip(dx, r.maxX - a[0]) &&
    clip(-dz, a[1] - r.minZ) &&
    clip(dz, r.maxZ - a[1]) &&
    t1 - t0 > 1e-6
  );
}

/**
 * The corners to walk round on the way from `from` to `to` so as not to cross `r`: none when the line is clear, else the
 * shortest of going round one corner or two.
 */
export function detour(from: Waypoint, to: Waypoint, r: Rect, margin = DETOUR_MARGIN_M): Waypoint[] {
  const grown: Rect = { minX: r.minX - margin, minZ: r.minZ - margin, maxX: r.maxX + margin, maxZ: r.maxZ + margin };
  if (!crosses(from, to, r)) {
    return [];
  }

  // Corners in order round the rectangle, so neighbours are one side apart.
  const corners: Waypoint[] = [
    [grown.minX, grown.minZ],
    [grown.maxX, grown.minZ],
    [grown.maxX, grown.maxZ],
    [grown.minX, grown.maxZ],
  ];
  const length = (path: readonly Waypoint[]): number => {
    let total = 0;
    for (let i = 1; i < path.length; i++) {
      total += Math.hypot(path[i][0] - path[i - 1][0], path[i][1] - path[i - 1][1]);
    }

    return total;
  };
  const clear = (path: readonly Waypoint[]): boolean => {
    for (let i = 1; i < path.length; i++) {
      if (crosses(path[i - 1], path[i], r)) {
        return false;
      }
    }

    return true;
  };

  let best: Waypoint[] | null = null;
  let bestLength = Infinity;
  for (let i = 0; i < 4; i++) {
    for (const via of [[corners[i]], [corners[i], corners[(i + 1) % 4]], [corners[i], corners[(i + 3) % 4]]]) {
      const path = [from, ...via, to];
      const l = length(path);
      if (l < bestLength && clear(path)) {
        best = via;
        bestLength = l;
      }
    }
  }

  return best ?? [];
}

/** The ground-plane points of a placed asset's first door path, in walking order; empty when it publishes none. */
export function approachOf(descriptor: Descriptor, pose: Pose): Waypoint[] {
  const path = descriptor.portals.length > 0 ? (descriptor.portals[0].approach ?? []) : [];
  return path.map((p) => {
    const w = pointToWorld(pose, p);
    return [w[0], w[2]] as const;
  });
}

/**
 * The way in through a building's door: round the building if it stands between, to where the path starts, then the path.
 *
 * @param descriptor The building's descriptor.
 * @param pose Where it is placed.
 * @param from Where the body stands.
 * @returns The waypoints, and which of them is the first of the path itself; `null` when the building has no path.
 */
export function pathIn(
  descriptor: Descriptor,
  pose: Pose,
  from: Waypoint,
): { points: Waypoint[]; pathStart: number } | null {
  const path = approachOf(descriptor, pose);
  if (path.length < 2) {
    return null;
  }

  const footprint = footprintOf(descriptor, pose);
  const round = footprint === null ? [] : detour(from, path[0], footprint);
  return { points: [...round, ...path], pathStart: round.length };
}

/** A world point in an asset's own glTF frame: the inverse of {@link pointToWorld}. */
export function pointToAsset(pose: Pose, p: Vec3): Vec3 {
  const q = pose.rot;
  const inverse: Quat = [-q[0], -q[1], -q[2], q[3]];
  const local = rotate(inverse, [p[0] - pose.pos[0], p[1] - pose.pos[1], p[2] - pose.pos[2]]);
  return [-local[0], local[1], local[2]];
}

/** The node a body standing at an asset-frame point walks straight to (§ 5.9), searching outwards for a served cell. */
export function navNodeAt(nav: NavDesc, x: number, z: number): number {
  const { origin, size, cols, rows, node } = nav.cells;
  const col = Math.floor((x - origin[0]) / size);
  const row = Math.floor((z - origin[1]) / size);
  const rings = Math.ceil(2 / size);
  for (let ring = 0; ring <= rings; ring++) {
    let best = -1;
    let bestD = Infinity;
    for (let dr = -ring; dr <= ring; dr++) {
      for (let dc = -ring; dc <= ring; dc++) {
        if (Math.max(Math.abs(dr), Math.abs(dc)) !== ring) {
          continue;
        }

        const c = col + dc;
        const r = row + dr;
        if (c < 0 || c >= cols || r < 0 || r >= rows) {
          continue;
        }

        const n = node[r * cols + c];
        const d = dr * dr + dc * dc;
        if (n >= 0 && d < bestD) {
          best = n;
          bestD = d;
        }
      }
    }

    if (best >= 0) {
      return best;
    }
  }

  return -1;
}

/** The shortest edge path between two nodes (Dijkstra, cost = distance), both included; `null` when none exists. */
export function navRoute(nav: NavDesc, from: number, to: number): number[] | null {
  const n = nav.nodes.length;
  const dist = new Array<number>(n).fill(Infinity);
  const prev = new Array<number>(n).fill(-1);
  const done = new Array<boolean>(n).fill(false);
  const neighbours: number[][] = Array.from({ length: n }, () => []);
  for (const [a, b] of nav.edges) {
    neighbours[a].push(b);
    neighbours[b].push(a);
  }

  dist[from] = 0;
  for (let step = 0; step < n; step++) {
    let u = -1;
    for (let i = 0; i < n; i++) {
      if (!done[i] && (u < 0 || dist[i] < dist[u])) {
        u = i;
      }
    }

    if (u < 0 || dist[u] === Infinity) {
      break;
    }

    done[u] = true;
    for (const v of neighbours[u]) {
      const d = dist[u] + Math.hypot(nav.nodes[v][0] - nav.nodes[u][0], nav.nodes[v][2] - nav.nodes[u][2]);
      if (d < dist[v]) {
        dist[v] = d;
        prev[v] = u;
      }
    }
  }

  if (dist[to] === Infinity) {
    return null;
  }

  const route: number[] = [];
  for (let v = to; v >= 0; v = prev[v]) {
    route.unshift(v);
  }

  return route;
}

/**
 * The way out of a room: to the cell's node, along the graph to the node the way out's path starts on, then the path.
 *
 * @param descriptor The room's descriptor.
 * @param pose Where the room is placed — its realm's origin.
 * @param from Where the body stands.
 * @returns The waypoints; `null` when the room publishes no way out. Without navigation, straight to the path.
 */
export function pathOut(
  descriptor: Descriptor,
  pose: Pose,
  from: Waypoint,
): { points: Waypoint[]; pathStart: number } | null {
  const path = descriptor.portals.length > 0 ? (descriptor.portals[0].approach ?? []) : [];
  if (path.length < 2) {
    return null;
  }

  const world = (p: Vec3): Waypoint => {
    const w = pointToWorld(pose, p);
    return [w[0], w[2]];
  };
  const tail = path.map(world);
  const nav = descriptor.nav;
  if (nav === undefined) {
    return { points: tail, pathStart: 0 };
  }

  const local = pointToAsset(pose, [from[0], 0, from[1]]);
  const start = navNodeAt(nav, local[0], local[2]);
  const door = nav.nodes.findIndex((n) => Math.abs(n[0] - path[0][0]) < 0.01 && Math.abs(n[2] - path[0][2]) < 0.01);
  const route = start >= 0 && door >= 0 ? navRoute(nav, start, door) : null;
  if (route === null) {
    return { points: tail, pathStart: 0 };
  }

  // The route ends on the node the path starts on, which the path repeats: drop the duplicate.
  const nodes = route.slice(0, -1).map((i) => world(nav.nodes[i]));
  return { points: [...nodes, ...tail], pathStart: nodes.length };
}

/**
 * Follows a list of waypoints by handing out movement intents, one frame at a time.
 *
 * Done when the realm changes — the door fired — when the last point is reached, or when the body has stopped getting
 * closer for {@link STALL_MS}; the frame loop then sends the stop a released key would.
 */
export class DoorWalker {
  private points: Waypoint[] = [];
  private next = 0;
  private pathStart = 0;
  private realm = -1;
  private bestDistance = Infinity;
  private bestAtMs = 0;
  private stopAtEnd = false;

  /** Whether a walk is under way. */
  get active(): boolean {
    return this.next < this.points.length;
  }

  /** Where it is going next, for an overlay; `null` when idle. */
  get target(): Waypoint | null {
    return this.active ? this.points[this.next] : null;
  }

  /**
   * Starts a walk.
   *
   * @param points The waypoints, in walking order.
   * @param pathStart Which of them starts the door's own path: the walk runs up to it and walks from it.
   * @param realm The realm the body is in, so the crossing ends the walk.
   * @param nowMs When.
   * @param stopAtEnd Stop on the last point rather than walk on through it: a walk TO somewhere (`ClientApp.walkTo`), not
   *   through a door, whose last point is past the plane and must never count as reached.
   */
  start(points: readonly Waypoint[], pathStart: number, realm: number, nowMs: number, stopAtEnd = false): void {
    this.points = [...points];
    this.next = 0;
    this.pathStart = pathStart;
    this.realm = realm;
    this.bestDistance = Infinity;
    this.bestAtMs = nowMs;
    this.stopAtEnd = stopAtEnd;
  }

  /** Ends the walk; the frame loop stops the body if it was moving. */
  cancel(): void {
    this.points = [];
    this.next = 0;
  }

  /**
   * This frame's intent, or `null` once the walk is over.
   *
   * @param x Where the body stands, east.
   * @param z Where it stands, north.
   * @param realm The realm it is in.
   * @param nowMs When.
   */
  intent(x: number, z: number, realm: number, nowMs: number): MoveIntent | null {
    if (!this.active) {
      return null;
    }

    if (realm !== this.realm) {
      this.cancel();
      return null;
    }

    // <b>The last point is never "reached".</b> A door path ends a quarter metre past the plane, and the position this
    // reads is the PREDICTED one, which runs a round trip ahead of the server's: stopping when the prediction got there
    // stopped the server's body short of the plane, and the prediction was then pulled back to it — measured live, a body
    // halted at 3.1 m with the plane at 3.5. The walk ends on the crossing (another realm) or when it stalls; the last leg
    // keeps its own direction so a body on top of the point does not spin.
    const last = this.points.length - 1;
    let [tx, tz] = this.points[this.next];
    let d = Math.hypot(tx - x, tz - z);
    while (d < WAYPOINT_REACH_M && this.next < last) {
      this.next++;
      this.bestDistance = Infinity;
      this.bestAtMs = nowMs;
      [tx, tz] = this.points[this.next];
      d = Math.hypot(tx - x, tz - z);
    }

    if (d < this.bestDistance - 0.05) {
      this.bestDistance = d;
      this.bestAtMs = nowMs;
    } else if (nowMs - this.bestAtMs > STALL_MS) {
      this.cancel();
      return null;
    }

    // Running across open ground, walking the path itself: it climbs a ramp and steps through a doorway.
    if (this.stopAtEnd && this.next === last && d < ARRIVED_M) {
      this.cancel();
      return { heading: Math.atan2(tz - z, tx - x), speedClass: SpeedClass.stop };
    }

    const speedClass = this.next < this.pathStart ? SpeedClass.run : SpeedClass.walk;
    if (this.next === last && last > 0 && d < WAYPOINT_REACH_M) {
      const [px, pz] = this.points[last - 1];
      return { heading: Math.atan2(tz - pz, tx - px), speedClass };
    }

    return { heading: Math.atan2(tz - z, tx - x), speedClass };
  }
}
