import { describe, expect, it } from 'vitest';
import type { ArchetypeInfo, ComponentSummary } from '@/hooks/schema/types';
import { findArchetype, resolveArchetypeComponents, indexedComponents } from '@/panels/ArchetypeInspector/archetypeInspectorModel';

// Stage 2 · Archetype Inspector model (GAP-02). Unit coverage of the bus-id lookup, the component join,
// and the indexed-component derivation the tabs render.

const comp = (over: Partial<ComponentSummary> & { typeName: string; fullName: string }): ComponentSummary => ({
  storageSize: 16,
  fieldCount: 2,
  archetypeCount: 1,
  entityCount: 0,
  indexCount: 0,
  storageMode: 'Versioned',
  ...over,
});

const arch = (over: Partial<ArchetypeInfo> & { archetypeId: string }): ArchetypeInfo => ({
  name: '',
  componentTypes: [],
  entityCount: 0,
  componentSize: 16,
  storageMode: 'cluster',
  chunkCount: 0,
  chunkCapacity: 0,
  occupancyPct: 0,
  ...over,
});

describe('findArchetype', () => {
  const list = [arch({ archetypeId: '800' }), arch({ archetypeId: '801' })];
  it('resolves by id, returns null for missing or null id', () => {
    expect(findArchetype(list, '801')?.archetypeId).toBe('801');
    expect(findArchetype(list, '999')).toBeNull();
    expect(findArchetype(list, null)).toBeNull();
  });
});

describe('resolveArchetypeComponents', () => {
  it('joins component types to summaries; unresolved types still listed with a fallback name', () => {
    const components = [comp({ typeName: 'Position', fullName: 'Game.Position', storageSize: 12, indexCount: 1 })];
    const a = arch({ archetypeId: '800', componentTypes: ['Game.Position', 'Game.Missing'] });
    const rows = resolveArchetypeComponents(a, components);
    expect(rows.map((r) => r.typeName)).toEqual(['Position', 'Missing']);
    expect(rows[0].summary?.storageSize).toBe(12);
    expect(rows[1].summary).toBeNull();
  });
});

describe('indexedComponents', () => {
  it('keeps only components with an index (unresolved excluded)', () => {
    const components = [
      comp({ typeName: 'Position', fullName: 'Game.Position', indexCount: 1 }),
      comp({ typeName: 'Health', fullName: 'Game.Health', indexCount: 0 }),
    ];
    const a = arch({ archetypeId: '800', componentTypes: ['Game.Position', 'Game.Health', 'Game.Missing'] });
    const rows = resolveArchetypeComponents(a, components);
    expect(indexedComponents(rows).map((r) => r.typeName)).toEqual(['Position']);
  });
});
