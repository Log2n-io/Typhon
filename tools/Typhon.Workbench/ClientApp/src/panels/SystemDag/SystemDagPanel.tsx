import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useTopology } from '@/hooks/data/useTopology';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { useGatingStore } from '@/stores/useGatingStore';
import { selectEffectiveScope, useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import {
  computeCriticalPathForTick,
  computeCriticalPathParticipation,
  computeSystemSkipRates,
  dominantTickInRange,
} from '../CriticalPath/criticalPath';
import { toNodeData, resolveNoAccessReason } from './dagModel';
import { resolveSystemsForDataTrack } from './dataTrackHighlight';
import { deriveEdges } from '@/lib/dag/edgeDerivation';
import { computeGatingAnalysis } from '@/lib/dag/gatingAnalysis';
import SystemDagCanvas from './SystemDagCanvas';
import SystemDagSidePanel from './SystemDagSidePanel';
import SystemDagToolbar from './SystemDagToolbar';
import { buildQueryCountsBySystem, buildSingleOwnedDefBySystem } from './queryCounts';
import { timeToTickRange } from './tickRangeMapping';
import { useDagViewStore } from './useDagViewStore';
import { useQueueBackpressure } from './useQueueBackpressure';
import { useSystemStats } from './useSystemStats';
import { useQueryDefinitions } from '../QueryCatalog/useQueryDefinitions';

/**
 * System DAG view — Phase 1 + Phase 2 (#315 + #316).
 *
 * Phase 1 shipped: topology-only canvas (phase swim-lanes, derived edges, declared access on click).
 *
 * Phase 2 (this file): Tier 1 node colouring driven by /aggregate over per-system tracks. Toolbar
 * adds a "Snapshot last N ticks" pin and a stat-mode selector (mean / p50 / p95 / p99 / max). The
 * panel auto-snapshots once on first metadata arrival so a fresh open shows useful colour without
 * a click. Cross-panel TimeArea binding (Phase 2 final per design) is deferred — the panel owns
 * the range until that wiring lands in a follow-up.
 *
 * Selection mirrors to {@link useSelectionStore.system} as before; reverse direction (other panel
 * sets the system slot) opens the side panel here.
 */
export default function SystemDagPanel(_props: IDockviewPanelProps) {
  const sessionId = useSessionStore((s) => s.sessionId);
  const { data: topology, isLoading, isError } = useTopology(sessionId);
  // Profiler metadata gives us the tick → µs mapping for the cross-panel binding plus the inputs
  // for the CP / skip-rate algorithms. Shared TanStack cache with the profiler panel — no
  // duplicate fetch.
  const { data: metadata } = useProfilerMetadata(sessionId);
  const statMode = useDagViewStore((s) => s.statMode);

  // Cross-panel binding (#345): the tick range comes from `useProfilerViewStore.viewRange` (the
  // *committed* slot — debounced upstream so pan/zoom doesn't re-trigger the DAG's heavy
  // aggregations on every gesture frame). The profiler's TimeArea writes the transient slot
  // during gesture and commits on settle, so scrubbing only kicks DAG aggregations once.
  //
  // Conversion µs → tick happens at the panel boundary; downstream hooks (useSystemStats /
  // useQueueBackpressure / CP / skip-rate) all take TickRange and stay tick-native.
  // Resolved through the link/unlink scope (3B): follows the global window when linked, the frozen window when unlinked.
  const viewRange = useProfilerViewStore(selectEffectiveScope);
  const range = useMemo(
    () => timeToTickRange(viewRange, metadata?.tickSummaries),
    [viewRange, metadata],
  );

  const selectedSystemName = useSelectionStore((s) => s.system);
  const setSystem = useSelectionStore((s) => s.setSystem);
  // Phase D (#327): cross-panel selection slots. The DAG reacts to all three but never writes them — track and
  // phase clicks originate in the Data Flow / Access Matrix panels; hover originates in the Data Flow Timeline.
  const dataTrack = useSelectionStore((s) => s.dataTrack);
  const selectedPhase = useSelectionStore((s) => s.phase);
  const hoveredKey = useSelectionStore((s) => s.hoveredSystemTickKey);

  // Local view of the side-panel close button — the selection store is shared, so we don't want
  // closing here to clear it for other panels. We hide the side panel when this local flag is
  // set, even if the store still has a value.
  const [sidePanelOverride, setSidePanelOverride] = useState<string | null>(null);
  const sidePanelHidden = sidePanelOverride !== null && sidePanelOverride === selectedSystemName;

  const systemNames = useMemo(() => {
    if (!topology?.systems) return [];
    const out: string[] = [];
    for (const s of topology.systems) {
      if (s.name) out.push(s.name);
    }
    return out;
  }, [topology]);

  // Resolve the side-panel's selected node by direct lookup on `topology.systems` instead of
  // running the full dagre layout (`buildDagModel`) just to find one entry. The Canvas already
  // computes the layout once; doing it a second time per click is O(systems × edges) wasted.
  const selectedNode = useMemo(() => {
    if (!selectedSystemName || !topology?.systems) return null;
    for (const s of topology.systems) {
      if (s.name === selectedSystemName) return toNodeData(s);
    }
    return null;
  }, [topology, selectedSystemName]);

  const showSidePanel = selectedNode !== null && !sidePanelHidden;

  const { stats } = useSystemStats(sessionId, systemNames, range, statMode);

  // Edges are derived once per topology; queue-name derivation and CP computation both consume
  // this. Without this, both `useMemo`s below called `deriveEdges` redundantly on every change.
  const derivedEdges = useMemo(
    () => (topology?.systems ? deriveEdges(topology.systems) : []),
    [topology],
  );

  const queueNames = useMemo(() => {
    if (derivedEdges.length === 0) return [];
    const names = new Set<string>();
    for (const e of derivedEdges) {
      if (e.kind !== 'event') continue;
      for (const n of e.via) names.add(n);
    }
    return Array.from(names).sort();
  }, [derivedEdges]);
  const queueStats = useQueueBackpressure(sessionId, queueNames, range);

  // Critical-path participation rate per system. Pure client-side computation per design §9.3.
  // Recomputes only when topology rows or range change — `metadata.systemTickSummaries` is
  // referentially stable while the cache is loaded.
  const cpParticipation = useMemo(() => {
    if (!topology?.systems || !metadata?.systemTickSummaries || metadata.systemTickSummaries.length === 0) {
      return null;
    }
    return computeCriticalPathParticipation({
      systems: topology.systems,
      rows: metadata.systemTickSummaries,
      edges: derivedEdges,
      phases: topology.phases ?? [],
      range,
    });
  }, [topology, metadata, range, derivedEdges]);

  // Dominant-tick CP set — drives the red outline on DAG nodes per `09-system-dag.md §11 Phase 3`
  // ("Critical-path systems also render with a red border in the dominant tick of the range"). The
  // ★ badge derives from range-wide participation; this cue is per-tick — the longest single tick
  // in the window is what the user is most likely investigating, so it gets the spotlight. Empty
  // ranges / tickless metadata leave the set null and the canvas renders without the cue.
  const dominantCpSystems = useMemo<Set<string> | null>(() => {
    if (!topology?.systems || !metadata) return null;
    const tick = dominantTickInRange(metadata.tickSummaries ?? null, range);
    if (tick == null) return null;
    const tickRow = (metadata.tickSummaries ?? []).find((t) => Number(t.tickNumber) === tick) ?? null;
    const bars = computeCriticalPathForTick({
      tickNumber: tick,
      systems: topology.systems,
      rows: metadata.systemTickSummaries ?? [],
      edges: derivedEdges,
      phases: topology.phases ?? [],
      postTickRows: metadata.postTickSummaries ?? [],
      tickSummaryRow: tickRow,
    });
    if (!bars) return null;
    const out = new Set<string>();
    for (const bar of bars.cpChain) out.add(bar.systemName);
    return out;
  }, [topology, metadata, range, derivedEdges]);

  // Gating-predecessor analysis — for each system, identifies which predecessor's completion
  // determined when the system could start, plus the wait gap and edge metadata. Drives the
  // side panel's "Gated by" section, the canvas's gating-edge highlight, and the per-node
  // "blocked" icon. See `gatingAnalysis.ts` for the math (it's exact, not an estimate — the
  // engine's `ReadyUs` equals `max(predecessor.EndUs)` by construction).
  const gatingAnalysis = useMemo(() => {
    if (!topology?.systems || !metadata?.systemTickSummaries) return null;
    return computeGatingAnalysis({
      systems: topology.systems,
      rows: metadata.systemTickSummaries,
      edges: derivedEdges,
      range,
    });
  }, [topology, metadata, derivedEdges, range]);

  // Publish to the cross-panel store so DataFlow's bar tooltip can render the gating line without
  // recomputing. Fingerprint covers the inputs that change the result; mismatches force recompute
  // on the consumer side. Cleared automatically when the panel unmounts.
  const setGatingStore = useGatingStore((s) => s.setGating);
  useEffect(() => {
    if (!gatingAnalysis) return;
    const fp = `dag|${(metadata?.fingerprint ?? '')}|${range?.from ?? ''}-${range?.to ?? ''}|${derivedEdges.length}`;
    setGatingStore(gatingAnalysis, fp);
  }, [gatingAnalysis, metadata?.fingerprint, range?.from, range?.to, derivedEdges.length, setGatingStore]);

  const skipRates = useMemo(() => {
    if (!topology?.systems || !metadata?.systemTickSummaries || metadata.systemTickSummaries.length === 0) {
      return null;
    }
    return computeSystemSkipRates({
      systems: topology.systems,
      rows: metadata.systemTickSummaries,
      range,
    });
  }, [topology, metadata, range]);

  // Phase D (#327): resolve which systems touch the currently-selected dataTrack. Called once per (topology, track)
  // change; re-renders only fan out when the resolved Set actually differs.
  const dataTrackSystems = useMemo(
    () => resolveSystemsForDataTrack(topology ?? null, dataTrack),
    [topology, dataTrack],
  );

  // P8 of #342: distinct query-definition counts per owning system. Drives the "Queries" badge on
  // each DAG tile. We reuse the QueryCatalog data hook (TanStack cache is shared so this is a free
  // ride) and resolve the numeric ownerSystemIds against the metadata.systems table to keys we can
  // index by name on the canvas side.
  const { definitions: queryDefinitions } = useQueryDefinitions();
  const systemIndexToName = useMemo(() => {
    const m = new Map<number, string>();
    for (const s of metadata?.systems ?? []) m.set(Number(s.index), s.name ?? `System[${s.index}]`);
    return m;
  }, [metadata]);
  // Inverse lookup used by the QueriesBadge to navigate to the Catalog with a numeric system filter.
  // Computed once here so the per-node badge component is hook-free (no useProfilerMetadata /
  // useSessionStore subscriptions on the hot render path — 50+ nodes would otherwise mount 50+
  // observers against the shared TanStack cache).
  const systemNameToIndex = useMemo(() => {
    const m = new Map<string, number>();
    for (const s of metadata?.systems ?? []) {
      if (s.name) m.set(s.name, Number(s.index));
    }
    return m;
  }, [metadata]);
  const queryCountsBySystem = useMemo(
    () => buildQueryCountsBySystem(queryDefinitions, systemIndexToName),
    [queryDefinitions, systemIndexToName],
  );
  // When a system owns exactly one query, the badge click auto-expands that row in the Catalog so
  // the user lands on the relevant detail instead of just a one-row filtered list. Multi-owner
  // systems leave the choice to the user — the filter alone is enough discovery.
  const singleOwnedDefBySystem = useMemo(
    () => buildSingleOwnedDefBySystem(queryDefinitions, systemIndexToName),
    [queryDefinitions, systemIndexToName],
  );

  const tickSummaries = metadata?.tickSummaries ?? null;
  // Worker count drives the toolbar's parallelism-inefficiency pill (A1 / A6). Header field is
  // `number | string` per the OpenAPI shape; coerce defensively at the boundary so the toolbar
  // can hide the pill for missing / < 2 worker traces without a parse step there.
  const workerCount = useMemo(() => {
    const raw = metadata?.header?.workerCount;
    if (raw == null) return null;
    const n = typeof raw === 'number' ? raw : Number(raw);
    return Number.isFinite(n) && n >= 1 ? n : null;
  }, [metadata]);

  // Fit-request counter — incremented by every "want to fit" trigger. Consumed by the canvas's
  // FitController, which gates on xyflow's readiness signal before actually fitting (see that
  // component's docstring for the why). The panel only needs to bump; it never calls fitView.
  //
  // Triggers covered here:
  //   - Fit button in the toolbar (via the `onFit` prop).
  //   - Middle-click on the canvas (via SystemDagCanvas's `onRequestFit` prop, which it routes here).
  //   - Tick-range change (the effect below) — user clicks another tick in the profiler → refit.
  //
  // NOT covered here (handled inside the canvas directly into FitController's local token):
  //   - First-show / panel-becomes-visible / dockview readiness — the FitController's readiness
  //     gate handles all of those without a special trigger, because xyflow's `width` / `height` /
  //     `nodesInitialized` deps fire the effect when the gate opens.
  //   - Layout switch / hideSkipped filter change / showEngineTracks toggle — the canvas reads
  //     those locally and bumps the FitController's token directly.
  const [fitSignal, setFitSignal] = useState(0);
  const requestFit = useCallback(() => setFitSignal((n) => n + 1), []);

  // Refit on every committed viewRange change (tick click in TickOverview, profiler scrub-settle).
  // Skips the very first run (mount) — the FitController's readiness gate already produces the
  // initial fit; an extra bump here would just be a duplicate token consumed on the same tick.
  const viewRangeFirstRunRef = useRef(true);
  useEffect(() => {
    if (viewRangeFirstRunRef.current) {
      viewRangeFirstRunRef.current = false;
      return;
    }
    requestFit();
  }, [viewRange.startUs, viewRange.endUs, requestFit]);

  if (!sessionId) {
    return <EmptyState message="No session attached. Open a trace or attach to a live engine to see the DAG." />;
  }
  if (isError) {
    return <EmptyState message="Topology fetch failed — check the server log." tone="error" />;
  }
  if (isLoading || !topology) {
    return <EmptyState message="Loading topology…" />;
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <SystemDagToolbar
        tickSummaries={tickSummaries}
        autoSnapshotEnabled
        systemTickSummaries={metadata?.systemTickSummaries ?? null}
        workerCount={workerCount}
        onFit={requestFit}
      />
      <div className="flex flex-1 overflow-hidden">
        <div className="flex-1 min-w-0">
          <SystemDagCanvas
            topology={topology}
            selectedSystemName={selectedSystemName}
            systemStats={range ? stats : null}
            queueStats={range && queueStats.size > 0 ? queueStats : null}
            cpParticipation={cpParticipation}
            dominantCpSystems={dominantCpSystems}
            skipRates={skipRates}
            gatingAnalysis={gatingAnalysis}
            dataTrackSystems={dataTrack ? dataTrackSystems : null}
            selectedPhase={selectedPhase}
            hoveredSystemFromCrossPanel={hoveredKey?.systemName ?? null}
            queryCountsBySystem={queryCountsBySystem}
            singleOwnedDefBySystem={singleOwnedDefBySystem}
            systemNameToIndex={systemNameToIndex}
            fitSignal={fitSignal}
            onRequestFit={requestFit}
            onSelectSystem={(name) => {
              setSystem(name);
              setSidePanelOverride(null);
            }}
          />
        </div>
        {showSidePanel && selectedNode && (
          <SystemDagSidePanel
            node={selectedNode}
            sessionId={sessionId}
            range={range}
            cpStat={cpParticipation?.perSystem.get(selectedNode.systemName) ?? null}
            cpTotalTicks={cpParticipation?.totalTicks ?? null}
            gatingInfo={gatingAnalysis?.get(selectedNode.systemName) ?? null}
            noAccessReason={resolveNoAccessReason(topology, selectedNode.dagId)}
            onClose={() => setSidePanelOverride(selectedSystemName)}
          />
        )}
      </div>
    </div>
  );
}

function EmptyState({ message, tone = 'muted' }: { message: string; tone?: 'muted' | 'error' }) {
  const colour = tone === 'error' ? 'text-destructive' : 'text-muted-foreground';
  return (
    <div className={`flex h-full w-full items-center justify-center bg-background text-fs-base ${colour}`}>
      {message}
    </div>
  );
}
