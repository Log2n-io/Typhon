import { placement, pointToWorld, poseToWorld, type Pose } from '../assets/asset-frame';
import type { Descriptor } from '../assets/descriptor';
import { structureYaw } from '../assets/structure-assets';
import type { ContextMenuItem } from '../state/ui-store';

/**
 * Where a shuttleport's ticket terminal is used from: the published `shuttle_ticket` interaction of its `ticket_terminal`
 * child, in the world.
 *
 * <b>The testbench's interaction</b> (`world/interactions.ts`): a point and a radius, and nothing about facing — in
 * range is usable. The point is where the user stands, in front of the terminal; it is the child's interaction placed by
 * the child's pose, which is the port's pose composed with the child's (the same composition `AssetLibrary.instantiate`
 * draws the terminal with, so the point and the terminal on screen cannot disagree).
 */

/** The interaction's kind on the published ticket terminal. */
export const TICKET_INTERACTION = 'shuttle_ticket';

/** How far beyond its published radius the terminal still counts as in reach: the radius is a disc on the stand point. */
export const TICKET_SLACK_M = 0.5;

/** A ticket terminal's stand point in the world, and how near it counts. */
export interface TicketTerminal {
  readonly x: number;
  readonly z: number;
  readonly radiusM: number;
  /**
   * The terminal itself, where it is drawn: the eight corners of its render box in the world, planet coordinates, x y z
   * each — what it is hovered, picked and framed as. `null` for a descriptor without bounds.
   */
  readonly corners: Float64Array | null;
}

/**
 * The ticket terminal of a placed shuttleport, or `null` when the port publishes none.
 *
 * @param port The shuttleport's descriptor.
 * @param terminals The descriptor of each child asset, by id — `ticket_terminal` at least.
 * @param x Where the port stands, east.
 * @param y Its altitude.
 * @param z Where it stands, north.
 * @param quarters Its replicated quarter turn.
 */
export function ticketTerminalOf(
  port: Descriptor,
  terminals: ReadonlyMap<string, Descriptor>,
  x: number,
  y: number,
  z: number,
  quarters: number,
): TicketTerminal | null {
  const pose = placement([x, y, z], structureYaw(quarters));
  for (const child of port.children) {
    const descriptor = terminals.get(child.asset);
    const use = descriptor?.interactions.find((i) => i.kind === TICKET_INTERACTION);
    if (descriptor === undefined || use === undefined) {
      continue;
    }

    const childPose = poseToWorld(pose, child.pos, child.rot);
    const at = pointToWorld(childPose, use.pos);
    return {
      x: at[0],
      z: at[2],
      radiusM: (use.radius ?? 1) + TICKET_SLACK_M,
      corners: cornersOf(childPose, descriptor),
    };
  }

  return null;
}

/** The eight corners of a child's render box in the world, through its pose, or `null` without bounds. */
function cornersOf(pose: Pose, descriptor: Descriptor): Float64Array | null {
  // Typed as always there; a hand-written descriptor (a test's) may still leave it out.
  const bounds = (descriptor.bounds as Descriptor['bounds'] | undefined)?.render;
  if (bounds === undefined) {
    return null;
  }

  const [lo, hi] = bounds;
  const corners = new Float64Array(24);
  for (let i = 0; i < 8; i++) {
    const p = pointToWorld(pose, [
      (i & 1) === 0 ? lo[0] : hi[0],
      (i & 2) === 0 ? lo[1] : hi[1],
      (i & 4) === 0 ? lo[2] : hi[2],
    ]);
    corners[i * 3] = p[0];
    corners[i * 3 + 1] = p[1];
    corners[i * 3 + 2] = p[2];
  }

  return corners;
}

/**
 * The ticket terminal's context menu entries: every other city, each queueing for the next shuttle there.
 *
 * @param from The city whose port the terminal is in, which is not a destination; -1 for none known.
 * @param names Every city's name, by city index.
 */
export function ticketMenuItems(from: number, names: readonly string[]): ContextMenuItem[] {
  const items: ContextMenuItem[] = [];
  names.forEach((name, city) => {
    if (city !== from) {
      items.push({ label: name, hint: `Queue for the next shuttle to ${name}`, action: { kind: 'board', city } });
    }
  });
  return items;
}
