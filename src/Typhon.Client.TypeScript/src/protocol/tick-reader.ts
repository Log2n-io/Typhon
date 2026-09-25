import {
  ValueKind,
  type ArchetypePlan,
  type CatalogPlan,
  type GridPlan,
  type MessagePlan,
  type MetricPlan,
} from './catalog.js';
import { BlockType, MessageType, SourceStatus, TickFlags } from './constants.js';
import { malformed, protocolError } from './errors.js';
import { readNumber, readSection, type FieldSink } from './field-codec.js';
import { WireReader } from './reader.js';
import type { ArchetypeStore } from '../store/archetype-store.js';

/**
 * Receives a decoded `TICK`, block by block, in stream order (C# `ITickSink`). Field values arrive through the
 * {@link FieldSink} members between the record call that opened them and the next record call. Every array or view
 * handed over is reused by the decoder: valid only for the duration of the call.
 */
export interface TickSink extends FieldSink {
  /** A frame begins. `periodUs` is the elapsed interval's period when {@link TickFlags.Period} is set, otherwise 0. */
  beginTick(tick: number, flags: number, periodUs: number): void;
  /** An `ENTITIES` block begins; the records that follow belong to `archetype`. */
  beginEntities(archetype: ArchetypePlan): void;
  /**
   * An enter record: its position, then every public field in wire order through the field members. `position` is
   * exactly the archetype's `position.dims` long (empty when it is not spatial), `velocity` as long when it moves
   * linearly and empty otherwise; the start tick `t0[0]` and `epoch` are 0 unless it moves.
   *
   * `t0` is a one-slot array, not a number: an absolute tick outgrows the small-integer range (2^31 in Node, 2^30 in
   * browsers with pointer compression), and past it a number crossing a call V8 does not inline is boxed, once per
   * record (AC-6).
   */
  enter(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): void;
  /**
   * A motion segment starting at tick `t0[0]`; `velocity` is empty for the `none` model. Never interpolate across an
   * epoch change.
   */
  segment(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): void;
  /** A state record: the fields of the groups in `groupMask` follow, ascending. */
  state(netId: number, groupMask: number): void;
  /** A leave. A store applies it last in the frame, so an event can still resolve the entity. */
  leave(netId: number): void;
  /** An event: its fields follow. */
  event(type: MessagePlan): void;
  /**
   * The `SELF` block: the owner groups in `ownerMask` follow. `archetype` is `null` and `netId` 0 when the session
   * controls no entity (W17′): an acknowledgement only, which also tells the client to drop the owner state it holds.
   */
  self(archetype: ArchetypePlan | null, netId: number, lastSeq: number, ownerMask: number): void;
  /** A command rejection. */
  ack(seq: number, reason: number): void;
  /** A source lifecycle entry; `code` is 0 unless `status` is {@link SourceStatus.Error}. */
  source(requestId: number, status: number, code: number): void;
  /** An `AGG` block begins; with `reset`, every cell not listed is now empty. */
  beginAggregate(grid: GridPlan, reset: boolean): void;
  /** One changed cell's counts, one per grid archetype. */
  aggregateCell(cell: number, counts: Uint32Array): void;
  /** One metric value; `valueIndex` is 0 for a scalar. */
  metric(metric: MetricPlan, valueIndex: number, value: number): void;
  /** A `DEBUG` sub-block: `data[offset .. offset + length)`. */
  debug(subType: number, data: Uint8Array, offset: number, length: number): void;
  /** An `EXT` block: `data[offset .. offset + length)`. */
  ext(appTypeId: number, data: Uint8Array, offset: number, length: number): void;
  /** A block of a type this library does not know, skipped. */
  unknownBlock(blockType: number): void;
  endTick(): void;
}

/**
 * What a generated `ENTITIES` decoder (05-sdks § 2, `typhon-codegen`) writes into: the record bookkeeping of a store
 * that applies frames, so the generated code only turns bytes into column values. Implemented by `FrameApplier`.
 *
 * Every method keeps the interpreter's semantics exactly — the anomalies it counts and the records it ignores — because
 * the two paths must leave the same store for the same bytes.
 */
export interface EntitiesTarget {
  /** The store of archetype `idx`. Its columns are replaced when it grows: re-read them when its `version` changes. */
  archetypeStore(idx: number): ArchetypeStore;
  /** An enter; the slot its fields go to, or −1 when the enter is an anomaly and its fields are discarded. */
  enterSlot(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): number;
  /** A motion segment. */
  segmentAt(netId: number, position: Float64Array, velocity: Float64Array, t0: Uint32Array, epoch: number): void;
  /** A state record; the slot its groups' fields go to, or −1 when it is an anomaly. */
  stateSlot(netId: number, groupMask: number): number;
  /** A leave, applied last in the frame. */
  leave(netId: number): void;
}

