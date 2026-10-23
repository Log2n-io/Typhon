import { describe, expect, it } from 'vitest';
import { startInventoryPoll } from '../src/app/inventory-poll';
import type { RealmInventory } from '../src/data/realm-inventory';

/** A document identified by its tick, which is all these cases need to tell two of them apart. */
function at(tick: number): RealmInventory {
  return {
    tick,
    censusTick: tick,
    maxRows: 64,
    omitted: 0,
    counts: { active: 0, simulated: 0, dormant: 0, closing: 0, divided: 0 },
    realms: [],
  };
}

/**
 * A `read` whose responses are resolved by hand, so a case can land them in whatever order it likes.
 *
 * Deliberately not a timer or a delay: what is under test is the ORDER responses are published in, and staging that
 * with sleeps would be a race dressed as a test.
 */
function stagedReads() {
  const pending: ((inventory: RealmInventory | null) => void)[] = [];
  const published: (RealmInventory | null)[] = [];
  let visible = true;
  const poll = startInventoryPoll({
    read: () =>
      new Promise((resolve) => {
        pending.push(resolve);
      }),
    publish: (inventory) => published.push(inventory),
    visible: () => visible,
  });

  return {
    poll,
    published,
    inFlight: () => pending.length,
    hide: () => {
      visible = false;
    },
    show: () => {
      visible = true;
    },
    /** Lands the n-th issued response (0-based) and lets its `.then` run. */
    land: async (n: number, inventory: RealmInventory | null) => {
      pending[n]?.(inventory);
      await Promise.resolve();
      await Promise.resolve();
    },
  };
}

describe('the realm inventory poll', () => {
  it('publishes what it reads', async () => {
    const h = stagedReads();
    h.poll.tick();
    await h.land(0, at(10));
    expect(h.published).toEqual([at(10)]);
  });

  it('drops a response that arrives after a newer one', async () => {
    // The interval fires whether or not the previous request came back, so a slow response really can land after a
    // newer one. On a panel watched for a room falling asleep, letting it through means the row coming back.
    const h = stagedReads();
    h.poll.tick();
    h.poll.tick();
    expect(h.inFlight()).toBe(2);

    await h.land(1, at(20));
    await h.land(0, at(10));
    expect(h.published).toEqual([at(20)]);
  });

  it('keeps publishing after an out-of-order response was dropped', async () => {
    const h = stagedReads();
    h.poll.tick();
    h.poll.tick();
    await h.land(1, at(20));
    await h.land(0, at(10));

    h.poll.tick();
    await h.land(2, at(30));
    expect(h.published).toEqual([at(20), at(30)]);
  });

  it('publishes null, because a server that stopped answering must not leave the last document looking live', async () => {
    const h = stagedReads();
    h.poll.tick();
    await h.land(0, null);
    expect(h.published).toEqual([null]);
  });

  it('reads nothing while the page is hidden', () => {
    // A hidden tab must not keep a request a second alive for hours against a server nobody is looking at.
    const h = stagedReads();
    h.hide();
    h.poll.tick();
    h.poll.tick();
    expect(h.inFlight()).toBe(0);
    expect(h.published).toEqual([]);
  });

  it('reads at once when the page comes back, rather than waiting out a throttled interval', async () => {
    // Without this the visibility gate makes staleness WORSE: browsers throttle a hidden tab's interval to about a
    // minute, so the panel would show a document from before the tab was hidden, looking live.
    const h = stagedReads();
    h.hide();
    h.poll.tick();
    h.show();
    h.poll.refresh();
    await h.land(0, at(50));
    expect(h.published).toEqual([at(50)]);
  });

  it('publishes nothing once stopped, including a response already in flight', async () => {
    // The component unmounts; a request issued a moment earlier still resolves.
    const h = stagedReads();
    h.poll.tick();
    h.poll.stop();
    await h.land(0, at(10));
    expect(h.published).toEqual([]);
  });

  it('issues nothing once stopped', () => {
    const h = stagedReads();
    h.poll.stop();
    h.poll.tick();
    h.poll.refresh();
    expect(h.inFlight()).toBe(0);
  });
});
