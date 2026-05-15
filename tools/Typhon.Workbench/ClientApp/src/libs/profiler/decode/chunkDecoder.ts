/**
 * Client-side binary decoder for a Typhon trace chunk — originally ported from the retired
 * <c>Typhon.Profiler.Server/RecordDecoder.cs</c>; now mirrors <c>tools/Typhon.Workbench/Sessions/Profiler/RecordDecoder.cs</c>.
 *
 * **Why.** The legacy /api/trace/chunk endpoint decompresses LZ4 chunks on the server, walks the records with RecordDecoder, and emits JSON
 * — round-tripping every record through two string encodings plus the server CPU cost of JSON encode. The new /api/trace/chunk-binary
 * endpoint ships the raw LZ4 bytes straight from the sidecar cache. This module decompresses + decodes them entirely in-browser (inside the
 * Web Worker), so the wire carries compact binary and server CPU stays near zero for chunk serving.
 *
 * **Coverage.** All 31 TraceEventKind values the server supports: 7 instant kinds + 24 span kinds across 10 codec families (SchedulerChunk,
 * 4× BTree, 3× Transaction + Persist, 2× EcsLifecycle + 3× Query + ViewRefresh, 6× PageCache + Backpressure + 3× PageCache async completions,
 * ClusterMigration, 3× WAL, 6× Checkpoint, Statistics, NamedSpan). Unknown kinds fall through to a generic span decoder that surfaces the
 * header-level fields only.
 *
 * **Fidelity target.** Output records are byte-equivalent to the server's JSON path for the same chunk — same fields, same units (µs), same
 * ID formatting (decimal strings for 64-bit IDs, matching <c>RecordDecoder.Id</c> / <c>SignedId</c>). Any divergence would cause different
 * viewer behavior depending on transport — a bug-hunting nightmare we explicitly want to avoid.
 */

import { BinaryReader } from './binaryReader';
import { TraceEventKind, type TraceEvent } from '../model/types';

// ═══════════════════════════════════════════════════════════════════════
// Layout constants — mirror TraceRecordHeader.cs exactly
// ═══════════════════════════════════════════════════════════════════════

/** 12-byte common header shared by every record. Layout: u16 size, u8 kind, u8 threadSlot, i64 startTs. */
const COMMON_HEADER_SIZE = 12;
/** 25-byte span header extension: i64 durationTicks, u64 spanId, u64 parentSpanId, u8 spanFlags. */
const SPAN_HEADER_EXT_SIZE = 25;
/** Optional 16-byte trace-context extension: u64 traceIdHi, u64 traceIdLo. Present iff spanFlags bit 0 set. */
const TRACE_CONTEXT_SIZE = 16;
/** bit 0 of spanFlags: set when the span record carries an OpenTelemetry-style trace context. */
const SPAN_FLAGS_HAS_TRACE_CONTEXT = 0x01;
/** bit 1 of spanFlags: set when the span record carries a 2-byte compile-time source-location id (#302). */
const SPAN_FLAGS_HAS_SOURCE_LOCATION = 0x02;
/** Optional 2-byte source-location id, appended after the trace context (when present). */
const SOURCE_LOCATION_ID_SIZE = 2;

/**
 * Entry point. Decode an LZ4-decompressed record block. <paramref name="firstTick"/> primes the tick counter so events carry the correct
 * tick number; the exact seed value depends on <paramref name="isContinuation"/>. <paramref name="ticksPerUs"/> is
 * <c>timestampFrequency / 1_000_000</c> — arrives via the X-Timestamp-Frequency response header on the binary chunk endpoint.
 *
 * <b>Seeding rules (mirrors the server's RecordDecoder):</b>
 * <ul>
 *   <li><b>Normal chunk</b> (<c>isContinuation=false</c>): seed at <c>firstTick - 1</c>. The first record in the chunk is a
 *       <c>TickStart</c>, which increments the counter to <c>firstTick</c>; subsequent records get the right tick number.</li>
 *   <li><b>Continuation chunk</b> (<c>isContinuation=true</c>, cache v8+ intra-tick splits): seed at <c>firstTick</c> directly.
 *       The chunk has NO leading <c>TickStart</c> — the previous chunk already consumed it. All events before the NEXT (if any)
 *       internal TickStart are tagged with <c>firstTick</c>.</li>
 * </ul>
 *
 * Malformed records (size less than header, size overruns slice, or size exceeds ushort range) stop the walk early — partial results remain
 * returned. Mirrors the server's behavior and keeps the viewer useful on truncated traces.
 */
/**
 * True for kinds whose wire format is the 12-byte common header + per-kind payload only — no 25-byte span-header extension. Must mirror
 * `TraceEventKindExtensions.IsSpan` (negated) in `src/Typhon.Profiler/TraceEventKind.cs`. Kept in this file (rather than `model/types.ts`)
 * because it's purely a wire-decode concern; renderer code never branches on it.
 *
 * If you add a new instant-style kind on the C# side, add it here too. The list of carve-out ranges below is structured the same way as the
 * C# IsSpan body so the two stay in lockstep visually.
 */
function isInstantKind(v: number): boolean {
  if (v < 10) return true;                                                 // pre-#243 instants (TickStart..MemoryAllocEvent)
  if (v === TraceEventKind.PerTickSnapshot || v === TraceEventKind.ThreadInfo) return true; // 76, 77
  if (v >= 90 && v <= 116) return true;                                    // Concurrency tracing (Phase 2, #280)
  // Spatial tracing (Phase 3, #281) — mixed; instants are 127-135, 137, 140-142, 144, 145.
  if ((v >= 127 && v <= 135) || v === 137 || (v >= 140 && v <= 142) || v === 144 || v === 145) return true;
  // Scheduler & Runtime tracing (Phase 4, #282) — mixed; instants:
  //   146-148 (System Start/Completion/QueueWait), 151 (WorkerWake), 153 (Dispense),
  //   154 (DependencyReady), 156-158 (Overload trio), 161-162 (UoWCreate/Flush).
  if ((v >= 146 && v <= 148) || v === 151 || v === 153 || v === 154
      || (v >= 156 && v <= 158) || v === 161 || v === 162) return true;
  if (v >= 166 && v <= 172) return true;                                   // Storage & Memory (Phase 5, #283) — 165 is the only span
  // Data plane (Phase 6, #284) — instants: 176, 178, 180, 182-183, 185-186.
  if (v === 176 || v === 178 || v === 180 || v === 182 || v === 183 || v === 185 || v === 186) return true;
  // Query / ECS (Phase 7, #285) — instants: 191, 197, 200, 202-203, 206-208, 211-213.
  if (v === 191 || v === 197 || v === 200 || v === 202 || v === 203 || v === 206
      || v === 207 || v === 208 || v === 211 || v === 212 || v === 213) return true;
  // Durability (Phase 8, #286) — instants: 217-218, 220, 225, 228, 233-234.
  if (v === 217 || v === 218 || v === 220 || v === 225 || v === 228 || v === 233 || v === 234) return true;
  // Phase 4 follow-up (#289):
  //   241 (SchedulerMetronomeWait) — SPAN, falls through to false below.
  //   242 (SchedulerOverloadDetector) — instant.
  //   243 (RuntimePhaseSpan)        — SPAN, falls through.
  //   244 (QueueTickEnd)            — instant rollup, hand-coded codec, no span-header extension.
  if (v === 242 || v === 244) return true;
  return false;
}

