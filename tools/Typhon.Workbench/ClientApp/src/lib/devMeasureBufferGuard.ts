/**
 * Keeps React's development-build render instrumentation from exhausting the tab's heap.
 *
 * **The failure this exists to stop.** React 19's development build records a `performance.measure` for every component
 * render, each carrying a `detail` object for the DevTools "Components" performance track (`logComponentRender` in
 * `react-dom-client.development.js`). Nothing ever clears that buffer — the browser holds every entry for the page's
 * lifetime. An app that renders steadily therefore leaks in proportion to its render count, and the Workbench attached to a
 * live engine renders steadily by design.
 *
 * Measured against the SWG demo at 50 Hz, capture-everything, in `wb-dev` dev mode: **88 measures at t=5 s, 88 924 at
 * t=55 s** — about 830/s — and the heap sawtoothing between 330 and 500 MB on top of the chunk cache. At roughly two
 * minutes (tick ~5 950) the tab died with the measure call itself as the thing that failed:
 *
 * ```
 * Uncaught DataCloneError: Failed to execute 'measure' on 'Performance': Data cannot be cloned, out of memory.
 *     at logComponentRender (react-dom-client.development.js)
 * ```
 *
 * The window stopped answering a menu click and never recovered. Reducing the render rate helps and has been done
 * separately, but it cannot fix this: any nonzero steady render rate eventually exhausts an unbounded buffer.
 *
 * **Why clearing is safe here.** The Workbench calls `performance.measure` nowhere — verified across `ClientApp/src`, the
 * only hit is a comment mentioning `measureUserAgentSpecificMemory`. Every entry in the buffer is React's own, written and
 * forgotten. `performance.mark` is left alone: marks are cheap, nothing accumulates them at render rate, and clearing them
 * could disturb a profiling session a developer set up by hand.
 *
 * **Dev only, and deliberately so.** React's production build has no `logComponentRender`, so the buffer does not grow and
 * this guard has nothing to do. It is behind `import.meta.env.DEV` to keep it out of the shipped bundle entirely rather
 * than shipping a timer that would never fire.
 *
 * The trade-off is explicit: a developer who opens the Performance panel mid-session sees only the measures since the last
 * sweep. That is the cost of the tool staying alive long enough to profile at all, and the interval is long enough
 * (30 s) that a recording started deliberately still captures a useful window.
 */

/** How often to sweep. Long enough that a hand-started recording keeps a useful window; short enough that ~25 000 entries is the ceiling. */
const SWEEP_INTERVAL_MS = 30_000;

/**
 * Starts the periodic sweep. A no-op outside the development build, and when `performance.clearMeasures` is unavailable.
 *
 * @returns a function that stops the sweep, for a caller that owns a lifetime (tests); the app itself never stops it.
 */
export function installDevMeasureBufferGuard(): () => void {
  if (!import.meta.env.DEV) {
    return () => {};
  }
  if (typeof performance === 'undefined' || typeof performance.clearMeasures !== 'function') {
    return () => {};
  }

  const timer = setInterval(() => {
    try {
      performance.clearMeasures();
    } catch {
      // A UA that refuses the call is not a reason to take the app down; the leak is the lesser problem of the two.
    }
  }, SWEEP_INTERVAL_MS);

  return () => clearInterval(timer);
}
