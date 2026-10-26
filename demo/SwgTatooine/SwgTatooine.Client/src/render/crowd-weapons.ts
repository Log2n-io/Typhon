import { VertexBuffer } from '@babylonjs/core/Buffers/buffer';
import type { Material } from '@babylonjs/core/Materials/material';
import { PBRMaterial } from '@babylonjs/core/Materials/PBR/pbrMaterial';
import { Matrix, Quaternion, Vector3 } from '@babylonjs/core/Maths/math.vector';
import { InstancedMesh } from '@babylonjs/core/Meshes/instancedMesh';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import type { Scene } from '@babylonjs/core/scene';

import { placement } from '../assets/asset-frame';
import type { AssetLibrary } from '../assets/asset-library';
import type { Quat, Vec3 } from '../math/vec';
import type { CrowdRig } from './crowd-rig';
import { SCENE_GROUP } from './render-groups';

/**
 * A hand weapon drawn on crowd bodies: one mesh, skinned to the hand's joint at weight 1, thin-instanced once per body
 * that holds it.
 *
 * <b>The crowd has no joint nodes to hang a weapon from</b> — it skins on the GPU from the baked atlas — so the weapon is
 * drawn as the asset format says a renderer without nodes should (ASSET_FORMAT.md § 5.10): its vertices put in the rig's
 * bind space once, at load, `v_bind = inverse(IBM[joint]) × scale(1/s) × translate(pos) × rotate(rot) × v`, and every
 * vertex bound to that one joint. Each instance then carries the SAME placement and clip state as the body holding it
 * (`CrowdLayer`), so the shader poses the hand and the weapon with one lookup and they cannot come apart.
 */
export interface CrowdWeapon {
  readonly id: string;
  /** The clip it attacks with (`weapon.clip`). */
  readonly clip: string;
  readonly mesh: Mesh;
  readonly triangles: number;
  /** The per-instance buffers, bound once at the layer's capacity: placement, clip A, clip B, fade. */
  readonly targets: readonly [Float32Array, Float32Array, Float32Array, Float32Array];
  /** How many bodies hold it this frame. */
  count: number;
  dispose(): void;
}

/**
 * The matrix taking a vertex of the weapon's own frame into the rig's bind space, for a weapon attached to `joint`.
 *
 * Babylon's matrices are row-vector: `v × A × B` applies A first. So the asset format's
 * `inverse(IBM) × scale(1/s) × translate(pos) × rotate(rot)` reads right to left here.
 *
 * @param inverseBind The rig's inverse bind matrix of the joint.
 * @param pos `attach.pos`, metres in the joint's frame with the character's scale removed.
 * @param rot `attach.rot`, the weapon's frame into the joint's.
 * @param characterScale The pack's uniform scale `s` (`source.scale`), which the joint's matrices carry.
 */
export function weaponBindMatrix(inverseBind: Matrix, pos: Vec3, rot: Quat, characterScale: number): Matrix {
  const grip = Matrix.Compose(Vector3.One(), Quaternion.FromArray(rot), Vector3.FromArray(pos));
  const unscale = Matrix.Scaling(1 / characterScale, 1 / characterScale, 1 / characterScale);
  return grip.multiply(unscale).multiply(inverseBind.clone().invert());
}

/**
 * Builds one weapon's crowd mesh.
 *
 * @param scene The live scene.
 * @param library The open asset library.
 * @param id The weapon's catalogue id (`kind: "weapon"`).
 * @param rig The characters' baked rig, whose joint the weapon is bound to.
 * @param material The characters' crowd material: the same skinning, the same vertex colours.
 * @param characterScale The pack's uniform scale (`source.scale`).
 * @param capacity The most bodies that may hold it at once.
 * @returns The weapon.
 * @throws If it is no weapon, or the rig has no joint it names.
 */
export async function buildCrowdWeapon(
  scene: Scene,
  library: AssetLibrary,
  id: string,
  rig: CrowdRig,
  material: Material,
  characterScale: number,
  capacity: number,
): Promise<CrowdWeapon> {
  const weapon = (await library.describe(id)).weapon;
  if (weapon === undefined) {
    throw new Error(`crowd: "${id}" is no weapon`);
  }

  const joint = rig.boneNames.indexOf(weapon.attach.joint);
  const inverseBind = rig.inverseBind.at(joint);
  if (joint < 0 || inverseBind === undefined) {
    throw new Error(`crowd: the rig has no joint "${weapon.attach.joint}" for ${id}`);
  }

  const bind = weaponBindMatrix(inverseBind, weapon.attach.pos, weapon.attach.rot, characterScale);
  await library.acquire(id);
  const holder = new TransformNode(`crowd-weapon-load:${id}`, scene);
  const instance = await library.instantiate(id, placement([0, 0, 0], 0), holder, `crowd-weapon:${id}`);
  try {
    // The vertices in the weapon's own glTF frame: its glTF root, which turns that frame into Babylon's, made the identity
    // — the rig's matrices already carry the character's.
    for (const node of instance.root.getChildren()) {
      if (node instanceof TransformNode) {
        node.position.setAll(0);
        node.rotationQuaternion = Quaternion.Identity();
        node.scaling.setAll(1);
      }
    }

    return merge(scene, id, weapon.clip, instance.meshes, bind, joint, material, capacity);
  } finally {
    instance.dispose();
    holder.dispose();
  }
}

