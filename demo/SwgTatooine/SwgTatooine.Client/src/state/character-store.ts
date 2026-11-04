import { create } from 'zustand';
import { useInstallations } from './installations-store';
import { useMarket } from './market-store';

/** The character window's tabs (client.md C39). */
export type CharacterTab = 'overview' | 'equipment' | 'structures';

/** What `GET /swg/character` says of the character's progress (character-level.md § 8). */
export interface CharacterProgress {
  readonly level: number;
  readonly xp: number;
  readonly xpLevelStart: number;
  /** The XP the next level starts at; 0 at the last level. */
  readonly xpNextLevel: number;
  readonly killXp: number;
  readonly missionXp: number;
  readonly maxHealth: number;
}

/** The answer of `GET /swg/character`, checked; `null` for anything else. */
export function parseProgress(json: unknown): CharacterProgress | null {
  if (typeof json !== 'object' || json === null) {
    return null;
  }

  const o = json as Record<string, unknown>;
  const n = (k: string): number => (typeof o[k] === 'number' ? o[k] : Number.NaN);
  const p = {
    level: n('level'),
    xp: n('xp'),
    xpLevelStart: n('xpLevelStart'),
    xpNextLevel: n('xpNextLevel'),
    killXp: n('killXp'),
    missionXp: n('missionXp'),
    maxHealth: n('maxHealth'),
  };
  return Object.values(p).every(Number.isFinite) ? p : null;
}

/**
 * The character window (client.md C39): whether it is open, on which tab, and the progress as last read — on opening and every few seconds while open.
 * Its Structures tab keeps the installations read (`useInstallations`), as the Lots window did.
 */
export interface CharacterStore {
  readonly open: boolean;
  readonly tab: CharacterTab;
  readonly progress: CharacterProgress | null;
  /** Opens it on a tab, or closes it when it is open on that tab already. */
  readonly toggle: (tab?: CharacterTab) => void;
  readonly setTab: (tab: CharacterTab) => void;
  readonly close: () => void;
  readonly refresh: () => Promise<void>;
  readonly reset: () => void;
}

/** How often the progress is read again while the window is open, ms. */
export const CHARACTER_REFRESH_MS = 10_000;

let timer: ReturnType<typeof setInterval> | null = null;
let generation = 0;

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

export const useCharacter = create<CharacterStore>((set, get) => {
  const stop = (): void => {
    if (timer !== null) {
      clearInterval(timer);
      timer = null;
    }
  };

  // The Structures tab reads the installations as the Lots window did: on showing it, and every few seconds while it shows.
  const watchStructures = (): void => {
    const installations = useInstallations.getState();
    const want = get().open && get().tab === 'structures';
    if (want && !installations.open) {
      installations.toggle();
    } else if (!want && installations.open) {
      installations.close();
    }
  };

  return {
    open: false,
    tab: 'overview',
    progress: null,
    toggle: (tab) => {
      const s = get();
      if (s.open && (tab === undefined || tab === s.tab)) {
        get().close();
        return;
      }

      set({ open: true, tab: tab ?? s.tab });
      watchStructures();
      void get().refresh();
      stop();
      timer = setInterval(() => {
        void get().refresh();
      }, CHARACTER_REFRESH_MS);
    },
    setTab: (tab) => {
      set({ tab });
      watchStructures();
    },
    close: () => {
      stop();
      set({ open: false });
      watchStructures();
    },
    refresh: async () => {
      const mine = ++generation;
      const progress = parseProgress(await fetchJson('swg/character'));
      if (progress !== null && mine === generation) {
        set({ progress });
      }
    },
    reset: () => {
      stop();
      generation++;
      set({ open: false, progress: null });
    },
  };
});
