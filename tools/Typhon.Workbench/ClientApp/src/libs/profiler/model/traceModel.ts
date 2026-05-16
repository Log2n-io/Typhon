import {
  TraceEventKind,
  type TraceEvent,
  type SystemDef,
  type TraceMetadata,
  SpanKindNames,
  type GaugeId,
  FIXED_AT_INIT_GAUGES,
  waitReasonToCategory,
} from './types';

/** A chunk span: one rectangle in the Gantt chart */
export interface ChunkSpan {
  systemIndex: number;
  systemName: string;
  chunkIndex: number;
  threadSlot: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  entitiesProcessed: number;
  totalChunks: number;
  isParallel: boolean;
}

/** A tick phase span */
export interface PhaseSpan {
  phase: number;
  phaseName: string;
  startUs: number;
  endUs: number;
  durationUs: number;
  /**
   * SpanId of the underlying RuntimePhaseSpan record. Used by the Detail pane so the user can read the
   * phase's id and cross-reference it against `parentSpanId` on child spans (e.g. to verify that a
   * `PageCacheFlush` is actually attached to the `UoW Flush` phase). Undefined for legacy traces that only
   * carry the deprecated PhaseStart/PhaseEnd instant pair.
   */
  spanId?: string;
}

/**
 * Single-point marker rendered as a glyph in the phase track. Surfaces tick-lifecycle landmarks that aren't durations
 * (UoW create / UoW flush, future overload transitions). Drawn alongside {@link PhaseSpan} bars by `drawPhases`.
 */
export interface PhaseMarker {
  /** TraceEventKind value — drives the glyph shape and tooltip label. */
  kind: number;
  /** Short human label for tooltip and legend. */
  label: string;
  /** Timestamp on the global timeline. */
  timestampUs: number;
  /** Optional one-line tooltip extension (e.g. UoW Flush exposes its dirty changeCount). */
  detail?: string;
}

/** Skip event for a system */
export interface SkipEvent {
  systemIndex: number;
  systemName: string;
  reason: number;
  reasonName: string;
  timestampUs: number;
}

/**
 * One (gaugeId → value) observation at a tick boundary. Produced from a <c>PerTickSnapshot</c> record — the wire format packs multiple
 * gauges into a single record, but the viewer thinks in per-gauge time series, so this interface represents one sample of one series.
 */
export interface GaugeSample {
  tickNumber: number;
  timestampUs: number;
  value: number;
}

/**
 * One time series of a single gauge across ticks. Samples are inserted in tick-order during chunk processing and stay sorted; the
 * viewer binary-searches by timestamp to find the first visible sample in the viewport.
 */
export interface GaugeSeries {
  id: GaugeId;
  samples: GaugeSample[];
}

/**
 * Packed snapshot at a single tick — all gauge values emitted by the scheduler at that tick boundary. Duplicates the information in
 * <c>GaugeSeries[]</c> but indexed by tick rather than by gauge, which is the shape a few viewer paths want (hover tooltips show
 * "every value at tick N" rather than "value of gauge X over time").
 */
export interface GaugeSnapshot {
  tickNumber: number;
  timestampUs: number;
  values: Map<GaugeId, number>;
}

/**
 * One discrete unmanaged allocation or free event, as emitted by <c>PinnedMemoryBlock</c> ctor/dispose via
 * <c>TyphonEvent.EmitMemoryAlloc</c>. Rendered by the viewer as a triangle marker on the memory track (up = alloc, down = free),
 * colour-coded by <c>sourceTag</c>.
 */
export interface MemoryAllocEventData {
  tickNumber: number;
  timestampUs: number;
  threadSlot: number;
  direction: number;       // 0 = alloc, 1 = free (MemoryAllocDirection)
  sourceTag: number;        // u16 interned tag (MemoryAllocSource)
  sizeBytes: number;
  totalAfterBytes: number;
}

/**
 * One .NET runtime GC-boundary event: either a GcStart (kind 7) or GcEnd (kind 8). Rendered as a triangle marker on the GC gauge
 * track, colour-coded by generation. The <c>durationUs</c> is populated only for GcEnd (carries the GC pause duration).
 */
export interface GcEvent {
  kind: TraceEventKind.GcStart | TraceEventKind.GcEnd;
  tickNumber: number;
  timestampUs: number;
  generation: number;
  gcCount: number;
  reason?: number;         // GcStart only — GcReason enum value
  gcType?: number;         // GcStart only — GcType enum value
  pauseDurationUs?: number; // GcEnd only
  promotedBytes?: number;   // GcEnd only
}

/**
 * A user-clicked marker on a gauge track — either a memory allocation event or a GC boundary. Discriminated-union rather than a
 * flat struct because the two marker kinds carry disjoint fields and the detail pane renders them with different layouts.
 * Threaded through App → Workspace → GraphArea (selection source) and App → Workspace → DetailPane (render).
 */
export type MarkerSelection =
  | { kind: 'memory-alloc'; event: MemoryAllocEventData }
  | { kind: 'gc'; event: GcEvent };

/**
 * One GC suspension window (kind 75). Rendered as a red/orange vertical bar on the GC gauge track spanning the EE-suspension
 * duration. These are the "stop-the-world" pauses that directly impact tick latency.
 */
export interface GcSuspensionEvent {
  tickNumber: number;
  startUs: number;
  durationUs: number;
  threadSlot: number;
}

/**
 * One ON-CPU slice for a Typhon-registered thread, decoded from a ThreadContextSwitch (kind 254) record. The viewer never
 * renders slices directly — they are the raw input from which the off-CPU GAPS are derived (see {@link OffCpuStore}).
 * Stored per tick in {@link TickData.contextSwitches}, keyed by the slice's `targetSlotIdx`.
 */
export interface OnCpuSlice {
  /** When the thread got the CPU (µs, global timeline). */
  startUs: number;
  /** How long it held the CPU (µs). */
  durationUs: number;
  /** Raw `ThreadWaitReason` byte — why the thread left the CPU at the END of this slice. */
  waitReason: number;
  /** Post-switch `System.Diagnostics.ThreadState` raw byte. */
  threadState: number;
  /** True when the CPU went to the System Idle thread after this slice. */
  gettingIdle: boolean;
  /** Logical CPU id the slice ran on. */
  processorNumber: number;
  /** Ready-queue latency that preceded this slice (µs) — time between becoming runnable and getting the CPU. */
  readyTimeUs: number;
}

/**
 * Struct-of-arrays store of off-CPU intervals for one thread slot — the GAPS between consecutive {@link OnCpuSlice}s.
 * Built once per cache reassembly by {@link aggregateGaugeData}, never per frame. Parallel arrays (one logical interval
 * spread across them at a shared index) keep ~27 B/interval with zero per-interval JS objects, and `startUs` doubles as
 * the binary-search key for viewport culling. Both `startUs` and `endUs` are monotonically ascending (the intervals are
 * non-overlapping, sorted gaps), so the renderer can binary-search either edge.
 */
export interface OffCpuStore {
  /** Interval start (µs) — sorted ascending. Binary-search key. */
  startUs: Float64Array;
  /** Interval end (µs) — also ascending (non-overlapping gaps). */
  endUs: Float64Array;
  /** Ready-queue latency of the slice that ENDS this gap (µs). */
  readyTimeUs: Float64Array;
  /** OffCpuCategory byte per interval — the coarse coloring bucket. */
  category: Uint8Array;
  /** Raw ThreadWaitReason byte per interval, for the tooltip / detail label. */
  waitReason: Uint8Array;
  /** Logical CPU the thread had been running on before the gap. */
  processorNumber: Uint8Array;
}

/**
 * A single off-CPU interval extracted from an {@link OffCpuStore} at a given index — the shape carried in a profiler
 * selection and rendered by the detail pane. Materialized only on click (one object), never in bulk.
 */
export interface OffCpuInterval {
  threadSlot: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  readyTimeUs: number;
  category: number;
  waitReason: number;
  processorNumber: number;
}

/**
 * Materialize one {@link OffCpuInterval} object from an {@link OffCpuStore} at the given index — used by the hit-test
 * when the user clicks an off-CPU bar, and by tests. This is the ONLY place a per-interval object is created; the
 * renderer reads the struct-of-arrays directly.
 */
export function materializeOffCpuInterval(store: OffCpuStore, index: number, threadSlot: number): OffCpuInterval {
  const startUs = store.startUs[index];
  const endUs = store.endUs[index];
  return {
    threadSlot,
    startUs,
    endUs,
    durationUs: endUs - startUs,
    readyTimeUs: store.readyTimeUs[index],
    category: store.category[index],
    waitReason: store.waitReason[index],
    processorNumber: store.processorNumber[index],
  };
}

