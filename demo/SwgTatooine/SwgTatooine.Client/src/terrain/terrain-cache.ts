import { CITIES, POIS } from '../data/world-data';
import { FIELD_ORIGIN_M, POSTS, POST_SPACING_M } from './heightfield';
import { landformLayers, TERRAIN_SEED } from './tatooine-terrain';

/**
 * The baked planet, kept in the browser between visits.
 *
 * <b>Why this exists.</b> The field is a pure function of a seed and an authored layer tree, and computing it costs ~13 s
 * of CPU — 2.1 s of wall clock across eight workers. Every page load recomputed it from scratch, which is the same
 * arithmetic producing the same 67 MB every time. Nothing about it is per-session, so it is cached, and the only question
 * a cache has to answer is *when the answer would differ*.
 *
 * <b>IndexedDB, and nothing else was a candidate.</b> `localStorage` is synchronous, string-only and capped at about 5 MB;
 * the Cache API stores responses, not typed arrays. IndexedDB stores a `Float32Array` by structured clone, asynchronously,
 * with a quota measured in hundreds of megabytes. A browser without it, or with it blocked (private mode on some engines,
 * site data disabled), simply never gets a hit — see {@link indexedDbStore}.
 */

/**
 * Bumped by hand when the bake's OUTPUT changes for reasons the key below cannot see.
 *
 * The key digests the authored configuration — the layer tree, the seed, the grid, the pad sites — which is everything a
 * person normally edits. It cannot see a change to the noise hash in `hash.ts`, to how `layers.ts` composes a layer, or to
 * the quadtree the finishing pass measures: those change the bits with the configuration untouched, and a cache that
 * missed one would hand back terrain the server no longer agrees with. That failure is silent and looks like entities
 * floating, which is precisely the class of bug `TerrainGoldenChecks` exists to prevent.
 *
 * So it is pinned rather than trusted: `terrain-cache.test.ts` digests those source files and fails when they change
 * without this number moving. The test prints the digest it computed, so the fix is to bump this and paste that in.
 */
export const TERRAIN_BAKE_VERSION = 1;

/** A baked planet as it is stored. The three quadtree arrays are what `PlanetProfile.refreshTerrain` needs. */
export interface TerrainRecord {
  readonly key: string;
  readonly posts: number;
  readonly spacingM: number;
  readonly originM: number;
  /** What the ORIGINAL bake cost, carried so the HUD can still report it on a cache hit. */
  readonly bakeMs: number;
  readonly height: Float32Array;
  readonly nodeError: Float32Array;
  readonly nodeMinY: Float32Array;
  readonly nodeMaxY: Float32Array;
  readonly savedAtMs: number;
}

/**
 * Where records are kept.
 *
 * An interface rather than IndexedDB directly, because the cache's decisions — what the key is, what counts as a corrupt
 * record, what happens when a write is refused — are the part worth testing, and the test environment is `node`, which has
 * no IndexedDB. The production implementation is {@link indexedDbStore}; tests pass an in-memory one.
 */
export interface TerrainCacheStore {
  read(key: string): Promise<TerrainRecord | null>;
  /** @returns Whether it was stored. A refused write is normal — see {@link indexedDbStore}. */
  write(record: TerrainRecord): Promise<boolean>;
}

/** 64 bits of FNV-1a over a string, as hex. Two 32-bit passes with different mixes, which is enough to key a cache. */
function digest(text: string): string {
  let a = 0x811c9dc5;
  let b = 0x01000193;
  for (let i = 0; i < text.length; i++) {
    const c = text.charCodeAt(i);
    a = Math.imul(a ^ c, 0x01000193) >>> 0;
    b = Math.imul(b ^ c, 0x85ebca6b) >>> 0;
  }

  return a.toString(16).padStart(8, '0') + b.toString(16).padStart(8, '0');
}

/**
 * The key for the terrain the current sources would bake.
 *
 * Everything that changes a post's value is in here: the version above, the seed, the grid's geometry, the whole authored
 * layer tree, and the pad sites — a town that moves re-levels the ground under it, so its position is an input to the bake
 * exactly as a layer's amplitude is.
 *
 * @param seed Overrides {@link TERRAIN_SEED}; only the tests pass one.
 * @param version Overrides {@link TERRAIN_BAKE_VERSION}. A parameter so that a test can show the version actually reaches
 *   the key — without one, dropping it from the descriptor is a change nothing can observe, which is the same silent
 *   failure the version exists to prevent.
 */
