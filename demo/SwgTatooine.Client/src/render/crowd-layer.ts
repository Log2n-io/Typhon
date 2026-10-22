import { Matrix, Quaternion, Vector3 } from '@babylonjs/core/Maths/math.vector';
import type { Scene } from '@babylonjs/core/scene';

import type { Descriptor } from '../assets/descriptor';
import { AnimationGraph, locomotionFrom, type LocomotionTuning } from '../character/animation-graph';
import type { AtlasPlan } from '../character/clip-atlas';
import { CrowdAnimator } from '../character/crowd-anim';
import { outfitFor } from '../character/outfits';
import type { CrowdSkinPlugin } from './crowd-material';
import type { CrowdOutfit } from './crowd-mesh';
import { CULLED, SPRITE_TIER, classify, packTiers, type ViewParams } from './crowd-view';

/**
 * Every replicated body, drawn as a character.
 *
 * <b>This is the part the crowd spike did not have.</b> It drove a stand-in simulation of wandering agents; the bodies
 * here are the server's, arriving as replicated positions at the tick rate, and the question is no longer "how do I
 * draw five thousand of something" but "how do I draw the five thousand that exist, each as itself, frame after
 * frame, while they come and go".
 *
 * Three things follow from that and none of them were in the spike:
 *
 * - **Identity.** A body is a netId. Which outfit it wears, which animation state it carries and which slot holds that
 *   state all have to follow the netId, not the array position — the packer's arrays are rebuilt as bodies enter and
 *   leave range. A netId is also NOT an identity across a `RESET`, when the server renumbers; the slot's recorded id
 *   is what detects that, exactly as the entity layer's band state does.
 * - **Speed is replicated, in metres per TICK.** Converting it is not a detail: this client has already shipped the
 *   bug where it was treated as metres per second, and the symptom was a sprinting character playing the walk clip at
 *   its slowest rate.
 * - **One decision layer.** The clip comes from {@link AnimationGraph}, the same pure graph the player's avatar uses,
 *   fed replicated motion instead of a motor. The spike bypassed it with a state→role map and lost the rate scaling
 *   that stops feet sliding.
 */

/** What one body looks like to this layer, per frame. */
export interface CrowdBody {
  readonly netId: number;
  /** Render-space metres — the origin is subtracted by the caller, as it is for every other instanced thing. */
  readonly x: number;
  readonly y: number;
  readonly z: number;
  /** Ground speed in metres per SECOND. The caller converts; see the class note. */
  readonly speed: number;
  readonly heading: number;
}

/** Per-body state that must survive a frame. */
interface Slot {
  netId: number;
  outfit: number;
  graph: AnimationGraph;
}

export interface CrowdLayerOptions {
  readonly scene: Scene;
  readonly plan: AtlasPlan;
  readonly outfits: readonly CrowdOutfit[];
  readonly skin: CrowdSkinPlugin;
  /** Descriptors of the outfits, in the same order, for the role maps and the clip tables. */
  readonly described: readonly Descriptor[];
  /** The most bodies this layer will ever draw. Buffers are sized once. */
  readonly capacity: number;
}

export class CrowdLayer {
  private readonly outfits: readonly CrowdOutfit[];
  private readonly plan: AtlasPlan;
  private readonly skin: CrowdSkinPlugin;
  private readonly described: readonly Descriptor[];
  private readonly capacity: number;

  /** Per-outfit, per-body state. A body lives in the bucket of the outfit it wears. */
  private readonly buckets: {
    readonly animator: CrowdAnimator;
    readonly matrices: Float32Array;
    readonly heights: Float32Array;
    readonly tiers: Uint8Array;
    readonly slots: (Slot | null)[];
    readonly byNetId: Map<number, number>;
    readonly counts: Int32Array;
    readonly slotOf: Int8Array;
    readonly targets: Float32Array[][];
    count: number;
    tuning: LocomotionTuning;
  }[];

  /** The gait figures the tunings were built from; see `update`. */
  private tunedWalk = Number.NaN;
  private tunedRun = Number.NaN;

  private readonly scratch = { matrix: new Matrix(), quat: new Quaternion(), pos: new Vector3() };

  /**
   * When this layer started, in the same units the caller's clock uses.
   *
   * <b>The crowd's clock is SECONDS SINCE THIS LAYER BEGAN, never since the epoch, and the layer subtracts it here so
   * that no caller can get it wrong.</b> `crowdTime` reaches the shader as a float32 uniform. Epoch seconds are about
   * 1.79e9, where float32 steps in units of 128 — so `crowdTime - clipStart` is pure quantisation noise, `floor(f)`
   * leaves the texture, `texelFetch` returns zero, every bone matrix is zero and every vertex of every body collapses
   * to a point. Nothing errors: the draw calls, the instance counts, the defines and the matrices all stay perfectly
   * correct, and the crowd is simply invisible.
   */
  private origin = Number.NaN;
  /** Per-tier totals for the HUD, rebuilt each frame. */
  readonly tierTotals = new Int32Array(SPRITE_TIER + 1);
  /** netIds the crowd drew this frame: what the entity packer must skip so a body is not drawn twice. */
  readonly drawn = new Set<number>();
  private drawCalls = 0;
  private trianglesDrawn = 0;

