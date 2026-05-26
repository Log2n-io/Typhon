// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { TickData } from '@/libs/profiler/model/traceModel';
import { GaugeId } from '@/libs/profiler/model/types';

// Hoisted spy — vi.mock factories below reference it, so the mocks can be reconfigured per-test.
const cacheState = vi.hoisted(() => ({
  ticks: [] as TickData[],
}));

vi.mock('@/hooks/profiler/useProfilerCache', () => ({
  useProfilerCache: () => ({
    ticks: cacheState.ticks,
    traceMetadata: null,
    gaugeData: {
      gaugeSeries: new Map(),
      gaugeCapacities: new Map(),
      memoryAllocEvents: [],
      gcEvents: [],
      gcSuspensions: [],
      threadNames: new Map(),
      offCpuBySlot: new Map(),
    },
    threadInfos: new Map(),
    pendingRangesUs: [],
  }),
}));

// Mock aggregateGaugeData — its full pipeline touches many TickData fields (contextSwitches, etc.)
// that this test doesn't care about. Scope of this test = useLiveGaugeData's windowing logic.
// The aggregation itself is exercised through useProfilerCache's own tests.
vi.mock('@/libs/profiler/model/traceModel', async () => {
  const actual = await vi.importActual<typeof import('@/libs/profiler/model/traceModel')>('@/libs/profiler/model/traceModel');
  return {
    ...actual,
    aggregateGaugeData: (ticks: TickData[]) => {
      // Lightweight stand-in: walk gaugeSnapshot.values into a series-per-gauge map.
      const gaugeSeries = new Map();
      for (const tick of ticks) {
        if (tick.gaugeSnapshot === undefined) continue;
        for (const [id, value] of tick.gaugeSnapshot.values) {
          let s = gaugeSeries.get(id);
          if (s === undefined) {
            s = { id, samples: [] };
            gaugeSeries.set(id, s);
          }
          s.samples.push({ tickNumber: tick.gaugeSnapshot.tickNumber, timestampUs: tick.gaugeSnapshot.timestampUs, value });
        }
      }
      return {
        gaugeSeries,
        gaugeCapacities: new Map(),
        memoryAllocEvents: [],
        gcEvents: [],
        gcSuspensions: [],
        threadNames: new Map(),
        threadKinds: new Map(),
        offCpuBySlot: new Map(),
      };
    },
  };
});

import { useLiveGaugeData } from '@/hooks/profiler/useLiveGaugeData';

/** Minimal TickData with the fields useLiveGaugeData inspects. */
function makeTick(tickNumber: number, startUs: number, endUs: number, gaugeValues: Array<[GaugeId, number]> = []): TickData {
  return {
    tickNumber,
    startUs,
    endUs,
    durationUs: endUs - startUs,
    chunks: [],
    phases: [],
    phaseMarkers: [],
    skips: [],
    spans: [],
    spanEndMaxRunning: new Float64Array(0),
    spansByThreadSlot: new Map(),
    spanEndMaxByThreadSlot: new Map(),
    maxSpanDepthByThreadSlot: new Map(),
    gaugeSnapshot: gaugeValues.length > 0
      ? { tickNumber, timestampUs: startUs, values: new Map(gaugeValues) }
      : undefined,
    memoryAllocEvents: [],
    gcEvents: [],
    gcSuspensions: [],
    threadInfos: [],
  } as unknown as TickData;
}

afterEach(() => {
  cacheState.ticks = [];
});

describe('useLiveGaugeData — windowing + aggregation', () => {
  it('returns empty bundle when sessionId is null', () => {
    cacheState.ticks = [makeTick(0, 0, 1_000)];
    const { result } = renderHook(() => useLiveGaugeData(null));
    expect(result.current.hasData).toBe(false);
    expect(result.current.windowedTicks.length).toBe(0);
  });

  it('returns empty bundle when the cache has no ticks yet', () => {
    cacheState.ticks = [];
    const { result } = renderHook(() => useLiveGaugeData('sess-A'));
    expect(result.current.hasData).toBe(false);
  });

  it('windows ticks to the last N µs from the latest tick endUs', () => {
    // 5 ticks: 0..1ms, 10ms..11ms, 30s..30.001s, 59s..59.001s, 70s..70.001s
    // With a 60-s window pinned to the last tick (70.001s), windowStartUs = 10.001s.
    // So ticks with endUs >= 10.001s = the 30 s, 59 s, and 70 s ticks (3 entries).
    cacheState.ticks = [
      makeTick(0, 0, 1_000),
      makeTick(1, 10_000, 11_000),
      makeTick(2, 30_000_000, 30_001_000),
      makeTick(3, 59_000_000, 59_001_000),
      makeTick(4, 70_000_000, 70_001_000),
    ];
    const { result } = renderHook(() => useLiveGaugeData('sess-A', 60_000));
    expect(result.current.hasData).toBe(true);
    expect(result.current.windowedTicks.length).toBe(3);
    expect(result.current.windowEndUs).toBe(70_001_000);
    expect(result.current.windowStartUs).toBe(70_001_000 - 60_000 * 1000);
  });

  it('re-aggregates gauge series for the windowed subset only', () => {
    // Two ticks with the same gauge id, but only one inside the window.
    cacheState.ticks = [
      makeTick(0, 0, 1_000, [[GaugeId.MemoryUnmanagedTotalBytes, 100]]),
      makeTick(1, 70_000_000, 70_001_000, [[GaugeId.MemoryUnmanagedTotalBytes, 200]]),
    ];
    const { result } = renderHook(() => useLiveGaugeData('sess-A', 60_000));
    const memSeries = result.current.gaugeData.gaugeSeries.get(GaugeId.MemoryUnmanagedTotalBytes);
    expect(memSeries).toBeTruthy();
    // Only the in-window tick's sample should appear.
    expect(memSeries!.samples.length).toBe(1);
    expect(memSeries!.samples[0].value).toBe(200);
  });

  it('respects a custom windowMs', () => {
    cacheState.ticks = [
      makeTick(0, 0, 1_000),
      makeTick(1, 5_000_000, 5_001_000),
      makeTick(2, 9_000_000, 9_001_000),
    ];
    // 4-s window from tick 2's endUs (9.001s) → windowStartUs = 5.001s; tick 1's endUs (5.001s) is in (>=).
    const { result } = renderHook(() => useLiveGaugeData('sess-A', 4_000));
    expect(result.current.windowedTicks.length).toBe(2); // ticks 1 + 2
    expect(result.current.windowStartUs).toBe(9_001_000 - 4_000_000);
  });
});
