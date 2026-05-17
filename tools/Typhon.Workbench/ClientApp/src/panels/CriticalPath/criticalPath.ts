import type { PostTickSummary } from '@/api/generated/model/postTickSummary';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { SystemTickSummary } from '@/api/generated/model/systemTickSummary';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import type { TrackDto } from '@/api/generated/model/trackDto';
import type { DerivedEdge } from '@/lib/dag/edgeDerivation';
import type { TickRange } from '../SystemDag/useDagViewStore';

/**
 * Critical-path computation — client-side per `09-system-dag.md §5.2 / §9.3` ("Why client-side:
 * zero engine cost when the workbench isn't attached; iterate freely on the algorithm without
 * redeploying").
 *
 * **Model (post phase-overlap rewrite).** The scheduler overlaps phases — a system runs as soon
 * as its dependencies clear, regardless of phase — so the critical path is a **measured
 * longest-path traceback**, not a phase-fenced duration-sum walk:
 *
 * 1. `terminus` = the system that finished last (`argmax endUs`).
 * 2. Trace back: from a system, its **gating predecessor** is the predecessor that finished last
 *    (`argmax endUs`) — by construction the one that defined this system's `readyUs`.
 * 3. Stop at a root (no predecessor that ran this tick).
 *
 * Phases are display metadata only — never consulted here. `SystemDefinition.predecessors` (the
 * engine DAG) is the complete ordering authority; for legacy traces that don't carry it the
 * client-derived `edges` are the fallback.
 *
 * **Track scope (#354).** The runtime partitions systems into `Track → DAG → Phase → System`;
 * tracks run under a hard barrier (every DAG of track *N* completes before track *N+1* begins),
 * and a dependency edge never crosses a DAG. So the path is computed **per track**: the candidate
 * set is the track's ran systems, the terminus is the track-wide `argmax endUs`, and the
 * traceback stays DAG-local on its own. The `'all'` scope concatenates the per-track chains in
 * track order (the barrier makes the concatenation monotonic in time); a single-track scope shows
 * just that track. A topology with no track table falls back to the single global traceback.
 *
 * The CP has exactly one wait class: **worker-claim wait** (`startUs − readyUs`) — the gap
 * between a system becoming eligible and a worker picking it up. Phase-fence wait and
 * chunk-dispatch wait (pre-overlap classes) are gone.
 */

// ── Participation (cross-tick "on critical path X%" stat) ─────────────────

export interface CriticalPathParticipation {
  /** systemName → participation. Systems that never ran in the range are absent from the map. */
  perSystem: Map<string, { onPathTicks: number; rate: number }>;
  /** Total ticks the algorithm examined — useful for "X of Y" displays. */
  totalTicks: number;
}

export interface CriticalPathInputs {
  systems: SystemDefinitionDto[];
  /** Per-tick per-system rows from `metadata.systemTickSummaries`. */
  rows: SystemTickSummary[];
  /** Edges from {@link deriveEdges} — legacy fallback when `system.predecessors` is absent. */
  edges: DerivedEdge[];
  /** Phase names in declared order. Accepted for API compatibility; the measured walk ignores it. */
  phases: string[];
  /** Tick range to consider. `null` means all rows. */
  range: TickRange | null;
  /**
   * Track → DAG hierarchy from `topology.tracks`. When present the walk is track-scoped (per-track
   * traceback concatenated in track order); when absent / empty it falls back to the single global
   * traceback (legacy, track-less traces).
   */
  tracks?: TrackDto[];
  /** `'all'` (default) walks every in-scope track; a track name scopes the walk to that track alone. */
  trackScope?: string;
  /** When `false` (default), `engine`-tagged tracks are excluded from the `'all'` scope. */
  showEngineSystems?: boolean;
}

export function computeCriticalPathParticipation(input: CriticalPathInputs): CriticalPathParticipation {
  const { systems, rows, edges, range, tracks, trackScope = 'all', showEngineSystems = false } = input;

  const indexToName = buildIndexToName(systems);
  const predecessorsByName = buildPredecessorMap(systems, indexToName, edges);
  const hasGraph = predecessorsByName.size > 0;
  const scopeTracks = selectScopeTracks(resolveScopeTracks(tracks), trackScope, showEngineSystems);
  const nameToDagId = buildNameToDagId(systems);

  const byTick = bucketRanByTick(rows, indexToName, range);

  const onPathTicksByName = new Map<string, number>();
  let totalTicks = 0;
  for (const [, ran] of byTick) {
    totalTicks++;
    // Per-track measured traceback (track-less → single global traceback). Without a DAG every
    // ran system is trivially on the path — `tracedCriticalPath` returns the full ordered set.
    const { cpNames } = tracedCriticalPath(ran, scopeTracks, nameToDagId, predecessorsByName, hasGraph);
    for (const name of cpNames) {
      onPathTicksByName.set(name, (onPathTicksByName.get(name) ?? 0) + 1);
    }
  }

  const perSystem = new Map<string, { onPathTicks: number; rate: number }>();
  for (const [name, onPathTicks] of onPathTicksByName) {
    perSystem.set(name, { onPathTicks, rate: totalTicks > 0 ? onPathTicks / totalTicks : 0 });
  }
  return { perSystem, totalTicks };
}

/**
 * Per-system skip rate over a tick range — `(rows with SkipReasonCode > 0) / (total rows for that
 * system)`. Skipped rows still have a SystemTickSummary entry, so the denominator includes them.
 * Returns `null` for systems with zero rows in the range.
 */
