import { PLANET_EDGE_M, PLANET_HALF_EXTENT_M } from '../data/world-data';
import type { Heightfield } from '../terrain/heightfield';

/**
 * A world-anchored quadtree over the heightfield, with a measured geometric error per node.
 *
 * ## Why a quadtree and not the clipmap that was here before
 *
 * A clipmap's rings are centred on the camera, so every ring carries a snap step and the whole structure has to be
 * re-anchored as the camera moves — get that wrong and the tessellation crawls across the ground, which is exactly the
 * defect this replaced. **A quadtree node is a fixed cell of the world.** Its corners are constants. Moving the camera
 * changes only *which* nodes are chosen, never where a chosen node's vertices are, so the question of swimming cannot
 * arise rather than being answered carefully.
 *
 * It also buys the thing a clipmap structurally cannot: **LOD per region**. A global error is dominated by the worst
 * ridge on the planet — this field measures 13 m of error at its first decimation because the terrace affector makes real
 * cliffs — and one cliff should not pin the flat basins to the finest mesh.
 *
 * ## The error, and why it is propagated upward
 *
 * A node's own error is the largest vertical gap between the full-resolution field and what this node's grid would
 * interpolate. It is then raised to at least the largest of its children's:
 *
 * > `ε(parent) := max(ε(parent's own), max over children ε(child))`
 *
 * Without that, a coarse node can measure as *more* accurate than the node inside it, the selection walk oscillates
 * between them frame to frame, and the terrain flickers between levels. These are ROAM's nested error bounds, and they are
 * a requirement rather than a refinement.
 */

/** Vertices per side of the grid every node is drawn with. 33 × 33 vertices, 2 048 triangles. */
export const NODE_GRID = 32;

/**
 * Depth of the tree. Eight levels take the root's 16 384 m down to 128 m, whose 32-quad grid is a **4 m vertex spacing —
 * exactly the field's post spacing**, and there is nothing finer to resolve.
 *
 * This constant and {@link POSTS} move together and neither is useful alone. Seven levels bottomed out at 256 m nodes, so
 * raising the field to 4 m posts without this would have had the renderer read every other post and buy nothing; raising
 * this without the field would have had two vertices share a post and buy nothing either. The pair is what the vertex
 * grid being no coarser than the data means.
 */
export const TREE_LEVELS = 8;

/** Nodes across one axis at a level. */
function nodesAcross(level: number): number {
  return 1 << level;
}

/** Metres a node spans at a level. */
export function nodeSizeAt(level: number): number {
  return PLANET_EDGE_M / nodesAcross(level);
}

/** Where a level's nodes start in the flat arrays. */
function levelOffset(level: number): number {
  // 1 + 4 + 16 + … = (4^level − 1) / 3
  return ((1 << (2 * level)) - 1) / 3;
}

/** Total nodes in the tree. */
export const NODE_COUNT = levelOffset(TREE_LEVELS);

/**
 * The tree's per-node data, as parallel arrays.
 *
 * Structure of arrays rather than an object per node: the selection walk touches `error` and the bounds of a few hundred
 * nodes per frame, and an object graph of 21 845 nodes would scatter that across the heap.
 */
export class TerrainQuadtree {
  /** Largest vertical gap, in metres, between this node's grid and the real field — children included. */
  readonly error = new Float32Array(NODE_COUNT);
  /** Lowest and highest ground inside each node, for a bounding box the selection can measure distance to. */
  readonly minY = new Float32Array(NODE_COUNT);
  readonly maxY = new Float32Array(NODE_COUNT);
  /**
   * The largest error at each level, which is what the **morph band** is derived from.
   *
   * Selection stays per node — that is the whole reason this is a quadtree and not a clipmap. The morph cannot be, and
   * the reason is the shared edge: the coarse snap moves an edge vertex *along* the edge as well as across it, so two
   * neighbours blending by different amounts put the same edge vertex in two places and the seam opens onto the sky. A
   * band computed from the level rather than from the node is identical for every node at that level, so neighbours agree
   * by construction. This is why Strugar keys the morph to the LOD level.
   *
   * What it costs, stated rather than discovered later: a node whose parent is much flatter than its level's worst is
   * replaced by that parent before its blend has finished, and that boundary pops. The alternative — a band from the
   * node's own parent — trades that pop for a crack, which is worse. Closing both needs an edge-consistency rule
   * between neighbours, which is a bigger change than this one.
   */
  readonly levelError = new Float32Array(TREE_LEVELS);

