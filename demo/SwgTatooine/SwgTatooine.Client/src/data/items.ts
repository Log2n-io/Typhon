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
  /** An item with stats (crafting-build.md § 2.1): its lines by category, its condition. Absent for units. */
  readonly stats?: ItemStats;
  /** What it packs with into a crate (crafting-catalog.md § 11.2): equal for identical pristine items; absent for one that does not pack. */
  readonly packKey?: string;
}

/** What `/swg/inventory` says of an item with stats: a weapon's or an armour piece's lines, and its wear. */
export interface ItemStats {
  /** A weapon's damage per hit, delay (ms) and reach (dm). */
  readonly damage?: number;
  readonly delayMs?: number;
  readonly rangeDm?: number;
  /** An armour piece's protections, per mille. */
  readonly kinetic?: number;
  readonly energy?: number;
  /** A crafted part's grade, per mille. */
  readonly grade?: number;
  /** A potion's potency and duration (ms). */
  readonly potency?: number;
  readonly durationMs?: number;
  /** The equip slot it is worn in, 101-107; 0 for a part or a potion. */
  readonly wears?: number;
  readonly condition: number;
  readonly maxCondition: number;
  readonly lifeMilli: number;
  readonly repairs: number;
  /** At 0 condition with a repair left (crafting-catalog.md § 9.2): worn, but it does nothing until repaired. Absent from older servers. */
  readonly broken?: boolean;
  /** The crafter's raw id, `'0'` for standard issue and loot. */
  readonly crafter: string;
}

/** Repairs an item takes at most (§ 9.2), each to a max × 0.75. */
export const MAX_REPAIRS = 2;

/** The kit a worn item is repaired with: a weapon's (62) or an armour piece's (63); `undefined` for anything that does not wear. */
export function repairKitOf(item: InventoryItem): number | undefined {
  const wears = item.stats?.wears;
  return wears === undefined || wears === 0 ? undefined : wears === 101 ? 62 : 63;
}

/** The equip slots (crafting-build.md § 2.2): 101 the weapon, then the six armour pieces; what each is called on the equipment strip. */
export const EQUIP_SLOTS: readonly { readonly slot: number; readonly label: string }[] = [
  { slot: 101, label: 'Weapon' },
  { slot: 102, label: 'Head' },
  { slot: 103, label: 'Torso' },
  { slot: 104, label: 'Arms' },
  { slot: 105, label: 'Legs' },
  { slot: 106, label: 'Hands' },
  { slot: 107, label: 'Feet' },
];

/** Whether a slot is one of the equip slots, out of the grid. */
export function isEquipSlot(slot: number): boolean {
  return slot >= 101 && slot <= 107;
}

/** What is worn, by equip slot. */
export function equipped(items: readonly InventoryItem[]): ReadonlyMap<number, InventoryItem> {
  const worn = new Map<number, InventoryItem>();
  for (const item of items) {
    if (isEquipSlot(item.slot)) {
      worn.set(item.slot, item);
    }
  }

  return worn;
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
    items: a.items.filter(isItem).map((i) => {
      const { stats, packKey, ...rest } = i;
      const base = { ...rest, template: typeof i.template === 'number' ? i.template : 0 };
      const item = typeof packKey === 'string' ? { ...base, packKey } : base;
      return isStats(stats) ? { ...item, stats } : item;
    }),
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

/**
 * What hovering a stack says: `Hide · Mk II · ×7`. An item with stats says them instead of a count: `DL-44 Blaster · Mk I · 81 dmg · 1.02 s · 30 m ·
 * condition 100 %`, or an armour piece's protections.
 */
export function itemTooltip(item: InventoryItem): string {
  const mk = mkNumeral(item.mk);
  const head = mk === '' ? item.name : `${item.name} · Mk ${mk}`;
  const s = item.stats;
  if (s === undefined) {
    return `${head} · ×${String(item.count)}`;
  }

  const parts = [head];
  if (s.damage !== undefined && s.delayMs !== undefined && s.rangeDm !== undefined) {
    parts.push(`${String(s.damage)} dmg`, `${(s.delayMs / 1000).toFixed(2)} s`, `${String(s.rangeDm / 10)} m`);
  }

  if (s.kinetic !== undefined && s.energy !== undefined) {
    parts.push(`kinetic ${(s.kinetic / 10).toFixed(0)} %`, `energy ${(s.energy / 10).toFixed(0)} %`);
  }

  const condition = s.maxCondition > 0 ? Math.round((100 * s.condition) / s.maxCondition) : 0;
  parts.push(s.broken === true ? 'broken' : `condition ${String(condition)} %`);
  if (s.repairs > 0) {
    parts.push(`repaired ${String(s.repairs)}/${String(MAX_REPAIRS)}`);
  }

  return parts.join(' · ');
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

function isStats(value: unknown): value is ItemStats {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const s = value as Record<string, unknown>;
  return (
    typeof s.condition === 'number' &&
    typeof s.maxCondition === 'number' &&
    typeof s.lifeMilli === 'number' &&
    typeof s.repairs === 'number' &&
    typeof s.crafter === 'string'
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
