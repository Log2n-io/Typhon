import { useEffect, useRef } from 'react';
import uPlot, { type AlignedData, type Options as UPlotOptions } from 'uplot';
import 'uplot/dist/uPlot.min.css';
import type { SystemDefinitionDto } from '@/api/generated/model/systemDefinitionDto';
import { type AccessKind, accessKindFor, type Bar } from './barBuilding';
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
  /** Bars to draw. Each one renders on the row matching its `trackId`; a bar with no matching track is dropped silently. */
  bars: readonly Bar[];
  /** Inclusive tick range covering the X axis. */
  tickRange: { from: number; to: number } | null;
  /** Phase column boundaries — drawn as vertical fence dividers. Pass `[]` to suppress. */
  phaseSegments: readonly PhaseSegment[];
  /** Topology systems — used by the bar coloring path to look up access kind. */
  systems: readonly SystemDefinitionDto[];
  /** When set, only bars matching this (systemName, tickNumber) render at full opacity. */
  hoverIsolate: { systemName: string; tickNumber: number } | null;
  /** Currently selected system (cross-panel) — gets a stronger outline on bars. */
  selectedSystem: string | null;
  /** Click handler — fires with the system name when a bar is clicked. */
  onBarClick?: (systemName: string) => void;
  /** Hover handler — fires with the (system, tick) pair (or null when unhovered). */
  onBarHover?: (key: { systemName: string; tickNumber: number } | null) => void;
}

const ROW_HEIGHT_PX = 22;
const ROW_PADDING_PX = 2;
const BAR_HALF_WIDTH_TICKS = 0.5;

const ACCESS_COLOR: Record<AccessKind, string> = {
  'write':            '#dc2626', // red
  'side-write':       '#ea580c', // orange
  'reads-fresh':      '#2563eb', // blue
  'reads-snapshot':   '#0891b2', // cyan
  'reads':            '#65a30d', // green
  'additional-reads': '#84cc16', // lighter green
  'none':             '#94a3b8', // slate
};

export default function DataFlowTimeline(props: DataFlowTimelineProps) {
  const containerRef = useRef<HTMLDivElement>(null);
  const plotRef = useRef<uPlot | null>(null);
  // Refs hold the latest props so the uPlot draw hook closure (created once) sees fresh data on every redraw.
  const propsRef = useRef(props);
  propsRef.current = props;

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
      cursor: {
        x: true,
        y: false,
        drag: { x: true, y: false, setScale: true },
      },
      scales: {
        x: { time: false },
        y: { range: () => [computeYExtent(propsRef.current.tracks), 0] },
      },
      axes: [
        { label: 'tick' },
        { show: false }, // y axis labels rendered in custom column to the left of the chart
      ],
      series: [
        { label: 'tick' },
        { label: '_ghost', show: false, points: { show: false } },
      ],
      hooks: { draw: drawBars, drawAxes },
    };

    const tickRange = props.tickRange ?? { from: 0, to: 1 };
    const data: AlignedData = [
      [tickRange.from, tickRange.to],
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
      if (!u || !propsRef.current.onBarHover) return;
      const hit = barAtPoint(u, e, propsRef.current);
      propsRef.current.onBarHover(hit ? { systemName: hit.systemName, tickNumber: hit.tickNumber } : null);
    };
    const onLeave = () => propsRef.current.onBarHover?.(null);
    canvas?.addEventListener('click', onClick);
    canvas?.addEventListener('mousemove', onMove);
    canvas?.addEventListener('mouseleave', onLeave);

    const ro = new ResizeObserver(() => {
      const el = containerRef.current;
      if (el && plotRef.current) {
        plotRef.current.setSize({ width: el.clientWidth, height: el.clientHeight });
      }
    });
    ro.observe(containerRef.current);

    return () => {
      ro.disconnect();
      canvas?.removeEventListener('click', onClick);
      canvas?.removeEventListener('mousemove', onMove);
      canvas?.removeEventListener('mouseleave', onLeave);
      plotRef.current?.destroy();
      plotRef.current = null;
    };
  }, []);

  // Push tick range into uPlot's x scale on every change.
  useEffect(() => {
    const u = plotRef.current;
    if (!u || !props.tickRange) return;
    const data: AlignedData = [
      [props.tickRange.from, props.tickRange.to],
      [0, 0],
    ];
    u.setData(data);
    u.redraw(true, true);
  }, [props.tickRange?.from, props.tickRange?.to]);

  // Track count → height adjusts. uPlot recalculates Y extent via the closure in scales.y.range above.
  useEffect(() => {
    plotRef.current?.redraw(false, true);
  }, [props.tracks, props.bars, props.phaseSegments, props.hoverIsolate, props.selectedSystem, props.systems]);

  return (
    <div className="flex h-full w-full flex-row overflow-hidden">
      {/* Y-axis row labels: rendered as a separate flex column so HTML accessibility + CSS theming work naturally. */}
      <div
        className="flex shrink-0 select-none flex-col border-r border-border bg-card"
        style={{ width: '180px', overflow: 'hidden' }}
      >
        {props.tracks.map((track) => (
          <div
            key={track.id}
            className="flex items-center truncate px-2 text-xs text-foreground"
            style={{ height: `${ROW_HEIGHT_PX}px`, lineHeight: `${ROW_HEIGHT_PX}px` }}
            title={track.label}
          >
            {track.label}
          </div>
        ))}
      </div>
      <div ref={containerRef} className="relative min-h-0 flex-1" />
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

  for (const bar of props.bars) {
    const rowIdx = trackIndex.get(bar.trackId);
    if (rowIdx == null) continue;

    // Compute screen x range for the bar (centered on tickNumber, half-tick wide each side).
    const xStartPx = u.valToPos(bar.tickNumber - BAR_HALF_WIDTH_TICKS, 'x', true);
    const xEndPx = u.valToPos(bar.tickNumber + BAR_HALF_WIDTH_TICKS, 'x', true);
    const w = Math.max(2, xEndPx - xStartPx); // floor at 2px so single-tick bars stay clickable
    const yPx = top + rowIdx * ROW_HEIGHT_PX + ROW_PADDING_PX;
    const h = ROW_HEIGHT_PX - 2 * ROW_PADDING_PX;

    // Color resolution: bar inherits the system's access kind on the row's component (when known).
    const track = props.tracks[rowIdx];
    const componentName = track.componentName ?? null;
    let color = ACCESS_COLOR.none;
    if (componentName) {
      const sys = systemByName.get(bar.systemName);
      if (sys) color = ACCESS_COLOR[accessKindFor(sys, componentName)];
    } else {
      // Domain rows (no specific component): use a neutral system-row color.
      color = '#475569';
    }

    // Hover-isolate dimming.
    const isolate = props.hoverIsolate;
    const matches = !isolate || (isolate.systemName === bar.systemName && isolate.tickNumber === bar.tickNumber);
    ctx.globalAlpha = matches ? 1.0 : 0.25;

    ctx.fillStyle = color;
    ctx.fillRect(xStartPx, yPx, w, h);

    // Selection outline: stronger stroke on bars that match the cross-panel selected system.
    if (props.selectedSystem && props.selectedSystem === bar.systemName) {
      ctx.lineWidth = 1.5;
      ctx.strokeStyle = '#facc15'; // amber-400 — stands out against any access color
      ctx.strokeRect(xStartPx + 0.5, yPx + 0.5, w - 1, h - 1);
    }
  }

  ctx.globalAlpha = 1.0;
  ctx.restore();
}

