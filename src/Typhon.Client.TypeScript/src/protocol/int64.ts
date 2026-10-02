/**
 * 64-bit integers (W32) as two 32-bit words, `lo` then `hi` — the form a decoder hands them over in, because a `number`
 * cannot hold them and a `bigint` is a heap object per value: words copy into a store's `BigUint64Array` /
 * `BigInt64Array` column with no allocation. A signed value is its two's complement; {@link bigintOf} turns words into the
 * value an application reads, and {@link wordsOf} goes the other way for an encoder.
 */

const LITTLE_ENDIAN = new Uint8Array(Uint16Array.of(1).buffer)[0] === 1;

/** The `Uint32Array` index of the low word of 64-bit element `i` of a view over the same buffer. */
export const LOW_WORD = LITTLE_ENDIAN ? 0 : 1;

/** The `Uint32Array` index of the high word of 64-bit element `i`, relative to `2 × i`. */
export const HIGH_WORD = LITTLE_ENDIAN ? 1 : 0;

/** The value of words `lo`, `hi`: unsigned, or signed when `signed`. Allocates a `bigint`. */
export function bigintOf(lo: number, hi: number, signed: boolean): bigint {
  const u = (BigInt(hi >>> 0) << 32n) | BigInt(lo >>> 0);
  return signed ? BigInt.asIntN(64, u) : u;
}

/**
 * Splits a 64-bit value into `out[at] = lo`, `out[at + 1] = hi`. A `number` must be a safe integer; a value outside the
 * codec's range throws a `RangeError` — a bug on the encoding side, never peer input.
 */
export function wordsOf(value: bigint | number, signed: boolean, out: Uint32Array, at: number, what: string): void {
  let v: bigint;
  if (typeof value === 'number') {
    if (!Number.isSafeInteger(value)) {
      throw new RangeError(`${what}: ${value} is not a safe integer; pass a bigint for a 64-bit value`);
    }

    v = BigInt(value);
  } else {
    v = value;
  }

  const inRange = signed ? v >= -(1n << 63n) && v < 1n << 63n : v >= 0n && v < 1n << 64n;
  if (!inRange) {
    throw new RangeError(`${what}: ${v} is outside the ${signed ? 'signed' : 'unsigned'} 64-bit range`);
  }

  const u = BigInt.asUintN(64, v);
  out[at] = Number(u & 0xffffffffn);
  out[at + 1] = Number(u >> 32n);
}

/** Words `lo`, `hi` as sixteen lower-case hex digits, most significant first: the golden vectors' notation. */
export function hex64(lo: number, hi: number): string {
  return (hi >>> 0).toString(16).padStart(8, '0') + (lo >>> 0).toString(16).padStart(8, '0');
}
