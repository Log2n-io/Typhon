import { CRATE_CAPACITY } from './crates';
import { EQUIP_SLOTS, MAX_REPAIRS, mkNumeral, STACK_MAX, type InventoryItem } from './items';

/**
 * The inventory window's own operations (inventory-loot.md § 10, client.md C43): what a stack dropped on a slot does, the count prompts its menu opens, and
 * the preview panel's lines. Plain data; the server decides, and its refusal is said on the window's notice row.
 */

/** What dropping `item` on a slot sends: a move (to a free slot, a merge, a swap) or a pack into an identical crate. */
export type GridDrop =
  | { readonly kind: 'move'; readonly item: string; readonly slot: number; readonly count: number }
  | { readonly kind: 'pack'; readonly item: string; readonly into: string };

/** Units, as opposed to an item with stats or a crate: what splits and merges. */
export function isUnits(item: InventoryItem): boolean {
  return item.stats === undefined;
}

/** Whether two grid entries are the same units: one item, one Mk, both units. */
export function sameUnits(a: InventoryItem, b: InventoryItem): boolean {
  return isUnits(a) && isUnits(b) && a.item === b.item && a.mk === b.mk;
}

/**
 * The drop of the whole of `item` on grid slot `slot`, which holds `there` (`null`: free). On itself, nothing. On the same units, a merge of as much as the
 * stack there holds; on an identical item or crate with room, a pack; on anything else, the two swap.
 */
export function dropOnSlot(item: InventoryItem, slot: number, there: InventoryItem | null): GridDrop | null {
  if (item.slot === slot || item.slot > 100) {
    return null;
  }

  if (there === null) {
    return { kind: 'move', item: item.id, slot, count: item.count };
  }

  if (sameUnits(item, there)) {
    return there.count >= STACK_MAX ? null : { kind: 'move', item: item.id, slot, count: item.count };
  }

  if (item.packKey !== undefined && item.packKey === there.packKey && item.count + there.count <= CRATE_CAPACITY) {
    return { kind: 'pack', item: item.id, into: there.id };
  }

  return { kind: 'move', item: item.id, slot, count: item.count };
}

/** What a count prompt asks: how many to split off, to unpack, or to throw away. */
export type PromptMode = 'split' | 'unpack' | 'discard';

/** A count prompt, open on one item; `slot` is where a split lands (0: the lowest free slot). */
export interface ItemPrompt {
  readonly mode: PromptMode;
  readonly item: string;
  readonly slot: number;
}

/**
 * The counts a prompt allows and starts at; `fixed` when there is nothing to choose — an item or a crate thrown away goes whole. An unpack takes no more
 * than the free slots hold. A range whose `max` is under its `min` — a stack down to one under an open split, a crate with no room — has nothing to ask.
 */
export function promptRange(
  mode: PromptMode,
  item: InventoryItem,
  free = Number.POSITIVE_INFINITY,
): { min: number; max: number; start: number; fixed: boolean } {
  switch (mode) {
    case 'split':
      return { min: 1, max: item.count - 1, start: Math.max(1, Math.floor(item.count / 2)), fixed: false };
    case 'unpack':
      return { min: 1, max: Math.min(item.count - 1, free), start: 1, fixed: false };
    case 'discard':
      return isUnits(item)
        ? { min: 1, max: item.count, start: item.count, fixed: item.count === 1 }
        : { min: item.count, max: item.count, start: item.count, fixed: true };
  }
}

/** The count typed, clamped into the prompt's range; a field cleared or not a number is the range's start. */
export function promptCount(
  typed: string,
  range: { readonly min: number; readonly max: number; readonly start: number },
): number {
  const n = Math.floor(Number(typed));
  return Number.isFinite(n) && typed.trim() !== '' ? Math.min(range.max, Math.max(range.min, n)) : range.start;
}

/** The free grid slots, lowest first. */
export function freeSlots(items: readonly InventoryItem[], slots: number): number[] {
  const used = new Set(items.map((i) => i.slot));
  const free: number[] = [];
  for (let s = 1; s <= slots; s++) {
    if (!used.has(s)) {
      free.push(s);
    }
  }

  return free;
}

/** One line of the preview's stats: a label and its value. */
export interface PreviewLine {
  readonly label: string;
  readonly value: string;
}

const CATEGORY_NAMES: Readonly<Record<string, string>> = {
  material: 'Material',
  core: 'Core',
  addon: 'Add-on',
  part: 'Part',
  weapon: 'Weapon',
  armor: 'Armour',
  potion: 'Potion',
  kit: 'Kit',
  structure: 'Structure kit',
  container: 'Container',
};

