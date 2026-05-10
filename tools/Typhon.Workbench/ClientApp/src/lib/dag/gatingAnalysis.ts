import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { SystemTickSummary } from '@/api/generated/model/systemTickSummary';
import type { DerivedEdge } from './edgeDerivation';
import type { TickRange } from '@/panels/SystemDag/useDagViewStore';

/**
 * "Why is this system blocked?" analytics. Per-system, over a tick range, identifies which
 * predecessor was the *gating* one (the predecessor whose `EndUs` matched the system's
 * `ReadyUs` — i.e. the one that took longest, determining when the system could start).
 *
 * Computed entirely client-side from data already on the wire — no engine change needed.
 * The math is exact: by definition, a system's `ReadyUs == max(predecessor.EndUs)`. Whichever
 * predecessor's `EndUs` matches the `ReadyUs` (within ε) IS the gater. Other predecessors
 * finished earlier and weren't the bottleneck.
 *
 * Aggregated across the selected range so the UI can answer "every tick? specific ticks? what
 * fraction?" — most actionable when one predecessor gates the system close to 100% of the time.
 */

export interface GaterEntry {
  /** Predecessor system name. */
  predecessorName: string;
  /** Number of ticks (in range) where THIS predecessor was the gater. */
  ticksGated: number;
  /** Total ticks (in range) where both this system and the predecessor had timestamps. */
  ticksObserved: number;
  /** Mean of `predecessor.EndUs - predecessor.StartUs` across observed ticks. Useful for
   *  judging "how big is this predecessor's contribution to the wait". */
  meanPredDurationUs: number;
  /** Mean of `predecessor.EndUs - system.ReadyUs` across observed ticks. ~0 for the gater
   *  by construction; positive for non-gaters (they finished earlier). */
  meanGatingMarginUs: number;
  /**
   * Edge metadata for this (predecessor → system) pair, when an edge exists in the DAG. Carries
   * the kind ('fresh' | 'snapshot' | 'manual' | 'event' | 'resource') and the via list (the
   * components / queues / resources that justified the edge). Drives the "why was the edge
   * needed?" half of the explanation in the side panel.
   */
  edge: DerivedEdge | null;
}

export interface SystemGatingInfo {
  systemName: string;
  /** Mean `(StartUs - ReadyUs)` across observed ticks — the dispatch wait. */
  meanWaitGapUs: number;
  /** Mean `(EndUs - StartUs)` across observed ticks — the system's own duration, for
   *  cross-reference with the heat colour. */
  meanDurationUs: number;
  /** Number of ticks (in range) where the system has a populated `ReadyUs`. */
  ticksObserved: number;
  /** Per-predecessor breakdown sorted by `ticksGated` DESC. The first entry is the
   *  most-frequent gater. */
  gaters: GaterEntry[];
}

interface ComputeInput {
  systems: SystemDefinitionDto[];
  rows: SystemTickSummary[] | null | undefined;
  edges: DerivedEdge[];
  range: TickRange | null;
}

