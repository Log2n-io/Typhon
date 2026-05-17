import { describe, expect, it } from 'vitest';
import type { PostTickSummary } from '@/api/generated/model/postTickSummary';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { SystemTickSummary } from '@/api/generated/model/systemTickSummary';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import type { TrackDto } from '@/api/generated/model/trackDto';
import type { DerivedEdge } from '@/lib/dag/edgeDerivation';
import {
  computeAggregateCriticalPath,
  computeCriticalPathForTick,
  computeCriticalPathParticipation,
  computeWorkerOccupancy,
  dominantTickInRange,
} from '../criticalPath';

/**
 * Fixtures for the measured-traceback critical-path algorithm (`09-system-dag.md §5.2`).
 *
 * The path is a measured longest-path traceback: `terminus = argmax endUs`, each step back picks
 * the predecessor that finished last. Phases never affect the path — only `system.predecessors`
 * and measured timestamps do.
 */

function sys(name: string, index: number, phase: string, opts?: Partial<SystemDefinitionDto>): SystemDefinitionDto {
  return {
    index,
    name,
    type: 0,
    priority: 0,
    isParallel: false,
    tierFilter: 0x0F,
    predecessors: [],
    successors: [],
    phaseName: phase,
    isExclusivePhase: false,
    reads: [],
    readsFresh: [],
    readsSnapshot: [],
    additionalReads: [],
    writes: [],
    sideWrites: [],
    writesEvents: [],
    readsEvents: [],
    writesResources: [],
    readsResources: [],
    explicitAfter: [],
    explicitBefore: [],
    ...opts,
  } as SystemDefinitionDto;
}

function row(o: {
  tick: number;
  sysIdx: number;
  durationUs: number;
  startUs?: number;
  endUs?: number;
  readyUs?: number;
  totalCpuUs?: number;
  workersTouched?: number;
  chunksProcessed?: number;
  skipReasonCode?: number;
}): SystemTickSummary {
  const startUs = o.startUs ?? 0;
  return {
    tickNumber: o.tick,
    systemIndex: o.sysIdx,
    skipReasonCode: o.skipReasonCode ?? 0,
    flags: 0,
    startUs,
    endUs: o.endUs ?? startUs + o.durationUs,
    readyUs: o.readyUs ?? 0,
    durationUs: o.durationUs,
    entitiesProcessed: 0,
    workersTouched: o.workersTouched ?? 0,
    chunksProcessed: o.chunksProcessed ?? 0,
    totalCpuUs: o.totalCpuUs ?? 0,
  } as unknown as SystemTickSummary;
}

function tickSummary(tickNumber: number, durationUs: number, metronomeWaitUs = 0): TickSummaryDto {
  return {
    tickNumber, durationUs, metronomeWaitUs,
    eventCount: 0, maxSystemDurationUs: durationUs,
    activeSystemsBitmask: '0', overloadLevel: 0, tickMultiplier: 0,
    startUs: 0, metronomeIntentClass: 0, consecutiveOverrun: 0, consecutiveUnderrun: 0,
  } as unknown as TickSummaryDto;
}

function postTick(tick: number, opts: Partial<PostTickSummary> = {}): PostTickSummary {
  return {
    tickNumber: tick,
    writeTickFenceUs: 0, walFlushUs: 0, subscriptionOutputUs: 0,
    tierIndexRebuildUs: 0, dormancySweepUs: 0, tierBudgetUs: 0,
    ...opts,
  } as unknown as PostTickSummary;
}

function edge(source: string, target: string, kind: DerivedEdge['kind'] = 'fresh'): DerivedEdge {
  return { id: `e-${source}-${target}`, source, target, kind, via: ['t'], reason: '' };
}

const NO_PHASE = ['p1', 'p2'];

// ── computeCriticalPathParticipation ──────────────────────────────────────

