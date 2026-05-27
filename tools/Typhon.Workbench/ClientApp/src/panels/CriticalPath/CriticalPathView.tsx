import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { colorForPhase, OCCUPANCY_FILL } from '@/libs/palettes';
import { pickTextColorFor, rgbCss } from '@/libs/color/contrast';
import { categoricalColor } from '@/libs/color/categorical';
import { useHoverStore } from '@/stores/useHoverStore';
import { computeWorkerOccupancy, type TickPathBar, type TickPathBars, type TickPathPostTick, type WorkerOccupancy } from './criticalPath';
import { packIntervals } from './intervalPacking';
import { useCriticalPathViewStore, type Orientation } from './useCriticalPathViewStore';
import {
  drawnUsAt,
  easeOutCubic,
  interpZoomWindow,
  pushViewHistory,
  stepViewHistory,
  viewParamsForWindow,
  visibleWindow,
  type DrawnWindow,
  type ViewHistory,
} from './criticalPathZoom';

interface Props {
  bars: TickPathBars;
  selectedSystemName: string | null;
  /** Click handler — selects the system and (single-tick mode) snaps the profiler viewport. */
  onSelectBar: (systemName: string, tickNumber: number) => void;
  /** Increment-signal — every change refits the timeline to the viewport. */
  fitSignal: number;
  /** Imperative fit trigger (middle-mouse). */
  onFit: () => void;
  /** Worker pool size — drives the worker-occupancy ribbon. Null hides it. */
  workerCount: number | null;
}

/**
 * Critical-path timeline — a time-accurate, multi-track Gantt of one tick (`09-system-dag.md §5`).
 *
 * **Layout.** The major axis is wall-clock time (tick-relative µs, linear). Bands stack on the
 * minor axis: time ruler → multi-lane phase band → worker-occupancy ribbon → pinned CP track →
 * interval-packed non-CP tracks (full-Gantt only). A metronome stripe leads, a post-tick serial
 * block trails.
 *
 * **Bars are placed at their measured `[startUs, endUs)`** — never by a duration-sum. The CP
 * track shows the dependency chain with hatched worker-claim-wait gaps; non-CP systems are
 * interval-packed into as many tracks as concurrency demands.
 *
 * **Zoom** is unbounded — `pxPerUs` in {@link useCriticalPathViewStore}; wheel zoom re-anchors so
 * the point under the cursor stays put.
 */

// Minor-axis band sizes (px).
const RULER_PX = 20;
const PHASE_LANE_PX = 13;
const RIBBON_PX = 15;
/** An occupancy cell prints its percentage inline only when the rect is large enough to hold the label un-clipped. */
const RIBBON_LABEL_MIN_W = 26;
const RIBBON_LABEL_MIN_H = 11;
const TRACK_PX = 30;
/** Height of the Tracks band (#354) — a single non-overlapping lane, shown only in the "All" scope. */
const TRACK_BAND_PX = 14;
/** Minor-axis inset of a bar inside its TRACK_PX lane — leaves room for the ≤2 px stroke (centred
 *  on the rect edge) so a bar never bleeds into the neighbouring lane or a section separator. */
const BAR_INSET_PX = 2;
const BAND_GAP_PX = 4;
const EDGE_PAD_PX = 6;
/** Width of the sticky left band-label gutter (horizontal orientation only). */
const GUTTER_PX = 92;
/** Pointer travel (px) before a press is treated as a drag-to-zoom rather than a click. */
const DRAG_THRESHOLD_PX = 3;
/** Duration of the zoom-to-selection tween — matches the profiler's TimeArea for an identical feel. */
const ZOOM_ANIMATION_MS = 800;
/** Cap on CP-local zoom/pan history entries — back/forward walks at most this many states. */
const CP_HISTORY_CAP = 50;

