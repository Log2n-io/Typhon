import { Matrix, Quaternion, Vector3 } from '@babylonjs/core/Maths/math.vector';
import type { Scene } from '@babylonjs/core/scene';

import type { Descriptor } from '../assets/descriptor';
import { AnimationGraph, locomotionFrom, type LocomotionTuning } from '../character/animation-graph';
import type { AtlasPlan } from '../character/clip-atlas';
import { CrowdAnimator } from '../character/crowd-anim';
import { RIDE_ROLE, outfitFor } from '../character/outfits';
import type { CrowdSkinPlugin } from './crowd-material';
import type { CrowdOutfit } from './crowd-mesh';
import type { CrowdBody } from './layer-packer';
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

// <b>One declaration of the hand-over, in the file that writes it.</b> This had its own structurally identical copy,
// which meant two sources of truth for the one contract between the packer and the crowd — and the note that the
// speed is metres per SECOND while the store carries metres per TICK lived on only one of them. Re-exported so this
// file's readers still find it where they expect it.
export type { CrowdBody } from './layer-packer';

/** Per-body state that must survive a frame. */
interface Slot {
  netId: number;
  outfit: number;
  graph: AnimationGraph;
  /** The gait figures this graph was tuned with; see the retune note in `update`. */
  tunedAt: number;
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

/** A body drawn seated this frame, in render space: where its swoop goes. */
export interface CrowdRider {
  netId: number;
  x: number;
  y: number;
  z: number;
  heading: number;
}

export class CrowdLayer {
  /**
   * Where a rider's root sits above its swoop's origin, metres: the swoop descriptor's `seats[0].pos[1]` once it is read
   * (`character-mount.ts` uses the same figure for the avatar).
   */
  seatY = 0.62;

  /** The bodies drawn seated this frame, pooled: the first {@link riderCount} are live. */
  readonly riders: CrowdRider[] = [];

  /** How many of {@link riders} this frame filled. */
  riderCount = 0;

  private readonly outfits: readonly CrowdOutfit[];
  private readonly plan: AtlasPlan;
  private readonly skin: CrowdSkinPlugin;
  private readonly described: readonly Descriptor[];
  private readonly capacity: number;
  /** How many bodies ONE outfit may draw; see {@link bucketCapacity}. */
  private readonly bucketCapacity: number;
  /** Whether a bucket has ever been full, so the warning is said once rather than per frame. */
  private overflowed = false;

  /**
   * Per-outfit, per-body state. A body lives in the bucket of the outfit it wears.
   *
   * <b>Readable, deliberately.</b> Everything interesting about this layer is what survives between two `update`
   * calls — a body's clip across an index change, its tier's hysteresis, whether the tuning was rebuilt — and none
   * of it can be asserted from the outside without this. The same handle is what a live session reads through
   * `window.__typhon` to answer "what is the crowd actually holding", which is how both of this file's silent
   * failures were finally seen. Nothing outside writes to it.
   */
  readonly buckets: {
    readonly animator: CrowdAnimator;
    readonly matrices: Float32Array;
    tiers: Uint8Array;
    /** Last frame's tiers, which a carried body takes its hysteresis from. Swapped with `tiers` each frame. */
    previousTiers: Uint8Array;
    slots: (Slot | null)[];
    /**
     * Last frame's slots, read by a body that moved index.
     *
     * <b>Ping-ponged, not written in place.</b> Two bodies that swap index would otherwise destroy each other: the
     * first writes its slot over the index the second has not read from yet, and the second is handed its neighbour's
     * animation graph. Reading one array and writing another cannot do that whatever the permutation.
     */
    previousSlots: (Slot | null)[];
    /** Where each netId sat LAST frame. Rebuilt as this frame is placed; see the identity note on the class. */
    byNetId: Map<number, number>;
    previousByNetId: Map<number, number>;
    readonly counts: Int32Array;
    readonly slotOf: Int8Array;
    readonly targets: Float32Array[][];
    count: number;
    tuning: LocomotionTuning;
  }[];

  /** The gait figures the tunings were built from; see `update`. */
  private tunedWalk = Number.NaN;
  private tunedRun = Number.NaN;
  /**
   * Bumped whenever the tunings are rebuilt, so a live body adopts the new one and a dormant one is not walked.
   *
   * <b>Pushed to the bodies that exist rather than to the capacity.</b> Sweeping `slots` from here also read the
   * wrong array: the per-frame ping-pong means the live slots are in `previousSlots` at the point this runs.
   */
  private tuningSerial = 0;

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

  /**
   * Whether the crowd is drawing bodies, which is what lets the entity layers stop drawing boxes for them.
   *
   * <b>It is not "the crowd mounted".</b> A crowd that builds, classifies and issues draw calls while putting no
   * pixels on screen is exactly the state this spent an evening in, and every counter looked healthy throughout — the
   * outfits, the atlas rows, the tier totals, the draw calls and the triangle count were all correct and the street
   * was empty. The layer cannot answer this itself: it knows what it submitted, not what came out.
   */
  isDrawing = false;

