import { describe, expect, it } from 'vitest';
import { decodeChunkBinary } from '@/libs/profiler/decode/chunkDecoder';

/**
 * Wire-format end-to-end test for the optional source-location-id field (issue #293, Phase 1+3).
 * Constructs a tiny synthetic chunk holding a single span record with the source-location flag
 * set, then asserts the decoder's output carries the right `sourceLocationId`.
 *
 * The chunk shape mirrors what `TraceFileWriter` produces server-side: one record beginning with
 * a u16 size, then the common header (12 B) + span extension (25 B) + optional 16 B trace context
 * + optional 2 B source-location id + kind-specific payload. We use BTreeInsert (kind=12, no
 * payload) so the record is just the header pieces.
 */
describe('chunkDecoder — source-location id (issue #293)', () => {
  it('decodes a span carrying SpanFlags bit 1 + 2-byte siteId', () => {
    const SPAN_FLAGS_HAS_SOURCE_LOCATION = 0x02;
    const KIND_BTREE_INSERT = 40; // TraceEventKind.BTreeInsert
    const recordSize = 12 + 25 + 2; // common + span ext + source-loc bytes (no trace ctx)
    const buf = new ArrayBuffer(recordSize + 1); // +1 trailing byte to ensure we don't over-read
    const view = new DataView(buf);
    let off = 0;

    // Common header: u16 size, u8 kind, u8 threadSlot, i64 startTs
    view.setUint16(off, recordSize, true); off += 2;
    view.setUint8(off, KIND_BTREE_INSERT); off += 1;
    view.setUint8(off, 7); off += 1; // threadSlot
    view.setBigInt64(off, 1000n, true); off += 8;

    // Span extension: i64 durationTicks, u64 spanId, u64 parentSpanId, u8 spanFlags
    view.setBigInt64(off, 500n, true); off += 8;
    view.setBigUint64(off, 0xAAAA_BBBB_CCCC_DDDDn, true); off += 8;
    view.setBigUint64(off, 0n, true); off += 8;
    view.setUint8(off, SPAN_FLAGS_HAS_SOURCE_LOCATION); off += 1;

    // Optional source-location id (u16 LE)
    view.setUint16(off, 0xABCD, true); off += 2;

    // Decode one tick's worth of records.
    // ticksPerUs = 10 (i.e. 10 MHz timestamp); the test asserts on integer fields only.
    const events = decodeChunkBinary(
      new Uint8Array(buf, 0, recordSize),
      /* firstTick */ 1,
      /* ticksPerUs */ 10,
      /* isContinuation */ true,
    );

    expect(events.length).toBeGreaterThanOrEqual(1);
    const span = events.find((e) => e.kind === KIND_BTREE_INSERT);
    expect(span).toBeDefined();
    expect(span!.sourceLocationId).toBe(0xABCD);
    // Don't compare the full spanId decimal — the focus of this test is the source-location field.
    expect(span!.spanId).toMatch(/^\d+$/);
  });

  it('omits sourceLocationId when the flag is not set', () => {
    const KIND_BTREE_INSERT = 40;
    const recordSize = 12 + 25; // no trace ctx, no source-loc bytes
    const buf = new ArrayBuffer(recordSize);
    const view = new DataView(buf);
    let off = 0;

    view.setUint16(off, recordSize, true); off += 2;
    view.setUint8(off, KIND_BTREE_INSERT); off += 1;
    view.setUint8(off, 0); off += 1;
    view.setBigInt64(off, 0n, true); off += 8;
    view.setBigInt64(off, 100n, true); off += 8;
    view.setBigUint64(off, 1n, true); off += 8;
    view.setBigUint64(off, 0n, true); off += 8;
    view.setUint8(off, 0); off += 1; // SpanFlags = 0 (neither bit set)

    const events = decodeChunkBinary(
      new Uint8Array(buf),
      /* firstTick */ 1,
      /* ticksPerUs */ 10,
      /* isContinuation */ true,
    );
    const span = events.find((e) => e.kind === KIND_BTREE_INSERT);
    expect(span).toBeDefined();
    expect(span!.sourceLocationId).toBeUndefined();
  });
});
