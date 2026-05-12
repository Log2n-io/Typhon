import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useProfilerViewStore } from '../useProfilerViewStore';
import { useSelectionStore } from '../useSelectionStore';
import {
  applySelectionToStore,
  buildSelectionSearch,
  installSelectionUrlSync,
  parseSelectionFromSearch,
} from '../selectionUrlSync';

beforeEach(() => {
  useSelectionStore.getState().clear();
  // Reset viewRange to the `{0, 0}` sentinel so URL snapshots don't carry over between tests.
  useProfilerViewStore.getState().commitViewRange({ startUs: 0, endUs: 0 });
});

describe('parseSelectionFromSearch', () => {
  it('parses every stable slot, including time as a TimeRange', () => {
    const parsed = parseSelectionFromSearch(
      '?session=abc&time=120000-134000&system=AI&component=Position&queue=Damage&resource=storage/paged-mmf&entity=e-42',
    );
    // Post-#345: time parses into a `TimeRange { startUs, endUs }` (profiler-store shape).
    expect(parsed.viewRange).toEqual({ startUs: 120_000, endUs: 134_000 });
    expect(parsed.system).toBe('AI');
    expect(parsed.component).toBe('Position');
    expect(parsed.queue).toBe('Damage');
    expect(parsed.resource).toBe('storage/paged-mmf');
    expect(parsed.entity).toBe('e-42');
  });

  it('returns nulls for missing params', () => {
    const parsed = parseSelectionFromSearch('?session=abc');
    expect(parsed.viewRange).toBeNull();
    expect(parsed.system).toBeNull();
  });

  it('rejects malformed time ranges', () => {
    expect(parseSelectionFromSearch('?time=foo').viewRange).toBeNull();
    expect(parseSelectionFromSearch('?time=100').viewRange).toBeNull();
    expect(parseSelectionFromSearch('?time=200-100').viewRange).toBeNull(); // end <= start
    expect(parseSelectionFromSearch('?time=100-100').viewRange).toBeNull();
  });

  it('ignores volatile slots even if present in the URL', () => {
    const parsed = parseSelectionFromSearch('?focusTick=7&worker=3');
    // Volatile slots are not in the parsed shape — the URL is the wrong place for them.
    expect(parsed).not.toHaveProperty('focusTick');
    expect(parsed).not.toHaveProperty('worker');
  });
});

describe('buildSelectionSearch', () => {
  it('serialises stable slots and preserves unrelated params', () => {
    const out = buildSelectionSearch(new URLSearchParams('session=abc&extra=keep'), {
      viewRange: { startUs: 100, endUs: 200 },
      system: 'AI',
      component: null,
      queue: 'Damage',
      resource: null,
      entity: null,
    });
    const s = out.toString();
    expect(s).toContain('session=abc');
    expect(s).toContain('extra=keep');
    expect(s).toContain('time=100-200');
    expect(s).toContain('system=AI');
    expect(s).toContain('queue=Damage');
    expect(s).not.toContain('component');
    expect(s).not.toContain('resource');
  });

  it('removes stale stable params when slots clear', () => {
    const out = buildSelectionSearch(new URLSearchParams('system=Old&queue=Old'), {
      viewRange: null,
      system: null,
      component: null,
      queue: null,
      resource: null,
      entity: null,
    });
    expect(out.toString()).toBe('');
  });
});

describe('parse / build round-trip', () => {
  it('survives a complete cycle', () => {
    const original =
      'session=abc&time=120000-134000&system=AI&component=Position&queue=Damage&resource=storage/paged-mmf&entity=e-42';
    const parsed = parseSelectionFromSearch(`?${original}`);
    const built = buildSelectionSearch(new URLSearchParams('session=abc'), {
      viewRange: parsed.viewRange,
      system: parsed.system,
      component: parsed.component,
      queue: parsed.queue,
      resource: parsed.resource,
      entity: parsed.entity,
    });
    const reparsed = parseSelectionFromSearch(`?${built.toString()}`);
    expect(reparsed).toEqual(parsed);
  });
});

