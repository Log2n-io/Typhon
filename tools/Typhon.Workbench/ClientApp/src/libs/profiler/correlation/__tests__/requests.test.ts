import { describe, expect, it } from 'vitest';
import {
  causalKeyOf, commandCausalKey, computeRequests, filterRequests, rootsInView, groupRequests, NO_REQUEST_FILTER, percentile, spansForCausal, type RequestRow,
} from '@/libs/profiler/correlation/requests';
import { buildSpan, buildTick } from '@/libs/profiler/model/__tests__/spanBuilder';
import type { SpanData } from '@/libs/profiler/model/traceModel';
import { TraceEventKind, type TraceEvent } from '@/libs/profiler/model/types';

const range = { startUs: 0, endUs: 10_000 };
const systems = [{ name: 'Movement' }, { name: 'Combat' }];

function caused(key: string, startUs: number, endUs: number, slot = 1): SpanData {
  const [hi, lo] = key.split(':');
  return { ...buildSpan({ startUs, endUs, threadSlot: slot }), traceIdHi: hi, traceIdLo: lo };
}

function instant(kind: TraceEventKind, timestampUs: number, extra: Partial<TraceEvent>): TraceEvent {
  return { kind, threadSlot: 2, tickNumber: 1, timestampUs, ...extra } as TraceEvent;
}

describe('commandCausalKey', () => {
  it('matches the engine derivation: marker | session, drainTick << 16 | seq', () => {
    // CorrelationInstantCodec.CommandCausalId(7, 100, 3) = (0x5459430000000007, 100 << 16 | 3).
    expect(commandCausalKey(7, 100, 3)).toBe(`${0x5459430000000007n}:${(100n << 16n) | 3n}`);
  });

  it('reads the key of a span from its trace context, or null', () => {
    expect(causalKeyOf({ traceIdHi: '1', traceIdLo: '2' })).toBe('1:2');
    expect(causalKeyOf({})).toBeNull();
  });
});

describe('computeRequests', () => {
  it('breaks a command down into ring wait, tick wait, system time and send wait', () => {
    const key = commandCausalKey(7, 40, 3);
    const tick = buildTick({ tickNumber: 40, startUs: 0, durationUs: 1_000, spans: [caused(key, 300, 350), caused(key, 360, 400, 2)] });
    tick.correlation = [
      instant(TraceEventKind.IngressReceived, 100, { session: 7, seq: 3, wireIdx: 2 }),
      instant(TraceEventKind.IngressAdmitted, 200, { session: 7, seq: 3, wireIdx: 2, corrTick: 40 }),
      instant(TraceEventKind.CommandConsumed, 290, { session: 7, seq: 3, consumerSystemIndex: 1 }),
      instant(TraceEventKind.EgressSent, 50, { session: 7 }), // an earlier frame: not this command's
      instant(TraceEventKind.EgressSent, 450, { session: 7 }),
      instant(TraceEventKind.EgressSent, 470, { session: 8 }), // another session's frame
    ];

    const [row] = computeRequests([tick], range, systems);
    expect(row).toMatchObject({
      key, kind: 'command', label: 'command 2 → Combat', session: 7,
      startUs: 100, endUs: 450, totalUs: 350,
      ringWaitUs: 100, tickWaitUs: 90, sendWaitUs: 50,
      spanCount: 2, engineUs: 90,
    });
  });

  it('records an HTTP request with its route, status and the engine work under its trace id', () => {
    const http: SpanData = {
      ...buildSpan({ startUs: 1_000, endUs: 1_800 }), kind: TraceEventKind.ExternalRequest, traceIdHi: '11', traceIdLo: '22',
      rawEvent: { kind: TraceEventKind.ExternalRequest, threadSlot: 0, tickNumber: 1, timestampUs: 1_000, httpMethod: 1, httpStatus: 200, httpRoute: '/swg/inventory' },
    };
    const tick = buildTick({ tickNumber: 1, startUs: 900, durationUs: 2_000, spans: [http, caused('11:22', 1_100, 1_400)] });

    const [row] = computeRequests([tick], range, systems);
    expect(row).toMatchObject({ kind: 'http', label: 'GET /swg/inventory', status: 200, totalUs: 800, spanCount: 1, engineUs: 300 });
  });

  it('lists a dropped command with its reason, and leaves out roots that start outside the range', () => {
    const tick = buildTick({ tickNumber: 1, startUs: 0, durationUs: 20_000, spans: [] });
    tick.correlation = [
      instant(TraceEventKind.IngressDropped, 500, { session: 1, seq: 9, dropReason: 1 }),
      instant(TraceEventKind.IngressDropped, 15_000, { session: 1, seq: 10, dropReason: 2 }),
    ];

    const rows = computeRequests([tick], range, systems);
    expect(rows.map((r) => r.label)).toEqual(['dropped: ring full']);
  });

  it('leaves a breakdown part undefined when its bounding instant is missing', () => {
    const tick = buildTick({ tickNumber: 1, startUs: 0, durationUs: 1_000, spans: [] });
    tick.correlation = [instant(TraceEventKind.IngressAdmitted, 200, { session: 7, seq: 3, wireIdx: 2, corrTick: 1 })];

    const [row] = computeRequests([tick], range, systems);
    expect(row.ringWaitUs).toBeUndefined();
    expect(row.tickWaitUs).toBeUndefined();
    expect(row.sendWaitUs).toBeUndefined();
    expect(row.label).toBe('command 2');
  });
});

