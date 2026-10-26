import { describe, expect, it } from 'vitest';
import {
  ANSWER_TIMEOUT_MS,
  BOARDING_SETTLE_MS,
  NOTICE_MS,
  NOT_TRAVELLING,
  nextTravel,
  REASON_NO_ANSWER,
  REASON_NOT_SENT,
  travelIsUnderway,
  type TravelInputs,
  type TravelState,
} from '../src/app/travel';

/**
 * What a person is told while a shuttle request is in flight.
 *
 * <b>Every case here is a thing the browser used to say nothing about.</b> A click produced no visible change until the
 * body appeared in another city, so the states that matter are the ones in between: the ask, the queue, the refusal and
 * the two ways a queue can end. They are asserted as a machine rather than through the panel because the panel is a
 * rendering of this and the interesting content is the transitions.
 */
const AWAITING = 5;
const IDLE = 0;

/** A frame's worth of inputs, with the quiet defaults a test overrides one at a time. */
function frame(over: Partial<TravelInputs> = {}): TravelInputs {
  return {
    ask: { city: 3, seq: 1 },
    activity: IDLE,
    refusal: null,
    x: 0,
    z: 0,
    destination: { x: 1000, z: 1000, radiusM: 330 },
    nowMs: 1_000,
    ...over,
  };
}

/** Drives the machine through a list of frames and returns every state it passed through. */
function run(frames: readonly Partial<TravelInputs>[], from: TravelState = NOT_TRAVELLING): TravelState[] {
  const seen: TravelState[] = [];
  let state = from;
  for (const over of frames) {
    state = nextTravel(state, frame(over));
    seen.push(state);
  }

  return seen;
}

