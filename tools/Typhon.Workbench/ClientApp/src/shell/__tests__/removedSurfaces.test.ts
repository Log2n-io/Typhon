import { describe, expect, it } from 'vitest';
import * as schemaCommands from '@/shell/commands/openSchemaBrowser';
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
