import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
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
 * Chunk-cache lifecycle + viewRange-driven loader. Creates a cache on first metadata arrival, converts the
 * OpenAPI DTO to the internal `TraceMetadata` + manifest shape, kicks `ensureRangeLoaded` whenever the viewport
 * changes, and returns the currently-resident `TickData[]` via `assembleTickViewAndNumbers`.
 *
 * #289 — post-unification, both Trace (replay) and Attach (live) sessions ride this same chunk-cache path.
 * The server's `IncrementalCacheBuilder` produces the same chunk + manifest shape in either mode; in live mode
 * the manifest grows over time as deltas arrive, and `ensureRangeLoaded` re-evaluates against the latest manifest.
 */
export interface ProfilerGaugeData {
  gaugeSeries: Map<GaugeId, GaugeSeries>;
  gaugeCapacities: Map<GaugeId, number>;
  memoryAllocEvents: MemoryAllocEventData[];
  gcEvents: GcEvent[];
  gcSuspensions: GcSuspensionEvent[];
  threadNames: Map<number, string>;
  /** Slot → off-CPU interval store, derived cross-tick from ThreadContextSwitch records. Empty for traces with no scheduling data. */
  offCpuBySlot: Map<number, OffCpuStore>;
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

/**
 * Slot → (name, kind) — unified across replay and live modes. Replay sources both fields from the
 * chunk-cache's persistent `threadNames` + `threadKinds` maps. Live appends/overrides from the SSE
 * `threadInfoAdded` deltas (which carry the kind byte explicitly). Consumed by the section-filter
 * popup to bucket lanes into Main / Workers / Other; missing kind defaults to Other.
 */
export interface SlotThreadInfo {
  name: string;
  kind: number;
}

export function useProfilerCache(sessionId: string | null, isLive: boolean): {
  ticks: TickData[];
  traceMetadata: TraceMetadata | null;
  gaugeData: ProfilerGaugeData;
  /** Unified slot → {name, kind} map; replay reads chunk cache, live merges SSE deltas. */
  threadInfos: Map<number, SlotThreadInfo>;
  /**
   * µs-ranges within the current viewport whose chunk data is not yet resident in the cache.
   * Empty when everything is loaded or when no selection is active.
   */
  pendingRangesUs: Array<{ startUs: number; endUs: number }>;
} {
  const metadataDto = useProfilerSessionStore((s) => s.metadata);
  const liveThreadInfos = useProfilerSessionStore((s) => s.liveThreadInfos);
  const viewRange = useProfilerViewStore((s) => s.viewRange);

  const hasSelection = viewRange.endUs > viewRange.startUs;

  // ── Stable references derived from the DTO ──────────────────────────────────────────────────
  // Live mode: metadataDto reference flips every time tickSummaries / chunkManifest mutate (the store's appenders
  // shallow-clone). Replay: changes once when the build completes. Either way, depending on metadataDto directly
  // is correct.
  const fingerprint = metadataDto?.fingerprint ?? null;

  const traceMetadata: TraceMetadata | null = useMemo(() => {
    if (!metadataDto) return null;
    return convertProfilerMetadata(metadataDto);
  }, [metadataDto]);

  const manifest: ChunkManifestEntry[] = useMemo(() => {
    if (!metadataDto) return [];
    return convertChunkManifest(metadataDto.chunkManifest);
  }, [metadataDto]);

  const tickSummaries: TickSummary[] = useMemo(() => {
    if (!metadataDto) return [];
    return convertTickSummaries(metadataDto.tickSummaries);
  }, [metadataDto]);

  // ── Cache instance ──────────────────────────────────────────────────────────────────────────
  // One cache per session. Live: keyed by sessionId since fingerprint is empty for live sessions. Replay: cache
  // also re-creates on fingerprint change (different source file → different cache).
  const cacheRef = useRef<ChunkCacheState | null>(null);
  useEffect(() => {
    if (!sessionId) {
      cacheRef.current = null;
      return;
    }
    cacheRef.current = createChunkCache();
    setEntriesVersion(0);
  }, [sessionId, fingerprint]);

  // ── Load driver ──────────────────────────────────────────────────────────────────────────────
  // #289 — `manifest` and `tickSummaries` are read through refs so a live-mode `chunkAdded` delta (which clones
  // the manifest array reference) does not re-create `loadRange` and thereby abort an in-flight fetch through the
  // viewRange effect's dep change. The user's tick-1 selection during AntHill's 200K-spawn burst would otherwise
  // never finish loading because each new chunk delta cancelled the prior ensureRangeLoaded mid-flight.
  const [entriesVersion, setEntriesVersion] = useState(0);
  const inFlightController = useRef<AbortController | null>(null);
  const manifestRef = useRef(manifest);
  manifestRef.current = manifest;
  const tickSummariesRef = useRef(tickSummaries);
  tickSummariesRef.current = tickSummaries;
  const traceMetadataRef = useRef(traceMetadata);
  traceMetadataRef.current = traceMetadata;

  const loadRange = useCallback(async (range: TimeRange): Promise<void> => {
    const cache = cacheRef.current;
    const md = traceMetadataRef.current;
    const m = manifestRef.current;
    const ts = tickSummariesRef.current;
    if (!cache || !md || !sessionId || m.length === 0 || ts.length === 0) return;
    const tr = viewRangeToTickRange(ts, range.startUs, range.endUs);
    if (!tr) return;

    inFlightController.current?.abort();
    const ac = new AbortController();
    inFlightController.current = ac;

    try {
      await ensureRangeLoaded(
        cache, sessionId, md, m,
        tr.fromTick, tr.toTick,
        undefined, undefined,
        ac.signal,
      );
      if (!ac.signal.aborted) {
        setEntriesVersion((v) => v + 1);
      }
    } catch (err) {
      if (err !== null && typeof err === 'object' && 'name' in err && (err as { name: string }).name === 'AbortError') return;
      console.warn('[useProfilerCache] ensureRangeLoaded failed:', err);
    }
  }, [sessionId]);

  useEffect(() => {
    if (!hasSelection) return;
    void loadRange(viewRange);
  }, [viewRange, hasSelection, loadRange]);

  // #289 — when the manifest grows (live mode), the user's currently-selected viewRange may now have additional
  // chunks available. Re-trigger loadRange so those chunks land. This effect is independent of the loadRange
  // dependency above; the ref pattern means loadRange itself is stable.
  useEffect(() => {
    if (!isLive) return;
    if (!hasSelection) return;
    void loadRange(viewRange);
  }, [isLive, manifest, hasSelection, viewRange, loadRange]);

  // Eager metadata-chunk prefetch — chunk 0 carries the prepended pre-tick events from the cache builder
  // (`ThreadInfo` records, early `MemoryAllocEvent`s) that are session-wide metadata living inside chunk-0's binary.
  // Without this, a user who drags a viewRange into a later region never loads chunk 0 and the chunk-cache
  // `threadNames` map stays empty. Re-runs in live mode every time the manifest length changes, but the cache loader
  // is idempotent and a no-op for already-resident chunks.
  useEffect(() => {
    if (!sessionId || !traceMetadata || manifest.length === 0) return;
    const cache = cacheRef.current;
    if (!cache) return;
    const firstChunk = manifest[0];
    if (firstChunk === undefined) return;
    void ensureRangeLoaded(
      cache, sessionId, traceMetadata, manifest,
      firstChunk.fromTick, firstChunk.toTick,
      undefined, undefined,
      undefined,
    ).then(() => {
      setEntriesVersion((v) => v + 1);
    }).catch((err) => {
      if (err !== null && typeof err === 'object' && 'name' in err && (err as { name: string }).name === 'AbortError') return;
      console.warn('[useProfilerCache] eager chunk-0 load failed:', err);
    });
  }, [sessionId, traceMetadata, manifest]);

  // Live mode: as new chunks arrive in the manifest, ensure the visible/recent range is kept resident so the
  // renderer doesn't lag the SSE stream. Re-evaluates on every manifest growth; cache loader is idempotent.
  useEffect(() => {
    if (!isLive) return;
    if (!sessionId || !traceMetadata || manifest.length === 0) return;
    const cache = cacheRef.current;
    if (!cache) return;
    const last = manifest[manifest.length - 1];
    if (last === undefined) return;
    void ensureRangeLoaded(
      cache, sessionId, traceMetadata, manifest,
      last.fromTick, last.toTick,
      undefined, undefined,
      undefined,
    ).then(() => {
      setEntriesVersion((v) => v + 1);
    }).catch(() => {
      /* live cache fetch best-effort; the next manifest update retries */
    });
  }, [isLive, sessionId, traceMetadata, manifest]);

  // ── Assembled tick view + gauge data ─────────────────────────────────────────────────────────
  const assembled = useMemo(() => {
    void entriesVersion;
    const cache = cacheRef.current;
    if (!cache || !traceMetadata) return null;
    return assembleTickViewAndNumbers(cache, traceMetadata.systems);
  }, [entriesVersion, traceMetadata]);

  const ticks: TickData[] = assembled?.tickData ?? [];
  // #289 — in live mode, slot→{name, kind} mappings flow over SSE (`threadInfoAdded`) into `liveThreadInfos`.
  // Merge the name-only view with the chunk-cache-derived names so we don't lose either source: chunk-cache
  // gives us names for replay or for panned-into regions; the live store gives us names for slots whose
  // ThreadInfo records are buried in chunks the user hasn't loaded yet. The `kind` is consumed separately
  // by the filter popup; here we only project to names.
  const mergedThreadNames = useMemo(() => {
    if (!isLive) return assembled?.threadNames ?? new Map<number, string>();
    const out = new Map<number, string>(assembled?.threadNames ?? new Map());
    for (const [slot, info] of liveThreadInfos) {
      if (info.name) out.set(slot, info.name);
    }
    return out;
  }, [isLive, assembled, liveThreadInfos]);

  const gaugeData: ProfilerGaugeData = assembled
    ? {
        gaugeSeries: assembled.gaugeSeries,
        gaugeCapacities: assembled.gaugeCapacities,
        memoryAllocEvents: assembled.memoryAllocEvents,
        gcEvents: assembled.gcEvents,
        gcSuspensions: assembled.gcSuspensions,
        threadNames: mergedThreadNames,
        offCpuBySlot: assembled.offCpuBySlot,
      }
    : { ...EMPTY_GAUGE_DATA, threadNames: mergedThreadNames };

  // Unified slot → {name, kind} map. Replay zips the cache's persistent `threadNames` and
  // `threadKinds` maps; live overlays the SSE-delta-driven `liveThreadInfos` on top so a
  // mid-session connect honours the kind byte that the engine's catch-up replay carries.
  const threadInfos = useMemo<Map<number, SlotThreadInfo>>(() => {
    const out = new Map<number, SlotThreadInfo>();
    const names = assembled?.threadNames;
    const kinds = assembled?.threadKinds;
    if (names) {
      for (const [slot, name] of names) {
        out.set(slot, { name, kind: kinds?.get(slot) ?? 3 /* Other */ });
      }
    }
    if (isLive) {
      for (const [slot, info] of liveThreadInfos) {
        out.set(slot, { name: info.name, kind: info.kind });
      }
    }
    return out;
  }, [isLive, assembled, liveThreadInfos]);

  const pendingRangesUs = useMemo(() => {
    void entriesVersion;
    if (!hasSelection) return [];
    return computePendingRangesUs(cacheRef.current, manifest, tickSummaries, viewRange);
  }, [entriesVersion, manifest, tickSummaries, viewRange, hasSelection]);

  return { ticks, traceMetadata, gaugeData, threadInfos, pendingRangesUs };
}