describe('applySelectionToStore', () => {
  it('writes parsed values into the canonical stores (selection + profiler-view)', () => {
    applySelectionToStore({
      viewRange: { startUs: 1000, endUs: 2000 },
      system: 'AI',
      component: null,
      queue: 'Damage',
      resource: null,
      entity: null,
    });
    const sel = useSelectionStore.getState();
    expect(sel.system).toBe('AI');
    expect(sel.queue).toBe('Damage');
    expect(sel.component).toBeNull();
    // Time goes to the profiler view store (atomic commit — both slots).
    const view = useProfilerViewStore.getState();
    expect(view.viewRange).toEqual({ startUs: 1000, endUs: 2000 });
    expect(view.transientViewRange).toEqual({ startUs: 1000, endUs: 2000 });
  });

  it('leaves viewRange untouched when parsed.viewRange is null', () => {
    useProfilerViewStore.getState().commitViewRange({ startUs: 500, endUs: 600 });
    applySelectionToStore({
      viewRange: null,
      system: 'AI',
      component: null,
      queue: null,
      resource: null,
      entity: null,
    });
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 500, endUs: 600 });
  });
});

describe('installSelectionUrlSync', () => {
  it('writes to URL on stable-slot changes in either store', () => {
    const replaceState = vi.fn<(search: string) => void>();
    const readSearch = () => '?session=abc';
    const unsubscribe = installSelectionUrlSync({ replaceState, readSearch });

    useSelectionStore.getState().setSystem('AI');
    expect(replaceState).toHaveBeenCalledTimes(1);
    expect(replaceState.mock.calls[0][0]).toContain('session=abc');
    expect(replaceState.mock.calls[0][0]).toContain('system=AI');

    // Volatile slot — must not trigger a URL write.
    useSelectionStore.getState().setWorker(3);
    expect(replaceState).toHaveBeenCalledTimes(1);

    useSelectionStore.getState().setQueue('Damage');
    expect(replaceState).toHaveBeenCalledTimes(2);
    expect(replaceState.mock.calls[1][0]).toContain('queue=Damage');

    // Profiler viewRange changes also fire URL writes (atomic commit only — transient writes
    // don't reach the committed slot during the debounce, so they don't fire here either).
    useProfilerViewStore.getState().commitViewRange({ startUs: 100, endUs: 200 });
    expect(replaceState).toHaveBeenCalledTimes(3);
    expect(replaceState.mock.calls[2][0]).toContain('time=100-200');

    unsubscribe();
  });

  it('clears the search when every stable slot becomes empty', () => {
    const replaceState = vi.fn<(search: string) => void>();
    const readSearch = () => '';
    const unsub = installSelectionUrlSync({ replaceState, readSearch });

    useSelectionStore.getState().setSystem('AI');
    useSelectionStore.getState().setSystem(null);
    const last = replaceState.mock.calls.at(-1)?.[0];
    expect(last).toBe('');

    unsub();
  });

  it('omits the `?time=` param when viewRange is the {0,0} no-selection sentinel', () => {
    const replaceState = vi.fn<(search: string) => void>();
    const unsub = installSelectionUrlSync({ replaceState, readSearch: () => '' });
    // beforeEach already set viewRange to {0,0}. Setting a non-time slot fires emit but the URL
    // must not include a time= entry for the degenerate range.
    useSelectionStore.getState().setSystem('AI');
    const lastCall = replaceState.mock.calls.at(-1)?.[0] ?? '';
    expect(lastCall).not.toContain('time=');
    expect(lastCall).toContain('system=AI');
    unsub();
  });

  it('stops emitting after unsubscribe', () => {
    const replaceState = vi.fn<(search: string) => void>();
    const unsub = installSelectionUrlSync({ replaceState, readSearch: () => '' });
    unsub();
    useSelectionStore.getState().setSystem('AI');
    useProfilerViewStore.getState().commitViewRange({ startUs: 10, endUs: 20 });
    expect(replaceState).not.toHaveBeenCalled();
  });
});
