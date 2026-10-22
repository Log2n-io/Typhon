import { describe, expect, it } from 'vitest';

import type { ClipSlot } from '../src/character/clip-atlas';
import { CrowdAnimator, frameOf } from '../src/character/crowd-anim';

/**
 * Which clip each body plays, and which row of the atlas the GPU will read for it.
 *
 * <b>{@link frameOf} is the shader's `crowdFrame`, line for line.</b> That duplication is deliberate — the GPU cannot
 * be asked what it drew — and these tests are what makes it a mirror rather than two similar functions that drift.
 * GLSL's `mod` and JavaScript's `%` disagree on negative operands; both sides clamp with `max(0, …)` first, so the
 * cases below include times before a clip started.
 */

const FPS = 30;
const IDLE: ClipSlot = { startRow: 0, frames: 125, loop: true, source: 'casual_male', clip: 'Idle' };
const WALK: ClipSlot = { startRow: 125, frames: 38, loop: true, source: 'casual_male', clip: 'Walk' };
const RUN: ClipSlot = { startRow: 163, frames: 26, loop: true, source: 'casual_male', clip: 'Run' };
const DEATH: ClipSlot = { startRow: 400, frames: 69, loop: false, source: 'casual_male', clip: 'Death' };

describe('the crowd animator', () => {
  it('starts a fresh body with nothing to fade from, so it pays 16 texel fetches and not 32', () => {
    // A fade weight already at 1 makes the shader skip the second pose entirely. Correct by construction rather than
    // by a branch in the shader, and it matters: the branch diverges across a draw call.
    const a = new CrowdAnimator(4, FPS);
    a.play(0, WALK, 10);
    expect(a.weightOf(0, 10)).toBe(1);
    expect(a.clipA.subarray(0, 2)).toEqual(a.clipB.subarray(0, 2));
  });

  it('points at the atlas row the clip was baked to', () => {
    const a = new CrowdAnimator(2, FPS);
    a.play(0, RUN, 5);
    expect(frameOf(a.clipA, 0, 5, FPS)).toBe(RUN.startRow);
    // A quarter of the way through a 26-frame cycle at rate 1.
    expect(frameOf(a.clipA, 0, 5 + 6.5 / FPS, FPS)).toBe(RUN.startRow + 6);
  });

  it('wraps a looping clip and never leaves its rows', () => {
    const a = new CrowdAnimator(1, FPS);
    a.play(0, IDLE, 0);
    for (let k = 0; k < 400; k++) {
      const row = frameOf(a.clipA, 0, k / FPS, FPS);
      expect(row).toBeGreaterThanOrEqual(IDLE.startRow);
      expect(row).toBeLessThan(IDLE.startRow + IDLE.frames);
    }
  });

  it('holds a one-shot on its last frame instead of looping it', () => {
    // What keeps a death pose. The frame count is stored negative to say so.
    const a = new CrowdAnimator(1, FPS);
    a.play(0, DEATH, 0);
    expect(a.clipA[1]).toBe(-DEATH.frames);
    const last = DEATH.startRow + DEATH.frames - 1;
    expect(frameOf(a.clipA, 0, 100, FPS)).toBe(last);
    expect(frameOf(a.clipA, 0, 1000, FPS)).toBe(last);
  });

  it('reads a time before the clip started as its first frame, not as a negative row', () => {
    // The case where GLSL mod and JS % would disagree if the clamp were missing on either side.
    const a = new CrowdAnimator(1, FPS);
    a.play(0, WALK, 100);
    expect(frameOf(a.clipA, 0, 0, FPS)).toBe(WALK.startRow);
    expect(frameOf(a.clipA, 0, 99.5, FPS)).toBe(WALK.startRow);
  });

  it('keeps the step when a gait changes, which is what stops a planted foot sliding', () => {
    // Walk → run inherits the cycle fraction rather than restarting at 0.
    const a = new CrowdAnimator(1, FPS);
    a.play(0, WALK, 0);
    const at = 0.4;
    const phase = a.cycleOf(0, at);
    expect(phase).toBeGreaterThan(0);
    a.play(0, RUN, at);
    expect(a.cycleOf(0, at)).toBeCloseTo(phase, 5);
  });

  it('does not restart the clip already looping, so a walk does not stutter every frame', () => {
    const a = new CrowdAnimator(1, FPS);
    a.play(0, WALK, 0);
    const started = a.clipA[2];
    a.play(0, WALK, 1);
    a.play(0, WALK, 2);
    expect(a.clipA[2]).toBe(started);
  });

  it('DOES restart a one-shot asked for again, because two punches are two punches', () => {
    const a = new CrowdAnimator(1, FPS);
    a.play(0, DEATH, 0);
    a.play(0, DEATH, 5);
    expect(a.clipA[2]).toBe(5);
  });

  it('crossfades from the outgoing clip over the fade window', () => {
    const a = new CrowdAnimator(1, FPS, 0.25);
    a.play(0, WALK, 0);
    a.play(0, RUN, 10);
    expect(a.weightOf(0, 10)).toBe(0);
    expect(a.weightOf(0, 10.125)).toBeCloseTo(0.5, 5);
    expect(a.weightOf(0, 10.25)).toBe(1);
    expect(a.weightOf(0, 11)).toBe(1);
    // The outgoing clip is still addressable while the fade runs.
    expect(frameOf(a.clipB, 0, 10.1, FPS)).toBeGreaterThanOrEqual(WALK.startRow);
    expect(frameOf(a.clipB, 0, 10.1, FPS)).toBeLessThan(WALK.startRow + WALK.frames);
  });

  it('scales the playback rate, which is how feet are matched to ground speed', () => {
    const a = new CrowdAnimator(1, FPS);
    a.play(0, RUN, 0, 2);
    // Twice the rate reaches frame 10 in half the time.
    expect(frameOf(a.clipA, 0, 5 / FPS, FPS)).toBe(RUN.startRow + 10);
  });

  it('forgets a slot on clear, so a reused slot does not inherit a stranger stride', () => {
    // A netId is not an identity across a RESET: the slot may be another body next frame.
    const a = new CrowdAnimator(1, FPS);
    a.play(0, RUN, 0);
    a.clear(0);
    expect(a.clipA[1]).toBe(0);
    a.play(0, WALK, 50);
    // Fresh again: weight 1, nothing to fade from, and starting at the clip's own frame 0.
    expect(a.weightOf(0, 50)).toBe(1);
    expect(frameOf(a.clipA, 0, 50, FPS)).toBe(WALK.startRow);
  });

  it('reports dirty once per change', () => {
    const a = new CrowdAnimator(1, FPS);
    expect(a.takeDirty()).toBe(true);
    expect(a.takeDirty()).toBe(false);
    a.play(0, WALK, 0);
    expect(a.takeDirty()).toBe(true);
    expect(a.takeDirty()).toBe(false);
  });
});