export function decodeChunkBinary(bytes: Uint8Array, firstTick: number, ticksPerUs: number, isContinuation: boolean): TraceEvent[] {
  const reader = new BinaryReader(bytes);
  const events: TraceEvent[] = [];
  // Seed currentTick at firstTick directly. Records in this chunk's [fromTick, toTick) range — which is what the
  // manifest entry SAYS they are — should be tagged with tickNumber within that range, never below firstTick.
  //
  // The pre-#289 OLD behavior was `firstTick - 1` for normal chunks: assume first record is TickStart, let the
  // increment-on-TickStart bump up to firstTick. That broke when the consumer thread's per-block sort by startTs
  // pulled records from worker slots (whose timestamps are slightly earlier than the main thread's TickStart) in
  // FRONT of TickStart in the byte stream — those records got tagged firstTick-1 (= 0 for chunk 1!), polluting tick
  // 0 with worker activity from a real later tick. Symptom: tick 0's TickData.endUs extended past its real
  // pre-tick window into worker territory, breaking visibility math downstream.
  //
  // New behavior: seed at firstTick, suppress the increment for the chunk's FIRST TickStart record (when present).
  // Subsequent TickStart records increment normally (handles multi-tick chunks). Continuation chunks AND tick-0
  // synthetic chunks have no TickStart at the head, so suppression is moot for them — the flag starts as already-
  // consumed.
  let currentTick = firstTick;
  let suppressFirstTickStartIncrement = !isContinuation && firstTick > 0;
  let pos = 0;

  while (pos + COMMON_HEADER_SIZE <= reader.length) {
    const size = reader.readU16(pos);
    if (size < COMMON_HEADER_SIZE || pos + size > reader.length) {
      break;
    }

    const kindByte = reader.readU8(pos + 2);
    const threadSlot = reader.readU8(pos + 3);
    const startTs = reader.readI64AsNumber(pos + 4);
    const timestampUs = startTs / ticksPerUs;

    if (kindByte === TraceEventKind.TickStart) {
      if (suppressFirstTickStartIncrement) {
        suppressFirstTickStartIncrement = false;   // consume the head TickStart without incrementing
      } else {
        currentTick++;
      }
    }

    const kind = kindByte as TraceEventKind;
    let evt: TraceEvent | null;

    // Instant vs span discrimination — must mirror C# `TraceEventKindExtensions.IsSpan` at
    // `TraceEventKind.cs:903` exactly. Numeric kinds ≥ 10 are MOSTLY spans, but #277 (Tracing
    // Instrumentation Expansion) added many instant-shaped kinds in the ≥ 10 range whose wire
    // format omits the 25-byte span header extension. Routing them through readSpanHeader reads
    // 25 bytes of payload as span metadata, producing nonsense durationUs (big end time) and
    // parent/child links (deep stack), even though `pos += size` keeps subsequent records aligned.
    if (isInstantKind(kindByte)) {
      evt = decodeInstant(reader, pos, kind, threadSlot, currentTick, timestampUs, ticksPerUs, size);
    } else {
      evt = decodeSpan(reader, pos, kind, threadSlot, currentTick, timestampUs, ticksPerUs);
    }

    if (evt !== null) {
      events.push(evt);
    }

    pos += size;
  }

  return events;
}

// ═══════════════════════════════════════════════════════════════════════
// Instant decoders — mirror InstantEventCodec.Decode + RecordDecoder.DecodeInstant
// ═══════════════════════════════════════════════════════════════════════

