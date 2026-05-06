import { create } from 'zustand';

/**
 * Cross-panel selection state — see `claude/design/workbench/10-internal-data-api.md §9`.
 *
 * Eight independently-observable dimension slots. Panels subscribe via
 * `useSelectionStore(s => s.system)` and only re-render when *that* slot changes.
 *
 * `time` is the TimeArea range in absolute µs timestamps (matches profiler convention).
 * `focusTick` and `worker` are volatile (not URL-synced); the rest are stable and
 * round-trip through the URL via {@link selectionUrlSync}.
 */
export interface TimeSelection {
  /** Inclusive start (µs). */
  start: number;
  /** Exclusive end (µs). */
  end: number;
}

export interface SelectionState {
  time: TimeSelection | null;
  focusTick: number | null;
  system: string | null;
  component: string | null;
  queue: string | null;
  resource: string | null;
  entity: string | null;
  worker: number | null;

  setTime: (range: TimeSelection | null) => void;
  setFocusTick: (tick: number | null) => void;
  setSystem: (name: string | null) => void;
  setComponent: (name: string | null) => void;
  setQueue: (name: string | null) => void;
  setResource: (id: string | null) => void;
  setEntity: (id: string | null) => void;
  setWorker: (id: number | null) => void;

  clear: () => void;
}

const INITIAL: Pick<
  SelectionState,
  'time' | 'focusTick' | 'system' | 'component' | 'queue' | 'resource' | 'entity' | 'worker'
> = {
  time: null,
  focusTick: null,
  system: null,
  component: null,
  queue: null,
  resource: null,
  entity: null,
  worker: null,
};

/** Value-equality for {@link TimeSelection}. Exported for use in cross-store bridges and URL sync. */
export function timeEqual(a: TimeSelection | null, b: TimeSelection | null): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return a.start === b.start && a.end === b.end;
}

export const useSelectionStore = create<SelectionState>()((set, get) => ({
  ...INITIAL,
  // Setters are value-equal-aware: writing the current value is a silent no-op so subscribers
  // (especially the URL-sync mirror) don't fire on idempotent writes from the bridges.
  setTime: (range) => {
    if (timeEqual(get().time, range)) return;
    set({ time: range });
  },
  setFocusTick: (tick) => {
    if (get().focusTick === tick) return;
    set({ focusTick: tick });
  },
  setSystem: (name) => {
    if (get().system === name) return;
    set({ system: name });
  },
  setComponent: (name) => {
    if (get().component === name) return;
    set({ component: name });
  },
  setQueue: (name) => {
    if (get().queue === name) return;
    set({ queue: name });
  },
  setResource: (id) => {
    if (get().resource === id) return;
    set({ resource: id });
  },
  setEntity: (id) => {
    if (get().entity === id) return;
    set({ entity: id });
  },
  setWorker: (id) => {
    if (get().worker === id) return;
    set({ worker: id });
  },
  clear: () => set({ ...INITIAL }),
}));
