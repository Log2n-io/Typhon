/**
 * Memory-bounded LRU cache over trace chunks. Keeps the client from ever materializing the whole trace — only the chunks overlapping the
 * current viewport (plus optional prefetch margin) stay resident. Off-range chunks are evicted when the total estimated byte size exceeds
 * the budget.
 *
 * Design constraints:
 *  - The cache has no opinion about rendering. It answers "for tick range [a, b), what ticks do we have loaded?" and returns them in order.
 *  - Chunk identity = index into the server-provided chunkManifest. That's stable across the life of an open trace session.
 *  - Loading is idempotent: repeated calls to ensureRange with the same viewport after a load completes do nothing (fast path).
 *  - Concurrent in-flight loads are deduped — calling ensureRange again while a chunk is mid-fetch doesn't kick off a duplicate request.
 *  - Eviction never drops a chunk that overlaps the current viewport (pin-on-visible). LRU only evicts off-screen chunks.
 */
import type { ChunkManifestEntry, GaugeId, SystemDef, TickSummary, TraceMetadata } from '../model/types';
import type { GaugeSeries, GcEvent, GcSuspensionEvent, MemoryAllocEventData, OffCpuStore, TickData } from '../model/traceModel';
import { aggregateGaugeData, mergeTickData } from '../model/traceModel';
import { fetchChunk, fetchChunkBinary } from './api';
import { processEventsInWorker, processBinaryInWorker } from '../decode/chunkWorkerClient';
import { OpfsChunkStore } from './opfsChunkStore';

/** One loaded-and-processed chunk, resident in memory. */
export interface LoadedChunk {
  chunkIdx: number;
  fromTick: number;
  toTick: number;
  tickData: TickData[];
  /** Rough byte estimate for LRU math. Computed at load time from event count × avg-DTO-size heuristic. */
  byteSize: number;
  /** Monotonic access counter — bumped whenever the chunk is touched. Smallest value = least-recently-used. */
  lastAccessTick: number;
}

/** Internal state — don't mutate externally; call the exported helpers instead. */
export interface ChunkCacheState {
  entries: Map<number, LoadedChunk>;
  totalBytes: number;
  /** In-flight fetches keyed by chunkIdx so concurrent ensureRange calls dedup. */
  inFlight: Map<number, Promise<LoadedChunk>>;
  accessCounter: number;
  budgetBytes: number;
  /**
   * Optional OPFS-backed persistent store. When present, loadChunk first attempts an OPFS hit (survives page reload and LRU
   * evictions). Missing/disabled → direct server fetch. Writes are fire-and-forget: the UI gets the bytes back immediately,
   * OPFS persistence happens in the background.
   */
  opfsStore: OpfsChunkStore | null;
  /**
   * Latch for the "cache stuck over budget" diagnostic. One warning per transition INTO over-budget state, cleared when the
   * cache drops back under. Scoped to the cache instance so that switching traces (new cache) resets the warning independently
   * of any previous session's state.
   */
  overBudgetWarned: boolean;
  /**
   * Chunks that failed to decode, keyed by chunkIdx. Each entry records when the failure happened so we can apply a
   * retry-after window instead of hammering the server every viewport change. Without this, a permanently-bad chunk (server
   * bug, cache-format skew, LZ4 corruption that re-persists) would trigger a fresh fetch + decode + reject cycle on EVERY
   * viewport intersection — surfacing as an error-banner loop the user can't escape.
   */
  failedChunks: Map<number, { error: string; failedAt: number }>;
  /**
   * Monotonic version counter bumped every time <see cref="entries"/> changes (insert, delete). Used by
   * <see cref="assembleTickViewAndNumbers"/> to short-circuit on pure pan/zoom (no chunks loaded or evicted): if the version
   * matches the last-computed assembly's snapshot, return the cached result directly. This turns a ~500 ms/frame cost on
   * intra-tick-split traces (chain-fold re-runs of processTickEvents) into a pointer compare + return.
   */
  entriesVersion: number;
  /** Snapshot of the last <see cref="assembleTickViewAndNumbers"/> output + the entriesVersion at which it was computed. */
  lastAssembly: {
    version: number;
    result: {
      tickData: TickData[];
      tickNumbers: number[];
      gaugeSeries: Map<GaugeId, GaugeSeries>;
      gaugeCapacities: Map<GaugeId, number>;
      memoryAllocEvents: MemoryAllocEventData[];
      gcEvents: GcEvent[];
      gcSuspensions: GcSuspensionEvent[];
      threadNames: Map<number, string>;
      threadKinds: Map<number, number>;
      offCpuBySlot: Map<number, OffCpuStore>;
    };
  } | null;
  /**
   * Persistent slot → thread-name map. ThreadInfo records are emitted ONCE per slot at slot-claim time and live in the
   * pre-tick bucket of the FIRST chunk only (the cache builder prepends pre-tick records to chunk 1's binary). If chunk 1
   * isn't currently loaded — e.g., the user dragged a viewRange to a later region of the trace — the per-assembly
   * `threadNames` aggregation walks only the resident chunks and finds nothing. Persisting the map across chunk loads /
   * evictions matches the metadata-not-per-tick-data nature of ThreadInfo: once a slot's name is observed, it stays. The
   * accumulator is mutated during chunk insertion (`storeChunk`); `assembleTickViewAndNumbers` returns this map directly
   * instead of recomputing per call. Cleared only when the cache itself is recreated (new session / fingerprint change).
   */
  threadNames: Map<number, string>;
  /**
   * Persistent slot → ThreadKind map (Main=0, Worker=1, Pool=2, Other=3) — same chunk-eviction-survives
   * lifecycle as {@link threadNames}. Drives the filter tree's Main/Workers/Other subgrouping in trace
   * mode. Empty for pre-v4 traces where ThreadInfo records lack the trailing kind byte.
   */
  threadKinds: Map<number, number>;
}