function decodeInstant(
  reader: BinaryReader,
  pos: number,
  kind: TraceEventKind,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
  ticksPerUs: number,
  recordSize: number,
): TraceEvent | null {
  const payloadOffset = pos + COMMON_HEADER_SIZE;

  switch (kind) {
    case TraceEventKind.TickStart:
      return { kind, threadSlot, tickNumber, timestampUs };

    case TraceEventKind.TickEnd:
      return {
        kind, threadSlot, tickNumber, timestampUs,
        overloadLevel: reader.readU8(payloadOffset),
        tickMultiplier: reader.readU8(payloadOffset + 1),
      };

    case TraceEventKind.PhaseStart:
    case TraceEventKind.PhaseEnd:
      return {
        kind, threadSlot, tickNumber, timestampUs,
        phase: reader.readU8(payloadOffset),
      };

    case TraceEventKind.SystemReady:
      // predecessorCount (offset+2, u16) is decoded by server-side InstantEventCodec but intentionally dropped from the JSON DTO — mirror that.
      return {
        kind, threadSlot, tickNumber, timestampUs,
        systemIndex: reader.readU16(payloadOffset),
      };

    case TraceEventKind.SystemSkipped:
      return {
        kind, threadSlot, tickNumber, timestampUs,
        systemIndex: reader.readU16(payloadOffset),
        skipReason: reader.readU8(payloadOffset + 2),
      };

    case TraceEventKind.Instant:
      // Generic instant — i32 nameId + i32 payload in wire. Not surfaced on the server's JSON DTO; mirror that by returning header only.
      return { kind, threadSlot, tickNumber, timestampUs };

    case TraceEventKind.MemoryAllocEvent:
      return decodeMemoryAllocEvent(reader, pos, threadSlot, tickNumber, timestampUs);

    case TraceEventKind.PerTickSnapshot:
      return decodePerTickSnapshot(reader, pos, threadSlot, timestampUs);

    case TraceEventKind.GcStart:
      return decodeGcStart(reader, pos, threadSlot, tickNumber, timestampUs);

    case TraceEventKind.GcEnd:
      return decodeGcEnd(reader, pos, threadSlot, tickNumber, timestampUs, ticksPerUs);

    case TraceEventKind.ThreadInfo:
      return decodeThreadInfo(reader, pos, threadSlot, tickNumber, timestampUs, recordSize);

    case TraceEventKind.RuntimePhaseUoWCreate:
      // UoW allocated at start of tick. Payload: i64 tick at payloadOffset.
      // Surfaced as a glyph in the phase track (see drawPhases).
      return {
        kind, threadSlot, tickNumber, timestampUs,
        // tick value is the UoW's owning tick number; we don't surface it as a separate field on TraceEvent
        // (the wrapper TickData.tickNumber already carries it) — header alone is enough for the marker.
      };

    case TraceEventKind.RuntimePhaseUoWFlush:
      // UoW flushed at end of tick. Payload: i64 tick, i32 changeCount at payloadOffset.
      // Surfaced as a glyph in the phase track (see drawPhases). changeCount is captured for the tooltip.
      return {
        kind, threadSlot, tickNumber, timestampUs,
        changeCount: reader.readI32(payloadOffset + 8),
      };

    default:
      // Phase 4 follow-up (#289) — Scheduler.Overload.Detector instant. Per-tick OverloadDetector
      // gauge snapshot so a viewer can audit why the engine throttled itself. Payload after common
      // header: tick i64, overrunRatio f32, consecutiveOverrun u16, consecutiveUnderrun u16,
      // consecutiveQueueGrowth u16, queueDepth i32, level u8, multiplier u8.
      if ((kind as number) === 242) {
        // tickNumber on the wire (payloadOffset+0..7) is the per-emit tick — keep the surrounding
        // chunk-tick for consistency with other instants. The fields the viewer cares about are
        // overrunRatio + level + multiplier + the consecutive counters.
        return {
          kind, threadSlot, tickNumber, timestampUs,
          overrunRatio: reader.readF32(payloadOffset + 8),
          consecutiveOverrun: reader.readU16(payloadOffset + 12),
          consecutiveUnderrun: reader.readU16(payloadOffset + 14),
          consecutiveQueueGrowth: reader.readU16(payloadOffset + 16),
          queueDepth: reader.readI32(payloadOffset + 18),
          overloadLevel: reader.readU8(payloadOffset + 22),
          tickMultiplier: reader.readU8(payloadOffset + 23),
        };
      }
      if ((kind as number) === 244) {
        // QueueTickEnd — per-(tick, queue) rollup. Wire layout (from `QueueTickEndCodec.Write`):
        //   tickNumber u32 @ +0, queueId u16 @ +4, padding u16 @ +6,
        //   peakDepth u32 @ +8, endOfTickDepth u32 @ +12, overflowCount u32 @ +16,
        //   produced u32 @ +20, consumed u32 @ +24. (28 byte payload.)
        // Surfacing here makes the rollup available to UI tooltips / queue drill-downs without
        // round-tripping through the server-side cache section.
        return {
          kind, threadSlot, tickNumber, timestampUs,
          queueId: reader.readU16(payloadOffset + 4),
          queuePeakDepth: reader.readU32(payloadOffset + 8),
          queueEndOfTickDepth: reader.readU32(payloadOffset + 12),
          queueOverflowCount: reader.readU32(payloadOffset + 16),
          queueProduced: reader.readU32(payloadOffset + 20),
          queueConsumed: reader.readU32(payloadOffset + 24),
        };
      }
      return null;
  }
}

/**
 * Shared TextDecoder for UTF-8 name payloads (ThreadInfo, and any future variable-length-string kinds). Reusing the instance avoids
 * per-record allocation; it's safe for arbitrary byte counts.
 */
const utf8Decoder = new TextDecoder('utf-8');

/**
 * Decode a ThreadInfo (kind 77) record. Wire: <c>i32 managedThreadId, u16 nameByteCount, byte[nameByteCount] nameUtf8, u8 threadKind</c>
 * after the common header. <c>threadKind</c> was added in cache v4 (Main / Worker / Pool / Other);
 * pre-v4 records lack it and we surface it as <c>undefined</c> so the consumer can fall back.
 */
function decodeThreadInfo(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
  recordSize: number,
): TraceEvent {
  const o = pos + COMMON_HEADER_SIZE;
  const managedThreadId = reader.readI32(o);
  const nameByteCount = reader.readU16(o + 4);
  const threadName = nameByteCount > 0 ? reader.readUtf8(o + 6, nameByteCount, utf8Decoder) : undefined;
  // Trailing 1-byte ThreadKind. Position = common header + 4 (mtid) + 2 (nameLen) + nameByteCount.
  // Older records may stop right after the name bytes; guard with the recordSize.
  const kindOffset = o + 6 + nameByteCount;
  const threadKind = kindOffset < pos + recordSize ? reader.readU8(kindOffset) : undefined;
  return {
    kind: TraceEventKind.ThreadInfo,
    threadSlot,
    tickNumber,
    timestampUs,
    managedThreadId,
    threadName,
    threadKind,
  };
}

/**
 * Decode a GcStart (kind 7) record. Wire: <c>u8 generation, u8 reason, u8 type, u32 count</c> after the common header.
 */
function decodeGcStart(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
): TraceEvent {
  const o = pos + COMMON_HEADER_SIZE;
  return {
    kind: TraceEventKind.GcStart,
    threadSlot,
    tickNumber,
    timestampUs,
    generation: reader.readU8(o),
    gcReason: reader.readU8(o + 1),
    gcType: reader.readU8(o + 2),
    gcCount: reader.readU32(o + 3),
  };
}

/**
 * Decode a GcEnd (kind 8) record. Wire: <c>u8 generation, u32 count, i64 pauseDurationTicks, u64 promotedBytes</c>, then five u64
 * per-gen sizes + u64 committed — those last six are already materialised via the per-tick gauge snapshot so we don't re-emit them
 * here. Only the pause duration and promoted bytes are unique to the GcEnd record.
 */
function decodeGcEnd(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
  ticksPerUs: number,
): TraceEvent {
  const o = pos + COMMON_HEADER_SIZE;
  return {
    kind: TraceEventKind.GcEnd,
    threadSlot,
    tickNumber,
    timestampUs,
    generation: reader.readU8(o),
    gcCount: reader.readU32(o + 1),
    gcPauseDurationUs: reader.readI64AsNumber(o + 5) / ticksPerUs,
    gcPromotedBytes: reader.readI64AsNumber(o + 13),
  };
}

/**
 * Decode a <c>MemoryAllocEvent</c> record (kind 9). Wire layout after the 12-byte common header:
 * <c>u8 direction, u16 sourceTag, u64 sizeBytes, u64 totalAfterBytes</c>. Mirrors the server's <c>DecodeMemoryAllocEvent</c>.
 */
function decodeMemoryAllocEvent(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  tickNumber: number,
  timestampUs: number,
): TraceEvent {
  const payloadOffset = pos + COMMON_HEADER_SIZE;
  return {
    kind: TraceEventKind.MemoryAllocEvent,
    threadSlot,
    tickNumber,
    timestampUs,
    direction: reader.readU8(payloadOffset),
    sourceTag: reader.readU16(payloadOffset + 1),
    // sizeBytes / totalAfterBytes are u64 on the wire. Using readI64AsNumber is safe here because realistic allocation sizes and running
    // totals live well below 2^53 (we'd need a petabyte of RAM to overflow). If that assumption ever breaks, switch to a u64-as-double
    // helper that masks the sign bit.
    sizeBytes: reader.readI64AsNumber(payloadOffset + 3),
    totalAfterBytes: reader.readI64AsNumber(payloadOffset + 11),
  };
}

