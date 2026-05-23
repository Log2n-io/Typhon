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
  'toggle-view-system-dag',
  'toggle-view-data-flow',
  'toggle-view-access-matrix',
  'toggle-view-dbmap',
  'data-browser',
  'toggle-view-source-preview',
  'show-source-current-span',
  'toggle-view-profiler',
  'toggle-view-critical-path',
  'toggle-view-query-catalog',
  'toggle-view-query-plan-tree',
  'toggle-view-execution-inspector',
  'toggle-view-top-spans',
  // profiler-view interaction commands (only meaningful with the Profiler view mounted)
  'profiler-toggle-gauges',
  'profiler-toggle-systems',
  'profiler-zoom-full',
  'profiler-pan-left',
  'profiler-pan-right',
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
