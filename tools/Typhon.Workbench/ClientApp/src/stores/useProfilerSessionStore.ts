import { create } from 'zustand';
import type {
  ChunkManifestEntryDto,
  GlobalMetricsDto,
  ProfilerMetadataDto,
  TickSummaryDto,
} from '@/api/generated/model';
import type { TrackState } from '@/libs/profiler/model/uiTypes';
import type { Anomaly } from '@/panels/EngineLiveHealth/anomalies';

/**
 * Mirror of the server's BuildProgressDto. Orval regen may emit a generated type for this; wire through when available.
 */
export interface BuildProgressPayload {
  phase: 'building' | 'done' | 'error';
  bytesRead?: number;
  totalBytes?: number;
  tickCount?: number;
  eventCount?: number;
  message?: string;
}

/**
 * Producer-thread category — mirrors the engine's `Typhon.Profiler.ThreadKind` enum. Drives the filter tree's
 * Threads → {Main, Workers, Other} subgroup split.
 */
export enum ThreadKind {
  Main = 0,
  Worker = 1,
  Pool = 2,
  Other = 3,
}

/** Per-slot thread-info snapshot — server emits one per discovered (slot, name, kind) tuple. */
export interface LiveThreadInfo {
  threadSlot: number;
  name: string;
  managedThreadId: number;
  kind: ThreadKind;
}

/** Slot → cached thread name + category. Stored in {@link useProfilerSessionStore.liveThreadInfos}. */
export interface SlotThreadInfo {
  name: string;
  kind: ThreadKind;
}

/**
 * Discriminated union matching the server's `LiveStreamEventDto` (#289 unified pipeline).
 *
 * Replaces the old `LiveTickBatch` / per-tick payloads with growth deltas:
 *   - `metadata`             — full snapshot on connect / reconnect.
 *   - `tickSummaryAdded`     — one per tick the server's IncrementalCacheBuilder finalizes.
 *   - `chunkAdded`           — one per chunk the builder flushes (becomes addressable via /chunks/{idx}).
 *   - `threadInfoAdded`      — one per (slot, name, kind) the runtime observes; replayed on every (re)connect.
 *   - `globalMetricsUpdated` — ~1 Hz coalesced p95 / max / total-events refresh.
 *   - `heartbeat`            — connection-state change or 5 s idle pulse.
 *   - `shutdown`             — engine ended the session; renderer goes read-only.
 */
export type LiveStreamPayload =
  | { kind: 'metadata'; metadata: ProfilerMetadataDto }
  | { kind: 'tickSummaryAdded'; tickSummary: TickSummaryDto }
  | { kind: 'chunkAdded'; chunkEntry: ChunkManifestEntryDto }
  | { kind: 'threadInfoAdded'; threadInfo: LiveThreadInfo }
  | { kind: 'globalMetricsUpdated'; globalMetrics: GlobalMetricsDto }
  | { kind: 'heartbeat'; status: 'connecting' | 'connected' | 'reconnecting' | 'disconnected' }
  | { kind: 'shutdown'; status: string };

/** Live connection status — mirrors the server's `AttachSessionRuntime.ConnectionStatus`. */
export type ConnectionStatus = 'connecting' | 'connected' | 'reconnecting' | 'disconnected';

interface ProfilerSessionStoreState {
  /** Non-null once metadata lands. Mutable in live mode — tickSummaries / chunkManifest grow via deltas. */
  metadata: ProfilerMetadataDto | null;
  /** Latest build-progress frame from the Trace SSE stream; null until first frame arrives. */
  buildProgress: BuildProgressPayload | null;
  /** Non-null if the Trace build failed. Shown by the panel's error branch. */
  buildError: string | null;

  // ── Attach-mode live state ──────────────────────────────────────────────────────────
  /** True when the active session is `kind === 'attach'`. Panels flip UI affordances based on this. */
  isLive: boolean;
  /** Live connection status; null when no live session is active. */
  connectionStatus: ConnectionStatus | null;
  /**
   * Reason string carried by the server's `shutdown` SSE event (e.g. `"init_mismatch"` when a reconnect's Init
   * signature differs from the original session). Null for transient drops where the server emits no reason.
   * Cleared on successful reconnect (next metadata arrival OR `connectionStatus` returning to `'connected'`).
   * Drives the Stage 4 Phase 4 reconnect/shutdown banner's context-specific copy + action set (#377).
   */
  disconnectReason: string | null;
  /**
   * Highest engine tick number seen so far in the live stream. Matches the rightmost bar of the tick overview.
   */
  latestTickNumber: number;
  /**
   * Server-provided slot → (name, kind) map. Updated via `threadInfoAdded` SSE deltas; persists across
   * chunk-cache eviction so the timeline never reverts to "Slot N" labels just because a chunk got LRU'd.
   * Empty for replay traces (chunk-cache `threadNames` is the source there).
   */
  liveThreadInfos: Map<number, SlotThreadInfo>;
  /**
   * Anomalies detected by the client-side heuristics in `useAnomalyDetection` (#377 Stage 4 Phase 3,
   * GAP-21 jump). Cleared on session change via `reset()`; replaced wholesale by `setAnomalies(list)`
   * whenever the chunk-cache's tick array changes. Tick-number-ascending order; the AnomalyLog UI
   * sorts descending for "most recent first" display.
   */
  anomalies: Anomaly[];

