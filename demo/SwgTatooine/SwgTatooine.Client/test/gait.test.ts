import { describe, expect, it } from 'vitest';

import { boostFor, DEFAULT_BOOST, gaitLabel } from '../src/app/gait';
import { asRide, boostOf, gaitChanged, intentChanged, movementIntent, stopAfter } from '../src/app/player-intent';
import { speedForClass } from '../src/app/predictor';
import { SpeedClass } from '../src/data/source';

/**
 * The boost shortcuts: Shift+1..4 multiply how fast this character walks, runs and rides, by 1 to 4.
 *
 * <b>What is actually at risk is the key MATCHING, which is why much of this file is about codes.</b> The arithmetic is a
 * four-entry map; the thing that silently fails is matching the wrong property of the event. `KeyboardEvent.key` for
 * Shift+1 is `!` on a US layout and `&` on a French one, so a shortcut written against `key` works for whoever wrote it and
 * for nobody else — and nothing about it looks wrong in review.
 */

describe('the boost a key selects', () => {
  it('is the digit of the top row, with shift', () => {
    expect(boostFor('Digit1', true)).toBe(1);
    expect(boostFor('Digit2', true)).toBe(2);
    expect(boostFor('Digit3', true)).toBe(3);
    expect(boostFor('Digit4', true)).toBe(4);
  });

  /** Shift is required: the bare 1 is the swoop, and a mistyped digit must not change speed. */
  it('is none without shift', () => {
    expect(boostFor('Digit1', false)).toBeNull();
    expect(boostFor('Digit4', false)).toBeNull();
  });

  /**
   * The numeric pad is deliberately not bound — it was asked for that way, and on a laptop without one the pad codes
   * arrive from a function-key overlay nobody pressed on purpose.
   */
  it('is none on the numeric pad', () => {
    expect(boostFor('Numpad1', true)).toBeNull();
    expect(boostFor('Numpad4', true)).toBeNull();
  });

  it('is none for a digit outside the four, or any other key', () => {
    expect(boostFor('Digit5', true)).toBeNull();
    expect(boostFor('Digit0', true)).toBeNull();
    expect(boostFor('KeyW', true)).toBeNull();
    expect(boostFor('ShiftLeft', true)).toBeNull();
  });

  /** A session starts unboosted. */
  it('starts at ×1', () => {
    expect(DEFAULT_BOOST).toBe(1);
  });
});

describe('a way of moving on screen', () => {
  it('is named', () => {
    expect(gaitLabel(SpeedClass.walk)).toBe('Walk');
    expect(gaitLabel(SpeedClass.run)).toBe('Run');
    expect(gaitLabel(SpeedClass.swoop)).toBe('Swoop');
    expect(gaitLabel(SpeedClass.stop)).toBe('Stopped');
  });

  /**
   * The label never contains a speed. The HUD prints the server's figure beside it, and a number in the name would be a
   * second copy of it — wrong the moment the server grants something else, in the one place a viewer would believe.
   */
  it('never names a speed', () => {
    for (const gait of Object.values(SpeedClass)) {
      expect(gaitLabel(gait)).not.toMatch(/\d/);
    }
  });
});

/**
 * What the boost does to the intent that gets sent: it rides along, on walk, run and the swoop alike, and nothing else
 * about the intent changes. The heading arithmetic, the cancelling keys and the stop are A9's (`player-intent.test.ts`).
 */
describe('a movement intent at a boost', () => {
  const held = (...codes: string[]) => new Set(codes);

  it('carries the boost when walking, running and riding', () => {
    expect(movementIntent(held('KeyW'), 0, 3)).toMatchObject({ speedClass: SpeedClass.walk, boost: 3 });
    expect(movementIntent(held('KeyW', 'ShiftLeft'), 0, 3)).toMatchObject({ speedClass: SpeedClass.run, boost: 3 });
    expect(asRide(movementIntent(held('KeyW'), 0, 3), true)).toMatchObject({ speedClass: SpeedClass.swoop, boost: 3 });
  });

  /** Unchanged for a caller that has no boost to pass, which is every test written before it existed. */
  it('is ×1 when none is given', () => {
    expect(boostOf(movementIntent(held('KeyW', 'ShiftLeft'), 0))).toBe(1);
    expect(boostOf({ heading: 0, speedClass: SpeedClass.run })).toBe(1);
  });

  /** The boost does not touch the heading or the class, which belong to the camera and to Shift. */
  it('leaves the heading and the class alone', () => {
    const plain = movementIntent(held('KeyD', 'ShiftLeft'), 0.4, 1);
    const fast = movementIntent(held('KeyD', 'ShiftLeft'), 0.4, 4);
    expect(fast?.heading).toBe(plain?.heading);
    expect(fast?.speedClass).toBe(plain?.speedClass);
  });

  /**
   * A boost latched under a held key is a CHANGE, both to send and to predict from: the speed is different from that
   * instant, and a client that waited for the refresh window would integrate the old speed while the server ran the new one.
   */
  it('is a change when it moves under a held key, and not when nothing is moving', () => {
    const run1 = movementIntent(held('KeyW', 'ShiftLeft'), 0, 1);
    const run2 = movementIntent(held('KeyW', 'ShiftLeft'), 0, 2);
    expect(intentChanged(run2, run1)).toBe(true);
    expect(gaitChanged(run2, run1)).toBe(true);

    const stop1 = movementIntent(held('KeyW', 'KeyS'), 0, 1);
    const stop2 = movementIntent(held('KeyW', 'KeyS'), 0, 2);
    expect(gaitChanged(stop2, stop1)).toBe(false);
  });

  /** The stop owed after a boosted move names the same boost, so the server is not asked for a different speed to stop at. */
  it('keeps its boost on the stop that ends it', () => {
    expect(stopAfter(movementIntent(held('KeyW'), 0, 4))).toMatchObject({ speedClass: SpeedClass.stop, boost: 4 });
  });

  /** The predictor's speed is the replicated stat times the factor sent — the same product the server applies. */
  it('multiplies the predicted speed by the factor', () => {
    const granted = { walkMps: 5, runMps: 10, swoopMps: 40 };
    expect(speedForClass(SpeedClass.walk, granted, 2)).toBe(10);
    expect(speedForClass(SpeedClass.run, granted, 3)).toBe(30);
    expect(speedForClass(SpeedClass.swoop, granted, 4)).toBe(160);
    expect(speedForClass(SpeedClass.stop, granted, 4)).toBe(0);
  });
});
