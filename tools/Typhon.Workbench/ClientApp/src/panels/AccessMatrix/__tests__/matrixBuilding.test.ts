import { describe, expect, it } from 'vitest';
import type { ArchetypeDto } from '@/api/generated/model/archetypeDto';
import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { buildAccessMatrix } from '../matrixBuilding';

function topo(overrides: Partial<TopologyDto> = {}): TopologyDto {
  return {
    systems: [],
    archetypes: [],
    componentTypes: [],
    phases: [],
    componentFamilies: { componentToFamily: {}, familyOrder: [] },
    ...overrides,
  };
}

function sys(name: string, index: number, phase: string = 'Sim', extras: Partial<SystemDefinitionDto> = {}): SystemDefinitionDto {
  return {
    index, name, type: 0, priority: 0, isParallel: true, tierFilter: 0,
    predecessors: [], successors: [], phaseName: phase, isExclusivePhase: false,
    reads: [], readsFresh: [], readsSnapshot: [], additionalReads: [],
    writes: [], sideWrites: [],
    writesEvents: [], readsEvents: [],
    writesResources: [], readsResources: [],
    explicitAfter: [], explicitBefore: [],
    ...extras,
  };
}

function arch(id: number, label: string, components: string[]): ArchetypeDto {
  return { archetypeId: id, name: label, label, schemaRevision: 1, componentTypeNames: components };
}

function touch(tick: number, sysIdx: number, archId: number): SystemArchetypeTouchSummary {
  return { tickNumber: tick, systemIndex: sysIdx, archetypeId: archId, entityCount: 0, chunkCount: 0 } as unknown as SystemArchetypeTouchSummary;
}

describe('buildAccessMatrix — null / empty', () => {
  it('returns empty matrix when topology is null', () => {
    const m = buildAccessMatrix(null, 'L2', []);
    expect(m).toEqual({ rows: [], columns: [], cells: new Map() });
  });

  it('returns empty matrix when systems list is empty', () => {
    const m = buildAccessMatrix(topo({ phases: ['A'] }), 'L0', []);
    expect(m.rows.length).toBe(3); // domain rows
    expect(m.columns).toEqual([]);
    expect(m.cells.size).toBe(0);
  });
});

describe('buildAccessMatrix — declared access cells', () => {
  it('emits one cell per (component-row, system-column) for declared writes', () => {
    const t = topo({
      systems: [sys('Phys', 1, 'Sim', { writes: ['Position'] })],
      componentTypes: [{ componentTypeId: 1, name: 'Position' }],
    });
    const m = buildAccessMatrix(t, 'L3', []);
    // 1 component row + 2 domain rows; 1 column
    expect(m.columns.length).toBe(1);
    const cell = m.cells.get('component:Position|Phys');
    expect(cell).toBeDefined();
    expect(cell?.accessKind).toBe('write');
    expect(cell?.touchCount).toBe(0);
  });

  it('domain rows light up when system has any declared access in that domain', () => {
    const t = topo({
      systems: [sys('Phys', 1, 'Sim', { writes: ['Position'], readsEvents: ['Tick'] })],
    });
    const m = buildAccessMatrix(t, 'L0', []);
    expect(m.cells.get('domain:components|Phys')?.accessKind).toBe('write');
    expect(m.cells.get('domain:queues|Phys')?.accessKind).toBe('reads');
    expect(m.cells.get('domain:resources|Phys')).toBeUndefined();  // no resource declaration → no cell
  });

  it('write beats reads at the domain row', () => {
    const t = topo({
      systems: [sys('Mixed', 1, 'Sim', { writes: ['A'], reads: ['B'] })],
    });
    const m = buildAccessMatrix(t, 'L0', []);
    expect(m.cells.get('domain:components|Mixed')?.accessKind).toBe('write');
  });
});

describe('buildAccessMatrix — empirical touch counts', () => {
  it('increments touchCount on rows that match the event\'s archetype', () => {
    const t = topo({
      systems: [sys('Phys', 1, 'Sim', { writes: ['Position'] })],
      archetypes: [arch(100, 'Ant', ['Position'])],
      componentTypes: [{ componentTypeId: 1, name: 'Position' }],
    });
    const m = buildAccessMatrix(t, 'L3', [touch(1, 1, 100), touch(2, 1, 100)]);
    expect(m.cells.get('component:Position|Phys')?.touchCount).toBe(2);
  });

  it('creates a none-kind cell when system touched a row at runtime without declaring matching access', () => {
    // Runtime drift case: sys touches Position but doesn't declare it. The cell appears so the discrepancy is visible.
    const t = topo({
      systems: [sys('Phys', 1, 'Sim')],   // no writes/reads
      archetypes: [arch(100, 'Ant', ['Position'])],
      componentTypes: [{ componentTypeId: 1, name: 'Position' }],
    });
    const m = buildAccessMatrix(t, 'L3', [touch(1, 1, 100)]);
    const cell = m.cells.get('component:Position|Phys');
    expect(cell).toBeDefined();
    expect(cell?.accessKind).toBe('none');
    expect(cell?.touchCount).toBe(1);
  });
});

describe('buildAccessMatrix — multi-system topology', () => {
  it('builds correctly across multiple phases', () => {
    const t = topo({
      systems: [
        sys('A', 1, 'Input',  { writes: ['Position'] }),
        sys('B', 2, 'Sim',    { reads: ['Position'], writes: ['Velocity'] }),
        sys('C', 3, 'Output', { reads: ['Velocity'] }),
      ],
      componentTypes: [
        { componentTypeId: 1, name: 'Position' },
        { componentTypeId: 2, name: 'Velocity' },
      ],
      phases: ['Input', 'Sim', 'Output'],
    });
    const m = buildAccessMatrix(t, 'L3', []);
    expect(m.columns.length).toBe(3);
    // 6 declared cells: A.write Position, B.read Position, B.write Velocity, C.read Velocity + A/B/C domain rows.
    expect(m.cells.get('component:Position|A')?.accessKind).toBe('write');
    expect(m.cells.get('component:Position|B')?.accessKind).toBe('reads');
    expect(m.cells.get('component:Velocity|B')?.accessKind).toBe('write');
    expect(m.cells.get('component:Velocity|C')?.accessKind).toBe('reads');
    expect(m.cells.get('component:Position|C')).toBeUndefined();
    expect(m.cells.get('component:Velocity|A')).toBeUndefined();
  });
});