describe('computeCriticalPathParticipation', () => {
  it('returns empty perSystem when no rows fall in range', () => {
    const r = computeCriticalPathParticipation({
      systems: [sys('A', 0, 'p1')], rows: [], edges: [], phases: NO_PHASE, range: null,
    });
    expect(r.perSystem.size).toBe(0);
    expect(r.totalTicks).toBe(0);
  });

  it('linear chain A→B→C — all three on the CP every tick', () => {
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1', { predecessors: [0] }),
      sys('C', 2, 'p1', { predecessors: [1] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 200, startUs: 100, endUs: 300 }),
      row({ tick: 1, sysIdx: 2, durationUs: 50, startUs: 300, endUs: 350 }),
      row({ tick: 2, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 2, sysIdx: 1, durationUs: 200, startUs: 100, endUs: 300 }),
      row({ tick: 2, sysIdx: 2, durationUs: 50, startUs: 300, endUs: 350 }),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges: [], phases: NO_PHASE, range: null });
    expect(r.totalTicks).toBe(2);
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(1);
    expect(r.perSystem.get('C')?.rate).toBe(1);
  });

  it('parallel branches — only the later-finishing branch is on the CP', () => {
    // A → {B, C} → D. B finishes at 110, C at 20 — D is gated by B.
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1', { predecessors: [0] }),
      sys('C', 2, 'p1', { predecessors: [0] }),
      sys('D', 3, 'p1', { predecessors: [1, 2] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 10, startUs: 0, endUs: 10 }),
      row({ tick: 1, sysIdx: 1, durationUs: 100, startUs: 10, endUs: 110 }),
      row({ tick: 1, sysIdx: 2, durationUs: 10, startUs: 10, endUs: 20 }),
      row({ tick: 1, sysIdx: 3, durationUs: 10, startUs: 110, endUs: 120 }),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges: [], phases: NO_PHASE, range: null });
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(1);
    expect(r.perSystem.get('D')?.rate).toBe(1);
    expect(r.perSystem.get('C')?.rate ?? 0).toBe(0);
  });

  it('gating branch flips per tick → fractional rates', () => {
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1', { predecessors: [0] }),
      sys('C', 2, 'p1', { predecessors: [0] }),
      sys('D', 3, 'p1', { predecessors: [1, 2] }),
    ];
    const rows = [
      // tick 1 — B finishes last
      row({ tick: 1, sysIdx: 0, durationUs: 10, startUs: 0, endUs: 10 }),
      row({ tick: 1, sysIdx: 1, durationUs: 100, startUs: 10, endUs: 110 }),
      row({ tick: 1, sysIdx: 2, durationUs: 10, startUs: 10, endUs: 20 }),
      row({ tick: 1, sysIdx: 3, durationUs: 10, startUs: 110, endUs: 120 }),
      // tick 2 — C finishes last
      row({ tick: 2, sysIdx: 0, durationUs: 10, startUs: 0, endUs: 10 }),
      row({ tick: 2, sysIdx: 1, durationUs: 10, startUs: 10, endUs: 20 }),
      row({ tick: 2, sysIdx: 2, durationUs: 100, startUs: 10, endUs: 110 }),
      row({ tick: 2, sysIdx: 3, durationUs: 10, startUs: 110, endUs: 120 }),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges: [], phases: NO_PHASE, range: null });
    expect(r.totalTicks).toBe(2);
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('D')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(0.5);
    expect(r.perSystem.get('C')?.rate).toBe(0.5);
  });

  it('cross-phase predecessors ARE used (no phase fence anymore)', () => {
    // A in p1, B in p2, B depends on A. The traceback must follow the cross-phase edge.
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p2', { predecessors: [0] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 30, startUs: 0, endUs: 30 }),
      row({ tick: 1, sysIdx: 1, durationUs: 70, startUs: 30, endUs: 100 }),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges: [], phases: NO_PHASE, range: null });
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(1);
  });

  it('skipped system (SkipReasonCode > 0) cannot be on the CP', () => {
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1', { predecessors: [0] })];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 0, skipReasonCode: 3 }),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges: [], phases: NO_PHASE, range: null });
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate ?? 0).toBe(0);
  });

  it('range filter — only ticks in [from, to] are counted', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [1, 2, 3, 4].map((t) => row({ tick: t, sysIdx: 0, durationUs: 10, startUs: 0, endUs: 10 }));
    const r = computeCriticalPathParticipation({ systems, rows, edges: [], phases: NO_PHASE, range: { from: 2, to: 3 } });
    expect(r.totalTicks).toBe(2);
    expect(r.perSystem.get('A')?.onPathTicks).toBe(2);
  });

  it('no DAG (no predecessors, no edges) → every system that ran counts as on-path', () => {
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1')];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 10, startUs: 0, endUs: 10 }),
      row({ tick: 1, sysIdx: 1, durationUs: 10, startUs: 0, endUs: 10 }),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges: [], phases: NO_PHASE, range: null });
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(1);
  });

  it('legacy `edges` are used as a predecessor fallback', () => {
    // systems carry no `predecessors`; edges supply A → B → C.
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1'), sys('C', 2, 'p1')];
    const edges = [edge('A', 'B'), edge('B', 'C')];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 50, startUs: 100, endUs: 150 }),
      row({ tick: 1, sysIdx: 2, durationUs: 50, startUs: 150, endUs: 200 }),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges, phases: NO_PHASE, range: null });
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(1);
    expect(r.perSystem.get('C')?.rate).toBe(1);
  });
});