  // ── Filter-popup ephemeral state (NOT persisted; reset on session change) ────────────
  /** Map slot index → false (hidden). Missing key = visible. Reset on `reset()`. */
  slotVisibility: Record<number, boolean>;
  /** Map system index → false (hidden). Missing key = visible. Reset on `reset()`. */
  systemVisibility: Record<number, boolean>;
  /**
   * Per-non-gauge-track collapse state — keyed by track id (`slot-N`, `system-N`, `phases`,
   * `page-cache`, `disk-io`, `transactions`, `wal`, `checkpoint`). Missing key = `'expanded'`.
   * Gauges have their own persisted map (`useProfilerViewStore.gaugeCollapse`) because they
   * support the 3-state cycle including `'double'`. This map is ephemeral so a stale slot-5
   * collapse-to-summary doesn't carry forward to a different thread on slot 5 next session.
   */
  collapseState: Record<string, TrackState>;

  setMetadata: (metadata: ProfilerMetadataDto) => void;
  setBuildProgress: (progress: BuildProgressPayload) => void;
  setBuildError: (message: string) => void;

  setIsLive: (isLive: boolean) => void;
  setConnectionStatus: (status: ConnectionStatus) => void;
  /** #289 — append a server-finalized tick summary. Mutates metadata.tickSummaries; mode-agnostic. */
  appendTickSummary: (summary: TickSummaryDto) => void;
  /** #289 — append a server-flushed chunk manifest entry. */
  appendChunkEntry: (entry: ChunkManifestEntryDto) => void;
  /** #289 — replace global metrics (1 Hz). */
  updateGlobalMetrics: (metrics: GlobalMetricsDto) => void;
  /** #289 — record a (slot → name + kind) mapping from the server's threadInfoAdded SSE delta. */
  upsertThreadInfo: (info: LiveThreadInfo) => void;
  /**
   * Coalesced version of {@link appendTickSummary}/{@link appendChunkEntry}/{@link updateGlobalMetrics}/{@link upsertThreadInfo}
   * — applies a whole rAF-frame's worth of SSE events in one `set()` call. The per-event mutators above clone the
   * `metadata` DTO + spread `tickSummaries` / `chunkManifest`, so 50 events arriving in one frame produced 50× O(N)
   * array spreads + 50× subscriber notifications. Batching collapses that to one O(N+batchSize) spread + one
   * subscriber notification per frame, eliminating live-stream stutter.
   *
   * Behaviour for each event kind is identical to the per-event mutators; this method is purely a perf optimisation
   * that callers can use when they have an array of events ready (typically the rAF flush in
   * {@link useProfilerLiveStream}). Events are applied in array order so tickSummaries / chunkManifest stay
   * monotonically ordered as the server emitted them.
   */
  applyLiveBatch: (events: LiveStreamPayload[]) => void;

  // ── Filter-popup setters ─────────────────────────────────────────────────────────────
  /** Set / clear visibility for a single slot. Pass `true` to clear (= visible / missing key); `false` to hide. */
  setSlotVisibility: (slot: number, visible: boolean) => void;
  setSystemVisibility: (idx: number, visible: boolean) => void;
  /** Batch-merge a slot-visibility map. Existing keys not in the batch are left as-is. Use `clearSlotVisibility` to fully reset. */
  setManySlotVisibility: (updates: Record<number, boolean>) => void;
  setManySystemVisibility: (updates: Record<number, boolean>) => void;
  clearSlotVisibility: () => void;
  clearSystemVisibility: () => void;

  // ── Collapse-state setters (non-gauge tracks) ───────────────────────────────────────
  setCollapseState: (id: string, state: TrackState) => void;
  setManyCollapseState: (updates: Record<string, TrackState>) => void;
  clearCollapseState: () => void;

  // ── Anomaly setters ──────────────────────────────────────────────────────────────────
  /** Replace the anomaly list wholesale — called by `useAnomalyDetection` after each cache change. */
  setAnomalies: (list: Anomaly[]) => void;

  reset: () => void;
}

