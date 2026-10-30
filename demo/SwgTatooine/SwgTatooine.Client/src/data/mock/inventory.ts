import type { InventoryItem, InventorySnapshot, LostNote } from '../items';
import type { PlayerNotice } from '../notices';
import type { RevisionFetch } from '../revision-reader';
import { Rng } from './rng';

/**
 * The mock's answer to `GET /swg/inventory` (inventory-loot.md § 3.6), so the inventory window, its icons and the loot lines
 * can be exercised without the server: one character whose inventory a drop fills every {@link MOCK_DROP_MS}, placed by the
 * server's rules — the same item at the same Mk stacks to 99, then opens the lowest free slot; what does not fit is lost
 * and noted. Its `items` revision moves with every drop, as the body's owner field does, and with every move the mock
 * bazaar makes (`data/mock/market.ts`): units taken into a listing or sold to the vendor, units collected.
 *
 * Not the server's loot tables (`LootTables.cs`): a drop here is a few units of one or two families, now and then a core or
 * an add-on, at a Mk I-IV, enough to see every badge colour and a loss.
 */

/** How often the mock's character loots something, milliseconds. */
export const MOCK_DROP_MS = 8000;

/** One lootable item as the server's catalogue holds it (`ItemCatalog.cs`): its template id, asset id, name and category. */
export interface MockItemDef {
  readonly template: number;
  readonly item: string;
  readonly name: string;
  readonly category: string;
}

/** What the mock drops: the 17 lootable items (inventory-loot.md § 3.1), as the server names and numbers them. */
export const MOCK_MATERIALS: readonly MockItemDef[] = [
  { template: 3, item: 'mat_metal', name: 'Metal', category: 'material' },
  { template: 4, item: 'mat_crystal', name: 'Crystal', category: 'material' },
  { template: 5, item: 'mat_gas', name: 'Gas', category: 'material' },
  { template: 6, item: 'mat_chemical', name: 'Chemical', category: 'material' },
  { template: 7, item: 'mat_fiber', name: 'Fiber', category: 'material' },
  { template: 8, item: 'mat_hide', name: 'Hide', category: 'material' },
  { template: 9, item: 'mat_bone', name: 'Bone', category: 'material' },
  { template: 10, item: 'mat_gland', name: 'Gland', category: 'material' },
  { template: 11, item: 'mat_venom', name: 'Venom', category: 'material' },
];

export const MOCK_PARTS: readonly MockItemDef[] = [
  { template: 12, item: 'core_ranged', name: 'Ranged Weapon Core', category: 'core' },
  { template: 13, item: 'core_melee', name: 'Melee Weapon Core', category: 'core' },
  { template: 14, item: 'core_armor', name: 'Armor Core', category: 'core' },
  { template: 15, item: 'core_propulsion', name: 'Propulsion Core', category: 'core' },
  { template: 16, item: 'addon_weapon', name: 'Weapon Enhancement', category: 'addon' },
  { template: 17, item: 'addon_armor', name: 'Armor Enhancement', category: 'addon' },
  { template: 18, item: 'addon_tuning', name: 'Tuning Kit', category: 'addon' },
  { template: 19, item: 'addon_additive', name: 'Additive', category: 'addon' },
];

/** The lootable item of template `template`, or `undefined`. */
export function mockItem(template: number): MockItemDef | undefined {
  return MOCK_MATERIALS.find((m) => m.template === template) ?? MOCK_PARTS.find((m) => m.template === template);
}

const STACK_MAX = 99;
const LOST = 10;
const UNITS_PER_DROP = 4;
/** One drop in four carries a core or an add-on: far above the server's rates, so the mock shows them. */
const PART_CHANCE = 0.25;

interface Stack {
  id: string;
  slot: number;
  item: string;
  name: string;
  category: string;
  template: number;
  mk: number;
  count: number;
}

export class MockInventory {
  private readonly rng: Rng;
  private readonly slots: number;
  private readonly stacks: Stack[] = [];
  /** The notices of the moves made since the last {@link takeNotices}, as the server tells them (`PlayerNotice`). */
  private outbox: PlayerNotice[] = [];
  private readonly lost: LostNote[] = [];
  private startMs = Number.NaN;
  private drops = 0;
  /** Moves made by the mock bazaar: each moves the revision as a drop does. */
  private moves = 0;
  private lostN = 0;
  private nextStack = 1;

  /**
   * @param seed The mock world's seed: the same seed loots the same things.
   * @param slots The grid's size; the server's is 30.
   */
  constructor(seed: number, slots = 30) {
    this.rng = new Rng(seed ^ 0x17e3);
    this.slots = slots;
  }

  /**
   * The `items` revision at `nowMs`, dropping whatever came due since the last call: 1 on the first call, one more per drop
   * and per bazaar move.
   *
   * @param nowMs A monotonic clock in milliseconds; its first value starts the clock.
   */
  revision(nowMs: number): number {
    if (Number.isNaN(this.startMs)) {
      this.startMs = nowMs;
    }

    while (nowMs - this.startMs >= (this.drops + 1) * MOCK_DROP_MS) {
      this.drop();
    }

    return this.drops + this.moves + 1;
  }

  /** The inventory as `GET /swg/inventory` answers it. */
  snapshot(): InventorySnapshot {
    const items: InventoryItem[] = this.stacks.map((s) => ({ ...s }));
    items.sort((a, b) => a.slot - b.slot);
    return { open: true, slots: this.slots, items, lost: [...this.lost] };
  }

