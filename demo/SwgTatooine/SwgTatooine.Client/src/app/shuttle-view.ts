import { Quaternion } from '@babylonjs/core/Maths/math.vector';
import type { TransformNode } from '@babylonjs/core/Meshes/transformNode';
import { placement } from '../assets/asset-frame';
import type { AssetLibrary, Instance } from '../assets/asset-library';
import type { SceneLighting } from '../render/scene-lighting';
import type { FlightPose } from './shuttle-flight';

/** The published shuttle. */
export const SHUTTLE_ASSET = 'shuttle';

/**
 * The shuttle of the shuttleport nearest the viewer, drawn where its flight puts it (`shuttle-flight.ts`).
 *
 * <b>One, and cosmetic.</b> The server's shuttle is a schedule and nothing more: it has no entity and no colliders, so
 * nothing stands on it and nothing is dropped onto a body on the pad (the testbench's gotcha). Only the port the viewer
 * is near is drawn — a flight is 14 s across a few hundred metres, and the ports are kilometres apart.
 */
export class ShuttleView {
  private readonly library: AssetLibrary;
  private readonly parent: TransformNode;
  private readonly lighting: SceneLighting;
  private instance: Instance | null = null;
  private loading = false;

  constructor(library: AssetLibrary, parent: TransformNode, lighting: SceneLighting) {
    this.library = library;
    this.parent = parent;
    this.lighting = lighting;
  }

  /** Whether the shuttle is drawn this frame. */
  get visible(): boolean {
    return this.instance?.root.isEnabled() ?? false;
  }

  /**
   * Draws the shuttle at a flight pose over a pad, or hides it.
   *
   * @param padX The pad's centre, east, planet metres.
   * @param padY The pad's top.
   * @param padZ The pad's centre, north.
   * @param flight Where the shuttle is relative to the pad, or `null` while it is nowhere near.
   */
  update(padX: number, padY: number, padZ: number, flight: FlightPose | null): void {
    if (flight === null) {
      this.instance?.root.setEnabled(false);
      return;
    }

    const instance = this.instance;
    if (instance === null) {
      this.load();
      return;
    }

    const root = instance.root;
    root.setEnabled(true);
    // The pad is never turned (the shuttleport keeps yaw 0), so the pad frame is the world's.
    root.position.set(padX + flight.x, padY + flight.y, padZ + flight.z);
    root.rotationQuaternion ??= new Quaternion();
    // Babylon's +X pitch turns the nose DOWN (the testbench's note), so a nose-up pose is a negative pitch.
    Quaternion.RotationYawPitchRollToRef(flight.yaw, -flight.noseUp, 0, root.rotationQuaternion);
  }

  private load(): void {
    if (this.loading || this.library.catalogue.assets[SHUTTLE_ASSET] === undefined) {
      return;
    }

    this.loading = true;
    void this.library
      .instantiate(SHUTTLE_ASSET, placement([0, 0, 0], 0), this.parent, 'shuttle#flight')
      .then((placed) => {
        this.instance = placed;
        placed.root.setEnabled(false);
        this.lighting.castShadows(placed.meshes);
      })
      .catch((error: unknown) => {
        console.warn(`assets: ${SHUTTLE_ASSET} did not mount; shuttles fly unseen`, error);
      });
  }

  dispose(): void {
    this.instance?.dispose();
    this.instance = null;
  }
}
