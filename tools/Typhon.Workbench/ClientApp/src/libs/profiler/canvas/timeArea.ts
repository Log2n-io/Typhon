import type { ChunkSpan, OffCpuStore, ProfilerSelection, SpanData, TickData } from '@/libs/profiler/model/traceModel';
import type { TimeRange, TrackLayout, Viewport } from '@/libs/profiler/model/uiTypes';
import type { TimeAreaHover } from './timeAreaHitTest';
import { relativeLuminanceHex } from '@/libs/color/contrast';
import {
  colorForChunk,
  colorForSpan,
  computeGridStep,
  formatDuration,
  formatRulerLabel,
  setupCanvas,
} from './canvasUtils';
import type { SpanColorMode, SpanPalette } from '@/stores/useProfilerViewStore';
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
    TRACK_GAP, SPAN_BAR_TEXT_OFFSET,
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

// ─── Off-CPU overlay constants + scratch ─────────────────────────────────────────────────────────
/** Compositing alpha for the off-CPU hatch fill — the diagonal lines are sparse, so the lane content shows through the gaps. */
const OFF_CPU_ALPHA = 0.3;
/** Diagonal-hatch tile size in px for the off-CPU fill pattern. */
const OFF_CPU_HATCH_TILE = 6;
/** Below this pixel width an off-CPU interval is sub-pixel; adjacent sub-pixel intervals coalesce into one run (LOD). */
const OFF_CPU_MIN_WIDTH_PX = 2;
// Per-frame run scratch for the off-CPU LOD pass. Grown on demand to the canvas pixel width. `buildOffCpuRuns` keeps every
// emitted run ≥1px AND pixel-disjoint from the next, so the run count is bounded by the viewport pixel width regardless of
// how many thousands of intervals a lane holds. Reused across slot lanes (drawTimeArea is single-threaded per frame), so the
// off-CPU pass allocates nothing once the scratch has grown.
let _offCpuRunX1 = new Float64Array(0);
let _offCpuRunX2 = new Float64Array(0);
let _offCpuRunCat = new Uint8Array(0);
// Per-category off-CPU hatch CanvasPattern cache, indexed by OffCpuCategory. Built lazily on first paint from a small
// offscreen tile; a CanvasPattern is reusable across contexts, so a single module-lifetime cache is safe.
const _offCpuHatch: (CanvasPattern | null)[] = [];

/**
 * Lazily build + cache the diagonal-hatch fill pattern for one off-CPU category. The tile is a small transparent canvas
 * with a single corner-to-corner diagonal stroke in the category color, which tiles into a seamless 45° hatch. Returns
 * null only if an offscreen 2D context can't be obtained (never in a real browser) — callers fall back to a solid fill.
 */
function getOffCpuHatch(ctx: CanvasRenderingContext2D, category: number, color: string): CanvasPattern | null {
  const cached = _offCpuHatch[category];
  if (cached !== undefined) {
    return cached;
  }
  const tile = document.createElement('canvas');
  tile.width = OFF_CPU_HATCH_TILE;
  tile.height = OFF_CPU_HATCH_TILE;
  const tileCtx = tile.getContext('2d');
  let pattern: CanvasPattern | null = null;
  if (tileCtx !== null) {
    tileCtx.strokeStyle = color;
    tileCtx.lineWidth = 1.3;
    tileCtx.beginPath();
    tileCtx.moveTo(0, 0);
    tileCtx.lineTo(OFF_CPU_HATCH_TILE, OFF_CPU_HATCH_TILE);
    tileCtx.stroke();
    pattern = ctx.createPattern(tile, 'repeat');
  }
  _offCpuHatch[category] = pattern;
  return pattern;
}