// ── computeCriticalPathForTick ────────────────────────────────────────────

describe('computeCriticalPathForTick', () => {
  it('returns null when no rows match the tick', () => {
    const r = computeCriticalPathForTick({
      tickNumber: 99, systems: [sys('A', 0, 'p1')], rows: [], edges: [], phases: NO_PHASE,
      postTickRows: [], tickSummaryRow: null,
    });
    expect(r).toBeNull();
  });

  it('cpChain is in forward time order, root → terminus', () => {
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1', { predecessors: [0] }),
      sys('C', 2, 'p1', { predecessors: [1] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 50, startUs: 100, endUs: 150 }),
      row({ tick: 1, sysIdx: 2, durationUs: 200, startUs: 150, endUs: 350 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 350),
    });
    expect(r!.cpChain.map((b) => b.systemName)).toEqual(['A', 'B', 'C']);
    expect(r!.mode).toBe('critical-path');
  });

  it('terminus is the LAST-finishing system, not the longest-duration one', () => {
    // A finishes last (endUs 500) though B has the longer duration. C→D supplies a DAG so the
    // measured traceback runs rather than the no-DAG fallback.
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1'),
      sys('C', 2, 'p1'),
      sys('D', 3, 'p1', { predecessors: [2] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 20, startUs: 480, endUs: 500 }),
      row({ tick: 1, sysIdx: 1, durationUs: 400, startUs: 0, endUs: 400 }),
      row({ tick: 1, sysIdx: 2, durationUs: 10, startUs: 0, endUs: 10 }),
      row({ tick: 1, sysIdx: 3, durationUs: 10, startUs: 10, endUs: 20 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 500),
    });
    expect(r!.mode).toBe('critical-path');
    expect(r!.cpChain.map((b) => b.systemName)).toEqual(['A']);
  });

  it('overlapping phases — a later phase starts before an earlier one ends', () => {
    // p1: A [0, 5000]. p2: B [100, 600] — B is in a later phase but ran inside A's span.
    // C→D (p1) supplies the DAG; A finishes last so it is the terminus.
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p2'),
      sys('C', 2, 'p1'),
      sys('D', 3, 'p1', { predecessors: [2] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 5000, startUs: 0, endUs: 5000 }),
      row({ tick: 1, sysIdx: 1, durationUs: 500, startUs: 100, endUs: 600 }),
      row({ tick: 1, sysIdx: 2, durationUs: 10, startUs: 0, endUs: 10 }),
      row({ tick: 1, sysIdx: 3, durationUs: 10, startUs: 10, endUs: 20 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 5000),
    });
    expect(r!.cpChain.map((b) => b.systemName)).toEqual(['A']);
    const bBar = r!.nonCpBars.find((b) => b.systemName === 'B')!;
    expect(bBar.startUs).toBe(100);
    expect(bBar.endUs).toBe(600);
    // Phase spans reflect measured extents and overlap: p2 starts (100) before p1 ends (5000).
    const p1 = r!.phaseSpans.find((p) => p.name === 'p1')!;
    const p2 = r!.phaseSpans.find((p) => p.name === 'p2')!;
    expect(p2.startUs).toBeLessThan(p1.endUs);
  });

  it('worker-claim wait = startUs − readyUs when readyUs is observed', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [row({ tick: 1, sysIdx: 0, durationUs: 100, readyUs: 10, startUs: 30, endUs: 130 })];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 130),
    });
    expect(r!.cpChain[0].workerClaimWaitUs).toBe(20);
  });

  it('worker-claim wait suppressed when readyUs == 0 (old traces)', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [row({ tick: 1, sysIdx: 0, durationUs: 100, readyUs: 0, startUs: 30, endUs: 130 })];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 130),
    });
    expect(r!.cpChain[0].workerClaimWaitUs).toBe(0);
  });

  it('non-CP bars carry every other ran system, sorted by startUs', () => {
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1', { predecessors: [0] }),
      sys('X', 2, 'p1'),
      sys('Y', 3, 'p1'),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 100, startUs: 100, endUs: 200 }),
      row({ tick: 1, sysIdx: 2, durationUs: 10, startUs: 50, endUs: 60 }),
      row({ tick: 1, sysIdx: 3, durationUs: 10, startUs: 5, endUs: 15 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 200),
    });
    expect(r!.cpChain.map((b) => b.systemName)).toEqual(['A', 'B']);
    expect(r!.nonCpBars.map((b) => b.systemName)).toEqual(['Y', 'X']);
  });

  it('correctness invariant — Σ(cp durations) + Σ(cp claim waits) + post-tick = tick wall-clock', () => {
    // Chain A → B. A starts at the tick origin (no leading wait); B's readyUs is observed, so
    // the CP bars + worker-claim gaps tile the tick body exactly.
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1', { predecessors: [0] })];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, readyUs: 0, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 80, readyUs: 100, startUs: 120, endUs: 200 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE,
      postTickRows: [postTick(1, { walFlushUs: 50 })], tickSummaryRow: tickSummary(1, 250),
    });
    const cpDur = r!.cpChain.reduce((s, b) => s + b.durationUs, 0);
    const cpWait = r!.cpChain.reduce((s, b) => s + b.workerClaimWaitUs, 0);
    expect(cpDur + cpWait).toBe(r!.timeBounds.endUs); // 180 + 20 = 200
    expect(cpDur + cpWait + r!.postTick.totalUs).toBe(r!.totalUs); // 200 + 50 = 250
  });

  it('no-DAG fallback → execution-order, every system sorted by startUs', () => {
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1'), sys('C', 2, 'p1')];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 200, endUs: 300 }),
      row({ tick: 1, sysIdx: 1, durationUs: 50, startUs: 0, endUs: 50 }),
      row({ tick: 1, sysIdx: 2, durationUs: 30, startUs: 400, endUs: 430 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 430),
    });
    expect(r!.mode).toBe('execution-order');
    expect(r!.cpChain.map((b) => b.systemName)).toEqual(['B', 'A', 'C']);
    expect(r!.nonCpBars).toHaveLength(0);
  });

  it('skipped systems are excluded from the timeline', () => {
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1')];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 0, skipReasonCode: 2 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 100),
    });
    expect(r!.cpChain.map((b) => b.systemName)).toEqual(['A']);
    expect(r!.nonCpBars).toHaveLength(0);
  });

  it('parallel system → one bar carrying isParallel + worker count', () => {
    const systems = [sys('A', 0, 'p1', { isParallel: true })];
    const rows = [row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100, workersTouched: 16, chunksProcessed: 64 })];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 100),
    });
    expect(r!.cpChain).toHaveLength(1);
    expect(r!.cpChain[0].isParallel).toBe(true);
    expect(r!.cpChain[0].workersTouched).toBe(16);
  });

  it('post-tick block + metronome populate from their sources', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 })];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE,
      postTickRows: [postTick(1, { walFlushUs: 50, writeTickFenceUs: 10 })],
      tickSummaryRow: tickSummary(1, 100, /* metronomeWaitUs */ 250),
    });
    expect(r!.postTick.totalUs).toBe(60);
    expect(r!.metronomeWaitUs).toBe(250);
  });
});

