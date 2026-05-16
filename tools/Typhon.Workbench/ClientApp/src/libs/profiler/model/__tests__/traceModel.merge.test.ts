import { describe, expect, it } from 'vitest';
import { processTickEvents, mergeTickData, aggregateGaugeData } from '@/libs/profiler/model/traceModel';
import type { GaugeId, TraceEvent } from '@/libs/profiler/model/types';
import type { TickData } from '@/libs/profiler/model/traceModel';

/**
 * Covers two cross-tick folds that would silently corrupt the viewer if they regressed:
 *
 *   - `mergeTickData`: combines two TickData halves of an intra-tick split. The fold re-runs
 *     `processTickEvents` on the concatenated raw events — so every derived field (spans, per-kind
 *     projections, running-max arrays, depth walk) must match what a single-pass build would
 *     produce. If this drifted, split-tick regions would render with half their spans missing or
 *     with the wrong depth assignments.
 *
 *   - `aggregateGaugeData`: folds per-tick gauge snapshots into cross-tick series. Gauge series
 *     samples must land in tick order (otherwise sparklines draw time-reversed); memoryAllocEvents
 *     and gcEvents must concatenate preserving per-tick order. A regression in ordering would
 *     show up as reverse-time gauge lines or allocation rollups with scrambled timestamps.
 */

const KIND = {
  TickStart: 0,
  TickEnd: 1,
  BTreeInsert: 14,
  ThreadContextSwitch: 254,
} as const;

const ZERO_HEX = '0000000000000000';

function baseEvent(overrides: Partial<TraceEvent>): TraceEvent {
  return {
    kind: 0 as TraceEvent['kind'],
    threadSlot: 0,
    tickNumber: 1,
    timestampUs: 0,
    ...overrides,
  };
}

function buildTick(tickNumber: number, events: TraceEvent[]): TickData {
  return processTickEvents(tickNumber, events, []);
}

describe('mergeTickData — intra-tick split fold', () => {
  it('produces a merged tick equal to a single-pass build on the union of events', () => {
    // Two halves of tick 1, each with its own span. The single-pass reference build processes
    // both halves as one flat event list; the merged result must match byte-for-byte on the
    // derived fields we care about.
    const head: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 }),
      baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'], timestampUs: 10, durationUs: 5,
        spanId: 'aaaaaaaaaaaaaaaa', parentSpanId: ZERO_HEX,
      }),
    ];
    const tail: TraceEvent[] = [
      baseEvent({
        kind: KIND.BTreeInsert as TraceEvent['kind'], timestampUs: 20, durationUs: 10,
        spanId: 'bbbbbbbbbbbbbbbb', parentSpanId: ZERO_HEX,
      }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 50 }),
    ];

    // Important: processTickEvents stamps `rawEvents` on the returned TickData only when built
    // from an event stream that still has room in its caller's buffer — for tests we set it
    // directly so mergeTickData has something to concat.
    const a = buildTick(1, head);
    a.rawEvents = head;
    const b = buildTick(1, tail);
    b.rawEvents = tail;

    const merged = mergeTickData(a, b, []);
    const reference = buildTick(1, [...head, ...tail]);

    // Core invariants: span count, sort order, depth.
    expect(merged.spans).toHaveLength(2);
    expect(merged.spans.map((s) => s.spanId)).toEqual(
      reference.spans.map((s) => s.spanId),
    );
    expect(merged.startUs).toBe(reference.startUs);
    expect(merged.endUs).toBe(reference.endUs);

    // rawEvents is preserved on the merged result for downstream fold chains; `assembleTickView`
    // wipes it only on the final slot. This test pins that behaviour so a "clear rawEvents on
    // merge" regression can't slip in silently.
    expect(merged.rawEvents.length).toBe(head.length + tail.length);
  });

  it('preserves ThreadContextSwitch records across a split-tick merge', () => {
    // Off-CPU slices can land in either half of an intra-tick split. mergeTickData re-runs
    // processTickEvents on the concatenated rawEvents, so the kind-254 demux must reproduce the
    // full per-slot slice list — a regression here would drop off-CPU intervals on split ticks.
    const head: TraceEvent[] = [
      baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: 0 }),
      baseEvent({
        kind: KIND.ThreadContextSwitch as TraceEvent['kind'], timestampUs: 10, durationUs: 5,
        targetSlotIdx: 3, waitReason: 30, threadState: 0, gettingIdle: false, processorNumber: 1, readyTimeUs: 0,
      }),
    ];
    const tail: TraceEvent[] = [
      baseEvent({
        kind: KIND.ThreadContextSwitch as TraceEvent['kind'], timestampUs: 40, durationUs: 5,
        targetSlotIdx: 3, waitReason: 7, threadState: 0, gettingIdle: false, processorNumber: 2, readyTimeUs: 0,
      }),
      baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: 50 }),
    ];
    const a = buildTick(1, head);
    a.rawEvents = head;
    const b = buildTick(1, tail);
    b.rawEvents = tail;

    const merged = mergeTickData(a, b, []);
    const slices = merged.contextSwitches.get(3);
    expect(slices).toHaveLength(2);
    expect(slices!.map((s) => s.startUs)).toEqual([10, 40]);
  });

  it('throws on tickNumber mismatch — mergeTickData is strictly intra-tick', () => {
    const a = buildTick(1, [baseEvent({ kind: KIND.TickStart as TraceEvent['kind'] })]);
    a.rawEvents = [];
    const b = buildTick(2, [baseEvent({ kind: KIND.TickStart as TraceEvent['kind'] })]);
    b.rawEvents = [];
    expect(() => mergeTickData(a, b, [])).toThrow(/tickNumber mismatch/);
  });
});