/**
 * Coalesce one slot's off-CPU intervals into draw runs for the current viewport — the level-of-detail pass. A wide interval
 * (≥ <paramref name="minWidthPx"/>) becomes its own run; ANY run of adjacent sub-pixel intervals merges into one run,
 * irrespective of category — at sub-pixel zoom the per-interval colour is invisible anyway, and merging across categories is
 * what keeps the output bounded. The leftmost interval's category wins the merged run. Each emitted run is ≥1px and
 * pixel-disjoint from the previous one, so a zoomed-out lane with thousands of gaps collapses to at most pixel-width runs
 * (and pixel-width `fillRect` calls) — never thousands. Pure + scratch-based: writes <c>outX1 / outX2 / outCat</c> and
 * returns the run count, so it allocates nothing per frame and can be unit-tested in isolation.
 *
 * Preconditions: <c>store.startUs</c> / <c>store.endUs</c> are ascending; <paramref name="firstIdx"/> is the first interval
 * index with <c>endUs &gt; visStartUs</c> (binary-searched by the caller). The loop also hard-stops once the scratch is full
 * — since runs are emitted left-to-right and pixel-disjoint, a full scratch means every viewport pixel is already painted.
 */
export function buildOffCpuRuns(
  store: OffCpuStore,
  firstIdx: number,
  visEndUs: number,
  pxOfUs: (us: number) => number,
  gutterWidth: number,
  minWidthPx: number,
  outX1: Float64Array,
  outX2: Float64Array,
  outCat: Uint8Array,
): number {
  const n = store.startUs.length;
  const cap = outX1.length;
  let runCount = 0;
  let curOpen = false;
  let curCat = -1;
  let curX1 = 0;
  let curX2 = 0;
  // Right edge of the last flushed run / wide interval. A sub-pixel interval entirely behind this is on an
  // already-painted pixel column, so it is dropped — this is what makes consecutive runs pixel-disjoint.
  let lastEmittedX2 = Number.NEGATIVE_INFINITY;
  const flush = (): void => {
    if (curOpen && runCount < cap) {
      outX1[runCount] = curX1;
      outX2[runCount] = curX2;
      outCat[runCount] = curCat;
      runCount++;
      lastEmittedX2 = curX2;
    }
    curOpen = false;
  };
  for (let i = firstIdx; i < n; i++) {
    if (runCount >= cap) break;                        // scratch full ⇒ every viewport pixel already painted
    if (store.startUs[i] > visEndUs) break;            // sorted by startUs ⇒ nothing further is visible
    const x2 = pxOfUs(store.endUs[i]);
    if (x2 < gutterWidth) continue;                    // fully left of the content area
    let x1 = pxOfUs(store.startUs[i]);
    if (x1 < gutterWidth) x1 = gutterWidth;            // clamp to the content-left edge
    const cat = store.category[i];
    if (x2 - x1 >= minWidthPx) {
      // Wide interval — its own run. Flush any open sub-pixel run first so draw order stays left-to-right.
      flush();
      if (runCount < cap) {
        outX1[runCount] = x1;
        outX2[runCount] = x2;
        outCat[runCount] = cat;
        runCount++;
        lastEmittedX2 = x2;
      }
      continue;
    }
    // Sub-pixel interval — extend the open run when adjacent (≤1px gap), regardless of category.
    if (curOpen && x1 <= curX2 + 1) {
      if (x2 > curX2) curX2 = x2;
      continue;
    }
    flush();
    if (x2 <= lastEmittedX2) continue;                 // fully inside an already-emitted run ⇒ pixel already painted
    curOpen = true;
    curCat = cat;
    curX1 = x1 > lastEmittedX2 ? x1 : lastEmittedX2;    // never start left of the last painted pixel
    curX2 = x2 > curX1 + 1 ? x2 : curX1 + 1;           // a sub-pixel run still paints ≥1px wide
  }
  flush();
  return runCount;
}

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
// Categorical palettes hash into ≤ 8 colours (huge hit rate), but DURATION colour mode paints a continuous heat
// gradient — near-unique hex per bar, ~zero hits, and the module-level map would grow without bound across frames.
// Cap it: once it fills, drop it and start fresh. The categorical modes never reach the cap, so they never churn.
const TEXT_ON_BAR_CACHE_MAX = 512;

function readableOnBar(barHex: string, theme: StudioTheme): string {
  let pick = _textOnBarCache.get(barHex);
  if (pick === undefined) {
    pick = relativeLuminanceHex(barHex) > 0.5 ? 'dark' : 'light';
    if (_textOnBarCache.size >= TEXT_ON_BAR_CACHE_MAX) {
      _textOnBarCache.clear();
    }
    _textOnBarCache.set(barHex, pick);
  }
  return pick === 'dark' ? theme.textOnLightBar : theme.textOnDarkBar;
}