export default function CriticalPathView({ bars, selectedSystemName, onSelectBar, fitSignal, onFit, workerCount }: Props) {
  const rawOrientation = useCriticalPathViewStore((s) => s.orientation);
  const pxPerUs = useCriticalPathViewStore((s) => s.pxPerUs);
  const setPxPerUs = useCriticalPathViewStore((s) => s.setPxPerUs);
  const fullGantt = useCriticalPathViewStore((s) => s.fullGantt);
  const showMetronome = useCriticalPathViewStore((s) => s.showMetronome);
  const hoveredSystem = useHoverStore((s) => s.hoveredSystem);
  const setHoveredSystem = useHoverStore((s) => s.setHoveredSystem);
  const hoveredPhase = useHoverStore((s) => s.hoveredPhase);
  const setHoveredPhase = useHoverStore((s) => s.setHoveredPhase);

  const scrollRef = useRef<HTMLDivElement>(null);
  const [viewportSize, setViewportSize] = useState({ width: 0, height: 0 });
  const [stableViewportSize, setStableViewportSize] = useState({ width: 0, height: 0 });
  const [tooltip, setTooltip] = useState<TooltipState | null>(null);
  // Major-axis pan offset (px). The time axis is clipped to the viewport — TimeArea model — and
  // panned via this offset rather than a native scrollbar. `panMajorRef` mirrors the clamped value
  // so the native wheel handler reads it fresh without re-binding.
  const [panMajor, setPanMajor] = useState(0);
  const panMajorRef = useRef(0);

  // ── Drag-to-zoom gesture ────────────────────────────────────────────────
  // `dragRef` tracks an in-flight major-axis selection; `dragRect` mirrors it into render state so
  // the selection overlay paints. `suppressClickRef` swallows the synthetic click that trails a
  // moved drag so it doesn't also select a bar. `tweenRef` drives the 800 ms ease-out zoom tween —
  // same machinery as the profiler's TimeArea: it interpolates the drawn-µs window endpoints and
  // each rAF frame derives `(pxPerUs, panMajor)` from the lerped window.
  const dragRef = useRef<{ startMajor: number; currentMajor: number; moved: boolean } | null>(null);
  const [dragRect, setDragRect] = useState<{ a: number; b: number } | null>(null);
  const suppressClickRef = useRef(false);
  const tweenRef = useRef<{ from: { start: number; end: number }; to: { start: number; end: number }; startTime: number } | null>(null);
  const rafRef = useRef(0);

  // CP-local zoom/pan history — independent of the profiler's shared nav-history stack. Mouse
  // back/forward over the CP panel walks THIS stack (drag-to-zoom / wheel / fit states), restored
  // with the same 800 ms tween. Each entry is a drawn-µs window; `pointer` is the current position.
  // A fit resets the stack — its windows are tick-relative, hence stale once the displayed tick
  // changes.
  const historyRef = useRef<ViewHistory>({ entries: [], pointer: -1 });
  // Debounce timer — a wheel burst settles into a single history entry.
  const wheelSettleRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const orientation: Exclude<Orientation, 'auto'> = useMemo(
    () => resolveOrientation(rawOrientation, stableViewportSize.width, stableViewportSize.height),
    [rawOrientation, stableViewportSize.width, stableViewportSize.height],
  );
  const orientationRef = useRef(orientation);
  orientationRef.current = orientation;
  const viewportSizeRef = useRef(viewportSize);
  viewportSizeRef.current = viewportSize;

  // ── Timeline model ──────────────────────────────────────────────────────
  // Drawn timeline = [metronome lead] + [tick body 0..endUs] + [post-tick tail]. A tick-relative
  // time `t` maps to drawn-µs `leadUs + t`.
  const leadUs = showMetronome && bars.metronomeWaitUs > 0 ? bars.metronomeWaitUs : 0;
  const bodyEndUs = leadUs + bars.timeBounds.endUs;
  const postEndUs = bodyEndUs + bars.postTick.totalUs;
  const totalSpanUs = Math.max(1, postEndUs);

  const occupancy = useMemo<WorkerOccupancy | null>(
    () => (workerCount && workerCount >= 1 ? computeWorkerOccupancy(bars, workerCount) : null),
    [bars, workerCount],
  );

  // Non-CP track packing — interval-pack so overlapping systems land on separate lanes.
  const nonCpPacked = useMemo(
    () => packIntervals(bars.nonCpBars, (b) => b.startUs, (b) => b.endUs),
    [bars.nonCpBars],
  );
  // Phase band lanes — same packing over measured phase spans.
  const phasePacked = useMemo(
    () => packIntervals(bars.phaseSpans, (p) => p.startUs, (p) => p.endUs),
    [bars.phaseSpans],
  );

  // ── Minor-axis band offsets ─────────────────────────────────────────────
  // Band order: ruler → worker-occupancy ribbon → Tracks band → phase band → CP track → non-CP
  // tracks. The ribbon sits directly under the ruler so pool utilisation reads against the time
  // axis first; the Tracks band (only populated in the "All" scope) sits above the phases since a
  // track contains DAGs which contain phases.
  const phaseBandPx = phasePacked.laneCount * PHASE_LANE_PX;
  const ribbonPx = occupancy ? RIBBON_PX : 0;
  const tracksBandPx = bars.trackSpans.length > 0 ? TRACK_BAND_PX : 0;
  const nonCpTracksPx = fullGantt ? nonCpPacked.laneCount * TRACK_PX : 0;

  const rulerStart = 0;
  const ribbonStart = RULER_PX + BAND_GAP_PX;
  const tracksBandStart = ribbonStart + ribbonPx + (ribbonPx > 0 ? BAND_GAP_PX : 0);
  const phaseBandStart = tracksBandStart + tracksBandPx + (tracksBandPx > 0 ? BAND_GAP_PX : 0);
  const cpTrackStart = phaseBandStart + phaseBandPx + (phaseBandPx > 0 ? BAND_GAP_PX : 0);
  const nonCpStart = cpTrackStart + TRACK_PX + BAND_GAP_PX;
  const minorTotalPx = (fullGantt && nonCpTracksPx > 0 ? nonCpStart + nonCpTracksPx : cpTrackStart + TRACK_PX) + EDGE_PAD_PX;

  // Section / lane separators (minor-axis positions). One rule above the Critical Path track, one
  // above the Systems region, then one between every packed Systems lane. The ruler / worker /
  // phase bands above sit together as context and are deliberately left undivided.
  const separators: number[] = [cpTrackStart - BAND_GAP_PX / 2];
  if (fullGantt && nonCpTracksPx > 0) {
    separators.push(nonCpStart - BAND_GAP_PX / 2);
    for (let lane = 1; lane < nonCpPacked.laneCount; lane++) {
      separators.push(nonCpStart + lane * TRACK_PX);
    }
  }

  // Left band-label gutter — horizontal orientation only; the time content is shifted right by its width.
  const gutterPx = orientation === 'horizontal' ? GUTTER_PX : 0;
  const majorTotalPx = Math.max(1, totalSpanUs * pxPerUs);
  // Major-axis viewport extent. The content is clipped to this (no native scrollbar on the time
  // axis) and panned via `panMajor`; the SVG is sized to the viewport, never to the content.
  const majorViewport = Math.max(1, orientation === 'horizontal' ? viewportSize.width : viewportSize.height);
  // Largest valid pan — content end flush with the viewport edge; 0 when the content already fits.
  const maxPanMajor = Math.max(0, gutterPx + majorTotalPx - majorViewport);
  const panMajorClamped = Math.min(Math.max(0, panMajor), maxPanMajor);
  panMajorRef.current = panMajorClamped;
  // Major axis = viewport-sized (clipped); minor axis = full content (native scroll when taller).
  const svgW = orientation === 'horizontal' ? majorViewport : minorTotalPx;
  const svgH = orientation === 'horizontal' ? minorTotalPx : majorViewport;

  // ── Viewport tracking ───────────────────────────────────────────────────
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const obs = new ResizeObserver((entries) => {
      const r = entries[0]?.contentRect;
      if (r) setViewportSize({ width: r.width, height: r.height });
    });
    obs.observe(el);
    return () => obs.disconnect();
  }, []);

  useEffect(() => {
    const t = setTimeout(() => {
      setStableViewportSize({ width: viewportSize.width, height: viewportSize.height });
    }, 100);
    return () => clearTimeout(t);
  }, [viewportSize.width, viewportSize.height]);

  // Auto-fit on orientation flip / toggles — the major axis just changed length.
  const previousOrientationRef = useRef(orientation);
  useEffect(() => {
    if (previousOrientationRef.current === orientation) return;
    previousOrientationRef.current = orientation;
    if (useCriticalPathViewStore.getState().lockZoom) return;
    onFit();
  }, [orientation, onFit]);
  const previousFullGanttRef = useRef(fullGantt);
  useEffect(() => {
    if (previousFullGanttRef.current === fullGantt) return;
    previousFullGanttRef.current = fullGantt;
    if (useCriticalPathViewStore.getState().lockZoom) return;
    onFit();
  }, [fullGantt, onFit]);
  const previousShowMetronomeRef = useRef(showMetronome);
  useEffect(() => {
    if (previousShowMetronomeRef.current === showMetronome) return;
    previousShowMetronomeRef.current = showMetronome;
    if (useCriticalPathViewStore.getState().lockZoom) return;
    onFit();
  }, [showMetronome, onFit]);

  // ── Drag-to-zoom: 800 ms ease-out tween ─────────────────────────────────
  // Ref-stored step function (TimeArea pattern) so the rAF recursion never captures a stale
  // closure — every frame reads the freshest orientation / viewport off the refs.
  const stepTweenRef = useRef<() => void>(() => {});
  stepTweenRef.current = () => {
    const tw = tweenRef.current;
    if (!tw) return;
    const orient = orientationRef.current;
    const g = orient === 'horizontal' ? GUTTER_PX : 0;
    const vp = viewportSizeRef.current;
    const vpMajor = Math.max(1, orient === 'horizontal' ? vp.width : vp.height);
    const raw = (performance.now() - tw.startTime) / ZOOM_ANIMATION_MS;
    const w = interpZoomWindow(tw.from, tw.to, easeOutCubic(raw));
    const { pxPerUs: pp, panMajor: pm } = viewParamsForWindow(w.start, w.end, g, vpMajor);
    useCriticalPathViewStore.getState().setPxPerUs(pp);
    setPanMajor(pm);
    if (raw >= 1) {
      tweenRef.current = null;
    } else {
      rafRef.current = requestAnimationFrame(() => stepTweenRef.current());
    }
  };

  // Kick a tween from the current visible window to the dragged `[start, end]` drawn-µs window.
  const animateToWindow = useCallback((start: number, end: number): void => {
    const orient = orientationRef.current;
    const g = orient === 'horizontal' ? GUTTER_PX : 0;
    const vp = viewportSizeRef.current;
    const vpMajor = Math.max(1, orient === 'horizontal' ? vp.width : vp.height);
    const from = visibleWindow(panMajorRef.current, useCriticalPathViewStore.getState().pxPerUs, g, vpMajor);
    tweenRef.current = { from, to: { start, end }, startTime: performance.now() };
    cancelAnimationFrame(rafRef.current);
    rafRef.current = requestAnimationFrame(() => stepTweenRef.current());
  }, []);

  useEffect(() => () => {
    cancelAnimationFrame(rafRef.current);
    if (wheelSettleRef.current) clearTimeout(wheelSettleRef.current);
  }, []);

  // ── CP-local zoom/pan history ───────────────────────────────────────────
  // Append the current viewport to CP history (drops forward entries, caps length).
  const pushHistory = useCallback((w: DrawnWindow): void => {
    historyRef.current = pushViewHistory(historyRef.current, w, CP_HISTORY_CAP);
  }, []);

  // Walk CP history by `dir` (−1 back, +1 forward) and tween to that entry. No-op at either end.
  const navigateHistory = useCallback((dir: -1 | 1): void => {
    // A pending wheel-settle push would land on the wrong slot after we move the pointer — drop it.
    if (wheelSettleRef.current) { clearTimeout(wheelSettleRef.current); wheelSettleRef.current = null; }
    const stepped = stepViewHistory(historyRef.current, dir);
    if (stepped === historyRef.current) return; // at an end — nothing to navigate to
    historyRef.current = stepped;
    const w = stepped.entries[stepped.pointer];
    animateToWindow(w.start, w.end);
  }, [animateToWindow]);

  // ── Wheel: cursor-anchored zoom + clip-and-pan ──────────────────────────
  // The time axis is clipped to the viewport (no native scrollbar — TimeArea model). Plain wheel
  // zooms around the cursor; horizontal wheel / Shift+wheel pan the time axis; Ctrl/Cmd+wheel
  // scrolls the minor (cross) axis, which keeps its native scrollbar.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      // A wheel gesture overrides any in-flight zoom tween — drop it so they don't fight.
      tweenRef.current = null;
      cancelAnimationFrame(rafRef.current);
      const rect = el.getBoundingClientRect();
      const orient = orientationRef.current;
      const g = orient === 'horizontal' ? GUTTER_PX : 0;
      const vpMajor = Math.max(1, orient === 'horizontal' ? rect.width : rect.height);
      const cur = useCriticalPathViewStore.getState().pxPerUs;
      const clampPan = (p: number, px: number): number =>
        Math.min(Math.max(0, p), Math.max(0, g + totalSpanUs * px - vpMajor));

      if (e.ctrlKey || e.metaKey) {
        if (orient === 'horizontal') el.scrollTop += e.deltaY;
        else el.scrollLeft += e.deltaY;
        return;
      }
      // Any non-Ctrl wheel changes the major-axis viewport — debounce it into one CP history entry.
      if (wheelSettleRef.current) clearTimeout(wheelSettleRef.current);
      wheelSettleRef.current = setTimeout(() => {
        wheelSettleRef.current = null;
        const wOrient = orientationRef.current;
        const wg = wOrient === 'horizontal' ? GUTTER_PX : 0;
        const wvp = viewportSizeRef.current;
        const wMajor = Math.max(1, wOrient === 'horizontal' ? wvp.width : wvp.height);
        pushHistory(visibleWindow(panMajorRef.current, useCriticalPathViewStore.getState().pxPerUs, wg, wMajor));
      }, 250);
      if (e.deltaX !== 0 || e.shiftKey) {
        // Horizontal wheel, or Shift+wheel → pan the time axis.
        const delta = e.shiftKey ? e.deltaY : e.deltaX;
        setPanMajor(clampPan(panMajorRef.current + delta, cur));
        return;
      }
      // Plain wheel → zoom, anchored on the drawn-µs under the cursor. Position of a drawn-µs `u`
      // is `g + u·pxPerUs − panMajor`; solve for the `u` under the cursor, then re-pan so it stays.
      const cursorMajor = orient === 'horizontal' ? e.clientX - rect.left : e.clientY - rect.top;
      const anchorUs = (panMajorRef.current + cursorMajor - g) / cur;
      const next = Math.max(1e-6, cur * Math.exp(-e.deltaY * 0.0015));
      useCriticalPathViewStore.getState().setPxPerUs(next);
      setPanMajor(clampPan(g + anchorUs * next - cursorMajor, next));
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [totalSpanUs, pushHistory]);

  // Re-clamp the pan when a zoom-out or a viewport resize shrinks the pannable range.
  useEffect(() => {
    setPanMajor((p) => Math.min(Math.max(0, p), maxPanMajor));
  }, [maxPanMajor]);

  // ── Fit ─────────────────────────────────────────────────────────────────
  const fitInputsRef = useRef({ orientation, viewportSize, totalSpanUs });
  fitInputsRef.current = { orientation, viewportSize, totalSpanUs };
  // Seed to -1 (no real fitSignal is negative) so the *first* observed signal always counts as a
  // change — a freshly-mounted view on a new trace must fit, not skip because its initial ref
  // value happened to equal the incoming prop.
  const lastFitSignalRef = useRef(-1);
  // A fit can be requested before the ResizeObserver has reported a non-zero viewport (new-trace
  // mount). Latch the request here and let the viewport-size effect run finishes it once a real
  // size lands — otherwise the request is silently dropped and the view stays unfitted until the
  // user presses Fit.
  const pendingFitRef = useRef(false);
  useEffect(() => {
    // Detect FIRST fit (no prior signal observed) BEFORE we mutate the latch — used below to skip
    // the ease-out tween on initial mount. Animating from origin-zero to fitted reads as the
    // diagram "flying in" rather than motion; subsequent fits (middle-click, Fit button, orientation
    // flip, fullGantt / metronome toggle) have a real prior framing to ease from, so they tween
    // through the same 800 ms ease-out curve drag-to-zoom and history back/forward use.
    const isFirstFit = lastFitSignalRef.current < 0;
    if (fitSignal !== lastFitSignalRef.current) {
      lastFitSignalRef.current = fitSignal;
      pendingFitRef.current = true;
    }
    if (!pendingFitRef.current) return;
    const { orientation: o, viewportSize: v, totalSpanUs: total } = fitInputsRef.current;
    // Horizontal reserves GUTTER_PX for the band-label gutter — the time content fits in what's left.
    const major = o === 'horizontal' ? v.width - GUTTER_PX : v.height;
    if (major <= 0) return; // viewport not measured yet — keep the latch, retry on the next size change
    pendingFitRef.current = false;
    // A fit overrides any pending wheel-settle and resets CP history: the new framing becomes the
    // lone entry — the old windows are tick-relative, hence stale here. The history reset fires
    // unconditionally (before the tween) so back/forward acts on the new framing immediately.
    if (wheelSettleRef.current) { clearTimeout(wheelSettleRef.current); wheelSettleRef.current = null; }
    historyRef.current = { entries: [{ start: 0, end: total }], pointer: 0 };
    if (isFirstFit) {
      // First-fit snaps — no prior viewport to ease from. Match the SystemDag first-fit-instant rule.
      tweenRef.current = null;
      cancelAnimationFrame(rafRef.current);
      setPxPerUs(major / Math.max(1, total));
      setPanMajor(0);
    } else {
      // Subsequent fits tween to the full window via the existing 800 ms ease-out infrastructure.
      // `animateToWindow` itself cancels any in-flight rAF + replaces `tweenRef.current`.
      animateToWindow(0, total);
    }
  }, [fitSignal, viewportSize, setPxPerUs, animateToWindow]);

  // Middle-click = fit. Mouse thumb buttons = CP-local zoom-history back/forward.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const onDown = (e: MouseEvent) => {
      if (e.button === 1) {
        e.preventDefault();
        onFit();
        return;
      }
      // Mouse 3 = back (Shift = forward), Mouse 4 = forward — same mapping as the global mouse-nav
      // handler (`useKeyboardShortcuts`). `stopPropagation` keeps that global handler from ALSO
      // firing, so back/forward acts only on the panel the cursor is over.
      if (e.button === 3) {
        e.preventDefault();
        e.stopPropagation();
        navigateHistory(e.shiftKey ? 1 : -1);
      } else if (e.button === 4) {
        e.preventDefault();
        e.stopPropagation();
        navigateHistory(1);
      }
    };
    // Suppress the browser's own back/forward — Chrome / Edge navigate on mouseup / auxclick.
    const onAux = (e: MouseEvent) => {
      if (e.button === 1 || e.button === 3 || e.button === 4) e.preventDefault();
    };
    el.addEventListener('mousedown', onDown, { passive: false });
    el.addEventListener('auxclick', onAux, { passive: false });
    return () => {
      el.removeEventListener('mousedown', onDown);
      el.removeEventListener('auxclick', onAux);
    };
  }, [onFit, navigateHistory]);

  // ── Drag-to-zoom: pointer gesture ───────────────────────────────────────
  const onPointerDown = (e: React.PointerEvent<HTMLDivElement>): void => {
    if (e.button !== 0) return;
    const el = scrollRef.current;
    if (!el) return;
    // A fresh press overrides any in-flight tween and any pending wheel-settle history push.
    tweenRef.current = null;
    cancelAnimationFrame(rafRef.current);
    if (wheelSettleRef.current) { clearTimeout(wheelSettleRef.current); wheelSettleRef.current = null; }
    suppressClickRef.current = false;
    const rect = el.getBoundingClientRect();
    const orient = orientationRef.current;
    const g = orient === 'horizontal' ? GUTTER_PX : 0;
    const major = orient === 'horizontal' ? e.clientX - rect.left : e.clientY - rect.top;
    if (major < g) return; // started in the band-label gutter — not a time-axis selection
    dragRef.current = { startMajor: major, currentMajor: major, moved: false };
  };

  const onPointerMove = (e: React.PointerEvent<HTMLDivElement>): void => {
    const drag = dragRef.current;
    const el = scrollRef.current;
    if (!drag || !el) return;
    const rect = el.getBoundingClientRect();
    const orient = orientationRef.current;
    const g = orient === 'horizontal' ? GUTTER_PX : 0;
    const vpMajor = orient === 'horizontal' ? rect.width : rect.height;
    const raw = orient === 'horizontal' ? e.clientX - rect.left : e.clientY - rect.top;
    const major = Math.max(g, Math.min(vpMajor, raw));
    drag.currentMajor = major;
    if (!drag.moved && Math.abs(major - drag.startMajor) >= DRAG_THRESHOLD_PX) {
      drag.moved = true;
      // Capture only once the gesture is a real drag — a plain click never captures, so a bar's
      // native click still lands and selects.
      try { el.setPointerCapture(e.pointerId); } catch { /* noop */ }
    }
    if (drag.moved) {
      suppressClickRef.current = true;
      setDragRect({ a: drag.startMajor, b: major });
    }
  };

  const onPointerUp = (e: React.PointerEvent<HTMLDivElement>): void => {
    const drag = dragRef.current;
    dragRef.current = null;
    setDragRect(null);
    try { scrollRef.current?.releasePointerCapture(e.pointerId); } catch { /* noop */ }
    if (!drag || !drag.moved) return;
    const orient = orientationRef.current;
    const g = orient === 'horizontal' ? GUTTER_PX : 0;
    const pp = useCriticalPathViewStore.getState().pxPerUs;
    const pmc = panMajorRef.current;
    const m1 = Math.min(drag.startMajor, drag.currentMajor);
    const m2 = Math.max(drag.startMajor, drag.currentMajor);
    const d1 = drawnUsAt(m1, pmc, pp, g);
    const d2 = drawnUsAt(m2, pmc, pp, g);
    if (d2 - d1 > 1e-6) {
      pushHistory({ start: d1, end: d2 });
      animateToWindow(d1, d2);
    }
  };

  const onPointerCancel = (): void => {
    dragRef.current = null;
    setDragRect(null);
  };

  // Swallow the click that trails a moved drag-to-zoom so it doesn't also select a bar.
  const handleBarSelect = (systemName: string, tickNumber: number): void => {
    if (suppressClickRef.current) {
      suppressClickRef.current = false;
      return;
    }
    onSelectBar(systemName, tickNumber);
  };

  // ── Geometry helpers ────────────────────────────────────────────────────
  // A tick-relative time → drawn-µs (metronome lead included) → px on the major axis. The gutter
  // shifts every absolute position right; lengths taken as a difference of two majorPx cancel it.
  const majorPx = (tickRelUs: number): number => gutterPx + (leadUs + tickRelUs) * pxPerUs - panMajorClamped;
  // A drawn-µs value (already lead-relative, e.g. metronome / post-tick) → px. Pure span — no gutter
  // (it is used as a length; callers add `gutterPx` themselves when they need an absolute start).
  const drawnPx = (drawnUs: number): number => drawnUs * pxPerUs;
  // (major start, major length, minor start, minor length) → SVG rect.
  const rect = (mjStart: number, mjLen: number, mnStart: number, mnLen: number) =>
    orientation === 'horizontal'
      ? { x: mjStart, y: mnStart, width: Math.max(0, mjLen), height: mnLen }
      : { x: mnStart, y: mjStart, width: mnLen, height: Math.max(0, mjLen) };

  if (bars.cpChain.length === 0 && bars.nonCpBars.length === 0) {
    return (
      <div className="flex h-full w-full items-center justify-center bg-background text-fs-base text-muted-foreground">
        {bars.aggregate ? 'No critical-path data in range.' : `Tick ${bars.tickNumber} has no measured work.`}
      </div>
    );
  }

  const rulerTicks = buildRulerTicks(bars.timeBounds.endUs, pxPerUs);

  return (
    <div
      ref={scrollRef}
      className="relative h-full w-full overflow-auto bg-background"
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={onPointerUp}
      onPointerCancel={onPointerCancel}
      onMouseLeave={() => {
        setTooltip(null);
        setHoveredSystem(null);
        setHoveredPhase(null);
      }}
    >
      <svg width={svgW} height={svgH} style={{ display: 'block' }}>
        <defs>
          <pattern id="cp-hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <line x1="0" y1="0" x2="0" y2="6" stroke="var(--muted-foreground)" strokeOpacity="0.55" strokeWidth="2" />
          </pattern>
          <filter id="cp-shadow" x="-20%" y="-20%" width="140%" height="140%">
            <feDropShadow dx="0" dy="1" stdDeviation="1.5" floodColor="#000" floodOpacity="0.4" />
          </filter>
        </defs>

        {/* ── Time ruler ───────────────────────────────────────────────── */}
        {rulerTicks.map((tk, i) => {
          const r = rect(majorPx(tk), 1, rulerStart, RULER_PX);
          return (
            <g key={`tick-${i}`}>
              <rect x={r.x} y={r.y} width={r.width} height={r.height} fill="var(--border)" />
              <text
                x={orientation === 'horizontal' ? r.x + 3 : minorTotalPx - 4}
                y={orientation === 'horizontal' ? rulerStart + 13 : r.y + 11}
                fontSize={9}
                fontFamily="ui-monospace, monospace"
                textAnchor={orientation === 'horizontal' ? 'start' : 'end'}
                className="fill-muted-foreground"
              >
                {formatUs(tk)}
              </text>
            </g>
          );
        })}

        {/* ── Metronome lead stripe ────────────────────────────────────── */}
        {leadUs > 0 && (() => {
          const r = rect(gutterPx - panMajorClamped, drawnPx(leadUs), ribbonStart, minorTotalPx - ribbonStart - EDGE_PAD_PX);
          return (
            <g
              onMouseEnter={(e) => showTip(e, setTooltip, {
                kind: 'metronome',
                lines: [
                  `Metronome wait — ${formatUs(bars.metronomeWaitUs)}`,
                  bars.metronomeIntentClass ? `intent: ${bars.metronomeIntentClass}` : 'intent: unknown',
                  'Idle gap from the previous TickEnd to this TickStart.',
                ],
              })}
              onMouseLeave={() => setTooltip(null)}
            >
              <rect
                x={r.x}
                y={r.y}
                width={r.width}
                height={r.height}
                className="fill-[color-mix(in_oklch,var(--background),black_4%)] dark:fill-[color-mix(in_oklch,var(--background),white_5%)]"
              />
              <rect x={r.x} y={r.y} width={r.width} height={r.height} fill="url(#cp-hatch)" />
            </g>
          );
        })()}

        {/* ── Tracks band (#354) — one stripe per track; populated only in the "All" scope ── */}
        {bars.trackSpans.map((span, i) => {
          const r = rect(
            majorPx(span.startUs),
            majorPx(span.endUs) - majorPx(span.startUs),
            tracksBandStart,
            tracksBandPx - 1,
          );
          const colour = colorForPhase(span.index);
          return (
            <g
              key={`track-${i}`}
              onMouseEnter={(e) => showTip(e, setTooltip, {
                kind: 'track',
                lines: [
                  `Track ${span.name}`,
                  `span ${formatUs(span.startUs)} – ${formatUs(span.endUs)}`,
                  `wall ${formatUs(span.endUs - span.startUs)}`,
                ],
              })}
              onMouseLeave={() => setTooltip(null)}
            >
              <rect
                x={r.x}
                y={r.y}
                width={r.width}
                height={r.height}
                fill={colour.fill}
                stroke={colour.stroke}
                strokeWidth={0.75}
                rx={2}
              />
              {r.width >= 44 && orientation === 'horizontal' && (
                <text
                  x={r.x + 4}
                  y={r.y + tracksBandPx - 4}
                  fontSize={9}
                  fontFamily="ui-monospace, monospace"
                  fill={pickTextColorFor(colour.fill)}
                >
                  {clipText(span.name, r.width - 8)}
                </text>
              )}
            </g>
          );
        })}

        {/* ── Phase band (multi-lane) ──────────────────────────────────── */}
        {phasePacked.packed.map(({ item: span, lane }, i) => {
          const r = rect(
            majorPx(span.startUs),
            majorPx(span.endUs) - majorPx(span.startUs),
            phaseBandStart + lane * PHASE_LANE_PX,
            PHASE_LANE_PX - 1,
          );
          const colour = colorForPhase(span.phaseIndex);
          const isHovered = span.name === hoveredPhase;
          return (
            <g
              key={`phase-${i}`}
              onMouseEnter={(e) => {
                if (span.phaseIndex >= 0) setHoveredPhase(span.name);
                showTip(e, setTooltip, {
                  kind: 'phase',
                  lines: [`Phase ${span.name}`, `span ${formatUs(span.startUs)} – ${formatUs(span.endUs)}`, `wall ${formatUs(span.endUs - span.startUs)}`],
                });
              }}
              onMouseLeave={() => {
                setHoveredPhase(null);
                setTooltip(null);
              }}
            >
              <rect
                x={r.x}
                y={r.y}
                width={r.width}
                height={r.height}
                fill={colour.fill}
                stroke={colour.stroke}
                strokeWidth={isHovered ? 1.5 : 0.75}
                opacity={isHovered ? 1 : 0.85}
                rx={2}
              />
              {r.width >= 44 && orientation === 'horizontal' && (
                <text x={r.x + 4} y={r.y + PHASE_LANE_PX - 4} fontSize={9} fontFamily="ui-monospace, monospace" fill={pickTextColorFor(colour.fill)}>
                  {clipText(span.name, r.width - 8)}
                </text>
              )}
            </g>
          );
        })}

        {/* ── Worker-occupancy ribbon ──────────────────────────────────── */}
        {occupancy && (
          <OccupancyRibbon
            occupancy={occupancy}
            orientation={orientation}
            ribbonStart={ribbonStart}
            ribbonPx={RIBBON_PX}
            majorPx={majorPx}
            onTip={(e, lines) => showTip(e, setTooltip, { kind: 'ribbon', lines })}
            onLeave={() => setTooltip(null)}
          />
        )}

        {/* ── CP track ─────────────────────────────────────────────────── */}
        {bars.cpChain.map((bar, i) => (
          <BarShape
            key={`cp-${i}`}
            bar={bar}
            track="cp"
            majorPx={majorPx}
            trackStart={cpTrackStart}
            orientation={orientation}
            selected={bar.systemName === selectedSystemName}
            hovered={bar.systemName === hoveredSystem}
            onSelect={() => handleBarSelect(bar.systemName, bars.tickNumber)}
            onTip={(e, lines) => showTip(e, setTooltip, { kind: 'bar', lines })}
            onLeaveTip={() => setTooltip(null)}
            setHoveredSystem={setHoveredSystem}
          />
        ))}

        {/* ── Non-CP tracks (full-Gantt) ───────────────────────────────── */}
        {fullGantt && nonCpPacked.packed.map(({ item: bar, lane }, i) => (
          <BarShape
            key={`ncp-${i}`}
            bar={bar}
            track="noncp"
            majorPx={majorPx}
            trackStart={nonCpStart + lane * TRACK_PX}
            orientation={orientation}
            selected={bar.systemName === selectedSystemName}
            hovered={bar.systemName === hoveredSystem}
            onSelect={() => handleBarSelect(bar.systemName, bars.tickNumber)}
            onTip={(e, lines) => showTip(e, setTooltip, { kind: 'bar', lines })}
            onLeaveTip={() => setTooltip(null)}
            setHoveredSystem={setHoveredSystem}
          />
        ))}

        {/* ── Post-tick serial tail ────────────────────────────────────── */}
        {buildPostTickBlocks(bars.postTick).map((blk, i, arr) => {
          const offsetUs = bodyEndUs + arr.slice(0, i).reduce((s, b) => s + b.us, 0);
          const r = rect(gutterPx + drawnPx(offsetUs) - panMajorClamped, drawnPx(blk.us), cpTrackStart + BAR_INSET_PX, TRACK_PX - 2 * BAR_INSET_PX);
          return (
            <g
              key={`pt-${i}`}
              onMouseEnter={(e) => showTip(e, setTooltip, { kind: 'posttick', lines: [`${blk.label} — ${formatUs(blk.us)}`, 'Post-tick serial work.'] })}
              onMouseLeave={() => setTooltip(null)}
            >
              <rect x={r.x} y={r.y} width={r.width} height={r.height} fill={`hsla(${blk.hue},50%,30%,0.85)`} rx={2} filter="url(#cp-shadow)" />
              {r.width >= 30 && orientation === 'horizontal' && (
                <text x={r.x + r.width / 2} y={r.y + r.height / 2 + 3} fontSize={10} fontFamily="ui-monospace, monospace" textAnchor="middle" fill="#fff">
                  {clipText(blk.label, r.width - 6)}
                </text>
              )}
            </g>
          );
        })}

        {/* ── Section / lane separators ─────────────────────────────────── */}
        {separators.map((mn, i) =>
          orientation === 'horizontal' ? (
            <line key={`sep-${i}`} x1={0} y1={mn + 0.5} x2={svgW} y2={mn + 0.5} stroke="var(--border)" strokeWidth={1} />
          ) : (
            <line key={`sep-${i}`} x1={mn + 0.5} y1={0} x2={mn + 0.5} y2={svgH} stroke="var(--border)" strokeWidth={1} />
          ),
        )}

        {/* ── Band-label gutter (horizontal only) ──────────────────────────
            Drawn last so it sits above the panned content. The time axis pans underneath it via
            `panMajor`; the gutter itself never pans (fixed at x=0) and scrolls vertically with the
            bands as part of the SVG. The opaque fill masks content panned under it. */}
        {orientation === 'horizontal' && (
          <g>
            <rect x={0} y={0} width={gutterPx} height={minorTotalPx} fill="var(--card)" />
            <line x1={gutterPx - 0.5} y1={0} x2={gutterPx - 0.5} y2={minorTotalPx} stroke="var(--border)" strokeWidth={1} />
            {occupancy && <GutterLabel y={ribbonStart} h={RIBBON_PX} label="Workers" />}
            {tracksBandPx > 0 && <GutterLabel y={tracksBandStart} h={tracksBandPx} label="Tracks" />}
            {phaseBandPx > 0 && <GutterLabel y={phaseBandStart} h={phaseBandPx} label="Phases" />}
            <GutterLabel y={cpTrackStart} h={TRACK_PX} label="Critical Path" />
            {fullGantt && nonCpTracksPx > 0 && <GutterLabel y={nonCpStart} h={nonCpTracksPx} label="Systems" />}
          </g>
        )}

        {/* ── Drag-to-zoom selection overlay ───────────────────────────────
            Drawn last so it sits above every band. `dragRect` carries major-axis container px,
            which equals the SVG major coordinate (the SVG is viewport-sized on the major axis). */}
        {dragRect && (() => {
          const lo = Math.min(dragRect.a, dragRect.b);
          const r = rect(lo, Math.abs(dragRect.b - dragRect.a), 0, minorTotalPx);
          return (
            <rect
              x={r.x}
              y={r.y}
              width={r.width}
              height={r.height}
              fill="var(--primary)"
              fillOpacity={0.18}
              stroke="var(--primary)"
              strokeWidth={1}
              pointerEvents="none"
            />
          );
        })()}
      </svg>
      {tooltip && <Tooltip tooltip={tooltip} />}
      {bars.mode === 'execution-order' && <FallbackBadge />}
    </div>
  );
}

