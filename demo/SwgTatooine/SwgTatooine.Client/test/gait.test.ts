import { describe, expect, it } from 'vitest';

import { DEFAULT_GAIT, gaitDigit, gaitFor, gaitLabel } from '../src/app/gait';
import { movementIntent } from '../src/app/player-intent';
import { SpeedClass } from '../src/data/source';

/**
 * The gait shortcuts: Shift+1..4 choose how fast this character moves.
 *
 * <b>What is actually at risk is the key MATCHING, which is why most of this file is about codes.</b> The arithmetic is a
 * four-entry map; the thing that silently fails is matching the wrong property of the event. `KeyboardEvent.key` for
 * Shift+1 is `!` on a US layout and `&` on a French one, so a shortcut written against `key` works for whoever wrote it and
 * for nobody else — and nothing about it looks wrong in review.
 */

describe('the gait a key selects', () => {
  it('is the four digits of the top row, with shift', () => {
    expect(gaitFor('Digit1', true)).toBe(SpeedClass.run);
    expect(gaitFor('Digit2', true)).toBe(SpeedClass.burst);
    expect(gaitFor('Digit3', true)).toBe(SpeedClass.speeder);
    expect(gaitFor('Digit4', true)).toBe(SpeedClass.speederFast);
  });

  /** Faster as the digit rises, which is the only thing the person pressing them has been told. */
  it('rises with the digit', () => {
    const ladder = [gaitFor('Digit1', true), gaitFor('Digit2', true), gaitFor('Digit3', true), gaitFor('Digit4', true)];

    expect(ladder).toEqual([...ladder].sort((a, b) => (a ?? 0) - (b ?? 0)));
    expect(new Set(ladder).size).toBe(4);
  });

  /** Shift is required: a bare digit belongs to whatever a later hotbar wants, and a mistyped one must not change speed. */
  it('is none without shift', () => {
    expect(gaitFor('Digit1', false)).toBeNull();
    expect(gaitFor('Digit4', false)).toBeNull();
  });

  /**
   * The numeric pad is deliberately not bound — it was asked for that way, and on a laptop without one the pad codes
   * arrive from a function-key overlay nobody pressed on purpose.
   */
  it('is none on the numeric pad', () => {
    expect(gaitFor('Numpad1', true)).toBeNull();
    expect(gaitFor('Numpad4', true)).toBeNull();
  });

  it('is none for a digit outside the four, or any other key', () => {
    expect(gaitFor('Digit5', true)).toBeNull();
    expect(gaitFor('Digit0', true)).toBeNull();
    expect(gaitFor('KeyW', true)).toBeNull();
    expect(gaitFor('ShiftLeft', true)).toBeNull();
  });

  /** A session starts at a run, which is what every session did before gaits existed. */
  it('starts at a run', () => {
    expect(DEFAULT_GAIT).toBe(SpeedClass.run);
    expect(gaitFor('Digit1', true)).toBe(DEFAULT_GAIT);
  });
});

describe('a gait on screen', () => {
  it('is named, and carries the digit that selects it', () => {
    expect(gaitLabel(SpeedClass.run)).toBe('Run');
    expect(gaitLabel(SpeedClass.speederFast)).toBe('Speeder (fast)');
    expect(gaitDigit(SpeedClass.burst)).toBe(2);
    expect(gaitDigit(SpeedClass.speeder)).toBe(3);
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

  /** Walk and stop have no shortcut: shift-with-a-direction and letting go are how you get them. */
  it('has no digit for the gaits no shortcut selects', () => {
    expect(gaitDigit(SpeedClass.walk)).toBe(0);
    expect(gaitDigit(SpeedClass.stop)).toBe(0);
  });
});

/**
 * What the selected gait does to the intent that gets sent.
 *
 * <b>The gait is what MOVEMENT asks for, and nothing else about the intent changes.</b> The heading arithmetic, the
 * cancelling keys and the stop are A9's and are tested in `player-intent.test.ts`; what is new is one substitution.
 */
describe('a movement intent at a gait', () => {
  const held = (...codes: string[]) => new Set(codes);

  it('asks for the selected gait when shift is held', () => {
    expect(movementIntent(held('KeyW', 'ShiftLeft'), 0, SpeedClass.speeder)?.speedClass).toBe(SpeedClass.speeder);
    expect(movementIntent(held('KeyW', 'ShiftLeft'), 0, SpeedClass.speederFast)?.speedClass).toBe(
      SpeedClass.speederFast,
    );
    expect(movementIntent(held('KeyW', 'ShiftLeft'), 0, SpeedClass.burst)?.speedClass).toBe(SpeedClass.burst);
  });

  /** Unchanged for a caller that has no gait to pass, which is every test written before they existed. */
  it('runs on shift when no gait is given', () => {
    expect(movementIntent(held('KeyW', 'ShiftLeft'), 0)?.speedClass).toBe(SpeedClass.run);
  });

  /**
   * <b>A direction on its own walks, whatever is latched</b> (inverted 2026-10-21, Loïc). Shift is the gait modifier AND
   * the shortcuts' prefix: the press latches which gait Shift gives, the hold uses it. A speeder that could not be
   * dropped to a walk would make the shortcuts a one-way door.
   */
  it('walks without shift, whatever the gait', () => {
    expect(movementIntent(held('KeyW'), 0, SpeedClass.speederFast)?.speedClass).toBe(SpeedClass.walk);
    expect(movementIntent(held('KeyW'), 0, SpeedClass.speeder)?.speedClass).toBe(SpeedClass.walk);
  });

  /** A stop is a stop at every gait: cancelling keys mean stand still, and the gait is what you resume at. */
  it('stops at any gait', () => {
    expect(movementIntent(held('KeyW', 'KeyS'), 0, SpeedClass.speederFast)?.speedClass).toBe(SpeedClass.stop);
    expect(movementIntent(held(), 0, SpeedClass.speederFast)).toBeNull();
  });

  /** The gait does not touch the heading, which is the other half of the intent and belongs to the camera. */
  it('leaves the heading alone', () => {
    const run = movementIntent(held('KeyD'), 0, SpeedClass.run);
    const fast = movementIntent(held('KeyD'), 0, SpeedClass.speederFast);
    expect(fast?.heading).toBe(run?.heading);
  });
});
