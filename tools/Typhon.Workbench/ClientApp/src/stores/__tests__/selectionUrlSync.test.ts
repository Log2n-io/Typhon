import { beforeEach, describe, expect, it, vi } from 'vitest';
import { useSelectionStore } from '../useSelectionStore';
import {
  applySelectionToStore,
  buildSelectionSearch,
  installSelectionUrlSync,
  parseSelectionFromSearch,
} from '../selectionUrlSync';

beforeEach(() => {
  useSelectionStore.getState().clear();
});

describe('parseSelectionFromSearch', () => {
  it('parses every stable slot', () => {
    const parsed = parseSelectionFromSearch(
      '?session=abc&time=120000-134000&system=AI&component=Position&queue=Damage&resource=storage/paged-mmf&entity=e-42',
    );
    expect(parsed.time).toEqual({ start: 120_000, end: 134_000 });
    expect(parsed.system).toBe('AI');
    expect(parsed.component).toBe('Position');
    expect(parsed.queue).toBe('Damage');
    expect(parsed.resource).toBe('storage/paged-mmf');
    expect(parsed.entity).toBe('e-42');
  });

  it('returns nulls for missing params', () => {
    const parsed = parseSelectionFromSearch('?session=abc');
    expect(parsed.time).toBeNull();
    expect(parsed.system).toBeNull();
  });

  it('rejects malformed time ranges', () => {
    expect(parseSelectionFromSearch('?time=foo').time).toBeNull();
    expect(parseSelectionFromSearch('?time=100').time).toBeNull();
    expect(parseSelectionFromSearch('?time=200-100').time).toBeNull(); // end <= start
    expect(parseSelectionFromSearch('?time=100-100').time).toBeNull();
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
      time: { start: 100, end: 200 },
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
      time: null,
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
      time: parsed.time,
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
  it('writes parsed values into the store', () => {
    applySelectionToStore({
      time: { start: 1000, end: 2000 },
      system: 'AI',
      component: null,
      queue: 'Damage',
      resource: null,
      entity: null,
    });
    const s = useSelectionStore.getState();
    expect(s.time).toEqual({ start: 1000, end: 2000 });
    expect(s.system).toBe('AI');
    expect(s.queue).toBe('Damage');
    expect(s.component).toBeNull();
  });
});

describe('installSelectionUrlSync', () => {
  it('writes to URL on stable-slot changes and ignores volatile slots', () => {
    const replaceState = vi.fn<(search: string) => void>();
    const readSearch = () => '?session=abc';
    const unsubscribe = installSelectionUrlSync({ replaceState, readSearch });

    useSelectionStore.getState().setSystem('AI');
    expect(replaceState).toHaveBeenCalledTimes(1);
    expect(replaceState.mock.calls[0][0]).toContain('session=abc');
    expect(replaceState.mock.calls[0][0]).toContain('system=AI');

    // Volatile slot — must not trigger a URL write.
    useSelectionStore.getState().setFocusTick(7);
    useSelectionStore.getState().setWorker(3);
    expect(replaceState).toHaveBeenCalledTimes(1);

    useSelectionStore.getState().setQueue('Damage');
    expect(replaceState).toHaveBeenCalledTimes(2);
    expect(replaceState.mock.calls[1][0]).toContain('queue=Damage');

    unsubscribe();
  });

  it('clears the search when every stable slot becomes null', () => {
    const replaceState = vi.fn<(search: string) => void>();
    const readSearch = () => '';
    const unsub = installSelectionUrlSync({ replaceState, readSearch });

    useSelectionStore.getState().setSystem('AI');
    useSelectionStore.getState().setSystem(null);
    const last = replaceState.mock.calls.at(-1)?.[0];
    expect(last).toBe('');

    unsub();
  });

  it('stops emitting after unsubscribe', () => {
    const replaceState = vi.fn<(search: string) => void>();
    const unsub = installSelectionUrlSync({ replaceState, readSearch: () => '' });
    unsub();
    useSelectionStore.getState().setSystem('AI');
    expect(replaceState).not.toHaveBeenCalled();
  });
});
