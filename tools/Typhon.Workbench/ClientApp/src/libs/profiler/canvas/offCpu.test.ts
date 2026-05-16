import { describe, expect, it } from 'vitest';
import { buildOffCpuRuns } from './timeArea';
import { findOffCpuAtX } from './timeAreaHitTest';
import type { OffCpuStore } from '@/libs/profiler/model/traceModel';
import type { Viewport } from '@/libs/profiler/model/uiTypes';

/**
 * Pure-module tests for the off-CPU render + hit-test path:
 *   - `buildOffCpuRuns` — viewport-culled LOD coalescing (wide intervals draw individually; sub-pixel
 *     same-category intervals collapse into one run).
 *   - `findOffCpuAtX` — O(log n) binary-search probe with a ~2px snap tolerance.
 */

function makeStore(intervals: Array<{ start: number; end: number; cat: number }>): OffCpuStore {
  const n = intervals.length;
  const startUs = new Float64Array(n);
  const endUs = new Float64Array(n);
  const readyTimeUs = new Float64Array(n);
  const category = new Uint8Array(n);
  const waitReason = new Uint8Array(n);
  const processorNumber = new Uint8Array(n);
  intervals.forEach((iv, i) => {
    startUs[i] = iv.start;
    endUs[i] = iv.end;
    category[i] = iv.cat;
  });
  return { startUs, endUs, readyTimeUs, category, waitReason, processorNumber };
}

describe('buildOffCpuRuns — LOD coalescing', () => {
  it('emits one run per wide interval (≥ minWidthPx)', () => {
    const store = makeStore([
      { start: 10, end: 40, cat: 1 },
      { start: 60, end: 100, cat: 2 },
    ]);
    const x1 = new Float64Array(16);
    const x2 = new Float64Array(16);
    const cat = new Uint8Array(16);
    // pxOfUs = identity ⇒ both intervals are ≥30px wide.
    const count = buildOffCpuRuns(store, 0, 1e9, (us) => us, 0, 2, x1, x2, cat);

    expect(count).toBe(2);
    expect(x1[0]).toBe(10);
    expect(x2[0]).toBe(40);
    expect(cat[0]).toBe(1);
    expect(x1[1]).toBe(60);
    expect(cat[1]).toBe(2);
  });

  it('coalesces adjacent sub-pixel intervals of the same category into a single run', () => {
    // 6 sub-pixel intervals, all category 3, packed tight. At 0.1 px/µs each is ~0.5px wide.
    const intervals = Array.from({ length: 6 }, (_, i) => ({ start: i * 5, end: i * 5 + 4, cat: 3 }));
    const store = makeStore(intervals);
    const x1 = new Float64Array(16);
    const x2 = new Float64Array(16);
    const cat = new Uint8Array(16);
    const count = buildOffCpuRuns(store, 0, 1e9, (us) => us * 0.1, 0, 2, x1, x2, cat);

    expect(count).toBe(1);          // all six collapse into one run
    expect(cat[0]).toBe(3);
    expect(x2[0] - x1[0]).toBeGreaterThanOrEqual(1);
  });

  it('coalesces adjacent sub-pixel intervals across category boundaries (leftmost category wins)', () => {
    // At sub-pixel zoom the per-interval colour is invisible; merging across categories is what bounds the output.
    const store = makeStore([
      { start: 0, end: 4, cat: 1 },
      { start: 5, end: 9, cat: 1 },
      { start: 10, end: 14, cat: 2 },   // different category — still coalesces
      { start: 15, end: 19, cat: 2 },
    ]);
    const x1 = new Float64Array(16);
    const x2 = new Float64Array(16);
    const cat = new Uint8Array(16);
    const count = buildOffCpuRuns(store, 0, 1e9, (us) => us * 0.1, 0, 2, x1, x2, cat);

    expect(count).toBe(1);
    expect(cat[0]).toBe(1);   // leftmost interval's category
  });

  it('run count never exceeds scratch capacity even with thousands of category-alternating sub-pixel intervals', () => {
    // Worst case for the LOD pass: thousands of sub-pixel intervals, category flipping every interval, all inside a
    // narrow viewport. Runs must stay pixel-disjoint ⇒ bounded by the scratch capacity, never the interval count.
    const intervals = Array.from({ length: 4000 }, (_, i) => ({ start: i, end: i + 0.4, cat: i & 1 }));
    const store = makeStore(intervals);
    const cap = 120;   // simulates a ~120px-wide lane
    const x1 = new Float64Array(cap);
    const x2 = new Float64Array(cap);
    const cat = new Uint8Array(cap);
    // 0.05 px/µs ⇒ the 4000-µs span maps to ~200px; each interval is ~0.02px (sub-pixel).
    const count = buildOffCpuRuns(store, 0, 1e9, (us) => us * 0.05, 0, 2, x1, x2, cat);

    expect(count).toBeLessThanOrEqual(cap);
    expect(count).toBeGreaterThan(0);
    // Runs are emitted left-to-right and pixel-disjoint.
    for (let i = 1; i < count; i++) {
      expect(x1[i]).toBeGreaterThanOrEqual(x2[i - 1]);
    }
  });

  it('stops walking once startUs passes visEndUs (viewport cull)', () => {
    const store = makeStore([
      { start: 10, end: 40, cat: 1 },
      { start: 1000, end: 1040, cat: 1 },   // beyond visEndUs — must be skipped
    ]);
    const x1 = new Float64Array(16);
    const x2 = new Float64Array(16);
    const cat = new Uint8Array(16);
    const count = buildOffCpuRuns(store, 0, 500, (us) => us, 0, 2, x1, x2, cat);

    expect(count).toBe(1);
    expect(x1[0]).toBe(10);
  });
});