/** Everything the preview says of an item, in the order it says it: what it is, then its stats, then its wear and its maker. */
export function previewLines(item: InventoryItem): PreviewLine[] {
  const lines: PreviewLine[] = [];
  const mk = mkNumeral(item.mk);
  lines.push({ label: 'Kind', value: CATEGORY_NAMES[item.category] ?? item.category });
  if (mk !== '') {
    lines.push({ label: 'Mk', value: mk });
  }

  const s = item.stats;
  if (s === undefined) {
    lines.push({ label: 'Units', value: item.count.toLocaleString('en-US') });
    lines.push({
      label: 'Stack',
      value: `${item.count.toLocaleString('en-US')} / ${STACK_MAX.toLocaleString('en-US')}`,
    });
    return lines;
  }

  if (item.count > 1) {
    lines.push({ label: 'Crate', value: `${String(item.count)} / ${String(CRATE_CAPACITY)} identical` });
  }

  if (s.damage !== undefined) {
    lines.push({ label: 'Damage', value: `${String(s.damage)} per hit` });
  }

  if (s.delayMs !== undefined) {
    lines.push({ label: 'Delay', value: `${(s.delayMs / 1000).toFixed(2)} s` });
  }

  if (s.rangeDm !== undefined) {
    lines.push({ label: 'Range', value: `${String(s.rangeDm / 10)} m` });
  }

  if (s.kinetic !== undefined) {
    lines.push({ label: 'Kinetic', value: `${(s.kinetic / 10).toFixed(1)} %` });
  }

  if (s.energy !== undefined) {
    lines.push({ label: 'Energy', value: `${(s.energy / 10).toFixed(1)} %` });
  }

  if (s.grade !== undefined) {
    lines.push({ label: 'Grade', value: `${(s.grade / 10).toFixed(1)} %` });
  }

  if (s.potency !== undefined) {
    lines.push({ label: 'Potency', value: String(s.potency) });
  }

  if (s.durationMs !== undefined) {
    lines.push({ label: 'Duration', value: `${String(Math.round(s.durationMs / 1000))} s` });
  }

  if (s.rate !== undefined) {
    lines.push({ label: 'Output', value: `${String(s.rate)} units an hour` });
  }

  if (s.hopper !== undefined) {
    lines.push({ label: 'Hopper', value: `${String(s.hopper)} units` });
  }

  const worn = EQUIP_SLOTS.find((e) => e.slot === s.wears);
  if (worn !== undefined) {
    lines.push({ label: 'Worn on', value: worn.label });
  }

  if (s.maxCondition > 0) {
    const pct = Math.round((100 * s.condition) / s.maxCondition);
    lines.push({
      label: 'Condition',
      // A share, as the tooltip says it: the raw units are the server's (combat-milliseconds, damage points), meaningless to a player.
      value: s.broken === true ? 'broken' : `${String(pct)} %`,
    });
  }

  lines.push({ label: 'Repairs', value: `${String(s.repairs)} / ${String(MAX_REPAIRS)}` });
  lines.push({ label: 'Made by', value: s.crafter === '0' ? 'standard issue or loot' : 'a crafter' });
  return lines;
}

/**
 * The preview's spin (client.md C43): the model turns on its own at {@link IDLE_SPIN} rad/s; dragged, it follows the pointer, and let go, it keeps the
 * speed it was flung at and eases back to the idle spin. Pitch is the drag's alone, held where it was left, within {@link PITCH_LIMIT}.
 */
export interface Spin {
  readonly yaw: number;
  readonly pitch: number;
  /** Yaw speed, rad/s. */
  readonly speed: number;
}

/** The spin a model turns at, untouched: a turn every ten seconds or so. */
export const IDLE_SPIN = 0.6;

/** How far a drag tilts the model, rad: past it, the view is from under or over. */
export const PITCH_LIMIT = 1.2;

/** Radians per pixel dragged. */
export const DRAG_RAD_PER_PX = 0.012;

/** The fastest a fling spins it, rad/s. */
export const MAX_SPIN = 20;

/** How quickly a fling's speed returns to the idle spin: its time constant, s. */
export const SPIN_EASE_S = 1.5;

/** The spin of a model just shown: a three-quarter view from slightly above. */
export const SPIN_START: Spin = { yaw: -0.6, pitch: 0.25, speed: IDLE_SPIN };

/** The spin `dt` seconds on, untouched: its speed eased toward the idle spin. */
export function spinStep(spin: Spin, dt: number): Spin {
  const speed = IDLE_SPIN + (spin.speed - IDLE_SPIN) * Math.exp(-dt / SPIN_EASE_S);
  return { yaw: wrap(spin.yaw + speed * dt), pitch: spin.pitch, speed };
}

/** The spin after a drag of (`dx`, `dy`) px over `dt` s: turned by it, its speed the drag's, capped. */
export function spinDrag(spin: Spin, dx: number, dy: number, dt: number): Spin {
  // Inverted like the pitch (Loïc, 2026-11-04): the model turns the way the pointer pushes its near side.
  const turn = -dx * DRAG_RAD_PER_PX;
  const speed = dt > 0 ? Math.max(-MAX_SPIN, Math.min(MAX_SPIN, turn / dt)) : spin.speed;
  // Inverted (Loïc, 2026-11-04): a drag up tips the model's front up, as grabbing it would; a drag down, down.
  const pitch = Math.max(-PITCH_LIMIT, Math.min(PITCH_LIMIT, spin.pitch - dy * DRAG_RAD_PER_PX));
  return { yaw: wrap(spin.yaw + turn), pitch, speed };
}

/** A drag held still for `dt` s: the fling it will be let go at slows, so a held pointer lets the model go still. */
export function spinHold(spin: Spin, dt: number): Spin {
  return { ...spin, speed: spin.speed * Math.exp(-dt / 0.08) };
}

function wrap(angle: number): number {
  const turn = 2 * Math.PI;
  return angle - turn * Math.floor((angle + Math.PI) / turn);
}