/**
 * Decode a <c>PerTickSnapshot</c> record (kind 76). Wire layout after the 12-byte common header:
 * <c>u32 tickNumber, u16 fieldCount, u32 flags, then repeated {u16 id, u8 valueKind, [4|8] bytes value}</c>. The record's embedded
 * tickNumber is authoritative (from the scheduler's CurrentTickNumber at emit) — the caller's <c>currentTick</c> counter may lag or
 * lead by a tick depending on where the snapshot landed relative to the TickStart/TickEnd markers, so we use the payload's value.
 */
function decodePerTickSnapshot(
  reader: BinaryReader,
  pos: number,
  threadSlot: number,
  timestampUs: number,
): TraceEvent {
  const prefixOffset = pos + COMMON_HEADER_SIZE;
  const tickNumber = reader.readU32(prefixOffset);
  const fieldCount = reader.readU16(prefixOffset + 4);
  const flags = reader.readU32(prefixOffset + 6);

  const gauges: Record<number, number> = {};
  let offset = prefixOffset + 10;
  for (let i = 0; i < fieldCount; i++) {
    const id = reader.readU16(offset);
    const valueKind = reader.readU8(offset + 2);
    offset += 3;

    // valueKind dispatch — matches GaugeValueKind on the server. Sizes must stay in sync with the codec.
    let value: number;
    switch (valueKind) {
      case 0: // U32Count
      case 3: // U32PercentHundredths
        value = reader.readU32(offset);
        offset += 4;
        break;
      case 1: // U64Bytes — safe as number up to 2^53 (petabyte scale)
        value = reader.readI64AsNumber(offset);
        offset += 8;
        break;
      case 2: // I64Signed — read as signed; sign is preserved by readI64AsNumber
        value = reader.readI64AsNumber(offset);
        offset += 8;
        break;
      default:
        // Unknown value kind — stop walking this snapshot to avoid reading past the record. The partial gauges we've already collected stay in the DTO.
        return { kind: TraceEventKind.PerTickSnapshot, threadSlot, tickNumber, timestampUs, flags, gauges };
    }

    gauges[id] = value;
  }

  return {
    kind: TraceEventKind.PerTickSnapshot,
    threadSlot,
    tickNumber,
    timestampUs,
    flags,
    gauges,
  };
}

// ═══════════════════════════════════════════════════════════════════════
// Span header helper — reads durationTicks + spanId + parentSpanId + optional trace context
// ═══════════════════════════════════════════════════════════════════════

interface SpanHeader {
  durationUs: number;
  spanId: string;
  parentSpanId: string;
  traceIdHi: string | null;
  traceIdLo: string | null;
  /** Compile-time source-location id from `SourceLocationGenerator`, or null when the record didn't carry one. */
  sourceLocationId: number | null;
  payloadOffset: number;
  /** Absolute end offset of the record in the chunk reader. Lets per-kind decoders validate trailing wire-additive fields. */
  recordEnd: number;
}

/**
 * Reads the 25-byte span header extension + optional 16-byte trace-context extension + optional 2-byte
 * source-location id, returns the shared span fields plus the offset at which the kind-specific payload begins.
 * All 24 span-kind decoders call this first.
 */
function readSpanHeader(reader: BinaryReader, recordPos: number, ticksPerUs: number): SpanHeader {
  const recordSize = reader.readU16(recordPos);
  const recordEnd = recordPos + recordSize;
  const extStart = recordPos + COMMON_HEADER_SIZE;
  const durationTicks = reader.readI64AsNumber(extStart);
  const spanId = reader.readU64Decimal(extStart + 8);
  const parentSpanId = reader.readU64Decimal(extStart + 16);
  const spanFlags = reader.readU8(extStart + 24);
  const hasTraceContext = (spanFlags & SPAN_FLAGS_HAS_TRACE_CONTEXT) !== 0;
  const hasSourceLocation = (spanFlags & SPAN_FLAGS_HAS_SOURCE_LOCATION) !== 0;

  let traceIdHi: string | null = null;
  let traceIdLo: string | null = null;
  let payloadOffset = extStart + SPAN_HEADER_EXT_SIZE;

  if (hasTraceContext) {
    traceIdHi = reader.readU64Decimal(payloadOffset);
    traceIdLo = reader.readU64Decimal(payloadOffset + 8);
    payloadOffset += TRACE_CONTEXT_SIZE;
  }

  let sourceLocationId: number | null = null;
  if (hasSourceLocation) {
    sourceLocationId = reader.readU16(payloadOffset);
    payloadOffset += SOURCE_LOCATION_ID_SIZE;
  }

  return {
    durationUs: durationTicks / ticksPerUs,
    spanId,
    parentSpanId,
    recordEnd,
    traceIdHi,
    traceIdLo,
    sourceLocationId,
    payloadOffset,
  };
}

/** Build the base TraceEvent from header fields — all span decoders spread this then add kind-specific fields. */
function baseSpanEvent(
  kind: TraceEventKind, threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const evt: TraceEvent = {
    kind, threadSlot, tickNumber, timestampUs,
    durationUs: header.durationUs,
    spanId: header.spanId,
    parentSpanId: header.parentSpanId,
  };
  if (header.traceIdHi !== null) {
    evt.traceIdHi = header.traceIdHi;
    evt.traceIdLo = header.traceIdLo ?? undefined;
  }
  if (header.sourceLocationId !== null) {
    evt.sourceLocationId = header.sourceLocationId;
  }
  return evt;
}

// ═══════════════════════════════════════════════════════════════════════
// Span decoder dispatch — mirrors RecordDecoder.DecodeRecord switch
// ═══════════════════════════════════════════════════════════════════════

