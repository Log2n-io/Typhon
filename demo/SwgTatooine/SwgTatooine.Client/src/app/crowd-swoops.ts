import { Quaternion } from '@babylonjs/core/Maths/math.vector';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import type { Scene } from '@babylonjs/core/scene';
import { placement } from '../assets/asset-frame';
import type { AssetLibrary, Instance } from '../assets/asset-library';
import type { CrowdRider } from '../render/crowd-layer';
import type { SceneLighting } from '../render/scene-lighting';
import { SWOOP_ASSET } from './character-mount';

/** The most swoops drawn under the crowd's riders at once. A rider past it is drawn seated on nothing, which is honest about the cap. */
export const CROWD_SWOOPS = 64;

/** One rider's swoop: its own node, posed like the avatar's (`character-mount.ts`), and the instance under it once loaded. */
interface Ridden {
  readonly node: TransformNode;
  instance: Instance | null;
  /** The frame it was last asked for, so one that stopped riding — or left view — is let go. */
  seenFrame: number;
}

/**
 * The swoops under everyone else's riders — the crowd's half of what the avatar draws for its own (`CharacterMount.setRiding`).
 *
 * <b>Instances, one per rider, not thin instances.</b> Riders are few — a body on a swoop is passing through at forty metres
 * a second — and a swoop is several primitives; a thin-instanced copy would be a second buffer set per primitive for a
 * handful of bodies. The pool is capped, and a swoop is loaded the first time its rider is seen and let go the first frame
 * it is not.
 *
 * Render space, like the crowd: a rider's position is already origin-shifted, so the nodes hang off a plain root.
 */
export class CrowdSwoops {
  private readonly library: AssetLibrary;
  private readonly lighting: SceneLighting;
  private readonly root: TransformNode;
  private readonly byNetId = new Map<number, Ridden>();
  private frame = 0;

  constructor(scene: Scene, library: AssetLibrary, lighting: SceneLighting) {
    this.library = library;
    this.lighting = lighting;
    this.root = new TransformNode('crowd-swoops', scene);
  }

  /** How many swoops are drawn or loading. */
  get count(): number {
    return this.byNetId.size;
  }

  /**
   * Puts a swoop under each rider the crowd drew seated this frame, and lets go of the rest.
   *
   * @param riders The crowd's pooled rider records.
   * @param count How many of them are this frame's.
   */
  update(riders: readonly CrowdRider[], count: number): void {
    this.frame++;
    const canLoad = this.library.catalogue.assets[SWOOP_ASSET] !== undefined;
    for (let i = 0; i < count; i++) {
      const rider = riders[i];
      let ridden = this.byNetId.get(rider.netId);
      if (ridden === undefined) {
        if (!canLoad || this.byNetId.size >= CROWD_SWOOPS) {
          continue;
        }

        ridden = this.load(rider.netId);
      }

      ridden.seenFrame = this.frame;
      // The swoop's origin on the ground, its nose along the rider's heading: the avatar's pose, in the crowd's yaw.
      ridden.node.position.set(rider.x, rider.y, rider.z);
      Quaternion.RotationYawPitchRollToRef(rider.heading, 0, 0, ridden.node.rotationQuaternion!);
      ridden.node.setEnabled(ridden.instance !== null);
    }

    for (const [netId, ridden] of this.byNetId) {
      if (ridden.seenFrame !== this.frame) {
        ridden.instance?.dispose();
        ridden.node.dispose();
        this.byNetId.delete(netId);
      }
    }
  }

  private load(netId: number): Ridden {
    const node = new TransformNode(`crowd-swoop#${String(netId)}`, this.root.getScene());
    node.parent = this.root;
    node.rotationQuaternion = new Quaternion();
    node.setEnabled(false);
    const ridden: Ridden = { node, instance: null, seenFrame: this.frame };
    this.byNetId.set(netId, ridden);
    void this.library
      .instantiate(SWOOP_ASSET, placement([0, 0, 0], 0), node, `swoop#${String(netId)}`)
      .then((placed) => {
        // Let go while it was loading: the GLB is parsed for nothing, and disposed at once.
        if (this.byNetId.get(netId) !== ridden) {
          placed.dispose();
          return;
        }

        ridden.instance = placed;
        this.lighting.castShadows(placed.meshes);
      })
      .catch((error: unknown) => {
        console.warn(`assets: ${SWOOP_ASSET} did not mount for a crowd rider`, error);
      });
    return ridden;
  }

  dispose(): void {
    for (const ridden of this.byNetId.values()) {
      ridden.instance?.dispose();
      ridden.node.dispose();
    }

    this.byNetId.clear();
    this.root.dispose();
  }
}
