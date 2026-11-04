import { afterEach, describe, expect, it } from 'vitest';

import { canUseAt, installationPartOf, INSTALLATION_BOX_M } from '../src/app/ticket-terminal';
import { itemMenuItems } from '../src/data/item-menu';
import {
  EXTRACTOR_TEMPLATE,
  INSTALLATION_REACH_M,
  installationAt,
  installationMenu,
  kitMenuItems,
  locationText,
  placedNotices,
  type Installation,
  type InstallationList,
} from '../src/data/installations';
import type { InventoryItem } from '../src/data/items';
import { useUi } from '../src/state/ui-store';

/**
 * A window lists, the world acts (client.md C42, Loïc 2026-11-04): a structure is listed with where it stands and a waypoint, and what it does is its
 * own menu, held on it within reach; an item's actions are its own menu, from its slot.
 */

const EXTRACTOR: Installation = {
  id: '77',
  kind: 1,
  family: 3,
  familyName: 'Metal',
  x: 4000,
  z: -4200,
  rate: 360,
  hopper: 500,
  held: 42,
  state: 1,
  paidSeconds: 5400,
};

const LIST: InstallationList = { installations: [EXTRACTOR], lots: 1, lotsAllowed: 10, upkeepPerHour: 100 };

const STATS = { condition: 100, maxCondition: 100, lifeMilli: 1000, repairs: 0, crafter: '0' };

function item(
  id: string,
  slot: number,
  template: number,
  count = 1,
  extra: Partial<InventoryItem> = {},
): InventoryItem {
  return { id, slot, item: 'x', name: 'Thing', category: 'weapon', template, mk: 1, count, ...extra };
}

describe('a structure, listed and acted on (client.md C42)', () => {
  afterEach(() => {
    useUi.getState().closeContextMenu();
  });

  it('is located from the nearest city, by distance and compass point', () => {
    // Port Sable stands at (3460, -4768): the extractor is 540 m east and 568 m north of it.
    expect(locationText(4000, -4200)).toBe('784 m NE of Port Sable · 4000, -4200');
    expect(locationText(3460, -1768)).toBe('3.0 km N of Port Sable · 3460, -1768');
  });

  it('is the marker’s when it stands where the list says, and no other’s', () => {
    expect(installationAt(LIST, 4000.5, -4200.5)?.id).toBe('77');
    expect(installationAt(LIST, 4064, -4200)).toBeNull();
    expect(installationAt(null, 4000, -4200)).toBeNull();
  });

  it('offers collect while it holds, upkeep always, and pick up only empty', () => {
    const full = installationMenu(EXTRACTOR, 100);
    expect(full.title).toBe('Extractor · Metal');
    expect(full.detail).toBe('42 / 500 · running · upkeep 1 h 30 min');
    expect(full.items.map((i) => i.action)).toEqual([
      { kind: 'installCollect', installation: '77' },
      { kind: 'installPay', installation: '77', credits: 100 },
      { kind: 'installPay', installation: '77', credits: 800 },
    ]);

    const empty = installationMenu({ ...EXTRACTOR, held: 0 }, 100);
    expect(empty.items.map((i) => i.action.kind)).toEqual(['installPay', 'installPay', 'installPickUp']);
  });

  it('is usable within the server’s reach less a metre, picked by a box when its asset gives none', () => {
    const part = installationPartOf(null, 4000, 50, -4200, 0);
    expect(canUseAt(part, 4000 + INSTALLATION_REACH_M - 1.5, -4200)).toBe(true);
    expect(canUseAt(part, 4000 + INSTALLATION_REACH_M - 0.5, -4200)).toBe(false);
    const xs = Array.from({ length: 8 }, (_, i) => part.corners?.[i * 3] ?? NaN);
    const ys = Array.from({ length: 8 }, (_, i) => part.corners?.[i * 3 + 1] ?? NaN);
    expect([Math.min(...xs), Math.max(...xs), Math.min(...ys), Math.max(...ys)]).toEqual([
      4000 - INSTALLATION_BOX_M / 2,
      4000 + INSTALLATION_BOX_M / 2,
      50,
      50 + INSTALLATION_BOX_M,
    ]);
  });

  it('is said in the chat once placed, and not on the first reading', () => {
    const none: InstallationList = { ...LIST, installations: [] };
    expect(placedNotices(none, LIST)).toEqual([
      'Extractor · Metal placed, 784 m NE of Port Sable · 4000, -4200: hold on it to pay its upkeep, and it starts',
    ]);
    expect(placedNotices(LIST, LIST)).toEqual([]);
    expect(placedNotices(null, LIST)).toEqual([]);
  });

  it('runs its command from the menu', () => {
    // Without a live session the command is not sent; the action still closes the menu, as every entry does.
    useUi.getState().openContextMenu({ x: 0, y: 0, title: 't', source: 'installation', items: [] });
    useUi.getState().runContextAction({ kind: 'installCollect', installation: '77' });
    expect(useUi.getState().contextMenu).toBeNull();
  });
});

describe('an item’s own menu (client.md C42)', () => {
  it('places a kit here, one entry a family, and offers nothing for a worn kit or a part', () => {
    const kit = item('k', 3, EXTRACTOR_TEMPLATE, 1, { category: 'structure', stats: STATS });
    const entries = kitMenuItems(kit);
    expect(entries.map((e) => e.label)).toContain('Place here · Ore');
    expect(entries).toHaveLength(7);
    expect(entries[0].action).toEqual({ kind: 'installPlace', item: 'k', family: 1 });
    expect(kitMenuItems({ ...kit, slot: 101 })).toEqual([]);
    expect(itemMenuItems(item('p', 4, 36), [])).toEqual([]);
  });

  it('wears an item with stats, packs it with its identical ones, and unpacks a crate', () => {
    const worn = { ...STATS, wears: 101 };
    const a = item('a', 1, 69, 1, { stats: worn, packKey: 'k' });
    const b = item('b', 2, 69, 4, { stats: worn, packKey: 'k' });
    expect(itemMenuItems(a, [a, b]).map((e) => e.action)).toEqual([
      { kind: 'equip', item: 'a' },
      { kind: 'pack', item: 'a', into: 'b' },
    ]);
    expect(itemMenuItems(b, [a, b]).map((e) => e.action)).toEqual([
      { kind: 'pack', item: 'a', into: 'b' },
      { kind: 'unpack', item: 'b', count: 1 },
      { kind: 'unpack', item: 'b', count: 3 },
    ]);
  });
});
