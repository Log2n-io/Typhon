import { memo } from 'react';
import { Handle, Position } from '@xyflow/react';
import { Hourglass } from 'lucide-react';
import { useThemeStore } from '@/stores/useThemeStore';
import type { DagNodeData } from './dagModel';
import type { SystemStat } from './useSystemStats';

/** Threshold (µs) above which the per-node "blocked" icon appears. Picked low (10µs) because
 *  the user's interest is "find systems that wait at all" — sub-µs noise is filtered. The
 *  `Hourglass` glyph deliberately mirrors the existing ★ badge placement: same top-right
 *  cluster, distinct shape so the two cues don't blend. */
const BLOCKED_ICON_THRESHOLD_US = 10;

/** Renders a single system tile with kind chip, primary stat (when stats are loaded), CP-rate badge, skip chip, and filter chips. */
function SystemDagNodeInner({
  data,
  selected,
}: {
  data: DagNodeData & {
    stat?: SystemStat | null;
    cpRate?: number | null;
    skipRate?: number | null;
    isOnDominantCp?: boolean;
    isHovered?: boolean;
    waitGapUs?: number | null;
    /** Phase D (#327): node's system touches the currently-selected dataTrack — render with amber halo. */
    isOnSelectedDataTrack?: boolean;
    /** Phase D (#327): node belongs to the currently-selected phase — subtle brightness boost. */
    isOnSelectedPhase?: boolean;
    /** Phase D (#327): cross-panel hover key matches this node's name — bright ring. */
    isHoveredFromCrossPanel?: boolean;
  };
  selected?: boolean;
}) {
  const theme = useThemeStore((s) => s.theme);
  const kindClass = kindClasses(data.kind);
  const exclusiveBar = data.isExclusivePhase ? 'border-l-4 border-l-amber-500' : '';
  // Selection wins over hover — once you click the node the primary ring locks in; hover only
  // illuminates when no harder selection is active. Hover comes from the cross-panel store, so
  // hovering a tape bar lights this node and vice-versa.
  // Selection ring priority (top → bottom):
  // 1. Local DAG selection (clicked here) — primary
  // 2. Cross-panel hover (Data Flow bar) — bright foreground
  // 3. Cross-panel dataTrack — amber halo for "you clicked a track this system touches"
  // 4. Local hover — dim foreground
  // Phase D (#327): adds the cross-panel hover + dataTrack rings on top of the existing selection / hover stack.
  const ring = selected
    ? 'ring-2 ring-primary'
    : data.isHoveredFromCrossPanel
      ? 'ring-2 ring-foreground'
      : data.isOnSelectedDataTrack
        ? 'ring-2 ring-amber-400'
        : data.isHovered
          ? 'ring-2 ring-foreground/60'
          : '';
  // Phase D (#327): subtle brightness boost for nodes whose phase is selected. Stacks on top of every other style.
  const phaseBoost = data.isOnSelectedPhase ? 'brightness-110' : '';
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
  // Hourglass icon when the system has a non-trivial mean dispatch wait — driven by the
  // gating analysis. Subtle by design: same top-right cluster as the ★ badge so the user
  // gets a single "this system has interesting dynamics" line without a busy tile.
  const waitGapUs = data.waitGapUs ?? null;
  const showBlockedIcon = waitGapUs != null && waitGapUs >= BLOCKED_ICON_THRESHOLD_US;

  // Drag ghost: applied via the wrapper-level `style: { opacity: 0.5 }` set on the node in
  // `SystemDagCanvas.styledNodes` rather than via a class here. Reading dragging state from
  // `data` would force this component to re-render on every drag frame (data ref churn) and
  // make React Flow re-measure the handles → flicker on attached edges. Wrapper-level style
  // updates skip the inner render path entirely.
  return (
    <div
      className={`relative flex h-[56px] w-[180px] flex-col rounded border bg-card shadow-sm ${exclusiveBar} ${ring} ${dominantCpOutline} ${phaseBoost}`}
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
          {showBlockedIcon && waitGapUs != null && (
            <Hourglass
              className="h-2.5 w-2.5 text-slate-700 dark:text-slate-300"
              aria-label="Blocked by predecessor"
              data-testid={`system-dag-block-icon-${data.systemName}`}
            >
              <title>{`Mean dispatch wait ${formatStat(waitGapUs)} after ready — open the side panel for the gating predecessor`}</title>
            </Hourglass>
          )}
          {cpBadge && (
            <span
              className={`text-[12px] leading-none ${cpBadge.tone === 'solid'
                ? 'text-amber-700 dark:text-amber-300'
                : 'text-amber-700/50 dark:text-amber-400/40'}`}
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
            style={heatChip(stat.heat, theme)}
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

/**
 * Wrap in React.memo so the tile only re-renders when its `data` / `selected` props actually
 * change identity. Critical during drag: the dragged node's wrapper re-renders every frame to
 * apply the new CSS transform + opacity ghost, but its `data` ref is preserved (the canvas's
 * styledNodes drag patch is `{ ...n, position, style }` — `data` passes through). With memo,
 * the inner tile sees ref-equal props and skips render → handle measurements stay stable →
 * attached edges re-route smoothly without flicker.
 */
export default memo(SystemDagNodeInner);

interface ChipProps {
  children: React.ReactNode;
  tone?: 'default' | 'muted' | 'warn' | 'info';
}

function Chip({ children, tone = 'default' }: ChipProps) {
  // Theme-paired tones — light theme uses *-100/*-800, dark theme uses *-950/40 / *-200.
  // The muted tone is already theme-aware via the shadcn `bg-muted` token, so it's left
  // as-is.
  const cls =
    tone === 'muted'
      ? 'border-border bg-muted/40 text-muted-foreground'
      : tone === 'warn'
        ? 'border-amber-300 bg-amber-100 text-amber-800 dark:border-amber-700/50 dark:bg-amber-950/40 dark:text-amber-200'
        : tone === 'info'
          ? 'border-sky-300 bg-sky-100 text-sky-800 dark:border-sky-700/50 dark:bg-sky-950/40 dark:text-sky-200'
          : 'border-slate-300 bg-slate-100 text-slate-800 dark:border-slate-600/50 dark:bg-slate-900/40 dark:text-slate-200';
  return <span className={`rounded border px-1 py-px text-[9px] font-mono ${cls}`}>{children}</span>;
}

function kindClasses(kind: DagNodeData['kind']): string {
  // PIPELINE / QUERY / CALLBACK / UNKNOWN tile-corner badges. Same dual-class shape as the
  // edge-kind chip in the side panel so the visual vocabulary stays consistent across views.
  switch (kind) {
    case 'Pipeline':
      return 'bg-emerald-100 text-emerald-800 dark:bg-emerald-900/40 dark:text-emerald-200';
    case 'Query':
      return 'bg-sky-100 text-sky-800 dark:bg-sky-900/40 dark:text-sky-200';
    case 'Callback':
      return 'bg-violet-100 text-violet-800 dark:bg-violet-900/40 dark:text-violet-200';
    case 'Unknown':
    default:
      return 'bg-slate-200 text-slate-800 dark:bg-slate-800 dark:text-slate-300';
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

/**
 * Heat-aware stat chip styling. Inline-styled because the bg/text colours interpolate along a
 * continuous hue ramp — Tailwind classes can't express a 220→0° gradient — but the chip still
 * has to read in both themes. We branch on the theme:
 *
 * - **Dark theme**: bg is dark (35% L, 40% α) so the card show-through stays subtle, text is
 *   light (88% L) for high contrast against the deep bg.
 * - **Light theme**: bg is very light (92% L, 60% α) — a soft tint on the white card — and text
 *   is dark (28% L) so the µs/ms readout has the same WCAG-AA contrast as on dark theme.
 *
 * The border colour stays the mid-tone 55% L either way; it works on both bg shades and keeps
 * the heat hue identifiable at a glance.
 */
function heatChip(heat: number, theme: 'dark' | 'light'): React.CSSProperties {
  const hue = 220 - heat * 220;
  const isDark = theme === 'dark';
  return {
    backgroundColor: isDark
      ? `hsla(${hue}, 70%, 35%, 0.4)`
      : `hsla(${hue}, 70%, 92%, 0.6)`,
    borderColor: `hsla(${hue}, 70%, 55%, 0.7)`,
    color: isDark
      ? `hsla(${hue}, 80%, 88%, 1)`
      : `hsla(${hue}, 70%, 28%, 1)`,
  };
}

/** Format µs in a width-stable way: <1ms in µs (e.g. "812µs"), ≥1ms in ms (e.g. "3.2ms"). */
function formatStat(us: number): string {
  if (us < 1000) return `${Math.round(us)}µs`;
  const ms = us / 1000;
  return ms < 10 ? `${ms.toFixed(2)}ms` : `${ms.toFixed(1)}ms`;
}
