import type { ClipSlot } from './clip-atlas';

/**
 * Which clip each crowd body plays, as the three per-instance buffers the crowd shader reads.
 *
 * - `clipA` (4 floats): the clip being faded IN — start row, frame count (negative: plays once and holds its last
 *   frame), the time its frame 0 played, playback rate;
 * - `clipB` (4 floats): the clip being faded OUT, same layout;
 * - `fade` (2 floats): when the fade started, and 1 / its duration.
 *
 * The GPU derives the frame from these and the global time, so a body that keeps walking needs no CPU work and no
 * write; only a clip SWITCH touches a buffer. (That is a property of this state, not yet of the upload: the spike
 * recompacted and re-uploaded every drawn instance every frame regardless, and said otherwise in its comments. What
 * the layer above does with `takeDirty` is its business.)
 *
 * Pure and engine-free: {@link frameOf} is the shader's formula line for line, so what the tests check is what the GPU
 * draws. Keep them identical — a divergence here is invisible until a foot slides.
 *
 * <b>A locomotion switch keeps the step.</b> Walk → run starts the new clip at the same fraction of its cycle as the
 * old one, so a foot that was down stays down through the crossfade. One-shots start at 0.
 */

/** Below this, a time stands for "long ago": weight 1, nothing to fade from. */
const NEVER = -1e6;

/** Crossfade duration, seconds. The spike's figure; short enough that a gait change reads as one step. */
export const CROSSFADE_S = 0.25;

export class CrowdAnimator {
  readonly count: number;
  clipA: Float32Array;
  clipB: Float32Array;
  fade: Float32Array;
  /** Last frame's rows, which {@link carry} reads. See {@link beginFrame}. */
  private previousA: Float32Array;
  private previousB: Float32Array;
  private previousFade: Float32Array;
  private dirty = true;
  private readonly fps: number;
  private readonly fadeS: number;

  /**
   * @param count How many bodies this animator carries state for.
   * @param fps The rate the atlas was baked at.
   * @param fadeS The crossfade duration.
   */
  constructor(count: number, fps: number, fadeS = CROSSFADE_S) {
    this.count = count;
    this.fps = fps;
    this.fadeS = fadeS;
    this.clipA = new Float32Array(count * 4);
    this.clipB = new Float32Array(count * 4);
    this.fade = new Float32Array(count * 2);
    this.previousA = new Float32Array(count * 4);
    this.previousB = new Float32Array(count * 4);
    this.previousFade = new Float32Array(count * 2);
    for (let i = 0; i < count; i++) {
      // A fresh body has nothing to fade FROM, so its weight is already 1 and the shader skips the second pose —
      // sixteen texel fetches, not thirty-two. Correct by construction rather than by a branch.
      this.fade[i * 2] = NEVER;
      this.fade[i * 2 + 1] = 1 / fadeS;
      this.previousFade[i * 2] = NEVER;
      this.previousFade[i * 2 + 1] = 1 / fadeS;
    }
  }

  /**
   * Starts a frame: this frame's rows become blank and last frame's become readable by {@link carry}.
   *
   * <b>A body's index is its position in a list the packer rebuilds every frame</b> — it pushes in live-slot order,
   * filtered by frustum AND by band, so one body ahead of you leaving the view shifts every body behind it by one.
   * Without this, the row at index `i` belonged to whoever stood there last frame and the clip restarts: a foot
   * popping back to the start of its stride, every time the camera turns. The layer carries each body's own row
   * across by netId; this is what makes "its own row" still exist to carry.
   */
  beginFrame(): void {
    const a = this.previousA;
    const b = this.previousB;
    const f = this.previousFade;
    this.previousA = this.clipA;
    this.previousB = this.clipB;
    this.previousFade = this.fade;
    this.clipA = a;
    this.clipB = b;
    this.fade = f;
  }

  /**
   * Moves a body's clip state from the index it had last frame to the one it has now.
   *
   * @param from Its index last frame.
   * @param to Its index now.
   */
  carry(from: number, to: number): void {
    const f = from * 4;
    const t = to * 4;
    this.clipA.set(this.previousA.subarray(f, f + 4), t);
    this.clipB.set(this.previousB.subarray(f, f + 4), t);
    this.fade[to * 2] = this.previousFade[from * 2] ?? NEVER;
    this.fade[to * 2 + 1] = this.previousFade[from * 2 + 1] ?? 1 / this.fadeS;
    this.dirty = true;
  }

