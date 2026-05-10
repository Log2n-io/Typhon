import type { PostTickSummary } from '@/api/generated/model/postTickSummary';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { SystemTickSummary } from '@/api/generated/model/systemTickSummary';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import type { DerivedEdge } from '@/lib/dag/edgeDerivation';
import type { TickRange } from '../SystemDag/useDagViewStore';

/**
 * Critical-path computation — client-side per `09-system-dag.md §9.3` ("Why client-side: zero
 * engine cost when the workbench isn't attached; iterate freely on the algorithm... without
 * redeploying").
 *
 * v1 algorithm: per tick, walk systems in phase order; pathDurationTo[S] = duration[S] + max of
 * (intra-phase predecessor pathDurationTo, previous-phase phase-fence). Traceback from the
 * max-pathDurationTo system at the last phase yields the critical path for that tick. Per-system
 * participation rate = (ticks on which the system was on the CP) / (total ticks examined).
 *
 * **What v1 does NOT model** (deferred to #317 Phase 3):
 * - Wait classification (phase-fence vs worker-claim vs chunk-dispatch) — we use raw
 *   pathDurationTo without distinguishing wait classes. The CP-rate stat doesn't depend on which
 *   bucket the wait falls in; the §5.2 wait segments do.
 * - Post-tick serial block — adds a constant tail, doesn't affect which user-system is on the CP.
 * - Compound systems — treated atomically through their parent declaration; expanding to walk
 *   into compound children needs `09-system-dag.md §4.4`'s parent-node feature first.
 *
 * **Returned shape** uses raw counts so callers can re-use the count in downstream displays
 * ("on CP 73% — 743 of 1024 ticks") without recomputing.
 */
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
  /** Edges from {@link deriveEdges}. Inter-phase edges are tolerated and ignored at walk time. */
  edges: DerivedEdge[];
  /** Phase names in declared order (`topology.phases`). */
  phases: string[];
  /** Tick range to consider. `null` means all rows. */
  range: TickRange | null;
}

export function computeCriticalPathParticipation(input: CriticalPathInputs): CriticalPathParticipation {
  const { systems, rows, edges, phases, range } = input;

  // ── Build name-keyed lookups once (re-used across all ticks). ───────────
  const indexToName = new Map<number, string>();
  const nameToPhase = new Map<string, string>();
  for (const s of systems) {
    if (!s.name) continue;
    const idx = numberValue(s.index);
    if (idx == null) continue;
    indexToName.set(idx, s.name);
    nameToPhase.set(s.name, s.phaseName ?? '');
  }

  // Predecessors map: targetSystemName → [sourceSystemNames]. Restricted to intra-phase edges
  // (cross-phase pairs use the phase-fence rule instead of an explicit edge). Read directly
  // from `system.predecessors` (engine-built — includes explicit `.After/.Before` chains the
  // client-side `deriveEdges` can't see). Falls back to `edges` for legacy traces / synthetic
  // test fixtures that pass edges without populating `system.predecessors`.
  const predecessorsByName = new Map<string, string[]>();
  for (const s of systems) {
    if (!s.name) continue;
    const targetPhase = nameToPhase.get(s.name);
    if (!targetPhase) continue;
    const predIdxs = (s as { predecessors?: unknown }).predecessors;
    if (!Array.isArray(predIdxs)) continue;
    const preds: string[] = [];
    for (const raw of predIdxs) {
      const predIdx = numberValue(raw);
      if (predIdx == null) continue;
      const predName = indexToName.get(predIdx);
      if (!predName) continue;
      const sourcePhase = nameToPhase.get(predName);
      if (sourcePhase !== targetPhase) continue;
      preds.push(predName);
    }
    if (preds.length > 0) predecessorsByName.set(s.name, preds);
  }
  if (predecessorsByName.size === 0) {
    for (const e of edges) {
      const sPhase = nameToPhase.get(e.source);
      const tPhase = nameToPhase.get(e.target);
      if (!sPhase || !tPhase || sPhase !== tPhase) continue;
      let preds = predecessorsByName.get(e.target);
      if (!preds) {
        preds = [];
        predecessorsByName.set(e.target, preds);
      }
      preds.push(e.source);
    }
  }

  // Phase index map for quick "is phase A before phase B" comparisons.
  const phaseIndex = new Map<string, number>();
  phases.forEach((p, i) => phaseIndex.set(p, i));

  // Systems grouped by phase, in declared order; unknown-phase systems live in a synthetic last
  // group keyed by '' (empty string).
  const phasesWithSystems: Array<{ phase: string; names: string[] }> = [];
  for (const p of phases) phasesWithSystems.push({ phase: p, names: [] });
  const synthetic: { phase: string; names: string[] } = { phase: '', names: [] };
  for (const s of systems) {
    if (!s.name) continue;
    const phase = s.phaseName ?? '';
    if (phase && phaseIndex.has(phase)) {
      phasesWithSystems[phaseIndex.get(phase)!].names.push(s.name);
    } else {
      synthetic.names.push(s.name);
    }
  }
  if (synthetic.names.length > 0) phasesWithSystems.push(synthetic);

  // ── Bucket rows by tick number, filtered to range. ─────────────────────
  const byTick = new Map<number, Map<string, number>>(); // tick → systemName → durationUs
  for (const r of rows) {
    const tick = numberValue(r.tickNumber);
    if (tick == null) continue;
    if (range && (tick < range.from || tick > range.to)) continue;
    const sysIdx = numberValue(r.systemIndex);
    if (sysIdx == null) continue;
    const name = indexToName.get(sysIdx);
    if (!name) continue;
    const duration = numberValue(r.durationUs) ?? 0;
    let bucket = byTick.get(tick);
    if (!bucket) {
      bucket = new Map<string, number>();
      byTick.set(tick, bucket);
    }
    bucket.set(name, duration);
  }

  const onPathTicksByName = new Map<string, number>();
  let totalTicks = 0;

  for (const [, durationByName] of byTick) {
    totalTicks++;
    const { onPath } = walkOneTick({
      durationByName,
      phasesWithSystems,
      predecessorsByName,
    });
    for (const name of onPath) {
      onPathTicksByName.set(name, (onPathTicksByName.get(name) ?? 0) + 1);
    }
  }

  const perSystem = new Map<string, { onPathTicks: number; rate: number }>();
  for (const [name, onPathTicks] of onPathTicksByName) {
    perSystem.set(name, {
      onPathTicks,
      rate: totalTicks > 0 ? onPathTicks / totalTicks : 0,
    });
  }
  return { perSystem, totalTicks };
}

