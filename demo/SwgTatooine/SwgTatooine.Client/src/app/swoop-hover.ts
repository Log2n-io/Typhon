/**
 * How high a swoop floats: repulsors pushing it off the ground like a damped spring, and gravity pulling it back — not a
 * fixed height stuck to every bump of the terrain.
 *
 * <b>The bike used to stand on the ground sample under it, every frame.</b> At 40 m/s, and 160 boosted, every ridge in the
 * height field became a vertical jolt in the bike, the rider and the camera that follows them (Loïc, 2026-10-24). A
 * hovering bike does not do that: its repulsors push harder the closer it gets to the ground and not at all past their
 * reach, so it rides over small bumps, lifts ahead of a rise, floats for a moment off a crest and settles back.
 *
 * - <b>Repulsors</b>: within {@link REACH_M} of the ground the push grows as {@link STIFFNESS_PER_S2} × (reach − height),
 *   damped by {@link DAMPING_PER_S}; it balances {@link GRAVITY} at {@link HOVER_M}. Past the reach a floaty gravity and a
 *   soft pull back ({@link SEEK_PER_S2}) act, so a crest is a short float and not a flight.
 * - <b>Damped against the GROUND's rise and fall, not against stillness</b>: the damping works on the bike's vertical speed
 *   relative to the ground's (eased over {@link GROUND_RATE_S}), so a long slope is followed at the hover height rather
 *   than lagged — measured at 160 m/s down a desert slope, damping against stillness left the bike 7 m up on average and
 *   26 m at worst.
 * - <b>The ground it feels is the footprint's, not a point</b>: the nose, the tail and the spot under it, averaged, so a bump
 *   is ridden rather than tracked; and a spot ahead that moves further out with speed ({@link LOOKAHEAD_S}) says how fast
 *   the ground is about to rise or fall, so a slope is anticipated.
 * - <b>A floor</b>: the skids never go below {@link MIN_CLEARANCE_M} of the ground under the body, whatever the spring
 *   has done — a wall of terrain at full speed lifts the bike rather than burying it.
 * - <b>Pitch</b> follows the slope under it and the path it flies, eased, so it noses up a rise and down off a crest.
 *
 * Drawing only. The server stands the body on the ground; nothing here reaches it, and other viewers see the rider at the
 * server's altitude. Pure: the ground is a function the caller supplies. Headings are radians from +X towards +Z.
 */

/** Height the repulsors hold the bike at on flat ground, metres. */
export const HOVER_M = 0.5;

/** The lowest the skids may go, metres above the ground under the body. */
export const MIN_CLEARANCE_M = 0.12;

/** A floaty gravity, metres per second squared: what pulls the bike back down off a crest. */
export const GRAVITY = 7;

/** The repulsors' stiffness, per second squared: about 1.5 Hz, a firm float. */
export const STIFFNESS_PER_S2 = 90;

/** The repulsors' damping, per second: enough to settle after one small overshoot. */
export const DAMPING_PER_S = 11;

/** How far above the ground the repulsors reach, metres; past it the bike is flying. Where the push balances gravity at {@link HOVER_M}. */
export const REACH_M = HOVER_M + GRAVITY / STIFFNESS_PER_S2;

/** Past the reach, the pull back per metre too high, per second squared: soft, so a crest still floats a moment. */
export const SEEK_PER_S2 = 6;

/** Past the reach, the damping against the ground's rate, per second: light, so the fall is mostly gravity's. */
export const AIR_DAMPING_PER_S = 3;

/** Time constant the ground's vertical rate is eased over, seconds: a slope is followed, a pebble is not. */
export const GROUND_RATE_S = 0.25;

/** Half the bike's length the ground is felt over, metres, fore and aft of the body. */
export const FOOT_M = 1.4;

/** How far ahead, in seconds of travel, the repulsors feel the ground coming. */
export const LOOKAHEAD_S = 0.15;

/** Time constant of the pitch following the ground and the path, seconds. */
export const PITCH_RESPONSE_S = 0.15;

/** The steepest the bike noses up or down, radians (23°). */
export const MAX_PITCH_RAD = 0.4;

