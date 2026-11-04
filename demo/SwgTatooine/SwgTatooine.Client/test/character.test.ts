import { describe, expect, it } from 'vitest';
import { equipped, mitigation, type InventoryItem } from '../src/data/items';
import { parseProgress } from '../src/state/character-store';

/** The character window (client.md C39, U4): its progress read, and the armour totals the server fights with. The installations' notices are mail now (mail.md § 5). */

const piece = (slot: number, kinetic: number, energy: number, broken = false): InventoryItem => ({
  id: String(slot),
  slot,
  item: 'armor',
  name: 'Armour',
  category: 'armor',
  template: 44,
  mk: 1,
  count: 1,
  stats: {
    kinetic,
    energy,
    wears: slot,
    condition: 10,
    maxCondition: 10,
    lifeMilli: 1000,
    repairs: 0,
    broken,
    crafter: '0',
  },
});

describe('the character’s progress', () => {
  it('is read from /swg/character, or not at all', () => {
    expect(
      parseProgress({
        level: 4,
        xp: 1200,
        xpLevelStart: 1000,
        xpNextLevel: 2000,
        killXp: 700,
        missionXp: 500,
        maxHealth: 400,
      }),
    ).toMatchObject({ level: 4, xpNextLevel: 2000 });
    expect(parseProgress({ level: 4 })).toBeNull();
    expect(parseProgress('nope')).toBeNull();
  });
});

describe('the armour totals', () => {
  it('weigh each piece by its slot, leave a broken one out, and stop at the cap', () => {
    const worn = equipped([piece(102, 600, 300), piece(103, 600, 300), piece(107, 600, 300, true)]);
    // Head 13 % and torso 33 % of 600 and 300; the feet broken.
    expect(mitigation(worn)).toEqual({ kinetic: 276, energy: 138 });
    const full = equipped([102, 103, 104, 105, 106, 107].map((s) => piece(s, 1000, 1000)));
    expect(mitigation(full)).toEqual({ kinetic: 750, energy: 750 });
    expect(mitigation(equipped([]))).toEqual({ kinetic: 0, energy: 0 });
  });
});
