import {
  assembleTickViewAndNumbers,
  computePendingRangesUs,
  createChunkCache,
  ensureRangeLoaded,
  type ChunkCacheState,
  viewRangeToTickRange,
} from '@/libs/profiler/cache/chunkCache';
import {
  convertChunkManifest,
  convertProfilerMetadata,
  convertTickSummaries,
} from '@/libs/profiler/cache/dtoConverters';
import {
  type GaugeSeries,
  type GcEvent,
  type GcSuspensionEvent,
  type MemoryAllocEventData,
  type OffCpuStore,
  type TickData,
} from '@/libs/profiler/model/traceModel';
import type { ChunkManifestEntry, GaugeId, TickSummary, TraceMetadata } from '@/libs/profiler/model/types';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';

/**
 * Profiler chunk-cache **registry** — one cache instance per `sessionId`, shared across every
 * consumer (`useProfilerCache` callers) in the dock tree. Solves the Stage-4 perf regression where
 * three independent `useProfilerCache` hook instances (`ProfilerPanel`, `EngineHealthScalars`,
 * `useAnomalyDetection`) each ran their own chunk decode + `assembleTickViewAndNumbers` +
 * `aggregateGaugeData` pipeline on every `chunkAdded` SSE event — making the main-thread Task
 * width ~120 ms per chunk and starving Dockview's pointermove handler (the "Detail-pane resize
 * lag" symptom).
 *
 * **Lifecycle.** Consumers `acquireSessionCache(sessionId, isLive)` on mount and `releaseSessionCache`
 * on unmount; the registry refcounts and destroys the entry (aborts in-flight + unsubscribes from
 * the stores) when the count hits 0. Inside, the entry subscribes to `useProfilerSessionStore` (for
 * metadata + liveThreadInfos) and `useProfilerViewStore` (for viewRange) — **once** per session,
 * not once per consumer. The expensive `assembleTickViewAndNumbers` runs **once** per `entriesVersion`
 * bump; the resulting snapshot is shared via `useSyncExternalStore` semantics (stable reference until
 * something materially changes, then a single notification fans out to all listeners).
 *
 * **Snapshot stability.** `getSnapshot(entry)` returns a memoised object that only re-allocates when
 * a content field has changed. This is required by `useSyncExternalStore` — calling `getSnapshot`
 * multiple times within one render must yield the same reference.
 */

// ── Public snapshot shape — mirrors the historical useProfilerCache return so consumers don't change.
export interface ProfilerGaugeData {
  gaugeSeries: Map<GaugeId, GaugeSeries>;
  gaugeCapacities: Map<GaugeId, number>;
  memoryAllocEvents: MemoryAllocEventData[];
  gcEvents: GcEvent[];
  gcSuspensions: GcSuspensionEvent[];
  threadNames: Map<number, string>;
  /** Slot → off-CPU interval store (cross-tick). Empty when scheduling data is absent. */
  offCpuBySlot: Map<number, OffCpuStore>;
}

export interface SlotThreadInfo {
  name: string;
  kind: number;
}

export interface ProfilerCacheSnapshot {
  ticks: TickData[];
  traceMetadata: TraceMetadata | null;
  gaugeData: ProfilerGaugeData;
  threadInfos: Map<number, SlotThreadInfo>;
  pendingRangesUs: Array<{ startUs: number; endUs: number }>;
}

const EMPTY_GAUGE_DATA: ProfilerGaugeData = {
  gaugeSeries: new Map(),
  gaugeCapacities: new Map(),
  memoryAllocEvents: [],
  gcEvents: [],
  gcSuspensions: [],
  threadNames: new Map(),
  offCpuBySlot: new Map(),
};

const EMPTY_SNAPSHOT: ProfilerCacheSnapshot = Object.freeze({
  ticks: [],
  traceMetadata: null,
  gaugeData: EMPTY_GAUGE_DATA,
  threadInfos: new Map<number, SlotThreadInfo>(),
  pendingRangesUs: [],
}) as ProfilerCacheSnapshot;

