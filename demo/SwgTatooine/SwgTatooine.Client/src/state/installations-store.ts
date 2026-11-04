import { create } from 'zustand';

import {
  installationAlerts,
  NO_ALERTS,
  parseInstallations,
  placedNotices,
  type InstallationAlerts,
  type InstallationList,
} from '../data/installations';
import { useChat } from './chat-store';
import { useMarket } from './market-store';

/**
 * The installations window's state (crafting-build.md § 10): whether it is open, and the character's installations as last read. Read on opening, every
 * few seconds while open, and after every command of it — the server computes what each holds; this only shows it.
 */
export interface InstallationsStore {
  readonly open: boolean;
  readonly list: InstallationList | null;
  readonly toggle: () => void;
  readonly close: () => void;
  readonly refresh: () => Promise<void>;
  readonly place: (kit: string, family: number) => void;
  readonly collect: (id: string) => void;
  readonly pay: (id: string, credits: number) => void;
  readonly pickUp: (id: string) => void;
  readonly reset: () => void;
  /**
   * Reads them every {@link ALERT_READ_MS} while nothing else does, so a full hopper or an upkeep running out is said even with the window shut (client.md
   * C39): called by the frame loop.
   */
  readonly watch: () => void;
}

/** How often the installations are read for their notices with the window shut, ms. */
export const ALERT_READ_MS = 60_000;

let alerts: InstallationAlerts = NO_ALERTS;
let readAt = Number.NEGATIVE_INFINITY;

/** How often the list is read again while the window is open, ms. */
export const INSTALLATIONS_REFRESH_MS = 5_000;

let generation = 0;
let timer: ReturnType<typeof setInterval> | null = null;

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

export const useInstallations = create<InstallationsStore>((set, get) => {
  const stopTimer = (): void => {
    if (timer !== null) {
      clearInterval(timer);
      timer = null;
    }
  };

  // A command's effect shows on the next read: one soon after it, the rest on the timer.
  const after = (): void => {
    setTimeout(() => {
      void get().refresh();
    }, 600);
  };

  return {
    open: false,
    list: null,
    toggle: () => {
      if (get().open) {
        get().close();
        return;
      }

      set({ open: true });
      void get().refresh();
      stopTimer();
      timer = setInterval(() => {
        void get().refresh();
      }, INSTALLATIONS_REFRESH_MS);
    },
    close: () => {
      stopTimer();
      set({ open: false });
    },
    refresh: async () => {
      const mine = ++generation;
      const list = parseInstallations(await fetchJson('swg/installations'));
      if (list !== null && mine === generation) {
        const placed = placedNotices(get().list, list);
        set({ list });
        for (const text of placed) {
          useChat.getState().notice(text);
        }

        readAt = performance.now();
        const said = installationAlerts(alerts, list.installations);
        alerts = said.next;
        for (const text of said.notices) {
          useChat.getState().notice(text);
        }
      }
    },
    watch: () => {
      const now = performance.now();
      if (get().open || useMarket.getState().actions === null || now - readAt < ALERT_READ_MS) {
        return;
      }

      readAt = now;
      void get().refresh();
    },
    place: (kit, family) => {
      if (useMarket.getState().sendCommand({ kind: 'installPlace', item: kit, family })) {
        after();
      }
    },
    collect: (id) => {
      if (useMarket.getState().sendCommand({ kind: 'installCollect', installation: id })) {
        after();
      }
    },
    pay: (id, credits) => {
      if (useMarket.getState().sendCommand({ kind: 'installPay', installation: id, credits })) {
        after();
      }
    },
    pickUp: (id) => {
      if (useMarket.getState().sendCommand({ kind: 'installPickUp', installation: id })) {
        after();
      }
    },
    reset: () => {
      stopTimer();
      generation++;
      alerts = NO_ALERTS;
      readAt = Number.NEGATIVE_INFINITY;
      set({ open: false, list: null });
    },
  };
});
