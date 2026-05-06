import { describe, expect, it } from 'vitest';
import { computeSelectionStats } from '../selectionStats';
import type { TickData, SpanData, ChunkSpan, GcSuspensionEvent } from '@/libs/profiler/model/traceModel';
import type { TickSummary } from '@/libs/profiler/model/types';
import { TraceEventKind } from '@/libs/profiler/model/types';

/**
 * computeSelectionStats — pure aggregation. Tests cover:
 *  - degenerate range returns null
 *  - tick / span / chunk / gc range-clip math
 *  - top-N sorting (descending by total duration)
 *  - coverage counts (loaded vs total)
 */

function makeTick(overrides: Partial<TickData> = {}): TickData {
  const empty = new Float64Array(0);
  return {
    tickNumber: 1,
    startUs: 0,
    endUs: 100,
    durationUs: 100,
    chunks: [],
    phases: [],
    phaseMarkers: [],
    skips: [],
    spans: [],
    spanEndMaxRunning: empty,
    spansByThreadSlot: new Map(),
    spanEndMaxByThreadSlot: new Map(),
    spanMaxDepthByThreadSlot: new Map(),
    diskReads: [], diskReadsEndMax: empty,
    diskWrites: [], diskWritesEndMax: empty,
    cacheMisses: [], cacheMissesEndMax: empty,
    cacheFlushes: [], cacheFlushesEndMax: empty,
    cacheFetch: [], cacheFetchEndMax: empty,
    cacheAlloc: [], cacheAllocEndMax: empty,
    cacheEvict: [], cacheEvictEndMax: empty,
    txCommits: [], txCommitsEndMax: empty,
    txRollbacks: [], txRollbacksEndMax: empty,
    txPersists: [], txPersistsEndMax: empty,
    walFlushes: [], walFlushesEndMax: empty,
    walWaits: [], walWaitsEndMax: empty,
    checkpointCycles: [], checkpointCyclesEndMax: empty,
    systemDurations: new Map(),
    rawEvents: [],
    threadInfos: [],
    memoryAllocEvents: [],
    gcEvents: [],
    gcSuspensions: [],
    ...overrides,
  };
}

function span(name: string, startUs: number, endUs: number, threadSlot = 0): SpanData {
  return {
    kind: TraceEventKind.BTreeInsert,
    name,
    threadSlot,
    startUs,
    endUs,
    durationUs: endUs - startUs,
  };
}

function chunk(systemIndex: number, systemName: string, startUs: number, endUs: number): ChunkSpan {
  return {
    systemIndex,
    systemName,
    chunkIndex: 0,
    threadSlot: 0,
    startUs,
    endUs,
    durationUs: endUs - startUs,
    entitiesProcessed: 0,
    totalChunks: 1,
    isParallel: false,
  };
}

function gcSus(startUs: number, durationUs: number): GcSuspensionEvent {
  return { tickNumber: 1, startUs, durationUs, threadSlot: 0 };
}

function summary(tickNumber: number, startUs: number, durationUs: number): TickSummary {
  return {
    tickNumber,
    startUs,
    durationUs,
    eventCount: 0,
    maxSystemDurationUs: 0,
    activeSystemsBitmask: '0',
  };
}

describe('computeSelectionStats — degenerate inputs', () => {
  it('returns null when endUs <= startUs', () => {
    expect(computeSelectionStats([], [], { startUs: 100, endUs: 100 })).toBeNull();
    expect(computeSelectionStats([], [], { startUs: 200, endUs: 100 })).toBeNull();
  });

  it('returns zeros and empty top-N when no ticks intersect', () => {
    const stats = computeSelectionStats([], [], { startUs: 0, endUs: 1000 });
    expect(stats).not.toBeNull();
    expect(stats!.ticksLoaded).toBe(0);
    expect(stats!.ticksTotal).toBe(0);
    expect(stats!.eventsLoaded).toBe(0);
    expect(stats!.tickDurationStats).toBeNull();
    expect(stats!.spanGroups).toEqual([]);
    expect(stats!.topSystemsByTotal).toEqual([]);
    expect(stats!.gcPauseTotalUs).toBe(0);
  });
});

