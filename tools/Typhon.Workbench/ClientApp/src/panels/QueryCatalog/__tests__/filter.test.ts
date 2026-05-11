import { describe, expect, it } from 'vitest';
import type { QueryDefinitionDto, FieldEvaluatorShapeDto } from '@/api/generated/model';
import { passesFilter, type CatalogFilter, type NameLookup } from '../filter';

function evaluator(fieldIdx: number, fieldName: string, op: number, opDisplay: string): FieldEvaluatorShapeDto {
  return { fieldIdx, fieldName, op, opDisplay };
}

function def(opts: {
  archetype?: number;
  evals?: FieldEvaluatorShapeDto[];
  owners?: number[];
  method?: string;
} = {}): QueryDefinitionDto {
  return {
    instanceId: { kind: 0, localId: 1 },
    targetComponentType: opts.archetype ?? 100,
    primaryIndexFieldIdx: -1,
    sortFieldIdx: -1,
    sortDescending: false,
    evaluators: opts.evals ?? [],
    fieldDependencies: [],
    ownerSystemIds: opts.owners ?? [],
    aggregate: {
      executionCount: 0, totalWallNs: 0, avgWallNs: 0,
      p50WallNs: 0, p95WallNs: 0, p99WallNs: 0,
      totalRowsScanned: 0, totalRowsReturned: 0, avgSelectivity: 0,
    },
    userSource: { file: '', line: 0, method: opts.method ?? '' },
  };
}

const NO_NAMES: NameLookup = {
  archetypeName: () => '',
  systemName: () => '',
};

const FOOD_NAMES: NameLookup = {
  archetypeName: (id) => (id === 100 ? 'Ant' : id === 200 ? 'Food' : ''),
  systemName: (id) => (id === 5 ? 'FoodSeekerSystem' : id === 6 ? 'TrailSystem' : ''),
};

const NO_FILTER: CatalogFilter = { search: '', systemFilter: null, archetypeFilter: null };

describe('passesFilter — no filter', () => {
  it('returns true for every definition when filter is empty', () => {
    expect(passesFilter(def(), NO_FILTER, NO_NAMES)).toBe(true);
  });
});

describe('passesFilter — archetypeFilter', () => {
  it('matches when archetype id matches', () => {
    const f: CatalogFilter = { ...NO_FILTER, archetypeFilter: 100 };
    expect(passesFilter(def({ archetype: 100 }), f, NO_NAMES)).toBe(true);
  });

  it('excludes when archetype id does not match', () => {
    const f: CatalogFilter = { ...NO_FILTER, archetypeFilter: 200 };
    expect(passesFilter(def({ archetype: 100 }), f, NO_NAMES)).toBe(false);
  });
});

describe('passesFilter — systemFilter', () => {
  it('matches when system id is in OwnerSystemIds', () => {
    const f: CatalogFilter = { ...NO_FILTER, systemFilter: 5 };
    expect(passesFilter(def({ owners: [5, 6] }), f, NO_NAMES)).toBe(true);
  });

  it('excludes when system id is not in OwnerSystemIds', () => {
    const f: CatalogFilter = { ...NO_FILTER, systemFilter: 5 };
    expect(passesFilter(def({ owners: [6, 7] }), f, NO_NAMES)).toBe(false);
  });

  it('excludes when OwnerSystemIds is empty', () => {
    const f: CatalogFilter = { ...NO_FILTER, systemFilter: 5 };
    expect(passesFilter(def({ owners: [] }), f, NO_NAMES)).toBe(false);
  });
});

describe('passesFilter — search', () => {
  it('matches when search appears in resolved archetype name', () => {
    const f: CatalogFilter = { ...NO_FILTER, search: 'ant' };
    expect(passesFilter(def({ archetype: 100 }), f, FOOD_NAMES)).toBe(true);
  });

  it('matches when search appears in evaluator field name', () => {
    const f: CatalogFilter = { ...NO_FILTER, search: 'energy' };
    const d = def({ evals: [evaluator(3, 'energy', 3, '>')] });
    expect(passesFilter(d, f, NO_NAMES)).toBe(true);
  });

  it('matches when search appears in resolved system owner name', () => {
    const f: CatalogFilter = { ...NO_FILTER, search: 'seeker' };
    expect(passesFilter(def({ owners: [5] }), f, FOOD_NAMES)).toBe(true);
  });

  it('matches when search appears in source method name', () => {
    const f: CatalogFilter = { ...NO_FILTER, search: 'configure' };
    expect(passesFilter(def({ method: 'Configure' }), f, NO_NAMES)).toBe(true);
  });

  it('excludes when search does not match anywhere', () => {
    const f: CatalogFilter = { ...NO_FILTER, search: 'xyz' };
    expect(passesFilter(def({ archetype: 100 }), f, FOOD_NAMES)).toBe(false);
  });

  it('search is case-insensitive (needle should be lower-cased by caller)', () => {
    const f: CatalogFilter = { ...NO_FILTER, search: 'ant' };
    expect(passesFilter(def({ archetype: 100 }), f, FOOD_NAMES)).toBe(true);
  });
});

describe('passesFilter — combined filters', () => {
  it('ALL filters must pass (AND semantics)', () => {
    const d = def({ archetype: 100, owners: [5], evals: [evaluator(3, 'energy', 3, '>')] });
    // matches everything
    expect(passesFilter(d, { search: 'energy', systemFilter: 5, archetypeFilter: 100 }, FOOD_NAMES)).toBe(true);
    // archetype mismatch
    expect(passesFilter(d, { search: 'energy', systemFilter: 5, archetypeFilter: 200 }, FOOD_NAMES)).toBe(false);
    // system mismatch
    expect(passesFilter(d, { search: 'energy', systemFilter: 6, archetypeFilter: 100 }, FOOD_NAMES)).toBe(false);
    // search mismatch
    expect(passesFilter(d, { search: 'unrelated', systemFilter: 5, archetypeFilter: 100 }, FOOD_NAMES)).toBe(false);
  });
});
