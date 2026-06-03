import { describe, expect, it } from 'vitest';
import type { QuerySpecDto } from '@/api/generated/model/querySpecDto';
import type { SpatialClauseDto } from '@/api/generated/model/spatialClauseDto';
import { DEFAULT_TAKE, hasExplicitTake, spatialToDsl, specToDsl } from '@/panels/QueryConsole/specToDsl';

// Minimal spec factory — only the fields specToDsl reads matter; the rest default to "absent".
function spec(overrides: Partial<QuerySpecDto>): QuerySpecDto {
  return {
    archetype: '#823',
    polymorphic: true,
    with: [],
    without: [],
    exclude: [],
    enabled: [],
    disabled: [],
    where: null as unknown as QuerySpecDto['where'],
    select: [],
    spatial: [],
    navigate: [],
    orderBy: null as unknown as QuerySpecDto['orderBy'],
    skip: 0,
    take: DEFAULT_TAKE,
    revision: { kind: 'head', value: 0, timeIso: '' },
    ...overrides,
  } as QuerySpecDto;
}

describe('hasExplicitTake', () => {
  // The bug (#386 QC review): a TAKE parsed in from DSL was hidden in chip mode because chip visibility used a
  // user-added flag instead of the value. This rule is now the single source of truth shared with specToDsl.
  it.each([
    [undefined, false],
    [null, false],
    [DEFAULT_TAKE, false], // 1000 is the implicit default → not "explicit"
    [0, false],
    [-5, false],
    [5, true],
    ['5', true], // orval can surface int as string
    [999, true],
    [1001, true],
  ] as const)('hasExplicitTake(%s) === %s', (take, expected) => {
    expect(hasExplicitTake(take)).toBe(expected);
  });
});

describe('specToDsl', () => {
  it('omits TAKE for the default cap', () => {
    expect(specToDsl(spec({ take: DEFAULT_TAKE }))).toBe('FROM #823');
  });

  it('emits a TAKE line for a non-default cap (the value chip mode must also surface)', () => {
    expect(specToDsl(spec({ take: 5 }))).toContain('TAKE 5');
  });

  it('emits SKIP only when positive', () => {
    expect(specToDsl(spec({ skip: 0 }))).not.toContain('SKIP');
    expect(specToDsl(spec({ skip: 10 }))).toContain('SKIP 10');
  });

  it('serializes a full chip-built query in canonical stage order', () => {
    const dsl = specToDsl(
      spec({
        where: { kind: 'cmp', component: 'Typhon.Workbench.Fixture.Player', field: 'Level', op: '>=', value: 0 } as QuerySpecDto['where'],
        orderBy: { component: 'Typhon.Workbench.Fixture.Player', field: 'Level', descending: true } as QuerySpecDto['orderBy'],
        take: 5,
      }),
    );
    expect(dsl).toBe(
      [
        'FROM #823',
        'WHERE Typhon.Workbench.Fixture.Player.Level >= 0',
        'ORDER BY Typhon.Workbench.Fixture.Player.Level DESC',
        'TAKE 5',
      ].join('\n'),
    );
  });

  it('marks a non-polymorphic FROM with `exact`', () => {
    expect(specToDsl(spec({ polymorphic: false }))).toBe('FROM #823 exact');
  });

  it('serializes SELECT (component list) after SPATIAL and before ORDER BY', () => {
    const dsl = specToDsl(
      spec({
        where: { kind: 'cmp', component: 'Fix.Meta', field: 'Level', op: '>=', value: 4 } as QuerySpecDto['where'],
        spatial: [{ component: 'Fix.Pos', kind: 'aabb', parameters: [0, 0, 0, 100, 100, 0] }],
        select: ['Fix.Player', 'Fix.Wallet'],
        orderBy: null as unknown as QuerySpecDto['orderBy'],
      }),
    );
    expect(dsl).toBe(
      ['FROM #823', 'WHERE Fix.Meta.Level >= 4', 'SPATIAL Fix.Pos AABB 0, 0, 0, 100, 100, 0', 'SELECT Fix.Player, Fix.Wallet'].join('\n'),
    );
  });

  it('omits SELECT when empty (no-SELECT default round-trips unchanged)', () => {
    expect(specToDsl(spec({ select: [] }))).toBe('FROM #823');
  });

  it('serializes SPATIAL between WHERE and ORDER BY (stage stack order)', () => {
    const dsl = specToDsl(
      spec({
        where: { kind: 'cmp', component: 'Fix.Meta', field: 'Level', op: '>=', value: 4 } as QuerySpecDto['where'],
        spatial: [{ component: 'Fix.Pos', kind: 'aabb', parameters: [0, 0, 0, 100, 100, 0] }],
        orderBy: null as unknown as QuerySpecDto['orderBy'],
      }),
    );
    expect(dsl).toBe(
      ['FROM #823', 'WHERE Fix.Meta.Level >= 4', 'SPATIAL Fix.Pos AABB 0, 0, 0, 100, 100, 0'].join('\n'),
    );
  });
});

describe('spatialToDsl', () => {
  const clause = (kind: string, parameters: number[]): SpatialClauseDto => ({ component: 'Fix.Pos', kind, parameters });

  it('serializes NEARBY (center + RADIUS)', () => {
    expect(spatialToDsl(clause('nearby', [1, 2, 3, 50]))).toBe('SPATIAL Fix.Pos NEARBY 1, 2, 3 RADIUS 50');
  });

  it('serializes AABB (min then max point)', () => {
    expect(spatialToDsl(clause('aabb', [0, 0, 0, 100, 200, 300]))).toBe('SPATIAL Fix.Pos AABB 0, 0, 0, 100, 200, 300');
  });

  it('serializes RAY (origin, direction, maxDist)', () => {
    expect(spatialToDsl(clause('ray', [0, 0, 0, 1, 0, 0, 500]))).toBe('SPATIAL Fix.Pos RAY 0, 0, 0, 1, 0, 0, 500');
  });

  it('preserves negative and fractional coordinates', () => {
    expect(spatialToDsl(clause('nearby', [-1.5, 2, 0, 10]))).toBe('SPATIAL Fix.Pos NEARBY -1.5, 2, 0 RADIUS 10');
  });

  it('returns null for an unknown kind or missing component (half-built chip emits nothing)', () => {
    expect(spatialToDsl(clause('frustum', [0, 0, 0, 1]))).toBeNull();
    expect(spatialToDsl({ component: null, kind: 'aabb', parameters: [0, 0, 0, 1, 1, 0] })).toBeNull();
  });
});
