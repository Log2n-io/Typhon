import { Vector2 } from '@babylonjs/core/Maths/math.vector';
import { describe, expect, it } from 'vitest';
import { MapCamera, type Ray } from '../src/camera/map-camera';
import { fogFor } from '../src/render/ground';
/**
 * The rest of the rendering side that can be tested without a GPU: the haze that has to serve two cameras at once, and the
 * map camera obligation to stay above ground. The terrain LOD lives in `terrain-quadtree.test.ts`.
 */

describe('fogFor', () => {
  const out = new Vector2();

  it('is thick and total at the eye, thin and tinted from altitude', () => {
    fogFor(0, out);
    const ground = out.clone();
    fogFor(25000, out);
    // At the eye the far field must reach the sky colour COMPLETELY: what lies past it is the planet's darkened edge, and
    // at eye level that darkening is a brown sky rather than the map affordance it is from above.
    expect(ground.x).toBeGreaterThan(out.x * 2);
    expect(ground.y).toBeCloseTo(1, 2);
    expect(out.y).toBeCloseTo(0.85, 2);
  });

  it('falls off monotonically with height, so there is no altitude where the haze thickens', () => {
    let previous = Infinity;
    for (let y = 0; y <= 20000; y += 250) {
      fogFor(y, out);
      expect(out.x).toBeLessThan(previous);
      previous = out.x;
    }
  });

  it('treats a camera below sea level as one at it', () => {
    fogFor(-500, out);
    const below = out.x;
    fogFor(0, out);
    expect(below).toBe(out.x);
  });
});

describe('MapCamera over terrain', () => {
  it('orbits the ground under its target, so the eye rises with the terrain', () => {
    const camera = new MapCamera();
    camera.jumpTo(0, 0, 100);
    const overSeaLevel = camera.altitude;
    camera.groundY = 130;
    camera.update(0);
    // Exactly 130 m higher: the camera has not changed its pitch or distance, only what it considers the ground.
    expect(camera.altitude).toBeCloseTo(overSeaLevel + 130, 6);
  });

  it('never puts the eye inside a mesa: the eye stays above the ground at every pitch and distance', () => {
    const camera = new MapCamera();
    for (const distance of [6, 50, 500, 20000]) {
      for (const pitch of [0.21, 0.6, 1.5]) {
        camera.jumpTo(0, 0, distance);
        camera.pitch = pitch;
        camera.groundY = 131;
        camera.update(0);
        expect(camera.altitude).toBeGreaterThan(131);
      }
    }
  });

  it('meets a raised ground plane, not sea level', () => {
    // A ray from 200 m up pointing straight down meets ground at 130 m after 70 m, not after 200.
    const ray: Ray = { ox: 10, oy: 200, oz: -20, dx: 0, dy: -1, dz: 0 };
    const out = new Float64Array(2);
    expect(MapCamera.groundHit(ray, out, Infinity, 130)).toBe(true);
    expect(out[0]).toBe(10);
    expect(out[1]).toBe(-20);
    // And the distance limit is measured to THAT plane: 70 m of travel passes a 100 m limit that 200 m would fail.
    expect(MapCamera.groundHit(ray, out, 100, 130)).toBe(true);
    expect(MapCamera.groundHit(ray, out, 100, 0)).toBe(false);
  });

  it('refuses a plane that is behind the ray — a camera under the ground must not pan by its own reflection', () => {
    const ray: Ray = { ox: 0, oy: 50, oz: 0, dx: 0, dy: -1, dz: 0 };
    const out = new Float64Array(2);
    expect(MapCamera.groundHit(ray, out, Infinity, 130)).toBe(false);
  });

  it('bounds a cursor drag by the height above the GROUND, not above sea level', () => {
    // `maxGroundHitM` caps how far a ray may travel before a pixel spans too much ground. Measured from sea level it would
    // report a camera 2 m over a 130 m mesa as being 132 m up, and allow a drag sixty times longer than it should.
    const camera = new MapCamera();
    camera.jumpTo(0, 0, 40);
    const overSeaLevel = camera.maxGroundHitM(800);
    camera.groundY = 130;
    camera.update(0);
    expect(camera.maxGroundHitM(800)).toBeCloseTo(overSeaLevel, 6);
  });
});
