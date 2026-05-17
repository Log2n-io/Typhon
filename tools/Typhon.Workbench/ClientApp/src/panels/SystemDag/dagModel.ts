import dagre from 'dagre';
import type { Edge, Node } from '@xyflow/react';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import type { TrackDto } from '@/api/generated/model/trackDto';
import { deriveEdges, type DerivedEdge, type DerivedEdgeKind } from '@/lib/dag/edgeDerivation';
import type { LayoutMode } from './useDagViewStore';

/**
 * DAG model — pure transform from topology DTO → React Flow nodes/edges.
 *
 * Since #354 the model is grouped by the runtime partitioning hierarchy
 * `Track → DAG → Phase → System`: lanes are emitted in track-order → DAG-order → DAG-local
 * phase-order, and each DAG is delimited by a {@link DagGroup} box. Phases are DAG-local — two
 * DAGs may legitimately carry a phase of the same name, so lanes are keyed by `${dagId}::${phase}`,
 * never by phase name alone.
 *
 * Four layout strategies are supported (see {@link LayoutMode}):
 *
 * - `horizontal-lanes` (default per `09-system-dag.md §4.1`): phases stack vertically as swim-
 *   lanes; systems flow LR within each phase via dagre. DAGs stack with a wider gap + header.
 * - `vertical-lanes`: phases as side-by-side columns; systems flow TB within each phase.
 * - `compact`: flat dagre LR over all systems; lanes are not rendered AND cross-phase edges are
 *   surfaced (the user explicitly opted out of the swim-lane contract).
 * - `circular`: systems on a single circle, ordered by track → DAG → phase, then by name.
 *
 * Systems whose DAG belongs to an `engine`-tagged track are dropped unless
 * {@link BuildDagModelOptions.showEngineTracks} is set. Systems whose phase isn't in their DAG's
 * declared phase list fall into a synthetic `(unphased)` lane at the end of that DAG.
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
  // Event queues this system reads from / writes to. Surfaced as separate sections in the side
  // panel so the user can see "this system produces AntDied; that system consumes AntDied"
  // without having to read the edge labels.
  readsEvents: string[];
  writesEvents: string[];
  // Named resources this system reads / writes. Same rationale as events — the topology DTO
  // already carries them; the previous side panel just didn't surface them. Resources have no
  // Fresh/Snapshot variant so each side is a single list.
  readsResources: string[];
  writesResources: string[];
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
  /** Unique lane key — `${dagId}::${name}`. Phase names are DAG-local, so a name alone collides. */
  id: string;
  /** Phase name, or `(unphased)` for the fallback lane. */
  name: string;
  /** DAG-local phase index — drives the lane colour (-1 for `(unphased)`). */
  index: number;
  /** Flat global id of the DAG this lane belongs to. */
  dagId: number;
  /** Name of the owning DAG. */
  dagName: string;
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

/**
 * Bounding box delimiting one DAG — the union of its phase lanes plus a header strip carrying the
 * DAG name. Emitted only by the swim-lane layouts, and only when the topology carries a real
 * Track → DAG hierarchy (an un-named synthetic group produces no box).
 */
export interface DagGroup {
  dagId: number;
  dagName: string;
  /** Owning track name — shown alongside the DAG name in the header. */
  trackName: string;
  /** True when the owning track carries the `engine` tag. */
  isEngine: boolean;
  xLeft: number;
  yTop: number;
  width: number;
  height: number;
  /** Header-strip height reserved above the lanes for the DAG label (0 for an un-named group). */
  headerHeight: number;
}

export interface DagModel {
  nodes: DagNode[];
  edges: DagEdge[];
  lanes: PhaseLane[];
  /** DAG delimiter boxes — empty for `compact` / `circular` and for track-less topologies. */
  dagGroups: DagGroup[];
  /** Total bounding-box width — useful for sizing the lane background tiles. */
  width: number;
  /** Total bounding-box height. */
  height: number;
}