// ── Internal per-session entry ──────────────────────────────────────────────────────────────────
interface SessionCacheEntry {
  sessionId: string;
  isLive: boolean;
  refCount: number;

  // Identity for cache invalidation. Replay traces flip fingerprint when the file changes; live
  // sessions stay empty — handled by `setMetadata`.
  fingerprint: string;
  cache: ChunkCacheState;

  // Derived from the metadata DTO (recomputed in `setMetadata`).
  metadataDto: ReturnType<typeof useProfilerSessionStore.getState>['metadata'];
  traceMetadata: TraceMetadata | null;
  manifest: ChunkManifestEntry[];
  tickSummaries: TickSummary[];

  // Live state (recomputed when the store mutates).
  liveThreadInfos: Map<number, SlotThreadInfo>;
  viewRange: TimeRange;

  // Assembly output — recomputed only when the underlying chunk cache actually grew. We gate on
  // `cache.entriesVersion` (the chunk cache's own monotonic counter, bumped exactly once per
  // chunk add/remove — see chunkCache.ts:547,678): the assembler is internally memoised on that
  // same key, so if our `lastBumpedCacheVersion` already matches `cache.entriesVersion`, calling
  // it again would return the SAME reference and the only observable effect would be a spurious
  // `markDirty` notification (re-rendering every `useProfilerCache` consumer for an unchanged
  // snapshot). #4b — gate on cache version, skip the notification.
  assembled: ReturnType<typeof assembleTickViewAndNumbers> | null;
  lastBumpedCacheVersion: number;

  // rAF coalescing for `bumpEntriesVersion` (#4a). Per `chunkAdded` SSE event, both `loadViewRange`
  // and `runLiveTailPrefetch` call `bumpEntriesVersion` via independent `ensureRangeLoaded.then(...)`
  // chains — so today every chunk arrival fires TWO assemblies + TWO markDirty cascades. We collapse
  // them into one per frame: if a bump is already scheduled, additional calls no-op and the queued
  // rAF runs against the latest entry state (cache is shared mutable, the closure reads it at fire
  // time). `null` means no bump is scheduled.
  pendingBumpRafId: number | null;

  // Memoised snapshot (the publicly visible thing).
  snapshot: ProfilerCacheSnapshot;
  snapshotDirty: boolean;

  // In-flight load. Aborted when superseded by a new viewRange / fingerprint / release.
  inFlightController: AbortController | null;

  // Eager chunk-0 prefetch guard — fires once per cache lifetime.
  eagerChunkLoaded: boolean;
  // Live-tail prefetch — re-fires on every manifest growth (idempotent against already-loaded chunks).
  lastPrefetchedTailIndex: number;

  // Subscribers.
  listeners: Set<() => void>;

  // Unsubscribe handles from the two stores.
  unsubscribeSession: () => void;
  unsubscribeView: () => void;
}

const registry = new Map<string, SessionCacheEntry>();

// ── Public API ──────────────────────────────────────────────────────────────────────────────────

export function acquireSessionCache(sessionId: string, isLive: boolean): SessionCacheEntry {
  let entry = registry.get(sessionId);
  if (entry) {
    entry.refCount += 1;
    // If a later consumer asks for live mode while the earlier one wanted replay, upgrade — the
    // live-tail prefetch effect only adds work, doesn't conflict with replay loads.
    if (isLive && !entry.isLive) {
      entry.isLive = true;
      // Re-run live-tail prefetch immediately if the manifest already grew while we weren't watching.
      runLiveTailPrefetch(entry);
    }
    return entry;
  }
  entry = createEntry(sessionId, isLive);
  registry.set(sessionId, entry);
  return entry;
}

