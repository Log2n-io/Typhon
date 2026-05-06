import dagre from 'dagre';
import type { Edge, Node } from '@xyflow/react';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { deriveEdges, type DerivedEdge, type DerivedEdgeKind } from './edgeDerivation';
import type { LayoutMode } from './useDagViewStore';

/**
 * DAG model — pure transform from topology DTO → React Flow nodes/edges. Four layout strategies
 * are supported (see {@link LayoutMode}):
 *
 * - `horizontal-lanes` (default per `09-system-dag.md §4.1`): phases stack vertically as swim-
 *   lanes; systems flow LR within each phase via dagre. Inter-phase edges are dropped — the lane
 *   order IS the phase contract; rendering them is O(systems²) noise.
 * - `vertical-lanes`: phases as side-by-side columns; systems flow TB within each phase.
 * - `compact`: flat dagre LR over all systems; lanes are not rendered AND cross-phase edges are
 *   surfaced (the user explicitly opted out of the swim-lane contract).
 * - `circular`: systems on a single circle, ordered by phase then by name. No lanes, no dagre.
 *
 * Systems whose phase isn't in `topology.phases` (or whose phaseName is empty) fall into a
 * synthetic `(unphased)` group — last lane / last bucket — present in non-RFC-07 traces.
 */

export interface DagNodeData extends Record<string, unknown> {
  systemName: string;
  kind: 'Pipeline' | 'Query' | 'Callback' | 'Unknown';
  phaseName: string;
  isParallel: boolean;
  isExclusivePhase: boolean;
  tierFilter: number;
  // Display chips — derived from access declarations. Kept as raw arrays so the renderer can
  // chip them without re-parsing.
  reads: string[];
  readsFresh: string[];
  readsSnapshot: string[];
  writes: string[];
  sideWrites: string[];
  changeFilterTypes: string[]; // not in DTO yet — placeholder for future field
  /** True if this system's declarations produced any access — used to dim "blank" tiles. */
  hasAccess: boolean;
}

export interface DagEdgeData extends Record<string, unknown> {
  kind: DerivedEdgeKind;
  via: string[];
  reason: string;
}

export type DagNode = Node<DagNodeData>;
export type DagEdge = Edge<DagEdgeData>;

export interface PhaseLane {
  /** Phase name, or `(unphased)` for the fallback lane. */
  name: string;
  /** Order index in the canonical phase list (-1 for `(unphased)`). */
  index: number;
  systemCount: number;
  /** Absolute x of the lane's left edge (px). */
  xLeft: number;
  /** Absolute y of the lane's top edge (px). Kept under `yTop` (rather than `y`) for back-compat with existing tests. */
  yTop: number;
  /** Lane height (px). */
  height: number;
  /** Lane width (px). */
  width: number;
  /**
   * Where the lane label sits relative to the lane bounding box. `'left'` for horizontal lanes
   * (sticky-left); `'top'` for vertical lanes (sticky-top). Phase-agnostic layouts (`compact`,
   * `circular`) emit no lanes, so this field is never observed for them.
   */
  labelEdge: 'left' | 'top';
}

export interface DagModel {
  nodes: DagNode[];
  edges: DagEdge[];
  lanes: PhaseLane[];
  /** Total bounding-box width — useful for sizing the lane background tiles. */
  width: number;
  /** Total bounding-box height. */
  height: number;
}

/** Tile dimensions used by the dagre layout and the React-Flow renderer. */
export const NODE_WIDTH = 180;
export const NODE_HEIGHT = 56;

/** Vertical gap between phase lanes. */
export const LANE_GAP = 32;
/** Padding inside a lane (top + bottom + left). The lane label sits in the left margin. */
export const LANE_PADDING = 16;
const LANE_LABEL_WIDTH = 160;
/** Vertical space reserved above the systems in a vertical lane for the phase label. */
const LANE_LABEL_HEIGHT = 28;

const SYNTHETIC_PHASE = '(unphased)';

/** Narrowed topology — `systems` and `phases` are guaranteed non-null after the buildDagModel guard. */
interface ResolvedTopology {
  systems: SystemDefinitionDto[];
  phases: string[];
}

/**
 * Build the full model for an entire topology DTO. Pure function — no React, no DOM.
 *
 * `layout` defaults to `'horizontal-lanes'` so existing call sites and tests don't change shape.
 */