/**
 * A generic Typhon span captured via `TyphonEvent.BeginXxx` (B+Tree / Transaction / ECS / PageCache / Cluster / NamedSpan).
 * Every span-kind record arrives already with its duration in the wire format, so no Start/End pairing is needed.
 *
 * **Async-completion enrichment.** For the three PageCache.* kinds that can emit a paired completion record
 * (DiskRead / DiskWrite / Flush), the kickoff span is pushed first with `durationUs` set to the synchronous kickoff cost, then
 * replaced in-place when the matching `*Completed` record arrives: `endUs` and `durationUs` are rewritten to cover the full async
 * tail, and `kickoffDurationUs` preserves the original synchronous kickoff cost so the viewer can render both numbers. When no
 * completion record arrives (completion kind suppressed, or the record was dropped from the ring), the span stays as kickoff-only
 * and `kickoffDurationUs` is undefined.
 */
export interface SpanData {
  kind: TraceEventKind;
  name: string;
  threadSlot: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  spanId?: string;
  parentSpanId?: string;
  traceIdHi?: string;
  traceIdLo?: string;
  /** Original synchronous-kickoff duration in µs, set only after the async-completion record has folded into this span. */
  kickoffDurationUs?: number;
  /**
   * Parent-chain nesting depth. 0 = no parent (or parent not captured in this tick). Computed at tick processing time by walking
   * parentSpanId links up to the root. Used by the GraphArea OTel spans track to assign each span a stable row by hierarchical
   * depth rather than by kind — which is what the OTel model actually describes: spans form a tree, row-by-depth renders that
   * tree the same way a flame graph does. Sanity-capped at 32 to prevent infinite loops if the chain ever cycles.
   */
  depth?: number;
  /**
   * Display row assigned by greedy interval packing across all spans for this thread slot. Not the
   * same as <c>depth</c>: `depth` is the semantic call-stack nesting at record time (can be 0 for
   * multiple temporally-overlapping siblings like a long Checkpoint.Cycle and its sub-events when
   * the recorder only sees them at completion). `renderDepth` guarantees two overlapping bars land
   * on different visual rows. Mutated by <c>deriveSlotInfo</c> after cross-tick assembly; stable
   * across frames until the chunk cache version bumps.
   */
  renderDepth?: number;
  /** Full DTO event — carried so the DetailPane can show kind-specific fields without duplicating them on SpanData. */
  rawEvent?: TraceEvent;
}

/** All data for a single tick */
export interface TickData {
  tickNumber: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  chunks: ChunkSpan[];
  phases: PhaseSpan[];
  /**
   * Single-point markers in the phase track — UoW create / UoW flush, future overload transitions. Sparse: typically 1–3 entries
   * per tick, ordered by timestamp.
   */
  phaseMarkers: PhaseMarker[];
  skips: SkipEvent[];
  spans: SpanData[];
  /**
   * Parallel to <c>spans</c>: running max of <c>spans[0..i].endUs</c>. Enables O(log N) left-edge visibility search —
   * binary-search this for the first index whose running-max ≥ viewStartUs and iteration can start there with zero
   * off-screen-left scanning. Without this, a naive walk-back from the sorted-by-startUs binary search misses any long
   * parent span that was interrupted by shorter children finishing before the viewport.
   */
  spanEndMaxRunning: Float64Array;
  /**
   * Spans grouped by owning thread slot. Each inner array is sorted by startUs (inherited from the global sort on <c>spans</c>).
   * Populated in <see cref="processTickEvents"/>. Enables per-slot rendering in <c>GraphArea</c> where OTel span bars are drawn
   * beneath the chunk bar of their owning thread's lane instead of in a separate track.
   */
  spansByThreadSlot: Map<number, SpanData[]>;
  /**
   * Per-slot running-max endUs array, keyed by thread slot — same shape as <see cref="spanEndMaxRunning"/> but scoped to
   * one slot at a time, so the render loop can binary-search each lane's span list independently.
   */
  spanEndMaxByThreadSlot: Map<number, Float64Array>;
  /**
   * Deepest observed span nesting per thread slot, used by <c>GraphArea.buildLayout</c> to grow each slot lane just tall
   * enough to contain its deepest span chain. A slot with no spans has no entry in the map (renders with only the chunk row).
   */
  spanMaxDepthByThreadSlot: Map<number, number>;
  /**
   * Disk read operations (PageCacheDiskRead + orphan DiskReadCompleted) projected across all thread slots, sorted by startUs.
   * Used by the Disk IO track to render green bars on the "reads" sub-row. When async-completion folding is active, each
   * span's <c>durationUs</c> is the full device occupancy time (kickoff → completion), not just the synchronous kickoff.
   */
  diskReads: SpanData[];
  diskReadsEndMax: Float64Array;
  /**
   * Disk write operations (PageCacheDiskWrite + PageCacheFlush + orphan WriteCompleted/FlushCompleted) projected across all
   * thread slots, sorted by startUs. Used by the Disk IO track to render blue bars on the "writes" sub-row.
   */
  diskWrites: SpanData[];
  diskWritesEndMax: Float64Array;
  /** Cache-miss chain (Fetch + AllocatePage + PageEvicted) projected across all slots, sorted by startUs. Each kind draws
   *  in its own color on the same "Misses" sub-row so overlapping parent/child spans (Fetch wrapping Allocate) are visible. */
  cacheMisses: SpanData[];
  cacheMissesEndMax: Float64Array;
  /** Page cache flush operations (checkpoint coordination) projected across all slots, sorted by startUs. */
  cacheFlushes: SpanData[];
  cacheFlushesEndMax: Float64Array;
  cacheFetch: SpanData[];
  cacheFetchEndMax: Float64Array;
  cacheAlloc: SpanData[];
  cacheAllocEndMax: Float64Array;
  cacheEvict: SpanData[];
  cacheEvictEndMax: Float64Array;
  // Transaction projection
  txCommits: SpanData[];
  txCommitsEndMax: Float64Array;
  txRollbacks: SpanData[];
  txRollbacksEndMax: Float64Array;
  txPersists: SpanData[];
  txPersistsEndMax: Float64Array;
  // WAL projection
  walFlushes: SpanData[];
  walFlushesEndMax: Float64Array;
  walWaits: SpanData[];
  walWaitsEndMax: Float64Array;
  // Checkpoint projection
  checkpointCycles: SpanData[];
  checkpointCyclesEndMax: Float64Array;
  /** Per-system aggregate duration in µs (for heat map) */
  systemDurations: Map<number, number>;
  /**
   * Gauge snapshot for this tick — at most one per tick (emitted by the scheduler at end-of-tick). May be undefined if the tick
   * predates the first snapshot, or if gauge emission is disabled (<c>ProfilerGaugesActive == false</c>).
   */
  gaugeSnapshot?: GaugeSnapshot;
  /**
   * Discrete memory alloc/free events that landed in this tick. Ordered by timestamp. Unbounded in principle (a tick can allocate
   * thousands of small blocks) but in practice driven by subsystems' alloc patterns — typically sparse.
   */
  memoryAllocEvents: MemoryAllocEventData[];
  /** GcStart + GcEnd instant events in this tick. Triangle markers on the GC gauge track, colour-coded by generation. */
  gcEvents: GcEvent[];
  /** GcSuspension spans that started (or ended) inside this tick. Drawn as red/orange vertical bars on the GC gauge track. */
  gcSuspensions: GcSuspensionEvent[];
  /**
   * ThreadInfo instant events observed in this tick — one per slot claim. Cross-tick aggregation folds these into
   * <c>TraceMetadata.threadNames</c> in <see cref="aggregateGaugeData"/>. Typically emitted once per thread at first emission,
   * so this is usually empty after the initial slot claims.
   */
  threadInfos: ThreadInfoEvent[];
  /**
   * ON-CPU slices observed in this tick, keyed by the owning thread slot (`targetSlotIdx`). Raw per-tick storage — the
   * off-CPU GAPS between slices are derived cross-tick by <see cref="aggregateGaugeData"/> (a gap can span a TickStart, so
   * it cannot be computed per tick). Each inner array is sorted by `startUs`. Empty for traces with no scheduling data.
   */
  contextSwitches: Map<number, OnCpuSlice[]>;
  /**
   * The raw events that went into building this TickData. Stored verbatim so that <c>mergeTickData</c> can combine two
   * partial <c>TickData</c> entries (from an intra-tick-split chunk pair) by concatenating their <c>rawEvents</c> and re-running
   * <c>processTickEvents</c> on the union — guaranteeing byte-identical output to a single-pass build on the full events.
   *
   * Memory cost: ~2× per-TickData (the derived state + the raw events). Acceptable because (a) TickData objects are short-lived
   * LRU cache entries capped by the 500 MB budget, (b) raw TraceEvent records are the same size as the SpanData entries they
   * produce, so we're not doubling the total — more like +50-70%, (c) the merge path is rare (intra-tick splits only fire on
   * pathological ticks). If memory pressure shows up in real workloads, the optimisation is to only retain rawEvents for
   * <b>boundary</b> ticks — the first and last tick of each chunk, the only candidates for participating in a merge.
   */
  rawEvents: TraceEvent[];
}

