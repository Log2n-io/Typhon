import type { ArchetypePlan, CatalogPlan, GridPlan, MessagePlan, PositionPlan } from './catalog.js';
import { CodecKind } from './codec-kinds.js';
import { BlockType, MessageType, SourceStatus, TickFlags } from './constants.js';
import { writeNumber, writeSection, type FieldValues } from './field-codec.js';
import { uintInRange } from './range.js';
import type { WireWriter } from './writer.js';

/*
 * The `TICK` encoder, block by block — the server's direction, mirroring the C# reference encoder (`TickWriter`). A
 * client never sends a `TICK`; this exists so recorded or synthetic frames can be produced in TypeScript (tests, mock
 * servers) with the exact bytes a server would write.
 */

export interface EnterRecord {
  readonly netId: number;
  /** The position, when the archetype is spatial. */
  readonly position?: ArrayLike<number>;
  /** The velocity per tick, when the archetype moves linearly. */
  readonly velocity?: ArrayLike<number>;
  /** The segment's absolute start tick, when the archetype moves. */
  readonly t0?: number;
  readonly epoch?: number;
  /** Every public field's value. */
  readonly values: FieldValues;
}

export interface SegmentRecord {
  readonly netId: number;
  readonly position: ArrayLike<number>;
  readonly velocity?: ArrayLike<number>;
  readonly t0: number;
  readonly epoch: number;
}

export interface StateRecord {
  readonly netId: number;
  readonly groupMask: number;
  /** The values of the carried groups' fields. */
  readonly values: FieldValues;
}

export interface EventInput {
  readonly type: MessagePlan;
  readonly values: FieldValues;
}

export interface AggregateCellInput {
  readonly cell: number;
  readonly counts: ArrayLike<number>;
}

export interface SourceInput {
  readonly requestId: number;
  readonly status: number;
  readonly code?: number;
}

const NO_VALUES: ArrayLike<number> = [];

/** The type byte and frame header; {@link TickFlags.Period} is set when `periodUs` is non-zero. */
export function writeTickHeader(w: WireWriter, tick: number, flags: number, periodUs = 0): void {
  w.u8(MessageType.Tick);
  w.u32(uintInRange(tick, 0xffffffff, 'tick'));
  // Checked before PERIOD is ORed in: `|` truncates to 32 bits first, so 2^32 + 1 or 1.5 would pass as 9.
  const checkedFlags = uintInRange(flags, 0xff, 'flags');
  const f = periodUs !== 0 ? checkedFlags | TickFlags.Period : checkedFlags;
  w.u8(f);
  if ((f & TickFlags.Period) !== 0) {
    w.u32(uintInRange(periodUs, 0xffffffff, 'periodUs'));
  }
}

/** Starts a block; write its payload, then {@link endBlock} with the returned mark. */
export function beginBlock(w: WireWriter, blockType: number): number {
  w.u8(blockType);
  return w.beginLengthPrefixed();
}

export function endBlock(w: WireWriter, mark: number): void {
  w.endLengthPrefixed(mark);
}

/**
 * A whole `ENTITIES` block for the frame at `frameTick`. Each list must be sorted by ascending, distinct netId, and every
 * segment's `t0` must lie in the 2^16 ticks up to the frame's (W9), or `tickLo` would decode it 65 536 ticks off.
 */
