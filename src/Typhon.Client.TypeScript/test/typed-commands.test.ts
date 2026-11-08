import { mkdirSync, writeFileSync } from 'node:fs';
import { PerformanceObserver } from 'node:perf_hooks';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { setFlagsFromString } from 'node:v8';
import { runInNewContext } from 'node:vm';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';
import {
  catalogHashFromHex,
  CatalogPlan,
  catalogHashOf,
  CommandQueue,
  FrameApplier,
  generateDecoders,
  parseCatalog,
  readCommands,
  WireWriter,
  writeEntitiesBlock,
  writeTickHeader,
  type ArchetypeStore,
  type FieldValues,
  type GeneratedCommand,
  type WorldStore,
} from '../src/index.js';
import { goldenBin } from './golden-support.js';

/*
 * #1614: `typhon-codegen` emits, per command, a record type and a specialized encoder, and per archetype typed column
 * getters. A typed command encodes to the bytes the name-keyed path writes, a misspelled or missing field does not
 * compile, a module generated from another catalog is refused, and a steady frame of typed commands allocates nothing.
 */

const here = dirname(fileURLToPath(import.meta.url));
const bytes = goldenBin('catalog-commands');
const plan = CatalogPlan.compile(parseCatalog(bytes));
const sessionHash = catalogHashFromHex(catalogHashOf(bytes));

interface MoveCmd {
  Forward: number;
  Strafe: number;
  Buttons: number;
  Fx: number;
  Fz: number;
}

interface Probe {
  Boost: boolean;
  Gait: number;
  Heading: number;
  Speed: number;
  Budget: number;
  Delta: number;
  Exact: number;
  Big: bigint;
  id: bigint;
}

interface Generated {
  readonly file: string;
  readonly commands: {
    readonly catalogHash: string;
    readonly MoveCmd: GeneratedCommand<MoveCmd>;
    readonly Probe: GeneratedCommand<Probe>;
    readonly Pulse: GeneratedCommand<{ Budget: number }>;
    readonly Say: GeneratedCommand<{ channel: number; text: string }>;
  };
  readonly enums: { readonly Gait: { readonly walk: number; readonly run: number; readonly sprint: number } };
  readonly WalkerColumns: {
    of(world: WorldStore): {
      readonly $store: ArchetypeStore;
      readonly hp: Uint16Array;
      readonly speed: Float32Array;
      readonly account: BigUint64Array;
      readonly name: string[];
    };
  };
}

let module: Generated | null = null;

async function generated(): Promise<Generated> {
  if (module === null) {
    const file = join(here, '.generated', 'catalog-commands.gen.ts');
    mkdirSync(dirname(file), { recursive: true });
    writeFileSync(file, generateDecoders(plan, bytes, { importFrom: '../../src/index.js' }));
    module = { file, ...((await import(pathToFileURL(file).href)) as Omit<Generated, 'file'>) };
  }

  return module;
}

function queue(commands: Generated['commands'] | null): CommandQueue {
  return new CommandQueue(
    commands === null ? { plan, now: () => 0 } : { plan, now: () => 0, commands, catalogHash: sessionHash },
  );
}

function flushed(q: CommandQueue): Uint8Array[] {
  const messages: Uint8Array[] = [];
  q.flush(7, (m) => messages.push(m.slice()));
  return messages;
}

function decodeNumbers(message: Uint8Array): Record<string, number>[] {
  const commands: Record<string, number>[] = [];
  readCommands(message, plan, {
    command: () => commands.push({}),
    number: (field, values) => {
      commands[commands.length - 1]![field.name] = values[0]!;
    },
    integer64: () => undefined,
    text: () => undefined,
    bytes: () => undefined,
    list: () => undefined,
  });
  return commands;
}

