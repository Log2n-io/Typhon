import { DebugGrid, PushGeometry, PushGeometryFlags, PushShape, WireReader, WireWriter } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { replicationStatsOf } from '../src/data/replication-stats';
import { CELL_TEXTURE_SIZE, fillDeliveredCells, hullUniform, type CellRect } from '../src/render/debug-cells';

/**
 * The delivered-cell overlay (CLI3D-03).
 *
 * These build the payloads with the SDK's own writer and decode them with its reader, so what is under test is the
 * client's use of the geometry — the texel a cell lands on, the metres a texel covers, the hull the shader is handed —
 * and not the decoding, which the SDK's golden vectors already hold to the C# encoder.
 */

/** A flat window of `w` cells whose bit `(lx, ly)` is set when `set(lx, ly)` says so. */
function sphereGeometry(
  w: number,
  originX: number,
  originY: number,
  set: (lx: number, ly: number) => boolean,
  flags: number = PushGeometryFlags.None,
): PushGeometry {
  const rows: number[] = [];
  for (let ly = 0; ly < w; ly++) {
    let bits = 0;
    for (let lx = 0; lx < w; lx++) {
      bits |= set(lx, ly) ? 1 << lx : 0;
    }

    rows.push(bits);
  }

  const w2 = new WireWriter(1024);
  w2.u8(PushShape.Sphere);
  w2.u8(flags);
  for (let i = 0; i < 3; i++) {
    w2.f64(0);
  }

  w2.f32(500);
  w2.f32(10);
  w2.u8(2);
  w2.vari(originX);
  w2.vari(originY);
  w2.vari(0);
  w2.u8(w);
  const rowBytes = (w + 7) >> 3;
  for (const bits of rows) {
    for (let b = 0; b < rowBytes; b++) {
      w2.u8((bits >> (8 * b)) & 0xff);
    }
  }

  return new PushGeometry().readFrom(new WireReader(w2.written()));
}

/** A 250 m grid whose origin is the planet's lower corner. */
function grid(cellM = 250, originX = -8192, originY = -8192): DebugGrid {
  const w = new WireWriter(64);
  w.f64(originX);
  w.f64(originY);
  w.f64(0);
  w.f64(cellM);
  w.varu(66);
  w.varu(66);
  w.varu(1);
  return new DebugGrid().readFrom(new WireReader(w.written()));
}

describe('the delivered-cell texture', () => {
  it('sets exactly the texels of the cells the server delivered', () => {
    // A TRIANGLE, not a checkerboard. The checkerboard this started as was `(lx + ly) % 2`, which is symmetric under
    // transposition — so writing texel (lx, ly) at (ly, lx) passed it, and the mutation that proves the row stride
    // walked straight through. A strict lower triangle is asymmetric on every axis a wrong index can confuse.
    const g = sphereGeometry(8, 3, -2, (lx, ly) => lx < ly);
    const out = new Uint8Array(CELL_TEXTURE_SIZE * CELL_TEXTURE_SIZE * 4);
    const rect: CellRect = { x: 0, z: 0, width: 0, height: 0 };

    expect(fillDeliveredCells(g, grid(), out, CELL_TEXTURE_SIZE, rect)).toBe(28);
    for (let ly = 0; ly < 8; ly++) {
      for (let lx = 0; lx < 8; lx++) {
        const alpha = out[(ly * CELL_TEXTURE_SIZE + lx) * 4 + 3];
        expect(alpha > 0, `texel (${lx}, ${ly})`).toBe(g.delivered(3 + lx, -2 + ly, 0));
      }
    }
  });

  it('covers exactly the window, in planet metres', () => {
    const g = sphereGeometry(5, 4, -3, () => true);
    const rect: CellRect = { x: 0, z: 0, width: 0, height: 0 };
    fillDeliveredCells(
      g,
      grid(250, -8192, -8192),
      new Uint8Array(CELL_TEXTURE_SIZE * CELL_TEXTURE_SIZE * 4),
      CELL_TEXTURE_SIZE,
      rect,
    );

    // Cell 4 of a 250 m grid from -8192 starts at -7192; five cells span 1250 m.
    expect(rect).toEqual({ x: -8192 + 4 * 250, z: -8192 + -3 * 250, width: 1250, height: 1250 });
  });

  it('leaves the texels past the window clear, because the shader never samples them', () => {
    const g = sphereGeometry(4, 0, 0, () => true);
    const out = new Uint8Array(CELL_TEXTURE_SIZE * CELL_TEXTURE_SIZE * 4);
    const rect: CellRect = { x: 0, z: 0, width: 0, height: 0 };
    fillDeliveredCells(g, grid(), out, CELL_TEXTURE_SIZE, rect);

    expect(out[(0 * CELL_TEXTURE_SIZE + 4) * 4 + 3]).toBe(0);
    expect(out[(4 * CELL_TEXTURE_SIZE + 0) * 4 + 3]).toBe(0);
  });

  it('draws nothing for a session with no window — a World profile has none', () => {
    const w = new WireWriter(32);
    w.u8(PushShape.World);
    w.u8(0);
    w.u32(0x20);
    w.u32(1);
    const world = new PushGeometry().readFrom(new WireReader(w.written()));
    const out = new Uint8Array(CELL_TEXTURE_SIZE * CELL_TEXTURE_SIZE * 4).fill(9);
    const rect: CellRect = { x: 0, z: 0, width: 0, height: 0 };

    expect(fillDeliveredCells(world, grid(), out, CELL_TEXTURE_SIZE, rect)).toBe(0);
    expect(out.every((b) => b === 0)).toBe(true);
  });
});

