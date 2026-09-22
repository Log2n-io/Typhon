import { describe, expect, it } from 'vitest';
import {
  MapCamera,
  MAX_DISTANCE_M,
  MAX_METRES_PER_PIXEL_RATIO,
  MIN_DISTANCE_M,
  type Ray,
} from '../src/camera/map-camera';

const ray = (): Ray => ({ ox: 0, oy: 0, oz: 0, dx: 0, dy: 0, dz: 0 });

describe('MapCamera', () => {
  it('looks at its target: the ray through the viewport centre hits the ground at the target', () => {
    const cam = new MapCamera();
    cam.jumpTo(1234, -567, 800);
    cam.rotateBy(0.7, 0.2);
    cam.update(10);
    const hit = new Float64Array(2);
    expect(MapCamera.groundHit(cam.rayThrough(400, 300, 800, 600, ray()), hit)).toBe(true);
    expect(hit[0]).toBeCloseTo(1234, 6);
    expect(hit[1]).toBeCloseTo(-567, 6);
  });

  it('keeps the ground point under the cursor fixed while zooming', () => {
    const cam = new MapCamera();
    cam.jumpTo(0, 0, 2000);
    const cursor = new Float64Array(2);
    expect(MapCamera.groundHit(cam.rayThrough(650, 420, 800, 600, ray()), cursor)).toBe(true);
    cam.zoomBy(0.5, cursor[0], cursor[1]);
    cam.update(10);
    const after = new Float64Array(2);
    expect(MapCamera.groundHit(cam.rayThrough(650, 420, 800, 600, ray()), after)).toBe(true);
    expect(after[0]).toBeCloseTo(cursor[0], 3);
    expect(after[1]).toBeCloseTo(cursor[1], 3);
  });

  it('follows by moving its goal: the camera glides instead of cutting', () => {
    const cam = new MapCamera();
    cam.jumpTo(0, 0, 500);
    cam.follow(1000, 0);
    expect(cam.targetX).toBe(0);
    cam.update(1 / 60);
    expect(cam.targetX).toBeGreaterThan(0);
    expect(cam.targetX).toBeLessThan(1000);
    cam.update(10);
    expect(cam.targetX).toBeCloseTo(1000, 6);
  });

  it('pans along its own ground axes: forward moves toward where it looks', () => {
    const cam = new MapCamera();
    cam.jumpTo(0, 0, 500);
    cam.rotateBy(Math.PI / 2, 0);
    cam.update(10);
    cam.panScreen(0, 100);
    cam.update(10);
    // Yaw 90°: looking along +X.
    expect(cam.targetX).toBeCloseTo(100, 6);
    expect(cam.targetZ).toBeCloseTo(0, 6);
  });

  it('clamps distance, pitch and the target to the planet', () => {
    const cam = new MapCamera();
    cam.jumpTo(99_999, -99_999, 1e9);
    expect(cam.distance).toBe(MAX_DISTANCE_M);
    expect(cam.targetX).toBe(8192);
    cam.zoomBy(1e-9);
    cam.update(10);
    expect(cam.distance).toBeCloseTo(MIN_DISTANCE_M, 6);
    cam.rotateBy(0, 10);
    cam.update(10);
    expect(cam.pitch).toBeLessThan(Math.PI / 2);
    expect(cam.eyeY).toBeGreaterThan(0);
  });

  it('misses the ground when looking at the sky', () => {
    expect(MapCamera.groundHit({ ox: 0, oy: 10, oz: 0, dx: 0, dy: 0.2, dz: 1 }, new Float64Array(2))).toBe(false);
  });

  it('ignores a cursor ray grazing the horizon: next to an accepted hit, one pixel moves it by at most 5 % of the distance', () => {
    const cam = new MapCamera();
    cam.jumpTo(0, 0, 3000);
    cam.rotateBy(0, -10); // lowest pitch
    cam.update(10);
    const height = 900;
    const limit = cam.maxGroundHitM(height);
    const hit = new Float64Array(2);
    const next = new Float64Array(2);
    let capped = 0;
    let accepted = 0;
    for (let py = 0; py < height / 2; py++) {
      const r = cam.rayThrough(800, py, 1600, height, ray());
      if (MapCamera.groundHit(r, hit, limit)) {
        accepted++;
        expect(MapCamera.groundHit(cam.rayThrough(800, py + 1, 1600, height, ray()), next, limit)).toBe(true);
        expect(Math.hypot(next[0] - hit[0], next[1] - hit[1])).toBeLessThanOrEqual(
          MAX_METRES_PER_PIXEL_RATIO * 3000 * 1.02,
        );
      } else if (MapCamera.groundHit(r, hit)) {
        capped++;
      }
    }

    // Some rows meet the ground too far away to trust, and the rows below them are still usable.
    expect(capped).toBeGreaterThan(0);
    expect(accepted).toBeGreaterThan(0);
  });

  it('scales its clip planes with distance', () => {
    const cam = new MapCamera();
    cam.jumpTo(0, 0, MIN_DISTANCE_M);
    expect(cam.nearPlane).toBeGreaterThan(0);
    const closeNear = cam.nearPlane;
    cam.jumpTo(0, 0, MAX_DISTANCE_M);
    expect(cam.nearPlane).toBeGreaterThan(closeNear);
    expect(cam.farPlane).toBeGreaterThan(cam.distance);
  });
});
