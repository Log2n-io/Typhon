import type { ArchetypeStore, FieldArray } from '@typhondb/client';

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

/**
 * The wire name of the altitude a placement carries beside its position (terrain rung (b)).
 *
 * It is a component field and **not** a position axis, deliberately: a vertical step folded into `pos` is measured by the
 * motion tolerance as a horizontal jump, so walking up a cliff would replicate as a teleport.
 */
export const ALTITUDE_FIELD = 'y';

/**
 * The store's altitude field, or `null` when it has none.
 *
 * Absent is a real case and not a failure: the browser-side mock serves no such field, and neither does any catalog
 * older than rung (b). {@link altitudeOf} says what to do then.
 */
export function altitudeField(store: ArchetypeStore): FieldArray | null {
  const index = store.fieldIndex(ALTITUDE_FIELD);
  return index < 0 ? null : store.fieldAt(index);
}

/**
 * Where to draw an entity, in metres above sea level.
 *
 * **With the server's field, its value is the answer and nothing is added to it.** The server samples the same baked
 * heightfield this client draws — one spec, one golden, `TerrainGoldenChecks` — so adding the client's own ground on top
 * would double the relief. It also carries the cases the client cannot know: an entity inside a building is on that
 * realm's flat floor, not on the mesa the door happens to sit on.
 *
 * **Without it, the old guess.** `at.y` is the position's third axis, which is 0 in a flat realm and real in the space
 * realm, and the ground underneath comes from this client's own field. That is what every frame did before rung (b) and
 * it is still what the mock needs.
 *
 * @param field The store's altitude field from {@link altitudeField}, or `null`.
 * @param slot The entity's slot in that store.
 * @param at The evaluated placement, for the fallback.
 * @param ground The client's own heightfield, for the fallback.
 */
export function altitudeOf(
  field: FieldArray | null,
  slot: number,
  at: Placement,
  ground: { heightAt(x: number, z: number): number },
): number {
  return field === null ? at.y + ground.heightAt(at.x, at.z) : field[slot];
}

/** Below this, the server's altitude over this client's terrain is quantisation, not a deck: 16 bits over 480 m is 7.3 mm. */
export const DECK_MIN_M = 0.05;

/**
 * Where to draw a MOVING entity on a terrain realm: this client's ground under the position it DRAWS, plus whatever the
 * server says it stands on above that ground — a deck, a ramp — measured where the server last placed it.
 *
 * <b>Why not the server's altitude as it is</b> ({@link altitudeOf}): `y` is the latest sample, a tick newer than the
 * interpolated, render-delayed x/z it is drawn with, and it only changes per update. On a slope a body floated or sank by the
 * slope × the distance travelled in the render delay, and stepped at every update — a swoop at 40 m/s over a dune visibly
 * (Loïc, 2026-10-27). Both sides sample one golden heightfield, so on open ground the deck term is 0 to the quantum and the
 * altitude is this client's own, smooth at the drawn position; on a deck it is that deck's height, which only the server knows.
 *
 * @param field The store's altitude field, or `null` (the mock): then {@link altitudeOf}'s guess.
 * @param slot The entity's slot.
 * @param at The DRAWN placement — interpolated at the render tick.
 * @param latestX The entity's east at the latest tick, where the server's `y` was sampled.
 * @param latestZ Its north there.
 * @param ground This client's terrain.
 */
export function groundedAltitude(
  field: FieldArray | null,
  slot: number,
  at: Placement,
  latestX: number,
  latestZ: number,
  ground: { heightAt(x: number, z: number): number },
): number {
  if (field === null) {
    return at.y + ground.heightAt(at.x, at.z);
  }

  const deck = field[slot] - ground.heightAt(latestX, latestZ);
  return ground.heightAt(at.x, at.z) + (deck > DECK_MIN_M ? deck : 0);
}

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
