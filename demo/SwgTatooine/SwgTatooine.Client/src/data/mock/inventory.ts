import type { InventoryItem, InventoryRow, InventorySnapshot, LostNote } from '../items';
import type { RevisionFetch } from '../revision-reader';
import { Rng } from './rng';

/**
 * The mock's answer to `GET /swg/inventory` (inventory-loot.md § 3.6), so the inventory window, its icons and the loot lines
 * can be exercised without the server: one character whose inventory a drop fills every {@link MOCK_DROP_MS}, placed by the
 * server's rules — the same item at the same Mk stacks to 99, then opens the lowest free slot; what does not fit is lost
 * and noted. Its `items` revision moves with every drop, as the body's owner field does.
 *
 * Not the server's loot tables (`LootTables.cs`): a drop here is a few units of one or two families, now and then a core or
 * an add-on, at a Mk I-IV, enough to see every badge colour and a loss.
 */

/** How often the mock's character loots something, milliseconds. */
export const MOCK_DROP_MS = 8000;

/** What the mock drops: the 17 lootable items (inventory-loot.md § 3.1), as the server names them. */
const MATERIALS: readonly (readonly [string, string])[] = [
  ['mat_metal', 'Metal'],
  ['mat_crystal', 'Crystal'],
  ['mat_gas', 'Gas'],
  ['mat_chemical', 'Chemical'],
  ['mat_fiber', 'Fiber'],
  ['mat_hide', 'Hide'],
  ['mat_bone', 'Bone'],
  ['mat_gland', 'Gland'],
  ['mat_venom', 'Venom'],
];

const PARTS: readonly (readonly [string, string, string])[] = [
  ['core_ranged', 'Ranged Weapon Core', 'core'],
  ['core_melee', 'Melee Weapon Core', 'core'],
  ['core_armor', 'Armor Core', 'core'],
  ['core_propulsion', 'Propulsion Core', 'core'],
  ['addon_weapon', 'Weapon Enhancement', 'addon'],
  ['addon_armor', 'Armour Enhancement', 'addon'],
  ['addon_tuning', 'Tuning Kit', 'addon'],
  ['addon_additive', 'Additive', 'addon'],
];

const STACK_MAX = 99;
const RECENT = 20;
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
  mk: number;
  count: number;
}

export class MockInventory {
  private readonly rng: Rng;
  private readonly slots: number;
  private readonly stacks: Stack[] = [];
  /** Newest first, as the server lists them. */
  private readonly rows: InventoryRow[] = [];
  private readonly lost: LostNote[] = [];
  private startMs = Number.NaN;
  private drops = 0;
  private seq = 0;
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
   * The `items` revision at `nowMs`, dropping whatever came due since the last call: 1 on the first call, one more per drop.
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

    return this.drops + 1;
  }

  /** The inventory as `GET /swg/inventory` answers it. */
  snapshot(): InventorySnapshot {
    const items: InventoryItem[] = this.stacks.map((s) => ({ ...s }));
    items.sort((a, b) => a.slot - b.slot);
    return { open: true, slots: this.slots, items, recent: [...this.rows], lost: [...this.lost] };
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

  private drop(): void {
    this.drops++;
    const tick = this.drops * 20;
    const ref = String(1_000_000 + this.drops);
    const mk = 1 + Math.min(3, Math.floor(this.rng.next() * this.rng.next() * 5));
    if (this.rng.next() < PART_CHANCE) {
      const [item, name, category] = PARTS[this.rng.int(0, PARTS.length)];
      this.place(item, name, category, mk, 1, tick, ref);
    }

    const first = MATERIALS[this.rng.int(0, MATERIALS.length)];
    const second = MATERIALS[this.rng.int(0, MATERIALS.length)];
    const split = this.rng.int(1, UNITS_PER_DROP + 1);
    this.place(first[0], first[1], 'material', mk, split, tick, ref);
    if (split < UNITS_PER_DROP) {
      this.place(second[0], second[1], 'material', mk, UNITS_PER_DROP - split, tick, ref);
    }
  }

  /** The server's placement (`ItemTransfer`, World → character): fill the key's stacks, lowest slot first, then open one. */
  private place(
    item: string,
    name: string,
    category: string,
    mk: number,
    count: number,
    tick: number,
    ref: string,
  ): void {
    let left = count;
    const own = this.stacks.filter((s) => s.item === item && s.mk === mk).sort((a, b) => a.slot - b.slot);
    for (const stack of own) {
      const put = Math.min(left, STACK_MAX - stack.count);
      if (put > 0) {
        stack.count += put;
        left -= put;
        this.row(tick, item, name, mk, put, ref);
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
      this.stacks.push({ id: String(this.nextStack++), slot, item, name, category, mk, count: put });
      left -= put;
      this.row(tick, item, name, mk, put, ref);
    }
  }

  private row(tick: number, item: string, name: string, mk: number, count: number, ref: string): void {
    this.rows.unshift({ seq: String(++this.seq), tick, kind: 'kill', item, name, mk, count, ref });
    this.rows.length = Math.min(this.rows.length, RECENT);
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
