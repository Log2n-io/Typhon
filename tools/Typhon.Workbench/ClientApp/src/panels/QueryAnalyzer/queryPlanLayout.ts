import dagre from 'dagre';
import type { Node, Edge } from '@xyflow/react';
import type { QueryDefinitionDto } from '@/api/generated/model/queryDefinitionDto';
import type { QueryExecutionDto } from '@/api/generated/model/queryExecutionDto';
import type { QueryExecutionPhaseDto } from '@/api/generated/model/queryExecutionPhaseDto';
import { toNumber } from './numeric';

export type QueryPlanNodeKind = 'IndexScan' | 'Filter' | 'Sort' | 'Pagination' | 'Result';

export interface QueryPlanNodeData extends Record<string, unknown> {
  kind: QueryPlanNodeKind;
  /** Short label displayed in the node header. */
  title: string;
  /** Primary structural detail (field name, op, threshold, etc.). */
  detail: string;
  /** Execution-mode stats — populated only when the layout is built with a matching execution. */
  stats?: QueryPlanNodeStats;
}

export interface QueryPlanNodeStats {
  /** Wall-time in nanoseconds for this plan step in the execution. */
  wallNs?: number;
  /** Optimizer's estimated row count for this step. */
  estimate?: number;
  /** Actual row count produced by this step. */
  actual?: number;
  /** Free-form note (e.g., "early-term" for pagination). */
  notes?: string;
}

export type QueryPlanNode = Node<QueryPlanNodeData>;

/** Tile dimensions consumed by the dagre layout. Kept here so tests, layout and renderer agree. */
export const NODE_WIDTH = 220;
export const NODE_HEIGHT = 76;

/** Resolve archetype id → display string. Pass through unchanged when the lookup misses. */
export type ArchetypeLookup = (id: number) => string;

/**
 * Build the React Flow plan graph from a query definition. When `execution` is supplied, the per-node
 * stats are populated by matching the definition's structural step against the execution's
 * <c>phases[]</c> by name (case-insensitive). Pure function — no React, no DOM — testable in isolation.
 *
 * <para>The structural layout is a linear pipeline: <c>IndexScan?</c> → <c>Filter*</c> → <c>Sort?</c>
 * → <c>Result</c>. Pagination is omitted in this first cut because the DTO surface does not yet carry
 * skip/take limits — when it does, add the node between <c>Sort</c> and <c>Result</c>.</para>
 */
export function buildQueryPlanGraph(
  def: QueryDefinitionDto,
  execution: QueryExecutionDto | null,
  archetypeName?: ArchetypeLookup,
): { nodes: QueryPlanNode[]; edges: Edge[] } {
  const nodes: QueryPlanNode[] = [];
  const edges: Edge[] = [];
  const phaseLookup = buildPhaseLookup(execution);

  const archetypeId = toNumber(def.targetComponentType);
  const archetypeLabel = archetypeName ? archetypeName(archetypeId) : `Component[${archetypeId}]`;

  const primaryIdx = toNumber(def.primaryIndexFieldIdx);
  if (primaryIdx >= 0) {
    const evalForPrimary = (def.evaluators ?? []).find((e) => toNumber(e.fieldIdx) === primaryIdx);
    const fieldName = evalForPrimary?.fieldName ?? `Field[${primaryIdx}]`;
    pushNode(nodes, {
      id: 'index-scan',
      kind: 'IndexScan',
      title: 'Index Scan',
      detail: `${archetypeLabel}.${fieldName}`,
      stats: phaseLookup.IndexScan,
    });
  } else {
    pushNode(nodes, {
      id: 'full-scan',
      kind: 'IndexScan',
      title: 'Full Scan',
      detail: archetypeLabel,
      stats: phaseLookup.IndexScan,
    });
  }

  const evaluators = def.evaluators ?? [];
  let prevId = nodes[nodes.length - 1].id;
  for (let i = 0; i < evaluators.length; i++) {
    const e = evaluators[i];
    if (toNumber(e.fieldIdx) === primaryIdx && primaryIdx >= 0) {
      continue;
    }
    const nodeId = `filter-${i}`;
    const fieldName = e.fieldName ?? `Field[${toNumber(e.fieldIdx)}]`;
    const op = e.opDisplay ?? `Op[${toNumber(e.op)}]`;
    pushNode(nodes, {
      id: nodeId,
      kind: 'Filter',
      title: 'Filter',
      detail: `${fieldName} ${op}`,
      stats: phaseLookup.Filter,
    });
    edges.push({ id: `e-${prevId}-${nodeId}`, source: prevId, target: nodeId, animated: false });
    prevId = nodeId;
  }

  const sortIdx = toNumber(def.sortFieldIdx);
  if (sortIdx >= 0) {
    const sortEvaluator = (def.evaluators ?? []).find((e) => toNumber(e.fieldIdx) === sortIdx);
    const fieldName = sortEvaluator?.fieldName ?? `Field[${sortIdx}]`;
    const direction = def.sortDescending ? 'DESC' : 'ASC';
    pushNode(nodes, {
      id: 'sort',
      kind: 'Sort',
      title: 'Sort',
      detail: `${fieldName} ${direction}`,
      stats: phaseLookup.Sort,
    });
    edges.push({ id: `e-${prevId}-sort`, source: prevId, target: 'sort', animated: false });
    prevId = 'sort';
  }

  pushNode(nodes, {
    id: 'result',
    kind: 'Result',
    title: 'Result',
    detail: execution
      ? `${formatRowCount(phaseLookup.Result?.actual)} rows`
      : 'final result set',
    stats: phaseLookup.Result,
  });
  edges.push({ id: `e-${prevId}-result`, source: prevId, target: 'result', animated: false });

  return applyDagreLayout({ nodes, edges });
}

