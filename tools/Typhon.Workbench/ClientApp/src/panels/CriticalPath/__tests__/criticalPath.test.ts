import { describe, expect, it } from 'vitest';
import type { PostTickSummary } from '@/api/generated/model/postTickSummary';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { SystemTickSummary } from '@/api/generated/model/systemTickSummary';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import {
  computeCriticalPathForTick,
  computeCriticalPathParticipation,
  dominantTickInRange,
} from '../criticalPath';
import type { DerivedEdge } from '../../SystemDag/edgeDerivation';

/**
 * Synthetic fixtures for the client-side critical-path algorithm. Each test fabricates a tiny
 * topology + a per-tick row stream and asserts which systems land on the CP.
 *
 * The algorithm is per `09-system-dag.md §9.3`: client-side, no engine cost. v1 ignores wait
 * classification (phase-fence vs worker-claim) and post-tick serial — these tests exercise the
 * core "max-pathDurationTo with traceback" walk over a phase-ordered DAG.
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

function row(tick: number, sysIdx: number, durationUs: number): SystemTickSummary {
  return {
    tickNumber: tick,
    systemIndex: sysIdx,
    skipReasonCode: 0,
    flags: 0,
    startUs: 0,
    endUs: durationUs,
    readyUs: 0,
    durationUs,
    entitiesProcessed: 0,
    workersTouched: 0,
    chunksProcessed: 0,
  } as unknown as SystemTickSummary;
}

function edge(source: string, target: string, kind: DerivedEdge['kind'] = 'fresh'): DerivedEdge {
  return { id: `e-${source}-${target}`, source, target, kind, via: ['t'], reason: '' };
}

describe('computeCriticalPathParticipation', () => {
  it('returns empty perSystem when no rows fall in range', () => {
    const r = computeCriticalPathParticipation({
      systems: [sys('A', 0, 'p1')],
      rows: [],
      edges: [],
      phases: ['p1'],
      range: null,
    });
    expect(r.perSystem.size).toBe(0);
    expect(r.totalTicks).toBe(0);
  });

  it('linear chain: all three systems on CP every tick → rate 1.0 each', () => {
    // p1: A → B → C (all in same phase, durations 100/200/50). CP = A→B→C every tick.
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1'), sys('C', 2, 'p1')];
    const edges = [edge('A', 'B'), edge('B', 'C')];
    const rows = [
      row(1, 0, 100), row(1, 1, 200), row(1, 2, 50),
      row(2, 0, 100), row(2, 1, 200), row(2, 2, 50),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges, phases: ['p1'], range: null });
    expect(r.totalTicks).toBe(2);
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(1);
    expect(r.perSystem.get('C')?.rate).toBe(1);
  });

  it('parallel branches: only the heavier branch is on the CP', () => {
    // p1:  A ─→ B ─→ D
    //        ╲    ╱
    //         ╲  ╱
    //          C  (lighter branch, both into D)
    // Durations: A=10, B=100, C=10, D=10. Heavy path A→B→D dominates; C never on CP.
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1'),
      sys('C', 2, 'p1'),
      sys('D', 3, 'p1'),
    ];
    const edges = [edge('A', 'B'), edge('A', 'C'), edge('B', 'D'), edge('C', 'D')];
    const rows = [
      row(1, 0, 10),
      row(1, 1, 100),
      row(1, 2, 10),
      row(1, 3, 10),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges, phases: ['p1'], range: null });
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(1);
    expect(r.perSystem.get('D')?.rate).toBe(1);
    expect(r.perSystem.get('C')?.rate ?? 0).toBe(0);
  });

  it('branch dominance flips per tick → fractional rates reflect both', () => {
    // Same topology as above. Tick 1: B heavy. Tick 2: C heavy. Each on CP for one of two ticks.
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1'),
      sys('C', 2, 'p1'),
      sys('D', 3, 'p1'),
    ];
    const edges = [edge('A', 'B'), edge('A', 'C'), edge('B', 'D'), edge('C', 'D')];
    const rows = [
      // tick 1 — B heavy
      row(1, 0, 10), row(1, 1, 100), row(1, 2, 10), row(1, 3, 10),
      // tick 2 — C heavy
      row(2, 0, 10), row(2, 1, 10), row(2, 2, 100), row(2, 3, 10),
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges, phases: ['p1'], range: null });
    expect(r.totalTicks).toBe(2);
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('D')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(0.5);
    expect(r.perSystem.get('C')?.rate).toBe(0.5);
  });

  it('phase fence: phase-2 system inherits phase-1 max as its base path duration', () => {
    // p1: A=100, B=10 (parallel, no edges). p2: C=20 (no in-phase preds → phase-fence).
    // CP terminates at C; traceback should include A (phase-fence dominator) → C.
    const systems = [
      sys('A', 0, 'p1'),
      sys('B', 1, 'p1'),
      sys('C', 2, 'p2'),
    ];
    const rows = [row(1, 0, 100), row(1, 1, 10), row(1, 2, 20)];
    const r = computeCriticalPathParticipation({ systems, rows, edges: [], phases: ['p1', 'p2'], range: null });
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('C')?.rate).toBe(1);
    // B never on CP.
    expect(r.perSystem.get('B')?.rate ?? 0).toBe(0);
  });

  it('skipped system (no row) cannot be on the CP', () => {
    // If B has no row this tick (skipped — e.g. NoDirty), it must not appear on the CP.
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1')];
    const edges = [edge('A', 'B')];
    const rows = [
      row(1, 0, 100),
      // no row for B at tick 1 — it skipped
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges, phases: ['p1'], range: null });
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate ?? 0).toBe(0);
  });

  it('range filter: only ticks in [from, to] are counted', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [row(1, 0, 10), row(2, 0, 10), row(3, 0, 10), row(4, 0, 10)];
    const r = computeCriticalPathParticipation({
      systems,
      rows,
      edges: [],
      phases: ['p1'],
      range: { from: 2, to: 3 },
    });
    expect(r.totalTicks).toBe(2);
    expect(r.perSystem.get('A')?.onPathTicks).toBe(2);
    expect(r.perSystem.get('A')?.rate).toBe(1);
  });

  it('rate is bounded to [0, 1]', () => {
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1')];
    const rows = [
      row(1, 0, 100), row(1, 1, 10),
      row(2, 0, 10), row(2, 1, 100),
      row(3, 0, 50), row(3, 1, 50), // tie → traceback picks one consistently; just make sure rate is [0,1]
    ];
    const r = computeCriticalPathParticipation({ systems, rows, edges: [], phases: ['p1'], range: null });
    for (const stat of r.perSystem.values()) {
      expect(stat.rate).toBeGreaterThanOrEqual(0);
      expect(stat.rate).toBeLessThanOrEqual(1);
    }
  });

  it('cross-phase edges are ignored (phase fence is the contract)', () => {
    // Cross-phase edges shouldn't add to predecessor lists; phase fence handles ordering.
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p2')];
    // A → B is cross-phase — algorithm must ignore the explicit edge and rely on the fence.
    const edges = [edge('A', 'B')];
    const rows = [row(1, 0, 30), row(1, 1, 70)];
    const r = computeCriticalPathParticipation({ systems, rows, edges, phases: ['p1', 'p2'], range: null });
    expect(r.perSystem.get('A')?.rate).toBe(1);
    expect(r.perSystem.get('B')?.rate).toBe(1);
  });
});

// ── per-tick tape data ────────────────────────────────────────────────────

/** Richer row helper for tape tests — needs startUs, endUs, readyUs explicitly set. */
function detailedRow(opts: {
  tick: number; sysIdx: number; durationUs: number;
  startUs?: number; endUs?: number; readyUs?: number;
  workersTouched?: number; chunksProcessed?: number;
}): SystemTickSummary {
  return {
    tickNumber: opts.tick,
    systemIndex: opts.sysIdx,
    skipReasonCode: 0,
    flags: 0,
    startUs: opts.startUs ?? 0,
    endUs: opts.endUs ?? opts.durationUs,
    readyUs: opts.readyUs ?? 0,
    durationUs: opts.durationUs,
    entitiesProcessed: 0,
    workersTouched: opts.workersTouched ?? 0,
    chunksProcessed: opts.chunksProcessed ?? 0,
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

describe('computeCriticalPathForTick', () => {
  it('returns null when no rows match the tick', () => {
    const r = computeCriticalPathForTick({
      tickNumber: 99,
      systems: [sys('A', 0, 'p1')],
      rows: [],
      edges: [],
      phases: ['p1'],
      postTickRows: [],
      tickSummaryRow: null,
    });
    expect(r).toBeNull();
  });

  it('orderedPath flows in forward execution order (root → terminus)', () => {
    // Linear A → B → C; durations 100/50/200. CP = A→B→C; bars should appear A first, C last.
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1'), sys('C', 2, 'p1')];
    const edges = [edge('A', 'B'), edge('B', 'C')];
    const rows = [
      detailedRow({ tick: 1, sysIdx: 0, durationUs: 100 }),
      detailedRow({ tick: 1, sysIdx: 1, durationUs: 50 }),
      detailedRow({ tick: 1, sysIdx: 2, durationUs: 200 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges, phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 350),
    });
    expect(r).not.toBeNull();
    expect(r!.phases).toHaveLength(1);
    expect(r!.phases[0].bars.map((b) => b.systemName)).toEqual(['A', 'B', 'C']);
  });

  it('worker-claim wait = startUs - readyUs when readyUs is captured', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [detailedRow({ tick: 1, sysIdx: 0, durationUs: 100, readyUs: 10, startUs: 30, endUs: 130 })];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 130),
    });
    expect(r!.phases[0].bars[0].workerClaimWaitUs).toBe(20);
  });

  it('worker-claim wait suppressed when readyUs == 0 (pre-#311 traces)', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [detailedRow({ tick: 1, sysIdx: 0, durationUs: 100, readyUs: 0, startUs: 30, endUs: 130 })];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 130),
    });
    expect(r!.phases[0].bars[0].workerClaimWaitUs).toBe(0);
  });

  it('phase-fence wait = phase max EndUs minus CP-tail EndUs', () => {
    // A on the CP, finishes at endUs=100. B is a non-CP straggler, finishes at endUs=180.
    // phase-fence wait should be 80, attributed to B.
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1')];
    const rows = [
      detailedRow({ tick: 1, sysIdx: 0, durationUs: 100, endUs: 100 }),
      detailedRow({ tick: 1, sysIdx: 1, durationUs: 180, endUs: 180 }), // not on CP edge-wise
    ];
    // Edges: empty — A and B are independent. CP terminus = B (longer). Hmm.
    // Tweak: make A larger so it dominates. A=200, B=180.
    rows[0] = detailedRow({ tick: 1, sysIdx: 0, durationUs: 200, endUs: 200 });
    rows[1] = detailedRow({ tick: 1, sysIdx: 1, durationUs: 180, endUs: 180 });
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 200),
    });
    // A is the CP terminus (endUs=200), so phase max == CP-tail end → no wait reported.
    expect(r!.phases[0].phaseFenceWaitUs).toBe(0);

    // Now flip: B has bigger endUs (straggler) but A is on the CP via edge ordering.
    const edges2 = [edge('A', 'B')]; // forces A → B, B becomes CP terminus
    rows[0] = detailedRow({ tick: 1, sysIdx: 0, durationUs: 100, endUs: 100 });
    rows[1] = detailedRow({ tick: 1, sysIdx: 1, durationUs: 80, endUs: 180 });
    const r2 = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: edges2, phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 180),
    });
    // B IS on CP (terminus); B is also the phase straggler — no wait because the CP holds it.
    expect(r2!.phases[0].phaseFenceWaitUs).toBe(0);

    // Real fence wait scenario: A and B in same phase, A is on CP (no edge to B), B is a
    // straggler that finishes later — but A's path duration dominates. We use a self-loop on A
    // to force critical-path mode (edges.length > 0) without otherwise perturbing the walk; a
    // self-loop has no effect on path semantics because pathDurationTo[A] isn't yet set when
    // computing A's own bestPredPathTo.
    const rows3 = [
      detailedRow({ tick: 1, sysIdx: 0, durationUs: 200, endUs: 200 }),
      detailedRow({ tick: 1, sysIdx: 1, durationUs: 50, endUs: 220 }), // started later, finishes after A
    ];
    const r3 = computeCriticalPathForTick({
      tickNumber: 1, systems, rows: rows3, edges: [edge('A', 'A')], phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 220),
    });
    expect(r3!.phases[0].phaseFenceWaitUs).toBe(20);
    expect(r3!.phases[0].phaseFenceStraggler).toBe('B');
  });

  it('post-tick block populates from PostTickSummary', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [detailedRow({ tick: 1, sysIdx: 0, durationUs: 100 })];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: ['p1'],
      postTickRows: [postTick(1, { walFlushUs: 50, writeTickFenceUs: 10 })],
      tickSummaryRow: tickSummary(1, 100),
    });
    expect(r!.postTick.walFlushUs).toBe(50);
    expect(r!.postTick.writeTickFenceUs).toBe(10);
    expect(r!.postTick.totalUs).toBe(60);
  });

  it('metronomeWaitUs propagated from tickSummaryRow', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [detailedRow({ tick: 1, sysIdx: 0, durationUs: 100 })];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 100, /* metronomeWaitUs */ 250),
    });
    expect(r!.metronomeWaitUs).toBe(250);
  });

  it('fallback: no edges → all running systems on the path, sorted by startUs', () => {
    // Three systems, no edges (e.g. trace lacks RFC 07 declarations). Without the dependency
    // graph the dependency-aware walker would emit only the longest-duration system; the
    // fallback should emit all three in startUs order.
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1'), sys('C', 2, 'p1')];
    const rows = [
      detailedRow({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 200, endUs: 300 }),  // 2nd
      detailedRow({ tick: 1, sysIdx: 1, durationUs: 50, startUs: 0, endUs: 50 }),       // 1st
      detailedRow({ tick: 1, sysIdx: 2, durationUs: 30, startUs: 400, endUs: 430 }),    // 3rd
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 430),
    });
    expect(r).not.toBeNull();
    expect(r!.mode).toBe('execution-order');
    expect(r!.phases[0].bars.map((b) => b.systemName)).toEqual(['B', 'A', 'C']);
  });

  it('fallback: skipped systems (durationUs == 0) excluded from path', () => {
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1')];
    const rows = [
      detailedRow({ tick: 1, sysIdx: 0, durationUs: 100, startUs: 0, endUs: 100 }),
      detailedRow({ tick: 1, sysIdx: 1, durationUs: 0, startUs: 0, endUs: 0 }),  // skipped
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 100),
    });
    expect(r!.phases[0].bars.map((b) => b.systemName)).toEqual(['A']);
  });

  it('mode reports critical-path when edges exist', () => {
    const systems = [sys('A', 0, 'p1'), sys('B', 1, 'p1')];
    const rows = [
      detailedRow({ tick: 1, sysIdx: 0, durationUs: 100 }),
      detailedRow({ tick: 1, sysIdx: 1, durationUs: 50 }),
    ];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows,
      edges: [edge('A', 'B')],
      phases: ['p1'],
      postTickRows: [],
      tickSummaryRow: tickSummary(1, 150),
    });
    expect(r!.mode).toBe('critical-path');
  });

  it('chunkDispatchWaitUs is structurally present and zero in v1', () => {
    const systems = [sys('A', 0, 'p1')];
    const rows = [detailedRow({ tick: 1, sysIdx: 0, durationUs: 100, workersTouched: 4, chunksProcessed: 16 })];
    const r = computeCriticalPathForTick({
      tickNumber: 1, systems, rows, edges: [], phases: ['p1'],
      postTickRows: [], tickSummaryRow: tickSummary(1, 100),
    });
    expect(r!.phases[0].bars[0].chunkDispatchWaitUs).toBe(0);
    expect(r!.phases[0].bars[0].workersTouched).toBe(4);
    expect(r!.phases[0].bars[0].chunksProcessed).toBe(16);
  });
});

