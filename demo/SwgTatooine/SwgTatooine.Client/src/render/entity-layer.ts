import '@babylonjs/core/Meshes/thinInstanceMesh';
import { ShaderMaterial } from '@babylonjs/core/Materials/shaderMaterial';
import { Vector2 } from '@babylonjs/core/Maths/math.vector';
import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import type { Scene } from '@babylonjs/core/scene';
import type { ArchetypeStore } from '@typhondb/client';
import { LayerPacker, type FrameView } from './layer-packer';
import { createShapeMesh } from './meshes';
import { PrefixUploader } from './prefix-upload';
import { SCENE_GROUP } from './render-groups';
import { ENTITY_FRAGMENT, ENTITY_VERTEX, SPRITE_FRAGMENT, SPRITE_VERTEX } from './shaders';
import type { LayerStyle } from './styles';
import { SUN_DIRECTION } from './sun';
import {
  packedStyle,
  pickInstances,
  pickShapes,
  projectToScreen,
  type PickHit,
  type PickRay,
  type ScreenPoint,
} from './view-math';

export type { FrameView, PredictedOverride } from './layer-packer';
export type { PickHit, PickRay } from './view-math';

const SUN = SUN_DIRECTION;

/** Where an entity was drawn this frame, and as what box: see {@link EntityLayer.instanceOf}. */
export interface DrawnInstance {
  /** Render space: planet metres less the render origin. */
  x: number;
  y: number;
  z: number;
  yaw: number;
  /** The style's box, metres — or, for a sprite, its diameter in CSS pixels in `width` and `height`. */
  width: number;
  height: number;
  length: number;
  /** Drawn as the far band's fixed-size sprite. */
  far: boolean;
}

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
  /**
   * Whether this layer's NEAR band is drawn by the crowd as rigged characters instead of as boxes.
   *
   * The far band is unaffected either way: below the near threshold a body is a sprite, which is where the crowd's
   * own coarsest tier stops too.
   */
  get drawsCharacters(): boolean {
    return this.packer.drawsCharacters;
  }

  set drawsCharacters(value: boolean) {
    this.packer.drawsCharacters = value;
  }

  /** The packer's arrays the GPU buffers were created over. */
  private arraysVersion = -1;
  private readonly screen: ScreenPoint = { x: 0, y: 0, w: 0 };

  /**
   * @param archetype Which archetype's store this layer draws.
   * @param style Its art.
   * @param scene The Babylon scene.
   * @param onlyStyle Draw only this style index, or -1 for all; see {@link LayerPacker}.
   * @param omitStyle Skip this style index, or -1 for none.
   */
  constructor(archetype: number, style: LayerStyle, scene: Scene, onlyStyle = -1, omitStyle = -1) {
    this.archetype = archetype;
    this.style = style;
    this.packer = new LayerPacker(archetype, style, onlyStyle, omitStyle);
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

  /** The bodies the crowd drew for this layer; pickable whenever the layer is visible. */
  get heldCount(): number {
    return this.visible ? this.packer.heldCount : 0;
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

  /**
   * Where one entity was drawn this frame and as what: its render-space position and yaw, and its style's box — what the
   * pick tests it as. The far band is a sprite with no yaw; `far` says so, and its frame is the sprite's square.
   *
   * @returns False when this layer packed no such netId this frame.
   */
  instanceOf(netId: number, out: DrawnInstance): boolean {
    const p = this.packer;
    if (
      this.findMesh(p.nearData, p.nearYaw, p.nearNetIds, this.nearCount, netId, out) ||
      this.findMesh(p.heldData, p.heldYaw, p.heldNetIds, this.heldCount, netId, out)
    ) {
      return true;
    }

    for (let k = 0; k < this.farCount; k++) {
      if (p.farNetIds[k] === netId) {
        const b = k * 4;
        out.x = p.farData[b];
        out.y = p.farData[b + 1] + this.style.spriteLift;
        out.z = p.farData[b + 2];
        out.yaw = 0;
        out.width = this.style.spritePixels;
        out.height = this.style.spritePixels;
        out.length = 0;
        out.far = true;
        return true;
      }
    }

    return false;
  }

  /** {@link instanceOf} over one band drawn as meshes: the near band, or the bodies the crowd drew in its place. */
  private findMesh(
    data: Float32Array,
    yaws: Float32Array,
    netIds: Uint32Array,
    count: number,
    netId: number,
    out: DrawnInstance,
  ): boolean {
    const sizes = this.style.bounds.sizesFlat;
    for (let k = 0; k < count; k++) {
      if (netIds[k] === netId) {
        const b = k * 4;
        const style = packedStyle(data[b + 3]);
        out.x = data[b];
        out.y = data[b + 1];
        out.z = data[b + 2];
        out.yaw = yaws[k];
        out.width = sizes[style * 3];
        out.height = sizes[style * 3 + 1];
        out.length = sizes[style * 3 + 2];
        out.far = false;
        return true;
      }
    }

    return false;
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

    for (let k = 0; k < this.heldCount; k++) {
      if (p.heldNetIds[k] === netId) {
        const b = k * 4;
        const s = projectToScreen(
          matrix,
          p.heldData[b],
          p.heldData[b + 1] + this.style.bounds.pickY[packedStyle(p.heldData[b + 3])],
          p.heldData[b + 2],
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
      a,
      best,
      maxT,
    );
    // The bodies the crowd drew in this layer's place, tested as the boxes this layer WOULD have drawn: close enough to
    // the character standing there, and the same hit test whichever renderer drew it.
    pickShapes(
      ray,
      p.heldData,
      p.heldYaw,
      p.heldNetIds,
      this.heldCount,
      this.style.bounds.sizesFlat,
      this.style.flattenMode,
      a,
      best,
      maxT,
    );
    pickInstances(
      ray,
      matrix,
      p.heldData,
      p.heldNetIds,
      this.heldCount,
      this.style.bounds.pickY,
      0,
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
