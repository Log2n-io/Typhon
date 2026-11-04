import type { ContextMenuItem } from '../state/ui-store';
import { CRATE_CAPACITY, packSteps } from './crates';
import { kitMenuItems } from './installations';
import { isUnits } from './inventory-grid';
import type { InventoryItem } from './items';

/**
 * An item's context menu (client.md C42, C43): everything the character can do with it from its slot — a kit placed here, an item with stats worn, an item
 * packed into a crate with the identical ones, a crate unpacked, units split off into a free slot, and anything thrown away. Entries that need a count
 * open the window's count prompt; throwing away always does, as its confirmation. Nothing for what is worn: it is the character window's.
 *
 * @param item The item held on.
 * @param items The whole inventory, which says what it packs with.
 * @param freeSlots How many grid slots are free: a split or an unpack needs one.
 */
export function itemMenuItems(item: InventoryItem, items: readonly InventoryItem[], freeSlots = 1): ContextMenuItem[] {
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

  if (crate && freeSlots > 0) {
    entries.push({
      label: 'Unpack 1',
      hint: 'One item out of the crate, into a free slot',
      action: { kind: 'unpack', item: item.id, count: 1 },
    });
    if (item.count > 2) {
      entries.push({
        label: 'Unpack…',
        hint: 'Some of the items out of the crate, each into a free slot',
        action: { kind: 'itemPrompt', item: item.id, mode: 'unpack' },
      });
    }

    entries.push({
      label: 'Unpack all',
      hint: 'Every item out of the crate, into free slots',
      action: { kind: 'unpack', item: item.id, count: item.count - 1 },
    });
  }

  if (isUnits(item) && item.count > 1 && freeSlots > 0) {
    entries.push({
      label: 'Split…',
      hint: 'Some of the stack into the first free slot; or drag it, Shift held, onto the slot you want',
      action: { kind: 'itemPrompt', item: item.id, mode: 'split' },
    });
  }

  entries.push({
    label: 'Throw away…',
    hint: 'Destroyed for good: asks how many, and to confirm',
    action: { kind: 'itemPrompt', item: item.id, mode: 'discard' },
  });
  return entries;
}
