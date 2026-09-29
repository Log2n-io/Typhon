import '@babylonjs/core/Meshes/thinInstanceMesh';
import { Constants } from '@babylonjs/core/Engines/constants';
import { RawTexture } from '@babylonjs/core/Materials/Textures/rawTexture';
import { Texture } from '@babylonjs/core/Materials/Textures/texture';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { VertexData } from '@babylonjs/core/Meshes/mesh.vertexData';
import type { Scene } from '@babylonjs/core/scene';
import type { Heightfield } from '../terrain/heightfield';
import { PrefixUploader } from './prefix-upload';
import { NODE_GRID, TerrainQuadtree, selectNodes, selectionBuffers, type SelectedNodes } from './terrain-quadtree';

/**
 * The ground's geometry: **CDLOD** — one small grid mesh, thin-instanced once per selected quadtree node, displaced in the
 * vertex shader and morphed toward its parent's grid near the LOD boundary.
 *
 * ## Why this replaced a camera-centred mesh, twice
 *
 * The first attempt was a single grid whose spacing grew geometrically outward, re-centred on the camera. Almost none of
 * its vertex offsets were multiples of its snap step, so **translating** the camera moved every vertex to a different
 * patch of ground and the tessellation visibly crawled. The second was a clipmap: nested rings, each snapped to its own
 * spacing, which fixes the crawl but leaves two problems a clipmap has by construction — adjacent rings snap to different
 * grids, so their edges do not line up without an L-shaped trim strip, and the whole map runs at one global level, so the
 * worst cliff on the planet pins the flattest basin to the finest mesh.
 *
 * A CDLOD node is a **fixed cell of the world**. Its corners are constants; moving the camera changes only which nodes are
 * chosen. Swimming cannot arise rather than being prevented, there are no rings to align, and the level is chosen per
 * region against that region's own measured error.
 *
 * ## What the vertex shader does
 *
 * Each instance carries `(originX, originZ, size, _)` and `(morphStart, morphEnd)`. A vertex's grid coordinate is a
 * fraction of the node, so its planet position is `origin + grid · size` — no snapping, because the node is already
 * anchored. Near the far edge of the node's range the vertex is blended onto its **parent's** grid, by dropping the odd
 * half of its grid coordinate, so that when the node is eventually split or merged nothing moves. That is what removes
 * both the crack at a level boundary and the pop.
 */

/**
 * How many nodes a frame may draw.
 *
 * It was 2048 and described as slack. At 4 m posts, eight tree levels and 450 m of relief it stopped being slack: the
 * measured selection at a 1 px tolerance was **exactly 2048**, which is what hitting a cap looks like. 8192 costs 196 KB
 * of instance buffer and leaves the default 2 px setting (~1 200 nodes) five times under it.
 *
 * The cap is a budget, not a guarantee of accuracy: past it {@link selectNodes} coarsens, and says so through
 * {@link SelectedNodes.capped}.
 */
export const MAX_NODES = 8192;

export { NODE_GRID };

/** How the terrain is drawn this frame. The caller keeps one and rewrites it, so a frame allocates nothing. */
export class TerrainView {
  /** Render origin, subtracted from planet coordinates to keep float32 precision near the eye. */
  originX = 0;
  originZ = 0;
  /** Camera position in planet metres, and its altitude: the selection measures a 3-D distance to each node's box. */
  camX = 0;
  camY = 0;
  camZ = 0;
  /** Screen pixels a one-metre object covers at one metre — the perspective half of the error projection. */
  pixelsPerMetre = 1000;
  /** Pixels of geometric error the viewer accepts. Smaller selects more, smaller nodes. */
  pixelTolerance = 2;
  /** Nodes entirely beyond this are dropped. */
  maxDistanceM = 40_000;
}

/**
 * The node mesh, its instances, and the height texture they sample.
 *
 * The texture is **R32F, sampled NEAREST**, and the shader does its own bilinear blend of four texels — the same four
 * texels and the same weights as `Heightfield.heightAt` on the CPU, so the GPU's displaced ground and the CPU's entity
 * altitudes land in the same place.
 */
export class TerrainMesh {
  readonly mesh: Mesh;
  readonly texture: RawTexture;
  /** `originM`, `spacingM`, `posts`, `1 / posts` — what the shader needs to index the field. */
  readonly fieldUniform: Float32Array;
  /** The quadtree the selection walks. */
  readonly tree = new TerrainQuadtree();
  /** Triangles drawn last frame: nodes × the node grid. This is what the tolerance actually moves. */
  triangleCount = 0;
  /** Nodes selected last frame. */
  nodeCount = 0;
  /** The finest node size the last selection reached, in metres — the HUD's "detail here" number. */
  finestNodeM = 0;
  /** Whether the last selection ran out of node budget and left ground coarser than asked. */
  capped = false;
  private readonly field: Heightfield;
  private readonly selection: SelectedNodes = selectionBuffers(MAX_NODES);
  private readonly trianglesPerNode: number;
  private readonly dataUploader = new PrefixUploader(this.selection.data, 4);
  private readonly morphUploader = new PrefixUploader(this.selection.morph, 2);