/** Tile dimensions used by the dagre layout and the React-Flow renderer. */
export const NODE_WIDTH = 180;
export const NODE_HEIGHT = 56;

/** Vertical gap between phase lanes within the same DAG. */
export const LANE_GAP = 32;
/** Padding inside a lane (top + bottom + left). The lane label sits in the left margin. */
export const LANE_PADDING = 16;
/** Gap between two DAG groups — wider than {@link LANE_GAP} so the grouping reads at a glance. */
export const DAG_GROUP_GAP = 72;
/** Header strip reserved above a DAG group's lanes for the DAG-name label. */
export const DAG_HEADER_HEIGHT = 26;
const LANE_LABEL_WIDTH = 160;
/** Vertical space reserved above the systems in a vertical lane for the phase label. */
const LANE_LABEL_HEIGHT = 28;

const SYNTHETIC_PHASE = '(unphased)';
/** Track tag marking engine-internal tracks — hidden by default in the System DAG. */
const ENGINE_TAG = 'engine';

/** Coerce an orval `number | string` scalar (integer DTO fields carry a string-union type). */
function numOf(v: number | string): number {
  return typeof v === 'number' ? v : Number(v);
}

/** Narrowed topology — `systems`, `phases` and `tracks` are guaranteed non-null after the buildDagModel guard. */
interface ResolvedTopology {
  systems: SystemDefinitionDto[];
  phases: string[];
  tracks: TrackDto[];
}

/**
 * Toggleable model-building options. None of these affect the topology DTO itself; they only
 * change how nodes/edges are filtered before handing them to the layout engine.
 */
export interface BuildDagModelOptions {
  /**
   * Default <code>false</code>. When <code>true</code>, swim-lane layouts (horizontal-lanes,
   * vertical-lanes) include edges whose endpoints sit in different phases. Compact / circular
   * layouts always show every edge — this flag is a no-op there.
   */
  showCrossPhaseEdges?: boolean;
  /**
   * Default <code>false</code>. When <code>false</code>, systems whose DAG belongs to an
   * `engine`-tagged track (Engine-Pre, Engine-Post / Fence) are dropped from the model entirely —
   * no nodes, no lanes, no edges. When <code>true</code>, those DAGs render as their own groups.
   */
  showEngineTracks?: boolean;
}

const EMPTY_MODEL: DagModel = { nodes: [], edges: [], lanes: [], dagGroups: [], width: 0, height: 0 };

/**
 * Build the full model for an entire topology DTO. Pure function — no React, no DOM.
 *
 * `layout` defaults to `'horizontal-lanes'` so existing call sites and tests don't change shape.
 */
export function buildDagModel(
  topology: TopologyDto | null | undefined,
  layout: LayoutMode = 'horizontal-lanes',
  options: BuildDagModelOptions = {},
): DagModel {
  if (!topology || !topology.systems || topology.systems.length === 0) {
    return EMPTY_MODEL;
  }

  const tracks = topology.tracks ?? [];
  const showEngineTracks = options.showEngineTracks === true;

  // Drop engine-track systems up front (unless revealed) so deriveEdges + every layout works on
  // the surviving set — no orphan edges, no empty engine lanes.
  let systems = topology.systems;
  if (!showEngineTracks && tracks.length > 0) {
    const engineDagIds = engineDagIdSet(tracks);
    if (engineDagIds.size > 0) {
      const surviving = systems.filter((s) => !engineDagIds.has(numOf(s.dagId)));
      if (surviving.length !== systems.length) {
        systems = surviving;
      }
    }
  }
  if (systems.length === 0) {
    return EMPTY_MODEL;
  }

  const resolved: ResolvedTopology = {
    systems,
    phases: topology.phases ?? [],
    tracks,
  };

  const showCrossPhaseEdges = options.showCrossPhaseEdges === true;

  switch (layout) {
    case 'horizontal-lanes':
      return layoutHorizontalLanes(resolved, showCrossPhaseEdges);
    case 'vertical-lanes':
      return layoutVerticalLanes(resolved, showCrossPhaseEdges);
    case 'compact':
      return layoutCompact(resolved);
    case 'circular':
      return layoutCircular(resolved);
  }
}