describe('computeRequests — pairing', () => {
  it('answers a command with the first frame for its tick or later, not one still going out for an earlier tick', () => {
    const tick = buildTick({ tickNumber: 40, startUs: 0, durationUs: 1_000, spans: [] });
    tick.correlation = [
      instant(TraceEventKind.IngressAdmitted, 200, { session: 7, seq: 3, corrTick: 40 }),
      instant(TraceEventKind.CommandConsumed, 300, { session: 7, seq: 3 }),
      instant(TraceEventKind.EgressSent, 320, { session: 7, corrTick: 39 }), // the frame of tick 39, still being sent
      instant(TraceEventKind.EgressSent, 500, { session: 7, corrTick: 40 }),
    ];
    const [row] = computeRequests([tick], range, systems);
    expect([row.endUs, row.sendWaitUs]).toEqual([500, 200]);
  });

  it('keeps commands of two sessions apart when they reuse a seq', () => {
    const tick = buildTick({ tickNumber: 5, startUs: 0, durationUs: 1_000, spans: [] });
    tick.correlation = [
      instant(TraceEventKind.IngressReceived, 100, { session: 1, seq: 9 }),
      instant(TraceEventKind.IngressReceived, 150, { session: 2, seq: 9 }),
      instant(TraceEventKind.IngressAdmitted, 200, { session: 2, seq: 9, corrTick: 5 }),
      instant(TraceEventKind.IngressAdmitted, 210, { session: 1, seq: 9, corrTick: 5 }),
    ];
    const rows = computeRequests([tick], range, systems);
    expect(rows.map((r) => [r.session, r.ringWaitUs])).toEqual([[1, 110], [2, 50]]);
  });

  it('gives each HTTP request its own row id even when an upstream trace makes them share a causal key', () => {
    const http = (startUs: number, spanId: string): SpanData => ({
      ...buildSpan({ startUs, endUs: startUs + 100 }), spanId, kind: TraceEventKind.ExternalRequest, traceIdHi: '1', traceIdLo: '2',
    });
    const tick = buildTick({ tickNumber: 1, startUs: 0, durationUs: 1_000, spans: [http(10, '5'), http(300, '6')] });
    const rows = computeRequests([tick], range, systems);
    expect(rows.map((r) => r.key)).toEqual(['1:2', '1:2']);
    expect(new Set(rows.map((r) => r.id)).size).toBe(2);
  });

  it('bounds a command by the admissions of the same (session, seq) around it, which recurs once the u16 wraps', () => {
    const tick = buildTick({ tickNumber: 5, startUs: 0, durationUs: 10_000, spans: [] });
    tick.correlation = [
      instant(TraceEventKind.IngressReceived, 100, { session: 1, seq: 9 }),
      instant(TraceEventKind.IngressAdmitted, 200, { session: 1, seq: 9, corrTick: 5 }), // never consumed
      instant(TraceEventKind.IngressAdmitted, 5_000, { session: 1, seq: 9, corrTick: 6 }), // the same seq, wrapped
      instant(TraceEventKind.CommandConsumed, 5_100, { session: 1, seq: 9 }),
    ];
    const [first, second] = computeRequests([tick], range, systems);
    expect([first.tickWaitUs, first.endUs]).toEqual([undefined, 200]);
    expect([second.ringWaitUs, second.tickWaitUs]).toEqual([undefined, 100]); // the reception before the first admission is not its own
  });

  it('accepts a frame with no tick, from an engine that did not record one', () => {
    const tick = buildTick({ tickNumber: 5, startUs: 0, durationUs: 1_000, spans: [] });
    tick.correlation = [
      instant(TraceEventKind.IngressAdmitted, 200, { session: 3, seq: 1, corrTick: 5 }),
      instant(TraceEventKind.CommandConsumed, 300, { session: 3, seq: 1 }),
      instant(TraceEventKind.EgressSent, 350, { session: 3 }),
    ];
    expect(computeRequests([tick], range, systems)[0].endUs).toBe(350);
  });

  it('returns nothing for ticks with no correlation', () => {
    const tick = buildTick({ tickNumber: 1, startUs: 0, durationUs: 1_000, spans: [caused('1:2', 10, 20)] });
    expect(computeRequests([tick], range, systems)).toEqual([]);
  });
});

