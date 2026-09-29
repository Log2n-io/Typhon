import '@babylonjs/core/Meshes/thinInstanceMesh';
import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { Scene } from '@babylonjs/core/scene';
import { AggregateGrid } from '@typhondb/client';
import { altitudeOf, Placement } from '../src/data/placement';
import { describe, expect, it } from 'vitest';
import { fillHeatmap, slabAt } from '../src/render/heatmap-fill';
import { PrefixUploader } from '../src/render/prefix-upload';
import { buildShape, type ShapeKind } from '../src/render/shapes';
import * as shaders from '../src/render/shaders';
import { KNOWN_ARCHETYPES } from '../src/data/archetypes';
import { SELECTED_MESH_SCALE, STYLE_COUNT, styleFor, TINT_COUNT, type LayerStyle } from '../src/render/styles';
import {
  invertMatrix4,
  packState,
  pickInstances,
  pickShapes,
  rayGroundT,
  unprojectRay,
  type PickHit,
  type PickRay,
} from '../src/render/view-math';
import { Heightfield } from '../src/terrain/heightfield';
import { createHeightGrid } from '../src/terrain/layers';

describe('shapes', () => {
  const closed: ShapeKind[] = ['box', 'arrow', 'prism', 'mound'];

  for (const kind of closed) {
    it(`${kind}: no hole, consistent winding, outward normals, inside the unit box`, () => {
      const { positions, normals, indices } = buildShape(kind);
      const key = (i: number): string =>
        `${positions[i * 3].toFixed(4)},${positions[i * 3 + 1].toFixed(4)},${positions[i * 3 + 2].toFixed(4)}`;
      const onGround = (i: number): boolean => Math.abs(positions[i * 3 + 1]) < 1e-9;

      const vertexCount = positions.length / 3;
      let cx = 0;
      let cy = 0;
      let cz = 0;
      for (let v = 0; v < vertexCount; v++) {
        cx += positions[v * 3] / vertexCount;
        cy += positions[v * 3 + 1] / vertexCount;
        cz += positions[v * 3 + 2] / vertexCount;
        expect(Math.abs(positions[v * 3])).toBeLessThanOrEqual(0.5 + 1e-9);
        expect(Math.abs(positions[v * 3 + 2])).toBeLessThanOrEqual(0.5 + 1e-9);
        expect(positions[v * 3 + 1]).toBeGreaterThanOrEqual(0);
        expect(positions[v * 3 + 1]).toBeLessThanOrEqual(1);
      }

      const directed = new Map<string, number>();
      const edges: [number, number][] = [];
      for (let t = 0; t < indices.length; t += 3) {
        const tri = [indices[t], indices[t + 1], indices[t + 2]];
        for (let e = 0; e < 3; e++) {
          const a = tri[e];
          const b = tri[(e + 1) % 3];
          const k = `${key(a)}>${key(b)}`;
          directed.set(k, (directed.get(k) ?? 0) + 1);
          edges.push([a, b]);
        }

        // Index order is the reverse of the outward normal (`shapes.ts`: the fan is emitted reversed), on every triangle.
        const [a, b, c] = tri;
        const ux = positions[b * 3] - positions[a * 3];
        const uy = positions[b * 3 + 1] - positions[a * 3 + 1];
        const uz = positions[b * 3 + 2] - positions[a * 3 + 2];
        const vx = positions[c * 3] - positions[a * 3];
        const vy = positions[c * 3 + 1] - positions[a * 3 + 1];
        const vz = positions[c * 3 + 2] - positions[a * 3 + 2];
        const nx = uy * vz - uz * vy;
        const ny = uz * vx - ux * vz;
        const nz = ux * vy - uy * vx;
        expect(nx * normals[a * 3] + ny * normals[a * 3 + 1] + nz * normals[a * 3 + 2]).toBeLessThan(0);
        // Outward: the face's normal points away from the (convex) shape's centre.
        const out =
          normals[a * 3] * (positions[a * 3] - cx) +
          normals[a * 3 + 1] * (positions[a * 3 + 1] - cy) +
          normals[a * 3 + 2] * (positions[a * 3 + 2] - cz);
        expect(out).toBeGreaterThan(0);
      }

      for (const [a, b] of edges) {
        expect(directed.get(`${key(a)}>${key(b)}`)).toBe(1);
        if (!(onGround(a) && onGround(b))) {
          // Every edge off the ground is shared with a neighbour walking it the other way: the surface is closed.
          expect(directed.get(`${key(b)}>${key(a)}`)).toBe(1);
        }
      }
    });
  }
});

