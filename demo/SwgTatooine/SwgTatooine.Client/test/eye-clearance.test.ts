import { describe, expect, it } from 'vitest';
import { EyeCamera } from '../src/camera/eye-camera';
import { bodyOpacityAt, EYE_RADIUS_M, marchEye, MIN_EYE_DISTANCE_M, WALL_MARGIN_M } from '../src/camera/eye-clearance';
import { prepareShape, type PlacedShape, type PlanBox } from '../src/app/structure-collision';

function box(over: Partial<PlanBox>): PlanBox {
  return {
    minX: -1,
    minZ: -1,
    maxX: 1,
    maxZ: 1,
    minY: 0,
    maxY: 2,
    planeY: 2,
    slopeX: 0,
    slopeZ: 0,
    radius: 0,
    ...over,
  };
}

/** One structure at the origin made of the given boxes. */
function placed(boxes: PlanBox[], y = 0): PlacedShape[] {
  return [{ shape: prepareShape(boxes), x: 0, z: 0, quarters: 0, y }];
}

/** A wall across +Z, 0.4 m thick, its near face at z 3: two metres tall, like every published wall. */
const WALL = box({ minX: -5, maxX: 5, minZ: 3, maxZ: 3.4, minY: 0, maxY: 2 });

describe('marchEye', () => {
  it('stops the eye sphere at the first wall behind the head', () => {
    const t = marchEye(placed([WALL]), 1, 0, 1.7, 0, 0, 0, 1, 10);
    expect(t).toBeCloseTo(3 - EYE_RADIUS_M, 2);
  });

  it('takes a wall as reaching the sky: a ray climbing over its two metres still meets it', () => {
    // Up at 45°: past the wall's published top long before it reaches the wall's plane, which is how the eye got into roofs.
    const s = Math.SQRT1_2;
    const t = marchEye(placed([WALL]), 1, 0, 1.7, 0, 0, s, s, 10);
    expect(t).toBeLessThan(3 / s);
  });

  it('takes a buried ramp as the floor it is: the head of a body at its foot is not inside a wall', () => {
    // `tat_house_large`'s ramp as published: a sloped box from 0.7 m under the ground up to the porch, 1.98 m tall. Its
    // plan covers the whole ramp, so measured by its own height it swallowed the head and the eye fell to the minimum.
    const ramp = box({ minX: -2, maxX: 2, minZ: -2, maxZ: 2, minY: -0.7, maxY: 1.28, slopeX: -0.4 });
    expect(marchEye(placed([ramp]), 1, 0, 1.7, 0, 1, 0, 0, 10)).toBe(10);
  });

  it('still takes a wall standing on higher ground as a wall', () => {
    // Its own height is 1.2 m, but its top is 2.2 m above the feet: it hides the head from behind.
    const terrace = box({ minX: -5, maxX: 5, minZ: 3, maxZ: 3.4, minY: 1, maxY: 2.2 });
    expect(marchEye(placed([terrace]), 1, 0, 1.7, 0, 0, Math.SQRT1_2, Math.SQRT1_2, 10)).toBeLessThan(3 * Math.SQRT2);
  });

  it('lets the eye go over something low: a step is not a wall', () => {
    const step = box({ minX: -5, maxX: 5, minZ: 3, maxZ: 3.4, minY: 0, maxY: 0.5 });
    expect(marchEye(placed([step]), 1, 0, 1.7, 0, 0, 0, 1, 10)).toBe(10);
  });

  it('sees a round collider as round', () => {
    const pillar = box({ minX: 2, maxX: 4, minZ: 2, maxZ: 4, minY: 0, maxY: 3, radius: 1 });
    // Along the diagonal towards (3, 3): the cylinder's surface is sqrt(18) - 1 away, the square's corner nearer.
    const s = Math.SQRT1_2;
    const t = marchEye(placed([pillar]), 1, 0, 1.7, 0, s, 0, s, 10);
    expect(t).toBeCloseTo(Math.sqrt(18) - 1 - EYE_RADIUS_M, 2);
  });
});

