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
import TopSpansPanel from '@/panels/profiler/TopSpansPanel';
import CallTree from '@/panels/profiler/CallTree';
import SourcePreviewPanel from '@/panels/profiler/SourcePreviewPanel';
import CriticalPathPanel from '@/panels/CriticalPath/CriticalPathPanel';
import QueryAnalyzerPanel from '@/panels/QueryAnalyzer/QueryAnalyzerPanel';
import QueryConsolePanel from '@/panels/QueryConsole/QueryConsolePanel';
import EngineLiveHealthPanel from '@/panels/EngineLiveHealth/EngineLiveHealthPanel';
import DevFixturePanel from '@/panels/DevFixture/DevFixturePanel';

// AC2.11 / AC3.11 — per-view conformance, parameterized over the reintroduced Stage-2/3 views (the conformance
// doc's suites D + E). Each view is rendered in its **cold** state (no session → hooks disabled → empty/loading)
// and must satisfy:
//   • D (PC-2): never a blank panel — a cold view shows a skeleton/sentence/picker, not nothing.
//   • E (PC-6): no broken affordance — no *disabled* control whose label reads Open in / Reveal in / Go to.
// The registry is the enrolment list: a new reintroduced view is added here and inherits the suite. F (focus/
// F6) is covered by panelFocus.test + the per-view keyboard tests (ComponentInspector [ / ]); H (density) by
// density.test (lists read --row-h). Canvas views — the File Map and the Profiler timeline (TimeArea/
// TickOverview) — are DS-1 density-exempt (suite H.2) and not jsdom-mountable (2D context); their conformance
// rides their libs/* unit tests + the handoff matrix. (Top Spans is a DOM table → enrolled below.)
// Data Flow joins the canvas-excluded set (3A): its Timeline mode statically imports uPlot, which calls
// `matchMedia` at module-load — absent in jsdom, so importing the panel at all crashes the suite. Its Matrix-mode
// DOM + the mode toggle are covered by DataFlow/__tests__/DataFlowMode.test.tsx instead (suite-D/E-equivalent).
// System DAG joins it too (3D): SystemDagCanvas statically imports `@xyflow/react` (React Flow), which touches
// browser-only APIs jsdom lacks at mount. Its D/E ride the dagModel/dag* unit tests + the Inspector reveal handoff
// (Inspector.test) — the design's canvas-exempt caveat (AC3.11). Critical Path is plain SVG → enrolled in VIEWS.
const CANVAS_EXCLUDED = ['DbMap', 'Profiler', 'DataFlow', 'SystemDag'];

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
  { id: 'TopSpans', label: 'Top Spans', render: () => <TopSpansPanel /> },
  { id: 'CallTree', label: 'Call Tree', render: () => <CallTree /> },
  { id: 'SourcePreview', label: 'Source Preview', render: () => <SourcePreviewPanel {...NO_PROPS} /> },
  { id: 'CriticalPath', label: 'Critical Path', render: () => <CriticalPathPanel {...NO_PROPS} /> },
  // Query Analyzer (3 Phase 4): jsdom-mountable because the React-Flow Plan tab is lazy-loaded — the
  // panel's static import graph carries no `@xyflow/react`, so it is enrolled here, not CANVAS_EXCLUDED.
  { id: 'QueryAnalyzer', label: 'Query Analyzer', render: () => <QueryAnalyzerPanel {...NO_PROPS} /> },
  // #386 Phase 1: Query Console — pure DOM (textarea editor + chip stack + result grid). Disabled-state branch
  // (non-open session) covers D + E for the conformance enrolment guard.
  { id: 'QueryConsole', label: 'Query Console', render: () => <QueryConsolePanel {...NO_PROPS} /> },
  // Engine Live Health (4 Phase 1, #377): pure DOM (header + placeholders + button); cold-state branch
  // (non-attach session) covers D + E for the conformance enrolment guard.
  { id: 'EngineLiveHealth', label: 'Engine Live Health', render: () => <EngineLiveHealthPanel {...NO_PROPS} /> },
  // Dev Fixture: pure DOM (form + buttons). Capability probe is async — the first render shows
  // "Checking capability…", satisfying PC-2. PC-6 (no broken Open in/Reveal in/Go to) holds trivially.
  { id: 'DevFixture', label: 'Dev Fixture', render: () => <DevFixturePanel {...NO_PROPS} /> },
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