/** One ThreadInfo record (kind 77) — slot ownership metadata emitted when a producer thread claims its slot. */
export interface ThreadInfoEvent {
  threadSlot: number;
  managedThreadId: number;
  name: string;
  timestampUs: number;
  /** Engine's `Typhon.Profiler.ThreadKind` (Main=0, Worker=1, Pool=2, Other=3). Undefined for pre-v4 traces. */
  kind?: number;
}

/** Processed trace ready for rendering */
export interface ProcessedTrace {
  metadata: TraceMetadata;
  ticks: TickData[];
  /** All tick numbers, sorted */
  tickNumbers: number[];
  /** Min/max timestamps across all ticks */
  globalStartUs: number;
  globalEndUs: number;
  /** Max per-system duration across all ticks (for heat map color scaling) */
  maxSystemDurationUs: number;
  /** Max tick duration */
  maxTickDurationUs: number;
  /** P95 tick duration for timeline bar height scaling */
  p95TickDurationUs: number;
  /** System color palette */
  systemColors: string[];
  /**
   * Per-tick summary from the sidecar cache, one entry per tick in the whole file. Always-resident across the full timeline so the overview can
   * render immediately on open without loading any detail events. Present when a `/api/trace/open` response has been consumed; undefined for
   * live-mode traces (no cache in that path yet).
   */
  summary?: import('./types').TickSummary[];
  /**
   * Per-gauge time series across the currently-loaded ticks. Rebuilt from <c>TickData.gaugeSnapshot</c> entries when the cache is
   * reassembled (<see cref="chunkCache.ts#assembleTickViewAndNumbers"/>). Sparse: only gauges that were actually observed in at
   * least one loaded tick have an entry.
   */
  gaugeSeries?: Map<GaugeId, GaugeSeries>;
  /**
   * Fixed-at-init gauge values (<see cref="FIXED_AT_INIT_GAUGES"/>) cached from the first snapshot that carried them. These are
   * capacities — <c>PageCacheTotalPages</c>, <c>TransientStoreMaxBytes</c>, <c>WalCommitBufferCapacityBytes</c>,
   * <c>WalStagingPoolCapacity</c>. The scheduler emits them only in the first snapshot of a session; the viewer caches them here
   * so reference-line overlays can keep drawing the ceiling on every subsequent tick.
   */
  gaugeCapacities?: Map<GaugeId, number>;
  /**
   * Flat, chronologically-sorted list of <c>MemoryAllocEvent</c> records across the currently-loaded ticks. Denormalized from
   * per-tick arrays to make viewport-range filtering a single binary search.
   */
  memoryAllocEvents?: MemoryAllocEventData[];
  /** Flat list of GcStart + GcEnd events across loaded ticks — markers on the GC gauge track. */
  gcEvents?: GcEvent[];
  /** Flat list of GcSuspension bars across loaded ticks. */
  gcSuspensions?: GcSuspensionEvent[];
  /**
   * Per-thread-slot off-CPU intervals across the currently-loaded ticks — the GAPS where a thread was switched out, derived
   * cross-tick from <c>ThreadContextSwitch</c> records. Struct-of-arrays for compactness; rendered as overlay bars on each
   * thread's lane. Undefined / empty-map for traces with no OS scheduling data (non-Windows, or the pump never ran).
   */
  offCpuBySlot?: Map<number, OffCpuStore>;
}

const PHASE_NAMES: Record<number, string> = {
  0: 'System Dispatch',
  1: 'UoW Flush',
  2: 'Write Tick Fence',
  3: 'Output Phase',
  4: 'Tier Index Rebuild',
  5: 'Dormancy Sweep'
};

const SKIP_REASON_NAMES: Record<number, string> = {
  0: 'Not Skipped',
  1: 'ShouldRun False',
  2: 'Empty Input',
  3: 'Empty Events',
  4: 'Throttled',
  5: 'Shed',
  6: 'Exception',
  7: 'Dependency Failed'
};

/** Color palette for systems — visually distinct, dark-theme friendly */
const SYSTEM_PALETTE = [
  '#e94560', '#4ecdc4', '#ffe66d', '#95e1d3', '#f38181',
  '#aa96da', '#a8d8ea', '#fcbad3', '#c3aed6', '#b8de6f',
  '#ff9a76', '#679b9b', '#ffc4a3', '#e8a87c', '#41b3a3',
  '#d63447', '#f57b51', '#f6efa6', '#5ee7df', '#b490ca',
];

/** Generate system color based on index */
function generateSystemColors(count: number): string[] {
  const colors: string[] = [];
  for (let i = 0; i < count; i++) {
    colors.push(SYSTEM_PALETTE[i % SYSTEM_PALETTE.length]);
  }
  return colors;
}

/** Process raw records into renderable tick structures */
export function processTrace(metadata: TraceMetadata, events: TraceEvent[]): ProcessedTrace {
  const systems = metadata.systems;
  const systemColors = generateSystemColors(systems.length);

  // Group records by derived tick number (the server already assigns tickNumber on every record).
  const tickMap = new Map<number, TraceEvent[]>();
  for (const evt of events) {
    let list = tickMap.get(evt.tickNumber);
    if (!list) {
      list = [];
      tickMap.set(evt.tickNumber, list);
    }
    list.push(evt);
  }

  const ticks: TickData[] = [];
  let maxSystemDurationUs = 0;
  let maxTickDurationUs = 0;

  for (const [tickNumber, tickEvents] of tickMap) {
    // processTrace takes full-session event arrays (file-upload path) — every tick is self-contained, never a continuation.
    const tickData = processTickEvents(tickNumber, tickEvents, systems);
    if (tickData.durationUs > maxTickDurationUs) {
      maxTickDurationUs = tickData.durationUs;
    }
    for (const dur of tickData.systemDurations.values()) {
      if (dur > maxSystemDurationUs) {
        maxSystemDurationUs = dur;
      }
    }
    ticks.push(tickData);
  }

  ticks.sort((a, b) => a.tickNumber - b.tickNumber);
  const tickNumbers = ticks.map(t => t.tickNumber);

  const globalStartUs = ticks.length > 0 ? ticks[0].startUs : 0;
  const globalEndUs = ticks.length > 0 ? ticks[ticks.length - 1].endUs : 0;

  const sortedDurations = ticks.map(t => t.durationUs).sort((a, b) => a - b);
  // Percentile-of-N with small N is statistically meaningless — `Math.floor(1 * 0.95) = 0` returns the 0th percentile, which is
  // the MIN, not the 95th. For the tick-timeline height scaling we want "worst of the observed few" as the ceiling, so fall
  // back to the max when the sample size is below the sanity threshold (20 was the agent's suggestion; that's roughly where
  // a floor(N*0.95) starts landing on a meaningful index). For the common case (hundreds+ of ticks) this is a no-op.
  const p95Idx = Math.floor(sortedDurations.length * 0.95);
  const p95TickDurationUs = sortedDurations.length === 0
    ? 0
    : sortedDurations.length < 20
      ? sortedDurations[sortedDurations.length - 1]  // fall back to max on tiny samples
      : sortedDurations[Math.min(p95Idx, sortedDurations.length - 1)];

  const { gaugeSeries, gaugeCapacities, memoryAllocEvents, gcEvents, gcSuspensions, threadNames, offCpuBySlot } = aggregateGaugeData(ticks);

  // Fold the aggregated slot→name map into metadata so lane labels ("DagScheduler", "TyphonProfilerGcIngest", ...) can render. Mutating
  // `metadata` here is safe: the caller passes a fresh instance per processTrace call, and threadNames is additive-only.
  if (threadNames.size > 0) {
    const existing = metadata.threadNames ?? {};
    const merged: Record<number, string> = { ...existing };
    for (const [slot, name] of threadNames) {
      merged[slot] = name;
    }
    metadata.threadNames = merged;
  }

  return {
    metadata,
    ticks,
    tickNumbers,
    globalStartUs,
    globalEndUs,
    maxSystemDurationUs,
    maxTickDurationUs,
    p95TickDurationUs,
    systemColors,
    gaugeSeries,
    gaugeCapacities,
    memoryAllocEvents,
    gcEvents,
    gcSuspensions,
    offCpuBySlot,
  };
}