describe('style bounds', () => {
  it('holds every corner of every style, grown by the selection scale, whatever the yaw', () => {
    for (const style of KNOWN_ARCHETYPES.map((name): LayerStyle => styleFor(name))) {
      style.sizes.forEach(([w, h, l]: readonly [number, number, number], i: number) => {
        const r = style.bounds.radius[i];
        const cy = style.bounds.centerY[i];
        for (const s of [1, SELECTED_MESH_SCALE]) {
          for (const y of [0, h * s]) {
            // The farthest point in xz of a yawed [w, l] box is its half-diagonal.
            const d = Math.hypot(Math.hypot(w * s, l * s) / 2, y - cy);
            expect(d).toBeLessThanOrEqual(r + 1e-9);
          }
        }

        expect(style.bounds.extent[i]).toBe(Math.max(w, h, l));
        expect(style.bounds.pickY[i]).toBe(h / 2);
      });
    }
  });
});

describe('PrefixUploader', () => {
  const recorder = () => {
    const calls: { view: Float32Array; offset: number; vertexCount: number | undefined }[] = [];
    return {
      calls,
      target: {
        updateDirectly(view: Float32Array, offset: number, vertexCount?: number) {
          calls.push({ view, offset, vertexCount });
        },
      },
    };
  };

  it('uploads a cached power-of-two prefix with its vertex count, capped at capacity', () => {
    const data = new Float32Array(600 * 4);
    const up = new PrefixUploader(data, 4);
    const { calls, target } = recorder();

    expect(up.upload(target, 1)).toBe(1);
    expect(up.upload(target, 3)).toBe(4);
    expect(up.upload(target, 4)).toBe(4);
    expect(up.upload(target, 1000)).toBe(600);
    expect(calls.map((c) => c.view.length)).toEqual([4, 16, 16, 2400]);
    // The vertex count matches the view: Babylon then takes its no-copy path and does not keep the prefix as its data.
    expect(calls.every((c) => c.offset === 0 && c.vertexCount === c.view.length / 4)).toBe(true);
    // The same bucket reuses the same view: no allocation per frame.
    expect(calls[1].view).toBe(calls[2].view);
    expect(calls[0].view.buffer).toBe(data.buffer);
  });

  it('leaves Babylon no prefix to rebuild from after a lost context (checked on a NullEngine)', () => {
    const engine = new NullEngine();
    const scene = new Scene(engine);
    const mesh = new Mesh('m', scene);
    const data = new Float32Array(64 * 4);
    mesh.thinInstanceSetBuffer('instData', data, 4, false);
    const buffer = mesh.getVertexBuffer('instData')?.getWrapperBuffer();
    expect(buffer).toBeDefined();
    if (buffer === undefined) {
      return;
    }

    new PrefixUploader(data, 4).upload(buffer, 3);
    // No kept data: `_rebuild` re-creates the buffer at its full capacity instead of at the uploaded prefix.
    expect(buffer.getData()).toBeNull();
    // Without a vertex count Babylon keeps the view — the 4-instance prefix — which is what broke after a context loss.
    buffer.updateDirectly(data.subarray(0, 16), 0);
    expect(buffer.getData()).toBeInstanceOf(Float32Array);
    expect((buffer.getData() as Float32Array).length).toBe(16);
    scene.dispose();
    engine.dispose();
  });

  it('sends nothing for an empty frame or a missing buffer', () => {
    const up = new PrefixUploader(new Float32Array(16), 4);
    const { calls, target } = recorder();
    expect(up.upload(target, 0)).toBe(0);
    expect(up.upload(null, 5)).toBe(0);
    expect(calls).toHaveLength(0);
  });
});