  constructor(options: CrowdLayerOptions) {
    this.outfits = options.outfits;
    this.plan = options.plan;
    this.skin = options.skin;
    this.described = options.described;
    this.capacity = options.capacity;

    this.buckets = this.outfits.map((outfit) => {
      const levels = outfit.lods.length;
      // A tier with no level of its own draws the coarsest the outfit has; the sprite tier draws nothing here.
      const slotOf = Int8Array.from({ length: SPRITE_TIER + 1 }, (_, t) =>
        t >= SPRITE_TIER ? -1 : Math.min(t, levels - 1),
      );
      return {
        animator: new CrowdAnimator(this.capacity, this.plan.fps),
        matrices: new Float32Array(this.capacity * 16),
        heights: new Float32Array(this.capacity),
        tiers: new Uint8Array(this.capacity).fill(CULLED),
        slots: new Array<Slot | null>(this.capacity).fill(null),
        byNetId: new Map<number, number>(),
        counts: new Int32Array(levels),
        slotOf,
        targets: outfit.lods.map(() => [
          new Float32Array(this.capacity * 16),
          new Float32Array(this.capacity * 4),
          new Float32Array(this.capacity * 4),
          new Float32Array(this.capacity * 2),
        ]),
        count: 0,
        tuning: locomotionFrom(Number.NaN, Number.NaN, outfit.height),
      };
    });

    // <b>Bound once, at full capacity, and never re-bound.</b> Two reasons, and the first is a bug this cost an
    // evening: Babylon decides `INSTANCES`/`THIN_INSTANCES` when it compiles the effect, from whether the mesh has
    // thin instances AT THAT MOMENT. Binding the matrix buffer later leaves an effect whose `finalWorld` is the
    // mesh's own — so every body drew stacked at the origin, as one small blob, while the matrices reaching the GPU
    // were perfectly correct. Nothing errors; the draw call count and the instance count both look right.
    //
    // The second is cost: `thinInstanceSetBuffer` builds a VertexBuffer, so calling it per frame per level rebuilds
    // nineteen outfits' worth of them every frame. The contents are written in place and pushed with a PARTIAL
    // update bounded by the live count, which is the spike's own hard-won note — `thinInstanceBufferUpdated` uploads
    // the whole capacity whatever the count.
    for (const [which, bucket] of this.buckets.entries()) {
      for (const [level, lod] of this.outfits[which].lods.entries()) {
        const target = bucket.targets[level];
        lod.mesh.thinInstanceSetBuffer('matrix', target[0], 16, false);
        lod.mesh.thinInstanceSetBuffer('crowdClipA', target[1], 4, false);
        lod.mesh.thinInstanceSetBuffer('crowdClipB', target[2], 4, false);
        lod.mesh.thinInstanceSetBuffer('crowdFade', target[3], 2, false);
        lod.mesh.thinInstanceCount = 0;
        lod.mesh.isVisible = false;
      }
    }
  }

  /** Where the crowd's clock is, in seconds since it started. Not epoch seconds: float32 loses milliseconds there. */
  get time(): number {
    return this.skin.time;
  }

  get stats(): { drawCalls: number; triangles: number } {
    return { drawCalls: this.drawCalls, triangles: this.trianglesDrawn };
  }