export function releaseSessionCache(sessionId: string): void {
  const entry = registry.get(sessionId);
  if (!entry) return;
  entry.refCount -= 1;
  if (entry.refCount > 0) return;
  destroyEntry(entry);
  registry.delete(sessionId);
}

export function subscribeSessionCache(entry: SessionCacheEntry, listener: () => void): () => void {
  entry.listeners.add(listener);
  return () => { entry.listeners.delete(listener); };
}

export function getSessionCacheSnapshot(entry: SessionCacheEntry): ProfilerCacheSnapshot {
  if (entry.snapshotDirty) {
    entry.snapshot = computeSnapshot(entry);
    entry.snapshotDirty = false;
  }
  return entry.snapshot;
}

/** Test-only: drop every registered entry. Used by tests to start each case from a clean slate. */
export function _resetRegistry(): void {
  for (const entry of registry.values()) {
    destroyEntry(entry);
  }
  registry.clear();
}

/** Test-only: peek registered sessionIds. */
export function _registeredSessionIds(): readonly string[] {
  return Array.from(registry.keys());
}

/**
 * Internal accessor for `useProfilerCache`'s render path — returns the entry directly by sessionId
 * without acquiring (refCount untouched). Returns null when no consumer has acquired this session
 * yet. Underscore-prefixed because it's intended only for the hook wrapper.
 */
export function _entryByIdOrNull(sessionId: string): SessionCacheEntry | null {
  return registry.get(sessionId) ?? null;
}

/**
 * Test-only — schedule a bump through the public rAF-coalesce path. Used by registry tests to
 * exercise the coalesce behaviour with a stubbed `requestAnimationFrame`.
 */
export function _scheduleBumpForTest(entry: SessionCacheEntry): void {
  bumpEntriesVersion(entry);
}

/**
 * Test-only — run a bump synchronously, bypassing the rAF gate. Lets tests assert the
 * `cache.entriesVersion`-based dedup logic (#4b) without juggling animation frames.
 */
export function _bumpImmediateForTest(entry: SessionCacheEntry): void {
  bumpEntriesVersionImmediate(entry);
}

/**
 * Test-only — peek the entry's pending-rAF state so the rAF-coalesce contract is observable from
 * the test (e.g., "N scheduled calls produced exactly 1 pending rAF").
 */
export function _pendingBumpRafIdForTest(entry: SessionCacheEntry): number | null {
  return entry.pendingBumpRafId;
}

// ── Internal lifecycle ──────────────────────────────────────────────────────────────────────────

