import { afterEach, describe, expect, it, vi } from 'vitest';
import { MARKET_ANSWER_MS, type MarketCommand } from '../src/data/market';
import { craftDoneText, useCraft, type CraftSent } from '../src/state/craft-store';
import { useMarket, type MarketActions } from '../src/state/market-store';
import type { CraftUnderWay } from '../src/data/craft';
import type { InventoryItem } from '../src/data/items';

/**
 * The crafting panel's state (crafting-build.md C2): a read that fails keeps the craft shown, an older answer never overwrites a newer one, a command in
 * flight holds the next — a double click spends one point — and a new session forgets the last character's craft.
 */

const CRAFT = {
  craft: {
    id: '9',
    schematic: 69,
    output: 69,
    mk: 2,
    pointsLeft: 6,
    addonMk: 0,
    lines: [{ q: 500_000, f: 0, value: 1 }],
  },
};

function harness() {
  const sent: MarketCommand[] = [];
  const answers: (() => Promise<unknown>)[] = [];
  const actions: MarketActions = {
    send: (command) => {
      sent.push(command);
      return true;
    },
    fetch: (url) => {
      if (url.startsWith('swg/schematics')) {
        return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ schematics: [], attrs: {} }) });
      }

      const next = answers.shift() ?? (() => Promise.resolve(CRAFT));
      return next().then((body) =>
        body instanceof Error ? Promise.reject(body) : { ok: true, status: 200, json: () => Promise.resolve(body) },
      );
    },
    token: () => 't',
  };
  useMarket.getState().setActions(actions);
  return { sent, answers };
}

describe('the craft store', () => {
  afterEach(() => {
    useCraft.getState().reset();
    useMarket.getState().reset();
    useMarket.getState().setActions(null);
  });

  it('keeps the craft shown when a read fails, and clears it when the server says there is none', async () => {
    const h = harness();
    await useCraft.getState().refresh();
    expect(useCraft.getState().craft?.schematic).toBe(69);

    h.answers.push(() => Promise.resolve(new Error('network')));
    await useCraft.getState().refresh();
    expect(useCraft.getState().craft?.schematic).toBe(69);

    h.answers.push(() => Promise.resolve({ craft: null }));
    await useCraft.getState().refresh();
    expect(useCraft.getState().craft).toBeNull();
  });

  it('drops an older answer that lands after a newer one', async () => {
    const h = harness();
    let releaseOld: (value: unknown) => void = () => undefined;
    h.answers.push(() => new Promise((resolve) => (releaseOld = resolve)));
    h.answers.push(() => Promise.resolve({ craft: null }));
    const older = useCraft.getState().refresh();
    await useCraft.getState().refresh();
    releaseOld(CRAFT);
    await older;
    expect(useCraft.getState().craft).toBeNull();
  });

  it('sends one point for a double click, and forgets everything on reset', async () => {
    const h = harness();
    await useCraft.getState().refresh();
    useCraft.getState().point(0);
    useCraft.getState().point(0);
    expect(h.sent.filter((c) => c.kind === 'craftPoint')).toHaveLength(1);

    useCraft.getState().select(69);
    useCraft.getState().reset();
    expect(useCraft.getState()).toMatchObject({ craft: null, selected: null, mks: [], parts: [], open: false });
  });
});

/**
 * A step is done when it shows (Loïc, 2026-11-02: a click after a step sometimes did nothing). Accepted commands are never acked, so the row in flight
 * is settled by the read that shows the step — and only by the step's own change, never by a read a loot drop caused.
 */
describe('a workshop step', () => {
  afterEach(() => {
    vi.useRealTimers();
    useCraft.getState().reset();
    useMarket.getState().reset();
    useMarket.getState().setActions(null);
  });

  const under = (pointsLeft: number, addonMk = 0): CraftUnderWay => ({ ...CRAFT.craft, pointsLeft, addonMk });
  const sent = (kind: CraftSent['kind'], label = 'Damage', item = '', repairs = 0): CraftSent => ({
    kind,
    seq: 1,
    item,
    repairs,
    label,
  });
  const blaster = (repairs: number): InventoryItem =>
    ({
      id: '77',
      slot: 3,
      item: 'blaster_dl44',
      name: 'DL-44 Blaster',
      template: 60,
      mk: 1,
      count: 1,
      stats: { repairs },
    }) as InventoryItem;

  it('is told by what it changes: points, the add-on, the craft gone or come, the item repaired or gone', () => {
    expect(craftDoneText(sent('craftPoint'), under(6), under(5), [])).toBe('+1 to Damage — 5 point(s) left');
    expect(craftDoneText(sent('craftPoint'), under(6), under(6), [])).toBeNull();
    expect(craftDoneText(sent('craftEnhance'), under(6), under(6, 2), [])).toBe('Add-on Mk II applied');
    expect(craftDoneText(sent('craftFinish', 'DL-44 Blaster'), under(0), null, [])).toBe(
      'Finished: DL-44 Blaster — in your inventory',
    );
    expect(craftDoneText(sent('craftStart', 'DL-44 Blaster'), null, under(6), [])).toBe(
      'Craft started: DL-44 Blaster — 6 point(s) to spend',
    );
    expect(craftDoneText(sent('repair', 'DL-44 Blaster', '77', 0), null, null, [blaster(1)])).toBe(
      'Repaired: DL-44 Blaster',
    );
    expect(craftDoneText(sent('repair', 'DL-44 Blaster', '77', 0), null, null, [blaster(0)])).toBeNull();
    expect(craftDoneText(sent('salvage', 'DL-44 Blaster', '77'), null, null, [blaster(0)])).toBeNull();
    expect(craftDoneText(sent('salvage', 'DL-44 Blaster', '77'), null, null, [])).toContain('Salvaged: DL-44 Blaster');
  });

  it('frees the buttons as soon as the read shows it, not after the 5 s guard — and not on a read that shows nothing new', async () => {
    const h = harness();
    await useCraft.getState().refresh();
    useCraft.getState().point(0);
    expect(useMarket.getState().pending).toContain('craft');

    await useCraft.getState().refresh(); // a loot drop moved the revision: the craft is unchanged
    expect(useMarket.getState().pending).toContain('craft');
    expect(useCraft.getState().done).toBeNull();

    h.answers.push(() => Promise.resolve({ craft: under(5) }));
    await useCraft.getState().refresh();
    expect(useMarket.getState().pending).not.toContain('craft');
    expect(useCraft.getState().done).toMatchObject({ ok: true, text: '+1 to line 1 — 5 point(s) left' });

    useCraft.getState().point(0);
    expect(h.sent.filter((c) => c.kind === 'craftPoint')).toHaveLength(2);
  });

  it('says so when the server never answers, and nothing more when it refused', () => {
    vi.useFakeTimers();
    harness();
    useCraft.getState().finish(); // no craft: nothing sent
    useCraft.setState({ craft: under(0) });
    useCraft.getState().finish();
    vi.advanceTimersByTime(MARKET_ANSWER_MS);
    expect(useCraft.getState().done).toMatchObject({ ok: false, text: 'No answer from the server — try again' });

    useCraft.getState().finish();
    useMarket.getState().refused(171, { kind: 'craftFinish', schematic: 69 });
    vi.advanceTimersByTime(MARKET_ANSWER_MS);
    expect(useCraft.getState().done?.ok).not.toBe(false);
  });
});
