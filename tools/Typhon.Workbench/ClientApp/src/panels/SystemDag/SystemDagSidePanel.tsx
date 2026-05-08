import { useMemo, useState } from 'react';
import { X } from 'lucide-react';
import { useAggregations } from '@/hooks/data/useAggregations';
import type { AggregationQueryDto } from '@/api/generated/model/aggregationQueryDto';
import type { HistogramBucketDto } from '@/api/generated/model/histogramBucketDto';
import type { TopKEntryDto } from '@/api/generated/model/topKEntryDto';
import type { DagNodeData } from './dagModel';
import type { GaterEntry, SystemGatingInfo } from './gatingAnalysis';
import type { TickRange } from './useDagViewStore';

/**
 * Stats derived from the batched /aggregate response by the side-panel's `useMemo`. Replaces the
 * earlier `ReturnType<typeof statsShape>` typeof-helper, which IDEs flagged as a runtime-unused
 * function. Numeric fields are widened to `number | null` because `numericValue` parses string-
 * encoded numbers (orval surfaces them as `number | string` per the OpenAPI patterns).
 */
interface PanelStats {
  mean: number | null;
  p50: number | null;
  p95: number | null;
  p99: number | null;
  max: number | null;
  count: number | null;
  histogram: HistogramBucketDto[] | null;
  topk: TopKEntryDto[] | null;
}

interface Props {
  node: DagNodeData;
  sessionId: string | null;
  range: TickRange | null;
  /** Pre-computed CP participation for this system (or null if no metadata loaded yet). */
  cpStat: { onPathTicks: number; rate: number } | null;
  /** Total ticks the CP algorithm examined — used for the "X of Y" display. */
  cpTotalTicks: number | null;
  /** Gating-predecessor analysis for this system (null when no range / no data). */
  gatingInfo: SystemGatingInfo | null;
  onClose: () => void;
}

const HISTOGRAM_BUCKETS = 20;
const TOPK_N = 5;

/**
 * Side panel rendered to the right of the DAG canvas when a system tile is clicked.
 *
 * Phase 1 (#315) shipped the identity + RFC 07 declared-access view.
 * Phase 2 (#316) layer adds — when a tick range is pinned — a stats section with the
 * duration distribution histogram and the worst-N overrun ticks, both fetched in one batched
 * /aggregate call. When no range is pinned, only the declared-access view shows.
 */
