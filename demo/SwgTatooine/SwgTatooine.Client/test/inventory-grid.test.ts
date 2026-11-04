import { describe, expect, it } from 'vitest';
import {
  dropOnSlot,
  freeSlots,
  IDLE_SPIN,
  MAX_SPIN,
  PITCH_LIMIT,
  previewLines,
  promptCount,
  promptRange,
  SPIN_START,
  spinDrag,
  spinHold,
  spinStep,
} from '../src/data/inventory-grid';
import { slotCount, STACK_MAX, type InventoryItem, type ItemStats } from '../src/data/items';
import { commandRow, MAX_LISTING_QTY, marketWire } from '../src/data/market';

function units(id: string, slot: number, count: number, item = 'mat_ore', mk = 1): InventoryItem {
  return { id, slot, item, name: 'Ore', category: 'material', template: 30, mk, count };
}

const STATS: ItemStats = {
  condition: 100,
  maxCondition: 100,
  lifeMilli: 1000,
  repairs: 0,
  crafter: '0',
  wears: 101,
  damage: 81,
};

function gear(id: string, slot: number, count = 1, packKey = 'k'): InventoryItem {
  return {
    id,
    slot,
    item: 'blaster_dl44',
    name: 'DL-44',
    category: 'weapon',
    template: 69,
    mk: 1,
    count,
    stats: STATS,
    packKey,
  };
}

describe('a stack dropped on a slot (client.md C43)', () => {
  it('moves to a free slot whole, merges into the same units, swaps with anything else', () => {
    const ore = units('a', 1, 500);
    expect(dropOnSlot(ore, 7, null)).toEqual({ kind: 'move', item: 'a', slot: 7, count: 500 });
    expect(dropOnSlot(ore, 2, units('b', 2, 300))).toEqual({ kind: 'move', item: 'a', slot: 2, count: 500 });
    expect(dropOnSlot(ore, 2, units('b', 2, 300, 'mat_ore', 2)), 'another Mk is another item: a swap').toEqual({
      kind: 'move',
      item: 'a',
      slot: 2,
      count: 500,
    });
    expect(dropOnSlot(ore, 2, gear('g', 2))).toEqual({ kind: 'move', item: 'a', slot: 2, count: 500 });
  });

  it('does nothing on itself, from an equip slot, or onto a full stack', () => {
    const ore = units('a', 1, 500);
    expect(dropOnSlot(ore, 1, null)).toBeNull();
    expect(dropOnSlot({ ...ore, slot: 101 }, 3, null)).toBeNull();
    expect(dropOnSlot(ore, 2, units('b', 2, STACK_MAX))).toBeNull();
  });

  it('packs identical items into a crate with room, and swaps when there is none', () => {
    expect(dropOnSlot(gear('a', 1), 2, gear('b', 2, 4))).toEqual({ kind: 'pack', item: 'a', into: 'b' });
    expect(dropOnSlot(gear('a', 1, 3), 2, gear('b', 2, 8))).toEqual({ kind: 'move', item: 'a', slot: 2, count: 3 });
    expect(dropOnSlot(gear('a', 1), 2, gear('b', 2, 1, 'other'))).toEqual({
      kind: 'move',
      item: 'a',
      slot: 2,
      count: 1,
    });
  });
});

describe('the count prompt', () => {
  it('splits off part of a stack, unpacks part of a crate, throws away units by the count and an item whole', () => {
    expect(promptRange('split', units('a', 1, 9))).toEqual({ min: 1, max: 8, start: 4, fixed: false });
    expect(promptRange('unpack', gear('c', 1, 6))).toEqual({ min: 1, max: 5, start: 1, fixed: false });
    expect(promptRange('discard', units('a', 1, 9))).toEqual({ min: 1, max: 9, start: 9, fixed: false });
    expect(promptRange('discard', units('a', 1, 1)).fixed).toBe(true);
    expect(promptRange('discard', gear('c', 1, 6))).toEqual({ min: 6, max: 6, start: 6, fixed: true });
  });

  it('unpacks no more than the free slots hold, and has nothing to ask with none or a stack of one', () => {
    expect(promptRange('unpack', gear('c', 1, 6), 3)).toEqual({ min: 1, max: 3, start: 1, fixed: false });
    expect(promptRange('unpack', gear('c', 1, 6), 10).max).toBe(5);
    const none = promptRange('unpack', gear('c', 1, 6), 0);
    expect(none.max).toBeLessThan(none.min);
    const one = promptRange('split', units('a', 1, 1));
    expect(one.max).toBeLessThan(one.min);
    expect(one.start).toBe(1);
  });

  it('clamps what is typed, and starts over from a field cleared', () => {
    const range = { min: 1, max: 8, start: 4 };
    expect(promptCount('3', range)).toBe(3);
    expect(promptCount('80', range)).toBe(8);
    expect(promptCount('0', range)).toBe(1);
    expect(promptCount('2.9', range)).toBe(2);
    expect(promptCount('', range)).toBe(4);
    expect(promptCount('x', range)).toBe(4);
  });

  it('knows the free slots, lowest first', () => {
    expect(freeSlots([units('a', 1, 1), units('b', 3, 1)], 4)).toEqual([2, 4]);
  });
});

