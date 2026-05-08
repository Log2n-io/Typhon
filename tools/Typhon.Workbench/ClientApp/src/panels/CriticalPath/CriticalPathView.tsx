import { useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { pickTextColorFor } from '@/libs/colors';
import { TIMELINE_PALETTE } from '@/libs/profiler/canvas/canvasUtils';
import { useHoverStore } from '@/stores/useHoverStore';
import { computePhaseUtilization, type PhaseUtilization } from '../SystemDag/tickUtilization';
import type { MetronomeIntentClass, TickPathBar, TickPathBars, TickPathPhase, TickPathPostTick } from './criticalPath';
import { colorForPhase } from './phaseColors';
import { useCriticalPathViewStore, type CpScale, type Orientation } from './useCriticalPathViewStore';

interface Props {
  bars: TickPathBars;
  selectedSystemName: string | null;
  /** Click handler — wires to the panel which sets useSelectionStore.system + focusTick. */
  onSelectBar: (systemName: string, tickNumber: number) => void;
  /**
   * Increment-signal: every time this changes, the view recomputes `pxPerUs` so the timeline fits
   * exactly inside the current viewport on the major axis. Driven from the toolbar's "Fit"
   * button and the `0` keybind in the panel.
   */
  fitSignal: number;
  /**
   * Imperative fit trigger — used by the middle-mouse-button shortcut. Equivalent to bumping
   * `fitSignal` from outside; we keep both so the toolbar / keybind / button all funnel through
   * the same panel-level counter while the view's local mouse handler can call this directly
   * without round-tripping through panel state.
   */
  onFit: () => void;
  /**
   * Worker pool size. Drives the A2 per-phase parallelism band — capacity = workerCount × phase
   * wall-clock; work = Σ system durations in the phase (CP + non-CP). Null hides the band (single-
   * worker traces, missing header, aggregate mode where waits aren't carried per phase).
   */
  workerCount: number | null;
}

/**
 * Critical-path zoomable view — replaces the old fixed-width tape with a true timeline canvas.
 *
 * **Layout model.** Every "thing that took time" in a tick — system bars, the three wait classes,
 * post-tick serial sub-blocks, and the metronome wait — is flattened into a single linear list
 * of {@link Segment}s drawn in execution order along the major axis. The minor axis carries the
 * phase stripe + phase labels in a band above (horizontal) or beside (vertical) the bars.
 *
 * **Zoom.** Truly unbounded. `pxPerUs` lives in {@link useCriticalPathViewStore}; the major-axis
 * length is `totalUs × pxPerUs`. Wheel zoom multiplies the factor and re-anchors the scroll so
 * the segment under the cursor stays put. Bar pixel sizes are NOT clamped — text is dropped when
 * a bar is too narrow to fit it.
 *
 * **Scale.** Linear is `us × pxPerUs`. Log redistributes proportionally so total pixel length is
 * preserved (so zoom feels consistent across modes). `segmentFraction = log10(us+1) / Σlog10(us+1)`.
 *
 * **Phase colour.** A 5 px stripe in the phase-header band, spanning each phase's stretch of the
 * timeline. The phase name sits inside that band when there's room.
 */
export default function CriticalPathView({ bars, selectedSystemName, onSelectBar, fitSignal, onFit, workerCount }: Props) {
  const rawOrientation = useCriticalPathViewStore((s) => s.orientation);
  const scale = useCriticalPathViewStore((s) => s.scale);
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
  // `stableViewportSize` lags `viewportSize` by `ORIENTATION_DEBOUNCE_MS` and drives the
  // `auto` orientation calculation. Without the debounce, a viewport near a 1:1 ratio can
  // oscillate during a dock-drag: orientation flips → scrollbar moves to the other axis →
  // contentRect changes → flips back. The debounce lets the resize settle (100 ms is below
  // human flicker perception while comfortably outside the per-frame ResizeObserver cadence).
  // `viewportSize` itself stays immediate so fit / scroll maths use the freshest dimensions.
  const [stableViewportSize, setStableViewportSize] = useState({ width: 0, height: 0 });
  const [tooltip, setTooltip] = useState<TooltipState | null>(null);

  // Effective orientation — "auto" resolves to horizontal/vertical based on the viewport's
  // current aspect ratio. Re-evaluates on every *stable* resize so a dock-drag from a wide
  // pane into a narrow tab swaps the layout automatically once the user stops dragging.
  const orientation: Exclude<Orientation, 'auto'> = useMemo(
    () => resolveOrientation(rawOrientation, stableViewportSize.width, stableViewportSize.height),
    [rawOrientation, stableViewportSize.width, stableViewportSize.height],
  );

  // Live orientation ref — the wheel handler runs outside the React render cycle and needs the
  // current effective orientation without re-attaching the listener on every change. Updated
  // synchronously on each render so reads from event handlers are always current.
  const orientationRef = useRef(orientation);
  orientationRef.current = orientation;

  // Auto-fit on orientation flip — `auto` mode swaps horizontal/vertical when the viewport
  // ratio crosses 1:1. The major axis just changed length by a large factor, so the user's
  // current `pxPerUs` is the wrong scale. Refit to make the work fill the new layout. Skipped
  // when `lockZoom` is on so power users can compare the same scale across orientation flips.
  // Skipped on the first render (the initial fit chain in the panel handles that path).
  const previousOrientationRef = useRef(orientation);
  useEffect(() => {
    if (previousOrientationRef.current === orientation) return;
    previousOrientationRef.current = orientation;
    if (useCriticalPathViewStore.getState().lockZoom) return;
    onFit();
  }, [orientation, onFit]);

  // Auto-fit when full-Gantt toggles — the timeline either grows (non-CP bars added) or shrinks
  // (CP-only) by a large factor, so the previous `pxPerUs` is the wrong scale. `lockZoom` opts
  // power users out so they can compare CP-only vs full views at the same scale.
  const previousFullGanttRef = useRef(fullGantt);
  useEffect(() => {
    if (previousFullGanttRef.current === fullGantt) return;
    previousFullGanttRef.current = fullGantt;
    if (useCriticalPathViewStore.getState().lockZoom) return;
    onFit();
  }, [fullGantt, onFit]);

  // Same auto-fit dance for the metronome toggle — turning it on adds a leading stripe (often
  // larger than the work itself on idle ticks); turning it off shrinks the timeline.
  const previousShowMetronomeRef = useRef(showMetronome);
  useEffect(() => {
    if (previousShowMetronomeRef.current === showMetronome) return;
    previousShowMetronomeRef.current = showMetronome;
    if (useCriticalPathViewStore.getState().lockZoom) return;
    onFit();
  }, [showMetronome, onFit]);

  // Track viewport size so "Fit" / scroll calculations have current numbers. ResizeObserver fires
  // on dock-resize, panel float, etc.
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

  // Debounce stable viewport size — drives the `auto` orientation re-evaluation. 100 ms is the
  // sweet spot: long enough to absorb scrollbar-swap oscillations near a 1:1 viewport ratio,
  // short enough that the layout swap feels responsive after the user stops drag-resizing.
  useEffect(() => {
    const ORIENTATION_DEBOUNCE_MS = 100;
    const t = setTimeout(() => {
      setStableViewportSize({ width: viewportSize.width, height: viewportSize.height });
    }, ORIENTATION_DEBOUNCE_MS);
    return () => clearTimeout(t);
  }, [viewportSize.width, viewportSize.height]);

  // Build the flat segment list — `fullGantt` decides whether non-CP bars are appended into each
  // phase. Cheap (linear in segments). `effectiveTotalUs` is the SUM of segment durations rather
  // than `bars.totalUs`, because in full-Gantt mode non-CP additions inflate the timeline above
  // the strict-wall-clock CP total. We use the effective value everywhere placement / zoom /
  // fit-anchor calculations would otherwise round-trip through `bars.totalUs`.
  const segments = useMemo(() => buildSegments(bars, fullGantt, showMetronome), [bars, fullGantt, showMetronome]);
  const effectiveTotalUs = useMemo(() => {
    let sum = 0;
    for (const seg of segments) sum += seg.durationUs;
    return Math.max(1, sum);
  }, [segments]);

  // Major-axis logical-fraction table — drives both linear and log placement uniformly.
  const placement = useMemo(() => placeSegments(segments, scale, effectiveTotalUs), [segments, scale, effectiveTotalUs]);

  const majorTotalPx = Math.max(1, effectiveTotalUs * pxPerUs);

  // Pending zoom-anchor: written by the wheel handler, applied by the layout effect *after*
  // React commits the new SVG width. Using rAF here is unreliable — React 18's automatic
  // batching can flush the commit AFTER the rAF fires, in which case the browser clamps
  // scrollLeft to the still-old maxScroll and the anchor drifts.
  const pendingAnchor = useRef<{ fraction: number; cursorMajor: number; orientation: Orientation } | null>(null);

  // Wheel handler — plain = zoom (cursor-anchored), Shift = scroll major (5×), Ctrl = scroll
  // minor. preventDefault on Ctrl+wheel suppresses the browser's page-zoom default.
  // Reads pxPerUs / totalUs straight from the store to dodge stale-closure bugs across rapid
  // wheel events (the listener attaches once on mount, doesn't re-attach per render).
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const rect = el.getBoundingClientRect();
      // Read the *committed* effective orientation from the live ref instead of resolving on
      // the wheel event itself. Resolving here would skip the debounce — a wheel event during
      // a near-1:1 oscillation could pick a different orientation from what the SVG just
      // rendered with, miscomputing the cursor anchor.
      const orient = orientationRef.current;
      // Horizontal wheel input (tilt-wheel or trackpad swipe) — scroll horizontally regardless
      // of orientation: this matches what every other scroll container in the UI does. We let
      // any non-zero deltaX through and skip the zoom branch entirely so a horizontal swipe
      // never zooms by accident.
      if (e.deltaX !== 0) {
        el.scrollLeft += e.deltaX;
        return;
      }
      if (e.shiftKey) {
        const delta = e.deltaY * 5;
        if (orient === 'horizontal') el.scrollLeft += delta;
        else el.scrollTop += delta;
        return;
      }
      if (e.ctrlKey || e.metaKey) {
        if (orient === 'horizontal') el.scrollTop += e.deltaY;
        else el.scrollLeft += e.deltaY;
        return;
      }
      // Cursor-anchored zoom. wheel-up → zoom in.
      const cur = useCriticalPathViewStore.getState().pxPerUs;
      const factor = Math.exp(-e.deltaY * 0.0015);
      const cursorMajor = orient === 'horizontal' ? e.clientX - rect.left : e.clientY - rect.top;
      const scrollMajor = orient === 'horizontal' ? el.scrollLeft : el.scrollTop;
      const oldMajor = Math.max(1, effectiveTotalUs * cur);
      const fraction = (scrollMajor + cursorMajor) / oldMajor;
      pendingAnchor.current = { fraction, cursorMajor, orientation: orient };
      useCriticalPathViewStore.getState().setPxPerUs(cur * factor);
    };
    el.addEventListener('wheel', onWheel, { passive: false });
    return () => el.removeEventListener('wheel', onWheel);
  }, [effectiveTotalUs]);

  // Apply the pending anchor right after React commits the SVG resize. useLayoutEffect runs
  // synchronously after the DOM update and before paint — by this point the SVG has its new
  // width/height so scrollLeft/scrollTop can land at the correct value without browser clamping.
  useLayoutEffect(() => {
    const a = pendingAnchor.current;
    if (!a) return;
    const el = scrollRef.current;
    if (!el) return;
    const newMajor = Math.max(1, effectiveTotalUs * pxPerUs);
    const newScroll = a.fraction * newMajor - a.cursorMajor;
    if (a.orientation === 'horizontal') el.scrollLeft = newScroll;
    else el.scrollTop = newScroll;
    pendingAnchor.current = null;
  }, [pxPerUs, effectiveTotalUs]);

  // Fit-to-viewport: size the canvas so the visible timeline fills the major axis. When the
  // metronome toggle is OFF the leading stripe isn't drawn (it's not in `segments`), so the
  // budget naturally excludes it via `effectiveTotalUs`. When the toggle is ON the stripe IS
  // drawn — and the user enabled it precisely because they want to see it — so include it in the
  // fit budget and start scroll at zero. (Earlier behaviour subtracted it and scrolled past it,
  // which immediately hid what the toggle was meant to surface.)
  //
  // **Trigger discipline**: only `fitSignal` may dispatch a fit. Viewport / bars are read via
  // refs so their *changes* don't re-trigger the effect. Without this, zoom-in causes the
  // scrollbar to appear, which shrinks the viewport, fires ResizeObserver, runs the fit effect
  // again, and dispatches `setPxPerUs(viewport / fitUs)` — reverting the user's wheel zoom in
  // a visible flicker.
  const fitInputsRef = useRef({ orientation, viewportSize, bars, effectiveTotalUs, showMetronome });
  fitInputsRef.current = { orientation, viewportSize, bars, effectiveTotalUs, showMetronome };
  const lastFitSignalRef = useRef(fitSignal);
  const pendingFitScroll = useRef<number | null>(null);
  useEffect(() => {
    if (fitSignal === lastFitSignalRef.current) return;
    lastFitSignalRef.current = fitSignal;
    const { orientation: o, viewportSize: v, effectiveTotalUs: total } = fitInputsRef.current;
    const major = o === 'horizontal' ? v.width : v.height;
    // Fit budget = the full rendered timeline. `effectiveTotalUs` already accounts for the
    // metronome stripe — present when the toggle is on, absent when off — so we pack everything
    // visible into the major axis. Scroll position 0 lines up with the leading edge (metronome
    // when shown, first tick segment otherwise).
    const fitUs = Math.max(1, total);
    if (major <= 0) return;
    const newPxPerUs = major / fitUs;
    setPxPerUs(newPxPerUs);
    pendingAnchor.current = null; // wheel anchor would conflict with fit; clear it.
    pendingFitScroll.current = 0;
  }, [fitSignal, setPxPerUs]);

  // Apply the pending fit scroll right after the SVG resize commits.
  useLayoutEffect(() => {
    const offset = pendingFitScroll.current;
    if (offset == null) return;
    const el = scrollRef.current;
    if (!el) return;
    if (orientation === 'horizontal') {
      el.scrollLeft = offset;
      el.scrollTop = 0;
    } else {
      el.scrollTop = offset;
      el.scrollLeft = 0;
    }
    pendingFitScroll.current = null;
  }, [pxPerUs, orientation]);

  // Phase stripe segments — coalesce consecutive segments belonging to the same phase so the
  // header band shows one stripe per phase rather than many tiny ones.
  const phaseStripes = useMemo(() => buildPhaseStripes(segments, placement, bars.totalUs), [segments, placement, bars.totalUs]);

  // Per-phase parallelism band (A2) — capacity = workerCount × phase wallTime; work = Σ
  // `totalCpuUs` across CP and non-CP bars in the phase (chunker v13+; older caches surface 0
  // and the band stays empty). `durationUs` would under-count parallel work the same way it
  // did at the toolbar level — see `tickUtilization.ts` for the rationale. Suppressed in
  // aggregate mode (the bars there carry mean wall-clock only, not cpu time). Map keyed by
  // phaseIndex matches what `PhaseStripe` already has — synthetic stripes (post-tick /
  // metronome, indices < 0) intentionally absent.
  const phaseUtilizations = useMemo<Map<number, PhaseUtilization>>(() => {
    const map = new Map<number, PhaseUtilization>();
    if (!workerCount || workerCount < 2) return map;
    if (bars.aggregate) return map;
    bars.phases.forEach((phase, phaseIndex) => {
      let work = 0;
      for (const b of phase.bars) work += b.totalCpuUs;
      for (const b of phase.nonCpBars) work += b.totalCpuUs;
      if (work <= 0) return; // pre-v13 cache or empty phase — no band
      const u = computePhaseUtilization({ workerCount, phaseWorkUs: work, phaseWallTimeUs: phase.totalUs });
      if (u) map.set(phaseIndex, u);
    });
    return map;
  }, [bars, workerCount]);

  // Header band budget — color stripe (5 px) + 2 px gap + 3 px parallelism bar + 2 px breathing
  // room above the system bars + label space above the stripe. Was 18 before A2; now 22 to host
  // the parallelism bar without crowding the phase name. Synthetic phases (post-tick / metronome,
  // phaseIndex < 0) skip the parallelism bar so the layout stays consistent — they have no
  // worker-vs-wallclock concept attached.
  const HEADER_BAND_PX = 22;
  const BAR_HEIGHT_PX = 64;
  // Minor-axis padding past the bars so the drop-shadow doesn't clip at the SVG edge. Without
  // this, dy=2 on the shadow (horizontal layout) renders into clipped space and looks like
  // "no shadow". 6 px ≥ shadow dy + stdDeviation; same value applies symmetrically in vertical.
  const SHADOW_PAD_PX = 6;
  const minorTotalPx = HEADER_BAND_PX + BAR_HEIGHT_PX + SHADOW_PAD_PX;

  // SVG dimensions. Major = zoomed length; minor = fixed.
  const svgWidth = orientation === 'horizontal' ? majorTotalPx : minorTotalPx;
  const svgHeight = orientation === 'horizontal' ? minorTotalPx : majorTotalPx;

  // Middle-mouse-button = fit. Bound natively (not via React's synthetic event) with
  // `{ passive: false }` so `preventDefault()` reliably suppresses Chrome / Edge's middle-click
  // autoscroll marker on `overflow: auto` containers — React's synthetic mousedown isn't always
  // dispatched in time to stop the browser default. Also block `auxclick` for the same reason
  // (some browsers trigger autoscroll on the up edge if mousedown's preventDefault didn't catch
  // it).
  // Declared BEFORE the early-return below — Rules of Hooks demand the same hook order on every
  // render, and the empty-tick branch returns a different element so the hook can't appear after.
  useEffect(() => {
    const el = scrollRef.current;
    if (!el) return;
    const onDown = (e: MouseEvent) => {
      if (e.button !== 1) return;
      e.preventDefault();
      onFit();
    };
    const onAux = (e: MouseEvent) => {
      if (e.button === 1) e.preventDefault();
    };
    el.addEventListener('mousedown', onDown, { passive: false });
    el.addEventListener('auxclick', onAux, { passive: false });
    return () => {
      el.removeEventListener('mousedown', onDown);
      el.removeEventListener('auxclick', onAux);
    };
  }, [onFit]);

  if (bars.totalUs === 0) {
    return (
      <div className="flex h-full w-full items-center justify-center bg-background text-[12px] text-muted-foreground">
        Tick {bars.tickNumber} has no measured work.
      </div>
    );
  }

  return (
    <div
      ref={scrollRef}
      className="relative h-full w-full overflow-auto bg-background"
      onMouseLeave={() => {
        setTooltip(null);
        setHoveredSystem(null);
      }}
    >
      <svg
        width={svgWidth}
        height={svgHeight}
        style={{ display: 'block' }}
      >
        {/*
          Single hatch <pattern> at the SVG root — re-used by every wait/metronome rect via
          fill="url(#cp-hatch)". Defining it inside per-segment groups (the previous shape) put
          a <defs> inside every <g>, which broke event bubble / bbox calculations and is also
          wasteful (one pattern per segment vs one global).
        */}
        <defs>
          <pattern id="cp-hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
            <line x1="0" y1="0" x2="0" y2="6" stroke="rgba(148,163,184,0.4)" strokeWidth="2" />
          </pattern>
          {/*
            Drop-shadow filters applied to "active work" boxes (system + post-tick) — not the
            hatched wait segments, which stay visually flat to read as "absence of work". Two
            variants so the shadow extends along the *minor* axis in each layout (i.e. away from
            the next neighbour bar in the stack): down in horizontal, right in vertical.
          */}
          <filter id="cp-shadow-h" x="-20%" y="-20%" width="140%" height="140%">
            <feDropShadow dx="0" dy="2" stdDeviation="2" floodColor="#000" floodOpacity="0.45" />
          </filter>
          <filter id="cp-shadow-v" x="-20%" y="-20%" width="140%" height="140%">
            <feDropShadow dx="2" dy="0" stdDeviation="2" floodColor="#000" floodOpacity="0.45" />
          </filter>
        </defs>
        {/* Phase-header band — 5 px stripe + 3 px parallelism bar + name per phase stretch. */}
        {phaseStripes.map((stripe, i) => (
          <PhaseStripe
            key={i}
            stripe={stripe}
            orientation={orientation}
            majorTotalPx={majorTotalPx}
            headerBandPx={HEADER_BAND_PX}
            utilization={phaseUtilizations.get(stripe.phaseIndex) ?? null}
            isHovered={stripe.phaseIndex >= 0 && stripe.phaseName === hoveredPhase}
            onMouseEnter={() => {
              // Only sync real phases (post-tick / metronome have synthetic indices < 0). Otherwise
              // hovering the post-tick stripe would broadcast a phase the DAG canvas can't match.
              if (stripe.phaseIndex >= 0) setHoveredPhase(stripe.phaseName);
            }}
            onMouseLeave={() => {
              if (stripe.phaseIndex >= 0) setHoveredPhase(null);
            }}
          />
        ))}
        {/* Bars row. */}
        {segments.map((seg, i) => {
          const place = placement[i];
          const startPx = place.startFraction * majorTotalPx;
          const lengthPx = place.lengthFraction * majorTotalPx;
          return (
            <SegmentShape
              key={i}
              seg={seg}
              startPx={startPx}
              lengthPx={lengthPx}
              orientation={orientation}
              barOffsetPx={HEADER_BAND_PX}
              barHeightPx={BAR_HEIGHT_PX}
              isSelected={(seg.kind === 'system' || seg.kind === 'non-cp-system') && seg.bar.systemName === selectedSystemName}
              isHovered={(seg.kind === 'system' || seg.kind === 'non-cp-system') && seg.bar.systemName === hoveredSystem}
              onMouseEnter={(e) => {
                const target = e.currentTarget as SVGElement;
                const rect = target.getBoundingClientRect();
                // Viewport coordinates — tooltip is portaled to document.body with
                // position:fixed, so we don't need to compensate for container scroll or
                // dockview ancestor transforms. Centre on the bar's bbox so the tooltip
                // appears next to the segment, not the cursor (cursor-following would require
                // mousemove tracking which we don't need for this v1).
                setTooltip({
                  segment: seg,
                  x: rect.left + rect.width / 2,
                  y: rect.top + rect.height / 2,
                });
                // Cross-panel hover sync: writing here makes the matching DAG node pulse and
                // any future panels that subscribe to hover light up too. Phase-fence / wait
                // segments leave the hover slot alone — there's no "system" to point at.
                if (seg.kind === 'system' || seg.kind === 'non-cp-system') {
                  setHoveredSystem(seg.bar.systemName);
                } else if (seg.kind === 'worker-claim') {
                  // Hover the wait segment → highlight the system that was waiting. Symmetric
                  // with the click-through; lets the user trace "this gap is in front of THAT
                  // node" without having to click first.
                  setHoveredSystem(seg.systemName);
                }
              }}
              onMouseLeave={() => {
                setTooltip(null);
                if (seg.kind === 'system' || seg.kind === 'non-cp-system' || seg.kind === 'worker-claim') {
                  setHoveredSystem(null);
                }
              }}
              onClick={() => {
                if (seg.kind === 'system' || seg.kind === 'non-cp-system') {
                  onSelectBar(seg.bar.systemName, bars.tickNumber);
                } else if (seg.kind === 'phase-fence' && seg.straggler != null) {
                  // Click-through to the straggler — same behaviour as a direct system-bar click,
                  // so cross-panel selection sync (DAG node highlight, side-panel access) updates
                  // automatically.
                  onSelectBar(seg.straggler, bars.tickNumber);
                } else if (seg.kind === 'worker-claim') {
                  // Click-through to the system that was waiting on a worker — the natural pivot
                  // for "why couldn't a worker pick this up?". Selects the system + opens its
                  // side-panel (access decls, parallelism mode, prior chunks).
                  onSelectBar(seg.systemName, bars.tickNumber);
                }
              }}
            />
          );
        })}
      </svg>
      {tooltip && <Tooltip tooltip={tooltip} />}
      {bars.mode === 'execution-order' && <FallbackBadge />}
    </div>
  );
}

