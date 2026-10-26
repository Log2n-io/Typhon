import { Matrix, Quaternion, Vector3 } from '@babylonjs/core/Maths/math.vector';
import type { Scene } from '@babylonjs/core/scene';

import type { Descriptor } from '../assets/descriptor';
import { AnimationGraph, locomotionFrom, type LocomotionTuning } from '../character/animation-graph';
import type { AtlasPlan } from '../character/clip-atlas';
import { CrowdAnimator } from '../character/crowd-anim';
import { GET_UP_CLIP, RIDE_ROLE, clipRole, locomotionRoles, outfitFor } from '../character/outfits';
import type { CrowdSkinPlugin } from './crowd-material';
import type { CrowdOutfit } from './crowd-mesh';
import type { CrowdWeapon } from './crowd-weapons';
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
  /** The body's strike count this slot has answered; a body that strikes again plays its attack. See `CrowdBody.strikes`. */
  strikes: number;
  /** The blows taken this slot has answered; see `CrowdBody.hits`. */
  hits: number;
  /** The `down` this slot last played: a change is a fall, or getting up. */
  down: number;
  /**
   * The one-shot serial last handed to the animator, so a held attack or death is played once and not restarted every
   * frame. `CrowdAnimator.play` restarts a one-shot on every call — two punches in a row are two punches — which is right
   * for a new blow and wrong for the same one asked for again.
   */
  played: number;
  /** Whether the outfit has the one-shots at all. Every published one does; a body that had none would only walk. */
  acts: boolean;
}

export interface CrowdLayerOptions {
  readonly scene: Scene;
  readonly plan: AtlasPlan;
  /** Every outfit's catalogue id, in the layer's order: what a body is dressed in, whether or not it has loaded yet. */
  readonly ids: readonly string[];
  /**
   * The outfits already built, in the same order; `null` for one still loading, which {@link CrowdLayer.addOutfit} supplies
   * when it lands. Omitted: every one is still loading.
   */
  readonly outfits?: readonly (CrowdOutfit | null)[];
  readonly skin: CrowdSkinPlugin;
  /** Descriptors of the outfits, in the same order, for the role maps and the clip tables. */
  readonly described: readonly Descriptor[];
  /** The most bodies this layer will ever draw. Buffers are sized once. */
  readonly capacity: number;
  /**
   * The outfits a body may be dressed in by its netId, as indices into {@link outfits}; every one when omitted.
   *
   * <b>The civilians' wardrobe, and the enemies' is kept out of it.</b> A body the router dresses explicitly
   * (`CrowdBody.outfit`) may wear any outfit; one it does not is a city NPC or a player and draws from this list, so a
   * ninja's costume is never seen walking the market.
   */
  readonly hashed?: readonly number[];
}

/** One outfit's per-body state; see {@link CrowdLayer.buckets}. */
interface Bucket {
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
  /** The stance each body holds a weapon in this frame, 0 for none: which weapon draws one in its hand. */
  readonly held: Uint8Array;
  readonly targets: Float32Array[][];
  count: number;
  tuning: LocomotionTuning;
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

  /** Each outfit once built, `null` while it loads (Loïc, 2026-10-26: nothing that waits on an asset is drawn). */
  private readonly outfits: (CrowdOutfit | null)[];
  /** Every outfit's id, built or not: what a body wears is decided by these, so it does not change as outfits land. */
  private readonly ids: readonly string[];
  /** Outfits that will never build: a body dressed in one wears a civilian's instead, as the router does for a look. */
  private readonly failed: boolean[];
  private readonly plan: AtlasPlan;
  private readonly skin: CrowdSkinPlugin;
  private readonly described: readonly Descriptor[];
  private readonly capacity: number;
  /** See {@link CrowdLayerOptions.hashed}. */
  private readonly hashed: readonly number[];
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
  readonly buckets: (Bucket | null)[];

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

  /** The weapons drawn in a body's hand, by the server's stance number; see {@link setWeapon}. */
  private readonly weapons: (CrowdWeapon | null)[] = [];

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