export default function SystemDagSidePanel({ node, sessionId, range, cpStat, cpTotalTicks, gatingInfo, onClose }: Props) {
  const queries = useMemo<AggregationQueryDto[]>(() => {
    if (!range || !node.systemName) return [];
    const trackId = `system/${node.systemName}`;
    return [
      { trackId, field: 'durationUs', op: 'mean', range: [range.from, range.to] },
      { trackId, field: 'durationUs', op: 'p50', range: [range.from, range.to] },
      { trackId, field: 'durationUs', op: 'p95', range: [range.from, range.to] },
      { trackId, field: 'durationUs', op: 'p99', range: [range.from, range.to] },
      { trackId, field: 'durationUs', op: 'max', range: [range.from, range.to] },
      { trackId, field: 'durationUs', op: 'count', range: [range.from, range.to] },
      { trackId, field: 'durationUs', op: 'histogram', range: [range.from, range.to], buckets: HISTOGRAM_BUCKETS },
      { trackId, field: 'durationUs', op: 'topk', range: [range.from, range.to], n: TOPK_N },
    ];
  }, [range, node.systemName]);

  const { data, isLoading, error } = useAggregations(sessionId, queries);

  const stats = useMemo<PanelStats | null>(() => {
    if (!data?.results) return null;
    const r = data.results;
    return {
      mean: numericValue(r[0]?.value),
      p50: numericValue(r[1]?.value),
      p95: numericValue(r[2]?.value),
      p99: numericValue(r[3]?.value),
      max: numericValue(r[4]?.value),
      count: numericValue(r[5]?.value),
      histogram: r[6]?.histogram ?? null,
      topk: r[7]?.topK ?? null,
    };
  }, [data]);

  return (
    <div className="flex h-full w-[300px] flex-col border-l border-border bg-background">
      <div className="flex items-center gap-2 border-b border-border px-3 py-2">
        <h3 className="truncate font-mono text-[12px] font-semibold text-foreground" title={node.systemName}>
          {node.systemName}
        </h3>
        <span className="rounded bg-muted/40 px-1.5 py-0.5 text-[9px] font-mono uppercase text-muted-foreground">
          {node.kind}
        </span>
        <button
          type="button"
          onClick={onClose}
          className="ml-auto rounded p-1 text-muted-foreground hover:bg-muted/40 hover:text-foreground"
          title="Close"
        >
          <X className="h-3.5 w-3.5" />
        </button>
      </div>

      <div className="flex-1 overflow-y-auto px-3 py-2">
        <Section label="Phase">
          <span className="font-mono text-[11px] text-foreground">{node.phaseName || '(unphased)'}</span>
        </Section>
        <Section label="Flags">
          <ChipRow>
            {node.isParallel && <span className="rounded border border-slate-300 bg-slate-100 px-1.5 py-0.5 font-mono text-[10px] text-slate-800 dark:border-slate-600/50 dark:bg-slate-900/40 dark:text-slate-200">parallel</span>}
            {node.isExclusivePhase && <span className="rounded border border-amber-300 bg-amber-100 px-1.5 py-0.5 font-mono text-[10px] text-amber-800 dark:border-amber-700/50 dark:bg-amber-950/40 dark:text-amber-200">exclusive</span>}
            {node.tierFilter !== 0x0F && <span className="rounded border border-slate-300 bg-slate-100 px-1.5 py-0.5 font-mono text-[10px] text-slate-800 dark:border-slate-600/50 dark:bg-slate-900/40 dark:text-slate-200">tier {node.tierFilter}</span>}
            {!node.isParallel && !node.isExclusivePhase && node.tierFilter === 0x0F && (
              <span className="font-mono text-[10px] text-muted-foreground">none</span>
            )}
          </ChipRow>
        </Section>

        {cpStat && cpTotalTicks != null && cpTotalTicks > 0 && (
          <Section label="critical-path participation">
            <CriticalPathRow rate={cpStat.rate} onPathTicks={cpStat.onPathTicks} totalTicks={cpTotalTicks} />
          </Section>
        )}

        {gatingInfo && gatingInfo.gaters.length > 0 && (
          <GatedBySection info={gatingInfo} />
        )}

        {range && (
          <StatsSection
            range={range}
            stats={stats}
            isLoading={isLoading}
            error={error as Error | null}
          />
        )}

        <AccessGroup label="reads" tone="read" items={node.reads} />
        <AccessGroup label="reads fresh" tone="fresh" items={node.readsFresh} />
        <AccessGroup label="reads snapshot" tone="snapshot" items={node.readsSnapshot} />
        <AccessGroup label="writes" tone="write" items={node.writes} />
        <AccessGroup label="side-writes" tone="side-write" items={node.sideWrites} />

        {/*
          Event queues — producer (writesEvents) and consumer (readsEvents) sides shown
          separately. Same dashed-violet tone as the corresponding edge style on the canvas
          so the cross-reference reads at a glance.
        */}
        <AccessGroup label="reads events" tone="event-read" items={node.readsEvents} />
        <AccessGroup label="writes events" tone="event-write" items={node.writesEvents} />

        {/*
          Named resources. Resources have no Fresh/Snapshot distinction in v1 so each side is
          a flat list. Tone matches the dotted-red resource edges on the canvas.
        */}
        <AccessGroup label="reads resources" tone="resource-read" items={node.readsResources} />
        <AccessGroup label="writes resources" tone="resource-write" items={node.writesResources} />

        {!node.hasAccess && (
          <div className="mt-3 rounded border border-amber-300 bg-amber-50 px-2 py-1.5 text-[10px] text-amber-900 dark:border-amber-700/40 dark:bg-amber-950/30 dark:text-amber-200">
            This system has no RFC 07 declarations on the wire. The trace may predate v6 of the
            wire format, or the host has not been recompiled after #310.
          </div>
        )}
      </div>
    </div>
  );
}