// ── Segment model — every drawable thing along the major axis ─────────────

type Segment =
  | { kind: 'system';            bar: TickPathBar;                  phaseName: string; phaseIndex: number; durationUs: number }
  | { kind: 'non-cp-system';     bar: TickPathBar;                  phaseName: string; phaseIndex: number; durationUs: number }
  | { kind: 'worker-claim';      durationUs: number;                phaseName: string; phaseIndex: number; systemName: string }
  | { kind: 'chunk-dispatch';    durationUs: number;                phaseName: string; phaseIndex: number; systemName: string }
  | { kind: 'phase-fence';       durationUs: number;                phaseName: string; phaseIndex: number; straggler: string | null }
  | { kind: 'post-tick';         durationUs: number; label: string; hue: number }
  | { kind: 'metronome';         durationUs: number; intentClass: MetronomeIntentClass | null };

function buildSegments(bars: TickPathBars, fullGantt: boolean, showMetronome: boolean): Segment[] {
  const out: Segment[] = [];
  // Metronome wait that PRECEDED this tick — sits at the start of the timeline (visually before
  // the tick's first phase). Off by default per design §5.4 ("the metronome is what the engine
  // wasn't doing — noise to most investigations"); flip the toolbar toggle to inspect throttling
  // / sleep behaviour. Suppressed at zero either way (steady-state "no jitter").
  if (showMetronome && bars.metronomeWaitUs > 0) {
    out.push({ kind: 'metronome', durationUs: bars.metronomeWaitUs, intentClass: bars.metronomeIntentClass });
  }
  bars.phases.forEach((phase, phaseIndex) => emitPhaseSegments(out, phase, phaseIndex, fullGantt));
  emitPostTickSegments(out, bars.postTick);
  return out;
}

