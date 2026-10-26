/**
 * What is happening to a travel request, as the only three things that can report it actually say.
 *
 * <b>Why a machine and not a boolean.</b> Boarding a shuttle is the one action in this client whose outcome arrives
 * later, somewhere else, and through three unrelated channels: the server may refuse the ask (an `ACKS` reason), it may
 * accept it and leave the body standing in a queue for a shuttle that lands on its own schedule (the avatar's own
 * `activity` field), and the journey itself is a teleport that shows up as the body being somewhere else. Until this
 * existed the browser said nothing about any of it — a person clicked a city and the world did not visibly change, so
 * the only readings available were "it is broken" and "wait longer", and those are indistinguishable.
 *
 * <b>Nothing here is predicted and nothing is invented.</b> Each phase is entered because something observable said so,
 * which is why there is no `flying`: no frame ever describes a shuttle in transit, so a client claiming one would be
 * writing fiction. The body queues, and then the body is elsewhere.
 *
 * - `idle` — nothing asked, or the last outcome has been shown long enough.
 * - `asking` — a `BoardShuttle` went out and nothing has answered it yet.
 * - `queued` — the server put the body in {@link PlayerActivity.AwaitingShuttle}: the ask was accepted and the wait has
 *   begun. This is the phase a person spends real seconds in, so it carries when it started.
 * - `refused` — the server said no, with its reason.
 * - `arrived` — the body left the queue and is inside the city it asked for. The flight happened.
 * - `boarding` — the body has left the queue and where it ends up is not yet known. One frame of settling, because the
 *   activity and the arrival do NOT land together: the field update applies immediately while the position is read
 *   through the motion record the renderer interpolates, so classifying on the first frame compares the new activity
 *   against the old position and calls every flight a failure. Found in a browser, where a completed journey to Port
 *   Sable reported itself stranded at the port it had just left.
 * - `stranded` — the body left the queue and did not go anywhere, which the shuttle system does when the port pair it
 *   was queued with turns out to be invalid: the activity drops to idle and the body is not moved. Saying nothing here
 *   would leave a person waiting for a flight that is no longer coming.
 * - `done` — an ask that has been answered and shown. <b>Not the same as `idle`, and conflating them span the machine in
 *   a loop.</b> The ask itself stays in the store after it is answered, so a machine that fell back to `idle` saw an ask
 *   it had never handled and started again: asking, timing out, clearing, asking. Remembering the sequence is what makes
 *   an answer final until a person asks something new.
 *
 * <b>A queued human is not on a timer, and that is the server's behaviour rather than an omission here.</b>
 * `ShuttleWaitTicks` bounds the wait for the simulated crowd only, because `PlayerThink` skips possessed bodies — so a
 * person who queues waits until a shuttle actually lands. That is precisely why the queue phase carries an elapsed
 * figure: it is the only thing that distinguishes waiting from being stuck.
 */
export type TravelState =
  | { readonly phase: 'idle' }
  | { readonly phase: 'asking'; readonly city: number; readonly seq: number; readonly sinceMs: number }
  | {
      readonly phase: 'queued';
      readonly city: number;
      readonly seq: number;
      readonly sinceMs: number;
      readonly fromX: number;
      readonly fromZ: number;
    }
  | {
      readonly phase: 'refused';
      readonly city: number;
      readonly seq: number;
      readonly reason: number;
      readonly atMs: number;
    }
  | {
      readonly phase: 'boarding';
      readonly city: number;
      readonly seq: number;
      readonly sinceMs: number;
      readonly fromX: number;
      readonly fromZ: number;
    }
  | { readonly phase: 'arrived'; readonly city: number; readonly seq: number; readonly atMs: number }
  | { readonly phase: 'stranded'; readonly city: number; readonly seq: number; readonly atMs: number }
  | { readonly phase: 'done'; readonly seq: number };

/** Nothing asked. */
export const NOT_TRAVELLING: TravelState = { phase: 'idle' };

/**
 * A refusal that never reached the server: the client's own command bucket dropped the ask.
 *
 * <b>Its own reason because no `ACKS` will ever arrive for it.</b> The server acknowledges commands it received; one
 * refused locally was never sent, so a machine waiting for an answer would wait for ever. The rate limiter exists and
 * does fire — clicking a city twice in one tick is enough — so this is a state a person can reach by hand.
 */
export const REASON_NOT_SENT = -1;

/**
 * No answer at all within {@link ANSWER_TIMEOUT_MS}.
 *
 * Distinct from a refusal, and deliberately vague about whose fault it is, because the client cannot tell: a command
 * lost on the wire, a server that dropped it and a frame that never came all look identical from here. What matters is
 * that the person stops being told "asking" about something nothing is going to answer.
 */
export const REASON_NO_ANSWER = -2;

/** How long to wait for an accept or a refusal before calling the ask unanswered. */
export const ANSWER_TIMEOUT_MS = 3_000;

/** How long an outcome stays on screen before the panel goes back to offering destinations. */
export const NOTICE_MS = 6_000;

/**
 * How long to let a boarding settle before deciding where the body ended up.
 *
 * The activity field and the arrival are applied in the same server tick but are not read by the client at the same
 * time: the field is immediate and the position comes through the interpolated motion record. A fifth of a second is
 * several frames at any rate this client runs at, and it is far below the time any shuttle takes.
 */
export const BOARDING_SETTLE_MS = 200;

