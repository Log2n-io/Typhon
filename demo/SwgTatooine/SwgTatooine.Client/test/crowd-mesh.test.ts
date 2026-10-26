import { describe, expect, it } from 'vitest';
import { AssetContainer } from '@babylonjs/core/assetContainer';
import { Bone } from '@babylonjs/core/Bones/bone';
import { Skeleton } from '@babylonjs/core/Bones/skeleton';
import { VertexBuffer } from '@babylonjs/core/Buffers/buffer';
import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { PBRMaterial } from '@babylonjs/core/Materials/PBR/pbrMaterial';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { Matrix } from '@babylonjs/core/Maths/math.vector';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import { Scene } from '@babylonjs/core/scene';
import { buildCrowdOutfit } from '../src/render/crowd-mesh';
import type { CrowdRig } from '../src/render/crowd-rig';

/**
 * An outfit is merged from its asset's CONTAINER, which is never in the scene: instantiating a skinned outfit cloned its
 * meshes, its skeleton and every animation group only for the merge to read them once (2026-10-26).
 */

/** A one-triangle skinned body on a one-bone skeleton, moved into a container as the glTF loader leaves an asset. */
function outfitContainer(scene: Scene): AssetContainer {
  const skeleton = new Skeleton('armature', 'armature', scene);
  new Bone('root', skeleton, null, Matrix.Identity());
  const body = new Mesh('Body_LOD0', scene);
  const data = new VertexData();
  data.positions = [0, 0, 0, 1, 0, 0, 0, 1, 0];
  data.normals = [0, 0, 1, 0, 0, 1, 0, 0, 1];
  data.indices = [0, 1, 2];
  data.applyToMesh(body);
  body.setVerticesData(VertexBuffer.MatricesIndicesKind, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], false, 4);
  body.setVerticesData(VertexBuffer.MatricesWeightsKind, [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0], false, 4);
  body.skeleton = skeleton;
  const cloth = new PBRMaterial('cloth', scene);
  cloth.albedoColor = new Color3(0.5, 0.25, 0.125);
  body.material = cloth;
  const container = new AssetContainer(scene);
  container.moveAllFromScene();
  return container;
}

describe('buildCrowdOutfit, from a container', () => {
  it("merges the container's own meshes into one in the scene, and leaves the container as it was", () => {
    const scene = new Scene(new NullEngine());
    const container = outfitContainer(scene);
    const rig = { boneNames: ['root'], inverseBind: [Matrix.Identity()] } as unknown as CrowdRig;

    const outfit = buildCrowdOutfit(
      scene,
      { id: 'casual', meshes: container.meshes },
      rig,
      new StandardMaterial('crowd', scene),
      1.8,
    );

    expect(outfit.lods).toHaveLength(1);
    const merged = outfit.lods[0].mesh;
    expect(scene.meshes).toEqual([merged]);
    expect(merged.getTotalVertices()).toBe(3);
    expect(outfit.lods[0].triangles).toBe(1);
    expect(Array.from(merged.getVerticesData(VertexBuffer.ColorKind)!.slice(0, 4))).toEqual([0.5, 0.25, 0.125, 1]);
    expect(Array.from(merged.getVerticesData('crowdWeights')!.slice(0, 4))).toEqual([1, 0, 0, 0]);
    expect(scene.skeletons, 'nothing cloned: the container keeps the only skeleton').toHaveLength(0);
    expect(container.meshes[0].isDisposed()).toBe(false);
    expect(container.skeletons).toHaveLength(1);
  });
});