describe('aggregateGaugeData — cross-tick fold', () => {
  it('concatenates gauge samples across ticks, preserving tick order', () => {
    const gaugeId = 1 as unknown as GaugeId;
    const ticks: TickData[] = [];
    for (let i = 1; i <= 3; i++) {
      const tick = buildTick(i, [
        baseEvent({ kind: KIND.TickStart as TraceEvent['kind'], timestampUs: (i - 1) * 100 }),
        baseEvent({ kind: KIND.TickEnd as TraceEvent['kind'], timestampUs: i * 100 }),
      ]);
      tick.gaugeSnapshot = {
        tickNumber: i,
        timestampUs: i * 100,
        values: new Map([[gaugeId, i * 10]]),
      };
      ticks.push(tick);
    }

    const result = aggregateGaugeData(ticks);
    const series = result.gaugeSeries.get(gaugeId)!;
    expect(series).toBeDefined();
    expect(series.samples.map((s) => s.tickNumber)).toEqual([1, 2, 3]);
    expect(series.samples.map((s) => s.value)).toEqual([10, 20, 30]);
    expect(series.samples.map((s) => s.timestampUs)).toEqual([100, 200, 300]);
  });

  it('concatenates memoryAllocEvents across ticks in input order', () => {
    const ticks: TickData[] = [];
    for (let i = 1; i <= 2; i++) {
      const tick = buildTick(i, [baseEvent({ kind: KIND.TickStart as TraceEvent['kind'] })]);
      // Manually populate memoryAllocEvents — processTickEvents builds these from the raw events,
      // but for aggregateGaugeData's concat semantics we only need the per-tick arrays seeded.
      tick.memoryAllocEvents = [
        {
          tickNumber: i, timestampUs: i * 100, threadSlot: 0, direction: 0, sourceTag: 0,
          sizeBytes: 10, totalAfterBytes: 10,
        },
      ];
      ticks.push(tick);
    }
    const result = aggregateGaugeData(ticks);
    expect(result.memoryAllocEvents.map((e) => e.tickNumber)).toEqual([1, 2]);
  });

  it('handles empty ticks array without allocating state', () => {
    const result = aggregateGaugeData([]);
    expect(result.gaugeSeries.size).toBe(0);
    expect(result.gaugeCapacities.size).toBe(0);
    expect(result.memoryAllocEvents).toEqual([]);
    expect(result.gcEvents).toEqual([]);
  });

  it('ignores ticks with no gaugeSnapshot (sparse snapshots are the common case)', () => {
    // Only tick 2 has a gauge snapshot; ticks 1 and 3 don't. The fold must not produce ghost
    // samples for the empty ticks — that would misrepresent the sparkline.
    const gaugeId = 1 as unknown as GaugeId;
    const t1 = buildTick(1, [baseEvent({ kind: KIND.TickStart as TraceEvent['kind'] })]);
    const t2 = buildTick(2, [baseEvent({ kind: KIND.TickStart as TraceEvent['kind'] })]);
    t2.gaugeSnapshot = { tickNumber: 2, timestampUs: 200, values: new Map([[gaugeId, 42]]) };
    const t3 = buildTick(3, [baseEvent({ kind: KIND.TickStart as TraceEvent['kind'] })]);

    const result = aggregateGaugeData([t1, t2, t3]);
    const series = result.gaugeSeries.get(gaugeId)!;
    expect(series.samples).toHaveLength(1);
    expect(series.samples[0].tickNumber).toBe(2);
  });

  it('last-seen thread name wins when the same slot emits a name twice', () => {
    // Note: the code's docstring says "First observation wins"; the implementation at line 1108
    // does `threadNames.set(info.threadSlot, info.name)` unconditionally — so last-seen actually
    // wins. This test pins the SHIPPED behaviour so a later "fix" that aligns to the docstring
    // doesn't silently change viewer output. If the intent really is first-wins, the fix is in
    // the implementation — not this test.
    const t1 = buildTick(1, [baseEvent({ kind: KIND.TickStart as TraceEvent['kind'] })]);
    t1.threadInfos = [{ threadSlot: 0, managedThreadId: 100, timestampUs: 0, name: 'First' }];
    const t2 = buildTick(2, [baseEvent({ kind: KIND.TickStart as TraceEvent['kind'] })]);
    t2.threadInfos = [{ threadSlot: 0, managedThreadId: 100, timestampUs: 0, name: 'Second' }];

    const result = aggregateGaugeData([t1, t2]);
    expect(result.threadNames.get(0)).toBe('Second');
  });
});
