import { describe, expect, it } from 'vitest';
import { decodeChunkBinary } from '@/libs/profiler/decode/chunkDecoder';
import { TraceEventKind } from '@/libs/profiler/model/types';

/**
 * Wire-format test for kind-254 (ThreadContextSwitch). One ON-CPU slice per record: 12-byte common
 * header + 13-byte payload, no span extension. Mirrors `ThreadSchedulingEvents.cs`:
 *   u8 targetSlotIdx, u8 processorNumber, u8 waitReason, u8 threadState, u8 gettingIdle,
 *   u32 durationQpc @+5, u32 readyTimeQpc @+9.
 *
 * Built with `isContinuation=true` so no leading TickStart is needed — the record stands alone.
 */

const KIND_CSWITCH = 254;

interface CSwitchFields {
  threadSlot: number;
  startTs: bigint;
  targetSlotIdx: number;
  processorNumber: number;
  waitReason: number;
  threadState: number;
  gettingIdle: number;
  durationQpc: number;
  readyTimeQpc: number;
}

function buildCSwitchRecord(f: CSwitchFields): Uint8Array {
  const recordSize = 12 + 13;
  const buf = new ArrayBuffer(recordSize);
  const view = new DataView(buf);
  let off = 0;
  view.setUint16(off, recordSize, true); off += 2;
  view.setUint8(off, KIND_CSWITCH); off += 1;
  view.setUint8(off, f.threadSlot); off += 1;
  view.setBigInt64(off, f.startTs, true); off += 8;
  view.setUint8(off, f.targetSlotIdx); off += 1;
  view.setUint8(off, f.processorNumber); off += 1;
  view.setUint8(off, f.waitReason); off += 1;
  view.setUint8(off, f.threadState); off += 1;
  view.setUint8(off, f.gettingIdle); off += 1;
  view.setUint32(off, f.durationQpc, true); off += 4;
  view.setUint32(off, f.readyTimeQpc, true); off += 4;
  return new Uint8Array(buf);
}

describe('chunkDecoder — ThreadContextSwitch (kind 254)', () => {
  it('decodes the 13-byte payload into a TraceEvent', () => {
    const bytes = buildCSwitchRecord({
      threadSlot: 3,
      startTs: 10_000n,
      targetSlotIdx: 7,
      processorNumber: 11,
      waitReason: 30,
      threadState: 5,
      gettingIdle: 0,
      durationQpc: 4_000,
      readyTimeQpc: 250,
    });
    // ticksPerUs = 1 ⇒ QPC ticks == µs.
    const events = decodeChunkBinary(bytes, 1, 1, true);

    expect(events).toHaveLength(1);
    const e = events[0];
    expect(e.kind).toBe(TraceEventKind.ThreadContextSwitch);
    expect(e.threadSlot).toBe(3);
    expect(e.timestampUs).toBe(10_000);
    expect(e.targetSlotIdx).toBe(7);
    expect(e.processorNumber).toBe(11);
    expect(e.waitReason).toBe(30);
    expect(e.threadState).toBe(5);
    expect(e.gettingIdle).toBe(false);
    expect(e.durationUs).toBe(4_000);
    expect(e.readyTimeUs).toBe(250);
  });

  it('decodes gettingIdle=1 as boolean true', () => {
    const bytes = buildCSwitchRecord({
      threadSlot: 0, startTs: 0n, targetSlotIdx: 1, processorNumber: 0,
      waitReason: 0, threadState: 0, gettingIdle: 1, durationQpc: 100, readyTimeQpc: 0,
    });
    const events = decodeChunkBinary(bytes, 1, 1, true);
    expect(events[0].gettingIdle).toBe(true);
  });

  it('scales durationQpc / readyTimeQpc by ticksPerUs', () => {
    const bytes = buildCSwitchRecord({
      threadSlot: 0, startTs: 0n, targetSlotIdx: 2, processorNumber: 0,
      waitReason: 0, threadState: 0, gettingIdle: 0, durationQpc: 6_000, readyTimeQpc: 600,
    });
    // ticksPerUs = 3 ⇒ divide raw QPC by 3.
    const events = decodeChunkBinary(bytes, 1, 3, true);
    expect(events[0].durationUs).toBe(2_000);
    expect(events[0].readyTimeUs).toBe(200);
  });

  it('walks multiple kind-254 records without misalignment', () => {
    const a = buildCSwitchRecord({
      threadSlot: 0, startTs: 100n, targetSlotIdx: 4, processorNumber: 1,
      waitReason: 7, threadState: 2, gettingIdle: 0, durationQpc: 50, readyTimeQpc: 10,
    });
    const b = buildCSwitchRecord({
      threadSlot: 0, startTs: 200n, targetSlotIdx: 5, processorNumber: 2,
      waitReason: 32, threadState: 3, gettingIdle: 0, durationQpc: 80, readyTimeQpc: 20,
    });
    const chunk = new Uint8Array(a.length + b.length);
    chunk.set(a, 0);
    chunk.set(b, a.length);
    const events = decodeChunkBinary(chunk, 1, 1, true);

    expect(events).toHaveLength(2);
    expect(events[0].targetSlotIdx).toBe(4);
    expect(events[0].timestampUs).toBe(100);
    expect(events[1].targetSlotIdx).toBe(5);
    expect(events[1].timestampUs).toBe(200);
    expect(events[1].waitReason).toBe(32);
  });
});
