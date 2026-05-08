import { describe, expect, it } from 'vitest';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { SystemTickSummary } from '@/api/generated/model/systemTickSummary';
import type { DerivedEdge } from '../edgeDerivation';
import { computeGatingAnalysis } from '../gatingAnalysis';

// Test fixtures — minimal SystemDefinitionDto-shaped objects. Cast through `unknown` because the
// generated DTO uses `(number | string)[]` for index lists which TS can't infer cleanly here.
function sys(s: { name: string; index: number; predecessors?: number[] }): SystemDefinitionDto {
  return {
    name: s.name,
    index: s.index,
    predecessors: s.predecessors ?? [],
    successors: [],
  } as unknown as SystemDefinitionDto;
}

function row(r: {
  systemIndex: number;
  tickNumber: number;
  readyUs?: number;
  startUs?: number;
  endUs?: number;
}): SystemTickSummary {
  return r as unknown as SystemTickSummary;
}

function edge(source: string, target: string, kind: DerivedEdge['kind'], via: string[] = []): DerivedEdge {
  return {
    id: `${kind}-${source}-${target}`,
    source,
    target,
    kind,
    via,
    reason: '',
  };
}

describe('computeGatingAnalysis', () => {
  it('identifies the slowest predecessor as the gater', () => {
    // System B has two predecessors A1 (fast, ends at 100µs) and A2 (slow, ends at 250µs).
    // B's readyUs = 250µs. A2 should be flagged as the gater 100% of the time.
    const systems = [
      sys({ name: 'A1', index: 0 }),
      sys({ name: 'A2', index: 1 }),
      sys({ name: 'B', index: 2, predecessors: [0, 1] }),
    ];
    const rows = [
      row({ systemIndex: 0, tickNumber: 1, startUs: 0, endUs: 100 }),
      row({ systemIndex: 1, tickNumber: 1, startUs: 50, endUs: 250 }),
      row({ systemIndex: 2, tickNumber: 1, readyUs: 250, startUs: 260, endUs: 350 }),
    ];
    const result = computeGatingAnalysis({ systems, rows, edges: [], range: null });
    const info = result.get('B')!;
    expect(info).toBeDefined();
    expect(info.gaters[0].predecessorName).toBe('A2');
    expect(info.gaters[0].ticksGated).toBe(1);
    expect(info.gaters[0].ticksObserved).toBe(1);
    // A1 is in the gaters list too but never gated.
    const a1 = info.gaters.find((g) => g.predecessorName === 'A1');
    expect(a1?.ticksGated).toBe(0);
    // Mean wait gap = 260 - 250 = 10µs (the dispatch wait beyond ready).
    expect(info.meanWaitGapUs).toBe(10);
  });

  it('aggregates gater frequency across ticks', () => {
    // Two ticks: tick 1, A1 is slowest; tick 2, A2 is slowest. Each should gate once.
    const systems = [
      sys({ name: 'A1', index: 0 }),
      sys({ name: 'A2', index: 1 }),
      sys({ name: 'B', index: 2, predecessors: [0, 1] }),
    ];
    const rows = [
      // Tick 1: A1 slow (200), A2 fast (100), B.ready = 200.
      row({ systemIndex: 0, tickNumber: 1, startUs: 0, endUs: 200 }),
      row({ systemIndex: 1, tickNumber: 1, startUs: 0, endUs: 100 }),
      row({ systemIndex: 2, tickNumber: 1, readyUs: 200, startUs: 200, endUs: 300 }),
      // Tick 2: A1 fast (100), A2 slow (300), B.ready = 300.
      row({ systemIndex: 0, tickNumber: 2, startUs: 0, endUs: 100 }),
      row({ systemIndex: 1, tickNumber: 2, startUs: 0, endUs: 300 }),
      row({ systemIndex: 2, tickNumber: 2, readyUs: 300, startUs: 300, endUs: 400 }),
    ];
    const result = computeGatingAnalysis({ systems, rows, edges: [], range: null });
    const info = result.get('B')!;
    expect(info.ticksObserved).toBe(2);
    const a1 = info.gaters.find((g) => g.predecessorName === 'A1')!;
    const a2 = info.gaters.find((g) => g.predecessorName === 'A2')!;
    expect(a1.ticksGated).toBe(1);
    expect(a2.ticksGated).toBe(1);
  });

  it('attaches edge metadata when an edge exists for the (predecessor, system) pair', () => {
    const systems = [
      sys({ name: 'MoveAll', index: 0 }),
      sys({ name: 'Metabolism_T0', index: 1, predecessors: [0] }),
    ];
    const rows = [
      row({ systemIndex: 0, tickNumber: 1, startUs: 0, endUs: 318 }),
      row({ systemIndex: 1, tickNumber: 1, readyUs: 318, startUs: 318, endUs: 700 }),
    ];
    const edges = [edge('MoveAll', 'Metabolism_T0', 'fresh', ['Velocity', 'WorldBounds'])];
    const result = computeGatingAnalysis({ systems, rows, edges, range: null });
    const info = result.get('Metabolism_T0')!;
    expect(info.gaters[0].edge?.kind).toBe('fresh');
    expect(info.gaters[0].edge?.via).toEqual(['Velocity', 'WorldBounds']);
  });

  it('respects the tick range — rows outside the window are ignored', () => {
    const systems = [
      sys({ name: 'A', index: 0 }),
      sys({ name: 'B', index: 1, predecessors: [0] }),
    ];
    const rows = [
      row({ systemIndex: 0, tickNumber: 1, startUs: 0, endUs: 100 }),
      row({ systemIndex: 1, tickNumber: 1, readyUs: 100, startUs: 100, endUs: 200 }),
      row({ systemIndex: 0, tickNumber: 5, startUs: 0, endUs: 100 }),
      row({ systemIndex: 1, tickNumber: 5, readyUs: 100, startUs: 100, endUs: 200 }),
    ];
    const result = computeGatingAnalysis({
      systems,
      rows,
      edges: [],
      range: { from: 4, to: 6 },
    });
    const info = result.get('B')!;
    // Only tick 5 should be counted.
    expect(info.ticksObserved).toBe(1);
  });

  it('returns no entry for systems with no observed ticks', () => {
    const systems = [sys({ name: 'A', index: 0 }), sys({ name: 'B', index: 1, predecessors: [0] })];
    // No rows for B at all.
    const rows = [row({ systemIndex: 0, tickNumber: 1, startUs: 0, endUs: 100 })];
    const result = computeGatingAnalysis({ systems, rows, edges: [], range: null });
    expect(result.has('B')).toBe(false);
  });

  it('skips ticks where the system has no readyUs', () => {
    const systems = [sys({ name: 'A', index: 0 }), sys({ name: 'B', index: 1, predecessors: [0] })];
    const rows = [
      row({ systemIndex: 0, tickNumber: 1, startUs: 0, endUs: 100 }),
      // B was skipped — no readyUs on this row.
      row({ systemIndex: 1, tickNumber: 1, startUs: 0, endUs: 0 }),
    ];
    const result = computeGatingAnalysis({ systems, rows, edges: [], range: null });
    expect(result.has('B')).toBe(false);
  });

  it('handles empty inputs gracefully', () => {
    expect(computeGatingAnalysis({ systems: [], rows: [], edges: [], range: null }).size).toBe(0);
    expect(computeGatingAnalysis({ systems: [sys({ name: 'A', index: 0 })], rows: null, edges: [], range: null }).size).toBe(0);
  });

  it('breaks ties between predecessors deterministically — first-by-iteration wins', () => {
    // When two predecessors finish at exactly the same EndUs (rare in real traces but possible in
    // synthetic fixtures and at floating-point epsilons), the algorithm currently credits whichever
    // is encountered first in the predecessor iteration order. The test pins this behaviour so a
    // future refactor (e.g., sorting predecessors before iteration) doesn't silently change the
    // gater attribution. If this assertion ever needs to change, document the new tie-break rule
    // (e.g., "alphabetical by predecessor name") in gatingAnalysis.ts.
    const systems = [
      sys({ name: 'A1', index: 0 }),
      sys({ name: 'A2', index: 1 }),
      sys({ name: 'B', index: 2, predecessors: [0, 1] }), // A1 listed first
    ];
    const rows = [
      // Both predecessors end at 200µs — exact tie.
      row({ systemIndex: 0, tickNumber: 1, startUs: 0, endUs: 200 }),
      row({ systemIndex: 1, tickNumber: 1, startUs: 50, endUs: 200 }),
      row({ systemIndex: 2, tickNumber: 1, readyUs: 200, startUs: 200, endUs: 300 }),
    ];
    const result = computeGatingAnalysis({ systems, rows, edges: [], range: null });
    const info = result.get('B')!;
    expect(info.gaters[0].predecessorName).toBe('A1');
    expect(info.gaters[0].ticksGated).toBe(1);
  });

  it('mean predecessor duration reflects the actual pred duration', () => {
    const systems = [
      sys({ name: 'A', index: 0 }),
      sys({ name: 'B', index: 1, predecessors: [0] }),
    ];
    const rows = [
      // Tick 1: A duration = 100µs.
      row({ systemIndex: 0, tickNumber: 1, startUs: 0, endUs: 100 }),
      row({ systemIndex: 1, tickNumber: 1, readyUs: 100, startUs: 100, endUs: 150 }),
      // Tick 2: A duration = 200µs.
      row({ systemIndex: 0, tickNumber: 2, startUs: 0, endUs: 200 }),
      row({ systemIndex: 1, tickNumber: 2, readyUs: 200, startUs: 200, endUs: 250 }),
    ];
    const result = computeGatingAnalysis({ systems, rows, edges: [], range: null });
    const info = result.get('B')!;
    expect(info.gaters[0].meanPredDurationUs).toBe(150); // (100 + 200) / 2
  });
});