/**
 * How long a chunk is considered "failed" before ensureRangeLoaded will retry it. Short enough that a transient network
 * blip self-heals on the next natural viewport change; long enough that a genuinely bad chunk doesn't spam the server or
 * the error banner.
 */
const FAILED_CHUNK_RETRY_AFTER_MS = 30_000;

const DEFAULT_BUDGET = 500 * 1024 * 1024;     // 500 MB client-side in-memory cache (separate from the OPFS persistence layer). Bumped
                                              // from 200 MB after observing that dense end-of-trace regions (readBurst, heavy allocation
                                              // aftermath) routinely push a single viewport's visible+prefetch set close to — or past —
                                              // the old budget. At 500 MB, a typical visible+prefetch window (7 chunks × ~20 MB each)
                                              // leaves ~2–3× headroom for older chunks to stay warm, cutting re-fetch churn.
// Per-event heap cost used for LRU accounting. The prior value (200) was a pre-V8-overhead estimate that badly undercounted
// real resident size: each decoded TraceEvent is a JS object with ~5-10 fields plus a class-shape header (~24 B) + field slots
// (~8 B each), typically 300-500 B total; span and alloc events carry more. On dense chunks the discrepancy compounded into a
// real overshoot (e.g., tick 2000's 2M events accounted as 400 MB was closer to 800 MB+ in real heap). Raising the constant
// to 500 B gets the budget math back inside a safe envelope on realistic workloads. A future refinement: sample decoded-size
// during the first few chunk loads via the Memory Measurement API (performance.measureUserAgentSpecificMemory) and recalibrate
// per-session — but that's gated behind cross-origin-isolation, so the constant is the pragmatic default until then.
const AVG_BYTES_PER_EVENT = 500;

/**
 * Transport selector for chunk loading. `true` uses the binary endpoint (/api/trace/chunk-binary + LZ4 + TS decoder); `false` falls back to
 * the legacy JSON endpoint. Flip to false if a decoder fidelity bug surfaces — the legacy path is preserved exactly, so switching is
 * zero-risk. Module-scope const rather than a runtime flag because we want it tree-shaken out of one code path in production builds.
 */
const USE_BINARY_CHUNK_TRANSPORT = true;

export function createChunkCache(budgetBytes: number = DEFAULT_BUDGET, opfsStore: OpfsChunkStore | null = null): ChunkCacheState {
  return {
    entries: new Map(),
    entriesVersion: 0,
    lastAssembly: null,
    totalBytes: 0,
    inFlight: new Map(),
    accessCounter: 0,
    budgetBytes,
    opfsStore,
    overBudgetWarned: false,
    failedChunks: new Map(),
    threadNames: new Map(),
    threadKinds: new Map(),
  };
}

/** How many adjacent chunks on each side of the visible range to speculatively load when stationary. Keeps panning smooth. */
export const DEFAULT_PREFETCH_CHUNKS = 2;

/**
 * Load every chunk overlapping [fromTick, toTick), plus adjacent chunks on each side of the visible range, evicting off-range chunks if
 * needed to stay under budget. Returns the loaded chunks covering the VISIBLE range (not the prefetch range), sorted by fromTick. Prefetched
 * chunks enter the cache but aren't included in the return value — they're opportunistic for the next viewport change, not for immediate
 * rendering.
 *
 * <paramref name="prefetchBefore"/> and <paramref name="prefetchAfter"/> are asymmetric so callers can bias prefetch toward the direction the
 * user is panning. Moving forward (wheeling through time) → larger `prefetchAfter`, smaller `prefetchBefore`. Moving backward → inverse. When
 * stationary, both default to DEFAULT_PREFETCH_CHUNKS.
 *
 * Idempotent — already-loaded chunks in the range are returned without refetching. Pass <paramref name="signal"/> to propagate cancellation
 * from the caller (viewport effect cleanup); aborted fetches leave no residue in the cache.
 */
