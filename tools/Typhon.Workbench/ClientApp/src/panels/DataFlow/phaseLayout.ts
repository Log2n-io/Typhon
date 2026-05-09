import type { XAxisMode } from './useDataFlowViewStore';

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
