/**
 * The codec arithmetic of `03-wire-protocol.md` § 12.1 (W1–W10): quantization, normalized values, angles,
 * smallest-three quaternions, half floats and low-bit ticks, bit-identical with the C# reference
 * (`Typhon.Protocol/Wire/WireMath.cs`).
 *
 * **The rules that make that true (W1).** Everything is binary64. Ties round half away from zero
 * ({@link roundHalfAway}); `Math.round` alone is used only where x > 0, and `Math.floor(x + 0.5)` never. Values are
 * clamped in double before any integer conversion. Only `+ − × ÷`, `Math.sqrt`, `Math.floor`, `Math.abs` and
 * comparisons appear; powers of two come from {@link pow2}, built by doubling, never from `Math.pow` or `**`. Each
 * formula is written left to right, one IEEE operation per step, exactly as § 12 spells it: reordering a product "for
 * speed" changes the last bit, and the golden vectors exist to catch that.
 *
 * Decoders take precomputed parameters (a step, a limit) so the hot path does no redundant division; each parameter is
 * produced by the same single operation the formula names, so the result is the same double.
 */

/** 2π, as C#'s `Math.Tau` spells it (6.283185307179586). */
export const TAU = 2 * Math.PI;

/** √½, verified bit-identical to C#'s `Math.Sqrt(0.5)` (W8). */
export const SQRT1_2 = Math.SQRT1_2;

const POW2_BIAS = 64;
const POW2 = buildPow2();

/** 2^e as an exact double, for e in [−64, 64]. */
export function pow2(e: number): number {
  return POW2[e + POW2_BIAS]!;
}

/** Rounds half away from zero (`rha`). */
export function roundHalfAway(x: number): number {
  return x < 0 ? -Math.round(-x) : Math.round(x);
}

/** 2^(b−1) − 1: the largest magnitude of a symmetric signed code. */
export function symmetricLimit(bits: number): number {
  return pow2(bits - 1) - 1;
}

/** 2^b − 1: the top code of an unsigned quantizer. */
export function unsignedTop(bits: number): number {
  return pow2(bits) - 1;
}

// ---- quant, pos (W2, W3) --------------------------------------------------------------------------------------------

/** The quantization step of a `quant` or of one `pos` axis: (max − min) / 2^b. */
export function quantStep(min: number, max: number, bits: number): number {
  return (max - min) / pow2(bits);
}

/** Encodes over [min, max): NaN, −∞ and anything below min give 0; anything at or above max − step/2 the top code. */
export function encodeQuant(v: number, min: number, step: number, top: number): number {
  const x = (v - min) / step;
  if (!(x > 0)) {
    return 0;
  }

  const r = Math.round(x);
  return r > top ? top : r;
}

/** `min + q × step`; never clamps. */
export function decodeQuant(q: number, min: number, step: number): number {
  return min + q * step;
}

// ---- vec (W4) -------------------------------------------------------------------------------------------------------

/** Encodes one `vec` component: `scale` is one step; signed, clamped symmetrically to ±limit. NaN gives 0. */
export function encodeVec(v: number, scale: number, limit: number): number {
  if (v !== v) {
    return 0;
  }

  const r = roundHalfAway(v / scale);
  return r > limit ? limit : r < -limit ? -limit : r;
}

/** Decodes one `vec` component; the code −2^(b−1) reads as −limit. */
export function decodeVec(q: number, scale: number, limit: number): number {
  return (q < -limit ? -limit : q) * scale;
}

// ---- vel (W5) -------------------------------------------------------------------------------------------------------

/**
 * Encodes one `vel` axis (W5, `typhon.3`): displacement per tick in units of `unit` = 2^unitExp metres — absolute, the
 * same in every realm — rounded half away from zero, symmetric clamp. Server-side only.
 */
export function encodeVel(d: number, unit: number, limit: number): number {
  const x = d / unit;
  if (x !== x) {
    return 0;
  }

  const r = roundHalfAway(x);
  return r > limit ? limit : r < -limit ? -limit : r;
}