describe('the hull handed to the shader', () => {
  it('is the vertices the SERVER reports, padded so an unused slot draws no edge', () => {
    const w = new WireWriter(512);
    w.u8(PushShape.Region);
    w.u8(0);
    w.u8(2);
    w.varu(4);
    for (const [x, z] of [
      [-100, -200],
      [300, -200],
      [300, 400],
      [-100, 400],
    ]) {
      w.f64(x);
      w.f64(z);
    }

    w.varu(1234);
    w.varu(5000);
    w.vari(0);
    w.vari(0);
    w.vari(0);
    w.u8(0);
    const region = new PushGeometry().readFrom(new WireReader(w.written()));

    const data = new Array<number>(64).fill(-1);
    const count = hullUniform(region, data, 16);
    expect(count).toBe(4);
    expect(data.slice(0, 16)).toEqual([-100, -200, 0, 0, 300, -200, 0, 0, 300, 400, 0, 0, -100, 400, 0, 0]);
    // Every slot past the hull repeats the last vertex: a zero-length edge, not a line to the origin.
    expect(data.slice(16, 20)).toEqual([-100, 400, 0, 0]);
    expect(data).toHaveLength(64);
  });

  it('is empty, not garbage, before the client has sent a region', () => {
    const w = new WireWriter(64);
    w.u8(PushShape.Region);
    w.u8(0);
    w.u8(2);
    w.varu(0);
    w.varu(0);
    w.varu(0);
    w.vari(0);
    w.vari(0);
    w.vari(0);
    w.u8(0);
    const data = new Array<number>(64).fill(-1);
    const count = hullUniform(new PushGeometry().readFrom(new WireReader(w.written())), data, 16);

    expect(count).toBe(0);
    expect(data.every((v) => v === 0)).toBe(true);
  });
});

describe('the HUD row', () => {
  it('reports the SERVER’s shape and cell count, not the client’s request', () => {
    const g = sphereGeometry(5, 0, 0, (lx, ly) => lx === ly, PushGeometryFlags.ViewComplete);
    const stats = replicationStatsOf({ grid: grid(256), geometry: g });

    expect(stats).toEqual({
      shape: 'sphere',
      deliveredCells: 5,
      windowCells: 25,
      cellM: 256,
      held: 0,
      nearBudget: 0,
      radiusM: 500,
      level: 2,
      viewComplete: true,
    });
  });

  it('is null until both halves of the DEBUG block have arrived', () => {
    const g = sphereGeometry(2, 0, 0, () => true);
    expect(replicationStatsOf(null)).toBeNull();
    expect(replicationStatsOf({ grid: null, geometry: g })).toBeNull();
    expect(replicationStatsOf({ grid: grid(), geometry: null })).toBeNull();
  });
});
