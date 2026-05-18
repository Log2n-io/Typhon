import { describe, expect, it } from 'vitest';
import {
  L3_FULL_PAGE_PX,
  L3_MIN_PAGE_PX,
  L4_FULL_PAGE_PX,
  L4_MIN_PAGE_PX,
  lodForScale,
  tileNodesForSpan,
} from '../dbMapLod';

describe('lodForScale', () => {
  it('reports pure L1 below the L3 threshold', () => {
    const lod = lodForScale(L3_MIN_PAGE_PX - 50);
    expect(lod.band).toBe('L1');
    expect(lod.l3Alpha).toBe(0);
    expect(lod.l4Alpha).toBe(0);
  });

  it('fully crossfades into L3 at the L3-full scale', () => {
    const lod = lodForScale(L3_FULL_PAGE_PX);
    expect(lod.l3Alpha).toBe(1);
    expect(lod.band).toBe('L3');
  });

  it('fully crossfades into L4 at the L4-full scale', () => {
    const lod = lodForScale(L4_FULL_PAGE_PX);
    expect(lod.l3Alpha).toBe(1);
    expect(lod.l4Alpha).toBe(1);
    expect(lod.band).toBe('L4');
  });

  it('ramps the L3 alpha continuously and monotonically — no jump-cut', () => {
    let prev = -1;
    let maxStep = 0;
    for (let scale = 0; scale <= L4_FULL_PAGE_PX; scale += 20) {
      const a = lodForScale(scale).l3Alpha;
      expect(a).toBeGreaterThanOrEqual(prev);
      if (prev >= 0) {
        maxStep = Math.max(maxStep, a - prev);
      }
      prev = a;
    }
    // A 20px scale step never moves the crossfade alpha by more than a small fraction.
    expect(maxStep).toBeLessThan(0.1);
  });

  it('places the L4 crossfade band entirely above the L3 band', () => {
    expect(L4_MIN_PAGE_PX).toBeGreaterThan(L3_FULL_PAGE_PX);
  });
});

describe('tileNodesForSpan', () => {
  it('returns the single node containing a span within one tile', () => {
    expect(tileNodesForSpan(10, 900, 1024)).toEqual([0]);
  });

  it('returns every node a span straddles', () => {
    expect(tileNodesForSpan(900, 2100, 1024)).toEqual([0, 1, 2]);
  });

  it('returns an empty list for a degenerate span or tile size', () => {
    expect(tileNodesForSpan(100, 50, 1024)).toEqual([]);
    expect(tileNodesForSpan(0, 100, 0)).toEqual([]);
  });
});
