import { describe, expect, it } from 'vitest';
import {
  Band,
  chooseBand,
  ENTER_NEAR_PX,
  extractFrustumPlanes,
  LEAVE_NEAR_PX,
  MODE_LIMIT,
  packedStyle,
  packState,
  projectToScreen,
  RenderOrigin,
  SELECTED_BIT,
  sphereInFrustum,
  STYLE_LIMIT,
} from '../src/render/view-math';

/** A Babylon-style row-major view-projection: perspective looking down +Z from the origin (left-handed, depth −1..1). */
function perspective(fovY: number, aspect: number, near: number, far: number): Float32Array {
  const f = 1 / Math.tan(fovY / 2);
  const m = new Float32Array(16);
  m[0] = f / aspect;
  m[5] = f;
  m[10] = (far + near) / (far - near);
  m[11] = 1;
  m[14] = (-2 * far * near) / (far - near);
  return m;
}

describe('LOD band', () => {
  it('uses hysteresis: enters near at the high threshold, leaves below the low one', () => {
    expect(chooseBand(Band.Far, ENTER_NEAR_PX - 0.1)).toBe(Band.Far);
    expect(chooseBand(Band.Far, ENTER_NEAR_PX)).toBe(Band.Near);
    expect(chooseBand(Band.Near, LEAVE_NEAR_PX)).toBe(Band.Near);
    expect(chooseBand(Band.Near, LEAVE_NEAR_PX - 0.1)).toBe(Band.Far);
  });

  it('starts a new entity on the side of the midpoint', () => {
    expect(chooseBand(Band.Unset, 6.5)).toBe(Band.Near);
    expect(chooseBand(Band.Unset, 5.5)).toBe(Band.Far);
  });
});

describe('packState', () => {
  it('packs style, mode and selection into a small integer a float32 holds exactly', () => {
    const packed = packState(13, 5, true);
    expect(Math.fround(packed)).toBe(packed);
    expect(packed % STYLE_LIMIT).toBe(13);
    expect(Math.floor(packed / STYLE_LIMIT) % MODE_LIMIT).toBe(5);
    expect(Math.floor(packed / SELECTED_BIT) % 2).toBe(1);
    expect(packedStyle(packed)).toBe(13);
    expect(Math.floor(packState(13, 5, false) / SELECTED_BIT) % 2).toBe(0);
  });

  it('draws a style index the shaders have no entry for as style 0', () => {
    expect(packedStyle(packState(STYLE_LIMIT, 0, false))).toBe(0);
    expect(packedStyle(packState(-1, 0, false))).toBe(0);
    expect(packedStyle(packState(Number.NaN, 0, false))).toBe(0);
  });
});

describe('frustum', () => {
  const m = perspective(Math.PI / 2, 1, 1, 1000);
  const planes = new Float64Array(24);
  extractFrustumPlanes(m, planes);

  it('keeps what is in front and inside the field of view', () => {
    expect(sphereInFrustum(planes, 0, 0, 100, 1)).toBe(true);
    expect(sphereInFrustum(planes, 90, 0, 100, 1)).toBe(true);
  });

  it('culls behind, beyond the far plane and outside the sides, with the radius as margin', () => {
    expect(sphereInFrustum(planes, 0, 0, -10, 1)).toBe(false);
    expect(sphereInFrustum(planes, 0, 0, 2000, 1)).toBe(false);
    expect(sphereInFrustum(planes, 150, 0, 100, 1)).toBe(false);
    // 110 m off at 100 m ahead is ≈ 7 m outside the 45° side plane: a 10 m radius keeps it.
    expect(sphereInFrustum(planes, 110, 0, 100, 10)).toBe(true);
  });

  it('projects the centre of view to the centre of the viewport and rejects points behind the camera', () => {
    const out = { x: 0, y: 0, w: 0 };
    projectToScreen(m, 0, 0, 50, 800, 600, out);
    expect(out.x).toBeCloseTo(400, 6);
    expect(out.y).toBeCloseTo(300, 6);
    projectToScreen(m, 0, 10, 10, 800, 600, out);
    expect(out.y).toBeLessThan(300);
    projectToScreen(m, 0, 0, -5, 800, 600, out);
    expect(out.w).toBeLessThanOrEqual(0);
    expect(Number.isNaN(out.x)).toBe(true);
  });
});

describe('RenderOrigin', () => {
  it('snaps to the grid and moves only when the camera crosses a cell', () => {
    const origin = new RenderOrigin(1024);
    expect(origin.follow(100, -100)).toBe(false);
    expect(origin.follow(600, 0)).toBe(true);
    expect(origin.x).toBe(1024);
    expect(origin.follow(1400, 0)).toBe(false);
    expect(origin.follow(-3000, 5000)).toBe(true);
    expect([origin.x, origin.z]).toEqual([-3072, 5120]);
  });
});
