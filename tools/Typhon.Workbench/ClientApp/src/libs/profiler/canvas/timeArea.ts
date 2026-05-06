import type { ChunkSpan, SpanData, TickData } from '@/libs/profiler/model/traceModel';
import type { TimeRange, TrackLayout, Viewport } from '@/libs/profiler/model/uiTypes';
import type { ProfilerSelection } from '@/stores/useProfilerSelectionStore';
import {
  colorForChunk,
  colorForSpan,
  computeGridStep,
  formatDuration,
  formatRulerLabel,
  setupCanvas,
} from './canvasUtils';
import type { SpanColorMode } from '@/stores/useProfilerViewStore';
import { drawGaugeSummaryStrip, GROUP_RENDERERS, type GaugeData } from './gauges/renderers';
import { GAUGE_TRACK_ID_SET, getGaugeGroupSpec } from './gauges/region';
import type { StudioTheme } from './theme';
import {
  LABEL_ROW_HEIGHT,
  MINI_ROW_HEIGHT,
  MIN_RECT_WIDTH,
  PHASE_TRACK_HEIGHT,
  RULER_HEIGHT,
  SPAN_BAR_HEIGHT,
  SPAN_BAR_MARGIN,
  SPAN_ROW_HEIGHT,
  SUMMARY_STRIP_HEIGHT,
  SUMMARY_STRIP_TOP_PAD,
  TRACK_GAP,
} from './timeAreaLayout';

/**
 * Pure draw for the main time area. Originally ported from the retired
 * `Typhon.Profiler.Server/ClientApp/src/GraphArea.tsx::render()` (~700 LOC).
 *
 * **Every color comes from the `theme` argument — no bare hex in this file.** Identity palettes
 * (SPAN, TIMELINE, PHASE) reach through `theme.spans / theme.timelineBands / theme.phaseColor`;
 * theme-adaptive chrome reaches through `theme.summaryStripBg`, etc. Text drawn on coloured bars
 * (chunk labels, span labels) goes through {@link readableOnBar} which picks black-or-white ink
 * per bar via WCAG relative-luminance — a single theme token can't cover a palette that spans
 * `#363E59` → `#FFFCEE`.
 *
 * Draw order discipline — labels and chrome come *after* bars so bars never paint over them.
 */

// ─── Gutter constants (only used by this file + computeGutterWidth) ─────────────────────────────
const MIN_GUTTER_WIDTH = 80;
const GUTTER_PAD_LEFT = 6;
const GUTTER_PAD_RIGHT = 8;
const GUTTER_CARET_WIDTH = 20;     // "▶ " / "▼ " prefix width measured in 10px monospace
const HELP_ICON_WIDTH = 20;        // reserved when `legendsVisible` — keeps "?" off the longest label (glyph ~10 + right-pad ~7 + slack)
export const HELP_ICON_GLYPH_WIDTH = 10; // painted size of the "?" character
export const HELP_ICON_BG_HEIGHT = 14;   // painted backdrop height around the "?" glyph
export const HELP_ICON_RIGHT_PAD = 7;    // right-edge inset inside the gutter (glyph sits ~7 px from the border)
export const HELP_ICON_HIT_PAD = 4;      // extra px of hit zone around the glyph on all sides
// Tick boundary dashes + tick-number labels share the over-P95 accent color (theme-adapted: magenta-pink in light
// theme, bright cyan in dark theme) so they read as a coherent "tick demarcation" visual band, separate from the
// muted ruler-time labels above.

// Coalescing state pool — sized at 8 depths (spans > depth 8 are vanishingly rare in Typhon). One
// shared pool is safe because `drawTimeArea` is single-threaded per frame and flushes between slots.
const COAL_MAX_DEPTH = 8;
const _coalPool = {
  x1: new Float64Array(COAL_MAX_DEPTH),
  x2: new Float64Array(COAL_MAX_DEPTH),
  count: new Int32Array(COAL_MAX_DEPTH),
  sy: new Float64Array(COAL_MAX_DEPTH),
};

/**
 * Pick a readable label colour for text drawn on top of a coloured bar. Uses WCAG relative
 * luminance of the bar's sRGB colour to decide between the theme's "on light" vs "on dark" ink
 * tones. Cached per hex string — each palette has ≤ 8 distinct bar colours so the cache stays
 * tiny, and spans hash into the same 8 slots so real call sites hit the cache almost always.
 *
 * Resolves the "label invisible on very light palette slot" problem — the dark-mode SPAN palette
 * is all dark enough that `#eee` worked everywhere, but the light-mode palette has slots going up
 * to `#FFFCEE` where white text would vanish. Per-bar contrast is the only correct fix.
 */
const _textOnBarCache = new Map<string, 'light' | 'dark'>();

function readableOnBar(barHex: string, theme: StudioTheme): string {
  let pick = _textOnBarCache.get(barHex);
  if (pick === undefined) {
    pick = relativeLuminance(barHex) > 0.5 ? 'dark' : 'light';
    _textOnBarCache.set(barHex, pick);
  }
  return pick === 'dark' ? theme.textOnLightBar : theme.textOnDarkBar;
}

