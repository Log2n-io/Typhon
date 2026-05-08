import { describe, expect, it } from 'vitest';
import type { SystemTickSummary } from '@/api/generated/model/systemTickSummary';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import { computePhaseUtilization, computeRangeUtilization } from '../tickUtilization';

function tick(tickNumber: number, durationUs: number, overrides?: Partial<TickSummaryDto>): TickSummaryDto {
  return {
    tickNumber,
    startUs: 0,
    durationUs,
    eventCount: 0,
    maxSystemDurationUs: 0,
    activeSystemsBitmask: null,
    overloadLevel: 0,
    tickMultiplier: 1,
    metronomeWaitUs: 0,
    metronomeIntentClass: 0,
    consecutiveOverrun: 0,
    consecutiveUnderrun: 0,
    ...overrides,
  } as TickSummaryDto;
}

/**
 * Build a synthetic SystemTickSummary row. Default `totalCpuUs = durationUs` (single-threaded
 * system: cpu and wall-clock match). For parallel systems pass `totalCpuUs` explicitly to model
 * the multi-worker case (16-worker × 50µs avg chunk = 800µs cpu vs ~50-100µs wall).
 */
function row(tickNumber: number, systemIndex: number, durationUs: number, totalCpuUs?: number): SystemTickSummary {
  return {
    tickNumber,
    systemIndex,
    skipReasonCode: 0,
    flags: 0,
    startUs: 0,
    endUs: durationUs,
    readyUs: 0,
    durationUs,
    entitiesProcessed: 0,
    workersTouched: 1,
    chunksProcessed: 1,
    totalCpuUs: totalCpuUs ?? durationUs,
  } as SystemTickSummary;
}

