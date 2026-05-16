import { describe, expect, it } from 'vitest';
import { aggregateGaugeData, processTickEvents } from '@/libs/profiler/model/traceModel';
import type { TickData } from '@/libs/profiler/model/traceModel';
import { OffCpuCategory, waitReasonToCategory, type TraceEvent } from '@/libs/profiler/model/types';

/**
 * Coverage for the off-CPU pipeline:
 *   - `processTickEvents` demux of kind-254 (ThreadContextSwitch) records into `TickData.contextSwitches`.
 *   - `aggregateGaugeData` cross-tick gap derivation into `OffCpuStore` (incl. cross-tick gaps and
 *     leading/trailing open-ended-gap suppression).
 *   - `waitReasonToCategory` raw-reason → coarse-category mapping.
 *
 * `TraceEventKind.ThreadContextSwitch` is 254; we pass the literal so the const-enum inlines correctly.
 */

const KIND_TICK_START = 0;
const KIND_TICK_END = 1;
const KIND_CSWITCH = 254;

function baseEvent(overrides: Partial<TraceEvent>): TraceEvent {
  return {
    kind: 0 as TraceEvent['kind'],
    threadSlot: 0,
    tickNumber: 1,
    timestampUs: 0,
    ...overrides,
  };
}

interface SliceSpec {
  targetSlotIdx: number;
  startUs: number;
  durationUs: number;
  waitReason?: number;
  threadState?: number;
  gettingIdle?: boolean;
  processorNumber?: number;
  readyTimeUs?: number;
}

function cswitch(s: SliceSpec): TraceEvent {
  return baseEvent({
    kind: KIND_CSWITCH as TraceEvent['kind'],
    timestampUs: s.startUs,
    targetSlotIdx: s.targetSlotIdx,
    durationUs: s.durationUs,
    waitReason: s.waitReason ?? 0,
    threadState: s.threadState ?? 0,
    gettingIdle: s.gettingIdle ?? false,
    processorNumber: s.processorNumber ?? 0,
    readyTimeUs: s.readyTimeUs ?? 0,
  });
}

function buildTick(tickNumber: number, startUs: number, endUs: number, slices: SliceSpec[]): TickData {
  const events: TraceEvent[] = [
    baseEvent({ kind: KIND_TICK_START as TraceEvent['kind'], timestampUs: startUs, tickNumber }),
    ...slices.map(cswitch),
    baseEvent({ kind: KIND_TICK_END as TraceEvent['kind'], timestampUs: endUs, tickNumber }),
  ];
  return processTickEvents(tickNumber, events, []);
}

describe('processTickEvents — ThreadContextSwitch demux', () => {
  it('collects kind-254 records into contextSwitches keyed by targetSlotIdx', () => {
    const tick = buildTick(1, 0, 1000, [
      { targetSlotIdx: 5, startUs: 100, durationUs: 40, waitReason: 30, processorNumber: 2, readyTimeUs: 7 },
      { targetSlotIdx: 5, startUs: 300, durationUs: 10 },
      { targetSlotIdx: 9, startUs: 150, durationUs: 20 },
    ]);

    expect(tick.contextSwitches.get(5)).toHaveLength(2);
    expect(tick.contextSwitches.get(9)).toHaveLength(1);

    const first = tick.contextSwitches.get(5)![0];
    expect(first.startUs).toBe(100);
    expect(first.durationUs).toBe(40);
    expect(first.waitReason).toBe(30);
    expect(first.processorNumber).toBe(2);
    expect(first.readyTimeUs).toBe(7);
  });

  it('does NOT push context-switch records onto the lane span list', () => {
    const tick = buildTick(1, 0, 1000, [{ targetSlotIdx: 3, startUs: 50, durationUs: 10 }]);
    expect(tick.spans.some((s) => (s.kind as number) === KIND_CSWITCH)).toBe(false);
  });
});

