import { SpeedClass } from '../data/source';

/**
 * Client-side prediction of the controlled avatar (A14).
 *
 * The server's movement rule is velocity STATE, not per-input impulses: an entity holds `(heading, speedClass)` and the move
 * phase advances it by `dir(heading) × speed(speedClass) × dt`. The client knows that rule exactly, so prediction here is
 * **integration, not input replay** — no sequence numbers, no command queue, no re-simulation. Those exist for games whose
 * movement is impulses against physics, and they can be added the day this one gains a jump.
 *
 * Pressing a key therefore moves the avatar on the next rendered frame rather than 72 ms later, independent of tick rate and
 * ping. The server starts integrating the same thing one tick boundary later, so the predicted position sits a roughly
 * CONSTANT distance ahead — about 0.3 m at 5 m/s and 60 ms — which does not rubber-band, because a constant offset does not
 * oscillate, and closes itself the moment the player stops.
 *
 * ## Reconciliation, and why it is shaped like this
 *
 * The authoritative position a client holds is interpolated at RENDER time, which trails real time by `Clock.renderDelayMs`.
 * Two mistakes are available here, and the first version of this file made both.
 *
 * **Comparing the authority with the CURRENT predicted position** reads the render delay as error. A corrector that believes
 * it pulls the prediction back at `error / τ`, which at a 5 m/s run is a third of the speed, and presents as "prediction makes
 * the avatar sluggish" rather than as a measurement mistake. So the predicted path is kept in a ring and the authority is
 * compared against what was predicted FOR ITS OWN MOMENT.
 *
 * **Correcting the position but not the history** is worse, and is why this is now written as a running total rather than a
 * per-frame nudge. A correction applied now is not reflected in the samples already recorded, so the same error is measured
 * again on every frame until those samples age past the delay — `D/dt` times, 9 to 18 frames. That is textbook dead-time
 * integral windup: the loop gain becomes `D/τ`, and at this client's delay ceiling of 300 ms with τ = 0.12 s that is 2.5. A
 * reviewer measured the consequence by running this file: a stationary avatar limit-cycled ±0.21 m at D = 200 ms, and at
 * D = 300 ms it DIVERGED until it crossed the snap limit, then repeated. It was invisible to the suite because the tests fed
 * an authority generated from the same model, where the error is identically zero and the corrector never acts at all.
 *
 * What is here instead: the ring holds the **pure** integration, and a running `corrX`/`corrZ` holds everything the corrector
 * has applied. The displayed position is their sum, and a past sample is read as `pure(then) + corr(now)` — identical to
 * rewriting every sample whenever a correction lands, at O(1) instead of O(ring). The measured error then excludes corrections
 * already made, so the loop has no windup and is stable at every delay: a standing offset settles geometrically, and a wall
 * parks monotonically.
 *
 * ## What it cannot predict
 *
 * A building the server stops the player against, a clamp at the world edge, a portal or shuttle teleport, an intent the
 * server refused, and the slope scaling the move phase applies to a climbing step (`SimBridge.Steer` sets metres per tick and
 * the move phase shortens an uphill one — the common one on dunes, and the only one that is continuous rather than a jump).
 *
 * Against a wall the body parks at `speed × (renderDelay + τ)`: 0.8 m at this demo's 41 ms delay, 2.1 m at the 300 ms ceiling.
 * **That is a floor, not a tuning choice** — the client cannot learn the server stopped it any sooner than the render delay,
 * so no correction law does better. {@link Predictor.snapLimit} is derived from it rather than fixed, because a constant
 * smaller than the legitimate steady state makes a key held against a building snap for ever.
 */

/** Below this, the authority is adopted outright: holding a sub-centimetre offset only accumulates float noise. */
const ADOPT_EPSILON_M = 0.02;

/** The smallest snap threshold, for a standing player whose legitimate steady-state error is zero. */
const SNAP_FLOOR_M = 2;

/**
 * How far past the legitimate steady state an error must go to count as a structural surprise rather than as lag.
 *
 * The steady state against a wall is `speed × (renderDelay + τ)`; a teleport is hundreds of metres. 1.6 leaves the band
 * between them wide, which is what makes a snap mean "the client was wrong about the world" rather than "the connection is
 * slow today".
 */
const SNAP_MARGIN = 1.6;

/**
 * Error-correction time constant.
 *
 * It costs nothing in the common case, where the measured error is already zero; what it sets is how far the prediction trails
 * a server doing something unpredictable. It is NOT the whole wall overshoot — see the module remarks: the render delay
 * dominates and τ adds to it.
 */
const CORRECTION_TAU_S = 0.12;

