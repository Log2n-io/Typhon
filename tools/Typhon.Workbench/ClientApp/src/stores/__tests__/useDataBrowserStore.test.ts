import { describe, it, expect, beforeEach } from 'vitest';
import { useDataBrowserStore, DEFAULT_PAGE_SIZE } from '../useDataBrowserStore';

describe('useDataBrowserStore', () => {
  beforeEach(() => useDataBrowserStore.getState().reset());

  it('setArchetype sets the id, clears selection, returns to page 0, and drops custom columns', () => {
    useDataBrowserStore.getState().selectEntity('42');
    useDataBrowserStore.getState().setPageIndex(5);
    useDataBrowserStore.getState().setPreviewFields([{ typeName: 'X', fieldId: 1 }]);
    useDataBrowserStore.getState().setArchetype('100');
    const s = useDataBrowserStore.getState();
    expect(s.archetypeId).toBe('100');
    expect(s.selectedEntityId).toBeNull();
    expect(s.pageIndex).toBe(0);
    expect(s.previewFields).toBeNull();
  });

  it('setPreviewFields stores an explicit list and reset() clears it back to default (null)', () => {
    useDataBrowserStore.getState().setPreviewFields([{ typeName: 'A', fieldId: 0 }]);
    expect(useDataBrowserStore.getState().previewFields).toEqual([{ typeName: 'A', fieldId: 0 }]);
    useDataBrowserStore.getState().setPreviewFields(null);
    expect(useDataBrowserStore.getState().previewFields).toBeNull();
  });

  it('setPageSize updates the size and resets to page 0', () => {
    useDataBrowserStore.getState().setPageIndex(3);
    useDataBrowserStore.getState().setPageSize(100);
    const s = useDataBrowserStore.getState();
    expect(s.pageSize).toBe(100);
    expect(s.pageIndex).toBe(0);
  });

  it('setPageIndex clamps to non-negative', () => {
    useDataBrowserStore.getState().setPageIndex(4);
    expect(useDataBrowserStore.getState().pageIndex).toBe(4);
    useDataBrowserStore.getState().setPageIndex(-2);
    expect(useDataBrowserStore.getState().pageIndex).toBe(0);
  });

  it('selectEntity sets the id and bumps touchedAt', () => {
    expect(useDataBrowserStore.getState().touchedAt).toBe(0);
    useDataBrowserStore.getState().selectEntity('7');
    const s = useDataBrowserStore.getState();
    expect(s.selectedEntityId).toBe('7');
    expect(s.touchedAt).toBeGreaterThan(0);
  });

  it('reset clears everything and restores the default page size', () => {
    useDataBrowserStore.getState().setArchetype('1');
    useDataBrowserStore.getState().selectEntity('2');
    useDataBrowserStore.getState().setPageSize(50);
    useDataBrowserStore.getState().setPageIndex(2);
    useDataBrowserStore.getState().reset();
    const s = useDataBrowserStore.getState();
    expect(s.archetypeId).toBeNull();
    expect(s.selectedEntityId).toBeNull();
    expect(s.touchedAt).toBe(0);
    expect(s.pageSize).toBe(DEFAULT_PAGE_SIZE);
    expect(s.pageIndex).toBe(0);
  });
});
