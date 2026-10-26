import { describe, expect, it } from 'vitest';
import {
  ARRIVAL_S,
  arrivalPose,
  flightAt,
  HOVER_HEIGHT_M,
  shuttleMoment,
  type ShuttleSchedule,
} from '../src/app/shuttle-flight';
import { canUseAt, ticketTerminalOf } from '../src/app/ticket-terminal';
import type { Descriptor } from '../src/assets/descriptor';

/** The server's defaults: a landing every 120 s for 30 s, at 50 Hz, seven ports. */
const SCHEDULE: ShuttleSchedule = { intervalTicks: 6000, windowTicks: 1500, ports: 7, boardM: 30 };
const HZ = 50;

describe('shuttleMoment', () => {
  it('is down exactly when the server boards: (tick + city × interval / ports) % interval < window', () => {
    // City 0 has no offset: down on ticks 0..1499 of every interval.
    expect(shuttleMoment(0, 0, SCHEDULE, HZ).landed).toBe(true);
    expect(shuttleMoment(1499.5, 0, SCHEDULE, HZ).landed).toBe(true);
    expect(shuttleMoment(1500, 0, SCHEDULE, HZ).landed).toBe(false);
    expect(shuttleMoment(6000, 0, SCHEDULE, HZ).landed).toBe(true);
  });

  it("offsets each city's port by its share of the interval, as the server staggers them", () => {
    const offset = Math.floor((3 * 6000) / 7);
    expect(shuttleMoment(6000 - offset, 3, SCHEDULE, HZ).landed).toBe(true);
    expect(shuttleMoment(6000 - offset - 1, 3, SCHEDULE, HZ).landed).toBe(false);
  });

  it('counts the seconds to the next landing and since the last departure', () => {
    const before = shuttleMoment(6000 - 100, 0, SCHEDULE, HZ);
    expect(before.untilLandingS).toBeCloseTo(2, 9);
    const after = shuttleMoment(1500 + 50, 0, SCHEDULE, HZ);
    expect(after.sinceDepartureS).toBeCloseTo(1, 9);
  });
});

describe('the flight', () => {
  it('ends on the pad, and hovers before it touches down', () => {
    const landed = arrivalPose(ARRIVAL_S);
    expect([landed.x, landed.y, landed.z]).toEqual([0, 0, 0]);
    expect(arrivalPose(9).y).toBeCloseTo(HOVER_HEIGHT_M, 6);
    expect(arrivalPose(0).y).toBeGreaterThan(50);
  });

  it('is drawn on the pad while down, coming in for 14 s before, leaving for 14 s after, and nowhere otherwise', () => {
    expect(flightAt(shuttleMoment(10, 0, SCHEDULE, HZ))).toMatchObject({ x: 0, y: 0, z: 0 });
    expect(flightAt(shuttleMoment(6000 - 5 * HZ, 0, SCHEDULE, HZ))?.y).toBeGreaterThan(0);
    expect(flightAt(shuttleMoment(1500 + 5 * HZ, 0, SCHEDULE, HZ))?.y).toBeGreaterThan(0);
    expect(flightAt(shuttleMoment(3000, 0, SCHEDULE, HZ))).toBeNull();
  });
});

describe('ticketTerminalOf', () => {
  /** `tat_shuttleport`'s ticket terminal child and its stand point, as published (catalogue 4). */
  const PORT = {
    id: 'tat_shuttleport',
    portals: [],
    spawns: [],
    interactions: [],
    children: [{ asset: 'ticket_terminal', pos: [2.1434, 0.3, 20.4714], rot: [0, 0, 0, 1] }],
  } as unknown as Descriptor;
  const TERMINAL = {
    id: 'ticket_terminal',
    interactions: [{ kind: 'shuttle_ticket', pos: [0, 0, 0.9], rot: [0, 0, 0, 1], forward: '-Z', radius: 1.5 }],
  } as unknown as Descriptor;

  it('stands the user in front of the terminal, through the X mirror like every published point', () => {
    const use = ticketTerminalOf(PORT, new Map([['ticket_terminal', TERMINAL]]), 1000, 0, 2000, 0);
    expect(use).not.toBeNull();
    expect(use!.x).toBeCloseTo(1000 - 2.1434, 4);
    expect(use!.z).toBeCloseTo(2000 + 20.4714 + 0.9, 4);
    expect(use!.radiusM).toBeCloseTo(2, 9);
  });

  it('without reach_m, is usable only on the stand disc', () => {
    const use = ticketTerminalOf(PORT, new Map([['ticket_terminal', TERMINAL]]), 1000, 0, 2000, 0)!;
    expect(use.reachX).toBeCloseTo(use.x, 9);
    expect(use.reachZ).toBeCloseTo(use.z, 9);
    expect(use.reachM).toBeCloseTo(use.radiusM, 9);
    expect(canUseAt(use, use.x, use.z - use.radiusM - 0.1)).toBe(false);
  });

  it('with reach_m, is usable from any side of the terminal, about its origin, with no slack on top', () => {
    const reaching = {
      ...TERMINAL,
      interactions: [
        { kind: 'shuttle_ticket', pos: [0, 0, 0.9], rot: [0, 0, 0, 1], forward: '-Z', radius: 1.5, reach_m: 3 },
      ],
    } as unknown as Descriptor;
    const use = ticketTerminalOf(PORT, new Map([['ticket_terminal', reaching]]), 1000, 0, 2000, 0)!;
    const ox = 1000 - 2.1434;
    const oz = 2000 + 20.4714;
    expect(use.x).toBeCloseTo(ox, 4); // the stand point is unchanged: bots still walk there
    expect(use.z).toBeCloseTo(oz + 0.9, 4);
    expect(use.reachX).toBeCloseTo(ox, 4);
    expect(use.reachZ).toBeCloseTo(oz, 4);
    expect(use.reachM).toBe(3);
    // Behind the terminal, 2.9 m from its origin: out of the stand disc, inside the reach.
    expect(canUseAt(use, ox, oz - 2.9)).toBe(true);
    expect(canUseAt(use, ox + 2.9, oz)).toBe(true);
    expect(canUseAt(use, ox, oz - 3.1)).toBe(false);
  });

  it('is null for a port that publishes no ticket terminal', () => {
    expect(ticketTerminalOf(PORT, new Map(), 0, 0, 0, 0)).toBeNull();
  });
});