function createEntry(sessionId: string, isLive: boolean): SessionCacheEntry {
  const sessionStore = useProfilerSessionStore.getState();
  const viewStore = useProfilerViewStore.getState();
  const metadataDto = sessionStore.metadata;
  const fingerprint = metadataDto?.fingerprint ?? '';

  const entry: SessionCacheEntry = {
    sessionId,
    isLive,
    refCount: 1,
    fingerprint,
    cache: createChunkCache(),
    metadataDto,
    traceMetadata: null,
    manifest: [],
    tickSummaries: [],
    liveThreadInfos: new Map(sessionStore.liveThreadInfos),
    viewRange: viewStore.viewRange,
    assembled: null,
    lastBumpedCacheVersion: -1,
    pendingBumpRafId: null,
    snapshot: EMPTY_SNAPSHOT,
    snapshotDirty: false,
    inFlightController: null,
    eagerChunkLoaded: false,
    lastPrefetchedTailIndex: -1,
    listeners: new Set(),
    unsubscribeSession: () => {},
    unsubscribeView: () => {},
  };

  // Subscribe to the session store: metadata growth (tickSummaries, chunkManifest, globalMetrics) +
  // liveThreadInfos. Recomputes derived state + triggers loads as needed.
  entry.unsubscribeSession = useProfilerSessionStore.subscribe((state) => {
    let changed = false;
    // Metadata DTO reference flips on every applyLiveBatch with appended ticks/chunks/metrics. We
    // detect this by reference equality; the work inside guards against no-op duplicates.
    if (state.metadata !== entry.metadataDto) {
      entry.metadataDto = state.metadata;
      const nextFingerprint = state.metadata?.fingerprint ?? '';
      if (nextFingerprint !== entry.fingerprint) {
        // Fingerprint shifted (replay only — live sessions stay empty). Reset the cache and the
        // per-entry derived state. `lastBumpedCacheVersion` resets to -1 so the next bump always
        // proceeds (the new cache starts at `entriesVersion = 0`, which a -1 sentinel guarantees
        // we treat as "newer than what we last assembled").
        entry.fingerprint = nextFingerprint;
        entry.cache = createChunkCache();
        entry.assembled = null;
        entry.lastBumpedCacheVersion = -1;
        entry.eagerChunkLoaded = false;
        entry.lastPrefetchedTailIndex = -1;
        entry.inFlightController?.abort();
        entry.inFlightController = null;
      }
      // Recompute the derived shapes (cheap; small per-tick struct conversions).
      entry.traceMetadata = state.metadata !== null ? convertProfilerMetadata(state.metadata) : null;
      entry.manifest = state.metadata !== null ? convertChunkManifest(state.metadata.chunkManifest) : [];
      entry.tickSummaries = state.metadata !== null ? convertTickSummaries(state.metadata.tickSummaries) : [];
      changed = true;
      // Trigger eager chunk-0 + live-tail prefetch in response to the new manifest shape.
      runEagerChunkPrefetch(entry);
      if (entry.isLive) runLiveTailPrefetch(entry);
      // Also: a manifest grew → the viewRange may now have chunks available. Re-issue the load.
      if (entry.viewRange.endUs > entry.viewRange.startUs) {
        void loadViewRange(entry);
      }
    }
    if (state.liveThreadInfos !== entry.liveThreadInfos) {
      entry.liveThreadInfos = state.liveThreadInfos;
      changed = true;
    }
    if (changed) markDirty(entry);
  });

  // Subscribe to the view store: viewRange drives chunk-cache loading.
  entry.unsubscribeView = useProfilerViewStore.subscribe((state) => {
    if (state.viewRange !== entry.viewRange) {
      entry.viewRange = state.viewRange;
      void loadViewRange(entry);
      markDirty(entry);
    }
  });

  // Kick off the initial loads + assembly from whatever the stores hold at acquire time.
  if (entry.metadataDto !== null) {
    entry.traceMetadata = convertProfilerMetadata(entry.metadataDto);
    entry.manifest = convertChunkManifest(entry.metadataDto.chunkManifest);
    entry.tickSummaries = convertTickSummaries(entry.metadataDto.tickSummaries);
    runEagerChunkPrefetch(entry);
    if (entry.isLive) runLiveTailPrefetch(entry);
    if (entry.viewRange.endUs > entry.viewRange.startUs) void loadViewRange(entry);
  }
  markDirty(entry);

  return entry;
}

function destroyEntry(entry: SessionCacheEntry): void {
  entry.inFlightController?.abort();
  entry.inFlightController = null;
  // Cancel any rAF-scheduled bump so it can't fire after the entry is gone (which would walk a
  // detached cache + stores). Symmetric with `inFlightController.abort()`.
  if (entry.pendingBumpRafId !== null) {
    cancelAnimationFrame(entry.pendingBumpRafId);
    entry.pendingBumpRafId = null;
  }
  entry.unsubscribeSession();
  entry.unsubscribeView();
  entry.listeners.clear();
}

function markDirty(entry: SessionCacheEntry): void {
  entry.snapshotDirty = true;
  // Notify subscribers — they'll call getSnapshot() which will (lazily) recompute.
  for (const l of entry.listeners) l();
}

// ── Load drivers ────────────────────────────────────────────────────────────────────────────────

