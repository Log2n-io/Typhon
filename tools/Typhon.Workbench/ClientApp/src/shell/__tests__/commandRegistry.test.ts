// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import { buildBaseCommands } from '../commands/baseCommands';
import { isViewActive } from '../viewRegistry';

// Command ids bound to a now-deactivated zone-D view — none of these may appear in the Stage 0 palette.
const GATED_COMMAND_IDS = [
  'toggle-view-component-browser',
  'toggle-view-archetype-browser',
  'toggle-view-schema-archetypes',
  'toggle-view-schema-indexes',
  'toggle-view-schema-relationships',
];

// Commands whose bound zone-D view has been reintroduced (Stage 2+) — they must now appear in the palette.
const ACTIVE_ZONE_D_COMMAND_IDS = [
  'data-browser', // Data Browser reintroduced onto the bus (Stage 2 Phase 2).
  'toggle-view-dbmap', // File Map reintroduced (Stage 2 Phase 3).
  'toggle-view-storage-health', // Storage Health dashboard (Stage 2 Phase 3).
  // Stage 3 Phase 1: Profiler timeline + Top Spans reintroduced — their toggles + the Profiler-view
  // interaction commands (gauges / per-system lanes / zoom / pan) now surface.
  'toggle-view-profiler',
  'toggle-view-top-spans',
  'profiler-toggle-gauges',
  'profiler-toggle-systems',
  'profiler-zoom-full',
  'profiler-pan-left',
  'profiler-pan-right',
  // Stage 3 Phase 2: Call Tree + Source Preview reintroduced — their toggles surface.
  'toggle-view-call-tree',
  'toggle-view-source-preview',
  'show-source-current-span',
  // Stage 3 Phase 3 (3A): Data Flow reintroduced, absorbing the Access Matrix as its in-panel Matrix mode.
  'toggle-view-data-flow',
  // Stage 3 Phase 3 (3D): the rest of the scheduling cluster — System DAG + Critical Path.
  'toggle-view-system-dag',
  'toggle-view-critical-path',
  // Stage 3 Phase 4 (4B+4C): the consolidated Query Analyzer.
  'toggle-view-query-analyzer',
];

// Shell commands that must survive the Stage 0 filter.
const SHELL_COMMAND_IDS = [
  'open-file',
  'close-session',
  'refresh-graph',
  'toggle-view-resource-tree',
  'toggle-view-detail',
  'toggle-view-logs',
  'toggle-view-options',
  'save-layout-as-default',
  'reset-layout',
  'toggle-theme',
  'reload',
  'profiler-save-replay',
  'toggle-legends',
];

describe('command palette — Stage 0 view gating', () => {
  const ids = new Set(buildBaseCommands().map((c) => c.id));

  it('omits every command bound to a deactivated view', () => {
    for (const id of GATED_COMMAND_IDS) {
      expect(ids.has(id), `command "${id}" should be filtered out in Stage 0`).toBe(false);
    }
  });

  it('surfaces reintroduced zone-D commands (Stage 2+)', () => {
    for (const id of ACTIVE_ZONE_D_COMMAND_IDS) {
      expect(ids.has(id), `command "${id}" should be available once its view is reintroduced`).toBe(true);
    }
  });

  it('keeps all shell commands', () => {
    for (const id of SHELL_COMMAND_IDS) {
      expect(ids.has(id), `shell command "${id}" should remain`).toBe(true);
    }
  });

  it('drops the dead no-op "about" command', () => {
    expect(ids.has('about')).toBe(false);
  });

  it('never surfaces a command whose bound view is gated', () => {
    for (const cmd of buildBaseCommands()) {
      expect(isViewActive(cmd.viewId), `command "${cmd.id}" leaked a gated view`).toBe(true);
    }
  });
});
