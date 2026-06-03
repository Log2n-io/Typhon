// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';
import type { DockviewApi } from 'dockview-react';
import { largestGroupPanelId } from '@/shell/commands/openSchemaBrowser';

// Minimal dockview-group stand-in: largestGroupPanelId reads only group.element.getBoundingClientRect() and
// group.activePanel?.id. We model a layout as {area, activePanelId} tuples.
function mockApi(groups: Array<{ w: number; h: number; panelId?: string }>): DockviewApi {
  return {
    groups: groups.map((g) => ({
      element: { getBoundingClientRect: () => ({ width: g.w, height: g.h }) },
      activePanel: g.panelId ? { id: g.panelId } : undefined,
    })),
  } as unknown as DockviewApi;
}

describe('largestGroupPanelId (#386 QC — center-placement fallback)', () => {
  it('returns the active-panel id of the largest group by area', () => {
    // A DbMap-centered layout: wide center + narrow bottom/edge docks. The center must win.
    const api = mockApi([
      { w: 1100, h: 700, panelId: 'dbmap' }, // center
      { w: 1100, h: 160, panelId: 'logs' }, // bottom dock — where the QC bug placed the panel
      { w: 260, h: 860, panelId: 'detail' }, // right edge
    ]);
    expect(largestGroupPanelId(api)).toBe('dbmap');
  });

  it('ignores groups with no active panel (e.g. collapsed/empty)', () => {
    const api = mockApi([
      { w: 2000, h: 2000 }, // largest by area but has no active panel → skipped
      { w: 800, h: 600, panelId: 'schema-explorer' },
    ]);
    expect(largestGroupPanelId(api)).toBe('schema-explorer');
  });

  it('returns undefined for an empty dock', () => {
    expect(largestGroupPanelId(mockApi([]))).toBeUndefined();
  });
});
