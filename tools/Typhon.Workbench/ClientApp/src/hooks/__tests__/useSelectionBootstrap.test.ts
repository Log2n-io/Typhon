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
  useProfilerViewStore.getState().commitViewRange({ startUs: 0, endUs: 0 });
});

afterEach(() => {
  cleanup();
});

describe('useSelectionBootstrap', () => {
  it('cold-load: URL → canonical stores on mount (time → profiler view store)', () => {
    setUrl('?session=abc&time=120000-134000&system=AI&component=Position&queue=Damage');

    renderHook(() => useSelectionBootstrap());

    const sel = useSelectionStore.getState();
    expect(sel.system).toBe('AI');
    expect(sel.component).toBe('Position');
    expect(sel.queue).toBe('Damage');
    // Post-#345: time-window lives in the profiler view store. Atomic commit on cold-load.
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 120_000, endUs: 134_000 });
  });

  it('cold-load: time-only URL still commits viewRange', () => {
    setUrl('?time=200000-300000&component=Velocity');

    renderHook(() => useSelectionBootstrap());

    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 200_000, endUs: 300_000 });
    // SchemaInspector still receives `component` via the remaining inlined bridge.
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

  it('viewRange commits propagate to URL', () => {
    setUrl('?session=abc');
    renderHook(() => useSelectionBootstrap());

    useProfilerViewStore.getState().commitViewRange({ startUs: 100, endUs: 250 });
    expect(window.location.search).toContain('time=100-250');
  });

  it('volatile slots stay out of the URL', () => {
    setUrl('');
    renderHook(() => useSelectionBootstrap());

    useSelectionStore.getState().setWorker(3);

    expect(window.location.search).toBe('');
  });

  it('teardown unsubscribes URL sync (bridges are inlined and always active)', () => {
    setUrl('');
    const { unmount } = renderHook(() => useSelectionBootstrap());

    unmount();

    // After unmount, store changes must NOT reach the URL.
    useSelectionStore.getState().setSystem('AI');
    expect(window.location.search).toBe('');
    // The cross-store mirrors are now inlined into the legacy stores' setters (#345 Step 7), so
    // they ALWAYS propagate — no "unmount disables them" semantics anymore. This is correct:
    // bridges were Phase-C transitional state; the canonical model is one inline write per setter.
    useSelectionStore.getState().setComponent('Position');
    expect(useSchemaInspectorStore.getState().selectedComponentType).toBe('Position');
  });
});
