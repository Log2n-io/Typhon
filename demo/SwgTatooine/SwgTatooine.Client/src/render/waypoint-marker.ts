import { Color3 } from '@babylonjs/core/Maths/math.color';
import { StandardMaterial } from '@babylonjs/core/Materials/standardMaterial';
import { CreateCylinder } from '@babylonjs/core/Meshes/Builders/cylinderBuilder';
import type { Mesh } from '@babylonjs/core/Meshes/mesh';
import type { Scene } from '@babylonjs/core/scene';
import { Constants } from '@babylonjs/core/Engines/constants';
import { SCENE_GROUP } from './render-groups';

/** The pillar's height and width, metres: seen over a town's roofs from a kilometre away, thin enough not to hide what stands at it. */
export const PILLAR_HEIGHT_M = 120;
export const PILLAR_WIDTH_M = 1.2;

/** How far inside the viewport's edge the edge arrow stands, CSS pixels. */
export const EDGE_MARGIN_PX = 28;

/**
 * The active waypoint in the world (client.md C38, U5): a light pillar standing on the ground at it, in its colour — unlit and blended, not added:
 * added, a red one went white against the bright sky (seen live, 2026-11-03) — never picked, never shadowed.
 */
export class WaypointPillar {
  private readonly mesh: Mesh;
  private readonly material: StandardMaterial;
  private colour = '';

  constructor(scene: Scene) {
    this.mesh = CreateCylinder('waypoint-pillar', { height: 1, diameter: 1, tessellation: 12, cap: 0 }, scene);
    this.material = new StandardMaterial('waypoint-pillar', scene);
    this.material.disableLighting = true;
    this.material.alpha = 0.45;
    this.material.alphaMode = Constants.ALPHA_COMBINE;
    this.material.backFaceCulling = false;
    this.material.disableDepthWrite = true;
    this.mesh.material = this.material;
    this.mesh.renderingGroupId = SCENE_GROUP;
    this.mesh.isPickable = false;
    this.mesh.scaling.set(PILLAR_WIDTH_M, PILLAR_HEIGHT_M, PILLAR_WIDTH_M);
    this.mesh.isVisible = false;
  }

  /** Stands it at a planet point (render coordinates: the origin taken off), its foot at `groundY`; hidden for `null`. */
  update(
    at: { readonly x: number; readonly z: number; readonly groundY: number; readonly colour: string } | null,
    originX: number,
    originZ: number,
  ): void {
    if (at === null) {
      this.mesh.isVisible = false;
      return;
    }

    if (at.colour !== this.colour) {
      this.colour = at.colour;
      this.material.emissiveColor = Color3.FromHexString(at.colour);
    }

    this.mesh.position.set(at.x - originX, at.groundY + PILLAR_HEIGHT_M / 2, at.z - originZ);
    this.mesh.isVisible = true;
  }

  dispose(): void {
    this.mesh.dispose();
    this.material.dispose();
  }
}

/**
 * Where the edge arrow goes for a waypoint projected at (sx, sy) with clip w, in a viewport of width × height: `null` when it is on screen (the pillar
 * shows it), else a point inside the edge, `margin` in, on the line from the centre towards it, and the angle the arrow points, radians, 0 up and
 * growing clockwise. Behind the camera the projection says nothing useful, so `turn` — its bearing from straight ahead, the waypoint pane's — does:
 * a waypoint behind and to the left is shown at the bottom left.
 */
export function edgeArrow(
  sx: number,
  sy: number,
  w: number,
  width: number,
  height: number,
  turn: number,
  margin = EDGE_MARGIN_PX,
): { readonly x: number; readonly y: number; readonly angle: number } | null {
  const cx = width / 2;
  const cy = height / 2;
  const behind = !(w > 1e-6) || !Number.isFinite(sx) || !Number.isFinite(sy);
  if (!behind && sx >= margin && sx <= width - margin && sy >= margin && sy <= height - margin) {
    return null;
  }

  let dx = behind ? Math.sin(turn) : sx - cx;
  let dy = behind ? -Math.cos(turn) : sy - cy;
  if (Math.abs(dx) < 1e-6 && Math.abs(dy) < 1e-6) {
    dy = 1;
    dx = 0;
  }

  const hx = Math.max(1, cx - margin);
  const hy = Math.max(1, cy - margin);
  const t = Math.min(hx / Math.max(1e-6, Math.abs(dx)), hy / Math.max(1e-6, Math.abs(dy)));
  return { x: cx + dx * t, y: cy + dy * t, angle: Math.atan2(dx, -dy) };
}
