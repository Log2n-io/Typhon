import type { SpatialRealmShape, SpatialTickTelemetry, TickData } from '@/libs/profiler/model/traceModel';

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

// ── Per-realm shape (#WB-05, kind 67) ────────────────────────────────────────────────────────────────────────────

/** How far a realm's query reach may run past its own cell size before the cell-level broadphase stops pruning usefully. */
export const REALM_REACH_WARN_CELLS = 1;

export interface RealmShapeRow extends SpatialRealmShape {
  /**
   * `clusterReach / cellSize`. Reach is meaningless on its own — 180 m is nothing in a 1 km realm and catastrophic in a
   * 64 m one — so the ratio, not the raw value, is what a row is judged on.
   */
  reachInCells: number;
  /** True when a query for this realm reaches past its neighbouring cell, i.e. the cell broadphase has stopped pruning. */
  reachBlown: boolean;
}

export interface RealmShapeReading {
  /** The tick these rows came from, or null when no tick in the window carried any. */
  tickNumber: number | null;
  /** The realms that sent a row, ascending by id. Runnable realms only — see `presentRealms` for what is missing. */
  rows: RealmShapeRow[];
  /** Realms this archetype has state in, from the archetype record of the SAME tick. */
  presentRealms: number;
  /** How many of those were runnable, i.e. how many rows the engine sent. */
  runnableRealms: number;
  /**
   * The budget the engine actually ENFORCES for this archetype, in ms, from the archetype row of the same tick.
   *
   * Every row's own `budgetConfiguredMs` is that realm's DECLARATION. Maintenance is budgeted per archetype (Realms D-6)
   * and the one budget spent comes from realm 0's grid, so a realm declaring 4 ms runs under realm 0's whatever it says.
   * The panel shows both because the gap is the thing worth seeing — a realm configured differently from what runs.
   */
  enforcedBudgetMs: number;
  /** True when at least one row DECLARES a budget the engine will not enforce for it. */
  someRealmDeclaresADifferentBudget: boolean;
}

/**
 * The most recent tick in `ticks` that carried per-realm rows for `archetypeId`, as a sorted table.
 *
 * <b>The census comes from the same tick, not from the latest archetype record.</b> Pairing this tick's rows with
 * another tick's counts would let the panel say "3 of 1 188" over four rows — the sort of off-by-one that reads as a
 * bug in the engine rather than in the panel.
 */
export function readRealmShapes(ticks: readonly TickData[], archetypeId: number): RealmShapeReading {
  for (let i = ticks.length - 1; i >= 0; i--) {
    const byRealm = ticks[i].spatialByRealm;
    if (byRealm === undefined || byRealm.size === 0) continue;

    const rows: RealmShapeRow[] = [];
    for (const shape of byRealm.values()) {
      if (shape.archetypeId !== archetypeId) continue;
      const reachInCells = shape.cellSize > 0 ? shape.clusterReach / shape.cellSize : 0;
      rows.push({ ...shape, reachInCells, reachBlown: reachInCells > REALM_REACH_WARN_CELLS });
    }
    if (rows.length === 0) continue;

    rows.sort((a, b) => a.realmId - b.realmId);
    const archetypeRow = ticks[i].spatialByArchetype?.get(archetypeId);
    const enforcedBudgetMs = archetypeRow?.budgetConfiguredMs ?? 0;
    return {
      tickNumber: ticks[i].tickNumber,
      rows,
      presentRealms: archetypeRow?.presentRealms ?? rows.length,
      runnableRealms: archetypeRow?.runnableRealms ?? rows.length,
      enforcedBudgetMs,
      // Compared at f16 precision, which is what the wire carries: a difference below that is the codec, not a configuration.
      someRealmDeclaresADifferentBudget: rows.some((r) => Math.abs(r.budgetConfiguredMs - enforcedBudgetMs) > 0.01),
    };
  }

  return { tickNumber: null, rows: [], presentRealms: 0, runnableRealms: 0, enforcedBudgetMs: 0, someRealmDeclaresADifferentBudget: false };
}

/** `RealmRunState` on the wire. Named here because the Workbench has no other reason to know the engine's enum. */
export const REALM_RUN_STATE_NAMES: Readonly<Record<number, string>> = {
  0: 'Dormant',
  1: 'Simulated',
  2: 'Active',
  3: 'Closing',
};

export function realmRunStateName(state: number): string {
  return REALM_RUN_STATE_NAMES[state] ?? `state ${state}`;
}

// ── The maintenance controller (#944 / #941's appended fields) ───────────────────────────────────────────────────

export interface QueryEfficiencyReading {
  /** Ticks in the window that carried a record for this archetype. Zero means the ratio below is not a reading. */
  samples: number;
  /** Clusters the range queries opened, summed over the window. */
  clustersOpened: number;
  /** Entities in those clusters, summed. */
  candidates: number;
  /** Matches returned, summed. */
  hits: number;
  /**
   * Summed candidates over summed hits — the cost of a match over the window. Zero when nothing was hit.
   *
   * <b>Summed, never averaged.</b> The field's own instruction, and the reason is Simpson's paradox in miniature: a tick that opens
   * one cluster for one hit and a tick that opens 400 for 200 average to a ratio of 1.5, while the work that actually happened cost
   * 401/201 ≈ 2.0. Averaging per-tick ratios weights a cheap tick and an expensive one equally; the engine's controller sums, and a
   * panel that does otherwise disagrees with the number the engine is steering on.
   */
  candidatesPerHit: number;
}

