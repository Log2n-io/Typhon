import { describe, expect, it } from 'vitest';
import type { ArchetypeDto } from '@/api/generated/model/archetypeDto';
import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { accessKindFor, buildBars } from '../barBuilding';
import { buildTracks } from '../trackBuilding';

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

function sys(name: string, index: number, phaseName: string = 'Sim', overrides: Partial<SystemDefinitionDto> = {}): SystemDefinitionDto {
  return {
    index, name, type: 0, priority: 0, isParallel: true, tierFilter: 0,
    predecessors: [], successors: [], phaseName, isExclusivePhase: false,
    reads: [], readsFresh: [], readsSnapshot: [], additionalReads: [],
    writes: [], sideWrites: [],
    writesEvents: [], readsEvents: [],
    writesResources: [], readsResources: [],
    explicitAfter: [], explicitBefore: [],
    ...overrides,
  };
}

function arch(id: number, label: string, components: string[]): ArchetypeDto {
  return { archetypeId: id, name: label, label, schemaRevision: 1, componentTypeNames: components };
}

function touch(tick: number, sys: number, arch: number, entities: number = 100, chunks: number = 4): SystemArchetypeTouchSummary {
  return { tickNumber: tick, systemIndex: sys, archetypeId: arch, entityCount: entities, chunkCount: chunks } as unknown as SystemArchetypeTouchSummary;
}

describe('buildBars — empty inputs', () => {
  it('returns [] for empty touches', () => {
    expect(buildBars([], [], topo(), 'L2')).toEqual([]);
  });

  it('returns [] for empty tracks', () => {
    const t = topo({ systems: [sys('Phys', 1)], archetypes: [arch(100, 'Ant', ['Position'])] });
    expect(buildBars([touch(1, 1, 100)], [], t, 'L4')).toEqual([]);
  });

  it('returns [] for null topology', () => {
    expect(buildBars([touch(1, 1, 100)], buildTracks(null, 'L0'), null, 'L0')).toEqual([]);
  });
});

describe('buildBars — L0 fan-out', () => {
  it('emits one bar per touch on the components-domain row', () => {
    const t = topo({
      systems: [sys('Phys', 1)],
      archetypes: [arch(100, 'Ant', ['Position'])],
    });
    const tracks = buildTracks(t, 'L0');
    const bars = buildBars([touch(1, 1, 100), touch(1, 1, 100)], tracks, t, 'L0');
    expect(bars).toHaveLength(2);
    expect(bars[0]).toMatchObject({ trackId: 'domain:components', tickNumber: 1, systemName: 'Phys' });
  });
});

describe('buildBars — L1 fan-out', () => {
  it('routes each touch to the (phase × components) row', () => {
    const t = topo({
      systems: [sys('Phys', 1, 'Simulation'), sys('Render', 2, 'Output')],
      archetypes: [arch(100, 'Ant', ['Position'])],
      phases: ['Input', 'Simulation', 'Output'],
    });
    const tracks = buildTracks(t, 'L1');
    const bars = buildBars(
      [touch(1, 1, 100), touch(1, 2, 100)],
      tracks,
      t,
      'L1',
    );
    expect(bars).toHaveLength(2);
    expect(bars[0].trackId).toBe('phase:Simulation/components');
    expect(bars[1].trackId).toBe('phase:Output/components');
  });

  it('falls back to domain:components when system has no phase', () => {
    const t = topo({ systems: [sys('Phys', 1, '')], archetypes: [arch(100, 'A', ['C'])], phases: ['P'] });
    const bars = buildBars([touch(1, 1, 100)], buildTracks(t, 'L1'), t, 'L1');
    expect(bars).toHaveLength(1);
    expect(bars[0].trackId).toBe('domain:components');
  });
});

