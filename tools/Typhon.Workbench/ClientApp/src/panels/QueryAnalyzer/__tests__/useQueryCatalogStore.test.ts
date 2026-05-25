import { beforeEach, describe, expect, it } from 'vitest';
import { useQueryCatalogStore, rowIdOf } from '../useQueryCatalogStore';

beforeEach(() => {
  useQueryCatalogStore.getState().reset();
});

describe('useQueryCatalogStore', () => {
  it('starts with empty search + null filters + no expanded row', () => {
    const s = useQueryCatalogStore.getState();
    expect(s.search).toBe('');
    expect(s.systemFilter).toBeNull();
    expect(s.archetypeFilter).toBeNull();
    expect(s.expandedRowId).toBeNull();
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

describe('rowIdOf', () => {
  it('encodes (kind, localId) as kind:localId', () => {
    expect(rowIdOf(0, 42)).toBe('0:42');
    expect(rowIdOf(1, 9999)).toBe('1:9999');
  });
});
