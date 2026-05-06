// @vitest-environment jsdom
import { renderHook, cleanup } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSelectionBootstrap } from '../useSelectionBootstrap';

function setUrl(search: string): void {
  window.history.replaceState(null, '', `/${search}`);
}

beforeEach(() => {
  setUrl('');
  useSelectionStore.getState().clear();
  useSchemaInspectorStore.getState().reset();
  useProfilerViewStore.getState().setViewRange({ startUs: 0, endUs: 0 });
});

afterEach(() => {
  cleanup();
});

describe('useSelectionBootstrap', () => {
  it('cold-load: URL → unified store on mount', () => {
    setUrl('?session=abc&time=120000-134000&system=AI&component=Position&queue=Damage');

    renderHook(() => useSelectionBootstrap());

    const s = useSelectionStore.getState();
    expect(s.time).toEqual({ start: 120_000, end: 134_000 });
    expect(s.system).toBe('AI');
    expect(s.component).toBe('Position');
    expect(s.queue).toBe('Damage');
  });

  it('cold-load: URL values propagate through bridges to legacy stores', () => {
    setUrl('?time=200000-300000&component=Velocity');

    renderHook(() => useSelectionBootstrap());

    // The bridge installed before applySelectionToStore picks up the URL writes and forwards
    // them out to legacy stores.
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 200_000, endUs: 300_000 });
    expect(useSchemaInspectorStore.getState().selectedComponentType).toBe('Velocity');
  });

  it('steady-state: store changes mirror to URL', () => {
    setUrl('?session=abc');
    renderHook(() => useSelectionBootstrap());

    useSelectionStore.getState().setSystem('AI');
    expect(window.location.search).toContain('system=AI');
    expect(window.location.search).toContain('session=abc');

    useSelectionStore.getState().setQueue('Damage');
    expect(window.location.search).toContain('queue=Damage');
  });

  it('volatile slots stay out of the URL', () => {
    setUrl('');
    renderHook(() => useSelectionBootstrap());

    useSelectionStore.getState().setFocusTick(7);
    useSelectionStore.getState().setWorker(3);

    expect(window.location.search).toBe('');
  });

  it('teardown unsubscribes both URL sync and bridges', () => {
    setUrl('');
    const { unmount } = renderHook(() => useSelectionBootstrap());

    unmount();

    // After unmount, store changes must NOT reach the URL.
    useSelectionStore.getState().setSystem('AI');
    expect(window.location.search).toBe('');
    // ...nor reach the legacy stores via the bridges.
    useSelectionStore.getState().setComponent('Position');
    expect(useSchemaInspectorStore.getState().selectedComponentType).toBeNull();
  });
});
