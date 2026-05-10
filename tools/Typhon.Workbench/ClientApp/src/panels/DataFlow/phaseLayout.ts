import type { XAxisMode } from './useDataFlowViewStore';

/**
 * Per-phase wall-clock contribution + per-tick phase span data, computed from SystemTickSummary[] + the
 * declared phase order + the system→phase map. Drives the X-axis layout (segments) and the per-tick bar
 * positioning inside each phase column (a bar's sub-tick µs offset becomes a fraction of its phase's
 * span, which then maps to a position inside the segment's normalized [xStart, xEnd]).
 *
 * Computed once per (range, topology) change; reused by both bar building and phase-fence rendering.
 */
export interface PhaseAxis {
  /** Per-phase X segment in [0, 1] (output of {@link computePhaseLayout}). */
  readonly segments: readonly PhaseSegment[];
  /**
   * For each tick in the data slice: per-phase tick-local span. Keyed by tick number → phase name →
   * `{ startUs, endUs }` where startUs/endUs are TICK-RELATIVE µs (matching SystemTickSummary's convention).
   * Empty for ticks that have no system summaries; consumers fall back to "spread across the segment".
   */
  readonly tickPhaseSpans: ReadonlyMap<number, ReadonlyMap<string, { startUs: number; endUs: number }>>;
}

/**
 * Pure X-axis layout for the Data Flow Timeline. The Marey chart's X axis is segmented per phase per design §6.1
 * — phases aren't a filter, they're the structural skeleton of the tick. Three modes let the user pick how those
 * phase columns are sized:
 *
 * - <b>uniform</b> — column width proportional to wall-clock contribution. Honest representation; default.
 * - <b>equal</b>   — every column gets <code>1/N</code> of screen. Better for "is each phase efficient internally?"
 * - <b>log</b>     — log-time compression so the dominant phase doesn't crush the smaller ones.
 *
 * Output: an array of segments with normalized `[xStart, xEnd]` values in [0, 1]. Consumers multiply by the
 * timeline's pixel width to position phase fences and bars. Segments are guaranteed contiguous (segment[i].xEnd
 * == segment[i+1].xStart) and the last segment ends at exactly 1.
 */
export interface PhaseSegment {
  /** Phase name as it appears in `TopologyDto.phases`. */
  readonly name: string;
  /** Wall-clock micros contributed by this phase (input). */
  readonly wallClockUs: number;
  /** Normalized [0, 1] start position along the timeline. */
  readonly xStart: number;
  /** Normalized [0, 1] end position. Always > xStart. */
  readonly xEnd: number;
}

/**
 * Compute per-phase X segments. The input is a list of (phase name, wall-clock contribution) pairs in
 * declared phase order. Empty/zero contributions still appear in the output as zero-width segments at
 * the appropriate position — `equal` mode makes them visible, `uniform`/`log` collapse them flush with
 * the next fence.
 *
 * Returns an empty array when the input is empty (timeline has no segments to draw).
 */
export function computePhaseLayout(
  phases: readonly { name: string; wallClockUs: number }[],
  mode: XAxisMode,
): PhaseSegment[] {
  if (phases.length === 0) return [];

  switch (mode) {
    case 'uniform':
      return computeUniform(phases);
    case 'equal':
      return computeEqual(phases);
    case 'log':
      return computeLog(phases);
  }
}

/**
 * Each column sized proportional to its share of total wall-clock. When the total is zero (every phase contributed
 * nothing — degenerate case from a pre-tick state or an idle session), falls back to `equal` so columns are still
 * visible rather than collapsed to a single zero-width strip.
 */
function computeUniform(phases: readonly { name: string; wallClockUs: number }[]): PhaseSegment[] {
  let total = 0;
  for (const p of phases) {
    total += Math.max(0, p.wallClockUs);
  }
  if (total <= 0) return computeEqual(phases);

  const out: PhaseSegment[] = [];
  let cursor = 0;
  for (let i = 0; i < phases.length; i++) {
    const share = Math.max(0, phases[i].wallClockUs) / total;
    const xStart = cursor;
    // Last segment locks to exactly 1 so floating-point error doesn't leave a sliver at the right edge.
    const xEnd = i === phases.length - 1 ? 1 : cursor + share;
    out.push({ name: phases[i].name, wallClockUs: phases[i].wallClockUs, xStart, xEnd });
    cursor = xEnd;
  }
  return out;
}

