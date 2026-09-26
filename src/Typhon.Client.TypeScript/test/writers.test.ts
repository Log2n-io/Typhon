import { setFlagsFromString } from 'node:v8';
import { runInNewContext } from 'node:vm';
import { describe, expect, it } from 'vitest';
import {
  CatalogPlan,
  FieldPlan,
  FrameApplier,
  parseCatalog,
  parseWelcome,
  readCommands,
  SectionPlan,
  TickReader,
  WireReader,
  WireWriter,
  writeCommands,
  writeEntitiesBlock,
  writeBye,
  writeDebugBlock,
  writeAcksBlock,
  writeHello,
  writeKick,
  writePing,
  writePong,
  writeSection,
  writeSelfBlock,
  writeTickHeader,
  writeWelcome,
  type CommandSink,
  type TickSink,
} from '../src/index.js';
import { goldenBin } from './golden-support.js';
import { KITCHEN } from './frames.js';

/*
 * The encoders refuse what they cannot represent instead of writing its low bits, and the module-level decoders let go
 * of the message they read.
 */

const plan = CatalogPlan.compile(parseCatalog(goldenBin('catalog-kitchen-sink')));
const drone = plan.archetypeByName('Drone')!;
const beacon = plan.archetypeByName('Beacon')!;
const steer = plan.commandByName('Steer')!;

describe('field values', () => {
  it('are own properties: a field named `constructor` does not pick up the inherited member', () => {
    for (const codec of [{ t: 'f32' }, { t: 'f16' }, { t: 'quant', bits: 8, min: [0], max: [1] }]) {
      const section = new SectionPlan([new FieldPlan('constructor', 0, { name: 'constructor', codec }, codec, {})]);
      expect(() => {
        writeSection(new WireWriter(), section, {});
      }).toThrow(RangeError);
      expect(() => {
        writeSection(new WireWriter(), section, { constructor: 0.5 });
      }).not.toThrow();
    }
  });
});

describe('tick writers', () => {
  it('refuse a negative segment start tick and a frame tick beyond u32', () => {
    const segment = { netId: 1, position: [0, 0, 0], velocity: [0, 0, 0], t0: -1, epoch: 0 };
    expect(() => {
      writeEntitiesBlock(new WireWriter(), 0, drone, [], [segment], [], [], KITCHEN);
    }).toThrow(RangeError);
    expect(() => {
      writeEntitiesBlock(new WireWriter(), 2 ** 32, drone, [], [{ ...segment, t0: 2 ** 32 - 1 }], [], [], KITCHEN);
    }).toThrow(/frame tick/);
  });

  it('refuse a state or owner mask that only fits after truncation to 32 bits', () => {
    expect(() => {
      writeEntitiesBlock(
        new WireWriter(),
        1,
        beacon,
        [],
        [],
        [{ netId: 1, groupMask: 2 ** 32 + 1, values: {} }],
        [],
        KITCHEN,
      );
    }).toThrow(RangeError);
    expect(() => {
      writeSelfBlock(new WireWriter(), drone, 1, 0, 2 ** 32 + 1, {}, KITCHEN);
    }).toThrow(RangeError);
    expect(() => {
      writeTickHeader(new WireWriter(), 1, 256, 0);
    }).toThrow(RangeError);
    // Checked before PERIOD is ORed in, which would truncate them to 9.
    for (const flags of [2 ** 32 + 1, 1.5]) {
      expect(() => {
        writeTickHeader(new WireWriter(), 1, flags, 50_000);
      }).toThrow(RangeError);
    }
  });
});

describe('message and command writers', () => {
  const zeros = (n: number) => new Uint8Array(n);
  const hello = {
    major: 3,
    minor: 0,
    caps: 0,
    kind: 'player',
    token: '',
    resumeToken: zeros(16),
    clientCatalogHash: zeros(8),
    helloPayload: zeros(0),
  };
  const welcome = {
    major: 3,
    minor: 0,
    capsGranted: 0,
    sessionId: 1,
    resumeToken: zeros(16),
    tick: 1,
    tickPeriodUs: 50_000,
    catalogHash: zeros(8),
    catalogJson: zeros(0),
  };

  it('range-check every u16 and u32', () => {
    const cases: (() => void)[] = [
      () => {
        writeHello(new WireWriter(), { ...hello, major: 65_536 });
      },
      () => {
        writeHello(new WireWriter(), { ...hello, caps: 2 ** 32 });
      },
      () => {
        writeWelcome(new WireWriter(), { ...welcome, sessionId: -1 });
      },
      () => {
        writeWelcome(new WireWriter(), { ...welcome, tick: 1.5 });
      },
      () => {
        writePing(new WireWriter(), { clientMs: 2 ** 32, lastAppliedTick: 0 });
      },
      () => {
        writePong(new WireWriter(), { clientMs: 0, tick: 0, usIntoTick: -1 });
      },
      () => {
        writeKick(new WireWriter(), { code: 70_000, reason: '' });
      },
      () => {
        writeBye(new WireWriter(), 4000.5);
      },
      () => {
        writeCommands(new WireWriter(), 2 ** 32, [{ type: steer, seq: 0, values: {} }], KITCHEN);
      },
      () => {
        writeCommands(new WireWriter(), 0, [{ type: steer, seq: 65_536, values: {} }], KITCHEN);
      },
    ];
    for (const write of cases) {
      expect(write).toThrow(RangeError);
    }

    expect(() => {
      writeHello(new WireWriter(), hello);
      writeWelcome(new WireWriter(), welcome);
    }).not.toThrow();
  });
});

