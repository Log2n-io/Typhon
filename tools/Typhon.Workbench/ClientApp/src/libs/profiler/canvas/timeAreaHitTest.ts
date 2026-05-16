import type { ChunkSpan, OffCpuInterval, OffCpuStore, PhaseMarker, PhaseSpan, SpanData, TickData } from '@/libs/profiler/model/traceModel';
import { materializeOffCpuInterval } from '@/libs/profiler/model/traceModel';
import type { TrackLayout, Viewport } from '@/libs/profiler/model/uiTypes';
import {
  LABEL_ROW_HEIGHT,
  MINI_ROW_HEIGHT,
  PHASE_TRACK_HEIGHT,
  SPAN_ROW_HEIGHT,
  TRACK_GAP,
} from './timeAreaLayout';
import {
  HELP_ICON_BG_HEIGHT,
  HELP_ICON_GLYPH_WIDTH,
  HELP_ICON_HIT_PAD,
  HELP_ICON_RIGHT_PAD,
} from './timeArea';

/**
 * Pure hit-test for the main time area. Originally ported from inline logic in the retired
 * `Typhon.Profiler.Server/ClientApp/src/GraphArea.tsx` (`onMouseMove`/`onMouseUp`,
 * approximately lines 1816–2205).
 *
 * Takes everything it needs as arguments so it can be unit-tested against synthetic inputs.
 * Caller (React wrapper) converts a returned hover into either a hover-tooltip or a
 * `useProfilerSelectionStore.setSelected()` write, depending on whether a click is in flight.
 */

export type TimeAreaHover =
  | { kind: 'chunk'; chunk: ChunkSpan; trackId: string }
  | { kind: 'span'; span: SpanData; trackId: string }
  | { kind: 'phase'; phase: PhaseSpan; tickNumber: number }
  | { kind: 'phase-marker'; marker: PhaseMarker; tickNumber: number }
  | { kind: 'mini-row-op'; op: SpanData; trackId: string; rowLabel: string }
  | { kind: 'off-cpu'; interval: OffCpuInterval; trackId: string }  // off-CPU overlay bar on a slot lane
  | { kind: 'tick'; tickNumber: number }  // click on ruler or empty area within a slot lane
  | { kind: 'gutter-chevron'; trackId: string }  // click toggles collapse state
  | { kind: 'help'; trackId: string; label: string }  // hover on "?" glyph in a track's gutter
  | { kind: 'gauge'; trackId: string; localY: number; trackHeight: number; cursorUs: number }
  | null;

export interface HitTestInputs {
  mx: number;
  my: number;
  tracks: readonly TrackLayout[];
  /**
   * All loaded ticks — NOT just the visible subset. Long-running spans/ops (like Checkpoint.Cycle)
   * are stored in the tick where they *started*, not where they *end*. If a span started 5 ticks
   * before the current viewport but extends into it, we still need to find it.
   *
   * The internal helpers iterate the full array but return early via cheap per-tick skips
   * (`tick.startUs > us + tolUs` break, `endMax[last] < us - tolUs` continue), so the cost is
   * a few µs per hit even on large traces.
   */
  ticks: readonly TickData[];
  vp: Viewport;
  gutterWidth: number;
  /** Whether the "?" glyph is being rendered (same gate the draw pass uses). */
  legendsVisible: boolean;
  /** Slot → off-CPU interval store. Used to resolve a click on an off-CPU overlay bar. */
  offCpuBySlot: Map<number, OffCpuStore>;
  /** Whether the off-CPU overlay is shown — when false the off-CPU probe is skipped (matches the draw gate). */
  showOffCpu: boolean;
}

/**
 * Run the hit test. Walks the track stack, matches `my` against a track's Y band, then dispatches
 * by track kind to a specialised inner test. Returns `null` when no track matches (off-canvas or
 * inside a gap).
 */