/**
 * Each column gets exactly `1/N` of the screen, regardless of contribution. Useful when the user wants to see
 * how "balanced" each phase looks internally without the dominant phase visually swallowing the others.
 */
function computeEqual(phases: readonly { name: string; wallClockUs: number }[]): PhaseSegment[] {
  const n = phases.length;
  const width = 1 / n;
  const out: PhaseSegment[] = [];
  for (let i = 0; i < n; i++) {
    const xStart = i * width;
    const xEnd = i === n - 1 ? 1 : (i + 1) * width;
    out.push({ name: phases[i].name, wallClockUs: phases[i].wallClockUs, xStart, xEnd });
  }
  return out;
}

/**
 * Log-time compression. Apply <code>log1p(x)</code> to each phase's contribution and use the resulting share.
 * Compresses the dominant phase so the long tail of small phases stays readable. Equivalent to `equal` when
 * every phase has the same contribution. When all contributions are zero, falls back to `equal` for visibility.
 */
function computeLog(phases: readonly { name: string; wallClockUs: number }[]): PhaseSegment[] {
  let total = 0;
  const weights: number[] = new Array(phases.length);
  for (let i = 0; i < phases.length; i++) {
    const w = Math.log1p(Math.max(0, phases[i].wallClockUs));
    weights[i] = w;
    total += w;
  }
  if (total <= 0) return computeEqual(phases);

  const out: PhaseSegment[] = [];
  let cursor = 0;
  for (let i = 0; i < phases.length; i++) {
    const share = weights[i] / total;
    const xStart = cursor;
    const xEnd = i === phases.length - 1 ? 1 : cursor + share;
    out.push({ name: phases[i].name, wallClockUs: phases[i].wallClockUs, xStart, xEnd });
    cursor = xEnd;
  }
  return out;
}

/**
 * Apply auto-collapse (D10): phases contributing less than {@link AUTO_COLLAPSE_THRESHOLD} of total wall-clock
 * are rendered as thin summary strips so they don't crowd the dominant phases off-screen. Honors a user-supplied
 * "manually expanded" set (the user has clicked these phase headers explicitly to keep them open). Manually
 * collapsed phases — explicit click on a wide phase — are also collapsed, regardless of contribution.
 *
 * Output preserves segment count and order. Collapsed segments get {@link COLLAPSED_SEGMENT_WIDTH} normalized
 * width; the remaining width is distributed across the expanded segments proportional to their existing widths.
 *
 * Pure transform — does not allocate when no segment changes status (returns the input by reference).
 */
export const AUTO_COLLAPSE_THRESHOLD = 0.05;
export const COLLAPSED_SEGMENT_WIDTH = 0.012;

