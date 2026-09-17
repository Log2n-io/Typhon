import { CloseCode } from './constants.js';
import { malformed, WireFormatError } from './errors.js';
import { decodeF16Into } from './math.js';
import { decodeUtf8 } from './utf8.js';

const EMPTY = new Uint8Array(0);

/** One float32's bytes, and the float they spell in the platform's byte order. */
const singleBytes = new Uint8Array(4);
const single = new Float32Array(singleBytes.buffer);
const LITTLE_ENDIAN = new Uint8Array(Uint16Array.of(1).buffer)[0] === 1;
const scratch = new Float64Array(1);

/**
 * Reads the wire's primitives (`03-wire-protocol.md` § 2) from a byte array: little-endian fixed-width integers, LEB128
 * varints, half and single floats, and length-prefixed strings and blobs.
 *
 * - **Every read is bounds-checked** against the current limit and throws {@link WireFormatError} (1007) when the input
 *   is short, so a decoder built on it never reads past its message or its block.
 * - **Blocks are isolated without allocation**: {@link pushLimit} narrows the readable range to a block's declared
 *   length, and {@link popLimit} restores it and reports what the block left unread.
 * - **Reusable, and allocation-free.** {@link reset} points the reader at the next message; integers are assembled from
 *   the bytes rather than read through a `DataView`, which would cost one per message. Byte payloads are handed out as
 *   an offset into {@link bytes} rather than copied.
 * - **`…Into` reads store a double instead of returning it.** A number with a fraction, or beyond the small-integer
 *   range, returned from a call V8 does not inline is boxed into a heap number; stored into a `Float64Array` it is not.
 *   The small-integer range ends at 2^31 in Node and at 2^30 in browsers with pointer compression (Chrome): the Node
 *   tests observe the first bound, not the second. Per-record decoding uses these reads (AC-6).
 * - **Released, it refuses to read.** {@link release} drops the message; any read after it throws, so a read that
 *   re-entered a finished decode fails loudly instead of reading `undefined` past a restored limit.
 * - **Varints are read leniently.** An over-long encoding (`80 00` for zero) is accepted as long as the value fits 32
 *   bits in at most five bytes: encoders always write the minimal form, so golden vectors stay canonical while a
 *   decoder does not have to police it.
 */
export class WireReader {
  private pos = 0;
  private message: Uint8Array = EMPTY;
  private limit = 0;

  constructor(bytes?: Uint8Array) {
    if (bytes !== undefined) {
      this.reset(bytes);
    }
  }

  /** Points the reader at `bytes`, positioned at its first byte. */
  reset(bytes: Uint8Array): this {
    this.message = bytes;
    this.pos = 0;
    this.limit = bytes.length;
    return this;
  }

  /** Lets go of the message, so a reader kept for reuse does not keep the last one reachable. */
  release(): void {
    this.message = EMPTY;
    this.pos = 0;
    this.limit = 0;
  }

  /** Absolute read position in {@link bytes}. */
  get position(): number {
    return this.pos;
  }

  /** The message being read. Offsets returned by {@link take} index into it. */
  get bytes(): Uint8Array {
    return this.message;
  }

  /** Bytes left before the current limit. */
  get remaining(): number {
    return this.limit - this.pos;
  }

  get isAtEnd(): boolean {
    return this.pos === this.limit;
  }

  u8(): number {
    if (this.pos >= this.limit || this.message === EMPTY) {
      throw this.message === EMPTY ? released() : short(1, this.remaining);
    }

    return this.message[this.pos++]!;
  }

  i8(): number {
    const at = this.take(1);
    return (this.message[at]! << 24) >> 24;
  }

  u16(): number {
    const at = this.take(2);
    const b = this.message;
    return b[at]! | (b[at + 1]! << 8);
  }

  i16(): number {
    return (this.u16() << 16) >> 16;
  }

  /** `b0 | b1 << 8 | b2 << 16`. */
  u24(): number {
    const at = this.take(3);
    const b = this.message;
    return b[at]! | (b[at + 1]! << 8) | (b[at + 2]! << 16);
  }

  /** A `u24` sign-extended from bit 23. */
  i24(): number {
    return (this.u24() << 8) >> 8;
  }

  /** Always non-negative, in [0, 2^32). */
  u32(): number {
    return this.i32() >>> 0;
  }