export async function ensureRangeLoaded(
  cache: ChunkCacheState,
  path: string,
  metadata: TraceMetadata,
  manifest: ChunkManifestEntry[],
  fromTick: number,
  toTick: number,
  prefetchBefore: number = DEFAULT_PREFETCH_CHUNKS,
  prefetchAfter: number = DEFAULT_PREFETCH_CHUNKS,
  signal?: AbortSignal,
): Promise<LoadedChunk[]> {
  const visibleIndices = findChunksOverlapping(manifest, fromTick, toTick);
  if (visibleIndices.length === 0) return [];

  // Expand to include prefetch neighbours. Bounded at manifest edges so we don't fetch chunks that don't exist.
  const firstVisible = visibleIndices[0];
  const lastVisible = visibleIndices[visibleIndices.length - 1];
  const prefetchFrom = Math.max(0, firstVisible - Math.max(0, prefetchBefore));
  const prefetchTo = Math.min(manifest.length - 1, lastVisible + Math.max(0, prefetchAfter));
  const allIndices: number[] = [];
  for (let i = prefetchFrom; i <= prefetchTo; i++) allIndices.push(i);

  // Pin set for the visibleLoadPromises ordering check below — strictly the visible range. Prefetched chunks aren't awaited by
  // the caller but still land in the cache on completion. The BROADER allIndices set (below) is what eviction uses as its pin
  // set so that freshly-loaded prefetch chunks aren't immediately evicted by another prefetch chunk that lands a few ms later.
  const pinnedIdxs = new Set(visibleIndices);
  // Full pin set (visible + prefetch) threaded through to every loadChunk spawned by THIS ensureRangeLoaded call. Used both
  // by this call's post-await eviction pass AND by each loadChunk's per-completion eviction for late-arriving prefetch
  // chunks. Passing the set explicitly (rather than reading a shared cache field) makes each loadChunk's eviction semantics
  // deterministic w.r.t. the viewport that spawned it — no cross-call stomping when two ensureRangeLoaded calls overlap in
  // time (rapid pan, velocity-biased prefetch landing after the next viewport has already been requested).
  const allIndicesSet = new Set(allIndices);
  const visibleLoadPromises: Promise<LoadedChunk>[] = [];

  const now = performance.now();
  for (const idx of allIndices) {
    const existing = cache.entries.get(idx);
    if (existing) {
      existing.lastAccessTick = ++cache.accessCounter;
      if (pinnedIdxs.has(idx)) visibleLoadPromises.push(Promise.resolve(existing));
      continue;
    }
    const inFlight = cache.inFlight.get(idx);
    if (inFlight) {
      if (pinnedIdxs.has(idx)) visibleLoadPromises.push(inFlight);
      continue;
    }
    // Failed-chunk gate: if this chunk recently failed, don't re-fetch for a while. Visible chunks still produce a rejected
    // promise so the error banner can (once) tell the user the region is unrenderable; prefetch chunks are silently skipped.
    // When the retry window elapses the gate opens on its own — one natural viewport change triggers a fresh attempt.
    const prevFailure = cache.failedChunks.get(idx);
    if (prevFailure && now - prevFailure.failedAt < FAILED_CHUNK_RETRY_AFTER_MS) {
      if (pinnedIdxs.has(idx)) visibleLoadPromises.push(Promise.reject(new Error(prevFailure.error)));
      continue;
    }
    // Pass the abort signal ONLY to visible-range fetches. Prefetch fetches must NOT be cancellable — they're speculative loads that should
    // complete to populate the cache, so a subsequent wheel event can find the chunk already-loaded. If we aborted prefetches on every
    // viewport change, rapid wheel navigation would cancel every chunk before it finishes, forcing the final stop to refetch from scratch.
    // With this split, rapid wheels still land on a cache full of in-flight or completed prefetches — the final visible chunk almost always
    // resolves from cache (or a reusable inFlight promise), not a cold fetch.
    const isPinned = pinnedIdxs.has(idx);
    const fetchSignal = isPinned ? signal : undefined;
    const promise = loadChunk(cache, path, metadata, manifest[idx], idx, allIndicesSet, fetchSignal);
    cache.inFlight.set(idx, promise);
    if (isPinned) {
      visibleLoadPromises.push(promise);
    } else {
      // Prefetch-only path: nothing awaits this promise, so if it rejects (speculative fetch 500, network blip) we'd surface an
      // "Uncaught (in promise)" warning in the browser console. Attach a silent tail so the rejection is considered handled — the chunk
      // simply won't land in the cache, and the next viewport change will try again if it's still needed.
      promise.catch(() => {});
    }
  }

  const visible = await Promise.all(visibleLoadPromises);
  // Post-load eviction: drop chunks not in the full (visible + prefetch) set if we're over budget. Shares the same local
  // `allIndicesSet` that was threaded through every loadChunk above, so both in-call and per-chunk evictions use IDENTICAL
  // pin semantics for this viewport — and remain independent of any concurrent ensureRangeLoaded call operating on a different
  // viewport (no shared cache-level stamp to stomp on).
  evictIfOverBudget(cache, allIndicesSet);
  return visible.sort((a, b) => a.fromTick - b.fromTick);
}

