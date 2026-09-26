import { describe, expect, it } from 'vitest';
import {
  archetypeOf,
  beginBlock,
  BlockType,
  CatalogPlan,
  Clock,
  endBlock,
  evaluateSlot,
  FrameApplier,
  RealmFrame,
  MAX_MOTION_STRIDE,
  DEFAULT_MAX_RENDER_DELAY_MS,
  MOTION_CHANGE_BIT,
  NOT_FOUND,
  parseCatalog,
  segmentHistoryFor,
  slotOf,
  TickFlags,
  WireFormatError,
  WireWriter,
  worldSchemaFromCatalog,
  writeAcksBlock,
  writeAggregateBlock,
  writeEntitiesBlock,
  writeEventsBlock,
  writeSelfBlock,
  writeSelfNoneBlock,
  writeSourcesBlock,
  writeStatsBlock,
  writeRealmBlock,
  writeTickHeader,
  type EnterRecord,
  type EventRecord,
  type StateRecord,
} from '../src/index.js';
import { goldenBin, goldenJson } from './golden-support.js';
import { KITCHEN } from './frames.js';

const plan = CatalogPlan.compile(parseCatalog(goldenBin('catalog-kitchen-sink')));
const beacon = plan.archetypeByName('Beacon')!;
const buoy = plan.archetypeByName('Buoy')!;
const drone = plan.archetypeByName('Drone')!;
const ledger = plan.archetypeByName('Ledger')!;
const ping = plan.eventByName('Ping')!;
const chat = plan.eventByName('Chat')!;
/** The Ledger's `future` field has a codec newer than this library: its value is its encoded bytes. */
const FUTURE = Uint8Array.of(0, 0, 0);

function beaconEnter(netId: number, channel = 1): EnterRecord {
  return { netId, position: [netId, -netId], values: { channel, strength: 0.5, drift: -1 } };
}

function droneValues(netId: number): Record<string, number | number[] | string | Uint8Array> {
  return {
    serial: Uint8Array.of(1, 2, 3, netId),
    label: `d${netId}`,
    armed: 1,
    lights: 0,
    stance: 2,
    heading: 0.5,
    rotation: [0, 0, 0, 1],
    thrust: [1, 2, 3],
    battery: 1,
    lastHit: 990,
    target: 7,
    temperature: 20,
    tilt: 0,
  };
}

interface FrameParts {
  readonly tick: number;
  readonly flags?: number;
  readonly periodUs?: number;
  readonly write: (w: WireWriter) => void;
}

function frame({ tick, flags = 0, periodUs = 0, write }: FrameParts): Uint8Array {
  const w = new WireWriter();
  writeTickHeader(w, tick, flags, periodUs);
  write(w);
  return w.toBytes();
}

function beacons(tick: number, enters: number[], leaves: number[] = [], states: StateRecord[] = []): Uint8Array {
  return frame({
    tick,
    write: (w) => {
      writeEntitiesBlock(
        w,
        tick,
        beacon,
        enters.map((id) => beaconEnter(id)),
        [],
        states,
        leaves,
        KITCHEN,
      );
    },
  });
}

function slot(applier: FrameApplier, netId: number): number {
  const location = applier.world.locate(netId);
  expect(location).not.toBe(NOT_FOUND);
  return slotOf(location);
}

describe('worldSchemaFromCatalog', () => {
  it('maps positions, groups, onEnter fields and storage from the catalog, and skips unknown codecs', () => {
    const schema = worldSchemaFromCatalog(plan);
    expect(schema.archetypes.map((a) => [a.name, a.position])).toEqual([
      ['Beacon', { kind: 'static', dims: 2 }],
      ['Buoy', { kind: 'motion', model: 'none', dims: 2 }],
      ['Drone', { kind: 'motion', model: 'linear', dims: 3 }],
      ['Ledger', undefined],
    ]);
    expect(schema.archetypes[2]!.fields).toEqual([
      { name: 'label', kind: 'text' },
      { name: 'serial', kind: 'bytes' },
      { name: 'armed', kind: 'u8', group: 0 },
      { name: 'lights', kind: 'u8', group: 0 },
      { name: 'stance', kind: 'u8', group: 0 },
      { name: 'heading', kind: 'f64', group: 1 },
      { name: 'rotation', kind: 'f64', components: 4, group: 1 },
      { name: 'thrust', kind: 'f64', components: 3, group: 1 },
      { name: 'battery', kind: 'f64', group: 2 },
      { name: 'lastHit', kind: 'u32', group: 2 },
      { name: 'target', kind: 'u32', group: 2 },
      { name: 'temperature', kind: 'f64', group: 2 },
      { name: 'tilt', kind: 'f64', group: 2 },
    ]);
    expect(schema.archetypes[3]!.fields.map((f) => f.name)).toEqual(['owner', 'amount']);
  });
});

