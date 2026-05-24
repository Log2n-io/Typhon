import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DockviewApi } from 'dockview-react';
import { registerDockApi, focusNextPanel, focusPrevPanel, focusPanelBody } from '@/shell/commands/openSchemaBrowser';

// Conformance suite F (Stage-1 keyboard part): F6/Shift+F6 cycle focus across EVERY docked pane — edge
// groups (nav/inspector/logs) AND the stacked tabs of a center group.
//
// REGRESSION GUARD 1 (why focus lands on the body, not just the group): `panel.focus()` only flips the
// active group; the cue (`:focus-visible`) needs DOM focus inside the panel — so we assert `focusPanelBody`
// focuses the group's `.dv-content-container`.
//
// REGRESSION GUARD 2 (the "F6 stuck" bug): the current stop must be the active *panel*, not the active
// *group*. A stacked group shares `.dv-active-group` across all its tabs, so keying on the group class
// landed on the group's first panel every cycle and F6 ping-ponged on two tabs, never traversing the rest.
// The stacked-group test below fails if cycling regresses to group-granular detection.

interface FakeBody {
  focus: ReturnType<typeof vi.fn>;
}
interface FakePanel {
  id: string;
  focus: ReturnType<typeof vi.fn>;
  api: { group: FakeGroup };
}
interface FakeGroup {
  element: {
    classList: { contains: (c: string) => boolean };
    getBoundingClientRect: () => { left: number; top: number };
    querySelector: (sel: string) => FakeBody | null;
  };
  api: { isCollapsed: () => boolean; expand: ReturnType<typeof vi.fn> };
  panels: FakePanel[];
  activePanel: FakePanel | null;
  body: FakeBody;
}

interface GroupSpec {
  left: number;
  active?: boolean;
  collapsed?: boolean;
  /** Panel ids in tab order; first is the group's active tab unless `activeId` overrides. */
  panels: string[];
  activeId?: string;
}

/** Build a fake dockview api: one or more groups, each holding one or more stacked panels (tabs). */
function setup(specs: GroupSpec[]) {
  const byId = new Map<string, FakePanel>();
  const groups: FakeGroup[] = specs.map((spec) => {
    const body: FakeBody = { focus: vi.fn() };
    const group: FakeGroup = {
      element: {
        classList: { contains: (c) => c === 'dv-active-group' && !!spec.active },
        getBoundingClientRect: () => ({ left: spec.left, top: 0 }),
        querySelector: (sel) => (sel === '.dv-content-container' ? body : null),
      },
      api: { isCollapsed: () => !!spec.collapsed, expand: vi.fn() },
      panels: [],
      activePanel: null,
      body,
    };
    for (const id of spec.panels) {
      const panel: FakePanel = { id, focus: vi.fn(), api: { group } };
      group.panels.push(panel);
      byId.set(id, panel);
    }
    group.activePanel = group.panels.find((p) => p.id === (spec.activeId ?? spec.panels[0])) ?? null;
    return group;
  });
  registerDockApi({ groups, getPanel: (id: string) => byId.get(id) ?? null } as unknown as DockviewApi);
  return byId;
}

afterEach(() => registerDockApi(null));

describe('suite F — F6 panel cycling', () => {
  it('focusNextPanel moves DOM focus into the next panel (across edge groups)', () => {
    // DOM order by group left: resource-tree(0) → logs(35) → detail(960). Active = resource-tree.
    const p = setup([
      { left: 0, active: true, panels: ['resource-tree'] },
      { left: 35, panels: ['logs'] },
      { left: 960, panels: ['detail'] },
    ]);
    focusNextPanel();
    expect(p.get('logs')!.focus).toHaveBeenCalledTimes(1); // tab activated
    expect(p.get('logs')!.api.group.body.focus).toHaveBeenCalledTimes(1); // DOM focus into the body
    expect(p.get('detail')!.focus).not.toHaveBeenCalled();
  });

  it('steps through the stacked tabs of a group, not just the group (regression: F6 stuck)', () => {
    // One center group (left 300) stacking three tabs, active tab = the MIDDLE one.
    const p = setup([
      { left: 0, panels: ['resource-tree'] },
      { left: 300, active: true, panels: ['schema', 'archetype', 'dbmap'], activeId: 'archetype' },
      { left: 960, panels: ['detail'] },
    ]);
    focusNextPanel();
    // Must advance to the NEXT tab in the same group (archetype → dbmap), not snap back to the group's first.
    expect(p.get('dbmap')!.focus).toHaveBeenCalledTimes(1);
    expect(p.get('schema')!.focus).not.toHaveBeenCalled();
  });

  it('exits a stacked group to the next group once its last tab is active', () => {
    const p = setup([
      { left: 0, panels: ['resource-tree'] },
      { left: 300, active: true, panels: ['schema', 'archetype', 'dbmap'], activeId: 'dbmap' },
      { left: 960, panels: ['detail'] },
    ]);
    focusNextPanel();
    expect(p.get('detail')!.focus).toHaveBeenCalledTimes(1); // last center tab → next group
  });

  it('focusPrevPanel wraps to the last panel and focuses it', () => {
    const p = setup([
      { left: 0, active: true, panels: ['resource-tree'] },
      { left: 35, panels: ['logs'] },
      { left: 960, panels: ['detail'] },
    ]);
    focusPrevPanel();
    expect(p.get('detail')!.focus).toHaveBeenCalledTimes(1);
  });

  it('skips panels in collapsed edge groups', () => {
    const p = setup([
      { left: 0, active: true, panels: ['resource-tree'] },
      { left: 35, collapsed: true, panels: ['logs'] }, // collapsed → skipped
      { left: 960, panels: ['detail'] },
    ]);
    focusNextPanel();
    expect(p.get('logs')!.focus).not.toHaveBeenCalled();
    expect(p.get('detail')!.focus).toHaveBeenCalledTimes(1);
  });

  it('focusPanelBody activates the panel AND focuses its content body', () => {
    const p = setup([{ left: 0, panels: ['detail'] }]);
    const panel = p.get('detail')!;
    focusPanelBody(panel as unknown as NonNullable<ReturnType<DockviewApi['getPanel']>>);
    expect(panel.focus).toHaveBeenCalledTimes(1);
    expect(panel.api.group.body.focus).toHaveBeenCalledTimes(1);
  });

  it('is a safe no-op before the dock api is registered', () => {
    registerDockApi(null);
    expect(() => {
      focusNextPanel();
      focusPrevPanel();
    }).not.toThrow();
  });
});
