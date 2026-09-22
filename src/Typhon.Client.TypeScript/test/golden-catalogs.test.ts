import { describe, expect, it } from 'vitest';
import {
  CatalogError,
  CatalogPlan,
  CodecKind,
  parseCatalog,
  ValueKind,
  worldSchemaFromCatalog,
  type Catalog,
} from '../src/index.js';
import { goldenBin, goldenJson, goldenNames } from './golden-support.js';

/*
 * catalog-*: the canonical JSON parses, validates and compiles. The client never hashes, so the digest is not
 * recomputed.
 */

type Mutable<T> = T extends readonly (infer E)[]
  ? Mutable<E>[]
  : T extends object
    ? { -readonly [K in keyof T]: Mutable<T[K]> }
    : T;

/** A fresh, mutable copy of the kitchen-sink catalog, to break one rule at a time. */
function kitchenSink(): Mutable<Catalog> {
  return JSON.parse(new TextDecoder().decode(goldenBin('catalog-kitchen-sink'))) as Mutable<Catalog>;
}

function problemsOf(catalog: Mutable<Catalog>): string {
  try {
    parseCatalog(JSON.stringify(catalog));
  } catch (e) {
    expect(e).toBeInstanceOf(CatalogError);
    return (e as CatalogError).problems.join('\n');
  }

  throw new Error('the catalog was accepted');
}

interface CatalogVector {
  readonly hash: string;
  readonly byteCount: number;
}

interface RefusalsVector {
  readonly cases: { readonly name: string; readonly accept: boolean; readonly json: string }[];
}

