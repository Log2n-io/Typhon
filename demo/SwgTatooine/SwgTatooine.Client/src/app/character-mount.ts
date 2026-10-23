import type { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import { axisVector, placement, yawForHeading } from '../assets/asset-frame';
import type { AssetLibrary } from '../assets/asset-library';
import { AnimationGraph, locomotionFrom } from '../character/animation-graph';
import type { SceneLighting } from '../render/scene-lighting';
import { turnToward } from '../math/vec';
import { CharacterRig } from '../render/character-rig';
import { prepareAsset } from './asset-mount';

/**
 * One replicated body drawn as a rigged, animated character.
 *
 * <b>It follows the simulation; it does not drive it.</b> The position, the heading and the speed all come from the
 * store — the server integrates movement through `Steer` and replicates the result, and this turns that result into a
 * clip. There is no motor here, no prediction of its own and no authority: a character that walks on screen because the
 * body it is pinned to walked on the server. That is the whole of this slice, and it is what lets the rig arrive before
 * any of the questions the testbench's motor raises (the capsule radius, the step height, who decides a portal) have to
 * be answered.
 *
 * <b>The speed comes from the replicated velocity, in metres per second.</b> What arrives on the wire is a velocity per
 * TICK — that is what keeps a changing tick period from distorting history — so the caller converts with the server's
 * own period. The same vector gives the mesh its heading, so the clip and the facing can never disagree about which way
 * the character is walking.
 */
/**
 * How fast the body may turn, radians per second — one and a half turns, so a real about-face takes about a third of a
 * second and reads as a person turning rather than as a sprite being flipped.
 *
 * It is also what makes a reconciliation harmless. A corrected body is walked backwards by the replicated motion for a
 * tick or two; unbounded, the character faces that correction and spins right round, and when the velocity then reaches
 * exactly zero the heading is frozen facing backwards. Bounded, 17 ms of correction is about nine degrees.
 */
export const MAX_TURN_RATE = Math.PI * 3;

export interface CharacterMount {
  readonly rig: CharacterRig;
  readonly graph: AnimationGraph;
  /** The body's height in metres, from its descriptor: what the clip rates are scaled by. */
  readonly height: number;
  /** The netId this rig is currently standing in for, or 0 when it is parked. */
  readonly subjectNetId: number;
  /**
   * Re-tunes locomotion to the server's replicated gaits. Cheap, and called whenever they are known; before the owner
   * block arrives they are `NaN` and the tuning falls back to the clips' own speeds.
   */
  retune(walkMps: number, runMps: number): void;
  /**
   * Draws the character at a replicated placement.
   *
   * @param netId Whose body this is; a change re-pins the rig and is not a teleport to animate.
   * @param x Planet X.
   * @param y Altitude of the feet.
   * @param z Planet Z.
   * @param speed Ground speed in metres per SECOND. The replicated velocity is per tick; converting it is the caller's
   *   job, because only the caller knows the server's period.
   * @param heading Where the body is TRYING to face, radians from +X towards +Z. The rig turns towards it at
   *   {@link MAX_TURN_RATE} rather than snapping, so a momentary reversal is a lean and not an about-face.
   * @param nowS A monotonic clock in seconds, for one-shot durations.
   * @param dt Seconds since the last frame.
   */
  update(
    netId: number,
    x: number,
    y: number,
    z: number,
    speed: number,
    heading: number,
    nowS: number,
    dt: number,
  ): void;
  /** Parks the rig: nothing to stand in for, so nothing drawn. */
  park(): void;
  dispose(): void;
}

/**
 * Loads a character asset and makes one rig from it.
 *
 * @param library The open asset library.
 * @param assetId Which character — a `kind: "character"` asset with `animations` in its descriptor.
 * @param root The node the arrangement hangs off, which carries the render origin.
 * @param lighting Gives the rig's materials their share of baked AO on direct light.
 * @returns The mount, parked until something gives it a subject.
 */
export async function mountCharacter(
  library: AssetLibrary,
  assetId: string,
  root: TransformNode,
  lighting: SceneLighting,
): Promise<CharacterMount> {
  const asset = await library.acquire(assetId);
  const animations = asset.descriptor.animations;
  if (animations === undefined) {
    throw new Error(`asset "${assetId}" has no animations, so it is not a character`);
  }

  // Parked at the origin until a subject arrives. Placed through the same `placement` every other asset uses, so the
  // glTF mirror is applied in exactly one place.
  // Once per asset, on the container: an instance takes its rendering group from what it instances, and the materials
  // are shared. A skinned mesh is cloned rather than instanced — Babylon refuses to instance one — but the group and
  // the AO factor are the container's either way.
  prepareAsset(asset, lighting);
  const instance = await library.instantiate(assetId, placement([0, 0, 0], 0), root);
  // <b>The avatar casts too, and it is marked here rather than with the structures.</b> A skinned mesh cannot be
  // instanced — Babylon refuses — so this one is a clone, which `static-assets` never sees.
  lighting.castShadows(instance.meshes);

  const [min, max] = asset.descriptor.bounds.render;
  const height = max[1] - min[1];
  // Which way the model faces in its own frame ("+Z" for the Quaternius rigs). Read from the descriptor rather than
  // assumed, because it is the one fact a differently-authored character would differ on, and a wrong guess shows up as
  // a body that walks sideways.
  const forward = axisVector(asset.descriptor.forward ?? '+Z');
  const rig = new CharacterRig(instance);
  const graph = new AnimationGraph(animations.roles, animations.clips, locomotionFrom(Number.NaN, Number.NaN, height));
  rig.setEnabled(false);

  let subject = 0;
  let facing = 0;
  let tunedWalk = Number.NaN;
  let tunedRun = Number.NaN;
  return {
    rig,
    graph,
    height,
    get subjectNetId(): number {
      return subject;
    },
    retune(walkMps: number, runMps: number): void {
      // Called every frame with figures that change about never; rebuilding the tuning each time allocated one per
      // frame for nothing.
      if (walkMps === tunedWalk && runMps === tunedRun) {
        return;
      }

      tunedWalk = walkMps;
      tunedRun = runMps;
      graph.tuning = locomotionFrom(walkMps, runMps, height);
    },
    update(netId, x, y, z, speed, heading, nowS, dt): void {
      if (subject !== netId) {
        subject = netId;
        // A new body is not a body that turned: it arrives facing wherever it faces.
        facing = heading;
        rig.setEnabled(true);
      }

      // A floor under dt, because `turnToward` snaps when the step is not positive — so a frame reporting zero elapsed
      // time would turn the body instantly, which is the flip the clamp exists to prevent.
      facing = turnToward(facing, heading, MAX_TURN_RATE * Math.max(dt, 1e-4));
      rig.setPose(x, y, z, yawForHeading(facing, forward));
      // Grounded is true because nothing replicates otherwise: this client has no airborne state, and a jump is an
      // animation event it does not receive yet. Stated here rather than hidden in the call, so the day either arrives
      // there is one place to change.
      rig.apply(graph.update(speed, true, nowS), dt);
    },
    park(): void {
      if (subject !== 0) {
        subject = 0;
        rig.setEnabled(false);
      }
    },
    dispose(): void {
      rig.dispose();
      instance.dispose();
    },
  };
}
