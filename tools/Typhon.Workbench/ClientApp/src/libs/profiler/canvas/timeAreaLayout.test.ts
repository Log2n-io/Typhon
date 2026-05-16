import { describe, expect, it } from 'vitest';
import { buildLayout, deriveSlotInfo, getVisibleTicks } from './timeAreaLayout';
import type { SpanData, TickData } from '@/libs/profiler/model/traceModel';

/**
 * Pure-module tests — buildLayout walks a deterministic track sequence and getVisibleTicks
 * filters by half-open viewport overlap. No DOM / canvas / React required.
 */

describe('timeAreaLayout.buildLayout', () => {
  it('walks ruler → slot lanes → fixed tracks and skips gauges', () => {
    const { tracks } = buildLayout({
      activeSlots: [0, 3, 7],
      slotsWithChunks: new Set([0, 3]),
      spanMaxDepthBySlot: new Map([[0, 2], [3, 0]]),
      threadNames: { 0: 'Main', 3: 'Worker' },
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [],
      systemNames: null,
      perSystemLanesVisible: false,
    });

    const ids = tracks.map((t) => t.id);
    expect(ids).toEqual([
      'ruler',
      'slot-0', 'slot-3', 'slot-7',
      'phases', 'page-cache', 'disk-io', 'transactions', 'wal', 'checkpoint',
    ]);
    // Gauges MUST NOT be present — that's 2c's seam.
    expect(ids.some((id) => id.startsWith('gauge-'))).toBe(false);
  });

  it('prefers threadNames[slot] over the generic Slot N label', () => {
    const { tracks } = buildLayout({
      activeSlots: [0, 1],
      slotsWithChunks: new Set([0]),
      spanMaxDepthBySlot: new Map(),
      threadNames: { 0: 'MainThread' },
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [],
      systemNames: null,
      perSystemLanesVisible: false,
    });
    expect(tracks.find((t) => t.id === 'slot-0')?.label).toBe('MainThread');
    expect(tracks.find((t) => t.id === 'slot-1')?.label).toBe('Slot 1'); // fallback
  });

  it('omits the chunk row for span-only slots', () => {
    const { tracks } = buildLayout({
      activeSlots: [0, 1],
      slotsWithChunks: new Set([0]), // slot 1 has spans but no chunks
      spanMaxDepthBySlot: new Map([[1, 1]]),
      threadNames: null,
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [],
      systemNames: null,
      perSystemLanesVisible: false,
    });
    expect(tracks.find((t) => t.id === 'slot-0')?.chunkRowHeight).toBeGreaterThan(0);
    expect(tracks.find((t) => t.id === 'slot-1')?.chunkRowHeight).toBe(0);
  });

  it('coerces `double` on non-gauge tracks to `expanded` (gauge-only state)', () => {
    const { tracks } = buildLayout({
      activeSlots: [],
      slotsWithChunks: new Set(),
      spanMaxDepthBySlot: new Map(),
      threadNames: null,
      collapseState: { phases: 'double' },
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [],
      systemNames: null,
      perSystemLanesVisible: false,
    });
    expect(tracks.find((t) => t.id === 'phases')?.state).toBe('expanded');
  });

  it('y-positions are monotonically increasing and totalHeight equals last track bottom', () => {
    const { tracks, totalHeight } = buildLayout({
      activeSlots: [0],
      slotsWithChunks: new Set([0]),
      spanMaxDepthBySlot: new Map([[0, 1]]),
      threadNames: null,
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [],
      systemNames: null,
      perSystemLanesVisible: false,
    });
    for (let i = 1; i < tracks.length; i++) {
      expect(tracks[i].y).toBeGreaterThanOrEqual(tracks[i - 1].y);
    }
    const last = tracks[tracks.length - 1];
    expect(totalHeight).toBeGreaterThan(last.y);
  });

  it('emits one system-N track per active system when perSystemLanesVisible=true', () => {
    const { tracks } = buildLayout({
      activeSlots: [],
      slotsWithChunks: new Set(),
      spanMaxDepthBySlot: new Map(),
      threadNames: null,
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [0, 3, 7],
      systemNames: { 0: 'Movement', 3: 'Rendering', 7: 'Physics' },
      perSystemLanesVisible: true,
    });
    expect(tracks.find((t) => t.id === 'system-0')?.label).toBe('Movement');
    expect(tracks.find((t) => t.id === 'system-3')?.label).toBe('Rendering');
    expect(tracks.find((t) => t.id === 'system-7')?.label).toBe('Physics');
    // Default state is 'summary' so a many-system trace doesn't eat the screen on first open.
    expect(tracks.find((t) => t.id === 'system-0')?.state).toBe('summary');
  });

  it('omits system-* tracks when perSystemLanesVisible=false', () => {
    const { tracks } = buildLayout({
      activeSlots: [],
      slotsWithChunks: new Set(),
      spanMaxDepthBySlot: new Map(),
      threadNames: null,
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [0, 1, 2],
      systemNames: null,
      perSystemLanesVisible: false,
    });
    expect(tracks.some((t) => t.id.startsWith('system-'))).toBe(false);
  });

  it('honours per-system collapseState override (summary → expanded)', () => {
    const { tracks } = buildLayout({
      activeSlots: [],
      slotsWithChunks: new Set(),
      spanMaxDepthBySlot: new Map(),
      threadNames: null,
      collapseState: { 'system-5': 'expanded' },
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [5],
      systemNames: null,
      perSystemLanesVisible: true,
    });
    expect(tracks.find((t) => t.id === 'system-5')?.state).toBe('expanded');
  });
});

