import { describe, expect, it } from 'vitest';
import type { SpatialRealmShape, SpatialTickTelemetry, TickData } from '@/libs/profiler/model/traceModel';
import { realmArchetypeKey } from '@/libs/profiler/model/traceModel';
import {
  archetypeIdsIn,
  checkDrifterIdentity,
  detectRepairPin,
  latestSampleFor,
  ratePerSecond,
  readController,
  readQueryEfficiency,
  readRealmShapes,
  readTightness,
  realmRunStateName,
  REPAIR_PIN_MIN_SAMPLES,
  windowGrowth,
} from '../spatialReadings';

const ARCH = 3;

function row(over: Partial<SpatialTickTelemetry> = {}): SpatialTickTelemetry {
  return {
    archetypeId: ARCH,
    migrations: 0, hysteresisAbsorbed: 0, migrationCpuMs: 0,
    driftersDetected: 0, relocationsAdmitted: 0, relocationsThrottled: 0, relocationsSuperseded: 0,
    driftersUnplaced: 0, driftersUnplacedNoCandidate: 0, driftersSpilled: 0, pinsRejected: 0, crossingsQueued: 0,
    repairUnits: 0, repairUnitsRefused: 0, repairQueueDepth: 0,
    budgetUsedMs: 0,
    tightnessSamples: 0, extentRatio: 0, packingBound: 0,
    activeClusters: 0, cellTreePromotions: 0, cellTreeDemotions: 0,
    queryClustersOpened: 0, queryCandidates: 0, queryHits: 0,
    budgetConfiguredMs: 0, budgetGrantedMs: 0, efficiencyTolerance: 0,
    candidatesPerHitSmoothed: 0, candidatesPerHitBest: 0, ticksAtWholeBudget: 0,
    controllerFlags: 0, efficiencyRebases: 0,
    repairCellsCooling: 0, repairValveFires: 0, repairedEntities: 0, repairQueueEvicted: 0,
    measuredNsPerEntity: 0, driftTargetBoost: 0,
    presentRealms: 0, runnableRealms: 0, ratesRealmsTouched: 0, ratesRealmsEmitted: 0,
    ...over,
  };
}

/** Only the four fields these pure functions read. The rest of TickData is irrelevant here and building it would obscure the test. */
function tick(tickNumber: number, rows: Array<SpatialTickTelemetry> | null, startUs = tickNumber * 1000, endUs = startUs + 1000): TickData {
  return {
    tickNumber, startUs, endUs,
    spatialByArchetype: rows === null ? undefined : new Map(rows.map((r) => [r.archetypeId, r])),
  } as unknown as TickData;
}

describe('latestSampleFor', () => {
  it('skips ticks that carried no spatial record rather than reading their absence as zero', () => {
    // The newest tick is silent — a fence that did no spatial work emits nothing. Falling back to it would render zeros
    // as if they were this tick's measurement, which is the exact failure the sample-count discipline exists to prevent.
    const ticks = [tick(1, [row({ migrations: 7 })]), tick(2, null)];
    const s = latestSampleFor(ticks, ARCH);
    expect(s?.tickNumber).toBe(1);
    expect(s?.row.migrations).toBe(7);
  });

  it('returns null when the archetype never appears', () => {
    expect(latestSampleFor([tick(1, [row()])], 99)).toBeNull();
  });
});

describe('archetypeIdsIn', () => {
  it('unions across ticks, ascending', () => {
    const ticks = [
      tick(1, [row({ archetypeId: 5 })]),
      tick(2, [row({ archetypeId: 2 }), row({ archetypeId: 5 })]),
      tick(3, null),
    ];
    expect(archetypeIdsIn(ticks)).toEqual([2, 5]);
  });
});