export function buildDagModel(
  topology: TopologyDto | null | undefined,
  layout: LayoutMode = 'horizontal-lanes',
): DagModel {
  if (!topology || !topology.systems || topology.systems.length === 0) {
    return { nodes: [], edges: [], lanes: [], width: 0, height: 0 };
  }
  const resolved: ResolvedTopology = {
    systems: topology.systems,
    phases: topology.phases ?? [],
  };

  switch (layout) {
    case 'horizontal-lanes':
      return layoutHorizontalLanes(resolved);
    case 'vertical-lanes':
      return layoutVerticalLanes(resolved);
    case 'compact':
      return layoutCompact(resolved);
    case 'circular':
      return layoutCircular(resolved);
  }
}

// ── Phase bucketing helper (shared by both lane-based layouts) ───────────

interface OrderedPhase {
  name: string;
  index: number;
  systems: SystemDefinitionDto[];
}

function bucketByPhase(topology: ResolvedTopology): OrderedPhase[] {
  const phaseOrder = topology.phases.filter((p): p is string => !!p);
  const phaseToIndex = new Map<string, number>();
  phaseOrder.forEach((p, i) => phaseToIndex.set(p, i));

  const buckets = new Map<string, SystemDefinitionDto[]>();
  for (const s of topology.systems) {
    const key = (s.phaseName && phaseToIndex.has(s.phaseName)) ? s.phaseName : SYNTHETIC_PHASE;
    let bucket = buckets.get(key);
    if (!bucket) {
      bucket = [];
      buckets.set(key, bucket);
    }
    bucket.push(s);
  }

  const ordered: OrderedPhase[] = [];
  for (const name of phaseOrder) {
    const bucket = buckets.get(name);
    if (bucket && bucket.length > 0) {
      ordered.push({ name, index: phaseToIndex.get(name)!, systems: bucket });
    }
  }
  const synthBucket = buckets.get(SYNTHETIC_PHASE);
  if (synthBucket && synthBucket.length > 0) {
    ordered.push({ name: SYNTHETIC_PHASE, index: -1, systems: synthBucket });
  }
  return ordered;
}

function intraPhaseEdgesOnly(derived: DerivedEdge[], orderedPhases: OrderedPhase[]): DagEdge[] {
  const phaseOf = new Map<string, string>();
  for (const phase of orderedPhases) {
    for (const s of phase.systems) {
      if (s.name) phaseOf.set(s.name, phase.name);
    }
  }
  const edges: DagEdge[] = [];
  for (const d of derived) {
    if (phaseOf.get(d.source) !== phaseOf.get(d.target)) continue;
    edges.push(toReactFlowEdge(d));
  }
  return edges;
}

// ── horizontal-lanes (default per `09-system-dag.md §4.1`) ───────────────

function layoutHorizontalLanes(topology: ResolvedTopology): DagModel {
  const derived = deriveEdges(topology.systems);
  const orderedPhases = bucketByPhase(topology);

  const nodes: DagNode[] = [];
  const lanes: PhaseLane[] = [];
  let yCursor = 0;
  let maxWidth = 0;

  for (const phase of orderedPhases) {
    const phaseSystemNames = new Set(phase.systems.map((s) => s.name).filter((n): n is string => !!n));
    const phaseEdges = derived.filter((e) => phaseSystemNames.has(e.source) && phaseSystemNames.has(e.target));
    const layout = layoutPhase(phase.systems, phaseEdges, 'LR');

    const xOffset = LANE_LABEL_WIDTH + LANE_PADDING;
    const yOffset = yCursor + LANE_PADDING;
    for (const node of layout.nodes) {
      nodes.push({ ...node, position: { x: node.position.x + xOffset, y: node.position.y + yOffset } });
    }

    const laneHeight = layout.height + LANE_PADDING * 2;
    const laneWidth = LANE_LABEL_WIDTH + LANE_PADDING * 2 + layout.width;
    lanes.push({
      name: phase.name,
      index: phase.index,
      systemCount: phase.systems.length,
      xLeft: 0,
      yTop: yCursor,
      height: laneHeight,
      width: laneWidth,
      labelEdge: 'left',
    });

    if (laneWidth > maxWidth) maxWidth = laneWidth;
    yCursor += laneHeight + LANE_GAP;
  }

  return {
    nodes,
    edges: intraPhaseEdgesOnly(derived, orderedPhases),
    lanes,
    width: maxWidth,
    height: yCursor > 0 ? yCursor - LANE_GAP : 0,
  };
}

// ── vertical-lanes ───────────────────────────────────────────────────────

