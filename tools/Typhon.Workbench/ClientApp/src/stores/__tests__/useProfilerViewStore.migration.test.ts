/**
 * @vitest-environment jsdom
 *
 * zustand/middleware's persist reads localStorage at rehydrate time, so this file opts into the
 * jsdom env (default is node for zero-DOM startup cost).
 */
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { selectGaugeRegionVisible, useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { GAUGE_GROUPS } from '@/libs/profiler/canvas/gauges/region';

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
      gaugeVisibility: {},
      gaugesBeforeHide: null,
      perSystemLanesVisible: true,
      spanPalette: 'categorical',
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
    // v6: a hidden region is every gauge unchecked, so the filter shows why the band is gone.
    expect(selectGaugeRegionVisible(state)).toBe(false);
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
    expect(selectGaugeRegionVisible(state)).toBe(true);
  });

  it('v3 → current: drops orphan liveFollowWindowUs from persisted state (#345 Step 8)', async () => {
    // Live-follow mode is gone — leftover `liveFollowWindowUs` from a v3 install must be removed
    // so localStorage doesn't carry the orphan key indefinitely. The runtime state already lacks
    // the field (spread initialiser ignores unknown keys), but the test asserts the migrate
    // function explicitly deletes the field from the persisted blob.
    localStorage.setItem('workbench-profiler-view', JSON.stringify({
      state: { gaugeCollapse: { 'gauge-gc': 'double' }, liveFollowWindowUs: 2_000_000 },
      version: 3,
    }));
    await useProfilerViewStore.persist.rehydrate();
    // Read the persisted blob back — `liveFollowWindowUs` should be gone.
    const raw = localStorage.getItem('workbench-profiler-view');
    expect(raw).toBeTruthy();
    const persisted = JSON.parse(raw!) as { state: Record<string, unknown>; version: number };
    // Rehydrating an old blob re-persists at the CURRENT store version (6), running every intermediate
    // migration clause — including the < 4 `liveFollowWindowUs` drop asserted below.
    expect(persisted.version).toBe(6);
    expect(persisted.state).not.toHaveProperty('liveFollowWindowUs');
    // Other v3 fields survived the migration.
    expect(persisted.state.gaugeCollapse).toEqual({ 'gauge-gc': 'double' });
  });

  it('v4 → v5: spanPalette defaults to "categorical" and persists (#376 Phase 5)', async () => {
    // A pre-Phase-5 (v4) blob has no spanPalette; the migration must default it to 'categorical' and
    // bump the persisted version, without disturbing the other v4 fields.
    localStorage.setItem('workbench-profiler-view', JSON.stringify({
      state: { gaugeCollapse: {}, spanColorMode: 'thread' },
      version: 4,
    }));
    await useProfilerViewStore.persist.rehydrate();
    expect(useProfilerViewStore.getState().spanPalette).toBe('categorical');
    const persisted = JSON.parse(localStorage.getItem('workbench-profiler-view')!) as { state: Record<string, unknown>; version: number };
    expect(persisted.version).toBe(6);
    expect(persisted.state.spanPalette).toBe('categorical');
    expect(persisted.state.spanColorMode).toBe('thread');
  });

  it('v5: an explicit spanPalette = "curated" round-trips unchanged', async () => {
    localStorage.setItem('workbench-profiler-view', JSON.stringify({
      state: { gaugeCollapse: {}, spanPalette: 'curated' },
      version: 5,
    }));
    await useProfilerViewStore.persist.rehydrate();
    expect(useProfilerViewStore.getState().spanPalette).toBe('curated');
  });
});

describe('useProfilerViewStore — gauge region is the gauge checkboxes (v6)', () => {
  const ids = GAUGE_GROUPS.map((g) => g.id);

  beforeEach(() => {
    localStorage.clear();
    useProfilerViewStore.setState({ gaugeVisibility: {}, gaugesBeforeHide: null });
  });

  it('g hides the region by unchecking every gauge, and restores the previous choice', () => {
    const store = useProfilerViewStore.getState();
    store.setGaugeVisibility(ids[0], false);
    const chosen = useProfilerViewStore.getState().gaugeVisibility;

    store.toggleGaugeRegion();
    let s = useProfilerViewStore.getState();
    expect(selectGaugeRegionVisible(s)).toBe(false);
    expect(ids.every((id) => s.gaugeVisibility[id] === false)).toBe(true);

    store.toggleGaugeRegion();
    s = useProfilerViewStore.getState();
    expect(selectGaugeRegionVisible(s)).toBe(true);
    expect(s.gaugeVisibility).toEqual(chosen);
    expect(s.gaugesBeforeHide).toBeNull();
  });

  it('unchecking every gauge in the filter is the same hidden state g reads, and g then shows them all', () => {
    const store = useProfilerViewStore.getState();
    store.setManyGaugeVisibility(Object.fromEntries(ids.map((id) => [id, false])));
    expect(selectGaugeRegionVisible(useProfilerViewStore.getState())).toBe(false);

    store.toggleGaugeRegion();
    expect(useProfilerViewStore.getState().gaugeVisibility).toEqual({});
  });

  it('v5 gaugeRegionVisible:false migrates to every gauge unchecked, restorable by g', async () => {
    localStorage.setItem(STORE_KEY, JSON.stringify({
      state: { gaugeRegionVisible: false, gaugeVisibility: { [ids[1]]: false } },
      version: 5,
    }));
    await useProfilerViewStore.persist.rehydrate();
    const s = useProfilerViewStore.getState();
    expect(selectGaugeRegionVisible(s)).toBe(false);
    expect(s.gaugesBeforeHide).toEqual({ [ids[1]]: false });

    s.toggleGaugeRegion();
    expect(useProfilerViewStore.getState().gaugeVisibility).toEqual({ [ids[1]]: false });
    const persisted = JSON.parse(localStorage.getItem(STORE_KEY)!) as { state: Record<string, unknown> };
    expect(persisted.state).not.toHaveProperty('gaugeRegionVisible');
  });
});
