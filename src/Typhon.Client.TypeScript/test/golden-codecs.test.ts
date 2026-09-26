import { describe, expect, it } from 'vitest';
import {
  CODEC_TOKENS,
  CodecKind,
  codecKindOf,
  decodeAngle,
  decodeF16,
  decodeQuant,
  decodeQuat3,
  decodeSnorm,
  decodeTickLo,
  decodeUnorm,
  decodeUtf8,
  decodeVec,
  decodeVel,
  FieldPlan,
  isPacked,
  readNumber,
  readSection,
  SectionPlan,
  WireReader,
  WireWriter,
  writeNumber,
  writeSection,
  type CatalogCodec,
  type RealmFrame,
} from '../src/index.js';
import {
  bitsOf,
  fromBits,
  fromHex,
  goldenBin,
  goldenJson,
  goldenNames,
  hex,
  RecordingSink,
  frameFromJson,
  type FrameJson,
} from './golden-support.js';

/*
 * codec-*: every codec at its boundaries (W1–W10). Per case, decoding the byte range must yield the committed IEEE bits
 * exactly, and — when the case has an input — encoding it must yield the byte range exactly. A case without an input is
 * decode-only: wire codes no encoder produces (an over-long varint, the code −2^(b−1)).
 */

interface CodecCase {
  readonly input?: string[];
  readonly decoded?: string[];
  readonly count?: number;
  readonly text?: string;
  readonly bytes?: string;
  readonly offset: number;
  readonly length: number;
}

interface CodecVector {
  readonly codec: CatalogCodec;
  readonly frameTick: number;
  /** For a velocity: the unit exponent it decodes with (also in the codec). */
  readonly unitExp?: number;
  /** For a realm-framed codec: the frame it is quantized over (typhon.3). */
  readonly frame?: FrameJson;
  readonly cases: CodecCase[];
}

/**
 * The same range through the exported `math.ts` decoder, when the kind has one: `readNumber` writes its arithmetic out
 * rather than calling it, so both copies are held to the vector's bits.
 */
function decodeWithMath(
  bytes: Uint8Array,
  f: FieldPlan,
  frameTick: number,
  out: Float64Array,
  frame: RealmFrame | null,
): boolean {
  const r = new WireReader(bytes);
  switch (f.kind) {
    case CodecKind.F16:
      out[0] = decodeF16(r.u16());
      return true;
    case CodecKind.Quant:
      out[0] = decodeQuant(r.unsigned(f.bits), f.min[0]!, f.step[0]!);
      return true;
    case CodecKind.Pos2:
    case CodecKind.Pos3:
      for (let i = 0; i < f.components; i++) {
        out[i] = decodeQuant(r.unsigned(frame!.positionBits), frame!.min[i]!, frame!.step[i]!);
      }

      return true;
    case CodecKind.Vec2:
    case CodecKind.Vec3:
      for (let i = 0; i < f.components; i++) {
        out[i] = decodeVec(r.signed(f.bits), f.scale, f.limit);
      }

      return true;
    case CodecKind.Vel2:
    case CodecKind.Vel3:
      for (let i = 0; i < f.components; i++) {
        out[i] = decodeVel(r.signed(f.bits), f.velocityUnit, f.limit);
      }

      return true;
    case CodecKind.Unorm:
      out[0] = decodeUnorm(r.unsigned(f.bits), f.top);
      return true;
    case CodecKind.Snorm:
      out[0] = decodeSnorm(r.signed(f.bits), f.limit);
      return true;
    case CodecKind.Angle:
      out[0] = decodeAngle(r.signed(f.bits), f.bits);
      return true;
    case CodecKind.Quat3:
      decodeQuat3(r.u32(), out, 0);
      return true;
    case CodecKind.TickLo:
      out[0] = decodeTickLo(r.u16(), frameTick);
      return true;
    default:
      return false;
  }
}

function planOf(vector: CodecVector): FieldPlan {
  return new FieldPlan('v', 0, { name: 'v', codec: vector.codec }, vector.codec, {});
}

describe('golden codec vectors', () => {
  const names = goldenNames('codec-');

  it('has a vector for every byte-aligned codec kind', () => {
    const covered = new Set(names.map((name) => codecKindOf((goldenJson(name) as CodecVector).codec.t)));
    const missing = CODEC_TOKENS.filter((_, kind) => {
      const k = kind as CodecKind;
      return k !== CodecKind.Unknown && !isPacked(k) && !covered.has(k);
    });
    expect(missing).toEqual([]);
  });

  for (const name of names) {
    it(name, () => {
      const vector = goldenJson(name) as CodecVector;
      const bin = goldenBin(name);
      const plan = planOf(vector);
      const section = new SectionPlan([plan]);
      const reader = new WireReader();
      const writer = new WireWriter();
      const frame = frameFromJson(vector.frame);
      let covered = 0;

      for (const c of vector.cases) {
        const at = `${name} @${c.offset}`;
        const range = bin.subarray(c.offset, c.offset + c.length);
        covered = Math.max(covered, c.offset + c.length);
        const sink = new RecordingSink();
        reader.reset(range);
        writer.reset();
        let encoded = true;

        switch (plan.kind) {
          case CodecKind.Str:
            readSection(reader, section, vector.frameTick, sink);
            expect(sink.log, at).toEqual([{ call: 'text', field: 'v', utf8: c.text }]);
            // The SDK's own decoder, so the string encoded is the one a decode yields (a leading BOM included).
            writeSection(writer, section, { v: decodeUtf8(fromHex(c.text!))! });
            break;
          case CodecKind.Blob:
          case CodecKind.Bytes:
            readSection(reader, section, vector.frameTick, sink);
            expect(sink.log, at).toEqual([{ call: 'bytes', field: 'v', bytes: c.bytes }]);
            writeSection(writer, section, { v: fromHex(c.bytes!) });
            break;
          case CodecKind.List:
            readSection(reader, section, vector.frameTick, sink, false, frame);
            expect(sink.log, at).toEqual([{ call: 'list', field: 'v', count: c.count, values: c.decoded }]);
            if (c.input === undefined) {
              encoded = false;
            } else {
              writeSection(writer, section, { v: Float64Array.from(c.input, fromBits) }, false, frame);
            }

            break;
          default: {
            const out = new Float64Array(4);
            readNumber(reader, plan, vector.frameTick, out, 0, frame);
            expect(bitsOf(out, plan.components), `${at}: decoded bits`).toEqual(c.decoded);
            const viaMath = new Float64Array(4);
            if (decodeWithMath(range, plan, vector.frameTick, viaMath, frame)) {
              expect(bitsOf(viaMath, plan.components), `${at}: math.ts decoder bits`).toEqual(c.decoded);
            }

            if (c.input === undefined) {
              encoded = false;
            } else {
              writeNumber(writer, plan, Float64Array.from(c.input, fromBits), 0, frame);
            }

            break;
          }
        }

        expect(reader.isAtEnd, `${at}: decode consumed the whole range`).toBe(true);
        if (encoded) {
          expect(hex(writer.written()), `${at}: encoded bytes`).toBe(hex(range));
        }
      }

      expect(covered).toBe(bin.length);
    });
  }
});
