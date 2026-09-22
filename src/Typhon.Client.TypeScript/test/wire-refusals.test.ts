import { describe, expect, it } from 'vitest';
import {
  beginBlock,
  BlockType,
  CatalogPlan,
  CloseCode,
  endBlock,
  FieldPlan,
  MessageType,
  parseBye,
  parseCatalog,
  parseHello,
  parseKick,
  parsePing,
  parsePong,
  parseWelcome,
  readCommands,
  readSection,
  SectionPlan,
  TickReader,
  WireFormatError,
  WireReader,
  WireWriter,
  writeCommands,
  writeTickHeader,
  type CatalogCodec,
} from '../src/index.js';
import { fromHex, goldenBin, goldenJson, RecordingSink } from './golden-support.js';

/*
 * wire-refusals: byte sequences every decoder must reject, with the close code it must reject them with — the half a
 * lenient decoder gets wrong silently. Then the refusals the vector does not carry.
 */

interface RefusalCase {
  readonly name: string;
  readonly kind: 'codec' | 'tick' | 'commands' | 'message';
  readonly codec?: CatalogCodec;
  readonly type?: number;
  readonly hex: string;
  readonly closeCode: number;
}

function closeCodeOf(action: () => void): number {
  try {
    action();
  } catch (e) {
    if (e instanceof WireFormatError) {
      return e.closeCode;
    }

    throw e;
  }

  throw new Error('the input was accepted');
}

const kitchen = CatalogPlan.compile(parseCatalog(goldenBin('catalog-kitchen-sink')));

const parsers: Record<number, (message: Uint8Array) => unknown> = {
  [MessageType.Hello]: parseHello,
  [MessageType.Welcome]: parseWelcome,
  [MessageType.Ping]: parsePing,
  [MessageType.Pong]: parsePong,
  [MessageType.Kick]: parseKick,
  [MessageType.Bye]: parseBye,
};

describe('golden wire-refusals', () => {
  const vector = goldenJson('wire-refusals') as { catalog: string; cases: RefusalCase[] };

  it('refers to the kitchen-sink catalog', () => {
    expect(vector.catalog).toBe('catalog-kitchen-sink');
  });

  for (const c of vector.cases) {
    it(`${c.kind}: ${c.name}`, () => {
      const bytes = fromHex(c.hex);
      const sink = new RecordingSink();
      const code = closeCodeOf(() => {
        switch (c.kind) {
          case 'codec': {
            const section = new SectionPlan([new FieldPlan('v', 0, { name: 'v', codec: c.codec! }, c.codec!, {})]);
            readSection(new WireReader(bytes), section, 0, sink);
            break;
          }
          case 'tick':
            new TickReader(kitchen).read(bytes, sink);
            break;
          case 'commands':
            readCommands(bytes, kitchen, sink);
            break;
          case 'message':
            parsers[c.type!]!(bytes);
            break;
        }
      });

      expect(code).toBe(c.closeCode);
    });
  }
});

