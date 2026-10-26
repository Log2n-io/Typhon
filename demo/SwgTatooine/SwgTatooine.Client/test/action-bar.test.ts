import { beforeEach, describe, expect, it } from 'vitest';

import {
  ACTION_SLOTS,
  NO_STANCE,
  slotForKey,
  stanceAfter,
  STANCE_WEAPONS,
  serverStance,
  weaponOf,
} from '../src/app/action-bar';
import { AnimationGraph, locomotionFrom } from '../src/character/animation-graph';
import { iconFile, type Catalogue } from '../src/assets/descriptor';
import { boostFor } from '../src/app/gait';
import { useUi } from '../src/state/ui-store';

/** The action bar: 1 the swoop, 2–4 the combat stances, on the bare top-row digits. */

describe('the slot a key uses', () => {
  it('is the bare top-row digit', () => {
    expect(slotForKey('Digit1', false)?.kind).toBe('mount');
    expect(slotForKey('Digit2', false)?.asset).toBe('vibroknuckler');
    expect(slotForKey('Digit3', false)?.asset).toBe('sword_vibroblade');
    expect(slotForKey('Digit4', false)?.asset).toBe('blaster_dl44');
  });

  /** Shift+digit is the boost; the bar and the boost must never both take one key press. */
  it('is none with a modifier, and every shifted digit is a boost instead', () => {
    for (const slot of ACTION_SLOTS) {
      expect(slotForKey(slot.code, true)).toBeNull();
      expect(boostFor(slot.code, true)).not.toBeNull();
      expect(boostFor(slot.code, false)).toBeNull();
    }
  });

  it('is none on the numeric pad and past the four slots', () => {
    expect(slotForKey('Numpad1', false)).toBeNull();
    expect(slotForKey('Digit5', false)).toBeNull();
    expect(slotForKey('KeyW', false)).toBeNull();
  });
});

describe('an icon from the catalogue', () => {
  const base: Catalogue = { schema: 2, version: 3, assets: {}, textures: [] };
  const withIcons: Catalogue = {
    ...base,
    icons: { swoop: { rev: 1, png: { '32': 'swoop_icon32-1.png', '48': 'swoop_icon48-1.png' } } },
  };

  it('is the file published at that size', () => {
    expect(iconFile(withIcons, 'swoop', 48)).toBe('swoop_icon48-1.png');
  });

  /** Never another size: an icon is drawn at its own size. */
  it('is none at a size not published, for an asset without one, and in a catalogue without icons', () => {
    expect(iconFile(withIcons, 'swoop', 64)).toBeNull();
    expect(iconFile(withIcons, 'blaster_dl44', 48)).toBeNull();
    expect(iconFile(base, 'swoop', 48)).toBeNull();
  });
});

describe('using a slot', () => {
  beforeEach(() => {
    useUi.setState({ stance: NO_STANCE, mountRequest: null, slotUsed: null });
  });

  /** A pushed/unpushed toggle: the same key twice is back to idle (Loïc, 2026-10-24). */
  it("toggles an attack slot's stance on and off", () => {
    useUi.getState().pressSlot(2, true);
    expect(useUi.getState().stance).toBe(2);
    useUi.getState().pressSlot(2, true);
    expect(useUi.getState().stance).toBe(NO_STANCE);
  });

  it('moves from one stance straight to another', () => {
    useUi.getState().pressSlot(2, true);
    useUi.getState().pressSlot(4, false);
    expect(useUi.getState().stance).toBe(4);
  });

  /** Riding is idle: the swoop's slot drops the stance, by key and by click alike. */
  it('drops the stance on the swoop', () => {
    useUi.getState().pressSlot(3, true);
    useUi.getState().pressSlot(1, true);
    expect(useUi.getState().stance).toBe(NO_STANCE);
    useUi.getState().pressSlot(3, true);
    useUi.getState().pressSlot(1, false);
    expect(useUi.getState().stance).toBe(NO_STANCE);
  });

  it('drops the stance on a realm crossing', () => {
    useUi.getState().pressSlot(4, true);
    useUi.getState().dropStance();
    expect(useUi.getState().stance).toBe(NO_STANCE);
  });

  /** The 1 key toggles the mount itself; a click asks through the store. Both asking would toggle twice: no change. */
  it('asks for the swoop from a click only', () => {
    useUi.getState().pressSlot(1, true);
    expect(useUi.getState().mountRequest).toBeNull();
    useUi.getState().pressSlot(1, false);
    useUi.getState().pressSlot(1, false);
    expect(useUi.getState().mountRequest?.seq).toBe(2);
  });

  /** Each use restarts the slot's flash, the same slot twice included. */
  it('counts every use for the flash', () => {
    useUi.getState().pressSlot(2, true);
    useUi.getState().pressSlot(2, true);
    expect(useUi.getState().slotUsed).toEqual({ key: 2, seq: 2 });
  });

  it('ignores a slot the bar does not have', () => {
    useUi.getState().pressSlot(7, false);
    expect(useUi.getState().slotUsed).toBeNull();
    expect(useUi.getState().mountRequest).toBeNull();
    expect(useUi.getState().stance).toBe(NO_STANCE);
  });
});

