/**
 * Where an entity is, read from an evaluated motion record (CLI3D-04).
 *
 * **This module exists because the offsets are not constants.** `evaluateSlot` writes `p[dims]` then `v[dims]`, so a
 * three-axis archetype's velocity starts at index 3, not 2. Every reader in this client assumed 2 — which in a `pos3`
 * realm silently draws altitude as the second ground axis and reads a velocity component as a heading. Five places had
 * the assumption baked in; they now all come through here, so there is one place to be right.
 *
 * **Axes.** The engine's axis 0 and 1 are the ground, axis 2 is altitude, which is how the demo routes and how a flat
 * realm's `pos2` is defined (axes 0 and 1, `03-wire-protocol.md` § 4). The renderer's own convention is x/y/z with **y
 * up**, so axis 1 becomes `z` and axis 2 becomes `y`: that swap happens once, here.
 */

/** The engine axis carrying the first ground coordinate. */
export const AXIS_X = 0;
/** The engine axis carrying the second ground coordinate — the renderer's `z`. */
export const AXIS_GROUND2 = 1;
/** The engine axis carrying altitude — the renderer's `y`. Present only when a store has three dimensions. */
export const AXIS_UP = 2;

/**
 * One entity's evaluated position and velocity in the renderer's axes. Reused by its owner: reading rewrites it, so a
 * frame that walks ten thousand entities allocates nothing.
 */
export class Placement {
  /** First ground axis. */
  x = 0;
  /** Altitude; always 0 for a two-dimensional store. */
  y = 0;
  /** Second ground axis. */
  z = 0;
  vx = 0;
  vy = 0;
  vz = 0;

  /**
   * Reads a motion record `evaluateSlot` wrote.
   *
   * @param dims The store's dimensions, 2 or 3 — `ArchetypeStore.dims`, never a constant.
   * @param motion The evaluated record.
   * @param offset Where the record starts; records are `2 × dims` values apart.
   * @returns This placement.
   */
  read(dims: number, motion: Float64Array, offset = 0): this {
    const deep = dims > AXIS_UP;
    this.x = motion[offset + AXIS_X]!;
    this.z = motion[offset + AXIS_GROUND2]!;
    this.y = deep ? motion[offset + AXIS_UP] : 0;
    this.vx = motion[offset + dims + AXIS_X]!;
    this.vz = motion[offset + dims + AXIS_GROUND2]!;
    this.vy = deep ? motion[offset + dims + AXIS_UP] : 0;
    return this;
  }

  /** Speed over the ground, in metres per second, from a tick period in milliseconds. Altitude is deliberately not in it. */
  groundSpeedMps(tickPeriodMs: number): number {
    return (Math.hypot(this.vx, this.vz) * 1000) / tickPeriodMs;
  }

  /** Heading in degrees, clockwise from +z, from the GROUND velocity — an entity climbing straight up keeps its heading. */
  headingDeg(): number {
    return ((Math.atan2(this.vx, this.vz) * 180) / Math.PI + 360) % 360;
  }
}
