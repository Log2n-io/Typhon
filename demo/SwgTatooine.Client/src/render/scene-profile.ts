import type { Color3 } from '@babylonjs/core/Maths/math.color';
import type { AggregateGrid } from '@typhondb/client';
import type { RealmView, SceneKind } from '../data/realm-view';
import type { GroundSampler } from '../terrain/ground-sampler';
import type { DebugView } from '../data/source';
import type { GroundView } from './ground';

/**
 * Where a camera may go in a realm.
 *
 * <b>Supplied by the scene, applied by the camera.</b> The profile knows how big its world is and whether there is a
 * floor under it; the camera knows how to smooth, drag and orbit. Keeping the two apart is what stops a third scene
 * from needing a third camera — the difference between a 16 km planet and a 64 m room is six numbers, not a rig.
 */
export interface CameraBounds {
  readonly minX: number;
  readonly maxX: number;
  readonly minZ: number;
  readonly maxZ: number;
  readonly minDistanceM: number;
  readonly maxDistanceM: number;
  /**
   * The shallowest the camera may look, radians — and the only thing deciding whether the eye may go below `y = 0`.
   *
   * A room is looked into from above and a planet can be grazed, so both are positive. **Space is negative**, which is
   * what lets the eye orbit under the ecliptic: `updateEye` puts it at `groundY + sin(pitch) * distance`, so nothing
   * but a negative pitch can take it below the plane. A separate `allowBelowGround` flag was tried and removed — it
   * said the same thing, nothing read it, and the tests guarding it were guarding a value with no consumer.
   */
  readonly minPitch: number;
}

/** The terrain numbers the HUD reports; all zero for a scene that draws no terrain. */
export interface SceneStats {
  readonly triangles: number;
  readonly nodes: number;
  readonly finestM: number;
  readonly capped: boolean;
}

/**
 * The backdrop for one kind of realm: the ground (or the absence of one), what decorates it, and where a camera may go.
 *
 * <b>A profile owns the backdrop and nothing else.</b> The entity layers, the packer, attack lines, picking, the labels'
 * selection tag, the HUD and the inspector are all realm-agnostic — they resolve everything by archetype NAME and read
 * positions through `Placement` — so they stay where they are and are shared by every scene. The moment a profile owns
 * them, every LOD, pick and follow bug becomes three bugs.
 *
 * <b>Built lazily, kept forever, hidden on {@link leave}.</b> The planet's field is 67 MB on the CPU and another 67 MB
 * as an R32F texture, from a bake measured at 13.2 s across eight workers. A viewer steps into a shop for ten seconds;
 * paying that twice to come back out is indefensible, and since every planet shares one bake there is never a reason to
 * drop it. The other scenes are a handful of meshes each, so keeping all of them resident costs a few MB and no draw
 * calls — a disabled Babylon mesh is not submitted.
 */
export interface SceneProfile {
  readonly kind: SceneKind;
  /** What the camera orbits, the pick marches and the labels sit above. `FLAT_GROUND` wherever the floor is flat. */
  readonly ground: GroundSampler;
  readonly bounds: CameraBounds;
  readonly sky: Color3;
  readonly stats: SceneStats;
  /** Shows this scene and reframes it for a realm. Must allocate nothing on the GPU: that belongs in the constructor. */
  enter(view: RealmView): void;
  /** Hides it. Never disposes — see the note on lifetime above. */
  leave(): void;
  update(view: GroundView, grid: AggregateGrid, debug: DebugView | null): void;
  dispose(): void;
}

/** A room is looked into from above: below this the camera is inside a wall rather than seeing the floor. */
const INTERIOR_MIN_PITCH = (25 * Math.PI) / 180;

/** How far under the ecliptic a camera in space may swing. Short of straight down, as the ceiling is short of straight up. */
const SPACE_MAX_PITCH = (85 * Math.PI) / 180;

/** The map camera's floor on the planet, kept as it was: 12°. */
const PLANET_MIN_PITCH = (12 * Math.PI) / 180;

/** The closest any camera may sit to what it orbits. Below this the near plane starts clipping the subject. */
const MIN_DISTANCE_M = 6;

/**
 * Where the camera may go in this realm, from the realm's own bounds.
 *
 * <b>Scaled from the realm rather than tabulated per scene.</b> The whole reason {@link RealmView} carries a centre and
 * a half-extent is that a 16 km planet and a 64 m room are the same problem at two scales; a table of magic distances
 * per kind would throw that away and be wrong for the first realm sized differently from the one it was written for.
 *
 * The pull-back ceiling is the realm's diagonal times a little over one, which frames the whole world with room to
 * spare and — on the planet — lands within a few hundred metres of the 22 km the map camera used before it was per
 * realm.
 */
export function cameraBoundsFor(view: RealmView): CameraBounds {
  const reach = Math.max(view.halfX, view.halfZ) * 2.7;
  return {
    minX: view.minX,
    maxX: view.maxX,
    minZ: view.minZ,
    maxZ: view.maxZ,
    minDistanceM: MIN_DISTANCE_M,
    maxDistanceM: Math.max(MIN_DISTANCE_M * 2, reach),
    // Space is the one realm with no floor — there is nothing under the camera to be inside of, and its bounds run
    // below zero on every axis — so it is the one realm whose pitch floor is NEGATIVE: the eye may orbit under the
    // ecliptic and look up at what is above it. Everywhere else there is a floor at y = 0 or terrain above it, and a
    // camera that went under would see the inside of the ground.
    minPitch: view.scene === 'space' ? -SPACE_MAX_PITCH : view.scene === 'planet' ? PLANET_MIN_PITCH : INTERIOR_MIN_PITCH,
  };
}