  /** The flat index of a node. */
  static indexOf(level: number, ix: number, iz: number): number {
    return levelOffset(level) + iz * nodesAcross(level) + ix;
  }

  /**
   * Measures every node against the field.
   *
   * Done once, in the worker, because it is a pass over the field per level. The result is what the pixel tolerance is
   * spent against at runtime, so a guessed constant here would make the tolerance a number with no units.
   */
  measure(field: Heightfield): void {
    const { posts, spacingM, originM, height } = field.grid;
    this.error.fill(0);
    this.minY.fill(0);
    this.maxY.fill(0);

    for (let level = TREE_LEVELS - 1; level >= 0; level--) {
      const across = nodesAcross(level);
      const size = nodeSizeAt(level);
      // Posts spanned by one node, and the stride between the node grid's own samples.
      const postsPerNode = Math.max(1, Math.round(size / spacingM));
      const step = Math.max(1, Math.round(postsPerNode / NODE_GRID));

      for (let iz = 0; iz < across; iz++) {
        for (let ix = 0; ix < across; ix++) {
          const at = TerrainQuadtree.indexOf(level, ix, iz);
          // A node's world x is `-PLANET_HALF_EXTENT_M + ix * size`, and a post index is `(world - originM) / spacing`
          // — the convention `Heightfield.heightAt` and the ground shader both use. Written with both signs flipped this
          // agreed only because `FIELD_ORIGIN_M === -PLANET_HALF_EXTENT_M`; a windowed bake or a re-centred planet would
          // have measured the wrong cells and keyed every node's LOD to ground it does not cover.
          const px0 = Math.round((ix * size - PLANET_HALF_EXTENT_M - originM) / spacingM);
          const pz0 = Math.round((iz * size - PLANET_HALF_EXTENT_M - originM) / spacingM);
          const px1 = Math.min(px0 + postsPerNode, posts - 1);
          const pz1 = Math.min(pz0 + postsPerNode, posts - 1);

          let worst = 0;
          let low = Infinity;
          let high = -Infinity;
          for (let pz = pz0; pz <= pz1; pz++) {
            const z0 = pz - ((pz - pz0) % step);
            const z1 = Math.min(z0 + step, posts - 1);
            const tz = step === 0 ? 0 : (pz - z0) / step;
            for (let px = px0; px <= px1; px++) {
              const x0 = px - ((px - px0) % step);
              const x1 = Math.min(x0 + step, posts - 1);
              const tx = step === 0 ? 0 : (px - x0) / step;
              const real = height[pz * posts + px];
              if (real < low) {
                low = real;
              }

              if (real > high) {
                high = real;
              }

              const h00 = height[z0 * posts + x0];
              const h10 = height[z0 * posts + x1];
              const h01 = height[z1 * posts + x0];
              const h11 = height[z1 * posts + x1];
              const top = h00 + (h10 - h00) * tx;
              const bottom = h01 + (h11 - h01) * tx;
              const gap = Math.abs(real - (top + (bottom - top) * tz));
              if (gap > worst) {
                worst = gap;
              }
            }
          }

          this.minY[at] = low === Infinity ? 0 : low;
          this.maxY[at] = high === -Infinity ? 0 : high;

          // ROAM's nested bound: never less than any child's, or the walk oscillates between a node and its own children.
          if (level + 1 < TREE_LEVELS) {
            for (let cz = 0; cz < 2; cz++) {
              for (let cx = 0; cx < 2; cx++) {
                const child = TerrainQuadtree.indexOf(level + 1, ix * 2 + cx, iz * 2 + cz);
                if (this.error[child] > worst) {
                  worst = this.error[child]!;
                }
              }
            }
          }

          this.error[at] = worst;
        }
      }
    }

    this.measureLevels();
  }

  /**
   * Fills {@link levelError} from {@link error}.
   *
   * Derived rather than transferred: the worker hands over three arrays and this is one pass over 21 845 floats, so
   * widening the worker protocol to carry it would be a fourth thing to keep in step for no gain.
   */
  private measureLevels(): void {
    for (let level = 0; level < TREE_LEVELS; level++) {
      const across = nodesAcross(level);
      let worst = 0;
      const from = TerrainQuadtree.indexOf(level, 0, 0);
      const to = from + across * across;
      for (let at = from; at < to; at++) {
        if (this.error[at]! > worst) {
          worst = this.error[at]!;
        }
      }

      this.levelError[level] = worst;
    }
  }

  /** Adopts errors and bounds measured elsewhere — the worker measures, the main thread receives. */
  adopt(error: Float32Array, minY: Float32Array, maxY: Float32Array): void {
    this.error.set(error);
    this.minY.set(minY);
    this.maxY.set(maxY);
    this.measureLevels();
  }
}

