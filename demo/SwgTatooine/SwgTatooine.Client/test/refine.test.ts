import { describe, expect, it } from 'vitest';

import type { InventoryItem } from '../src/data/items';
import { marketWire } from '../src/data/market';
import { refineFee, refineMaxOutputs, refineOptions, refineRatio } from '../src/data/refine';
import { craftDoneText } from '../src/state/craft-store';

function units(template: number, category: string, mk: number, count: number, slot = 1): InventoryItem {
  return {
    id: `${String(template)}${String(mk)}${String(slot)}`,
    slot,
    item: `a${String(template)}`,
    name: `T${String(template)}`,
    category,
    template,
    mk,
    count,
  };
}

describe('refine (crafting-catalog.md § 11.1)', () => {
  it('takes 3 / 4 / 5 / 7 cores or add-ons and 12 / 3 / 4 / 6 materials a step, and nothing past Mk IV or of a crafted kind', () => {
    expect([1, 2, 3, 4].map((mk) => refineRatio('core', mk))).toEqual([3, 4, 5, 7]);
    expect([1, 2, 3, 4].map((mk) => refineRatio('addon', mk))).toEqual([3, 4, 5, 7]);
    expect([1, 2, 3, 4].map((mk) => refineRatio('material', mk))).toEqual([12, 3, 4, 6]);
    expect([refineRatio('core', 5), refineRatio('core', 0), refineRatio('part', 1), refineRatio('kit', 1)]).toEqual([
      0, 0, 0, 0,
    ]);
    expect([refineMaxOutputs('core'), refineMaxOutputs('material'), refineMaxOutputs('weapon')]).toEqual([10, 99, 0]);
  });

  it("charges 5 % of the output's value, rounded down, 1 cr at least, as the server does", () => {
    expect([1549, 3099, 5950, 11106].map((v) => refineFee(v, 1))).toEqual([77, 154, 297, 555]);
    expect(refineFee(2.8, 2)).toBe(1);
    expect(refineFee(0, 2)).toBe(0);
    expect(refineFee(2.8, 99)).toBe(13);
  });

  it('offers what is held N times, summed over stacks, capped at one action', () => {
    const items = [
      units(12, 'core', 1, 20, 1),
      units(12, 'core', 1, 20, 2),
      units(3, 'material', 1, 11, 3),
      units(16, 'addon', 4, 7, 4),
      units(16, 'addon', 5, 9, 5),
      {
        ...units(69, 'weapon', 1, 1, 6),
        stats: { condition: 1, maxCondition: 1, repairs: 0, crafter: '0' },
      } as InventoryItem,
    ];
    expect(refineOptions(items).map((o) => [o.template, o.mk, o.held, o.ratio, o.most])).toEqual([
      [12, 1, 40, 3, 10],
      [16, 4, 7, 7, 1],
    ]);
  });

  it('goes out as RefineItems, and is done when the next Mk is held', () => {
    expect(marketWire({ kind: 'refine', template: 12, mk: 1, count: 2 })).toEqual({
      name: 'RefineItems',
      values: { template: 12, mk: 1, count: 2 },
    });
    const sent = {
      kind: 'refine' as const,
      seq: 1,
      item: '',
      repairs: 0,
      label: '2 × Core Mk II',
      output: '12:2',
      units: 0,
    };
    expect(craftDoneText(sent, null, null, [units(12, 'core', 1, 3)])).toBeNull();
    expect(craftDoneText(sent, null, null, [units(12, 'core', 2, 2)])).toBe('Refined: 2 × Core Mk II');
  });
});