function emitPhaseSegments(out: Segment[], phase: TickPathPhase, phaseIndex: number, fullGantt: boolean): void {
  for (const bar of phase.bars) {
    if (bar.workerClaimWaitUs > 0) {
      out.push({ kind: 'worker-claim', durationUs: bar.workerClaimWaitUs, phaseName: phase.name, phaseIndex, systemName: bar.systemName });
    }
    if (bar.chunkDispatchWaitUs > 0) {
      out.push({ kind: 'chunk-dispatch', durationUs: bar.chunkDispatchWaitUs, phaseName: phase.name, phaseIndex, systemName: bar.systemName });
    }
    out.push({ kind: 'system', bar, phaseName: phase.name, phaseIndex, durationUs: bar.durationUs });
  }
  if (fullGantt) {
    // Append non-CP bars after the CP bars in this phase. They render dimmed; their durations
    // inflate `totalUs` (computed as the sum of segment durations later) so the timeline grows,
    // but the CP focus stays visually distinct via the lower opacity. This is the v1 trade-off
    // documented in `09-system-dag.md §5.6`: full-Gantt is "what else ran", not "real wall-clock".
    for (const bar of phase.nonCpBars) {
      out.push({ kind: 'non-cp-system', bar, phaseName: phase.name, phaseIndex, durationUs: bar.durationUs });
    }
  }
  if (phase.phaseFenceWaitUs > 0) {
    out.push({ kind: 'phase-fence', durationUs: phase.phaseFenceWaitUs, phaseName: phase.name, phaseIndex, straggler: phase.phaseFenceStraggler });
  }
}

