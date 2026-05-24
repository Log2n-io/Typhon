// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { ArchetypeInfo, ComponentSchema, ComponentSummary, IndexInfo } from '@/hooks/schema/types';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useDataBrowserStore } from '@/stores/useDataBrowserStore';
import { useInspectorTargetStore } from '@/stores/useInspectorTargetStore';

// The Layout tab (default) mounts a canvas that measures itself with a ResizeObserver — absent in jsdom.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: typeof ResizeObserverStub }).ResizeObserver = ResizeObserverStub;

// Stage 2 · Component Inspector panel (GAP-02). Component coverage: PC-9 self-addressing (auto-target on cold
// open, header switcher, PC-1 restore), bus-driven header, Layout (default) + Indexes + Used-in tabs, the
// reveal→bus handoff, the type-first Data Browser auto-pick, and the pin behavior. No disabled stubs (PC-6).

const mocks = vi.hoisted(() => ({
  comp: { list: [] as ComponentSummary[], isLoading: false, isError: false, isFetching: false, refetch: () => {} },
  indexes: { indexes: [] as IndexInfo[], isLoading: false, isError: false },
  archetypes: { archetypes: [] as ArchetypeInfo[], isLoading: false, isError: false, isFetching: false, refetch: () => {} },
  schema: { schema: undefined as ComponentSchema | undefined, isLoading: false, isError: false },
  rels: { response: { runtimeHosted: false, systems: [] as unknown[] }, isLoading: false, isError: false },
}));
vi.mock('@/hooks/schema/useComponentList', () => ({ useComponentList: () => mocks.comp }));
vi.mock('@/hooks/schema/useComponentSchema', () => ({ useComponentSchema: () => mocks.schema }));
vi.mock('@/hooks/schema/useIndexesForComponent', () => ({ useIndexesForComponent: () => mocks.indexes }));
vi.mock('@/hooks/schema/useArchetypesForComponent', () => ({ useArchetypesForComponent: () => mocks.archetypes }));
vi.mock('@/hooks/schema/useSystemRelationships', () => ({ useSystemRelationships: () => mocks.rels }));
// The Relationships children fetch topology / render a React-Flow DAG — not jsdom-friendly; stub them.
vi.mock('@/panels/SchemaInspector/AccessChips', () => ({ default: () => null }));
vi.mock('@/panels/SchemaInspector/SchemaRelationshipsGraph', () => ({ default: () => null }));

import ComponentInspectorPanel from '@/panels/ComponentInspector/ComponentInspectorPanel';

const summary = (over: Partial<ComponentSummary> & { typeName: string; fullName: string }): ComponentSummary => ({
  storageSize: 16,
  fieldCount: 2,
  archetypeCount: 3,
  entityCount: 0,
  indexCount: 1,
  storageMode: 'Versioned',
  ...over,
});

const archetype = (id: string): ArchetypeInfo => ({
  archetypeId: id,
  componentTypes: ['Game.Position'],
  entityCount: 500,
  componentSize: 12,
  storageMode: 'cluster',
  chunkCount: 1,
  chunkCapacity: 500,
  occupancyPct: 80,
});

const PROPS = {} as IDockviewPanelProps;
const FILE = 'test.typhon';