describe('groupRequests', () => {
  it('groups by label with nearest-rank percentiles', () => {
    const rows = [10, 20, 30, 40, 1_000].map((totalUs, i) => ({
      id: `${i}`, key: `${i}`, kind: 'http' as const, label: 'GET /a', startUs: 0, endUs: totalUs, totalUs, spanCount: 0, engineUs: 0, markers: [],
    }));
    const [g] = groupRequests(rows);
    expect(g).toMatchObject({ label: 'GET /a', count: 5, p50Us: 30, p99Us: 1_000, maxUs: 1_000, totalUs: 1_100 });
    expect(percentile([], 0.5)).toBe(0);
  });
});

describe('spansForCausal', () => {
  it('returns exactly the spans carrying the key, across ticks, in start order', () => {
    const key = commandCausalKey(1, 2, 3);
    const a = caused(key, 500, 600);
    const b = caused(key, 100, 200);
    const other = caused(commandCausalKey(1, 2, 4), 150, 160);
    const ticks = [buildTick({ tickNumber: 1, startUs: 0, durationUs: 400, spans: [b, other] }), buildTick({ tickNumber: 2, startUs: 400, durationUs: 400, spans: [a] })];
    expect(spansForCausal(ticks, key)).toEqual([b, a]);
  });
});

describe('filterRequests', () => {
  const row = (label: string, session: number | undefined, totalUs: number): RequestRow => ({
    id: label, key: label, kind: 'command', label, session, startUs: 0, endUs: totalUs, totalUs, spanCount: 0, engineUs: 0, markers: [],
  });
  const rows = [row('command 3 → Combat', 1, 500), row('command 24 → PlayerSessions', 2, 30_000), row('command 24 → PlayerSessions', 1, 900)];
  const keep = (f: Partial<typeof NO_REQUEST_FILTER>) => filterRequests(rows, { ...NO_REQUEST_FILTER, ...f }).map((r) => `${r.label}@${r.session}`);

  it('is the same array when off', () => {
    expect(filterRequests(rows, NO_REQUEST_FILTER)).toBe(rows);
    expect(filterRequests(rows, { ...NO_REQUEST_FILTER, text: '  ' })).toBe(rows);
  });

  it('matches the label case-insensitively, one session, a floor on the total, all together', () => {
    expect(keep({ text: 'playersessions' })).toEqual(['command 24 → PlayerSessions@2', 'command 24 → PlayerSessions@1']);
    expect(keep({ session: 1 })).toEqual(['command 3 → Combat@1', 'command 24 → PlayerSessions@1']);
    expect(keep({ minTotalUs: 900 })).toEqual(['command 24 → PlayerSessions@2', 'command 24 → PlayerSessions@1']);
    expect(keep({ text: 'Player', session: 1, minTotalUs: 901 })).toEqual([]);
    expect(keep({ text: 'Player', session: 1, minTotalUs: 900 })).toEqual(['command 24 → PlayerSessions@1']);
  });
});

describe('rootsInView', () => {
  const row = (key: string, startUs: number, endUs: number): RequestRow => ({
    id: key, key, kind: 'command', label: key, startUs, endUs, totalUs: endUs - startUs, spanCount: 0, engineUs: 0, markers: [],
  });
  const rows = [row('before', 0, 90), row('across', 50, 150), row('inside', 110, 120), row('after', 200, 260), row('ending-at-start', 40, 100)];

  it('keeps what overlaps the view, including what started before it', () => {
    expect(rootsInView(rows, { startUs: 100, endUs: 200 }).map((r) => r.key)).toEqual(['across', 'inside', 'ending-at-start']);
  });

  it('is empty for an empty view', () => {
    expect(rootsInView(rows, { startUs: 100, endUs: 100 })).toEqual([]);
  });
});