describe('golden catalogs', () => {
  it('catalog-swg parses and compiles in wire order, with no re-sorting', () => {
    const bin = goldenBin('catalog-swg');
    expect(bin.length).toBe((goldenJson('catalog-swg') as CatalogVector).byteCount);
    const plan = CatalogPlan.compile(parseCatalog(bin));

    expect(plan.archetypes.map((a) => a.name)).toEqual(['Creature', 'Player']);
    const creature = plan.archetypeByName('Creature')!;
    expect(creature.position!.dims).toBe(2);
    expect(creature.position!.linear).toBe(true);
    expect(creature.onEnter.fields.map((f) => f.name)).toEqual(['template']);
    expect(creature.groupSections.map((s) => s.fields.map((f) => f.name))).toEqual([['mode'], ['hp']]);
    expect(creature.groupSections[0]!.packBytes).toBe(1);
    expect(creature.fields.map((f) => f.index)).toEqual([0, 1, 2]);
    expect(creature.fields[1]!.enumNames).toEqual(['Idle', 'Wander', 'Pursue', 'Fighting', 'Leashing', 'Dead']);
    // The velocity step is the position step: 16 384 m over 2^24 codes.
    expect(Array.from(creature.position!.vel!.velocityStep)).toEqual([16384 / 2 ** 24, 16384 / 2 ** 24]);

    expect(plan.clientRegion!.body.fields.map((f) => f.name)).toEqual(['altitudeM', 'budgetKiBps', 'vertices']);
    expect(plan.command(16).name).toBe('MoveTo');
    expect(plan.event(16).name).toBe('Attack');
    expect(() => plan.event(0)).toThrow();
    expect(plan.grids[0]!.cellCount).toBe(64 * 64);
    expect(plan.metrics.map((m) => [m.name, m.offset, m.valueCount])).toEqual([['typhon.tick.p99', 0, 1]]);
  });

  it('catalog-kitchen-sink compiles every construct', () => {
    const bin = goldenBin('catalog-kitchen-sink');
    expect(bin.length).toBe((goldenJson('catalog-kitchen-sink') as CatalogVector).byteCount);
    const catalog = parseCatalog(bin);
    const plan = CatalogPlan.compile(catalog);

    expect(plan.archetypes.map((a) => [a.name, a.position?.moving, a.position?.linear, a.position?.dims])).toEqual([
      ['Beacon', false, false, 2],
      ['Buoy', true, false, 2],
      ['Drone', true, true, 3],
      ['Ledger', undefined, undefined, undefined],
    ]);

    const drone = plan.archetypeByName('Drone')!;
    const flags = drone.groupSections[0]!;
    expect(flags.fields.map((f) => [f.name, f.bitOffset, f.bitCount])).toEqual([
      ['armed', 0, 1],
      ['lights', 1, 1],
      ['stance', 2, 6],
    ]);
    expect(flags.packBytes).toBe(1);
    expect(drone.fields.map((f) => [f.name, f.components])).toEqual([
      ['label', 0],
      ['serial', 0],
      ['armed', 1],
      ['lights', 1],
      ['stance', 1],
      ['heading', 1],
      ['rotation', 4],
      ['thrust', 3],
      ['battery', 1],
      ['lastHit', 1],
      ['target', 1],
      ['temperature', 1],
      ['tilt', 1],
    ]);
    expect(drone.ownerGroups).toEqual(['cargo', 'secrets']);
    expect(drone.ownerSections.map((s) => s.fields.map((f) => f.name))).toEqual([
      ['fuel', 'manifest'],
      ['vault', 'pin'],
    ]);
    expect(drone.ownerFields.map((f) => f.index)).toEqual([0, 1, 2, 3]);

    const future = plan.archetypeByName('Ledger')!.fields.find((f) => f.name === 'future')!;
    expect([future.kind, future.valueKind, future.fixedBytes]).toEqual([CodecKind.Unknown, ValueKind.Skipped, 3]);

    const path = plan.eventByName('Ping')!.body.fields.find((f) => f.name === 'path')!;
    expect([path.minCount, path.maxCount, path.components, path.element!.kind]).toEqual([0, 4, 2, CodecKind.Pos2]);

    expect(plan.serverMetrics.map((m) => [m.name, m.offset, m.valueCount])).toEqual([
      ['typhon.tick.p50', 0, 1],
      ['typhon.system.mean', 1, 3],
      ['app.load', 5, 1],
    ]);
    expect(plan.sessionMetrics.map((m) => [m.name, m.offset])).toEqual([
      ['typhon.session.skippedFrames', 4],
      ['app.queue', 6],
    ]);
    expect(plan.metricValueCount).toBe(7);
  });

  it('catalog-wide compiles at the limits: two-byte indices, a full group mask, a full u8 enum', () => {
    const bin = goldenBin('catalog-wide');
    expect(bin.length).toBe((goldenJson('catalog-wide') as CatalogVector).byteCount);
    const plan = CatalogPlan.compile(parseCatalog(bin));
    // What the plan must reproduce, read from the JSON itself rather than restated here.
    const json = JSON.parse(new TextDecoder().decode(bin)) as Catalog;

    expect(plan.archetypes.map((a) => [a.idx, a.name])).toEqual(json.archetypes.map((a) => [a.idx, a.name]));
    expect(plan.archetypes.length).toBeGreaterThan(128);
    for (const e of json.events) {
      expect(plan.event(e.idx).name).toBe(e.name);
    }

    expect(Math.max(...json.events.map((e) => e.idx))).toBeGreaterThanOrEqual(128);
    const widest = json.archetypes.reduce((x, y) => (y.fields.length > x.fields.length ? y : x));
    const wide = plan.archetype(widest.idx);
    expect(wide.groups.length).toBe(8);
    expect(wide.fields.map((f) => f.index)).toEqual(widest.fields.map((_, i) => i));
    for (const [name, names] of Object.entries(json.enums)) {
      expect(plan.catalog.enums[name]!.length, name).toBe(names.length);
    }

    expect(Object.values(json.enums).some((names) => names.length === 256)).toBe(true);
    const schema = worldSchemaFromCatalog(plan);
    expect(schema.archetypes[widest.idx]!.groups.length).toBe(8);
  });

  it('catalog-refusals: accepts and refuses each raw catalog text as the vector says', () => {
    const names = goldenNames('catalog-refusals');
    if (names.length === 0) {
      return;
    }

    const vector = goldenJson('catalog-refusals') as RefusalsVector;
    expect(vector.cases.length).toBeGreaterThan(0);
    for (const c of vector.cases) {
      let verdict: unknown = null;
      try {
        parseCatalog(c.json);
      } catch (e) {
        verdict = e;
      }

      if (c.accept) {
        expect(verdict, `${c.name}: accepted`).toBeNull();
      } else {
        expect(verdict, `${c.name}: refused with CatalogError`).toBeInstanceOf(CatalogError);
      }
    }
  });

  it('refuses what a client cannot decode, listing every problem', () => {
    const catalog = kitchenSink();
    const ledger = catalog.archetypes[3]!;
    ledger.fields[2] = { name: 'future', codec: { t: 'future' }, group: 'balance' };
    ledger.groups = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i'];
    catalog.commands[1]!.fields = [{ name: 'many', codec: { t: 'list', of: { t: 'u8' }, minCount: 5, maxCount: 4 } }];

    const problems = problemsOf(catalog);
    expect(problems).toMatch(/'future' is unknown and declares no usable fixedBytes/);
    expect(problems).toMatch(/9 groups; 0\.\.8 allowed/);
    expect(problems).toMatch(/list counts must satisfy/);
    expect(problems).toMatch(/names group 'balance', which the archetype does not declare/);
  });

  it('refuses a parameter the codec kind does not read, fixedBytes on a known codec included', () => {
    const catalog = kitchenSink();
    catalog.archetypes[0]!.fields[0]!.codec = { t: 'u8', fixedBytes: 2 };
    catalog.events.find((e) => e.name === 'Chat')!.fields.find((f) => f.name === 'text')!.codec = {
      t: 'str',
      maxBytes: 128,
      bits: 8,
    };
    const problems = problemsOf(catalog);
    expect(problems).toMatch(/field 'channel': codec 'u8' carries a parameter its kind does not read/);
    expect(problems).toMatch(/field 'text': codec 'str' carries a parameter its kind does not read/);
  });

  it('recognises built-ins by shape, and refuses SubscribeRequest until its shape is decided', () => {
    const catalog = kitchenSink();
    catalog.commands[0]!.rate = { perSec: 50, burst: 50 };
    catalog.metrics[0]!.unit = 's';
    catalog.metrics[3]!.name = 'typhon.custom';
    expect(problemsOf(catalog)).toMatch(/'ClientRegion' is a built-in whose shape/);
    expect(problemsOf(catalog)).toMatch(/'typhon.tick.p50' is a built-in whose shape/);
    expect(problemsOf(catalog)).toMatch(/'typhon.' prefix is reserved/);

    const subscribe = kitchenSink();
    subscribe.commands.splice(1, 0, { idx: 1, name: 'SubscribeRequest', delivery: 'latest', fields: [] });
    expect(problemsOf(subscribe)).toMatch(/'SubscribeRequest' is reserved/);
  });

  it('refuses grids of other than 2 or 3 axes, beyond 2^24 cells, or counting an archetype twice', () => {
    const catalog = kitchenSink();
    catalog.grids.push(
      { idx: 1, origin: [0], cell: 1, dims: [4], archetypes: [0] },
      { idx: 2, origin: [0, 0, 0], cell: 1, dims: [4096, 4096, 2], archetypes: [1, 1] },
    );
    const problems = problemsOf(catalog);
    expect(problems).toMatch(/grid 1: dims and origin need 2 or 3 matching axes/);
    expect(problems).toMatch(/grid 2: more than 16777216 cells/);
    expect(problems).toMatch(/grid 2: archetype index 1 does not exist or is listed twice/);
  });

  it('refuses a valid catalog that is not canonical: order, index, reserved range, spelled-out default', () => {
    const swapped = kitchenSink();
    [swapped.archetypes[0], swapped.archetypes[1]] = [swapped.archetypes[1]!, swapped.archetypes[0]!];
    swapped.archetypes[0].idx = 0;
    swapped.archetypes[1].idx = 1;
    expect(problemsOf(swapped)).toMatch(/archetype 'Beacon' is not at its ordinal position/);

    const fields = kitchenSink();
    const drone = fields.archetypes[2]!;
    [drone.fields[2], drone.fields[3]] = [drone.fields[3]!, drone.fields[2]!];
    expect(problemsOf(fields)).toMatch(/archetype 'Drone' groups or fields are not in wire order/);

    const event = kitchenSink();
    event.events[1]!.idx = 18;
    expect(problemsOf(event)).toMatch(/event 'Ping' is not at its ordinal index/);

    const command = kitchenSink();
    command.commands[1]!.idx = 2;
    expect(problemsOf(command)).toMatch(/command 'Steer' is not at its ordinal index from 16/);

    const metric = kitchenSink();
    metric.metrics[3]!.scope = 'server';
    expect(problemsOf(metric)).toMatch(/metric 'app.load' spells out a default scope or kind/);
  });

  it('refuses JSON that is not a catalog', () => {
    expect(() => parseCatalog('{')).toThrow(CatalogError);
    expect(() => parseCatalog('[]')).toThrow(/catalog must be an object/);
    expect(() => parseCatalog(new Uint8Array([0xff]))).toThrow(/UTF-8/);
    const wrongMajor = JSON.parse(new TextDecoder().decode(goldenBin('catalog-swg'))) as {
      protocol: { major: number };
    };
    wrongMajor.protocol.major = 3;
    expect(() => parseCatalog(JSON.stringify(wrongMajor))).toThrow(/protocol 3\.0; this client speaks major 2/);
  });
});
