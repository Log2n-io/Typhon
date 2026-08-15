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
  /** u64-as-string bitmask of the systems that ran this tick. Absent on minimal callers; `0`/null on a tick with none. */
  activeSystemsBitmask?: string | number | null;
  /** Longest single system execution this tick. `0` when no system-scoped record was retained. */
  maxSystemDurationUs?: number | string;
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
/**
 * Does this tick carry anything to look at?
 *
 * On-demand tick capture (#805) keeps a per-tick SKELETON for every tick — `TickStart`/`TickEnd`, the metronome wait,
 * the overload counters, the gauge snapshot — so tick numbering stays absolute across windows the operator never armed.
 * Those ticks still finalize into summaries, so without this predicate the overview draws a bar per engine tick and a
 * hundred recorded ticks are lost in thousands of empty ones. Clicking such a bar selects a tick with no systems, no
 * spans and nothing in the detail panel.
 *
 * <p><b>Only the timeline filters.</b> Engine Live Health reads the same summaries for tick rate, gauges and anomalies
 * and must keep seeing every tick — that panel is what you watch while deciding when to press Record.</p>
 *
 * <b>Summaries carrying neither field</b> deliberately survive: a caller that supplies only the core four cannot be
 * judged, and guessing "not recorded" would blank the strip entirely. Absence of evidence is not evidence of absence.
 *
 * The synthetic pre-tick summary (tick 0) is <i>not</i> special-cased here — it has no systems by construction, so it
 * fails this test. {@link recordedTicksOnly} decides its fate, because whether it is worth showing depends on its
 * neighbour rather than on itself.
 */
export function isRecordedTick(s: TickSummaryLike): boolean {
  const bitmask = s.activeSystemsBitmask;
  const hasBitmask = bitmask !== undefined && bitmask !== null;
  const hasMaxDuration = s.maxSystemDurationUs !== undefined;
  if (!hasBitmask && !hasMaxDuration) return true;

  // Number() on a u64 beyond 2^53 loses precision, but never turns a non-zero value into zero — and zero is the only
  // thing being tested. Both fields are consulted because the bitmask only tracks system indices below 64, so a DAG
  // wider than that would report 0 for a tick whose systems genuinely ran.
  if (hasBitmask && Number(bitmask) !== 0) return true;
  return hasMaxDuration && Number(s.maxSystemDurationUs) > 0;
}

/**
 * How many tick numbers are missing immediately before `index`, or 0 when the axis is continuous there.
 *
 * Cherry-picked capture makes the tick axis **discontinuous**: recording ticks 1–4 and then 8–10 leaves the overview
 * drawing seven bars whose numbers jump 4 → 8. Without a marker those bars read as one uninterrupted run, so a spike
 * at tick 8 looks like it happened right after tick 4 — the strip would be quietly lying about time.
 *
 * Returns a count rather than a boolean because the size of the gap is what the reader wants: "3 ticks skipped" and
 * "4,000 ticks skipped" are the same picture but very different runs.
 */
export function missingTicksBefore(ticks: readonly TickRow[], index: number): number {
  if (index <= 0 || index >= ticks.length) return 0;
  const gap = ticks[index].tickNumber - ticks[index - 1].tickNumber - 1;
  // Negative would mean the list is not ascending — never true for summaries, and reporting a "gap" for it would be
  // an invention. Zero is the honest answer for anything that is not a forward jump.
  return gap > 0 ? gap : 0;
}

/** Width of the hatched band inserted where the tick axis jumps. */
export const GAP_BAND_WIDTH = 6;

/**
 * X offset of each visible bar, relative to {@link BAR_LEFT_PAD}, with a {@link GAP_BAND_WIDTH} slot **inserted**
 * wherever the tick axis jumps.
 *
 * The band occupies real space rather than overpainting the bars either side of it. Overpainting was the first
 * attempt and it is wrong twice over: it mutilates two bars that carry data, and it still presents them as adjacent
 * — the reader sees one continuous run with a mark drawn on it, instead of a run that visibly stops and restarts.
 *
 * Returned as a table because every consumer needs the same arithmetic — bars, GC markers, the selection overlay,
 * tick labels, the hover outline and hit-testing. Nine sites computing `(i - startIdx) * BAR_WIDTH` independently is
 * exactly how a strip ends up where the bar you click is not the bar you hit.
 */