// ── computeWorkerOccupancy ────────────────────────────────────────────────

describe('computeWorkerOccupancy', () => {
  function barsWith(rowsIn: Array<{ idx: number; start: number; end: number; cpu: number }>): Parameters<typeof computeWorkerOccupancy>[0] {
    const systems = rowsIn.map((r, i) => sys(`S${i}`, r.idx, 'p1'));
    const rows = rowsIn.map((r) => row({ tick: 1, sysIdx: r.idx, durationUs: r.end - r.start, startUs: r.start, endUs: r.end, totalCpuUs: r.cpu }));
    return computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [], tickSummaryRow: tickSummary(1, 100),
    })!;
  }

  it('returns null when no system carries totalCpuUs (pre-v13 cache)', () => {
    const bars = barsWith([{ idx: 0, start: 0, end: 100, cpu: 0 }]);
    expect(computeWorkerOccupancy(bars, 8)).toBeNull();
  });

  it('builds a step function and clamps to workerCount', () => {
    // One serial system [0,100] cpu 100 → concurrency 1. One 16-wide system [0,50] cpu 800 →
    // concurrency 16. Overlap [0,50] = 17, clamped to W=8.
    const bars = barsWith([
      { idx: 0, start: 0, end: 100, cpu: 100 },
      { idx: 1, start: 0, end: 50, cpu: 800 },
    ]);
    const occ = computeWorkerOccupancy(bars, 8)!;
    expect(occ.breakpoints).toEqual([0, 50, 100]);
    expect(occ.levels[0]).toBe(8);       // clamped
    expect(occ.levels[1]).toBeCloseTo(1, 5);
  });
});

