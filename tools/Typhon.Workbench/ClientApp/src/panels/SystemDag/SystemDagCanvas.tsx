import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  ViewportPortal,
  type Edge,
  type Node,
  type NodeChange,
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
import type { SystemGatingInfo } from '@/lib/dag/gatingAnalysis';
import { useDagViewStore } from './useDagViewStore';
import { getOverride, useNodePositionsStore } from './useNodePositionsStore';

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
  /**
   * Per-system gating analysis. Drives (a) the per-node "blocked" indicator (icon when
   * `meanWaitGapUs` is non-trivial) and (b) the bolded edge from the selected system's
   * top gating predecessor.
   */
  gatingAnalysis: Map<string, SystemGatingInfo> | null;
  /**
   * Phase D (#327): set of system names that touch the currently-selected `dataTrack`. Each one gets an amber
   * halo so the user can see, at a glance, which systems care about the track they clicked in Data Flow / Access
   * Matrix. Null when no track is selected — nodes render without the halo.
   */
  dataTrackSystems: Set<string> | null;
  /**
   * Phase D (#327): the currently-selected phase from `useSelectionStore.phase`. When set, the matching swim-lane
   * brightens. Distinct from the existing `hoveredPhase` (which is volatile, hover-driven) — this one is sticky
   * until cleared. The two combine: hover takes effect on top of the sticky selection.
   */
  selectedPhase: string | null;
  /**
   * Phase D (#327): cross-panel hover key. When the user hovers a bar in the Data Flow Timeline, the matching
   * system's node gets a brightened ring. Null when nothing is hovered. Decoupled from the existing per-DAG
   * `hoveredSystem` so it doesn't bleed into other DAG-internal hover effects.
   */
  hoveredSystemFromCrossPanel: string | null;
  /**
   * P8 of umbrella #342: per-system count of distinct query definitions owned by the system. Drives
   * the "Queries" badge on each tile. Null is treated as "no data" (badge hidden). Computed by
   * {@link buildQueryCountsBySystem}.
   */
  queryCountsBySystem: Map<string, number> | null;
  /**
   * P8 cross-panel nav: system-name → numeric-index lookup so the QueriesBadge can pre-filter the
   * Query Catalog without re-resolving metadata per node.
   */
  systemNameToIndex: Map<string, number>;
  /**
   * For systems with exactly one owned query, the (kind, localId) of that query — drives the
   * "badge click also expands the relevant Catalog row" behaviour. Missing entry = either zero
   * or multiple owned queries; the badge just applies the filter in that case.
   */
  singleOwnedDefBySystem: Map<string, { kind: number; localId: number }>;
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
  gatingAnalysis,
  dataTrackSystems,
  selectedPhase,
  hoveredSystemFromCrossPanel,
  queryCountsBySystem,
  singleOwnedDefBySystem,
  systemNameToIndex,
}: Props) {
  // Layout is read straight from the store (avoids prop drilling). Switching layouts re-runs
  // `buildDagModel` and `<ReactFlow fitView>` re-fits the viewport to the new bounds.
  const layout = useDagViewStore((s) => s.layout);
  const hideSkipped = useDagViewStore((s) => s.hideSkipped);
  const showCrossPhaseEdges = useDagViewStore((s) => s.showCrossPhaseEdges);

  // "Hide skipped" matches what the user sees on each tile: if the stat chip would read 0.0us
  // (or is missing entirely), the system contributed nothing to the selected range and we drop
  // it. We filter the topology *before* buildDagModel so dagre lays out only the surviving
  // systems — otherwise the canvas leaves holes where the hidden tiles used to be. The
  // underlying "no contribution" signal covers RunIf-skipped, tier-filtered, and
  // "scheduled-but-zero-duration" uniformly; matching `systemStats` keeps the toggle behaviour
  // aligned with the visible "0.0us" on each tile. No-op when no range is selected
  // (`systemStats` null) since we can't tell what counts as "didn't run" without a range.
  const visibleTopology = useMemo(() => {
    if (!hideSkipped || !systemStats || !topology?.systems) return topology;
    const surviving = topology.systems.filter((s) => {
      if (!s.name) return false;
      const stat = systemStats.get(s.name);
      return stat != null && stat.value > 0;
    });
    if (surviving.length === topology.systems.length) return topology;
    return { ...topology, systems: surviving };
  }, [topology, hideSkipped, systemStats]);

  const model = useMemo(
    () => buildDagModel(visibleTopology, layout, { showCrossPhaseEdges }),
    [visibleTopology, layout, showCrossPhaseEdges],
  );

  const hoveredSystem = useHoverStore((s) => s.hoveredSystem);
  const setHoveredSystem = useHoverStore((s) => s.setHoveredSystem);
  const hoveredPhase = useHoverStore((s) => s.hoveredPhase);
  const setHoveredPhase = useHoverStore((s) => s.setHoveredPhase);

  // Ctrl-gated node dragging. Default behaviour stays "drag pans the canvas, click selects
  // a tile". When the user holds Ctrl, `nodesDraggable` flips to true and dragging on a
  // tile moves it instead. The keyboard listener is on `window` so it fires regardless of
  // focus; the `blur` reset covers the alt-tab case where we'd otherwise miss the keyup.
  const [ctrlHeld, setCtrlHeld] = useState(false);
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === 'Control' || e.ctrlKey) setCtrlHeld(true);
    };
    const onKeyUp = (e: KeyboardEvent) => {
      // Either explicit Control keyup, or any keyup where ctrl is no longer pressed
      // (e.g. user released Ctrl while another key was down).
      if (e.key === 'Control' || !e.ctrlKey) setCtrlHeld(false);
    };
    const onBlur = () => setCtrlHeld(false);
    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    window.addEventListener('blur', onBlur);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
      window.removeEventListener('blur', onBlur);
    };
  }, []);

  // Manual position overrides. The persisted store is keyed by `${layout}|${systemName}` so
  // dragging a tile in horizontal-lanes doesn't carry over to vertical-lanes / circular.
  // We read the entire `overrides` map and resolve per-node via `getOverride`. Drag-END
  // events write to the store, persisting to localStorage so the arrangement survives
  // reloads.
  const overrides = useNodePositionsStore((s) => s.overrides);
  const setOverride = useNodePositionsStore((s) => s.setOverride);

  // Live drag state — separate from the persisted store. React Flow streams `position`
  // changes with `dragging: true` while a drag is in progress; we mirror them into local
  // state so `styledNodes` re-renders with the cursor-following position. Without this,
  // the persisted (pre-drag) position is re-applied on every render and the tile snaps
  // back to its starting point. On drag-end (`dragging: false`) we persist to the store
  // and drop the live entry so future renders use the override path.
  const [livePositions, setLivePositions] = useState<Record<string, { x: number; y: number }>>({});
  const [draggingId, setDraggingId] = useState<string | null>(null);

  // Hover-scoped cross-phase edge filtering. When "Show cross-phase edges" is ON, the lane
  // layouts include ALL edges (`allEdges()` in dagModel) and the canvas would otherwise paint
  // a visually-overwhelming cross-phase mesh. As a temporary clutter reducer, we keep every
  // intra-phase edge visible but show cross-phase edges only when one of their endpoints is
  // currently hovered. With nothing hovered the cross-phase set collapses entirely — same as
  // the toggle being off — so the user opts into seeing a system's cross-phase neighbours by
  // pointing at it.
  //
  // Identification of "cross-phase" relies on the node-level `phaseName`; if either endpoint
  // is missing from the node map (shouldn't happen, defensive) the edge is treated as
  // cross-phase and follows the hover rule.
  const phaseByNode = useMemo(() => {
    const m = new Map<string, string | undefined>();
    for (const n of model.nodes) m.set(n.id, n.data.phaseName);
    return m;
  }, [model.nodes]);

  // Edge visibility is split into two reference-stable memos so the per-frame "drag" path
  // never depends on `hoveredSystem`. Why this matters: during a Ctrl+drag, the cursor sweeps
  // over neighbouring tiles, firing onNodeMouseEnter/Leave on each one. That toggles
  // `hoveredSystem` continuously. If a single memo had both `hoveredSystem` and `draggingId`
  // in its deps, every hover toggle would invalidate it — even though the *result* during drag
  // is identical (all cross-phase edges hidden, regardless of hover). The new ref would
  // propagate to `styledEdges`, which rebuilds `style` objects on event + gating edges,
  // restarting their CSS animations every frame → visible flicker.
  //
  //   • intraOnlyEdges  — cross-phase stripped, intra-phase kept. Used during drag and when
  //                       nothing is hovered. Deps exclude `hoveredSystem` and `draggingId`,
  //                       so it stays stable for the entire drag.
  //   • hoverGatedEdges — same as intraOnly but additionally lets cross-phase edges through
  //                       when one of their endpoints is the hovered system. Used in the
  //                       normal (non-drag) hover-gating path.
  //
  // visibleEdges picks between them via a plain ternary on `draggingId`. During drag,
  // visibleEdges === intraOnlyEdges (stable ref). At rest, visibleEdges === hoverGatedEdges
  // (stable until hoveredSystem changes). styledEdges memoises off this — so event/gating
  // edge style objects are not rebuilt mid-drag, and their animations don't flicker.
  const intraOnlyEdges = useMemo(() => {
    if (!showCrossPhaseEdges) return model.edges;
    const filtered = model.edges.filter((e) => {
      const srcPhase = phaseByNode.get(e.source);
      const tgtPhase = phaseByNode.get(e.target);
      return srcPhase === tgtPhase;
    });
    return filtered.length === model.edges.length ? model.edges : filtered;
  }, [model.edges, phaseByNode, showCrossPhaseEdges]);

  const hoverGatedEdges = useMemo(() => {
    if (!showCrossPhaseEdges) return model.edges;
    if (!hoveredSystem) return intraOnlyEdges;
    const filtered = model.edges.filter((e) => {
      const srcPhase = phaseByNode.get(e.source);
      const tgtPhase = phaseByNode.get(e.target);
      if (srcPhase === tgtPhase) return true;
      return e.source === hoveredSystem || e.target === hoveredSystem;
    });
    return filtered.length === model.edges.length ? model.edges : filtered;
  }, [model.edges, phaseByNode, showCrossPhaseEdges, hoveredSystem, intraOnlyEdges]);

  const visibleEdges = draggingId != null ? intraOnlyEdges : hoverGatedEdges;

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

  // Merge selection state + stats + CP rate + skip rate + gating wait into node.data in one
  // pass — keeps the node renderer pure (it just reads what's on data) and lets the canvas
  // re-render only when these inputs change.
  //
  // Note: the drag-ghost opacity is intentionally NOT in `data` — it's applied via the
  // wrapper-level `style` prop in `styledNodes` instead. Putting it in data would force a new
  // `data` reference on every drag frame (the drag patch spreads `{...n.data, isDragging: true}`),
  // which makes the inner SystemDagNode re-render every frame, re-runs useThemeStore /
  // className computation, and forces React Flow to re-measure handles → edge attachment
  // points briefly invalidate per frame → visible flicker on attached edges. By moving opacity
  // to the wrapper, the dragged node's `data` ref is preserved and the inner component skips
  // re-rendering entirely.
  type EnrichedNode = Node<DagNodeData & {
    stat?: SystemStat | null;
    cpRate?: number | null;
    skipRate?: number | null;
    isOnDominantCp?: boolean;
    isHovered?: boolean;
    /** Mean dispatch wait in selected range (µs). Drives the per-node "blocked" icon on the tile. */
    waitGapUs?: number | null;
    /** P8 of #342: number of distinct query definitions owned by this system. Drives the "Queries" badge. */
    queryCount?: number | null;
    /** P8: numeric system index, pre-resolved from metadata so the badge stays hook-free on the hot render path. */
    numericSystemId?: number;
    /**
     * P8 follow-up: when this system owns exactly one query, its (kind, localId) — drives the
     * badge's "click also expands the relevant Catalog row" behaviour. Undefined for zero or
     * multi-owner systems (filter alone is enough).
     */
    soleOwnedDefId?: { kind: number; localId: number };
  }>;

  // ── Base decorated nodes (no hover, no drag state) ─────────────────
  // Hot stability layer — no per-frame deps, no hover dep. This memo excludes both
  // `livePositions`/`draggingId` (drag) AND `hoveredSystem` (hover). Why hover too: during a
  // Ctrl+drag, the cursor sweeps over neighbouring tiles, flipping `hoveredSystem` rapidly.
  // If hover were in this memo's deps, every flip would re-create the entire node array —
  // every tile gets a fresh ref → React Flow re-renders all 50 nodes and re-routes every
  // attached edge → visible flicker. Pulling hover into a thin patch above means only ONE
  // node ref changes per hover transition.
  //
  // Position here uses the persisted-override chain only (no live drag position). The drag
  // patch below applies the live position to just the dragged node.
  const baseDecoratedNodes = useMemo<EnrichedNode[]>(() => {
    return model.nodes.map((n) => {
      const stat = systemStats?.get(n.id) ?? null;
      const cpRate = cpParticipation?.perSystem.get(n.id)?.rate ?? null;
      const skipRate = skipRates?.get(n.id) ?? null;
      const isSelected = n.id === selectedSystemName;
      const isOnDominantCp = dominantCpSystems?.has(n.id) ?? false;
      const waitGapUs = gatingAnalysis?.get(n.id)?.meanWaitGapUs ?? null;
      const queryCount = queryCountsBySystem?.get(n.id) ?? null;
      const numericSystemId = systemNameToIndex.get(n.id) ?? -1;
      const soleOwnedDefId = singleOwnedDefBySystem.get(n.id);
      // Phase D (#327): does this node touch the currently-selected dataTrack? Drives the amber halo.
      const isOnSelectedDataTrack = dataTrackSystems?.has(n.id) ?? false;
      // Phase D (#327): is the node's phase the currently-selected phase? Drives the swim-lane tint
      // (handled at the lane-render level) AND a subtle brightness boost on the node itself.
      const isOnSelectedPhase = selectedPhase != null && n.data.phaseName === selectedPhase;
      // Phase D (#327): cross-panel hover ring — Data Flow bar hover lights up this node when the names match.
      const isHoveredFromCrossPanel = hoveredSystemFromCrossPanel != null && n.id === hoveredSystemFromCrossPanel;
      const overridePos = getOverride(overrides, layout, n.id);
      const position = overridePos ?? n.position;
      return {
        ...n,
        position,
        selected: isSelected,
        data: {
          ...n.data,
          stat,
          cpRate,
          skipRate,
          isOnDominantCp,
          isHovered: false,
          waitGapUs,
          isOnSelectedDataTrack,
          isOnSelectedPhase,
          isHoveredFromCrossPanel,
          queryCount,
          numericSystemId,
          soleOwnedDefId,
        },
      };
    });
  }, [model.nodes, selectedSystemName, systemStats, cpParticipation, dominantCpSystems, skipRates, gatingAnalysis, overrides, layout, dataTrackSystems, selectedPhase, hoveredSystemFromCrossPanel, queryCountsBySystem, singleOwnedDefBySystem, systemNameToIndex]);

  // ── Hover patch — only the hovered tile gets a new ref ─────────────
  // Maps the base array, replacing exactly one node (the hovered one) with a patched copy
  // and passing every other node through untouched. The array itself is new on every hover
  // change, but React Flow's per-node reconciler is keyed by id and ref — non-hovered tiles
  // skip re-render entirely. Without this split, the entire node list churned every hover.
  const decoratedNodes = useMemo<EnrichedNode[]>(() => {
    if (!hoveredSystem) return baseDecoratedNodes;
    return baseDecoratedNodes.map((n) =>
      n.id !== hoveredSystem
        ? n
        : { ...n, data: { ...n.data, isHovered: true } },
    );
  }, [baseDecoratedNodes, hoveredSystem]);

  // ── Live drag patch ────────────────────────────────────────────────
  // The only memo that depends on per-frame state. When no drag is in flight, returns the
  // decorated array unchanged (same reference). When a drag IS in flight, replaces ONLY the
  // dragged node's reference — every other node passes through untouched.
  //
  // CRITICAL: the dragged node's `data` ref is PRESERVED here (we don't spread `n.data`).
  // Only `position` and `style` change. With React Flow's per-node memo + `SystemDagNode`
  // wrapped in `React.memo`, the inner tile component does NOT re-render mid-drag — its
  // props are reference-equal. The wrapper still re-renders to apply the new CSS transform
  // and the `opacity: 0.5` ghost, but that's a cheap inline-style update with no DOM
  // structural change. Handles inside the tile stay measured, so edge attachment points
  // are stable, so attached edges don't flicker as they re-route to follow the moving tile.
  const DRAG_GHOST_STYLE = useMemo(() => ({ opacity: 0.5 }), []);
  const styledNodes = useMemo<EnrichedNode[]>(() => {
    if (draggingId == null) return decoratedNodes;
    const livePos = livePositions[draggingId];
    if (!livePos) return decoratedNodes;
    return decoratedNodes.map((n) =>
      n.id !== draggingId
        ? n
        : { ...n, position: livePos, style: DRAG_GHOST_STYLE },
    );
  }, [decoratedNodes, draggingId, livePositions, DRAG_GHOST_STYLE]);

  /**
   * Drag-state plumbing. React Flow emits a `position` NodeChange on every frame of a drag
   * (with `dragging: true`) and one final change with `dragging: false` when the user
   * releases. We:
   *   • mirror every position (with dragging=true) into `livePositions` so styledNodes
   *     re-renders with the cursor-following position,
   *   • record the dragging node id in `draggingId` so the styledNodes patch can apply the
   *     50% opacity ghost via wrapper-level `style` (and so the cross-phase edge filter
   *     can hide its arrows for the duration of the drag),
   *   • on drag-END, write the final position to the persisted store, then clear the
   *     live state so subsequent renders use the override path.
   * localStorage is only touched on drag-end — the per-frame updates are in-memory only.
   */
  const onNodesChange = useCallback((changes: NodeChange<Node<DagNodeData>>[]) => {
    for (const c of changes) {
      if (c.type !== 'position' || !c.position) continue;
      if (c.dragging === true) {
        setLivePositions((prev) => ({ ...prev, [c.id]: { x: c.position!.x, y: c.position!.y } }));
        setDraggingId((prev) => (prev === c.id ? prev : c.id));
      } else if (c.dragging === false) {
        setOverride(layout, c.id, { x: c.position.x, y: c.position.y });
        setLivePositions((prev) => {
          if (!(c.id in prev)) return prev;
          const next = { ...prev };
          delete next[c.id];
          return next;
        });
        setDraggingId((prev) => (prev === c.id ? null : prev));
      }
    }
  }, [layout, setOverride]);

  // Apply backpressure styling to event-class edges. The first entry of `via` is the primary
  // queue name (event edges almost always cite a single queue; multi-queue edges are degenerate).
  // When the queue isn't in the stats map (no data, range cleared), the edge keeps its default
  // dashed-violet look from `dagModel.toReactFlowEdge`.
  // Identify the gating edge for the currently-selected system: the edge from its top
  // gating-predecessor (the one that gated the most ticks). Drives the bolded edge styling
  // below — the canvas mirrors the side panel's "Gated by ..." answer.
  const gatingEdgeId = useMemo<string | null>(() => {
    if (!selectedSystemName || !gatingAnalysis) return null;
    const info = gatingAnalysis.get(selectedSystemName);
    const top = info?.gaters[0];
    if (!top || top.ticksGated === 0) return null;
    return top.edge?.id ?? null;
  }, [selectedSystemName, gatingAnalysis]);

  const styledEdges = useMemo<Edge<DagEdgeData>[]>(() => {
    const promoteIfGating = (e: Edge<DagEdgeData>): Edge<DagEdgeData> => {
      if (gatingEdgeId == null || e.id !== gatingEdgeId) return e;
      // Bump contrast/thickness without overriding the kind colour. Existing stroke colour
      // (kind-driven) stays so the user still sees fresh/snapshot/manual/event/resource;
      // we just make THIS arrow louder.
      const baseStyle = e.style ?? {};
      return {
        ...e,
        style: {
          ...baseStyle,
          strokeWidth: Math.max(typeof baseStyle.strokeWidth === 'number' ? baseStyle.strokeWidth : 1.5, 3.5),
          opacity: 1,
          filter: 'drop-shadow(0 0 4px currentColor)',
        },
        zIndex: (e.zIndex ?? 0) + 10,
        animated: true,
      };
    };
    // Edges attached to the dragged node MUST re-route every frame as the source/target moves.
    // CSS dashoffset (`animated: true`) restarts from 0 on each render, and `drop-shadow(...)`
    // is GPU-expensive to repaint per frame — both manifest as visible flicker on the
    // attached edges as they track the moving tile. Strip both for the duration of the drag,
    // restored on release. Edges NOT attached to the dragged node aren't re-routing, so their
    // animations are unaffected even when this strip is active (we still pass them through
    // the regular pipeline below).
    const stripIfAttachedToDragged = (e: Edge<DagEdgeData>): Edge<DagEdgeData> => {
      if (draggingId == null) return e;
      if (e.source !== draggingId && e.target !== draggingId) return e;
      const baseStyle = e.style ?? {};
      // Drop just the filter; keep stroke / strokeWidth / opacity (those are visual identity).
      // Spread carefully so we don't mint a fresh style object when there's nothing to strip.
      const needsStyleStrip = 'filter' in baseStyle;
      const needsAnimStrip = e.animated === true;
      if (!needsStyleStrip && !needsAnimStrip) return e;
      const next: Edge<DagEdgeData> = { ...e };
      if (needsAnimStrip) next.animated = false;
      if (needsStyleStrip) {
        const { filter: _filter, ...rest } = baseStyle;
        next.style = rest;
      }
      return next;
    };

    if (!queueStats || queueStats.size === 0) {
      return visibleEdges.map((e) => stripIfAttachedToDragged(promoteIfGating(e)));
    }
    return visibleEdges.map((e) => {
      if (e.data?.kind !== 'event') return stripIfAttachedToDragged(promoteIfGating(e));
      const queueName = e.data.via?.[0];
      if (!queueName) return stripIfAttachedToDragged(promoteIfGating(e));
      const stat = queueStats.get(queueName);
      if (!stat) return stripIfAttachedToDragged(promoteIfGating(e));
      const stroke = backpressureColour(stat);
      const baseStyle = e.style ?? {};
      const labelPrefix = stat.overflowSum > 0 ? `⚠ ${formatCount(stat.overflowSum)} drops · ` : '';
      // Per design §4.5: stroke colour = peak-driven (worst moment), strokeWidth = end-of-tick-
      // driven (chronic backlog). Two independent channels answering two different questions.
      const strokeWidth = 1.5 + stat.outlineHeat * 1.5;
      return stripIfAttachedToDragged(promoteIfGating({
        ...e,
        animated: stat.overflowSum > 0,
        label: `${labelPrefix}${e.label ?? queueName}`,
        labelStyle: { fontSize: 10, fill: stroke, fontFamily: 'monospace' },
        style: { ...baseStyle, stroke, strokeWidth },
      }));
    });
  }, [visibleEdges, queueStats, gatingEdgeId, draggingId]);

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
    <div className={`relative h-full w-full bg-background ${ctrlHeld ? 'cursor-grab' : ''}`}>
      <ReactFlow
        nodes={styledNodes}
        edges={styledEdges}
        nodeTypes={NODE_TYPES}
        fitView
        // Ctrl-gated dragging: tiles only move when the user holds Ctrl. Without it,
        // dragging on a tile is treated as a click (so selection still works) and the
        // canvas itself is panned via `panOnDrag`. The 5px threshold prevents a normal
        // click-to-select from accidentally triggering a single-pixel drag.
        nodesDraggable={ctrlHeld}
        nodeDragThreshold={5}
        onNodesChange={onNodesChange}
        proOptions={{ hideAttribution: true }}
        minZoom={0.3}
        maxZoom={1.6}
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
        {/*
          MiniMap colours via shadcn theme tokens. The project's `--card` / `--background` /
          `--muted-foreground` etc. are full `oklch(...)` values (not HSL components), so they
          must be referenced directly with `var(--token)` — wrapping them in `hsl(...)` produces
          `hsl(oklch(...))` which is invalid CSS and the browser collapses to plain black/white.
          For the translucent mask we use `color-mix(in oklch, var(--background) 60%, transparent)`
          which is the modern way to overlay-with-alpha against an oklch base.
        */}
        <MiniMap
          pannable
          zoomable
          position="bottom-right"
          bgColor="var(--card)"
          nodeColor="var(--muted-foreground)"
          nodeStrokeColor="var(--border)"
          nodeStrokeWidth={2}
          maskColor="color-mix(in oklch, var(--background) 60%, transparent)"
          maskStrokeColor="var(--primary)"
          maskStrokeWidth={1}
        />
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