describe('timeAreaLayout.getVisibleTicks', () => {
  const tick = (tickNumber: number, startUs: number, durationUs: number): TickData => ({
    tickNumber,
    startUs,
    endUs: startUs + durationUs,
    durationUs,
    chunks: [],
    phases: [],
    phaseMarkers: [],
    skips: [],
    spans: [],
    spanEndMaxRunning: new Float64Array(0),
    spansByThreadSlot: new Map(),
    spanEndMaxByThreadSlot: new Map(),
    spanMaxDepthByThreadSlot: new Map(),
    diskReads: [], diskReadsEndMax: new Float64Array(0),
    diskWrites: [], diskWritesEndMax: new Float64Array(0),
    cacheMisses: [], cacheMissesEndMax: new Float64Array(0),
    cacheFlushes: [], cacheFlushesEndMax: new Float64Array(0),
    cacheFetch: [], cacheFetchEndMax: new Float64Array(0),
    cacheAlloc: [], cacheAllocEndMax: new Float64Array(0),
    cacheEvict: [], cacheEvictEndMax: new Float64Array(0),
    txCommits: [], txCommitsEndMax: new Float64Array(0),
    txRollbacks: [], txRollbacksEndMax: new Float64Array(0),
    txPersists: [], txPersistsEndMax: new Float64Array(0),
    walFlushes: [], walFlushesEndMax: new Float64Array(0),
    walWaits: [], walWaitsEndMax: new Float64Array(0),
    checkpointCycles: [], checkpointCyclesEndMax: new Float64Array(0),
    systemDurations: new Map(),
    memoryAllocEvents: [],
    gcEvents: [],
    gcSuspensions: [],
    threadInfos: [],
    contextSwitches: new Map(),
    rawEvents: [],
  });

  it('returns empty when the viewRange is degenerate', () => {
    expect(getVisibleTicks([tick(0, 0, 100)], { startUs: 50, endUs: 50 })).toEqual([]);
    expect(getVisibleTicks([tick(0, 0, 100)], { startUs: 100, endUs: 50 })).toEqual([]);
  });

  it('includes ticks that partially overlap the viewport edges', () => {
    const ticks = [
      tick(0, 0, 100),    // [0, 100)
      tick(1, 100, 100),  // [100, 200)
      tick(2, 200, 100),  // [200, 300)
    ];
    const v = getVisibleTicks(ticks, { startUs: 80, endUs: 220 });
    expect(v.map((t) => t.tickNumber)).toEqual([0, 1, 2]);
  });

  it('excludes a tick that begins exactly at viewRange.endUs (half-open right edge)', () => {
    const ticks = [tick(0, 0, 100), tick(1, 100, 100)];
    const v = getVisibleTicks(ticks, { startUs: 0, endUs: 100 });
    expect(v.map((t) => t.tickNumber)).toEqual([0]);
  });
});

// ═════════════════════════════════════════════════════════════════════════════════════════════════
// deriveSlotInfo — greedy interval packing that mutates span.renderDepth
// ═════════════════════════════════════════════════════════════════════════════════════════════════

