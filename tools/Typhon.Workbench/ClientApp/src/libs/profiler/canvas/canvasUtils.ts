import type { Viewport } from '@/libs/profiler/model/uiTypes';
import type { StudioTheme } from './theme';
import { categoricalColor } from '@/libs/color/categorical';
import { rgbCss } from '@/libs/color/contrast';

// ═══════════════════════════════════════════════════════════════════════
// Static identity palettes — ported from the old profiler client.
// ═══════════════════════════════════════════════════════════════════════
//
// Three separate palettes, one per UI section — the eye can tell "gauge signal / code execution / operation bar"
// apart at a glance. Callers pick by explicit index per semantic role. Palettes are identity colors (a subsystem's
// signature), not theme-dependent — dark and light modes share them so the reader's color-coded intuition carries
// across theme switches.

/**
 * Tick-overview timeline palette — exactly 3 colors, 1:1 with the data-carrying roles.
 * Used for per-tick duration bars, the overP95 outlier highlight, and the selection-range rectangle.
 */
export const OVERVIEW_PALETTE = {
  bar: '#252E55',          // dark navy — baseline per-tick duration bar
  selection: '#4ADE80',    // emerald green — selected time range
  overP95: '#00C4FF',      // bright cyan — tick exceeded its P95 budget
} as const;

/**
 * Gauge palette — 8-color Viridis ramp for every gauge-region surface (line colors, accent stripes, heap-gen
 * stacked areas, page-cache buckets, GC markers, Tx/UoW lines). Walks purple → yellow; callers pick by explicit
 * index per semantic role.
 */
export const GAUGE_PALETTE: readonly string[] = [
  '#4A2ABA', // 0  — deep indigo-purple (brightened for tooltip contrast)
  '#1C3B84', // 1  — navy blue
  '#14618D', // 2  — ocean blue
  '#1E8784', // 3  — teal
  '#35A96D', // 4  — green
  '#76BA3E', // 5  — olive-green
  '#C3C22E', // 6  — mustard
  '#F6D85C', // 7  — warm yellow
];

/**
 * Span palette — 8-color warm ramp used for ALL span-like rendering: top-level system chunks, nested spans
 * inside chunks, and flame-graph nodes. Sized at 8 so neighboring hues stay visually distinct at the 1-2 px
 * widths spans collapse to at zoomed-out levels.
 *
 * Single palette, shared across both themes. Text drawn on top of a bar uses {@link readableOnBar} in
 * `timeArea.ts` to pick black-or-white ink per-bar via WCAG relative luminance — so label contrast is
 * solved independently of theme. Bar-vs-background contrast is asymmetric (dark slots dim in dark theme,
 * warm slots dim in light theme) but symmetric across themes; trading a second palette for maintenance
 * simplicity wins.
 */
export const SPAN_PALETTE: readonly string[] = [
  '#2B1255', // 0  — deep violet
  '#541965', // 1  — rich plum
  '#7E266B', // 2  — dusky plum
  '#A6386C', // 3  — magenta-rose
  '#C7506B', // 4  — coral pink
  '#DF6C69', // 5  — salmon
  '#ED8C66', // 6  — peach
  '#F5AB65', // 7  — warm amber
];

/**
 * Light-mode span palette — `rocket-custom` ramp. Distinct from {@link SPAN_PALETTE} because the
 * dark palette's deep violets (#2B1255) render as near-black bars in light mode, which makes the
 * selected-span outline (`#111827`) invisible. This ramp keeps warm identity while staying light
 * enough for the outline to show.
 */
export const SPAN_PALETTE_LIGHT: readonly string[] = [
  '#683C5A', // 0
  '#904B5A', // 1
  '#B25957', // 2
  '#CE6753', // 3
  '#E3784D', // 4
  '#F08A4D', // 5
  '#F8A05E', // 6
  '#FABB82', // 7
];

/**
 * Timeline-bar palette — 13-color Turbo ramp for phase bars + per-operation mini-row strips (cache / disk / Tx /
 * WAL / checkpoint). Sized exactly at 13, one color per operation type, stable semantic indices assigned per
 * operation at the call site.
 */