// ── computeAggregateCriticalPath ──────────────────────────────────────────

describe('computeAggregateCriticalPath', () => {
  it('returns null when no ticks fall in range', () => {
    const r = computeAggregateCriticalPath({
      systems: [sys('A', 0, 'p1')], rows: [], edges: [], phases: NO_PHASE,
      postTickRows: [], tickSummaries: [], range: { from: 1, to: 10 },
    });
    expect(r).toBeNull();
  });

  it('produces mean-duration bars across the range with the aggregate flag set', () => {
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1', { predecessors: [0] })];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 200, startUs: 100, endUs: 300 }),
      row({ tick: 2, sysIdx: 0, durationUs: 300, startUs: 0, endUs: 300 }),
      row({ tick: 2, sysIdx: 1, durationUs: 400, startUs: 300, endUs: 700 }),
    ];
    const r = computeAggregateCriticalPath({
      systems, rows, edges: [], phases: NO_PHASE, postTickRows: [],
      tickSummaries: [tickSummary(1, 300), tickSummary(2, 700)], range: { from: 1, to: 2 },
    })!;
    expect(r.aggregate?.tickCount).toBe(2);
    expect(r.tickNumber).toBe(-1);
    const a = r.cpChain.find((b) => b.systemName === 'A')!;
    const b = r.cpChain.find((b) => b.systemName === 'B')!;
    expect(a.durationUs).toBe(200); // mean of 100, 300
    expect(b.durationUs).toBe(300); // mean of 200, 400
    expect(r.nonCpBars).toHaveLength(0);
  });
});