/**
 * Assemble a flat, sorted array of TickData from the currently-cached chunks. This is what `ProcessedTrace.ticks` should point to after any
 * cache mutation — consumers (GraphArea, DetailPane) read from this view without knowing about chunk boundaries.
 *
 * The returned array is newly allocated on every call; callers should re-reference (setTrace) to trigger Preact re-render.
 * Prefer <see cref="assembleTickViewAndNumbers"/> when the caller needs both arrays — saves a second sort + second iteration.
 */
/**
 * Build the flat TickData array, parallel tickNumbers array, and the cross-tick aggregates (gauge series, memory
 * alloc events, GC events / suspensions, thread names) in a single pass over the resident chunks. Single sort of
 * cache entries, single inner-loop traversal of each chunk's ticks. The function fires on every viewport effect
 * during pan/zoom; the memo at the top short-circuits when entriesVersion hasn't changed.
 */
export function assembleTickViewAndNumbers(cache: ChunkCacheState, systems: SystemDef[]): {
  tickData: TickData[];
  tickNumbers: number[];
  gaugeSeries: Map<GaugeId, GaugeSeries>;
  gaugeCapacities: Map<GaugeId, number>;
  memoryAllocEvents: MemoryAllocEventData[];
  gcEvents: GcEvent[];
  gcSuspensions: GcSuspensionEvent[];
  threadNames: Map<number, string>;
  threadKinds: Map<number, number>;
  offCpuBySlot: Map<number, OffCpuStore>;
} {
  // ── Memo short-circuit ────────────────────────────────────────────────────────────────────────────────────────────
  // assembleTickViewAndNumbers runs on EVERY viewport effect — which fires on every pan, zoom, and wheel event. When the
  // cache hasn't changed (pure pan/zoom, no new chunks loaded or evicted) the expensive work below — particularly
  // mergeTickData's chain-fold on intra-tick-split traces, which re-runs processTickEvents on a cumulative event union
  // per iteration — produces an IDENTICAL result to the previous call. Gate on entriesVersion to skip all of it.
  //
  // The memo holds references to (not copies of) the TickData arrays and map objects from the previous computation.
  // That's safe because:
  //   (1) tickData entries are either (a) direct references to cache-resident per-chunk TickData (never mutated after
  //       creation), or (b) merged results produced by mergeTickData (also never mutated after return; rawEvents is
  //       wiped immediately in this function's same pass).
  //   (2) The downstream consumer (App.tsx's setLoaded) shallow-assigns the arrays into trace state — Preact's shallow
  //       compare sees the same references and skips re-rendering components that haven't changed otherwise.
  if (cache.lastAssembly !== null && cache.lastAssembly.version === cache.entriesVersion) {
    return cache.lastAssembly.result;
  }

  // Primary sort key is fromTick. For chunks that share the same fromTick (an intra-tick split, v8+), secondary sort by
  // chunkIdx preserves the original emission order — continuation chunks always follow their parent in the manifest, so
  // sorting by chunkIdx keeps the partial-tick events in the correct temporal order before we merge them.
  const chunks = Array.from(cache.entries.values()).sort((a, b) => {
    if (a.fromTick !== b.fromTick) return a.fromTick - b.fromTick;
    return a.chunkIdx - b.chunkIdx;
  });
  const flattened: TickData[] = [];
  for (const chunk of chunks) {
    for (const tick of chunk.tickData) {
      flattened.push(tick);
    }
  }
  // Intra-tick merge pass: walk the flattened tickData and combine adjacent entries that share a tickNumber. Handles chains
  // of N > 2 split chunks via repeated fold from the left — after one merge, the result becomes the new candidate for the next
  // iteration. The common case (no splits) is a no-op: every tickNumber is unique so the loop just copies entries across.
  //
  // mergedIndices tracks which tickData slots hold a merged result (as opposed to a pass-through from the LRU-cached per-chunk
  // TickData). Only merged results own their rawEvents — pass-throughs are shared-owned by the LRU cache and must NOT be
  // mutated here. Post-loop we wipe rawEvents on the merged slots only, since no further fold can fire on them (the chain is
  // fully consumed) and a resident ~N-event array is pure display-time dead weight.
  const tickData: TickData[] = [];
  const tickNumbers: number[] = [];
  const mergedIndices: number[] = [];
  for (const td of flattened) {
    const last = tickData.length > 0 ? tickData[tickData.length - 1] : null;
    if (last !== null && last.tickNumber === td.tickNumber) {
      // Replace the tail with the merged result. mergeTickData is O(N log N) in total events; with intra-tick splits only
      // firing on pathological ticks, this is cold for normal workloads.
      const slot = tickData.length - 1;
      tickData[slot] = mergeTickData(last, td, systems);
      // Track this slot only on the FIRST fold — subsequent folds on the same tickNumber reuse the slot and the tracker
      // stays correct. Using a simple O(N) de-dupe check here is fine because chain-folds are rare.
      if (mergedIndices.length === 0 || mergedIndices[mergedIndices.length - 1] !== slot) {
        mergedIndices.push(slot);
      }
    } else {
      tickData.push(td);
      tickNumbers.push(td.tickNumber);
    }
  }
  // Now that no further merges can fire, wipe rawEvents on the merged results. For a 2 M-event dense tick that split across
  // N chunks and finally merged to a single TickData, this frees ~1 GB of otherwise-retained event data that no downstream
  // renderer consumes.
  for (const idx of mergedIndices) {
    tickData[idx].rawEvents = [];
  }
  const { gaugeSeries, gaugeCapacities, memoryAllocEvents, gcEvents, gcSuspensions, offCpuBySlot } = aggregateGaugeData(tickData);
  // Use the cache-level persistent thread-name map rather than the per-assembly aggregator's
  // result. The aggregator only sees currently-resident ticks; if chunk 1 (where pre-tick
  // ThreadInfo records live) has been evicted or never loaded for the current viewRange, the
  // aggregator returns an empty map even though we saw the names earlier. The persistent map on
  // the cache survives evictions — see `cache.threadNames` docs and the harvest in `loadChunk`.
  const threadNames = cache.threadNames;
  const threadKinds = cache.threadKinds;
  const result = {
    tickData, tickNumbers, gaugeSeries, gaugeCapacities, memoryAllocEvents, gcEvents, gcSuspensions, threadNames, threadKinds, offCpuBySlot,
  };
  // Snapshot the version at which this result was computed. Any future call with the same version returns the same result via the
  // short-circuit at the top. Invalidated automatically when entries mutate (loadChunk ↑, evict ↑).
  cache.lastAssembly = { version: cache.entriesVersion, result };
  return result;
}

