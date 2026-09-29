import { TerrainQuadtree } from '../render/terrain-quadtree';
import { finishBake } from './band-bake';
import { Heightfield } from './heightfield';
import { adoptHeightGrid } from './layers';

/**
 * Finishes an assembled planet: the town pads, the min/max scan, and the quadtree's per-node error.
 *
 * The landform itself is baked by {@link terrain-band.worker} on as many threads as the machine has — see `band-bake.ts`
 * for why a band is exact. What is left is the part that cannot be banded and the part that has to see the finished
 * field:
 *
 * - **Pads** level each town to the height the landform reached at the site's own centre, which may lie in another band.
 * - **The quadtree's error** is a pass over the whole field per level, and it is what the renderer's pixel tolerance is
 *   spent against — a guessed constant would make the tolerance a number with no units.
 *
 * This runs in a worker rather than on the main thread for the same reason the bands do: it is roughly a second of
 * straight-line work at the shipping resolution, and a second of dropped frames to draw a hill is not a trade worth
 * making. The client renders a flat planet from the first frame and the relief arrives when it arrives.
 */

declare const self: {
  postMessage(message: TerrainBaked, transfer: Transferable[]): void;
  onmessage: ((event: MessageEvent<FinishRequest>) => void) | null;
};

/** The assembled landform, handed over by transfer. */
export interface FinishRequest {
  /** `posts × posts` heights, every band written in. Transferred, so the sender no longer owns it. */
  readonly height: Float32Array;
  readonly posts: number;
  readonly spacingM: number;
  readonly originM: number;
}

export interface TerrainBaked {
  readonly kind: 'baked';
  /** `posts × posts` heights in metres, row-major with z outer. */
  readonly height: Float32Array;
  readonly posts: number;
  readonly spacingM: number;
  readonly originM: number;
  /** Wall-clock milliseconds the whole bake took, for the HUD and for anyone who doubts the number above. */
  readonly bakeMs: number;
  /**
   * The quadtree's per-node geometric error in metres, and each node's height range.
   *
   * Per NODE rather than per level because a global figure is dominated by the worst cliff on the planet, which would pin
   * the flattest basin to the finest mesh.
   */
  readonly nodeError: Float32Array;
  readonly nodeMinY: Float32Array;
  readonly nodeMaxY: Float32Array;
}

self.onmessage = (event: MessageEvent<FinishRequest>): void => {
  const { height, posts, spacingM, originM } = event.data;
  const field = new Heightfield(adoptHeightGrid(posts, spacingM, originM, height));
  finishBake(field);
  const tree = new TerrainQuadtree();
  tree.measure(field);
  self.postMessage(
    {
      kind: 'baked',
      height: field.grid.height,
      posts,
      spacingM,
      originM,
      // The caller owns the clock: it started before the first band did, and this thread has no idea when that was.
      bakeMs: 0,
      nodeError: tree.error,
      nodeMinY: tree.minY,
      nodeMaxY: tree.maxY,
    },
    [field.grid.height.buffer, tree.error.buffer, tree.minY.buffer, tree.maxY.buffer],
  );
};