function StatsSection({
  range,
  stats,
  isLoading,
  error,
}: {
  range: TickRange;
  stats: PanelStats | null;
  isLoading: boolean;
  error: Error | null;
}) {
  return (
    <Section label={`stats over ticks ${range.from}–${range.to}`}>
      {error ? (
        <div className="font-mono text-[10px] text-destructive">{error.message}</div>
      ) : isLoading || !stats ? (
        <div className="font-mono text-[10px] text-muted-foreground">Loading…</div>
      ) : (
        <>
          <div className="grid grid-cols-3 gap-x-2 gap-y-1 font-mono text-[10px] text-foreground">
            <Stat label="count" value={stats.count} unit="" />
            <Stat label="mean" value={stats.mean} unit="µs" />
            <Stat label="max" value={stats.max} unit="µs" />
            <Stat label="p50" value={stats.p50} unit="µs" />
            <Stat label="p95" value={stats.p95} unit="µs" />
            <Stat label="p99" value={stats.p99} unit="µs" />
          </div>
          {stats.histogram && stats.histogram.length > 0 && (
            <div className="mt-2">
              <div className="mb-1 font-mono text-[9px] uppercase tracking-wide text-muted-foreground">
                duration distribution
              </div>
              <Histogram buckets={stats.histogram} />
            </div>
          )}
          {stats.topk && stats.topk.length > 0 && (
            <div className="mt-2">
              <div className="mb-1 font-mono text-[9px] uppercase tracking-wide text-muted-foreground">
                top-{stats.topk.length} overruns
              </div>
              <div className="space-y-0.5 font-mono text-[10px]">
                {stats.topk.map((entry, i) => (
                  <div key={`${entry.tickNumber}-${i}`} className="flex justify-between">
                    <span className="text-muted-foreground">tick {String(entry.tickNumber)}</span>
                    <span className="text-foreground">{formatUs(numericValue(entry.value) ?? 0)}</span>
                  </div>
                ))}
              </div>
            </div>
          )}
        </>
      )}
    </Section>
  );
}

function CriticalPathRow({
  rate,
  onPathTicks,
  totalTicks,
}: {
  rate: number;
  onPathTicks: number;
  totalTicks: number;
}) {
  const pct = (rate * 100).toFixed(0);
  const tone = rate >= 0.5 ? 'text-amber-300' : rate >= 0.1 ? 'text-amber-400/70' : 'text-muted-foreground';
  const headline = rate >= 0.5
    ? 'Bottleneck — fix here first'
    : rate >= 0.1
      ? 'Occasional spike — not the dominant cost'
      : 'Never holds the tick — safe to deprioritise';
  return (
    <div className="space-y-0.5">
      <div className="flex items-baseline justify-between font-mono">
        <span className={`text-[14px] tabular-nums ${tone}`}>{pct}%</span>
        <span className="text-[10px] text-muted-foreground">
          {onPathTicks} / {totalTicks} ticks
        </span>
      </div>
      <div className="font-mono text-[10px] text-muted-foreground">{headline}</div>
    </div>
  );
}

function Stat({ label, value, unit }: { label: string; value: number | null; unit: string }) {
  return (
    <div>
      <div className="text-[8px] uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className="text-[11px] tabular-nums">
        {value == null ? '—' : `${formatNumber(value)}${unit ? ' ' + unit : ''}`}
      </div>
    </div>
  );
}

function Histogram({ buckets }: { buckets: PanelStats['histogram'] }) {
  // SVG bar chart. Cosmetic — design says ~280×140 for Tier 3, but inside a 300px panel we go
  // a touch narrower.
  const safeBuckets = buckets ?? [];
  let max = 0;
  for (const b of safeBuckets) {
    const c = numericValue(b.count) ?? 0;
    if (c > max) max = c;
  }
  if (max === 0 || safeBuckets.length === 0) {
    return <div className="font-mono text-[10px] text-muted-foreground">No data in range.</div>;
  }
  const width = 260;
  const height = 80;
  const barW = width / safeBuckets.length;
  return (
    <svg width={width} height={height} className="block">
      {safeBuckets.map((b, i) => {
        const c = numericValue(b.count) ?? 0;
        const h = (c / max) * (height - 16);
        const x = i * barW;
        const y = height - h - 12;
        return (
          <g key={i}>
            <rect x={x + 0.5} y={y} width={Math.max(0, barW - 1)} height={h} fill="hsl(var(--primary, 220 70% 60%))" opacity={0.7} />
          </g>
        );
      })}
      <text x={0} y={height - 1} className="font-mono" fontSize={9} fill="currentColor" opacity={0.5}>
        {formatUs(numericValue(safeBuckets[0]?.bucketStart) ?? 0)}
      </text>
      <text
        x={width}
        y={height - 1}
        textAnchor="end"
        className="font-mono"
        fontSize={9}
        fill="currentColor"
        opacity={0.5}
      >
        {formatUs(numericValue(safeBuckets[safeBuckets.length - 1]?.bucketEnd) ?? 0)}
      </text>
    </svg>
  );
}