/**
 * Predicted samples kept, one per rendered frame.
 *
 * <b>Sized from the clock's delay CEILING, not from a comfortable-looking number.</b> At 48 samples the ring spanned
 * `47 × frameMs` — 326 ms at 144 Hz, but only 285 ms at 165 Hz and 196 ms at 240 Hz, all under the 300 ms ceiling. A ring that
 * cannot reach the authority's moment makes {@link Predictor.sampleAt} fail on every frame, so reconciliation stops entirely
 * and an offset is held for ever with nothing raised: measured at 240 Hz and D = 300 ms, a 0.5 m error never corrected and
 * never snapped. 128 spans 533 ms even at 240 Hz, for 3 KB.
 */
const HISTORY = 128;

/**
 * Turns a speed CLASS into the metres per second the server granted this player.
 *
 * @param speedClass One of {@link SpeedClass}.
 * @param walkMps The player's replicated walk speed.
 * @param runMps The player's replicated run speed.
 * @returns The speed, or `NaN` when the stat it needs has not arrived or is not a usable speed.
 *
 * @remarks
 * <b>The stats come from the server and are never mirrored here.</b> A class value the client gets wrong is refused by the
 * server and counted, loudly; a SPEED the client gets wrong produces position drift that grows for as long as the player
 * walks.
 *
 * <b>Non-positive counts as absent, not as "stand still".</b> `selfNumber` yields `NaN` before the owner block arrives, but
 * the server-side fields are plain floats defaulting to 0 — so an avatar persisted before these stats existed, or any future
 * path that forgets to write them, hands the client a finite 0. Treating that as a speed would hold the body still while the
 * server walked it, growing the error until it snapped, repeatedly, with the fallback counter reading zero. `NaN` routes it to
 * the documented no-prediction fallback instead, which is loud.
 */
export function speedForClass(speedClass: number, walkMps: number, runMps: number): number {
  if (speedClass === SpeedClass.stop) {
    return 0;
  }

  const granted = speedClass === SpeedClass.walk ? walkMps : runMps;
  return Number.isFinite(granted) && granted > 0 ? granted : NaN;
}

/** The predicted position of one entity, integrated locally and reconciled against the authority. */
export class Predictor {
  /** Whether {@link x}/{@link z} hold a prediction. False until {@link start}, and after {@link stop}. */
  active = false;

  /** The predicted position, in world metres — the pure integration plus everything the corrector has applied. */
  x = 0;
  z = 0;

  /** How many times the error exceeded {@link snapLimit} and the prediction was jumped. A diagnostic, not a counter to ignore. */
  snaps = 0;

  /** How many frames were asked to predict without a usable speed stat, and declined. */
  fallbacks = 0;

  /**
   * How many frames found the ring unable to reach the authority's moment, so no reconciliation happened.
   *
   * Expected to be tiny: the first frames of a session, and the two after a snap. A number that keeps climbing means
   * {@link HISTORY} is too small for this display's refresh rate against the clock's delay — a silent stall rather than a
   * visible fault, because the body simply stops being corrected.
   */
  staleReads = 0;

  private heading = 0;
  private speedMps = 0;

  // The pure integration, and the running total of every correction applied to it. `x = pure + corr`, kept apart so a past
  // sample can be read as `pure(then) + corr(now)` — see the module remarks.
  private pureX = 0;
  private pureZ = 0;
  private corrX = 0;
  private corrZ = 0;

  // Ring of the PURE path: real-time milliseconds, and the integration with no correction in it.
  private readonly atMs = new Float64Array(HISTORY);
  private readonly atX = new Float64Array(HISTORY);
  private readonly atZ = new Float64Array(HISTORY);
  private head = -1;
  private count = 0;

  // sampleAt's output, as fields: one object per frame is within budget, but zero is free.
  private sampleX = 0;
  private sampleZ = 0;

  /**
   * The error that counts as a structural surprise at this speed and delay, in metres.
   *
   * @param renderDelayMs The authority's lag, `Clock.renderDelayMs`.
   * @returns The snap threshold.
   */
  snapLimit(renderDelayMs: number): number {
    const steady = this.speedMps * (renderDelayMs / 1000 + CORRECTION_TAU_S);
    return Math.max(SNAP_FLOOR_M, steady * SNAP_MARGIN);
  }

  /** Begins predicting at the authority's current position. Call again to re-anchor on a different subject. */
  start(nowMs: number, x: number, z: number): void {
    this.active = true;
    this.heading = 0;
    this.speedMps = 0;
    this.anchor(nowMs, x, z);
  }

  /** Stops predicting — a god camera, a spectator, or a session whose intents the server refuses. */
  stop(): void {
    this.active = false;
    this.speedMps = 0;
    this.head = -1;
    this.count = 0;
  }

