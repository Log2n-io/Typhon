import type { InventoryItem } from './items';

/**
 * Refine (crafting-catalog.md § 11.1), as the server's `Refining` states it: N units of a material, core or add-on at one Mk into one of the next, at a
 * crafting station, for 5 % of the output's value. The numbers are the server's; a refusal is still the authority.
 */

/** The fee, thousandths of the output's market value. */
export const REFINE_FEE_PER_MILLE = 50;

/** The most cores or add-ons one refine makes: a crate's capacity. A material refines a stack's worth. */
export const REFINE_MAX_ITEMS = 10;

const STACK_MAX = 99;
const ITEM_RATIO = [0, 3, 4, 5, 7];
const MATERIAL_RATIO = [0, 12, 3, 4, 6];

/** How many of a category at Mk `mk` make one of the next; 0 when it does not refine (a crafted item, a kit, Mk V). */
export function refineRatio(category: string, mk: number): number {
  if (mk < 1 || mk > 4) {
    return 0;
  }

  return category === 'material'
    ? MATERIAL_RATIO[mk]
    : category === 'core' || category === 'addon'
      ? ITEM_RATIO[mk]
      : 0;
}

/** The most outputs one refine of a category makes; 0 when it does not refine. */
export function refineMaxOutputs(category: string): number {
  return category === 'material' ? STACK_MAX : category === 'core' || category === 'addon' ? REFINE_MAX_ITEMS : 0;
}

/** The fee for `outputs` of the next Mk, from one unit's value at that Mk (credits, a material's decimal), whole credits rounded down — 1 at least. */
export function refineFee(nextValue: number, outputs: number): number {
  const milli = Math.round(nextValue * 1000) * outputs;
  return milli <= 0 ? 0 : Math.max(1, Math.floor((milli * REFINE_FEE_PER_MILLE) / 1_000_000));
}

/** One refine the inventory allows: what, from which Mk, how many units are held, N, and the most one action makes from them. */
export interface RefineOption {
  readonly template: number;
  readonly item: string;
  readonly name: string;
  readonly category: string;
  readonly mk: number;
  readonly held: number;
  readonly ratio: number;
  /** ⌊held / N⌋, capped at one action's worth; at least 1. */
  readonly most: number;
}

/** Every (template, Mk) in the grid held at least N times, by name then Mk. */
export function refineOptions(items: readonly InventoryItem[]): RefineOption[] {
  const held = new Map<string, { item: InventoryItem; count: number }>();
  for (const i of items) {
    if (i.stats !== undefined || i.slot > 100 || i.template === 0 || refineRatio(i.category, i.mk) === 0) {
      continue;
    }

    const key = `${String(i.template)}:${String(i.mk)}`;
    const was = held.get(key);
    held.set(key, { item: was?.item ?? i, count: (was?.count ?? 0) + i.count });
  }

  const options: RefineOption[] = [];
  for (const { item, count } of held.values()) {
    const ratio = refineRatio(item.category, item.mk);
    const most = Math.min(Math.floor(count / ratio), refineMaxOutputs(item.category));
    if (most >= 1) {
      options.push({
        template: item.template,
        item: item.item,
        name: item.name,
        category: item.category,
        mk: item.mk,
        held: count,
        ratio,
        most,
      });
    }
  }

  return options.sort((a, b) => a.name.localeCompare(b.name) || a.mk - b.mk);
}
