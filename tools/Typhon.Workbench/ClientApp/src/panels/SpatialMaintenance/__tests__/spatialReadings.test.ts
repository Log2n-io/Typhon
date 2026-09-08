import { describe, expect, it } from 'vitest';
import type { SpatialTickTelemetry, TickData } from '@/libs/profiler/model/traceModel';
import {
  archetypeIdsIn,
  checkDrifterIdentity,
  detectRepairPin,
  latestSampleFor,
  ratePerSecond,
  readTightness,
  REPAIR_PIN_MIN_SAMPLES,
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
});
