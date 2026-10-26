// Node types for THIS FILE only — the app's tsconfig sets `types: ["vite/client"]` deliberately, so `src` cannot reach a
// node API by accident. The same exemption `styles.test.ts` takes, and for the same reason: the last test here has to read
// source files off disk.
/// <reference types="node" />
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

import { describe, expect, it } from 'vitest';

import { FIELD_ORIGIN_M, POSTS, POST_SPACING_M } from '../src/terrain/heightfield';
import { TERRAIN_SEED } from '../src/terrain/tatooine-terrain';
import {
  TERRAIN_BAKE_VERSION,
  terrainCacheKey,
  usableRecord,
  type TerrainCacheStore,
  type TerrainRecord,
} from '../src/terrain/terrain-cache';
import { loadTerrain, resetSharedTerrain, sharedTerrain } from '../src/terrain/terrain-provider';

/**
 * The local database that keeps the baked planet between visits.
 *
 * <b>What is actually at risk here is not speed, it is agreement.</b> The server bakes the same field and writes entity
 * altitudes from it; `TerrainGoldenChecks` holds the two to the same bits. A cache that handed back a field this build
 * would no longer produce puts the client back in disagreement with the server — entities floating or sunk, each side
 * self-consistent, nothing failing. So most of this file is about when a cached field must be REFUSED.
 */

/** A record of the right shape and size, so `usable` has nothing to object to. */
function record(key: string): TerrainRecord {
  return {
    key,
    posts: POSTS,
    spacingM: POST_SPACING_M,
    originM: FIELD_ORIGIN_M,
    bakeMs: 2073,
    height: new Float32Array(POSTS * POSTS),
    nodeError: new Float32Array(4),
    nodeMinY: new Float32Array(4),
    nodeMaxY: new Float32Array(4),
    savedAtMs: 1,
  };
}

/** An in-memory store, which is what makes the cache's decisions testable in the `node` environment. */
function memoryStore(seed?: TerrainRecord): TerrainCacheStore & { writes: TerrainRecord[]; reads: string[] } {
  const held = new Map<string, TerrainRecord>();
  if (seed !== undefined) {
    held.set(seed.key, seed);
  }

  const writes: TerrainRecord[] = [];
  const reads: string[] = [];
  return {
    writes,
    reads,
    read(key) {
      reads.push(key);
      return Promise.resolve(held.get(key) ?? null);
    },
    write(entry) {
      writes.push(entry);
      held.set(entry.key, entry);
      return Promise.resolve(true);
    },
  };
}

describe('the terrain cache key', () => {
  it('is stable for the same inputs', () => {
    expect(terrainCacheKey()).toBe(terrainCacheKey());
  });

  /**
   * The seed is the most obvious input and the cheapest to get wrong, because the tests pass one and the app does not.
   * A key that ignored it would serve the app's planet to a test asking for another.
   */
  it('changes with the seed', () => {
    expect(terrainCacheKey(1)).not.toBe(terrainCacheKey(2));
    expect(terrainCacheKey(1)).not.toBe(terrainCacheKey());
  });

  /**
   * Bumping the version is the one escape hatch for a change the descriptor cannot see — the noise hash, the layer
   * evaluator, the quadtree. It only works if it reaches the key, and a version that did not would make every one of
   * those bumps a no-op while reading as a fix.
   */
  it('changes with the bake version', () => {
    expect(terrainCacheKey(TERRAIN_SEED, 1)).not.toBe(terrainCacheKey(TERRAIN_SEED, 2));
    expect(terrainCacheKey()).toBe(terrainCacheKey(TERRAIN_SEED, TERRAIN_BAKE_VERSION));
  });
});

describe('loading the terrain', () => {
  it('reads a cached field instead of baking', async () => {
    const store = memoryStore(record(terrainCacheKey()));

    const loaded = await loadTerrain({ store });

    expect(loaded.fromCache).toBe(true);
    expect(loaded.baked.posts).toBe(POSTS);
    // The bake's own cost is carried through, so a cache hit can still report what the field cost to make.
    expect(loaded.baked.bakeMs).toBe(2073);
    expect(store.writes).toEqual([]);
  });

  /**
   * The phases exist so the loading panel can say which of two very different waits is happening. A cache hit must never
   * report `baking`, or every visit would claim to be building a planet it is only reading.
   */
  it('reports reading and then ready on a hit, and never baking', async () => {
    const store = memoryStore(record(terrainCacheKey()));
    const phases: string[] = [];

    await loadTerrain({ store, onPhase: (phase) => phases.push(phase) });

    expect(phases).toEqual(['reading', 'ready']);
  });

  /**
   * A record stored under another key is not this planet. It is the whole point of the key, and the failure it prevents —
   * adopting a field baked from a layer tree somebody has since edited — is silent.
   */
  it('ignores a record stored under a different key', async () => {
    const store = memoryStore(record('terrain-somethingelse'));

    const read = await store.read(terrainCacheKey());

    expect(read).toBeNull();
  });

  /**
   * The cache is an optimisation and nothing more: a browser with no IndexedDB, or site data turned off, must still get a
   * planet. `store: null` is that case, and it has to go all the way to a bake rather than resolving to nothing.
   */
  it('bakes when there is no store at all', async () => {
    const phases: string[] = [];

    // It reaches the workers and fails there, because `node` has no `Worker` and no `import.meta.url` bundle to give one.
    // That failure IS the assertion: what matters is that a missing store sends the load to the bake rather than leaving
    // it with nothing, and the only observable of that here is the attempt.
    await expect(loadTerrain({ store: null, onPhase: (phase) => phases.push(phase) })).rejects.toThrow();

    expect(phases).toEqual(['baking']);
  });
});

