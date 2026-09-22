import { describe, expect, it } from 'vitest';
import { decodeChunkBinary } from '../chunkDecoder';
import { TraceEventKind } from '@/libs/profiler/model/types';

// #911 — the wire layouts for kinds 64, 65 and 66 are hand-written offset tables in `chunkDecoder.ts`, transcribed from
// the producers' `[BeginParam]` / `[Optional]` field order in `ClusterMigrationEvent.cs`. Nothing in the build ties the
// two together, so a field inserted on the engine side shifts every offset after it and the decoder keeps returning
// plausible numbers — the same silent-drift class `isInstantKind.test.ts` guards for the span/instant discriminator.
//
// These tests encode a record BYTE BY BYTE against the layout the enum documents, then assert the decoder recovers the
// exact field values. A transposed pair or a missing field fails here rather than in someone's panel.

const COMMON_HEADER_SIZE = 12;
const SPAN_HEADER_EXT = 25;

/** Builds one record: common header, optional span-header extension, then the caller's payload. */
function record(kind: number, isSpan: boolean, payload: (view: DataView, o: number) => number): Uint8Array {
  const headerSize = COMMON_HEADER_SIZE + (isSpan ? SPAN_HEADER_EXT : 0);
  const buf = new ArrayBuffer(256);
  const view = new DataView(buf);
  const size = payload(view, headerSize);

  view.setUint16(0, size, true);        // record size
  view.setUint8(2, kind);
  view.setUint8(3, 0);                  // thread slot
  view.setBigUint64(4, 1000n, true);    // timestamp ticks
  if (isSpan) {
    view.setBigUint64(COMMON_HEADER_SIZE, 500n, true);       // duration
    view.setBigUint64(COMMON_HEADER_SIZE + 8, 7n, true);     // spanId
    view.setBigUint64(COMMON_HEADER_SIZE + 16, 0n, true);    // parentSpanId
    view.setUint8(COMMON_HEADER_SIZE + 24, 0);               // span flags
  }
  return new Uint8Array(buf, 0, size);
}

function decodeOne(bytes: Uint8Array) {
  const events = decodeChunkBinary(bytes, 0, 1, true);
  expect(events, 'exactly one record was written, so exactly one event must come back').toHaveLength(1);
  return events[0];
}