/** WCAG 2 relative-luminance (Y) of an sRGB hex colour. Input must be `#rrggbb` or `#rgb`. */
function relativeLuminance(hex: string): number {
  let r = 0;
  let g = 0;
  let b = 0;
  if (hex.length === 7) {
    r = parseInt(hex.slice(1, 3), 16) / 255;
    g = parseInt(hex.slice(3, 5), 16) / 255;
    b = parseInt(hex.slice(5, 7), 16) / 255;
  } else if (hex.length === 4) {
    r = parseInt(hex[1] + hex[1], 16) / 255;
    g = parseInt(hex[2] + hex[2], 16) / 255;
    b = parseInt(hex[3] + hex[3], 16) / 255;
  }
  const lin = (c: number): number => (c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

// Diagonal-stripe pattern painted over tick ranges whose chunk data hasn't arrived yet. Gives the
// user a "something's loading here" cue instead of empty or stale canvas during rapid panning.
// Same per-ctx caching strategy as the coalesced pattern below.
const _pendingPatternCache = new WeakMap<CanvasRenderingContext2D, CanvasPattern>();
function getPendingPattern(ctx: CanvasRenderingContext2D): CanvasPattern {
  const cached = _pendingPatternCache.get(ctx);
  if (cached) return cached;
  const tile = document.createElement('canvas');
  tile.width = 12;
  tile.height = 12;
  const tctx = tile.getContext('2d');
  if (!tctx) throw new Error('getPendingPattern: no 2d context');
  tctx.fillStyle = 'rgba(48, 50, 54, 0.55)';
  tctx.fillRect(0, 0, 12, 12);
  tctx.strokeStyle = 'rgba(145, 150, 156, 0.35)';
  tctx.lineWidth = 1.5;
  tctx.beginPath();
  // Two diagonals per tile, wrapping cleanly so the stripes stay continuous across the repeat edge.
  tctx.moveTo(-2, 4);  tctx.lineTo(8, -6);
  tctx.moveTo(-2, 16); tctx.lineTo(16, -2);
  tctx.moveTo(4, 16);  tctx.lineTo(16, 4);
  tctx.stroke();
  const pat = ctx.createPattern(tile, 'repeat');
  if (!pat) throw new Error('getPendingPattern: createPattern failed');
  _pendingPatternCache.set(ctx, pat);
  return pat;
}

// Zig-zag pattern for coalesced span/mini-row blocks. Cached per-ctx because
// `ctx.createPattern` holds a reference to the tile — reusing across contexts causes flicker.
const _coalescedPatternCache = new WeakMap<CanvasRenderingContext2D, CanvasPattern>();
function getCoalescedPattern(ctx: CanvasRenderingContext2D): CanvasPattern {
  const cached = _coalescedPatternCache.get(ctx);
  if (cached) return cached;
  const tile = document.createElement('canvas');
  tile.width = 8;
  tile.height = 8;
  const tctx = tile.getContext('2d');
  if (!tctx) throw new Error('getCoalescedPattern: no 2d context');
  tctx.fillStyle = 'rgba(85, 85, 85, 0.5)';
  tctx.fillRect(0, 0, 8, 8);
  tctx.strokeStyle = 'rgba(140, 140, 140, 0.5)';
  tctx.lineWidth = 1;
  tctx.beginPath();
  tctx.moveTo(0, 0);
  tctx.lineTo(3, 2);
  tctx.lineTo(0, 4);
  tctx.lineTo(3, 6);
  tctx.lineTo(0, 8);
  tctx.stroke();
  const pat = ctx.createPattern(tile, 'repeat');
  if (!pat) throw new Error('getCoalescedPattern: createPattern failed');
  _coalescedPatternCache.set(ctx, pat);
  return pat;
}

/**
 * Measure every track label in the current layout and pick the smallest gutter width that fits
 * the widest one (plus chevron + padding). Floored at {@link MIN_GUTTER_WIDTH}. Legends-visible
 * adds a right-column reservation for the "?" help glyph (glyph itself rendered in 2f).
 */
export function computeGutterWidth(
  ctx: CanvasRenderingContext2D,
  tracks: readonly Pick<TrackLayout, 'label' | 'collapsible'>[],
  legendsVisible: boolean,
): number {
  const prevFont = ctx.font;
  ctx.font = '10px monospace';
  let widest = MIN_GUTTER_WIDTH - GUTTER_PAD_LEFT - GUTTER_PAD_RIGHT;
  for (const t of tracks) {
    const prefix = t.collapsible ? GUTTER_CARET_WIDTH : 0;
    const w = prefix + ctx.measureText(t.label).width;
    if (w > widest) widest = w;
  }
  ctx.font = prevFont;
  const helpReserve = legendsVisible ? HELP_ICON_WIDTH : 0;
  const total = widest + GUTTER_PAD_LEFT + GUTTER_PAD_RIGHT + helpReserve;
  return Math.max(MIN_GUTTER_WIDTH, Math.ceil(total / 2) * 2);
}

// ─── Public entry point ─────────────────────────────────────────────────────────────────────────

export interface TimeAreaInputs {
  visibleTicks: TickData[];
  /** Full tick array (not just visible) — gauges need this for snapshot binary search. */
  ticks: readonly TickData[];
  tracks: readonly TrackLayout[];
  viewRange: TimeRange;
  vp: Viewport;
  gutterWidth: number;
  legendsVisible: boolean;
  selection: ProfilerSelection | null;
  dragSelection: { x1: number; x2: number } | null;
  crosshairX: number;     // -1 = cursor outside canvas
  /** Gauge bundle from the chunk cache. Required when gauge tracks are in the layout. */
  gaugeData: GaugeData;
  /** Track id whose "?" help glyph is currently hovered, or null. Drives the brighten effect. */
  helpHover: string | null;
  /**
   * µs-ranges inside the viewport whose chunk data is not yet loaded — painted with a diagonal-
   * stripe pattern over the full track area so the user sees "loading here" instead of empty or
   * stale canvas. Empty when everything's resident (or the cache isn't ready).
   */
  pendingRangesUs: readonly { startUs: number; endUs: number }[];
  /**
   * How to colour span bars on slot lanes. Sourced from `useProfilerViewStore.spanColorMode`.
   * Threaded through to {@link drawSlotLane} so the renderer's hot loop has a stable lookup.
   */
  spanColorMode: SpanColorMode;
}

/**
 * Paint the full time area into the supplied canvas. No React, no store reads — every input is
 * an argument. Caller (React wrapper) is responsible for calling {@link setupCanvas} first (so
 * HiDPI scaling is applied) and resolving the theme via `getStudioThemeTokens()`.
 */
export function drawTimeArea(
  canvas: HTMLCanvasElement,
  inputs: TimeAreaInputs,
  theme: StudioTheme,
): void {
  const { width, height } = setupCanvas(canvas);
  const ctx = canvas.getContext('2d');
  if (!ctx) return;

  const { tracks, viewRange, vp, gutterWidth, legendsVisible, visibleTicks, ticks, selection, dragSelection, crosshairX, gaugeData, helpHover, pendingRangesUs, spanColorMode } = inputs;
  const contentWidth = width - gutterWidth;

  // Sync vp from viewRange if they've drifted — matches old client's eager-sync block so the first
  // paint after an external viewRange change doesn't flicker. Skip when contentWidth is 0 (initial mount).
  if (contentWidth > 0) {
    const rangeUs = viewRange.endUs - viewRange.startUs;
    if (rangeUs > 0) {
      const expectedScaleX = contentWidth / rangeUs;
      if (Math.abs(vp.offsetX - viewRange.startUs) > 0.5 || Math.abs(vp.scaleX - expectedScaleX) > 0.0001) {
        vp.offsetX = viewRange.startUs;
        vp.scaleX = expectedScaleX;
      }
    }
  }

  const pxOfUs = (us: number): number => gutterWidth + (us - vp.offsetX) * vp.scaleX;
  const visStartUs = vp.offsetX;
  const visEndUs = vp.offsetX + (vp.scaleX > 0 ? contentWidth / vp.scaleX : 0);

  // ─── Background ──────────────────────────────────────────────────────────────────────────────
  ctx.fillStyle = theme.background;
  ctx.fillRect(0, 0, width, height);

  // ─── Left gutter ─────────────────────────────────────────────────────────────────────────────
  ctx.fillStyle = theme.card;
  ctx.fillRect(0, 0, gutterWidth, height);
  ctx.strokeStyle = theme.border;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(gutterWidth - 0.5, 0);
  ctx.lineTo(gutterWidth - 0.5, height);
  ctx.stroke();

  // Labels + chevrons + separators + "?" help glyph (when legendsVisible).
  // Glyph position is fixed at the right edge of the gutter, vertically centred in the label row.
  for (const track of tracks) {
    const ty = track.y - vp.scrollY;
    ctx.fillStyle = theme.mutedForeground;
    ctx.font = '10px monospace';
    ctx.textAlign = 'left';
    if (track.collapsible) {
      const chevron = track.state === 'summary' ? '▶' : track.state === 'double' ? '▼▼' : '▼';
      ctx.fillText(`${chevron} ${track.label}`, GUTTER_PAD_LEFT, ty + 12);
    } else {
      ctx.fillText(track.label, GUTTER_PAD_LEFT, ty + 12);
    }

    // "?" help glyph — every track except 'ruler' gets one when legends are visible. The ruler is
    // skipped because its help would be redundant (the cursor/timestamp tell you what it is).
    if (legendsVisible && track.id !== 'ruler') {
      const isHelpHovered = helpHover !== null && helpHover === track.id;
      const gx = gutterWidth - HELP_ICON_RIGHT_PAD;
      const gy = ty + 12;
      ctx.textAlign = 'right';
      ctx.font = 'bold 11px monospace';
      ctx.fillStyle = theme.tooltipBackground;
      ctx.fillRect(gx - HELP_ICON_GLYPH_WIDTH - 2, gy - 11, HELP_ICON_GLYPH_WIDTH + 6, HELP_ICON_BG_HEIGHT);
      ctx.fillStyle = isHelpHovered ? theme.foreground : theme.mutedForeground;
      ctx.fillText('?', gx, gy);
      ctx.font = '10px monospace';
      ctx.textAlign = 'left';
    }

    // Track separator at bottom edge. `stripInsideLabel` = slot lanes in summary render inside
    // the label-row band, so their advance is LABEL_ROW_HEIGHT+TRACK_GAP; all other summary tracks
    // use the legacy 4-px dark strip below the label.
    const stripInsideLabel = track.id.startsWith('slot-');
    const trackAdvance = track.state === 'summary'
      ? (stripInsideLabel ? LABEL_ROW_HEIGHT + TRACK_GAP : LABEL_ROW_HEIGHT + 4)
      : (track.height + TRACK_GAP);
    const sepY = track.y + trackAdvance - vp.scrollY - 0.5;
    ctx.strokeStyle = theme.border;
    ctx.lineWidth = 0.5;
    ctx.beginPath();
    ctx.moveTo(0, sepY);
    ctx.lineTo(width, sepY);
    ctx.stroke();
  }

  // ─── Content area (clipped) ──────────────────────────────────────────────────────────────────
  ctx.save();
  ctx.beginPath();
  ctx.rect(gutterWidth, 0, contentWidth, height);
  ctx.clip();

  // Pending-chunk overlay — full-height diagonal-stripe pattern for any µs-range whose chunk
  // isn't resident yet. Painted first so track content renders on top once it lands; meanwhile the
  // user sees "loading here" instead of empty canvas.
  if (pendingRangesUs.length > 0) {
    ctx.fillStyle = getPendingPattern(ctx);
    for (const range of pendingRangesUs) {
      const px1 = pxOfUs(range.startUs);
      const px2 = pxOfUs(range.endUs);
      if (px2 <= gutterWidth) continue;
      if (px1 >= width) break; // ranges are sorted by startUs.
      const x = Math.max(px1, gutterWidth);
      const w = Math.max(px2 - x, 1);
      ctx.fillRect(x, 0, w, height);
    }
  }

  for (const track of tracks) {
    const ty = track.y - vp.scrollY;

    if (track.id === 'ruler') {
      drawRuler(ctx, { visStartUs, visEndUs, contentWidth, gutterWidth, width, height, visibleTicks, vp, ty, pxOfUs }, theme);
      continue;
    }

    // Summary mode
    if (track.state === 'summary') {
      if (GAUGE_TRACK_ID_SET.has(track.id)) {
        // Spark-line preview drawn INSIDE the label row's Y band.
        drawGaugeSummaryStrip(ctx, gaugeData, track.id,
          vp,
          { x: gutterWidth, y: ty + SUMMARY_STRIP_TOP_PAD, width: contentWidth, height: SUMMARY_STRIP_HEIGHT },
          gutterWidth, theme);
      } else if (track.id.startsWith('slot-')) {
        const threadSlot = Number.parseInt(track.id.slice(5), 10);
        drawSlotSummary(ctx, threadSlot, visibleTicks, visStartUs, visEndUs, gutterWidth, contentWidth, pxOfUs, ty, width, theme);
      } else if (track.id.startsWith('system-')) {
        const systemIdx = Number.parseInt(track.id.slice(7), 10);
        drawSystemSummary(ctx, systemIdx, visibleTicks, gutterWidth, contentWidth, pxOfUs, ty, width, theme);
      } else {
        ctx.fillStyle = theme.summaryStripBg;
        ctx.fillRect(gutterWidth, ty + LABEL_ROW_HEIGHT, contentWidth, 4);
      }
      continue;
    }

    // Gauge tracks (expanded / double)
    if (GAUGE_TRACK_ID_SET.has(track.id)) {
      const renderer = GROUP_RENDERERS[track.id];
      const spec = getGaugeGroupSpec(track.id);
      if (renderer && spec) {
        renderer({
          ctx, ticks, gaugeData, vp, labelWidth: gutterWidth,
          layout: { x: gutterWidth, y: ty, width: contentWidth, height: track.height },
          spec, legendsVisible, theme,
        });
      }
      continue;
    }

    // Slot lanes (expanded) — pass both `ticks` (for span rendering; needs cross-tick coverage
    // so a long span that started before the viewport still draws) and `visibleTicks` (for the
    // chunk row; chunks never span ticks so the visible-only subset is sufficient + faster).
    if (track.id.startsWith('slot-')) {
      const threadSlot = Number.parseInt(track.id.slice(5), 10);
      drawSlotLane(ctx, track, threadSlot, ticks, visibleTicks, visStartUs, visEndUs, gutterWidth, width, pxOfUs, ty, selection, theme, spanColorMode);
      continue;
    }

    // Per-system lanes (expanded) — chunks grouped by systemIndex, single row, same colours as
    // the slot-lane chunk row.
    if (track.id.startsWith('system-')) {
      const systemIdx = Number.parseInt(track.id.slice(7), 10);
      drawSystemLane(ctx, systemIdx, visibleTicks, gutterWidth, width, pxOfUs, ty, selection, theme, spanColorMode);
      continue;
    }

    // Phases
    if (track.id === 'phases') {
      drawPhases(ctx, visibleTicks, gutterWidth, width, pxOfUs, ty, theme);
      continue;
    }

    // Operation mini-rows — use `ticks` (not visibleTicks) so long-running ops like Checkpoint.Cycle
    // that started in a tick now off-screen but extend INTO the viewport still render. The inner
    // loop cheaply skips ticks whose running-max endUs is before visStartUs.
    if (track.id === 'page-cache' || track.id === 'disk-io' || track.id === 'transactions' || track.id === 'wal' || track.id === 'checkpoint') {
      drawMiniRowsTrack(ctx, track, ticks, visibleTicks, visStartUs, visEndUs, gutterWidth, width, pxOfUs, ty, theme);
      continue;
    }
  }

  // Restore clip before overlays (crosshair + drag selection span full height)
  ctx.restore();

  // ─── GC pause bands ──────────────────────────────────────────────────────────────────────────
  // Faint full-height red tint over every `GcSuspension` window inside the viewport. Renders
  // ACROSS ALL LANES (slot, system, ops, gauges) so a 2 ms pause buried in the middle of a 3 ms
  // tick is instantly visible as a vertical stripe — answers "why was this tick so slow?"
  // without expanding the GC gauge. Low alpha (~13 %) so bars still read clearly through the
  // tint. Drawn AFTER track content (sits on top of bars) but BEFORE crosshair / drag-selection
  // so those interactive overlays still read crisp.
  //
  // Skips the ruler area (Y < RULER_HEIGHT) so timestamp labels stay legible — same convention
  // the crosshair uses. X-clipped to the content area so the gutter labels aren't tinted either.
  if (gaugeData.gcSuspensions.length > 0) {
    ctx.fillStyle = 'rgba(232, 93, 77, 0.13)';  // CACHE_EXCLUSIVE_COLOR @ ~13% alpha
    const bandTop = RULER_HEIGHT;
    const bandHeight = height - RULER_HEIGHT;
    for (const sus of gaugeData.gcSuspensions) {
      const susEndUs = sus.startUs + sus.durationUs;
      if (susEndUs <= visStartUs || sus.startUs >= visEndUs) continue;
      const x1 = Math.max(pxOfUs(sus.startUs), gutterWidth);
      const x2 = Math.min(pxOfUs(susEndUs), width);
      const w = x2 - x1;
      if (w >= 1) ctx.fillRect(x1, bandTop, w, bandHeight);
    }
  }

  // ─── Overlays (crosshair + drag-selection) ───────────────────────────────────────────────────
  if (crosshairX >= gutterWidth) {
    drawCrosshair(ctx, crosshairX, width, height, gutterWidth, vp, visibleTicks, theme);
  }
  if (dragSelection && dragSelection.x2 > dragSelection.x1) {
    drawDragSelection(ctx, dragSelection, width, height, gutterWidth, vp, theme);
  }


}

// ═════════════════════════════════════════════════════════════════════════════════════════════════
// Sub-draws
// ═════════════════════════════════════════════════════════════════════════════════════════════════

interface RulerCtx {
  visStartUs: number;
  visEndUs: number;
  contentWidth: number;
  gutterWidth: number;
  width: number;
  height: number;
  visibleTicks: TickData[];
  vp: Viewport;
  ty: number;
  pxOfUs: (us: number) => number;
}

function drawRuler(ctx: CanvasRenderingContext2D, r: RulerCtx, theme: StudioTheme): void {
  const { visStartUs, visEndUs, contentWidth, gutterWidth, width, height, visibleTicks, vp, ty, pxOfUs } = r;
  const gridStep = computeGridStep(visEndUs - visStartUs, contentWidth, 90);
  const baseUs = visibleTicks[0]?.startUs ?? 0;
  const leftEdgeUs = visStartUs;
  const gridStart = Math.ceil(leftEdgeUs / gridStep) * gridStep;

  // Absolute anchor at the left edge
  ctx.fillStyle = theme.foreground;
  ctx.font = '10px monospace';
  ctx.textAlign = 'left';
  ctx.fillText(formatRulerLabel(leftEdgeUs - baseUs), gutterWidth + 4, ty + 16);

  // Grid lines + offset labels
  for (let t = gridStart; t <= visEndUs; t += gridStep) {
    const x = pxOfUs(t);
    if (x < gutterWidth + 60) continue; // don't crowd the anchor label
    ctx.strokeStyle = theme.gridColor;
    ctx.lineWidth = 0.5;
    ctx.beginPath();
    ctx.moveTo(x, 0);
    ctx.lineTo(x, height);
    ctx.stroke();
    ctx.fillStyle = theme.mutedForeground;
    ctx.font = '10px monospace';
    ctx.textAlign = 'center';
    ctx.fillText(`+${formatRulerLabel(t - leftEdgeUs)}`, Math.round(x), ty + 16);
  }

  // Per-tick adaptive labeling
  const minLabelSpacingPx = 40;
  const niceTickSteps = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 25000, 50000];
  const labeled: TickData[] = [];
  let lastLabeledX = Number.NEGATIVE_INFINITY;
  for (const tick of visibleTicks) {
    const x = pxOfUs(tick.startUs);
    if (x < gutterWidth) continue;
    const isAnchor = lastLabeledX === Number.NEGATIVE_INFINITY;
    if (!isAnchor && x - lastLabeledX < minLabelSpacingPx) continue;
    const pxThisTick = tick.durationUs * vp.scaleX;
    const localNeeded = pxThisTick > 0 ? Math.max(1, Math.ceil(minLabelSpacingPx / pxThisTick)) : 1;
    let localStep = niceTickSteps[niceTickSteps.length - 1];
    for (const s of niceTickSteps) {
      if (s >= localNeeded) { localStep = s; break; }
    }
    if (!isAnchor && tick.tickNumber % localStep !== 0) continue;
    labeled.push(tick);
    lastLabeledX = x;
  }

  // Dashed tick boundaries — shared accent color with the tick-number labels below (theme.overviewP95).
  ctx.setLineDash([3, 3]);
  ctx.strokeStyle = theme.overviewP95;
  ctx.lineWidth = 1;
  for (const tick of labeled) {
    const x = pxOfUs(tick.startUs);
    ctx.beginPath();
    ctx.moveTo(x, RULER_HEIGHT);
    ctx.lineTo(x, height);
    ctx.stroke();
  }
  ctx.setLineDash([]);

  // Tick-number labels — over-P95 accent color, prefixed with `#` so they read as tick numbers (not raw ints).
  ctx.fillStyle = theme.overviewP95;
  ctx.font = '10px monospace';
  ctx.textAlign = 'center';
  for (const tick of labeled) {
    const x = pxOfUs(tick.startUs);
    ctx.fillText(`#${tick.tickNumber}`, Math.round(x), ty + 8);
  }
  // Suppress unused-var lint (width kept in the signature for future pending-chunk overlay).
  void width;
}

function drawSlotSummary(
  ctx: CanvasRenderingContext2D,
  threadSlot: number,
  visibleTicks: TickData[],
  visStartUs: number,
  visEndUs: number,
  gutterWidth: number,
  contentWidth: number,
  pxOfUs: (us: number) => number,
  ty: number,
  width: number,
  theme: StudioTheme,
): void {
  const stripTop = ty + SUMMARY_STRIP_TOP_PAD;
  const stripH = SUMMARY_STRIP_HEIGHT;
  ctx.save();
  ctx.beginPath();
  ctx.rect(gutterWidth, stripTop, contentWidth, stripH);
  ctx.clip();
  ctx.fillStyle = theme.activitySilhouette;
  for (const tick of visibleTicks) {
    for (const chunk of tick.chunks) {
      if (chunk.threadSlot !== threadSlot) continue;
      const x1 = pxOfUs(chunk.startUs);
      const x2 = pxOfUs(chunk.endUs);
      if (x2 < gutterWidth || x1 > width) continue;
      const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
      ctx.fillRect(x1, stripTop, w, stripH);
    }
    const slotSpans = tick.spansByThreadSlot.get(threadSlot);
    if (!slotSpans || slotSpans.length === 0) continue;
    const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
    if (!endMax) continue;
    let lo = 0;
    let hi = slotSpans.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (endMax[mid] < visStartUs) lo = mid + 1; else hi = mid;
    }
    for (let i = lo; i < slotSpans.length; i++) {
      const span = slotSpans[i];
      if (span.startUs > visEndUs) break;
      const x1 = pxOfUs(span.startUs);
      const x2 = pxOfUs(span.endUs);
      if (x2 < gutterWidth) continue;
      const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
      ctx.fillRect(x1, stripTop, w, stripH);
    }
  }
  ctx.restore();
}

