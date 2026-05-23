import { describe, expect, it } from 'vitest';
import type { Camera } from '../camera';
import { buildLayout } from '../dbMapLayout';
import { pageAtScreen, regionAtScreen } from '../dbMapHitTest';
import { pageToXY } from '../hilbert';

describe('dbMapHitTest', () => {
  // 200 pages → order 4 (16×16 grid). No WAL.
  const layout = buildLayout(200, 0, 4);
  // 10 screen-px per page cell, no translation.
  const cam: Camera = { scale: 10, x: 0, y: 0 };

  it('pageAtScreen resolves the cell centre back to its page index', () => {
    for (const page of [0, 1, 42, 137, 199]) {
      const { x, y } = pageToXY(layout.order, 'hilbert', page);
      const hit = pageAtScreen(cam, layout, 'hilbert', x * 10 + 5, y * 10 + 5);
      expect(hit).toBe(page);
    }
  });

  it('pageAtScreen round-trips under the sequential (row-major) ordering', () => {
    for (const page of [0, 1, 42, 137, 199]) {
      const { x, y } = pageToXY(layout.order, 'sequential', page);
      const hit = pageAtScreen(cam, layout, 'sequential', x * 10 + 5, y * 10 + 5);
      expect(hit).toBe(page);
    }
  });

  it('pageAtScreen returns null on the inert Hilbert tail', () => {
    // Page 255 is the last grid cell but only 200 pages are real.
    const { x, y } = pageToXY(layout.order, 'hilbert', 255);
    expect(pageAtScreen(cam, layout, 'hilbert', x * 10 + 5, y * 10 + 5)).toBeNull();
  });

  it('pageAtScreen returns null outside the data rect', () => {
    expect(pageAtScreen(cam, layout, 'hilbert', -50, -50)).toBeNull();
    expect(pageAtScreen(cam, layout, 'hilbert', 100_000, 100_000)).toBeNull();
  });

  it('regionAtScreen distinguishes the data file from the WAL', () => {
    const withWal = buildLayout(200, 64 * 1024 * 1024, 4);
    expect(regionAtScreen(cam, withWal, 5, 5)).toBe('data');
    const wal = withWal.walRect!;
    const wx = (wal.x + wal.w / 2) * 10;
    const wy = (wal.y + wal.h / 2) * 10;
    expect(regionAtScreen(cam, withWal, wx, wy)).toBe('wal');
    expect(regionAtScreen(cam, withWal, -10, -10)).toBeNull();
  });
});
