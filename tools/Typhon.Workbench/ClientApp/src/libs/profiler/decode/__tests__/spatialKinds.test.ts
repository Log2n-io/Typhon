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

  it('kind 66 SpatialArchetypeTelemetry: a pre-#941 record still decodes, and its appended fields read zero', () => {
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

    // #944. This record stops at 58 payload bytes — exactly what the engine wrote before #941 appended the controller
    // fields. An instant grows by appending, so a short record is a strict PREFIX of a long one and its missing fields
    // must decode as zero. The alternative is what makes this worth a test: reading past the record into whatever
    // follows it, which yields plausible numbers rather than an error.
    expect(e.queryClustersOpened).toBe(0);
    expect(e.candidatesPerHitSmoothed).toBe(0);
    expect(e.controllerFlags).toBe(0);
    expect(e.driftTargetBoost).toBe(0);
  });

  it('kind 66 SpatialArchetypeTelemetry: the seventeen fields #941 appended, in the layout the producer declares', () => {
    // Transcribed from SpatialArchetypeTelemetryEvent's [BeginParam] order. The generator packs with NO alignment
    // padding — a u8 at 106 is followed by an i32 at 107 — so every offset after ControllerFlags is odd, which is
    // exactly the kind of layout a hand-transcribed decoder gets wrong by assuming alignment.
    const bytes = record(66, false, (v, o) => {
      v.setUint16(o, 9, true);                      // archetypeId
      v.setInt32(o + 2, 1234, true);                // activeClusters
      v.setInt32(o + 6, 56, true);                  // migrations
      v.setFloat32(o + 10, 4.5, true);              // migrationCpuMs
      v.setInt32(o + 14, 7, true);                  // hysteresisAbsorbed
      v.setInt32(o + 18, 88, true);                 // driftersDetected
      v.setInt32(o + 22, 2, true);                  // repairUnits
      v.setInt32(o + 26, 3, true);                  // repairUnitsRefused
      v.setInt32(o + 30, 17, true);                 // repairQueueDepth
      v.setFloat32(o + 34, 1.25, true);             // budgetUsedMs
      v.setInt32(o + 38, 640, true);                // tightnessSamples
      v.setFloat32(o + 42, 0.9, true);              // extentRatio
      v.setFloat32(o + 46, 0.5, true);              // packingBound
      v.setInt32(o + 50, 1, true);                  // cellTreePromotions
      v.setInt32(o + 54, 4, true);                  // cellTreeDemotions
      v.setBigInt64(o + 58, 9_001n, true);          // queryClustersOpened
      v.setBigInt64(o + 66, 400_000n, true);        // queryCandidates
      v.setBigInt64(o + 74, 200_000n, true);        // queryHits
      v.setFloat32(o + 82, 8.0, true);              // budgetConfiguredMs
      v.setFloat32(o + 86, 2.0, true);              // budgetGrantedMs
      v.setFloat32(o + 90, 0.25, true);             // efficiencyTolerance
      v.setFloat32(o + 94, 2.5, true);              // candidatesPerHitSmoothed
      v.setFloat32(o + 98, 2.0, true);              // candidatesPerHitBest
      v.setInt32(o + 102, 13, true);                // ticksAtWholeBudget
      v.setUint8(o + 106, 0x03);                    // controllerFlags — both bits
      v.setInt32(o + 107, 6, true);                 // efficiencyRebases
      v.setInt32(o + 111, 21, true);                // repairCellsCooling
      v.setInt32(o + 115, 2, true);                 // repairValveFires
      v.setInt32(o + 119, 512, true);               // repairedEntities
      v.setBigInt64(o + 123, 77n, true);            // repairQueueEvicted
      v.setFloat32(o + 131, 145.5, true);           // measuredNsPerEntity
      v.setFloat32(o + 135, 1.5, true);             // driftTargetBoost
      v.setInt32(o + 139, 1188, true);              // presentRealms  (#WB-05)
      v.setInt32(o + 143, 3, true);                 // runnableRealms
      return o + 147;
    });

    const e = decodeOne(bytes);
    // The prefix must still be right: an offset slip in the appended block is a bug, but one in the prefix would be a
    // regression in what already worked.
    expect(e.cellTreeDemotions).toBe(4);
    expect(e.queryClustersOpened).toBe(9_001);
    expect(e.queryCandidates).toBe(400_000);
    expect(e.queryHits).toBe(200_000);
    expect(e.budgetConfiguredMs).toBeCloseTo(8.0, 5);
    expect(e.budgetGrantedMs).toBeCloseTo(2.0, 5);
    expect(e.efficiencyTolerance).toBeCloseTo(0.25, 5);
    expect(e.candidatesPerHitSmoothed).toBeCloseTo(2.5, 5);
    expect(e.candidatesPerHitBest).toBeCloseTo(2.0, 5);
    expect(e.ticksAtWholeBudget).toBe(13);
    expect(e.controllerFlags).toBe(0x03);
    expect(e.efficiencyRebases).toBe(6);
    expect(e.repairCellsCooling).toBe(21);
    expect(e.repairValveFires).toBe(2);
    expect(e.repairedEntities).toBe(512);
    expect(e.repairQueueEvicted).toBe(77);
    expect(e.measuredNsPerEntity).toBeCloseTo(145.5, 4);
    expect(e.driftTargetBoost).toBeCloseTo(1.5, 5);
    expect(e.presentRealms).toBe(1188);
    expect(e.runnableRealms).toBe(3);
  });

  it('kind 66: a record that stops MID-append zero-fills from that point, rather than reading past its end', () => {
    // The case a length check alone would miss: the record reaches some appended fields and not others. 82 payload
    // bytes is the three i64 query counters and nothing after them.
    const bytes = record(66, false, (v, o) => {
      v.setUint16(o, 9, true);
      v.setBigInt64(o + 58, 5n, true);
      v.setBigInt64(o + 66, 6n, true);
      v.setBigInt64(o + 74, 7n, true);
      return o + 82;
    });

    const e = decodeOne(bytes);
    expect(e.queryClustersOpened).toBe(5);
    expect(e.queryCandidates).toBe(6);
    expect(e.queryHits).toBe(7);
    expect(e.budgetConfiguredMs).toBe(0);
    expect(e.controllerFlags).toBe(0);
    expect(e.driftTargetBoost).toBe(0);
  });

  it('kind 67 SpatialRealmTelemetry: two u16s then two u8s, so every field after them is misaligned on purpose', () => {
    // The layout a decoder gets wrong by assuming a float starts on a 4-byte boundary: cellSize sits at payload+6.
    const bytes = record(67, false, (v, o) => {
      v.setUint16(o, 1188, true);         // realmId
      v.setUint16(o + 2, 7, true);        // archetypeId
      v.setUint8(o + 4, 1);               // runState — Simulated
      v.setUint8(o + 5, 4);               // divisor
      v.setFloat32(o + 6, 64, true);      // cellSize
      v.setInt32(o + 10, 256, true);      // cellCount
      v.setInt32(o + 14, 1, true);        // gridDepth
      v.setInt32(o + 18, 31, true);       // clusters
      v.setFloat32(o + 22, 180.9, true);  // clusterReach
      v.setInt32(o + 26, 3, true);        // escapedClusters
      v.setInt32(o + 30, 2, true);        // promotedCells
      v.setInt32(o + 34, 4, true);        // blockedCells
      v.setFloat32(o + 38, 8, true);      // budgetConfiguredMs
      v.setFloat32(o + 42, 0.25, true);   // efficiencyTolerance
      return o + 46;
    });

    const e = decodeOne(bytes);
    expect(e.kind).toBe(TraceEventKind.SpatialRealmTelemetry);
    expect(e.realmId).toBe(1188);
    expect(e.archetypeId).toBe(7);
    expect(e.runState).toBe(1);
    expect(e.divisor).toBe(4);
    expect(e.cellSize).toBeCloseTo(64, 5);
    expect(e.cellCount).toBe(256);
    expect(e.gridDepth).toBe(1);
    expect(e.clusters).toBe(31);
    expect(e.clusterReach).toBeCloseTo(180.9, 4);
    expect(e.escapedClusters).toBe(3);
    expect(e.promotedCells).toBe(2);
    expect(e.blockedCells).toBe(4);
    expect(e.budgetConfiguredMs).toBeCloseTo(8, 5);
    expect(e.efficiencyTolerance).toBeCloseTo(0.25, 5);
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

// #WB-02 — the same silent-drift guard for push replication's two operator kinds. Both layouts put wide fields at
// offsets that are not naturally aligned, which is exactly where a hand-written table goes wrong: the server record has
// i64s at 16 and 32, and the session row has an f32 at 10 and an i64 at 14.
describe('#WB-02 subscriptions trace kinds — wire layout', () => {
  it('kind 68 SubscriptionsServerTelemetry: 9 fields, i64s at 16 and 32', () => {
    const bytes = record(68, false, (v, o) => {
      v.setInt32(o, 2048, true);            // sessions
      v.setFloat32(o + 4, 1_250_000, true); // netOutBytesPerSec
      v.setFloat32(o + 8, 0.75, true);      // trackP99Ms
      v.setFloat32(o + 12, 3.5, true);      // durabilityWaitP99Ms
      v.setBigInt64(o + 16, 91_233n, true); // framesSkipped
      v.setInt32(o + 24, 40, true);         // framePoolRented
      v.setInt32(o + 28, 64, true);         // framePoolBlocks
      v.setBigInt64(o + 32, 7n, true);      // framePoolBudgetSkips
      v.setInt32(o + 40, 64, true);         // reportedSessions
      return o + 44;
    });

    const e = decodeOne(bytes);
    expect(e.kind).toBe(TraceEventKind.SubscriptionsServerTelemetry);
    expect(e.sessions).toBe(2048);
    expect(e.netOutBytesPerSec).toBe(1_250_000);
    expect(e.trackP99Ms).toBe(0.75);
    expect(e.durabilityWaitP99Ms).toBe(3.5);
    expect(e.framesSkipped).toBe(91_233);
    expect(e.framePoolRented).toBe(40);
    expect(e.framePoolBlocks).toBe(64);
    expect(e.framePoolBudgetSkips).toBe(7);
    // Below `sessions` on purpose: the rows are capped at 64, and a panel has to say so rather than present the rows it
    // received as the whole population.
    expect(e.reportedSessions).toBe(64);
    expect(e.reportedSessions!).toBeLessThan(e.sessions!);
  });

  it('kind 69 SubscriptionsSessionTelemetry: the u64 id keeps its generation, and 0xFFFF realm is not realm 0', () => {
    const bytes = record(69, false, (v, o) => {
      // slot 2, generation 7 — the engine packs slot | generation << 16, so dropping the high word would make two
      // sessions that reused one slot indistinguishable.
      v.setBigUint64(o, BigInt(2 | (7 << 16)), true); // sessionId
      v.setUint16(o + 8, 0xffff, true);               // realmId — not yet told its realm
      v.setFloat32(o + 10, 48_000, true);             // bytesPerSec
      v.setBigInt64(o + 14, 12n, true);               // framesSkipped
      v.setInt32(o + 22, 2, true);                    // degradeLevel
      return o + 26;
    });

    const e = decodeOne(bytes);
    expect(e.kind).toBe(TraceEventKind.SubscriptionsSessionTelemetry);
    expect(e.sessionId).toBe(2 | (7 << 16));
    expect(e.realmId).toBe(0xffff);
    expect(e.bytesPerSec).toBe(48_000);
    expect(e.framesSkipped).toBe(12);
    expect(e.degradeLevel).toBe(2);
  });
});

// The prefix half of the append-only contract, which the C# side has had since kind 66 and the TS side did not: a record
// written by an older producer is shorter, and every field it does not reach must read zero rather than the following
// record's bytes. For the LAST record in a block an unguarded read walks off the buffer and DataView throws RangeError,
// which `decodeChunkBinary` does not catch — the chunk is then marked failed for 30 s and renders as a gap.
describe('append-only prefixes decode without reading past the record', () => {
  it('kind 68: a record that stops after durabilityWaitP99Ms reads the rest as zero', () => {
    const bytes = record(68, false, (v, o) => {
      v.setInt32(o, 7, true);
      v.setFloat32(o + 4, 1_000, true);
      v.setFloat32(o + 8, 0.5, true);
      v.setFloat32(o + 12, 2.5, true);
      return o + 16; // a pre-append producer: 16 bytes of payload, not 44
    });

    const e = decodeOne(bytes);
    expect(e.sessions).toBe(7);
    expect(e.durabilityWaitP99Ms).toBe(2.5);
    expect(e.framesSkipped).toBe(0);
    expect(e.framePoolBudgetSkips).toBe(0);
    expect(e.reportedSessions).toBe(0);
  });

  it('kind 69: a truncated row keeps the realm SENTINEL rather than claiming realm 0', () => {
    const bytes = record(69, false, (v, o) => {
      v.setBigUint64(o, 42n, true);
      return o + 8; // stops before realmId
    });

    const e = decodeOne(bytes);
    expect(e.sessionId).toBe(42);
    // Zero here would assert the session is in realm 0, which is a different claim from "we were not told".
    expect(e.realmId).toBe(0xffff);
    expect(e.bytesPerSec).toBe(0);
    expect(e.degradeLevel).toBe(0);
  });

  it('a truncated record as the LAST in its block does not throw', () => {
    // The unguarded version threw RangeError here, which decodeChunkBinary does not catch.
    const bytes = record(68, false, (v, o) => {
      v.setInt32(o, 1, true);
      return o + 4;
    });
    expect(() => decodeOne(bytes)).not.toThrow();
  });
});
