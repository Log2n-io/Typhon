import { describe, expect, it } from 'vitest';
import { decodeF16, encodeF16 } from '../src/index.js';

/*
 * W10 against the platform: the hand-written half conversion must agree with a native `Float16Array` — every one of the
 * 65 536 bit patterns decoded, and doubles encoded, ties included. Skipped where the runtime has no `Float16Array`
 * (Node 22 without `--js-float16array`).
 */

interface HalfArray {
  [index: number]: number;
  readonly buffer: ArrayBuffer;
}

const Native = (globalThis as { Float16Array?: new (length: number) => HalfArray }).Float16Array;

describe.skipIf(Native === undefined)('f16 (W10) against the native Float16Array', () => {
  // Created on first use: a skipped suite's body still runs at collection.
  let half: HalfArray | null = null;
  let bits = new Uint16Array(0);

  function view(): HalfArray {
    if (half === null) {
      half = new Native!(1);
      bits = new Uint16Array(half.buffer);
    }

    return half;
  }

  function nativeEncode(x: number): number {
    view()[0] = x;
    return bits[0]!;
  }

  function nativeDecode(pattern: number): number {
    const native = view();
    bits[0] = pattern;
    return native[0]!;
  }

  /** The encoded bits, with every NaN folded to the canonical `0x7E00` the protocol writes (§ 10). */
  function expected(x: number): number {
    return x !== x ? 0x7e00 : nativeEncode(x);
  }

  it('decodes all 65 536 bit patterns identically', () => {
    for (let pattern = 0; pattern < 0x10000; pattern++) {
      const native = nativeDecode(pattern);
      const mine = decodeF16(pattern);
      if (native !== native) {
        expect(mine !== mine, `0x${pattern.toString(16)}`).toBe(true);
      } else if (!Object.is(mine, native)) {
        expect(mine, `0x${pattern.toString(16)}`).toBe(native);
      }
    }
  });

  it('encodes every half value, its neighbouring doubles and every tie between two halves identically', () => {
    const doubles = new Float64Array(1);
    const words = new BigUint64Array(doubles.buffer);
    const step = (x: number, up: boolean): number => {
      doubles[0] = x;
      words[0] = up === x >= 0 ? words[0]! + 1n : words[0]! - 1n;
      return doubles[0];
    };

    let previous = Number.NaN;
    for (let pattern = 0; pattern < 0x7c00; pattern++) {
      const h = decodeF16(pattern);
      const candidates = [
        h,
        -h,
        h === 0 ? Number.MIN_VALUE : step(h, true),
        h === 0 ? Number.MIN_VALUE : step(h, false),
      ];
      if (previous === previous) {
        // The midpoint between two adjacent halves is exact in a double: a tie, which goes to the even mantissa.
        const tie = (previous + h) / 2;
        candidates.push(tie, -tie, step(tie, true), step(tie, false));
      }

      for (const x of candidates) {
        const mine = encodeF16(x);
        if (mine !== expected(x)) {
          expect(`0x${mine.toString(16)}`, `encode(${x})`).toBe(`0x${expected(x).toString(16)}`);
        }
      }

      previous = h;
    }

    // Past the largest half: the tie with 65536 overflows to infinity, anything below it rounds down.
    for (const x of [65504, 65519.99999999999, 65520, 1e300, Infinity, -Infinity, Number.NaN]) {
      expect(encodeF16(x), `encode(${x})`).toBe(expected(x));
    }
  });

  it('encodes random doubles identically, across the whole bit space and the half range', () => {
    // xorshift32, seeded: the same doubles on every run.
    let state = 0x9e3779b9;
    const next = (): number => {
      state ^= state << 13;
      state ^= state >>> 17;
      state ^= state << 5;
      return state >>> 0;
    };

    const view = new DataView(new ArrayBuffer(8));
    for (let i = 0; i < 200_000; i++) {
      let x: number;
      if (i % 2 === 0) {
        view.setUint32(0, next());
        view.setUint32(4, next());
        x = view.getFloat64(0);
      } else {
        // Uniform in magnitude over [2^-26, 2^17): subnormal halves up to overflow.
        x = (next() / 0x100000000) * 2 ** ((next() % 43) - 26) * (next() & 1 ? -1 : 1);
      }

      const mine = encodeF16(x);
      if (mine !== expected(x)) {
        expect(`0x${mine.toString(16)}`, `encode(${x})`).toBe(`0x${expected(x).toString(16)}`);
      }
    }
  });
});
