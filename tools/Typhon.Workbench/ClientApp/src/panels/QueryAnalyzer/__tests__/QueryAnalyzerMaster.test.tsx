// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { QueryDefinitionDto } from '@/api/generated/model';
import { useSessionStore } from '@/stores/useSessionStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useQueryCatalogStore } from '@/panels/QueryAnalyzer/useQueryCatalogStore';
import { useQueryAnalyzerStore } from '../useQueryAnalyzerStore';
import { makeDef } from './fixtures';

// Mutable holder so each test can swap the catalog the (mocked) data hook returns. `vi.hoisted` lifts it
// above the hoisted `vi.mock` factory below.
const hoisted = vi.hoisted(() => ({ defs: [] as QueryDefinitionDto[] }));

vi.mock('@/panels/QueryAnalyzer/useQueryDefinitions', () => ({
  useQueryDefinitions: () => ({ definitions: hoisted.defs, isLoading: false, isError: false, error: null }),
}));
vi.mock('@/hooks/useProfilerNameMaps', () => ({
  useProfilerNameMaps: () => ({
    archetypeNames: new Map<number, string>([[1, 'Position'], [2, 'AABB']]),
    systemNames: new Map<number, string>([[0, 'Movement']]),
  }),
}));

// Imported after the mocks are registered (vi.mock calls are hoisted above all imports by vitest).
import { QueryAnalyzerMaster } from '../QueryAnalyzerMaster';

function renderMaster() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <QueryAnalyzerMaster />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  useSessionStore.setState({ sessionId: 'sess-1', kind: 'trace' });
  useQueryCatalogStore.getState().reset();
  useQueryAnalyzerStore.getState().reset();
  useSelectionStore.getState().clear();
});
afterEach(() => cleanup());

describe('QueryAnalyzerMaster', () => {
  it('AC2: ranks rows by TotalWallNs desc by default (heavier query first)', () => {
    // Supplied in REVERSE order so a pass proves real sorting, not insertion order.
    hoisted.defs = [
      makeDef({ localId: 2, target: 2, totalWallNs: 60_000 }),
      makeDef({ localId: 1, target: 1, totalWallNs: 140_000 }),
    ];
    renderMaster();
    const rows = screen.getAllByTestId('query-analyzer-row');
    expect(rows.map((r) => r.getAttribute('data-row-id'))).toEqual(['0:1', '0:2']);
  });

  it('AC3: flags structurally-identical definitions as duplicates', () => {
    hoisted.defs = [
      makeDef({ localId: 1, target: 1, totalWallNs: 100_000 }),
      makeDef({ localId: 9, target: 1, totalWallNs: 90_000 }), // same shape as #1 → both flagged
    ];
    renderMaster();
    expect(screen.getAllByTestId('query-analyzer-duplicate-marker')).toHaveLength(2);
  });

  it('AC3: the archetype filter narrows the catalog', () => {
    hoisted.defs = [
      makeDef({ localId: 1, target: 1, totalWallNs: 100_000 }),
      makeDef({ localId: 2, target: 2, totalWallNs: 90_000 }),
    ];
    useQueryCatalogStore.setState({ archetypeFilter: 2 });
    renderMaster();
    const rows = screen.getAllByTestId('query-analyzer-row');
    expect(rows.map((r) => r.getAttribute('data-row-id'))).toEqual(['0:2']);
  });

  it('AC4: clicking a row selects it in the store AND writes the bus query leaf', () => {
    hoisted.defs = [
      makeDef({ localId: 1, target: 1, totalWallNs: 140_000 }),
      makeDef({ localId: 2, target: 2, totalWallNs: 60_000 }),
    ];
    renderMaster();
    const row = screen.getAllByTestId('query-analyzer-row').find((r) => r.getAttribute('data-row-id') === '0:2');
    fireEvent.click(row as HTMLElement);

    expect(useQueryAnalyzerStore.getState().selectedQuery).toEqual({ kind: 0, localId: 2 });
    const leaf = useSelectionStore.getState().leaf;
    expect(leaf?.type).toBe('query');
    expect(leaf?.ref).toEqual({ kind: 0, localId: 2 });
  });
});
