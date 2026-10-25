import { describe, expect, it } from 'vitest';
import { DoorWalker, RIDDEN_REACH_M } from '../src/app/door-walk';

import {
  ACCEL_S,
  BRAKE_S,
  AUTOPILOT_DEADBAND_RAD,
  AUTOPILOT_THROTTLE_RAD,
  autopilotInput,
  driveInputOf,
  handsOn,
  MAX_ROLL_RAD,
  SwoopDrive,
  TURN_RATE_RAD_S,
  type DriveInput,
} from '../src/app/swoop-drive';

/**
 * The swoop handles like a hovering ship: W throttles, S brakes, A/D steer a turn rate that builds, the motion follows the
 * nose by grip, and it banks into a turn. Driven at a 60 Hz step against a granted 40 m/s.
 */

const MAX = 40;
const DT = 1 / 60;
const NONE: DriveInput = { throttle: false, brake: false, steer: 0 };
const GAS: DriveInput = { throttle: true, brake: false, steer: 0 };

function run(drive: SwoopDrive, input: DriveInput, seconds: number, max = MAX): void {
  for (let t = 0; t < seconds; t += DT) {
    drive.step(DT, input, max);
  }
}

function cruising(): SwoopDrive {
  const drive = new SwoopDrive();
  drive.reset(0);
  run(drive, GAS, ACCEL_S + 0.5);
  return drive;
}

describe('the throttle and the brake', () => {
  it('builds speed to what the server grants, and no further', () => {
    const drive = new SwoopDrive();
    drive.reset(0);
    run(drive, GAS, ACCEL_S / 2);
    expect(drive.speed).toBeGreaterThan(MAX * 0.3);
    expect(drive.speed).toBeLessThan(MAX * 0.7);

    run(drive, GAS, ACCEL_S);
    expect(drive.speed).toBeCloseTo(MAX, 6);
  });

  /** A boosted swoop handles the same, faster: every rate is a share of the granted speed. */
  it('scales with the granted speed', () => {
    const drive = new SwoopDrive();
    drive.reset(0);
    run(drive, GAS, ACCEL_S + 0.5, MAX * 4);
    expect(drive.speed).toBeCloseTo(MAX * 4, 6);
  });

  it('brakes to a stop, and does not go backwards', () => {
    const drive = cruising();
    run(drive, { throttle: false, brake: true, steer: 0 }, BRAKE_S + 0.1);
    expect(drive.speed).toBe(0);
    run(drive, { throttle: false, brake: true, steer: 0 }, 1);
    expect(drive.speed).toBe(0);
  });

  /** Letting go coasts — the hover drags slowly — and parks rather than creeping forever. */
  it('coasts down when let go, and parks', () => {
    const drive = cruising();
    run(drive, NONE, 1);
    expect(drive.speed).toBeLessThan(MAX);
    expect(drive.speed).toBeGreaterThan(MAX * 0.6);

    run(drive, NONE, 30);
    expect(drive.speed).toBe(0);
  });
});

describe('steering', () => {
  /** Right is towards a falling heading in this frame (`player-intent.ts`'s basis, which the browser was checked against). */
  it('turns D right and A left', () => {
    const right = cruising();
    run(right, { throttle: true, brake: false, steer: 1 }, 0.5);
    expect(right.yaw).toBeLessThan(0);

    const left = cruising();
    run(left, { throttle: true, brake: false, steer: -1 }, 0.5);
    expect(left.yaw).toBeGreaterThan(0);
  });

  /** A curve, not a corner: the turn rate builds over a fraction of a second rather than jumping to full lock. */
  it('builds its turn rate', () => {
    const drive = cruising();
    drive.step(DT, { throttle: true, brake: false, steer: 1 }, MAX);
    expect(Math.abs(drive.yawRate)).toBeLessThan(TURN_RATE_RAD_S * 0.2);

    run(drive, { throttle: true, brake: false, steer: 1 }, 2);
    expect(Math.abs(drive.yawRate)).toBeCloseTo(TURN_RATE_RAD_S, 2);
  });

  /** The motion lags the nose — the slide that makes it float — and catches up once the turn ends. */
  it('slides: the motion follows the nose by grip', () => {
    const drive = cruising();
    run(drive, { throttle: true, brake: false, steer: 1 }, 0.6);
    const slip = Math.abs(drive.heading - drive.yaw);
    expect(slip).toBeGreaterThan(0.05);

    run(drive, GAS, 3);
    expect(Math.abs(drive.heading - drive.yaw)).toBeLessThan(0.01);
  });

  it('pivots on the spot when parked, more slowly', () => {
    const drive = new SwoopDrive();
    drive.reset(0);
    run(drive, { throttle: false, brake: false, steer: -1 }, 1);
    expect(drive.yaw).toBeGreaterThan(0.3);
    expect(drive.speed).toBe(0);
  });
});

