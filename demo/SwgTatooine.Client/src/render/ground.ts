import { Texture } from '@babylonjs/core/Materials/Textures/texture';
import { RawTexture } from '@babylonjs/core/Materials/Textures/rawTexture';
import { Color3 } from '@babylonjs/core/Maths/math.color';
import type { PBRMaterial } from '@babylonjs/core/Materials/PBR/pbrMaterial';
import { Vector2, Vector3, Vector4 } from '@babylonjs/core/Maths/math.vector';
import { Scene } from '@babylonjs/core/scene';
import type { AggregateGrid, DebugGrid, PushGeometry } from '@typhondb/client';
import { PushShape } from '@typhondb/client';
import type { DebugView } from '../data/source';
import type { Heightfield } from '../terrain/heightfield';
import { CITIES, POIS } from '../data/world-data';
import { CELL_TEXTURE_SIZE, fillDeliveredCells, hullUniform, type CellRect } from './debug-cells';
import { fillHeatmap, slabAt } from './heatmap-fill';
import { SCENE_GROUP } from './render-groups';
import { HULL_SLOTS } from './shaders';
import { TerrainMesh, TerrainView } from './terrain-mesh';
import type { SceneLighting } from './scene-lighting';
import { createTerrainMaterial, type TerrainMaterialPlugin } from './terrain-material';

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
  /** Draw the `DEBUG` block: the replication grid, the delivered cells and the session's shape (CLI3D-03). */
  showDebug = false;
  /** Where the clipmap centres, in planet coordinates: the active camera's own ground target. */
  centerX = 0;
  centerZ = 0;
  /** Screen pixels per metre at one metre, for the terrain's screen-space error budget. */
  pixelsPerMetre = 1000;
  /** How many pixels of terrain geometric error the viewer accepts. */
  pixelTolerance = 8;

}


/**
 * A planet's ground and sky, keyed by the `palette` field of its `AppTag`.
 *
 * <b>Every planet is the same terrain.</b> One bake serves them all (decision 3), and the server populates every planet
 * from one map — `TatooineMap.Build` is called once and every planet gets the same cities in the same places — so the
 * colour is not a decoration on top of a difference, it IS the difference. Two planets with the same relief and the same
 * towns are told apart by being sand and being green.
 *
 * Index 0 is Tatooine's, unchanged: the three literals the ground shader used to carry.
 */
export interface GroundPalette {
  /** The flat ground. */
  readonly base: Color3;
  /** Steep faces — what a slope reveals under the surface. */
  readonly rock: Color3;
  /** High ground, mixed in above about 12 m. */
  readonly peak: Color3;
  readonly sky: Color3;
}

export const PALETTES: readonly GroundPalette[] = [
  // 0 — desert. The colours this client shipped with.
  {
    base: new Color3(0.78, 0.66, 0.47),
    rock: new Color3(0.54, 0.47, 0.39),
    peak: new Color3(0.86, 0.8, 0.68),
    sky: new Color3(0.83, 0.78, 0.7),
  },
  // 1 — temperate. Green ground over grey stone, under a cooler sky.
  {
    base: new Color3(0.34, 0.45, 0.26),
    rock: new Color3(0.45, 0.44, 0.4),
    peak: new Color3(0.62, 0.66, 0.55),
    sky: new Color3(0.66, 0.74, 0.78),
  },
  // 2 — ochre. Iron-red, the colour a dry world goes when its dust is oxidised rather than silicate.
  {
    base: new Color3(0.56, 0.34, 0.24),
    rock: new Color3(0.42, 0.3, 0.25),
    peak: new Color3(0.74, 0.56, 0.44),
    sky: new Color3(0.78, 0.63, 0.53),
  },
  // 3 — ice. Pale ground, blue-grey rock, a washed-out sky.
  {
    base: new Color3(0.78, 0.81, 0.85),
    rock: new Color3(0.48, 0.53, 0.6),
    peak: new Color3(0.92, 0.94, 0.97),
    sky: new Color3(0.8, 0.85, 0.9),
  },
];

