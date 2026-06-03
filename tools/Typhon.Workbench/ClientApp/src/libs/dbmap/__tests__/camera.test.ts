import { describe, expect, it } from 'vitest';
import {
  cameraCenteredOn,
  fitToRect,
  frameWorldRect,
  panBy,
  screenToWorldX,
  screenToWorldY,
  tweenCamera,
  visibleWorldRect,
  worldToScreenX,
  worldToScreenY,
  zoomAt,
  type Camera,
} from '../camera';

const CAM: Camera = { scale: 4, x: 100, y: 50 };

describe('camera', () => {
  it('world↔screen transforms are inverses', () => {
    for (const w of [0, 12.5, 500]) {
      expect(screenToWorldX(CAM, worldToScreenX(CAM, w))).toBeCloseTo(w, 6);
      expect(screenToWorldY(CAM, worldToScreenY(CAM, w))).toBeCloseTo(w, 6);
    }
  });

  it('panBy shifts the offset, not the scale', () => {
    const moved = panBy(CAM, 30, -20);
    expect(moved.scale).toBe(CAM.scale);
    expect(moved.x).toBe(130);
    expect(moved.y).toBe(30);
  });

  it('zoomAt keeps the world point under the cursor fixed', () => {
    const cursorX = 640;
    const cursorY = 360;
    const worldBefore = { x: screenToWorldX(CAM, cursorX), y: screenToWorldY(CAM, cursorY) };
    const zoomed = zoomAt(CAM, cursorX, cursorY, 2.5);
    expect(zoomed.scale).toBeCloseTo(CAM.scale * 2.5, 6);
    expect(screenToWorldX(zoomed, cursorX)).toBeCloseTo(worldBefore.x, 4);
    expect(screenToWorldY(zoomed, cursorY)).toBeCloseTo(worldBefore.y, 4);
  });

  it('fitToRect centres the world rect within the viewport', () => {
    const cam = fitToRect({ x: 0, y: 0, w: 100, h: 100 }, 800, 600, 0);
    // The square fits to the smaller axis (height).
    expect(cam.scale).toBeCloseTo(6, 6);
    // Centred horizontally: 100×6 = 600 wide in an 800 viewport → 100px margin each side.
    expect(cam.x).toBeCloseTo(100, 6);
    expect(cam.y).toBeCloseTo(0, 6);
  });

  it('visibleWorldRect reports the on-screen world region', () => {
    const vis = visibleWorldRect(CAM, 800, 600);
    expect(vis.x).toBeCloseTo(screenToWorldX(CAM, 0), 6);
    expect(vis.w).toBeCloseTo(800 / CAM.scale, 6);
    expect(vis.h).toBeCloseTo(600 / CAM.scale, 6);
  });

  it('cameraCenteredOn puts the world point at the viewport centre', () => {
    const cam = cameraCenteredOn(40, 25, 8, 800, 600);
    expect(cam.scale).toBe(8);
    expect(worldToScreenX(cam, 40)).toBeCloseTo(400, 6);
    expect(worldToScreenY(cam, 25)).toBeCloseTo(300, 6);
  });

  it('tweenCamera lands exactly on the from / to endpoints', () => {
    const from: Camera = { scale: 1, x: 0, y: 0 };
    const to: Camera = { scale: 100, x: -500, y: -300 };
    const tween = { from, to, startMs: 1000, durationMs: 400 };

    const atStart = tweenCamera(tween, 1000, 800, 600);
    expect(atStart.camera.scale).toBeCloseTo(1, 6);
    expect(atStart.done).toBe(false);

    const atEnd = tweenCamera(tween, 1400, 800, 600);
    expect(atEnd.camera.scale).toBeCloseTo(100, 3);
    expect(atEnd.camera.x).toBeCloseTo(-500, 2);
    expect(atEnd.camera.y).toBeCloseTo(-300, 2);
    expect(atEnd.done).toBe(true);
  });

  it('frameWorldRect with fillFraction 1 fits exactly (same as fitToRect)', () => {
    const world = { x: 10, y: 20, w: 40, h: 30 };
    const framed = frameWorldRect(world, 800, 600, 24, 1);
    const fit = fitToRect(world, 800, 600, 24);
    expect(framed).toEqual(fit);
  });

  it('frameWorldRect with fillFraction 0.5 halves the fitted scale and keeps the region centred', () => {
    const world = { x: 10, y: 20, w: 40, h: 30 };
    const fit = fitToRect(world, 800, 600, 24);
    const framed = frameWorldRect(world, 800, 600, 24, 0.5);
    // Zoomed out 2× → the region spans about half the view, leaving context around it.
    expect(framed.scale).toBeCloseTo(fit.scale * 0.5, 6);
    // The region's centre still maps to the viewport centre (centred reveal).
    const cx = world.x + world.w / 2;
    const cy = world.y + world.h / 2;
    expect(framed.x + cx * framed.scale).toBeCloseTo(400, 6);
    expect(framed.y + cy * framed.scale).toBeCloseTo(300, 6);
  });

  it('tweenCamera interpolates scale in log space — the geometric mean at ease 0.5', () => {
    const from: Camera = { scale: 1, x: 0, y: 0 };
    const to: Camera = { scale: 100, x: 0, y: 0 };
    // The ease-out cubic 1-(1-t)^3 reaches 0.5 at t = 1 - cbrt(0.5); the scale there is √(1·100) = 10.
    const t = 1 - Math.cbrt(0.5);
    const sample = tweenCamera({ from, to, startMs: 0, durationMs: 1000 }, t * 1000, 800, 600);
    expect(sample.camera.scale).toBeCloseTo(10, 3);
  });

  it('tweenCamera with an anchor keeps the anchored world point pinned through the whole glide', () => {
    // A pure cursor zoom: `to` is `from` zoomed about the anchor, so the world point under the anchor must
    // stay fixed at every sampled frame — no mid-glide wobble.
    const from: Camera = { scale: 4, x: 100, y: 50 };
    const anchorX = 600;
    const anchorY = 200;
    const to = zoomAt(from, anchorX, anchorY, 3);
    const tween = { from, to, startMs: 0, durationMs: 1000, anchorX, anchorY };
    const pinned = { x: screenToWorldX(from, anchorX), y: screenToWorldY(from, anchorY) };
    for (const ms of [0, 120, 370, 600, 880, 1000]) {
      const { camera } = tweenCamera(tween, ms, 1280, 720);
      expect(screenToWorldX(camera, anchorX)).toBeCloseTo(pinned.x, 4);
      expect(screenToWorldY(camera, anchorY)).toBeCloseTo(pinned.y, 4);
    }
  });
});
