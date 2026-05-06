import { useEffect, useMemo } from 'react';
import { Camera } from 'lucide-react';
import type { TickSummaryDto } from '@/api/generated/model/tickSummaryDto';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { lastNTicksToTime, timeToTickRange } from './tickRangeMapping';
import { useDagViewStore, type LayoutMode, type StatMode } from './useDagViewStore';

interface Props {
  /** Profiler-metadata tick rows — used for both the µs↔tick conversion and the auto-snapshot decision. */
  tickSummaries: readonly TickSummaryDto[] | null;
  /** Auto-snapshot once on first arrival of metadata when no time selection exists yet. */
  autoSnapshotEnabled: boolean;
}

const STAT_OPTIONS: Array<{ key: StatMode; label: string }> = [
  { key: 'mean', label: 'mean' },
  { key: 'p50', label: 'p50' },
  { key: 'p95', label: 'p95' },
  { key: 'p99', label: 'p99' },
  { key: 'max', label: 'max' },
];

/**
 * Layout choices exposed in the toolbar combo. Phase-aware layouts (horizontal/vertical lanes)
 * preserve the design's swim-lane skeleton; phase-agnostic layouts (compact/circular) drop the
 * lanes for cases where the user wants a different angle on the same topology.
 */
const LAYOUT_OPTIONS: Array<{ key: LayoutMode; label: string; description: string }> = [
  { key: 'horizontal-lanes', label: 'Horizontal lanes', description: 'Phases stack top-to-bottom; systems flow left-to-right within each phase' },
  { key: 'vertical-lanes', label: 'Vertical lanes', description: 'Phases as side-by-side columns; systems flow top-to-bottom within each phase' },
  { key: 'compact', label: 'Compact', description: 'Flat layered layout. No swim lanes; cross-phase edges are visible' },
  { key: 'circular', label: 'Circular', description: 'Systems on a circle, ordered by phase then name. All edges visible' },
];

const SNAPSHOT_TICK_COUNT = 600;

/**
 * Top-of-panel toolbar for the System DAG. Two controls per `09-system-dag.md §6.1` + §7.2:
 * the **stat-mode** selector that swaps the per-node primary stat aggregation, and the
 * **Snapshot last N ticks** action that pins both panels (DAG aggregation range + profiler
 * TimeArea) to a frozen window via `useSelectionStore.time`.
 *
 * After cross-panel binding (§7.1), the range readout reflects whatever µs window the user has
 * selected — whether they snapshotted here or scrubbed in the profiler. The selection store and
 * its bridges keep the two views in lockstep automatically.
 *
 * Auto-snapshot fires once on first metadata arrival when the time slot is null AND nothing has
 * been deep-linked from a URL — so a fresh open shows useful colour without a click.
 */
export default function SystemDagToolbar({ tickSummaries, autoSnapshotEnabled }: Props) {
  const time = useSelectionStore((s) => s.time);
  const setTime = useSelectionStore((s) => s.setTime);
  const statMode = useDagViewStore((s) => s.statMode);
  const setStatMode = useDagViewStore((s) => s.setStatMode);
  const layout = useDagViewStore((s) => s.layout);
  const setLayout = useDagViewStore((s) => s.setLayout);

  const hasTicks = tickSummaries != null && tickSummaries.length > 0;

  // Translate the current time-window (µs) back to ticks for the readout. Cross-panel scrubs
  // round through the converter so the user sees consistent tick numbers regardless of who set
  // the window.
  const tickRange = useMemo(() => timeToTickRange(time, tickSummaries), [time, tickSummaries]);

  // Auto-snapshot once on first arrival when the time slot is unset. After that, snapshot is
  // user-driven (per §7.3 — no continuous live updates).
  useEffect(() => {
    if (!autoSnapshotEnabled) return;
    if (!hasTicks || time != null || tickSummaries == null) return;
    const next = lastNTicksToTime(SNAPSHOT_TICK_COUNT, tickSummaries);
    if (next) setTime(next);
  }, [autoSnapshotEnabled, hasTicks, time, tickSummaries, setTime]);

  const onSnapshotClick = () => {
    if (tickSummaries == null) return;
    const next = lastNTicksToTime(SNAPSHOT_TICK_COUNT, tickSummaries);
    if (next) setTime(next);
  };

  const onClearClick = () => setTime(null);

  return (
    <div className="flex items-center gap-3 border-b border-border bg-background/95 px-3 py-1.5">
      <button
        type="button"
        disabled={!hasTicks}
        onClick={onSnapshotClick}
        className="flex items-center gap-1.5 rounded border border-border bg-card px-2 py-1 font-mono text-[11px] text-foreground hover:bg-muted disabled:cursor-not-allowed disabled:opacity-40"
        title={hasTicks ? `Pin both views to the last ${SNAPSHOT_TICK_COUNT} ticks` : 'Waiting for ticks…'}
      >
        <Camera className="h-3 w-3" />
        Snapshot last {SNAPSHOT_TICK_COUNT} ticks
      </button>

      <div className="flex items-center gap-1">
        <span className="font-mono text-[10px] uppercase tracking-wide text-muted-foreground">stat</span>
        <div className="flex overflow-hidden rounded border border-border">
          {STAT_OPTIONS.map((opt) => {
            const active = statMode === opt.key;
            return (
              <button
                key={opt.key}
                type="button"
                onClick={() => setStatMode(opt.key)}
                className={`px-2 py-0.5 font-mono text-[11px] ${active
                    ? 'bg-primary text-primary-foreground'
                    : 'bg-card text-foreground hover:bg-muted'
                  }`}
                title={`Show ${opt.label} duration per system`}
              >
                {opt.label}
              </button>
            );
          })}
        </div>
      </div>

      <div className="flex items-center gap-1">
        <span className="font-mono text-[10px] uppercase tracking-wide text-muted-foreground">layout</span>
        <select
          value={layout}
          onChange={(e) => setLayout(e.target.value as LayoutMode)}
          className="rounded border border-border bg-card px-2 py-0.5 font-mono text-[11px] text-foreground hover:bg-muted focus:outline-none focus:ring-1 focus:ring-primary"
          title={LAYOUT_OPTIONS.find((o) => o.key === layout)?.description ?? ''}
        >
          {LAYOUT_OPTIONS.map((opt) => (
            <option key={opt.key} value={opt.key} title={opt.description}>
              {opt.label}
            </option>
          ))}
        </select>
      </div>

      <div className="ml-auto flex items-center gap-2 font-mono text-[10px] text-muted-foreground">
        {tickRange ? (
          <>
            <span>
              Ticks {tickRange.from}–{tickRange.to}{' '}
              <span className="text-muted-foreground/60">({tickRange.to - tickRange.from + 1})</span>
            </span>
            <button
              type="button"
              onClick={onClearClick}
              className="rounded px-1.5 py-0.5 text-muted-foreground hover:bg-muted/40 hover:text-foreground"
              title="Clear the time selection — node stats hidden until you snapshot or scrub the profiler"
            >
              clear
            </button>
          </>
        ) : time != null ? (
          // Time slot is set but the window doesn't intersect any tick (e.g. selection scrubbed
          // before the first tick). Show a hint instead of a stale tick range.
          <span>Selection has no ticks — scrub or snapshot.</span>
        ) : (
          <span>No range — snapshot or scrub the profiler to enable stats.</span>
        )}
      </div>
    </div>
  );
}