describe('#911 spatial trace kinds — wire layout', () => {
  it('kind 64 SpatialRepairUnit: 4 required fields then a 3-entry optional-mask block', () => {
    const bytes = record(64, true, (v, o) => {
      v.setUint16(o, 5, true);          // archetypeId
      v.setInt32(o + 2, 4242, true);    // cellKey
      v.setInt32(o + 6, 8, true);       // clusterCount
      v.setInt32(o + 10, 501, true);    // entityCount
      v.setUint8(o + 14, 0x07);         // mask: degradation | valveFired | movedCount
      v.setFloat32(o + 15, 0.75, true); // degradation
      v.setUint8(o + 19, 1);            // valveFired
      v.setInt32(o + 20, 499, true);    // movedCount
      return o + 24;
    });

    const e = decodeOne(bytes);
    expect(e.archetypeId).toBe(5);
    expect(e.cellKey).toBe(4242);
    expect(e.clusterCount).toBe(8);
    expect(e.entityCount).toBe(501);
    expect(e.degradation).toBeCloseTo(0.75, 6);
    expect(e.valveFired).toBe(1);
    expect(e.movedCount).toBe(499);
  });

  it('kind 64: an absent optional block leaves the three fields undefined rather than reading past the record', () => {
    const bytes = record(64, true, (v, o) => {
      v.setUint16(o, 5, true);
      v.setInt32(o + 2, 1, true);
      v.setInt32(o + 6, 2, true);
      v.setInt32(o + 10, 3, true);
      return o + 14;   // no mask byte
    });

    const e = decodeOne(bytes);
    expect(e.entityCount).toBe(3);
    expect(e.degradation).toBeUndefined();
    expect(e.movedCount).toBeUndefined();
  });

  it('kind 65 SpatialRelocationOutcome: nine required i32/u16 fields, no mask', () => {
    const bytes = record(65, false, (v, o) => {
      v.setUint16(o, 2, true);
      const vals = [11, 22, 33, 44, 55, 66, 77, 88];
      vals.forEach((n, i) => v.setInt32(o + 2 + i * 4, n, true));
      return o + 34;
    });

    const e = decodeOne(bytes);
    expect(e.kind).toBe(TraceEventKind.SpatialRelocationOutcome);
    expect(e.archetypeId).toBe(2);
    expect(e.relocationsAdmitted).toBe(11);
    expect(e.relocationsThrottled).toBe(22);
    expect(e.relocationsSuperseded).toBe(33);
    expect(e.driftersUnplaced).toBe(44);
    expect(e.driftersUnplacedNoCandidate).toBe(55);
    expect(e.driftersSpilled).toBe(66);
    expect(e.pinsRejected).toBe(77);
    expect(e.crossingsQueued).toBe(88);
  });

  it('kind 66 SpatialArchetypeTelemetry: fifteen required fields, floats where the producer declares floats', () => {
    // The three f32 slots (migrationCpuMs, budgetUsedMs, extentRatio/packingBound) are what an offset slip most easily
    // hides: an i32 read of a float's bytes yields a huge integer that no assertion on a count would catch.
    const bytes = record(66, false, (v, o) => {
      v.setUint16(o, 9, true);            // archetypeId
      v.setInt32(o + 2, 1234, true);      // activeClusters
      v.setInt32(o + 6, 56, true);        // migrations
      v.setFloat32(o + 10, 4.5, true);    // migrationCpuMs
      v.setInt32(o + 14, 7, true);        // hysteresisAbsorbed
      v.setInt32(o + 18, 88, true);       // driftersDetected
      v.setInt32(o + 22, 2, true);        // repairUnits
      v.setInt32(o + 26, 3, true);        // repairUnitsRefused
      v.setInt32(o + 30, 17, true);       // repairQueueDepth
      v.setFloat32(o + 34, 1.25, true);   // budgetUsedMs
      v.setInt32(o + 38, 640, true);      // tightnessSamples
      v.setFloat32(o + 42, 0.9, true);    // extentRatio
      v.setFloat32(o + 46, 0.5, true);    // packingBound
      v.setInt32(o + 50, 1, true);        // cellTreePromotions
      v.setInt32(o + 54, 4, true);        // cellTreeDemotions
      return o + 58;
    });

    const e = decodeOne(bytes);
    expect(e.kind).toBe(TraceEventKind.SpatialArchetypeTelemetry);
    expect(e.archetypeId).toBe(9);
    expect(e.activeClusters).toBe(1234);
    expect(e.migrationCount).toBe(56);
    expect(e.migrationCpuMs).toBeCloseTo(4.5, 5);
    expect(e.hysteresisAbsorbed).toBe(7);
    expect(e.driftersDetected).toBe(88);
    expect(e.repairUnits).toBe(2);
    expect(e.repairUnitsRefused).toBe(3);
    expect(e.repairQueueDepth).toBe(17);
    expect(e.budgetUsedMs).toBeCloseTo(1.25, 5);
    expect(e.tightnessSamples).toBe(640);
    expect(e.extentRatio).toBeCloseTo(0.9, 5);
    expect(e.packingBound).toBeCloseTo(0.5, 5);
    expect(e.cellTreePromotions).toBe(1);
    expect(e.cellTreeDemotions).toBe(4);
  });

  it('kind 60 ClusterMigration: the #911 optional block is additive — a pre-#911 record still decodes', () => {
    const withKinds = record(60, true, (v, o) => {
      v.setUint16(o, 1, true);
      v.setInt32(o + 2, 30, true);      // migrationCount (the slice length)
      v.setInt32(o + 6, 90, true);      // componentCount
      v.setUint8(o + 10, 0x07);
      v.setInt32(o + 11, 10, true);     // crossingCount
      v.setInt32(o + 15, 15, true);     // relocationCount
      v.setInt32(o + 19, 5, true);      // repairCount
      return o + 23;
    });

    const a = decodeOne(withKinds);
    expect(a.migrationCount).toBe(30);
    expect(a.componentCount).toBe(90);
    // The split is checkable against the span's own denominator, which is what makes it worth carrying.
    expect((a.crossingCount ?? 0) + (a.relocationCount ?? 0) + (a.repairCount ?? 0)).toBe(a.migrationCount);

    const legacy = record(60, true, (v, o) => {
      v.setUint16(o, 1, true);
      v.setInt32(o + 2, 30, true);
      v.setInt32(o + 6, 90, true);
      return o + 10;   // pre-#911: no mask byte at all
    });

    const b = decodeOne(legacy);
    expect(b.migrationCount).toBe(30);
    expect(b.componentCount).toBe(90);
    expect(b.crossingCount).toBeUndefined();
  });
});
