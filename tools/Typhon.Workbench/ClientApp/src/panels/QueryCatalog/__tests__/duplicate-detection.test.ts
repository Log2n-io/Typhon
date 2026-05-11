import { describe, expect, it } from 'vitest';
import type { QueryDefinitionDto, FieldEvaluatorShapeDto } from '@/api/generated/model';
import { findDuplicateDefinitions } from '../duplicate-detection';

function evaluator(fieldIdx: number, op: number): FieldEvaluatorShapeDto {
  return {
    fieldIdx,
    fieldName: `Field[${fieldIdx}]`,
    op,
    opDisplay: '==',
  };
}

function def(kind: number, localId: number, opts: {
  archetype?: number;
  primary?: number;
  sort?: number;
  sortDesc?: boolean;
  evals?: FieldEvaluatorShapeDto[];
} = {}): QueryDefinitionDto {
  return {
    instanceId: { kind, localId },
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

describe('findDuplicateDefinitions', () => {
  it('empty input → empty result', () => {
    expect(findDuplicateDefinitions([]).size).toBe(0);
  });

  it('single definition → no duplicate', () => {
    const result = findDuplicateDefinitions([def(0, 1)]);
    expect(result.size).toBe(0);
  });

  it('two structurally-different definitions → no duplicates', () => {
    const result = findDuplicateDefinitions([
      def(0, 1, { archetype: 100 }),
      def(0, 2, { archetype: 200 }),
    ]);
    expect(result.size).toBe(0);
  });

  it('two structurally-identical-but-distinct definitions → both marked duplicate', () => {
    const result = findDuplicateDefinitions([
      def(0, 1, { archetype: 100, evals: [evaluator(5, 3)] }),
      def(0, 2, { archetype: 100, evals: [evaluator(5, 3)] }),
    ]);
    expect(result.size).toBe(2);
    expect(result.has('0:1')).toBe(true);
    expect(result.has('0:2')).toBe(true);
  });

  it('three structurally-identical definitions → all three marked duplicate', () => {
    const result = findDuplicateDefinitions([
      def(0, 1, { archetype: 100, evals: [evaluator(5, 3)] }),
      def(0, 2, { archetype: 100, evals: [evaluator(5, 3)] }),
      def(0, 3, { archetype: 100, evals: [evaluator(5, 3)] }),
    ]);
    expect(result.size).toBe(3);
    expect(result.has('0:1')).toBe(true);
    expect(result.has('0:2')).toBe(true);
    expect(result.has('0:3')).toBe(true);
  });

  it('evaluator order is normalized — same evaluators in different order are still duplicates', () => {
    const result = findDuplicateDefinitions([
      def(0, 1, { evals: [evaluator(1, 0), evaluator(2, 0)] }),
      def(0, 2, { evals: [evaluator(2, 0), evaluator(1, 0)] }),
    ]);
    expect(result.size).toBe(2);
  });

  it('LocalId is excluded — only structural fields contribute to hash', () => {
    const result = findDuplicateDefinitions([
      def(0, 9999, { evals: [evaluator(5, 3)] }),
      def(0, 10000, { evals: [evaluator(5, 3)] }),
    ]);
    expect(result.size).toBe(2);
  });

  it('Kind difference does NOT distinguish — View+EcsQuery with same shape are both flagged duplicates', () => {
    // A View and an EcsQuery with otherwise-identical shape ARE considered duplicates per the
    // current implementation — Kind is intentionally excluded from structuralShape. This matches
    // the design's user-facing question: "do two systems have the same query?" — answer yes
    // regardless of which pipeline they used. If we want strict per-pipeline dedup later, add Kind
    // to structuralShape.
    const result = findDuplicateDefinitions([
      def(0, 1, { evals: [evaluator(5, 3)] }),
      def(1, 1, { evals: [evaluator(5, 3)] }),
    ]);
    expect(result.size).toBe(2);
  });
});