describe('aggregateGaugeData — off-CPU gap derivation', () => {
  it('emits the gap between two consecutive on-CPU slices', () => {
    const tick = buildTick(1, 0, 1000, [
      { targetSlotIdx: 5, startUs: 100, durationUs: 50, waitReason: 30, processorNumber: 4, readyTimeUs: 0 },
      { targetSlotIdx: 5, startUs: 300, durationUs: 20, readyTimeUs: 12 },
    ]);
    const store = aggregateGaugeData([tick]).offCpuBySlot.get(5)!;

    expect(store.startUs).toHaveLength(1);
    expect(store.startUs[0]).toBe(150);          // prev.start + prev.duration
    expect(store.endUs[0]).toBe(300);            // next.start
    expect(store.readyTimeUs[0]).toBe(12);       // ready time of the slice that ENDS the gap
    expect(store.waitReason[0]).toBe(30);        // why prev left the CPU
    expect(store.processorNumber[0]).toBe(4);
    expect(store.category[0]).toBe(OffCpuCategory.QuantumEnd);
  });

  it('renders a gap straddling a TickStart as ONE continuous interval (cross-tick)', () => {
    // Slice ends at 150 in tick 1; next slice starts at 300 in tick 2. The gap [150,300] spans the
    // tick-1→tick-2 boundary and must be a single interval, not truncated at the tick edge.
    const tick1 = buildTick(1, 0, 200, [{ targetSlotIdx: 7, startUs: 100, durationUs: 50 }]);
    const tick2 = buildTick(2, 200, 400, [{ targetSlotIdx: 7, startUs: 300, durationUs: 20 }]);
    const store = aggregateGaugeData([tick1, tick2]).offCpuBySlot.get(7)!;

    expect(store.startUs).toHaveLength(1);
    expect(store.startUs[0]).toBe(150);
    expect(store.endUs[0]).toBe(300);
  });

  it('suppresses leading/trailing open-ended gaps — N slices yield N-1 intervals', () => {
    const tick = buildTick(1, 0, 1000, [
      { targetSlotIdx: 2, startUs: 100, durationUs: 10 },
      { targetSlotIdx: 2, startUs: 200, durationUs: 10 },
      { targetSlotIdx: 2, startUs: 400, durationUs: 10 },
    ]);
    const store = aggregateGaugeData([tick]).offCpuBySlot.get(2)!;
    // 3 slices → only the 2 between-pair gaps; no gap before the first slice or after the last.
    expect(store.startUs).toHaveLength(2);
    expect(Array.from(store.startUs)).toEqual([110, 210]);
    expect(Array.from(store.endUs)).toEqual([200, 400]);
  });

  it('skips zero / negative gaps (back-to-back or overlapping slices)', () => {
    const tick = buildTick(1, 0, 1000, [
      { targetSlotIdx: 1, startUs: 100, durationUs: 50 },   // ends 150
      { targetSlotIdx: 1, startUs: 150, durationUs: 50 },   // starts exactly at 150 — zero gap
      { targetSlotIdx: 1, startUs: 400, durationUs: 10 },   // real gap [200,400]
    ]);
    const store = aggregateGaugeData([tick]).offCpuBySlot.get(1)!;
    expect(store.startUs).toHaveLength(1);
    expect(store.startUs[0]).toBe(200);
    expect(store.endUs[0]).toBe(400);
  });

  it('produces no store for a slot with fewer than two slices', () => {
    const tick = buildTick(1, 0, 1000, [{ targetSlotIdx: 8, startUs: 100, durationUs: 10 }]);
    expect(aggregateGaugeData([tick]).offCpuBySlot.has(8)).toBe(false);
  });

  it('a trace with no context-switch events yields an empty offCpuBySlot', () => {
    const tick = buildTick(1, 0, 1000, []);
    expect(aggregateGaugeData([tick]).offCpuBySlot.size).toBe(0);
  });
});

describe('waitReasonToCategory', () => {
  it('gettingIdle forces Idle regardless of wait reason', () => {
    expect(waitReasonToCategory(30, true)).toBe(OffCpuCategory.Idle);
    expect(waitReasonToCategory(0, true)).toBe(OffCpuCategory.Idle);
  });

  it('maps representative reasons to their coarse category', () => {
    expect(waitReasonToCategory(0, false)).toBe(OffCpuCategory.SyncWait);    // Executive
    expect(waitReasonToCategory(27, false)).toBe(OffCpuCategory.SyncWait);   // WrResource
    expect(waitReasonToCategory(32, false)).toBe(OffCpuCategory.Preempted);  // WrPreempted
    expect(waitReasonToCategory(30, false)).toBe(OffCpuCategory.QuantumEnd); // WrQuantumEnd
    expect(waitReasonToCategory(2, false)).toBe(OffCpuCategory.Paging);      // PageIn
    expect(waitReasonToCategory(4, false)).toBe(OffCpuCategory.UserWait);    // DelayExecution
  });

  it('unmapped / out-of-range reasons fall back to Other', () => {
    expect(waitReasonToCategory(22, false)).toBe(OffCpuCategory.Other);   // WrTerminated — unmapped
    expect(waitReasonToCategory(200, false)).toBe(OffCpuCategory.Other);  // out of enum range
  });
});
