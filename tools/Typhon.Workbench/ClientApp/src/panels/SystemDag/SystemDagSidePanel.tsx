import { useMemo } from 'react';
import { X } from 'lucide-react';
import { useAggregations } from '@/hooks/data/useAggregations';
import type { AggregationQueryDto } from '@/api/generated/model/aggregationQueryDto';
import type { HistogramBucketDto } from '@/api/generated/model/histogramBucketDto';
import type { TopKEntryDto } from '@/api/generated/model/topKEntryDto';
import type { DagNodeData } from './dagModel';
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
export default function SystemDagSidePanel({ node, sessionId, range, cpStat, cpTotalTicks, onClose }: Props) {
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
            {node.isParallel && <span className="rounded border border-slate-600/50 bg-slate-900/40 px-1.5 py-0.5 font-mono text-[10px] text-slate-200">parallel</span>}
            {node.isExclusivePhase && <span className="rounded border border-amber-700/50 bg-amber-950/40 px-1.5 py-0.5 font-mono text-[10px] text-amber-200">exclusive</span>}
            {node.tierFilter !== 0x0F && <span className="rounded border border-slate-600/50 bg-slate-900/40 px-1.5 py-0.5 font-mono text-[10px] text-slate-200">tier {node.tierFilter}</span>}
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

        {!node.hasAccess && (
          <div className="mt-3 rounded border border-amber-700/40 bg-amber-950/30 px-2 py-1.5 text-[10px] text-amber-200">
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

function AccessGroup({
  label,
  tone,
  items,
}: {
  label: string;
  tone: 'read' | 'fresh' | 'snapshot' | 'write' | 'side-write';
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

function toneClasses(tone: 'read' | 'fresh' | 'snapshot' | 'write' | 'side-write'): string {
  switch (tone) {
    case 'read':
      return 'border-slate-600/50 bg-slate-900/40 text-slate-200';
    case 'fresh':
      return 'border-emerald-700/50 bg-emerald-950/40 text-emerald-200';
    case 'snapshot':
      return 'border-sky-700/50 bg-sky-950/40 text-sky-200';
    case 'write':
      return 'border-rose-700/50 bg-rose-950/40 text-rose-200';
    case 'side-write':
      return 'border-orange-700/50 bg-orange-950/40 text-orange-200';
  }
}
