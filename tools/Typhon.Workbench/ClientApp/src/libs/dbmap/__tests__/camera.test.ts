import { describe, expect, it } from 'vitest';
import {
  fitToRect,
  panBy,
  screenToWorldX,
  screenToWorldY,
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
});
