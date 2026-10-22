import type { AnimationGroup } from '@babylonjs/core/Animations/animationGroup';
import type { Skeleton } from '@babylonjs/core/Bones/skeleton';
import { RawTexture } from '@babylonjs/core/Materials/Textures/rawTexture';
import { Texture } from '@babylonjs/core/Materials/Textures/texture';
import { Matrix } from '@babylonjs/core/Maths/math.vector';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import type { Scene } from '@babylonjs/core/scene';

import type { Instance } from '../assets/asset-library';
import type { AtlasPlan } from '../character/clip-atlas';

/**
 * The crowd's clips, baked into one texture of bone matrices.
 *
 * Row `r` is one frame: `bones` matrices of four RGBA32F texels each. Each matrix is the bone's skinning matrix times
 * the skinned mesh's WORLD matrix, so the crowd mesh sits at identity and a thin instance's matrix is the whole
 * placement. That multiplication is not an optimisation — Babylon applies a thin instance's matrix BEFORE the mesh's
 * own world matrix, so leaving the glTF root's handedness flip on the mesh would mirror every instance's position.
 *
 * <b>Why not Babylon's `VertexAnimationBaker`.</b> It plays clips through `scene.beginAnimation(skeleton)`, which
 * drives the skeleton's own bone animations — and a glTF skeleton has none: the loader animates the joint transform
 * NODES through animation groups, each bone linked to its node. Pointed at a glTF rig it bakes the bind pose. This
 * evaluates the groups' curves at each frame and lets `Skeleton.prepare` copy the nodes into the bones. Deterministic,
 * and it renders nothing.
 *
 * <b>Why the plan comes from outside.</b> The spike baked every clip of ONE outfit and played it for all nineteen,
 * which is wrong by up to 414 mm on the five outfits the pack gives their own keys. {@link AtlasPlan} decides which
 * outfit each clip is baked from; this executes it. The arithmetic of that choice is unit-tested without a GPU.
 */
export interface CrowdRig {
  readonly texture: RawTexture;
  readonly plan: AtlasPlan;
  readonly fps: number;
  readonly bones: number;
  readonly boneNames: readonly string[];
  /** The canonical inverse bind matrices, by bone index — what {@link rigOffset} rebases an outfit against. */
  readonly inverseBind: readonly Matrix[];
  readonly textureBytes: number;
  /** The texture's contents, kept so a bone socket or a CPU-side check can read a pose without the GPU. */
  readonly data: Float32Array;
  dispose(): void;
}

/** A loaded source outfit: its skinned primitives and its animation groups. */
export interface BakeSource {
  readonly instance: Instance;
}

/**
 * Bakes an atlas plan into one texture.
 *
 * @param scene The live scene.
 * @param plan What to bake and where, from {@link planClipAtlas}.
 * @param sources The instantiated source outfits the plan names, by id. Each is read and may be disposed afterwards
 * by the caller; nothing here keeps a reference to one.
 * @returns The rig.
 * @throws If a named source is missing, has no skinned primitive, or does not share the first source's skeleton shape.
 */
export function bakeClipAtlas(scene: Scene, plan: AtlasPlan, sources: ReadonlyMap<string, BakeSource>): CrowdRig {
  const data = new Float32Array(plan.rows * plan.bones * 16);
  const bone = new Matrix();
  let boneNames: readonly string[] | null = null;
  let inverseBind: readonly Matrix[] | null = null;

  for (const step of plan.bakes) {
    const source = sources.get(step.source);
    if (source === undefined) {
      throw new Error(`crowd: no source loaded for ${step.source}/${step.clip}`);
    }

    const skinned = skinnedPrimitives(source.instance).at(0);
    const skeleton = skinned?.skeleton ?? null;
    if (skinned === undefined || skeleton === null) {
      throw new Error(`crowd: ${step.source} has no skinned primitive`);
    }

    if (skeleton.bones.length !== plan.bones) {
      throw new Error(`crowd: ${step.source} has ${String(skeleton.bones.length)} bones, the plan says ${String(plan.bones)}`);
    }

    // The first source defines the canonical skeleton; every later one must agree, because one texture serves them
    // all and a bone index means the same thing in every row.
    const names = skeleton.bones.map((b) => b.name);
    if (boneNames === null) {
      boneNames = names;
      inverseBind = skeleton.bones.map((b) => b.getAbsoluteInverseBindMatrix().clone());
    } else if (names.some((n, i) => n !== boneNames?.[i])) {
      throw new Error(`crowd: ${step.source}'s bones are not in the rig's order`);
    }

    const group = source.instance.animationGroups.find((g) => clipName(g) === step.clip);
    if (group === undefined) {
      throw new Error(`crowd: ${step.source} publishes no clip "${step.clip}"`);
    }

    group.stop();
    const sourceFps = group.targetedAnimations[0]?.animation.framePerSecond ?? 60;
    for (let k = 0; k < step.frames; k++) {
      // A looping clip's last key equals its first, so sampling [0, duration) rather than [0, duration] keeps the
      // loop from holding one frame twice — a visible hitch once a second on an idle.
      const at = group.from + (k / plan.fps) * sourceFps;
      for (const targeted of group.targetedAnimations) {
        assign(targeted.target as Record<string, unknown>, targeted.animation.targetProperty, targeted.animation.evaluate(at));
      }

      // Recomputed per frame rather than hoisted: nothing in this pack animates an ancestor of the mesh node, but the
      // cost is one matrix and the alternative is a silent error if one ever does.
      const world = skinned.computeWorldMatrix(true);
      skeleton.prepare(true);
      const matrices = skeleton.getTransformMatrices(skinned);
      const out = (step.startRow + k) * plan.bones * 16;
      for (let b = 0; b < plan.bones; b++) {
        Matrix.FromArrayToRef(matrices, b * 16, bone);
        bone.multiplyToArray(world, data, out + b * 16);
      }
    }
  }

  const texture = RawTexture.CreateRGBATexture(
    data,
    plan.width,
    plan.rows,
    scene,
    false,
    false,
    // NEAREST: a row is a discrete frame, and interpolating between two of them interpolates MATRICES, which is not
    // the same as interpolating a pose. The crossfade blends two poses in the shader instead.
    Texture.NEAREST_NEAREST,
    // TEXTURETYPE_FLOAT. Matrices are not colours and do not survive 8 bits.
    1,
  );
  texture.name = 'crowd-atlas';

  return {
    texture,
    plan,
    fps: plan.fps,
    bones: plan.bones,
    boneNames: boneNames ?? [],
    inverseBind: inverseBind ?? [],
    textureBytes: data.byteLength,
    data,
    dispose() {
      texture.dispose();
    },
  };
}