/** `PlayerActivity.AwaitingShuttle` — the body is in the port's queue. */
const AWAITING_SHUTTLE = 5;

/** Where the body is allowed to have drifted and still count as "it never left": the server's own boarding radius. */
const DID_NOT_MOVE_M = 20;

/** What the reducer is told each frame. */
export interface TravelInputs {
  /**
   * The client's own last ask, or null when it has never asked.
   *
   * Its own belief rather than anything the server sent, because the ask is the only part of this the client knows
   * first. The sequence is what distinguishes a second ask for the same city from no new ask at all.
   */
  readonly ask: { readonly city: number; readonly seq: number } | null;

  /** The avatar's own `activity`, from `SELF`; `NaN` when the server does not replicate it or there is no avatar. */
  readonly activity: number;

  /**
   * An outcome for the pending ask that landed this frame: an `ACKS` reason, {@link REASON_NOT_SENT}, or null.
   *
   * One frame only. The caller drains it, so a reason seen here will not be seen again.
   */
  readonly refusal: number | null;

  /** Where the avatar is, east. */
  readonly x: number;

  /** Where the avatar is, north. */
  readonly z: number;

  /** The city that was asked for, as the client's map data describes it, or null when the index names none. */
  readonly destination: { readonly x: number; readonly z: number; readonly radiusM: number } | null;

  /** Now, on the same clock every phase stamps. */
  readonly nowMs: number;
}

/**
 * Advances the travel state from what this frame reports.
 *
 * @param prev The state as of the last frame.
 * @param input What is observable now.
 * @returns The new state; `prev` itself when nothing moved, so a caller may compare by identity.
 *
 * <b>A new ask always wins.</b> Whatever the machine had reached, a person clicking a destination is a fresh question
 * and the old answer stops being interesting — including when the old answer was a refusal of the same city.
 *
 * <b>Leaving the queue is read by where the body ended up, not by a message.</b> There is no "you have arrived" on the
 * wire: the shuttle moves the body and the activity drops back to idle. So the two outcomes are told apart by
 * geometry — inside the destination's disc is an arrival, and standing within {@link DID_NOT_MOVE_M} of where the wait
 * began is a shuttle that never came. Anything else is neither, and the machine says nothing rather than guessing: a
 * body that was carried somewhere unasked-for is a story this client has no evidence for.
 */
export function nextTravel(prev: TravelState, input: TravelInputs): TravelState {
  const { ask, activity, refusal, nowMs } = input;
  if (ask === null) {
    return prev.phase === 'idle' ? prev : NOT_TRAVELLING;
  }

  // A fresh ask restarts the machine, whatever it had reached.
  if (prev.phase === 'idle' || prev.seq !== ask.seq) {
    return { phase: 'asking', city: ask.city, seq: ask.seq, sinceMs: nowMs };
  }

  switch (prev.phase) {
    case 'asking': {
      if (refusal !== null) {
        return { phase: 'refused', city: prev.city, seq: prev.seq, reason: refusal, atMs: nowMs };
      }

      if (activity === AWAITING_SHUTTLE) {
        return { phase: 'queued', city: prev.city, seq: prev.seq, sinceMs: nowMs, fromX: input.x, fromZ: input.z };
      }

      if (nowMs - prev.sinceMs >= ANSWER_TIMEOUT_MS) {
        return { phase: 'refused', city: prev.city, seq: prev.seq, reason: REASON_NO_ANSWER, atMs: nowMs };
      }

      return prev;
    }

    case 'queued': {
      if (activity === AWAITING_SHUTTLE) {
        // Still waiting. Held as the same object so the panel's elapsed figure counts from one instant rather than
        // restarting every frame.
        return prev;
      }

      // Off the queue: something happened, and which thing it was cannot be read yet.
      return {
        phase: 'boarding',
        city: prev.city,
        seq: prev.seq,
        sinceMs: nowMs,
        fromX: prev.fromX,
        fromZ: prev.fromZ,
      };
    }

    case 'boarding': {
      if (nowMs - prev.sinceMs < BOARDING_SETTLE_MS) {
        return prev;
      }

      const destination = input.destination;
      if (destination !== null) {
        const dx = input.x - destination.x;
        const dz = input.z - destination.z;
        if (dx * dx + dz * dz <= destination.radiusM * destination.radiusM) {
          return { phase: 'arrived', city: prev.city, seq: prev.seq, atMs: nowMs };
        }
      }

      const bx = input.x - prev.fromX;
      const bz = input.z - prev.fromZ;
      if (bx * bx + bz * bz <= DID_NOT_MOVE_M * DID_NOT_MOVE_M) {
        return { phase: 'stranded', city: prev.city, seq: prev.seq, atMs: nowMs };
      }

      // Carried somewhere that was not asked for, which this client has no evidence about and will not narrate.
      return { phase: 'done', seq: prev.seq };
    }

    case 'done':
      return prev;

    default:
      // An outcome, shown for a while and then retired. `done` and not `idle`, so the ask that is still sitting in the
      // store cannot start the machine over.
      return nowMs - prev.atMs >= NOTICE_MS ? { phase: 'done', seq: prev.seq } : prev;
  }
}

/** Whether the panel should hide its destination buttons: an ask is in flight, queued, or landing. */
export function travelIsUnderway(state: TravelState): boolean {
  return state.phase === 'asking' || state.phase === 'queued' || state.phase === 'boarding';
}
