import { placement, pointToWorld, poseToWorld, type Pose } from '../assets/asset-frame';
import type { Descriptor, InteractionDesc } from '../assets/descriptor';
import { structureYaw } from '../assets/structure-assets';
import type { ContextAction, ContextMenuItem } from '../state/ui-store';

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

/** The interaction's kind on the published mission terminal (missions.md § 5). */
export const MISSION_INTERACTION = 'mission';

/** The interaction's kind on the published bazaar terminal (bazaar.md § 6; `bazaar_terminal` rev 3, `reach_m` 3.3). */
export const BAZAAR_INTERACTION = 'bazaar';

/** How far beyond its published radius the terminal still counts as in reach: the radius is a disc on the stand point. */
export const TICKET_SLACK_M = 0.5;

/** A ticket terminal's stand point in the world, and how near it counts. */
export interface TicketTerminal {
  /** The stand point: where a body walks to and faces the screen from. */
  readonly x: number;
  readonly z: number;
  /** The stand point's published radius, with {@link TICKET_SLACK_M}. */
  readonly radiusM: number;
  /**
   * Where it is usable from, a disc in plan: about the terminal's origin with its `reach_m` when the asset publishes one
   * (usable from any side), otherwise the stand point's disc. See {@link canUseAt}.
   */
  readonly reachX: number;
  readonly reachZ: number;
  readonly reachM: number;
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
    return terminalAt(childPose, use, cornersOf(childPose, descriptor));
  }

  return null;
}

/**
 * A placed mission terminal's stand point and box, or `null` when its asset publishes no `mission` interaction. The terminal
 * is a structure of its own, not a child: its pose is its placement.
 *
 * @param terminal The mission terminal's descriptor.
 * @param x Where it stands, east.
 * @param y Its altitude.
 * @param z Where it stands, north.
 * @param quarters Its replicated quarter turn.
 */
export function missionTerminalOf(
  terminal: Descriptor,
  x: number,
  y: number,
  z: number,
  quarters: number,
): TicketTerminal | null {
  return standingTerminalOf(terminal, MISSION_INTERACTION, x, y, z, quarters);
}

/**
 * A placed bazaar terminal's stand point and box, or `null` when its asset publishes no `bazaar` interaction: a structure of
 * its own, as a mission terminal is.
 */
export function bazaarTerminalOf(
  terminal: Descriptor,
  x: number,
  y: number,
  z: number,
  quarters: number,
): TicketTerminal | null {
  return standingTerminalOf(terminal, BAZAAR_INTERACTION, x, y, z, quarters);
}

/** A terminal that is a structure of its own: its interaction of `kind` placed by its own pose. */
function standingTerminalOf(
  terminal: Descriptor,
  kind: string,
  x: number,
  y: number,
  z: number,
  quarters: number,
): TicketTerminal | null {
  const use = terminal.interactions.find((i) => i.kind === kind);
  if (use === undefined) {
    return null;
  }

  const pose = placement([x, y, z], structureYaw(quarters));
  return terminalAt(pose, use, cornersOf(pose, terminal));
}

/** A terminal's stand point and use disc in the world, from its interaction placed by `pose` (the asset's own pose). */
function terminalAt(pose: Pose, use: InteractionDesc, corners: Float64Array | null): TicketTerminal {
  const at = pointToWorld(pose, use.pos);
  const radiusM = (use.radius ?? 1) + TICKET_SLACK_M;
  if (use.reach_m === undefined) {
    return { x: at[0], z: at[2], radiusM, reachX: at[0], reachZ: at[2], reachM: radiusM, corners };
  }

  // Usable from any side (ASSET_FORMAT § 5.5): reach_m about the asset's origin, the middle of its base. It already reaches
  // 2.5 m past the terminal's farthest point, so no slack goes on top.
  const origin = pointToWorld(pose, [0, 0, 0]);
  return { x: at[0], z: at[2], radiusM, reachX: origin[0], reachZ: origin[2], reachM: use.reach_m, corners };
}

/** Whether a body standing at (`x`, `z`) can use the terminal: inside its use disc ({@link TicketTerminal.reachM}). */
export function canUseAt(use: TicketTerminal, x: number, z: number): boolean {
  return Math.hypot(use.reachX - x, use.reachZ - z) <= use.reachM;
}

/**
 * What using a mission terminal does: open its list of missions at once, no menu between (Loïc, 2026-10-27). The journal of the
 * missions held is the toolbar's, not the terminal's.
 */
