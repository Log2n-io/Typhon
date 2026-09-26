import { ValueKind, type FieldPlan, type SectionPlan } from './catalog.js';
import { CodecKind } from './codec-kinds.js';
import { ProtocolConstants } from './constants.js';
import { malformed } from './errors.js';
import {
  decodeQuat3Halves,
  encodeAngle,
  encodeQuant,
  encodeQuat3,
  encodeSnorm,
  encodeUnorm,
  encodeVec,
  encodeVel,
  TAU,
} from './math.js';
import type { WireReader } from './reader.js';
import type { WireWriter } from './writer.js';

/**
 * Receives the values of a decoded section, one call per field, in wire order (C# `IFieldSink`). Every array or view it
 * is handed is reused by the decoder: valid only for the duration of the call.
 */
export interface FieldSink {
  /** A numeric field: `values[0 .. field.components)`. */
  number(field: FieldPlan, values: Float64Array): void;
  /** A text field, already validated as UTF-8. */
  text(field: FieldPlan, value: string): void;
  /** A `bytes` or `blob` field: `data[offset .. offset + length)`, a view of the message. */
  bytes(field: FieldPlan, data: Uint8Array, offset: number, length: number): void;
  /** A list: `count` elements of `field.components` numbers each, flattened in `values[0 .. count × components)`. */
  list(field: FieldPlan, count: number, values: Float64Array): void;
}

/**
 * A value to encode: a number (or boolean) for a scalar; numbers for a vector, a quaternion or a list's flattened
 * elements; a string for `str`; bytes for `bytes`, `blob`, or an unknown codec written verbatim.
 */
export type FieldValue = number | boolean | ArrayLike<number> | string | Uint8Array;

/** Field values by wire name. */
export type FieldValues = Readonly<Record<string, FieldValue | undefined>>;

/** The largest number of components one decode yields, a list's included. */
export const MAX_LIST_COMPONENTS = ProtocolConstants.maxListCount * 4;

// Shared by every decode: a value is handed to its sink before the next one is read, so one buffer of each is enough.
const scalar = new Float64Array(4);
const listValues = new Float64Array(MAX_LIST_COMPONENTS);

/**
 * Decodes a section (§ 5, W11–W13): the leading bit pack, then each byte-aligned field in wire order. `strictEnums`
 * applies the client → server rule — an enum value outside its names is malformed (1007); server → client it decodes as
 * the bare integer.
 */
export function readSection(
  r: WireReader,
  section: SectionPlan,
  frameTick: number,
  sink: FieldSink,
  strictEnums = false,
): void {
  const fields = section.fields;
  const packedCount = section.packedCount;
  if (section.packBytes > 0) {
    const at = r.take(section.packBytes);
    for (let i = 0; i < packedCount; i++) {
      const f = fields[i]!;
      const value = readPackedBits(r.bytes, at, section.packBytes, f.bitOffset, f.bitCount);
      if (strictEnums) {
        checkEnum(f, value);
      }

      scalar[0] = value;
      sink.number(f, scalar);
    }
  }

  for (let i = packedCount; i < fields.length; i++) {
    const f = fields[i]!;
    switch (f.valueKind) {
      case ValueKind.Number:
        readNumber(r, f, frameTick, scalar, 0);
        if (strictEnums) {
          checkEnum(f, scalar[0]!);
        }

        sink.number(f, scalar);
        break;
      case ValueKind.Text:
        sink.text(f, r.str(f.maxBytes));
        break;
      case ValueKind.Bytes: {
        const length = f.kind === CodecKind.Bytes ? f.n : r.blobLength(f.maxBytes);
        sink.bytes(f, r.bytes, r.take(length), length);
        break;
      }
      case ValueKind.List:
        readList(r, f, frameTick, sink);
        break;
      default:
        r.skip(f.fixedBytes);
        break;
    }
  }
}

/**
 * Decodes one numeric value of a byte-aligned field (or list element, or position codec) into `out` at `offset`.
 *
 * **No double crosses a call.** V8 does not inline a call site it reaches rarely — a position read once per enter among
 * thousands of state fields — and boxes every number beyond 2^30, or with a fraction, passed to or returned from such a
 * call. So wide codes are read into `out` and dequantized in place, with the arithmetic of `math.ts`'s `decode*`
 * functions written out here. The golden codec test runs every case through both and holds each to the vector's bits.
 */
