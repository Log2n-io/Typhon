import type { PlacedShape, PlanBox } from '../app/structure-collision';

/**
 * How far the eye can pull back before it meets a building: a sphere traced through the distance field of the structures'
 * collision primitives — the testbench's camera (`orbit-camera.ts`, `collision-world.ts` `march`), on the shapes the
 * prediction already collides with.
 *
 * <b>Walls reach the sky, for the camera.</b> Colliders are published only up to about two metres above the floor (roofs,
 * domes and lintels are dropped), so a ray climbing behind a body leaves them within a metre or two and the eye ends up
 * inside a dome that has no collider — the testbench's known hole outside. Here a primitive whose top stands
 * {@link WALL_TOP_M} or more above the body's FEET (the server's chest-height rule) is taken as unbounded upward: a house
 * pulls the eye in rather than letting it slip over the wall into the roof. Steps, ramps, porches and terminals stay below
 * that, keep their height, and the eye goes over them.
 *
 * <b>Measured against the feet, not against the primitive's own base.</b> Colliders are buried: a house's ramp is a sloped
 * box from 0.7 m under the ground to the porch, 1.98 m tall, so judged by its own height it was a wall to the sky, and since
 * its plan covers the whole ramp, a body walking up to a door had its head inside it and the eye collapsed to the minimum —
 * in the open, with nothing behind the body. Its top is 0.7 m above feet standing at its foot.
 */

/** Height of the eye's origin, the head, above the followed body's feet, metres. A human's, near enough to read as one. */
export const EYE_HEIGHT_M = 1.7;

/** The eye's radius, metres: the testbench's 0.2 sphere. */
export const EYE_RADIUS_M = 0.2;

/** How far short of a wall the eye stops, metres, beyond its radius. */
export const WALL_MARGIN_M = 0.15;

/** The nearest the eye comes to the head, metres: the fade has hidden the body well before this. */
export const MIN_EYE_DISTANCE_M = 0.3;

/** A primitive whose top stands this far above the body's feet blocks the camera all the way up. */
export const WALL_TOP_M = 1.6;

/** A sphere trace gives up after this many steps — the testbench's cap. */
const MAX_STEPS = 64;

/**
 * Signed distance from a point to one primitive placed in the world.
 *
 * @param wallY A primitive whose top reaches this height is a wall to the sky: the body's feet plus {@link WALL_TOP_M}.
 */
function primitiveDistance(box: PlanBox, place: PlacedShape, x: number, y: number, z: number, wallY: number): number {
  const base = place.y + box.minY;
  const top = place.y + box.maxY >= wallY ? Infinity : place.y + box.maxY;
  const minX = place.x + box.minX;
  const maxX = place.x + box.maxX;
  const minZ = place.z + box.minZ;
  const maxZ = place.z + box.maxZ;

  // Vertical: how far above the top or below the base, negative inside the span.
  const dy = Math.max(base - y, y - top);
  let planar: number;
  if (box.radius > 0) {
    planar = Math.hypot(x - (minX + maxX) * 0.5, z - (minZ + maxZ) * 0.5) - box.radius;
  } else {
    const dx = Math.max(minX - x, x - maxX);
    const dz = Math.max(minZ - z, z - maxZ);
    planar = dx > 0 || dz > 0 ? Math.hypot(Math.max(dx, 0), Math.max(dz, 0)) : Math.max(dx, dz);
  }

  return planar > 0 || dy > 0 ? Math.hypot(Math.max(planar, 0), Math.max(dy, 0)) : Math.max(planar, dy);
}

/**
 * Distance from a point to the nearest primitive of the shapes within `range` of it; `range` when none is nearer.
 *
 * @param wallY See {@link primitiveDistance}.
 */
export function clearanceAt(
  shapes: readonly PlacedShape[],
  count: number,
  x: number,
  y: number,
  z: number,
  range: number,
  wallY: number,
): number {
  let best = range;
  for (let i = 0; i < count; i++) {
    const place = shapes[i];
    const q = place.quarters & 3;
    const b = place.shape.bounds[q];
    // The cheap reject: a structure whose plan is further than the best so far cannot hold anything nearer.
    const ox = Math.max(place.x + b.minX - x, x - place.x - b.maxX, 0);
    const oz = Math.max(place.z + b.minZ - z, z - place.z - b.maxZ, 0);
    if (ox * ox + oz * oz >= best * best) {
      continue;
    }

    for (const box of place.shape.turned[q]) {
      const d = primitiveDistance(box, place, x, y, z, wallY);
      if (d < best) {
        best = d;
      }
    }
  }

  return best;
}

/**
 * How far a sphere of {@link EYE_RADIUS_M} can travel from `from` along the unit `dir` before touching a primitive, capped
 * at `max` — the testbench's `CollisionWorld.march`.
 *
 * @param fromY The head: the body's feet plus {@link EYE_HEIGHT_M}, which is where the walls are measured from.
 */
export function marchEye(
  shapes: readonly PlacedShape[],
  count: number,
  fromX: number,
  fromY: number,
  fromZ: number,
  dirX: number,
  dirY: number,
  dirZ: number,
  max: number,
): number {
  const wallY = fromY - EYE_HEIGHT_M + WALL_TOP_M;
  let t = 0;
  for (let i = 0; i < MAX_STEPS && t < max; i++) {
    const d =
      clearanceAt(shapes, count, fromX + dirX * t, fromY + dirY * t, fromZ + dirZ * t, max, wallY) - EYE_RADIUS_M;
    if (d < 1e-3) {
      return t;
    }

    t += d;
  }

  return Math.min(t, max);
}

/** The character's opacity for an eye this far from its head: gone under 0.6 m, whole past 1.6 m (the testbench's). */
export function bodyOpacityAt(eyeDistance: number): number {
  return Math.min(1, Math.max(0, (eyeDistance - 0.6) / (1.6 - 0.6)));
}
