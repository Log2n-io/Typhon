import { useMemo, useState } from 'react';
import type { IDockviewPanelProps } from 'dockview-react';
import { useTopology } from '@/hooks/data/useTopology';
import { useProfilerMetadata } from '@/hooks/profiler/useProfilerMetadata';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useSessionStore } from '@/stores/useSessionStore';
import {
  computeCriticalPathForTick,
  computeCriticalPathParticipation,
  computeSystemSkipRates,
  dominantTickInRange,
} from '../CriticalPath/criticalPath';
import { toNodeData } from './dagModel';
import { deriveEdges } from './edgeDerivation';
import SystemDagCanvas from './SystemDagCanvas';
import SystemDagSidePanel from './SystemDagSidePanel';
import SystemDagToolbar from './SystemDagToolbar';
import { timeToTickRange } from './tickRangeMapping';
import { useDagViewStore } from './useDagViewStore';
import { useQueueBackpressure } from './useQueueBackpressure';
import { useSystemStats } from './useSystemStats';

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

  // Cross-panel binding (#316 Phase 2 final per `09-system-dag.md §7.1`): the tick range comes
  // from `useSelectionStore.time`, the same µs window the profiler's TimeArea writes to. The
  // existing selection-bridge sync (selectionBridges.ts) already mirrors profiler.viewRange ↔
  // selection.time, so scrubbing the profiler's TimeArea live-updates the DAG aggregations, and
  // hitting "Snapshot last N ticks" in the DAG (which writes to selection.time) pins the
  // profiler's TimeArea too.
  //
  // Conversion µs → tick happens at the panel boundary; downstream hooks (useSystemStats /
  // useQueueBackpressure / CP / skip-rate) all take TickRange and stay tick-native.
  const time = useSelectionStore((s) => s.time);
  const range = useMemo(
    () => timeToTickRange(time, metadata?.tickSummaries),
    [time, metadata],
  );

  const selectedSystemName = useSelectionStore((s) => s.system);
  const setSystem = useSelectionStore((s) => s.setSystem);

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
    for (const phase of bars.phases) {
      for (const bar of phase.bars) out.add(bar.systemName);
    }
    return out;
  }, [topology, metadata, range, derivedEdges]);

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

  const tickSummaries = metadata?.tickSummaries ?? null;

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
      <SystemDagToolbar tickSummaries={tickSummaries} autoSnapshotEnabled />
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
    <div className={`flex h-full w-full items-center justify-center bg-background text-[12px] ${colour}`}>
      {message}
    </div>
  );
}
