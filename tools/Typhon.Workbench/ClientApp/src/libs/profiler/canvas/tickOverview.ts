import type { TimeRange } from '@/libs/profiler/model/uiTypes';
import {
  OVERVIEW_PALETTE,
  formatDuration,
  setupCanvas,
} from './canvasUtils';
import type { StudioTheme } from './theme';

/**
 * Minimal row shape the overview strip needs. Feeds both from server-supplied `metadata.tickSummaries`
 * (trace mode — complete, pre-aggregated) and from live-tick aggregation (attach mode — filled in as
 * ticks arrive). Derived at the call site; the draw function doesn't care about the source.
 */
export interface TickRow {
  tickNumber: number;
  startUs: number;
  endUs: number;
  durationUs: number;
  eventCount: number;
  // ── v9 fields (issue #289 follow-up) — drive overview tint + tooltip diagnostics ──
  /** Effective tick-rate multiplier (1, 2, 3, 4, 6). Bars with mult>1 are tinted to expose throttling at a glance. Defaults to 1. */
  tickMultiplier?: number;
  /** OverloadDetector level at end-of-tick. 0=Normal..4=PlayerShedding. */
  overloadLevel?: number;
  /** Metronome wait µs that PRECEDED this tick. Surfaced in the hover tooltip. */
  metronomeWaitUs?: number;
  /** 0=CatchUp, 1=Throttled, 2=Headroom. */
  metronomeIntentClass?: number;
  // ── v11 fields ──
  /** OverloadDetector consecutive-overrun streak at end-of-tick. */
  consecutiveOverrun?: number;
  /** OverloadDetector consecutive-underrun streak at end-of-tick. */
  consecutiveUnderrun?: number;
}

/**
 * Minimal shape consumed by {@link buildTickRows}: must surface the core four fields, and may optionally
 * carry the v9 diagnostic fields (overload + metronome). Wider DTOs (e.g. `TickSummaryDto` from the OpenAPI
 * client) satisfy this structurally — the v9 fields are forwarded when present.
 */
export interface TickSummaryLike {
  tickNumber: number | string;
  startUs: number | string;
  durationUs: number | string;
  eventCount: number | string;
  // v9+ integer fields. Orval's .NET-OpenAPI codegen emits these as `number | string` (precision-preserving
  // dual representation, same as `tickNumber` / `eventCount` above), so the Like shape accepts both — consumers
  // coerce with `Number(...)` where they need a numeric value.
  tickMultiplier?: number | string;
  overloadLevel?: number | string;
  metronomeWaitUs?: number | string;
  metronomeIntentClass?: number | string;
  consecutiveOverrun?: number | string;
  consecutiveUnderrun?: number | string;
}

/**
 * Build {@link TickRow} entries from a tickSummaries array. Performs **boundary clamping**:
 * `endUs := Math.min(startUs + durationUs, nextTick.startUs)` so consecutive ticks always butt up exactly.
 *
 * **Why the clamp.** The engine stores `TickSummary.DurationUs` as a 32-bit float on the wire while
 * `StartUs` is a 64-bit double, so `start + duration` (computed as JS doubles after the float→double
 * widen) can drift slightly past the next tick's wire `startUs`. Without clamping, strict-less-than
 * overlap tests in {@link computeSelectionIdxRange} flip and a single-tick selection silently bleeds
 * into the next tick. The original `durationUs` is preserved unchanged for renderers that size bars from
 * it; only `endUs` is clamped.
 *
 * Real engine-idle gaps (next.startUs greater than start + duration) are preserved — the clamp uses
 * `Math.min`, so it only trims overshoot, never extends.
 */
