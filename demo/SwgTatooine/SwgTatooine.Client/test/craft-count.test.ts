import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { parseSchematics } from '../src/data/craft';
import type { InventoryItem } from '../src/data/items';
import { craftableCount } from '../src/ui/CraftPanel';

/** The crafting panel's count beside each schematic: how many times what is held covers its slots. */
describe('the craftable count', () => {
  const book = parseSchematics(JSON.parse(readFileSync(join(__dirname, 'data', 'schematics.json'), 'utf8')) as unknown);
  const units = (template: number, mk: number, count: number): InventoryItem => ({
    id: `${String(template)}-${String(mk)}`,
    slot: 1,
    item: 'x',
    name: 'x',
    category: 'material',
    template,
    mk,
    count,
  });
  const part = (id: string, template: number): InventoryItem => ({ ...units(template, 0, 1), id, category: 'part' });

  it('is the fewest a slot allows: one Mk covering a material, the parts by the slot count', () => {
    const grip = book?.schematics.find((s) => s.name === 'Grip');
    const blaster = book?.schematics.find((s) => s.name === 'DL-44 Blaster');
    expect(grip).toBeDefined();
    expect(blaster).toBeDefined();
    if (grip === undefined || blaster === undefined) {
      return;
    }

    // Grip: Hide ×6 quality, Metal ×4 bulk — 13 Hide Mk II covers it twice, the Metal three times split over two Mks does not add up.
    expect(craftableCount(grip, [units(8, 2, 13), units(3, 1, 2), units(3, 2, 9)])).toBe(2);
    expect(craftableCount(grip, [units(8, 2, 13)])).toBe(0);
    // The blaster: a core, three parts, Metal ×20 — the parts are the limit.
    const held = [units(12, 2, 3), units(3, 1, 60), part('b1', 20), part('c1', 22), part('g1', 25), part('b2', 20)];
    expect(craftableCount(blaster, held)).toBe(1);
  });
});
