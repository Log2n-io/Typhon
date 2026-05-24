import { describe, it, expect, beforeEach } from 'vitest';
import { useDataBrowserStore, DEFAULT_PAGE_SIZE } from '../useDataBrowserStore';
import { useSelectionStore } from '../useSelectionStore';

describe('useDataBrowserStore', () => {
  beforeEach(() => {
    useDataBrowserStore.getState().reset();
    useSelectionStore.getState().clear();
  });

  it('setArchetype sets the id, returns to page 0, drops custom columns, and mirrors to the bus', () => {
    useDataBrowserStore.getState().setPageIndex(5);
    useDataBrowserStore.getState().setPreviewFields([{ typeName: 'X', fieldId: 1 }]);
    useDataBrowserStore.getState().setArchetype('100');
    const s = useDataBrowserStore.getState();
    expect(s.archetypeId).toBe('100');
    expect(s.pageIndex).toBe(0);
    expect(s.previewFields).toBeNull();
    // GAP-05: archetype is the bus leaf, superseding any prior entity selection.
    expect(useSelectionStore.getState().leaf).toMatchObject({ type: 'archetype', ref: '100' });
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

  it('selectEntity (GAP-05) writes the entity to the bus leaf, carrying its archetype', () => {
    useDataBrowserStore.getState().setArchetype('2002');
    useDataBrowserStore.getState().selectEntity('7');
    expect(useSelectionStore.getState().leaf).toMatchObject({
      type: 'entity',
      ref: { archetypeId: '2002', entityId: '7' },
    });
  });

  it('reset clears panel view-state and restores the default page size', () => {
    useDataBrowserStore.getState().setArchetype('1');
    useDataBrowserStore.getState().setPageSize(50);
    useDataBrowserStore.getState().setPageIndex(2);
    useDataBrowserStore.getState().reset();
    const s = useDataBrowserStore.getState();
    expect(s.archetypeId).toBeNull();
    expect(s.pageSize).toBe(DEFAULT_PAGE_SIZE);
    expect(s.pageIndex).toBe(0);
    expect(s.previewFields).toBeNull();
  });
});