/**
 * Find all ticks that overlap with the given absolute time range. Uses strict half-open semantics: a tick is considered overlapping iff
 * `tick.endUs > startUs && tick.startUs < endUs`. Back-to-back ticks that only TOUCH the boundary (tick N+1 starts exactly where tick N
 * ends) are NOT counted as overlapping — otherwise a viewRange covering exactly tick N would spill GraphArea's render list into ticks
 * N-1 and N+1, which is what "tick 2 shows no bar" was actually caused by (the empty sliver gets hidden between its much-larger neighbours).
 */
export function getTicksInRange(trace: ProcessedTrace, startUs: number, endUs: number): TickData[] {
  return trace.ticks.filter(t => t.endUs > startUs && t.startUs < endUs);
}

/** Find the tick index (in trace.ticks array) for a given tick number */
export function findTickIndex(trace: ProcessedTrace, tickNumber: number): number {
  return trace.ticks.findIndex(t => t.tickNumber === tickNumber);
}

/**
 * Build a {@link TickData} from a tick's raw events.
 */
export function processTickEvents(tickNumber: number, events: TraceEvent[], systems: SystemDef[]): TickData {
  let startUs = Infinity;
  let endUs = -Infinity;

  const chunks: ChunkSpan[] = [];
  const phases: PhaseSpan[] = [];
  const phaseMarkers: PhaseMarker[] = [];
  const skips: SkipEvent[] = [];
  const spans: SpanData[] = [];
  const systemDurations = new Map<number, number>();
  const memoryAllocEvents: MemoryAllocEventData[] = [];
  const gcEvents: GcEvent[] = [];
  const gcSuspensions: GcSuspensionEvent[] = [];
  const threadInfos: ThreadInfoEvent[] = [];
  const contextSwitches = new Map<number, OnCpuSlice[]>();
  let gaugeSnapshot: GaugeSnapshot | undefined;

  // Phases are still emitted as Start/End instant pairs — keep a short-lived map to pair them up.
  const openPhases = new Map<number, TraceEvent>();

  // ── Async-completion folding (PageCache.DiskRead/DiskWrite/Flush + their *Completed counterparts) ──
  //
  // Kickoff and completion records share the same SpanId (by design — the correlator). The consumer-side sort is by start
  // timestamp, and BOTH records carry the same begin-time as their start, so when the kickoff and completion tie the sort
  // order is undefined (introsort is not stable). We need to handle BOTH orderings without duplicating the span:
  //
  //   • kickoffById: maps spanId → the already-pushed kickoff span so a later completion can rewrite its duration in-place.
  //   • pendingCompletion: maps spanId → the whole TraceEvent for completions that arrived BEFORE their kickoff. When the
  //     kickoff is later processed it consults this map and folds immediately. Any entries left at tick end (orphan
  //     completions with no matching kickoff in this tick) are pushed as standalone spans so the data isn't silently lost.
  //
  // Cross-tick completions (I/O taking longer than one tick) land in a different `events` array and won't find their match
  // in either map — the completion is pushed as a standalone span at the end-of-tick orphan flush below, so the data is
  // never lost, just not visually paired with its kickoff. That's rare on a healthy SSD and can be inspected via SpanId
  // search in the detail pane.
  const kickoffById = new Map<string, SpanData>();
  const pendingCompletion = new Map<string, TraceEvent>();

  for (const evt of events) {
    switch (evt.kind) {
      case TraceEventKind.TickStart:
        startUs = evt.timestampUs;
        break;

      case TraceEventKind.TickEnd:
        endUs = evt.timestampUs;
        break;

      case TraceEventKind.PhaseStart:
        if (evt.phase !== undefined) {
          openPhases.set(evt.phase, evt);
        }
        break;

      case TraceEventKind.PhaseEnd: {
        if (evt.phase === undefined) break;
        const open = openPhases.get(evt.phase);
        if (open) {
          const dur = evt.timestampUs - open.timestampUs;
          phases.push({
            phase: evt.phase,
            phaseName: PHASE_NAMES[evt.phase] ?? `Phase ${evt.phase}`,
            startUs: open.timestampUs,
            endUs: evt.timestampUs,
            durationUs: dur
          });
          openPhases.delete(evt.phase);
        }
        break;
      }

      case TraceEventKind.SystemSkipped: {
        const sysIdx = evt.systemIndex ?? 0;
        const reason = evt.skipReason ?? 0;
        skips.push({
          systemIndex: sysIdx,
          systemName: systems[sysIdx]?.name ?? `System[${sysIdx}]`,
          reason,
          reasonName: SKIP_REASON_NAMES[reason] ?? `Unknown(${reason})`,
          timestampUs: evt.timestampUs
        });
        break;
      }

      case TraceEventKind.GcStart: {
        if (evt.generation !== undefined && evt.gcCount !== undefined) {
          gcEvents.push({
            kind: TraceEventKind.GcStart,
            tickNumber: evt.tickNumber,
            timestampUs: evt.timestampUs,
            generation: evt.generation,
            gcCount: evt.gcCount,
            reason: evt.gcReason,
            gcType: evt.gcType,
          });
        }
        break;
      }

      case TraceEventKind.GcEnd: {
        if (evt.generation !== undefined && evt.gcCount !== undefined) {
          gcEvents.push({
            kind: TraceEventKind.GcEnd,
            tickNumber: evt.tickNumber,
            timestampUs: evt.timestampUs,
            generation: evt.generation,
            gcCount: evt.gcCount,
            pauseDurationUs: evt.gcPauseDurationUs,
            promotedBytes: evt.gcPromotedBytes,
          });
        }
        break;
      }

      case TraceEventKind.MemoryAllocEvent: {
        // Discrete alloc/free event. Appended in arrival order — inherits the overall event sort so the per-tick list stays sorted by timestamp.
        if (evt.direction !== undefined && evt.sizeBytes !== undefined && evt.totalAfterBytes !== undefined) {
          memoryAllocEvents.push({
            tickNumber: evt.tickNumber,
            timestampUs: evt.timestampUs,
            threadSlot: evt.threadSlot,
            direction: evt.direction,
            sourceTag: evt.sourceTag ?? 0,
            sizeBytes: evt.sizeBytes,
            totalAfterBytes: evt.totalAfterBytes,
          });
        }
        break;
      }

      case TraceEventKind.ThreadInfo: {
        // ThreadInfo lands in the ring as soon as a thread claims its slot. We pass the record through here so the cross-tick
        // aggregation in aggregateGaugeData can fold it into metadata.threadNames. A slot without a set Thread.Name arrives with
        // an empty/undefined name — skip those so the viewer can fall back to "Slot {n}" rather than showing a blank label.
        // `threadKind` is the v4+ trailing byte (Main / Worker / Pool / Other) — drives the filter
        // tree's per-kind subgrouping in trace mode.
        if (evt.threadName && evt.threadName.length > 0) {
          threadInfos.push({
            threadSlot: evt.threadSlot,
            managedThreadId: evt.managedThreadId ?? 0,
            name: evt.threadName,
            timestampUs: evt.timestampUs,
            kind: evt.threadKind,
          });
        }
        break;
      }

      case TraceEventKind.PerTickSnapshot: {
        // At most one snapshot per tick (the scheduler emits exactly one at end-of-tick). If multiple arrive — e.g., if a future phase adds
        // mid-tick snapshots — we keep the last one; the viewer renders one sample per tick regardless.
        if (evt.gauges !== undefined) {
          const values = new Map<GaugeId, number>();
          for (const [key, val] of Object.entries(evt.gauges)) {
            values.set(Number(key) as GaugeId, val);
          }
          gaugeSnapshot = {
            tickNumber: evt.tickNumber,
            timestampUs: evt.timestampUs,
            values,
          };
        }
        break;
      }

      case TraceEventKind.RuntimePhaseUoWCreate:
        // UoW create marker — fires once at the top of OnTickStart. Glyph rendered in the phase track.
        phaseMarkers.push({
          kind: evt.kind,
          label: 'UoW Create',
          timestampUs: evt.timestampUs,
        });
        break;

      case TraceEventKind.RuntimePhaseUoWFlush: {
        // UoW flush marker — fires inside the UoWFlush phase after the WAL becomes durable. changeCount surfaces in the tooltip
        // detail line so an operator can correlate "huge dirty-page count" with a slow UoW.Flush span.
        const cc = evt.changeCount;
        phaseMarkers.push({
          kind: evt.kind,
          label: 'UoW Flush',
          timestampUs: evt.timestampUs,
          detail: cc !== undefined ? `${cc} dirty page${cc === 1 ? '' : 's'}` : undefined,
        });
        break;
      }

      case TraceEventKind.RuntimePhaseSpan: {
        // Real span carrying its own duration — replaces the legacy PhaseStart+PhaseEnd instant pair (which is still handled above for old
        // traces). Pushed to BOTH `phases[]` (for the tick-wide phase track) AND `spans[]` (for the TickDriver thread lane) — these are two
        // different views with different purposes:
        //   - phase track: per-tick lifecycle structure ("where in the tick lifecycle are we")
        //   - thread lane: thread activity ("what is this thread doing right now")
        // The phase IS work executed on the TickDriver thread, so it belongs on its lane too. PageCacheFlush and other child spans then sit
        // beneath it at depth 1 instead of appearing as orphan roots, which is the visual hierarchy the user expects.
        if (evt.phase !== undefined && evt.durationUs !== undefined) {
          const phaseName = PHASE_NAMES[evt.phase] ?? `Phase ${evt.phase}`;
          phases.push({
            phase: evt.phase,
            phaseName,
            startUs: evt.timestampUs,
            endUs: evt.timestampUs + evt.durationUs,
            durationUs: evt.durationUs,
            spanId: evt.spanId,
          });
          // Fall through to the default span-push path so the phase span also lands on the TickDriver lane. We don't replicate the entire
          // default block here; instead we let it run by NOT breaking — but since switch fall-through is forbidden in TS without an explicit
          // marker, we manually invoke the same span-push code path:
          spans.push({
            kind: evt.kind,
            name: phaseName,
            threadSlot: evt.threadSlot,
            startUs: evt.timestampUs,
            endUs: evt.timestampUs + evt.durationUs,
            durationUs: evt.durationUs,
            spanId: evt.spanId,
            parentSpanId: evt.parentSpanId,
            traceIdHi: evt.traceIdHi,
            traceIdLo: evt.traceIdLo,
            rawEvent: evt,
          });
        }
        break;
      }

      case TraceEventKind.SchedulerChunk: {
        // Single-record span: timestampUs is the start, durationUs is the full width. No pairing.
        const sysIdx = evt.systemIndex ?? 0;
        const duration = evt.durationUs ?? 0;
        const sys = systems[sysIdx];
        chunks.push({
          systemIndex: sysIdx,
          systemName: sys?.name ?? `System[${sysIdx}]`,
          chunkIndex: evt.chunkIndex ?? 0,
          threadSlot: evt.threadSlot,
          startUs: evt.timestampUs,
          endUs: evt.timestampUs + duration,
          durationUs: duration,
          entitiesProcessed: evt.entitiesProcessed ?? 0,
          totalChunks: evt.totalChunks ?? 1,
          isParallel: sys?.isParallel ?? false
        });

        const existing = systemDurations.get(sysIdx) ?? 0;
        systemDurations.set(sysIdx, existing + duration);
        break;
      }

      case TraceEventKind.ThreadContextSwitch: {
        // One ON-CPU slice. Collected per target slot — the viewer renders the off-CPU GAPS between slices, never the
        // slices themselves, so this case does NOT fall through to the generic span-push path: a slice is not a lane span.
        // events[] is timestamp-sorted, so each per-slot array stays sorted by startUs.
        if (evt.targetSlotIdx !== undefined && evt.durationUs !== undefined) {
          let arr = contextSwitches.get(evt.targetSlotIdx);
          if (arr === undefined) {
            arr = [];
            contextSwitches.set(evt.targetSlotIdx, arr);
          }
          arr.push({
            startUs: evt.timestampUs,
            durationUs: evt.durationUs,
            waitReason: evt.waitReason ?? 0,
            threadState: evt.threadState ?? 0,
            gettingIdle: evt.gettingIdle ?? false,
            processorNumber: evt.processorNumber ?? 0,
            readyTimeUs: evt.readyTimeUs ?? 0,
          });
        }
        break;
      }

      default: {
        // SchedulerSystemArchetype (kind 245) — per-(system, archetype, tick) entity-touch rollup. Emitted from
        // TyphonRuntime.OnSystemEndInternal on whichever worker thread completed the last chunk; its timestamps cover the
        // full parallel-query bracket (FirstChunkGrabTick → LastChunkDoneTick), so rendering it on the completer's lane
        // misrepresents per-worker activity (a 1.9 ms span "running" on worker-12 that actually describes work distributed
        // across 16 workers). Skip the span-push path; consumers that want the rollup read it from `rawEvents`.
        if ((evt.kind as number) === 245) {
          break;
        }

        // QueryPlan per-tick variant (kind 189 with OwnerSystemIdx present) — emitted by TyphonRuntime.OnSystemEnd around
        // the whole system body for pull-mode/system-input views. Conceptually a system-scope wrapper, not a thread-scope
        // span, so it doesn't belong on the worker lane that happened to complete the last chunk. Hide it from worker
        // lanes; the Execution Inspector and Query Catalog read it directly from `rawEvents` so the data isn't lost.
        //
        // Detection: the decoder only sets `evt.systemIndex` for kind 189 when the OwnerSystemIdx optional-field mask
        // bit (0x20) is present in the payload — which is precisely the per-tick variant. The in-execution QueryPlan
        // emitted by PlanBuilder leaves the mask bit clear → systemIndex undefined → falls through to normal span
        // rendering on whatever thread did the query work. System index 0 is a valid per-tick owner, hence the explicit
        // `!== undefined` check rather than `> 0`.
        if ((evt.kind as number) === 189 && evt.systemIndex !== undefined) {
          break;
        }

        // Every other span kind (≥ 10) is surfaced as a generic SpanData entry — spans already carry duration directly.
        if (evt.kind >= 10 && evt.durationUs !== undefined) {
          const duration = evt.durationUs;

          // GC suspension spans — collect into the dedicated gcSuspensions array so renderGcGroup can draw vertical "stop-the-world"
          // bars on the GC gauge track. Still falls through to the generic span-push below so it ALSO shows up in the slot lane
          // where the GC ingestion thread lives (useful for cross-referencing which thread drove the pause).
          if (evt.kind === TraceEventKind.GcSuspension) {
            gcSuspensions.push({
              tickNumber: evt.tickNumber,
              startUs: evt.timestampUs,
              durationUs: duration,
              threadSlot: evt.threadSlot,
            });
          }

          // Async-completion fold path: if this is one of the three *Completed kinds, try to rewrite an existing kickoff span
          // with the full async duration. If the kickoff hasn't been seen yet (sort ties flipped the order), stash the duration
          // in pendingCompletion so the kickoff can fold it in when it arrives.
          const isCompletion =
            evt.kind === TraceEventKind.PageCacheDiskReadCompleted ||
            evt.kind === TraceEventKind.PageCacheDiskWriteCompleted ||
            evt.kind === TraceEventKind.PageCacheFlushCompleted;

          if (isCompletion && evt.spanId !== undefined) {
            const kickoff = kickoffById.get(evt.spanId);
            if (kickoff !== undefined) {
              // Found the kickoff — rewrite its duration to the full async tail, preserving the original kickoff duration for the viewer.
              kickoff.kickoffDurationUs = kickoff.durationUs;
              kickoff.durationUs = duration;
              kickoff.endUs = kickoff.startUs + duration;
            } else {
              // Kickoff hasn't been processed yet — stash the full event; the kickoff branch (or the end-of-tick orphan flush) folds it in.
              pendingCompletion.set(evt.spanId, evt);
            }
            break;
          }

          // Kickoff / generic span path: push a new SpanData. If a pending completion already exists for this SpanId (the sort
          // put the completion record ahead of the kickoff), fold immediately so only ONE span shows up for the pair.
          const span: SpanData = {
            kind: evt.kind,
            name: SpanKindNames[evt.kind] ?? `Kind[${evt.kind}]`,
            threadSlot: evt.threadSlot,
            startUs: evt.timestampUs,
            endUs: evt.timestampUs + duration,
            durationUs: duration,
            spanId: evt.spanId,
            parentSpanId: evt.parentSpanId,
            traceIdHi: evt.traceIdHi,
            traceIdLo: evt.traceIdLo,
            rawEvent: evt,
          };
          spans.push(span);

          if (evt.spanId !== undefined) {
            kickoffById.set(evt.spanId, span);
            const pending = pendingCompletion.get(evt.spanId);
            if (pending !== undefined && pending.durationUs !== undefined) {
              span.kickoffDurationUs = span.durationUs;
              span.durationUs = pending.durationUs;
              span.endUs = span.startUs + pending.durationUs;
              pendingCompletion.delete(evt.spanId);
            }
          }
        }
        break;
      }
    }
  }

  // Orphan completion flush: any completion records whose kickoff never arrived in this tick (because the kickoff was
  // suppressed, dropped from the ring, or landed in a different tick) are pushed as standalone spans. They'll render under
  // their *Completed display name with the full async duration — the viewer just won't show the kickoff-vs-tail split.
  if (pendingCompletion.size > 0) {
    for (const evt of pendingCompletion.values()) {
      if (evt.durationUs === undefined) continue;
      spans.push({
        kind: evt.kind,
        name: SpanKindNames[evt.kind] ?? `Kind[${evt.kind}]`,
        threadSlot: evt.threadSlot,
        startUs: evt.timestampUs,
        endUs: evt.timestampUs + evt.durationUs,
        durationUs: evt.durationUs,
        spanId: evt.spanId,
        parentSpanId: evt.parentSpanId,
        traceIdHi: evt.traceIdHi,
        traceIdLo: evt.traceIdLo,
      });
    }
  }

  // No TickStart observed for this tick (continuation chunk where the previous chunk already consumed it,
  // or the tickNumber==0 pre-tick bucket carrying records emitted before the first TickStart). Derive the
  // time window from the events themselves so spans land at their real timestamps; without this, ticks
  // with [startUs=0, endUs=0] are filtered out by TimeArea's per-tick viewport overlap check.
  if (startUs === Infinity) {
    let minTs = Infinity;
    let maxTs = -Infinity;
    for (const span of spans) {
      if (span.startUs < minTs) minTs = span.startUs;
      const e = span.endUs ?? span.startUs;
      if (e > maxTs) maxTs = e;
    }
    if (minTs !== Infinity) {
      startUs = minTs;
    } else {
      startUs = 0;
    }
    if (endUs === -Infinity) {
      endUs = maxTs > startUs ? maxTs : startUs;
    }
  }
  if (endUs === -Infinity) endUs = startUs;

  // Sort spans by (startUs, kind, spanId) so the render loop iterates in a canonical order and binary-searching for the first
  // visible span by startUs works correctly. Secondary sort keys (kind, then spanId) break ties deterministically — without them
  // two spans that share a start timestamp (like a kickoff and its folded completion before rewrite) could swap positions on
  // repeat re-processing, which would show up as visual flicker in the render layer.
  spans.sort((a, b) => {
    if (a.startUs !== b.startUs) return a.startUs - b.startUs;
    if (a.kind !== b.kind) return a.kind - b.kind;
    const as = a.spanId ?? '';
    const bs = b.spanId ?? '';
    return as < bs ? -1 : as > bs ? 1 : 0;
  });

  // ── Depth computation ──────────────────────────────────────────────────────
  // Walk parentSpanId links to build each span's parent-chain depth. Row N in the OTel track = depth N, so this is the only
  // place that controls "which row does this span draw on".
  //
  // **Zero detection is critical here.** The server encodes SpanIds as 16-char lowercase hex via `value.ToString("x16")`, so
  // a "no parent" span comes through with `parentSpanId == "0000000000000000"`, NOT empty string and NOT the literal "0".
  // The previous version of this walk only filtered out '' and '0', leaving "0000000000000000" to fall through into a
  // failed map lookup that always landed at depth 0 — so every span was at depth 0 and every bar stacked on row 0. The
  // ZERO_HEX_SPANID constant + explicit equality check is the actual fix for "bars not staying in their depth row".
  const ZERO_HEX_SPANID = '0000000000000000';
  const spanById = new Map<string, SpanData>();
  for (const s of spans) {
    if (s.spanId !== undefined && s.spanId !== ZERO_HEX_SPANID) {
      spanById.set(s.spanId, s);
    }
  }
  for (const s of spans) {
    let depth = 0;
    let parentId = s.parentSpanId;
    const visited = new Set<string>();
    // Loop continues only when parentId is a real, non-zero span ID. Treat undefined / null / empty / 16-zeroes / single "0"
    // all as "no parent" so any future encoding tweak still terminates the walk cleanly.
    while (parentId !== undefined && parentId !== null && parentId !== '' && parentId !== '0' && parentId !== ZERO_HEX_SPANID) {
      if (visited.has(parentId)) break;  // cycle guard — shouldn't happen with monotonic SpanId generation but cheap to check
      visited.add(parentId);
      const parent = spanById.get(parentId);
      if (parent === undefined) break;   // parent not in this tick — truncate here
      depth++;
      if (depth > 32) break;             // sanity cap — anything deeper is almost certainly a runaway walk
      parentId = parent.parentSpanId;
    }
    s.depth = depth;
  }

  // Running-max endUs array — see TickData.spanEndMaxRunning for rationale. Built after the sort so the order lines up with
  // spans[]. Float64Array keeps it compact (~7 MB for a 900K-span tick vs ~25 MB for a boxed number[]).
  const spanEndMaxRunning = new Float64Array(spans.length);
  {
    let runMax = -Infinity;
    for (let i = 0; i < spans.length; i++) {
      const e = spans[i].endUs;
      if (e > runMax) runMax = e;
      spanEndMaxRunning[i] = runMax;
    }
  }

  // Per-slot indexes. Because the global spans[] is already sorted by startUs, filtering by threadSlot preserves that
  // order — no per-slot re-sort is needed. We also walk once to track max depth per slot so the layout pass can size
  // each slot's lane without a second full traversal.
  const spansByThreadSlot = new Map<number, SpanData[]>();
  const spanMaxDepthByThreadSlot = new Map<number, number>();
  for (const s of spans) {
    let arr = spansByThreadSlot.get(s.threadSlot);
    if (arr === undefined) {
      arr = [];
      spansByThreadSlot.set(s.threadSlot, arr);
    }
    arr.push(s);
    const d = s.depth ?? 0;
    const prev = spanMaxDepthByThreadSlot.get(s.threadSlot) ?? -1;
    if (d > prev) spanMaxDepthByThreadSlot.set(s.threadSlot, d);
  }
  const spanEndMaxByThreadSlot = new Map<number, Float64Array>();
  for (const [slot, arr] of spansByThreadSlot) {
    const em = new Float64Array(arr.length);
    let runMax = -Infinity;
    for (let i = 0; i < arr.length; i++) {
      const e = arr[i].endUs;
      if (e > runMax) runMax = e;
      em[i] = runMax;
    }
    spanEndMaxByThreadSlot.set(slot, em);
  }

  // Per-kind projections — one pass over spans[], switch(kind) dispatch. spans[] is already sorted by (startUs, kind, spanId),
  // so every bucket inherits that order and no per-bucket sort is needed. `cacheMisses` is the union of Fetch + AllocatePage +
  // PageEvicted and is populated alongside the per-kind sub-buckets in the same iteration. Running-max Float64Arrays are built
  // after bucketing because their size is only known then.
  const diskReads: SpanData[] = [];
  const diskWrites: SpanData[] = [];
  const cacheMisses: SpanData[] = [];
  const cacheFlushes: SpanData[] = [];
  const cacheFetch: SpanData[] = [];
  const cacheAlloc: SpanData[] = [];
  const cacheEvict: SpanData[] = [];
  const txCommits: SpanData[] = [];
  const txRollbacks: SpanData[] = [];
  const txPersists: SpanData[] = [];
  const walFlushes: SpanData[] = [];
  const walWaits: SpanData[] = [];
  const checkpointCycles: SpanData[] = [];

  for (const s of spans) {
    switch (s.kind) {
      case TraceEventKind.PageCacheDiskRead:
      case TraceEventKind.PageCacheDiskReadCompleted:
        diskReads.push(s);
        break;
      case TraceEventKind.PageCacheDiskWrite:
      case TraceEventKind.PageCacheDiskWriteCompleted:
        diskWrites.push(s);
        break;
      case TraceEventKind.PageCacheFetch:
        cacheMisses.push(s);
        cacheFetch.push(s);
        break;
      case TraceEventKind.PageCacheAllocatePage:
        cacheMisses.push(s);
        cacheAlloc.push(s);
        break;
      case TraceEventKind.PageEvicted:
        cacheMisses.push(s);
        cacheEvict.push(s);
        break;
      case TraceEventKind.PageCacheFlush:
      case TraceEventKind.PageCacheFlushCompleted:
        cacheFlushes.push(s);
        break;
      case TraceEventKind.TransactionCommit:
        txCommits.push(s);
        break;
      case TraceEventKind.TransactionRollback:
        txRollbacks.push(s);
        break;
      case TraceEventKind.TransactionPersist:
        txPersists.push(s);
        break;
      case TraceEventKind.WalFlush:
      case TraceEventKind.WalSegmentRotate:
        walFlushes.push(s);
        break;
      case TraceEventKind.WalWait:
        walWaits.push(s);
        break;
      case TraceEventKind.CheckpointCycle:
        checkpointCycles.push(s);
        break;
    }
  }

  const buildRunMax = (arr: SpanData[]) => {
    const em = new Float64Array(arr.length);
    let rm = -Infinity;
    for (let i = 0; i < arr.length; i++) {
      if (arr[i].endUs > rm) rm = arr[i].endUs;
      em[i] = rm;
    }
    return em;
  };
  const diskReadsEndMax = buildRunMax(diskReads);
  const diskWritesEndMax = buildRunMax(diskWrites);
  const cacheMissesEndMax = buildRunMax(cacheMisses);
  const cacheFlushesEndMax = buildRunMax(cacheFlushes);
  const cacheFetchEndMax = buildRunMax(cacheFetch);
  const cacheAllocEndMax = buildRunMax(cacheAlloc);
  const cacheEvictEndMax = buildRunMax(cacheEvict);
  const txCommitsEndMax = buildRunMax(txCommits);
  const txRollbacksEndMax = buildRunMax(txRollbacks);
  const txPersistsEndMax = buildRunMax(txPersists);
  const walFlushesEndMax = buildRunMax(walFlushes);
  const walWaitsEndMax = buildRunMax(walWaits);
  const checkpointCyclesEndMax = buildRunMax(checkpointCycles);

  return {
    tickNumber,
    startUs,
    endUs,
    durationUs: endUs - startUs,
    chunks,
    phases,
    phaseMarkers,
    skips,
    spans,
    spanEndMaxRunning,
    spansByThreadSlot,
    spanEndMaxByThreadSlot,
    spanMaxDepthByThreadSlot,
    diskReads,
    diskReadsEndMax,
    diskWrites,
    diskWritesEndMax,
    cacheMisses,
    cacheMissesEndMax,
    cacheFlushes,
    cacheFlushesEndMax,
    cacheFetch, cacheFetchEndMax,
    cacheAlloc, cacheAllocEndMax,
    cacheEvict, cacheEvictEndMax,
    txCommits, txCommitsEndMax,
    txRollbacks, txRollbacksEndMax,
    txPersists, txPersistsEndMax,
    walFlushes, walFlushesEndMax,
    walWaits, walWaitsEndMax,
    checkpointCycles, checkpointCyclesEndMax,
    systemDurations,
    gaugeSnapshot,
    memoryAllocEvents,
    gcEvents,
    gcSuspensions,
    threadInfos,
    contextSwitches,
    rawEvents: events,
  };
}

