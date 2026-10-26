import { WorldStore, type WorldSchema } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { describeFields, formatField } from '../src/data/swg-format';

/**
 * The inspector's field list over a store holding a non-numeric field. The day a player gained its text `name` (A12),
 * `describeFields` asked it for a number, `fieldAt` threw, and since the stats publish inspects the selection inside the
 * frame, a session with a player selected threw on every frame and its HUD — the health bar included — stopped updating
 * (2026-10-25, a MacBook Air).
 */
const SCHEMA: WorldSchema = {
  tickPeriodUs: 100_000,
  archetypes: [
    {
      index: 0,
      name: 'Player',
      groups: ['state'],
      position: { kind: 'motion', model: 'linear', dims: 2 },
      fields: [
        { name: 'name', kind: 'text' },
        { name: 'activity', kind: 'u8', group: 0 },
      ],
    },
  ],
};

describe('describeFields', () => {
  it('lists a text field as its text, and every numeric one formatted, without throwing', () => {
    const world = new WorldStore(SCHEMA, { maxNetId: 16 });
    const store = world.archetypeStore(0);
    world.beginFrame(1);
    const slot = world.enter(0, 3);
    store.textAt(0)[slot] = 'hansolo';

    expect(describeFields(store, slot)).toEqual([
      { name: 'name', value: 'hansolo' },
      { name: 'activity', value: 'Idle' },
    ]);
  });

  it('shows a dash for an empty text — the scripted crowd carries no name', () => {
    const world = new WorldStore(SCHEMA, { maxNetId: 16 });
    world.beginFrame(1);
    const slot = world.enter(0, 4);
    expect(describeFields(world.archetypeStore(0), slot)[0]).toEqual({ name: 'name', value: '—' });
  });
});

describe('formatField', () => {
  it('reads a level as its number', () => {
    expect(formatField('Creature', 'level', 23)).toBe('23');
  });
});
