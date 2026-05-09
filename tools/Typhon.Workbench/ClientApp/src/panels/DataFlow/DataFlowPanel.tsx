import { useEffect, useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import { useTopology } from '@/hooks/data/useTopology';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { timeToTickRange } from '@/panels/SystemDag/tickRangeMapping';
import { type Bar, buildBars } from './barBuilding';
import { computePhaseLayout } from './phaseLayout';
import { buildTracks } from './trackBuilding';
import { findTickRangeSlice } from './tickRangeFilter';
import { useDataFlowViewStore } from './useDataFlowViewStore';
import DataFlowTimeline from './DataFlowTimeline';
import DataFlowToolbar from './DataFlowToolbar';
import DataFlowSidePanel from './DataFlowSidePanel';

/**
 * Data Flow Timeline panel — Workbench Data Flow module Phase B (#327).
 *
 * Marey-style timeline: data tracks on the Y axis, tick time on the X axis, system runs as colored bars
 * on every track they touch. Sibling to the System DAG (which is scheduler-first); this panel is data-first.
 *
 * Composes:
 * - `DataFlowToolbar` — granularity / X-mode / hover-isolate controls
 * - `DataFlowTimeline` — uPlot-backed multi-row bar chart
 * - `DataFlowSidePanel` — right-rail bar/track detail
 *
 * Data flow:
 * 1. `useTopology` + `useProfilerMetadata` — already-cached hooks shared with System DAG; no extra fetches.
 * 2. `metadata.systemArchetypeTouches` — full sparse touch array, server-side fold of the new
 *    `SchedulerSystemArchetypeEvent` wire kind. May be empty for traces that predate Phase A — in that
 *    case the panel renders the row scaffold (tracks list) with zero bars and waits.
 * 3. `useSelectionStore.time` (µs) → `timeToTickRange` → `findTickRangeSlice` → `buildBars`.
 * 4. Click handler mirrors `useSelectionStore.system` so the System DAG node lights up automatically
 *    (existing reverse direction has been wired since #322).
 *
 * Phase D will add additional cross-panel selection slots (`dataTrack`, `phase`, hover broadcast).
 */
export default function DataFlowPanel(_props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { data: topology } = useTopology(sessionId);
  const { data: metadata } = useProfilerMetadata(sessionId);
  const granularityLevel = useDataFlowViewStore((s) => s.granularityLevel);
  const xMode = useDataFlowViewStore((s) => s.xMode);
  const hoverIsolateEnabled = useDataFlowViewStore((s) => s.hoverIsolateEnabled);
  const setHoverIsolateEnabled = useDataFlowViewStore((s) => s.setHoverIsolateEnabled);

  const time = useSelectionStore((s) => s.time);
  const selectedSystem = useSelectionStore((s) => s.system);
  const setSelectedSystem = useSelectionStore((s) => s.setSystem);

  // Tick range for the X axis. Null when no time selection — fall back to "all ticks".
  const tickRange = useMemo(
    () => timeToTickRange(time, metadata?.tickSummaries),
    [time, metadata],
  );

  // Tracks (Y axis) — pure derivation from topology + granularity.
  const tracks = useMemo(
    () => buildTracks(topology ?? null, granularityLevel),
    [topology, granularityLevel],
  );

  // Touches slice — binary-search the sorted array for the visible tick range.
  const touchesSlice = useMemo(() => {
    const all = (metadata?.systemArchetypeTouches ?? []) as SystemArchetypeTouchSummary[];
    if (all.length === 0) return [];
    const slice = findTickRangeSlice(all, tickRange);
    return all.slice(slice.startIdx, slice.endIdx);
  }, [metadata, tickRange]);

  // Bars — fan out (system, archetype) events across the relevant tracks at this granularity.
  const bars = useMemo(
    () => buildBars(touchesSlice, tracks, topology ?? null, granularityLevel),
    [touchesSlice, tracks, topology, granularityLevel],
  );

  // Phase segments for the X axis. Equal-weighted in the absence of per-phase wallclock telemetry —
  // future work plumbs RuntimePhaseSpan totals here so the uniform mode renders proportionally.
  const phaseSegments = useMemo(() => {
    const phases = topology?.phases ?? [];
    return computePhaseLayout(
      phases.map((p) => ({ name: p, wallClockUs: 1 })),  // equal-weight fallback until per-phase wallclock plumbs in
      xMode,
    );
  }, [topology?.phases, xMode]);

  // Local hover state — broadcast through the timeline component for hover-isolate. Component-internal
  // (not in a store) since cross-panel hover broadcasting is Phase D scope.
  const [hoveredBar, setHoveredBar] = useState<Bar | null>(null);
  const [selectedBar, setSelectedBar] = useState<Bar | null>(null);

  // Resolve the "isolate" key from the hovered bar.
  const hoverIsolate = useMemo(() => {
    if (!hoverIsolateEnabled || !hoveredBar) return null;
    return { systemName: hoveredBar.systemName, tickNumber: hoveredBar.tickNumber };
  }, [hoverIsolateEnabled, hoveredBar]);

  // Keyboard shortcut: H toggles hover-isolate (escape hatch per design §11.4).
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      // Only react when this panel is in focus — checking activeElement keeps the shortcut from stealing
      // typing in unrelated text inputs across the workbench.
      const active = document.activeElement;
      if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA')) return;
      if (e.key === 'h' || e.key === 'H') {
        setHoverIsolateEnabled(!hoverIsolateEnabled);
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [hoverIsolateEnabled, setHoverIsolateEnabled]);

  function onBarHover(key: { systemName: string; tickNumber: number } | null) {
    if (!key) {
      setHoveredBar(null);
      return;
    }
    // Map back to the bar object so the side panel has full detail. Linear scan (low N visible).
    const found = bars.find((b) => b.systemName === key.systemName && b.tickNumber === key.tickNumber);
    setHoveredBar(found ?? null);
  }

  function onBarClick(systemName: string) {
    setSelectedSystem(systemName);
    if (hoveredBar) setSelectedBar(hoveredBar);
  }

  // Loading / empty states.
  if (!topology) {
    return (
      <div className="flex h-full w-full items-center justify-center bg-background text-sm text-muted-foreground">
        Loading topology…
      </div>
    );
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background text-foreground">
      <DataFlowToolbar />
      <div className="flex min-h-0 flex-1 flex-row">
        <div className="min-w-0 flex-1">
          <DataFlowTimeline
            tracks={tracks}
            bars={bars}
            tickRange={tickRange ? { from: tickRange.from, to: tickRange.to } : null}
            phaseSegments={phaseSegments}
            systems={topology.systems ?? []}
            hoverIsolate={hoverIsolate}
            selectedSystem={selectedSystem}
            onBarClick={onBarClick}
            onBarHover={onBarHover}
          />
        </div>
        <div className="w-64 shrink-0 border-l border-border bg-card">
          <DataFlowSidePanel
            hoveredBar={hoveredBar}
            selectedBar={selectedBar}
            tracks={tracks}
            systems={topology.systems ?? []}
          />
        </div>
      </div>
    </div>
  );
}