/**
 * Merge two <see cref="TickData"/> entries that share the same <c>tickNumber</c>. Used by
 * <c>assembleTickViewAndNumbers</c> when an intra-tick split (cache v8+) produced multiple chunks covering the same tick —
 * each chunk's per-chunk call to <c>processTickEvents</c> yields a partial <c>TickData</c>, and the consumer needs ONE
 * combined entry with correctly-merged derived state.
 *
 * <b>Strategy:</b> concatenate the <c>rawEvents</c> of both inputs and re-run <c>processTickEvents</c> on the union. This
 * is correct by construction — the merged output is byte-identical to what a single-pass call on the full event stream
 * would produce, including:
 *   <ul>
 *     <li>Async-completion fold across the split (Q4): a kickoff in tick-A + completion in tick-B now see each other
 *         because both are in the combined events array, so the fold path pairs them into one span with the full duration.</li>
 *     <li>Sort stability: the full event sort runs over the union, avoiding any edge case where per-chunk sort orderings
 *         would produce a different final order when naively concatenated.</li>
 *     <li>Depth / running-max / per-kind projections: all derived from the re-sorted merged spans, so no partial-state
 *         desyncs are possible.</li>
 *   </ul>
 *
 * <b>Cost:</b> O(N log N) in the combined event count, where N is the sum of raw events from both inputs. For a split tick
 * of 150K events, that's ~100-300 ms on the main thread — noticeable but bounded, and only fires when a split actually
 * happens (rare by design: intra-tick split caps are 2× the normal per-chunk caps).
 *
 * Throws if the two inputs have different <c>tickNumber</c> values — mergeTickData is strictly for intra-tick merges; a
 * mismatch indicates a caller bug.
 */