/**
 * The transform `Q` carrying an outfit's skin onto the rig's: `IBM_outfit = Q × IBM_rig` for every bone.
 *
 * The pack's characters share one armature, but gltfpack quantises each outfit's positions with its own offset and
 * folds the dequantisation into that outfit's inverse bind matrices. The difference between two outfits is therefore
 * ONE transform common to all bones — a similarity, not just a translation: the measured scales are 1.977e-4 on
 * fifteen outfits and three other values on the rest. The outfit's mesh absorbs it once, at merge time.
 *
 * @param rig The baked rig.
 * @param skeleton The outfit's skeleton.
 * <b>The tolerance is measured, not guessed, and tightening it broke every outfit.</b> Over the eighteen wearable
 * characters the worst per-component spread of `IBM_outfit × IBM_rig⁻¹` across bones is **2.7e-3**
 * (`casual2_male`, `Shoulder.R`), the best 7.4e-4, and in every case it is in the TRANSLATION column — components 12
 * and 13 — never the rotation block. So the pack really is one transform plus float noise in the offset, and 1e-2
 * leaves about 3.7× headroom over the worst real outfit. 1e-4 is inside the noise and rejects all nineteen.
 *
 * @param tolerance Allowed per-component drift, RELATIVE to the component's own magnitude.
 * @returns `Q`.
 * @throws If no single transform relates the two — a different armature, not a different quantisation.
 */
export function rigOffset(rig: CrowdRig, skeleton: Skeleton, tolerance = 1e-2): Matrix {
  if (skeleton.bones.length !== rig.boneNames.length || skeleton.bones.some((b, i) => b.name !== rig.boneNames[i])) {
    throw new Error('crowd: outfit skeleton does not match the rig (bone names or order)');
  }

  let q: Matrix | null = null;
  for (const [i, b] of skeleton.bones.entries()) {
    const canonical = rig.inverseBind.at(i);
    if (canonical === undefined) {
      continue;
    }

    const m = b.getAbsoluteInverseBindMatrix().multiply(canonical.clone().invert());
    if (q === null) {
      q = m;
      continue;
    }

    for (let k = 0; k < 16; k++) {
      const scale = Math.max(1, Math.abs(q.m[k] ?? 0));
      if (Math.abs((m.m[k] ?? 0) - (q.m[k] ?? 0)) > tolerance * scale) {
        throw new Error(`crowd: outfit skin differs from the rig by more than one transform (bone ${b.name})`);
      }
    }
  }

  return q ?? Matrix.Identity();
}

/**
 * The skinned, drawable primitives of an instance.
 *
 * <b>Filtered by what they ARE, never by what they are called.</b> The published GLBs carry two EMPTY transform nodes
 * named `Body_LOD1` and `Body_LOD2` beside the armature — residue of the exporter's regrouping step — so a name match
 * finds decoys. Having vertices and a skeleton is the property that matters.
 */
export function skinnedPrimitives(instance: Instance): Mesh[] {
  return instance.meshes.filter((m): m is Mesh => m instanceof Mesh && m.getTotalVertices() > 0 && m.skeleton !== null);
}

/** An instantiated group is named `<label>/<clip>`; this is the clip. */
export function clipName(group: AnimationGroup): string {
  return group.name.slice(group.name.lastIndexOf('/') + 1);
}

function assign(target: Record<string, unknown>, property: string, value: unknown): void {
  const current = target[property] as { copyFrom?: (v: unknown) => void } | null | undefined;
  if (current !== undefined && current !== null && typeof current.copyFrom === 'function') {
    current.copyFrom(value);
  } else {
    target[property] = value;
  }
}
