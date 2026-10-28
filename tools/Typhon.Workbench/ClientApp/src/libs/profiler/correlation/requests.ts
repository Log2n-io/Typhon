import type { SpanData, TickData } from '@/libs/profiler/model/traceModel';
import { HTTP_METHOD_NAMES, TraceEventKind, type TraceEvent } from '@/libs/profiler/model/types';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';

/**
 * Request correlation (#1517): the roots of a capture — HTTP requests and client commands — and the engine work each one caused, read from the
 * loaded ticks. Spans carry their cause as a 128-bit causal id in their trace context (decimal hi/lo strings on the client); a request's id is its W3C
 * trace id, a command's is derived from (session, drain tick, seq) the same way the engine derives it (`CorrelationInstantCodec.CommandCausalId`).
 */

/** The marker in the high half of a command's causal id: "TYC". */
const COMMAND_MARKER = 0x5459_4300_0000_0000n;

/** The causal key of a span: its trace id halves joined, or null when it carries none. */
export function causalKeyOf(span: { traceIdHi?: string; traceIdLo?: string }): string | null {
  return span.traceIdHi !== undefined && span.traceIdLo !== undefined ? `${span.traceIdHi}:${span.traceIdLo}` : null;
}

/** The causal key of a client command, as the engine derives it. */
export function commandCausalKey(session: number, drainTick: number, seq: number): string {
  const hi = COMMAND_MARKER | BigInt(session >>> 0);
  const lo = (BigInt(drainTick >>> 0) << 16n) | BigInt(seq & 0xffff);
  return `${hi.toString()}:${lo.toString()}`;
}

/** True when the key is a client command's rather than a W3C trace id. */
export function isCommandCausalKey(key: string): boolean {
  try {
    return (BigInt(key.split(':')[0]) & 0xffff_ff00_0000_0000n) === COMMAND_MARKER;
  } catch {
    return false;
  }
}

export const DROP_REASON_NAMES = ['unknown', 'ring full', 'rate limited', 'malformed', 'realm changed', 'rejected', 'coalesced'];

export type RequestKind = 'http' | 'command' | 'dropped';

/** A lifecycle point of a command with no span of its own (received, admitted, consumed, sent), on the thread slot that recorded it. */
export interface CausalMarker {
  slot: number;
  us: number;
}

/** One root and what it cost. Breakdown fields are absent when the capture did not carry the instant that bounds them. */
export interface RequestRow {
  /** Unique per row. Not the causal key: HTTP requests continuing one upstream trace (`traceparent`) share their trace id. */
  id: string;
  /** The causal id the root's engine spans carry, as `hi:lo`. */
  key: string;
  kind: RequestKind;
  /** What groups rows: `GET /swg/inventory`, `command 3 → Combat`, `dropped: ring full`. */
  label: string;
  session?: number;
  status?: number;
  startUs: number;
  endUs: number;
  totalUs: number;
  /** Engine spans carrying the root's id, and their summed duration. */
  spanCount: number;
  engineUs: number;
  /** Command only: received → admitted. */
  ringWaitUs?: number;
  /** Command only: admitted → first consumed. */
  tickWaitUs?: number;
  /** Command only: last engine span (or consumption) → frame sent. */
  sendWaitUs?: number;
  /** Command only: the lifecycle points the time area's flow line joins, in time order. Empty for an HTTP request, whose root is a span. */
  markers: CausalMarker[];
}

export interface RequestGroup {
  label: string;
  kind: RequestKind;
  count: number;
  p50Us: number;
  p99Us: number;
  maxUs: number;
  totalUs: number;
}

/** Only the system names are read, so the DTO and the model shape both fit. */
export type NamedSystem = { name?: string | null };

interface CausalAggregate {
  spanCount: number;
  engineUs: number;
  lastEndUs: number;
}

/** Every span carrying a causal id, aggregated per id. */
function aggregateByCause(ticks: readonly TickData[]): Map<string, CausalAggregate> {
  const map = new Map<string, CausalAggregate>();
  for (const tick of ticks) {
    for (const span of tick.spans) {
      if (span.traceIdHi === undefined || span.kind === TraceEventKind.ExternalRequest) continue;
      const key = causalKeyOf(span);
      if (key === null) continue;
      let agg = map.get(key);
      if (agg === undefined) {
        agg = { spanCount: 0, engineUs: 0, lastEndUs: -Infinity };
        map.set(key, agg);
      }
      agg.spanCount++;
      agg.engineUs += span.durationUs;
      if (span.endUs > agg.lastEndUs) agg.lastEndUs = span.endUs;
    }
  }
  return map;
}

/** A command's (session, seq) as one number: session is a u32 and seq a u16, so it fits a double exactly. */
const pairOf = (evt: TraceEvent): number => (evt.session ?? 0) * 65_536 + (evt.seq ?? 0);