export const TIMELINE_PALETTE: readonly string[] = [
  '#30123B', // 0  — deep purple    ← Phases (background context)
  '#413E93', // 1  — indigo         ← Cache Fetch
  '#4568D7', // 2  — blue           ← Cache Allocate
  '#4490FE', // 3  — bright blue    ← Cache Evicted
  '#2FB6EA', // 4  — cyan           ← Cache Flush (writeback)
  '#1BD6C3', // 5  — teal           ← Disk Read
  '#29EF7F', // 6  — green          ← Disk Write
  '#87F859', // 7  — bright green   ← Tx Commit (success)
  '#C1EE3B', // 8  — lime           ← Tx Persist
  '#EDD03A', // 9  — yellow         ← Checkpoint Cycle
  '#F99B29', // 10 — orange         ← WAL Flush
  '#CF5916', // 11 — red-orange     ← WAL Wait (stall)
  '#7A0403', // 12 — dark red       ← Tx Rollback (failure)
];

/**
 * Timeline-bar palette (light theme) — each slot is the matching {@link TIMELINE_PALETTE} entry
 * with every sRGB channel multiplied by 0.75 (i.e. 25% closer to black). Keeps the semantic
 * identity per slot (index N's role stays the same) while giving every hue enough contrast to
 * read against off-white card backgrounds at the 15%-alpha ("26") bar fill and 80%-alpha ("CC")
 * label-swatch tints used by the mini-row renderer.
 */
export const TIMELINE_PALETTE_LIGHT: readonly string[] = [
  '#240E2C', // 0  — deep purple    (25% darker)
  '#312F6E', // 1  — indigo
  '#344EA1', // 2  — blue
  '#336CBF', // 3  — bright blue
  '#2389B0', // 4  — cyan
  '#14A192', // 5  — teal
  '#1FB35F', // 6  — green
  '#65BA43', // 7  — bright green
  '#91B32C', // 8  — lime
  '#B29C2C', // 9  — yellow
  '#BB741F', // 10 — orange
  '#9B4311', // 11 — red-orange
  '#5C0302', // 12 — dark red
];

/** Phase bars always use slot 0 of the timeline palette. Named re-export for readability at call sites. */
export const PHASE_COLOR = TIMELINE_PALETTE[0];

/** Selection outline color (coral red). Identity color — used across every section's selection-highlight stroke. */
export const SELECTED_COLOR = '#e94560';

/**
 * Page-Cache "Exclusive" bucket color (coral). Identity constant — lives here rather than in
 * `GAUGE_PALETTE` because the palette is a Viridis ramp by design and this slot intentionally
 * breaks the ramp to flag the "pinned by active UoW" bucket visually.
 */
export const CACHE_EXCLUSIVE_COLOR = '#E85D4D';

// ═══════════════════════════════════════════════════════════════════════
// Canvas primitives
// ═══════════════════════════════════════════════════════════════════════

/**
 * Set up a canvas for HiDPI rendering. Returns logical width/height in CSS pixels — **always
 * integers**. Callers should treat the return values as the full drawable surface; bars, clips,
 * and overlays can go edge-to-edge at integer coordinates without sub-pixel gaps.
 *
 * Why the integer pin matters: inside a flex/grid parent, `getBoundingClientRect().width` is often
 * fractional (e.g. `1234.25`). Setting `canvas.width = 1234.25 * dpr` silently truncates the
 * backing buffer, but the element's CSS size (`width: 100%`) still reflects the parent's fractional
 * width — so the browser scales a `2468`-pixel buffer onto a `2468.5`-physical-pixel display box.
 * That 0.5-px scale is imperceptible on bars but **kills text sharpness** because glyphs are
 * rasterised assuming pixel-grid alignment.
 *
 * Fix: floor the CSS size to an integer and force it via inline style. Canvas may end up 0-1 px
 * smaller than its container; `overflow-hidden` on the parent absorbs the gap.
 */
