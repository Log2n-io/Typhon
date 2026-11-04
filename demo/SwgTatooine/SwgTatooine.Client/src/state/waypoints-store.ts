import { create } from 'zustand';
import { parseWaypoints, type Waypoint } from '../data/waypoints';
import { useChat } from './chat-store';
import { useMarket } from './market-store';

/**
 * The player's own waypoints (client.md C38): as `GET /swg/waypoints` last listed them, read at a session's start and after each of the player's own
 * waypoint commands — nothing else changes them — and whether the Waypoints window is open.
 */
export interface WaypointsStore {
  readonly list: readonly Waypoint[] | null;
  readonly max: number;
  readonly open: boolean;
  readonly toggle: () => void;
  readonly close: () => void;
  /** Reads the list now. */
  readonly refresh: () => Promise<void>;
  /** Reads it once a session has the means to, when it has not been read: called by the frame loop's waypoint scan. */
  readonly ensure: () => void;
  /** Sends a create; false when nothing left the client. */
  readonly create: (name: string, x: number, z: number, color: number, installation?: string) => boolean;
  readonly remove: (id: string) => boolean;
  /** Makes one the active waypoint; `'0'` makes none. */
  readonly activate: (id: string) => boolean;
  /** A new session: nothing read yet. */
  readonly reset: () => void;
}

/** How long after a command its effect is read, ms: the Workshop applies it on its next pass. */
const READ_AFTER_MS = 600;

/** The least time between two reads {@link WaypointsStore.ensure} makes, ms. */
const ENSURE_EVERY_MS = 5_000;

let generation = 0;
let ensuredAt = Number.NEGATIVE_INFINITY;

const fetchJson = async (url: string): Promise<unknown> => {
  const actions = useMarket.getState().actions;
  if (actions === null) {
    return null;
  }

  const token = actions.token();
  try {
    const response = await actions.fetch(url, {
      headers: token === null ? {} : { Authorization: `Bearer ${token}` },
      signal: AbortSignal.timeout(10_000),
    });
    return response.ok ? await response.json() : null;
  } catch {
    return null;
  }
};

export const useWaypoints = create<WaypointsStore>((set, get) => {
  const after = (): void => {
    setTimeout(() => {
      void get().refresh();
    }, READ_AFTER_MS);
  };

  // A refusal is said where the command was typed: chat (C37).
  useMarket.getState().onWaypointRefused((text) => {
    useChat.getState().notice(`Waypoint: ${text}`);
  });

  return {
    list: null,
    max: 100,
    open: false,
    toggle: () => {
      set((s) => ({ open: !s.open }));
      if (get().open) {
        void get().refresh();
      }
    },
    close: () => {
      set({ open: false });
    },
    refresh: async () => {
      const mine = ++generation;
      const read = parseWaypoints(await fetchJson('swg/waypoints'));
      if (read !== null && mine === generation) {
        set({ list: read.waypoints, max: read.max });
        // What was sent has been applied or refused by now: the row is free for the next command (an accepted one is never answered).
        useMarket.getState().settle('waypoint');
      }
    },
    ensure: () => {
      const now = performance.now();
      if (get().list !== null || useMarket.getState().actions === null || now - ensuredAt < ENSURE_EVERY_MS) {
        return;
      }

      ensuredAt = now;
      void get().refresh();
    },
    create: (name, x, z, color, installation = '0') => {
      const sent = useMarket.getState().sendCommand({ kind: 'waypointCreate', name, x, z, color, installation });
      if (sent) {
        after();
      }

      return sent;
    },
    remove: (id) => {
      const sent = useMarket.getState().sendCommand({ kind: 'waypointDelete', waypoint: id });
      if (sent) {
        after();
      }

      return sent;
    },
    activate: (id) => {
      const sent = useMarket.getState().sendCommand({ kind: 'waypointActivate', waypoint: id });
      if (sent) {
        after();
      }

      return sent;
    },
    reset: () => {
      generation++;
      ensuredAt = Number.NEGATIVE_INFINITY;
      set({ list: null, open: false });
    },
  };
});
