import { describe, expect, it } from 'vitest';
import { decodeChunkBinary } from '../chunkDecoder';
import { commandCausalKey, computeRequests, isCommandCausalKey, markersFor } from '@/libs/profiler/correlation/requests';
import { buildTick } from '@/libs/profiler/model/__tests__/spanBuilder';
import { TraceEventKind, type TraceEvent } from '@/libs/profiler/model/types';

// Request correlation (#1517). The bytes are the goldens the engine's CorrelationCodecTests pins its encoders to
// (Encoders_match_the_goldens_the_workbench_decodes), so the hand-written offsets here and the C# codecs cannot drift apart.

function bytes(hex: string): Uint8Array {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
  return out;
}

function decodeOne(hex: string): TraceEvent {
  const events = decodeChunkBinary(bytes(hex), 0, 1, true);
  expect(events).toHaveLength(1);
  return events[0];
}

describe('correlation trace kinds — wire layout', () => {
  it('235 ExternalRequest: a span with its trace context, method, status and route', () => {
    const e = decodeOne('4100EB03E803000000000000EE0200000000000063000000000000000000000000000000011111000000000000222200000000000002C90007002F612F7B69647D');
    expect(e.kind).toBe(TraceEventKind.ExternalRequest);
    expect([e.durationUs, e.traceIdHi, e.traceIdLo]).toEqual([750, '4369', '8738']);
    expect([e.httpMethod, e.httpStatus, e.httpRoute]).toEqual([2, 201, '/a/{id}']);
  });

  it('236 IngressReceived: session, seq, wire index, bytes', () => {
    const e = decodeOne('1600EC0164000000000000000700000003000C002C01');
    expect([e.kind, e.session, e.seq, e.wireIdx, e.messageBytes]).toEqual([TraceEventKind.IngressReceived, 7, 3, 12, 300]);
  });

  it('237 IngressAdmitted: session, seq, wire index, drain tick', () => {
    const e = decodeOne('1800ED01C8000000000000000700000003000C0000093D00');
    expect([e.kind, e.session, e.seq, e.wireIdx, e.corrTick]).toEqual([TraceEventKind.IngressAdmitted, 7, 3, 12, 4_000_000]);
  });

  it('238 IngressDropped: session, seq, reason', () => {
    const e = decodeOne('1300EE012C0100000000000007000000040002');
    expect([e.kind, e.session, e.seq, e.dropReason]).toEqual([TraceEventKind.IngressDropped, 7, 4, 2]);
  });

  it('239 CommandConsumed: session, seq, consuming system', () => {
    const e = decodeOne('1400EF0290010000000000000700000003001500');
    expect([e.kind, e.session, e.seq, e.consumerSystemIndex]).toEqual([TraceEventKind.CommandConsumed, 7, 3, 21]);
  });

  it('240 EgressSent: session, tick, bytes, acks', () => {
    const e = decodeOne('1A00F001F4010000000000000700000000093D00701101000200');
    expect([e.kind, e.session, e.corrTick, e.messageBytes, e.acks]).toEqual([TraceEventKind.EgressSent, 7, 4_000_000, 70_000, 2]);
  });
});

describe('command markers', () => {
  const ev = (kind: TraceEventKind, slot: number, us: number, extra: Partial<TraceEvent>): TraceEvent =>
    ({ kind, threadSlot: slot, tickNumber: 1, timestampUs: us, ...extra }) as TraceEvent;
  const range = { startUs: 0, endUs: 10_000 };

  it('places a command on its transport, drain, consumer and send lanes, in order', () => {
    const tick = buildTick({ tickNumber: 9, startUs: 0, durationUs: 1_000, spans: [] });
    tick.correlation = [
      ev(TraceEventKind.IngressReceived, 5, 100, { session: 2, seq: 4 }),
      ev(TraceEventKind.IngressAdmitted, 0, 200, { session: 2, seq: 4, corrTick: 9 }),
      ev(TraceEventKind.CommandConsumed, 1, 300, { session: 2, seq: 4 }),
      ev(TraceEventKind.EgressSent, 7, 400, { session: 2, corrTick: 9 }),
      ev(TraceEventKind.IngressAdmitted, 0, 450, { session: 2, seq: 4, corrTick: 10 }), // the same seq a tick later: another command
    ];
    const rows = computeRequests([tick], range, []);
    expect(markersFor(rows, commandCausalKey(2, 9, 4))).toEqual([
      { slot: 5, us: 100, kind: 'received' }, { slot: 0, us: 200, kind: 'admitted' }, { slot: 1, us: 300, kind: 'consumed' },
      { slot: 7, us: 400, kind: 'sent' },
    ]);
  });

  it('yields nothing for an HTTP request, whose root is a span', () => {
    expect(isCommandCausalKey('4369:8738')).toBe(false);
    expect(markersFor([], '4369:8738')).toEqual([]);
    expect(isCommandCausalKey(commandCausalKey(1, 1, 1))).toBe(true);
  });
});
