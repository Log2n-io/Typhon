import { afterEach, describe, expect, it } from 'vitest';
import type { MarketCommand } from '../src/data/market';
import { useCraft } from '../src/state/craft-store';
import { useMarket, type MarketActions } from '../src/state/market-store';

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
