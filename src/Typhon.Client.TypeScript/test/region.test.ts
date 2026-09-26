import { describe, expect, it } from 'vitest';
import {
  AckReason,
  BuiltInCommand,
  CatalogPlan,
  createClientRegion,
  readCommands,
  RealmFrame,
  REGION_MAX_VERTICES,
  REGION_MIN_VERTICES,
  RegionSender,
  validateCatalog,
  WireWriter,
  writeCommands,
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

/** catalog-swg's realm: flat, ±8192 m at 24 bits. A deep one for the polyhedron cases. */
const FLAT = new RealmFrame(0, 0, 0, 0, 24, 256, false, [-8192, -8192, 0], [8192, 8192, 256]);
const DEEP = new RealmFrame(1, 0, 0, 0, 24, 256, true, [-8192, -8192, -8192], [8192, 8192, 8192]);

function quad(x: number, z: number, half: number): number[] {
  return [x - half, z - half, x + half, z - half, x + half, z + half, x - half, z + half];
}

/** A flat realm's footprint as it travels: pos3 triples, z = 0 (typhon.3, D-8). */
function onGround(pairs: number[]): number[] {
  const triples: number[] = [];
  for (let i = 0; i < pairs.length; i += 2) {
    triples.push(pairs[i]!, pairs[i + 1]!, 0);
  }

  return triples;
}

describe('createClientRegion', () => {
  it('builds the command the catalog already carries, field for field', () => {
    const declared = catalog().commands.find((c) => c.name === BuiltInCommand.ClientRegion)!;
    expect(createClientRegion()).toEqual(declared);
  });

  it('is accepted as a built-in, and a different shape under the same name is not', () => {
    const base = catalog();
    const withBuilt: CatalogCommand[] = [createClientRegion(), ...base.commands.slice(1)];
    const problems: string[] = [];
    validateCatalog({ ...base, commands: withBuilt }, problems);
    expect(problems).toEqual([]);

    const broken: CatalogCommand = { ...createClientRegion(), rate: { perSec: 60, burst: 60 } };
    const brokenProblems: string[] = [];
    validateCatalog({ ...base, commands: [broken, ...base.commands.slice(1)] }, brokenProblems);
    expect(brokenProblems.join('\n')).toMatch(/built-in whose shape/);
  });

  it('carries pos3 vertices over the realm frame (typhon.3, D-8): no width, no bounds of its own', () => {
    const vertices = createClientRegion().fields.find((f) => f.name === BuiltInCommand.regionVerticesField)!;
    expect(vertices.codec.of).toEqual({ t: 'pos3' });
    expect([vertices.codec.minCount, vertices.codec.maxCount]).toEqual([REGION_MIN_VERTICES, REGION_MAX_VERTICES]);
  });

  it('takes 4–16 triples in a deep realm and 3–16 pairs in a flat one', () => {
    const plan = catalogPlan();
    const flat = new RegionSender({ plan, send: () => 1, realm: () => FLAT });
    const deep = new RegionSender({ plan, send: () => 1, realm: () => DEEP });
    expect([flat.dims, deep.dims]).toEqual([2, 3]);
    expect(() => deep.setRegion([0, 0, 0, 1, 0, 0, 0, 1, 0], 1, 1)).toThrow(/4–16 vertices of 3 axes/);
    expect(() => flat.setRegion([0, 0, 1, 0, 0, 1], 1, 1)).not.toThrow();
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
    realm: () => FLAT,
    ...options,
  });
}

describe('RegionSender', () => {
  it('waits for a realm, and sends the same footprint again after a realm change (the server dropped it)', () => {
    const plan = catalogPlan();
    const sent: Sent[] = [];
    const now = { ms: 0 };
    let realm: RealmFrame | null = null;
    const region = sender(plan, sent, now, { realm: () => realm });

    // No realm: nothing to frame the vertices over, so the region waits.
    expect(region.setRegion(quad(0, 0, 100), 50, 256)).toBe(false);
    expect([sent.length, region.isPending]).toEqual([0, true]);
    realm = FLAT;
    expect(region.poll()).toBe(true);
    expect(sent).toHaveLength(1);

    // The same footprint is not worth its bytes — until the realm changes and the server forgets it.
    now.ms = 10_000;
    expect(region.setRegion(quad(0, 0, 100), 50, 256)).toBe(false);
    region.realmChanged();
    expect(region.poll()).toBe(true);
    expect(sent).toHaveLength(2);

    // Into a deep realm, a flat footprint is dropped rather than sent as a degenerate polyhedron.
    now.ms = 20_000;
    realm = DEEP;
    region.realmChanged();
    expect([region.poll(), region.isPending, sent.length]).toEqual([false, false, 2]);
  });

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
    expect(Array.from(sent[0]!.values[BuiltInCommand.regionVerticesField] as number[])).toEqual(
      onGround(quad(0, 0, 100)),
    );

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
    expect(Array.from(sent[1]!.values[BuiltInCommand.regionVerticesField] as number[])).toEqual(
      onGround(quad(200, 0, 100)),
    );
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
    writeCommands(w, 7, [{ type: sent[0]!.type, seq: 1, values: sent[0]!.values }], FLAT);
    const decoded: Record<string, number[]> = {};
    readCommands(
      w.toBytes(),
      plan,
      {
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
      },
      FLAT,
    );

    expect(decoded[BuiltInCommand.regionAltitudeField]).toEqual([42]);
    expect(decoded[BuiltInCommand.regionBudgetField]).toEqual([1024]);
    // pos3 at the 32-bit command width over the realm's 16 384 m: the vertices come back within a quantum, z = 0.
    const step = 16_384 / 2 ** 32;
    const vertices = decoded[BuiltInCommand.regionVerticesField]!;
    expect(vertices).toHaveLength(12);
    quad(1024, -2048, 512).forEach((expected, i) => {
      const at = Math.floor(i / 2) * 3 + (i % 2);
      expect(Math.abs(vertices[at]! - expected)).toBeLessThanOrEqual(step);
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
      realm: () => FLAT,
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