function drawSlotLane(
  ctx: CanvasRenderingContext2D,
  track: TrackLayout,
  threadSlot: number,
  ticks: readonly TickData[],
  visibleTicks: TickData[],
  visStartUs: number,
  visEndUs: number,
  gutterWidth: number,
  width: number,
  pxOfUs: (us: number) => number,
  ty: number,
  selection: ProfilerSelection | null,
  theme: StudioTheme,
  spanColorMode: SpanColorMode,
): void {
  const chunkRowHeight = track.chunkRowHeight ?? 0;
  const spanRegionTop = ty + chunkRowHeight;
  const trackBottom = ty + track.height;

  // Chunk bar row
  if (chunkRowHeight > 0) {
    for (const tick of visibleTicks) {
      for (const chunk of tick.chunks) {
        if (chunk.threadSlot !== threadSlot) continue;
        const x1 = pxOfUs(chunk.startUs);
        const x2 = pxOfUs(chunk.endUs);
        if (x2 < gutterWidth || x1 > width) continue;
        const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
        ctx.fillStyle = colorForChunk(chunk, spanColorMode, theme.spans);
        ctx.fillRect(x1, ty + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT);
        if (selection && selection.kind === 'chunk' && isSameChunk(selection.chunk, chunk)) {
          ctx.strokeStyle = theme.selectedOutline;
          ctx.lineWidth = 1.5;
          ctx.strokeRect(x1 + 0.5, ty + SPAN_BAR_MARGIN + 0.5, w - 1, SPAN_BAR_HEIGHT - 1);
        }
        if (x2 - x1 > 10) {
          ctx.save();
          ctx.beginPath();
          ctx.rect(x1, ty + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT);
          ctx.clip();
          ctx.fillStyle = readableOnBar(colorForChunk(chunk, spanColorMode, theme.spans), theme);
          ctx.font = '12px monospace';
          ctx.textAlign = 'left';
          const chunkName = chunk.isParallel ? `${chunk.systemName}[${chunk.chunkIndex}]` : chunk.systemName;
          const label = `${chunkName} (${formatDuration(chunk.endUs - chunk.startUs)})`;
          const textX = Math.max(x1 + 3, gutterWidth + 3);
          ctx.fillText(label, textX, ty + SPAN_BAR_MARGIN + SPAN_BAR_HEIGHT - 2);
          ctx.restore();
        }
      }
    }
  }

  // Span rows with per-depth coalescing. Span-bar label is 12px monospace.
  //
  // Iterate ALL ticks — a span's `tick.startUs` is NOT a bound on its own `startUs`. Long-running
  // ops (Checkpoint.Cycle, etc.) are recorded when they *complete*, so they live in the tick where
  // the completion event was written even though their `startUs` can be many ticks earlier. The
  // per-tick skip `endMax[last] < visStartUs` is still valid (running max of endUs bounds every
  // span's endUs in that tick), but the outer `tick.startUs > visEndUs` break would drop exactly
  // the ticks we need.
  //
  // Two-pass draw: non-visible ticks first (background), then visibleTicks (foreground). This
  // ensures spans native to the current viewport always render on top of cross-tick long-running
  // ops stored in a future tick whose `startUs` falls inside the viewport — without this, a
  // Checkpoint.Cycle bar from tick 200 would overdraw all spans visible at tick 80.
  ctx.font = '12px monospace';
  ctx.textAlign = 'left';

  const drawOneTickSpans = (tick: TickData, nativeOnly: boolean): void => {
    const slotSpans = tick.spansByThreadSlot.get(threadSlot);
    if (!slotSpans || slotSpans.length === 0) return;
    const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
    if (!endMax) return;
    if (endMax[endMax.length - 1] < visStartUs) return;

    let lo = 0;
    let hi = slotSpans.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (endMax[mid] < visStartUs) lo = mid + 1; else hi = mid;
    }

    const { x1: coalX1, x2: coalX2, count: coalCount, sy: coalSy } = _coalPool;
    coalX1.fill(0); coalX2.fill(0); coalCount.fill(0); coalSy.fill(0);
    let prevFill = '';

    const flushDepth = (d: number): void => {
      if (coalCount[d] >= 2) {
        const cw = Math.max(coalX2[d] - coalX1[d], 2);
        ctx.fillStyle = getCoalescedPattern(ctx);
        prevFill = '__pattern__';
        ctx.fillRect(coalX1[d], coalSy[d] + SPAN_BAR_MARGIN, cw, SPAN_BAR_HEIGHT);
        if (cw > 50) {
          ctx.fillStyle = theme.coalescedText;
          ctx.font = '9px monospace';
          ctx.textAlign = 'left';
          ctx.fillText(`${coalCount[d]} spans — zoom in`, coalX1[d] + 3, coalSy[d] + SPAN_BAR_MARGIN + SPAN_BAR_HEIGHT - 2);
          prevFill = theme.coalescedText;
          ctx.font = '12px monospace'; // restore span-label font
        }
      }
      coalCount[d] = 0;
    };

    for (let i = lo; i < slotSpans.length; i++) {
      const span = slotSpans[i];
      if (span.startUs > visEndUs) break;
      // In Pass 1 (non-visible ticks) only native spans are drawn. Cross-tick spans — where the
      // span started before this tick recorded its completion (e.g. Checkpoint.Cycle) — are
      // skipped here because they can produce full-width bars when panning into a gap. They will
      // be drawn correctly in Pass 2 once a tick that contains them becomes visible.
      if (nativeOnly && span.startUs < tick.startUs) continue;
      const x1 = pxOfUs(span.startUs);
      const x2 = pxOfUs(span.endUs);
      if (x2 < gutterWidth) continue;

      // renderDepth (from deriveSlotInfo's greedy packing) guarantees two overlapping bars on
      // this slot land on different rows. Falls back to span.depth when packing hasn't run yet
      // (first paint before the useMemo resolves), and finally to 0.
      const depth = span.renderDepth ?? span.depth ?? 0;
      const d = depth < COAL_MAX_DEPTH ? depth : COAL_MAX_DEPTH - 1;
      const sy = spanRegionTop + depth * SPAN_ROW_HEIGHT;
      if (sy + SPAN_ROW_HEIGHT > trackBottom) continue;

      const actualWidth = x2 - x1;
      const w = actualWidth < MIN_RECT_WIDTH ? MIN_RECT_WIDTH : actualWidth;

      if (actualWidth <= 1) {
        // Extend existing run at this depth?
        if (coalCount[d] > 0 && x1 <= coalX2[d] + 1) {
          coalCount[d]++;
          if (x2 > coalX2[d]) coalX2[d] = x2;
          if (coalX2[d] < coalX1[d] + 1) coalX2[d] = coalX1[d] + 1;
          continue;
        }
        flushDepth(d);
        const c = colorForSpan(span, spanColorMode, theme.spans);
        if (c !== prevFill) { ctx.fillStyle = c; prevFill = c; }
        ctx.fillRect(x1, sy + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT);
        coalX1[d] = x1;
        coalX2[d] = x1 + w;
        coalSy[d] = sy;
        coalCount[d] = 1;
        continue;
      }

      // Wide bar
      flushDepth(d);
      const c = colorForSpan(span, spanColorMode, theme.spans);
      if (c !== prevFill) { ctx.fillStyle = c; prevFill = c; }
      ctx.fillRect(x1, sy + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT);

      if (selection && selection.kind === 'span' && isSameSpan(selection.span, span)) {
        ctx.strokeStyle = theme.selectedOutline;
        ctx.lineWidth = 1.5;
        ctx.strokeRect(x1 + 0.5, sy + SPAN_BAR_MARGIN + 0.5, w - 1, SPAN_BAR_HEIGHT - 1);
      }

      if (actualWidth > 10) {
        ctx.save();
        ctx.beginPath();
        ctx.rect(x1, sy + SPAN_BAR_MARGIN, actualWidth, SPAN_BAR_HEIGHT);
        ctx.clip();
        ctx.fillStyle = readableOnBar(colorForSpan(span, spanColorMode, theme.spans), theme);
        ctx.font = '12px monospace';
        ctx.textAlign = 'left';
        // Clamp the text's X to the visible-left edge so the label stays readable when the bar's
        // left edge is off-screen. The clip rect above still trims anything that runs past the
        // bar's actual right edge, so a long label on a wide bar with most of its body off-screen
        // left now slides in from the gutter and cuts at the bar's right edge naturally.
        //
        // Intentionally NOT `Math.round(...)`: the bar's left edge is at a fractional canvas X
        // (from `pxOfUs`) and its anti-aliased border blends across pixels. Keeping the text's X
        // fractional means the 3 px left-inset visually tracks the bar's AA'd edge in-place, and
        // when the bar is off-screen left the text anchors to the gutter at a stable integer
        // offset anyway — readability wins on both sides.
        const textX = Math.max(x1 + 3, gutterWidth + 3);
        ctx.fillText(`${span.name} (${formatDuration(span.durationUs)})`, textX, sy + SPAN_BAR_MARGIN + SPAN_BAR_HEIGHT - 2);
        ctx.restore();
        prevFill = ''; // clip/restore may invalidate cached fill
      }
    }

    for (let d = 0; d < COAL_MAX_DEPTH; d++) flushDepth(d);
  };

  // Pass 1: non-visible ticks, native spans only (background). Native spans may overflow past
  // their tick's endUs and still overlap the viewport — they draw correctly here. Cross-tick spans
  // are excluded: they start before the tick and can produce full-width bars in a gap.
  const visSpanSet = new Set<TickData>(visibleTicks);
  for (const tick of ticks) {
    if (!visSpanSet.has(tick)) drawOneTickSpans(tick, true);
  }

  // Pass 2: visible ticks, all spans (foreground). Spans are sorted by startUs so cross-tick
  // context (low startUs) naturally draws before native spans — correct z-order within the tick.
  for (const tick of visibleTicks) {
    drawOneTickSpans(tick, false);
  }
}

