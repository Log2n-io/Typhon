// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useQueryCatalogStore, rowIdOf, DEFAULT_SORT } from '../useQueryCatalogStore';

const STORE_KEY = 'workbench-query-catalog';

beforeEach(() => {
  localStorage.clear();
  useQueryCatalogStore.getState().reset();
});
afterEach(() => {
  localStorage.clear();
});

describe('useQueryCatalogStore', () => {
  it('starts with empty search + null filters + no expanded row + default sort', () => {
    const s = useQueryCatalogStore.getState();
    expect(s.search).toBe('');
    expect(s.systemFilter).toBeNull();
    expect(s.archetypeFilter).toBeNull();
    expect(s.expandedRowId).toBeNull();
    expect(s.sort).toEqual(DEFAULT_SORT);
  });

  it('setSort updates the sort state', () => {
    useQueryCatalogStore.getState().setSort({ key: 'count', dir: 'asc' });
    expect(useQueryCatalogStore.getState().sort).toEqual({ key: 'count', dir: 'asc' });
  });

  it('clearFilters wipes filters + expandedRowId but PRESERVES sort (PC-1 preference survives session change)', () => {
    const s = useQueryCatalogStore.getState();
    s.setSearch('foo');
    s.setSystemFilter(7);
    s.setArchetypeFilter(42);
    s.toggleExpanded('0:1');
    s.setSort({ key: 'selectivity', dir: 'asc' });

    s.clearFilters();

    const next = useQueryCatalogStore.getState();
    expect(next.search).toBe('');
    expect(next.systemFilter).toBeNull();
    expect(next.archetypeFilter).toBeNull();
    expect(next.expandedRowId).toBeNull();
    // Sort survives — that's the whole point of split-reset vs full reset.
    expect(next.sort).toEqual({ key: 'selectivity', dir: 'asc' });
  });

  it('setSearch updates the search string', () => {
    useQueryCatalogStore.getState().setSearch('foo');
    expect(useQueryCatalogStore.getState().search).toBe('foo');
  });

  it('setSystemFilter accepts a number and null', () => {
    useQueryCatalogStore.getState().setSystemFilter(7);
    expect(useQueryCatalogStore.getState().systemFilter).toBe(7);
    useQueryCatalogStore.getState().setSystemFilter(null);
    expect(useQueryCatalogStore.getState().systemFilter).toBeNull();
  });

  it('setArchetypeFilter accepts a number and null', () => {
    useQueryCatalogStore.getState().setArchetypeFilter(100);
    expect(useQueryCatalogStore.getState().archetypeFilter).toBe(100);
    useQueryCatalogStore.getState().setArchetypeFilter(null);
    expect(useQueryCatalogStore.getState().archetypeFilter).toBeNull();
  });

  it('toggleExpanded sets and unsets', () => {
    useQueryCatalogStore.getState().toggleExpanded('0:42');
    expect(useQueryCatalogStore.getState().expandedRowId).toBe('0:42');
    useQueryCatalogStore.getState().toggleExpanded('0:42');
    expect(useQueryCatalogStore.getState().expandedRowId).toBeNull();
  });

  it('toggleExpanded switches between different rows', () => {
    useQueryCatalogStore.getState().toggleExpanded('0:42');
    expect(useQueryCatalogStore.getState().expandedRowId).toBe('0:42');
    useQueryCatalogStore.getState().toggleExpanded('1:7');
    expect(useQueryCatalogStore.getState().expandedRowId).toBe('1:7');
  });

  it('reset returns to initial state', () => {
    const s = useQueryCatalogStore.getState();
    s.setSearch('foo');
    s.setSystemFilter(5);
    s.toggleExpanded('0:1');
    s.reset();
    const next = useQueryCatalogStore.getState();
    expect(next.search).toBe('');
    expect(next.systemFilter).toBeNull();
    expect(next.expandedRowId).toBeNull();
  });
});

// AC3.16 / PC-1 — sort + filters survive a reload; expandedRowId does NOT (volatile UI state).
describe('useQueryCatalogStore — persistence (AC3.16)', () => {
  it('persists sort + search + filters; does NOT persist expandedRowId', () => {
    const s = useQueryCatalogStore.getState();
    s.setSearch('foo');
    s.setSystemFilter(7);
    s.setArchetypeFilter(42);
    s.setSort({ key: 'count', dir: 'asc' });
    s.toggleExpanded('0:1');

    const raw = localStorage.getItem(STORE_KEY);
    expect(raw).toBeTruthy();
    const persisted = JSON.parse(raw!) as { state: Record<string, unknown>; version: number };
    expect(persisted.version).toBe(1);
    expect(persisted.state.search).toBe('foo');
    expect(persisted.state.systemFilter).toBe(7);
    expect(persisted.state.archetypeFilter).toBe(42);
    expect(persisted.state.sort).toEqual({ key: 'count', dir: 'asc' });
    // expandedRowId is volatile UI state — must not leak into the persisted blob.
    expect(persisted.state).not.toHaveProperty('expandedRowId');
  });

  it('rehydrates sort + filters from a seeded blob; expandedRowId stays at its default', async () => {
    localStorage.setItem(STORE_KEY, JSON.stringify({
      state: { search: 'bar', systemFilter: 9, archetypeFilter: 17, sort: { key: 'id', dir: 'asc' } },
      version: 1,
    }));
    await useQueryCatalogStore.persist.rehydrate();
    const s = useQueryCatalogStore.getState();
    expect(s.search).toBe('bar');
    expect(s.systemFilter).toBe(9);
    expect(s.archetypeFilter).toBe(17);
    expect(s.sort).toEqual({ key: 'id', dir: 'asc' });
    expect(s.expandedRowId).toBeNull();
  });
});

describe('rowIdOf', () => {
  it('encodes (kind, localId) as kind:localId', () => {
    expect(rowIdOf(0, 42)).toBe('0:42');
    expect(rowIdOf(1, 9999)).toBe('1:9999');
  });
});
