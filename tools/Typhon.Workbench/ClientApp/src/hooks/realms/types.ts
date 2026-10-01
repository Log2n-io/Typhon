/**
 * The realm catalog as `/api/sessions/{id}/realms` answers it (#1083).
 *
 * Hand-written rather than generated, following `useArchetypeList`: the shape is small and stable, and a raw
 * `customFetch` keeps this rung free of an Orval regen. The OpenAPI document is regenerated once when the item lands.
 *
 * **The nullable fields are the ones a database FILE cannot answer**, and they are null rather than zero for a reason
 * the server states at length: a realm the application did not register is given an invented default policy at open, so
 * a divisor and a run state are sitting there to be read that nobody chose. Rendering them would be rendering a default
 * as a decision.
 */
export interface RealmGrid {
  readonly minX: number;
  readonly minY: number;
  readonly minZ: number;
  readonly maxX: number;
  readonly maxY: number;
  readonly maxZ: number;
  readonly cellSize: number;
  readonly migrationHysteresisRatio: number;
  /** Three-dimensional: more than one cell along Z. A flat realm's Z extent is exactly one cell, not zero. */
  readonly deep: boolean;
}

/** Where a row came from: realm 0's own grid record, a persisted catalog row, or the live realm table. */
export type RealmSource = 'grid' | 'catalog' | 'registered';

/** The catalog's lifecycle word for a realm, across opens. */
export type RealmLifecycle = 'live' | 'closing' | 'retired' | 'unknown';

export interface Realm {
  readonly id: number;
  /** Which incarnation of the id this is: identity is the pair `(id, generation)`. */
  readonly generation: number;
  readonly source: RealmSource;
  /** Whether the engine holds this realm in its realm table right now — a different question from `lifecycle`. */
  readonly registered: boolean;
  readonly lifecycle: RealmLifecycle;
  readonly grid: RealmGrid;
  /** `Active` / `Simulated` / `Dormant` / `Closing` / `Divided`, or empty when the session cannot know. */
  readonly runState: string;
  readonly divisor: number | null;
  readonly sessions: number | null;
  /** The replication kind the application declared. Empty when unknown — it is not persisted. */
  readonly kind: string;
}

export interface RealmList {
  /** The id space a caller should consider: valid ids are `[0, maxRealms)`. */
  readonly maxRealms: number;
  readonly realms: Realm[];
  /** Whether `runState`, `divisor` and `sessions` are answerable at all in this session. */
  readonly liveState: boolean;
  /** Why the live half is absent, to show verbatim. Empty when `liveState` is true. */
  readonly liveStateReason: string;
  /** How many rows came from the persisted catalog, as opposed to the realm table or realm 0's grid record. */
  readonly catalogued: number;
}

/** A realm's display name. Realm 0 is the primary world and says so; the rest are their id, plus their kind when known. */
export function realmLabel(realm: Realm): string {
  if (realm.id === 0) {
    return realm.kind ? `Realm 0 — primary (${realm.kind})` : 'Realm 0 — primary';
  }

  return realm.kind ? `Realm ${realm.id} — ${realm.kind}` : `Realm ${realm.id}`;
}

/** A world coordinate, trimmed: integers stay integers, everything else gets one decimal. */
export function realmCoord(n: number): string {
  return Number.isInteger(n) ? String(n) : n.toFixed(1);
}

/**
 * One axis of a realm's world bounds, as `min … max`.
 *
 * **A range rather than a size, because a realm is placed as well as sized.** An interior at `0 … 64` is *not* centred
 * on the origin the way a planet is, and "64" alone would hide that. Reading the two numbers as a range also needs the
 * axis named beside it — an unlabelled `-8192, -8192 … 8192, 8192` is a puzzle, which is what this replaced.
 */
export function realmAxis(min: number, max: number): string {
  return `${realmCoord(min)} … ${realmCoord(max)}`;
}

/**
 * A realm's bounds as one labelled line, for places with a single line to spend (the command palette's sublabel).
 *
 * Surfaces with room use {@link realmAxis} per axis in their own column instead, so the numbers line up down the list.
 */
export function realmExtent(grid: RealmGrid): string {
  const axes = `X ${realmAxis(grid.minX, grid.maxX)} · Y ${realmAxis(grid.minY, grid.maxY)} · Z ${realmAxis(grid.minZ, grid.maxZ)}`;
  return `${axes} · cell ${realmCoord(grid.cellSize)}${grid.deep ? ' · 3D' : ''}`;
}
