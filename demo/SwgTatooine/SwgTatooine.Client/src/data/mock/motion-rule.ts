import { POSITION_QUANTUM_M, VELOCITY_QUANTUM_M } from '../swg-schema';
import { PLANET_HALF_EXTENT_M } from '../world-data';

/**
 * The engine's motion rule (`claude/design/Subscriptions/V2/02-execution.md` § 4), reproduced so the client receives the
 * segments the real server would send — not idealised ones:
 *
 * - positions quantized to 24 bits over the planet, velocities to 1/16 of that per tick;
 * - a new segment when extrapolation drifts past the tolerance, on a teleport (`epoch++`), or as a heartbeat after
 *   `MaxAge` for a moving entity;
 * - the new velocity refitted over the current straight run, which restarts on a teleport, on an error-triggered refit,
 *   or when a step departs from the run's mean velocity by more than max(2 mm/tick, 10 %);
 * - an entity watched now but not last tick starts with `v = 0`: its velocity is measured from its second watched tick.
 */
export interface MotionRuleOptions {
  readonly toleranceM: number;
  /** Largest legitimate speed; a faster step is a teleport. */
  readonly teleportSpeedMps: number;
  readonly tickPeriodMs: number;
  readonly maxAgeTicks: number;
}

export const DEFAULT_MOTION_RULE: MotionRuleOptions = {
  toleranceM: 0.05,
  // SWG declares 1.5 × the 12 m/s mount (`03-server.md` § 5).
  teleportSpeedMps: 18,
  tickPeriodMs: 100,
  maxAgeTicks: 50,
};

export function quantizePosition(v: number): number {
  return Math.round((v + PLANET_HALF_EXTENT_M) / POSITION_QUANTUM_M) * POSITION_QUANTUM_M - PLANET_HALF_EXTENT_M;
}

export function quantizeVelocity(v: number): number {
  return Math.round(v / VELOCITY_QUANTUM_M) * VELOCITY_QUANTUM_M;
}

/** Per-entity motion state for one archetype, valid while the entity is watched. */
export class MotionTracker {
  readonly p0x: Float64Array;
  readonly p0z: Float64Array;
  readonly vx: Float64Array;
  readonly vz: Float64Array;
  readonly t0: Uint32Array;
  readonly epoch: Uint8Array;
  private readonly prevX: Float64Array;
  private readonly prevZ: Float64Array;
  private readonly runX: Float64Array;
  private readonly runZ: Float64Array;
  private readonly runT: Uint32Array;
  private readonly options: MotionRuleOptions;
  private readonly teleportStepM: number;

  constructor(count: number, options: MotionRuleOptions = DEFAULT_MOTION_RULE) {
    this.options = options;
    this.teleportStepM = options.teleportSpeedMps * (options.tickPeriodMs / 1000);
    this.p0x = new Float64Array(count);
    this.p0z = new Float64Array(count);
    this.vx = new Float64Array(count);
    this.vz = new Float64Array(count);
    this.t0 = new Uint32Array(count);
    this.epoch = new Uint8Array(count);
    this.prevX = new Float64Array(count);
    this.prevZ = new Float64Array(count);
    this.runX = new Float64Array(count);
    this.runZ = new Float64Array(count);
    this.runT = new Uint32Array(count);
  }

  /** The entity became watched at `tick`: its segment starts here, stationary until measured. */
  begin(i: number, x: number, z: number, tick: number): void {
    const qx = quantizePosition(x);
    const qz = quantizePosition(z);
    this.p0x[i] = qx;
    this.p0z[i] = qz;
    this.vx[i] = 0;
    this.vz[i] = 0;
    this.t0[i] = tick;
    this.prevX[i] = qx;
    this.prevZ[i] = qz;
    this.runX[i] = qx;
    this.runZ[i] = qz;
    this.runT[i] = tick;
  }

  /** Measures tick `tick`; returns true when a new segment was produced (read it from the public arrays). */
  step(i: number, x: number, z: number, tick: number): boolean {
    const qx = quantizePosition(x);
    const qz = quantizePosition(z);
    const stepX = qx - this.prevX[i];
    const stepZ = qz - this.prevZ[i];
    this.prevX[i] = qx;
    this.prevZ[i] = qz;

    if (stepX * stepX + stepZ * stepZ > this.teleportStepM * this.teleportStepM) {
      this.p0x[i] = qx;
      this.p0z[i] = qz;
      this.vx[i] = 0;
      this.vz[i] = 0;
      this.t0[i] = tick;
      this.epoch[i] = (this.epoch[i] + 1) & 0xff;
      this.restartRun(i, qx, qz, tick);
      return true;
    }

    // Does this step depart from the run's mean velocity? Then the run (re)starts at the previous tick.
    const runTicks = tick - 1 - this.runT[i];
    if (runTicks > 0) {
      const meanX = (qx - stepX - this.runX[i]) / runTicks;
      const meanZ = (qz - stepZ - this.runZ[i]) / runTicks;
      const departX = stepX - meanX;
      const departZ = stepZ - meanZ;
      const meanLen = Math.sqrt(meanX * meanX + meanZ * meanZ);
      const limit = Math.max(0.002, 0.1 * meanLen);
      if (departX * departX + departZ * departZ > limit * limit) {
        this.restartRun(i, qx - stepX, qz - stepZ, tick - 1);
      }
    }

    const dt = tick - this.t0[i];
    const errX = this.p0x[i] + this.vx[i] * dt - qx;
    const errZ = this.p0z[i] + this.vz[i] * dt - qz;
    const tol = this.options.toleranceM;
    const drifted = errX * errX + errZ * errZ > tol * tol;
    const moving = this.vx[i] !== 0 || this.vz[i] !== 0;
    const heartbeat = moving && dt >= this.options.maxAgeTicks;
    if (!drifted && !heartbeat) {
      return false;
    }

    const span = tick - this.runT[i];
    const fitX = span > 0 ? (qx - this.runX[i]) / span : stepX;
    const fitZ = span > 0 ? (qz - this.runZ[i]) / span : stepZ;
    this.p0x[i] = qx;
    this.p0z[i] = qz;
    this.vx[i] = quantizeVelocity(fitX);
    this.vz[i] = quantizeVelocity(fitZ);
    this.t0[i] = tick;
    if (drifted) {
      this.restartRun(i, qx, qz, tick);
    }

    return true;
  }

  private restartRun(i: number, x: number, z: number, tick: number): void {
    this.runX[i] = x;
    this.runZ[i] = z;
    this.runT[i] = tick;
  }
}
