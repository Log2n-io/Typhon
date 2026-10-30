import { formatDuration } from '@/libs/profiler/canvas/canvasUtils';
import type { SpanData } from '@/libs/profiler/model/traceModel';

/**
 * The geometry of a selected request's flow line in the time area (#1517), apart from the canvas so it can be tested: which segments to draw, which
 * one the pointer is on, what it means, and where clicking it leads.
 */

/** What a flow point stands for. */
export type FlowPointKind = 'root' | 'span' | 'received' | 'admitted' | 'consumed' | 'sent';

/**
 * Where a flow point lives in the time area, in layout terms, so a click can bring it into view: its time range, its track, and the row inside the
 * track (offset from the track's top, and height).
 */
export interface NavTarget {
  startUs: number;
  endUs: number;
  trackId: string;
  rowOffset: number;
  rowHeight: number;
}

/** A point on the flow, in canvas coordinates: a caused span's left edge or a lifecycle marker, or the root's own bar. */
export interface FlowPoint {
  x: number;
  y: number;
  us: number;
  /** The lane it sits in — the thread slot for spans and markers. Points of one lane chain; the root fans out once per lane. */
  lane: number;
  root?: boolean;
  kind: FlowPointKind;
  /** What the tooltip calls it: the span's name, the request's label, or the lifecycle step. */
  label: string;
  /** The span, for a span point: a click on an arrow into it selects it. */
  span?: SpanData;
  nav: NavTarget;
}

export type FlowSegment = [FlowPoint, FlowPoint];

/**
 * The segments to draw, each from → to. With the root on screen it fans out to the first point of each lane, and each lane then chains its own
 * points in time order; without it (scrolled off), every point chains in time order.
 */
export function flowSegments(points: readonly FlowPoint[]): FlowSegment[] {
  const sorted = points.slice().sort((a, b) => a.us - b.us || a.x - b.x);
  const segments: FlowSegment[] = [];
  const root = sorted.find((p) => p.root === true);
  if (root === undefined) {
    for (let i = 1; i < sorted.length; i++) segments.push([sorted[i - 1], sorted[i]]);
    return segments;
  }

  const lastByLane = new Map<number, FlowPoint>();
  for (const p of sorted) {
    if (p === root) continue;
    segments.push([lastByLane.get(p.lane) ?? root, p]);
    lastByLane.set(p.lane, p);
  }
  return segments;
}

/** Half-size of the diamond a command's lifecycle step is drawn as on its thread's lane, in pixels. */
export const STEP_MARK_RADIUS = 5;

/** True for a command's lifecycle steps — points that have no span of their own, drawn as a diamond. */
export const isStepPoint = (p: FlowPoint): boolean => p.kind === 'received' || p.kind === 'admitted' || p.kind === 'consumed' || p.kind === 'sent';

/** The lifecycle step whose diamond is under (mx, my), or null; steps win over the arrows that end on them. */
export function hitStepMark(segments: readonly FlowSegment[], mx: number, my: number): FlowPoint | null {
  for (const [a, b] of segments) {
    for (const p of [a, b]) {
      if (isStepPoint(p) && Math.abs(mx - p.x) + Math.abs(my - p.y) <= STEP_MARK_RADIUS + 1) return p;
    }
  }
  return null;
}

/** A step's tooltip: what happened, on which thread, and when, measured from the earliest point of the flow drawn. */
export function describeStepMark(step: FlowPoint, segments: readonly FlowSegment[], threadName?: string): string[] {
  let originUs = step.us;
  for (const [a, b] of segments) originUs = Math.min(originUs, a.us, b.us);
  const slot = step.nav.trackId.replace('slot-', '');
  return [
    `Command ${STEP_NAMES[step.kind]}`,
    threadName ? `On ${threadName} (slot ${slot})` : `On thread slot ${slot}`,
    `At +${formatDuration(Math.max(0, step.us - originUs))}`,
    'Click to show the command',
  ];
}

/** Index of the segment within <paramref name="tolPx"/> of (mx, my) — the nearest when several are — or -1. Arrows shorter than 8 px are not drawn and not hit. */
export function hitFlowSegment(segments: readonly FlowSegment[], mx: number, my: number, tolPx = 5): number {
  let best = -1;
  let bestD = tolPx;
  for (let i = 0; i < segments.length; i++) {
    const [a, b] = segments[i];
    const dx = b.x - a.x;
    const dy = b.y - a.y;
    const len2 = dx * dx + dy * dy;
    if (len2 < 64) continue;
    const t = Math.max(0, Math.min(1, ((mx - a.x) * dx + (my - a.y) * dy) / len2));
    const d = Math.hypot(mx - (a.x + t * dx), my - (a.y + t * dy));
    if (d <= bestD) {
      best = i;
      bestD = d;
    }
  }
  return best;
}

const STEP_NAMES: Record<FlowPointKind, string> = {
  root: 'request',
  span: 'span',
  received: 'received',
  admitted: 'admitted to a tick',
  consumed: 'read by a system',
  sent: 'answer sent',
};

/** What a segment means, for its tooltip: the two ends, the relationship between them and the time it took. */
export function describeFlowSegment([from, to]: FlowSegment): string[] {
  const name = (p: FlowPoint): string => (p.kind === 'span' || p.kind === 'root' ? p.label : `Command ${STEP_NAMES[p.kind]}`);
  return [
    `From: ${name(from)}`,
    `To: ${name(to)}`,
    relation(from, to),
    `After ${formatDuration(Math.max(0, to.us - from.us))}`,
    'Click to go to it',
  ];
}

function relation(from: FlowPoint, to: FlowPoint): string {
  if (from.kind === 'received' && to.kind === 'admitted') return 'Waited in the session\'s ingress ring for the next tick\'s drain';
  if (from.kind === 'admitted' && to.kind === 'consumed') return 'Waited in its tick for the system that reads it';
  if (to.kind === 'sent') return 'The frame answering it left for the client';
  if (from.kind === 'root' && to.kind === 'span') return 'The request ran this engine work';
  if (from.kind === 'root') return `The command was ${STEP_NAMES[to.kind]}`;
  if (from.kind === 'consumed' && to.kind === 'span') return 'The system that read it did this engine work';
  if (to.kind === 'span') return 'Next engine work for the same cause';
  return `The command was ${STEP_NAMES[to.kind]}`;
}