// ── Bar shape ───────────────────────────────────────────────────────────────

function BarShape({
  bar,
  track,
  majorPx,
  trackStart,
  orientation,
  selected,
  hovered,
  onSelect,
  onTip,
  onLeaveTip,
  setHoveredSystem,
}: {
  bar: TickPathBar;
  track: 'cp' | 'noncp';
  majorPx: (us: number) => number;
  trackStart: number;
  orientation: Exclude<Orientation, 'auto'>;
  selected: boolean;
  hovered: boolean;
  onSelect: () => void;
  onTip: (e: React.MouseEvent<SVGElement>, lines: string[]) => void;
  onLeaveTip: () => void;
  setHoveredSystem: (s: string | null) => void;
}) {
  // Phase colour — the same stroke+fill pair the phase band and the DAG swim-lanes use, so a bar
  // reads as "belongs to phase X" at a glance. Same-phase bars stay distinct by their name label.
  const colour = colorForPhase(bar.phaseIndex);
  // DS-2 stable hue-per-object: the system's shared categorical identity colour, painted as a thin leading-edge
  // rect over the phase fill so the SAME system reads the same colour here as in the timeline lane / DAG stripe /
  // Access-Matrix header / Query Analyzer. Non-destructive: the phase-grouping read is preserved by the bar's fill
  // + stroke; identity rides as a small left-edge accent (top edge in vertical orientation).
  const identityFill = rgbCss(categoricalColor(bar.systemName));
  const barStartPx = majorPx(bar.startUs);
  const barLenPx = majorPx(bar.endUs) - barStartPx;
  const claimPx = bar.workerClaimWaitUs > 0 ? majorPx(bar.startUs) - majorPx(bar.startUs - bar.workerClaimWaitUs) : 0;

  // Minor-axis extent of the bar inside its lane — inset so the stroke stays within TRACK_PX.
  const minorStart = trackStart + BAR_INSET_PX;
  const minorSize = TRACK_PX - 2 * BAR_INSET_PX;
  const r = orientation === 'horizontal'
    ? { x: barStartPx, y: minorStart, width: Math.max(0, barLenPx), height: minorSize }
    : { x: minorStart, y: barStartPx, width: minorSize, height: Math.max(0, barLenPx) };
  const claim = orientation === 'horizontal'
    ? { x: barStartPx - claimPx, y: minorStart, width: claimPx, height: minorSize }
    : { x: minorStart, y: barStartPx - claimPx, width: minorSize, height: claimPx };

  const tipLines = useMemo(() => {
    const lines = [`${bar.systemName} — ${formatUs(bar.durationUs)}`, `${formatUs(bar.startUs)} → ${formatUs(bar.endUs)}`];
    if (bar.workerClaimWaitUs > 0) lines.push(`worker-claim wait: ${formatUs(bar.workerClaimWaitUs)}`);
    if (bar.isParallel || bar.workersTouched > 1) {
      lines.push(`parallel: ${bar.workersTouched} workers / ${bar.chunksProcessed} chunks`);
      if (bar.totalCpuUs > 0 && bar.workersTouched > 0 && bar.durationUs > 0) {
        const eff = bar.totalCpuUs / (bar.workersTouched * bar.durationUs);
        lines.push(`efficiency: ${(eff * 100).toFixed(0)}%`);
      }
    }
    if (track === 'noncp') lines.push('Not on the critical path.');
    return lines;
  }, [bar, track]);

  return (
    <g>
      {/* Worker-claim-wait hatch — CP track only (`TickPathBar.workerClaimWaitUs` doc). On the CP
          track it fills the inter-bar gap [gating.endUs, startUs]; for a non-CP bar the hatch would
          extend back past whatever neighbour the interval-packer placed before it (packing keys on
          [startUs, endUs) and is blind to the hatch) and paint over it. So it is CP-track only. */}
      {track === 'cp' && claimPx > 0 && (
        <g
          onMouseEnter={(e) => {
            onTip(e, [`Worker-claim wait — ${formatUs(bar.workerClaimWaitUs)}`, `before: ${bar.systemName}`, 'Eligible, but no worker had picked it up yet.']);
            setHoveredSystem(bar.systemName);
          }}
          onMouseLeave={() => {
            onLeaveTip();
            setHoveredSystem(null);
          }}
          onClick={onSelect}
          style={{ cursor: 'pointer' }}
        >
          <rect
            x={claim.x}
            y={claim.y}
            width={claim.width}
            height={claim.height}
            className="fill-[color-mix(in_oklch,var(--background),black_4%)] dark:fill-[color-mix(in_oklch,var(--background),white_5%)]"
          />
          <rect x={claim.x} y={claim.y} width={claim.width} height={claim.height} fill="url(#cp-hatch)" />
        </g>
      )}
      <g
        onMouseEnter={(e) => {
          onTip(e, tipLines);
          setHoveredSystem(bar.systemName);
        }}
        onMouseLeave={() => {
          onLeaveTip();
          setHoveredSystem(null);
        }}
        onClick={onSelect}
        style={{ cursor: 'pointer' }}
      >
        <rect
          x={r.x}
          y={r.y}
          width={r.width}
          height={r.height}
          fill={colour.fill}
          stroke={selected ? 'var(--primary)' : hovered ? 'color-mix(in oklch, var(--foreground) 60%, transparent)' : colour.stroke}
          strokeWidth={selected || hovered ? 2 : 1}
          rx={2}
          filter="url(#cp-shadow)"
        />
        {/* System-identity leading-edge accent (DS-2). 4 px wide in horizontal (left edge), 4 px tall in vertical
            (top edge); clamped to the bar size so a sliver bar doesn't draw an oversized identity strip. */}
        <rect
          x={r.x}
          y={r.y}
          width={orientation === 'horizontal' ? Math.min(4, r.width) : r.width}
          height={orientation === 'horizontal' ? r.height : Math.min(4, r.height)}
          fill={identityFill}
          pointerEvents="none"
          data-testid={`cp-system-edge-${bar.systemName}`}
        />
        {r.width >= 30 && orientation === 'horizontal' && (
          <text
            x={r.x + r.width / 2}
            y={r.y + r.height / 2 + 3}
            fontSize={10}
            fontFamily="ui-monospace, monospace"
            textAnchor="middle"
            fill={pickTextColorFor(colour.fill)}
          >
            {clipText(bar.systemName, r.width - 6)}
          </text>
        )}
      </g>
    </g>
  );
}

