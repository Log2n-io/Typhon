/**
 * The client's half of collision: the same push-out the server runs, on the position it predicts.
 *
 * <b>Why the client collides at all, when the server is authoritative.</b> Prediction integrates the movement intent
 * forward so the avatar answers the key rather than the round trip; reconciliation pulls it back towards where the
 * server says it is. Without collision in the prediction, walking into a wall predicts straight through it while the
 * server pushes out, and the two settle into a standing error — `predictor.ts` states its own size: *"against a wall
 * the body parks at speed × (renderDelay + τ): 0.8 m at this demo's 41 ms delay, 2.1 m at the 300 ms ceiling"*. That
 * was survivable while a building was an opaque box. With 1.2 m doorways it is not: 0.8 m is the difference between
 * standing in the doorway and standing in the jamb.
 *
 * <b>This does not make the client authoritative.</b> It makes the prediction agree with the authority, so
 * reconciliation has nothing left to correct. Where the two differ — and they will, since no two floating-point
 * implementations agree exactly — the server wins, as it already does, and the residual is micrometres against the
 * 0.8 m it replaces.
 *
 * <b>The geometry is the server's, published in `/typhon/demo.json`.</b> This used to read the descriptors and apply the
 * same chest-height rule — and therefore needed the server's list of which assets that rule had been checked against,
 * which was on no wire. It invented a wall across the shuttleport's landing pad, at the centre of every city, and
 * fought the authority about it for ever. One reader of a descriptor, one copy of the rule, one published answer.
 *
 * The arithmetic is deliberately the same as `SimBridge.Proximity`'s, down to the order of the comparisons: a plan box
 * per blocking primitive, a quarter turn that keeps an AABB an AABB, and a push along the shallowest axis.
 */

/**
 * A collision primitive in an asset's own frame, as the server published it.
 *
 * <b>The vertical span is not decoration: it is what makes a box a floor, a step, a wall or a lintel.</b> Which one it
 * is depends on where the body's feet are, so it cannot be decided when the geometry is published — a building's deck
 * is a floor to someone climbing its stairs and a wall to someone walking into its side. Both ends apply the same rule
 * at the moment of contact, which is the only way prediction and authority can agree.
 */
export interface PlanBox {
  readonly minX: number;
  readonly minZ: number;
  readonly maxX: number;
  readonly maxZ: number;
  /** Underside, measured from the asset's own origin — the ground it stands on. */
  readonly minY: number;
  /** The bounding box's own top. */
  readonly maxY: number;
  /**
   * The top face's height at the box's plan centre — which is NOT `maxY` for a tilted collider.
   *
   * <b>This number was on the wire and this end was reading past it.</b> The server publishes nine, and the parser
   * here was written for eight: it took the plane as `slopeX` and `slopeX` as `slopeZ`. A house entry ramp then
   * predicted as a FLAT slab at its bounding box's top — 0.79 m above the desert, which is a wall — so the client
   * refused to walk up a ramp the server was perfectly happy to carry a body up, and the body stuck at its foot.
   */
  readonly planeY: number;
  /** How the top face rises per metre east; 0 for a flat collider. */
  readonly slopeX: number;
  /** How it rises per metre north. */
  readonly slopeZ: number;
  /**
   * The radius of the collider's plan when that plan is a circle, and 0 when it is the rectangle above.
   *
   * A round collider taken as the square around it is what walled off the shuttleport: its plaza is a 21.6 m cylinder,
   * whose bounding square has corners 8.9 m out in open desert.
   */
  readonly radius: number;
}

/**
 * How far a point lies outside a collider's plan, in metres; 0 inside it. The server's `PlanBox.OutsideBy`.
 */
export function outsideBy(box: PlanBox, x: number, z: number): number {
  if (box.radius > 0) {
    const rx = x - (box.minX + box.maxX) / 2;
    const rz = z - (box.minZ + box.maxZ) / 2;
    return Math.max(Math.hypot(rx, rz) - box.radius, 0);
  }

  const dx = Math.max(box.minX - x, 0, x - box.maxX);
  const dz = Math.max(box.minZ - z, 0, z - box.maxZ);
  return Math.hypot(dx, dz);
}