describe('typed commands (#1614)', () => {
  it('encodes MoveCmd as the name-keyed path does, its f32s at their own bits', async () => {
    const { commands } = await generated();
    const typed = queue(commands);
    const named = queue(null);
    const move = plan.commandByName('MoveCmd')!;
    const edges = [
      Number.NaN,
      -0,
      1.401298464324817e-45,
      3.4028234663852886e38,
      -1.5,
      Math.fround(0.1),
      0.1,
      Number.NEGATIVE_INFINITY,
    ];
    const record: MoveCmd = { Forward: 0, Strafe: 0, Buttons: 0, Fx: 0, Fz: 0 };
    for (const [i, fx] of edges.entries()) {
      // One record, rewritten in place: the allocation-free pattern, and proof the body is encoded at enqueue.
      record.Forward = (i % 3) - 1;
      record.Strafe = 1 - (i % 3);
      record.Buttons = 0xa0 + i;
      record.Fx = fx;
      record.Fz = -fx;
      expect(typed.enqueueTyped(commands.MoveCmd, record)).toBe(named.enqueue(move, { ...record }));
    }

    const typedBytes = flushed(typed);
    expect(typedBytes).toEqual(flushed(named));
    const decoded = decodeNumbers(typedBytes[0]!);
    expect(decoded.map((c) => c['Fx'])).toEqual(edges.map((x) => Math.fround(x)));
  });

  it('encodes every scalar kind as the name-keyed path does, over 500 seeded values', async () => {
    const { commands, enums } = await generated();
    const probe = plan.commandByName('Probe')!;
    let seed = 1614;
    const next = (): number => {
      seed = (seed * 1103515245 + 12345) & 0x7fffffff;
      return seed / 0x80000000;
    };

    for (let i = 0; i < 500; i++) {
      const record: Probe = {
        Boost: next() < 0.5,
        Gait: [enums.Gait.walk, enums.Gait.run, enums.Gait.sprint][i % 3]!,
        Heading: next() * 720 - 360,
        Speed: next(),
        Budget: Math.floor(next() * 65536),
        Delta: Math.floor(next() * 2 ** 32) - 2 ** 31,
        Exact: next() * 1e12 - 5e11,
        Big: BigInt(Math.floor(next() * 2 ** 53)) * (i % 2 === 0 ? -97n : 89n),
        id: BigInt(Math.floor(next() * 2 ** 53)) * 1531n,
      };
      const typed = queue(commands);
      const named = queue(null);
      typed.enqueueTyped(commands.Probe, record);
      named.enqueue(probe, record as unknown as FieldValues);
      expect(flushed(typed)).toEqual(flushed(named));
    }
  });

  it('keeps one order and one seq space across the typed and the name-keyed paths, and coalesces a latest command', async () => {
    const { commands } = await generated();
    const q = queue(commands);
    expect(q.enqueueTyped(commands.Pulse, { Budget: 1 })).toBe(1);
    expect(q.enqueue(plan.commandByName('Say')!, { channel: 2, text: 'hi' })).toBe(2);
    expect(q.enqueueTyped(commands.Pulse, { Budget: 2 })).toBe(3);
    expect(q.enqueueTyped(commands.Say, { channel: 3, text: 'typed' })).toBe(4);
    expect(q.coalescedCount).toBe(1);

    const seqs: number[] = [];
    const names: string[] = [];
    readCommands(flushed(q)[0]!, plan, {
      command: (type, seq) => {
        seqs.push(seq);
        names.push(type.name);
      },
      number: () => undefined,
      integer64: () => undefined,
      text: () => undefined,
      bytes: () => undefined,
      list: () => undefined,
    });
    expect(names).toEqual(['Say', 'Pulse', 'Say']);
    expect(seqs).toEqual([2, 3, 4]);
  });

  it('refuses a module generated from another catalog, and a typed enqueue with no module', async () => {
    const { commands } = await generated();
    expect(() => new CommandQueue({ plan, commands, catalogHash: catalogHashFromHex('0123456789abcdef') })).toThrow(
      /come from catalog .* but the session's is 0123456789abcdef/,
    );
    expect(() => new CommandQueue({ plan, commands })).toThrow(/need the session catalog hash/);
    expect(() =>
      queue(null).enqueueTyped(commands.MoveCmd, { Forward: 0, Strafe: 0, Buttons: 0, Fx: 0, Fz: 0 }),
    ).toThrow(/needs the generated module's commands/);
  });

  it('refuses a value out of its codec at enqueue, leaving the queue as it was', async () => {
    const { commands } = await generated();
    const q = queue(commands);
    q.enqueueTyped(commands.MoveCmd, { Forward: 1, Strafe: 0, Buttons: 0, Fx: 0, Fz: 0 });
    expect(() => q.enqueueTyped(commands.MoveCmd, { Forward: 200, Strafe: 0, Buttons: 0, Fx: 0, Fz: 0 })).toThrow(
      RangeError,
    );
    expect(() => q.enqueueTyped(commands.Probe, { ...probeZero(), Gait: 3 })).toThrow(/enum value 3/);
    expect([q.pendingCount, q.nextSeq]).toEqual([1, 2]);
    expect(flushed(q)).toEqual(flushed(namedOne()));
  });

  it('turns a misspelled command, a misspelled field and a missing one into compile errors', async () => {
    const { file } = await generated();
    const snippet = join(here, '.generated', 'typed-commands.snippet.ts');
    writeFileSync(
      snippet,
      [
        "import { CommandQueue, type CatalogPlan } from '../../src/index.js';",
        "import { commands, WalkerColumns } from './catalog-commands.gen.js';",
        'declare const q: CommandQueue;',
        'declare const plan: CatalogPlan;',
        'void plan;',
        'q.enqueueTyped(commands.MoveCmd, { Forward: 1, Strafe: 0, Buttons: 0, Fx: 0, Fz: 1 });',
        'q.enqueueTyped(commands.MoveCmd, { Forward: 1, Strafe: 0, Buttons: 0, Fx: 0, Fy: 1 });',
        'q.enqueueTyped(commands.MoveCmd, { Forward: 1, Strafe: 0, Buttons: 0, Fx: 0 });',
        'q.enqueueTyped(commands.MoveCommand, { Forward: 1 });',
        'q.enqueueTyped(commands.Probe, { Boost: 1, Gait: 0, Heading: 0, Speed: 0, Budget: 0, Delta: 0, Exact: 0, Big: 0n, id: 0n });',
        'declare const w: WalkerColumns;',
        'const hp: Uint16Array = w.hp;',
        'const typo = w.hitPoints;',
        'void hp; void typo;',
      ].join('\n'),
    );

    const config = ts.readConfigFile(join(here, '..', 'tsconfig.json'), (p) => ts.sys.readFile(p));
    const options = ts.parseJsonConfigFileContent(config.config, ts.sys, join(here, '..')).options;
    const program = ts.createProgram([snippet, file], { ...options, noEmit: true });
    const diagnostics = ts.getPreEmitDiagnostics(program).filter((d) => d.file !== undefined);
    const inModule = diagnostics
      .filter((d) => ts.sys.resolvePath(d.file!.fileName) === ts.sys.resolvePath(file))
      .map((d) => ts.flattenDiagnosticMessageText(d.messageText, '\n'));
    // The generated module itself compiles clean under the SDK's settings: only the snippet's deliberate mistakes may fail.
    expect(inModule).toEqual([]);
    const lines = diagnostics
      .filter((d) => ts.sys.resolvePath(d.file!.fileName) === ts.sys.resolvePath(snippet))
      .map((d) => d.file!.getLineAndCharacterOfPosition(d.start!).line + 1);
    // Line 6 compiles; 7 names Fy, 8 lacks Fz, 9 names no command, 10 passes a number for a boolean, 13 names no column.
    expect([...new Set(lines)].sort((a, b) => a - b)).toEqual([7, 8, 9, 10, 13]);
  }, 60_000);

  it('generates a module that compiles when catalog names collide with its own or with each other', () => {
    // Catalog names are any strings: a field named like the class's member or `constructor`, a command named like an import, two names that sanitize
    // to one identifier. Compiled without the canonical check — the module is what is tested, not the catalog.
    const base = parseCatalog(bytes);
    const walker = base.archetypes[0]!;
    const command = (idx: number, name: string) => ({
      idx,
      name,
      delivery: 'queued',
      fields: [{ name: 'v', codec: { t: 'u8' } }],
    });
    const plan2 = CatalogPlan.compile({
      ...base,
      archetypes: [
        {
          ...walker,
          fields: [
            ...walker.fields,
            { name: 'store', codec: { t: 'u8' }, group: 'vitals' },
            { name: 'constructor', codec: { t: 'u8' }, group: 'vitals' },
          ],
        },
      ],
      commands: [...base.commands, command(21, 'Generated'), command(22, 'a-b'), command(23, 'a_b')],
    });
    const file = join(here, '.generated', 'catalog-collisions.gen.ts');
    writeFileSync(file, generateDecoders(plan2, bytes, { importFrom: '../../src/index.js' }));
    const config = ts.readConfigFile(join(here, '..', 'tsconfig.json'), (p) => ts.sys.readFile(p));
    const options = ts.parseJsonConfigFileContent(config.config, ts.sys, join(here, '..')).options;
    const program = ts.createProgram([file], { ...options, noEmit: true });
    const problems = ts
      .getPreEmitDiagnostics(program)
      .filter((d) => d.file !== undefined && ts.sys.resolvePath(d.file.fileName) === ts.sys.resolvePath(file))
      .map((d) => ts.flattenDiagnosticMessageText(d.messageText, '\n'));
    expect(problems).toEqual([]);
  }, 60_000);

  it('reads a store through typed column getters', async () => {
    const { WalkerColumns } = await generated();
    const walker = plan.archetypeByName('Walker')!;
    const w = new WireWriter(1024);
    writeTickHeader(w, 1, 1);
    writeEntitiesBlock(
      w,
      1,
      walker,
      [{ netId: 5, values: { hp: 812, speed: 1.5, account: 2n ** 60n + 3n, name: 'Ana' } }],
      [],
      [],
      [],
    );
    const applier = new FrameApplier(plan);
    applier.apply(w.toBytes());
    const columns = WalkerColumns.of(applier.world);
    const slot = applier.world.locate(5) & 0xffffff;
    expect(columns.hp[slot]).toBe(812);
    expect(columns.speed[slot]).toBe(1.5);
    expect(columns.account[slot]).toBe(2n ** 60n + 3n);
    expect(columns.name[slot]).toBe('Ana');
  });
});

function probeZero(): Probe {
  return { Boost: false, Gait: 0, Heading: 0, Speed: 0, Budget: 0, Delta: 0, Exact: 0, Big: 0n, id: 0n };
}

function namedOne(): CommandQueue {
  const q = queue(null);
  q.enqueue(plan.commandByName('MoveCmd')!, { Forward: 1, Strafe: 0, Buttons: 0, Fx: 0, Fz: 0 });
  return q;
}

// ── steady state ────────────────────────────────────────────────────────────────────────────────────────────────────────

function exposeCollector(): (() => void) | undefined {
  try {
    setFlagsFromString('--expose-gc');
    return runInNewContext('gc') as () => void;
  } catch {
    return undefined;
  }
}

const gc = exposeCollector();
const onCi = (process.env.CI ?? '') !== '';

const settle = async (): Promise<void> => {
  for (let i = 0; i < 3; i++) {
    await new Promise<void>((resolve) => setImmediate(resolve));
  }

  await new Promise<void>((resolve) => setTimeout(resolve, 0));
};

describe.skipIf(gc === undefined && !onCi)('typed commands steady state (#1614)', () => {
  it('allocates nothing per frame of two typed commands and a flush', async () => {
    const collect = gc!;
    const { commands } = await generated();
    const q = queue(commands);
    const record: MoveCmd = { Forward: 1, Strafe: -1, Buttons: 0, Fx: 0.25, Fz: -0.5 };
    let sent = 0;
    const send = (m: Uint8Array): void => {
      sent += m.length;
    };
    const frame = (k: number): void => {
      record.Buttons = k & 0xff;
      record.Fx = k * 0.125;
      q.enqueueTyped(commands.MoveCmd, record);
      q.enqueueTyped(commands.MoveCmd, record);
      q.flush(k, send);
    };

    // Warm up through the optimizing tiers: their lower tiers box numbers.
    for (let k = 0; k < 20_000; k++) {
      frame(k);
    }

    let collections = 0;
    const observer = new PerformanceObserver((list) => {
      collections += list.getEntries().length;
    });
    observer.observe({ entryTypes: ['gc'] });
    try {
      const memory = (): number => {
        const m = process.memoryUsage();
        return m.heapUsed + m.arrayBuffers;
      };
      const measure = async (work: () => void): Promise<number> => {
        collect();
        await settle();
        collections = 0;
        const before = memory();
        work();
        const after = memory();
        await settle();
        return collections === 0 ? after - before : Number.POSITIVE_INFINITY;
      };

      let baseline = Number.POSITIVE_INFINITY;
      for (let i = 0; i < 8; i++) {
        baseline = Math.min(baseline, await measure(() => {}));
      }

      let allocated = Number.POSITIVE_INFINITY;
      for (let attempt = 0; attempt < 20 && !(allocated <= 0); attempt++) {
        allocated = Math.min(
          allocated,
          (await measure(() => {
            for (let k = 0; k < 1000; k++) {
              frame(k);
            }
          })) - baseline,
        );
      }

      expect(sent).toBeGreaterThan(0);
      expect(allocated).toBeLessThanOrEqual(0);
    } finally {
      observer.disconnect();
    }
  }, 60_000);
});