// ── Band-label gutter ───────────────────────────────────────────────────────

/** One right-aligned band label in the sticky left gutter, vertically centred in its band. */
function GutterLabel({ y, h, label }: { y: number; h: number; label: string }) {
  return (
    <text
      x={GUTTER_PX - 8}
      y={y + h / 2 + 3}
      fontSize={9}
      fontFamily="ui-monospace, monospace"
      textAnchor="end"
      className="fill-muted-foreground"
    >
      {label}
    </text>
  );
}

// ── Worker-occupancy ribbon ─────────────────────────────────────────────────

function OccupancyRibbon({
  occupancy,
  orientation,
  ribbonStart,
  ribbonPx,
  majorPx,
  onTip,
  onLeave,
}: {
  occupancy: WorkerOccupancy;
  orientation: Exclude<Orientation, 'auto'>;
  ribbonStart: number;
  ribbonPx: number;
  majorPx: (us: number) => number;
  onTip: (e: React.MouseEvent<SVGElement>, lines: string[]) => void;
  onLeave: () => void;
}) {
  const { breakpoints, levels, workerCount } = occupancy;
  if (breakpoints.length < 2) return null;

  // Occupancy is encoded as fill *alpha*, not bar height — every cell spans the full ribbon
  // thickness and just gets more opaque as more of the worker pool is busy. Idle (0 busy) is
  // fully transparent, so the neutral surface shows through.
  const cells = levels.map((level, i) => {
    const mjStart = majorPx(breakpoints[i]);
    const mjEnd = majorPx(breakpoints[i + 1]);
    const r = orientation === 'horizontal'
      ? { x: mjStart, y: ribbonStart, width: Math.max(0, mjEnd - mjStart), height: ribbonPx }
      : { x: ribbonStart, y: mjStart, width: ribbonPx, height: Math.max(0, mjEnd - mjStart) };
    // Opacity = strength. Any occupancy at all jumps straight to a 0.3 floor (so even a sliver of
    // work is legible against the neutral surface) and ramps the remaining 0.7 with the busy
    // fraction; an idle segment stays fully transparent.
    const frac = Math.max(0, Math.min(1, level / workerCount));
    const opacity = level > 0 ? 0.3 + frac * 0.7 : 0;
    return { r, level, i, opacity };
  });

  return (
    <g>
      {/* Ribbon surface — a flat, calm neutral so idle (no occupancy) recedes instead of reading as a black void. */}
      <rect
        x={orientation === 'horizontal' ? majorPx(0) : ribbonStart}
        y={orientation === 'horizontal' ? ribbonStart : majorPx(0)}
        width={orientation === 'horizontal' ? majorPx(breakpoints[breakpoints.length - 1]) - majorPx(0) : ribbonPx}
        height={orientation === 'horizontal' ? ribbonPx : majorPx(breakpoints[breakpoints.length - 1]) - majorPx(0)}
        fill="var(--muted)"
        stroke="var(--border)"
        strokeWidth={1}
      />
      {cells.map(({ r, level, i, opacity }) => {
        const pct = Math.round((level / workerCount) * 100);
        // Print the percentage inside the cell when it is wide and tall enough for the label not to clip.
        // The label is always drawn horizontally, so a thin vertical ribbon (width = RIBBON_PX) skips it.
        const showLabel = r.width >= RIBBON_LABEL_MIN_W && r.height >= RIBBON_LABEL_MIN_H;
        return (
          <g key={i}>
            <rect
              x={r.x}
              y={r.y}
              width={r.width}
              height={r.height}
              fill={OCCUPANCY_FILL}
              fillOpacity={opacity}
              onMouseEnter={(e) => onTip(e, [`Worker occupancy — ${level.toFixed(1)} / ${workerCount}`, `${pct}% of the worker pool busy here.`])}
              onMouseLeave={onLeave}
            />
            {showLabel && (
              <text
                x={r.x + r.width / 2}
                y={r.y + r.height / 2}
                fontSize={10}
                fontWeight="bold"
                fontFamily="ui-monospace, monospace"
                textAnchor="middle"
                dominantBaseline="central"
                pointerEvents="none"
                fill="var(--foreground)"
              >
                {pct}%
              </text>
            )}
          </g>
        );
      })}
    </g>
  );
}

