import { useMemo } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  type Edge,
  type Node,
  type NodeMouseHandler,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { buildDagModel, type DagEdgeData, type DagNodeData } from './dagModel';
import SystemDagNode from './SystemDagNode';
import type { SystemStat } from './useSystemStats';
import type { QueueBackpressureStat } from './useQueueBackpressure';
import type { CriticalPathParticipation } from '../CriticalPath/criticalPath';
import { useDagViewStore } from './useDagViewStore';

interface Props {
  topology: TopologyDto | null | undefined;
  selectedSystemName: string | null;
  onSelectSystem: (name: string | null) => void;
  /** Optional per-system primary stat. When null, nodes render without heat colouring (Phase 1 view). */
  systemStats: Map<string, SystemStat> | null;
  /** Optional per-queue backpressure stats. When null, event edges keep their flat default style. */
  queueStats: Map<string, QueueBackpressureStat> | null;
  /** Optional per-system critical-path participation. Drives the ★ badge on nodes. */
  cpParticipation: CriticalPathParticipation | null;
  /** Optional per-system skip rates ∈ [0, 1]. Drives the ↪ chip on nodes. */
  skipRates: Map<string, number> | null;
}

const NODE_TYPES = { system: SystemDagNode as never };

export default function SystemDagCanvas({
  topology,
  selectedSystemName,
  onSelectSystem,
  systemStats,
  queueStats,
  cpParticipation,
  skipRates,
}: Props) {
  // Layout is read straight from the store (avoids prop drilling). Switching layouts re-runs
  // `buildDagModel` and `<ReactFlow fitView>` re-fits the viewport to the new bounds.
  const layout = useDagViewStore((s) => s.layout);
  const model = useMemo(() => buildDagModel(topology, layout), [topology, layout]);

  // Merge selection state + stats + CP rate + skip rate into node.data in one pass — keeps the
  // node renderer pure (it just reads what's on data) and lets the canvas re-render only when
  // these inputs change.
  const styledNodes = useMemo<
    Node<DagNodeData & { stat?: SystemStat | null; cpRate?: number | null; skipRate?: number | null }>[]
  >(() => {
    return model.nodes.map((n) => {
      const stat = systemStats?.get(n.id) ?? null;
      const cpRate = cpParticipation?.perSystem.get(n.id)?.rate ?? null;
      const skipRate = skipRates?.get(n.id) ?? null;
      const isSelected = n.id === selectedSystemName;
      return {
        ...n,
        selected: isSelected,
        data: { ...n.data, stat, cpRate, skipRate },
      };
    });
  }, [model.nodes, selectedSystemName, systemStats, cpParticipation, skipRates]);

  // Apply backpressure styling to event-class edges. The first entry of `via` is the primary
  // queue name (event edges almost always cite a single queue; multi-queue edges are degenerate).
  // When the queue isn't in the stats map (no data, range cleared), the edge keeps its default
  // dashed-violet look from `dagModel.toReactFlowEdge`.
  const styledEdges = useMemo<Edge<DagEdgeData>[]>(() => {
    if (!queueStats || queueStats.size === 0) return model.edges;
    return model.edges.map((e) => {
      if (e.data?.kind !== 'event') return e;
      const queueName = e.data.via?.[0];
      if (!queueName) return e;
      const stat = queueStats.get(queueName);
      if (!stat) return e;
      const stroke = backpressureColour(stat);
      const baseStyle = e.style ?? {};
      const labelPrefix = stat.overflowSum > 0 ? `⚠ ${formatCount(stat.overflowSum)} drops · ` : '';
      // Per design §4.5: stroke colour = peak-driven (worst moment), strokeWidth = end-of-tick-
      // driven (chronic backlog). Two independent channels answering two different questions.
      const strokeWidth = 1.5 + stat.outlineHeat * 1.5;
      return {
        ...e,
        animated: stat.overflowSum > 0,
        label: `${labelPrefix}${e.label ?? queueName}`,
        labelStyle: { fontSize: 10, fill: stroke, fontFamily: 'monospace' },
        style: { ...baseStyle, stroke, strokeWidth },
      };
    });
  }, [model.edges, queueStats]);

  const onNodeClick = useMemo<NodeMouseHandler<Node<DagNodeData>>>(() => {
    return (_e, node) => onSelectSystem(node.id);
  }, [onSelectSystem]);

  const onPaneClick = useMemo(() => () => onSelectSystem(null), [onSelectSystem]);

  if (model.nodes.length === 0) {
    return (
      <div className="flex h-full w-full items-center justify-center bg-background text-[12px] text-muted-foreground">
        No topology yet. Open a trace or attach a session to populate the DAG.
      </div>
    );
  }

  return (
    <div className="relative h-full w-full bg-background">
      {/*
        Lane backgrounds — rendered behind the ReactFlow canvas as absolutely-positioned divs.
        Lanes are emitted by `horizontal-lanes` and `vertical-lanes` only; `compact` and
        `circular` produce empty `model.lanes` and this block renders nothing for them.
      */}
      <div className="pointer-events-none absolute inset-0 overflow-hidden">
        {model.lanes.map((lane) => {
          const isVertical = lane.labelEdge === 'top';
          // Horizontal lanes span the full canvas width (lanes line up vertically); vertical lanes
          // span the full canvas height. The model's width/height bounding box drives the cross-axis.
          const left = isVertical ? lane.xLeft : 0;
          const top = isVertical ? 0 : lane.yTop;
          const width = isVertical ? lane.width : Math.max(lane.width, model.width);
          const height = isVertical ? Math.max(lane.height, model.height) : lane.height;
          return (
            <div
              key={lane.name}
              className={`absolute ${isVertical ? 'border-x' : 'border-y'} border-border/40`}
              style={{ left, top, width, height, backgroundColor: laneTint(lane.index) }}
            >
              <div
                className={`${isVertical ? 'sticky top-0' : 'sticky left-0'} inline-block px-3 py-1.5 font-mono text-[10px] uppercase tracking-wide text-muted-foreground`}
              >
                {lane.name} · {lane.systemCount}
              </div>
            </div>
          );
        })}
      </div>
      <ReactFlow
        nodes={styledNodes}
        edges={styledEdges}
        nodeTypes={NODE_TYPES}
        fitView
        proOptions={{ hideAttribution: true }}
        minZoom={0.3}
        maxZoom={1.6}
        nodesDraggable={false}
        nodesConnectable={false}
        elementsSelectable
        onNodeClick={onNodeClick}
        onPaneClick={onPaneClick}
      >
        <Background color="var(--border)" gap={16} />
        <Controls showInteractive={false} position="bottom-left" />
        <MiniMap pannable zoomable position="bottom-right" />
      </ReactFlow>
    </div>
  );
}