function layoutVerticalLanes(topology: ResolvedTopology): DagModel {
  const derived = deriveEdges(topology.systems);
  const orderedPhases = bucketByPhase(topology);

  const nodes: DagNode[] = [];
  const lanes: PhaseLane[] = [];
  let xCursor = 0;
  let maxHeight = 0;

  for (const phase of orderedPhases) {
    const phaseSystemNames = new Set(phase.systems.map((s) => s.name).filter((n): n is string => !!n));
    const phaseEdges = derived.filter((e) => phaseSystemNames.has(e.source) && phaseSystemNames.has(e.target));
    const layout = layoutPhase(phase.systems, phaseEdges, 'TB');

    const xOffset = xCursor + LANE_PADDING;
    const yOffset = LANE_LABEL_HEIGHT + LANE_PADDING;
    for (const node of layout.nodes) {
      nodes.push({ ...node, position: { x: node.position.x + xOffset, y: node.position.y + yOffset } });
    }

    const laneWidth = layout.width + LANE_PADDING * 2;
    const laneHeight = LANE_LABEL_HEIGHT + LANE_PADDING * 2 + layout.height;
    lanes.push({
      name: phase.name,
      index: phase.index,
      systemCount: phase.systems.length,
      xLeft: xCursor,
      yTop: 0,
      height: laneHeight,
      width: laneWidth,
      labelEdge: 'top',
    });

    if (laneHeight > maxHeight) maxHeight = laneHeight;
    xCursor += laneWidth + LANE_GAP;
  }

  return {
    nodes,
    edges: intraPhaseEdgesOnly(derived, orderedPhases),
    lanes,
    width: xCursor > 0 ? xCursor - LANE_GAP : 0,
    height: maxHeight,
  };
}

// ── compact (flat dagre, no swim-lanes, cross-phase edges visible) ───────

function layoutCompact(topology: ResolvedTopology): DagModel {
  const derived = deriveEdges(topology.systems);
  const layout = layoutPhase(topology.systems, derived, 'LR');

  // No lanes; cross-phase edges are kept (the user explicitly opted out of the swim-lane contract).
  const edges: DagEdge[] = [];
  for (const d of derived) edges.push(toReactFlowEdge(d));

  return {
    nodes: layout.nodes,
    edges,
    lanes: [],
    width: layout.width,
    height: layout.height,
  };
}

// ── circular ─────────────────────────────────────────────────────────────

function layoutCircular(topology: ResolvedTopology): DagModel {
  const derived = deriveEdges(topology.systems);

  // Order systems by phase (declared order), then by name within phase. Without phase data we
  // fall back to plain name order — works for non-RFC-07 traces.
  const orderedPhases = bucketByPhase(topology);
  const ordered: SystemDefinitionDto[] = [];
  for (const phase of orderedPhases) {
    const sortedNames = phase.systems
      .filter((s): s is SystemDefinitionDto & { name: string } => !!s.name)
      .sort((a, b) => a.name.localeCompare(b.name));
    for (const s of sortedNames) ordered.push(s);
  }
  const n = ordered.length;
  if (n === 0) {
    return { nodes: [], edges: [], lanes: [], width: 0, height: 0 };
  }

  // Radius scales so the circumference accommodates n tiles with a comfortable gap. The minimum
  // radius prevents tiny circles for trivial topologies.
  const tileSpacing = NODE_WIDTH + 60;
  const minRadius = NODE_WIDTH * 1.5;
  const radius = Math.max(minRadius, (tileSpacing * n) / (2 * Math.PI));
  const cx = radius + NODE_WIDTH / 2;
  const cy = radius + NODE_HEIGHT / 2;

  const nodes: DagNode[] = [];
  for (let i = 0; i < n; i++) {
    const s = ordered[i];
    if (!s.name) continue;
    // Start at the top (-π/2) and walk clockwise.
    const theta = -Math.PI / 2 + (2 * Math.PI * i) / n;
    const x = cx + radius * Math.cos(theta) - NODE_WIDTH / 2;
    const y = cy + radius * Math.sin(theta) - NODE_HEIGHT / 2;
    nodes.push({
      id: s.name,
      type: 'system',
      position: { x, y },
      data: toNodeData(s),
    });
  }

  // All edges visible (cross-phase included) — the circle has no phase contract.
  const edges: DagEdge[] = [];
  for (const d of derived) edges.push(toReactFlowEdge(d));

  const total = 2 * radius + NODE_WIDTH;
  return { nodes, edges, lanes: [], width: total, height: total };
}

// ── shared dagre helper ──────────────────────────────────────────────────

interface PhaseLayoutResult {
  nodes: DagNode[];
  width: number;
  height: number;
}

