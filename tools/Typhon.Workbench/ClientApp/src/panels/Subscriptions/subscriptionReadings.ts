import type { SubscriptionsServerTelemetry, SubscriptionsSessionTelemetry, TickData } from '@/libs/profiler/model/traceModel';
import { SESSION_REALM_UNKNOWN } from '@/libs/profiler/model/traceModel';

/**
 * The derived readings the Subscriptions panel shows (#WB-02), as pure functions over decoded ticks.
 *
 * Out of the component for the same reason the Spatial panel's are: each encodes a claim about the engine that is easy to get subtly
 * wrong — a cumulative counter rendered as a rate, a sampled row list presented as a population, a sentinel realm read as realm 65535 —
 * and a claim worth testing is a claim worth writing where a test can reach it.
 */

/** A server record with the tick it arrived on, because a per-tick figure without its tick is not a reading. */
export interface ServerSample {
  tickNumber: number;
  row: SubscriptionsServerTelemetry;
}

/**
 * The most recent tick carrying a kind-68 record, or null.
 *
 * <b>Not "the last tick".</b> The emission is once per stats period — about 1 Hz — so nearly every tick in a window is silent while
 * replication is perfectly healthy. Falling back to the newest tick regardless would render absence as zeros.
 */
export function latestServerSample(ticks: readonly TickData[]): ServerSample | null {
  for (let i = ticks.length - 1; i >= 0; i--) {
    const row = ticks[i].subscriptions;
    if (row !== undefined) {
      return { tickNumber: ticks[i].tickNumber, row };
    }
  }
  return null;
}

/** The session rows from the same tick as {@link latestServerSample}, sorted by bytes/s descending. */
export function latestSessionRows(ticks: readonly TickData[]): SubscriptionsSessionTelemetry[] {
  for (let i = ticks.length - 1; i >= 0; i--) {
    if (ticks[i].subscriptions === undefined) {
      continue;
    }
    // Deliberately keyed to the tick that carried the SERVER record, not to the newest tick with any row: pairing rows from one emission
    // with a population count from another would let the panel state a difference that never existed.
    const rows = ticks[i].subscriptionSessions;
    return rows === undefined ? [] : [...rows.values()].sort((a, b) => b.bytesPerSec - a.bytesPerSec);
  }
  return [];
}

/** What the panel must say about how complete its session list is. */
export interface SessionCoverage {
  /** Sessions the server reported open. */
  open: number;
  /** Rows that accompanied the record. */
  reported: number;
  /** Open minus reported — sessions the emission capped away. Never negative. */
  hidden: number;
  /** True when the list is a sample rather than the population, which the panel must state rather than imply. */
  capped: boolean;
}

/**
 * Coverage of the session list.
 *
 * The engine caps rows at 64 because volume scales with the session count, so at any real scale this list IS a sample. A panel that
 * renders the rows without saying so is lying by omission — the same contract the spatial panel's realm census carries.
 */
export function readSessionCoverage(row: SubscriptionsServerTelemetry, rows: readonly SubscriptionsSessionTelemetry[]): SessionCoverage {
  const open = Math.max(0, row.sessions);
  // Trust the row count over the record's own field where they disagree: the rows are what is on screen, and a record whose
  // `reportedSessions` outran its rows (a truncated chunk, a mid-emission reconnect) must not make the panel claim rows it lacks.
  const reported = Math.min(rows.length, Math.max(0, row.reportedSessions));
  return { open, reported, hidden: Math.max(0, open - reported), capped: open > reported };
}

/** A cumulative counter differentiated over the window, so the panel shows a rate rather than a total that only ever climbs. */
export interface CounterRate {
  /** The newest cumulative value. */
  total: number;
  /** Per second over the window, or null when the window holds fewer than two emissions to difference. */
  perSecond: number | null;
}

/**
 * Differentiate one cumulative field of the server record over the ticks in the window.
 *
 * <b>Why not show the total.</b> `framesSkipped` and `framePoolBudgetSkips` count since each session opened, so on any long-lived
 * server they are large numbers that move slowly — which reads as "lots of skips" when the truth is "none recently". The slope is the
 * diagnostic; the total is context.
 *
 * Returns `perSecond: null` rather than 0 for a window with one emission: zero would assert "no skips are happening", which is a claim
 * a single sample cannot support.
 */
export function differentiate(
  ticks: readonly TickData[],
  pick: (row: SubscriptionsServerTelemetry) => number,
  tickSeconds: number,
): CounterRate | null {
  let newestIdx = -1;
  let oldestIdx = -1;
  for (let i = ticks.length - 1; i >= 0; i--) {
    if (ticks[i].subscriptions === undefined) {
      continue;
    }
    if (newestIdx < 0) {
      newestIdx = i;
    }
    oldestIdx = i;
  }
  if (newestIdx < 0) {
    return null;
  }

  const newest = ticks[newestIdx].subscriptions!;
  const total = pick(newest);
  if (oldestIdx === newestIdx) {
    return { total, perSecond: null };
  }

  const oldest = ticks[oldestIdx].subscriptions!;
  const spanTicks = ticks[newestIdx].tickNumber - ticks[oldestIdx].tickNumber;
  const seconds = spanTicks * tickSeconds;
  if (!(seconds > 0)) {
    return { total, perSecond: null };
  }
  // Clamped at zero, and the reason is ordinary rather than exceptional. `framesSkipped` on the server record is the SUM of per-session
  // counters that each count since their own session opened, over a population that changes — so a session disconnecting removes its whole
  // contribution and the total drops, with no restart and nothing wrong. (A restart does it too; it is the rarer cause.)
  //
  // The consequence to know, because the clamp hides it: in a window where a session left, skips by the sessions that stayed are subtracted
  // away with it, so this reads 0 while skipping was happening. Reading 0 here means "no NET growth in a changing population", not "no skips".
  // The fix belongs on the wire — a server-wide counter that does not leave with its session — not in this arithmetic, which cannot recover
  // information the record does not carry.
  return { total, perSecond: Math.max(0, total - pick(oldest)) / seconds };
}

/** Frame-pool occupancy as a fraction, or null when the pool has allocated nothing yet. */
export function poolOccupancy(row: SubscriptionsServerTelemetry): number | null {
  return row.framePoolBlocks > 0 ? row.framePoolRented / row.framePoolBlocks : null;
}

/** How a session's realm should be labelled. `null` means the session has not been told its realm yet. */
export function sessionRealmLabel(row: SubscriptionsSessionTelemetry): string | null {
  return row.realmId === SESSION_REALM_UNKNOWN ? null : `realm ${row.realmId}`;
}