const POST_TICK_ITEMS: Array<{ key: keyof TickPathPostTick; label: string; hue: number }> = [
  { key: 'writeTickFenceUs',     label: 'WriteTickFence',     hue: 200 },
  { key: 'walFlushUs',           label: 'WAL flush',          hue: 30 },
  { key: 'tierBudgetUs',         label: 'TierBudget',         hue: 280 },
  { key: 'subscriptionOutputUs', label: 'SubscriptionOutput', hue: 140 },
  { key: 'tierIndexRebuildUs',   label: 'TierIndexRebuild',   hue: 250 },
  { key: 'dormancySweepUs',      label: 'DormancySweep',      hue: 60 },
];

function emitPostTickSegments(out: Segment[], postTick: TickPathPostTick): void {
  for (const item of POST_TICK_ITEMS) {
    const us = postTick[item.key];
    if (us > 0) {
      out.push({ kind: 'post-tick', durationUs: us, label: item.label, hue: item.hue });
    }
  }
}

// ── Placement (linear vs log) ─────────────────────────────────────────────

interface Placement {
  startFraction: number;
  lengthFraction: number;
}

/**
 * Convert each segment's `durationUs` into `[startFraction, lengthFraction]` along the major
 * axis (each fraction in [0, 1]). Linear is the trivial case `us / totalUs`. Log redistributes
 * proportionally — small segments get more pixels relative to big ones — but the SUM of fractions
 * still equals 1 so the timeline isn't truncated or padded.
 */
