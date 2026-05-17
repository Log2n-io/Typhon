import { useEffect, useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useTopology } from '@/hooks/data/useTopology';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { deriveEdges } from '@/lib/dag/edgeDerivation';
import { timeToTickRange } from '../SystemDag/tickRangeMapping';
import { computeAggregateCriticalPath, computeCriticalPathForTick, dominantTickInRange } from './criticalPath';
import CriticalPathToolbar from './CriticalPathToolbar';
import CriticalPathView from './CriticalPathView';
import { useCriticalPathViewStore } from './useCriticalPathViewStore';

/**
 * Dedicated Critical-Path panel — top-level dockable view, replaces the old in-DAG tape.
 *
 * **Tick source.** Same model as the System DAG aggregation range: read
 * `useProfilerViewStore.viewRange` (committed slot — debounced upstream so we don't recompute on
 * every gesture frame), convert to ticks, then ask {@link dominantTickInRange} for the longest
 * tick in window, with midpoint-fallback for sub-tick zoom. This keeps the four visible panels —
 * profiler / DAG / critical-path / detail — consistent on what "the current window" means.
 *
 * Pre-#345 had a separate `focusTick` cross-panel slot that overrode the dominant-tick
 * computation. That's gone: pinning a specific tick now means snapping the viewport to that
 * tick's bounds (click in TickOverview or on a CP bar) — same outcome, one less concept.
 *
 * **Composition.** Toolbar + zoomable view. The view owns its scroll viewport and SVG canvas; the
 * panel only feeds it data and forwards the click selection. `fitSignal` is an increment counter
 * the toolbar / `0` keybind bumps whenever the user wants the timeline to refit.
 */