export function mergeTickData(a: TickData, b: TickData, systems: SystemDef[]): TickData {
  if (a.tickNumber !== b.tickNumber) {
    throw new Error(`mergeTickData: tickNumber mismatch (a=${a.tickNumber} vs b=${b.tickNumber})`);
  }
  // Concat raw events and re-run. This is the whole implementation — the power of "make the happy path the cold path."
  // Uses a flat concatenation; processTickEvents sorts internally so input order doesn't matter for correctness.
  const combined = a.rawEvents.concat(b.rawEvents);
  // Merged output is the union of a split tick's pieces — conceptually a fully-formed tick with its own TickStart at the
  // head of the combined event stream. Not a continuation. Note: rawEvents on the merged result is intentionally preserved
  // so a subsequent chain fold (mergeTickData(mergeTickData(t1,t2), t3)) has the cumulative raw events available. The caller
  // (assembleTickViewAndNumbers) is responsible for wiping rawEvents on the FINAL merged result once no more folds can fire.
  return processTickEvents(a.tickNumber, combined, systems);
}

/**
 * Walk a sorted-by-tick list of <c>TickData</c> and fold their gauge snapshots + memory alloc events into cross-tick series / caches /
 * flat arrays. Intended single caller: <c>assembleTickViewAndNumbers</c> in <c>chunkCache.ts</c> after LRU reassembly. Computes
 * everything in one pass — no per-gauge filter scans — so cost is O(ticks + total gauge values + total alloc events).
 */
