/**
 * How a swoop handles: a hovering bike driven like a small ship, not a body walking in the direction of the keys.
 *
 * <b>W is the throttle, S the brake, A and D steer</b> (Loïc, 2026-10-24). On foot the keys name a direction in the
 * camera's frame; on the swoop they name what the rider's hands do, and the bike answers with inertia:
 *
 * - <b>Steering is a turn RATE that builds</b> ({@link YAW_RESPONSE_S}), so a tap is a gentle curve and a held key a
 *   tightening one — the path is a curve, never a corner.
 * - <b>The velocity follows the nose by grip</b> ({@link GRIP_PER_S}), not instantly: in a turn the bike slides out a
 *   little before it carries on where it points, which is what makes it float rather than run on rails.
 * - <b>Throttle builds speed, letting go coasts, the brake stops</b> ({@link ACCEL_S}, {@link COAST_S}, {@link BRAKE_S}),
 *   all as shares of the speed the server grants, so a boosted swoop handles the same way faster.
 * - <b>It banks into a turn</b> ({@link MAX_ROLL_RAD} at full lock, eased over {@link ROLL_RESPONSE_S}) and bobs gently
 *   on its repulsors — drawing only; nothing about the lean reaches the server.
 *
 * <b>What the server sees is the motion, never the nose.</b> The drive's velocity becomes a `MoveDir` heading and a
 * throttle — a share of the granted swoop speed, which can only ever slow the swoop down — and the server integrates that
 * as it integrates every intent. So the authority stays the server's and the curve, the drift and the brake are what the
 * intents trace.
 *
 * Pure: no clock, no keys, no scene. Headings are radians from +X towards +Z, the server's frame.
 */

/** Seconds from standing to the granted speed with the throttle held. */
export const ACCEL_S = 2;

/** Seconds from the granted speed to a stop with the brake held. */
export const BRAKE_S = 0.8;

/** Time constant of the coast with neither held, seconds: the hover drags, slowly. */
export const COAST_S = 3;

/** Full lock's turn rate, radians per second: a 22 m circle at the unboosted 40 m/s. */
export const TURN_RATE_RAD_S = 1.8;

/** Share of {@link TURN_RATE_RAD_S} the bike pivots at on the spot, where a hover can turn without moving. */
export const PIVOT_SHARE = 0.6;

/** Time constant of the turn rate answering the keys, seconds — what makes a steer a curve. */
export const YAW_RESPONSE_S = 0.18;

/** How fast the velocity swings onto the nose, per second: lower drifts more. */
export const GRIP_PER_S = 2.5;

/** Bank at full lock and speed, radians (22°). */
export const MAX_ROLL_RAD = 0.38;

/** Time constant of the bank following the turn, seconds: a short lean in and out, never a snap. */
export const ROLL_RESPONSE_S = 0.15;

/** The hover's bob: height, metres, and frequency, hertz. Drawing only. */
export const BOB_M = 0.04;
export const BOB_HZ = 0.6;

/** Below this share of the granted speed with no throttle, the bike is parked rather than creeping. */
export const PARKED_SHARE = 0.01;

/** What the rider's hands are doing this frame. */
export interface DriveInput {
  readonly throttle: boolean;
  readonly brake: boolean;
  /** -1 left, +1 right, 0 straight. */
  readonly steer: number;
}

/** The keys a rider steers with: W/↑ throttle, S/↓ brake, A/← and D/→ steer. */
export function driveInputOf(keys: ReadonlySet<string>): DriveInput {
  const throttle = keys.has('KeyW') || keys.has('ArrowUp');
  const brake = keys.has('KeyS') || keys.has('ArrowDown');
  const left = keys.has('KeyA') || keys.has('ArrowLeft');
  const right = keys.has('KeyD') || keys.has('ArrowRight');
  return { throttle, brake, steer: (right ? 1 : 0) - (left ? 1 : 0) };
}

export class SwoopDrive {
  /** Where the nose points, radians from +X towards +Z. */
  yaw = 0;

  /** The velocity, metres per second, in the ground plane. */
  vx = 0;
  vz = 0;

  /** The turn rate, radians per second; positive turns from +X towards +Z, which is LEFT seen from above with +Z north. */
  yawRate = 0;

