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
import { categoricalColor } from '@/libs/color/categorical';
import { rgbCss } from '@/libs/color/contrast';

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

  it('AC3.10: owner systems render with their shared categorical identity dot', () => {
    hoisted.defs = [makeDef({ localId: 1, target: 1, totalWallNs: 100_000, owners: [0] })];
    renderMaster();
    const row = screen.getByTestId('query-analyzer-row');
    const expected = rgbCss(categoricalColor('Movement'));
    const dots = Array.from(row.querySelectorAll('span[aria-hidden]')) as HTMLElement[];
    expect(dots.some((d) => d.style.backgroundColor === expected)).toBe(true);
  });

  it('AC3.11 / §6: ↑/↓/Home/End move the catalog selection and fire onSelect for each step', () => {
    hoisted.defs = [
      makeDef({ localId: 1, target: 1, totalWallNs: 140_000 }), // ranked #1 (heaviest)
      makeDef({ localId: 2, target: 2, totalWallNs: 60_000 }),  // ranked #2
      makeDef({ localId: 3, target: 1, totalWallNs: 30_000 }),  // ranked #3
    ];
    renderMaster();

    // Seed: pick the first row (heaviest = localId 1) so we have a starting point.
    fireEvent.click(screen.getAllByTestId('query-analyzer-row').find((r) => r.getAttribute('data-row-id') === '0:1')!);
    expect(useQueryAnalyzerStore.getState().selectedQuery).toEqual({ kind: 0, localId: 1 });

    const catalog = screen.getByTestId('query-analyzer-catalog');

    fireEvent.keyDown(catalog, { key: 'ArrowDown' });
    expect(useQueryAnalyzerStore.getState().selectedQuery).toEqual({ kind: 0, localId: 2 });

    fireEvent.keyDown(catalog, { key: 'ArrowDown' });
    expect(useQueryAnalyzerStore.getState().selectedQuery).toEqual({ kind: 0, localId: 3 });

    // At the last row → ArrowDown clamps (no change).
    fireEvent.keyDown(catalog, { key: 'ArrowDown' });
    expect(useQueryAnalyzerStore.getState().selectedQuery).toEqual({ kind: 0, localId: 3 });

    fireEvent.keyDown(catalog, { key: 'ArrowUp' });
    expect(useQueryAnalyzerStore.getState().selectedQuery).toEqual({ kind: 0, localId: 2 });

    fireEvent.keyDown(catalog, { key: 'Home' });
    expect(useQueryAnalyzerStore.getState().selectedQuery).toEqual({ kind: 0, localId: 1 });

    fireEvent.keyDown(catalog, { key: 'End' });
    expect(useQueryAnalyzerStore.getState().selectedQuery).toEqual({ kind: 0, localId: 3 });
  });

  it('AC3.11 / §6: ArrowDown with no prior selection lands on the first row', () => {
    hoisted.defs = [
      makeDef({ localId: 1, target: 1, totalWallNs: 100_000 }),
      makeDef({ localId: 2, target: 2, totalWallNs: 50_000 }),
    ];
    renderMaster();
    expect(useQueryAnalyzerStore.getState().selectedQuery).toBeNull();
    fireEvent.keyDown(screen.getByTestId('query-analyzer-catalog'), { key: 'ArrowDown' });
    expect(useQueryAnalyzerStore.getState().selectedQuery).toEqual({ kind: 0, localId: 1 });
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