  /** A `fetch` for the inventory reader: `swg/inventory` answered with {@link snapshot}, anything else 404. */
  readonly fetch: RevisionFetch = (url) => {
    const answer = url === 'swg/inventory' ? this.snapshot() : null;
    return Promise.resolve({
      ok: answer !== null,
      status: answer === null ? 404 : 200,
      json: () => Promise.resolve(answer),
    });
  };

  /** Units of (`template`, `mk`) held, every stack summed. */
  held(template: number, mk: number): number {
    let n = 0;
    for (const s of this.stacks) {
      if (s.template === template && s.mk === mk) {
        n += s.count;
      }
    }

    return n;
  }

  /**
   * Takes `count` units of (`template`, `mk`) out — into a listing, or to the vendor — the highest slot first, as the server's
   * `ItemTransfer` debit does; false, and nothing taken, when fewer are held.
   *
   * @param kind The rows' kind: `list` or `vendor`.
   */
  take(template: number, mk: number, count: number, kind: string, tick: number, ref: string): boolean {
    if (count < 1 || this.held(template, mk) < count) {
      return false;
    }

    let left = count;
    const own = this.stacks.filter((s) => s.template === template && s.mk === mk).sort((a, b) => b.slot - a.slot);
    for (const stack of own) {
      const out = Math.min(left, stack.count);
      stack.count -= out;
      left -= out;
      this.row(tick, stack.template, stack.name, mk, out, ref, kind, false);
      if (stack.count === 0) {
        this.stacks.splice(this.stacks.indexOf(stack), 1);
      }

      if (left === 0) {
        break;
      }
    }

    this.moves++;
    return true;
  }

  /** Whether `count` units of (`template`, `mk`) fit: the key's stacks topped up to 99, then the free slots. */
  roomFor(template: number, mk: number, count: number): boolean {
    let room = (this.slots - this.stacks.length) * STACK_MAX;
    for (const s of this.stacks) {
      if (s.template === template && s.mk === mk) {
        room += STACK_MAX - s.count;
      }
    }

    return room >= count;
  }

  /**
   * Puts `count` units in, all or nothing (the server's `RoomFor`): a bazaar collect, `kind` `buy` or `return`. False, and
   * nothing placed, when they do not fit.
   */
  give(def: MockItemDef, mk: number, count: number, kind: string, tick: number, ref: string): boolean {
    if (!this.roomFor(def.template, mk, count)) {
      return false;
    }

    this.place(def, mk, count, tick, ref, kind);
    this.moves++;
    return true;
  }

  private drop(): void {
    this.drops++;
    const tick = this.drops * 20;
    const ref = String(1_000_000 + this.drops);
    const mk = 1 + Math.min(3, Math.floor(this.rng.next() * this.rng.next() * 5));
    if (this.rng.next() < PART_CHANCE) {
      this.place(MOCK_PARTS[this.rng.int(0, MOCK_PARTS.length)], mk, 1, tick, ref, 'kill');
    }

    const first = MOCK_MATERIALS[this.rng.int(0, MOCK_MATERIALS.length)];
    const second = MOCK_MATERIALS[this.rng.int(0, MOCK_MATERIALS.length)];
    const split = this.rng.int(1, UNITS_PER_DROP + 1);
    this.place(first, mk, split, tick, ref, 'kill');
    if (split < UNITS_PER_DROP) {
      this.place(second, mk, UNITS_PER_DROP - split, tick, ref, 'kill');
    }
  }

  /** The server's placement (`ItemTransfer`, into a character): fill the key's stacks, lowest slot first, then open one. */
  private place(def: MockItemDef, mk: number, count: number, tick: number, ref: string, kind: string): void {
    const { item, name, category, template } = def;
    let left = count;
    const own = this.stacks.filter((s) => s.item === item && s.mk === mk).sort((a, b) => a.slot - b.slot);
    for (const stack of own) {
      const put = Math.min(left, STACK_MAX - stack.count);
      if (put > 0) {
        stack.count += put;
        left -= put;
        this.row(tick, template, name, mk, put, ref, kind, true);
      }
    }

    while (left > 0) {
      const slot = this.freeSlot();
      if (slot === 0) {
        this.lost.unshift({ n: ++this.lostN, tick, item, name, mk, count: left });
        this.lost.length = Math.min(this.lost.length, LOST);
        return;
      }

      const put = Math.min(left, STACK_MAX);
      this.stacks.push({ id: String(this.nextStack++), slot, item, name, category, template, mk, count: put });
      left -= put;
      this.row(tick, template, name, mk, put, ref, kind, true);
    }
  }

  private row(
    tick: number,
    template: number,
    name: string,
    mk: number,
    count: number,
    ref: string,
    kind: string,
    incoming: boolean,
  ): void {
    this.outbox.push({ tick, items: true, incoming, template, mk, count, amount: 0, ref, kind, other: null, name });
  }

  /** The notices of the moves made since the last call, oldest first: what the server would have told the session. */
  takeNotices(): PlayerNotice[] {
    const taken = this.outbox;
    this.outbox = [];
    return taken;
  }

  private freeSlot(): number {
    for (let slot = 1; slot <= this.slots; slot++) {
      if (!this.stacks.some((s) => s.slot === slot)) {
        return slot;
      }
    }

    return 0;
  }
}
