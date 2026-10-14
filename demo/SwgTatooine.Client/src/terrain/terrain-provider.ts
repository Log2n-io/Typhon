import { startTerrainBake } from './bake-client';
import { FIELD_ORIGIN_M, POSTS, POST_SPACING_M } from './heightfield';
import { indexedDbStore, terrainCacheKey, type TerrainCacheStore, type TerrainRecord } from './terrain-cache';
import type { TerrainBaked } from './terrain.worker';

/**
 * The one place the planet is obtained, from the cache when there is one and from the workers when there is not.
 *
 * <b>It is started before anything needs it, and that is the point.</b> The field used to be baked by `ClientApp`'s
 * constructor, so it began when the world mounted — after the login form was filled in — and the first two seconds of a
 * session were spent with a flat placeholder standing in for the ground. Everything downstream was wrong for that window:
 * the avatar's altitude is the server's and is correct, the ground under it was not, so the camera's clearance probe read
 * the whole pull-back ray as underground and collapsed third person to first person, 20 m below a plane that was not
 * there. Starting the load at module scope means it runs while the login screen is on display, which on a warm cache is
 * finished before the form is submitted and on a cold one usually finished before the password is typed.
 *
 * <b>Nothing waits for the network and nothing waits for this.</b> The two are independent — the socket opens while the
 * field loads — and only the WORLD waits for both. See `Viewport`.
 */

/** The planet, plus how it was obtained, for the HUD and the tests. */
export interface LoadedTerrain {
  readonly baked: TerrainBaked;
  /** Whether it came from the local database rather than from the workers. */
  readonly fromCache: boolean;
  /** Wall-clock milliseconds this LOAD took: a cache read, or the bake. Distinct from `baked.bakeMs`, which is the bake. */
  readonly loadMs: number;
}

/** What the provider is doing, for a UI that would rather say so than show a spinner with no explanation. */
export type TerrainPhase = 'idle' | 'reading' | 'baking' | 'ready';

export interface TerrainLoadOptions {
  /** Where records live. Defaults to IndexedDB; `null` disables the cache entirely, which tests and a private tab use. */
  readonly store?: TerrainCacheStore | null;
  /** Called as the phase changes, so the UI can say "reading" and then "baking" rather than nothing. */
  readonly onPhase?: (phase: TerrainPhase) => void;
}

function toRecord(key: string, baked: TerrainBaked): TerrainRecord {
  return {
    key,
    posts: baked.posts,
    spacingM: baked.spacingM,
    originM: baked.originM,
    bakeMs: baked.bakeMs,
    height: baked.height,
    nodeError: baked.nodeError,
    nodeMinY: baked.nodeMinY,
    nodeMaxY: baked.nodeMaxY,
    savedAtMs: Date.now(),
  };
}

function toBaked(record: TerrainRecord): TerrainBaked {
  return {
    kind: 'baked',
    height: record.height,
    posts: record.posts,
    spacingM: record.spacingM,
    originM: record.originM,
    bakeMs: record.bakeMs,
    nodeError: record.nodeError,
    nodeMinY: record.nodeMinY,
    nodeMaxY: record.nodeMaxY,
  };
}

/** The bake, as a promise. `startTerrainBake` is callback-shaped because the worker protocol is. */
function bake(): Promise<TerrainBaked> {
  return new Promise((resolve) => {
    startTerrainBake((baked) => {
      resolve(baked);
    });
  });
}

/**
 * Loads the planet once.
 *
 * @param options See {@link TerrainLoadOptions}.
 * @returns The field, and whether it was cached.
 *
 * @remarks
 * <b>A write failure is not a load failure.</b> The record is stored on a best-effort basis after the field is already in
 * hand — a refused quota, a blocked database, a browser with no IndexedDB at all — and none of those stop the session
 * using the field it just computed. The cost of getting that wrong is a session that cannot start on a machine where the
 * uncached path worked perfectly well.
 */
export async function loadTerrain(options: TerrainLoadOptions = {}): Promise<LoadedTerrain> {
  const started = performance.now();
  const phase = options.onPhase ?? (() => undefined);
  const store = options.store === undefined ? indexedDbStore() : options.store;
  const key = terrainCacheKey();

  if (store !== null) {
    phase('reading');
    const found = await store.read(key);
    if (found !== null) {
      phase('ready');
      return { baked: toBaked(found), fromCache: true, loadMs: performance.now() - started };
    }
  }

  phase('baking');
  const baked = await bake();

  // Only what this build would itself produce is stored. A bake whose geometry disagrees with the constants cannot happen
  // today — the workers are given those very constants — but a record that lies about its geometry is the one thing
  // `usable` cannot fully detect later, so it is refused at the door rather than at the read.
  if (
    store !== null &&
    baked.posts === POSTS &&
    baked.spacingM === POST_SPACING_M &&
    baked.originM === FIELD_ORIGIN_M
  ) {
    void store.write(toRecord(key, baked));
  }

  phase('ready');
  return { baked, fromCache: false, loadMs: performance.now() - started };
}

/** The in-flight or finished load, so every caller shares one planet rather than racing two bakes. */
let pending: Promise<LoadedTerrain> | null = null;

/**
 * The shared load, started on the first call and reused by every one after.
 *
 * Called at module scope by `main.tsx` so it is already running during login, and again by the viewport when it needs the
 * result. Two callers must not mean two bakes: that would be 26 s of CPU and two 67 MB fields.
 */
export function sharedTerrain(options: TerrainLoadOptions = {}): Promise<LoadedTerrain> {
  pending ??= loadTerrain(options);
  return pending;
}

/** Drops the shared load. Tests only — nothing in the app has a reason to reload the planet. */
export function resetSharedTerrain(): void {
  pending = null;
}
