import { describe, expect, it } from 'vitest';
import { nextRide, NOT_RIDING, rideOwnsTheView, rideSubjectOf, type RideState } from '../src/app/ride';

/**
 * Drives the machine over a script of `[asked, self, selected, resets]` frames, collecting what it asked the app to do.
 *
 * `resets` defaults to 1, and a script that starts from `idle` with SELF already present has to spell out the ask at
 * 0 and the answer at 1 — because the gate is the point: an ask is answered by a RESET, never before one.
 */
function run(frames: [number, number, number, number?][], from: RideState = NOT_RIDING) {
  let state = from;
  const actions: string[] = [];
  for (const [asked, self, selected, resets] of frames) {
    const step = nextRide(state, asked, self, selected, resets ?? 1);
    state = step.state;
    actions.push(step.action.kind === 'adopt' ? `adopt:${step.action.netId}` : step.action.kind);
  }

  return { state, actions };
}

describe('a ride that is taking hold', () => {
  it('does NOT release while the ask is unanswered, which is the bug that ended every ride it started', () => {
    // SELF does not arrive in the frame that carries the ride's own RESET. A machine that released whenever the
    // session controlled nothing released on the ride's first frame — every time, against a local server in under a
    // millisecond. Only a live run found it; the unit under test could not be constructed at the time.
    const { state, actions } = run([
      [42, 0, 42, 0],
      [42, 0, 0, 0],
      [42, 0, 0, 1],
    ]);

    expect(actions).toEqual(['none', 'none', 'none']);
    expect(state.phase).toBe('asked');
  });

  it('adopts the subject under the netId the SERVER named, not the one that was clicked', () => {
    // Anchoring re-sends the view and netIds are dense per view, so the subject arrives under a different id. This is
    // the whole reason SELF is read at all: the client cannot compute it.
    const { state, actions } = run([
      [42, 0, 0, 0],
      [42, 907, 0, 1],
    ]);

    expect(actions).toEqual(['none', 'adopt:907']);
    expect(state).toEqual({ phase: 'held', askedNetId: 42, subjectNetId: 907 });
  });

  it('stops asking once the selection has caught up, so it does not re-select every frame', () => {
    expect(
      run([
        [42, 0, 42, 0],
        [42, 907, 907, 1],
      ]).actions,
    ).toEqual(['none', 'none']);
  });
});

describe('a ride that ended', () => {
  it('releases when a subject it HELD goes away, because a camera riding a corpse is worse than no ride', () => {
    const { actions } = run([
      [42, 0, 42, 0],
      [42, 907, 0, 1],
      [42, 0, 907, 1],
    ]);

    expect(actions).toEqual(['none', 'adopt:907', 'release']);
  });

  it('keeps asking for as long as the subject is gone, because the ask is rate-limited and may not have been sent', () => {
    // A release the local bucket refused must not be the only attempt: the selection is already 0, so the inspector is
    // closed and there is no control on screen to try again with. Throttling is the caller's job; giving up is not.
    const { actions } = run([
      [42, 0, 42, 0],
      [42, 907, 907, 1],
      [42, 0, 0, 1],
      [42, 0, 0, 1],
      [42, 0, 0, 1],
    ]);

    expect(actions).toEqual(['none', 'none', 'release', 'release', 'release']);
  });

  it('goes idle the moment the client stops believing it is riding', () => {
    const { state, actions } = run([
      [42, 0, 42, 0],
      [42, 907, 907, 1],
      [0, 907, 907, 1],
    ]);

    expect(actions).toEqual(['none', 'none', 'none']);
    expect(state).toBe(NOT_RIDING);
  });
});

describe('switching subjects, and a netId that comes back', () => {
  it('waits for the switch’s own RESET rather than reading the anchor it is leaving', () => {
    const { state, actions } = run([
      [42, 0, 42, 0],
      [42, 907, 907, 1],
      [77, 907, 907, 1],
    ]);

    // 907 is the OLD subject and SELF still names it: the switch's reset has not arrived. Taking it would land in
    // `held` on the entity being left, and then never release when the new ask turned out to name something gone.
    expect(state).toEqual({ phase: 'asked', askedNetId: 77, resetsAtAsk: 1 });
    expect(actions[2]).toBe('none');
  });

  it('adopts the new subject once that RESET has landed', () => {
    const { state, actions } = run([
      [42, 0, 42, 0],
      [42, 907, 907, 1],
      [77, 907, 907, 1],
      [77, 512, 907, 2],
    ]);

    expect(state).toEqual({ phase: 'held', askedNetId: 77, subjectNetId: 512 });
    expect(actions).toEqual(['none', 'none', 'none', 'adopt:512']);
  });

  it('does not let a REUSED netId inherit the subject of the ride that used it before', () => {
    // netIds are dense and reused. Ride 42, stop, and 42 is handed to something else; asking for it again must not
    // resolve to the first ride's subject.
    const held = run([
      [42, 0, 42, 0],
      [42, 907, 907, 1],
    ]).state;
    expect(held.phase).toBe('held');
    const released = nextRide(held, 0, 0, 0, 1).state;
    expect(released).toBe(NOT_RIDING);

    const again = nextRide(released, 42, 0, 0, 4);
    expect(again.state).toEqual({ phase: 'asked', askedNetId: 42, resetsAtAsk: 4 });
    expect(again.action.kind).toBe('none');
  });

  it('survives an ask that is refused: the belief rolls back to 0 and the machine follows it', () => {
    const { state } = run([
      [42, 0, 42, 0],
      [0, 0, 42, 0],
    ]);

    expect(state).toBe(NOT_RIDING);
  });
});

describe('what the rest of the app reads off it', () => {
  it('names the server’s subject once known and the asked one until then, so the button does not flicker', () => {
    expect(rideSubjectOf(NOT_RIDING)).toBe(0);
    expect(rideSubjectOf({ phase: 'asked', askedNetId: 42, resetsAtAsk: 0 })).toBe(42);
    expect(rideSubjectOf({ phase: 'held', askedNetId: 42, subjectNetId: 907 })).toBe(907);
  });

  it('owns the view from the ASK, not from the arrival, because the server may have applied it already', () => {
    // The region sender reads this. An ask applied on the server before the client's next region goes out would
    // otherwise send a planet-sized disc into a sphere profile, which is the degenerate hull the server refuses.
    expect(rideOwnsTheView(NOT_RIDING)).toBe(false);
    expect(rideOwnsTheView({ phase: 'asked', askedNetId: 42, resetsAtAsk: 0 })).toBe(true);
    expect(rideOwnsTheView({ phase: 'held', askedNetId: 42, subjectNetId: 907 })).toBe(true);
  });
});