describe('buildBars — L2 fan-out (component-family)', () => {
  it('emits one bar per family hit by the archetype components', () => {
    const t = topo({
      systems: [sys('AI', 1)],
      archetypes: [arch(100, 'Ant', ['Position', 'Velocity', 'Health'])],
      componentFamilies: {
        componentToFamily: { Position: 'Spatial', Velocity: 'Spatial', Health: 'Combat' },
        familyOrder: ['Spatial', 'Combat', 'AI', 'Inventory', 'Rendering', 'Networking', 'Input', 'Misc'],
      },
    });
    const tracks = buildTracks(t, 'L2');
    const bars = buildBars([touch(1, 1, 100)], tracks, t, 'L2');
    // Spatial + Combat = 2 bars (Position+Velocity collapse to one Spatial bar via the seen-set).
    expect(bars).toHaveLength(2);
    const ids = bars.map((b) => b.trackId).sort();
    expect(ids).toEqual(['family:Combat', 'family:Spatial']);
  });

  it('skips events whose archetype has no family-mapped components', () => {
    const t = topo({
      systems: [sys('Foo', 1)],
      archetypes: [arch(100, 'Foo', ['Unknown'])],
      componentFamilies: { componentToFamily: {}, familyOrder: ['Spatial', 'Combat', 'AI', 'Inventory', 'Rendering', 'Networking', 'Input', 'Misc'] },
    });
    const tracks = buildTracks(t, 'L2');
    expect(buildBars([touch(1, 1, 100)], tracks, t, 'L2')).toEqual([]);
  });
});

describe('buildBars — L3 fan-out (component)', () => {
  it('emits one bar per component on the archetype', () => {
    const t = topo({
      systems: [sys('Phys', 1)],
      archetypes: [arch(100, 'Ant', ['Position', 'Velocity'])],
      componentTypes: [
        { componentTypeId: 1, name: 'Position' },
        { componentTypeId: 2, name: 'Velocity' },
      ],
    });
    const bars = buildBars([touch(1, 1, 100)], buildTracks(t, 'L3'), t, 'L3');
    expect(bars).toHaveLength(2);
    expect(bars.map((b) => b.trackId).sort()).toEqual(['component:Position', 'component:Velocity']);
  });
});

describe('buildBars — L4 fan-out (archetype, component)', () => {
  it('emits one bar per (archetype, component) pair', () => {
    const t = topo({
      systems: [sys('Phys', 1)],
      archetypes: [
        arch(100, 'Ant', ['Position', 'Velocity']),
        arch(101, 'Food', ['Position', 'Health']),
      ],
    });
    // Two events: one on Ant, one on Food, same tick, same system.
    const bars = buildBars(
      [touch(1, 1, 100), touch(1, 1, 101)],
      buildTracks(t, 'L4'),
      t,
      'L4',
    );
    expect(bars).toHaveLength(4);
    const ids = bars.map((b) => b.trackId).sort();
    expect(ids).toEqual([
      'archcomp:100:Position', 'archcomp:100:Velocity',
      'archcomp:101:Health', 'archcomp:101:Position',
    ]);
  });
});

describe('buildBars — defensive', () => {
  it('skips rows whose systemIndex is unknown to the topology', () => {
    const t = topo({ systems: [sys('Phys', 1)], archetypes: [arch(100, 'A', ['C'])] });
    // sysIdx=99 is not registered → bar dropped.
    const bars = buildBars([touch(1, 99, 100), touch(1, 1, 100)], buildTracks(t, 'L0'), t, 'L0');
    expect(bars).toHaveLength(1);
  });

  it('skips rows whose archetypeId is unknown when level needs the components list', () => {
    const t = topo({ systems: [sys('Phys', 1)], archetypes: [arch(100, 'A', ['Position'])] });
    // archId=200 doesn't exist → no fan-out at L4.
    const bars = buildBars([touch(1, 1, 200)], buildTracks(t, 'L4'), t, 'L4');
    expect(bars).toHaveLength(0);
  });
});

describe('accessKindFor', () => {
  it('write beats reads-fresh beats reads-snapshot beats reads', () => {
    const s = sys('S', 1, 'P', { writes: ['A'], readsFresh: ['B'], readsSnapshot: ['C'], reads: ['D'] });
    expect(accessKindFor(s, 'A')).toBe('write');
    expect(accessKindFor(s, 'B')).toBe('reads-fresh');
    expect(accessKindFor(s, 'C')).toBe('reads-snapshot');
    expect(accessKindFor(s, 'D')).toBe('reads');
    expect(accessKindFor(s, 'Unrelated')).toBe('none');
  });

  it('side-write surfaces when not also a write', () => {
    const s = sys('S', 1, 'P', { sideWrites: ['Z'] });
    expect(accessKindFor(s, 'Z')).toBe('side-write');
  });
});