export function hitTestTimeArea(inputs: HitTestInputs): TimeAreaHover {
  const { mx, my, tracks, ticks, vp, gutterWidth, legendsVisible, offCpuBySlot, showOffCpu } = inputs;

  // Gutter — "?" help glyph wins over the chevron when hovered; else chevron toggles collapse.
  if (mx < gutterWidth) {
    const track = findTrackAtY(tracks, my, vp.scrollY);
    if (!track) return null;
    if (legendsVisible && track.id !== 'ruler' && isInHelpHitZone(mx, my, track.y - vp.scrollY, gutterWidth)) {
      return { kind: 'help', trackId: track.id, label: track.label };
    }
    if (track.collapsible) {
      return { kind: 'gutter-chevron', trackId: track.id };
    }
    return null;
  }

  // Content area — find the track that contains `my`, then dispatch.
  const track = findTrackAtY(tracks, my, vp.scrollY);
  if (!track) return null;
  const ty = track.y - vp.scrollY;

  // Ruler → nearest tick whose band contains mx.
  if (track.id === 'ruler') {
    const tick = findTickAtX(ticks, mx, gutterWidth, vp);
    if (tick !== null) return { kind: 'tick', tickNumber: tick.tickNumber };
    return null;
  }

  // Gauge tracks — any hit inside their body returns a gauge hover so the tooltip builder can
  // resolve the right sub-row. No fine-grained hit-testing per sample (tooltip shows the nearest
  // snapshot at cursorUs, not per-point).
  if (track.id.startsWith('gauge-')) {
    if (track.state === 'summary') {
      // Summary-mode gauges don't emit tooltips (spark-line preview is hint enough). Fall through
      // to the tick fallback below.
    } else {
      const cursorUs = vp.offsetX + (mx - gutterWidth) / vp.scaleX;
      return { kind: 'gauge', trackId: track.id, localY: my - ty, trackHeight: track.height, cursorUs };
    }
  }

  // Summary state → coarse tick hit (no sub-element granularity).
  if (track.state === 'summary') {
    const tick = findTickAtX(ticks, mx, gutterWidth, vp);
    if (tick !== null) return { kind: 'tick', tickNumber: tick.tickNumber };
    return null;
  }

  // Per-system lanes — single-row chunk track, chunks filtered by systemIndex. Click routes to
  // the same 'chunk' selection kind as the slot-lane chunk row.
  if (track.id.startsWith('system-')) {
    const systemIdx = Number.parseInt(track.id.slice(7), 10);
    if (my >= ty && my < ty + track.height) {
      const chunk = findSystemChunkAtX(ticks, systemIdx, mx, gutterWidth, vp);
      if (chunk) return { kind: 'chunk', chunk, trackId: track.id };
    }
    const tick = findTickAtX(ticks, mx, gutterWidth, vp);
    if (tick !== null) return { kind: 'tick', tickNumber: tick.tickNumber };
    return null;
  }

  // Slot lanes — chunk row on top, span rows below with per-depth layout.
  if (track.id.startsWith('slot-')) {
    const threadSlot = Number.parseInt(track.id.slice(5), 10);

    // Off-CPU overlay takes precedence over chunk/span. The translucent band is the topmost drawn element and spans the
    // full lane height; an off-CPU interval is a gap between the thread's on-CPU slices, so no chunk or span legitimately
    // coexists in that time range. Probe it first — `findOffCpuAtX` returns null when the cursor isn't within an off-CPU
    // bar (incl. its small snap tolerance), so a miss falls through cleanly to the chunk/span hit-test below.
    if (showOffCpu) {
      const interval = findOffCpuAtX(offCpuBySlot, threadSlot, mx, gutterWidth, vp);
      if (interval !== null) return { kind: 'off-cpu', interval, trackId: track.id };
    }

    const chunkRowHeight = track.chunkRowHeight ?? 0;
    const inChunkRow = chunkRowHeight > 0 && my >= ty && my < ty + chunkRowHeight;
    if (inChunkRow) {
      const chunk = findChunkAtX(ticks, threadSlot, mx, gutterWidth, vp);
      if (chunk) return { kind: 'chunk', chunk, trackId: track.id };
      // No chunk under the cursor — the only spans that draw in the chunk-row band are the pinned
      // ones (renderDepth = -1: Scheduler.Worker.Idle / BetweenTick). They exist exactly during the
      // gaps between chunks, so a click landing here resolves to them.
      const pinned = findSpanAtX(ticks, threadSlot, -1, mx, gutterWidth, vp);
      if (pinned) return { kind: 'span', span: pinned, trackId: track.id };
    } else {
      const spanRegionTop = ty + chunkRowHeight;
      if (my >= spanRegionTop && my < ty + track.height) {
        const depth = Math.floor((my - spanRegionTop) / SPAN_ROW_HEIGHT);
        // Pass `ticks` (full array) — a long span started in a tick before the viewport still
        // renders its body into view, and must be findable by a click there.
        const span = findSpanAtX(ticks, threadSlot, depth, mx, gutterWidth, vp);
        if (span) return { kind: 'span', span, trackId: track.id };
      }
    }
    // Fallback — on an empty slot area, report the tick for DetailPanel.
    const tick = findTickAtX(ticks, mx, gutterWidth, vp);
    if (tick !== null) return { kind: 'tick', tickNumber: tick.tickNumber };
    return null;
  }

  // Phases
  if (track.id === 'phases') {
    if (my >= ty + 1 && my <= ty + PHASE_TRACK_HEIGHT - 1) {
      // Markers win over phase bars when the cursor sits on a glyph — they're typically narrower
      // (a few pixels) and a click on a marker should resolve to the marker, not the phase behind it.
      const markerHit = findPhaseMarkerAtX(ticks, mx, gutterWidth, vp);
      if (markerHit) return { kind: 'phase-marker', marker: markerHit.marker, tickNumber: markerHit.tickNumber };
      const hit = findPhaseAtX(ticks, mx, gutterWidth, vp);
      if (hit) return { kind: 'phase', phase: hit.phase, tickNumber: hit.tickNumber };
    }
    const tick = findTickAtX(ticks, mx, gutterWidth, vp);
    if (tick !== null) return { kind: 'tick', tickNumber: tick.tickNumber };
    return null;
  }

  // Mini-row operation tracks — which row is `my` in?
  if (track.id === 'page-cache' || track.id === 'disk-io' || track.id === 'transactions' || track.id === 'wal' || track.id === 'checkpoint') {
    const rows = rowProjections(track.id);
    const rowIdx = Math.floor((my - ty) / MINI_ROW_HEIGHT);
    if (rowIdx >= 0 && rowIdx < rows.length) {
      const row = rows[rowIdx];
      // Pass `ticks` (full array) — a Checkpoint.Cycle that started before the viewport can still
      // extend into it. Cheap per-tick skips inside `findMiniRowOpAtX` keep the scan bounded.
      const op = findMiniRowOpAtX(ticks, row.getOps, row.getEndMax, mx, gutterWidth, vp);
      if (op) return { kind: 'mini-row-op', op, trackId: track.id, rowLabel: row.label };
    }
    const tick = findTickAtX(ticks, mx, gutterWidth, vp);
    if (tick !== null) return { kind: 'tick', tickNumber: tick.tickNumber };
    return null;
  }

  return null;
}

