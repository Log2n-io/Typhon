import type { SystemTickSummary } from '@/api/generated/model/systemTickSummary';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import type { TickRange } from './useDagViewStore';

/**
 * Parallelism-inefficiency / wait-time computation for the System DAG view's toolbar pill (A1) and
 * sparkline (A6), and the Critical Path tape's per-phase utilization band (A2).
 *
 * **Formula (per Loïc's spec).**
 *
 * ```
 *   work     = Σ systemTickSummary.totalCpuUs        // CPU consumed across every worker, per system
 *   wallTime = tickSummary.durationUs                // measured tick wall-clock
 *   capacity = workerCount × wallTime                // worker-µs available
 *   wait     = capacity - work                       // worker-µs unused (parallelism inefficiency)
 *   util     = work / capacity                       // ∈ [0, 1]
 *   waitPct  = 1 - util
 * ```
 *
 * **Why `totalCpuUs` and not `durationUs`.** `durationUs` is wall-clock per system — for a parallel
 * system using 16 workers for 690 µs of wall, `durationUs = 690 µs`. The actual CPU consumed is
 * ~16 × chunk_avg ≈ 5,700 µs. Summing `durationUs` collapses parallel work into one number and
 * massively under-counts cpu use; summing `totalCpuUs` (folded by the cache builder from chunk
 * spans, chunker v13+) gives the true worker-µs consumed. Old caches default to 0 — the pill /
 * band stay hidden until the cache is rebuilt.
 *
 * `wallTime` is taken from `TickSummary.DurationUs` (engine-recorded). It includes post-tick serial
 * (WAL fsync, etc.) — single-threaded work where other workers are idle, correctly accounted as
 * "wait" under the "worker-µs unused" framing.
 */

export interface TickUtilization {
  tickNumber: number;
  /** Sum of every system's duration in this tick (µs). */
  workUs: number;
  /** Tick wall-clock from telemetry (µs). */
  wallTimeUs: number;
  /** Worker-µs available = workerCount × wallTimeUs. */
  capacityUs: number;
  /** Capacity minus work — saturated to 0 when work > capacity (defensive, shouldn't happen). */
  waitUs: number;
  /** work / capacity ∈ [0, 1]. NaN-safe; capacity == 0 → 0. */
  utilization: number;
}

export interface RangeUtilization {
  workerCount: number;
  /** Per-tick rows in tickNumber-ascending order — drives the A6 sparkline. */
  perTick: TickUtilization[];
  /** Sum of every tick's `waitUs` in the range. */
  totalWaitUs: number;
  /** Sum of every tick's `capacityUs` in the range. */
  totalCapacityUs: number;
  /** Sum of every tick's `workUs` in the range. */
  totalWorkUs: number;
  /** totalWorkUs / totalCapacityUs — the headline number for A1 (work-weighted, not arithmetic mean of per-tick %). */
  meanUtilization: number;
  /** 1 - meanUtilization, also the headline. */
  meanWaitFraction: number;
  /** Arithmetic mean of `waitUs` per tick — for the "wait X.Xms / tick" readout. */
  meanWaitUsPerTick: number;
}

/**
 * Compute per-tick utilization and the range mean for a closed tick range. Returns `null` when
 * inputs are insufficient (no worker count, no rows, empty range).
 *
 * **Why work-weighted mean** (not arithmetic mean of per-tick %): a single 50ms outlier tick
 * dominates user-visible wall-clock pain, but arithmetic-mean dilutes it across 600 cheap ticks.
 * Work-weighted gives the same answer as "if you sum the wait time across the whole range,
 * what fraction of total worker-time was that?" — which is what the user actually feels.
 */
