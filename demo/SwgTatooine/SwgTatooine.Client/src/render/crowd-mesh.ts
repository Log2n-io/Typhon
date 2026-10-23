import { VertexBuffer } from '@babylonjs/core/Buffers/buffer';
import type { Material } from '@babylonjs/core/Materials/material';
import { PBRMaterial } from '@babylonjs/core/Materials/PBR/pbrMaterial';
import { Vector3, type Matrix } from '@babylonjs/core/Maths/math.vector';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import '@babylonjs/core/Meshes/thinInstanceMesh';
import type { Scene } from '@babylonjs/core/scene';

import type { Instance } from '../assets/asset-library';
import type { LodDesc } from '../assets/descriptor';
import { lodLevelOf } from './asset-lod';
import { SCENE_GROUP } from './render-groups';
import { rigOffset, skinnedPrimitives, type CrowdRig } from './crowd-rig';

/**
 * One outfit as crowd meshes: its primitives merged into one per detail level, each primitive's flat base colour
 * turned into a vertex colour, and its skin rebased onto the shared rig so the one atlas animates it.
 *
 * <b>Rebasing.</b> The outfit's inverse bind matrices are `Q × IBM_rig` (see {@link rigOffset}), so a vertex `v`
 * skinned by this outfit equals `v·Q` skinned by the rig. Positions go through `Q` once, here, and normals through its
 * linear part. The mesh has no skeleton: bone indices and weights are plain attributes the crowd material reads.
 *
 * <b>Vertex colours, because the pack has no textures at all</b> — no images, no samplers, every material a flat
 * `baseColorFactor`. That is what makes merging primitives free: there is nothing per-primitive left to vary. It is
 * also a one-way door, and worth knowing before someone asks for a textured character: nothing merges UVs here, so
 * adding a texture later is a vertex-format change, not a material swap.
 */
export interface CrowdLod {
  readonly mesh: Mesh;
  readonly triangles: number;
}

export interface CrowdOutfit {
  readonly id: string;
  /** Level 0 first, then each coarser level the asset carries. */
  readonly lods: readonly CrowdLod[];
  /** The body's height in metres, from its own bounds — never a constant shared across outfits. */
  readonly height: number;
  dispose(): void;
}

/**
 * Merges one outfit into one mesh per detail level.
 *
 * @param scene The live scene.
 * @param instance The instantiated outfit.
 * @param rig The baked atlas, for the skin rebasing.
 * @param material The shared crowd material.
 * @param height The body's height, from its descriptor's render bounds.
 * @param name A label for the meshes.
 * @param declared What the descriptor says the levels are, so the name-based split has a second source to agree with.
 * @returns The outfit's levels, finest first.
 * @throws If the levels are not contiguous from 0, if they disagree with the descriptor, if a primitive carries
 * more than four influences, or if a primitive has a texture — all of which would otherwise be lost in silence.
 */
export function buildCrowdOutfit(
  scene: Scene,
  instance: Instance,
  rig: CrowdRig,
  material: Material,
  height: number,
  name = instance.id,
  declared: readonly LodDesc[] | null = null,
): CrowdOutfit {
  const parts = skinnedPrimitives(instance);
  const skeleton = parts.at(0)?.skeleton ?? null;
  if (skeleton === null || parts.some((p) => p.skeleton !== skeleton)) {
    throw new Error(`crowd: ${name} needs skinned primitives sharing one skeleton`);
  }

  const q = rigOffset(rig, skeleton);
  const levels = new Map<number, Mesh[]>();
  for (const p of parts) {
    const level = lodLevelOf(p.name);
    levels.set(level, [...(levels.get(level) ?? []), p]);
  }

  // <b>A gap is an error, not a shorter ladder.</b> The spike stopped at the first missing level, so an outfit with
  // LOD0 and LOD2 but no LOD1 silently lost LOD2 and drew its finest mesh at every distance. Latent today because all
  // nineteen are contiguous — which is exactly the kind of thing that stays latent until an artist publishes one that
  // is not.
  const highest = Math.max(...levels.keys());
  for (let level = 0; level <= highest; level++) {
    if (!levels.has(level)) {
      throw new Error(`crowd: ${name} has LOD${String(highest)} but no LOD${String(level)}`);
    }
  }

  // <b>Against what the descriptor DECLARES, because the split above is by NAME.</b> `lodLevelOf` answers 0 for any
  // name it does not recognise, so an outfit published with other node names merges all three of its levels into one
  // mesh: three times the triangles, z-fighting, no throw — and the gap check above sees a perfectly contiguous
  // ladder of one. This file otherwise filters primitives by what they ARE and never by what they are called
  // (`crowd-rig.ts`); the names are unavoidable here, so the count is checked against a second source.
  if (declared !== null && declared.length !== highest + 1) {
    throw new Error(
      `crowd: ${name} declares ${String(declared.length)} detail levels and its meshes name ${String(highest + 1)}`,
    );
  }

  const lods: CrowdLod[] = [];
  for (let level = 0; level <= highest; level++) {
    lods.push(mergeLevel(scene, levels.get(level) ?? [], q, material, `crowd:${name}:LOD${String(level)}`));
  }

  return {
    id: name,
    lods,
    height,
    dispose() {
      for (const l of lods) {
        l.mesh.dispose(false, false);
      }
    },
  };
}