// ── Track / DAG / Phase bucketing ────────────────────────────────────────

interface OrderedPhase {
  /** Unique lane key — `${dagId}::${name}`. */
  id: string;
  name: string;
  index: number;
  dagId: number;
  dagName: string;
  systems: SystemDefinitionDto[];
}

interface OrderedDag {
  dagId: number;
  dagName: string;
  trackName: string;
  isEngine: boolean;
  phases: OrderedPhase[];
}

/** Flat global ids of every DAG whose owning track carries the `engine` tag. */
function engineDagIdSet(tracks: TrackDto[]): Set<number> {
  const ids = new Set<number>();
  for (const t of tracks) {
    if (!(t.tags ?? []).includes(ENGINE_TAG)) continue;
    for (const d of t.dags ?? []) {
      ids.add(numOf(d.id));
    }
  }
  return ids;
}

/**
 * Bucket systems into the `Track → DAG → Phase` hierarchy. Tracks are taken in
 * {@link TrackDto.orderIndex} order, DAGs in declaration order, phases in DAG-local order; empty
 * phases and empty DAGs are dropped. A system whose `phaseName` isn't declared by its DAG lands in
 * that DAG's synthetic `(unphased)` lane.
 *
 * Falls back to a single un-named group bucketed by the flat `topology.phases` list when the
 * topology carries no Track → DAG hierarchy (track-less traces).
 */
function bucketByTrackDagPhase(topology: ResolvedTopology): OrderedDag[] {
  if (topology.tracks.length === 0) {
    return [fallbackGroup(topology)];
  }

  // dagId → systems, in topology order.
  const systemsByDag = new Map<number, SystemDefinitionDto[]>();
  for (const s of topology.systems) {
    const dagId = numOf(s.dagId);
    let bucket = systemsByDag.get(dagId);
    if (!bucket) {
      bucket = [];
      systemsByDag.set(dagId, bucket);
    }
    bucket.push(s);
  }

  const tracks = [...topology.tracks].sort((a, b) => numOf(a.orderIndex) - numOf(b.orderIndex));
  const groups: OrderedDag[] = [];
  for (const track of tracks) {
    const isEngine = (track.tags ?? []).includes(ENGINE_TAG);
    for (const dag of track.dags ?? []) {
      const dagId = numOf(dag.id);
      const dagSystems = systemsByDag.get(dagId);
      if (!dagSystems || dagSystems.length === 0) continue;

      const phases = bucketDagPhases(dagId, dag.name ?? '', dag.phases ?? [], dagSystems);
      if (phases.length === 0) continue;
      groups.push({
        dagId,
        dagName: dag.name ?? '',
        trackName: track.name ?? '',
        isEngine,
        phases,
      });
    }
  }

  // Defensive: a topology with tracks but whose systems reference unknown DAG ids would yield no
  // groups — fall back so the canvas still shows something rather than an empty panel.
  return groups.length > 0 ? groups : [fallbackGroup(topology)];
}

/** Bucket one DAG's systems into its DAG-local phases (plus a synthetic trailing lane). */
function bucketDagPhases(
  dagId: number,
  dagName: string,
  phaseNames: string[],
  dagSystems: SystemDefinitionDto[],
): OrderedPhase[] {
  const phaseOrder = phaseNames.filter((p): p is string => !!p);
  const phaseToIndex = new Map<string, number>();
  phaseOrder.forEach((p, i) => phaseToIndex.set(p, i));

  const buckets = new Map<string, SystemDefinitionDto[]>();
  for (const s of dagSystems) {
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
      ordered.push({ id: `${dagId}::${name}`, name, index: phaseToIndex.get(name)!, dagId, dagName, systems: bucket });
    }
  }
  const synthBucket = buckets.get(SYNTHETIC_PHASE);
  if (synthBucket && synthBucket.length > 0) {
    ordered.push({ id: `${dagId}::${SYNTHETIC_PHASE}`, name: SYNTHETIC_PHASE, index: -1, dagId, dagName, systems: synthBucket });
  }
  return ordered;
}

