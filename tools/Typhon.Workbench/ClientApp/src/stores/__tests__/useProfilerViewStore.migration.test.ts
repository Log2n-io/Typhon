/**
 * @vitest-environment jsdom
 *
 * zustand/middleware's persist reads localStorage at rehydrate time, so this file opts into the
 * jsdom env (default is node for zero-DOM startup cost).
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';

/**
 * Pins the v0 → v1 persist migration on `useProfilerViewStore`. Before the migration, collapse
 * state was `Record<string, boolean>`; v1 uses `Record<string, TrackState>` with three possible
 * values (`'summary' | 'expanded' | 'double'`). A silently-broken migration would drop every
 * user's saved gauge-collapse preferences on their next Workbench launch.
 *
 * The test drives zustand's `persist.rehydrate()` API directly rather than importing the migrate
 * closure — that closure isn't exported, and the end-to-end path (localStorage → rehydrate →
 * state) is what actually matters.
 */

const STORE_KEY = 'workbench-profiler-view';

function seedV0(blob: { gaugeCollapse?: Record<string, unknown>; gaugeRegionVisible?: boolean }): void {
  localStorage.setItem(STORE_KEY, JSON.stringify({ state: blob, version: 0 }));
}

function seedV1(blob: {
  gaugeCollapse?: Record<string, string>;
  gaugeRegionVisible?: boolean;
}): void {
  localStorage.setItem(STORE_KEY, JSON.stringify({ state: blob, version: 1 }));
}

describe('useProfilerViewStore — v0 → v1 migration', () => {
  beforeEach(() => {
    localStorage.clear();
    // zustand keeps store state in-memory across tests; clearing localStorage doesn't reset it.
    // Explicitly reset to defaults before each test so leftover state from a prior run can't
    // leak in.
    useProfilerViewStore.setState({
      gaugeCollapse: {},
      gaugeRegionVisible: true,
      perSystemLanesVisible: true,
    });
  });
  afterEach(() => {
    localStorage.clear();
  });

  it('maps v0 boolean true → v1 "summary"', async () => {
    seedV0({ gaugeCollapse: { 'gauge-gc': true, 'gauge-memory': true } });
    await useProfilerViewStore.persist.rehydrate();
    const state = useProfilerViewStore.getState();
    expect(state.gaugeCollapse['gauge-gc']).toBe('summary');
    expect(state.gaugeCollapse['gauge-memory']).toBe('summary');
  });

  it('maps v0 boolean false → v1 "expanded"', async () => {
    seedV0({ gaugeCollapse: { 'gauge-gc': false } });
    await useProfilerViewStore.persist.rehydrate();
    expect(useProfilerViewStore.getState().gaugeCollapse['gauge-gc']).toBe('expanded');
  });

  it('passes through valid v1 TrackState values unchanged', async () => {
    // If a forward-compat field already carries a TrackState literal (e.g., user ran a dev build
    // that wrote v1 before this test), the migration must not re-map it.
    seedV0({ gaugeCollapse: { 'gauge-gc': 'double', 'gauge-memory': 'expanded' } });
    await useProfilerViewStore.persist.rehydrate();
    const state = useProfilerViewStore.getState();
    expect(state.gaugeCollapse['gauge-gc']).toBe('double');
    expect(state.gaugeCollapse['gauge-memory']).toBe('expanded');
  });

  it('drops entries with unrecognised shapes rather than propagating garbage', async () => {
    seedV0({
      gaugeCollapse: {
        valid: true,
        nullish: null,
        weird: 42,
        otherObj: { nested: 'foo' },
      } as Record<string, unknown>,
    });
    await useProfilerViewStore.persist.rehydrate();
    const collapse = useProfilerViewStore.getState().gaugeCollapse;
    expect(collapse['valid']).toBe('summary');
    expect(collapse['nullish']).toBeUndefined();
    expect(collapse['weird']).toBeUndefined();
    expect(collapse['otherObj']).toBeUndefined();
  });

  it('preserves the other persisted toggles across migration', async () => {
    seedV0({
      gaugeCollapse: { 'gauge-gc': true },
      gaugeRegionVisible: false,
    });
    await useProfilerViewStore.persist.rehydrate();
    const state = useProfilerViewStore.getState();
    expect(state.gaugeRegionVisible).toBe(false);
    expect(state.gaugeCollapse['gauge-gc']).toBe('summary');
  });

  it('v1 blob rehydrates without touching TrackState values', async () => {
    seedV1({ gaugeCollapse: { 'gauge-gc': 'double' } });
    await useProfilerViewStore.persist.rehydrate();
    expect(useProfilerViewStore.getState().gaugeCollapse['gauge-gc']).toBe('double');
  });

  it('no persisted blob → defaults apply cleanly', async () => {
    // Fresh install: nothing in localStorage. Rehydrate completes without errors and state holds
    // the defaults defined on create(). Guards against a migrate bug that could throw on
    // `undefined` input.
    await useProfilerViewStore.persist.rehydrate();
    const state = useProfilerViewStore.getState();
    expect(state.gaugeCollapse).toEqual({});
    expect(state.gaugeRegionVisible).toBe(true);
  });
});