/**
 * Draw vertical phase-fence dividers across the chart. Runs after axis labels so the lines render on top of
 * the X-axis baseline. Phase segments come pre-normalized in [0, 1]; we map them through the current x scale
 * to pixels using uPlot's own range.
 */
function drawPhaseFences(u: uPlot, props: DataFlowTimelineProps): void {
  const { ctx } = u;
  const segments = props.phaseSegments;
  if (!ctx || segments.length <= 1) return;

  const left = u.bbox.left;
  const top = u.bbox.top;
  const width = u.bbox.width;
  const height = u.bbox.height;

  ctx.save();
  ctx.strokeStyle = '#64748b'; // slate-500
  ctx.lineWidth = 1;
  ctx.setLineDash([4, 4]);

  // Each interior fence is drawn at the right edge of segment[i], for i in [0, len-2].
  for (let i = 0; i < segments.length - 1; i++) {
    const xPx = left + segments[i].xEnd * width;
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
 */
function barAtPoint(u: uPlot, e: MouseEvent, props: DataFlowTimelineProps): Bar | null {
  const rect = u.over.getBoundingClientRect();
  const x = e.clientX - rect.left;
  const y = e.clientY - rect.top;

  if (x < 0 || y < 0 || x > rect.width || y > rect.height) return null;

  const tickAtCursor = u.posToVal(x, 'x');

  // Find the row under the cursor — `y` is already in canvas-local coordinates.
  const rowIdx = Math.floor((y - u.bbox.top) / ROW_HEIGHT_PX);
  if (rowIdx < 0 || rowIdx >= props.tracks.length) return null;

  // Linear scan within the row — fine for typical bar counts (<10k visible).
  const targetTrackId = props.tracks[rowIdx].id;
  for (let i = props.bars.length - 1; i >= 0; i--) {
    const bar = props.bars[i];
    if (bar.trackId !== targetTrackId) continue;
    const dt = Math.abs(bar.tickNumber - tickAtCursor);
    if (dt <= BAR_HALF_WIDTH_TICKS + 0.25) return bar;  // small grace zone for clickability
  }
  return null;
}