describe('timeAreaLayout.deriveSlotInfo', () => {
  const SPAN_KIND = 10 as unknown as SpanData['kind'];

  const span = (start: number, end: number, threadSlot = 0, name = 's'): SpanData => ({
    kind: SPAN_KIND,
    name,
    threadSlot,
    startUs: start,
    endUs: end,
    durationUs: end - start,
  });

  const tickWithSpans = (tickNumber: number, tickStart: number, tickDur: number, spans: SpanData[]): TickData => {
    const byThread = new Map<number, SpanData[]>();
    for (const s of spans) {
      let arr = byThread.get(s.threadSlot);
      if (!arr) { arr = []; byThread.set(s.threadSlot, arr); }
      arr.push(s);
    }
    const base: TickData = {
      tickNumber,
      startUs: tickStart,
      endUs: tickStart + tickDur,
      durationUs: tickDur,
      chunks: [],
      phases: [],
      phaseMarkers: [],
      skips: [],
      spans,
      spanEndMaxRunning: new Float64Array(0),
      spansByThreadSlot: byThread,
      spanEndMaxByThreadSlot: new Map(),
      spanMaxDepthByThreadSlot: new Map(),
      diskReads: [], diskReadsEndMax: new Float64Array(0),
      diskWrites: [], diskWritesEndMax: new Float64Array(0),
      cacheMisses: [], cacheMissesEndMax: new Float64Array(0),
      cacheFlushes: [], cacheFlushesEndMax: new Float64Array(0),
      cacheFetch: [], cacheFetchEndMax: new Float64Array(0),
      cacheAlloc: [], cacheAllocEndMax: new Float64Array(0),
      cacheEvict: [], cacheEvictEndMax: new Float64Array(0),
      txCommits: [], txCommitsEndMax: new Float64Array(0),
      txRollbacks: [], txRollbacksEndMax: new Float64Array(0),
      txPersists: [], txPersistsEndMax: new Float64Array(0),
      walFlushes: [], walFlushesEndMax: new Float64Array(0),
      walWaits: [], walWaitsEndMax: new Float64Array(0),
      checkpointCycles: [], checkpointCyclesEndMax: new Float64Array(0),
      systemDurations: new Map(),
      memoryAllocEvents: [],
      gcEvents: [],
      gcSuspensions: [],
      threadInfos: [],
      contextSwitches: new Map(),
      rawEvents: [],
    };
    return base;
  };

  it('puts non-overlapping spans on the same row', () => {
    const s1 = span(0, 100);
    const s2 = span(100, 200);
    const s3 = span(200, 300);
    deriveSlotInfo([tickWithSpans(0, 0, 300, [s1, s2, s3])]);
    expect(s1.renderDepth).toBe(0);
    expect(s2.renderDepth).toBe(0);
    expect(s3.renderDepth).toBe(0);
  });

  it('puts overlapping spans on different rows', () => {
    const outer = span(0, 300);
    const inner1 = span(50, 100);
    const inner2 = span(120, 180);
    deriveSlotInfo([tickWithSpans(0, 0, 300, [outer, inner1, inner2])]);
    expect(outer.renderDepth).toBe(0);
    // Both inner spans overlap outer, so they go on row 1. They don't overlap each other, so they
    // can share row 1 by time-sequencing.
    expect(inner1.renderDepth).toBe(1);
    expect(inner2.renderDepth).toBe(1);
  });

  it('gives the longer outer span the lower row when two spans start together (endUs desc tiebreak)', () => {
    const outer = span(0, 200, 0, 'outer');
    const inner = span(0, 50, 0, 'inner');
    deriveSlotInfo([tickWithSpans(0, 0, 200, [outer, inner])]);
    // Tiebreak on startUs=0 is endUs desc, so outer (endUs 200) wins row 0; inner goes to row 1.
    expect(outer.renderDepth).toBe(0);
    expect(inner.renderDepth).toBe(1);
  });

  it('reports spanMaxDepthBySlot equal to the max renderDepth actually assigned', () => {
    // 3 mutually overlapping spans → 3 distinct rows → max renderDepth = 2.
    const a = span(0, 100);
    const b = span(10, 90);
    const c = span(20, 80);
    const { spanMaxDepthBySlot } = deriveSlotInfo([tickWithSpans(0, 0, 100, [a, b, c])]);
    expect(spanMaxDepthBySlot.get(0)).toBe(2);
    const rows = [a, b, c].map(s => s.renderDepth!).sort((x, y) => x - y);
    expect(rows).toEqual([0, 1, 2]);
  });

  it('packs spans across multiple ticks belonging to the same slot', () => {
    // Span A lives in tick 0 (its start tick), span B lives in tick 1 — but both are on the same
    // slot. They don't overlap, so both should land on row 0.
    const a = span(0, 50, 0);
    const b = span(60, 100, 0);
    deriveSlotInfo([
      tickWithSpans(0, 0, 100, [a]),
      tickWithSpans(1, 100, 100, [b]),
    ]);
    expect(a.renderDepth).toBe(0);
    expect(b.renderDepth).toBe(0);
  });

  it('keeps per-slot packing independent (spans in different slots share row 0)', () => {
    const a = span(0, 100, 0);
    const b = span(0, 100, 1); // same time range but different slot
    deriveSlotInfo([tickWithSpans(0, 0, 100, [a, b])]);
    expect(a.renderDepth).toBe(0);
    expect(b.renderDepth).toBe(0);
  });
});

