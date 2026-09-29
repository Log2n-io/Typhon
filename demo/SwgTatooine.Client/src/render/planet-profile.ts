import type { Color3 } from '@babylonjs/core/Maths/math.color';
import type { Scene } from '@babylonjs/core/scene';
import type { AggregateGrid } from '@typhondb/client';
import type { RealmView, SceneKind } from '../data/realm-view';
import type { Heightfield } from '../terrain/heightfield';
import type { GroundSampler } from '../terrain/ground-sampler';
import type { DebugView } from '../data/source';
import { Ground, paletteFor, type GroundPalette, type GroundView } from './ground';
import { cameraBoundsFor, type CameraBounds, type SceneProfile, type SceneStats } from './scene-profile';

/**
 * The planet: today's {@link Ground} — CDLOD terrain, place discs, the grid, the heatmap and the debug overlay — behind
 * the scene interface.
 *
 * <b>It adds nothing.</b> Every pixel it draws it drew before; this exists so that the thing which knows how to draw a
 * planet is a peer of the thing that knows how to draw a room, rather than the only option hard-wired into the frame.
 * The terrain, its 67 MB field and its bake belong to this profile and are built once for the session — every planet
 * shares one bake, so entering a building and coming back out must not disturb any of it.
 */
export class PlanetProfile implements SceneProfile {
  readonly kind: SceneKind = 'planet';

  private readonly groundLayer: Ground;
  private readonly field: Heightfield;
  private current: CameraBounds;
  private palette: GroundPalette = paletteFor(0);
  private readonly terrainStats = { triangles: 0, nodes: 0, finestM: 0, capped: false };

  constructor(scene: Scene, field: Heightfield, planetHalfExtentM: number) {
    this.field = field;
    this.groundLayer = new Ground(scene, field);
    this.current = cameraBoundsFor(planetView(planetHalfExtentM));
  }

  /** The sky of the planet now on screen — part of its palette, so a green world is not under a desert's haze. */
  get sky(): Color3 {
    return this.palette.sky;
  }

  /** The heightfield: what the camera rides, the pick marches and the labels sit above. */
  get ground(): GroundSampler {
    return this.field;
  }

  get bounds(): CameraBounds {
    return this.current;
  }

  get stats(): SceneStats {
    // Rewritten, not rebuilt: the HUD reads four fields off this and would otherwise allocate four objects to do it.
    // The other profiles return a shared constant for the same reason.
    this.terrainStats.triangles = this.groundLayer.terrainTriangles;
    this.terrainStats.nodes = this.groundLayer.terrainNodes;
    this.terrainStats.finestM = this.groundLayer.terrainFinestM;
    this.terrainStats.capped = this.groundLayer.terrainCapped;
    return this.terrainStats;
  }

  enter(view: RealmView): void {
    this.current = cameraBoundsFor(view);

    // Every planet is the same relief and the same towns — one bake, one map — so the palette is not decoration on top
    // of a difference, it IS the difference between one planet and the next.
    this.palette = paletteFor(view.palette);
    this.groundLayer.setPalette(this.palette);
    this.groundLayer.setEnabled(true);
  }

  leave(): void {
    this.groundLayer.setEnabled(false);
  }

  update(view: GroundView, grid: AggregateGrid, debug: DebugView | null): void {
    this.groundLayer.update(view, grid, debug);
  }

  /** Re-uploads the height texture once the bake lands, and takes the quadtree the worker measured. */
  refreshTerrain(error?: Float32Array, minY?: Float32Array, maxY?: Float32Array): void {
    this.groundLayer.refreshTerrain(error, minY, maxY);
  }

  dispose(): void {
    this.groundLayer.dispose();
  }
}

/** The planet as a realm view, for the bounds this profile reports before any `REALM` block has arrived. */
function planetView(halfExtentM: number): RealmView {
  return {
    scene: 'planet',
    realmId: 0,
    generation: 0,
    key: '',
    appTag: 0,
    palette: 0,
    placeSet: 0,
    slot: 0,
    deep: false,
    cellM: 64,
    minX: -halfExtentM,
    maxX: halfExtentM,
    minY: 0,
    maxY: 0,
    minZ: -halfExtentM,
    maxZ: halfExtentM,
    centreX: 0,
    centreZ: 0,
    halfX: halfExtentM,
    halfZ: halfExtentM,
  };
}