  /** {@link u32} into `out[offset]`. Assembled here: an `i32()` result beyond ±2^30 would be boxed crossing the call. */
  u32Into(out: Float64Array, offset: number): void {
    const at = this.take(4);
    const b = this.message;
    out[offset] = (b[at]! | (b[at + 1]! << 8) | (b[at + 2]! << 16) | (b[at + 3]! << 24)) >>> 0;
  }

  /** {@link i32} into `out[offset]`, assembled here for the same reason. */
  i32Into(out: Float64Array, offset: number): void {
    const at = this.take(4);
    const b = this.message;
    out[offset] = b[at]! | (b[at + 1]! << 8) | (b[at + 2]! << 16) | (b[at + 3]! << 24);
  }

  i32(): number {
    const at = this.take(4);
    const b = this.message;
    return b[at]! | (b[at + 1]! << 8) | (b[at + 2]! << 16) | (b[at + 3]! << 24);
  }

  /** An unsigned integer of 8, 16, 24 or 32 bits. */
  unsigned(bits: number): number {
    switch (bits) {
      case 8:
        return this.u8();
      case 16:
        return this.u16();
      case 24:
        return this.u24();
      case 32:
        return this.u32();
      default:
        throw new RangeError(`a byte-aligned width is 8, 16, 24 or 32 bits, not ${bits}`);
    }
  }

  /** {@link unsigned} into `out[offset]`. */
  unsignedInto(bits: number, out: Float64Array, offset: number): void {
    if (bits === 32) {
      this.u32Into(out, offset);
    } else {
      out[offset] = this.unsigned(bits);
    }
  }

  /** {@link signed} into `out[offset]`. */
  signedInto(bits: number, out: Float64Array, offset: number): void {
    if (bits === 32) {
      this.i32Into(out, offset);
    } else {
      out[offset] = this.signed(bits);
    }
  }

  /** A two's-complement integer of 8, 16, 24 or 32 bits, sign-extended. */
  signed(bits: number): number {
    switch (bits) {
      case 8:
        return this.i8();
      case 16:
        return this.i16();
      case 24:
        return this.i24();
      case 32:
        return this.i32();
      default:
        throw new RangeError(`a byte-aligned width is 8, 16, 24 or 32 bits, not ${bits}`);
    }
  }

  /** Unsigned LEB128: at most five bytes, fitting 32 bits. */
  varu(): number {
    let b = this.u8();
    let result = b & 0x7f;
    if ((b & 0x80) === 0) {
      return result;
    }

    for (let shift = 7; shift < 28; shift += 7) {
      b = this.u8();
      result |= (b & 0x7f) << shift;
      if ((b & 0x80) === 0) {
        return result;
      }
    }

    b = this.u8();
    if (b > 0x0f) {
      throw malformed('varu does not fit 32 bits');
    }

    // The fifth byte holds bits 28–31: added, not shifted in, so the result stays a non-negative double.
    return result + b * 0x10000000;
  }

  /** {@link varu} into `out[offset]`: a value beyond the small-integer range is not boxed crossing the call. */
  varuInto(out: Float64Array, offset: number): void {
    let b = this.u8();
    let result = b & 0x7f;
    if ((b & 0x80) === 0) {
      out[offset] = result;
      return;
    }

    for (let shift = 7; shift < 28; shift += 7) {
      b = this.u8();
      result |= (b & 0x7f) << shift;
      if ((b & 0x80) === 0) {
        out[offset] = result;
        return;
      }
    }

    b = this.u8();
    if (b > 0x0f) {
      throw malformed('varu does not fit 32 bits');
    }

    out[offset] = result + b * 0x10000000;
  }

  /** Zigzag-mapped `varu`, in [−2^31, 2^31). */
  vari(): number {
    const u = this.varu();
    return (u >>> 1) ^ -(u & 1);
  }

  /** {@link vari} into `out[offset]`. */
  variInto(out: Float64Array, offset: number): void {
    this.varuInto(out, offset);
    const u = out[offset]!;
    out[offset] = (u >>> 1) ^ -(u & 1);
  }

  /** A `varu` that must not exceed `max`: a count, a length or an index. */
  varuAtMost(max: number, what: string): number {
    const value = this.varu();
    if (value > max) {
      throw malformed(`${what} ${value} exceeds ${max}`);
    }

    return value;
  }