function placeSegments(segments: Segment[], scale: CpScale, totalUs: number): Placement[] {
  if (segments.length === 0 || totalUs <= 0) return [];

  if (scale === 'linear') {
    const out: Placement[] = [];
    let cursor = 0;
    for (const seg of segments) {
      const len = seg.durationUs / totalUs;
      out.push({ startFraction: cursor, lengthFraction: len });
      cursor += len;
    }
    return out;
  }
  // Log: weight = log10(us + 1). Total = sum of weights. Renormalize so fractions sum to 1.
  let totalLog = 0;
  for (const seg of segments) totalLog += Math.log10(seg.durationUs + 1);
  if (totalLog <= 0) {
    return segments.map(() => ({ startFraction: 0, lengthFraction: 0 }));
  }
  const out: Placement[] = [];
  let cursor = 0;
  for (const seg of segments) {
    const len = Math.log10(seg.durationUs + 1) / totalLog;
    out.push({ startFraction: cursor, lengthFraction: len });
    cursor += len;
  }
  return out;
}

// ── Phase stripes (header band) ───────────────────────────────────────────

interface PhaseStripeData {
  phaseName: string;
  phaseIndex: number;
  startFraction: number;
  endFraction: number;
}

/**
 * Coalesce consecutive segments with the same phaseIndex into a single header-band stripe.
 * Post-tick / metronome get their own stripes (`phaseIndex = -1` / `-2`) so the band always
 * spans the full timeline.
 */
function buildPhaseStripes(segments: Segment[], placement: Placement[], _totalUs: number): PhaseStripeData[] {
  const out: PhaseStripeData[] = [];
  let current: PhaseStripeData | null = null;
  segments.forEach((seg, i) => {
    const phaseName = segmentPhaseName(seg);
    const phaseIndex = segmentPhaseIndex(seg);
    const start = placement[i].startFraction;
    const end = start + placement[i].lengthFraction;
    if (current && current.phaseIndex === phaseIndex && current.phaseName === phaseName) {
      current.endFraction = end;
    } else {
      current = { phaseName, phaseIndex, startFraction: start, endFraction: end };
      out.push(current);
    }
  });
  return out;
}

function segmentPhaseName(seg: Segment): string {
  switch (seg.kind) {
    case 'system':
    case 'non-cp-system':
    case 'worker-claim':
    case 'chunk-dispatch':
    case 'phase-fence':
      return seg.phaseName;
    case 'post-tick':
      return 'Post-tick';
    case 'metronome':
      return 'Metronome wait';
  }
}

function segmentPhaseIndex(seg: Segment): number {
  switch (seg.kind) {
    case 'system':
    case 'non-cp-system':
    case 'worker-claim':
    case 'chunk-dispatch':
    case 'phase-fence':
      return seg.phaseIndex;
    case 'post-tick':
      return -1;
    case 'metronome':
      return -2;
  }
}

// ── PhaseStripe SVG node ──────────────────────────────────────────────────

