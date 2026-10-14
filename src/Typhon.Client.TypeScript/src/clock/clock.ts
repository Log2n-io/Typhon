export interface ClockOptions {
  /** Server tick period in milliseconds, from `WELCOME`. */
  readonly tickPeriodMs: number;
  /** Render delay before enough frames arrived to measure jitter. Default 200 ms. */
  readonly initialDelayMs?: number;
  /** Bounds of the adaptive render delay. Default 150–300 ms. */
  readonly minDelayMs?: number;
  readonly maxDelayMs?: number;
  /** Window over which the offset and the jitter are measured. Default 2 000 ms. */
  readonly windowMs?: number;
  /** Largest speed-up or slow-down applied to converge on the target time. Default 0.05 (±5 %). */
  readonly maxRateAdjust?: number;
  /** An error at least this large jumps forward, or holds when render time is ahead. Default 1 000 ms. */
  readonly snapMs?: number;
  /**
   * How far render time may run past the newest frame before it holds. Default 1 000 ms: more than the 500 ms keepalive
   * interval, so a quiet view whose entities all move on long straight segments never stutters.
   */
  readonly maxExtrapolationMs?: number;
}

const SAMPLE_CAPACITY = 128;
const MIN_SAMPLES_FOR_JITTER = 4;
const PERIOD_HISTORY = 8;

/**
 * Maps server ticks to local time and produces the render time motion is evaluated at.
 *
 * - **Server timeline.** Tick → server milliseconds is piecewise linear: one piece per tick period, since the server
 *   stretches ticks under overload and says so (`PERIOD`). Render time runs on that timeline, in milliseconds, and is
 *   converted to a tick only at the end, through the piece it falls in — so time before a period change keeps the old
 *   period and time after it the new one.
 * - **Offset.** Each frame yields `offset = recvMs − serverMs(tick)`. The smallest offset over a window belongs to the
 *   least-delayed frame and is the best estimate of the true offset; delay only ever adds.
 * - **Render delay.** `period + p95(offset − minOffset)`, clamped to [min, max]: one tick plus the jitter, so the next
 *   segment has almost always arrived before render time reaches its `t0`.
 * - **Convergence.** Render time advances with local time, corrected by at most ±`maxRateAdjust` toward
 *   `serverNow − delay`. It jumps forward only past `snapMs`, and never jumps backward: when it is that far ahead (the
 *   first frame after a stall looks late) it holds until the target catches up.
 * - **Output.** {@link renderTick} (an integer) plus {@link renderFrac} in [0, 1): motion evaluation subtracts integer
 *   ticks first (`motion/motion.ts`). The milliseconds are float64 and stay sub-microsecond exact for centuries.
 */
export class Clock {
  renderTick = 0;
  renderFrac = 0;

  private readonly initialPeriodMs: number;
  private readonly initialDelayMs: number;
  private readonly minDelayMs: number;
  private readonly delayCeilingMs: number;
  private readonly windowMs: number;
  private readonly maxRateAdjust: number;
  private readonly snapMs: number;
  private readonly maxExtrapolationMs: number;

  private delayMs: number;
  private renderMs = 0;
  private started = false;
  private lastUpdateMs = 0;
  private newestTick = -1;

  /** Pieces of the tick → server-time map, oldest first, in `[0, pieceCount)`. */
  private readonly pieceTick = new Float64Array(PERIOD_HISTORY);
  private readonly pieceMs = new Float64Array(PERIOD_HISTORY);
  private readonly piecePeriod = new Float64Array(PERIOD_HISTORY);
  private pieceCount = 0;
  private pendingPeriodMs: number;

  private readonly sampleRecvMs = new Float64Array(SAMPLE_CAPACITY);
  private readonly sampleOffset = new Float64Array(SAMPLE_CAPACITY);
  private sampleStart = 0;
  private sampleCount = 0;
  private minOffsetMs = 0;
  private readonly scratch = new Float64Array(SAMPLE_CAPACITY);

  constructor(options: ClockOptions) {
    if (!(options.tickPeriodMs > 0)) {
      throw new Error(`tickPeriodMs must be positive, got ${options.tickPeriodMs}`);
    }

    this.initialPeriodMs = options.tickPeriodMs;
    this.pendingPeriodMs = options.tickPeriodMs;
    this.minDelayMs = options.minDelayMs ?? 150;
    this.delayCeilingMs = options.maxDelayMs ?? 300;
    this.initialDelayMs = clamp(options.initialDelayMs ?? 200, this.minDelayMs, this.delayCeilingMs);
    this.delayMs = this.initialDelayMs;
    this.windowMs = options.windowMs ?? 2000;
    this.maxRateAdjust = options.maxRateAdjust ?? 0.05;
    this.snapMs = options.snapMs ?? 1000;
    this.maxExtrapolationMs = options.maxExtrapolationMs ?? 1000;
  }

  /** The largest render delay this clock applies, in milliseconds: a store's segment ring must span it. */
  get maxDelayMs(): number {
    return this.delayCeilingMs;
  }