describe('fillHeatmap', () => {
  it('normalises each channel to its own maximum on a log scale, and clears the rest', () => {
    const grid = new AggregateGrid({ index: 0, origin: [0, 0], cell: 1, dims: [2, 2], archetypes: [7, 8] });
    grid.beginFrame();
    grid.setCell(0, new Uint32Array([100, 0]), 0);
    grid.setCell(3, new Uint32Array([9, 2]), 0);
    const out = new Uint8Array(4 * 4 * 4).fill(99);
    fillHeatmap(grid, out, 4, 4);

    const texel = (x: number, z: number) => Array.from(out.subarray((z * 4 + x) * 4, (z * 4 + x) * 4 + 4));
    expect(texel(0, 0)).toEqual([255, 0, 0, 0]);
    expect(texel(1, 1)).toEqual([Math.round((Math.log1p(9) / Math.log1p(100)) * 255), 255, 0, 0]);
    expect(texel(1, 0)).toEqual([0, 0, 0, 0]);
    // Texels beyond the grid are cleared, not left stale.
    expect(texel(3, 3)).toEqual([0, 0, 0, 0]);
  });

  it('draws the slab it is asked for, not always the floor of a deep grid (CLI3D-04)', () => {
    // 2 × 2 × 3: `cell = i₀ + 2·(i₁ + 2·i₂)`, so slab 1 starts at cell 4 and slab 2 at cell 8.
    const grid = new AggregateGrid({ index: 0, origin: [0, 0, 0], cell: 100, dims: [2, 2, 3], archetypes: [7] });
    grid.beginFrame();
    grid.setCell(0, new Uint32Array([5]), 0); // slab 0, texel (0, 0)
    grid.setCell(5, new Uint32Array([5]), 0); // slab 1, texel (1, 0)
    grid.setCell(11, new Uint32Array([5]), 0); // slab 2, texel (1, 1)

    const lit = (slab: number): string[] => {
      const out = new Uint8Array(2 * 2 * 4);
      fillHeatmap(grid, out, 2, 2, slab);
      const on: string[] = [];
      for (let z = 0; z < 2; z++) {
        for (let x = 0; x < 2; x++) {
          if (out[(z * 2 + x) * 4] > 0) {
            on.push(`${x},${z}`);
          }
        }
      }

      return on;
    };

    expect(lit(0)).toEqual(['0,0']);
    expect(lit(1)).toEqual(['1,0']);
    expect(lit(2)).toEqual(['1,1']);
  });

  it('picks the slab the camera is in, clamped to the grid', () => {
    const deep = new AggregateGrid({ index: 0, origin: [0, 0, -50], cell: 100, dims: [2, 2, 3], archetypes: [7] });
    const flat = new AggregateGrid({ index: 0, origin: [0, 0], cell: 100, dims: [2, 2], archetypes: [7] });

    // Slabs span [-50, 50), [50, 150), [150, 250) from the grid's own origin on the third axis.
    expect(slabAt(deep, 0)).toBe(0);
    expect(slabAt(deep, 100)).toBe(1);
    expect(slabAt(deep, 200)).toBe(2);
    expect(slabAt(deep, -9999), 'below the grid clamps to the floor').toBe(0);
    expect(slabAt(deep, 9999), 'above it clamps to the ceiling').toBe(2);
    expect(slabAt(flat, 9999), 'a flat grid has one slab whatever the altitude').toBe(0);
  });
});

