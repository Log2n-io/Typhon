import { describe, expect, it } from 'vitest';
import { PLANET_EDGE_M } from '../src/data/world-data';
import { NODE_GRID } from '../src/render/terrain-mesh';
import {
  MORPH_BEGIN,
  TREE_LEVELS,
  TerrainQuadtree,
  selectNodes,
  selectionBuffers,
} from '../src/render/terrain-quadtree';
import { Heightfield } from '../src/terrain/heightfield';
import { createHeightGrid } from '../src/terrain/layers';

/**
 * The terrain's level-of-detail: a world-anchored quadtree, a measured error per node, and a selection driven by a pixel
 * tolerance.
 *
 * None of this needs a GPU, which is the point of having it on the CPU: the two defects this replaced — a tessellation
 * that slid across the ground when the camera translated, and a global error that pinned the whole planet to its worst
 * cliff — are both properties of the selection, not of the shading.
 */

/** A field with a cliff in one corner and flat ground everywhere else: the case a global error metric cannot express. */
function cliffInOneCorner(): Heightfield {
  const field = new Heightfield(createHeightGrid(256, 64, -8192));
  const posts = field.grid.posts;
  for (let z = 0; z < posts; z++) {
    for (let x = 0; x < posts; x++) {
      field.grid.height[z * posts + x] = x < 24 && z < 24 ? (x % 2 === 0 ? 60 : 0) : 0;
    }
  }

  field.measure();
  return field;
}

/**
 * Rough everywhere, so the selection genuinely wants more nodes than a small budget holds.
 *
 * {@link cliffInOneCorner} cannot test the budget: its flat 99 % measures zero error, so the walk emits ten nodes however
 * much room it is given, and a capacity test against it passes whatever the code does. That is what it did.
 */
function roughEverywhere(): Heightfield {
  const field = new Heightfield(createHeightGrid(256, 64, -8192));
  const posts = field.grid.posts;
  for (let z = 0; z < posts; z++) {
    for (let x = 0; x < posts; x++) {
      field.grid.height[z * posts + x] = ((x * 7 + z * 13) % 11) * 9 - ((x * z) % 5) * 7;
    }
  }

  field.measure();
  return field;
}

