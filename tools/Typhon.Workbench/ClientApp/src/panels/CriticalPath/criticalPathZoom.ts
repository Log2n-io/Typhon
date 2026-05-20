/**
 * Pure major-axis zoom math for {@link CriticalPathView}'s drag-to-zoom gesture — kept out of the
 * component so the coordinate algebra is unit-testable in isolation.
 *
 * **Coordinate spaces.**
 * - *Major px* — a pixel position along the time axis, measured from the container edge. `0` is the
 *   container edge; the band-label gutter (horizontal orientation) occupies `[0, gutterPx)`.
 * - *Drawn-µs* — the timeline's intrinsic coordinate (metronome lead included). The view maps a
 *   drawn-µs `d` to major px via `gutterPx + d·pxPerUs − panMajor`.
 *
 * `panMajor` is the major-axis pan offset (px); `pxPerUs` is the zoom factor. Together they define
 * which drawn-µs window is on screen — see {@link visibleWindow}.
 */

/** A drawn-µs interval `[start, end)` on the major axis. */
export interface DrawnWindow
{
  start: number;
  end: number;
}

/** Drawn-µs under a major-axis pixel position — inverse of the view's `majorPx` mapping. */
export function drawnUsAt(majorPx: number, panMajor: number, pxPerUs: number, gutterPx: number): number
{
  return (panMajor + majorPx - gutterPx) / pxPerUs;
}

/**
 * The drawn-µs window currently framed by the major-axis viewport. `vpMajor` is the viewport's
 * extent on the major axis (width when horizontal, height when vertical); the content area is
 * `vpMajor − gutterPx`.
 */
export function visibleWindow(panMajor: number, pxPerUs: number, gutterPx: number, vpMajor: number): DrawnWindow
{
  const start = panMajor / pxPerUs;
  const contentMajor = Math.max(1, vpMajor - gutterPx);
  return { start, end: start + contentMajor / pxPerUs };
}

/**
 * The `(pxPerUs, panMajor)` pair that frames the drawn-µs window `[start, end)` exactly into the
 * content area. Inverse of {@link visibleWindow}. A degenerate (zero-width) window is floored to a
 * 1e-6 span so `pxPerUs` stays finite.
 */
export function viewParamsForWindow(
  start: number,
  end: number,
  gutterPx: number,
  vpMajor: number,
): { pxPerUs: number; panMajor: number }
{
  const contentMajor = Math.max(1, vpMajor - gutterPx);
  const pxPerUs = contentMajor / Math.max(1e-6, end - start);
  return { pxPerUs, panMajor: start * pxPerUs };
}

/** Ease-out cubic — the profiler's zoom-tween easing (`TimeArea`), reused here for an identical feel. */
export function easeOutCubic(t: number): number
{
  const c = 1 - Math.min(Math.max(t, 0), 1);
  return 1 - c * c * c;
}

/**
 * Interpolate a drawn-µs window for the zoom tween at fraction `t` (already eased by the caller).
 *
 * Both endpoints are interpolated **linearly** — that is what keeps the motion overshoot-free: a
 * linear ramp is monotonic, so a shared edge (far-left selection → common `start`; right-aligned
 * selection → common `end`) stays pinned exactly, and neither edge ever swings past its target.
 *
 * The trick is the **time warp** `τ(t)`. A plain linear interp ramps the *width* linearly too, and
 * since `pxPerUs ∝ 1/width` that makes a zoom-out lurch — the first frames collapse `pxPerUs` by a
 * huge factor, the rest crawls. `τ(t)` is chosen so that linear-in-τ width interpolation traces a
 * *geometric* width ramp `w0·(w1/w0)^t` — constant perceived zoom rate — while the endpoints,
 * still linear in τ, stay monotonic. `τ` is monotonic 0→1, so the window never leaves `[from,to]`.
 */
export function interpZoomWindow(from: DrawnWindow, to: DrawnWindow, t: number): DrawnWindow
{
  const w0 = Math.max(1e-6, from.end - from.start);
  const w1 = Math.max(1e-6, to.end - to.start);
  // τ(t): solve `w0 + (w1−w0)·τ = w0·(w1/w0)^t` for τ. Degenerates to τ = t for a pure pan
  // (w0 ≈ w1) where the width is constant and linear time is already uniform.
  let tau: number;
  if (Math.abs(w1 - w0) < 1e-6)
  {
    tau = t;
  }
  else
  {
    tau = (w0 * (Math.pow(w1 / w0, t) - 1)) / (w1 - w0);
  }
  return {
    start: from.start + (to.start - from.start) * tau,
    end: from.end + (to.end - from.end) * tau,
  };
}

/**
 * A bounded back/forward stack of drawn-µs windows — the Critical-Path panel's own zoom/pan
 * history, walked by mouse back/forward. `pointer` indexes the currently-displayed entry.
 */
export interface ViewHistory
{
  entries: DrawnWindow[];
  pointer: number;
}

/**
 * Append `w` to the history, dropping any forward entries (a new gesture forks the timeline) and
 * capping the length at `cap` (oldest entry falls off the front). The pointer lands on `w`.
 */
export function pushViewHistory(h: ViewHistory, w: DrawnWindow, cap: number): ViewHistory
{
  const entries = h.entries.slice(0, h.pointer + 1);
  entries.push(w);
  while (entries.length > cap) entries.shift();
  return { entries, pointer: entries.length - 1 };
}

/**
 * Move the history pointer by `dir` (−1 back, +1 forward). Returns the **same object** when the
 * step would fall off either end — callers treat referential equality as "no-op, nothing to do".
 */
export function stepViewHistory(h: ViewHistory, dir: number): ViewHistory
{
  const next = h.pointer + dir;
  if (next < 0 || next >= h.entries.length) return h;
  return { entries: h.entries, pointer: next };
}
