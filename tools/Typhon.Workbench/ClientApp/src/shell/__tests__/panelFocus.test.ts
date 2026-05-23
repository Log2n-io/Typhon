import { afterEach, describe, expect, it, vi } from 'vitest';
import type { DockviewApi } from 'dockview-react';
import { registerDockApi, focusNextPanel, focusPrevPanel, focusPanelBody } from '@/shell/commands/openSchemaBrowser';

// Conformance suite F (Stage-1 keyboard part): F6/Shift+F6 cycle focus across panels — including the
// edge-group panels (nav/inspector/logs) that dockview's own `moveToNext` ignores. Intra-panel roving
// lands per-view in Stages 2-4 (shell-and-dockview §6).
//
// REGRESSION GUARD (why these assertions look the way they do): the first cut of F6 "worked" in a green
// test that only checked `panel.focus()` was *called* — but live, `panel.focus()` only flips the active
// group; it never moves DOM focus into the panel, so `:focus-visible` never fired and F6 looked dead.
// These tests therefore assert the thing that was actually broken: focus lands on the panel's
// `.dv-content-container` *body*, not merely that the panel was activated.

interface FakeBody {
  focus: ReturnType<typeof vi.fn>;
}
interface FakePanel {
  id: string;
  focus: ReturnType<typeof vi.fn>;
  body: FakeBody;
  api: {
    group: {
      element: {
        classList: { contains: (c: string) => boolean };
        getBoundingClientRect: () => { left: number; top: number };
        querySelector: (sel: string) => FakeBody | null;
      };
      api: { isCollapsed: () => boolean };
    };
  };
}

function mkPanel(id: string, left: number, active = false, collapsed = false): FakePanel {
  const body: FakeBody = { focus: vi.fn() };
  return {
    id,
    focus: vi.fn(),
    body,
    api: {
      group: {
        element: {
          classList: { contains: (c: string) => c === 'dv-active-group' && active },
          getBoundingClientRect: () => ({ left, top: 0 }),
          // focusPanelBody resolves the panel body via `.dv-content-container` — mirror that here so
          // the test exercises the real focus path, not just `panel.focus()`.
          querySelector: (sel: string) => (sel === '.dv-content-container' ? body : null),
        },
        api: { isCollapsed: () => collapsed },
      },
    },
  };
}

function registerPanels(panels: Record<string, FakePanel>) {
  registerDockApi({ groups: [], getPanel: (id: string) => panels[id] } as unknown as DockviewApi);
}

afterEach(() => registerDockApi(null));

describe('suite F — F6 panel cycling', () => {
  it('focusNextPanel moves DOM focus into the next panel body (incl. edge groups)', () => {
    // resource-tree active at x0; DOM order by left: resource-tree(0) → logs(35) → detail(960).
    const panels = {
      'resource-tree': mkPanel('resource-tree', 0, true),
      logs: mkPanel('logs', 35),
      detail: mkPanel('detail', 960),
    };
    registerPanels(panels);
    focusNextPanel();
    // The bug-catching assertion: focus must land on the *body*, not just activate the panel.
    expect(panels.logs.body.focus).toHaveBeenCalledTimes(1);
    expect(panels.logs.focus).toHaveBeenCalledTimes(1);
    expect(panels.detail.body.focus).not.toHaveBeenCalled();
  });

  it('focusPrevPanel wraps to the last panel and focuses its body', () => {
    const panels = {
      'resource-tree': mkPanel('resource-tree', 0, true),
      logs: mkPanel('logs', 35),
      detail: mkPanel('detail', 960),
    };
    registerPanels(panels);
    focusPrevPanel();
    expect(panels.detail.body.focus).toHaveBeenCalledTimes(1);
  });

  it('skips panels in collapsed edge groups', () => {
    const panels = {
      'resource-tree': mkPanel('resource-tree', 0, true),
      logs: mkPanel('logs', 35, false, true), // collapsed → skipped
      detail: mkPanel('detail', 960),
    };
    registerPanels(panels);
    focusNextPanel();
    expect(panels.logs.body.focus).not.toHaveBeenCalled();
    expect(panels.detail.body.focus).toHaveBeenCalledTimes(1);
  });

  it('focusPanelBody activates the panel AND focuses its content body', () => {
    const panel = mkPanel('detail', 0);
    focusPanelBody(panel as unknown as NonNullable<ReturnType<DockviewApi['getPanel']>>);
    expect(panel.focus).toHaveBeenCalledTimes(1);
    expect(panel.body.focus).toHaveBeenCalledTimes(1);
  });

  it('is a safe no-op before the dock api is registered', () => {
    registerDockApi(null);
    expect(() => {
      focusNextPanel();
      focusPrevPanel();
    }).not.toThrow();
  });
});