export const useProfilerSessionStore = create<ProfilerSessionStoreState>()((set) => ({
  metadata: null,
  buildProgress: null,
  buildError: null,

  isLive: false,
  connectionStatus: null,
  disconnectReason: null,
  latestTickNumber: 0,
  liveThreadInfos: new Map(),
  anomalies: [],

  slotVisibility: {},
  systemVisibility: {},
  collapseState: {},

  setMetadata: (metadata) => {
    const summaries = metadata.tickSummaries ?? [];
    const last = summaries.length > 0 ? summaries[summaries.length - 1] : null;
    // Metadata arrival means the session is ready / re-ready — any previous shutdown reason no longer applies.
    return set({
      metadata,
      buildError: null,
      latestTickNumber: last ? Number(last.tickNumber) : 0,
      disconnectReason: null,
    });
  },
  setBuildProgress: (progress) => set({ buildProgress: progress }),
  setBuildError: (message) => set({ buildError: message }),

  setIsLive: (isLive) => set({ isLive }),
  // Clear disconnectReason whenever we move back to a 'connected' state — the user has effectively recovered.
  setConnectionStatus: (status) => set((s) => status === 'connected' && s.disconnectReason !== null ? { connectionStatus: status, disconnectReason: null } : { connectionStatus: status }),

  appendTickSummary: (summary) =>
    set((s) => {
      if (!s.metadata) return s;
      const tickSummaries = [...(s.metadata.tickSummaries ?? []), summary];
      const tn = Number(summary.tickNumber);
      return {
        metadata: { ...s.metadata, tickSummaries },
        latestTickNumber: tn > s.latestTickNumber ? tn : s.latestTickNumber,
      };
    }),
  appendChunkEntry: (entry) =>
    set((s) => {
      if (!s.metadata) return s;
      const chunkManifest = [...(s.metadata.chunkManifest ?? []), entry];
      return { metadata: { ...s.metadata, chunkManifest } };
    }),
  updateGlobalMetrics: (metrics) =>
    set((s) => {
      if (!s.metadata) return s;
      return { metadata: { ...s.metadata, globalMetrics: metrics } };
    }),
  upsertThreadInfo: (info) =>
    set((s) => {
      const existing = s.liveThreadInfos.get(info.threadSlot);
      if (existing && existing.name === info.name && existing.kind === info.kind) return s;
      const next = new Map(s.liveThreadInfos);
      next.set(info.threadSlot, { name: info.name, kind: info.kind });
      return { liveThreadInfos: next };
    }),
  applyLiveBatch: (events) =>
    set((s) => {
      // Walk the batch once and accumulate the deltas into local arrays / scalars. We avoid mutating the store
      // shape until the very end so subscribers see a single transition. `metadata` is the only DTO with append-shape
      // fields (tickSummaries, chunkManifest, globalMetrics); we clone it once at the end if any of those changed.
      let metadata = s.metadata;
      let liveThreadInfos = s.liveThreadInfos;
      let connectionStatus = s.connectionStatus;
      let disconnectReason = s.disconnectReason;
      let latestTickNumber = s.latestTickNumber;

      let pendingTicks: TickSummaryDto[] | null = null;
      let pendingChunks: ChunkManifestEntryDto[] | null = null;
      let pendingMetrics: GlobalMetricsDto | null = null;
      let pendingThreadInfos: Map<number, SlotThreadInfo> | null = null;

      for (const ev of events) {
        switch (ev.kind) {
          case 'metadata': {
            // Full snapshot — replaces metadata and discards any pending appends from earlier in the batch
            // (those came from a previous session that's now superseded).
            const summaries = ev.metadata.tickSummaries ?? [];
            const last = summaries.length > 0 ? summaries[summaries.length - 1] : null;
            metadata = ev.metadata;
            latestTickNumber = last ? Number(last.tickNumber) : 0;
            pendingTicks = null;
            pendingChunks = null;
            pendingMetrics = null;
            break;
          }
          case 'tickSummaryAdded': {
            (pendingTicks ??= []).push(ev.tickSummary);
            const tn = Number(ev.tickSummary.tickNumber);
            if (tn > latestTickNumber) latestTickNumber = tn;
            break;
          }
          case 'chunkAdded':
            (pendingChunks ??= []).push(ev.chunkEntry);
            break;
          case 'globalMetricsUpdated':
            // Last-wins — only the final metrics in the batch matter.
            pendingMetrics = ev.globalMetrics;
            break;
          case 'threadInfoAdded': {
            const existing = (pendingThreadInfos ?? liveThreadInfos).get(ev.threadInfo.threadSlot);
            if (existing && existing.name === ev.threadInfo.name && existing.kind === ev.threadInfo.kind) break;
            if (pendingThreadInfos === null) pendingThreadInfos = new Map(liveThreadInfos);
            pendingThreadInfos.set(ev.threadInfo.threadSlot, { name: ev.threadInfo.name, kind: ev.threadInfo.kind });
            break;
          }
          case 'heartbeat':
            connectionStatus = ev.status;
            // Recovery: a `connected` heartbeat after a recorded disconnect clears the stale reason.
            if (ev.status === 'connected' && disconnectReason !== null) {
              disconnectReason = null;
            }
            break;
          case 'shutdown':
            connectionStatus = 'disconnected';
            // Server's shutdown payload carries a reason string (e.g. `"init_mismatch"` after a reconnect's
            // Init signature differs). Preserve it so the P4 reconnect banner can render context-specific copy.
            disconnectReason = ev.status;
            break;
        }
      }

      // Apply pending mutations to metadata in one clone if any are present.
      if (metadata !== null && (pendingTicks !== null || pendingChunks !== null || pendingMetrics !== null)) {
        const nextMetadata = { ...metadata };
        if (pendingTicks !== null) {
          nextMetadata.tickSummaries = [...(metadata.tickSummaries ?? []), ...pendingTicks];
        }
        if (pendingChunks !== null) {
          nextMetadata.chunkManifest = [...(metadata.chunkManifest ?? []), ...pendingChunks];
        }
        if (pendingMetrics !== null) {
          nextMetadata.globalMetrics = pendingMetrics;
        }
        metadata = nextMetadata;
      }
      if (pendingThreadInfos !== null) {
        liveThreadInfos = pendingThreadInfos;
      }

      return { metadata, liveThreadInfos, connectionStatus, disconnectReason, latestTickNumber };
    }),

  setSlotVisibility: (slot, visible) =>
    set((s) => {
      if (visible) {
        if (!(slot in s.slotVisibility)) return s;
        const next = { ...s.slotVisibility };
        delete next[slot];
        return { slotVisibility: next };
      }
      if (s.slotVisibility[slot] === false) return s;
      return { slotVisibility: { ...s.slotVisibility, [slot]: false } };
    }),
  setSystemVisibility: (idx, visible) =>
    set((s) => {
      if (visible) {
        if (!(idx in s.systemVisibility)) return s;
        const next = { ...s.systemVisibility };
        delete next[idx];
        return { systemVisibility: next };
      }
      if (s.systemVisibility[idx] === false) return s;
      return { systemVisibility: { ...s.systemVisibility, [idx]: false } };
    }),
  setManySlotVisibility: (updates) =>
    set((s) => {
      const next = { ...s.slotVisibility };
      for (const [k, v] of Object.entries(updates)) {
        const idx = Number(k);
        if (v) delete next[idx];
        else next[idx] = false;
      }
      return { slotVisibility: next };
    }),
  setManySystemVisibility: (updates) =>
    set((s) => {
      const next = { ...s.systemVisibility };
      for (const [k, v] of Object.entries(updates)) {
        const idx = Number(k);
        if (v) delete next[idx];
        else next[idx] = false;
      }
      return { systemVisibility: next };
    }),
  clearSlotVisibility: () => set({ slotVisibility: {} }),
  clearSystemVisibility: () => set({ systemVisibility: {} }),

  setCollapseState: (id, state) =>
    set((s) => {
      if (s.collapseState[id] === state) return s;
      return { collapseState: { ...s.collapseState, [id]: state } };
    }),
  setManyCollapseState: (updates) =>
    set((s) => {
      const next = { ...s.collapseState };
      for (const [id, state] of Object.entries(updates)) {
        next[id] = state;
      }
      return { collapseState: next };
    }),
  clearCollapseState: () => set({ collapseState: {} }),

  setAnomalies: (list) =>
    set((s) => {
      // Skip the set if nothing materially changed — same length + same tickNumber sequence + same kind sequence.
      // The detector runs on every cache tick; most calls during steady state produce identical output.
      if (s.anomalies.length === list.length) {
        let same = true;
        for (let i = 0; i < list.length; i++) {
          if (s.anomalies[i].tickNumber !== list[i].tickNumber || s.anomalies[i].kind !== list[i].kind) {
            same = false;
            break;
          }
        }
        if (same) return s;
      }
      return { anomalies: list };
    }),

  reset: () =>
    set({
      metadata: null,
      buildProgress: null,
      buildError: null,
      isLive: false,
      connectionStatus: null,
      disconnectReason: null,
      latestTickNumber: 0,
      liveThreadInfos: new Map(),
      anomalies: [],
      slotVisibility: {},
      systemVisibility: {},
      collapseState: {},
    }),
}));