function numericValue(v: number | string | null | undefined): number | null {
  if (v == null) return null;
  const n = typeof v === 'number' ? v : Number(v);
  return Number.isFinite(n) ? n : null;
}

function formatNumber(n: number): string {
  if (n >= 1000) return n.toFixed(0);
  if (n >= 100) return n.toFixed(1);
  return n.toFixed(2);
}

function formatUs(us: number): string {
  if (us < 1000) return `${Math.round(us)}µs`;
  const ms = us / 1000;
  return ms < 10 ? `${ms.toFixed(2)}ms` : `${ms.toFixed(1)}ms`;
}

/**
 * "Gated by" panel — answers "why did this system wait, and what for?". Per-predecessor row
 * shows: who, % of ticks they gated, predecessor's mean duration, the wait gap (mostly = 0
 * for the dominant gater since `ReadyUs == max(pred.EndUs)` by construction), the edge kind,
 * and the via list (component / event / resource that justifies the edge). Sorted with the
 * most-frequent gater first. Trailing mitigation hint maps the edge kind to a likely fix.
 */
function GatedBySection({ info }: { info: SystemGatingInfo }) {
  // Split predecessors into "active" (gated at least one tick) and "silent" (never gated).
  // Silent predecessors are required for correctness but never the bottleneck — showing all
  // of them inline floods the panel for systems with many deps (e.g. PrepareRenderBuffer, 14
  // silent predecessors). Hide them behind a click-to-expand summary so the active gaters
  // stay prominent.
  const active = info.gaters.filter((g) => g.ticksGated > 0);
  const silent = info.gaters.filter((g) => g.ticksGated === 0);
  const [showSilent, setShowSilent] = useState(false);

  return (
    <div className="mb-2.5">
      <div className="mb-0.5 flex items-baseline justify-between">
        <span className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">gated by</span>
        <span className="font-mono text-[9px] text-muted-foreground">
          mean wait {formatUs(info.meanWaitGapUs)} · {info.ticksObserved} ticks
        </span>
      </div>
      <div className="space-y-0.5">
        {active.length > 0 ? (
          active.map((g) => <GaterRow key={g.predecessorName} gater={g} />)
        ) : (
          <div className="font-mono text-[10px] italic text-muted-foreground">
            No predecessor was the gater in any observed tick.
          </div>
        )}
      </div>
      {silent.length > 0 && (
        <SilentPredecessorsBlock
          silent={silent}
          expanded={showSilent}
          onToggle={() => setShowSilent((s) => !s)}
        />
      )}
      <MitigationHint gaters={info.gaters} />
    </div>
  );
}

/**
 * Collapsible "+ N other predecessors (never gated)" block. Closed by default so the active
 * gater(s) stay the focus; expanded reveals the full silent list with the same per-row
 * styling as the active rows (just consistently in the < 10% tier, since `ticksGated == 0`).
 */
function SilentPredecessorsBlock({
  silent,
  expanded,
  onToggle,
}: {
  silent: GaterEntry[];
  expanded: boolean;
  onToggle: () => void;
}) {
  return (
    <>
      <button
        type="button"
        onClick={onToggle}
        className="mt-1 flex w-full items-center justify-between rounded border border-border/40 bg-card/40 px-1.5 py-0.5 font-mono text-[10px] text-muted-foreground hover:bg-muted/40 hover:text-foreground"
        title={expanded
          ? 'Collapse — hide predecessors that never gated this system in the range'
          : 'Expand — show predecessors that exist in the DAG but never gated this system'}
      >
        <span>+ {silent.length} other predecessor{silent.length === 1 ? '' : 's'} (never gated)</span>
        <span className="font-sans text-[9px]">{expanded ? '▾' : '▸'}</span>
      </button>
      {expanded && (
        <div className="mt-0.5 space-y-0.5">
          {silent.map((g) => (
            <GaterRow key={g.predecessorName} gater={g} />
          ))}
        </div>
      )}
    </>
  );
}

