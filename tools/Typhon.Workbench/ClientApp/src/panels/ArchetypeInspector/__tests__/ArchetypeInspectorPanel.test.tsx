// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { ArchetypeInfo, ComponentSummary } from '@/hooks/schema/types';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDataBrowserStore } from '@/stores/useDataBrowserStore';
import { useInspectorTargetStore } from '@/stores/useInspectorTargetStore';
import { useDbMapStore } from '@/stores/useDbMapStore';

// Stage 2 · Archetype Inspector panel (GAP-02). Component coverage: PC-9 self-addressing (auto-target on cold
// open, header switcher, PC-1 restore), the bus-driven header + Components tab, row→bus, the launchpad/
// degraded tabs, and the "pinned to the last archetype leaf" behavior (a component click must not blank it).

const mocks = vi.hoisted(() => ({
  arch: { list: [] as ArchetypeInfo[], isLoading: false, isError: false, isFetching: false, refetch: () => {} },
  comp: { list: [] as ComponentSummary[], isLoading: false, isError: false, isFetching: false, refetch: () => {} },
  health: null as unknown,
}));
vi.mock('@/hooks/schema/useArchetypeList', () => ({ useArchetypeList: () => mocks.arch }));
vi.mock('@/hooks/schema/useComponentList', () => ({ useComponentList: () => mocks.comp }));
// The Storage tab's #619 breakdown reads the segment table. Stub the query so this panel test stays provider-free.
vi.mock('@/hooks/dbmap/useDbMapHealth', () => ({
  useDbMapHealth: (sessionId: string | null) => ({ data: sessionId ? mocks.health : null }),
}));
// The header/switcher labels run through useArchetypeNames (live react-query). Stub it to a passthrough so this
// panel test stays provider-free; labels then fall back to "#<id>" (the mocked DTOs carry no archetype name).
vi.mock('@/hooks/queryConsole/useArchetypeNames', () => ({
  useArchetypeNames: () => ({ label: (ref: string | null | undefined) => ref ?? '', isLoading: false }),
}));

import ArchetypeInspectorPanel from '@/panels/ArchetypeInspector/ArchetypeInspectorPanel';

class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}

const comp = (over: Partial<ComponentSummary> & { typeName: string; fullName: string }): ComponentSummary => ({
  storageSize: 16,
  fieldCount: 2,
  archetypeCount: 1,
  entityCount: 0,
  indexCount: 0,
  storageMode: 'Versioned',
  ...over,
});

const arch = (id: string, entityCount: number, over: Partial<ArchetypeInfo> = {}): ArchetypeInfo => ({
  archetypeId: id,
  name: '',
  componentTypes: ['Game.CompA', 'Game.CompB'],
  entityCount,
  componentSize: 32,
  storageMode: 'cluster',
  chunkCount: 2,
  chunkCapacity: 500,
  occupancyPct: 99,
  ...over,
});

const PROPS = {} as IDockviewPanelProps;
const FILE = 'test.typhon';