  /**
   * Starts a clip on one body, fading from whatever it played.
   *
   * Playing the clip already playing does nothing, unless it is a one-shot — two punches in a row are two punches.
   *
   * @param i The body's index.
   * @param slot Where the clip lives in the atlas.
   * @param now Seconds since the crowd started; the same clock the shader is given.
   * @param rate Playback rate, which is how the feet are made to match the ground speed.
   * @param phase Where in a looping clip's cycle to begin, 0–1; by default it inherits the outgoing clip's.
   */
  play(i: number, slot: ClipSlot, now: number, rate = 1, phase: number | null = null): void {
    const a = i * 4;
    const count = slot.loop ? slot.frames : -slot.frames;
    const hadClip = this.clipA[a + 1] !== 0;
    const same = hadClip && this.clipA[a] === slot.startRow && this.clipA[a + 1] === count && slot.loop;
    // <b>Compared as a float32, because that is what was stored.</b> The rate arrives as a float64 from
    // `speed / clipSpeed`; the buffer holds it rounded. Comparing the two is `1.0714285714285714 !== 1.0714285373`,
    // which is never equal — so the early-out never fires, every walking body is re-timed and its buffer marked
    // dirty on every frame, for a rate that has not moved. `Math.fround` is the question actually being asked.
    if (same && this.clipA[a + 3] === Math.fround(rate)) {
      return;
    }

    let start = phase;
    if (start === null) {
      start = hadClip && slot.loop && (this.clipA[a + 1] ?? 0) > 0 ? this.cycleOf(i, now) : 0;
    }

    // <b>A rate change on the clip already playing is a re-timing, not a transition.</b> Locomotion scales the clip
    // by ground speed, so a body accelerating from 1.0 to 2.5 m/s stays in the `walk` role the whole way and only its
    // rate moves. Returning early on "same clip" kept the rate it had on its first walking frame and slid the feet —
    // the exact defect this design blames the POC for. Crossfading a clip with itself would instead blend two copies
    // of one pose a quarter-second apart, which is a stutter; so the phase is preserved and nothing is faded.
    if (same) {
      this.clipA[a + 2] = now - (start * slot.frames) / (this.fps * rate);
      this.clipA[a + 3] = rate;
      this.dirty = true;
      return;
    }

    if (hadClip) {
      this.clipB.set(this.clipA.subarray(a, a + 4), a);
      this.fade[i * 2] = now;
    } else {
      this.fade[i * 2] = NEVER;
    }

    this.clipA[a] = slot.startRow;
    this.clipA[a + 1] = count;
    this.clipA[a + 2] = now - (start * slot.frames) / (this.fps * rate);
    this.clipA[a + 3] = rate;
    if (!hadClip) {
      this.clipB.set(this.clipA.subarray(a, a + 4), a);
    }

    this.dirty = true;
  }

  /** Where a body's current clip is in its cycle at `now`, 0–1. */
  cycleOf(i: number, now: number): number {
    const a = i * 4;
    const n = Math.abs(this.clipA[a + 1] ?? 0);
    if (n === 0) {
      return 0;
    }

    const f = Math.max(0, (now - (this.clipA[a + 2] ?? 0)) * this.fps * (this.clipA[a + 3] ?? 1));
    return (this.clipA[a + 1] ?? 0) > 0 ? (f % n) / n : Math.min(f, n - 1) / n;
  }

  /** The weight of `clipA` for a body at `now` — the shader's `crowdW`. */
  weightOf(i: number, now: number): number {
    return Math.min(1, Math.max(0, (now - (this.fade[i * 2] ?? NEVER)) * (this.fade[i * 2 + 1] ?? 1)));
  }

  /** Forgets a body's clip, so the slot can be reused by another without inheriting its stride. */
  clear(i: number): void {
    const a = i * 4;
    this.clipA.fill(0, a, a + 4);
    this.clipB.fill(0, a, a + 4);
    this.fade[i * 2] = NEVER;
    this.fade[i * 2 + 1] = 1 / this.fadeS;
    this.dirty = true;
  }

  /** True once after any change. */
  takeDirty(): boolean {
    const d = this.dirty;
    this.dirty = false;
    return d;
  }
}

/**
 * The atlas row a clip slot shows at a time — <b>the shader's `crowdFrame`, and it must stay identical.</b>
 *
 * GLSL's `mod` and JavaScript's `%` disagree for negative operands; the `Math.max(0, …)` above excludes them, which is
 * what makes the two the same function rather than nearly the same.
 *
 * @param slot The per-instance buffer.
 * @param offset Where this body's four floats start.
 * @param time Seconds, the same clock the shader is given.
 * @param fps The bake rate.
 * @returns The texture row.
 */
export function frameOf(slot: ArrayLike<number>, offset: number, time: number, fps: number): number {
  const start = slot[offset] ?? 0;
  const count = slot[offset + 1] ?? 0;
  const n = Math.abs(count);
  let f = Math.max(0, (time - (slot[offset + 2] ?? 0)) * fps * (slot[offset + 3] ?? 1));
  f = count > 0 ? f % n : Math.min(f, n - 1);
  return start + Math.floor(f);
}
