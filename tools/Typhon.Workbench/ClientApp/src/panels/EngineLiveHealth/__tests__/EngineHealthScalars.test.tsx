// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { TickData } from '@/libs/profiler/model/traceModel';
import { useSessionStore } from '@/stores/useSessionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';

// Mock the hook the component depends on — the windowing+aggregation logic has its own test.
const liveData = vi.hoisted(() => ({
  state: {
    windowedTicks: [] as TickData[],
    gaugeData: {
      gaugeSeries: new Map(),
      gaugeCapacities: new Map(),
      memoryAllocEvents: [] as unknown[],
      gcEvents: [] as unknown[],
      gcSuspensions: [] as Array<{ tickNumber: number; startUs: number; durationUs: number; threadSlot: number }>,
      offCpuBySlot: new Map(),
    },
    windowStartUs: 0,
    windowEndUs: 0,
    hasData: false,
  },
}));

vi.mock('@/hooks/profiler/useLiveGaugeData', () => ({
  useLiveGaugeData: () => liveData.state,
}));

import EngineHealthScalars from '../EngineHealthScalars';

function makeTick(durationUs: number): TickData {
  return { durationUs } as unknown as TickData;
}

afterEach(() => {
  cleanup();
  liveData.state = {
    windowedTicks: [],
    gaugeData: {
      gaugeSeries: new Map(),
      gaugeCapacities: new Map(),
      memoryAllocEvents: [],
      gcEvents: [],
      gcSuspensions: [],
      offCpuBySlot: new Map(),
    },
    windowStartUs: 0,
    windowEndUs: 0,
    hasData: false,
  };
});

describe('UC-OBS-02a — engine-runtime scalar tiles always render (PC-2 / AC4.4)', () => {
  it('renders dash placeholders for every tile when hasData is false', () => {
    useSessionStore.setState({ kind: 'attach', sessionId: 'sess-A', filePath: 'localhost:9100' });
    render(<EngineHealthScalars />);
    for (const id of ['tile-tick-rate', 'tile-p95-duration', 'tile-max-duration', 'tile-gc-pauses', 'tile-total-ticks']) {
      const el = screen.getByTestId(`${id}-value`);
      expect(el.textContent).toBe('—');
    }
  });
});

describe('UC-OBS-02b — scalars derive correctly from live data (AC4.4)', () => {
  it('computes tick rate, p95, max from windowed ticks', () => {
    // 6 ticks with durations 100, 200, 300, 400, 500, 600 µs spanning 10 s
    const durations = [100, 200, 300, 400, 500, 600];
    liveData.state = {
      windowedTicks: durations.map(makeTick),
      gaugeData: { gaugeSeries: new Map(), gaugeCapacities: new Map(), memoryAllocEvents: [], gcEvents: [], gcSuspensions: [], offCpuBySlot: new Map() },
      windowStartUs: 0,
      windowEndUs: 10_000_000, // 10 s span
      hasData: true,
    };
    useSessionStore.setState({ kind: 'attach', sessionId: 'sess-A', filePath: 'localhost:9100' });
    useProfilerSessionStore.setState({ metadata: null });

    render(<EngineHealthScalars />);

    // 6 ticks / 10 s = 0.6 Hz → formatted '0.60'
    expect(screen.getByTestId('tile-tick-rate-value').textContent).toBe('0.60');
    // Max = 600 µs → '600 µs'
    expect(screen.getByTestId('tile-max-duration-value').textContent).toBe('600 µs');
    // p95 with 6 values: floor(6*0.95) = 5 → durations[5] = 600 µs
    expect(screen.getByTestId('tile-p95-duration-value').textContent).toBe('600 µs');
  });

  it('reports GC pause count + total time over the window', () => {
    liveData.state = {
      windowedTicks: [makeTick(100)],
      gaugeData: {
        gaugeSeries: new Map(),
        gaugeCapacities: new Map(),
        memoryAllocEvents: [],
        gcEvents: [],
        gcSuspensions: [
          { tickNumber: 0, startUs: 0, durationUs: 1_500, threadSlot: 0 },
          { tickNumber: 1, startUs: 100, durationUs: 2_500, threadSlot: 0 },
        ],
        offCpuBySlot: new Map(),
      },
      windowStartUs: 0,
      windowEndUs: 60_000_000,
      hasData: true,
    };
    useSessionStore.setState({ kind: 'attach', sessionId: 'sess-A', filePath: 'localhost:9100' });
    useProfilerSessionStore.setState({ metadata: null });

    render(<EngineHealthScalars />);
    // 2 pauses, total = 4 ms (1500+2500 µs = 4000 µs = 4.0 ms)
    expect(screen.getByTestId('tile-gc-pauses-value').textContent).toBe('2 · 4.0 ms');
  });

  it('reads totalTicks from globalMetrics', () => {
    liveData.state = {
      windowedTicks: [makeTick(100)],
      gaugeData: { gaugeSeries: new Map(), gaugeCapacities: new Map(), memoryAllocEvents: [], gcEvents: [], gcSuspensions: [], offCpuBySlot: new Map() },
      windowStartUs: 0,
      windowEndUs: 60_000_000,
      hasData: true,
    };
    useSessionStore.setState({ kind: 'attach', sessionId: 'sess-A', filePath: 'localhost:9100' });
    useProfilerSessionStore.setState({
      metadata: {
        globalMetrics: {
          globalStartUs: 0,
          globalEndUs: 0,
          maxTickDurationUs: 0,
          maxSystemDurationUs: 0,
          p95TickDurationUs: 0,
          totalEvents: 0,
          totalTicks: 12345,
          systemAggregates: null,
        },
      } as unknown as ReturnType<typeof useProfilerSessionStore.getState>['metadata'],
    });

    render(<EngineHealthScalars />);
    // toLocaleString may use locale-grouping; test tolerates either.
    expect(screen.getByTestId('tile-total-ticks-value').textContent ?? '').toMatch(/12.?345/);
  });

  it('marks p95 tone as warn / bad above thresholds', () => {
    // 20 ticks all at 6 ms → p95 above the 5-ms 'bad' threshold.
    liveData.state = {
      windowedTicks: new Array(20).fill(0).map(() => makeTick(6_000)),
      gaugeData: { gaugeSeries: new Map(), gaugeCapacities: new Map(), memoryAllocEvents: [], gcEvents: [], gcSuspensions: [], offCpuBySlot: new Map() },
      windowStartUs: 0,
      windowEndUs: 60_000_000,
      hasData: true,
    };
    useSessionStore.setState({ kind: 'attach', sessionId: 'sess-A', filePath: 'localhost:9100' });
    useProfilerSessionStore.setState({ metadata: null });

    render(<EngineHealthScalars />);
    expect(screen.getByTestId('tile-p95-duration').getAttribute('data-tone')).toBe('bad');
  });
});
