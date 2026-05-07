import { useMemo } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  ViewportPortal,
  type Edge,
  type Node,
  type NodeMouseHandler,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { useHoverStore } from '@/stores/useHoverStore';
import { colorForPhase } from '../CriticalPath/phaseColors';
import { buildDagModel, NODE_HEIGHT, NODE_WIDTH, type DagEdgeData, type DagNodeData } from './dagModel';
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
  /**
   * System names on the critical path of the dominant (longest) tick in the current range. Drives
   * the red outline on nodes per `09-system-dag.md §11 Phase 3`. Distinct from `cpParticipation`,
   * which is range-wide (badge), this is single-tick (per-tick spotlight).
   */
  dominantCpSystems: Set<string> | null;
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
  dominantCpSystems,
  skipRates,
}: Props) {
  // Layout is read straight from the store (avoids prop drilling). Switching layouts re-runs
  // `buildDagModel` and `<ReactFlow fitView>` re-fits the viewport to the new bounds.
  const layout = useDagViewStore((s) => s.layout);
  const model = useMemo(() => buildDagModel(topology, layout), [topology, layout]);
  const hoveredSystem = useHoverStore((s) => s.hoveredSystem);
  const setHoveredSystem = useHoverStore((s) => s.setHoveredSystem);
  const hoveredPhase = useHoverStore((s) => s.hoveredPhase);
  const setHoveredPhase = useHoverStore((s) => s.setHoveredPhase);

  // Phase-highlight rects for the lane-less layouts (compact / circular). When a phase is
  // hovered (in the CP tape, here in the lanes layouts, etc.) and we're in a layout that
  // doesn't draw swim-lanes, paint a coloured backdrop + border behind every node belonging
  // to that phase. Colour comes from `colorForPhase` — same palette as the Critical Path
  // tape's phase stripe — so the cross-panel association reads instantly.
  const phaseHighlights = useMemo(() => {
    if (!hoveredPhase || model.lanes.length > 0 || !topology?.phases) return [];
    const phaseIndex = topology.phases.indexOf(hoveredPhase);
    if (phaseIndex < 0) return [];
    const colour = colorForPhase(phaseIndex);
    const out: Array<{ id: string; x: number; y: number; stroke: string; fill: string }> = [];
    for (const n of model.nodes) {
      if (n.data.phaseName === hoveredPhase) {
        out.push({ id: n.id, x: n.position.x, y: n.position.y, stroke: colour.stroke, fill: colour.fill });
      }
    }
    return out;
  }, [hoveredPhase, model.lanes.length, model.nodes, topology]);

  // Merge selection state + stats + CP rate + skip rate into node.data in one pass — keeps the
  // node renderer pure (it just reads what's on data) and lets the canvas re-render only when
  // these inputs change.
  const styledNodes = useMemo<
    Node<DagNodeData & { stat?: SystemStat | null; cpRate?: number | null; skipRate?: number | null; isOnDominantCp?: boolean; isHovered?: boolean }>[]
  >(() => {
    return model.nodes.map((n) => {
      const stat = systemStats?.get(n.id) ?? null;
      const cpRate = cpParticipation?.perSystem.get(n.id)?.rate ?? null;
      const skipRate = skipRates?.get(n.id) ?? null;
      const isSelected = n.id === selectedSystemName;
      const isOnDominantCp = dominantCpSystems?.has(n.id) ?? false;
      const isHovered = n.id === hoveredSystem;
      return {
        ...n,
        selected: isSelected,
        data: { ...n.data, stat, cpRate, skipRate, isOnDominantCp, isHovered },
      };
    });
  }, [model.nodes, selectedSystemName, systemStats, cpParticipation, dominantCpSystems, skipRates, hoveredSystem]);

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

  // Cross-panel hover sync (#317 Phase 3 §11): mouseenter writes the node id to the shared hover
  // store; the matching tape bar in the Critical Path view picks it up via the same store. Leave
  // clears the slot; we don't try to debounce or trail because dockview re-parents propagate
  // mouseleave correctly.
  const onNodeMouseEnter = useMemo<NodeMouseHandler<Node<DagNodeData>>>(() => {
    return (_e, node) => setHoveredSystem(node.id);
  }, [setHoveredSystem]);

  const onNodeMouseLeave = useMemo<NodeMouseHandler<Node<DagNodeData>>>(() => {
    return () => setHoveredSystem(null);
  }, [setHoveredSystem]);

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
        onNodeMouseEnter={onNodeMouseEnter}
        onNodeMouseLeave={onNodeMouseLeave}
        onPaneClick={onPaneClick}
      >
        {/*
          Lane backgrounds — rendered inside `ViewportPortal` so they share the ReactFlow viewport
          transform (translate + scale) with the nodes. Without this, the lanes are static in
          screen space while the nodes pan/zoom underneath, creating the visual mis-alignment the
          user reported. Coordinates here are in flow-space (same coordinate system as
          `node.position`); the viewport applies the transform automatically.
          Lanes are emitted by `horizontal-lanes` and `vertical-lanes` only; `compact` /
          `circular` produce empty `model.lanes` and this block renders nothing for them.
        */}
        <ViewportPortal>
          {/*
            Phase-flow arrow — visualises the order phases run in (top→bottom for horizontal lanes,
            left→right for vertical lanes). A single SVG positioned in the outer margin (left of
            horizontal lanes / above vertical lanes) carrying a shaft + chevrons at each phase
            boundary + a trailing arrowhead. Skipped when there are 0 or 1 phases — single phase
            has no flow to show. Position is in flow-space so it pans/zooms with the rest of the
            viewport.
          */}
          {model.lanes.length > 1 && <PhaseFlowArrow lanes={model.lanes} />}
          {/*
            Phase-highlight rects — drawn behind nodes when a phase is hovered AND the layout
            has no swim-lanes (compact / circular). Each rect sits at the node's flow-space
            position with a small padding so it reads as a backdrop, not a tile replacement.
            Uses `laneTintHovered` so the colour matches the swim-lane hover tint exactly,
            keeping the cross-layout cue consistent.
          */}
          {phaseHighlights.map((hl) => (
            <div
              key={hl.id}
              className="pointer-events-none absolute rounded"
              style={{
                // 12 px padding around the node tile so the backdrop reads as visibly wider
                // than the box it sits behind (the user's "twice as large" — relative to the
                // node, not a stroke width).
                left: hl.x - 12,
                top: hl.y - 12,
                width: NODE_WIDTH + 24,
                height: NODE_HEIGHT + 24,
                // Plain fill, no border. CP tape's phase-stripe `stroke` hue re-cast as HSLA
                // at very low alpha — visible enough to register "this node is in the hovered
                // phase", washed-out enough to stay subtle and not compete with the node tile.
                backgroundColor: hl.stroke.replace('hsl(', 'hsla(').replace(')', ', 0.12)'),
                zIndex: -1,
              }}
            />
          ))}
          {model.lanes.map((lane) => {
            // Cross-panel phase-hover sync (#317 §5.5): when this lane's phase is hovered (here
            // or in the CP tape's stripe), brighten the lane background. The lane keeps its
            // tint baseline; we layer a higher-opacity version on top.
            const isHovered = hoveredPhase != null && hoveredPhase === lane.name;
            const isVertical = lane.labelEdge === 'top';
            return (
              <div
                key={lane.name}
                // `pointer-events: none` so clicks pass through to nodes / pane; the inner label
                // re-enables them for the hover sync.
                // `zIndex: -1` puts the lane below the edges + nodes ReactFlow renders inside
                // the same viewport plane, so the band reads as a backdrop, not an overlay.
                className={`pointer-events-none absolute ${isVertical ? (isHovered ? 'border-x-2' : 'border-x') : (isHovered ? 'border-y-2' : 'border-y')} ${isHovered ? 'border-foreground/60' : 'border-border/70'}`}
                style={{
                  left: lane.xLeft,
                  top: lane.yTop,
                  width: lane.width,
                  height: lane.height,
                  backgroundColor: isHovered ? laneTintHovered(lane.index) : laneTint(lane.index),
                  zIndex: -1,
                }}
              >
                {/* Label sits at the lane's flow-space top-left, panning/zooming with the
                    lane (it's part of the same coordinate system). Sticky positioning was
                    dropped — it relied on the parent being page-scroll-anchored, which no
                    longer holds inside the transformed viewport plane. */}
                <div
                  className={`pointer-events-auto inline-block px-3 py-1.5 font-mono text-[10px] uppercase tracking-wide ${isHovered ? 'text-foreground/80' : 'text-muted-foreground'}`}
                  onMouseEnter={() => setHoveredPhase(lane.name)}
                  onMouseLeave={() => setHoveredPhase(null)}
                >
                  {lane.name} · {lane.systemCount}
                </div>
              </div>
            );
          })}
        </ViewportPortal>
        <Background color="var(--border)" gap={16} />
        <Controls showInteractive={false} position="bottom-left" />
        <MiniMap pannable zoomable position="bottom-right" />
      </ReactFlow>
    </div>
  );
}

