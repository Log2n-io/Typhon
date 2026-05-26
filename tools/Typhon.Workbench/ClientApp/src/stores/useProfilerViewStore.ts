import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { safeStorage } from './safeStorage';
import type { TimeRange, TrackState } from '@/libs/profiler/model/uiTypes';
import { useOptionsStore } from '@/stores/useOptionsStore';

/**
 * How the renderer colors span bars on slot lanes. Different lenses on the same data:
 *   - `name`     → hash span.name into the palette (default — pre-color-by-toggle behavior).
 *   - `thread`   → palette[span.threadSlot]; spots cross-thread patterns at a glance.
 *   - `depth`    → palette[span.depth]; visualizes call-stack nesting across the timeline.
 *   - `duration` → log-scale heat-map (blue → green → orange → red); makes outliers pop.
 */
export type SpanColorMode = 'name' | 'thread' | 'depth' | 'duration';

/**
 * Which palette the categorical span/chunk modes (name / thread / depth) draw from:
 *   - `categorical` → the shared DS-2 `categoricalColor` scale (stable hue-per-object across views) — default.
 *   - `curated`     → the hand-tuned 8-colour `SPAN_PALETTE` warm ramp (the pre-DS-2 flame aesthetic).
 * The `duration` heat mode is a sequential ramp and is unaffected by this choice.
 */
export type SpanPalette = 'categorical' | 'curated';

/**
 * View-state slice for the Profiler panel — viewport + toggle states + per-gauge-group collapse states.
 *
 * **Partial persistence:** toggles (gauge region visible, legends visible, per-system lanes visible) and gauge
 * collapse states persist across Workbench reopens — they're user preferences. `viewRange` does NOT persist:
 * each new session resets it to `{globalStartUs, globalEndUs}` from the metadata DTO, because a prior
 * session's viewport is meaningless on a different trace.
 */
interface ProfilerViewState {
  /**
   * Committed viewport (absolute µs timestamps). Every cross-panel consumer subscribes here:
   * SystemDag / CriticalPath / DataFlow / AccessMatrix / RangeStatsDetail / URL sync. Updated by
   * the debounced commit of {@link transientViewRange} during pan/zoom, or atomically by
   * {@link commitViewRange} for programmatic / animation-end writes. Session-scoped — not persisted.
   */
  viewRange: TimeRange;
  /**
   * In-flight viewport written on every wheel notch / drag pixel / rAF zoom-animation frame. Only
   * TimeArea (canvas + cursor / ruler / hover overlays) and TickOverview's pan handler subscribe
   * here. Committed into {@link viewRange} after `viewRangeDebounceMs` of idle (see app options).
   * Session-scoped — not persisted.
   */
  transientViewRange: TimeRange;
  /**
   * Linked/unlink scope flag (GAP-11, stage-3 Phase 3). When `true` (default), the scheduling-cluster panels
   * (System DAG / Critical Path / Data Flow) follow {@link viewRange}; when `false` they freeze on
   * {@link pinnedRange} — the window captured at unlink time — and ignore further `viewRange` changes. Consumers
   * read the resolved window via {@link selectEffectiveScope}. Session-scoped (reset on each open; not persisted).
   */
  scopeLinked: boolean;
  /** The frozen time window while unlinked (captured at the unlink toggle); `null` while linked. */
  pinnedRange: TimeRange | null;
  /** Toggled by the `g` key. Hides the full gauge region. */
  gaugeRegionVisible: boolean;
  /** Per-system chunk-lanes section visibility. */
  perSystemLanesVisible: boolean;
  /**
   * Collapse state per gauge group, keyed by group id (e.g. "gauge-gc", "gauge-memory"). Absent key = default.
   *
   * Gauges are the ONLY tracks that support the 3-state cycle (`summary | expanded | double`). Other track
   * kinds persist separately as plain booleans. v2 of this store stored collapse as boolean
   * (`true → summary`, `false → expanded`); the persist migration in the store body coerces on first load.
   */
  gaugeCollapse: Record<string, TrackState>;