export function buildTickRows(summaries: readonly TickSummaryLike[] | null | undefined): TickRow[] {
  if (!summaries || summaries.length === 0) return [];
  const result: TickRow[] = new Array(summaries.length);
  for (let i = 0; i < summaries.length; i++) {
    const s = summaries[i];
    const start = Number(s.startUs);
    const duration = Number(s.durationUs);
    const computedEnd = start + duration;
    const nextStart = i + 1 < summaries.length ? Number(summaries[i + 1].startUs) : Number.POSITIVE_INFINITY;
    result[i] = {
      tickNumber: Number(s.tickNumber),
      startUs: start,
      endUs: Math.min(computedEnd, nextStart),
      durationUs: duration,
      eventCount: Number(s.eventCount),
      // v9/v11 optional fields are `number | string | undefined` on the source DTO. Coerce to `number | undefined`
      // for TickRow consumers (the canvas tints + tooltips read these as numbers, not as the dual representation).
      tickMultiplier: s.tickMultiplier !== undefined ? Number(s.tickMultiplier) : undefined,
      overloadLevel: s.overloadLevel !== undefined ? Number(s.overloadLevel) : undefined,
      metronomeWaitUs: s.metronomeWaitUs !== undefined ? Number(s.metronomeWaitUs) : undefined,
      metronomeIntentClass: s.metronomeIntentClass !== undefined ? Number(s.metronomeIntentClass) : undefined,
      consecutiveOverrun: s.consecutiveOverrun !== undefined ? Number(s.consecutiveOverrun) : undefined,
      consecutiveUnderrun: s.consecutiveUnderrun !== undefined ? Number(s.consecutiveUnderrun) : undefined,
    };
  }
  return result;
}

/** Inputs to `drawTickOverview` and hit-test helpers. */
export interface TickOverviewInputs {
  ticks: TickRow[];
  /** The main graph's viewport — used to render the orange "selected ticks" overlay. */
  viewRange: TimeRange;
  /** Slice of ticks currently visible in the overview (pan state, separate from viewRange). */
  scrollWindow: { startIdx: number; endIdx: number };
  /**
   * Set true while the user is hovering the scrollbar track or actively dragging the thumb. Lets the renderer
   * brighten the thumb during interaction. Optional — falsy by default.
   */
  scrollbarHovered?: boolean;
  /** Ticks that overlap viewRange. `-1`/`-1` if no overlap. */
  selection: { first: number; last: number };
  /** In-flight drag preview, or null if no drag. */
  dragPreview: { startIdx: number; currentIdx: number; moved: boolean } | null;
  /** Hovered tick + mouse-relative coordinates, or null. */
  hover: { tickIdx: number; x: number; y: number } | null;
  /** P95 tick duration (µs) — bars clamp at this; taller ticks are drawn in a warning hue. */
  p95TickDurationUs: number;
  /** Legends + "?" help glyph visibility ('l' key toggles). */
  legendsVisible: boolean;
  /** True when the cursor is inside the help-glyph hit zone — brightens the glyph. */
  helpHovered: boolean;
  /**
   * Set of `tickNumber` values that overlap at least one GC suspension. When set, the renderer draws a small
   * yellow upward triangle at the base of each matching bar so the user can spot GC pauses at a glance.
   * Sourced from `metadata.gcSuspensions` (session-wide, available at open time — no chunk-decode dependency).
   * Undefined or empty ⇒ no markers drawn.
   */
  gcTicks?: ReadonlySet<number>;
}

export const TIMELINE_HEIGHT = 80;
/** y-offset of the top of the bar area inside the canvas. */
export const BAR_AREA_TOP = 2;
/** Pixel offset between the bottom of the bar area and the bottom of the canvas — reserved for tick-number labels and the (optional) scrollbar. */
export const BAR_AREA_BOTTOM_RESERVED = 26;
export const MAX_BAR_WIDTH = 10;
/** Per-bar floor so individual ticks stay legible. Caps visible window at `floor(width/MIN_BAR_WIDTH)` ticks. */
export const MIN_BAR_WIDTH = 4;
/** Pixel threshold separating click from drag. */
export const DRAG_THRESHOLD_PX = 3;

/**
 * Fixed bar width for the tick overview strip. One pixel wider than <see cref="MIN_BAR_WIDTH"/> so bars stay
 * stable as the user pans / resizes — no more dynamic stretch from MIN_BAR_WIDTH..MAX_BAR_WIDTH. Visible window
 * is <c>floor((width - BAR_LEFT_PAD) / BAR_WIDTH)</c> ticks; trailing bars render off-canvas as the user scrolls.
 */