export function computeSystemSkipRates(input: {
  systems: SystemDefinitionDto[];
  rows: SystemTickSummary[];
  range: TickRange | null;
}): Map<string, number> {
  const { systems, rows, range } = input;
  const indexToName = buildIndexToName(systems);

  const totals = new Map<string, number>();
  const skipped = new Map<string, number>();
  for (const r of rows) {
    const tick = numberValue((r as { tickNumber?: unknown }).tickNumber);
    if (tick == null) continue;
    if (range && (tick < range.from || tick > range.to)) continue;
    const sysIdx = numberValue((r as { systemIndex?: unknown }).systemIndex);
    if (sysIdx == null) continue;
    const name = indexToName.get(sysIdx);
    if (!name) continue;
    totals.set(name, (totals.get(name) ?? 0) + 1);
    const skip = numberValue((r as { skipReasonCode?: unknown }).skipReasonCode) ?? 0;
    if (skip > 0) {
      skipped.set(name, (skipped.get(name) ?? 0) + 1);
    }
  }

  const out = new Map<string, number>();
  for (const [name, total] of totals) {
    if (total === 0) continue;
    out.set(name, (skipped.get(name) ?? 0) / total);
  }
  return out;
}

// ── Per-tick tape data ────────────────────────────────────────────────────

/**
 * One bar in the timeline. Drawn at its measured `[startUs, endUs)`. `workerClaimWaitUs` is the
 * `startUs − readyUs` gap rendered as a hatched segment before the bar (CP track only).
 */
export interface TickPathBar {
  systemName: string;
  /** Index into `topology.phases` (declared order) — drives the bar colour; -1 for an unphased system. */
  phaseIndex: number;
  durationUs: number;
  /** Tick-relative µs the system's first chunk started. */
  startUs: number;
  /** Tick-relative µs the system's last chunk completed. */
  endUs: number;
  /** Tick-relative µs the system became eligible (all deps done). 0 = unobserved (old traces). */
  readyUs: number;
  /** Σ chunk durations across all workers (chunker v13+); 0 on older caches. */
  totalCpuUs: number;
  /** Worker-claim wait — `max(0, startUs − readyUs)`; 0 when `readyUs` is unobserved. */
  workerClaimWaitUs: number;
  isParallel: boolean;
  workersTouched: number;
  chunksProcessed: number;
}

/** Measured extent of one phase — `[min startUs, max endUs]` over its member systems. Display-only. */
export interface PhaseSpan {
  name: string;
  /** Index into `topology.phases` (for stable colour); -1 for unphased systems. */
  phaseIndex: number;
  startUs: number;
  endUs: number;
}

/**
 * Measured extent of one track — `[min startUs, max endUs]` over its ran systems. Populated only
 * in the `'all'` scope with ≥2 tracks; drives the Critical-Path view's Tracks band. Tracks run
 * under a hard barrier, so spans never overlap — the band needs no interval packing.
 */
export interface TrackSpan {
  name: string;
  /** Position in the in-scope track list — drives a stable colour. */
  index: number;
  startUs: number;
  endUs: number;
}

export interface TickPathPostTick {
  writeTickFenceUs: number;
  walFlushUs: number;
  subscriptionOutputUs: number;
  tierIndexRebuildUs: number;
  dormancySweepUs: number;
  tierBudgetUs: number;
  totalUs: number;
}

/**
 * Path semantics — drives the header label. With a DAG (`system.predecessors` or legacy `edges`)
 * the bars are the real dependency-driven critical path. Without one, every system that ran is on
 * the path, sorted by `startUs` — an execution timeline, not a "longest data-flow path."
 */
export type PathMode = 'critical-path' | 'execution-order';

export interface TickPathBars {
  /** Real tick number; `-1` in aggregate mode (`aggregate` is then set). */
  tickNumber: number;
  mode: PathMode;
  /** Critical-path systems, in forward time order (root → terminus). */
  cpChain: TickPathBar[];
  /** Every other system that ran this tick, sorted by `startUs`. Empty in `execution-order` mode. */
  nonCpBars: TickPathBar[];
  /** Per-phase measured spans, sorted by `startUs` — drives the multi-lane phase band. */
  phaseSpans: PhaseSpan[];
  /**
   * Per-track measured spans, in track order — drives the Tracks band. Non-empty only in the
   * `'all'` scope with ≥2 visible tracks; empty for a single-track scope, a track-less topology,
   * and aggregate mode.
   */
  trackSpans: TrackSpan[];
  /** Tick body `[0, maxEndUs]`. Post-tick extends past `endUs`; metronome before `startUs`. */
  timeBounds: { startUs: number; endUs: number };
  postTick: TickPathPostTick;
  /** Metronome wait that PRECEDED this tick (µs). */
  metronomeWaitUs: number;
  metronomeIntentClass: MetronomeIntentClass | null;
  /** Tick wall-clock = `timeBounds.endUs + postTick.totalUs` (excludes the metronome lead). */
  totalUs: number;
  /**
   * Aggregate flag — set when the bars carry mean values across a tick range. In aggregate mode
   * `cpChain` carries per-system means with synthetic cumulative `startUs/endUs`, and `nonCpBars`
   * / `phaseSpans` are empty (no shared timeline across ticks). `null` for a single tick.
   */
  aggregate: { tickCount: number; range: { from: number; to: number } } | null;
}

export type MetronomeIntentClass = 'CatchUp' | 'Throttled' | 'Headroom';

/**
 * Decode the wire `intentClass` byte. Out-of-range or missing values return `null` — the renderer
 * suppresses the chip rather than guessing.
 */