  /**
   * The outfit a body this layer dresses by netId wears, by asset id: what the rigged avatar must wear to look like the
   * body the crowd would have drawn in its place. `null` when the layer has no outfit to choose from.
   *
   * @param netId The body's replicated id.
   */
  civilianOutfitOf(netId: number): string | null {
    return this.hashed.length === 0 ? null : this.ids[this.hashed[outfitFor(netId, this.hashed.length)]];
  }

  /** How many outfits have landed and are drawing. */
  get ready(): number {
    let n = 0;
    for (const outfit of this.outfits) {
      n += outfit === null ? 0 : 1;
    }

    return n;
  }

  constructor(options: CrowdLayerOptions) {
    this.ids = options.ids;
    this.outfits = this.ids.map((_, i) => options.outfits?.[i] ?? null);
    this.failed = this.ids.map(() => false);
    this.plan = options.plan;
    this.skin = options.skin;
    this.described = options.described;
    this.capacity = options.capacity;
    this.hashed = options.hashed ?? this.ids.map((_, i) => i);

    // Shared by every outfit the layer will EVER hold, not the ones built so far: a share that grew as outfits landed
    // would resize buffers already bound to meshes (see `makeBucket`).
    this.bucketCapacity = bucketCapacity(this.capacity, this.ids.length);
    this.buckets = this.outfits.map((outfit) => (outfit === null ? null : this.makeBucket(outfit)));
  }

  /**
   * An outfit has loaded: bodies dressed in it are drawn from the next frame. Until then they were drawn by nobody — not a
   * box, not another outfit — because a body that changes clothes when its own arrives is worse than one that appears.
   */
  addOutfit(which: number, outfit: CrowdOutfit): void {
    if (which < 0 || which >= this.ids.length || this.outfits[which] !== null) {
      outfit.dispose();
      return;
    }

    this.outfits[which] = outfit;
    this.buckets[which] = this.makeBucket(outfit);
  }

  /** An outfit will never load: a body dressed in it wears a civilian's outfit instead of being drawn by nobody for good. */
  failOutfit(which: number): void {
    if (which >= 0 && which < this.failed.length) {
      this.failed[which] = true;
    }
  }

  /** One outfit's state, its buffers bound to its meshes. */
  private makeBucket(outfit: CrowdOutfit): Bucket {
    const share = this.bucketCapacity;
    const levels = outfit.lods.length;
    // A tier with no level of its own draws the coarsest the outfit has; the sprite tier draws nothing here.
    const slotOf = Int8Array.from({ length: SPRITE_TIER + 1 }, (_, t) =>
      t >= SPRITE_TIER ? -1 : Math.min(t, levels - 1),
    );
    const bucket: Bucket = {
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
      held: new Uint8Array(share),
      targets: outfit.lods.map(() => [
        new Float32Array(share * 16),
        new Float32Array(share * 4),
        new Float32Array(share * 4),
        new Float32Array(share * 2),
      ]),
      count: 0,
      // The gait figures already known, if any: a bucket made after they landed would otherwise keep the clips' own speeds.
      tuning: locomotionFrom(this.tunedWalk, this.tunedRun, outfit.height),
    };

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
    for (const [level, lod] of outfit.lods.entries()) {
      const target = bucket.targets[level];
      lod.mesh.thinInstanceSetBuffer('matrix', target[0], 16, false);
      lod.mesh.thinInstanceSetBuffer('crowdClipA', target[1], 4, false);
      lod.mesh.thinInstanceSetBuffer('crowdClipB', target[2], 4, false);
      lod.mesh.thinInstanceSetBuffer('crowdFade', target[3], 2, false);
      lod.mesh.thinInstanceCount = 0;
      lod.mesh.isVisible = false;
    }

    return bucket;
  }