describe('checkDrifterIdentity', () => {
  it('pairs a tick\'s detection with the NEXT tick\'s outcomes', () => {
    // TH-02: drift detection runs in AabbRefresh, which is after Migrate, so the relocations a tick detects are decided by
    // the following tick's Prep. Pairing a tick with its own outcomes compares two different populations.
    const ticks = [
      tick(1, [row({ driftersDetected: 10 })]),
      tick(2, [row({ relocationsAdmitted: 4, relocationsThrottled: 3, relocationsSuperseded: 1, driftersUnplaced: 2 })]),
    ];
    const check = checkDrifterIdentity(ticks, ARCH);
    expect(check).not.toBeNull();
    expect(check?.detectedTick).toBe(1);
    expect(check?.outcomeTick).toBe(2);
    expect(check?.accountedFor).toBe(10);
    expect(check?.balanced).toBe(true);
  });

  it('reports an imbalance rather than hiding it', () => {
    const ticks = [
      tick(1, [row({ driftersDetected: 10 })]),
      tick(2, [row({ relocationsAdmitted: 4 })]),
    ];
    expect(checkDrifterIdentity(ticks, ARCH)?.balanced).toBe(false);
  });

  it('refuses a non-consecutive pair — a gap in the window is not a one-tick lag', () => {
    const ticks = [
      tick(1, [row({ driftersDetected: 10 })]),
      tick(9, [row({ relocationsAdmitted: 10 })]),
    ];
    expect(checkDrifterIdentity(ticks, ARCH)).toBeNull();
  });

  it('returns null rather than a balanced check over nothing', () => {
    expect(checkDrifterIdentity([tick(1, [row()])], ARCH)).toBeNull();
  });
});

describe('readTightness', () => {
  it('divides the measured extent by the bound, not by the cell', () => {
    const r = readTightness(row({ tightnessSamples: 12, extentRatio: 0.9, packingBound: 0.5 }));
    expect(r.toBound).toBeCloseTo(1.8, 6);
    expect(r.inSingleClusterBasin).toBe(false);
  });

  it('reports zero — and no basin claim — when nothing was sampled', () => {
    const r = readTightness(row({ tightnessSamples: 0, extentRatio: 0, packingBound: 0 }));
    expect(r.toBound).toBe(0);
    expect(r.inSingleClusterBasin).toBe(false);
  });

  it('flags the single-cluster basin, where a 0.9 tightness is correct rather than a defect', () => {
    // A cell holding no more entities than one cluster's slots has a bound of 1: one cluster IS the cell, and intra-cell
    // maintenance switching itself off there is the design working, not a miss.
    const r = readTightness(row({ tightnessSamples: 4, extentRatio: 0.9, packingBound: 1 }));
    expect(r.inSingleClusterBasin).toBe(true);
    expect(r.toBound).toBeCloseTo(0.9, 6);
  });
});

describe('detectRepairPin', () => {
  it('fires when every repairing tick admitted exactly one unit while units were being refused', () => {
    const ticks = Array.from({ length: REPAIR_PIN_MIN_SAMPLES }, (_, i) =>
      tick(i + 1, [row({ repairUnits: 1, repairUnitsRefused: 2 })]));
    const r = detectRepairPin(ticks, ARCH);
    expect(r.ticksWithRepair).toBe(REPAIR_PIN_MIN_SAMPLES);
    expect(r.pinned).toBe(true);
  });

  it('stays quiet when the planner sometimes admits more than one', () => {
    const ticks = Array.from({ length: REPAIR_PIN_MIN_SAMPLES }, (_, i) =>
      tick(i + 1, [row({ repairUnits: i === 0 ? 3 : 1, repairUnitsRefused: 2 })]));
    expect(detectRepairPin(ticks, ARCH).pinned).toBe(false);
  });

  it('stays quiet when nothing is being refused — one unit a tick can simply be all the work there was', () => {
    const ticks = Array.from({ length: REPAIR_PIN_MIN_SAMPLES }, (_, i) => tick(i + 1, [row({ repairUnits: 1 })]));
    expect(detectRepairPin(ticks, ARCH).pinned).toBe(false);
  });

  it('needs a few samples before it will claim a pattern', () => {
    const ticks = Array.from({ length: REPAIR_PIN_MIN_SAMPLES - 1 }, (_, i) =>
      tick(i + 1, [row({ repairUnits: 1, repairUnitsRefused: 1 })]));
    expect(detectRepairPin(ticks, ARCH).pinned).toBe(false);
  });
});