/**
 * A generated decoder for one archetype's `ENTITIES` block, after its archetype index: every record, straight into the
 * target's columns. {@link TickReader} still reads the block's framing and checks it is the archetype's only one.
 */
export type EntitiesDecoder = (r: WireReader, tick: number, target: EntitiesTarget) => void;

/** The generated decoders a `typhon-codegen` module exports, and the catalog they were generated from. */
export interface GeneratedDecoders {
  /** The catalog's hash as 16 lower-case hex digits, compared with the server's at connect. */
  readonly catalogHash: string;
  /** By archetype index: that archetype's `ENTITIES` decoder, or `null` to leave it to the interpreter. */
  readonly entities: readonly (EntitiesDecoder | null)[];
}

/** Block selection for {@link TickReader.read}: one bit per block type. */
export const BlockMask = {
  Entities: 1 << BlockType.Entities,
  Events: 1 << BlockType.Events,
  Self: 1 << BlockType.Self,
  Agg: 1 << BlockType.Agg,
  Stats: 1 << BlockType.Stats,
  Debug: 1 << BlockType.Debug,
  Acks: 1 << BlockType.Acks,
  Sources: 1 << BlockType.Sources,
  Ext: 1 << 9,
  Unknown: 1 << 10,
  All: 0x7fe,
} as const;

function blockBit(type: number): number {
  return type >= BlockType.Entities && type <= BlockType.Sources
    ? 1 << type
    : type === BlockType.Ext
      ? BlockMask.Ext
      : BlockMask.Unknown;
}

/**
 * Decodes `TICK` messages against one catalog plan: the header, then every block, each isolated by its declared length
 * so it can neither read past its end nor leave bytes unread. Reusable and allocation-free per record: it owns its
 * reader and the position and velocity buffers it hands to the sink.
 *
 * **Not re-entrant.** A read shares field-codec's module-level value buffers with every other decode, so a sink must not
 * start another read — of any message — from its callbacks. Once a read ends, the reader lets go of the message.
 */
export class TickReader {
  readonly plan: CatalogPlan;
  private readonly r = new WireReader();
  private readonly metricValue = new Float64Array(4);
  /** Per archetype: a view of the position buffer exactly `dims` long, empty when not spatial. */
  private readonly positions: readonly Float64Array[];
  /** Per archetype: a view of the velocity buffer exactly `dims` long for the linear model, empty otherwise. */
  private readonly velocities: readonly Float64Array[];
  /** The start tick handed to {@link TickSink.enter} and {@link TickSink.segment}. */
  private readonly t0 = new Uint32Array(1);
  /** Per archetype: the {@link reads} count of the message whose `ENTITIES` block for it was last read. */
  private readonly entitiesSeenAt: Uint32Array;
  private reads = 0;
  private readonly generated: readonly (EntitiesDecoder | null)[] | null;
  private readonly target: EntitiesTarget | null;

  /**
   * With `generated` and its `target`, an archetype's `ENTITIES` block is decoded by its generated decoder straight into
   * the target, and the sink only sees {@link TickSink.beginEntities}; without, by the interpreter through the sink.
   */
  constructor(plan: CatalogPlan, generated?: { decoders: GeneratedDecoders; target: EntitiesTarget }) {
    this.plan = plan;
    this.generated = generated?.decoders.entities ?? null;
    this.target = generated?.target ?? null;
    // Views sized once per archetype, over two shared buffers: a sink sees exactly the values of this record, and a
    // record allocates nothing.
    const position = new Float64Array(3);
    const velocity = new Float64Array(3);
    const empty = new Float64Array(0);
    this.positions = plan.archetypes.map((a) => (a.position === null ? empty : position.subarray(0, a.position.dims)));
    this.velocities = plan.archetypes.map((a) =>
      a.position === null || a.position.vel === null ? empty : velocity.subarray(0, a.position.dims),
    );
    this.entitiesSeenAt = new Uint32Array(plan.archetypes.length);
  }

  /**
   * Decodes one `TICK` message, type byte included. `blocks` selects which block types reach the sink; the others are
   * skipped by their length, unvalidated. The header's {@link TickSink.beginTick} and {@link TickSink.endTick} always
   * do.
   */
  read(message: Uint8Array, sink: TickSink, blocks: number = BlockMask.All): void {
    try {
      this.readMessage(message, sink, blocks);
    } finally {
      this.r.release();
    }
  }