export function writeEntitiesBlock(
  w: WireWriter,
  frameTick: number,
  archetype: ArchetypePlan,
  enters: readonly EnterRecord[],
  segments: readonly SegmentRecord[],
  states: readonly StateRecord[],
  leaves: readonly number[],
): void {
  const mark = beginBlock(w, BlockType.Entities);
  w.varu(archetype.idx);
  const position = archetype.position;

  w.varu(enters.length);
  let prev = -1;
  for (const e of enters) {
    prev = writeGap(w, prev, e.netId);
    if (position !== null) {
      writeNumber(w, position.pos, e.position ?? NO_VALUES);
      if (position.moving) {
        writeSegmentTail(w, frameTick, position, e.velocity ?? NO_VALUES, e.t0 ?? 0, e.epoch ?? 0);
      }
    }

    writeSection(w, archetype.onEnter, e.values);
    for (const section of archetype.groupSections) {
      writeSection(w, section, e.values);
    }
  }

  if (segments.length > 0 && !(position?.moving ?? false)) {
    throw new RangeError(`archetype '${archetype.name}' does not move and cannot carry segments`);
  }

  w.varu(segments.length);
  prev = -1;
  for (const s of segments) {
    prev = writeGap(w, prev, s.netId);
    writeNumber(w, position!.pos, s.position);
    writeSegmentTail(w, frameTick, position!, s.velocity ?? NO_VALUES, s.t0, s.epoch);
  }

  w.varu(states.length);
  prev = -1;
  const groupCount = archetype.groups.length;
  for (const s of states) {
    // Compared, not shifted: `>>` truncates to 32 bits first, so a mask of 2^32 + 1 would pass as 1.
    if (!(Number.isInteger(s.groupMask) && s.groupMask > 0 && s.groupMask < 2 ** groupCount)) {
      throw new RangeError(`state mask 0x${s.groupMask.toString(16)} is invalid for ${groupCount} group(s)`);
    }

    prev = writeGap(w, prev, s.netId);
    w.u8(s.groupMask);
    for (let g = 0; g < groupCount; g++) {
      if ((s.groupMask & (1 << g)) !== 0) {
        writeSection(w, archetype.groupSections[g]!, s.values);
      }
    }
  }

  w.varu(leaves.length);
  prev = -1;
  for (const netId of leaves) {
    prev = writeGap(w, prev, netId);
  }

  endBlock(w, mark);
}

/** A whole `EVENTS` block, in emission order. */
export function writeEventsBlock(w: WireWriter, events: readonly EventInput[]): void {
  const mark = beginBlock(w, BlockType.Events);
  w.varu(events.length);
  for (const e of events) {
    w.varu(e.type.idx);
    writeSection(w, e.type.body, e.values);
  }

  endBlock(w, mark);
}

/** A `SELF` block. */
export function writeSelfBlock(
  w: WireWriter,
  archetype: ArchetypePlan,
  netId: number,
  lastSeq: number,
  ownerMask: number,
  values: FieldValues,
): void {
  const groupCount = archetype.ownerGroups.length;
  if (!(Number.isInteger(ownerMask) && ownerMask >= 0 && ownerMask < 2 ** groupCount)) {
    throw new RangeError(`owner mask 0x${ownerMask.toString(16)} is invalid for ${groupCount} owner group(s)`);
  }

  const mark = beginBlock(w, BlockType.Self);
  w.varu(archetype.idx);
  w.varu(uintInRange(netId, 0xffffffff, 'netId'));
  w.u16(uintInRange(lastSeq, 0xffff, 'lastSeq'));
  w.u8(ownerMask);
  for (let g = 0; g < groupCount; g++) {
    if ((ownerMask & (1 << g)) !== 0) {
      writeSection(w, archetype.ownerSections[g]!, values);
    }
  }

  endBlock(w, mark);
}

/** An `ACKS` block. */
export function writeAcksBlock(
  w: WireWriter,
  acks: readonly { readonly seq: number; readonly reason: number }[],
): void {
  const mark = beginBlock(w, BlockType.Acks);
  w.varu(acks.length);
  for (const a of acks) {
    w.u16(uintInRange(a.seq, 0xffff, 'seq'));
    w.u8(uintInRange(a.reason, 0xff, 'reason'));
  }

  endBlock(w, mark);
}

/** A `SOURCES` block. */
export function writeSourcesBlock(w: WireWriter, entries: readonly SourceInput[]): void {
  const mark = beginBlock(w, BlockType.Sources);
  w.varu(entries.length);
  for (const e of entries) {
    w.u16(uintInRange(e.requestId, 0xffff, 'requestId'));
    w.u8(uintInRange(e.status, 0xff, 'status'));
    if (e.status === SourceStatus.Error) {
      w.u16(uintInRange(e.code ?? 0, 0xffff, 'code'));
    }
  }

  endBlock(w, mark);
}