/**
 * Map an absolute-µs range to a half-open tick-number range [fromTick, toTick). Uses binary search over the summary (sorted by startUs).
 * Strict half-open overlap: a tick [tickStart, tickEnd) overlaps [fromUs, toUs) iff tickEnd > fromUs && tickStart < toUs. Boundary touches
 * (tickStart == toUs or tickEnd == fromUs) do NOT count — otherwise selecting a single tick would spill into adjacent chunks whose startUs
 * kisses the selection's endUs.
 *
 * This runs on every wheel event at 60 Hz; on a 500K-tick summary the prior linear scan was ~5 ms × 60 = 300 ms/sec of pure CPU just on
 * range mapping. Binary search drops that to ~3 µs per call.
 */
export function viewRangeToTickRange(
  summary: TickSummary[],
  fromUs: number,
  toUs: number,
): { fromTick: number; toTick: number } | null {
  if (summary.length === 0) return null;

  // `first` = smallest i such that summary[i].startUs + durationUs > fromUs (tick extends past viewport start).
  // Binary search on the monotone predicate P(i) := endUs(i) > fromUs. Standard lower-bound.
  let lo = 0;
  let hi = summary.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    const s = summary[mid];
    if (s.startUs + s.durationUs > fromUs) hi = mid;
    else lo = mid + 1;
  }
  const firstIdx = lo;
  if (firstIdx >= summary.length) return null;               // all ticks end at or before fromUs

  // `last` = largest i such that summary[i].startUs < toUs. Binary search for upper-bound of startUs < toUs.
  lo = firstIdx;
  hi = summary.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (summary[mid].startUs < toUs) lo = mid + 1;
    else hi = mid;
  }
  const lastIdx = lo - 1;                                   // last i satisfying the predicate
  if (lastIdx < firstIdx) return null;                      // no tick overlaps [fromUs, toUs)

  return { fromTick: summary[firstIdx].tickNumber, toTick: summary[lastIdx].tickNumber + 1 };
}

/**
 * List the µs-ranges inside `viewRange` that correspond to manifest chunks whose data is NOT yet
 * resident in the cache. Consumers (TimeArea) overlay a diagonal-stripe pattern on these ranges so
 * the user sees "something's loading here" instead of empty or stale canvas.
 *
 * Ranges coalesce adjacent missing chunks into a single output entry so the renderer paints one
 * pattern tile per contiguous pending span instead of N adjacent tiles. Ranges are clipped to
 * `viewRange` at the boundary.
 *
 * O(M + K) where M = manifest length overlapping the viewport, K = chunks-per-output — in practice
 * a small number; this runs once per frame from the React wrapper.
 */