export function decodeMetronomeIntentClass(raw: unknown): MetronomeIntentClass | null {
  const n = typeof raw === 'number' ? raw : Number(raw as string);
  if (!Number.isFinite(n)) return null;
  switch (n) {
    case 0: return 'CatchUp';
    case 1: return 'Throttled';
    case 2: return 'Headroom';
    default: return null;
  }
}

/**
 * Pick the "dominant" tick in a window — the one with the longest wall-clock duration. Used as the
 * default focus tick for the CP timeline.
 *
 * Two-tier semantics:
 * 1. **Strict**: at least one tick falls within `[range.from, range.to]`. Returns the longest one.
 * 2. **Midpoint fallback** (only when `time` is provided): no tick number is in the strict window
 *    — the user has zoomed inside one tick. Returns the tick whose body contains the µs midpoint.
 *
 * Returns `null` only when both tiers fail.
 */
export function dominantTickInRange(
  tickSummaries: TickSummaryDto[] | null | undefined,
  range: TickRange | null,
  time?: { startUs: number; endUs: number } | null,
): number | null {
  if (tickSummaries && tickSummaries.length > 0 && range) {
    let bestTick: number | null = null;
    let bestDuration = -1;
    for (const t of tickSummaries) {
      const tn = numberValue((t as { tickNumber?: unknown }).tickNumber);
      if (tn == null || tn < range.from || tn > range.to) continue;
      const dur = numberValue((t as { durationUs?: unknown }).durationUs) ?? 0;
      if (dur > bestDuration) {
        bestDuration = dur;
        bestTick = tn;
      }
    }
    if (bestTick != null) return bestTick;
  }

  if (!time || !tickSummaries || tickSummaries.length === 0) return null;
  if (!Number.isFinite(time.startUs) || !Number.isFinite(time.endUs) || time.endUs <= time.startUs) return null;
  const mid = (time.startUs + time.endUs) / 2;
  for (const t of tickSummaries) {
    const start = numberValue((t as { startUs?: unknown }).startUs);
    if (start == null) continue;
    const dur = numberValue((t as { durationUs?: unknown }).durationUs) ?? 0;
    if (dur <= 0) continue;
    if (start <= mid && mid < start + dur) {
      return numberValue((t as { tickNumber?: unknown }).tickNumber);
    }
  }
  return null;
}

/**
 * Compute the time-accurate timeline data for ONE tick — per `09-system-dag.md §5`. Bars carry
 * measured `[startUs, endUs)`; the renderer places them on a wall-clock axis and interval-packs
 * the non-CP set into tracks.
 *
 * Returns `null` when no rows match `tickNumber`.
 */
export function computeCriticalPathForTick(input: {
  tickNumber: number;
  systems: SystemDefinitionDto[];
  rows: SystemTickSummary[];
  edges: DerivedEdge[];
  phases: string[];
  postTickRows: PostTickSummary[];
  tickSummaryRow: TickSummaryDto | null;
  /** Track → DAG hierarchy; when present the walk is track-scoped (see {@link CriticalPathInputs}). */
  tracks?: TrackDto[];
  /** `'all'` (default) or a single track name. */
  trackScope?: string;
  /** When `false` (default), `engine`-tagged tracks drop out of the `'all'` scope. */
  showEngineSystems?: boolean;
}): TickPathBars | null {
  const {
    tickNumber, systems, rows, edges, phases, postTickRows, tickSummaryRow,
    tracks, trackScope = 'all', showEngineSystems = false,
  } = input;

  const indexToName = buildIndexToName(systems);
  const nameToPhase = buildNameToPhase(systems);
  const nameToDef = new Map<string, SystemDefinitionDto>();
  for (const s of systems) {
    if (s.name) nameToDef.set(s.name, s);
  }
  const phaseOrder = new Map<string, number>();
  phases.forEach((p, i) => phaseOrder.set(p, i));
  const phaseIndexOf = (name: string): number => {
    const ph = nameToPhase.get(name) ?? '';
    return ph ? phaseOrder.get(ph) ?? -1 : -1;
  };
  const predecessorsByName = buildPredecessorMap(systems, indexToName, edges);

  const ran = indexRanSystems(rows, tickNumber, indexToName);
  if (ran.size === 0) return null;

  const hasGraph = predecessorsByName.size > 0;
  const scopeTracks = selectScopeTracks(resolveScopeTracks(tracks), trackScope, showEngineSystems);
  const nameToDagId = buildNameToDagId(systems);
  // Per-track measured traceback, concatenated in track order. `scopedRan` is `ran` narrowed to
  // the in-scope tracks — it is the universe for the non-CP bars, phase spans and time bounds.
  const { cpNames, scopedRan } = tracedCriticalPath(ran, scopeTracks, nameToDagId, predecessorsByName, hasGraph);
  if (scopedRan.size === 0) return null;
  const cpSet = new Set(cpNames);

  const cpChain = cpNames.map((n) => makeBar(scopedRan.get(n)!, nameToDef.get(n), phaseIndexOf(n)));
  const nonCpBars: TickPathBar[] = [];
  for (const [name, s] of scopedRan) {
    if (cpSet.has(name)) continue;
    nonCpBars.push(makeBar(s, nameToDef.get(name), phaseIndexOf(name)));
  }
  nonCpBars.sort((a, b) => (a.startUs - b.startUs) || a.systemName.localeCompare(b.systemName));

  const phaseSpans = buildPhaseSpans(scopedRan, nameToPhase, phases);
  // Tracks band: only meaningful when the whole tick is in view across ≥2 tracks.
  const trackSpans = (trackScope === 'all' && scopeTracks.length >= 2)
    ? buildTrackSpans(scopeTracks, scopedRan, nameToDagId)
    : [];

  let maxEnd = 0;
  for (const s of scopedRan.values()) {
    if (s.endUs > maxEnd) maxEnd = s.endUs;
  }
  const timeBounds = { startUs: 0, endUs: maxEnd };

  // Post-tick serial work belongs to the whole tick — shown only when the whole tick is in view
  // (the `'all'` scope with engine tracks visible, or a track-less trace). A scoped view (single
  // track, or app-tracks-only) drops it so the displayed total equals the sum of what is shown.
  const wholeTick = scopeTracks.length === 0 || (trackScope === 'all' && showEngineSystems);
  const postTick = wholeTick ? buildPostTick(postTickRows, tickNumber) : EMPTY_POST_TICK;
  const metronomeWaitUs = tickSummaryRow
    ? numberValue((tickSummaryRow as { metronomeWaitUs?: unknown }).metronomeWaitUs) ?? 0
    : 0;
  const metronomeIntentClass = tickSummaryRow
    ? decodeMetronomeIntentClass((tickSummaryRow as { metronomeIntentClass?: unknown }).metronomeIntentClass)
    : null;

  return {
    tickNumber,
    mode: hasGraph ? 'critical-path' : 'execution-order',
    cpChain,
    nonCpBars,
    phaseSpans,
    trackSpans,
    timeBounds,
    postTick,
    metronomeWaitUs,
    metronomeIntentClass,
    totalUs: timeBounds.endUs + postTick.totalUs,
    aggregate: null,
  };
}

