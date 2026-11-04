import { describe, expect, it } from 'vitest';
import { installationAlerts, NO_ALERTS, type Installation } from '../src/data/installations';
import { equipped, mitigation, type InventoryItem } from '../src/data/items';
import { parseProgress } from '../src/state/character-store';

/** The character window (client.md C39, U4): its progress read, the armour totals the server fights with, and the installations' notices. */

const extractor = (over: Partial<Installation>): Installation => ({
  id: '7',
  kind: 1,
  family: 3,
  familyName: 'Metal',
  x: 1000,
  z: -200,
  rate: 360,
  hopper: 500,
  held: 10,
  state: 1,
  paidSeconds: 4 * 3600,
  ...over,
});

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

describe('the installations’ notices (U4-3)', () => {
  it('say a full hopper and an upkeep under an hour once each, until they clear', () => {
    const first = installationAlerts(NO_ALERTS, [extractor({ held: 500, paidSeconds: 1800 })]);
    expect(first.notices).toHaveLength(2);
    expect(first.notices[0]).toContain('full');
    expect(first.notices[1]).toContain('upkeep');

    const again = installationAlerts(first.next, [extractor({ held: 500, paidSeconds: 1700 })]);
    expect(again.notices).toEqual([]);

    // Collected and paid: forgotten — the next time it fills, it is said again.
    const cleared = installationAlerts(again.next, [extractor({ held: 0, paidSeconds: 8 * 3600 })]);
    expect(cleared.notices).toEqual([]);
    expect(installationAlerts(cleared.next, [extractor({ held: 500 })]).notices).toHaveLength(1);
  });

  it('say nothing of one unpaid from the start, nor of one running well', () => {
    expect(
      installationAlerts(NO_ALERTS, [extractor({ paidSeconds: 0, state: 3 }), extractor({ id: '8' })]).notices,
    ).toEqual([]);
  });
});