export function computeBarOffsets(
  ticks: readonly TickRow[],
  startIdx: number,
  endIdx: number,
): number[] {
  const count = Math.max(0, endIdx - startIdx);
  const offsets = new Array<number>(count);
  let x = 0;
  for (let k = 0; k < count; k++) {
    // No band before the first visible bar: there is no visible left-hand neighbour for it to separate. The break is
    // still real, but it is off-screen, and a leading band would only push the strip right for no reader benefit.
    if (k > 0 && missingTicksBefore(ticks, startIdx + k) > 0) {
      x += GAP_BAND_WIDTH;
    }
    offsets[k] = x;
    x += BAR_WIDTH;
  }
  return offsets;
}

/**
 * How many bars fit in `availablePx`, counting the gap bands that have to be inserted among them.
 *
 * Sizing the window as `availablePx / BAR_WIDTH` ignores that space and overflows the canvas by exactly the total
 * band width — the right-hand bars would be drawn past the edge and silently clipped.
 */
export function barsThatFit(ticks: readonly TickRow[], startIdx: number, availablePx: number): number {
  if (availablePx <= 0) return 0;
  let used = 0;
  let fitted = 0;
  for (let k = 0; startIdx + k < ticks.length; k++) {
    const band = k > 0 && missingTicksBefore(ticks, startIdx + k) > 0 ? GAP_BAND_WIDTH : 0;
    if (used + band + BAR_WIDTH > availablePx) break;
    used += band + BAR_WIDTH;
    fitted++;
  }
  return fitted;
}

/**
 * The overview's input: the recorded ticks, in order. Returns the original array untouched when nothing is filtered,
 * so the common case (a trace, or an attach recording everything) keeps referential identity for `useMemo`.
 */
export function recordedTicksOnly<T extends TickSummaryLike>(
  summaries: readonly T[] | null | undefined,
): readonly T[] | null | undefined {
  if (!summaries || summaries.length === 0) return summaries;
  const kept = summaries.filter(isRecordedTick);

  // The synthetic tick 0 — the pre-tick engine-setup window — is worth a bar only when it ABUTS the run being shown,
  // i.e. the recording starts at tick 1. Then it is the "before the loop started" slot the user clicks to reach setup
  // events, and it sits flush against tick 1 with no discontinuity.
  //
  // In a cherry-picked capture it usually does not abut anything: record a window at tick 133 and you get a stub
  // holding whatever arrived before the first TickStart, followed by a 132-tick gap. That bar is not the setup window
  // for anything on screen — it is a fragment with a separator after it, and both are noise.
  const first = summaries[0];
  if (first && Number(first.tickNumber) === 0 && !isRecordedTick(first) && kept.length > 0 && Number(kept[0].tickNumber) === 1) {
    kept.unshift(first);
  }

  return kept.length === summaries.length ? summaries : kept;
}

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

