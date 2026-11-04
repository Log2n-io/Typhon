import type { ContextMenuItem } from '../state/ui-store';
import type { InventoryItem } from './items';
import { CITIES } from './world-data';

/**
 * A character's installations (crafting-build.md § 10, C5a), as `GET /swg/installations` lists them: where each stands, what it draws, its rate and
 * hopper, what it holds now, its state and how long its upkeep lasts; and the lots used of the character's 10.
 */

/** An installation's state, as the server's `InstallationState` numbers it. */
export const InstallationState = { Running: 1, Full: 2, Unpaid: 3 } as const;

/** The vendor's extractor kit, as `ItemCatalog.Extractor` numbers it. */
export const EXTRACTOR_TEMPLATE = 70;

/** The families an extractor draws, by template (the vendor's bulk materials), with their names. */
export const EXTRACTOR_FAMILIES: readonly { readonly template: number; readonly name: string }[] = [
  { template: 1, name: 'Ore' },
  { template: 2, name: 'Water' },
  { template: 3, name: 'Metal' },
  { template: 4, name: 'Crystal' },
  { template: 5, name: 'Gas' },
  { template: 6, name: 'Chemical' },
  { template: 7, name: 'Fiber' },
];

/** How close a character stands to collect, pay or pick up, metres — the server's `Installations.ReachM`. */
export const INSTALLATION_REACH_M = 30;

export interface Installation {
  readonly id: string;
  readonly kind: number;
  readonly family: number;
  readonly familyName: string;
  readonly x: number;
  readonly z: number;
  /** Units a running hour, and the hopper's size. */
  readonly rate: number;
  readonly hopper: number;
  /** Units it holds now. */
  readonly held: number;
  readonly state: number;
  /** Seconds of upkeep left. */
  readonly paidSeconds: number;
}

export interface InstallationList {
  readonly installations: readonly Installation[];
  readonly lots: number;
  readonly lotsAllowed: number;
  readonly upkeepPerHour: number;
}

/** The endpoint's answer checked: its list, `null` for anything else. */
export function parseInstallations(answer: unknown): InstallationList | null {
  if (typeof answer !== 'object' || answer === null) {
    return null;
  }

  const a = answer as Record<string, unknown>;
  if (!Array.isArray(a.installations) || typeof a.lots !== 'number' || typeof a.lotsAllowed !== 'number') {
    return null;
  }

  return {
    installations: a.installations.filter(isInstallation),
    lots: a.lots,
    lotsAllowed: a.lotsAllowed,
    upkeepPerHour: typeof a.upkeepPerHour === 'number' ? a.upkeepPerHour : 100,
  };
}

function isInstallation(value: unknown): value is Installation {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const i = value as Record<string, unknown>;
  return (
    typeof i.id === 'string' &&
    typeof i.family === 'number' &&
    typeof i.x === 'number' &&
    typeof i.z === 'number' &&
    typeof i.rate === 'number' &&
    typeof i.hopper === 'number' &&
    typeof i.held === 'number' &&
    typeof i.state === 'number' &&
    typeof i.paidSeconds === 'number'
  );
}

/** What a state reads as. */
export function stateText(state: number): string {
  return state === InstallationState.Running
    ? 'running'
    : state === InstallationState.Full
      ? 'hopper full'
      : 'unpaid — stopped';
}

/** Upkeep left as a person reads it: `2 h 15 min`, `40 min`, `none`. */
export function paidText(seconds: number): string {
  if (seconds <= 0) {
    return 'none';
  }

  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  return h > 0 ? `${String(h)} h ${String(m)} min` : `${String(Math.max(1, m))} min`;
}

/** Whether a point is within reach of an installation. */
export function withinReach(i: Installation, x: number, z: number): boolean {
  const dx = i.x - x;
  const dz = i.z - z;
  return dx * dx + dz * dz <= INSTALLATION_REACH_M * INSTALLATION_REACH_M;
}

/** The upkeep a menu entry buys, hours. */
export const PAY_HOURS = [1, 8] as const;

/** How far an installation's marker may stand from where the list says it is and still be that installation, metres: one per 64 m lot. */
export const MARKER_MATCH_M = 2;

/** The compass point of a bearing from north, clockwise, in eighths. */
const COMPASS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'] as const;