/** Single un-named group bucketed by the flat phase list — the track-less fallback. */
function fallbackGroup(topology: ResolvedTopology): OrderedDag {
  const phases = bucketDagPhases(0, '', topology.phases, topology.systems);
  return { dagId: 0, dagName: '', trackName: '', isEngine: false, phases };
}

/** Every phase lane across all groups, flattened — convenient for edge / colour lookups. */
function allPhases(groups: OrderedDag[]): OrderedPhase[] {
  const out: OrderedPhase[] = [];
  for (const g of groups) {
    for (const p of g.phases) out.push(p);
  }
  return out;
}

function intraLaneEdgesOnly(derived: DerivedEdge[], phases: OrderedPhase[]): DagEdge[] {
  const laneOf = new Map<string, string>();
  for (const phase of phases) {
    for (const s of phase.systems) {
      if (s.name) laneOf.set(s.name, phase.id);
    }
  }
  const edges: DagEdge[] = [];
  for (const d of derived) {
    if (laneOf.get(d.source) !== laneOf.get(d.target)) continue;
    edges.push(toReactFlowEdge(d));
  }
  return edges;
}

/**
 * All derived edges as React-Flow edges, including ones that span lanes. Used by the lane
 * layouts only when the user opts into cross-phase visibility — otherwise the lane order
 * suffices and the cross-lane chain is suppressed (see {@link intraLaneEdgesOnly}).
 */
function allEdges(derived: DerivedEdge[]): DagEdge[] {
  return derived.map(toReactFlowEdge);
}

// ── horizontal-lanes (default per `09-system-dag.md §4.1`) ───────────────