  /**
   * Current render delay in milliseconds — how far behind the newest server frame {@link renderTick}/{@link renderFrac} sits.
   *
   * <b>A client that predicts its own entity must reconcile against this moment, not against now.</b> The position this clock
   * evaluates is the authority as of `now − renderDelayMs`, so a predictor comparing it with its CURRENT predicted position
   * reads the render delay as if it were error — and a corrector that believes it pulls the prediction back at `error / τ`,
   * which at walking pace is a large fraction of the speed and looks like a smoothing bug rather than a measurement mistake.
   * Comparing against what was predicted FOR `now − renderDelayMs` leaves the steady state at zero, so only real divergence
   * moves the correction.
   */
  get renderDelayMs(): number {
    return this.delayMs;
  }

  /** The current tick period in milliseconds. */
  get tickPeriodMs(): number {
    return this.pieceCount > 0 ? this.piecePeriod[this.pieceCount - 1]! : this.pendingPeriodMs;
  }

  /** Newest tick received, or -1. */
  get latestTick(): number {
    return this.newestTick;
  }

  /** Estimated offset between local time and the server timeline, in milliseconds. */
  get offsetMs(): number {
    return this.minOffsetMs;
  }

  /** Render time as a float, for display only: evaluation uses {@link renderTick} and {@link renderFrac}. */
  get renderTime(): number {
    return this.renderTick + this.renderFrac;
  }

  /**
   * How far into the server's tick a local instant fell, as a fraction of the tick period in `[0, 1)`.
   *
   * @param atMs The instant, on the same monotonic clock {@link update} is driven with.
   * @param oneWayDelayMs The estimated downlink delay to take off the grid's anchor — see the remarks. Half the RTT is
   *   the usual estimate; 0 leaves the bias in, which is what loopback wants.
   * @returns The fraction, or `NaN` before the first frame has anchored the grid.
   *
   * @remarks
   * <b>This is the grid a client has to quantise an INTENT against, and the reason is that the server's own integration
   * is quantised.</b> A tick advances an entity by one whole period's worth of whatever intent it held at the boundary,
   * so a client that changes velocity on a key event and a server that changes it on a boundary integrate for different
   * DURATIONS — the stop position differs by `v x (startPhase - stopPhase)`, up to a full tick's travel in either
   * direction, which is a back-step a player sees every time they let go of a key. Sending the phase with the intent
   * lets the server split the boundary tick between the old velocity and the new one, after which both sides have
   * integrated the same duration exactly. Nothing else closes it: predicting instantly means being ahead of what the
   * server has committed, and no client-side correction law can un-spend that.
   *
   * <b>The anchor carries the downlink delay, which is why the estimate is a parameter.</b> {@link offsetMs} is the
   * smallest `recvMs - serverMs(tick)` over the window — the local instant a tick's frame ARRIVED, not the instant the
   * boundary happened, so the grid read here sits one downlink behind the server's. Uncorrected, the phase is short by
   * `delay / period`, which on loopback is nothing and at 30 ms on a 100 ms tick is a third of a step. The caller owns
   * the estimate because the SDK does not know whether the ping is being measured.
   */
  subTickPhase(atMs: number, oneWayDelayMs = 0): number {
    if (this.pieceCount === 0 || this.sampleCount === 0) {
      return NaN;
    }

    const ms = atMs - this.minOffsetMs + oneWayDelayMs;
    const p = this.pieceAt(ms);
    const rel = (ms - this.pieceMs[p]!) / this.piecePeriod[p]!;

    // `rel - floor(rel)` can land on exactly 1: a `rel` a hair below an integer from below — `-1e-17` floors to -1, and
    // `1 - 1e-17` has no float64 neighbour short of 1. The range this promises is half-open, and a caller quantising it to a
    // byte would send "a whole tick" where it means "none of it".
    const fraction = rel - Math.floor(rel);
    return fraction < 1 ? fraction : 0;
  }

  /** Records a frame for `tick`, received at local time `recvMs`. Frames must be passed in arrival order. */
  onFrame(tick: number, recvMs: number): void {
    if (this.pieceCount === 0) {
      this.pieceTick[0] = tick;
      this.pieceMs[0] = 0;
      this.piecePeriod[0] = this.pendingPeriodMs;
      this.pieceCount = 1;
    }

    if (tick > this.newestTick) {
      this.newestTick = tick;
    }

    this.pushSample(recvMs, recvMs - this.serverMsOf(tick));
  }

  /** The server changed its tick period from `tick` on (the `PERIOD` flag). */
  onPeriodChange(tick: number, periodMs: number): void {
    if (!(periodMs > 0)) {
      throw new Error(`periodMs must be positive, got ${periodMs}`);
    }

    if (this.pieceCount === 0) {
      this.pendingPeriodMs = periodMs;
      return;
    }

    const ms = this.serverMsOf(tick);
    if (this.pieceCount === PERIOD_HISTORY) {
      this.pieceTick.copyWithin(0, 1);
      this.pieceMs.copyWithin(0, 1);
      this.piecePeriod.copyWithin(0, 1);
      this.pieceCount--;
    }

    this.pieceTick[this.pieceCount] = tick;
    this.pieceMs[this.pieceCount] = ms;
    this.piecePeriod[this.pieceCount] = periodMs;
    this.pieceCount++;
  }

