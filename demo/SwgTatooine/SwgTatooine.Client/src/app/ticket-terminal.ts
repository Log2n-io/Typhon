import { placement, pointToWorld, poseToWorld } from '../assets/asset-frame';
import type { Descriptor } from '../assets/descriptor';
import { structureYaw } from '../assets/structure-assets';

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
    if (use === undefined) {
      continue;
    }

    const at = pointToWorld(poseToWorld(pose, child.pos, child.rot), use.pos);
    return { x: at[0], z: at[2], radiusM: (use.radius ?? 1) + TICKET_SLACK_M };
  }

  return null;
}
