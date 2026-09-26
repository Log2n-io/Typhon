import { encodeF16 } from './math.js';
import { encodeUtf8 } from './utf8.js';

const MAX_VARU_BYTES = 5;

/**
 * Writes the wire's primitives (`03-wire-protocol.md` § 2) into a growable buffer.
 *
 * - **Canonical output.** Minimal varints, little-endian integers, NaN as `0x7E00` in a half: two encoders fed the same
 *   values produce the same bytes, which is what lets a golden vector be a byte comparison.
 * - **Growable and reusable.** Capacity doubles on demand; {@link reset} rewinds for the next message without releasing
 *   it.
 * - **Integer writers take the low bits** of their argument, so a signed code writes its two's complement. Range checks
 *   belong to the field codec, which knows what a value means.
 */
export class WireWriter {
  private buffer: Uint8Array;
  private view: DataView;
  private pos = 0;

  constructor(initialCapacity = 256) {
    this.buffer = new Uint8Array(Math.max(16, initialCapacity));
    this.view = new DataView(this.buffer.buffer);
  }

  /** Bytes written so far. */
  get position(): number {
    return this.pos;
  }

  /** The backing array. Replaced when the writer grows: re-read it after any write. */
  get bytes(): Uint8Array {
    return this.buffer;
  }

  /** Rewinds to empty, keeping the capacity. */
  reset(): void {
    this.pos = 0;
  }

  /** A view of the bytes written so far; valid until the next write or {@link reset}. */
  written(): Uint8Array {
    return this.buffer.subarray(0, this.pos);
  }

  /** A copy of the bytes written so far. */
  toBytes(): Uint8Array {
    return this.buffer.slice(0, this.pos);
  }

  u8(value: number): void {
    this.ensure(1);
    this.buffer[this.pos++] = value;
  }

  u16(value: number): void {
    this.ensure(2);
    this.view.setUint16(this.pos, value, true);
    this.pos += 2;
  }

  u24(value: number): void {
    this.ensure(3);
    const b = this.buffer;
    b[this.pos] = value;
    b[this.pos + 1] = value >> 8;
    b[this.pos + 2] = value >> 16;
    this.pos += 3;
  }

  u32(value: number): void {
    this.ensure(4);
    this.view.setUint32(this.pos, value >>> 0, true);
    this.pos += 4;
  }

  /** Signed writers: the same bytes as their unsigned twins, named for the reader's symmetry. */
  i8(value: number): void {
    this.u8(value);
  }

  i16(value: number): void {
    this.u16(value);
  }

  i24(value: number): void {
    this.u24(value);
  }

  i32(value: number): void {
    this.u32(value);
  }

  /** The low `bits` ∈ {8, 16, 24, 32} bits of `value`: an unsigned code, or a signed code's two's complement. */
  bits(value: number, bits: number): void {
    switch (bits) {
      case 8:
        this.u8(value);
        break;
      case 16:
        this.u16(value);
        break;
      case 24:
        this.u24(value);
        break;
      case 32:
        this.u32(value);
        break;
      default:
        throw new RangeError(`a byte-aligned width is 8, 16, 24 or 32 bits, not ${bits}`);
    }
  }

  /** Minimal unsigned LEB128 of a value in [0, 2^32). */
  varu(value: number): void {
    let v = value >>> 0;
    this.ensure(MAX_VARU_BYTES);
    const b = this.buffer;
    while (v >= 0x80) {
      b[this.pos++] = (v & 0x7f) | 0x80;
      v >>>= 7;
    }

    b[this.pos++] = v;
  }

  /** Zigzag, then `varu`, of a value in [−2^31, 2^31). */
  vari(value: number): void {
    this.varu(((value << 1) ^ (value >> 31)) >>> 0);
  }

  /**
   * An IEEE single, narrowed with ties to even; NaN as `0x7FC00000`. ECMA-262 lets `setFloat32` write any NaN pattern
   * (V8 keeps the sign of the NaN it was given), so the canonical bytes are written explicitly.
   */
  f32(value: number): void {
    this.ensure(4);
    if (value !== value) {
      this.view.setUint32(this.pos, 0x7fc00000, true);
    } else {
      this.view.setFloat32(this.pos, value, true);
    }

    this.pos += 4;
  }

  /** A little-endian IEEE double; NaN as `0x7FF8000000000000`, the canonical pattern the C# writer emits. */
  f64(value: number): void {
    this.ensure(8);
    if (value !== value) {
      this.view.setUint32(this.pos, 0, true);
      this.view.setUint32(this.pos + 4, 0x7ff80000, true);
    } else {
      this.view.setFloat64(this.pos, value, true);
    }

    this.pos += 8;
  }

  /** An IEEE half converted directly from `value` (W10). */
  f16(value: number): void {
    this.u16(encodeF16(value));
  }

  raw(bytes: Uint8Array): void {
    this.ensure(bytes.length);
    this.buffer.set(bytes, this.pos);
    this.pos += bytes.length;
  }

  /** Reserves `count` zeroed bytes and returns their offset in {@link bytes}. */
  zeroes(count: number): number {
    this.ensure(count);
    const at = this.pos;
    this.buffer.fill(0, at, at + count);
    this.pos += count;
    return at;
  }

  /** A `blob`: a `varu` length, then the bytes. */
  blob(bytes: Uint8Array, maxBytes: number): void {
    if (bytes.length > maxBytes) {
      throw new RangeError(`blob of ${bytes.length} bytes exceeds its cap of ${maxBytes}`);
    }

    this.varu(bytes.length);
    this.raw(bytes);
  }

  /** A `str`: a `varu` byte length, then the UTF-8 bytes. */
  str(text: string, maxBytes: number): void {
    const utf8 = encodeUtf8(text);
    if (utf8.length > maxBytes) {
      throw new RangeError(`string of ${utf8.length} UTF-8 bytes exceeds its cap of ${maxBytes}`);
    }

    this.varu(utf8.length);
    this.raw(utf8);
  }

  /**
   * Reserves room for a `varu` length prefix and returns a mark for {@link endLengthPrefixed}: the content is written
   * next, and the prefix is patched in minimal form afterwards, so a block's size never has to be known before encoding
   * it.
   */
  beginLengthPrefixed(): number {
    const mark = this.pos;
    this.zeroes(MAX_VARU_BYTES);
    return mark;
  }

  /** Writes the minimal `varu` length of everything since `mark` and moves the content down behind it. */
  endLengthPrefixed(mark: number): void {
    const contentStart = mark + MAX_VARU_BYTES;
    const length = this.pos - contentStart;
    const prefix = varuSize(length);
    const b = this.buffer;
    b.copyWithin(mark + prefix, contentStart, this.pos);
    let v = length;
    let at = mark;
    while (v >= 0x80) {
      b[at++] = (v & 0x7f) | 0x80;
      v >>>= 7;
    }

    b[at] = v;
    this.pos = mark + prefix + length;
  }

  private ensure(count: number): void {
    const needed = this.pos + count;
    if (needed <= this.buffer.length) {
      return;
    }

    let capacity = this.buffer.length * 2;
    while (capacity < needed) {
      capacity *= 2;
    }

    const next = new Uint8Array(capacity);
    next.set(this.buffer.subarray(0, this.pos));
    this.buffer = next;
    this.view = new DataView(next.buffer);
  }
}

/** Bytes a minimal `varu` of `value` takes: 1 to 5. */
export function varuSize(value: number): number {
  return value < 0x80 ? 1 : value < 0x4000 ? 2 : value < 0x200000 ? 3 : value < 0x10000000 ? 4 : 5;
}