  /**
   * Per-gauge-group visibility map. Keyed by gauge group id (`"gauge-gc"`, `"gauge-memory"`, …). Missing key
   * = visible. Set by the TimeArea filter popup. Persisted because gauge ids are part of the engine's static
   * schema — selections are stable across runs.
   */
  gaugeVisibility: Record<string, boolean>;
  /**
   * Per-engine-op-track visibility map. Keyed by track id (`"phases"`, `"page-cache"`, `"disk-io"`,
   * `"transactions"`, `"wal"`, `"checkpoint"`). Missing key = visible. Persisted (same reasoning as
   * `gaugeVisibility`).
   */
  engineOpVisibility: Record<string, boolean>;

  /** How spans are coloured on slot lanes. See {@link SpanColorMode}. Persisted UX preference. */
  spanColorMode: SpanColorMode;

  /** Which palette the categorical span modes draw from. See {@link SpanPalette}. Persisted UX preference. */
  spanPalette: SpanPalette;

  /** When true, each slot track height is sized to the deepest span visible in the current viewport
   *  rather than the session-wide maximum. Tracks shrink/grow as the user pans. No scroll
   *  stabilisation — heights change immediately. Persisted UX preference. */
  dynamicTrackHeight: boolean;

  /** When true, slot lanes overlay off-CPU intervals (gaps where the thread was switched out) as
   *  translucent, wait-reason-coloured bars. Default on; persisted UX preference. */
  showOffCpu: boolean;

  /**
   * Write the in-flight viewport. Updates {@link transientViewRange} immediately and schedules a
   * debounced commit into {@link viewRange}. Called on every wheel notch / drag pixel / rAF
   * animation frame — high frequency. The debounce window is read from
   * `useOptionsStore.options.profiler.viewRangeDebounceMs` (default 150 ms; 0 = synchronous commit).
   */
  setTransientViewRange: (r: TimeRange) => void;
  /**
   * Atomically write both slots, bypassing any pending debounce. Use for programmatic writes (URL
   * deep-link, "Snapshot last N ticks", animation end, metadata-arrival reset) where consumers
   * should see the change immediately rather than after the debounce window.
   */
  commitViewRange: (r: TimeRange) => void;
  /** Toggle scope linking. Unlink freezes the current {@link viewRange} into {@link pinnedRange}; relink clears it. */
  setScopeLinked: (linked: boolean) => void;
  toggleGaugeRegion: () => void;
  togglePerSystemLanes: () => void;
  setGaugeCollapse: (groupId: string, state: TrackState) => void;
  setManyGaugeCollapse: (updates: Record<string, TrackState>) => void;

  setSpanColorMode: (mode: SpanColorMode) => void;
  setSpanPalette: (p: SpanPalette) => void;
  toggleDynamicTrackHeight: () => void;
  toggleShowOffCpu: () => void;

  setGaugeVisibility: (id: string, visible: boolean) => void;
  setEngineOpVisibility: (id: string, visible: boolean) => void;
  setManyGaugeVisibility: (updates: Record<string, boolean>) => void;
  setManyEngineOpVisibility: (updates: Record<string, boolean>) => void;
  clearGaugeVisibility: () => void;
  clearEngineOpVisibility: () => void;
}


// Initial state = the "no selection" sentinel. The rest of the system treats `endUs <= startUs`
// as "nothing selected" (TickOverview hides the overlay, SystemDag/CriticalPath skip aggregations,
// ProfilerPanel's first-tick init commits the first tick). Pre-#345 this was `{0, 1_000_000}` —
// non-degenerate, which meant TimeArea's globalMetrics fallback kicked in and rendered the full
// trace by default; not great for large captures.
const INITIAL_VIEW_RANGE: TimeRange = { startUs: 0, endUs: 0 };
const DEFAULT_VIEW_RANGE_DEBOUNCE_MS = 150;

// Module-level commit timer. Shared across the store instance so successive
// `setTransientViewRange` calls coalesce into one commit. Cleared by both setters.
let commitTimer: ReturnType<typeof setTimeout> | null = null;

