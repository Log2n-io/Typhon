import { describe, expect, it } from 'vitest';
import {
  createChunkCache,
  assembleTickViewAndNumbers,
  computePendingRangesUs,
  seedThreadNames,
} from '@/libs/profiler/cache/chunkCache';
import type { ChunkManifestEntry, TickSummary } from '@/libs/profiler/model/types';
import { buildEmptyTick } from '@/libs/profiler/model/__tests__/spanBuilder';

/**
 * Covers two cache helpers that sit on the hot path of every viewport change:
 *
 *   - `assembleTickViewAndNumbers` memo: gated on `entriesVersion`. On pure pan/zoom (no cache
 *     mutation) this must return the previous result by reference so Preact's shallow compare
 *     skips re-rendering downstream panels. A regression here would re-run `processTickEvents`
 *     on every mouse-wheel event (>60 Hz) and re-allocate the full tick view, tanking scroll
 *     performance on dense traces.
 *   - `computePendingRangesUs`: consumed by TimeArea to render the diagonal-stripe overlay for
 *     not-yet-loaded chunks. Adjacent pending chunks must coalesce into a single painted range
 *     (cheaper + cleaner visual); ranges must clip at the viewport edge so the pattern doesn't
 *     bleed into black margin area; resident chunks break a run.
 */

describe('assembleTickViewAndNumbers — memo short-circuit', () => {
  it('returns the cached result by reference when entriesVersion is unchanged', () => {
    const cache = createChunkCache();
    cache.entries.set(0, {
      chunkIdx: 0,
      fromTick: 1,
      toTick: 2,
      tickData: [buildEmptyTick(1, 0, 100)],
      byteSize: 1000,
      lastAccessTick: 0,
    });
    cache.entriesVersion = 1;

    const first = assembleTickViewAndNumbers(cache, []);
    const second = assembleTickViewAndNumbers(cache, []);

    // Reference equality is the whole point — shallow compare downstream sees the same result.
    expect(second).toBe(first);
    expect(second.tickData).toBe(first.tickData);
    expect(second.tickNumbers).toBe(first.tickNumbers);
  });

  it('recomputes when entriesVersion bumps (new chunk loaded)', () => {
    const cache = createChunkCache();
    cache.entries.set(0, {
      chunkIdx: 0, fromTick: 1, toTick: 2, byteSize: 1000, lastAccessTick: 0,
      tickData: [buildEmptyTick(1, 0, 100)],
    });
    cache.entriesVersion = 1;
    const first = assembleTickViewAndNumbers(cache, []);
    expect(first.tickData).toHaveLength(1);

    // Simulate a load: new chunk entry + bumped version → memo must recompute.
    cache.entries.set(1, {
      chunkIdx: 1, fromTick: 2, toTick: 3, byteSize: 1000, lastAccessTick: 1,
      tickData: [buildEmptyTick(2, 100, 100)],
    });
    cache.entriesVersion = 2;
    const second = assembleTickViewAndNumbers(cache, []);

    expect(second).not.toBe(first);
    expect(second.tickData).toHaveLength(2);
    expect(second.tickNumbers).toEqual([1, 2]);
  });

  it('populates lastAssembly snapshot after first computation', () => {
    const cache = createChunkCache();
    cache.entries.set(0, {
      chunkIdx: 0, fromTick: 1, toTick: 2, byteSize: 100, lastAccessTick: 0,
      tickData: [buildEmptyTick(1, 0, 100)],
    });
    cache.entriesVersion = 5;
    expect(cache.lastAssembly).toBeNull();

    assembleTickViewAndNumbers(cache, []);

    expect(cache.lastAssembly).not.toBeNull();
    expect(cache.lastAssembly!.version).toBe(5);
  });
});