/**
 * The roots that start inside <paramref name="range"/>, with their breakdown. Reads every loaded tick, not only the range's, so a command received
 * before the range or answered after it still gets its whole breakdown.
 *
 * A command's frame is the first `EgressSent` of its session after its last engine work whose tick is at or past the command's drain tick — a frame
 * the pump was still sending for an earlier tick does not answer it (design §3). Lookups binary-search per-key event lists sorted once, so the pass is
 * O(E log E) in the correlation events, not O(commands × frames).
 */
export function computeRequests(ticks: readonly TickData[], range: TimeRange, systems: readonly NamedSystem[]): RequestRow[] {
  const received = new Map<number, TraceEvent[]>();
  const admittedByPair = new Map<number, TraceEvent[]>();
  const consumed = new Map<number, TraceEvent[]>();
  const sent = new Map<number, TraceEvent[]>();
  const admitted: TraceEvent[] = [];
  const dropped: TraceEvent[] = [];
  const http: SpanData[] = [];

  for (const tick of ticks) {
    if (tick.hasExternalRequest === true) {
      for (const span of tick.spans) {
        if (span.kind === TraceEventKind.ExternalRequest) http.push(span);
      }
    }
    for (const evt of tick.correlation ?? []) {
      switch (evt.kind) {
        case TraceEventKind.IngressReceived:
          pushTo(received, pairOf(evt), evt);
          break;
        case TraceEventKind.IngressAdmitted:
          admitted.push(evt);
          pushTo(admittedByPair, pairOf(evt), evt);
          break;
        case TraceEventKind.IngressDropped:
          dropped.push(evt);
          break;
        case TraceEventKind.CommandConsumed:
          pushTo(consumed, pairOf(evt), evt);
          break;
        case TraceEventKind.EgressSent:
          pushTo(sent, evt.session ?? 0, evt);
          break;
      }
    }
  }
  if (http.length === 0 && admitted.length === 0 && dropped.length === 0) return [];

  const causes = aggregateByCause(ticks);
  for (const list of [received, admittedByPair, consumed, sent]) {
    for (const events of list.values()) events.sort(byTime);
  }

  const inRange = (us: number): boolean => us >= range.startUs && us < range.endUs;
  const rows: RequestRow[] = [];

  for (const span of http) {
    if (!inRange(span.startUs)) continue;
    const ev = span.rawEvent;
    const key = causalKeyOf(span) ?? `http:${span.spanId}`;
    const agg = causes.get(key);
    rows.push({
      id: `http:${span.threadSlot}:${span.spanId}`,
      key,
      kind: 'http',
      label: `${HTTP_METHOD_NAMES[ev?.httpMethod ?? 0] ?? 'OTHER'} ${ev?.httpRoute ?? '?'}`,
      status: ev?.httpStatus,
      startUs: span.startUs,
      endUs: span.endUs,
      totalUs: span.durationUs,
      spanCount: agg?.spanCount ?? 0,
      engineUs: agg?.engineUs ?? 0,
      markers: [],
    });
  }

  for (const adm of admitted) {
    const pair = pairOf(adm);
    // A (session, seq) recurs once the u16 wraps: the instants of this command lie between the admissions of the same pair before and after it.
    const sameAdmitted = admittedByPair.get(pair) ?? [];
    const at = sameAdmitted.indexOf(adm);
    const prevAdmUs = at > 0 ? sameAdmitted[at - 1].timestampUs : -Infinity;
    const nextAdmUs = at >= 0 && at + 1 < sameAdmitted.length ? sameAdmitted[at + 1].timestampUs : Infinity;
    const receivedCandidate = lastAtOrBefore(received.get(pair), adm.timestampUs);
    const receivedEvt = receivedCandidate !== undefined && receivedCandidate.timestampUs > prevAdmUs ? receivedCandidate : undefined;
    const startUs = receivedEvt?.timestampUs ?? adm.timestampUs;
    if (!inRange(startUs)) continue;

    const session = adm.session ?? 0;
    const drainTick = adm.corrTick ?? 0;
    const key = commandCausalKey(session, drainTick, adm.seq ?? 0);
    const agg = causes.get(key);
    const consumedCandidate = firstAtOrAfter(consumed.get(pair), adm.timestampUs);
    const consumption = consumedCandidate !== undefined && consumedCandidate.timestampUs < nextAdmUs ? consumedCandidate : undefined;
    const workEndUs = agg !== undefined || consumption !== undefined
      ? Math.max(agg?.lastEndUs ?? -Infinity, consumption?.timestampUs ?? -Infinity)
      : undefined;
    const frame = workEndUs !== undefined ? firstFrameAnswering(sent.get(session), workEndUs, drainTick) : undefined;
    const endUs = frame?.timestampUs ?? workEndUs ?? adm.timestampUs;
    const sysIdx = consumption?.consumerSystemIndex;
    const system = sysIdx !== undefined && sysIdx !== 0xffff ? systems[sysIdx]?.name ?? `System[${sysIdx}]` : undefined;

    const markers: CausalMarker[] = [];
    for (const e of [receivedEvt, adm, consumption, frame]) {
      if (e !== undefined) markers.push({ slot: e.threadSlot, us: e.timestampUs });
    }

    rows.push({
      id: `${key}:${adm.timestampUs}`,
      key,
      kind: 'command',
      label: `command ${adm.wireIdx ?? '?'}${system !== undefined ? ` → ${system}` : ''}`,
      session,
      startUs,
      endUs,
      totalUs: endUs - startUs,
      spanCount: agg?.spanCount ?? 0,
      engineUs: agg?.engineUs ?? 0,
      ringWaitUs: receivedEvt !== undefined ? adm.timestampUs - receivedEvt.timestampUs : undefined,
      tickWaitUs: consumption !== undefined ? consumption.timestampUs - adm.timestampUs : undefined,
      sendWaitUs: frame !== undefined && workEndUs !== undefined ? frame.timestampUs - workEndUs : undefined,
      markers,
    });
  }

  for (const d of dropped) {
    if (!inRange(d.timestampUs)) continue;
    const key = `dropped:${d.session}:${d.seq}:${d.timestampUs}`;
    rows.push({
      id: key,
      key,
      kind: 'dropped',
      label: `dropped: ${DROP_REASON_NAMES[d.dropReason ?? 0] ?? 'unknown'}`,
      session: d.session,
      startUs: d.timestampUs,
      endUs: d.timestampUs,
      totalUs: 0,
      spanCount: 0,
      engineUs: 0,
      markers: [],
    });
  }

  rows.sort((a, b) => a.startUs - b.startUs);
  return rows;
}