function mergeLevel(scene: Scene, parts: readonly Mesh[], q: Matrix, material: Material, name: string): CrowdLod {
  const total = parts.reduce((n, p) => n + p.getTotalVertices(), 0);
  const positions = new Float32Array(total * 3);
  const normals = new Float32Array(total * 3);
  const bones = new Float32Array(total * 4);
  const weights = new Float32Array(total * 4);
  const colors = new Float32Array(total * 4);
  const indices: number[] = [];
  const v = new Vector3();
  let base = 0;

  for (const p of parts) {
    const n = p.getTotalVertices();
    const pos = need(p.getVerticesData(VertexBuffer.PositionKind, false, true), p, 'position');
    const nor = need(p.getVerticesData(VertexBuffer.NormalKind, false, true), p, 'normal');
    const mi = need(p.getVerticesData(VertexBuffer.MatricesIndicesKind, false, true), p, 'matricesIndices');
    const mw = need(p.getVerticesData(VertexBuffer.MatricesWeightsKind, false, true), p, 'matricesWeights');
    if (p.isVerticesDataPresent(VertexBuffer.MatricesIndicesExtraKind)) {
      throw new Error(`crowd: ${name} has more than 4 influences per vertex`);
    }

    const pbr = p.material instanceof PBRMaterial ? p.material : null;
    // A textured outfit would lose its texture here without a word. The pack has none; say so if that changes.
    if (pbr?.albedoTexture != null) {
      throw new Error(`crowd: ${name} has a texture, which the merged crowd mesh carries no UVs for`);
    }

    const albedo = pbr?.albedoColor ?? null;
    for (let i = 0; i < n; i++) {
      const o = (base + i) * 3;
      Vector3.TransformCoordinatesFromFloatsToRef(pos[i * 3] ?? 0, pos[i * 3 + 1] ?? 0, pos[i * 3 + 2] ?? 0, q, v);
      positions[o] = v.x;
      positions[o + 1] = v.y;
      positions[o + 2] = v.z;
      Vector3.TransformNormalFromFloatsToRef(nor[i * 3] ?? 0, nor[i * 3 + 1] ?? 0, nor[i * 3 + 2] ?? 1, q, v);
      v.normalize();
      normals[o] = v.x;
      normals[o + 1] = v.y;
      normals[o + 2] = v.z;

      const c = (base + i) * 4;
      for (let k = 0; k < 4; k++) {
        bones[c + k] = mi[i * 4 + k] ?? 0;
        weights[c + k] = mw[i * 4 + k] ?? 0;
      }

      // Linear already: glTF's baseColorFactor is defined in linear space and Babylon multiplies surfaceAlbedo by
      // vColor with no conversion. See `createCrowdMaterial` for why this differs from the terrain's albedo.
      colors[c] = albedo?.r ?? 1;
      colors[c + 1] = albedo?.g ?? 1;
      colors[c + 2] = albedo?.b ?? 1;
      colors[c + 3] = 1;
    }

    for (const i of need(p.getIndices(), p, 'indices')) {
      indices.push(i + base);
    }

    base += n;
  }

  const mesh = new Mesh(name, scene);
  const vd = new VertexData();
  vd.positions = positions;
  vd.normals = normals;
  vd.colors = colors;
  vd.indices = total > 65535 ? new Uint32Array(indices) : new Uint16Array(indices);
  vd.applyToMesh(mesh, false);
  mesh.setVerticesData('crowdBones', bones, false, 4);
  mesh.setVerticesData('crowdWeights', weights, false, 4);
  mesh.material = material;
  // One group, shared depth: the client's own rule since the terrain gained relief.
  mesh.renderingGroupId = SCENE_GROUP;
  // Every instance shares one bounding volume, the bind pose's, so Babylon's culling means nothing here — the crowd
  // culls its own bodies against the frustum on the CPU.
  mesh.alwaysSelectAsActiveMesh = true;
  mesh.doNotSyncBoundingInfo = true;
  mesh.isPickable = false;
  // Receiving is the drawn mesh's own property. Casting is marked by the layer, which knows what is on screen.
  mesh.receiveShadows = true;
  return { mesh, triangles: (indices.length / 3) | 0 };
}

function need<T>(value: T | null, mesh: Mesh, what: string): T {
  if (value === null) {
    throw new Error(`crowd: ${mesh.name} has no ${what}`);
  }

  return value;
}