/** An `AGG` block. Cells must be ascending. */
export function writeAggregateBlock(
  w: WireWriter,
  grid: GridPlan,
  reset: boolean,
  cells: readonly AggregateCellInput[],
): void {
  const mark = beginBlock(w, BlockType.Agg);
  w.varu(grid.idx);
  w.u8(reset ? 1 : 0);
  w.varu(cells.length);
  let prev = -1;
  for (const c of cells) {
    prev = writeGap(w, prev, c.cell);
    if (c.counts.length !== grid.counts.length) {
      throw new RangeError(`cell ${c.cell} needs one count per grid archetype`);
    }

    for (let a = 0; a < c.counts.length; a++) {
      w.varu(uintInRange(c.counts[a]!, 0xffffffff, 'count'));
    }
  }

  endBlock(w, mark);
}

/** A `STATS` block: every server metric's values, then every session metric's, in index order. */
export function writeStatsBlock(
  w: WireWriter,
  plan: CatalogPlan,
  values: Readonly<Record<string, ArrayLike<number> | undefined>>,
): void {
  const mark = beginBlock(w, BlockType.Stats);
  for (const metrics of [plan.serverMetrics, plan.sessionMetrics]) {
    for (const m of metrics) {
      const v = Object.hasOwn(values, m.name) ? values[m.name] : undefined;
      if (v?.length !== m.valueCount) {
        throw new RangeError(`metric '${m.name}' needs ${m.valueCount} value(s)`);
      }

      for (let i = 0; i < v.length; i++) {
        scalar[0] = saturate(v[i]!, m.value.kind);
        writeNumber(w, m.value, scalar);
      }
    }
  }

  endBlock(w, mark);
}

/** A `DEBUG` block of sub-blocks. */
export function writeDebugBlock(
  w: WireWriter,
  subBlocks: readonly { readonly subType: number; readonly payload: Uint8Array }[],
): void {
  const mark = beginBlock(w, BlockType.Debug);
  for (const s of subBlocks) {
    w.u8(uintInRange(s.subType, 0xff, 'subType'));
    w.varu(s.payload.length);
    w.raw(s.payload);
  }

  endBlock(w, mark);
}

/** An `EXT` block. */
export function writeExtBlock(w: WireWriter, appTypeId: number, payload: Uint8Array): void {
  const mark = beginBlock(w, BlockType.Ext);
  w.varu(uintInRange(appTypeId, 0xffffffff, 'appTypeId'));
  w.raw(payload);
  endBlock(w, mark);
}

function writeSegmentTail(
  w: WireWriter,
  frameTick: number,
  position: PositionPlan,
  velocity: ArrayLike<number>,
  t0: number,
  epoch: number,
): void {
  // W9: tickLo is exact only for a past tick less than 2^16 old; a stale segment would decode 65 536 ticks off.
  uintInRange(frameTick, 0xffffffff, 'frame tick');
  if (!(Number.isInteger(t0) && t0 >= 0 && t0 <= frameTick && frameTick - t0 < 65536)) {
    throw new RangeError(`segment start tick ${t0} is not within the 2^16 ticks up to frame tick ${frameTick}`);
  }

  if (position.vel !== null) {
    writeNumber(w, position.vel, velocity);
  }

  w.u16(t0 & 0xffff);
  w.u8(uintInRange(epoch, 0xff, 'epoch'));
}

const scalar = new Float64Array(1);

/** W25: a metric never carries NaN or an infinity, and a float saturates at its largest finite value. */
function saturate(x: number, kind: CodecKind): number {
  if (kind !== CodecKind.F16 && kind !== CodecKind.F32) {
    return x;
  }

  const max = kind === CodecKind.F16 ? 65504 : 3.4028234663852886e38;
  return x !== x ? 0 : x > max ? max : x < -max ? -max : x;
}

/** A value that must be an integer in [0, max]: a bug on the encoding side otherwise, never peer input. */
function writeGap(w: WireWriter, prev: number, id: number): number {
  if (!(id > prev) || !Number.isInteger(id) || id > 0xffffffff) {
    throw new RangeError(`ids must be ascending, distinct 32-bit integers: ${id} after ${prev}`);
  }

  w.varu(id - prev - 1);
  return id;
}
