// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { ArchetypeInfo, ComponentSummary } from '@/hooks/schema/types';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDataBrowserStore } from '@/stores/useDataBrowserStore';
import { useInspectorTargetStore } from '@/stores/useInspectorTargetStore';

// Stage 2 · Archetype Inspector panel (GAP-02). Component coverage: PC-9 self-addressing (auto-target on cold
// open, header switcher, PC-1 restore), the bus-driven header + Components tab, row→bus, the launchpad/
// degraded tabs, and the "pinned to the last archetype leaf" behavior (a component click must not blank it).

const mocks = vi.hoisted(() => ({
  arch: { list: [] as ArchetypeInfo[], isLoading: false, isError: false, isFetching: false, refetch: () => {} },
  comp: { list: [] as ComponentSummary[], isLoading: false, isError: false, isFetching: false, refetch: () => {} },
}));
vi.mock('@/hooks/schema/useArchetypeList', () => ({ useArchetypeList: () => mocks.arch }));
vi.mock('@/hooks/schema/useComponentList', () => ({ useComponentList: () => mocks.comp }));
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
  useSessionStore.setState({ filePath: FILE, sessionId: 'sess', kind: 'open' });
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
});