describe('a stance', () => {
  it("holds its slot's weapon, and idle holds none", () => {
    expect(weaponOf(2)).toBe('vibroknuckler');
    expect(weaponOf(3)).toBe('sword_vibroblade');
    expect(weaponOf(4)).toBe('blaster_dl44');
    expect(weaponOf(NO_STANCE)).toBeNull();
    expect(weaponOf(1)).toBeNull();
  });

  it('follows a press: its own slot toggles, another replaces it, the swoop clears it', () => {
    const slot = (key: number) => ACTION_SLOTS.find((s) => s.key === key)!;
    expect(stanceAfter(NO_STANCE, slot(3))).toBe(3);
    expect(stanceAfter(3, slot(3))).toBe(NO_STANCE);
    expect(stanceAfter(3, slot(2))).toBe(2);
    expect(stanceAfter(3, slot(1))).toBe(NO_STANCE);
  });
});

describe("the server's stance", () => {
  /** `Stances` on the server: 0 none, 1 unarmed, 2 one-handed, 3 pistol — the attack slots in order. */
  it('numbers the attack slots 1 to 3, and idle 0', () => {
    expect(serverStance(NO_STANCE)).toBe(0);
    expect(serverStance(2)).toBe(1);
    expect(serverStance(3)).toBe(2);
    expect(serverStance(4)).toBe(3);
  });

  it("gives another player's replicated stance the weapon its slot holds", () => {
    for (const key of [2, 3, 4]) {
      expect(STANCE_WEAPONS[serverStance(key)]).toBe(weaponOf(key));
    }

    expect(STANCE_WEAPONS[0]).toBeNull();
  });
});

describe("a weapon's attack in the animation graph", () => {
  const graph = new AnimationGraph(
    { idle: 'Idle', walk: 'Walk', run: 'Run', attack: ['Punch', 'SwordSlash'] },
    [
      { name: 'Idle', duration: 4, loop: true },
      { name: 'Walk', duration: 1, loop: true },
      { name: 'Run', duration: 1, loop: true },
      { name: 'Punch', duration: 0.75, loop: false },
      { name: 'Shoot_OneHanded', duration: 0.5, loop: false },
    ],
    locomotionFrom(2, 5, 1.8),
  );

  /** The weapon's clip, not the attack role's alternation: a pistol shoots, it does not punch. */
  it('plays the named clip once, then returns to locomotion', () => {
    graph.strike('Shoot_OneHanded', 1);
    const during = graph.update(0, true, 1.2);
    expect(during.clip).toBe('Shoot_OneHanded');
    expect(during.loop).toBe(false);
    expect(graph.update(0, true, 1.6).clip).toBe('Idle');
  });

  /** The crowd finds a weapon's rows by the role it is baked under, so the role is the one the strike names. */
  it('reports the role it was struck under', () => {
    graph.strike('Shoot_OneHanded', 3, 'clip:Shoot_OneHanded');
    const out = graph.update(0, true, 3.1);
    expect(out.clip).toBe('Shoot_OneHanded');
    expect(out.role).toBe('clip:Shoot_OneHanded');
  });

  it('restarts the clip on a second blow', () => {
    graph.strike('Punch', 5);
    const first = graph.update(0, true, 5.1).serial;
    graph.strike('Punch', 5.2);
    expect(graph.update(0, true, 5.3).serial).not.toBe(first);
  });

  it("knows a clip's length, and has none for a clip the rig lacks", () => {
    expect(graph.durationOf('Punch')).toBe(0.75);
    expect(graph.durationOf('SwordSlash')).toBeNaN();
  });
});