const OVERLAY_COLOR = OVERVIEW_PALETTE.selection + '80';
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
  // Every x in this function comes from here, so bars, markers, overlay, labels and hit-testing cannot drift apart
  // once discontinuity bands start consuming horizontal space.
  const barOffsets = computeBarOffsets(ticks, sr.startIdx, sr.endIdx);
  const barX = (index: number) => BAR_LEFT_PAD + barOffsets[index - sr.startIdx];

  // Bars. Minimum 1 px height floor so very-short ticks (e.g. a fast ForceCheckpoint) stay visible.
  // A second pass below draws the GC marker triangles so they overlay the bars without being clipped by
  // tall bars in subsequent iterations.
  for (let i = sr.startIdx; i < sr.endIdx; i++) {
    const tick = ticks[i];
    const ratio = Math.min(tick.durationUs / p95, 1.0);
    const barH = Math.max(1, ratio * barAreaHeight);
    const x = barX(i);
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
      const x = barX(i);
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

  // Discontinuity markers — where the tick axis jumps.
  //
  // Drawn in the 1-px lane the bar pass already leaves between bars, so the layout, hit-testing and every index
  // calculation downstream are untouched: this is annotation, not spacing. A cherry-picked capture holds ticks 1–4 and
  // 8–10 side by side, and without this the strip presents them as one continuous run — the reader would measure a
  // spike at tick 8 as following immediately from tick 4.
  //
  // A hatched band rather than a line: diagonal stripes are the conventional "axis break" mark, and they cannot be
  // mistaken for data the way a vertical rule sitting among vertical bars can. The band is cleared to the strip
  // background first, so it reads as a CUT through the strip instead of an overlay on top of it.
  {
    const gapTop = barAreaTop;
    const gapBottom = barAreaTop + barAreaHeight;
    // The band sits in space `computeBarOffsets` already reserved for it — the bar to its right was pushed along by
    // exactly this width — so it covers no data at all. Starting at startIdx + 1 matches that layout: no space is
    // reserved before the first visible bar, and a band drawn there would sit on top of it.
    // One pixel narrower than the slot, matching the `barWidth - 1` convention the bar pass uses: the bar to the LEFT
    // already ends one pixel short, so painting the full slot would leave the band flush against the right-hand bar and
    // separated from the left one. Inset keeps a lane on both sides and the band visually centred in its own space.
    const bandWidth = GAP_BAND_WIDTH - 1;
    for (let i = sr.startIdx + 1; i < sr.endIdx; i++) {
      if (missingTicksBefore(ticks, i) <= 0) continue;
      const x0 = barX(i) - GAP_BAND_WIDTH;

      ctx.save();
      ctx.beginPath();
      ctx.rect(x0, gapTop, bandWidth, gapBottom - gapTop);
      ctx.clip();

      ctx.fillStyle = theme.card;
      ctx.fillRect(x0, gapTop, bandWidth, gapBottom - gapTop);

      // Mid-grey: it reads on both themes without claiming severity. A break in the axis is a fact about the recording,
      // not a problem with the run — amber sat in the same register as the throttle tints and the GC marker, which are
      // warnings.
      ctx.strokeStyle = '#8A8A8A';
      ctx.lineWidth = 1;
      ctx.beginPath();
      // 45° stripes. The loop starts a band-width above the top and ends one below the bottom so the diagonals reach
      // the corners — stopping at the exact bounds leaves untouched triangles at each end.
      for (let y = gapTop - bandWidth; y <= gapBottom + bandWidth; y += 3) {
        ctx.moveTo(x0, y + bandWidth);
        ctx.lineTo(x0 + bandWidth, y);
      }
      ctx.stroke();
      ctx.restore();
    }
  }

  // Green selection overlay — ticks overlapping viewRange.
  if (selection.first >= 0) {
    const drawFirst = Math.max(selection.first, sr.startIdx);
    const drawLast = Math.min(selection.last, sr.endIdx - 1);
    if (drawFirst <= drawLast) {
      const overlayStartX = barX(drawFirst);
      const overlayEndX = barX(drawLast) + barWidth;
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
    const x = barX(i) + barWidth / 2;
    ctx.fillText(`${ticks[i].tickNumber}`, x, height - 5);
  }

  // Drag-preview overlay (in-flight select drag).
  if (dragPreview && dragPreview.moved) {
    const a = Math.min(dragPreview.startIdx, dragPreview.currentIdx);
    const b = Math.max(dragPreview.startIdx, dragPreview.currentIdx);
    const clampedA = Math.max(sr.startIdx, a);
    const clampedB = Math.min(sr.endIdx - 1, b);
    if (clampedA <= clampedB) {
      const x1 = barX(clampedA);
      const x2 = barX(clampedB) + barWidth;
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
    const x = barX(hover.tickIdx);
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
 *
 * Reads the same offset table the draw pass uses, because bars are no longer on a uniform pitch: a discontinuity band
 * consumes {@link GAP_BAND_WIDTH} of horizontal space, so `floor(x / BAR_WIDTH)` drifts by one bar per band and every
 * click past the first gap would select the wrong tick — silently, since the wrong tick is a perfectly plausible one.
 *
 * A click that lands **inside** a band returns `-1`: there is no tick there. That is the honest answer, and it stops a
 * click in the gap from snapping the viewport to a neighbour the user did not aim at.
 */
export function hitTestTick(
  mouseX: number,
  _canvasWidth: number,
  scrollWindow: { startIdx: number; endIdx: number },
  barOffsets: readonly number[],
): number {
  const visibleCount = Math.min(scrollWindow.endIdx - scrollWindow.startIdx, barOffsets.length);
  if (visibleCount <= 0) return -1;
  const offsetMouseX = mouseX - BAR_LEFT_PAD;
  if (offsetMouseX < 0) return -1;

  // Offsets ascend, so the last bar starting at or before the cursor is the only candidate; anything beyond its width
  // is either a band (bands precede the bar that follows them) or past the end of the strip.
  for (let k = visibleCount - 1; k >= 0; k--) {
    const start = barOffsets[k];
    if (offsetMouseX < start) continue;
    return offsetMouseX < start + BAR_WIDTH ? scrollWindow.startIdx + k : -1;
  }
  return -1;
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