// ═════════════════════════════════════════════════════════════════════════════════════════════════
// Inner helpers
// ═════════════════════════════════════════════════════════════════════════════════════════════════

function findTrackAtY(tracks: readonly TrackLayout[], my: number, scrollY: number): TrackLayout | null {
  for (const t of tracks) {
    const top = t.id === 'ruler' ? t.y : t.y - scrollY; // ruler is always pinned at its layout y
    const advance = t.state === 'summary'
      ? (t.id.startsWith('slot-') ? LABEL_ROW_HEIGHT + TRACK_GAP : LABEL_ROW_HEIGHT + 4)
      : (t.height + TRACK_GAP);
    if (my >= top && my < top + advance) return t;
  }
  return null;
}

/**
 * Return true when canvas-space `(mx, my)` falls inside the track's "?" glyph hit zone. `ty` is the
 * track's top in canvas space (already accounting for `vp.scrollY`). Geometry mirrors the draw
 * logic in `timeArea.ts`: glyph is right-aligned in the gutter, vertically centred on the label
 * baseline (`ty + 12`).
 */
function isInHelpHitZone(mx: number, my: number, ty: number, gutterWidth: number): boolean {
  const glyphRight = gutterWidth - HELP_ICON_RIGHT_PAD;
  const glyphLeft = glyphRight - HELP_ICON_GLYPH_WIDTH;
  const glyphTop = ty + 12 - 11;
  const glyphBottom = glyphTop + HELP_ICON_BG_HEIGHT;
  return mx >= glyphLeft - HELP_ICON_HIT_PAD
    && mx <= glyphRight + HELP_ICON_HIT_PAD
    && my >= glyphTop - HELP_ICON_HIT_PAD
    && my <= glyphBottom + HELP_ICON_HIT_PAD;
}

