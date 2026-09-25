import { describe, expect, it } from 'vitest';
import {
  AckReason,
  BuiltInCommand,
  CatalogPlan,
  createClientRegion,
  readCommands,
  REGION_MAX_VERTICES,
  REGION_MIN_VERTICES_3D,
  RegionSender,
  validateCatalog,
  WireWriter,
  writeCommands,
  type CatalogCodec,
  type CatalogCommand,
  type FieldValues,
  type MessagePlan,
} from '../src/index.js';
import { catalog, catalogPlan } from './net-support.js';

/*
 * The built-in `ClientRegion` command (W28): a convex footprint polygon of 3–16 `pos2` points quantized like the world's
 * positions, at most 5 a second, latest wins. The builder must produce exactly the shape the engine's own builder does,
 * since a command named `ClientRegion` that is shaped differently is refused by both sides.
 */

const positionCodec: CatalogCodec = { t: 'pos2', bits: 24, min: [-8192, -8192], max: [8192, 8192] };

function quad(x: number, z: number, half: number): number[] {
  return [x - half, z - half, x + half, z - half, x + half, z + half, x - half, z + half];
}

describe('createClientRegion', () => {
  it('builds the command the catalog already carries, field for field', () => {
    const declared = catalog().commands.find((c) => c.name === BuiltInCommand.ClientRegion)!;
    const built = createClientRegion(
      declared.fields.find((f) => f.name === BuiltInCommand.regionVerticesField)!.codec.of!,
    );
    expect(built).toEqual(declared);
  });

  it('is accepted as a built-in, and a different shape under the same name is not', () => {
    const base = catalog();
    const withBuilt: CatalogCommand[] = [createClientRegion(positionCodec), ...base.commands.slice(1)];
    const problems: string[] = [];
    validateCatalog({ ...base, commands: withBuilt }, problems);
    expect(problems).toEqual([]);

    const broken: CatalogCommand = { ...createClientRegion(positionCodec), rate: { perSec: 60, burst: 60 } };
    const brokenProblems: string[] = [];
    validateCatalog({ ...base, commands: [broken, ...base.commands.slice(1)] }, brokenProblems);
    expect(brokenProblems.join('\n')).toMatch(/built-in whose shape/);
  });

  it('quantizes its vertices with the world’s own position codec', () => {
    const built = createClientRegion(positionCodec);
    const vertices = built.fields.find((f) => f.name === BuiltInCommand.regionVerticesField)!;
    expect(vertices.codec.of).toBe(positionCodec);
    expect([vertices.codec.minCount, vertices.codec.maxCount]).toEqual([3, REGION_MAX_VERTICES]);
  });

  it('takes a polyhedron of 4–16 pos3 points in a deep world, and validates as the built-in', () => {
    const pos3: CatalogCodec = { t: 'pos3', bits: 24, min: [-1024, -1024, -1024], max: [1024, 1024, 1024] };
    const built = createClientRegion(pos3);
    const vertices = built.fields.find((f) => f.name === BuiltInCommand.regionVerticesField)!;
    expect([vertices.codec.minCount, vertices.codec.maxCount]).toEqual([REGION_MIN_VERTICES_3D, REGION_MAX_VERTICES]);

    const base = catalog();
    const problems: string[] = [];
    validateCatalog({ ...base, commands: [built, ...base.commands.slice(1)] }, problems);
    expect(problems).toEqual([]);

    // A pos3 region that allows three points is not the built-in's shape.
    const loose: CatalogCommand = {
      ...built,
      fields: built.fields.map((f) =>
        f.name === BuiltInCommand.regionVerticesField ? { ...f, codec: { ...f.codec, minCount: 3 } } : f,
      ),
    };
    const looseProblems: string[] = [];
    validateCatalog({ ...base, commands: [loose, ...base.commands.slice(1)] }, looseProblems);
    expect(looseProblems.join(String.fromCharCode(10))).toMatch(/built-in whose shape/);
  });
});

interface Sent {
  readonly type: MessagePlan;
  readonly values: FieldValues;
}

function sender(
  plan: CatalogPlan,
  sent: Sent[],
  now: { ms: number },
  options: Partial<ConstructorParameters<typeof RegionSender>[0]> = {},
): RegionSender {
  return new RegionSender({
    plan,
    send: (type, values) => sent.push({ type, values }),
    now: () => now.ms,
    ...options,
  });
}

