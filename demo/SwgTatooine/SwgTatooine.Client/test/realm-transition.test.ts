import { describe, expect, it } from 'vitest';
import { advance, arrived, askedFor, IDLE, opacityAt, refused } from '../src/app/realm-transition';

describe('a crossing the viewer asked for', () => {
  it('fades out from the ask, because that is the only moment there is still a world to fade out of', () => {
    const out = askedFor(IDLE, 'travel', 1000);
    expect(out.phase).toBe('out');
    expect(opacityAt(out, 1000)).toBe(0);
    expect(opacityAt(out, 1090)).toBeCloseTo(0.5, 2);
    expect(opacityAt(out, 1180)).toBe(1);
  });

  it('holds at full opacity once the ramp is done, waiting for the frame', () => {
    const held = advance(askedFor(IDLE, 'travel', 1000), 1180);
    expect(held.phase).toBe('hold');
    expect(opacityAt(held, 5000)).toBe(1);
  });

  it('does not restart when the viewer clicks again mid-crossing', () => {
    // Without this a viewer clicking twice would be held at full opacity for as long as they kept clicking.
    const out = askedFor(IDLE, 'travel', 1000);
    expect(askedFor(out, 'travel', 1100)).toBe(out);
  });

  it('lets the fade-out finish when the frame beats it, rather than cutting to black', () => {
    // A frame that lands mid-ramp must not snap the screen to opaque: a flash is more noticeable than the crossing it
    // was hiding. It stays in `out` and records the arrival.
    const out = askedFor(IDLE, 'travel', 1000);
    const early = arrived(out, 'travel', 1050);
    expect(early.phase).toBe('out');
    expect(early.settled).toBe(true);
  });

  it('comes back on its OWN once an early arrival has been recorded, without waiting out the hold limit', () => {
    // The bug this file's first version encoded as correct, and that only a live run exposed. Against a local server
    // the round trip is about a millisecond, so the frame lands well inside the 180 ms fade-out — every single time.
    // Parking that in `hold` meant the happy path was rescued by the 2.5 s safety valve, i.e. a two-and-a-half second
    // black screen on every realm change that worked.
    const early = arrived(askedFor(IDLE, 'travel', 1000), 'travel', 1050);

    const ramping = advance(early, 1180);
    expect(ramping.phase).toBe('in');

    // Back to a visible world inside half a second of the ask, against 2 500 ms before.
    expect(opacityAt(ramping, 1180 + 220)).toBe(0);
    expect(advance(ramping, 1180 + 220)).toBe(IDLE);
  });

  it('still waits in hold when the arrival has NOT landed, which is the only case that can strand a viewer', () => {
    const waiting = advance(askedFor(IDLE, 'travel', 1000), 1180);
    expect(waiting.phase).toBe('hold');
    expect(waiting.settled).toBe(false);
  });

  it('carries a caption only where the fiction has one', () => {
    expect(askedFor(IDLE, 'travel', 0).caption).toBe('Travelling…');
    // Nothing travels into space in this demo; the camera looks somewhere else. Captioning it would claim a journey.
    expect(askedFor(IDLE, 'channel', 0).caption).toBe('');
  });
});

describe('a crossing the world imposed', () => {
  it('fades IN only, because the store was already empty when the client heard about it', () => {
    // A door is server-initiated: `onRealmChanged` fires after the RESET, so there is nothing left to fade out of, and
    // faking one would mean delaying the frame's apply — which SUB-29 forbids.
    expect(askedFor(IDLE, 'transition', 1000)).toBe(IDLE);

    const back = arrived(IDLE, 'transition', 1000);
    expect(back.phase).toBe('in');
    expect(opacityAt(back, 1000)).toBe(1);
    expect(opacityAt(back, 1120)).toBe(0);
  });

  it('is quicker than a journey, because stepping through a doorway is not travel', () => {
    const door = arrived(IDLE, 'transition', 0);
    const journey = arrived(IDLE, 'travel', 0);
    expect(door.durationMs).toBeLessThan(journey.durationMs);
  });

  it('leaves nothing on screen when the session was left in no realm at all', () => {
    expect(arrived(IDLE, 'leave', 1000)).toBe(IDLE);
  });
});

describe('a crossing the server refused', () => {
  it('gives the screen back at once, not after the safety limit', () => {
    // The server was given reason codes so a refusal would be AUDIBLE; for a long while nothing on the client listened,
    // and a refused crossing was indistinguishable from one that was merely slow — 2.5 s of opaque screen either way.
    const waiting = advance(askedFor(IDLE, 'travel', 0), 180);
    expect(waiting.phase).toBe('hold');

    const back = refused(waiting, 200);
    expect(back.phase).toBe('in');
    expect(back.durationMs).toBeLessThan(200);
    expect(opacityAt(back, 200 + back.durationMs)).toBe(0);
  });

  it('interrupts a fade-out that has not finished, rather than completing it for nothing', () => {
    expect(refused(askedFor(IDLE, 'travel', 0), 50).phase).toBe('in');
  });

  it('does nothing to a screen that is already coming back, or already clear', () => {
    const coming = arrived(IDLE, 'transition', 0);
    expect(refused(coming, 10)).toBe(coming);
    expect(refused(IDLE, 10)).toBe(IDLE);
  });
});

describe('a second arrival during the fade-in', () => {
  it('does not restart it, because a snap back to opaque is the flash the out branch avoids', () => {
    const coming = arrived(IDLE, 'transition', 1000);
    expect(arrived(coming, 'transition', 1040)).toBe(coming);
  });
});

describe('the hold limit, which is what keeps a failure cosmetic', () => {
  it('gives the screen back when the arrival never comes', () => {
    // The most important case here. `ViewRealm` is rate-limited, refused for a realm that does not exist, and answered
    // by a frame the client does not control — so every failure mode ends in a hold that receives nothing. Without
    // this the result is a black screen over a live, working simulation.
    const held = advance(askedFor(IDLE, 'travel', 0), 180);
    expect(held.phase).toBe('hold');

    expect(advance(held, 180 + 2400).phase).toBe('hold');

    const rescued = advance(held, 180 + 2600);
    expect(rescued.phase).toBe('in');
    expect(opacityAt(rescued, 180 + 2600 + 200)).toBe(0);
  });

  it('returns to idle after the ramp, so nothing is left covering the world', () => {
    const back = arrived(IDLE, 'transition', 0);
    expect(advance(back, 119).phase).toBe('in');
    expect(advance(back, 120)).toBe(IDLE);
    expect(opacityAt(IDLE, 99999)).toBe(0);
  });
});

describe('advance', () => {
  it('returns the SAME object when nothing moved, so the store does not re-render every frame', () => {
    // The overlay lives in a zustand store React subscribes to. Publishing an equal-but-new object each frame would
    // re-render the whole UI sixty times a second to show an unchanged screen.
    const out = askedFor(IDLE, 'travel', 1000);
    expect(advance(out, 1100)).toBe(out);
    expect(advance(IDLE, 99999)).toBe(IDLE);
  });

  it('never reports an opacity outside 0..1, however late the frame', () => {
    const out = askedFor(IDLE, 'travel', 1000);
    expect(opacityAt(out, 99999)).toBe(1);
    expect(opacityAt(out, 0)).toBe(0);
    expect(opacityAt(arrived(IDLE, 'travel', 1000), 99999)).toBe(0);
  });
});
