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

/** Upkeep left under this, seconds, is said once (client.md C39): an hour to pay before it stops. */
export const UPKEEP_WARN_SECONDS = 3600;

/** The installations already said to be full, and to be running out of upkeep: each said once until it is not any more. */
export interface InstallationAlerts {
  readonly full: ReadonlySet<string>;
  readonly low: ReadonlySet<string>;
}

export const NO_ALERTS: InstallationAlerts = { full: new Set(), low: new Set() };

/**
 * What a new reading of the installations says (client.md C39): each that has just filled its hopper, each whose upkeep has just gone under an hour —
 * once, until it is emptied or paid again — and what is remembered for the next reading.
 */
export function installationAlerts(
  before: InstallationAlerts,
  list: readonly Installation[],
): { readonly notices: readonly string[]; readonly next: InstallationAlerts } {
  const notices: string[] = [];
  const full = new Set<string>();
  const low = new Set<string>();
  for (const i of list) {
    const where = `${String(Math.round(i.x))}, ${String(Math.round(i.z))}`;
    if (i.hopper > 0 && i.held >= i.hopper) {
      full.add(i.id);
      if (!before.full.has(i.id)) {
        notices.push(`Your ${i.familyName} extractor at ${where} is full: collect it, or it makes nothing more`);
      }
    }

    if (i.paidSeconds > 0 && i.paidSeconds < UPKEEP_WARN_SECONDS) {
      low.add(i.id);
      if (!before.low.has(i.id)) {
        notices.push(`Your ${i.familyName} extractor at ${where} has ${paidText(i.paidSeconds)} of upkeep left`);
      }
    }
  }

  return { notices, next: { full, low } };
}
