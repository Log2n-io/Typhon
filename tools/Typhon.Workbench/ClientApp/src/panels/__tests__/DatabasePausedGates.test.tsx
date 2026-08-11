// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useSessionStore } from '@/stores/useSessionStore';
import type { SessionDto } from '@/api/generated/model';

/**
 * Every database-backed panel's paused branch, in one place (#621, design §9.6).
 *
 * <p><b>Why one spec rather than a case per panel's own suite.</b> The defect these guard against is uniform: while
 * the database is released every request 409s, TanStack reports `isError`, and the panel's ordinary error branch says
 * "Failed to load …". That text is both false and actively misleading — it tells the user to close and reopen the
 * session, which is the dance pausing exists to remove. The bug is the *same* bug in fifteen places, so the
 * regression net is one table of panels, and adding a panel to the family means adding a row.</p>
 *
 * <p>Each case asserts two things, because either alone is passable by accident: the paused notice appears, AND the
 * panel's error/empty text does <b>not</b>. A panel that renders both is still lying, just more quietly.</p>
 */

// Every hook these panels call for data is stubbed to the shape a released database produces: an error, and no rows.
// That is the real 409 signature — the request fails and the list comes back empty — which is exactly the
// combination that made panels show "Failed to load" AND "nothing registered" at the same time.
const failed = { isLoading: false, isError: true, error: new Error('409'), refetch: vi.fn(), isFetching: false };

vi.mock('@/hooks/schema/useArchetypeList', () => ({ useArchetypeList: () => ({ list: [], ...failed }) }));
vi.mock('@/hooks/schema/useComponentList', () => ({ useComponentList: () => ({ list: [], ...failed }) }));
vi.mock('@/hooks/dbmap/useDbMapHealth', () => ({ useDbMapHealth: () => ({ data: undefined, ...failed }) }));
vi.mock('@/hooks/useResourceIndex', () => ({
  useResourceIndex: () => ({ root: undefined, isLoading: false, isError: true, refresh: vi.fn(), isFetching: false }),
  refreshResourceGraph: vi.fn(),
}));
vi.mock('@/hooks/streams/useResourceGraphStream', () => ({ useResourceGraphStream: () => undefined }));
vi.mock('@/hooks/queryConsole/useComponentNames', () => ({
  useComponentNames: () => ({ label: (n: string) => n }),
}));
vi.mock('@/hooks/queryConsole/useArchetypeNames', () => ({
  useArchetypeNames: () => ({ label: (n: string) => n }),
}));

import SchemaExplorerPanel from '@/panels/SchemaExplorer/SchemaExplorerPanel';
import ArchetypeInspectorPanel from '@/panels/ArchetypeInspector/ArchetypeInspectorPanel';
import ComponentInspectorPanel from '@/panels/ComponentInspector/ComponentInspectorPanel';
import StorageHealthPanel from '@/panels/StorageHealth/StorageHealthPanel';
import ResourceTreePanel from '@/panels/ResourceTreePanel';
import { ResultGrid } from '@/panels/QueryConsole/ResultGrid';

// The schema panels mount canvases/trees that measure themselves; jsdom has no ResizeObserver. Same stub the
// ArchetypeInspector and ComponentInspector suites install.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: typeof ResizeObserverStub }).ResizeObserver = ResizeObserverStub;

const OPEN: SessionDto = { sessionId: 's1', kind: 'Open', state: 'Ready', filePath: 'C:/db/world.typhon' };

/** A session that has released its database: paused, and the `database` capability withdrawn. */
function setPaused() {
  useSessionStore.getState().setSession({
    ...OPEN,
    isPaused: true,
    lifecycle: 'Paused',
    reason: 'Database released to PID 4242.',
    capabilities: ['profiler'],
  });
}

/** The same session with its database back — the control, so a notice that renders unconditionally fails. */
function setLive() {
  useSessionStore.getState().setSession({ ...OPEN, isPaused: false, capabilities: ['database'] });
}

/** These panels take `IDockviewPanelProps` and ignore it; the grid takes none. One loose type covers both. */
// eslint-disable-next-line @typescript-eslint/no-explicit-any -- deliberately structural: the props are never read
type AnyPanel = React.ComponentType<any>;

function renderPanel(Panel: AnyPanel) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <Panel />
    </QueryClientProvider>,
  );
}

interface PanelCase {
  name: string;
  Panel: AnyPanel;
  testId: string;
  /** The wrong text this panel used to show while paused — must be gone once the gate is in. */
  wrongText: RegExp;
}

const PANELS: PanelCase[] = [
  { name: 'Schema Explorer', Panel: SchemaExplorerPanel, testId: 'schema-explorer-paused', wrongText: /failed to load schema|no schema registered/i },
  { name: 'Archetype Inspector', Panel: ArchetypeInspectorPanel, testId: 'archetype-inspector-paused', wrongText: /failed to load schema/i },
  { name: 'Component Inspector', Panel: ComponentInspectorPanel, testId: 'component-inspector-paused', wrongText: /failed to load schema/i },
  { name: 'Storage Health', Panel: StorageHealthPanel, testId: 'storage-health-paused', wrongText: /failed to load storage health|loading storage health/i },
  { name: 'Resource Tree', Panel: ResourceTreePanel, testId: 'resource-tree-paused', wrongText: /failed to load resources/i },
];

beforeEach(() => {
  useSessionStore.getState().clearSession();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('database-backed panels — paused state (#621 §9.6)', () => {
  it.each(PANELS)('$name shows the paused notice instead of an error', ({ Panel, testId, wrongText }) => {
    setPaused();
    renderPanel(Panel);

    expect(screen.getByTestId(testId)).toBeTruthy();
    expect(screen.queryByText(wrongText)).toBeNull();
  });

  it.each(PANELS)('$name still reports a real failure when the database is live', ({ Panel, testId, wrongText }) => {
    // The control that stops the gate from swallowing genuine errors: same failing hooks, database present.
    setLive();
    renderPanel(Panel);

    expect(screen.queryByTestId(testId)).toBeNull();
    expect(screen.getByText(wrongText)).toBeTruthy();
  });
});

describe('Query Console result grid — paused state', () => {
  it('shows the paused notice rather than a red `database_paused` error code', () => {
    setPaused();
    renderPanel(ResultGrid);

    expect(screen.getByTestId('query-console-paused')).toBeTruthy();
    expect(screen.queryByText(/database_paused/i)).toBeNull();
  });
});

describe('the notice itself', () => {
  it('names what is coming back, so the message is not interchangeable boilerplate', () => {
    setPaused();
    renderPanel(StorageHealthPanel);

    expect(screen.getByTestId('storage-health-paused').textContent).toMatch(/storage health will be available again/i);
  });

  it('does not render for an Attach session, which has no database to come back', () => {
    // `!hasDatabase` alone would be true here — the gate must require BOTH flags or an attach session
    // would sit behind a paused notice forever.
    useSessionStore.getState().setSession({ ...OPEN, kind: 'Attach', capabilities: ['profiler'] });
    renderPanel(StorageHealthPanel);

    expect(screen.queryByTestId('storage-health-paused')).toBeNull();
  });
});