export function terrainCacheKey(seed = TERRAIN_SEED, version = TERRAIN_BAKE_VERSION): string {
  const sites = [...CITIES, ...POIS].map((site) => [site.name, site.x, site.z, site.radius]);
  const descriptor = JSON.stringify({
    version,
    seed,
    posts: POSTS,
    spacingM: POST_SPACING_M,
    originM: FIELD_ORIGIN_M,
    layers: landformLayers(seed),
    sites,
  });

  return `terrain-${digest(descriptor)}`;
}

/** The database and store names. Versioned by IndexedDB's own mechanism, which only ever creates the one store. */
const DB_NAME = 'swg-terrain';
const DB_VERSION = 1;
const STORE = 'fields';

/**
 * Whether a record read back is usable.
 *
 * <b>A cache hit is not a promise that the bytes are intact.</b> A record written by an older build, truncated by a quota
 * eviction mid-write, or holding arrays of the wrong length would be adopted as the planet and drawn — so the geometry is
 * re-checked against the constants this build compiled with, and every array against the length it must have. The key
 * already covers an authored change; this covers the storage itself going wrong.
 *
 * Exported for its tests: it is the one guard whose failure mode is adopting 67 MB of the wrong thing as the planet, and
 * the only production caller is behind an IndexedDB the `node` test environment does not have.
 */
export function usableRecord(record: TerrainRecord | null): boolean {
  if (record === null) {
    return false;
  }

  if (record.posts !== POSTS || record.spacingM !== POST_SPACING_M || record.originM !== FIELD_ORIGIN_M) {
    return false;
  }

  if (!(record.height instanceof Float32Array) || record.height.length !== POSTS * POSTS) {
    return false;
  }

  const nodes = record.nodeError;
  if (!(nodes instanceof Float32Array) || nodes.length === 0) {
    return false;
  }

  return (
    record.nodeMinY instanceof Float32Array &&
    record.nodeMaxY instanceof Float32Array &&
    record.nodeMinY.length === nodes.length &&
    record.nodeMaxY.length === nodes.length
  );
}

/** Promisifies one IndexedDB request. */
function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => {
      resolve(req.result);
    };

    req.onerror = () => {
      reject(req.error ?? new Error('indexeddb request failed'));
    };
  });
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open(DB_NAME, DB_VERSION);
    open.onupgradeneeded = () => {
      if (!open.result.objectStoreNames.contains(STORE)) {
        open.result.createObjectStore(STORE, { keyPath: 'key' });
      }
    };

    open.onsuccess = () => {
      resolve(open.result);
    };

    open.onerror = () => {
      reject(open.error ?? new Error('indexeddb open failed'));
    };

    // A second tab holding an older version open blocks the upgrade indefinitely; failing is better than hanging, because
    // the caller's fallback is simply to bake.
    open.onblocked = () => {
      reject(new Error('indexeddb upgrade blocked by another tab'));
    };
  });
}

/**
 * The real store, or `null` where IndexedDB is unavailable.
 *
 * <b>Every failure is a miss, never a throw.</b> A cache is an optimisation: a browser with site data disabled, a quota
 * that refuses 67 MB, a corrupt record — all of them mean "bake it", and none of them should reach a person as an error.
 * The one thing this must not do is make a session that would have worked fail.
 */
export function indexedDbStore(): TerrainCacheStore | null {
  if (typeof indexedDB === 'undefined') {
    return null;
  }

  return {
    async read(key) {
      let db: IDBDatabase | null = null;
      try {
        db = await openDb();
        const tx = db.transaction(STORE, 'readonly');
        const found = (await request(tx.objectStore(STORE).get(key))) as TerrainRecord | undefined;
        const record = found ?? null;
        return usableRecord(record) ? record : null;
      } catch {
        return null;
      } finally {
        db?.close();
      }
    },

    async write(record) {
      let db: IDBDatabase | null = null;
      try {
        db = await openDb();
        const tx = db.transaction(STORE, 'readwrite');
        const store = tx.objectStore(STORE);
        // One planet at a time: a stale key is dead weight at 67 MB a copy, and clearing first is also what keeps a quota
        // refusal from being caused by the very entry this call is replacing.
        await request(store.clear());
        await request(store.put(record));
        return true;
      } catch {
        // QuotaExceededError lands here, and it is an ordinary outcome rather than a fault: the session carries on with
        // the field it baked itself and simply pays the bake again next time.
        return false;
      } finally {
        db?.close();
      }
    },
  };
}
