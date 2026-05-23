import { describe, expect, it } from 'vitest';
import { parsePaletteMode, parseJump } from '@/shell/commands/paletteRouting';
import { buildObjectHits, type ObjectSources } from '@/shell/commands/objectHits';

// Conformance suite C — command palette (IA §4, GAP-08). Pure routing + object-resolution laws.

const SOURCES: ObjectSources = {
  resources: [{ id: 'r1', name: 'Storage', kind: 'Storage', path: ['Storage'], raw: {} }],
  components: [{ typeName: 'Position' }, { typeName: 'Velocity' }],
  archetypes: [{ archetypeId: '2002', componentTypes: ['Position', 'Velocity'] }],
  systems: [{ index: 0, name: 'Movement' }, { index: 1, name: 'Damage' }],
  queries: [{ instanceId: { kind: 1, localId: 4 } }],
};

describe('suite C — palette prefix routing', () => {
  it('routes each prefix to its mode (C.1)', () => {
    expect(parsePaletteMode('reset').mode).toBe('command');
    expect(parsePaletteMode('>reset').mode).toBe('action');
    expect(parsePaletteMode('@Pos').mode).toBe('object-session');
    expect(parsePaletteMode('#Pos').mode).toBe('object-global');
    expect(parsePaletteMode(':tick 4').mode).toBe('jump');
    expect(parsePaletteMode('?').mode).toBe('help');
  });

  it('strips the prefix and trims the query', () => {
    expect(parsePaletteMode('@  Position ').query).toBe('Position');
    expect(parsePaletteMode('>Reset Layout').query).toBe('Reset Layout');
  });

  it('parses jump targets', () => {
    expect(parseJump('tick 8412')).toEqual({ kind: 'tick', value: 8412 });
    expect(parseJump('page 1024')).toEqual({ kind: 'page', value: 1024 });
    expect(parseJump('PAGE 7')).toEqual({ kind: 'page', value: 7 });
    expect(parseJump('nonsense')).toBeNull();
  });
});

describe('suite C — object resolution', () => {
  it('resolves each object type to the correct bus write (C.2)', () => {
    const hits = buildObjectHits('', SOURCES, 'trace');
    const byType = (t: string) => hits.find((h) => h.type === t);
    expect(byType('component')?.ref).toBe('Position');
    expect(byType('archetype')?.ref).toBe('2002');
    expect(byType('system')?.ref).toBe('Movement');
    expect(byType('query')?.ref).toEqual({ kind: 1, localId: 4 });
  });

  it('emits groups in the canonical order (C.3)', () => {
    const groups = buildObjectHits('', SOURCES, 'open').map((h) => h.group);
    // Open kind → Resources, Components, Archetypes (no Systems/Queries).
    expect([...new Set(groups)]).toEqual(['Resources', 'Components', 'Archetypes']);
  });

  it('filters by session kind (C.4)', () => {
    const openGroups = new Set(buildObjectHits('', SOURCES, 'open').map((h) => h.group));
    expect(openGroups.has('Resources')).toBe(true);
    expect(openGroups.has('Systems')).toBe(false); // Systems absent in an Open session

    const traceGroups = new Set(buildObjectHits('', SOURCES, 'trace').map((h) => h.group));
    expect(traceGroups.has('Systems')).toBe(true);
    expect(traceGroups.has('Resources')).toBe(false); // Resources absent in a Trace session

    const attachGroups = new Set(buildObjectHits('', SOURCES, 'attach').map((h) => h.group));
    expect(attachGroups).toEqual(new Set(['Systems', 'Queries']));
  });

  it('substring-filters within the query', () => {
    const hits = buildObjectHits('Move', SOURCES, 'trace');
    expect(hits.map((h) => h.label)).toContain('Movement');
    expect(hits.map((h) => h.label)).not.toContain('Damage');
  });

  it('returns nothing for a session with no objects', () => {
    expect(buildObjectHits('x', SOURCES, 'none')).toEqual([]);
  });
});