describe('a stack of 100 000', () => {
  it('is drawn in four characters at most, rounded down', () => {
    expect(slotCount(7)).toBe('7');
    expect(slotCount(9999)).toBe('9999');
    expect(slotCount(10_000)).toBe('10.0K');
    expect(slotCount(12_345)).toBe('12.3K');
    expect(slotCount(99_999)).toBe('99.9K');
    expect(slotCount(STACK_MAX)).toBe('100K');
  });

  it('is what a listing holds at most, and what the preview counts against', () => {
    expect(MAX_LISTING_QTY).toBe(STACK_MAX);
    expect(previewLines(units('a', 1, 1234))).toContainEqual({ label: 'Stack', value: '1,234 / 100,000' });
  });
});

describe('the wire of the window’s commands', () => {
  it('sends a move and a discard by name, refused on the gear row', () => {
    expect(marketWire({ kind: 'move', item: '12', slot: 0, count: 50_000 })).toEqual({
      name: 'MoveItem',
      values: { item: 12n, slot: 0, count: 50_000 },
    });
    expect(marketWire({ kind: 'discard', item: '12', count: 3 })).toEqual({
      name: 'DiscardItem',
      values: { item: 12n, count: 3 },
    });
    expect(commandRow({ kind: 'move', item: '12', slot: 4, count: 1 })).toBe('gear');
    expect(commandRow({ kind: 'discard', item: '12', count: 1 })).toBe('gear');
  });
});

describe('the preview', () => {
  it('says an item’s stats, its wear and its maker', () => {
    const lines = previewLines(gear('g', 1));
    expect(lines.map((l) => l.label)).toEqual(['Kind', 'Mk', 'Damage', 'Worn on', 'Condition', 'Repairs', 'Made by']);
    expect(lines.find((l) => l.label === 'Worn on')?.value).toBe('Weapon');
    expect(previewLines(gear('c', 1, 4)).find((l) => l.label === 'Crate')?.value).toBe('4 / 10 identical');
  });

  it('spins on its own, follows a drag, keeps a fling and eases back to the idle spin', () => {
    const idle = spinStep(SPIN_START, 1);
    expect(idle.speed).toBeCloseTo(IDLE_SPIN);
    expect(idle.yaw).toBeCloseTo(SPIN_START.yaw + IDLE_SPIN);

    const flung = spinDrag(SPIN_START, -100, 0, 0.05);
    expect(flung.speed).toBeGreaterThan(IDLE_SPIN);
    expect(spinDrag(SPIN_START, -10_000, 0, 0.001).speed).toBe(MAX_SPIN);
    expect(spinDrag(SPIN_START, 100, 0, 0.05).speed, 'flung the other way').toBeLessThan(0);
    let s = flung;
    for (let i = 0; i < 300; i++) {
      s = spinStep(s, 1 / 60);
    }

    expect(Math.abs(s.speed - IDLE_SPIN)).toBeLessThan(Math.abs(flung.speed - IDLE_SPIN) / 10);
    expect(Math.abs(s.yaw)).toBeLessThanOrEqual(Math.PI);
  });

  it('tilts within its limit, and a held drag lets the model go still', () => {
    expect(spinDrag(SPIN_START, 0, 10_000, 0.05).pitch).toBe(-PITCH_LIMIT);
    expect(spinDrag(SPIN_START, 0, -10_000, 0.05).pitch).toBe(PITCH_LIMIT);
    expect(spinHold({ yaw: 0, pitch: 0, speed: 10 }, 0.5).speed).toBeLessThan(0.05);
  });
});