describe('RegionSender', () => {
  it('sends the first region, and one the camera barely moved for it does not', () => {
    const plan = catalogPlan();
    const sent: Sent[] = [];
    const now = { ms: 0 };
    const region = sender(plan, sent, now, { moveThreshold: 10 });

    expect(region.setRegion(quad(0, 0, 100), 50, 256)).toBe(true);
    expect(sent).toHaveLength(1);
    expect(sent[0]!.type).toBe(plan.clientRegion);
    expect(sent[0]!.values[BuiltInCommand.regionAltitudeField]).toBe(50);
    expect(sent[0]!.values[BuiltInCommand.regionBudgetField]).toBe(256);
    expect(Array.from(sent[0]!.values[BuiltInCommand.regionVerticesField] as number[])).toEqual(quad(0, 0, 100));

    now.ms = 10_000;
    expect(region.setRegion(quad(5, 5, 100), 50, 256)).toBe(false);
    expect([sent.length, region.suppressedCount, region.isPending]).toEqual([1, 1, false]);

    // A budget change is worth its bytes whatever the camera did.
    expect(region.setRegion(quad(5, 5, 100), 50, 512)).toBe(true);
    expect(sent).toHaveLength(2);
  });

  it('holds a region back until the rate allows it, and sends only the newest', () => {
    const plan = catalogPlan();
    const sent: Sent[] = [];
    const now = { ms: 0 };
    const region = sender(plan, sent, now, { moveThreshold: 1 });

    expect(region.setRegion(quad(0, 0, 100), 10, 64)).toBe(true);
    now.ms = 50;
    expect(region.setRegion(quad(100, 0, 100), 10, 64)).toBe(false);
    expect(region.isPending).toBe(true);
    now.ms = 100;
    // The camera moved again while the rate held the previous one: only the newest travels (latest wins).
    expect(region.setRegion(quad(200, 0, 100), 10, 64)).toBe(false);
    expect(region.poll(150)).toBe(false);

    // The built-in's rate is 5 Hz, so 200 ms after the first.
    expect(region.poll(200)).toBe(true);
    expect(sent).toHaveLength(2);
    expect(Array.from(sent[1]!.values[BuiltInCommand.regionVerticesField] as number[])).toEqual(quad(200, 0, 100));
    expect(region.isPending).toBe(false);
    expect(region.poll(400)).toBe(false);
    expect(region.sentCount).toBe(2);
  });

  it('encodes through the catalog plan, and decodes back to the polygon sent', () => {
    const plan = catalogPlan();
    const sent: Sent[] = [];
    const now = { ms: 0 };
    const region = sender(plan, sent, now);
    region.setRegion(quad(1024, -2048, 512), 42, 1024);

    const w = new WireWriter(256);
    writeCommands(w, 7, [{ type: sent[0]!.type, seq: 1, values: sent[0]!.values }]);
    const decoded: Record<string, number[]> = {};
    readCommands(w.toBytes(), plan, {
      command: (type, seq, clientTick) => {
        expect([type.name, seq, clientTick]).toEqual([BuiltInCommand.ClientRegion, 1, 7]);
      },
      number: (field, values) => {
        decoded[field.name] = [values[0]!];
      },
      list: (field, count, values) => {
        decoded[field.name] = Array.from(values.subarray(0, count * field.components));
      },
      text: () => undefined,
      bytes: () => undefined,
    });

    expect(decoded[BuiltInCommand.regionAltitudeField]).toEqual([42]);
    expect(decoded[BuiltInCommand.regionBudgetField]).toEqual([1024]);
    // 24-bit quantization over the grid's 16 384 m: the vertices come back within a quantum.
    const step = 16_384 / 2 ** 24;
    const vertices = decoded[BuiltInCommand.regionVerticesField]!;
    expect(vertices).toHaveLength(8);
    quad(1024, -2048, 512).forEach((expected, i) => {
      expect(Math.abs(vertices[i]! - expected)).toBeLessThanOrEqual(step);
    });
  });

  it('surfaces a refused region and never sends that footprint again', () => {
    const plan = catalogPlan();
    const sent: Sent[] = [];
    const rejected: number[] = [];
    const now = { ms: 0 };
    let seq = 40;
    const region = new RegionSender({
      plan,
      send: (type, values) => {
        sent.push({ type, values });
        return seq++;
      },
      now: () => now.ms,
      onRejected: (s) => rejected.push(s),
      moveThreshold: 1,
    });

    const footprint = [0, 0, 10, 0, 10, 0.0001];
    expect(region.setRegion(footprint, 5, 64)).toBe(true);
    // An ACK for someone else's command is not this sender's business.
    expect(region.onAck(999, AckReason.RegionInvalid)).toBe(false);
    expect(region.onAck(40, AckReason.RegionInvalid)).toBe(true);
    expect([rejected, region.rejectedCount]).toEqual([[40], 1]);

    // The hull the server refused: offering it again changes nothing, so it does not travel again.
    now.ms = 10_000;
    expect(region.setRegion(footprint, 5, 64)).toBe(false);
    expect(sent).toHaveLength(1);
    expect(region.suppressedCount).toBe(1);

    // A different footprint is this client's way out.
    expect(region.setRegion([0, 0, 100, 0, 100, 100], 5, 64)).toBe(true);
    expect(sent).toHaveLength(2);
    // A rate limit on a later region is not a refusal.
    expect(region.onAck(41, AckReason.RateLimited)).toBe(true);
    expect(region.rejectedCount).toBe(1);
  });

  it('refuses a polygon the wire cannot carry', () => {
    const plan = catalogPlan();
    const region = sender(plan, [], { ms: 0 });
    expect(() => region.setRegion([0, 0, 1, 1], 0, 1)).toThrow(/3–16 vertices/);
    expect(() => region.setRegion(new Array<number>(2 * (REGION_MAX_VERTICES + 1)).fill(0), 0, 1)).toThrow(
      /3–16 vertices/,
    );
    expect(() => region.setRegion([0, 0, 1, 1, Number.NaN, 2], 0, 1)).toThrow(/not finite/);
  });

  it('says so when the server did not enable the built-in', () => {
    const base = catalog();
    const plan = CatalogPlan.compile({ ...base, commands: base.commands.slice(1) });
    expect(() => sender(plan, [], { ms: 0 })).toThrow(/no ClientRegion/);
  });
});