  private readMessage(message: Uint8Array, sink: TickSink, blocks: number): void {
    // A stamp per read rather than a cleared set: at most one ENTITIES block per archetype per message (1007).
    this.reads = (this.reads + 1) >>> 0;
    if (this.reads === 0) {
      this.entitiesSeenAt.fill(0);
      this.reads = 1;
    }

    const r = this.r.reset(message);
    if (r.u8() !== MessageType.Tick) {
      throw protocolError('not a TICK message');
    }

    const tick = r.u32();
    const flags = r.u8();
    const periodUs = (flags & TickFlags.Period) !== 0 ? r.u32() : 0;
    sink.beginTick(tick, flags, periodUs);

    while (!r.isAtEnd) {
      const type = r.u8();
      const length = r.varuAtMost(r.remaining, 'block length');
      if ((blocks & blockBit(type)) === 0) {
        r.skip(length);
        continue;
      }

      const saved = r.pushLimit(length);
      switch (type) {
        case BlockType.Entities:
          this.readEntities(tick, sink);
          break;
        case BlockType.Events:
          for (let n = r.varu(); n > 0; n--) {
            const eventType = this.plan.event(r.varu());
            sink.event(eventType);
            readSection(r, eventType.body, tick, sink);
          }

          break;
        case BlockType.Self:
          this.readSelf(tick, sink);
          break;
        case BlockType.Agg:
          this.readAggregate(sink);
          break;
        case BlockType.Stats:
          this.readMetrics(this.plan.serverMetrics, tick, sink);
          this.readMetrics(this.plan.sessionMetrics, tick, sink);
          break;
        case BlockType.Debug:
          while (r.remaining > 0) {
            const subType = r.u8();
            const size = r.varuAtMost(r.remaining, 'DEBUG sub-block length');
            sink.debug(subType, r.bytes, r.take(size), size);
          }

          break;
        case BlockType.Acks:
          for (let n = r.varu(); n > 0; n--) {
            const seq = r.u16();
            sink.ack(seq, r.u8());
          }

          break;
        case BlockType.Sources:
          for (let n = r.varu(); n > 0; n--) {
            const requestId = r.u16();
            const status = r.u8();
            if (status > SourceStatus.Error) {
              throw malformed(`SOURCES status ${status} is unknown`);
            }

            sink.source(requestId, status, status === SourceStatus.Error ? r.u16() : 0);
          }

          break;
        case BlockType.Ext: {
          const appTypeId = r.varu();
          const size = r.remaining;
          sink.ext(appTypeId, r.bytes, r.take(size), size);
          break;
        }
        default:
          r.skip(r.remaining);
          sink.unknownBlock(type);
          break;
      }

      const unread = r.popLimit(saved);
      if (unread !== 0) {
        // A block shorter than its declared length is malformed, not padded — STATS included (§ 10 precisions).
        throw malformed(`block 0x${type.toString(16)}: ${unread} unread byte(s) after its content`);
      }
    }

    sink.endTick();
  }

  private readEntities(tick: number, sink: TickSink): void {
    const r = this.r;
    const archetype = this.plan.archetype(r.varu());
    if (this.entitiesSeenAt[archetype.idx] === this.reads) {
      throw malformed(`a second ENTITIES block for archetype '${archetype.name}'`);
    }

    this.entitiesSeenAt[archetype.idx] = this.reads;
    const position = archetype.position;
    const p = this.positions[archetype.idx]!;
    const v = this.velocities[archetype.idx]!;
    const sections = archetype.groupSections;
    sink.beginEntities(archetype);
    const generated = this.generated?.[archetype.idx];
    if (generated != null) {
      generated(r, tick, this.target!);
      return;
    }

    const t0 = this.t0;

    // ── enters ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    for (let runs = r.varu(); runs > 0; runs--) {
      let prev = -1;
      for (let n = runLength(r); n > 0; n--) {
        prev = nextNetId(r, prev);
        t0[0] = 0;
        let epoch = 0;
        if (position !== null) {
          readNumber(r, position.pos, tick, p, 0);
          if (position.moving) {
            if (position.vel !== null) {
              readNumber(r, position.vel, tick, v, 0);
            }

            // decodeTickLo, written out: its result would be boxed crossing the call once the tick passes 2^30.
            const low = r.u16();
            t0[0] = tick - ((tick - low) & 0xffff);
            epoch = r.u8();
          }
        }

        sink.enter(prev, p, v, t0, epoch);
        readSection(r, archetype.onEnter, tick, sink);
        for (let s = 0; s < sections.length; s++) {
          readSection(r, sections[s]!, tick, sink);
        }
      }
    }

    // ── segments ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    const segmentRuns = r.varu();
    if (segmentRuns > 0 && (position === null || !position.moving)) {
      throw segmentsOfAStill(archetype.name);
    }

    for (let runs = segmentRuns; runs > 0; runs--) {
      let prev = -1;
      for (let n = runLength(r); n > 0; n--) {
        prev = nextNetId(r, prev);
        readNumber(r, position!.pos, tick, p, 0);
        if (position!.vel !== null) {
          readNumber(r, position!.vel, tick, v, 0);
        }

        const low = r.u16();
        t0[0] = tick - ((tick - low) & 0xffff);
        sink.segment(prev, p, v, t0, r.u8());
      }
    }