describe('FrameApplier', () => {
  it('keeps slots stable: a slot freed in a frame is reused only in a later one', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(beacons(1, [1, 2]));
    const of1 = slot(applier, 1);
    applier.apply(beacons(2, [3], [1]));
    const of3 = slot(applier, 3);
    applier.apply(beacons(3, [4]));
    expect(of3).not.toBe(of1);
    expect(slot(applier, 4)).toBe(of1);
    expect(slot(applier, 2)).toBe(1);
    expect(applier.world.anomalies).toBe(0);
  });

  it('applies events after enters and before leaves, whatever order the blocks travel in', () => {
    const seen: { from: number; fromHeld: boolean; loud: number; path: number[] }[] = [];
    const applier = new FrameApplier(plan, {
      initialRealm: KITCHEN,
      onEvent: (event: EventRecord) => {
        const from = event.number('from');
        const path = event.fieldIndex('path');
        seen.push({
          from,
          fromHeld: applier.world.locate(from) !== NOT_FOUND,
          loud: event.number('loud'),
          path: Array.from(event.numbers.subarray(event.offsets[path], event.offsets[path]! + 2 * event.counts[path]!)),
        });
      },
    });
    applier.apply(beacons(1, [5]));

    // EVENTS travel first: one names netId 6, which enters later in the message; one names 5, which leaves in it.
    applier.apply(
      frame({
        tick: 2,
        write: (w) => {
          writeEventsBlock(
            w,
            [
              { type: ping, values: { from: 6, loud: 1, path: [0.5, -0.5] } },
              { type: ping, values: { from: 5, loud: 0, path: [] } },
            ],
            KITCHEN,
          );
          writeEntitiesBlock(w, 2, beacon, [beaconEnter(6)], [], [], [5], KITCHEN);
        },
      }),
    );

    expect(seen).toEqual([
      { from: 6, fromHeld: true, loud: 1, path: [0.5, -0.5] },
      { from: 5, fromHeld: true, loud: 0, path: [] },
    ]);
    expect(applier.world.locate(5)).toBe(NOT_FOUND);
    expect(applier.world.archetypeStore(beacon.idx).left[0]).toBe(5);
  });

  it('counts a tick that does not advance as an anomaly, except in a RESET frame', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(beacons(100, [1]));
    applier.apply(frame({ tick: 100, write: () => {} }));
    expect(applier.world.anomalies).toBe(1);
    applier.apply(frame({ tick: 5, flags: TickFlags.Reset, write: () => {} }));
    expect(applier.world.anomalies).toBe(1);
    expect(applier.tick).toBe(5);
  });

  it('clears the world, the aggregates and the owner state on RESET, before the frame applies; metrics survive', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(
      frame({
        tick: 1,
        write: (w) => {
          writeEntitiesBlock(w, 1, beacon, [beaconEnter(1), beaconEnter(2), beaconEnter(3)], [], [], [], KITCHEN);
          writeSelfBlock(w, drone, 9, 4, 0b01, { fuel: 2.5, manifest: Uint8Array.of(7) }, KITCHEN);
          writeAggregateBlock(w, plan.grids[0]!, true, [{ cell: 3, counts: [1, 2] }]);
          writeStatsBlock(w, plan, {
            'typhon.tick.p50': [2.5],
            'typhon.system.mean': [1, 2, 3],
            'typhon.session.skippedFrames': [4],
            'app.load': [0.5],
            'app.queue': [9],
          });
        },
      }),
    );
    expect(applier.selfState.archetype).toBe(drone);
    expect(applier.grids[0]!.count(3, 1)).toBe(2);

    applier.apply(
      frame({
        tick: 2,
        flags: TickFlags.Reset,
        write: (w) => {
          writeEntitiesBlock(w, 2, beacon, [beaconEnter(2)], [], [], [], KITCHEN);
        },
      }),
    );
    expect(applier.flags).toBe(TickFlags.Reset);
    expect(applier.world.resetThisFrame).toBe(true);
    expect(applier.world.entityCount).toBe(1);
    expect(applier.world.locate(1)).toBe(NOT_FOUND);
    expect(applier.world.locate(2)).not.toBe(NOT_FOUND);
    expect(applier.world.archetypeStore(beacon.idx).leftCount).toBe(0);
    expect(applier.selfState.archetype).toBeNull();
    expect(applier.grids[0]!.count(3, 1)).toBe(0);
    expect(applier.stats.value('typhon.system.mean', 2)).toBe(3);
    expect(applier.stats.value('app.queue')).toBe(9);
    expect(applier.stats.received).toBe(false);
  });

  it('refuses netId 0 in the entity SELF writer: it is writeSelfNoneBlock', () => {
    expect(() => {
      frame({
        tick: 1,
        write: (w) => {
          writeSelfBlock(w, drone, 0, 1, 0, {}, KITCHEN);
        },
      });
    }).toThrow(RangeError);
  });

  it('drops the owner state and keeps lastSeq on a SELF with no controlled entity (W17′)', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(
      frame({
        tick: 1,
        write: (w) => {
          writeSelfBlock(w, drone, 9, 4, 0b01, { fuel: 2.5, manifest: Uint8Array.of(7) }, KITCHEN);
        },
      }),
    );
    expect(applier.selfState.archetype).toBe(drone);

    applier.apply(
      frame({
        tick: 2,
        write: (w) => {
          writeSelfNoneBlock(w, 5);
        },
      }),
    );
    const self = applier.selfState;
    expect(self.received).toBe(true);
    expect(self.archetype).toBeNull();
    expect(self.netId).toBe(0);
    expect(self.lastSeq).toBe(5);
    expect(self.numbers.length).toBe(0);
    expect(self.present.length).toBe(0);
  });

  it('lets a netId come back as another archetype in a later frame, and replaces a live one inside a frame', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(beacons(1, [3]));
    applier.apply(beacons(2, [], [3]));
    applier.apply(
      frame({
        tick: 3,
        write: (w) => {
          writeEntitiesBlock(
            w,
            3,
            ledger,
            [{ netId: 3, values: { owner: 9, amount: 5, future: FUTURE } }],
            [],
            [],
            [],
            KITCHEN,
          );
        },
      }),
    );

    expect(archetypeOf(applier.world.locate(3))).toBe(ledger.idx);
    const store = applier.world.archetypeStore(ledger.idx);
    expect(store.field('owner')[slot(applier, 3)]).toBe(9);
    expect(store.field('amount')[slot(applier, 3)]).toBe(5);

    expect(applier.world.anomalies).toBe(0);
  });

  it('applies § 10 netId reuse: an enter for a live netId replaces it as an anomaly, and leaves apply last', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(beacons(1, [3, 4]));

    // No well-formed frame carries this: an enter for netId 3, which the Beacon still holds. The enter replaces it.
    applier.apply(
      frame({
        tick: 2,
        write: (w) => {
          writeEntitiesBlock(
            w,
            2,
            ledger,
            [{ netId: 3, values: { owner: 9, amount: 5, future: FUTURE } }],
            [],
            [],
            [],
            KITCHEN,
          );
        },
      }),
    );
    expect(applier.world.anomalies).toBe(1);
    expect(archetypeOf(applier.world.locate(3))).toBe(ledger.idx);
    expect(applier.world.archetypeStore(beacon.idx).liveCount).toBe(1);
    expect(applier.world.archetypeStore(beacon.idx).left[0]).toBe(3);

    // Leaves apply after everything else, to the holder of their block's archetype. The Beacon block's leave of 4
    // travels before the Ledger block whose enter replaces the Beacon: by the time leaves apply, a Ledger holds 4, so
    // the leave is an anomaly and changes nothing. So is the leave of 77, which nobody holds.
    applier.apply(
      frame({
        tick: 3,
        write: (w) => {
          writeEntitiesBlock(w, 3, beacon, [], [], [], [4, 77], KITCHEN);
          writeEntitiesBlock(
            w,
            3,
            ledger,
            [{ netId: 4, values: { owner: 1, amount: 2, future: FUTURE } }],
            [],
            [],
            [],
            KITCHEN,
          );
        },
      }),
    );
    expect(archetypeOf(applier.world.locate(4))).toBe(ledger.idx);
    expect(applier.world.anomalies).toBe(4);
    expect(applier.world.entityCount).toBe(2);
    expect(applier.world.archetypeStore(beacon.idx).liveCount).toBe(0);
  });

  it('applies a leave to the entity its own block entered in the same frame', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(beacons(1, [5], [5]));
    expect(applier.world.locate(5)).toBe(NOT_FOUND);
    expect(applier.world.archetypeStore(beacon.idx).liveCount).toBe(0);
  });

  it('counts a leave in another archetype’s block as an anomaly, and changes nothing', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(beacons(1, [9]));
    applier.apply(
      frame({
        tick: 2,
        write: (w) => {
          writeEntitiesBlock(w, 2, ledger, [], [], [], [9], KITCHEN);
        },
      }),
    );
    expect(archetypeOf(applier.world.locate(9))).toBe(beacon.idx);
    expect(applier.world.anomalies).toBe(1);
  });

  it('writes enters, state groups and segments into the store, in 3D, marking what changed', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(
      frame({
        tick: 1000,
        write: (w) => {
          writeEntitiesBlock(
            w,
            1000,
            drone,
            [
              {
                netId: 100,
                position: [10, 20, -30],
                velocity: [0.5, 0, -0.25],
                t0: 1000,
                epoch: 1,
                values: droneValues(100),
              },
            ],
            [],
            [],
            [],
            KITCHEN,
          );
        },
      }),
    );

    const store = applier.world.archetypeStore(drone.idx);
    const s = slot(applier, 100);
    expect(store.textAt(store.fieldIndex('label'))[s]).toBe('d100');
    expect(Array.from(store.bytesAt(store.fieldIndex('serial'))[s]!)).toEqual([1, 2, 3, 100]);
    expect(Array.from(store.field('thrust').subarray(3 * s, 3 * s + 3))).toEqual([1, 2, 3]);
    expect(store.field('stance')[s]).toBe(2);
    const out = new Float64Array(MAX_MOTION_STRIDE);
    evaluateSlot(store, s, 1002, 0, out, 0);
    expect(Array.from(out)).toEqual([11, 20, -30.5, 0.5, 0, -0.25]);

    applier.apply(
      frame({
        tick: 1001,
        write: (w) => {
          writeEntitiesBlock(
            w,
            1001,
            drone,
            [],
            [{ netId: 100, position: [11, 21, -31], velocity: [0.25, 0, 0], t0: 1001, epoch: 1 }],
            [{ netId: 100, groupMask: 0b100, values: { ...droneValues(100), battery: 0.5, temperature: -40 } }],
            [],
            KITCHEN,
          );
        },
      }),
    );

    expect(store.updateMask[s]).toBe(0b100 | MOTION_CHANGE_BIT);
    expect(store.field('battery')[s]).toBeCloseTo(0.5, 4);
    expect(store.field('temperature')[s]).toBe(-40);
    expect(store.field('stance')[s]).toBe(2);
    evaluateSlot(store, s, 1003, 0, out, 0);
    expect(Array.from(out)).toEqual([11.5, 21, -31, 0.25, 0, 0]);
  });

  it('accumulates owner groups across SELF blocks, and resets per-frame lists', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN });
    applier.apply(
      frame({
        tick: 1,
        write: (w) => {
          writeSelfBlock(
            w,
            drone,
            100,
            5,
            0b11,
            { fuel: 2.5, manifest: Uint8Array.of(1, 2), vault: 1, pin: 42 },
            KITCHEN,
          );
          writeAcksBlock(w, [{ seq: 5, reason: 2 }]);
          writeSourcesBlock(w, [{ requestId: 9, status: 1, code: 404 }]);
        },
      }),
    );
    const self = applier.selfState;
    expect([self.netId, self.lastSeq, self.ownerMask, self.received]).toEqual([100, 5, 3, true]);
    expect(applier.acks.count).toBe(1);
    expect([applier.sources.requestId[0], applier.sources.status[0], applier.sources.code[0]]).toEqual([9, 1, 404]);

    applier.apply(
      frame({
        tick: 2,
        write: (w) => {
          writeSelfBlock(w, drone, 100, 6, 0b10, { vault: 0, pin: 43 }, KITCHEN);
        },
      }),
    );
    expect([self.lastSeq, self.ownerMask, self.number('pin'), self.number('vault'), self.number('fuel')]).toEqual([
      6, 2, 43, 0, 2.5,
    ]);
    const manifest = drone.ownerFields.findIndex((f) => f.name === 'manifest');
    const kept = self.bytes[manifest]!;
    expect(Array.from(kept)).toEqual([1, 2]);
    expect([applier.acks.count, applier.sources.count]).toEqual([0, 0]);

    // Bytes resent unchanged keep their array; changed bytes get a new one.
    applier.apply(
      frame({
        tick: 3,
        write: (w) => {
          writeSelfBlock(w, drone, 100, 6, 0b01, { fuel: 2.5, manifest: Uint8Array.of(1, 2) }, KITCHEN);
        },
      }),
    );
    expect(self.bytes[manifest]).toBe(kept);
    applier.apply(
      frame({
        tick: 4,
        write: (w) => {
          writeSelfBlock(w, drone, 100, 6, 0b01, { fuel: 2.5, manifest: Uint8Array.of(1, 3) }, KITCHEN);
        },
      }),
    );
    expect(self.bytes[manifest]).not.toBe(kept);
    expect(Array.from(self.bytes[manifest]!)).toEqual([1, 3]);

    applier.apply(frame({ tick: 5, write: () => {} }));
    expect([self.received, self.ownerMask, self.lastSeq]).toEqual([false, 0, 6]);

    // Another controlled entity starts from no owner values.
    applier.apply(
      frame({
        tick: 6,
        write: (w) => {
          writeSelfBlock(w, drone, 101, 7, 0b10, { vault: 1, pin: 1 }, KITCHEN);
        },
      }),
    );
    expect(Number.isNaN(self.number('fuel'))).toBe(true);
    expect(applier.world.anomalies).toBe(0);
  });

  it('propagates a throwing event handler, leaving the frame partly applied: its leaves have not run', () => {
    const applier = new FrameApplier(plan, {
      initialRealm: KITCHEN,
      onEvent: () => {
        throw new Error('handler bug');
      },
    });
    applier.apply(beacons(1, [5]));
    const message = frame({
      tick: 2,
      write: (w) => {
        writeEntitiesBlock(w, 2, beacon, [beaconEnter(6)], [], [], [5], KITCHEN);
        writeEventsBlock(w, [{ type: ping, values: { from: 6, loud: 1, path: [] } }], KITCHEN);
      },
    });

    expect(() => {
      applier.apply(message);
    }).toThrow('handler bug');
    expect(applier.world.locate(6)).not.toBe(NOT_FOUND);
    expect(applier.world.locate(5)).not.toBe(NOT_FOUND);
  });

  it('throws 1007 for a malformed EVENTS block once the first pass has applied the rest of the frame', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN, onEvent: () => {} });
    const message = frame({
      tick: 1,
      write: (w) => {
        writeEventsBlock(w, [{ type: ping, values: { from: 8, loud: 0, path: [] } }], KITCHEN);
        writeEntitiesBlock(w, 1, beacon, [beaconEnter(8)], [], [], [], KITCHEN);
        // One event of type 200, which the catalog does not declare.
        const mark = beginBlock(w, BlockType.Events);
        w.varu(1);
        w.varu(200);
        endBlock(w, mark);
      },
    });

    let error: unknown = null;
    try {
      applier.apply(message);
    } catch (e) {
      error = e;
    }

    expect(error).toBeInstanceOf(WireFormatError);
    expect((error as WireFormatError).closeCode).toBe(1007);
    expect(applier.world.locate(8)).not.toBe(NOT_FOUND);
  });

  it('grows an event’s bytes buffer on demand', () => {
    const received: [number[], string][] = [];
    const applier = new FrameApplier(plan, {
      initialRealm: KITCHEN,
      onEvent: (event) => {
        const at = event.fieldIndex('attachment');
        received.push([
          Array.from(event.bytes[at]!.subarray(0, event.counts[at])),
          event.texts[event.fieldIndex('text')]!,
        ]);
      },
    });
    const long = Array.from({ length: 32 }, (_, i) => i);
    applier.apply(
      frame({
        tick: 1,
        write: (w) => {
          writeEventsBlock(
            w,
            [
              { type: chat, values: { attachment: Uint8Array.of(9), text: 'm1' } },
              { type: chat, values: { attachment: Uint8Array.from(long), text: 'm2' } },
              { type: chat, values: { attachment: new Uint8Array(0), text: 'm3' } },
            ],
            KITCHEN,
          );
        },
      }),
    );

    expect(received).toEqual([
      [[9], 'm1'],
      [long, 'm2'],
      [[], 'm3'],
    ]);
  });

  it('a REALM re-lays the aggregate grids over its frame and fires onRealmChanged once, before the records it frames', () => {
    const changes: [number | null, number | null][] = [];
    // What the store held when each change fired: after the reset, before any record of the new realm.
    const heldAtChange: number[] = [];
    const applier: FrameApplier = new FrameApplier(plan, {
      onRealmChanged: (previous, current) => {
        changes.push([previous?.realmId ?? null, current?.realmId ?? null]);
        heldAtChange.push(applier.world.entityCount);
      },
    });
    const small = new RealmFrame(4, 1, 0, 7, 16, 64, false, [0, 0, 0], [1024, 512, 64]);
    const realmFrame = (tick: number, realm: RealmFrame | null): Uint8Array =>
      frame({
        tick,
        flags: TickFlags.Reset,
        write: (w) => {
          writeRealmBlock(w, realm);
          if (realm !== null) {
            writeEntitiesBlock(w, tick, beacon, [beaconEnter(1)], [], [], [], realm);
          }
        },
      });

    applier.apply(realmFrame(1, small));
    // 1024 × 512 m in 4-cell tiles of 64 m: 4 × 2 cells, two axes in a flat realm.
    expect([applier.realmFrame?.realmId, applier.grids[0]!.schema.dims, applier.grids[0]!.cellCount]).toEqual([
      4,
      [4, 2],
      8,
    ]);
    expect(applier.world.entityCount).toBe(1);

    applier.apply(realmFrame(2, small));
    expect(changes).toEqual([[null, 4]]);

    applier.apply(realmFrame(3, null));
    expect([changes, applier.realmFrame, applier.world.entityCount]).toEqual([
      [
        [null, 4],
        [4, null],
      ],
      null,
      0,
    ]);
    expect(heldAtChange).toEqual([0, 0]);
  });

  it('applies AGG to a three-axis grid, laid over a deep realm, whose counts exist only once a block arrived', () => {
    const cube = CatalogPlan.compile({
      ...plan.catalog,
      grids: [{ idx: 0, tileCells: 1, archetypes: [1, 2] }],
    });
    // A deep realm of 20 × 30 × 40 m in 10 m cells: the grid is 2 × 3 × 4 cells (typhon.3: the frame's, not the catalog's).
    const applier = new FrameApplier(cube, {
      initialRealm: new RealmFrame(5, 0, 0, 0, 24, 10, true, [0, 0, 0], [20, 30, 40]),
    });
    const grid = applier.grids[0]!;
    expect([grid.cellCount, grid.counts.length]).toEqual([24, 0]);

    // i = (1, 2, 3): 1 + 2 · (2 + 3 · 3)
    expect(grid.cellAt(15, 25, 35)).toBe(23);
    applier.apply(
      frame({
        tick: 1,
        write: (w) => {
          writeAggregateBlock(w, cube.grids[0]!, false, [{ cell: 23, counts: [4, 5] }]);
        },
      }),
    );
    expect([grid.count(23, 0), grid.count(23, 1), grid.counts.length]).toEqual([4, 5, 48]);
    expect(Array.from(grid.changed.subarray(0, grid.changedCount))).toEqual([23]);
  });

  it('counts inconsistencies instead of throwing: unknown netIds, and an enter beyond maxNetId', () => {
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN, maxNetId: 1000 });
    applier.apply(beacons(1, [1001], [43], [{ netId: 42, groupMask: 1, values: { strength: 1, drift: 1 } }]));
    expect(applier.world.anomalies).toBe(3);
    expect(applier.world.entityCount).toBe(0);
  });

  it('resets the clock on a RESET frame that goes back in time, so render time follows the restarted server', () => {
    const clock = new Clock({ tickPeriodMs: 50 });
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN, clock });
    let now = 0;
    for (let tick = 100_000; tick < 100_200; tick++, now += 50) {
      applier.apply(frame({ tick, write: () => {} }), now);
      clock.update(now);
    }

    expect(clock.renderTick).toBeGreaterThan(100_000);
    applier.apply(frame({ tick: 5, flags: TickFlags.Reset, write: () => {} }), now);
    clock.update(now);
    // 20 s of the restarted server's frames.
    let tick = 6;
    for (const end = now + 20_000; now < end; tick++) {
      now += 50;
      applier.apply(frame({ tick, write: () => {} }), now);
      clock.update(now);
    }

    expect(clock.latestTick).toBe(tick - 1);
    expect(clock.renderTick).toBeLessThanOrEqual(tick);
    expect(clock.renderTick).toBeGreaterThan(tick - 20);
  });

  it('reports PERIOD to the clock as the duration of the interval that just elapsed', () => {
    const clock = new Clock({ tickPeriodMs: 50 });
    const applier = new FrameApplier(plan, { initialRealm: KITCHEN, clock });
    applier.apply(beacons(10, [1]), 1000);
    expect(clock.latestTick).toBe(10);
    applier.apply(frame({ tick: 11, periodUs: 150_000, write: () => {} }), 1150);
    expect(applier.periodUs).toBe(150_000);
    expect(clock.tickPeriodMs).toBe(150);
    expect(clock.latestTick).toBe(11);
  });

  it('applies tick-limits as its decode log and § 10 prescribe: leaves last, to a holder of their archetype', () => {
    const wide = CatalogPlan.compile(parseCatalog(goldenBin('catalog-wide')));
    const vector = goldenJson('tick-limits') as { log: Record<string, unknown>[] };
    const events: string[] = [];
    const applier = new FrameApplier(wide, { initialRealm: KITCHEN, onEvent: (event) => events.push(event.type.name) });
    applier.apply(goldenBin('tick-limits'));

    // The expected world, replayed from the decode log: an enter replaces (an anomaly when the netId is held or beyond
    // the default 2^22 map), a segment or state record for a netId the archetype does not hold is an anomaly, and the
    // leaves apply last.
    const held = new Map<number, { archetype: string; mask: number }>();
    const leaves: { netId: number; archetype: string }[] = [];
    const expectedEvents: string[] = [];
    let anomalies = 0;
    let archetype = '';
    for (const call of vector.log) {
      const netId = call.netId as number;
      switch (call.call) {
        case 'beginEntities':
          archetype = call.archetype as string;
          break;
        case 'enter':
          if (netId === 0 || netId > applier.world.maxNetId) {
            anomalies++;
          } else {
            anomalies += held.has(netId) ? 1 : 0;
            held.set(netId, { archetype, mask: 0 });
          }

          break;
        case 'segment':
        case 'state': {
          const entity = held.get(netId);
          if (entity?.archetype !== archetype) {
            anomalies++;
          } else if (call.call === 'state') {
            entity.mask |= call.groupMask as number;
          }

          break;
        }
        case 'leave':
          leaves.push({ netId, archetype });
          break;
        case 'event':
          expectedEvents.push(call.type as string);
          break;
      }
    }

    for (const leave of leaves) {
      if (held.get(leave.netId)?.archetype === leave.archetype) {
        held.delete(leave.netId);
      } else {
        anomalies++;
      }
    }

    expect(leaves.length).toBeGreaterThan(0);
    // The count the C# store asserts too: five leaves for netIds nobody holds, 2^32 − 1 among them.
    expect(anomalies).toBe(5);
    expect(archetypeOf(applier.world.locate(2))).toBe(wide.archetypeByName('A127')!.idx);
    expect(applier.world.archetypeStore(wide.archetypeByName('A254')!.idx).liveCount).toBe(1);
    expect(events).toEqual(expectedEvents);
    expect(applier.world.anomalies).toBe(anomalies);
    expect(applier.world.entityCount).toBe(held.size);
    for (const [netId, entity] of held) {
      const location = applier.world.locate(netId);
      const store = applier.world.archetypeStore(archetypeOf(location));
      expect(store.schema.name).toBe(entity.archetype);
      expect(store.updateMask[slotOf(location)]).toBe(entity.mask);
    }
  });
});