export const BAR_WIDTH = MIN_BAR_WIDTH + 1;

/**
 * Left padding before the first bar. The first bar appeared half-cut without this — likely a 2-3 px parent CSS
 * clip / border. Integer pixels so <c>fillRect</c> stays pixel-aligned.
 */
export const BAR_LEFT_PAD = 3;
/**
 * Help-glyph geometry. Anchored at the top-right of the canvas (not the gutter — the overview sits alone
 * until the time-area section lands in 2b and provides a real gutter). `HELP_GLYPH_MARGIN_RIGHT` is the
 * distance from the right canvas edge to the glyph's right baseline.
 */
export const HELP_GLYPH_MARGIN_RIGHT = 8;
export const HELP_GLYPH_Y_BASELINE = 14;
export const HELP_ICON_HIT_PAD = 4;
export const HELP_ICON_GLYPH_WIDTH = 10;

/** Scrollbar track height (px). Drawn between the bar area and the tick-number labels. */
export const SCROLLBAR_HEIGHT = 5;
/** Vertical gap (px) between the bar area's bottom and the scrollbar track. */
export const SCROLLBAR_TOP_PAD = 1;
/** Minimum thumb width (px) for usability — short thumbs become un-grabbable on long traces. */
export const SCROLLBAR_MIN_THUMB_PX = 16;

const OVERLAY_COLOR = OVERVIEW_PALETTE.selection + '40';
const OVERLAY_BORDER = OVERVIEW_PALETTE.selection + 'B3';

/**
 * Multiplier → bar tint. Hex strings (no theme dependency — these encode the *severity* of throttling
 * in a stable, theme-independent ramp from amber → red). Issue #289 follow-up.
 *
 * Multiplier chain in `OverloadDetector`: `[1, 2, 3, 4, 6]`. We don't tint mult=1 (caller falls back to
 * normal/P95 colour). 2/3 are amber-orange (warning), 4 is red (significant throttle), 6 is dark-red
 * (engine has run out of headroom — running at MinTickRateHz floor). Visible at small bar widths.
 */
function multiplierBarTint(multiplier: number): string | null {
  if (multiplier <= 1) return null;
  if (multiplier === 2) return '#d97706'; // amber-600
  if (multiplier === 3) return '#ea580c'; // orange-600
  if (multiplier === 4) return '#dc2626'; // red-600
  return '#991b1b';                       // red-800 (5+, including the chain's terminal value 6)
}

/**
 * Pure render entry point for the tick-overview strip. Clears + repaints the whole canvas each call —
 * rAF-driven from the React wrapper. Theme is passed in so this stays DOM-free and unit-testable.
 */
