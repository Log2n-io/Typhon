import { describe, expect, it } from 'vitest';
import type { SubscriptionsServerTelemetry, SubscriptionsSessionTelemetry, TickData } from '@/libs/profiler/model/traceModel';
import { SESSION_REALM_UNKNOWN } from '@/libs/profiler/model/traceModel';
import {
  differentiate,
  latestServerSample,
  latestSessionRows,
  poolOccupancy,
  readSessionCoverage,
  sessionRealmLabel,
} from '../subscriptionReadings';

function server(over: Partial<SubscriptionsServerTelemetry> = {}): SubscriptionsServerTelemetry {
  return {
    sessions: 0,
    netOutBytesPerSec: 0,
    trackP99Ms: 0,
    durabilityWaitP99Ms: 0,
    framesSkipped: 0,
    framePoolRented: 0,
    framePoolBlocks: 0,
    framePoolBudgetSkips: 0,
    reportedSessions: 0,
    ...over,
  };
}

function session(id: number, over: Partial<SubscriptionsSessionTelemetry> = {}): SubscriptionsSessionTelemetry {
  return { sessionId: id, realmId: 0, bytesPerSec: 0, framesSkipped: 0, degradeLevel: 0, ...over };
}

/** A tick that may or may not carry an emission. `startUs` is real so the panel's tick-period derivation works. */
function tick(n: number, row?: SubscriptionsServerTelemetry, rows?: SubscriptionsSessionTelemetry[]): TickData {
  return {
    tickNumber: n,
    startUs: n * 20_000,
    endUs: n * 20_000 + 19_000,
    subscriptions: row,
    subscriptionSessions: rows === undefined ? undefined : new Map(rows.map((r) => [r.sessionId, r])),
  } as unknown as TickData;
}

describe('latestServerSample', () => {
  it('finds the newest tick that carried a record, not the newest tick', () => {
    // The emission is ~1 Hz, so almost every tick in a window is silent. Falling back to the newest tick would render
    // absence as zeros — the failure this exists to prevent.
    const ticks = [tick(10, server({ sessions: 3 })), tick(11), tick(12)];
    expect(latestServerSample(ticks)?.tickNumber).toBe(10);
    expect(latestServerSample(ticks)?.row.sessions).toBe(3);
  });

  it('returns null when the window carries no record at all', () => {
    expect(latestServerSample([tick(1), tick(2)])).toBeNull();
  });
});

describe('latestSessionRows', () => {
  it('sorts busiest first, so a capped list keeps the sessions worth seeing', () => {
    const rows = [session(1, { bytesPerSec: 10 }), session(2, { bytesPerSec: 900 }), session(3, { bytesPerSec: 50 })];
    expect(latestSessionRows([tick(5, server(), rows)]).map((r) => r.sessionId)).toEqual([2, 3, 1]);
  });

  it('takes rows from the tick that carried the SERVER record, not from a newer tick with rows', () => {
    // Pairing rows from one emission with a population count from another would let the panel state a difference that
    // never existed. The server record's tick is the anchor.
    const ticks = [
      tick(5, server({ sessions: 2, reportedSessions: 2 }), [session(1), session(2)]),
      tick(6, undefined, [session(9)]),
    ];
    expect(latestSessionRows(ticks).map((r) => r.sessionId)).toEqual([1, 2]);
  });

  it('is empty when the record arrived with no rows (the session gate is off)', () => {
    expect(latestSessionRows([tick(5, server({ sessions: 4 }))])).toEqual([]);
  });
});

