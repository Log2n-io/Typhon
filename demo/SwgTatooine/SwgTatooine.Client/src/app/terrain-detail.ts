/**
 * The terrain's detail, governed by the frame rate the machine actually holds (2026-10-25, Loïc: "on a computer with a weak
 * GPU the triangle count of the terrain rendering is not killing the rendering time of the frame … something that works
 * reliably, everywhere").
 *
 * <b>The rule is the frame rate, and only the frame rate</b> (Loïc, same day: "your algo should just increase the error if
 * the FPS is below 60"). Under {@link TERRAIN_TARGET_FPS} the terrain coarsens; holding it, the terrain refines back. The
 * first version tried to coarsen only for a GPU-bound frame — interval over budget while the CPU spent little of it — and
 * that split is what kept it from engaging: Babylon's `render()`, counted as CPU, is where the GPU's back-pressure and the
 * terrain's own draw cost land, so a slow machine read as CPU-bound and was never relieved. Whatever slows a frame, fewer
 * terrain triangles is a cost the frame no longer pays.
 *
 * <b>Unless coarsening does nothing for it, which is a cap, not a load.</b> A browser in battery saver or efficiency mode
 * (Chrome, Edge) holds `requestAnimationFrame` to 30 fps, and a 50 Hz display to 50: there the frame rate is under 60 for good
 * and no terrain makes it faster, so "coarsen while under 60" pinned the ground at its ugliest. The governor therefore
 * checks its own effect: {@link CAP_PROBE_STEPS} steps coarser and the frame no faster by {@link NO_GAIN}, it puts the
 * terrain back at the floor and holds it there while the frame stays at that rate (`capped`). A frame clearly slower than
 * the cap is a load again, and coarsening resumes; one holding 60 means the cap is gone.
 *
 * <b>The Detail slider is the floor</b>: the finest the viewer allows. The governor coarsens above it fast (a step every half
 * second under the target) and refines back slowly (a step after {@link TERRAIN_CALM_MS} at the target). <b>A refinement the
 * machine cannot hold doubles the wait before the next one</b>, up to {@link TERRAIN_MAX_CALM_MS}: a 60 Hz display caps a
 * frame at the target exactly, so "comfortable" cannot mean "faster than needed", and without the backoff the terrain would
 * bounce between two levels every few seconds.
 */

/** The frame rate the terrain is coarsened to hold. **Load-bearing**: the demo's target, and a 60 Hz display's own rate. */
export const TERRAIN_TARGET_FPS = 60;

/** The coarsest tolerance the governor reaches, rendered pixels: the bound on how ugly it will make the ground. */
export const TERRAIN_MAX_PX = 32;

/** How long one decision's samples span, milliseconds. */
export const TERRAIN_WINDOW_MS = 500;

/** How long frames must hold the target before the terrain is refined a step, milliseconds — the first time. */
export const TERRAIN_CALM_MS = 3000;

/** The longest the backoff makes that wait. */
export const TERRAIN_MAX_CALM_MS = 48_000;

/**
 * A frame interval longer than this is not a frame the machine took: a hidden tab, a debugger, an alt-tab. Dropped from the
 * window, or one such pause would coarsen the terrain for nothing.
 */
export const TERRAIN_IGNORE_INTERVAL_MS = 250;

/** One step, either way: × or ÷ this. */
const STEP = 1.25;

/**
 * Under the target means a median interval past the budget by this share; holding it, within this share. Vsync puts a
 * frame at the budget give or take a timer tick, so the two are close and both above 1.
 */
const OVER = 1.1;
const HOLDING = 1.05;

/** How many steps coarser the terrain is taken before the governor asks whether it helped: ×1.95 the floor. */
export const CAP_PROBE_STEPS = 3;

/** Helped means the frame got at least this much shorter: 5 %. Less, and the frame rate is not the terrain's to fix. */
export const NO_GAIN = 0.95;

/** A frame this much slower than a detected cap is a load on top of it, and coarsening resumes. */
const OVER_CAP = 1.15;

/** Samples one window can hold: 500 ms at 240 Hz is 120. A window that fills decides early. */
const WINDOW_CAPACITY = 128;

/**
 * The governor: fed one frame at a time, answers the tolerance the terrain should be selected at this frame.
 * Allocation-free per frame — one fixed sample array and a handful of numbers.
 */
export class TerrainDetailGovernor {
  private readonly budgetMs: number;
  private readonly maxPx: number;
  private readonly intervals = new Float64Array(WINDOW_CAPACITY);
  private count = 0;
  private windowStartMs = Number.NaN;
  private calmSinceMs = Number.NaN;
  private calmMs = TERRAIN_CALM_MS;
  /** When the last refinement happened, or NaN: a coarsening within one calm of it means the machine could not hold it. */
  private refinedAtMs = Number.NaN;
  private floorPx = Number.NaN;
  private autoPx = Number.NaN;
  /** The frame's median interval when the governor first coarsened above the floor: what its steps are measured against. */
  private baselineMs = Number.NaN;
  /** The interval of a detected frame-rate cap, or 0 for none; see the module's remarks. */
  private cappedMs = 0;

