import { describe, expect, it } from 'vitest';
import {
  bodyBox,
  FRAME_MIN_PX,
  frameGeometry,
  projectBox,
  styleBox,
  type ScreenRect,
} from '../src/render/selection-frame';

// Row-major perspective looking down +Z from the origin, 90° field of view, as in render-pure.test.ts.
const f = 1 / Math.tan(Math.PI / 4);
const m = new Float32Array(16);
m[0] = f;
m[5] = f;
m[10] = 1001 / 999;
m[11] = 1;
m[14] = -2000 / 999;

const W = 800;
const H = 800;
const scratch = { x: 0, y: 0, w: 0 };
const rect = (): ScreenRect => ({ left: 0, top: 0, right: 0, bottom: 0 });
/** Where a render-space point lands, by this projection: x / z and y / z scaled to the viewport. */
const sx = (x: number, z: number): number => (x / z / 2 + 0.5) * W;
const sy = (y: number, z: number): number => (1 - (y / z / 2 + 0.5)) * H;

describe('projectBox', () => {
  it('bounds the projections of the eight corners: the near face sets the extent', () => {
    const r = rect();
    // A 2 m cube standing on y = −1, centred 10 m ahead: its corners span x, y ∈ [−1, 1] and z ∈ [9, 11].
    expect(projectBox(m, 0, -1, 10, 0, styleBox(2, 2, 2), W, H, scratch, r)).toBe(true);
    expect(r.left).toBeCloseTo(sx(-1, 9), 6);
    expect(r.right).toBeCloseTo(sx(1, 9), 6);
    expect(r.top).toBeCloseTo(sy(1, 9), 6);
    expect(r.bottom).toBeCloseTo(sy(-1, 9), 6);
  });

  it("turns with the instance shaders' yaw: a long body broadside to the eye is framed wider than one end on", () => {
    // 0.2 m across, 4 m along its facing: yaw 0 points it down the view, π/2 across it (`turned.x = lx·c + lz·s`).
    const box = styleBox(0.2, 1, 4);
    const endOn = rect();
    const broadside = rect();
    expect(projectBox(m, 0, 0, 20, 0, box, W, H, scratch, endOn)).toBe(true);
    expect(projectBox(m, 0, 0, 20, Math.PI / 2, box, W, H, scratch, broadside)).toBe(true);
    expect(broadside.right - broadside.left).toBeGreaterThan(5 * (endOn.right - endOn.left));
    // Its width is depth now: the nearest corners are 0.1 m in front of its centre.
    expect(broadside.left).toBeCloseTo(sx(-2, 19.9), 6);
  });

  it('frames nothing the camera stands inside or behind, and nothing wholly off screen', () => {
    const r = rect();
    // z ∈ [−0.5, 1.5]: two corners are behind the eye, and a rectangle round their NaN projections frames nothing.
    expect(projectBox(m, 0, 0, 0.5, 0, styleBox(1, 1, 2), W, H, scratch, r)).toBe(false);
    expect(projectBox(m, 0, 0, -10, 0, styleBox(1, 1, 1), W, H, scratch, r)).toBe(false);
    expect(projectBox(m, 100, 0, 10, 0, styleBox(1, 1, 1), W, H, scratch, r)).toBe(false);
  });
});

describe('frameGeometry', () => {
  it('pads the body, rounds to whole pixels, and gives the corners a share of the shorter side', () => {
    const g = frameGeometry({ left: 100.4, top: 50.2, right: 140.4, bottom: 150.2 });
    // 40 × 100 plus 3 px of padding each side, about the same centre.
    expect([g.x, g.y, g.w, g.h]).toEqual([97, 47, 46, 106]);
    expect(g.corner).toBe(Math.round(46 * 0.22));
  });

  it('never draws a frame smaller than its minimum, grown about the body centre', () => {
    const g = frameGeometry({ left: 200, top: 200, right: 202, bottom: 204 });
    expect([g.w, g.h]).toEqual([FRAME_MIN_PX, FRAME_MIN_PX]);
    expect(g.x + g.w / 2).toBe(201);
    expect(g.y + g.h / 2).toBe(202);
    expect(g.corner).toBe(4);
  });

  it('caps the corner arm on a large frame', () => {
    expect(frameGeometry({ left: 0, top: 0, right: 600, bottom: 400 }).corner).toBe(16);
  });
});

describe('bodyBox', () => {
  // casual_male-2: arms out in its bind pose, 0.6 m wide where the server walks it.
  const render = [
    [-0.8494, -0.0097, -0.3179],
    [0.8494, 1.7338, 0.3766],
  ] as const;
  const collision = [
    [-0.3, 0, -0.3],
    [0.3, 1.65, 0.3],
  ] as const;

  it("takes a character's width from its collision box and its height from what is drawn", () => {
    expect(bodyBox('character', render, collision)).toEqual({
      minX: -0.3,
      minY: -0.0097,
      minZ: -0.3,
      maxX: 0.3,
      maxY: 1.7338,
      maxZ: 0.3,
    });
  });

  it("takes a creature's render box whole: its bind pose is how it stands", () => {
    const rat = [
      [-0.1278, -0.0002, -0.8441],
      [0.1278, 0.394, 0.3559],
    ] as const;
    expect(bodyBox('creature', rat, null)).toEqual({
      minX: -0.1278,
      minY: -0.0002,
      minZ: -0.8441,
      maxX: 0.1278,
      maxY: 0.394,
      maxZ: 0.3559,
    });
  });
});
