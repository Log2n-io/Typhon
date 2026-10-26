import { describe, expect, it } from 'vitest';
import { FreeCamera } from '@babylonjs/core/Cameras/freeCamera';
import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { Vector3 } from '@babylonjs/core/Maths/math.vector';
import { CreateBox } from '@babylonjs/core/Meshes/Builders/boxBuilder';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import { Scene } from '@babylonjs/core/scene';
import { placeRoot } from '../src/app/asset-mount';
import { GIZMO_GROUP, SCENE_GROUP } from '../src/render/render-groups';
import { configureScene } from '../src/render/scene-setup';

/** A scene as the client configures it, with a camera at the origin looking down +Z. */
function scene(): Scene {
  const s = new Scene(new NullEngine());
  configureScene(s);
  const camera = new FreeCamera('camera', Vector3.Zero(), s);
  camera.setTarget(new Vector3(0, 0, 1));
  return s;
}

function activeNames(s: Scene): string[] {
  s.render();
  const active = s.getActiveMeshes();
  const names: string[] = [];
  for (let i = 0; i < active.length; i++) {
    names.push(active.data[i].name);
  }

  return names.sort();
}

describe('configureScene', () => {
  /** Culling was off from the first day: every placement in range was drawn, the street behind the camera included. */
  it('culls a placement behind the camera, and keeps what culls itself', () => {
    const s = scene();
    CreateBox('ahead', {}, s).position.z = 10;
    CreateBox('behind', {}, s).position.z = -10;
    const own = CreateBox('self-culled', {}, s);
    own.position.z = -10;
    own.alwaysSelectAsActiveMesh = true;

    expect(activeNames(s)).toEqual(['ahead', 'self-culled']);
  });

  /** The terrain draws alone, first; the scene drawn after it must be depth-tested against it, not against a cleared buffer. */
  it("keeps the terrain's depth for the scene group, and clears it for the gizmos", () => {
    const s = scene();
    const clears = (
      s as unknown as { _renderingManager: { _autoClearDepthStencil: Record<number, { autoClear: boolean }> } }
    )._renderingManager._autoClearDepthStencil;
    expect(clears[SCENE_GROUP].autoClear).toBe(false);
    expect(clears[GIZMO_GROUP].autoClear).toBe(true);
  });
});

describe('placeRoot', () => {
  /**
   * `Vector3.set` marks the root dirty whatever it is given, and a dirty root makes every placement under it recompute
   * its world matrix and bounds: 2 251 meshes a frame in a city, for nothing.
   */
  it('leaves the placements alone on a frame the origin did not move, and moves them when it snaps', () => {
    const s = scene();
    const root = new TransformNode('assets', s);
    const house = CreateBox('house', {}, s);
    house.parent = root;
    // A frame as the app runs one: the origin written, then the placement's matrix asked for under a new render id —
    // Babylon caches a world matrix within one.
    const frame = (x: number, z: number): void => {
      placeRoot(root, x, z);
      s.incrementRenderId();
      house.computeWorldMatrix();
    };
    frame(1000, 2000);
    const settled = house.getWorldMatrix().updateFlag;

    for (let k = 0; k < 3; k++) {
      frame(1000, 2000);
    }

    expect(house.getWorldMatrix().updateFlag, 'recomputed with nothing changed').toBe(settled);

    frame(2000, 2000);
    expect(house.getWorldMatrix().updateFlag).not.toBe(settled);
    expect(house.getAbsolutePosition().x).toBe(-2000);
  });
});