beforeEach(() => {
  mocks.arch = { list: [arch('800', 1000)], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
  mocks.comp = {
    list: [
      comp({ typeName: 'CompA', fullName: 'Game.CompA', storageSize: 12, indexCount: 1 }),
      comp({ typeName: 'CompB', fullName: 'Game.CompB', indexCount: 0 }),
    ],
    isLoading: false,
    isError: false,
    isFetching: false,
    refetch: () => {},
  };
  useSelectionStore.getState().clear();
  useDataBrowserStore.getState().reset();
  useInspectorTargetStore.setState({ byKey: {} });
  useSessionStore.setState({ filePath: FILE, sessionId: 'sess', kind: 'open', capabilities: ['database'] });
  mocks.health = {
    dataFileBytes: 4_096 * 10_000,
    dataFilePageCount: 10_000,
    segments: [
      { id: 0, kind: 'Cluster', typeName: 'Game.Unit', pageCount: 1_200, allocatedChunkCount: 62, chunkCapacity: 100, chunkFillPct: 62, reclaimableBytes: 1_800_000, entityCount: 340_182, occupancyPct: 71 },
      { id: 1, kind: 'EntityMap', typeName: 'Game.Unit', pageCount: 4, allocatedChunkCount: 2, chunkCapacity: 10, chunkFillPct: 20, reclaimableBytes: 0, entityCount: 0, occupancyPct: 20 },
      { id: 2, kind: 'Component', typeName: 'CompA', pageCount: 900, allocatedChunkCount: 5, chunkCapacity: 10, chunkFillPct: 50, reclaimableBytes: 0, entityCount: 0, occupancyPct: 50 },
    ],
  };
  (globalThis as unknown as { ResizeObserver: typeof ResizeObserverStub }).ResizeObserver = ResizeObserverStub;
  Element.prototype.scrollIntoView = () => {};
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
});
afterEach(() => cleanup());

describe('ArchetypeInspectorPanel', () => {
  it('auto-targets the most-entities archetype when nothing is on the bus, with the (auto) chip (PC-9)', () => {
    mocks.arch = {
      list: [arch('800', 1000), arch('806', 5000)],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    render(<ArchetypeInspectorPanel {...PROPS} />);
    expect(screen.getByText('#806')).toBeTruthy(); // 5000 > 1000
    expect(screen.getByTestId('archetype-auto-chip')).toBeTruthy();
  });

  it('restores the PC-1 last-viewed archetype on cold open — no (auto) chip — even if another has more entities', () => {
    mocks.arch = {
      list: [arch('800', 1000), arch('806', 5000)],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    useInspectorTargetStore.getState().save(FILE, { archetypeId: '800' });
    render(<ArchetypeInspectorPanel {...PROPS} />);
    expect(screen.getByText('#800')).toBeTruthy();
    expect(screen.queryByTestId('archetype-auto-chip')).toBeNull();
  });

  it('shows the PC-2 empty state (no switcher) only when the DB has zero archetypes', () => {
    mocks.arch = { list: [], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
    render(<ArchetypeInspectorPanel {...PROPS} />);
    expect(screen.getByText(/no archetypes/i)).toBeTruthy();
    expect(screen.queryByTestId('archetype-switcher')).toBeNull();
  });

  it('header switcher re-targets via the bus and clears the (auto) chip (PC-9)', () => {
    mocks.arch = {
      list: [arch('800', 1000), arch('806', 5000)],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    render(<ArchetypeInspectorPanel {...PROPS} />);
    expect(screen.getByText('#806')).toBeTruthy(); // auto-picked
    fireEvent.click(screen.getByTestId('archetype-switcher'));
    const rows = screen.getAllByTestId('archetype-switcher-item');
    expect(rows.map((r) => r.getAttribute('data-id'))).toEqual(['800', '806']);
    fireEvent.click(rows[0]); // pick #800
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'archetype', ref: '800' });
    expect(screen.getByText('#800')).toBeTruthy();
    expect(screen.queryByTestId('archetype-auto-chip')).toBeNull();
    // PC-1 recorded the deliberate pick.
    expect(useInspectorTargetStore.getState().byKey[FILE]?.archetypeId).toBe('800');
  });

  it('an external archetype selection clears the (auto) chip', () => {
    mocks.arch = {
      list: [arch('800', 1000), arch('806', 5000)],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    render(<ArchetypeInspectorPanel {...PROPS} />);
    expect(screen.getByTestId('archetype-auto-chip')).toBeTruthy();
    act(() => useSelectionStore.getState().select('archetype', '800'));
    expect(screen.getByText('#800')).toBeTruthy();
    expect(screen.queryByTestId('archetype-auto-chip')).toBeNull();
  });

  it('renders the archetype header + Components tab from the bus leaf; row click sets the component leaf', () => {
    useSelectionStore.getState().select('archetype', '800');
    render(<ArchetypeInspectorPanel {...PROPS} />);

    expect(screen.getByText('#800')).toBeTruthy();
    expect(screen.getByText(/2 components · 1,000 entities/)).toBeTruthy();
    const rows = screen.getAllByTestId('archetype-component-row');
    expect(rows.map((r) => r.getAttribute('data-type-name'))).toEqual(['CompA', 'CompB']);

    fireEvent.click(screen.getByText('CompA'));
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'component', ref: 'CompA' });
  });

  it('stays pinned to its archetype when the leaf moves to a component', () => {
    useSelectionStore.getState().select('archetype', '800');
    render(<ArchetypeInspectorPanel {...PROPS} />);
    expect(screen.getByText('#800')).toBeTruthy();

    // Selecting a component (e.g. from elsewhere) moves the leaf — the inspector must keep showing #800.
    act(() => useSelectionStore.getState().select('component', 'CompA'));
    expect(screen.getByText('#800')).toBeTruthy();
  });

  it('Entities tab offers a real "Open in Data Browser" verb (AC2.6, no disabled stub)', () => {
    useSelectionStore.getState().select('archetype', '800');
    render(<ArchetypeInspectorPanel {...PROPS} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Entities' }));
    expect(screen.getByText('1,000 entities')).toBeTruthy(); // the tab's standalone count (exact), not the header
    const open = screen.getByTestId('archetype-open-data-browser');
    expect(open.hasAttribute('disabled')).toBe(false); // PC-6: a real verb, never a disabled stub
    expect(document.querySelector('button[disabled]')).toBeNull();

    fireEvent.click(open);
    // openDataBrowser scopes the Data Browser to this archetype (silo) and mirrors it to the bus.
    expect(useDataBrowserStore.getState().archetypeId).toBe('800');
  });

  it('Indexes tab lists only indexed components (type-global framing)', () => {
    useSelectionStore.getState().select('archetype', '800');
    render(<ArchetypeInspectorPanel {...PROPS} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Indexes' }));
    const rows = screen.getAllByTestId('archetype-index-row');
    expect(rows.map((r) => r.getAttribute('data-type-name'))).toEqual(['CompA']);
  });

  // ── #619 §4.2 — the Storage tab's physical breakdown ──────────────────────────────────────────────────────
  //
  // Before #619 this tab's "Reveal in File Map" passed `rows[0].typeName` — the archetype's FIRST COMPONENT. For a
  // cluster archetype the components have no component-table segment, so the reveal matched nothing and the button
  // silently did nothing. No test covered it, which is how it survived.

  function renderStorageTab(over: Partial<ArchetypeInfo> = {}) {
    mocks.arch = { list: [arch('800', 1000, { name: 'Game.Unit', ...over })], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
    useSelectionStore.getState().select('archetype', '800');
    render(<ArchetypeInspectorPanel {...PROPS} />);
    fireEvent.click(screen.getByRole('tab', { name: 'Storage' }));
  }

  it('Storage tab lists the segments the archetype owns, and their page total', () => {
    renderStorageTab();
    const owned = screen.getByTestId('archetype-owned-segments').textContent ?? '';
    expect(owned).toContain('Cluster · EntityMap');
    expect(owned).toContain('1,204'); // 1,200 cluster + 4 entity map — NOT the 900-page shared component table
  });

  it('names shared component tables without counting them in the total', () => {
    // CompA's table is type-global: every archetype carrying CompA stores rows there. Adding its 900 pages here
    // would report the same bytes again for the next archetype that lists CompA.
    renderStorageTab();
    expect(screen.getByTestId('archetype-shared-tables').textContent).toContain('not counted above');
    expect(screen.getByTestId('archetype-owned-segments').textContent).not.toContain('2,104');
  });

  it('reveals the ARCHETYPE, not its first component (the pre-#619 defect)', () => {
    useDbMapStore.getState().clearPendingFocus();
    renderStorageTab();

    fireEvent.click(screen.getByTestId('archetype-reveal-file-map'));

    expect(useDbMapStore.getState().pendingFocus).toEqual({ kind: 'archetype', name: 'Game.Unit' });
  });

  it('offers no reveal at all for an archetype this database has no segment for (§5.7)', () => {
    // Absent, not a button that opens the map onto nothing.
    renderStorageTab({ name: 'Game.Vanished' });
    expect(screen.queryByTestId('archetype-reveal-file-map')).toBeNull();
    expect(screen.queryByTestId('archetype-owned-segments')).toBeNull();
  });

  it('carries the staleness caveat only when a capture is attached', () => {
    // With no capture there are no recorded figures nearby to confuse these present-tense ones with, so the
    // caveat would be an apology for data that is simply current.
    renderStorageTab();
    expect(screen.queryByTestId('archetype-storage-caveat')).toBeNull();

    cleanup();
    useSessionStore.setState({ capabilities: ['database', 'profiler'] });
    renderStorageTab();
    expect(screen.getByTestId('archetype-storage-caveat').textContent).toContain('move pages');
  });
});