describe('ratePerSecond', () => {
  it('sums the window and divides by its span rather than showing one tick as a rate', () => {
    // Three ticks of 1 ms each, 10 migrations apiece → 30 over 3 ms → 10 000/s.
    const ticks = [1, 2, 3].map((n) => tick(n, [row({ migrations: 10 })], (n - 1) * 1000, n * 1000));
    expect(ratePerSecond(ticks, ARCH, (r) => r.migrations)).toBeCloseTo(10_000, 3);
  });

  it('is zero for a window with no samples', () => {
    expect(ratePerSecond([tick(1, null)], ARCH, (r) => r.migrations)).toBe(0);
  });

  it('divides by the whole window, not by the span of the ticks that carried a row', () => {
    // The settled-world case, and the one that was wrong: 100 migrations on ONE 1 ms tick inside a 1 s window is 100/s.
    // Spanning only the carrying tick makes the denominator 1 ms and reports ~100 000/s — a rate that grows as the
    // archetype gets quieter, which is precisely backwards.
    const ticks = [
      tick(1, [row({ migrations: 100 })], 0, 1000),
      ...Array.from({ length: 9 }, (_, i) => tick(i + 2, null, (i + 1) * 100_000, ((i + 1) * 100_000) + 1000)),
    ];
    ticks.push(tick(11, null, 900_000, 1_000_000));

    expect(ratePerSecond(ticks, ARCH, (r) => r.migrations)).toBeCloseTo(100, 3);
  });

  it('is zero for an empty window', () => {
    expect(ratePerSecond([], ARCH, (r) => r.migrations)).toBe(0);
  });
});

// ── #944: the maintenance controller's readings ───────────────────────────────────────────────────────────────────

describe('readQueryEfficiency', () => {
  it('divides SUMMED candidates by SUMMED hits, which is not the mean of the per-tick ratios', () => {
    // The engine's own instruction on the field: "Sum across records for a window, never average." This is why. One
    // cheap tick and one expensive one:
    const ticks = [
      tick(1, [row({ queryCandidates: 1, queryHits: 1, queryClustersOpened: 1 })]),
      tick(2, [row({ queryCandidates: 400, queryHits: 200, queryClustersOpened: 40 })]),
    ];

    const reading = readQueryEfficiency(ticks, ARCH);
    // Summed: 401 candidates bought 201 hits.
    expect(reading.candidatesPerHit).toBeCloseTo(401 / 201, 6);
    // Averaging the per-tick ratios gives (1.0 + 2.0) / 2 = 1.5, which weights a single-hit tick as heavily as one that
    // did 200x the work — and disagrees with the number the engine steers on.
    expect(reading.candidatesPerHit).not.toBeCloseTo(1.5, 2);
    expect(reading.clustersOpened).toBe(41);
    expect(reading.samples).toBe(2);
  });

  it('reports zero cost rather than a division by zero when the window hit nothing', () => {
    const reading = readQueryEfficiency([tick(1, [row({ queryCandidates: 900, queryHits: 0 })])], ARCH);
    expect(reading.candidates).toBe(900);
    expect(reading.candidatesPerHit).toBe(0);
  });

  it('counts only ticks that carried the archetype, so a silent tick is not a zero-cost sample', () => {
    const reading = readQueryEfficiency([tick(1, [row({ queryHits: 10, queryCandidates: 20 })]), tick(2, [])], ARCH);
    expect(reading.samples).toBe(1);
    expect(reading.candidatesPerHit).toBeCloseTo(2, 6);
  });
});

