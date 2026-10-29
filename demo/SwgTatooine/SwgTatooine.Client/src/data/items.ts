import { iconFile, type Catalogue } from '../assets/descriptor';

/**
 * The character's items (inventory-loot.md § 3.6, § 6): its stacks and its lost drops, as `GET /swg/inventory` answers
 * them, read on the body's `items` revision by a `RevisionReader`. <b>Never a ledger row</b>: the ledger is the operators'
 * audit trail, and a drop reaches the client as the server's notice (`notices.ts`).
 *
 * Not `inventory-*`: that name is the realm inventory's (`app/inventory-poll.ts`).
 *
 * <b>The server names every item</b> (`name`, `category`): the client owns no item table. The icon is the catalogue's,
 * found by the item's asset id (`item`).
 */

/** One stack in the grid: a template at a Mk, in a slot. */
export interface InventoryItem {
  /** The stack's raw id, as text: a 64-bit value. */
  readonly id: string;
  /** 1..slots; a slot out of the grid is not drawn. */
  readonly slot: number;
  /** The item's asset id (`mat_hide`), which names its icon. */
  readonly item: string;
  readonly name: string;
  readonly category: string;
  /** The item template's id, what a bazaar listing or a vendor sale names (bazaar.md § 3.1); 0 when the server did not say. */
  readonly template: number;
  /** 1..5; 0 for an item without Mk. */
  readonly mk: number;
  readonly count: number;
}

/** A drop, or part of one, that did not fit and was never minted; numbered per character so it is announced once. */
export interface LostNote {
  readonly n: number;
  readonly tick: number;
  readonly item: string;
  readonly name: string;
  readonly mk: number;
  readonly count: number;
}

/** The inventory as last read. */
export interface InventorySnapshot {
  /** Whether the character has a wallet yet, and so an inventory. */
  readonly open: boolean;
  readonly slots: number;
  readonly items: readonly InventoryItem[];
  readonly lost: readonly LostNote[];
}

/** The size the grid draws an icon at, in pixels: the catalogue publishes it at that size, and it is never scaled. */
export const ITEM_ICON_PX = 32;

/** The answer of `GET /swg/inventory`, checked; `null` for anything else. A malformed row is dropped, not the answer. */
export function parseInventory(answer: unknown): InventorySnapshot | null {
  if (typeof answer !== 'object' || answer === null) {
    return null;
  }

  const a = answer as Record<string, unknown>;
  if (typeof a.slots !== 'number' || !Array.isArray(a.items) || !Array.isArray(a.lost)) {
    return null;
  }

  return {
    open: a.open === true,
    slots: a.slots,
    items: a.items.filter(isItem).map((i) => ({ ...i, template: typeof i.template === 'number' ? i.template : 0 })),
    lost: a.lost.filter(isLost),
  };
}

/** The grid's cells, slot 1 first: a stack in its cell, `null` where none is; a slot outside 1..slots is not drawn. */
export function slotGrid(items: readonly InventoryItem[], slots: number): (InventoryItem | null)[] {
  const cells = new Array<InventoryItem | null>(slots).fill(null);
  for (const item of items) {
    if (Number.isInteger(item.slot) && item.slot >= 1 && item.slot <= slots && cells[item.slot - 1] === null) {
      cells[item.slot - 1] = item;
    }
  }

  return cells;
}

const NUMERALS = ['', 'I', 'II', 'III', 'IV', 'V'];

/** A Mk as its roman numeral: 1 → `I` … 5 → `V`; 0 (an item without Mk) and anything else → empty. */
export function mkNumeral(mk: number): string {
  return NUMERALS[mk] ?? '';
}

/** What hovering a stack says: `Hide · Mk II · ×7`. */
export function itemTooltip(item: InventoryItem): string {
  const mk = mkNumeral(item.mk);
  return mk === '' ? `${item.name} · ×${String(item.count)}` : `${item.name} · Mk ${mk} · ×${String(item.count)}`;
}