  /**
   * Sets the intent being integrated. Call every frame with the CURRENT intent, not only when it changes: the send is rate
   * limited and coalesced, and the predictor has to keep moving between sends.
   *
   * @param heading Radians, 0 along +X toward +Z — the frame the server integrates in.
   * @param speedMps Metres per second, from {@link speedForClass}; `NaN` suspends prediction for this frame.
   * @returns Whether the intent was usable.
   */
  setIntent(heading: number, speedMps: number): boolean {
    if (!Number.isFinite(speedMps)) {
      this.fallbacks++;
      this.speedMps = 0;
      return false;
    }

    this.heading = heading;
    this.speedMps = speedMps;
    return true;
  }

  /** Stands still — the keys are up. Kept distinct from a failed {@link setIntent}, which is not a decision to stop. */
  clearIntent(): void {
    this.speedMps = 0;
  }

  /**
   * Advances the prediction one frame and reconciles it against the authority.
   *
   * @param nowMs The frame's timestamp, in the same clock {@link Clock.renderDelayMs} is measured against.
   * @param dtSeconds Seconds since the previous frame.
   * @param authX The authoritative (interpolated) position this frame.
   * @param authZ As {@link authX}.
   * @param renderDelayMs How far behind real time the authority is — `Clock.renderDelayMs`.
   */
  step(nowMs: number, dtSeconds: number, authX: number, authZ: number, renderDelayMs: number): void {
    if (!this.active) {
      return;
    }

    if (this.speedMps > 0) {
      // The server's own rule. `atan2(dz, dx)` is what produced this heading, so cos is the x axis and sin the z — the same
      // frame `MoveDir`'s destination is computed in.
      this.pureX += Math.cos(this.heading) * this.speedMps * dtSeconds;
      this.pureZ += Math.sin(this.heading) * this.speedMps * dtSeconds;
    }

    this.record(nowMs);

    if (!this.sampleAt(nowMs - renderDelayMs)) {
      this.staleReads++;
      this.publish();
      return;
    }

    // What the DISPLAYED path was at the authority's moment: the pure sample plus the corrections standing NOW. Using the
    // current total is what stops an already-applied correction being measured a second time.
    const ex = authX - (this.sampleX + this.corrX);
    const ez = authZ - (this.sampleZ + this.corrZ);
    const error = Math.hypot(ex, ez);
    if (error >= this.snapLimit(renderDelayMs)) {
      this.snaps++;
      this.anchor(nowMs, authX, authZ);
      return;
    }

    if (error <= ADOPT_EPSILON_M) {
      this.corrX += ex;
      this.corrZ += ez;
    } else {
      const k = 1 - Math.exp(-dtSeconds / CORRECTION_TAU_S);
      this.corrX += ex * k;
      this.corrZ += ez * k;
    }

    this.publish();
  }

  /** Restarts the pure path at a known position, with nothing owed and no history to reconcile against. */
  private anchor(nowMs: number, x: number, z: number): void {
    this.pureX = x;
    this.pureZ = z;
    this.corrX = 0;
    this.corrZ = 0;
    this.head = -1;
    this.count = 0;
    this.record(nowMs);
    this.publish();
  }

  private publish(): void {
    this.x = this.pureX + this.corrX;
    this.z = this.pureZ + this.corrZ;
  }

  private record(nowMs: number): void {
    this.head = (this.head + 1) % HISTORY;
    this.atMs[this.head] = nowMs;
    this.atX[this.head] = this.pureX;
    this.atZ[this.head] = this.pureZ;
    if (this.count < HISTORY) {
      this.count++;
    }
  }

  /**
   * Reads the pure path at `whenMs` into {@link sampleX}/{@link sampleZ}, interpolating between the samples around it.
   *
   * @param whenMs The moment to read.
   * @returns Whether the ring reaches back that far.
   *
   * @remarks
   * `false` when it does not — the first frames of a session, the frames after a snap, and a ring too small for the display's
   * refresh rate. Correcting against the oldest sample instead would mean correcting against an arbitrarily stale position,
   * which is the kind of guess this class exists to avoid; the caller counts the refusal instead.
   */
  private sampleAt(whenMs: number): boolean {
    if (this.count < 2) {
      return false;
    }

    const oldest = (this.head - this.count + 1 + HISTORY) % HISTORY;
    if (whenMs < this.atMs[oldest]) {
      return false;
    }

    let newer = this.head;
    for (let i = 1; i < this.count; i++) {
      const index = (this.head - i + HISTORY) % HISTORY;
      if (this.atMs[index] <= whenMs) {
        const span = this.atMs[newer] - this.atMs[index];
        // Clamped, because `whenMs` can land past the newest sample when a frame runs longer than the render delay. An
        // unclamped factor would extrapolate the pure path forward and reconcile against a position never displayed.
        const t = span > 0 ? Math.min(1, Math.max(0, (whenMs - this.atMs[index]) / span)) : 0;
        this.sampleX = this.atX[index] + (this.atX[newer] - this.atX[index]) * t;
        this.sampleZ = this.atZ[index] + (this.atZ[newer] - this.atZ[index]) * t;
        return true;
      }

      newer = index;
    }

    return false;
  }
}
