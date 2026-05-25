import { useCallback, useEffect, useLayoutEffect, useMemo, useReducer, useRef, useState } from 'react';
import type { ChunkSpan, SpanData, TickData } from '@/libs/profiler/model/traceModel';
import { resolveOwningSystem } from '@/libs/profiler/model/owningSystem';
import type { TimeRange, TrackLayout, TrackState, Viewport } from '@/libs/profiler/model/uiTypes';
import { computeGutterWidth, drawTimeArea } from '@/libs/profiler/canvas/timeArea';
import { hitTestTimeArea, type TimeAreaHover } from '@/libs/profiler/canvas/timeAreaHitTest';
import { buildLayout, deriveActiveSystems, deriveSlotInfo, deriveVisibleChunkSlots, deriveVisibleSpanMaxDepthBySlot, getVisibleTicks, RULER_HEIGHT, TRACK_GAP } from '@/libs/profiler/canvas/timeAreaLayout';
import { GAUGE_TRACK_ID_SET, getGaugeGroupSpec } from '@/libs/profiler/canvas/gauges/region';
import { buildGaugeTooltipLines, type GaugeData } from '@/libs/profiler/canvas/gauges/renderers';
import { getStudioThemeTokens } from '@/libs/profiler/canvas/theme';
import { GaugeTooltip } from '@/panels/profiler/components/GaugeTooltip';
import { HelpOverlay } from '@/panels/profiler/components/HelpOverlay';
import { TimeAreaFilterButton } from '@/panels/profiler/sections/TimeAreaFilterButton';
import { TimeAreaDisplayButton } from '@/panels/profiler/sections/TimeAreaDisplayButton';
import { TimeAreaHelpButton } from '@/panels/profiler/sections/TimeAreaHelpButton';
import { TimeAreaContextMenu } from '@/panels/profiler/sections/TimeAreaContextMenu';
import { getTrackHelpLines } from '@/libs/profiler/canvas/trackHelpLines';
import { buildHoverTooltipLines } from '@/libs/profiler/canvas/hoverTooltipLines';
import { openViewCallTree, registerAnimateViewport } from '@/shell/commands/profilerCommands';
import { openSourcePreview } from '@/shell/commands/openSchemaBrowser';
import { resolveFrameRootForSite } from '@/libs/profiler/resolveFrameRoot';
import { spanKindScope, systemScope, useCallTreeScopeStore } from '@/stores/useCallTreeScopeStore';
import { useCpuFrameStore } from '@/stores/useCpuFrameStore';
import { useSessionStore } from '@/stores/useSessionStore';
import { useOptionsStore } from '@/stores/useOptionsStore';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import type { ProfilerSelection } from '@/libs/profiler/model/traceModel';
import { useSelectionStore } from '@/stores/useSelectionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useSourceLocationStore, type ResolvedSourceLocation } from '@/stores/useSourceLocationStore';
import { useThemeStore } from '@/stores/useThemeStore';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';

/**
 * Main time area — renders the ruler + phases + thread-slot lanes (chunks + nested spans) +
 * operation mini-rows (page-cache / disk-io / transactions / wal / checkpoint).
 *
 * Gauges are **deferred to 2c** — this component leaves a clean seam in `buildLayout` for 2c to
 * inject the gauge tracks between the ruler and the slot lanes.
 *
 * Data source: `ticks` prop fed by the caller. The plan convention ("chunk cache is the only
 * data access") lands in the companion data-loader hook; this component is purely presentational
 * plus the React-wrapper concerns (pointer capture, native wheel, rAF, theme repaint).
 */

interface Props {
  ticks: TickData[];
  /** Gauge-region data bundle from `useProfilerCache`. */
  gaugeData: GaugeData;
  /**
   * Slot → thread-name mapping accumulated from `ThreadInfo` records (kind 77). Sourced from
   * `useProfilerCache`'s gauge bundle (the hook's `ProfilerGaugeData` carries it; the renderer-side
   * `GaugeData` type doesn't, so it's surfaced as its own prop). Used to label thread lanes —
   * falls back to `Slot N` when absent or empty.
   */
  threadNames?: Map<number, string>;
  /**
   * Slot → {name, kind} unified across replay and live modes (sourced from `useProfilerCache.threadInfos`).
   * Drives the section-filter popup's Threads → Main / Workers / Other subgrouping. Replay traces older than
   * cache v4 carry no kind byte; the popup defaults those slots to Other.
   */
  threadInfos?: Map<number, { name: string; kind: number }>;
  /** Pending-chunk µs-ranges from `useProfilerCache` — painted as a diagonal-stripe overlay. */
  pendingRangesUs: readonly { startUs: number; endUs: number }[];
  isLive?: boolean;
  /** Reported back up to ProfilerPanel so sibling sections can align to the same gutter column. */
  onGutterWidthChange?: (widthPx: number) => void;
}

const DRAG_THRESHOLD_PX = 3;
const ZOOM_ANIMATION_MS = 800;
const LERP_FACTOR = 0.15;   // fraction of remaining distance to close per rAF frame (~60 fps)