/**
 * Worker-occupancy step function for the ribbon (`09-system-dag.md §5.5`). At each instant,
 * `occupancy = Σ (totalCpuUs / durationUs)` over live systems — each contributes its average
 * concurrent worker count — clamped to `[0, workerCount]`.
 *
 * Returns `null` when no system carries `totalCpuUs` (pre-v13 caches) — the ribbon stays hidden
 * rather than rendering a misleading flat zero.
 */
export interface WorkerOccupancy {
  /** Sorted distinct breakpoints (µs). `levels` has one fewer entry — the value on each interval. */
  breakpoints: number[];
  /** `levels[i]` = worker occupancy on `[breakpoints[i], breakpoints[i + 1])`. */
  levels: number[];
  /** Ceiling for the chart's vertical scale. */
  workerCount: number;
}

export function computeWorkerOccupancy(bars: TickPathBars, workerCount: number): WorkerOccupancy | null {
  if (!Number.isFinite(workerCount) || workerCount < 1) return null;
  const all = [...bars.cpChain, ...bars.nonCpBars];
  let anyCpu = false;
  const events: Array<{ t: number; delta: number }> = [];
  for (const b of all) {
    if (b.durationUs <= 0 || b.totalCpuUs <= 0) continue;
    anyCpu = true;
    const concurrency = b.totalCpuUs / b.durationUs;
    events.push({ t: b.startUs, delta: concurrency });
    events.push({ t: b.endUs, delta: -concurrency });
  }
  if (!anyCpu) return null;
  events.sort((a, b) => a.t - b.t);

  // Coalesce events at the same timestamp, then sweep.
  const breakpoints: number[] = [];
  const levels: number[] = [];
  let running = 0;
  let i = 0;
  while (i < events.length) {
    const t = events[i].t;
    let delta = 0;
    while (i < events.length && events[i].t === t) {
      delta += events[i].delta;
      i += 1;
    }
    if (breakpoints.length > 0) {
      levels.push(Math.max(0, Math.min(workerCount, running)));
    }
    breakpoints.push(t);
    running += delta;
  }
  return { breakpoints, levels, workerCount };
}

/**
 * Aggregate critical-path bars across a tick range. There is no shared timeline across ticks, so
 * the result is a duration-bar summary, not a time-accurate Gantt: `cpChain` carries each system's
 * mean on-CP duration with synthetic cumulative `startUs/endUs` (so the renderer's time-axis
 * placement still works as a stacked bar), and `nonCpBars` / `phaseSpans` are empty.
 *
 * `tickNumber` is `-1`; `aggregate` carries the participation count and range. Returns `null` when
 * no ticks fell in range.
 */