export default function CriticalPathPanel(_props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { data: topology, isLoading: topoLoading, isError: topoError } = useTopology(sessionId);
  const { data: metadata, isLoading: metaLoading } = useProfilerMetadata(sessionId);

  // Post-#345: time-window canonical source is the profiler view store. Reading the *committed*
  // slot so CP only re-computes after the user stops scrubbing.
  const viewRange = useProfilerViewStore((s) => s.viewRange);
  const commitViewRange = useProfilerViewStore((s) => s.commitViewRange);
  const range = useMemo(() => timeToTickRange(viewRange, metadata?.tickSummaries), [viewRange, metadata]);

  const selectedSystemName = useSelectionStore((s) => s.system);
  const setSystem = useSelectionStore((s) => s.setSystem);

  const tickSummaries = metadata?.tickSummaries ?? null;
  // Display tick = dominant tick in the visible range, with midpoint-fallback for sub-tick zoom.
  // `dominantTickInRange` (extended in #345 with the optional third `time` arg) absorbs the
  // previous `focusTickForWindow` semantics, so the CP tape keeps tracking whatever tick the user
  // is zoomed into even when the viewport is narrower than a tick. With `focusTick` deleted, the
  // user pins a tick by clicking it in TickOverview — which now snaps viewRange to that tick's
  // bounds, making it the unique tick in range and thus the dominant one trivially.
  const tapeTick = useMemo(
    () => dominantTickInRange(tickSummaries, range, viewRange),
    [tickSummaries, range, viewRange],
  );

  // Worker count drives the worker-occupancy ribbon (§5.5). Coerced once at the boundary; the
  // ribbon is meaningful even on a single-worker trace, so the floor is 1.
  const workerCount = useMemo(() => {
    const raw = metadata?.header?.workerCount;
    if (raw == null) return null;
    const n = typeof raw === 'number' ? raw : Number(raw);
    return Number.isFinite(n) && n >= 1 ? n : null;
  }, [metadata]);

  const derivedEdges = useMemo(
    () => (topology?.systems ? deriveEdges(topology.systems) : []),
    [topology],
  );

  // In aggregate mode the displayed bars are means across the selected tick range — bypass the
  // dominant-tick selector entirely. Single-tick (default) keeps the existing behaviour.
  const aggregateMode = useCriticalPathViewStore((s) => s.aggregateMode);
  const bars = useMemo(() => {
    if (!topology?.systems || !metadata) return null;
    if (aggregateMode) {
      return computeAggregateCriticalPath({
        systems: topology.systems,
        rows: metadata.systemTickSummaries ?? [],
        edges: derivedEdges,
        phases: topology.phases ?? [],
        postTickRows: metadata.postTickSummaries ?? [],
        tickSummaries: metadata.tickSummaries ?? [],
        range,
      });
    }
    if (tapeTick == null) return null;
    const tickRow = (metadata.tickSummaries ?? []).find((t) => Number(t.tickNumber) === tapeTick) ?? null;
    return computeCriticalPathForTick({
      tickNumber: tapeTick,
      systems: topology.systems,
      rows: metadata.systemTickSummaries ?? [],
      edges: derivedEdges,
      phases: topology.phases ?? [],
      postTickRows: metadata.postTickSummaries ?? [],
      tickSummaryRow: tickRow,
    });
  }, [aggregateMode, tapeTick, topology, metadata, derivedEdges, range]);

  // Fit signal — increments per "Fit" press / `0` keybind / middle-click / auto-fit. View
  // watches and recomputes pxPerUs.
  const [fitSignal, setFitSignal] = useState(0);
  const requestFit = () => setFitSignal((n) => n + 1);

  // Auto-fit on selection change — every time the displayed tick changes (single mode) or the
  // aggregate-mode toggle flips (the totalUs goes from one tick to a range mean), refit so the
  // new wall-clock total fills the viewport. Without this, the persisted `pxPerUs` from a
  // different tick / mode leaves the view either empty or overflowing. The `lockZoom` toggle in
  // the toolbar disables this so power users can compare phases / systems across ticks at the
  // same scale.
  const lockZoom = useCriticalPathViewStore((s) => s.lockZoom);
  useEffect(() => {
    if (lockZoom) return;
    if (!bars) return;
    requestFit();
    // requestFit identity is stable enough — it's a closure over the local setter — but we don't
    // include it in deps to avoid re-firing for unrelated reasons. Only the displayed bars and
    // the `lockZoom` flag should drive auto-fit.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bars, lockZoom]);

  // Keyboard zoom: `+`/`=` zoom in, `-` zoom out, `0` fit. Listens on the whole panel container
  // so it works wherever the user clicks inside it. Doesn't fight inputs because there are none.
  const zoomBy = useCriticalPathViewStore((s) => s.zoomBy);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;
      if (e.key === '+' || e.key === '=') {
        e.preventDefault();
        zoomBy(1.25);
      } else if (e.key === '-' || e.key === '_') {
        e.preventDefault();
        zoomBy(1 / 1.25);
      } else if (e.key === '0') {
        e.preventDefault();
        requestFit();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [zoomBy]);

  if (!sessionId) {
    return <EmptyState message="No session attached. Open a trace or attach to a live engine to see the critical path." />;
  }
  if (topoError) {
    return <EmptyState message="Topology fetch failed — check the server log." tone="error" />;
  }
  if (topoLoading || metaLoading || !topology || !metadata) {
    return <EmptyState message="Loading topology…" />;
  }
  if (!bars) {
    return (
      <div className="flex h-full w-full flex-col overflow-hidden bg-background">
        <CriticalPathToolbar bars={null} onFit={requestFit} />
        <EmptyState message="Snapshot or scrub the profiler to populate the view — and pick a focus tick by clicking a system on the DAG." />
      </div>
    );
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <CriticalPathToolbar bars={bars} onFit={requestFit} />
      <div className="min-h-0 flex-1">
        <CriticalPathView
          bars={bars}
          selectedSystemName={selectedSystemName}
          fitSignal={fitSignal}
          onFit={requestFit}
          workerCount={workerCount}
          onSelectBar={(name, tickNumber) => {
            setSystem(name);
            // Pin the clicked tick by snapping viewRange to its bounds — replaces the pre-#345
            // `setFocusTick(tickNumber)`. CP's `dominantTickInRange(viewRange)` then lands on
            // this tick because it's the only one in range. Looking up the tick row is cheap
            // (linear scan, N ≤ a few thousand; the strict-path of dominantTickInRange does the
            // same shape of scan).
            const row = (tickSummaries ?? []).find((t) => Number(t.tickNumber) === tickNumber);
            if (row) {
              const startUs = Number(row.startUs);
              const dur = Number(row.durationUs) || 0;
              commitViewRange({ startUs, endUs: startUs + Math.max(dur, 1) });
            }
          }}
        />
      </div>
    </div>
  );
}

function EmptyState({ message, tone = 'muted' }: { message: string; tone?: 'muted' | 'error' }) {
  const colour = tone === 'error' ? 'text-destructive' : 'text-muted-foreground';
  return (
    <div className={`flex h-full w-full items-center justify-center bg-background text-[12px] ${colour}`}>
      {message}
    </div>
  );
}