describe('computeSelectionStats — coverage counts', () => {
  it('counts ticksTotal from summaries even when no ticks are loaded', () => {
    const summaries = [
      summary(1, 0, 100),
      summary(2, 100, 100),
      summary(3, 200, 100),
      summary(4, 300, 100),
    ];
    // viewRange [50, 250) overlaps ticks 1, 2, 3 (1: 0..100 overlaps 50; 3: 200..300 overlaps up to 250).
    const stats = computeSelectionStats([], summaries, { startUs: 50, endUs: 250 });
    expect(stats!.ticksTotal).toBe(3);
    expect(stats!.ticksLoaded).toBe(0);
  });

  it('reports a partial-coverage state when loaded < total', () => {
    const summaries = [summary(1, 0, 100), summary(2, 100, 100), summary(3, 200, 100)];
    const ticks = [makeTick({ tickNumber: 1, startUs: 0, endUs: 100, durationUs: 100 })];
    const stats = computeSelectionStats(ticks, summaries, { startUs: 0, endUs: 300 });
    expect(stats!.ticksTotal).toBe(3);
    expect(stats!.ticksLoaded).toBe(1);
  });
});

describe('computeSelectionStats — span aggregation', () => {
  it('groups spans by name with full per-group stats (count / total / min / avg / max)', () => {
    const ticks = [makeTick({
      spans: [
        span('Alpha', 0, 50),
        span('Alpha', 60, 70),
        span('Beta',  0, 80),
        span('Gamma', 0, 5),
      ],
    })];
    const stats = computeSelectionStats(ticks, [], { startUs: 0, endUs: 100 });
    // spanGroups is unsorted; consumers slice + sort for their surface. Assert by name lookup.
    const byName = (n: string) => stats!.spanGroups.find((g) => g.name === n)!;
    expect(byName('Beta').count).toBe(1);
    expect(byName('Beta').totalUs).toBe(80);
    expect(byName('Beta').maxUs).toBe(80);
    expect(byName('Alpha').count).toBe(2);
    expect(byName('Alpha').totalUs).toBe(60); // 50 + 10
    expect(byName('Alpha').minUs).toBe(10);
    expect(byName('Alpha').maxUs).toBe(50);
    expect(byName('Alpha').avgUs).toBe(30);
    expect(byName('Gamma').totalUs).toBe(5);
  });

  it('records the worst-instance span per group for click-to-jump', () => {
    const slow = span('Foo', 0, 100);
    const fast = span('Foo', 200, 210);
    const ticks = [makeTick({ spans: [slow, fast], endUs: 300, durationUs: 300 })];
    const stats = computeSelectionStats(ticks, [], { startUs: 0, endUs: 300 });
    expect(stats!.spanGroups[0].worstSpan).toBe(slow);
  });

  it('clips spans that straddle the range boundary', () => {
    const ticks = [makeTick({
      // span 0..100, range 50..200 → contributes 50 (the [50, 100) portion).
      spans: [span('Edge', 0, 100)],
    })];
    const stats = computeSelectionStats(ticks, [], { startUs: 50, endUs: 200 });
    expect(stats!.spanGroups[0].totalUs).toBe(50);
  });
});