function runEagerChunkPrefetch(entry: SessionCacheEntry): void {
  if (entry.eagerChunkLoaded) return;
  if (entry.traceMetadata === null || entry.manifest.length === 0) return;
  const firstChunk = entry.manifest[0];
  if (firstChunk === undefined) return;
  entry.eagerChunkLoaded = true;
  void ensureRangeLoaded(
    entry.cache, entry.sessionId, entry.traceMetadata, entry.manifest,
    firstChunk.fromTick, firstChunk.toTick,
    undefined, undefined,
    undefined,
  ).then(() => {
    bumpEntriesVersion(entry);
  }).catch((err) => {
    if (isAbortError(err)) return;
    console.warn('[profilerCacheRegistry] eager chunk-0 load failed:', err);
  });
}

function runLiveTailPrefetch(entry: SessionCacheEntry): void {
  if (!entry.isLive) return;
  if (entry.traceMetadata === null || entry.manifest.length === 0) return;
  const tailIdx = entry.manifest.length - 1;
  if (tailIdx === entry.lastPrefetchedTailIndex) return;
  const last = entry.manifest[tailIdx];
  if (last === undefined) return;
  entry.lastPrefetchedTailIndex = tailIdx;
  void ensureRangeLoaded(
    entry.cache, entry.sessionId, entry.traceMetadata, entry.manifest,
    last.fromTick, last.toTick,
    undefined, undefined,
    undefined,
  ).then(() => {
    bumpEntriesVersion(entry);
  }).catch(() => { /* live-tail is best-effort; the next manifest update retries */ });
}

async function loadViewRange(entry: SessionCacheEntry): Promise<void> {
  if (entry.traceMetadata === null || entry.manifest.length === 0 || entry.tickSummaries.length === 0) return;
  const tr = viewRangeToTickRange(entry.tickSummaries, entry.viewRange.startUs, entry.viewRange.endUs);
  if (!tr) return;

  entry.inFlightController?.abort();
  const ac = new AbortController();
  entry.inFlightController = ac;

  try {
    await ensureRangeLoaded(
      entry.cache, entry.sessionId, entry.traceMetadata, entry.manifest,
      tr.fromTick, tr.toTick,
      undefined, undefined,
      ac.signal,
    );
    if (!ac.signal.aborted) bumpEntriesVersion(entry);
  } catch (err) {
    if (isAbortError(err)) return;
    console.warn('[profilerCacheRegistry] ensureRangeLoaded failed:', err);
  }
}

/**
 * Schedule a rebuild of `entry.assembled` + a `markDirty` notification. **rAF-coalesced** (#4a):
 * multiple calls inside the same frame collapse into a single rebuild — the rAF closure reads
 * `entry.*` at fire time, so the latest cache state always wins. Today every `chunkAdded` SSE
 * event triggers TWO bump calls (one from `loadViewRange.then`, one from `runLiveTailPrefetch.then`)
 * that race against each other; coalescing cuts that to one per frame.
 *
 * The actual work happens in `bumpEntriesVersionImmediate`, which additionally gates on
 * `cache.entriesVersion` (#4b) — see that function's note.
 */
function bumpEntriesVersion(entry: SessionCacheEntry): void {
  if (entry.pendingBumpRafId !== null) return;
  entry.pendingBumpRafId = requestAnimationFrame(() => {
    entry.pendingBumpRafId = null;
    bumpEntriesVersionImmediate(entry);
  });
}

/**
 * The synchronous side of `bumpEntriesVersion`. Skips the rebuild + notify when the chunk cache
 * hasn't grown since our last assembly (#4b): `assembleTickViewAndNumbers` is internally memoised
 * on `cache.entriesVersion` (chunkCache.ts:282–284) so it would return the same reference; the
 * cost we'd otherwise pay is the `markDirty` cascade — every `useProfilerCache` consumer rendering
 * against a freshly-allocated outer snapshot whose substantive content is identical. Net effect in
 * scroll-back mode: a `chunkAdded` whose chunks are outside the current viewport (so loadViewRange
 * is a cache-hit no-op) produces zero re-renders.
 *
 * Exported as `_bumpEntriesVersionImmediateForTest` below so the registry tests can drive bumps
 * deterministically without a real rAF loop.
 */