export function computeAggregateCriticalPath(input: {
  systems: SystemDefinitionDto[];
  rows: SystemTickSummary[];
  edges: DerivedEdge[];
  phases: string[];
  postTickRows: PostTickSummary[];
  tickSummaries: TickSummaryDto[];
  range: TickRange | null;
  tracks?: TrackDto[];
  trackScope?: string;
  showEngineSystems?: boolean;
}): TickPathBars | null {
  const {
    systems, rows, edges, phases, postTickRows, tickSummaries, range,
    tracks, trackScope = 'all', showEngineSystems = false,
  } = input;
  if (!range) return null;

  const ticksSeen = new Set<number>();
  for (const r of rows) {
    const tn = numberValue((r as { tickNumber?: unknown }).tickNumber);
    if (tn == null || tn < range.from || tn > range.to) continue;
    ticksSeen.add(tn);
  }
  if (ticksSeen.size === 0) return null;

  const nameToDef = new Map<string, SystemDefinitionDto>();
  for (const s of systems) {
    if (s.name) nameToDef.set(s.name, s);
  }
  const phaseOrder = new Map<string, number>();
  phases.forEach((p, i) => phaseOrder.set(p, i));

  const sumByName = new Map<string, number>();
  const countByName = new Map<string, number>();
  let mode: PathMode = 'execution-order';
  for (const tickNumber of ticksSeen) {
    const tickSummaryRow = tickSummaries.find((t) => Number((t as { tickNumber?: unknown }).tickNumber) === tickNumber) ?? null;
    const bars = computeCriticalPathForTick({
      tickNumber, systems, rows, edges, phases, postTickRows, tickSummaryRow,
      tracks, trackScope, showEngineSystems,
    });
    if (!bars) continue;
    if (bars.mode === 'critical-path') mode = 'critical-path';
    for (const bar of bars.cpChain) {
      sumByName.set(bar.systemName, (sumByName.get(bar.systemName) ?? 0) + bar.durationUs);
      countByName.set(bar.systemName, (countByName.get(bar.systemName) ?? 0) + 1);
    }
  }
  if (sumByName.size === 0) return null;

  const means: Array<{ name: string; meanUs: number }> = [];
  for (const [name, sum] of sumByName) {
    means.push({ name, meanUs: sum / (countByName.get(name) ?? 1) });
  }
  means.sort((a, b) => b.meanUs - a.meanUs || a.name.localeCompare(b.name));

  const cpChain: TickPathBar[] = [];
  let cursor = 0;
  for (const m of means) {
    const ph = nameToDef.get(m.name)?.phaseName ?? '';
    cpChain.push({
      systemName: m.name,
      phaseIndex: ph ? phaseOrder.get(ph) ?? -1 : -1,
      durationUs: m.meanUs,
      startUs: cursor,
      endUs: cursor + m.meanUs,
      readyUs: 0,
      totalCpuUs: 0,
      workerClaimWaitUs: 0,
      isParallel: nameToDef.get(m.name)?.isParallel ?? false,
      workersTouched: 0,
      chunksProcessed: 0,
    });
    cursor += m.meanUs;
  }

  // Post-tick serial work — included only when the whole tick is in view (same rule as the
  // single-tick path); a scoped aggregate drops it so the total equals the sum of what's shown.
  const scopeTracks = selectScopeTracks(resolveScopeTracks(tracks), trackScope, showEngineSystems);
  const wholeTick = scopeTracks.length === 0 || (trackScope === 'all' && showEngineSystems);
  const postTick = wholeTick ? buildAggregatePostTick(postTickRows, range) : EMPTY_POST_TICK;
  const { metronomeWaitUs, metronomeIntentClass } = buildAggregateMetronome(tickSummaries, range);

  return {
    tickNumber: -1,
    mode,
    cpChain,
    nonCpBars: [],
    phaseSpans: [],
    trackSpans: [],
    timeBounds: { startUs: 0, endUs: cursor },
    postTick,
    metronomeWaitUs,
    metronomeIntentClass,
    totalUs: cursor + postTick.totalUs,
    aggregate: { tickCount: ticksSeen.size, range: { from: range.from, to: range.to } },
  };
}

// ── Internals ─────────────────────────────────────────────────────────────

/** Per-tick per-system measured record — the working unit of the walk. */
interface RanSystem {
  name: string;
  startUs: number;
  endUs: number;
  readyUs: number;
  durationUs: number;
  totalCpuUs: number;
  workersTouched: number;
  chunksProcessed: number;
}

function buildIndexToName(systems: SystemDefinitionDto[]): Map<number, string> {
  const map = new Map<number, string>();
  for (const s of systems) {
    if (!s.name) continue;
    const idx = numberValue((s as { index?: unknown }).index);
    if (idx != null) map.set(idx, s.name);
  }
  return map;
}

function buildNameToPhase(systems: SystemDefinitionDto[]): Map<string, string> {
  const map = new Map<string, string>();
  for (const s of systems) {
    if (s.name) map.set(s.name, s.phaseName ?? '');
  }
  return map;
}

/**
 * Predecessor map: targetSystemName → [sourceSystemNames]. Read from `system.predecessors` (the
 * engine DAG — the complete ordering authority, **including cross-phase edges**; the scheduler
 * overlaps phases so there is no phase-fence rule to substitute). Falls back to client-derived
 * `edges` for legacy traces that don't surface `predecessors`.
 */
function buildPredecessorMap(
  systems: SystemDefinitionDto[],
  indexToName: Map<number, string>,
  edges: DerivedEdge[],
): Map<string, string[]> {
  const preds = new Map<string, string[]>();
  for (const s of systems) {
    if (!s.name) continue;
    const predIdxs = (s as { predecessors?: unknown }).predecessors;
    if (!Array.isArray(predIdxs)) continue;
    const list: string[] = [];
    for (const raw of predIdxs) {
      const idx = numberValue(raw);
      if (idx == null) continue;
      const predName = indexToName.get(idx);
      if (predName && predName !== s.name) list.push(predName);
    }
    if (list.length > 0) preds.set(s.name, list);
  }
  if (preds.size === 0) {
    for (const e of edges) {
      if (e.source === e.target) continue;
      let list = preds.get(e.target);
      if (!list) {
        list = [];
        preds.set(e.target, list);
      }
      if (!list.includes(e.source)) list.push(e.source);
    }
  }
  return preds;
}

/** Index this tick's non-skipped, non-empty rows by system name. */
function indexRanSystems(
  rows: SystemTickSummary[],
  tickNumber: number,
  indexToName: Map<number, string>,
): Map<string, RanSystem> {
  const ran = new Map<string, RanSystem>();
  for (const r of rows) {
    const tn = numberValue((r as { tickNumber?: unknown }).tickNumber);
    if (tn !== tickNumber) continue;
    const lite = toRanSystem(r, indexToName);
    if (lite) ran.set(lite.name, lite);
  }
  return ran;
}

