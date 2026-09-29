import type { CrossingKind } from '../data/realm-view';

/**
 * Where a realm transition is in its run.
 *
 * `out` and `hold` exist only for a crossing the viewer ASKED for, because that is the only kind the client sees
 * coming. A door is server-initiated: the client learns at `onRealmChanged`, by which point the store is already empty
 * and there is nothing left to fade out of. Faking one would mean delaying the frame's apply, which SUB-29 forbids and
 * which would desynchronise the clock.
 */
export type TransitionPhase = 'idle' | 'out' | 'hold' | 'in';

export interface TransitionState {
  readonly phase: TransitionPhase;
  /** When the current phase began, in `performance.now()` milliseconds. */
  readonly sinceMs: number;
  /** How long the current ramp takes. Zero in `hold`, which ends on an event rather than a clock. */
  readonly durationMs: number;
  /** What the caption says, or `''` for none. */
  readonly caption: string;
  /**
   * Whether the arrival has already landed.
   *
   * <b>Needed because the frame usually beats the fade.</b> Against a local server the round trip is about a
   * millisecond, so a `REALM` block arrives well inside the 180 ms fade-out — and a state machine that only knew
   * "waiting" or "not waiting" had nowhere to record an arrival that came too early. It parked in `hold` and sat there
   * until the safety valve fired, which is a 2.5 s black screen on the happy path.
   */
  readonly settled: boolean;
}

export const IDLE: TransitionState = { phase: 'idle', sinceMs: 0, durationMs: 0, caption: '', settled: false };

/**
 * How long a viewer may be held at full opacity before the screen is given back.
 *
 * <b>The most important number here.</b> `ViewRealm` is rate-limited at one per tick, refused for a realm that does not
 * exist, and answered by a frame the client does not control — so every failure mode ends with a hold that never
 * receives its arrival. Without this the result is a black screen over a live, working simulation, which is the worst
 * outcome of the whole feature and the one most likely to be blamed on the engine.
 */
const HOLD_LIMIT_MS = 2500;

/** How long the screen takes to come back after a refusal — brisk, because nothing changed and nothing is loading. */
const REFUSED_IN_MS = 140;

/** How long the screen takes to come back once a fade-out completes over an arrival that already landed. */
const IN_AFTER_SETTLED_MS = 220;

/** Per-crossing timings, milliseconds. `out` is 0 wherever the client cannot see the crossing coming. */
const TIMING: Record<CrossingKind, { out: number; in: number; caption: string }> = {
  // Asked for, and a journey in the fiction: the shuttle has a boarding window, so time passing is honest here.
  travel: { out: 180, in: 220, caption: 'Travelling…' },

  // A door. Server-initiated, so fade IN only, and quick — a crossing that takes longer than stepping through a
  // doorway reads as a loading screen rather than as a step.
  transition: { out: 0, in: 120, caption: '' },

  // Nothing travels into space in this demo; the camera looks somewhere else. A dissolve with no caption, because
  // captioning it would claim a journey that does not happen.
  channel: { out: 120, in: 120, caption: '' },

  // Arriving from nothing, and leaving to nothing.
  arrive: { out: 0, in: 160, caption: '' },
  leave: { out: 0, in: 0, caption: '' },
};

/**
 * The viewer asked to be put in another realm.
 *
 * Ignored unless idle: a second ask during a crossing must not restart the ramp, or a viewer clicking twice would be
 * held at full opacity for as long as they kept clicking.
 */
export function askedFor(state: TransitionState, kind: CrossingKind, nowMs: number): TransitionState {
  if (state.phase !== 'idle') {
    return state;
  }

  const timing = TIMING[kind];
  if (timing.out === 0) {
    return state;
  }

  return { phase: 'out', sinceMs: nowMs, durationMs: timing.out, caption: timing.caption, settled: false };
}

