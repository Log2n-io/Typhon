import type { SpanData, TickData } from '@/libs/profiler/model/traceModel';

/**
 * Shared test-fixture builders for profiler model tests. Centralises the boilerplate that
 * otherwise gets copy-pasted into every `*.test.ts` — constructing a full `TickData` with all the
 * per-kind arrays, running-max buffers, and typed-maps is ~60 LOC of ceremony per fixture.
 *
 * Reused by:
 *   - `libs/profiler/canvas/timeAreaLayout.test.ts` (`deriveSlotInfo`, `buildLayout`, `getVisibleTicks`)
 *   - future `traceModel.test.ts` (`processTickEvents`, `mergeTickData`, `aggregateGaugeData`)
 *   - future `timeAreaHitTest.test.ts` (cross-tick span iteration)
 *   - future `chunkCache.test.ts` (`computePendingRangesUs`, `assembleTickViewAndNumbers`)
 */

// TraceEventKind is a const enum in types.ts — Vitest isn't configured to inline const enums, so we
// pass a numeric literal cast to the right type. Tests only care about the span-build behaviour,
// not the specific kind byte.
const SPAN_KIND_DEFAULT = 10 as unknown as SpanData['kind'];

export interface BuildSpanOptions {
  name?: string;
  startUs: number;
  endUs: number;
  threadSlot?: number;
  depth?: number;
  spanId?: string;
  parentSpanId?: string;
  kickoffDurationUs?: number;
}

/**
 * Build one {@link SpanData} with sensible defaults. Required: `startUs`, `endUs`. Everything
 * else gets a deterministic default so test data stays readable.
 */
export function buildSpan(opts: BuildSpanOptions): SpanData {
  const {
    name = 'span',
    startUs,
    endUs,
    threadSlot = 0,
    depth,
    spanId,
    parentSpanId,
    kickoffDurationUs,
  } = opts;
  const span: SpanData = {
    kind: SPAN_KIND_DEFAULT,
    name,
    threadSlot,
    startUs,
    endUs,
    durationUs: endUs - startUs,
  };
  if (depth !== undefined) span.depth = depth;
  if (spanId !== undefined) span.spanId = spanId;
  if (parentSpanId !== undefined) span.parentSpanId = parentSpanId;
  if (kickoffDurationUs !== undefined) span.kickoffDurationUs = kickoffDurationUs;
  return span;
}

export interface BuildTickOptions {
  tickNumber: number;
  startUs: number;
  durationUs: number;
  spans?: SpanData[];
}

/**
 * Build a {@link TickData} populated with the given spans. Groups them by `threadSlot`, computes
 * per-slot `spanEndMaxByThreadSlot` running-max arrays, and initialises every per-kind collection
 * to an empty array — so the result passes any shape check from {@link TickData}'s consumers.
 *
 * All spans must already have matching `threadSlot` values; the helper doesn't filter them.
 */
export function buildTick(opts: BuildTickOptions): TickData {
  const { tickNumber, startUs, durationUs, spans = [] } = opts;
  const endUs = startUs + durationUs;

  const spansByThreadSlot = new Map<number, SpanData[]>();
  for (const s of spans) {
    let arr = spansByThreadSlot.get(s.threadSlot);
    if (!arr) { arr = []; spansByThreadSlot.set(s.threadSlot, arr); }
    arr.push(s);
  }

  const spanEndMaxByThreadSlot = new Map<number, Float64Array>();
  const spanMaxDepthByThreadSlot = new Map<number, number>();
  for (const [slot, arr] of spansByThreadSlot) {
    arr.sort((a, b) => a.startUs - b.startUs);
    const em = new Float64Array(arr.length);
    let running = -Infinity;
    let maxDepth = 0;
    for (let i = 0; i < arr.length; i++) {
      if (arr[i].endUs > running) running = arr[i].endUs;
      em[i] = running;
      const d = arr[i].depth ?? 0;
      if (d > maxDepth) maxDepth = d;
    }
    spanEndMaxByThreadSlot.set(slot, em);
    spanMaxDepthByThreadSlot.set(slot, maxDepth);
  }

  // spanEndMaxRunning is the global (cross-slot) running max of endUs across all spans in the tick.
  const sortedSpans = [...spans].sort((a, b) => a.startUs - b.startUs);
  const globalEndMax = new Float64Array(sortedSpans.length);
  let gRunning = -Infinity;
  for (let i = 0; i < sortedSpans.length; i++) {
    if (sortedSpans[i].endUs > gRunning) gRunning = sortedSpans[i].endUs;
    globalEndMax[i] = gRunning;
  }

  const empty = new Float64Array(0);
  return {
    tickNumber,
    startUs,
    endUs,
    durationUs,
    chunks: [],
    phases: [],
    phaseMarkers: [],
    skips: [],
    spans: sortedSpans,
    spanEndMaxRunning: globalEndMax,
    spansByThreadSlot,
    spanEndMaxByThreadSlot,
    spanMaxDepthByThreadSlot,
    diskReads: [], diskReadsEndMax: empty,
    diskWrites: [], diskWritesEndMax: empty,
    cacheMisses: [], cacheMissesEndMax: empty,
    cacheFlushes: [], cacheFlushesEndMax: empty,
    cacheFetch: [], cacheFetchEndMax: empty,
    cacheAlloc: [], cacheAllocEndMax: empty,
    cacheEvict: [], cacheEvictEndMax: empty,
    txCommits: [], txCommitsEndMax: empty,
    txRollbacks: [], txRollbacksEndMax: empty,
    txPersists: [], txPersistsEndMax: empty,
    walFlushes: [], walFlushesEndMax: empty,
    walWaits: [], walWaitsEndMax: empty,
    checkpointCycles: [], checkpointCyclesEndMax: empty,
    systemDurations: new Map(),
    memoryAllocEvents: [],
    gcEvents: [],
    gcSuspensions: [],
    threadInfos: [],
    contextSwitches: new Map(),
    rawEvents: [],
  };
}

/**
 * Build an empty tick — no spans, no chunks, just the time bounds. Handy for
 * `getVisibleTicks` filter tests.
 */
export function buildEmptyTick(tickNumber: number, startUs: number, durationUs: number): TickData {
  return buildTick({ tickNumber, startUs, durationUs, spans: [] });
}
