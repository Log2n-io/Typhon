import { Color3 } from '@babylonjs/core/Maths/math.color';
import { ShaderMaterial } from '@babylonjs/core/Materials/shaderMaterial';
import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import type { Scene } from '@babylonjs/core/scene';
import type { RealmView, SceneKind } from '../data/realm-view';
import { FLAT_GROUND, type GroundSampler } from '../terrain/ground-sampler';
import type { GroundView } from './ground';
import { createBoxMesh } from './room-mesh';
import { SCENE_GROUP } from './render-groups';
import { SPACE_FRAGMENT, SPACE_VERTEX } from './shaders';
import { cameraBoundsFor, type CameraBounds, type SceneProfile, type SceneStats } from './scene-profile';

/** Nothing here draws terrain, so every terrain figure the HUD reports is zero rather than the planet's stale one. */
const NO_TERRAIN: SceneStats = { triangles: 0, nodes: 0, finestM: 0, capped: false };

/** The grid and the bounds. Cold, and dim enough that a star still reads as brighter than the structure around it. */
const GRID = new Color3(0.26, 0.42, 0.62);

/** Deep space, which is what the scene clears to behind the box in the frames before it draws. */
const SPACE_SKY = new Color3(0.02, 0.025, 0.045);

/** The size the box geometry is authored at; every realm scales against it. */
const BUILT_EDGE_M = 1;

/**
 * Space: the inside of the realm's own bounds, carrying a star field, the replication grid and a lit edge.
 *
 * <b>It is deliberately empty of ships.</b> `Starship` is not replicated — the archetype carries no `[Replicated]` and
 * `ShipPlacement` no `[Replicate]` — so nothing in space reaches the wire today. Drawing the realm honestly empty is the
 * point: it shows that the deep realm exists, is entered, is framed and is bounded, without inventing occupants. Adding
 * them is its own item, and it is not a small one: the position codec is built over realm 0's grid and TypeScript
 * codegen still bakes those bounds as literals, so a ship would decode into a 256 m slab inside a 16 km cube.
 *
 * <b>The one realm the camera is inside rather than above.</b> Its bounds run below zero on all three axes, so the pitch
 * floor here is negative and `MapCamera.groundHit` had to stop rejecting upward rays — from under the ecliptic, looking
 * up is the ordinary case, and the old sign test turned pan- and zoom-to-cursor off without saying anything.
 */
export class SpaceProfile implements SceneProfile {
  readonly kind: SceneKind = 'space';
  /** There is no ground in space; the camera orbits the `y = 0` plane through the middle of the realm. */
  readonly ground: GroundSampler = FLAT_GROUND;
  readonly sky = SPACE_SKY;
  readonly stats = NO_TERRAIN;

  private readonly mesh: Mesh;
  private readonly material: ShaderMaterial;
  private current: CameraBounds;

  constructor(scene: Scene) {
    // A unit box, scaled per realm. Space is one realm and always the same size, but authoring at 1 means the scale is
    // the realm's extent rather than a ratio against a constant that would have to be kept in step with the server.
    this.mesh = createBoxMesh('space', scene, BUILT_EDGE_M, BUILT_EDGE_M, BUILT_EDGE_M);
    this.mesh.renderingGroupId = SCENE_GROUP;
    this.mesh.setEnabled(false);

    this.material = new ShaderMaterial(
      'space',
      scene,
      { vertexSource: SPACE_VERTEX, fragmentSource: SPACE_FRAGMENT },
      { attributes: ['position', 'normal', 'uv'], uniforms: ['world', 'viewProjection', 'uGridColor', 'uCellM', 'uGrid'] },
    );
    this.material.setColor3('uGridColor', GRID);

    // Seeded here as well as in `enter`, because `update` may run first and a cell of 0 divides to infinity in the
    // shader — a grid of NaN, which reads as a black or white face rather than as anything recognisable.
    this.material.setFloat('uCellM', 500);
    this.material.setFloat('uGrid', 0);

    // The box is the edge of the world seen from inside it, so it must never be depth-tested away by something drawn
    // in front of it, and nothing is ever behind it.
    this.material.backFaceCulling = false;
    this.mesh.material = this.material;
    this.current = cameraBoundsFor(spaceView(8000));
  }

  get bounds(): CameraBounds {
    return this.current;
  }

  enter(view: RealmView): void {
    this.mesh.position.set(view.minX, view.minY, view.minZ);

    // `.set` rather than a new Vector3: one allocation per realm switch is not a leak, but the scene owns this vector
    // and replacing it costs a GC entry for nothing.
    this.mesh.scaling.set(view.maxX - view.minX, view.maxY - view.minY, view.maxZ - view.minZ);

    // The realm's own replication cell, in metres — the grid is drawn in world space, so this is all the shader needs
    // and it stays correct on a realm that is not a cube.
    this.material.setFloat('uCellM', view.cellM > 0 ? view.cellM : 500);
    this.current = cameraBoundsFor(view);
    this.mesh.setEnabled(true);
  }

  leave(): void {
    this.mesh.setEnabled(false);
  }

  update(view: GroundView): void {
    this.material.setFloat('uGrid', view.showGrid ? 1 : 0);
  }

  dispose(): void {
    this.mesh.dispose();
    this.material.dispose();
  }
}

/** A cube of the given half-extent, for the bounds this profile reports before a realm has been entered. */
function spaceView(halfM: number): RealmView {
  return {
    scene: 'space',
    realmId: 0,
    generation: 0,
    key: '',
    appTag: 0,
    palette: 0,
    placeSet: 0,
    slot: 0,
    deep: true,
    cellM: 500,
    minX: -halfM,
    maxX: halfM,
    minY: -halfM,
    maxY: halfM,
    minZ: -halfM,
    maxZ: halfM,
    centreX: 0,
    centreZ: 0,
    halfX: halfM,
    halfZ: halfM,
  };
}
