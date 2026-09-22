import { describe, expect, it } from 'vitest';
import {
  FieldPlan,
  readSection,
  SectionPlan,
  WireReader,
  WireWriter,
  writeSection,
  type CatalogCodec,
} from '../src/index.js';
import { fromBits, goldenBin, goldenJson, hex, RecordingSink } from './golden-support.js';

/*
 * section-packs: one implicit pack per section, least significant bit first, packed fields ahead of byte-aligned ones
 * (W12).
 */

interface PacksVector {
  readonly fields: { readonly name: string; readonly codec: CatalogCodec; readonly bitOffset: number }[];
  readonly packBytes: number;
  readonly cases: { readonly values: Record<string, string>; readonly offset: number; readonly length: number }[];
}

describe('golden section-packs', () => {
  const vector = goldenJson('section-packs') as PacksVector;
  const bin = goldenBin('section-packs');
  const section = new SectionPlan(
    vector.fields.map((f, i) => new FieldPlan(f.name, i, { name: f.name, codec: f.codec }, f.codec, {})),
  );

  it('lays the pack out as the generator did', () => {
    expect(section.packBytes).toBe(vector.packBytes);
    expect(section.fields.map((f) => (f.packed ? f.bitOffset : -1))).toEqual(vector.fields.map((f) => f.bitOffset));
  });

  it('decodes and encodes every case', () => {
    const reader = new WireReader();
    const writer = new WireWriter();
    for (const c of vector.cases) {
      const range = bin.subarray(c.offset, c.offset + c.length);
      const sink = new RecordingSink();
      readSection(reader.reset(range), section, 0, sink);
      expect(reader.isAtEnd).toBe(true);
      const decoded: Record<string, string> = {};
      for (const entry of sink.log) {
        decoded[entry.field as string] = (entry.values as string[])[0]!;
      }

      expect(decoded).toEqual(c.values);

      const values: Record<string, number> = {};
      for (const [name, value] of Object.entries(c.values)) {
        values[name] = fromBits(value);
      }

      writer.reset();
      writeSection(writer, section, values);
      expect(hex(writer.written())).toBe(hex(range));
    }
  });

  it('ignores non-zero padding bits on decode', () => {
    // The pack's used bits, from the vector's layout: its padding is the rest of the last byte.
    const packedBits = Math.max(
      ...vector.fields
        .filter((f) => f.bitOffset >= 0)
        .map((f) => f.bitOffset + (f.codec.t === 'bool' ? 1 : f.codec.n!)),
    );
    const used = packedBits - 8 * (vector.packBytes - 1);
    expect(used).toBeGreaterThan(0);
    expect(used).toBeLessThanOrEqual(8);
    const padding = 0xff & ~((1 << used) - 1);

    for (const c of vector.cases) {
      const bytes = bin.slice(c.offset, c.offset + c.length);
      bytes[vector.packBytes - 1] = bytes[vector.packBytes - 1]! | padding;
      const sink = new RecordingSink();
      readSection(new WireReader(bytes), section, 0, sink);
      const decoded: Record<string, string> = {};
      for (const entry of sink.log) {
        decoded[entry.field as string] = (entry.values as string[])[0]!;
      }

      expect(decoded).toEqual(c.values);
    }
  });
});