function layoutPhase(
  systems: SystemDefinitionDto[],
  edges: DerivedEdge[],
  rankdir: 'LR' | 'TB',
): PhaseLayoutResult {
  const g = new dagre.graphlib.Graph();
  g.setGraph({ rankdir, ranksep: 80, nodesep: 30, marginx: 0, marginy: 0 });
  g.setDefaultEdgeLabel(() => ({}));

  for (const s of systems) {
    if (!s.name) continue;
    g.setNode(s.name, { width: NODE_WIDTH, height: NODE_HEIGHT });
  }
  for (const e of edges) {
    g.setEdge(e.source, e.target);
  }
  dagre.layout(g);

  const nodes: DagNode[] = [];
  let maxX = 0;
  let maxY = 0;
  for (const s of systems) {
    if (!s.name) continue;
    const node = g.node(s.name);
    if (!node) continue;
    const x = node.x - NODE_WIDTH / 2;
    const y = node.y - NODE_HEIGHT / 2;
    nodes.push({
      id: s.name,
      type: 'system',
      position: { x, y },
      data: toNodeData(s),
    });
    if (x + NODE_WIDTH > maxX) maxX = x + NODE_WIDTH;
    if (y + NODE_HEIGHT > maxY) maxY = y + NODE_HEIGHT;
  }
  return { nodes, width: maxX, height: maxY };
}

/**
 * Pure transform from a single {@link SystemDefinitionDto} to {@link DagNodeData}. Exported so
 * panels can resolve node-shaped data for a specific system (e.g. side panel on selection)
 * **without** rebuilding the whole DAG layout, which is O(systems × edges) per dagre call.
 */
export function toNodeData(s: SystemDefinitionDto): DagNodeData {
  const access = (
    (s.reads?.length ?? 0)
    + (s.readsFresh?.length ?? 0)
    + (s.readsSnapshot?.length ?? 0)
    + (s.writes?.length ?? 0)
    + (s.sideWrites?.length ?? 0)
    + (s.writesEvents?.length ?? 0)
    + (s.readsEvents?.length ?? 0)
    + (s.writesResources?.length ?? 0)
    + (s.readsResources?.length ?? 0)
  );
  return {
    systemName: s.name ?? '',
    kind: kindFromByte(s.type),
    phaseName: s.phaseName ?? '',
    isParallel: s.isParallel,
    isExclusivePhase: s.isExclusivePhase,
    tierFilter: typeof s.tierFilter === 'number' ? s.tierFilter : Number(s.tierFilter),
    reads: s.reads ?? [],
    readsFresh: s.readsFresh ?? [],
    readsSnapshot: s.readsSnapshot ?? [],
    writes: s.writes ?? [],
    sideWrites: s.sideWrites ?? [],
    changeFilterTypes: [], // not surfaced through topology DTO yet — placeholder
    hasAccess: access > 0,
  };
}

function kindFromByte(type: number | string): DagNodeData['kind'] {
  const n = typeof type === 'number' ? type : Number(type);
  switch (n) {
    case 0:
      return 'Pipeline';
    case 1:
      return 'Query';
    case 2:
      return 'Callback';
    default:
      return 'Unknown';
  }
}

function toReactFlowEdge(d: DerivedEdge): DagEdge {
  const style = edgeStyle(d.kind);
  return {
    id: d.id,
    source: d.source,
    target: d.target,
    type: style.type,
    label: d.via.length > 1 ? `${d.via[0]} +${d.via.length - 1}` : d.via[0],
    labelStyle: { fontSize: 10, fill: style.colour, fontFamily: 'monospace' },
    labelBgStyle: { fill: 'var(--background)', fillOpacity: 0.85 },
    style: { stroke: style.colour, strokeDasharray: style.dasharray },
    animated: false,
    data: {
      kind: d.kind,
      via: d.via,
      reason: d.reason,
    },
  };
}

interface EdgeStyle {
  colour: string;
  dasharray: string | undefined;
  type: 'default' | 'smoothstep';
}

function edgeStyle(kind: DerivedEdgeKind): EdgeStyle {
  switch (kind) {
    case 'fresh':
      return { colour: '#f59e0b', dasharray: undefined, type: 'smoothstep' }; // amber/orange
    case 'snapshot':
      return { colour: '#3b82f6', dasharray: undefined, type: 'smoothstep' }; // blue
    case 'manual':
      return { colour: '#94a3b8', dasharray: undefined, type: 'smoothstep' }; // slate
    case 'event':
      return { colour: '#a78bfa', dasharray: '6 4', type: 'smoothstep' }; // violet, dashed
    case 'resource':
      return { colour: '#ef4444', dasharray: '2 4', type: 'smoothstep' }; // red, dotted
  }
}
