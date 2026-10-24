import { Material } from '@babylonjs/core/Materials/material';
import { Quaternion, Vector3 } from '@babylonjs/core/Maths/math.vector';
import { AbstractMesh } from '@babylonjs/core/Meshes/abstractMesh';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import { axisVector, placement, yawForHeading } from '../assets/asset-frame';
import type { AssetLibrary, Instance } from '../assets/asset-library';
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

/** The vehicle a rider summons with the 1 key. */
export const SWOOP_ASSET = 'swoop';

/**
 * Where the rider's root sits above the swoop's origin, metres, when the descriptor has not been read: `seats[0].pos` of
 * `swoop-1` (blender-models). With `SitDown`'s end pose that puts the thighs on the 0.836 m cushion (physics, measured).
 */
const DEFAULT_SEAT_Y = 0.62;

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
  /**
   * Puts the body on its swoop, or back on foot: the swoop drawn under it, the rider on the descriptor's seat, holding
   * `SitDown` on its last frame. The swoop is loaded on the first ride; until it has arrived the rider still sits.
   */
  setRiding(on: boolean): void;
  /** Whether the body is drawn on its swoop. */
  readonly riding: boolean;
  /**
   * Puts a weapon in the hand (ASSET_FORMAT.md § 5.10), or empties it (`null`). Loaded on first use, never for the ghost;
   * until it has arrived the hand is empty and {@link weapon} is `null`. Not drawn while riding.
   */
  setWeapon(assetId: string | null): void;
  /** The weapon in the hand, once loaded: the clip it attacks with, and that clip's length in seconds (`NaN` if the rig lacks it). */
  readonly weapon: HeldWeapon | null;
  /**
   * The swoop's attitude and hover, drawing only (`swoop-drive.ts`, `swoop-hover.ts`): the bike rolls about its own length
   * by `roll` radians — positive puts the right side down — pitches its nose up by `pitch`, and floats `lift` metres above
   * the ground the body stands on. The rider is carried with it. All 0 for a swoop nobody here steers.
   */
  setLean(roll: number, pitch: number, lift: number): void;
  /** How far above its feet the body is drawn: the seat's height while riding, 0 on foot. */
  readonly riderLift: number;
  /**
   * Fades the body, 0 to 1: the camera pulled in close to it — a wall behind — must not look through the inside of a head.
   *
   * The testbench's: `mesh.visibility`, with the materials switched to alpha blending and a depth pre-pass while fading
   * (the glTF loader pins them OPAQUE, which ignores visibility; the pre-pass keeps the limbs from showing through the
   * body), and back to opaque at 1. The meshes stay `isVisible` — dropping them would drop the shadow, which is what shows
   * a player where they stand.
   */
  setOpacity(opacity: number): void;
  dispose(): void;
}

/** A weapon a character holds; see {@link CharacterMount.weapon}. */
export interface HeldWeapon {
  readonly clip: string;
  readonly clipS: number;
}

interface LoadedWeapon extends HeldWeapon {
  /** The node under the hand's joint the weapon hangs from: enabling it shows the weapon. */
  readonly holder: TransformNode;
  readonly instance: Instance;
}

/**
 * Loads a character asset and makes one rig from it.
 *
 * @param library The open asset library.
 * @param assetId Which character — a `kind: "character"` asset with `animations` in its descriptor.
 * @param root The node the arrangement hangs off, which carries the render origin.
 * @param lighting Gives the rig's materials their share of baked AO on direct light.
 * @param ghostAlpha Draws this rig translucent at this opacity, on its own copies of the materials, and casting no
 *   shadow: the "Server pos" ghost, which stands where the server says the avatar is beside the predicted body. Omitted
 *   for an ordinary, opaque character.
 * @returns The mount, parked until something gives it a subject.
 */
