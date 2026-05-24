import { useEffect, useRef, useState } from 'react';
import uPlot, { type AlignedData, type Options as UPlotOptions } from 'uplot';
import 'uplot/dist/uPlot.min.css';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import type { TopologyDto } from '@/api/generated/model/topologyDto';
import { ACCESS_COLOR, accessKindFor, aggregateAccessKindForDomain, type Bar, type DensityCell } from './barBuilding';
import DataFlowTooltip, { type BarTickStats } from './DataFlowTooltip';
import type { PhaseSegment } from './phaseLayout';
import type { Track } from './trackBuilding';

/**
 * Thin React wrapper around uPlot for the Data Flow Timeline. uPlot owns the X-axis (tick number), pan/zoom,
 * cursor, and resize handling; bar rendering happens in a custom `hooks.draw` plugin so we keep total control
 * over the multi-row Marey-style layout the design specifies.
 *
 * Design refs: §13 (renderer choice), §11 (interaction details), §6.1 (phase fences as structural axis).
 *
 * The `hoverIsolate` prop carries the (systemName, tickNumber) pair currently under the cursor — when set,
 * non-matching bars dim to ~25% opacity, which is the v1 multi-row unification per design D3.
 */
export interface DataFlowTimelineProps {
  /** Visible tracks in display order (Y axis). Order is preserved as render order, top to bottom. */
  tracks: readonly Track[];
  /**
   * Bars to draw. Each one renders on the row matching its `trackId`; a bar with no matching track is dropped silently.
   * Bar coordinates are in normalized [0, 1] phase-space (see {@link Bar.xStart}).
   */
  bars: readonly Bar[];
  /** Phase column boundaries in normalized [0, 1] phase-space. The X scale spans [0, 1]; segments tile contiguously. */
  phaseSegments: readonly PhaseSegment[];
  /**
   * Density-mode heatmap cells — paired with `bars: []` when the panel is in density aggregation mode. Each cell
   * fills its (track, phase) rectangle with an alpha-modulated color whose intensity scales with `touchCount`.
   */
  densityCells?: readonly DensityCell[];
  /** Topology systems — used by the bar coloring path to look up access kind. */
  systems: readonly SystemDefinitionDto[];
  /** When set, only bars matching this (systemName, tickNumber) render at full opacity. */
  hoverIsolate: { systemName: string; tickNumber: number } | null;
  /** Currently selected system (cross-panel) — gets a stronger outline on bars. */
  selectedSystem: string | null;
  /** Phase D (#327): currently-selected track id — the matching row label highlights with an amber outline. */
  selectedTrackId: string | null;
  /**
   * Set of `${systemName}|${tickNumber}` keys whose system was reactive-skipped this tick (non-zero skipReason).
   * When non-null, matching bars render at reduced opacity. Filter chip from the toolbar; null = filter off.
   */
  skippedKeys: ReadonlySet<string> | null;
  /** Click handler — fires with the system name when a bar is clicked. */
  onBarClick?: (systemName: string) => void;
  /** Hover handler — fires with the (system, tick) pair (or null when unhovered). */
  onBarHover?: (key: { systemName: string; tickNumber: number } | null) => void;
  /** Phase D (#327): track-row click handler — fires with the track id when a row label is clicked. */
  onTrackClick?: (trackId: string) => void;
  /** Phase header click — fires with the phase name when its column header is clicked. Cycles collapse state. */
  onPhaseClick?: (phaseName: string) => void;
  /**
   * Increments to request a fit-to-selection: clears wheel-zoom so the X axis reverts to the full [0, 1]
   * phase-space. Bumped by the panel's `F` key handler (spec §11.4). Initial mount value is irrelevant.
   */
  fitToken?: number;
  /** Topology — passed through to the bar tooltip for access-set / phase resolution. */
  topology?: TopologyDto | null;
  /**
   * Resolver that converts a hovered bar into the per-tick stats the tooltip displays. The panel computes
   * this once per (topology, summaries) change; the timeline calls it on hover. Returning null hides the
   * tick-stats lines (tooltip still shows system + phase + access set + gating).
   */
  resolveBarTickStats?: (bar: Bar) => BarTickStats | null;
  /** Tick durationUs of the bar's tick — passed to the tooltip's "tick N (Xms)" line. */
  resolveTickDurationUs?: (tickNumber: number) => number | null;
  /**
   * Converts a normalized [0, 1] phase-space X value to a label rendered on the X-axis rule. Panel-side because
   * only the panel knows the dominant tick + per-phase µs spans needed to invert the phase mapping. Returns ""
   * for splits the formatter wants to suppress (e.g., labels too close together at heavy zoom).
   */
  formatXLabel?: (x01: number) => string;
}

const ROW_HEIGHT_PX = 22;
const ROW_PADDING_PX = 2;
/** Height of the phase-header strip above the canvas. Sized to leave room for a ~12 px label and click target. */
const PHASE_HEADER_HEIGHT_PX = 18;
/** Width below which a phase header label is hidden (keeps thin/collapsed segments from spamming truncated text). */
const PHASE_HEADER_MIN_LABEL_PX = 32;