// ── Track-aware critical path (#354) ──────────────────────────────────────

/** Build a `TrackDto` with `dagIds` DAGs (each declares no phases — irrelevant to the walk). */
function track(name: string, orderIndex: number, dagIds: number[], tags: string[] = []): TrackDto {
  return {
    name,
    orderIndex,
    tags,
    dags: dagIds.map((id) => ({ id, name: `dag${id}`, phases: [] })),
  } as unknown as TrackDto;
}

describe('track-aware critical path', () => {
  it('per-track traceback — terminus is the track-wide last finisher; concurrent sibling DAGs are off-path', () => {
    // One track "Sim" with two concurrent DAGs: Physics A→B→C (dag 0), Audio D→E (dag 1).
    const systems = [
      sys('A', 0, 'p1', { dagId: 0 }),
      sys('B', 1, 'p1', { dagId: 0, predecessors: [0] }),
      sys('C', 2, 'p1', { dagId: 0, predecessors: [1] }),
      sys('D', 3, 'p1', { dagId: 1 }),
      sys('E', 4, 'p1', { dagId: 1, predecessors: [3] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 20, startUs: 0, endUs: 20 }),
      row({ tick: 1, sysIdx: 1, durationUs: 30, startUs: 20, endUs: 50 }),
      row({ tick: 1, sysIdx: 2, durationUs: 40, startUs: 50, endUs: 90 }),
      row({ tick: 1, sysIdx: 3, durationUs: 30, startUs: 0, endUs: 30 }),
      row({ tick: 1, sysIdx: 4, durationUs: 30, startUs: 30, endUs: 60 }),
    ];
    const tracks = [track('Sim', 0, [0, 1])];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE,
      postTickRows: [], tickSummaryRow: tickSummary(1, 90), tracks, trackScope: 'Sim',
    })!;
    // C finishes last (90) → the Physics DAG holds the CP; the Audio DAG ran concurrently, off-path.
    expect(r.cpChain.map((b) => b.systemName)).toEqual(['A', 'B', 'C']);
    expect(r.nonCpBars.map((b) => b.systemName).sort()).toEqual(['D', 'E']);
  });

  it('"All" scope concatenates per-track chains in track order across the barrier', () => {
    // Track "First" (dag 0): A→B. Track "Second" (dag 1): C→D. Barriered — Second starts after First.
    const systems = [
      sys('A', 0, 'p1', { dagId: 0 }),
      sys('B', 1, 'p1', { dagId: 0, predecessors: [0] }),
      sys('C', 2, 'p1', { dagId: 1 }),
      sys('D', 3, 'p1', { dagId: 1, predecessors: [2] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 100, startUs: 100, endUs: 200 }),
      row({ tick: 1, sysIdx: 2, durationUs: 60, startUs: 200, endUs: 260 }),
      row({ tick: 1, sysIdx: 3, durationUs: 140, startUs: 260, endUs: 400 }),
    ];
    const tracks = [track('First', 0, [0]), track('Second', 1, [1])];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE,
      postTickRows: [], tickSummaryRow: tickSummary(1, 400), tracks, trackScope: 'all',
    })!;
    expect(r.cpChain.map((b) => b.systemName)).toEqual(['A', 'B', 'C', 'D']);
    // Tracks band — one span per track, in order, at measured extents.
    expect(r.trackSpans.map((s) => s.name)).toEqual(['First', 'Second']);
    expect(r.trackSpans[0]).toMatchObject({ startUs: 0, endUs: 200 });
    expect(r.trackSpans[1]).toMatchObject({ startUs: 200, endUs: 400 });
  });

  it('engine-tagged tracks are excluded from "All" unless showEngineSystems is set', () => {
    const systems = [
      sys('A', 0, 'p1', { dagId: 0 }),
      sys('F', 1, 'p1', { dagId: 1 }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 100, startUs: 200, endUs: 300 }),
    ];
    const tracks = [track('Public', 0, [0]), track('Engine-Post', 1, [1], ['engine'])];
    const hidden = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [],
      tickSummaryRow: tickSummary(1, 300), tracks, trackScope: 'all', showEngineSystems: false,
    })!;
    expect(hidden.cpChain.map((b) => b.systemName)).toEqual(['A']);
    const shown = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [],
      tickSummaryRow: tickSummary(1, 300), tracks, trackScope: 'all', showEngineSystems: true,
    })!;
    expect(shown.cpChain.map((b) => b.systemName)).toEqual(['A', 'F']);
  });

  it('single-track scope shows only that track and emits no Tracks band', () => {
    const systems = [
      sys('A', 0, 'p1', { dagId: 0 }),
      sys('F', 1, 'p1', { dagId: 1 }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 100, startUs: 200, endUs: 300 }),
    ];
    const tracks = [track('Public', 0, [0]), track('Engine-Post', 1, [1], ['engine'])];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [],
      tickSummaryRow: tickSummary(1, 300), tracks, trackScope: 'Public',
    })!;
    expect(r.cpChain.map((b) => b.systemName)).toEqual(['A']);
    expect(r.nonCpBars).toHaveLength(0);
    expect(r.trackSpans).toHaveLength(0);
  });

  it('track-less topology falls back to the single global traceback', () => {
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1', { predecessors: [0] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 100, startUs: 100, endUs: 200 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: NO_PHASE, postTickRows: [],
      tickSummaryRow: tickSummary(1, 200), tracks: [], trackScope: 'all',
    })!;
    expect(r.cpChain.map((b) => b.systemName)).toEqual(['A', 'B']);
    expect(r.trackSpans).toHaveLength(0);
  });

  it('participation surfaces every track — not just the last DAG the global traceback reaches', () => {
    // Two barriered tracks. Without track-awareness the global terminus is in track 2 and the
    // DAG-local traceback never reaches track 1's A, B — that is the bug this rework fixes.
    const systems = [
      sys('A', 0, 'p1', { dagId: 0 }),
      sys('B', 1, 'p1', { dagId: 0, predecessors: [0] }),
      sys('C', 2, 'p1', { dagId: 1 }),
      sys('D', 3, 'p1', { dagId: 1, predecessors: [2] }),
    ];
    const rows = [
      row({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      row({ tick: 1, sysIdx: 1, durationUs: 100, startUs: 100, endUs: 200 }),
      row({ tick: 1, sysIdx: 2, durationUs: 60, startUs: 200, endUs: 260 }),
      row({ tick: 1, sysIdx: 3, durationUs: 140, startUs: 260, endUs: 400 }),
    ];
    const tracks = [track('First', 0, [0]), track('Second', 1, [1])];
    const trackAware = computeCriticalPathParticipation({
      systems, rows, edges: [], phases: NO_PHASE, range: null, tracks, trackScope: 'all',
    });
    expect(trackAware.perSystem.get('A')?.rate).toBe(1);
    expect(trackAware.perSystem.get('B')?.rate).toBe(1);
    expect(trackAware.perSystem.get('C')?.rate).toBe(1);
    expect(trackAware.perSystem.get('D')?.rate).toBe(1);
    // Track-less: the global traceback orphans track 1's systems.
    const trackLess = computeCriticalPathParticipation({
      systems, rows, edges: [], phases: NO_PHASE, range: null,
    });
    expect(trackLess.perSystem.get('A')?.rate ?? 0).toBe(0);
  });
});