// ── Per-tick walk ─────────────────────────────────────────────────────────

interface WalkInput {
  durationByName: Map<string, number>;
  phasesWithSystems: Array<{ phase: string; names: string[] }>;
  predecessorsByName: Map<string, string[]>;
}

interface WalkResult {
  /** Membership set — used by participation-rate aggregation across many ticks. */
  onPath: Set<string>;
  /** Forward execution order — first system on the path → terminus. Used by the tape renderer. */
  orderedPath: string[];
}

/**
 * Returns the set + ordered list of system names on the critical path for a single tick. Systems
 * that didn't run (no row this tick — i.e. skipped or filtered) contribute zero duration AND zero
 * pathDurationTo — they cannot be on the critical path.
 */
function walkOneTick(input: WalkInput): WalkResult {
  const { durationByName, phasesWithSystems, predecessorsByName } = input;
  // pathDurationTo[name] = wall-clock to finish this system, summing predecessors.
  const pathDurationTo = new Map<string, number>();
  // bestPredecessor[name] = the source name that maximised pathDurationTo[name] — used for traceback.
  // null sentinel means "no predecessor; root of the path."
  const bestPredecessor = new Map<string, string | null>();

  let phaseFenceFloor = 0;
  let phaseFenceSystem: string | null = null;

  for (const phase of phasesWithSystems) {
    let phaseMax = 0;
    let phaseMaxName: string | null = null;
    for (const name of phase.names) {
      const duration = durationByName.get(name);
      if (duration == null || duration <= 0) {
        // Skipped / didn't run — leave out of pathDurationTo so it can't be on the CP.
        continue;
      }
      let bestPred: string | null = null;
      let bestPredPathTo = 0;
      const preds = predecessorsByName.get(name) ?? [];
      for (const p of preds) {
        const pathTo = pathDurationTo.get(p);
        if (pathTo != null && pathTo > bestPredPathTo) {
          bestPredPathTo = pathTo;
          bestPred = p;
        }
      }
      // Phase-fence rule: a fresh phase starts after all of the previous phase finishes. If the
      // fence dominates an in-phase predecessor, the fence "predecessor" is the previous phase's
      // max-pathDurationTo system.
      let baseDuration: number;
      let chosenPred: string | null;
      if (bestPredPathTo >= phaseFenceFloor) {
        baseDuration = bestPredPathTo;
        chosenPred = bestPred;
      } else {
        baseDuration = phaseFenceFloor;
        chosenPred = phaseFenceSystem;
      }
      const pathTo = baseDuration + duration;
      pathDurationTo.set(name, pathTo);
      bestPredecessor.set(name, chosenPred);
      if (pathTo > phaseMax) {
        phaseMax = pathTo;
        phaseMaxName = name;
      }
    }
    if (phaseMaxName != null && phaseMax > phaseFenceFloor) {
      phaseFenceFloor = phaseMax;
      phaseFenceSystem = phaseMaxName;
    }
  }

  // Terminus = system with overall max pathDurationTo (last phase that produced anything).
  let terminus: string | null = null;
  let terminusPathTo = 0;
  for (const [name, pathTo] of pathDurationTo) {
    if (pathTo > terminusPathTo) {
      terminusPathTo = pathTo;
      terminus = name;
    }
  }
  if (terminus == null) return { onPath: new Set<string>(), orderedPath: [] };

  // Traceback walks back through bestPredecessor; we collect in reverse, then flip so the
  // returned `orderedPath` is in forward execution order (root → terminus). The renderer wants
  // forward order so bars stack top-to-bottom in time.
  const onPath = new Set<string>();
  const reverse: string[] = [];
  let cursor: string | null = terminus;
  // Bound the walk by total entries to avoid an infinite loop on a buggy bestPredecessor map.
  let safety = pathDurationTo.size + 2;
  while (cursor != null && safety-- > 0) {
    onPath.add(cursor);
    reverse.push(cursor);
    cursor = bestPredecessor.get(cursor) ?? null;
  }
  return { onPath, orderedPath: reverse.reverse() };
}