/** Decodes one `vel` axis to world units per tick: `max(q, −limit) × unit`, exact (the unit is dyadic). */
export function decodeVel(q: number, unit: number, limit: number): number {
  return (q < -limit ? -limit : q) * unit;
}

// ---- unorm, snorm (W6) ----------------------------------------------------------------------------------------------

/** Encodes a `unorm`: rhu(v × top), clamped to [0, top]; NaN and anything ≤ 0 give 0. */
export function encodeUnorm(v: number, top: number): number {
  const x = v * top;
  if (!(x > 0)) {
    return 0;
  }

  const r = Math.round(x);
  return r > top ? top : r;
}

/** `q / top` — a division, not a multiplication by the reciprocal, which differs in the last bit. */
export function decodeUnorm(q: number, top: number): number {
  return q / top;
}

/** Encodes a `snorm`: rha(clamp(v, −1, 1) × limit); NaN gives 0. */
export function encodeSnorm(v: number, limit: number): number {
  if (v !== v) {
    return 0;
  }

  const c = v < -1 ? -1 : v > 1 ? 1 : v;
  return roundHalfAway(c * limit);
}

/** `max(q / limit, −1)`: −1, 0 and 1 exact. */
export function decodeSnorm(q: number, limit: number): number {
  const x = q / limit;
  return x < -1 ? -1 : x;
}

// ---- angle (W7) -----------------------------------------------------------------------------------------------------

/**
 * Encodes radians as a two's-complement code over [−π, π). Non-finite values, and values whose code would exceed 2^53
 * in magnitude (beyond which TypeScript and C# diverge), give 0.
 */
export function encodeAngle(theta: number, bits: number): number {
  if (!Number.isFinite(theta)) {
    return 0;
  }

  const p = pow2(bits);
  const k = roundHalfAway((theta * p) / TAU);
  if (!(Math.abs(k) <= 9007199254740992)) {
    return 0;
  }

  return k - p * Math.floor((k + pow2(bits - 1)) / p);
}

/** `(q × τ) / 2^b`: radians in [−π, π). */
export function decodeAngle(q: number, bits: number): number {
  return (q * TAU) / pow2(bits);
}

// ---- quat3 (W8) -----------------------------------------------------------------------------------------------------

const quatScratch = new Float64Array(4);

/**
 * Encodes a rotation as smallest-three in 32 bits: the dropped index in bits 0–1, then three 10-bit two's-complement
 * components in ascending axis order. A zero, NaN or infinite norm encodes the identity.
 */
export function encodeQuat3(x: number, y: number, z: number, w: number): number {
  const n = Math.sqrt(x * x + y * y + z * z + w * w);
  const c = quatScratch;
  if (!(n > 0) || n === Infinity) {
    c[0] = 0;
    c[1] = 0;
    c[2] = 0;
    c[3] = 1;
  } else {
    c[0] = x / n;
    c[1] = y / n;
    c[2] = z / n;
    c[3] = w / n;
  }

  let largest = 0;
  for (let k = 1; k < 4; k++) {
    if (Math.abs(c[k]!) > Math.abs(c[largest]!)) {
      largest = k;
    }
  }

  if (c[largest]! < 0) {
    for (let k = 0; k < 4; k++) {
      c[k] = -c[k]!;
    }
  }

  let bits = largest;
  let shift = 2;
  for (let k = 0; k < 4; k++) {
    if (k === largest) {
      continue;
    }

    const r = roundHalfAway((c[k]! / SQRT1_2) * 511);
    const e = r > 511 ? 511 : r < -511 ? -511 : r;
    bits |= (e & 0x3ff) << shift;
    shift += 10;
  }

  return bits >>> 0;
}

/** Decodes a smallest-three rotation into `out[offset..offset + 4)` as x, y, z, w. */
export function decodeQuat3(bits: number, out: Float64Array, offset: number): void {
  decodeQuat3Halves(bits & 0xffff, (bits >>> 16) & 0xffff, out, offset);
}

/**
 * {@link decodeQuat3} from the code's low and high 16 bits: two small integers, where a 32-bit code above 2^30 passed
 * to a call V8 does not inline is boxed into a heap number.
 */