export function readNumber(r: WireReader, f: FieldPlan, frameTick: number, out: Float64Array, offset: number): void {
  switch (f.kind) {
    case CodecKind.U8:
      out[offset] = r.u8();
      break;
    case CodecKind.I8:
      out[offset] = r.i8();
      break;
    case CodecKind.U16:
      out[offset] = r.u16();
      break;
    case CodecKind.I16:
      out[offset] = r.i16();
      break;
    case CodecKind.U32:
      r.u32Into(out, offset);
      break;
    case CodecKind.I32:
      r.i32Into(out, offset);
      break;
    case CodecKind.Varu:
    case CodecKind.EntityRef:
      r.varuInto(out, offset);
      break;
    case CodecKind.Vari:
      r.variInto(out, offset);
      break;
    case CodecKind.F32:
      r.f32Into(out, offset);
      break;
    case CodecKind.F16:
      r.f16Into(out, offset);
      break;
    case CodecKind.Quant:
    case CodecKind.Pos2:
    case CodecKind.Pos3:
      // decodeQuant: min + q × step.
      for (let i = 0; i < f.components; i++) {
        r.unsignedInto(f.bits, out, offset + i);
        out[offset + i] = f.min[i]! + out[offset + i]! * f.step[i]!;
      }

      break;
    case CodecKind.Vec2:
    case CodecKind.Vec3:
      // decodeVec: max(q, −limit) × scale.
      for (let i = 0; i < f.components; i++) {
        r.signedInto(f.bits, out, offset + i);
        const q = out[offset + i]!;
        out[offset + i] = (q < -f.limit ? -f.limit : q) * f.scale;
      }

      break;
    case CodecKind.Vel2:
    case CodecKind.Vel3:
      // decodeVel: max(q, −limit) × 2^unitExp.
      for (let i = 0; i < f.components; i++) {
        r.signedInto(f.bits, out, offset + i);
        const q = out[offset + i]!;
        out[offset + i] = (q < -f.limit ? -f.limit : q) * f.velocityUnit;
      }

      break;
    case CodecKind.Unorm:
      // decodeUnorm: q ÷ top.
      r.unsignedInto(f.bits, out, offset);
      out[offset] = out[offset]! / f.top;
      break;
    case CodecKind.Snorm: {
      // decodeSnorm: max(q ÷ limit, −1).
      r.signedInto(f.bits, out, offset);
      const x = out[offset]! / f.limit;
      out[offset] = x < -1 ? -1 : x;
      break;
    }
    case CodecKind.Angle:
      // decodeAngle: q × τ ÷ 2^bits, where 2^bits = top + 1 exactly.
      r.signedInto(f.bits, out, offset);
      out[offset] = (out[offset]! * TAU) / (f.top + 1);
      break;
    case CodecKind.Quat3: {
      const low = r.u16();
      decodeQuat3Halves(low, r.u16(), out, offset);
      break;
    }
    case CodecKind.TickLo: {
      // decodeTickLo.
      const low = r.u16();
      out[offset] = (frameTick - ((frameTick - low) & 0xffff)) >>> 0;
      break;
    }
    default:
      throw new Error(`'${f.codec.t}' is not a byte-aligned numeric codec`);
  }
}

function readList(r: WireReader, f: FieldPlan, frameTick: number, sink: FieldSink): void {
  const count = r.varuAtMost(f.maxCount, 'list count');
  if (count < f.minCount) {
    throw malformed(`list '${f.name}' has ${count} element(s); at least ${f.minCount} required`);
  }

  const element = f.element!;
  const stride = f.components;
  for (let e = 0; e < count; e++) {
    readNumber(r, element, frameTick, listValues, e * stride);
  }

  sink.list(f, count, listValues);
}

function checkEnum(f: FieldPlan, value: number): void {
  const names = f.enumNames;
  if (names !== null && value >= names.length) {
    throw malformed(`field '${f.name}': enum value ${value} is outside its ${names.length} name(s)`);
  }
}

/**
 * Extracts `count` ≤ 24 bits at bit `offset` of the pack at `bytes[at .. at + length)`, least significant bit first.
 */
export function readPackedBits(bytes: Uint8Array, at: number, length: number, offset: number, count: number): number {
  const byteIndex = offset >> 3;
  let window = 0;
  for (let i = 0; i < 4 && byteIndex + i < length; i++) {
    window |= bytes[at + byteIndex + i]! << (8 * i);
  }

  return (window >>> (offset & 7)) & ((1 << count) - 1);
}