export function aggregateGaugeData(ticks: TickData[]): {
  gaugeSeries: Map<GaugeId, GaugeSeries>;
  gaugeCapacities: Map<GaugeId, number>;
  memoryAllocEvents: MemoryAllocEventData[];
  gcEvents: GcEvent[];
  gcSuspensions: GcSuspensionEvent[];
  /** Slot → thread name map, folded from <see cref="ThreadInfoEvent"/>s across all ticks. First name wins for a given slot. */
  threadNames: Map<number, string>;
  /**
   * Slot → ThreadKind byte (Main=0, Worker=1, Pool=2, Other=3). Sourced from the v4+ trailing byte
   * on ThreadInfo records. Empty for pre-v4 traces — the filter UI then defaults all slots to Other.
   */
  threadKinds: Map<number, number>;
  /** Slot → off-CPU interval store, derived cross-tick from per-tick ON-CPU slices. Empty for traces with no scheduling data. */
  offCpuBySlot: Map<number, OffCpuStore>;
} {
  const gaugeSeries = new Map<GaugeId, GaugeSeries>();
  const gaugeCapacities = new Map<GaugeId, number>();
  const memoryAllocEvents: MemoryAllocEventData[] = [];
  const gcEvents: GcEvent[] = [];
  const gcSuspensions: GcSuspensionEvent[] = [];
  const threadNames = new Map<number, string>();
  const threadKinds = new Map<number, number>();

  for (const tick of ticks) {
    if (tick.gaugeSnapshot !== undefined) {
      for (const [id, value] of tick.gaugeSnapshot.values) {
        // Fixed-at-init gauges (capacities) are only emitted in the first snapshot of a session. Cache them separately; the viewer
        // uses these for reference-line overlays (dashed ceilings on commit buffer / staging pool / page cache / transient store).
        if (FIXED_AT_INIT_GAUGES.has(id) && !gaugeCapacities.has(id)) {
          gaugeCapacities.set(id, value);
          continue;
        }
        let series = gaugeSeries.get(id);
        if (series === undefined) {
          series = { id, samples: [] };
          gaugeSeries.set(id, series);
        }
        series.samples.push({
          tickNumber: tick.gaugeSnapshot.tickNumber,
          timestampUs: tick.gaugeSnapshot.timestampUs,
          value,
        });
      }
    }
  }

  // memoryAllocEvents + gcEvents + gcSuspensions pass — kept in a second loop for readability; the per-tick arrays are tick-sorted
  // already, and concatenation across tick-sorted array preserves total order.
  for (const tick of ticks) {
    if (tick.memoryAllocEvents.length > 0) {
      for (const e of tick.memoryAllocEvents) {
        memoryAllocEvents.push(e);
      }
    }
    if (tick.gcEvents.length > 0) {
      for (const e of tick.gcEvents) {
        gcEvents.push(e);
      }
    }
    if (tick.gcSuspensions.length > 0) {
      for (const s of tick.gcSuspensions) {
        gcSuspensions.push(s);
      }
    }
    // Fold ThreadInfo records into the slot→name map. First observation wins for a slot — re-claims under the same slot are
    // rare (thread death + reclaim) and carry the SAME thread's name until the new claim's ThreadInfo record arrives, at which
    // point we honour the later name too.
    if (tick.threadInfos.length > 0) {
      for (const info of tick.threadInfos) {
        threadNames.set(info.threadSlot, info.name);
        if (info.kind !== undefined) {
          threadKinds.set(info.threadSlot, info.kind);
        }
      }
    }
  }

  // ── Off-CPU interval derivation ──────────────────────────────────────────────────────────────
  // Concatenate each slot's per-tick ON-CPU slices across all resident ticks, then emit the GAP between each adjacent pair
  // as one off-CPU interval. A defensive sort precedes the gap pass: per-tick arrays are individually startUs-sorted, but a
  // slice whose start lies in tick N can be tagged tick N+1 (it closed after the next TickStart), so concatenation alone
  // does not guarantee global order — and both the gap pass and the renderer's binary-search culling require it. A gap
  // straddling a TickStart is just a normal adjacent pair here, so cross-tick gaps are handled for free; leading/trailing
  // open-ended gaps are never emitted because only between-pair gaps are produced. Runs once per cache reassembly.
  const offCpuBySlot = new Map<number, OffCpuStore>();
  {
    const slicesBySlot = new Map<number, OnCpuSlice[]>();
    for (const tick of ticks) {
      if (tick.contextSwitches.size === 0) continue;
      for (const [slot, slices] of tick.contextSwitches) {
        let arr = slicesBySlot.get(slot);
        if (arr === undefined) {
          arr = [];
          slicesBySlot.set(slot, arr);
        }
        for (const s of slices) arr.push(s);
      }
    }
    for (const [slot, slices] of slicesBySlot) {
      if (slices.length < 2) continue;   // need ≥2 slices for a gap to exist between them
      slices.sort((a, b) => a.startUs - b.startUs);
      // Size the typed arrays exactly: count valid (strictly-positive) gaps first, so there is no over-allocation and no
      // per-interval JS object ever exists.
      let gapCount = 0;
      for (let i = 0; i + 1 < slices.length; i++) {
        if (slices[i + 1].startUs > slices[i].startUs + slices[i].durationUs) gapCount++;
      }
      if (gapCount === 0) continue;
      const startUs = new Float64Array(gapCount);
      const endUs = new Float64Array(gapCount);
      const readyTimeUs = new Float64Array(gapCount);
      const category = new Uint8Array(gapCount);
      const waitReason = new Uint8Array(gapCount);
      const processorNumber = new Uint8Array(gapCount);
      let g = 0;
      for (let i = 0; i + 1 < slices.length; i++) {
        const prev = slices[i];
        const next = slices[i + 1];
        const gapStart = prev.startUs + prev.durationUs;
        if (next.startUs <= gapStart) continue;   // zero / negative gap — clock skew or unsorted residue; skip
        startUs[g] = gapStart;
        endUs[g] = next.startUs;
        readyTimeUs[g] = next.readyTimeUs;
        category[g] = waitReasonToCategory(prev.waitReason, prev.gettingIdle);
        waitReason[g] = prev.waitReason & 0xff;
        processorNumber[g] = prev.processorNumber & 0xff;
        g++;
      }
      offCpuBySlot.set(slot, { startUs, endUs, readyTimeUs, category, waitReason, processorNumber });
    }
  }

  return { gaugeSeries, gaugeCapacities, memoryAllocEvents, gcEvents, gcSuspensions, threadNames, threadKinds, offCpuBySlot };
}

