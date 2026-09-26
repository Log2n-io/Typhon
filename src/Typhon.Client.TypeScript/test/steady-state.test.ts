import { PerformanceObserver } from 'node:perf_hooks';
import { setFlagsFromString } from 'node:v8';
import { runInNewContext } from 'node:vm';
import { describe, expect, it } from 'vitest';
import {
  CatalogPlan,
  FrameApplier,
  NOT_FOUND,
  parseCatalog,
  WireWriter,
  writeAggregateBlock,
  writeEntitiesBlock,
  writeEventsBlock,
  writeSelfBlock,
  writeStatsBlock,
  writeTickHeader,
  type ArchetypePlan,
  type Catalog,
  type EnterRecord,
  type EventInput,
  type FieldValues,
  type SegmentRecord,
  type StateRecord,
} from '../src/index.js';
import { goldenBin } from './golden-support.js';
import { KITCHEN } from './frames.js';

/*
 * AC-6 (`07-delivery.md`): once the store has grown to the frames' size, applying a steady-state frame — both passes and
 * the leaves — allocates nothing and retains nothing.
 *
 * - **The frames.** 10 000 Beacon state records and 100 enters or 100 leaves, alternating; 200 segments and 200 state
 *   records each for a `none` archetype, a linear 3D one and a 32-bit one, whose values have fractions and whose codes
 *   reach past 2^31 (u32, i32, f32, quant, vec, vel, unorm, snorm, angle, quat3, entityRef); `SELF`, `AGG` and `STATS`;
 *   100 events. Fractions matter: a whole number stays a small integer across any call and hides a boxing decode. The
 *   whole run is repeated with the tick past 2^31, where a start tick crossing a call as a number would be boxed once per
 *   record; the frame tick still is, a few times per frame, and is bounded there.
 * - **The measure.** V8's `heapUsed` plus `arrayBuffers`, summed over a fixed window of rounds after a full collection,
 *   against an empty window. A window during which a collection ran does not count as clean: collecting would hide what
 *   it collected. The optimizing compiler installs its code in the background, so windows are retried within a bound.
 * - **Warm-up.** Code that runs once per frame or per block (`SELF`, `STATS`, `AGG`, a block's header) is optimized only
 *   after thousands of frames, and until then V8's lower tiers box its numbers (measured: 16–112 B per frame, gone once
 *   optimized). Light frames of one record per block warm it up cheaply; the steady state is what AC-6 is about.
 * - **Text.** A decoded string is a new string, decoded from a new view of the message: `Chat` events are measured apart
 *   and bounded, not zero.
 */

/** The collector, when this runtime lets it be exposed; `undefined` otherwise (the file then skips, or fails on CI). */
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

const WINDOW_ROUNDS = 10;
/** The most a frame may allocate once its tick is past 2^31: the frame tick boxed where it crosses a call. */
const FRAME_TICK_BYTES = 64;
const WINDOW_ATTEMPTS = 20;
const ENTITIES = 200;

/** The kitchen sink plus an archetype whose every code is 32 bits wide. */
function catalog(): Catalog {
  const kitchenSink = parseCatalog(goldenBin('catalog-kitchen-sink'));
  const all = (t: string, extra: object = {}) => ({ t, ...extra });
  return {
    ...kitchenSink,
    archetypes: [
      ...kitchenSink.archetypes,
      {
        idx: kitchenSink.archetypes.length,
        name: 'Wide',
        groups: ['all'],
        position: {
          kind: 'motion',
          model: 'linear',
          pos: all('pos3', { bits: 32, min: [-1e6, -1e6, -1e6], max: [1e6, 1e6, 1e6] }),
          vel: all('vel3', { bits: 32, unitExp: -20 }),
        },
        fields: [
          { name: 'u', codec: all('u32'), group: 'all' },
          { name: 'i', codec: all('i32'), group: 'all' },
          { name: 'f', codec: all('f32'), group: 'all' },
          { name: 'q', codec: all('quant', { bits: 32, min: [-1000], max: [1000] }), group: 'all' },
          { name: 'v', codec: all('vec3', { bits: 32, scale: 1e-6 }), group: 'all' },
          { name: 'un', codec: all('unorm', { bits: 32 }), group: 'all' },
          { name: 'sn', codec: all('snorm', { bits: 32 }), group: 'all' },
          { name: 'an', codec: all('angle', { bits: 32 }), group: 'all' },
          { name: 'rot', codec: all('quat3'), group: 'all' },
        ],
      },
    ],
  };
}