// ── dominantTickInRange ───────────────────────────────────────────────────

describe('dominantTickInRange', () => {
  it('returns null on empty inputs', () => {
    expect(dominantTickInRange(null, { from: 1, to: 10 })).toBeNull();
    expect(dominantTickInRange([], { from: 1, to: 10 })).toBeNull();
    expect(dominantTickInRange([tickSummary(1, 100)], null)).toBeNull();
  });

  it('picks the longest-durationUs tick in range', () => {
    const ticks = [tickSummary(1, 100), tickSummary(2, 500), tickSummary(3, 200), tickSummary(4, 50)];
    expect(dominantTickInRange(ticks, { from: 1, to: 4 })).toBe(2);
  });

  it('respects the range — out-of-range ticks ignored even if longer', () => {
    const ticks = [tickSummary(1, 50), tickSummary(2, 9999), tickSummary(3, 100)];
    expect(dominantTickInRange(ticks, { from: 3, to: 4 })).toBe(3);
  });

  function ts(tickNumber: number, startUs: number, durationUs: number): TickSummaryDto {
    return {
      tickNumber, durationUs, startUs, metronomeWaitUs: 0,
      eventCount: 0, maxSystemDurationUs: durationUs,
      activeSystemsBitmask: '0', overloadLevel: 0, tickMultiplier: 0,
      metronomeIntentClass: 0, consecutiveOverrun: 0, consecutiveUnderrun: 0,
    } as unknown as TickSummaryDto;
  }

  it('falls back to the midpoint tick when the window is inside one tick', () => {
    const ticks = [ts(4, 500, 500), ts(5, 1000, 500), ts(6, 1500, 500)];
    expect(dominantTickInRange(ticks, null, { startUs: 1100, endUs: 1300 })).toBe(5);
  });

  it('strict path beats the midpoint fallback when both are valid', () => {
    const ticks = [ts(5, 1000, 500)];
    expect(dominantTickInRange(ticks, { from: 5, to: 5 }, { startUs: 1100, endUs: 1300 })).toBe(5);
  });

  it('returns null on a degenerate window', () => {
    expect(dominantTickInRange([ts(1, 0, 100)], null, { startUs: 50, endUs: 50 })).toBeNull();
  });
});

