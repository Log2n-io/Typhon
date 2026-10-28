import { describe, expect, it } from 'vitest';
import { flowSegments, type FlowPoint } from '@/libs/profiler/correlation/causalFlow';

const pt = (us: number, lane: number, root = false): FlowPoint => ({ x: us, y: lane * 10, us, lane, root });
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