describe('the bank', () => {
  /** Into the turn, eased in, and back to level after it — never a snap. */
  it('leans into a turn over a fraction of a second, and levels after', () => {
    const drive = cruising();
    drive.step(DT, { throttle: true, brake: false, steer: 1 }, MAX);
    expect(Math.abs(drive.roll)).toBeLessThan(MAX_ROLL_RAD * 0.1);

    run(drive, { throttle: true, brake: false, steer: 1 }, 1);
    expect(drive.roll).toBeGreaterThan(MAX_ROLL_RAD * 0.8);
    expect(drive.roll).toBeLessThanOrEqual(MAX_ROLL_RAD + 1e-9);

    run(drive, GAS, 1.5);
    expect(Math.abs(drive.roll)).toBeLessThan(MAX_ROLL_RAD * 0.05);
  });

  it('leans the other way for the other turn, and barely when crawling', () => {
    const drive = cruising();
    run(drive, { throttle: true, brake: false, steer: -1 }, 1);
    expect(drive.roll).toBeLessThan(-MAX_ROLL_RAD * 0.8);

    const parked = new SwoopDrive();
    parked.reset(0);
    run(parked, { throttle: false, brake: false, steer: 1 }, 1);
    expect(Math.abs(parked.roll)).toBeLessThan(1e-9);
  });
});

describe('a wall', () => {
  it('takes the speed into it, and leaves the speed along it', () => {
    const drive = new SwoopDrive();
    drive.reset(0, 30, 10);
    // Pushed back along -X: a wall across the bike's path.
    drive.collide(-0.2, 0);
    expect(drive.vx).toBeCloseTo(0, 9);
    expect(drive.vz).toBe(10);

    // A push the motion is already leaving takes nothing.
    drive.collide(0, 0.2);
    expect(drive.vz).toBe(10);
  });
});

describe('the keys', () => {
  it('are W/S/A/D and the arrows', () => {
    expect(driveInputOf(new Set(['KeyW', 'KeyD']))).toEqual({ throttle: true, brake: false, steer: 1 });
    expect(driveInputOf(new Set(['ArrowDown', 'ArrowLeft']))).toEqual({ throttle: false, brake: true, steer: -1 });
    expect(driveInputOf(new Set(['KeyA', 'KeyD']))).toEqual({ throttle: false, brake: false, steer: 0 });
  });
});

/**
 * A waypoint clicked on the swoop out in the open: the walk rides the bike there through its own handling. In a city the
 * rider is put on foot instead (`ClientApp.walkTo`), so there are no corners to take here.
 */
describe('autopilotInput', () => {
  it('brakes once the walk is over, and holds a straight line inside its dead band', () => {
    expect(autopilotInput(0, null, 100, 10, 40)).toEqual({ throttle: false, brake: true, steer: 0 });
    expect(autopilotInput(1, 1 + AUTOPILOT_DEADBAND_RAD / 2, 500, 10, 40)).toEqual({
      throttle: true,
      brake: false,
      steer: 0,
    });
    expect(handsOn(autopilotInput(1, 1, 500, 10, 40))).toBe(true);
    expect(handsOn({ throttle: false, brake: false, steer: 0 })).toBe(false);
  });

  it('turns before it throttles, and slows to turn', () => {
    expect(autopilotInput(0, Math.PI / 2, 500, 0, 40).throttle).toBe(false);
    expect(autopilotInput(0, AUTOPILOT_THROTTLE_RAD / 2, 500, 0, 40).throttle).toBe(true);
    expect(autopilotInput(0, Math.PI / 2, 500, 30, 40).brake, 'too fast to come round').toBe(true);
  });

  it('is no faster than it can stop by the end, and creeps onto one under a metre away', () => {
    expect(autopilotInput(0, 0, 5, 30, 40).brake).toBe(true);
    expect(autopilotInput(0, 0, 200, 30, 40)).toEqual({ throttle: true, brake: false, steer: 0 });
    expect(autopilotInput(0, 0, 0.5, 0, 40).throttle).toBe(true);
  });

  it('brings the bike round onto the way, either side, and up to speed along it', () => {
    for (const target of [Math.PI / 2, -Math.PI / 2, 3]) {
      const drive = new SwoopDrive();
      drive.reset(0);
      for (let t = 0; t < 8; t += 1 / 60) {
        drive.step(1 / 60, autopilotInput(drive.yaw, target, 1000, drive.speed, 40), 40);
      }

      const off = Math.atan2(Math.sin(target - drive.yaw), Math.cos(target - drive.yaw));
      expect(Math.abs(off), `target ${target}`).toBeLessThan(0.1);
      expect(drive.speed, `target ${target}`).toBeGreaterThan(20);
    }
  });

  it('rides a walk across open ground to its end, behind it to start with, and stops there', () => {
    const walker = new DoorWalker();
    walker.reachM = RIDDEN_REACH_M;
    walker.start([[-300, -400]], 0, 0, 0, true);
    const drive = new SwoopDrive();
    drive.reset(Math.PI / 2);
    let x = 0;
    let z = 0;
    let top = 0;
    let t = 0;
    for (; t < 60 && walker.active; t += 1 / 60) {
      const walk = walker.intent(x, z, 0, t * 1000);
      const end = walker.end;
      const hands = autopilotInput(
        drive.yaw,
        walk !== null && walk.speedClass !== 0 ? walk.heading : null,
        end === null ? 0 : Math.hypot(end[0] - x, end[1] - z),
        drive.speed,
        40,
      );
      drive.step(1 / 60, hands, 40);
      x += drive.vx / 60;
      z += drive.vz / 60;
      top = Math.max(top, drive.speed);
    }

    expect(walker.active, 'the walk gave up or never ended').toBe(false);
    expect(Math.hypot(x + 300, z + 400), 'ended away from its end').toBeLessThan(RIDDEN_REACH_M + 1);
    expect(top, 'never got up to speed').toBeGreaterThan(30);
    expect(t, 'took the long way').toBeLessThan(25);
  });
});
