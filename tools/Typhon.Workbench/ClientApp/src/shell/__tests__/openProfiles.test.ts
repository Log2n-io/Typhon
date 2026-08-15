import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DockviewApi } from 'dockview-react';
import { registerDockApi, openProfiles } from '@/shell/commands/openSchemaBrowser';

// The Profile sessions list is a NAVIGATOR — you pick a capture from it and the result appears elsewhere — so it docks
// left beside Resources rather than in the centre workspace. These guard the placement, because a no-position addPanel
// silently joins whatever group happened to be active, which is the recurring "opened into a cramped strip" bug.

interface AddCall {
  id: string;
  component: string;
  title: string;
  position?: { referencePanel?: string; direction?: string };
}

function setup(opts: { hasResourceTree?: boolean; existing?: boolean; leftCollapsed?: boolean } = {}) {
  const added: AddCall[] = [];
  const focus = vi.fn();
  const expandGroup = vi.fn();
  const expandEdge = vi.fn();

  const existingPanel = {
    id: 'profiles',
    focus,
    api: { group: { api: { isCollapsed: () => !!opts.leftCollapsed, expand: expandGroup } } },
  };
  const resourceTree = { id: 'resource-tree' };

  const panels = new Map<string, unknown>();
  if (opts.existing) panels.set('profiles', existingPanel);
  if (opts.hasResourceTree !== false) panels.set('resource-tree', resourceTree);

  registerDockApi({
    groups: [],
    getPanel: (id: string) => panels.get(id) ?? null,
    getEdgeGroup: (side: string) =>
      side === 'left' ? { isCollapsed: () => !!opts.leftCollapsed, expand: expandEdge } : undefined,
    addPanel: (spec: AddCall) => {
      added.push(spec);
      return { id: spec.id, focus };
    },
  } as unknown as DockviewApi);

  return { added, focus, expandGroup, expandEdge };
}

afterEach(() => registerDockApi(null));

describe('openProfiles — placement', () => {
  it('docks beside the Resource Tree, so it lands in the left navigator group', () => {
    const { added } = setup();
    openProfiles();
    expect(added).toHaveLength(1);
    expect(added[0].position).toEqual({ referencePanel: 'resource-tree', direction: 'within' });
  });

  it('is titled "Profile sessions"', () => {
    const { added } = setup();
    openProfiles();
    expect(added[0].title).toBe('Profile sessions');
    expect(added[0].component).toBe('Profiles'); // the component id is layout-persisted — renaming it would break restores
  });

  it('expands a collapsed left edge before adding, so the panel is not created behind a closed rail', () => {
    const { expandEdge } = setup({ leftCollapsed: true });
    openProfiles();
    expect(expandEdge).toHaveBeenCalled();
  });

  it('lets dockview place it when the Resource Tree has been closed — never a hard failure', () => {
    const { added } = setup({ hasResourceTree: false });
    openProfiles();
    expect(added).toHaveLength(1);
    expect(added[0].position).toBeUndefined();
  });

  it('focuses an already-open panel instead of adding a second one', () => {
    const { added, focus, expandGroup } = setup({ existing: true, leftCollapsed: true });
    openProfiles();
    expect(added).toHaveLength(0);
    expect(expandGroup).toHaveBeenCalled();
    expect(focus).toHaveBeenCalled();
  });
});