beforeEach(() => {
  mocks.comp = { list: [summary({ typeName: 'Position', fullName: 'Game.Position' })], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
  mocks.indexes = {
    indexes: [{ fieldName: 'X', fieldOffset: 0, fieldSize: 4, allowsMultiple: false, indexType: 'BTree' }],
    isLoading: false,
    isError: false,
  };
  mocks.archetypes = { archetypes: [archetype('800'), archetype('801')], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
  mocks.schema = { schema: undefined, isLoading: false, isError: false };
  mocks.rels = { response: { runtimeHosted: false, systems: [] }, isLoading: false, isError: false };
  useSessionStore.setState({ kind: 'open', filePath: FILE, sessionId: 'sess' });
  useSelectionStore.getState().clear();
  useDataBrowserStore.getState().reset();
  useInspectorTargetStore.setState({ byKey: {} });
  Element.prototype.scrollIntoView = () => {};
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
});
afterEach(() => cleanup());

describe('ComponentInspectorPanel', () => {
  it('auto-targets the most-entities component when nothing is on the bus, with the (auto) chip (PC-9)', () => {
    mocks.comp = {
      list: [summary({ typeName: 'Position', fullName: 'Game.Position', entityCount: 0 }), summary({ typeName: 'Health', fullName: 'Game.Health', entityCount: 5000 })],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    render(<ComponentInspectorPanel {...PROPS} />);
    expect(screen.getByText('Health')).toBeTruthy(); // 5000 > 0
    expect(screen.getByTestId('component-auto-chip')).toBeTruthy();
  });

  it('restores the PC-1 last-viewed component on cold open — no (auto) chip — even if another has more entities', () => {
    mocks.comp = {
      list: [summary({ typeName: 'Position', fullName: 'Game.Position', entityCount: 0 }), summary({ typeName: 'Health', fullName: 'Game.Health', entityCount: 5000 })],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    useInspectorTargetStore.getState().save(FILE, { componentType: 'Position' });
    render(<ComponentInspectorPanel {...PROPS} />);
    expect(screen.getByText('Position')).toBeTruthy();
    expect(screen.queryByTestId('component-auto-chip')).toBeNull();
  });

  it('shows the PC-2 empty state (no switcher) only when the DB has zero components', () => {
    mocks.comp = { list: [], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
    render(<ComponentInspectorPanel {...PROPS} />);
    expect(screen.getByText(/no components/i)).toBeTruthy();
    expect(screen.queryByTestId('component-switcher')).toBeNull();
  });

  it('header switcher re-targets all tabs via the bus and clears the (auto) chip (PC-9)', () => {
    mocks.comp = {
      list: [summary({ typeName: 'Position', fullName: 'Game.Position', entityCount: 0 }), summary({ typeName: 'Health', fullName: 'Game.Health', entityCount: 5000 })],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    render(<ComponentInspectorPanel {...PROPS} />);
    expect(screen.getByText('Health')).toBeTruthy(); // auto-picked
    fireEvent.click(screen.getByTestId('component-switcher'));
    const rows = screen.getAllByTestId('component-switcher-item');
    expect(rows.map((r) => r.getAttribute('data-id'))).toEqual(['Position', 'Health']);
    fireEvent.click(rows[0]); // pick Position
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'component', ref: 'Position' });
    expect(screen.queryByTestId('component-auto-chip')).toBeNull();
    expect(useInspectorTargetStore.getState().byKey[FILE]?.componentType).toBe('Position');
  });

  it('an external component selection clears the (auto) chip', () => {
    mocks.comp = {
      list: [summary({ typeName: 'Position', fullName: 'Game.Position', entityCount: 0 }), summary({ typeName: 'Health', fullName: 'Game.Health', entityCount: 5000 })],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    render(<ComponentInspectorPanel {...PROPS} />);
    expect(screen.getByTestId('component-auto-chip')).toBeTruthy();
    act(() => useSelectionStore.getState().select('component', 'Position'));
    expect(screen.getByText('Position')).toBeTruthy();
    expect(screen.queryByTestId('component-auto-chip')).toBeNull();
  });

  it('renders the header + Layout tab (default) byte-grid canvas', () => {
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    expect(screen.getByText('Position')).toBeTruthy();
    expect(screen.getByText(/16B · 2 fields · used in 3/)).toBeTruthy();
    expect(screen.getByTestId('schema-layout-canvas')).toBeTruthy(); // Layout is the default tab
  });

  it('Indexes tab shows the type-global note + indexed fields', () => {
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    act(() => screen.getByRole('tab', { name: 'Indexes' }).focus()); // Radix Tabs: automatic activation on focus
    expect(screen.getByText(/type-global: one b\+tree per indexed field/i)).toBeTruthy();
    expect(screen.getAllByTestId('component-index-row')).toHaveLength(1);
  });

  it('Used-in tab lists archetypes; clicking a row reveals it on the bus (archetype leaf)', () => {
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    act(() => screen.getByRole('tab', { name: 'Used in' }).focus());
    const rows = screen.getAllByTestId('component-usedin-row');
    expect(rows.map((r) => r.getAttribute('data-archetype-id'))).toEqual(['800', '801']);

    fireEvent.click(rows[0]);
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'archetype', ref: '800' });
  });

  it('stays pinned to its component when the leaf moves to a field', () => {
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    expect(screen.getByText('Position')).toBeTruthy();
    act(() => useSelectionStore.getState().select('field', { component: 'Position', fieldName: 'X' }));
    expect(screen.getByText('Position')).toBeTruthy();
  });

  it('Relationships tab (Open session) shows the trace/attach note — no topology fetch', () => {
    useSessionStore.setState({ kind: 'open' });
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    act(() => screen.getByRole('tab', { name: 'Relationships' }).focus());
    expect(screen.getByText(/a database file on its own has no systems/i)).toBeTruthy();
    expect(screen.queryByTestId('rel-runtime-banner')).toBeNull();
  });

  it('Relationships tab (trace, runtime not hosted) shows the runtime-not-hosted gate', () => {
    useSessionStore.setState({ kind: 'trace' });
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    act(() => screen.getByRole('tab', { name: 'Relationships' }).focus());
    expect(screen.getByTestId('rel-runtime-banner')).toBeTruthy();
  });

  it('Storage mode tab shows the mode + plain-language note (GAP-25)', () => {
    mocks.schema = {
      schema: { typeName: 'Position', fullName: 'Game.Position', storageSize: 12, totalSize: 16, allowMultiple: false, revision: 1, fields: [], storageMode: 'SingleVersion' },
      isLoading: false,
      isError: false,
    };
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    act(() => screen.getByRole('tab', { name: 'Storage mode' }).focus());
    expect(screen.getByTestId('storagemode-value').textContent).toBe('SingleVersion');
    expect(screen.getByText(/in-place writes/i)).toBeTruthy();
  });

  it('header "Open in Data Browser" auto-picks the max-entity archetype (AC2.7)', () => {
    mocks.archetypes = {
      archetypes: [archetype('800'), { ...archetype('806'), entityCount: 2000 }],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    fireEvent.click(screen.getByTestId('component-open-data-browser'));
    // openDataBrowser scopes the Data Browser to the archetype with the most entities (806 > 800).
    expect(useDataBrowserStore.getState().archetypeId).toBe('806');
  });

  it('omits "Open in Data Browser" when no archetype has entities (PC-6, no dead verb)', () => {
    mocks.archetypes = {
      archetypes: [{ ...archetype('800'), entityCount: 0 }],
      isLoading: false,
      isError: false,
      isFetching: false,
      refetch: () => {},
    };
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    expect(screen.queryByTestId('component-open-data-browser')).toBeNull();
  });

  it('has no disabled affordances (PC-6)', () => {
    useSelectionStore.getState().select('component', 'Position');
    render(<ComponentInspectorPanel {...PROPS} />);
    expect(document.querySelector('button[disabled]')).toBeNull();
  });

  // AC2.3 — panel-scoped [ / ] tab cycling (PC-8). Active only while this is the focused dock panel.
  const selected = (name: string) => screen.getByRole('tab', { name }).getAttribute('aria-selected') === 'true';
  const pressKey = (key: string) =>
    act(() => {
      document.body.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
    });

  it('cycles tabs with ] and [ while the panel is the active dock panel', () => {
    const active = { api: { isActive: true } } as unknown as IDockviewPanelProps;
    render(<ComponentInspectorPanel {...active} />);
    expect(selected('Layout')).toBe(true); // default
    pressKey(']');
    expect(selected('Indexes')).toBe(true);
    pressKey('[');
    expect(selected('Layout')).toBe(true);
    pressKey('['); // wraps to the last tab
    expect(selected('Relationships')).toBe(true);
  });

  it('does not cycle tabs when the panel is not the active dock panel', () => {
    const inactive = { api: { isActive: false } } as unknown as IDockviewPanelProps;
    render(<ComponentInspectorPanel {...inactive} />);
    expect(selected('Layout')).toBe(true);
    pressKey(']');
    expect(selected('Layout')).toBe(true);
  });
});