const plan = CatalogPlan.compile(catalog());
const beacon = plan.archetypeByName('Beacon')!;
const buoy = plan.archetypeByName('Buoy')!;
const drone = plan.archetypeByName('Drone')!;
const wide = plan.archetypeByName('Wide')!;
const ping = plan.eventByName('Ping')!;
const chat = plan.eventByName('Chat')!;

function range(first: number, count: number): number[] {
  return Array.from({ length: count }, (_, i) => first + i);
}

const droneValues = (k: number): FieldValues => ({
  label: 'drone',
  serial: Uint8Array.of(1, 2, 3, 4),
  armed: 1,
  lights: 0,
  stance: 5,
  heading: 1.25 + k / 1000,
  // z negative: its 10 bits land in bits 22–31, so the code is above 2^30.
  rotation: [0.1, 0.2, -0.3, Math.sqrt(1 - 0.14)],
  thrust: [0.5, -1.5, 2.5],
  battery: 0.625,
  lastHit: 1,
  // Beyond 2^31: a varu or entityRef value outside the small-integer range.
  target: 3_000_000_000 + k,
  temperature: 21.5,
  tilt: -0.375,
});

const wideValues = (k: number): FieldValues => ({
  u: 3_000_000_000 + k,
  i: -2_000_000_000 - k,
  f: 0.1 + k,
  q: 123.456,
  v: [0.25, -0.5, 0.125],
  un: 0.7,
  sn: -0.3,
  an: -2.5,
  rot: [0.1, 0.2, -0.3, Math.sqrt(1 - 0.14)],
});

const buoyValues = (k: number): FieldValues => ({ depth: -300 - k, reading: -1_500_000_000 - k });

function positions(ids: number[], dims: 2 | 3, velocity: boolean, scale: number): SegmentRecord[] {
  return ids.map((netId) => {
    const x = ((netId % 97) + 0.25) * scale;
    return {
      netId,
      position: dims === 2 ? [x, -x] : [x, 0.5 * scale, -x],
      ...(velocity ? { velocity: [0.25 * scale, 0, -0.125 * scale] } : {}),
      t0: 1,
      epoch: 0,
    };
  });
}

function entities(
  w: WireWriter,
  archetype: ArchetypePlan,
  enters: readonly EnterRecord[],
  segments: readonly SegmentRecord[],
  states: readonly StateRecord[],
  leaves: readonly number[],
): void {
  writeEntitiesBlock(w, 1, archetype, enters, segments, states, leaves, KITCHEN);
}

const buoys = range(20_001, ENTITIES);
const drones = range(30_001, ENTITIES);
const wides = range(40_001, ENTITIES);
const beacons = range(1, 10_000);
const churn = range(10_001, 100);

function beaconEnters(ids: number[]): EnterRecord[] {
  return ids.map((netId) => ({
    netId,
    position: [(netId % 100) + 0.25, -(netId % 100) - 0.5],
    values: { channel: 1, strength: 0.5, drift: -2 },
  }));
}

/** Enters every entity the steady-state frames update. */
function setupFrame(): Uint8Array {
  const w = new WireWriter(1 << 22);
  writeTickHeader(w, 1, 0, 0);
  entities(w, beacon, beaconEnters(beacons), [], [], []);
  entities(
    w,
    buoy,
    positions(buoys, 2, false, 1).map((s, k) => ({ ...s, values: buoyValues(k) })),
    [],
    [],
    [],
  );
  entities(
    w,
    drone,
    positions(drones, 3, true, 1).map((s, k) => ({ ...s, values: droneValues(k) })),
    [],
    [],
    [],
  );
  entities(
    w,
    wide,
    positions(wides, 3, true, 1000).map((s, k) => ({ ...s, values: wideValues(k) })),
    [],
    [],
    [],
  );
  return w.toBytes();
}