export function computeRangeUtilization(input: {
  workerCount: number | null;
  tickSummaries: readonly TickSummaryDto[] | null;
  systemTickSummaries: readonly SystemTickSummary[] | null;
  range: TickRange | null;
}): RangeUtilization | null {
  const { workerCount, tickSummaries, systemTickSummaries, range } = input;
  if (!workerCount || workerCount < 1) return null;
  if (!tickSummaries || tickSummaries.length === 0) return null;
  if (!systemTickSummaries || systemTickSummaries.length === 0) return null;
  if (!range) return null;

  // Sum work per tick in one pass over systemTickSummaries — O(N) where N = systems × ticks-in-range.
  // `totalCpuUs` is the CPU consumed across all workers for THIS system on THIS tick (chunker v13+).
  // For parallel systems it's ~workersTouched × chunkAvg, much larger than the wall-clock `durationUs`.
  // Older caches without the field deliver zero, which makes the pill stay hidden until rebuild.
  const workByTick = new Map<number, number>();
  for (const r of systemTickSummaries) {
    const tick = num(r['tickNumber']);
    if (tick == null || tick < range.from || tick > range.to) continue;
    const cpu = num(r['totalCpuUs']) ?? 0;
    if (cpu <= 0) continue;
    workByTick.set(tick, (workByTick.get(tick) ?? 0) + cpu);
  }

  const perTick: TickUtilization[] = [];
  let totalWait = 0;
  let totalCapacity = 0;
  let totalWork = 0;
  for (const t of tickSummaries) {
    const tick = num(t.tickNumber);
    if (tick == null || tick < range.from || tick > range.to) continue;
    const wallTime = num(t.durationUs) ?? 0;
    if (wallTime <= 0) continue;
    const work = workByTick.get(tick) ?? 0;
    const capacity = workerCount * wallTime;
    const wait = Math.max(0, capacity - work);
    const utilization = capacity > 0 ? Math.min(1, work / capacity) : 0;
    perTick.push({ tickNumber: tick, workUs: work, wallTimeUs: wallTime, capacityUs: capacity, waitUs: wait, utilization });
    totalWait += wait;
    totalCapacity += capacity;
    totalWork += work;
  }

  if (perTick.length === 0) return null;
  // Pre-v13 caches don't carry totalCpuUs — every row defaults to zero, totalWork stays at zero.
  // The formula would render "100% wait" which is technically correct but misleading: it really
  // means "we don't know how much cpu was used". Hide the pill in that case so the user gets the
  // "rebuild your cache" hint by absence rather than a fake-alarming number.
  if (totalWork === 0) return null;
  perTick.sort((a, b) => a.tickNumber - b.tickNumber);

  const meanUtilization = totalCapacity > 0 ? totalWork / totalCapacity : 0;
  return {
    workerCount,
    perTick,
    totalWaitUs: totalWait,
    totalCapacityUs: totalCapacity,
    totalWorkUs: totalWork,
    meanUtilization,
    meanWaitFraction: 1 - meanUtilization,
    meanWaitUsPerTick: totalWait / perTick.length,
  };
}

/**
 * Per-phase utilization for ONE tick — drives the A2 phase-fence band on the CP tape. Inputs are
 * the per-phase `bars[].nonCpBars[]` lists already computed by {@link computeCriticalPathForTick}
 * (which carry every system that ran in the phase, CP and non-CP), plus the phase's wall-clock
 * `totalUs`. Capacity per phase = workerCount × phase.totalUs.
 *
 * Defined here (not in `criticalPath.ts`) so the parallelism concept lives in one place; the CP
 * module stays focused on path-tracing and wait-classification.
 */
export interface PhaseUtilization {
  /** Sum of every system's durationUs in the phase (CP + non-CP). */
  workUs: number;
  /** Phase wall-clock contribution (already includes CP bars + claim waits + fence waits). */
  wallTimeUs: number;
  capacityUs: number;
  waitUs: number;
  utilization: number;
}

export function computePhaseUtilization(input: {
  workerCount: number | null;
  /** Sum of CP-bar durations + non-CP-bar durations in the phase. */
  phaseWorkUs: number;
  /** Phase wall-clock (e.g. `TickPathPhase.totalUs`). */
  phaseWallTimeUs: number;
}): PhaseUtilization | null {
  const { workerCount, phaseWorkUs, phaseWallTimeUs } = input;
  if (!workerCount || workerCount < 1) return null;
  if (phaseWallTimeUs <= 0) return null;
  const capacity = workerCount * phaseWallTimeUs;
  const wait = Math.max(0, capacity - phaseWorkUs);
  const utilization = capacity > 0 ? Math.min(1, phaseWorkUs / capacity) : 0;
  return { workUs: phaseWorkUs, wallTimeUs: phaseWallTimeUs, capacityUs: capacity, waitUs: wait, utilization };
}

function num(v: unknown): number | null {
  if (v == null) return null;
  const n = typeof v === 'number' ? v : Number(v as string);
  return Number.isFinite(n) ? n : null;
}