/** One selected node, as the renderer instances it. */
export interface SelectedNodes {
  /** `(originX, originZ, size, 0)` per node, in planet metres. */
  readonly data: Float32Array;
  /** `(morphStart, morphEnd)` per node, in metres of distance. */
  readonly morph: Float32Array;
  /** How many of the buffers are live this frame. */
  count: number;
  /**
   * Whether the walk ran out of room and had to leave some ground coarser than the tolerance asked for.
   *
   * Worth reporting rather than hiding: past that point dragging the Detail slider finer stops changing anything, and
   * without this the slider simply appears to stop working.
   */
  capped: boolean;
}

/** Allocates the buffers a selection writes into. */
export function selectionBuffers(capacity: number): SelectedNodes {
  return { data: new Float32Array(capacity * 4), morph: new Float32Array(capacity * 2), count: 0, capped: false };
}

/**
 * Where a node stops being accurate enough, in metres from the camera.
 *
 * A node's geometric error `ε` metres, seen from distance `d`, subtends `ε · P / d` pixels, where `P` is the client's
 * `pixelsPerMetre` — viewport height over `2·tan(fov/2)`. Setting that equal to the tolerance `τ` and solving:
 *
 * > `d = ε · P / τ`
 *
 * Closer than that, the node must be replaced by its children. This is Ulrich's `ρ = ε·K/D` and de Boer's `C = A/T`,
 * which are the same relation reached by different routes.
 */
function accurateBeyond(error: number, pixelsPerMetre: number, pixelTolerance: number): number {
  return (error * pixelsPerMetre) / Math.max(pixelTolerance, 0.05);
}

/**
 * Walks the tree and picks the coarsest node everywhere that still meets the tolerance.
 *
 * Descends only where a node is too coarse for how close the camera is, so a flat basin is one big node and a cliff a few
 * metres away is a small one — which a single global level, clipmap or otherwise, cannot express.
 *
 * @param tree The measured tree.
 * @param out Receives the selection.
 * @param camX Camera position in planet metres.
 * @param camY Camera altitude in metres — the distance is 3-D, so standing on the ground and orbiting both work.
 * @param camZ Camera position in planet metres.
 * @param pixelsPerMetre Screen pixels a one-metre object covers at one metre.
 * @param pixelTolerance Pixels of geometric error the viewer accepts.
 * @param maxDistance Nodes entirely beyond this are dropped: the far plane, or the fog's reach.
 */
export function selectNodes(
  tree: TerrainQuadtree,
  out: SelectedNodes,
  camX: number,
  camY: number,
  camZ: number,
  pixelsPerMetre: number,
  pixelTolerance: number,
  maxDistance: number,
): void {
  out.count = 0;
  out.capped = false;
  const capacity = out.data.length / 4;
  visit(tree, out, 0, 0, 0, camX, camY, camZ, pixelsPerMetre, pixelTolerance, maxDistance, capacity, 0);
}