// ── Ruler ───────────────────────────────────────────────────────────────────

/** Nice-interval ruler ticks across `[0, endUs]` — step picked so spacing is ~90 px. */
function buildRulerTicks(endUs: number, pxPerUs: number): number[] {
  if (endUs <= 0 || pxPerUs <= 0) return [0];
  const targetPx = 90;
  const rawStep = targetPx / pxPerUs;
  const mag = Math.pow(10, Math.floor(Math.log10(rawStep)));
  const norm = rawStep / mag;
  const step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;
  const ticks: number[] = [];
  for (let t = 0; t <= endUs + step * 0.5 && ticks.length < 500; t += step) ticks.push(t);
  return ticks;
}

// ── Post-tick blocks ────────────────────────────────────────────────────────

const POST_TICK_ITEMS: Array<{ key: keyof TickPathPostTick; label: string; hue: number }> = [
  { key: 'writeTickFenceUs', label: 'WriteTickFence', hue: 200 },
  { key: 'walFlushUs', label: 'WAL flush', hue: 30 },
  { key: 'tierBudgetUs', label: 'TierBudget', hue: 280 },
  { key: 'subscriptionOutputUs', label: 'SubscriptionOutput', hue: 140 },
  { key: 'tierIndexRebuildUs', label: 'TierIndexRebuild', hue: 250 },
  { key: 'dormancySweepUs', label: 'DormancySweep', hue: 60 },
];

