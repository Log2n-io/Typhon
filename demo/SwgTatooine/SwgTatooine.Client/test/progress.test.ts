import { describe, expect, it } from 'vitest';
import { LevelWatch, inspectorTitle, levelTitle, xpPercent } from '../src/data/progress';

describe('xpPercent', () => {
  it('is the byte over 255, as a percent', () => {
    expect(xpPercent(0)).toBe(0);
    expect(xpPercent(255)).toBe(100);
    expect(xpPercent(51)).toBeCloseTo(20, 5);
  });

  it('is NaN before the byte arrives, and clamped past its range', () => {
    expect(xpPercent(Number.NaN)).toBeNaN();
    expect(xpPercent(300)).toBe(100);
    expect(xpPercent(-1)).toBe(0);
  });
});

describe('levelTitle', () => {
  it('says the level and the way to the next one', () => {
    expect(levelTitle(4, 51)).toBe('Combat level 4 — 20 % to 5');
  });

  it('says only the level at the cap', () => {
    expect(levelTitle(50, 255)).toBe('Combat level 50');
  });
});

describe('LevelWatch', () => {
  it('primes on the first sight of a body: a level already held is not news', () => {
    const watch = new LevelWatch();
    expect(watch.update(7, 12)).toBeNull();
    expect(watch.update(7, 12)).toBeNull();
  });

  it('fires on a rise of one or more, once', () => {
    const watch = new LevelWatch();
    watch.update(7, 3);
    expect(watch.update(7, 4)).toBe(4);
    expect(watch.update(7, 4)).toBeNull();
    expect(watch.update(7, 6)).toBe(6);
  });

  it('never fires on a fall', () => {
    const watch = new LevelWatch();
    watch.update(7, 5);
    expect(watch.update(7, 4)).toBeNull();
  });

  it('primes again on a new body: a reconnect or a realm change is not a level-up', () => {
    const watch = new LevelWatch();
    watch.update(7, 3);
    expect(watch.update(9, 5)).toBeNull();
    expect(watch.update(9, 6)).toBe(6);
  });

  it('waits for the level to arrive before priming', () => {
    const watch = new LevelWatch();
    expect(watch.update(7, Number.NaN)).toBeNull();
    expect(watch.update(7, 2)).toBeNull();
    expect(watch.update(7, 3)).toBe(3);
  });
});

describe('inspectorTitle', () => {
  it('titles a player with a character level "Player · CL n"', () => {
    expect(inspectorTitle('Player #12', 'Player', 4)).toBe('Player · CL 4');
  });

  it('keeps the plain title for the crowd, which has no level', () => {
    expect(inspectorTitle('Player #12', 'Player', 0)).toBe('Player #12');
  });

  it('adds a content level to anything else that carries one', () => {
    expect(inspectorTitle('Creature #3', 'Creature', 12)).toBe('Creature #3 · L 12');
  });
});