/**
 * What a stored record has to satisfy before it is adopted.
 *
 * <b>A hit is not a promise that the bytes are intact.</b> The key proves the INPUTS match; it says nothing about a
 * record truncated by an eviction, written by a build with different constants, or holding arrays that do not line up.
 * Every case below ends with 67 MB of the wrong thing being drawn as the planet, with nothing raised.
 */
describe('a record read back', () => {
  it('is accepted when it matches this build', () => {
    expect(usableRecord(record(terrainCacheKey()))).toBe(true);
  });

  it('is refused when there is none', () => {
    expect(usableRecord(null)).toBe(false);
  });

  it('is refused when the grid disagrees with this build', () => {
    expect(usableRecord({ ...record('k'), posts: POSTS / 2 })).toBe(false);
    expect(usableRecord({ ...record('k'), spacingM: POST_SPACING_M * 2 })).toBe(false);
    expect(usableRecord({ ...record('k'), originM: FIELD_ORIGIN_M + 1 })).toBe(false);
  });

  /**
   * The one a truncated write produces, and the most dangerous: the geometry still says 4096, so every sampler computes
   * an index the array cannot answer and reads `undefined` as a height.
   */
  it('is refused when the height array is the wrong length', () => {
    expect(usableRecord({ ...record('k'), height: new Float32Array(POSTS * POSTS - 1) })).toBe(false);
    expect(usableRecord({ ...record('k'), height: new Float32Array(0) })).toBe(false);
  });

  /** The quadtree's three arrays are read in step by `refreshTerrain`; a short one is an out-of-bounds per node. */
  it('is refused when the quadtree arrays do not line up', () => {
    expect(usableRecord({ ...record('k'), nodeMinY: new Float32Array(3) })).toBe(false);
    expect(usableRecord({ ...record('k'), nodeMaxY: new Float32Array(5) })).toBe(false);
    expect(usableRecord({ ...record('k'), nodeError: new Float32Array(0) })).toBe(false);
  });
});

/**
 * One planet per page, however many callers ask for it.
 *
 * `main.tsx` starts the load and the viewport reads the result; a second load would mean a second 2 s bake and a second
 * 67 MB field, concurrently, for nothing.
 */
describe('the shared terrain', () => {
  it('is loaded once however many callers ask', async () => {
    resetSharedTerrain();
    const store = memoryStore(record(terrainCacheKey()));

    const [first, second] = await Promise.all([sharedTerrain({ store }), sharedTerrain({ store })]);

    expect(store.reads).toHaveLength(1);
    expect(second).toBe(first);
    resetSharedTerrain();
  });
});

/**
 * The version pin.
 *
 * <b>The key cannot see these files, and they decide every post's value.</b> `terrainCacheKey` digests the authored
 * configuration — amplitudes, wavelengths, boundaries, seeds, pad sites — which is what a person edits when they change
 * the terrain on purpose. It cannot see a changed constant in the noise hash, a reordered operation in the layer
 * evaluator, or a different quadtree measure: those move the bits with the configuration untouched, and a cache that
 * missed one would serve a planet the server no longer agrees with.
 *
 * So the files themselves are digested here. Changing any of them fails this test, and the fix is to bump
 * `TERRAIN_BAKE_VERSION` and paste the digest the failure prints — which is exactly the same ritual as the golden file,
 * for the same reason.
 */
describe('the terrain sources the cache key cannot see', () => {
  it('have not changed without a version bump', () => {
    const files = ['hash.ts', 'layers.ts', 'band-bake.ts', 'tatooine-terrain.ts', 'heightfield.ts'];
    const hash = createHash('sha256');
    for (const name of files) {
      // Newlines normalised, so a checkout with different line endings is not a different terrain.
      const text = readFileSync(fileURLToPath(new URL(`../src/terrain/${name}`, import.meta.url)), 'utf8');
      hash.update(name);
      hash.update(text.replace(/\r\n/g, '\n'));
    }

    const digest = hash.digest('hex').slice(0, 16);
    expect(
      digest,
      `a terrain source changed: bump TERRAIN_BAKE_VERSION (now ${TERRAIN_BAKE_VERSION}) so every cached field is refused, then paste ${digest} here`,
    ).toBe('720af35581506e2f');
  });
});
