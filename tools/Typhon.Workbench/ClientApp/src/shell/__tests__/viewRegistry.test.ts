import { describe, expect, it } from 'vitest';
import { ANY_ZONE_D_VIEW_ACTIVE, ZONE_D_VIEW_ACTIVE, isViewActive } from '../viewRegistry';

// The deep/workspace (zone-D) views still gated off. Kept here as the test's own copy so a regression
// (a view silently flipped back on, or a new deep view added without gating) is caught.
// Stage 2 (GAP-02): SchemaBrowser/ArchetypeBrowser AND the four Schema* deep panels were *removed* —
// consolidated into the Schema Explorer + Archetype/Component Inspectors (see removedSurfaces.test.ts).
const ZONE_D_GATED_OFF = [
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

// Zone-D views reintroduced (flipped on) by Stages 2-4. Listed so a *de*activation regression is caught too.
// Stage 2 Phase 2: Data Browser. Stage 2 Phase 3: File Map + Storage Health (the storage drill).
const ZONE_D_ACTIVE = ['DataBrowserEntities', 'DbMap', 'StorageHealth'] as const;

// The full registry key set = gated-off ∪ active. Used to assert the registry covers exactly the documented set.
const ZONE_D_VIEW_IDS = [...ZONE_D_GATED_OFF, ...ZONE_D_ACTIVE] as const;

// Shell-structural surfaces that must always remain reachable (not in the registry → always-on). The
// Schema Explorer is the Open default workspace — always-on, like the navigators (Stage 2).
const SHELL_VIEW_IDS = ['ResourceTree', 'SchemaExplorer', 'Detail', 'Logs', 'Options', 'PaletteDebug'] as const;

describe('viewRegistry — Stage 0 deactivation gate', () => {
  it('keeps the still-deferred zone-D views inactive', () => {
    for (const id of ZONE_D_GATED_OFF) {
      expect(isViewActive(id), `${id} should still be deactivated`).toBe(false);
    }
  });

  it('marks the reintroduced zone-D views active (Stage 2+)', () => {
    for (const id of ZONE_D_ACTIVE) {
      expect(isViewActive(id), `${id} should be reintroduced (active)`).toBe(true);
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

  it('reports a zone-D view active (drives the View-menu separator once views return)', () => {
    expect(ANY_ZONE_D_VIEW_ACTIVE).toBe(true);
  });
});