/**
 * Per-system chunk lane — one row, filters chunks by `systemIndex`. Same bar colouring as the
 * slot-lane chunk row (`getSystemColor(systemIndex, theme.spans)`). No label text on the bar —
 * the gutter already shows the system name.
 */
function drawSystemLane(
  ctx: CanvasRenderingContext2D,
  systemIndex: number,
  visibleTicks: TickData[],
  gutterWidth: number,
  width: number,
  pxOfUs: (us: number) => number,
  ty: number,
  selection: ProfilerSelection | null,
  theme: StudioTheme,
  spanColorMode: SpanColorMode,
): void {
  for (const tick of visibleTicks) {
    for (const chunk of tick.chunks) {
      if (chunk.systemIndex !== systemIndex) continue;
      const x1 = pxOfUs(chunk.startUs);
      const x2 = pxOfUs(chunk.endUs);
      if (x2 < gutterWidth || x1 > width) continue;
      const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
      ctx.fillStyle = colorForChunk(chunk, spanColorMode, theme.spans);
      ctx.fillRect(x1, ty + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT);
      if (selection && selection.kind === 'chunk' && isSameChunk(selection.chunk, chunk)) {
        ctx.strokeStyle = theme.selectedOutline;
        ctx.lineWidth = 1.5;
        ctx.strokeRect(x1 + 0.5, ty + SPAN_BAR_MARGIN + 0.5, w - 1, SPAN_BAR_HEIGHT - 1);
      }
    }
  }
}

