import { describe, expect, it } from 'vitest';
import { CURSORS, cursorFor, isShootable, selectionTone, type CursorSubject } from '../src/app/game-cursor';

const body = (archetype: string | null, hp = 1, self = false): CursorSubject => ({ archetype, hp, self });

describe('cursorFor', () => {
  it('is a crosshair only where a click would attack: playing, in a stance, over something the server shoots', () => {
    for (const archetype of ['Creature', 'CreatureLair', 'Player']) {
      expect(cursorFor(body(archetype), true, true), archetype).toBe('attack');
      expect(cursorFor(body(archetype), true, false), `${archetype} without a stance`).toBe('select');
      expect(cursorFor(body(archetype), false, true), `${archetype} from the god camera`).toBe('select');
    }
  });

  it('selects what the server will not shoot: a city NPC, a building, the dead, the viewer, nothing', () => {
    expect(cursorFor(body('CityNpc'), true, true)).toBe('select');
    expect(cursorFor(body('WorldObject', Number.NaN), true, true)).toBe('select');
    expect(cursorFor(body('Creature', 0), true, true)).toBe('select');
    expect(cursorFor(body('Player', 1, true), true, true)).toBe('select');
    expect(cursorFor(body(null, Number.NaN), true, true)).toBe('select');
  });

  it('names each image with its hotspot and a system fallback', () => {
    expect(CURSORS.select).toMatch(/^url\("data:image\/svg\+xml,.+"\) 2 1, default$/);
    expect(CURSORS.select).toContain(encodeURIComponent('width="22" height="22"'));
    expect(CURSORS.attack).toMatch(/^url\("data:image\/svg\+xml,.+"\) 16 16, crosshair$/);
  });
});

describe('isShootable', () => {
  it('takes a player as a target, as the server does (2026-10-25)', () => {
    expect(isShootable(body('Player'))).toBe(true);
  });
});

describe('selectionTone', () => {
  it('is red when the selection is attacking the viewer, armed or not', () => {
    expect(selectionTone(body('Creature'), true, false, true)).toBe('hostile');
    expect(selectionTone(body('Creature'), true, true, true)).toBe('hostile');
  });

  it('is red when the viewer is attacking it, and green when it would only be selected', () => {
    expect(selectionTone(body('Creature'), true, true, false)).toBe('hostile');
    expect(selectionTone(body('Creature'), true, false, false)).toBe('passive');
    expect(selectionTone(body('CityNpc'), true, true, false)).toBe('passive');
  });

  it('is green once the fight is over: a dead attacker is no threat', () => {
    expect(selectionTone(body('Creature', 0), true, true, true)).toBe('passive');
  });
});