describe('the terrain quadtree', () => {
  it('measures a real error where the terrain is rough and none where it is flat', () => {
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    // The root sees the cliff through the nested bound; a leaf on the far side of the planet sees nothing.
    expect(tree.error[TerrainQuadtree.indexOf(0, 0, 0)]).toBeGreaterThan(10);
    const across = 1 << (TREE_LEVELS - 1);
    expect(tree.error[TerrainQuadtree.indexOf(TREE_LEVELS - 1, across - 1, across - 1)]).toBe(0);
  });

  it('never lets a parent claim less error than its child — ROAM nested bounds', () => {
    // Without this the walk oscillates: a coarse node measures as more accurate than the node inside it, so the selection
    // flips between them frame to frame and the terrain flickers between levels.
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    for (let level = 0; level + 1 < TREE_LEVELS; level++) {
      const across = 1 << level;
      for (let iz = 0; iz < across; iz++) {
        for (let ix = 0; ix < across; ix++) {
          const parent = tree.error[TerrainQuadtree.indexOf(level, ix, iz)];
          for (let cz = 0; cz < 2; cz++) {
            for (let cx = 0; cx < 2; cx++) {
              const child = tree.error[TerrainQuadtree.indexOf(level + 1, ix * 2 + cx, iz * 2 + cz)];
              expect(parent, `level ${level} node (${ix}, ${iz})`).toBeGreaterThanOrEqual(child);
            }
          }
        }
      }
    }
  });

  it('splits where the ground is rough and leaves the flat side coarse — what one global level cannot do', () => {
    // The whole reason for a quadtree rather than a clipmap. One cliff must not pin the flat basin to the finest mesh.
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    const out = selectionBuffers(8192);
    selectNodes(tree, out, 0, 400, 0, 1200, 2, 1e9);

    let smallestByTheCliff = Infinity;
    let smallestFarFromIt = Infinity;
    for (let i = 0; i < out.count; i++) {
      const x = out.data[i * 4];
      const z = out.data[i * 4 + 1];
      const size = out.data[i * 4 + 2];
      if (x < -7000 && z < -7000) {
        smallestByTheCliff = Math.min(smallestByTheCliff, size);
      }

      if (x > 4000 && z > 4000) {
        smallestFarFromIt = Math.min(smallestFarFromIt, size);
      }
    }

    expect(smallestByTheCliff).toBeLessThan(smallestFarFromIt);
  });

  it('selects fewer nodes as the tolerance loosens, and never none', () => {
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    const out = selectionBuffers(8192);
    let previous = Infinity;
    for (const tolerance of [0.5, 1, 2, 4, 8, 16]) {
      selectNodes(tree, out, 0, 200, 0, 1200, tolerance, 1e9);
      expect(out.count, `tolerance ${tolerance}`).toBeLessThanOrEqual(previous);
      expect(out.count).toBeGreaterThan(0);
      previous = out.count;
    }
  });

  it('covers the planet exactly once — no gap to show sky through, no overlap to z-fight', () => {
    // Every selected node is a quadtree cell, so their areas must sum to the root's. Both failures are invisible in a
    // screenshot until you already suspect them.
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    const out = selectionBuffers(16384);
    selectNodes(tree, out, 1234, 300, -5678, 900, 2, 1e9);

    let area = 0;
    for (let i = 0; i < out.count; i++) {
      const size = out.data[i * 4 + 2];
      area += size * size;
    }

    expect(area).toBeCloseTo(PLANET_EDGE_M * PLANET_EDGE_M, -3);
  });

  it('drops what is past the far distance rather than drawing the whole planet every frame', () => {
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    const all = selectionBuffers(16384);
    const near = selectionBuffers(16384);
    selectNodes(tree, all, 0, 100, 0, 900, 2, 1e9);
    selectNodes(tree, near, 0, 100, 0, 900, 2, 3000);
    expect(near.count).toBeLessThan(all.count);
    expect(near.count).toBeGreaterThan(0);
  });

  it('coarsens when it runs out of node budget rather than leaving a hole in the planet', () => {
    // The walk used to return at capacity, which drops a whole subtree and shows sky through the ground. At 4 m posts and
    // eight levels the real selection reached the 2 048-node cap EXACTLY at a 1 px tolerance, so this was live. Coverage
    // is the property that has to survive: the selected areas must still sum to the planet, budget or no budget.
    const tree = new TerrainQuadtree();
    tree.measure(roughEverywhere());
    const roomy = selectionBuffers(16384);
    selectNodes(tree, roomy, 0, 40, 0, 4000, 0.5, 1e9);
    // The test is only worth anything if the unbounded walk wants more than the small budget below.
    expect(roomy.count).toBeGreaterThan(40);
    expect(roomy.capped).toBe(false);

    const tiny = selectionBuffers(40);
    selectNodes(tree, tiny, 0, 40, 0, 4000, 0.5, 1e9);
    expect(tiny.count).toBeLessThanOrEqual(40);
    expect(tiny.capped).toBe(true);

    let area = 0;
    for (let i = 0; i < tiny.count; i++) {
      const size = tiny.data[i * 4 + 2];
      area += size * size;
    }

    expect(area).toBeCloseTo(PLANET_EDGE_M * PLANET_EDGE_M, -3);
  });

  it('does not report a cap merely because the selection ended near capacity', () => {
    // `capped` drives a warning in the HUD, so a false positive is worse than no readout. A budget of exactly the number
    // of nodes the walk wants is not a cap: nothing was prevented.
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    const roomy = selectionBuffers(16384);
    selectNodes(tree, roomy, 0, 400, 0, 1200, 2, 1e9);
    expect(roomy.capped).toBe(false);

    const exact = selectionBuffers(roomy.count);
    selectNodes(tree, exact, 0, 400, 0, 1200, 2, 1e9);
    expect(exact.count).toBe(roomy.count);
    expect(exact.capped).toBe(false);
  });

  it('applies the projection: a node is kept beyond its switch distance and replaced inside it', () => {
    // A node's error subtends `ε · pixelsPerMetre / d` pixels, so it stops being good enough at `d = ε · P / τ`. Straddle
    // that distance and the selection must change on the right side of it.
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    const ppm = 1000;
    const tolerance = 2;
    const switchAt = (tree.error[0] * ppm) / tolerance;
    const out = selectionBuffers(16384);

    // Far beyond it, the root alone is accurate enough and is the only node drawn.
    selectNodes(tree, out, 0, switchAt * 2, 0, ppm, tolerance, 1e9);
    expect(out.count).toBe(1);
    expect(out.data[2]).toBe(PLANET_EDGE_M);

    // Well inside it, it is not.
    selectNodes(tree, out, 0, switchAt / 8, 0, ppm, tolerance, 1e9);
    expect(out.count).toBeGreaterThan(1);
  });

  it('gives a drawn node a morph that is LIVE at the distance it is drawn', () => {
    // The test the morph never had, and the reason a total defect survived a green suite.
    //
    // Its predecessors asserted the band's SHAPE — `end > start`, `end < the node's own switch distance`, `end > half of
    // it`. Every one of those passes while the band sits entirely inside the distances at which the node is NEVER DRAWN,
    // which is what it did: a node is only emitted when `near >= enough`, and the band ran from `0.7 * enough` to
    // `0.997 * enough`. So every vertex of every node had `toCamera > morphEnd`, `morph` clamped to 1, and the shader used
    // the coarse grid unconditionally — the planet drawn at half its vertex density with three quarters of its triangles
    // collapsed, while the tolerance measured a grid that was not on screen.
    //
    // So this one does not look at the band. It evaluates the shader's own expression at the nearest vertex each drawn
    // node can have, and requires that the blend is somewhere other than hard against its stop.
    const tree = new TerrainQuadtree();
    tree.measure(roughEverywhere());
    const out = selectionBuffers(16384);
    const camX = 0;
    const camY = 400;
    const camZ = 0;
    selectNodes(tree, out, camX, camY, camZ, 1200, 2, 1e9);
    expect(out.count).toBeGreaterThan(4);

    let live = 0;
    for (let i = 0; i < out.count; i++) {
      const x0 = out.data[i * 4]!;
      const z0 = out.data[i * 4 + 1]!;
      const size = out.data[i * 4 + 2]!;
      // The closest point of this node's footprint to the camera: the smallest `toCamera` any of its vertices can take.
      const nx = Math.min(Math.max(camX, x0), x0 + size);
      const nz = Math.min(Math.max(camZ, z0), z0 + size);
      const toCamera = Math.sqrt((nx - camX) ** 2 + camY ** 2 + (nz - camZ) ** 2);
      const start = out.morph[i * 2]!;
      const end = out.morph[i * 2 + 1]!;
      // GROUND_VERTEX, transcribed.
      const morph = Math.min(Math.max((toCamera - start) / Math.max(end - start, 1e-3), 0), 1);
      if (morph < 1) {
        live++;
      }
    }

    // Under the defect this is exactly zero, at every camera position and every tolerance.
    expect(live).toBeGreaterThan(0);
  });

  it('never morphs the root, which has no coarser grid to morph toward', () => {
    // The root is not replaced by anything, so blending it onto "its parent's" grid would throw away half its vertices for
    // nothing. Its band is placed beyond every distance the planet is drawn at — and finitely, because an Infinity in the
    // Float32Array makes the shader's `(toCamera - start) / (end - start)` a NaN and a NaN mix factor puts the vertex
    // nowhere at all.
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    const ppm = 1000;
    const tolerance = 2;
    const rootSwitch = (tree.error[0]! * ppm) / tolerance;
    const out = selectionBuffers(8192);
    selectNodes(tree, out, 0, rootSwitch * 2, 0, ppm, tolerance, 1e9);

    expect(out.count).toBe(1);
    expect(out.data[2]).toBe(PLANET_EDGE_M);
    expect(Number.isFinite(out.morph[0]!)).toBe(true);
    expect(Number.isFinite(out.morph[1]!)).toBe(true);
    expect(out.morph[0]!).toBeGreaterThan(PLANET_EDGE_M * 1000);
  });

  it('gives two nodes at one level the SAME band, or their shared edge cracks', () => {
    // The morph band is per LEVEL and not per node, and this is the whole reason. The coarse snap moves an edge vertex
    // ALONG the shared edge as well as across it, so two neighbours blending by different amounts put the same vertex in
    // two places and the seam opens onto the sky. Deriving the band from each node's own error — which differs between
    // neighbours, since that per-node error is the point of a quadtree — is what would reintroduce it.
    const tree = new TerrainQuadtree();
    tree.measure(roughEverywhere());
    const out = selectionBuffers(16384);
    selectNodes(tree, out, 0, 300, 0, 1200, 2, 1e9);
    expect(out.count).toBeGreaterThan(4);

    const bandBySize = new Map<number, [number, number]>();
    for (let i = 0; i < out.count; i++) {
      const size = out.data[i * 4 + 2]!;
      const band: [number, number] = [out.morph[i * 2]!, out.morph[i * 2 + 1]!];
      const seen = bandBySize.get(size);
      if (seen === undefined) {
        bandBySize.set(size, band);
        continue;
      }

      expect(band[0]).toBe(seen[0]);
      expect(band[1]).toBe(seen[1]);
    }

    // And the premise: more than one node was drawn at some level, or the loop above compared nothing.
    expect(bandBySize.size).toBeLessThan(out.count);
  });

  it('gives every node a band that opens no earlier than the node is drawn', () => {
    const tree = new TerrainQuadtree();
    tree.measure(cliffInOneCorner());
    const out = selectionBuffers(8192);
    selectNodes(tree, out, 500, 120, -500, 1200, 2, 1e9);
    expect(out.count).toBeGreaterThan(0);
    for (let i = 0; i < out.count; i++) {
      const start = out.morph[i * 2]!;
      const end = out.morph[i * 2 + 1]!;
      expect(end).toBeGreaterThan(start);
      expect(start).toBeGreaterThanOrEqual(0);
      expect(Number.isFinite(end)).toBe(true);
    }
  });
});

describe('the node morph', () => {
  it('lands an odd grid coordinate on its parent grid and leaves an even one alone', () => {
    // Transcribed from GROUND_VERTEX. At full morph a vertex must coincide with one the parent node actually has — that is
    // what closes the T-junction at a level boundary and removes the pop when a node is split or merged.
    const coarse = (grid: number): number => (Math.floor(grid * NODE_GRID * 0.5 + 0.5) * 2) / NODE_GRID;
    expect(coarse(0)).toBeCloseTo(0, 9);
    expect(coarse(1)).toBeCloseTo(1, 9);
    expect(coarse(2 / NODE_GRID)).toBeCloseTo(2 / NODE_GRID, 9);

    const odd = coarse(3 / NODE_GRID);
    expect(Math.abs(odd - 3 / NODE_GRID)).toBeLessThanOrEqual(1 / NODE_GRID + 1e-9);
    expect(Math.round(odd * NODE_GRID) % 2).toBe(0);
  });

  it('begins inside the node range rather than at its edge', () => {
    expect(MORPH_BEGIN).toBeGreaterThan(0);
    expect(MORPH_BEGIN).toBeLessThan(1);
  });
});