/** The ground's height at a point of the planet. */
export type GroundAt = (x: number, z: number) => number;

export class SwoopHover {
  /** Where the bike's base is, metres: above the ground it stands over. */
  y = Number.NaN;

  /** Its vertical speed, metres per second. */
  vy = 0;

  /** Nose up, radians; negative is nose down. */
  pitch = 0;

  /** How fast the ground ahead is rising, eased, metres per second. */
  private groundRate = 0;

  /** Puts the bike at its hover height over the ground it was mounted on, at rest. */
  reset(groundY: number): void {
    this.y = groundY + HOVER_M;
    this.vy = 0;
    this.pitch = 0;
    this.groundRate = 0;
  }

  /**
   * Advances the float.
   *
   * @param dt Seconds.
   * @param ground The terrain's height at a point.
   * @param floorY The ground directly under the body, structures included — a deck, a ramp — which the bike may never go
   *   under; the footprint's samples are the terrain's and are raised to it.
   * @param x The body, east.
   * @param z The body, north.
   * @param yaw Where the nose points, radians.
   * @param speed Ground speed, metres per second.
   */
  step(dt: number, ground: GroundAt, floorY: number, x: number, z: number, yaw: number, speed: number): void {
    if (!Number.isFinite(floorY)) {
      return;
    }

    if (!Number.isFinite(this.y)) {
      this.reset(floorY);
    }

    if (!(dt > 0)) {
      return;
    }

    const fx = Math.cos(yaw);
    const fz = Math.sin(yaw);
    const ahead = FOOT_M + speed * LOOKAHEAD_S;
    const here = ground(x, z);
    const nose = ground(x + fx * FOOT_M, z + fz * FOOT_M);
    const tail = ground(x - fx * FOOT_M, z - fz * FOOT_M);
    const coming = ground(x + fx * ahead, z + fz * ahead);
    // What the repulsors push off: the ground under the bike's length. The spot ahead does not raise it — on a 10 % slope
    // at full boost that biased the float by a metre — it says how fast the ground is about to rise or fall instead.
    // Raised to the floor once, not sample by sample: clamped per sample, every spot down a slope read as the floor and the
    // bike never learnt the ground was falling. On a deck the floor wins, as it must. The rate is the TERRAIN's slope ahead,
    // which a deck the body happens to stand on says nothing about.
    const felt = Math.max(floorY, (floorY + nose + tail) / 3);
    const upcoming = speed > 1 ? ((coming - here) / ahead) * speed : 0;
    this.groundRate += (upcoming - this.groundRate) * (1 - Math.exp(-dt / GROUND_RATE_S));

    // Repulsors inside their reach, gravity everywhere, and the damping against the ground's own rate. Integrated
    // semi-implicitly, which stays stable at a long frame.
    const height = this.y - felt;
    const relative = this.vy - this.groundRate;
    let a = -GRAVITY;
    if (height < REACH_M) {
      a += STIFFNESS_PER_S2 * (REACH_M - height) - DAMPING_PER_S * relative;
    } else {
      a -= SEEK_PER_S2 * (height - REACH_M) + AIR_DAMPING_PER_S * relative;
    }

    this.vy += a * dt;
    this.y += this.vy * dt;

    // The floor: lifted, never buried, and moving up with the ground that lifted it rather than bouncing off.
    const lowest = floorY + MIN_CLEARANCE_M;
    if (this.y < lowest) {
      this.y = lowest;
      this.vy = Math.max(this.vy, this.groundRate);
    }

    // Nose along the slope under it and the path it is flying, eased and bounded.
    const slope = Math.atan2(nose - tail, 2 * FOOT_M);
    const path = Math.atan2(this.vy, Math.max(speed, 4));
    const target = clamp(0.6 * slope + 0.4 * path, -MAX_PITCH_RAD, MAX_PITCH_RAD);
    this.pitch += (target - this.pitch) * (1 - Math.exp(-dt / PITCH_RESPONSE_S));
  }
}

function clamp(v: number, lo: number, hi: number): number {
  return v < lo ? lo : v > hi ? hi : v;
}