function bumpEntriesVersionImmediate(entry: SessionCacheEntry): void {
  if (entry.assembled !== null && entry.lastBumpedCacheVersion === entry.cache.entriesVersion) {
    return;
  }
  entry.lastBumpedCacheVersion = entry.cache.entriesVersion;
  if (entry.traceMetadata === null) {
    entry.assembled = null;
  } else {
    entry.assembled = assembleTickViewAndNumbers(entry.cache, entry.traceMetadata.systems);
  }
  markDirty(entry);
}

// ── Snapshot computation ────────────────────────────────────────────────────────────────────────

function computeSnapshot(entry: SessionCacheEntry): ProfilerCacheSnapshot {
  if (entry.assembled === null) {
    // No data yet — return a snapshot with the latest threadInfos merged (live deltas may arrive
    // before chunks land, so the names should still surface even with empty ticks).
    return {
      ticks: [],
      traceMetadata: entry.traceMetadata,
      gaugeData: { ...EMPTY_GAUGE_DATA, threadNames: mergedThreadNames(null, entry.liveThreadInfos) },
      threadInfos: mergedThreadInfos(null, entry.liveThreadInfos, entry.isLive),
      pendingRangesUs: [],
    };
  }
  const assembled = entry.assembled;
  return {
    ticks: assembled.tickData,
    traceMetadata: entry.traceMetadata,
    gaugeData: {
      gaugeSeries: assembled.gaugeSeries,
      gaugeCapacities: assembled.gaugeCapacities,
      memoryAllocEvents: assembled.memoryAllocEvents,
      gcEvents: assembled.gcEvents,
      gcSuspensions: assembled.gcSuspensions,
      threadNames: mergedThreadNames(assembled.threadNames, entry.liveThreadInfos),
      offCpuBySlot: assembled.offCpuBySlot,
    },
    threadInfos: mergedThreadInfos(assembled.threadNames, entry.liveThreadInfos, entry.isLive, assembled.threadKinds),
    pendingRangesUs: entry.viewRange.endUs > entry.viewRange.startUs
      ? computePendingRangesUs(entry.cache, entry.manifest, entry.tickSummaries, entry.viewRange)
      : [],
  };
}

/**
 * Merge slot→name maps: chunk-cache names + live SSE deltas. Live wins for the kinds it carries —
 * chunk-cache provides names for past chunks (post-eviction) and pre-tick replay; the live store
 * has names for slots whose `ThreadInfo` record is still buried in a chunk the viewer hasn't loaded.
 */
function mergedThreadNames(
  fromCache: Map<number, string> | null,
  fromLive: Map<number, SlotThreadInfo>,
): Map<number, string> {
  const out = new Map<number, string>(fromCache ?? []);
  for (const [slot, info] of fromLive) {
    if (info.name) out.set(slot, info.name);
  }
  return out;
}

function mergedThreadInfos(
  fromCache: Map<number, string> | null,
  fromLive: Map<number, SlotThreadInfo>,
  isLive: boolean,
  threadKinds?: Map<number, number>,
): Map<number, SlotThreadInfo> {
  const out = new Map<number, SlotThreadInfo>();
  if (fromCache) {
    for (const [slot, name] of fromCache) {
      out.set(slot, { name, kind: threadKinds?.get(slot) ?? 3 /* Other */ });
    }
  }
  if (isLive) {
    for (const [slot, info] of fromLive) {
      out.set(slot, info);
    }
  }
  return out;
}

function isAbortError(err: unknown): boolean {
  return err !== null && typeof err === 'object' && 'name' in err && (err as { name: string }).name === 'AbortError';
}
