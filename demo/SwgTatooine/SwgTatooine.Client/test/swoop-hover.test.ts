import { describe, expect, it } from 'vitest';

import { HOVER_M, MIN_CLEARANCE_M, REACH_M, SwoopHover, type GroundAt } from '../src/app/swoop-hover';

/**
 * The swoop floats on damped repulsors over the ground it feels under its length, instead of standing on the ground sample
 * under it every frame. Ridden along +X at a 60 Hz step.
 */

const DT = 1 / 60;

/** Rides the bike along +X over a ground, returning its height and the ground's at every frame. */
function ride(
  ground: GroundAt,
  speed: number,
  seconds: number,
  hover = new SwoopHover(),
): { y: number[]; g: number[] } {
  const y: number[] = [];
  const g: number[] = [];
  let x = 0;
  for (let t = 0; t < seconds; t += DT) {
    x += speed * DT;
    const floor = ground(x, 0);
    hover.step(DT, ground, floor, x, 0, 0, speed);
    y.push(hover.y);
    g.push(floor);
  }

  return { y, g };
}

/** The root mean square of a series' second difference: how hard it is shaken, frame to frame. */
function jolt(series: number[]): number {
  let sum = 0;
  for (let i = 2; i < series.length; i++) {
    const d = series[i] - 2 * series[i - 1] + series[i - 2];
    sum += d * d;
  }

  return Math.sqrt(sum / (series.length - 2));
}

describe('the float', () => {
  it('settles at its hover height over flat ground', () => {
    const { y } = ride(() => 10, 0, 3);
    expect(y[y.length - 1]).toBeCloseTo(10 + HOVER_M, 3);
  });

  /**
   * The defect: standing on the ground sample shook the bike with every ridge of the height field. Over a ground rippled
   * 0.25 m on a 4 m wavelength at 40 m/s — a 10 Hz shake under the old rule — the float must be at least ten times calmer.
   */
  it('rides over small bumps instead of following them', () => {
    const rippled: GroundAt = (x) => 5 + 0.25 * Math.sin((2 * Math.PI * x) / 4);
    const { y, g } = ride(rippled, 40, 4);
    const settled = y.slice(60);
    expect(jolt(settled)).toBeLessThan(jolt(g.slice(60)) / 10);
    for (let i = 60; i < y.length; i++) {
      expect(y[i] - g[i]).toBeGreaterThanOrEqual(MIN_CLEARANCE_M - 1e-9);
    }
  });

  /** A rise is felt coming, and the floor holds the skids out of the ground whatever the spring lags. */
  it('never goes under the ground, even up a wall of terrain at speed', () => {
    const cliff: GroundAt = (x) => (x < 30 ? 0 : 3);
    const { y, g } = ride(cliff, 40, 3);
    for (let i = 0; i < y.length; i++) {
      expect(y[i] - g[i]).toBeGreaterThanOrEqual(MIN_CLEARANCE_M - 1e-9);
    }

    expect(y[y.length - 1]).toBeCloseTo(3 + HOVER_M, 1);
  });

  /** Off a ledge it floats for a moment — past the repulsors' reach, under a floaty gravity — and settles again. */
  it('floats off a crest and settles', () => {
    const ledge: GroundAt = (x) => (x < 20 ? 4 : 0);
    const { y, g } = ride(ledge, 30, 3);
    const airborne = y.filter((h, i) => h - g[i] > REACH_M).length;
    expect(airborne * DT).toBeGreaterThan(0.2);
    expect(y[y.length - 1]).toBeCloseTo(HOVER_M, 1);
  });
});

/**
 * Found riding the desert at 160 m/s: damped against stillness, the bike could not fall as fast as a long downhill and
 * flew 7 m above it on average, 26 m at worst. Damped against the ground's own rate, it follows the slope.
 */
describe('a long slope', () => {
  it('is followed at the hover height, downhill at full boost', () => {
    const slope: GroundAt = (x) => -0.1 * x;
    const { y, g } = ride(slope, 160, 4);
    const tail = y.slice(120).map((h, i) => h - g[i + 120]);
    const mean = tail.reduce((s, v) => s + v, 0) / tail.length;
    expect(mean).toBeLessThan(1);
    expect(Math.max(...tail)).toBeLessThan(2);
  });

  it('is followed uphill too, clear of the ground', () => {
    const slope: GroundAt = (x) => 0.1 * x;
    const { y, g } = ride(slope, 160, 4);
    const tail = y.slice(120).map((h, i) => h - g[i + 120]);
    expect(Math.min(...tail)).toBeGreaterThanOrEqual(MIN_CLEARANCE_M - 1e-9);
    expect(tail.reduce((s, v) => s + v, 0) / tail.length).toBeLessThan(1);
  });
});

describe('the pitch', () => {
  it('noses up a rise and down a fall, within its bound', () => {
    const up = new SwoopHover();
    ride((x) => 0.3 * x, 20, 1.5, up);
    expect(up.pitch).toBeGreaterThan(0.1);
    expect(up.pitch).toBeLessThanOrEqual(0.4 + 1e-9);

    const down = new SwoopHover();
    ride((x) => -0.3 * x, 20, 1.5, down);
    expect(down.pitch).toBeLessThan(-0.1);
  });

  it('is level on the flat', () => {
    const flat = new SwoopHover();
    ride(() => 2, 30, 2, flat);
    expect(Math.abs(flat.pitch)).toBeLessThan(1e-3);
  });
});