  /** The bank, radians; positive rolls the right side down, into a right turn. */
  roll = 0;

  /** Seconds driven, for the bob. */
  private t = 0;

  /** Puts the bike under a rider who has just mounted, facing where the body faced and moving as it moved. */
  reset(yaw: number, vx = 0, vz = 0): void {
    this.yaw = yaw;
    this.vx = vx;
    this.vz = vz;
    this.yawRate = 0;
    this.roll = 0;
  }

  /** Ground speed, metres per second. */
  get speed(): number {
    return Math.hypot(this.vx, this.vz);
  }

  /** Where the bike is GOING, radians — the heading the server is sent, which in a slide is not where it points. */
  get heading(): number {
    return Math.atan2(this.vz, this.vx);
  }

  /** The hover's height offset this instant, metres. */
  get bob(): number {
    return BOB_M * Math.sin(2 * Math.PI * BOB_HZ * this.t);
  }

  /**
   * Advances the bike.
   *
   * @param dt Seconds.
   * @param input The rider's hands.
   * @param maxMps What the server grants a swoop at the current boost; every rate here is a share of it.
   */
  step(dt: number, input: DriveInput, maxMps: number): void {
    if (!(dt > 0) || !(maxMps > 0)) {
      return;
    }

    this.t += dt;
    const speed = this.speed;
    const share = Math.min(1, speed / maxMps);

    // The turn rate eases towards what the hands ask. Right is towards -heading (see `player-intent.ts`'s basis), so +1
    // on the stick is a negative rate. A parked hover still pivots, more slowly.
    const lock = TURN_RATE_RAD_S * (PIVOT_SHARE + (1 - PIVOT_SHARE) * Math.min(1, share * 4));
    this.yawRate += (-input.steer * lock - this.yawRate) * (1 - Math.exp(-dt / YAW_RESPONSE_S));
    this.yaw = wrap(this.yaw + this.yawRate * dt);

    // The velocity swings onto the nose by grip, keeping its length: the slide out of a turn.
    const nx = Math.cos(this.yaw);
    const nz = Math.sin(this.yaw);
    const grip = 1 - Math.exp(-GRIP_PER_S * dt);
    this.vx += (nx * speed - this.vx) * grip;
    this.vz += (nz * speed - this.vz) * grip;

    // Then the speed: throttle pushes along the nose, the brake and the coast take away along the motion.
    if (input.throttle && !input.brake) {
      const a = (maxMps / ACCEL_S) * dt;
      this.vx += nx * a;
      this.vz += nz * a;
    }

    const now = this.speed;
    let next = now;
    if (input.brake) {
      next = Math.max(0, now - (maxMps / BRAKE_S) * dt);
    } else if (!input.throttle) {
      next = now * Math.exp(-dt / COAST_S);
      if (next < maxMps * PARKED_SHARE) {
        next = 0;
      }
    }

    next = Math.min(next, maxMps);
    const scale = now > 0 ? next / now : 0;
    this.vx *= scale;
    this.vz *= scale;

    // Bank into the turn, more the faster it is taken; eased in and out.
    const bank = (-this.yawRate / TURN_RATE_RAD_S) * MAX_ROLL_RAD * Math.min(1, share * 2);
    this.roll += (bank - this.roll) * (1 - Math.exp(-dt / ROLL_RESPONSE_S));
  }

  /**
   * Takes out the part of the velocity that drives into a wall: what collision pushed the body back along.
   *
   * @param pushX The push collision applied this frame, east.
   * @param pushZ The push, north.
   *
   * @remarks
   * A bike that kept its speed against a wall would drive the prediction into it every frame and be pushed back every
   * frame, grinding at full throttle; with the component removed it slides along the wall and stops against one head-on.
   */
  collide(pushX: number, pushZ: number): void {
    const len = Math.hypot(pushX, pushZ);
    if (!(len > 1e-6)) {
      return;
    }

    const ux = pushX / len;
    const uz = pushZ / len;
    const into = this.vx * ux + this.vz * uz;
    if (into < 0) {
      this.vx -= into * ux;
      this.vz -= into * uz;
    }
  }
}

function wrap(radians: number): number {
  return Math.atan2(Math.sin(radians), Math.cos(radians));
}