export function readQueryEfficiency(ticks: readonly TickData[], archetypeId: number): QueryEfficiencyReading {
  let samples = 0;
  let clustersOpened = 0;
  let candidates = 0;
  let hits = 0;

  for (const t of ticks) {
    const row = t.spatialByArchetype?.get(archetypeId);
    if (row === undefined) continue;
    samples++;
    clustersOpened += row.queryClustersOpened;
    candidates += row.queryCandidates;
    hits += row.queryHits;
  }

  return { samples, clustersOpened, candidates, hits, candidatesPerHit: hits > 0 ? candidates / hits : 0 };
}

/** Bit 0 of `controllerFlags` — the queries hit enough this tick for the efficiency signal to mean anything. */
export const CONTROLLER_FLAG_SIGNAL = 0x01;
/** Bit 1 of `controllerFlags` — this tick re-based the best, accepting what the whole budget could not recover. */
export const CONTROLLER_FLAG_REBASED = 0x02;

export interface ControllerReading {
  /**
   * Whether the controller is running at all. `efficiencyTolerance === 0` means OFF — not "perfectly tolerant" — so every figure
   * below is meaningless and the panel must say off rather than draw zeros that look like measurements.
   */
  active: boolean;
  /** The configured `QueryEfficiencyTolerance` itself — the verdict cites it, so it travels with the reading. */
  tolerance: number;
  configuredMs: number;
  grantedMs: number;
  /** Granted over configured. 1 is the whole budget. Zero when nothing is configured. */
  grantedShare: number;
  smoothed: number;
  best: number;
  /** `smoothed / best` — how far above its own best the archetype's queries currently cost. 1 is at the best. */
  distanceFromBest: number;
  /** Whether that distance is inside the configured tolerance, i.e. whether the grant should be whole. */
  withinTolerance: boolean;
  ticksAtWholeBudget: number;
  rebases: number;
  /** Bit 0: the queries hit enough to steer by. Without it the controller is holding, not deciding. */
  hasSignal: boolean;
  /** Bit 1: this tick re-based. Rare and meaningful — it is the controller giving up on recovering the old best. */
  rebasedThisTick: boolean;
}

export function readController(row: SpatialTickTelemetry): ControllerReading {
  const { budgetConfiguredMs: configuredMs, budgetGrantedMs: grantedMs, efficiencyTolerance: tolerance } = row;
  const distanceFromBest = row.candidatesPerHitBest > 0 ? row.candidatesPerHitSmoothed / row.candidatesPerHitBest : 0;
  return {
    active: tolerance > 0,
    tolerance,
    configuredMs,
    grantedMs,
    grantedShare: configuredMs > 0 ? grantedMs / configuredMs : 0,
    smoothed: row.candidatesPerHitSmoothed,
    best: row.candidatesPerHitBest,
    distanceFromBest,
    // At or below (1 + tolerance) the grant is whole. Evaluated here rather than read off the grant so the panel can say when the two
    // DISAGREE — a grant that is not whole while the distance says it should be is a controller bug, and it would otherwise be
    // invisible behind a share that merely looks plausible.
    withinTolerance: distanceFromBest > 0 && distanceFromBest <= 1 + tolerance,
    ticksAtWholeBudget: row.ticksAtWholeBudget,
    rebases: row.efficiencyRebases,
    hasSignal: (row.controllerFlags & CONTROLLER_FLAG_SIGNAL) !== 0,
    rebasedThisTick: (row.controllerFlags & CONTROLLER_FLAG_REBASED) !== 0,
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
/**
 * The growth of a CUMULATIVE counter across the window — first record to last.
 *
 * `efficiencyRebases` and `repairQueueEvicted` are cumulative since the archetype's cluster state was created, deliberately, so that a
 * dropped record or a late attach loses none of them. That makes their instantaneous value a reading about the whole process lifetime
 * rather than about now: an engine up for an hour shows a large number and a panel that renders it as a per-tick figure says the
 * spatial layer is thrashing when it is idle. Differentiating is the only way to read them as activity.
 *
 * Returns 0 when fewer than two records carry the archetype — one sample cannot show growth, and returning the single value would
 * report a lifetime total as if it had just happened.
 */
export function windowGrowth(
  ticks: readonly TickData[], archetypeId: number, select: (row: SpatialTickTelemetry) => number,
): number {
  let first: number | null = null;
  let last = 0;
  let samples = 0;

  for (const t of ticks) {
    const row = t.spatialByArchetype?.get(archetypeId);
    if (row === undefined) continue;
    samples++;
    const value = select(row);
    if (first === null) first = value;
    last = value;
  }

  return samples >= 2 && first !== null ? Math.max(0, last - first) : 0;
}

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
