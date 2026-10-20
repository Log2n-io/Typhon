import { describe, expect, it } from 'vitest';
import { decodeChunkBinary } from '../chunkDecoder';
import { TraceEventKind } from '@/libs/profiler/model/types';

// The backup spans (#1416: 44-49) and the archive span (#1418: 74). The bytes below are the SAME goldens the engine's
// TraceEventEncodeEquivalenceTests pins its encoder to (Backup*Event_StructEncode_MatchesLayout): one record each, with a
// trace context, every optional present. Decoding them here ties the hand-written offsets in `decodeBackup` to what the
// engine writes — a field moved on either side fails one of the two fixtures.

const HEADER = '0744443333222211111111000000000000DDDDCCCCBBBBAAAAF0DEBC9A78563412010F0F0F0F0F0F0F0FF0F0F0F0F0F0F0F0';

function bytes(hex: string): Uint8Array {
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) {
    out[i] = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
  }
  return out;
}

function decodeOne(hex: string) {
  const events = decodeChunkBinary(bytes(hex), 0, 1, true);
  expect(events, 'exactly one record was written, so exactly one event must come back').toHaveLength(1);
  return events[0];
}

describe('backup trace kinds — wire layout', () => {
  it('44 BackupCapture: requested kind, then backupId, pages, cbwCopies', () => {
    const e = decodeOne('43002C' + HEADER + '010704030201F401000007000000');
    expect(e.kind).toBe(TraceEventKind.BackupCapture);
    expect([e.requestedKind, e.backupId, e.pages, e.cbwCopies]).toEqual([1, 0x01020304, 500, 7]);
  });

  it('45-47 BackupFreeze, BackupTail, BackupScan: one optional each', () => {
    expect(decodeOne('3A002D' + HEADER + '0187000000').imagePages).toBe(135);
    expect(decodeOne('3E002E' + HEADER + '010807060504030201').pointLsn).toBe('72623859790382856');
    expect(decodeOne('3A002F' + HEADER + '012A000000').pages).toBe(42);
  });

  it('48 BackupVerify: backupId and level, no optional mask', () => {
    const e = decodeOne('3A0030' + HEADER + '0900000001');
    expect([e.backupId, e.level]).toEqual([9, 1]);
  });

  it('49 BackupLifecycle: op, then packs and bytes', () => {
    const e = decodeOne('430031' + HEADER + '0203030000000000010000000000');
    expect([e.op, e.packs, e.bytes]).toEqual([2, 3, 0x10000]);
  });

  it('74 BackupArchiveSegment: segment id, then bytes and outcome; a span', () => {
    const e = decodeOne('47004A' + HEADER + '020000000100000003000002000000000001');
    expect(e.kind).toBe(TraceEventKind.BackupArchiveSegment);
    expect([e.segmentId, e.bytes, e.outcome]).toEqual([0x1_0000_0002, 0x20000, 1]);
    expect(e.durationUs).toBeGreaterThan(0);
  });

  it('74 with no optional: the mask is clear and nothing is read past the record', () => {
    // size 0x3E = 62: header 53 + segment id 8 + mask 1.
    const e = decodeOne('3E004A' + HEADER + '0C0000000000000000');
    expect(e.segmentId).toBe(12);
    expect(e.bytes).toBeUndefined();
    expect(e.outcome).toBeUndefined();
  });
});