function buildPostTickBlocks(postTick: TickPathPostTick): Array<{ label: string; us: number; hue: number }> {
  const out: Array<{ label: string; us: number; hue: number }> = [];
  for (const item of POST_TICK_ITEMS) {
    const us = postTick[item.key];
    if (us > 0) out.push({ label: item.label, us, hue: item.hue });
  }
  return out;
}

// ── Tooltip ─────────────────────────────────────────────────────────────────

interface TooltipState {
  kind: 'bar' | 'phase' | 'ribbon' | 'posttick' | 'metronome' | 'track';
  lines: string[];
  x: number;
  y: number;
}

function showTip(
  e: React.MouseEvent<SVGElement>,
  setTooltip: (t: TooltipState | null) => void,
  partial: Omit<TooltipState, 'x' | 'y'>,
): void {
  const r = (e.currentTarget as SVGElement).getBoundingClientRect();
  setTooltip({ ...partial, x: r.left + r.width / 2, y: r.top + r.height / 2 });
}

function Tooltip({ tooltip }: { tooltip: TooltipState }) {
  const TOOLTIP_W = 250;
  const TOOLTIP_H = 14 * tooltip.lines.length + 12;
  let left = tooltip.x + 12;
  let top = tooltip.y + 12;
  if (left + TOOLTIP_W > window.innerWidth) left = tooltip.x - TOOLTIP_W - 12;
  if (top + TOOLTIP_H > window.innerHeight) top = tooltip.y - TOOLTIP_H - 12;
  return createPortal(
    <div
      className="pointer-events-none fixed z-[1000] rounded border border-border bg-card px-2 py-1.5 font-mono text-fs-xs text-foreground shadow-md"
      style={{ left, top, width: TOOLTIP_W }}
    >
      {tooltip.lines.map((line, i) => (
        <div key={i} className={i === 0 ? 'mb-1 font-semibold text-foreground' : 'text-muted-foreground'}>
          {line}
        </div>
      ))}
    </div>,
    document.body,
  );
}