export function applyPhaseCollapse(
  segments: readonly PhaseSegment[],
  totalWallClockUs: number,
  manuallyCollapsed: ReadonlySet<string>,
  manuallyExpanded: ReadonlySet<string>,
): PhaseSegment[] {
  if (segments.length === 0) return segments as PhaseSegment[];

  // Phase i is collapsed if: user-collapsed it explicitly, OR (auto-collapse triggers AND user has not explicitly
  // expanded it). Auto-collapse triggers when totalWallClockUs > 0 AND share < AUTO_COLLAPSE_THRESHOLD.
  const collapsedFlags: boolean[] = new Array(segments.length);
  let anyCollapsed = false;
  let expandedTotalWidth = 0;
  for (let i = 0; i < segments.length; i++) {
    const s = segments[i];
    let collapse = false;
    if (manuallyCollapsed.has(s.name)) {
      collapse = true;
    } else if (!manuallyExpanded.has(s.name) && totalWallClockUs > 0) {
      const share = Math.max(0, s.wallClockUs) / totalWallClockUs;
      if (share < AUTO_COLLAPSE_THRESHOLD) collapse = true;
    }
    collapsedFlags[i] = collapse;
    if (collapse) anyCollapsed = true;
    else expandedTotalWidth += s.xEnd - s.xStart;
  }
  if (!anyCollapsed) return segments as PhaseSegment[];

  // Total width that collapsed segments will consume; distribute the remainder proportionally to expanded segments
  // by their current width. Edge case: when EVERY segment is collapsed (degenerate) we just use COLLAPSED_SEGMENT_WIDTH
  // each and rescale to fill [0, 1].
  let collapsedCount = 0;
  for (let i = 0; i < collapsedFlags.length; i++) if (collapsedFlags[i]) collapsedCount++;
  let collapsedWidth = collapsedCount * COLLAPSED_SEGMENT_WIDTH;
  let scaleExpanded: number;
  if (collapsedCount === segments.length) {
    // All collapsed — give each segment 1/N regardless of COLLAPSED_SEGMENT_WIDTH.
    collapsedWidth = 1;
    scaleExpanded = 0;
  } else if (collapsedWidth >= 1) {
    // Pathological: too many collapsed segments at the configured width — clamp.
    collapsedWidth = 0.95;
    scaleExpanded = 0.05 / Math.max(expandedTotalWidth, 1e-9);
  } else {
    scaleExpanded = (1 - collapsedWidth) / Math.max(expandedTotalWidth, 1e-9);
  }
  const perCollapsed = collapsedWidth / Math.max(collapsedCount, 1);

  const out: PhaseSegment[] = [];
  let cursor = 0;
  for (let i = 0; i < segments.length; i++) {
    const s = segments[i];
    const w = collapsedFlags[i] ? perCollapsed : (s.xEnd - s.xStart) * scaleExpanded;
    const xStart = cursor;
    const xEnd = i === segments.length - 1 ? 1 : cursor + w;
    out.push({ name: s.name, wallClockUs: s.wallClockUs, xStart, xEnd });
    cursor = xEnd;
  }
  return out;
}

/**
 * Map a tick-relative µs offset (matching SystemTickSummary.startUs convention) to a position in [0, 1] phase-space.
 * Looks up the phase by `phaseName`, finds the segment in `axis.segments`, and uses `axis.tickPhaseSpans` to
 * compute the bar's intra-phase fraction.
 *
 * When the phase isn't found in segments (unknown phase, or topology has no phases), returns `null` so the caller
 * can fall back to a default placement. When the phase is found but the tick has no recorded phase span (no system
 * summaries for that tick × phase pair), spreads the bar across the full segment.
 */
export function tickOffsetToNormalizedX(
  axis: PhaseAxis | null,
  tickNumber: number,
  phaseName: string,
  startUs: number,
  endUs: number,
): { xStart: number; xEnd: number } | null {
  if (!axis) return null;
  const segment = axis.segments.find((s) => s.name === phaseName);
  if (!segment) return null;
  const segWidth = segment.xEnd - segment.xStart;
  if (segWidth <= 0) {
    // Collapsed phase — bar sits inside the strip at its start position. xEnd matches xStart so it doesn't bleed
    // into the next phase; the renderer floors at 2 px so it stays visible as a tick mark.
    return { xStart: segment.xStart, xEnd: segment.xStart };
  }
  const tickPhases = axis.tickPhaseSpans.get(tickNumber);
  const span = tickPhases?.get(phaseName);
  if (!span || span.endUs <= span.startUs) {
    return { xStart: segment.xStart, xEnd: segment.xEnd };
  }
  const denom = span.endUs - span.startUs;
  const relStart = Math.max(0, Math.min(1, (startUs - span.startUs) / denom));
  const relEnd = Math.max(0, Math.min(1, (endUs - span.startUs) / denom));
  return {
    xStart: segment.xStart + relStart * segWidth,
    xEnd: segment.xStart + relEnd * segWidth,
  };
}