function findTickAtX(ticks: readonly TickData[], mx: number, gutterWidth: number, vp: Viewport): TickData | null {
  // Convert mx → µs, then binary-search the sorted tick array. `spanEndMaxRunning` isn't helpful
  // here because we want the tick whose [startUs, endUs) contains the cursor.
  const us = vp.offsetX + (mx - gutterWidth) / vp.scaleX;
  let lo = 0;
  let hi = ticks.length - 1;
  while (lo <= hi) {
    const mid = (lo + hi) >>> 1;
    const t = ticks[mid];
    if (us < t.startUs) hi = mid - 1;
    else if (us >= t.endUs) lo = mid + 1;
    else return t;
  }
  return null;
}

function findChunkAtX(
  ticks: readonly TickData[],
  threadSlot: number,
  mx: number,
  gutterWidth: number,
  vp: Viewport,
): ChunkSpan | null {
  const us = vp.offsetX + (mx - gutterWidth) / vp.scaleX;
  // Small hit tolerance so sub-pixel chunks aren't impossible to click. ~1 pixel wide.
  const tolUs = 0.5 / Math.max(vp.scaleX, 1e-9);
  for (const tick of ticks) {
    if (us < tick.startUs - tolUs || us > tick.endUs + tolUs) continue;
    for (const chunk of tick.chunks) {
      if (chunk.threadSlot !== threadSlot) continue;
      if (us >= chunk.startUs - tolUs && us <= chunk.endUs + tolUs) return chunk;
    }
  }
  return null;
}

function findSystemChunkAtX(
  ticks: readonly TickData[],
  systemIndex: number,
  mx: number,
  gutterWidth: number,
  vp: Viewport,
): ChunkSpan | null {
  const us = vp.offsetX + (mx - gutterWidth) / vp.scaleX;
  const tolUs = 0.5 / Math.max(vp.scaleX, 1e-9);
  for (const tick of ticks) {
    if (us < tick.startUs - tolUs || us > tick.endUs + tolUs) continue;
    for (const chunk of tick.chunks) {
      if (chunk.systemIndex !== systemIndex) continue;
      if (us >= chunk.startUs - tolUs && us <= chunk.endUs + tolUs) return chunk;
    }
  }
  return null;
}

function findSpanAtX(
  ticks: readonly TickData[],
  threadSlot: number,
  depth: number,
  mx: number,
  gutterWidth: number,
  vp: Viewport,
): SpanData | null {
  const us = vp.offsetX + (mx - gutterWidth) / vp.scaleX;
  // Tolerance covers a full MIN_RECT_WIDTH pixel (1 px) on screen — the renderer clamps any span
  // narrower than that to 1 px so a click on the drawn bar can land up to 1 px past the span's
  // actual endUs. Old 0.5 px tolerance would miss sub-pixel span clicks on wide zooms.
  const tolUs = 1 / Math.max(vp.scaleX, 1e-9);
  // Iterate ALL ticks — a span is attributed to the tick where its completion was recorded, not
  // where it *started*. So `tick.startUs` is NOT a lower bound on its spans' `startUs` and we
  // cannot break on `tick.startUs > us + tolUs` (we'd miss Checkpoint.Cycle and friends, which
  // typically live in a tick whose own start is well after the span's). Per-tick skip via endMax
  // still works because running-max of endUs bounds every span's endUs in that tick.
  let best: SpanData | null = null;
  let bestDist = Number.POSITIVE_INFINITY;
  for (const tick of ticks) {
    const slotSpans = tick.spansByThreadSlot.get(threadSlot);
    if (!slotSpans || slotSpans.length === 0) continue;
    const endMax = tick.spanEndMaxByThreadSlot.get(threadSlot);
    if (endMax && endMax[endMax.length - 1] < us - tolUs) continue;
    for (const span of slotSpans) {
      // Match on renderDepth (set by deriveSlotInfo's greedy packing) so a click on the row where
      // the bar was actually drawn resolves the correct span. Fall back to span.depth when packing
      // hasn't run (first-paint corner case) and finally to 0.
      const rd = span.renderDepth ?? span.depth ?? 0;
      if (rd !== depth) continue;
      if (us < span.startUs - tolUs || us > span.endUs + tolUs) continue;
      const mid = (span.startUs + span.endUs) / 2;
      const dist = Math.abs(us - mid);
      if (dist < bestDist) { best = span; bestDist = dist; }
    }
  }
  return best;
}

