// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useProfilerStatsWriter } from '../useProfilerStatsWriter';
import { useProfilerStatsStore } from '@/stores/useProfilerStatsStore';
import type { TickData } from '@/libs/profiler/model/traceModel';
import type { TickSummary } from '@/libs/profiler/model/types';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';

/**
 * Verifies the rAF-coalesce contract of {@link useProfilerStatsWriter}. The key promise — that
 * rapid deps flips during live capture (one per chunkAdded SSE event) collapse into a single
 * compute per frame — only matters if the test framework's rAF is observable and drivable. We
 * stub `requestAnimationFrame` / `cancelAnimationFrame` onto `globalThis` with an explicit queue
 * so the test never depends on a real timer loop — calling {@link flushRaf} fires every pending
 * callback in FIFO order, mirroring how the browser drains its rAF queue at the next paint.
 *
 * What's intentionally NOT covered here: the correctness of `computeSelectionStats` itself — that
 * lives in `selectionStats.test.ts`. We just assert "called with the latest inputs once per frame".
 */

// ── rAF queue stub ──────────────────────────────────────────────────────────────────────────────
const rafQueue: Array<{ id: number; cb: FrameRequestCallback }> = [];
let nextRafId = 1;

function flushRaf(): void {
  const drained = rafQueue.splice(0);
  for (const { cb } of drained) cb(performance.now());
}

beforeEach(() => {
  rafQueue.length = 0;
  nextRafId = 1;
  globalThis.requestAnimationFrame = ((cb: FrameRequestCallback) => {
    const id = nextRafId++;
    rafQueue.push({ id, cb });
    return id;
  }) as typeof requestAnimationFrame;
  globalThis.cancelAnimationFrame = ((id: number) => {
    const idx = rafQueue.findIndex((q) => q.id === id);
    if (idx >= 0) rafQueue.splice(idx, 1);
  }) as typeof cancelAnimationFrame;
  useProfilerStatsStore.setState({ stats: null });
});

afterEach(() => {
  vi.restoreAllMocks();
});

// ── Fixture helpers ─────────────────────────────────────────────────────────────────────────────
function makeTick(startUs: number, durationUs: number): TickData {
  return {
    tickNumber: 0,
    startUs,
    endUs: startUs + durationUs,
    durationUs,
    rawEvents: [],
    spans: [],
    chunks: [],
    gcSuspensions: [],
    threadInfos: new Map(),
  } as unknown as TickData;
}

const VR_FULL: TimeRange = { startUs: 0, endUs: 1_000_000 };
const SUMMARIES: TickSummary[] = [];

// ── Tests ───────────────────────────────────────────────────────────────────────────────────────
describe('useProfilerStatsWriter — rAF coalescing', () => {
  it('defers the first compute into the next frame (not synchronous)', () => {
    const ticks = [makeTick(0, 1000)];
    renderHook(() => useProfilerStatsWriter(ticks, SUMMARIES, VR_FULL));
    // Effect ran and scheduled an rAF, but the rAF has not fired yet — the store is still null.
    expect(useProfilerStatsStore.getState().stats).toBeNull();
    expect(rafQueue.length).toBe(1);

    flushRaf();
    expect(useProfilerStatsStore.getState().stats).not.toBeNull();
    expect(useProfilerStatsStore.getState().stats?.ticksLoaded).toBe(1);
  });

  it('collapses N rapid deps flips inside one frame into a single compute against the latest values', () => {
    const t1 = [makeTick(0, 1000)];
    const t2 = [makeTick(0, 1000), makeTick(1000, 1000)];
    const t3 = [makeTick(0, 1000), makeTick(1000, 1000), makeTick(2000, 1000)];

    const { rerender } = renderHook(
      ({ ticks }: { ticks: TickData[] }) => useProfilerStatsWriter(ticks, SUMMARIES, VR_FULL),
      { initialProps: { ticks: t1 } },
    );
    // Three rapid flips before the first frame fires. The cleanup-and-reschedule pattern means
    // the first two rAFs get cancelled; only the rAF from t3 survives — proving the coalesce.
    rerender({ ticks: t2 });
    rerender({ ticks: t3 });

    expect(rafQueue.length).toBe(1); // Cancellations kept the queue size at one.
    expect(useProfilerStatsStore.getState().stats).toBeNull();

    flushRaf();
    expect(useProfilerStatsStore.getState().stats?.ticksLoaded).toBe(3); // Latest (t3) won.
  });

  it('cancels the pending rAF on unmount — no spurious setStats after the component is gone', () => {
    const ticks = [makeTick(0, 1000)];
    const { unmount } = renderHook(() => useProfilerStatsWriter(ticks, SUMMARIES, VR_FULL));
    expect(rafQueue.length).toBe(1);
    unmount();
    expect(rafQueue.length).toBe(0); // Cleanup cancelled the queued frame.

    flushRaf(); // No-op, queue is empty.
    expect(useProfilerStatsStore.getState().stats).toBeNull();
  });

  it('recomputes when viewRange changes (deps change → new rAF scheduled)', () => {
    const ticks = [makeTick(0, 1000), makeTick(1000, 1000)];
    const { rerender } = renderHook(
      ({ vr }: { vr: TimeRange }) => useProfilerStatsWriter(ticks, SUMMARIES, vr),
      { initialProps: { vr: { startUs: 0, endUs: 500 } } },
    );
    flushRaf();
    expect(useProfilerStatsStore.getState().stats?.ticksLoaded).toBe(1); // First tick only.

    rerender({ vr: { startUs: 0, endUs: 2000 } });
    flushRaf();
    expect(useProfilerStatsStore.getState().stats?.ticksLoaded).toBe(2); // Both ticks now.
  });
});
