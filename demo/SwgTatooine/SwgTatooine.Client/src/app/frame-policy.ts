/** The controller's small decisions, apart from WebGL and the DOM so they can be tested. */

/**
 * Mean and nearest-rank 95th percentile of the first `n` samples, sorted into `scratch` (insertion sort: `n` is a few
 * hundred at most, at 4 Hz). Writes `[mean, p95]` into `out`; both 0 when there is no sample.
 */
export function meanAndP95(samples: Float64Array, n: number, scratch: Float64Array, out: Float64Array): void {
  if (n <= 0) {
    out[0] = 0;
    out[1] = 0;
    return;
  }

  let sum = 0;
  for (let i = 0; i < n; i++) {
    const v = samples[i];
    sum += v;
    let j = i;
    while (j > 0 && scratch[j - 1] > v) {
      scratch[j] = scratch[j - 1];
      j--;
    }

    scratch[j] = v;
  }

  out[0] = sum / n;
  out[1] = scratch[Math.ceil(n * 0.95) - 1];
}

/** Whether the god camera's region is due: moved 5 % of the radius or a new radius, and not sent in the last `intervalMs`. */
export function regionDue(
  x: number,
  z: number,
  radius: number,
  sentX: number,
  sentZ: number,
  sentRadius: number,
  nowMs: number,
  sentMs: number,
  intervalMs: number,
): boolean {
  // NaN (nothing sent yet) fails every comparison: `!(moved <= …)` makes it due.
  const moved = Math.hypot(x - sentX, z - sentZ);
  const changed = !(moved <= radius * 0.05) || radius !== sentRadius;
  return changed && nowMs - sentMs >= intervalMs;
}