describe('computePendingRangesUs', () => {
  /**
   * Build a 4-chunk manifest and matching tick summary for a 400 µs trace:
   *   chunk 0: ticks 1..1, 0..100 µs
   *   chunk 1: ticks 2..2, 100..200 µs
   *   chunk 2: ticks 3..3, 200..300 µs
   *   chunk 3: ticks 4..4, 300..400 µs
   */
  function buildManifestAndSummary(): { manifest: ChunkManifestEntry[]; summary: TickSummary[] } {
    const manifest: ChunkManifestEntry[] = [];
    const summary: TickSummary[] = [];
    for (let i = 0; i < 4; i++) {
      manifest.push({
        chunkIdx: i,
        fromTick: i + 1,
        toTick: i + 2,
        eventCount: 10,
        uncompressedBytes: 1000,
        cacheByteLength: 500,
        flags: 0,
      } as unknown as ChunkManifestEntry);
      summary.push({
        tickNumber: i + 1,
        startUs: i * 100,
        durationUs: 100,
      } as unknown as TickSummary);
    }
    return { manifest, summary };
  }

  it('returns empty when the cache has every chunk loaded', () => {
    const { manifest, summary } = buildManifestAndSummary();
    const cache = createChunkCache();
    for (let i = 0; i < 4; i++) {
      cache.entries.set(i, {
        chunkIdx: i, fromTick: i + 1, toTick: i + 2, byteSize: 0, lastAccessTick: 0,
        tickData: [],
      });
    }
    const ranges = computePendingRangesUs(cache, manifest, summary, { startUs: 0, endUs: 400 });
    expect(ranges).toEqual([]);
  });

  it('coalesces adjacent missing chunks into a single range', () => {
    const { manifest, summary } = buildManifestAndSummary();
    // No chunks loaded → every manifest entry pending. The four adjacent ranges [0,100) [100,200)
    // [200,300) [300,400) must collapse to one [0, 400).
    const ranges = computePendingRangesUs(null, manifest, summary, { startUs: 0, endUs: 400 });
    expect(ranges).toEqual([{ startUs: 0, endUs: 400 }]);
  });

  it('breaks a run when a resident chunk sits between two missing ones', () => {
    const { manifest, summary } = buildManifestAndSummary();
    const cache = createChunkCache();
    // Chunk 1 resident → pending runs split as [0,100) and [200,400).
    cache.entries.set(1, {
      chunkIdx: 1, fromTick: 2, toTick: 3, byteSize: 0, lastAccessTick: 0, tickData: [],
    });
    const ranges = computePendingRangesUs(cache, manifest, summary, { startUs: 0, endUs: 400 });
    expect(ranges).toEqual([
      { startUs: 0, endUs: 100 },
      { startUs: 200, endUs: 400 },
    ]);
  });

  it('clips output ranges to the viewport', () => {
    const { manifest, summary } = buildManifestAndSummary();
    // Viewport spans mid-chunk-0 to mid-chunk-2 → single coalesced pending range clipped to the
    // viewport's edges (not the chunk edges).
    const ranges = computePendingRangesUs(null, manifest, summary, { startUs: 50, endUs: 250 });
    expect(ranges).toEqual([{ startUs: 50, endUs: 250 }]);
  });

  it('empty viewport returns no ranges', () => {
    const { manifest, summary } = buildManifestAndSummary();
    const ranges = computePendingRangesUs(null, manifest, summary, { startUs: 100, endUs: 100 });
    expect(ranges).toEqual([]);
  });

  it('empty manifest or summary returns no ranges', () => {
    expect(computePendingRangesUs(null, [], [], { startUs: 0, endUs: 100 })).toEqual([]);
  });
});

describe('seedThreadNames', () => {
  it('names every slot of the capture from its thread table before any chunk loads', () => {
    const cache = createChunkCache();
    seedThreadNames(cache, [{ slot: 9, name: 'Typhon.Worker-8', kind: 1 }, { slot: '48', name: '.NET TP Worker', kind: 2 }]);
    expect([...cache.threadNames]).toEqual([[9, 'Typhon.Worker-8'], [48, '.NET TP Worker']]);
    expect(cache.threadKinds.get(48)).toBe(2);
  });

  it('is a no-op for a cache too old to carry the table', () => {
    const cache = createChunkCache();
    seedThreadNames(cache, null);
    expect(cache.threadNames.size).toBe(0);
  });
});