/** What a cell shows when the catalogue has no icon for the item (no `--assets`): the initials of its name. */
export function itemInitials(name: string): string {
  return name
    .split(/\s+/)
    .filter((w) => w.length > 0)
    .slice(0, 2)
    .map((w) => w[0].toUpperCase())
    .join('');
}

/**
 * Every icon the catalogue publishes at {@link ITEM_ICON_PX}, by asset id: what the grid finds an item's icon in.
 *
 * Not filtered by kind: the catalogue does not carry an asset's kind (its descriptor does, one fetch per asset), and the
 * grid only ever asks for the asset ids the server names, all of them kind `item` (ASSET_FORMAT.md § 5.11).
 *
 * @param catalogue The catalogue the server pinned.
 * @param iconUrl The URL of an icon file: the asset folder's.
 */
export function itemIcons(catalogue: Catalogue, iconUrl: (file: string) => string): Record<string, string> {
  const icons: Record<string, string> = {};
  for (const id of Object.keys(catalogue.icons ?? {})) {
    const file = iconFile(catalogue, id, ITEM_ICON_PX);
    if (file !== null) {
      icons[id] = iconUrl(file);
    }
  }

  return icons;
}

/**
 * The chat lines a new read brings: one per tick of lost notes that arrived since `before` — what did not fit and was never
 * minted. A drop that did fit is said by the server's notice (`notices.ts`), never read back from here.
 *
 * Nothing on the first read of a session (`before` null): its notes are history, not news. A note is new when its number is
 * past every note `before` had.
 *
 * @returns Oldest first.
 */
export function lostLines(before: InventorySnapshot | null, after: InventorySnapshot): string[] {
  if (before === null) {
    return [];
  }

  let lastLost = 0;
  for (const note of before.lost) {
    lastLost = Math.max(lastLost, note.n);
  }

  const lost = new Map<number, LostNote[]>();
  for (const note of [...after.lost].sort((a, b) => a.n - b.n)) {
    if (note.n > lastLost) {
      const notes = lost.get(note.tick);
      if (notes === undefined) {
        lost.set(note.tick, [note]);
      } else {
        notes.push(note);
      }
    }
  }

  return [...lost.entries()]
    .sort((a, b) => a[0] - b[0])
    .map(([, notes]) => `Inventory full — lost: ${itemList(notes)}`);
}

/** `Armor Core Mk II, Hide Mk II ×3`: the same item at the same Mk once, its counts summed, in the order first seen. */
export function itemList(entries: readonly { item: string; name: string; mk: number; count: number }[]): string {
  const summed = new Map<string, { name: string; mk: number; count: number }>();
  for (const e of entries) {
    const key = `${e.item}:${String(e.mk)}`;
    const s = summed.get(key);
    if (s === undefined) {
      summed.set(key, { name: e.name, mk: e.mk, count: e.count });
    } else {
      s.count += e.count;
    }
  }

  return [...summed.values()]
    .map((s) => {
      const mk = mkNumeral(s.mk);
      return `${s.name}${mk === '' ? '' : ` Mk ${mk}`}${s.count > 1 ? ` ×${String(s.count)}` : ''}`;
    })
    .join(', ');
}

function isItem(value: unknown): value is InventoryItem {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const i = value as Record<string, unknown>;
  return (
    typeof i.id === 'string' &&
    typeof i.slot === 'number' &&
    typeof i.item === 'string' &&
    typeof i.name === 'string' &&
    typeof i.category === 'string' &&
    typeof i.mk === 'number' &&
    typeof i.count === 'number'
  );
}

function isLost(value: unknown): value is LostNote {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const l = value as Record<string, unknown>;
  return (
    typeof l.n === 'number' &&
    typeof l.tick === 'number' &&
    typeof l.item === 'string' &&
    typeof l.name === 'string' &&
    typeof l.mk === 'number' &&
    typeof l.count === 'number'
  );
}
