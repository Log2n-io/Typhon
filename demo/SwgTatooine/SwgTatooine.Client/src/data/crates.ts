import type { InventoryItem } from './items';

/**
 * Crates (crafting-catalog.md § 11.2), as the server's `Crates` states them: identical pristine items with stats held as one, its count how many — one
 * slot, one listing. The server says what packs with what (`packKey`); a refusal is still the authority.
 */

/** A crate's capacity. */
export const CRATE_CAPACITY = 10;

/** The next pack of a group of identical items: `item` (an item or a crate) into `into`, making `total`. */
export interface PackStep {
  readonly name: string;
  readonly mk: number;
  readonly item: string;
  readonly into: string;
  readonly total: number;
  /** How many identical items the group holds, crates included. */
  readonly held: number;
}

/** For each group of two or more identical items in the grid with room left in one: the smallest packed into the fullest that it fits. */
export function packSteps(items: readonly InventoryItem[]): PackStep[] {
  const groups = new Map<string, InventoryItem[]>();
  for (const i of items) {
    if (i.packKey !== undefined && i.slot <= 100) {
      const group = groups.get(i.packKey) ?? [];
      group.push(i);
      groups.set(i.packKey, group);
    }
  }

  const steps: PackStep[] = [];
  for (const group of groups.values()) {
    const open = group.filter((i) => i.count < CRATE_CAPACITY).sort((a, b) => b.count - a.count || a.slot - b.slot);
    const into = open[0] as InventoryItem | undefined;
    const item =
      into === undefined
        ? undefined
        : open
            .slice(1)
            .reverse()
            .find((i) => into.count + i.count <= CRATE_CAPACITY);
    if (into !== undefined && item !== undefined) {
      steps.push({
        name: into.name,
        mk: into.mk,
        item: item.id,
        into: into.id,
        total: into.count + item.count,
        held: group.reduce((n, i) => n + i.count, 0),
      });
    }
  }

  return steps.sort((a, b) => a.name.localeCompare(b.name) || a.mk - b.mk);
}

/** The crates in the grid: items with stats whose count is above one. */
export function cratesOf(items: readonly InventoryItem[]): InventoryItem[] {
  return items.filter((i) => i.stats !== undefined && i.count > 1 && i.slot <= 100);
}
