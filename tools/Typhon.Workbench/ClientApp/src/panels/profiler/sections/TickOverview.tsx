import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  BAR_AREA_BOTTOM_RESERVED,
  BAR_AREA_TOP,
  BAR_LEFT_PAD,
  BAR_WIDTH,
  DRAG_THRESHOLD_PX,
  TIMELINE_HEIGHT,
  buildTickRows,
  computeSelectionIdxRange,
  drawTickOverview,
  hitTestScrollbar,
  hitTestTick,
  isInHelpHitZone,
  type TickRow,
} from '@/libs/profiler/canvas/tickOverview';
import { formatDuration } from '@/libs/profiler/canvas/canvasUtils';
import { getStudioThemeTokens } from '@/libs/profiler/canvas/theme';
import { HelpOverlay } from '@/panels/profiler/components/HelpOverlay';
import { useProfilerSelectionStore } from '@/stores/useProfilerSelectionStore';
import { useProfilerSessionStore } from '@/stores/useProfilerSessionStore';
import { useProfilerViewStore } from '@/stores/useProfilerViewStore';
import { useUiPrefsStore } from '@/stores/useUiPrefsStore';
import { useThemeStore } from '@/stores/useThemeStore';
import type { TimeRange } from '@/libs/profiler/model/uiTypes';

/**
 * Tick-overview strip — the thin bar at the top of the Profiler panel. One bar per tick (height ∝ duration,
 * clamped at P95), orange overlay for the ticks inside the main viewport, drag-to-range-select, wheel to pan,
 * Shift+wheel to grow the selection edge.
 *
 * Reads tick data from `useProfilerSessionStore.metadata.tickSummaries` (trace mode — pre-aggregated by the
 * server's cache builder). Attach mode has no tick summaries yet (live aggregation lands later); the strip
 * renders as an empty placeholder until per-tick durations arrive.
 *
 * All state goes through stores:
 *  - `useProfilerViewStore.viewRange` / `legendsVisible` — read + write
 *  - `useProfilerSelectionStore.setSelected({kind:'tick', ...})` — on single-tick click (Phase 2e wires
 *    DetailPanel; for now the store just records the choice)
 */
const OVERVIEW_HELP_LINES: string[] = [
  'Overview timeline',
  '',
  'What\'s drawn:',
  '  One bar per tick, height ∝ tick duration (clamped at the',
  '    P95 reference — dashed line at top).',
  '  Orange overlay = ticks overlapping the current viewport.',
  '  ◀ / ▶ chevrons mean the selection extends past the visible',
  '    window.',
  '',
  'Bar colour:',
  '  Throttle multiplier (when > 1) wins over the P95 colouring,',
  '  so the more severe diagnostic always shows through.',
  '',
  '  1× (nominal)',
  '    Default tone if duration ≤ P95;',
  '    P95 warning hue if duration > P95.',
  '  2× amber          — engine slowed itself once (Level 3 entry)',
  '  3× orange         — second escalation step within Level 3',
  '  4× red            — third step (typical migration-storm range)',
  '  6× dark red       — engine pinned at MinTickRateHz floor',
  '',
  '  Multiplier rises after 5 consecutive ticks above 1.20× target',
  '  (OverloadDetector escalation). It steps back down only after',
  '  20 consecutive ticks under 0.60× — asymmetric, by design.',
  '',
  'Hover tooltip lines (when present):',
  '  Tick N',
  '  Duration: actual + allocated   (allocated = baseTarget × multiplier)',
  '  Events: total trace records emitted this tick',
  '  Throttled: mult=N (level M)   — only when multiplier > 1',
  '  Pre-tick wait: X ms (Class)   — only when wait > 0',
  '    Class is the metronome\'s intent for that wait:',
  '      CatchUp   — target was already past, no real wait',
  '      Throttled — multiplier > 1; engine waited longer on purpose',
  '      Headroom  — multiplier == 1; normal idle to next 60 Hz tick',
  '  Streak overrun: N / 5         — consecutive ticks > 1.2× target',
  '    Reaches 5 → multiplier escalates one step. Resets on any',
  '    non-overrun tick.',
  '  Streak underrun: N / 20       — consecutive ticks < 0.6× target',
  '    Reaches 20 → multiplier deescalates one step. Resets on any',
  '    overrun tick (a single spike breaks the streak).',
  '    Watch this one — if it climbs to 18-19 and resets, your',
  '    workload has periodic spikes preventing deescalation.',
  '',
  'See also: the Overload strip below auto-appears on the first',
  'overrun and surfaces the per-tick ratio + multiplier history',
  'plus the same streak counters in its tooltip.',
  '',
  'Key + Mouse:',
  '',
  '  Left click',
  '    Click a single tick bar, no drag.',
  '    → Viewport selection jumps to that single tick.',
  '',
  '  Left click + drag',
  '    → On release, viewport selection becomes that range.',
  '',
  '  Middle click + drag',
  '    → Overview window scrolls left/right.',
  '    → Viewport selection is NOT changed.',
  '',
  '  Wheel (no modifier)',
  '    Move the viewport selection by ±1 tick (translates the',
  '    whole range — multi-tick selections keep their size).',
  '',
  '  Shift + Wheel',
  '    Step the viewport-selection right edge by ±1 tick',
  '    (resizes the range; left edge stays put).',
  '',
  '  Ctrl + Wheel',
  '    Pan the overview window fast (≈25% per notch).',
];

