import { describe, expect, it } from 'vitest';
import { FIGHT_MEMORY_MS, FightRoster } from '../src/app/fight-roster';

/** Who the avatar is fighting, from the blows it sees: what puts a health bar over a player, whose target is not on the wire. */

const SELF = 7;

const foes = (roster: FightRoster, nowMs: number): number[] => {
  const out: number[] = [];
  roster.forEach(nowMs, (id) => out.push(id));
  return out.sort((a, b) => a - b);
};

describe('the fight roster', () => {
  it('holds whoever the avatar struck and whoever struck it', () => {
    const roster = new FightRoster();
    roster.note(SELF, 20, SELF, 0);
    roster.note(30, SELF, SELF, 0);
    expect(foes(roster, 1000)).toEqual([20, 30]);
  });

  it("ignores everybody else's fights, and every blow without an avatar", () => {
    const roster = new FightRoster();
    roster.note(40, 41, SELF, 0);
    roster.note(SELF, SELF, SELF, 0);
    roster.note(SELF, 50, 0, 0);
    expect(foes(roster, 0)).toEqual([]);
  });

  it(`lets a body leave the fight ${FIGHT_MEMORY_MS / 1000} s after the last blow, and a new blow renews it`, () => {
    const roster = new FightRoster();
    roster.note(SELF, 20, SELF, 0);
    roster.note(30, SELF, SELF, 0);
    roster.note(SELF, 30, SELF, FIGHT_MEMORY_MS - 1);
    expect(foes(roster, FIGHT_MEMORY_MS + 1)).toEqual([30]);
    expect(foes(roster, 2 * FIGHT_MEMORY_MS + 1)).toEqual([]);
  });

  it('forgets everyone for a new session', () => {
    const roster = new FightRoster();
    roster.note(SELF, 20, SELF, 0);
    roster.clear();
    expect(foes(roster, 0)).toEqual([]);
  });
});
