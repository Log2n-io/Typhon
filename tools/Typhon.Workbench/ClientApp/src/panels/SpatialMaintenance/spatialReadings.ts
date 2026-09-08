import type { SpatialTickTelemetry, TickData } from '@/libs/profiler/model/traceModel';

/**
 * The three derived readings the Spatial panel exists to show (#911 O3), as pure functions over decoded ticks.
 *
 * Kept out of the component for the usual reason plus a specific one: each of these encodes a claim about the engine that is easy to
 * get subtly wrong (a lagged identity, a mean over a possibly-empty sample, a defect signature), and a claim worth testing is a claim
 * worth writing where a test can reach it.
 */

/** One tick's spatial row, paired with the tick it came from — the panel must be able to SAY which tick it is showing. */
export interface SpatialSample {
  tickNumber: number;
  row: SpatialTickTelemetry;
}

/**
 * The most recent tick in `ticks` that carried a record for `archetypeId`, or null.
 *
 * <b>Not "the last tick".</b> A tick whose fence did no spatial work emits nothing, so the newest tick in the window is frequently
 * silent while the archetype is perfectly healthy. Falling back to the newest tick regardless would render zeros as if they were this
 * tick's measurement.
 */
export function latestSampleFor(ticks: readonly TickData[], archetypeId: number): SpatialSample | null {
  for (let i = ticks.length - 1; i >= 0; i--) {
    const row = ticks[i].spatialByArchetype?.get(archetypeId);
    if (row !== undefined) {
      return { tickNumber: ticks[i].tickNumber, row };
    }
  }
  return null;
}

/** Every archetype id seen anywhere in the window, ascending. */
export function archetypeIdsIn(ticks: readonly TickData[]): number[] {
  const ids = new Set<number>();
  for (const t of ticks) {
    if (t.spatialByArchetype === undefined) continue;
    for (const id of t.spatialByArchetype.keys()) ids.add(id);
  }
  return [...ids].sort((a, b) => a - b);
}

// ── Reading 1: the drifter identity, shown as a check ────────────────────────────────────────────────────────────

export interface IdentityCheck {
  /** The tick whose DETECTION is the left-hand side. */
  detectedTick: number;
  /** The tick whose OUTCOMES are the right-hand side — the tick after `detectedTick`, per rule TH-02. */
  outcomeTick: number;
  detected: number;
  admitted: number;
  throttled: number;
  superseded: number;
  unplaced: number;
  /** `admitted + throttled + superseded + unplaced`. */
  accountedFor: number;
  balanced: boolean;
}

/**
 * `DriftersDetected == admitted + throttled + superseded + unplaced`, evaluated across the ONE-TICK LAG the engine actually has.
 *
 * Drift detection runs in AabbRefresh, which is phase 3 — after Migrate. So the relocations a tick detects are decided by the NEXT
 * tick's Prep, and pairing a tick's detection with its own outcomes compares two different populations and reads as a permanent
 * imbalance (rule `TH-02`). This walks back from the newest tick for the most recent consecutive pair that carries the archetype on
 * both sides.
 *
 * Returns null when the window holds no such pair — which is the honest answer, not a balanced check over zeros.
 */
export function checkDrifterIdentity(ticks: readonly TickData[], archetypeId: number): IdentityCheck | null {
  for (let i = ticks.length - 1; i >= 1; i--) {
    const outcome = ticks[i].spatialByArchetype?.get(archetypeId);
    const detect = ticks[i - 1].spatialByArchetype?.get(archetypeId);
    if (outcome === undefined || detect === undefined) continue;
    if (ticks[i].tickNumber !== ticks[i - 1].tickNumber + 1) continue;   // a gap in the window is not a lag

    const accountedFor = outcome.relocationsAdmitted + outcome.relocationsThrottled
      + outcome.relocationsSuperseded + outcome.driftersUnplaced;
    return {
      detectedTick: ticks[i - 1].tickNumber,
      outcomeTick: ticks[i].tickNumber,
      detected: detect.driftersDetected,
      admitted: outcome.relocationsAdmitted,
      throttled: outcome.relocationsThrottled,
      superseded: outcome.relocationsSuperseded,
      unplaced: outcome.driftersUnplaced,
      accountedFor,
      balanced: detect.driftersDetected === accountedFor,
    };
  }
  return null;
}

// ── Reading 2: tightness against the bound ───────────────────────────────────────────────────────────────────────