  /** A little-endian IEEE single, widened exactly. */
  f32(): number {
    this.f32Into(scratch, 0);
    return scratch[0]!;
  }

  /** {@link f32} into `out[offset]`. */
  f32Into(out: Float64Array, offset: number): void {
    const at = this.take(4);
    const b = this.message;
    if (LITTLE_ENDIAN) {
      singleBytes[0] = b[at]!;
      singleBytes[1] = b[at + 1]!;
      singleBytes[2] = b[at + 2]!;
      singleBytes[3] = b[at + 3]!;
    } else {
      singleBytes[3] = b[at]!;
      singleBytes[2] = b[at + 1]!;
      singleBytes[1] = b[at + 2]!;
      singleBytes[0] = b[at + 3]!;
    }

    const value = single[0]!;
    // Every NaN pattern becomes the canonical NaN: V8 would otherwise carry a sign and payload no decoder agrees on. Two
    // stores rather than one of a conditional: merging the NaN constant with the value boxes the value (measured).
    if (value === value) {
      out[offset] = value;
    } else {
      out[offset] = NaN;
    }
  }

  /** A little-endian IEEE half, widened exactly; any NaN pattern decodes as NaN. */
  f16(): number {
    this.f16Into(scratch, 0);
    return scratch[0]!;
  }

  /** {@link f16} into `out[offset]`. */
  f16Into(out: Float64Array, offset: number): void {
    decodeF16Into(this.u16(), out, offset);
  }

  /** Advances past `count` bytes and returns the offset of the first in {@link bytes}. */
  take(count: number): number {
    // A negative count is refused like an oversized one, as C#'s unsigned compare does.
    if (!(count >= 0 && count <= this.limit - this.pos) || this.message === EMPTY) {
      throw this.message === EMPTY ? released() : short(count, this.remaining);
    }

    const at = this.pos;
    this.pos += count;
    return at;
  }

  skip(count: number): void {
    this.take(count);
  }

  /** A `str`: a `varu` byte length, at most `maxBytes`, then that many bytes of valid UTF-8. */
  str(maxBytes: number): string {
    const length = this.varuAtMost(maxBytes, 'string length');
    const at = this.take(length);
    if (length === 0) {
      return '';
    }

    const text = decodeUtf8(this.message.subarray(at, at + length));
    if (text === null) {
      throw malformed('string is not valid UTF-8');
    }

    return text;
  }

  /** A `blob`'s length: a `varu` of at most `maxBytes`. The bytes follow; {@link take} them. */
  blobLength(maxBytes: number): number {
    return this.varuAtMost(maxBytes, 'blob length');
  }

  /** A copy of the next `count` bytes. Allocates: for control messages, never the per-record path. */
  copyBytes(count: number): Uint8Array {
    const at = this.take(count);
    return this.message.slice(at, at + count);
  }

  /**
   * Narrows the readable range to the next `length` bytes — a length-prefixed block — and returns the previous limit
   * for {@link popLimit}.
   */
  pushLimit(length: number): number {
    if (!(length >= 0 && length <= this.limit - this.pos)) {
      throw short(length, this.remaining);
    }

    const saved = this.limit;
    this.limit = this.pos + length;
    return saved;
  }

  /**
   * Restores the limit {@link pushLimit} returned and reports how many bytes of the block were left unread. A block
   * whose content is shorter than its declared length is malformed, not padded: the caller throws unless this is 0.
   */
  popLimit(saved: number): number {
    if (this.message === EMPTY) {
      throw released();
    }

    const unread = this.limit - this.pos;
    this.limit = saved;
    return unread;
  }

  /** Throws unless every byte of the message has been consumed. */
  expectEnd(what: string): void {
    if (this.pos !== this.limit) {
      throw new WireFormatError(
        CloseCode.MalformedPayload,
        `${what}: ${this.remaining} unread byte(s) after the declared content`,
      );
    }
  }
}

/** Not the peer's fault: a read after {@link WireReader.release}, from a callback that re-entered a decode. */
function released(): Error {
  return new Error('the reader holds no message: a read re-entered a decode that already ended');
}

function short(needed: number, left: number): WireFormatError {
  return malformed(`needed ${needed} byte(s), ${left} left`);
}