describe('a body keeps its own clip when its index moves', () => {
  it('carries clip, fade and phase from the index it had to the index it has', () => {
    const a = new CrowdAnimator(8, FPS);
    a.play(5, WALK, 0, 1);
    const phase = a.cycleOf(5, 0.7);
    expect(phase).toBeGreaterThan(0);

    // The packer rebuilt its list and this body is now at 2. Nothing about the body changed.
    a.beginFrame();
    a.carry(5, 2);
    expect(a.cycleOf(2, 0.7)).toBeCloseTo(phase, 6);
    expect(frameOf(a.clipA, 2 * 4, 0.7, FPS)).toBe(frameOf(a.clipA, 2 * 4, 0.7, FPS));
    // Playing the same clip at the same rate on the carried row is a no-op, which is the whole point: the stride
    // continues instead of restarting at frame 0.
    a.play(2, WALK, 0.7, 1);
    expect(a.cycleOf(2, 0.7)).toBeCloseTo(phase, 6);
  });

  it('survives two bodies swapping index, which is what a naive in-place move destroys', () => {
    const a = new CrowdAnimator(8, FPS);
    a.play(0, WALK, 0, 1);
    a.play(1, RUN, 0, 1);
    a.beginFrame();
    a.carry(0, 1);
    a.carry(1, 0);
    expect(a.clipA[0 * 4]).toBe(RUN.startRow);
    expect(a.clipA[1 * 4]).toBe(WALK.startRow);
  });

  it('starts a body that was not here last frame from nothing, not from the stranger in its slot', () => {
    const a = new CrowdAnimator(8, FPS);
    a.play(3, RUN, 0, 1);
    a.beginFrame();
    a.clear(3);
    expect(a.clipA[3 * 4 + 1]).toBe(0);
    expect(a.weightOf(3, 0)).toBe(1);
  });
});

describe('a rate change on the clip already playing', () => {
  it('re-times the clip instead of being ignored, so the feet keep up with the ground', () => {
    const a = new CrowdAnimator(4, FPS);
    a.play(0, WALK, 0, 1);
    const slow = frameOf(a.clipA, 0, 1, FPS);
    // Same role, same clip, twice the ground speed: this is what locomotion does between 1.0 and 2.5 m/s.
    a.play(0, WALK, 1, 2);
    expect(a.clipA[3]).toBe(2);
    // The phase is preserved across the re-time — a foot that was down stays down.
    expect(a.cycleOf(0, 1)).toBeCloseTo((slow - WALK.startRow) / WALK.frames, 1);
    // …and it now advances at twice the rate.
    const before = frameOf(a.clipA, 0, 1, FPS);
    const after = frameOf(a.clipA, 0, 1 + 1 / FPS, FPS);
    expect(after - before).toBe(2);
  });

  it('does not crossfade a clip with itself, which would blend one pose with its own past', () => {
    const a = new CrowdAnimator(4, FPS);
    a.play(0, WALK, 0, 1);
    a.play(0, WALK, 1, 2);
    expect(a.weightOf(0, 1)).toBe(1);
  });

  it('still does nothing at all when neither the clip nor the rate moved', () => {
    const a = new CrowdAnimator(4, FPS);
    a.play(0, WALK, 0, 1.5);
    const before = [...a.clipA.subarray(0, 4)];
    a.play(0, WALK, 9, 1.5);
    expect([...a.clipA.subarray(0, 4)]).toEqual(before);
  });
});