function PhaseStripe({
  stripe,
  orientation,
  majorTotalPx,
  headerBandPx,
  utilization,
  isHovered,
  onMouseEnter,
  onMouseLeave,
}: {
  stripe: PhaseStripeData;
  orientation: Orientation;
  majorTotalPx: number;
  headerBandPx: number;
  /**
   * Per-phase parallelism (A2). Splits a 3 px bar below the colour stripe into a green "work"
   * head and a hatched-grey "wait" tail. `null` skips the band — for synthetic stripes (post-tick
   * / metronome), single-worker traces, and aggregate-mode bars.
   */
  utilization: PhaseUtilization | null;
  isHovered: boolean;
  onMouseEnter: () => void;
  onMouseLeave: () => void;
}) {
  const colour = stripe.phaseIndex === -1
    ? { stroke: 'hsl(220, 8%, 60%)', fill: 'hsl(220, 8%, 25%)' }
    : stripe.phaseIndex === -2
      ? { stroke: 'hsl(40, 60%, 60%)', fill: 'hsl(40, 50%, 25%)' }
      : colorForPhase(stripe.phaseIndex);
  const startPx = stripe.startFraction * majorTotalPx;
  const lengthPx = (stripe.endFraction - stripe.startFraction) * majorTotalPx;
  // Header-band layout (top→bottom in horizontal orientation; same math, swapped axes in vertical):
  //
  //   ┌──────────── HEADER_BAND_PX (22) ────────────┐
  //   │ name label area                              │ stripeStart - 3 (text baseline)
  //   │ ─── 5 px colour stripe ──────                │ stripeStart .. stripeEnd
  //   │ 1 px gap                                     │
  //   │ ━━━ 3 px parallelism bar ━━━━                │ parBarStart .. parBarEnd  (A2)
  //   │ 2 px breathing gap                           │
  //   └──────────────────────────────────────────────┘ headerBandPx → system bars start
  //
  // Numbers chosen so the original 18 px → 22 px bump only adds the 3 px bar + 1 px gap; everything
  // else stays where the user is already used to it.
  const stripePx = 5;
  const stripeParBarGapPx = 1;
  const parBarPx = 3;
  const parBarBottomGapPx = 2;
  const parBarEnd = headerBandPx - parBarBottomGapPx;
  const parBarStart = parBarEnd - parBarPx;
  const stripeEnd = parBarStart - stripeParBarGapPx;
  const stripeStart = stripeEnd - stripePx;
  // Hover visual: a slightly thicker, fully-opaque stripe + a faint band tint behind the stripe
  // so the user can see the matched phase even when squinting at a tiny stripe segment.
  const stripeFill = isHovered ? colour.stroke : colour.stroke;
  const stripeOpacity = isHovered ? 1 : 0.95;
  const stripeThickness = isHovered ? stripePx + 2 : stripePx;
  // Parallelism bar (A2). Width split = utilization × lengthPx green head + remainder hatched
  // grey tail. Hidden when `utilization` is null (synthetic stripes / single-worker / aggregate)
  // or when the stripe is too narrow to render meaningfully (< 4 px).
  const parBarTitle = utilization
    ? `parallelism: ${(utilization.utilization * 100).toFixed(0)}% · wait ${formatUsCompact(utilization.waitUs)} of ${formatUsCompact(utilization.capacityUs)}`
    : null;
  const renderParBar = utilization != null && lengthPx >= 4;

  if (orientation === 'horizontal') {
    const workPx = renderParBar ? lengthPx * utilization!.utilization : 0;
    const waitPx = renderParBar ? lengthPx - workPx : 0;
    return (
      <g>
        {isHovered && (
          <rect x={startPx} y={0} width={lengthPx} height={headerBandPx} fill={colour.stroke} opacity={0.18} />
        )}
        <rect
          x={startPx}
          y={stripeStart - (stripeThickness - stripePx)}
          width={lengthPx}
          height={stripeThickness}
          fill={stripeFill}
          opacity={stripeOpacity}
        />
        {renderParBar && (
          <g>
            {/* Slot background — 3 px hatched grey behind the work head, so even at 0 % utilization
                the slot reads as "this is where parallelism lives". */}
            <rect x={startPx} y={parBarStart} width={lengthPx} height={parBarPx} fill="url(#cp-hatch)" opacity={0.6} />
            {workPx > 0.5 && (
              <rect x={startPx} y={parBarStart} width={workPx} height={parBarPx} fill={parBarWorkFill(utilization!.utilization)} />
            )}
            {/* Hairline tick at the work/wait boundary — improves legibility when work head is short. */}
            {workPx > 0.5 && waitPx > 0.5 && (
              <line x1={startPx + workPx} y1={parBarStart} x2={startPx + workPx} y2={parBarEnd} stroke="hsl(var(--background))" strokeWidth={0.5} opacity={0.7} />
            )}
            <title>{parBarTitle}</title>
          </g>
        )}
        {lengthPx >= 40 && (
          <text
            x={startPx + 4}
            y={stripeStart - 3}
            fontSize={9}
            fontFamily="ui-monospace, monospace"
            fill="currentColor"
            className="fill-foreground"
          >
            {stripe.phaseName}
            {utilization && lengthPx >= 90 && (
              <tspan dx={6} className="fill-muted-foreground">
                {(utilization.utilization * 100).toFixed(0)}%
              </tspan>
            )}
          </text>
        )}
        {/* Transparent hit-target spanning the full header band so hovering the label or the
            empty band area also fires the sync, not just the 5 px stripe. */}
        <rect
          x={startPx}
          y={0}
          width={lengthPx}
          height={headerBandPx}
          fill="transparent"
          onMouseEnter={onMouseEnter}
          onMouseLeave={onMouseLeave}
        >
          {parBarTitle && <title>{parBarTitle}</title>}
        </rect>
      </g>
    );
  }
  // Vertical.
  return (
    <g>
      {isHovered && (
        <rect x={0} y={startPx} width={headerBandPx} height={lengthPx} fill={colour.stroke} opacity={0.18} />
      )}
      <rect
        x={stripeStart - (stripeThickness - stripePx)}
        y={startPx}
        width={stripeThickness}
        height={lengthPx}
        fill={stripeFill}
        opacity={stripeOpacity}
      />
      {renderParBar && (
        <g>
          <rect x={parBarStart} y={startPx} width={parBarPx} height={lengthPx} fill="url(#cp-hatch)" opacity={0.6} />
          {(() => {
            const workPx = lengthPx * utilization!.utilization;
            const waitPx = lengthPx - workPx;
            return (
              <>
                {workPx > 0.5 && (
                  <rect x={parBarStart} y={startPx} width={parBarPx} height={workPx} fill={parBarWorkFill(utilization!.utilization)} />
                )}
                {workPx > 0.5 && waitPx > 0.5 && (
                  <line x1={parBarStart} y1={startPx + workPx} x2={parBarEnd} y2={startPx + workPx} stroke="hsl(var(--background))" strokeWidth={0.5} opacity={0.7} />
                )}
              </>
            );
          })()}
          <title>{parBarTitle}</title>
        </g>
      )}
      {lengthPx >= 40 && (
        <text
          x={stripeStart - 3}
          y={startPx + 10}
          fontSize={9}
          fontFamily="ui-monospace, monospace"
          fill="currentColor"
          textAnchor="end"
          className="fill-foreground"
        >
          {stripe.phaseName}
          {utilization && lengthPx >= 90 && (
            <tspan dx={6} className="fill-muted-foreground">
              {(utilization.utilization * 100).toFixed(0)}%
            </tspan>
          )}
        </text>
      )}
      <rect
        x={0}
        y={startPx}
        width={headerBandPx}
        height={lengthPx}
        fill="transparent"
        onMouseEnter={onMouseEnter}
        onMouseLeave={onMouseLeave}
      >
        {parBarTitle && <title>{parBarTitle}</title>}
      </rect>
    </g>
  );
}

/**
 * Green ramp for the parallelism work-head: low utilization stays green-ish but reads as "more
 * grey hatched tail showing"; high utilization becomes saturated green. Independent of the pill's
 * range tone so per-tick spikes stand out even when the range mean looks fine.
 */
function parBarWorkFill(utilization: number): string {
  // Saturation ramps with utilization so 100 % phases punch visually.
  const sat = 35 + utilization * 30;
  return `hsl(142, ${sat}%, 45%)`;
}

function formatUsCompact(us: number): string {
  if (us < 1) return '0µs';
  if (us < 1000) return `${Math.round(us)}µs`;
  const ms = us / 1000;
  if (ms < 10) return `${ms.toFixed(2)}ms`;
  if (ms < 100) return `${ms.toFixed(1)}ms`;
  return `${Math.round(ms)}ms`;
}

// ── Segment shape (one rect per segment) ──────────────────────────────────