/** One pass: bucket non-skipped rows by tick, filtered to `range`. Used by participation. */
function bucketRanByTick(
  rows: SystemTickSummary[],
  indexToName: Map<number, string>,
  range: TickRange | null,
): Map<number, Map<string, RanSystem>> {
  const byTick = new Map<number, Map<string, RanSystem>>();
  for (const r of rows) {
    const tn = numberValue((r as { tickNumber?: unknown }).tickNumber);
    if (tn == null) continue;
    if (range && (tn < range.from || tn > range.to)) continue;
    const lite = toRanSystem(r, indexToName);
    if (!lite) continue;
    let bucket = byTick.get(tn);
    if (!bucket) {
      bucket = new Map<string, RanSystem>();
      byTick.set(tn, bucket);
    }
    bucket.set(lite.name, lite);
  }
  return byTick;
}

/** Project a wire row into a {@link RanSystem}, or `null` if skipped / empty / unknown system. */
function toRanSystem(r: SystemTickSummary, indexToName: Map<number, string>): RanSystem | null {
  const skip = numberValue((r as { skipReasonCode?: unknown }).skipReasonCode) ?? 0;
  if (skip > 0) return null;
  const durationUs = numberValue((r as { durationUs?: unknown }).durationUs) ?? 0;
  if (durationUs <= 0) return null;
  const sysIdx = numberValue((r as { systemIndex?: unknown }).systemIndex);
  if (sysIdx == null) return null;
  const name = indexToName.get(sysIdx);
  if (!name) return null;
  return {
    name,
    startUs: numberValue((r as { startUs?: unknown }).startUs) ?? 0,
    endUs: numberValue((r as { endUs?: unknown }).endUs) ?? 0,
    readyUs: numberValue((r as { readyUs?: unknown }).readyUs) ?? 0,
    durationUs,
    totalCpuUs: numberValue((r as { totalCpuUs?: unknown }).totalCpuUs) ?? 0,
    workersTouched: numberValue((r as { workersTouched?: unknown }).workersTouched) ?? 0,
    chunksProcessed: numberValue((r as { chunksProcessed?: unknown }).chunksProcessed) ?? 0,
  };
}

/**
 * Measured longest-path traceback. `terminus` = last system to finish; each step back picks the
 * predecessor that finished last (the one that defined the cursor's `readyUs`). Returns the path
 * in forward time order (root → terminus). The `seen` set guards against a malformed cyclic DAG.
 */
function traceCriticalPath(ran: Map<string, RanSystem>, preds: Map<string, string[]>): string[] {
  if (ran.size === 0) return [];
  let terminus: string | null = null;
  let maxEnd = -Infinity;
  for (const [name, s] of ran) {
    if (s.endUs > maxEnd) {
      maxEnd = s.endUs;
      terminus = name;
    }
  }
  if (terminus == null) return [];

  const reverse: string[] = [terminus];
  const seen = new Set<string>([terminus]);
  let cursor = terminus;
  let safety = ran.size + 2;
  while (safety-- > 0) {
    const predNames = preds.get(cursor);
    if (!predNames || predNames.length === 0) break;
    let gating: string | null = null;
    let gatingEnd = -Infinity;
    for (const p of predNames) {
      if (seen.has(p)) continue;
      const ps = ran.get(p);
      if (!ps) continue; // predecessor skipped / didn't run this tick
      if (ps.endUs > gatingEnd) {
        gatingEnd = ps.endUs;
        gating = p;
      }
    }
    if (gating == null) break; // cursor is a path root
    reverse.push(gating);
    seen.add(gating);
    cursor = gating;
  }
  return reverse.reverse();
}

/** No-DAG fallback: every system that ran, sorted by `startUs` (ties broken by name). */
function fallbackOrder(ran: Map<string, RanSystem>): string[] {
  return [...ran.values()]
    .sort((a, b) => (a.startUs - b.startUs) || a.name.localeCompare(b.name))
    .map((s) => s.name);
}

// ── Track scoping (#354) ──────────────────────────────────────────────────

/** Track tag marking engine-internal tracks — excluded from the `'all'` scope unless revealed. */
const ENGINE_TAG = 'engine';

/** Empty post-tick block — used by scoped views, which omit the whole-tick serial tail. */
const EMPTY_POST_TICK: TickPathPostTick = {
  writeTickFenceUs: 0,
  walFlushUs: 0,
  subscriptionOutputUs: 0,
  tierIndexRebuildUs: 0,
  dormancySweepUs: 0,
  tierBudgetUs: 0,
  totalUs: 0,
};

/** One track resolved for scoping — its DAG-id set plus the `engine` tag, in execution order. */
interface ScopeTrack {
  name: string;
  orderIndex: number;
  isEngine: boolean;
  dagIds: Set<number>;
}

/**
 * Project `topology.tracks` into ordered scope buckets. Returns an empty array for a track-less
 * topology (legacy traces) — the caller then falls back to the single global traceback.
 */
function resolveScopeTracks(tracks: TrackDto[] | undefined): ScopeTrack[] {
  if (!tracks || tracks.length === 0) return [];
  const out: ScopeTrack[] = [];
  for (const t of tracks) {
    const dagIds = new Set<number>();
    for (const d of t.dags ?? []) {
      const id = numberValue((d as { id?: unknown }).id);
      if (id != null) dagIds.add(id);
    }
    out.push({
      name: t.name ?? '',
      orderIndex: numberValue((t as { orderIndex?: unknown }).orderIndex) ?? 0,
      isEngine: (t.tags ?? []).includes(ENGINE_TAG),
      dagIds,
    });
  }
  out.sort((a, b) => a.orderIndex - b.orderIndex);
  return out;
}

