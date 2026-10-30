import { afterEach, describe, expect, it } from 'vitest';
import type { Catalogue } from '../src/assets/descriptor';
import {
  itemIcons,
  itemInitials,
  itemTooltip,
  lostLines,
  mkNumeral,
  parseInventory,
  slotGrid,
  type InventoryItem,
  type InventorySnapshot,
  type LostNote,
} from '../src/data/items';
import { useChat } from '../src/state/chat-store';
import { useItems } from '../src/state/items-store';

/**
 * The inventory on the client (inventory-loot.md § 6, IL-11): the answer of `GET /swg/inventory` checked, the grid, the Mk
 * badge, the tooltip, the catalogue's icons, and the chat lines a loss makes — none on a session's first read. No ledger
 * rows: a drop is the server's notice (`notices.test.ts`).
 */

const item = (slot: number, name: string, mk: number, count: number, asset = 'mat_hide'): InventoryItem => ({
  id: `9${String(slot)}`,
  slot,
  item: asset,
  name,
  category: 'material',
  template: 8,
  mk,
  count,
});

const lost = (n: number, name: string, mk: number, count: number, tick = 20): LostNote => ({
  n,
  tick,
  item: name.toLowerCase(),
  name,
  mk,
  count,
});

const snapshot = (lostNotes: LostNote[] = [], items: InventoryItem[] = []): InventorySnapshot => ({
  open: true,
  slots: 30,
  items,
  lost: lostNotes,
});

describe('the inventory answer', () => {
  it('is read with its stacks and lost notes', () => {
    const answer = {
      open: true,
      slots: 30,
      items: [
        { id: '17', slot: 1, item: 'mat_hide', name: 'Hide', category: 'material', template: 8, mk: 2, count: 7 },
      ],
      lost: [{ n: 4, tick: 130, item: 'core_armor', name: 'Armor Core', mk: 2, count: 1 }],
    };
    expect(parseInventory(answer)).toEqual(answer);

    // A server that does not name templates (before the bazaar): read, the template 0 — nothing to list it by.
    const untemplated = { id: '17', slot: 1, item: 'mat_hide', name: 'Hide', category: 'material', mk: 2, count: 7 };
    expect(parseInventory({ ...answer, items: [untemplated] })?.items[0].template).toBe(0);
  });

  it('is refused when a field is missing or of the wrong type, and a malformed entry is dropped', () => {
    expect(parseInventory(null)).toBeNull();
    expect(parseInventory('x')).toBeNull();
    expect(parseInventory({ open: true, slots: 30, items: [] })).toBeNull();
    expect(parseInventory({ open: true, slots: '30', items: [], lost: [] })).toBeNull();
    expect(parseInventory({ open: true, slots: 30, items: {}, lost: [] })).toBeNull();

    const parsed = parseInventory({
      open: 'yes',
      slots: 30,
      items: [item(1, 'Hide', 2, 7), { id: 17, slot: 2 }],
      lost: [lost(1, 'Bone', 1, 2), { n: '2' }],
    });
    expect(parsed).toEqual({
      open: false,
      slots: 30,
      items: [item(1, 'Hide', 2, 7)],
      lost: [lost(1, 'Bone', 1, 2)],
    });
  });
});

describe('the grid', () => {
  it('puts each stack in its slot, slot 1 first, and draws none outside the grid', () => {
    const a = item(1, 'Hide', 2, 7);
    const b = item(30, 'Bone', 1, 1);
    const cells = slotGrid([b, a, item(0, 'Zero', 1, 1), item(31, 'Past', 1, 1), item(2.5, 'Half', 1, 1)], 30);
    expect(cells).toHaveLength(30);
    expect(cells[0]).toBe(a);
    expect(cells[29]).toBe(b);
    expect(cells.filter((c) => c !== null)).toHaveLength(2);
  });

  it('names a Mk by its numeral, none for an item without Mk', () => {
    expect([0, 1, 2, 3, 4, 5, 6].map(mkNumeral)).toEqual(['', 'I', 'II', 'III', 'IV', 'V', '']);
  });

  it("says a stack's name, Mk and count on hover", () => {
    expect(itemTooltip(item(1, 'Hide', 2, 7))).toBe('Hide · Mk II · ×7');
    expect(itemTooltip(item(1, 'Crate', 0, 1))).toBe('Crate · ×1');
  });

  it("shows an item's initials without an icon", () => {
    expect(itemInitials('Armor Core')).toBe('AC');
    expect(itemInitials('Hide')).toBe('H');
    expect(itemInitials('Ranged  Weapon Core')).toBe('RW');
  });
});

describe('the item icons', () => {
  it("are the catalogue's 32 px icons by asset id, URL'd in the asset folder, and none at another size", () => {
    const catalogue: Catalogue = {
      schema: 2,
      version: 13,
      assets: {},
      textures: [],
      icons: {
        mat_hide: { rev: 1, png: { '32': 'mat_hide_icon32-1.png', '48': 'mat_hide_icon48-1.png' } },
        core_armor: { rev: 2, png: { '32': 'core_armor_icon32-2.png' } },
        swoop_big: { rev: 1, png: { '48': 'swoop_big_icon48-1.png' } },
      },
    };
    expect(itemIcons(catalogue, (file) => `/assets/${file}`)).toEqual({
      mat_hide: '/assets/mat_hide_icon32-1.png',
      core_armor: '/assets/core_armor_icon32-2.png',
    });
    const bare: Catalogue = { schema: 2, version: 13, assets: {}, textures: [] };
    expect(itemIcons(bare, (file) => file)).toEqual({});
  });
});

describe('the lost lines', () => {
  it('announce nothing on the first read of a session: its notes are history, not news', () => {
    expect(lostLines(null, snapshot([lost(1, 'Bone', 1, 1)]))).toEqual([]);
  });

  it('say the new notes of one tick as one line, and a note read twice once', () => {
    const before = snapshot([lost(1, 'Bone', 1, 2, 5)]);
    const after = snapshot([lost(3, 'Hide', 2, 2), lost(2, 'Armor Core', 2, 1), lost(1, 'Bone', 1, 2, 5)]);
    expect(lostLines(before, after)).toEqual(['Inventory full — lost: Armor Core Mk II, Hide Mk II ×2']);
    expect(lostLines(after, after)).toEqual([]);
  });
});

describe('the items store', () => {
  afterEach(() => {
    useItems.setState({ snapshot: null, icons: {} });
    useChat.getState().clear();
  });

  it('says each loss in the chat, but not the first read, and nothing after a reset', () => {
    useItems.getState().setSnapshot(snapshot([lost(1, 'Hide', 1, 4)]));
    expect(useChat.getState().lines).toHaveLength(0);

    useItems.getState().setSnapshot(snapshot([lost(2, 'Bone', 1, 4), lost(1, 'Hide', 1, 4)]));
    const lines = useChat.getState().lines;
    expect(lines.map((l) => l.text)).toEqual(['Inventory full — lost: Bone Mk I ×4']);
    expect(lines.every((l) => l.system === true)).toBe(true);

    useItems.getState().reset();
    useItems.getState().setSnapshot(snapshot([lost(3, 'Gland', 1, 4)]));
    expect(useChat.getState().lines).toHaveLength(1);
  });

  it('keeps the icons across a reset: they are the catalogue’s, not the session’s', () => {
    useItems.getState().setIcons({ mat_hide: '/assets/mat_hide_icon32-1.png' });
    useItems.getState().reset();
    expect(useItems.getState().icons).toEqual({ mat_hide: '/assets/mat_hide_icon32-1.png' });
  });
});
