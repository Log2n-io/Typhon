import type { CrossingKind } from '../data/realm-view';
import type { TransitionState } from './realm-transition';

/**
 * Placing the camera after a door crossing, before the curtain lifts.
 *
 * The scene stays black from the realm switch until the camera is behind the body, facing the way the published spawn it
 * landed on faces (`ClientApp.placeArrival`). This is the decision of WHEN, kept apart from the camera so its timing can
 * be tested against the transition's own (`realm-transition.ts`):
 *
 * - <b>The spawn is matched against where the body FIRST stood in the new realm</b>, not where it stands now. A room
 *   entered for the first time needs its descriptor fetched, and a body still running covers the 0.6 m match radius in a
 *   tenth of a second: matched against the current position, it missed, and the screen stayed black for the whole wait
 *   and came back with the old yaw.
 * - <b>It stands down once the curtain is no longer holding</b> — the safety valve gave the screen back, or another
 *   crossing took over. It used to keep a fallback of its own, later than the valve, which faded in a second time from
 *   black over a scene already showing.
 */

/** How long an arrival waits for the spawn it landed on to be known before the camera is placed anyway, ms. */
export const ARRIVAL_WAIT_MS = 1500;

/**
 * What the camera and the body were doing as they went through: carried onto the far side rather than reset behind the
 * body, so a viewer who walked through backwards is still looking back at the door and the key they hold still moves them
 * the way it did.
 */
export interface DoorCarry {
  /** The renderer yaw (0 along +Z) of the direction the door was crossed in, in the realm left. */
  readonly doorYaw: number;
  /** The camera's yaw at the switch. */
  readonly cameraYaw: number;
  /** The body's heading at the switch, the server's convention (from +X towards +Z). */
  readonly heading: number;
}

/**
 * The camera's yaw and the body's heading on the far side of a door: both turned by the rotation that takes the door's
 * crossing direction onto the facing of the spawn the body landed on.
 *
 * Through a door walking forward, that is the spawn's facing with the camera behind the body — what a reset did. Walking
 * backward, the camera still faces the door it came through, and the key that was moving the body still moves it on.
 *
 * @param carry The state at the switch.
 * @param spawnYaw The renderer yaw of the spawn's facing, in the realm arrived in.
 */
export function carryThrough(carry: DoorCarry, spawnYaw: number): { yaw: number; heading: number } {
  // A yaw turns one way and a heading the other: the renderer's yaw is atan2(x, z), the server's heading atan2(z, x).
  const turn = spawnYaw - carry.doorYaw;
  return { yaw: carry.cameraYaw + turn, heading: carry.heading - turn };
}

/** A door crossing whose camera is not yet placed. */
export interface DoorArrival {
  /** The camera and body to carry through, or `null` when the door crossed is not known: the camera is then put behind the body. */
  readonly carry: DoorCarry | null;
  readonly kind: CrossingKind;
  readonly sinceMs: number;
  /** Whether a published spawn can face it: a building's door has them, a dungeon's none, and nothing is worth waiting for. */
  readonly expectsSpawn: boolean;
  /** Where the body first stood after the switch, once known. */
  firstX: number;
  firstZ: number;
  located: boolean;
}

/** A door crossing has just switched the realm. */
export function beginArrival(
  kind: CrossingKind,
  nowMs: number,
  expectsSpawn = true,
  carry: DoorCarry | null = null,
): DoorArrival {
  return { kind, sinceMs: nowMs, expectsSpawn, carry, firstX: 0, firstZ: 0, located: false };
}

/** What to do with an arrival this frame. */
export type ArrivalStep =
  | { readonly action: 'wait' }
  | { readonly action: 'abandon' }
  | { readonly action: 'place'; readonly facing: readonly [number, number] | null };

const WAIT: ArrivalStep = { action: 'wait' };
const ABANDON: ArrivalStep = { action: 'abandon' };

/**
 * One frame of an arrival.
 *
 * @param arrival The arrival; its first position is latched on the first call.
 * @param transition The curtain's state.
 * @param x Where the body stands now.
 * @param z The same, north.
 * @param nowMs The frame's clock.
 * @param facingAt The facing of the published spawn at a point, or `null` when none is known there (yet).
 * @returns `place` with the facing, or with `null` once the wait is over; `wait`; or `abandon` when the curtain is no
 * longer this arrival's to lift.
 */
export function stepArrival(
  arrival: DoorArrival,
  transition: TransitionState,
  x: number,
  z: number,
  nowMs: number,
  facingAt: (x: number, z: number) => readonly [number, number] | null,
): ArrivalStep {
  if (transition.phase !== 'hold') {
    return ABANDON;
  }

  if (!arrival.expectsSpawn) {
    return { action: 'place', facing: null };
  }

  if (!arrival.located) {
    arrival.firstX = x;
    arrival.firstZ = z;
    arrival.located = true;
  }

  const facing = facingAt(arrival.firstX, arrival.firstZ);
  if (facing === null && nowMs - arrival.sinceMs < ARRIVAL_WAIT_MS) {
    return WAIT;
  }

  return { action: 'place', facing };
}