interface Props {
  /** True when the active session is Attach-mode; toggles live-follow behavior. */
  isLive?: boolean;
}

export default function TickOverview({ isLive = false }: Props) {
  const metadata = useProfilerSessionStore((s) => s.metadata);
  const liveFollowActive = useProfilerSessionStore((s) => s.liveFollowActive);
  const setLiveFollowActive = useProfilerSessionStore((s) => s.setLiveFollowActive);
  const viewRange = useProfilerViewStore((s) => s.viewRange);
  const setViewRange = useProfilerViewStore((s) => s.setViewRange);
  const legendsVisible = useUiPrefsStore((s) => s.legendsVisible);
  // Range-drag in TickOverview clears any stale span/chunk/marker click selection so the
  // right-pane's range-stats fallback ("Selection") takes over instead of the click-detail card.
  // Without this, an old TimeArea click sticks indefinitely and blocks the range stats from showing.
  const clearProfilerSelection = useProfilerSelectionStore((s) => s.clear);

  // In live mode, any explicit user interaction (wheel pan, drag-select, overview pan, ...)
  // implicitly turns off live-follow — otherwise the next tick batch (≤100 ms later) would snap
  // both the viewport AND the overview's scroll window back to the live tail and erase the user's
  // pan. Mirrors the standard "scroll = pause follow" UX in DevTools-style live profilers. The user
  // re-enables follow via the header toggle.
  const pauseLiveFollow = useCallback(() => {
    if (isLive) setLiveFollowActive(false);
  }, [isLive, setLiveFollowActive]);

  const canvasRef = useRef<HTMLCanvasElement>(null);
  const wheelCleanupRef = useRef<(() => void) | null>(null);
  const containerRef = useRef<HTMLDivElement>(null);

  // Callback ref bound to <canvas ref={setCanvasNode}>. React invokes it with the canvas DOM node on
  // mount and with `null` on unmount — synchronous, not batched into a useEffect — so the wheel listener
  // is guaranteed to be attached the moment the canvas appears in the DOM. handleWheelRef.current is
  // re-assigned every render below; the listener reads through the ref so it always sees the latest
  // closure (with current tickRows etc.).
  const setCanvasNode = useCallback((node: HTMLCanvasElement | null) => {
    if (wheelCleanupRef.current) {
      wheelCleanupRef.current();
      wheelCleanupRef.current = null;
    }
    canvasRef.current = node;
    if (node) {
      const listener = (e: WheelEvent) => handleWheelRef.current(e);
      node.addEventListener('wheel', listener, { passive: false });
      wheelCleanupRef.current = () => node.removeEventListener('wheel', listener);
    }
  }, []);

  // #289 — both trace and live modes derive from `metadata.tickSummaries`. In live mode the array grows over time
  // via SSE deltas (server-side IncrementalCacheBuilder finalizes ticks → store appends). The `buildTickRows`
  // helper handles the float-drift boundary clamp; see its doc for why.
  const tickRows: TickRow[] = useMemo(() => buildTickRows(metadata?.tickSummaries), [metadata?.tickSummaries]);

  const p95 = Number(metadata?.globalMetrics?.p95TickDurationUs ?? 0);

  // Scroll window — which ticks are visible in the overview (pan state). Separate from viewRange.
  const scrollRangeRef = useRef({ startIdx: 0, endIdx: tickRows.length });
  const hoverRef = useRef<{ tickIdx: number; x: number; y: number } | null>(null);
  const dragRef = useRef<
    | { mode: 'select'; startClientX: number; startTickIdx: number; currentTickIdx: number; moved: boolean }
    | { mode: 'pan'; startClientX: number; startStartIdx: number; moved: boolean }
    | { mode: 'scrollbar'; startClientX: number; startStartIdx: number; thumbW: number; moved: boolean }
    | null
  >(null);
  const [scrollbarHovered, setScrollbarHovered] = useState(false);
  const scrollbarHoveredRef = useRef(false);
  scrollbarHoveredRef.current = scrollbarHovered;
  const canvasWidthRef = useRef(0);
  const selectionIdxRef = useRef<{ first: number; last: number }>({ first: -1, last: -1 });
  const rafRef = useRef(0);

  // Help tooltip is a DOM overlay so it can overflow the canvas's 80px height.
  const [helpTooltipPos, setHelpTooltipPos] = useState<{ clientX: number; clientY: number } | null>(null);
  const helpTooltipPosRef = useRef(helpTooltipPos);
  helpTooltipPosRef.current = helpTooltipPos;

  // Hovered-tick tooltip — DOM overlay rendered BELOW the strip (clientY = canvas.bottom) so the
  // tooltip never covers adjacent bars the user might want to read while hovering. HelpOverlay
  // anchors `top: clientY + 14` so passing the canvas bottom puts the tooltip 14 px under the strip.
  const [tickTooltipState, setTickTooltipState] = useState<
    { lines: readonly string[]; clientX: number; clientY: number } | null
  >(null);

  const clampStart = useCallback((startIdx: number, visibleCount: number) => {
    return Math.max(0, Math.min(tickRows.length - visibleCount, startIdx));
  }, [tickRows]);

  // Initial + on-tickRows-change scroll-window setup.
  // Live mode: snap to tail on first paint and whenever live-follow is active. When the user has
  // paused follow (by panning, drag-selecting, etc.) we preserve their scroll offset and only
  // re-clamp it against the new tickRows length — otherwise the 100 ms tick cadence would erase
  // every pan within one frame.
  useEffect(() => {
    if (tickRows.length === 0) {
      scrollRangeRef.current = { startIdx: 0, endIdx: 0 };
      return;
    }
    if (isLive) {
      // Cap visibleCount by what fits at BAR_WIDTH — same rule as trace mode below. The previous
      // hardcoded 200 was unrelated to canvas width and caused inconsistent behavior across modes.
      const w = canvasWidthRef.current;
      const maxVisible = w > 0 ? Math.max(1, Math.floor((w - BAR_LEFT_PAD) / BAR_WIDTH)) : tickRows.length;
      const sr = scrollRangeRef.current;
      const visibleCount = sr.endIdx - sr.startIdx;
      if (visibleCount <= 0 || liveFollowActive) {
        // Auto-scroll ON: snap window to the live tail.
        const endIdx = tickRows.length;
        const desiredVisible = Math.min(maxVisible, tickRows.length);
        const startIdx = Math.max(0, endIdx - desiredVisible);
        scrollRangeRef.current = { startIdx, endIdx };
      } else {
        // Auto-scroll OFF: keep user's pan anchor (sr.startIdx), but let visibleCount grow up to the
        // cap as new ticks accumulate — otherwise pausing follow at 50 ticks freezes the visible window
        // at 50 forever even when thousands of ticks land later.
        const desiredVisible = Math.min(maxVisible, tickRows.length);
        const startIdx = Math.max(0, Math.min(tickRows.length - desiredVisible, sr.startIdx));
        scrollRangeRef.current = { startIdx, endIdx: startIdx + desiredVisible };
      }
    } else {
      const w = canvasWidthRef.current;
      const visible = w > 0 ? Math.max(1, Math.min(tickRows.length, Math.floor((w - BAR_LEFT_PAD) / BAR_WIDTH))) : tickRows.length;
      scrollRangeRef.current = { startIdx: 0, endIdx: Math.min(visible, tickRows.length) };
    }
  }, [tickRows, isLive, liveFollowActive]);

  const render = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;

    // Mirror setupCanvas's measurement protocol: clear the pinned inline width before reading
    // getBoundingClientRect, otherwise we read back last frame's pinned size after a panel resize
    // — the self-correction below would clamp maxVisible to the OLD width while setupCanvas (called
    // inside drawTickOverview just below) measures the NEW width and pins the canvas bigger, so
    // bars cover only the old width's worth of pixels and the right half of the canvas stays empty.
    canvas.style.width = '';
    canvas.style.height = '';
    const rect = canvas.getBoundingClientRect();
    canvasWidthRef.current = rect.width;

    // Width-aware self-correction — init effect runs before first layout, so scrollWindow may be larger than the
    // canvas can show. Clamp down to what fits at BAR_WIDTH.
    if (!isLive && tickRows.length > 0 && rect.width > 0) {
      const maxVisible = Math.max(1, Math.min(tickRows.length, Math.floor((rect.width - BAR_LEFT_PAD) / BAR_WIDTH)));
      const sr0 = scrollRangeRef.current;
      const currentVisible = sr0.endIdx - sr0.startIdx;
      if (currentVisible !== maxVisible || sr0.endIdx > tickRows.length) {
        const newStart = Math.max(0, Math.min(tickRows.length - maxVisible, sr0.startIdx));
        scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + maxVisible };
      }
    }

    drawTickOverview(canvas, {
      ticks: tickRows,
      viewRange,
      scrollWindow: scrollRangeRef.current,
      selection: selectionIdxRef.current,
      dragPreview: dragRef.current?.mode === 'select'
        ? { startIdx: dragRef.current.startTickIdx, currentIdx: dragRef.current.currentTickIdx, moved: dragRef.current.moved }
        : null,
      hover: hoverRef.current,
      p95TickDurationUs: p95,
      legendsVisible,
      helpHovered: helpTooltipPosRef.current !== null,
      scrollbarHovered: scrollbarHoveredRef.current || dragRef.current?.mode === 'scrollbar',
    }, getStudioThemeTokens());
  }, [tickRows, viewRange, p95, legendsVisible, isLive]);

  const scheduleRender = useCallback(() => {
    cancelAnimationFrame(rafRef.current);
    rafRef.current = requestAnimationFrame(() => render());
  }, [render]);

  // Repaint on relevant state changes + resize.
  useEffect(() => {
    scheduleRender();
    const obs = new ResizeObserver(() => scheduleRender());
    if (containerRef.current) obs.observe(containerRef.current);
    return () => { obs.disconnect(); cancelAnimationFrame(rafRef.current); };
  }, [scheduleRender]);

  // Theme toggle (`Alt+Shift+T`) doesn't touch any of the draw-loop deps, so the canvas wouldn't repaint on
  // its own. Subscribe to the theme store and force a redraw — `getStudioThemeTokens()` at the top of each
  // draw then picks up the new CSS variable values.
  const theme = useThemeStore((s) => s.theme);
  useEffect(() => {
    scheduleRender();
  }, [theme, scheduleRender]);

  // When viewRange or tickRows change, recompute the selection-idx range + auto-scroll to keep it visible.
  // Sticky: when the viewport is in a gap between ticks (computeSelectionIdxRange returns {-1,-1}),
  // keep the last valid selection so the yellow overlay stays visible as a navigation anchor while
  // panning through empty time.
  useEffect(() => {
    const newSel = computeSelectionIdxRange(tickRows, viewRange);
    if (newSel.first >= 0) selectionIdxRef.current = newSel;
    const sel = selectionIdxRef.current;
    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;
    if (visibleCount <= 0 || sel.first < 0) {
      scheduleRender();
      return;
    }
    if (sel.first >= sr.startIdx && sel.last < sr.endIdx) {
      scheduleRender();
      return;
    }
    const selMid = Math.floor((sel.first + sel.last) / 2);
    const newStart = clampStart(selMid - Math.floor(visibleCount / 2), visibleCount);
    scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
    scheduleRender();
  }, [viewRange, tickRows, clampStart, scheduleRender]);

  const getCanvasLocal = useCallback((e: { clientX: number; clientY: number }): { mx: number; my: number } | null => {
    const canvas = canvasRef.current;
    if (!canvas) return null;
    const rect = canvas.getBoundingClientRect();
    return { mx: e.clientX - rect.left, my: e.clientY - rect.top };
  }, []);

  const hitTest = useCallback((clientX: number): number => {
    const canvas = canvasRef.current;
    if (!canvas) return -1;
    const rect = canvas.getBoundingClientRect();
    return hitTestTick(clientX - rect.left, rect.width, scrollRangeRef.current);
  }, []);

  const panBy = useCallback((deltaTicks: number) => {
    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;
    if (visibleCount <= 0) return;
    const newStart = clampStart(sr.startIdx + deltaTicks, visibleCount);
    if (newStart === sr.startIdx) return;
    pauseLiveFollow();
    scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
    scheduleRender();
  }, [clampStart, pauseLiveFollow, scheduleRender]);

  const applyViewRange = useCallback((r: TimeRange) => {
    pauseLiveFollow();
    setViewRange(r);
  }, [pauseLiveFollow, setViewRange]);

  // React attaches wheel handlers as **passive** — `e.preventDefault()` is silently ignored, which lets the
  // browser still fire Ctrl+wheel zoom or page scroll. Attach natively via a `{passive:false}` listener to
  // suppress those defaults. Kept inside a ref so the installer runs once and the latest deps are read live.
  //
  // **Mapping.**
  //   - **plain wheel** — translate the viewport selection by ±1 tick. Multi-tick ranges keep their size:
  //     both edges shift together (clamped at the trace ends — selection size is preserved, never grows
  //     or shrinks at the boundary).
  //   - **Shift + wheel** — step the right edge of the viewport selection by ±1 tick (resize). Left edge
  //     stays put. Wheel up grows the range, wheel down shrinks it.
  //   - **Ctrl + wheel** — pan the overview's visible window fast (25% per notch). The drag-to-pan and
  //     middle-click pan paths cover the slower, finer-grained variants.
  const handleWheelRef = useRef<(e: WheelEvent) => void>(() => {});
  handleWheelRef.current = (e: WheelEvent) => {
    if (tickRows.length === 0) return;
    e.preventDefault();

    // Any wheel interaction in live mode = user took control. Disable auto-scroll FIRST so subsequent
    // tick batches don't keep snapping the view to tail and erasing the change we're about to apply.
    pauseLiveFollow();

    const sr = scrollRangeRef.current;
    const visibleCount = sr.endIdx - sr.startIdx;

    if (e.ctrlKey) {
      // Pan the overview window fast — 25% of visible per notch. Wheel up = pan toward later ticks; wheel down = pan toward earlier.
      if (visibleCount <= 0) return;
      const step = Math.max(1, Math.floor(visibleCount * 0.25));
      panBy(e.deltaY < 0 ? step : -step);
      return;
    }

    // Resolve current selection (or seed at index 0 when nothing is selected yet — the wheel still has to
    // do something useful on a fresh attach session).
    let firstIdx = selectionIdxRef.current.first;
    let lastIdx = selectionIdxRef.current.last;
    if (firstIdx < 0) {
      firstIdx = 0;
      lastIdx = 0;
    }

    if (e.shiftKey) {
      // Resize: step the right edge only, by ±1 tick. Wheel up grows; wheel down shrinks (but never below the left edge).
      if (e.deltaY < 0) lastIdx = Math.min(tickRows.length - 1, lastIdx + 1);
      else lastIdx = Math.max(firstIdx, lastIdx - 1);
      applyViewRange({ startUs: tickRows[firstIdx].startUs, endUs: tickRows[lastIdx].endUs });
      return;
    }

    // Plain wheel: translate the whole range by ±1 tick. Wheel up moves toward later ticks; wheel down moves
    // earlier. Range size is preserved at boundaries — when one edge would overshoot, the entire range stops
    // at the boundary instead of clipping the size.
    //
    // **Tick-indexed.** Operations here are pure idx arithmetic; the only conversion to/from microseconds is at
    // the apply boundary via the `tickRows[i].startUs / endUs` table. Doing arithmetic on µs (e.g. `newStart +
    // duration`) reintroduces the wire-format float-drift the boundary clamp in the tickRows useMemo just
    // eliminated, with the same "selection bleeds into the next tick" symptom.
    const direction = e.deltaY < 0 ? 1 : -1;
    let newFirst = firstIdx + direction;
    let newLast = lastIdx + direction;
    if (newFirst < 0) {
      const overshoot = -newFirst;
      newFirst = 0;
      newLast = Math.min(tickRows.length - 1, newLast + overshoot);
    }
    if (newLast >= tickRows.length) {
      const overshoot = newLast - (tickRows.length - 1);
      newLast = tickRows.length - 1;
      newFirst = Math.max(0, newFirst - overshoot);
    }
    applyViewRange({ startUs: tickRows[newFirst].startUs, endUs: tickRows[newLast].endUs });
  };

  // Wheel listener is attached via a callback ref on the canvas element (see `setCanvasNode` below).
  // Doing this in a useEffect was flaky: in attach (live) mode the canvas isn't in the DOM until the
  // first tick batch arrives, so a `useEffect(() => ..., [])` would early-return on first run and never
  // re-fire. A callback ref runs synchronously the moment React mounts/unmounts the canvas.

  const onPointerDown = useCallback((e: React.PointerEvent<HTMLCanvasElement>) => {
    const local = getCanvasLocal(e);
    if (!local) return;

    const canvas = canvasRef.current;
    const canvasWidth = canvas?.getBoundingClientRect().width ?? 0;
    const canvasHeight = canvas?.getBoundingClientRect().height ?? 0;
    if (isInHelpHitZone(local.mx, local.my, canvasWidth, legendsVisible)) {
      e.preventDefault();
      return;
    }

    if (e.button !== 0 && e.button !== 1) return;

    // Left-click on the scrollbar takes priority over a select-drag — the click is on the bar, not the bars.
    if (e.button === 0)
    {
      const sb = hitTestScrollbar(
        local.mx, local.my, canvasWidth, tickRows.length, scrollRangeRef.current,
        BAR_AREA_TOP, canvasHeight - BAR_AREA_BOTTOM_RESERVED,
      );
      if (sb)
      {
        e.preventDefault();
        // Pause auto-follow as soon as the user grabs the scrollbar — same UX as middle-button pan.
        pauseLiveFollow();
        if (sb.kind === 'track')
        {
          // Click on the track outside the thumb: jump-scroll so the thumb's center lands at the click X.
          const sr = scrollRangeRef.current;
          const visibleCount = sr.endIdx - sr.startIdx;
          const totalTicks = tickRows.length;
          const usable = canvasWidth - sb.thumbW;
          if (usable > 0 && totalTicks > visibleCount)
          {
            const targetThumbX = Math.max(0, Math.min(usable, local.mx - sb.thumbW / 2));
            const startFrac = targetThumbX / usable;
            const newStart = Math.round(startFrac * (totalTicks - visibleCount));
            scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
            scheduleRender();
          }
        }
        // Either kind enters drag mode so the user can continue scrolling smoothly.
        dragRef.current = {
          mode: 'scrollbar',
          startClientX: e.clientX,
          startStartIdx: scrollRangeRef.current.startIdx,
          thumbW: sb.thumbW,
          moved: false,
        };
        try { canvas?.setPointerCapture(e.pointerId); } catch { /* safari private mode */ }
        return;
      }
    }

    const mode: 'select' | 'pan' = e.button === 0 ? 'select' : 'pan';
    if (mode === 'select') {
      const startIdx = hitTest(e.clientX);
      if (startIdx < 0) return;
      e.preventDefault();
      dragRef.current = {
        mode: 'select',
        startClientX: e.clientX,
        startTickIdx: startIdx,
        currentTickIdx: startIdx,
        moved: false,
      };
    } else {
      e.preventDefault();
      dragRef.current = {
        mode: 'pan',
        startClientX: e.clientX,
        startStartIdx: scrollRangeRef.current.startIdx,
        moved: false,
      };
    }
    // Pointer capture keeps the drag live when the cursor leaves the canvas — pointermove/pointerup continue
    // firing on this element until release.
    try { canvas?.setPointerCapture(e.pointerId); } catch { /* safari private mode */ }
  }, [getCanvasLocal, hitTest, legendsVisible, pauseLiveFollow, scheduleRender, tickRows]);

  const onPointerMove = useCallback((e: React.PointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;

    const drag = dragRef.current;
    if (drag) {
      const dx = e.clientX - drag.startClientX;
      // Scrollbar drag is responsive from the first pixel — skip the threshold to avoid feeling sluggish.
      if (drag.mode !== 'scrollbar' && !drag.moved && Math.abs(dx) < DRAG_THRESHOLD_PX) return;
      drag.moved = true;

      if (drag.mode === 'pan') {
        const sr = scrollRangeRef.current;
        const visibleCount = sr.endIdx - sr.startIdx;
        if (visibleCount <= 0) return;
        const barWidth = Math.min(rect.width / visibleCount, 10);
        if (barWidth <= 0) return;
        const deltaIdx = -Math.round(dx / barWidth);
        const newStart = clampStart(drag.startStartIdx + deltaIdx, visibleCount);
        if (newStart !== sr.startIdx) {
          pauseLiveFollow();
          scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
        }
      } else if (drag.mode === 'scrollbar') {
        // Scrollbar drag: translate pointer dx into a scroll-window-startIdx delta. The mapping is the
        // inverse of `computeScrollbarGeometry`'s thumb placement: thumbX = startFrac * (trackW - thumbW).
        const sr = scrollRangeRef.current;
        const visibleCount = sr.endIdx - sr.startIdx;
        const totalTicks = tickRows.length;
        const usable = rect.width - drag.thumbW;
        if (usable > 0 && totalTicks > visibleCount) {
          const fracDelta = dx / usable;
          const tickDelta = Math.round(fracDelta * (totalTicks - visibleCount));
          const newStart = clampStart(drag.startStartIdx + tickDelta, visibleCount);
          if (newStart !== sr.startIdx) {
            scrollRangeRef.current = { startIdx: newStart, endIdx: newStart + visibleCount };
          }
        }
      } else {
        const idx = hitTest(e.clientX);
        if (idx >= 0) drag.currentTickIdx = idx;
      }
      hoverRef.current = null;
      if (helpTooltipPosRef.current !== null) {
        helpTooltipPosRef.current = null;
        setHelpTooltipPos(null);
      }
      if (tickTooltipState !== null) setTickTooltipState(null);
      scheduleRender();
      return;
    }

    if (isInHelpHitZone(mx, my, rect.width, legendsVisible)) {
      hoverRef.current = null;
      if (tickTooltipState !== null) setTickTooltipState(null);
      if (helpTooltipPosRef.current === null) {
        const pos = { clientX: e.clientX, clientY: e.clientY };
        helpTooltipPosRef.current = pos;
        setHelpTooltipPos(pos);
      }
      scheduleRender();
      return;
    }
    if (helpTooltipPosRef.current !== null) {
      helpTooltipPosRef.current = null;
      setHelpTooltipPos(null);
    }

    // Scrollbar hover — brighten the thumb when the pointer is over it (or anywhere on the track). Skips the
    // tick-bar tooltip in that band so we don't fight for the same vertical pixels.
    const onScrollbar = hitTestScrollbar(
      mx, my, rect.width, tickRows.length, scrollRangeRef.current,
      BAR_AREA_TOP, rect.height - BAR_AREA_BOTTOM_RESERVED,
    ) != null;
    if (onScrollbar !== scrollbarHoveredRef.current) {
      setScrollbarHovered(onScrollbar);
    }
    if (onScrollbar) {
      hoverRef.current = null;
      if (tickTooltipState !== null) setTickTooltipState(null);
      scheduleRender();
      return;
    }

    const idx = hitTest(e.clientX);
    const sr = scrollRangeRef.current;
    if (idx >= sr.startIdx && idx < sr.endIdx && idx < tickRows.length) {
      hoverRef.current = { tickIdx: idx, x: mx, y: my };
      const tick = tickRows[idx];
      // Allocated tick budget for THIS tick — base-rate target × multiplier. At multiplier > 1 the
      // engine voluntarily widens the per-tick wall-clock slot (TickRateModulation), so a 60Hz base
      // tick at mult=4 has a 66.67ms allocated period, not 16.67ms. Default to 1× when the
      // multiplier is unknown (old v8/zeroed-v9 caches surface 0 here).
      const baseTickRate = Number(metadata?.header?.baseTickRate ?? 60);
      const effectiveMult = (tick.tickMultiplier && tick.tickMultiplier > 0) ? tick.tickMultiplier : 1;
      const allocatedUs = baseTickRate > 0 ? (1_000_000 / baseTickRate) * effectiveMult : 0;
      const lines: string[] = [
        `Tick ${tick.tickNumber}`,
        `Duration: ${formatDuration(tick.durationUs)} (${formatDuration(allocatedUs)} allocated)`,
        `Events: ${tick.eventCount.toLocaleString()}`,
      ];
      // v9 (#289 follow-up): expose throttle + metronome diagnostics when present.
      const mult = tick.tickMultiplier ?? 0;
      if (mult > 1) {
        const lvl = tick.overloadLevel ?? 0;
        lines.push(`Throttled: mult=${mult} (level ${lvl})`);
      }
      const waitUs = tick.metronomeWaitUs ?? 0;
      if (waitUs > 0) {
        const intent = tick.metronomeIntentClass ?? 0;
        const intentLabel = intent === 0 ? 'CatchUp' : intent === 1 ? 'Throttled' : intent === 2 ? 'Headroom' : `?${intent}`;
        // 65535 µs is the saturation sentinel for the u16 wire field — flag it so the user knows the actual gap was longer.
        const waitLabel = waitUs >= 65535 ? '≥65 ms' : formatDuration(waitUs);
        lines.push(`Pre-tick wait: ${waitLabel} (${intentLabel})`);
      }
      // OverloadDetector streak counters (v11) — the two are mutually exclusive (Update's branches).
      // Show whichever is active so users can watch escalation/deescalation streaks climb and reset.
      const consecOver = tick.consecutiveOverrun ?? 0;
      const consecUnder = tick.consecutiveUnderrun ?? 0;
      if (consecOver > 0) {
        lines.push(`Streak overrun: ${consecOver} / 5  (escalate at 5)`);
      } else if (consecUnder > 0) {
        lines.push(`Streak underrun: ${consecUnder} / 20  (deescalate at 20)`);
      }
      setTickTooltipState({
        lines,
        clientX: e.clientX,
        clientY: rect.bottom,
      });
    } else {
      hoverRef.current = null;
      if (tickTooltipState !== null) setTickTooltipState(null);
    }
    scheduleRender();
  }, [tickRows, hitTest, clampStart, pauseLiveFollow, scheduleRender, legendsVisible, tickTooltipState, metadata?.header?.baseTickRate]);

  const onPointerUp = useCallback((e: React.PointerEvent<HTMLCanvasElement>) => {
    const drag = dragRef.current;
    dragRef.current = null;
    try { canvasRef.current?.releasePointerCapture(e.pointerId); } catch { /* noop */ }
    if (!drag) return;

    if (drag.mode === 'select') {
      if (drag.moved) {
        const a = Math.min(drag.startTickIdx, drag.currentTickIdx);
        const b = Math.max(drag.startTickIdx, drag.currentTickIdx);
        if (a >= 0 && b < tickRows.length) {
          applyViewRange({ startUs: tickRows[a].startUs, endUs: tickRows[b].endUs });
          // Clear any stale element click — the user just declared "show me stats for this range".
          clearProfilerSelection();
        }
      } else {
        const idx = hitTest(e.clientX);
        if (idx >= 0 && idx < tickRows.length) {
          const tick = tickRows[idx];
          applyViewRange({ startUs: tick.startUs, endUs: tick.endUs });
          clearProfilerSelection();
        }
      }
    }
    scheduleRender();
  }, [tickRows, applyViewRange, hitTest, scheduleRender, clearProfilerSelection]);

  const onPointerLeave = useCallback(() => {
    // Only clear hover state on leave — in-flight drags are pointer-captured so pointermove still fires.
    if (dragRef.current) return;
    hoverRef.current = null;
    if (helpTooltipPosRef.current !== null) {
      helpTooltipPosRef.current = null;
      setHelpTooltipPos(null);
    }
    if (tickTooltipState !== null) setTickTooltipState(null);
    if (scrollbarHoveredRef.current) {
      setScrollbarHovered(false);
    }
    scheduleRender();
  }, [scheduleRender, tickTooltipState]);

  // Help-tooltip overlay — portaled so dockview's transformed ancestors and pane separators don't
  // misplace or paint over it. See HelpOverlay.tsx for the rationale.
  const helpOverlay = helpTooltipPos !== null ? (
    <HelpOverlay
      lines={OVERVIEW_HELP_LINES}
      clientX={helpTooltipPos.clientX}
      clientY={helpTooltipPos.clientY}
    />
  ) : null;

  // Hovered-tick tooltip — anchored at `clientY = canvas.bottom` with a 2 px gap so the tooltip
  // sits flush against the strip's bottom edge without overlapping any bars.
  const tickOverlay = tickTooltipState !== null ? (
    <HelpOverlay
      lines={tickTooltipState.lines}
      clientX={tickTooltipState.clientX}
      clientY={tickTooltipState.clientY}
      gap={2}
    />
  ) : null;

  if (tickRows.length === 0) {
    return (
      <div
        ref={containerRef}
        className="flex w-full shrink-0 select-none items-center justify-center border-b border-border bg-card text-[11px] text-muted-foreground"
        style={{ height: `${TIMELINE_HEIGHT}px` }}
      >
        {isLive ? 'Live tick overview — aggregation lands in a later phase.' : 'No tick summaries available.'}
      </div>
    );
  }

  return (
    <div
      ref={containerRef}
      className="w-full shrink-0 select-none border-b border-border"
      style={{ height: `${TIMELINE_HEIGHT}px` }}
    >
      <canvas
        ref={setCanvasNode}
        className="h-full w-full cursor-pointer touch-none"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onPointerLeave={onPointerLeave}
      />
      {helpOverlay}
      {tickOverlay}
    </div>
  );
}
