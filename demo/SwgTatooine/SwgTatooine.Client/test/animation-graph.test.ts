import { describe, expect, it } from 'vitest';
import type { ClipDesc, RoleMap } from '../src/assets/descriptor';
import { AnimationGraph, LOCOMOTION_REFERENCE_HEIGHT, locomotionFrom } from '../src/character/animation-graph';

/**
 * S2 — which clip a replicated body plays.
 *
 * The rules that matter are about thresholds and about one-shots winning over locomotion. Everything here is pure: no
 * engine, no rig, no socket.
 */

const ROLES: RoleMap = {
  idle: 'Idle',
  walk: 'Walk',
  run: 'Run',
  jump: 'Jump',
  roll: 'Roll',
  attack: ['Punch', 'SwordSlash'],
  hit: 'RecieveHit',
  death: 'Death',
};

const CLIPS: readonly ClipDesc[] = [
  { name: 'Idle', duration: 4.17, loop: true },
  { name: 'Walk', duration: 1.25, loop: true },
  { name: 'Run', duration: 0.88, loop: true },
  { name: 'Jump', duration: 1.04, loop: false },
  { name: 'Roll', duration: 0.92, loop: false },
  { name: 'Punch', duration: 0.75, loop: false },
  { name: 'SwordSlash', duration: 1.04, loop: false },
  { name: 'RecieveHit', duration: 0.62, loop: false },
  { name: 'Death', duration: 2.29, loop: false },
];

/** The server's own figures, as the owner block publishes them. */
const WALK = 2.5;
const RUN = 5;
const HEIGHT = 1.67;

function graph(): AnimationGraph {
  return new AnimationGraph(ROLES, CLIPS, locomotionFrom(WALK, RUN, HEIGHT));
}

describe('locomotion tuning', () => {
  it('puts the walk/run threshold halfway between the SERVER speeds', () => {
    expect(locomotionFrom(WALK, RUN, HEIGHT).runFrom).toBe(3.75);
    // Retuning a gait on the server moves the threshold with it, which is the whole reason it is not a constant.
    expect(locomotionFrom(4, 12, HEIGHT).runFrom).toBe(8);
  });

  it('scales the clip speeds with the body, and the thresholds not at all', () => {
    const tall = locomotionFrom(WALK, RUN, LOCOMOTION_REFERENCE_HEIGHT);
    const short = locomotionFrom(WALK, RUN, LOCOMOTION_REFERENCE_HEIGHT / 2);
    expect(short.walkClipSpeed).toBeCloseTo(tall.walkClipSpeed / 2, 10);
    expect(short.runClipSpeed).toBeCloseTo(tall.runClipSpeed / 2, 10);
    expect(short.runFrom).toBe(tall.runFrom);
  });

  it('falls back to the clips own speeds before the owner block arrives', () => {
    // NaN is what `selfNumber` answers until then. A tuning built from it must not produce NaN thresholds, or every
    // comparison below goes false and the body idles while it walks.
    const unknown = locomotionFrom(Number.NaN, Number.NaN, HEIGHT);
    expect(Number.isFinite(unknown.runFrom)).toBe(true);
    expect(Number.isFinite(unknown.idleBelow)).toBe(true);
    expect(unknown.runFrom).toBeGreaterThan(unknown.idleBelow);
  });
});

describe('locomotion', () => {
  it('idles, walks and runs by speed', () => {
    const g = graph();
    const { idleBelow, runFrom } = g.tuning;
    expect(g.update(0, true, 0).clip).toBe('Idle');
    // A tenth of a walk: interpolation jitter around a standing body stays idle, a genuine shuffle does not.
    expect(idleBelow).toBeCloseTo(0.25, 10);
    expect(g.update(idleBelow * 0.99, true, 0).clip).toBe('Idle');
    expect(g.update(idleBelow, true, 0).clip).toBe('Walk');
    expect(g.update(runFrom - 0.01, true, 0).clip).toBe('Walk');
    expect(g.update(runFrom, true, 0).clip).toBe('Run');
    expect(g.update(20, true, 0).clip).toBe('Run');
  });

  it('loops locomotion and keeps one serial, so nothing restarts mid-stride', () => {
    const g = graph();
    const a = g.update(3, true, 0);
    const b = g.update(3, true, 1);
    expect(a.loop).toBe(true);
    expect(b.serial).toBe(a.serial);
  });

  it('scales the rate so the feet match the ground speed, within limits', () => {
    const g = graph();
    const tuning = g.tuning;
    const at = (speed: number): number => g.update(speed, true, 0).rate;
    expect(at(tuning.walkClipSpeed)).toBeCloseTo(1, 10);
    expect(at(tuning.runClipSpeed)).toBeCloseTo(1, 10);
    // A speeder gait is far past anything the clips were authored for: capped, not left to strobe.
    expect(at(60)).toBe(tuning.maxRate);
    expect(at(tuning.idleBelow + 1e-9)).toBe(tuning.minRate);
  });

  it('re-tunes when the server publishes its speeds', () => {
    const g = new AnimationGraph(ROLES, CLIPS, locomotionFrom(Number.NaN, Number.NaN, HEIGHT));
    g.tuning = locomotionFrom(WALK, RUN, HEIGHT);
    expect(g.update(3.74, true, 0).clip).toBe('Walk');
    expect(g.update(3.76, true, 0).clip).toBe('Run');
  });
});