export async function mountCharacter(
  library: AssetLibrary,
  assetId: string,
  root: TransformNode,
  lighting: SceneLighting,
  ghostAlpha?: number,
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
  const ownMaterials: Material[] = [];
  if (ghostAlpha === undefined) {
    // <b>The avatar casts too, and it is marked here rather than with the structures.</b> A skinned mesh cannot be
    // instanced — Babylon refuses — so this one is a clone, which `static-assets` never sees.
    lighting.castShadows(instance.meshes);

    // Its OWN materials too, for `setOpacity`: the clone shares the container's, and fading those would fade every other
    // body drawn from the same asset (the physics session's pitfall).
    for (const mesh of instance.meshes) {
      const material = mesh.material?.clone(`${mesh.material.name}#own`) ?? null;
      if (material !== null) {
        mesh.material = material;
        ownMaterials.push(material);
      }
    }
  } else {
    // Its OWN materials: the clone shares the container's, so making those translucent would ghost the avatar too. A
    // depth pre-pass keeps the limbs from showing through the body, which a translucent skinned mesh otherwise does.
    for (const mesh of instance.meshes) {
      const material = mesh.material?.clone(`${mesh.material.name}#ghost`) ?? null;
      if (material === null) {
        continue;
      }

      material.alpha = ghostAlpha;
      material.transparencyMode = Material.MATERIAL_ALPHABLEND;
      material.needDepthPrePass = true;
      mesh.material = material;
      ownMaterials.push(material);
    }
  }

  const [min, max] = asset.descriptor.bounds.render;
  const height = max[1] - min[1];
  // Which way the model faces in its own frame ("+Z" for the Quaternius rigs). Read from the descriptor rather than
  // assumed, because it is the one fact a differently-authored character would differ on, and a wrong guess shows up as
  // a body that walks sideways.
  const forward = axisVector(asset.descriptor.forward ?? '+Z');
  const rig = new CharacterRig(instance);
  const graph = new AnimationGraph(animations.roles, animations.clips, locomotionFrom(Number.NaN, Number.NaN, height));
  rig.setEnabled(false);

  // The skeleton's joints by their glTF name (`<label>/Fist.R` once instantiated): where a weapon is attached. The
  // animation drives these nodes, so whatever hangs from one moves with the hand.
  const joints = new Map<string, TransformNode>();
  for (const node of instance.root.getDescendants(false)) {
    if (node instanceof TransformNode && !(node instanceof AbstractMesh)) {
      joints.set(node.name.slice(node.name.lastIndexOf('/') + 1), node);
    }
  }

  // The pack's uniform scale, which a joint's world matrix carries and a weapon's `attach.pos` does not (§ 5.10).
  const characterScale = asset.descriptor.source?.scale ?? 1;
  const weapons = new Map<string, LoadedWeapon | null>();
  let wantedWeapon: string | null = null;
  // Read through a call: after an await, a plain boolean checked twice looks constant to the type checker.
  let disposed = false;
  const gone = (): boolean => disposed;

  let subject = 0;
  let facing = 0;
  let tunedWalk = Number.NaN;
  let tunedRun = Number.NaN;
  let fading = false;

  // The swoop: its own node beside the rider's, posed with it every frame. Loaded on the first ride, never for a body
  // that does not ride, and not at all for the ghost.
  let riding = false;
  let swoop: Instance | null = null;
  let swoopLoading = false;
  let seatY = DEFAULT_SEAT_Y;
  let lean = 0;
  let nose = 0;
  let hover = 0;
  const seatOffset = new Vector3();
  const seatAt = new Vector3();
  const swoopRoot = new TransformNode('swoop-ride', root.getScene());
  swoopRoot.parent = root;
  swoopRoot.rotationQuaternion = new Quaternion();
  swoopRoot.setEnabled(false);
  const loadSwoop = (): void => {
    if (swoopLoading || ghostAlpha !== undefined || library.catalogue.assets[SWOOP_ASSET] === undefined) {
      return;
    }

    swoopLoading = true;
    void library
      .instantiate(SWOOP_ASSET, placement([0, 0, 0], 0), swoopRoot)
      .then((placed) => {
        swoop = placed;
        lighting.castShadows(placed.meshes);
        const seat = library.described(SWOOP_ASSET)?.seats?.[0];
        if (seat !== undefined) {
          seatY = seat.pos[1];
        }
      })
      .catch((error: unknown) => {
        console.warn(`assets: ${SWOOP_ASSET} did not mount; riding draws the rider alone`, error);
      });
  };

  const showWeapons = (): void => {
    for (const [id, held] of weapons) {
      held?.holder.setEnabled(id === wantedWeapon && subject !== 0 && !riding);
    }
  };

  /**
   * Hangs a weapon from its joint: `weapon world = J × scale(1/s) × translate(pos) × rotate(rot)` (§ 5.10), as two nodes,
   * because one Babylon node scales BEFORE it translates.
   */
  const loadWeapon = async (id: string): Promise<void> => {
    const desc = (await library.describe(id)).weapon;
    const joint = desc === undefined ? undefined : joints.get(desc.attach.joint);
    if (desc === undefined || joint === undefined) {
      throw new Error(`"${id}" is no weapon, or "${assetId}" has no joint "${desc?.attach.joint ?? '?'}"`);
    }

    prepareAsset(await library.acquire(id), lighting);
    if (gone()) {
      return;
    }

    const scene = root.getScene();
    const holder = new TransformNode(`${id}/unscale`, scene);
    holder.parent = joint;
    holder.scaling.setAll(1 / characterScale);
    holder.setEnabled(false);
    const grip = new TransformNode(`${id}/grip`, scene);
    grip.parent = holder;
    grip.position.fromArray(desc.attach.pos);
    grip.rotationQuaternion = Quaternion.FromArray(desc.attach.rot);
    const placed = await library.instantiate(id, placement([0, 0, 0], 0), grip, `${assetId}/${id}`);
    if (gone()) {
      placed.dispose();
      holder.dispose();
      return;
    }

    // Under a joint everything is already in the glTF frame: the character's own glTF root turned it into Babylon's above
    // the skeleton. The weapon's root would turn it a second time, so it is made the identity.
    for (const node of placed.root.getChildren()) {
      if (node instanceof TransformNode) {
        node.position.setAll(0);
        node.rotationQuaternion = Quaternion.Identity();
        node.scaling.setAll(1);
      }
    }

    lighting.castShadows(placed.meshes);
    weapons.set(id, { holder, instance: placed, clip: desc.clip, clipS: graph.durationOf(desc.clip) });
    showWeapons();
  };

  return {
    rig,
    graph,
    height,
    get riding(): boolean {
      return riding;
    },
    get weapon(): HeldWeapon | null {
      return wantedWeapon === null ? null : (weapons.get(wantedWeapon) ?? null);
    },
    setWeapon(id: string | null): void {
      if (id === wantedWeapon) {
        return;
      }

      wantedWeapon = id;
      if (id !== null && !weapons.has(id) && ghostAlpha === undefined) {
        weapons.set(id, null);
        loadWeapon(id).catch((error: unknown) => {
          console.warn(`assets: weapon ${id} did not mount; the hand stays empty`, error);
        });
      }

      showWeapons();
    },
    get riderLift(): number {
      return riding ? seatY + hover : 0;
    },
    setLean(roll: number, pitch: number, lift: number): void {
      lean = roll;
      nose = pitch;
      hover = lift;
    },
    setRiding(on: boolean): void {
      if (on === riding) {
        return;
      }

      riding = on;
      if (on) {
        loadSwoop();
      }

      swoopRoot.setEnabled(on && subject !== 0);
      showWeapons();
    },
    setOpacity(opacity: number): void {
      if (ghostAlpha !== undefined) {
        return;
      }

      const nowFading = opacity < 0.999;
      if (nowFading !== fading) {
        fading = nowFading;
        for (const material of ownMaterials) {
          material.transparencyMode = nowFading ? Material.MATERIAL_ALPHABLEND : Material.MATERIAL_OPAQUE;
          material.needDepthPrePass = nowFading;
        }
      }

      // Not the swoop: its meshes are instances (`AssetLibrary.instantiate`), which Babylon cannot fade and warns about
      // on every call. The camera pulled in behind a rider looks over the swoop's tail, not through it.
      for (const mesh of instance.meshes) {
        mesh.visibility = opacity;
      }
    },
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
        swoopRoot.setEnabled(riding);
        showWeapons();
      }

      // A floor under dt, because `turnToward` snaps when the step is not positive — so a frame reporting zero elapsed
      // time would turn the body instantly, which is the flip the clamp exists to prevent.
      facing = turnToward(facing, heading, MAX_TURN_RATE * Math.max(dt, 1e-4));
      const yaw = yawForHeading(facing, forward);
      if (riding) {
        // The swoop's origin on the ground and its nose (+Z, as the rider's) along the heading; the rider on the seat,
        // facing the same way (the seat's 180° turn of -Z is +Z), sitting.
        // Banked about its own length and floating on its bob; the rider is carried on the seat through the same rotation,
        // so it leans with the bike rather than staying upright on a tilted seat.
        const tilt = swoopRoot.rotationQuaternion!;
        swoopRoot.position.set(x, y + hover, z);
        // Babylon's pitch puts the nose DOWN for a positive angle, and its roll lifts the right side; both are turned round.
        Quaternion.RotationYawPitchRollToRef(yaw, -nose, -lean, tilt);
        seatOffset.set(0, seatY, 0);
        seatOffset.rotateByQuaternionToRef(tilt, seatAt);
        rig.setPoseRotation(x + seatAt.x, y + hover + seatAt.y, z + seatAt.z, tilt);
        rig.hold('SitDown');
        return;
      }

      rig.setPose(x, y, z, yaw);
      // Grounded is true because nothing replicates otherwise: this client has no airborne state, and a jump is an
      // animation event it does not receive yet. Stated here rather than hidden in the call, so the day either arrives
      // there is one place to change.
      rig.apply(graph.update(speed, true, nowS), dt);
    },
    park(): void {
      if (subject !== 0) {
        subject = 0;
        rig.setEnabled(false);
        swoopRoot.setEnabled(false);
        showWeapons();
      }
    },
    dispose(): void {
      disposed = true;
      for (const held of weapons.values()) {
        held?.instance.dispose();
        held?.holder.dispose();
      }

      weapons.clear();
      rig.dispose();
      instance.dispose();
      swoop?.dispose();
      swoopRoot.dispose();
      for (const material of ownMaterials) {
        material.dispose();
      }
    },
  };
}