export function computePendingRangesUs(
  cache: ChunkCacheState | null,
  manifest: readonly ChunkManifestEntry[],
  tickSummaries: readonly TickSummary[],
  viewRange: { startUs: number; endUs: number },
): Array<{ startUs: number; endUs: number }> {
  if (viewRange.endUs <= viewRange.startUs) return [];
  if (manifest.length === 0 || tickSummaries.length === 0) return [];

  // Index tick summaries by tickNumber for O(log N) lookup. They're sorted by tickNumber + startUs
  // (monotone), so binary search works.
  const findSummaryIdx = (tickNumber: number): number => {
    let lo = 0; let hi = tickSummaries.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (tickSummaries[mid].tickNumber < tickNumber) lo = mid + 1; else hi = mid;
    }
    return lo < tickSummaries.length && tickSummaries[lo].tickNumber === tickNumber ? lo : -1;
  };

  const out: Array<{ startUs: number; endUs: number }> = [];
  let runStart = -1;
  let runEnd = -1;

  const flushRun = (): void => {
    if (runStart < 0) return;
    // Clip to viewRange and emit.
    const s = Math.max(runStart, viewRange.startUs);
    const e = Math.min(runEnd, viewRange.endUs);
    if (e > s) out.push({ startUs: s, endUs: e });
    runStart = -1;
    runEnd = -1;
  };

  for (let i = 0; i < manifest.length; i++) {
    const entry = manifest[i];
    // Convert [entry.fromTick, entry.toTick) to [startUs, endUs).
    const fromIdx = findSummaryIdx(entry.fromTick);
    if (fromIdx < 0) continue;
    // toTick is exclusive; look up the LAST contained tick (entry.toTick - 1).
    const lastTick = entry.toTick - 1;
    const lastIdx = findSummaryIdx(lastTick);
    if (lastIdx < 0) continue;
    const s = tickSummaries[fromIdx].startUs;
    const lastSummary = tickSummaries[lastIdx];
    const e = lastSummary.startUs + lastSummary.durationUs;

    // Skip entries entirely outside the viewport.
    if (e <= viewRange.startUs) continue;
    if (s >= viewRange.endUs) break; // manifest is ordered by startUs → everything after is past the view.

    // Resident? No overlay. A chunk is "resident" iff it's in cache.entries (already loaded). An
    // in-flight fetch still counts as pending — the data isn't usable for rendering yet, so the
    // overlay remains until the fetch resolves and the cache version bumps.
    if (cache !== null && cache.entries.has(i)) {
      flushRun();
      continue;
    }

    // Extend or start a run.
    if (runStart < 0 || s > runEnd) {
      flushRun();
      runStart = s;
      runEnd = e;
    } else if (e > runEnd) {
      runEnd = e;
    }
  }
  flushRun();
  return out;
}

// ─────────────────────────────────────────────────────────────────────────────
// Internals
// ─────────────────────────────────────────────────────────────────────────────