// Baseline lane tints — readable at any zoom. The previous values (0.04–0.06 alpha) were so
// faint they only registered when the lane filled most of the viewport; bumped to 0.14–0.18 so
// the bands read as bands at zoom-out levels too without overpowering node tiles.
const TINT_PALETTE = [
  'rgba(56, 189, 248, 0.16)',  // sky
  'rgba(148, 163, 184, 0.12)', // slate
  'rgba(251, 146, 60, 0.16)',  // orange
  'rgba(167, 139, 250, 0.16)', // violet
  'rgba(74, 222, 128, 0.16)',  // emerald
];

function laneTint(index: number): string {
  if (index < 0) return 'rgba(0, 0, 0, 0)';
  return TINT_PALETTE[index % TINT_PALETTE.length];
}

/**
 * Higher-opacity twin of {@link laneTint} for the phase-hover state. Same hues, alpha roughly
 * doubled vs. the baseline so the matched lane is unmistakable.
 */
const TINT_PALETTE_HOVERED = [
  'rgba(56, 189, 248, 0.32)',
  'rgba(148, 163, 184, 0.26)',
  'rgba(251, 146, 60, 0.32)',
  'rgba(167, 139, 250, 0.32)',
  'rgba(74, 222, 128, 0.32)',
];

function laneTintHovered(index: number): string {
  if (index < 0) return 'rgba(0, 0, 0, 0)';
  return TINT_PALETTE_HOVERED[index % TINT_PALETTE_HOVERED.length];
}

