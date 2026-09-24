import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';
import {
  catalogHashFromHex,
  catalogHashOf,
  catalogHashToHex,
  CatalogPlan,
  FrameApplier,
  generateDecoders,
  BlockType,
  beginBlock,
  endBlock,
  MessageType,
  parseCodegenArgs,
  parseCatalog,
  WireWriter,
  writeTickHeader,
  type Catalog,
  type CatalogCodec,
  parseMessage,
  readWelcome,
  type GeneratedDecoders,
} from '../src/index.js';
import { goldenBin, goldenJson, goldenNames } from './golden-support.js';

/*
 * typhon-codegen (05-sdks § 2): generated `ENTITIES` decoders must leave the store byte for byte where the interpreter
 * leaves it, and refuse what it refuses with the same error. The proof runs every golden vector that carries ENTITIES
 * blocks through both paths — the engine's own streams included — and a few hundred corruptions of them, comparing the
 * whole store: every column, every motion record, the live, entered, updated and left lists, and the anomaly count.
 */

const here = dirname(fileURLToPath(import.meta.url));
const OUT = join(here, '.generated');

/** Generates a catalog's module into `test/.generated`, importing the client from source, and loads it. */
async function generated(
  name: string,
  catalogBytes: Uint8Array,
): Promise<{ decoders: GeneratedDecoders; file: string; source: string }> {
  mkdirSync(OUT, { recursive: true });
  const plan = CatalogPlan.compile(parseCatalog(catalogBytes));
  const source = generateDecoders(plan, catalogBytes, { importFrom: '../../src/index.js' });
  const file = join(OUT, `${name}.gen.ts`);
  writeFileSync(file, source);
  const module = (await import(pathToFileURL(file).href)) as { decoders: GeneratedDecoders };
  return { decoders: module.decoders, file, source };
}

/** Everything a decode can change in the world, rendered comparable: the stores' arrays whole, and the counters. */
function dump(applier: FrameApplier): unknown {
  const hexOf = (a: ArrayLike<number>): string => Buffer.from(Uint8Array.from(a)).toString('hex');
  const bytesOf = (a: ArrayBufferView): string => Buffer.from(a.buffer, a.byteOffset, a.byteLength).toString('hex');
  return {
    anomalies: applier.world.anomalies,
    archetypes: applier.plan.archetypes.map((a) => {
      const s = applier.world.archetypeStore(a.idx);
      return {
        capacity: s.capacity,
        live: hexOf(s.live.subarray(0, s.liveCount)),
        netIds: bytesOf(s.netIds),
        entered: bytesOf(s.entered.subarray(0, s.enteredCount)),
        updated: bytesOf(s.updated.subarray(0, s.updatedCount)),
        updateMask: bytesOf(s.updateMask),
        left: bytesOf(s.left.subarray(0, s.leftCount)),
        motion: bytesOf(s.motionU8),
        columns: a.fields.map((f) => {
          const index = s.fieldIndex(f.name);
          if (index < 0) {
            return 'not stored';
          }

          const column = s.columns[index];
          if (column != null) {
            return bytesOf(column);
          }

          try {
            return JSON.stringify(s.textAt(index));
          } catch {
            return s
              .bytesAt(index)
              .map((b) => bytesOf(b))
              .join(',');
          }
        }),
      };
    }),
  };
}

/** Applies every message to both appliers; each message either applies on both or fails on both with the same error. */
function applyBoth(
  interpreter: FrameApplier,
  generatedApplier: FrameApplier,
  messages: readonly Uint8Array[],
  what: string,
): void {
  messages.forEach((message, i) => {
    const outcome = (applier: FrameApplier): string => {
      try {
        applier.apply(message);
        return 'applied';
      } catch (e) {
        return `${(e as Error).constructor.name}: ${(e as Error).message}`;
      }
    };

    const expected = outcome(interpreter);
    expect(outcome(generatedApplier), `${what}: message ${i}`).toBe(expected);
    expect(dump(generatedApplier), `${what}: the store after message ${i}`).toEqual(dump(interpreter));
  });
}

function unframe(stream: Uint8Array): Uint8Array[] {
  const view = new DataView(stream.buffer, stream.byteOffset, stream.byteLength);
  const messages: Uint8Array[] = [];
  for (let at = 0; at < stream.length;) {
    const length = view.getUint32(at, true);
    messages.push(stream.subarray(at + 4, at + 4 + length));
    at += 4 + length;
  }

  return messages;
}

function pair(catalogBytes: Uint8Array, decoders: GeneratedDecoders): [FrameApplier, FrameApplier] {
  const plan = CatalogPlan.compile(parseCatalog(catalogBytes));
  const hash = catalogHashFromHex(catalogHashOf(catalogBytes));
  return [new FrameApplier(plan), new FrameApplier(plan, { decoders, catalogHash: hash })];
}