describe('findOffCpuAtX — binary-search hit-test', () => {
  const vp1to1: Viewport = { offsetX: 0, scaleX: 1, scrollY: 0 };

  it('returns the interval containing the cursor', () => {
    const store = makeStore([
      { start: 100, end: 200, cat: 1 },
      { start: 400, end: 500, cat: 2 },
      { start: 700, end: 800, cat: 3 },
    ]);
    const map = new Map([[5, store]]);
    const hit = findOffCpuAtX(map, 5, 450, 0, vp1to1);

    expect(hit).not.toBeNull();
    expect(hit!.threadSlot).toBe(5);
    expect(hit!.startUs).toBe(400);
    expect(hit!.endUs).toBe(500);
    expect(hit!.durationUs).toBe(100);
    expect(hit!.category).toBe(2);
  });

  it('returns null when the cursor is far from any interval', () => {
    const store = makeStore([{ start: 100, end: 200, cat: 1 }]);
    const map = new Map([[5, store]]);
    expect(findOffCpuAtX(map, 5, 1000, 0, vp1to1)).toBeNull();
  });

  it('returns null for a slot with no off-CPU store', () => {
    expect(findOffCpuAtX(new Map(), 9, 150, 0, vp1to1)).toBeNull();
  });

  it('snaps to a nearby interval within the ~2px tolerance when zoomed out', () => {
    // scaleX = 0.5 px/µs ⇒ snap tolerance = 2 / 0.5 = 4µs. Cursor 3µs left of the interval start.
    const store = makeStore([{ start: 100, end: 110, cat: 4 }]);
    const map = new Map([[1, store]]);
    const vpZoomedOut: Viewport = { offsetX: 0, scaleX: 0.5, scrollY: 0 };
    // mx in px: us = offsetX + (mx - gutter)/scaleX ⇒ to land at us=97, mx = 97 * 0.5 = 48.5.
    const hit = findOffCpuAtX(map, 1, 48.5, 0, vpZoomedOut);

    expect(hit).not.toBeNull();
    expect(hit!.startUs).toBe(100);
  });

  it('accounts for gutterWidth and viewport offset in the X→µs mapping', () => {
    const store = makeStore([{ start: 1000, end: 1100, cat: 0 }]);
    const map = new Map([[2, store]]);
    const vp: Viewport = { offsetX: 900, scaleX: 1, scrollY: 0 };
    // us = 900 + (mx - 40)/1 ⇒ mx = 40 + (1050 - 900) = 190 lands at us=1050, inside [1000,1100].
    const hit = findOffCpuAtX(map, 2, 190, 40, vp);
    expect(hit).not.toBeNull();
    expect(hit!.startUs).toBe(1000);
  });
});
