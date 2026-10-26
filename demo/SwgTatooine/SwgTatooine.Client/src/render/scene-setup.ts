import type { Scene } from '@babylonjs/core/scene';
import { SCENE_GROUP } from './render-groups';

/**
 * The scene-wide switches the client sets once, before anything is drawn.
 *
 * <b>Frustum culling is ON.</b> It was off from the client's first day (`skipFrustumClipping`), when the scene held only
 * layers that cull themselves on the CPU. The published placements came later — every building part, lair, terminal and
 * shuttle within range is a mesh of its own — and with culling off each of them was drawn every frame, the street behind
 * the camera included: measured, 632 active meshes of 632 enabled in a city, 4 151 of 4 151 over the planet.
 *
 * What culls itself keeps doing so: the entity layers, the crowd and its weapons, the terrain, the attack lines, the
 * combat effects and the damage text all draw from instance buffers whose bounds Babylon cannot know, and each sets
 * `alwaysSelectAsActiveMesh` — without it, culling by a stale or bind-pose box would drop bodies that are on screen.
 *
 * <b>The scene group keeps the depth the terrain left</b> (`TERRAIN_GROUP`, drawn first). Babylon clears depth before
 * every group by default, which would let everything draw through the ground; the gizmo group still clears, so a gizmo
 * shows through what it describes.
 *
 * @param scene The live scene.
 */
export function configureScene(scene: Scene): void {
  // The camera input owns the canvas: Babylon's own pointer handling would only pick meshes we never let it pick.
  scene.detachControl();
  scene.skipPointerMovePicking = true;
  scene.skipFrustumClipping = false;
  scene.setRenderingAutoClearDepthStencil(SCENE_GROUP, false);
}
