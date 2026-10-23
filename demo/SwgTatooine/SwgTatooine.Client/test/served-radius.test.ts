import { PushGeometry, PushShape, WireReader, WireWriter } from '@typhondb/client';
import { describe, expect, it } from 'vitest';
import { hullInRadiusM } from '../src/data/replication-stats';

/*
 * CLI3D-06 — the radius the session is SERVED, not the one it asked for.
 *
 * The renderer draws the near-radius ring and fades the heatmap at this number, and it was the camera's request: exactly
 * the quantity #1075 says the server silently shrinks. With CLI3D-03's `PUSH_GEOMETRY` decoded, the server's own
 * geometry can answer instead, and `hullInRadiusM` is the part of that with arithmetic worth testing.
 */

/** A flat region whose hull is the given (x, z) vertices, with no window. */
function region(...xz: number[]): PushGeometry {
  const w = new WireWriter(512);
  w.u8(PushShape.Region);
  w.u8(0);
  w.u8(2);
  w.varu(xz.length / 2);
  for (let i = 0; i < xz.length; i += 2) {
    w.f64(xz[i]);
    w.f64(xz[i + 1]);
  }

  w.varu(0);
  w.varu(0);
  w.vari(0);
  w.vari(0);
  w.vari(0);
  w.u8(0);
  return new PushGeometry().readFrom(new WireReader(w.written()));
}

/** The square the client sends for a disc of `r` about (cx, cz) — `RegionSender`'s own quad. */
function quad(cx: number, cz: number, r: number): PushGeometry {
  return region(cx - r, cz - r, cx + r, cz - r, cx + r, cz + r, cx - r, cz + r);
}

describe('hullInRadiusM', () => {
  it('is the requested radius for the quad the client actually sends', () => {
    // The client asks for a disc of 1500 m as the smallest quad containing it, so the largest disc inside that quad is
    // the 1500 m it asked for — the unclamped case must report back exactly what was requested.
    expect(hullInRadiusM(quad(0, 0, 1500))).toBeCloseTo(1500, 6);
    expect(hullInRadiusM(quad(-2000, 1500, 900))).toBeCloseTo(900, 6);
  });

  it('reports the CLAMPED radius when the server kept a smaller hull — the whole point of the row', () => {
    // #1075: an oversized region is shrunk about its centroid without a word. A client that asked 4000 and is served
    // 1500 must read 1500.
    const served = quad(0, 0, 1500);
    expect(hullInRadiusM(served)).toBeCloseTo(1500, 6);
    expect(hullInRadiusM(served)).toBeLessThan(4000);
  });

  it('is the IN-radius, not the circumradius', () => {
    // A 1000 m half-side square: inscribed disc 1000, circumscribed 1414. Taking the vertex distance would overstate
    // what is served by 41 %.
    const g = quad(0, 0, 1000);
    expect(hullInRadiusM(g)).toBeCloseTo(1000, 6);
    expect(hullInRadiusM(g)).not.toBeCloseTo(Math.SQRT2 * 1000, 0);
  });

  it('measures from the hull it is given, wherever that hull sits', () => {
    // Translating the hull must not change its in-radius: the centroid moves with it.
    expect(hullInRadiusM(quad(7000, -6000, 480))).toBeCloseTo(480, 6);
  });

  it('handles a non-square hull by its nearest edge', () => {
    // 600 × 2000: the inscribed disc is bounded by the short pair.
    expect(hullInRadiusM(region(-300, -1000, 300, -1000, 300, 1000, -300, 1000))).toBeCloseTo(300, 6);
  });

  it('locates the centroid on BOTH axes — a hull off the origin on its short axis', () => {
    // Every case above is symmetric about the origin, so a centroid that forgot to average still lands at 0 on that
    // axis and the `min` over edges quietly picks the pair that was computed correctly. Caught by mutation.
    // This one is 600 wide about x = 5000 and 2000 deep about z = -4000, so the answer comes from the SHORT axis and a
    // wrong `cx` changes it.
    expect(hullInRadiusM(region(4700, -5000, 5300, -5000, 5300, -3000, 4700, -3000))).toBeCloseTo(300, 6);
    // And the mirror case, so neither axis can be the one that happens to work.
    expect(hullInRadiusM(region(-5000, 3700, -3000, 3700, -3000, 4300, -5000, 4300))).toBeCloseTo(300, 6);
  });

  it('is 0 for a shape that has no hull to measure', () => {
    const w = new WireWriter(64);
    w.u8(PushShape.Sphere);
    w.u8(0);
    for (let i = 0; i < 3; i++) {
      w.f64(0);
    }

    w.f32(500);
    w.f32(10);
    w.u8(0);
    w.vari(0);
    w.vari(0);
    w.vari(0);
    w.u8(0);

    expect(
      hullInRadiusM(new PushGeometry().readFrom(new WireReader(w.written()))),
      'a sphere carries R′, not a hull',
    ).toBe(0);
    expect(hullInRadiusM(region()), 'a region before the client sent one').toBe(0);
    expect(hullInRadiusM(region(0, 0, 100, 0)), 'two vertices are not a polygon').toBe(0);
  });
});
