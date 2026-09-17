import { Texture } from '@babylonjs/core/Materials/Textures/texture';
import { RawTexture } from '@babylonjs/core/Materials/Textures/rawTexture';
import { ShaderMaterial } from '@babylonjs/core/Materials/shaderMaterial';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import { Vector2, Vector3, Vector4 } from '@babylonjs/core/Maths/math.vector';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import type { Scene } from '@babylonjs/core/scene';
import type { AggregateGrid } from '@typhondb/client';
import { CITIES, PLANET_HALF_EXTENT_M, POIS } from '../data/world-data';
import { fillHeatmap } from './heatmap-fill';
import { GROUND_GROUP } from './render-groups';
import { GROUND_FRAGMENT, GROUND_VERTEX } from './shaders';

/** What the ground draws this frame; the caller keeps one and rewrites it, so a frame allocates nothing. */
export class GroundView {
  originX = 0;
  originZ = 0;
  /** Camera position in render space, for fog. */
  eyeX = 0;
  eyeY = 0;
  eyeZ = 0;
  /** Centre and radius of the near tier, in planet coordinates: the heatmap fades in beyond it. */
  viewCenterX = 0;
  viewCenterZ = 0;
  nearRadius = 0;
  /** Which archetype slots of the grid the heatmap shows (up to four). */
  heatMask: readonly boolean[] = [];
  showGrid = false;
  /** Selected entity for the radius rings, at (selectionX, selectionZ) when `hasSelection`. */
  hasSelection = false;
  selectionX = 0;
  selectionZ = 0;
}

export const SKY = new Color3(0.83, 0.78, 0.7);

/**
 * The planet floor: one quad, everything drawn in its fragment shader — sand, city and landmark discs, the 256 m / 1 024 m
 * grid, selection radii and the far-tier heatmap — so no overlay is coplanar geometry and nothing z-fights at altitude
 * (`05-client.md` § 6).
 *
 * It draws in rendering group 0, everything else in group 1, and Babylon clears depth between groups: the ground never
 * hides anything. That is exact, not a trick — the camera and every entity are above the plane, so no segment between
 * them crosses it — and it spares sprites and lines, which are flat at the depth of their centre, a depth bias.
 */
export class Ground {
  private readonly mesh: Mesh;
  private readonly material: ShaderMaterial;
  private readonly scene: Scene;
  private heat: HeatmapTexture;
  private readonly camera = new Vector3();
  private readonly center = new Vector2();
  private readonly mask = new Vector4();
  private readonly selection = new Vector4();
  private readonly heatRect = new Vector4();

  constructor(scene: Scene) {
    this.scene = scene;
    const extent = PLANET_HALF_EXTENT_M * 1.6;
    const mesh = new Mesh('ground', scene);
    const data = new VertexData();
    data.positions = [-extent, 0, -extent, -extent, 0, extent, extent, 0, extent, extent, 0, -extent];
    data.indices = [0, 1, 2, 0, 2, 3];
    data.applyToMesh(mesh, false);
    mesh.alwaysSelectAsActiveMesh = true;
    mesh.isPickable = false;
    mesh.renderingGroupId = GROUND_GROUP;
    this.mesh = mesh;

    this.heat = new HeatmapTexture(scene);
    const material = new ShaderMaterial(
      'ground',
      scene,
      { vertexSource: GROUND_VERTEX, fragmentSource: GROUND_FRAGMENT },
      {
        attributes: ['position'],
        uniforms: [
          'world',
          'viewProjection',
          'uCamera',
          'uCities',
          'uPois',
          'uHeatMask',
          'uHeatRect',
          'uHeatNear',
          'uViewCenter',
          'uHeatAlpha',
          'uGrid',
          'uSelection',
          'uSkyColor',
        ],
        samplers: ['uHeat'],
      },
    );
    material.backFaceCulling = false;
    material.setArray4(
      'uCities',
      CITIES.flatMap((c) => [c.x, c.z, c.radius, 0]),
    );
    material.setArray4(
      'uPois',
      POIS.flatMap((p) => [p.x, p.z, p.radius, 0]),
    );
    material.setTexture('uHeat', this.heat.texture);
    material.setColor3('uSkyColor', SKY);
    mesh.material = material;
    this.material = material;
  }

  update(view: GroundView, grid: AggregateGrid): void {
    this.mesh.position.set(-view.originX, 0, -view.originZ);
    this.camera.set(view.eyeX, view.eyeY, view.eyeZ);
    this.center.set(view.viewCenterX, view.viewCenterZ);
    this.mask.set(
      view.heatMask[0] ? 1 : 0,
      view.heatMask[1] ? 1 : 0,
      view.heatMask[2] ? 1 : 0,
      view.heatMask[3] ? 1 : 0,
    );
    this.selection.set(view.selectionX, view.selectionZ, view.hasSelection ? 1 : 0, 0);
    const { origin, cell, dims } = grid.schema;
    this.heatRect.set(origin[0], origin[1], dims[0] * cell, dims[1] * cell);
    if (this.heat.width !== dims[0] || this.heat.height !== dims[1]) {
      // One texel per cell: the shader maps the grid's extent onto the whole texture.
      this.heat.dispose();
      this.heat = new HeatmapTexture(this.scene, dims[0], dims[1]);
      this.material.setTexture('uHeat', this.heat.texture);
    }

    this.heat.update(grid);
    let anyHeat = false;
    const mask = view.heatMask;
    for (let i = 0; i < mask.length; i++) {
      anyHeat ||= mask[i];
    }

    const m = this.material;
    m.setVector3('uCamera', this.camera);
    m.setVector2('uViewCenter', this.center);
    m.setFloat('uHeatNear', view.nearRadius);
    m.setVector4('uHeatMask', this.mask);
    m.setVector4('uHeatRect', this.heatRect);
    m.setFloat('uHeatAlpha', anyHeat ? 1 : 0);
    m.setFloat('uGrid', view.showGrid ? 1 : 0);
    m.setVector4('uSelection', this.selection);
  }

  dispose(): void {
    this.heat.dispose();
    this.material.dispose();
    this.mesh.dispose();
  }
}

/**
 * The far-tier counts as an RGBA8 texture, one channel per counted archetype, each normalised on a log scale to its own
 * maximum so a sparse archetype stays visible beside a dense one. Rebuilt whole when the grid's version moves: 64 × 64
 * texels is a 16 KB upload, at most once a second.
 */
export class HeatmapTexture {
  readonly texture: RawTexture;
  readonly width: number;
  readonly height: number;
  private readonly data: Uint8Array;
  private version = -1;

  constructor(scene: Scene, width = 64, height = 64) {
    this.width = width;
    this.height = height;
    this.data = new Uint8Array(width * height * 4);
    this.texture = RawTexture.CreateRGBATexture(
      this.data,
      width,
      height,
      scene,
      false,
      false,
      Texture.BILINEAR_SAMPLINGMODE,
    );
    this.texture.wrapU = Texture.CLAMP_ADDRESSMODE;
    this.texture.wrapV = Texture.CLAMP_ADDRESSMODE;
  }

  update(grid: AggregateGrid): void {
    if (grid.version === this.version) {
      return;
    }

    this.version = grid.version;
    fillHeatmap(grid, this.data, this.width, this.height);
    this.texture.update(this.data);
  }

  dispose(): void {
    this.texture.dispose();
  }
}