/**
 * Summary-mode silhouette for a per-system lane — same grey activity-union pattern as a slot
 * lane, filtered by `systemIndex`. Spans aren't drawn (systems don't own spans; only chunks).
 */
function drawSystemSummary(
  ctx: CanvasRenderingContext2D,
  systemIndex: number,
  visibleTicks: TickData[],
  gutterWidth: number,
  contentWidth: number,
  pxOfUs: (us: number) => number,
  ty: number,
  width: number,
  theme: StudioTheme,
): void {
  const stripTop = ty + SUMMARY_STRIP_TOP_PAD;
  const stripH = SUMMARY_STRIP_HEIGHT;
  ctx.save();
  ctx.beginPath();
  ctx.rect(gutterWidth, stripTop, contentWidth, stripH);
  ctx.clip();
  ctx.fillStyle = theme.activitySilhouette;
  for (const tick of visibleTicks) {
    for (const chunk of tick.chunks) {
      if (chunk.systemIndex !== systemIndex) continue;
      const x1 = pxOfUs(chunk.startUs);
      const x2 = pxOfUs(chunk.endUs);
      if (x2 < gutterWidth || x1 > width) continue;
      const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
      ctx.fillRect(x1, stripTop, w, stripH);
    }
  }
  ctx.restore();
}

