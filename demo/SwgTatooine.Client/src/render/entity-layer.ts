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
import { SELECTED_MESH_SCALE, SELECTED_SPRITE_SCALE, type LayerStyle } from './styles';
import {
  packedStyle,
  pickInstances,
  pickShapes,
  projectToScreen,
  type PickHit,
  type PickRay,
  type ScreenPoint,
} from './view-math';

export type { FrameView } from './layer-packer';
export type { PickHit, PickRay } from './view-math';

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
  private yawUploader: PrefixUploader | null = null;
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
        attributes: ['position', 'normal', 'instData', 'instYaw'],
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
    this.yawUploader?.upload(this.nearMesh.getVertexBuffer('instYaw')?.getWrapperBuffer() ?? null, packer.nearCount);
    this.setCounts(packer.nearCount, packer.farCount);
  }

  /** Nearest drawn entity within `maxPixels` of a viewport point (CSS pixels); updates `best` when closer than it. */
  /**
   * Where one packed entity is on screen, by the same projection {@link pick} uses. For diagnosis: a click that misses is
   * either the projection disagreeing with the eye or the pointer never reaching the pick, and these tell them apart.
   */
  screenOf(
    matrix: ArrayLike<number>,
    view: FrameView,
    netId: number,
  ): { x: number; y: number; depth: number; band: 'near' | 'far' } | null {
    const p = this.packer;
    const w = view.viewportWidth;
    const h = view.viewportHeight;
    for (let k = 0; k < this.nearCount; k++) {
      if (p.nearNetIds[k] === netId) {
        const b = k * 4;
        // (x, y, z, packed): the label's height is measured from the entity's own altitude (CLI3D-04).
        const s = projectToScreen(
          matrix,
          p.nearData[b],
          p.nearData[b + 1] + this.style.bounds.pickY[packedStyle(p.nearData[b + 3])],
          p.nearData[b + 2],
          w,
          h,
          this.screen,
        );
        return { x: s.x, y: s.y, depth: s.w, band: 'near' };
      }
    }

    for (let k = 0; k < this.farCount; k++) {
      if (p.farNetIds[k] === netId) {
        const b = k * 4;
        const s = projectToScreen(
          matrix,
          p.farData[b],
          p.farData[b + 1] + this.style.spriteLift,
          p.farData[b + 2],
          w,
          h,
          this.screen,
        );
        return { x: s.x, y: s.y, depth: s.w, band: 'far' };
      }
    }

    return null;
  }

  /**
   * Updates `best` with anything of this layer's under the cursor, or near it when nothing is under it.
   *
   * The near band is tested as the boxes it is DRAWN as, not as points. That is the fix for the defect this had: an
   * 18 m building 60 m away covers about 320 screen pixels and only the 16 around its centre used to be clickable, so
   * clicking its wall or its roof selected nothing. The far band is a fixed-size sprite, so its silhouette is a disc.
   *
   * @param maxT How far along the ray the ground is. Nothing beyond it is on screen, so nothing beyond it is pickable:
   * the GPU gets this from depth testing and the pick has to be told. Without it, clicking a mesa's visible rock face
   * selects whatever is standing behind the mesa.
   */
  pick(
    ray: PickRay,
    matrix: ArrayLike<number>,
    view: FrameView,
    px: number,
    py: number,
    maxPixels: number,
    best: PickHit,
    maxT: number,
  ): void {
    if (!this.visible) {
      return;
    }

    const p = this.packer;
    const w = view.viewportWidth;
    const h = view.viewportHeight;
    const a = this.archetype;
    const s = this.screen;
    pickShapes(
      ray,
      p.nearData,
      p.nearYaw,
      p.nearNetIds,
      this.nearCount,
      this.style.bounds.sizesFlat,
      this.style.flattenMode,
      SELECTED_MESH_SCALE,
      a,
      best,
      maxT,
    );
    // The near band's silhouette is settled above; 0 leaves this as the near-miss fallback for a mesh only a few pixels
    // across, which is a fiddly target to hit exactly at the band boundary.
    pickInstances(
      ray,
      matrix,
      p.nearData,
      p.nearNetIds,
      this.nearCount,
      this.style.bounds.pickY,
      0,
      SELECTED_MESH_SCALE,
      w,
      h,
      px,
      py,
      maxPixels,
      a,
      best,
      s,
      maxT,
    );
    pickInstances(
      ray,
      matrix,
      p.farData,
      p.farNetIds,
      this.farCount,
      this.style.spriteLift,
      this.style.spritePixels * 0.5,
      SELECTED_SPRITE_SCALE,
      w,
      h,
      px,
      py,
      maxPixels,
      a,
      best,
      s,
      maxT,
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
    this.yawUploader = new PrefixUploader(packer.nearYaw, 1);
    this.nearMesh.thinInstanceSetBuffer('instData', null);
    this.nearMesh.thinInstanceSetBuffer('instData', packer.nearData, 4, false);
    // Only the mesh band turns, so only it carries a yaw (CLI3D-04): instData's four slots went to (x, y, z, packed).
    this.nearMesh.thinInstanceSetBuffer('instYaw', null);
    this.nearMesh.thinInstanceSetBuffer('instYaw', packer.nearYaw, 1, false);
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