function SegmentShape({
  seg,
  startPx,
  lengthPx,
  orientation,
  barOffsetPx,
  barHeightPx,
  isSelected,
  isHovered,
  onMouseEnter,
  onMouseLeave,
  onClick,
}: {
  seg: Segment;
  startPx: number;
  lengthPx: number;
  orientation: Orientation;
  barOffsetPx: number;
  barHeightPx: number;
  isSelected: boolean;
  isHovered: boolean;
  onMouseEnter: (e: React.MouseEvent<SVGElement>) => void;
  onMouseLeave: () => void;
  onClick: () => void;
}) {
  const fill = colourForSegment(seg);
  // System bars use a hex from TIMELINE_PALETTE — pick a contrast-correct ink via WCAG luminance
  // so the label is legible on both the dark indigo (#30123B) and bright lime (#C1EE3B) ends of
  // the ramp. Post-tick fills are HSL with lightness 30 % so they're always dark — hard-code
  // white ink rather than parsing HSL through the luminance helper. Wait segments are hatched
  // overlays without labels, so they don't need a text colour.
  const textFill =
    seg.kind === 'system' || seg.kind === 'non-cp-system' ? pickTextColorFor(fill) :
    seg.kind === 'post-tick' ? '#fff' :
    undefined;
  // Drop-shadow only on "active work" boxes — system + post-tick. Wait segments + non-CP stay
  // flat: non-CP runs are also "real work" but the dim opacity already reads as "supporting cast",
  // and a shadow on them would compete with the CP-bar shadows for visual focus.
  const hasShadow = seg.kind === 'system' || seg.kind === 'post-tick';
  // System bars (CP and non-CP) are clickable — selection jumps to the system either way. Phase-
  // fence segments are clickable when a straggler is recorded — clicking jumps the selection to
  // the straggler. Worker-claim waits are clickable too — they precede the system that was
  // waiting, so clicking selects THAT system (the user's investigation target: "why couldn't a
  // worker pick this up?"). Chunk-dispatch wait isn't clickable yet — placeholder kind in v1.
  const isClickable = seg.kind === 'system'
    || seg.kind === 'non-cp-system'
    || (seg.kind === 'phase-fence' && seg.straggler != null)
    || seg.kind === 'worker-claim';
  // Metronome bars get a chip when an intentClass is known and there's enough room (~38 px). The
  // chip text is short — `Headroom` / `Throttled` / `CatchUp` — so the threshold is lower than the
  // 30 px we use for system / post-tick names. Drawn through the same showText path so it
  // benefits from the per-orientation rotation logic.
  const metronomeChip = seg.kind === 'metronome' && seg.intentClass != null && lengthPx >= 38
    ? seg.intentClass
    : null;
  const showText = (lengthPx >= 30 && (seg.kind === 'system' || seg.kind === 'non-cp-system' || seg.kind === 'post-tick')) || metronomeChip != null;
  const showHatch = seg.kind === 'worker-claim' || seg.kind === 'chunk-dispatch' || seg.kind === 'phase-fence' || seg.kind === 'metronome';

  // Coordinates per orientation. Major = time axis; minor = bar row, offset by header band.
  const x = orientation === 'horizontal' ? startPx : barOffsetPx;
  const y = orientation === 'horizontal' ? barOffsetPx : startPx;
  const width = orientation === 'horizontal' ? lengthPx : barHeightPx;
  const height = orientation === 'horizontal' ? barHeightPx : lengthPx;

  // Visual layers all `pointer-events="none"` so the transparent hit-rect at the bottom of the
  // group is the SOLE owner of hit testing. Without this, the hatched overlay (drawn on top of
  // the fill rect) would intercept events and break the parent <g>'s mouseenter bubble.
  return (
    <g>
      <rect
        x={x}
        y={y}
        width={width}
        height={height}
        fill={fill}
        // Non-CP bars in full-Gantt mode read as "ran but off the critical path" via reduced
        // opacity. CP bars stay opaque; wait/metronome use 0.35 to blend into the hatch.
        opacity={showHatch ? 0.35 : seg.kind === 'non-cp-system' ? 0.4 : 1}
        // Selection outranks hover — when a system is both clicked AND hovered, the primary
        // ring stays put. Hover supplies a soft theme-foreground stroke at 60 % alpha, dialled
        // down so it's visible against bright Turbo-palette fills without punching like ink in
        // light theme.
        stroke={isSelected ? 'hsl(var(--primary))' : isHovered ? 'hsl(var(--foreground) / 0.6)' : 'transparent'}
        strokeWidth={isSelected ? 2 : isHovered ? 2 : 0}
        filter={hasShadow ? (orientation === 'horizontal' ? 'url(#cp-shadow-h)' : 'url(#cp-shadow-v)') : undefined}
        pointerEvents="none"
      />
      {showHatch && (
        <rect
          x={x}
          y={y}
          width={width}
          height={height}
          fill="url(#cp-hatch)"
          pointerEvents="none"
        />
      )}
      {showText && (
        <text
          x={x + width / 2}
          y={y + height / 2 + 3}
          fontSize={10}
          fontFamily="ui-monospace, monospace"
          fill={textFill ?? 'currentColor'}
          className={textFill ? undefined : 'fill-foreground'}
          textAnchor="middle"
          pointerEvents="none"
        >
          {clipText(
            seg.kind === 'system' ? seg.bar.systemName
              : seg.kind === 'non-cp-system' ? seg.bar.systemName
              : seg.kind === 'post-tick' ? seg.label
              : metronomeChip ?? '',
            width - 8,
          )}
        </text>
      )}
      {/*
        Transparent hit-rect — rendered last so it sits above every visual layer in the group
        and owns all hover/click events. Without `fill="transparent"` (a paint that *does*
        respond to pointer-events), an SVG rect with no fill is invisible to the hit-test.
      */}
      <rect
        x={x}
        y={y}
        width={width}
        height={height}
        fill="transparent"
        onMouseEnter={onMouseEnter}
        onMouseLeave={onMouseLeave}
        onClick={isClickable ? onClick : undefined}
        style={{ cursor: isClickable ? 'pointer' : 'default' }}
      />
    </g>
  );
}

function colourForSegment(seg: Segment): string {
  switch (seg.kind) {
    case 'system':
    case 'non-cp-system':
      // 13-colour Turbo ramp from the profiler's per-operation overview palette (`Cache Fetch`,
      // `Cache Allocate`, `Cache Evicted`, …). Cycled by a stable hash of `systemName` so a
      // given system always lands on the same hue across ticks / sessions. Phase identity comes
      // from the header stripe; heat (duration) is conveyed by bar *length*, not colour. Non-CP
      // bars share the palette but render with reduced opacity (see `SegmentShape`) so they read
      // as "ran but off the critical path".
      return TIMELINE_PALETTE[hashString(seg.bar.systemName) % TIMELINE_PALETTE.length];
    case 'worker-claim':
    case 'chunk-dispatch':
      return 'hsla(40, 30%, 40%, 0.5)';
    case 'phase-fence':
      return 'hsla(0, 30%, 40%, 0.5)';
    case 'post-tick':
      return `hsla(${seg.hue}, 50%, 30%, 0.7)`;
    case 'metronome':
      return 'hsla(40, 50%, 25%, 0.6)';
  }
}