/** Apply dagre top-down layout. Exported for tests that want to validate positioning logic. */
export function applyDagreLayout({
  nodes,
  edges,
}: {
  nodes: QueryPlanNode[];
  edges: Edge[];
}): { nodes: QueryPlanNode[]; edges: Edge[] } {
  const g = new dagre.graphlib.Graph();
  g.setGraph({ rankdir: 'TB', ranksep: 56, nodesep: 24, marginx: 24, marginy: 24 });
  g.setDefaultEdgeLabel(() => ({}));

  for (const n of nodes) {
    g.setNode(n.id, { width: NODE_WIDTH, height: NODE_HEIGHT });
  }
  for (const e of edges) {
    g.setEdge(e.source, e.target);
  }
  dagre.layout(g);

  const positioned = nodes.map((n) => {
    const pos = g.node(n.id);
    return {
      ...n,
      position: { x: pos.x - NODE_WIDTH / 2, y: pos.y - NODE_HEIGHT / 2 },
    };
  });
  return { nodes: positioned, edges };
}

interface PhaseLookup {
  IndexScan?: QueryPlanNodeStats;
  Filter?: QueryPlanNodeStats;
  Sort?: QueryPlanNodeStats;
  Pagination?: QueryPlanNodeStats;
  Result?: QueryPlanNodeStats;
}

function buildPhaseLookup(execution: QueryExecutionDto | null): PhaseLookup {
  const out: PhaseLookup = {};
  if (!execution) return out;
  for (const phase of execution.phases ?? []) {
    const slot = classifyPhase(phase);
    if (slot && !out[slot]) {
      out[slot] = phaseToStats(phase);
    }
  }
  return out;
}

function classifyPhase(phase: QueryExecutionPhaseDto): QueryPlanNodeKind | null {
  const name = (phase.phaseName ?? '').toLowerCase();
  if (name.includes('index') || name.includes('scan')) return 'IndexScan';
  if (name.includes('filter')) return 'Filter';
  if (name.includes('sort')) return 'Sort';
  if (name.includes('paginat')) return 'Pagination';
  if (name.includes('result') || name.includes('return')) return 'Result';
  return null;
}

function phaseToStats(phase: QueryExecutionPhaseDto): QueryPlanNodeStats {
  return {
    wallNs: toNumber(phase.wallNs),
    estimate: phase.estimate == null ? undefined : toNumber(phase.estimate),
    actual: phase.actual == null ? undefined : toNumber(phase.actual),
    notes: phase.notes ?? undefined,
  };
}

function pushNode(
  nodes: QueryPlanNode[],
  spec: { id: string; kind: QueryPlanNodeKind; title: string; detail: string; stats?: QueryPlanNodeStats },
): void {
  nodes.push({
    id: spec.id,
    type: spec.kind,
    position: { x: 0, y: 0 },
    data: { kind: spec.kind, title: spec.title, detail: spec.detail, stats: spec.stats },
  });
}

function formatRowCount(n: number | undefined): string {
  if (n == null) return '—';
  return n.toLocaleString();
}
