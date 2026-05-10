import { create } from 'zustand';

/**
 * Cross-panel selection state — see `claude/design/workbench/10-internal-data-api.md §9`.
 *
 * Independently-observable dimension slots. Panels subscribe via
 * `useSelectionStore(s => s.system)` and only re-render when *that* slot changes.
 *
 * `time` is the TimeArea range in absolute µs timestamps (matches profiler convention).
 * `focusTick`, `worker`, `dataTrack`, `phase`, and `hoveredSystemTickKey` are volatile (not URL-synced);
 * the rest are stable and round-trip through the URL via {@link selectionUrlSync}.
 *
 * <b>Phase D additions (#327):</b>
 * - `dataTrack` — selecting a row in Data Flow / Access Matrix highlights its column / row in the sibling
 *   panel and every system in the System DAG that touches the row's underlying data.
 * - `phase` — selecting a phase in any view tints that phase's swim-lane / column tint in all three.
 * - `hoveredSystemTickKey` — drives the hover-to-isolate effect across panels without coupling the
 *   underlying renderers to one another.
 */
export interface TimeSelection {
  /** Inclusive start (µs). */
  start: number;
  /** Exclusive end (µs). */
  end: number;
}

/**
 * Identifier for a "data track" — a row in the Data Flow Timeline / Access Matrix that the user clicks to
 * scope downstream highlights. The `kind` discriminator lets the System DAG resolve which systems touch
 * it (e.g. `component` → systems that read/write that component name).
 */
export interface DataTrackSelection {
  readonly kind:
    | 'component'
    | 'component-family'
    | 'archetype-component'
    | 'queue'
    | 'resource'
    | 'component-domain'
    | 'queue-domain'
    | 'resource-domain';
  /** The track's stable id, matches `Track.id` in `panels/DataFlow/trackBuilding`. */
  readonly id: string;
  /** Component name when relevant — pre-resolved here so consumers don't need to re-parse the id. */
  readonly componentName?: string;
  /** Family name when relevant. */
  readonly familyName?: string;
  /** Archetype id when relevant. */
  readonly archetypeId?: number;
}

/**
 * Cross-panel hover key. When set, every panel can dim non-matching elements to highlight the
 * (system, tick) pair under the cursor. Phase D introduces this as the v1 multi-panel unification
 * mechanism — same pattern as Phase B's local hover-isolate but now panel-spanning.
 */
export interface HoveredSystemTickKey {
  readonly systemName: string;
  readonly tickNumber: number;
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
  // Phase D (#327)
  dataTrack: DataTrackSelection | null;
  phase: string | null;
  hoveredSystemTickKey: HoveredSystemTickKey | null;

  setTime: (range: TimeSelection | null) => void;
  setFocusTick: (tick: number | null) => void;
  setSystem: (name: string | null) => void;
  setComponent: (name: string | null) => void;
  setQueue: (name: string | null) => void;
  setResource: (id: string | null) => void;
  setEntity: (id: string | null) => void;
  setWorker: (id: number | null) => void;
  setDataTrack: (track: DataTrackSelection | null) => void;
  setPhase: (phase: string | null) => void;
  setHoveredSystemTickKey: (key: HoveredSystemTickKey | null) => void;

  clear: () => void;
}

const INITIAL: Pick<
  SelectionState,
  | 'time' | 'focusTick' | 'system' | 'component' | 'queue' | 'resource' | 'entity' | 'worker'
  | 'dataTrack' | 'phase' | 'hoveredSystemTickKey'
> = {
  time: null,
  focusTick: null,
  system: null,
  component: null,
  queue: null,
  resource: null,
  entity: null,
  worker: null,
  dataTrack: null,
  phase: null,
  hoveredSystemTickKey: null,
};

/** Value-equality for {@link TimeSelection}. Exported for use in cross-store bridges and URL sync. */
export function timeEqual(a: TimeSelection | null, b: TimeSelection | null): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return a.start === b.start && a.end === b.end;
}

/** Value-equality for {@link DataTrackSelection}. Used by the value-equal-aware setter. */
export function dataTrackEqual(a: DataTrackSelection | null, b: DataTrackSelection | null): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return a.kind === b.kind && a.id === b.id;
}

/** Value-equality for {@link HoveredSystemTickKey}. */
export function hoveredKeyEqual(a: HoveredSystemTickKey | null, b: HoveredSystemTickKey | null): boolean {
  if (a === b) return true;
  if (a === null || b === null) return false;
  return a.systemName === b.systemName && a.tickNumber === b.tickNumber;
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
  setDataTrack: (track) => {
    if (dataTrackEqual(get().dataTrack, track)) return;
    set({ dataTrack: track });
  },
  setPhase: (phase) => {
    if (get().phase === phase) return;
    set({ phase });
  },
  setHoveredSystemTickKey: (key) => {
    if (hoveredKeyEqual(get().hoveredSystemTickKey, key)) return;
    set({ hoveredSystemTickKey: key });
  },
  clear: () => set({ ...INITIAL }),
}));
