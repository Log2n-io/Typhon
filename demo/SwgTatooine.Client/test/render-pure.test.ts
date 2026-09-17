import '@babylonjs/core/Meshes/thinInstanceMesh';
import { NullEngine } from '@babylonjs/core/Engines/nullEngine';
import { Mesh } from '@babylonjs/core/Meshes/mesh';
import { Scene } from '@babylonjs/core/scene';
import { AggregateGrid } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { fillHeatmap } from '../src/render/heatmap-fill';
import { PrefixUploader } from '../src/render/prefix-upload';
import { buildShape, type ShapeKind } from '../src/render/shapes';
import * as shaders from '../src/render/shaders';
import { LAYER_STYLES, SELECTED_MESH_SCALE, STYLE_COUNT, TINT_COUNT } from '../src/render/styles';
import { packState, pickInstances, type PickHit } from '../src/render/view-math';

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
    for (const style of LAYER_STYLES) {
      style.sizes.forEach(([w, h, l], i) => {
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

  const pack = (...xz: number[]): Float32Array => {
    const data = new Float32Array((xz.length / 2) * 4);
    for (let k = 0; k < xz.length / 2; k++) {
      data[k * 4] = xz[k * 2];
      data[k * 4 + 1] = xz[k * 2 + 1];
      data[k * 4 + 3] = packState(0, 0, false);
    }

    return data;
  };
  const fresh = (): PickHit => ({ archetype: -1, netId: 0, pixels: Infinity, depth: Infinity });
  const screen = { x: 0, y: 0, w: 0 };

  it('picks the instance nearest the cursor, and the nearer one on a tie, reporting the netId packed with it', () => {
    const data = pack(0, 100, 0, 50, 30, 100);
    const netIds = new Uint32Array([11, 22, 33]);
    const best = fresh();
    pickInstances(m, data, netIds, 3, 0, 800, 800, 400, 400, 8, 4, best, screen);
    expect(best.netId).toBe(22);
    expect(best.archetype).toBe(4);
  });

  it('keeps what it holds when nothing is within reach; a point behind the camera projects to NaN, never within reach', () => {
    const best = fresh();
    pickInstances(m, pack(0, -50, 30, 100), new Uint32Array([1, 2]), 2, 0, 800, 800, 400, 400, 8, 0, best, screen);
    expect(best.netId).toBe(0);
  });

  it('lifts each instance by its style', () => {
    const data = pack(0, 100);
    data[3] = packState(1, 0, false);
    const lift = new Float64Array(16);
    lift[1] = 10; // 10 m up at 100 m: 40 px above the centre on an 800 px viewport
    const best = fresh();
    pickInstances(m, data, new Uint32Array([5]), 1, lift, 800, 800, 400, 360, 2, 0, best, screen);
    expect(best.netId).toBe(5);
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