/** A steady-state frame: `enter` adds the churn, otherwise it leaves. */
function steadyFrame(enter: boolean): Uint8Array {
  const w = new WireWriter(1 << 22);
  writeTickHeader(w, 1, 0, 0);
  // Events first: the apply order is exercised too.
  const pathPoint = [12.5, -7.25, 3.75, 0.5];
  writeEventsBlock(
    w,
    Array<EventInput>(100).fill({ type: ping, values: { from: drones[0]!, loud: 1, path: pathPoint } }),
    KITCHEN,
  );
  entities(
    w,
    beacon,
    enter ? beaconEnters(churn) : [],
    [],
    beacons.map((netId) => ({ netId, groupMask: 1, values: { strength: (netId % 7) / 8, drift: netId } })),
    enter ? [] : churn,
  );
  entities(
    w,
    buoy,
    [],
    positions(buoys, 2, false, 1),
    buoys.map((netId, k) => ({ netId, groupMask: 1, values: buoyValues(k) })),
    [],
  );
  entities(
    w,
    drone,
    [],
    positions(drones, 3, true, 1),
    drones.map((netId, k) => ({ netId, groupMask: 0b111, values: droneValues(k) })),
    [],
  );
  entities(
    w,
    wide,
    [],
    positions(wides, 3, true, 1000),
    wides.map((netId, k) => ({ netId, groupMask: 1, values: wideValues(k) })),
    [],
  );
  writeSelfBlock(
    w,
    drone,
    drones[0]!,
    7,
    0b11,
    {
      fuel: 2.75,
      manifest: Uint8Array.of(1, 2, 3),
      vault: 1,
      pin: 4242,
    },
    KITCHEN,
  );
  writeAggregateBlock(
    w,
    plan.grids[0]!,
    false,
    range(0, 16).map((cell) => ({ cell, counts: [cell, 2 * cell] })),
  );
  writeStatsBlock(w, plan, {
    'typhon.tick.p50': [2.5],
    'typhon.system.mean': [1.25, 2.5, 0.75],
    'typhon.session.skippedFrames': [3],
    'app.load': [0.375],
    'app.queue': [17],
  });
  return w.toBytes();
}

/** Every block of a steady-state frame, with one record each: the per-block code, warmed up cheaply. */
function lightFrame(): Uint8Array {
  const w = new WireWriter(1 << 12);
  writeTickHeader(w, 1, 0, 0);
  writeEventsBlock(w, [{ type: ping, values: { from: drones[0]!, loud: 1, path: [12.5, -7.25] } }], KITCHEN);
  entities(w, beacon, [], [], [{ netId: 1, groupMask: 1, values: { strength: 0.125, drift: 1 } }], []);
  entities(
    w,
    buoy,
    [],
    positions([buoys[0]!], 2, false, 1),
    [{ netId: buoys[0]!, groupMask: 1, values: buoyValues(0) }],
    [],
  );
  entities(
    w,
    drone,
    [],
    positions([drones[0]!], 3, true, 1),
    [{ netId: drones[0]!, groupMask: 0b111, values: droneValues(0) }],
    [],
  );
  entities(
    w,
    wide,
    [],
    positions([wides[0]!], 3, true, 1000),
    [{ netId: wides[0]!, groupMask: 1, values: wideValues(0) }],
    [],
  );
  writeSelfBlock(
    w,
    drone,
    drones[0]!,
    7,
    0b11,
    { fuel: 2.75, manifest: Uint8Array.of(1, 2, 3), vault: 1, pin: 4242 },
    KITCHEN,
  );
  writeAggregateBlock(w, plan.grids[0]!, false, [{ cell: 3, counts: [1, 2] }]);
  writeStatsBlock(w, plan, {
    'typhon.tick.p50': [2.5],
    'typhon.system.mean': [1.25, 2.5, 0.75],
    'typhon.session.skippedFrames': [3],
    'app.load': [0.375],
    'app.queue': [17],
  });
  return w.toBytes();
}

function chatFrame(): Uint8Array {
  const w = new WireWriter(1 << 16);
  writeTickHeader(w, 1, 0, 0);
  writeEventsBlock(
    w,
    Array<EventInput>(100).fill({
      type: chat,
      values: { attachment: Uint8Array.of(0, 255, 16), text: 'héllo \uFEFF☀' },
    }),
    KITCHEN,
  );
  return w.toBytes();
}

const settle = async (): Promise<void> => {
  for (let i = 0; i < 3; i++) {
    await new Promise<void>((resolve) => setImmediate(resolve));
  }

  await new Promise<void>((resolve) => setTimeout(resolve, 0));
};

interface SteadyState {
  readonly baseline: number;
  readonly allocated: number;
  readonly chatAllocated: number;
  readonly retained: number;
  readonly anomalies: number;
  readonly seen: number;
  readonly resolved: number;
}