function GaterRow({ gater }: { gater: GaterEntry }) {
  const pctTicks = gater.ticksObserved > 0 ? (gater.ticksGated / gater.ticksObserved) * 100 : 0;
  // Top gater (≥ 50% of ticks) is the explanation; weak gaters are dimmed.
  const isPrimary = pctTicks >= 50;
  // Theme-paired tones — light theme gets dark amber on a soft tint, dark theme gets the
  // earlier light-amber-on-deep-bg treatment. Without the `dark:` split the text washes out
  // on the white-ish light theme card.
  const pctTone = isPrimary
    ? 'text-amber-700 dark:text-amber-300'
    : pctTicks >= 10
      ? 'text-amber-700/70 dark:text-amber-400/70'
      : 'text-muted-foreground/60';
  const nameTone = isPrimary ? 'text-foreground' : 'text-muted-foreground';
  const rowToneClass = isPrimary
    ? 'border-amber-300 bg-amber-100/60 dark:border-amber-700/40 dark:bg-amber-950/20'
    : 'border-border/60 bg-card/60';
  const edge = gater.edge;
  return (
    <div className={`rounded border px-1.5 py-1 font-mono text-[10px] ${rowToneClass}`}>
      <div className="flex items-baseline gap-1.5">
        <span className={`flex-1 truncate ${nameTone}`} title={gater.predecessorName}>
          {gater.predecessorName}
        </span>
        <span className={`tabular-nums ${pctTone}`}>{pctTicks.toFixed(0)}%</span>
      </div>
      <div className="mt-0.5 flex flex-wrap items-baseline gap-x-2 gap-y-0.5 text-[9px] text-muted-foreground">
        <span>pred dur {formatUs(gater.meanPredDurationUs)}</span>
        {edge ? (
          <>
            <span className={edgeKindClasses(edge.kind)}>{edgeKindLabel(edge.kind)}</span>
            {edge.via.length > 0 && (
              <span className="truncate" title={edge.via.join(', ')}>via {edge.via.join(', ')}</span>
            )}
          </>
        ) : (
          <span className="italic">no derived edge</span>
        )}
      </div>
    </div>
  );
}

/**
 * Kind-driven hint: surfaced under the gater list when one predecessor dominates. The hint maps
 * the most-frequent edge kind to a typical mitigation. We only show it when the primary gater
 * gates ≥ 50% of ticks — at lower frequencies the dependency is intermittent and the hint is
 * more noise than signal.
 */
function MitigationHint({ gaters }: { gaters: GaterEntry[] }) {
  if (gaters.length === 0) return null;
  const primary = gaters[0];
  const pctTicks = primary.ticksObserved > 0 ? (primary.ticksGated / primary.ticksObserved) * 100 : 0;
  if (pctTicks < 50) return null;
  const edge = primary.edge;
  if (!edge) return null;
  const hint = mitigationHintForKind(edge.kind, edge.via);
  if (!hint) return null;
  return (
    <div className="mt-1.5 rounded border border-sky-300 bg-sky-50 px-2 py-1 text-[10px] text-sky-900 dark:border-sky-800/40 dark:bg-sky-950/30 dark:text-sky-200">
      <span className="mr-1 font-semibold">Mitigation:</span>
      {hint}
    </div>
  );
}

function mitigationHintForKind(kind: GaterEntry['edge'] extends infer T ? (T extends { kind: infer K } ? K : never) : never, via: string[]): string | null {
  switch (kind) {
    case 'fresh':
      // Fresh covers the standard "writer→reader" within-phase relation AND the cross-phase
      // case (any access conflict on a component). The actionable response is the same: see
      // if the write is conditional / rare and could move out of this system.
      return `Both systems touch ${via.length > 0 ? via.join(', ') : 'a shared component'}. If the write is conditional / rare, consider moving it to a dedicated system or using SideWrites<T>().`;
    case 'snapshot':
      return `Snapshot read on ${via.length > 0 ? via.join(', ') : 'this component'}. If the reader doesn't truly need the previous-tick value, downgrading to a fresh read could remove the inversion.`;
    case 'manual':
      return 'Explicit `.After()` / `.Before()` ordering. Verify the manual edge is still required — it may have outlived its original constraint.';
    case 'event':
    case 'resource':
      // Event and resource dependencies are usually intentional — no canned hint.
      return null;
  }
}

function edgeKindLabel(kind: GaterEntry['edge'] extends infer T ? (T extends { kind: infer K } ? K : never) : never): string {
  switch (kind) {
    case 'fresh': return 'fresh';
    case 'snapshot': return 'snapshot';
    case 'manual': return 'manual';
    case 'event': return 'event';
    case 'resource': return 'resource';
  }
}