function FallbackBadge() {
  return (
    <div
      className="pointer-events-none absolute right-2 top-2 z-40 rounded bg-amber-950/80 px-2 py-0.5 font-mono text-fs-2xs uppercase text-amber-300"
      title="The topology has no dependency edges — without a DAG the walker can't trace a critical path. Showing every system that ran, sorted by startUs."
    >
      execution order
    </div>
  );
}

// ── Utils ───────────────────────────────────────────────────────────────────

function resolveOrientation(raw: Orientation, width: number, height: number): Exclude<Orientation, 'auto'> {
  if (raw === 'horizontal') return 'horizontal';
  if (raw === 'vertical') return 'vertical';
  if (width <= 0 || height <= 0) return 'horizontal';
  return width >= height ? 'horizontal' : 'vertical';
}

function clipText(text: string, maxWidth: number): string {
  const maxChars = Math.max(0, Math.floor(maxWidth / 6));
  if (text.length <= maxChars) return text;
  if (maxChars <= 1) return '';
  return text.slice(0, maxChars - 1) + '…';
}

function formatUs(us: number): string {
  if (us < 1) return '0µs';
  if (us < 1000) return `${Math.round(us)}µs`;
  const ms = us / 1000;
  return ms < 10 ? `${ms.toFixed(2)}ms` : `${ms.toFixed(1)}ms`;
}