/**
 * Phase-flow arrows — one short arrow per lane boundary, spanning the gap between consecutive
 * lanes. Communicates "phase i → phase i+1" without putting a single dominant spine on the
 * canvas. Each arrow goes from the bottom (or right, vertical mode) edge of one lane to the top
 * (or left) edge of the next, with the body inside the LANE_GAP empty space.
 *
 * **Alignment.** All arrows share a common position on the cross-flow axis (same x in horizontal
 * mode, same y in vertical mode), so they form a clean column / row of pointers rather than
 * scattered glyphs. The shared coordinate is the canvas centre on the cross-flow axis.
 *
 * **Subtle by design.** Opacity 0.2 over the muted-foreground tone — visible enough to register
 * "things flow this direction", quiet enough not to compete with the actual data (nodes / edges).
 *
 * Pointer-events disabled so arrows don't intercept node hover / click. Rendered inside
 * ViewportPortal with `currentColor` so theme switches and viewport pan/zoom Just Work.
 */
function PhaseFlowArrow({ lanes }: { lanes: ReadonlyArray<{ name: string; xLeft: number; yTop: number; width: number; height: number; labelEdge: 'left' | 'top' }> }) {
  const isVertical = lanes[0].labelEdge === 'top';
  const SHAFT_WIDTH = 1.5;
  const ARROWHEAD_HALF = 5; // arrowhead width on the cross-flow axis
  const ARROWHEAD_LEN = 8;  // arrowhead extent on the flow axis
  const OPACITY = 0.2;

  // Cross-axis position — arrows live in a leading margin area: 20 px in from the lane stack's
  // leading edge (top in vertical-lanes mode, left in horizontal-lanes mode). Keeps the arrows
  // off the node area and away from the dense centre of the canvas.
  const LEADING_MARGIN = 20;

  if (isVertical) {
    // Vertical lanes — arrow goes from right edge of lane[i] to left edge of lane[i+1]; all
    // arrows share the same y near the top of the lane stack.
    const minY = Math.min(...lanes.map((l) => l.yTop));
    const sharedY = minY + LEADING_MARGIN;
    return (
      <>
        {lanes.slice(0, -1).map((lane, i) => {
          const next = lanes[i + 1];
          const startX = lane.xLeft + lane.width;
          const endX = next.xLeft;
          return (
            <PhaseFlowSegment
              key={i}
              startX={startX}
              startY={sharedY}
              endX={endX}
              endY={sharedY}
              opacity={OPACITY}
              shaftWidth={SHAFT_WIDTH}
              arrowheadHalf={ARROWHEAD_HALF}
              arrowheadLen={ARROWHEAD_LEN}
              orientation="horizontal"
            />
          );
        })}
      </>
    );
  }

  // Horizontal lanes — arrow goes from bottom edge of lane[i] to top edge of lane[i+1]; all
  // arrows share the same x near the left of the lane stack.
  const minX = Math.min(...lanes.map((l) => l.xLeft));
  const sharedX = minX + LEADING_MARGIN;
  return (
    <>
      {lanes.slice(0, -1).map((lane, i) => {
        const next = lanes[i + 1];
        const startY = lane.yTop + lane.height;
        const endY = next.yTop;
        return (
          <PhaseFlowSegment
            key={i}
            startX={sharedX}
            startY={startY}
            endX={sharedX}
            endY={endY}
            opacity={OPACITY}
            shaftWidth={SHAFT_WIDTH}
            arrowheadHalf={ARROWHEAD_HALF}
            arrowheadLen={ARROWHEAD_LEN}
            orientation="vertical"
          />
        );
      })}
    </>
  );
}

