import { describe, expect, it } from 'vitest';
import { ARRIVAL_WAIT_MS, beginArrival, carryThrough, stepArrival } from '../src/app/door-arrival';
import { advance, askedFor, blackout, HOLD_LIMIT_MS, IDLE } from '../src/app/realm-transition';

const INTO_THE_ROOM = [0, -1] as const;

/** A spawn at the origin, matched within 0.6 m, known only from `knownFromMs` — a descriptor still being fetched. */
function spawnAtOrigin(clock: { now: number }, knownFromMs = 0) {
  return (x: number, z: number) => (clock.now >= knownFromMs && Math.hypot(x, z) <= 0.6 ? INTO_THE_ROOM : null);
}

describe('carryThrough', () => {
  // A door crossed towards +X (renderer yaw pi/2), landing on a spawn that faces -Z (yaw pi): a quarter turn, as when a
  // house turned on the planet opens onto a room authored facing another way.
  const DOOR = Math.PI / 2;
  const SPAWN = Math.PI;

  it('puts the camera behind a body that walked through forward, facing the spawn', () => {
    const c = carryThrough({ doorYaw: DOOR, cameraYaw: DOOR, heading: Math.PI / 2 - DOOR }, SPAWN);
    expect(c.yaw).toBeCloseTo(SPAWN, 9);
    expect(c.heading).toBeCloseTo(Math.PI / 2 - SPAWN, 9);
  });

  it('keeps the camera looking back at the door for a body that walked through backward', () => {
    // Facing away from the door, walking backward through it: the camera looks at the door, and S still moves the body on.
    const back = DOOR + Math.PI;
    const c = carryThrough({ doorYaw: DOOR, cameraYaw: back, heading: Math.PI / 2 - back }, SPAWN);
    expect(c.yaw).toBeCloseTo(SPAWN + Math.PI, 9);
    expect(c.heading).toBeCloseTo(Math.PI / 2 - (SPAWN + Math.PI), 9);
  });

  it('keeps any angle between the camera and the body, and between both and the door', () => {
    const c = carryThrough({ doorYaw: DOOR, cameraYaw: DOOR + 0.7, heading: Math.PI / 2 - (DOOR - 0.2) }, SPAWN);
    expect(c.yaw - SPAWN).toBeCloseTo(0.7, 9);
    expect(Math.PI / 2 - c.heading - SPAWN).toBeCloseTo(-0.2, 9);
  });
});

describe('a door arrival', () => {
  it('faces the spawn the body FIRST stood on, even once a running body has left it', () => {
    // A first entry: the room's descriptor arrives 200 ms after the switch, by when a body at run speed is 2 m away.
    const clock = { now: 1000 };
    const facingAt = spawnAtOrigin(clock, 1200);
    const black = blackout(IDLE, 1000);
    const arrival = beginArrival('transition', 1000);

    expect(stepArrival(arrival, black, 0.1, 0, clock.now, facingAt).action).toBe('wait');
    clock.now = 1200;
    const step = stepArrival(arrival, black, 2.1, 0, clock.now, facingAt);
    expect(step).toEqual({ action: 'place', facing: INTO_THE_ROOM });
  });

  it('places the camera anyway once the wait is over, before the safety valve gives the screen back', () => {
    const black = blackout(IDLE, 1000);
    const arrival = beginArrival('transition', 1000);
    const nowhere = () => null;

    expect(stepArrival(arrival, black, 5, 5, ARRIVAL_WAIT_MS + 999, nowhere).action).toBe('wait');
    expect(stepArrival(arrival, black, 5, 5, ARRIVAL_WAIT_MS + 1000, nowhere)).toEqual({
      action: 'place',
      facing: null,
    });
    expect(ARRIVAL_WAIT_MS).toBeLessThan(HOLD_LIMIT_MS);
  });

  it("places a dungeon's arrival at once: no spawn is published there, and waiting for one was 1.5 s of black", () => {
    const arrival = beginArrival('transition', 1000, false);
    expect(stepArrival(arrival, blackout(IDLE, 1000), 0, 0, 1000, () => INTO_THE_ROOM)).toEqual({
      action: 'place',
      facing: null,
    });
  });

  it('stands down once the curtain is not holding: the valve gave the screen back, and lifting it twice is a flash', () => {
    const arrival = beginArrival('transition', 1000);
    const rescued = advance(blackout(IDLE, 1000), 1000 + HOLD_LIMIT_MS);
    expect(rescued.phase).toBe('in');
    expect(stepArrival(arrival, rescued, 0, 0, 1000 + HOLD_LIMIT_MS, () => INTO_THE_ROOM).action).toBe('abandon');
    expect(stepArrival(arrival, askedFor(IDLE, 'travel', 0), 0, 0, 0, () => INTO_THE_ROOM).action).toBe('abandon');
  });
});
