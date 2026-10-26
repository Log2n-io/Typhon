import { Color4 } from '@babylonjs/core/Maths/math.color';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { CreateLineSystem } from '@babylonjs/core/Meshes/Builders/linesBuilder';
import type { LinesMesh } from '@babylonjs/core/Meshes/linesMesh';
import type { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import type { Scene } from '@babylonjs/core/scene';
import { axisVector, directionToWorld, pointToWorld, poseToWorld, type Pose } from '../assets/asset-frame';
import type { Descriptor } from '../assets/descriptor';
import { topAt, walkable, type AssetShape, type PlanBox } from '../app/structure-collision';
import { add, rotate, type Vec3 } from '../math/vec';
import { GIZMO_GROUP } from './render-groups';

/**
 * Gameplay data drawn over the world: portals, spawns, interactions, approach paths, and the colliders the body is pushed
 * out of. A debug view, ported from the asset testbench's `debug-overlay.ts` and drawn from the same data the game uses.
 *
 * <b>From the data the game acts on, not from the meshes.</b> Markers come from each placed structure's descriptor, through
 * the same placement the renderer uses (`asset-frame.ts`), which is also what the server reads its doors from; colliders come
 * from the boxes the server publishes in `/typhon/demo.json`, the ones the prediction pushes the body out of. A gizmo that
 * misses its mesh is therefore a real disagreement, not a drawing error.
 *
 * Colours follow the testbench: colliders orange, portals blue (with an arrow along the direction that crosses them),
 * spawns green, interactions magenta, approach paths yellow, the player's body square white.
 */

/** A colour, linear RGBA. */
export type Rgba = readonly [number, number, number, number];

export const COLLIDER_COLOUR: Rgba = [1, 0.55, 0.1, 1];
/** A collider top steep enough to be a wall rather than something to stand on is drawn redder. */
export const WALL_TOP_COLOUR: Rgba = [1, 0.25, 0.1, 1];
export const PORTAL_COLOUR: Rgba = [0.2, 0.6, 1, 1];
export const SPAWN_COLOUR: Rgba = [0.2, 1, 0.3, 1];
export const INTERACTION_COLOUR: Rgba = [1, 0.2, 0.9, 1];
export const APPROACH_COLOUR: Rgba = [1, 0.9, 0.2, 1];
export const BODY_COLOUR: Rgba = [1, 1, 1, 1];

/** Polylines with one colour each: what a Babylon line system is built from. */
export interface LineSet {
  readonly lines: Vec3[][];
  readonly colours: Rgba[];
}

function emptySet(): LineSet {
  return { lines: [], colours: [] };
}

function push(set: LineSet, line: Vec3[], colour: Rgba): void {
  set.lines.push(line);
  set.colours.push(colour);
}

/** The twelve edges of a box given by its eight corners, `corner(sx, sy, sz)` with each sign ±1. */
function boxEdges(set: LineSet, corner: (sx: number, sy: number, sz: number) => Vec3, colour: Rgba): void {
  for (const sy of [-1, 1]) {
    push(set, [corner(-1, sy, -1), corner(1, sy, -1), corner(1, sy, 1), corner(-1, sy, 1), corner(-1, sy, -1)], colour);
  }

  for (const sx of [-1, 1]) {
    for (const sz of [-1, 1]) {
      push(set, [corner(sx, -1, sz), corner(sx, 1, sz)], colour);
    }
  }
}

/** A horizontal circle. */
function circle(set: LineSet, centre: Vec3, radius: number, colour: Rgba, segments = 24): void {
  const line: Vec3[] = [];
  for (let i = 0; i <= segments; i++) {
    const a = (i / segments) * Math.PI * 2;
    line.push([centre[0] + radius * Math.cos(a), centre[1], centre[2] + radius * Math.sin(a)]);
  }

  push(set, line, colour);
}

/** An arrow from `from` along the unit `direction`, with a small head. */
function arrow(set: LineSet, from: Vec3, direction: Vec3, length: number, colour: Rgba): void {
  const tip = add(from, [direction[0] * length, direction[1] * length, direction[2] * length]);
  push(set, [from, tip], colour);
  // The head in the horizontal plane, a quarter of the shaft back from the tip.
  const back: Vec3 = [-direction[0] * length * 0.25, 0, -direction[2] * length * 0.25];
  const side: Vec3 = [-direction[2] * length * 0.15, 0, direction[0] * length * 0.15];
  push(set, [add(add(tip, back), side), tip, add(add(tip, back), [-side[0], 0, -side[2]])], colour);
}

/** A marker's facing: its local `forward` axis, turned by its own rotation, then by the placement. */
function markerForward(pose: Pose, rot: readonly [number, number, number, number], forward: string): Vec3 {
  return directionToWorld(pose, rotate(rot, axisVector(forward)));
}

/**
 * The portals, spawns, interactions and approach paths of one placed asset and its children, in the world frame.
 *
 * @param descriptor The asset's descriptor.
 * @param pose Its world placement (`placement([x, y, z], structureYaw(quarters))`, as `static-assets.ts` places it).
 * @param describe The descriptor of a child asset, or `null` when it is not loaded; children without one are skipped.
 */
export function markerLines(
  descriptor: Descriptor,
  pose: Pose,
  describe: (asset: string) => Descriptor | null,
  set: LineSet = emptySet(),
): LineSet {
  for (const portal of descriptor.portals) {
    const at = poseToWorld(pose, portal.center, portal.rot);
    const [hx, hy, hz] = portal.half;
    boxEdges(set, (sx, sy, sz) => add(at.pos, rotate(at.rot, [sx * hx, sy * hy, sz * hz])), PORTAL_COLOUR);
    arrow(set, at.pos, markerForward(pose, portal.rot, portal.forward), 1.2, PORTAL_COLOUR);
    approach(set, pose, portal.approach);
  }

  for (const spawn of descriptor.spawns) {
    const at = pointToWorld(pose, spawn.pos);
    circle(set, at, 0.3, SPAWN_COLOUR, 16);
    arrow(set, at, markerForward(pose, spawn.rot, spawn.forward), 0.8, SPAWN_COLOUR);
  }

  for (const interaction of descriptor.interactions) {
    const at = pointToWorld(pose, interaction.pos);
    circle(set, at, interaction.radius ?? 0.5, INTERACTION_COLOUR);
    arrow(set, at, markerForward(pose, interaction.rot, interaction.forward), 0.8, INTERACTION_COLOUR);
  }

  for (const child of descriptor.children) {
    approach(set, pose, child.approach);
    const childDescriptor = describe(child.asset);
    if (childDescriptor !== null) {
      markerLines(childDescriptor, poseToWorld(pose, child.pos, child.rot), describe, set);
    }
  }

  return set;
}

function approach(set: LineSet, pose: Pose, points: readonly Vec3[] | undefined): void {
  if (points === undefined || points.length < 2) {
    return;
  }

  push(
    set,
    points.map((p) => pointToWorld(pose, p)),
    APPROACH_COLOUR,
  );
}

/**
 * The colliders of one placed structure as the server publishes them and the prediction pushes against them.
 *
 * Each box is drawn as its plan rectangle (or circle) from its underside, with the TOP the collision rule reads (`topAt`):
 * a ramp's sloped face rather than its bounding box's roof. A top too steep to stand on is drawn in the wall colour.
 *
 * @param shape The asset's prepared shape.
 * @param quarters The placement's quarter turns, which pick the pre-turned boxes.
 * @param x The placement's X; boxes are measured from it.
 * @param y The altitude the asset's origin stands at; the boxes' heights are measured from it.
 * @param z The placement's Z.
 */
export function colliderLines(
  shape: AssetShape,
  quarters: number,
  x: number,
  y: number,
  z: number,
  set: LineSet = emptySet(),
): LineSet {
  const boxes = shape.turned[((quarters % 4) + 4) % 4];
  for (const box of boxes) {
    colliderBox(set, box, x, y, z);
  }

  return set;
}

function colliderBox(set: LineSet, box: PlanBox, x: number, y: number, z: number): void {
  const colour = walkable(box) ? COLLIDER_COLOUR : WALL_TOP_COLOUR;
  if (box.radius > 0) {
    const cx = (box.minX + box.maxX) / 2;
    const cz = (box.minZ + box.maxZ) / 2;
    circle(set, [x + cx, y + box.minY, z + cz], box.radius, colour);
    circle(set, [x + cx, y + topAt(box, cx, cz), z + cz], box.radius, colour);
    for (let i = 0; i < 4; i++) {
      const a = (i / 4) * Math.PI * 2;
      const px = cx + box.radius * Math.cos(a);
      const pz = cz + box.radius * Math.sin(a);
      push(
        set,
        [
          [x + px, y + box.minY, z + pz],
          [x + px, y + topAt(box, px, pz), z + pz],
        ],
        colour,
      );
    }

    return;
  }

  const corners: [number, number][] = [
    [box.minX, box.minZ],
    [box.maxX, box.minZ],
    [box.maxX, box.maxZ],
    [box.minX, box.maxZ],
  ];
  const bottom = corners.map(([px, pz]): Vec3 => [x + px, y + box.minY, z + pz]);
  const top = corners.map(([px, pz]): Vec3 => [x + px, y + topAt(box, px, pz), z + pz]);
  push(set, [...bottom, bottom[0]], colour);
  push(set, [...top, top[0]], colour);
  for (let i = 0; i < 4; i++) {
    push(set, [bottom[i], top[i]], colour);
  }
}

/** The player's collision body: an axis-aligned square of half-extent `half`, `height` tall, standing at its feet. */
export function bodyLines(x: number, y: number, z: number, half: number, height: number): LineSet {
  const set = emptySet();
  boxEdges(set, (sx, sy, sz) => [x + sx * half, y + (sy + 1) * 0.5 * height, z + sz * half], BODY_COLOUR);
  return set;
}

/** One placed structure, as the overlay needs it. */
export interface GizmoPlacement {
  readonly netId: number;
  /** The descriptor's id, so a placement that changed asset is rebuilt. */
  readonly asset: string;
  readonly descriptor: Descriptor | null;
  readonly pose: Pose;
  readonly shape: AssetShape | null;
  readonly quarters: number;
  readonly x: number;
  readonly y: number;
  readonly z: number;
}

/** What the overlay draws. */
export interface GizmoOptions {
  /** Portals, spawns, interactions, approach paths, and the player's body. */
  readonly markers: boolean;
  /** The published colliders. */
  readonly colliders: boolean;
}

interface Drawn {
  readonly key: string;
  readonly markers: LinesMesh | null;
  readonly colliders: LinesMesh | null;
}

function disposeDrawn(drawn: Drawn): void {
  drawn.markers?.dispose();
  drawn.colliders?.dispose();
}

/**
 * Keeps one line mesh per kind per placed structure, built when the structure is first placed and disposed when it is not.
 *
 * Built once rather than per frame: a placement does not move, and a debug overlay must not be what makes the frame slow.
 * Hung off the assets' root, which carries the render origin, so the lines are in planet coordinates like the meshes.
 */
export class GameplayGizmos {
  private readonly scene: Scene;
  private readonly root: TransformNode;
  private readonly drawn = new Map<number, Drawn>();
  private readonly seen = new Set<number>();
  private body: LinesMesh | null = null;
  private bodyKey = '';

  constructor(scene: Scene, root: TransformNode) {
    this.scene = scene;
    this.root = root;
  }

  /**
   * Brings the overlay in line with this frame's placements.
   *
   * @param placements The structures placed this frame.
   * @param count How many of them are live.
   * @param options What to draw; both off disposes everything.
   * @param describe A child asset's descriptor, or `null`.
   */
  update(
    placements: readonly GizmoPlacement[],
    count: number,
    options: GizmoOptions,
    describe: (asset: string) => Descriptor | null,
  ): void {
    this.seen.clear();
    if (options.markers || options.colliders) {
      for (let i = 0; i < count; i++) {
        const placement = placements[i];
        this.seen.add(placement.netId);
        const key = `${placement.asset}|${String(placement.x)}|${String(placement.y)}|${String(placement.z)}|${String(placement.quarters)}`;
        let drawn = this.drawn.get(placement.netId);
        if (drawn !== undefined && drawn.key !== key) {
          disposeDrawn(drawn);
          drawn = undefined;
        }

        if (drawn === undefined) {
          drawn = {
            key,
            markers:
              placement.descriptor === null
                ? null
                : this.mesh(
                    `gizmo:${String(placement.netId)}`,
                    markerLines(placement.descriptor, placement.pose, describe),
                  ),
            colliders:
              placement.shape === null
                ? null
                : this.mesh(
                    `collider:${String(placement.netId)}`,
                    colliderLines(placement.shape, placement.quarters, placement.x, placement.y, placement.z),
                  ),
          };
          this.drawn.set(placement.netId, drawn);
        }

        drawn.markers?.setEnabled(options.markers);
        drawn.colliders?.setEnabled(options.colliders);
      }
    }

    for (const [netId, drawn] of this.drawn) {
      if (!this.seen.has(netId)) {
        disposeDrawn(drawn);
        this.drawn.delete(netId);
      }
    }
  }

  /**
   * Draws the player's collision body at its feet, or hides it with `null`.
   *
   * @param at The predicted feet and the body's half-extent and height, or `null`.
   */
  setBody(at: { x: number; y: number; z: number; half: number; height: number } | null): void {
    if (at === null) {
      this.body?.setEnabled(false);
      return;
    }

    // Rebuilt only when its size changes; moved otherwise.
    const key = `${String(at.half)}|${String(at.height)}`;
    let body = this.body;
    if (body === null || this.bodyKey !== key) {
      body?.dispose();
      body = this.mesh('gizmo:body', bodyLines(0, 0, 0, at.half, at.height));
      this.body = body;
      this.bodyKey = key;
    }

    body?.position.set(at.x, at.y, at.z);
    body?.setEnabled(true);
  }

  dispose(): void {
    for (const drawn of this.drawn.values()) {
      disposeDrawn(drawn);
    }

    this.drawn.clear();
    this.body?.dispose();
    this.body = null;
  }

  private mesh(name: string, set: LineSet): LinesMesh | null {
    if (set.lines.length === 0) {
      return null;
    }

    const lines = set.lines.map((line) => line.map((p) => new Vector3(p[0], p[1], p[2])));
    const colours = set.lines.map((line, i) => {
      const c = set.colours[i];
      return line.map(() => new Color4(c[0], c[1], c[2], c[3]));
    });
    const mesh = CreateLineSystem(name, { lines, colors: colours }, this.scene);
    mesh.parent = this.root;
    mesh.isPickable = false;
    mesh.renderingGroupId = GIZMO_GROUP;
    return mesh;
  }
}
