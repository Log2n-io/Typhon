import { describe, expect, it } from 'vitest';
import {
  clampView,
  fitView,
  MAX_PX_PER_M,
  panBy,
  reliefPixels,
  toCanvas,
  toPlanet,
  zoomAt,
} from '../src/data/planet-map';
import { panelKey } from '../src/app/panel-keys';
import { samePlace } from '../src/state/map-store';

// `isTyping` tests `target instanceof HTMLInputElement`, which throws when the global is undefined (as market.test.ts).
{
  const target = globalThis as unknown as Record<string, unknown>;
  const absent = function Absent(): void {};
  target['HTMLInputElement'] ??= absent;
  target['HTMLSelectElement'] ??= absent;
  target['HTMLTextAreaElement'] ??= absent;
}

const HALF = 8192;
const SIZE = 800;

describe('the planet map (Loïc, 2026-11-02)', () => {
  it('fits the whole planet, north up: +x right, +z up', () => {
    const view = fitView(SIZE, HALF);
    expect(toCanvas(view, SIZE, 0, 0)).toEqual({ px: 400, py: 400 });
    expect(toCanvas(view, SIZE, -HALF, HALF)).toEqual({ px: 0, py: 0 });
    expect(toCanvas(view, SIZE, HALF, -HALF)).toEqual({ px: 800, py: 800 });
    const back = toPlanet(view, SIZE, 600, 100);
    expect(toCanvas(view, SIZE, back.x, back.z).px).toBeCloseTo(600);
    expect(toCanvas(view, SIZE, back.x, back.z).py).toBeCloseTo(100);
  });

  it('zooms about the cursor: the planet point under it stays under it', () => {
    const view = fitView(SIZE, HALF);
    const under = toPlanet(view, SIZE, 620, 230);
    const zoomed = zoomAt(view, SIZE, HALF, 620, 230, 4);
    const after = toCanvas(zoomed, SIZE, under.x, under.z);
    expect(zoomed.pxPerM).toBeCloseTo(view.pxPerM * 4);
    expect(after.px).toBeCloseTo(620);
    expect(after.py).toBeCloseTo(230);
  });

  it('never zooms out past the whole planet nor in past its closest', () => {
    const view = fitView(SIZE, HALF);
    expect(zoomAt(view, SIZE, HALF, 400, 400, 0.1)).toEqual(view);
    expect(zoomAt(view, SIZE, HALF, 400, 400, 1e6).pxPerM).toBe(MAX_PX_PER_M);
  });

  it('pans with the pointer, and never past the planet edge', () => {
    const zoomed = zoomAt(fitView(SIZE, HALF), SIZE, HALF, 400, 400, 8);
    const panned = panBy(zoomed, SIZE, HALF, 100, 50);
    // A drag to the right and down shows what was to the left and above: west and north.
    expect(panned.cx).toBeLessThan(zoomed.cx);
    expect(panned.cz).toBeGreaterThan(zoomed.cz);
    const far = panBy(zoomed, SIZE, HALF, 1e7, -1e7);
    const corner = toCanvas(far, SIZE, -HALF, -HALF);
    expect(corner.px).toBeCloseTo(0);
    expect(corner.py).toBeCloseTo(SIZE);
    expect(clampView(fitView(SIZE, HALF), SIZE, HALF)).toEqual(fitView(SIZE, HALF));
  });

  it("draws the relief north up: the field's last row is the image's first", () => {
    const posts = 4;
    const height = new Float32Array(posts * posts);
    height.fill(0);
    height.fill(100, (posts - 1) * posts);
    const pixels = reliefPixels({ height, posts }, posts);
    const red = (x: number, y: number): number => pixels[(y * posts + x) * 4];
    expect(red(1, 0)).toBeGreaterThan(red(1, posts - 1));
    expect(pixels[3]).toBe(255);
  });

  it('is toggled by M, not by a modified M', () => {
    const key = (k: string, ctrlKey = false) =>
      ({
        key: k,
        repeat: false,
        isComposing: false,
        ctrlKey,
        altKey: false,
        metaKey: false,
        target: null,
      }) as unknown as KeyboardEvent;
    expect(panelKey(key('m'))).toBe('map');
    expect(panelKey(key('M'))).toBe('map');
    expect(panelKey(key('m', true))).toBeNull();
  });
});

describe('the map marker (map-store)', () => {
  it('is published when the player moved or turned, not while it stands', () => {
    const at = { x: 10, z: 20, yaw: 1 };
    expect(samePlace(at, { x: 10.2, z: 20.1, yaw: 1.01 })).toBe(true);
    expect(samePlace(at, { x: 11, z: 20, yaw: 1 })).toBe(false);
    expect(samePlace(at, { x: 10, z: 20, yaw: 1.1 })).toBe(false);
    expect(samePlace(null, null)).toBe(true);
    expect(samePlace(at, null)).toBe(false);
  });
});