describe('one-shots', () => {
  it('beat locomotion while they run, and let it back when they end', () => {
    const g = graph();
    g.fire('attack', 0);
    expect(g.update(5, true, 0.1).clip).toBe('Punch');
    expect(g.locksMovement(0.1)).toBe(true);
    // 0.75 s later the punch is over and the body is running again.
    expect(g.update(5, true, 0.8).clip).toBe('Run');
    expect(g.locksMovement(0.8)).toBe(false);
  });

  it('alternate attacks, and change serial even when the clip repeats', () => {
    const g = graph();
    g.fire('attack', 0);
    const first = { ...g.update(0, true, 0) };
    g.fire('attack', 1);
    const second = { ...g.update(0, true, 1) };
    g.fire('attack', 2);
    const third = { ...g.update(0, true, 2) };
    expect([first.clip, second.clip, third.clip]).toEqual(['Punch', 'SwordSlash', 'Punch']);
    // The serial is what makes the rig restart a clip it is already playing — two punches in a row are two punches.
    expect(third.serial).not.toBe(first.serial);
  });

  it('hands back ONE output object, reused — a caller that keeps it keeps a moving target', () => {
    // `update` runs once per body per frame and a crowd is thousands of bodies, so a fresh literal was a quarter of
    // a million objects a second on the render path. Both callers read the fields and keep nothing, which is what
    // makes this safe; this test is here so that stops being an accident. Copy before you store.
    const g = graph();
    const idle = g.update(0, true, 0);
    expect(idle.role).toBe('idle');
    const walking = g.update(1.2, true, 0.1);
    expect(walking).toBe(idle);
    expect(idle.role).toBe('walk');
  });

  it('hold death until a respawn, and refuse everything else meanwhile', () => {
    const g = graph();
    g.fire('die', 0);
    expect(g.dead).toBe(true);
    expect(g.update(5, true, 100).clip).toBe('Death');
    expect(g.accepts('attack', 100, true)).toBe(false);
    expect(g.accepts('respawn', 100, true)).toBe(true);
    g.fire('respawn', 100);
    expect(g.dead).toBe(false);
    expect(g.update(5, true, 100).clip).toBe('Run');
  });

  it('end a jump on landing rather than on a timer', () => {
    const g = graph();
    g.fire('jump', 0);
    // Still airborne well past the clip's own duration: the last frame holds, which is what a long fall needs.
    expect(g.update(3, false, 5).clip).toBe('Jump');
    expect(g.update(3, true, 5).clip).toBe('Walk');
  });

  it('refuse a jump while airborne and an attack while attacking', () => {
    const g = graph();
    expect(g.accepts('jump', 0, false)).toBe(false);
    g.fire('attack', 0);
    expect(g.accepts('attack', 0.1, true)).toBe(false);
    expect(g.accepts('hit', 0.1, true)).toBe(true);
  });

  it('refuse a rig with no locomotion, where it is mounted rather than in the render loop', () => {
    // `update` runs every frame, so a missing walk used to take the frame loop down on the character's first step.
    expect(() => new AnimationGraph({ idle: 'Idle' }, CLIPS, locomotionFrom(WALK, RUN, HEIGHT))).toThrow(
      'has no "walk" clip',
    );

    // An EVENT role is still resolved lazily: it is asked for only when the event fires, and `accepts` is the gate.
    const noRoll = new AnimationGraph(
      { idle: 'Idle', walk: 'Walk', run: 'Run' },
      CLIPS,
      locomotionFrom(WALK, RUN, HEIGHT),
    );
    expect(noRoll.update(5, true, 0).clip).toBe('Run');
    expect(() => {
      noRoll.fire('roll', 0);
    }).toThrow('no clip for role "roll"');
  });
});
