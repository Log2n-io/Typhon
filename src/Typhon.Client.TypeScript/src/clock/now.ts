/**
 * The clock every part of the SDK measures local time with, unless one is injected.
 *
 * <b>Monotonic, and that is the point.</b> `Date.now()` is wall time: it jumps when the host's clock is corrected, which
 * an offset estimator reads as the server having moved and an RTT measurement reads as a spike. `performance.now()` does
 * not jump, and it shares its origin with `requestAnimationFrame`, so a render loop and the network layer measure the
 * same timeline.
 *
 * <b>They must be the same timeline.</b> {@link Clock.update} is given the renderer's own `now`, while frames are stamped
 * by whatever the connection uses; a mismatch is not a small error but an offset of the Unix epoch — about 1.79e12 ms,
 * which reaches the renderer as a render tick near −1.8e10 and a view in which nothing that moves is ever drawn. That is
 * what a default of `Date.now()` here produced on the first live session the browser client ever opened.
 */
interface PerformanceLike {
  now(): number;
}

// Through `globalThis`, because the SDK builds without the DOM library: it runs in a browser, in a worker and in Node,
// and all three have `performance`, but only the first has it in `lib.dom`.
const perf = (globalThis as { performance?: PerformanceLike }).performance;

export const monotonicNow: () => number =
  perf !== undefined && typeof perf.now === 'function' ? () => perf.now() : () => Date.now();