/**
 * Stores `count` bits of `value` at bit `offset` of the pack starting at `bytes[at]`; the pack must be zeroed first.
 */
export function writePackedBits(bytes: Uint8Array, at: number, offset: number, count: number, value: number): void {
  for (let i = 0; i < count; i++) {
    if (((value >>> i) & 1) !== 0) {
      const bit = offset + i;
      const index = at + (bit >> 3);
      bytes[index] = bytes[index]! | (1 << (bit & 7));
    }
  }
}

// ---- encoding -------------------------------------------------------------------------------------------------------

/**
 * Encodes a section from `values`, by field name. `strictEnums` refuses an enum value outside its names — what a client
 * must never send (W13). Values that cannot be represented throw a `RangeError`: a bug on this side, never peer input.
 */
export function writeSection(w: WireWriter, section: SectionPlan, values: FieldValues, strictEnums = false): void {
  const fields = section.fields;
  if (section.packBytes > 0) {
    const at = w.zeroes(section.packBytes);
    for (let i = 0; i < section.packedCount; i++) {
      const f = fields[i]!;
      const v = numberOf(ownValue(values, f.name), f, 0);
      let code: number;
      if (f.kind === CodecKind.Bool) {
        code = v !== 0 ? 1 : 0;
      } else {
        code = toUnsigned(v, (1 << f.bitCount) - 1, f);
        if (strictEnums) {
          refuseEnum(f, code);
        }
      }

      writePackedBits(w.bytes, at, f.bitOffset, f.bitCount, code);
    }
  }

  for (let i = section.packedCount; i < fields.length; i++) {
    const f = fields[i]!;
    const value = ownValue(values, f.name);
    switch (f.valueKind) {
      case ValueKind.Number:
        if (strictEnums && f.enumNames !== null) {
          refuseEnum(f, numberOf(value, f, 0));
        }

        writeNumber(w, f, componentsOf(value, f));
        break;
      case ValueKind.Text:
        if (typeof value !== 'string') {
          throw new RangeError(`field '${f.name}' needs a string`);
        }

        w.str(value, f.maxBytes);
        break;
      case ValueKind.Bytes:
        if (!(value instanceof Uint8Array)) {
          throw new RangeError(`field '${f.name}' needs bytes`);
        }

        if (f.kind === CodecKind.Bytes) {
          if (value.length !== f.n) {
            throw new RangeError(`field '${f.name}' needs exactly ${f.n} bytes`);
          }

          w.raw(value);
        } else {
          w.blob(value, f.maxBytes);
        }

        break;
      case ValueKind.List:
        writeList(w, f, componentsOf(value, f));
        break;
      default:
        // A codec newer than this library: only its width is known, so the caller supplies the encoded bytes verbatim.
        if (!(value instanceof Uint8Array) || value.length !== f.fixedBytes) {
          throw new RangeError(`field '${f.name}' has an unknown codec; supply exactly ${f.fixedBytes} encoded bytes`);
        }

        w.raw(value);
        break;
    }
  }
}