/**
 * Locate the off-CPU interval under the cursor for one thread slot. Converts the cursor X to µs, binary-searches the
 * slot's interval store (sorted by `startUs`), and returns the containing interval — or the nearest one within a small
 * pixel tolerance so a click in a dense, sub-pixel heat band still resolves to a real interval. O(log n), zoom-independent.
 */
export function findOffCpuAtX(
  offCpuBySlot: Map<number, OffCpuStore>,
  threadSlot: number,
  mx: number,
  gutterWidth: number,
  vp: Viewport,
): OffCpuInterval | null {
  const store = offCpuBySlot.get(threadSlot);
  if (store === undefined) return null;
  const n = store.startUs.length;
  if (n === 0) return null;

  const us = vp.offsetX + (mx - gutterWidth) / vp.scaleX;
  // ~2px snap tolerance so sub-pixel bars on a zoomed-out lane are still clickable.
  const tolUs = 2 / Math.max(vp.scaleX, 1e-9);

  // Largest index whose startUs ≤ us + tolUs (upper-bound − 1). The containing interval, if any, is at or just below it.
  let lo = 0;
  let hi = n;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (store.startUs[mid] <= us + tolUs) lo = mid + 1; else hi = mid;
  }
  // Probe a tiny window around the boundary: the containing interval is index lo-1; lo-2 / lo cover the snap-tolerance
  // case where the cursor sits in the gap between two intervals.
  let best = -1;
  let bestDist = Number.POSITIVE_INFINITY;
  for (let i = Math.max(0, lo - 2); i < Math.min(n, lo + 1); i++) {
    const s = store.startUs[i];
    const e = store.endUs[i];
    if (us < s - tolUs || us > e + tolUs) continue;
    const dist = us < s ? s - us : us > e ? us - e : 0;   // 0 when strictly inside
    if (dist < bestDist) { bestDist = dist; best = i; }
  }
  if (best < 0) return null;
  return materializeOffCpuInterval(store, best, threadSlot);
}

/**
 * Locate a single-point phase marker (UoW Create / UoW Flush glyph) under the cursor. Markers are
 * narrow (a few pixels each), so we widen the hit zone with a per-pixel tolerance so a hover doesn't
 * have to land precisely on the centre. Returns the closest marker by absolute distance to the cursor.
 */
function findPhaseMarkerAtX(
  ticks: readonly TickData[],
  mx: number,
  gutterWidth: number,
  vp: Viewport,
): { marker: PhaseMarker; tickNumber: number } | null {
  const us = vp.offsetX + (mx - gutterWidth) / vp.scaleX;
  // ±4 px hit zone around the marker — matches the glyph radius used by drawPhaseMarkers (PHASE_TRACK_HEIGHT/4).
  const tolUs = 4 / Math.max(vp.scaleX, 1e-9);
  let best: { marker: PhaseMarker; tickNumber: number } | null = null;
  let bestDist = Number.POSITIVE_INFINITY;
  for (const tick of ticks) {
    if (tick.phaseMarkers.length === 0) continue;
    if (us < tick.startUs - tolUs || us > tick.endUs + tolUs) continue;
    for (const m of tick.phaseMarkers) {
      const d = Math.abs(us - m.timestampUs);
      if (d <= tolUs && d < bestDist) {
        best = { marker: m, tickNumber: tick.tickNumber };
        bestDist = d;
      }
    }
  }
  return best;
}