/**
 * One short flow-arrow spanning the gap between two consecutive lanes. The shaft runs from
 * `(startX, startY)` toward `(endX, endY)` and stops `arrowheadLen` short; the arrowhead
 * triangle takes that final stretch and points at the destination edge. Wrapped in its own SVG
 * so we can keep arrow segments self-contained inside `ViewportPortal` without manually
 * computing a parent SVG bounding box.
 */
function PhaseFlowSegment({
  startX, startY, endX, endY, opacity, shaftWidth, arrowheadHalf, arrowheadLen, orientation,
}: {
  startX: number; startY: number; endX: number; endY: number;
  opacity: number; shaftWidth: number; arrowheadHalf: number; arrowheadLen: number;
  orientation: 'vertical' | 'horizontal';
}) {
  // Render each segment as its own absolutely-positioned SVG inside ViewportPortal. We size
  // the SVG just large enough to contain the arrow + arrowhead; `overflow: visible` makes the
  // arrowhead triangle tolerant of tiny rounding mismatches without clipping.
  const left = Math.min(startX, endX) - arrowheadHalf;
  const top = Math.min(startY, endY) - arrowheadHalf;
  const width = Math.abs(endX - startX) + arrowheadHalf * 2;
  const height = Math.abs(endY - startY) + arrowheadHalf * 2;
  // Convert absolute coords into SVG-local coords (subtract the SVG's flow-space origin).
  const sx = startX - left;
  const sy = startY - top;
  const ex = endX - left;
  const ey = endY - top;

  if (orientation === 'vertical') {
    // Vertical arrow — flow direction sign tells us which way the arrowhead points.
    const dir = ey >= sy ? 1 : -1;
    const shaftEndY = ey - arrowheadLen * dir;
    return (
      <svg
        width={width}
        height={height}
        className="text-muted-foreground"
        style={{ position: 'absolute', left, top, overflow: 'visible', pointerEvents: 'none', zIndex: -1, opacity }}
      >
        <line x1={sx} y1={sy} x2={ex} y2={shaftEndY} stroke="currentColor" strokeWidth={shaftWidth} />
        <polygon
          points={`${ex - arrowheadHalf},${shaftEndY} ${ex + arrowheadHalf},${shaftEndY} ${ex},${ey}`}
          fill="currentColor"
        />
      </svg>
    );
  }
  // Horizontal arrow.
  const dir = ex >= sx ? 1 : -1;
  const shaftEndX = ex - arrowheadLen * dir;
  return (
    <svg
      width={width}
      height={height}
      className="text-muted-foreground"
      style={{ position: 'absolute', left, top, overflow: 'visible', pointerEvents: 'none', zIndex: -1, opacity }}
    >
      <line x1={sx} y1={sy} x2={shaftEndX} y2={ey} stroke="currentColor" strokeWidth={shaftWidth} />
      <polygon
        points={`${shaftEndX},${ey - arrowheadHalf} ${shaftEndX},${ey + arrowheadHalf} ${ex},${ey}`}
        fill="currentColor"
      />
    </svg>
  );
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