describe('dominantTickInRange', () => {
  it('returns null on empty inputs', () => {
    expect(dominantTickInRange(null, { from: 1, to: 10 })).toBeNull();
    expect(dominantTickInRange([], { from: 1, to: 10 })).toBeNull();
    expect(dominantTickInRange([tickSummary(1, 100)], null)).toBeNull();
  });

  it('picks the longest-durationUs tick in range', () => {
    const ticks = [
      tickSummary(1, 100),
      tickSummary(2, 500),  // ← winner
      tickSummary(3, 200),
      tickSummary(4, 50),
    ];
    expect(dominantTickInRange(ticks, { from: 1, to: 4 })).toBe(2);
  });

  it('respects the range — out-of-range ticks ignored even if longer', () => {
    const ticks = [
      tickSummary(1, 50),
      tickSummary(2, 9999), // out of range
      tickSummary(3, 100),  // ← winner inside range
    ];
    expect(dominantTickInRange(ticks, { from: 3, to: 4 })).toBe(3);
  });
});

// ── Performance — #317 acceptance: 1024 ticks × 200 systems < 50 ms ───────

describe('performance', () => {
  /**
   * Synthetic worst-case-ish workload: 200 systems split evenly across 5 phases (40 systems per
   * phase), each phase forming a linear chain (40 intra-phase edges per phase = 200 edges total),
   * 1024 ticks of rows. Mirrors the #317 acceptance threshold from `09-system-dag.md §11 Phase 3`.
   *
   * The walker is O(systems × ticks) per `computeCriticalPathParticipation`; on a typical dev box
   * 1024 × 200 lands well under 50 ms. We assert against 750 ms (15× the dev-box target) to
   * absorb CI variance — GitHub Actions runners are roughly 4× slower than a dev workstation
   * before factoring in scheduling jitter and GC pauses, so the initial 200 ms ceiling tripped
   * on CI by ~5 ms. The 50 ms target is the design budget on a typical workstation; this
   * threshold catches gross algorithmic regressions (any change that makes the walker O(n²) or
   * adds an allocation-heavy hot path) without flaking on slow CI runners.
   */
  it('1024 ticks × 200 systems CP participation under 750 ms (target 50 ms)', () => {
    const PHASE_COUNT = 5;
    const PHASE_NAMES = Array.from({ length: PHASE_COUNT }, (_, i) => `p${i}`);
    const SYSTEMS_PER_PHASE = 40;
    const SYSTEM_COUNT = PHASE_COUNT * SYSTEMS_PER_PHASE;
    const TICK_COUNT = 1024;

    // Build systems with predecessors[] populated (engine-built path the walker prefers).
    const systems: SystemDefinitionDto[] = [];
    for (let p = 0; p < PHASE_COUNT; p++) {
      for (let i = 0; i < SYSTEMS_PER_PHASE; i++) {
        const idx = p * SYSTEMS_PER_PHASE + i;
        const preds = i > 0 ? [idx - 1] : [];
        systems.push(sys(`s${idx}`, idx, PHASE_NAMES[p], { predecessors: preds }));
      }
    }

    // Per-tick rows for every system. Vary durations slightly per tick so different ticks pick
    // different terminus systems — exercises the traceback path on every tick.
    const rows: SystemTickSummary[] = [];
    for (let t = 1; t <= TICK_COUNT; t++) {
      for (let s = 0; s < SYSTEM_COUNT; s++) {
        // Duration: hash of (tick, system) so results aren't degenerate (all-equal would let
        // first-system always win the tie). 1..100 µs.
        const dur = (((t * 31 + s) * 17) % 100) + 1;
        rows.push(row(t, s, dur));
      }
    }

    const start = performance.now();
    const result = computeCriticalPathParticipation({
      systems, rows, edges: [], phases: PHASE_NAMES, range: { from: 1, to: TICK_COUNT },
    });
    const elapsedMs = performance.now() - start;

    expect(result.totalTicks).toBe(TICK_COUNT);
    expect(elapsedMs).toBeLessThan(750);
    // Sanity: every CP across the range should hit at least one system per phase (linear chain
    // forces it). So the total perSystem map should have entries for some systems in every phase.
    expect(result.perSystem.size).toBeGreaterThan(0);
  });
});