function readDebounceMs(): number {
  // Server clamps to [0, 5000] via the [Range] attribute on `ProfilerOptions.ViewRangeDebounceMs`;
  // the defensive clamp + Number.isFinite check below catches the (impossible) case where the
  // server is older than the client and the field is missing from the response — fall back to the
  // default rather than crash.
  const v = useOptionsStore.getState().options.profiler.viewRangeDebounceMs;
  if (typeof v !== 'number' || !Number.isFinite(v) || v < 0) return DEFAULT_VIEW_RANGE_DEBOUNCE_MS;
  return Math.min(5000, v);
}

export const useProfilerViewStore = create<ProfilerViewState>()(
  persist(
    (set, get) => ({
      viewRange: INITIAL_VIEW_RANGE,
      transientViewRange: INITIAL_VIEW_RANGE,
      scopeLinked: true,
      pinnedRange: null,
      gaugeRegionVisible: true,
      perSystemLanesVisible: true,
      gaugeCollapse: {},
      gaugeVisibility: {},
      engineOpVisibility: {},
      spanColorMode: 'name',
      spanPalette: 'categorical',
      dynamicTrackHeight: true,
      showOffCpu: true,

      setTransientViewRange: (r) => {
        set({ transientViewRange: r });
        if (commitTimer !== null) {
          clearTimeout(commitTimer);
          commitTimer = null;
        }
        const ms = readDebounceMs();
        if (ms <= 0) {
          // Synchronous commit — useful for tests, and for users who want zero-lag behaviour at
          // the cost of fluidity (configurable via the Profiler options dialog).
          set({ viewRange: r });
          return;
        }
        commitTimer = setTimeout(() => {
          commitTimer = null;
          // Read the latest transient at fire time, not the captured `r`, so the commit reflects
          // the user's final position rather than an intermediate frame.
          set({ viewRange: get().transientViewRange });
        }, ms);
      },
      commitViewRange: (r) => {
        if (commitTimer !== null) {
          clearTimeout(commitTimer);
          commitTimer = null;
        }
        set({ transientViewRange: r, viewRange: r });
      },
      setScopeLinked: (linked) =>
        set((s) => (linked
          ? { scopeLinked: true, pinnedRange: null }
          : { scopeLinked: false, pinnedRange: s.viewRange })),
      toggleGaugeRegion: () => set((s) => ({ gaugeRegionVisible: !s.gaugeRegionVisible })),
      togglePerSystemLanes: () => set((s) => ({ perSystemLanesVisible: !s.perSystemLanesVisible })),
      setGaugeCollapse: (groupId, state) =>
        set((s) => ({ gaugeCollapse: { ...s.gaugeCollapse, [groupId]: state } })),
      setManyGaugeCollapse: (updates) =>
        set((s) => {
          const next = { ...s.gaugeCollapse };
          for (const [id, state] of Object.entries(updates)) {
            next[id] = state;
          }
          return { gaugeCollapse: next };
        }),

      setSpanColorMode: (mode) => set({ spanColorMode: mode }),
      setSpanPalette: (p) => set({ spanPalette: p }),
      toggleDynamicTrackHeight: () => set((s) => ({ dynamicTrackHeight: !s.dynamicTrackHeight })),
      toggleShowOffCpu: () => set((s) => ({ showOffCpu: !s.showOffCpu })),

      setGaugeVisibility: (id, visible) =>
        set((s) => {
          if (visible) {
            if (!(id in s.gaugeVisibility)) return s;
            const next = { ...s.gaugeVisibility };
            delete next[id];
            return { gaugeVisibility: next };
          }
          if (s.gaugeVisibility[id] === false) return s;
          return { gaugeVisibility: { ...s.gaugeVisibility, [id]: false } };
        }),
      setEngineOpVisibility: (id, visible) =>
        set((s) => {
          if (visible) {
            if (!(id in s.engineOpVisibility)) return s;
            const next = { ...s.engineOpVisibility };
            delete next[id];
            return { engineOpVisibility: next };
          }
          if (s.engineOpVisibility[id] === false) return s;
          return { engineOpVisibility: { ...s.engineOpVisibility, [id]: false } };
        }),
      setManyGaugeVisibility: (updates) =>
        set((s) => {
          const next = { ...s.gaugeVisibility };
          for (const [id, v] of Object.entries(updates)) {
            if (v) delete next[id];
            else next[id] = false;
          }
          return { gaugeVisibility: next };
        }),
      setManyEngineOpVisibility: (updates) =>
        set((s) => {
          const next = { ...s.engineOpVisibility };
          for (const [id, v] of Object.entries(updates)) {
            if (v) delete next[id];
            else next[id] = false;
          }
          return { engineOpVisibility: next };
        }),
      clearGaugeVisibility: () => set({ gaugeVisibility: {} }),
      clearEngineOpVisibility: () => set({ engineOpVisibility: {} }),
    }),
    {
      name: 'workbench-profiler-view',
      storage: safeStorage,
      version: 5,
      // Only persist UX preferences; viewRange / transientViewRange are session-scoped and reset
      // on each open (never appear in `partialize`).
      partialize: (s) => ({
        gaugeRegionVisible: s.gaugeRegionVisible,
        perSystemLanesVisible: s.perSystemLanesVisible,
        gaugeCollapse: s.gaugeCollapse,
        gaugeVisibility: s.gaugeVisibility,
        engineOpVisibility: s.engineOpVisibility,
        spanColorMode: s.spanColorMode,
        spanPalette: s.spanPalette,
        dynamicTrackHeight: s.dynamicTrackHeight,
        showOffCpu: s.showOffCpu,
      }),
      // v0 → v1: gaugeCollapse changed from `Record<string, boolean>` to `Record<string, TrackState>`.
      // v1 → v2: added gaugeVisibility / engineOpVisibility maps for the section-filter popup.
      //   Defaults are empty maps (= every track visible). Pre-v2 persisted state has neither field,
      //   which the spread initializer in the constructor handles cleanly.
      // v2 → v3: added dynamicTrackHeight (default true). Missing field defaults to true.
      // v3 → v4 (#345 Step 8): dropped `liveFollowWindowUs` — live-follow mode is gone. The spread
      //   initialiser would already filter the field out of runtime state, but we delete it from
      //   the persisted blob explicitly so localStorage doesn't carry an orphan key forever.
      // v4 → v5 (#376 Phase 5): added `spanPalette` (default 'categorical'). Missing field defaults to categorical.
      migrate: (persisted: unknown, fromVersion: number): Partial<ProfilerViewState> | undefined => {
        if (!persisted || typeof persisted !== 'object') return undefined;
        const p = persisted as Partial<ProfilerViewState> & {
          gaugeCollapse?: Record<string, unknown>;
          liveFollowWindowUs?: number;
        };
        if (fromVersion < 1 && p.gaugeCollapse) {
          const migrated: Record<string, TrackState> = {};
          for (const [id, v] of Object.entries(p.gaugeCollapse)) {
            if (typeof v === 'boolean') {
              migrated[id] = v ? 'summary' : 'expanded';
            } else if (v === 'summary' || v === 'expanded' || v === 'double') {
              migrated[id] = v;
            }
            // Any other shape → drop the entry; falls back to default on first read.
          }
          p.gaugeCollapse = migrated;
        }
        if (fromVersion < 2) {
          p.gaugeVisibility = p.gaugeVisibility ?? {};
          p.engineOpVisibility = p.engineOpVisibility ?? {};
        }
        if (fromVersion < 3) {
          p.dynamicTrackHeight = p.dynamicTrackHeight ?? true;
        }
        if (fromVersion < 4) {
          delete p.liveFollowWindowUs;
        }
        if (fromVersion < 5) {
          p.spanPalette = p.spanPalette ?? 'categorical';
        }
        return p;
      },
    },
  ),
);

/**
 * The time window the scheduling-cluster panels (System DAG / Critical Path / Data Flow) should bind to: the live
 * {@link ProfilerViewState.viewRange} while linked, or the frozen {@link ProfilerViewState.pinnedRange} while
 * unlinked (GAP-11). Returns an existing object reference (never a fresh allocation), so it is safe to pass
 * directly as a zustand selector under the default `Object.is` equality — no extra `useShallow` needed.
 */
export const selectEffectiveScope = (s: ProfilerViewState): TimeRange =>
  s.scopeLinked ? s.viewRange : (s.pinnedRange ?? s.viewRange);