export default function TimeArea({ ticks, gaugeData, threadNames: threadNamesMap, threadInfos, pendingRangesUs, isLive: _isLive = false, onGutterWidthChange }: Props): React.JSX.Element {

  // `metadata` is read for `threadNames` (slot-label lookup) + gating the wheel handler before the
  // session has any data. Not used to derive viewRange — that comes from the profiler view store.
  const metadata = useProfilerSessionStore((s) => s.metadata);
  // TimeArea is the gesture-rate consumer: canvas paint + visible-depth memos + vpRef sync all read
  // the in-flight transient slot so the rendering stays at 60 Hz during pan/zoom. The committed
  // `viewRange` (read separately below) is reserved for effects that should fire once per settle
  // rather than once per gesture frame (history push, viewport sync from external writes).
  const viewRange = useProfilerViewStore((s) => s.transientViewRange);
  // Committed slot — only the two settle-driven effects depend on this. Effect bodies use this
  // name explicitly to make the intent obvious at the call site.
  const committedViewRange = useProfilerViewStore((s) => s.viewRange);
  const setTransientViewRange = useProfilerViewStore((s) => s.setTransientViewRange);
  const commitViewRange = useProfilerViewStore((s) => s.commitViewRange);
  const legendsVisible = useUiPrefsStore((s) => s.legendsVisible);

  // Centralised viewRange mutation. Writes the **transient** slot. The store debounces and copies
  // it to the committed slot after the user stops gesturing — so heavy cross-panel consumers
  // (SystemDag, CriticalPath) don't get re-triggered on every wheel notch. For animation-end /
  // programmatic atomicity, use `commitViewRange` directly (see the zoom-animation tween below).
  //
  // Post-#345: live-follow mode is gone — there's no auto-scroll to "pause" on user gesture.
  // First-tick init in `ProfilerPanel` pins the viewport once on attach; from there the user has
  // full control.
  const applyViewRange = useCallback((r: TimeRange) => {
    setTransientViewRange(r);
  }, [setTransientViewRange]);
  const gaugeRegionVisible = useProfilerViewStore((s) => s.gaugeRegionVisible);
  const gaugeCollapse = useProfilerViewStore((s) => s.gaugeCollapse);
  const setGaugeCollapse = useProfilerViewStore((s) => s.setGaugeCollapse);
  // Profiler selection now lives on the unified bus leaf (3E — the `useProfilerSelectionStore` silo was retired).
  // A profiler selection routes to the `span` leaf (or `tick`); other leaf types aren't ours → null. The selector
  // returns the leaf's stable `ref`, so re-render behaviour matches the old per-store subscription (no canvas churn
  // when an unrelated object is selected elsewhere).
  const selection = useSelectionStore((s) =>
    (s.leaf && (s.leaf.type === 'span' || s.leaf.type === 'tick')) ? (s.leaf.ref as ProfilerSelection) : null);

  // Right-click context menu on a span bar. Session id/kind drive the "View in Call Tree" action —
  // CPU sampling (and therefore the Call Tree) is `trace`-session only.
  const sessionId = useSessionStore((s) => s.sessionId);
  const sessionKind = useSessionStore((s) => s.kind);
  const setCallTreeScope = useCallTreeScopeStore((s) => s.setScope);
  const [contextMenu, setContextMenu] = useState<
    | { kind: 'span'; x: number; y: number; span: SpanData; sourceLoc: ResolvedSourceLocation | null }
    | { kind: 'chunk'; x: number; y: number; chunk: ChunkSpan; sourceLoc: ResolvedSourceLocation | null }
    | null
  >(null);

  // The view store's `viewRange` doubles as the TickOverview selection. `{0, 0}` = "no selection";
  // in that state TimeArea shows an empty-state placeholder, not the full trace. A range only
  // reaches the renderer once the user has made a selection (drag in overview, wheel-zoom, etc.).
  const hasSelection = viewRange.endUs > viewRange.startUs;

  const canvasRef = useRef<HTMLCanvasElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const scrollOverlayRef = useRef<HTMLDivElement>(null);
  const scrollPhantomRef = useRef<HTMLDivElement>(null);

  // Viewport snapshot — mutated imperatively by wheel + drag handlers before the next rAF. `scrollY`
  // is 0 while there's no vertical scrolling; when the layout's total height exceeds the container
  // the wrapper will animate it but that's a 2d concern.
  const vpRef = useRef<Viewport>({ offsetX: viewRange.startUs, scaleX: 0.5, scrollY: 0 });
  const gutterWidthRef = useRef<number>(80);
  const lastEmittedGutterRef = useRef<number>(-1);
  // React-state mirror of `gutterWidthRef`. Used to position the absolutely-positioned filter-icon
  // overlay in the ruler gutter — needs a re-render to track gutter-width changes (label-width
  // recomputation when slot/system names lengthen). Updated via the same throttling path as the
  // upward `onGutterWidthChange` callback.
  const [gutterWidth, setGutterWidth] = useState<number>(80);
  const crosshairXRef = useRef<number>(-1);
  const hoverRef = useRef<TimeAreaHover>(null);
  const dragRef = useRef<
    | { mode: 'select'; startX: number; currentX: number; moved: boolean }
    | { mode: 'pan'; startClientX: number; startClientY: number; startOffsetX: number; startScrollY: number; moved: boolean }
    | null
  >(null);
  const zoomAnimRef = useRef<{ from: TimeRange; to: TimeRange; startTime: number } | null>(null);
  const rafRef = useRef(0);
  // Per-slot-track animated heights (float pixels). Lerp toward the committed layout's target
  // height each rAF frame. Keyed by track id ("slot-N").
  const animTrackHeightsRef = useRef<Map<string, number>>(new Map());
  const isAnimatingRef      = useRef(false);
  // Ref mirror of the dynamicTrackHeight store value so the rAF closure never goes stale.
  const dynamicTrackHeightRef = useRef(false);
  // Track collapse — session-store, ephemeral. Lives there (not view-store) because slot/system
  // ids aren't stable across sessions; persisting would silently misalign collapse state with
  // a different thread on the same slot index next run. Lifted out of component-local state so
  // the section-filter popup can dispatch batch collapse / expand / double commands into it.
  const collapseState = useProfilerSessionStore((s) => s.collapseState);
  const setSingleCollapseState = useProfilerSessionStore((s) => s.setCollapseState);
  // Gauge tooltip — DOM overlay (not canvas) because multi-line coloured text is cleaner in
  // HTML. Updated when the hit-test lands on a gauge track; cleared when cursor leaves.
  const [gaugeTooltipState, setGaugeTooltipState] = useState<
    { trackId: string; localY: number; trackHeight: number; cursorUs: number; clientX: number; clientY: number } | null
  >(null);
  // "?" help glyph tooltip — trackId identifies which track the overlay renders help for.
  const [helpTooltipState, setHelpTooltipState] = useState<
    { trackId: string; label: string; clientX: number; clientY: number } | null
  >(null);
  const helpHoverRef = useRef<string | null>(null);
  // Hover tooltip for spans / chunks / phases / mini-row ops. Portaled DOM overlay via HelpOverlay
  // — gauges have their own dedicated tooltip (gaugeTooltipState), help has its own (helpTooltip-
  // State); anything else bar-shaped feeds this one.
  const [hoverTooltipState, setHoverTooltipState] = useState<
    { lines: readonly string[]; clientX: number; clientY: number } | null
  >(null);

  // Derive activeSlots + slotsWithChunks + spanMaxDepthBySlot from current ticks. Memoise on the
  // ticks reference (the data loader guarantees a stable identity until the cache version changes).
  const slotInfo = useMemo(() => deriveSlotInfo(ticks), [ticks]);
  const activeSystems = useMemo(() => deriveActiveSystems(ticks), [ticks]);

  // Thread names come from the `threadNames` prop — a `Map<slot, name>` that `aggregateGaugeData`
  // accumulates from `ThreadInfo` records (kind 77) as ticks are decoded. Trace + live both feed
  // it from `useProfilerCache.gaugeData.threadNames`. The previous code read from
  // `metadata.threadNames` which never existed on the server DTO, so lane labels were always
  // falling back to "Slot N". `buildLayout` wants a plain `Record<number, string>`, so convert
  // the Map (memoised on its identity — the chunk cache hands back the same reference until a new
  // ThreadInfo lands).
  // Depend on `threadNamesMap.size` in addition to the Map ref. The persistent Map (live mode's
  // `liveThreadNamesRef.current` and trace mode's `cache.threadNames`) keeps a stable reference
  // across renders to avoid unnecessary downstream invalidation, but its content GROWS as new
  // ThreadInfo records arrive (and on the trace side, as new chunks land). Reference-only deps
  // would cache the empty-Map result even after entries are added. Size as a secondary dep makes
  // the memo re-run whenever the Map gets bigger — append-only is the only mutation we do.
  // The Map ref is intentionally stable across renders (the persistent caches in
  // `useProfilerCache.liveThreadNamesRef` and `chunkCache.threadNames` mutate in place to survive
  // ring-buffer / chunk eviction). Reference-only deps would cache the empty-Map result even after
  // entries are added, so we also depend on `size` — append-only is the only mutation we do, so
  // size is a reliable freshness signal.
  const threadNamesSize = threadNamesMap?.size ?? 0;
  const threadNames = useMemo<Record<number, string> | null>(() => {
    void threadNamesSize;
    if (!threadNamesMap || threadNamesMap.size === 0) return null;
    const out: Record<number, string> = {};
    for (const [slot, name] of threadNamesMap) out[slot] = name;
    return out;
  }, [threadNamesMap, threadNamesSize]);
  // `metadata.systems` is a DTO-level array; map to `{[systemIndex]: name}` for O(1) lookup.
  const systemNames = useMemo(() => {
    const systems = metadata?.systems;
    if (!systems) return null;
    const out: Record<number, string> = {};
    for (const s of systems) {
      const idx = typeof s.index === 'number' ? s.index : Number(s.index);
      if (Number.isFinite(idx) && s.name) out[idx] = s.name;
    }
    return out;
  }, [metadata]);
  const perSystemLanesVisible = useProfilerViewStore((s) => s.perSystemLanesVisible);

  // Section-filter visibility maps from the filter popup. Slots/systems are session-ephemeral
  // (`useProfilerSessionStore`); gauges/engine-ops are persisted (`useProfilerViewStore`). Each map's
  // missing key = visible — see TimeAreaFilterButton for the popup that drives them.
  const slotVisibility = useProfilerSessionStore((s) => s.slotVisibility);
  const systemVisibility = useProfilerSessionStore((s) => s.systemVisibility);
  const gaugeVisibility = useProfilerViewStore((s) => s.gaugeVisibility);
  const engineOpVisibility = useProfilerViewStore((s) => s.engineOpVisibility);
  const spanColorMode = useProfilerViewStore((s) => s.spanColorMode);
  const dynamicTrackHeight = useProfilerViewStore((s) => s.dynamicTrackHeight);
  const showOffCpu = useProfilerViewStore((s) => s.showOffCpu);
  dynamicTrackHeightRef.current = dynamicTrackHeight;

  // Committed depth — the depth currently reflected in the layout. Grows immediately (before paint)
  // when the viewport reveals deeper spans; shrinks only after 300 ms of stable viewport so heights
  // never collapse while the user is actively panning. A useReducer counter (`bumpCommitted`) is
  // the sole trigger for a layout recompute — layout no longer depends on viewRange directly.
  const committedDepthRef      = useRef<Map<number, number>>(new Map());
  const committedChunkSlotsRef = useRef<Set<number>>(new Set());
  const visibleDepthRef        = useRef<Map<number, number>>(new Map());
  const visibleChunkSlotsRef   = useRef<Set<number>>(new Set());
  const shrinkTimerRef         = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [committedVersion, bumpCommitted] = useReducer((c: number) => c + 1, 0);

  // Current viewport depth + visible chunk slots — recomputed on every pan in a single tick pass.
  // Neither is fed to buildLayout directly; they drive the committed refs (grow/shrink effects).
  const { visibleDepth, visibleChunkSlots } = useMemo(() => {
    if (!dynamicTrackHeight) return { visibleDepth: new Map<number, number>(), visibleChunkSlots: new Set<number>() };
    return { visibleDepth: deriveVisibleSpanMaxDepthBySlot(ticks, viewRange), visibleChunkSlots: deriveVisibleChunkSlots(ticks, viewRange) };
  }, [dynamicTrackHeight, ticks, viewRange]);
  // Keep refs in sync so shrink-timer callbacks read freshest values regardless of capture time.
  visibleDepthRef.current = visibleDepth;
  visibleChunkSlotsRef.current = visibleChunkSlots;

  // Build layout once per (slotInfo, collapseState, visibility maps, committedVersion). When
  // dynamicTrackHeight is on, committedDepthRef.current holds the depth to use; layout no longer
  // depends on viewRange so it does not recompute on every pan frame.
  const layoutRef = useRef<{ tracks: readonly TrackLayout[]; totalHeight: number }>({ tracks: [], totalHeight: 0 });
  const layout = useMemo(() => {
    const spanMaxDepthBySlot = dynamicTrackHeight
      ? committedDepthRef.current
      : slotInfo.spanMaxDepthBySlot;
    const r = buildLayout({
      activeSlots: slotInfo.activeSlots,
      slotsWithChunks: slotInfo.slotsWithChunks,
      committedChunkSlots: dynamicTrackHeight ? committedChunkSlotsRef.current : undefined,
      spanMaxDepthBySlot,
      threadNames,
      collapseState,
      gaugeRegionVisible,
      gaugeCollapse,
      activeSystems,
      systemNames,
      perSystemLanesVisible,
      slotVisibility,
      systemVisibility,
      gaugeVisibility,
      engineOpVisibility,
    });
    layoutRef.current = r;
    return r;
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [slotInfo, dynamicTrackHeight, committedVersion, collapseState, threadNames, gaugeRegionVisible, gaugeCollapse, activeSystems, systemNames, perSystemLanesVisible, slotVisibility, systemVisibility, gaugeVisibility, engineOpVisibility]);

  // Grow committed depth + chunk slots immediately (useLayoutEffect = before paint) so track heights
  // never flash at the wrong size. When dynamicTrackHeight is toggled off, reset refs so the next
  // toggle-on starts clean.
  useLayoutEffect(() => {
    if (!dynamicTrackHeight) {
      committedDepthRef.current = new Map();
      committedChunkSlotsRef.current = new Set();
      animTrackHeightsRef.current = new Map();
      isAnimatingRef.current = false;
      return;
    }
    let grew = false;
    const nextDepth = new Map(committedDepthRef.current);
    for (const [slot, vd] of visibleDepth) {
      if (vd > (nextDepth.get(slot) ?? -1)) { nextDepth.set(slot, vd); grew = true; }
    }
    if (grew) committedDepthRef.current = nextDepth;

    const prevChunk = committedChunkSlotsRef.current;
    let chunkGrew = false;
    for (const slot of visibleChunkSlots) {
      if (!prevChunk.has(slot)) { chunkGrew = true; break; }
    }
    if (chunkGrew) {
      const nextChunk = new Set(prevChunk);
      for (const slot of visibleChunkSlots) nextChunk.add(slot);
      committedChunkSlotsRef.current = nextChunk;
    }

    if (grew || chunkGrew) bumpCommitted();
  }, [dynamicTrackHeight, visibleDepth, visibleChunkSlots, bumpCommitted]);

  // Shrink committed depth + chunk slots 300 ms after the viewport settles. Cancelled and rescheduled
  // on every pan so heights never collapse while the user is actively moving. Timer callbacks read
  // from the *Ref.current variants (not stale closure values) so they always shrink to what is
  // actually visible at fire time.
  useEffect(() => {
    if (!dynamicTrackHeight) return;
    if (shrinkTimerRef.current !== null) { clearTimeout(shrinkTimerRef.current); shrinkTimerRef.current = null; }
    let hasShrinks = false;
    for (const [slot, cd] of committedDepthRef.current) {
      const vd = visibleDepth.get(slot);
      if (vd === undefined || vd < cd) { hasShrinks = true; break; }
    }
    if (!hasShrinks) {
      for (const slot of committedChunkSlotsRef.current) {
        if (!visibleChunkSlots.has(slot)) { hasShrinks = true; break; }
      }
    }
    if (!hasShrinks) return;
    shrinkTimerRef.current = setTimeout(() => {
      shrinkTimerRef.current = null;
      const cv = visibleDepthRef.current;
      const shrunk = new Map<number, number>();
      for (const [slot] of committedDepthRef.current) {
        const vd = cv.get(slot);
        if (vd !== undefined) shrunk.set(slot, vd);
      }
      for (const [slot, vd] of cv) { if (!shrunk.has(slot)) shrunk.set(slot, vd); }
      committedDepthRef.current = shrunk;

      const cvc = visibleChunkSlotsRef.current;
      const shrunkChunk = new Set<number>();
      for (const slot of committedChunkSlotsRef.current) { if (cvc.has(slot)) shrunkChunk.add(slot); }
      for (const slot of cvc) shrunkChunk.add(slot);
      committedChunkSlotsRef.current = shrunkChunk;

      bumpCommitted();
    }, 300);
    return () => { if (shrinkTimerRef.current !== null) { clearTimeout(shrinkTimerRef.current); shrinkTimerRef.current = null; } };
  }, [dynamicTrackHeight, visibleDepth, visibleChunkSlots, bumpCommitted]);

  // Scroll anchor compensation — when dynamicTrackHeight is on, track heights change every pan and
  // the total canvas height shifts. Without compensation the content jumps vertically. We record
  // which track straddles scrollY in the old layout, find it by id in the new layout, and adjust
  // scrollY by the delta so the same track edge stays at the same screen position.
  // useLayoutEffect fires after commit but before paint, so the rAF draw picks up the correction.
  const prevLayoutForAnchorRef = useRef<{ tracks: readonly TrackLayout[]; totalHeight: number }>({ tracks: [], totalHeight: 0 });
  useLayoutEffect(() => {
    const prevLayout = prevLayoutForAnchorRef.current;
    prevLayoutForAnchorRef.current = layout;
    if (!dynamicTrackHeight || prevLayout.tracks.length === 0) return;
    const scrollY = vpRef.current.scrollY;
    if (scrollY === 0) return;

    // Find anchor: the track whose vertical band contains scrollY. Use the next track's y as the
    // bottom boundary so we don't need to replicate the advance formula from buildLayout.
    let anchorId: string | null = null;
    let anchorOffset = 0;
    const pt = prevLayout.tracks;
    for (let i = 0; i < pt.length; i++) {
      const nextY = i + 1 < pt.length ? pt[i + 1].y : prevLayout.totalHeight;
      if (nextY > scrollY) {
        anchorId = pt[i].id;
        anchorOffset = scrollY - pt[i].y;
        break;
      }
    }
    if (!anchorId) return;

    const newTrack = layout.tracks.find(t => t.id === anchorId);
    if (!newTrack) return;
    const newScrollY = newTrack.y + anchorOffset;
    if (Math.abs(newScrollY - scrollY) < 0.5) return;

    const canvas = canvasRef.current;
    const containerH = canvas?.getBoundingClientRect().height ?? 0;
    const maxScroll = Math.max(0, layout.totalHeight - containerH);
    const clamped = Math.max(0, Math.min(maxScroll, newScrollY));
    vpRef.current.scrollY = clamped;
    const overlay = scrollOverlayRef.current;
    if (overlay && Math.abs(overlay.scrollTop - clamped) > 0.5) {
      overlay.scrollTop = clamped;
    }
  }, [layout, dynamicTrackHeight]);

  const render = useCallback((): void => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    if (!ctx) return;

    // Measure gutter width from the widest label — uses the same font the draw path sets.
    const gutter = computeGutterWidth(ctx, layout.tracks, legendsVisible);
    gutterWidthRef.current = gutter;
    if (lastEmittedGutterRef.current !== gutter) {
      lastEmittedGutterRef.current = gutter;
      const w = gutter;
      // Defer the React state + parent-callback fan-out off the rAF tick — setState during render
      // is a no-no, and queueMicrotask runs after the current frame's draw completes.
      queueMicrotask(() => {
        setGutterWidth(w);
        onGutterWidthChange?.(w);
      });
    }

    // Advance zoom animation if one's in flight. Intermediate frames write the transient slot so
    // TickOverview's mirror animates smoothly. The FINAL frame commits atomically via
    // `commitViewRange` so cross-panel consumers (SystemDag, CriticalPath, …) see the settled
    // position immediately rather than after the post-animation debounce window.
    const anim = zoomAnimRef.current;
    if (anim) {
      const elapsed = performance.now() - anim.startTime;
      const rawT = Math.min(elapsed / ZOOM_ANIMATION_MS, 1);
      const t = 1 - (1 - rawT) * (1 - rawT) * (1 - rawT); // ease-out cubic
      const curStart = anim.from.startUs + (anim.to.startUs - anim.from.startUs) * t;
      const curEnd = anim.from.endUs + (anim.to.endUs - anim.from.endUs) * t;
      if (rawT >= 1) {
        // Animation end — commit atomically, bypass debounce.
        commitViewRange({ startUs: anim.to.startUs, endUs: anim.to.endUs });
        zoomAnimRef.current = null;
      } else {
        applyViewRange({ startUs: curStart, endUs: curEnd });
        cancelAnimationFrame(rafRef.current);
        rafRef.current = requestAnimationFrame(() => render());
      }
    }

    const dragSelection = dragRef.current?.mode === 'select'
      ? { x1: Math.min(dragRef.current.startX, dragRef.current.currentX), x2: Math.max(dragRef.current.startX, dragRef.current.currentX) }
      : null;

    // Advance track-height animation. Each frame lerps animated heights toward the committed target.
    // Only expanded slot-N tracks ever change height with dynamic depth; all other tracks are fixed.
    let frameTracks: readonly TrackLayout[] = layout.tracks;
    if (dynamicTrackHeightRef.current) {
      const animH = animTrackHeightsRef.current;
      let stillAnimating = false;
      for (const track of layout.tracks) {
        if (!track.id.startsWith('slot-') || track.state !== 'expanded') continue;
        const target = track.height;
        const cur = animH.get(track.id) ?? target;
        const diff = target - cur;
        if (Math.abs(diff) < 0.5) {
          animH.set(track.id, target);
        } else {
          animH.set(track.id, cur + diff * LERP_FACTOR);
          stillAnimating = true;
        }
      }
      if (stillAnimating || isAnimatingRef.current) {
        // Rebuild track list with animated heights + recalculated Y positions.
        const patched: TrackLayout[] = [];
        let yOffset = 0;
        for (const track of layout.tracks) {
          if (track.id.startsWith('slot-') && track.state === 'expanded') {
            const h = animH.get(track.id) ?? track.height;
            patched.push({ ...track, y: track.y + yOffset, height: h });
            yOffset += h - track.height;
          } else {
            patched.push(yOffset === 0 ? track : { ...track, y: track.y + yOffset });
          }
        }
        frameTracks = patched;
        const frameTotalH = layout.totalHeight + yOffset;
        const phantom = scrollPhantomRef.current;
        if (phantom) phantom.style.height = `${frameTotalH}px`;
      }
      if (!stillAnimating && isAnimatingRef.current) {
        // Animation just settled — snap phantom back to committed height.
        const phantom = scrollPhantomRef.current;
        if (phantom) phantom.style.height = `${layout.totalHeight}px`;
      }
      isAnimatingRef.current = stillAnimating;
      if (stillAnimating) {
        cancelAnimationFrame(rafRef.current);
        rafRef.current = requestAnimationFrame(() => render());
      }
    }

    drawTimeArea(canvas, {
      visibleTicks: getVisibleTicks(ticks, viewRange),
      ticks,
      tracks: frameTracks,
      viewRange,
      vp: vpRef.current,
      gutterWidth: gutter,
      legendsVisible,
      selection,
      hover: hoverRef.current,
      dragSelection: dragSelection && dragSelection.x2 - dragSelection.x1 > DRAG_THRESHOLD_PX ? dragSelection : null,
      crosshairX: crosshairXRef.current,
      gaugeData,
      helpHover: helpHoverRef.current,
      pendingRangesUs,
      spanColorMode,
      showOffCpu,
    }, getStudioThemeTokens());
  }, [layout, ticks, viewRange, legendsVisible, selection, applyViewRange, commitViewRange, onGutterWidthChange, gaugeData, pendingRangesUs, spanColorMode, showOffCpu]);

  const scheduleRender = useCallback((): void => {
    cancelAnimationFrame(rafRef.current);
    rafRef.current = requestAnimationFrame(() => {
      try { render(); } catch (err) { console.error('TimeArea render failed:', err); }
    });
  }, [render]);

  useEffect(() => {
    scheduleRender();
    const obs = new ResizeObserver(() => scheduleRender());
    if (containerRef.current) obs.observe(containerRef.current);
    return () => { obs.disconnect(); cancelAnimationFrame(rafRef.current); };
  }, [scheduleRender]);

  // Theme toggle fires a repaint — deps-list of the render callback doesn't cover CSS var changes.
  const theme = useThemeStore((s) => s.theme);
  useEffect(() => { scheduleRender(); }, [theme, scheduleRender]);

  // Nav-history — viewport is the primary navigation event (matches the old profiler). Each entry
  // captures `{viewRange, selection-at-that-moment}`. Pan/zoom/drag-to-zoom/Ctrl+Home/animateToRange
  // all mutate `viewRange`, and the debounce below coalesces rapid wheel/pan bursts into one entry.
  //
  // Selection changes don't push a new entry — they patch the top entry in place via
  // `updateTopSelection`. Rationale: selecting a span at the current viewport isn't "traveling
  // somewhere else," it's marking what you were looking at. Walking back should restore both the
  // viewport and the last span you had highlighted at that viewport.
  //
  // Restore detection: after back()/forward() writes viewRange, this effect re-fires. We compare
  // the tip entry's viewRange to the current one — if they match (reference or value), skip push.
  // `isRestoring` isn't reliable here because it's flipped back synchronously long before the
  // 250 ms timer fires.
  const selectionRef = useRef<typeof selection>(selection);
  useEffect(() => { selectionRef.current = selection; }, [selection]);

  useEffect(() => {
    // Driven by the committed slot — fires once per settled change rather than once per gesture
    // frame. The 250 ms internal debounce remains as a second-line coalescer in case the user
    // chains rapid commits (e.g., history back/forward).
    if (committedViewRange.endUs <= committedViewRange.startUs) return;
    const timer = setTimeout(() => {
      const nav = useNavHistoryStore.getState();
      const top = nav.pointer >= 0 ? nav.entries[nav.pointer] : null;
      if (top?.kind === 'profiler-selected'
        && top.viewRange.startUs === committedViewRange.startUs
        && top.viewRange.endUs === committedViewRange.endUs) {
        return;
      }
      nav.push({
        kind: 'profiler-selected',
        selection: selectionRef.current,
        viewRange: committedViewRange,
        timestamp: Date.now(),
      });
    }, 250);
    return () => clearTimeout(timer);
  }, [committedViewRange]);

  // Selection change → patch the top entry so back/forward remembers what was highlighted here.
  useEffect(() => {
    useNavHistoryStore.getState().updateTopSelection(selection);
  }, [selection]);

  // Viewport sync — fires when the *committed* slot changes (external writes: TickOverview
  // commit, history back/forward, URL deep-link, programmatic animation end). TimeArea's own
  // transient writes do NOT flow through here because the pointer handlers already mutate vpRef
  // directly. Driven by `committedViewRange` to avoid re-firing on every gesture frame.
  useEffect(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const contentWidth = rect.width - gutterWidthRef.current;
    const rangeUs = committedViewRange.endUs - committedViewRange.startUs;
    if (rangeUs > 0 && contentWidth > 0) {
      vpRef.current.offsetX = committedViewRange.startUs;
      vpRef.current.scaleX = contentWidth / rangeUs;
    }
    scheduleRender();
  }, [committedViewRange, scheduleRender]);

  // ─── Animation helper ────────────────────────────────────────────────────────────────────────
  const animateToRange = useCallback((target: TimeRange): void => {
    zoomAnimRef.current = {
      from: { startUs: viewRange.startUs, endUs: viewRange.endUs },
      to: target,
      startTime: performance.now(),
    };
    scheduleRender();
  }, [viewRange, scheduleRender]);

  // Expose the tween to the rest of the app (nav-history restore, etc.) via a module-level
  // register slot. The wrapping useEffect re-registers whenever the callback identity changes so
  // the registered closure always captures the latest `viewRange` + `scheduleRender`.
  useEffect(() => {
    registerAnimateViewport(animateToRange);
    return () => registerAnimateViewport(null);
  }, [animateToRange]);

  // ─── Pointer handlers ────────────────────────────────────────────────────────────────────────
  const getLocal = (e: { clientX: number; clientY: number }): { mx: number; my: number } | null => {
    const canvas = canvasRef.current;
    if (!canvas) return null;
    const rect = canvas.getBoundingClientRect();
    return { mx: e.clientX - rect.left, my: e.clientY - rect.top };
  };

  const onPointerDown = useCallback((e: React.PointerEvent<HTMLCanvasElement>): void => {
    const local = getLocal(e);
    if (!local) return;
    const canvas = canvasRef.current;
    if (!canvas) return;

    if (e.button === 0) {
      // Gutter chevron → toggle collapse
      if (local.mx < gutterWidthRef.current) {
        const hit = hitTestTimeArea({
          mx: local.mx, my: local.my,
          tracks: layoutRef.current.tracks, ticks, vp: vpRef.current,
          gutterWidth: gutterWidthRef.current,
          legendsVisible,
          offCpuBySlot: gaugeData.offCpuBySlot,
          showOffCpu,
        });
        if (hit && hit.kind === 'gutter-chevron') {
          if (GAUGE_TRACK_ID_SET.has(hit.trackId)) {
            // Gauge tracks cycle 3 states (summary → expanded → double → summary). Persisted
            // via the view store so the state survives reload.
            const cur = gaugeCollapse[hit.trackId] ?? 'expanded';
            const next: TrackState = cur === 'summary' ? 'expanded' : cur === 'expanded' ? 'double' : 'summary';
            setGaugeCollapse(hit.trackId, next);
          } else {
            const cur = collapseState[hit.trackId] ?? 'expanded';
            const next: TrackState = cur === 'summary' ? 'expanded' : 'summary';
            setSingleCollapseState(hit.trackId, next);
          }
          e.preventDefault();
        }
        return;
      }

      // Shift+left → pan; plain left → drag-to-zoom
      const mode: 'select' | 'pan' = e.shiftKey ? 'pan' : 'select';
      e.preventDefault();
      if (mode === 'pan') {
        dragRef.current = {
          mode: 'pan',
          startClientX: e.clientX,
          startClientY: e.clientY,
          startOffsetX: vpRef.current.offsetX,
          startScrollY: vpRef.current.scrollY,
          moved: false,
        };
      } else {
        dragRef.current = { mode: 'select', startX: local.mx, currentX: local.mx, moved: false };
      }
      try { canvas.setPointerCapture(e.pointerId); } catch { /* noop */ }
    } else if (e.button === 1) {
      // Middle-drag → pan (X and Y)
      e.preventDefault();
      dragRef.current = {
        mode: 'pan',
        startClientX: e.clientX,
        startClientY: e.clientY,
        startOffsetX: vpRef.current.offsetX,
        startScrollY: vpRef.current.scrollY,
        moved: false,
      };
      try { canvas.setPointerCapture(e.pointerId); } catch { /* noop */ }
    }
  }, [ticks, gaugeCollapse, setGaugeCollapse, collapseState, setSingleCollapseState, legendsVisible, gaugeData.offCpuBySlot, showOffCpu]);

  const onPointerMove = useCallback((e: React.PointerEvent<HTMLCanvasElement>): void => {
    const local = getLocal(e);
    if (!local) return;
    crosshairXRef.current = local.mx;

    const drag = dragRef.current;
    if (drag) {
      if (drag.mode === 'select') {
        const dx = local.mx - drag.startX;
        if (!drag.moved && Math.abs(dx) < DRAG_THRESHOLD_PX) {
          scheduleRender();
          return;
        }
        drag.moved = true;
        drag.currentX = local.mx;
      } else {
        const dxClient = e.clientX - drag.startClientX;
        const dyClient = e.clientY - drag.startClientY;
        if (!drag.moved && Math.abs(dxClient) < DRAG_THRESHOLD_PX && Math.abs(dyClient) < DRAG_THRESHOLD_PX) return;
        drag.moved = true;

        // Horizontal pan — translates pointer delta into world µs via the current X scale.
        const deltaUs = -dxClient / vpRef.current.scaleX;
        vpRef.current.offsetX = drag.startOffsetX + deltaUs;
        const canvas = canvasRef.current;
        if (canvas) {
          const rect = canvas.getBoundingClientRect();
          const contentWidth = rect.width - gutterWidthRef.current;
          applyViewRange({
            startUs: vpRef.current.offsetX,
            endUs: vpRef.current.offsetX + contentWidth / vpRef.current.scaleX,
          });
        }

        // Vertical pan — grab-and-drag UX: dragging the cursor down should reveal content above
        // (i.e. scroll up), so subtract the client-Y delta. Clamp to [0, totalHeight - viewport]
        // so we never overshoot the bottom of the layout. We push the new scrollY into the
        // overlay's scrollTop so the native scrollbar thumb tracks the drag, and the renderer
        // already reads vp.scrollY directly so the next scheduleRender() picks it up.
        const overlay = scrollOverlayRef.current;
        const containerH = canvas?.getBoundingClientRect().height ?? 0;
        const maxScroll = Math.max(0, layoutRef.current.totalHeight - containerH);
        const proposed = drag.startScrollY - dyClient;
        const clamped = Math.max(0, Math.min(maxScroll, proposed));
        vpRef.current.scrollY = clamped;
        if (overlay && Math.abs(overlay.scrollTop - clamped) > 0.5) {
          overlay.scrollTop = clamped;
        }
      }
      scheduleRender();
      return;
    }

    // Hover → hit-test for tooltip + cursor shape feedback (cursor-change deferred to 2f)
    const hover = hitTestTimeArea({
      mx: local.mx, my: local.my,
      tracks: layoutRef.current.tracks, ticks, vp: vpRef.current,
      gutterWidth: gutterWidthRef.current,
      legendsVisible,
      offCpuBySlot: gaugeData.offCpuBySlot,
      showOffCpu,
    });
    hoverRef.current = hover;

    // Gauge hovers feed the DOM-overlay tooltip; non-gauge hovers clear it.
    if (hover && hover.kind === 'gauge') {
      setGaugeTooltipState({
        trackId: hover.trackId,
        localY: hover.localY,
        trackHeight: hover.trackHeight,
        cursorUs: hover.cursorUs,
        clientX: e.clientX,
        clientY: e.clientY,
      });
    } else if (gaugeTooltipState !== null) {
      setGaugeTooltipState(null);
    }

    // "?" help glyph — brighten the glyph on canvas + show the HelpOverlay.
    if (hover && hover.kind === 'help') {
      if (helpHoverRef.current !== hover.trackId) {
        helpHoverRef.current = hover.trackId;
      }
      setHelpTooltipState({ trackId: hover.trackId, label: hover.label, clientX: e.clientX, clientY: e.clientY });
    } else if (helpHoverRef.current !== null || helpTooltipState !== null) {
      helpHoverRef.current = null;
      setHelpTooltipState(null);
    }

    // Span / chunk / phase / mini-row-op hovers → generic multi-line tooltip. Gauge / help / other
    // kinds return null from the builder, which clears the overlay.
    const lines = buildHoverTooltipLines(hover);
    if (lines) {
      setHoverTooltipState({ lines, clientX: e.clientX, clientY: e.clientY });
    } else if (hoverTooltipState !== null) {
      setHoverTooltipState(null);
    }
    scheduleRender();
  }, [ticks, scheduleRender, applyViewRange, gaugeTooltipState, legendsVisible, helpTooltipState, hoverTooltipState, gaugeData.offCpuBySlot, showOffCpu]);

  const onPointerUp = useCallback((e: React.PointerEvent<HTMLCanvasElement>): void => {
    const drag = dragRef.current;
    dragRef.current = null;
    const canvas = canvasRef.current;
    try { canvas?.releasePointerCapture(e.pointerId); } catch { /* noop */ }
    if (!drag) return;

    if (drag.mode === 'select') {
      if (drag.moved) {
        // Drag-to-zoom — convert pixel range to time range, kick the 800 ms tween
        const x1 = Math.min(drag.startX, drag.currentX);
        const x2 = Math.max(drag.startX, drag.currentX);
        const vp = vpRef.current;
        const gutter = gutterWidthRef.current;
        const startUs = vp.offsetX + (x1 - gutter) / vp.scaleX;
        const endUs = vp.offsetX + (x2 - gutter) / vp.scaleX;
        if (endUs > startUs) animateToRange({ startUs, endUs });
      } else {
        // Click-without-drag → selection
        const local = getLocal(e);
        if (!local) return;
        const hit = hitTestTimeArea({
          mx: local.mx, my: local.my,
          tracks: layoutRef.current.tracks, ticks, vp: vpRef.current,
          gutterWidth: gutterWidthRef.current,
          legendsVisible,
          offCpuBySlot: gaugeData.offCpuBySlot,
          showOffCpu,
        });
        if (hit) {
          routeSelection(hit, ticks);
        }
      }
    }
    scheduleRender();
  }, [ticks, animateToRange, scheduleRender, legendsVisible, gaugeData.offCpuBySlot, showOffCpu]);

  const onPointerLeave = useCallback((): void => {
    if (dragRef.current) return; // captured drag continues
    crosshairXRef.current = -1;
    hoverRef.current = null;
    if (gaugeTooltipState !== null) setGaugeTooltipState(null);
    if (helpHoverRef.current !== null || helpTooltipState !== null) {
      helpHoverRef.current = null;
      setHelpTooltipState(null);
    }
    if (hoverTooltipState !== null) setHoverTooltipState(null);
    scheduleRender();
  }, [scheduleRender, gaugeTooltipState, helpTooltipState, hoverTooltipState]);

  // Double-click a chunk / span / phase / mini-row op → smooth-zoom the viewport to its bounds.
  // Ported verbatim from the old profiler's `onDblClick`. Tick / gutter-chevron hits are ignored —
  // nothing meaningful to zoom to there. The 800 ms ease-out tween lives in `animateToRange`.
  //
  // #302: Ctrl+double-click on a span / chunk opens the inline source-preview panel for that
  // emission site (when the span carries a sourceLocationId and the manifest has resolved it).
  // Falls through to zoom when source attribution isn't available so the gesture is never a
  // dead-end on un-attributed spans (e.g. non-Engine call sites).
  const onDoubleClick = useCallback((e: React.MouseEvent<HTMLCanvasElement>): void => {
    const local = getLocal(e);
    if (!local) return;
    const hit = hitTestTimeArea({
      mx: local.mx, my: local.my,
      tracks: layoutRef.current.tracks, ticks, vp: vpRef.current,
      gutterWidth: gutterWidthRef.current,
      legendsVisible,
      offCpuBySlot: gaugeData.offCpuBySlot,
      showOffCpu,
    });
    if (!hit) return;

    if (e.ctrlKey && hit.kind === 'span') {
      const siteId = hit.span.rawEvent?.sourceLocationId;
      const loc = useSourceLocationStore.getState().resolve(siteId);
      if (loc) {
        void useOptionsStore.getState().openInEditor(loc.file, loc.line);
        return;
      }
      // No attribution → fall through to zoom-to-span.
    }

    if (e.ctrlKey && hit.kind === 'chunk') {
      const loc = useSourceLocationStore.getState().resolveSystem(hit.chunk.systemIndex);
      if (loc) {
        void useOptionsStore.getState().openInEditor(loc.file, loc.line);
        return;
      }
      // No PDB attribution → fall through to zoom-to-chunk.
    }

    switch (hit.kind) {
      case 'chunk':       animateToRange({ startUs: hit.chunk.startUs, endUs: hit.chunk.endUs }); return;
      case 'span':        animateToRange({ startUs: hit.span.startUs,  endUs: hit.span.endUs  }); return;
      case 'phase':       animateToRange({ startUs: hit.phase.startUs, endUs: hit.phase.endUs }); return;
      case 'mini-row-op': animateToRange({ startUs: hit.op.startUs,    endUs: hit.op.endUs    }); return;
      default: return; // 'tick', 'gutter-chevron' — no-op
    }
  }, [ticks, animateToRange, legendsVisible, gaugeData.offCpuBySlot, showOffCpu]);

  // ─── Context menu ────────────────────────────────────────────────────────────────────────────
  // Right-click → context menu for a span bar or a scheduler-chunk bar. The browser's native menu is
  // always suppressed over the canvas — it distracts and offers nothing useful for a visualization.
  // Other hits (phase / gauge / off-CPU / empty) just dismiss our menu without opening it.
  const onContextMenu = useCallback((e: React.MouseEvent<HTMLCanvasElement>): void => {
    e.preventDefault();
    const local = getLocal(e);
    if (!local) return;
    const hit = hitTestTimeArea({
      mx: local.mx, my: local.my,
      tracks: layoutRef.current.tracks, ticks, vp: vpRef.current,
      gutterWidth: gutterWidthRef.current,
      legendsVisible,
      offCpuBySlot: gaugeData.offCpuBySlot,
      showOffCpu,
    });
    if (hit && hit.kind === 'span') {
      const sourceLoc = useSourceLocationStore.getState().resolve(hit.span.rawEvent?.sourceLocationId);
      setContextMenu({ kind: 'span', x: e.clientX, y: e.clientY, span: hit.span, sourceLoc });
    } else if (hit && hit.kind === 'chunk') {
      const sourceLoc = useSourceLocationStore.getState().resolveSystem(hit.chunk.systemIndex);
      setContextMenu({ kind: 'chunk', x: e.clientX, y: e.clientY, chunk: hit.chunk, sourceLoc });
    } else {
      setContextMenu(null);
    }
  }, [ticks, legendsVisible, gaugeData.offCpuBySlot, showOffCpu]);

  // "View in Call Tree (merged)" — scope the Call Tree to every instance of this span's kind (#351
  // span-kind scope) and re-root it at the span's emitting method, so the tree opens rooted on that
  // method (percentages relative to it, no ancestor noise — §8.2). Falls back to no re-root when the
  // method isn't resolvable / wasn't sampled.
  const handleViewInCallTreeMerged = useCallback((span: SpanData, sourceLoc: ResolvedSourceLocation | null): void => {
    if (!sessionId) return;
    const frameRoot = resolveFrameRootForSite(sourceLoc, useCpuFrameStore.getState().byId);
    setCallTreeScope(sessionId, spanKindScope(span.kind, span.name, frameRoot));
    openViewCallTree();
  }, [sessionId, setCallTreeScope]);

  // "View in Call Tree (system)" — scope the Call Tree to the chunk's system (#351 system scope) and
  // re-root it at the system's method, same rationale as the span path above.
  const handleViewInCallTreeSystem = useCallback((chunk: ChunkSpan, sourceLoc: ResolvedSourceLocation | null): void => {
    if (!sessionId) return;
    const frameRoot = resolveFrameRootForSite(sourceLoc, useCpuFrameStore.getState().byId);
    setCallTreeScope(sessionId, systemScope(chunk.systemIndex, chunk.systemName || `System ${chunk.systemIndex}`, frameRoot));
    openViewCallTree();
  }, [sessionId, setCallTreeScope]);

  // ─── Native wheel listener ──────────────────────────────────────────────────────────────────
  // React's synthetic wheel is passive — preventDefault on Ctrl+wheel would be ignored and the
  // browser would zoom the page. Attach natively with {passive:false}.
  const handleWheelRef = useRef<(e: WheelEvent) => void>(() => {});
  handleWheelRef.current = (e: WheelEvent) => {
    if (ticks.length === 0 && !metadata) return;
    e.preventDefault();
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const vp = vpRef.current;
    const gutter = gutterWidthRef.current;
    const contentWidth = rect.width - gutter;
    const mouseX = Math.max(0, e.clientX - rect.left - gutter);

    if (e.ctrlKey) {
      // Ctrl+Wheel — vertical scroll through the lane stack. Same UX as the gutter scrollbar drag
      // (which itself drives vp.scrollY via the overlay scrollTop). Clamped so we never overshoot
      // the layout bottom. Delta is taken raw from deltaY — wheel "lines" feel right at this scale;
      // trackpads pre-scale to pixels and feel right too.
      const containerH = rect.height;
      const maxScroll = Math.max(0, layoutRef.current.totalHeight - containerH);
      const proposed = vp.scrollY + e.deltaY;
      const clamped = Math.max(0, Math.min(maxScroll, proposed));
      vp.scrollY = clamped;
      const overlay = scrollOverlayRef.current;
      if (overlay && Math.abs(overlay.scrollTop - clamped) > 0.5) {
        overlay.scrollTop = clamped;
      }
      // No applyViewRange — viewport time range is unchanged by vertical scroll.
      scheduleRender();
      return;
    }

    if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
      // Horizontal pan
      const delta = e.shiftKey ? e.deltaY : e.deltaX;
      vp.offsetX += delta / vp.scaleX;
    } else {
      // Zoom around cursor.
      // scaleX = pixels per microsecond. Floor at 0.0001 → ~10 s visible at 1000 px content width;
      // ceiling at 10000 → ~0.1 ns visible at 1000 px (deep zoom-in for tight spans).
      const usAtMouse = vp.offsetX + mouseX / vp.scaleX;
      const factor = e.deltaY > 0 ? 0.85 : 1.18;
      vp.scaleX = Math.max(0.0001, Math.min(10000, vp.scaleX * factor));
      vp.offsetX = usAtMouse - mouseX / vp.scaleX;
    }
    applyViewRange({ startUs: vp.offsetX, endUs: vp.offsetX + contentWidth / vp.scaleX });
    scheduleRender();
  };

  useEffect(() => {
    // Re-run on `hasSelection` changes because the canvas is absent from the tree when no range
    // is selected (empty-state placeholder instead). Without this dep the listener would miss
    // the canvas's first mount after the user drags a selection in TickOverview.
    const canvas = canvasRef.current;
    if (!canvas) return;
    const listener = (e: WheelEvent): void => handleWheelRef.current(e);
    canvas.addEventListener('wheel', listener, { passive: false });
    return () => canvas.removeEventListener('wheel', listener);
  }, [hasSelection]);

  // ─── Render ──────────────────────────────────────────────────────────────────────────────────
  if (!hasSelection) {
    return (
      <div
        ref={containerRef}
        className="flex h-full w-full items-center justify-center overflow-hidden select-none bg-background text-center text-fs-base text-muted-foreground"
      >
        <span>Drag a range in the tick overview above to show details.</span>
      </div>
    );
  }
  return (
    <div ref={containerRef} className="relative h-full w-full overflow-hidden select-none">
      <canvas
        ref={canvasRef}
        data-testid="profiler-time-area-canvas"
        className="absolute inset-0 h-full w-full touch-none"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onPointerLeave={onPointerLeave}
        onDoubleClick={onDoubleClick}
        onContextMenu={onContextMenu}
      />
      {/*
       * Vertical scroll overlay. Sits on the right edge of the canvas as a thin native-scrollbar
       * gutter — when its phantom inner is taller than the gutter, the browser's overflow scrollbar
       * appears and the user can drag it. We forward the resulting `scrollTop` to `vpRef.current.scrollY`
       * so the canvas redraws with the correct vertical offset (`drawTimeArea` already honours
       * `vp.scrollY`). The gutter is 14px wide — wide enough for a visible Windows scrollbar without
       * eating much canvas real-estate. We don't shrink the canvas to make room because the renderer
       * already reserves trailing margin and the overlap is harmless.
       *
       * The overlay only hosts the scrollbar — no pointer events for the rest of the canvas (zoom,
       * pan, drag, hit-test) are affected, since the rest of the canvas is left exposed.
       */}
      <div
        ref={scrollOverlayRef}
        className="absolute right-0 bottom-0 w-[14px] overflow-y-auto"
        style={{ top: RULER_HEIGHT + TRACK_GAP }}
        onScroll={(e) => {
          vpRef.current.scrollY = e.currentTarget.scrollTop;
          scheduleRender();
        }}
      >
        <div ref={scrollPhantomRef} style={{ height: layout.totalHeight - (RULER_HEIGHT + TRACK_GAP), width: 1 }} aria-hidden />
      </div>
      {/*
       * Gutter icon cluster — pinned to the right edge of the ruler row's gutter band, same column as
       * the gutter labels. Distinct concerns behind distinct icons:
       *   - Display options (sliders) — span colour lens, off-CPU overlay, dynamic track height.
       *   - Section filter (funnel)   — search input + tri-state tree (Gauges / Threads / Systems /
       *     Engine Operations) controlling which sections render.
       *   - Help (?)                  — how-to-use dialog; only mounted when inline legends are on,
       *     mirroring the per-track "?" canvas glyphs. Each icon is 20 px wide + a 2 px gap, so the
       *     cluster is 64 px with help / 42 px without — `left` is offset to keep it right-anchored.
       * The wrapper absorbs its own pointer/wheel events so popover interaction doesn't trigger the
       * canvas drag/zoom handlers underneath.
       */}
      <div
        className="absolute flex items-center gap-0.5"
        style={{ left: gutterWidth - (legendsVisible ? 66 : 44), top: 4 }}
        onPointerDown={(e) => e.stopPropagation()}
        onWheel={(e) => e.stopPropagation()}
      >
        <TimeAreaDisplayButton />
        <TimeAreaFilterButton
          activeSlots={slotInfo.activeSlots}
          activeSystems={activeSystems}
          threadNamesBySlot={threadNames}
          systemNamesByIdx={systemNames}
          threadInfos={threadInfos}
        />
        {legendsVisible && <TimeAreaHelpButton />}
      </div>
      {/* Tooltips are suppressed while the context menu is open — they portal to body at z-9999 and
          would otherwise paint over the menu. */}
      {!contextMenu && gaugeTooltipState && (
        <GaugeTooltip
          lines={buildGaugeTooltipLines(
            ticks,
            gaugeData,
            gaugeTooltipState.trackId,
            getGaugeGroupSpec(gaugeTooltipState.trackId)?.label ?? gaugeTooltipState.trackId,
            gaugeTooltipState.cursorUs,
            getStudioThemeTokens(),
            gaugeTooltipState.localY,
            gaugeTooltipState.trackHeight,
          )}
          clientX={gaugeTooltipState.clientX}
          clientY={gaugeTooltipState.clientY}
        />
      )}
      {!contextMenu && helpTooltipState && (
        <HelpOverlay
          lines={getTrackHelpLines(helpTooltipState.trackId, helpTooltipState.label)}
          clientX={helpTooltipState.clientX}
          clientY={helpTooltipState.clientY}
        />
      )}
      {!contextMenu && hoverTooltipState && (
        <HelpOverlay
          lines={hoverTooltipState.lines}
          clientX={hoverTooltipState.clientX}
          clientY={hoverTooltipState.clientY}
        />
      )}
      {contextMenu && contextMenu.kind === 'span' && (
        <TimeAreaContextMenu
          kind="span"
          x={contextMenu.x}
          y={contextMenu.y}
          spanName={contextMenu.span.name}
          spanId={contextMenu.span.spanId}
          callTreeAvailable={sessionKind === 'trace'}
          sourceAvailable={contextMenu.sourceLoc != null}
          onClose={() => setContextMenu(null)}
          onViewInCallTree={() => {
            handleViewInCallTreeMerged(contextMenu.span, contextMenu.sourceLoc);
            setContextMenu(null);
          }}
          onZoom={() => {
            animateToRange({ startUs: contextMenu.span.startUs, endUs: contextMenu.span.endUs });
            setContextMenu(null);
          }}
          onOpenSource={() => {
            const loc = contextMenu.sourceLoc;
            if (loc) void useOptionsStore.getState().openInEditor(loc.file, loc.line);
            setContextMenu(null);
          }}
        />
      )}
      {contextMenu && contextMenu.kind === 'chunk' && (
        <TimeAreaContextMenu
          kind="chunk"
          x={contextMenu.x}
          y={contextMenu.y}
          systemName={contextMenu.chunk.systemName || `System ${contextMenu.chunk.systemIndex}`}
          callTreeAvailable={sessionKind === 'trace'}
          sourceAvailable={contextMenu.sourceLoc != null}
          onClose={() => setContextMenu(null)}
          onViewInCallTree={() => {
            handleViewInCallTreeSystem(contextMenu.chunk, contextMenu.sourceLoc);
            setContextMenu(null);
          }}
          onZoom={() => {
            animateToRange({ startUs: contextMenu.chunk.startUs, endUs: contextMenu.chunk.endUs });
            setContextMenu(null);
          }}
          onShowSourceInline={() => {
            const loc = contextMenu.sourceLoc;
            if (loc) openSourcePreview(loc.file, loc.line);
            setContextMenu(null);
          }}
          onOpenSource={() => {
            const loc = contextMenu.sourceLoc;
            if (loc) void useOptionsStore.getState().openInEditor(loc.file, loc.line);
            setContextMenu(null);
          }}
        />
      )}
    </div>
  );
}

