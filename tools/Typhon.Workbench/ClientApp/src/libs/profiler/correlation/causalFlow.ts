/**
 * The geometry of a selected request's flow line in the time area (#1517), apart from the canvas so it can be tested.
 */

/** A point on the flow, in canvas coordinates: a caused span's left edge or a lifecycle marker, or the root's own bar. */
export interface FlowPoint {
  x: number;
  y: number;
  us: number;
  /** The lane it sits in — the thread slot for spans and markers. Points of one lane chain; the root fans out once per lane. */
  lane: number;
  root?: boolean;
}

/**
 * The segments to draw, each from → to. With the root on screen it fans out to the first point of each lane, and each lane then chains its own
 * points in time order; without it (scrolled off), every point chains in time order.
 */
export function flowSegments(points: readonly FlowPoint[]): [FlowPoint, FlowPoint][] {
  const sorted = points.slice().sort((a, b) => a.us - b.us || a.x - b.x);
  const segments: [FlowPoint, FlowPoint][] = [];
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