describe('readController', () => {
  it('reads a zero tolerance as OFF, not as infinitely tolerant', () => {
    // The distinction the whole block hangs on: with the controller off every figure beside it is meaningless, and a
    // panel that renders them as measurements invents a controller that is not running.
    const reading = readController(row({ efficiencyTolerance: 0, budgetConfiguredMs: 8, budgetGrantedMs: 8 }));
    expect(reading.active).toBe(false);
  });

  it('derives the granted share and the distance from best, and judges the two against the tolerance', () => {
    const reading = readController(row({
      efficiencyTolerance: 0.25,
      budgetConfiguredMs: 8,
      budgetGrantedMs: 2,
      candidatesPerHitSmoothed: 2.5,
      candidatesPerHitBest: 2.0,
      controllerFlags: 0x01,
    }));

    expect(reading.active).toBe(true);
    expect(reading.grantedShare).toBeCloseTo(0.25, 6);
    expect(reading.distanceFromBest).toBeCloseTo(1.25, 6);
    // 1.25 is exactly at (1 + 0.25) — the boundary is inclusive, so the grant SHOULD be whole. It is a quarter, which
    // is the disagreement the reading exists to surface rather than smooth over.
    expect(reading.withinTolerance).toBe(true);
    expect(reading.hasSignal).toBe(true);
    expect(reading.rebasedThisTick).toBe(false);
  });

  it('decodes the two flag bits independently', () => {
    expect(readController(row({ controllerFlags: 0x02 })).hasSignal).toBe(false);
    expect(readController(row({ controllerFlags: 0x02 })).rebasedThisTick).toBe(true);
    expect(readController(row({ controllerFlags: 0x03 })).hasSignal).toBe(true);
  });

  it('reports no distance when there is no best yet, rather than dividing by zero', () => {
    const reading = readController(row({ efficiencyTolerance: 0.25, candidatesPerHitSmoothed: 3, candidatesPerHitBest: 0 }));
    expect(reading.distanceFromBest).toBe(0);
    expect(reading.withinTolerance).toBe(false);
  });
});

describe('windowGrowth', () => {
  it('differentiates a cumulative counter instead of reporting its lifetime total', () => {
    // repairQueueEvicted is cumulative since the cluster state was created. An engine up for an hour carries a large
    // value that says nothing about now.
    const ticks = [
      tick(1, [row({ repairQueueEvicted: 1_000_000 })]),
      tick(2, [row({ repairQueueEvicted: 1_000_004 })]),
    ];
    expect(windowGrowth(ticks, ARCH, (r) => r.repairQueueEvicted)).toBe(4);
  });

  it('returns zero on a single sample, because one reading cannot show growth', () => {
    expect(windowGrowth([tick(1, [row({ repairQueueEvicted: 1_000_000 })])], ARCH, (r) => r.repairQueueEvicted)).toBe(0);
  });

  it('clamps a counter that went backwards to zero — a reset is not negative activity', () => {
    const ticks = [tick(1, [row({ efficiencyRebases: 9 })]), tick(2, [row({ efficiencyRebases: 0 })])];
    expect(windowGrowth(ticks, ARCH, (r) => r.efficiencyRebases)).toBe(0);
  });
});

// ── #WB-05: the per-realm shape table ─────────────────────────────────────────────────────────────────────────────

function shape(over: Partial<SpatialRealmShape> = {}): SpatialRealmShape {
  return {
    realmId: 0, archetypeId: ARCH, runState: 2, divisor: 1,
    cellSize: 1024, cellCount: 256, gridDepth: 1, clusters: 31,
    clusterReach: 12.4, escapedClusters: 0, promotedCells: 0, blockedCells: 0,
    budgetConfiguredMs: 8, efficiencyTolerance: 0.25,
    ...over,
  };
}

function tickWithRealms(tickNumber: number, shapes: SpatialRealmShape[], archetypeRow?: SpatialTickTelemetry): TickData {
  return {
    tickNumber,
    startUs: tickNumber * 1000,
    endUs: tickNumber * 1000 + 1000,
    spatialByArchetype: archetypeRow === undefined ? undefined : new Map([[archetypeRow.archetypeId, archetypeRow]]),
    spatialByRealm: new Map(shapes.map((sh) => [realmArchetypeKey(sh.realmId, sh.archetypeId), sh])),
  } as unknown as TickData;
}