/** A deterministic PRNG, so a corruption that finds a difference can be replayed. */
function random(seed: number): () => number {
  let s = seed >>> 0;
  return () => {
    s = (s * 1664525 + 1013904223) >>> 0;
    return s / 2 ** 32;
  };
}

/** Corrupted copies of `messages`: one to three bytes changed, or the message truncated. */
function corruptions(messages: readonly Uint8Array[], count: number, seed: number): Uint8Array[] {
  const next = random(seed);
  const out: Uint8Array[] = [];
  for (let k = 0; k < count; k++) {
    const source = messages[Math.floor(next() * messages.length)]!;
    const copy = source.slice();
    if (next() < 0.2 && copy.length > 7) {
      out.push(copy.subarray(0, 6 + Math.floor(next() * (copy.length - 6))));
      continue;
    }

    for (let n = 1 + Math.floor(next() * 3); n > 0; n--) {
      // Past the TICK header (type, tick, flags), where the blocks are.
      copy[6 + Math.floor(next() * Math.max(1, copy.length - 6))] = Math.floor(next() * 256);
    }

    out.push(copy);
  }

  return out;
}

describe('typhon-codegen', () => {
  it("computes the server's catalog hash from the canonical bytes", () => {
    for (const name of goldenNames('catalog-')) {
      const vector = goldenJson(name) as { hash?: string };
      if (vector.hash !== undefined) {
        expect(catalogHashOf(goldenBin(name)), name).toBe(vector.hash);
      }
    }

    const welcome = parseMessage(unframe(goldenBin('stream-engine'))[0]!, MessageType.Welcome, readWelcome);
    expect(catalogHashOf(welcome.catalogJson), "the engine's own catalog, as its WELCOME carries it").toBe(
      catalogHashToHex(welcome.catalogHash),
    );
  });

  it('leaves the store where the interpreter does, on every snapshot stream and tick vector', async () => {
    const byCatalog = new Map<string, Uint8Array[]>();
    const add = (catalog: string, messages: Uint8Array[]): void => {
      byCatalog.set(catalog, [...(byCatalog.get(catalog) ?? []), ...messages]);
    };

    for (const name of goldenNames('stream-').filter((n) => !n.startsWith('stream-engine'))) {
      add((goldenJson(name) as { catalog: string }).catalog, unframe(goldenBin(name)));
    }

    for (const name of goldenNames('tick-')) {
      add((goldenJson(name) as { catalog: string }).catalog, [goldenBin(name)]);
    }

    expect([...byCatalog.keys()].sort()).toEqual(['catalog-kitchen-sink', 'catalog-wide']);
    for (const [catalog, messages] of byCatalog) {
      const { decoders } = await generated(catalog, goldenBin(catalog));
      const [interpreter, gen] = pair(goldenBin(catalog), decoders);
      applyBoth(interpreter, gen, messages, catalog);
    }
  });

  it("leaves the store where the interpreter does on the engine's own streams, 2D and deep", async () => {
    for (const name of ['stream-engine', 'stream-engine-3d']) {
      const messages = unframe(goldenBin(name));
      const welcome = parseMessage(messages[0]!, MessageType.Welcome, readWelcome);
      const { decoders } = await generated(name, welcome.catalogJson);
      const plan = CatalogPlan.compile(parseCatalog(welcome.catalogJson));
      const interpreter = new FrameApplier(plan);
      const gen = new FrameApplier(plan, { decoders, catalogHash: welcome.catalogHash });
      applyBoth(interpreter, gen, messages.slice(1), name);
      expect(interpreter.world.anomalies).toBe(0);
    }
  });

  it('refuses a corrupted frame exactly as the interpreter does, or applies it identically', async () => {
    const catalog = goldenBin('catalog-kitchen-sink');
    const { decoders } = await generated('catalog-kitchen-sink', catalog);
    const messages = [
      ...unframe(goldenBin('stream-kitchen-sink')),
      ...unframe(goldenBin('stream-motion')),
      goldenBin('tick-entities'),
      goldenBin('tick-runs'),
    ];
    let refused = 0;
    for (const [i, bad] of corruptions(messages, 400, 20260924).entries()) {
      // A fresh pair per corruption: a refused frame leaves a partial store, whose state is compared, then discarded.
      const [interpreter, gen] = pair(catalog, decoders);
      applyBoth(interpreter, gen, [bad], `corruption ${i}`);
      try {
        new FrameApplier(interpreter.plan).apply(bad);
      } catch {
        refused++;
      }
    }

    expect(refused, 'most corruptions are refused, so the refusal paths are the ones compared').toBeGreaterThan(100);
  });

  it("decodes every codec's edge codes as the interpreter does, the wire-only ones included", async () => {
    // One archetype per codec vector, whose single field is that codec; one frame per archetype entering an entity per
    // case, the case's bytes as the field's. The vectors hold each codec's edges: the code −2^(b−1) no encoder writes,
    // NaN patterns, tickLo's wrap, over-long varints.
    const vectors = goldenNames('codec-')
      .map((name) => ({
        name,
        vector: goldenJson(name) as {
          codec: CatalogCodec;
          frameTick: number;
          cases: { offset: number; length: number }[];
        },
      }))
      .filter(({ vector }) => !['list', 'vel2', 'vel3', 'pos2', 'pos3'].includes(vector.codec.t));
    const kitchen = parseCatalog(goldenBin('catalog-kitchen-sink'));
    const catalog: Catalog = {
      ...kitchen,
      archetypes: vectors.map(({ vector }, idx) => ({
        idx,
        name: `C${idx}`,
        groups: ['g'],
        fields: [{ name: 'v', codec: vector.codec, group: 'g' }],
      })),
    };
    const bytes = new TextEncoder().encode(JSON.stringify(catalog));
    const plan = CatalogPlan.compile(catalog);
    mkdirSync(OUT, { recursive: true });
    const file = join(OUT, 'codec-edges.gen.ts');
    writeFileSync(file, generateDecoders(plan, bytes, { importFrom: '../../src/index.js' }));
    const { decoders } = (await import(pathToFileURL(file).href)) as { decoders: GeneratedDecoders };
    const interpreter = new FrameApplier(plan);
    const gen = new FrameApplier(plan, { decoders, catalogHash: catalogHashFromHex(catalogHashOf(bytes)) });

    const messages = vectors.map(({ name, vector }, idx) => {
      const bin = goldenBin(name);
      const w = new WireWriter(4096);
      writeTickHeader(w, vector.frameTick, 0);
      const mark = beginBlock(w, BlockType.Entities);
      w.varu(idx);
      w.varu(1);
      w.varu(vector.cases.length);
      vector.cases.forEach((c, k) => {
        w.varu(k === 0 ? 1 : 0);
        w.raw(bin.subarray(c.offset, c.offset + c.length));
      });
      w.varu(0);
      w.varu(0);
      w.varu(0);
      endBlock(w, mark);
      return w.toBytes();
    });

    expect(vectors.length).toBeGreaterThan(30);
    applyBoth(interpreter, gen, messages, 'codec edges');
  });

  it('parses its command line as 05-sdks § 2 writes it, and refuses a malformed one', () => {
    expect(parseCodegenArgs(['catalog.json', '-o', 'src/gen'])).toEqual({
      catalog: 'catalog.json',
      outDir: 'src/gen',
      importFrom: '@typhondb/client',
    });
    expect(parseCodegenArgs(['--import', '../client/index.js', 'c.json'])).toEqual({
      catalog: 'c.json',
      outDir: 'src/gen',
      importFrom: '../client/index.js',
    });
    expect(() => parseCodegenArgs([])).toThrow(/no catalog/);
    expect(() => parseCodegenArgs(['a.json', 'b.json'])).toThrow(/one catalog only/);
    expect(() => parseCodegenArgs(['a.json', '-o'])).toThrow(/needs a value/);
    expect(() => parseCodegenArgs(['a.json', '--watch'])).toThrow(/unknown option/);
  });

  it('refuses decoders generated from another catalog, and decoders without the session hash', async () => {
    const swg = goldenBin('catalog-swg');
    const { decoders } = await generated('catalog-swg', swg);
    const plan = CatalogPlan.compile(parseCatalog(swg));
    expect(() => new FrameApplier(plan, { decoders, catalogHash: catalogHashFromHex('0000000000000001') })).toThrow(
      /regenerate/,
    );
    expect(() => new FrameApplier(plan, { decoders })).toThrow(/catalog hash/);
    expect(
      () => new FrameApplier(plan, { decoders, catalogHash: catalogHashFromHex(catalogHashOf(swg)) }),
    ).not.toThrow();
  });

  it('generates modules that type-check under the SDK’s strict settings', async () => {
    const files: string[] = [];
    for (const catalog of ['catalog-kitchen-sink', 'catalog-wide', 'catalog-swg']) {
      files.push((await generated(catalog, goldenBin(catalog))).file);
    }

    const config = ts.readConfigFile(join(here, '..', 'tsconfig.json'), (p) => ts.sys.readFile(p));
    const options = ts.parseJsonConfigFileContent(config.config, ts.sys, join(here, '..')).options;
    const program = ts.createProgram(files, { ...options, noEmit: true });
    const problems = ts
      .getPreEmitDiagnostics(program)
      .filter(
        (d) =>
          d.file !== undefined && files.some((f) => ts.sys.resolvePath(f) === ts.sys.resolvePath(d.file!.fileName)),
      )
      .map((d) => `${d.file!.fileName}: ${ts.flattenDiagnosticMessageText(d.messageText, '\n')}`);
    expect(problems).toEqual([]);
  }, 60_000);
});