export function setupCanvas(canvas: HTMLCanvasElement): { width: number; height: number } {
  const dpr = window.devicePixelRatio || 1;
  // Clear any prior inline size BEFORE measuring, otherwise we read back our own last write instead
  // of the parent's current available space. Without this the canvas can't grow back when its
  // container resizes larger — the inline `width: NNNpx` we set on the previous frame wins.
  canvas.style.width = '';
  canvas.style.height = '';
  const rect = canvas.getBoundingClientRect();
  const cssW = Math.max(1, Math.floor(rect.width));
  const cssH = Math.max(1, Math.floor(rect.height));
  // Pin the displayed size to an integer so backing buffer and CSS box align exactly.
  canvas.style.width = `${cssW}px`;
  canvas.style.height = `${cssH}px`;
  canvas.width = cssW * dpr;
  canvas.height = cssH * dpr;
  const ctx = canvas.getContext('2d');
  if (ctx) ctx.scale(dpr, dpr);
  return { width: cssW, height: cssH };
}

/**
 * Convert an absolute timestamp (µs) to a pixel X coordinate in canvas space.
 * `vp.offsetX` is the viewport's left-edge timestamp; content starts past the gutter and grows right.
 */
export function toPixelX(us: number, vp: Viewport, labelWidth: number): number {
  return labelWidth + (us - vp.offsetX) * vp.scaleX;
}

/**
 * Pick a human-friendly time grid step (µs) such that labels on the ruler are spaced at least
 * `minLabelPxSpacing` apart. Walks a 1-2-5 decade ladder from 10 ns to 24 h so the ruler stays readable at
 * every zoom level from "one span" (sub-microsecond) to "whole session" (multi-second).
 */
export function computeGridStep(timeRangeUs: number, contentWidthPx: number, minLabelPxSpacing: number = 90): number {
  if (timeRangeUs <= 0 || contentWidthPx <= 0) return 0.01;
  const minStepUs = (timeRangeUs * minLabelPxSpacing) / contentWidthPx;
  const steps = [
    0.01, 0.02, 0.05,
    0.1, 0.2, 0.5,
    1, 2, 5,
    10, 20, 50,
    100, 200, 500,
    1_000, 2_000, 5_000,
    10_000, 20_000, 50_000,
    100_000, 200_000, 500_000,
    1_000_000, 2_000_000, 5_000_000,
    10_000_000, 20_000_000, 50_000_000,
    100_000_000, 200_000_000, 500_000_000,
    1_000_000_000, 2_000_000_000, 5_000_000_000,
    10_000_000_000, 20_000_000_000, 60_000_000_000,
    600_000_000_000, 3_600_000_000_000, 86_400_000_000_000,
  ];
  for (const s of steps) {
    if (s >= minStepUs) return s;
  }
  return steps[steps.length - 1];
}

/**
 * Format a µs offset for display on the ruler. Picks the coarsest unit that gives a readable number — ns
 * under 1 µs, µs up to 1 ms, ms up to 1 s, s up to 60 s, min past that.
 */
export function formatRulerLabel(us: number): string {
  const abs = Math.abs(us);
  const sign = us < 0 ? '-' : '';
  if (abs < 1) {
    const ns = abs * 1000;
    return `${sign}${ns.toFixed(ns < 10 ? 1 : 0)}ns`;
  }
  if (abs < 1_000) {
    const decimals = abs < 10 ? 2 : abs < 100 ? 1 : 0;
    return `${sign}${abs.toFixed(decimals)}us`;
  }
  if (abs < 1_000_000) return `${sign}${(abs / 1_000).toFixed(abs < 10_000 ? 2 : abs < 100_000 ? 1 : 0)}ms`;
  if (abs < 60_000_000) return `${sign}${(abs / 1_000_000).toFixed(abs < 10_000_000 ? 2 : abs < 100_000_000 ? 1 : 0)}s`;
  const mins = Math.floor(abs / 60_000_000);
  const secs = (abs % 60_000_000) / 1_000_000;
  return `${sign}${mins}m${secs.toFixed(0).padStart(2, '0')}s`;
}

/** Format a µs duration with adaptive units. Alias of `formatRulerLabel` under a semantic name. */
export const formatDuration = formatRulerLabel;