export function drawTickOverview(
  canvas: HTMLCanvasElement,
  inputs: TickOverviewInputs,
  theme: StudioTheme,
): void {
  const { width, height } = setupCanvas(canvas);
  const ctx = canvas.getContext('2d');
  if (!ctx) return;

  const { ticks, scrollWindow: sr, selection, dragPreview, hover, p95TickDurationUs, legendsVisible, helpHovered, gcTicks } = inputs;
  const p95 = p95TickDurationUs || 1;
  const visibleCount = sr.endIdx - sr.startIdx;
  if (visibleCount <= 0) return;

  // Background
  ctx.fillStyle = theme.card;
  ctx.fillRect(0, 0, width, height);

  // Bottom border
  ctx.strokeStyle = theme.border;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(0, height - 0.5);
  ctx.lineTo(width, height - 0.5);
  ctx.stroke();

  const barAreaHeight = height - BAR_AREA_BOTTOM_RESERVED;
  const barAreaTop = BAR_AREA_TOP;

  // P95 reference dashed line — drawn before bars so bars visually sit "under the ceiling". The P95 LABEL
  // is drawn much later (after bars + overlay) so its backdrop stays on top and actually reads.
  ctx.strokeStyle = theme.mutedForeground;
  ctx.lineWidth = 0.5;
  ctx.setLineDash([4, 4]);
  ctx.beginPath();
  ctx.moveTo(0, barAreaTop);
  ctx.lineTo(width, barAreaTop);
  ctx.stroke();
  ctx.setLineDash([]);

  const barWidth = BAR_WIDTH;

  // Bars. Minimum 1 px height floor so very-short ticks (e.g. a fast ForceCheckpoint) stay visible.
  // A second pass below draws the GC marker triangles so they overlay the bars without being clipped by
  // tall bars in subsequent iterations.
  for (let i = sr.startIdx; i < sr.endIdx; i++) {
    const tick = ticks[i];
    const ratio = Math.min(tick.durationUs / p95, 1.0);
    const barH = Math.max(1, ratio * barAreaHeight);
    const x = BAR_LEFT_PAD + (i - sr.startIdx) * barWidth;
    const y = barAreaTop + barAreaHeight - barH;

    // Throttle tint takes priority over P95 colouring — a tick where the engine has slowed itself is
    // almost always going to also exceed the previous P95 (the throttle was triggered by sustained
    // overruns), so we don't want the P95 hue to mask the throttle severity. v9-only data; falls
    // through to the existing P95/normal colour scheme for v8 traces (multiplier defaults to 0/1).
    const throttleTint = tick.tickMultiplier && tick.tickMultiplier > 1
      ? multiplierBarTint(tick.tickMultiplier)
      : null;
    ctx.fillStyle = throttleTint ?? (tick.durationUs > p95 ? theme.overviewP95 : theme.overviewBar);
    // Integer coords + width-1 leaves a 1-px gap between bars without sub-pixel anti-aliasing.
    ctx.fillRect(x, y, Math.max(barWidth - 1, 1), barH);
  }

  // GC marker — small upward triangle at the base of every bar whose tick overlapped a GC suspension.
  // Drawn after the bar pass so the triangle always lays on top of the bar fill (no clip issues for
  // bars that grew tall after a shorter neighbour). Theme-independent yellow — perf signal, not theme chrome.
  if (gcTicks && gcTicks.size > 0) {
    const baseY = barAreaTop + barAreaHeight - 2;
    const halfW = 4;
    const height = 7;
    ctx.fillStyle = '#F6D85C';
    ctx.strokeStyle = '#404040';
    ctx.lineWidth = 1;
    for (let i = sr.startIdx; i < sr.endIdx; i++) {
      const tick = ticks[i];
      if (!gcTicks.has(tick.tickNumber)) continue;
      const x = BAR_LEFT_PAD + (i - sr.startIdx) * barWidth;
      const cx = x + Math.floor((barWidth - 1) / 2);
      ctx.beginPath();
      ctx.moveTo(cx, baseY - height);
      ctx.lineTo(cx - halfW, baseY);
      ctx.lineTo(cx + halfW, baseY);
      ctx.closePath();
      ctx.fill();
      ctx.stroke();
    }
  }

  // Orange selection overlay — ticks overlapping viewRange.
  if (selection.first >= 0) {
    const drawFirst = Math.max(selection.first, sr.startIdx);
    const drawLast = Math.min(selection.last, sr.endIdx - 1);
    if (drawFirst <= drawLast) {
      const overlayStartX = BAR_LEFT_PAD + (drawFirst - sr.startIdx) * barWidth;
      const overlayEndX = BAR_LEFT_PAD + (drawLast - sr.startIdx + 1) * barWidth;
      ctx.fillStyle = OVERLAY_COLOR;
      ctx.fillRect(overlayStartX, barAreaTop, overlayEndX - overlayStartX, barAreaHeight);
      ctx.strokeStyle = OVERLAY_BORDER;
      ctx.lineWidth = 1.5;
      ctx.strokeRect(overlayStartX, barAreaTop, overlayEndX - overlayStartX, barAreaHeight);

      // "N frames" caption (total selection — not clamped — so the number stays stable as bars scroll).
      const totalFrames = selection.last - selection.first + 1;
      const label = totalFrames === 1 ? '1 frame' : `${totalFrames} frames`;
      ctx.font = '10px monospace';
      const textWidth = ctx.measureText(label).width;
      if (textWidth + 12 <= overlayEndX - overlayStartX) {
        ctx.fillStyle = theme.foreground;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(label, (overlayStartX + overlayEndX) / 2, barAreaTop + barAreaHeight / 2);
        ctx.textBaseline = 'alphabetic';
      }
    }

    // Edge chevrons when selection extends past the visible window.
    const cy = barAreaTop + barAreaHeight / 2;
    if (selection.first < sr.startIdx) {
      ctx.fillStyle = OVERLAY_BORDER;
      ctx.beginPath();
      ctx.moveTo(6, cy);
      ctx.lineTo(12, cy - 5);
      ctx.lineTo(12, cy + 5);
      ctx.closePath();
      ctx.fill();
    }
    if (selection.last >= sr.endIdx) {
      ctx.fillStyle = OVERLAY_BORDER;
      ctx.beginPath();
      ctx.moveTo(width - 6, cy);
      ctx.lineTo(width - 12, cy - 5);
      ctx.lineTo(width - 12, cy + 5);
      ctx.closePath();
      ctx.fill();
    }
  }

  // Tick number labels — spaced at ~60 px min to avoid overlap.
  ctx.fillStyle = theme.mutedForeground;
  ctx.font = '10px monospace';
  ctx.textAlign = 'center';
  const labelEvery = Math.max(1, Math.floor(60 / barWidth));
  for (let i = sr.startIdx; i < sr.endIdx; i += labelEvery) {
    const x = BAR_LEFT_PAD + (i - sr.startIdx) * barWidth + barWidth / 2;
    ctx.fillText(`${ticks[i].tickNumber}`, x, height - 5);
  }

  // Drag-preview overlay (in-flight select drag).
  if (dragPreview && dragPreview.moved) {
    const a = Math.min(dragPreview.startIdx, dragPreview.currentIdx);
    const b = Math.max(dragPreview.startIdx, dragPreview.currentIdx);
    const clampedA = Math.max(sr.startIdx, a);
    const clampedB = Math.min(sr.endIdx - 1, b);
    if (clampedA <= clampedB) {
      const x1 = BAR_LEFT_PAD + (clampedA - sr.startIdx) * barWidth;
      const x2 = BAR_LEFT_PAD + (clampedB - sr.startIdx + 1) * barWidth;
      ctx.fillStyle = OVERVIEW_PALETTE.selection + '30';
      ctx.fillRect(x1, barAreaTop, x2 - x1, barAreaHeight);
      ctx.strokeStyle = OVERLAY_BORDER;
      ctx.setLineDash([4, 3]);
      ctx.lineWidth = 1;
      ctx.strokeRect(x1, barAreaTop, x2 - x1, barAreaHeight);
      ctx.setLineDash([]);

      // Live "N frames" caption during drag (uses unclamped range).
      const dragFrames = b - a + 1;
      const dragLabel = dragFrames === 1 ? '1 frame' : `${dragFrames} frames`;
      ctx.font = '11px monospace';
      const dragTextWidth = ctx.measureText(dragLabel).width;
      if (dragTextWidth + 12 <= x2 - x1) {
        ctx.fillStyle = theme.foreground;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillText(dragLabel, (x1 + x2) / 2, barAreaTop + barAreaHeight / 2);
        ctx.textBaseline = 'alphabetic';
      }
    }
  }

  // "P95: X" label at top-left — backdrop + text drawn AFTER bars/overlay so it stays legible over any bar
  // that pokes up to the top of the strip. Same adaptive tooltip bg/text as the "?" glyph.
  ctx.font = '11px monospace';
  ctx.textAlign = 'left';
  ctx.textBaseline = 'alphabetic';
  const p95Label = `P95: ${formatDuration(p95)}`;
  const p95LabelWidth = ctx.measureText(p95Label).width;
  ctx.fillStyle = theme.tooltipBackground;
  ctx.fillRect(2, barAreaTop + 1, p95LabelWidth + 6, 13);
  ctx.fillStyle = theme.mutedForeground;
  ctx.fillText(p95Label, 5, barAreaTop + 11);

  // Help "?" glyph — anchored at the top-right of the canvas with a theme-aware backdrop so the glyph reads
  // in both themes regardless of what bars sit under it (bars fill edge-to-edge in this section).
  if (legendsVisible) {
    ctx.textAlign = 'right';
    ctx.font = 'bold 11px monospace';
    const glyphRight = width - HELP_GLYPH_MARGIN_RIGHT;
    const bgW = HELP_ICON_GLYPH_WIDTH + 6;
    const bgH = 14;
    ctx.fillStyle = theme.tooltipBackground;
    ctx.fillRect(glyphRight - bgW + 3, HELP_GLYPH_Y_BASELINE - 11, bgW, bgH);
    ctx.fillStyle = helpHovered ? theme.foreground : theme.mutedForeground;
    ctx.fillText('?', glyphRight, HELP_GLYPH_Y_BASELINE);
  }

  // Hover outline — the tooltip itself is a DOM overlay rendered BELOW the canvas by the React
  // wrapper (see TickOverview.tsx) so it doesn't obstruct adjacent bars in the strip. The canvas
  // draw pass only highlights the hovered bar; content goes through HelpOverlay.
  if (!helpHovered && hover && hover.tickIdx >= sr.startIdx && hover.tickIdx < sr.endIdx) {
    const x = BAR_LEFT_PAD + (hover.tickIdx - sr.startIdx) * barWidth;
    // Primary accent — chromatic outline that reads as "this is hovered" in both themes without feeling as
    // heavy as a jet-black foreground stroke does against a pale card in light mode.
    ctx.strokeStyle = theme.primary;
    ctx.lineWidth = 1.5;
    ctx.strokeRect(x, barAreaTop, barWidth, barAreaHeight);
  }

  // Horizontal scrollbar — drawn only when the visible window doesn't cover all ticks. Sits between the bar
  // area and the tick-number labels. Track = muted background; thumb = primary accent (brightened on hover).
  // Geometry mirrors `computeScrollbarGeometry` so hit-tests in the React wrapper line up exactly.
  const sbg = computeScrollbarGeometry(width, ticks.length, sr, barAreaTop, barAreaHeight);
  if (sbg) {
    ctx.fillStyle = theme.muted;
    ctx.fillRect(sbg.trackX, sbg.trackY, sbg.trackW, sbg.trackH);
    ctx.fillStyle = inputs.scrollbarHovered ? theme.primary : theme.mutedForeground;
    ctx.fillRect(sbg.thumbX, sbg.trackY, sbg.thumbW, sbg.trackH);
  }
}

