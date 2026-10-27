import { iconFile, type Catalogue } from '../assets/descriptor';

/**
 * The character's items (inventory-loot.md § 3.6, § 6): its stacks, its recent item rows and its lost drops, as
 * `GET /swg/inventory` answers them, read on the body's `items` revision by a `RevisionReader`.
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

/**
 * One row of the item ledger touching this character: World → it for the drops, and the bazaar's moves (`list` into a
 * listing, `buy`/`return` out of one, `vendor` to World).
 */
export interface InventoryRow {
  /** The ledger's sequence number, as text: a 64-bit value. */
  readonly seq: string;
  readonly tick: number;
  /** `kill`, `lair`, `mission`, `buy`, `return` are announced; `list`, `vendor` and later kinds are read and not. */
  readonly kind: string;
  readonly item: string;
  readonly name: string;
  readonly mk: number;
  readonly count: number;
  /** What the drop came from (the victim, the lair, the mission), as text: the rows of one drop share it and the tick. */
  readonly ref: string;
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
  /** The most recent first. */
  readonly recent: readonly InventoryRow[];
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
  if (typeof a.slots !== 'number' || !Array.isArray(a.items) || !Array.isArray(a.recent) || !Array.isArray(a.lost)) {
    return null;
  }

  return {
    open: a.open === true,
    slots: a.slots,
    items: a.items.filter(isItem).map((i) => ({ ...i, template: typeof i.template === 'number' ? i.template : 0 })),
    recent: a.recent.filter(isRow),
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

/** How a drop's line starts, by its rows' kind; a kind not here is not a drop and is not announced. */
const DROP_PREFIX: Readonly<Partial<Record<string, string>>> = {
  kill: 'Loot',
  lair: 'Lair cache',
  mission: 'Mission cache',
  // Out of a bazaar listing into the inventory (bazaar.md § 6): bought, or one's own listing back.
  buy: 'Collected',
  return: 'Collected',
};

/**
 * The chat lines a new read brings: one per drop that arrived since `before`, one per tick of lost notes.
 *
 * Nothing on the first read of a session (`before` null): its rows are history, not news. A row is new when its sequence is
 * past every row `before` had, so a row read twice is said once; the rows of one drop (one `ref`, one tick) are one line,
 * the same item at the same Mk summed (a drop that filled one stack and opened another is two rows). A lost note is new
 * when its number is past every note `before` had.
 *
 * @returns Oldest first.
 */
export function lootLines(before: InventorySnapshot | null, after: InventorySnapshot): string[] {
  if (before === null) {
    return [];
  }

  let lastSeq = -1n;
  for (const row of before.recent) {
    const seq = seqOf(row.seq);
    if (seq > lastSeq) {
      lastSeq = seq;
    }
  }

  const drops = new Map<string, { tick: number; first: bigint; prefix: string; rows: InventoryRow[] }>();
  for (const row of after.recent) {
    const seq = seqOf(row.seq);
    const prefix = DROP_PREFIX[row.kind];
    if (seq <= lastSeq || prefix === undefined) {
      continue;
    }

    const key = `${row.kind}:${row.ref}:${String(row.tick)}`;
    const drop = drops.get(key);
    if (drop === undefined) {
      drops.set(key, { tick: row.tick, first: seq, prefix, rows: [row] });
    } else {
      drop.rows.push(row);
      if (seq < drop.first) {
        drop.first = seq;
      }
    }
  }

  // Within a tick, the drops first (in sequence order), then what did not fit: what was kept is said before what was lost.
  const lines: { tick: number; rank: number; order: bigint; text: string }[] = [];
  for (const drop of drops.values()) {
    // In the order they were placed: the core or add-on first, then the materials.
    drop.rows.sort((a, b) => compareSeq(a.seq, b.seq));
    lines.push({
      tick: drop.tick,
      rank: 0,
      order: drop.first,
      text: `${drop.prefix}: ${itemList(drop.rows)}`,
    });
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

  for (const [tick, notes] of lost) {
    lines.push({ tick, rank: 1, order: BigInt(notes[0].n), text: `Inventory full — lost: ${itemList(notes)}` });
  }

  lines.sort((a, b) => a.tick - b.tick || a.rank - b.rank || (a.order < b.order ? -1 : a.order > b.order ? 1 : 0));
  return lines.map((l) => l.text);
}

/** `Armor Core Mk II, Hide Mk II ×3`: the same item at the same Mk once, its counts summed, in the order first seen. */
function itemList(entries: readonly { item: string; name: string; mk: number; count: number }[]): string {
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

/** A sequence number as text, read as the 64-bit value it is; one that is not a number reads as -1 and is never new. */
function seqOf(text: string): bigint {
  return /^\d+$/.test(text) ? BigInt(text) : -1n;
}

function compareSeq(a: string, b: string): number {
  const x = seqOf(a);
  const y = seqOf(b);
  return x < y ? -1 : x > y ? 1 : 0;
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

function isRow(value: unknown): value is InventoryRow {
  if (typeof value !== 'object' || value === null) {
    return false;
  }

  const r = value as Record<string, unknown>;
  return (
    typeof r.seq === 'string' &&
    typeof r.tick === 'number' &&
    typeof r.kind === 'string' &&
    typeof r.item === 'string' &&
    typeof r.name === 'string' &&
    typeof r.mk === 'number' &&
    typeof r.count === 'number' &&
    typeof r.ref === 'string'
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