/**
 * The tracks in scope for the current selection, in execution order. `'all'` keeps every track
 * (minus `engine`-tagged ones unless `showEngineSystems`); a track name keeps just that track. An
 * unknown name — e.g. a `trackScope` persisted from another trace — degrades to the `'all'` set.
 */
function selectScopeTracks(resolved: ScopeTrack[], trackScope: string, showEngineSystems: boolean): ScopeTrack[] {
  if (resolved.length === 0) return [];
  if (trackScope !== 'all') {
    const named = resolved.filter((t) => t.name === trackScope);
    if (named.length > 0) return named;
    // Unknown track — fall through to the 'all' set.
  }
  return showEngineSystems ? resolved : resolved.filter((t) => !t.isEngine);
}

/** systemName → owning DAG id, from `SystemDefinitionDto.dagId`. */
function buildNameToDagId(systems: SystemDefinitionDto[]): Map<string, number> {
  const map = new Map<string, number>();
  for (const s of systems) {
    if (!s.name) continue;
    const dagId = numberValue((s as { dagId?: unknown }).dagId);
    if (dagId != null) map.set(s.name, dagId);
  }
  return map;
}

/**
 * Track-aware critical-path names for one tick's `ran` set. With a real Track hierarchy the path
 * is the per-track measured traceback ({@link traceCriticalPath} over the track's ran systems —
 * terminus = track-wide `argmax endUs`; the traceback stays DAG-local on its own since
 * `predecessors` never crosses a DAG) concatenated in track order. A track-less topology
 * (`scopeTracks` empty) falls back to the single global traceback.
 *
 * Returns the ordered CP names plus `scopedRan` — `ran` narrowed to the in-scope tracks, which is
 * the universe the caller uses for non-CP bars / spans / bounds.
 */
function tracedCriticalPath(
  ran: Map<string, RanSystem>,
  scopeTracks: ScopeTrack[],
  nameToDagId: Map<string, number>,
  preds: Map<string, string[]>,
  hasGraph: boolean,
): { cpNames: string[]; scopedRan: Map<string, RanSystem> } {
  // Track-less: today's behaviour over the whole ran set.
  if (scopeTracks.length === 0) {
    return { cpNames: hasGraph ? traceCriticalPath(ran, preds) : fallbackOrder(ran), scopedRan: ran };
  }

  const cpNames: string[] = [];
  const scopedRan = new Map<string, RanSystem>();
  for (const track of scopeTracks) {
    const ranT = new Map<string, RanSystem>();
    for (const [name, rs] of ran) {
      const dagId = nameToDagId.get(name);
      if (dagId != null && track.dagIds.has(dagId)) {
        ranT.set(name, rs);
        scopedRan.set(name, rs);
      }
    }
    if (ranT.size === 0) continue;
    const chainT = hasGraph ? traceCriticalPath(ranT, preds) : fallbackOrder(ranT);
    for (const n of chainT) cpNames.push(n);
  }
  return { cpNames, scopedRan };
}

/** Per-track measured `[min startUs, max endUs]` over `scopedRan`. Tracks with no ran system are dropped. */
function buildTrackSpans(
  scopeTracks: ScopeTrack[],
  scopedRan: Map<string, RanSystem>,
  nameToDagId: Map<string, number>,
): TrackSpan[] {
  const spans: TrackSpan[] = [];
  for (let i = 0; i < scopeTracks.length; i++) {
    const track = scopeTracks[i];
    let start = Infinity;
    let end = -Infinity;
    for (const [name, rs] of scopedRan) {
      const dagId = nameToDagId.get(name);
      if (dagId == null || !track.dagIds.has(dagId)) continue;
      if (rs.startUs < start) start = rs.startUs;
      if (rs.endUs > end) end = rs.endUs;
    }
    if (start !== Infinity) {
      spans.push({ name: track.name, index: i, startUs: start, endUs: end });
    }
  }
  return spans;
}

function makeBar(s: RanSystem, def: SystemDefinitionDto | undefined, phaseIndex: number): TickPathBar {
  // readyUs == 0 in older traces (#311 started capturing it). Treat zero as "unknown" → no claim
  // wait rather than a real "ready at tick start".
  const workerClaimWaitUs = s.readyUs > 0 ? Math.max(0, s.startUs - s.readyUs) : 0;
  return {
    systemName: s.name,
    phaseIndex,
    durationUs: s.durationUs,
    startUs: s.startUs,
    endUs: s.endUs,
    readyUs: s.readyUs,
    totalCpuUs: s.totalCpuUs,
    workerClaimWaitUs,
    isParallel: def?.isParallel ?? false,
    workersTouched: s.workersTouched,
    chunksProcessed: s.chunksProcessed,
  };
}

/** Per-phase measured spans `[min startUs, max endUs]`, sorted by `startUs`. */
function buildPhaseSpans(
  ran: Map<string, RanSystem>,
  nameToPhase: Map<string, string>,
  phases: string[],
): PhaseSpan[] {
  const phaseIndex = new Map<string, number>();
  phases.forEach((p, i) => phaseIndex.set(p, i));

  const acc = new Map<string, { start: number; end: number }>();
  for (const [name, s] of ran) {
    const phase = nameToPhase.get(name) ?? '';
    const cur = acc.get(phase);
    if (!cur) {
      acc.set(phase, { start: s.startUs, end: s.endUs });
    } else {
      cur.start = Math.min(cur.start, s.startUs);
      cur.end = Math.max(cur.end, s.endUs);
    }
  }

  const spans: PhaseSpan[] = [];
  for (const [phase, ext] of acc) {
    spans.push({
      name: phase || '(unphased)',
      phaseIndex: phase && phaseIndex.has(phase) ? phaseIndex.get(phase)! : -1,
      startUs: ext.start,
      endUs: ext.end,
    });
  }
  spans.sort((a, b) => (a.startUs - b.startUs) || (a.phaseIndex - b.phaseIndex));
  return spans;
}

