import { Color3 } from '@babylonjs/core/Maths/math.color';
import { ShaderMaterial } from '@babylonjs/core/Materials/shaderMaterial';
import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import type { Scene } from '@babylonjs/core/scene';
import type { RealmView, SceneKind } from '../data/realm-view';
import { FLAT_GROUND, type GroundSampler } from '../terrain/ground-sampler';
import type { GroundView } from './ground';
import { createRoomMesh } from './room-mesh';
import { SCENE_GROUP } from './render-groups';
import { ROOM_FRAGMENT, ROOM_VERTEX } from './shaders';
import { cameraBoundsFor, type CameraBounds, type SceneProfile, type SceneStats } from './scene-profile';

/** Nothing here draws terrain, so every terrain figure the HUD reports is zero — and says so rather than going stale. */
const NO_TERRAIN: SceneStats = { triangles: 0, nodes: 0, finestM: 0, capped: false };

const FLOOR = new Color3(0.42, 0.36, 0.3);
const WALL = new Color3(0.3, 0.26, 0.23);
const INTERIOR_SKY = new Color3(0.16, 0.14, 0.13);

/**
 * A room: the inside of a box, sized from the realm's own bounds, with a floor grid at its replication cell.
 *
 * <b>A GENERIC room, and that is the honest scope.</b> The server has no room data — every interior realm is the same
 * 64 m square (`WorldBuilder.InteriorEdgeM`, one constant for every building), its occupants are scattered by
 * `PointInDisc(32, 32, 16)` at altitude 0, and there are no walls, doors or per-building shapes anywhere in the
 * simulation. Drawing something that looked like a particular cantina would be inventing data the server does not have.
 * Real interiors are a server-side feature — room geometry per portal — and not a renderer one.
 *
 * What this profile is really for is that the planet must stop being drawn. Measured live before it existed: a camera
 * dropped to an interior's centre put the terrain quadtree at **2 281 472 triangles over 1 114 nodes**, against 198 656
 * over 97 from orbit, because the LOD was subdividing at eye level over ground nobody should have been looking at.
 */
export class InteriorProfile implements SceneProfile {
  readonly kind: SceneKind = 'interior';
  readonly ground: GroundSampler = FLAT_GROUND;
  readonly sky = INTERIOR_SKY;
  readonly stats = NO_TERRAIN;

  private readonly mesh: Mesh;
  private readonly material: ShaderMaterial;
  /** The edge the geometry was built at; every {@link enter} scales the realm's own edge against it. */
  private readonly builtEdgeM: number;
  private current: CameraBounds;

  constructor(scene: Scene, edgeM: number) {
    // Built at a nominal size and SCALED per realm rather than rebuilt: every interior in this world is the same box,
    // and a realm that is not would still only need a scale. Rebuilding geometry on every door would be the one
    // allocation on the switch path.
    this.builtEdgeM = edgeM;
    this.mesh = createRoomMesh('interior', scene, edgeM, edgeM);
    this.mesh.renderingGroupId = SCENE_GROUP;
    this.mesh.setEnabled(false);

    this.material = new ShaderMaterial(
      'room',
      scene,
      { vertexSource: ROOM_VERTEX, fragmentSource: ROOM_FRAGMENT },
      {
        attributes: ['position', 'normal'],
        uniforms: ['world', 'viewProjection', 'uFloor', 'uWall', 'uCellM', 'uGrid'],
      },
    );
    this.material.setColor3('uFloor', FLOOR);
    this.material.setColor3('uWall', WALL);

    // Seeded here as well as in `enter`: `update` may run first, and a cell of 0 divides to infinity in the shader.
    this.material.setFloat('uCellM', 64);
    this.material.setFloat('uGrid', 0);
    this.mesh.material = this.material;

    // A room at the origin, until the first `enter` gives it a realm. Never used to drive a camera — `resetForRealm`
    // sets the bounds from the realm itself — but a profile that could report nothing would make every reader of
    // `bounds` handle a case that cannot happen.
    this.current = cameraBoundsFor(roomView(0, 0, edgeM, edgeM, edgeM));
  }

  get bounds(): CameraBounds {
    return this.current;
  }

  enter(view: RealmView): void {
    // The room is placed and scaled from the realm's bounds, which for an interior start at the ORIGIN rather than
    // straddling it. A profile that assumed a centred world would put the box on one corner of itself.
    const width = view.maxX - view.minX;
    const depth = view.maxZ - view.minZ;
    this.mesh.position.set(view.minX, 0, view.minZ);

    // `.set` rather than a new Vector3: the scene owns this vector and replacing it costs a GC entry for nothing.
    this.mesh.scaling.set(width / this.builtEdgeM, 1, depth / this.builtEdgeM);
    this.material.setFloat('uCellM', view.cellM > 0 ? view.cellM : 64);
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

/** The subset of a {@link RealmView} {@link cameraBoundsFor} reads, for a room this profile has not been given yet. */
function roomView(minX: number, minZ: number, maxX: number, maxZ: number, cellM: number): RealmView {
  return {
    scene: 'interior',
    realmId: 0,
    generation: 0,
    key: '',
    appTag: 0,
    palette: 0,
    placeSet: 0,
    slot: 0,
    deep: false,
    cellM,
    minX,
    maxX,
    minY: 0,
    maxY: 0,
    minZ,
    maxZ,
    centreX: (minX + maxX) * 0.5,
    centreZ: (minZ + maxZ) * 0.5,
    halfX: (maxX - minX) * 0.5,
    halfZ: (maxZ - minZ) * 0.5,
  };
}
