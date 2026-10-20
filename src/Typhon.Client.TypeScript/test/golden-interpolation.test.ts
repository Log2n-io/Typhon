import { describe, expect, it } from 'vitest';
import { interpolateAngle, interpolateQuat3 } from '../src/index.js';
import { bits, fromBits, goldenJson } from './golden-support.js';

/*
 * interpolation: the cross-language pin of 03 § 6's interpolation of an `angle` and a `quat3` (#1398). The .NET SDK
 * generated it from WireMath; this side must produce the same IEEE bits for every case.
 */

interface AngleCase {
  readonly bits: number;
  readonly from: string;
  readonly to: string;
  readonly t: string;
  readonly value: string;
}

interface QuatCase {
  readonly from: string[];
  readonly to: string[];
  readonly t: string;
  readonly value: string[];
}

interface InterpolationVector {
  readonly angle: AngleCase[];
  readonly quat3: QuatCase[];
}

const vector = goldenJson('interpolation') as InterpolationVector;

describe('golden interpolation', () => {
  it('interpolates every angle case to the same bits', () => {
    expect(vector.angle.length).toBeGreaterThan(0);
    for (const c of vector.angle) {
      const value = interpolateAngle(fromBits(c.from), fromBits(c.to), fromBits(c.t), c.bits);
      expect(bits(value), `angle ${c.bits} bits, ${c.from} → ${c.to} at ${c.t}`).toBe(c.value);
    }
  });

  it('interpolates every quat3 case to the same bits, in place too', () => {
    expect(vector.quat3.length).toBeGreaterThan(0);
    const out = new Float64Array(4);
    for (const c of vector.quat3) {
      const from = Float64Array.from(c.from.map(fromBits));
      const to = Float64Array.from(c.to.map(fromBits));
      const t = fromBits(c.t);
      interpolateQuat3(from, 0, to, 0, t, out, 0);
      expect(Array.from(out, bits), `quat3 ${c.from.join(',')} → ${c.to.join(',')} at ${c.t}`).toEqual(c.value);

      // Aliased: the result written over `from`.
      interpolateQuat3(from, 0, to, 0, t, from, 0);
      expect(Array.from(from, bits)).toEqual(c.value);
    }
  });
});