  /** Forgets every frame, sample and period change (a reconnection): the next frame re-anchors, next update snaps. */
  reset(): void {
    this.newestTick = -1;
    this.started = false;
    this.sampleCount = 0;
    this.sampleStart = 0;
    this.minOffsetMs = 0;
    this.delayMs = this.initialDelayMs;
    this.pendingPeriodMs = this.initialPeriodMs;
    this.pieceCount = 0;
    this.renderMs = 0;
    this.renderTick = 0;
    this.renderFrac = 0;
  }

  /** Advances render time to local time `nowMs`. Call once per rendered frame. */
  update(nowMs: number): void {
    if (this.pieceCount === 0 || this.sampleCount === 0) {
      return;
    }

    const target = nowMs - this.minOffsetMs - this.delayMs;
    if (!this.started) {
      this.renderMs = target;
      this.started = true;
    } else {
      const elapsed = Math.max(0, nowMs - this.lastUpdateMs);
      const predicted = this.renderMs + elapsed;
      const error = target - predicted;
      if (error >= this.snapMs) {
        this.renderMs = target;
      } else if (error > -this.snapMs) {
        const period = this.tickPeriodMs;
        const correction = clamp((error / period) * 0.5, -this.maxRateAdjust, this.maxRateAdjust);
        this.renderMs = predicted + elapsed * correction;
      }
      // Otherwise render time is far ahead of the target: hold.
    }

    this.lastUpdateMs = nowMs;
    const limit = this.serverMsOf(this.newestTick) + this.maxExtrapolationMs;
    if (this.renderMs > limit) {
      this.renderMs = limit;
    }

    this.toTick(this.renderMs);
  }

  private serverMsOf(tick: number): number {
    let p = this.pieceCount - 1;
    while (p > 0 && tick < this.pieceTick[p]!) {
      p--;
    }

    return this.pieceMs[p]! + (tick - this.pieceTick[p]!) * this.piecePeriod[p]!;
  }

  /**
   * The piece of the tick -> server-time map that `ms` falls in.
   *
   * <b>Requires a non-empty map</b> — it returns -1 for an empty one, which indexes to `undefined`. Both callers test
   * `pieceCount` first, and a third has to.
   */
  private pieceAt(ms: number): number {
    let p = this.pieceCount - 1;
    while (p > 0 && ms < this.pieceMs[p]!) {
      p--;
    }

    return p;
  }

  private toTick(ms: number): void {
    const p = this.pieceAt(ms);
    const rel = (ms - this.pieceMs[p]!) / this.piecePeriod[p]!;
    const whole = Math.floor(rel);
    this.renderTick = this.pieceTick[p]! + whole;
    this.renderFrac = rel - whole;
  }

  private pushSample(recvMs: number, offset: number): void {
    const index = (this.sampleStart + this.sampleCount) % SAMPLE_CAPACITY;
    this.sampleRecvMs[index] = recvMs;
    this.sampleOffset[index] = offset;
    if (this.sampleCount < SAMPLE_CAPACITY) {
      this.sampleCount++;
    } else {
      this.sampleStart = (this.sampleStart + 1) % SAMPLE_CAPACITY;
    }

    while (this.sampleCount > 1 && recvMs - this.sampleRecvMs[this.sampleStart]! > this.windowMs) {
      this.sampleStart = (this.sampleStart + 1) % SAMPLE_CAPACITY;
      this.sampleCount--;
    }

    let min = Infinity;
    for (let i = 0; i < this.sampleCount; i++) {
      const o = this.sampleOffset[(this.sampleStart + i) % SAMPLE_CAPACITY]!;
      if (o < min) {
        min = o;
      }
    }

    this.minOffsetMs = min;
    const n = this.sampleCount;
    if (n < MIN_SAMPLES_FOR_JITTER) {
      return;
    }

    // Insertion sort of the jitter samples into the scratch buffer: n ≤ 128, no allocation.
    const s = this.scratch;
    for (let i = 0; i < n; i++) {
      const v = this.sampleOffset[(this.sampleStart + i) % SAMPLE_CAPACITY]! - min;
      let j = i;
      while (j > 0 && s[j - 1]! > v) {
        s[j] = s[j - 1]!;
        j--;
      }

      s[j] = v;
    }

    // Nearest-rank 95th percentile.
    const p95 = s[Math.ceil(n * 0.95) - 1]!;
    this.delayMs = clamp(this.tickPeriodMs + p95, this.minDelayMs, this.delayCeilingMs);
  }
}

function clamp(value: number, min: number, max: number): number {
  return value < min ? min : value > max ? max : value;
}
