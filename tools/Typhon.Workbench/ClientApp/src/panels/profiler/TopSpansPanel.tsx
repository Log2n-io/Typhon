import { useMemo, useState } from 'react';
import { ArrowDown, ArrowUp } from 'lucide-react';
import { cn } from '@/lib/utils';
import type { SpanGroupStats } from '@/libs/profiler/stats/selectionStats';
import { useProfilerStatsStore } from '@/stores/useProfilerStatsStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useSessionStore } from '@/stores/useSessionStore';

/**
 * Top-N expensive spans — its own dock panel because the table is wide (7 columns) and would force
 * the user to widen the right Detail strip just to read it. Lives in its own pane so the columns
 * have room to breathe horizontally without crowding the more-vertical Detail / Logs panels.
 *
 * Reads pre-aggregated stats from `useProfilerStatsStore` — the producer (`useProfilerStatsWriter`,
 * called by `ProfilerPanel`) runs `computeSelectionStats` synchronously on each committed `viewRange`
 * change. Coalescing across a wheel-zoom burst is handled upstream by the transient/committed slot
 * split in `useProfilerViewStore` (#345), so the writer no longer needs its own debounce. Click a row
 * → tween viewport onto the worst-instance span of that group ± 5% padding.
 */

type SpanSortKey = 'name' | 'count' | 'minUs' | 'avgUs' | 'maxUs' | 'p95Us' | 'totalUs';
const TOP_SPANS_LIMIT = 20;

export default function TopSpansPanel(): React.JSX.Element {
  const sessionKind = useSessionStore((s) => s.kind);
  const isProfilerSession = sessionKind === 'attach' || sessionKind === 'trace';
  const stats = useProfilerStatsStore((s) => s.stats);

  if (!isProfilerSession) {
    return (
      <div className="flex h-full items-center justify-center bg-background p-3">
        <p className="text-fs-sm text-muted-foreground">Open a profiler trace or attach a session to see top spans.</p>
      </div>
    );
  }

  if (stats === null || stats.spanGroups.length === 0) {
    return (
      <div className="flex h-full items-center justify-center bg-background p-3">
        <p className="text-fs-sm text-muted-foreground">No spans in the current viewport — pan or zoom to see top spans.</p>
      </div>
    );
  }

  return (
    <div className="flex h-full flex-col overflow-hidden bg-background">
      <TopSpansTable groups={stats.spanGroups} />
    </div>
  );
}

