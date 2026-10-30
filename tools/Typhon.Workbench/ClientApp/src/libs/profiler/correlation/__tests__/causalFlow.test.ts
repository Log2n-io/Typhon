import { describe, expect, it } from 'vitest';
import {
  describeFlowSegment, describeStepMark, flowSegments, hitFlowSegment, hitStepMark, type FlowPoint, type FlowPointKind,
} from '@/libs/profiler/correlation/causalFlow';

const pt = (us: number, lane: number, root = false, kind: FlowPointKind = root ? 'root' : 'span'): FlowPoint => ({
  x: us, y: lane * 10, us, lane, root, kind, label: `${kind}@${us}`,
  nav: { startUs: us, endUs: us + 1, trackId: `slot-${lane}`, rowOffset: 0, rowHeight: 14 },
});
const pairs = (points: FlowPoint[]): [number, number][] => flowSegments(points).map(([a, b]) => [a.us, b.us]);

describe('flowSegments', () => {
  it('fans out from the root once per lane, then chains each lane in time order', () => {
    const root = pt(0, -1, true);
    // Lane 3 holds two spans on different rows (y), one lane all the same: the root reaches it once.
    const points = [pt(30, 3), { ...pt(20, 3), y: 44 }, pt(10, 1), root, pt(40, 1)];
    expect(pairs(points)).toEqual([[0, 10], [0, 20], [20, 30], [10, 40]]);
  });

  it('chains every point in time order when the root is off screen', () => {
    expect(pairs([pt(30, 2), pt(10, 1), pt(20, 3)])).toEqual([[10, 20], [20, 30]]);
  });

  it('draws nothing for a single point', () => {
    expect(flowSegments([pt(5, 1)])).toEqual([]);
    expect(flowSegments([pt(0, -1, true)])).toEqual([]);
  });
});

describe('hitFlowSegment', () => {
  const a = { ...pt(0, 0), x: 0, y: 0 };
  const b = { ...pt(10, 0), x: 100, y: 0 };
  const c = { ...pt(20, 0), x: 100, y: 100 };
  const segments = flowSegments([a, b, c]);

  it('finds the segment the pointer is on, within the tolerance, the nearest first', () => {
    expect(hitFlowSegment(segments, 50, 3)).toBe(0);
    expect(hitFlowSegment(segments, 98, 50)).toBe(1);
    expect(hitFlowSegment(segments, 50, 20)).toBe(-1);
  });

  it('ignores arrows too short to be drawn', () => {
    const near = { ...pt(1, 0), x: 3, y: 0 };
    expect(hitFlowSegment(flowSegments([a, near]), 1, 0)).toBe(-1);
  });
});

describe('describeFlowSegment', () => {
  it('names a command step and says what the wait between two steps was', () => {
    const received = pt(100, 5, false, 'received');
    const admitted = pt(350, 0, false, 'admitted');
    const [from, to, relation, after] = describeFlowSegment([received, admitted]);
    expect([from, to]).toEqual(['From: Command received', 'To: Command admitted to a tick']);
    expect(relation).toMatch(/ingress ring/);
    expect(after).toMatch(/^After 250/);
  });

  it('names spans and the request by their labels', () => {
    const root = { ...pt(0, -1, true), label: 'GET /swg/inventory' };
    const span = { ...pt(40, 2), label: 'BTree.Insert' };
    expect(describeFlowSegment([root, span]).slice(0, 3)).toEqual(['From: GET /swg/inventory', 'To: BTree.Insert', 'The request ran this engine work']);
  });
});

describe('lifecycle step marks', () => {
  const root = { ...pt(0, -1, true), x: 0, y: 0 };
  const sent = { ...pt(61, 48, false, 'sent'), x: 200, y: 300, nav: { startUs: 61, endUs: 61, trackId: 'slot-48', rowOffset: 0, rowHeight: 18 } };
  const segments = flowSegments([root, sent]);

  it('a step diamond is hit around its centre, and wins over the arrow ending on it', () => {
    expect(hitStepMark(segments, 202, 301)?.kind).toBe('sent');
    expect(hitStepMark(segments, 210, 300)).toBeNull();
    expect(hitStepMark(segments, 100, 150)).toBeNull(); // on the arrow, not the diamond
  });

  it('a step tooltip names the step, its thread and when it happened from the flow start', () => {
    expect(describeStepMark(sent, segments).slice(0, 3)).toEqual(['Command answer sent', 'On thread slot 48', expect.stringMatching(/^At \+61/)]);
    expect(describeStepMark(sent, segments, 'SendPump')[1]).toBe('On SendPump (slot 48)');
  });
});
