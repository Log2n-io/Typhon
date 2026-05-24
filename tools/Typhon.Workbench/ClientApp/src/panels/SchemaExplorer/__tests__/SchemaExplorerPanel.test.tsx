// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { ArchetypeInfo, ComponentSummary } from '@/hooks/schema/types';
import { useSelectionStore } from '@/stores/useSelectionStore';

// jsdom has no ResizeObserver; the panel measures the tree viewport with one. Minimal stub.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
(globalThis as unknown as { ResizeObserver: typeof ResizeObserverStub }).ResizeObserver = ResizeObserverStub;

// Stage 2 · Schema Explorer panel (GAP-02). Component coverage of the jsdom-friendly surface — Types
// table, mode toggle, search, PC-2 states, and row→bus. The Archetypes tree is react-arborist
// (virtualized; 0 rows at jsdom's 0-height), so it is proven by the E2E (increment 3); its data logic is
// already unit-tested in schemaExplorerModel.test.ts. Conformance: cheapest sufficient layer.

const mocks = vi.hoisted(() => ({
  arch: { list: [] as ArchetypeInfo[], isLoading: false, isError: false, isFetching: false, refetch: () => {} },
  comp: { list: [] as ComponentSummary[], isLoading: false, isError: false, isFetching: false, refetch: () => {} },
}));
vi.mock('@/hooks/schema/useArchetypeList', () => ({ useArchetypeList: () => mocks.arch }));
vi.mock('@/hooks/schema/useComponentList', () => ({ useComponentList: () => mocks.comp }));

import SchemaExplorerPanel from '@/panels/SchemaExplorer/SchemaExplorerPanel';

const comp = (over: Partial<ComponentSummary> & { typeName: string; fullName: string }): ComponentSummary => ({
  storageSize: 16,
  fieldCount: 2,
  archetypeCount: 1,
  entityCount: 0,
  indexCount: 0,
  storageMode: 'Versioned',
  ...over,
});

const PROPS = {} as IDockviewPanelProps;

beforeEach(() => {
  mocks.arch = { list: [], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
  mocks.comp = { list: [], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
  useSelectionStore.getState().clear();
});
afterEach(() => cleanup());

describe('SchemaExplorerPanel', () => {
  it('renders the shell: search + both mode toggles', () => {
    mocks.comp.list = [comp({ typeName: 'Position', fullName: 'Game.Position' })];
    render(<SchemaExplorerPanel {...PROPS} />);
    expect(screen.getByTestId('schema-explorer')).toBeTruthy();
    expect(screen.getByPlaceholderText(/search/i)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Archetypes' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Types' })).toBeTruthy();
  });

  it('PC-2 states: loading / error / empty', () => {
    mocks.arch.isLoading = true;
    mocks.comp.isLoading = true;
    const { rerender } = render(<SchemaExplorerPanel {...PROPS} />);
    expect(screen.getByText(/loading schema/i)).toBeTruthy();

    mocks.arch = { list: [], isLoading: false, isError: true, isFetching: false, refetch: () => {} };
    mocks.comp = { list: [], isLoading: false, isError: true, isFetching: false, refetch: () => {} };
    rerender(<SchemaExplorerPanel {...PROPS} />);
    expect(screen.getByText(/failed to load schema/i)).toBeTruthy();

    mocks.arch = { list: [], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
    mocks.comp = { list: [], isLoading: false, isError: false, isFetching: false, refetch: () => {} };
    rerender(<SchemaExplorerPanel {...PROPS} />);
    expect(screen.getByText(/no schema registered/i)).toBeTruthy();
  });

  it('Types mode lists component types and a row click writes the bus leaf', () => {
    mocks.comp.list = [
      comp({ typeName: 'Position', fullName: 'Game.Position', storageSize: 12, entityCount: 2000, indexCount: 1, archetypeCount: 3 }),
      comp({ typeName: 'Health', fullName: 'Game.Health' }),
    ];
    render(<SchemaExplorerPanel {...PROPS} />);

    fireEvent.click(screen.getByRole('button', { name: 'Types' }));
    const rows = screen.getAllByTestId('schema-explorer-type-row');
    expect(rows).toHaveLength(2);

    fireEvent.click(screen.getByText('Position'));
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'component', ref: 'Position' });
  });

  it('Types-mode search narrows the list', () => {
    mocks.comp.list = [
      comp({ typeName: 'Position', fullName: 'Game.Position' }),
      comp({ typeName: 'Health', fullName: 'Game.Health' }),
    ];
    render(<SchemaExplorerPanel {...PROPS} />);
    fireEvent.click(screen.getByRole('button', { name: 'Types' }));
    fireEvent.change(screen.getByPlaceholderText(/search/i), { target: { value: 'Health' } });
    const rows = screen.getAllByTestId('schema-explorer-type-row');
    expect(rows).toHaveLength(1);
    expect(rows[0].getAttribute('data-type-name')).toBe('Health');
  });

  it('Types-mode "Indexed" filter keeps only indexed types', () => {
    mocks.comp.list = [
      comp({ typeName: 'Position', fullName: 'Game.Position', indexCount: 1 }),
      comp({ typeName: 'Health', fullName: 'Game.Health', indexCount: 0 }),
    ];
    render(<SchemaExplorerPanel {...PROPS} />);
    fireEvent.click(screen.getByRole('button', { name: 'Types' }));
    fireEvent.click(screen.getByText('Indexed'));
    const rows = screen.getAllByTestId('schema-explorer-type-row');
    expect(rows.map((r) => r.getAttribute('data-type-name'))).toEqual(['Position']);
  });
});
