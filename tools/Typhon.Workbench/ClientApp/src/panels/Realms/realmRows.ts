import type { Realm } from '@/hooks/realms/types';
import type { RealmBoardReading, RealmBoardRow } from './realmBoardReading';

/**
 * One row per realm, from whichever sources this session has.
 *
 * **A realm is one object with two halves, and a session may hold either or both.** The persisted catalog carries
 * identity and geometry — id, generation, bounds, cell size, lifecycle — and nothing running. The per-realm trace
 * records carry run state and this tick's spatial work over a database that may not be readable at all. A file with a
 * capture attached, or a file whose holder is being watched, has *both*, and the panel used to show only the first
 * because it chose its body by session kind.
 *
 * Merging them here rather than picking one is what lets a row say "realm 7, a 64 m room at (0,0)…(64,64), simulated
 * every 4th tick, 12 clusters" — which neither source can say alone.
 */
export interface RealmRow {
  readonly id: number;
  /** Catalog identity + geometry, when this session can read the file. */
  readonly catalog: Realm | null;
  /** This tick's run state and spatial work, when this session has telemetry AND the realm reported on that tick. */
  readonly live: RealmBoardRow | null;
}

export interface RealmRowSet {
  readonly rows: RealmRow[];
  /** The tick the live half was read from, or null when there is no telemetry in scope. */
  readonly tickNumber: number | null;
  /** Realms the engine holds that reported nothing on that tick — asleep, not missing. Null without a census. */
  readonly dormantRealms: number | null;
  /** Realms registered with the engine, per the census. Null without one. */
  readonly presentRealms: number | null;
}

/**
 * Union the two sources by realm id, ascending.
 *
 * **The union is deliberate, not a join.** A catalog-only realm is a real realm that happens to be asleep; a
 * telemetry-only realm is a real realm in a session that cannot read the catalog (or one registered at run time and not
 * yet persisted). Dropping either side would hide realms that exist.
 */
export function mergeRealmRows(catalog: readonly Realm[], board: RealmBoardReading | null): RealmRowSet {
  const byId = new Map<number, { catalog: Realm | null; live: RealmBoardRow | null }>();

  for (const realm of catalog) {
    byId.set(realm.id, { catalog: realm, live: null });
  }
  for (const row of board?.rows ?? []) {
    const existing = byId.get(row.realmId);
    if (existing) {
      existing.live = row;
    } else {
      byId.set(row.realmId, { catalog: null, live: row });
    }
  }

  const rows: RealmRow[] = [...byId.entries()]
    .sort((a, b) => a[0] - b[0])
    .map(([id, sides]) => ({ id, catalog: sides.catalog, live: sides.live }));

  return {
    rows,
    tickNumber: board?.tickNumber ?? null,
    // With a catalog in hand the panel LISTS every realm, so "how many am I not showing" is answered by the list
    // itself and the census would be the wrong number to print beside it. It only means something when the rows are
    // the runnable set — i.e. when telemetry is the only source.
    dormantRealms: board != null && catalog.length === 0 ? board.dormantRealms : null,
    presentRealms: board != null && catalog.length === 0 ? board.presentRealms : null,
  };
}

/** How many of the listed realms reported on the sampled tick — the awake count, whatever the row source. */
export function awakeCount(set: RealmRowSet): number {
  let n = 0;
  for (const row of set.rows) {
    if (row.live !== null) {
      n++;
    }
  }
  return n;
}