async function loadChunk(
  cache: ChunkCacheState,
  path: string,
  metadata: TraceMetadata,
  entry: ChunkManifestEntry,
  chunkIdx: number,
  pinnedIdxs: Set<number>,
  signal?: AbortSignal,
): Promise<LoadedChunk> {
  try {
    let tickData: TickData[];
    let byteSize: number;

    if (USE_BINARY_CHUNK_TRANSPORT) {
      // OPFS-first path. Try the persistent store before hitting the network. On-disk layout prepends a 4-byte little-endian uint
      // carrying `uncompressedBytes` (the only per-chunk decode metadata NOT available from the client-side manifest entry).
      // `timestampFrequency` is trace-global and read from metadata.header.
      const ticksPerUs = metadata.header.timestampFrequency / 1_000_000;
      const opfsBytes = await cache.opfsStore?.get(chunkIdx) ?? null;
      let opfsDecoded: { tickData: TickData[]; byteSize: number } | null = null;
      if (opfsBytes !== null && opfsBytes.byteLength >= 4) {
        // Try decoding what's on disk. A corrupt file (torn write from a prior crash, OPFS bug, disk error) will manifest as an
        // LZ4 size-prefix mismatch or a binary-decoder throw — both caught here. On failure we REMOVE the bad entry and fall
        // through to a server re-fetch. Without this, every viewport intersecting the bad chunk permanently fails, because
        // the OPFS hit keeps winning and keeps throwing.
        try {
          const dv = new DataView(opfsBytes, 0, 4);
          const uncompressedBytes = dv.getUint32(0, true);
          const compressed = opfsBytes.slice(4);  // structured clone, transferable to worker
          // OPFS doesn't persist the isContinuation flag in the blob itself (the blob is just the LZ4 bytes) — pull it from the
          // manifest entry, which is canonical. Server-side regeneration uses the same manifest, so this value is stable across
          // all opens of the same trace even if the OPFS contents rehydrate.
          const decoded = await processBinaryInWorker(
            compressed, uncompressedBytes, entry.fromTick, ticksPerUs, metadata.systems, entry.isContinuation,
          );
          opfsDecoded = { tickData: decoded, byteSize: entry.eventCount * AVG_BYTES_PER_EVENT };
        } catch (err) {
          console.warn(`[chunkCache] OPFS chunk ${chunkIdx} decode failed, evicting and re-fetching from server:`, err);
          try { await cache.opfsStore?.remove(chunkIdx); } catch { /* best-effort cleanup */ }
        }
      }

      if (opfsDecoded !== null) {
        tickData = opfsDecoded.tickData;
        byteSize = opfsDecoded.byteSize;
      } else {
        const fetched = await fetchPersistAndDecode(cache, path, entry, chunkIdx, ticksPerUs, metadata.systems, signal);
        tickData = fetched.tickData;
        byteSize = fetched.byteSize;
      }
    } else {
      // Legacy JSON path — server decodes, emits JSON, client runs through the Worker on already-parsed events.
      const response = await fetchChunk(path, chunkIdx, signal);
      tickData = await processEventsInWorker(response.events, metadata.systems);
      byteSize = response.events.length * AVG_BYTES_PER_EVENT;
    }

    const loaded: LoadedChunk = {
      chunkIdx,
      fromTick: entry.fromTick,
      toTick: entry.toTick,
      tickData,
      byteSize,
      lastAccessTick: ++cache.accessCounter,
    };
    cache.entries.set(chunkIdx, loaded);
    cache.entriesVersion++;
    cache.totalBytes += byteSize;
    // Harvest ThreadInfo entries from this chunk's tickData into the persistent cache-level map.
    // ThreadInfo records are emitted ONCE per slot at slot-claim time and only live in chunk 1's
    // pre-tick bucket (cache builder prepends pre-tick records to chunk 1's binary). Walk every
    // tick's threadInfos here so the slot→name map survives chunk eviction — without this, a user
    // whose viewRange doesn't currently overlap chunk 1 sees lane labels regress to "Slot N".
    // First observation wins (re-claims under the same slot would emit a fresh ThreadInfo, which we
    // intentionally honour via the `set` overwrite — matches `aggregateGaugeData`'s behavior).
    for (const td of tickData) {
      if (td.threadInfos.length === 0) continue;
      for (const info of td.threadInfos) {
        cache.threadNames.set(info.threadSlot, info.name);
        if (info.kind !== undefined) {
          cache.threadKinds.set(info.threadSlot, info.kind);
        }
      }
    }
    // Successful decode clears any prior failure record for this chunk. A transient error (network blip, server restart
    // mid-request) would otherwise leave a stale entry in failedChunks that suppresses future retries for the retry-after
    // window even though the chunk is demonstrably fine now.
    cache.failedChunks.delete(chunkIdx);
    // Per-chunk budget check. ensureRangeLoaded's post-await eviction only fires once per call and covers visible chunks that
    // completed before the Promise.all resolved. Prefetch chunks (fired off but not awaited) land asynchronously after that
    // sweep — without THIS check, each late completion bumps totalBytes with no compensation.
    //
    // `pinnedIdxs` is the SAME Set instance that the spawning ensureRangeLoaded call passes to every loadChunk AND to its own
    // post-await eviction. So when this late prefetch completion runs its eviction pass, it protects EXACTLY the viewport that
    // requested it — not "whatever the latest viewport happens to be" (which would stomp on an older, still-in-progress call
    // whose visible chunks haven't resolved yet).
    evictIfOverBudget(cache, pinnedIdxs);
    return loaded;
  } catch (err) {
    // Record the failure so ensureRangeLoaded's failedChunks gate suppresses retries for FAILED_CHUNK_RETRY_AFTER_MS. DON'T
    // record AbortError — a viewport-change cancellation isn't a decode problem; a future viewport intersecting this chunk
    // should retry immediately without a cooldown.
    const isAbort = err !== null && typeof err === 'object' && 'name' in err && (err as { name: string }).name === 'AbortError';
    if (!isAbort) {
      const msg = err instanceof Error ? err.message : String(err);
      cache.failedChunks.set(chunkIdx, { error: msg, failedAt: performance.now() });
    }
    throw err;
  } finally {
    cache.inFlight.delete(chunkIdx);
  }
}

/**
 * Server-fetch + OPFS-persist + Worker-decode helper. Shared between the cold-miss path and the OPFS-corruption recovery path in
 * {@link loadChunk}. Extracted so the two branches are guaranteed to persist and decode identically — a subtle divergence here
 * (e.g., "recovery path skips OPFS re-persist") would recreate the very corruption case we're recovering from.
 *
 * Note on transfer semantics: <paramref name="signal"/>'s abort only cancels the network fetch; the worker decode still runs to
 * completion (Worker has no interrupt primitive). Caller must check `signal.aborted` AFTER this resolves if they want to skip
 * committing a chunk that arrived late.
 */
