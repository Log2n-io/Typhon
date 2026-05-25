import { describe, expect, it } from 'vitest';
import type { QueryDefinitionDto, FieldEvaluatorShapeDto } from '@/api/generated/model';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import type { QueryExecutionPhaseDto } from '@/api/generated/model/queryExecutionPhaseDto';
import { buildQueryPlanGraph, type QueryPlanNode } from '../queryPlanLayout';

function evaluator(fieldIdx: number, fieldName: string, op: number, opDisplay: string): FieldEvaluatorShapeDto {
  return { fieldIdx, fieldName, op, opDisplay };
}

function def(opts: {
  archetype?: number;
  primary?: number;
  sort?: number;
  sortDesc?: boolean;
  evals?: FieldEvaluatorShapeDto[];
} = {}): QueryDefinitionDto {
  return {
    instanceId: { kind: 0, localId: 1 },
    targetComponentType: opts.archetype ?? 100,
    primaryIndexFieldIdx: opts.primary ?? -1,
    sortFieldIdx: opts.sort ?? -1,
    sortDescending: opts.sortDesc ?? false,
    evaluators: opts.evals ?? [],
    fieldDependencies: [],
    ownerSystemIds: [],
    aggregate: {
      executionCount: 0, totalWallNs: 0, avgWallNs: 0,
      p50WallNs: 0, p95WallNs: 0, p99WallNs: 0,
      totalRowsScanned: 0, totalRowsReturned: 0, avgSelectivity: 0,
    },
    userSource: { file: '', line: 0, method: '' },
  };
}

function phase(name: string, wallNs: number, estimate?: number, actual?: number, notes?: string): QueryExecutionPhaseDto {
  return { phaseName: name, wallNs, estimate: estimate ?? null, actual: actual ?? null, notes: notes ?? null };
}

function execution(phases: QueryExecutionPhaseDto[]): QueryExecutionDto {
  return {
    definitionId: { kind: 0, localId: 1 },
    spanId: 0,
    parentSpanId: 0,
    tickIndex: 0,
    systemId: -1,
    startTs: 0,
    endTs: 0,
    args: null,
    phases,
  };
}

function ids(nodes: QueryPlanNode[]): string[] {
  return nodes.map((n) => n.id);
}

describe('buildQueryPlanGraph — structural', () => {
  it('definition with no primary index emits a Full Scan + Result', () => {
    const { nodes, edges } = buildQueryPlanGraph(def(), null);
    expect(ids(nodes)).toEqual(['full-scan', 'result']);
    expect(edges).toHaveLength(1);
    expect(edges[0].source).toBe('full-scan');
    expect(edges[0].target).toBe('result');
  });

  it('definition with primary index emits an Index Scan + Result', () => {
    const { nodes } = buildQueryPlanGraph(def({ primary: 3, evals: [evaluator(3, 'energy', 0, '>')] }), null);
    expect(ids(nodes)).toEqual(['index-scan', 'result']);
  });

  it('definition with primary index + filters skips the evaluator that drives the primary scan', () => {
    const { nodes } = buildQueryPlanGraph(
      def({
        primary: 3,
        evals: [
          evaluator(3, 'energy', 0, '>'),
          evaluator(5, 'hunger', 1, '>'),
          evaluator(7, 'range', 2, '<'),
        ],
      }),
      null,
    );
    // index-scan, filter-1, filter-2, result (filter-0 = primary, omitted)
    expect(ids(nodes)).toEqual(['index-scan', 'filter-1', 'filter-2', 'result']);
  });

  it('definition with sort field emits a Sort node before Result', () => {
    const { nodes } = buildQueryPlanGraph(
      def({ primary: 3, sort: 5, evals: [evaluator(3, 'energy', 0, '>'), evaluator(5, 'range', 0, '<')] }),
      null,
    );
    expect(ids(nodes)).toEqual(['index-scan', 'filter-1', 'sort', 'result']);
  });

  it('full chain: index + filters + sort', () => {
    const { nodes, edges } = buildQueryPlanGraph(
      def({
        primary: 3,
        sort: 7,
        sortDesc: true,
        evals: [
          evaluator(3, 'energy', 0, '>'),
          evaluator(5, 'hunger', 1, '>'),
          evaluator(7, 'range', 2, '<'),
        ],
      }),
      null,
    );
    expect(ids(nodes)).toEqual(['index-scan', 'filter-1', 'filter-2', 'sort', 'result']);
    // Edges form a linear pipeline
    expect(edges).toHaveLength(4);
    expect(edges.map((e) => `${e.source}→${e.target}`)).toEqual([
      'index-scan→filter-1',
      'filter-1→filter-2',
      'filter-2→sort',
      'sort→result',
    ]);
  });

  it('Result node carries a structural detail when no execution is provided', () => {
    const { nodes } = buildQueryPlanGraph(def(), null);
    const result = nodes.find((n) => n.id === 'result');
    expect(result?.data.detail).toBe('final result set');
  });

  it('archetypeName resolver is used to label the scan node', () => {
    const { nodes } = buildQueryPlanGraph(def({ archetype: 100 }), null, (id) => (id === 100 ? 'Ant' : ''));
    const scan = nodes.find((n) => n.id === 'full-scan');
    expect(scan?.data.detail).toBe('Ant');
  });
});