/**
 * Compute scrollbar track + thumb pixel rects for the current state. Returns <c>null</c> when the visible window
 * already covers every tick (no need for a scrollbar). Shared by <see cref="drawTickOverview"/> and the React
 * wrapper's hit-test logic so click coordinates resolve to the same target the renderer drew.
 */
export function computeScrollbarGeometry(
  canvasWidth: number,
  totalTicks: number,
  scrollWindow: { startIdx: number; endIdx: number },
  barAreaTop: number,
  barAreaHeight: number,
): { trackX: number; trackY: number; trackW: number; trackH: number; thumbX: number; thumbW: number } | null {
  const visibleCount = scrollWindow.endIdx - scrollWindow.startIdx;
  if (totalTicks <= 0 || visibleCount <= 0 || visibleCount >= totalTicks) {
    return null;
  }
  const trackX = 0;
  const trackY = barAreaTop + barAreaHeight + SCROLLBAR_TOP_PAD;
  const trackW = canvasWidth;
  const trackH = SCROLLBAR_HEIGHT;
  // Thumb width is proportional to the fraction of total ticks we can see, with a usability floor.
  const proportional = (visibleCount / totalTicks) * trackW;
  const thumbW = Math.max(SCROLLBAR_MIN_THUMB_PX, proportional);
  // Thumb left = startIdx normalized to [0, 1] mapped to [0, trackW - thumbW] so the thumb's right edge stops
  // exactly at the track's right edge when scrollWindow is fully right-justified.
  const maxStartIdx = totalTicks - visibleCount;
  const startFrac = maxStartIdx > 0 ? scrollWindow.startIdx / maxStartIdx : 0;
  const thumbX = startFrac * (trackW - thumbW);
  return { trackX, trackY, trackW, trackH, thumbX, thumbW };
}