describe('computeRangeUtilization', () => {
  it('returns null when worker count is missing', () => {
    expect(computeRangeUtilization({
      workerCount: null,
      tickSummaries: [tick(1, 1000)],
      systemTickSummaries: [row(1, 0, 500)],
      range: { from: 1, to: 1 },
    })).toBeNull();
  });

  it('returns null when range is null', () => {
    expect(computeRangeUtilization({
      workerCount: 4,
      tickSummaries: [tick(1, 1000)],
      systemTickSummaries: [row(1, 0, 500)],
      range: null,
    })).toBeNull();
  });

  it('returns null when no ticks fall in range', () => {
    expect(computeRangeUtilization({
      workerCount: 4,
      tickSummaries: [tick(1, 1000)],
      systemTickSummaries: [row(1, 0, 500)],
      range: { from: 5, to: 10 },
    })).toBeNull();
  });

  it('computes capacity, wait, and utilization for a single tick', () => {
    // 4 workers × 1000µs wall = 4000µs capacity. Sum of system durations = 1500µs.
    // Wait = 2500µs. Utilization = 1500/4000 = 0.375.
    const result = computeRangeUtilization({
      workerCount: 4,
      tickSummaries: [tick(1, 1000)],
      systemTickSummaries: [row(1, 0, 500), row(1, 1, 500), row(1, 2, 500)],
      range: { from: 1, to: 1 },
    });
    expect(result).not.toBeNull();
    expect(result!.perTick).toHaveLength(1);
    const t = result!.perTick[0];
    expect(t.workUs).toBe(1500);
    expect(t.wallTimeUs).toBe(1000);
    expect(t.capacityUs).toBe(4000);
    expect(t.waitUs).toBe(2500);
    expect(t.utilization).toBeCloseTo(0.375, 5);
    expect(result!.meanUtilization).toBeCloseTo(0.375, 5);
    expect(result!.meanWaitFraction).toBeCloseTo(0.625, 5);
  });

  it('weights mean by capacity, not by tick count', () => {
    // Tick 1: 1 worker-µs total, util 100%. Tick 2: 1000 worker-µs total, util 0%.
    // Arithmetic mean of per-tick utilization = 50%.
    // Work-weighted mean = 1 / 1001 ≈ 0.1%, which is what users actually feel.
    const result = computeRangeUtilization({
      workerCount: 1,
      tickSummaries: [tick(1, 1), tick(2, 1000)],
      systemTickSummaries: [row(1, 0, 1)],
      range: { from: 1, to: 2 },
    });
    expect(result).not.toBeNull();
    expect(result!.meanUtilization).toBeCloseTo(1 / 1001, 5);
  });

  it('excludes skipped systems (durationUs <= 0)', () => {
    const result = computeRangeUtilization({
      workerCount: 2,
      tickSummaries: [tick(1, 1000)],
      systemTickSummaries: [row(1, 0, 500), row(1, 1, 0), row(1, 2, -10)],
      range: { from: 1, to: 1 },
    });
    expect(result!.perTick[0].workUs).toBe(500);
  });

  it('saturates wait at zero when work exceeds capacity (defensive)', () => {
    // Pathological: 1 worker × 100µs = 100µs capacity, but reported work 200µs (e.g. timing artifact).
    const result = computeRangeUtilization({
      workerCount: 1,
      tickSummaries: [tick(1, 100)],
      systemTickSummaries: [row(1, 0, 200)],
      range: { from: 1, to: 1 },
    });
    expect(result!.perTick[0].waitUs).toBe(0);
    expect(result!.perTick[0].utilization).toBe(1);
  });

  it('skips ticks with zero or negative wallTime', () => {
    const result = computeRangeUtilization({
      workerCount: 2,
      tickSummaries: [tick(1, 1000), tick(2, 0)],
      systemTickSummaries: [row(1, 0, 500), row(2, 0, 500)],
      range: { from: 1, to: 2 },
    });
    expect(result!.perTick).toHaveLength(1);
    expect(result!.perTick[0].tickNumber).toBe(1);
  });

  it('sorts perTick by tickNumber ascending', () => {
    const result = computeRangeUtilization({
      workerCount: 2,
      tickSummaries: [tick(3, 100), tick(1, 100), tick(2, 100)],
      systemTickSummaries: [row(1, 0, 50), row(2, 0, 50), row(3, 0, 50)],
      range: { from: 1, to: 3 },
    });
    expect(result!.perTick.map((t) => t.tickNumber)).toEqual([1, 2, 3]);
  });

  it('filters rows outside the range', () => {
    const result = computeRangeUtilization({
      workerCount: 2,
      tickSummaries: [tick(1, 100), tick(2, 100), tick(3, 100)],
      systemTickSummaries: [row(1, 0, 50), row(2, 0, 80), row(3, 0, 50)],
      range: { from: 2, to: 2 },
    });
    expect(result!.perTick).toHaveLength(1);
    expect(result!.perTick[0].workUs).toBe(80);
  });

  it('uses totalCpuUs (not durationUs) as work — fixes parallel-system under-counting', () => {
    // Anthill-like scenario: 16 workers × 7.5ms wall = 120,000 µs capacity.
    // One parallel system: durationUs = 690 (wall-clock) but totalCpuUs = 5,700 (sum of 16 chunks).
    // Without the fix (using durationUs), util = 690/120000 = 0.6%. With the fix, util = 4.8%.
    // Tests the chunker-v13 cpu-vs-wall-clock distinction is honoured.
    const result = computeRangeUtilization({
      workerCount: 16,
      tickSummaries: [tick(1, 7500)],
      systemTickSummaries: [row(1, 0, /* durationUs */ 690, /* totalCpuUs */ 5700)],
      range: { from: 1, to: 1 },
    });
    expect(result).not.toBeNull();
    expect(result!.perTick[0].workUs).toBe(5700);
    expect(result!.perTick[0].utilization).toBeCloseTo(5700 / 120000, 5);
  });

  it('falls back to zero work when totalCpuUs is missing (old cache, pre-v13)', () => {
    // Old caches don't carry totalCpuUs; the row helper sets it to durationUs by default, so to
    // simulate "missing" we explicitly pass zero. The pill should still render — it just sees no
    // work — rather than crashing or NaN-ing.
    const result = computeRangeUtilization({
      workerCount: 4,
      tickSummaries: [tick(1, 1000)],
      systemTickSummaries: [row(1, 0, /* durationUs */ 500, /* totalCpuUs */ 0)],
      range: { from: 1, to: 1 },
    });
    // No tick has positive work → no perTick row → returns null.
    expect(result).toBeNull();
  });

  it('reports meanWaitUsPerTick as arithmetic mean of waits', () => {
    // 3 ticks: wait 100, 200, 300 → mean 200.
    const result = computeRangeUtilization({
      workerCount: 2,
      tickSummaries: [tick(1, 100), tick(2, 200), tick(3, 300)],
      // 1 system per tick; capacity = 2*wall, work = wall → wait = wall.
      systemTickSummaries: [row(1, 0, 100), row(2, 0, 200), row(3, 0, 300)],
      range: { from: 1, to: 3 },
    });
    expect(result!.meanWaitUsPerTick).toBeCloseTo(200, 3);
  });
});

describe('computePhaseUtilization', () => {
  it('returns null when worker count is missing', () => {
    expect(computePhaseUtilization({ workerCount: null, phaseWorkUs: 100, phaseWallTimeUs: 200 })).toBeNull();
  });

  it('returns null when wall time is zero', () => {
    expect(computePhaseUtilization({ workerCount: 4, phaseWorkUs: 100, phaseWallTimeUs: 0 })).toBeNull();
  });

  it('computes wait and utilization for a populated phase', () => {
    // 4 workers × 1000µs wall = 4000µs capacity. work = 1500 → wait = 2500, util = 0.375.
    const u = computePhaseUtilization({ workerCount: 4, phaseWorkUs: 1500, phaseWallTimeUs: 1000 })!;
    expect(u.capacityUs).toBe(4000);
    expect(u.waitUs).toBe(2500);
    expect(u.utilization).toBeCloseTo(0.375, 5);
  });

  it('saturates work-exceeds-capacity case', () => {
    const u = computePhaseUtilization({ workerCount: 1, phaseWorkUs: 999, phaseWallTimeUs: 100 })!;
    expect(u.waitUs).toBe(0);
    expect(u.utilization).toBe(1);
  });
});