const TINT_PALETTE = [
  'rgba(56, 189, 248, 0.06)', // sky
  'rgba(148, 163, 184, 0.04)', // slate
  'rgba(251, 146, 60, 0.06)', // orange
  'rgba(167, 139, 250, 0.06)', // violet
  'rgba(74, 222, 128, 0.06)', // emerald
];

function laneTint(index: number): string {
  if (index < 0) return 'rgba(0, 0, 0, 0)';
  return TINT_PALETTE[index % TINT_PALETTE.length];
}

/**
 * Backpressure → edge stroke colour. Cool (low traffic) → red (overflow). Per `09-system-dag.md
 * §4.5`'s threshold ramp, but applied to the relative-heat fallback documented in
 * {@link useQueueBackpressure}. Overflow always wins.
 */
function backpressureColour(stat: QueueBackpressureStat): string {
  if (stat.overflowSum > 0) return 'hsl(0, 80%, 55%)'; // catastrophic — deep red
  // 0 → violet (idle), 1 → orange (hot)
  const hue = 270 - stat.heat * 240; // 270 (violet) → 30 (orange)
  return `hsl(${hue}, 70%, 60%)`;
}

function formatCount(n: number): string {
  if (n < 1000) return String(n);
  return `${(n / 1000).toFixed(1)}k`;
}
