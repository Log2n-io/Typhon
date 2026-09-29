/**
 * The state of a ride: whether this session is anchored on an entity, and on which one.
 *
 * <b>Three identities, one of them authoritative.</b> A ride involves the netId the viewer clicked, the netId the
 * subject holds in the view the server is now sending, and the selection. They are not the same number: anchoring a
 * session re-sends its whole view and netIds are allocated densely per view, so the subject almost always arrives
 * under a different id. Only the second is the server's — it comes from the `SELF` block, which is what `Control`
 * fills — and everything that went wrong in the first version of this was two of the three disagreeing.
 *
 * So the ride is one value with three shapes rather than a number that means "asked" before the reset and "still
 * riding" after it:
 *
 * - `idle` — not riding, and not waiting to be.
 * - `asked` — a `Spectate` went out and the frame that answers it has not landed. The only id in hand is the one the
 *   viewer clicked, and it names an entity in the view being left.
 * - `held` — the server has said what this session controls. That id is the one to compare a selection against, and
 *   the clicked one is of no further use.
 *
 * <b>An ask carries the reset count it was made at, and that is load-bearing.</b> An accepted ride is always a
 * `RESET` — the session's profile changed — so a `SELF` read before one has arrived still describes the PREVIOUS
 * anchor. Switching from one subject to another without the gate therefore lands straight in `held` on the entity
 * being left, and then never releases when the new ask turns out to name something that has gone.
 */
export type RideState =
  | { readonly phase: 'idle' }
  | { readonly phase: 'asked'; readonly askedNetId: number; readonly resetsAtAsk: number }
  | { readonly phase: 'held'; readonly askedNetId: number; readonly subjectNetId: number };

/** Not riding, and not waiting to be. */
export const NOT_RIDING: RideState = { phase: 'idle' };

/**
 * What the app should do about the ride this frame, on top of the new state.
 *
 * `adopt` carries the subject's current netId: the selection has to follow it, because the reset that the ride caused
 * has just dropped whatever was selected before.
 */
export type RideAction =
  | { readonly kind: 'none' }
  | { readonly kind: 'adopt'; readonly netId: number }
  | { readonly kind: 'release' };

const NONE: RideAction = { kind: 'none' };
const RELEASE: RideAction = { kind: 'release' };

/**
 * Advances the ride from what the source reports this frame.
 *
 * @param prev The ride as of the last frame.
 * @param asked What the client last asked to ride, or 0 — its own belief, rolled back when an ask is refused.
 * @param self The netId of the entity the session controls, from `SELF`; 0 for none.
 * @param selected The selection, so an `adopt` is raised only when it is actually out of step.
 * @param resets How many `RESET` frames the source has applied; an ask is answered by one, never before.
 *
 * <b>"Not given a subject yet" and "lost the subject" are different states, and conflating them ended every ride a
 * few milliseconds after it began.</b> `SELF` does not arrive in the same frame as the `RESET` the ride causes, so a
 * machine that released whenever `self` was 0 released on the ride's own first frame, every time. That is what the
 * `asked` phase is for: it is the window in which 0 means "not yet".
 *
 * <b>A release is raised for as long as the subject is gone</b>, not once. The ask is rate-limited at one a tick and
 * this runs sixty times a second, so the CALLER must throttle it; what it must not do is give up after one attempt,
 * because a release the local bucket refused would then leave the session anchored on a dead entity with no control
 * on screen to try again — the selection is 0, so the inspector is not even open.
 */
export function nextRide(
  prev: RideState,
  asked: number,
  self: number,
  selected: number,
  resets: number,
): { state: RideState; action: RideAction } {
  if (asked === 0) {
    // Released, or never started. Held state is dropped with it: keeping it would let a later ride of a REUSED id
    // inherit this one's subject.
    return { state: NOT_RIDING, action: NONE };
  }

  // A different ask than the one being tracked — the viewer switched subjects — starts again from `asked`, whatever
  // the old ride had reached.
  const tracked: RideState =
    prev.phase === 'idle' || prev.askedNetId !== asked ? { phase: 'asked', askedNetId: asked, resetsAtAsk: resets } : prev;

  // `SELF` is only about THIS ask once a reset has answered it; before that it still describes the anchor being left.
  if (self !== 0 && (tracked.phase === 'held' || resets > tracked.resetsAtAsk)) {
    const state: RideState = { phase: 'held', askedNetId: asked, subjectNetId: self };
    return { state, action: selected === self ? NONE : { kind: 'adopt', netId: self } };
  }

  // The server says this session controls nothing. Before the ride's frame lands that is simply "not yet"; after it,
  // the subject died or left, and a camera riding a corpse is worse than no ride.
  return tracked.phase === 'held' ? { state: tracked, action: RELEASE } : { state: tracked, action: NONE };
}

/**
 * The subject's netId as the UI should name it: the server's once it is known, and the asked one until then.
 *
 * The fallback matters for the second or two between the click and the frame: without it the inspector's button
 * flickers back to `Ride` on the entity the viewer has just asked to ride.
 */
export function rideSubjectOf(ride: RideState): number {
  switch (ride.phase) {
    case 'held':
      return ride.subjectNetId;
    case 'asked':
      return ride.askedNetId;
    default:
      return 0;
  }
}

/**
 * Whether the session's replication is anchored on an entity rather than on a region the client chose.
 *
 * <b>What the region sender has to ask before sending anything.</b> An anchored session is served a sphere around its
 * subject and its `ClientRegion` is not read at all; sending one is not merely waste, because a planet-sized disc
 * quantized into a 64 m interior collapses to a degenerate hull, which the server refuses and counts — so the HUD
 * grows a refusal warning that means nothing is wrong. `asked` counts as anchored: the ask may already have been
 * applied on the server by the time the client's next region would go out.
 */
export function rideOwnsTheView(ride: RideState): boolean {
  return ride.phase !== 'idle';
}