function layoutHorizontalLanes(topology: ResolvedTopology, showCrossPhaseEdges: boolean): DagModel {
  const derived = deriveEdges(topology.systems);
  const groups = bucketByTrackDagPhase(topology);

  const nodes: DagNode[] = [];
  const lanes: PhaseLane[] = [];
  const dagGroups: DagGroup[] = [];
  let yCursor = 0;
  let maxWidth = 0;

  for (const group of groups) {
    const headerHeight = group.dagName ? DAG_HEADER_HEIGHT : 0;
    const groupTop = yCursor;
    yCursor += headerHeight;

    for (const phase of group.phases) {
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
        id: phase.id,
        name: phase.name,
        index: phase.index,
        dagId: phase.dagId,
        dagName: phase.dagName,
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

    const groupBottom = yCursor - LANE_GAP;
    if (group.dagName) {
      dagGroups.push({
        dagId: group.dagId,
        dagName: group.dagName,
        trackName: group.trackName,
        isEngine: group.isEngine,
        xLeft: 0,
        yTop: groupTop,
        width: 0, // patched to maxWidth below
        height: groupBottom - groupTop,
        headerHeight,
      });
    }
    yCursor += DAG_GROUP_GAP;
  }

  for (const g of dagGroups) g.width = maxWidth;

  return {
    nodes,
    edges: showCrossPhaseEdges ? allEdges(derived) : intraLaneEdgesOnly(derived, allPhases(groups)),
    lanes,
    dagGroups,
    width: maxWidth,
    height: yCursor > 0 ? yCursor - DAG_GROUP_GAP : 0,
  };
}

// ── vertical-lanes ───────────────────────────────────────────────────────

function layoutVerticalLanes(topology: ResolvedTopology, showCrossPhaseEdges: boolean): DagModel {
  const derived = deriveEdges(topology.systems);
  const groups = bucketByTrackDagPhase(topology);

  const nodes: DagNode[] = [];
  const lanes: PhaseLane[] = [];
  const dagGroups: DagGroup[] = [];
  let xCursor = 0;
  let maxHeight = 0;

  for (const group of groups) {
    const headerHeight = group.dagName ? DAG_HEADER_HEIGHT : 0;
    const groupLeft = xCursor;

    for (const phase of group.phases) {
      const phaseSystemNames = new Set(phase.systems.map((s) => s.name).filter((n): n is string => !!n));
      const phaseEdges = derived.filter((e) => phaseSystemNames.has(e.source) && phaseSystemNames.has(e.target));
      const layout = layoutPhase(phase.systems, phaseEdges, 'TB');

      const xOffset = xCursor + LANE_PADDING;
      const yOffset = headerHeight + LANE_LABEL_HEIGHT + LANE_PADDING;
      for (const node of layout.nodes) {
        nodes.push({ ...node, position: { x: node.position.x + xOffset, y: node.position.y + yOffset } });
      }

      const laneWidth = layout.width + LANE_PADDING * 2;
      const laneHeight = headerHeight + LANE_LABEL_HEIGHT + LANE_PADDING * 2 + layout.height;
      lanes.push({
        id: phase.id,
        name: phase.name,
        index: phase.index,
        dagId: phase.dagId,
        dagName: phase.dagName,
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

    const groupRight = xCursor - LANE_GAP;
    if (group.dagName) {
      dagGroups.push({
        dagId: group.dagId,
        dagName: group.dagName,
        trackName: group.trackName,
        isEngine: group.isEngine,
        xLeft: groupLeft,
        yTop: 0,
        width: groupRight - groupLeft,
        height: 0, // patched to maxHeight below
        headerHeight,
      });
    }
    xCursor += DAG_GROUP_GAP;
  }

  for (const g of dagGroups) g.height = maxHeight;

  return {
    nodes,
    edges: showCrossPhaseEdges ? allEdges(derived) : intraLaneEdgesOnly(derived, allPhases(groups)),
    lanes,
    dagGroups,
    width: xCursor > 0 ? xCursor - DAG_GROUP_GAP : 0,
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
    dagGroups: [],
    width: layout.width,
    height: layout.height,
  };
}

// ── circular ─────────────────────────────────────────────────────────────

function layoutCircular(topology: ResolvedTopology): DagModel {
  const derived = deriveEdges(topology.systems);

  // Order systems by track → DAG → phase (declared order), then by name within phase. Without a
  // hierarchy we fall back to flat phase order — works for track-less traces.
  const groups = bucketByTrackDagPhase(topology);
  const ordered: SystemDefinitionDto[] = [];
  for (const phase of allPhases(groups)) {
    const sortedNames = phase.systems
      .filter((s): s is SystemDefinitionDto & { name: string } => !!s.name)
      .sort((a, b) => a.name.localeCompare(b.name));
    for (const s of sortedNames) ordered.push(s);
  }
  const n = ordered.length;
  if (n === 0) {
    return EMPTY_MODEL;
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
      width: NODE_WIDTH,
      height: NODE_HEIGHT,
      data: toNodeData(s),
    });
  }

  // All edges visible (cross-phase included) — the circle has no phase contract.
  const edges: DagEdge[] = [];
  for (const d of derived) edges.push(toReactFlowEdge(d));

  const total = 2 * radius + NODE_WIDTH;
  return { nodes, edges, lanes: [], dagGroups: [], width: total, height: total };
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
      width: NODE_WIDTH,
      height: NODE_HEIGHT,
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
    readsEvents: s.readsEvents ?? [],
    writesEvents: s.writesEvents ?? [],
    readsResources: s.readsResources ?? [],
    writesResources: s.writesResources ?? [],
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