  /**
   * @param targetFps The frame rate under which the terrain is coarsened; see {@link TERRAIN_TARGET_FPS}.
   * @param maxPx The coarsest tolerance it may reach; see {@link TERRAIN_MAX_PX}.
   */
  constructor(targetFps = TERRAIN_TARGET_FPS, maxPx = TERRAIN_MAX_PX) {
    this.budgetMs = 1000 / targetFps;
    this.maxPx = maxPx;
  }

  /** The tolerance the governor has settled on, rendered pixels; the floor until it has coarsened. */
  get tolerance(): number {
    return this.autoPx;
  }

  /** Whether the governor holds the terrain coarser than the viewer's floor. */
  get coarsened(): boolean {
    return this.autoPx > this.floorPx;
  }

  /**
   * The frame rate the machine is capped at, when the governor has found that coarsening does not raise it — a browser's
   * battery saver, a 30 or 50 Hz display — or `null`. While it is set the terrain is held at the viewer's setting.
   */
  get cappedFps(): number | null {
    return this.cappedMs > 0 ? 1000 / this.cappedMs : null;
  }

  /** How long frames must now hold the target before the next refinement, milliseconds: the backoff's current wait. */
  get calmWaitMs(): number {
    return this.calmMs;
  }

  /**
   * Takes one frame and answers the tolerance to select the terrain at.
   *
   * @param nowMs The frame's time, milliseconds (`performance.now()`).
   * @param intervalMs The time since the previous frame began (`engine.getDeltaTime()`).
   * @param floorPx The viewer's Detail setting, rendered pixels — the finest allowed. A change resets the governor to it.
   */
  update(nowMs: number, intervalMs: number, floorPx: number): number {
    if (floorPx !== this.floorPx) {
      // The viewer moved the slider: theirs is the word, so start from it rather than from what the governor learned.
      this.floorPx = floorPx;
      this.autoPx = floorPx;
      this.count = 0;
      this.windowStartMs = nowMs;
      this.calmSinceMs = nowMs;
      this.calmMs = TERRAIN_CALM_MS;
      this.refinedAtMs = Number.NaN;
      this.baselineMs = Number.NaN;
      this.cappedMs = 0;
    }

    if (intervalMs > 0 && intervalMs <= TERRAIN_IGNORE_INTERVAL_MS && this.count < WINDOW_CAPACITY) {
      this.intervals[this.count++] = intervalMs;
    }

    if (nowMs - this.windowStartMs >= TERRAIN_WINDOW_MS || this.count === WINDOW_CAPACITY) {
      this.decide(nowMs);
      this.count = 0;
      this.windowStartMs = nowMs;
    }

    return this.autoPx;
  }

  private decide(nowMs: number): void {
    if (this.count === 0) {
      return;
    }

    const interval = median(this.intervals, this.count);
    if (interval > this.budgetMs * OVER) {
      if (this.cappedMs > 0) {
        if (interval <= this.cappedMs * OVER_CAP) {
          // The cap, still: no terrain makes this faster, so the viewer keeps the detail they asked for.
          this.calmSinceMs = nowMs;
          return;
        }

        // Slower than the cap: a real load on top of it. Measured from here.
        this.cappedMs = 0;
        this.baselineMs = interval;
      } else if (!this.coarsened) {
        this.baselineMs = interval;
      } else if (this.autoPx >= this.floorPx * STEP ** CAP_PROBE_STEPS && interval > this.baselineMs * NO_GAIN) {
        // Coarser by CAP_PROBE_STEPS and the frame no faster: not the terrain's to fix. Back to the floor, and hold it.
        this.cappedMs = interval;
        this.autoPx = this.floorPx;
        this.refinedAtMs = Number.NaN;
        this.calmSinceMs = nowMs;
        return;
      }

      if (this.autoPx < Math.max(this.maxPx, this.floorPx)) {
        // A step it refined to and could not hold: wait twice as long before trying that again.
        if (nowMs - this.refinedAtMs < this.calmMs) {
          this.calmMs = Math.min(this.calmMs * 2, TERRAIN_MAX_CALM_MS);
        }

        this.autoPx = Math.min(this.autoPx * STEP, Math.max(this.maxPx, this.floorPx));
      }

      this.refinedAtMs = Number.NaN;
      this.calmSinceMs = nowMs;
    } else if (interval > this.budgetMs * HOLDING) {
      // Between holding the target and missing it: neither way.
      this.calmSinceMs = nowMs;
    } else if (this.cappedMs > 0) {
      // Holding 60 again: whatever capped the frame rate is gone.
      this.cappedMs = 0;
    } else if (this.autoPx > this.floorPx && nowMs - this.calmSinceMs >= this.calmMs) {
      this.autoPx = Math.max(this.autoPx / STEP, this.floorPx);
      this.refinedAtMs = nowMs;
      this.calmSinceMs = nowMs;
    }
  }
}

/** The median of the first `n` values, sorting them in place (insertion sort: n ≤ 128, and no allocation). */
function median(values: Float64Array, n: number): number {
  for (let i = 1; i < n; i++) {
    const v = values[i];
    let j = i - 1;
    while (j >= 0 && values[j] > v) {
      values[j + 1] = values[j];
      j--;
    }

    values[j + 1] = v;
  }

  return n % 2 === 1 ? values[n >> 1] : (values[(n >> 1) - 1] + values[n >> 1]) * 0.5;
}