function findPhaseAtX(
  ticks: readonly TickData[],
  mx: number,
  gutterWidth: number,
  vp: Viewport,
): { phase: PhaseSpan; tickNumber: number } | null {
  const us = vp.offsetX + (mx - gutterWidth) / vp.scaleX;
  const tolUs = 0.5 / Math.max(vp.scaleX, 1e-9);
  for (const tick of ticks) {
    if (us < tick.startUs - tolUs || us > tick.endUs + tolUs) continue;
    for (const phase of tick.phases) {
      if (us >= phase.startUs - tolUs && us <= phase.endUs + tolUs) {
        return { phase, tickNumber: tick.tickNumber };
      }
    }
  }
  return null;
}

function findMiniRowOpAtX(
  ticks: readonly TickData[],
  getOps: (t: TickData) => SpanData[],
  getEndMax: (t: TickData) => Float64Array,
  mx: number,
  gutterWidth: number,
  vp: Viewport,
): SpanData | null {
  const us = vp.offsetX + (mx - gutterWidth) / vp.scaleX;
  // 1 px of tolerance covers the full MIN_RECT_WIDTH=1 rendering clamp — see the same note on findSpanAtX.
  const tolUs = 1 / Math.max(vp.scaleX, 1e-9);
  // Iterate ALL ticks — same reason as findSpanAtX. An op is attributed to the tick where it
  // completed, so its own startUs can predate tick.startUs. We cannot break on tick.startUs.
  for (const tick of ticks) {
    const ops = getOps(tick);
    if (ops.length === 0) continue;
    const endMax = getEndMax(tick);
    if (endMax[endMax.length - 1] < us - tolUs) continue;
    // Binary-search the running-max for the first op whose endUs ≥ us-tolUs. Still valid because
    // ops within a tick are sorted by startUs and endMax is the running max of endUs.
    let lo = 0;
    let hi = ops.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (endMax[mid] < us - tolUs) lo = mid + 1; else hi = mid;
    }
    for (let i = lo; i < ops.length; i++) {
      const op = ops[i];
      if (op.startUs > us + tolUs) break;
      if (us >= op.startUs - tolUs && us <= op.endUs + tolUs) return op;
    }
  }
  return null;
}

// Row-definition projection — parallel to `miniRowsForTrack` in timeArea.ts but kept local so the
// hit-test doesn't depend on the draw module. Only the projection accessors matter for hit-testing.
interface RowProjection {
  label: string;
  getOps: (t: TickData) => SpanData[];
  getEndMax: (t: TickData) => Float64Array;
}

function rowProjections(trackId: string): RowProjection[] {
  switch (trackId) {
    case 'page-cache':
      return [
        { label: 'Fetch',    getOps: t => t.cacheFetch,   getEndMax: t => t.cacheFetchEndMax },
        { label: 'Allocate', getOps: t => t.cacheAlloc,   getEndMax: t => t.cacheAllocEndMax },
        { label: 'Evicted',  getOps: t => t.cacheEvict,   getEndMax: t => t.cacheEvictEndMax },
        { label: 'Flush',    getOps: t => t.cacheFlushes, getEndMax: t => t.cacheFlushesEndMax },
      ];
    case 'disk-io':
      return [
        { label: 'Reads',  getOps: t => t.diskReads,  getEndMax: t => t.diskReadsEndMax },
        { label: 'Writes', getOps: t => t.diskWrites, getEndMax: t => t.diskWritesEndMax },
      ];
    case 'transactions':
      return [
        { label: 'Commits',   getOps: t => t.txCommits,   getEndMax: t => t.txCommitsEndMax },
        { label: 'Rollbacks', getOps: t => t.txRollbacks, getEndMax: t => t.txRollbacksEndMax },
        { label: 'Persists',  getOps: t => t.txPersists,  getEndMax: t => t.txPersistsEndMax },
      ];
    case 'wal':
      return [
        { label: 'Flushes', getOps: t => t.walFlushes, getEndMax: t => t.walFlushesEndMax },
        { label: 'Waits',   getOps: t => t.walWaits,   getEndMax: t => t.walWaitsEndMax },
      ];
    case 'checkpoint':
      return [
        { label: 'Cycles', getOps: t => t.checkpointCycles, getEndMax: t => t.checkpointCyclesEndMax },
      ];
    default:
      return [];
  }
}