describe('readRealmShapes', () => {
  it('judges reach against the realm own cell size, not as an absolute', () => {
    // The whole reason the ratio exists: the same 180 m reach is healthy on a planet and fatal in a dungeon.
    const reading = readRealmShapes([tickWithRealms(4, [
      shape({ realmId: 0, cellSize: 1024, clusterReach: 180.9 }),
      shape({ realmId: 7, cellSize: 64, clusterReach: 180.9 }),
    ])], ARCH);

    expect(reading.rows).toHaveLength(2);
    expect(reading.rows[0].reachInCells).toBeCloseTo(180.9 / 1024, 6);
    expect(reading.rows[0].reachBlown).toBe(false);
    expect(reading.rows[1].reachInCells).toBeCloseTo(180.9 / 64, 6);
    expect(reading.rows[1].reachBlown).toBe(true);
  });

  it('takes the census from the SAME tick as the rows', () => {
    // Pairing this tick's rows with a later archetype record would let the panel print "3 of 1 188" over four rows.
    const older = tickWithRealms(1, [shape({ realmId: 0 })], row({ presentRealms: 1188, runnableRealms: 1 }));
    const newer = { ...tickWithRealms(2, []), spatialByRealm: undefined, spatialByArchetype: new Map([[ARCH, row({ presentRealms: 9, runnableRealms: 9 })]]) } as unknown as TickData;

    const reading = readRealmShapes([older, newer], ARCH);
    expect(reading.tickNumber).toBe(1);
    expect(reading.presentRealms).toBe(1188);
    expect(reading.runnableRealms).toBe(1);
  });

  it('skips rows belonging to another archetype rather than mixing them in', () => {
    const reading = readRealmShapes([tickWithRealms(1, [
      shape({ realmId: 0, archetypeId: ARCH }),
      shape({ realmId: 0, archetypeId: ARCH + 1 }),
    ])], ARCH);
    expect(reading.rows).toHaveLength(1);
    expect(reading.rows[0].archetypeId).toBe(ARCH);
  });

  it('reports no tick when the window carried no realm rows, rather than an empty table at tick 0', () => {
    const reading = readRealmShapes([tick(1, [row()])], ARCH);
    expect(reading.tickNumber).toBeNull();
    expect(reading.rows).toEqual([]);
  });

  it('separates the budget a realm DECLARES from the one the engine enforces', () => {
    // Maintenance is budgeted per archetype from realm 0's grid, so a realm declaring 4 ms runs under whatever realm 0
    // declares. Presenting a row's own value as the ceiling a grant was measured against would compare the grant to a
    // number nothing used.
    const reading = readRealmShapes([tickWithRealms(9, [
      shape({ realmId: 0, budgetConfiguredMs: 8 }),
      shape({ realmId: 7, budgetConfiguredMs: 4 }),
    ], row({ presentRealms: 2, runnableRealms: 2, budgetConfiguredMs: 8 }))], ARCH);

    expect(reading.enforcedBudgetMs).toBe(8);
    expect(reading.someRealmDeclaresADifferentBudget).toBe(true);
    expect(reading.rows[1].budgetConfiguredMs).toBe(4);
  });

  it('does not cry difference over half-precision noise', () => {
    // The wire carries f16, so a value that round-trips to 7.996 is the same configuration as 8 — flagging it would train
    // a reader to ignore the flag.
    const reading = readRealmShapes([tickWithRealms(9, [
      shape({ realmId: 0, budgetConfiguredMs: 7.996 }),
    ], row({ presentRealms: 1, runnableRealms: 1, budgetConfiguredMs: 8 }))], ARCH);

    expect(reading.someRealmDeclaresADifferentBudget).toBe(false);
  });

  it('names the run states the engine defines, and labels an unknown one rather than dropping it', () => {
    expect(realmRunStateName(0)).toBe('Dormant');
    expect(realmRunStateName(2)).toBe('Active');
    expect(realmRunStateName(9)).toBe('state 9');
  });
});
