import '@babylonjs/core/Meshes/thinInstanceMesh';
import { ShaderMaterial } from '@babylonjs/core/Materials/shaderMaterial';
import { Vector2, Vector3 } from '@babylonjs/core/Maths/math.vector';
import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import type { Scene } from '@babylonjs/core/scene';
import type { ArchetypeStore } from '@typhondb/client';
import { LayerPacker, type FrameView } from './layer-packer';
import { createShapeMesh } from './meshes';
import { PrefixUploader } from './prefix-upload';
import { SCENE_GROUP } from './render-groups';
import { ENTITY_FRAGMENT, ENTITY_VERTEX, SPRITE_FRAGMENT, SPRITE_VERTEX } from './shaders';
import type { LayerStyle } from './styles';
import { pickInstances, type PickHit, type ScreenPoint } from './view-math';

export type { FrameView } from './layer-packer';
export type { PickHit } from './view-math';

const SUN = new Vector3(-0.45, -0.8, -0.4).normalize();

/**
 * One archetype on screen: the {@link LayerPacker} evaluates, culls, bands and packs every live entity on the CPU; this uploads
 * the two instance arrays and draws them — a lit mesh for the near band, a sprite for the far one. Nothing per entity lives on
 * the GPU between frames (`05-client.md` § 4, as amended: the god view holds thousands of entities, not hundreds of
 * thousands).
 */
export class EntityLayer {
  readonly archetype: number;
  readonly style: LayerStyle;
  visible = true;

  private readonly packer: LayerPacker;
  private readonly nearMesh: Mesh;
  private readonly farMesh: Mesh;
  private readonly nearMaterial: ShaderMaterial;
  private readonly farMaterial: ShaderMaterial;
  private readonly viewport = new Vector2(1, 1);
  private nearUploader: PrefixUploader | null = null;
  private farUploader: PrefixUploader | null = null;
  /** The packer's arrays the GPU buffers were created over. */
  private arraysVersion = -1;
  private readonly screen: ScreenPoint = { x: 0, y: 0, w: 0 };

  constructor(archetype: number, style: LayerStyle, scene: Scene) {
    this.archetype = archetype;
    this.style = style;
    this.packer = new LayerPacker(archetype, style);
    this.nearMesh = createShapeMesh(`near-${archetype}`, style.shape, scene);
    this.farMesh = createShapeMesh(`far-${archetype}`, 'quad', scene);
    this.nearMesh.renderingGroupId = SCENE_GROUP;
    this.farMesh.renderingGroupId = SCENE_GROUP;

    this.nearMaterial = new ShaderMaterial(
      `near-${archetype}`,
      scene,
      { vertexSource: ENTITY_VERTEX, fragmentSource: ENTITY_FRAGMENT },
      {
        attributes: ['position', 'normal', 'instData'],
        uniforms: ['viewProjection', 'uColors', 'uSizes', 'uTints', 'uFlattenMode', 'uSunDirection'],
      },
    );
    this.nearMaterial.setArray3('uColors', style.colors.flat());
    this.nearMaterial.setArray3('uSizes', style.sizes.flat());
    this.nearMaterial.setArray4('uTints', style.tints.flat());
    this.nearMaterial.setFloat('uFlattenMode', style.flattenMode);
    this.nearMaterial.setVector3('uSunDirection', SUN);
    this.nearMesh.material = this.nearMaterial;

    this.farMaterial = new ShaderMaterial(
      `far-${archetype}`,
      scene,
      { vertexSource: SPRITE_VERTEX, fragmentSource: SPRITE_FRAGMENT },
      {
        attributes: ['position', 'instData'],
        uniforms: ['viewProjection', 'uViewport', 'uColors', 'uTints', 'uPixels', 'uLift'],
      },
    );
    this.farMaterial.backFaceCulling = false;
    this.farMaterial.setArray3('uColors', style.colors.flat());
    this.farMaterial.setArray4('uTints', style.tints.flat());
    this.farMaterial.setFloat('uPixels', style.spritePixels);
    this.farMaterial.setFloat('uLift', style.spriteLift);
    this.farMesh.material = this.farMaterial;

    this.syncBuffers();
    this.setCounts(0, 0);
  }

  get nearCount(): number {
    return this.nearMesh.isVisible ? this.packer.nearCount : 0;
  }

  get farCount(): number {
    return this.farMesh.isVisible ? this.packer.farCount : 0;
  }

  /** Binds the layer to a store: per-slot state starts over. */
  bind(store: ArchetypeStore): void {
    this.packer.bind(store);
    this.setCounts(0, 0);
  }

  dispose(): void {
    this.nearMesh.dispose();
    this.farMesh.dispose();
    this.nearMaterial.dispose();
    this.farMaterial.dispose();
  }

  /** Packs every live entity, then uploads both instance buffers. */
  update(view: FrameView): void {
    if (!this.visible || this.packer.bound === null) {
      this.setCounts(0, 0);
      return;
    }

    const packer = this.packer;
    packer.pack(view);
    this.syncBuffers();
    this.viewport.set(view.viewportWidth, view.viewportHeight);
    this.farMaterial.setVector2('uViewport', this.viewport);
    this.nearUploader?.upload(this.nearMesh.getVertexBuffer('instData')?.getWrapperBuffer() ?? null, packer.nearCount);
    this.farUploader?.upload(this.farMesh.getVertexBuffer('instData')?.getWrapperBuffer() ?? null, packer.farCount);
    this.setCounts(packer.nearCount, packer.farCount);
  }

  /** Nearest drawn entity within `maxPixels` of a viewport point (CSS pixels); updates `best` when closer than it. */
  pick(matrix: ArrayLike<number>, view: FrameView, px: number, py: number, maxPixels: number, best: PickHit): void {
    if (!this.visible) {
      return;
    }

    const p = this.packer;
    const w = view.viewportWidth;
    const h = view.viewportHeight;
    const a = this.archetype;
    const s = this.screen;
    pickInstances(
      matrix,
      p.nearData,
      p.nearNetIds,
      this.nearCount,
      this.style.bounds.pickY,
      w,
      h,
      px,
      py,
      maxPixels,
      a,
      best,
      s,
    );
    pickInstances(
      matrix,
      p.farData,
      p.farNetIds,
      this.farCount,
      this.style.spriteLift,
      w,
      h,
      px,
      py,
      maxPixels,
      a,
      best,
      s,
    );
  }

  /** Re-creates the GPU instance buffers over the packer's arrays when it replaced them. */
  private syncBuffers(): void {
    const packer = this.packer;
    if (packer.arraysVersion === this.arraysVersion) {
      return;
    }

    this.arraysVersion = packer.arraysVersion;
    this.nearUploader = new PrefixUploader(packer.nearData, 4);
    this.farUploader = new PrefixUploader(packer.farData, 4);
    this.nearMesh.thinInstanceSetBuffer('instData', null);
    this.nearMesh.thinInstanceSetBuffer('instData', packer.nearData, 4, false);
    this.farMesh.thinInstanceSetBuffer('instData', null);
    this.farMesh.thinInstanceSetBuffer('instData', packer.farData, 4, false);
  }

  private setCounts(near: number, far: number): void {
    // Babylon draws a thin-instanced mesh with `forcedInstanceCount` instances; 0 would fall back to a plain draw.
    this.nearMesh.forcedInstanceCount = near;
    this.nearMesh.isVisible = near > 0;
    this.farMesh.forcedInstanceCount = far;
    this.farMesh.isVisible = far > 0;
  }
}