/** Whether a body of this half-extent standing here overlaps the collider's plan. The server's `PlanBox.Touches`. */
export function touches(box: PlanBox, x: number, z: number, half: number): boolean {
  return outsideBy(box, x, z) < half;
}

/** The steepest top face a body walks up, as a gradient: `tan(46°)`, the server's `MaxWalkableGrade`. */
const MAX_WALKABLE_GRADE = 1.0355;

/**
 * The top of a collider above a point, in the asset's own frame.
 *
 * <b>A ramp is a thin slab rotated about its length, and its bounding box is a wall.</b> Taking the highest corner
 * would make the foot of a ramp as impassable as its head; the plane makes it a step up where you meet it.
 */
export function topAt(box: PlanBox, x: number, z: number): number {
  if (box.slopeX === 0 && box.slopeZ === 0) {
    return box.maxY;
  }

  // Over the box's own plan, and nowhere else. Callers evaluate the top at the BODY's position and only afterwards ask
  // whether the body overlaps the box, and that test is generous by the body's half-extent -- so the plane was read up
  // to `half` metres outside the slab it describes, which on a 0.4 gradient is 0.12 m of lift that is not there. That
  // answer becomes the next frame's feet, and the next frame reads further out again: a ratchet no single evaluation
  // can show. A top face exists over its own footprint; past the edge, the edge's height stands.
  const px = Math.min(Math.max(x, box.minX), box.maxX);
  const pz = Math.min(Math.max(z, box.minZ), box.maxZ);
  const cx = (box.minX + box.maxX) / 2;
  const cz = (box.minZ + box.maxZ) / 2;
  // NOT `maxY`, which is the bounding box's roof: a 22-degree ramp's plane sits 0.83 m below it at the centre, and
  // reading the roof flattens the ramp into a kerb three steps tall. The server's `TopAt` reads `PlaneY` here too.
  const plane = Number.isFinite(box.planeY) ? box.planeY : box.maxY;
  return plane + box.slopeX * (px - cx) + box.slopeZ * (pz - cz);
}

/** Whether a collider's top face is shallow enough to stand on. */
export function walkable(box: PlanBox): boolean {
  return box.slopeX * box.slopeX + box.slopeZ * box.slopeZ <= MAX_WALKABLE_GRADE * MAX_WALKABLE_GRADE;
}

/**
 * Negation that returns 0 rather than -0.
 *
 * A flat collider turned a quarter gets `-0` for its slope, which is numerically identical to `0` and compares
 * unequal under a deep-equality check — so without this a test of the turn fails on a sign nobody can see.
 */
function negate(v: number): number {
  return v === 0 ? 0 : -v;
}

/** A plan box turned by a quarter about the asset's origin — Babylon's `rotation.y`, so `(x, z)` goes to `(z, -x)`. */
export function turn(quarters: number, box: PlanBox): PlanBox {
  switch (quarters & 3) {
    case 1:
      return { minX: box.minZ, minZ: -box.maxX, maxX: box.maxZ, maxZ: -box.minX, minY: box.minY, maxY: box.maxY,
               planeY: box.planeY, slopeX: box.slopeZ, slopeZ: negate(box.slopeX), radius: box.radius };
    case 2:
      return { minX: -box.maxX, minZ: -box.maxZ, maxX: -box.minX, maxZ: -box.minZ, minY: box.minY, maxY: box.maxY,
               planeY: box.planeY, slopeX: negate(box.slopeX), slopeZ: negate(box.slopeZ), radius: box.radius };
    case 3:
      return { minX: -box.maxZ, minZ: box.minX, maxX: -box.minZ, maxZ: box.maxX, minY: box.minY, maxY: box.maxY,
               planeY: box.planeY, slopeX: negate(box.slopeZ), slopeZ: box.slopeX, radius: box.radius };
    default:
      return box;
  }
}

/**
 * One asset's geometry, pre-turned for each of the four orientations and bounded for a cheap reject.
 *
 * <b>Turned once per asset, not once per primitive per frame.</b> A structure's quarter never changes, so turning at
 * use would allocate a box per primitive per pass on the frame path — about thirty thousand objects a second for a
 * street. The bound is the server's two-level test, mirrored: a body outside it cannot be inside any primitive, which
 * is the common case for every structure in range but the one being walked into.
 */