  /**
   * Draws one frame.
   *
   * @param bodies Every body in range, in render space.
   * @param view The active camera, for culling and for the projected-size tiers.
   * @param now A monotonic clock in seconds. Its ZERO does not matter: the layer records its own start and works
   * relative to it, because the shader's copy is a float32 and epoch seconds have no resolution left in one. There is
   * no `dt` — every crossfade and clip phase is derived from absolute time on the GPU, which is what makes a body
   * that keeps walking cost nothing per frame.
   * @param walkMps The server's own walk speed, for the gait thresholds. Never a literal in this client.
   * @param runMps The server's own run speed.
   */
  update(
    bodies: readonly CrowdBody[],
    view: ViewParams,
    now: number,
    walkMps: number,
    runMps: number,
  ): void {
    if (!Number.isFinite(this.origin)) {
      this.origin = now;
    }

    const clock = now - this.origin;
    this.skin.time = clock;
    this.drawn.clear();
    this.tierTotals.fill(0);
    for (const bucket of this.buckets) {
      bucket.count = 0;
    }

    // The server's gait figures change about never, and a tuning per outfit per frame is an allocation per outfit per
    // frame for nothing — the avatar path learned the same thing. Rebuilt only when a figure actually moves.
    if (walkMps !== this.tunedWalk || runMps !== this.tunedRun) {
      this.tunedWalk = walkMps;
      this.tunedRun = runMps;
      for (const [which, bucket] of this.buckets.entries()) {
        bucket.tuning = locomotionFrom(walkMps, runMps, this.outfits[which].height);
        for (const slot of bucket.slots) {
          if (slot !== null) {
            slot.graph.tuning = bucket.tuning;
          }
        }
      }
    }

    const outfitCount = this.outfits.length;
    if (outfitCount === 0) {
      return;
    }

    // ---- place every body in its outfit's bucket -----------------------------------------------------------------
    for (const body of bodies) {
      const which = outfitFor(body.netId, outfitCount);
      const bucket = this.buckets[which];
      const outfit = this.outfits[which];
      if (bucket.count >= this.capacity) {
        continue;
      }

      const i = bucket.count++;
      // The slot may be holding another body's state: a netId is not an identity across a RESET, and the arrays are
      // rebuilt as bodies come and go. Re-pin, and forget the stride of whoever was here.
      let slot = bucket.slots[i];
      if (slot === null || slot.netId !== body.netId) {
        const descriptor = this.described[which];
        const roles = descriptor.animations?.roles ?? {};
        const clips = descriptor.animations?.clips ?? [];
        slot = {
          netId: body.netId,
          outfit: which,
          graph: new AnimationGraph(roles, clips, bucket.tuning),
        };
        bucket.slots[i] = slot;
        bucket.animator.clear(i);
        bucket.tiers[i] = CULLED;
      }

      const out = slot.graph.update(body.speed, true, clock);
      const atlas = this.plan.slotOf(outfit.id, out.clip);
      if (atlas !== null) {
        bucket.animator.play(i, atlas, clock, out.rate);
      }

      // Yaw only: a body leans with the ground nowhere in this client, and the matrix is the whole placement because
      // the glTF root flip is baked into the atlas rather than left on the mesh.
      Quaternion.RotationYawPitchRollToRef(body.heading, 0, 0, this.scratch.quat);
      this.scratch.pos.set(body.x, body.y, body.z);
      Matrix.ComposeToRef(ONE, this.scratch.quat, this.scratch.pos, this.scratch.matrix);
      this.scratch.matrix.copyToArray(bucket.matrices, i * 16);
      bucket.heights[i] = outfit.height;
      this.drawn.add(body.netId);
    }

    // ---- classify, pack and upload ---------------------------------------------------------------------------
    this.drawCalls = 0;
    this.trianglesDrawn = 0;
    for (const [which, bucket] of this.buckets.entries()) {
      const outfit = this.outfits[which];
      classify(bucket.matrices, bucket.count, bucket.heights, view, bucket.tiers, this.tierTotals);
      packTiers(
        bucket.tiers,
        bucket.count,
        bucket.slotOf,
        [
          { data: bucket.matrices, stride: 16 },
          { data: bucket.animator.clipA, stride: 4 },
          { data: bucket.animator.clipB, stride: 4 },
          { data: bucket.animator.fade, stride: 2 },
        ],
        bucket.targets,
        bucket.counts,
      );

      for (const [level, lod] of outfit.lods.entries()) {
        const count = bucket.counts[level] ?? 0;
        const mesh = lod.mesh;
        mesh.isVisible = count > 0;
        if (count === 0) {
          mesh.thinInstanceCount = 0;
          continue;
        }

        // The arrays `packTiers` just filled are the ones already bound; only their first `count` entries are live.
        mesh.thinInstanceCount = count;
        mesh.thinInstancePartialBufferUpdate('matrix', count * 16, 0);
        mesh.thinInstancePartialBufferUpdate('crowdClipA', count * 4, 0);
        mesh.thinInstancePartialBufferUpdate('crowdClipB', count * 4, 0);
        mesh.thinInstancePartialBufferUpdate('crowdFade', count * 2, 0);
        this.drawCalls++;
        this.trianglesDrawn += lod.triangles * count;
      }
    }
  }

  /** Hides every crowd mesh — what a profile with no bodies, or a torn-down session, leaves behind. */
  hideAll(): void {
    for (const outfit of this.outfits) {
      for (const lod of outfit.lods) {
        lod.mesh.isVisible = false;
        lod.mesh.thinInstanceCount = 0;
      }
    }

    this.drawn.clear();
  }

  dispose(): void {
    for (const outfit of this.outfits) {
      outfit.dispose();
    }
  }
}

/** Reused: every body is drawn at its published size. */
const ONE = Vector3.One();