function drawPhases(
  ctx: CanvasRenderingContext2D,
  visibleTicks: TickData[],
  gutterWidth: number,
  width: number,
  pxOfUs: (us: number) => number,
  ty: number,
  theme: StudioTheme,
): void {
  for (const tick of visibleTicks) {
    for (const phase of tick.phases) {
      const x1 = pxOfUs(phase.startUs);
      const x2 = pxOfUs(phase.endUs);
      if (x2 < gutterWidth || x1 > width) continue;
      const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
      ctx.fillStyle = theme.phaseColor;
      ctx.fillRect(x1, ty + 1, w, PHASE_TRACK_HEIGHT - 2);
      if (w > 50) {
        ctx.save();
        ctx.beginPath();
        ctx.rect(x1, ty + 1, w, PHASE_TRACK_HEIGHT - 2);
        ctx.clip();
        // Phase bar fill is always a dark colour (slot 0 of either timeline palette is the deep
        // purple identity), so the label needs the textOnDarkBar token in both themes — the previous
        // `theme.foreground` produced dark-on-dark text in light mode.
        ctx.fillStyle = theme.textOnDarkBar;
        ctx.font = '9px monospace';
        ctx.textAlign = 'left';
        ctx.fillText(`${phase.phaseName} (${formatDuration(phase.durationUs)})`, x1 + 3, ty + 12);
        ctx.restore();
      }
    }
  }

  // ─── Marker glyphs (lifecycle landmarks: UoW Create / UoW Flush) ────────────────────────────────
  // Same convention as the GC track: triangle for "begin"-shape events (UoW Create), circle for
  // "end"-shape events (UoW Flush). Drawn near the top of the phase row so they sit above the
  // phase bars without overlapping the duration-label text.
  drawPhaseMarkers(ctx, visibleTicks, gutterWidth, width, pxOfUs, ty, theme);
}