/** The palette a realm's `AppTag` names, wrapping rather than failing: a fifth planet repeats the first's colour. */
export function paletteFor(index: number): GroundPalette {
  return PALETTES[index % PALETTES.length];
}

/** Tatooine's sky, and what the scene clears to before any realm frame has arrived. */
export const SKY = PALETTES[0].sky;

/** Haze density at sea level, per metre: the horizon goes to sky over a few kilometres, the way a hot desert does. */
const FOG_GROUND = 0.00012;

/** Haze density from orbit, per metre. The map view's original value, which a 22 km view has to keep to stay readable. */
const FOG_SKY = 0.00004;

/** Altitude over which the density falls from one to the other. */
const FOG_SCALE_HEIGHT_M = 700;

/**
 * Haze density and its ceiling for a camera altitude.
 *
 * **One density cannot serve both cameras**, and the reason only shows once the ground has relief. From 2 km up you look
 * down: everything on screen is within a couple of kilometres and thin haze is right. Standing on the ground you look
 * *along* it, and the far half of the screen is ground 5–15 km away — which, past the planet's edge, is deliberately
 * darkened to mark the end of the world. At eye level that darkening is not a map affordance, it is a brown sky.
 *
 * So the density falls off with the eye's height, as an atmosphere's does, and the ceiling rises with it: near the ground
 * the far field reaches the sky colour completely, and from altitude it keeps the tint the map view has always had.
 *
 * @param altitudeM Camera height above sea level.
 * @param out Receives `(density, ceiling)`.
 */
export function fogFor(altitudeM: number, out: Vector2): void {
  const low = Math.exp(-Math.max(altitudeM, 0) / FOG_SCALE_HEIGHT_M);
  out.set(FOG_SKY + (FOG_GROUND - FOG_SKY) * low, 0.85 + 0.15 * low);
}

/**
 * The planet floor: one camera-centred displaced mesh, everything else drawn in its fragment shader — sand, rock, city and
 * landmark discs, the 256 m / 1 024 m grid, selection radii and the far-tier heatmap — so no overlay is coplanar geometry
 * and nothing z-fights at altitude (`05-client.md` § 6).
 *
 * **It shares the depth buffer with everything else** ({@link SCENE_GROUP}), which it did not use to. While the ground was
 * the plane `y = 0` it could draw alone in group 0 and let Babylon clear depth before the entities, because no segment from
 * a camera above the plane to a point above the plane crosses it — the ground could not occlude anything, exactly. Give it
 * a ridge and that stops being true: entities behind a mesa would draw through it. In a map view a dot behind a hill reads
 * as a map pin; at eye level it is a bug, and first person is why this changed.
 *
 * The bill for sharing depth is a small bias on sprites, lines and labels, which used to be free of it.
 */
export class Ground {
  private readonly terrain: TerrainMesh;
  private readonly material: PBRMaterial;
  private readonly scene: Scene;
  private readonly cameraPlanet = new Vector3();
  private readonly terrainView = new TerrainView();
  private readonly fog = new Vector2();
  private heat: HeatmapTexture;
  private readonly camera = new Vector3();
  private readonly center = new Vector2();
  private readonly mask = new Vector4();
  private readonly selection = new Vector4();
  private readonly heatRect = new Vector4();
  private readonly cells: DeliveredCellsTexture;
  private readonly cellRect = new Vector4();
  private readonly replGrid = new Vector4();
  private readonly sessionDisc = new Vector4();
  private readonly hull: number[] = new Array<number>(HULL_SLOTS * 4).fill(0);
  private readonly rect: CellRect = { x: 0, z: 0, width: 1, height: 1 };

  /** Where every terrain uniform goes: the plugin binds them onto the effect when Babylon draws the sub-mesh. */
  private readonly plugin: TerrainMaterialPlugin;

  /** The scene's sun and sky, so that {@link setPalette} can light the world the colour the world is. */
  private readonly lighting: SceneLighting;