export function decodeQuat3Halves(low: number, high: number, out: Float64Array, offset: number): void {
  const bits = low | (high << 16);
  const largest = bits & 3;
  let sum = 0;
  let shift = 2;
  for (let k = 0; k < 4; k++) {
    if (k === largest) {
      continue;
    }

    let e = ((((bits >>> shift) & 0x3ff) << 22) >> 22) | 0;
    if (e < -511) {
      e = -511;
    }

    const v = (e / 511) * SQRT1_2;
    out[offset + k] = v;
    sum += v * v;
    shift += 10;
  }

  const rest = 1 - sum;
  out[offset + largest] = Math.sqrt(rest > 0 ? rest : 0);
}

// ---- f16 (W10) ------------------------------------------------------------------------------------------------------

const f16Bits = new DataView(new ArrayBuffer(8));

/**
 * Converts a double straight to IEEE half bits: ties to even, overflow to infinity, NaN as `0x7E00`. Hand-written
 * because Node 22 and Safari < 18.2 lack `Math.f16round`; always convert from the source's own precision, never through
 * a float32.
 */
export function encodeF16(x: number): number {
  if (x !== x) {
    return 0x7e00;
  }

  const s = x < 0 || (x === 0 && 1 / x < 0) ? 0x8000 : 0;
  const a = Math.abs(x);
  if (a === Infinity) {
    return s | 0x7c00;
  }

  if (a < pow2(-14)) {
    // Subnormal; r = 1024 lands on the smallest normal.
    return s | roundHalfEven(a * pow2(24));
  }

  f16Bits.setFloat64(0, a);
  let e = ((f16Bits.getUint16(0) >> 4) & 0x7ff) - 1023;
  if (e > 15) {
    return s | 0x7c00;
  }

  let r = roundHalfEven(a * pow2(10 - e));
  if (r === 2048) {
    r = 1024;
    e += 1;
  }

  if (e > 15) {
    return s | 0x7c00;
  }

  return s | ((e + 15) << 10) | (r - 1024);
}

const f16Value = new Float64Array(1);

/** Decodes IEEE half bits, exactly; any NaN pattern decodes as NaN. */
export function decodeF16(bits: number): number {
  decodeF16Into(bits, f16Value, 0);
  return f16Value[0]!;
}

/** {@link decodeF16} into `out[offset]`: a decoder's hot path stores the double rather than returning it boxed. */
export function decodeF16Into(bits: number, out: Float64Array, offset: number): void {
  const e = (bits >> 10) & 31;
  const f = bits & 0x3ff;
  let v: number;
  if (e === 0) {
    v = f / 16777216;
  } else if (e === 31) {
    if (f !== 0) {
      // Unsigned: the canonical NaN, whatever the pattern's sign.
      out[offset] = NaN;
      return;
    }

    v = Infinity;
  } else {
    v = (1024 + f) * pow2(e - 25);
  }

  out[offset] = (bits & 0x8000) !== 0 ? -v : v;
}

/** Ties to even over m < 2^11, where every step is exact. */
function roundHalfEven(m: number): number {
  const f = Math.floor(m);
  const d = m - f;
  return d > 0.5 ? f + 1 : d < 0.5 ? f : f + (f % 2);
}

// ---- tickLo (W9) ----------------------------------------------------------------------------------------------------

/**
 * Rebuilds a past absolute tick from its low 16 bits against the frame's tick: `(f − ((f − lo) & 0xFFFF)) >>> 0`, exact
 * because `(f − lo) & 0xFFFF` is the value mod 2^16 whenever f − lo ∈ (−2^16, 2^32).
 */
export function decodeTickLo(low: number, frameTick: number): number {
  return (frameTick - ((frameTick - low) & 0xffff)) >>> 0;
}

function buildPow2(): Float64Array {
  const table = new Float64Array(2 * POW2_BIAS + 1);
  table[POW2_BIAS] = 1;
  for (let i = 1; i <= POW2_BIAS; i++) {
    table[POW2_BIAS + i] = table[POW2_BIAS + i - 1]! * 2;
    table[POW2_BIAS - i] = table[POW2_BIAS - i + 1]! / 2;
  }

  return table;
}
