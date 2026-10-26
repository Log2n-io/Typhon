import type { PickRay } from '../render/view-math';
import type { PlanBox, PlacedShape } from './structure-collision';

/**
 * The pick against the world's real geometry: the structures' published collision boxes, and the parts of a structure
 * that are things in their own right (a shuttleport's ticket terminal).
 *
 * <b>Why a structure is not picked as its style's box.</b> A structure drawn as its published asset is picked where its
 * walls are: the shuttleport's style box is 46 m square and 5 m tall, and anyone standing inside the port — on its open
 * plaza — was behind that box's face, so a click on them selected the port. Its collision boxes are its ring wall, its
 * pad and its terminal, with the plaza open between them, which is where the pick has to see through.
 */

/** The nearest hit of a ray, and on which placed shape: the index into the array the test was given. */
export interface ShapeHit {
  t: number;
  index: number;
}

/**
 * Where a ray first meets any of the first `count` placed shapes, within `maxT`. Planet coordinates.
 *
 * @returns Whether it met one; `out` then holds the distance along the ray and the shape's index.
 */
export function rayOnShapes(
  shapes: readonly PlacedShape[],
  count: number,
  ray: PickRay,
  maxT: number,
  out: ShapeHit,
): boolean {
  out.t = maxT;
  out.index = -1;
  for (let i = 0; i < count; i++) {
    const placed = shapes[i];
    const q = placed.quarters & 3;
    const bound = placed.shape.bounds[q];
    // The plan's bound first: a ray that misses it in plan meets none of its boxes.
    if (
      !(
        rayOnSlabs(
          ray,
          placed.x + bound.minX,
          -Infinity,
          placed.z + bound.minZ,
          placed.x + bound.maxX,
          Infinity,
          placed.z + bound.maxZ,
        ) < out.t
      )
    ) {
      continue;
    }

    for (const box of placed.shape.turned[q]) {
      const t = rayOnBox(ray, box, placed.x, placed.y, placed.z);
      if (t < out.t) {
        out.t = t;
        out.index = i;
      }
    }
  }

  return out.index >= 0;
}

/** A ray against one collider placed at `(x, y, z)`: its box, or its upright cylinder when it is round. */
export function rayOnBox(ray: PickRay, box: PlanBox, x: number, y: number, z: number): number {
  const minY = y + box.minY;
  const maxY = y + box.maxY;
  if (box.radius > 0) {
    return rayOnCylinder(ray, x + (box.minX + box.maxX) / 2, z + (box.minZ + box.maxZ) / 2, box.radius, minY, maxY);
  }

  return rayOnSlabs(ray, x + box.minX, minY, z + box.minZ, x + box.maxX, maxY, z + box.maxZ);
}

/** The distance along the ray to where it enters an axis-aligned box, or `Infinity`; 0 from inside it. */
export function rayOnSlabs(
  ray: PickRay,
  minX: number,
  minY: number,
  minZ: number,
  maxX: number,
  maxY: number,
  maxZ: number,
): number {
  let near = 0;
  let far = Infinity;
  for (let axis = 0; axis < 3; axis++) {
    const o = axis === 0 ? ray.ox : axis === 1 ? ray.oy : ray.oz;
    const d = axis === 0 ? ray.dx : axis === 1 ? ray.dy : ray.dz;
    const lo = axis === 0 ? minX : axis === 1 ? minY : minZ;
    const hi = axis === 0 ? maxX : axis === 1 ? maxY : maxZ;
    if (d === 0) {
      if (o < lo || o > hi) {
        return Infinity;
      }

      continue;
    }

    let t0 = (lo - o) / d;
    let t1 = (hi - o) / d;
    if (t0 > t1) {
      const swap = t0;
      t0 = t1;
      t1 = swap;
    }

    near = Math.max(near, t0);
    far = Math.min(far, t1);
    if (near > far) {
      return Infinity;
    }
  }

  return near;
}

/** The distance along the ray to where it enters an upright cylinder, or `Infinity`; 0 from inside it. */
export function rayOnCylinder(
  ray: PickRay,
  cx: number,
  cz: number,
  radius: number,
  minY: number,
  maxY: number,
): number {
  const ox = ray.ox - cx;
  const oz = ray.oz - cz;
  const a = ray.dx * ray.dx + ray.dz * ray.dz;
  const c = ox * ox + oz * oz - radius * radius;
  let near: number;
  let far: number;
  if (a === 0) {
    // Straight up or down: inside the circle for the whole ray, or never.
    if (c > 0) {
      return Infinity;
    }

    near = 0;
    far = Infinity;
  } else {
    const b = ox * ray.dx + oz * ray.dz;
    const disc = b * b - a * c;
    if (disc < 0) {
      return Infinity;
    }

    const root = Math.sqrt(disc);
    near = (-b - root) / a;
    far = (-b + root) / a;
  }

  // The vertical span, as the third slab.
  if (ray.dy === 0) {
    if (ray.oy < minY || ray.oy > maxY) {
      return Infinity;
    }
  } else {
    let t0 = (minY - ray.oy) / ray.dy;
    let t1 = (maxY - ray.oy) / ray.dy;
    if (t0 > t1) {
      const swap = t0;
      t0 = t1;
      t1 = swap;
    }

    near = Math.max(near, t0);
    far = Math.min(far, t1);
  }

  near = Math.max(near, 0);
  return near > far ? Infinity : near;
}
