import { beforeEach, describe, expect, it, vi } from 'vitest';

import { previewLines } from '../src/data/inventory-grid';
import { itemMenuItems } from '../src/data/item-menu';
import type { InventoryItem } from '../src/data/items';
import { cooldownLeft, STIM_COOLDOWN_MS, STIM_ITEM, stimDoses, stimToDrink } from '../src/data/potions';
import { HEAL_FX_MS, HEAL_MOTES, moteAt, ringAt } from '../src/render/heal-fx';
import { useItems } from '../src/state/items-store';
import { useMarket } from '../src/state/market-store';
import { useUi } from '../src/state/ui-store';

/**
 * The stim on the client (crafting-catalog.md § 5.4; Loïc, 2026-11-05): slot 5 drinks the one with the fewest doses left, a crate last; the item's menu
 * offers a drink; the heal rises round the body and is gone at its end.
 */

function stim(id: string, slot: number, doses = 3, count = 1): InventoryItem {
  return {
    id,
    slot,
    item: STIM_ITEM,
    name: 'Stim',
    category: 'potion',
    template: 56,
    mk: 0,
    count,
    stats: { potency: 204, condition: doses, maxCondition: 3, lifeMilli: 1000, repairs: 0, crafter: '0' },
  };
}

const blaster: InventoryItem = {
  id: '9',
  slot: 9,
  item: 'blaster_dl44',
  name: 'DL-44',
  category: 'weapon',
  template: 69,
  mk: 1,
  count: 1,
  stats: {
    damage: 81,
    delayMs: 1021,
    rangeDm: 300,
    wears: 101,
    condition: 1,
    maxCondition: 1,
    lifeMilli: 1000,
    repairs: 0,
    crafter: '0',
  },
};

describe('the stim drunk', () => {
  it('is the one with the fewest doses, a crate last, none without one', () => {
    expect(stimToDrink([stim('1', 1), stim('2', 2, 1), stim('3', 3, 3, 4)])).toBe('2');
    expect(stimToDrink([stim('3', 3, 3, 4), stim('1', 1)])).toBe('1');
    expect(stimToDrink([stim('3', 3, 3, 4)])).toBe('3');
    expect(stimToDrink([stim('3', 3, 3, 4)], 0)).toBeNull();
    expect(stimToDrink([blaster])).toBeNull();
    expect(stimToDrink([{ ...stim('4', 120), slot: 120 }])).toBeNull();
    expect(stimDoses([stim('1', 1), stim('2', 2, 1), stim('3', 3, 3, 4)])).toBe(3 + 1 + 12);
  });

  it('is previewed by its potency and its doses, not as a worn item', () => {
    const lines = previewLines(stim('2', 2, 2));
    expect(lines).toContainEqual({ label: 'Doses', value: '2 / 3' });
    expect(lines).toContainEqual({ label: 'Potency', value: '204' });
    expect(lines.some((l) => l.label === 'Repairs' || l.label === 'Condition')).toBe(false);
  });

  it('is offered by its menu — a crate only with a slot to take one out into — and never by a blaster', () => {
    const items = [stim('1', 1), stim('3', 3, 3, 4), blaster];
    expect(itemMenuItems(items[0], items).find((e) => e.label === 'Drink')?.action).toEqual({
      kind: 'consume',
      item: '1',
    });
    expect(itemMenuItems(items[1], items, 1).some((e) => e.label === 'Drink')).toBe(true);
    expect(itemMenuItems(items[1], items, 0).some((e) => e.label === 'Drink')).toBe(false);
    expect(itemMenuItems(blaster, items).some((e) => e.label === 'Drink')).toBe(false);
  });

  describe('from slot 5', () => {
    beforeEach(() => {
      useItems.setState({ snapshot: { open: true, slots: 40, items: [stim('1', 1), stim('2', 2, 2)], lost: [] } });
    });

    it('sends the emptiest stim, and keeps the stance', () => {
      const consume = vi.fn();
      const before = useMarket.getState().consume;
      useMarket.setState({ consume });
      try {
        useUi.setState({ stance: 4 });
        useUi.getState().pressSlot(5, true);
        expect(consume).toHaveBeenCalledWith('2');
        expect(useUi.getState().stance).toBe(4);
      } finally {
        useMarket.setState({ consume: before });
      }
    });

    it('sends nothing within the cooldown the last heal started', () => {
      const consume = vi.fn();
      const before = useMarket.getState().consume;
      useMarket.setState({ consume });
      try {
        useUi.getState().drankStim(performance.now());
        useUi.getState().pressSlot(5, true);
        expect(consume).not.toHaveBeenCalled();
        useUi.setState({ stimReadyAt: 0 });
        useUi.getState().pressSlot(5, true);
        expect(consume).toHaveBeenCalledOnce();
      } finally {
        useMarket.setState({ consume: before });
      }
    });

    it('waits the cooldown from the heal, not from the press', () => {
      useUi.getState().drankStim(1_000);
      expect(cooldownLeft(useUi.getState().stimReadyAt, 1_000)).toBe(1);
      expect(cooldownLeft(useUi.getState().stimReadyAt, 1_000 + STIM_COOLDOWN_MS / 2)).toBeCloseTo(0.5, 6);
      expect(cooldownLeft(useUi.getState().stimReadyAt, 1_000 + STIM_COOLDOWN_MS)).toBe(0);
    });
  });
});

describe('the heal seen', () => {
  it('rises round the body, every mote in and out within the heal, none past its end', () => {
    const span = HEAL_FX_MS / 1000;
    for (let i = 0; i < HEAL_MOTES; i++) {
      let shown = false;
      let lastY = -1;
      for (let t = 0; t < span; t += 0.02) {
        const m = moteAt(i, t);
        if (m.size > 0) {
          shown = true;
          expect(m.y).toBeGreaterThanOrEqual(lastY);
          expect(Math.hypot(m.x, m.z)).toBeLessThan(0.7);
          lastY = m.y;
        }
      }

      expect(shown).toBe(true);
      expect(moteAt(i, span).size).toBe(0);
      expect(moteAt(i, -0.1).size).toBe(0);
    }

    expect(ringAt(0).diameter).toBeGreaterThan(0);
    expect(ringAt(span * 0.3).diameter).toBeGreaterThan(ringAt(0.05).diameter);
    expect(ringAt(span).scale).toBe(0);
  });
});