describe('pickInstances', () => {
  // Row-major perspective looking down +Z from the origin, as in view-math.test.ts.
  const f = 1 / Math.tan(Math.PI / 4);
  const m = new Float32Array(16);
  m[0] = f;
  m[5] = f;
  m[10] = 1001 / 999;
  m[11] = 1;
  m[14] = -2000 / 999;

  /** Ground points into the packed layout `(x, y, z, packed)`, at altitude 0 — every SWG archetype's (CLI3D-04). */
  const pack = (...xz: number[]): Float32Array => {
    const data = new Float32Array((xz.length / 2) * 4);
    for (let k = 0; k < xz.length / 2; k++) {
      data[k * 4] = xz[k * 2];
      data[k * 4 + 2] = xz[k * 2 + 1];
      data[k * 4 + 3] = packState(0, 0, false);
    }

    return data;
  };
  const fresh = (): PickHit => ({ archetype: -1, netId: 0, pixels: Infinity, depth: Infinity, inside: false });
  const screen = { x: 0, y: 0, w: 0 };
  const inverse = new Float64Array(16);
  /** The ray through a viewport pixel, by the same route the client takes. */
  const rayAt = (px: number, py: number): PickRay => {
    const ray: PickRay = { ox: 0, oy: 0, oz: 0, dx: 0, dy: 0, dz: 1 };
    expect(invertMatrix4(m, inverse)).toBe(true);
    expect(unprojectRay(inverse, px, py, 800, 800, ray)).toBe(true);
    return ray;
  };

  it('picks the instance nearest the cursor, and the nearer one on a tie, reporting the netId packed with it', () => {
    const data = pack(0, 100, 0, 50, 30, 100);
    const netIds = new Uint32Array([11, 22, 33]);
    const best = fresh();
    pickInstances(rayAt(400, 400), m, data, netIds, 3, 0, 0, 1, 800, 800, 400, 400, 8, 4, best, screen);
    expect(best.netId).toBe(22);
    expect(best.archetype).toBe(4);
  });

  it('keeps what it holds when nothing is within reach; a point behind the camera projects to NaN, never within reach', () => {
    const best = fresh();
    const data = pack(0, -50, 30, 100);
    pickInstances(
      rayAt(400, 400),
      m,
      data,
      new Uint32Array([1, 2]),
      2,
      0,
      0,
      1,
      800,
      800,
      400,
      400,
      8,
      0,
      best,
      screen,
    );
    expect(best.netId).toBe(0);
  });

  it('lifts each instance by its style', () => {
    const data = pack(0, 100);
    data[3] = packState(1, 0, false);
    const lift = new Float64Array(16);
    lift[1] = 10; // 10 m up at 100 m: 40 px above the centre on an 800 px viewport
    const best = fresh();
    pickInstances(
      rayAt(400, 360),
      m,
      data,
      new Uint32Array([5]),
      1,
      lift,
      0,
      1,
      800,
      800,
      400,
      360,
      2,
      0,
      best,
      screen,
    );
    expect(best.netId).toBe(5);
  });

  it('picks a flying entity where it is drawn, not on the ground beneath it (CLI3D-04)', () => {
    // 100 m away at 10 m of altitude: 40 px above the viewport's centre on an 800 px, 90° view.
    const data = pack(0, 100);
    data[1] = 10;
    const atGround = fresh();
    pickInstances(
      rayAt(400, 400),
      m,
      data,
      new Uint32Array([7]),
      1,
      0,
      0,
      1,
      800,
      800,
      400,
      400,
      2,
      0,
      atGround,
      screen,
    );
    expect(atGround.netId, 'the cursor on the ground point must not pick a thing 10 m above it').toBe(0);

    const aloft = fresh();
    pickInstances(rayAt(400, 360), m, data, new Uint32Array([7]), 1, 0, 0, 1, 800, 800, 400, 360, 2, 0, aloft, screen);
    expect(aloft.netId, 'the cursor on the entity itself must pick it').toBe(7);
  });

  /** An 18 × 9 × 18 m building, the size `styles.ts` gives `StructureKind.Building`. */
  const buildingSizes = (): Float64Array => {
    const sizes = new Float64Array(16 * 3);
    sizes[0] = 18;
    sizes[1] = 9;
    sizes[2] = 18;
    return sizes;
  };

  it('picks a big shape anywhere on it, not only within a few pixels of its centre', () => {
    // THE defect. At 60 m on an 800 px, 90° view a metre is 400/60 px, so an 18 m building spans 120 px and a 9 m wall
    // reaches 60 px above the ground point. Clicking the top of that wall is 50-odd pixels from the centre the old pick
    // measured against, and it selected nothing at all — which is exactly what was reported.
    const data = pack(0, 60);
    const yaws = new Float32Array([0]);
    const netIds = new Uint32Array([42]);
    const sizes = buildingSizes();

    for (const [px, py, where] of [
      [400, 400, 'the centre'],
      [455, 400, 'the right edge'],
      [345, 400, 'the left edge'],
      [400, 345, 'the top of the wall'],
      [450, 350, 'a top corner'],
    ] as const) {
      const best = fresh();
      pickShapes(rayAt(px, py), data, yaws, netIds, 1, sizes, -1, 1.4, 3, best);
      expect(best.netId, `clicking ${where}`).toBe(42);
      expect(best.inside, `clicking ${where}`).toBe(true);
    }

    // And it still refuses the sky beside it, which is the other half of picking working.
    const miss = fresh();
    pickShapes(rayAt(700, 400), data, yaws, netIds, 1, sizes, -1, 1.4, 3, miss);
    expect(miss.netId).toBe(0);
  });

  it('turns the box by the instance yaw, as the shader does', () => {
    // A 4 × 2 × 40 m sliver: end-on it is 4 m wide, broadside 40 m. Turned a quarter turn, the two swap — and a test that
    // did not check both sides of that would pass on a box test that ignored yaw entirely.
    const sizes = new Float64Array(16 * 3);
    sizes[0] = 4;
    sizes[1] = 2;
    sizes[2] = 40;
    const data = pack(0, 100);
    const netIds = new Uint32Array([9]);
    // 100 m away, a metre is 4 px. 10 m to the side is 40 px: inside the 40 m length, outside the 4 m width.
    const aside = rayAt(440, 399);

    const along = fresh();
    pickShapes(aside, data, new Float32Array([0]), netIds, 1, sizes, -1, 1.4, 0, along);
    expect(along.netId, 'facing +Z, 10 m to the side is outside the 4 m width').toBe(0);

    const across = fresh();
    pickShapes(aside, data, new Float32Array([Math.PI / 2]), netIds, 1, sizes, -1, 1.4, 0, across);
    expect(across.netId, 'turned a quarter turn, the same point is inside the 40 m length').toBe(9);
  });

  it('grows the box for a selected instance and flattens it in the flatten mode, exactly as the shader does', () => {
    const sizes = new Float64Array(16 * 3);
    sizes[0] = 10;
    sizes[1] = 10;
    sizes[2] = 10;
    const yaws = new Float32Array([0]);
    const netIds = new Uint32Array([3]);
    // 100 m away, a metre is 4 px. The unselected box reaches 5 m (20 px); grown by 1.4 it reaches 7 m (28 px).
    const edge = rayAt(424, 399);

    const plain = pack(0, 100);
    const unselected = fresh();
    pickShapes(edge, plain, yaws, netIds, 1, sizes, -1, 1.4, 0, unselected);
    expect(unselected.netId).toBe(0);

    const chosen = pack(0, 100);
    chosen[3] = packState(0, 0, true);
    const selected = fresh();
    pickShapes(edge, chosen, yaws, netIds, 1, sizes, -1, 1.4, 0, selected);
    expect(selected.netId, 'a selected shape is drawn 1.4× bigger, so it is that much bigger to click').toBe(3);

    // A corpse is a quarter of its height: 10 m becomes 2.5 m, which at 100 m is 10 px above the ground point.
    const corpse = pack(0, 100);
    corpse[3] = packState(0, 2, false);
    const high = rayAt(400, 385);
    const standing = fresh();
    pickShapes(high, corpse, yaws, netIds, 1, sizes, -1, 1.4, 0, standing);
    expect(standing.netId, 'without the flatten it still reaches 15 px up').toBe(3);

    const flattened = fresh();
    pickShapes(high, corpse, yaws, netIds, 1, sizes, 2, 1.4, 0, flattened);
    expect(flattened.netId, 'flattened to 2.5 m it does not reach 15 px up').toBe(0);
  });

  it('prefers what the cursor is ON over what it is merely NEAR, and the nearest of those', () => {
    // A creature standing in front of a building: the cursor is inside the building's silhouette and also within a few
    // pixels of the creature. The creature is nearer along the ray, so it wins — which is the case that makes a town
    // usable, since a building covers most of the screen there.
    const sizes = buildingSizes();
    const building = pack(0, 60);
    const best = fresh();
    pickShapes(rayAt(420, 380), building, new Float32Array([0]), new Uint32Array([100]), 1, sizes, -1, 1.4, 1, best);
    expect(best.netId).toBe(100);
    expect(best.inside).toBe(true);

    // A far-band sprite 30 m away, 4 px across, whose centre is 2 px from the cursor.
    const creature = pack(0, 30);
    creature[0] = (420 - 400) / (400 / 30);
    creature[1] = (400 - 380) / (400 / 30);
    pickInstances(
      rayAt(420, 380),
      m,
      creature,
      new Uint32Array([7]),
      1,
      0,
      3,
      2.5,
      800,
      800,
      420,
      380,
      8,
      2,
      best,
      screen,
    );
    expect(best.netId, 'the nearer thing the cursor is also on must win').toBe(7);
    expect(best.depth).toBeLessThan(60);
  });

  it('lets nothing that is merely near a centre displace something the cursor is on', () => {
    const sizes = buildingSizes();
    const best = fresh();
    pickShapes(rayAt(400, 400), pack(0, 60), new Float32Array([0]), new Uint32Array([100]), 1, sizes, -1, 1.4, 1, best);
    expect(best.netId).toBe(100);

    // A point entity 200 m away whose centre is a pixel from the cursor: nearer in SCREEN terms, and not what was
    // clicked. Before the shape test existed this is what a click in a town always found.
    const distant = pack(0, 200);
    pickInstances(
      rayAt(400, 400),
      m,
      distant,
      new Uint32Array([55]),
      1,
      0,
      0,
      1,
      800,
      800,
      400,
      400,
      8,
      2,
      best,
      screen,
    );
    expect(best.netId).toBe(100);
  });
});