// ── Performance — #317 acceptance: 1024 ticks × 200 systems ───────────────

describe('performance', () => {
  it('1024 ticks × 200 systems CP participation under 750 ms (target 50 ms)', () => {
    const PHASE_COUNT = 5;
    const PHASE_NAMES = Array.from({ length: PHASE_COUNT }, (_, i) => `p${i}`);
    const SYSTEMS_PER_PHASE = 40;
    const SYSTEM_COUNT = PHASE_COUNT * SYSTEMS_PER_PHASE;
    const TICK_COUNT = 1024;

    const systems: SystemDefinitionDto[] = [];
    for (let p = 0; p < PHASE_COUNT; p++) {
      for (let i = 0; i < SYSTEMS_PER_PHASE; i++) {
        const idx = p * SYSTEMS_PER_PHASE + i;
        systems.push(sys(`s${idx}`, idx, PHASE_NAMES[p], { predecessors: i > 0 ? [idx - 1] : [] }));
      }
    }

    const rows: SystemTickSummary[] = [];
    for (let t = 1; t <= TICK_COUNT; t++) {
      for (let s = 0; s < SYSTEM_COUNT; s++) {
        const dur = (((t * 31 + s) * 17) % 100) + 1;
        // Stagger starts so endUs varies per system per tick — exercises the traceback.
        rows.push(row({ tick: t, sysIdx: s, durationUs: dur, startUs: s * 10, endUs: s * 10 + dur }));
      }
    }

    const start = performance.now();
    const result = computeCriticalPathParticipation({
      systems, rows, edges: [], phases: PHASE_NAMES, range: { from: 1, to: TICK_COUNT },
    });
    const elapsedMs = performance.now() - start;

    expect(result.totalTicks).toBe(TICK_COUNT);
    expect(elapsedMs).toBeLessThan(750);
    // A linear chain through the terminus's phase is always on the CP → the map is non-empty.
    expect(result.perSystem.size).toBeGreaterThan(0);
  });
});
