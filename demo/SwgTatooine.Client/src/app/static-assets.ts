import type { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import { placement } from '../assets/asset-frame';
import type { AssetLibrary, Instance } from '../assets/asset-library';
import { assetForStructure, structureYaw } from '../assets/structure-assets';
import type { AssetSet } from '../data/server-config';
import type { SceneLighting } from '../render/scene-lighting';

/**
 * The structures the server placed, drawn as published assets.
 *
 * <b>This is the half of the renderer that the asset pipeline is for.</b> `render/entity-layer.ts` evaluates thousands
 * of positions a frame and packs them into one thin-instance buffer per archetype — the right shape for a crowd whose
 * every member is moving. A building is the opposite: it never moves, there are tens of them near a player rather than
 * thousands, and what matters is that it looks like a building. So a structure in range gets a real instance in the
 * scene graph, and the packer is told to skip it.
 *
 * <b>Instances are created and destroyed by RANGE, not by frustum.</b> Turning on the spot would otherwise unload the
 * street behind you and reload it a second later — a GLB parse and a GPU upload each time, for geometry that was
 * already resident. What a session holds at all is already bounded by its interest radius, so this is a second, smaller
 * bound inside it.
 *
 * <b>Loading is budgeted per frame</b> because `LoadAssetContainerAsync` parses and uploads on the main thread. The
 * first frame in a city would otherwise try to start forty of them at once, and the frame they all land on is a visible
 * stall rather than a gradual fill.
 */

/** What the app reads off one structure for this layer: identity, where it stands, and what it is. */
export interface StructureSample {
  readonly netId: number;
  readonly x: number;
  /** Ground altitude under it — a building's origin sits ON the ground, its foundation skirt below. */
  readonly y: number;
  readonly z: number;
  readonly kind: number;
  /** The replicated quarter turn; see `structureYaw`. */
  readonly yaw: number;
  /** The replicated asset index; see `assetForStructure`. */
  readonly asset: number;
}

/** A placed instance and the structure it stands for. */
interface Standing {
  readonly instance: Instance;
  readonly asset: string;
  readonly x: number;
  readonly z: number;
  readonly yaw: number;
}

export class StaticAssets {
  private readonly library: AssetLibrary;
  /** Marks each placement as a shadow caster, or `null` on a mount without lighting (the tests'). */
  private readonly lighting: SceneLighting | null;
  private readonly set: AssetSet;
  private readonly root: TransformNode;
  private readonly budget: number;
  /** Live instances by netId, with what each one stands for so a renumbered id is caught. */
  private readonly placed = new Map<number, Standing>();
  /** Loads in flight, so a second frame does not start the same one again. */
  private readonly loading = new Set<number>();
  /** What this frame's samples asked for, reused so a frame allocates nothing. */
  private readonly wanted = new Set<number>();
  /** What is on screen, handed to the packer as the set it must not draw. */
  private readonly drawn = new Set<number>();
  private disposed = false;

  constructor(
    library: AssetLibrary,
    root: TransformNode,
    set: AssetSet,
    budgetPerFrame = 2,
    lighting: SceneLighting | null = null,
  ) {
    this.set = set;
    this.library = library;
    this.root = root;
    this.budget = budgetPerFrame;
    this.lighting = lighting;
  }

  /** How many structures are drawn as assets, for the HUD and for a test. */
  get count(): number {
    return this.placed.size;
  }

  /** How many are being loaded right now. */
  get pending(): number {
    return this.loading.size;
  }

  /**
   * Brings the placed set in line with what is in range.
   *
   * @param samples Every structure the app wants drawn as an asset this frame.
   * @returns The netIds that have an instance, which the packer must skip.
   */
  update(samples: readonly StructureSample[]): ReadonlySet<number> {
    if (this.disposed) {
      return this.drawn;
    }

    this.wanted.clear();
    let started = 0;
    for (const sample of samples) {
      const id = assetForStructure(this.set, sample.asset);
      if (id === null) {
        continue;
      }

      this.wanted.add(sample.netId);
      const standing = this.placed.get(sample.netId);
      if (standing !== undefined) {
        // <b>A netId is not an identity across a view RESET.</b> The server renumbers every entity on one, so an id
        // can come back naming a different structure — and an instance kept on the id alone would stand as the wrong
        // building at the wrong place, while suppressing the new entity's own shape through `drawn`.
        if (
          standing.asset === id &&
          standing.x === sample.x &&
          standing.z === sample.z &&
          standing.yaw === sample.yaw
        ) {
          continue;
        }

        standing.instance.dispose();
        this.placed.delete(sample.netId);
        this.drawn.delete(sample.netId);
      }

      if (this.loading.has(sample.netId) || this.library.hasFailed(id)) {
        continue;
      }

      if (started >= this.budget) {
        continue;
      }

      started++;
      this.begin(sample, id);
    }

    // Everything that was in range and is not any more. A load in flight for one of them is left alone: it is cheaper to
    // let it land and dispose it on the next frame than to carry a cancellation through the loader.
    for (const [netId, standing] of this.placed) {
      if (!this.wanted.has(netId)) {
        standing.instance.dispose();
        this.placed.delete(netId);
        this.drawn.delete(netId);
      }
    }

    return this.drawn;
  }

  dispose(): void {
    this.disposed = true;
    for (const standing of this.placed.values()) {
      standing.instance.dispose();
    }

    this.placed.clear();
    this.loading.clear();
    this.drawn.clear();
  }

  private begin(sample: StructureSample, id: string): void {
    if (this.library.catalogue.assets[id] === undefined) {
      // A catalogue that does not publish it: the renderer's own shape stands, which is what happens for every kind
      // that has no asset at all. Not counted as pending, or the budget would be spent on it every frame.
      return;
    }

    const netId = sample.netId;
    this.loading.add(netId);
    const pose = placement([sample.x, sample.y, sample.z], structureYaw(sample.yaw));
    this.library
      .instantiate(id, pose, this.root, `${id}#${String(netId)}`)
      .then((instance) => {
        this.loading.delete(netId);
        // It may have left range, or the whole layer may be gone, while the GLB was parsing.
        if (this.disposed || !this.wanted.has(netId)) {
          instance.dispose();
          return;
        }

        // Marked where it is PLACED, because that is the mesh the shadow map can draw: the container's own meshes
        // are never in the scene. Nothing has to unmark it — a placement that leaves range is disposed, and the map's
        // predicate reads the scene.
        this.lighting?.castShadows(instance.meshes);
        this.placed.set(netId, { instance, asset: id, x: pose.pos[0], z: pose.pos[2], yaw: sample.yaw });
        this.drawn.add(netId);
      })
      .catch((error: unknown) => {
        this.loading.delete(netId);
        // One asset failing is not a reason for the street to stop drawing: the renderer's own shape stands in its
        // place, and the id is named so the failure is diagnosable rather than merely visible.
        console.error(`assets: ${id} not placed`, error);
      });
  }
}
