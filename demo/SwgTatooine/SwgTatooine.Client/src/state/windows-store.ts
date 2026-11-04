import { create } from 'zustand';
import {
  defaultLayout,
  loadLayout,
  saveLayout,
  type BarOffset,
  type UiLayout,
  type WindowRect,
} from '../data/ui-layout';

/**
 * The interface's layout and stacking (client.md C36): each game window's rect, each bar's offset, the lock, the debug layer — kept per account —
 * and which windows are open in which order, so a click brings one to the front and Escape closes the front one.
 *
 * <b>Not whether a window is open.</b> That stays with the store each window belongs to (the bazaar's, the journal's…), whose opening reads what
 * it shows and whose closing drops it; a window registers here while it is mounted, with the way to close it.
 */
export interface WindowsState {
  /** The account the layout is kept for; `null` before a login (the god camera, the mock). */
  readonly account: string | null;
  readonly layout: UiLayout;
  /** The open windows, back to front. */
  readonly stack: readonly string[];
  /** Adopts an account's stored layout, or the default one for a first visit. */
  readonly load: (account: string | null, playing: boolean) => void;
  /** A window mounted: on top, closed by `close` when Escape asks for the front one. */
  readonly register: (id: string, close: () => void) => void;
  readonly unregister: (id: string) => void;
  /** Brings an open window to the front. */
  readonly focus: (id: string) => void;
  /** Closes the front window; false when none is open. */
  readonly closeTop: () => boolean;
  /** A window moved or resized, kept. */
  readonly place: (id: string, rect: WindowRect) => void;
  /** A bar moved, kept. */
  readonly moveBar: (id: string, offset: BarOffset) => void;
  readonly sizeChat: (w: number, h: number) => void;
  readonly setLocked: (locked: boolean) => void;
  readonly setDebug: (debug: boolean) => void;
  readonly toggleDebug: () => void;
  /** Shows or leaves out a kind of row in the waypoint pane (C38). */
  readonly setNavShown: (key: string, shown: boolean) => void;
  /** Every window back where it opens by default, every bar where it is drawn; the lock and the debug layer as they were. */
  readonly resetLayout: () => void;
  /** The Options window (client.md C36): the player's settings, the lock, the debug layer, the reset. */
  readonly optionsOpen: boolean;
  readonly toggleOptions: () => void;
  readonly closeOptions: () => void;
}

// The way each open window closes: functions, so not state.
const closers = new Map<string, () => void>();

export const useWindows = create<WindowsState>((set, get) => {
  const keep = (layout: UiLayout): void => {
    set({ layout });
    saveLayout(get().account, layout);
  };

  return {
    account: null,
    layout: defaultLayout(false),
    stack: [],
    load: (account, playing) => {
      set({ account, layout: loadLayout(account, defaultLayout(playing)) });
    },
    register: (id, close) => {
      closers.set(id, close);
      set((s) => ({ stack: [...s.stack.filter((w) => w !== id), id] }));
    },
    unregister: (id) => {
      closers.delete(id);
      set((s) => ({ stack: s.stack.filter((w) => w !== id) }));
    },
    focus: (id) => {
      const { stack } = get();
      if (stack.includes(id) && stack[stack.length - 1] !== id) {
        set({ stack: [...stack.filter((w) => w !== id), id] });
      }
    },
    closeTop: () => {
      const { stack } = get();
      const top = stack.at(-1);
      if (top === undefined) {
        return false;
      }

      closers.get(top)?.();
      return true;
    },
    place: (id, rect) => {
      const { layout } = get();
      keep({ ...layout, windows: { ...layout.windows, [id]: rect } });
    },
    moveBar: (id, offset) => {
      const { layout } = get();
      keep({ ...layout, bars: { ...layout.bars, [id]: offset } });
    },
    sizeChat: (w, h) => {
      keep({ ...get().layout, chatW: w, chatH: h });
    },
    setLocked: (locked) => {
      keep({ ...get().layout, locked });
    },
    setDebug: (debug) => {
      keep({ ...get().layout, debug });
    },
    toggleDebug: () => {
      keep({ ...get().layout, debug: !get().layout.debug });
    },
    setNavShown: (key, shown) => {
      const { layout } = get();
      const hidden = layout.navHidden.filter((k) => k !== key);
      keep({ ...layout, navHidden: shown ? hidden : [...hidden, key] });
    },
    resetLayout: () => {
      const { layout } = get();
      keep({ ...layout, windows: {}, bars: {}, chatW: 0, chatH: 0 });
    },
    optionsOpen: false,
    toggleOptions: () => {
      set((s) => ({ optionsOpen: !s.optionsOpen }));
    },
    closeOptions: () => {
      set({ optionsOpen: false });
    },
  };
});

/** Where a window stands in the stack, for its z-index: 0 at the back. -1 when it is not open. */
export function stackIndex(stack: readonly string[], id: string): number {
  return stack.indexOf(id);
}
