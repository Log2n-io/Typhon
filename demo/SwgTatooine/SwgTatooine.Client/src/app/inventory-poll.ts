import type { RealmInventory } from '../data/realm-inventory';

/**
 * The realm inventory's poll, as a thing that can be tested.
 *
 * <b>Extracted for the reason `frame-policy.ts` and `realm-switch.ts` were.</b> No test in this client can render
 * React — there is no DOM — so anything living inside a `useEffect` is untested by construction. Both of this poll's
 * real failure modes are invisible that way: a response that lands out of order, and a tab that comes back showing a
 * document from before it was hidden. Neither is hypothetical and neither is something a reviewer should have to take
 * on trust.
 *
 * Everything it touches is injected. It knows nothing about React, zustand, `window` or `document`.
 */
export interface InventoryPollHost {
  /** Fetches one document. Must not throw — `fetchRealmInventory` answers `null` instead. */
  readonly read: () => Promise<RealmInventory | null>;
  /** Publishes a document, or `null` for a source that stopped reporting one. */
  readonly publish: (inventory: RealmInventory | null) => void;
  /** Whether the page is being looked at. */
  readonly visible: () => boolean;
}

/** A running poll. */
export interface InventoryPoll {
  /** Reads once, unless the page is hidden. */
  readonly tick: () => void;
  /** Reads once whether or not a read is already in flight — for coming back to a hidden tab. */
  readonly refresh: () => void;
  /** Stops publishing. A response already in flight is dropped when it lands. */
  readonly stop: () => void;
}

/**
 * Starts a poll that publishes documents in the order they were ASKED FOR, not the order they arrive.
 *
 * <b>Out-of-order responses are the failure this exists to prevent.</b> The interval fires whether or not the previous
 * request has come back, so a slow response can land after a newer one and overwrite it — and on a panel watched for a
 * room falling asleep, that means the row coming back. Each request carries the sequence it was issued at and a
 * response older than the newest one already published is dropped.
 *
 * Nothing is published after {@link InventoryPoll.stop}, including a response already in flight.
 */
export function startInventoryPoll(host: InventoryPollHost): InventoryPoll {
  let issued = 0;
  let published = 0;
  let stopped = false;

  const read = (): void => {
    const seq = ++issued;
    void host.read().then((inventory) => {
      if (stopped || seq < published) {
        return;
      }

      published = seq;
      host.publish(inventory);
    });
  };

  return {
    tick: () => {
      // A hidden tab must not keep a request a second alive for hours against a server nobody is looking at. The
      // interval keeps running — browsers throttle it to about a minute anyway — and simply does nothing.
      if (!stopped && host.visible()) {
        read();
      }
    },
    refresh: () => {
      // Coming back to the tab reads at once rather than waiting out the throttled interval, which is up to a minute.
      // Without this the visibility gate above makes staleness WORSE: the panel shows a document from before the tab
      // was hidden, looking live.
      if (!stopped) {
        read();
      }
    },
    stop: () => {
      stopped = true;
    },
  };
}