describe('computeSelectionStats — chunk + system aggregation', () => {
  it('groups chunks by systemIndex and sorts descending by wall-clock span', () => {
    const ticks = [makeTick({
      chunks: [
        chunk(1, 'PheroDecay', 0, 30),
        chunk(2, 'FillBuffer', 0, 50),
        chunk(2, 'FillBuffer', 60, 80),
      ],
    })];
    const stats = computeSelectionStats(ticks, [], { startUs: 0, endUs: 100 });
    expect(stats!.topSystemsByTotal[0].systemIndex).toBe(2);
    expect(stats!.topSystemsByTotal[0].systemName).toBe('FillBuffer');
    // Wall-clock span: min(0,60)=0 → max(50,80)=80 → 80µs (not the sum 50+20=70).
    expect(stats!.topSystemsByTotal[0].totalDurationUs).toBe(80);
    expect(stats!.topSystemsByTotal[0].count).toBe(2);
    expect(stats!.topSystemsByTotal[1].systemIndex).toBe(1);
  });

  it('uses wall-clock span (not CPU-sum) for overlapping parallel chunks', () => {
    // 4 parallel chunks all executing concurrently 0..660µs on different threads.
    // CPU-sum = 4 × 660 = 2640µs; wall-clock span = 660µs.
    const ticks = [makeTick({
      chunks: [
        { ...chunk(1, 'FillRender', 0, 660), threadSlot: 0 },
        { ...chunk(1, 'FillRender', 0, 660), threadSlot: 1 },
        { ...chunk(1, 'FillRender', 0, 660), threadSlot: 2 },
        { ...chunk(1, 'FillRender', 0, 660), threadSlot: 3 },
      ],
    })];
    const stats = computeSelectionStats(ticks, [], { startUs: 0, endUs: 1000 });
    expect(stats!.topSystemsByTotal[0].totalDurationUs).toBe(660);
    expect(stats!.topSystemsByTotal[0].count).toBe(4);
  });

  it('accumulates wall-clock spans across multiple ticks', () => {
    // Same system runs in two ticks, each contributing a 100µs wall-clock window.
    const ticks = [
      makeTick({ tickNumber: 1, startUs: 0,   endUs: 200,  durationUs: 200,  chunks: [chunk(1, 'Sys', 0,   100)] }),
      makeTick({ tickNumber: 2, startUs: 200,  endUs: 400,  durationUs: 200,  chunks: [chunk(1, 'Sys', 200, 300)] }),
    ];
    const stats = computeSelectionStats(ticks, [], { startUs: 0, endUs: 400 });
    expect(stats!.topSystemsByTotal[0].totalDurationUs).toBe(200); // 100 + 100
    expect(stats!.topSystemsByTotal[0].count).toBe(2);
  });
});

describe('computeSelectionStats — GC pause aggregation', () => {
  it('sums gc-suspension durations within range', () => {
    const ticks = [makeTick({
      gcSuspensions: [gcSus(10, 20), gcSus(50, 30), gcSus(200, 5)],
    })];
    // Range 0..100 captures the first two pauses (sums 50), excludes the third.
    const stats = computeSelectionStats(ticks, [], { startUs: 0, endUs: 100 });
    expect(stats!.gcPauseTotalUs).toBe(50);
    expect(stats!.gcSuspensionCount).toBe(2);
  });

  it('clips gc pauses that straddle the range boundary', () => {
    const ticks = [makeTick({
      gcSuspensions: [gcSus(80, 40)], // 80..120, range 0..100 → contributes 20 (the [80, 100) portion).
    })];
    const stats = computeSelectionStats(ticks, [], { startUs: 0, endUs: 100 });
    expect(stats!.gcPauseTotalUs).toBe(20);
  });
});

describe('computeSelectionStats — tick duration stats', () => {
  it('computes min / avg / p95 / max across loaded ticks', () => {
    const ticks = [
      makeTick({ tickNumber: 1, startUs: 0,   endUs: 10,  durationUs: 10  }),
      makeTick({ tickNumber: 2, startUs: 10,  endUs: 30,  durationUs: 20  }),
      makeTick({ tickNumber: 3, startUs: 30,  endUs: 80,  durationUs: 50  }),
      makeTick({ tickNumber: 4, startUs: 80,  endUs: 180, durationUs: 100 }),
    ];
    const stats = computeSelectionStats(ticks, [], { startUs: 0, endUs: 200 });
    expect(stats!.tickDurationStats!.minUs).toBe(10);
    expect(stats!.tickDurationStats!.maxUs).toBe(100);
    expect(stats!.tickDurationStats!.avgUs).toBe((10 + 20 + 50 + 100) / 4);
    // p95 of 4-element sorted array: floor(4 * 0.95) = 3 → last element = 100.
    expect(stats!.tickDurationStats!.p95Us).toBe(100);
  });
});