describe('readSessionCoverage', () => {
  it('reports the hidden count when the emission was capped', () => {
    const rows = Array.from({ length: 64 }, (_, i) => session(i));
    const c = readSessionCoverage(server({ sessions: 2048, reportedSessions: 64 }), rows);
    expect(c).toEqual({ open: 2048, reported: 64, hidden: 1984, capped: true });
  });

  it('is not capped when every open session sent a row', () => {
    const c = readSessionCoverage(server({ sessions: 2, reportedSessions: 2 }), [session(1), session(2)]);
    expect(c).toEqual({ open: 2, reported: 2, hidden: 0, capped: false });
  });

  it('trusts the rows on screen over a record claiming more than it delivered', () => {
    // A truncated chunk or a mid-emission reconnect can leave `reportedSessions` above the rows that actually arrived.
    // The panel must not claim rows it does not have.
    const c = readSessionCoverage(server({ sessions: 10, reportedSessions: 8 }), [session(1), session(2)]);
    expect(c.reported).toBe(2);
    expect(c.hidden).toBe(8);
    expect(c.capped).toBe(true);
  });
});

describe('differentiate', () => {
  const tickSeconds = 0.02; // 50 Hz

  it('turns a cumulative counter into a per-second rate over the window', () => {
    const ticks = [tick(0, server({ framesSkipped: 100 })), tick(50, server({ framesSkipped: 400 }))];
    const r = differentiate(ticks, (x) => x.framesSkipped, tickSeconds);
    // 300 skips over 50 ticks at 20 ms = 1 s.
    expect(r?.total).toBe(400);
    expect(r?.perSecond).toBeCloseTo(300, 6);
  });

  it('reports null rather than zero with only one emission in the window', () => {
    // Zero would assert "no skips are happening", which one sample cannot support. The distinction is the point.
    const r = differentiate([tick(7, server({ framesSkipped: 12 }))], (x) => x.framesSkipped, tickSeconds);
    expect(r?.total).toBe(12);
    expect(r?.perSecond).toBeNull();
  });

  // The commonest cause is not a restart: framesSkipped is a SUM over per-session counters, so a session disconnecting takes its whole
  // contribution out of the total and the sum falls with nothing wrong. A negative rate is not a reading about anything either way.
  it('clamps a counter that went backwards, which a session disconnecting is enough to cause', () => {
    const ticks = [tick(0, server({ framesSkipped: 900 })), tick(50, server({ framesSkipped: 4 }))];
    expect(differentiate(ticks, (x) => x.framesSkipped, tickSeconds)?.perSecond).toBe(0);
  });

  // The limitation the clamp hides, pinned so it is a known property rather than a surprise: in a window where a session left, the skipping
  // done by the sessions that stayed is subtracted away with it. 0 here means "no NET growth across a changing population", not "no skips".
  it('reads zero when a departing session\'s counter outweighs the skips of the sessions that stayed', () => {
    // 700 leaves with one session; the remaining sessions skipped 60 more in the same window. Net is negative, so the real 60 is invisible.
    const ticks = [tick(0, server({ framesSkipped: 900, sessions: 4 })), tick(50, server({ framesSkipped: 260, sessions: 3 }))];
    const r = differentiate(ticks, (x) => x.framesSkipped, tickSeconds);
    expect(r?.perSecond).toBe(0);
    expect(r?.total).toBe(260);
  });

  it('returns null when nothing in the window carried a record', () => {
    expect(differentiate([tick(1), tick(2)], (x) => x.framesSkipped, tickSeconds)).toBeNull();
  });
});

describe('poolOccupancy', () => {
  it('is a fraction of allocated blocks', () => {
    expect(poolOccupancy(server({ framePoolRented: 32, framePoolBlocks: 64 }))).toBe(0.5);
  });

  it('is null before the pool has allocated anything, rather than 0 %', () => {
    // 0/0 is not "empty" — it is "nothing to be full of", and a gauge reading 0 % would imply headroom that has not
    // been established.
    expect(poolOccupancy(server({ framePoolRented: 0, framePoolBlocks: 0 }))).toBeNull();
  });
});

describe('sessionRealmLabel', () => {
  it('names the realm', () => {
    expect(sessionRealmLabel(session(1, { realmId: 3 }))).toBe('realm 3');
  });

  it('returns null for the not-yet-told sentinel, which is neither realm 65535 nor realm 0', () => {
    expect(sessionRealmLabel(session(1, { realmId: SESSION_REALM_UNKNOWN }))).toBeNull();
    expect(sessionRealmLabel(session(1, { realmId: 0 }))).toBe('realm 0');
  });
});