/**
 * Resolve the user-facing orientation setting (which may be "auto") to the concrete layout the
 * renderer needs ("horizontal" | "vertical"). Auto picks horizontal when the viewport is wider
 * than tall, vertical otherwise; ties resolve to horizontal because that's the more natural
 * default reading direction for timelines. Falls back to horizontal when the viewport hasn't
 * been measured yet (initial mount, before ResizeObserver fires).
 */
function resolveOrientation(raw: Orientation, width: number, height: number): Exclude<Orientation, 'auto'> {
  if (raw === 'horizontal') return 'horizontal';
  if (raw === 'vertical') return 'vertical';
  if (width <= 0 || height <= 0) return 'horizontal';
  return width >= height ? 'horizontal' : 'vertical';
}

/** djb2 hash — small, deterministic, no allocations. Mod into the palette at the call site. */
function hashString(s: string): number {
  let h = 5381;
  for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) | 0;
  return h < 0 ? -h : h;
}

function clipText(text: string, maxWidth: number): string {
  // Rough char-width heuristic — monospace 10 px ≈ 6 px/char. Saves measuring the canvas every
  // segment.
  const maxChars = Math.max(0, Math.floor(maxWidth / 6));
  if (text.length <= maxChars) return text;
  if (maxChars <= 1) return '';
  return text.slice(0, maxChars - 1) + '…';
}

// ── Tooltip ───────────────────────────────────────────────────────────────

interface TooltipState {
  segment: Segment;
  x: number;
  y: number;
}

function Tooltip({ tooltip }: { tooltip: TooltipState }) {
  const lines = describeSegment(tooltip.segment);
  // Portaled to document.body with position:fixed so it escapes any ancestor that has
  // overflow, transforms, or filters (dockview panels can have all three). Coordinates are
  // viewport-space, so we clamp against window.innerWidth/innerHeight directly.
  const TOOLTIP_W = 240;
  const TOOLTIP_H = 14 * lines.length + 12;
  let left = tooltip.x + 12;
  let top = tooltip.y + 12;
  if (left + TOOLTIP_W > window.innerWidth) left = tooltip.x - TOOLTIP_W - 12;
  if (top + TOOLTIP_H > window.innerHeight) top = tooltip.y - TOOLTIP_H - 12;
  return createPortal(
    <div
      className="pointer-events-none fixed z-[1000] rounded border border-border bg-card px-2 py-1.5 font-mono text-[10px] text-foreground shadow-md"
      style={{ left, top, width: TOOLTIP_W }}
    >
      {lines.map((line, i) => (
        <div key={i} className={i === 0 ? 'mb-1 font-semibold text-foreground' : 'text-muted-foreground'}>
          {line}
        </div>
      ))}
    </div>,
    document.body,
  );
}

function describeSegment(seg: Segment): string[] {
  switch (seg.kind) {
    case 'system': {
      const lines: string[] = [`${seg.bar.systemName} — ${formatUs(seg.bar.durationUs)}`];
      lines.push(`phase: ${seg.phaseName || '(unphased)'}`);
      if (seg.bar.workerClaimWaitUs > 0) lines.push(`worker-claim wait: ${formatUs(seg.bar.workerClaimWaitUs)}`);
      if (seg.bar.chunkDispatchWaitUs > 0) lines.push(`chunk-dispatch wait: ${formatUs(seg.bar.chunkDispatchWaitUs)}`);
      if (seg.bar.isParallel) lines.push(`parallel: ${seg.bar.workersTouched} workers / ${seg.bar.chunksProcessed} chunks`);
      return lines;
    }
    case 'non-cp-system': {
      const lines: string[] = [`${seg.bar.systemName} — ${formatUs(seg.bar.durationUs)}`];
      lines.push(`phase: ${seg.phaseName || '(unphased)'}`);
      lines.push('NOT on the critical path (full Gantt mode).');
      if (seg.bar.isParallel) lines.push(`parallel: ${seg.bar.workersTouched} workers / ${seg.bar.chunksProcessed} chunks`);
      return lines;
    }
    case 'worker-claim':
      return [
        `Worker-claim wait — ${formatUs(seg.durationUs)}`,
        `before: ${seg.systemName}`,
        `phase: ${seg.phaseName}`,
        'Gap between deps cleared and a worker picking the system up.',
        'Click to select the waiting system.',
      ];
    case 'chunk-dispatch':
      return [
        `Chunk-dispatch wait — ${formatUs(seg.durationUs)}`,
        `before: ${seg.systemName}`,
        `phase: ${seg.phaseName}`,
        'Work-stealing imbalance across workers.',
      ];
    case 'phase-fence': {
      const lines = [
        `Phase-fence wait — ${formatUs(seg.durationUs)}`,
        `phase: ${seg.phaseName}`,
        seg.straggler ? `straggler: ${seg.straggler}` : 'no straggler recorded',
        'CP waited for the slowest non-CP system to clear the fence.',
      ];
      if (seg.straggler) lines.push('Click to jump to the straggler.');
      return lines;
    }
    case 'post-tick':
      return [`${seg.label} — ${formatUs(seg.durationUs)}`, 'Post-tick serial work.'];
    case 'metronome': {
      const lines = [`Metronome wait — ${formatUs(seg.durationUs)}`];
      if (seg.intentClass) lines.push(`intent: ${seg.intentClass} — ${describeIntent(seg.intentClass)}`);
      lines.push('Idle gap from previous TickEnd to this TickStart.');
      return lines;
    }
  }
}

function describeIntent(intent: MetronomeIntentClass): string {
  switch (intent) {
    case 'CatchUp':   return 'engine fell behind — wait elided';
    case 'Throttled': return 'paced down for power / overload';
    case 'Headroom':  return 'normal idle — work fits within tick budget';
  }
}

function formatUs(us: number): string {
  if (us < 1) return '0µs';
  if (us < 1000) return `${Math.round(us)}µs`;
  const ms = us / 1000;
  return ms < 10 ? `${ms.toFixed(2)}ms` : `${ms.toFixed(1)}ms`;
}

function FallbackBadge() {
  return (
    <div
      className="pointer-events-none absolute right-2 top-2 z-40 rounded bg-amber-950/80 px-2 py-0.5 font-mono text-[9px] uppercase text-amber-300"
      title="The topology has no RFC 07 access declarations — without dependency edges, the walker can't trace a critical path. Showing every system that ran, sorted by startUs."
    >
      execution order
    </div>
  );
}
