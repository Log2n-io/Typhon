import { Handle, Position } from '@xyflow/react';
import type { DagNodeData } from './dagModel';
import type { SystemStat } from './useSystemStats';

/** Renders a single system tile with kind chip, primary stat (when stats are loaded), CP-rate badge, skip chip, and filter chips. */
export default function SystemDagNode({
  data,
  selected,
}: {
  data: DagNodeData & { stat?: SystemStat | null; cpRate?: number | null; skipRate?: number | null; isOnDominantCp?: boolean; isHovered?: boolean };
  selected?: boolean;
}) {
  const kindClass = kindClasses(data.kind);
  const exclusiveBar = data.isExclusivePhase ? 'border-l-4 border-l-amber-500' : '';
  // Selection wins over hover — once you click the node the primary ring locks in; hover only
  // illuminates when no harder selection is active. Hover comes from the cross-panel store, so
  // hovering a tape bar lights this node and vice-versa.
  const ring = selected
    ? 'ring-2 ring-primary'
    : data.isHovered
      ? 'ring-2 ring-foreground/60'
      : '';
  // Per `09-system-dag.md §11 Phase 3`: nodes on the critical path of the dominant tick get a red
  // outline. We use Tailwind's `outline` (not `border` or `ring`) so it stacks cleanly with the
  // selection ring + heat border without fighting any of them. `outline-offset-1` keeps the red
  // halo visually distinct from the heat colour painted on the actual border.
  const dominantCpOutline = data.isOnDominantCp ? 'outline outline-2 outline-red-500 outline-offset-1' : '';
  const stat = data.stat ?? null;
  const heatStyle = stat ? heatBorder(stat.heat) : undefined;
  const cpRate = data.cpRate ?? null;
  // Per `09-system-dag.md §4.2`: solid ★ at ≥50% CP participation, dimmed at 10–50%, none below.
  const cpBadge = cpRate == null || cpRate < 0.1
    ? null
    : cpRate >= 0.5
      ? { glyph: '★', tone: 'solid' as const, title: `On critical path ${(cpRate * 100).toFixed(0)}% of ticks` }
      : { glyph: '★', tone: 'dim' as const, title: `On critical path ${(cpRate * 100).toFixed(0)}% of ticks` };
  // Per design §4.2: skip-rate chip rendered when skipRate > 50%.
  const skipRate = data.skipRate ?? null;
  const showSkipChip = skipRate != null && skipRate > 0.5;

  return (
    <div
      className={`relative flex h-[56px] w-[180px] flex-col rounded border bg-card shadow-sm ${exclusiveBar} ${ring} ${dominantCpOutline}`}
      style={heatStyle}
      data-testid={`system-dag-node-${data.systemName}`}
      title={data.isOnDominantCp ? 'On the critical path of the dominant tick' : undefined}
    >
      <Handle type="target" position={Position.Left} className="!h-2 !w-2 !border-0 !bg-muted-foreground" />
      <div className="flex items-center justify-between gap-1 px-2 pt-1">
        <span className="truncate font-mono text-[11px] font-semibold text-foreground" title={data.systemName}>
          {data.systemName}
        </span>
        <div className="flex items-center gap-1">
          {cpBadge && (
            <span
              className={`text-[12px] leading-none ${cpBadge.tone === 'solid' ? 'text-amber-300' : 'text-amber-400/40'}`}
              title={cpBadge.title}
            >
              {cpBadge.glyph}
            </span>
          )}
          <span className={`rounded px-1.5 py-0.5 text-[9px] font-semibold uppercase ${kindClass}`}>{data.kind}</span>
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-1 px-2 pb-1 pt-0.5">
        {stat ? (
          <span
            className="rounded border px-1 py-px font-mono text-[10px]"
            style={heatChip(stat.heat)}
            title={`${stat.value.toFixed(1)} µs`}
          >
            {formatStat(stat.value)}
          </span>
        ) : null}
        {showSkipChip && skipRate != null && (
          <Chip tone="muted">↪ {(skipRate * 100).toFixed(0)}%</Chip>
        )}
        {data.isParallel && <Chip>parallel</Chip>}
        {data.isExclusivePhase && <Chip tone="warn">exclusive</Chip>}
        {data.tierFilter !== 0x0F && <Chip tone="muted">tier {data.tierFilter}</Chip>}
        {data.changeFilterTypes.length > 0 && <Chip tone="info">change:{data.changeFilterTypes.length}</Chip>}
        {!data.hasAccess && !stat && <Chip tone="muted">no decls</Chip>}
      </div>
      <Handle type="source" position={Position.Right} className="!h-2 !w-2 !border-0 !bg-muted-foreground" />
    </div>
  );
}

interface ChipProps {
  children: React.ReactNode;
  tone?: 'default' | 'muted' | 'warn' | 'info';
}

function Chip({ children, tone = 'default' }: ChipProps) {
  const cls =
    tone === 'muted'
      ? 'border-border bg-muted/40 text-muted-foreground'
      : tone === 'warn'
        ? 'border-amber-700/50 bg-amber-950/40 text-amber-200'
        : tone === 'info'
          ? 'border-sky-700/50 bg-sky-950/40 text-sky-200'
          : 'border-slate-600/50 bg-slate-900/40 text-slate-200';
  return <span className={`rounded border px-1 py-px text-[9px] font-mono ${cls}`}>{children}</span>;
}

function kindClasses(kind: DagNodeData['kind']): string {
  switch (kind) {
    case 'Pipeline':
      return 'bg-emerald-900/40 text-emerald-200';
    case 'Query':
      return 'bg-sky-900/40 text-sky-200';
    case 'Callback':
      return 'bg-violet-900/40 text-violet-200';
    case 'Unknown':
    default:
      return 'bg-slate-800 text-slate-300';
  }
}

/**
 * Heat ramp: cool (blue, low duration) → hot (red, high duration). The exact gradient is
 * cosmetic — the goal is "scan the canvas, see which systems are hot." Hue interpolates
 * from 220° (blue) → 0° (red); saturation flat 70%; alpha increases with heat so cold
 * tiles stay readable.
 */
function heatBorder(heat: number): React.CSSProperties {
  const hue = 220 - heat * 220; // 220 → 0
  const alpha = 0.4 + heat * 0.5; // 0.4 → 0.9
  return {
    borderColor: `hsla(${hue}, 70%, 55%, ${alpha})`,
    boxShadow: heat > 0.66 ? `0 0 8px hsla(${hue}, 70%, 55%, 0.35)` : undefined,
  };
}

function heatChip(heat: number): React.CSSProperties {
  const hue = 220 - heat * 220;
  return {
    backgroundColor: `hsla(${hue}, 70%, 35%, 0.4)`,
    borderColor: `hsla(${hue}, 70%, 55%, 0.7)`,
    color: `hsla(${hue}, 80%, 88%, 1)`,
  };
}

/** Format µs in a width-stable way: <1ms in µs (e.g. "812µs"), ≥1ms in ms (e.g. "3.2ms"). */
function formatStat(us: number): string {
  if (us < 1000) return `${Math.round(us)}µs`;
  const ms = us / 1000;
  return ms < 10 ? `${ms.toFixed(2)}ms` : `${ms.toFixed(1)}ms`;
}