  constructor(scene: Scene, field: Heightfield) {
    this.field = field;
    const grid = field.grid;
    const built = buildNodeMesh(scene);
    this.mesh = built.mesh;
    this.trianglesPerNode = built.triangles;
    this.texture = new RawTexture(
      grid.height,
      grid.posts,
      grid.posts,
      Constants.TEXTUREFORMAT_R,
      scene,
      false,
      false,
      Texture.NEAREST_SAMPLINGMODE,
      Constants.TEXTURETYPE_FLOAT,
    );
    this.texture.wrapU = Texture.CLAMP_ADDRESSMODE;
    this.texture.wrapV = Texture.CLAMP_ADDRESSMODE;
    this.fieldUniform = new Float32Array([grid.originM, grid.spacingM, grid.posts, 1 / grid.posts]);
    this.mesh.thinInstanceSetBuffer('nodeData', this.selection.data, 4, false);
    this.mesh.thinInstanceSetBuffer('nodeMorph', this.selection.morph, 2, false);
    this.mesh.forcedInstanceCount = 0;
    this.mesh.isVisible = false;
  }

  /** Re-uploads the heights after a re-bake, and takes the tree the worker measured. */
  refresh(error?: Float32Array, minY?: Float32Array, maxY?: Float32Array): void {
    this.texture.update(this.field.grid.height);
    if (error !== undefined && minY !== undefined && maxY !== undefined) {
      this.tree.adopt(error, minY, maxY);
    } else {
      this.tree.measure(this.field);
    }
  }

  /**
   * Selects this frame's nodes and uploads them.
   *
   * @param view What the camera is doing.
   */
  place(view: TerrainView): void {
    selectNodes(
      this.tree,
      this.selection,
      view.camX,
      view.camY,
      view.camZ,
      view.pixelsPerMetre,
      view.pixelTolerance,
      view.maxDistanceM,
    );

    this.nodeCount = this.selection.count;
    this.capped = this.selection.capped;
    this.triangleCount = this.selection.count * this.trianglesPerNode;
    let finest = Infinity;
    for (let i = 0; i < this.selection.count; i++) {
      const size = this.selection.data[i * 4 + 2];
      if (size < finest) {
        finest = size;
      }
    }

    this.finestNodeM = finest === Infinity ? 0 : finest;
    // Only the live prefix reaches the GPU; the buffers are allocated once at capacity. Same path the entity layers and
    // the attack lines use, so one uploader bug would show everywhere rather than only here.
    this.dataUploader.upload(this.mesh.getVertexBuffer('nodeData')?.getWrapperBuffer() ?? null, this.selection.count);
    this.morphUploader.upload(this.mesh.getVertexBuffer('nodeMorph')?.getWrapperBuffer() ?? null, this.selection.count);
    this.mesh.forcedInstanceCount = this.selection.count;
    this.mesh.isVisible = this.selection.count > 0;
    this.mesh.position.set(-view.originX, 0, -view.originZ);
  }

  dispose(): void {
    this.texture.dispose();
    this.mesh.dispose();
  }
}

/**
 * The one mesh every node is drawn with: a unit grid, `NODE_GRID` quads a side.
 *
 * Its vertices are the node's own fractions, `0…1` — nothing here knows a size or a place in the world, so the same
 * buffer serves a 256 m node under the camera and a 16 km one at the horizon.
 */
function buildNodeMesh(scene: Scene): { mesh: Mesh; triangles: number } {
  const side = NODE_GRID + 1;
  const positions = new Float32Array(side * side * 3);
  for (let j = 0; j < side; j++) {
    for (let i = 0; i < side; i++) {
      const at = (j * side + i) * 3;
      positions[at] = i / NODE_GRID;
      positions[at + 1] = 0;
      positions[at + 2] = j / NODE_GRID;
    }
  }

  const indices = new Uint32Array(NODE_GRID * NODE_GRID * 6);
  let at = 0;
  for (let j = 0; j < NODE_GRID; j++) {
    for (let i = 0; i < NODE_GRID; i++) {
      const a = j * side + i;
      indices[at++] = a;
      indices[at++] = a + side;
      indices[at++] = a + side + 1;
      indices[at++] = a;
      indices[at++] = a + side + 1;
      indices[at++] = a + 1;
    }
  }

  const mesh = new Mesh('terrain', scene);
  const data = new VertexData();
  data.positions = positions;
  data.indices = indices;
  data.applyToMesh(mesh, false);
  // Instances are placed in the vertex shader from their own attributes, so Babylon's bounding box means nothing here and
  // culling by it would drop the whole terrain.
  mesh.alwaysSelectAsActiveMesh = true;
  // Babylon's own picking is never used — `view-math.ts` projects instances itself.
  mesh.isPickable = false;
  return { mesh, triangles: NODE_GRID * NODE_GRID * 2 };
}