/** Warms an applier up from `startTick`, then measures a window of steady-state rounds, of Chat frames, and retention. */
async function steadyState(collect: () => void, startTick: number): Promise<SteadyState> {
  let collections = 0;
  const observer = new PerformanceObserver((list) => {
    collections += list.getEntries().length;
  });
  observer.observe({ entryTypes: ['gc'] });

  try {
    let seen = 0;
    let resolved = 0;
    const from = ping.body.fields.findIndex((f) => f.name === 'from');
    const applier = new FrameApplier(plan, {
      initialRealm: KITCHEN,
      onEvent: (event) => {
        if (event.type === ping) {
          seen++;
          if (applier.world.locate(event.numbers[event.offsets[from]!]!) !== NOT_FOUND) {
            resolved++;
          }
        }
      },
    });

    // The tick is rewritten in place (offset 1, u32), so every frame advances it without building a new message.
    let tick = startTick;
    const apply = (message: Uint8Array): void => {
      tick++;
      message[1] = tick & 0xff;
      message[2] = (tick >>> 8) & 0xff;
      message[3] = (tick >>> 16) & 0xff;
      message[4] = (tick >>> 24) & 0xff;
      applier.apply(message);
    };

    const arrive = steadyFrame(true);
    const depart = steadyFrame(false);
    const chatter = chatFrame();
    const round = (): void => {
      apply(arrive);
      apply(depart);
    };

    apply(setupFrame());
    const light = lightFrame();
    for (let i = 0; i < 60; i++) {
      round();
      apply(chatter);
      for (let k = 0; k < 100; k++) {
        apply(light);
      }
    }

    const memory = (): number => {
      const m = process.memoryUsage();
      return m.heapUsed + m.arrayBuffers;
    };

    /** Bytes a window of `work` allocates, or +∞ when a collection ran inside it. */
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

    // The first measurements after the warm-up carry one-off costs of their own: take the least of several.
    let baseline = Number.POSITIVE_INFINITY;
    for (let i = 0; i < 8; i++) {
      baseline = Math.min(baseline, await measure(() => {}));
    }

    const window = (work: () => void) => () => {
      for (let i = 0; i < WINDOW_ROUNDS; i++) {
        work();
      }
    };

    // Retried only while the compiler settles: an allocation in every round shows in every window.
    let allocated = Number.POSITIVE_INFINITY;
    for (let attempt = 0; attempt < WINDOW_ATTEMPTS && !(allocated <= 0); attempt++) {
      allocated = Math.min(allocated, (await measure(window(round))) - baseline);
    }

    let chatAllocated = Number.POSITIVE_INFINITY;
    for (let attempt = 0; attempt < WINDOW_ATTEMPTS && !(chatAllocated <= 0); attempt++) {
      const chat = await measure(
        window(() => {
          apply(chatter);
        }),
      );
      chatAllocated = Math.min(chatAllocated, chat - baseline);
    }

    collect();
    await settle();
    const retainedBefore = memory();
    for (let i = 0; i < 50; i++) {
      round();
      apply(chatter);
    }

    collect();
    await settle();
    const retained = memory() - retainedBefore;
    return { baseline, allocated, chatAllocated, retained, anomalies: applier.world.anomalies, seen, resolved };
  } finally {
    observer.disconnect();
  }
}

describe.skipIf(gc === undefined && !onCi)('AC-6 steady state', () => {
  it.each([
    { from: 'a small tick', startTick: 1 },
    { from: 'a tick past 2^31', startTick: 3_000_000_000 },
  ])(
    'applies segments, state records, enters, leaves, SELF, AGG, STATS and events from $from, allocating nothing',
    async ({ startTick }) => {
      expect(gc, 'CI must be able to expose the collector').toBeDefined();
      const run = await steadyState(gc!, startTick);

      // Every empty window saw a collection: nothing below would mean anything.
      expect(Number.isFinite(run.baseline)).toBe(true);
      // Past 2^31 the frame tick itself is boxed a few times a frame (measured 32–48 B), where it crosses
      // `TickSink.beginTick` and the block readers as a number: per frame, never per record.
      expect(run.allocated).toBeLessThanOrEqual(startTick < 2 ** 31 ? 0 : 2 * WINDOW_ROUNDS * FRAME_TICK_BYTES);
      // Per text value, the string and the view `TextDecoder` decodes from: 128 B measured on Node 22, bounded at twice
      // that. Nothing else a Chat event carries allocates (its attachment is copied into a reused buffer).
      expect(run.chatAllocated).toBeLessThanOrEqual(WINDOW_ROUNDS * 100 * 256);
      // A leak of one byte per record would retain several megabytes over these frames.
      expect(run.retained).toBeLessThan(64 * 1024);
      expect(run.anomalies).toBe(0);
      expect([run.seen > 0, run.resolved]).toEqual([true, run.seen]);
    },
    120_000,
  );
});
