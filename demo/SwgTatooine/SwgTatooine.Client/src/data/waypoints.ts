/**
 * Waypoints on the client (client.md C38): the player's own, as `GET /swg/waypoints` lists them; the colour each kind is drawn in; the named colours
 * `/way` takes; and `/way`'s arguments read.
 */

/** What a waypoint is, for its colour: a mission's lair, a city's structure, a player's structure, or a point of the player's own. */
export type WaypointGroup = 'mission' | 'city' | 'structure' | 'custom';

/** The colour of each group (C38): a waypoint the player coloured is drawn in its own. */
export const GROUP_COLORS: Readonly<Record<WaypointGroup, string>> = {
  mission: '#ff9b3d',
  city: '#4fc3f7',
  structure: '#66bb6a',
  custom: '#ffd54f',
};

/** The colours `/way` names (`#red` …), as `0xRRGGBB`. */
export const NAMED_COLORS: Readonly<Record<string, number>> = {
  red: 0xff5252,
  orange: 0xff9b3d,
  yellow: 0xffd54f,
  green: 0x66bb6a,
  cyan: 0x4fc3f7,
  blue: 0x448aff,
  purple: 0xb388ff,
  white: 0xffffff,
};

/** The server's kinds (`WaypointKind`). */
export const KIND_CUSTOM = 1;
export const KIND_INSTALLATION = 2;

/** A name's longest, UTF-8 bytes: the server's `Waypoints.NameMaxBytes`. */
export const WAYPOINT_NAME_MAX_BYTES = 32;

/** The most a character keeps, until the server says otherwise. */
export const WAYPOINTS_MAX = 100;

/** One of the player's own waypoints. */
export interface Waypoint {
  readonly id: string;
  readonly name: string;
  readonly kind: number;
  /** `0xRRGGBB`, or 0 for its kind's colour. */
  readonly color: number;
  readonly x: number;
  readonly z: number;
  readonly realm: number;
  /** The installation it pins, or `'0'`. */
  readonly installation: string;
  readonly active: boolean;
}

export interface WaypointList {
  readonly max: number;
  readonly waypoints: readonly Waypoint[];
}

/** The answer of `GET /swg/waypoints`, checked; `null` for anything else. A row that is not a waypoint is dropped on its own. */
export function parseWaypoints(json: unknown): WaypointList | null {
  if (typeof json !== 'object' || json === null) {
    return null;
  }

  const o = json as Record<string, unknown>;
  if (!Array.isArray(o.waypoints)) {
    return null;
  }

  const waypoints: Waypoint[] = [];
  for (const row of o.waypoints as unknown[]) {
    const w = row as Record<string, unknown> | null;
    if (
      w !== null &&
      typeof w.id === 'string' &&
      typeof w.name === 'string' &&
      typeof w.kind === 'number' &&
      typeof w.color === 'number' &&
      typeof w.x === 'number' &&
      typeof w.z === 'number'
    ) {
      waypoints.push({
        id: w.id,
        name: w.name,
        kind: w.kind,
        color: w.color,
        x: w.x,
        z: w.z,
        realm: typeof w.realm === 'number' ? w.realm : 0,
        installation: typeof w.installation === 'string' ? w.installation : '0',
        active: w.active === true,
      });
    }
  }

  return { max: typeof o.max === 'number' ? o.max : WAYPOINTS_MAX, waypoints };
}

/** The group a stored waypoint is drawn as: a pin is a player's structure, the rest the player's own. */
export function groupOf(w: Waypoint): WaypointGroup {
  return w.kind === KIND_INSTALLATION ? 'structure' : 'custom';
}

/** `0xRRGGBB` as CSS. */
export function cssColor(rgb: number): string {
  return `#${(rgb & 0xffffff).toString(16).padStart(6, '0')}`;
}

/** The colour a stored waypoint is drawn in: its own, or its group's. */
export function colorOf(w: Waypoint): string {
  return w.color !== 0 ? cssColor(w.color) : GROUP_COLORS[groupOf(w)];
}

/** A colour as `/way` takes it — `#rrggbb`, `#rgb` or a named one — as `0xRRGGBB`; `null` for anything else. 0 (black) is sent as 1. */
export function parseColor(text: string): number | null {
  if (!text.startsWith('#')) {
    return null;
  }

  const body = text.slice(1).toLowerCase();
  if (Object.hasOwn(NAMED_COLORS, body)) {
    return NAMED_COLORS[body];
  }

  const hex = /^[0-9a-f]{3}$/.test(body) ? body.replace(/./g, (c) => c + c) : /^[0-9a-f]{6}$/.test(body) ? body : null;
  if (hex === null) {
    return null;
  }

  // 0 is "the kind's colour" on the wire (0 means no data), so black is the nearest colour that is one.
  return Math.max(1, parseInt(hex, 16));
}

/** UTF-8 bytes of a text. */
function utf8Length(text: string): number {
  return new TextEncoder().encode(text).length;
}

/** `/way`'s arguments: where (none for the selection or the character), the name, the colour (0 for the kind's); or why they cannot be. */
export type WayArgs =
  | {
      readonly ok: true;
      readonly x: number | null;
      readonly z: number | null;
      readonly name: string;
      readonly color: number;
    }
  | { readonly ok: false; readonly error: string };

/** The usage `/way` answers a bad line with. */
export const WAY_USAGE = '/way [x z] <name> [#colour] — no x z: the selection, or where you stand';

/**
 * Reads `/way [x z] <name> [#colour]`: two numbers first are a place; a last word starting with `#` is the colour (`#rrggbb`, `#rgb`, or one of
 * {@link NAMED_COLORS}); what is left is the name, which must be there and fit {@link WAYPOINT_NAME_MAX_BYTES} bytes.
 */
export function parseWayArgs(args: readonly string[]): WayArgs {
  let rest = [...args];
  let x: number | null = null;
  let z: number | null = null;
  const number = /^[-+]?\d+(\.\d+)?$/;
  if (rest.length >= 2 && number.test(rest[0]) && number.test(rest[1])) {
    x = Number(rest[0]);
    z = Number(rest[1]);
    rest = rest.slice(2);
  }

  let color = 0;
  const last = rest.at(-1);
  if (last?.startsWith('#') === true) {
    const parsed = parseColor(last);
    if (parsed === null) {
      return {
        ok: false,
        error: `/way: "${last}" is not a colour — #rrggbb, or #${Object.keys(NAMED_COLORS).join(', #')}`,
      };
    }

    color = parsed;
    rest = rest.slice(0, -1);
  }

  const name = rest.join(' ').trim();
  if (name.length === 0) {
    return { ok: false, error: `Usage: ${WAY_USAGE}` };
  }

  if (utf8Length(name) > WAYPOINT_NAME_MAX_BYTES) {
    return { ok: false, error: `/way: a name is ${String(WAYPOINT_NAME_MAX_BYTES)} characters at most` };
  }

  return { ok: true, x, z, name, color };
}

/** The player's own custom waypoints of a name, case-insensitive: what `/waydel` deletes. Pins are deleted from the character window. */
export function namedWaypoints(list: readonly Waypoint[], name: string): Waypoint[] {
  const wanted = name.trim().toLowerCase();
  return list.filter((w) => w.kind === KIND_CUSTOM && w.name.toLowerCase() === wanted);
}