    // ── states ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    const groupCount = archetype.groups.length;
    for (let runs = r.varu(); runs > 0; runs--) {
      let prev = -1;
      for (let n = runLength(r); n > 0; n--) {
        prev = nextNetId(r, prev);
        const mask = r.u8();
        if (mask === 0 || mask >> groupCount !== 0) {
          throw invalidStateMask(mask, groupCount);
        }

        sink.state(prev, mask);
        for (let g = 0; g < groupCount; g++) {
          if ((mask & (1 << g)) !== 0) {
            readSection(r, sections[g]!, tick, sink);
          }
        }
      }
    }

    // ── leaves ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    for (let runs = r.varu(); runs > 0; runs--) {
      let prev = -1;
      for (let n = runLength(r); n > 0; n--) {
        prev = nextNetId(r, prev);
        sink.leave(prev);
      }
    }
  }

  private readSelf(tick: number, sink: TickSink): void {
    const r = this.r;
    const archetypeIdx = r.varu();
    const netId = r.varu();
    const lastSeq = r.u16();
    const mask = r.u8();

    // netId 0 is never an entity: it is "no controlled entity" (W17′), an acknowledgement only, so it names no
    // archetype and carries no group.
    if (netId === 0) {
      if (archetypeIdx !== 0 || mask !== 0) {
        throw malformed(
          `SELF with no controlled entity must name archetype 0 and no owner group ` +
            `(archetype ${archetypeIdx}, mask 0x${mask.toString(16)})`,
        );
      }

      sink.self(null, 0, lastSeq, 0);
      return;
    }

    const archetype = this.plan.archetype(archetypeIdx);
    const groupCount = archetype.ownerGroups.length;
    if (mask >> groupCount !== 0) {
      throw malformed(`SELF owner mask 0x${mask.toString(16)} is invalid for ${groupCount} owner group(s)`);
    }

    sink.self(archetype, netId, lastSeq, mask);
    for (let g = 0; g < groupCount; g++) {
      if ((mask & (1 << g)) !== 0) {
        readSection(r, archetype.ownerSections[g]!, tick, sink);
      }
    }
  }

  private readAggregate(sink: TickSink): void {
    const r = this.r;
    const grid = this.plan.grid(r.varu());
    const flags = r.u8();
    sink.beginAggregate(grid, (flags & 1) !== 0);
    const counts = grid.counts;
    let prev = -1;
    for (let n = r.varu(); n > 0; n--) {
      const cell = prev + 1 + r.varu();
      if (cell >= grid.cellCount) {
        throw malformed(`AGG cell ${cell} is outside the grid's ${grid.cellCount} cells`);
      }

      prev = cell;
      for (let a = 0; a < counts.length; a++) {
        counts[a] = r.varu();
      }

      sink.aggregateCell(cell, counts);
    }
  }

  private readMetrics(metrics: readonly MetricPlan[], tick: number, sink: TickSink): void {
    const r = this.r;
    const value = this.metricValue;
    for (let k = 0; k < metrics.length; k++) {
      const m = metrics[k]!;
      for (let i = 0; i < m.valueCount; i++) {
        if (m.value.valueKind === ValueKind.Skipped) {
          r.skip(m.value.fixedBytes);
          continue;
        }

        readNumber(r, m.value, tick, value, 0);
        sink.metric(m, i, value[0]!);
      }
    }
  }
}

/**
 * One sub-list run's record count, which the grammar forbids to be zero. Shared with generated decoders, so both paths
 * refuse the same bytes with the same error.
 *
 * A canonical encoding has no empty run: a sub-list with nothing to say spends one `varu` zero on its RUN count and stops. Admitting an empty run
 * would give two byte strings for one frame, and would let a hostile stream spend a megabyte of run counts on no records at all.
 */
export function runLength(r: WireReader): number {
  const n = r.varu();
  if (n === 0) {
    throw malformed('an ENTITIES sub-list run carries no record');
  }

  return n;
}

/** The error for segments in the block of an archetype that does not move. */
export function segmentsOfAStill(archetype: string): Error {
  return malformed(`archetype '${archetype}' does not move but its block carries segment(s)`);
}

/** The error for a state record mask that is zero or names a group the archetype does not have. */
export function invalidStateMask(mask: number, groupCount: number): Error {
  return malformed(`state record mask 0x${mask.toString(16)} is invalid for ${groupCount} group(s)`);
}

/** netIds in a list are ascending: each is the previous plus one plus a `varu` gap, starting from −1. */
export function nextNetId(r: WireReader, prev: number): number {
  const next = prev + 1 + r.varu();
  if (next > 0xffffffff) {
    throw malformed('netId gap overflows 32 bits');
  }

  return next;
}