/** Encodes one numeric value of a byte-aligned field (or list element, or position codec) from `c[offset ..]`. */
export function writeNumber(w: WireWriter, f: FieldPlan, c: ArrayLike<number>, offset = 0): void {
  if (c.length - offset < f.components) {
    throw new RangeError(`field '${f.name}' needs ${f.components} component(s), got ${c.length - offset}`);
  }

  const v = c[offset]!;
  switch (f.kind) {
    case CodecKind.U8:
      w.u8(toUnsigned(v, 0xff, f));
      break;
    case CodecKind.I8:
      w.u8(toSigned(v, -0x80, 0x7f, f));
      break;
    case CodecKind.U16:
      w.u16(toUnsigned(v, 0xffff, f));
      break;
    case CodecKind.I16:
      w.u16(toSigned(v, -0x8000, 0x7fff, f));
      break;
    case CodecKind.U32:
      w.u32(toUnsigned(v, 0xffffffff, f));
      break;
    case CodecKind.I32:
      w.u32(toSigned(v, -0x80000000, 0x7fffffff, f));
      break;
    case CodecKind.Varu:
    case CodecKind.EntityRef:
      w.varu(toUnsigned(v, 0xffffffff, f));
      break;
    case CodecKind.Vari:
      w.vari(toSigned(v, -0x80000000, 0x7fffffff, f));
      break;
    case CodecKind.F32:
      w.f32(v);
      break;
    case CodecKind.F16:
      w.f16(v);
      break;
    case CodecKind.Quant:
      w.bits(encodeQuant(v, f.min[0]!, f.step[0]!, f.top), f.bits);
      break;
    case CodecKind.Pos2:
    case CodecKind.Pos3:
      for (let i = 0; i < f.components; i++) {
        w.bits(encodeQuant(c[offset + i]!, f.min[i]!, f.step[i]!, f.top), f.bits);
      }

      break;
    case CodecKind.Vec2:
    case CodecKind.Vec3:
      for (let i = 0; i < f.components; i++) {
        w.bits(encodeVec(c[offset + i]!, f.scale, f.limit), f.bits);
      }

      break;
    case CodecKind.Vel2:
    case CodecKind.Vel3:
      for (let i = 0; i < f.components; i++) {
        w.bits(encodeVel(c[offset + i]!, f.velocityUnit, f.limit), f.bits);
      }

      break;
    case CodecKind.Unorm:
      w.bits(encodeUnorm(v, f.top), f.bits);
      break;
    case CodecKind.Snorm:
      w.bits(encodeSnorm(v, f.limit), f.bits);
      break;
    case CodecKind.Angle:
      w.bits(encodeAngle(v, f.bits), f.bits);
      break;
    case CodecKind.Quat3:
      w.u32(encodeQuat3(v, c[offset + 1]!, c[offset + 2]!, c[offset + 3]!));
      break;
    case CodecKind.TickLo:
      w.u16(toUnsigned(v, 0xffffffff, f) & 0xffff);
      break;
    default:
      throw new Error(`'${f.codec.t}' is not a byte-aligned numeric codec`);
  }
}

function writeList(w: WireWriter, f: FieldPlan, flattened: ArrayLike<number>): void {
  const stride = f.components;
  if (stride === 0 || flattened.length % stride !== 0) {
    throw new RangeError(`list '${f.name}' needs a multiple of ${stride} numbers`);
  }

  const count = flattened.length / stride;
  if (count < f.minCount || count > f.maxCount) {
    throw new RangeError(`list '${f.name}' has ${count} element(s); ${f.minCount}..${f.maxCount} allowed`);
  }

  const element = f.element!;
  w.varu(count);
  for (let e = 0; e < count; e++) {
    writeNumber(w, element, flattened, e * stride);
  }
}

/**
 * A field's value, when `values` has it as an own property: a field named `constructor` must not pick up the object's
 * inherited member. Absent, it is `undefined`, which every caller refuses with a `RangeError`.
 */
function ownValue(values: FieldValues, name: string): FieldValue | undefined {
  return Object.hasOwn(values, name) ? values[name] : undefined;
}

function numberOf(value: FieldValue | undefined, f: FieldPlan, index: number): number {
  if (typeof value === 'number') {
    return value;
  }

  if (typeof value === 'boolean') {
    return value ? 1 : 0;
  }

  if (value === undefined || typeof value === 'string' || value.length <= index) {
    throw new RangeError(`no numeric value supplied for field '${f.name}'`);
  }

  return value[index]!;
}

const one: number[] = [0];

function componentsOf(value: FieldValue | undefined, f: FieldPlan): ArrayLike<number> {
  if (typeof value === 'number' || typeof value === 'boolean') {
    one[0] = numberOf(value, f, 0);
    return one;
  }

  if (value === undefined || typeof value === 'string') {
    throw new RangeError(`no numeric value supplied for field '${f.name}'`);
  }

  return value;
}

function refuseEnum(f: FieldPlan, value: number): void {
  const names = f.enumNames;
  if (names !== null && value >= names.length) {
    throw new RangeError(`field '${f.name}': enum value ${value} is outside its ${names.length} name(s)`);
  }
}

function toUnsigned(v: number, max: number, f: FieldPlan): number {
  if (!(v >= 0) || v > max || Math.floor(v) !== v) {
    throw new RangeError(`field '${f.name}': ${v} is not an integer in [0, ${max}]`);
  }

  return v;
}

function toSigned(v: number, min: number, max: number, f: FieldPlan): number {
  if (!(v >= min) || v > max || Math.floor(v) !== v) {
    throw new RangeError(`field '${f.name}': ${v} is not an integer in [${min}, ${max}]`);
  }

  return v;
}
