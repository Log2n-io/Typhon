import { Handle, Position } from '@xyflow/react';
import type { QueryPlanNodeData } from './queryPlanLayout';
import { formatNs } from './format';

/**
 * Custom node components for the plan tree. One component per <c>QueryPlanNodeKind</c>; React Flow
 * picks the component via the node's <c>type</c> field (set in {@link buildQueryPlanGraph}).
 *
 * <para>Each node carries the same set of fields (<c>title</c>, <c>detail</c>, optional <c>stats</c>);
 * only the accent color and icon differ. The structural detail line is always rendered; the stats
 * footer appears when execution-mode data is present.</para>
 */

type NodeProps = { data: QueryPlanNodeData };

const NODE_BASE = 'rounded border bg-card text-card-foreground shadow-sm transition-shadow w-[220px]';

// Theme-paired accents — light-theme bg counterparts mirror the SystemDagNode chip palette so the
// nodes render with a visible tint in both themes (dark-only `dark:bg-X-950/30` left the light theme
// rendering as a plain card with only the colored border).
export function IndexScanNode({ data }: NodeProps) {
  return <PlanNodeShell data={data} accent="border-sky-500/60 bg-sky-100 dark:bg-sky-950/30" badge="scan" />;
}
export function FilterNode({ data }: NodeProps) {
  return <PlanNodeShell data={data} accent="border-amber-500/60 bg-amber-100 dark:bg-amber-950/30" badge="filter" />;
}
export function SortNode({ data }: NodeProps) {
  return <PlanNodeShell data={data} accent="border-purple-500/60 bg-purple-100 dark:bg-purple-950/30" badge="sort" />;
}
export function PaginationNode({ data }: NodeProps) {
  return <PlanNodeShell data={data} accent="border-emerald-500/60 bg-emerald-100 dark:bg-emerald-950/30" badge="paginate" />;
}
export function ResultNode({ data }: NodeProps) {
  return <PlanNodeShell data={data} accent="border-border bg-muted/40 dark:bg-muted/30" badge="result" />;
}

function PlanNodeShell({ data, accent, badge }: { data: QueryPlanNodeData; accent: string; badge: string }) {
  return (
    <div className={`${NODE_BASE} ${accent}`}>
      <Handle type="target" position={Position.Top} className="opacity-40" />
      <div className="flex items-center justify-between border-b border-border/40 px-2.5 py-1">
        <span className="font-semibold text-fs-base">{data.title}</span>
        <span className="text-fs-2xs uppercase tracking-wider text-muted-foreground">{badge}</span>
      </div>
      <div className="px-2.5 py-1.5">
        <div className="truncate font-mono text-fs-sm text-foreground">{data.detail}</div>
        {data.stats && <StatsFooter stats={data.stats} />}
      </div>
      <Handle type="source" position={Position.Bottom} className="opacity-40" />
    </div>
  );
}

function StatsFooter({ stats }: { stats: NonNullable<QueryPlanNodeData['stats']> }) {
  const parts: string[] = [];
  if (stats.wallNs != null) parts.push(`${formatNs(stats.wallNs)}`);
  if (stats.estimate != null && stats.actual != null) {
    parts.push(`${stats.actual.toLocaleString()} / ${stats.estimate.toLocaleString()}`);
  } else if (stats.actual != null) {
    parts.push(`${stats.actual.toLocaleString()} rows`);
  }
  if (stats.notes) parts.push(stats.notes);
  if (parts.length === 0) return null;
  return (
    <div className="mt-1 truncate text-fs-xs text-muted-foreground">{parts.join(' · ')}</div>
  );
}