// ═══════════════════════════════════════════════════════════════════════
// Tooltip
// ═══════════════════════════════════════════════════════════════════════

/**
 * One tooltip line. Strings render in the theme's default foreground; objects carry an explicit color so
 * callers can match a line to the signal it describes (e.g., "Gen0: 4 MB" colored with the Gen0 layer's hue).
 */
export type TooltipLine = string | { text: string; color?: string };

function tooltipLineText(line: TooltipLine): string {
  return typeof line === 'string' ? line : line.text;
}

/**
 * Draw a multi-line tooltip anchored near `(x, y)`, flipped if it would overflow `(maxX, maxY)`. Uses the
 * theme's adaptive tooltip chrome (theme-branched bg + text + border) so the tooltip stays readable in both
 * dark and light modes.
 */
export function drawTooltip(
  ctx: CanvasRenderingContext2D,
  x: number,
  y: number,
  lines: TooltipLine[],
  maxX: number,
  maxY: number,
  theme: StudioTheme,
): void {
  ctx.font = '11px monospace';
  let maxLineW = 120;
  for (const line of lines) {
    const w = ctx.measureText(tooltipLineText(line)).width;
    if (w > maxLineW) maxLineW = w;
  }
  const tooltipW = maxLineW + 16;
  const tooltipH = lines.length * 16 + 8;
  const tx = Math.min(x + 12, maxX - tooltipW - 4);
  const ty = Math.min(y + 12, maxY - tooltipH - 4);

  ctx.fillStyle = theme.tooltipBackground;
  ctx.strokeStyle = theme.tooltipBorder;
  ctx.lineWidth = 1;
  ctx.fillRect(tx, ty, tooltipW, tooltipH);
  ctx.strokeRect(tx, ty, tooltipW, tooltipH);

  ctx.textAlign = 'left';
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    ctx.fillStyle = typeof line === 'string' ? theme.tooltipText : (line.color ?? theme.tooltipText);
    ctx.fillText(tooltipLineText(line), tx + 6, ty + 14 + i * 16);
  }
}

/** Deterministic hash of a string into an unsigned int — used by the per-name color path. */
function hashString(s: string): number {
  let hash = 0;
  for (let i = 0; i < s.length; i++) {
    hash = ((hash << 5) - hash + s.charCodeAt(i)) | 0;
  }
  return hash >>> 0;
}

/**
 * Log-scale duration → heat color. Maps span duration onto a blue → green → orange → red ramp on a
 * log10 scale: 1 µs maps to deep blue, 1 ms to green, ~30 ms to orange, ≥1 s to deep red. Returns an
 * HSL string (works in both themes — saturated mid-lightness reads on either background). The
 * gradient is theme-agnostic so the perceptual mapping stays stable when toggling light/dark.
 *
 * Why log: real workloads span 6+ orders of magnitude in span duration (1 µs lookup, 1 s checkpoint).
 * A linear gradient would render every span on the timeline as the same hue except a handful of
 * outliers. Log-scale spreads them across the visible color range.
 */
export function durationHeatColor(durationUs: number): string {
  const us = Math.max(1, durationUs);
  // log10(1) = 0 → 1 µs ; log10(1e6) = 6 → 1 s. Clamp the upper end so anything ≥ 1 s pegs the red end.
  const t = Math.max(0, Math.min(1, Math.log10(us) / 6));
  // Hue 220 = blue, 0 = red. We sweep 220 → 0 as t goes 0 → 1 (i.e. shorter = blue, longer = red).
  const hue = (1 - t) * 220;
  return `hsl(${Math.round(hue)} 65% 50%)`;
}

/**
 * Which palette the categorical span/chunk modes draw from (mirror of `useProfilerViewStore.SpanPalette`; inlined
 * as a union so this canvas lib has no store dependency). `duration` is a sequential heat ramp under both.
 */
export type SpanPaletteMode = 'categorical' | 'curated';