// A per-frame cache of `new Set(visibleTicks)`: every lane needs the membership set to skip ticks already drawn in the
// foreground pass, and they all receive the same `visibleTicks` array within one render — so build it once (first lane)
// and reuse it, keyed by array identity so a new viewport (new array) rebuilds it. Both call sites read it read-only.
let _visTickSetKey: readonly TickData[] | null = null;
let _visTickSet = new Set<TickData>();
function visibleTickSet(visibleTicks: TickData[]): Set<TickData> {
  if (visibleTicks !== _visTickSetKey) {
    _visTickSet = new Set(visibleTicks);
    _visTickSetKey = visibleTicks;
  }
  return _visTickSet;
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
  /**
   * Current pointer hover (from `hitTestTimeArea`), or `null` when the cursor is outside the
   * canvas / over a non-highlightable element. Drives the thin contour stroke that distinguishes
   * adjacent same-coloured bars (spans, chunks, phases, mini-row ops). Selection's heavier stroke
   * takes precedence — the two are visually distinct (1.5 px full-alpha vs 1 px at ~55% alpha).
   */
  hover: TimeAreaHover | null;
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
  /**
   * Which palette the categorical span modes draw from. Sourced from `useProfilerViewStore.spanPalette`.
   * Threaded alongside {@link spanColorMode} so the renderer's hot loop has a stable lookup.
   */
  spanPalette: SpanPalette;
  /**
   * Whether the off-CPU overlay is shown on slot lanes. Sourced from the TimeArea filter toggle
   * (`useProfilerViewStore.showOffCpu`). When false the off-CPU pass is skipped entirely.
   */
  showOffCpu: boolean;
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

  const { tracks, viewRange, vp, gutterWidth, legendsVisible, visibleTicks, ticks, selection, hover, dragSelection, crosshairX, gaugeData, helpHover, pendingRangesUs, spanColorMode, spanPalette, showOffCpu } = inputs;
  const contentWidth = width - gutterWidth;
  // Height of the pinned ruler band (ruler + gap below it). Everything above this line is always
  // visible regardless of vertical scroll; tracks below are clipped to this boundary.
  const rulerStickyBottom = RULER_HEIGHT + TRACK_GAP;

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
    const ty = track.id === 'ruler' ? track.y : track.y - vp.scrollY;
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
    const sepY = track.id === 'ruler' ? rulerStickyBottom - 0.5 : track.y + trackAdvance - vp.scrollY - 0.5;
    ctx.strokeStyle = theme.border;
    ctx.lineWidth = 0.5;
    ctx.beginPath();
    ctx.moveTo(0, sepY);
    ctx.lineTo(width, sepY);
    ctx.stroke();
  }

  // Pin the ruler gutter: overwrite any scrolled labels that entered the ruler band.
  ctx.fillStyle = theme.card;
  ctx.fillRect(0, 0, gutterWidth, rulerStickyBottom);
  ctx.strokeStyle = theme.border;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(gutterWidth - 0.5, 0);
  ctx.lineTo(gutterWidth - 0.5, rulerStickyBottom);
  ctx.stroke();
  {
    const rulerTrack = tracks.find(t => t.id === 'ruler');
    if (rulerTrack) {
      ctx.fillStyle = theme.mutedForeground;
      ctx.font = '10px monospace';
      ctx.textAlign = 'left';
      ctx.fillText(rulerTrack.label, GUTTER_PAD_LEFT, rulerTrack.y + 12);
    }
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
    const ty = track.id === 'ruler' ? track.y : track.y - vp.scrollY;

    if (track.id === 'ruler') {
      drawRuler(ctx, { visStartUs, visEndUs, contentWidth, gutterWidth, width, height, visibleTicks, vp, ty, pxOfUs }, theme);
      continue;
    }

    // Summary mode
    if (track.state === 'summary') {
      if (GAUGE_TRACK_ID_SET.has(track.id)) {
        // Spark-line preview drawn INSIDE the label row's Y band. Memory is special-cased inside
        // drawGaugeSummaryStrip — it renders the full Memory chart at reduced alpha so the GC
        // overlay remains visible when the track is collapsed.
        drawGaugeSummaryStrip(ctx, gaugeData, track.id,
          vp,
          { x: gutterWidth, y: ty + SUMMARY_STRIP_TOP_PAD, width: contentWidth, height: SUMMARY_STRIP_HEIGHT },
          gutterWidth, theme, ticks);
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
      drawSlotLane(ctx, track, threadSlot, ticks, visibleTicks, visStartUs, visEndUs, gutterWidth, width, pxOfUs, ty, selection, hover, theme,
        spanColorMode, spanPalette, gaugeData.offCpuBySlot.get(threadSlot), showOffCpu);
      continue;
    }

    // Per-system lanes (expanded) — chunks grouped by systemIndex, single row, same colours as
    // the slot-lane chunk row.
    if (track.id.startsWith('system-')) {
      const systemIdx = Number.parseInt(track.id.slice(7), 10);
      drawSystemLane(ctx, systemIdx, visibleTicks, gutterWidth, width, pxOfUs, ty, selection, hover, theme, spanColorMode, spanPalette);
      continue;
    }

    // Phases
    if (track.id === 'phases') {
      drawPhases(ctx, visibleTicks, gutterWidth, width, pxOfUs, ty, hover, theme);
      continue;
    }

    // Operation mini-rows — use `ticks` (not visibleTicks) so long-running ops like Checkpoint.Cycle
    // that started in a tick now off-screen but extend INTO the viewport still render. The inner
    // loop cheaply skips ticks whose running-max endUs is before visStartUs.
    if (track.id === 'page-cache' || track.id === 'disk-io' || track.id === 'transactions' || track.id === 'wal' || track.id === 'checkpoint') {
      drawMiniRowsTrack(ctx, track, ticks, visibleTicks, visStartUs, visEndUs, gutterWidth, width, pxOfUs, ty, hover, theme);
      continue;
    }
  }

  // Pin the ruler content: overwrite any scrolled track content that entered the ruler band.
  ctx.fillStyle = theme.background;
  ctx.fillRect(gutterWidth, 0, contentWidth, rulerStickyBottom);
  for (const track of tracks) {
    if (track.id !== 'ruler') continue;
    drawRuler(ctx, { visStartUs, visEndUs, contentWidth, gutterWidth, width, height, visibleTicks, vp, ty: track.y, pxOfUs }, theme);
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
  // Skips the ruler band so timestamp labels stay legible. X-clipped to the content area so the
  // gutter labels aren't tinted either.
  if (gaugeData.gcSuspensions.length > 0) {
    ctx.fillStyle = 'rgba(232, 93, 77, 0.13)';  // CACHE_EXCLUSIVE_COLOR @ ~13% alpha
    const bandTop = rulerStickyBottom;
    const bandHeight = height - rulerStickyBottom;
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

  // Sticky ruler separator — always drawn last so GC bands, crosshair, and drag-selection
  // never paint over the visual boundary between the pinned ruler and the scrollable area.
  ctx.strokeStyle = theme.border;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(0, rulerStickyBottom - 0.5);
  ctx.lineTo(width, rulerStickyBottom - 0.5);
  ctx.stroke();
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

/**
 * Thin contour stroke for the bar the mouse is currently hovering. Visually distinct from the
 * heavier 1.5 px selection stroke (clicks) only by line weight — both at full alpha for max
 * legibility against busy bar fills. Caller already verified the hover identity match; this
 * helper just paints the contour.
 */
function strokeHoverContour(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  w: number,
  h: number,
  theme: StudioTheme,
): void {
  ctx.strokeStyle = theme.selectedOutline;
  ctx.lineWidth = 1.5;
  const prevAlpha = ctx.globalAlpha;
  ctx.globalAlpha = 0.8;
  // +0.5 inset keeps the 1 px stroke crisp on the pixel grid (canvas strokes straddle the path).
  ctx.strokeRect(x + 0.5, y + 0.5, w - 1, h - 1);
  ctx.globalAlpha = prevAlpha;
}

/**
 * Paint one slot lane's off-CPU intervals as a translucent, category-colored band over the full lane height. Drawn last
 * (over the chunk row + span rows) so it reads as "this thread was switched out here, and here's why". Viewport-culled via
 * a binary search on the monotonic <c>endUs</c> array, then LOD-coalesced by {@link buildOffCpuRuns} so a zoomed-out lane
 * never issues more <c>fillRect</c>s than it has pixels.
 */
function drawOffCpuOverlay(
  ctx: CanvasRenderingContext2D,
  store: OffCpuStore,
  ty: number,
  laneHeight: number,
  visStartUs: number,
  visEndUs: number,
  gutterWidth: number,
  width: number,
  pxOfUs: (us: number) => number,
  theme: StudioTheme,
): void {
  const n = store.startUs.length;
  if (n === 0 || laneHeight <= 0) return;

  // First interval whose endUs is still inside the viewport. endUs is monotonically ascending (the intervals are
  // non-overlapping, sorted gaps), so a plain lower-bound binary search is valid.
  let lo = 0;
  let hi = n;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (store.endUs[mid] <= visStartUs) lo = mid + 1; else hi = mid;
  }
  if (lo >= n) return;

  // Grow the run scratch to the canvas pixel width — a run is always ≥1px, so the run count can never exceed `width`.
  const cap = Math.max(64, Math.ceil(width) + 1);
  if (_offCpuRunX1.length < cap) {
    _offCpuRunX1 = new Float64Array(cap);
    _offCpuRunX2 = new Float64Array(cap);
    _offCpuRunCat = new Uint8Array(cap);
  }
  const runCount = buildOffCpuRuns(
    store, lo, visEndUs, pxOfUs, gutterWidth, OFF_CPU_MIN_WIDTH_PX, _offCpuRunX1, _offCpuRunX2, _offCpuRunCat);

  if (runCount > 0) {
    const prevAlpha = ctx.globalAlpha;

    // Hatch fill — per-category 45° diagonal pattern. The pattern is anchored to the canvas origin, so the hatch phase is
    // continuous across every bar and lane. Lane content shows through the gaps between the hatch lines.
    ctx.globalAlpha = OFF_CPU_ALPHA;
    let prevCat = -1;
    for (let i = 0; i < runCount; i++) {
      const x1 = _offCpuRunX1[i];
      if (x1 > width) break;
      const w = Math.max(_offCpuRunX2[i] - x1, 1);
      const cat = _offCpuRunCat[i];
      if (cat !== prevCat) {
        const color = theme.offCpu[cat] ?? theme.offCpu[theme.offCpu.length - 1];
        // Solid-color fallback when a pattern couldn't be built (no offscreen 2D context — never in a real browser).
        ctx.fillStyle = getOffCpuHatch(ctx, cat, color) ?? color;
        prevCat = cat;
      }
      ctx.fillRect(x1, ty, w, laneHeight);
    }

    ctx.globalAlpha = prevAlpha;
  }
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
  hover: TimeAreaHover | null,
  theme: StudioTheme,
  spanColorMode: SpanColorMode,
  spanPalette: SpanPalette,
  offCpuStore: OffCpuStore | undefined,
  showOffCpu: boolean,
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
        const chunkColor = colorForChunk(chunk, spanColorMode, theme.spans, spanPalette, theme.isDark);
        ctx.fillStyle = chunkColor;
        ctx.fillRect(x1, ty + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT);
        if (selection && selection.kind === 'chunk' && isSameChunk(selection.chunk, chunk)) {
          ctx.strokeStyle = theme.selectedOutline;
          ctx.lineWidth = 1.5;
          ctx.strokeRect(x1 + 0.5, ty + SPAN_BAR_MARGIN + 0.5, w - 1, SPAN_BAR_HEIGHT - 1);
        } else if (hover && hover.kind === 'chunk' && isSameChunk(hover.chunk, chunk)) {
          strokeHoverContour(ctx, x1, ty + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT, theme);
        }
        if (x2 - x1 > 10) {
          ctx.save();
          ctx.beginPath();
          ctx.rect(x1, ty + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT);
          ctx.clip();
          ctx.fillStyle = readableOnBar(chunkColor, theme);
          ctx.font = '12px monospace';
          ctx.textAlign = 'left';
          const chunkName = chunk.isParallel ? `${chunk.systemName}[${chunk.chunkIndex}]` : chunk.systemName;
          const label = `${chunkName} (${formatDuration(chunk.endUs - chunk.startUs)})`;
          const textX = Math.max(x1 + 3, gutterWidth + 3);
          ctx.fillText(label, textX, ty + SPAN_BAR_MARGIN + SPAN_BAR_HEIGHT -  SPAN_BAR_TEXT_OFFSET);
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
          ctx.fillText(`${coalCount[d]} spans — zoom in`, coalX1[d] + 3, coalSy[d] + SPAN_BAR_MARGIN + SPAN_BAR_HEIGHT -  SPAN_BAR_TEXT_OFFSET);
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
      // Sentinel renderDepth === -1: pin to the chunk-row band (Idle / BetweenTick) — those
      // spans live exactly in the no-chunk gap, so the chunk row's vertical space is empty.
      // When the slot has no chunk row (chunkRowHeight === 0), `ty === spanRegionTop` so the
      // pinned span draws on top of the lane, same as a normal renderDepth=0 span.
      const rawDepth = span.renderDepth ?? span.depth ?? 0;
      const pinChunkRow = rawDepth === -1;
      const depth = pinChunkRow ? 0 : rawDepth;
      const d = depth < COAL_MAX_DEPTH ? depth : COAL_MAX_DEPTH - 1;
      const sy = pinChunkRow ? ty : spanRegionTop + depth * SPAN_ROW_HEIGHT;
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
        // Idle / BetweenTick bars (pinChunkRow) draw with a deliberately muted theme.idleBar so
        // they're spotted by being LESS visible than real work — light grey on light, dark grey on dark.
        const c = pinChunkRow ? theme.idleBar : colorForSpan(span, spanColorMode, theme.spans, spanPalette, theme.isDark);
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
      const c = pinChunkRow ? theme.idleBar : colorForSpan(span, spanColorMode, theme.spans, spanPalette, theme.isDark);
      if (c !== prevFill) { ctx.fillStyle = c; prevFill = c; }
      ctx.fillRect(x1, sy + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT);

      if (selection && selection.kind === 'span' && isSameSpan(selection.span, span)) {
        ctx.strokeStyle = theme.selectedOutline;
        ctx.lineWidth = 1.5;
        ctx.strokeRect(x1 + 0.5, sy + SPAN_BAR_MARGIN + 0.5, w - 1, SPAN_BAR_HEIGHT - 1);
      } else if (hover && hover.kind === 'span' && isSameSpan(hover.span, span)) {
        strokeHoverContour(ctx, x1, sy + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT, theme);
      }

      if (actualWidth > 10) {
        ctx.save();
        ctx.beginPath();
        ctx.rect(x1, sy + SPAN_BAR_MARGIN, actualWidth, SPAN_BAR_HEIGHT);
        ctx.clip();
        ctx.fillStyle = readableOnBar(c, theme);
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
        ctx.fillText(`${span.name} (${formatDuration(span.durationUs)})`, textX, sy + SPAN_BAR_MARGIN + SPAN_BAR_HEIGHT - SPAN_BAR_TEXT_OFFSET);
        ctx.restore();
        prevFill = ''; // clip/restore may invalidate cached fill
      }
    }

    for (let d = 0; d < COAL_MAX_DEPTH; d++) flushDepth(d);
  };

  // Pass 1: non-visible ticks, native spans only (background). Native spans may overflow past
  // their tick's endUs and still overlap the viewport — they draw correctly here. Cross-tick spans
  // are excluded: they start before the tick and can produce full-width bars in a gap.
  const visSpanSet = visibleTickSet(visibleTicks);
  for (const tick of ticks) {
    if (!visSpanSet.has(tick)) drawOneTickSpans(tick, true);
  }

  // Pass 2: visible ticks, all spans (foreground). Spans are sorted by startUs so cross-tick
  // context (low startUs) naturally draws before native spans — correct z-order within the tick.
  for (const tick of visibleTicks) {
    drawOneTickSpans(tick, false);
  }

  // Off-CPU overlay — drawn last so the translucent band composites over the chunk + span rows. Opt-in via the
  // TimeArea filter toggle; skipped entirely when the slot has no scheduling data.
  if (showOffCpu && offCpuStore !== undefined) {
    drawOffCpuOverlay(ctx, offCpuStore, ty, track.height, visStartUs, visEndUs, gutterWidth, width, pxOfUs, theme);
  }
}

/**
 * Per-system chunk lane — one row, filters chunks by `systemIndex`. Same bar colouring as the
 * slot-lane chunk row (`colorForChunk(chunk, spanColorMode, …)`). No label text on the bar —
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
  hover: TimeAreaHover | null,
  theme: StudioTheme,
  spanColorMode: SpanColorMode,
  spanPalette: SpanPalette,
): void {
  for (const tick of visibleTicks) {
    for (const chunk of tick.chunks) {
      if (chunk.systemIndex !== systemIndex) continue;
      const x1 = pxOfUs(chunk.startUs);
      const x2 = pxOfUs(chunk.endUs);
      if (x2 < gutterWidth || x1 > width) continue;
      const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
      ctx.fillStyle = colorForChunk(chunk, spanColorMode, theme.spans, spanPalette, theme.isDark);
      ctx.fillRect(x1, ty + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT);
      if (selection && selection.kind === 'chunk' && isSameChunk(selection.chunk, chunk)) {
        ctx.strokeStyle = theme.selectedOutline;
        ctx.lineWidth = 1.5;
        ctx.strokeRect(x1 + 0.5, ty + SPAN_BAR_MARGIN + 0.5, w - 1, SPAN_BAR_HEIGHT - 1);
      } else if (hover && hover.kind === 'chunk' && isSameChunk(hover.chunk, chunk)) {
        strokeHoverContour(ctx, x1, ty + SPAN_BAR_MARGIN, w, SPAN_BAR_HEIGHT, theme);
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
  hover: TimeAreaHover | null,
  theme: StudioTheme,
): void {
  for (const tick of visibleTicks) {
    for (const phase of tick.phases) {
      const x1 = pxOfUs(phase.startUs);
      const x2 = pxOfUs(phase.endUs);
      if (x2 < gutterWidth || x1 > width) continue;
      const w = Math.max(x2 - x1, MIN_RECT_WIDTH);
      ctx.fillStyle = theme.phaseColor;
      ctx.fillRect(x1, ty, w, PHASE_TRACK_HEIGHT);
      if (hover && hover.kind === 'phase' && hover.tickNumber === tick.tickNumber && hover.phase.phase === phase.phase) {
        strokeHoverContour(ctx, x1, ty, w, PHASE_TRACK_HEIGHT, theme);
      }
      if (w > 50) {
        ctx.save();
        ctx.beginPath();
        ctx.rect(x1, ty, w, PHASE_TRACK_HEIGHT);
        ctx.clip();
        // Phase bar fill is always a dark colour (slot 0 of either timeline palette is the deep
        // purple identity), so the label needs the textOnDarkBar token in both themes — the previous
        // `theme.foreground` produced dark-on-dark text in light mode.
        ctx.fillStyle = theme.textOnDarkBar;
        ctx.font = '9px monospace';
        ctx.textAlign = 'left';
        ctx.fillText(`${phase.phaseName} (${formatDuration(phase.durationUs)})`, x1 + 3, ty + 11);
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
  hover: TimeAreaHover | null,
  theme: StudioTheme,
): void {
  const rows = miniRowsForTrack(track.id, theme.timelineBands);
  const MRH = MINI_ROW_HEIGHT;
  const barH = MRH + 1;

  ctx.font = '9px monospace';
  ctx.textAlign = 'left';
  const labelPad = 3;

  for (let r = 0; r < rows.length; r++) {
    const row = rows[r];
    const rowY = ty + r * MRH;

    // Draw bars FIRST (so labels overlay them)
    drawMiniRowBars(ctx, row.getOps, row.getEndMax, rowY - 1, row.barColor, barH, track.id, ticks, visibleTicks, visStartUs, visEndUs, gutterWidth, pxOfUs, hover, theme);

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
  trackId: string,
  ticks: readonly TickData[],
  visibleTicks: TickData[],
  visStartUs: number,
  visEndUs: number,
  gutterWidth: number,
  pxOfUs: (us: number) => number,
  hover: TimeAreaHover | null,
  theme: StudioTheme,
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
      if (hover && hover.kind === 'mini-row-op' && hover.trackId === trackId && isSameSpan(hover.op, op)) {
        strokeHoverContour(ctx, x1, rowY, w, barH, theme);
        // Hover stroke restored globalAlpha but may have changed fillStyle/strokeStyle; ensure
        // subsequent ops in this row pick up the right fill again.
        ctx.fillStyle = barColor;
      }
    }
  };

  const visMiniSet = visibleTickSet(visibleTicks);
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