  constructor(options: CrowdLayerOptions) {
    this.outfits = options.outfits;
    this.plan = options.plan;
    this.skin = options.skin;
    this.described = options.described;
    this.capacity = options.capacity;

    const share = bucketCapacity(this.capacity, this.outfits.length);
    this.bucketCapacity = share;
    this.buckets = this.outfits.map((outfit) => {
      const levels = outfit.lods.length;
      // A tier with no level of its own draws the coarsest the outfit has; the sprite tier draws nothing here.
      const slotOf = Int8Array.from({ length: SPRITE_TIER + 1 }, (_, t) =>
        t >= SPRITE_TIER ? -1 : Math.min(t, levels - 1),
      );
      return {
        animator: new CrowdAnimator(share, this.plan.fps),
        matrices: new Float32Array(share * 16),
        tiers: new Uint8Array(share).fill(CULLED),
        previousTiers: new Uint8Array(share).fill(CULLED),
        slots: new Array<Slot | null>(share).fill(null),
        previousSlots: new Array<Slot | null>(share).fill(null),
        byNetId: new Map<number, number>(),
        previousByNetId: new Map<number, number>(),
        counts: new Int32Array(levels),
        slotOf,
        targets: outfit.lods.map(() => [
          new Float32Array(share * 16),
          new Float32Array(share * 4),
          new Float32Array(share * 4),
          new Float32Array(share * 2),
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
   * @param bodies The pooled body records; only the first `count` are this frame's.
   * @param count How many of `bodies` are live. The array keeps its capacity across frames, so its length is not it.
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
    count: number,
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
    this.riderCount = 0;
    this.tierTotals.fill(0);
    for (const bucket of this.buckets) {
      bucket.count = 0;
      // This frame's rows start blank and last frame's become readable, so a body that moved index can take its own
      // clip, fade and tier with it. See `CrowdAnimator.beginFrame`.
      bucket.animator.beginFrame();
      const tiers = bucket.previousTiers;
      bucket.previousTiers = bucket.tiers;
      bucket.tiers = tiers;
      const byNetId = bucket.previousByNetId;
      bucket.previousByNetId = bucket.byNetId;
      bucket.byNetId = byNetId;
      bucket.byNetId.clear();
      const slots = bucket.previousSlots;
      bucket.previousSlots = bucket.slots;
      bucket.slots = slots;
    }

    // The server's gait figures change about never, and a tuning per outfit per frame is an allocation per outfit per
    // frame for nothing — the avatar path learned the same thing. Rebuilt only when a figure actually moves.
    //
    // <b>`Object.is`, because both figures are NaN until the owner block lands and `NaN !== NaN`.</b> With `!==` this
    // test passed on EVERY frame of the first seconds of every session: eighteen `locomotionFrom` allocations and a
    // walk of eighteen times the whole capacity, inside the render loop, to rebuild tunings identical to the ones
    // already there. The guard was written to prevent exactly that and inverted itself on the one value it had to
    // handle.
    if (!Object.is(walkMps, this.tunedWalk) || !Object.is(runMps, this.tunedRun)) {
      this.tunedWalk = walkMps;
      this.tunedRun = runMps;
      this.tuningSerial++;
      for (const [which, bucket] of this.buckets.entries()) {
        bucket.tuning = locomotionFrom(walkMps, runMps, this.outfits[which].height);
      }
    }

    const outfitCount = this.outfits.length;
    if (outfitCount === 0) {
      return;
    }

    // ---- place every body in its outfit's bucket -----------------------------------------------------------------
    for (let b = 0; b < count; b++) {
      const body = bodies[b];
      const which = outfitFor(body.netId, outfitCount);
      const bucket = this.buckets[which];
      const outfit = this.outfits[which];
      if (bucket.count >= this.bucketCapacity) {
        // A costume worn by far more than its share. Saying so once beats a body silently not being drawn: the
        // headroom below is four times the expected share, so reaching it means the hash is not spreading, not that
        // the crowd is busy.
        if (!this.overflowed) {
          this.overflowed = true;
          console.warn(
            `crowd: outfit ${outfit.id} exceeded its ${String(this.bucketCapacity)} bodies; some are not drawn`,
          );
        }

        continue;
      }

      const i = bucket.count++;
      bucket.byNetId.set(body.netId, i);

      // <b>A body is its netId, never its index.</b> The index is a position in a list the packer rebuilds every
      // frame — it pushes in live-slot order, filtered by frustum and by band — so one body ahead leaving the view
      // shifts everyone behind it by one. Treating the index as the identity restarted those bodies' clips, lost
      // their crossfades and their tier hysteresis, and allocated an `AnimationGraph` per shifted body per frame.
      // Measured as a foot popping back to the start of its stride whenever the camera turned.
      const was = bucket.previousByNetId.get(body.netId);
      let slot: Slot | null = null;
      if (was !== undefined) {
        slot = bucket.previousSlots[was];
        if (slot !== null) {
          bucket.animator.carry(was, i);
          bucket.tiers[i] = bucket.previousTiers[was] ?? CULLED;
        }
      }

      if (slot === null) {
        const descriptor = this.described[which];
        const roles = descriptor.animations?.roles ?? {};
        const clips = descriptor.animations?.clips ?? [];
        slot = {
          netId: body.netId,
          outfit: which,
          graph: new AnimationGraph(roles, clips, bucket.tuning),
          tunedAt: this.tuningSerial,
        };
        bucket.animator.clear(i);
        bucket.tiers[i] = CULLED;
      } else if (slot.tunedAt !== this.tuningSerial) {
        slot.graph.tuning = bucket.tuning;
        slot.tunedAt = this.tuningSerial;
      }

      bucket.slots[i] = slot;

      // A rider sits: the seated clip held at its end, once — a one-shot played again every frame would restart — and the
      // body raised onto the seat of the swoop drawn under it (`riders`). Locomotion is not consulted while it rides.
      let lift = 0;
      const ride = body.mounted ? this.plan.slotOf(outfit.id, RIDE_ROLE) : null;
      if (ride !== null) {
        if (!bucket.animator.isPlaying(i, ride)) {
          bucket.animator.play(i, ride, clock);
        }

        lift = this.seatY;
        const r = this.riderCount++;
        if (r >= this.riders.length) {
          this.riders.push({ netId: 0, x: 0, y: 0, z: 0, heading: 0 });
        }

        const rider = this.riders[r];
        rider.netId = body.netId;
        rider.x = body.x;
        rider.y = body.y;
        rider.z = body.z;
        rider.heading = body.heading;
      } else {
        const out = slot.graph.update(body.speed, true, clock);
        // <b>By ROLE, not by clip name.</b> See `AnimOutput.role`: the atlas is keyed on the role because an
        // outfit may carry its own copy of one, and a clip name finds nothing without saying so.
        const atlas = this.plan.slotOf(outfit.id, out.role);
        if (atlas !== null) {
          bucket.animator.play(i, atlas, clock, out.rate);
        }
      }

      // Yaw only: a body leans with the ground nowhere in this client, and the matrix is the whole placement because
      // the glTF root flip is baked into the atlas rather than left on the mesh.
      Quaternion.RotationYawPitchRollToRef(body.heading, 0, 0, this.scratch.quat);
      this.scratch.pos.set(body.x, body.y + lift, body.z);
      Matrix.ComposeToRef(ONE, this.scratch.quat, this.scratch.pos, this.scratch.matrix);
      this.scratch.matrix.copyToArray(bucket.matrices, i * 16);
      this.drawn.add(body.netId);
    }

    // ---- classify, pack and upload ---------------------------------------------------------------------------
    this.drawCalls = 0;
    this.trianglesDrawn = 0;
    for (const [which, bucket] of this.buckets.entries()) {
      const outfit = this.outfits[which];
      // One height for the whole bucket: every body in it wears the same outfit, so an array of 2 048 copies of one
      // constant was 2 048 copies of one constant. `classify` has taken a scalar since it was written.
      classify(bucket.matrices, bucket.count, outfit.height, view, bucket.tiers, this.tierTotals);
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
        // <b>The argument is a COUNT OF INSTANCES, not a count of floats.</b> Babylon multiplies it by the buffer's
        // own stride to build the view it uploads (`thinInstanceMesh.pure.js`), so passing `count * stride` asks for
        // `count * stride * stride` floats and throws `RangeError: Invalid typed array length` — inside the render
        // loop, once per mesh per frame, which is why nothing was ever drawn and no single frame reported an error.
        mesh.thinInstancePartialBufferUpdate('matrix', count, 0);
        mesh.thinInstancePartialBufferUpdate('crowdClipA', count, 0);
        mesh.thinInstancePartialBufferUpdate('crowdClipB', count, 0);
        mesh.thinInstancePartialBufferUpdate('crowdFade', count, 0);
        this.drawCalls++;
        this.trianglesDrawn += lod.triangles * count;
      }
    }
  }

  /**
   * Records that the crowd has been SEEN to draw, which is the only thing that retires the boxes.
   *
   * @param drawing Whether bodies are reaching the screen.
   */
  confirmDrawing(drawing: boolean): void {
    this.isDrawing = drawing;
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

/**
 * How many bodies one outfit's bucket holds.
 *
 * <b>A share of the crowd, not the whole of it.</b> Every bucket at full capacity sized the client for 2 048 bodies
 * IN EVERY COSTUME at once, which cannot happen: the cap is 2 048 bodies in total. At nineteen outfits that was about
 * 16 MB of CPU arrays and 12 MB of GPU vertex buffers to hold at most 2 048 bodies' worth of data.
 *
 * The headroom is four times the expected share. {@link outfitFor} is a hash, so the counts are multinomial: 2 048
 * bodies over nineteen buckets expects 108 each with a standard deviation of about 10, and four times the mean is
 * more than thirty deviations out. The floor keeps a tiny crowd from having buckets of one or two.
 *
 * @param capacity The most bodies the crowd draws at once, across every outfit.
 * @param outfits How many outfits share them.
 * @returns The per-outfit capacity.
 */
export function bucketCapacity(capacity: number, outfits: number): number {
  if (outfits <= 1) {
    return capacity;
  }

  return Math.min(capacity, Math.max(64, Math.ceil((capacity / outfits) * 4)));
}
