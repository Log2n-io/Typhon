import { describe, expect, it } from 'vitest';
import { Clock, PingScheduler } from '../src/index.js';
import { bits, fromBits, goldenJson } from './golden-support.js';

/*
 * clock-trace: the cross-language pin of the clock and the round-trip estimator (#1398, 05 § 1). The .NET SDK generated
 * it by driving its Clock and RoundTripEstimator through a committed arrival trace; this side replays the same events
 * and must report the same bits after every step — the three clocks are ports of one another, and this is what keeps
 * them so.
 */

interface UpdateExpect {
  readonly renderTick: number;
  readonly renderFrac: string;
  readonly offsetMs: string;
  readonly renderDelayMs: string;
  readonly tickPeriodMs: string;
  readonly latestTick: number;
}

type ClockEvent =
  | { readonly op: 'frame'; readonly tick: number; readonly recvMs: string }
  | { readonly op: 'period'; readonly tick: number; readonly periodMs: string }
  | { readonly op: 'reset' }
  | { readonly op: 'update'; readonly nowMs: string; readonly expect: UpdateExpect }
  | { readonly op: 'phase'; readonly atMs: string; readonly oneWayDelayMs: string; readonly value: string }
  | {
      readonly op: 'pong';
      readonly clientMs: number;
      readonly recvMs: string;
      readonly expect: { readonly rttMs: string; readonly lastRttMs: string };
    };

interface ClockTraceVector {
  readonly options: { readonly tickPeriodMs: string };
  readonly events: ClockEvent[];
}

const vector = goldenJson('clock-trace') as ClockTraceVector;

describe('golden clock-trace', () => {
  it('replays every event to the same clock and round trip', () => {
    const clock = new Clock({ tickPeriodMs: fromBits(vector.options.tickPeriodMs) });
    const ping = new PingScheduler({ pingHz: 4, send: () => {}, lastAppliedTick: () => 0 });
    let updates = 0;
    vector.events.forEach((e, i) => {
      const at = `event ${i} (${e.op})`;
      switch (e.op) {
        case 'frame':
          clock.onFrame(e.tick, fromBits(e.recvMs));
          break;
        case 'period':
          clock.onPeriodChange(e.tick, fromBits(e.periodMs));
          break;
        case 'reset':
          clock.reset();
          break;
        case 'update': {
          clock.update(fromBits(e.nowMs));
          updates++;
          expect(
            {
              renderTick: clock.renderTick,
              renderFrac: bits(clock.renderFrac),
              offsetMs: bits(clock.offsetMs),
              renderDelayMs: bits(clock.renderDelayMs),
              tickPeriodMs: bits(clock.tickPeriodMs),
              latestTick: clock.latestTick,
            },
            at,
          ).toEqual(e.expect);
          break;
        }
        case 'phase':
          expect(bits(clock.subTickPhase(fromBits(e.atMs), fromBits(e.oneWayDelayMs))), at).toBe(e.value);
          break;
        case 'pong':
          ping.onPong({ clientMs: e.clientMs, tick: 0, usIntoTick: 0 }, fromBits(e.recvMs));
          expect({ rttMs: bits(ping.rttMs), lastRttMs: bits(ping.lastRttMs) }, at).toEqual(e.expect);
          break;
      }
    });

    expect(updates).toBeGreaterThan(100);
  });
});