/** Maximum ticks to keep in memory during live streaming (~50s at 60Hz). */
const MAX_LIVE_TICKS = 3000;

/** Create an empty ProcessedTrace for live session initialization. */
export function createEmptyTrace(metadata: TraceMetadata): ProcessedTrace {
  return {
    metadata,
    ticks: [],
    tickNumbers: [],
    globalStartUs: 0,
    globalEndUs: 0,
    maxSystemDurationUs: 0,
    maxTickDurationUs: 0,
    p95TickDurationUs: 0,
    systemColors: generateSystemColors(metadata.systems.length),
    gaugeSeries: new Map(),
    gaugeCapacities: new Map(),
    memoryAllocEvents: [],
    gcEvents: [],
    gcSuspensions: [],
  };
}

/**
 * Incrementally process a single tick and append it to an existing ProcessedTrace.
 * Used in live streaming mode to avoid reprocessing the entire trace on every tick.
 */
export function processTickAndAppend(
  trace: ProcessedTrace,
  tickNumber: number,
  events: TraceEvent[]
): ProcessedTrace {
  // processTickAndAppend is the live-session path — each tick arrives with its own TickStart, never a continuation.
  const tickData = processTickEvents(tickNumber, events, trace.metadata.systems);

  const ticks = [...trace.ticks, tickData];
  const tickNumbers = [...trace.tickNumbers, tickNumber];

  if (ticks.length > MAX_LIVE_TICKS) {
    const excess = ticks.length - MAX_LIVE_TICKS;
    ticks.splice(0, excess);
    tickNumbers.splice(0, excess);
  }

  let maxSystemDurationUs = trace.maxSystemDurationUs;
  for (const dur of tickData.systemDurations.values()) {
    if (dur > maxSystemDurationUs) {
      maxSystemDurationUs = dur;
    }
  }

  let p95TickDurationUs = trace.p95TickDurationUs;
  if (ticks.length % 100 === 0 && ticks.length > 0) {
    const sorted = ticks.map(t => t.durationUs).sort((a, b) => a - b);
    const idx = Math.floor(sorted.length * 0.95);
    // Same small-sample guard as the offline processTrace path: fall back to the max when N < 20 so a couple-tick live session
    // doesn't produce a p95 pinned at the MIN (which would clip the tick-timeline's bar-height scaling).
    p95TickDurationUs = sorted.length < 20
      ? sorted[sorted.length - 1]
      : sorted[Math.min(idx, sorted.length - 1)];
  }

  // Rebuild gauge aggregates from the (possibly-trimmed) ticks array. A single linear pass — cheap at MAX_LIVE_TICKS = 3000.
  // Live mode slides a window over the trace, so incrementally patching the existing series would require equally-paced removes on the
  // left side; a full rebuild is simpler, O(loaded ticks), and matches the chunk-path's refresh model.
  const { gaugeSeries, gaugeCapacities, memoryAllocEvents, gcEvents, gcSuspensions, threadNames, offCpuBySlot } = aggregateGaugeData(ticks);

  // Merge thread names into metadata — live mode sees new slot claims arrive as the session runs.
  const mergedMetadata = threadNames.size > 0
    ? { ...trace.metadata, threadNames: { ...(trace.metadata.threadNames ?? {}), ...Object.fromEntries(threadNames) } }
    : trace.metadata;

  return {
    ...trace,
    metadata: mergedMetadata,
    ticks,
    tickNumbers,
    globalStartUs: ticks[0]?.startUs ?? 0,
    globalEndUs: tickData.endUs,
    maxTickDurationUs: Math.max(trace.maxTickDurationUs, tickData.durationUs),
    maxSystemDurationUs,
    p95TickDurationUs,
    gaugeSeries,
    gaugeCapacities,
    memoryAllocEvents,
    gcEvents,
    gcSuspensions,
    offCpuBySlot,
  };
}
