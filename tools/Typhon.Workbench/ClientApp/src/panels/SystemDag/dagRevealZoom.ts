import { NODE_HEIGHT, NODE_WIDTH } from './dagModel';

/**
 * Compute the target xyflow zoom for a "Reveal in System DAG" hand-off (#379 follow-up,
 * 2026-05-26). The legacy behaviour was `instance.setCenter(node, { zoom: getZoom() })` — preserved
 * the user's current zoom and merely panned the camera, so revealing from a fully-zoomed-out state
 * landed the target as a tiny tile lost in the crowd. The new rule frames the target by **1-hop
 * neighbourhood** (the DAG's whole point is dependencies; revealing a node without showing what it
 * connects to defeats the panel's purpose):
 *
 *  1. **Primary** — fit the bounding box of `{node + visible 1-hop predecessors + visible 1-hop
 *     successors}` into the viewport with `FIT_PADDING_PX` inset.
 *  2. **Cap** — never zoom in so far that the target node would occupy more than
 *     `COVERAGE_CAP` (60 %) of either viewport axis. Stops "isolated node fills the screen,
 *     where am I?" disorientation when the target has no neighbours.
 *  3. **Floor** — never zoom *out* below the user's current zoom. Revealing repositions the
 *     camera; it doesn't undo the detail level the user chose.
 *
 * Edge filter: a "visible" neighbour is one currently present in `nodes` (xyflow's rendered set).
 * Filters / phase collapse / `showEngineTracks=false` all manifest as the neighbour being absent
 * from `nodes`, so we exclude it and the fit shrinks to what the user can actually see.
 *
 * Pure function — testable without xyflow. Returns the zoom only; the caller still drives
 * `instance.setCenter(node, { zoom })`. xyflow's own min/max clamp applies at the call site.
 */

export interface DagRevealNode {
  id: string;
  position: { x: number; y: number };
}

export interface DagRevealEdge {
  source: string;
  target: string;
}

export interface ComputeRevealZoomInput {
  /** Target node id — must be present in `nodes`; if it isn't, callers should bail before invoking. */
  nodeId: string;
  /** Currently rendered nodes (xyflow's visible set — already honours filters / engine-track collapse). */
  nodes: readonly DagRevealNode[];
  /** All edges — `source` / `target` are node ids. Edges referencing hidden nodes are filtered by membership. */
  edges: readonly DagRevealEdge[];
  /** Viewport size in CSS px. */
  viewportWidth: number;
  viewportHeight: number;
  /** User's current xyflow zoom (`instance.getZoom()`). Acts as the floor. */
  currentZoom: number;
}

/** Padding (px from each viewport edge) used by the subgraph-fit math. */
export const FIT_PADDING_PX = 40;
/**
 * Upper bound on how much of either viewport axis the **target node** may cover. Tuned down from an
 * initial 0.6 → 0.25 (2026-05-26) after live testing showed the original cap produced a 3.3× zoom on
 * isolated 180×56 nodes on a 1000-px-wide panel — node ate ~60% of the screen, no context visible.
 * 0.25 keeps the node a prominent focal point (~180–300 px wide depending on panel size) while
 * leaving room for 5–10 surrounding nodes to remain in view.
 */
export const COVERAGE_CAP = 0.25;
/**
 * Fraction of the padded viewport that the 1-hop subgraph is allowed to fill. Backing off from
 * "fit-to-edges" leaves a margin around the subgraph so 2nd-hop neighbours can peek through —
 * matters because the DAG's value is dependency context, and a tight fit on just 1-hop crops the
 * neighbours-of-neighbours that frame the bigger picture. Multiplier applied AFTER the raw fit zoom
 * so the cap still binds independently on isolated nodes.
 */
export const FIT_SUBGRAPH_FRACTION = 0.65;

export function computeRevealTargetZoom(input: ComputeRevealZoomInput): number {
  const { nodeId, nodes, edges, viewportWidth, viewportHeight, currentZoom } = input;
  if (!(viewportWidth > 0) || !(viewportHeight > 0)) return currentZoom;

  const visibleIds = new Set(nodes.map((n) => n.id));
  if (!visibleIds.has(nodeId)) return currentZoom; // defensive — caller should have filtered this

  // 1-hop neighbours that are currently visible (predecessors via edges targeting the node, successors
  // via edges sourced from it).
  const neighborIds = new Set<string>();
  for (const e of edges) {
    if (e.source === nodeId && visibleIds.has(e.target)) neighborIds.add(e.target);
    if (e.target === nodeId && visibleIds.has(e.source)) neighborIds.add(e.source);
  }
  // Subgraph = target + visible neighbours.
  const subgraph: DagRevealNode[] = [];
  for (const n of nodes) {
    if (n.id === nodeId || neighborIds.has(n.id)) subgraph.push(n);
  }

  // Bounding box of the subgraph in flow-space px. Always includes the target node, so the box is
  // never empty.
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const n of subgraph) {
    const x = n.position.x;
    const y = n.position.y;
    if (x < minX) minX = x;
    if (y < minY) minY = y;
    if (x + NODE_WIDTH > maxX) maxX = x + NODE_WIDTH;
    if (y + NODE_HEIGHT > maxY) maxY = y + NODE_HEIGHT;
  }
  const subW = Math.max(1, maxX - minX);
  const subH = Math.max(1, maxY - minY);

  // Fit-zoom that brings the subgraph into the padded viewport, BACKED OFF by `FIT_SUBGRAPH_FRACTION`
  // so the subgraph fills ~65 % of the padded viewport (not 100 %). The remaining margin is what lets
  // 2nd-hop neighbours peek in at the edges instead of getting cropped — without this back-off, the
  // 1-hop fit was visually claustrophobic.
  const usableW = Math.max(1, viewportWidth - 2 * FIT_PADDING_PX);
  const usableH = Math.max(1, viewportHeight - 2 * FIT_PADDING_PX);
  const fitZoom = FIT_SUBGRAPH_FRACTION * Math.min(usableW / subW, usableH / subH);

  // Coverage cap — the zoom at which the target node alone would cover COVERAGE_CAP of either axis.
  // Bites for isolated nodes (subgraph = just the target) where fitZoom would otherwise zoom in
  // until the node fills the viewport.
  const coverageCap = Math.min(
    (viewportWidth * COVERAGE_CAP) / NODE_WIDTH,
    (viewportHeight * COVERAGE_CAP) / NODE_HEIGHT,
  );

  // Primary target = min(fit, cap). Floor = currentZoom (never zoom out on reveal — respect the
  // user's detail level).
  return Math.max(currentZoom, Math.min(fitZoom, coverageCap));
}
