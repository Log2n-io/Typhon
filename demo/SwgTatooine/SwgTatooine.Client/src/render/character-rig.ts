import type { AnimationGroup } from '@babylonjs/core/Animations/animationGroup';
import '@babylonjs/core/Animations/animatable';
import { Quaternion } from '@babylonjs/core/Maths/math.vector';
import type { Instance } from '../assets/asset-library';
import type { AnimOutput } from '../character/animation-graph';

/**
 * Plays what the animation graph decided, on one skinned instance, with crossfades.
 *
 * Each clip is one `AnimationGroup` of the instance. Blending is by group WEIGHT: the target clip's weight rises to 1
 * over `fadeS` while every other playing clip falls to 0 and is stopped there. A one-shot is (re)started whenever the
 * graph's serial changes, even when it is the clip already playing — two punches in a row are two punches. A
 * non-looping clip that ends keeps its last frame, which is what holds a death pose.
 *
 * <b>The groups belong to the instance, not to the container.</b> `instantiateModelsToScene` clones them per placement,
 * so two characters of the same asset animate independently; the names arrive as `<label>/<clip>`.
 *
 * Ported from the asset testbench (`BlenderGenTest/tools/testbench/src/character/character-rig.ts`), trimmed to what
 * this client drives: its viewer controls and its camera-proximity fade belong with the features that call them.
 */
export class CharacterRig {
  readonly instance: Instance;
  private readonly groups = new Map<string, AnimationGroup>();
  private readonly weights = new Map<AnimationGroup, number>();
  private readonly fadeS: number;
  private target: AnimationGroup | null = null;
  private serial = -1;

  constructor(instance: Instance, fadeS = 0.18) {
    this.instance = instance;
    this.fadeS = fadeS;
    for (const group of instance.animationGroups) {
      const clip = group.name.slice(group.name.lastIndexOf('/') + 1);
      group.stop();
      group.weight = 0;
      this.groups.set(clip, group);
    }
  }

  /**
   * The world altitude of the lowest point of the drawn body, or `NaN` when it is not drawn.
   *
   * <b>Read from Babylon's own bounding boxes, not from what the renderer was ASKED to draw.</b> "The character is
   * flying" is a claim about the distance between two numbers that nobody could see: the altitude the collision rule
   * computed and where the mesh ended up. Any of the placement, the rig's own pivot, the animation's root motion or
   * the drawn terrain can put those apart. This is the measured one.
   */
  lowestY(): number {
    let low = Number.POSITIVE_INFINITY;
    for (const mesh of this.instance.meshes) {
      if (!mesh.isEnabled() || mesh.getTotalVertices() === 0) {
        continue;
      }

      mesh.computeWorldMatrix(true);
      const y = mesh.getBoundingInfo().boundingBox.minimumWorld.y;
      if (y < low) {
        low = y;
      }
    }

    return Number.isFinite(low) ? low : Number.NaN;
  }

  get clips(): readonly string[] {
    return [...this.groups.keys()];
  }

  /** Whether this rig is drawn at all. A session with no subject has a character and nothing to put it on. */
  setEnabled(enabled: boolean): void {
    this.instance.root.setEnabled(enabled);
  }

  /**
   * Places the rig.
   *
   * @param x Planet X of the feet — the body's origin is between them.
   * @param y Ground altitude there.
   * @param z Planet Z.
   * @param yaw Babylon's `rotation.y`.
   */
  setPose(x: number, y: number, z: number, yaw: number): void {
    const root = this.instance.root;
    root.position.set(x, y, z);
    // Into the quaternion the node already holds: this runs every frame, and a fresh Vector3 and Quaternion per frame
    // per character is the allocation the crowd path cannot afford.
    root.rotationQuaternion ??= new Quaternion();
    Quaternion.RotationYawPitchRollToRef(yaw, 0, 0, root.rotationQuaternion);
  }

  /** {@link setPose} with a whole rotation rather than a yaw: a rider leaning with the bike it sits on. */
  setPoseRotation(x: number, y: number, z: number, rotation: Quaternion): void {
    const root = this.instance.root;
    root.position.set(x, y, z);
    root.rotationQuaternion ??= new Quaternion();
    root.rotationQuaternion.copyFrom(rotation);
  }

  /**
   * Applies one frame of the graph's output.
   *
   * @param out What the graph decided.
   * @param dt Seconds since the last frame — what the crossfade advances on.
   */
  apply(out: AnimOutput, dt: number): void {
    const group = this.groups.get(out.clip);
    if (group === undefined) {
      return;
    }

    const restart = out.serial !== this.serial && !out.loop;
    this.serial = out.serial;
    if (group !== this.target || restart) {
      if (restart || !group.isPlaying) {
        group.stop();
        group.start(out.loop, out.rate);
      }

      if (!this.weights.has(group)) {
        this.weights.set(group, 0);
      }

      this.target = group;
    }

    group.loopAnimation = out.loop;
    group.speedRatio = out.rate;
    const step = this.fadeS > 0 ? dt / this.fadeS : 1;
    for (const [playing, weight] of this.weights) {
      const next = playing === this.target ? Math.min(1, weight + step) : Math.max(0, weight - step);
      playing.weight = next;
      if (next === 0 && playing !== this.target) {
        playing.stop();
        this.weights.delete(playing);
      } else {
        this.weights.set(playing, next);
      }
    }
  }

  /**
   * Holds a one-shot clip on its last frame — the seated rider: `SitDown` played to its end and kept there (the swoop's
   * seat `pose` is `{clip: "SitDown", at: "end"}`). Everything else is stopped at once; {@link apply} fades back out of it.
   *
   * @returns Whether the clip exists.
   */
  hold(clip: string): boolean {
    const group = this.groups.get(clip);
    if (group === undefined) {
      return false;
    }

    if (this.target === group) {
      return true;
    }

    for (const playing of this.weights.keys()) {
      if (playing !== group) {
        playing.stop();
        playing.weight = 0;
      }
    }

    this.weights.clear();
    group.start(false, 1, group.from, group.to);
    group.goToFrame(group.to);
    group.pause();
    group.weight = 1;
    this.weights.set(group, 1);
    this.target = group;
    this.serial = -2;
    return true;
  }

  dispose(): void {
    for (const group of this.groups.values()) {
      group.stop();
    }

    this.groups.clear();
    this.weights.clear();
    this.target = null;
  }
}
