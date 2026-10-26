/**
 * The shuttle's flight, from the schedule the server publishes: when each port's shuttle is down, and where it is in the
 * sky on its way in and out.
 *
 * <b>Ported from the physics testbench</b> (`tools/testbench/src/world/shuttle-flight.ts`): a curved, decelerating
 * approach from high behind the pad to a hover above it, then a vertical touchdown, and the departure as the arrival
 * played backwards. Closed-form in time, so a client that arrives in the middle of a flight draws it where it is, and
 * every client draws the same shuttle in the same place.
 *
 * <b>The schedule is the server's, not this client's</b> (`/typhon/demo.json` → `shuttle`): city `c`'s port is down
 * while `(tick + c × interval / ports) % interval < window`, the same expression the server boards by. The flight is
 * drawn around that window — touching down as it opens, lifting off as it closes — so a passenger who sees the shuttle
 * land is boarded on that tick (`SimBridge.ShuttleTick` boards a session's passengers the moment the shuttle is down).
 */

/** When each port's shuttle is down: the server's own figures, in its ticks. */
export interface ShuttleSchedule {
  readonly intervalTicks: number;
  readonly windowTicks: number;
  readonly ports: number;
  /** How close to a port's centre a player must stand to board there, metres. */
  readonly boardM: number;
}

/** Seconds of the approach, the curved descent to the hover. */
export const APPROACH_S = 9;

/** Seconds of the touchdown, the vertical descent from the hover onto the pad. */
export const TOUCHDOWN_S = 5;

/** Seconds from the edge of view to the pad. */
export const ARRIVAL_S = APPROACH_S + TOUCHDOWN_S;

/** Height of the hover above the pad, metres. */
export const HOVER_HEIGHT_M = 14;

type Vec3 = readonly [number, number, number];

/** Approach control points in the pad frame: x to the pad's right, y up, z the landed nose direction. */
const P0: Vec3 = [70, 95, -170];
const P1: Vec3 = [45, 55, -80];
const P2: Vec3 = [0, 24, -18];
const P3: Vec3 = [0, HOVER_HEIGHT_M, 0];

/** Where the shuttle is relative to the pad: a position in the pad frame, a yaw and a nose-up pitch. */
export interface FlightPose {
  readonly x: number;
  readonly y: number;
  readonly z: number;
  /** Turn about +Y relative to the pad, the renderer's convention (+Z towards +X). */
  readonly yaw: number;
  /** Nose up, radians; the renderer's pitch is its negative. */
  readonly noseUp: number;
}

const smoothstep = (u: number): number => u * u * (3 - 2 * u);
const clamp01 = (u: number): number => Math.min(1, Math.max(0, u));

function bezier(u: number, axis: 0 | 1 | 2): number {
  const a = (1 - u) ** 3;
  const b = 3 * (1 - u) ** 2 * u;
  const c = 3 * (1 - u) * u * u;
  const d = u ** 3;
  return a * P0[axis] + b * P1[axis] + c * P2[axis] + d * P3[axis];
}

function bezierTangent(u: number, axis: 0 | 1 | 2): number {
  const a = 3 * (1 - u) ** 2;
  const b = 6 * (1 - u) * u;
  const c = 3 * u * u;
  return a * (P1[axis] - P0[axis]) + b * (P2[axis] - P1[axis]) + c * (P3[axis] - P2[axis]);
}

/** A pose or a moment written in place: the frame loop keeps one of each rather than allocating them sixty times a second. */
export type Writable<T> = { -readonly [K in keyof T]: T[K] };

/**
 * The pose `t` seconds into the arrival, clamped to it; at {@link ARRIVAL_S} exactly on the pad.
 *
 * @param out Written and returned when given; a new pose otherwise.
 */
export function arrivalPose(
  t: number,
  out: Writable<FlightPose> = { x: 0, y: 0, z: 0, yaw: 0, noseUp: 0 },
): FlightPose {
  if (t < APPROACH_S) {
    // Ease out: fast from the sky, slowing into the hover.
    const u = 1 - (1 - clamp01(t / APPROACH_S)) ** 2;
    const along = Math.atan2(bezierTangent(u, 0), bezierTangent(u, 2));
    out.x = bezier(u, 0);
    out.y = bezier(u, 1);
    out.z = bezier(u, 2);
    // Along the path, settling to the landed heading over the last third; nose up while braking, level in the hover.
    out.yaw = along * (1 - smoothstep(clamp01((u - 0.66) / 0.34)));
    out.noseUp = 0.14 * Math.sin(Math.PI * u);
    return out;
  }

  const u = smoothstep(clamp01((t - APPROACH_S) / TOUCHDOWN_S));
  out.x = 0;
  out.y = HOVER_HEIGHT_M * (1 - u);
  out.z = 0;
  out.yaw = 0;
  out.noseUp = 0;
  return out;
}

/** Where a port's shuttle is in its cycle at a tick. */
export interface ShuttleMoment {
  /** Whether it is down on the pad, which is when the server boards. */
  readonly landed: boolean;
  /** Seconds until it touches down — 0 while it is down. */
  readonly untilLandingS: number;
  /** Seconds since it lifted off, or `Infinity` while it is down. */
  readonly sinceDepartureS: number;
}

/**
 * Where city `city`'s shuttle is in its cycle at a (fractional) server tick.
 *
 * @param tick The render tick, fractional.
 * @param city The port's city index.
 * @param schedule The server's schedule.
 * @param tickHz The server's tick rate.
 */
export function shuttleMoment(
  tick: number,
  city: number,
  schedule: ShuttleSchedule,
  tickHz: number,
  out: Writable<ShuttleMoment> = { landed: false, untilLandingS: 0, sinceDepartureS: 0 },
): ShuttleMoment {
  const interval = schedule.intervalTicks;
  const offset = Math.floor((city * interval) / Math.max(1, schedule.ports));
  const phase = (((tick + offset) % interval) + interval) % interval;
  out.landed = phase < schedule.windowTicks;
  out.untilLandingS = out.landed ? 0 : (interval - phase) / tickHz;
  out.sinceDepartureS = out.landed ? Infinity : (phase - schedule.windowTicks) / tickHz;
  return out;
}

/**
 * The shuttle's pose relative to its pad at a moment of its cycle, or `null` while it is nowhere to be seen — further
 * out than the arrival's first point, either coming or going.
 */
export function flightAt(moment: ShuttleMoment, out?: Writable<FlightPose>): FlightPose | null {
  if (moment.landed) {
    return arrivalPose(ARRIVAL_S, out);
  }

  if (moment.untilLandingS <= ARRIVAL_S) {
    return arrivalPose(ARRIVAL_S - moment.untilLandingS, out);
  }

  if (moment.sinceDepartureS <= ARRIVAL_S) {
    // The departure: the arrival played backwards.
    return arrivalPose(ARRIVAL_S - moment.sinceDepartureS, out);
  }

  return null;
}