function decodeSpan(
  reader: BinaryReader, pos: number, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, ticksPerUs: number,
): TraceEvent | null {
  const header = readSpanHeader(reader, pos, ticksPerUs);

  switch (kind) {
    case TraceEventKind.SchedulerChunk:
      return decodeSchedulerChunk(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.BTreeInsert:
    case TraceEventKind.BTreeDelete:
    case TraceEventKind.BTreeNodeSplit:
    case TraceEventKind.BTreeNodeMerge:
      // No typed payload — header fields are the complete record.
      return baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.TransactionCommit:
    case TraceEventKind.TransactionRollback:
    case TraceEventKind.TransactionCommitComponent:
      return decodeTransaction(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.TransactionPersist:
      return decodeTransactionPersist(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.EcsSpawn:
      return decodeEcsSpawn(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.EcsDestroy:
      return decodeEcsDestroy(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.EcsQueryExecute:
    case TraceEventKind.EcsQueryCount:
    case TraceEventKind.EcsQueryAny:
      return decodeEcsQuery(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.EcsViewRefresh:
      return decodeEcsViewRefresh(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.PageCacheFetch:
    case TraceEventKind.PageCacheDiskRead:
    case TraceEventKind.PageCacheDiskWrite:
    case TraceEventKind.PageCacheAllocatePage:
    case TraceEventKind.PageCacheFlush:
    case TraceEventKind.PageEvicted:
    case TraceEventKind.PageCacheDiskReadCompleted:
    case TraceEventKind.PageCacheDiskWriteCompleted:
    case TraceEventKind.PageCacheFlushCompleted:
      return decodePageCache(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.PageCacheBackpressure:
      return decodePageCacheBackpressure(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.ClusterMigration:
      return decodeClusterMigration(reader, kind, threadSlot, tickNumber, timestampUs, header);

    // WriteTickFenceCluster (61) — per-archetype body span. archetypeId only at Begin; everything else optional.
    case 61 as TraceEventKind:
      return decodeWriteTickFenceCluster(reader, kind, threadSlot, tickNumber, timestampUs, header);

    // WriteTickFenceClusterShadow (62) — ProcessClusterShadowEntries span.
    case 62 as TraceEventKind:
      return decodeWriteTickFenceClusterShadow(reader, kind, threadSlot, tickNumber, timestampUs, header);

    // WriteTickFenceClusterSpatial (63) — cluster spatial-maintenance span.
    case 63 as TraceEventKind:
      return decodeWriteTickFenceClusterSpatial(reader, kind, threadSlot, tickNumber, timestampUs, header);

    // SpatialClusterMigrationDetectScan (249) — fence-time scan span.
    case 249 as TraceEventKind:
      return decodeClusterMigrationDetectScan(reader, kind, threadSlot, tickNumber, timestampUs, header);

    // SpatialClusterAabbRefresh (250) — fence-time AABB refresh span.
    case 250 as TraceEventKind:
      return decodeClusterAabbRefresh(reader, kind, threadSlot, tickNumber, timestampUs, header);

    // WriteTickFenceTable (251) — per-ComponentTable fence body span.
    case 251 as TraceEventKind:
      return decodeWriteTickFenceTable(reader, kind, threadSlot, tickNumber, timestampUs, header);

    // WriteTickFenceShadow (252) — ProcessShadowEntries span for one table.
    case 252 as TraceEventKind:
      return decodeWriteTickFenceShadow(reader, kind, threadSlot, tickNumber, timestampUs, header);

    // WriteTickFenceSpatial (253) — ProcessSpatialEntries span for one table.
    case 253 as TraceEventKind:
      return decodeWriteTickFenceSpatial(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.RuntimePhaseSpan:
      return decodeRuntimePhaseSpan(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.WalFlush:
    case TraceEventKind.WalSegmentRotate:
    case TraceEventKind.WalWait:
      return decodeWal(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.CheckpointCycle:
    case TraceEventKind.CheckpointCollect:
    case TraceEventKind.CheckpointWrite:
    case TraceEventKind.CheckpointFsync:
    case TraceEventKind.CheckpointTransition:
    case TraceEventKind.CheckpointRecycle:
      return decodeCheckpoint(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.StatisticsRebuild:
      return decodeStatisticsRebuild(reader, kind, threadSlot, tickNumber, timestampUs, header);

    case TraceEventKind.NamedSpan:
      // Fall back to generic span — the name payload isn't surfaced on the server's DTO either.
      return baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);

    default:
      // Phase 4 follow-up (#289) — Scheduler.Metronome.Wait. Numeric literal because the kind has no
      // named entry in the TraceEventKind enum (the const-enum stops below 200 + special slots).
      // Payload after span-header: scheduledTimestamp i64, multiplier u8, intentClass u8, phaseFlags u8.
      if ((kind as number) === 241) {
        const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
        // scheduledTimestamp is in stopwatch ticks on the wire; convert to µs for the viewer's domain.
        evt.metronomeScheduledUs = reader.readI64AsNumber(header.payloadOffset) / ticksPerUs;
        evt.tickMultiplier = reader.readU8(header.payloadOffset + 8);
        evt.metronomeIntentClass = reader.readU8(header.payloadOffset + 9);
        evt.metronomePhaseFlags = reader.readU8(header.payloadOffset + 10);
        return evt;
      }

      // QueryPlan (kind 189) — extract the optional OwnerSystemIdx so the trace model can distinguish the
      // per-tick synthesised variant (OwnerSystemIdx > 0, emitted by TyphonRuntime.OnSystemEnd around the
      // whole system body) from the in-execution variant (OwnerSystemIdx absent / 0, emitted by PlanBuilder).
      // Wire layout: BeginParams (19 bytes) = u8 EvaluatorCount, u16 IndexFieldIdx, i64 RangeMin, i64 RangeMax;
      // then u8 optMask @ +19; then the set optional fields in mask-bit order:
      //   0x01: u8  _queryInstanceKind
      //   0x02: u32 _queryInstanceLocalId
      //   0x04: u16 _executionSourceFileId
      //   0x08: i32 _executionSourceLine
      //   0x10: u16 _executionSourceMethodId
      //   0x20: u16 _ownerSystemIdx
      if ((kind as number) === 189) {
        const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
        const optMaskOffset = header.payloadOffset + 19;
        // Guard against truncated/pre-v9 payloads — RecordDecoder would have rejected anything shorter than the
        // begin-params block, but the opt-mask byte may not be present in older traces.
        if (optMaskOffset < header.recordEnd) {
          const mask = reader.readU8(optMaskOffset);
          let cursor = optMaskOffset + 1;
          if ((mask & 0x01) !== 0) cursor += 1; // _queryInstanceKind
          if ((mask & 0x02) !== 0) cursor += 4; // _queryInstanceLocalId
          if ((mask & 0x04) !== 0) cursor += 2; // _executionSourceFileId
          if ((mask & 0x08) !== 0) cursor += 4; // _executionSourceLine
          if ((mask & 0x10) !== 0) cursor += 2; // _executionSourceMethodId
          if ((mask & 0x20) !== 0 && cursor + 2 <= header.recordEnd) {
            evt.systemIndex = reader.readU16(cursor);
          }
        }
        return evt;
      }

      // Unknown kind ≥ 10 — emit header-only event so the viewer can still render timing. Matches RecordDecoder.DecodeGenericSpan.
      return baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  }
}

// ═══════════════════════════════════════════════════════════════════════
// Per-kind span decoders — one per codec family, mirror the server's DecodeXxx methods
// ═══════════════════════════════════════════════════════════════════════

function decodeSchedulerChunk(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Payload: u16 systemIdx, u16 chunkIdx, u16 totalChunks, i32 entitiesProcessed.
  const o = header.payloadOffset;
  return {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    systemIndex: reader.readU16(o),
    chunkIndex: reader.readU16(o + 2),
    totalChunks: reader.readU16(o + 4),
    entitiesProcessed: reader.readI32(o + 6),
  };
}

// Optional-field mask constants matching the C# codecs. Keep these in step with the corresponding [Opt*] constants in Events/*.cs.
const OPT_TX_COMPONENT_COUNT = 0x01;
const OPT_TX_CONFLICT_DETECTED = 0x02;
const OPT_TX_WAL_LSN = 0x01;

function decodeTransaction(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i64 tsn] [i32 componentTypeId?] (CommitComponent only) [u8 optMask] [i32 componentCount?] [u8 conflictDetected?]
  let cursor = header.payloadOffset;
  const tsn = reader.readI64Decimal(cursor);
  cursor += 8;

  let componentTypeId: number | undefined;
  if (kind === TraceEventKind.TransactionCommitComponent) {
    componentTypeId = reader.readI32(cursor);
    cursor += 4;
  }

  const optMask = reader.readU8(cursor);
  cursor += 1;

  let componentCount: number | undefined;
  let conflictDetected: boolean | undefined;
  if ((optMask & OPT_TX_COMPONENT_COUNT) !== 0) {
    componentCount = reader.readI32(cursor);
    cursor += 4;
  }
  if ((optMask & OPT_TX_CONFLICT_DETECTED) !== 0) {
    conflictDetected = reader.readU8(cursor) !== 0;
    cursor += 1;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.tsn = tsn;
  if (componentTypeId !== undefined) evt.componentTypeId = componentTypeId;
  if (componentCount !== undefined) evt.componentCount = componentCount;
  if (conflictDetected !== undefined) evt.conflictDetected = conflictDetected;
  return evt;
}

function decodeTransactionPersist(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i64 tsn] [u8 optMask] [i64 walLsn?]
  let cursor = header.payloadOffset;
  const tsn = reader.readI64Decimal(cursor);
  cursor += 8;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let walLsn: string | undefined;
  if ((optMask & OPT_TX_WAL_LSN) !== 0) {
    walLsn = reader.readI64Decimal(cursor);
    cursor += 8;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.tsn = tsn;
  if (walLsn !== undefined) evt.walLsn = walLsn;
  return evt;
}

const OPT_SPAWN_ENTITY_ID = 0x01;
const OPT_SPAWN_TSN = 0x02;

function decodeEcsSpawn(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u16 archetypeId] [u8 optMask] [u64 entityId?] [i64 tsn?]
  let cursor = header.payloadOffset;
  const archetypeId = reader.readU16(cursor);
  cursor += 2;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let entityId: string | undefined;
  let tsn: string | undefined;
  if ((optMask & OPT_SPAWN_ENTITY_ID) !== 0) {
    entityId = reader.readU64Decimal(cursor);
    cursor += 8;
  }
  if ((optMask & OPT_SPAWN_TSN) !== 0) {
    tsn = reader.readI64Decimal(cursor);
    cursor += 8;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.archetypeId = archetypeId;
  if (entityId !== undefined) evt.entityId = entityId;
  if (tsn !== undefined) evt.tsn = tsn;
  return evt;
}

const OPT_DESTROY_CASCADE_COUNT = 0x01;
const OPT_DESTROY_TSN = 0x02;

function decodeEcsDestroy(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u64 entityId] [u8 optMask] [i32 cascadeCount?] [i64 tsn?]
  let cursor = header.payloadOffset;
  const entityId = reader.readU64Decimal(cursor);
  cursor += 8;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let cascadeCount: number | undefined;
  let tsn: string | undefined;
  if ((optMask & OPT_DESTROY_CASCADE_COUNT) !== 0) {
    cascadeCount = reader.readI32(cursor);
    cursor += 4;
  }
  if ((optMask & OPT_DESTROY_TSN) !== 0) {
    tsn = reader.readI64Decimal(cursor);
    cursor += 8;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.entityId = entityId;
  if (cascadeCount !== undefined) evt.cascadeCount = cascadeCount;
  if (tsn !== undefined) evt.tsn = tsn;
  return evt;
}

const OPT_QUERY_RESULT_COUNT = 0x01;
const OPT_QUERY_SCAN_MODE = 0x02;
const OPT_QUERY_FOUND = 0x04;

function decodeEcsQuery(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u16 archetypeTypeId] [u8 optMask] [i32 resultCount-or-found?] [u8 scanMode?]
  // ResultCount and Found share the same 4 B slot — decoder disambiguates by which optMask bit is set.
  let cursor = header.payloadOffset;
  const archetypeTypeId = reader.readU16(cursor);
  cursor += 2;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let resultCount: number | undefined;
  let scanMode: number | undefined;
  let found: boolean | undefined;

  if ((optMask & (OPT_QUERY_RESULT_COUNT | OPT_QUERY_FOUND)) !== 0) {
    const slot = reader.readI32(cursor);
    if ((optMask & OPT_QUERY_FOUND) !== 0) {
      found = slot !== 0;
    } else {
      resultCount = slot;
    }
    cursor += 4;
  }
  if ((optMask & OPT_QUERY_SCAN_MODE) !== 0) {
    scanMode = reader.readU8(cursor);
    cursor += 1;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  // Server DTO uses `archetypeId` on the wire (not `archetypeTypeId`) — the type ID IS the archetype ID in the viewer's vocabulary.
  evt.archetypeId = archetypeTypeId;
  if (resultCount !== undefined) evt.resultCount = resultCount;
  if (scanMode !== undefined) evt.scanMode = scanMode;
  if (found !== undefined) evt.found = found;
  return evt;
}

const OPT_VR_MODE = 0x01;
const OPT_VR_RESULT_COUNT = 0x02;
const OPT_VR_DELTA_COUNT = 0x04;

function decodeEcsViewRefresh(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u16 archetypeTypeId] [u8 optMask] [u8 mode?] [i32 resultCount?] [i32 deltaCount?]
  let cursor = header.payloadOffset;
  const archetypeTypeId = reader.readU16(cursor);
  cursor += 2;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let mode: number | undefined;
  let resultCount: number | undefined;
  let deltaCount: number | undefined;

  if ((optMask & OPT_VR_MODE) !== 0) {
    mode = reader.readU8(cursor);
    cursor += 1;
  }
  if ((optMask & OPT_VR_RESULT_COUNT) !== 0) {
    resultCount = reader.readI32(cursor);
    cursor += 4;
  }
  if ((optMask & OPT_VR_DELTA_COUNT) !== 0) {
    deltaCount = reader.readI32(cursor);
    cursor += 4;
  }

  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  evt.archetypeId = archetypeTypeId;
  if (mode !== undefined) evt.mode = mode;
  if (resultCount !== undefined) evt.resultCount = resultCount;
  if (deltaCount !== undefined) evt.deltaCount = deltaCount;
  return evt;
}

const OPT_PC_PAGE_COUNT = 0x01;

function decodePageCache(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i32 filePageIndex] [u8 optMask] [i32 pageCount?]
  // The Flush kinds reuse the filePageIndex slot to carry PageCount (per the wire spec on TraceEventKind.PageCacheFlush and *FlushCompleted).
  let cursor = header.payloadOffset;
  const primary = reader.readI32(cursor);
  cursor += 4;
  const optMask = reader.readU8(cursor);
  cursor += 1;

  let secondary: number | undefined;
  if ((optMask & OPT_PC_PAGE_COUNT) !== 0) {
    secondary = reader.readI32(cursor);
    cursor += 4;
  }

  const isFlush = kind === TraceEventKind.PageCacheFlush || kind === TraceEventKind.PageCacheFlushCompleted;
  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  if (isFlush) {
    evt.pageCount = primary;
  } else {
    evt.filePageIndex = primary;
    if (secondary !== undefined) evt.pageCount = secondary;
  }
  return evt;
}

function decodePageCacheBackpressure(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i32 retryCount] [i32 dirtyCount] [i32 epochCount]
  const o = header.payloadOffset;
  return {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    retryCount: reader.readI32(o),
    dirtyCount: reader.readI32(o + 4),
    epochCount: reader.readI32(o + 8),
  };
}

function decodeRuntimePhaseSpan(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u8 phase] (TickPhase enum). Replaces the deprecated PhaseStart+PhaseEnd instant pair.
  return {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    phase: reader.readU8(header.payloadOffset),
  };
}

function decodeClusterMigration(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [u16 archetypeId] [i32 migrationCount] [i32 componentCount?]
  // componentCount is wire-additive — older traces (pre-#289 follow-up) omit it. The decoder reads
  // it only when the record is large enough to contain it, otherwise treats it as 0.
  const o = header.payloadOffset;
  const evt: TraceEvent = {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    archetypeId: reader.readU16(o),
    migrationCount: reader.readI32(o + 2),
  };
  if (o + 10 <= header.recordEnd) {
    evt.componentCount = reader.readI32(o + 6);
  }
  return evt;
}

// SpatialClusterMigrationDetectScan (kind 249) — fence-time scan span. Wire layout:
// BeginParams (6 bytes): u16 archetypeId, i32 scanSlotCount.
// Then u8 optMask @ +6; then optional fields in mask-bit order:
//   0x01: i32 _migrationsQueued
//   0x02: i32 _hysteresisAbsorbed
//   0x04: i32 _clustersTouched
function decodeClusterMigrationDetectScan(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt: TraceEvent = {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    archetypeId: reader.readU16(o),
    scanSlotCount: reader.readI32(o + 2),
  };
  const optMaskOffset = o + 6;
  if (optMaskOffset < header.recordEnd) {
    const mask = reader.readU8(optMaskOffset);
    let cursor = optMaskOffset + 1;
    if ((mask & 0x01) !== 0) { evt.migrationsQueued = reader.readI32(cursor); cursor += 4; }
    if ((mask & 0x02) !== 0) { evt.hysteresisAbsorbed = reader.readI32(cursor); cursor += 4; }
    if ((mask & 0x04) !== 0) { evt.clustersTouched = reader.readI32(cursor); cursor += 4; }
  }
  return evt;
}

// SpatialClusterAabbRefresh (kind 250) — fence-time AABB refresh span. Wire layout:
// BeginParams (6 bytes): u16 archetypeId, i32 clusterScanned.
// Then u8 optMask @ +6; then optional fields in mask-bit order:
//   0x01: i32 _aabbsChanged
//   0x02: i32 _slotsScanned
//   0x04: i32 _outlierGuardFires
function decodeClusterAabbRefresh(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt: TraceEvent = {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    archetypeId: reader.readU16(o),
    clusterScanned: reader.readI32(o + 2),
  };
  const optMaskOffset = o + 6;
  if (optMaskOffset < header.recordEnd) {
    const mask = reader.readU8(optMaskOffset);
    let cursor = optMaskOffset + 1;
    if ((mask & 0x01) !== 0) { evt.aabbsChanged = reader.readI32(cursor); cursor += 4; }
    if ((mask & 0x02) !== 0) { evt.slotsScanned = reader.readI32(cursor); cursor += 4; }
    if ((mask & 0x04) !== 0) { evt.outlierGuardFires = reader.readI32(cursor); cursor += 4; }
  }
  return evt;
}

// WriteTickFenceTable (kind 251) — per-ComponentTable fence body span. Wire layout:
// BeginParams (6 bytes): u16 componentTypeId, i32 dirtyEntryCount.
// Then u8 optMask @ +6; then optional fields in mask-bit order:
//   0x01: u8 _walPublished
//   0x02: u8 _hasShadow
//   0x04: u8 _hasSpatial
function decodeWriteTickFenceTable(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt: TraceEvent = {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    componentTypeId: reader.readU16(o),
    dirtyEntryCount: reader.readI32(o + 2),
  };
  const optMaskOffset = o + 6;
  if (optMaskOffset < header.recordEnd) {
    const mask = reader.readU8(optMaskOffset);
    let cursor = optMaskOffset + 1;
    if ((mask & 0x01) !== 0) { evt.walPublished = reader.readU8(cursor); cursor += 1; }
    if ((mask & 0x02) !== 0) { evt.hasShadow = reader.readU8(cursor); cursor += 1; }
    if ((mask & 0x04) !== 0) { evt.hasSpatial = reader.readU8(cursor); cursor += 1; }
  }
  return evt;
}

// WriteTickFenceShadow (kind 252) — ProcessShadowEntries span. Wire layout:
// BeginParams (6 bytes): u16 componentTypeId, i32 indexedFieldCount.
// Then u8 optMask @ +6; then optional fields:
//   0x01: i32 _totalShadowEntries
function decodeWriteTickFenceShadow(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt: TraceEvent = {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    componentTypeId: reader.readU16(o),
    indexedFieldCount: reader.readI32(o + 2),
  };
  const optMaskOffset = o + 6;
  if (optMaskOffset < header.recordEnd) {
    const mask = reader.readU8(optMaskOffset);
    let cursor = optMaskOffset + 1;
    if ((mask & 0x01) !== 0) { evt.totalShadowEntries = reader.readI32(cursor); cursor += 4; }
  }
  return evt;
}

// WriteTickFenceSpatial (kind 253) — ProcessSpatialEntries span. Wire layout:
// BeginParams (6 bytes): u16 componentTypeId, i32 dirtyEntryCount.
// Then u8 optMask @ +6; then optional fields:
//   0x01: i32 _escapedCount
function decodeWriteTickFenceSpatial(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt: TraceEvent = {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    componentTypeId: reader.readU16(o),
    dirtyEntryCount: reader.readI32(o + 2),
  };
  const optMaskOffset = o + 6;
  if (optMaskOffset < header.recordEnd) {
    const mask = reader.readU8(optMaskOffset);
    let cursor = optMaskOffset + 1;
    if ((mask & 0x01) !== 0) { evt.escapedCount = reader.readI32(cursor); cursor += 4; }
  }
  return evt;
}

// WriteTickFenceCluster (kind 61) — per-archetype body span inside WriteClusterTickFence.
// BeginParams (2 bytes): u16 archetypeId.
// Then u8 optMask @ +2; then optional fields:
//   0x01: i32 _dirtyClusterCount
//   0x02: i32 _entryCount
//   0x04: u8  _hasShadow
//   0x08: u8  _hasSpatial
//   0x10: u8  _walPublished
function decodeWriteTickFenceCluster(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt: TraceEvent = {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    archetypeId: reader.readU16(o),
  };
  const optMaskOffset = o + 2;
  if (optMaskOffset < header.recordEnd) {
    const mask = reader.readU8(optMaskOffset);
    let cursor = optMaskOffset + 1;
    if ((mask & 0x01) !== 0) { evt.dirtyClusterCount = reader.readI32(cursor); cursor += 4; }
    if ((mask & 0x02) !== 0) { evt.entryCount = reader.readI32(cursor); cursor += 4; }
    if ((mask & 0x04) !== 0) { evt.hasShadow = reader.readU8(cursor); cursor += 1; }
    if ((mask & 0x08) !== 0) { evt.hasSpatial = reader.readU8(cursor); cursor += 1; }
    if ((mask & 0x10) !== 0) { evt.walPublished = reader.readU8(cursor); cursor += 1; }
  }
  return evt;
}

// WriteTickFenceClusterShadow (kind 62) — ProcessClusterShadowEntries span.
// BeginParams (6 bytes): u16 archetypeId, i32 dirtyClusterCount.
// Then u8 optMask @ +6; then optional fields:
//   0x01: i32 _totalShadowEntries
function decodeWriteTickFenceClusterShadow(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt: TraceEvent = {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    archetypeId: reader.readU16(o),
    dirtyClusterCount: reader.readI32(o + 2),
  };
  const optMaskOffset = o + 6;
  if (optMaskOffset < header.recordEnd) {
    const mask = reader.readU8(optMaskOffset);
    let cursor = optMaskOffset + 1;
    if ((mask & 0x01) !== 0) { evt.totalShadowEntries = reader.readI32(cursor); cursor += 4; }
  }
  return evt;
}

// WriteTickFenceClusterSpatial (kind 63) — cluster spatial-maintenance span.
// BeginParams (6 bytes): u16 archetypeId, i32 dirtyClusterCount.
// Then u8 optMask @ +6; then optional fields:
//   0x01: i32 _migrationsExecuted
function decodeWriteTickFenceClusterSpatial(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt: TraceEvent = {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    archetypeId: reader.readU16(o),
    dirtyClusterCount: reader.readI32(o + 2),
  };
  const optMaskOffset = o + 6;
  if (optMaskOffset < header.recordEnd) {
    const mask = reader.readU8(optMaskOffset);
    let cursor = optMaskOffset + 1;
    if ((mask & 0x01) !== 0) { evt.migrationsExecuted = reader.readI32(cursor); cursor += 4; }
  }
  return evt;
}

function decodeWal(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const o = header.payloadOffset;
  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);

  switch (kind) {
    case TraceEventKind.WalFlush:
      // [i32 batchByteCount][i32 frameCount][i64 highLsn]
      evt.batchByteCount = reader.readI32(o);
      evt.frameCount = reader.readI32(o + 4);
      evt.highLsn = reader.readI64Decimal(o + 8);
      break;
    case TraceEventKind.WalSegmentRotate:
      // [i32 newSegmentIndex]
      evt.newSegmentIndex = reader.readI32(o);
      break;
    case TraceEventKind.WalWait:
      // [i64 targetLsn]
      evt.targetLsn = reader.readI64Decimal(o);
      break;
  }
  return evt;
}

const OPT_CP_COUNT = 0x01;   // shared bit 0: DirtyPageCount / WrittenCount / TransitionedCount / RecycledCount depending on kind

function decodeCheckpoint(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  const evt = baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header);
  const o = header.payloadOffset;

  switch (kind) {
    case TraceEventKind.CheckpointCycle: {
      // [i64 targetLsn][u8 reason][u8 optMask][i32 dirtyPageCount?]
      evt.targetLsn = reader.readI64Decimal(o);
      evt.reason = reader.readU8(o + 8);
      const optMask = reader.readU8(o + 9);
      if ((optMask & OPT_CP_COUNT) !== 0) {
        evt.dirtyPageCount = reader.readI32(o + 10);
      }
      break;
    }
    case TraceEventKind.CheckpointWrite: {
      // [u8 optMask][i32 writtenCount?]
      const optMask = reader.readU8(o);
      if ((optMask & OPT_CP_COUNT) !== 0) {
        evt.writtenCount = reader.readI32(o + 1);
      }
      break;
    }
    case TraceEventKind.CheckpointTransition: {
      const optMask = reader.readU8(o);
      if ((optMask & OPT_CP_COUNT) !== 0) {
        evt.transitionedCount = reader.readI32(o + 1);
      }
      break;
    }
    case TraceEventKind.CheckpointRecycle: {
      const optMask = reader.readU8(o);
      if ((optMask & OPT_CP_COUNT) !== 0) {
        evt.recycledCount = reader.readI32(o + 1);
      }
      break;
    }
    // Collect + Fsync have no payload; header-only event is correct.
  }
  return evt;
}

function decodeStatisticsRebuild(
  reader: BinaryReader, kind: TraceEventKind,
  threadSlot: number, tickNumber: number, timestampUs: number, header: SpanHeader,
): TraceEvent {
  // Layout: [i32 entityCount][i32 mutationCount][i32 samplingInterval]
  const o = header.payloadOffset;
  return {
    ...baseSpanEvent(kind, threadSlot, tickNumber, timestampUs, header),
    entityCount: reader.readI32(o),
    mutationCount: reader.readI32(o + 4),
    samplingInterval: reader.readI32(o + 8),
  };
}