describe('timeAreaLayout.buildLayout — filter visibility', () => {
  it('skips slots whose slotVisibility[idx] is false', () => {
    const { tracks } = buildLayout({
      activeSlots: [0, 1, 2],
      slotsWithChunks: new Set(),
      spanMaxDepthBySlot: new Map(),
      threadNames: null,
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [],
      systemNames: null,
      perSystemLanesVisible: false,
      slotVisibility: { 1: false },
    });
    const ids = tracks.map((t) => t.id);
    expect(ids).toContain('slot-0');
    expect(ids).not.toContain('slot-1');
    expect(ids).toContain('slot-2');
  });

  it('skips systems whose systemVisibility[idx] is false', () => {
    const { tracks } = buildLayout({
      activeSlots: [],
      slotsWithChunks: new Set(),
      spanMaxDepthBySlot: new Map(),
      threadNames: null,
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [3, 5, 7],
      systemNames: null,
      perSystemLanesVisible: true,
      systemVisibility: { 5: false },
    });
    const ids = tracks.map((t) => t.id);
    expect(ids).toContain('system-3');
    expect(ids).not.toContain('system-5');
    expect(ids).toContain('system-7');
  });

  it('skips engine-op tracks whose engineOpVisibility[id] is false', () => {
    const { tracks } = buildLayout({
      activeSlots: [],
      slotsWithChunks: new Set(),
      spanMaxDepthBySlot: new Map(),
      threadNames: null,
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [],
      systemNames: null,
      perSystemLanesVisible: false,
      engineOpVisibility: { 'page-cache': false, 'disk-io': false },
    });
    const ids = tracks.map((t) => t.id);
    expect(ids).toContain('phases');
    expect(ids).not.toContain('page-cache');
    expect(ids).not.toContain('disk-io');
    expect(ids).toContain('transactions');
    expect(ids).toContain('wal');
    expect(ids).toContain('checkpoint');
  });

  it('skips gauges whose gaugeVisibility[id] is false', () => {
    const { tracks } = buildLayout({
      activeSlots: [],
      slotsWithChunks: new Set(),
      spanMaxDepthBySlot: new Map(),
      threadNames: null,
      collapseState: {},
      gaugeRegionVisible: true,
      gaugeCollapse: {},
      activeSystems: [],
      systemNames: null,
      perSystemLanesVisible: false,
      gaugeVisibility: { 'gauge-memory': false, 'gauge-wal': false },
    });
    const ids = tracks.map((t) => t.id);
    expect(ids).not.toContain('gauge-memory');
    expect(ids).not.toContain('gauge-wal');
    expect(ids).toContain('gauge-persistence'); // others stay
  });

  it('absent visibility maps preserve the legacy "everything visible" behavior', () => {
    const { tracks } = buildLayout({
      activeSlots: [0, 1],
      slotsWithChunks: new Set(),
      spanMaxDepthBySlot: new Map(),
      threadNames: null,
      collapseState: {},
      gaugeRegionVisible: false,
      gaugeCollapse: {},
      activeSystems: [3],
      systemNames: null,
      perSystemLanesVisible: true,
    });
    const ids = tracks.map((t) => t.id);
    expect(ids).toEqual([
      'ruler',
      'slot-0', 'slot-1',
      'system-3',
      'phases', 'page-cache', 'disk-io', 'transactions', 'wal', 'checkpoint',
    ]);
  });
});
