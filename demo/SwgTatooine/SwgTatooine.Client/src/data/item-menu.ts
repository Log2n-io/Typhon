import type { ContextMenuItem } from '../state/ui-store';
import { CRATE_CAPACITY, packSteps } from './crates';
import { kitMenuItems } from './installations';
import type { InventoryItem } from './items';

/**
 * An item's context menu (client.md C42): everything the character can do with it from its slot — a kit placed here, an item with stats worn, an item
 * packed into a crate with the identical ones, a crate unpacked. Empty for an item that offers nothing: units and parts are only listed, crafted, sold.
 *
 * @param item The item held on.
 * @param items The whole inventory, which says what it packs with.
 */
export function itemMenuItems(item: InventoryItem, items: readonly InventoryItem[]): ContextMenuItem[] {
  if (item.slot > 100) {
    return [];
  }

  const entries: ContextMenuItem[] = [...kitMenuItems(item)];
  const crate = item.stats !== undefined && item.count > 1;
  if (item.stats !== undefined && (item.stats.wears ?? 0) > 0 && !crate) {
    entries.push({
      label: 'Equip',
      hint: 'Worn in its slot, swapped with what was there',
      action: { kind: 'equip', item: item.id },
    });
  }

  const step = packSteps(items).find((s) => s.item === item.id || s.into === item.id);
  if (step !== undefined) {
    entries.push({
      label: `Pack into a crate (${String(step.total)})`,
      hint: `Identical unused items in one slot, ${String(CRATE_CAPACITY)} at most; unpacked before it is worn`,
      action: { kind: 'pack', item: step.item, into: step.into },
    });
  }

  if (crate) {
    entries.push({
      label: 'Unpack 1',
      hint: 'One item out of the crate, into a free slot',
      action: { kind: 'unpack', item: item.id, count: 1 },
    });
    entries.push({
      label: 'Unpack all',
      hint: 'Every item out of the crate, into free slots',
      action: { kind: 'unpack', item: item.id, count: item.count - 1 },
    });
  }

  return entries;
}