/**
 * The realm changed: a `REALM` block has been applied and the scene has been swapped.
 *
 * <b>Always ends in a fade IN, whatever it interrupted.</b> Arriving straight from `idle` is the server-initiated case
 * — a door — and is the common one; arriving during `out` is the crossing the viewer asked for, arriving before the
 * ramp finished. Either way the screen has to come back, and it comes back from wherever it had got to.
 */
export function arrived(state: TransitionState, kind: CrossingKind, nowMs: number): TransitionState {
  const timing = TIMING[kind];
  if (timing.in === 0) {
    return IDLE;
  }

  // A second arrival during a fade-IN does not restart it. Snapping the screen back to opaque to fade it in again is
  // the flash the `out` branch below exists to avoid, reached from the other side — and a door opening onto the tail of
  // a previous crossing is exactly when it happens.
  if (state.phase === 'in') {
    return state;
  }

  // Let the fade-out finish first: cutting to full opacity mid-ramp is a flash, and more noticeable than the crossing
  // it was hiding. The arrival is RECORDED rather than acted on, and `advance` turns it into the fade-in when the ramp
  // completes — this is the ordinary case against a local server, where the frame beats the fade every time.
  if (state.phase === 'out' && nowMs - state.sinceMs < state.durationMs) {
    return { ...state, settled: true };
  }

  return { phase: 'in', sinceMs: nowMs, durationMs: timing.in, caption: '', settled: true };
}

/**
 * The crossing is not coming: the server refused the ask, or the local rate limit did.
 *
 * <b>The screen comes back at once rather than waiting out {@link HOLD_LIMIT_MS}.</b> The limit is a safety valve for
 * the case where nothing answers at all; an answer that says "no" is not that case, and making a viewer stare at an
 * opaque screen for two and a half seconds after the server already replied is the thing the reason codes exist to
 * prevent.
 */
export function refused(state: TransitionState, nowMs: number): TransitionState {
  if (state.phase === 'idle' || state.phase === 'in') {
    return state;
  }

  return { phase: 'in', sinceMs: nowMs, durationMs: REFUSED_IN_MS, caption: '', settled: false };
}

/**
 * Advances the state for the clock alone: finishes a ramp, and gives the screen back when a hold has waited too long.
 *
 * @returns the state at `nowMs`, which is `state` itself when nothing changed.
 */
export function advance(state: TransitionState, nowMs: number): TransitionState {
  const elapsed = nowMs - state.sinceMs;
  switch (state.phase) {
    case 'out':
      if (elapsed < state.durationMs) {
        return state;
      }

      // A finished fade-out goes straight into the fade-in when the arrival already landed, and waits in `hold`
      // otherwise. Only the second of those can strand a viewer, which is why only it needs a time limit.
      return state.settled
        ? { phase: 'in', sinceMs: nowMs, durationMs: IN_AFTER_SETTLED_MS, caption: '', settled: true }
        : { ...state, phase: 'hold', sinceMs: nowMs, durationMs: 0 };

    case 'hold':
      // The safety valve. An ask that is refused, rate-limited or simply never answered leaves the viewer here, and a
      // black screen over a running world is worse than a crossing that visibly did not happen.
      return elapsed >= HOLD_LIMIT_MS ? { phase: 'in', sinceMs: nowMs, durationMs: 200, caption: '', settled: false } : state;

    case 'in':
      return elapsed >= state.durationMs ? IDLE : state;

    default:
      return state;
  }
}

/** How opaque the screen is now: 0 shows the world, 1 hides it completely. */
export function opacityAt(state: TransitionState, nowMs: number): number {
  const elapsed = nowMs - state.sinceMs;
  switch (state.phase) {
    case 'out':
      return state.durationMs <= 0 ? 1 : clamp01(elapsed / state.durationMs);
    case 'hold':
      return 1;
    case 'in':
      return state.durationMs <= 0 ? 0 : 1 - clamp01(elapsed / state.durationMs);
    default:
      return 0;
  }
}

function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}
