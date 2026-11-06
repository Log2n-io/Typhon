import type { InventoryItem } from './items';

/**
 * Potions on the client (crafting-catalog.md § 5.4; Loïc, 2026-11-05): the stim, drunk from the action bar's slot 5 or its menu. The server heals and keeps
 * the cooldown; the client picks which stim and shows the wait.
 */

/** The stim's asset: its icon on the bar, and what names it in the grid. */
export const STIM_ITEM = 'potion_stim';

/** The server's cooldown between two stims (`TatooineData.StimCooldownSec`), ms: the slot's sweep, started by the heal itself. */
export const STIM_COOLDOWN_MS = 15_000;

/** The stims in the grid, crates included. */
export function stimsHeld(items: readonly InventoryItem[]): InventoryItem[] {
  return items.filter((i) => i.item === STIM_ITEM && i.slot <= 100 && (i.stats?.condition ?? 0) > 0);
}

/** The doses the grid holds: each stim's left, a crate's count of full ones. */
export function stimDoses(items: readonly InventoryItem[]): number {
  return stimsHeld(items).reduce((n, i) => n + i.count * (i.stats?.condition ?? 0), 0);
}

/**
 * The stim the slot drinks: the one with the fewest doses left — a part-drunk one is used up before a full one is opened — and a crate last, which the
 * server unpacks one from, so only with a slot free (`freeSlots`). `null` with none.
 */
export function stimToDrink(items: readonly InventoryItem[], freeSlots = 1): string | null {
  let best: InventoryItem | null = null;
  for (const i of stimsHeld(items)) {
    const crate = i.count > 1;
    if (crate && freeSlots < 1) {
      continue;
    }

    if (
      best === null ||
      (best.count > 1 && !crate) ||
      (best.count > 1 === crate && (i.stats?.condition ?? 0) < (best.stats?.condition ?? 0))
    ) {
      best = i;
    }
  }

  return best?.id ?? null;
}

/** What is left of the cooldown, 0..1 of it, at `nowMs`: 0 when a stim may be drunk. */
export function cooldownLeft(readyAtMs: number, nowMs: number): number {
  return readyAtMs <= nowMs ? 0 : Math.min(1, (readyAtMs - nowMs) / STIM_COOLDOWN_MS);
}