async function fetchPersistAndDecode(
  cache: ChunkCacheState,
  path: string,
  entry: ChunkManifestEntry,
  chunkIdx: number,
  ticksPerUs: number,
  systems: SystemDef[],
  signal: AbortSignal | undefined,
): Promise<{ tickData: TickData[]; byteSize: number }> {
  const response = await fetchChunkBinary(path, chunkIdx, signal);
  const compressedBuffer = response.compressed.buffer as ArrayBuffer;
  const uncompressedBytes = response.uncompressedBytes;
  // Defense-in-depth: the manifest entry AND the response header both carry isContinuation. If they disagree, this is a
  // server/client cache-format skew — the exact silent-corruption class this flag was designed to catch. In dev builds we
  // throw loudly so CI and local testing fail fast; in prod builds we log + fall back to the manifest (canonical source,
  // set at /open time). The production fallback keeps the viewer usable on a skewed deployment rather than bricking it,
  // but dev-time testing is the time to catch the skew.
  if (response.isContinuation !== entry.isContinuation) {
    const msg =
      `chunk #${chunkIdx} isContinuation mismatch: manifest=${entry.isContinuation}, header=${response.isContinuation}. ` +
      `This indicates server/client cache-format skew.`;
    // Vite exposes import.meta.env.DEV at build time — true during `npm run dev`, false in `npm run build` bundles.
    if (import.meta.env.DEV) {
      throw new Error(`[chunkCache] ${msg}`);
    }
    console.warn(`[chunkCache] ${msg} Using manifest value.`);
  }

  // Persist to OPFS BEFORE we hand the ArrayBuffer to the worker (postMessage transfers ownership — the buffer becomes
  // detached in this frame afterwards). We copy the bytes by slicing first, then transfer the original to the worker.
  // The persisted layout is [u32 uncompressedBytes | compressed bytes]. Fire-and-forget; don't await — keeps the UI path
  // unblocked by disk I/O.
  if (cache.opfsStore !== null) {
    const persistCopy = new ArrayBuffer(4 + compressedBuffer.byteLength);
    new DataView(persistCopy).setUint32(0, uncompressedBytes, true);
    new Uint8Array(persistCopy, 4).set(new Uint8Array(compressedBuffer));
    void cache.opfsStore.put(chunkIdx, persistCopy);
  }

  const tickData = await processBinaryInWorker(
    compressedBuffer, uncompressedBytes, entry.fromTick, ticksPerUs, systems, entry.isContinuation,
  );
  return { tickData, byteSize: response.eventCount * AVG_BYTES_PER_EVENT };
}

/** Find all manifest indices whose [fromTick, toTick) range overlaps the requested [fromTick, toTick). */
function findChunksOverlapping(manifest: ChunkManifestEntry[], fromTick: number, toTick: number): number[] {
  const result: number[] = [];
  for (let i = 0; i < manifest.length; i++) {
    const entry = manifest[i];
    // Overlap condition for half-open ranges: !(entry.toTick <= fromTick || entry.fromTick >= toTick)
    if (entry.toTick > fromTick && entry.fromTick < toTick) {
      result.push(i);
    }
  }
  return result;
}

/**
 * If totalBytes exceeds the budget, evict LRU-ordered entries that are NOT in the pinned set. Pinned entries are the ones overlapping the
 * current viewport — evicting them would leave visible gaps, so they're immune regardless of age.
 */
function evictIfOverBudget(cache: ChunkCacheState, pinnedIdxs: Set<number>): void {
  if (cache.totalBytes <= cache.budgetBytes) return;

  // Sort candidates (unpinned) by lastAccessTick ascending — oldest first.
  const candidates: LoadedChunk[] = [];
  for (const entry of cache.entries.values()) {
    if (!pinnedIdxs.has(entry.chunkIdx)) candidates.push(entry);
  }
  candidates.sort((a, b) => a.lastAccessTick - b.lastAccessTick);

  for (const victim of candidates) {
    if (cache.totalBytes <= cache.budgetBytes) break;
    cache.entries.delete(victim.chunkIdx);
    cache.entriesVersion++;
    cache.totalBytes -= victim.byteSize;
  }
  // Diagnostic: if we evicted every candidate and the cache is STILL over budget, the remaining overshoot is all in pinned
  // chunks (current viewport's visible + prefetch). There's nothing correctness-preserving we can do — evicting a pinned
  // chunk would produce a visible pending-pattern gap RIGHT where the user is looking. Log once per "stuck over budget"
  // transition so a dev looking at the console understands why `ram:X/Y` shows X > Y. Throttled by a module-level flag to
  // avoid console spam across repeated evictions while stuck.
  if (cache.totalBytes > cache.budgetBytes && !cache.overBudgetWarned) {
    cache.overBudgetWarned = true;
    const pinned = cache.entries.size - candidates.length + (candidates.length - (candidates.filter(c => !cache.entries.has(c.chunkIdx)).length));
    console.warn(
      `[chunkCache] cache stuck over budget: ${(cache.totalBytes / (1024 * 1024)).toFixed(0)} MB used, ` +
      `${(cache.budgetBytes / (1024 * 1024)).toFixed(0)} MB budget, ${pinned} chunks pinned. ` +
      `This means the current viewport's visible+prefetch set alone exceeds the budget. ` +
      `Consider increasing DEFAULT_BUDGET or reducing prefetch horizon near dense regions.`
    );
  } else if (cache.totalBytes <= cache.budgetBytes && cache.overBudgetWarned) {
    // Reset the latch once we're back under budget, so a future stuck-state gets its own warning.
    cache.overBudgetWarned = false;
  }
}