function buildPostTick(postTickRows: PostTickSummary[], tickNumber: number): TickPathPostTick {
  const row = postTickRows.find((r) => numberValue((r as { tickNumber?: unknown }).tickNumber) === tickNumber);
  const pt: TickPathPostTick = {
    writeTickFenceUs: row ? numberValue((row as { writeTickFenceUs?: unknown }).writeTickFenceUs) ?? 0 : 0,
    walFlushUs: row ? numberValue((row as { walFlushUs?: unknown }).walFlushUs) ?? 0 : 0,
    subscriptionOutputUs: row ? numberValue((row as { subscriptionOutputUs?: unknown }).subscriptionOutputUs) ?? 0 : 0,
    tierIndexRebuildUs: row ? numberValue((row as { tierIndexRebuildUs?: unknown }).tierIndexRebuildUs) ?? 0 : 0,
    dormancySweepUs: row ? numberValue((row as { dormancySweepUs?: unknown }).dormancySweepUs) ?? 0 : 0,
    tierBudgetUs: row ? numberValue((row as { tierBudgetUs?: unknown }).tierBudgetUs) ?? 0 : 0,
    totalUs: 0,
  };
  pt.totalUs = pt.writeTickFenceUs + pt.walFlushUs + pt.subscriptionOutputUs
    + pt.tierIndexRebuildUs + pt.dormancySweepUs + pt.tierBudgetUs;
  return pt;
}

/** Per-key mean post-tick across in-range ticks. Divisor is the count of in-range post-tick rows. */
function buildAggregatePostTick(postTickRows: PostTickSummary[], range: TickRange): TickPathPostTick {
  const sums = { writeTickFenceUs: 0, walFlushUs: 0, subscriptionOutputUs: 0, tierIndexRebuildUs: 0, dormancySweepUs: 0, tierBudgetUs: 0 };
  let count = 0;
  for (const r of postTickRows) {
    const tn = numberValue((r as { tickNumber?: unknown }).tickNumber);
    if (tn == null || tn < range.from || tn > range.to) continue;
    count += 1;
    sums.writeTickFenceUs += numberValue((r as { writeTickFenceUs?: unknown }).writeTickFenceUs) ?? 0;
    sums.walFlushUs += numberValue((r as { walFlushUs?: unknown }).walFlushUs) ?? 0;
    sums.subscriptionOutputUs += numberValue((r as { subscriptionOutputUs?: unknown }).subscriptionOutputUs) ?? 0;
    sums.tierIndexRebuildUs += numberValue((r as { tierIndexRebuildUs?: unknown }).tierIndexRebuildUs) ?? 0;
    sums.dormancySweepUs += numberValue((r as { dormancySweepUs?: unknown }).dormancySweepUs) ?? 0;
    sums.tierBudgetUs += numberValue((r as { tierBudgetUs?: unknown }).tierBudgetUs) ?? 0;
  }
  const div = Math.max(1, count);
  const pt: TickPathPostTick = {
    writeTickFenceUs: sums.writeTickFenceUs / div,
    walFlushUs: sums.walFlushUs / div,
    subscriptionOutputUs: sums.subscriptionOutputUs / div,
    tierIndexRebuildUs: sums.tierIndexRebuildUs / div,
    dormancySweepUs: sums.dormancySweepUs / div,
    tierBudgetUs: sums.tierBudgetUs / div,
    totalUs: 0,
  };
  pt.totalUs = pt.writeTickFenceUs + pt.walFlushUs + pt.subscriptionOutputUs
    + pt.tierIndexRebuildUs + pt.dormancySweepUs + pt.tierBudgetUs;
  return pt;
}

/** Mean metronome wait across in-range ticks; intent class = modal value (ties degrade to null). */
function buildAggregateMetronome(
  tickSummaries: TickSummaryDto[],
  range: TickRange,
): { metronomeWaitUs: number; metronomeIntentClass: MetronomeIntentClass | null } {
  let sum = 0;
  let count = 0;
  const tally = new Map<MetronomeIntentClass, number>();
  for (const t of tickSummaries) {
    const tn = numberValue((t as { tickNumber?: unknown }).tickNumber);
    if (tn == null || tn < range.from || tn > range.to) continue;
    sum += numberValue((t as { metronomeWaitUs?: unknown }).metronomeWaitUs) ?? 0;
    count += 1;
    const intent = decodeMetronomeIntentClass((t as { metronomeIntentClass?: unknown }).metronomeIntentClass);
    if (intent) tally.set(intent, (tally.get(intent) ?? 0) + 1);
  }
  let top: MetronomeIntentClass | null = null;
  let topCount = 0;
  let tied = false;
  for (const [k, v] of tally) {
    if (v > topCount) {
      top = k;
      topCount = v;
      tied = false;
    } else if (v === topCount && k !== top) {
      tied = true;
    }
  }
  return {
    metronomeWaitUs: count > 0 ? sum / count : 0,
    metronomeIntentClass: tied ? null : top,
  };
}

function numberValue(v: unknown): number | null {
  if (v == null) return null;
  const n = typeof v === 'number' ? v : Number(v as string);
  return Number.isFinite(n) ? n : null;
}
