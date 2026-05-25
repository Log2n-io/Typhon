import { describe, expect, it } from 'vitest';
import type { Track } from '@/panels/DataFlow/trackBuilding';
import { type AccessMatrix, type Cell, type Column } from '../matrixBuilding';
import {
  clusterReorderColumns,
  clusterReorderRows,
  orderColumnsByPhaseDependency,
} from '../matrixOrdering';

function col(name: string, idx: number, phase: string): Column {
  return { systemName: name, systemIndex: idx, phaseName: phase };
}

function row(id: string): Track {
  return { id, label: id, kind: 'component', componentName: id };
}

function cell(rowId: string, colName: string, accessKind: Cell['accessKind'] = 'reads', touchCount = 0): [string, Cell] {
  return [`${rowId}|${colName}`, { rowId, columnSystemName: colName, accessKind, touchCount }];
}

describe('orderColumnsByPhaseDependency', () => {
  it('returns [] for empty input', () => {
    expect(orderColumnsByPhaseDependency([], [], new Map())).toEqual([]);
  });

  it('groups by phase in declared phase order', () => {
    const cols = [col('A', 1, 'Output'), col('B', 2, 'Input'), col('C', 3, 'Sim')];
    const result = orderColumnsByPhaseDependency(cols, ['Input', 'Sim', 'Output'], new Map());
    expect(result.map((c) => c.systemName)).toEqual(['B', 'C', 'A']);
  });

  it('sorts within phase by topological dependency order', () => {
    // Sim phase: B depends on A; A is the root.
    const cols = [col('A', 1, 'Sim'), col('B', 2, 'Sim'), col('C', 3, 'Sim')];
    const preds = new Map<string, string[]>([
      ['B', ['A']],
      ['C', ['B']],
    ]);
    const result = orderColumnsByPhaseDependency(cols, ['Sim'], preds);
    expect(result.map((c) => c.systemName)).toEqual(['A', 'B', 'C']);
  });

  it('puts unknown-phase columns at the end', () => {
    const cols = [col('A', 1, 'Sim'), col('Outsider', 2, 'GhostPhase')];
    const result = orderColumnsByPhaseDependency(cols, ['Sim'], new Map());
    expect(result.map((c) => c.systemName)).toEqual(['A', 'Outsider']);
  });

  it('degrades gracefully on dependency cycles (no system dropped)', () => {
    const cols = [col('A', 1, 'Sim'), col('B', 2, 'Sim')];
    const preds = new Map<string, string[]>([
      ['A', ['B']],
      ['B', ['A']],
    ]);
    const result = orderColumnsByPhaseDependency(cols, ['Sim'], preds);
    expect(result.length).toBe(2);
    expect(new Set(result.map((c) => c.systemName))).toEqual(new Set(['A', 'B']));
  });
});

describe('clusterReorderColumns — cosine similarity', () => {
  function buildMatrix(rows: Track[], columns: Column[], cellEntries: [string, Cell][]): AccessMatrix {
    return { rows, columns, cells: new Map(cellEntries) };
  }

  it('returns input unchanged when only one column', () => {
    const m = buildMatrix([row('Position')], [col('A', 1, 'Sim')], [cell('Position', 'A')]);
    expect(clusterReorderColumns(m).map((c) => c.systemName)).toEqual(['A']);
  });

  it('puts similar systems adjacent (Phys + Move both write Position)', () => {
    // Phys writes Position; Move writes Position; AI writes Health. Phys and Move should cluster together.
    const rows = [row('Position'), row('Health')];
    const cols = [col('Phys', 1, 'Sim'), col('AI', 2, 'Sim'), col('Move', 3, 'Sim')];
    const m = buildMatrix(rows, cols, [
      cell('Position', 'Phys'),
      cell('Health', 'AI'),
      cell('Position', 'Move'),
    ]);
    const ordered = clusterReorderColumns(m).map((c) => c.systemName);
    // Phys and Move (both touching Position) are adjacent. AI (touching only Health) is on the other end.
    const physIdx = ordered.indexOf('Phys');
    const moveIdx = ordered.indexOf('Move');
    expect(Math.abs(physIdx - moveIdx)).toBe(1);
  });

  it('seeds from the highest-norm column', () => {
    // A touches 3 rows, B touches 1, C touches 1. Seed should be A.
    const rows = [row('R1'), row('R2'), row('R3')];
    const cols = [col('A', 1, 'Sim'), col('B', 2, 'Sim'), col('C', 3, 'Sim')];
    const m = buildMatrix(rows, cols, [
      cell('R1', 'A'), cell('R2', 'A'), cell('R3', 'A'),
      cell('R1', 'B'),
      cell('R2', 'C'),
    ]);
    const ordered = clusterReorderColumns(m).map((c) => c.systemName);
    expect(ordered[0]).toBe('A');
  });

  it('falls back gracefully when all access vectors are empty', () => {
    const rows = [row('R1')];
    const cols = [col('A', 1, 'Sim'), col('B', 2, 'Sim')];
    const m = buildMatrix(rows, cols, []);  // no cells → all zero vectors
    const ordered = clusterReorderColumns(m).map((c) => c.systemName);
    expect(new Set(ordered)).toEqual(new Set(['A', 'B']));
    expect(ordered.length).toBe(2);
  });

  it('is deterministic — same input → same output', () => {
    const rows = [row('R1'), row('R2')];
    const cols = [col('A', 1, 'Sim'), col('B', 2, 'Sim'), col('C', 3, 'Sim')];
    const m = buildMatrix(rows, cols, [cell('R1', 'A'), cell('R1', 'B'), cell('R2', 'C')]);
    const a = clusterReorderColumns(m).map((c) => c.systemName);
    const b = clusterReorderColumns(m).map((c) => c.systemName);
    expect(a).toEqual(b);
  });

  it('preserves all columns (no drop)', () => {
    const rows = [row('R1'), row('R2'), row('R3')];
    const cols = [
      col('A', 1, 'Sim'), col('B', 2, 'Sim'), col('C', 3, 'Sim'),
      col('D', 4, 'Sim'), col('E', 5, 'Sim'),
    ];
    const m = buildMatrix(rows, cols, [
      cell('R1', 'A'), cell('R1', 'B'),
      cell('R2', 'C'), cell('R2', 'D'),
      cell('R3', 'E'),
    ]);
    const ordered = clusterReorderColumns(m);
    expect(new Set(ordered.map((c) => c.systemName))).toEqual(new Set(['A', 'B', 'C', 'D', 'E']));
  });
});

describe('clusterReorderRows — cosine similarity', () => {
  function buildMatrix(rows: Track[], columns: Column[], cellEntries: [string, Cell][]): AccessMatrix {
    return { rows, columns, cells: new Map(cellEntries) };
  }

  it('clusters rows touched by similar systems', () => {
    // R1 + R2 are both touched by Phys; R3 is only touched by AI.
    const rows = [row('R1'), row('R2'), row('R3')];
    const cols = [col('Phys', 1, 'Sim'), col('AI', 2, 'Sim')];
    const m = buildMatrix(rows, cols, [
      cell('R1', 'Phys'),
      cell('R2', 'Phys'),
      cell('R3', 'AI'),
    ]);
    const ordered = clusterReorderRows(m).map((r) => r.id);
    const r1 = ordered.indexOf('R1');
    const r2 = ordered.indexOf('R2');
    expect(Math.abs(r1 - r2)).toBe(1);
  });
});