// Categorical span-bar lightness. Dark theme keeps the default categorical swatch (0.58); light theme darkens it
// so bars read against off-white cards and the selected-span outline (`#111827`) stays visible — the same reason
// `SPAN_PALETTE_LIGHT` exists. Saturation stays at the categorical default (0.62).
const CAT_SPAN_LIGHT_DARK = 0.58;
const CAT_SPAN_LIGHT_LIGHT = 0.42;

/** Shared DS-2 categorical span/chunk colour as a CSS string, theme-aware on lightness. Same id → same hue everywhere. */
function categoricalSpanColor(id: string | number, isDark: boolean): string {
  return rgbCss(categoricalColor(id, 0.62, isDark ? CAT_SPAN_LIGHT_DARK : CAT_SPAN_LIGHT_LIGHT));
}

/**
 * Decide the bar-fill colour for a span based on the user's chosen color-by mode. Centralised here so the
 * renderer's hot loop has one switch point. Under `spanPalette === 'categorical'` (default) the name/thread/depth
 * modes draw from the shared DS-2 {@link categoricalColor} scale (stable hue-per-object across views); under
 * `'curated'` they index the hand-tuned `palette` (the legacy djb2-into-SPAN_PALETTE behaviour). `duration` is the
 * sequential heat ramp under both.
 */
export function colorForSpan(
  span: { name: string; threadSlot: number; depth?: number; durationUs: number },
  mode: 'name' | 'thread' | 'depth' | 'duration',
  palette: readonly string[],
  spanPalette: SpanPaletteMode,
  isDark: boolean,
): string {
  if (mode === 'duration') {
    return durationHeatColor(span.durationUs);
  }
  if (spanPalette === 'categorical') {
    switch (mode) {
      case 'thread':
        return categoricalSpanColor(span.threadSlot, isDark);
      case 'depth':
        return categoricalSpanColor(span.depth ?? 0, isDark);
      case 'name':
      default:
        return categoricalSpanColor(span.name, isDark);
    }
  }
  switch (mode) {
    case 'thread':
      return palette[span.threadSlot % palette.length];
    case 'depth':
      return palette[(span.depth ?? 0) % palette.length];
    case 'name':
    default:
      return palette[hashString(span.name) % palette.length];
  }
}

/**
 * Colour for a scheduler chunk bar. Chunks are the top-level unit of work scheduled onto a thread,
 * so they're conceptually all at depth 0 (no nesting). In `depth` mode they share one fixed slot —
 * `palette[0]` — and only the nested span bars below differentiate by depth, which is what makes
 * the "Depth" lens visually meaningful (otherwise depth mode would look identical to name mode for
 * the entire chunk row, since chunks would still differ by `systemIndex`). The other modes mirror
 * {@link colorForSpan}: thread picks by `threadSlot`, duration runs the heat ramp.
 *
 * Why this exists alongside `colorForSpan`: chunks are a distinct draw path (top-of-slot bar with
 * label, vs. nested span bars below). Routing both through one switch keeps the user's "Color by …"
 * choice consistent across the whole timeline rather than only affecting the inner spans.
 */
export function colorForChunk(
  chunk: { systemIndex: number; systemName: string; threadSlot: number; durationUs: number },
  mode: 'name' | 'thread' | 'depth' | 'duration',
  palette: readonly string[],
  spanPalette: SpanPaletteMode,
  isDark: boolean,
): string {
  if (mode === 'duration') {
    return durationHeatColor(chunk.durationUs);
  }
  if (spanPalette === 'categorical') {
    switch (mode) {
      case 'thread':
        return categoricalSpanColor(chunk.threadSlot, isDark);
      case 'depth':
        // Chunks are all depth 0 — one shared slot keeps "Depth" meaningful (only nested spans differ by depth).
        return categoricalSpanColor(0, isDark);
      case 'name':
      default:
        // Keyed on systemName (not systemIndex) so a system reads the same hue here as in the DAG / matrix / Query Analyzer.
        return categoricalSpanColor(chunk.systemName, isDark);
    }
  }
  switch (mode) {
    case 'thread':
      return palette[chunk.threadSlot % palette.length];
    case 'depth':
      return palette[0];
    case 'name':
    default:
      return palette[chunk.systemIndex % palette.length];
  }
}
