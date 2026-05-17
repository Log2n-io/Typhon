import { describe, expect, it } from 'vitest';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import type { ArchetypeDto } from '@/api/generated/model/archetypeDto';
import { buildTracks } from '../trackBuilding';

function topo(overrides: Partial<TopologyDto> = {}): TopologyDto {
  return {
    systems: [],
    archetypes: [],
    componentTypes: [],
    phases: [],
    tracks: [],
    componentFamilies: { componentToFamily: {}, familyOrder: [] },
    ...overrides,
  };
}

function arch(id: number, label: string, components: string[]): ArchetypeDto {
  return {
    archetypeId: id,
    name: label,
    label,
    schemaRevision: 1,
    componentTypeNames: components,
  };
}

describe('buildTracks — null / empty topology', () => {
  it('returns [] when topology is null', () => {
    expect(buildTracks(null, 'L0')).toEqual([]);
    expect(buildTracks(null, 'L4')).toEqual([]);
  });
});

describe('buildTracks — L0 Domain', () => {
  it('always returns the three fixed domain rows', () => {
    const result = buildTracks(topo(), 'L0');
    expect(result).toHaveLength(3);
    expect(result.map((t) => t.label)).toEqual(['Components', 'Event Queues', 'Resources']);
    expect(result.map((t) => t.kind)).toEqual(['component-domain', 'queue-domain', 'resource-domain']);
  });
});

describe('buildTracks — L1 Phase × Domain', () => {
  it('falls back to L0 when topology has no phases', () => {
    const result = buildTracks(topo(), 'L1');
    expect(result).toHaveLength(3);
    expect(result[0].kind).toBe('component-domain');
  });

  it('emits 3 rows per phase, in declared phase order', () => {
    const result = buildTracks(topo({ phases: ['Input', 'Simulation', 'Output'] }), 'L1');
    expect(result).toHaveLength(9);
    // Each phase block is consecutive: Input components → queues → resources, then Simulation, then Output.
    expect(result[0]).toMatchObject({ phaseName: 'Input', kind: 'component-domain' });
    expect(result[3]).toMatchObject({ phaseName: 'Simulation', kind: 'component-domain' });
    expect(result[6]).toMatchObject({ phaseName: 'Output', kind: 'component-domain' });
  });
});

describe('buildTracks — L2 Component-family', () => {
  it('falls back to L1 when fewer than 8 families (D9 auto-fallback)', () => {
    const result = buildTracks(topo({
      phases: ['Sim'],
      componentFamilies: { componentToFamily: {}, familyOrder: ['Spatial', 'Combat'] },
    }), 'L2');
    // L1 fallback for 1 phase × 3 domains = 3 rows
    expect(result).toHaveLength(3);
    expect(result[0].kind).toBe('component-domain');
  });

  it('emits one row per family + queue + resource rows when family count >= 8', () => {
    const families = ['Spatial', 'Combat', 'AI', 'Inventory', 'Rendering', 'Networking', 'Input', 'Misc'];
    const result = buildTracks(topo({
      componentFamilies: { componentToFamily: {}, familyOrder: families },
    }), 'L2');
    expect(result).toHaveLength(families.length + 2);  // families + queue domain + resource domain
    for (let i = 0; i < families.length; i++) {
      expect(result[i]).toMatchObject({ kind: 'component-family', familyName: families[i] });
    }
    expect(result[families.length].kind).toBe('queue-domain');
    expect(result[families.length + 1].kind).toBe('resource-domain');
  });
});

describe('buildTracks — L3 Component type', () => {
  it('falls back to L2 when topology.componentTypes is empty', () => {
    const result = buildTracks(topo({
      componentFamilies: { componentToFamily: {}, familyOrder: ['Spatial'] },
    }), 'L3');
    // L2 falls back to L1 (only 1 family, < 8), L1 falls back to L0 (no phases) — 3 rows
    expect(result).toHaveLength(3);
  });

  it('emits one row per component type in declaration order', () => {
    const result = buildTracks(topo({
      componentTypes: [
        { componentTypeId: 1, name: 'Position' },
        { componentTypeId: 2, name: 'Velocity' },
        { componentTypeId: 3, name: 'Health' },
      ],
    }), 'L3');
    expect(result).toHaveLength(5); // 3 components + queue + resource domains
    expect(result.slice(0, 3).map((t) => t.label)).toEqual(['Position', 'Velocity', 'Health']);
    expect(result[0]).toMatchObject({ kind: 'component', componentName: 'Position' });
  });
});

describe('buildTracks — L4 Archetype × Component', () => {
  it('emits one row per (archetype, component) pair', () => {
    const result = buildTracks(topo({
      archetypes: [
        arch(100, 'Ant', ['Position', 'Velocity']),
        arch(101, 'Food', ['Position', 'Health']),
      ],
    }), 'L4');
    // 2 archetypes × 2 components each = 4 archetype-component rows + 2 domain rows
    expect(result).toHaveLength(6);
    expect(result[0]).toMatchObject({
      kind: 'archetype-component',
      archetypeId: 100,
      componentName: 'Position',
      label: 'Position on Ant',
    });
    expect(result[2]).toMatchObject({ archetypeId: 101, componentName: 'Position', label: 'Position on Food' });
  });

  it('returns just the domain fallback rows when archetypes is empty', () => {
    const result = buildTracks(topo(), 'L4');
    expect(result).toHaveLength(2); // queue + resource domain only — keeps timeline non-empty
    expect(result.map((t) => t.kind)).toEqual(['queue-domain', 'resource-domain']);
  });
});

describe('buildTracks — track ids are stable + unique within a level', () => {
  it.each(['L0', 'L1', 'L2', 'L3', 'L4'] as const)('level %s yields unique ids', (level) => {
    const result = buildTracks(topo({
      phases: ['A', 'B'],
      componentTypes: [
        { componentTypeId: 1, name: 'C1' }, { componentTypeId: 2, name: 'C2' },
      ],
      archetypes: [arch(10, 'X', ['C1', 'C2']), arch(11, 'Y', ['C1'])],
      componentFamilies: { componentToFamily: { C1: 'F1', C2: 'F2' }, familyOrder: ['F1', 'F2', 'F3', 'F4', 'F5', 'F6', 'F7', 'F8'] },
    }), level);
    const ids = result.map((t) => t.id);
    expect(new Set(ids).size).toBe(ids.length);
  });
});