/**
 * Hit-test for the scrollbar. Returns a <c>"thumb"</c> hit if the pointer is within the thumb (drag start), a
 * <c>"track"</c> hit if it's elsewhere on the track (jump-to-here), or <c>null</c> if no scrollbar interaction.
 */
export function hitTestScrollbar(
  mouseX: number,
  mouseY: number,
  canvasWidth: number,
  totalTicks: number,
  scrollWindow: { startIdx: number; endIdx: number },
  barAreaTop: number,
  barAreaHeight: number,
): { kind: 'thumb' | 'track'; thumbX: number; thumbW: number } | null {
  const sbg = computeScrollbarGeometry(canvasWidth, totalTicks, scrollWindow, barAreaTop, barAreaHeight);
  if (sbg == null) {
    return null;
  }
  // Generous vertical hit pad so the 5-px-tall scrollbar isn't fiddly to grab.
  const hitPad = 4;
  if (mouseY < sbg.trackY - hitPad || mouseY > sbg.trackY + sbg.trackH + hitPad) {
    return null;
  }
  if (mouseX < sbg.trackX || mouseX > sbg.trackX + sbg.trackW) {
    return null;
  }
  const onThumb = mouseX >= sbg.thumbX && mouseX <= sbg.thumbX + sbg.thumbW;
  return { kind: onThumb ? 'thumb' : 'track', thumbX: sbg.thumbX, thumbW: sbg.thumbW };
}

