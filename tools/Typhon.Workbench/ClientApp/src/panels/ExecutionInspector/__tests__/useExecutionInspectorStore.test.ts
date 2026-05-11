import { beforeEach, describe, expect, it } from 'vitest';
import { useExecutionInspectorStore } from '../useExecutionInspectorStore';

beforeEach(() => {
  useExecutionInspectorStore.getState().reset();
});

describe('useExecutionInspectorStore', () => {
  it('initial state has no focus and no selection', () => {
    const s = useExecutionInspectorStore.getState();
    expect(s.focus).toBeNull();
    expect(s.selected).toBeNull();
  });

  it('setFocus stores the (kind, localId) pair and clears any pre-existing selection', () => {
    useExecutionInspectorStore.getState().setSelected({ tickIndex: 100, systemId: 5 });
    useExecutionInspectorStore.getState().setFocus({ kind: 0, localId: 42 });
    expect(useExecutionInspectorStore.getState().focus).toEqual({ kind: 0, localId: 42 });
    expect(useExecutionInspectorStore.getState().selected).toBeNull();
  });

  it('setFocus(null) clears focus and selection', () => {
    useExecutionInspectorStore.getState().setFocus({ kind: 0, localId: 42 });
    useExecutionInspectorStore.getState().setSelected({ tickIndex: 100, systemId: 5 });
    useExecutionInspectorStore.getState().setFocus(null);
    expect(useExecutionInspectorStore.getState().focus).toBeNull();
    expect(useExecutionInspectorStore.getState().selected).toBeNull();
  });

  it('setSelected accepts a selection and does not modify focus', () => {
    useExecutionInspectorStore.getState().setFocus({ kind: 0, localId: 42 });
    useExecutionInspectorStore.getState().setSelected({ tickIndex: 200, systemId: 7 });
    expect(useExecutionInspectorStore.getState().focus).toEqual({ kind: 0, localId: 42 });
    expect(useExecutionInspectorStore.getState().selected).toEqual({ tickIndex: 200, systemId: 7 });
  });

  it('setSelected(null) clears the selection while preserving focus', () => {
    useExecutionInspectorStore.getState().setFocus({ kind: 0, localId: 42 });
    useExecutionInspectorStore.getState().setSelected({ tickIndex: 200, systemId: 7 });
    useExecutionInspectorStore.getState().setSelected(null);
    expect(useExecutionInspectorStore.getState().focus).toEqual({ kind: 0, localId: 42 });
    expect(useExecutionInspectorStore.getState().selected).toBeNull();
  });

  it('reset returns to initial state', () => {
    useExecutionInspectorStore.getState().setFocus({ kind: 1, localId: 9 });
    useExecutionInspectorStore.getState().setSelected({ tickIndex: 500, systemId: 3 });
    useExecutionInspectorStore.getState().reset();
    const s = useExecutionInspectorStore.getState();
    expect(s.focus).toBeNull();
    expect(s.selected).toBeNull();
  });
});