function visit(
  tree: TerrainQuadtree,
  out: SelectedNodes,
  level: number,
  ix: number,
  iz: number,
  camX: number,
  camY: number,
  camZ: number,
  pixelsPerMetre: number,
  pixelTolerance: number,
  maxDistance: number,
  capacity: number,
  reserved: number,
): void {
  const at = TerrainQuadtree.indexOf(level, ix, iz);
  const size = nodeSizeAt(level);
  const x0 = -PLANET_HALF_EXTENT_M + ix * size;
  const z0 = -PLANET_HALF_EXTENT_M + iz * size;
  const near = boxDistance(camX, camY, camZ, x0, z0, size, tree.minY[at], tree.maxY[at]);
  if (near > maxDistance) {
    return;
  }

  const enough = accurateBeyond(tree.error[at], pixelsPerMetre, pixelTolerance);
  // Room for the four children this node would become, or it is drawn as it is.
  //
  // Running out of room has to COARSEN, never puncture. The walk used to return at capacity, which drops the subtree and
  // leaves a hole in the planet with sky behind it — and at 1 px the real selection reached the cap exactly, so this was
  // not hypothetical. Emitting the parent instead costs accuracy where the budget ran out and nothing else.
  // Room for the four children AND for every node still owed a slot elsewhere in the walk.
  //
  // `reserved` is the count of not-yet-visited siblings, of this node and of every ancestor. Each of them will emit at
  // least one node, so descending here without leaving them a slot each is how a depth-first walk spends the whole budget
  // on one corner of the planet and then has nothing left for the rest — which is a hole, with sky behind it. Checking
  // only `count + 4` is not enough, because a child may itself descend and take more than its share.
  const room = out.count + 4 + reserved <= capacity;
  const wouldSplit = level + 1 < TREE_LEVELS && near < enough;
  // Only a split the budget PREVENTED counts as capped. Setting it whenever room merely ran low would report a cap on the
  // last nodes of any selection that happens to end near capacity, which is the opposite of informative.
  if (wouldSplit && !room) {
    out.capped = true;
  }

  if (!wouldSplit || !room) {
    const d = out.count * 4;
    out.data[d] = x0;
    out.data[d + 1] = z0;
    out.data[d + 2] = size;
    out.data[d + 3] = 0;
    // The morph runs over the outer part of the range in which this node is the chosen one, so that by the time the
    // camera is close enough to split it, its grid has already become its parent's and the swap moves nothing.
    const m = out.count * 2;
    // The band runs over the outer part of the range in which this node is the CHOSEN one, so that by the time the camera
    // is far enough to hand it to its parent, its grid has already become its parent's and the swap moves nothing.
    //
    // That range is `[enough(self), replacedAt)`, and `replacedAt` is the distance the PARENT becomes good enough — not
    // this node's own `enough`, which is where the range BEGINS. Deriving the band from `enough` was the defect: a node is
    // only ever emitted when `near >= enough`, so a band ending below `enough` is entirely inside the distances at which
    // the node is not drawn. Every vertex of every node had `toCamera > morphEnd`, `morph` clamped to 1, and the shader
    // used the coarse grid unconditionally — the whole planet drawn at half its vertex density, three quarters of its
    // triangles degenerate, and a pixel tolerance measuring a grid that was not the one on screen.
    const replacedAt = level === 0 ? NEVER_MORPH_M : accurateBeyond(tree.levelError[level - 1]!, pixelsPerMetre, pixelTolerance);
    const start = Math.max(enough, replacedAt * MORPH_BEGIN);
    out.morph[m] = start;
    // The band ENDS a hair before the distance at which this node is replaced, so the morph has already reached 1 when the
    // swap happens. Strugar calls it the error fudge and it is not cosmetic: ending exactly at the switch leaves the blend
    // at 0.999 on the last frame before the swap, which is a hairline crack along the whole LOD boundary.
    out.morph[m + 1] = Math.max(start + 1, replacedAt * (1 - MORPH_FUDGE));
    out.count++;
    return;
  }

  for (let child = 0; child < 4; child++) {
    visit(
      tree,
      out,
      level + 1,
      ix * 2 + (child & 1),
      iz * 2 + (child >> 1),
      camX,
      camY,
      camZ,
      pixelsPerMetre,
      pixelTolerance,
      maxDistance,
      capacity,
      // This child's own siblings, plus whatever its ancestors already owe.
      reserved + (3 - child),
    );
  }
}

/**
 * Where in a node's range the morph toward the parent grid begins, as a fraction of it.
 *
 * Strugar's own figure is around 0.7: early enough that the blend is gradual, late enough that most of a node is drawn at
 * its own detail rather than half-way to its parent's.
 */
export const MORPH_BEGIN = 0.7;

/**
 * The band the root is given, in metres — far enough that `morph` is 0 at every distance the planet is drawn at.
 *
 * The root has no parent, so it has no coarser grid to blend toward and is never replaced by anything. Finite on purpose:
 * `Infinity` in the `Float32Array` makes the shader's `(toCamera - start) / (end - start)` a NaN, and a NaN `mix` factor
 * puts the vertex nowhere.
 */
export const NEVER_MORPH_M = 1e9;

/**
 * How far before the switch distance the morph must be complete, as a fraction of the band.
 *
 * One per cent, Strugar's own figure. Without it the blend reaches 1 exactly as the node is replaced, so on the final
 * frame before the split it is fractionally short and the LOD boundary shows a hairline crack.
 */
export const MORPH_FUDGE = 0.01;

/** Distance from a point to an axis-aligned box, which is what a node is once its height range is known. */
function boxDistance(
  px: number,
  py: number,
  pz: number,
  x0: number,
  z0: number,
  size: number,
  minY: number,
  maxY: number,
): number {
  const dx = Math.max(x0 - px, 0, px - (x0 + size));
  const dz = Math.max(z0 - pz, 0, pz - (z0 + size));
  const dy = Math.max(minY - py, 0, py - maxY);
  return Math.sqrt(dx * dx + dy * dy + dz * dz);
}