  constructor(scene: Scene, field: Heightfield, lighting: SceneLighting) {
    this.scene = scene;
    this.lighting = lighting;
    this.terrain = new TerrainMesh(scene, field);
    const mesh = this.terrain.mesh;
    mesh.renderingGroupId = SCENE_GROUP;

    this.heat = new HeatmapTexture(scene);
    this.cells = new DeliveredCellsTexture(scene);
    const { material, plugin } = createTerrainMaterial(scene);
    this.plugin = plugin;
    plugin.heightTexture = this.terrain.texture;
    const u = this.terrain.fieldUniform;
    plugin.field.set(u[0], u[1], u[2], u[3]);
    plugin.setArray4(
      'uCities',
      CITIES.flatMap((c) => [c.x, c.z, c.radius, 0]),
    );
    plugin.setArray4(
      'uPois',
      POIS.flatMap((poi) => [poi.x, poi.z, poi.radius, 0]),
    );
    plugin.setTexture('uHeat', this.heat.texture);
    plugin.setTexture('uCells', this.cells.texture);
    mesh.material = material;
    // <b>The whole reason this material exists.</b> A raw ShaderMaterial cannot receive a Babylon shadow, which forced
    // the generator to store a format a hand-written sampler could read and so ruled out every filter worth having.
    mesh.receiveShadows = true;
    this.material = material;

    // After the field is assigned, not beside the other setters above: setPalette reads `this.material`.
    this.setPalette(PALETTES[0]);
  }

  /**
   * The ground's three colours and its sky, for the planet now on screen.
   *
   * Three `setColor3` calls on a realm switch, not per frame: a palette is a property of the world, and the shader is
   * the same program either way — which is why the place tables and the colours could both stop being baked into the
   * GLSL without a second material.
   */
  setPalette(palette: GroundPalette): void {
    // <b>One palette, and now it decides the LIGHT as well.</b> These three colours already chose the terrain's albedo
    // and the haze; handing the sky and the sand to the lighting makes them choose the ambient too, so an ice planet is
    // lit by a cold sky instead of by the studio its environment probe happened to be baked in. See
    // {@link SceneLighting.setSky} for what that costs and why the probe's own harmonics are the wrong sky.
    this.scene.fogColor = palette.sky;
    this.lighting.setSky(palette.sky, palette.base);
    // <b>No ambient floor here any more.</b> The ground shader used to carry `0.66` itself, and the port kept it as a
    // scene ambient because a StandardMaterial under one sun is black in shadow. A `scene.ambientColor` is multiplied by
    // each material's own, and PBR's default for that is black — so the constant reached the terrain and never reached a
    // building, which is precisely the inconsistency it was papering over. The terrain is a PBRMaterial now and takes
    // its sky from `scene.environmentTexture`, the same probe the buildings do.
    this.plugin.setColor3('uPalette0', palette.base);
    this.plugin.setColor3('uPalette1', palette.rock);
    this.plugin.setColor3('uPalette2', palette.peak);
  }

  /** Re-uploads the height texture after the field has been re-baked, and takes the quadtree the worker measured. */
  refreshTerrain(error?: Float32Array, minY?: Float32Array, maxY?: Float32Array): void {
    this.terrain.refresh(error, minY, maxY);
  }

  /** Triangles the terrain drew last frame: the quadtree's selection times the node grid. */
  get terrainTriangles(): number {
    return this.terrain.triangleCount;
  }

  /** Quadtree nodes the selection chose last frame — one instance each, all in one draw. */
  get terrainNodes(): number {
    return this.terrain.nodeCount;
  }

  /** True when the node budget bound the selection, so a finer tolerance would change nothing. */
  get terrainCapped(): boolean {
    return this.terrain.capped;
  }

  /** The finest node the selection reached, in metres: how much detail the tolerance bought where it matters. */
  get terrainFinestM(): number {
    return this.terrain.finestNodeM;
  }

