import { useEffect, useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useTopology } from '@/hooks/data/useTopology';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { deriveEdges } from '@/lib/dag/edgeDerivation';
import { timeToTickRange } from '../SystemDag/tickRangeMapping';
import { computeAggregateCriticalPath, computeCriticalPathForTick, focusTickForWindow } from './criticalPath';
import CriticalPathToolbar from './CriticalPathToolbar';
import CriticalPathView from './CriticalPathView';
import { useCriticalPathViewStore } from './useCriticalPathViewStore';

/**
 * Dedicated Critical-Path panel — top-level dockable view, replaces the old in-DAG tape.
 *
 * **Tick source.** Same model as the System DAG aggregation range: read `useSelectionStore.time`
 * (cross-panel-bound to the profiler's TimeArea), convert to ticks, then either honour the
 * user-pinned `focusTick` or default to the dominant tick in the range. This keeps the four
 * visible panels — profiler / DAG / critical-path / detail — consistent on what "the current
 * window" means.
 *
 * **Composition.** Toolbar + zoomable view. The view owns its scroll viewport and SVG canvas; the
 * panel only feeds it data and forwards the click selection. `fitSignal` is an increment counter
 * the toolbar / `0` keybind bumps whenever the user wants the timeline to refit.
 */
export default function CriticalPathPanel(_props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { data: topology, isLoading: topoLoading, isError: topoError } = useTopology(sessionId);
  const { data: metadata, isLoading: metaLoading } = useProfilerMetadata(sessionId);

  const time = useSelectionStore((s) => s.time);
  const range = useMemo(() => timeToTickRange(time, metadata?.tickSummaries), [time, metadata]);

  const focusTick = useSelectionStore((s) => s.focusTick);
  const setFocusTick = useSelectionStore((s) => s.setFocusTick);
  const selectedSystemName = useSelectionStore((s) => s.system);
  const setSystem = useSelectionStore((s) => s.setSystem);

  const tickSummaries = metadata?.tickSummaries ?? null;
  // Fall back to `focusTickForWindow` (not `dominantTickInRange`) so that zooming the TimeArea
  // inside a single tick still lands on that tick — `timeToTickRange` correctly returns null for
  // sub-tick windows ("tick startUs in window" is the right semantic for SystemDag aggregations),
  // and the helper carries a midpoint fallback for the focus-tick case. Keeps the user's "I'm
  // looking at tick X" mental model intact across zoom gestures even when they didn't explicitly
  // pin focusTick.
  const tapeTick = useMemo(() => {
    if (focusTick != null) return focusTick;
    return focusTickForWindow(tickSummaries, range, time);
  }, [focusTick, tickSummaries, range, time]);

  // Worker count drives the per-phase parallelism band (A2). Coerced once at the boundary so
  // the view can render the band unconditionally and bail on null inside.
  const workerCount = useMemo(() => {
    const raw = metadata?.header?.workerCount;
    if (raw == null) return null;
    const n = typeof raw === 'number' ? raw : Number(raw);
    return Number.isFinite(n) && n >= 2 ? n : null;
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
            setFocusTick(tickNumber);
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
