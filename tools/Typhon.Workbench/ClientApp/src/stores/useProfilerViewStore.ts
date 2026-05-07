import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';
import type { TimeRange, TrackState } from '@/libs/profiler/model/uiTypes';

/**
 * How the renderer colors span bars on slot lanes. Different lenses on the same data:
 *   - `name`     → hash span.name into the palette (default — pre-color-by-toggle behavior).
 *   - `thread`   → palette[span.threadSlot]; spots cross-thread patterns at a glance.
 *   - `depth`    → palette[span.depth]; visualizes call-stack nesting across the timeline.
 *   - `duration` → log-scale heat-map (blue → green → orange → red); makes outliers pop.
 */
export type SpanColorMode = 'name' | 'thread' | 'depth' | 'duration';

/**
 * View-state slice for the Profiler panel — viewport + toggle states + per-gauge-group collapse states.
 *
 * **Partial persistence:** toggles (gauge region visible, legends visible, per-system lanes visible) and gauge
 * collapse states persist across Workbench reopens — they're user preferences. `viewRange` does NOT persist:
 * each new session resets it to `{globalStartUs, globalEndUs}` from the metadata DTO, because a prior
 * session's viewport is meaningless on a different trace.
 */
interface ProfilerViewState {
  /** Current viewport (absolute µs timestamps). Session-scoped — not persisted. */
  viewRange: TimeRange;
  /**
   * Width of the auto-follow window in µs (live mode). When `liveFollowActive` is true and a new
   * tick lands, `ProfilerPanel` sets `viewRange = [latest.endUs - liveFollowWindowUs, latest.endUs]`.
   * Persisted as a UX preference — different traces / engines expose different timescales and the
   * user may want a wider/narrower follow window per workload.
   */
  liveFollowWindowUs: number;
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

  /** When true, each slot track height is sized to the deepest span visible in the current viewport
   *  rather than the session-wide maximum. Tracks shrink/grow as the user pans. No scroll
   *  stabilisation — heights change immediately. Persisted UX preference. */
  dynamicTrackHeight: boolean;

  setViewRange: (r: TimeRange) => void;
  setLiveFollowWindowUs: (us: number) => void;
  toggleGaugeRegion: () => void;
  togglePerSystemLanes: () => void;
  setGaugeCollapse: (groupId: string, state: TrackState) => void;
  setManyGaugeCollapse: (updates: Record<string, TrackState>) => void;

  setSpanColorMode: (mode: SpanColorMode) => void;
  toggleDynamicTrackHeight: () => void;

  setGaugeVisibility: (id: string, visible: boolean) => void;
  setEngineOpVisibility: (id: string, visible: boolean) => void;
  setManyGaugeVisibility: (updates: Record<string, boolean>) => void;
  setManyEngineOpVisibility: (updates: Record<string, boolean>) => void;
  clearGaugeVisibility: () => void;
  clearEngineOpVisibility: () => void;
}

// Safe localStorage wrapper — falls back silently in non-browser environments (tests, SSR).
const safeStorage = createJSONStorage(() => ({
  getItem: (name: string) => {
    try { return localStorage.getItem(name); } catch { return null; }
  },
  setItem: (name: string, value: string) => {
    try { localStorage.setItem(name, value); } catch { /* noop */ }
  },
  removeItem: (name: string) => {
    try { localStorage.removeItem(name); } catch { /* noop */ }
  },
}));

const INITIAL_VIEW_RANGE: TimeRange = { startUs: 0, endUs: 1_000_000 };
const DEFAULT_LIVE_FOLLOW_WINDOW_US = 1_000_000; // 1 s of history visible while live-following.

export const useProfilerViewStore = create<ProfilerViewState>()(
  persist(
    (set) => ({
      viewRange: INITIAL_VIEW_RANGE,
      liveFollowWindowUs: DEFAULT_LIVE_FOLLOW_WINDOW_US,
      gaugeRegionVisible: true,
      perSystemLanesVisible: true,
      gaugeCollapse: {},
      gaugeVisibility: {},
      engineOpVisibility: {},
      spanColorMode: 'name',
      dynamicTrackHeight: true,

      setViewRange: (r) => set({ viewRange: r }),
      setLiveFollowWindowUs: (us) => set({ liveFollowWindowUs: Math.max(1, us) }),
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
      toggleDynamicTrackHeight: () => set((s) => ({ dynamicTrackHeight: !s.dynamicTrackHeight })),

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
      version: 3,
      // Only persist UX preferences; viewRange is session-scoped and reset on each open.
      partialize: (s) => ({
        liveFollowWindowUs: s.liveFollowWindowUs,
        gaugeRegionVisible: s.gaugeRegionVisible,
        perSystemLanesVisible: s.perSystemLanesVisible,
        gaugeCollapse: s.gaugeCollapse,
        gaugeVisibility: s.gaugeVisibility,
        engineOpVisibility: s.engineOpVisibility,
        spanColorMode: s.spanColorMode,
        dynamicTrackHeight: s.dynamicTrackHeight,
      }),
      // v0 → v1: gaugeCollapse changed from `Record<string, boolean>` to `Record<string, TrackState>`.
      // v1 → v2: added gaugeVisibility / engineOpVisibility maps for the section-filter popup.
      //   Defaults are empty maps (= every track visible). Pre-v2 persisted state has neither field,
      //   which the spread initializer in the constructor handles cleanly.
      // v2 → v3: added dynamicTrackHeight (default true). Missing field defaults to true.
      migrate: (persisted: unknown, fromVersion: number): Partial<ProfilerViewState> | undefined => {
        if (!persisted || typeof persisted !== 'object') return undefined;
        const p = persisted as Partial<ProfilerViewState> & { gaugeCollapse?: Record<string, unknown> };
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
        return p;
      },
    },
  ),
);
