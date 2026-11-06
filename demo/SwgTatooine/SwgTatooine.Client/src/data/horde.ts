import { cityName } from './market';
import type { Waypoint } from './waypoints';

/** A horde's phase (horde.md § 1), the server's `HordePhase`. */
export const HORDE_PHASES = ['none', 'incoming', 'active', 'won', 'lost', 'ended'] as const;
export type HordePhase = (typeof HORDE_PHASES)[number];

/** A horde's status (horde.md § 4): what every session hears, on change, and what `GET /swg/horde` gives a session that connects after it. */
export interface HordeStatus {
  /** The event's raw id: what an answer names. */
  readonly event: string;
  readonly phase: HordePhase;
  /** The city attacked, by index. */
  readonly city: number;
  /** Committed defenders against the attendance asked for, in 255ths: 255 is enough. */
  readonly help: number;
  /** Seconds to the start when heard, and when it was heard (ms), for the countdown the line counts itself. */
  readonly startsInS: number;
  readonly heardAtMs: number;
  readonly raiders: number;
  readonly committed: number;
  readonly present: number;
  /** The target: where the raiders go and the defenders rally. */
  readonly x: number;
  readonly z: number;
  /** The fortress the raiders come out of: the line's direction and the map's mark. */
  readonly fortX: number;
  readonly fortZ: number;
  /** The town hall the raiders break (horde.md § 10); 0, 0 without one. */
  readonly hallX?: number;
  readonly hallZ?: number;
  /** The town hall's and the fortress's hit points, 1 + 254ths of their whole (1 broken), 0 none. */
  readonly hallHp?: number;
  readonly fortHp?: number;
}

/** Where the raiders are (`HordeRadar`, horde.md § 10): the most crowded clusters, each with its count. */
export interface HordeRadar {
  readonly event: string;
  readonly blips: readonly { readonly x: number; readonly z: number; readonly n: number }[];
}

/** A siege pool as a percentage, or null for none (0 means no data). */
export function siegePercent(v: number | undefined): number | null {
  return v === undefined || !Number.isFinite(v) || v <= 0 ? null : Math.round(((v - 1) / 254) * 100);
}

/** A rally point told to this character (`HordeRally`): committed (1), arrived (2), decayed (3), or the event over (0). */
export interface HordeRally {
  readonly event: string;
  readonly x: number;
  readonly z: number;
  readonly state: number;
}

/** The phase a number names; 'none' for one this client does not know. */
export function phaseOf(n: number): HordePhase {
  return HORDE_PHASES[n] ?? 'none';
}

/** Whether nobody may answer it any more: won, lost or ended. */
export function isOver(phase: HordePhase): boolean {
  return phase === 'won' || phase === 'lost' || phase === 'ended';
}

/** `GET /swg/horde`'s answer, checked; `null` for none or anything else. */
export function parseHordeStatus(json: unknown, nowMs: number): HordeStatus | null {
  const s = json as Record<string, unknown> | null;
  if (s === null || typeof s !== 'object' || typeof s.event !== 'string') {
    return null;
  }

  const num = (v: unknown): number => (typeof v === 'number' && Number.isFinite(v) ? v : 0);
  return {
    event: s.event,
    phase: phaseOf(num(s.phase)),
    city: num(s.city),
    help: num(s.help),
    startsInS: num(s.startsIn),
    heardAtMs: nowMs,
    raiders: num(s.raiders),
    committed: num(s.committed),
    present: num(s.present),
    x: num(s.x),
    z: num(s.z),
    fortX: num(s.fx),
    fortZ: num(s.fz),
    hallX: num(s.hx),
    hallZ: num(s.hz),
    hallHp: num(s.hall),
    fortHp: num(s.fort),
  };
}

const COMPASS = ['east', 'north-east', 'north', 'north-west', 'west', 'south-west', 'south', 'south-east'] as const;

