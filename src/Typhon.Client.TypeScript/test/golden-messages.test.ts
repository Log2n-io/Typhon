import { describe, expect, it } from 'vitest';
import {
  catalogHashFromHex,
  catalogHashToHex,
  CatalogPlan,
  MessageType,
  parseCatalog,
  readBye,
  readCommands,
  readHello,
  readKick,
  readPing,
  readPong,
  readWelcome,
  ValueKind,
  WireReader,
  WireWriter,
  writeBye,
  writeCommands,
  writeHello,
  writeKick,
  writePing,
  writePong,
  writeWelcome,
  type CommandInput,
  type FieldValue,
} from '../src/index.js';
import {
  fromBits,
  fromHex,
  goldenBin,
  goldenJson,
  hex,
  RecordingSink,
  type LogEntry,
  frameFromJson,
  type FrameJson,
} from './golden-support.js';

/*
 * message-*: every message decodes to the committed object, and — both directions being implemented — re-encodes to the
 * committed bytes. COMMANDS is encoded from its inputs and decoded to the committed server-side log.
 */

interface MessageVector<T> {
  readonly type: number;
  readonly message: T;
}

const text = new TextDecoder();
const utf8 = new TextEncoder();

function open(name: string): { bin: Uint8Array; reader: WireReader; type: number } {
  const bin = goldenBin(name);
  const reader = new WireReader(bin);
  return { bin, reader, type: reader.u8() };
}

describe('golden messages', () => {
  for (const name of ['message-hello', 'message-hello-minimal']) {
    it(name, () => {
      const vector = goldenJson(name) as MessageVector<Record<string, unknown>>;
      const { bin, reader, type } = open(name);
      expect(type).toBe(vector.type);
      expect(type).toBe(MessageType.Hello);
      const m = readHello(reader);
      expect(reader.isAtEnd).toBe(true);
      expect({
        major: m.major,
        minor: m.minor,
        caps: m.caps,
        kind: m.kind,
        token: hex(utf8.encode(m.token)),
        resumeToken: hex(m.resumeToken),
        clientCatalogHash: catalogHashToHex(m.clientCatalogHash),
        helloPayload: hex(m.helloPayload),
      }).toEqual(vector.message);

      const w = new WireWriter();
      writeHello(w, {
        ...m,
        clientCatalogHash: catalogHashFromHex(vector.message.clientCatalogHash as string),
        token: text.decode(fromHex(vector.message.token as string)),
      });
      expect(hex(w.written())).toBe(hex(bin));
    });
  }

  for (const name of ['message-welcome', 'message-welcome-skip']) {
    it(name, () => {
      const vector = goldenJson(name) as MessageVector<Record<string, unknown>>;
      const { bin, reader, type } = open(name);
      expect(type).toBe(MessageType.Welcome);
      const m = readWelcome(reader);
      expect(reader.isAtEnd).toBe(true);
      expect({
        major: m.major,
        minor: m.minor,
        capsGranted: m.capsGranted,
        sessionId: m.sessionId,
        resumeToken: hex(m.resumeToken),
        tick: m.tick,
        tickPeriodUs: m.tickPeriodUs,
        catalogHash: catalogHashToHex(m.catalogHash),
        catalogBytes: m.catalogJson.length,
      }).toEqual(vector.message);

      const swg = goldenJson('catalog-swg') as { hash: string };
      expect(catalogHashToHex(m.catalogHash)).toBe(swg.hash);
      if (m.catalogJson.length > 0) {
        expect(hex(m.catalogJson)).toBe(hex(goldenBin('catalog-swg')));
        expect(parseCatalog(m.catalogJson).app.name).toBe('SwgTatooine');
      }

      const w = new WireWriter();
      writeWelcome(w, m);
      expect(hex(w.written())).toBe(hex(bin));
    });
  }

  it('message-ping and message-pong', () => {
    const ping = open('message-ping');
    expect(ping.type).toBe(MessageType.Ping);
    const p = readPing(ping.reader);
    expect(p).toEqual((goldenJson('message-ping') as MessageVector<unknown>).message);
    const w = new WireWriter();
    writePing(w, p);
    expect(hex(w.written())).toBe(hex(ping.bin));

    const pong = open('message-pong');
    expect(pong.type).toBe(MessageType.Pong);
    const q = readPong(pong.reader);
    expect(q).toEqual((goldenJson('message-pong') as MessageVector<unknown>).message);
    w.reset();
    writePong(w, q);
    expect(hex(w.written())).toBe(hex(pong.bin));
  });

  it('message-kick, truncating the reason at a code-point boundary', () => {
    const vector = goldenJson('message-kick') as MessageVector<{ code: number; reason: string }>;
    const { bin, reader, type } = open('message-kick');
    expect(type).toBe(MessageType.Kick);
    const m = readKick(reader);
    expect(reader.isAtEnd).toBe(true);
    expect({ code: m.code, reason: hex(utf8.encode(m.reason)) }).toEqual(vector.message);

    const w = new WireWriter();
    writeKick(w, { code: 1013, reason: `${'a'.repeat(122)}€ lagging` });
    expect(hex(w.written())).toBe(hex(bin));
  });

  it('message-bye', () => {
    const { bin, reader, type } = open('message-bye');
    expect(type).toBe(MessageType.Bye);
    const code = readBye(reader);
    expect({ code }).toEqual((goldenJson('message-bye') as MessageVector<unknown>).message);
    const w = new WireWriter();
    writeBye(w, code);
    expect(hex(w.written())).toBe(hex(bin));
    expect(() => {
      writeBye(w, 1001);
    }).toThrow(RangeError);
  });

  it('message-commands: encoded from its inputs, decoded to the server-side log', () => {
    const vector = goldenJson('message-commands') as {
      catalog: string;
      clientTick: number;
      inputs: { type: string; seq: number; values: Record<string, string[] | string> }[];
      frame?: FrameJson;
      log: LogEntry[];
    };
    const plan = CatalogPlan.compile(parseCatalog(goldenBin(vector.catalog)));

    const commands: CommandInput[] = vector.inputs.map((input) => {
      const type = plan.commandByName(input.type)!;
      const values: Record<string, FieldValue> = {};
      for (const field of type.body.fields) {
        const raw = input.values[field.name]!;
        values[field.name] =
          field.valueKind === ValueKind.Text
            ? text.decode(fromHex(raw as string))
            : Float64Array.from(raw as string[], fromBits);
      }

      return { type, seq: input.seq, values };
    });

    const w = new WireWriter();
    writeCommands(w, vector.clientTick, commands, frameFromJson(vector.frame));
    const bin = goldenBin('message-commands');
    expect(hex(w.written())).toBe(hex(bin));

    const sink = new RecordingSink();
    readCommands(bin, plan, sink, frameFromJson(vector.frame));
    expect(sink.log).toEqual(vector.log);
  });
});