function edgeKindClasses(kind: GaterEntry['edge'] extends infer T ? (T extends { kind: infer K } ? K : never) : never): string {
  // Theme-paired chip tones. Light theme uses a 100-shade tint with 800 text; dark uses the
  // 950/40 bg with 200 text. Same hue family as the corresponding canvas edge so the visual
  // pairing between side panel and DAG arrow holds in both modes.
  const base = 'rounded px-1 py-0.5';
  switch (kind) {
    case 'fresh': return `${base} bg-amber-100 text-amber-800 dark:bg-amber-950/40 dark:text-amber-200`;
    case 'snapshot': return `${base} bg-sky-100 text-sky-800 dark:bg-sky-950/40 dark:text-sky-200`;
    case 'manual': return `${base} bg-slate-200 text-slate-800 dark:bg-slate-800/50 dark:text-slate-200`;
    case 'event': return `${base} bg-violet-100 text-violet-800 dark:bg-violet-950/40 dark:text-violet-200`;
    case 'resource': return `${base} bg-red-100 text-red-800 dark:bg-red-950/40 dark:text-red-200`;
  }
}

function Section({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="mb-2.5">
      <div className="text-[10px] font-semibold uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className="mt-0.5">{children}</div>
    </div>
  );
}

function ChipRow({ children }: { children: React.ReactNode }) {
  return <div className="flex flex-wrap items-center gap-1">{children}</div>;
}

type AccessTone =
  | 'read'
  | 'fresh'
  | 'snapshot'
  | 'write'
  | 'side-write'
  | 'event-read'
  | 'event-write'
  | 'resource-read'
  | 'resource-write';

function AccessGroup({
  label,
  tone,
  items,
}: {
  label: string;
  tone: AccessTone;
  items: string[];
}) {
  if (items.length === 0) return null;
  return (
    <Section label={label}>
      <ChipRow>
        {items.map((item) => (
          <span key={item} className={`rounded border px-1.5 py-0.5 font-mono text-[10px] ${toneClasses(tone)}`}>
            {item}
          </span>
        ))}
      </ChipRow>
    </Section>
  );
}

function toneClasses(tone: AccessTone): string {
  // Theme-paired tones. Each chip uses light-bg + dark-text on light theme and dark-bg +
  // light-text on dark theme. The hue family is preserved so the side-panel chip still
  // visually pairs with its corresponding canvas edge style. Producer side gets a slightly
  // bolder tint (200/100 shades) than consumer to keep the two readable at a glance.
  switch (tone) {
    case 'read':
      return 'border-slate-300 bg-slate-100 text-slate-800 dark:border-slate-600/50 dark:bg-slate-900/40 dark:text-slate-200';
    case 'fresh':
      return 'border-emerald-300 bg-emerald-100 text-emerald-800 dark:border-emerald-700/50 dark:bg-emerald-950/40 dark:text-emerald-200';
    case 'snapshot':
      return 'border-sky-300 bg-sky-100 text-sky-800 dark:border-sky-700/50 dark:bg-sky-950/40 dark:text-sky-200';
    case 'write':
      return 'border-rose-300 bg-rose-100 text-rose-800 dark:border-rose-700/50 dark:bg-rose-950/40 dark:text-rose-200';
    case 'side-write':
      return 'border-orange-300 bg-orange-100 text-orange-800 dark:border-orange-700/50 dark:bg-orange-950/40 dark:text-orange-200';
    // Event tones — violet, matching the dashed event edges on the canvas.
    case 'event-read':
      return 'border-violet-300 bg-violet-100 text-violet-800 dark:border-violet-700/50 dark:bg-violet-950/40 dark:text-violet-200';
    case 'event-write':
      return 'border-violet-400 bg-violet-200 text-violet-900 dark:border-violet-600/60 dark:bg-violet-900/50 dark:text-violet-100';
    // Resource tones — red, matching the dotted resource edges.
    case 'resource-read':
      return 'border-red-300 bg-red-100 text-red-800 dark:border-red-800/50 dark:bg-red-950/40 dark:text-red-200';
    case 'resource-write':
      return 'border-red-400 bg-red-200 text-red-900 dark:border-red-700/60 dark:bg-red-900/50 dark:text-red-100';
  }
}