export interface TightnessReading {
  /** Clusters that contributed. ZERO is a real state: the fence wrote nothing this tick. */
  samples: number;
  /** Mean measured extent as a fraction of the cell edge. */
  extentRatio: number;
  /** Mean packing bound — the tightest geometry allows, as a fraction of the cell edge. */
  packingBound: number;
  /** `extentRatio / packingBound`. 1 is optimal packing. Zero when there are no samples. */
  toBound: number;
  /**
   * True when the bound is 1 — the cell holds no more entities than one cluster's slots, so one cluster IS the cell and intra-cell
   * maintenance correctly switches itself off. A tightness of 0.9 there is not a defect, and the panel must not present it as one.
   */
  inSingleClusterBasin: boolean;
}

export function readTightness(row: SpatialTickTelemetry): TightnessReading {
  const { tightnessSamples: samples, extentRatio, packingBound } = row;
  return {
    samples,
    extentRatio,
    packingBound,
    toBound: samples > 0 && packingBound > 0 ? extentRatio / packingBound : 0,
    inSingleClusterBasin: samples > 0 && packingBound >= 0.999,
  };
}

// ── Reading 3: the "RepairUnitCount pinned at 1.00" detector ─────────────────────────────────────────────────────

export interface RepairPinReading {
  /** Ticks in the window that admitted at least one repair unit. */
  ticksWithRepair: number;
  /** Of those, ticks that admitted exactly one. */
  ticksPinnedAtOne: number;
  /** Ticks in the window that refused at least one unit for budget. */
  ticksWithRefusals: number;
  /**
   * True when EVERY tick that repaired admitted exactly one unit while some tick was refusing units — the signature of a budget the
   * planner cannot actually spend, where the single unit each tick is the safety valve firing rather than the budget working.
   * Needs a few samples before it means anything, so it stays false on a short window.
   */
  pinned: boolean;
}

/** Minimum repairing ticks before the pin detector is allowed to fire. Below this, "always exactly one" is a coincidence. */
export const REPAIR_PIN_MIN_SAMPLES = 5;

export function detectRepairPin(ticks: readonly TickData[], archetypeId: number): RepairPinReading {
  let ticksWithRepair = 0;
  let ticksPinnedAtOne = 0;
  let ticksWithRefusals = 0;

  for (const t of ticks) {
    const row = t.spatialByArchetype?.get(archetypeId);
    if (row === undefined) continue;
    if (row.repairUnits > 0) {
      ticksWithRepair++;
      if (row.repairUnits === 1) ticksPinnedAtOne++;
    }
    if (row.repairUnitsRefused > 0) ticksWithRefusals++;
  }

  return {
    ticksWithRepair,
    ticksPinnedAtOne,
    ticksWithRefusals,
    pinned: ticksWithRepair >= REPAIR_PIN_MIN_SAMPLES
      && ticksPinnedAtOne === ticksWithRepair
      && ticksWithRefusals > 0,
  };
}

// ── Cumulative-member differentiation ────────────────────────────────────────────────────────────────────────────

/**
 * A per-second rate for a per-tick counter, over the window.
 *
 * <b>This is the "two clocks" discipline made operational.</b> Every counter on `SpatialTickTelemetry` is per-tick and reset at the
 * top of every fence, so a panel polling at UI rate reads one arbitrary tick out of hundreds. Where the panel wants a trend rather
 * than an instant, it sums the window and divides by its span — it must never present one tick's value as a rate.
 *
 * Returns 0 when the window has no span or no samples.
 */
export function ratePerSecond(
  ticks: readonly TickData[], archetypeId: number, select: (row: SpatialTickTelemetry) => number,
): number {
  if (ticks.length === 0) return 0;

  let total = 0;
  for (const t of ticks) {
    const row = t.spatialByArchetype?.get(archetypeId);
    if (row !== undefined) total += select(row);
  }

  // The denominator is the WHOLE window, not the span of the ticks that happened to carry a row. Deriving it from the carrying ticks
  // divides by the wrong thing exactly when the archetype has been quiet: 100 migrations on a single 1 ms tick inside a 1 s window
  // is 100/s, and spanning only that tick reports ~100,000/s. A rate whose denominator shrinks as activity gets rarer inflates
  // precisely the readings a quiet world should make small.
  const spanUs = ticks[ticks.length - 1].endUs - ticks[0].startUs;
  return spanUs > 0 ? (total * 1_000_000) / spanUs : 0;
}