describe('travel', () => {
  it('says nothing until something is asked', () => {
    expect(nextTravel(NOT_TRAVELLING, frame({ ask: null }))).toBe(NOT_TRAVELLING);
  });

  it('is asking from the moment the ask goes out', () => {
    const state = nextTravel(NOT_TRAVELLING, frame());
    expect(state).toMatchObject({ phase: 'asking', city: 3, seq: 1 });
    expect(travelIsUnderway(state)).toBe(true);
  });

  it('becomes a queue when the server puts the body in one, and counts from that instant', () => {
    const [, queued, later] = run([{}, { activity: AWAITING, nowMs: 2_000 }, { activity: AWAITING, nowMs: 9_000 }]);
    expect(queued).toMatchObject({ phase: 'queued', city: 3, sinceMs: 2_000 });

    // The SAME object, so the panel's elapsed figure counts from one instant instead of restarting every frame. This is
    // the whole point of the phase: a person watching it needs the number to grow.
    expect(later).toBe(queued);
  });

  it('reports the server refusing, with its reason', () => {
    const [, refused] = run([{}, { refusal: 131, nowMs: 1_200 }]);
    expect(refused).toMatchObject({ phase: 'refused', reason: 131, city: 3 });
    expect(travelIsUnderway(refused)).toBe(false);
  });

  it('reports an ask the client never sent, which no server will ever answer', () => {
    const [, refused] = run([{}, { refusal: REASON_NOT_SENT }]);
    expect(refused).toMatchObject({ phase: 'refused', reason: REASON_NOT_SENT });
  });

  it('stops saying "asking" when nothing answers at all', () => {
    // A command lost on the wire produces no ack and no activity change, so without the timeout the panel would claim
    // an ask was in flight for the rest of the session.
    const [, waiting, timedOut] = run([
      {},
      { nowMs: 1_000 + ANSWER_TIMEOUT_MS - 1 },
      { nowMs: 1_000 + ANSWER_TIMEOUT_MS },
    ]);
    expect(waiting).toMatchObject({ phase: 'asking' });
    expect(timedOut).toMatchObject({ phase: 'refused', reason: REASON_NO_ANSWER });
  });

  it('does not classify the moment the body leaves the queue, because the position has not caught up', () => {
    // THE defect a browser found and no test of this machine could: the activity field applies at once while the
    // position is read through the interpolated motion record, so a completed flight to Port Sable classified itself
    // against the port it had just left and reported "stranded" on arrival.
    const [, , boarding] = run([{}, { activity: AWAITING }, { activity: IDLE, x: 5, z: 5, nowMs: 30_000 }]);
    expect(boarding).toMatchObject({ phase: 'boarding', city: 3 });
    expect(travelIsUnderway(boarding)).toBe(true);
  });

  it('calls it an arrival once the body has settled inside the city it asked for', () => {
    const [, , , arrived] = run([
      {},
      { activity: AWAITING },
      { activity: IDLE, nowMs: 30_000 },
      { activity: IDLE, x: 1100, z: 1050, nowMs: 30_000 + BOARDING_SETTLE_MS },
    ]);
    expect(arrived).toMatchObject({ phase: 'arrived', city: 3 });
  });

  it('calls it stranded once the body has settled without having moved', () => {
    // Reached when the shuttle system finds the queued port pair invalid: it sets the activity to idle and does not
    // move the body. Saying nothing here is what left a person waiting for a flight that was no longer coming.
    const [, , , stranded] = run([
      {},
      { activity: AWAITING },
      { activity: IDLE, nowMs: 60_000 },
      { activity: IDLE, x: 5, z: 5, nowMs: 60_000 + BOARDING_SETTLE_MS },
    ]);
    expect(stranded).toMatchObject({ phase: 'stranded', city: 3 });
  });

  it('says nothing when the body ends up somewhere it did not ask for', () => {
    // Neither an arrival nor a failed wait: the body was carried somewhere else entirely, which this client has no
    // evidence about and should not narrate.
    const [, , , elsewhere] = run([
      {},
      { activity: AWAITING },
      { activity: IDLE, nowMs: 2_000 },
      { activity: IDLE, x: -7000, z: 7000, nowMs: 2_000 + BOARDING_SETTLE_MS },
    ]);
    expect(elsewhere).toMatchObject({ phase: 'done', seq: 1 });
  });

  it('treats an unknown destination as unprovable rather than as an arrival', () => {
    const [, , , unknown] = run([
      {},
      { activity: AWAITING },
      { activity: IDLE, destination: null, nowMs: 2_000 },
      { activity: IDLE, destination: null, x: 4000, z: 4000, nowMs: 2_000 + BOARDING_SETTLE_MS },
    ]);
    expect(unknown).toMatchObject({ phase: 'done' });
  });

  it('retires an outcome once it has been on screen long enough, without re-arming the ask', () => {
    // The OTHER defect a browser found. The ask stays in the store after it is answered, so retiring an outcome to
    // `idle` made the machine see an unhandled ask and start again: asking, timing out, clearing, asking, for as long
    // as the panel was open. `done` remembers the sequence, so the answer is final until a person asks something new.
    const [, refused, shown, retired, stillRetired] = run([
      {},
      { refusal: 132, nowMs: 1_000 },
      { nowMs: 1_000 + NOTICE_MS - 1 },
      { nowMs: 1_000 + NOTICE_MS },
      { nowMs: 1_000 + NOTICE_MS + 10_000 },
    ]);
    expect(refused).toMatchObject({ phase: 'refused' });
    expect(shown).toBe(refused);
    expect(retired).toMatchObject({ phase: 'done', seq: 1 });
    expect(stillRetired).toBe(retired);
    expect(travelIsUnderway(retired)).toBe(false);
  });

  it('starts again on a second ask, including for the city it has just refused', () => {
    const [, refused, again] = run([{}, { refusal: 132 }, { ask: { city: 3, seq: 2 }, nowMs: 2_000 }]);
    expect(refused).toMatchObject({ phase: 'refused', seq: 1 });
    expect(again).toMatchObject({ phase: 'asking', city: 3, seq: 2, sinceMs: 2_000 });
  });

  it('starts again after an outcome has been retired, when a person asks something new', () => {
    const [, , retired, again] = run([
      {},
      { refusal: 132, nowMs: 1_000 },
      { nowMs: 1_000 + NOTICE_MS },
      { ask: { city: 5, seq: 2 }, nowMs: 20_000 },
    ]);
    expect(retired).toMatchObject({ phase: 'done' });
    expect(again).toMatchObject({ phase: 'asking', city: 5, seq: 2 });
  });

  it('abandons a queue when the ask is withdrawn', () => {
    const [, , dropped] = run([{}, { activity: AWAITING }, { ask: null, activity: AWAITING }]);
    expect(dropped).toBe(NOT_TRAVELLING);
  });

  it('ignores an activity it cannot read', () => {
    // A server that does not replicate `activity` sends NaN here. It cannot be asked about a queue, so the ask simply
    // goes unanswered rather than being reported as accepted.
    const [, waiting] = run([{}, { activity: Number.NaN, nowMs: 1_500 }]);
    expect(waiting).toMatchObject({ phase: 'asking' });
  });
});
