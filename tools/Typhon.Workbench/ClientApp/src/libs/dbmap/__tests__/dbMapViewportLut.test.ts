import { describe, expect, it } from 'vitest';
import { fillViewportPageLut, lutPageAt } from '../dbMapViewportLut';
import { xyToPage } from '../hilbert';

/**
 * Tests for the per-frame viewport page-index LUT — extracted from the renderer's
 * {@link DbMapRenderer.precomputeViewportLut} so the inner fill loop is testable without a canvas.
 *
 * The LUT is the renderer's main optimisation against the hilbert hotspot (55.5% self-time profile reading): every
 * L1 annotation pass that used to call `xyToPage` per visible cell now reads the LUT in O(1). The tests verify both
 * correctness (every slot matches what `xyToPage` would have computed) and the sentinel discipline that lets
 * callers replace dual range checks with a single `< 0` test.
 */
describe('fillViewportPageLut', () => {
  it('matches xyToPage element-by-element in hilbert mode', () => {
    const order = 4; // 16×16 grid
    const pageCount = 200; // some pages off-file at the Hilbert tail (max grid index is 255)
    const rect = { cx0: 0, cy0: 0, cx1: 15, cy1: 15 };
    const lut = new Int32Array(16 * 16);
    const { w, h } = fillViewportPageLut(lut, rect, order, pageCount, 'hilbert');
    expect(w).toBe(16);
    expect(h).toBe(16);
    for (let cy = 0; cy < 16; cy++) {
      for (let cx = 0; cx < 16; cx++) {
        const expected = xyToPage(order, 'hilbert', cx, cy);
        const stored = lut[cy * 16 + cx];
        if (expected >= 0 && expected < pageCount) {
          expect(stored).toBe(expected);
        } else {
          // Off-file slot — must be the sentinel.
          expect(stored).toBe(-1);
        }
      }
    }
  });

  it('matches xyToPage element-by-element in sequential mode', () => {
    const order = 3; // 8×8 grid
    const pageCount = 50;
    const rect = { cx0: 0, cy0: 0, cx1: 7, cy1: 7 };
    const lut = new Int32Array(8 * 8);
    fillViewportPageLut(lut, rect, order, pageCount, 'sequential');
    for (let cy = 0; cy < 8; cy++) {
      for (let cx = 0; cx < 8; cx++) {
        const expected = xyToPage(order, 'sequential', cx, cy);
        const stored = lut[cy * 8 + cx];
        if (expected >= 0 && expected < pageCount) {
          expect(stored).toBe(expected);
        } else {
          expect(stored).toBe(-1);
        }
      }
    }
  });

  it('writes only the rect prefix when given a partial-viewport rect', () => {
    // Simulate the renderer's typical case: small visible rect inside a larger grid. The fill MUST only touch
    // [0 .. w*h-1] of the output buffer so the caller's reused-buffer pattern is safe.
    const order = 5; // 32×32
    const pageCount = 1024;
    const rect = { cx0: 4, cy0: 8, cx1: 11, cy1: 19 };
    const w = rect.cx1 - rect.cx0 + 1; // 8
    const h = rect.cy1 - rect.cy0 + 1; // 12
    const sentinel = 0x55555555;
    const lut = new Int32Array(2048).fill(sentinel); // bigger than needed, pre-filled with garbage
    const dims = fillViewportPageLut(lut, rect, order, pageCount, 'hilbert');
    expect(dims).toEqual({ w, h });
    // Inside the rect: every slot must reflect a valid xyToPage (or -1 if off-file — but pageCount=1024 = 32² so no).
    for (let i = 0; i < w * h; i++) {
      const cx = rect.cx0 + (i % w);
      const cy = rect.cy0 + Math.floor(i / w);
      expect(lut[i]).toBe(xyToPage(order, 'hilbert', cx, cy));
    }
    // Beyond the rect: untouched.
    for (let i = w * h; i < lut.length; i++) {
      expect(lut[i]).toBe(sentinel);
    }
  });

  it('handles a single-cell rect', () => {
    const order = 4;
    const pageCount = 256;
    const rect = { cx0: 7, cy0: 9, cx1: 7, cy1: 9 };
    const lut = new Int32Array(1);
    const dims = fillViewportPageLut(lut, rect, order, pageCount, 'hilbert');
    expect(dims).toEqual({ w: 1, h: 1 });
    expect(lut[0]).toBe(xyToPage(order, 'hilbert', 7, 9));
  });

  it('stamps the sentinel for cells inside the rect but past pageCount (Hilbert tail)', () => {
    const order = 4; // 256-cell grid
    const pageCount = 100; // most cells map to a page past the count → -1 sentinel
    const rect = { cx0: 0, cy0: 0, cx1: 15, cy1: 15 };
    const lut = new Int32Array(256);
    fillViewportPageLut(lut, rect, order, pageCount, 'hilbert');
    let sentinelCount = 0;
    let realCount = 0;
    for (let i = 0; i < 256; i++) {
      if (lut[i] === -1) sentinelCount++;
      else realCount++;
    }
    // Hilbert maps each grid index 0..255 to a unique cell, so exactly `pageCount` slots are real.
    expect(realCount).toBe(pageCount);
    expect(sentinelCount).toBe(256 - pageCount);
  });
});

describe('lutPageAt', () => {
  // Helper that mirrors the renderer's private lutPageAt; verified against xyToPage with a known LUT.
  const order = 4;
  const pageCount = 256;
  const rect = { cx0: 3, cy0: 5, cx1: 10, cy1: 12 };
  const w = rect.cx1 - rect.cx0 + 1;
  const h = rect.cy1 - rect.cy0 + 1;
  const lut = new Int32Array(w * h);
  fillViewportPageLut(lut, rect, order, pageCount, 'hilbert');

  it('returns the LUT-stored page for cells inside the rect', () => {
    for (let cy = rect.cy0; cy <= rect.cy1; cy++) {
      for (let cx = rect.cx0; cx <= rect.cx1; cx++) {
        expect(lutPageAt(lut, rect, w, h, cx, cy)).toBe(xyToPage(order, 'hilbert', cx, cy));
      }
    }
  });

  it('returns -1 for cells outside the rect (off-by-one before / after the bounds)', () => {
    expect(lutPageAt(lut, rect, w, h, rect.cx0 - 1, rect.cy0)).toBe(-1);
    expect(lutPageAt(lut, rect, w, h, rect.cx1 + 1, rect.cy0)).toBe(-1);
    expect(lutPageAt(lut, rect, w, h, rect.cx0, rect.cy0 - 1)).toBe(-1);
    expect(lutPageAt(lut, rect, w, h, rect.cx0, rect.cy1 + 1)).toBe(-1);
  });

  it('returns -1 far outside the rect (the drawSegmentOverlay adjacency-probe case)', () => {
    expect(lutPageAt(lut, rect, w, h, -100, -100)).toBe(-1);
    expect(lutPageAt(lut, rect, w, h, 10_000, 10_000)).toBe(-1);
  });
});
