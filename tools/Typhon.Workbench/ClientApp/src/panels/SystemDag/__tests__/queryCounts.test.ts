import { describe, expect, it } from 'vitest';
import type { QueryDefinitionDto } from '@/api/generated/model/queryDefinitionDto';
import { buildQueryCountsBySystem } from '../queryCounts';

function def(opts: {
  kind?: number;
  localId?: number;
  owners?: number[];
} = {}): QueryDefinitionDto {
  return {
    instanceId: { kind: opts.kind ?? 0, localId: opts.localId ?? 1 },
    targetComponentType: 100,
    primaryIndexFieldIdx: -1,
    sortFieldIdx: -1,
    sortDescending: false,
    evaluators: [],
    fieldDependencies: [],
    ownerSystemIds: opts.owners ?? [],
    aggregate: {
      executionCount: 0, totalWallNs: 0, avgWallNs: 0,
      p50WallNs: 0, p95WallNs: 0, p99WallNs: 0,
      totalRowsScanned: 0, totalRowsReturned: 0, avgSelectivity: 0,
    },
    userSource: { file: '', line: 0, method: '' },
  };
}

const SYSTEM_NAMES: Map<number, string> = new Map([
  [5, 'FoodSeekerSystem'],
  [6, 'TrailSystem'],
  [7, 'MetabolismSystem'],
]);

describe('buildQueryCountsBySystem', () => {
  it('empty definitions → empty map', () => {
    expect(buildQueryCountsBySystem([], SYSTEM_NAMES).size).toBe(0);
  });

  it('single definition with one owner counts on that owner', () => {
    const result = buildQueryCountsBySystem([def({ owners: [5] })], SYSTEM_NAMES);
    expect(result.size).toBe(1);
    expect(result.get('FoodSeekerSystem')).toBe(1);
  });

  it('multi-owner definition counts once on each owner', () => {
    const result = buildQueryCountsBySystem([def({ owners: [5, 6, 7] })], SYSTEM_NAMES);
    expect(result.get('FoodSeekerSystem')).toBe(1);
    expect(result.get('TrailSystem')).toBe(1);
    expect(result.get('MetabolismSystem')).toBe(1);
  });

  it('two definitions with overlapping owners accumulate per system', () => {
    const result = buildQueryCountsBySystem(
      [def({ localId: 1, owners: [5, 6] }), def({ localId: 2, owners: [5, 7] })],
      SYSTEM_NAMES,
    );
    expect(result.get('FoodSeekerSystem')).toBe(2);
    expect(result.get('TrailSystem')).toBe(1);
    expect(result.get('MetabolismSystem')).toBe(1);
  });

  it('a definition listing the same owner twice still counts as 1 for that owner', () => {
    const result = buildQueryCountsBySystem([def({ owners: [5, 5, 5] })], SYSTEM_NAMES);
    expect(result.get('FoodSeekerSystem')).toBe(1);
  });

  it('unknown system id (not in metadata) is filtered out', () => {
    const result = buildQueryCountsBySystem([def({ owners: [99] })], SYSTEM_NAMES);
    expect(result.has('99')).toBe(false);
    expect(result.size).toBe(0);
  });

  it('negative system id is filtered out (P4 unattributed sentinel)', () => {
    const result = buildQueryCountsBySystem([def({ owners: [-1, 5] })], SYSTEM_NAMES);
    expect(result.size).toBe(1);
    expect(result.get('FoodSeekerSystem')).toBe(1);
  });

  it('definition with empty/null ownerSystemIds → contributes nothing', () => {
    const result = buildQueryCountsBySystem(
      [def({ owners: [] }), def({ owners: undefined as unknown as number[] })],
      SYSTEM_NAMES,
    );
    expect(result.size).toBe(0);
  });

  // Acceptance criterion from issue #341: badge count must equal the Catalog filter count for the
  // same system. Filter semantics: a definition matches systemFilter S when ownerSystemIds includes S.
  it('count matches the Query Catalog system-filter count (cross-check)', () => {
    const definitions = [
      def({ localId: 1, owners: [5, 6] }),
      def({ localId: 2, owners: [5] }),
      def({ localId: 3, owners: [6, 7] }),
      def({ localId: 4, owners: [] }),
    ];
    const result = buildQueryCountsBySystem(definitions, SYSTEM_NAMES);
    for (const [id, name] of SYSTEM_NAMES) {
      const filterCount = definitions.filter((d) => (d.ownerSystemIds ?? []).map(Number).includes(id)).length;
      expect(result.get(name) ?? 0).toBe(filterCount);
    }
  });
});