/**
 * Where a structure stands, as a person reads it (client.md C42): its distance and direction from the nearest city's centre — `2.3 km NE of Mos
 * Eisley` — and its coordinates.
 */
export function locationText(x: number, z: number): string {
  let city = CITIES[0];
  let best = Infinity;
  for (const c of CITIES) {
    const d = Math.hypot(c.x - x, c.z - z);
    if (d < best) {
      city = c;
      best = d;
    }
  }

  const point = COMPASS[(Math.round(Math.atan2(x - city.x, z - city.z) / (Math.PI / 4)) + 8) % 8];
  const far = best >= 1000 ? `${(best / 1000).toFixed(1)} km` : `${String(Math.round(best))} m`;
  return `${far} ${point} of ${city.name} · ${String(Math.round(x))}, ${String(Math.round(z))}`;
}

/** What a structure is called in its list and its menu: `Extractor · Ore`. */
export function installationName(i: Installation): string {
  return `Extractor · ${i.familyName}`;
}

/** The character's own installation a marker at (`x`, `z`) draws, or `null`: another player's, or one the list has not read yet. */
export function installationAt(list: InstallationList | null, x: number, z: number): Installation | null {
  for (const i of list?.installations ?? []) {
    if (Math.hypot(i.x - x, i.z - z) <= MARKER_MATCH_M) {
      return i;
    }
  }

  return null;
}

/**
 * An installation's context menu (client.md C42): what it holds and how long it runs under its name; Collect when it holds anything, the upkeep
 * {@link PAY_HOURS} buy, and Pick up when it is empty — the server takes each within {@link INSTALLATION_REACH_M} of it, and the menu is only offered
 * there.
 */
export function installationMenu(
  i: Installation,
  upkeepPerHour: number,
): { readonly title: string; readonly detail: string; readonly items: ContextMenuItem[] } {
  const items: ContextMenuItem[] = [];
  if (i.held > 0) {
    items.push({
      label: `Collect ${String(i.held)} ${i.familyName}`,
      hint: 'Its hopper into your inventory, as Mk I units; what does not fit stays in it',
      action: { kind: 'installCollect', installation: i.id },
    });
  }

  for (const h of PAY_HOURS) {
    const credits = h * upkeepPerHour;
    items.push({
      label: `Pay ${String(h)} h · ${credits.toLocaleString('en-US')} cr`,
      hint: 'Running time bought ahead; it makes nothing while unpaid',
      action: { kind: 'installPay', installation: i.id, credits },
    });
  }

  if (i.held === 0) {
    items.push({
      label: 'Pick up',
      hint: 'Back into its kit, in your inventory; what upkeep is left is not refunded',
      action: { kind: 'installPickUp', installation: i.id },
    });
  }

  return {
    title: installationName(i),
    detail: `${String(i.held)} / ${String(i.hopper)} · ${stateText(i.state)} · upkeep ${paidText(i.paidSeconds)}`,
    items,
  };
}

/**
 * What chat says of installations a new reading has that the last did not (client.md C42): one placed from a kit's menu says nothing in a window, and
 * a placement the server took is otherwise silent. `before` null: the session's first reading, which announces nothing.
 */
export function placedNotices(before: InstallationList | null, after: InstallationList): string[] {
  if (before === null) {
    return [];
  }

  const known = new Set(before.installations.map((i) => i.id));
  return after.installations
    .filter((i) => !known.has(i.id))
    .map(
      (i) => `${installationName(i)} placed, ${locationText(i.x, i.z)}: hold on it to pay its upkeep, and it starts`,
    );
}

/** The kit menu's entries (client.md C42): placing it here, one entry per family it can draw. Empty for anything that is not a kit. */
export function kitMenuItems(item: InventoryItem): ContextMenuItem[] {
  if (item.template !== EXTRACTOR_TEMPLATE || item.slot > 100 || item.count > 1) {
    return [];
  }

  return EXTRACTOR_FAMILIES.map((f) => ({
    label: `Place here · ${f.name}`,
    hint: 'On the 64 m lot you stand in, out of town; it makes nothing until its upkeep is paid',
    action: { kind: 'installPlace', item: item.id, family: f.template },
  }));
}
