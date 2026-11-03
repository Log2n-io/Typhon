import { describe, expect, it } from 'vitest';

import { cratesOf, packSteps } from '../src/data/crates';
import type { InventoryItem } from '../src/data/items';
import { itemVendorPayout, marketWire } from '../src/data/market';

const STATS = { condition: 100, maxCondition: 100, lifeMilli: 1000, repairs: 0, crafter: '0' };

function blaster(id: string, slot: number, count = 1, packKey: string | null = 'k'): InventoryItem {
  return {
    id,
    slot,
    item: 'weapon_blaster',
    name: 'Blaster',
    category: 'weapon',
    template: 69,
    mk: 1,
    count,
    stats: STATS,
    ...(packKey === null ? {} : { packKey }),
  };
}

describe('crates (crafting-catalog.md § 11.2)', () => {
  it('packs the smallest of a group into the fullest it fits, ten to a crate', () => {
    expect(packSteps([blaster('a', 1), blaster('b', 2, 4), blaster('c', 3)])).toEqual([
      { name: 'Blaster', mk: 1, item: 'c', into: 'b', total: 5, held: 6 },
    ]);
    expect(packSteps([blaster('a', 1, 10), blaster('b', 2)])).toEqual([]);
    expect(packSteps([blaster('a', 1, 9), blaster('b', 2, 2), blaster('c', 3)])).toEqual([
      { name: 'Blaster', mk: 1, item: 'c', into: 'a', total: 10, held: 12 },
    ]);
  });

  it('packs only what the server keys alike, in the grid', () => {
    expect(packSteps([blaster('a', 1), blaster('b', 2, 1, 'other')])).toEqual([]);
    expect(packSteps([blaster('a', 1), blaster('b', 2, 1, null)])).toEqual([]);
    expect(packSteps([blaster('a', 1), blaster('b', 101)])).toEqual([]);
  });

  it('lists the crates, and sells one whole for its count', () => {
    expect(cratesOf([blaster('a', 1, 3), blaster('b', 2)]).map((c) => c.id)).toEqual(['a']);
    expect(itemVendorPayout(181_200, STATS, 3)).toBe(543);
  });

  it('goes out as PackItem and UnpackItem', () => {
    expect(marketWire({ kind: 'pack', item: '12', into: '34' })).toEqual({
      name: 'PackItem',
      values: { item: 12n, into: 34n },
    });
    expect(marketWire({ kind: 'unpack', item: '12', count: 2 })).toEqual({
      name: 'UnpackItem',
      values: { item: 12n, count: 2 },
    });
  });
});