/**
 * Translate an in-canvas mouse X to the tick index under it (within the current visible window), or `-1`.
 * Bar width is the fixed <see cref="BAR_WIDTH"/>. <c>canvasWidth</c> kept in the signature for symmetry with
 * <see cref="hitTestScrollbar"/> even though it isn't read — callers always pass it.
 */
export function hitTestTick(
  mouseX: number,
  _canvasWidth: number,
  scrollWindow: { startIdx: number; endIdx: number },
): number {
  const visibleCount = scrollWindow.endIdx - scrollWindow.startIdx;
  if (visibleCount <= 0) return -1;
  const offsetMouseX = mouseX - BAR_LEFT_PAD;
  if (offsetMouseX < 0 || offsetMouseX >= visibleCount * BAR_WIDTH) return -1;
  return scrollWindow.startIdx + Math.floor(offsetMouseX / BAR_WIDTH);
}

/**
 * Binary-search the `[first, last]` index range of ticks overlapping `viewRange`. Strict half-open semantics
 * — two neighbouring ticks that merely kiss boundaries never both count as "selected". Returns `{-1, -1}`
 * when no tick overlaps.
 */
export function computeSelectionIdxRange(ticks: TickRow[], viewRange: TimeRange): { first: number; last: number } {
  if (ticks.length === 0) return { first: -1, last: -1 };
  let lo = 0;
  let hi = ticks.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (ticks[mid].endUs > viewRange.startUs) hi = mid;
    else lo = mid + 1;
  }
  const first = lo;
  if (first >= ticks.length || ticks[first].startUs >= viewRange.endUs) {
    return { first: -1, last: -1 };
  }
  lo = first;
  hi = ticks.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (ticks[mid].startUs < viewRange.endUs) lo = mid + 1;
    else hi = mid;
  }
  return { first, last: lo - 1 };
}

/** True when canvas-space `(mx, my)` falls inside the "?" help glyph's hit zone. */
export function isInHelpHitZone(mx: number, my: number, canvasWidth: number, legendsVisible: boolean): boolean {
  if (!legendsVisible) return false;
  const glyphRightX = canvasWidth - HELP_GLYPH_MARGIN_RIGHT;
  const glyphLeftX = glyphRightX - HELP_ICON_GLYPH_WIDTH;
  const glyphTop = HELP_GLYPH_Y_BASELINE - 11;
  const glyphBottom = HELP_GLYPH_Y_BASELINE + 3;
  return mx >= glyphLeftX - HELP_ICON_HIT_PAD
    && mx <= glyphRightX + HELP_ICON_HIT_PAD
    && my >= glyphTop - HELP_ICON_HIT_PAD
    && my <= glyphBottom + HELP_ICON_HIT_PAD;
}