describe('the eye camera against walls', () => {
  function camera(): EyeCamera {
    const c = new EyeCamera();
    c.setDistance(6);
    const shapes = placed([WALL]);
    c.clearance = (fx, fy, fz, dx, dy, dz, max) => marchEye(shapes, 1, fx, fy, fz, dx, dy, dz, max);
    return c;
  }

  it('pulls in at once to stay short of the wall, and never closer than the minimum', () => {
    const c = camera();
    // Facing -Z: the eye goes back towards +Z, into the wall 3 m behind.
    c.jumpTo(0, 0, 0, Math.PI);
    expect(c.effectiveDistance).toBeCloseTo(3 - EYE_RADIUS_M - WALL_MARGIN_M, 2);
    expect(c.eyeZ).toBeLessThan(3);
    expect(c.effectiveDistance).toBeGreaterThanOrEqual(MIN_EYE_DISTANCE_M);
  });

  it('eases back out when the wall is gone, rather than snapping', () => {
    const c = camera();
    c.jumpTo(0, 0, 0, Math.PI);
    const pulled = c.effectiveDistance;
    c.clearance = null;
    c.update(0.05);
    expect(c.effectiveDistance).toBeGreaterThan(pulled);
    expect(c.effectiveDistance).toBeLessThan(6);
    for (let i = 0; i < 100; i++) {
      c.update(0.05);
    }

    expect(c.effectiveDistance).toBeCloseTo(6, 2);
  });

  it("gives the street's zoom back after a room: the room's cap is not written into the distance asked for", () => {
    const c = new EyeCamera();
    c.setDistance(20);
    c.jumpTo(0, 0, 0, 0);
    c.maxDistanceM = 7;
    for (let i = 0; i < 100; i++) {
      c.update(0.05);
    }

    expect(c.effectiveDistance).toBeCloseTo(7, 2);
    c.maxDistanceM = Infinity;
    for (let i = 0; i < 200; i++) {
      c.update(0.05);
    }

    expect(c.effectiveDistance).toBeCloseTo(20, 2);
  });

  it('zooms from the distance in use under the cap, so the wheel moves the eye at once', () => {
    const c = new EyeCamera();
    c.setDistance(20);
    c.maxDistanceM = 7;
    c.zoomBy(0.5);
    c.jumpTo(0, 0, 0, 0);
    expect(c.effectiveDistance).toBeCloseTo(3.5, 2);
  });

  it('caps the eye under a ceiling by flattening the pitch, not by clamping the height', () => {
    const c = new EyeCamera();
    c.setDistance(6);
    c.maxEyeHeightM = 1.9;
    c.jumpTo(0, 0, 0, 0);
    c.rotateBy(0, 0.8); // looking down: the eye would rise behind the body
    for (let i = 0; i < 200; i++) {
      c.update(0.05);
    }

    expect(c.eyeY).toBeLessThanOrEqual(1.9 + 1e-6);
    expect(c.effectiveDistance).toBeCloseTo(6, 2); // still 6 m back, just lower

    // And the view aims along the ray the eye was placed on, so it still looks at the head: placed with the flattened
    // pitch and aimed with the asked-for one, it looked below the head by the difference.
    expect(c.viewPitch).toBeLessThan(c.pitch);
    expect(c.eyeY).toBeCloseTo(1.7 + Math.sin(c.viewPitch) * c.effectiveDistance, 6);
  });
});

describe('bodyOpacityAt', () => {
  it("fades the body out as the eye closes in on its head: the testbench's 1.6 to 0.6 m", () => {
    expect(bodyOpacityAt(4)).toBe(1);
    expect(bodyOpacityAt(1.6)).toBe(1);
    expect(bodyOpacityAt(1.1)).toBeCloseTo(0.5, 9);
    expect(bodyOpacityAt(0.6)).toBe(0);
    expect(bodyOpacityAt(0)).toBe(0);
  });
});