describe('refusals beyond the vector', () => {
  it('a truncated TICK is malformed wherever it is cut', () => {
    const full = goldenBin('tick-entities');
    const reader = new TickReader(kitchen);
    for (const cut of [1, 5, 7, full.length >> 1, full.length - 1]) {
      expect(
        closeCodeOf(() => {
          reader.read(full.subarray(0, cut), new RecordingSink());
        }),
      ).toBe(CloseCode.MalformedPayload);
    }
  });

  it('a STATS block whose values do not fill its length is malformed, as any block is', () => {
    const w = new WireWriter();
    writeTickHeader(w, 1, 0);
    const mark = beginBlock(w, BlockType.Stats);
    // The kitchen sink's seven values take 12 bytes: f16, 3 × f16 and unorm8 (server), then varu and u16 (session).
    for (const b of [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xee]) {
      w.u8(b);
    }

    endBlock(w, mark);
    expect(
      closeCodeOf(() => {
        new TickReader(kitchen).read(w.toBytes(), new RecordingSink());
      }),
    ).toBe(CloseCode.MalformedPayload);
  });

  it('a string that is not UTF-8 is refused inside a TICK record too', () => {
    const w = new WireWriter();
    writeTickHeader(w, 1, 0);
    const mark = beginBlock(w, BlockType.Events);
    w.varu(1);
    w.varu(kitchen.eventByName('Chat')!.idx);
    w.varu(2);
    w.u8(0xc3);
    w.u8(0x28);
    endBlock(w, mark);
    expect(
      closeCodeOf(() => {
        new TickReader(kitchen).read(w.toBytes(), new RecordingSink());
      }),
    ).toBe(CloseCode.MalformedPayload);
  });

  it('a refused COMMANDS message delivers nothing, even commands before the bad one', () => {
    const steer = kitchen.commandByName('Steer')!;
    const w = new WireWriter();
    writeCommands(w, 7, [
      { type: steer, seq: 1, values: { heading: 0, boost: true, speed: 0.5, stance: 1, note: 'ok' } },
      { type: steer, seq: 2, values: { heading: 0, boost: false, speed: 0, stance: 0, note: '' } },
    ]);
    const bytes = w.toBytes();
    // The last command ends with its pack, heading (1 B), empty note (1 B) and speed (1 B): stance 3 is one past its names.
    bytes[bytes.length - 4] = 3 << 1;
    const sink = new RecordingSink();
    expect(
      closeCodeOf(() => {
        readCommands(bytes, kitchen, sink);
      }),
    ).toBe(CloseCode.MalformedPayload);
    expect(sink.log).toEqual([]);
  });

  it('decodes every f32 NaN pattern as the one canonical NaN, and encodes NaN as 0x7FC00000', () => {
    for (const pattern of [0x7fc00000, 0xffc00000, 0x7f800001, 0xfff00000 | 0x1]) {
      const bytes = new Uint8Array(4);
      new DataView(bytes.buffer).setUint32(0, pattern >>> 0, true);
      expect(Number.isNaN(new WireReader(bytes).f32())).toBe(true);
    }

    const w = new WireWriter();
    w.f32(-NaN);
    w.f32(Number.NaN);
    expect(Array.from(w.written())).toEqual([0, 0, 0xc0, 0x7f, 0, 0, 0xc0, 0x7f]);
  });

  it('a client refuses to encode an enum value its names do not cover, and an over-cap string', () => {
    const steer = kitchen.commandByName('Steer')!;
    const values = { heading: 0, boost: false, speed: 0, stance: 3, note: '' };
    expect(() => {
      writeCommands(new WireWriter(), 0, [{ type: steer, seq: 1, values }]);
    }).toThrow(RangeError);
    expect(() => {
      writeCommands(new WireWriter(), 0, [
        { type: steer, seq: 1, values: { ...values, stance: 2, note: 'x'.repeat(17) } },
      ]);
    }).toThrow(RangeError);
    const w = new WireWriter();
    writeCommands(w, 0, [{ type: steer, seq: 1, values: { ...values, stance: 2 } }]);
    expect(
      closeCodeOf(() => {
        readCommands(w.written().subarray(1), kitchen, new RecordingSink());
      }),
    ).toBe(CloseCode.ProtocolError);
  });
});

describe('wire primitives', () => {
  it('round-trips the fixed-width integers at their edges, u32 staying non-negative', () => {
    const w = new WireWriter(16);
    w.u8(255);
    w.i8(-128);
    w.u16(65535);
    w.i16(-32768);
    w.u24(0xffffff);
    w.i24(-0x800000);
    w.u32(0xffffffff);
    w.i32(-0x80000000);
    const r = new WireReader(w.toBytes());
    expect([r.u8(), r.i8(), r.u16(), r.i16(), r.u24(), r.i24(), r.u32(), r.i32()]).toEqual([
      255, -128, 65535, -32768, 0xffffff, -0x800000, 0xffffffff, -0x80000000,
    ]);
    expect(r.isAtEnd).toBe(true);
  });

  it('patches a length prefix in minimal form, moving content across a growth', () => {
    const w = new WireWriter(16);
    const mark = w.beginLengthPrefixed();
    for (let i = 0; i < 200; i++) {
      w.u8(i);
    }

    w.endLengthPrefixed(mark);
    const bytes = w.toBytes();
    expect(bytes.length).toBe(202);
    const r = new WireReader(bytes);
    expect(r.varu()).toBe(200);
    expect(r.u8()).toBe(0);
    expect(bytes[201]).toBe(199);
  });
});

describe('strings', () => {
  it('keeps a leading U+FEFF, as C# does', () => {
    const r = new WireReader(Uint8Array.of(4, 0xef, 0xbb, 0xbf, 0x41));
    expect(r.str(8)).toBe('﻿A');
  });
});