function drawPhaseMarkers(
  ctx: CanvasRenderingContext2D,
  visibleTicks: TickData[],
  gutterWidth: number,
  width: number,
  pxOfUs: (us: number) => number,
  ty: number,
  theme: StudioTheme,
): void {
  // Glyphs sit centred on the row with a small footprint so they read as decorations on top of the
  // phase bars rather than competing with them. Use timeline palette slot 6 (green) — high contrast
  // against the phase fill (deep purple slot 0) in both light and dark themes; white markers were
  // invisible against the light row background between phase bars.
  const cy = ty + PHASE_TRACK_HEIGHT / 2;
  const r = Math.max(3, Math.floor(PHASE_TRACK_HEIGHT / 4));
  const markerColor = theme.timelineBands[6];

  ctx.save();
  ctx.beginPath();
  ctx.rect(gutterWidth, ty, width - gutterWidth, PHASE_TRACK_HEIGHT);
  ctx.clip();

  for (const tick of visibleTicks) {
    if (tick.phaseMarkers.length === 0) continue;
    for (const m of tick.phaseMarkers) {
      const px = pxOfUs(m.timestampUs);
      if (px < gutterWidth - r || px > width + r) continue;

      // 161 = RuntimePhaseUoWCreate (triangle, "start"), 162 = RuntimePhaseUoWFlush (circle, "end").
      // Stays in sync with the legend convention used by the GC track renderer.
      ctx.fillStyle = markerColor;
      ctx.beginPath();
      if (m.kind === 161) {
        // Down-pointing triangle (apex at bottom) so the glyph reads as "this is the *start* of something
        // that opens here" — same convention as the GC track.
        ctx.moveTo(px - r, cy - r);
        ctx.lineTo(px + r, cy - r);
        ctx.lineTo(px, cy + r);
        ctx.closePath();
      } else if (m.kind === 162) {
        ctx.arc(px, cy, r, 0, Math.PI * 2);
      } else {
        // Forward-compat fallback: any other kind on phaseMarkers renders as a small square.
        ctx.rect(px - r, cy - r, r * 2, r * 2);
      }
      ctx.fill();
    }
  }
  ctx.restore();
}

/**
 * One row-definition per mini-row in a given operation track. `getOps` + `getEndMax` pull the
 * right projection array off each `TickData`; `labelColor` + `barColor` are resolved from the
 * theme's `timelineBands` (dark or light-darkened 25%) with alpha suffixes so colour identity
 * survives across themes and the alpha tint stays proportional in both.
 */
interface MiniRowDef {
  label: string;
  labelColor: string;
  barColor: string;
  getOps: (t: TickData) => SpanData[];
  getEndMax: (t: TickData) => Float64Array;
}

function miniRowsForTrack(trackId: string, bands: readonly string[]): MiniRowDef[] {
  const CC = 'CC'; // label alpha ~80%
  const B = '26';  // bar alpha ~15%
  switch (trackId) {
    case 'page-cache':
      return [
        { label: 'Fetch',    labelColor: bands[1] + CC, barColor: bands[1] + B, getOps: t => t.cacheFetch,   getEndMax: t => t.cacheFetchEndMax },
        { label: 'Allocate', labelColor: bands[2] + CC, barColor: bands[2] + B, getOps: t => t.cacheAlloc,   getEndMax: t => t.cacheAllocEndMax },
        { label: 'Evicted',  labelColor: bands[3] + CC, barColor: bands[3] + B, getOps: t => t.cacheEvict,   getEndMax: t => t.cacheEvictEndMax },
        { label: 'Flush',    labelColor: bands[4] + CC, barColor: bands[4] + B, getOps: t => t.cacheFlushes, getEndMax: t => t.cacheFlushesEndMax },
      ];
    case 'disk-io':
      return [
        { label: 'Reads',  labelColor: bands[5] + CC, barColor: bands[5] + B, getOps: t => t.diskReads,  getEndMax: t => t.diskReadsEndMax },
        { label: 'Writes', labelColor: bands[6] + CC, barColor: bands[6] + B, getOps: t => t.diskWrites, getEndMax: t => t.diskWritesEndMax },
      ];
    case 'transactions':
      return [
        { label: 'Commits',   labelColor: bands[7]  + CC, barColor: bands[7]  + B, getOps: t => t.txCommits,   getEndMax: t => t.txCommitsEndMax },
        { label: 'Rollbacks', labelColor: bands[12] + CC, barColor: bands[12] + B, getOps: t => t.txRollbacks, getEndMax: t => t.txRollbacksEndMax },
        { label: 'Persists',  labelColor: bands[8]  + CC, barColor: bands[8]  + B, getOps: t => t.txPersists,  getEndMax: t => t.txPersistsEndMax },
      ];
    case 'wal':
      return [
        { label: 'Flushes', labelColor: bands[10] + CC, barColor: bands[10] + B, getOps: t => t.walFlushes, getEndMax: t => t.walFlushesEndMax },
        { label: 'Waits',   labelColor: bands[11] + CC, barColor: bands[11] + B, getOps: t => t.walWaits,   getEndMax: t => t.walWaitsEndMax },
      ];
    case 'checkpoint':
      return [
        { label: 'Cycles', labelColor: bands[9] + CC, barColor: bands[9] + B, getOps: t => t.checkpointCycles, getEndMax: t => t.checkpointCyclesEndMax },
      ];
    default:
      return [];
  }
}

function drawMiniRowsTrack(
  ctx: CanvasRenderingContext2D,
  track: TrackLayout,
  ticks: readonly TickData[],
  visibleTicks: TickData[],
  visStartUs: number,
  visEndUs: number,
  gutterWidth: number,
  width: number,
  pxOfUs: (us: number) => number,
  ty: number,
  theme: StudioTheme,
): void {
  const rows = miniRowsForTrack(track.id, theme.timelineBands);
  const MRH = MINI_ROW_HEIGHT;
  const barH = MRH - 1;

  ctx.font = '9px monospace';
  ctx.textAlign = 'left';
  const labelPad = 3;

  for (let r = 0; r < rows.length; r++) {
    const row = rows[r];
    const rowY = ty + r * MRH;

    // Draw bars FIRST (so labels overlay them)
    drawMiniRowBars(ctx, row.getOps, row.getEndMax, rowY, row.barColor, barH, ticks, visibleTicks, visStartUs, visEndUs, gutterWidth, pxOfUs);

    // Label pill
    const swatchSize = 7;
    const swatchToText = 3;
    const labelTextWidth = ctx.measureText(row.label).width;
    const pillWidth = labelPad + swatchSize + swatchToText + labelTextWidth + labelPad;

    ctx.fillStyle = theme.labelPillBg;
    ctx.fillRect(gutterWidth + 2, rowY + 1, pillWidth, MRH - 1);

    const swatchX = gutterWidth + 2 + labelPad;
    const swatchY = rowY + 1 + Math.floor((MRH - 1 - swatchSize) / 2);
    ctx.fillStyle = row.labelColor;
    ctx.fillRect(swatchX, swatchY, swatchSize, swatchSize);

    ctx.fillStyle = theme.miniRowLabelText;
    ctx.fillText(row.label, swatchX + swatchSize + swatchToText, rowY + MRH - 2);
  }
  void width;
}

