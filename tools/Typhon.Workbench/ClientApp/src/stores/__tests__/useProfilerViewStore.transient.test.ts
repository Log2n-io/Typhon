/**
 * @vitest-environment jsdom
 *
 * Tests for the transient/committed viewport split (#345 Step 1). Pan/zoom in TimeArea writes
 * `transientViewRange` continuously; a debounced commit copies it into `viewRange` after
 * `viewRangeDebounceMs` of idle. `commitViewRange` writes both slots atomically and clears any
 * pending debounce — used for programmatic writes (URL deep-link, animation end, etc.).
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useOptionsStore } from '@/stores/useOptionsStore';

const INITIAL = { startUs: 0, endUs: 1_000_000 };

function setDebounce(ms: number | undefined): void {
  const prev = useOptionsStore.getState().options;
  useOptionsStore.setState({
    options: {
      ...prev,
      profiler: { ...prev.profiler, viewRangeDebounceMs: ms } as typeof prev.profiler,
    },
  });
}

describe('useProfilerViewStore — transient/committed split', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    useProfilerViewStore.setState({
      viewRange: INITIAL,
      transientViewRange: INITIAL,
    });
    setDebounce(150);
  });
  afterEach(() => {
    vi.useRealTimers();
    setDebounce(undefined);
  });

  it('setTransientViewRange updates the transient slot immediately', () => {
    const r = { startUs: 100, endUs: 200 };
    useProfilerViewStore.getState().setTransientViewRange(r);
    expect(useProfilerViewStore.getState().transientViewRange).toEqual(r);
    // viewRange unchanged until the debounce fires.
    expect(useProfilerViewStore.getState().viewRange).toEqual(INITIAL);
  });

  it('rapid setTransientViewRange bursts coalesce into one viewRange commit', () => {
    const s = useProfilerViewStore.getState();
    s.setTransientViewRange({ startUs: 10, endUs: 20 });
    s.setTransientViewRange({ startUs: 30, endUs: 40 });
    s.setTransientViewRange({ startUs: 50, endUs: 60 });
    s.setTransientViewRange({ startUs: 70, endUs: 80 });
    // Track viewRange writes by subscription — should fire exactly once after the debounce.
    let writes = 0;
    const unsub = useProfilerViewStore.subscribe((cur, prev) => {
      if (cur.viewRange !== prev.viewRange) writes++;
    });
    vi.advanceTimersByTime(149);
    expect(writes).toBe(0);
    vi.advanceTimersByTime(1);
    expect(writes).toBe(1);
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 70, endUs: 80 });
    unsub();
  });

  it('the committed viewRange reflects the LATEST transient value at fire time', () => {
    // Even if the closure captured an earlier `r`, the commit reads `get().transientViewRange`.
    const s = useProfilerViewStore.getState();
    s.setTransientViewRange({ startUs: 100, endUs: 200 });
    vi.advanceTimersByTime(50); // mid-debounce
    s.setTransientViewRange({ startUs: 300, endUs: 400 });
    vi.advanceTimersByTime(150);
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 300, endUs: 400 });
  });

  it('commitViewRange writes both slots atomically and bypasses pending debounce', () => {
    const s = useProfilerViewStore.getState();
    s.setTransientViewRange({ startUs: 10, endUs: 20 });
    // viewRange not yet committed.
    expect(useProfilerViewStore.getState().viewRange).toEqual(INITIAL);
    s.commitViewRange({ startUs: 999, endUs: 1_999 });
    // Both slots reflect the commit immediately.
    expect(useProfilerViewStore.getState().transientViewRange).toEqual({ startUs: 999, endUs: 1_999 });
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 999, endUs: 1_999 });
    // The pending debounce was cleared — advancing timers must NOT clobber with the old transient.
    vi.advanceTimersByTime(500);
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 999, endUs: 1_999 });
  });

  it('debounceMs = 0 commits synchronously', () => {
    setDebounce(0);
    const s = useProfilerViewStore.getState();
    s.setTransientViewRange({ startUs: 100, endUs: 200 });
    // No timer advance needed.
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 100, endUs: 200 });
  });

  it('debounceMs out of range clamps to default (negative → 150ms)', () => {
    setDebounce(-50);
    const s = useProfilerViewStore.getState();
    s.setTransientViewRange({ startUs: 100, endUs: 200 });
    expect(useProfilerViewStore.getState().viewRange).toEqual(INITIAL);
    vi.advanceTimersByTime(150);
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 100, endUs: 200 });
  });

  it('debounceMs above 5000 caps at 5000ms', () => {
    setDebounce(99999);
    const s = useProfilerViewStore.getState();
    s.setTransientViewRange({ startUs: 1, endUs: 2 });
    vi.advanceTimersByTime(4999);
    expect(useProfilerViewStore.getState().viewRange).toEqual(INITIAL);
    vi.advanceTimersByTime(1);
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 1, endUs: 2 });
  });

  it('missing viewRangeDebounceMs in options falls back to 150ms default', () => {
    setDebounce(undefined);
    const s = useProfilerViewStore.getState();
    s.setTransientViewRange({ startUs: 1, endUs: 2 });
    vi.advanceTimersByTime(150);
    expect(useProfilerViewStore.getState().viewRange).toEqual({ startUs: 1, endUs: 2 });
  });

});