export interface AssetShape {
  /** By quarter turn, 0 to 3. */
  readonly turned: readonly (readonly PlanBox[])[];
  /** The bounding box of `turned[q]`, for the reject. */
  readonly bounds: readonly PlanBox[];
}

/** Pre-turns and bounds one asset's published boxes. */
export function prepareShape(boxes: readonly PlanBox[]): AssetShape {
  const turned: PlanBox[][] = [];
  const bounds: PlanBox[] = [];
  for (let q = 0; q < 4; q++) {
    const at = boxes.map((box) => turn(q, box));
    turned.push(at);
    let minX = Infinity;
    let minZ = Infinity;
    let maxX = -Infinity;
    let maxZ = -Infinity;
    for (const box of at) {
      minX = Math.min(minX, box.minX);
      minZ = Math.min(minZ, box.minZ);
      maxX = Math.max(maxX, box.maxX);
      maxZ = Math.max(maxZ, box.maxZ);
    }

    bounds.push({ minX, minZ, maxX, maxZ, minY: -Infinity, maxY: Infinity, planeY: Infinity, slopeX: 0, slopeZ: 0,
                  radius: 0 });
  }

  return { turned, bounds };
}

/** One structure standing in the world: its shape, where it is, and which way it faces. */
export interface PlacedShape {
  shape: AssetShape;
  x: number;
  z: number;
  quarters: number;
  /** The altitude the asset's origin sits at: its boxes' Y is measured from here. */
  y: number;
}

/** A position being pushed out, in place. */
export interface Position {
  x: number;
  z: number;
}

/** How many times the push-out sweeps the set before giving up, matching the server's `ProximityPasses`. */
const PASSES = 4;

/**
 * What the body steps onto rather than walks into, and how tall it is — the server's `PlayerStepM` and
 * `PlayerHeightM`, and the testbench's `stepHeight` and `height`.
 *
 * <b>Written here rather than published because they are the body's, not the world's</b>, and because both ends take
 * them from the same place the testbench did. If they ever become per-creature they belong on the wire with the gait
 * speeds, beside `body.halfM`.
 */
const STEP_M = 0.4;
const HEIGHT_M = 1.65;

/**
 * Whether a box blocks a body whose feet are at `feetY`, rather than being a floor it stands on or a lintel it
 * passes under.
 *
 * <b>The whole character controller is this one comparison.</b> A box whose top is within a step of the feet is
 * something to step onto; one that starts above the head is something to duck under; what is left is a wall. Deciding
 * it against an absolute height instead — which the server did until this landed — classifies a building's deck as a
 * wall and discards everything standing on that deck, which is how a body came to walk through a house at desert
 * level.
 */
function blocks(box: PlanBox, originY: number, feetY: number, x: number, z: number): boolean {
  if (!Number.isFinite(feetY)) {
    return true;
  }

  // The top WHERE THE BODY IS: the foot of a ramp is a step up and its head is a wall, and they are one collider.
  return originY + topAt(box, x, z) > feetY + STEP_M && originY + box.minY < feetY + HEIGHT_M;
}

/**
 * The altitude a body stands at here: the terrain, raised by any published surface within a step of its feet.
 *
 * The mirror of the server's `GroundOnShapes`. Without it the client predicts a body at terrain height while the
 * server has it standing on a deck, and every step is corrected.
 */
export function groundOnShapes(
  shapes: readonly PlacedShape[],
  count: number,
  half: number,
  at: Position,
  terrain: number,
  feetY: number,
): number {
  let best = terrain;
  const reach = Number.isFinite(feetY) ? feetY + STEP_M : Infinity;
  for (let i = 0; i < count; i++) {
    const placed = shapes[i];
    const q = placed.quarters & 3;
    const bound = placed.shape.bounds[q];
    if (
      at.x <= placed.x + bound.minX - half ||
      at.x >= placed.x + bound.maxX + half ||
      at.z <= placed.z + bound.minZ - half ||
      at.z >= placed.z + bound.maxZ + half
    ) {
      continue;
    }

    for (const box of placed.shape.turned[q]) {
      if (!walkable(box)) {
        continue;
      }

      const top = placed.y + topAt(box, at.x - placed.x, at.z - placed.z);
      if (top <= best || top > reach) {
        continue;
      }

      if (touches(box, at.x - placed.x, at.z - placed.z, half)) {
        best = top;
      }
    }
  }

  return best;
}

