// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { IDockviewPanelProps } from 'dockview-react';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { ZONE_D_VIEW_ACTIVE } from '@/shell/viewRegistry';
import SchemaExplorerPanel from '@/panels/SchemaExplorer/SchemaExplorerPanel';
import ArchetypeInspectorPanel from '@/panels/ArchetypeInspector/ArchetypeInspectorPanel';
import ComponentInspectorPanel from '@/panels/ComponentInspector/ComponentInspectorPanel';
import EntityListPanel from '@/panels/DataBrowser/EntityListPanel';
import StorageHealthPanel from '@/panels/StorageHealth/StorageHealthPanel';

// AC2.11 — per-view conformance, parameterized over the reintroduced Stage-2 views (the conformance doc's
// suites D + E). Each view is rendered in its **cold** state (no session → hooks disabled → empty/loading) and
// must satisfy:
//   • D (PC-2): never a blank panel — a cold view shows a skeleton/sentence/picker, not nothing.
//   • E (PC-6): no broken affordance — no *disabled* control whose label reads Open in / Reveal in / Go to.
// The registry is the enrolment list: a new reintroduced view is added here and inherits the suite. F (focus/
// F6) is covered by panelFocus.test + the per-view keyboard tests (ComponentInspector [ / ]); H (density) by
// density.test (lists read --row-h). The File Map is a Canvas view — DS-1 density-exempt (suite H.2) and not
// jsdom-mountable (2D context); its conformance rides its libs/dbmap unit tests + the handoff matrix.
const CANVAS_EXCLUDED = ['DbMap'];

// ResizeObserver / canvas shims absent in jsdom (the Data Browser auto-page-size observer; any stray canvas).
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: typeof ResizeObserverStub }).ResizeObserver = ResizeObserverStub;

const NO_PROPS = {} as IDockviewPanelProps;

const VIEWS: { id: string; label: string; render: () => React.JSX.Element }[] = [
  { id: 'SchemaExplorer', label: 'Schema Explorer', render: () => <SchemaExplorerPanel {...NO_PROPS} /> },
  { id: 'ArchetypeInspector', label: 'Archetype Inspector', render: () => <ArchetypeInspectorPanel {...NO_PROPS} /> },
  { id: 'ComponentInspector', label: 'Component Inspector', render: () => <ComponentInspectorPanel {...NO_PROPS} /> },
  { id: 'DataBrowserEntities', label: 'Data Browser', render: () => <EntityListPanel {...NO_PROPS} /> },
  { id: 'StorageHealth', label: 'Storage Health', render: () => <StorageHealthPanel {...NO_PROPS} /> },
];

function mount(view: (typeof VIEWS)[number]) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(<QueryClientProvider client={client}>{view.render()}</QueryClientProvider>);
}

beforeAll(() => {
  // Open session, but no sessionId → every data hook stays disabled, so each view renders its cold state.
  useSessionStore.setState({ kind: 'open', filePath: 'conformance.typhon', sessionId: '' });
});
afterEach(() => {
  cleanup();
  useSelectionStore.getState().clear();
});

describe('AC2.11 — per-view conformance (suites D + E)', () => {
  describe.each(VIEWS)('$label', (view) => {
    it('D (PC-2): renders a non-blank cold state — never an empty panel', () => {
      const { container } = mount(view);
      expect((container.textContent ?? '').trim().length).toBeGreaterThan(0);
    });

    it('E (PC-6): no disabled Open in / Reveal in / Go to affordance', () => {
      const { container } = mount(view);
      const dead = Array.from(container.querySelectorAll('button, [role="button"]')).filter((el) => {
        const disabled = (el as HTMLButtonElement).disabled || el.getAttribute('aria-disabled') === 'true';
        return disabled && /\b(open in|reveal in|go to)\b/i.test(el.textContent ?? '');
      });
      expect(dead).toEqual([]);
    });
  });

  it('enrols every reintroduced (active zone-D) view except the canvas-excluded File Map', () => {
    const enrolled = new Set(VIEWS.map((v) => v.id));
    const activeZoneD = Object.entries(ZONE_D_VIEW_ACTIVE)
      .filter(([, active]) => active)
      .map(([id]) => id);
    const missing = activeZoneD.filter((id) => !enrolled.has(id) && !CANVAS_EXCLUDED.includes(id));
    expect(missing, `un-enrolled active views: ${missing.join(', ')}`).toEqual([]);
  });
});