function exposeCollector(): (() => void) | undefined {
  try {
    setFlagsFromString('--expose-gc');
    return runInNewContext('gc') as () => void;
  } catch {
    return undefined;
  }
}

const gc = exposeCollector();

/** Whether `message` is collectable once `use` has read it and dropped it. */
async function collectableAfter(build: () => Uint8Array, use: (message: Uint8Array) => void): Promise<boolean> {
  const ref = ((): WeakRef<ArrayBuffer> => {
    const message = build();
    use(message);
    return new WeakRef(message.buffer as ArrayBuffer);
  })();
  // A WeakRef keeps its target alive until the current job ends.
  await new Promise<void>((resolve) => setImmediate(resolve));
  gc!();
  return ref.deref() === undefined;
}

describe.skipIf(gc === undefined && (process.env.CI ?? '') === '')('decoders reused across messages', () => {
  it('let go of a WELCOME, a TICK and a COMMANDS message once they are read', async () => {
    expect(gc, 'CI must be able to expose the collector').toBeDefined();
    expect(
      await collectableAfter(
        () => {
          const w = new WireWriter();
          writeWelcome(w, {
            major: 3,
            minor: 0,
            capsGranted: 0,
            sessionId: 1,
            resumeToken: new Uint8Array(16),
            tick: 1,
            tickPeriodUs: 50_000,
            catalogHash: new Uint8Array(8),
            catalogJson: new Uint8Array(1 << 16).fill(0x20),
          });
          return w.toBytes();
        },
        (message) => parseWelcome(message),
      ),
    ).toBe(true);

    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    expect(
      await collectableAfter(
        () => {
          const w = new WireWriter();
          writeTickHeader(w, 1, 0, 0);
          return w.toBytes();
        },
        (message) => {
          applier.apply(message);
        },
      ),
    ).toBe(true);

    const discard: CommandSink = {
      command: () => undefined,
      number: () => undefined,
      text: () => undefined,
      bytes: () => undefined,
      list: () => undefined,
    };
    expect(
      await collectableAfter(
        () => {
          const w = new WireWriter();
          writeCommands(
            w,
            1,
            [{ type: steer, seq: 1, values: { boost: 1, stance: 1, heading: 0, note: '', speed: 0 } }],
            KITCHEN,
          );
          return w.toBytes();
        },
        (message) => {
          readCommands(message, plan, discard);
        },
      ),
    ).toBe(true);
  });
});

describe('a released reader', () => {
  it('refuses every read, a restored limit included', () => {
    const r = new WireReader(Uint8Array.of(1, 2, 3, 4));
    const saved = r.pushLimit(2);
    r.release();
    expect(() => r.u8()).toThrow(/re-entered/);
    expect(() => r.u16()).toThrow(/re-entered/);
    expect(() => r.popLimit(saved)).toThrow(/re-entered/);
  });

  it('makes a TICK read that a sink re-entered fail instead of skipping the rest of the message', () => {
    const reader = new TickReader(plan);
    const w = new WireWriter();
    writeTickHeader(w, 1, 0, 0);
    writeDebugBlock(w, [{ subType: 1, payload: Uint8Array.of(9) }]);
    writeAcksBlock(w, [{ seq: 3, reason: 1 }]);
    const message = w.toBytes();
    const acks: number[] = [];
    const noop = (): void => undefined;
    const quiet: TickSink = {
      beginTick: noop,
      realm: noop,
      beginEntities: noop,
      enter: noop,
      segment: noop,
      state: noop,
      leave: noop,
      event: noop,
      self: noop,
      ack: noop,
      source: noop,
      beginAggregate: noop,
      aggregateCell: noop,
      metric: noop,
      debug: noop,
      ext: noop,
      unknownBlock: noop,
      endTick: noop,
      number: noop,
      text: noop,
      bytes: noop,
      list: noop,
    };
    const reentering: TickSink = {
      ...quiet,
      debug: () => {
        reader.read(message, quiet);
      },
      ack: (seq) => acks.push(seq),
    };

    expect(() => {
      reader.read(message, reentering);
    }).toThrow(/re-entered/);
    expect(acks).toEqual([]);
  });
});