function drawMiniRowBars(
  ctx: CanvasRenderingContext2D,
  getOps: (t: TickData) => SpanData[],
  getEndMax: (t: TickData) => Float64Array,
  rowY: number,
  barColor: string,
  barH: number,
  ticks: readonly TickData[],
  visibleTicks: TickData[],
  visStartUs: number,
  visEndUs: number,
  gutterWidth: number,
  pxOfUs: (us: number) => number,
): void {
  // No coalescing on mini-row ops — overlapping semi-transparent bars naturally build up darker
  // tones where activity clusters, which is the intended read: "many events here" shows as
  // saturated colour, "sparse" shows as faint. The bar colour is pre-applied with the '26' (~15%)
  // alpha suffix at the call site, so ~7 overlapping bars compound to fully opaque.
  //
  // Iterates `ticks` (ALL ticks, not just visible) because an op is attributed to the tick where
  // its completion was recorded, not where it started — e.g. `Checkpoint.Cycle` runs across many
  // ticks but lives in exactly one tick's op list, whose own `startUs` is way past the op's start.
  // So `tick.startUs > visEndUs` cannot be used as a break. The `endMax[last] < visStartUs` skip
  // stays valid (running max of endUs bounds every op's endUs in that tick).
  //
  // Two-pass draw (same rationale as drawSlotLane): non-visible ticks first, visibleTicks second,
  // so native viewport bars are always drawn on top of cross-tick long-running op bars.
  ctx.fillStyle = barColor;

  const drawOneTick = (tick: TickData, nativeOnly: boolean): void => {
    const ops = getOps(tick);
    if (ops.length === 0) return;
    const endMax = getEndMax(tick);
    if (endMax[endMax.length - 1] < visStartUs) return;
    let lo = 0;
    let hi = ops.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (endMax[mid] < visStartUs) lo = mid + 1; else hi = mid;
    }
    for (let i = lo; i < ops.length; i++) {
      const op = ops[i];
      if (op.startUs > visEndUs) break;
      if (nativeOnly && op.startUs < tick.startUs) continue;
      const x1 = pxOfUs(op.startUs);
      const x2 = pxOfUs(op.endUs);
      if (x2 < gutterWidth) continue;
      const actualWidth = x2 - x1;
      const w = actualWidth < MIN_RECT_WIDTH ? MIN_RECT_WIDTH : actualWidth;
      ctx.fillRect(x1, rowY, w, barH);
    }
  };

  const visMiniSet = new Set<TickData>(visibleTicks);
  for (const tick of ticks) {
    if (!visMiniSet.has(tick)) drawOneTick(tick, true);
  }
  for (const tick of visibleTicks) {
    drawOneTick(tick, false);
  }
}

function drawCrosshair(
  ctx: CanvasRenderingContext2D,
  crosshairX: number,
  width: number,
  height: number,
  gutterWidth: number,
  vp: Viewport,
  visibleTicks: TickData[],
  theme: StudioTheme,
): void {
  const cursorUs = vp.offsetX + (crosshairX - gutterWidth) / vp.scaleX;
  const baseUs = visibleTicks[0]?.startUs ?? 0;
  ctx.strokeStyle = theme.crosshairLine;
  ctx.lineWidth = 1;
  ctx.setLineDash([]);
  ctx.beginPath();
  ctx.moveTo(crosshairX, RULER_HEIGHT);
  ctx.lineTo(crosshairX, height);
  ctx.stroke();

  const label = formatRulerLabel(cursorUs - baseUs);
  ctx.font = '9px monospace';
  const labelWidth = Math.round(ctx.measureText(label).width + 8);
  const labelX = Math.round(Math.min(crosshairX - labelWidth / 2, width - labelWidth - 2));
  const labelY = Math.round(height - 18);
  ctx.fillStyle = theme.crosshairPillBg;
  ctx.fillRect(labelX, labelY, labelWidth, 16);
  ctx.fillStyle = theme.foreground;
  ctx.textAlign = 'center';
  ctx.fillText(label, Math.round(crosshairX), labelY + 12);
}

function drawDragSelection(
  ctx: CanvasRenderingContext2D,
  sel: { x1: number; x2: number },
  width: number,
  height: number,
  gutterWidth: number,
  vp: Viewport,
  theme: StudioTheme,
): void {
  const selW = sel.x2 - sel.x1;
  ctx.fillStyle = theme.zoomDragFill;
  ctx.fillRect(sel.x1, RULER_HEIGHT, selW, height - RULER_HEIGHT);
  ctx.strokeStyle = theme.zoomDragStroke;
  ctx.lineWidth = 1;
  ctx.setLineDash([]);
  ctx.beginPath();
  ctx.moveTo(sel.x1, RULER_HEIGHT); ctx.lineTo(sel.x1, height);
  ctx.moveTo(sel.x2, RULER_HEIGHT); ctx.lineTo(sel.x2, height);
  ctx.stroke();

  // Duration label
  const selStartUs = vp.offsetX + (sel.x1 - gutterWidth) / vp.scaleX;
  const selEndUs = vp.offsetX + (sel.x2 - gutterWidth) / vp.scaleX;
  const selDuration = selEndUs - selStartUs;
  const durLabel = formatRulerLabel(selDuration);
  ctx.font = '11px monospace';
  ctx.textAlign = 'center';
  const durLabelX = Math.round((sel.x1 + sel.x2) / 2);
  const centerY = Math.round((RULER_HEIGHT + height) / 2);
  const durLabelW = Math.round(ctx.measureText(durLabel).width + 10);
  ctx.fillStyle = theme.crosshairPillBg;
  ctx.fillRect(durLabelX - Math.round(durLabelW / 2), centerY + 4, durLabelW, 18);
  ctx.fillStyle = theme.foreground;
  ctx.fillText(durLabel, durLabelX, centerY + 17);
  void width;
}

// ─── Selection identity helpers (exported for the hit-test module) ──────────────────────────────

export function isSameChunk(a: ChunkSpan, b: ChunkSpan): boolean {
  return a.systemIndex === b.systemIndex
      && a.chunkIndex === b.chunkIndex
      && a.threadSlot === b.threadSlot
      && Math.abs(a.startUs - b.startUs) < 0.01;
}

export function isSameSpan(a: SpanData, b: SpanData): boolean {
  if (a.spanId !== undefined && a.spanId === b.spanId) return true;
  return a.threadSlot === b.threadSlot
      && Math.abs(a.startUs - b.startUs) < 0.01
      && Math.abs(a.endUs - b.endUs) < 0.01
      && a.name === b.name;
}