export function computeGatingAnalysis(input: ComputeInput): Map<string, SystemGatingInfo> {
  const result = new Map<string, SystemGatingInfo>();
  if (!input.rows || input.rows.length === 0 || input.systems.length === 0) {
    return result;
  }

  // Build name lookups + the edge lookup keyed by (source, target).
  const indexToName = new Map<number, string>();
  const nameToSystem = new Map<string, SystemDefinitionDto>();
  for (const s of input.systems) {
    if (!s.name) continue;
    nameToSystem.set(s.name, s);
    const idx = numericValue(s.index);
    if (idx != null) indexToName.set(idx, s.name);
  }

  const edgeByPair = new Map<string, DerivedEdge>();
  for (const e of input.edges) {
    edgeByPair.set(`${e.source}|${e.target}`, e);
  }

  // Index rows by (tick, systemName) for O(1) lookups while iterating predecessors.
  // We materialise once instead of grepping the full rows array for every (tick, system) pair —
  // O(N) systems × O(P) preds per tick × O(R) rows would otherwise be cubic.
  type Row = { ready: number | null; start: number | null; end: number | null };
  const rowsByTickAndSystem = new Map<number, Map<string, Row>>();
  const ticksWithData = new Set<number>();

  for (const r of input.rows) {
    const tick = numericValue((r as { tickNumber?: unknown }).tickNumber);
    if (tick == null) continue;
    if (input.range && (tick < input.range.from || tick > input.range.to)) continue;
    const sysIdx = numericValue((r as { systemIndex?: unknown }).systemIndex);
    if (sysIdx == null) continue;
    const name = indexToName.get(sysIdx);
    if (!name) continue;

    const row: Row = {
      ready: numericValue((r as { readyUs?: unknown }).readyUs),
      start: numericValue((r as { startUs?: unknown }).startUs),
      end: numericValue((r as { endUs?: unknown }).endUs),
    };
    let bucket = rowsByTickAndSystem.get(tick);
    if (!bucket) {
      bucket = new Map();
      rowsByTickAndSystem.set(tick, bucket);
    }
    bucket.set(name, row);
    ticksWithData.add(tick);
  }

  // Per system, walk the ticks where the system has data and identify the gater.
  for (const [systemName, system] of nameToSystem) {
    const predIndices = (system.predecessors ?? [])
      .map((p) => numericValue(p))
      .filter((p): p is number => p != null);
    const predNames = predIndices
      .map((idx) => indexToName.get(idx))
      .filter((n): n is string => !!n);

    let ticksObserved = 0;
    let waitGapSum = 0;
    let durationSum = 0;
    // Per-predecessor accumulators: ticksGated, ticksObserved (both data), predDuration sum,
    // gating margin sum.
    type Acc = { gated: number; observed: number; predDur: number; margin: number };
    const accByPred = new Map<string, Acc>();

    for (const tick of ticksWithData) {
      const bucket = rowsByTickAndSystem.get(tick);
      if (!bucket) continue;
      const sysRow = bucket.get(systemName);
      if (!sysRow || sysRow.ready == null) continue;

      ticksObserved++;
      if (sysRow.start != null && sysRow.ready != null) {
        waitGapSum += sysRow.start - sysRow.ready;
      }
      if (sysRow.start != null && sysRow.end != null) {
        durationSum += sysRow.end - sysRow.start;
      }

      // Identify the gater for this tick: the predecessor with the LATEST EndUs among preds
      // that ran. By definition `system.ReadyUs >= max(pred.EndUs)` — the only gap is the few
      // ns the engine spends decrementing successor dep counters in `OnSystemComplete`. The
      // argmax IS the gating predecessor; a precise equality match against ReadyUs would be
      // wrong because that gap is real and varies with contention. If no predecessor has data
      // this tick (system is a root, or all preds were skipped), no gater is credited.
      let bestPredName: string | null = null;
      let bestPredEnd = -Infinity;
      for (const predName of predNames) {
        const predRow = bucket.get(predName);
        if (!predRow || predRow.end == null) continue;

        let acc = accByPred.get(predName);
        if (!acc) {
          acc = { gated: 0, observed: 0, predDur: 0, margin: 0 };
          accByPred.set(predName, acc);
        }
        acc.observed++;
        if (predRow.start != null) acc.predDur += predRow.end - predRow.start;
        acc.margin += sysRow.ready - predRow.end;

        if (predRow.end > bestPredEnd) {
          bestPredEnd = predRow.end;
          bestPredName = predName;
        }
      }

      if (bestPredName != null) {
        const acc = accByPred.get(bestPredName);
        if (acc) acc.gated++;
      }
    }

    if (ticksObserved === 0) continue;

    const gaters: GaterEntry[] = [];
    for (const [predName, acc] of accByPred) {
      gaters.push({
        predecessorName: predName,
        ticksGated: acc.gated,
        ticksObserved: acc.observed,
        meanPredDurationUs: acc.observed > 0 ? acc.predDur / acc.observed : 0,
        meanGatingMarginUs: acc.observed > 0 ? acc.margin / acc.observed : 0,
        edge: edgeByPair.get(`${predName}|${systemName}`) ?? null,
      });
    }
    gaters.sort((a, b) => b.ticksGated - a.ticksGated);

    result.set(systemName, {
      systemName,
      meanWaitGapUs: waitGapSum / ticksObserved,
      meanDurationUs: durationSum / ticksObserved,
      ticksObserved,
      gaters,
    });
  }

  return result;
}

function numericValue(v: unknown): number | null {
  if (v == null) return null;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : null;
}