  update(view: GroundView, grid: AggregateGrid, debug: DebugView | null = null): void {
    this.terrainView.originX = view.originX;
    this.terrainView.originZ = view.originZ;
    this.terrainView.camX = view.centerX;
    this.terrainView.camY = view.eyeY;
    this.terrainView.camZ = view.centerZ;
    this.terrainView.pixelsPerMetre = view.pixelsPerMetre;
    this.terrainView.pixelTolerance = view.pixelTolerance;
    this.terrain.place(this.terrainView);
    // The camera in PLANET metres, which is what the morph measures against — every node is anchored to the world, so
    // nothing here is relative to a render origin.
    this.cameraPlanet.set(view.centerX, view.eyeY, view.centerZ);
    this.plugin.setVector3('uCameraPlanet', this.cameraPlanet);
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
      this.plugin.setTexture('uHeat', this.heat.texture);
    }

    // The camera's own altitude picks the slab; a flat grid has one and this is always 0 (CLI3D-04).
    this.heat.update(grid, slabAt(grid, view.eyeY));
    let anyHeat = false;
    const mask = view.heatMask;
    for (let i = 0; i < mask.length; i++) {
      anyHeat ||= mask[i];
    }

    const m = this.plugin;
    // The render origin never offsets altitude, so `eyeY` IS the camera's height above sea level.
    // <b>The haze is the SCENE's now.</b> Density depends on the camera's altitude for the reason it always did: one
    // figure cannot serve both a 22 km map view and the horizon at eye level. `fogFor` still decides it; only the
    // consumer changed, from a uniform the ground shader read to the scene every Babylon material reads.
    fogFor(view.eyeY, this.fog);
    this.scene.fogMode = Scene.FOGMODE_EXP2;
    this.scene.fogDensity = this.fog.x;
    m.setVector2('uFog', this.fog);
    m.setVector3('uCamera', this.camera);
    m.setVector2('uViewCenter', this.center);
    m.setFloat('uHeatNear', view.nearRadius);
    m.setVector4('uHeatMask', this.mask);
    m.setVector4('uHeatRect', this.heatRect);
    m.setFloat('uHeatAlpha', anyHeat ? 1 : 0);
    m.setFloat('uGrid', view.showGrid ? 1 : 0);
    m.setVector4('uSelection', this.selection);
    this.updateDebug(view.showDebug ? debug : null);
  }

  /**
   * Uploads the `DEBUG` block's picture, or clears it. Everything here comes from the server: nothing is inferred from
   * what the client asked for, because the whole value of the overlay is that the two can differ — a region the engine
   * clamped draws smaller than the one the camera requested, and that is the thing worth seeing.
   */
  private updateDebug(debug: DebugView | null): void {
    const grid = debug?.grid ?? null;
    const geometry = debug?.geometry ?? null;
    if (grid === null) {
      this.replGrid.set(0, 0, 1, 0);
      this.cellRect.set(0, 0, 1, 1);
      this.sessionDisc.set(0, 0, 0, 0);
      this.plugin.setVector4('uReplGrid', this.replGrid);
      this.plugin.setVector4('uCellRect', this.cellRect);
      this.plugin.setVector4('uSessionDisc', this.sessionDisc);
      this.plugin.setFloat('uCellAlpha', 0);
      this.plugin.setFloat('uHullCount', 0);
      this.plugin.setArray4('uHull', this.hull);
      return;
    }

    this.replGrid.set(grid.origin[0], grid.origin[1], grid.cellM, 1);
    this.plugin.setVector4('uReplGrid', this.replGrid);

    const delivered = geometry === null ? 0 : this.cells.update(geometry, grid, this.rect);
    this.cellRect.set(this.rect.x, this.rect.z, Math.max(this.rect.width, 1), Math.max(this.rect.height, 1));
    this.plugin.setVector4('uCellRect', this.cellRect);
    this.plugin.setFloat('uCellAlpha', delivered > 0 ? 1 : 0);

    let hullCount = 0;
    if (geometry !== null && geometry.shape === PushShape.Region) {
      hullCount = hullUniform(geometry, this.hull, HULL_SLOTS);
    }

    this.plugin.setArray4('uHull', this.hull);
    this.plugin.setFloat('uHullCount', hullCount);

    // R′ as the SESSION holds it, which is what a sphere profile's overlay is for; a region has no radius.
    const sphere = geometry !== null && geometry.shape === PushShape.Sphere;
    this.sessionDisc.set(
      sphere ? geometry.anchor[0] : 0,
      sphere ? geometry.anchor[1] : 0,
      sphere ? geometry.radiusM : 0,
      sphere ? 1 : 0,
    );
    this.plugin.setVector4('uSessionDisc', this.sessionDisc);
  }

  /**
   * Shows or hides the whole planet backdrop.
   *
   * <b>Hidden, never disposed.</b> Behind this mesh sit a 67 MB height field and its R32F texture, from a bake measured
   * at 13.2 s across eight workers, and every planet in the world shares the one bake. A viewer steps into a shop for
   * ten seconds; paying that again to walk back out is not a trade worth making. A disabled Babylon mesh costs no draw
   * call, so the only thing keeping it costs is the memory it already occupies.
   */
  setEnabled(on: boolean): void {
    this.terrain.mesh.setEnabled(on);
  }

  dispose(): void {
    this.heat.dispose();
    this.cells.dispose();
    this.material.dispose();
    this.terrain.dispose();
  }
}