/** The lifecycle markers of the root carrying <paramref name="key"/>, for the time area's flow line; none when no command row carries it. */
export function markersFor(rows: readonly RequestRow[], key: string): CausalMarker[] {
  for (const row of rows) {
    if (row.key === key && row.markers.length > 0) return row.markers;
  }
  return [];
}

/** Rows grouped by label, with count and latency percentiles (nearest-rank). */
export function groupRequests(rows: readonly RequestRow[]): RequestGroup[] {
  const byLabel = new Map<string, RequestRow[]>();
  for (const row of rows) pushTo(byLabel, row.label, row);
  const groups: RequestGroup[] = [];
  for (const [label, members] of byLabel) {
    const totals = members.map((r) => r.totalUs).sort((a, b) => a - b);
    groups.push({
      label,
      kind: members[0].kind,
      count: members.length,
      p50Us: percentile(totals, 0.5),
      p99Us: percentile(totals, 0.99),
      maxUs: totals[totals.length - 1],
      totalUs: totals.reduce((s, v) => s + v, 0),
    });
  }
  return groups;
}

/** Nearest-rank percentile of an ascending array. */
export function percentile(sortedAsc: readonly number[], p: number): number {
  if (sortedAsc.length === 0) return 0;
  const rank = Math.ceil(p * sortedAsc.length);
  return sortedAsc[Math.min(sortedAsc.length, Math.max(1, rank)) - 1];
}

/** Every loaded span carrying <paramref name="key"/>, in start order. */
export function spansForCausal(ticks: readonly TickData[], key: string): SpanData[] {
  const out: SpanData[] = [];
  for (const tick of ticks) {
    for (const span of tick.spans) {
      if (causalKeyOf(span) === key) out.push(span);
    }
  }
  out.sort((a, b) => a.startUs - b.startUs);
  return out;
}

const byTime = (a: TraceEvent, b: TraceEvent): number => a.timestampUs - b.timestampUs;

function pushTo<K, V>(map: Map<K, V[]>, key: K, value: V): void {
  const arr = map.get(key);
  if (arr === undefined) map.set(key, [value]);
  else arr.push(value);
}

/** Index of the first event at or after <paramref name="us"/> in a time-sorted list. */
function lowerBound(sorted: readonly TraceEvent[], us: number): number {
  let lo = 0;
  let hi = sorted.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (sorted[mid].timestampUs < us) lo = mid + 1;
    else hi = mid;
  }
  return lo;
}

function firstAtOrAfter(sorted: readonly TraceEvent[] | undefined, us: number): TraceEvent | undefined {
  return sorted === undefined ? undefined : sorted[lowerBound(sorted, us)];
}

function lastAtOrBefore(sorted: readonly TraceEvent[] | undefined, us: number): TraceEvent | undefined {
  if (sorted === undefined) return undefined;
  let lo = 0;
  let hi = sorted.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (sorted[mid].timestampUs <= us) lo = mid + 1;
    else hi = mid;
  }
  return lo > 0 ? sorted[lo - 1] : undefined;
}

/** The first frame of a session sent at or after <paramref name="us"/> for a tick at or past <paramref name="drainTick"/>. */
function firstFrameAnswering(sorted: readonly TraceEvent[] | undefined, us: number, drainTick: number): TraceEvent | undefined {
  if (sorted === undefined) return undefined;
  for (let i = lowerBound(sorted, us); i < sorted.length; i++) {
    const tick = sorted[i].corrTick;
    if (tick === undefined || tick >= drainTick) return sorted[i];
  }
  return undefined;
}