describe('shaders', () => {
  const sources = Object.entries(shaders).filter((e): e is [string, string] => typeof e[1] === 'string');

  it('interpolate every constant into valid GLSL text', () => {
    expect(sources.length).toBe(8);
    for (const [, text] of sources) {
      const source = text.replace(/\/\/.*$/gm, '');
      expect(source).not.toMatch(/undefined|NaN|Infinity|\$\{/);
      // Every number followed by a dot has digits after it, or is an integer written as `n.0`.
      expect(source).not.toMatch(/\d\.(?!\d)/);
    }

    expect(shaders.ENTITY_VERTEX).toContain(`uColors[${STYLE_COUNT}]`);
    expect(shaders.ENTITY_VERTEX).toContain(`uTints[${TINT_COUNT}]`);
  });
});

describe('altitudeOf', () => {
  /**
   * Which altitude the client draws an entity at, and it is the only place that decision is made.
   *
   * The client used to add its own ground to whatever the server sent, which was right only because the server always
   * sent 0 — correct the way a stopped clock is. Terrain rung (b) gives the server an opinion, sampled from the same
   * baked field this client draws, so adding to it would double the relief.
   */
  const flat = { heightAt: (): number => 0 };
  const hilly = { heightAt: (x: number, z: number): number => 100 + x + z };
  const at = new Placement();

  it('takes the server field as the whole answer, adding nothing to it', () => {
    at.read(2, new Float64Array([7, 9, 0, 0]), 0);
    const served = new Float32Array([250, -30]);
    expect(altitudeOf(served, 0, at, hilly)).toBe(250);
    expect(altitudeOf(served, 1, at, hilly)).toBe(-30);
  });

  it('falls back to the position axis plus its own ground when the catalog has no such field', () => {
    // The browser-side mock, and any catalog older than rung (b).
    at.read(2, new Float64Array([7, 9, 0, 0]), 0);
    expect(altitudeOf(null, 0, at, hilly)).toBe(100 + 7 + 9);
    expect(altitudeOf(null, 0, at, flat)).toBe(0);
  });

  it('keeps the third position axis in the fallback, which is what a space realm has', () => {
    // A three-dimensional store carries altitude in the position itself; the ground under a ship is not a thing.
    at.read(3, new Float64Array([7, 9, 40, 0, 0, 0]), 0);
    expect(at.y).toBe(40);
    expect(altitudeOf(null, 0, at, flat)).toBe(40);
  });

  it('is not fooled by a zero the server actually meant', () => {
    // An entity inside a building stands on that realm's flat floor, and 0 is the right answer there however much mesa
    // the door sits on. The fallback would have added the mesa.
    at.read(2, new Float64Array([7, 9, 0, 0]), 0);
    expect(altitudeOf(new Float32Array([0]), 0, at, hilly)).toBe(0);
    expect(altitudeOf(null, 0, at, hilly)).toBe(116);
  });
});


describe('the ground stands between the cursor and what is behind it', () => {
  /**
   * A 200 m wall of ground across the middle of an otherwise flat field.
   *
   * Cheaper than the real planet and it isolates the question: everything the tests below turn on is whether the march
   * finds a crossing, not what shape the crossing has.
   */
  function mesaAcrossTheMiddle(): Heightfield {
    const field = new Heightfield(createHeightGrid(64, 16, -512));
    const posts = field.grid.posts;
    for (let z = 0; z < posts; z++) {
      for (let x = 0; x < posts; x++) {
        // Planet x of post `x` is `-512 + x * 16`; the wall occupies x ∈ [0, 128).
        field.grid.height[z * posts + x] = x >= 32 && x < 40 ? 200 : 0;
      }
    }

    field.measure();
    return field;
  }

  const flatRay = (ox: number, oy: number, dx: number, dy: number): PickRay => {
    const len = Math.hypot(dx, dy);
    return { ox, oy, oz: 0, dx: dx / len, dy: dy / len, dz: 0 };
  };

  it('finds the near face of a wall, and nothing at all when the ray clears it', () => {
    const field = mesaAcrossTheMiddle();
    // Level, at 100 m, straight at a wall whose top is 200 m: it must stop at the wall's near face, x = 0.
    const hit = rayGroundT(flatRay(-400, 100, 1, 0), field, 32000);
    expect(hit).toBeLessThan(420);
    expect(hit).toBeGreaterThan(380);

    // The same ray above the wall's top clears it, and the field is flat beyond, so it never comes down.
    expect(rayGroundT(flatRay(-400, 300, 1, 0), field, 32000)).toBe(Infinity);
  });

  it('reports no ground for an eye already under it, rather than zero', () => {
    // Zero would make everything unpickable. This is reachable: the eye camera clamps to the ground, and a frame taken
    // mid-bake sees a field that is still flat while the camera is not.
    const field = mesaAcrossTheMiddle();
    expect(rayGroundT(flatRay(0, -50, 1, 0), field, 32000)).toBe(Infinity);
  });

  it('refuses to pick a box the ground is in front of, and still picks the one in front of it', () => {
    // THE defect: with no ground test, clicking a mesa's visible rock face selects the creature standing behind it — an
    // entity that is not on screen at any pixel. Depth testing does this for the GPU; the pick was never told.
    const field = mesaAcrossTheMiddle();
    const ray = flatRay(-400, 100, 1, 0);
    const groundT = rayGroundT(ray, field, 32000);

    // Two boxes on the ray: one this side of the wall, one behind it.
    const data = new Float32Array([-200, 100, 0, packState(0, 0, false), 200, 100, 0, packState(0, 0, false)]);
    const yaws = new Float32Array([0, 0]);
    const netIds = new Uint32Array([7, 9]);
    const sizes = new Float64Array([40, 40, 40]);

    const behindOnly = new Float32Array(data.subarray(4));
    const hidden: PickHit = { archetype: -1, netId: 0, pixels: Infinity, depth: Infinity, inside: false };
    pickShapes(ray, behindOnly, new Float32Array([0]), new Uint32Array([9]), 1, sizes, -1, 1, 3, hidden, groundT);
    expect(hidden.netId).toBe(0);

    // Unbounded — the behaviour before this fix — it is picked, which is what makes the assertion above meaningful.
    const unbounded: PickHit = { archetype: -1, netId: 0, pixels: Infinity, depth: Infinity, inside: false };
    pickShapes(ray, behindOnly, new Float32Array([0]), new Uint32Array([9]), 1, sizes, -1, 1, 3, unbounded);
    expect(unbounded.netId).toBe(9);

    // And the near one is unaffected.
    const visible: PickHit = { archetype: -1, netId: 0, pixels: Infinity, depth: Infinity, inside: false };
    pickShapes(ray, data, yaws, netIds, 2, sizes, -1, 1, 3, visible, groundT);
    expect(visible.netId).toBe(7);
  });
});

describe('the view matrix inverse', () => {
  it('refuses a near-singular matrix instead of filling the inverse with Infinity', () => {
    // `det === 0` is not the failure that happens. A determinant of 1e-320 is not zero, `1 / det` is Infinity, and the
    // inverse comes back full of Infinity and NaN — so `unprojectRay` yields a NaN ray, every comparison against it is
    // false, and the pick selects nothing while reporting success.
    const out = new Float64Array(16);
    const tiny = 1e-110;
    const nearSingular = [
      tiny, 0, 0, 0,
      0, tiny, 0, 0,
      0, 0, tiny, 0,
      0, 0, 0, 1,
    ];
    expect(invertMatrix4(nearSingular, out)).toBe(false);

    // A well-conditioned matrix of the same shape still inverts.
    const fine = [2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 2, 0, 0, 0, 0, 1];
    expect(invertMatrix4(fine, out)).toBe(true);
    expect(out[0]).toBeCloseTo(0.5, 12);
  });
});