/**
 * Pushes a body out of the structures it is inside, shallowest axis first.
 *
 * @param shapes The structures near it, already placed.
 * @param count How many entries of `shapes` are live — the array is pooled.
 * @param half The body's half-extent, which the server publishes.
 * @param worldHalf Half the world's edge, which the server also publishes: it clamps after every push and so must this.
 * @param at The position, moved in place.
 * @returns Whether anything moved it.
 *
 * @remarks
 * Several passes because one push can put a body inside its neighbour: a corner between two walls resolves on the
 * second pass, and a body wedged where it does not fit stops moving rather than oscillating.
 *
 * A non-finite position is left alone. It would otherwise pass every "outside" test — `NaN < x` is false whichever way
 * it is written — so the body would be inside every box at once and leave with a poisoned position that nothing
 * recovers from.
 */
export function pushOut(
  shapes: readonly PlacedShape[],
  count: number,
  half: number,
  worldHalf: number,
  at: Position,
  feetY = Number.NaN,
): boolean {
  if (!Number.isFinite(at.x) || !Number.isFinite(at.z)) {
    return false;
  }

  let touched = false;
  let moved = true;
  for (let pass = 0; moved && pass < PASSES; pass++) {
    moved = false;
    for (let i = 0; i < count; i++) {
      const placed = shapes[i];
      const bound = placed.shape.bounds[placed.quarters & 3];
      // The cheap reject first, which is the server's footprint gate: outside the asset's own bound, no primitive of
      // it can be touching.
      if (
        at.x <= placed.x + bound.minX - half ||
        at.x >= placed.x + bound.maxX + half ||
        at.z <= placed.z + bound.minZ - half ||
        at.z >= placed.z + bound.maxZ + half
      ) {
        continue;
      }

      for (const box of placed.shape.turned[placed.quarters & 3]) {
        if (!blocks(box, placed.y, feetY, at.x - placed.x, at.z - placed.z)) {
          continue;
        }

        if (box.radius > 0) {
          // Out along the radius, which for a circle IS the shallowest axis. Resolving on x or z would eject the body
          // along a tangent, and over a 21.6 m plaza that is twenty metres of travel to leave something it was a
          // centimetre inside.
          const cxr = placed.x + (box.minX + box.maxX) / 2;
          const czr = placed.z + (box.minZ + box.maxZ) / 2;
          const rd = Math.hypot(at.x - cxr, at.z - czr);
          const want = box.radius + half;
          if (rd >= want) {
            continue;
          }

          // Dead centre: no radius to push along, so +X is as good as any other direction.
          const ux = rd > 1e-6 ? (at.x - cxr) / rd : 1;
          const uz = rd > 1e-6 ? (at.z - czr) / rd : 0;
          at.x = cxr + ux * want;
          at.z = czr + uz * want;
          if (worldHalf > 0) {
            at.x = Math.min(Math.max(at.x, -worldHalf + half), worldHalf - half);
            at.z = Math.min(Math.max(at.z, -worldHalf + half), worldHalf - half);
          }

          moved = true;
          continue;
        }

        const minX = placed.x + box.minX - half;
        const maxX = placed.x + box.maxX + half;
        const minZ = placed.z + box.minZ - half;
        const maxZ = placed.z + box.maxZ + half;
        if (at.x <= minX || at.x >= maxX || at.z <= minZ || at.z >= maxZ) {
          continue;
        }

        const west = at.x - minX;
        const east = maxX - at.x;
        const south = at.z - minZ;
        const north = maxZ - at.z;
        const dx = west < east ? -west : east;
        const dz = south < north ? -south : north;
        if (Math.abs(dx) <= Math.abs(dz)) {
          at.x += dx;
        } else {
          at.z += dz;
        }

        // Held inside the world after each push, exactly where the server does it: a push pulled back afterwards would
        // land the body back inside the box it had just left.
        if (worldHalf > 0) {
          at.x = Math.min(Math.max(at.x, -worldHalf + half), worldHalf - half);
          at.z = Math.min(Math.max(at.z, -worldHalf + half), worldHalf - half);
        }

        moved = true;
      }
    }

    touched = touched || moved;
  }

  return touched;
}
