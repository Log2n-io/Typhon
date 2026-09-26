import { describe, expect, it } from 'vitest';
import { CatalogError, CatalogPlan, type Catalog } from '../src/index.js';
import { goldenBin } from './golden-support.js';

/*
 * `CatalogPlan.compile` is a trust boundary of its own: a catalog built in code, or parsed by something other than
 * `parseCatalog`, reaches it unvalidated. Each case breaks one rule on a copy of the kitchen-sink catalog and compiles
 * the object directly.
 */

type Mutable<T> = T extends readonly (infer E)[]
  ? Mutable<E>[]
  : T extends object
    ? { -readonly [K in keyof T]: Mutable<T[K]> }
    : T;

type CatalogObject = Mutable<Catalog>;

function kitchenSink(): CatalogObject {
  return JSON.parse(new TextDecoder().decode(goldenBin('catalog-kitchen-sink'))) as CatalogObject;
}

const archetype = (c: CatalogObject, name: string) => c.archetypes.find((a) => a.name === name)!;
const field = (fields: CatalogObject['archetypes'][number]['fields'], name: string) =>
  fields.find((f) => f.name === name)!;
const event = (c: CatalogObject, name: string) => c.events.find((e) => e.name === name)!;

const cases: [string, (c: CatalogObject) => void, RegExp][] = [
  [
    'a list whose element is not numeric',
    (c) => {
      field(event(c, 'Ping').fields, 'path').codec = { t: 'list', of: { t: 'str', maxBytes: 4 }, maxCount: 4 };
    },
    /numeric element codec/,
  ],
  [
    'a list of lists',
    (c) => {
      field(event(c, 'Ping').fields, 'path').codec = { t: 'list', of: { t: 'list', of: { t: 'u8' } }, maxCount: 4 };
    },
    /numeric element codec/,
  ],
  [
    'a list of more than 255 elements',
    (c) => {
      field(event(c, 'Ping').fields, 'path').codec = { t: 'list', of: { t: 'u8' }, maxCount: 256 };
    },
    /at most 255 elements/,
  ],
  [
    'a velocity without a unit exponent',
    (c) => {
      archetype(c, 'Drone').position!.vel = { t: 'vel3', bits: 8 };
    },
    /unitExp in/,
  ],
  [
    'a velocity with fewer axes than its position',
    (c) => {
      archetype(c, 'Drone').position!.vel = { t: 'vel2', bits: 8, unitExp: -8 };
    },
    /matching pos's dimensions/,
  ],
  [
    'a quantizer of 12 bits',
    (c) => {
      field(archetype(c, 'Drone').fields, 'temperature').codec.bits = 12;
    },
    /bits of 8, 16, 24 or 32/,
  ],
  [
    'a bit field of 25 bits',
    (c) => {
      field(archetype(c, 'Drone').fields, 'stance').codec.n = 25;
    },
    /bits\.n must be in \[1, 24\]/,
  ],
  [
    'a bytes field of −1 bytes',
    (c) => {
      field(archetype(c, 'Drone').fields, 'serial').codec.n = -1;
    },
    /bytes\.n must be a non-negative integer/,
  ],
  [
    'a string of 1.5 bytes at most',
    (c) => {
      field(archetype(c, 'Drone').fields, 'label').codec.maxBytes = 1.5;
    },
    /maxBytes must be a non-negative integer/,
  ],
  [
    'an unknown codec without fixedBytes',
    (c) => {
      delete field(archetype(c, 'Ledger').fields, 'future').codec.fixedBytes;
    },
    /unknown and declares no fixedBytes/,
  ],
  [
    'a packed field after a byte-aligned one',
    (c) => {
      const fields = event(c, 'Ping').fields;
      [fields[0], fields[1]] = [fields[1]!, fields[0]!];
    },
    /packed but follows a byte-aligned field/,
  ],
  [
    'a position that is not pos2 or pos3',
    (c) => {
      archetype(c, 'Beacon').position!.pos = { t: 'vec2', bits: 8, scale: 1 };
    },
    /pos2 or pos3 codec/,
  ],
  [
    'the linear model without a velocity',
    (c) => {
      delete archetype(c, 'Drone').position!.vel;
    },
    /vel2 or vel3 codec/,
  ],
  [
    'nine public groups',
    (c) => {
      archetype(c, 'Ledger').groups.push('a', 'b', 'c', 'd', 'e', 'f', 'g', 'h');
    },
    /more groups than a u8 mask addresses/,
  ],
  [
    'nine owner groups',
    (c) => {
      archetype(c, 'Drone').owner!.groups.push('a', 'b', 'c', 'd', 'e', 'f', 'g');
    },
    /more groups than a u8 mask addresses/,
  ],
  [
    'a bool metric',
    (c) => {
      c.metrics[4]!.codec = { t: 'bool' };
    },
    /not a byte-aligned number/,
  ],
  [
    'a bits metric',
    (c) => {
      c.metrics[4]!.codec = { t: 'bits', n: 3 };
    },
    /not a byte-aligned number/,
  ],
  [
    'a text metric',
    (c) => {
      c.metrics[4]!.codec = { t: 'str', maxBytes: 8 };
    },
    /not a byte-aligned number/,
  ],
  [
    'a grid of no tile',
    (c) => {
      c.grids[0] = { idx: 0, tileCells: 0, archetypes: [1] };
    },
    /a tile of at least one replication cell/,
  ],
  [
    'a grid of a fractional tile',
    (c) => {
      c.grids[0]!.tileCells = 1.5;
    },
    /a tile of at least one replication cell/,
  ],
  [
    'a grid counting an archetype that does not exist',
    (c) => {
      c.grids[0]!.archetypes = [1, 9];
    },
    /counts archetype 9, which does not exist/,
  ],
  [
    'a tick period of zero',
    (c) => {
      c.tick.periodUs = 0;
    },
    /tick period of 0 µs/,
  ],
  [
    'a fractional tick period',
    (c) => {
      c.tick.periodUs = 0.5;
    },
    /tick period of 0.5 µs/,
  ],
  [
    '256 archetypes',
    (c) => {
      const ledger = archetype(c, 'Ledger');
      for (let i = c.archetypes.length; i < 256; i++) {
        c.archetypes.push({ ...ledger, idx: i, name: `L${i}` });
      }
    },
    /256 archetypes; a store addresses at most 255/,
  ],
  [
    'an archetype index that is not its position',
    (c) => {
      c.archetypes[1]!.idx = 7;
    },
    /archetype 'Buoy' has index 7 at position 1/,
  ],
  [
    'metrics out of index order',
    (c) => {
      [c.metrics[0], c.metrics[1]] = [c.metrics[1]!, c.metrics[0]!];
    },
    /out of index order/,
  ],
  [
    'a grid index that is not its position',
    (c) => {
      c.grids[0]!.idx = 1;
    },
    /grid at position 0 has index 1/,
  ],
  [
    'an event index beyond 65 535',
    (c) => {
      event(c, 'Ping').idx = 70_000;
    },
    /wire index 70000 is outside \[0, 65535\]/,
  ],
  [
    'a negative command index',
    (c) => {
      c.commands[1]!.idx = -1;
    },
    /wire index -1 is outside/,
  ],
  [
    'an event index used twice',
    (c) => {
      event(c, 'Ping').idx = event(c, 'Chat').idx;
    },
    /wire index 16 is assigned twice/,
  ],
];

describe('CatalogPlan.compile refuses', () => {
  it('nothing in the unmodified catalog', () => {
    expect(() => CatalogPlan.compile(kitchenSink())).not.toThrow();
  });

  for (const [name, mutate, problem] of cases) {
    it(name, () => {
      const catalog = kitchenSink();
      mutate(catalog);
      let error: unknown = null;
      try {
        CatalogPlan.compile(catalog);
      } catch (e) {
        error = e;
      }

      expect(error).toBeInstanceOf(CatalogError);
      expect((error as CatalogError).problems.join('\n')).toMatch(problem);
    });
  }
});
