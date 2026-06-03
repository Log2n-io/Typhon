import { describe, expect, it } from 'vitest';
import type { ArchetypeInfo, ComponentSummary } from '@/hooks/schema/types';
import {
  buildArchetypeLabelMap,
  buildArchetypeTree,
  filterArchetypeTree,
  applyArchetypeFilters,
  applyComponentFilters,
  sortComponents,
} from '@/panels/SchemaExplorer/schemaExplorerModel';

// Stage 2 · Schema Explorer model (GAP-02). Unit coverage of the archetype×component join, the SE-2
// "Types counts are totals" resolution, and the filter/sort logic — the data contract the panel renders.

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

describe('buildArchetypeTree', () => {
  it('joins each archetype\'s component types against the component list, with unique ids', () => {
    const components = [comp({ typeName: 'Position', fullName: 'Game.Position', storageSize: 12 })];
    const archetypes = [arch({ archetypeId: '2001', componentTypes: ['Game.Position', 'Game.Missing'] })];

    const tree = buildArchetypeTree(archetypes, components);
    expect(tree).toHaveLength(1);
    expect(tree[0].id).toBe('arch:2001');
    expect(tree[0].children.map((c) => c.id)).toEqual([
      'arch:2001/comp:Game.Position',
      'arch:2001/comp:Game.Missing',
    ]);
    // Resolved child carries its summary…
    expect(tree[0].children[0].summary?.storageSize).toBe(12);
    // …unresolved child still renders, falling back to the stripped name (never hidden).
    expect(tree[0].children[1].summary).toBeNull();
    expect(tree[0].children[1].typeName).toBe('Missing');
  });
});

describe('buildArchetypeLabelMap (friendly archetype names)', () => {
  it('shortens each named archetype to its shortest unique dot-suffix; colliding leaves keep a segment', () => {
    const map = buildArchetypeLabelMap([
      arch({ archetypeId: '800', name: 'Typhon.Workbench.Fixtures.PlayerArch' }),
      arch({ archetypeId: '801', name: 'Game.Combat.Loadout' }),
      arch({ archetypeId: '802', name: 'Game.Spatial.Loadout' }),
    ]);
    expect(map.get('800')).toBe('PlayerArch'); // unique leaf
    expect(map.get('801')).toBe('Combat.Loadout'); // leaf collides → keep one more segment
    expect(map.get('802')).toBe('Spatial.Loadout');
  });

  it('omits implicit/unnamed archetypes (no name to show)', () => {
    const map = buildArchetypeLabelMap([
      arch({ archetypeId: '900', name: '' }),
      arch({ archetypeId: '901', name: 'Game.Named' }),
    ]);
    expect(map.has('900')).toBe(false);
    expect(map.get('901')).toBe('Named');
  });
});

describe('buildArchetypeTree — node label', () => {
  it('uses the friendly name from the label map, falling back to #<id> when unnamed/absent', () => {
    const components = [comp({ typeName: 'Position', fullName: 'Game.Position' })];
    const named = arch({ archetypeId: '800', name: 'Game.PlayerArch', componentTypes: ['Game.Position'] });
    const unnamed = arch({ archetypeId: '801', name: '', componentTypes: ['Game.Position'] });
    const labels = buildArchetypeLabelMap([named, unnamed]);

    const tree = buildArchetypeTree([named, unnamed], components, labels);
    expect(tree[0].label).toBe('PlayerArch'); // named → friendly
    expect(tree[1].label).toBe('#801'); // unnamed → bare id fallback
  });

  it('falls back to #<id> for every node when no label map is passed', () => {
    const tree = buildArchetypeTree([arch({ archetypeId: '2001', name: 'Game.PlayerArch' })], []);
    expect(tree[0].label).toBe('#2001');
  });
});

describe('filterArchetypeTree', () => {
  const tree = buildArchetypeTree(
    [
      arch({ archetypeId: '1', componentTypes: ['Game.Position'] }),
      arch({ archetypeId: '2', componentTypes: ['Game.Health', 'Game.Position'] }),
    ],
    [
      comp({ typeName: 'Position', fullName: 'Game.Position' }),
      comp({ typeName: 'Health', fullName: 'Game.Health' }),
    ],
  );

  it('returns the tree unchanged for an empty query', () => {
    expect(filterArchetypeTree(tree, '   ')).toBe(tree);
  });

  it('keeps only matching component children when a component name matches', () => {
    const out = filterArchetypeTree(tree, 'health');
    expect(out).toHaveLength(1);
    expect(out[0].archetype.archetypeId).toBe('2');
    expect(out[0].children.map((c) => c.typeName)).toEqual(['Health']);
  });

  it('keeps the whole archetype (all children) on an id match', () => {
    const out = filterArchetypeTree(tree, '2');
    expect(out).toHaveLength(1);
    expect(out[0].children).toHaveLength(2);
  });
});

describe('applyArchetypeFilters', () => {
  const list = [
    arch({ archetypeId: '1', entityCount: 0, storageMode: 'cluster' }),
    arch({ archetypeId: '2', entityCount: 5, storageMode: 'legacy' }),
  ];
  it('filters empty archetypes', () => {
    expect(applyArchetypeFilters(list, { noEntities: true }).map((a) => a.archetypeId)).toEqual(['1']);
  });
  it('filters legacy-only', () => {
    expect(applyArchetypeFilters(list, { legacy: true }).map((a) => a.archetypeId)).toEqual(['2']);
  });
});

describe('applyComponentFilters', () => {
  const list = [
    comp({ typeName: 'A', fullName: 'A', entityCount: 0, indexCount: 0, storageSize: 200 }),
    comp({ typeName: 'B', fullName: 'B', entityCount: 9, indexCount: 2, storageSize: 16 }),
  ];
  it('noEntities / noIndexes / large / indexed', () => {
    expect(applyComponentFilters(list, { noEntities: true }).map((c) => c.typeName)).toEqual(['A']);
    expect(applyComponentFilters(list, { noIndexes: true }).map((c) => c.typeName)).toEqual(['A']);
    expect(applyComponentFilters(list, { large: true }).map((c) => c.typeName)).toEqual(['A']);
    expect(applyComponentFilters(list, { indexed: true }).map((c) => c.typeName)).toEqual(['B']);
  });
});

describe('sortComponents', () => {
  const list = [
    comp({ typeName: 'Beta', fullName: 'Beta', entityCount: 5, archetypeCount: null }),
    comp({ typeName: 'Alpha', fullName: 'Alpha', entityCount: 50, archetypeCount: 3 }),
  ];
  it('sorts by name (string) and by count (number), both directions', () => {
    expect(sortComponents(list, 'typeName', 'asc').map((c) => c.typeName)).toEqual(['Alpha', 'Beta']);
    expect(sortComponents(list, 'entityCount', 'desc').map((c) => c.typeName)).toEqual(['Alpha', 'Beta']);
    expect(sortComponents(list, 'entityCount', 'asc').map((c) => c.typeName)).toEqual(['Beta', 'Alpha']);
  });
  it('treats a null archetypeCount as 0 when sorting', () => {
    expect(sortComponents(list, 'archetypeCount', 'asc').map((c) => c.typeName)).toEqual(['Beta', 'Alpha']);
  });
});
