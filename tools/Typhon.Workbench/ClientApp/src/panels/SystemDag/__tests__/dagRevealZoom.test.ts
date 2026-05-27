import { describe, expect, it } from 'vitest';
import { computeRevealTargetZoom, COVERAGE_CAP, FIT_PADDING_PX, FIT_SUBGRAPH_FRACTION, type DagRevealEdge, type DagRevealNode } from '../dagRevealZoom';
import { NODE_HEIGHT, NODE_WIDTH } from '../dagModel';

/**
 * Tests for `computeRevealTargetZoom` — the "Reveal in System DAG" zoom math (#379 follow-up,
 * 2026-05-26). The math is the contract: fit the 1-hop subgraph, cap node coverage at 60 %, floor
 * at the user's current zoom. The xyflow `setCenter` call at the call site is not under test here
 * (would need a real xyflow harness); we just verify the zoom value the controller passes in.
 */

function nodeAt(id: string, x: number, y: number): DagRevealNode {
  return { id, position: { x, y } };
}

const VIEWPORT_W = 1000;
const VIEWPORT_H = 600;

describe('computeRevealTargetZoom', () => {
  it('isolated node (no neighbours) → coverage cap binds, node ≤ COVERAGE_CAP × viewport on smaller axis', () => {
    // Target alone in the visible set; no edges → no neighbours → subgraph = [node].
    const target = nodeAt('A', 0, 0);
    const zoom = computeRevealTargetZoom({
      nodeId: 'A',
      nodes: [target],
      edges: [],
      viewportWidth: VIEWPORT_W,
      viewportHeight: VIEWPORT_H,
      currentZoom: 0.5,
    });

    // Coverage cap = min(W*0.6/NODE_WIDTH, H*0.6/NODE_HEIGHT).
    const expectedCap = Math.min(
      (VIEWPORT_W * COVERAGE_CAP) / NODE_WIDTH,
      (VIEWPORT_H * COVERAGE_CAP) / NODE_HEIGHT,
    );
    expect(zoom).toBeCloseTo(expectedCap, 5);

    // Sanity: the resulting node footprint on-screen ≤ 60% of the smaller axis.
    const nodeOnScreenW = NODE_WIDTH * zoom;
    const nodeOnScreenH = NODE_HEIGHT * zoom;
    expect(nodeOnScreenW).toBeLessThanOrEqual(VIEWPORT_W * COVERAGE_CAP + 1e-6);
    expect(nodeOnScreenH).toBeLessThanOrEqual(VIEWPORT_H * COVERAGE_CAP + 1e-6);
  });

  it('1-hop neighbours within reach → fit zoom (subgraph + padding fills the padded viewport)', () => {
    // Target at (0,0), one predecessor 200px to the left, one successor 200px to the right.
    // Subgraph spans from (-200, 0) to (200 + NODE_WIDTH, NODE_HEIGHT) — a wide box, so fit zoom
    // is dominated by the horizontal dimension.
    const target = nodeAt('B', 0, 0);
    const pred = nodeAt('A', -200, 0);
    const succ = nodeAt('C', 200, 0);
    const edges: DagRevealEdge[] = [
      { source: 'A', target: 'B' },
      { source: 'B', target: 'C' },
    ];
    const zoom = computeRevealTargetZoom({
      nodeId: 'B',
      nodes: [pred, target, succ],
      edges,
      viewportWidth: VIEWPORT_W,
      viewportHeight: VIEWPORT_H,
      currentZoom: 0.1, // very zoomed out — the fit zoom will exceed this so the floor is irrelevant
    });

    // Subgraph bounding box: x ∈ [-200, 200 + NODE_WIDTH], y ∈ [0, NODE_HEIGHT]. The fit-zoom is
    // backed off by `FIT_SUBGRAPH_FRACTION` so the subgraph fills ~65% of the padded viewport
    // (not 100%) — leaves room for 2nd-hop neighbours to peek in at the edges.
    const subW = 200 + NODE_WIDTH - (-200);
    const subH = NODE_HEIGHT;
    const fitX = (VIEWPORT_W - 2 * FIT_PADDING_PX) / subW;
    const fitY = (VIEWPORT_H - 2 * FIT_PADDING_PX) / subH;
    const expectedFit = FIT_SUBGRAPH_FRACTION * Math.min(fitX, fitY);
    // Cap (if it bites) — coverage cap on the SINGLE target node.
    const cap = Math.min(
      (VIEWPORT_W * COVERAGE_CAP) / NODE_WIDTH,
      (VIEWPORT_H * COVERAGE_CAP) / NODE_HEIGHT,
    );

    // The expected output is Math.min(fitZoom, cap), floored at currentZoom.
    const expected = Math.min(expectedFit, cap);
    expect(zoom).toBeCloseTo(expected, 5);
  });

  it('floor (currentZoom) wins when fit zoom would zoom out — reveal pans without changing detail level', () => {
    // Subgraph is HUGE (predecessor and successor far away) — fit zoom is tiny. User is currently
    // zoomed in to 2.0. Reveal must keep them at 2.0 and just pan; otherwise the camera would zoom
    // way out and the user loses their current focus.
    const target = nodeAt('B', 0, 0);
    const pred = nodeAt('A', -5000, 0);
    const succ = nodeAt('C', 5000, 0);
    const edges: DagRevealEdge[] = [
      { source: 'A', target: 'B' },
      { source: 'B', target: 'C' },
    ];
    const currentZoom = 2.0;
    const zoom = computeRevealTargetZoom({
      nodeId: 'B',
      nodes: [pred, target, succ],
      edges,
      viewportWidth: VIEWPORT_W,
      viewportHeight: VIEWPORT_H,
      currentZoom,
    });
    expect(zoom).toBe(currentZoom); // floor preserved
  });

  it('hidden neighbour (not in nodes) is excluded from the fit — reveal frames what the user can see', () => {
    // Target has TWO edges, but only one neighbour is in the visible set. The other was filtered
    // out (engine-track collapse, phase filter, etc.) — its position MUST NOT pull the fit toward
    // empty space.
    const target = nodeAt('B', 0, 0);
    const visibleSucc = nodeAt('C', 100, 0);
    const edges: DagRevealEdge[] = [
      { source: 'B', target: 'C' },     // C is visible — counts
      { source: 'B', target: 'HIDDEN' }, // HIDDEN is filtered — must NOT count
    ];
    const zoomWith = computeRevealTargetZoom({
      nodeId: 'B',
      nodes: [target, visibleSucc],
      edges,
      viewportWidth: VIEWPORT_W,
      viewportHeight: VIEWPORT_H,
      currentZoom: 0.1,
    });
    // Compare to a reveal where the hidden edge is absent — result MUST be identical.
    const zoomWithout = computeRevealTargetZoom({
      nodeId: 'B',
      nodes: [target, visibleSucc],
      edges: [{ source: 'B', target: 'C' }],
      viewportWidth: VIEWPORT_W,
      viewportHeight: VIEWPORT_H,
      currentZoom: 0.1,
    });
    expect(zoomWith).toBeCloseTo(zoomWithout, 5);
  });

  it('predecessor edges (target = nodeId) and successor edges (source = nodeId) both count as 1-hop', () => {
    // The DAG has both directions; the reveal subgraph must include neighbours reached either way.
    const target = nodeAt('B', 0, 0);
    const predOnly = nodeAt('A', -300, 0);
    // Edge A → B means A is a predecessor of B.
    const edges: DagRevealEdge[] = [{ source: 'A', target: 'B' }];
    const zoom = computeRevealTargetZoom({
      nodeId: 'B',
      nodes: [predOnly, target],
      edges,
      viewportWidth: VIEWPORT_W,
      viewportHeight: VIEWPORT_H,
      currentZoom: 0.1,
    });
    // If predecessor wasn't included, subgraph would just be [target] and zoom would equal cap.
    // With predecessor included, the subgraph spans (-300, 0) → (NODE_WIDTH, NODE_HEIGHT), so fit
    // zoom is < cap — different from the isolated-node case.
    const cap = Math.min(
      (VIEWPORT_W * COVERAGE_CAP) / NODE_WIDTH,
      (VIEWPORT_H * COVERAGE_CAP) / NODE_HEIGHT,
    );
    expect(zoom).toBeLessThan(cap);
  });

  it('degenerate viewport (width or height ≤ 0) → returns currentZoom (no recenter)', () => {
    const target = nodeAt('A', 0, 0);
    const out = computeRevealTargetZoom({
      nodeId: 'A',
      nodes: [target],
      edges: [],
      viewportWidth: 0,   // panel hidden / not measured
      viewportHeight: 600,
      currentZoom: 1.5,
    });
    expect(out).toBe(1.5);
  });

  it('target not in visible nodes → returns currentZoom (defensive)', () => {
    const other = nodeAt('A', 0, 0);
    const out = computeRevealTargetZoom({
      nodeId: 'MISSING',
      nodes: [other],
      edges: [],
      viewportWidth: VIEWPORT_W,
      viewportHeight: VIEWPORT_H,
      currentZoom: 1.0,
    });
    // Caller should have bailed before invoking, but defensive guard returns the user's current
    // zoom rather than NaN.
    expect(out).toBe(1.0);
  });
});
