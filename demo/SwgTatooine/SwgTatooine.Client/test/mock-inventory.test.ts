import { describe, expect, it } from 'vitest';
import { lostLines, parseInventory, slotGrid } from '../src/data/items';
import { noticeLines } from '../src/data/notices';
import { MOCK_DROP_MS, MockInventory } from '../src/data/mock/inventory';
import { RevisionReader } from '../src/data/revision-reader';

/**
 * The mock's `GET /swg/inventory` (inventory-loot.md § 3.6): the window exercised without a server. Its answer is the
 * contract's shape, its revision moves with each drop, and its placement keeps the server's rules.
 */

describe('the mock inventory', () => {
  it('moves its revision once per drop, from 1 on the first call', () => {
    const inv = new MockInventory(7);
    expect(inv.revision(1000)).toBe(1);
    expect(inv.revision(1000 + MOCK_DROP_MS - 1)).toBe(1);
    expect(inv.revision(1000 + MOCK_DROP_MS)).toBe(2);
    expect(inv.revision(1000 + MOCK_DROP_MS * 4)).toBe(5);
    expect(inv.takeNotices().filter((n) => n.kind === 'kill').length).toBeGreaterThanOrEqual(4);
  });

  it("answers the contract's shape, read by the inventory reader under the mock's token", async () => {
    const inv = new MockInventory(7);
    inv.revision(0);
    inv.revision(MOCK_DROP_MS * 3);
    const reader = new RevisionReader('swg/inventory', parseInventory, () => 'mock', inv.fetch);
    reader.update(inv.revision(MOCK_DROP_MS * 3));
    for (let i = 0; i < 5; i++) {
      await Promise.resolve();
    }

    expect(reader.value).toEqual(inv.snapshot());
    expect(reader.value?.open).toBe(true);
    expect((await (await inv.fetch('swg/wallet', { headers: {} })).json()) === null).toBe(true);
  });

  it('stacks to 99 then opens the lowest free slot, loses what does not fit, and says each drop once', () => {
    const inv = new MockInventory(3, 4);
    inv.revision(0);
    let before = inv.snapshot();
    let lostCount = 0;
    for (let t = 1; t <= 400; t++) {
      inv.revision(t * MOCK_DROP_MS);
      const after = inv.snapshot();
      // What fit is one line (its notices), what did not another (the lost notes): a drop is never silent, never two lines of either.
      const lines = [...noticeLines(inv.takeNotices()), ...lostLines(before, after)];
      expect(lines.length).toBeGreaterThanOrEqual(1);
      expect(lines.filter((l) => l.startsWith('Loot: ')).length).toBeLessThanOrEqual(1);
      expect(lines.filter((l) => l.startsWith('Inventory full')).length).toBeLessThanOrEqual(1);
      lostCount += lines.filter((l) => l.startsWith('Inventory full')).length;
      before = after;
    }

    expect(lostCount).toBeGreaterThan(0);
    const snap = inv.snapshot();
    expect(snap.items.every((i) => i.count >= 1 && i.count <= 99)).toBe(true);
    expect(new Set(snap.items.map((i) => i.slot)).size).toBe(snap.items.length);
    expect(slotGrid(snap.items, 4).every((c) => c !== null)).toBe(true);
    expect(snap.lost.length).toBeGreaterThan(0);
    expect(snap.lost.length).toBeLessThanOrEqual(10);
  });
});
