import { describe, expect, it } from 'vitest';
import { ANY_ZONE_D_VIEW_ACTIVE, ZONE_D_VIEW_ACTIVE, isViewActive } from '../viewRegistry';

// The deep/workspace (zone-D) views deactivated in Stage 0. Kept here as the test's own copy so a regression
// (a view silently flipped back on, or a new deep view added without gating) is caught.
const ZONE_D_VIEW_IDS = [
  'SchemaBrowser',
  'ArchetypeBrowser',
  'SchemaLayout',
  'SchemaArchetypes',
  'SchemaIndexes',
  'SchemaRelationships',
  'DataBrowserEntities',
  'DbMap',
  'Profiler',
  'TopSpans',
  'CallTree',
  'SourcePreview',
  'SystemDag',
  'CriticalPath',
  'DataFlow',
  'AccessMatrix',
  'QueryCatalog',
  'QueryPlanTree',
  'ExecutionInspector',
] as const;

// Shell-structural surfaces that must always remain reachable (not in the registry → always-on).
const SHELL_VIEW_IDS = ['ResourceTree', 'Detail', 'Logs', 'Options', 'PaletteDebug'] as const;

describe('viewRegistry — Stage 0 deactivation gate', () => {
  it('marks every zone-D view inactive', () => {
    for (const id of ZONE_D_VIEW_IDS) {
      expect(isViewActive(id), `${id} should be deactivated in Stage 0`).toBe(false);
    }
  });

  it('keeps shell-structural views active (unlisted ids are always-on)', () => {
    for (const id of SHELL_VIEW_IDS) {
      expect(isViewActive(id), `${id} should stay reachable`).toBe(true);
    }
  });

  it('treats an undefined viewId (non-view command) as active', () => {
    expect(isViewActive(undefined)).toBe(true);
  });

  it('treats an unknown id as active (fail-open for shell chrome)', () => {
    expect(isViewActive('SomethingNew')).toBe(true);
  });

  it('registry covers exactly the documented zone-D set', () => {
    expect(Object.keys(ZONE_D_VIEW_ACTIVE).sort()).toEqual([...ZONE_D_VIEW_IDS].sort());
  });

  it('reports no zone-D view active (drives the View-menu separator)', () => {
    expect(ANY_ZONE_D_VIEW_ACTIVE).toBe(false);
  });
});