describe('buildQueryPlanGraph — execution mode', () => {
  it('phase named "IndexScan" feeds stats into the index-scan node', () => {
    const { nodes } = buildQueryPlanGraph(
      def({ primary: 3, evals: [evaluator(3, 'energy', 0, '>')] }),
      execution([phase('IndexScan', 1500, 1200, 934)]),
    );
    const scan = nodes.find((n) => n.id === 'index-scan');
    expect(scan?.data.stats?.wallNs).toBe(1500);
    expect(scan?.data.stats?.estimate).toBe(1200);
    expect(scan?.data.stats?.actual).toBe(934);
  });

  it('phase classification is case-insensitive and substring-friendly', () => {
    const { nodes } = buildQueryPlanGraph(
      def({ primary: 3, evals: [evaluator(3, 'energy', 0, '>')] }),
      execution([phase('full-index-scan', 999)]),
    );
    expect(nodes.find((n) => n.id === 'index-scan')?.data.stats?.wallNs).toBe(999);
  });

  it('unknown phases are ignored without throwing', () => {
    const { nodes } = buildQueryPlanGraph(
      def(),
      execution([phase('weirdo', 1), phase('also-not-a-phase', 2)]),
    );
    expect(nodes.find((n) => n.id === 'full-scan')?.data.stats).toBeUndefined();
  });

  it('phase notes propagate to the node stats', () => {
    const { nodes } = buildQueryPlanGraph(
      def({ sort: 3, evals: [evaluator(3, 'range', 1, '<')] }),
      execution([phase('Sort', 14_700, undefined, undefined, 'spilled')]),
    );
    expect(nodes.find((n) => n.id === 'sort')?.data.stats?.notes).toBe('spilled');
  });

  it('Result phase actual count surfaces on the Result node detail', () => {
    const { nodes } = buildQueryPlanGraph(
      def(),
      execution([phase('Result', 100, undefined, 50)]),
    );
    const result = nodes.find((n) => n.id === 'result');
    expect(result?.data.detail).toBe('50 rows');
    expect(result?.data.stats?.actual).toBe(50);
  });

  it('null execution → no node has stats populated', () => {
    const { nodes } = buildQueryPlanGraph(
      def({ primary: 3, sort: 5, evals: [evaluator(3, 'energy', 0, '>'), evaluator(5, 'range', 0, '<')] }),
      null,
    );
    for (const n of nodes) expect(n.data.stats).toBeUndefined();
  });
});

describe('buildQueryPlanGraph — layout', () => {
  it('dagre top-down assigns increasing y-coordinates to downstream nodes', () => {
    const { nodes } = buildQueryPlanGraph(
      def({ primary: 3, evals: [evaluator(3, 'energy', 0, '>'), evaluator(5, 'range', 0, '<')] }),
      null,
    );
    const ys = nodes.map((n) => n.position.y);
    for (let i = 1; i < ys.length; i++) {
      expect(ys[i]).toBeGreaterThanOrEqual(ys[i - 1]);
    }
  });
});
