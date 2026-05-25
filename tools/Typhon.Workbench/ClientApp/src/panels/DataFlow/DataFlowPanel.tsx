import { useEffect, useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import type { SystemArchetypeTouchSummary } from '@/api/generated/model/systemArchetypeTouchSummary';
import { useTopology } from '@/hooks/data/useTopology';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { REFRESH_DEBOUNCE_MS, useDebouncedValue, useTickGatedSnapshot } from '@/hooks/useTickGatedSnapshot';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { selectEffectiveScope, useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { timeToTickRange } from '@/panels/SystemDag/tickRangeMapping';
import { dominantTickInRange } from '@/panels/CriticalPath/criticalPath';
import { deriveEdges } from '@/lib/dag/edgeDerivation';
import { computeGatingAnalysis } from '@/lib/dag/gatingAnalysis';
import { useGatingStore } from '@/stores/useGatingStore';
import { type Bar, buildBars, buildDensityCells, buildEnvelopeBars, type DensityCell } from './barBuilding';
import { applyPhaseCollapse, computePhaseLayout, type PhaseAxis, type PhaseSegment } from './phaseLayout';
import { buildTracks, trackToDataTrackSelection } from './trackBuilding';
import { findTickRangeSlice } from './tickRangeFilter';
import { type XAxisMode, useDataFlowViewStore } from './useDataFlowViewStore';
import DataFlowTimeline from './DataFlowTimeline';
import DataFlowToolbar from './DataFlowToolbar';
import DataFlowSidePanel from './DataFlowSidePanel';
import DataFlowMatrix from './DataFlowMatrix';
import { type BarTickStats } from './DataFlowTooltip';

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
  const { data: liveMetadata } = useProfilerMetadata(sessionId);
  // Tick-gated debounced snapshot — recompute the heavy memos below only when the latest tick
  // advances on the live stream. Other metadata mutations (thread info, chunk manifest, global
  // metrics) are intentionally ignored. Post-#345: the user-pan/zoom portion is gone — viewRange
  // is already debounced upstream, so we read it directly here.
  const latestTickNumber = useProfilerSessionStore((s) => s.latestTickNumber);
  const refreshKey = `${latestTickNumber}`;
  const debouncedRefreshKey = useDebouncedValue(refreshKey, REFRESH_DEBOUNCE_MS);
  const metadata = useTickGatedSnapshot(liveMetadata, debouncedRefreshKey);
  // Committed slot — upstream debounce already coalesces pan/zoom bursts. Resolved through the link/unlink scope
  // (3B): follows the global window when linked, the frozen `pinnedRange` when unlinked.
  const time = useProfilerViewStore(selectEffectiveScope);
  const mode = useDataFlowViewStore((s) => s.mode);
  const granularityLevel = useDataFlowViewStore((s) => s.granularityLevel);
  const xMode = useDataFlowViewStore((s) => s.xMode);
  const aggMode = useDataFlowViewStore((s) => s.aggMode);
  const collapsedPhases = useDataFlowViewStore((s) => s.collapsedPhases);
  const manuallyExpandedPhases = useDataFlowViewStore((s) => s.manuallyExpandedPhases);
  const cyclePhaseCollapse = useDataFlowViewStore((s) => s.cyclePhaseCollapse);
  const hideUntouched = useDataFlowViewStore((s) => s.hideUntouched);
  const dimSkipped = useDataFlowViewStore((s) => s.dimSkipped);
  const hoverIsolateEnabled = useDataFlowViewStore((s) => s.hoverIsolateEnabled);
  const setHoverIsolateEnabled = useDataFlowViewStore((s) => s.setHoverIsolateEnabled);
  const setMode = useDataFlowViewStore((s) => s.setMode);
  const setXMode = useDataFlowViewStore((s) => s.setXMode);
  const clearSelection = useSelectionStore((s) => s.clear);

  const selectedSystem = useSelectionStore((s) => s.system);
  const setSelectedSystem = useSelectionStore((s) => s.setSystem);
  // Phase D (#327): track-row clicks write to the cross-panel dataTrack slot; the AccessMatrix highlights the
  // matching row and the System DAG halos the matching systems.
  const dataTrack = useSelectionStore((s) => s.dataTrack);
  const setDataTrack = useSelectionStore((s) => s.setDataTrack);
  // Phase D (#327): hover broadcast — when a bar is hovered, mirror the (systemName, tickNumber) into the store
  // so AccessMatrix can brighten the matching column and System DAG the matching node. Replaces the local-only
  // hover state from Phase B.
  const setHoveredKey = useSelectionStore((s) => s.setHoveredSystemTickKey);
  const sharedHoveredKey = useSelectionStore((s) => s.hoveredSystemTickKey);

  // Tick range for the X axis. When the user has dragged a time selection in the Profiler tick overview,
  // `timeToTickRange` returns the matching tick window; otherwise we fall back to the trace's full extent so
  // the timeline still renders a meaningful x-axis (without this fallback, uPlot's `setData` short-circuits on
  // `null`, leaving the x-scale at the [0, 1] placeholder from initial mount and clipping every bar).
  const tickRange = useMemo(() => {
    const fromTime = timeToTickRange(time, metadata?.tickSummaries);
    if (fromTime) return fromTime;
    const summaries = metadata?.tickSummaries;
    if (!summaries || summaries.length === 0) return null;
    const fromTick = numericTick(summaries[0]?.tickNumber);
    const toTick = numericTick(summaries[summaries.length - 1]?.tickNumber);
    if (fromTick == null || toTick == null) return null;
    return { from: fromTick, to: toTick };
  }, [time, metadata]);

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

  // Single-tick replay (spec D8): pick the dominant tick in the visible range. For envelope/density modes
  // we still want a representative tick to drive phaseAxis (tickPhaseSpans) — the dominant tick gives a
  // stable, meaningful sample rather than always tick 0.
  const dominantTick = useMemo(
    () => dominantTickInRange(metadata?.tickSummaries ?? null, tickRange),
    [metadata?.tickSummaries, tickRange],
  );

  // Build the PhaseAxis: per-phase wall-clock contribution (totalled across the visible range, drives `uniform`/`log`
  // modes) + per-tick phase span (drives bar X positioning inside each segment). Computed from
  // SystemTickSummary[] joined with the system→phase map; expensive enough to memo, cheap enough to recompute on
  // every range / topology change without throttling.
  const phaseAxis: PhaseAxis = useMemo(() => {
    const phaseNames = topology?.phases ?? [];
    if (phaseNames.length === 0) {
      return { segments: [], tickPhaseSpans: new Map() };
    }
    // System index → phase name lookup. Same map barBuilding builds, but we need it here too.
    const sysIdxToPhase = new Map<number, string>();
    for (const s of topology?.systems ?? []) {
      const idx = typeof s.index === 'number' ? s.index : Number(s.index);
      if (!Number.isFinite(idx) || !s.phaseName) continue;
      sysIdxToPhase.set(idx, s.phaseName);
    }

    // Walk SystemTickSummary[] within the tick range. For each (tick, phase): track min(startUs)/max(endUs).
    // Sum endUs - startUs into per-phase wall-clock totals (drives uniform/log mode).
    const tickPhaseSpans = new Map<number, Map<string, { startUs: number; endUs: number }>>();
    const phaseWallClock = new Map<string, number>();
    for (const name of phaseNames) phaseWallClock.set(name, 0);
    const summaries = (metadata?.systemTickSummaries ?? []) as unknown as readonly {
      tickNumber: number; systemIndex: number; startUs: number; durationUs: number;
    }[];
    for (const r of summaries) {
      const tick = typeof r.tickNumber === 'number' ? r.tickNumber : Number(r.tickNumber);
      if (!Number.isFinite(tick)) continue;
      if (tickRange && (tick < tickRange.from || tick > tickRange.to)) continue;
      const sysIdx = typeof r.systemIndex === 'number' ? r.systemIndex : Number(r.systemIndex);
      const phase = sysIdxToPhase.get(sysIdx);
      if (!phase) continue;
      const startUs = Number(r.startUs);
      const durUs = Number(r.durationUs);
      if (!Number.isFinite(startUs) || !Number.isFinite(durUs) || durUs < 0) continue;
      const endUs = startUs + durUs;
      let bucket = tickPhaseSpans.get(tick);
      if (!bucket) {
        bucket = new Map();
        tickPhaseSpans.set(tick, bucket);
      }
      const span = bucket.get(phase);
      if (span) {
        if (startUs < span.startUs) span.startUs = startUs;
        if (endUs > span.endUs) span.endUs = endUs;
      } else {
        bucket.set(phase, { startUs, endUs });
      }
    }
    // Wall-clock per phase = sum across visible ticks of (phaseEnd − phaseStart). This double-counts overlaps
    // between phases that ran in parallel (shouldn't happen — phases run sequentially per the runtime — but defensive).
    for (const [, perPhase] of tickPhaseSpans) {
      for (const [phase, span] of perPhase) {
        const cur = phaseWallClock.get(phase) ?? 0;
        phaseWallClock.set(phase, cur + Math.max(0, span.endUs - span.startUs));
      }
    }

    // Compute base segments via phaseLayout (uniform / equal / log), then layer manual + auto collapse.
    const baseSegments: PhaseSegment[] = computePhaseLayout(
      phaseNames.map((name) => ({ name, wallClockUs: phaseWallClock.get(name) ?? 0 })),
      xMode,
    );
    let totalWallClock = 0;
    for (const w of phaseWallClock.values()) totalWallClock += w;
    const collapsedSet = new Set(collapsedPhases);
    const expandedSet = new Set(manuallyExpandedPhases);
    const segments = applyPhaseCollapse(baseSegments, totalWallClock, collapsedSet, expandedSet);

    return { segments, tickPhaseSpans };
  }, [topology?.phases, topology?.systems, metadata?.systemTickSummaries, tickRange, xMode, collapsedPhases, manuallyExpandedPhases]);

  // Bars — fan out (system, archetype) events across the relevant tracks at this granularity. Replay emits the
  // dominant tick's bars (spec D8); envelope + density first build the full-range bar set, then aggregate.
  const bars = useMemo<readonly Bar[]>(() => {
    const summaries = metadata?.systemTickSummaries as readonly {
      tickNumber: number; systemIndex: number; startUs: number; durationUs: number;
    }[] | undefined;
    const ticks = metadata?.tickSummaries ?? undefined;
    if (aggMode === 'replay') {
      return buildBars(touchesSlice, tracks, topology ?? null, granularityLevel, summaries, ticks, phaseAxis, dominantTick);
    }
    // For envelope: build replay bars across ALL ticks in the range, then collapse to per-(track, system) p5/p95 bars.
    const allReplay = buildBars(touchesSlice, tracks, topology ?? null, granularityLevel, summaries, ticks, phaseAxis, null);
    if (aggMode === 'envelope') return buildEnvelopeBars(allReplay);
    // density: bars list is empty; the heatmap is fed via the densityCells prop instead. Returning [] disables
    // the regular bar draw pass, leaving the renderer to paint density cells as a separate primitive.
    return [];
  }, [aggMode, touchesSlice, tracks, topology, granularityLevel, metadata?.systemTickSummaries, metadata?.tickSummaries, phaseAxis, dominantTick]);

  // Density cells for the heat strip — only computed when in density mode, otherwise an empty array (cheap memo).
  const densityCells = useMemo<readonly DensityCell[]>(() => {
    if (aggMode !== 'density') return [];
    const summaries = metadata?.systemTickSummaries as readonly {
      tickNumber: number; systemIndex: number; startUs: number; durationUs: number;
    }[] | undefined;
    const ticks = metadata?.tickSummaries ?? undefined;
    const allReplay = buildBars(touchesSlice, tracks, topology ?? null, granularityLevel, summaries, ticks, phaseAxis, null);
    return buildDensityCells(allReplay);
  }, [aggMode, touchesSlice, tracks, topology, granularityLevel, metadata?.systemTickSummaries, metadata?.tickSummaries, phaseAxis]);

  // Filter chips: hide tracks with zero bars when `hideUntouched` is on. Keep at least one track so the
  // canvas isn't empty when nothing matched (avoids the user thinking the panel is broken).
  const visibleTracks = useMemo(() => {
    if (!hideUntouched) return tracks;
    const touched = new Set<string>();
    for (const b of bars) touched.add(b.trackId);
    const filtered = tracks.filter((t) => touched.has(t.id));
    return filtered.length === 0 ? tracks : filtered;
  }, [tracks, bars, hideUntouched]);

  // Build the (system, tick) → skipped lookup when the user has the `dimSkipped` filter on. Match the
  // SystemTickSummary's skipReason (non-zero ⇒ system was skipped this tick); the renderer dims those bars.
  const skippedKeys = useMemo(() => {
    if (!dimSkipped) return null;
    const set = new Set<string>();
    const sysIdxToName = new Map<number, string>();
    for (const s of topology?.systems ?? []) {
      const idx = typeof s.index === 'number' ? s.index : Number(s.index);
      if (Number.isFinite(idx) && s.name) sysIdxToName.set(idx, s.name);
    }
    const summaries = (metadata?.systemTickSummaries ?? []) as unknown as readonly {
      tickNumber: number; systemIndex: number; skipReasonCode: number;
    }[];
    for (const r of summaries) {
      const code = Number(r.skipReasonCode);
      if (!code) continue;
      const name = sysIdxToName.get(Number(r.systemIndex));
      if (!name) continue;
      set.add(`${name}|${Number(r.tickNumber)}`);
    }
    return set;
  }, [dimSkipped, topology?.systems, metadata?.systemTickSummaries]);

  const phaseSegments = phaseAxis.segments;

  // X-axis label formatter — converts a normalized [0, 1] phase-space x back to a human-readable time. In
  // replay mode this resolves to "<phase> · <µs/ms> from tick start"; in envelope/density modes it falls back
  // to the phase name only (the position no longer has a single-tick µs interpretation). Memoized so the
  // closure passes object-equality between renders when inputs are unchanged — uPlot re-creates labels on
  // every redraw and we don't want to thrash GC.
  const formatXLabel = useMemo(() => {
    return (x01: number): string => {
      // Find the phase segment containing x. Linear scan — typical phase counts are < 8.
      let seg = null;
      for (const s of phaseAxis.segments) {
        if (x01 >= s.xStart && x01 <= s.xEnd) { seg = s; break; }
      }
      if (!seg) return '';
      const segWidth = seg.xEnd - seg.xStart;
      if (segWidth <= 0) return seg.name; // collapsed strip — show only the phase name
      const intra = (x01 - seg.xStart) / segWidth;
      if (aggMode === 'replay' && dominantTick != null) {
        const span = phaseAxis.tickPhaseSpans.get(dominantTick)?.get(seg.name);
        if (span && span.endUs > span.startUs) {
          const us = span.startUs + intra * (span.endUs - span.startUs);
          return formatUsLabel(us);
        }
      }
      // Fallback: show "phase · NN%" which still anchors the position relative to the phase column.
      return `${seg.name} ${(intra * 100).toFixed(0)}%`;
    };
  }, [phaseAxis, aggMode, dominantTick]);

  // Pre-build (system,tick) → SystemTickSummary index for the tooltip's per-bar stats lookup. Linear scan is
  // fine; the index is the cost-amortizing structure since the tooltip queries it on every hover.
  const barStatsByKey = useMemo(() => {
    const sysIdxToName = new Map<number, string>();
    for (const s of topology?.systems ?? []) {
      const idx = typeof s.index === 'number' ? s.index : Number(s.index);
      if (Number.isFinite(idx) && s.name) sysIdxToName.set(idx, s.name);
    }
    const summaries = (metadata?.systemTickSummaries ?? []) as unknown as readonly {
      tickNumber: number; systemIndex: number; startUs: number; endUs: number; durationUs: number;
      entitiesProcessed: number; workersTouched: number; chunksProcessed: number; skipReasonCode: number;
    }[];
    const m = new Map<string, BarTickStats>();
    for (const r of summaries) {
      const name = sysIdxToName.get(Number(r.systemIndex));
      if (!name) continue;
      const tick = Number(r.tickNumber);
      m.set(`${name}|${tick}`, {
        startUs: Number(r.startUs),
        endUs: Number(r.endUs),
        durationUs: Number(r.durationUs),
        entitiesProcessed: Number(r.entitiesProcessed) || 0,
        workersTouched: Number(r.workersTouched) || 0,
        chunksProcessed: Number(r.chunksProcessed) || 0,
        skipped: Number(r.skipReasonCode) !== 0,
      });
    }
    return m;
  }, [topology?.systems, metadata?.systemTickSummaries]);

  const tickDurationByNumber = useMemo(() => {
    const m = new Map<number, number>();
    for (const t of metadata?.tickSummaries ?? []) {
      const n = Number(t.tickNumber);
      const d = Number(t.durationUs);
      if (Number.isFinite(n) && Number.isFinite(d)) m.set(n, d);
    }
    return m;
  }, [metadata?.tickSummaries]);

  // Compute gating client-side as a fallback when SystemDag isn't open. Both panels compute independently;
  // a fingerprint check on the shared store avoids redundant writes. Inputs are stable across the panels
  // (same topology + summaries + range), so when both panels are open they'll generally produce the same
  // map — last writer wins, which is fine since the result is deterministic.
  const gatingFingerprint = useDataFlowGatingFingerprint(metadata?.fingerprint, tickRange);
  const setGatingStore = useGatingStore((s) => s.setGating);
  const gatingStoreFingerprint = useGatingStore((s) => s.fingerprint);
  useEffect(() => {
    if (!topology?.systems || !metadata?.systemTickSummaries || !gatingFingerprint) return;
    if (gatingStoreFingerprint === gatingFingerprint) return;
    const edges = deriveEdges(topology.systems);
    const result = computeGatingAnalysis({
      systems: topology.systems,
      rows: metadata.systemTickSummaries,
      edges,
      range: tickRange,
    });
    setGatingStore(result, gatingFingerprint);
  }, [topology?.systems, metadata?.systemTickSummaries, tickRange, gatingFingerprint, gatingStoreFingerprint, setGatingStore]);

  // Phase D (#327): hover state is now shared via useSelectionStore.hoveredSystemTickKey. The local Bar object
  // is still kept (so the side panel has the full record details) but the (systemName, tickNumber) key flows
  // through the store for cross-panel reactivity.
  const [hoveredBar, setHoveredBar] = useState<Bar | null>(null);
  const [selectedBar, setSelectedBar] = useState<Bar | null>(null);

  // F-key fit-to-selection (spec §11.4): increment a token, the timeline reacts by clearing its wheel-zoom.
  const [fitToken, setFitToken] = useState(0);

  // Resolve the "isolate" key from the cross-panel hover store. Falls back to the local hovered bar so isolate
  // works even when the hover originates inside this panel (the most common case).
  const hoverIsolate = useMemo(() => {
    if (!hoverIsolateEnabled) return null;
    if (sharedHoveredKey) return sharedHoveredKey;
    if (hoveredBar) return { systemName: hoveredBar.systemName, tickNumber: hoveredBar.tickNumber };
    return null;
  }, [hoverIsolateEnabled, sharedHoveredKey, hoveredBar]);

  // Keyboard shortcuts per design §11.4. Listeners are global (window-level) so they work regardless of
  // which child element is focused, but we exit early when the user is typing in an input/textarea so
  // typing 'h' or '1' in the search box doesn't trigger panel actions.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      const active = document.activeElement;
      if (active && (active.tagName === 'INPUT' || active.tagName === 'TEXTAREA')) return;

      // View mode: [ → Timeline, ] → Matrix (stage-3 §3 Phase 2 — the two renderings of one dataset). Granularity
      // moved to the toolbar selector when these keys were repurposed for the mode toggle.
      if (e.key === '[' || e.key === ']') {
        setMode(e.key === '[' ? 'timeline' : 'matrix');
        return;
      }

      // X-axis mode: 1=uniform, 2=equal, 3=log. Number-row keys.
      if (e.key === '1' || e.key === '2' || e.key === '3') {
        const map: Record<string, XAxisMode> = { '1': 'uniform', '2': 'equal', '3': 'log' };
        const next = map[e.key];
        if (next && next !== xMode) setXMode(next);
        return;
      }

      // Esc clears every cross-panel selection slot. Strong default — users coming from Phase B's local-only
      // hover toggle expect Esc to be a "stop showing me anything" reset.
      if (e.key === 'Escape') {
        clearSelection();
        return;
      }

      // H toggles hover-isolate (existing Phase B shortcut, retained).
      if (e.key === 'h' || e.key === 'H') {
        setHoverIsolateEnabled(!hoverIsolateEnabled);
        return;
      }

      // F fits the X axis to the current selection range — clears any wheel zoom (spec §11.4).
      if (e.key === 'f' || e.key === 'F') {
        setFitToken((n) => n + 1);
        return;
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [xMode, hoverIsolateEnabled, setMode, setXMode, setHoverIsolateEnabled, clearSelection]);

  function onBarHover(key: { systemName: string; tickNumber: number } | null) {
    // Phase D (#327): mirror to the cross-panel store so AccessMatrix + System DAG react. Local Bar object
    // is still kept so the side panel can show full detail without re-scanning bars.
    setHoveredKey(key);
    if (!key) {
      setHoveredBar(null);
      return;
    }
    const found = bars.find((b) => b.systemName === key.systemName && b.tickNumber === key.tickNumber);
    setHoveredBar(found ?? null);
  }

  function onBarClick(systemName: string) {
    setSelectedSystem(systemName);
    if (hoveredBar) setSelectedBar(hoveredBar);
  }

  function onTrackClick(trackId: string) {
    // Phase D (#327): row label click → cross-panel dataTrack selection. Toggle off when clicking the
    // already-selected row so users can clear with a second click.
    if (dataTrack && dataTrack.id === trackId) {
      setDataTrack(null);
      return;
    }
    const track = tracks.find((t) => t.id === trackId);
    if (!track) return;
    const projection = trackToDataTrackSelection(track);
    if (!projection) return;
    setDataTrack(projection);
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
    <div
      className="flex h-full w-full flex-col overflow-hidden bg-background text-foreground"
      // Mirror the cross-panel hover key as a data attribute so e2e tests can assert hover propagation
      // without depending on whichever consumer panel (Access Matrix, System DAG) happens to be visible.
      // dockview unmounts inactive tabs and switching tabs would clear `hoveredSystemTickKey` via the
      // canvas' mouseleave; observing it on the always-mounted Data Flow panel sidesteps that.
      data-testid="data-flow-panel-root"
      data-mode={mode}
      data-hovered-system={sharedHoveredKey?.systemName ?? ''}
    >
      <DataFlowToolbar />
      {mode === 'matrix' ? (
        // Matrix mode — the absorbed Access Matrix, fed the same topology / granularity / touch slice as the
        // Timeline so the two never diverge. Selection is on the bus → switching back keeps the highlight.
        <DataFlowMatrix topology={topology} granularityLevel={granularityLevel} touchSlice={touchesSlice} />
      ) : (
        // @container marks this row a Tailwind v4 container; the side panel disappears when the panel itself is
        // below 720 px wide so the timeline canvas (already minus 180 px of track labels) doesn't get squeezed
        // to a sliver. The user can widen the panel by dragging the splitter to bring the side panel back.
        <div className="@container flex min-h-0 min-w-0 flex-1 flex-row">
          <div className="min-w-0 flex-1">
            <DataFlowTimeline
              tracks={visibleTracks}
              bars={bars}
              densityCells={densityCells}
              phaseSegments={phaseSegments}
              systems={topology.systems ?? []}
              hoverIsolate={hoverIsolate}
              selectedSystem={selectedSystem}
              selectedTrackId={dataTrack?.id ?? null}
              skippedKeys={skippedKeys}
              topology={topology}
              resolveBarTickStats={(bar) => barStatsByKey.get(`${bar.systemName}|${bar.tickNumber}`) ?? null}
              resolveTickDurationUs={(t) => tickDurationByNumber.get(t) ?? null}
              formatXLabel={formatXLabel}
              onBarClick={onBarClick}
              onBarHover={onBarHover}
              onTrackClick={onTrackClick}
              onPhaseClick={cyclePhaseCollapse}
              fitToken={fitToken}
            />
          </div>
          <div className="hidden w-64 shrink-0 border-l border-border bg-card @[720px]:block">
            <DataFlowSidePanel
              hoveredBar={hoveredBar}
              selectedBar={selectedBar}
              tracks={tracks}
              systems={topology.systems ?? []}
            />
          </div>
        </div>
      )}
    </div>
  );
}

// TickSummary fields ride through the API as numbers but the Orval-generated type is too loose to assert that;
// keep a small coercion helper for the rare string-on-the-wire case.
function numericTick(v: unknown): number | null {
  if (typeof v === 'number' && Number.isFinite(v)) return v;
  if (typeof v === 'string') {
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }
  return null;
}

/** Deterministic key over the inputs that drive gating analysis; used to dedupe writes to the shared store. */
function useDataFlowGatingFingerprint(metadataFingerprint: string | null | undefined, tickRange: { from: number; to: number } | null): string | null {
  if (!metadataFingerprint || !tickRange) return null;
  return `dataflow|${metadataFingerprint}|${tickRange.from}-${tickRange.to}`;
}

/** Format µs into a compact, axis-friendly label. ms when ≥ 1000 µs; integer µs otherwise. */
function formatUsLabel(us: number): string {
  if (!Number.isFinite(us)) return '—';
  if (Math.abs(us) >= 1000) return `${(us / 1000).toFixed(2)} ms`;
  return `${Math.round(us)} µs`;
}
