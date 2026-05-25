import { describe, expect, it } from 'vitest';
import * as schemaCommands from '@/shell/commands/openSchemaBrowser';
import * as profilerCommands from '@/shell/commands/profilerCommands';
import { buildBaseCommands } from '@/shell/commands/baseCommands';
import { ZONE_D_VIEW_ACTIVE } from '@/shell/viewRegistry';

// AC2.13 (GAP-02 subtraction) — an *absence* guard. The schema consolidation collapsed six legacy schema
// surfaces (the SchemaBrowser + ArchetypeBrowser navigators and the four Schema* deep panels) into the Schema
// Explorer + Archetype/Component Inspectors. This test fails if any of those surfaces is reintroduced — a class
// of regression ordinary presence assertions can't catch (you cannot assert on what must NOT exist by rendering
// it). It checks the three observable footprints the old surfaces left: command exports, the view registry, and
// the command palette.

const REMOVED_COMMAND_EXPORTS = [
  'toggleViewSchemaLayout',
  'toggleViewSchemaArchetypes',
  'toggleViewSchemaIndexes',
  'toggleViewSchemaRelationships',
  'toggleViewComponentBrowser',
  'toggleViewArchetypeBrowser',
] as const;

const REMOVED_VIEW_IDS = ['SchemaLayout', 'SchemaArchetypes', 'SchemaIndexes', 'SchemaRelationships', 'SchemaBrowser', 'ArchetypeBrowser'] as const;

const REMOVED_PALETTE_IDS = ['toggle-view-schema-archetypes', 'toggle-view-schema-indexes', 'toggle-view-schema-relationships'] as const;

describe('AC2.13 — GAP-02 subtraction (removed schema surfaces stay removed)', () => {
  it('exports no toggle command for a removed schema surface', () => {
    const exports = schemaCommands as Record<string, unknown>;
    for (const name of REMOVED_COMMAND_EXPORTS) {
      expect(name in exports, `${name} should be gone from openSchemaBrowser commands`).toBe(false);
    }
  });

  it('registers no view id for a removed schema surface', () => {
    for (const id of REMOVED_VIEW_IDS) {
      expect(id in ZONE_D_VIEW_ACTIVE, `${id} should not be a gated/registered view`).toBe(false);
    }
  });

  it('offers no command-palette toggle for a removed schema panel', () => {
    const ids = buildBaseCommands().map((c) => c.id);
    for (const id of REMOVED_PALETTE_IDS) {
      expect(ids, `${id} should be gone from the palette`).not.toContain(id);
    }
  });
});

// AC3.13 (Stage-3 Phase 4D-2, GAP-19 subtraction) — the Query Analyzer consolidation deleted the three
// legacy query panels (Query Catalog + Query Plan Tree + Execution Inspector). Their reused leaf modules
// (catalog filters/toolbar, plan graph, phase table, data hooks) were *relocated* into panels/QueryAnalyzer/;
// the panels, their view-specific stores, and their commands are gone. Reintroducing any is the regression.
const REMOVED_QUERY_COMMAND_EXPORTS = [
  'toggleViewQueryCatalog',
  'openViewQueryCatalog',
  'openViewQueryPlanTree',
  'toggleViewQueryPlanTree',
  'openViewExecutionInspector',
  'toggleViewExecutionInspector',
] as const;

const REMOVED_QUERY_VIEW_IDS = ['QueryCatalog', 'QueryPlanTree', 'ExecutionInspector'] as const;

const REMOVED_QUERY_PALETTE_IDS = ['toggle-view-query-catalog', 'toggle-view-query-plan-tree', 'toggle-view-execution-inspector'] as const;

const REMOVED_QUERY_MODULES = [
  '@/panels/QueryCatalog/QueryCatalogPanel',
  '@/panels/QueryPlanTree/QueryPlanTreePanel',
  '@/panels/ExecutionInspector/ExecutionInspectorPanel',
  '@/panels/QueryPlanTree/useQueryPlanStore',
  '@/panels/ExecutionInspector/useExecutionInspectorStore',
];

describe('AC3.13 — GAP-19 subtraction (Query Catalog / Plan Tree / Execution Inspector stay removed)', () => {
  it('exports no toggle/open command for a removed query panel', () => {
    const exports = profilerCommands as Record<string, unknown>;
    for (const name of REMOVED_QUERY_COMMAND_EXPORTS) {
      expect(name in exports, `${name} should be gone from profilerCommands`).toBe(false);
    }
  });

  it('registers no view id for a removed query panel', () => {
    for (const id of REMOVED_QUERY_VIEW_IDS) {
      expect(id in ZONE_D_VIEW_ACTIVE, `${id} should not be a registered view`).toBe(false);
    }
  });

  it('offers no command-palette toggle for a removed query panel', () => {
    const ids = buildBaseCommands().map((c) => c.id);
    for (const id of REMOVED_QUERY_PALETTE_IDS) {
      expect(ids, `${id} should be gone from the palette`).not.toContain(id);
    }
  });

  it('the old query-panel modules no longer resolve', async () => {
    for (const modulePath of REMOVED_QUERY_MODULES) {
      await expect(import(/* @vite-ignore */ modulePath), `${modulePath} should be deleted`).rejects.toBeTruthy();
    }
  });
});

// AC3.15 (Stage-3 Phase 3E) — the profiler-selection silo retirement is also an *absence* guard. The legacy
// `useProfilerSelectionStore` was a strangler mirror; once every consumer moved to the unified bus leaf it was
// deleted. Re-introducing it (the file returning, or a re-export) would re-create the dual-write hazard. The
// import-deletion is already compiler-enforced (tsc/build fail on a stray import); this is the runtime backstop.
describe('AC3.15 — profiler-selection silo retirement (stays retired)', () => {
  it('the legacy useProfilerSelectionStore module no longer resolves', async () => {
    const legacyPath = '@/stores/useProfilerSelectionStore';
    await expect(import(/* @vite-ignore */ legacyPath)).rejects.toBeTruthy();
  });
});