function numberValue(v: unknown): number | null {
  if (v == null) return null;
  const n = typeof v === 'number' ? v : Number(v as string);
  return Number.isFinite(n) ? n : null;
}

/**
 * Per-system skip rate over a tick range — `(rows with SkipReasonCode > 0) / (total rows for that
 * system)`. Skipped rows still have a SystemTickSummary entry (the engine records the skip decision
 * as telemetry), so the denominator includes them. Returns `null` for systems with zero rows in
 * the range — rendering should treat them as "didn't run" rather than "skipped 0%".
 */
export function computeSystemSkipRates(input: {
  systems: SystemDefinitionDto[];
  rows: SystemTickSummary[];
  range: TickRange | null;
}): Map<string, number> {
  const { systems, rows, range } = input;
  const indexToName = new Map<number, string>();
  for (const s of systems) {
    if (!s.name) continue;
    const idx = numberValue((s as { index?: unknown }).index);
    if (idx != null) indexToName.set(idx, s.name);
  }

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

// ── Per-tick tape data (#317 Phase 3 prep) ────────────────────────────────

/**
 * One CP-bar in the tape. `durationUs` is the system's wall-clock span; the wait fields are
 * rendered as separate hatched segments above the bar.
 *
 * `chunkDispatchWaitUs` is structurally present but always 0 in v1 — per `09-system-dag.md §5.2`
 * it requires per-chunk telemetry not yet on the wire. The field is reserved so the renderer can
 * consume it as soon as the engine ships chunk-grain timing without a shape change here.
 */
export interface TickPathBar {
  systemName: string;
  durationUs: number;
  /**
   * Σ chunk durations across all workers (chunker v13+). For parallel systems this can be far
   * larger than `durationUs` — drives the parallelism-inefficiency band on the tape.
   */
  totalCpuUs: number;
  /** Worker-claim wait (startUs - readyUs) — gap before a worker picked the system up. */
  workerClaimWaitUs: number;
  /** Chunk-dispatch wait — placeholder, always 0 in v1 (see comment above). */
  chunkDispatchWaitUs: number;
  /** True if this system used multiple workers — surfaces parallelism in the tooltip. */
  isParallel: boolean;
  workersTouched: number;
  chunksProcessed: number;
}

export interface TickPathPhase {
  name: string;
  /** Bars for systems on the CP within this phase, in forward execution order. */
  bars: TickPathBar[];
  /**
   * Every system in the phase that ran this tick but is NOT on the critical path, sorted by
   * `startUs`. Always populated; the renderer decides whether to show them based on the
   * `fullGantt` toggle. Excluded from `totalUs` — those values stay strictly wall-clock CP-only,
   * so adding non-CP bars to the view inflates the visual length but doesn't reinterpret the
   * core metric.
   */
  nonCpBars: TickPathBar[];
  /**
   * Phase-fence wait at the end of the phase: gap between the CP's last system finishing in this
   * phase and the slowest non-CP straggler clearing the phase fence. Zero when the CP holds the
   * straggler position itself.
   */
  phaseFenceWaitUs: number;
  /** Identity of the straggler that defined the fence (for the tooltip). null when no wait. */
  phaseFenceStraggler: string | null;
  /** Sum of bars' durations + waits — i.e. the phase's wall-clock contribution to the tick. */
  totalUs: number;
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
 * Path semantics — drives the tape's header label. With RFC 07 declarations, the bars represent
 * the actual dependency-driven critical path. Without declarations (no edges in the topology),
 * the bars are every system that ran, in `startUs` order — semantically just an execution
 * timeline, not a "longest data-flow path."
 */
export type PathMode = 'critical-path' | 'execution-order';

export interface TickPathBars {
  tickNumber: number;
  mode: PathMode;
  /**
   * Aggregate flag — `true` when the bars carry mean values across a tick range, `false` (or
   * undefined) when this is a single tick's CP. Drives the toolbar pill ("aggregate of N ticks")
   * and disables certain tooltip lines that don't make sense across many ticks.
   */
  aggregate?: { tickCount: number; range: { from: number; to: number } } | null;
  /** Phases in declared order; phases with no CP entries still appear (with empty bars) so the user sees the structure. */
  phases: TickPathPhase[];
  postTick: TickPathPostTick;
  /** Metronome wait that PRECEDED this tick (µs, saturated at 65535 per TickSummary v9+). */
  metronomeWaitUs: number;
  /**
   * intentClass for the metronome wait that preceded this tick. Drives the chip rendered on the
   * leading metronome stripe / tooltip. `null` when the tick has no recorded TickSummary row
   * (defensive — the renderer treats null as "unknown / suppress chip").
   * Mapping: 0=CatchUp, 1=Throttled, 2=Headroom (see DagScheduler comment).
   */
  metronomeIntentClass: MetronomeIntentClass | null;
  /** Sum of every bar + wait across all phases + post-tick — the total wall-clock height of the tape. */
  totalUs: number;
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
 * Pick the "dominant" tick in a range — the one with the longest wall-clock duration. Used as
 * the default focus tick for the tape when the user hasn't picked one explicitly. Falls back to
 * `null` when the range is empty.
 */
export function dominantTickInRange(
  tickSummaries: TickSummaryDto[] | null | undefined,
  range: TickRange | null,
): number | null {
  if (!tickSummaries || tickSummaries.length === 0 || !range) return null;
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
  return bestTick;
}

/**
 * Pick the tick the CP tape should focus on, given the current µs window plus its already-converted
 * tick range.
 *
 * Two-tier semantics, in priority order:
 * 1. **Strict**: at least one tick's `startUs` falls in `[time.start, time.end)`. Returns the
 *    longest such tick (delegates to {@link dominantTickInRange}). Covers the common case where
 *    the window spans one or more whole ticks.
 * 2. **Midpoint fallback**: no tick `startUs` is in window — typical when the user has zoomed
 *    *inside* one tick or straddled a boundary so the window is narrower than any tick. Returns
 *    the tick whose body `[startUs, startUs + durationUs)` contains the window's midpoint.
 *
 * Returns `null` only when no tick exists or both inputs are missing. `timeToTickRange` correctly
 * returns `null` for sub-tick windows (its semantic is "tick startUs in window"); this helper
 * rescues that case for the focus-tick use case where the user expects the tape to keep tracking
 * whatever tick they're zoomed into.
 */
export function focusTickForWindow(
  tickSummaries: TickSummaryDto[] | null | undefined,
  range: TickRange | null,
  time: { start: number; end: number } | null,
): number | null {
  const strict = dominantTickInRange(tickSummaries, range);
  if (strict != null) return strict;
  if (!time || !tickSummaries || tickSummaries.length === 0) return null;
  if (!Number.isFinite(time.start) || !Number.isFinite(time.end) || time.end <= time.start) return null;
  const mid = (time.start + time.end) / 2;
  // Linear scan (O(N) — N is at most a few thousand ticks; binary search would be faster but the
  // strict path covers the common case so this fallback is cold). Looking for a tick whose body
  // includes the midpoint. If durations overlap (shouldn't, but defensively), take the first.
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
 * Compute the CP-tape data for ONE tick — per `09-system-dag.md §5`. Output drives the vertical
 * timeline column rendering: bars in forward execution order, wait segments classified by the
 * three §5.2 categories, post-tick serial block, and metronome wait that preceded the tick.
 *
 * Returns `null` when no rows match `tickNumber` (e.g. tick is out of cache range).
 */
export function computeCriticalPathForTick(input: {
  tickNumber: number;
  systems: SystemDefinitionDto[];
  rows: SystemTickSummary[];
  edges: DerivedEdge[];
  phases: string[];
  postTickRows: PostTickSummary[];
  tickSummaryRow: TickSummaryDto | null;
}): TickPathBars | null {
  const { tickNumber, systems, rows, edges, phases, postTickRows, tickSummaryRow } = input;

  // Build name-keyed lookups (mirrors computeCriticalPathParticipation; intentional duplication
  // — keeping the two functions independent so changes to one don't ripple to the other).
  const indexToName = new Map<number, string>();
  const nameToPhase = new Map<string, string>();
  for (const s of systems) {
    if (!s.name) continue;
    const idx = numberValue((s as { index?: unknown }).index);
    if (idx != null) indexToName.set(idx, s.name);
    nameToPhase.set(s.name, s.phaseName ?? '');
  }
  const nameToDef = new Map<string, SystemDefinitionDto>();
  for (const s of systems) {
    if (s.name) nameToDef.set(s.name, s);
  }

  // Predecessor map: read directly from the engine-built `predecessors` field on each system.
  // The previous shape rebuilt this from `deriveEdges(systems)`, but `deriveEdges` only sees
  // access-declared edges — it can't see explicit `.After()` / `.Before()` chains because the
  // engine doesn't surface those on the wire (they fold into Successors at Build time and the
  // RecordBuilder ships ExplicitAfter/Before as empty arrays). For tier-discriminated W×W
  // chains (Brain_T0..T3, Metabolism_T0..T3, PheroDep_T0..T3) the chain edges are explicit-only,
  // so the client-side derivation missed them entirely and the CP traceback collapsed to a
  // single-system path. Using `system.predecessors` (computed engine-side from the full edge
  // set including explicit) gives the walker the complete graph. Falls back to `edges` for
  // legacy traces / synthetic test fixtures that pass edges without populating predecessors.
  const predecessorsByName = new Map<string, string[]>();
  for (const s of systems) {
    if (!s.name) continue;
    const targetPhase = nameToPhase.get(s.name);
    if (!targetPhase) continue;
    const predIdxs = (s as { predecessors?: unknown }).predecessors;
    if (!Array.isArray(predIdxs)) continue;
    const preds: string[] = [];
    for (const raw of predIdxs) {
      const predIdx = numberValue(raw);
      if (predIdx == null) continue;
      const predName = indexToName.get(predIdx);
      if (!predName) continue;
      const sourcePhase = nameToPhase.get(predName);
      if (sourcePhase !== targetPhase) continue; // intra-phase only — phase-fence rule covers cross-phase
      preds.push(predName);
    }
    if (preds.length > 0) predecessorsByName.set(s.name, preds);
  }
  if (predecessorsByName.size === 0) {
    for (const e of edges) {
      const sPhase = nameToPhase.get(e.source);
      const tPhase = nameToPhase.get(e.target);
      if (!sPhase || !tPhase || sPhase !== tPhase) continue;
      let preds = predecessorsByName.get(e.target);
      if (!preds) {
        preds = [];
        predecessorsByName.set(e.target, preds);
      }
      preds.push(e.source);
    }
  }

  const phaseIndex = new Map<string, number>();
  phases.forEach((p, i) => phaseIndex.set(p, i));

  const phasesWithSystems: Array<{ phase: string; names: string[] }> = [];
  for (const p of phases) phasesWithSystems.push({ phase: p, names: [] });
  const synthetic: { phase: string; names: string[] } = { phase: '', names: [] };
  for (const s of systems) {
    if (!s.name) continue;
    const phase = s.phaseName ?? '';
    if (phase && phaseIndex.has(phase)) {
      phasesWithSystems[phaseIndex.get(phase)!].names.push(s.name);
    } else {
      synthetic.names.push(s.name);
    }
  }
  if (synthetic.names.length > 0) phasesWithSystems.push(synthetic);

  // Index rows for THIS tick by system name.
  const rowByName = new Map<string, SystemTickSummary>();
  const durationByName = new Map<string, number>();
  for (const r of rows) {
    const tn = numberValue((r as { tickNumber?: unknown }).tickNumber);
    if (tn !== tickNumber) continue;
    const sysIdx = numberValue((r as { systemIndex?: unknown }).systemIndex);
    if (sysIdx == null) continue;
    const name = indexToName.get(sysIdx);
    if (!name) continue;
    rowByName.set(name, r);
    durationByName.set(name, numberValue((r as { durationUs?: unknown }).durationUs) ?? 0);
  }
  if (rowByName.size === 0) return null;

  // Branch on whether we have a dependency graph at all. With RFC 07 declarations on the wire,
  // `predecessorsByName` (built from `system.predecessors`) carries the full graph and the CP
  // walk traces a real wall-clock-longest path. The `edges` parameter is only checked as a
  // legacy fallback signal — older traces / synthetic test fixtures might pass edges without
  // populating `system.predecessors`. Either signal having content means we have a graph.
  // Without either, every system is a graph root and the walker would degenerate to
  // "single-system path = the system with max duration." Fallback: show every system that ran
  // in the tick, sorted by startUs.
  const hasGraph = predecessorsByName.size > 0 || edges.length > 0;
  const orderedPath = !hasGraph
    ? buildFallbackOrderByStartUs(rowByName)
    : walkOneTick({ durationByName, phasesWithSystems, predecessorsByName }).orderedPath;
  if (orderedPath.length === 0) return null;

  // ── Bucket the ordered CP into phase rows. We keep ALL declared phases (even empty ones) so
  // the tape shows the full structural scaffold even on light ticks. ─────
  const phaseByName = (name: string): string => nameToPhase.get(name) ?? '';

  const out: TickPathPhase[] = [];
  for (const p of phases) {
    out.push({ name: p, bars: [], nonCpBars: [], phaseFenceWaitUs: 0, phaseFenceStraggler: null, totalUs: 0 });
  }
  const synthRow: TickPathPhase = { name: '(unphased)', bars: [], nonCpBars: [], phaseFenceWaitUs: 0, phaseFenceStraggler: null, totalUs: 0 };
  let synthEverNeeded = false;
  const cpSet = new Set<string>();
  for (const name of orderedPath) {
    const phase = phaseByName(name);
    const target = phase && phaseIndex.has(phase) ? out[phaseIndex.get(phase)!] : (synthEverNeeded = true, synthRow);
    target.bars.push(makeBar(name, rowByName.get(name)!, nameToDef.get(name)));
    cpSet.add(name);
  }
  if (synthEverNeeded) out.push(synthRow);

  // Populate non-CP bars per phase — every system that ran this tick and isn't on the CP. Sorted
  // by startUs so the full-Gantt rendering reads as "execution order within the phase". Skipped
  // rows (durationUs <= 0) are excluded — same rule as the CP walker.
  const nonCpEntries = new Map<string, Array<{ name: string; startUs: number }>>();
  for (const [name, row] of rowByName) {
    if (cpSet.has(name)) continue;
    const dur = numberValue((row as { durationUs?: unknown }).durationUs) ?? 0;
    if (dur <= 0) continue;
    const phase = phaseByName(name);
    const phaseKey = phase && phaseIndex.has(phase) ? phase : '';
    const startUs = numberValue((row as { startUs?: unknown }).startUs) ?? 0;
    let bucket = nonCpEntries.get(phaseKey);
    if (!bucket) {
      bucket = [];
      nonCpEntries.set(phaseKey, bucket);
    }
    bucket.push({ name, startUs });
  }
  for (const [phaseKey, entries] of nonCpEntries) {
    entries.sort((a, b) => (a.startUs - b.startUs) || a.name.localeCompare(b.name));
    const target = phaseKey === ''
      ? out.find((p) => p.name === '(unphased)') ?? null
      : out[phaseIndex.get(phaseKey)!];
    if (!target) continue;
    for (const e of entries) {
      target.nonCpBars.push(makeBar(e.name, rowByName.get(e.name)!, nameToDef.get(e.name)));
    }
  }

  // Phase-fence wait per phase: max EndUs across ALL systems in the phase (CP or not) minus the
  // CP's last system EndUs in that phase. Zero if the CP terminates at the straggler.
  //
  // **Skipped in execution-order fallback** — without a dependency graph, the "CP tail" is just
  // "the alphabetically-last system tied at startUs=0", which has no well-defined relationship
  // with the phase max endUs. Reporting a wait would be meaningless.
  const inFallback = !hasGraph;
  for (const phaseRow of out) {
    if (phaseRow.bars.length === 0) continue;
    if (inFallback) {
      // Tally totalUs without phase-fence wait — bars only.
      let total = 0;
      for (const bar of phaseRow.bars) {
        total += bar.durationUs + bar.workerClaimWaitUs + bar.chunkDispatchWaitUs;
      }
      phaseRow.totalUs = total;
      continue;
    }

    // Slowest endUs across every system in the phase that ran this tick.
    let phaseMaxEnd = -Infinity;
    let stragglerName: string | null = null;
    for (const [name, row] of rowByName) {
      if (phaseByName(name) !== (phaseRow.name === '(unphased)' ? '' : phaseRow.name)) continue;
      const endUs = numberValue((row as { endUs?: unknown }).endUs) ?? 0;
      if (endUs > phaseMaxEnd) {
        phaseMaxEnd = endUs;
        stragglerName = name;
      }
    }
    // CP's last system in this phase = last bar in the phase row (forward order).
    const cpLastName = phaseRow.bars[phaseRow.bars.length - 1].systemName;
    const cpLastRow = rowByName.get(cpLastName);
    const cpLastEnd = cpLastRow ? (numberValue((cpLastRow as { endUs?: unknown }).endUs) ?? 0) : 0;
    const wait = Math.max(0, phaseMaxEnd - cpLastEnd);
    if (wait > 0 && stragglerName !== cpLastName) {
      phaseRow.phaseFenceWaitUs = wait;
      phaseRow.phaseFenceStraggler = stragglerName;
    }
    // Total = sum of bar durations + bar waits + phase-fence wait.
    let total = phaseRow.phaseFenceWaitUs;
    for (const bar of phaseRow.bars) {
      total += bar.durationUs + bar.workerClaimWaitUs + bar.chunkDispatchWaitUs;
    }
    phaseRow.totalUs = total;
  }

  // Post-tick block.
  const postRow = postTickRows.find((r) => numberValue((r as { tickNumber?: unknown }).tickNumber) === tickNumber);
  const postTick: TickPathPostTick = postRow
    ? {
        writeTickFenceUs: numberValue((postRow as { writeTickFenceUs?: unknown }).writeTickFenceUs) ?? 0,
        walFlushUs: numberValue((postRow as { walFlushUs?: unknown }).walFlushUs) ?? 0,
        subscriptionOutputUs: numberValue((postRow as { subscriptionOutputUs?: unknown }).subscriptionOutputUs) ?? 0,
        tierIndexRebuildUs: numberValue((postRow as { tierIndexRebuildUs?: unknown }).tierIndexRebuildUs) ?? 0,
        dormancySweepUs: numberValue((postRow as { dormancySweepUs?: unknown }).dormancySweepUs) ?? 0,
        tierBudgetUs: numberValue((postRow as { tierBudgetUs?: unknown }).tierBudgetUs) ?? 0,
        totalUs: 0,
      }
    : { writeTickFenceUs: 0, walFlushUs: 0, subscriptionOutputUs: 0, tierIndexRebuildUs: 0, dormancySweepUs: 0, tierBudgetUs: 0, totalUs: 0 };
  postTick.totalUs =
    postTick.writeTickFenceUs +
    postTick.walFlushUs +
    postTick.subscriptionOutputUs +
    postTick.tierIndexRebuildUs +
    postTick.dormancySweepUs +
    postTick.tierBudgetUs;

  const metronomeWaitUs = tickSummaryRow
    ? numberValue((tickSummaryRow as { metronomeWaitUs?: unknown }).metronomeWaitUs) ?? 0
    : 0;
  const metronomeIntentClass = tickSummaryRow
    ? decodeMetronomeIntentClass((tickSummaryRow as { metronomeIntentClass?: unknown }).metronomeIntentClass)
    : null;

  let totalUs = postTick.totalUs + metronomeWaitUs;
  for (const phaseRow of out) totalUs += phaseRow.totalUs;
  const mode: PathMode = hasGraph ? 'critical-path' : 'execution-order';
  return { tickNumber, mode, phases: out, postTick, metronomeWaitUs, metronomeIntentClass, totalUs };
}

/**
 * Fallback ordering used when the topology has no edges (engine isn't surfacing RFC 07 access
 * declarations). Returns every system that genuinely ran (durationUs > 0) sorted by startUs
 * ascending. Ties broken by name for determinism. Skipped rows are excluded — same rule as the
 * dependency-aware walker.
 */
function buildFallbackOrderByStartUs(rowByName: Map<string, SystemTickSummary>): string[] {
  const entries: Array<{ name: string; startUs: number }> = [];
  for (const [name, row] of rowByName) {
    const dur = numberValue((row as { durationUs?: unknown }).durationUs) ?? 0;
    if (dur <= 0) continue;
    const startUs = numberValue((row as { startUs?: unknown }).startUs) ?? 0;
    entries.push({ name, startUs });
  }
  entries.sort((a, b) => (a.startUs - b.startUs) || a.name.localeCompare(b.name));
  return entries.map((e) => e.name);
}

// ── Aggregate (range) tape ────────────────────────────────────────────────

/**
 * Compute aggregate critical-path bars across a range of ticks. For each system that appeared on
 * the CP at least once in the range, the bar carries `mean(durationUs over ticks-on-CP)`. Phase
 * grouping mirrors single-tick mode; bars within a phase are sorted by mean duration descending
 * (no execution-order is meaningful across many ticks). Wait segments + phase-fence are skipped
 * — averaging waits across heterogeneous ticks is too noisy to surface in v1. Post-tick / metronome
 * carry per-key means across the range.
 *
 * Returned shape uses `tickNumber = -1` as the "not a real tick" sentinel; the toolbar shows
 * "aggregate of N ticks" in place of the tick label. `aggregate` carries the participation count
 * and the actual range used. Returns `null` when no ticks fell in range.
 */
export function computeAggregateCriticalPath(input: {
  systems: SystemDefinitionDto[];
  rows: SystemTickSummary[];
  edges: DerivedEdge[];
  phases: string[];
  postTickRows: PostTickSummary[];
  tickSummaries: TickSummaryDto[];
  range: TickRange | null;
}): TickPathBars | null {
  const { systems, rows, edges, phases, postTickRows, tickSummaries, range } = input;
  if (!range) return null;

  // Collect the set of ticks in range with at least one row. Reuse single-tick CP for each;
  // accumulate per-system on-CP duration sums + counts.
  const sumByName = new Map<string, number>();
  const countByName = new Map<string, number>();
  const phaseByNameMap = new Map<string, string>();
  for (const s of systems) {
    if (s.name) phaseByNameMap.set(s.name, s.phaseName ?? '');
  }

  const ticksSeen = new Set<number>();
  for (const r of rows) {
    const tn = numberValue((r as { tickNumber?: unknown }).tickNumber);
    if (tn == null || tn < range.from || tn > range.to) continue;
    ticksSeen.add(tn);
  }
  if (ticksSeen.size === 0) return null;

  // Walk each tick's CP. Reuse `computeCriticalPathForTick` so the dependency / phase-fence /
  // fallback logic stays identical.
  for (const tickNumber of ticksSeen) {
    const tickSummaryRow = tickSummaries.find((t) => Number(t.tickNumber) === tickNumber) ?? null;
    const bars = computeCriticalPathForTick({
      tickNumber, systems, rows, edges, phases, postTickRows, tickSummaryRow,
    });
    if (!bars) continue;
    for (const phase of bars.phases) {
      for (const bar of phase.bars) {
        sumByName.set(bar.systemName, (sumByName.get(bar.systemName) ?? 0) + bar.durationUs);
        countByName.set(bar.systemName, (countByName.get(bar.systemName) ?? 0) + 1);
      }
    }
  }

  if (sumByName.size === 0) return null;

  // Bucket per phase + sort by mean desc.
  const phaseIndex = new Map<string, number>();
  phases.forEach((p, i) => phaseIndex.set(p, i));
  const phaseRows: TickPathPhase[] = [];
  for (const p of phases) phaseRows.push({ name: p, bars: [], nonCpBars: [], phaseFenceWaitUs: 0, phaseFenceStraggler: null, totalUs: 0 });
  const synth: TickPathPhase = { name: '(unphased)', bars: [], nonCpBars: [], phaseFenceWaitUs: 0, phaseFenceStraggler: null, totalUs: 0 };
  let synthNeeded = false;

  const nameToDef = new Map<string, SystemDefinitionDto>();
  for (const s of systems) {
    if (s.name) nameToDef.set(s.name, s);
  }

  const interim = new Map<string, Array<{ name: string; meanUs: number }>>();
  for (const [name, sum] of sumByName) {
    const count = countByName.get(name) ?? 1;
    const meanUs = sum / count;
    const phase = phaseByNameMap.get(name) ?? '';
    const key = phase && phaseIndex.has(phase) ? phase : '';
    let bucket = interim.get(key);
    if (!bucket) {
      bucket = [];
      interim.set(key, bucket);
    }
    bucket.push({ name, meanUs });
  }
  for (const [key, entries] of interim) {
    entries.sort((a, b) => b.meanUs - a.meanUs);
    const target = key === '' ? (synthNeeded = true, synth) : phaseRows[phaseIndex.get(key)!];
    for (const e of entries) {
      const def = nameToDef.get(e.name);
      target.bars.push({
        systemName: e.name,
        durationUs: e.meanUs,
        totalCpuUs: 0, // aggregate mode skips parallelism band; A1 pill uses range-mean from the same data
        workerClaimWaitUs: 0,
        chunkDispatchWaitUs: 0,
        isParallel: def?.isParallel ?? false,
        workersTouched: 0,
        chunksProcessed: 0,
      });
    }
  }
  if (synthNeeded) phaseRows.push(synth);
  for (const phaseRow of phaseRows) {
    let total = 0;
    for (const bar of phaseRow.bars) total += bar.durationUs;
    phaseRow.totalUs = total;
  }

  // Aggregate post-tick — per-key mean across the range. Skipped tickSummary rows (range filter)
  // contribute zero; we count tickSummaries IN RANGE for the divisor so partial post-tick coverage
  // doesn't wildly inflate the mean.
  const postSums = { writeTickFenceUs: 0, walFlushUs: 0, subscriptionOutputUs: 0, tierIndexRebuildUs: 0, dormancySweepUs: 0, tierBudgetUs: 0 };
  let postCount = 0;
  for (const r of postTickRows) {
    const tn = numberValue((r as { tickNumber?: unknown }).tickNumber);
    if (tn == null || tn < range.from || tn > range.to) continue;
    postCount++;
    postSums.writeTickFenceUs     += numberValue((r as { writeTickFenceUs?: unknown }).writeTickFenceUs)     ?? 0;
    postSums.walFlushUs           += numberValue((r as { walFlushUs?: unknown }).walFlushUs)                 ?? 0;
    postSums.subscriptionOutputUs += numberValue((r as { subscriptionOutputUs?: unknown }).subscriptionOutputUs) ?? 0;
    postSums.tierIndexRebuildUs   += numberValue((r as { tierIndexRebuildUs?: unknown }).tierIndexRebuildUs) ?? 0;
    postSums.dormancySweepUs      += numberValue((r as { dormancySweepUs?: unknown }).dormancySweepUs)       ?? 0;
    postSums.tierBudgetUs         += numberValue((r as { tierBudgetUs?: unknown }).tierBudgetUs)             ?? 0;
  }
  const div = Math.max(1, postCount);
  const postTick: TickPathPostTick = {
    writeTickFenceUs:     postSums.writeTickFenceUs / div,
    walFlushUs:           postSums.walFlushUs / div,
    subscriptionOutputUs: postSums.subscriptionOutputUs / div,
    tierIndexRebuildUs:   postSums.tierIndexRebuildUs / div,
    dormancySweepUs:      postSums.dormancySweepUs / div,
    tierBudgetUs:         postSums.tierBudgetUs / div,
    totalUs: 0,
  };
  postTick.totalUs = postTick.writeTickFenceUs + postTick.walFlushUs + postTick.subscriptionOutputUs
    + postTick.tierIndexRebuildUs + postTick.dormancySweepUs + postTick.tierBudgetUs;

  // Metronome aggregate — mean across in-range tickSummaries. intentClass is the modal value
  // (most common); ties + mixed buckets degrade to null.
  let metroSum = 0;
  let metroCount = 0;
  const intentTally = new Map<MetronomeIntentClass, number>();
  for (const t of tickSummaries) {
    const tn = numberValue((t as { tickNumber?: unknown }).tickNumber);
    if (tn == null || tn < range.from || tn > range.to) continue;
    metroSum += numberValue((t as { metronomeWaitUs?: unknown }).metronomeWaitUs) ?? 0;
    metroCount++;
    const intent = decodeMetronomeIntentClass((t as { metronomeIntentClass?: unknown }).metronomeIntentClass);
    if (intent) intentTally.set(intent, (intentTally.get(intent) ?? 0) + 1);
  }
  const metronomeWaitUs = metroCount > 0 ? metroSum / metroCount : 0;
  let topIntent: MetronomeIntentClass | null = null;
  let topIntentCount = 0;
  let intentTied = false;
  for (const [k, v] of intentTally) {
    if (v > topIntentCount) {
      topIntent = k;
      topIntentCount = v;
      intentTied = false;
    } else if (v === topIntentCount && k !== topIntent) {
      intentTied = true;
    }
  }
  const metronomeIntentClass: MetronomeIntentClass | null = intentTied ? null : topIntent;

  let totalUs = postTick.totalUs + metronomeWaitUs;
  for (const phaseRow of phaseRows) totalUs += phaseRow.totalUs;
  const mode: PathMode = edges.length > 0 ? 'critical-path' : 'execution-order';

  return {
    tickNumber: -1,
    mode,
    aggregate: { tickCount: ticksSeen.size, range: { from: range.from, to: range.to } },
    phases: phaseRows,
    postTick,
    metronomeWaitUs,
    metronomeIntentClass,
    totalUs,
  };
}

function makeBar(name: string, row: SystemTickSummary, def: SystemDefinitionDto | undefined): TickPathBar {
  const durationUs = numberValue((row as { durationUs?: unknown }).durationUs) ?? 0;
  const startUs = numberValue((row as { startUs?: unknown }).startUs) ?? 0;
  const readyUs = numberValue((row as { readyUs?: unknown }).readyUs) ?? 0;
  // readyUs == 0 in older traces (#311 was the version that started capturing it). Treat zero as
  // "unknown" rather than a real "ready immediately at engine start" — render no claim wait.
  const workerClaimWaitUs = readyUs > 0 ? Math.max(0, startUs - readyUs) : 0;
  // Cache v13+ ships totalCpuUs (Σ chunk durations across workers); v12 leaves it absent → 0.
  const totalCpuUs = numberValue((row as { totalCpuUs?: unknown }).totalCpuUs) ?? 0;
  return {
    systemName: name,
    durationUs,
    totalCpuUs,
    workerClaimWaitUs,
    chunkDispatchWaitUs: 0,
    isParallel: def?.isParallel ?? false,
    workersTouched: numberValue((row as { workersTouched?: unknown }).workersTouched) ?? 0,
    chunksProcessed: numberValue((row as { chunksProcessed?: unknown }).chunksProcessed) ?? 0,
  };
}