/**
 * Map a hit-test hover into a store mutation. Keeps the pointer handler readable.
 */
function routeSelection(
  hit: NonNullable<TimeAreaHover>,
  ticks: readonly TickData[],
): void {
  const selection = selectionFromHit(hit);
  if (selection === null) {
    // 'off-cpu' overlay bars are a hover-only visual (tooltip surfaces wait reason / duration); 'gutter-chevron'
    // is handled on pointerdown. Neither produces a selection.
    return;
  }
  // 3E — write the profiler selection straight to the unified bus leaf (tick → `tick`, everything else → `span`),
  // the same routing the retired `useProfilerSelectionStore` mirror used. The full selection rides as the leaf ref.
  useSelectionStore.getState().select(selection.kind === 'tick' ? 'tick' : 'span', selection);
  // AC3.2 (Stage 3 Phase 1) — project the owning System onto the bus as a *projection* (scalar slot, no recency
  // bump, so it never steals the Inspector leaf). This lets the Inspector resolve `System ⊃ Span` and, from
  // Phase 3, lets the scheduling cluster (DAG / Critical Path / Data Flow) highlight the same system. A span
  // carries only its thread slot; its system is the scheduler chunk occupying that slot at the span's instant
  // (infra spans — WAL / checkpoint / page-cache / GC — have no chunk → null clears the projection).
  useSelectionStore.getState().setSystem(resolveOwningSystem(selection, ticks.flatMap((t) => t.chunks)));
}

/** Map a hit-test result to a profiler selection, or null for hover-only / non-selecting hits. */
function selectionFromHit(hit: NonNullable<TimeAreaHover>): ProfilerSelection | null {
  switch (hit.kind) {
    case 'chunk':
      return { kind: 'chunk', chunk: hit.chunk };
    case 'span':
      return { kind: 'span', span: hit.span };
    case 'tick':
      return { kind: 'tick', tickNumber: hit.tickNumber };
    case 'phase':
      // Phase span (RuntimePhaseSpan, kind 243) — its own DetailPane branch so the user can read the phase's
      // SpanId and verify child spans (PageCacheFlush etc.) attach via parentSpanId.
      return { kind: 'phase', phase: hit.phase, tickNumber: hit.tickNumber };
    case 'phase-marker':
      // Glyph in the phase track (UoW Create / UoW Flush). Its own marker-detail branch.
      return { kind: 'phase-marker', marker: hit.marker, tickNumber: hit.tickNumber };
    case 'mini-row-op':
      // Mini-row ops ARE SpanData under the hood (stored in projection arrays) — treat as spans.
      return { kind: 'span', span: hit.op };
    default:
      return null;
  }
}