export default function DataFlowTimeline(props: DataFlowTimelineProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const plotRef = useRef<uPlot | null>(null);
  // Refs hold the latest props so the uPlot draw hook closure (created once) sees fresh data on every redraw.
  const propsRef = useRef(props);
  propsRef.current = props;

  // User-driven sub-range from wheel zoom. null = follow the parent tickRange (default). Cleared by middle-click
  // or whenever tickRange changes externally — picking a new tick on the Profiler should reset zoom rather than
  // smear stale bounds across an unrelated slice.
  //
  // Dual-track: the ref is read synchronously by uPlot's `scales.x.range` closure on every redraw (no React render
  // cycle), while the state mirror drives HTML re-renders for the phase header strip (which lives outside the canvas
  // and needs its widths remapped when the visible range narrows). State updates fire at most once per RAF since
  // wheel events accumulate into `pendingNotches` and apply on `requestAnimationFrame`.
  const zoomedRangeRef = useRef<[number, number] | null>(null);
  const [zoomedRange, setZoomedRange] = useState<[number, number] | null>(null);

  // Tooltip state — drives the absolutely-positioned overlay rendered alongside the canvas. The cursor position
  // is in container-local CSS pixels (not canvas-local), so the overlay is laid out without canvas geometry math.
  const [tooltipBar, setTooltipBar] = useState<Bar | null>(null);
  const [cursorPos, setCursorPos] = useState<{ x: number; y: number } | null>(null);
  const [containerWidth, setContainerWidth] = useState(0);
  // CSS-pixel bounding box of uPlot's `.u-over` (the actual drawing area), measured relative to its canvas
  // container. uPlot reserves auto-padding for axis-label overflow, so `.u-over` is inset from the container —
  // the HTML phase-header strip needs to mirror that inset, otherwise headers and bars don't align horizontally.
  const [chartBox, setChartBox] = useState<{ leftPx: number; widthPx: number }>({ leftPx: 0, widthPx: 0 });

  // Initialize uPlot once. All subsequent prop changes flow through setData / setSize / redraw.
  useEffect(() => {
    if (!containerRef.current) return;

    const drawBars: NonNullable<UPlotOptions['hooks']>['draw'] = [
      (u: uPlot) => drawBarsToCanvas(u, propsRef.current),
    ];

    const drawAxes: NonNullable<UPlotOptions['hooks']>['drawAxes'] = [
      (u: uPlot) => drawPhaseFences(u, propsRef.current),
    ];

    // x = tick numbers; series[1] is a hidden ghost so uPlot sets up the cursor + scales correctly.
    const opts: UPlotOptions = {
      width: containerRef.current.clientWidth,
      height: containerRef.current.clientHeight,
      class: 'dataflow-timeline',
      // Kill uPlot's auto-padding on left/right so `.u-over` fills the full canvas width. Without this, uPlot
      // reserves ~25 px on each side for axis-label overflow at edge ticks, producing a visible "gap" between
      // the track-label column and the first bar (and a matching gap on the right). With padding=0, bars and
      // phase fences sit flush with the canvas edges; the X-axis time labels at the edges may extend slightly
      // beyond `.u-over` but `chartBox` still measures the actual `.u-over` rect for the HTML phase header
      // alignment, so we're robust either way.
      padding: [null, 0, null, 0],
      cursor: {
        x: true,
        y: false,
        // Disabled: zoom is driven by wheel (anchor at cursor) + middle-click reset, not drag-rectangle. With a
        // range() closure on scales.x in place, setScale calls from drag would be overridden on the next redraw,
        // so leaving drag enabled would tease an interaction that does nothing.
        drag: { x: false, y: false, setScale: false },
      },
      scales: {
        // X axis is normalized phase-space [0, 1]: bar.xStart/xEnd are already in this space (barBuilding maps
        // through the PhaseAxis). Wheel-zoom narrows to a [zoomLo, zoomHi] sub-range; F-key / middle-click reset
        // by clearing zoomedRangeRef. The two ghost series carry the boundary values so uPlot's auto-scale falls
        // through to the range() closure consistently across redraws.
        x: {
          time: false,
          range: () => zoomedRangeRef.current ?? [0, 1],
        },
        y: { range: () => [computeYExtent(propsRef.current.tracks), 0] },
      },
      axes: [
        // Phase NAMES are in the HTML strip above the canvas; this bottom axis carries the TIME RULE — labels
        // are µs offsets from the dominant tick's start, computed by the panel-supplied `formatXLabel`. Without
        // a formatter (e.g., topology not loaded yet), labels fall through to uPlot's default normalized [0, 1]
        // formatting, which is harmless — it shows 0.0..1.0 ticks. Stroke + grid stay default for the rule lines.
        {
          show: true,
          values: (_self, splits) => splits.map((s) => {
            const f = propsRef.current.formatXLabel;
            if (!f) return s.toFixed(2);
            return f(s);
          }),
        },
        { show: false },
      ],
      series: [
        { label: 'tick' },
        { label: '_ghost', show: false, points: { show: false } },
      ],
      hooks: { draw: drawBars, drawAxes },
    };

    // X scale is [0, 1] phase-space; ghost series anchors the scale at both endpoints.
    const data: AlignedData = [
      [0, 1],
      [0, 0],
    ];

    plotRef.current = new uPlot(opts, data, containerRef.current);

    // Click + hover routing.
    const canvas = containerRef.current.querySelector<HTMLCanvasElement>('.u-over');
    const onClick = (e: MouseEvent) => {
      const u = plotRef.current;
      if (!u) return;
      const hit = barAtPoint(u, e, propsRef.current);
      if (hit && propsRef.current.onBarClick) {
        propsRef.current.onBarClick(hit.systemName);
      }
    };
    const onMove = (e: MouseEvent) => {
      const u = plotRef.current;
      if (!u) return;
      const hit = barAtPoint(u, e, propsRef.current);
      propsRef.current.onBarHover?.(hit ? { systemName: hit.systemName, tickNumber: hit.tickNumber } : null);
      // Local tooltip state — drives the overlay rendered below. Cursor coords are container-local CSS pixels.
      const containerRect = containerRef.current?.getBoundingClientRect();
      if (containerRect) {
        setCursorPos({ x: e.clientX - containerRect.left, y: e.clientY - containerRect.top });
      }
      setTooltipBar(hit);
    };
    const onLeave = () => {
      propsRef.current.onBarHover?.(null);
      setTooltipBar(null);
      setCursorPos(null);
    };

    // Wheel zoom anchored at the cursor X. Clamps to the parent tickRange so the user can't scroll out past the
    // slice the Profiler asked for — the Data Flow panel is always a sub-view of the selected window.
    //
    // Two subtleties learned the hard way:
    //   1. Browsers fire many small wheel events per physical notch on high-DPI / trackpad inputs (deltaMode=PIXEL).
    //      Applying a fixed factor per event compounds to absurd zoom levels in a single swipe. We accumulate
    //      deltaY and apply once per animation frame, normalizing across deltaMode (LINE=1, PIXEL=0, PAGE=2).
    //   2. We do NOT absorb overflow at the parent-window edges. Letting the span shrink at the edge preserves
    //      the cursor-anchor invariant (the data point under the cursor stays put). Absorbing overflow shifts
    //      the anchor away from the cursor, which feels like view drift.
    const MIN_SPAN = 1e-4; // floor in tick units — well above float64's relative precision; lets you inspect sub-µs detail
    const ZOOM_PER_NOTCH = 1.2;
    // Map raw deltaY to "notches": one mouse-wheel notch ≈ 100 px in PIXEL mode, 1 line in LINE mode, 1 page in PAGE mode.
    const NOTCH_PX = 100;
    const NOTCH_LINE = 1;
    const NOTCH_PAGE = 1;
    let pendingNotches = 0;
    let pendingFrame = 0;
    let pendingCursorX = 0;
    const applyPendingZoom = () => {
      pendingFrame = 0;
      const notches = pendingNotches;
      pendingNotches = 0;
      const u = plotRef.current;
      if (!u || notches === 0) return;
      // X is normalized [0, 1] phase-space. Wheel zoom narrows that range; we never zoom out past it.
      const fullMin = 0;
      const fullMax = 1;
      const cur = zoomedRangeRef.current ?? [fullMin, fullMax];
      const [lo, hi] = cur;
      const anchor = u.posToVal(pendingCursorX, 'x');
      // Negative notches = wheel-up = zoom in (factor > 1 shrinks the range).
      const factor = Math.pow(ZOOM_PER_NOTCH, -notches);
      let newLo = anchor - (anchor - lo) / factor;
      let newHi = anchor + (hi - anchor) / factor;
      // Floor: cap span at MIN_SPAN preserving the cursor anchor's relative position.
      const span = newHi - newLo;
      if (span < MIN_SPAN) {
        const t = (anchor - newLo) / Math.max(span, Number.MIN_VALUE); // anchor's normalized position in the would-be window
        newLo = anchor - t * MIN_SPAN;
        newHi = newLo + MIN_SPAN;
      }
      // Clamp at parent edges — DO NOT absorb overflow on the opposite side; let the span shrink. Keeps anchor under cursor.
      if (newLo < fullMin) newLo = fullMin;
      if (newHi > fullMax) newHi = fullMax;
      // Fully zoomed out → null so range() flows through tickRange again (lets external Profiler updates take over cleanly).
      if (newLo <= fullMin + 1e-12 && newHi >= fullMax - 1e-12) {
        zoomedRangeRef.current = null;
        setZoomedRange(null);
      } else {
        const next: [number, number] = [newLo, newHi];
        zoomedRangeRef.current = next;
        setZoomedRange(next);
      }
      u.redraw(true, true);
    };
    const onWheel = (e: WheelEvent) => {
      const u = plotRef.current;
      if (!u) return;
      e.preventDefault();
      // Normalize deltaY across input modes into "notches". Sign convention: deltaY > 0 = scroll down = zoom out.
      let notch: number;
      if (e.deltaMode === 0) notch = e.deltaY / NOTCH_PX;        // PIXEL — most trackpads + smooth-scroll mice
      else if (e.deltaMode === 1) notch = e.deltaY / NOTCH_LINE; // LINE — classic mouse wheels on Windows
      else notch = e.deltaY / NOTCH_PAGE;                         // PAGE — rare
      // Cap per-event contribution so a single high-velocity flick can't blow past the floor in one event.
      if (notch > 1) notch = 1; else if (notch < -1) notch = -1;
      pendingNotches += notch;
      const overRect = u.over.getBoundingClientRect();
      pendingCursorX = e.clientX - overRect.left;
      if (pendingFrame === 0) {
        pendingFrame = requestAnimationFrame(applyPendingZoom);
      }
    };

    // Middle-click resets zoom. preventDefault on mousedown is required to suppress the browser's autoscroll
    // bubble-cursor on Windows. auxclick has spotty cross-browser behavior with button=1, so listen on mousedown.
    const onMouseDown = (e: MouseEvent) => {
      if (e.button !== 1) return;
      e.preventDefault();
      if (zoomedRangeRef.current === null) return;
      zoomedRangeRef.current = null;
      setZoomedRange(null);
      plotRef.current?.redraw(true, true);
    };

    canvas?.addEventListener('click', onClick);
    canvas?.addEventListener('mousemove', onMove);
    canvas?.addEventListener('mouseleave', onLeave);
    canvas?.addEventListener('wheel', onWheel, { passive: false });
    canvas?.addEventListener('mousedown', onMouseDown);

    const updateChartBox = () => {
      const containerEl = containerRef.current;
      const overEl = containerEl?.querySelector<HTMLDivElement>('.u-over');
      if (!containerEl || !overEl) return;
      const containerRect = containerEl.getBoundingClientRect();
      const overRect = overEl.getBoundingClientRect();
      setChartBox({
        leftPx: Math.max(0, overRect.left - containerRect.left),
        widthPx: overRect.width,
      });
    };
    const ro = new ResizeObserver(() => {
      const el = containerRef.current;
      if (el && plotRef.current) {
        plotRef.current.setSize({ width: el.clientWidth, height: el.clientHeight });
        setContainerWidth(el.clientWidth);
        // setSize triggers a synchronous layout pass inside uPlot; measure right after to catch the new bbox.
        updateChartBox();
      }
    });
    ro.observe(containerRef.current);
    setContainerWidth(containerRef.current.clientWidth);
    updateChartBox();

    return () => {
      ro.disconnect();
      if (pendingFrame !== 0) cancelAnimationFrame(pendingFrame);
      canvas?.removeEventListener('click', onClick);
      canvas?.removeEventListener('mousemove', onMove);
      canvas?.removeEventListener('mouseleave', onLeave);
      canvas?.removeEventListener('wheel', onWheel);
      canvas?.removeEventListener('mousedown', onMouseDown);
      plotRef.current?.destroy();
      plotRef.current = null;
    };
    // Mount-once init: propsRef keeps the closures fresh, so listing props in the deps would only
    // tear down + rebuild uPlot on every prop change, defeating the design.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Reset zoom when the phase axis structure changes (different tick selection → different phase span totals,
  // different collapse state). The zoom is a sub-range in [0, 1] which is meaningful only relative to the
  // current segment layout; carrying it across a layout change creates surprising "the cursor jumps" UX.
  useEffect(() => {
    zoomedRangeRef.current = null;
    setZoomedRange(null);
    plotRef.current?.redraw(true, true);
  }, [props.phaseSegments]);

  // F key (spec §11.4): fit-to-selection clears the wheel zoom. The panel bumps `fitToken`; we react
  // by clearing zoomedRangeRef and redrawing — same path as middle-click reset. No-op on mount because
  // zoomedRangeRef is already null.
  useEffect(() => {
    if (props.fitToken === undefined) return;
    zoomedRangeRef.current = null;
    setZoomedRange(null);
    plotRef.current?.redraw(true, true);
  }, [props.fitToken]);

  // Track count → height adjusts. uPlot recalculates Y extent via the closure in scales.y.range above.
  useEffect(() => {
    plotRef.current?.redraw(false, true);
  }, [props.tracks, props.bars, props.phaseSegments, props.hoverIsolate, props.selectedSystem, props.systems]);

  return (
    <div className="flex h-full w-full flex-col overflow-hidden">
      {/* Phase header strip — clickable headers for collapse. Always shows the full [0, 1] phase axis even when
          the canvas is zoomed; acts as a "you are here" overview. Aligned to uPlot's `.u-over` (the actual
          drawing area) via `chartBox`, since uPlot reserves horizontal padding inside its canvas for axis-label
          overflow that the HTML strip would otherwise step into. */}
      {props.phaseSegments.length > 0 && (
        <div
          className="flex shrink-0 flex-row border-b border-border bg-card"
          style={{ height: `${PHASE_HEADER_HEIGHT_PX}px` }}
        >
          <div className="shrink-0" style={{ width: '180px' }} />
          <div className="relative min-w-0 flex-1">
            {/* Inner box mirrors uPlot's `.u-over` rect so phase boundaries line up with bar/fence positions.
                `overflow-hidden` so headers extending past the visible zoom window get clipped instead of
                bleeding into the track-label column or beyond the canvas right edge. */}
            <div
              className="absolute top-0 overflow-hidden"
              style={{
                left: `${chartBox.leftPx}px`,
                width: chartBox.widthPx > 0 ? `${chartBox.widthPx}px` : '100%',
                height: `${PHASE_HEADER_HEIGHT_PX}px`,
              }}
            >
              {(() => {
                // Map each phase segment from full [0, 1] axis-space into the visible zoom window. When zoomedRange
                // is null, visMin=0 / visMax=1 is the identity transform. Headers fully outside the window are
                // skipped; partial overlaps are placed (and clipped by the parent's overflow-hidden) so the user
                // sees the visible portion of each phase header even when only one phase is on screen.
                const visMin = zoomedRange ? zoomedRange[0] : 0;
                const visMax = zoomedRange ? zoomedRange[1] : 1;
                const visSpan = Math.max(visMax - visMin, 1e-9);
                return props.phaseSegments.map((seg) => {
                  if (seg.xEnd <= visMin || seg.xStart >= visMax) return null;
                  const leftPct = ((seg.xStart - visMin) / visSpan) * 100;
                  const widthPct = ((seg.xEnd - seg.xStart) / visSpan) * 100;
                  if (widthPct <= 0) return null;
                  return (
                    <button
                      key={seg.name}
                      type="button"
                      className="absolute top-0 truncate border-r border-border bg-muted/40 px-1 text-fs-sm leading-none text-foreground last:border-r-0 hover:bg-muted"
                      style={{
                        left: `${leftPct}%`,
                        width: `${widthPct}%`,
                        height: `${PHASE_HEADER_HEIGHT_PX}px`,
                        lineHeight: `${PHASE_HEADER_HEIGHT_PX}px`,
                        minWidth: 0,
                      }}
                      title={`${seg.name} — click to cycle collapse`}
                      data-testid={`data-flow-phase-${seg.name}`}
                      onClick={() => props.onPhaseClick?.(seg.name)}
                    >
                      {/* Hide label when the visible width is too narrow to read; the title attribute still works. */}
                      <span style={{ visibility: widthPct >= (PHASE_HEADER_MIN_LABEL_PX / 6) ? 'visible' : 'hidden' }}>
                        {seg.name}
                      </span>
                    </button>
                  );
                });
              })()}
            </div>
          </div>
        </div>
      )}
      <div className="flex min-h-0 flex-1 flex-row overflow-hidden">
      {/* Y-axis row labels: rendered as a separate flex column so HTML accessibility + CSS theming work naturally. */}
      <div
        className="flex shrink-0 select-none flex-col border-r border-border bg-card"
        style={{ width: '180px', overflow: 'hidden' }}
      >
        {props.tracks.map((track) => {
          const isSelected = props.selectedTrackId === track.id;
          return (
            <button
              key={track.id}
              type="button"
              className={
                'flex items-center truncate px-2 text-left text-xs ' +
                (isSelected
                  ? 'bg-amber-500/20 font-semibold text-foreground ring-1 ring-amber-400/60'
                  : 'text-foreground hover:bg-muted')
              }
              style={{ height: `${ROW_HEIGHT_PX}px`, lineHeight: `${ROW_HEIGHT_PX}px` }}
              title={track.label}
              data-testid={`data-flow-track-${track.id}`}
              aria-pressed={isSelected}
              onClick={() => props.onTrackClick?.(track.id)}
            >
              {track.label}
            </button>
          );
        })}
      </div>
      {/* `min-w-0` is required so the flex parent can SHRINK below the uPlot canvas's current pixel width.
          Without it, flex defaults to `min-width: auto` which locks at intrinsic content width — making the
          panel un-shrinkable once uPlot sized the canvas. Same reason `min-h-0` matters for height. */}
      <div ref={containerRef} className="relative min-h-0 min-w-0 flex-1">
        <DataFlowTooltip
          bar={tooltipBar}
          cursor={cursorPos}
          tickDurationUs={tooltipBar ? props.resolveTickDurationUs?.(tooltipBar.tickNumber) ?? null : null}
          barTickStats={tooltipBar ? props.resolveBarTickStats?.(tooltipBar) ?? null : null}
          topology={props.topology ?? null}
          containerWidth={containerWidth}
        />
      </div>
      </div>
    </div>
  );
}

/**
 * Compute the y-extent uPlot uses to space rows. The Y scale runs from 0 (top of chart) to N (bottom row),
 * one unit per track. Y is treated as a discrete row index in the bar drawing path.
 */
function computeYExtent(tracks: readonly Track[]): number {
  return Math.max(1, tracks.length);
}

/**
 * Draw every bar onto uPlot's overlay canvas. Runs after uPlot's own series have been drawn (which are empty
 * placeholders in our case). For each bar:
 * - Resolve its row index from `tracks.findIndex(t => t.id === bar.trackId)` (cached)
 * - Compute pixel x-extent from the current x scale + BAR_HALF_WIDTH_TICKS
 * - Compute pixel y-extent from the row index × ROW_HEIGHT_PX
 * - Resolve color from `accessKindFor(system, componentName)` — the row's component is encoded in the trackId
 * - Apply hover-isolate dimming: non-matching (sys, tick) bars draw at 25% opacity
 */
function drawBarsToCanvas(u: uPlot, props: DataFlowTimelineProps): void {
  const { ctx } = u;
  if (!ctx) return;

  const trackIndex = new Map<string, number>();
  for (let i = 0; i < props.tracks.length; i++) trackIndex.set(props.tracks[i].id, i);

  const systemByName = new Map<string, SystemDefinitionDto>();
  for (const s of props.systems) {
    if (s.name) systemByName.set(s.name, s);
  }

  // Plot rect for clipping bars to the drawing area (don't bleed into axes).
  const left = u.bbox.left;
  const top = u.bbox.top;
  const width = u.bbox.width;
  const height = u.bbox.height;
  ctx.save();
  ctx.beginPath();
  ctx.rect(left, top, width, height);
  ctx.clip();

  // Label setup — set once outside the loop. Font hardcoded (rather than read from CSS) so measurements stay
  // stable across themes; the saturated ACCESS_COLOR fills are dark enough that white reads cleanly on all of them.
  ctx.font = '11px ui-sans-serif, system-ui, -apple-system, sans-serif';
  ctx.textBaseline = 'middle';
  ctx.textAlign = 'left';

  // Density-mode heat-strip pass — runs INSTEAD of the per-bar loop when density cells are present. Each cell
  // fills its segment × row rectangle with amber alpha-modulated by log(touchCount). Caps the alpha at 0.85 so
  // overlapping cells (shouldn't happen — keys are unique) still render distinct shapes.
  const densityCells = props.densityCells ?? [];
  if (densityCells.length > 0) {
    const segByName = new Map<string, PhaseSegment>();
    for (const s of props.phaseSegments) segByName.set(s.name, s);
    let maxCount = 1;
    for (const c of densityCells) if (c.touchCount > maxCount) maxCount = c.touchCount;
    const denom = Math.max(1, Math.log1p(maxCount));
    for (const cell of densityCells) {
      const rowIdx = trackIndex.get(cell.trackId);
      if (rowIdx == null) continue;
      const seg = segByName.get(cell.phaseName);
      if (!seg) continue;
      const xStartPx = u.valToPos(seg.xStart, 'x', true);
      const xEndPx = u.valToPos(seg.xEnd, 'x', true);
      if (xEndPx <= xStartPx) continue;
      const yPx = top + rowIdx * ROW_HEIGHT_PX + ROW_PADDING_PX;
      const h = ROW_HEIGHT_PX - 2 * ROW_PADDING_PX;
      const intensity = Math.log1p(cell.touchCount) / denom;
      ctx.globalAlpha = 0.15 + 0.7 * intensity; // 0.15 floor so even rare cells stay visible
      ctx.fillStyle = '#f59e0b'; // amber-500
      ctx.fillRect(xStartPx, yPx, xEndPx - xStartPx, h);
    }
    ctx.globalAlpha = 1.0;
    ctx.restore();
    return;
  }

  for (const bar of props.bars) {
    const rowIdx = trackIndex.get(bar.trackId);
    if (rowIdx == null) continue;

    // Compute screen x range for the bar — Bar.xStart/xEnd carry the sub-tick position when SystemTickSummary
    // timing is available, so each system within a tick lands at its real time slot. Falls back to half-tick
    // wide centered on tickNumber when timing data is absent.
    const xStartPx = u.valToPos(bar.xStart, 'x', true);
    const xEndPx = u.valToPos(bar.xEnd, 'x', true);
    const w = Math.max(2, xEndPx - xStartPx); // floor at 2px so single-tick bars stay clickable
    const yPx = top + rowIdx * ROW_HEIGHT_PX + ROW_PADDING_PX;
    const h = ROW_HEIGHT_PX - 2 * ROW_PADDING_PX;

    // Color resolution: bar inherits the system's access kind on the row's component when known. For domain rows
    // (L0 / L1 — no specific component), aggregate across the system's full access set so the user still gets a
    // meaningful color (red = system writes any component, green = reads, etc.) instead of an opaque slate fill.
    const track = props.tracks[rowIdx];
    const componentName = track.componentName ?? null;
    const sys = systemByName.get(bar.systemName);
    let color = ACCESS_COLOR.none;
    if (componentName && sys) {
      color = ACCESS_COLOR[accessKindFor(sys, componentName)];
    } else if (sys) {
      const domain = track.kind === 'queue-domain' || track.kind === 'queue'
        ? 'queues' as const
        : track.kind === 'resource-domain' || track.kind === 'resource'
          ? 'resources' as const
          : 'components' as const;
      color = ACCESS_COLOR[aggregateAccessKindForDomain(sys, domain)];
    }

    // Hover-isolate dimming + filter-chip dimming. Skipped-system filter is independent and stacks: a hovered
    // non-isolate bar that is ALSO skipped just dims a bit more. We compose by multiplying both alphas.
    const isolate = props.hoverIsolate;
    const matches = !isolate || (isolate.systemName === bar.systemName && isolate.tickNumber === bar.tickNumber);
    const skipped = props.skippedKeys?.has(`${bar.systemName}|${bar.tickNumber}`) ?? false;
    const isolateAlpha = matches ? 1.0 : 0.25;
    const skipAlpha = skipped ? 0.4 : 1.0;
    ctx.globalAlpha = isolateAlpha * skipAlpha;

    ctx.fillStyle = color;
    ctx.fillRect(xStartPx, yPx, w, h);

    // Selection outline: stronger stroke on bars that match the cross-panel selected system.
    if (props.selectedSystem && props.selectedSystem === bar.systemName) {
      ctx.lineWidth = 1.5;
      ctx.strokeStyle = '#facc15'; // amber-400 — stands out against any access color
      ctx.strokeRect(xStartPx + 0.5, yPx + 0.5, w - 1, h - 1);
    }

    // Inline system-name label when the bar is wide enough. Full name when it fits with padding; otherwise try
    // a truncated form with an ellipsis if at least a couple visible chars remain. Hidden entirely on bars too
    // narrow for either to be readable. Measured once per bar — bar count is bounded by visible-tracks × ticks-in-range.
    const LABEL_PAD_PX = 4;
    const available = w - 2 * LABEL_PAD_PX;
    if (available >= 18) {
      const name = bar.systemName ?? '';
      const fullW = ctx.measureText(name).width;
      let label: string | null = null;
      if (fullW <= available) {
        label = name;
      } else {
        // Truncate from the right, append ellipsis. Cheap linear scan — names are short (typically <30 chars).
        const ELLIPSIS = '…';
        const ellipsisW = ctx.measureText(ELLIPSIS).width;
        let lo = 1;
        let hi = name.length - 1;
        let best = 0;
        while (lo <= hi) {
          const mid = (lo + hi) >> 1;
          const trial = ctx.measureText(name.slice(0, mid)).width + ellipsisW;
          if (trial <= available) { best = mid; lo = mid + 1; } else { hi = mid - 1; }
        }
        if (best >= 2) label = name.slice(0, best) + ELLIPSIS;
      }
      if (label) {
        ctx.globalAlpha = matches ? 1.0 : 0.25;
        ctx.fillStyle = '#ffffff';
        ctx.fillText(label, xStartPx + LABEL_PAD_PX, yPx + h / 2);
      }
    }
  }

  // Hatched wait strips. Two distinct kinds (spec §6.1 + goal #4):
  //   1. Per-row gap waits — between consecutive bars on the same row, the row's data is idle. Rendered as
  //      a thin horizontal hatched strip at row height. Captures inter-system serialization on a track.
  //   2. Phase-fence waits — between the last visible bar in a phase and the phase fence, the touched rows
  //      are idle waiting for the next phase to begin. Rendered as a wider hatched block spanning all
  //      touched rows. Visually distinct from per-row gaps because it's tall (multi-row), not thin.
  const segments = props.phaseSegments;

  // Hatch pattern — built once, reused across both passes. Diagonal lines with corner pieces so adjacent
  // tiles seamlessly continue the stripes. 8 px tile keeps the pattern visible at any zoom level.
  const HATCH_SIZE = 8;
  const off = document.createElement('canvas');
  off.width = HATCH_SIZE;
  off.height = HATCH_SIZE;
  const octx = off.getContext('2d');
  if (octx) {
    octx.strokeStyle = 'rgba(148, 163, 184, 0.55)'; // slate-400 @ 55% — visible but not loud
    octx.lineWidth = 1.3;
    octx.beginPath();
    octx.moveTo(-1, HATCH_SIZE + 1);
    octx.lineTo(HATCH_SIZE + 1, -1);
    octx.stroke();
    octx.beginPath();
    octx.moveTo(-1, 1);
    octx.lineTo(1, -1);
    octx.stroke();
    octx.beginPath();
    octx.moveTo(HATCH_SIZE - 1, HATCH_SIZE + 1);
    octx.lineTo(HATCH_SIZE + 1, HATCH_SIZE - 1);
    octx.stroke();
  }
  const pattern = octx ? ctx.createPattern(off, 'repeat') : null;

  // Pass 1 — per-row gap waits. Group bars by row, sort by xStart, render hatched rectangles in any gap.
  // Crossing a phase fence is fine: a gap that spans a fence is genuinely "this row went idle through the
  // phase boundary", which is a real wait the user wants to see.
  if (pattern) {
    ctx.fillStyle = pattern;
    const barsByRow = new Map<number, Bar[]>();
    for (const bar of props.bars) {
      const rowIdx = trackIndex.get(bar.trackId);
      if (rowIdx == null) continue;
      let arr = barsByRow.get(rowIdx);
      if (!arr) { arr = []; barsByRow.set(rowIdx, arr); }
      arr.push(bar);
    }
    for (const [rowIdx, arr] of barsByRow) {
      arr.sort((a, b) => a.xStart - b.xStart);
      const yPx = top + rowIdx * ROW_HEIGHT_PX + ROW_PADDING_PX;
      const hPx = ROW_HEIGHT_PX - 2 * ROW_PADDING_PX;
      for (let i = 0; i + 1 < arr.length; i++) {
        const a = arr[i];
        const b = arr[i + 1];
        if (b.xStart <= a.xEnd) continue; // bars overlap — no gap
        const xStartPx = u.valToPos(a.xEnd, 'x', true);
        const xEndPx = u.valToPos(b.xStart, 'x', true);
        if (xEndPx > xStartPx + 0.5) {
          ctx.fillRect(xStartPx, yPx, xEndPx - xStartPx, hPx);
        }
      }
    }
  }

  // Pass 2 — phase-fence waits. Per phase: if the last visible bar ended before the phase segment end,
  // render a hatched block from there to the segment end, spanning all rows the phase touched.
  if (pattern && segments.length > 0) {
    const phaseRightmostX01 = new Map<string, number>();
    const phaseTouchedRows = new Map<string, Set<number>>();
    for (const bar of props.bars) {
      const rowIdx = trackIndex.get(bar.trackId);
      if (rowIdx == null) continue;
      const phase = bar.phaseName;
      if (!phase) continue;
      const cur = phaseRightmostX01.get(phase) ?? 0;
      if (bar.xEnd > cur) phaseRightmostX01.set(phase, bar.xEnd);
      let rows = phaseTouchedRows.get(phase);
      if (!rows) {
        rows = new Set();
        phaseTouchedRows.set(phase, rows);
      }
      rows.add(rowIdx);
    }
    ctx.fillStyle = pattern;
    for (const seg of segments) {
      const lastX01 = phaseRightmostX01.get(seg.name);
      if (lastX01 == null || lastX01 >= seg.xEnd) continue;
      const rows = phaseTouchedRows.get(seg.name);
      if (!rows || rows.size === 0) continue;
      let minRow = Infinity;
      let maxRow = -Infinity;
      for (const r of rows) {
        if (r < minRow) minRow = r;
        if (r > maxRow) maxRow = r;
      }
      const xStart = u.valToPos(lastX01, 'x', true);
      const xEndPx = u.valToPos(seg.xEnd, 'x', true);
      const yStart = top + minRow * ROW_HEIGHT_PX + ROW_PADDING_PX;
      const yEnd = top + (maxRow + 1) * ROW_HEIGHT_PX - ROW_PADDING_PX;
      if (xEndPx > xStart && yEnd > yStart) {
        ctx.fillRect(xStart, yStart, xEndPx - xStart, yEnd - yStart);
      }
    }
  }

  ctx.globalAlpha = 1.0;
  ctx.restore();
}

/**
 * Draw vertical phase-fence dividers across the chart. Runs in the `drawAxes` hook so they render on top
 * of the X-axis baseline. Phase segments are pre-normalized in [0, 1]; uPlot's `valToPos` maps them through
 * the current x scale (which is also [0, 1], possibly narrowed by wheel-zoom).
 */
function drawPhaseFences(u: uPlot, props: DataFlowTimelineProps): void {
  const { ctx } = u;
  const segments = props.phaseSegments;
  if (!ctx || segments.length <= 1) return;

  const top = u.bbox.top;
  const height = u.bbox.height;

  ctx.save();
  ctx.strokeStyle = '#64748b'; // slate-500
  ctx.lineWidth = 1;
  ctx.setLineDash([4, 4]);

  for (let i = 0; i < segments.length - 1; i++) {
    const xPx = u.valToPos(segments[i].xEnd, 'x', true);
    ctx.beginPath();
    ctx.moveTo(xPx, top);
    ctx.lineTo(xPx, top + height);
    ctx.stroke();
  }

  ctx.restore();
}

/**
 * Hit-test: given a mouse event, return the bar under the cursor (if any). Used by the click + hover handlers.
 * Reverse-iterates the bars so the topmost bar wins when multiple overlap on the same row at the same tick.
 *
 * Hit-tests in **pixel space** against the same coordinates the drawer paints — not in tick-space — so the
 * clickable area exactly matches what's rendered regardless of zoom. The drawer floors bar width at 2 px; we
 * mirror that here, plus a small 2 px slop on each side for usability when bars get sub-pixel narrow.
 * Y check also accounts for `ROW_PADDING_PX` so cursor in the row's padding zone (between bars vertically)
 * doesn't false-match a bar on that row.
 */
function barAtPoint(u: uPlot, e: MouseEvent, props: DataFlowTimelineProps): Bar | null {
  // u.over is the chart drawing area (matches u.bbox), NOT the whole canvas. Cursor x/y here are over-local.
  // To compare against bar coordinates we ask uPlot for over-relative pixels via canvasPixels=false below.
  const rect = u.over.getBoundingClientRect();
  const x = e.clientX - rect.left;
  const y = e.clientY - rect.top;

  if (x < 0 || y < 0 || x > rect.width || y > rect.height) return null;

  // Y-axis: cursor y is already over-local (= bbox-local). Reject the padding zone above/below each bar.
  const rowIdx = Math.floor(y / ROW_HEIGHT_PX);
  if (rowIdx < 0 || rowIdx >= props.tracks.length) return null;
  const yInRow = y - rowIdx * ROW_HEIGHT_PX;
  if (yInRow < ROW_PADDING_PX || yInRow > ROW_HEIGHT_PX - ROW_PADDING_PX) return null;

  // X-axis: hit-test in pixel space against the bar's painted extent. Use canvasPixels=false so valToPos
  // returns over-relative pixels (same coordinate system as cursor x). 2 px floor matches the drawer; 2 px
  // slop on each side keeps narrow bars clickable.
  const targetTrackId = props.tracks[rowIdx].id;
  const HIT_SLOP_PX = 2;
  for (let i = props.bars.length - 1; i >= 0; i--) {
    const bar = props.bars[i];
    if (bar.trackId !== targetTrackId) continue;
    const xStartPx = u.valToPos(bar.xStart, 'x', false);
    const xEndPx = u.valToPos(bar.xEnd, 'x', false);
    const w = Math.max(2, xEndPx - xStartPx);
    if (x >= xStartPx - HIT_SLOP_PX && x <= xStartPx + w + HIT_SLOP_PX) return bar;
  }
  return null;
}