/** Where the raiders come from, seen from the target: the eight compass points (+x east, +z north). */
export function fromDirection(status: Pick<HordeStatus, 'x' | 'z' | 'fortX' | 'fortZ'>): string {
  const degrees = (Math.atan2(status.fortZ - status.z, status.fortX - status.x) * 180) / Math.PI;
  return COMPASS[Math.round((((degrees % 360) + 360) % 360) / 45) % 8] ?? 'east';
}

/** The help still needed, in percent: 100 with nobody committed, 0 once enough have. */
export function helpPercent(status: Pick<HordeStatus, 'help'>): number {
  return Math.round(((255 - Math.min(255, Math.max(0, status.help))) * 100) / 255);
}

/** Seconds left to the start now, counted from when the status was heard. */
export function startsIn(status: Pick<HordeStatus, 'startsInS' | 'heardAtMs'>, nowMs: number): number {
  return Math.max(0, Math.ceil(status.startsInS - (nowMs - status.heardAtMs) / 1000));
}

function clock(s: number): string {
  const m = Math.floor(s / 60);
  const r = s % 60;
  return `${String(m)}:${String(r).padStart(2, '0')}`;
}

/**
 * The HUD's line for a status (horde.md § 6): where, when, how much help is still needed. The city by the client's own name (the original names);
 * index + 1 is a region, as `cityName` counts.
 */
export function hordeLine(status: HordeStatus, nowMs: number): string {
  const where = cityName(status.city + 1) || 'a city';
  switch (status.phase) {
    case 'incoming':
      return `A horde marches on ${where} from the ${fromDirection(status)} in ${clock(startsIn(status, nowMs))} — help needed ${String(helpPercent(status))} %`;
    case 'active': {
      const hall = siegePercent(status.hallHp);
      const fort = siegePercent(status.fortHp);
      const siege = hall === null && fort === null ? '' : ` — town hall ${String(hall ?? 100)} %, fortress ${String(fort ?? 100)} %`;
      return `Raid on ${where} from the ${fromDirection(status)}: ${String(status.raiders)} raiders — help needed ${String(helpPercent(status))} % (${String(status.present)} defending)${siege}`;
    }
    case 'won':
      return `${where} held: the horde is beaten`;
    case 'lost':
      return `${where} fell to the horde`;
    case 'ended':
      return `The raid on ${where} is over`;
    default:
      return '';
  }
}

/** Whether the line offers to answer: a horde under way or announced, help still needed, and this character not committed to it. */
export function canAnswer(status: HordeStatus, rally: HordeRally | null): boolean {
  const committed = rally !== null && rally.event === status.event && (rally.state === 1 || rally.state === 2);
  return (status.phase === 'incoming' || status.phase === 'active') && status.help < 255 && !committed;
}

/** The rally point's colour on the guide: a raid's red. */
export const RALLY_COLOR = 0xe0402a;

/**
 * The guide's point during a horde (Loïc, 2026-11-04: shown to everyone, committing only adds to the count): the rally point told to this character while
 * its answer stands, or else the target of a horde announced or under way — unless the player muted it. `null` with neither.
 */
export function hordeGuide(
  status: HordeStatus | null,
  rally: HordeRally | null,
  mutedEvent: string | null,
): Waypoint | null {
  const told = rallyWaypoint(rally);
  if (told !== null) {
    return told;
  }

  if (status === null || isOver(status.phase) || status.phase === 'none' || mutedEvent === status.event) {
    return null;
  }

  return rallyWaypoint({ event: status.event, x: status.x, z: status.z, state: 1 });
}

/**
 * The rally point as a waypoint, for the guide (client.md C38) to steer by while the commitment stands: never saved, never listed in the window — the
 * server's, not the player's. `null` once it is over or decayed.
 */
export function rallyWaypoint(rally: HordeRally | null): Waypoint | null {
  if (rally === null || (rally.state !== 1 && rally.state !== 2)) {
    return null;
  }

  return {
    id: `rally-${rally.event}`,
    name: 'Rally point',
    kind: 0,
    color: RALLY_COLOR,
    x: rally.x,
    z: rally.z,
    realm: 0,
    installation: '0',
    active: true,
  };
}