function merge(
  scene: Scene,
  id: string,
  clip: string,
  meshes: readonly { getTotalVertices(): number }[],
  bind: Matrix,
  joint: number,
  material: Material,
  capacity: number,
): CrowdWeapon {
  const parts = meshes.filter((m): m is Mesh | InstancedMesh => m instanceof Mesh || m instanceof InstancedMesh);
  const positions: number[] = [];
  const normals: number[] = [];
  const colors: number[] = [];
  const indices: number[] = [];
  const v = new Vector3();
  const toBind = new Matrix();
  for (const part of parts) {
    const source = part instanceof InstancedMesh ? part.sourceMesh : part;
    const pos = source.getVerticesData(VertexBuffer.PositionKind, false, true);
    const nor = source.getVerticesData(VertexBuffer.NormalKind, false, true);
    const tri = source.getIndices();
    if (pos === null || tri === null) {
      continue;
    }

    part.computeWorldMatrix(true).multiplyToRef(bind, toBind);
    const pbr = source.material instanceof PBRMaterial ? source.material : null;
    const base = positions.length / 3;
    const n = pos.length / 3;
    for (let i = 0; i < n; i++) {
      Vector3.TransformCoordinatesFromFloatsToRef(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2], toBind, v);
      positions.push(v.x, v.y, v.z);
      Vector3.TransformNormalFromFloatsToRef(
        nor?.[i * 3] ?? 0,
        nor?.[i * 3 + 1] ?? 1,
        nor?.[i * 3 + 2] ?? 0,
        toBind,
        v,
      );
      v.normalize();
      normals.push(v.x, v.y, v.z);
      colors.push(pbr?.albedoColor.r ?? 0.5, pbr?.albedoColor.g ?? 0.5, pbr?.albedoColor.b ?? 0.5, 1);
    }

    for (const i of tri) {
      indices.push(i + base);
    }
  }

  const total = positions.length / 3;
  if (total === 0) {
    throw new Error(`crowd: "${id}" has no geometry`);
  }

  const mesh = new Mesh(`crowd-weapon:${id}`, scene);
  const vd = new VertexData();
  vd.positions = positions;
  vd.normals = normals;
  vd.colors = colors;
  vd.indices = total > 65535 ? new Uint32Array(indices) : new Uint16Array(indices);
  vd.applyToMesh(mesh, false);
  // Every vertex on the hand's joint, alone.
  const bones = new Float32Array(total * 4);
  const weights = new Float32Array(total * 4);
  for (let i = 0; i < total; i++) {
    bones[i * 4] = joint;
    weights[i * 4] = 1;
  }

  mesh.setVerticesData('crowdBones', bones, false, 4);
  mesh.setVerticesData('crowdWeights', weights, false, 4);
  mesh.material = material;
  mesh.renderingGroupId = SCENE_GROUP;
  mesh.alwaysSelectAsActiveMesh = true;
  mesh.doNotSyncBoundingInfo = true;
  mesh.isPickable = false;
  mesh.receiveShadows = true;

  // Bound once at full capacity, for the reason `CrowdLayer` gives: Babylon compiles the instancing path from whether
  // the mesh has thin instances when it first draws, and rebinding per frame rebuilds a vertex buffer per frame.
  const targets: [Float32Array, Float32Array, Float32Array, Float32Array] = [
    new Float32Array(capacity * 16),
    new Float32Array(capacity * 4),
    new Float32Array(capacity * 4),
    new Float32Array(capacity * 2),
  ];
  mesh.thinInstanceSetBuffer('matrix', targets[0], 16, false);
  mesh.thinInstanceSetBuffer('crowdClipA', targets[1], 4, false);
  mesh.thinInstanceSetBuffer('crowdClipB', targets[2], 4, false);
  mesh.thinInstanceSetBuffer('crowdFade', targets[3], 2, false);
  mesh.thinInstanceCount = 0;
  mesh.isVisible = false;

  return {
    id,
    clip,
    mesh,
    triangles: (indices.length / 3) | 0,
    targets,
    count: 0,
    dispose() {
      mesh.dispose(false, false);
    },
  };
}