function TopSpansTable({ groups }: { groups: SpanGroupStats[] }): React.JSX.Element {
  // Default sort: max desc — biggest single span first, the most useful starting point for "what's
  // the slowest thing in this range?". A sort-by-total view lets a thousand-instance hot kind win
  // even when each instance is fast, which is a different (and less common) question.
  const [sortKey, setSortKey] = useState<SpanSortKey>('maxUs');
  const [sortDesc, setSortDesc] = useState(true);
  const commitViewRange = useProfilerViewStore((s) => s.commitViewRange);

  const sorted = useMemo(() => {
    const cmp = (a: SpanGroupStats, b: SpanGroupStats): number => {
      if (sortKey === 'name') return a.name.localeCompare(b.name);
      return (a[sortKey] as number) - (b[sortKey] as number);
    };
    const arr = groups.slice().sort(cmp);
    if (sortDesc) arr.reverse();
    return arr.slice(0, TOP_SPANS_LIMIT);
  }, [groups, sortKey, sortDesc]);

  const onClickHeader = (key: SpanSortKey): void => {
    if (key === sortKey) {
      setSortDesc((d) => !d);
    } else {
      setSortKey(key);
      // Sensible default direction per column: name asc; everything else desc (biggest-first).
      setSortDesc(key !== 'name');
    }
  };

  const onClickRow = (group: SpanGroupStats): void => {
    const span = group.worstSpan;
    const dur = span.endUs - span.startUs;
    const pad = Math.max(dur * 0.05, 1); // 5% padding, with a 1 µs floor for sub-µs spans
    commitViewRange({ startUs: span.startUs - pad, endUs: span.endUs + pad });
  };

  return (
    <div className="flex h-full flex-col overflow-hidden">
      <div className="wb-pane-header flex items-baseline justify-between border-b border-border px-3 py-2">
        <span className="text-fs-sm text-muted-foreground">click row → jump to worst instance</span>
        {groups.length > TOP_SPANS_LIMIT && (
          <span className="text-fs-xs text-muted-foreground">
            top {TOP_SPANS_LIMIT} of {groups.length} groups by {labelForSortKey(sortKey)}
          </span>
        )}
      </div>
      <div className="min-h-0 flex-1 overflow-auto">
        <table className="w-full text-fs-sm">
          <thead className="sticky top-0 z-10 bg-background">
            <tr className="text-left text-muted-foreground">
              <SortHeader label="Name"  align="left"  sortKey="name"    activeKey={sortKey} desc={sortDesc} onClick={onClickHeader} />
              <SortHeader label="Count" align="right" sortKey="count"   activeKey={sortKey} desc={sortDesc} onClick={onClickHeader} />
              <SortHeader label="Min"   align="right" sortKey="minUs"   activeKey={sortKey} desc={sortDesc} onClick={onClickHeader} />
              <SortHeader label="Avg"   align="right" sortKey="avgUs"   activeKey={sortKey} desc={sortDesc} onClick={onClickHeader} />
              <SortHeader label="Max"   align="right" sortKey="maxUs"   activeKey={sortKey} desc={sortDesc} onClick={onClickHeader} />
              <SortHeader label="P95"   align="right" sortKey="p95Us"   activeKey={sortKey} desc={sortDesc} onClick={onClickHeader} />
              <SortHeader label="Total" align="right" sortKey="totalUs" activeKey={sortKey} desc={sortDesc} onClick={onClickHeader} />
            </tr>
          </thead>
          <tbody>
            {sorted.map((g) => (
              <tr
                key={g.name}
                className="cursor-pointer border-t border-border/50 hover:bg-accent/40"
                onClick={() => onClickRow(g)}
                title={`Click to jump to the slowest instance of ${g.name}`}
              >
                <td className="truncate px-3 py-1 font-mono text-foreground">{g.name}</td>
                <td className="px-3 py-1 text-right font-mono tabular-nums text-foreground">{g.count.toLocaleString()}</td>
                <td className="px-3 py-1 text-right font-mono tabular-nums text-foreground">{formatDurationUs(g.minUs)}</td>
                <td className="px-3 py-1 text-right font-mono tabular-nums text-foreground">{formatDurationUs(g.avgUs)}</td>
                <td className="px-3 py-1 text-right font-mono tabular-nums text-foreground">{formatDurationUs(g.maxUs)}</td>
                <td className="px-3 py-1 text-right font-mono tabular-nums text-foreground">{formatDurationUs(g.p95Us)}</td>
                <td className="px-3 py-1 text-right font-mono tabular-nums text-foreground">{formatDurationUs(g.totalUs)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function SortHeader({
  label,
  sortKey,
  activeKey,
  desc,
  align,
  onClick,
}: {
  label: string;
  sortKey: SpanSortKey;
  activeKey: SpanSortKey;
  desc: boolean;
  align: 'left' | 'right';
  onClick: (k: SpanSortKey) => void;
}): React.JSX.Element {
  const active = activeKey === sortKey;
  return (
    <th
      className={cn(
        'cursor-pointer select-none px-3 py-2 font-normal hover:text-foreground',
        align === 'right' ? 'text-right' : 'text-left',
        active && 'text-foreground',
      )}
      onClick={() => onClick(sortKey)}
    >
      <span className={cn('inline-flex items-center gap-1', align === 'right' && 'flex-row-reverse')}>
        {label}
        {active && (desc ? <ArrowDown className="h-3 w-3" /> : <ArrowUp className="h-3 w-3" />)}
      </span>
    </th>
  );
}

function labelForSortKey(k: SpanSortKey): string {
  switch (k) {
    case 'name':    return 'name';
    case 'count':   return 'count';
    case 'minUs':   return 'min duration';
    case 'avgUs':   return 'average duration';
    case 'maxUs':   return 'max duration';
    case 'p95Us':   return 'p95 duration';
    case 'totalUs': return 'total duration';
  }
}

/**
 * Adaptive time formatting — picks the coarsest unit (ns / µs / ms / s) that keeps the displayed
 * number readable. Shared shape with `ProfilerDetail.formatDurationUs`; duplicated here to keep the
 * panel self-contained without importing through ProfilerDetail's larger module.
 */
function formatDurationUs(us: number): string {
  const abs = Math.abs(us);
  if (abs === 0) return '0 µs';
  if (abs < 1) {
    const ns = abs * 1000;
    return `${ns.toFixed(ns < 10 ? 1 : 0)} ns`;
  }
  if (abs < 1000) return `${abs.toFixed(3)} µs`;
  if (abs < 1_000_000) return `${(abs / 1000).toFixed(3)} ms`;
  return `${(abs / 1_000_000).toFixed(3)} s`;
}