/**
 * The session's delivered cells as an RGBA8 texture, one texel per cell of the geometry's window, sampled nearest so a
 * cell reads as a square rather than a smudge. Rebuilt only when the geometry's version moves — which is when the
 * server sent a new one, not every frame — and at 64 × 64 that is a 16 KB upload.
 */
export class DeliveredCellsTexture {
  readonly texture: RawTexture;
  private readonly data = new Uint8Array(CELL_TEXTURE_SIZE * CELL_TEXTURE_SIZE * 4);
  private readonly rect: CellRect = { x: 0, z: 0, width: 1, height: 1 };
  private version = -1;
  private gridVersion = -1;
  private delivered = 0;

  constructor(scene: Scene) {
    this.texture = RawTexture.CreateRGBATexture(
      this.data,
      CELL_TEXTURE_SIZE,
      CELL_TEXTURE_SIZE,
      scene,
      false,
      false,
      Texture.NEAREST_SAMPLINGMODE,
    );
    this.texture.wrapU = Texture.CLAMP_ADDRESSMODE;
    this.texture.wrapV = Texture.CLAMP_ADDRESSMODE;
  }

  /**
   * Refreshes the texture from a geometry, and reports how many cells it holds.
   *
   * @param geometry The session's geometry.
   * @param grid The replication grid.
   * @param rect Receives the planet-space rectangle the texture covers.
   * @returns The delivered cell count, 0 when there is nothing to draw.
   */
  update(geometry: PushGeometry, grid: DebugGrid, rect: CellRect): number {
    // The count and the rectangle have to survive a frame that does not re-fill, so they are remembered. The grid's own
    // version counts too: a RESET can re-key the cells under an unchanged geometry.
    if (geometry.version !== this.version || grid.version !== this.gridVersion) {
      this.version = geometry.version;
      this.gridVersion = grid.version;
      this.delivered = fillDeliveredCells(geometry, grid, this.data, CELL_TEXTURE_SIZE, this.rect);
      this.texture.update(this.data);
    }

    rect.x = this.rect.x;
    rect.z = this.rect.z;
    rect.width = this.rect.width;
    rect.height = this.rect.height;
    return this.delivered;
  }

  dispose(): void {
    this.texture.dispose();
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
  private slab = -1;

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

  /**
   * Re-fills the texture when the grid moved, or when the camera crossed into another slab of a deep grid.
   *
   * @param grid The aggregate grid.
   * @param slab The slab to draw; `slabAt` picks it from the camera's altitude, and it is always 0 for a flat grid.
   */
  update(grid: AggregateGrid, slab: number): void {
    if (grid.version === this.version && slab === this.slab) {
      return;
    }

    this.version = grid.version;
    this.slab = slab;
    fillHeatmap(grid, this.data, this.width, this.height, slab);
    this.texture.update(this.data);
  }

  dispose(): void {
    this.texture.dispose();
  }
}