export function missionTerminalAction(terminal: number): ContextAction {
  return { kind: 'missions', terminal };
}

/**
 * The bazaar terminal's context menu entries (bazaar.md § 6): the board of this city or of all, selling, the listings held
 * and the goods waiting.
 *
 * @param reserved How many reservations wait for this character, anywhere; 0 before `/mine` was read.
 */
export function bazaarMenuItems(reserved: number): ContextMenuItem[] {
  return [
    {
      label: 'Browse this city',
      hint: "This city's listings",
      action: { kind: 'bazaar', tab: 'browse', scope: 'here' },
    },
    {
      label: 'Browse all cities',
      hint: 'The listings of every city',
      action: { kind: 'bazaar', tab: 'browse', scope: 'all' },
    },
    { label: 'Sell…', hint: 'List items from your inventory', action: { kind: 'bazaar', tab: 'sell' } },
    {
      label: 'Sell to vendor…',
      hint: 'The vendor buys any loot at a fifth of its value',
      action: { kind: 'bazaar', tab: 'sell' },
    },
    { label: 'My listings', hint: 'What you have listed, to cancel', action: { kind: 'bazaar', tab: 'mine' } },
    {
      label: reserved > 0 ? `Reserved (${String(reserved)})` : 'Reserved',
      hint: 'What you bought, or got back, waiting at a city',
      action: { kind: 'bazaar', tab: 'reserved' },
    },
  ];
}

/**
 * The region (city + 1) of the bazaar terminal usable from (`x`, `z`), or 0 at none: where list, buy, collect and the vendor
 * are allowed, and which reservations can be collected (bazaar.md B5).
 *
 * <b>The terminal's own replicated `region`, never a guess from where it stands.</b> The server files a terminal under its
 * city's region (`WorldIndex.Bazaars`) and refuses a collect elsewhere; a terminal standing nearer another city's centre is
 * still its own city's, so a position-derived city would offer Collect where the server answers `MarketWrongCity`.
 *
 * @param parts The terminals in view; only the bazaar ones count.
 * @param regionOf A structure's replicated `region` field, by netId; 0 for the wild (no city).
 */
export function bazaarRegionAt(
  parts: readonly { readonly kind: string; readonly owner: number; readonly use: TicketTerminal }[],
  x: number,
  z: number,
  regionOf: (owner: number) => number,
): number {
  for (const part of parts) {
    if (part.kind === 'bazaar' && canUseAt(part.use, x, z)) {
      const region = regionOf(part.owner);
      return Number.isInteger(region) && region > 0 ? region : 0;
    }
  }

  return 0;
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

/** How far round a terminal a body counts as crowding it, metres: the queue at its stand point, a step or two deep. */
export const TERMINAL_CROWD_M = 1.5;

/**
 * Whether a body the pick ray met at (`x`, `z`) stands at a terminal — inside its box or its stand disc, widened by
 * {@link TERMINAL_CROWD_M} — so that a ray which goes on to meet the terminal picks the terminal, not the body.
 *
 * <b>Why the terminal wins there.</b> Picking tests bodies before places, which is right in the open and wrong at a busy
 * terminal: at Port Sable's shuttleport hundreds of bots stand at the ticket and mission terminals for seconds each, and
 * every click on the terminal landed on whoever stood in front of it, so neither the shuttle nor the mission list could
 * be opened. A body away from the terminal still wins, and one at it is still picked where the ray misses the terminal.
 *
 * @param x Where the ray met the body, east, planet coordinates.
 * @param z Where it met it, north.
 * @param use The terminal's stand point and radius.
 * @param min The terminal's world box, minimum corner (x y z).
 * @param max Its maximum corner.
 */
export function crowdsTerminal(
  x: number,
  z: number,
  use: TicketTerminal,
  min: readonly number[],
  max: readonly number[],
): boolean {
  const inBox =
    x >= min[0] - TERMINAL_CROWD_M &&
    x <= max[0] + TERMINAL_CROWD_M &&
    z >= min[2] - TERMINAL_CROWD_M &&
    z <= max[2] + TERMINAL_CROWD_M;
  if (inBox) {
    return true;
  }

  const reach = use.radiusM + TERMINAL_CROWD_M;
  const dx = x - use.x;
  const dz = z - use.z;
  return dx * dx + dz * dz <= reach * reach;
}