describe.each([
  { hz: 20, periodUs: 50_000 },
  { hz: 60, periodUs: 16_667 },
])('the motion ring at $hz Hz', ({ periodUs }) => {
  const rated = CatalogPlan.compile({ ...plan.catalog, tick: { ...plan.catalog.tick, periodUs } });
  const delay = Math.ceil((DEFAULT_MAX_RENDER_DELAY_MS * 1000) / periodUs);
  const first = 1000;
  const last = first + 3 * delay;

  /** Frames `first..last`: a Buoy (`none`) sampled at (k, −k/2), and a Drone (`linear`) at (k, 0, 0) moving 0.5 a tick. */
  function run(): FrameApplier {
    const applier = new FrameApplier(rated, { initialRealm: KITCHEN });
    for (let tick = first; tick <= last; tick++) {
      const k = tick - first;
      const sample = { netId: 1, position: [k, -k / 2], t0: tick, epoch: 0 };
      const segment = { netId: 2, position: [k, 0, 0], velocity: [0.5, 0, 0], t0: tick, epoch: 0 };
      applier.apply(
        frame({
          tick,
          write: (w) => {
            if (tick === first) {
              writeEntitiesBlock(w, tick, buoy, [{ ...sample, values: { depth: 1, reading: 2 } }], [], [], [], KITCHEN);
              writeEntitiesBlock(w, tick, drone, [{ ...segment, values: droneValues(2) }], [], [], [], KITCHEN);
            } else {
              writeEntitiesBlock(w, tick, buoy, [], [sample], [], [], KITCHEN);
              writeEntitiesBlock(w, tick, drone, [], [segment], [], [], KITCHEN);
            }
          },
        }),
      );
    }

    return applier;
  }

  it('holds enough segments to span the largest render delay, from the catalog tick period', () => {
    const applier = run();
    const store = applier.world.archetypeStore(buoy.idx);
    expect(store.segmentHistory).toBe(segmentHistoryFor(periodUs));
    expect(store.segmentHistory).toBe(delay + 2);
    expect(applier.world.archetypeStore(drone.idx).segmentHistory).toBe(delay + 2);
    expect(applier.world.anomalies).toBe(0);
  });

  it('sizes the ring from its clock’s largest delay, unless told otherwise', () => {
    const clock = new Clock({ tickPeriodMs: periodUs / 1000, maxDelayMs: 600 });
    expect(clock.maxDelayMs).toBe(600);
    const history = (ms: number) => Math.min(255, Math.ceil((ms * 1000) / periodUs) + 2);
    expect(
      new FrameApplier(rated, { initialRealm: KITCHEN, clock }).world.archetypeStore(buoy.idx).segmentHistory,
    ).toBe(history(600));
    const explicit = new FrameApplier(rated, { initialRealm: KITCHEN, clock, maxRenderDelayMs: 400 });
    expect(explicit.world.archetypeStore(buoy.idx).segmentHistory).toBe(history(400));
  });

  it('sizes the ring from a longer render delay when the applier is given one', () => {
    const applier = new FrameApplier(rated, { initialRealm: KITCHEN, maxRenderDelayMs: 500 });
    const history = Math.ceil((500 * 1000) / periodUs) + 2;
    expect(applier.world.archetypeStore(buoy.idx).segmentHistory).toBe(Math.min(255, history));
  });

  it('interpolates `none` samples at the largest render delay', () => {
    const applier = run();
    const store = applier.world.archetypeStore(buoy.idx);
    const out = new Float64Array(MAX_MOTION_STRIDE);
    const k = last - delay - first;
    evaluateSlot(store, slot(applier, 1), last - delay, 0.5, out, 0);
    expect(Array.from(out.subarray(0, 4))).toEqual([k + 0.5, -(k + 0.5) / 2, 1, -0.5]);
  });

  it('extrapolates the `linear` segment in force at the largest render delay', () => {
    const applier = run();
    const store = applier.world.archetypeStore(drone.idx);
    const out = new Float64Array(MAX_MOTION_STRIDE);
    const k = last - delay - first;
    evaluateSlot(store, slot(applier, 2), last - delay, 0.5, out, 0);
    expect(Array.from(out)).toEqual([k + 0.25, 0, 0, 0.5, 0, 0]);
  });
});
