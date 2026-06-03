import { describe, expect, it } from 'vitest';
import {
  ANY_ZONE_D_VIEW_ACTIVE,
  ZONE_D_VIEW_ACTIVE,
  isViewActive,
  isViewAvailableInKind,
  isViewVisible,
  viewSessionScope,
} from '../viewRegistry';

// The deep/workspace (zone-D) views still gated off. Kept here as the test's own copy so a regression
// (a view silently flipped back on, or a new deep view added without gating) is caught.
// Stage 2 (GAP-02): SchemaBrowser/ArchetypeBrowser AND the four Schema* deep panels were *removed* —
// consolidated into the Schema Explorer + Archetype/Component Inspectors (see removedSurfaces.test.ts).
// After 4D-2 (GAP-19) no zone-D view remains gated-off — the former query panels were *deleted*, not gated
// (guarded in removedSurfaces.test.ts). Kept as an explicit empty list so newly gating a view is a conscious add.
const ZONE_D_GATED_OFF = [] as const;

// Zone-D views reintroduced (flipped on) by Stages 2-4. Listed so a *de*activation regression is caught too.
// Stage 2 Phase 2: Data Browser. Stage 2 Phase 3: File Map + Storage Health. Stage 3 Phase 1: Profiler timeline + Top
// Spans. Stage 3 Phase 2: Call Tree + Source Preview (the span→cause drill). Stage 3 Phase 3 (3A): Data Flow —
// absorbing the former Access Matrix as its in-panel Matrix mode (AccessMatrix is removed from the registry, not gated).
// Stage 3 Phase 3 (3D): System DAG + Critical Path — the rest of the scheduling cluster (bus-driven, one selection).
// Stage 3 Phase 4 (4B+4C, GAP-19): Query Analyzer — consolidates Catalog + Plan Tree + Execution Inspector.
// Stage 4 Phase 1 (#377, GAP-21/22): Engine Live Health — the consolidated live-attach surface (P1 shell + P2+ gauges/anomalies/Capture).
// Out-of-stage: DevFixture — the standalone fixture-creation panel (formerly the Connect-dialog tab). DEBUG-only
// on the server; client always-active so the View menu / palette entries render in dev builds, with the panel
// itself handling the "not available" cold state when the server's #if DEBUG endpoints are absent.
const ZONE_D_ACTIVE = [
  'DataBrowserEntities', 'DbMap', 'StorageHealth', 'Profiler', 'TopSpans', 'CallTree', 'SourcePreview', 'DataFlow', 'SystemDag', 'CriticalPath', 'QueryAnalyzer', 'QueryConsole', 'EngineLiveHealth', 'DevFixture',
] as const;

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

// IA §5.1 — the single source of truth for "which session kind can open which view", shared by the View menu and
// the command palette so the two can't drift.
describe('viewRegistry — session-kind scope (IA §5.1)', () => {
  it('classifies views by the session kind that can open them', () => {
    expect(viewSessionScope('DbMap')).toBe('open');
    expect(viewSessionScope('SchemaExplorer')).toBe('open');
    expect(viewSessionScope('ResourceTree')).toBe('open');
    expect(viewSessionScope('Profiler')).toBe('profiler');
    expect(viewSessionScope('SystemsQueriesNav')).toBe('profiler');
    expect(viewSessionScope('QueryAnalyzer')).toBe('profiler');
    expect(viewSessionScope('DevFixture')).toBe('any');
  });

  it('defaults unlisted / undefined ids to `any` (shell-structural + non-view commands)', () => {
    expect(viewSessionScope(undefined)).toBe('any');
    expect(viewSessionScope('NotARealView')).toBe('any');
  });

  it('isViewAvailableInKind — open views run only in an open session', () => {
    expect(isViewAvailableInKind('DbMap', 'open')).toBe(true);
    expect(isViewAvailableInKind('DbMap', 'attach')).toBe(false);
    expect(isViewAvailableInKind('DbMap', 'none')).toBe(false);
  });

  it('isViewAvailableInKind — profiler views run in trace and attach', () => {
    expect(isViewAvailableInKind('Profiler', 'trace')).toBe(true);
    expect(isViewAvailableInKind('Profiler', 'attach')).toBe(true);
    expect(isViewAvailableInKind('Profiler', 'open')).toBe(false);
  });

  it('isViewAvailableInKind — `any` views (and non-view commands) run in every kind', () => {
    for (const kind of ['open', 'attach', 'trace', 'none'] as const) {
      expect(isViewAvailableInKind('DevFixture', kind)).toBe(true);
      expect(isViewAvailableInKind(undefined, kind)).toBe(true);
    }
  });

  it('isViewVisible requires BOTH feature-active and in-scope for the session', () => {
    expect(isViewVisible('DbMap', 'open')).toBe(true); // active + in scope
    expect(isViewVisible('DbMap', 'attach')).toBe(false); // active but out of scope
    expect(isViewVisible('Profiler', 'open')).toBe(false); // active but wrong kind
    expect(isViewVisible('Profiler', 'attach')).toBe(true);
  });
});