  /**
   * Draws `weapon` in the hand of every body whose replicated `stance` is `stance`, and plays its clip on each of their
   * blows. A weapon that has not loaded is a stance drawn empty-handed, with the attack role's blow.
   */
  setWeapon(stance: number, weapon: CrowdWeapon): void {
    while (this.weapons.length <= stance) {
      this.weapons.push(null);
    }

    this.weapons[stance] = weapon;
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
      if (bucket === null) {
        continue;
      }

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
        const outfit = this.outfits[which];
        if (bucket !== null && outfit !== null) {
          bucket.tuning = locomotionFrom(walkMps, runMps, outfit.height);
        }
      }
    }

    const outfitCount = this.ids.length;
    if (outfitCount === 0) {
      return;
    }

    // ---- place every body in its outfit's bucket -----------------------------------------------------------------
    for (let b = 0; b < count; b++) {
      const body = bodies[b];
      // An outfit that will never load dresses the body as a civilian: a person in the wrong clothes beats a person missing.
      const civilian = this.hashed[outfitFor(body.netId, this.hashed.length)] ?? 0;
      const asked = body.outfit >= 0 && body.outfit < outfitCount ? body.outfit : civilian;
      const which = this.failed[asked] ? civilian : asked;
      const bucket = this.buckets[which];
      const outfit = this.outfits[which];
      // <b>Still loading: drawn by nobody</b> — no box, no stand-in outfit (Loïc, 2026-10-26). It appears the frame its own
      // outfit lands, rather than changing clothes in front of the viewer.
      if (bucket === null || outfit === null) {
        continue;
      }

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
        const roles = locomotionRoles(descriptor.animations?.roles ?? {});
        const clips = descriptor.animations?.clips ?? [];
        slot = {
          netId: body.netId,
          outfit: which,
          graph: new AnimationGraph(roles, clips, bucket.tuning),
          tunedAt: this.tuningSerial,
          // Answered already: a body coming into view mid-fight is not owed the blows it struck before it was seen.
          strikes: body.strikes,
          hits: body.hits,
          // Up, so a body first seen lying down falls at once and holds it.
          down: 0,
          played: -1,
          acts: roles.attack !== undefined && roles.death !== undefined,
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
        // The one-shots, from what the server said: death from the body's replicated mode, a blow from the Attack events
        // the router counts. Death wins over everything and holds until the server destroys the corpse; a blow is
        // accepted only between others, the graph's own rule.
        if (slot.acts) {
          if (body.dead && !slot.graph.dead) {
            slot.graph.fire('die', clock);
          }

          if (body.down !== slot.down) {
            this.fall(slot, outfit.id, body.down, clock);
          }

          if (body.hits !== slot.hits) {
            slot.hits = body.hits;
            if (!slot.graph.dead && this.plan.slotOf(outfit.id, 'hit') !== null) {
              slot.graph.fire('hit', clock);
            }
          }

          if (body.strikes !== slot.strikes) {
            slot.strikes = body.strikes;
            if (slot.graph.accepts('attack', clock, true)) {
              // A body in a stance strikes with its weapon's clip, under the role the atlas baked it as; anything else
              // alternates the rig's attack role, as the scripted crowd and the creatures always have.
              const clip = this.weapons[body.stance]?.clip ?? null;
              if (clip !== null && this.plan.slotOf(outfit.id, clipRole(clip)) !== null) {
                slot.graph.strike(clip, clock, clipRole(clip));
              } else {
                slot.graph.fire('attack', clock);
              }
            }
          }
        }

        const out = slot.graph.update(body.speed, true, clock);
        // <b>By ROLE, not by clip name.</b> See `AnimOutput.role`: the atlas is keyed on the role because an
        // outfit may carry its own copy of one, and a clip name finds nothing without saying so.
        const atlas = this.plan.slotOf(outfit.id, out.role);
        if (atlas !== null) {
          if (out.loop) {
            bucket.animator.play(i, atlas, clock, out.rate);
          } else if (out.serial !== slot.played) {
            slot.played = out.serial;
            bucket.animator.play(i, atlas, clock);
          }
        }
      }

      // Yaw only: a body leans with the ground nowhere in this client, and the matrix is the whole placement because
      // the glTF root flip is baked into the atlas rather than left on the mesh.
      Quaternion.RotationYawPitchRollToRef(body.heading, 0, 0, this.scratch.quat);
      this.scratch.pos.set(body.x, body.y + lift, body.z);
      Matrix.ComposeToRef(ONE, this.scratch.quat, this.scratch.pos, this.scratch.matrix);
      this.scratch.matrix.copyToArray(bucket.matrices, i * 16);
      // In the hand while it stands in its stance: not on the swoop (the server drops the stance there anyway), not dead.
      bucket.held[i] =
        ride === null && !body.dead && body.down === 0 && (this.weapons[body.stance] ?? null) !== null
          ? body.stance
          : 0;
      this.drawn.add(body.netId);
    }

    // ---- classify, pack and upload ---------------------------------------------------------------------------
    this.drawCalls = 0;
    this.trianglesDrawn = 0;
    for (const weapon of this.weapons) {
      if (weapon !== null) {
        weapon.count = 0;
      }
    }

    for (const [which, bucket] of this.buckets.entries()) {
      const outfit = this.outfits[which];
      if (bucket === null || outfit === null) {
        continue;
      }

      // One height for the whole bucket: every body in it wears the same outfit, so an array of 2 048 copies of one
      // constant was 2 048 copies of one constant. `classify` has taken a scalar since it was written.
      classify(bucket.matrices, bucket.count, outfit.height, view, bucket.tiers, this.tierTotals);
      this.packWeapons(bucket);
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

    for (const weapon of this.weapons) {
      if (weapon === null) {
        continue;
      }

      const mesh = weapon.mesh;
      mesh.isVisible = weapon.count > 0;
      mesh.thinInstanceCount = weapon.count;
      if (weapon.count > 0) {
        mesh.thinInstancePartialBufferUpdate('matrix', weapon.count, 0);
        mesh.thinInstancePartialBufferUpdate('crowdClipA', weapon.count, 0);
        mesh.thinInstancePartialBufferUpdate('crowdClipB', weapon.count, 0);
        mesh.thinInstancePartialBufferUpdate('crowdFade', weapon.count, 0);
        this.drawCalls++;
        this.trianglesDrawn += weapon.triangles * weapon.count;
      }
    }
  }

  /**
   * A player going down, or getting up (`CrowdBody.down`): dead or knocked out, it falls with its death and holds it; a
   * knocked-out body gets up with `StandUp`. A clone wakes elsewhere, already standing.
   */
  private fall(slot: Slot, outfit: string, down: number, clock: number): void {
    const was = slot.down;
    slot.down = down;
    if (down !== 0) {
      slot.graph.fire('die', clock);
    } else {
      slot.graph.fire('respawn', clock);
      if (was === DOWN_KNOCKED_OUT && this.plan.slotOf(outfit, clipRole(GET_UP_CLIP)) !== null) {
        slot.graph.getUp(GET_UP_CLIP, clock, clipRole(GET_UP_CLIP));
      }
    }
  }

  /**
   * Copies each body that holds a weapon into that weapon's instances: the body's own placement and clip state, so the
   * shader poses the weapon on the hand it poses. Only for the tiers drawn as meshes — a sprite or a culled body has no hand.
   */
  private packWeapons(bucket: Bucket): void {
    const animator = bucket.animator;
    for (let i = 0; i < bucket.count; i++) {
      const stance = bucket.held[i];
      const tier = bucket.tiers[i];
      if (stance === 0 || tier === CULLED || tier >= SPRITE_TIER) {
        continue;
      }

      const weapon = this.weapons[stance] ?? null;
      if (weapon === null || weapon.count >= weapon.targets[1].length / 4) {
        continue;
      }

      const k = weapon.count++;
      const [matrix, clipA, clipB, fade] = weapon.targets;
      matrix.set(bucket.matrices.subarray(i * 16, i * 16 + 16), k * 16);
      clipA.set(animator.clipA.subarray(i * 4, i * 4 + 4), k * 4);
      clipB.set(animator.clipB.subarray(i * 4, i * 4 + 4), k * 4);
      fade.set(animator.fade.subarray(i * 2, i * 2 + 2), k * 2);
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
      for (const lod of outfit?.lods ?? []) {
        lod.mesh.isVisible = false;
        lod.mesh.thinInstanceCount = 0;
      }
    }

    for (const weapon of this.weapons) {
      if (weapon !== null) {
        weapon.mesh.isVisible = false;
        weapon.mesh.thinInstanceCount = 0;
      }
    }

    this.drawn.clear();
  }

  dispose(): void {
    for (const outfit of this.outfits) {
      outfit?.dispose();
    }

    for (const weapon of this.weapons) {
      weapon?.dispose();
    }
  }
}

/** The server's `Downed.KnockedOut` (`PlayerVitals.Down`): the one fall a body gets up from. */
const DOWN_KNOCKED_OUT = 1;

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
